# Pop Translate

**一款面向 Windows 11 的原生 Fluent 2 桌面划词翻译、截图 OCR 与多模型文本润色工具。**

*A native Windows 11 Fluent 2 desktop application for instant selection translation, screenshot OCR, parallel multi-provider streaming translation, back-translation, and AI text polishing.*

[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20x64-0078D4?logo=windows11)](https://github.com/w366813640/pop_translate)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203%20Fluent%202-0078D4)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](./LICENSE)

---

## 核心特性

- **极速划词取词与无侵入悬浮图标**
  - 优先通过 Windows UI Automation (`IUIAutomation`) 无侵入读取选中文本，自动回退至防剪贴板污染的复制恢复机制。
  - 支持全局快捷键划词翻译、鼠标划词自动弹出悬浮图标，以及进程黑名单与全屏免打扰守护。
- **多引擎并行流式翻译**
  - 内置免配置翻译引擎、DeepL（自动识别 Free / Pro）以及任意 OpenAI 兼容大语言模型（SSE 实时流式输出）。
  - 支持双目标语言智能路由与 PDF/OCR 断行自动合并预处理。
- **文本润色与双向回译校验**
  - 支持通用、学术、商务、口语、精简与自定义 Prompt 等多种润色风格，多模型并行流式对比。
  - 支持一键/自动流式回译与本地词级 Diff 高亮对比，直观核验修改细节。
- **多显示器高 DPI 截图 OCR**
  - 逐显示器冻结画面框选截图，完美适配混合 DPI 多屏环境。
  - 默认支持 Windows 11 原生离线 OCR（`Windows.Media.Ocr`），同时支持切换 OpenAI 兼容视觉大模型 OCR。
- **原生 Windows 11 质感与安全设计**
  - 采用 WinUI 3 Fluent 2 设计语言，支持 Desktop Acrylic 翻译弹窗、Mica 设置窗口、浅色/深色主题跟随与系统托盘常驻。
  - 所有 API 密钥均通过 Windows DPAPI（`DataProtectionScope.CurrentUser`）本地加密存储，配置文件永不写入明文密钥。
  - 内置系统代理、直连、HTTP 及 SOCKS5 代理支持，以及标准自启与免 UAC 提权自启任务管理。

---

## 项目架构

项目采用清晰的四层解耦架构：

```text
PopTranslate.sln
├── src/
│   ├── PopTranslate.Core/     # 纯 .NET 10 领域层：契约接口、数据模型、翻译/润色/OCR 编排、语言路由、本地 Diff、DPAPI 配置存储、代理工厂
│   ├── PopTranslate.Win32/    # Windows 11 原生互操作层：CsWin32 全局热键、UIA 取词、防污染剪贴板、多屏截图覆盖层、Windows OCR、全屏守护与自启服务
│   └── PopTranslate.App/      # WinUI 3 Unpackaged 桌面端：托盘常驻、Acrylic 翻译/润色弹窗、Mica 设置页、MVVM ViewModels
└── tests/
    └── PopTranslate.Tests/    # xUnit 单元测试、契约测试与端到端 DI 集成测试
```

---

## 环境要求

- **操作系统**：Windows 11（`10.0.22000.0` 及以上，推荐 `x64`）
- **开发工具**：[.NET 10 SDK](https://dotnet.microsoft.com/download)
- **运行时**（Debug 模式）：Windows App Runtime 2.7（Release 模式默认启用 `WindowsAppSDKSelfContained=true` 独立便携输出）

---

## 构建与运行

### 1. 克隆仓库

```bash
git clone https://github.com/w366813640/pop_translate.git
cd pop_translate
```

### 2. 编译整个解决方案

```bash
dotnet build PopTranslate.sln -c Debug -p:Platform=x64
```

### 3. 运行测试套件

```bash
dotnet test tests/PopTranslate.Tests/PopTranslate.Tests.csproj -p:Platform=x64
```

### 4. 发布独立便携版本（Release）

```bash
dotnet build src/PopTranslate.App/PopTranslate.App.csproj -c Release -p:Platform=x64
```

编译产物位于 `src/PopTranslate.App/bin/x64/Release/net10.0-windows10.0.26100.0/`，默认自包含 Windows App SDK 运行时。

---

## 隐私与安全说明

- 应用配置默认存储于 `%LOCALAPPDATA%\PopTranslate\settings.json`。
- 用户配置的所有第三方 API 密钥在落盘前均通过 Windows DPAPI 绑定当前用户上下文加密，不会以明文形式保存在本地磁盘或任何日志中。
- 本项目不包含任何遥测或第三方数据收集逻辑；网络请求仅发往用户显式启用的翻译/OCR/大模型服务端点。

---

## 开源协议

本项目基于 [MIT License](./LICENSE) 开源，允许自由使用、修改与商业化分发。
