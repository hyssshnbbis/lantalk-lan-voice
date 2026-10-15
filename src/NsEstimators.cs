// NsEstimators.cs —— WebRTC 降噪移植（第 3 层：分位数噪声估计、维纳滤波器）
// 移植自 sonora-ns / WebRTC `ns/quantile_noise_estimator.cc`、`ns/wiener_filter.cc`。BSD-3-Clause。
using System;

namespace LanTalk
{
    /// <summary>
    /// 分位数噪声估计：同时跑 3 个不同更新速率的 25% 分位跟踪器，
    /// 每个跟踪器计数到期时把自己的估计"提拔"为输出噪声谱。
    /// 好处是即使一直在说话，也能把噪声底噪跟住。
    /// </summary>
    internal sealed class NsQuantileNoiseEstimator
    {
        private const int Simult = 3;
        private const int Bins = NsConfig.FftSizeBy2Plus1;
        private const int LongStartup = NsConfig.LongStartupPhaseBlocks;

        private readonly float[] _density = new float[Simult * Bins];
        private readonly float[] _logQuantile = new float[Simult * Bins];
        private readonly float[] _quantile = new float[Bins];
        private readonly int[] _counter = new int[Simult];
        private int _numUpdates;

        public NsQuantileNoiseEstimator()
        {
            Reset();
        }

        public void Reset()
        {
            float oneBySimult = 1.0f / Simult;
            for (int i = 0; i < Simult; i++)
                _counter[i] = (int)Math.Floor(LongStartup * (i + 1.0f) * oneBySimult);
            for (int i = 0; i < _density.Length; i++) _density[i] = 0.3f;
            for (int i = 0; i < _logQuantile.Length; i++) _logQuantile[i] = 8.0f;
            Array.Clear(_quantile, 0, Bins);
            _numUpdates = 1;
        }

        public void Estimate(float[] signalSpectrum, float[] noiseSpectrum)
        {
            float[] logSpectrum = new float[Bins];
            NsFastMath.LogBatch(signalSpectrum, logSpectrum);

            int quantileIndexToReturn = -1;

            for (int s = 0; s < Simult; s++)
            {
                int k = s * Bins;
                float oneByCounterPlus1 = 1.0f / (_counter[s] + 1.0f);

                for (int i = 0; i < Bins; i++)
                {
                    int j = k + i;
                    float logSpec = logSpectrum[i];

                    float delta = _density[j] > 1.0f ? 40.0f / _density[j] : 40.0f;
                    float multiplier = delta * oneByCounterPlus1;
                    if (logSpec > _logQuantile[j]) _logQuantile[j] += 0.25f * multiplier;
                    else _logQuantile[j] -= 0.75f * multiplier;

                    const float width = 0.01f;
                    const float oneOverTwoWidth = 1.0f / (2.0f * width);
                    if (Math.Abs(logSpec - _logQuantile[j]) < width)
                        _density[j] = (_counter[s] * _density[j] + oneOverTwoWidth) * oneByCounterPlus1;
                }

                if (_counter[s] >= LongStartup)
                {
                    _counter[s] = 0;
                    if (_numUpdates >= LongStartup) quantileIndexToReturn = k;
                }
                _counter[s]++;
            }

            // 启动阶段顺序更新，用最后一个跟踪器，避免一开始噪声估计恒为 0
            if (_numUpdates < LongStartup)
            {
                quantileIndexToReturn = Bins * (Simult - 1);
                _numUpdates++;
            }

            if (quantileIndexToReturn >= 0)
            {
                int start = quantileIndexToReturn;
                for (int i = 0; i < Bins; i++)
                    _quantile[i] = NsFastMath.Exp(_logQuantile[start + i]);
            }

            Array.Copy(_quantile, noiseSpectrum, Bins);
        }
    }

    /// <summary>
    /// 维纳滤波器：由信号谱与噪声谱逐频点算出增益。
    /// 启动阶段会与一个参数化估计做加权融合（上游行为）。
    /// </summary>
    internal sealed class NsWienerFilter
    {
        private const int Bins = NsConfig.FftSizeBy2Plus1;
        private const int ShortStartup = NsConfig.ShortStartupPhaseBlocks;
        private const int LongStartup = NsConfig.LongStartupPhaseBlocks;

        private NsSuppressionParams _params;
        private readonly float[] _spectrumPrevProcess = new float[Bins];
        private readonly float[] _initialSpectralEstimate = new float[Bins];
        private readonly float[] _filter = new float[Bins];

        public float[] Filter { get { return _filter; } }

        public NsWienerFilter(NsSuppressionParams p)
        {
            _params = p;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_spectrumPrevProcess, 0, Bins);
            Array.Clear(_initialSpectralEstimate, 0, Bins);
            for (int i = 0; i < Bins; i++) _filter[i] = 1.0f;
        }

        public void SetParams(NsSuppressionParams p) { _params = p; }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public void Update(int numAnalyzedFrames, float[] noiseSpectrum, float[] prevNoiseSpectrum,
                           float[] parametricNoiseSpectrum, float[] signalSpectrum)
        {
            float overSub = _params.OverSubtractionFactor;
            float minGain = _params.MinimumAttenuatingGain;

            for (int i = 0; i < Bins; i++)
            {
                float prevTsa = _spectrumPrevProcess[i] / (prevNoiseSpectrum[i] + 0.0001f) * _filter[i];
                float currentTsa = signalSpectrum[i] > noiseSpectrum[i]
                    ? signalSpectrum[i] / (noiseSpectrum[i] + 0.0001f) - 1.0f
                    : 0.0f;
                float snrPrior = 0.98f * prevTsa + 0.02f * currentTsa;
                _filter[i] = Clamp(snrPrior / (overSub + snrPrior), minGain, 1.0f);
            }

            if (numAnalyzedFrames < ShortStartup)
            {
                const float oneByShort = 1.0f / ShortStartup;
                for (int i = 0; i < Bins; i++)
                {
                    _initialSpectralEstimate[i] += signalSpectrum[i];
                    float filterInitial = _initialSpectralEstimate[i] - overSub * parametricNoiseSpectrum[i];
                    filterInitial /= _initialSpectralEstimate[i] + 0.0001f;
                    filterInitial = Clamp(filterInitial, minGain, 1.0f);

                    filterInitial *= (ShortStartup - numAnalyzedFrames);
                    _filter[i] *= numAnalyzedFrames;
                    _filter[i] += filterInitial;
                    _filter[i] *= oneByShort;
                }
            }

            Array.Copy(signalSpectrum, _spectrumPrevProcess, Bins);
        }

        /// <summary>根据滤波前后能量比与语音概率算一个整体缩放系数。</summary>
        public float ComputeOverallScalingFactor(int numAnalyzedFrames, float priorSpeechProbability,
                                                 float energyBefore, float energyAfter)
        {
            if (!_params.UseAttenuationAdjustment || numAnalyzedFrames <= LongStartup) return 1.0f;

            float gain = NsFastMath.SqrtFast(energyAfter / (energyBefore + 1.0f));

            const float bLim = 0.5f;
            float scale1 = 1.0f;
            if (gain > bLim)
            {
                scale1 = 1.0f + 1.3f * (gain - bLim);
                if (gain * scale1 > 1.0f) scale1 = 1.0f / gain;
            }

            float scale2 = 1.0f;
            if (gain < bLim)
            {
                if (gain < _params.MinimumAttenuatingGain) gain = _params.MinimumAttenuatingGain;
                scale2 = 1.0f - 0.3f * (bLim - gain);
            }

            return priorSpeechProbability * scale1 + (1.0f - priorSpeechProbability) * scale2;
        }
    }

    /// <summary>这两层的上游单测。</summary>
    internal static class NsEstimatorTests
    {
        public static string Run()
        {
            int pass = 0, fail = 0;
            string firstFail = "";
            int bins = NsConfig.FftSizeBy2Plus1;

            // --- 初始滤波器全 1 ---
            {
                NsWienerFilter wf = new NsWienerFilter(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                bool ok = true;
                for (int i = 0; i < bins; i++) if (wf.Filter[i] != 1.0f) { ok = false; break; }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "初始滤波器不是全 1"; }
            }

            // --- 高信噪比时增益接近 1 ---
            {
                NsWienerFilter wf = new NsWienerFilter(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float[] signal = new float[bins];
                float[] noise = new float[bins];
                for (int i = 0; i < bins; i++) { signal[i] = 100.0f; noise[i] = 0.001f; }
                wf.Update(60, noise, noise, noise, signal);
                bool ok = true;
                for (int i = 0; i < bins; i++) if (wf.Filter[i] <= 0.9f) { ok = false; break; }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "高信噪比时增益未接近 1"; }
            }

            // --- 噪声远大于信号时压到谱底 ---
            {
                NsSuppressionParams p = NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB);
                NsWienerFilter wf = new NsWienerFilter(p);
                float[] signal = new float[bins];
                float[] noise = new float[bins];
                for (int i = 0; i < bins; i++) { signal[i] = 1.0f; noise[i] = 100.0f; }
                wf.Update(60, noise, noise, noise, signal);
                bool ok = true;
                for (int i = 0; i < bins; i++)
                    if (Math.Abs(wf.Filter[i] - p.MinimumAttenuatingGain) > 1e-4f) { ok = false; break; }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "强噪声时未压到谱底"; }
            }

            // --- 分位数噪声估计：常数输入谱应收敛到该谱的量级 ---
            {
                NsQuantileNoiseEstimator q = new NsQuantileNoiseEstimator();
                float[] sig = new float[bins];
                float[] nz = new float[bins];
                for (int i = 0; i < bins; i++) sig[i] = 100.0f;
                for (int f = 0; f < 400; f++) q.Estimate(sig, nz);
                bool finite = true;
                float avg = 0;
                for (int i = 0; i < bins; i++)
                {
                    if (float.IsNaN(nz[i]) || float.IsInfinity(nz[i]) || nz[i] <= 0f) finite = false;
                    avg += nz[i];
                }
                avg /= bins;
                bool ok = finite && avg > 20.0f && avg < 500.0f;
                if (ok) pass++;
                else { fail++; if (firstFail == "") firstFail = "分位数噪声估计收敛异常，均值 " + avg.ToString("F1"); }
            }

            // --- 分位数噪声估计：噪声变大后估计要跟着上去 ---
            {
                NsQuantileNoiseEstimator q = new NsQuantileNoiseEstimator();
                float[] sig = new float[bins];
                float[] nz = new float[bins];
                for (int i = 0; i < bins; i++) sig[i] = 50.0f;
                for (int f = 0; f < 300; f++) q.Estimate(sig, nz);
                float lo = 0; for (int i = 0; i < bins; i++) lo += nz[i];
                lo /= bins;
                for (int i = 0; i < bins; i++) sig[i] = 500.0f;
                for (int f = 0; f < 300; f++) q.Estimate(sig, nz);
                float hi = 0; for (int i = 0; i < bins; i++) hi += nz[i];
                hi /= bins;
                if (hi > lo * 3.0f) pass++;
                else { fail++; if (firstFail == "") firstFail = "噪声上升后估计没跟上（" + lo.ToString("F1") + " → " + hi.ToString("F1") + "）"; }
            }

            // --- 整体缩放系数：启动期恒为 1 ---
            {
                NsWienerFilter wf = new NsWienerFilter(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float g = wf.ComputeOverallScalingFactor(10, 0.5f, 100f, 50f);
                if (g == 1.0f) pass++; else { fail++; if (firstFail == "") firstFail = "启动期缩放系数不为 1"; }
            }

            return "NS估计层 通过 " + pass + " 失败 " + fail + (fail > 0 ? "（首个失败：" + firstFail + "）" : "");
        }
    }
}
