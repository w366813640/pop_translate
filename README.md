# Pop Translate

**一款面向 Windows 11 的原生 Fluent 2 桌面划词翻译、截图 OCR 与多模型文本润色工具。**

*A native Windows 11 Fluent 2 desktop application for instant selection translation, screenshot OCR, parallel multi-provider streaming translation, back-translation, and AI text polishing.*

[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20x64-0078D4?logo=windows11)](https://github.com/w366813640/pop_translate)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![WinUI 3](https://img.shields.io/badge/UI-WinUI%203%20Fluent%202-0078D4)](https://learn.microsoft.com/windows/apps/winui/winui3/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](./LICENSE)

---

## 核心特性

- **极速无侵入划词与 `译` 悬浮图标**
  - 优先通过 Windows UI Automation (`IUIAutomation`) 零侵入读取选中文本，按需回退至防历史/云端污染的剪贴板快照恢复机制。
  - 鼠标划词弹出无焦点抢占 `译` 悬浮图标，点击即开统一单页弹窗，右键支持一键屏蔽当前应用或暂停划词 30 分钟；同时支持全局快捷键、可选按住 `Ctrl` 划词、全屏免打扰与双击 `Ctrl+C+C` 触发翻译。
- **多引擎并行流式翻译、词典分流与代码拆词**
  - 内置零配置免费翻译（Google 多节点 + Bing 自动故障转移）、DeepL（自动识别 Free / Pro 及配额检测）与任意 OpenAI 兼容大模型（SSE 实时流式输出，自动剥离推理模型 `<think>` 思维链）。
  - 内置主流服务商一键预设（OpenAI、DeepSeek、Gemini、SiliconFlow、Ollama 本地免密钥、OpenRouter、通义千问、智谱 GLM）及 `/models` 可用模型列表自动拉取。
  - 支持双目标语言智能路由、单词/短语词典模式自动分流（音标、词性释义、词形变化、双语例句）、开发者 `camelCase` / `snake_case` 标识符自动拆词、自定义术语表（Glossary）注入与 PDF/OCR 智能断行合并。
- **统一单页专属 AI 润色卡片、一键回填替换与语音朗读**
  - 翻译与润色统一呈现在同一 Fluent 2 单页弹窗内：支持配置专属 AI 润色服务卡片（可选自动并发润色或零 Token 消耗的按需点击润色），涵盖通用流畅、学术正式、商务邮件、口语自然、精简凝练、代码命名（`CodeNaming` 四种命名规范生成）与自定义 Prompt 共 7 种可卡片内实时切换的润色风格。
  - 支持**直接替换翻译**（选中输入框内文字后按 `Alt+R`、右键悬浮 `译` 图标或执行 `PopTranslate.exe replace`，静默翻译并原地替换为译文）与弹窗内**一键回填替换原文**（卡片「回填替换原文」按钮或 `Ctrl+Shift+Enter`）、一键/自动流式回译核对、本地词级 Myers Diff 高亮对比，以及原文/译文系统原生离线/自然语音 TTS 朗读。
- **多显示器高 DPI 截图翻译与静默截图 OCR**
  - 逐显示器高帧率冻结画面框选，完美适配混合 DPI 多屏环境；支持常规截图翻译（`Alt+W`）与**静默截图 OCR**（`Alt+Shift+W` 或截图时按住 `Ctrl` / `Ctrl+C` 直接识别文字写入剪贴板，不弹窗）。
  - 默认内置 Windows 11 原生离线 OCR（`Windows.Media.Ocr` 零拷贝直通），并支持一键切换视觉大模型 OCR 的**纯文本 / Markdown 表格提取 / LaTeX 公式识别**三种专业模式（失败自动回退本地 OCR）及弹窗内缩略图重识别。
- **原生 Windows 11 质感、便携模式与外部 CLI 联动**
  - 采用 WinUI 3 Fluent 2 设计语言，支持 Desktop Acrylic 翻译弹窗、Mica 设置窗口、浅色/深色主题跟随与系统托盘常驻（含临时剪贴板监听开关）。
  - 支持**便携模式（Portable Mode）**（在程序目录新建 `portable` 文件即可将配置与脱敏运行日志收敛至 `./data/`）与单实例命名管道 **CLI 参数调用**（无缝对接 Flow Launcher、PowerToys Run、Raycast、AutoHotkey 等工具）。
  - 所有 API 密钥与代理密码均经由 Windows DPAPI（`DataProtectionScope.CurrentUser`）加密落盘；内置系统代理、直连、HTTP 及 SOCKS5 代理，以及标准 HKCU 自启与任务计划程序免 UAC 管理员提权自启。

---

## 项目架构

项目采用清晰的四层解耦架构：

```text
PopTranslate.sln
├── src/
│   ├── PopTranslate.Core/     # 纯 .NET 10 领域层：契约接口、数据模型、翻译/润色/词典/OCR 编排、模型发现、本地 Diff、DPAPI 配置与代理工厂
│   ├── PopTranslate.Win32/    # Windows 11 原生互操作层：CsWin32 全局热键、UIA 取词、防污染剪贴板、原文回填、原生 TTS、多屏截图、Windows OCR 与提权自启
│   └── PopTranslate.App/      # WinUI 3 Unpackaged 桌面端：单实例 CLI 管道、托盘常驻、轻量悬浮图标、Fluent 2 统一单页弹窗、Mica 设置窗口
└── tests/
    └── PopTranslate.Tests/    # xUnit 单元测试、契约测试与端到端统一升级集成测试
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

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-release.ps1
# 或直接执行：dotnet build src/PopTranslate.App/PopTranslate.App.csproj -c Release -p:Platform=x64
```

构建完成后会自动生成清爽规整的独立发布包 `release/PopTranslate-win-x64/`（已剥离编译期 `.winmd` 元数据、调试符号与冗余多语种资源目录，并启用 Windows App SDK 自包含与 ReadyToRun 预编译）：

```text
release/PopTranslate-win-x64/
├── PopTranslate.exe   # 纯净主程序入口（转发全部 CLI 参数，零根目录 DLL 杂乱）
├── README.md          # 使用说明
├── LICENSE            # 开源协议
└── runtime/           # 精简后的自包含运行时与静态资源子目录
```

---

## 便携模式与 CLI 参数调用

- **便携模式（Portable Mode）**：在 `PopTranslate.exe` 所在根目录创建空文件 `portable`，配置与滚动脱敏日志将自动存储于同级 `./data/settings.json` 与 `./data/logs/app.log`。
- **单实例 CLI 外部触发**：运行中再次通过命令行调用可执行文件时，参数将经由当前用户命名管道转发给常驻实例：

```powershell
PopTranslate.exe translate              # 触发当前选中文本翻译
PopTranslate.exe replace                # 静默翻译当前选中文本并原地替换到输入框
PopTranslate.exe translate "Hello"      # 直接弹出并翻译指定文本
PopTranslate.exe polish "draft text"    # 打开统一单页弹窗并立即触发 AI 润色卡片
PopTranslate.exe ocr                    # 触发截图翻译
PopTranslate.exe silent-ocr             # 触发静默截图 OCR（直接写入剪贴板）
PopTranslate.exe input                  # 打开输入翻译弹窗并聚焦输入框
PopTranslate.exe settings Providers     # 打开设置窗口并跳转指定标签页
PopTranslate.exe exit                   # 保存配置并完全退出常驻进程
PopTranslate.exe --minimized            # 静默启动并常驻系统托盘
```

---

## 隐私与安全说明

- 应用配置默认独立存储于系统用户配置目录 `%APPDATA%\PopTranslate\settings.json`（与程序发布包彻底解耦，版本覆盖升级绝不丢失配置；便携模式下为 `./data/settings.json`，发布脚本更新时亦保留 `data/` 目录），并在每次写入前自动维护 `settings.json.bak` 滚动备份；脱敏运行日志存储于 `%LOCALAPPDATA%\PopTranslate\logs\app.log`。
- 用户配置的所有第三方 API 密钥与代理密码在落盘前均通过 Windows DPAPI 绑定当前用户上下文加密，脱敏诊断日志绝不记录用户原文、译文或明文凭证。
- 本项目不包含任何遥测或第三方数据收集逻辑；网络请求仅发往用户显式启用的翻译/OCR/大模型服务端点。

---

## 开源协议

本项目基于 [MIT License](./LICENSE) 开源，允许自由使用、修改与商业化分发。
