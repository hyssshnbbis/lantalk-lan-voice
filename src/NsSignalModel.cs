// NsSignalModel.cs —— WebRTC 降噪移植（第 5 层：信号特征提取 + 先验模型估计）
// 移植自 sonora-ns / WebRTC `ns/signal_model_estimator.cc`、`ns/prior_signal_model_estimator.cc`。
// BSD-3-Clause。
//
// 这一层做两件事：
//   1) 从频谱里提三个特征——LRT（对数似然比）、谱平坦度、谱差；
//   2) 把特征累积成直方图，每 500 帧分析一次直方图，反推出先验模型的门限与权重。
//      （这就是 WebRTC NS 能自适应不同噪声环境的原因。）
using System;

namespace LanTalk
{
    /// <summary>从直方图反推先验信号模型参数。</summary>
    internal sealed class NsPriorSignalModelEstimator
    {
        private const int HistSize = NsHistograms.HistogramSize;
        private readonly NsPriorSignalModel _priorModel;

        public NsPriorSignalModelEstimator(float lrtInitialValue)
        {
            _priorModel = new NsPriorSignalModel(lrtInitialValue);
        }

        public NsPriorSignalModel PriorModel { get { return _priorModel; } }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// 找直方图里最大的两个峰中的第一个。若两峰靠得很近且次峰不低于主峰一半，就把它们合并。
        /// </summary>
        private static void FindFirstOfTwoLargestPeaks(float binSize, int[] histogram,
                                                       out float peakPosition, out int peakWeight)
        {
            int peakValue = 0, secondaryPeakValue = 0;
            float peakPos = 0f, secondaryPeakPos = 0f;
            int weight = 0, secondaryWeight = 0;

            for (int i = 0; i < histogram.Length; i++)
            {
                int histVal = histogram[i];
                float binMid = (i + 0.5f) * binSize;
                if (histVal > peakValue)
                {
                    secondaryPeakValue = peakValue;
                    secondaryWeight = weight;
                    secondaryPeakPos = peakPos;

                    peakValue = histVal;
                    weight = histVal;
                    peakPos = binMid;
                }
                else if (histVal > secondaryPeakValue)
                {
                    secondaryPeakValue = histVal;
                    secondaryWeight = histVal;
                    secondaryPeakPos = binMid;
                }
            }

            if (Math.Abs(secondaryPeakPos - peakPos) < 2.0f * binSize
                && secondaryWeight > 0.5f * weight)
            {
                weight += secondaryWeight;
                peakPos = 0.5f * (peakPos + secondaryPeakPos);
            }

            peakPosition = peakPos;
            peakWeight = weight;
        }

        /// <summary>从前 10 个 bin 估 LRT 门限，并判断 LRT 波动是否过低（多半是纯噪声）。</summary>
        private static float UpdateLrt(int[] lrtHistogram, out bool lowLrtFluctuations)
        {
            float average = 0f, averageCompl = 0f, averageSquared = 0f;
            int count = 0;

            int n = lrtHistogram.Length < 10 ? lrtHistogram.Length : 10;
            for (int i = 0; i < n; i++)
            {
                float binMid = (i + 0.5f) * NsConfig.BinSizeLrt;
                average += lrtHistogram[i] * binMid;
                count += lrtHistogram[i];
            }
            if (count > 0) average /= count;

            for (int i = 0; i < lrtHistogram.Length; i++)
            {
                float binMid = (i + 0.5f) * NsConfig.BinSizeLrt;
                averageSquared += lrtHistogram[i] * binMid * binMid;
                averageCompl += lrtHistogram[i] * binMid;
            }
            const float oneByWindow = 1.0f / NsConfig.FeatureUpdateWindowSize;
            averageSquared *= oneByWindow;
            averageCompl *= oneByWindow;

            lowLrtFluctuations = averageSquared - average * averageCompl < 0.05f;

            const float maxLrt = 1.0f;
            const float minLrt = 0.2f;
            float priorModelLrt = lowLrtFluctuations
                ? maxLrt
                : Clamp(1.2f * average, minLrt, maxLrt);
            return priorModelLrt;
        }

        public void Update(NsHistograms histograms)
        {
            bool lowLrtFluctuations;
            float lrt = UpdateLrt(histograms.LrtHist, out lowLrtFluctuations);
            _priorModel.Lrt = lrt;

            float flatnessPeakPos, diffPeakPos;
            int flatnessPeakWeight, diffPeakWeight;
            FindFirstOfTwoLargestPeaks(NsConfig.BinSizeSpecFlat, histograms.SpectralFlatnessHist,
                                       out flatnessPeakPos, out flatnessPeakWeight);
            FindFirstOfTwoLargestPeaks(NsConfig.BinSizeSpecDiff, histograms.SpectralDiffHist,
                                       out diffPeakPos, out diffPeakWeight);

            bool useSpecFlat = flatnessPeakWeight >= 0.3f * NsConfig.FeatureUpdateWindowSize
                               && flatnessPeakPos >= 0.6f;
            bool useSpecDiff = diffPeakWeight >= 0.3f * NsConfig.FeatureUpdateWindowSize
                               && !lowLrtFluctuations;

            _priorModel.TemplateDiffThreshold = Clamp(1.2f * diffPeakPos, 0.16f, 1.0f);

            float oneByFeatureSum = 1.0f / (1.0f + (useSpecFlat ? 1f : 0f) + (useSpecDiff ? 1f : 0f));
            _priorModel.LrtWeighting = oneByFeatureSum;

            if (useSpecFlat)
            {
                _priorModel.FlatnessThreshold = Clamp(0.9f * flatnessPeakPos, 0.1f, 0.95f);
                _priorModel.FlatnessWeighting = oneByFeatureSum;
            }
            else
            {
                _priorModel.FlatnessWeighting = 0.0f;
            }

            if (useSpecDiff) _priorModel.DifferenceWeighting = oneByFeatureSum;
            else _priorModel.DifferenceWeighting = 0.0f;
        }
    }

    /// <summary>提特征、累积直方图、周期性刷新先验模型。</summary>
    internal sealed class NsSignalModelEstimator
    {
        private const int Bins = NsConfig.FftSizeBy2Plus1;
        private const float OneByBins = 1.0f / Bins;

        private float _diffNormalization;
        private float _signalEnergySum;
        private readonly NsHistograms _histograms = new NsHistograms();
        private int _histogramAnalysisCounter;
        private readonly NsPriorSignalModelEstimator _priorEstimator;
        private readonly NsSignalModel _features = new NsSignalModel();

        public NsSignalModelEstimator()
        {
            Reset();
            _priorEstimator = new NsPriorSignalModelEstimator(NsConfig.LrtFeatureThr);
        }

        public void Reset()
        {
            _diffNormalization = 0f;
            _signalEnergySum = 0f;
            _histograms.Clear();
            _histogramAnalysisCounter = NsConfig.FeatureUpdateWindowSize;
            _features.Reset();
        }

        public NsPriorSignalModel PriorModel { get { return _priorEstimator.PriorModel; } }
        public NsSignalModel Features { get { return _features; } }

        /// <summary>启动阶段用信号能量估一个归一化基准。</summary>
        public void AdjustNormalization(int numAnalyzedFrames, float signalEnergy)
        {
            _diffNormalization *= numAnalyzedFrames;
            _diffNormalization += signalEnergy;
            _diffNormalization /= numAnalyzedFrames + 1.0f;
        }

        /// <summary>谱差 = var(信号) - cov(信号,噪声)² / var(噪声)。</summary>
        private static float ComputeSpectralDiff(float[] conservativeNoiseSpectrum, float[] signalSpectrum,
                                                 float signalSpectralSum, float diffNormalization)
        {
            float noiseAverage = 0f;
            for (int i = 0; i < Bins; i++) noiseAverage += conservativeNoiseSpectrum[i];
            noiseAverage *= OneByBins;
            float signalAverage = signalSpectralSum * OneByBins;

            float covariance = 0f, noiseVariance = 0f, signalVariance = 0f;
            for (int i = 0; i < Bins; i++)
            {
                float signalDiff = signalSpectrum[i] - signalAverage;
                float noiseDiff = conservativeNoiseSpectrum[i] - noiseAverage;
                covariance += signalDiff * noiseDiff;
                noiseVariance += noiseDiff * noiseDiff;
                signalVariance += signalDiff * signalDiff;
            }
            covariance *= OneByBins;
            noiseVariance *= OneByBins;
            signalVariance *= OneByBins;

            float spectralDiff = signalVariance - (covariance * covariance) / (noiseVariance + 0.0001f);
            return spectralDiff / (diffNormalization + 0.0001f);
        }

        private void UpdateSpectralFlatness(float[] signalSpectrum, float signalSpectralSum)
        {
            const float averaging = 0.3f;

            for (int i = 1; i < Bins; i++)
            {
                if (signalSpectrum[i] == 0.0f)
                {
                    _features.SpectralFlatness -= averaging * _features.SpectralFlatness;
                    return;
                }
            }

            float num = 0f;
            for (int i = 1; i < Bins; i++) num += NsFastMath.Log(signalSpectrum[i]);

            float denom = signalSpectralSum - signalSpectrum[0];
            denom *= OneByBins;
            num *= OneByBins;

            float spectralTmp = NsFastMath.Exp(num) / denom;
            _features.SpectralFlatness += averaging * (spectralTmp - _features.SpectralFlatness);
        }

        private void UpdateSpectralLrt(float[] priorSnr, float[] postSnr)
        {
            float[] avgLogLrt = _features.AvgLogLrt;
            for (int i = 0; i < Bins; i++)
            {
                float tmp1 = 1.0f + 2.0f * priorSnr[i];
                float tmp2 = 2.0f * priorSnr[i] / (tmp1 + 0.0001f);
                float besselTmp = (postSnr[i] + 1.0f) * tmp2;
                avgLogLrt[i] += 0.5f * (besselTmp - NsFastMath.Log(tmp1) - avgLogLrt[i]);
            }

            float sum = 0f;
            for (int i = 0; i < Bins; i++) sum += avgLogLrt[i];
            _features.Lrt = sum * OneByBins;
        }

        public void Update(float[] priorSnr, float[] postSnr, float[] conservativeNoiseSpectrum,
                           float[] signalSpectrum, float signalSpectralSum, float signalEnergy)
        {
            UpdateSpectralFlatness(signalSpectrum, signalSpectralSum);

            float spectralDiff = ComputeSpectralDiff(conservativeNoiseSpectrum, signalSpectrum,
                                                     signalSpectralSum, _diffNormalization);
            _features.SpectralDiff += 0.3f * (spectralDiff - _features.SpectralDiff);

            _signalEnergySum += signalEnergy;

            // 直方图攒够 500 帧就反推一次先验模型参数
            _histogramAnalysisCounter--;
            if (_histogramAnalysisCounter > 0)
            {
                _histograms.Update(_features);
            }
            else
            {
                _priorEstimator.Update(_histograms);
                _histograms.Clear();
                _histogramAnalysisCounter = NsConfig.FeatureUpdateWindowSize;

                _signalEnergySum /= NsConfig.FeatureUpdateWindowSize;
                _diffNormalization = 0.5f * (_signalEnergySum + _diffNormalization);
                _signalEnergySum = 0f;
            }

            UpdateSpectralLrt(priorSnr, postSnr);
        }
    }

    internal static class NsSignalModelTests
    {
        public static string Run()
        {
            int pass = 0, fail = 0;
            string firstFail = "";
            int bins = NsConfig.FftSizeBy2Plus1;

            // --- 先验模型初值（上游 default_values / weightings_sum_to_one_initially）---
            {
                NsPriorSignalModelEstimator e = new NsPriorSignalModelEstimator(1.0f);
                NsPriorSignalModel m = e.PriorModel;
                bool ok = m.Lrt == 1.0f && m.FlatnessThreshold == 0.5f && m.TemplateDiffThreshold == 0.5f
                          && m.LrtWeighting == 1.0f && m.FlatnessWeighting == 0.0f && m.DifferenceWeighting == 0.0f
                          && m.LrtWeighting + m.FlatnessWeighting + m.DifferenceWeighting == 1.0f;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "先验模型初值不符"; }
            }

            // --- 空直方图：判为纯噪声，LRT 门限取上限 1.0，权重全压到 LRT 上 ---
            {
                NsPriorSignalModelEstimator e = new NsPriorSignalModelEstimator(0.5f);
                NsHistograms h = new NsHistograms();
                e.Update(h);
                NsPriorSignalModel m = e.PriorModel;
                bool ok = Math.Abs(m.Lrt - 1.0f) < 1e-6f
                          && Math.Abs(m.LrtWeighting - 1.0f) < 1e-6f
                          && Math.Abs(m.FlatnessWeighting) < 1e-6f
                          && Math.Abs(m.DifferenceWeighting) < 1e-6f
                          && Math.Abs(m.TemplateDiffThreshold - 0.16f) < 1e-6f;
                if (ok) pass++;
                else { fail++; if (firstFail == "") firstFail = "空直方图判定错误：lrt=" + m.Lrt + " tw=" + m.TemplateDiffThreshold; }
            }

            // --- 谱平坦度直方图有明显高峰时，平坦度特征应被启用 ---
            {
                NsPriorSignalModelEstimator e = new NsPriorSignalModelEstimator(0.5f);
                NsHistograms h = new NsHistograms();
                h.SpectralFlatnessHist[16] = 500;      // bin 中点 0.825，权重 500
                e.Update(h);
                NsPriorSignalModel m = e.PriorModel;
                float expectThr = 0.9f * 0.825f;
                bool ok = m.FlatnessWeighting > 0f
                          && Math.Abs(m.FlatnessThreshold - expectThr) < 1e-5f
                          && Math.Abs(m.LrtWeighting + m.FlatnessWeighting + m.DifferenceWeighting - 1.0f) < 1e-6f;
                if (ok) pass++;
                else { fail++; if (firstFail == "") firstFail = "平坦度特征启用判定错误：fw=" + m.FlatnessWeighting + " thr=" + m.FlatnessThreshold; }
            }

            // --- 信号模型：SNR 全 0 时 LRT 收敛到 0.230140（可解析推导） ---
            // 注意不是 0.25：公式里含 log_approximation(tmp1)，而 tmp1=1，
            // 快速对数在位技巧下给出 log(1)≈0.03972 而不是 0（这是该近似的最大偏差点）。
            {
                NsSignalModelEstimator e = new NsSignalModelEstimator();
                float[] prior = new float[bins];
                float[] post = new float[bins];
                float[] cons = new float[bins];
                float[] sig = new float[bins];
                for (int i = 0; i < bins; i++) sig[i] = 1.0f;
                e.Update(prior, post, cons, sig, 1.0f * bins, 1.0f);
                float lrt = e.Features.Lrt;
                // 解析值：0.5 + 0.5*(0 - log_approx(1) - 0.5)，log_approx(1) ≈ 0.0397208
                if (Math.Abs(lrt - 0.230140f) < 1e-5f) pass++;
                else { fail++; if (firstFail == "") firstFail = "LRT 解析值不符，得到 " + lrt.ToString("F6"); }
            }

            // --- 归一化基准调整（上游 adjust_normalization） ---
            {
                NsSignalModelEstimator e = new NsSignalModelEstimator();
                e.AdjustNormalization(0, 100.0f);
                // (0*0 + 100) / (0+1) = 100
                float[] prior = new float[bins];
                float[] post = new float[bins];
                float[] cons = new float[bins];
                float[] sig = new float[bins];
                for (int i = 0; i < bins; i++) sig[i] = 10.0f;
                e.Update(prior, post, cons, sig, 10.0f * bins, 1.0f);
                // diff_normalization=100 时谱差应当很小
                bool ok = e.Features.SpectralDiff < 1.0f;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "归一化后谱差异常大"; }
            }

            // --- 攒满 500 帧后先验模型会被刷新 ---
            {
                NsSignalModelEstimator e = new NsSignalModelEstimator();
                float before = e.PriorModel.TemplateDiffThreshold;
                float[] prior = new float[bins];
                float[] post = new float[bins];
                float[] cons = new float[bins];
                float[] sig = new float[bins];
                for (int i = 0; i < bins; i++) { sig[i] = 10.0f + i; cons[i] = 5.0f; }
                for (int f = 0; f < 501; f++) e.Update(prior, post, cons, sig, 0f, 1.0f);
                bool ok = e.PriorModel.TemplateDiffThreshold != before
                          || Math.Abs(e.PriorModel.Lrt - 0.5f) > 1e-6f;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "500 帧后先验模型未刷新"; }
            }

            return "NS信号模型 通过 " + pass + " 失败 " + fail + (fail > 0 ? "（首个失败：" + firstFail + "）" : "");
        }
    }
}
