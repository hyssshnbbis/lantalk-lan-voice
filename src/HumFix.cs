// HumFix.cs —— 针对「电流声」和「键盘敲击」的两级处理
//
// 这两类噪声和普通稳态噪声性质不同，用通用的降噪去压效果很差：
//
//   电流声（工频嗡声）  窄带、频点固定（50/60 Hz 及其谐波），最适合**陷波**
//                       只挖掉那几个频点，人声几乎不受影响，比整体降噪干净得多
//   键盘敲击            冲击型瞬态，能量瞬间暴涨再迅速衰减，适合**瞬态检测 + 快速衰减**
//
// 两者都是纯 IIR / 包络运算，开销可以忽略，也不需要任何模型权重。

using System;

namespace LanTalk
{
    /// <summary>
    /// 工频嗡声自动陷波。
    ///
    /// 工作方式：
    ///   1. 用 Goertzel 统计 50 Hz / 60 Hz 的能量占比（不需要 FFT）
    ///   2. 占比明显偏高 -> 判定为哪一国的电网频率
    ///   3. 对该频率的 1..N 次谐波挂二阶陷波
    ///
    /// 只有检测到才启用，避免无谓地挖掉正常低频。
    /// </summary>
    internal sealed class HumNotch
    {
        private readonly int _rate;
        private readonly Biquad[] _filters;
        private readonly Biquad[] _filtersAlt;   // 另一候选频率的一套，切换时免重建

        private const int Harmonics = 8;         // 处理 1..8 次谐波（50 Hz 到 400 Hz）
        private const int RateHz = 16000;

        // Goertzel 累加器
        private double _p50, _p60, _pTotal;
        private int _accum;
        private const int WindowSamples = 8000;  // 0.5 秒判定一次

        public int DetectedHz { get; private set; }   // 0=未检出，50 或 60
        public bool Enabled { get { return DetectedHz != 0; } }
        public double Confidence { get; private set; }

        public HumNotch(int rate)
        {
            _rate = rate;
            _filters = Build(rate, 50);
            _filtersAlt = Build(rate, 60);
        }

        private static Biquad[] Build(int rate, int f0)
        {
            Biquad[] arr = new Biquad[Harmonics];
            for (int h = 1; h <= Harmonics; h++)
            {
                double f = f0 * h;
                if (f > rate * 0.45) { arr[h - 1] = null; continue; }
                arr[h - 1] = Biquad.Notch(rate, f, 24.0);
            }
            return arr;
        }

        /// <summary>统计一段数据里的工频能量，攒够 0.5 秒判定一次。</summary>
        public void Analyze(short[] buf, int len)
        {
            if (len <= 0) return;
            double w50 = 2.0 * Math.PI * 50.0 / _rate;
            double w60 = 2.0 * Math.PI * 60.0 / _rate;
            double c50 = 2.0 * Math.Cos(w50), c60 = 2.0 * Math.Cos(w60);

            double s50a = 0, s50b = 0, s60a = 0, s60b = 0;
            for (int i = 0; i < len; i++)
            {
                double x = buf[i];
                double v50 = x + c50 * s50a - s50b; s50b = s50a; s50a = v50;
                double v60 = x + c60 * s60a - s60b; s60b = s60a; s60a = v60;
                _pTotal += x * x;
            }
            _p50 += s50a * s50a + s50b * s50b - c50 * s50a * s50b;
            _p60 += s60a * s60a + s60b * s60b - c60 * s60a * s60b;
            _accum += len;

            if (_accum < WindowSamples) return;

            double norm = _pTotal > 1.0 ? _pTotal : 1.0;
            double r50 = _p50 * 2.0 / norm;
            double r60 = _p60 * 2.0 / norm;

            int prev = DetectedHz;
            if (r50 > 0.03 && r50 > r60 * 1.5) { DetectedHz = 50; Confidence = r50; }
            else if (r60 > 0.03 && r60 > r50 * 1.5) { DetectedHz = 60; Confidence = r60; }
            else { DetectedHz = 0; Confidence = Math.Max(r50, r60); }

            if (DetectedHz != prev)
            {
                // 切换频率时把滤波器状态清掉，避免残留
                Biquad[] use = DetectedHz == 60 ? _filtersAlt : _filters;
                for (int i = 0; i < use.Length; i++) if (use[i] != null) use[i].Reset();
            }

            _p50 = _p60 = _pTotal = 0;
            _accum = 0;
        }

        /// <summary>对一段 PCM 施加陷波（原地）。未检出工频时什么都不做。</summary>
        public void Process(short[] buf, int len)
        {
            if (DetectedHz == 0) return;
            Biquad[] use = DetectedHz == 60 ? _filtersAlt : _filters;
            for (int i = 0; i < len; i++)
            {
                double v = buf[i];
                for (int h = 0; h < use.Length; h++)
                {
                    if (use[h] == null) continue;
                    v = use[h].Process(v);
                }
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                buf[i] = (short)v;
            }
        }

        public void Reset()
        {
            DetectedHz = 0;
            Confidence = 0;
            _p50 = _p60 = _pTotal = 0;
            _accum = 0;
            for (int i = 0; i < _filters.Length; i++) { if (_filters[i] != null) _filters[i].Reset(); }
            for (int i = 0; i < _filtersAlt.Length; i++) { if (_filtersAlt[i] != null) _filtersAlt[i].Reset(); }
        }
    }

    /// <summary>
    /// 键盘敲击这类冲击噪声的抑制。
    ///
    /// 原理：正常说话的能量包络是缓慢起伏的，键盘敲击是「瞬间暴涨 + 快速衰减」。
    /// 用短时能量和长时平均能量的比值做判据，命中就快速把这一小段压下去，
    /// 再用较慢的释放把它拉回来，避免产生"喘气"感。
    /// </summary>
    internal sealed class ClickGuard
    {
        private double _fast;        // 短时能量（指数平均，快）
        private double _slow;        // 长时平均能量（慢）
        private double _gain = 1.0;  // 当前增益
        private double _attack, _release;

        public int TriggerCount { get; private set; }
        public bool Enabled = true;

        /// <summary>比值超过这个倍数就判定为冲击。</summary>
        public double Threshold = 6.0;
        /// <summary>命中时的目标增益。</summary>
        public double DuckGain = 0.25;

        public ClickGuard(int rate, int frameSamples)
        {
            // 快：约一帧的时间常数；慢：约 0.5 秒
            _attack = 1.0 / Math.Max(1, frameSamples);
            _release = 1.0 / Math.Max(1, rate / 2);
        }

        public void Process(short[] buf, int len)
        {
            if (!Enabled || len <= 0) return;

            double e = 0;
            for (int i = 0; i < len; i++) { double x = buf[i]; e += x * x; }
            e /= len;

            if (_slow <= 0) { _slow = e; _fast = e; }
            _fast += (e - _fast) * 0.5;
            _slow += (e - _slow) * 0.02;

            bool hit = _slow > 100.0 && _fast > _slow * Threshold;
            if (hit) TriggerCount++;

            double target = hit ? DuckGain : 1.0;
            for (int i = 0; i < len; i++)
            {
                if (_gain > target) _gain += (target - _gain) * 0.6;          // 快攻
                else _gain += (target - _gain) * 0.02;                        // 慢放
                double v = buf[i] * _gain;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                buf[i] = (short)v;
            }
        }

        public void Reset()
        {
            _fast = _slow = 0;
            _gain = 1.0;
            TriggerCount = 0;
        }
    }

    /// <summary>二阶 IIR 双二阶滤波器，目前只用到带阻（陷波）。</summary>
    internal sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2;
        private double _x1, _x2, _y1, _y2;

        public static Biquad Notch(int rate, double f0, double q)
        {
            if (f0 <= 0 || f0 >= rate * 0.5) return null;
            double w0 = 2.0 * Math.PI * f0 / rate;
            double cw = Math.Cos(w0), sw = Math.Sin(w0);
            double alpha = sw / (2.0 * q);

            Biquad b = new Biquad();
            double a0 = 1.0 + alpha;
            b._b0 = 1.0 / a0;
            b._b1 = -2.0 * cw / a0;
            b._b2 = 1.0 / a0;
            b._a1 = -2.0 * cw / a0;
            b._a2 = (1.0 - alpha) / a0;
            return b;
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        public void Reset() { _x1 = _x2 = _y1 = _y2 = 0; }
    }
}
