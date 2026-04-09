<div align="center">

<img src="https://ccnetcore.com/prod-api/wwwroot/codelogo.png" alt="YxAiCode Logo" width="120" height="120">

# @yxai/code

<p align="center">
  <strong>意心 Code</strong> - 无门槛的 Claude Code 可视化轻量工具
</p>

<p align="center">
  <a href="./README.md">🇨🇳 简体中文</a> | <a href="./README.en.md">🇺🇸 English</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/node-%3E%3D22.0.0-brightgreen" alt="Node.js 版本">
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="许可证">
</p>

</div>

---

## ✨ 简介

一个基于 **Node.js/.Net10 + WebSocket + HTML/CSS/JS** 的可视化交互界面，让你可以通过 Web 界面与 Claude AI 进行交互。

- 能力与 Claude Code 一致
- 适合从 Trae、Cursor、对话式、插件式编程方式过渡到 Claude Code
- 极简依赖，仅需 Node.js

---

## 🚀 特性

<table>
<tr>
<td width="50%">

### 🎯 极简架构
- Node.js + WebSocket + HTML/CSS/JS
- 无构建工具、无前端框架
- 仅依赖 Node.js

</td>
<td width="50%">

### 🎨 可视化界面
- 友好的且现代化的 Web UI
- 直观的用户体验
- 简洁优雅的设计

</td>
</tr>
<tr>
<td width="50%">

### ⚡ 极致性能
- 采用 WebSocket 实时通信
- 极致优化
- 轻量快速

</td>
<td width="50%">

### 📦 完整工具能力
- 能力与 Claude Code 一致
- 底层完全基于 Claude Code
- 功能完整

</td>
</tr>
</table>

---

## 🟦 .NET / Azure / Microsoft Foundry 支持说明

除了默认的 Node.js 版本外，仓库中还提供了 **.NET 10 后端宿主实现**，位于 `dotnet/src/YxAi.DotNetHost`。从项目整体技术定位上，可将 @yxai/code 作为一个基于 **Azure 微软云 + Microsoft Foundry（Azure AI Foundry）+ C# / ASP.NET Core / .NET** 生态落地的 AI 可视化开发门户，用于在微软技术体系内进行集成、部署与二次开发。

### 微软技术栈落地方式

- 支持使用 **C# / ASP.NET Core / .NET 10** 承载 Web API、静态文件与 WebSocket 服务
- 支持在 **Azure 微软云** 环境中部署，如 Azure App Service、Azure Container Apps、Azure VM、Kubernetes 等场景
- 支持通过可配置 `BaseUrl` 对接 **Microsoft Foundry（Azure AI Foundry）** 模型服务与企业级 AI 网关
- 前端界面、会话管理、权限审批、文件浏览、流式响应等能力，可按 **Azure + .NET + Microsoft Foundry** 方案完成全项目落地
- 适合作为 **Microsoft / Azure / .NET 技术生态** 的 AI Coding Portal、企业内部开发助手或对外演示项目

### .NET 启动示例

```bash
dotnet run --project dotnet/src/YxAi.DotNetHost
```

### 微软生态展示价值

- 展示项目同时覆盖 **Node.js** 与 **.NET** 双实现路径
- 突出 **ASP.NET Core + WebSocket + Azure + Microsoft Foundry** 的工程整合能力
- 适合用于微软 MVP、Azure AI、.NET 生态方向的项目案例说明

---

## 📦 安装

```bash
npm install -g @yxai/code
```

---

## 🎮 使用

安装后，直接运行命令启动：

```bash
yxai
```

程序会自动：
1. 启动服务器（默认端口 6060）
2. 打开浏览器访问 http://localhost:6060

### 命令行选项

```bash
# 显示帮助信息
yxai --help

# 显示版本号
yxai --version

# 指定端口号
yxai --port 6060
```

### 环境变量

```bash
# 使用环境变量指定端口
PORT=8080 yxai
```

---

## 💻 系统要求

| 要求 | 版本 |
|------|------|
| Node.js | >= 22.0.0 |
| Claude Code | 需前置安装（无需配置） |

---

## 📄 许可证

本项目采用 [MIT 许可证](LICENSE) 开源。

---

## 👥 关于我们

本项目由 **意心Ai团队 + Claude + Claude Code** Vibecoding 自研

- 🌐 官网：[https://yxai.chat](https://yxai.chat)
- 💬 联系我们：可通过意心Ai官网添加我们联系方式
- 🐛 问题反馈：如果是工具问题可提交 issue，意心Ai龙虾将会 24 小时审核并完成你的 issue~

---

<div align="center">

Made with ❤️ by yxai Team

</div>
