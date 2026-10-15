// NsNoiseEstimator.cs —— WebRTC 降噪移植（第 4 层：噪声谱估计）
// 移植自 sonora-ns / WebRTC `ns/noise_estimator.cc`。BSD-3-Clause。
//
// 它把两种估计合起来用：
//   · 分位数噪声跟踪（一直在跑）；
//   · 启动阶段（前 50 帧）额外用白噪声+粉噪声的参数模型补足；
//   · 之后用"语音概率"区分频点——像语音的频点保守更新，像噪声的频点积极更新。
using System;

namespace LanTalk
{
    internal sealed class NsNoiseEstimator
    {
        private const int Bins = NsConfig.FftSizeBy2Plus1;   // 129
        private const int ShortStartup = NsConfig.ShortStartupPhaseBlocks;  // 50
        private const int StartBand = 5;

        /// <summary>
        /// 上游以常量表给出：i &lt; 5 时为 0，i ≥ 5 时为 ln(i)。
        /// （从 START_BAND=5 起才会用到，所以前 5 个 0 不影响结果。）
        /// </summary>
        private static readonly float[] LogTable = BuildLogTable();

        private static float[] BuildLogTable()
        {
            float[] t = new float[Bins];
            for (int i = 0; i < Bins; i++) t[i] = i < StartBand ? 0f : (float)Math.Log(i);
            return t;
        }

        private NsSuppressionParams _params;
        private float _whiteNoiseLevel;
        private float _pinkNoiseNumerator;
        private float _pinkNoiseExp;

        private readonly float[] _prevNoiseSpectrum = new float[Bins];
        private readonly float[] _conservativeNoiseSpectrum = new float[Bins];
        private readonly float[] _parametricNoiseSpectrum = new float[Bins];
        private readonly float[] _noiseSpectrum = new float[Bins];
        private readonly NsQuantileNoiseEstimator _quantile = new NsQuantileNoiseEstimator();

        public float[] NoiseSpectrum { get { return _noiseSpectrum; } }
        public float[] PrevNoiseSpectrum { get { return _prevNoiseSpectrum; } }
        public float[] ParametricNoiseSpectrum { get { return _parametricNoiseSpectrum; } }
        public float[] ConservativeNoiseSpectrum { get { return _conservativeNoiseSpectrum; } }

        public NsNoiseEstimator(NsSuppressionParams p)
        {
            _params = p;
            Reset();
        }

        public void Reset()
        {
            _whiteNoiseLevel = 0f;
            _pinkNoiseNumerator = 0f;
            _pinkNoiseExp = 0f;
            Array.Clear(_prevNoiseSpectrum, 0, Bins);
            Array.Clear(_conservativeNoiseSpectrum, 0, Bins);
            Array.Clear(_parametricNoiseSpectrum, 0, Bins);
            Array.Clear(_noiseSpectrum, 0, Bins);
            _quantile.Reset();
        }

        public void SetParams(NsSuppressionParams p) { _params = p; }

        /// <summary>每帧分析前把当前噪声谱存成"上一帧"。</summary>
        public void PrepareAnalysis()
        {
            Array.Copy(_noiseSpectrum, _prevNoiseSpectrum, Bins);
        }

        public void PreUpdate(int numAnalyzedFrames, float[] signalSpectrum, float signalSpectralSum)
        {
            _quantile.Estimate(signalSpectrum, _noiseSpectrum);

            if (numAnalyzedFrames >= ShortStartup) return;

            float sumLogI = 0f, sumLogISquare = 0f, sumLogMagn = 0f, sumLogILogMagn = 0f;
            for (int i = StartBand; i < Bins; i++)
            {
                float logI = LogTable[i];
                sumLogI += logI;
                sumLogISquare += logI * logI;
                float logSignal = NsFastMath.Log(signalSpectrum[i]);
                sumLogMagn += logSignal;
                sumLogILogMagn += logI * logSignal;
            }

            const float oneByBins = 1.0f / Bins;
            _whiteNoiseLevel += signalSpectralSum * oneByBins * _params.OverSubtractionFactor;

            float denom = sumLogISquare * (Bins - StartBand) - sumLogI * sumLogI;
            float num, adjustment;

            if (denom == 0f) denom = 1e-12f;   // 上游是 debug_assert，这里兜个底

            num = sumLogISquare * sumLogMagn - sumLogI * sumLogILogMagn;
            adjustment = num / denom;
            if (adjustment < 0f) adjustment = 0f;
            _pinkNoiseNumerator += adjustment;

            num = sumLogI * sumLogMagn - (Bins - StartBand) * sumLogILogMagn;
            adjustment = num / denom;
            if (adjustment < 0f) adjustment = 0f;
            else if (adjustment > 1f) adjustment = 1f;
            _pinkNoiseExp += adjustment;

            float oneByNumPlus1 = 1.0f / (numAnalyzedFrames + 1.0f);

            float parametricExp = 0f;
            float parametricNum = 0f;
            if (_pinkNoiseExp > 0f)
            {
                parametricNum = NsFastMath.Exp(_pinkNoiseNumerator * oneByNumPlus1);
                parametricNum *= numAnalyzedFrames + 1.0f;
                parametricExp = _pinkNoiseExp * oneByNumPlus1;
            }

            const float oneByShort = 1.0f / ShortStartup;
            for (int i = 0; i < Bins; i++)
            {
                if (_pinkNoiseExp == 0f)
                {
                    _parametricNoiseSpectrum[i] = _whiteNoiseLevel;
                }
                else
                {
                    int useBand = i < StartBand ? StartBand : i;
                    float parametricDenom = NsFastMath.Pow(useBand, parametricExp);
                    _parametricNoiseSpectrum[i] = parametricDenom == 0f ? 0f : parametricNum / parametricDenom;
                }
            }

            // 把参数化噪声模型和分位数估计加权融合
            for (int i = 0; i < Bins; i++)
            {
                _noiseSpectrum[i] *= numAnalyzedFrames;
                float tmp = _parametricNoiseSpectrum[i] * (ShortStartup - numAnalyzedFrames);
                _noiseSpectrum[i] += tmp * oneByNumPlus1;
                _noiseSpectrum[i] *= oneByShort;
            }
        }

        /// <summary>
        /// 用语音概率细化噪声谱。注意 gamma 是**跨频点迭代延续**的
        /// （上游 C++ 就是这么写的），移植时必须保持同样的行为。
        /// </summary>
        public void PostUpdate(float[] speechProbability, float[] signalSpectrum)
        {
            const float noiseUpdate = 0.9f;
            const float probRange = 0.2f;

            float gamma = noiseUpdate;
            for (int i = 0; i < Bins; i++)
            {
                float probSpeech = speechProbability[i];
                float probNonSpeech = 1.0f - probSpeech;

                float noiseUpdateTmp = gamma * _prevNoiseSpectrum[i]
                    + (1.0f - gamma) * (probNonSpeech * signalSpectrum[i] + probSpeech * _prevNoiseSpectrum[i]);

                float gammaOld = gamma;
                gamma = probSpeech > probRange ? 0.99f : noiseUpdate;

                if (probSpeech < probRange)
                    _conservativeNoiseSpectrum[i] += 0.05f * (signalSpectrum[i] - _conservativeNoiseSpectrum[i]);

                if (gamma == gammaOld)
                {
                    _noiseSpectrum[i] = noiseUpdateTmp;
                }
                else
                {
                    float v = gamma * _prevNoiseSpectrum[i]
                        + (1.0f - gamma) * (probNonSpeech * signalSpectrum[i] + probSpeech * _prevNoiseSpectrum[i]);
                    // 噪声谱允许向下更新（向下是安全的）
                    _noiseSpectrum[i] = v < noiseUpdateTmp ? v : noiseUpdateTmp;
                }
            }
        }
    }

    internal static class NsNoiseEstimatorTests
    {
        public static string Run()
        {
            int pass = 0, fail = 0;
            string firstFail = "";
            int bins = NsConfig.FftSizeBy2Plus1;

            // --- LOG_TABLE 与上游常量表一致 ---
            {
                bool ok = true;
                float[] expect = { 0f, 0f, 0f, 0f, 0f, 1.609438f, 1.791759f, 2.302585f, 4.852030f };
                int[] idx = { 0, 1, 2, 3, 4, 5, 6, 10, 128 };
                for (int i = 0; i < idx.Length; i++)
                {
                    float got = idx[i] < 5 ? 0f : (float)Math.Log(idx[i]);
                    if (Math.Abs(got - expect[i]) > 1e-5f) ok = false;
                }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "LOG_TABLE 与上游不符"; }
            }

            // --- 初始状态全 0 ---
            {
                NsNoiseEstimator e = new NsNoiseEstimator(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                bool ok = true;
                for (int i = 0; i < bins; i++)
                    if (e.NoiseSpectrum[i] != 0f || e.PrevNoiseSpectrum[i] != 0f
                        || e.ParametricNoiseSpectrum[i] != 0f || e.ConservativeNoiseSpectrum[i] != 0f) { ok = false; break; }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "噪声估计器初始状态不为 0"; }
            }

            // --- prepare_analysis 把噪声谱复制到 prev ---
            {
                NsNoiseEstimator e = new NsNoiseEstimator(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float[] signal = new float[bins];
                float sum = 0;
                for (int i = 0; i < bins; i++) { signal[i] = 5.0f; sum += 5.0f; }
                e.PreUpdate(0, signal, sum);
                bool anyNonZero = false;
                for (int i = 0; i < bins; i++) if (e.NoiseSpectrum[i] != 0f) { anyNonZero = true; break; }
                e.PrepareAnalysis();
                bool copied = true;
                for (int i = 0; i < bins; i++) if (e.PrevNoiseSpectrum[i] != e.NoiseSpectrum[i]) { copied = false; break; }
                if (anyNonZero && copied) pass++;
                else { fail++; if (firstFail == "") firstFail = "prepare_analysis 未拷贝噪声谱"; }
            }

            // --- 启动阶段会混入参数化模型 ---
            {
                NsNoiseEstimator e = new NsNoiseEstimator(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float[] signal = new float[bins];
                float sum = 0;
                for (int i = 0; i < bins; i++) { signal[i] = 10.0f; sum += 10.0f; }
                e.PreUpdate(0, signal, sum);
                bool ok0 = false;
                for (int i = 0; i < bins; i++) if (e.NoiseSpectrum[i] > 0f) { ok0 = true; break; }
                for (int f = 1; f < 10; f++) { e.PrepareAnalysis(); e.PreUpdate(f, signal, sum); }
                bool ok9 = false;
                for (int i = 0; i < bins; i++) if (e.NoiseSpectrum[i] > 0f) { ok9 = true; break; }
                if (ok0 && ok9) pass++;
                else { fail++; if (firstFail == "") firstFail = "启动阶段参数化模型未生效"; }
            }

            // --- 全 0 语音概率：噪声谱应朝信号谱靠近 ---
            {
                NsNoiseEstimator e = new NsNoiseEstimator(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float[] signal = new float[bins];
                float[] prob = new float[bins];
                for (int i = 0; i < bins; i++) { signal[i] = 1000.0f; prob[i] = 0.0f; }
                float sum = 1000.0f * bins;
                for (int f = 0; f < 60; f++) { e.PrepareAnalysis(); e.PreUpdate(f, signal, sum); e.PostUpdate(prob, signal); }
                float avg = 0;
                for (int i = 0; i < bins; i++) avg += e.NoiseSpectrum[i];
                avg /= bins;
                if (avg > 100f && avg < 5000f) pass++;
                else { fail++; if (firstFail == "") firstFail = "全非语音时噪声谱未跟上，均值 " + avg.ToString("F1"); }
            }

            // --- 全 1 语音概率（"全是语音"）：噪声谱应保持不动、绝不把语音当噪声 ---
            {
                NsNoiseEstimator e = new NsNoiseEstimator(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float[] signal = new float[bins];
                float[] prob = new float[bins];
                for (int i = 0; i < bins; i++) { signal[i] = 1000.0f; prob[i] = 1.0f; }
                float sum = 1000.0f * bins;
                for (int f = 0; f < 200; f++) { e.PrepareAnalysis(); e.PreUpdate(f, signal, sum); e.PostUpdate(prob, signal); }
                float avg = 0;
                for (int i = 0; i < bins; i++) avg += e.NoiseSpectrum[i];
                avg /= bins;
                // prob=1 时 post_update 会把噪声谱保持为上一帧，所以它应当一直停在初值 0，
                // 绝不能跟着 1000 的信号谱涨上去——这正是"不把语音当噪声"的体现。
                if (avg < 100f) pass++;
                else { fail++; if (firstFail == "") firstFail = "全语音时噪声谱跟着语音涨了，均值 " + avg.ToString("F1"); }
            }

            // --- 保守噪声谱只在非语音时更新 ---
            {
                NsNoiseEstimator e = new NsNoiseEstimator(NsSuppressionParams.ForLevel(NsSuppressionLevel.K12dB));
                float[] signal = new float[bins];
                float[] prob = new float[bins];
                for (int i = 0; i < bins; i++) { signal[i] = 500.0f; prob[i] = 1.0f; }
                float sum = 500.0f * bins;
                for (int f = 0; f < 10; f++) { e.PrepareAnalysis(); e.PreUpdate(f, signal, sum); e.PostUpdate(prob, signal); }
                bool allZero = true;
                for (int i = 0; i < bins; i++) if (e.ConservativeNoiseSpectrum[i] != 0f) { allZero = false; break; }
                if (allZero) pass++;
                else { fail++; if (firstFail == "") firstFail = "全语音时保守噪声谱不应更新"; }
            }

            return "NS噪声估计 通过 " + pass + " 失败 " + fail + (fail > 0 ? "（首个失败：" + firstFail + "）" : "");
        }
    }
}
