// LlmPost.cs - SRT 文本 LLM 后处理 (替代 srt_llm.ps1)
// 读 SRT -> 按批带上下文送本地 Ollama (/api/chat) -> 回填修正/译文 -> 重新断行 -> 写回
// 模式: refine=纠错(中文) / translate=翻译(其他语言->中文)

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SrtGui
{
    public static class LlmPost
    {
        public class Options
        {
            public string Srt;
            public string Out;
            public string Model = "qwen3-14b:latest";
            public string Mode = "translate";   // translate / refine
            public string Backend = "ollama";   // ollama / openai / llama (本地引擎)
            public string ApiKey = "";          // OpenAI 兼容端点密钥 (可选)
            public int Batch = 40;
            public string BaseUrl = "http://127.0.0.1:11434";
            public int TimeoutSec = 60;     // 单次请求超时 (秒, GUI 可配置)
            public int Retries = 4;          // 单批最大尝试次数 (GUI 可配置)
            public string LogFile = "";
            public Action<string> Log;
            public Func<bool> Cancel;
            public volatile HttpWebRequest ActiveRequest;   // 当前进行中的请求 (供 Stop 中断)
            public bool AddPunct;                            // true=翻译时补齐目标语言标点 (纯文本输出需要)
            public string TgtLang = "zh";                    // 目标语言 (翻译成什么: zh/en/ja/...) 
            public bool BreakLine = true;                    // 长字幕断行 (仅中文目标生效, 英文按字符断会切断单词)
        }

        class Block
        {
            public int Num;
            public string Time;
            public List<string> Texts = new List<string>();
            public bool Skip;                               // 无意义噪声行: 不送模型, 保留原文
            public bool Translated;                         // 已完成 LLM 翻译 (断点续传: 中断重跑时跳过)
        }

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly UTF8Encoding Utf8Bom = new UTF8Encoding(true);

        // 目标语言码 -> 中文名 (翻译指令用); 未收录的直接用语言码, 模型也能理解
        static string LangName(string code)
        {
            switch (code)
            {
                case "zh": return "简体中文";
                case "yue": return "粤语";
                case "en": return "英语";
                case "ja": return "日语";
                case "ko": return "韩语";
                case "es": return "西班牙语";
                case "fr": return "法语";
                case "de": return "德语";
                case "ru": return "俄语";
                case "it": return "意大利语";
                case "pt": return "葡萄牙语";
                case "ar": return "阿拉伯语";
                case "vi": return "越南语";
                case "th": return "泰语";
                case "id": return "印尼语";
                case "ms": return "马来语";
                case "hi": return "印地语";
                case "ur": return "乌尔都语";
                case "bn": return "孟加拉语";
                case "ta": return "泰米尔语";
                case "tr": return "土耳其语";
                case "nl": return "荷兰语";
                case "sv": return "瑞典语";
                case "da": return "丹麦语";
                case "no": return "挪威语";
                case "fi": return "芬兰语";
                case "pl": return "波兰语";
                case "uk": return "乌克兰语";
                case "he": return "希伯来语";
                case "fa": return "波斯语";
                case "el": return "希腊语";
                case "cs": return "捷克语";
                case "sk": return "斯洛伐克语";
                case "hu": return "匈牙利语";
                case "ro": return "罗马尼亚语";
                case "bg": return "保加利亚语";
                case "sr": return "塞尔维亚语";
                case "hr": return "克罗地亚语";
                case "ca": return "加泰罗尼亚语";
                default: return code;
            }
        }

        public static bool Run(Options o)
        {
            string logPath = o.LogFile;
            if (logPath == "") logPath = Path.ChangeExtension(o.Out, ".log");

            if (!File.Exists(o.Srt)) { Log(o, logPath, "错误: SRT 不存在: " + o.Srt); return false; }
            if (o.Out == "") o.Out = Regex.Replace(o.Srt, @"\.srt$", "_llm.srt");

            // ---- 解析 SRT 为块结构 ----
            List<Block> blocks;
            try { blocks = ParseSrt(o.Srt); }
            catch (Exception ex) { Log(o, logPath, "错误: 读取 SRT 失败: " + ex.Message); return false; }

            int total = blocks.Count;
            if (total == 0) { Log(o, logPath, "错误: SRT 无有效字幕块"); return false; }

            // ---- 断点续传: 读进度文件 (o.Out + ".progress"), 已翻译的块跳过不重翻 ----
            string progressPath = o.Out + ".progress";
            int resumed = 0;
            if (File.Exists(progressPath))
            {
                try
                {
                    HashSet<int> done = new HashSet<int>();
                    foreach (string ln in File.ReadAllLines(progressPath, Utf8NoBom))
                    {
                        int n;
                        if (int.TryParse(ln.Trim(), out n)) done.Add(n);
                    }
                    foreach (Block b in blocks) if (done.Contains(b.Num)) { b.Translated = true; resumed++; }
                }
                catch (Exception ex) { Log(o, logPath, "警告: 读取断点文件失败, 将重新翻译全部: " + ex.Message); }
            }
            if (resumed > 0)
            {
                Log(o, logPath, "检测到断点: " + resumed + " 块已翻译, 跳过续传");
                // 已翻译块必须从上次输出文件读回译文, 否则写盘时会被原文覆盖
                if (File.Exists(o.Out))
                {
                    try
                    {
                        Dictionary<int, Block> saved = new Dictionary<int, Block>();
                        foreach (Block sb in ParseSrt(o.Out)) saved[sb.Num] = sb;
                        foreach (Block b in blocks)
                            if (b.Translated && saved.ContainsKey(b.Num))
                                b.Texts = new List<string>(saved[b.Num].Texts);
                    }
                    catch (Exception ex)
                    {
                        Log(o, logPath, "警告: 读回上次译文失败, 相关块将重新翻译: " + ex.Message);
                        foreach (Block b in blocks) b.Translated = false;
                        resumed = 0;
                    }
                }
            }

            // ---- 清洗: 连续相同字符 >=20 的无意义噪声行 (如 whisper 把杂音转成 Aaaa...) 不送模型, 保留原文 ----
            int skipClean = 0;
            foreach (Block b in blocks)
            {
                string t = string.Join("", b.Texts.ToArray());
                if (MaxRunLen(t) >= 20)
                {
                    b.Skip = true;
                    skipClean++;
                    Log(o, logPath, "块 " + b.Num + " 跳过 LLM (连续相同字符 >=20), 保留原文");
                }
            }

            // 指令放 user 前缀而非 system: 翻译专用模型(如 Hy-MT2)对长 system 指令易角色漂移(输出分析/待命回复)
            string sysPrompt = (o.Mode == "translate")
                ? "你是专业影视字幕翻译。"
                : "你是电影字幕校对员。";
            string userPrefix = (o.Mode == "translate")
                ? (o.AddPunct
                    ? "将以下文本翻译为" + LangName(o.TgtLang) + "，注意只需要输出翻译后的结果，不要额外解释。翻译时请在每句末尾补上合适的目标语言标点（句号、逗号、问号等）。请按原文序号逐条输出，格式为 [序号] 译文："
                    : "将以下文本翻译为" + LangName(o.TgtLang) + "，注意只需要输出翻译后的结果，不要额外解释。请按原文序号逐条输出，格式为 [序号] 译文：")
                : "请逐条校对下面的字幕, 只输出 [序号] 修正文本, 没有问题的行原样输出, 不要任何解释:";

            Log(o, logPath, "========================================");
            Log(o, logPath, "开始: " + o.Srt + "  (共 " + total + " 块)");
            Log(o, logPath, "模式: " + o.Mode + "  模型: " + o.Model + "  批大小: " + o.Batch + "  范围: 块 1-" + total);
            Log(o, logPath, "输出: " + o.Out);
            Log(o, logPath, "========================================");

            // ---- LLM 连通性验证 (首次请求可能需把模型加载进显存, 预先让模型就绪) ----
            Log(o, logPath, "测试 LLM 状态: " + o.Model + " ...");
            DateTime tTest = DateTime.Now;
            bool testOk = false;
            string testResp = "";
            for (int t = 1; t <= 2 && !testOk; t++)
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "已取消"); return false; }
                try { testResp = InvokeOllama(o, "你是测试助手。", "只回复两个字: 正常", 120000); testOk = true; }
                catch (Exception ex)
                {
                    Log(o, logPath, "LLM 测试第 " + t + " 次失败: " + ex.Message);
                    if (t < 2) System.Threading.Thread.Sleep(3000);
                }
            }
            if (!testOk)
            {
                Log(o, logPath, "错误: LLM 不可用 (" + o.Model + "), 请确认 Ollama 已启动、模型已下载且地址正确: " + o.BaseUrl);
                return false;
            }
            Log(o, logPath, string.Format("LLM 状态正常 (模型已就绪, 用时 {0:N1}s), 开始翻译字幕...", (DateTime.Now - tTest).TotalSeconds));

            DateTime t0 = DateTime.Now;
            int fixCount = 0, keepCount = 0, failCount = 0;

            // ---- 主循环: 按批送模型, 失败自动拆半重试; 每批完成后即时存盘(含断点文件) ----
            for (int i = 0; i < total; )
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "已取消"); return false; }
                int j = Math.Min(total, i + o.Batch);
                ProcessBatch(o, logPath, sysPrompt, userPrefix, blocks, i, j, ref fixCount, ref keepCount, ref failCount);
                // 即时存盘: 已翻译的块写译文, 未翻译的保留原文; 进度文件同步 (先 SRT 后 progress, 崩溃安全)
                try { WriteSrt(o.Out, blocks); WriteProgress(progressPath, blocks); }
                catch (Exception ex) { Log(o, logPath, "警告: 即时存盘失败: " + ex.Message); }
                // ProcessBatch 内部可能因取消提前返回; 此处再检查一次, 中断时保留断点文件供续传
                if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "已取消 (已翻译部分已存盘, 重跑可续传)"); return false; }
                i = j;
            }

            // ---- 残留外语补翻 (仅 translate 且目标中文): 残留检测以汉字为基准, 非中文目标无法通用判定, 跳过 ----
            if (o.Mode == "translate" && o.TgtLang == "zh")
            {
                int refixed = RefixForeign(o, logPath, sysPrompt, userPrefix, blocks);
                if (refixed > 0)
                    Log(o, logPath, string.Format("残留补翻: 共补翻 {0} 块外语残留", refixed));
                else
                    Log(o, logPath, "残留补翻: 未检测到外语残留");
            }

            // ---- 写回 SRT (UTF-8 带 BOM); 完整跑完即删除断点文件 (下次全新翻译) ----
            try { WriteSrt(o.Out, blocks); }
            catch (Exception ex) { Log(o, logPath, "错误: 写入字幕失败: " + ex.Message); return false; }
            try { if (File.Exists(progressPath)) File.Delete(progressPath); }
            catch { }

            Log(o, logPath, "========================================");
            Log(o, logPath, "完成: 总 " + total + " 块, 处理 " + total + " 块, 修正 " + fixCount + ", 保持 " + keepCount + ", 失败 " + failCount);
            Log(o, logPath, string.Format("总用时 {0:N1}s, 输出: {1}", (DateTime.Now - t0).TotalSeconds, o.Out));
            Log(o, logPath, "========================================");
            return true;
        }

        // 送一批字幕给模型并回填; 整批失败且批>1 时拆成两半递归重试 (最小粒度单条)
        static void ProcessBatch(Options o, string logPath, string sysPrompt, string userPrefix, List<Block> blocks, int i, int j,
            ref int fixCount, ref int keepCount, ref int failCount)
        {
            DateTime tb = DateTime.Now;

            // 构造本批输入 (合并多行为连续文本; 跳过噪声行和已翻译行)
            List<string> batchLines = new List<string>();
            for (int k = i; k < j; k++)
            {
                if (blocks[k].Skip || blocks[k].Translated) { continue; }
                string t = string.Join("", blocks[k].Texts.ToArray());
                batchLines.Add("[" + blocks[k].Num + "] " + t);
            }
            if (batchLines.Count == 0) { return; }   // 本批全部已处理 (断点续传跳过)
            string user = userPrefix + "\n" + string.Join("\n", batchLines.ToArray());

            // 请求 (失败重试, 最多 o.Retries 次, 间隔 5s; 取消立即退出)
            string respText = "";
            bool ok = false;
            for (int tryN = 1; tryN <= o.Retries && !ok; tryN++)
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 已取消"); return; }
                try { respText = InvokeOllama(o, sysPrompt, user, o.TimeoutSec * 1000); ok = true; }
                catch (Exception ex)
                {
                    if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 已取消"); return; }
                    Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 第 " + tryN + " 次失败: " + ex.Message);
                    if (tryN < o.Retries) System.Threading.Thread.Sleep(5000);
                }
            }

            if (ok)
            {
                // 解析 [N] 译文: 先严格逐行; 命中不足时改用宽松扫描(容忍前缀/漏序号/不换行), 并过滤脱轨词
                Dictionary<int, string> map = ParseResp(respText, true);
                int need = 0;
                for (int k = i; k < j; k++) if (!blocks[k].Skip && !blocks[k].Translated) need++;
                if (need == 0) { return; }
                int needMin = (int)Math.Ceiling(need * 0.5);
                if (map.Count < needMin)
                {
                    map = ParseResp(respText, false);
                    if (map.Count >= needMin)
                        Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 严格解析命中不足, 已用宽松解析");
                }

                int hit = 0;
                for (int k = i; k < j; k++)
                {
                    if (blocks[k].Skip || blocks[k].Translated) { continue; }
                    int nn = blocks[k].Num;
                    string v;
                    if (map.TryGetValue(nn, out v))
                    {
                        // 模型输出常自带碎行(甚至断词): 先折叠为空格; 断行仅对中文目标生效 (英文按字符断会切断单词)
                        string v1 = System.Text.RegularExpressions.Regex.Replace(v, @"\r?\n", " ").Trim();
                        string broken = (o.TgtLang == "zh" && o.BreakLine)
                            ? SrtUtil.SplitLongLine(v1, 20, 26)
                            : v1;
                        blocks[k].Texts = new List<string>(broken.Split(new string[] { "\r\n" }, StringSplitOptions.None));
                        blocks[k].Translated = true;      // 标记已完成, 供即时存盘写断点文件
                        hit++;
                    }
                }
                fixCount += hit;
                keepCount += (j - i - hit);
                if (hit < needMin)
                {
                    // 命中不足: 疑似模型脱轨(输出分析/待命回复而非译文), 未命中的块不标记, 降级重试
                    // (fixCount/keepCount 已累加的部分由降级递归按实际结果修正: 已回填块被跳过, 未回填块在递归中重新统计)
                    Log(o, logPath, string.Format("字幕{0}-{1}行/{5} 命中不足({2}/{3}, 疑似模型脱轨), 降级重试",
                        i + 1, j, hit, need, (DateTime.Now - tb).TotalSeconds, blocks.Count));
                    DegradeRetry(o, logPath, sysPrompt, userPrefix, blocks, i, j, ref fixCount, ref keepCount, ref failCount);
                    return;
                }
                Log(o, logPath, string.Format("字幕{0}-{1}行/{5} 完成: 修正/回填 {2} 条, 保持 {3} 条, 用时 {4:N1}s",
                    i + 1, j, hit, j - i - hit, (DateTime.Now - tb).TotalSeconds, blocks.Count));
            }
            else
            {
                // 整批失败 (重试次数用尽): 降级重试, 不无限拆半
                //  批 >10 -> 10 条一批; 10 条内仍失败 -> 单条; 单条失败 -> 保留原文 (最多跳过一条)
                DegradeRetry(o, logPath, sysPrompt, userPrefix, blocks, i, j, ref fixCount, ref keepCount, ref failCount);
            }
        }

        // 降级重试: 批>10 -> 10条一批; >1 -> 单条; 单条失败 -> 保留原文 (最多跳过一条)
        static void DegradeRetry(Options o, string logPath, string sysPrompt, string userPrefix, List<Block> blocks, int i, int j,
            ref int fixCount, ref int keepCount, ref int failCount)
        {
            if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 已取消"); return; }
            int n = j - i;
            if (n > 10)
            {
                Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 失败, 降级为 10 条一批重试");
                for (int s = i; s < j; s += 10)
                {
                    ProcessBatch(o, logPath, sysPrompt, userPrefix, blocks, s, Math.Min(j, s + 10), ref fixCount, ref keepCount, ref failCount);
                }
            }
            else if (n > 1)
            {
                Log(o, logPath, "字幕" + (i + 1) + "-" + j + "行/" + blocks.Count + " 失败, 降级为单条重试");
                for (int s = i; s < j; s++)
                {
                    ProcessBatch(o, logPath, sysPrompt, userPrefix, blocks, s, s + 1, ref fixCount, ref keepCount, ref failCount);
                }
            }
            else
            {
                failCount++;
                keepCount++;
                Log(o, logPath, "字幕" + (i + 1) + "行/" + blocks.Count + " 失败, 保留原文");
            }
        }

        // ---- 残留外语补翻: 检测 translate 结果中未翻译的非中文残留块, 单条重送翻译并即时写回 ----
        static int RefixForeign(Options o, string logPath, string sysPrompt, string userPrefix, List<Block> blocks)
        {
            string progressPath = o.Out + ".progress";
            string t = "";
            int k, refixed = 0, fail = 0;
            bool ok;
            for (k = 0; k < blocks.Count; k++)
            {
                if (o.Cancel != null && o.Cancel()) { Log(o, logPath, "残留补翻已取消"); break; }
                Block b = blocks[k];
                if (b.Skip) continue;
                t = string.Join("", b.Texts.ToArray());
                if (!IsForeignResidual(t)) continue;
                Log(o, logPath, string.Format("块 {0} 检测到外语残留, 单条补翻: {1}", b.Num, TruncateForLog(t)));
                ok = RefixOne(o, logPath, sysPrompt, userPrefix, b);
                if (ok)
                {
                    refixed++;
                    try { WriteSrt(o.Out, blocks); WriteProgress(progressPath, blocks); }
                    catch (Exception ex) { Log(o, logPath, "警告: 补翻存盘失败: " + ex.Message); }
                }
                else { fail++; }
            }
            if (fail > 0) Log(o, logPath, string.Format("残留补翻: {0} 块补翻失败, 保留原文", fail));
            return refixed;
        }

        // ---- 单条补翻一块残留 (不走 ProcessBatch: 残留块可能已标 Translated, 会被跳过) ----
        static bool RefixOne(Options o, string logPath, string sysPrompt, string userPrefix, Block b)
        {
            string t = string.Join("", b.Texts.ToArray());
            string user = userPrefix + "\n[" + b.Num + "] " + t;
            string respText = "", v = "", broken = "";
            Dictionary<int, string> map;
            int tryN;
            for (tryN = 1; tryN <= o.Retries; tryN++)
            {
                if (o.Cancel != null && o.Cancel()) return false;
                try
                {
                    respText = InvokeOllama(o, sysPrompt, user, o.TimeoutSec * 1000);
                    map = ParseResp(respText, true);
                    if (!map.TryGetValue(b.Num, out v))
                    {
                        map = ParseResp(respText, false);
                        if (!map.TryGetValue(b.Num, out v))
                        {
                            Log(o, logPath, "块 " + b.Num + " 补翻未命中序号, 尝试 " + tryN);
                            continue;
                        }
                    }
                    string v1 = System.Text.RegularExpressions.Regex.Replace(v, @"\r?\n", " ").Trim();
                    broken = o.BreakLine ? SrtUtil.SplitLongLine(v1, 20, 26) : v1;
                    b.Texts = new List<string>(broken.Split(new string[] { "\r\n" }, StringSplitOptions.None));
                    Log(o, logPath, "块 " + b.Num + " 补翻完成: " + TruncateForLog(v));
                    return true;
                }
                catch (Exception ex)
                {
                    if (o.Cancel != null && o.Cancel()) return false;
                    Log(o, logPath, "块 " + b.Num + " 补翻第 " + tryN + " 次失败: " + ex.Message);
                    if (tryN < o.Retries) System.Threading.Thread.Sleep(5000);
                }
            }
            return false;
        }

        // ---- 残留外语检测: 统计块文本字符构成; 汉字足够多视为正常中文, 拉丁/韩日西里尔等明显占优判为残留 ----
        static bool IsForeignResidual(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            int han = 0, lat = 0;
            char c;
            foreach (char ch in s)
            {
                c = ch;
                if (c >= 0x4E00 && c <= 0x9FFF) { han++; continue; }
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= 0x00C0 && c <= 0x024F)) { lat++; continue; }
                if (c >= 0xAC00 && c <= 0xD7AF) return true;        // 韩文
                if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF)) return true;  // 日文假名
                if (c >= 0x0400 && c <= 0x04FF) return true;        // 西里尔
                if (c >= 0x0600 && c <= 0x06FF) return true;        // 阿拉伯
                if (c >= 0x0900 && c <= 0x097F) return true;        // 天城文
            }
            if (han >= 5) return false;              // 汉字足够多 → 正常中文译文
            return (lat >= 8 && lat > han * 3);      // 拉丁明显占优 → 葡/西/英整块残留
        }

        // 日志截断: 超长文本只显示开头 40 字符
        static string TruncateForLog(string s)
        {
            if (s == null) return "";
            return s.Length <= 40 ? s : s.Substring(0, 40) + "...";
        }

        // 解析模型返回的 [N] 译文
        //  strict=true : 逐行要求行首 [N] 译文
        //  strict=false: 全局扫描 [N] 后到下一个 [N]/结尾 的内容, 容忍前缀、漏序号、不换行; 过滤脱轨词
        static Dictionary<int, string> ParseResp(string resp, bool strict)
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            if (strict)
            {
                foreach (string ml in resp.Split('\n'))
                {
                    Match m = Regex.Match(ml, "^\\[\\s*(\\d+)\\s*\\]\\s*(.*)$");
                    if (m.Success)
                    {
                        string txt = m.Groups[2].Value.Trim();
                        if (txt != "" && !IsOffrail(txt)) map[int.Parse(m.Groups[1].Value)] = txt;
                    }
                }
                return map;
            }
            foreach (Match m in Regex.Matches(resp, "\\[\\s*(\\d+)\\s*\\]\\s*([^\\[]+)"))
            {
                int nn = int.Parse(m.Groups[1].Value);
                string txt = m.Groups[2].Value.Trim().TrimStart('-', ' ', ':', '：');
                if (txt == "" || IsOffrail(txt)) continue;
                if (!map.ContainsKey(nn)) map[nn] = txt;   // 同一序号取第一个译文
            }
            return map;
        }

        // 模型脱轨特征: 返回分析/待命/吐槽而非译文时丢弃 (短文本命中才判定, 长文本一般是真实译文)
        static bool IsOffrail(string s)
        {
            if (s.Length > 60) return false;
            return s.Contains("我的分析") || s.Contains("我已准备就绪") || s.Contains("请提供需要翻译")
                || s.Contains("正在为您") || s.Contains("已为您翻译") || s.Contains("请继续提供")
                || s.Contains("我已了解任务") || s.Contains("好的，我已了解") || s.Contains("这什么鬼东西")
                || s.Contains("这帮混蛋") || s.Contains("不知道你在说什么") || s.Contains("我来给您")
                || s.Contains("我先给您");
        }

        // 调用 LLM 对话接口: ollama -> /api/chat; openai/llama -> /v1/chat/completions
        // (UTF-8 安全传输, 返回 assistant 文本; 单次超时 timeoutMs)
        static string InvokeOllama(Options o, string sys, string usr, int timeoutMs)
        {
            if (o.Backend == "ollama") { return InvokeOllamaApi(o, sys, usr, timeoutMs); }
            return InvokeOpenAiApi(o, sys, usr, timeoutMs);
        }

        // Ollama 原生 /api/chat (messages 数组)
        static string InvokeOllamaApi(Options o, string sys, string usr, int timeoutMs)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = o.Model;
            payload["stream"] = false;
            payload["think"] = false;
            Dictionary<string, object> options = new Dictionary<string, object>();
            options["temperature"] = 0;
            options["num_ctx"] = 8192;
            payload["options"] = options;
            List<object> messages = new List<object>();
            Dictionary<string, object> m1 = new Dictionary<string, object>();
            m1["role"] = "system"; m1["content"] = sys;
            Dictionary<string, object> m2 = new Dictionary<string, object>();
            m2["role"] = "user"; m2["content"] = usr;
            messages.Add(m1); messages.Add(m2);
            payload["messages"] = messages;

            JavaScriptSerializer js = new JavaScriptSerializer();
            string json = js.Serialize(payload);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(o.BaseUrl.TrimEnd('/') + "/api/chat");
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = timeoutMs;                    // 单次请求超时 (配置值)
            req.ReadWriteTimeout = timeoutMs;
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentLength = data.Length;
            o.ActiveRequest = req;                  // 供 Stop 立即中断
            try
            {
                using (Stream rs = req.GetRequestStream()) { rs.Write(data, 0, data.Length); }
                string respText;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                { respText = sr.ReadToEnd(); }

                Dictionary<string, object> obj = js.Deserialize<Dictionary<string, object>>(respText);
                object msgObj;
                if (obj == null || !obj.TryGetValue("message", out msgObj)) throw new Exception("LLM 返回异常: " + respText);
                Dictionary<string, object> msg = msgObj as Dictionary<string, object>;
                if (msg == null || !msg.ContainsKey("content")) throw new Exception("LLM 返回异常: " + respText);
                return Convert.ToString(msg["content"]);
            }
            finally { o.ActiveRequest = null; }
        }

        // OpenAI 兼容 /v1/chat/completions (llama-server / 任意 OpenAI 兼容端点)
        static string InvokeOpenAiApi(Options o, string sys, string usr, int timeoutMs)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = o.Model;
            payload["stream"] = false;
            payload["temperature"] = 0;
            payload["max_tokens"] = 4096;
            List<object> messages = new List<object>();
            Dictionary<string, object> m1 = new Dictionary<string, object>();
            m1["role"] = "system"; m1["content"] = sys;
            Dictionary<string, object> m2 = new Dictionary<string, object>();
            m2["role"] = "user"; m2["content"] = usr;
            messages.Add(m1); messages.Add(m2);
            payload["messages"] = messages;

            JavaScriptSerializer js = new JavaScriptSerializer();
            string json = js.Serialize(payload);

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(o.BaseUrl.TrimEnd('/') + "/v1/chat/completions");
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            if (o.ApiKey != "") { req.Headers["Authorization"] = "Bearer " + o.ApiKey; }
            byte[] data = Encoding.UTF8.GetBytes(json);
            req.ContentLength = data.Length;
            o.ActiveRequest = req;                  // 供 Stop 立即中断
            try
            {
                using (Stream rs = req.GetRequestStream()) { rs.Write(data, 0, data.Length); }
                string respText;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                { respText = sr.ReadToEnd(); }

                Dictionary<string, object> obj = js.Deserialize<Dictionary<string, object>>(respText);
                if (obj == null) throw new Exception("LLM 返回异常: " + respText);
                object chObj;
                if (!obj.TryGetValue("choices", out chObj)) throw new Exception("LLM 返回异常: " + respText);
                object[] choices = chObj as object[];
                if (choices == null || choices.Length == 0) throw new Exception("LLM 返回异常: " + respText);
                Dictionary<string, object> first = choices[0] as Dictionary<string, object>;
                if (first == null || !first.ContainsKey("message")) throw new Exception("LLM 返回异常: " + respText);
                Dictionary<string, object> msg = first["message"] as Dictionary<string, object>;
                if (msg == null || !msg.ContainsKey("content")) throw new Exception("LLM 返回异常: " + respText);
                return Convert.ToString(msg["content"]);
            }
            finally { o.ActiveRequest = null; }
        }

        // 最大连续相同字符数 (大小写不敏感, 忽略空格/制表符);
        // 用于识别 Aaaa... 及 "아 아 아..." 交替重复类噪声行
        static int MaxRunLen(string s)
        {
            int max = 0, cur = 0;
            char last = '\0';
            foreach (char c in s)
            {
                if (c == ' ' || c == '\t') { continue; }   // 空格/制表符不打断连续
                char cc = char.ToLowerInvariant(c);
                if (cc == last) { cur++; }
                else { cur = 1; last = cc; }
                if (cur > max) max = cur;
            }
            return max;
        }

        // 解析 SRT 文件为块结构 (块号/时间/文本; 复用断点续传读回旧译文)
        static List<Block> ParseSrt(string path)
        {
            List<Block> blocks = new List<Block>();
            Block cur = null;
            foreach (string ln in File.ReadAllLines(path, Utf8NoBom))
            {
                string s = ln.Trim();
                if (s == "")
                {
                    if (cur != null) { blocks.Add(cur); cur = null; }
                    continue;
                }
                if (cur == null)
                {
                    if (Regex.IsMatch(s, "^\\d+$")) cur = new Block { Num = int.Parse(s) };
                    continue;
                }
                if (string.IsNullOrEmpty(cur.Time) && s.IndexOf("-->") >= 0) { cur.Time = s; continue; }
                if (!string.IsNullOrEmpty(cur.Time)) cur.Texts.Add(s);
            }
            if (cur != null) blocks.Add(cur);
            return blocks;
        }

        // 全量写 SRT (UTF-8 带 BOM): 已翻译块写译文, 未翻译块保留原文
        static void WriteSrt(string path, List<Block> blocks)
        {
            List<string> writeLines = new List<string>();
            foreach (Block b in blocks)
            {
                writeLines.Add(b.Num.ToString());
                writeLines.Add(b.Time);
                foreach (string t in b.Texts) writeLines.Add(t);
                writeLines.Add("");
            }
            while (writeLines.Count > 0 && writeLines[writeLines.Count - 1] == "") writeLines.RemoveAt(writeLines.Count - 1);
            File.WriteAllLines(path, writeLines.ToArray(), Utf8Bom);
        }

        // 写断点进度文件 (UTF-8 无 BOM): 每行一个已翻译块号
        static void WriteProgress(string path, List<Block> blocks)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Block b in blocks)
                if (b.Translated) { sb.Append(b.Num); sb.Append("\r\n"); }
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        }

        static void Log(Options o, string logPath, string msg)
        {
            if (o.Log != null) o.Log(msg);
            if (logPath != "")
            {
                try
                {
                    File.AppendAllText(logPath,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + "\r\n", Utf8NoBom);
                }
                catch { }
            }
        }
    }
}
