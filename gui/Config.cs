// Config.cs - config.txt 读写 (key=value, UTF-8, # 注释行)
// 保存程序关闭前的全部设置, 启动时读回
// C# / .NET Framework 4.8, csc 编译

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SrtGui
{
    public class Config
    {
        // ---- LLM 后处理 ----
        public string LlmType   = "ollama";                 // ollama / openai / llama(本地引擎, 移动版)
        public string LlmUrl    = "http://127.0.0.1:11434";
        public string LlmModel  = "qwen3-14b:latest";
        public string LlmKey    = "";                       // API Key (仅 OpenAI 兼容用)
        public int    LlmPort   = 39281;               // 本地 llama-server 端口 (llama 模式)
        public int    LlmNgl    = 99;                   // llama-server GPU 层数 (-ngl, 显存不足自动降级)
        public int    BatchSize = 40;
        public int    TimeoutSec = 60;                 // LLM 单次请求超时 (秒)
        public int    RetryCount = 4;                  // LLM 单批最大尝试次数
        public string Mode      = "translate";              // translate=翻译 / refine=中文纠错

        // ---- 分离 & 输出 ----
        public string SepModel  = "";                       // 空 = 默认 Inst_HQ_3 (models 或 UVR)
        public double Overlap   = 0.25;
        public bool   SaveVocals = true;                    // 保存人声轨 (落盘开关)
        public bool   SaveInst   = false;                   // 保存伴奏轨
        public bool   BreakLine  = true;                    // 长字幕断行
        public bool   TradToSimp = true;                    // 繁转简
        public bool   EnableSep  = true;                    // 人声分离可选 (false=跳过分离, 原轨直接识别)
        public bool   EnableVad  = true;                    // VAD 可选 (false=整轨直推 whisper, 不切片)

        // ---- 识别 ----
        public string VadPreset    = "standard";            // standard(0.5/250) / sensitive(0.3/100)
        public int    Parallel    = 2;
        public string WhisperModel = "models\\ggml-medium.bin";
        public string RecogLang    = "auto";                // 识别语言: auto/zh/en/ko/... (手输任意 whisper 语言码)
        public string TgtLang      = "zh";                  // 目标语言: 翻译成什么语言 (zh/en/ja/... 手输任意语言码)
        public int    WhisperGpuIndex = -1;                 // whisper GPU 选择: -1=自动(NVIDIA 优先) / 0..N=Vulkan 设备索引
        public int    TxtOut    = 0;                       // 纯文本输出: 0=不生成 1=分行版 2=段落版 3=两者
        public double TxtParaGap = 1.5;                    // 段落版阈值(秒): 相邻字幕块间隔超此值则分段

        // ---- 输出位置 ----
        public string OutMode = "program";       // program=程序文件夹 / source=原始文件夹 / custom=自定义文件夹
        public string CustomOutDir = "";

        // ---- 输入记忆 ----
        public string LastInput = "";
        public string LastDir   = "";

        private readonly string _path;

        public Config(string path)
        {
            _path = path;
        }

        // 读 config.txt (不存在则用默认值)
        public void Load()
        {
            if (!File.Exists(_path)) { return; }
            string[] lines;
            try { lines = File.ReadAllLines(_path, Encoding.UTF8); }
            catch { return; }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) { continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) { continue; }
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "LlmType":   LlmType = v; break;
                    case "LlmUrl":    LlmUrl = v; break;
                    case "LlmModel":  LlmModel = v; break;
                    case "LlmKey":    LlmKey = v; break;
                    case "LlmPort":   LlmPort = ParseInt(v, LlmPort); break;
                    case "LlmNgl":    LlmNgl = ParseInt(v, LlmNgl); break;
                    case "BatchSize": BatchSize = ParseInt(v, BatchSize); break;
                    case "TimeoutSec": TimeoutSec = ParseInt(v, TimeoutSec); break;
                    case "RetryCount": RetryCount = ParseInt(v, RetryCount); break;
                    case "Mode":      Mode = v; break;
                    case "SepModel":  SepModel = v; break;
                    case "Overlap":   Overlap = ParseDbl(v, Overlap); break;
                    case "SaveVocals": SaveVocals = ParseBool(v, SaveVocals); break;
                    case "SaveInst":   SaveInst = ParseBool(v, SaveInst); break;
                    case "BreakLine":  BreakLine = ParseBool(v, BreakLine); break;
                    case "TradToSimp": TradToSimp = ParseBool(v, TradToSimp); break;
                    case "EnableSep":  EnableSep = ParseBool(v, EnableSep); break;
                    case "EnableVad":  EnableVad = ParseBool(v, EnableVad); break;
                    case "VadPreset":    VadPreset = v; break;
                    case "Parallel":     Parallel = ParseInt(v, Parallel); break;
                    case "WhisperModel": WhisperModel = v; break;
                    case "RecogLang":    RecogLang = v; break;
                    case "TgtLang":      TgtLang = v; break;
                    case "WhisperGpuIndex": WhisperGpuIndex = ParseInt(v, WhisperGpuIndex); break;
                    case "TxtOut":      TxtOut = ParseInt(v, TxtOut); break;
                    case "TxtParaGap":  TxtParaGap = ParseDbl(v, TxtParaGap); break;
                    case "OutMode":      OutMode = v; break;
                    case "CustomOutDir": CustomOutDir = v; break;
                    case "LastInput":    LastInput = v; break;
                    case "LastDir":      LastDir = v; break;
                }
            }
        }

        // 写 config.txt (关闭前保存)
        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# SRT GUI 配置 (key=value, # 开头为注释)");
            sb.AppendLine();
            sb.AppendLine("# ---- LLM 后处理 ----");
            sb.AppendLine("LlmType=" + LlmType);
            sb.AppendLine("LlmUrl=" + LlmUrl);
            sb.AppendLine("LlmModel=" + LlmModel);
            sb.AppendLine("LlmKey=" + LlmKey);
            sb.AppendLine("LlmPort=" + LlmPort);
            sb.AppendLine("LlmNgl=" + LlmNgl);
            sb.AppendLine("BatchSize=" + BatchSize);
            sb.AppendLine("TimeoutSec=" + TimeoutSec);
            sb.AppendLine("RetryCount=" + RetryCount);
            sb.AppendLine("Mode=" + Mode);
            sb.AppendLine();
            sb.AppendLine("# ---- 分离 & 输出 ----");
            sb.AppendLine("SepModel=" + SepModel);
            sb.AppendLine("Overlap=" + Overlap.ToString("0.##"));
            sb.AppendLine("SaveVocals=" + (SaveVocals ? "1" : "0"));
            sb.AppendLine("SaveInst=" + (SaveInst ? "1" : "0"));
            sb.AppendLine("BreakLine=" + (BreakLine ? "1" : "0"));
            sb.AppendLine("TradToSimp=" + (TradToSimp ? "1" : "0"));
            sb.AppendLine("EnableSep=" + (EnableSep ? "1" : "0"));
            sb.AppendLine("EnableVad=" + (EnableVad ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("# ---- 识别 ----");
            sb.AppendLine("VadPreset=" + VadPreset);
            sb.AppendLine("Parallel=" + Parallel);
            sb.AppendLine("WhisperModel=" + WhisperModel);
            sb.AppendLine("RecogLang=" + RecogLang);
            sb.AppendLine("TgtLang=" + TgtLang);
            sb.AppendLine("WhisperGpuIndex=" + WhisperGpuIndex);
            sb.AppendLine("TxtOut=" + TxtOut);
            sb.AppendLine("TxtParaGap=" + TxtParaGap.ToString("0.##"));
            sb.AppendLine();
            sb.AppendLine("# ---- 输入记忆 ----");
            sb.AppendLine("OutMode=" + OutMode);
            sb.AppendLine("CustomOutDir=" + CustomOutDir);
            sb.AppendLine("LastInput=" + LastInput);
            sb.AppendLine("LastDir=" + LastDir);
            try { File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { /* 保存失败不影响程序 */ System.Diagnostics.Debug.WriteLine(ex.Message); }
        }

        private static int ParseInt(string v, int def)
        {
            int r; return int.TryParse(v, out r) ? r : def;
        }
        private static double ParseDbl(string v, double def)
        {
            double r; return double.TryParse(v, out r) ? r : def;
        }
        private static bool ParseBool(string v, bool def)
        {
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) { return false; }
            return def;
        }
    }
}
