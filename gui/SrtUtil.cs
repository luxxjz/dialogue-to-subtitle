// SrtUtil.cs - SRT 管线公共工具 (断行/繁转简/时长探测/工具定位)
// 由 make_srt_gpu.ps1 + srt_llm.ps1 的公共部分转写, C#5 兼容 (老 csc)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SrtGui
{
    public static class SrtUtil
    {
        // ---- 8.3 短路径 (whisper-cli 用 ANSI argv, 含韩文等非 GBK 字符的路径会乱码) ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        // 已存在的文件/目录 -> 短路径; 失败返回原路径
        public static string ShortPath(string p)
        {
            if (string.IsNullOrEmpty(p)) { return p; }
            try
            {
                uint n = GetShortPathNameW(p, null, 0);
                if (n == 0) { return p; }
                StringBuilder sb = new StringBuilder((int)n);
                if (GetShortPathNameW(p, sb, n) == 0) { return p; }
                return (sb.Length > 0) ? sb.ToString() : p;
            }
            catch { return p; }
        }

        // 尚不存在的输出路径 (whisper -of 等): 父目录转短路径 + 文件名
        public static string ShortPathForWrite(string p)
        {
            if (string.IsNullOrEmpty(p)) { return p; }
            string dir = Path.GetDirectoryName(p);
            if (string.IsNullOrEmpty(dir)) { return p; }
            return Path.Combine(ShortPath(dir), Path.GetFileName(p));
        }

        // ---- 工具定位 (与 ps1 一致: tools 优先, 递归兜底) ----

        public static string FindFfmpeg(string root)
        {
            string p = Path.Combine(root, "tools", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(p)) return p;
            return FindRecursive(root, "ffmpeg.exe");
        }

        public static string FindWhisper(string root)
        {
            string[] cand = new string[] {
                Path.Combine(root, "tools", "whisper-vulkan-new", "whisper-cli.exe"),
                Path.Combine(root, "tools", "whisper-vulkan", "whisper-cli.exe"),
                Path.Combine(root, "whisper-vulkan-new", "whisper-cli.exe"),
                Path.Combine(root, "whisper-vulkan", "whisper-cli.exe"),
            };
            foreach (string c in cand) { if (File.Exists(c)) return c; }
            return "";
        }

        public static string FindVadModel(string root)
        {
            string p = Path.Combine(root, "models", "ggml-silero-v6.2.0.bin");
            if (File.Exists(p)) return p;
            return "";
        }

        static string FindRecursive(string dir, string name)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, name))
                { if (f.IndexOf("git", StringComparison.OrdinalIgnoreCase) >= 0) return f; }
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string r = FindRecursive(d, name);
                    if (r != "") return r;
                }
            }
            catch { }
            return "";
        }

        // ---- 时长探测: ffmpeg -i 解析 Duration ----

        public static double ProbeDuration(string ffmpeg, string wav)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ffmpeg, "-hide_banner -i \"" + wav + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string all = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    foreach (string line in all.Split('\n'))
                    {
                        if (line.IndexOf("Duration:", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(
                            line, "Duration: (\\d+):(\\d+):(\\d+\\.?\\d*)");
                        if (m.Success)
                        {
                            return int.Parse(m.Groups[1].Value) * 3600
                                 + int.Parse(m.Groups[2].Value) * 60
                                 + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                }
            }
            catch { }
            return 0.0;
        }

        // ---- 长字幕断行 (与 ps1 相同规则) ----
        // 超过 MaxLen(20) 字时, 在段内第二个逗号后换行; 逗号不足 2 个用第 1 个;
        // 无逗号且超 HardMax(26) 字才按中点断; 两段仍超长递归再断

        public static string SplitLongLine(string s, int maxLen, int hardMax)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxLen) return s;

            List<int> commas = new List<int>();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == (char)0xFF0C || c == ',') commas.Add(i);
            }
            int pos = -1;
            if (commas.Count >= 2) pos = commas[1] + 1;
            else if (commas.Count == 1) pos = commas[0] + 1;
            else
            {
                if (s.Length <= hardMax) return s;
                pos = s.Length / 2;
            }
            if (pos <= 0 || pos >= s.Length) pos = s.Length / 2;
            if (pos <= 0) pos = 1;

            string p1 = SplitLongLine(s.Substring(0, pos), maxLen, hardMax);
            string p2 = SplitLongLine(s.Substring(pos), maxLen, hardMax);
            return p1 + "\r\n" + p2;
        }

        // ---- 繁转简 (OpenCC 单字映射表 TSCharacters.txt) ----

        public static Dictionary<string, string> LoadT2S(string tsFile)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            try
            {
                foreach (string raw in File.ReadAllLines(tsFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line == "" || line.StartsWith("#")) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 2) continue;
                    string[] simp = parts[1].Split(' ');
                    if (simp.Length > 0 && simp[0] != "") map[parts[0]] = simp[0];
                }
            }
            catch { }
            return map;
        }

        public static string ConvertT2S(string s, Dictionary<string, string> map)
        {
            if (string.IsNullOrEmpty(s) || map.Count == 0) return s;
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                string key = ch.ToString();
                string v;
                if (map.TryGetValue(key, out v)) sb.Append(v);
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        // ---- SRT 时间格式化 ----

        public static string FmtTime(double sec)
        {
            if (sec < 0) sec = 0;
            TimeSpan ts = TimeSpan.FromSeconds(sec);
            return string.Format("{0:D2}:{1:D2}:{2:D2},{3:D3}",
                ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds);
        }

        // ---- 进程辅助: 运行并将 stdout/stderr 原样(字节)重定向到文件 ----
        // whisper 的打印是 UTF-8, 直接读流会按系统 GBK 解码乱码, 所以复制原始字节

        public static int RunProcRedirect(string exe, string args, string outFile, string errFile,
            Func<bool> cancel, int timeoutMs)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                using (FileStream fo = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (FileStream fe = new FileStream(errFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    Thread t1 = new Thread(delegate() { try { p.StandardOutput.BaseStream.CopyTo(fo); } catch { } });
                    Thread t2 = new Thread(delegate() { try { p.StandardError.BaseStream.CopyTo(fe); } catch { } });
                    t1.Start(); t2.Start();
                    bool exited = false;
                    int waited = 0;
                    while (!exited)
                    {
                        // 用户停止: 立即 Kill 返回 -999, 不等进程自己跑完
                        if (cancel != null && cancel())
                        {
                            try { p.Kill(); } catch { }
                            t1.Join(5000); t2.Join(5000);
                            return -999;
                        }
                        if (waited >= timeoutMs)
                        {
                            try { p.Kill(); } catch { }
                            break;
                        }
                        exited = p.WaitForExit(200);
                        waited += 200;
                    }
                    t1.Join(5000); t2.Join(5000);
                    return p.ExitCode;
                }
            }
        }

        // ---- 纯文本输出: srt -> txt ----
        // line=true 输出分行版(每句一行); para=true 输出段落版(相邻块间隔>paraGap 秒切段, 句间空格连接)
        // 返回总句数; 写 UTF8(带 BOM, 记事本友好)
        public static int ExportTxt(string srtPath, string txtPath, bool line, bool para, double paraGap)
        {
            List<string> srcLines = new List<string>(File.ReadAllLines(srtPath, Encoding.UTF8));
            List<double[]> blocks = new List<double[]>();      // 每块 {st, en}
            List<string> texts = new List<string>();
            for (int i = 0; i < srcLines.Count; )
            {
                string idxLine = srcLines[i].Trim();
                if (idxLine == "" || i + 2 >= srcLines.Count) { i++; continue; }
                System.Text.RegularExpressions.Match tm = System.Text.RegularExpressions.Regex.Match(
                    srcLines[i + 1].Trim(),
                    "(\\d+):(\\d+):(\\d+),(\\d+)\\s*-->\\s*(\\d+):(\\d+):(\\d+),(\\d+)");
                if (!tm.Success) { i++; continue; }
                StringBuilder txt = new StringBuilder();
                int j = i + 2;
                while (j < srcLines.Count && srcLines[j].Trim() != "")
                {
                    if (txt.Length > 0) { txt.Append(" "); }
                    txt.Append(srcLines[j].Trim());
                    j++;
                }
                double st = int.Parse(tm.Groups[1].Value) * 3600 + int.Parse(tm.Groups[2].Value) * 60
                          + int.Parse(tm.Groups[3].Value) + int.Parse(tm.Groups[4].Value) / 1000.0;
                double en = int.Parse(tm.Groups[5].Value) * 3600 + int.Parse(tm.Groups[6].Value) * 60
                          + int.Parse(tm.Groups[7].Value) + int.Parse(tm.Groups[8].Value) / 1000.0;
                blocks.Add(new double[] { st, en });
                texts.Add(txt.ToString());
                i = j;
            }
            if (blocks.Count == 0) { throw new Exception("未解析到字幕块: " + srtPath); }

            StringBuilder sb = new StringBuilder();
            int n = 0;
            if (line)
            {
                for (int k = 0; k < texts.Count; k++) { sb.AppendLine(texts[k]); }
                n = texts.Count;
            }
            if (para)
            {
                if (line) { sb.AppendLine(""); }
                List<string> paraBuf = new List<string>();
                double prevEnd = -10.0;
                for (int k = 0; k < blocks.Count; k++)
                {
                    if (k > 0 && blocks[k][0] - prevEnd > paraGap && paraBuf.Count > 0)
                    {
                        sb.AppendLine(string.Join(" ", paraBuf.ToArray()));
                        sb.AppendLine("");
                        paraBuf.Clear();
                    }
                    paraBuf.Add(texts[k]);
                    prevEnd = blocks[k][1];
                }
                if (paraBuf.Count > 0) { sb.AppendLine(string.Join(" ", paraBuf.ToArray())); }
                if (n == 0) { n = texts.Count; }
            }
            File.WriteAllText(txtPath, sb.ToString(), new UTF8Encoding(true));
            return n;
        }

        // ---- ISO 639-2 三字母 -> whisper 两字母语言码 ----
        // 背景: MKV/MP4 音轨语言按 Matroska 规范存 ISO 639-2 三字母 (kor/jpn/chi/fre...),
        //       ffprobe 原样透传; 而 whisper-cli -l 只接受 ISO 639-1 两字母 (ko/ja/zh/fr...) 或 auto。
        //       回填/传参前必须归一化, 否则 whisper 语言识别失败。
        // 规则: 带国家码取主码("fre-ca"->fre); und/空/未收录 -> auto (交给 whisper 自动检测);
        //       已是两字母直接用; yue 特例保留 (whisper 支持, 非 639-2 码)。
        static readonly Dictionary<string, string> Iso639_2To1 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "afr", "af" }, { "amh", "am" }, { "ara", "ar" }, { "asm", "as" }, { "aze", "az" },
            { "bak", "ba" }, { "bel", "be" }, { "bul", "bg" }, { "ben", "bn" }, { "bod", "bo" },
            { "tib", "bo" }, { "bre", "br" }, { "bos", "bs" }, { "cat", "ca" }, { "ces", "cs" },
            { "cze", "cs" }, { "cym", "cy" }, { "wel", "cy" }, { "dan", "da" }, { "deu", "de" },
            { "ger", "de" }, { "ell", "el" }, { "gre", "el" }, { "eng", "en" }, { "spa", "es" },
            { "est", "et" }, { "eus", "eu" }, { "baq", "eu" }, { "fas", "fa" }, { "per", "fa" },
            { "fin", "fi" }, { "fao", "fo" }, { "fra", "fr" }, { "fre", "fr" }, { "glg", "gl" },
            { "guj", "gu" }, { "hau", "ha" }, { "heb", "he" }, { "hin", "hi" }, { "hrv", "hr" },
            { "hat", "ht" }, { "hun", "hu" }, { "hye", "hy" }, { "arm", "hy" }, { "ind", "id" },
            { "isl", "is" }, { "ice", "is" }, { "ita", "it" }, { "jpn", "ja" }, { "jav", "jw" },
            { "kat", "ka" }, { "geo", "ka" }, { "kaz", "kk" }, { "khm", "km" }, { "kan", "kn" },
            { "kor", "ko" }, { "lat", "la" }, { "ltz", "lb" }, { "lin", "ln" }, { "lao", "lo" },
            { "lit", "lt" }, { "lav", "lv" }, { "mlg", "mg" }, { "mri", "mi" }, { "mao", "mi" },
            { "mkd", "mk" }, { "mac", "mk" }, { "mal", "ml" }, { "mon", "mn" }, { "mar", "mr" },
            { "msa", "ms" }, { "may", "ms" }, { "mlt", "mt" }, { "mya", "my" }, { "bur", "my" },
            { "nep", "ne" }, { "nld", "nl" }, { "dut", "nl" }, { "nno", "nn" }, { "nob", "no" },
            { "oci", "oc" }, { "pan", "pa" }, { "pol", "pl" }, { "pus", "ps" }, { "por", "pt" },
            { "ron", "ro" }, { "rum", "ro" }, { "rus", "ru" }, { "san", "sa" }, { "snd", "sd" },
            { "sin", "si" }, { "slk", "sk" }, { "slo", "sk" }, { "slv", "sl" }, { "sna", "sn" },
            { "som", "so" }, { "sqi", "sq" }, { "alb", "sq" }, { "srp", "sr" }, { "sun", "su" },
            { "swe", "sv" }, { "swa", "sw" }, { "tam", "ta" }, { "tel", "te" }, { "tgk", "tg" },
            { "tha", "th" }, { "tuk", "tk" }, { "tgl", "tl" }, { "tur", "tr" }, { "tat", "tt" },
            { "ukr", "uk" }, { "urd", "ur" }, { "uzb", "uz" }, { "vie", "vi" }, { "yid", "yi" },
            { "yor", "yo" }, { "zho", "zh" }, { "chi", "zh" }, { "yue", "yue" }
        };

        public static string ToWhisperLang(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) { return "auto"; }
            string c = code.Trim().ToLowerInvariant();
            int dash = c.IndexOf('-');
            if (dash > 0) { c = c.Substring(0, dash); }     // "fre-ca" -> "fre"
            if (c == "und" || c == "auto") { return "auto"; }
            if (c.Length == 2) { return c; }                // 已是 639-1 两字母, 直接用
            string mapped;
            if (Iso639_2To1.TryGetValue(c, out mapped)) { return mapped; }
            return "auto";                                  // 未收录 -> 交给 whisper 自动检测
        }
    }
}
