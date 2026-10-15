// Denoise.cs —— 语音降噪（第一步：轻量版，纯 C#）
//
// 结构（刻意与 WebRTC 的 NS 模块保持一致，第二步换成 sonora-ns 移植版时接口不变）：
//
//   采集样本 ─▶ 高通（去直流/隆隆声）─▶ 加窗 ─▶ 256 点 FFT
//                                                   │
//                            最小统计噪声估计 ◀──────┤ 每个频点
//                            判决引导先验信噪比 ◀────┤
//                            维纳增益 + 过减 + 谱底 ─┘
//                                                   │
//   输出样本 ◀─ 重叠相加 ◀─ 加窗 ◀─ 逆 FFT ◀─ 增益加权
//
// 参数：16 kHz、256 点 FFT、128 点跳（50% 重叠，sqrt-Hann 窗 → 完美重构）、
//       算法延迟 128 样本 = 8 ms。
using System;

namespace LanTalk
{
    internal enum DenoiseLevel
    {
        Off = 0,
        Low = 1,
        Mid = 2,
        High = 3
    }

    internal static class DenoiseLevels
    {
        public static string Name(DenoiseLevel l)
        {
            switch (l)
            {
                case DenoiseLevel.Off: return "关闭";
                case DenoiseLevel.Low: return "弱";
                case DenoiseLevel.High: return "强";
                default: return "中";
            }
        }

        /// <summary>解析命令行/界面传来的档位。</summary>
        public static bool TryParse(string s, out DenoiseLevel l)
        {
            l = DenoiseLevel.Mid;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim().ToLowerInvariant();
            if (t == "off" || t == "0" || t == "none" || t == "关闭") { l = DenoiseLevel.Off; return true; }
            if (t == "low" || t == "1" || t == "weak" || t == "弱") { l = DenoiseLevel.Low; return true; }
            if (t == "mid" || t == "2" || t == "medium" || t == "中") { l = DenoiseLevel.Mid; return true; }
            if (t == "high" || t == "3" || t == "strong" || t == "强") { l = DenoiseLevel.High; return true; }
            return false;
        }
    }

    internal sealed class Denoiser
    {
        public const int FftSize = 256;                 // 16 ms 窗
        public const int HopSize = FftSize / 2;         // 8 ms 跳，50% 重叠
        public const int Bins = FftSize / 2 + 1;        // 129
        /// <summary>
        /// 算法延迟。第一帧的前半窗是补的零（不含输入信息），
        /// 所以完整的重叠相加结果从第 FftSize-1 个输出样本才开始 —— 已用冲激响应验证。
        /// </summary>
        public const int LatencySamples = FftSize - 1;  // 255 样本 ≈ 16 ms

        // ---- 档位参数 ----
        private sealed class Params
        {
            public float OverSub;       // 过减系数：越大压得越狠
            public float FloorGain;     // 谱底：单个频点最多被压到多少
            public float SmAlpha;       // 功率平滑
            public float DdAlpha;       // 判决引导平滑
            public float MinBias;       // 最小统计的偏置补偿
            public float GainSmooth;    // 增益时间平滑
        }

        private static readonly Params[] LevelParams;

        private static Params MakeParams(float overSub, float floorGain, float smAlpha,
                                         float ddAlpha, float minBias, float gainSmooth)
        {
            Params p = new Params();
            p.OverSub = overSub;
            p.FloorGain = floorGain;
            p.SmAlpha = smAlpha;
            p.DdAlpha = ddAlpha;
            p.MinBias = minBias;
            p.GainSmooth = gainSmooth;
            return p;
        }

        // ---- 配置 ----
        private volatile bool _enabled;
        private DenoiseLevel _level = DenoiseLevel.Mid;
        private Params _p = LevelParams[2];

        public bool Enabled { get { return _enabled; } }
        public DenoiseLevel Level { get { return _level; } }

        /// <summary>
        /// 仅供自检：增益恒为 1，跳过噪声估计。
        /// 用来单独验证"加窗→FFT→逆FFT→重叠相加"这条链能否完美重构（输出应等于输入延迟 Latency 个样本）。
        /// </summary>
        public bool UnityGainForTest { get; set; }

        /// <summary>仅供自检：跳过高通，单独验证 FFT/重叠相加链路。</summary>
        public bool BypassHighPassForTest { get; set; }

        // 诊断量（最近一帧）
        public float DbgMeanGain;
        public float DbgMeanNoise;
        public float DbgMeanMag;

        public void SetLevel(DenoiseLevel l)
        {
            _level = l;
            _enabled = l != DenoiseLevel.Off;
            _p = LevelParams[(int)l];
        }

        // ---- 窗与 FFT 查表 ----
        private static readonly float[] Window;         // sqrt-Hann：sin(pi*n/N)
        private static readonly float[] CosTab, SinTab; // 2*pi*k/N
        // 高通（两级一阶，-3dB @ 90 Hz）
        private static readonly float HpA;

        /// <summary>全部静态表在这里一次算好（C# 一个类只能有一个静态构造函数）。</summary>
        static Denoiser()
        {
            Window = new float[FftSize];
            for (int i = 0; i < FftSize; i++) Window[i] = (float)Math.Sin(Math.PI * i / FftSize);

            CosTab = new float[FftSize / 2 + 1];
            SinTab = new float[FftSize / 2 + 1];
            for (int i = 0; i <= FftSize / 2; i++)
            {
                double a = 2.0 * Math.PI * i / FftSize;
                CosTab[i] = (float)Math.Cos(a);
                SinTab[i] = (float)Math.Sin(a);
            }

            LevelParams = new Params[4];
            LevelParams[0] = new Params();     // Off，用不到
            // 判决引导系数不能太大：实测 0.97 时增益滞后约 660ms，跟不上 4Hz 的音节调制，
            // 会出现"语音起来了增益还没起来、语音停了增益反而爬上去"的反向跟踪。
            LevelParams[1] = MakeParams(1.2f, 0.25f, 0.70f, 0.60f, 1.40f, 0.50f);   // 弱：-12 dB 谱底
            LevelParams[2] = MakeParams(1.8f, 0.126f, 0.70f, 0.70f, 1.55f, 0.60f);  // 中：-18 dB 谱底
            LevelParams[3] = MakeParams(2.6f, 0.063f, 0.75f, 0.80f, 1.70f, 0.65f);  // 强：-24 dB 谱底

            double fc = 90.0, fs = 16000.0;
            double rc = 1.0 / (2.0 * Math.PI * fc);
            double dt = 1.0 / fs;
            HpA = (float)(rc / (rc + dt));
        }

        // ---- 运行状态 ----
        private readonly float[] _inHist = new float[FftSize];   // 最近 FftSize 个输入样本
        private readonly float[] _pend = new float[HopSize];
        private int _pendCount;

        private readonly float[] _ola = new float[FftSize];      // 重叠相加累加器
        private readonly float[] _outRing = new float[HopSize * 2 + 4];
        private int _outHead, _outTail, _outCount;

        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];

        private const int MinWindow = 50;                        // 最小统计窗口（50 帧 = 0.5 s）
        private readonly float[] _smoothPow = new float[Bins];
        private readonly float[] _minHist = new float[Bins * MinWindow];
        private readonly float[] _noisePow = new float[Bins];
        private readonly float[] _prevGain = new float[Bins];
        private readonly float[] _prevGamma = new float[Bins];
        private readonly float[] _gainSm = new float[Bins];
        private int _minPos;
        private int _minFrames;

        private float _hpX1a, _hpY1a, _hpX1b, _hpY1b;

        public Denoiser()
        {
            Reset();
        }

        /// <summary>仅供自检/调参：直接指定一组参数。</summary>
        public void SetParamsForTest(float overSub, float floorGain, float smAlpha,
                                     float ddAlpha, float minBias, float gainSmooth)
        {
            _p = MakeParams(overSub, floorGain, smAlpha, ddAlpha, minBias, gainSmooth);
            _enabled = true;
        }

        /// <summary>清空全部内部状态（开始新一通电话时调用）。</summary>
        public void Reset()
        {
            _pendCount = 0;
            _outHead = _outTail = _outCount = 0;
            _minPos = 0;
            _minFrames = 0;
            _hpX1a = _hpY1a = _hpX1b = _hpY1b = 0f;
            Array.Clear(_inHist, 0, _inHist.Length);
            Array.Clear(_pend, 0, _pend.Length);
            Array.Clear(_ola, 0, _ola.Length);
            Array.Clear(_outRing, 0, _outRing.Length);
            Array.Clear(_smoothPow, 0, Bins);
            Array.Clear(_minHist, 0, _minHist.Length);
            Array.Clear(_noisePow, 0, Bins);
            Array.Clear(_prevGain, 0, Bins);
            Array.Clear(_prevGamma, 0, Bins);
            Array.Clear(_gainSm, 0, Bins);
        }

        /// <summary>就地处理一段 16 位小端单声道 PCM。</summary>
        public void ProcessPcm(byte[] pcm, int len)
        {
            if (!_enabled) return;          // 关闭时完全不动数据（bit-exact 直通）
            for (int i = 0; i + 1 < len; i += 2)
            {
                short s = (short)(pcm[i] | (pcm[i + 1] << 8));
                short o = ProcessSample(s);
                pcm[i] = (byte)(o & 0xFF);
                pcm[i + 1] = (byte)((o >> 8) & 0xFF);
            }
        }

        private short ProcessSample(short x)
        {
            float s = x * (1f / 32768f);

            if (!BypassHighPassForTest)
            {
                // 两级一阶高通，去直流与低频隆隆声
                float y1 = HpA * (_hpY1a + s - _hpX1a);
                _hpX1a = s; _hpY1a = y1;
                float y2 = HpA * (_hpY1b + y1 - _hpX1b);
                _hpX1b = y1; _hpY1b = y2;
                s = y2;
            }

            _pend[_pendCount++] = s;
            if (_pendCount >= HopSize)
            {
                _pendCount = 0;
                Array.Copy(_inHist, HopSize, _inHist, 0, FftSize - HopSize);
                Array.Copy(_pend, 0, _inHist, FftSize - HopSize, HopSize);
                ProcessFrame();
            }

            float o = 0f;
            if (_outCount > 0)
            {
                o = _outRing[_outTail];
                _outTail++;
                if (_outTail >= _outRing.Length) _outTail = 0;
                _outCount--;
            }

            int v = (int)(o * 32768f + (o >= 0f ? 0.5f : -0.5f));
            if (v > 32767) v = 32767;
            else if (v < -32768) v = -32768;
            return (short)v;
        }

        private void PushOut(float v)
        {
            _outRing[_outHead] = v;
            _outHead++;
            if (_outHead >= _outRing.Length) _outHead = 0;
            _outCount++;
        }

        // ================= 一帧的频谱处理 =================

        private void ProcessFrame()
        {
            Params p = _p;
            bool unity = UnityGainForTest;

            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _inHist[i] * Window[i];
                _im[i] = 0f;
            }
            Fft(_re, _im, false);

            float sumGain = 0f, sumNoise = 0f, sumMag = 0f;

            for (int k = 0; k < Bins; k++)
            {
                float re = _re[k], im = _im[k];
                float mag2 = re * re + im * im;

                float g;
                if (unity)
                {
                    g = 1f;
                }
                else
                {
                    // 功率平滑（减少周期图方差）
                    _smoothPow[k] = p.SmAlpha * _smoothPow[k] + (1f - p.SmAlpha) * mag2;

                    // 最小统计噪声估计
                    UpdateNoiseEstimate(k, _smoothPow[k], p);
                    float np = _noisePow[k] * p.OverSub;
                    if (np < 1e-12f) np = 1e-12f;

                    // 后验信噪比
                    float gamma = mag2 / np;
                    if (gamma > 1e6f) gamma = 1e6f;

                    // 判决引导的先验信噪比
                    float xi = p.DdAlpha * (_prevGain[k] * _prevGain[k]) * _prevGamma[k]
                             + (1f - p.DdAlpha) * Math.Max(gamma - 1f, 0f);
                    if (xi < 1e-6f) xi = 1e-6f;

                    // 维纳增益
                    g = xi / (1f + xi);
                    if (g < p.FloorGain) g = p.FloorGain;
                    if (g > 1f) g = 1f;

                    // 时间平滑，抑制"音乐噪声"
                    _gainSm[k] = p.GainSmooth * _gainSm[k] + (1f - p.GainSmooth) * g;
                    g = _gainSm[k];

                    _prevGain[k] = g;
                    _prevGamma[k] = gamma;
                    sumGain += g;
                    sumNoise += _noisePow[k];
                    sumMag += mag2;
                }

                _re[k] *= g;
                _im[k] *= g;
                if (k > 0 && k < Bins - 1)
                {
                    _re[FftSize - k] *= g;
                    _im[FftSize - k] *= g;
                }
            }

            Fft(_re, _im, true);

            DbgMeanGain = sumGain / Bins;
            DbgMeanNoise = sumNoise / Bins;
            DbgMeanMag = sumMag / Bins;

            for (int i = 0; i < FftSize; i++) _ola[i] += _re[i] * Window[i];

            for (int i = 0; i < HopSize; i++) PushOut(_ola[i]);
            Array.Copy(_ola, HopSize, _ola, 0, FftSize - HopSize);
            Array.Clear(_ola, FftSize - HopSize, HopSize);

            // 最小统计窗口每"帧"推进一格（不是每频点，这个曾经写错导致噪声估计恒为 0）
            _minPos++;
            if (_minPos >= MinWindow) _minPos = 0;
            if (_minFrames < MinWindow) _minFrames++;
        }

        /// <summary>
        /// 最小统计：在 0.5 秒窗口内取每频点平滑功率的最小值作为噪声估计，
        /// 再乘一个偏置补偿（最小值本身偏小）。对稳态噪声跟踪很稳，
        /// 且不需要依赖语音活动检测。
        /// </summary>
        private void UpdateNoiseEstimate(int k, float power, Params p)
        {
            int baseIdx = k * MinWindow;
            _minHist[baseIdx + _minPos] = power;

            int n = _minFrames < MinWindow ? _minFrames + 1 : MinWindow;
            float min = float.MaxValue;
            int start = _minFrames < MinWindow ? 0 : (_minPos + 1) % MinWindow;
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                if (idx >= MinWindow) idx -= MinWindow;
                float v = _minHist[baseIdx + idx];
                if (v < min) min = v;
            }
            if (_minFrames < MinWindow) _minFrames++;

            float est = min * p.MinBias;
            // 上升快、下降慢：噪声变大时及时跟上，变小时不要立刻塌下去
            if (est > _noisePow[k]) _noisePow[k] = 0.5f * _noisePow[k] + 0.5f * est;
            else _noisePow[k] = 0.95f * _noisePow[k] + 0.05f * est;
        }

        // ================= 256 点基 2 FFT =================

        private static void Fft(float[] re, float[] im, bool inverse)
        {
            int n = FftSize;

            // 位反转置换
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int len = 2; len <= n; len <<= 1)
            {
                int half = len >> 1;
                int step = n / len;
                for (int i = 0; i < n; i += len)
                {
                    int tw = 0;
                    for (int k = 0; k < half; k++)
                    {
                        float wr = CosTab[tw];
                        float wi = inverse ? SinTab[tw] : -SinTab[tw];
                        int a = i + k, b = a + half;
                        float xr = re[b] * wr - im[b] * wi;
                        float xi = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr;        im[a] += xi;
                        tw += step;
                    }
                }
            }

            if (inverse)
            {
                float inv = 1f / n;
                for (int i = 0; i < n; i++) { re[i] *= inv; im[i] *= inv; }
            }
        }

        // ================= 供测试用的小工具 =================

        /// <summary>算法延迟（样本数）。测试对齐时要把输出往前挪这么多。</summary>
        public static int Latency { get { return LatencySamples; } }
    }
}
