// Pipeline.cs - 5 步管线状态机 (探测→提取→分离→识别→LLM)
// 断点续跑: 产物文件存在 = 步骤完成标志, 重新开始跳过已完成步骤
// 停止: 日志写"用户停止", Job 杀全部子进程; 临时文件下次开始全覆盖
// 步骤执行 v1: 调现有脚本/工具 (ffmpeg / mdx_infer.py / make_srt_gpu.ps1 / srt_llm.ps1)

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SrtGui
{
    public enum Step
    {
        Idle, Probe, Extract, Separate, Recognize, Llm
    }

    public class Pipeline
    {
        // ---- 输出位置模式 ----
        public enum OutModeEnum { Program, Source, Custom }

        // ---- 输入(由 MainForm 注入) ----
        public string Root;            // exe 所在目录 (SRTproject 根)
        public string Input;           // 输入视频/音频
        public int    TrackIndex = -1; // 音轨序号 (0-based, -1=自动选第一轨)
        public string Lang = "zh";     // 识别语言 (zh/en/ja/ko/auto)
        public string TgtLang = "zh";  // 目标语言 (翻译成什么: zh/en/ja/...)
        public bool   UseSeparate = true;
        public bool   SubtitleDirect = false;   // 字幕直翻模式: 只跑 LLM 步骤
        public OutModeEnum OutMode = OutModeEnum.Program;  // 任务目录根: 程序/原始/自定义
        public string CustomOutDir = "";                   // 自定义模式时的根目录
        public bool   SaveVocals = true;   // 落盘开关
        public bool   SaveInst = false;
        public string SepModel = "";
        public double Overlap = 0.25;
        public string VadPreset = "standard";
        public bool   EnableVad = true;       // false = 整轨直推 whisper (不 VAD/切片)
        public int    WhisperGpuIndex = -1;   // whisper GPU: -1=自动 / 0..N=Vulkan 设备索引
        public int    Parallel = 2;
        public int    TxtOut = 0;          // 纯文本输出: 0=不生成 1=分行版 2=段落版 3=两者
        public double TxtParaGap = 1.5;    // 段落版: 相邻字幕块间隔超此秒数则分段
        public string WhisperModel = "models\\ggml-medium.bin";
        public string LlmType = "ollama";
        public string LlmUrl = "http://127.0.0.1:11434";
        public string LlmModel = "qwen3-14b:latest";
        public string LlmKey = "";
        public int    LlmPort = 39281;      // 本地 llama-server 端口 (llama 模式)
        public int    LlmNgl  = 99;         // 本地引擎 GPU 层数 (-ngl)
        public int    BatchSize = 40;
        public int    LlmTimeoutSec = 60;  // LLM 单次请求超时 (秒)
        public int    LlmRetries = 4;      // LLM 单批最大尝试次数
        public string Mode = "translate";   // translate / refine
        public bool   BreakLine = true;     // 预留: 识别阶段断行(脚本内恒生效)
        public bool   TradToSimp = true;    // 预留: 识别阶段繁转简(脚本内恒生效)

        // ---- 回调 (MainForm 注入, 转 UI 线程) ----
        public Action<string> Log;
        public Action<string> Status;
        public Action<Step>   OnStep;
        public Action<string> OnFinished;   // "ok" / "stopped" / 错误信息

        // ---- 状态 ----
        public bool Running { get; private set; }
        private volatile bool _stopRequested;
        public bool StopRequested { get { return _stopRequested; } private set { _stopRequested = value; } }
        public Step Current { get; private set; }

        private ProcRunner _runner;
        private Thread _thread;
        private LlmPost.Options _llmOpts;          // LLM 进行中的 Options (Stop 时中断其 HTTP 请求)

        // ---- 任务目录与产物 ----
        public string TaskDir { get; private set; }
        public string Base { get; private set; }
        public string WavPath { get; private set; }          // 提取原始轨 (44.1k stereo)
        public string VocalsPath { get; private set; }       // 分离人声
        public string InstPath { get; private set; }         // 分离伴奏
        public string RawSrt { get; private set; }           // 识别原文
        public string ChsSrt { get; private set; }           // LLM 中文
        public string LogPath { get; private set; }
        public string TmpDir { get; private set; }

        public Pipeline()
        {
            _runner = new ProcRunner();
        }

        // 产物文件存在 = 该步骤完成
        public bool IsDone(Step s)
        {
            switch (s)
            {
                case Step.Probe:     return true;   // 探测由 GUI/自动完成, 无持久产物
                case Step.Extract:   return File.Exists(WavPath);
                case Step.Separate:  return File.Exists(VocalsPath);
                case Step.Recognize: return File.Exists(RawSrt);
                case Step.Llm:       return File.Exists(ChsSrt) && !File.Exists(ChsSrt + ".progress");   // 有断点文件=上次未翻完, 不能跳过
                default: return false;
            }
        }

        // 初始化任务目录/产物路径 (开始前调用)
        public void SetupPaths()
        {
            Base = Path.GetFileNameWithoutExtension(Input);
            // 输入是分离产物(_vocals/_inst)时, 归并到视频名: 任务目录/产物/主日志都用视频名
            if (Base.EndsWith("_vocals")) { Base = Base.Substring(0, Base.Length - 7); }
            else if (Base.EndsWith("_inst")) { Base = Base.Substring(0, Base.Length - 5); }
            // 任务目录根: 程序目录(默认) / 输入文件所在目录(原始) / 自定义目录
            string outRoot = Root;
            if (OutMode == OutModeEnum.Source)
            {
                string d = Path.GetDirectoryName(Input);
                if (!string.IsNullOrEmpty(d)) { outRoot = d; }
            }
            else if (OutMode == OutModeEnum.Custom && !string.IsNullOrEmpty(CustomOutDir))
            {
                outRoot = CustomOutDir;
            }
            TaskDir = Path.Combine(outRoot, Base);
            TmpDir = Path.Combine(TaskDir, "tmp");
            WavPath = Path.Combine(TaskDir, Base + ".wav");
            VocalsPath = Path.Combine(TaskDir, Base + "_vocals.wav");
            InstPath = Path.Combine(TaskDir, Base + "_inst.wav");
            RawSrt = Path.Combine(TaskDir, Base + ".raw.srt");
            ChsSrt = Path.Combine(TaskDir, Base + "." + TgtSuffix(TgtLang) + ".srt");
            LogPath = Path.Combine(TaskDir, Base + ".log");
        }

        // 目标语言 -> 输出文件名后缀: 中文用 chs (历史惯例), 其他用语言码 (如 .en.srt / .ja.srt)
        static string TgtSuffix(string code)
        {
            string c = (code ?? "").Trim().ToLowerInvariant();
            if (c == "" || c == "zh") { return "chs"; }
            return c;
        }

        public void Start()
        {
            if (Running) { return; }
            Running = true;
            StopRequested = false;
            _thread = new Thread(RunSteps);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Stop()
        {
            if (!Running) { return; }
            StopRequested = true;
            _runner.KillAll();
            // LLM 阶段的 HTTP 请求也立即中断, 不再等 60s 超时
            if (_llmOpts != null && _llmOpts.ActiveRequest != null)
            {
                try { _llmOpts.ActiveRequest.Abort(); } catch { }
            }
            Log("用户停止: 已终止当前子进程 (whisper/ffmpeg/python)");
        }

        // ============================================================ 主流程
        private void RunSteps()
        {
            try
            {
                SetupPaths();
                Directory.CreateDirectory(TaskDir);

                // ---- 字幕直翻: 复制源字幕 -> raw.srt, 只跑 LLM 步骤 ----
                if (SubtitleDirect)
                {
                    Log("========================================");
                    Log("字幕直翻: " + Input);
                    Log("任务目录: " + TaskDir);
                    if (!File.Exists(RawSrt)) { File.Copy(Input, RawSrt, true); Log("源字幕已复制: " + RawSrt); }
                    else { Log("源字幕已存在, 沿用: " + RawSrt); }
                    Current = Step.Llm;
                    OnStep(Current);
                    if (Mode == "none")
                    {
                        Log("阶段5/5 跳过: LLM 不处理, 识别结果即为终稿: " + RawSrt);
                    }
                    else if (!IsDone(Step.Llm))
                    {
                        if (!ExecLlm()) { Finish("LLM 后处理失败"); return; }
                    }
                    else { Log("阶段5/5 已完成, 跳过: " + ChsSrt); }
                    Log("========================================");
                    Log(Mode == "none" ? "字幕已就绪 (LLM 不处理, 终稿): " + RawSrt : "字幕翻译完成: " + ChsSrt);
                    CleanTmp();
                    Finish("ok");
                    return;
                }

                // 临时目录: 识别参数一致时保留(VAD 结果/分片 wav/分片 srt 断点续跑),
                // 参数变化或首次则清空重建, 防止换模型/语言后复用旧分片结果
                string fpFile = Path.Combine(TmpDir, "recog_params.txt");
                string paramsFp = Lang + "|" + WhisperModel + "|" + VadPreset + "|" + Parallel + "|" + (EnableVad ? "vad" : "full") + "|" + Path.GetFileName(Input);
                bool keepTmp = false;
                if (Directory.Exists(TmpDir) && File.Exists(fpFile))
                {
                    try { keepTmp = (File.ReadAllText(fpFile, Encoding.UTF8) == paramsFp); }
                    catch { keepTmp = false; }
                }
                if (!keepTmp)
                {
                    if (Directory.Exists(TmpDir)) { Directory.Delete(TmpDir, true); }
                    Directory.CreateDirectory(TmpDir);
                }
                else { Log("识别断点保留: 参数一致, 复用已有分片 (VAD/切片/已识别区间跳过)"); }
                try { File.WriteAllText(fpFile, paramsFp, new UTF8Encoding(false)); }
                catch { }
                Log("========================================");
                Log("开始: " + Input);
                Log("任务目录: " + TaskDir);

                // ---- 步1 探测音轨 ----
                Current = Step.Probe;
                OnStep(Current);
                if (!ExecProbe()) { Finish("探测音轨失败"); return; }

                // ---- 步2 提取音轨 (音频输入直接跳过) ----
                Current = Step.Extract;
                OnStep(Current);
                if (IsAudioInput(Input))
                {
                    Log("阶段2/5 跳过: 输入是音频文件, 直接使用");
                    WavPath = Input;
                }
                else if (!IsDone(Step.Extract))
                {
                    if (StopRequested) { Finish("stopped"); return; }
                    if (!ExecExtract()) { Finish("提取音轨失败"); return; }
                }
                else { Log("阶段2/5 已完成, 跳过: " + WavPath); }

                // ---- 步3 分离人声 (可选) ----
                Current = Step.Separate;
                OnStep(Current);
                if (UseSeparate)
                {
                    if (!IsDone(Step.Separate))
                    {
                        if (StopRequested) { Finish("stopped"); return; }
                        if (!ExecSeparate())
                        {
                            if (StopRequested)
                            {
                                // 分离被用户中断: 删除残缺产物, 下次续跑自动重做分离
                                TryDelete(VocalsPath);
                                TryDelete(InstPath);
                                Finish("stopped"); return;
                            }
                            Finish("分离人声失败"); return;
                        }
                    }
                    else { Log("阶段3/5 已完成, 跳过: " + VocalsPath); }
                }
                else { Log("阶段3/5 跳过: 未启用分离, 直接识别原始轨"); }

                // ---- 步4 识别 ----
                Current = Step.Recognize;
                OnStep(Current);
                if (!IsDone(Step.Recognize))
                {
                    if (StopRequested) { Finish("stopped"); return; }
                    if (!ExecRecognize()) { Finish("识别失败"); return; }
                }
                else { Log("阶段4/5 已完成, 跳过: " + RawSrt); }

                // ---- 步5 LLM 后处理 ----
                Current = Step.Llm;
                OnStep(Current);
                if (Mode == "none")
                {
                    Log("阶段5/5 跳过: LLM 不处理, 识别结果即为终稿: " + RawSrt);
                }
                else if (!IsDone(Step.Llm))
                {
                    if (StopRequested) { Finish("stopped"); return; }
                    if (!ExecLlm()) { Finish("LLM 后处理失败"); return; }
                }
                else { Log("阶段5/5 已完成, 跳过: " + ChsSrt); }

                // ---- 清理落盘开关 ----
                if (!SaveInst && File.Exists(InstPath)) { File.Delete(InstPath); Log("已删除伴奏轨 (未勾选保存)"); }
                if (!SaveVocals && File.Exists(VocalsPath)) { File.Delete(VocalsPath); Log("已删除人声轨 (未勾选保存)"); }

                Log("========================================");
                Log(Mode == "none" ? "全部完成 (LLM 不处理, 终稿): " + RawSrt : "全部完成: " + ChsSrt);
                CleanTmp();
                Finish("ok");
            }
            catch (Exception ex)
            {
                Log("异常: " + ex.Message);
                Finish("异常: " + ex.Message);
            }
        }

        // 全部完成后清理临时目录 (识别分片/VAD 结果等, 断点续跑不再需要)
        void CleanTmp()
        {
            try
            {
                if (Directory.Exists(TmpDir))
                {
                    Directory.Delete(TmpDir, true);
                    Log("临时文件已清理: " + TmpDir);
                }
            }
            catch (Exception ex) { Log("警告: 清理临时文件失败: " + ex.Message); }
        }

        private void Finish(string result)
        {
            Running = false;
            if (OnFinished != null) { OnFinished(result); }
        }

        // ============================================================ 步骤实现

        // 步1: 探测音轨 (TrackIndex<0 时自动选第一轨)
        private bool ExecProbe()
        {
            if (IsAudioInput(Input)) { Log("阶段1/5 探测: 音频输入, 无音轨选择"); return true; }
            if (TrackIndex >= 0) { Log("阶段1/5 探测: 使用音轨 #" + TrackIndex + " (已解析)"); return true; }
            Log("阶段1/5 探测: 自动选择第一条音轨...");
            string ffprobe = FindTool("ffprobe.exe", true);
            if (ffprobe == null) { Log("错误: 未找到 ffprobe.exe"); return false; }
            // 输出音轨序号列表 (csv), 取第一个
            string outTxt = RunCapture(ffprobe,
                "-v error -select_streams a -show_entries stream=index -of csv=p=0 \"" + Input + "\"", "utf8");
            if (outTxt == null) { Log("错误: ffprobe 探测失败"); return false; }
            string first = outTxt.Trim().Split(',')[0];
            int idx;
            if (!int.TryParse(first, out idx)) { Log("错误: 未探测到音轨"); return false; }
            TrackIndex = idx;
            Log("阶段1/5 探测完成: 自动选择音轨 #" + idx);
            return true;
        }

        // 步2: 提取指定音轨 -> 44.1k 立体声 pcm
        // 注意: 探测拿到的 stream index 是全局流序号 (视频0/音频1...), 用 -map 0:<index> 直接映射,
        //       不能用 -map 0:a:<n> (n 是"第n条音频流"序号, 与全局 index 差前面的视频流数)
        // 通道策略: mono/stereo 按现有 -ac 2 下混; 5.1/7.1 等多声道取中置声道(对白)复制成双声道,
        //           不折叠环绕声, 更利于语音识别; 多声道但无中置(如 quad)回退标准下混
        private bool ExecExtract()
        {
            string ffmpeg, ffprobe, args;
            int ch;
            string layout;
            bool useCenter;
            ffmpeg = FindTool("ffmpeg.exe", false);
            if (ffmpeg == null) { Log("错误: 未找到 ffmpeg.exe"); return false; }
            ch = 0; layout = "";
            ffprobe = FindTool("ffprobe.exe", true);
            if (ffprobe != null) { ProbeTrackInfo(ffprobe, out ch, out layout); }
            useCenter = (ch > 2) && HasCenter(layout);
            if (useCenter)
            {
                Log("阶段2/5 提取音轨: " + Input + " -> " + WavPath + " [44.1k stereo, 声道=" + ch
                    + " (" + layout + "), 取中置声道 FC 复制双声道]");
                args = "-hide_banner -y -i \"" + Input + "\" -map 0:" + TrackIndex
                       + " -vn -af \"pan=stereo|c0=FC|c1=FC\" -ar 44100 -c:a pcm_s16le \"" + WavPath + "\"";
            }
            else
            {
                if (ch > 2)
                {
                    Log("阶段2/5 提取音轨: " + Input + " 声道=" + ch + " (" + layout + ") 无中置声道, 按标准下混");
                }
                else
                {
                    Log("阶段2/5 提取音轨: " + Input + " -> " + WavPath + " [44.1k stereo]");
                }
                args = "-hide_banner -y -i \"" + Input + "\" -map 0:" + TrackIndex +
                       " -vn -ac 2 -ar 44100 -c:a pcm_s16le \"" + WavPath + "\"";
            }
            return Exec(ffmpeg, args, "utf8");   // ffmpeg 输出 UTF-8
        }

        // 探测目标音轨(全局 index)的声道数与 channel_layout (csv: index,channels,channel_layout)
        void ProbeTrackInfo(string ffprobe, out int channels, out string layout)
        {
            string outTxt;
            channels = 0; layout = "";
            try
            {
                outTxt = RunCapture(ffprobe,
                    "-v error -select_streams a -show_entries stream=index,channels,channel_layout -of csv=p=0 \""
                    + Input + "\"", "utf8");
            }
            catch { return; }
            if (outTxt == null) { return; }
            foreach (string line in outTxt.Split('\n'))
            {
                string[] f = line.Trim().Split(',');
                int idx;
                if (f.Length < 3) { continue; }
                if (!int.TryParse(f[0], out idx) || idx != TrackIndex) { continue; }
                int.TryParse(f[1], out channels);
                if (f[2] != "N/A") { layout = f[2]; }
                break;
            }
        }

        // ffprobe 的 channel_layout 是布局名 (5.1/quad/...): 除无中置的少数布局外均含 FC
        static bool HasCenter(string layout)
        {
            switch (layout)
            {
                case "": case "N/A": case "mono": case "stereo": case "2.1":
                case "4.0": case "quad": case "quad(side)": case "downmix":
                    return false;
                default:   // 3.0 / 5.0 / 5.1 / 5.1(side) / 6.x / 7.x / octagonal / 22.2 等均含 FC
                    return true;
            }
        }

        // 步3: MDX 人声分离 (onnx DirectML, C# 内置, 无 python 依赖)
        private bool ExecSeparate()
        {
            // 输入本身就是分离产物(_vocals/_inst)时跳过分离, 直接用输入识别
            string inBase = Path.GetFileNameWithoutExtension(Input);
            if (inBase.EndsWith("_vocals") || inBase.EndsWith("_inst"))
            {
                Log("阶段3/5 跳过: 输入已是分离产物, 直接用于识别: " + Input);
                return true;
            }
            if (SepModel == "") { SepModel = Path.Combine(Root, "models", "UVR-MDX-NET-Inst_HQ_3.onnx"); }
            if (!File.Exists(SepModel)) { Log("错误: 未找到分离模型: " + SepModel); return false; }
            Log("阶段3/5 分离人声: 模型 " + Path.GetFileName(SepModel) + " overlap=" + Overlap);
            return MDXSep.Separate(SepModel, WavPath, VocalsPath, SaveInst ? InstPath : "", Overlap,
                                   delegate(string s) { Log(s); },
                                   delegate { return StopRequested; });
        }

        void TryDelete(string path)
        {
            try { if (File.Exists(path)) { File.Delete(path); Log("已删除残缺产物: " + path); } }
            catch { }
        }

        // 步4: VAD 区间识别 + 汇总 SRT (内联 C# 识别管线, 输出 raw.srt)
        private bool ExecRecognize()
        {
            // 输入是分离产物时直接用输入; 否则用分离产物或原始轨
            string src;
            string inBase = Path.GetFileNameWithoutExtension(Input);
            if (inBase.EndsWith("_vocals") || inBase.EndsWith("_inst")) { src = Input; }
            else { src = UseSeparate ? VocalsPath : WavPath; }

            // VAD 预设 -> 参数
            double vt = 0.5; int vspd = 250;
            if (VadPreset == "sensitive") { vt = 0.3; vspd = 100; }
            string langArg = (Lang == "auto") ? "auto" : Lang;

            Log("阶段4/5 识别: " + src + "  Lang=" + langArg
                + (EnableVad ? "  VAD(" + vt + "/200/" + vspd + ")" : "  整轨直推(VAD 关闭)")
                + "  Parallel=" + Parallel);

            Recognize.Options o = new Recognize.Options();
            o.Root = Root;
            o.Audio = src;
            o.Model = WhisperModel;
            o.Lang = langArg;
            o.Threads = 4;
            o.Parallel = Parallel;
            o.VadThreshold = vt;
            o.VadMinSpeech = vspd;
            o.VadEnabled = EnableVad;
            o.GpuIndex = WhisperGpuIndex;
            o.Out = RawSrt;
            o.TmpDir = TmpDir;
            o.Log = delegate(string s) { Log(s); };
            o.Cancel = delegate { return StopRequested; };
            return Recognize.Run(o);
        }

        // 步5: LLM 后处理 (内联 C#: ollama / openai 兼容 / llama 本地引擎)
        private bool ExecLlm()
        {
            if (!File.Exists(RawSrt)) { Log("错误: 缺少识别结果 " + RawSrt); return false; }

            // llama 本地引擎: 自动拉起内置 ollama serve (内嵌 qwen3.5-mt:2b), 翻译完回收
            LlmServer server = null;
            if (LlmType == "llama")
            {
                Log("阶段5/5 LLM 本地引擎: " + LlmModel + "  批=" + BatchSize);
                server = new LlmServer();
                try
                {
                    string model;
                    string url = server.EnsureRunning(LlmPort, _runner, delegate(string s) { Log(s); }, delegate { return StopRequested; }, out model);
                    LlmUrl = url;
                    LlmModel = model;      // 内置模型名 (manifest)
                    Log("本地引擎地址: " + url + "  模型: " + model);
                }
                catch (Exception ex)
                {
                    Log("错误: 本地引擎启动失败 - " + ex.Message);
                    return false;
                }
            }
            else
            {
                Log("阶段5/5 LLM " + (Mode == "translate" ? "翻译" : "纠错") + ": " + LlmModel + "  批=" + BatchSize);
            }

            LlmPost.Options o = new LlmPost.Options();
            o.Srt = RawSrt;
            o.Out = ChsSrt;
            o.Model = LlmModel;
            o.Mode = Mode;
            o.Backend = (LlmType == "llama") ? "ollama" : LlmType;   // 内置引擎走 ollama /api/chat
            o.ApiKey = LlmKey;
            o.Batch = BatchSize;
            o.TimeoutSec = LlmTimeoutSec;
            o.Retries = LlmRetries;
            o.BaseUrl = LlmUrl;
            o.LogFile = Path.Combine(TaskDir, Base + "_llm.log");
            o.Log = delegate(string s) { Log(s); };
            o.Cancel = delegate { return StopRequested; };
            o.AddPunct = (TxtOut > 0);   // 需要生成纯文本时, 翻译补齐标点; 否则保持原提示词
            o.TgtLang = TgtLang;         // 目标语言 (翻译成什么)
            o.BreakLine = BreakLine;     // 断行开关 (GUI 可配置; 仅中文目标生效)
            _llmOpts = o;
            bool ok = false;
            try { ok = LlmPost.Run(o); }
            finally
            {
                _llmOpts = null;
                // 翻译完成: 卸载显存中的翻译模型 (ollama keep_alive=0), 释放显存供后续任务
                if (ok && (LlmType == "ollama" || LlmType == "llama"))
                {
                    UnloadLlmModel(LlmUrl, LlmModel);
                }
                if (server != null) { try { server.Stop(); } catch { } }
            }
            return ok && ExportTxtIfEnabled();
        }

        // 纯文本输出: LLM 完成后在 srt 旁生成 .txt (分行版/段落版各自独立文件); 失败仅记录不影响结果
        bool ExportTxtIfEnabled()
        {
            if (TxtOut <= 0 || !File.Exists(ChsSrt)) { return true; }
            try
            {
                string baseName, p;
                int n;
                bool line = (TxtOut == 1 || TxtOut == 3);
                bool para = (TxtOut == 2 || TxtOut == 3);
                baseName = ChsSrt.Substring(0, ChsSrt.Length - 4);   // ...\Base.chs
                if (line)
                {
                    p = baseName + ".分行.txt";
                    n = SrtUtil.ExportTxt(ChsSrt, p, true, false, TxtParaGap);
                    Log("纯文本输出(分行版): " + p + " (" + n + " 句)");
                }
                if (para)
                {
                    p = baseName + ".段落.txt";
                    n = SrtUtil.ExportTxt(ChsSrt, p, false, true, TxtParaGap);
                    Log("纯文本输出(段落版): " + p + " (" + n + " 句)");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("错误: 纯文本输出失败 - " + ex.Message);
                return true;   // txt 失败不阻断主流程
            }
        }

        // LLM 翻译完成后卸载显存中的模型 (ollama keep_alive=0 立即释放), 失败仅记录不影响结果
        void UnloadLlmModel(string baseUrl, string model)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/api/generate");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 5000;
                string body = "{\"model\":\"" + model.Replace("\"", "") + "\",\"prompt\":\"\",\"keep_alive\":0}";
                byte[] data = Encoding.UTF8.GetBytes(body);
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
                using (WebResponse r = req.GetResponse()) { }
                Log("已卸载翻译模型 " + model + " (释放显存)");
            }
            catch (Exception ex)
            {
                Log("卸载翻译模型失败(忽略): " + ex.Message);
            }
        }

        // ============================================================ 工具

        private bool IsAudioInput(string f)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            return ext == ".wav" || ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".aac" || ext == ".ogg";
        }

        // 找工具: 优先 git 版 ffmpeg (与脚本一致); ffprobe 取递归第一个
        private string FindTool(string name, bool preferGit)
        {
            string best = null;
            try
            {
                foreach (string f in Directory.GetFiles(Root, name, SearchOption.AllDirectories))
                {
                    if (preferGit) { return f; }
                    if (f.IndexOf("git", StringComparison.OrdinalIgnoreCase) >= 0) { best = f; }
                    else if (best == null) { best = f; }
                }
            }
            catch { }
            return best;
        }

        private bool ExecProbePython(string python)
        {
            return true; // python 存在性在 Exec 失败时体现
        }

        // 执行进程, 输出逐行进日志; 返回退出码是否为 0
        private bool Exec(string exe, string args, string enc)
        {
            if (StopRequested) { return false; }
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            Encoding e = (enc == "utf8") ? Encoding.UTF8 : Encoding.GetEncoding(936);
            psi.StandardOutputEncoding = e;
            psi.StandardErrorEncoding = e;
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception ex) { Log("启动失败: " + exe + "  " + ex.Message); return false; }
            _runner.AddProcess(p);
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs a)
            {
                if (!string.IsNullOrEmpty(a.Data)) { Log(a.Data); }
            };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a)
            {
                if (!string.IsNullOrEmpty(a.Data)) { Log(a.Data); }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        // python 执行 (UTF-8 输出, PYTHONIOENCODING)
        private bool ExecPython(string python, string args)
        {
            if (StopRequested) { return false; }
            ProcessStartInfo psi = new ProcessStartInfo(python, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            try { psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8"; } catch { }
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception ex) { Log("启动失败: python  " + ex.Message); return false; }
            _runner.AddProcess(p);
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs a)
            {
                if (!string.IsNullOrEmpty(a.Data)) { Log(a.Data); }
            };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs a)
            {
                if (!string.IsNullOrEmpty(a.Data)) { Log(a.Data); }
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode == 0;
        }

        // 同步捕获输出 (用于 ffprobe 探测)
        private string RunCapture(string exe, string args, string enc)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            Encoding e = (enc == "utf8") ? Encoding.UTF8 : Encoding.GetEncoding(936);
            psi.StandardOutputEncoding = e;
            psi.StandardErrorEncoding = e;
            try
            {
                Process p = Process.Start(psi);
                _runner.AddProcess(p);
                string s = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode == 0) ? s : null;
            }
            catch { return null; }
        }
    }
}
