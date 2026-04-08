using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

Console.OutputEncoding = Encoding.UTF8;

var startup = StartupOptions.Parse(args);
if (startup.ShowHelp)
{
    Console.WriteLine("""
意心Code (.NET 10 后端版)

用法:
  dotnet run --project src/YxAi.DotNetHost -- [选项]

选项:
  -h, --help     显示帮助信息
  -v, --version  显示版本号
  -p, --port     指定端口号 (默认: 6060)

环境变量:
  PORT           自定义端口号
""");
    return;
}

var appPaths = AppPaths.Discover();
if (startup.ShowVersion)
{
    Console.WriteLine(new PackageVersionService(appPaths).GetVersion());
    return;
}

var configBuilder = WebApplication.CreateBuilder(args);
configBuilder.Configuration
    .SetBasePath(appPaths.ProjectDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

configBuilder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.WriteIndented = false;
});

configBuilder.Services.AddSingleton(appPaths);
configBuilder.Services.AddSingleton<PackageVersionService>();
configBuilder.Services.AddSingleton<ClaudeSettingsService>();
configBuilder.Services.AddSingleton<ClaudeJsonService>();
configBuilder.Services.AddSingleton<ProjectSessionService>();
configBuilder.Services.AddSingleton<FileBrowserService>();
configBuilder.Services.AddSingleton<GitService>();
configBuilder.Services.AddSingleton<PendingApprovalStore>();
configBuilder.Services.AddSingleton<PlanModeStateStore>();
configBuilder.Services.AddSingleton<ClaudeSessionRegistry>();
configBuilder.Services.AddSingleton<ClaudeCliService>();
configBuilder.Services.AddSingleton<ChatWebSocketHandler>();
configBuilder.Services.AddHttpClient<ModelCatalogService>();

var serverDefaults = configBuilder.Configuration.GetSection("Server").Get<ServerSettings>() ?? new ServerSettings();
var desiredPort = startup.Port ?? TryParsePort(Environment.GetEnvironmentVariable("PORT")) ?? serverDefaults.DefaultPort;
var port = PortHelper.FindAvailablePort(desiredPort, serverDefaults.MaxPortAttempts);
configBuilder.WebHost.UseUrls($"http://localhost:{port}");

var app = configBuilder.Build();

var publicProvider = new PhysicalFileProvider(appPaths.PublicDirectory);
var promptProvider = new PhysicalFileProvider(appPaths.PromptDirectory);

if (Directory.Exists(appPaths.PublicDirectory))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = publicProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = publicProvider
    });
}

if (Directory.Exists(appPaths.PromptDirectory))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = promptProvider,
        RequestPath = "/prompt",
        ServeUnknownFileTypes = true
    });
}

app.UseWebSockets();

app.MapGet("/", () =>
{
    var indexPath = Path.Combine(appPaths.PublicDirectory, "index.html");
    return File.Exists(indexPath)
        ? Results.File(indexPath, "text/html; charset=utf-8")
        : Results.NotFound();
});

app.MapGet("/api/version", (PackageVersionService versionService) =>
{
    return Results.Json(new { version = versionService.GetVersion() });
});

app.MapGet("/api/models", async (HttpContext context, ModelCatalogService service, CancellationToken cancellationToken) =>
{
    var baseUrl = context.Request.Query["baseUrl"].ToString();
    try
    {
        var models = await service.GetModelsAsync(baseUrl, cancellationToken);
        return Results.Json(models);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/settings", async (SettingsRequest request, ClaudeSettingsService settingsService, CancellationToken cancellationToken) =>
{
    try
    {
        await settingsService.SyncAsync(request.ApiKey, request.BaseUrl, request.Model, cancellationToken);
        return Results.Json(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { success = false, message = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/test-connection", async (TestConnectionRequest request, ClaudeCliService claudeCliService, CancellationToken cancellationToken) =>
{
    var result = await claudeCliService.TestConnectionAsync(request, cancellationToken);
    return Results.Json(new { success = result.Success, message = result.Message });
});

app.MapGet("/api/projects", async (ProjectSessionService sessionService, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await sessionService.GetProjectsAsync(cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/projects/{name}/sessions", async (string name, ProjectSessionService sessionService, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await sessionService.GetSessionsAsync(name, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/projects/{name}/sessions/{id}/messages", async (string name, string id, ProjectSessionService sessionService, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Json(await sessionService.GetMessagesAsync(name, id, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapDelete("/api/projects/{name}/sessions/{id}", async (string name, string id, ProjectSessionService sessionService, CancellationToken cancellationToken) =>
{
    try
    {
        await sessionService.DeleteSessionAsync(name, id, cancellationToken);
        return Results.Json(new { success = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/open-folder", async (HttpContext context, CancellationToken cancellationToken) =>
{
    var path = context.Request.Query["path"].ToString();
    if (string.IsNullOrWhiteSpace(path))
    {
        return Results.Json(new { error = "path required" }, statusCode: 400);
    }

    try
    {
        await ShellHelper.OpenFolderAsync(path, cancellationToken);
        return Results.Json(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/browse", async (HttpContext context, FileBrowserService fileBrowserService, CancellationToken cancellationToken) =>
{
    try
    {
        var targetPath = context.Request.Query["path"].ToString();
        return Results.Json(await fileBrowserService.BrowseAsync(targetPath, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/files", async (HttpContext context, FileBrowserService fileBrowserService, CancellationToken cancellationToken) =>
{
    try
    {
        var cwd = context.Request.Query["cwd"].ToString();
        return Results.Json(await fileBrowserService.GetTreeAsync(cwd, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/files-flat", async (HttpContext context, FileBrowserService fileBrowserService, CancellationToken cancellationToken) =>
{
    try
    {
        var cwd = context.Request.Query["cwd"].ToString();
        return Results.Json(await fileBrowserService.GetFlatAsync(cwd, cancellationToken));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/file", async (HttpContext context, FileBrowserService fileBrowserService, CancellationToken cancellationToken) =>
{
    try
    {
        var filePath = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return Results.Json(new { error = "path required" }, statusCode: 400);
        }

        var file = await fileBrowserService.ReadFileAsync(filePath, cancellationToken);
        return Results.Json(file);
    }
    catch (FileTooLargeException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 413);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/git/status", async (HttpContext context, GitService gitService, CancellationToken cancellationToken) =>
{
    try
    {
        var cwd = context.Request.Query["cwd"].ToString();
        var output = await gitService.GetStatusAsync(cwd, cancellationToken);
        return Results.Json(new { output });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var handler = context.RequestServices.GetRequiredService<ChatWebSocketHandler>();
    await handler.HandleAsync(socket, context.RequestAborted);
});

var serverUrl = $"http://localhost:{port}";
app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"\n  意心Code (.NET 10 后端版) 已启动");
    Console.WriteLine($"  {serverUrl}\n");

    if (serverDefaults.AutoOpenBrowser)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            try
            {
                BrowserLauncher.Open(serverUrl);
            }
            catch
            {
                // ignore
            }
        });
    }
});

await app.RunAsync();
return;

static int? TryParsePort(string? value)
{
    return int.TryParse(value, out var port) ? port : null;
}

sealed class StartupOptions
{
    public bool ShowHelp { get; private init; }
    public bool ShowVersion { get; private init; }
    public int? Port { get; private init; }

    public static StartupOptions Parse(string[] args)
    {
        var showHelp = args.Contains("--help") || args.Contains("-h");
        var showVersion = args.Contains("--version") || args.Contains("-v");
        int? port = null;
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsed))
            {
                port = parsed;
                break;
            }
        }

        return new StartupOptions
        {
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            Port = port
        };
    }
}

sealed class ServerSettings
{
    public int DefaultPort { get; set; } = 6060;
    public int MaxPortAttempts { get; set; } = 100;
    public bool AutoOpenBrowser { get; set; } = true;
}

sealed class AppPaths
{
    public required string RepoRoot { get; init; }
    public required string ProjectDirectory { get; init; }
    public required string PublicDirectory { get; init; }
    public required string PromptDirectory { get; init; }
    public required string PackageJsonPath { get; init; }
    public required string HomeDirectory { get; init; }
    public required string ClaudeDirectory { get; init; }
    public required string ClaudeProjectsDirectory { get; init; }
    public required string ClaudeSettingsPath { get; init; }
    public required string ClaudeJsonPath { get; init; }
    public required string TempDirectory { get; init; }

    public static AppPaths Discover()
    {
        var current = Directory.GetCurrentDirectory();
        var baseDir = AppContext.BaseDirectory;
        var repoRoot = FindRepoRoot(current) ?? FindRepoRoot(baseDir) ?? Path.GetFullPath(Path.Combine(current, "..", ".."));
        var projectDirectory = Directory.Exists(Path.Combine(repoRoot, "dotnet", "src", "YxAi.DotNetHost"))
            ? Path.Combine(repoRoot, "dotnet", "src", "YxAi.DotNetHost")
            : current;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeDir = Path.Combine(home, ".claude");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "yxaicode-dotnet");
        Directory.CreateDirectory(tempDirectory);

        return new AppPaths
        {
            RepoRoot = repoRoot,
            ProjectDirectory = projectDirectory,
            PublicDirectory = Path.Combine(repoRoot, "public"),
            PromptDirectory = Path.Combine(repoRoot, "prompt"),
            PackageJsonPath = Path.Combine(repoRoot, "package.json"),
            HomeDirectory = home,
            ClaudeDirectory = claudeDir,
            ClaudeProjectsDirectory = Path.Combine(claudeDir, "projects"),
            ClaudeSettingsPath = Path.Combine(claudeDir, "settings.json"),
            ClaudeJsonPath = Path.Combine(home, ".claude.json"),
            TempDirectory = tempDirectory
        };
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            var packageJson = Path.Combine(dir.FullName, "package.json");
            var serverJs = Path.Combine(dir.FullName, "server.js");
            var publicApp = Path.Combine(dir.FullName, "public", "app.js");
            if (File.Exists(packageJson) && File.Exists(serverJs) && File.Exists(publicApp))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}

sealed class PackageVersionService
{
    private readonly AppPaths _paths;
    private string? _cachedVersion;

    public PackageVersionService(AppPaths paths)
    {
        _paths = paths;
    }

    public string GetVersion()
    {
        if (!string.IsNullOrWhiteSpace(_cachedVersion))
        {
            return _cachedVersion;
        }

        try
        {
            if (File.Exists(_paths.PackageJsonPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_paths.PackageJsonPath));
                if (document.RootElement.TryGetProperty("version", out var versionElement))
                {
                    _cachedVersion = versionElement.GetString() ?? "dotnet";
                    return _cachedVersion;
                }
            }
        }
        catch
        {
            // ignore
        }

        _cachedVersion = typeof(PackageVersionService).Assembly.GetName().Version?.ToString() ?? "dotnet";
        return _cachedVersion;
    }
}

sealed class ModelCatalogService
{
    private readonly HttpClient _httpClient;

    public ModelCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<IReadOnlyList<ModelItem>> GetModelsAsync(string? baseUrl, CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://yxai.chat" : baseUrl.Trim().TrimEnd('/');
        var url = $"{normalizedBaseUrl}/prod-api/model?ModelApiTypes=1&SkipCount=1&MaxResultCount=100";
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: cancellationToken);
        var items = json?["items"] as JsonArray ?? new JsonArray();
        var results = new List<ModelItem>();
        foreach (var itemNode in items)
        {
            if (itemNode is not JsonObject item)
            {
                continue;
            }

            results.Add(new ModelItem
            {
                Value = item["modelId"]?.GetValue<string>() ?? string.Empty,
                Label = item["name"]?.GetValue<string>() ?? string.Empty,
                Description = item["description"]?.GetValue<string>() ?? string.Empty,
                Icon = item["iconUrl"]?.GetValue<string>() ?? string.Empty,
                Provider = item["providerName"]?.GetValue<string>() ?? string.Empty
            });
        }

        return results;
    }
}

sealed class ClaudeSettingsService
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ClaudeSettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task SyncAsync(string? apiKey, string? baseUrl, string? model, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            JsonObject root;
            if (File.Exists(_paths.ClaudeSettingsPath))
            {
                try
                {
                    root = JsonNode.Parse(await File.ReadAllTextAsync(_paths.ClaudeSettingsPath, cancellationToken)) as JsonObject ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            var env = root["env"] as JsonObject ?? new JsonObject();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                env["ANTHROPIC_AUTH_TOKEN"] = apiKey;
                env["ANTHROPIC_API_KEY"] = apiKey;
            }

            env["ANTHROPIC_BASE_URL"] = string.IsNullOrWhiteSpace(baseUrl) ? "https://yxai.chat" : baseUrl;

            if (!string.IsNullOrWhiteSpace(model))
            {
                env["ANTHROPIC_MODEL"] = model;
                env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model;
                env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
                env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
            }

            root["env"] = env;
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.ClaudeSettingsPath)!);
            await File.WriteAllTextAsync(_paths.ClaudeSettingsPath, root.ToJsonString(JsonDefaults.Pretty), cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}

sealed class ClaudeJsonService
{
    private readonly AppPaths _paths;

    public ClaudeJsonService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<JsonObject?> LoadMergedMcpConfigAsync(string? cwd, CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ClaudeJsonPath))
        {
            return null;
        }

        JsonObject root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(_paths.ClaudeJsonPath, cancellationToken)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return null;
        }

        var servers = new JsonObject();
        if (root["mcpServers"] is JsonObject globalServers)
        {
            foreach (var item in globalServers)
            {
                servers[item.Key] = item.Value?.DeepClone();
            }
        }

        if (!string.IsNullOrWhiteSpace(cwd) && root["claudeProjects"] is JsonObject projectMap)
        {
            var normalized = NormalizePathKey(cwd);
            var matchedProject = projectMap.FirstOrDefault(pair => NormalizePathKey(pair.Key) == normalized).Value as JsonObject;
            if (matchedProject?["mcpServers"] is JsonObject projectServers)
            {
                foreach (var item in projectServers)
                {
                    servers[item.Key] = item.Value?.DeepClone();
                }
            }
        }

        return servers.Count == 0 ? null : new JsonObject { ["mcpServers"] = servers };
    }

    private static string NormalizePathKey(string value)
    {
        var normalized = value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch
        {
            // ignore
        }

        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }
}

sealed class ProjectSessionService
{
    private static readonly Regex SystemTextRegex = new("^(<system-reminder>|<command-name>|<local-command-|Caveat:)", RegexOptions.Compiled);
    private readonly AppPaths _paths;

    public ProjectSessionService(AppPaths paths)
    {
        _paths = paths;
    }

    public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken)
    {
        var projects = new List<ProjectInfo>();
        if (!Directory.Exists(_paths.ClaudeProjectsDirectory))
        {
            return Task.FromResult<IReadOnlyList<ProjectInfo>>(projects);
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.ClaudeProjectsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectName = Path.GetFileName(directory);
            var sessions = GetSessionsInternal(directory);
            if (sessions.Count > 0)
            {
                projects.Add(new ProjectInfo
                {
                    Name = projectName,
                    Sessions = sessions
                });
            }
        }

        var sorted = projects
            .OrderByDescending(project => project.Sessions.FirstOrDefault()?.Mtime ?? DateTimeOffset.MinValue)
            .ToList();
        return Task.FromResult<IReadOnlyList<ProjectInfo>>(sorted);
    }

    public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(string projectName, CancellationToken cancellationToken)
    {
        var projectDir = Path.Combine(_paths.ClaudeProjectsDirectory, projectName);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SessionInfo>>(GetSessionsInternal(projectDir));
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetMessagesAsync(string projectName, string sessionId, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_paths.ClaudeProjectsDirectory, projectName, sessionId + ".jsonl");
        var lines = await ReadJsonLinesAsync(filePath, cancellationToken);
        var toolResults = new Dictionary<string, ToolResultPayload>(StringComparer.Ordinal);
        foreach (var obj in lines)
        {
            var type = obj["type"]?.GetValue<string>() ?? string.Empty;
            if ((type == "human" || type == "user") && obj["message"]?["content"] is JsonArray contentArray)
            {
                foreach (var blockNode in contentArray)
                {
                    if (blockNode is not JsonObject block)
                    {
                        continue;
                    }

                    if ((block["type"]?.GetValue<string>() ?? string.Empty) == "tool_result")
                    {
                        var toolUseId = block["tool_use_id"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(toolUseId))
                        {
                            continue;
                        }

                        toolResults[toolUseId] = new ToolResultPayload
                        {
                            ToolUseId = toolUseId,
                            Content = ExtractToolResultText(block["content"]),
                            IsError = block["is_error"]?.GetValue<bool>() ?? false
                        };
                    }
                }
            }
        }

        var messages = new List<ChatHistoryMessage>();
        foreach (var obj in lines)
        {
            var type = obj["type"]?.GetValue<string>() ?? string.Empty;
            if (type == "human" || type == "user")
            {
                var text = ExtractMessageText(obj["message"]?["content"], joinWithNewLine: true, filterSystemText: true).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    messages.Add(new ChatHistoryMessage
                    {
                        Role = "user",
                        Content = text
                    });
                }
            }
            else if (type == "assistant")
            {
                var parts = new List<ChatHistoryPart>();
                var contentNode = obj["message"]?["content"];
                if (contentNode is JsonValue)
                {
                    var text = contentNode.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(new ChatHistoryPart
                        {
                            Type = "text",
                            Text = text
                        });
                    }
                }
                else if (contentNode is JsonArray contentArray)
                {
                    foreach (var blockNode in contentArray)
                    {
                        if (blockNode is not JsonObject block)
                        {
                            continue;
                        }

                        var blockType = block["type"]?.GetValue<string>() ?? string.Empty;
                        if (blockType == "text")
                        {
                            var text = block["text"]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                parts.Add(new ChatHistoryPart
                                {
                                    Type = "text",
                                    Text = text
                                });
                            }
                        }
                        else if (blockType == "tool_use")
                        {
                            var toolId = block["id"]?.GetValue<string>() ?? string.Empty;
                            parts.Add(new ChatHistoryPart
                            {
                                Type = "tool_use",
                                Id = toolId,
                                Name = block["name"]?.GetValue<string>(),
                                Input = block["input"]?.DeepClone()
                            });

                            if (!string.IsNullOrWhiteSpace(toolId) && toolResults.TryGetValue(toolId, out var toolResult))
                            {
                                parts.Add(new ChatHistoryPart
                                {
                                    Type = "tool_result",
                                    ToolUseId = toolResult.ToolUseId,
                                    Content = toolResult.Content,
                                    IsError = toolResult.IsError
                                });
                            }
                        }
                    }
                }

                if (parts.Count > 0)
                {
                    messages.Add(new ChatHistoryMessage
                    {
                        Role = "assistant",
                        Content = string.Join("\n", parts.Where(part => part.Type == "text").Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text))),
                        Parts = parts
                    });
                }
            }
        }

        return messages;
    }

    public async Task DeleteSessionAsync(string projectName, string sessionId, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_paths.ClaudeProjectsDirectory, projectName, sessionId + ".jsonl");
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("会话文件不存在", filePath);
        }

        if (OperatingSystem.IsWindows())
        {
            var escaped = filePath.Replace("'", "''");
            var command = $"Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('{escaped}', 'OnlyErrorDialogs', 'SendToRecycleBin')";
            await ProcessRunner.RunForExitAsync("powershell.exe", new[] { "-NoProfile", "-Command", command }, null, null, cancellationToken);
            return;
        }

        File.Delete(filePath);
    }

    private List<SessionInfo> GetSessionsInternal(string projectDir)
    {
        var sessions = new List<SessionInfo>();
        if (!Directory.Exists(projectDir))
        {
            return sessions;
        }

        foreach (var filePath in Directory.EnumerateFiles(projectDir, "*.jsonl"))
        {
            var fileInfo = new FileInfo(filePath);
            var raw = File.ReadAllText(filePath);
            var parsed = ParseSessionInfo(raw);
            sessions.Add(new SessionInfo
            {
                Id = Path.GetFileNameWithoutExtension(filePath),
                File = Path.GetFileName(filePath),
                Summary = parsed.Summary,
                MsgCount = parsed.MsgCount,
                Mtime = fileInfo.LastWriteTimeUtc
            });
        }

        return sessions.OrderByDescending(session => session.Mtime).ToList();
    }

    private static SessionSummary ParseSessionInfo(string raw)
    {
        var summary = string.Empty;
        var msgCount = 0;
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(line) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (obj is null)
            {
                continue;
            }

            var type = obj["type"]?.GetValue<string>() ?? string.Empty;
            if (type == "human" || type == "user" || type == "assistant")
            {
                msgCount++;
            }

            if (string.IsNullOrWhiteSpace(summary) && type == "summary" && !string.IsNullOrWhiteSpace(obj["summary"]?.GetValue<string>()))
            {
                summary = obj["summary"]!.GetValue<string>()[..Math.Min(50, obj["summary"]!.GetValue<string>().Length)];
                continue;
            }

            if (!string.IsNullOrWhiteSpace(summary) || (type != "human" && type != "user"))
            {
                continue;
            }

            var text = ExtractMessageText(obj["message"]?["content"], joinWithNewLine: false, filterSystemText: true).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var userMessageMatch = Regex.Match(text, "<user_message>\\s*([\\s\\S]*?)\\s*</user_message>", RegexOptions.IgnoreCase);
            if (userMessageMatch.Success)
            {
                var userText = userMessageMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    summary = userText[..Math.Min(50, userText.Length)];
                    continue;
                }
            }

            if (text.StartsWith("# 角色设定", StringComparison.Ordinal) || text.StartsWith("## ", StringComparison.Ordinal))
            {
                continue;
            }

            summary = text[..Math.Min(50, text.Length)];
        }

        return new SessionSummary(summary, msgCount);
    }

    private static async Task<List<JsonObject>> ReadJsonLinesAsync(string filePath, CancellationToken cancellationToken)
    {
        var results = new List<JsonObject>();
        if (!File.Exists(filePath))
        {
            return results;
        }

        using var stream = File.OpenRead(filePath);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                if (JsonNode.Parse(line) is JsonObject obj)
                {
                    results.Add(obj);
                }
            }
            catch
            {
                // ignore malformed line
            }
        }

        return results;
    }

    private static string ExtractMessageText(JsonNode? contentNode, bool joinWithNewLine, bool filterSystemText)
    {
        if (contentNode is null)
        {
            return string.Empty;
        }

        if (contentNode is JsonValue)
        {
            return contentNode.GetValue<string>();
        }

        if (contentNode is not JsonArray array)
        {
            return string.Empty;
        }

        var pieces = new List<string>();
        foreach (var node in array)
        {
            if (node is not JsonObject block)
            {
                continue;
            }

            var type = block["type"]?.GetValue<string>() ?? string.Empty;
            var text = block["text"]?.GetValue<string>() ?? string.Empty;
            if (type != "text" || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (filterSystemText && SystemTextRegex.IsMatch(text.Trim()))
            {
                continue;
            }

            pieces.Add(text);
        }

        return joinWithNewLine ? string.Join("\n", pieces) : string.Join(" ", pieces);
    }

    private static string ExtractToolResultText(JsonNode? contentNode)
    {
        if (contentNode is null)
        {
            return string.Empty;
        }

        if (contentNode is JsonValue)
        {
            return contentNode.GetValue<string>();
        }

        if (contentNode is JsonArray array)
        {
            return string.Join(string.Empty, array
                .OfType<JsonObject>()
                .Where(block => (block["type"]?.GetValue<string>() ?? string.Empty) == "text")
                .Select(block => block["text"]?.GetValue<string>() ?? string.Empty));
        }

        return contentNode.ToJsonString();
    }
}

sealed class FileBrowserService
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", "__pycache__", ".next", ".nuxt", "dist", "build", ".cache", ".claude"
    };

    public Task<object> BrowseAsync(string? targetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(targetPath) && OperatingSystem.IsWindows())
        {
            var drives = DriveInfo.GetDrives()
                .Where(drive => drive.DriveType != DriveType.CDRom || drive.IsReady)
                .Select(drive => new BrowseDirectoryItem
                {
                    Name = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    Path = drive.RootDirectory.FullName
                })
                .ToList();
            return Task.FromResult<object>(new BrowseResponse
            {
                Path = string.Empty,
                Parent = string.Empty,
                Dirs = drives
            });
        }

        var resolved = string.IsNullOrWhiteSpace(targetPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(targetPath);
        var directory = new DirectoryInfo(resolved);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException($"目录不存在: {resolved}");
        }

        var dirs = directory.EnumerateDirectories()
            .Where(dir => !dir.Name.StartsWith(".", StringComparison.Ordinal))
            .OrderBy(dir => dir.Name, StringComparer.OrdinalIgnoreCase)
            .Select(dir => new BrowseDirectoryItem
            {
                Name = dir.Name,
                Path = dir.FullName
            })
            .ToList();

        return Task.FromResult<object>(new BrowseResponse
        {
            Path = directory.FullName,
            Parent = directory.Parent?.FullName ?? string.Empty,
            Dirs = dirs
        });
    }

    public Task<IReadOnlyList<FileTreeItem>> GetTreeAsync(string? cwd, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        return Task.FromResult<IReadOnlyList<FileTreeItem>>(ScanTree(root, 0, cancellationToken));
    }

    public Task<IReadOnlyList<FileFlatItem>> GetFlatAsync(string? cwd, CancellationToken cancellationToken)
    {
        var root = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        var results = new List<FileFlatItem>();
        ScanFlat(root, root, 0, results, cancellationToken);
        return Task.FromResult<IReadOnlyList<FileFlatItem>>(results);
    }

    public async Task<FileReadResponse> ReadFileAsync(string filePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("文件不存在", fullPath);
        }

        if (fileInfo.Length > 500 * 1024)
        {
            throw new FileTooLargeException("File too large (>500KB)");
        }

        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        return new FileReadResponse
        {
            Path = fullPath,
            Size = fileInfo.Length,
            Content = content
        };
    }

    private static List<FileTreeItem> ScanTree(string directory, int depth, CancellationToken cancellationToken)
    {
        if (depth > 5 || !Directory.Exists(directory))
        {
            return new List<FileTreeItem>();
        }

        var items = new List<FileTreeItem>();
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name.StartsWith(".", StringComparison.Ordinal) && !string.Equals(entry.Name, ".env", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (SkipDirs.Contains(entry.Name))
                {
                    continue;
                }

                items.Add(new FileTreeItem
                {
                    Name = entry.Name,
                    Type = "dir",
                    Path = entry.FullName,
                    Children = ScanTree(entry.FullName, depth + 1, cancellationToken)
                });
            }
            else
            {
                var fileInfo = new FileInfo(entry.FullName);
                items.Add(new FileTreeItem
                {
                    Name = entry.Name,
                    Type = "file",
                    Path = entry.FullName,
                    Size = fileInfo.Length
                });
            }
        }

        return items
            .OrderBy(item => item.Type == "dir" ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ScanFlat(string root, string directory, int depth, List<FileFlatItem> results, CancellationToken cancellationToken)
    {
        if (depth > 10 || results.Count >= 5000 || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= 5000)
            {
                return;
            }

            if (entry.Name.StartsWith(".", StringComparison.Ordinal) && !string.Equals(entry.Name, ".env", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                if (SkipDirs.Contains(entry.Name))
                {
                    continue;
                }

                results.Add(new FileFlatItem
                {
                    Path = relative + "/",
                    Type = "dir"
                });
                ScanFlat(root, entry.FullName, depth + 1, results, cancellationToken);
            }
            else
            {
                results.Add(new FileFlatItem
                {
                    Path = relative,
                    Type = "file"
                });
            }
        }
    }
}

sealed class GitService
{
    public async Task<string> GetStatusAsync(string? cwd, CancellationToken cancellationToken)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : Path.GetFullPath(cwd);
        var status = await ProcessRunner.RunAndCaptureAsync("git", new[] { "status", "--porcelain" }, workingDirectory, null, cancellationToken);
        var staged = await ProcessRunner.RunAndCaptureAsync("git", new[] { "diff", "--cached" }, workingDirectory, null, cancellationToken);
        var unstaged = await ProcessRunner.RunAndCaptureAsync("git", new[] { "diff" }, workingDirectory, null, cancellationToken);
        return string.Concat(status.StdOut, staged.StdOut, unstaged.StdOut);
    }
}

sealed class PendingApprovalStore
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pending = new(StringComparer.Ordinal);

    public Task<ApprovalDecision> CreatePendingAsync(string requestId, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        cancellationToken.Register(() => Resolve(requestId, new ApprovalDecision { Cancelled = true }));
        return tcs.Task;
    }

    public void Resolve(string requestId, ApprovalDecision decision)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(decision);
        }
    }
}

sealed class PlanModeStateStore
{
    private readonly ConcurrentDictionary<string, bool> _states = new(StringComparer.Ordinal);

    public void Set(string sessionId, bool enabled)
    {
        _states[sessionId] = enabled;
    }

    public bool Get(string sessionId)
    {
        return _states.TryGetValue(sessionId, out var enabled) && enabled;
    }
}

sealed class ClaudeSessionRegistry
{
    private readonly ConcurrentDictionary<string, RunningClaudeSession> _sessions = new(StringComparer.Ordinal);

    public bool TryAdd(string sessionId, RunningClaudeSession session)
    {
        return _sessions.TryAdd(sessionId, session);
    }

    public bool TryGet(string sessionId, out RunningClaudeSession session)
    {
        return _sessions.TryGetValue(sessionId, out session!);
    }

    public bool TryRemove(string sessionId, out RunningClaudeSession? session)
    {
        return _sessions.TryRemove(sessionId, out session);
    }
}

sealed class ChatWebSocketHandler
{
    private readonly ClaudeCliService _claudeCliService;
    private readonly ClaudeSettingsService _settingsService;
    private readonly PendingApprovalStore _approvals;
    private readonly PlanModeStateStore _planModeStateStore;
    private readonly ClaudeSessionRegistry _sessionRegistry;

    public ChatWebSocketHandler(
        ClaudeCliService claudeCliService,
        ClaudeSettingsService settingsService,
        PendingApprovalStore approvals,
        PlanModeStateStore planModeStateStore,
        ClaudeSessionRegistry sessionRegistry)
    {
        _claudeCliService = claudeCliService;
        _settingsService = settingsService;
        _approvals = approvals;
        _planModeStateStore = planModeStateStore;
        _sessionRegistry = sessionRegistry;
    }

    public async Task HandleAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var connection = new JsonWebSocketConnection(socket);
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken);
            if (text is null)
            {
                break;
            }

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(text) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (message is null)
            {
                continue;
            }

            var type = message["type"]?.GetValue<string>() ?? string.Empty;
            switch (type)
            {
                case "claude-command":
                {
                    var command = message.Deserialize<ClaudeCommandMessage>(JsonDefaults.Web) ?? new ClaudeCommandMessage();
                    var sessionId = string.IsNullOrWhiteSpace(command.SessionId) ? Guid.NewGuid().ToString() : command.SessionId!;
                    var isNewSession = string.IsNullOrWhiteSpace(command.SessionId);
                    if (isNewSession)
                    {
                        await connection.SendAsync(new { type = "session-created", sessionId }, cancellationToken);
                    }

                    await _settingsService.SyncAsync(command.ApiKey, command.BaseUrl, command.Model, cancellationToken);
                    var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var runningSession = new RunningClaudeSession(runCts);
                    if (!_sessionRegistry.TryAdd(sessionId, runningSession))
                    {
                        await connection.SendAsync(new { type = "claude-error", error = "当前会话已有任务在执行", sessionId }, cancellationToken);
                        break;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _claudeCliService.RunAsync(command, sessionId, connection, runningSession, runCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            var reason = runningSession.ConsumePendingStopReason();
                            if (!string.IsNullOrWhiteSpace(reason))
                            {
                                await connection.SendAsync(new { type = "claude-error", error = reason, sessionId }, CancellationToken.None);
                            }
                        }
                        catch (Exception ex)
                        {
                            await connection.SendAsync(new { type = "claude-error", error = ex.Message, sessionId }, CancellationToken.None);
                        }
                        finally
                        {
                            _sessionRegistry.TryRemove(sessionId, out _);
                            runCts.Dispose();
                        }
                    }, CancellationToken.None);
                    break;
                }

                case "permission-response":
                {
                    var response = message.Deserialize<PermissionResponseMessage>(JsonDefaults.Web) ?? new PermissionResponseMessage();
                    if (!string.IsNullOrWhiteSpace(response.RequestId))
                    {
                        _approvals.Resolve(response.RequestId!, new ApprovalDecision
                        {
                            Allow = response.Allow,
                            Confirmed = response.Confirmed,
                            Cancelled = response.Cancelled,
                            UpdatedInput = response.UpdatedInput,
                            Message = response.Message
                        });
                    }
                    break;
                }

                case "abort-session":
                {
                    var sessionId = message["sessionId"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(sessionId) && _sessionRegistry.TryRemove(sessionId, out var running) && running is not null)
                    {
                        await running.AbortAsync();
                        await connection.SendAsync(new { type = "session-aborted", sessionId }, cancellationToken);
                    }
                    break;
                }

                case "plan-mode-toggle":
                {
                    var sessionId = message["sessionId"]?.GetValue<string>();
                    var enabled = message["enabled"]?.GetValue<bool>() ?? false;
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        _planModeStateStore.Set(sessionId!, enabled);
                        await connection.SendAsync(new { type = "plan-mode-updated", sessionId, enabled }, cancellationToken);
                    }
                    break;
                }
            }
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

sealed class ClaudeCliService
{
    private readonly AppPaths _paths;
    private readonly ClaudeJsonService _claudeJsonService;
    private readonly PendingApprovalStore _approvals;
    private readonly PlanModeStateStore _planModeStateStore;

    public ClaudeCliService(AppPaths paths, ClaudeJsonService claudeJsonService, PendingApprovalStore approvals, PlanModeStateStore planModeStateStore)
    {
        _paths = paths;
        _claudeJsonService = claudeJsonService;
        _approvals = approvals;
        _planModeStateStore = planModeStateStore;
    }

    public async Task<TestConnectionResult> TestConnectionAsync(TestConnectionRequest request, CancellationToken cancellationToken)
    {
        var sessionId = Guid.NewGuid().ToString();
        var tempFiles = new List<string>();
        try
        {
            var prompt = "hi";
            var args = await BuildArgumentsAsync(
                prompt,
                sessionId,
                resume: false,
                request.Cwd,
                request.Model,
                permissionMode: "bypassPermissions",
                request.ApiKey,
                request.BaseUrl,
                Array.Empty<ImageAttachment>(),
                noSessionPersistence: true,
                tempFiles,
                cancellationToken);

            var env = BuildEnvironment(request.ApiKey, request.BaseUrl, request.Model);
            var result = await ProcessRunner.RunAndCaptureAsync(GetClaudeExecutable(), args, request.Cwd, env, cancellationToken, timeout: TimeSpan.FromSeconds(30));
            if (result.ExitCode == 0 && (!string.IsNullOrWhiteSpace(result.StdOut) || string.IsNullOrWhiteSpace(result.StdErr)))
            {
                return new TestConnectionResult(true, "连接成功，模型响应正常");
            }

            var message = string.IsNullOrWhiteSpace(result.StdErr) ? (string.IsNullOrWhiteSpace(result.StdOut) ? "未收到模型响应" : result.StdOut.Trim()) : result.StdErr.Trim();
            return new TestConnectionResult(false, message);
        }
        catch (OperationCanceledException)
        {
            return new TestConnectionResult(false, "测试超时（30秒）");
        }
        catch (Exception ex)
        {
            return new TestConnectionResult(false, ex.Message);
        }
        finally
        {
            CleanupTempFiles(tempFiles);
        }
    }

    public async Task RunAsync(ClaudeCommandMessage command, string sessionId, JsonWebSocketConnection connection, RunningClaudeSession runningSession, CancellationToken cancellationToken)
    {
        var tempFiles = new List<string>();
        var stderrLines = new ConcurrentQueue<string>();
        var sawPlainText = false;
        var args = await BuildArgumentsAsync(
            command.Prompt ?? string.Empty,
            sessionId,
            resume: !string.IsNullOrWhiteSpace(command.SessionId),
            command.Cwd,
            command.Model,
            command.PermissionMode,
            command.ApiKey,
            command.BaseUrl,
            (IReadOnlyList<ImageAttachment>)(command.Images ?? new List<ImageAttachment>()),
            noSessionPersistence: false,
            tempFiles,
            cancellationToken);

        var env = BuildEnvironment(command.ApiKey, command.BaseUrl, command.Model);
        using var process = ProcessRunner.Start(GetClaudeExecutable(), args, command.Cwd, env);
        runningSession.AttachProcess(process);
        try
        {
            var stdOutTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (TryParseJson(line, out var node) && node is not null)
                    {
                        var handled = await TryHandleHookEventAsync(node, sessionId, connection, runningSession, cancellationToken);
                        if (!handled)
                        {
                            await connection.SendAsync(new { type = "claude-response", data = NormalizeClaudeEvent(node), sessionId }, cancellationToken);
                        }
                    }
                    else
                    {
                        sawPlainText = true;
                        await connection.SendAsync(new
                        {
                            type = "claude-response",
                            data = new
                            {
                                type = "content_block_delta",
                                delta = new { text = line + "\n" }
                            },
                            sessionId
                        }, cancellationToken);
                    }
                }
            }, cancellationToken);

            var stdErrTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        stderrLines.Enqueue(line);
                    }
                }
            }, cancellationToken);

            await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync(cancellationToken));

            if (sawPlainText)
            {
                await connection.SendAsync(new
                {
                    type = "claude-response",
                    data = new { type = "content_block_stop" },
                    sessionId
                }, cancellationToken);
            }

            var pendingStopReason = runningSession.ConsumePendingStopReason();
            if (!string.IsNullOrWhiteSpace(pendingStopReason))
            {
                await connection.SendAsync(new { type = "claude-error", error = pendingStopReason, sessionId }, cancellationToken);
                return;
            }

            if (process.ExitCode == 0)
            {
                await connection.SendAsync(new { type = "claude-complete", sessionId }, cancellationToken);
                return;
            }

            var error = stderrLines.IsEmpty ? $"Claude CLI exited with code {process.ExitCode}" : string.Join("\n", stderrLines);
            await connection.SendAsync(new { type = "claude-error", error, sessionId }, cancellationToken);
        }
        finally
        {
            runningSession.DetachProcess(process);
            CleanupTempFiles(tempFiles);
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task<List<string>> BuildArgumentsAsync(
        string prompt,
        string sessionId,
        bool resume,
        string? cwd,
        string? model,
        string? permissionMode,
        string? apiKey,
        string? baseUrl,
        IReadOnlyList<ImageAttachment> images,
        bool noSessionPersistence,
        List<string> tempFiles,
        CancellationToken cancellationToken)
    {
        var finalPrompt = await PreparePromptWithImagesAsync(prompt, sessionId, images, tempFiles, cancellationToken);
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--include-partial-messages",
            "--include-hook-events",
            "--permission-mode", NormalizePermissionMode(permissionMode),
            "--setting-sources", "project,user,local"
        };

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model!);
        }

        if (!string.IsNullOrWhiteSpace(cwd))
        {
            arguments.Add("--add-dir");
            arguments.Add(Path.GetFullPath(cwd));
        }

        var imageDirectories = images.Count == 0
            ? Array.Empty<string>()
            : tempFiles.Select(Path.GetDirectoryName).Where(directory => !string.IsNullOrWhiteSpace(directory)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()!;
        foreach (var directory in imageDirectories)
        {
            if (!string.IsNullOrWhiteSpace(directory) && !string.Equals(directory, cwd, StringComparison.OrdinalIgnoreCase))
            {
                arguments.Add("--add-dir");
                arguments.Add(directory!);
            }
        }

        if (resume)
        {
            arguments.Add("--resume");
            arguments.Add(sessionId);
        }
        else
        {
            arguments.Add("--session-id");
            arguments.Add(sessionId);
        }

        if (noSessionPersistence)
        {
            arguments.Add("--no-session-persistence");
        }

        var mcpConfig = await _claudeJsonService.LoadMergedMcpConfigAsync(cwd, cancellationToken);
        if (mcpConfig is not null)
        {
            var mcpFile = Path.Combine(_paths.TempDirectory, $"mcp-{sessionId}.json");
            await File.WriteAllTextAsync(mcpFile, mcpConfig.ToJsonString(JsonDefaults.Pretty), cancellationToken);
            tempFiles.Add(mcpFile);
            arguments.Add("--mcp-config");
            arguments.Add(mcpFile);
        }

        arguments.Add(finalPrompt);
        return arguments;
    }

    private async Task<string> PreparePromptWithImagesAsync(string prompt, string sessionId, IReadOnlyList<ImageAttachment> images, List<string> tempFiles, CancellationToken cancellationToken)
    {
        if (images.Count == 0)
        {
            return prompt;
        }

        var imageDir = Path.Combine(_paths.TempDirectory, "images", sessionId);
        Directory.CreateDirectory(imageDir);
        var savedFiles = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var extension = ImageExtensions.FromMediaType(image.MediaType);
            var safeName = string.IsNullOrWhiteSpace(image.Name) ? $"image-{i + 1}{extension}" : SanitizeFileName(Path.GetFileNameWithoutExtension(image.Name)) + extension;
            var filePath = Path.Combine(imageDir, safeName);
            var bytes = Convert.FromBase64String(image.Data);
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken);
            tempFiles.Add(filePath);
            savedFiles.Add(filePath);
        }

        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder.AppendLine(prompt.Trim());
            builder.AppendLine();
        }

        builder.AppendLine("以下是用户附带的图片文件，请直接读取并结合这些图片继续回答：");
        foreach (var file in savedFiles)
        {
            builder.AppendLine(file);
        }

        return builder.ToString().Trim();
    }

    private async Task<bool> TryHandleHookEventAsync(JsonNode node, string sessionId, JsonWebSocketConnection connection, RunningClaudeSession runningSession, CancellationToken cancellationToken)
    {
        if (node is not JsonObject obj)
        {
            return false;
        }

        var eventType = obj["type"]?.GetValue<string>() ?? string.Empty;
        var hookEventName = obj["hook_event_name"]?.GetValue<string>()
            ?? obj["event"]?.GetValue<string>()
            ?? obj["name"]?.GetValue<string>()
            ?? string.Empty;

        if (!IsHookLikeEvent(eventType, hookEventName))
        {
            return false;
        }

        var toolName = ExtractToolName(obj);
        var input = ExtractToolInput(obj);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        var requestId = Guid.NewGuid().ToString();
        var isPlanMode = _planModeStateStore.Get(sessionId);
        if (isPlanMode)
        {
            await connection.SendAsync(new { type = "plan-execution-request", requestId, toolName, input, sessionId }, cancellationToken);
        }
        else
        {
            await connection.SendAsync(new { type = "permission-request", requestId, toolName, input, sessionId }, cancellationToken);
        }

        var decision = await _approvals.CreatePendingAsync(requestId, cancellationToken);
        if (decision.Cancelled == true)
        {
            await connection.SendAsync(new { type = "permission-cancelled", requestId, sessionId }, cancellationToken);
            runningSession.RequestStopAfterCurrentEvent($"用户已取消 {toolName}");
            return true;
        }

        var allowed = isPlanMode ? decision.Confirmed == true || decision.Allow == true : decision.Allow == true || decision.Confirmed == true;
        if (!allowed)
        {
            await connection.SendAsync(new { type = "permission-cancelled", requestId, sessionId }, cancellationToken);
            runningSession.RequestStopAfterCurrentEvent(decision.Message ?? "用户拒绝执行工具");
            return true;
        }

        return true;
    }

    private static object NormalizeClaudeEvent(JsonNode node)
    {
        if (node is not JsonObject obj)
        {
            return node;
        }

        var type = obj["type"]?.GetValue<string>() ?? string.Empty;
        if (type == "result" || type == "assistant" || type == "user" || type == "content_block_delta" || type == "content_block_stop" || type == "system")
        {
            return node;
        }

        var text = obj["text"]?.GetValue<string>()
            ?? (obj["message"] is JsonObject messageObj ? messageObj["text"]?.GetValue<string>() : null)
            ?? (obj["delta"] is JsonObject deltaObj ? deltaObj["text"]?.GetValue<string>() : null)
            ?? (obj["content"] is JsonObject contentObj ? contentObj["text"]?.GetValue<string>() : null);

        if (!string.IsNullOrWhiteSpace(text))
        {
            return new
            {
                type = "content_block_delta",
                delta = new { text }
            };
        }

        return node;
    }

    private static bool IsHookLikeEvent(string eventType, string hookEventName)
    {
        if (eventType.Contains("hook", StringComparison.OrdinalIgnoreCase) || hookEventName.Contains("hook", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (eventType.Contains("tool", StringComparison.OrdinalIgnoreCase) || hookEventName.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string? ExtractToolName(JsonObject obj)
    {
        return obj["tool_name"]?.GetValue<string>()
            ?? obj["toolName"]?.GetValue<string>()
            ?? (obj["tool"] is JsonObject toolObj ? toolObj["name"]?.GetValue<string>() : null)
            ?? (obj["data"] is JsonObject dataObj ? dataObj["tool_name"]?.GetValue<string>() ?? dataObj["toolName"]?.GetValue<string>() : null);
    }

    private static JsonNode? ExtractToolInput(JsonObject obj)
    {
        return obj["tool_input"]?.DeepClone()
            ?? obj["toolInput"]?.DeepClone()
            ?? obj["input"]?.DeepClone()
            ?? (obj["tool"] is JsonObject toolObj ? toolObj["input"]?.DeepClone() : null)
            ?? (obj["data"] is JsonObject dataObj ? dataObj["tool_input"]?.DeepClone() ?? dataObj["toolInput"]?.DeepClone() : null)
            ?? new JsonObject();
    }

    private static Dictionary<string, string> BuildEnvironment(string? apiKey, string? baseUrl, string? model)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            env["ANTHROPIC_API_KEY"] = apiKey!;
            env["ANTHROPIC_AUTH_TOKEN"] = apiKey!;
        }

        env["ANTHROPIC_BASE_URL"] = string.IsNullOrWhiteSpace(baseUrl) ? "https://yxai.chat" : baseUrl!;

        if (!string.IsNullOrWhiteSpace(model))
        {
            env["ANTHROPIC_MODEL"] = model!;
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model!;
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model!;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model!;
        }

        if (OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH")))
        {
            var bashPath = FindGitBashPath();
            if (!string.IsNullOrWhiteSpace(bashPath))
            {
                env["CLAUDE_CODE_GIT_BASH_PATH"] = bashPath!;
            }
        }

        return env;
    }

    private static string NormalizePermissionMode(string? permissionMode)
    {
        return permissionMode switch
        {
            "acceptEdits" => "acceptEdits",
            "bypassPermissions" => "bypassPermissions",
            "dontAsk" => "dontAsk",
            "plan" => "plan",
            "auto" => "auto",
            _ => "default"
        };
    }

    private static string GetClaudeExecutable()
    {
        return OperatingSystem.IsWindows() ? "claude.cmd" : "claude";
    }

    private static bool TryParseJson(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text);
            return node is not null;
        }
        catch
        {
            node = null;
            return false;
        }
    }

    private static void CleanupTempFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.OrderByDescending(path => path.Length))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string? FindGitBashPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\usr\bin\bash.exe"
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "image";
        }

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }

        return name;
    }
}

sealed class JsonWebSocketConnection
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public JsonWebSocketConnection(WebSocket socket)
    {
        _socket = socket;
    }

    public async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, JsonDefaults.Web);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

sealed class RunningClaudeSession
{
    private readonly CancellationTokenSource _cts;
    private readonly object _sync = new();
    private Process? _process;
    private string? _pendingStopReason;

    public RunningClaudeSession(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    public void AttachProcess(Process process)
    {
        lock (_sync)
        {
            _process = process;
        }
    }

    public void DetachProcess(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_process, process))
            {
                _process = null;
            }
        }
    }

    public void RequestStopAfterCurrentEvent(string? reason)
    {
        lock (_sync)
        {
            _pendingStopReason ??= string.IsNullOrWhiteSpace(reason) ? "操作已取消" : reason;
        }
        _cts.Cancel();
        TryKillProcess();
    }

    public string? ConsumePendingStopReason()
    {
        lock (_sync)
        {
            var reason = _pendingStopReason;
            _pendingStopReason = null;
            return reason;
        }
    }

    public Task AbortAsync()
    {
        RequestStopAfterCurrentEvent("会话已停止");
        return Task.CompletedTask;
    }

    private void TryKillProcess()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }
}

static class ProcessRunner
{
    public static Process Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(workingDirectory)
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        return process;
    }

    public static async Task<ProcessResult> RunAndCaptureAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var linkedCts = timeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (timeout.HasValue)
        {
            linkedCts!.CancelAfter(timeout.Value);
            cancellationToken = linkedCts.Token;
        }

        using var process = Start(fileName, arguments, workingDirectory, environment);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static async Task RunForExitAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        using var process = Start(fileName, arguments, workingDirectory, environment);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"Process exited with code {process.ExitCode}" : stderr.Trim());
        }
    }
}

static class PortHelper
{
    public static int FindAvailablePort(int desiredPort, int maxAttempts)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var port = desiredPort + i;
            if (IsAvailable(port))
            {
                return port;
            }

            Console.WriteLine($"  端口 {port} 已被占用，尝试端口 {port + 1}...");
        }

        throw new InvalidOperationException($"无法找到可用端口 (已尝试 {maxAttempts} 次)");
    }

    private static bool IsAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

static class BrowserLauncher
{
    public static void Open(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
}

static class ShellHelper
{
    public static Task OpenFolderAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return ProcessRunner.RunForExitAsync("explorer.exe", new[] { fullPath }, null, null, cancellationToken);
        }

        if (OperatingSystem.IsMacOS())
        {
            return ProcessRunner.RunForExitAsync("open", new[] { fullPath }, null, null, cancellationToken);
        }

        return ProcessRunner.RunForExitAsync("xdg-open", new[] { fullPath }, null, null, cancellationToken);
    }
}

static class JsonDefaults
{
    public static readonly JsonSerializerOptions Web = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

static class ImageExtensions
{
    public static string FromMediaType(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".bin"
        };
    }
}

sealed class FileTooLargeException : Exception
{
    public FileTooLargeException(string message) : base(message)
    {
    }
}

record ProcessResult(int ExitCode, string StdOut, string StdErr);
record TestConnectionResult(bool Success, string Message);
record SessionSummary(string Summary, int MsgCount);

sealed class ModelItem
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
}

sealed class SettingsRequest
{
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    public string? Model { get; set; }
}

sealed class TestConnectionRequest
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? Cwd { get; set; }
}

sealed class ProjectInfo
{
    public string Name { get; set; } = string.Empty;
    public List<SessionInfo> Sessions { get; set; } = new();
}

sealed class SessionInfo
{
    public string Id { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public int MsgCount { get; set; }
    public DateTimeOffset Mtime { get; set; }
}

sealed class ChatHistoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public List<ChatHistoryPart>? Parts { get; set; }
}

sealed class ChatHistoryPart
{
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonNode? Input { get; set; }
    public string? ToolUseId { get; set; }
    public string? Content { get; set; }
    public bool? IsError { get; set; }
}

sealed class ToolResultPayload
{
    public string ToolUseId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsError { get; set; }
}

sealed class BrowseResponse
{
    public string Path { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public List<BrowseDirectoryItem> Dirs { get; set; } = new();
}

sealed class BrowseDirectoryItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

sealed class FileTreeItem
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public long? Size { get; set; }
    public List<FileTreeItem>? Children { get; set; }
}

sealed class FileFlatItem
{
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

sealed class FileReadResponse
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Content { get; set; } = string.Empty;
}

sealed class ClaudeCommandMessage
{
    public string? Prompt { get; set; }
    public List<ImageAttachment>? Images { get; set; }
    public string? SessionId { get; set; }
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    public string? PermissionMode { get; set; }
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
}

sealed class ImageAttachment
{
    public string Data { get; set; } = string.Empty;
    public string MediaType { get; set; } = "image/png";
    public string? Name { get; set; }
}

sealed class PermissionResponseMessage
{
    public string? RequestId { get; set; }
    public bool? Allow { get; set; }
    public bool? Confirmed { get; set; }
    public bool? Cancelled { get; set; }
    public JsonNode? UpdatedInput { get; set; }
    public string? Message { get; set; }
}

sealed class ApprovalDecision
{
    public bool? Allow { get; set; }
    public bool? Confirmed { get; set; }
    public bool? Cancelled { get; set; }
    public JsonNode? UpdatedInput { get; set; }
    public string? Message { get; set; }
}
