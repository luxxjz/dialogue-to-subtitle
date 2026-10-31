# 完整文件树

安装完成后，项目目录下应当有 **144 个文件**，合计 **5.81 GB**。本文件由 `scripts/gen-tree.ps1` 扫描实际目录生成，不是手写的。

判定标准：拿本表逐条比对，或直接运行部署脚本自带的完整性检查。

## 组成概览

| 来源 | 文件数 | 体积 | 由谁产生 |
|---|---:|---:|---|
| 模型站点 | 5 | 2.01 GB | deploy.ps1 → HuggingFace / whisper.cpp |
| ollama create | 7 | 1.81 GB | deploy.ps1 → ollama create |
| ollama 安装包 | 52 | 1.19 GB | deploy.ps1 → ollama/ollama v0.35.1 |
| BtbN FFmpeg | 4 | 630.5 MB | deploy.ps1 → BtbN/FFmpeg-Builds |
| 旧版(不还原) | 6 | 60.6 MB | 本项目未使用，可不还原 |
| 本仓库 Release | 6 | 55.5 MB | deploy.ps1 → 本仓库 Release v1.0.0 |
| build.ps1 | 13 | 37.3 MB | gui/build.ps1 编译 |
| NuGet 包 | 7 | 36.2 MB | deploy.ps1 → nuget.org |
| 仓库自带 | 31 | 744.1 KB | git clone |
| System32 | 4 | 701.0 KB | deploy.ps1 → 本机 System32 |
| 上游包附带 | 6 | 46.4 KB | 本机 ffmpeg 包自带，BtbN 包无此文件 |
| 本机私有 | 2 | 717 B | 你自己创建 |
| 残留(可删) | 1 | 15 B | 建议删除 |
| **合计** | **144** | **5.81 GB** | |

## 目录树

```
SRTproject/
├── gui/  (15 个文件)
│   ├── _icon/  (3 个文件)
│   │   ├── gen_icons.cs
│   │   ├── gen_icons.exe
│   │   └── srt.ico
│   ├── build.ps1
│   ├── Config.cs
│   ├── LlmPost.cs
│   ├── LlmServer.cs
│   ├── MainForm.cs
│   ├── MDXSep.cs
│   ├── Pipeline.cs
│   ├── ProcRunner.cs
│   ├── Program.cs
│   ├── Recognize.cs
│   ├── SrtUtil.cs
│   └── VkUtil.cs
├── manifests/  (1 个文件)
│   └── tree.txt
├── modelfile/  (1 个文件)
│   └── qwen3.5-mt-2b.Modelfile
├── models/  (13 个文件)
│   ├── ollama/  (7 个文件)
│   │   ├── blobs/  (5 个文件)
│   │   │   ├── sha256-4ecac2ba3fd000cf4638ce65dbffacbd8062c2c87a24a75559af421341adb4b2
│   │   │   ├── sha256-67230532416855bd5a50613d31f9f4f424024efc73752037bd23b29e4359e170
│   │   │   ├── sha256-b7dbc67611824c793e90607ac3cd9a498aaf25c57cc2499347ff68d65a33d58d
│   │   │   ├── sha256-be861f9d6d1594da0770680fae1bc186f76f859caad67ffe23205fee105e232b
│   │   │   └── sha256-dcfb4f067e3b8af32240fa708334f0cb8857a11397b13673deefb77a579bd161
│   │   ├── manifests/  (1 个文件)
│   │   │   └── registry.ollama.ai/  (1 个文件)
│   │   │       └── library/  (1 个文件)
│   │   │           └── qwen3.5-mt/  (1 个文件)
│   │   │               └── 2b
│   │   └── metadata/  (1 个文件)
│   │       └── sha256-4ecac2ba3fd000cf4638ce65dbffacbd8062c2c87a24a75559af421341adb4b2.json
│   ├── ggml-medium.bin
│   ├── ggml-silero-v6.2.0.bin
│   ├── ggml-small.bin
│   ├── ggml-vad.bin
│   ├── Kim_Vocal_2.onnx
│   └── UVR-MDX-NET-Inst_HQ_3.onnx
├── scripts/  (2 个文件)
│   ├── check-tree.ps1
│   └── gen-tree.ps1
├── tools/  (89 个文件)
│   ├── ffmpeg/  (10 个文件)
│   │   ├── bin/  (3 个文件)
│   │   │   ├── ffmpeg.exe
│   │   │   ├── ffplay.exe
│   │   │   └── ffprobe.exe
│   │   ├── presets/  (5 个文件)
│   │   │   ├── libvpx-1080p.ffpreset
│   │   │   ├── libvpx-1080p50_60.ffpreset
│   │   │   ├── libvpx-360p.ffpreset
│   │   │   ├── libvpx-720p.ffpreset
│   │   │   └── libvpx-720p50_60.ffpreset
│   │   ├── LICENSE
│   │   └── README.txt
│   ├── lib/  (11 个文件)
│   │   ├── DirectML.dll
│   │   ├── MathNet.Numerics.dll
│   │   ├── Microsoft.ML.OnnxRuntime.dll
│   │   ├── msvcp140.dll
│   │   ├── msvcp140_1.dll
│   │   ├── onnxruntime.dll
│   │   ├── onnxruntime_providers_shared.dll
│   │   ├── System.Memory.dll
│   │   ├── System.Runtime.CompilerServices.Unsafe.dll
│   │   ├── vcruntime140.dll
│   │   └── vcruntime140_1.dll
│   ├── ollama/  (53 个文件)
│   │   ├── lib/  (51 个文件)
│   │   │   └── ollama/  (51 个文件)
│   │   │       ├── cuda_v12/  (14 个文件)
│   │   │       │   ├── concrt140.dll
│   │   │       │   ├── cublas64_12.dll
│   │   │       │   ├── cublasLt64_12.dll
│   │   │       │   ├── cudart64_12.dll
│   │   │       │   ├── ggml-cuda.dll
│   │   │       │   ├── msvcp140.dll
│   │   │       │   ├── msvcp140_1.dll
│   │   │       │   ├── msvcp140_2.dll
│   │   │       │   ├── msvcp140_atomic_wait.dll
│   │   │       │   ├── msvcp140_codecvt_ids.dll
│   │   │       │   ├── vccorlib140.dll
│   │   │       │   ├── vcruntime140.dll
│   │   │       │   ├── vcruntime140_1.dll
│   │   │       │   └── vcruntime140_threads.dll
│   │   │       ├── vulkan/  (11 个文件)
│   │   │       │   ├── concrt140.dll
│   │   │       │   ├── ggml-vulkan.dll
│   │   │       │   ├── msvcp140.dll
│   │   │       │   ├── msvcp140_1.dll
│   │   │       │   ├── msvcp140_2.dll
│   │   │       │   ├── msvcp140_atomic_wait.dll
│   │   │       │   ├── msvcp140_codecvt_ids.dll
│   │   │       │   ├── vccorlib140.dll
│   │   │       │   ├── vcruntime140.dll
│   │   │       │   ├── vcruntime140_1.dll
│   │   │       │   └── vcruntime140_threads.dll
│   │   │       ├── ggml.dll
│   │   │       ├── ggml-base.dll
│   │   │       ├── ggml-cpu-alderlake.dll
│   │   │       ├── ggml-cpu-cannonlake.dll
│   │   │       ├── ggml-cpu-cascadelake.dll
│   │   │       ├── ggml-cpu-cooperlake.dll
│   │   │       ├── ggml-cpu-haswell.dll
│   │   │       ├── ggml-cpu-icelake.dll
│   │   │       ├── ggml-cpu-ivybridge.dll
│   │   │       ├── ggml-cpu-piledriver.dll
│   │   │       ├── ggml-cpu-sandybridge.dll
│   │   │       ├── ggml-cpu-sapphirerapids.dll
│   │   │       ├── ggml-cpu-skylakex.dll
│   │   │       ├── ggml-cpu-sse42.dll
│   │   │       ├── ggml-cpu-x64.dll
│   │   │       ├── ggml-cpu-zen4.dll
│   │   │       ├── libc++.dll
│   │   │       ├── libllama.dll
│   │   │       ├── libllama-common.dll
│   │   │       ├── libllama-quantize-impl.dll
│   │   │       ├── libllama-server-impl.dll
│   │   │       ├── libmtmd.dll
│   │   │       ├── libomp.dll
│   │   │       ├── libunwind.dll
│   │   │       ├── libwinpthread-1.dll
│   │   │       └── llama-server.exe
│   │   ├── ollama.exe
│   │   └── qwen35mt.jinja
│   ├── whisper-vulkan/  (6 个文件)
│   │   ├── ggml.dll
│   │   ├── ggml-base.dll
│   │   ├── ggml-cpu.dll
│   │   ├── ggml-vulkan.dll
│   │   ├── whisper.dll
│   │   └── whisper-cli.exe
│   ├── whisper-vulkan-new/  (6 个文件)
│   │   ├── ggml.dll
│   │   ├── ggml-base.dll
│   │   ├── ggml-cpu.dll
│   │   ├── ggml-vulkan.dll
│   │   ├── whisper.dll
│   │   └── whisper-cli.exe
│   ├── note.txt
│   ├── README.md
│   └── TSCharacters.txt
├── .gitattributes
├── .gitignore
├── ATTRIBUTIONS.md
├── config.example.txt
├── config.txt
├── deploy.ps1
├── DirectML.dll
├── FILES.md
├── LICENSE
├── MathNet.Numerics.dll
├── Microsoft.ML.OnnxRuntime.dll
├── msvcp140.dll
├── msvcp140_1.dll
├── onnxruntime.dll
├── onnxruntime_providers_shared.dll
├── README.md
├── README.txt
├── srt_gui.exe
├── System.Memory.dll
├── System.Runtime.CompilerServices.Unsafe.dll
├── vcruntime140.dll
├── vcruntime140_1.dll
└── 本地ollama.txt
```

## 一、仓库自带（clone 即有，无需下载）

共 29 个文件，712.7 KB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `.gitattributes` | 216 B | 仓库自带 | 仓库 |
| `.gitignore` | 1.3 KB | 仓库自带 | 仓库 |
| `ATTRIBUTIONS.md` | 2.6 KB | 仓库自带 | 仓库 |
| `config.example.txt` | 907 B | 仓库自带 | 仓库 |
| `deploy.ps1` | 38.8 KB | 仓库自带 | 仓库 |
| `gui/_icon/gen_icons.cs` | 11.7 KB | 仓库自带 | 仓库 |
| `gui/_icon/srt.ico` | 278.8 KB | 仓库自带 | 仓库 |
| `gui/build.ps1` | 2.4 KB | 仓库自带 | 仓库 |
| `gui/Config.cs` | 9.0 KB | 仓库自带 | 仓库 |
| `gui/LlmPost.cs` | 35.0 KB | 仓库自带 | 仓库 |
| `gui/LlmServer.cs` | 9.2 KB | 仓库自带 | 仓库 |
| `gui/MainForm.cs` | 52.1 KB | 仓库自带 | 仓库 |
| `gui/MDXSep.cs` | 25.0 KB | 仓库自带 | 仓库 |
| `gui/Pipeline.cs` | 31.7 KB | 仓库自带 | 仓库 |
| `gui/ProcRunner.cs` | 4.4 KB | 仓库自带 | 仓库 |
| `gui/Program.cs` | 567 B | 仓库自带 | 仓库 |
| `gui/Recognize.cs` | 34.7 KB | 仓库自带 | 仓库 |
| `gui/SrtUtil.cs` | 16.4 KB | 仓库自带 | 仓库 |
| `gui/VkUtil.cs` | 4.4 KB | 仓库自带 | 仓库 |
| `LICENSE` | 1.0 KB | 仓库自带 | 仓库 |
| `modelfile/qwen3.5-mt-2b.Modelfile` | 1.6 KB | 仓库自带 | 仓库 |
| `README.md` | 7.7 KB | 仓库自带 | 仓库 |
| `README.txt` | 17.2 KB | 仓库自带 | 仓库 |
| `scripts/check-tree.ps1` | 5.1 KB | 仓库自带 | 仓库 |
| `scripts/gen-tree.ps1` | 16.4 KB | 仓库自带 | 仓库 |
| `tools/note.txt` | 151 B | 仓库自带 | 仓库 |
| `tools/ollama/qwen35mt.jinja` | 247 B | 仓库自带 | 可选 |
| `tools/README.md` | 2.2 KB | 仓库自带 | 仓库 |
| `tools/TSCharacters.txt` | 102.1 KB | 仓库自带 | 必需 |

## 二、文档与清单（clone 即有）

共 2 个文件，31.4 KB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `FILES.md` | — | 仓库自带 | 文档 |
| `manifests/tree.txt` | 8.2 KB | 仓库自带 | 文档 |

## 三、第三方运行时（deploy.ps1 下载）

共 62 个文件，1.86 GB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `tools/ffmpeg/bin/ffmpeg.exe` | 209.7 MB | BtbN FFmpeg | 必需 |
| `tools/ffmpeg/bin/ffplay.exe` | 211.2 MB | BtbN FFmpeg | 必需 |
| `tools/ffmpeg/bin/ffprobe.exe` | 209.5 MB | BtbN FFmpeg | 必需 |
| `tools/ffmpeg/LICENSE` | 34.3 KB | BtbN FFmpeg | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/concrt140.dll` | 304.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/cublas64_12.dll` | 108.5 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/cublasLt64_12.dll` | 660.4 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/cudart64_12.dll` | 568.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/ggml-cuda.dll` | 339.7 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/msvcp140.dll` | 532.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/msvcp140_1.dll` | 22.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/msvcp140_2.dll` | 261.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/msvcp140_atomic_wait.dll` | 36.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/msvcp140_codecvt_ids.dll` | 18.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/vccorlib140.dll` | 331.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/vcruntime140.dll` | 109.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/vcruntime140_1.dll` | 36.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/cuda_v12/vcruntime140_threads.dll` | 25.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml.dll` | 50.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-base.dll` | 821.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-alderlake.dll` | 1.7 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-cannonlake.dll` | 1.9 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-cascadelake.dll` | 1.9 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-cooperlake.dll` | 1.9 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-haswell.dll` | 1.7 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-icelake.dll` | 1.9 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-ivybridge.dll` | 1.6 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-piledriver.dll` | 1.6 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-sandybridge.dll` | 1.5 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-sapphirerapids.dll` | 2.2 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-skylakex.dll` | 1.9 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-sse42.dll` | 1.2 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-x64.dll` | 1.2 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/ggml-cpu-zen4.dll` | 1.9 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libc++.dll` | 1.7 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libllama.dll` | 3.3 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libllama-common.dll` | 4.0 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libllama-quantize-impl.dll` | 81.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libllama-server-impl.dll` | 2.5 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libmtmd.dll` | 1.4 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libomp.dll` | 942.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libunwind.dll` | 105.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/libwinpthread-1.dll` | 380.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/llama-server.exe` | 22.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/concrt140.dll` | 304.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/ggml-vulkan.dll` | 41.6 MB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/msvcp140.dll` | 532.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/msvcp140_1.dll` | 22.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/msvcp140_2.dll` | 261.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/msvcp140_atomic_wait.dll` | 36.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/msvcp140_codecvt_ids.dll` | 18.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/vccorlib140.dll` | 331.9 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/vcruntime140.dll` | 109.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/vcruntime140_1.dll` | 36.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/lib/ollama/vulkan/vcruntime140_threads.dll` | 25.4 KB | ollama 安装包 | 必需 |
| `tools/ollama/ollama.exe` | 25.4 MB | ollama 安装包 | 必需 |
| `tools/whisper-vulkan-new/ggml.dll` | 60.5 KB | 本仓库 Release | 必需 |
| `tools/whisper-vulkan-new/ggml-base.dll` | 656.0 KB | 本仓库 Release | 必需 |
| `tools/whisper-vulkan-new/ggml-cpu.dll` | 833.5 KB | 本仓库 Release | 必需 |
| `tools/whisper-vulkan-new/ggml-vulkan.dll` | 53.0 MB | 本仓库 Release | 必需 |
| `tools/whisper-vulkan-new/whisper.dll` | 476.5 KB | 本仓库 Release | 必需 |
| `tools/whisper-vulkan-new/whisper-cli.exe` | 478.5 KB | 本仓库 Release | 必需 |

## 四、托管 DLL 与 VC++ 运行库

共 11 个文件，36.9 MB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `tools/lib/DirectML.dll` | 17.7 MB | NuGet 包 | 必需 |
| `tools/lib/MathNet.Numerics.dll` | 1.6 MB | NuGet 包 | 必需 |
| `tools/lib/Microsoft.ML.OnnxRuntime.dll` | 223.0 KB | NuGet 包 | 必需 |
| `tools/lib/msvcp140.dll` | 532.4 KB | System32 | 必需 |
| `tools/lib/msvcp140_1.dll` | 22.9 KB | System32 | 必需 |
| `tools/lib/onnxruntime.dll` | 16.5 MB | NuGet 包 | 必需 |
| `tools/lib/onnxruntime_providers_shared.dll` | 21.5 KB | NuGet 包 | 必需 |
| `tools/lib/System.Memory.dll` | 138.9 KB | NuGet 包 | 必需 |
| `tools/lib/System.Runtime.CompilerServices.Unsafe.dll` | 16.4 KB | NuGet 包 | 必需 |
| `tools/lib/vcruntime140.dll` | 109.4 KB | System32 | 必需 |
| `tools/lib/vcruntime140_1.dll` | 36.4 KB | System32 | 必需 |

## 五、模型文件（体积主体）

共 12 个文件，3.82 GB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `models/ggml-medium.bin` | 1.43 GB | 模型站点 | 必需 |
| `models/ggml-silero-v6.2.0.bin` | 864.4 KB | 模型站点 | 必需 |
| `models/ggml-small.bin` | 465.0 MB | 模型站点 | 必需 |
| `models/Kim_Vocal_2.onnx` | 63.7 MB | 模型站点 | 必需 |
| `models/ollama/blobs/sha256-4ecac2ba3fd000cf4638ce65dbffacbd8062c2c87a24a75559af421341adb4b2` | 1.81 GB | ollama create | 必需 |
| `models/ollama/blobs/sha256-67230532416855bd5a50613d31f9f4f424024efc73752037bd23b29e4359e170` | 437 B | ollama create | 必需 |
| `models/ollama/blobs/sha256-b7dbc67611824c793e90607ac3cd9a498aaf25c57cc2499347ff68d65a33d58d` | 117 B | ollama create | 必需 |
| `models/ollama/blobs/sha256-be861f9d6d1594da0770680fae1bc186f76f859caad67ffe23205fee105e232b` | 141 B | ollama create | 必需 |
| `models/ollama/blobs/sha256-dcfb4f067e3b8af32240fa708334f0cb8857a11397b13673deefb77a579bd161` | 247 B | ollama create | 必需 |
| `models/ollama/manifests/registry.ollama.ai/library/qwen3.5-mt/2b` | 891 B | ollama create | 必需 |
| `models/ollama/metadata/sha256-4ecac2ba3fd000cf4638ce65dbffacbd8062c2c87a24a75559af421341adb4b2.json` | 10.2 KB | ollama create | 可选 |
| `models/UVR-MDX-NET-Inst_HQ_3.onnx` | 63.7 MB | 模型站点 | 必需 |

## 六、构建产物（编译后出现）

共 13 个文件，37.3 MB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `DirectML.dll` | 17.7 MB | build.ps1 | 构建 |
| `gui/_icon/gen_icons.exe` | 11.0 KB | build.ps1 | 可选 |
| `MathNet.Numerics.dll` | 1.6 MB | build.ps1 | 构建 |
| `Microsoft.ML.OnnxRuntime.dll` | 223.0 KB | build.ps1 | 构建 |
| `msvcp140.dll` | 532.4 KB | build.ps1 | 构建 |
| `msvcp140_1.dll` | 22.9 KB | build.ps1 | 构建 |
| `onnxruntime.dll` | 16.5 MB | build.ps1 | 构建 |
| `onnxruntime_providers_shared.dll` | 21.5 KB | build.ps1 | 构建 |
| `srt_gui.exe` | 399.0 KB | build.ps1 | 构建 |
| `System.Memory.dll` | 138.9 KB | build.ps1 | 构建 |
| `System.Runtime.CompilerServices.Unsafe.dll` | 16.4 KB | build.ps1 | 构建 |
| `vcruntime140.dll` | 109.4 KB | build.ps1 | 构建 |
| `vcruntime140_1.dll` | 36.4 KB | build.ps1 | 构建 |

## 七、上游包附带 / 旧版本 / 本机私有（非必需）

共 15 个文件，60.7 MB。

| 路径 | 大小 | 来源 | 必需性 |
|---|---:|---|---|
| `config.txt` | 695 B | 本机私有 | 需自建 |
| `models/ggml-vad.bin` | 15 B | 残留(可删) | 可删 |
| `tools/ffmpeg/presets/libvpx-1080p.ffpreset` | 227 B | 上游包附带 | 可选 |
| `tools/ffmpeg/presets/libvpx-1080p50_60.ffpreset` | 227 B | 上游包附带 | 可选 |
| `tools/ffmpeg/presets/libvpx-360p.ffpreset` | 219 B | 上游包附带 | 可选 |
| `tools/ffmpeg/presets/libvpx-720p.ffpreset` | 227 B | 上游包附带 | 可选 |
| `tools/ffmpeg/presets/libvpx-720p50_60.ffpreset` | 227 B | 上游包附带 | 可选 |
| `tools/ffmpeg/README.txt` | 45.3 KB | 上游包附带 | 可选 |
| `tools/whisper-vulkan/ggml.dll` | 67.0 KB | 旧版(不还原) | 可选 |
| `tools/whisper-vulkan/ggml-base.dll` | 623.5 KB | 旧版(不还原) | 可选 |
| `tools/whisper-vulkan/ggml-cpu.dll` | 762.5 KB | 旧版(不还原) | 可选 |
| `tools/whisper-vulkan/ggml-vulkan.dll` | 57.5 MB | 旧版(不还原) | 可选 |
| `tools/whisper-vulkan/whisper.dll` | 1.3 MB | 旧版(不还原) | 可选 |
| `tools/whisper-vulkan/whisper-cli.exe` | 470.5 KB | 旧版(不还原) | 可选 |
| `本地ollama.txt` | 22 B | 本机私有 | 可选 |

## 怎么核对

```powershell
# 逐项比对当前目录与清单(缺必需文件时退出码 1)
powershell -ExecutionPolicy Bypass -File .\scripts\check-tree.ps1

# 连文件大小一起比对(改了源码/换了模型版本时会有差值, 属正常)
powershell -ExecutionPolicy Bypass -File .\scripts\check-tree.ps1 -Strict

# 文件有增减时重新生成本表
powershell -ExecutionPolicy Bypass -File .\scripts\gen-tree.ps1
```

机读版本是 `manifests/tree.txt`（`路径<TAB>字节数<TAB>来源<TAB>必需性`），每行一个文件，可以直接喂给脚本或 CI。

## 说明

- **`tools/whisper-vulkan/` 与 `tools/whisper-vulkan-new/`**：程序按 -new → 旧版 顺序查找，`whisper-vulkan-new` 是当前使用的那份。旧目录保留是因为原作者同时留了，deploy.ps1 不还原它，缺了不影响运行。
- **`models/ggml-vad.bin`** 只有 15 字节，内容是 `Entry not found`，是早先一次失败下载的残留。代码里没有任何地方读它（VAD 用的是 `ggml-silero-v6.2.0.bin`），可以删。
- **根目录那 11 个 DLL**（DirectML / onnxruntime / MathNet / System.Memory / System.Runtime.CompilerServices.Unsafe / onnxruntime_providers_shared / msvcp140 / msvcp140_1 / vcruntime140 / vcruntime140_1 / Microsoft.ML.OnnxRuntime）不是下载来的，是 `gui/build.ps1` 每次编译时从 `tools/lib/` 复制到 exe 同级的。换机重编译会自动重新生成。
- **`tools/ffmpeg/LICENSE`** 由 deploy.ps1 从压缩包里的 `LICENSE*` 写入（统一改名）。**`presets/*.ffpreset` 和 `README.txt`** 是你本机装的 gyan.dev 完整包自带的，deploy.ps1 用的 BtbN 包不含这两项；脚本发现压缩包里有 `presets/` 才会铺过去。程序没有调用 `-pre` 预设，README 只是构建说明，缺了都不影响运行。
- **`tools/ollama/qwen35mt.jinja`** 是内置翻译模型模板的副本（内容与 `modelfile/qwen3.5-mt-2b.Modelfile` 的 TEMPLATE 块一致），ollama 构建时不读它，保留仅作参考。
- **`config.txt`** 含你的片源路径，不进仓库；clone 后从 `config.example.txt` 复制一份改名即可。
- **本表按默认安装记录**。用 `-SmallModel` 时 `models/ggml-medium.bin` 会换成 `ggml-small.bin`；whisper 回退到 CPU 构建时 `tools/whisper-vulkan-new/` 里的文件集合也可能与上表不同（少 `ggml-vulkan.dll`）。这两种情况 `check-tree.ps1` 都不会判为失败。
- **根目录 DLL 与 `srt_gui.exe`** 每次编译都会被覆盖，大小会随源码变动；`-Strict` 模式下的体积差异属正常。

