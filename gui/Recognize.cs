// Recognize.cs - VAD 区间识别管线 (替代 make_srt_gpu.ps1)
// VAD 检测 -> 合并区间 -> ffmpeg 切片 -> 批次并行 whisper 识别 -> 归位 -> 汇总(繁转简+断行)
// 断点续跑: vad.log / seg wav / seg srt 存在即跳过 (由 Pipeline 保证 tmp 参数指纹一致)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SrtGui
{
    public static class Recognize
    {
        public class Options
        {
            public string Root;          // exe 所在目录
            public string Audio;         // 输入 wav (原始轨/分离产物)
            public string Model;         // whisper 模型 (相对或绝对)
            public string Lang = "zh";   // 识别语言
            public int Threads = 4;
            public int Parallel = 2;
            public double VadThreshold = 0.5;
            public int VadMinSpeech = 250;
            public bool VadEnabled = true;      // false = 整轨直推 whisper (不做 VAD/切片)
            public int GpuIndex = -1;           // whisper GPU: -1=自动(NVIDIA 优先) / 0..N=Vulkan 设备索引
            public string Out;           // 输出 srt 绝对路径
            public string TmpDir;        // tmp 目录 (Pipeline 已按指纹保留/重建)
            public Action<string> Log;
            public Func<bool> Cancel;
        }

        const double MergeGap = 1.5;   // 相邻语音段间隔小于此秒数则合并为同一区间
        const double PadExt = 0.3;     // 区间前后扩展秒数
        const int BatchSize = 100;     // 每批区间数 (与 ps1 默认一致)
        const int VadSilence = 200;    // VAD 最小静音间隔 ms

        public static bool Run(Options o)
        {
            string ffmpeg = SrtUtil.FindFfmpeg(o.Root);
            string whisper = SrtUtil.FindWhisper(o.Root);
            string vadModel = SrtUtil.FindVadModel(o.Root);
            if (ffmpeg == "") { Log(o, "错误: 未找到 ffmpeg.exe"); return false; }
            if (whisper == "") { Log(o, "错误: 未找到 whisper-cli.exe"); return false; }
            if (vadModel == "") { Log(o, "错误: 未找到 VAD 模型 ggml-silero-v6.2.0.bin"); return false; }

            string model = o.Model;
            if (!Path.IsPathRooted(model)) model = Path.Combine(o.Root, model);
            if (!File.Exists(model)) { Log(o, "错误: 未找到识别模型: " + model); return false; }
            if (!File.Exists(o.Audio)) { Log(o, "错误: 输入音轨不存在: " + o.Audio); return false; }
            if (o.Lang == "") o.Lang = "zh";

            // whisper GPU 选择: 手动指定索引优先; 自动模式优先 NVIDIA 独显 (多显卡笔记本核显默认在枚举第一位)
            int gpuIdx = o.GpuIndex;
            string gpuMode = (o.GpuIndex >= 0) ? "手动" : "自动";
            if (gpuIdx < 0) { gpuIdx = VkUtil.FindNvidiaVkIndex(); }
            if (gpuIdx >= 0)
            {
                Environment.SetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES", gpuIdx.ToString());
                Log(o, string.Format("whisper GPU: {0}选择 Vulkan 设备 #{1}", gpuMode, gpuIdx));
            }
            else
            {
                Log(o, "whisper GPU: 未指定可用 Vulkan 设备, 使用系统默认");
            }

            try { Directory.CreateDirectory(o.TmpDir); }
            catch { }

            // ---- 输入归一化: 非 wav 先转 16k mono wav ----
            // whisper 内置 miniaudio 解码器对部分 mp3/m4a (如抖音下载的 AAC 伪装 mp3) 解码失败且返回 0,
            // 统一先经 ffmpeg 转码, 保证 VAD/识别永远吃 wav
            string aExt = Path.GetExtension(o.Audio).ToLowerInvariant();
            if (aExt != ".wav")
            {
                string convWav = Path.Combine(o.TmpDir, "input_norm.wav");
                if (!File.Exists(convWav))
                {
                    Log(o, "阶段0/5 输入非 wav, ffmpeg 转码 16k mono: " + o.Audio);
                    string cargs = string.Format("-y -i \"{0}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{1}\"",
                        SrtUtil.ShortPath(o.Audio), SrtUtil.ShortPathForWrite(convWav));
                    int crc = SrtUtil.RunProcRedirect(ffmpeg, cargs,
                        Path.Combine(o.TmpDir, "conv_out.txt"),
                        Path.Combine(o.TmpDir, "conv_err.txt"), o.Cancel, 300000);
                    if (crc != 0 || !File.Exists(convWav))
                    {
                        Log(o, "错误: 输入音频转码失败 (ffmpeg exit " + crc + ")");
                        return false;
                    }
                }
                o.Audio = convWav;
                Log(o, "已转码: " + convWav);
            }

            // ---- 总时长 ----
            double totalSec = SrtUtil.ProbeDuration(ffmpeg, o.Audio);
            if (totalSec <= 0) { Log(o, "错误: 无法解析音频时长: " + o.Audio); return false; }
            Log(o, string.Format("输入音轨: {0}  总时长 {1:N0}s ({2:N1} 分钟)", o.Audio, totalSec, totalSec / 60));

            // ---- VAD 可选: false = 整轨直推 whisper (不切片, 时间戳相对全片) ----
            if (!o.VadEnabled)
            {
                return RunFullAudio(o, whisper, model, t2sPath: Path.Combine(o.Root, "tools", "TSCharacters.txt"));
            }

            string vadLogPath = Path.Combine(o.TmpDir, "vad.log");
            List<double[]> segs = new List<double[]>();

            // ---- VAD 检测 (已有结果复用) ----
            if (File.Exists(vadLogPath))
            {
                Log(o, "步骤 1/4: VAD (Resume 模式复用已有结果)");
            }
            else
            {
                Log(o, "步骤 1/4: VAD 检测语音段开始...");
                DateTime tVad = DateTime.Now;
                string vadOut = Path.Combine(o.TmpDir, "vad_out");

                // 长音频 + 并行>1: 音频均分成多段, 各段独立 whisper VAD 进程并发跑, 时间戳偏移后拼回统一 vad.log
                // (下游切片/识别与断点续跑逻辑不变, 仅 VAD 阶段提速)
                int parts = (o.Parallel >= 2 && totalSec > 120) ? Math.Min(o.Parallel, 4) : 1;
                if (parts == 1)
                {
                    string vargs = string.Format("-m \"{0}\" -f \"{1}\" -l {2} --vad --vad-model \"{3}\" -vsd {4} -vt {5} -vspd {6} -t {7} -otxt -of \"{8}\"",
                        model, SrtUtil.ShortPath(o.Audio), o.Lang, vadModel, VadSilence,
                        o.VadThreshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                        o.VadMinSpeech, o.Threads, SrtUtil.ShortPathForWrite(vadOut));
                    int rc = SrtUtil.RunProcRedirect(whisper, vargs, vadLogPath, Path.Combine(o.TmpDir, "vad_err.txt"), o.Cancel, 600000);
                    if (rc != 0) { Log(o, "错误: VAD 检测失败 (whisper exit " + rc + ")"); return false; }
                }
                else
                {
                    Log(o, string.Format("VAD 分段并发: 切成 {0} 段同时检测 (总 {1:N0}s)", parts, totalSec));
                    double segLen = totalSec / parts;
                    int[] rcs = new int[parts];
                    List<Thread> vts = new List<Thread>();
                    for (int i = 0; i < parts; i++)
                    {
                        int idx = i;
                        Thread t = new Thread(delegate()
                        {
                            try
                            {
                                string segWav = Path.Combine(o.TmpDir, "vad_seg_" + idx + ".wav");
                                double st = idx * segLen;
                                double len = Math.Min(segLen, totalSec - st);
                                // ffmpeg 切段 (保持原采样/声道, whisper 自行处理)
                                string cargs = string.Format("-v error -y -ss {0:0.###} -t {1:0.###} -i \"{2}\" -c:a pcm_s16le \"{3}\"",
                                    st, len, SrtUtil.ShortPath(o.Audio), SrtUtil.ShortPathForWrite(segWav));
                                System.Diagnostics.ProcessStartInfo pfi = new System.Diagnostics.ProcessStartInfo(ffmpeg, cargs);
                                pfi.UseShellExecute = false;
                                pfi.CreateNoWindow = true;
                                using (System.Diagnostics.Process pp = System.Diagnostics.Process.Start(pfi))
                                {
                                    // ffmpeg 切段: 轮询退出, 用户停止时立即 Kill
                                    while (!pp.WaitForExit(200))
                                    {
                                        if (o.Cancel != null && o.Cancel()) { try { pp.Kill(); } catch { } break; }
                                    }
                                }
                                string vargs = string.Format("-m \"{0}\" -f \"{1}\" -l {2} --vad --vad-model \"{3}\" -vsd {4} -vt {5} -vspd {6} -t {7} -otxt -of \"{8}\"",
                                    model, SrtUtil.ShortPath(segWav), o.Lang, vadModel, VadSilence,
                                    o.VadThreshold.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                                    o.VadMinSpeech, o.Threads, SrtUtil.ShortPathForWrite(Path.Combine(o.TmpDir, "vad_seg_" + idx)));
                                rcs[idx] = SrtUtil.RunProcRedirect(whisper, vargs,
                                    Path.Combine(o.TmpDir, "vad_seg_" + idx + ".log"),
                                    Path.Combine(o.TmpDir, "vad_seg_" + idx + "_err.txt"), o.Cancel, 600000);
                            }
                            catch (Exception ex) { rcs[idx] = -2; Log(o, "VAD 段 " + idx + " 异常: " + ex.Message); }
                        });
                        vts.Add(t); t.Start();
                    }
                    foreach (Thread t in vts) t.Join();
                    bool okAll = true;
                    List<string> allLog = new List<string>();
                    List<string> allErr = new List<string>();
                    for (int i = 0; i < parts; i++)
                    {
                        if (rcs[i] != 0) { okAll = false; Log(o, "VAD 段 " + i + " 失败 (exit " + rcs[i] + ")"); continue; }
                        double off = i * segLen;
                        string sfx = Path.Combine(o.TmpDir, "vad_seg_" + i);
                        foreach (string ln in File.ReadAllLines(sfx + ".log", Encoding.UTF8)) allLog.Add(OffsetVadLine(ln, off));
                        foreach (string ln in File.ReadAllLines(sfx + "_err.txt", Encoding.UTF8)) allErr.Add(OffsetVadLine(ln, off));
                    }
                    if (!okAll) { Log(o, "错误: VAD 分段检测失败"); return false; }
                    File.WriteAllLines(vadLogPath, allLog, Encoding.UTF8);
                    File.WriteAllLines(Path.Combine(o.TmpDir, "vad_err.txt"), allErr, Encoding.UTF8);
                    for (int i = 0; i < parts; i++)
                    {
                        try { File.Delete(Path.Combine(o.TmpDir, "vad_seg_" + i + ".wav")); } catch { }
                        try { File.Delete(Path.Combine(o.TmpDir, "vad_seg_" + i + ".log")); } catch { }
                        try { File.Delete(Path.Combine(o.TmpDir, "vad_seg_" + i + "_err.txt")); } catch { }
                    }
                }
                Log(o, string.Format("步骤 1/4 VAD 完成, 用时 {0:N1}s", (DateTime.Now - tVad).TotalSeconds));
            }

            // 解析 "Including segment N: start - end" (whisper 打在 stderr, 合并读两个文件)
            try
            {
                string[] vadFiles = new string[] { vadLogPath, Path.Combine(o.TmpDir, "vad_err.txt") };
                foreach (string vf in vadFiles)
                {
                    if (!File.Exists(vf)) continue;
                    foreach (string ln in File.ReadAllLines(vf, Encoding.UTF8))
                    {
                        Match m = Regex.Match(ln, "Including segment\\s+\\d+:\\s+([\\d.]+)\\s*-\\s*([\\d.]+)");
                        if (m.Success)
                            segs.Add(new double[] { double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                                     double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) });
                    }
                }
            }
            catch (Exception ex) { Log(o, "错误: 读取 VAD 结果失败: " + ex.Message); return false; }
            if (segs.Count == 0) { Log(o, "错误: 未解析到任何语音段"); return false; }

            double speechSec = 0;
            foreach (double[] s in segs) speechSec += s[1] - s[0];
            Log(o, string.Format("检测到 {0} 个语音段 (语音 {1:N1}s / 总 {2:N0}s)", segs.Count, speechSec, totalSec));

            // ---- 合并相邻段为区间 ----
            List<double[]> ints = new List<double[]>();
            double cs = segs[0][0], ce = segs[0][1];
            for (int i = 1; i < segs.Count; i++)
            {
                if (segs[i][0] - ce <= MergeGap) ce = segs[i][1];
                else { ints.Add(new double[] { cs, ce }); cs = segs[i][0]; ce = segs[i][1]; }
            }
            ints.Add(new double[] { cs, ce });
            double maxLen = 0;
            for (int i = 0; i < ints.Count; i++)
            {
                ints[i][0] = Math.Max(0.0, ints[i][0] - PadExt);
                ints[i][1] = Math.Min(totalSec, ints[i][1] + PadExt);
                if (ints[i][1] - ints[i][0] > maxLen) maxLen = ints[i][1] - ints[i][0];
            }
            Log(o, string.Format("合并为 {0} 个语音区间 (最长 {1:N1}s)", ints.Count, maxLen));

            // ---- 区间切片 (已有 wav 跳过) ----
            Log(o, "步骤 2/4: 区间切片开始...");
            DateTime tSlice = DateTime.Now;
            List<string> segWavs = new List<string>();
            for (int i = 0; i < ints.Count; i++)
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, "已取消"); return false; }
                string sw = Path.Combine(o.TmpDir, string.Format("seg_{0:D5}.wav", i));
                if (File.Exists(sw)) { segWavs.Add(sw); continue; }
                string fargs = string.Format("-hide_banner -y -ss {0:F3} -t {1:F3} -i \"{2}\" -ac 1 -ar 16000 -c:a pcm_s16le \"{3}\"",
                    ints[i][0], ints[i][1] - ints[i][0], o.Audio, sw);
                int rc = SrtUtil.RunProcRedirect(ffmpeg, fargs,
                    Path.Combine(o.TmpDir, "slice_out.txt"), Path.Combine(o.TmpDir, "slice_err.txt"), o.Cancel, 60000);
                if (rc != 0) { Log(o, "错误: 切片失败 seg " + i + " (exit " + rc + ")"); return false; }
                segWavs.Add(sw);
            }
            Log(o, string.Format("步骤 2/4 切片完成, 用时 {0:N1}s, {1} 个切片", (DateTime.Now - tSlice).TotalSeconds, segWavs.Count));

            // ---- 分批识别 (每批 BatchSize 区间, Parallel 实例并行) ----
            Log(o, string.Format("步骤 3/4: GPU 分批识别开始 (每批 {0} 区间, {1} 实例并行)...", BatchSize, o.Parallel));
            DateTime tRec = DateTime.Now;
            int total = segWavs.Count;
            for (int b = 0; b < total; b += BatchSize)
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, "已取消"); return false; }

                // 本批未识别区间 (seg_NNNNN.wav.srt 存在即已完成)
                List<string> todo = new List<string>();
                int hi = Math.Min(b + BatchSize, total);
                for (int k = b; k < hi; k++)
                {
                    string tf = segWavs[k] + ".srt";
                    if (File.Exists(tf)) continue;
                    todo.Add(segWavs[k]);
                }
                if (todo.Count == 0)
                {
                    Log(o, string.Format("  进度: {0}/{1} (本批已识别, 跳过)", hi, total));
                    continue;
                }

                // 轮询分组
                List<List<string>> groups = new List<List<string>>();
                for (int g = 0; g < o.Parallel; g++) groups.Add(new List<string>());
                for (int i = 0; i < todo.Count; i++) groups[i % o.Parallel].Add(todo[i]);

                List<System.Diagnostics.Process> procs = new List<System.Diagnostics.Process>();
                List<int> procGroups = new List<int>();
                for (int g = 0; g < o.Parallel; g++)
                {
                    List<string> grp = groups[g];
                    if (grp.Count == 0) continue;
                    string of = Path.Combine(o.TmpDir, string.Format("par_{0:D2}", g));
                    StringBuilder a = new StringBuilder();
                    a.Append("-m \"").Append(model).Append("\"");
                    a.Append(" -l ").Append(o.Lang);
                    a.Append(" -mc 0 -t ").Append(o.Threads);
                    a.Append(" -osrt -of \"").Append(SrtUtil.ShortPathForWrite(of)).Append("\"");
                    foreach (string f in grp) a.Append(" \"").Append(SrtUtil.ShortPath(f)).Append("\"");
                    string wlog = Path.Combine(o.TmpDir, string.Format("wlog_{0:D2}.txt", g));
                    string werr = Path.Combine(o.TmpDir, string.Format("werr_{0:D2}.txt", g));
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(whisper, a.ToString());
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    try
                    {
                        System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                        // 异步复制流到文件 (保持 whisper UTF-8 字节原样, 防 GBK 乱码)
                        CopyAsync(p.StandardOutput.BaseStream, wlog);
                        CopyAsync(p.StandardError.BaseStream, werr);
                        procs.Add(p);
                        procGroups.Add(g);
                    }
                    catch (Exception ex) { Log(o, "错误: 启动识别进程失败 组 " + g + ": " + ex.Message); return false; }
                }

                // 等待全部完成 (带取消检查)
                foreach (System.Diagnostics.Process p in procs)
                {
                    while (!p.WaitForExit(1000))
                    {
                        if (o.Cancel != null && o.Cancel())
                        {
                            foreach (System.Diagnostics.Process q in procs)
                            { try { if (!q.HasExited) q.Kill(); } catch { } }
                            Log(o, "已取消"); return false;
                        }
                    }
                }
                foreach (System.Diagnostics.Process p in procs)
                {
                    if (p.ExitCode != 0) { Log(o, "错误: 识别失败 批次 " + b + " (退出码 " + p.ExitCode + ")"); return false; }
                }

                // 归位: 每组首区间的输出 par_XX.srt -> grp[0].wav.srt
                // (whisper 多文件: 第一个文件输出到 -of 前缀.srt, 其余文件直接写 <输入文件>.srt;
                //  输入传的是短路径, 输出在短路径位置, 全部复制回原路径)
                for (int g = 0; g < o.Parallel; g++)
                {
                    List<string> grp = groups[g];
                    if (grp.Count == 0) continue;
                    string of = Path.Combine(o.TmpDir, string.Format("par_{0:D2}", g));
                    string bsrt = SrtUtil.ShortPathForWrite(of) + ".srt";
                    if (File.Exists(bsrt))
                    {
                        string dest = grp[0] + ".srt";
                        if (!File.Exists(dest))
                        {
                            try { File.Copy(bsrt, dest); }
                            catch (Exception ex) { Log(o, "警告: 归位失败 " + bsrt + ": " + ex.Message); }
                        }
                    }
                    for (int k = 1; k < grp.Count; k++)
                    {
                        string src = SrtUtil.ShortPath(grp[k]) + ".srt";
                        if (File.Exists(src) && !File.Exists(grp[k] + ".srt"))
                        {
                            try { File.Copy(src, grp[k] + ".srt"); }
                            catch (Exception ex) { Log(o, "警告: 归位失败 " + src + ": " + ex.Message); }
                        }
                    }
                }
                Log(o, string.Format("  进度: {0}/{1}", hi, total));
            }
            Log(o, string.Format("步骤 3/4 识别完成, 用时 {0:N1}s", (DateTime.Now - tRec).TotalSeconds));

            // ---- 汇总 SRT (区间起点 + 块内时间) ----
            Dictionary<string, string> t2s = SrtUtil.LoadT2S(Path.Combine(o.Root, "tools", "TSCharacters.txt"));
            Log(o, "已加载繁转简映射 " + t2s.Count + " 字");

            StringBuilder sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < total; i++)
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, "已取消"); return false; }
                string tf = segWavs[i] + ".srt";
                if (!File.Exists(tf)) continue;
                double offset = ints[i][0];
                string[] lines = File.ReadAllLines(tf, Encoding.UTF8);
                for (int j = 0; j < lines.Length; )
                {
                    if (lines[j].Trim() == "") { j++; continue; }
                    Match idx = Regex.Match(lines[j].Trim(), "^[\\d.]+$");
                    if (idx.Success && j + 2 < lines.Length)
                    {
                        Match tm = Regex.Match(lines[j + 1],
                            "(\\d+):(\\d+):(\\d+),(\\d+)\\s*-->\\s*(\\d+):(\\d+):(\\d+),(\\d+)");
                        if (tm.Success)
                        {
                            double t1 = offset + double.Parse(tm.Groups[1].Value) * 3600
                                      + double.Parse(tm.Groups[2].Value) * 60
                                      + double.Parse(tm.Groups[3].Value)
                                      + double.Parse(tm.Groups[4].Value) / 1000.0;
                            double t2 = offset + double.Parse(tm.Groups[5].Value) * 3600
                                      + double.Parse(tm.Groups[6].Value) * 60
                                      + double.Parse(tm.Groups[7].Value)
                                      + double.Parse(tm.Groups[8].Value) / 1000.0;
                            string txt = SrtUtil.ConvertT2S(lines[j + 2].Trim(), t2s);
                            // 断行仅对中文生效 (英文按字符断会截断单词, 翻译后的中文由 LlmPost 再断行)
                            if (o.Lang == "zh") txt = SrtUtil.SplitLongLine(txt, 20, 26);
                            j += 4;
                            if (txt == "") continue;
                            n++;
                            sb.AppendLine(n.ToString());
                            sb.AppendLine(SrtUtil.FmtTime(t1) + " --> " + SrtUtil.FmtTime(t2));
                            sb.AppendLine(txt);
                            sb.AppendLine("");
                            continue;
                        }
                    }
                    j++;
                }
            }
            try
            {
                File.WriteAllText(o.Out, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log(o, "错误: 写入字幕失败: " + ex.Message); return false; }
            Log(o, string.Format("步骤 4/4 汇总完成: {0} 条字幕 -> {1}", n, o.Out));
            return true;
        }

        static void Log(Options o, string msg)
        {
            if (o.Log != null) o.Log(msg);
        }

        // VAD 分段结果时间戳偏移: "Including segment N: start - end" 中 start/end 加 offset 秒 (段起点)
        static string OffsetVadLine(string ln, double off)
        {
            Match m = Regex.Match(ln, "(Including segment\\s+\\d+:\\s+)([\\d.]+)(\\s*-\\s*)([\\d.]+)");
            if (!m.Success) { return ln; }
            double a = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + off;
            double b = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) + off;
            return m.Groups[1].Value + a.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                 + m.Groups[3].Value + b.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // ---- VAD 关闭: 整轨直推 whisper, 输出 srt 直接繁转简/断行 ----
        // 断点续跑: tmp\full.srt 存在即跳过识别 (指纹含 EnableVad, 与切片模式互不复用)

        // 用 ffprobe 取音频总时长(秒), 用于整轨识别进度百分比; 失败返回 0 (退化为仅打时间)
        static double GetAudioDuration(string wav, string root)
        {
            try
            {
                string ff = Path.Combine(root, "tools", "ffmpeg", "bin", "ffprobe.exe");
                if (!File.Exists(ff)) { return 0; }
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(ff,
                    string.Format("-v error -show_entries format=duration -of csv=p=0 \"{0}\"", SrtUtil.ShortPath(wav)));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    string s = p.StandardOutput.ReadToEnd().Trim();
                    if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } }
                    double d;
                    if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out d)) { return d; }
                    return 0;
                }
            }
            catch { return 0; }
        }

        static bool RunFullAudio(Options o, string whisper, string model, string t2sPath)
        {
            Log(o, "步骤 1/2: 整轨直推 whisper 识别 (VAD 关闭, 不切片)...");
            string fullOf = Path.Combine(o.TmpDir, "full");
            string fullSrt = SrtUtil.ShortPathForWrite(fullOf) + ".srt";
            string wlog = Path.Combine(o.TmpDir, "full_out.txt");
            string werr = Path.Combine(o.TmpDir, "full_err.txt");

            if (!File.Exists(fullSrt))
            {
                DateTime tRec = DateTime.Now;
                // 整轨长音频识别耗时可能很长, 超时放宽到 2 小时
                string fargs = string.Format("-m \"{0}\" -l {1} -mc 0 -t {2} -f \"{3}\" -osrt -of \"{4}\"",
                    model, o.Lang, o.Threads, SrtUtil.ShortPath(o.Audio), SrtUtil.ShortPathForWrite(fullOf));
                // 实时进度: 后台线程读 whisper 输出文件尾部, 解析识别段时间 -> 按百分比/间隔打日志
                double durSec = GetAudioDuration(o.Audio, o.Root);
                bool done = false;
                int lastPct = -1;
                DateTime lastLog = DateTime.Now;
                Thread monitor = new Thread(delegate()
                {
                    long lastLen = 0;
                    while (!done)
                    {
                        bool hadData = false;
                        try
                        {
                            if (File.Exists(wlog))
                            {
                                using (FileStream fs = new FileStream(wlog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                                {
                                    if (fs.Length > lastLen)
                                    {
                                        hadData = true;
                                        fs.Seek(lastLen, SeekOrigin.Begin);
                                        byte[] b = new byte[fs.Length - lastLen];
                                        int rd = fs.Read(b, 0, b.Length);
                                        lastLen += rd;
                                        string chunk = Encoding.UTF8.GetString(b, 0, rd);
                                        // 行可能被截断, 回退到最后一个换行符之后的部分, 留到下次一起处理
                                        int cut = chunk.LastIndexOf('\n');
                                        if (cut >= 0) { lastLen -= (chunk.Length - cut - 1); }
                                        // 攒批输出时一批段行同时到达: 只取最后一条(最新进度)打日志, 不逐条刷屏
                                        int lastSecOfBatch = -1;
                                        double lastPctOfBatch = -1;
                                        foreach (Match m in Regex.Matches(chunk, "-->\\s+(\\d+):(\\d+):(\\d+)"))
                                        {
                                            lastSecOfBatch = int.Parse(m.Groups[1].Value) * 3600
                                                + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
                                            lastPctOfBatch = durSec > 0 ? (lastSecOfBatch * 100.0 / durSec) : -1;
                                        }
                                        if (lastSecOfBatch >= 0)
                                        {
                                            bool pctHit = lastPctOfBatch >= 0 && ((int)lastPctOfBatch) / 10 > lastPct / 10;
                                            bool timeHit = (DateTime.Now - lastLog).TotalSeconds >= 60;
                                            if (pctHit || timeHit)
                                            {
                                                if (lastPctOfBatch >= 0) { lastPct = (int)lastPctOfBatch; }
                                                lastLog = DateTime.Now;
                                                if (lastPctOfBatch >= 0)
                                                {
                                                    Log(o, string.Format("识别进度: {0,3}%  已识别到 {1:00}:{2:00}:{3:00} / {4:00}:{5:00}:{6:00}",
                                                        (int)lastPctOfBatch, lastSecOfBatch / 3600, (lastSecOfBatch % 3600) / 60, lastSecOfBatch % 60,
                                                        (int)(durSec / 3600), (int)(durSec % 3600) / 60, (int)(durSec % 60)));
                                                }
                                                else
                                                {
                                                    Log(o, string.Format("识别中... 已识别到 {0:00}:{1:00}:{2:00}",
                                                        lastSecOfBatch / 3600, (lastSecOfBatch % 3600) / 60, lastSecOfBatch % 60));
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        // 保底心跳: whisper 攒批输出期间长时间无段行增量, 每 45 秒打一条防误判死机
                        if (!hadData && !done && (DateTime.Now - lastLog).TotalSeconds >= 45)
                        {
                            lastLog = DateTime.Now;
                            Log(o, string.Format("识别中... 已用时 {0:N0}s (等待 whisper 输出)", (DateTime.Now - tRec).TotalSeconds));
                        }
                        Thread.Sleep(300);
                    }
                });
                monitor.IsBackground = true;
                monitor.Start();
                int rc = SrtUtil.RunProcRedirect(whisper, fargs, wlog, werr, o.Cancel, 7200000);
                done = true;
                monitor.Join(3000);
                if (rc != 0)
                {
                    Log(o, "错误: 整轨识别失败 (whisper exit " + rc + ")" + (rc == -999 ? " [超时或取消]" : ""));
                    return false;
                }
                Log(o, string.Format("步骤 1/2 整轨识别完成, 用时 {0:N1}s", (DateTime.Now - tRec).TotalSeconds));
            }
            else
            {
                Log(o, "步骤 1/2: 整轨识别 (Resume 模式复用已有结果)");
            }
            if (!File.Exists(fullSrt)) { Log(o, "错误: 未找到识别输出 " + fullSrt); return false; }

            Dictionary<string, string> t2s = SrtUtil.LoadT2S(t2sPath);
            Log(o, "已加载繁转简映射 " + t2s.Count + " 字");

            StringBuilder sb = new StringBuilder();
            int n = 0;
            string[] lines = File.ReadAllLines(fullSrt, Encoding.UTF8);
            for (int j = 0; j < lines.Length; )
            {
                if (lines[j].Trim() == "") { j++; continue; }
                Match idx = Regex.Match(lines[j].Trim(), "^[\\d.]+$");
                if (idx.Success && j + 2 < lines.Length)
                {
                    Match tm = Regex.Match(lines[j + 1],
                        "(\\d+):(\\d+):(\\d+),(\\d+)\\s*-->\\s*(\\d+):(\\d+):(\\d+),(\\d+)");
                    if (tm.Success)
                    {
                        string txt = SrtUtil.ConvertT2S(lines[j + 2].Trim(), t2s);
                        if (o.Lang == "zh") txt = SrtUtil.SplitLongLine(txt, 20, 26);
                        j += 4;
                        if (txt == "") continue;
                        n++;
                        sb.AppendLine(n.ToString());
                        sb.AppendLine(lines[j - 3].Trim());
                        sb.AppendLine(txt);
                        sb.AppendLine("");
                        continue;
                    }
                }
                j++;
            }
            try
            {
                File.WriteAllText(o.Out, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log(o, "错误: 写入字幕失败: " + ex.Message); return false; }
            Log(o, string.Format("步骤 2/2 汇总完成: {0} 条字幕 -> {1}", n, o.Out));
            return true;
        }

        static void CopyAsync(System.IO.Stream src, string destFile)
        {
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                try
                {
                    using (FileStream fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                    { src.CopyTo(fs); }
                }
                catch { }
                finally { try { src.Dispose(); } catch { } }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
