// MDXSep.cs - MDX-Net 歌声分离 (C# 版, 对齐 mdx_infer.py)
// onnxruntime DirectML (GPU) + MathNet.Numerics FFT, 无 python 依赖
// 链路: STFT(4ch L/R 实虚部, n_fft=6144, hop=1024, dim_f=3072) -> onnx -> iSTFT(OLA) -> 伴奏
//       Inst 系模型: 人声 = mix - 伴奏 (subtract); Vocal 系: 输出即人声 (direct)
using System;
using System.IO;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SrtGui
{
    public static class MDXSep
    {
        // ---- MDX-Net 固定参数 (对齐 UVR model_data) ----
        const int SR        = 44100;
        const int N_FFT     = 6144;
        const int HOP       = 1024;
        const int DIM_F     = 3072;      // 模型频率 bins (= N_FFT/2, 丢弃 Nyquist bin)
        const int SEGMENT   = 256;       // mdx_segment_size, dim_t
        const float ADJUST  = 1.0f;
        const float COMPENSATE = 1.0f;

        static float[] _hannPeriodic;    // N_FFT 周期窗 (缓存)
        static readonly object _sync = new object();

        static float[] HannPeriodic(int n)
        {
            // np.hanning(n+1)[:-1] 的等价: 0.5 - 0.5*cos(2*pi*i/n)
            float[] w = new float[n];
            for (int i = 0; i < n; i++)
                w[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / n);
            return w;
        }

        static float[] HannSymmetric(int n)
        {
            // np.hanning(n) 等价: 0.5 - 0.5*cos(2*pi*i/(n-1))
            float[] w = new float[n];
            if (n <= 1) { w[0] = 1f; return w; }
            for (int i = 0; i < n; i++)
                w[i] = 0.5f - 0.5f * (float)Math.Cos(2.0 * Math.PI * i / (n - 1));
            return w;
        }

        // ============================================================ 主入口 (流式子块滑动, 支持全片)
        // 与整段 Demix 数学等价: 窗网格/窗输入/OLA/divider 归一全部保留,
        // 只把 result/divider 改为活动窗滑动缓冲, 算完一段立即落盘 -> 内存恒定, 无 2GB 数组限制
        public static bool Separate(string modelPath, string inWav, string outVocals, string outInst,
                                    double overlap, Action<string> log, Func<bool> cancel)
        {
            lock (_sync) { if (_hannPeriodic == null) _hannPeriodic = HannPeriodic(N_FFT); }
            if (!File.Exists(modelPath)) { log("错误: 未找到分离模型: " + modelPath); return false; }

            string name = Path.GetFileName(modelPath).ToLowerInvariant();
            bool subtract = name.Contains("inst") || name.Contains("karaoke");

            int channels, dataOffset, dataSize, sr;
            try { sr = WavInfo(inWav, out channels, out dataOffset, out dataSize); }
            catch (Exception ex) { log("读取 wav 头失败: " + ex); return false; }
            long total = dataSize / 2L / channels;
            if (channels < 1 || channels > 2) { log("错误: 仅支持 1-2 声道, 实际 " + channels); return false; }
            log(string.Format("输入: {0}  sr={1}  声道={2}  时长={3:F1}s",
                Path.GetFileName(inWav), sr, channels, (double)total / sr));

            InferenceSession sess;
            try
            {
                var so = new SessionOptions();
                bool dml = false;
                try
                {
                    string[] prov = OrtEnv.Instance().GetAvailableProviders();
                    dml = Array.IndexOf(prov, "DmlExecutionProvider") >= 0;
                }
                catch { }
                if (dml) { so.AppendExecutionProvider_DML(); }
                sess = new InferenceSession(modelPath, so);
                log("后端: " + (dml ? "DML(GPU)+CPU" : "CPU"));
            }
            catch (Exception ex)
            {
                log("模型加载失败: " + ex);
                return false;
            }

            FileStream fsV = null, fsI = null, inFs = null;
            BinaryWriter bwV = null, bwI = null;
            try
            {
                CreateWav(outVocals, sr, out fsV, out bwV);
                if (!string.IsNullOrEmpty(outInst)) { CreateWav(outInst, sr, out fsI, out bwI); }
                inFs = File.OpenRead(inWav);

                int trim = N_FFT / 2;
                int chunkSize = HOP * (SEGMENT - 1);
                int genSize = chunkSize - 2 * trim;
                int step = (int)((1.0 - overlap) * chunkSize);
                long padTotal = (long)genSize + trim - (total % genSize);
                long M = trim + total + padTotal;
                long totalChunks = (M + step - 1) / step;

                int B = chunkSize;
                float[] res = new float[2 * B];      // res[2*i+c], 活动窗 OLA 累积
                float[] div = new float[2 * B];
                float[] winFull = (overlap > 0.0) ? HannSymmetric(B) : null;
                int off = 0;
                float[,] part = new float[2, chunkSize];
                Stopwatch swTotal = Stopwatch.StartNew();
                Stopwatch swFft = new Stopwatch();
                Stopwatch swRun = new Stopwatch();
                long written = 0;

                for (long cs = 0; cs < totalChunks; cs++)
                {
                    // 用户停止: 立即中断 (残缺产物由调用方删除, 句柄由 finally 关闭)
                    if (cancel != null && cancel())
                    {
                        log(string.Format("用户停止: 分离中断 (子块 {0}/{1}, 已过 {2:F1}s)",
                            cs + 1, totalChunks, swTotal.Elapsed.TotalSeconds));
                        return false;
                    }
                    long start = cs * step;
                    int csa = (int)Math.Min(chunkSize, M - start);
                    float[] ww = (csa == chunkSize) ? winFull : HannSymmetric(csa);

                    // ---- 读窗输入 mixture[start..start+csa) -> 音频 [start-trim, start-trim+csa), 越界补 0 ----
                    // part 是跨窗复用数组: 先清零 [csa, chunkSize) 残留, 防止上一窗旧数据污染本窗 STFT (仅最后窗 csa<chunkSize 受影响)
                    for (int i = csa; i < chunkSize; i++) { part[0, i] = 0f; part[1, i] = 0f; }
                    long a0 = start - trim;
                    byte[] raw = new byte[csa * 2 * channels];
                    long r0 = Math.Max(a0, 0);
                    long r1 = Math.Min(a0 + csa, total);
                    if (r1 > r0)
                    {
                        inFs.Position = dataOffset + r0 * 2L * channels;
                        int bytes = (int)((r1 - r0) * 2 * channels);
                        int got = 0;
                        while (got < bytes) { int r = inFs.Read(raw, got, bytes - got); if (r <= 0) { break; } got += r; }
                    }
                    for (int i = 0; i < csa; i++)
                    {
                        long ai = a0 + i;
                        if (ai < 0 || ai >= total) { part[0, i] = 0f; part[1, i] = 0f; continue; }
                        int o = (int)((ai - r0) * 2 * channels);   // raw 偏移基准是读入起点 r0, 不是 a0
                        for (int c = 0; c < 2; c++)
                        {
                            int src = (channels == 1) ? 0 : (c < channels ? c : channels - 1);
                            short s = BitConverter.ToInt16(raw, o + src * 2);
                            part[c, i] = (float)s / 32768f;
                        }
                    }

                    // ---- STFT -> 低3bin清零 -> 推理 -> iSTFT ----
                    swFft.Start();
                    int nFrames;
                    float[] spec = Stft4(part, out nFrames);
                    swFft.Stop();
                    for (int c = 0; c < 4; c++)
                        for (int f = 0; f < 3; f++)
                            for (int t = 0; t < nFrames; t++)
                                spec[(c * DIM_F + f) * nFrames + t] = 0f;
                    swRun.Start();
                    float[] pred = Run(sess, spec, nFrames);
                    swRun.Stop();
                    float[] wave = Istft4(pred, nFrames, csa);

                    // ---- OLA 累积 ----
                    for (int i = 0; i < csa; i++)
                    {
                        int bi = (off + i) % B;
                        float w = (ww != null) ? ww[i] : 1f;
                        res[2 * bi]     += wave[i] * w;          // L
                        res[2 * bi + 1] += wave[csa + i] * w;    // R
                        div[2 * bi]     += w;
                        div[2 * bi + 1] += w;
                    }

                    // ---- 发射 [start, start+step) (最后一个窗 [start, M)) 落盘 ----
                    long emit0 = start;
                    long emit1 = Math.Min(start + step, M);
                    long w0 = Math.Max(emit0 - trim, 0);
                    long w1 = Math.Min(emit1 - trim, total);
                    if (w1 > w0)
                    {
                        byte[] pcm = new byte[(int)((w1 - w0) * 4)];
                        for (long p = w0; p < w1; p++)
                        {
                            int i = (int)(p - emit0 + trim);      // part 内索引 (音频 p = start-trim+i)
                            int bi = (off + i) % B;
                            for (int c = 0; c < 2; c++)
                            {
                                float stemV = res[2 * bi + c] / Math.Max(div[2 * bi + c], 1e-8f);
                                float v = subtract ? (part[c, i] - stemV) : stemV;
                                if (v > 1f) { v = 1f; }
                                if (v < -1f) { v = -1f; }
                                short s = (short)(v * 32767f);
                                int o = (int)((p - w0) * 4 + c * 2);
                                pcm[o] = (byte)(s & 0xFF);
                                pcm[o + 1] = (byte)((s >> 8) & 0xFF);
                            }
                        }
                        bwV.Write(pcm);
                        if (bwI != null)
                        {
                            byte[] pcmI = new byte[(int)((w1 - w0) * 4)];
                            for (long p = w0; p < w1; p++)
                            {
                                int i = (int)(p - emit0 + trim);
                                int bi = (off + i) % B;
                                for (int c = 0; c < 2; c++)
                                {
                                    float stemV = res[2 * bi + c] / Math.Max(div[2 * bi + c], 1e-8f);
                                    if (stemV > 1f) { stemV = 1f; }
                                    if (stemV < -1f) { stemV = -1f; }
                                    short s = (short)(stemV * 32767f);
                                    int o = (int)((p - w0) * 4 + c * 2);
                                    pcmI[o] = (byte)(s & 0xFF);
                                    pcmI[o + 1] = (byte)((s >> 8) & 0xFF);
                                }
                            }
                            bwI.Write(pcmI);
                        }
                        written += (w1 - w0);
                    }

                    // ---- 发射区缓冲清零 (值已落盘, 下次被窗覆盖前归零; 不可提前清, 会丢 OLA 重叠累积) ----
                    for (int i = 0; i < step; i++)
                    {
                        int bi = (off + i) % B;
                        res[2 * bi] = 0f; res[2 * bi + 1] = 0f;
                        div[2 * bi] = 0f; div[2 * bi + 1] = 0f;
                    }

                    off = (off + step) % B;

                    if (cs % 5 == 0 || cs == totalChunks - 1)
                    {
                        log(string.Format("  子块 {0}/{1}  fft={2}ms run={3}ms chunk={4}ms 已过{5:F1}s",
                            cs + 1, totalChunks, swFft.ElapsedMilliseconds, swRun.ElapsedMilliseconds,
                            swTotal.ElapsedMilliseconds, swTotal.Elapsed.TotalSeconds));
                    }
                }

                FinalizeWav(fsV, bwV, total);
                if (fsI != null) { FinalizeWav(fsI, bwI, total); }
                log("分离完成(stem=" + (subtract ? "subtract" : "direct") + ") 已写出: " + outVocals);
                if (!string.IsNullOrEmpty(outInst)) { log("已写出: " + outInst); }
                return true;
            }
            catch (Exception ex)
            {
                log("分离失败: " + ex);
                return false;
            }
            finally
            {
                try { if (bwV != null) { bwV.Close(); } } catch { }
                try { if (bwI != null) { bwI.Close(); } } catch { }
                try { if (fsV != null) { fsV.Dispose(); } } catch { }
                try { if (fsI != null) { fsI.Dispose(); } } catch { }
                try { if (inFs != null) { inFs.Dispose(); } } catch { }
                sess.Dispose();
            }
        }

        // ============================================================ onnx 推理
        static float[] Run(InferenceSession sess, float[] spec, int nFrames)
        {
            var input = new DenseTensor<float>(spec, new[] { 1, 4, DIM_F, nFrames });
            using (var outputs = sess.Run(new[] { NamedOnnxValue.CreateFromTensor("input", input) }))
            {
                var dt = (DenseTensor<float>)outputs[0].AsTensor<float>();
                return dt.Buffer.ToArray();   // [1, 4, DIM_F, T]
            }
        }

        // ============================================================ STFT: [2,N] -> [4,DIM_F,T] (row-major (c*DIM_F+f)*T+t)
        static float[] Stft4(float[,] x, out int nFrames)
        {
            int len = x.GetLength(1);
            int pad = N_FFT / 2;
            int total = len + 2 * pad;
            nFrames = 1 + (total - N_FFT) / HOP;
            float[] win = _hannPeriodic;
            float[] spec = new float[4 * DIM_F * nFrames];
            Complex32[] buf = new Complex32[N_FFT];
            FastFft.EnsureInit();

            for (int f = 0; f < nFrames; f++)
            {
                int fStart = f * HOP - pad;
                // L 通道
                for (int i = 0; i < N_FFT; i++)
                {
                    int idx = fStart + i;
                    float v = (idx >= 0 && idx < len) ? x[0, idx] : 0f;
                    buf[i] = new Complex32(v * win[i], 0f);
                }
                FastFft.Forward6144(buf);
                for (int k = 0; k < DIM_F; k++)
                {
                    spec[(0 * DIM_F + k) * nFrames + f] = buf[k].Real * ADJUST;
                    spec[(1 * DIM_F + k) * nFrames + f] = buf[k].Imaginary * ADJUST;
                }
                // R 通道
                for (int i = 0; i < N_FFT; i++)
                {
                    int idx = fStart + i;
                    float v = (idx >= 0 && idx < len) ? x[1, idx] : 0f;
                    buf[i] = new Complex32(v * win[i], 0f);
                }
                FastFft.Forward6144(buf);
                for (int k = 0; k < DIM_F; k++)
                {
                    spec[(2 * DIM_F + k) * nFrames + f] = buf[k].Real * ADJUST;
                    spec[(3 * DIM_F + k) * nFrames + f] = buf[k].Imaginary * ADJUST;
                }
            }
            return spec;
        }

        // ============================================================ iSTFT: [4,DIM_F,T] -> [2, chunkSize]
        static float[] Istft4(float[] spec, int T, int outLen)
        {
            int nBins = N_FFT / 2 + 1;
            float[] win = _hannPeriodic;
            double[] wwin = new double[N_FFT];
            for (int i = 0; i < N_FFT; i++) { wwin[i] = (double)win[i] * win[i]; }

            int L = (T - 1) * HOP + N_FFT;
            float[] outW = new float[2 * outLen];
            Complex32[] buf = new Complex32[N_FFT];
            double[] accum = new double[L];
            double[] wsum = new double[L];
            int padN = N_FFT / 2;

            for (int ch = 0; ch < 2; ch++)
            {
                Array.Clear(accum, 0, L);
                Array.Clear(wsum, 0, L);
                int cRe = ch * 2;       // 实部通道 (0=L, 2=R)
                int cIm = ch * 2 + 1;   // 虚部通道

                for (int t = 0; t < T; t++)
                {
                    // 构造完整频谱: 正频 0..3072 (实虚), Nyquist 补 0, 负频共轭
                    buf[0] = new Complex32(spec[(cRe * DIM_F + 0) * T + t],
                                           spec[(cIm * DIM_F + 0) * T + t]);
                    for (int k = 1; k < DIM_F; k++)
                    {
                        float re = spec[(cRe * DIM_F + k) * T + t];
                        float im = spec[(cIm * DIM_F + k) * T + t];
                        buf[k] = new Complex32(re, im);
                    }
                    buf[DIM_F] = new Complex32(0f, 0f);   // Nyquist bin (丢弃位补 0)
                    for (int k = nBins; k < N_FFT; k++)
                    {
                        buf[k] = buf[N_FFT - k].Conjugate();
                    }
                    FastFft.Inverse6144(buf);

                    int idx0 = t * HOP;
                    for (int i = 0; i < N_FFT; i++)
                    {
                        accum[idx0 + i] += (double)buf[i].Real * win[i];
                        wsum[idx0 + i] += wwin[i];
                    }
                }

                for (int i = 0; i < outLen; i++)
                {
                    double v = (wsum[padN + i] < 1e-8) ? 0.0 : accum[padN + i] / wsum[padN + i];
                    outW[ch * outLen + i] = (float)v;
                }
            }
            return outW;
        }

        // ============================================================ WAV IO (PCM16, 流式)
        // 解析 RIFF/WAVE 头, 返回采样率; 输出 data 块偏移与大小
        static int WavInfo(string path, out int channels, out int dataOffset, out int dataSize)
        {
            using (FileStream fs = File.OpenRead(path))
            using (BinaryReader br = new BinaryReader(fs))
            {
                if (new string(br.ReadChars(4)) != "RIFF") { throw new Exception("不是 RIFF/WAV 文件"); }
                br.ReadInt32();
                if (new string(br.ReadChars(4)) != "WAVE") { throw new Exception("不是 WAVE 文件"); }

                int sr = 0;
                channels = 0;
                dataOffset = 0;
                dataSize = 0;
                while (fs.Position < fs.Length)
                {
                    string id = new string(br.ReadChars(4));
                    int sz = br.ReadInt32();
                    if (id == "fmt ")
                    {
                        br.ReadInt16();                 // 格式 (1=PCM)
                        channels = br.ReadInt16();
                        sr = br.ReadInt32();
                        br.ReadInt32();                 // byteRate
                        br.ReadInt16();                 // blockAlign
                        br.ReadInt16();                 // bits
                        if (sz > 16) { br.ReadBytes(sz - 16); }
                    }
                    else if (id == "data")
                    {
                        dataOffset = (int)fs.Position;  // data 内容起点
                        dataSize = sz;
                        return sr;
                    }
                    else { br.ReadBytes(sz); }
                }
                throw new Exception("未找到 data 块");
            }
        }

        // 创建输出 wav (44 字节头占位, FinalizeWav 回填长度)
        static void CreateWav(string path, int sr, out FileStream fs, out BinaryWriter bw)
        {
            fs = File.Create(path);
            bw = new BinaryWriter(fs);
            byte[] hdr = new byte[44];
            WriteAscii(hdr, 0, "RIFF");
            WriteInt(hdr, 4, 0);                 // 占位
            WriteAscii(hdr, 8, "WAVE");
            WriteAscii(hdr, 12, "fmt ");
            WriteInt(hdr, 16, 16);
            WriteShort(hdr, 20, 1);
            WriteShort(hdr, 22, 2);
            WriteInt(hdr, 24, sr);
            WriteInt(hdr, 28, sr * 2 * 2);
            WriteShort(hdr, 32, 4);
            WriteShort(hdr, 34, 16);
            WriteAscii(hdr, 36, "data");
            WriteInt(hdr, 40, 0);                // 占位
            bw.Write(hdr);
        }

        // 回填 RIFF/data 长度 (uint, 支持到 4GB)
        static void FinalizeWav(FileStream fs, BinaryWriter bw, long totalSamples)
        {
            long dataSize = totalSamples * 2L * 2L;
            bw.Flush();
            long pos = fs.Position;
            fs.Position = 4;
            bw.Write((uint)(36 + dataSize));
            fs.Position = 40;
            bw.Write((uint)dataSize);
            fs.Position = pos;
            bw.Flush();
        }

        static void WriteAscii(byte[] b, int off, string s)
        {
            for (int i = 0; i < s.Length; i++) { b[off + i] = (byte)s[i]; }
        }
        static void WriteInt(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
            b[off + 2] = (byte)((v >> 16) & 0xFF); b[off + 3] = (byte)((v >> 24) & 0xFF);
        }
        static void WriteShort(byte[] b, int off, int v)
        {
            b[off] = (byte)(v & 0xFF); b[off + 1] = (byte)((v >> 8) & 0xFF);
        }

    }

    // ============================================================ 混合基 FFT
    // 6144 = 2048*3: 三个 2048 点 radix-2 FFT + 组合 (替换 MathNet Bluestein, 快 ~40x)
    public static class FastFft
    {
        public const int N = 6144;
        static Complex32[] _w;   // W6144^k = exp(-2*pi*i*k/6144), k=0..6143
        static bool _init;

        public static void EnsureInit()
        {
            if (_init) { return; }
            _w = new Complex32[N];
            for (int k = 0; k < N; k++)
            {
                double ang = -2.0 * Math.PI * k / N;
                _w[k] = new Complex32((float)Math.Cos(ang), (float)Math.Sin(ang));
            }
            _init = true;
        }

        public static void Forward6144(Complex32[] x)
        {
            int n3 = N / 3;   // 2048
            Complex32[] y = new Complex32[3 * n3];
            for (int n = 0; n < n3; n++)
            {
                y[n]          = x[3 * n];
                y[n3 + n]     = x[3 * n + 1];
                y[2 * n3 + n] = x[3 * n + 2];
            }
            Radix2(y, 0, n3);
            Radix2(y, n3, n3);
            Radix2(y, 2 * n3, n3);
            for (int k = 0; k < N; k++)
            {
                int q = k % n3;
                Complex32 w1 = _w[k];
                Complex32 w2 = _w[(2 * k) % N];
                x[k] = y[q] + w1 * y[n3 + q] + w2 * y[2 * n3 + q];
            }
        }

        public static void Inverse6144(Complex32[] x)
        {
            // ifft(x) = conj(fft(conj(x))) / N
            for (int i = 0; i < N; i++) { x[i] = x[i].Conjugate(); }
            Forward6144(x);
            for (int i = 0; i < N; i++) { x[i] = new Complex32(x[i].Real / N, x[i].Imaginary / N); }
        }

        // 就地 radix-2 迭代 FFT (2 的幂长度)
        static void Radix2(Complex32[] a, int offset, int n)
        {
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) { j ^= bit; }
                j ^= bit;
                if (i < j)
                {
                    Complex32 t = a[offset + i]; a[offset + i] = a[offset + j]; a[offset + j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2.0 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float curR = 1f, curI = 0f;
                    int half = len >> 1;
                    for (int k = 0; k < half; k++)
                    {
                        int aIdx = offset + i + k;
                        int bIdx = aIdx + half;
                        float tR = a[bIdx].Real * curR - a[bIdx].Imaginary * curI;
                        float tI = a[bIdx].Real * curI + a[bIdx].Imaginary * curR;
                        float uR = a[aIdx].Real, uI = a[aIdx].Imaginary;
                        a[aIdx] = new Complex32(uR + tR, uI + tI);
                        a[bIdx] = new Complex32(uR - tR, uI - tI);
                        float nR = curR * wr - curI * wi;
                        float nI = curR * wi + curI * wr;
                        curR = nR; curI = nI;
                    }
                }
            }
        }
    }
}
