#!/usr/bin/env node
/**
 * 意心Code (yxcode) - Claude Code 可视化交互界面
 *
 * 极简架构：Express 静态服务 + WebSocket + Claude Agent SDK
 * 仅依赖 Node.js，无需构建工具
 */

import express from 'express';
import http from 'http';
import { WebSocketServer, WebSocket } from 'ws';
import { query } from '@anthropic-ai/claude-agent-sdk';
import crypto from 'crypto';
import path from 'path';
import os from 'os';
import { fileURLToPath } from 'url';
import { promises as fs } from 'fs';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// --- Config ---
const PORT = parseInt(process.env.PORT, 10) || 3456;

const CLAUDE_MODELS = [
  { value: 'sonnet', label: 'Sonnet' },
  { value: 'opus', label: 'Opus' },
  { value: 'haiku', label: 'Haiku' },
  { value: 'sonnet[1m]', label: 'Sonnet [1M]' },
];

// --- Session & Permission State ---
const activeSessions = new Map();
const pendingApprovals = new Map();

// --- Helpers ---
function uid() {
  return crypto.randomUUID?.() ?? crypto.randomBytes(16).toString('hex');
}

function wsSend(ws, data) {
  if (ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(data));
  }
}

// --- Tool Approval ---
function waitForApproval(requestId, signal) {
  return new Promise((resolve) => {
    let settled = false;
    const finalize = (v) => {
      if (settled) return;
      settled = true;
      pendingApprovals.delete(requestId);
      if (signal) signal.removeEventListener('abort', onAbort);
      resolve(v);
    };
    const onAbort = () => finalize({ cancelled: true });
    if (signal) {
      if (signal.aborted) return finalize({ cancelled: true });
      signal.addEventListener('abort', onAbort, { once: true });
    }
    pendingApprovals.set(requestId, finalize);
  });
}

function resolveApproval(requestId, decision) {
  const fn = pendingApprovals.get(requestId);
  if (fn) fn(decision);
}

// --- Claude SDK Query ---
async function runQuery(prompt, options, ws) {
  let sessionId = options.sessionId || null;

  // Inject API Base URL and API Key into env before query
  const prevBaseUrl = process.env.ANTHROPIC_BASE_URL;
  const prevApiKey = process.env.ANTHROPIC_API_KEY;
  if (options.apiBaseUrl) {
    process.env.ANTHROPIC_BASE_URL = options.apiBaseUrl;
    console.log(`[config] ANTHROPIC_BASE_URL = ${options.apiBaseUrl}`);
  }
  if (options.apiKey) {
    process.env.ANTHROPIC_API_KEY = options.apiKey;
    console.log(`[config] ANTHROPIC_API_KEY = ***${options.apiKey.slice(-6)}`);
  }

  const sdkOpts = {
    model: options.model || 'sonnet',
    cwd: options.cwd || process.cwd(),
    tools: { type: 'preset', preset: 'claude_code' },
    systemPrompt: { type: 'preset', preset: 'claude_code' },
    settingSources: ['project', 'user', 'local'],
  };

  if (options.permissionMode && options.permissionMode !== 'default') {
    sdkOpts.permissionMode = options.permissionMode;
  }
  if (sessionId) {
    sdkOpts.resume = sessionId;
  }

  // Load MCP servers from ~/.claude.json
  const mcpServers = await loadMcpConfig(sdkOpts.cwd);
  if (mcpServers) sdkOpts.mcpServers = mcpServers;

  // Permission callback
  sdkOpts.canUseTool = async (toolName, input, context) => {
    if (sdkOpts.permissionMode === 'bypassPermissions') {
      return { behavior: 'allow', updatedInput: input };
    }
    const requestId = uid();
    wsSend(ws, {
      type: 'permission-request',
      requestId, toolName, input,
      sessionId,
    });
    const decision = await waitForApproval(requestId, context?.signal);
    if (!decision || decision.cancelled) {
      wsSend(ws, { type: 'permission-cancelled', requestId, sessionId });
      return { behavior: 'deny', message: 'Permission denied or cancelled' };
    }
    if (decision.allow) {
      return { behavior: 'allow', updatedInput: decision.updatedInput ?? input };
    }
    return { behavior: 'deny', message: decision.message ?? 'User denied' };
  };

  // Start query
  const prev = process.env.CLAUDE_CODE_STREAM_CLOSE_TIMEOUT;
  process.env.CLAUDE_CODE_STREAM_CLOSE_TIMEOUT = '300000';

  const qi = query({ prompt, options: sdkOpts });

  if (prev !== undefined) process.env.CLAUDE_CODE_STREAM_CLOSE_TIMEOUT = prev;
  else delete process.env.CLAUDE_CODE_STREAM_CLOSE_TIMEOUT;

  if (sessionId) activeSessions.set(sessionId, qi);

  try {
    for await (const msg of qi) {
      // Capture session id
      if (msg.session_id && !sessionId) {
        sessionId = msg.session_id;
        activeSessions.set(sessionId, qi);
        wsSend(ws, { type: 'session-created', sessionId });
      }
      wsSend(ws, { type: 'claude-response', data: msg, sessionId });

      // Token usage
      if (msg.type === 'result' && msg.modelUsage) {
        const mk = Object.keys(msg.modelUsage)[0];
        const md = msg.modelUsage[mk];
        if (md) {
          const used = (md.cumulativeInputTokens || md.inputTokens || 0)
            + (md.cumulativeOutputTokens || md.outputTokens || 0)
            + (md.cumulativeCacheReadInputTokens || md.cacheReadInputTokens || 0)
            + (md.cumulativeCacheCreationInputTokens || md.cacheCreationInputTokens || 0);
          wsSend(ws, { type: 'token-usage', used, sessionId });
        }
      }
    }
    wsSend(ws, { type: 'claude-complete', sessionId });
  } catch (err) {
    console.error('SDK error:', err.message);
    wsSend(ws, { type: 'claude-error', error: err.message, sessionId });
  } finally {
    if (sessionId) activeSessions.delete(sessionId);
    // Restore env vars
    if (options.apiBaseUrl) {
      if (prevBaseUrl !== undefined) process.env.ANTHROPIC_BASE_URL = prevBaseUrl;
      else delete process.env.ANTHROPIC_BASE_URL;
    }
    if (options.apiKey) {
      if (prevApiKey !== undefined) process.env.ANTHROPIC_API_KEY = prevApiKey;
      else delete process.env.ANTHROPIC_API_KEY;
    }
  }
}

// --- Load MCP Config ---
async function loadMcpConfig(cwd) {
  try {
    const cfgPath = path.join(os.homedir(), '.claude.json');
    const raw = await fs.readFile(cfgPath, 'utf8').catch(() => null);
    if (!raw) return null;
    const cfg = JSON.parse(raw);
    let servers = {};
    if (cfg.mcpServers) servers = { ...cfg.mcpServers };
    if (cfg.claudeProjects?.[cwd]?.mcpServers) {
      servers = { ...servers, ...cfg.claudeProjects[cwd].mcpServers };
    }
    return Object.keys(servers).length ? servers : null;
  } catch { return null; }
}

// --- Express + WebSocket ---
const app = express();
app.use(express.json());
app.use(express.static(path.join(__dirname, 'public')));

// API: list models
app.get('/api/models', (_req, res) => res.json(CLAUDE_MODELS));

// API: list projects (scan ~/.claude/projects/)
app.get('/api/projects', async (_req, res) => {
  try {
    const base = path.join(os.homedir(), '.claude', 'projects');
    const entries = await fs.readdir(base, { withFileTypes: true }).catch(() => []);
    const projects = [];
    for (const ent of entries) {
      if (!ent.isDirectory()) continue;
      const projDir = path.join(base, ent.name);
      const files = await fs.readdir(projDir).catch(() => []);
      const sessions = [];
      for (const f of files) {
        if (!f.endsWith('.jsonl')) continue;
        const fp = path.join(projDir, f);
        const stat = await fs.stat(fp).catch(() => null);
        if (!stat) continue;
        let summary = '', msgCount = 0;
        try {
          const raw = await fs.readFile(fp, 'utf8');
          const lines = raw.split('\n').filter(Boolean).slice(0, 20);
          for (const line of lines) {
            try {
              const obj = JSON.parse(line);
              msgCount++;
              if (!summary && obj.type === 'human' && typeof obj.message?.content === 'string') {
                summary = obj.message.content.slice(0, 100);
              }
            } catch {}
          }
        } catch {}
        sessions.push({ id: f.replace('.jsonl', ''), file: f, summary, msgCount, mtime: stat.mtime });
      }
      sessions.sort((a, b) => new Date(b.mtime) - new Date(a.mtime));
      if (sessions.length) projects.push({ name: ent.name, sessions });
    }
    projects.sort((a, b) => {
      const ta = a.sessions[0]?.mtime || 0, tb = b.sessions[0]?.mtime || 0;
      return new Date(tb) - new Date(ta);
    });
    res.json(projects);
  } catch (e) { res.status(500).json({ error: e.message }); }
});

// API: sessions for a single project
app.get('/api/projects/:name/sessions', async (req, res) => {
  try {
    const projDir = path.join(os.homedir(), '.claude', 'projects', req.params.name);
    const files = await fs.readdir(projDir).catch(() => []);
    const sessions = [];
    for (const f of files) {
      if (!f.endsWith('.jsonl')) continue;
      const fp = path.join(projDir, f);
      const stat = await fs.stat(fp).catch(() => null);
      if (!stat) continue;
      let summary = '', msgCount = 0;
      try {
        const raw = await fs.readFile(fp, 'utf8');
        const lines = raw.split('\n').filter(Boolean).slice(0, 20);
        for (const line of lines) {
          try {
            const obj = JSON.parse(line);
            msgCount++;
            if (!summary && obj.type === 'human' && typeof obj.message?.content === 'string') {
              summary = obj.message.content.slice(0, 100);
            }
          } catch {}
        }
      } catch {}
      sessions.push({ id: f.replace('.jsonl', ''), file: f, summary, msgCount, mtime: stat.mtime });
    }
    sessions.sort((a, b) => new Date(b.mtime) - new Date(a.mtime));
    res.json(sessions);
  } catch (e) { res.status(500).json({ error: e.message }); }
});

// API: messages for a session
app.get('/api/projects/:name/sessions/:id/messages', async (req, res) => {
  try {
    const fp = path.join(os.homedir(), '.claude', 'projects', req.params.name, req.params.id + '.jsonl');
    const raw = await fs.readFile(fp, 'utf8');
    const messages = [];
    for (const line of raw.split('\n').filter(Boolean)) {
      try {
        const obj = JSON.parse(line);
        if (obj.type === 'human') {
          const text = typeof obj.message?.content === 'string' ? obj.message.content
            : Array.isArray(obj.message?.content) ? obj.message.content.map(c => c.text || '').join('') : '';
          if (text) messages.push({ role: 'user', content: text });
        } else if (obj.type === 'assistant') {
          const text = typeof obj.message?.content === 'string' ? obj.message.content
            : Array.isArray(obj.message?.content) ? obj.message.content.filter(c => c.type === 'text').map(c => c.text || '').join('\n') : '';
          if (text) messages.push({ role: 'assistant', content: text });
        }
      } catch {}
    }
    res.json(messages);
  } catch (e) { res.status(500).json({ error: e.message }); }
});

// API: file tree
const SKIP_DIRS = new Set(['node_modules', '.git', '.svn', '.hg', '__pycache__', '.next', '.nuxt', 'dist', 'build', '.cache', '.claude']);
app.get('/api/files', async (req, res) => {
  try {
    const root = req.query.cwd || process.cwd();
    async function scan(dir, depth) {
      if (depth > 5) return [];
      const entries = await fs.readdir(dir, { withFileTypes: true }).catch(() => []);
      const items = [];
      for (const ent of entries) {
        if (ent.name.startsWith('.') && ent.name !== '.env') continue;
        const full = path.join(dir, ent.name);
        if (ent.isDirectory()) {
          if (SKIP_DIRS.has(ent.name)) continue;
          const children = await scan(full, depth + 1);
          items.push({ name: ent.name, type: 'dir', path: full, children });
        } else {
          const stat = await fs.stat(full).catch(() => null);
          items.push({ name: ent.name, type: 'file', path: full, size: stat?.size || 0 });
        }
      }
      items.sort((a, b) => (a.type === b.type ? a.name.localeCompare(b.name) : a.type === 'dir' ? -1 : 1));
      return items;
    }
    res.json(await scan(root, 0));
  } catch (e) { res.status(500).json({ error: e.message }); }
});

// API: read single file (max 500KB)
app.get('/api/file', async (req, res) => {
  try {
    const fp = req.query.path;
    if (!fp) return res.status(400).json({ error: 'path required' });
    const stat = await fs.stat(fp);
    if (stat.size > 500 * 1024) return res.status(413).json({ error: 'File too large (>500KB)' });
    const content = await fs.readFile(fp, 'utf8');
    res.json({ path: fp, size: stat.size, content });
  } catch (e) { res.status(500).json({ error: e.message }); }
});

const server = http.createServer(app);
const wss = new WebSocketServer({ server, path: '/ws' });

wss.on('connection', (ws) => {
  console.log('[WS] client connected');

  ws.on('message', (raw) => {
    let msg;
    try { msg = JSON.parse(raw); } catch { return; }

    switch (msg.type) {
      case 'claude-command':
        runQuery(msg.prompt, {
          sessionId: msg.sessionId || null,
          cwd: msg.cwd || null,
          model: msg.model || 'sonnet',
          permissionMode: msg.permissionMode || 'default',
          apiBaseUrl: msg.apiBaseUrl || null,
          apiKey: msg.apiKey || null,
        }, ws).catch((e) => console.error('[query error]', e.message));
        break;

      case 'permission-response':
        resolveApproval(msg.requestId, {
          allow: msg.allow,
          updatedInput: msg.updatedInput,
          message: msg.message,
        });
        break;

      case 'abort-session':
        if (msg.sessionId && activeSessions.has(msg.sessionId)) {
          const qi = activeSessions.get(msg.sessionId);
          qi.interrupt().catch(() => {});
          activeSessions.delete(msg.sessionId);
          wsSend(ws, { type: 'session-aborted', sessionId: msg.sessionId });
        }
        break;
    }
  });

  ws.on('close', () => console.log('[WS] client disconnected'));
});

server.listen(PORT, () => {
  console.log(`\n  意心Code (yxcode) 已启动`);
  console.log(`  http://localhost:${PORT}\n`);
});
