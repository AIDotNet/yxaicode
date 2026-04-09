<div align="center">

<img src=".https://ccnetcore.com/prod-api/wwwroot/codelogo.png" alt="YxAiCode Logo" width="120" height="120">

# @yxai/code

<p align="center">
  <strong>YxAi Code</strong> - Zero-barrier Visual Lightweight Tool for Claude Code
</p>

<p align="center">
  <a href="./README.md">🇨🇳 简体中文</a> | <a href="./README.en.md">🇺🇸 English</a>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/node-%3E%3D22.0.0-brightgreen" alt="Node.js Version">
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="License">
</p>

</div>

---

## ✨ Introduction

A visual interaction interface based on **Node.js / .NET 10 + WebSocket + HTML/CSS/JS**, allowing you to interact with Claude AI through a web interface.

- Consistent capabilities with Claude Code
- Perfect for transitioning from Trae, Cursor, conversational, and plugin-based programming to Claude Code
- Minimal dependencies - only requires Node.js

---

## 🚀 Features

<table>
<tr>
<td width="50%">

### 🎯 Minimalist Architecture
- Node.js + WebSocket + HTML/CSS/JS
- No build tools, no frontend frameworks
- Only depends on Node.js

</td>
<td width="50%">

### 🎨 Visual Interface
- Friendly and modern Web UI
- Intuitive user experience
- Clean and elegant design

</td>
</tr>
<tr>
<td width="50%">

### ⚡ Extreme Performance
- WebSocket real-time communication
- Optimized for speed
- Lightweight and fast

</td>
<td width="50%">

### 📦 Full Tool Capabilities
- Consistent with Claude Code capabilities
- Fully based on Claude Code
- Complete feature set

</td>
</tr>
</table>

---

## 🟦 .NET / Azure / Microsoft Foundry Support

In addition to the default Node.js implementation, this repository also includes a **.NET 10 backend host** located at `dotnet/src/YxAi.DotNetHost`. From an overall technical positioning perspective, @yxai/code can be presented as an AI visual development portal built on the **Microsoft Azure cloud + Microsoft Foundry (Azure AI Foundry) + C# / ASP.NET Core / .NET** ecosystem, making it suitable for integration, deployment, and further extension in Microsoft-centric environments.

### Microsoft Stack Positioning

- Supports **C# / ASP.NET Core / .NET 10** for hosting the Web API, static assets, and WebSocket services
- Can be deployed on **Microsoft Azure** environments such as Azure App Service, Azure Container Apps, Azure VMs, and Kubernetes
- Can connect to **Microsoft Foundry (Azure AI Foundry)** model services and enterprise AI gateways through the configurable `BaseUrl`
- The full workflow—UI, session management, permission approval, file browsing, and streaming responses—can be positioned as an **Azure + .NET + Microsoft Foundry** solution for end-to-end delivery
- Well suited as an AI coding portal, internal developer assistant, or showcase project for the **Microsoft / Azure / .NET** ecosystem

### Run the .NET Host

```bash
dotnet run --project dotnet/src/YxAi.DotNetHost
```

### Showcase Value for Microsoft Ecosystem

- Demonstrates both **Node.js** and **.NET** implementation paths in one project
- Highlights engineering integration across **ASP.NET Core + WebSocket + Azure + Microsoft Foundry**
- Suitable as a project reference for Microsoft MVP, Azure AI, and .NET ecosystem materials

---

## 📦 Installation

```bash
npm install -g @yxai/code
```

---

## 🎮 Usage

After installation, simply run the command to start:

```bash
yxai
```

The program will automatically:
1. Start the server (default port: 6060)
2. Open your browser at http://localhost:6060

### Command Line Options

```bash
# Display help information
yxai --help

# Display version number
yxai --version

# Specify port number
yxai --port 6060
```

### Environment Variables

```bash
# Use environment variable to specify port
PORT=8080 yxai
```

---

## 💻 System Requirements

| Requirement | Version |
|-------------|---------|
| Node.js     | >= 22.0.0 |
| Claude Code | Prerequisite (no configuration needed) |

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

---

## 👥 About Us

This project is self-developed by **YiXin AI Team + Claude + Claude Code** using Vibecoding

- 🌐 Official Website: [https://yxai.chat](https://yxai.chat)
- 💬 Contact Us: You can add our contact information through the YiXin AI official website
- 🐛 Issue Feedback: For tool-related issues, please submit an issue, and YiXin AI Lobster will review and complete your issue within 24 hours~

---

<div align="center">

Made with ❤️ by YxAi Team

</div>
