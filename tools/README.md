# tools/ —— 运行时依赖目录

本目录下的二进制**不纳入版本库**（体积达 GB 级，且多为第三方分发物）。
仓库里只保留纯文本数据文件，其余由 `deploy.ps1` 一键还原。

```
tools/
├── TSCharacters.txt          ← 纳入仓库（OpenCC 繁简单字映射，4148 字）
├── ffmpeg/
│   └── bin/{ffmpeg,ffprobe,ffplay}.exe      ← deploy.ps1 下载（BtbN GPL 构建）
├── ollama/
│   ├── ollama.exe                            ← deploy.ps1 下载（v0.35.1）
│   └── lib/ollama/…                          ← 同上
├── whisper-vulkan-new/
│   └── whisper-cli.exe + ggml-*.dll + whisper.dll
│                                             ← 仓库 Release 附件（Vulkan 自编译）
├── whisper-vulkan/                           ← 可选：旧版 Vulkan 构建
└── lib/                                      ← deploy.ps1 从 NuGet 还原
    ├── Microsoft.ML.OnnxRuntime.dll          (Microsoft.ML.OnnxRuntime.Managed 1.24.4)
    ├── onnxruntime.dll                       (Microsoft.ML.OnnxRuntime.DirectML 1.24.4)
    ├── DirectML.dll                          (同上)
    ├── onnxruntime_providers_shared.dll      (同上)
    ├── MathNet.Numerics.dll                  (MathNet.Numerics 4.15.0)
    ├── System.Memory.dll                     (System.Memory 4.5.5)
    ├── System.Runtime.CompilerServices.Unsafe.dll  (4.5.3)
    └── msvcp140*.dll / vcruntime140*.dll     (取自系统 VC++ 运行库)
```

## 关于 whisper-cli 的 GPU 加速

程序通过 Vulkan 使用 GPU（`gui/VkUtil.cs` 枚举 Vulkan 设备）。
whisper.cpp 官方 Release **不提供 Windows Vulkan 预编译包**，因此本仓库把自编译结果
作为 Release 附件分发；拉不到时会自动退回官方 CPU 构建（能跑，但慢很多）。

想自己编译（需要 VS 2022 + Vulkan SDK）：

```powershell
git clone https://github.com/ggml-org/whisper.cpp
cd whisper.cpp
cmake -B build -DGGML_VULKAN=ON
cmake --build build --config Release -j
# 产物在 build\bin\Release\，把 whisper-cli.exe、whisper.dll、ggml-*.dll 拷到
# SRTproject\tools\whisper-vulkan-new\ 即可
```
