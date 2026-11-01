> **写在前面**：这个项目是学习 AI 本地部署 / AI 许愿式编程的成果，本人未写一行代码，都是由豆包生成；所有依赖都来自互联网。你从这里下载源代码也无法运行，因为缺少的东西太多了，建议下载 `deploy.ps1` 来一键部署，具体请参阅[「一键部署依赖」](#快速开始)。如果无法运行，请求助 AI。

# SRT 自动字幕生成

把任意视频/音频变成带时间轴的字幕文件，全流程本地运行，开箱即用。
五步管线：**探测音轨 → 提取音频 → 人声分离 → 语音识别 → LLM 翻译/纠错**。

多语言识别与互转，GPU 全程加速，断点续跑，输出 SRT + 纯文本。

```
视频/音频 ──ffprobe──► 音轨列表 ──ffmpeg──► 44.1k wav
                                              │
                                    MDX-Net 人声分离（DirectML GPU，可选）
                                              │
                                silero VAD 切片 ──► whisper.cpp（Vulkan GPU）
                                              │
                                   SRT 原文 ──► LLM 翻译/纠错 ──► 成品字幕
```

## 特性

- **全本地**：不依赖任何在线服务（可选的 OpenAI 兼容接口除外），断网可用
- **GPU 加速**：人声分离走 DirectML，识别走 Vulkan，支持多显卡选择
- **免配置 LLM**：内置 ollama + 2B 翻译模型，开箱即用；也可切系统 Ollama / OpenAI 接口
- **断点续跑**：产物文件存在即视为该步完成，中断后重开自动跳过
- **任意语言**：识别语言与目标语言都能手输语言码（`zh`/`en`/`ja`/`es`/…）
- **字幕直翻**：输入现成 `.srt`，跳过识别直接翻译
- **双击即用**：单个 `srt_gui.exe`，无需安装运行时（自带 .NET Fx 4.8 依赖的 DLL）

## 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10/11 64 位 |
| 运行时 | .NET Framework 4.8（Win10 1809+ 自带） |
| 显卡 | 任意 Vulkan 1.1+ 显卡（NVIDIA/AMD/Intel 集显均可）；无则自动回退 CPU |
| 磁盘 | 约 6 GB（模型占绝大部分） |
| 网络 | 部署阶段需要，之后完全离线 |

## 快速开始

**1. 取代码**

```bash
git clone https://github.com/luxxjz/dialogue-to-subtitle.git SRTproject
cd SRTproject
```

**2. 一键部署依赖**

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1
```

脚本会依次下载并还原 ffmpeg、whisper-cli、ollama、各模型与托管 DLL，最后编译出
`srt_gui.exe`。约 6 GB 下载量，中断后重跑会**接着下**，已就位的组件自动跳过。

大文件（ffmpeg 包、whisper 模型、1.9 GB 的 GGUF）走**多线程分段下载**：切成 6 MB 一段、
最多 24 路并发。gyan.dev 和 GitHub Release 单连接只给 10~20 KB/s，分段后实测能到 250 KB/s
以上（70 MB 的 ffmpeg 包从约 2 小时缩到 6 分钟）。用的是系统自带 `curl.exe`（Win10 1803+
起有），站点不支持 Range 或 curl 不可用时自动退回单连接下载。

国内网络建议：

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1 -Mirror cn
```

**3. 启动**

双击 `srt_gui.exe`。首次使用：选文件 → 点"解析" → 点"开始处理"。

详细操作见 [README.txt](README.txt)（完整使用说明）。

## deploy.ps1 参数

| 参数 | 说明 |
|---|---|
| `-Mirror auto\|global\|cn` | 模型下载源。`auto` 先官方、失败自动切 hf-mirror；`cn` 优先镜像 |
| `-Only lib,models` | 只部署指定组件：`lib` `ffmpeg` `ollama` `whisper` `models` `llm` `build` |
| `-Skip ffmpeg,llm` | 跳过指定组件 |
| `-SkipLlm` | 不装内置翻译模型（省 1.9 GB，只影响"内嵌引擎"档） |
| `-SmallModel` | 用 `ggml-small` 代替 `ggml-medium`（省 1 GB，精度略降） |
| `-GgufPath D:\x.gguf` | 用本地 GGUF 建内置模型，不下载基座 |
| `-NoBuild` | 只下依赖，不编译 |
| `-Force` | 忽略已存在文件，强制重下 |

常见组合：

```powershell
# 只是想重新编译（源码改过）
.\deploy.ps1 -Only build

# 网络差, 分批下
.\deploy.ps1 -Only lib,ffmpeg,ollama,whisper
.\deploy.ps1 -Only models,llm
```

## 目录结构

```
SRTproject/
├── srt_gui.exe              ← 编译产物（不纳入仓库）
├── config.txt               ← 本地配置（自动生成，不纳入仓库）
├── config.example.txt       ← 配置样例
├── deploy.ps1               ← 一键部署脚本
├── FILES.md                 ← 完整文件树（144 个文件，逐个标注来源）
├── gui/                     ← C# 源码（WinForms，单文件编译）
│   ├── Program.cs  MainForm.cs  Pipeline.cs
│   ├── Recognize.cs  MDXSep.cs  LlmPost.cs  LlmServer.cs
│   ├── Config.cs  SrtUtil.cs  VkUtil.cs  ProcRunner.cs
│   └── build.ps1            ← csc 一键编译
├── modelfile/               ← 内置 LLM 的 ollama 模型定义
├── manifests/tree.txt       ← 机读版清单（路径/字节数/来源/必需性）
├── scripts/
│   ├── gen-tree.ps1         ← 扫描目录，重新生成 FILES.md 与 manifests/tree.txt
│   └── check-tree.ps1       ← 按清单核对当前目录缺了什么
├── tools/                   ← 运行时依赖（由 deploy.ps1 还原）
│   ├── TSCharacters.txt     ← 繁简单字映射（唯一入库的运行时数据）
│   ├── ffmpeg/bin/  ollama/  whisper-vulkan-new/  lib/
└── models/                  ← 模型（由 deploy.ps1 还原）
    ├── ggml-medium.bin  ggml-silero-v6.2.0.bin
    ├── UVR-MDX-NET-Inst_HQ_3.onnx  Kim_Vocal_2.onnx
    └── ollama/               ← 内置模型 blob
```

**不确定装全了没有？** 逐个核对每个文件（含大小、来源、是否必需）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-tree.ps1
```

缺失项会按「必需 / 可选」分开列出，必需项缺失时退出码为 1。清单的完整版是 [FILES.md](FILES.md)。

## 从源码构建

```powershell
# 前置: deploy.ps1 -Only lib  还原编译所需的托管 DLL
powershell -ExecutionPolicy Bypass -File .\gui\build.ps1
```

`build.ps1` 调用系统自带的 `csc.exe`（`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`），
无需安装 Visual Studio。源码必须先转 UTF-8 BOM，否则 csc 按 ANSI 读中文会乱码 —— 脚本已处理。

## 依赖来源

仓库内**不含任何第三方二进制**，全部由部署脚本从上游拉取。完整清单（含版本与许可）见
[ATTRIBUTIONS.md](ATTRIBUTIONS.md)。

要点：

- FFmpeg 取 [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) 的 `ffmpeg-git-full`（与本项目原有
  `tools\ffmpeg` 同源同配置，含 `presets/`），是 `.7z`：脚本用系统自带 `tar.exe`（Win10 1803+ 支持 7z）
  或你装的 7-Zip 解开；都不可用时回退 BtbN 的 zip 构建（功能等价，少了 `presets/`）。FFmpeg 为
  **GPL v3**，再分发本程序 + FFmpeg 时需遵守 GPL 义务
- whisper.cpp 官方 Release **没有 Windows Vulkan 预编译包**，本仓库把自编译结果作为
  Release 附件分发；取不到时自动回退官方 CPU 构建（`tools/README.md` 有自编译步骤）

## 内置翻译模型

内置引擎用的是 [kaelri/qwen3.5-mt:2b](https://ollama.com/kaelri/qwen3.5-mt) 的原始权重：
Qwen3.5 2B 的翻译专用微调版，Q4_K_M，1.9 GB，Apache 2.0。

`deploy.ps1` 直接从 ollama 注册表取这个 GGUF 层（`sha256:4ecac2ba…`），核对摘要后再按
`modelfile/` 里的模板重建 `qwen3.5-mt:2b`。取到的权重与作者发布版**逐字节一致**，因此部署
结果和原作者环境下的翻译表现相同；若从 HuggingFace 回退，脚本会明确提示"这是通用基座"。

该模型经 abliteration 移除了拒答，会逐字翻译、不回避内容，输出请自行复核。

## 常见问题

**下载卡住 / 失败**
换镜像重跑：`-Mirror cn`。脚本支持断点续传，反复重跑不会从头开始。

**提示找不到 Vulkan 设备**
显卡驱动太旧。更新显卡驱动；或改用 CPU（能跑，一个 2 小时片子会从 ~1 小时变成数小时）。

**端口冲突 / ollama 启动失败**
内置引擎用 `127.0.0.1:39281`（见 `config.txt` 的 `LlmPort`）。若本机已装 Ollama 托盘程序，
先退出它，或把 `LlmType` 改成 `ollama` 并填 `LlmUrl=http://127.0.0.1:11434` 直接用系统 Ollama。

**杀毒软件报毒**
`deploy.ps1` 会编译生成 exe 并写文件，可能触发启发式告警。脚本内容全部可读，可先审阅再运行。

**config.txt 要不要提交**
不要。它含本机路径与片源文件名，已在 `.gitignore` 中。程序没有它也能跑（内置默认值）。

## 许可

代码 MIT，见 [LICENSE](LICENSE)。第三方组件各自遵循其许可，见 [ATTRIBUTIONS.md](ATTRIBUTIONS.md)。
