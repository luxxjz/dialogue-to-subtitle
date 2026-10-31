// LlmServer.cs - 内嵌 Ollama serve (移动版翻译引擎)
// 定位 tools\ollama\ollama.exe + 内置模型目录 models\ollama (OLLAMA_MODELS)
// 启动 ollama 服务 (/api/chat + /api/tags), 使用 manifest 自带模板/参数 (qwen3.5-mt:2b)
// 端口被占用自动换空闲端口; 进程挂入 ProcRunner Job, 停止/退出程序时一键带走

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SrtGui
{
    public class LlmServer
    {
        const string EngineModel = "qwen3.5-mt:2b";   // 内置模型名 (OLLAMA_MODELS manifest)

        Process _proc;
        readonly object _logLock = new object();
        StreamWriter _engineLog;   // 引擎完整日志落盘 (界面只显示精简信息)
        public int Port { get; private set; }

        // 定位 exe: <程序目录>\tools\ollama\ollama.exe
        public static string FindExe()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string direct = Path.Combine(baseDir, "tools", "ollama", "ollama.exe");
            if (File.Exists(direct)) return direct;
            try
            {
                foreach (string f in Directory.EnumerateFiles(Path.Combine(baseDir, "tools"), "ollama.exe", SearchOption.AllDirectories))
                    return f;
            }
            catch { }
            return null;
        }

        // 定位内置模型目录: <程序目录>\models\ollama (含 manifests 的 OLLAMA_MODELS 结构)
        public static string FindModelsDir()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dir = Path.Combine(baseDir, "models", "ollama");
            if (Directory.Exists(dir) && Directory.Exists(Path.Combine(dir, "manifests"))) return dir;
            return null;
        }

        // 健康检查: GET /api/tags 返回 200 且含内置模型
        static bool HasModel(string url, string model)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/api/tags");
                req.Method = "GET";
                req.Timeout = 4000;
                using (WebResponse resp = req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    return body.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        // 端口上有 HTTP 服务 (Ollama 或其它)
        static bool PortBusy(string url)
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url.TrimEnd('/') + "/api/tags");
                req.Method = "GET";
                req.Timeout = 2000;
                using (WebResponse resp = req.GetResponse()) { return true; }
            }
            catch { return false; }
        }

        static int FindFreePort()
        {
            System.Net.Sockets.TcpListener l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try { return ((IPEndPoint)l.LocalEndpoint).Port; }
            finally { l.Stop(); }
        }

        // 确保内置 ollama serve 在跑; 返回实际 BaseUrl 与模型名; 失败抛异常
        // runner: ProcRunner Job (挂进程, 停止时带走); log: 日志回调; cancel: 取消回调
        public string EnsureRunning(int port, ProcRunner runner, Action<string> log, Func<bool> cancel, out string model)
        {
            model = EngineModel;
            string url = "http://127.0.0.1:" + port;
            string exe = FindExe();
            if (exe == null) throw new Exception("未找到内嵌引擎: tools\\ollama\\ollama.exe (请检查程序目录完整性)");
            string modelsDir = FindModelsDir();
            if (modelsDir == null) throw new Exception("未找到内置模型目录: models\\ollama (请检查程序目录完整性)");

            // 端口上已有本程序拉起的服务且模型就绪 -> 复用
            if (_proc != null && !_proc.HasExited && HasModel(url, EngineModel))
            {
                log("本地引擎已在运行: " + url + " (" + EngineModel + ")");
                return url;
            }
            // 端口上已有 Ollama 服务且含内置模型 -> 复用 (不杀外部进程)
            if (_proc == null && HasModel(url, EngineModel))
            {
                log("端口 " + port + " 已有 Ollama 且含 " + EngineModel + ", 直接复用: " + url);
                return url;
            }
            // 端口被其他程序占用 -> 换空闲端口
            if (PortBusy(url))
            {
                port = FindFreePort();
                url = "http://127.0.0.1:" + port;
                log("端口 " + port + " 被其他程序占用, 改用空闲端口 " + port);
            }

            Stop();
            StartOnce(exe, modelsDir, port, runner, log);

            DateTime deadline = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < deadline)
            {
                if (cancel != null && cancel()) { Stop(); throw new OperationCanceledException(); }
                if (_proc != null && _proc.HasExited) { Stop(); throw new Exception("ollama serve 已退出 (代码 " + SafeExitCode() + "), 请检查端口冲突或关闭 Ollama 托盘程序"); }
                if (HasModel(url, EngineModel)) break;
                Thread.Sleep(500);
            }
            if (!HasModel(url, EngineModel))
            {
                Stop();
                throw new TimeoutException("内置引擎未在时限内就绪 (" + EngineModel + ")");
            }
            Port = port;
            log("本地引擎已启动: " + EngineModel + "  " + url);
            return url;
        }

        int SafeExitCode()
        {
            try { return _proc.ExitCode; } catch { return -1; }
        }

        void StartOnce(string exe, string modelsDir, int port, ProcRunner runner, Action<string> log)
        {
            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = exe;
            si.WorkingDirectory = Path.GetDirectoryName(exe);
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.Arguments = "serve";
            si.RedirectStandardError = true;   // 异步收集引擎日志 (BeginErrorReadLine 必需)
            si.EnvironmentVariables["OLLAMA_MODELS"] = modelsDir;
            si.EnvironmentVariables["OLLAMA_HOST"] = "127.0.0.1:" + port;
            si.EnvironmentVariables["OLLAMA_NOPRUNE"] = "1";
            // 强制 Vulkan 后端: cuda_v12 的 PDL kernel 需要 CUDA 12.8+ 驱动, GTX 970(Maxwell) 已停更驱动(最高 12.4),
            // PTX JIT 失败导致 llama-server 崩溃(0xc0000409); Vulkan 后端在 GTX 970 / Arc 均可正常推理
            si.EnvironmentVariables["OLLAMA_LLM_LIBRARY"] = "vulkan";

            // 引擎完整日志落盘: <程序目录>\_transub_tmp\ollama_serve.log (界面只显示精简信息)
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_transub_tmp");
                Directory.CreateDirectory(logDir);
                _engineLog = new StreamWriter(Path.Combine(logDir, "ollama_serve.log"), true, new UTF8Encoding(false));
                _engineLog.AutoFlush = true;
            }
            catch { _engineLog = null; }

            _proc = new Process { StartInfo = si, EnableRaisingEvents = true };
            _proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                string d = e.Data;
                if (string.IsNullOrEmpty(d)) { return; }
                lock (_logLock)
                {
                    try { if (_engineLog != null) { _engineLog.WriteLine(d); } } catch { }
                }
                // 界面只显示: GPU 探测结果 / 错误行 (其余引擎调试日志不刷屏)
                Match m = Regex.Match(d, "description=\"([^\"]+)\".*?total=\"([^\"]+)\"");
                if (m.Success)
                {
                    try { log("引擎 GPU: " + m.Groups[1].Value + " (" + m.Groups[2].Value + ")"); } catch { }
                    return;
                }
                if (d.IndexOf("level=ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    d.IndexOf("level=FATAL", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { log("[引擎] " + d); } catch { }
                }
            };
            if (!_proc.Start()) throw new Exception("无法创建 ollama 进程");
            if (runner != null) runner.AddProcess(_proc);
            _proc.BeginErrorReadLine();
        }

        public void Stop()
        {
            Process p = _proc;
            _proc = null;
            if (p != null)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            lock (_logLock)
            {
                try { if (_engineLog != null) { _engineLog.Dispose(); _engineLog = null; } } catch { }
            }
        }

        public void Dispose() { Stop(); }
    }
}
