# 第三方组件来源与许可

本项目**仓库内不含任何第三方二进制**，全部由 `deploy.ps1` 在部署时从上游下载。
下表列出各依赖的出处、版本与许可，使用时请遵守各自的许可条款。

| 组件 | 用途 | 来源 | 许可 |
|---|---|---|---|
| FFmpeg | 音轨提取 / 切片 / 转码 | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)（`win64-gpl`）<br>本地亦可换用 [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) full 构建 | GPL v3 |
| whisper.cpp | 语音识别（`whisper-cli.exe`） | [ggml-org/whisper.cpp](https://github.com/ggml-org/whisper.cpp) | MIT |
| Whisper 模型 | `ggml-medium.bin` / `ggml-small.bin` | [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) | MIT（OpenAI Whisper 权重） |
| Silero VAD | 语音活动检测 `ggml-silero-v6.2.0.bin` | [ggml-org/whisper-vad](https://huggingface.co/ggml-org/whisper-vad) | MIT |
| Ollama | 内置 LLM 推理引擎 | [ollama/ollama](https://github.com/ollama/ollama) v0.35.1 | MIT |
| Qwen3.5-MT-2B GGUF | 内置翻译模型权重（Q4_K_M 1.9 GB，由 [kaelri/qwen3.5-mt](https://ollama.com/kaelri/qwen3.5-mt) 注册表取得，`sha256:4ecac2ba…`） | [ollama.com/kaelri/qwen3.5-mt](https://ollama.com/kaelri/qwen3.5-mt) | Apache 2.0 |
| Qwen3.5-2B GGUF（回退） | 官方权重取不到时的通用基座 | [unsloth/Qwen3.5-2B-GGUF](https://huggingface.co/unsloth/Qwen3.5-2B-GGUF) | Apache 2.0 |
| ONNX Runtime + DirectML | 人声分离 GPU 推理 | [NuGet: Microsoft.ML.OnnxRuntime.DirectML](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.DirectML) 1.24.4 | MIT |
| MathNet.Numerics | FFT（MDX 频谱处理） | [NuGet](https://www.nuget.org/packages/MathNet.Numerics) 4.15.0 | MIT |
| System.Memory / Unsafe | 运行时依赖 | NuGet 4.5.5 / 4.5.3 | MIT |
| UVR MDX-Net 模型 | 人声/伴奏分离 | [TRvlvr/model_repo](https://github.com/TRvlvr/model_repo)（经 [python-audio-separator](https://github.com/nomadkaraoke/python-audio-separator) 镜像分发） | MIT |
| TSCharacters.txt | 繁转简单字映射（4148 字） | [BYVoid/OpenCC](https://github.com/BYVoid/OpenCC) | Apache 2.0 |

## 说明

- **FFmpeg 是 GPL v3**。若你要把本项目与 FFmpeg 一起再分发（例如打包成免安装 zip 发布），
  整体分发物需满足 GPL v3 的相应义务（提供源码等）。本仓库只分发"下载脚本"，不构成再分发。
- UVR MDX-Net 模型由 UVR 项目以 MIT 许可公开，仓库中的副本仅为方便下载的镜像，未做任何修改。
- 模型权重与推理引擎均为第三方作品，本项目仅调用，不对其质量与适用性作任何担保。
