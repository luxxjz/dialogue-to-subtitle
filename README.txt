SRT 自动字幕生成 - 使用说明
=====================================================================
适用程序: srt_gui.exe   版本: 最终版   更新日期: 2026-10-26

本程序是本地无字幕视频/音频自动生成 SRT 字幕的图形界面工具。
全流程本地运行, 不依赖任何在线服务(可选的 OpenAI 兼容接口除外)。


一、项目简介
---------------------------------------------------------------------
把一段视频(或音频、现有字幕)变成带时间轴的字幕文件, 五步管线:

    ① 探测音轨   ffprobe 读取音轨列表
    ② 提取音轨   ffmpeg 提取选中音轨 -> 44.1kHz 立体声 wav
    ③ 人声分离   MDX-Net 神经网络 (onnxruntime DirectML GPU), 可选
    ④ 语音识别   whisper.cpp (Vulkan GPU) + VAD 语音切片, 可选 VAD
    ⑤ LLM 后处理 翻译到目标语言 / 中文纠错 / 不处理 (三种后端可选)

另有"字幕直翻"模式: 输入已有 .srt 文件, 跳过②③④, 直接送 LLM 翻译。

特点:
  - 全流程 GPU 加速(人声分离 DirectML, 识别 Vulkan), 支持多显卡选择
  - 断点续跑: 产物文件存在即视为步骤完成, 停止/崩溃后重开自动跳过
  - LLM 三通道: 内置引擎(免配置) / 系统 Ollama / OpenAI 兼容接口
  - 任意语言互转: 识别语言与目标语言均可手输 whisper/ISO 语言码
  - 输出 SRT / 纯文本(分行/段落) 双格式, 输出位置可选


二、快速开始
---------------------------------------------------------------------
1. 双击 srt_gui.exe (必须位于 SRTproject 根目录, 见"文件结构")
2. ① 输入区: 浏览... 选择视频/音频/字幕文件
3. 点"解析"读取音轨列表(视频文件), 音轨语言会自动回填识别语言
4. ④ LLM 区: 默认"内嵌引擎"(免配置), 或改"接口"连自己的 Ollama
5. 点"开始处理"
6. 完成后到任务目录查看 <片名>.chs.srt (翻译终稿) / .raw.srt (原文)

一个 2 小时电影在主流配置下约需 30-60 分钟(识别为主, 与并行实例数相关)。


三、处理管线详解
---------------------------------------------------------------------
五步状态机(日志中显示"阶段 N/5"):

  阶段1/5 探测: 读取音轨。音频文件直接跳过; 已解析音轨用 #序号, 否则自动选第一条。
  阶段2/5 提取: ffmpeg 提取为 44.1k stereo wav。音频输入跳过(直接用原文件)。
  阶段3/5 分离: 勾选"② 人声分离输出"才执行。MDX-Net 分离人声/伴奏。
                未勾选 -> 跳过, 直接用原始音轨识别(有 BGM 时字幕质量下降)。
                输入文件是 _vocals/_inst 分离产物时, 任务名归并到原视频名。
  阶段4/5 识别: whisper.cpp 识别。
                - 输入非 wav 先 ffmpeg 转 16k mono (修复抖音 AAC 伪装 mp3 解码失败)
                - 勾选 VAD 切片 -> silero VAD 检测语音段 -> 合并区间 -> 切片
                  -> 批次并行识别 -> 归位 -> 汇总(繁转简 + 断行)
                - 不勾选 VAD -> 整轨直推 whisper(时间戳相对全片)
  阶段5/5 LLM:  按模式处理识别结果(见"五、LLM 后处理")。

字幕直翻模式: 输入 .srt 文件时, ②③④ 区自动置灰,
  文件复制为 <片名>.raw.srt, 只执行 LLM 翻译(固定"翻译到目标语言",
  源语言用识别语言下拉, 默认 en)。

停止: 点"停止"立即终止当前子进程(Job Object 杀进程树),
  LLM 进行中的 HTTP 请求也立即中断(不再等超时), 已完成的产物保留。


四、界面使用说明
---------------------------------------------------------------------
窗口自上而下 5 个分区 + 操作按钮 + 日志区:

① 输入
  文件        浏览视频/音频/字幕文件 (也支持直接把文件/文件夹拖到路径框)
  解析        读取音轨列表(仅视频), 自动回填识别语言(音轨带语言标签时;
              MKV/MP4 的 ISO 639-2 三字母码如 kor/jpn/chi 自动转 whisper 两字母码)
  音轨        选择要处理的音轨 (仅视频)
  识别语言    下拉 40+ 语言, 可手输任意 whisper 语言码; 勾"自动识别"则置灰
  目标语言    翻译成什么语言(默认中文), 同样支持手输语言码
  自动识别    勾选后强制 whisper 自动检测语言

② 人声分离输出 (总开关, 取消勾选整组灰掉 = 跳过分离)
  分离模型    自动扫描 models\*.onnx: UVR-MDX-NET-Inst_HQ_3 / Kim_Vocal_2
  保存人声轨  分离后保留人声 wav (推荐保留, 识别用)
  保存伴奏轨  保留伴奏 wav (一般不需要, 跑完自动删除未勾选产物)
  overlap     分块重叠率 0.25 / 0.5 (越大越慢越稳)

③ 识别
  VAD 切片    勾选 = VAD 检测后切片识别(推荐); 取消 = 整轨直推
  VAD 灵敏度  标准 (阈值0.5/最短语音250ms) / 灵敏 (0.3/100ms)
  并行实例    1-8 个 whisper 并发(长音频切段并发 VAD, 上限 4 段); 关 VAD 时无效
  断行        长字幕按行宽断行(仅中文生效)
  繁转简      识别结果繁体转简体(TSCharacters.txt 单字映射, 4148 字)
  whisper 模型   models\*.bin 自动扫描 (ggml-medium 默认 / ggml-small)
  whisper GPU   "自动 (NVIDIA 优先)" 或枚举到的 Vulkan 设备(多卡笔记本选独显)

④ LLM 后处理
  内嵌引擎 / 接口   单选联动: 选"内嵌引擎"后面全部灰掉(固定内置 qwen3.5-mt:2b);
                    选"接口"可编辑(连外部 LLM 服务)
  接口类型    Ollama / OpenAI 兼容
  地址        如 http://127.0.0.1:11434 (Ollama) 或兼容端点
  探测        拉取模型列表 (Ollama /api/tags, OpenAI /v1/models)
  模型        下拉/手输模型名; 接口模式留空默认 qwen3-14b:latest
  模式三选一  不处理 / 翻译到目标语言 / 中文纠错
              联动: 识别语言=zh 且目标=zh -> 自动切"中文纠错";
                    识别语言非 auto -> 自动切"翻译"; 选"不处理"时不覆盖
  超时 s      单次 LLM 请求超时 (10-60s)
  重试        单批最大尝试次数 (1-5)
  批大小      一次送给模型的字幕条数 (1-200, 默认 40)
  API Key     OpenAI 兼容接口的密钥(仅该模式显示)

⑤ 输出
  原始文件夹 / 程序文件夹 / 自定义文件夹  结果存放位置(任务目录 = 输出根\<片名>)
  纯文本输出  不生成 / 分行版 / 段落版 / 两者
  段落阈值 s  段落版判定: 相邻字幕块间隔超过该秒数则分段 (0.5-5.0)

操作按钮: 开始处理 / 停止 / 状态栏(当前步骤) / 日志区(时间戳明细)
  日志同时落盘到 <任务目录>\<片名>.log


五、LLM 后处理详解
---------------------------------------------------------------------
三种后端(④区左侧单选):
  内嵌引擎  程序自带 tools\ollama\ollama.exe + models\ollama 内置模型
             qwen3.5-mt:2b, 端口默认 39281 (被占用自动换空闲端口)
             强制 Vulkan 后端(兼容老 N 卡与 Intel Arc)
             翻译完成自动卸载模型释放显存
  接口       Ollama: http://127.0.0.1:11434 (需本地 Ollama 在跑)
             OpenAI 兼容: 任意 /v1/chat/completions 端点, 可带 API Key

处理逻辑(LlmPost.cs):
  - 指令放 user 前缀而非 system (翻译专用模型对长 system 易角色漂移)
  - 批处理: 每批带序号送模型, 解析 [序号] 译文回填; 每批完成即时存盘
  - 断点续传: 输出 .progress 文件记录已翻译块; 中断重跑自动跳过
  - 降级重试: 整批失败 -> 拆 10 条一批 -> 单条 -> 单条仍失败保留原文
  - 噪声过滤: 连续相同字符 >=20 的噪声行(whisper 把杂音转成 Aaaa...)不送模型
  - 残留补翻: 翻译到中文时, 检测未翻译的非中文残留块, 单条重翻
               (非中文目标无法通用判定, 自动跳过)
  - 翻译时补齐目标语言标点(纯文本输出需要); 长行重新断行(仅中文)

语言联动规则: 识别语言 zh + 目标语言 zh -> 默认"中文纠错";
  识别语言非 auto -> 默认"翻译到目标语言"; 识别语言 auto 不联动;
  选"不处理"时不覆盖任何模式, 识别结果即为终稿。


六、断点续跑机制
---------------------------------------------------------------------
产物文件存在 = 步骤完成, 重开自动跳过:
  阶段2 提取  <片名>.wav 存在
  阶段3 分离  <片名>_vocals.wav 存在
  阶段4 识别  <片名>.raw.srt 存在
  阶段5 LLM   <片名>.<后缀>.srt 存在 且 无 .progress 断点文件
              (有 .progress = 上次未翻完, 会续传不跳过)
  阶段1 探测  无持久产物, 恒跳过

临时目录 <任务目录>\tmp\: 存 VAD 结果/分片 wav/分片 srt。
  识别参数一致时保留(支持识别级断点续跑), 参数变化自动重建;
  全部完成后自动清理。异常/手动停止时可能残留, 可手动删除(会丢失该级断点)。

想强制重跑某一步: 删除对应产物文件即可(如删 raw.srt 重识别)。


七、输出文件说明
---------------------------------------------------------------------
任务目录 = 输出根\<片名>\, 输出根由⑤区决定(原始/程序/自定义文件夹)。

  <片名>.raw.srt       识别原文 (LLM 不处理时即终稿)
  <片名>.chs.srt       目标语言=中文时的 LLM 终稿
  <片名>.<码>.srt      目标语言为其他时, 用语言码作后缀 (如 .en.srt / .ja.srt)
  <片名>.wav           提取的原始音轨
  <片名>_vocals.wav    分离人声 (勾选"保存人声轨"时保留)
  <片名>_inst.wav      分离伴奏 (勾选"保存伴奏轨"时保留)
  <片名>.log           完整运行日志
  <片名>.txt           纯文本输出 (分行版/段落版, 勾选时生成)
  tmp\                 临时目录 (跑完自动清理)

SRT 编码: UTF-8 带 BOM(播放器兼容性最好)。纯文本分行版=每行一句;
段落版=按"段落阈值"间隔分段落。


八、配置文件 config.txt
---------------------------------------------------------------------
exe 同目录, key=value 格式, # 开头为注释(GBK 编码)。
程序启动读取, 关闭窗口时用当前界面值整体重写。

常用字段:
  LlmType     llama(内嵌引擎) / ollama / openai
  LlmUrl      LLM 服务地址 (接口模式)
  LlmModel    模型名 (留空默认 qwen3-14b:latest)
  LlmKey      OpenAI 兼容密钥
  LlmPort     内嵌引擎端口 (默认 39281)
  LlmNgl      内嵌引擎 GPU 层数 (99 = 尽量全进 GPU)
  BatchSize   批大小 (默认 40)
  TimeoutSec  LLM 单次请求超时秒 (默认 60, 界面默认 30)
  RetryCount  单批最大尝试次数 (默认 4, 界面默认 1)
  Mode        none / translate / refine
  SepModel    分离模型路径
  Overlap     分块重叠率 0.25 / 0.5
  SaveVocals / SaveInst   保存人声轨 / 伴奏轨 (1/0)
  BreakLine / TradToSimp  断行 / 繁转简 (1/0)
  EnableSep / EnableVad   启用分离 / VAD (1/0)
  VadPreset   standard / sensitive
  Parallel    并行实例数
  WhisperModel  whisper 模型路径
  RecogLang / TgtLang   识别语言 / 目标语言 (语言码)
  WhisperGpuIndex  whisper GPU 索引 (-1 自动)
  TxtOut       0 不生成 / 1 分行 / 2 段落 / 3 两者
  TxtParaGap   段落阈值秒
  OutMode      program / source / custom
  CustomOutDir 自定义输出根目录
  LastInput / LastDir    上次输入文件 / 目录 (记忆)

注意: 程序关闭时会用界面值覆盖 config.txt, 手改请先关程序,
  或改后不再打开程序。当前界面配置请以程序内显示为准。


九、文件结构
---------------------------------------------------------------------
SRTproject\
  srt_gui.exe          主程序 (必须与 models/tools 同根)
  config.txt           运行配置 (自动生成/保存)
  DirectML.dll 等      运行时库 (onnxruntime / MathNet / VC 运行库, 勿删)
  _transub_tmp\        历史临时目录 (可整体删除)
  models\              whisper 模型 + 分离模型 + 内置 LLM
    ggml-medium.bin     whisper medium (默认, 1.4GB)
    ggml-small.bin      whisper small (465MB, 低配用)
    ggml-silero-v6.2.0.bin  VAD 模型
    UVR-MDX-NET-Inst_HQ_3.onnx  人声分离模型 (Inst 系)
    Kim_Vocal_2.onnx    人声分离模型 (Vocal 系)
    ollama\             内嵌引擎模型 (qwen3.5-mt:2b, blobs+manifests)
  tools\
    ffmpeg\bin\         ffmpeg / ffprobe
    whisper-vulkan-new\ whisper-cli 主力 (Vulkan GPU)
    whisper-vulkan\     whisper-cli 备用
    ollama\             ollama.exe 内嵌引擎 + qwen35mt.jinja
    lib\                编译/运行所需 dll
    TSCharacters.txt    繁转简单字映射表
  gui\                  C# 源码 (.NET Framework 4.8)
    Program.cs          入口
    MainForm.cs         主窗体: 布局/事件/config 绑定/后台线程
    Config.cs           config.txt 读写
    Pipeline.cs         5 步状态机: 断点续跑/步骤执行/停止
    ProcRunner.cs       子进程执行 + Job Object 杀进程树
    MDXSep.cs           MDX-Net 人声分离 (onnxruntime DirectML, 无 python)
    Recognize.cs        VAD 区间识别管线 (替代 make_srt_gpu.ps1)
    LlmPost.cs          LLM 后处理: 批处理/断点续传/降级/残留补翻
    LlmServer.cs        内嵌 Ollama serve 管理
    SrtUtil.cs          断行/繁转简/时长探测/工具定位
    VkUtil.cs           Vulkan 设备枚举 (GPU 下拉)
    build.ps1           一键编译脚本
    readme.txt          早期骨架版说明 (已过时, 以本文件为准)
    _icon\              程序图标


十、编译方法 (改代码后)
---------------------------------------------------------------------
本机无 dotnet SDK, 用系统自带 csc 编译:

  powershell -ExecutionPolicy Bypass -File gui\build.ps1

脚本自动: ① 全部 .cs 转 UTF-8 带 BOM (csc 按 ANSI 读中文会乱码)
          ② csc.exe 编译 -> srt_gui.exe (输出到项目根)
          ③ 运行时 dll 拷贝到项目根

手动编译(等效):
  C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo ^
    /target:winexe /win32icon:gui\_icon\srt.ico /out:srt_gui.exe ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Numerics.dll ^
    /r:<fw>\netstandard.dll /r:<fw>\System.Web.Extensions.dll ^
    /r:tools\lib\System.Memory.dll /r:tools\lib\Microsoft.ML.OnnxRuntime.dll ^
    /r:tools\lib\MathNet.Numerics.dll ^
    gui\Program.cs gui\MainForm.cs gui\Config.cs gui\Pipeline.cs ^
    gui\ProcRunner.cs gui\MDXSep.cs gui\SrtUtil.cs gui\Recognize.cs ^
    gui\LlmPost.cs gui\LlmServer.cs gui\VkUtil.cs


十一、注意事项
---------------------------------------------------------------------
1. 目录完整性: srt_gui.exe 必须与 models\、tools\ 及运行时 dll 同目录。
   复制/移动程序请整目录拷贝(至少带上 models、tools、config.txt、全部 dll)。
   内嵌引擎依赖 tools\ollama\ollama.exe + models\ollama\ 完整目录。

2. 显存: 人声分离(DirectML)与识别/内嵌引擎(Vulkan)共用 GPU。
   whisper medium 模型约需 1.5-2GB; 并行实例越多显存占用越高。
   显存不足: 换 ggml-small、降并行实例、关 VAD(省分片缓存) 或关分离。
   同一时间别同时跑多个大任务(内嵌引擎翻译 + 外部 Ollama 大模型会抢显存)。

3. 断点续跑依赖产物文件: 换识别语言/模型/参数后, 旧产物仍在会导致跳过。
   想重跑某步, 删除该步产物(见"六、断点续跑机制")。

4. 断行仅对中文目标生效(英文按字符断会切断单词); 非中文目标时残留
   补翻也自动跳过(无法通用判定)。

5. 繁体原片: 识别结果可能是繁体, 勾选"繁转简"自动转简体。
   TSCharacters.txt 为单字映射(4148 字), 未收录字不转。

6. 抖音/部分 App 下载的 mp3 可能是 AAC 伪装(mp3 扩展名实际 AAC),
   whisper 内置解码器可能失败。程序已自动 ffmpeg 转 16k mono 修复,
   但请勿把 0 字节/损坏文件当输入。

7. 识别质量: 勾选人声分离可显著提升带 BGM 视频的识别准确率;
   纯语音(播客/访谈)可不分离直接识别更快。VAD 灵敏度"标准"即可,
   口齿不清/背景吵可试"灵敏"。

8. LLM 选择: 内嵌引擎(2B)速度快但翻译质量一般; 质量优先选"接口"连
   更大的本地模型(如 qwen3-14b)或 OpenAI 兼容端点。批大小越大越稳
   但越慢; 超时/重试设太低会跳过更多字幕。

9. 首次使用: 探测/开始时会拉起内嵌引擎(约需 30-90 秒加载模型),
   日志显示"本地引擎已启动"属正常; 首次请求包含模型加载, 会偏慢。

10. 日志: 界面日志同时写 <任务目录>\<片名>.log; 内嵌引擎完整日志写
    _transub_tmp\ollama_serve.log。排查问题先看这两个文件。

11. 停止后重开: 已完成步骤自动跳过, 可直接续跑; 想全量重跑可删任务
    目录或其中产物。

12. 本程序为单机离线工具, 除 OpenAI 兼容接口外不产生任何网络流量;
    本地模型推理, 不上传任何音视频/字幕内容。

十三、已知限制
---------------------------------------------------------------------
- 超长音频(>2h)识别时间较长, 建议分片处理或调高并行实例
- whisper 对重口音/非标准语言可能误识别; "自动识别"对大段语音较准
- 多语言混合音轨只能识别一种语言(识别语言设置)
- 目标语言为任意语言时, LLM 翻译质量取决于所接模型的能力
- 人声分离模型对音乐性强的片源(演唱会)分离效果一般
- 并行实例过高时显存不足会失败, 按显存容量调整(1-8)
