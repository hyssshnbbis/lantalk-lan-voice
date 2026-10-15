// NsFoundation.cs —— WebRTC 降噪模块移植（第 1 层：配置、快速数学、信号模型、直方图）
//
// 移植自 sonora 的 sonora-ns（Rust），后者是 WebRTC
// `webrtc/modules/audio_processing/ns/` 的逐行重写。BSD-3-Clause。
//
// 关键事实（决定了整条链路的数值口径）：
//   · 输入是 **int16 量纲**的浮点（[-32768, 32767]），不是归一化到 ±1；
//   · FFT 是标准**未归一化** DFT：real[k]=Σx[n]cos(2πkn/N)，imag[k]=-Σx[n]sin(2πkn/N)；
//   · 分析/合成窗是 sin(πi/192)，只作用于 256 点扩展帧的两端，中间 65 点不加权；
//   · 帧结构 = 96 点历史 + 160 点新样本 → 256 点 FFT，算法延迟 96 样本 = 6 ms。
using System;
using System.Runtime.InteropServices;

namespace LanTalk
{
    /// <summary>WebRTC 降噪的编译期常量。</summary>
    internal static class NsConfig
    {
        public const int FftSize = 256;                  // 256 点 FFT
        public const int FftSizeBy2Plus1 = FftSize / 2 + 1;   // 129 个唯一频点
        public const int NsFrameSize = 160;              // 10 ms @ 16 kHz
        public const int OverlapSize = FftSize - NsFrameSize; // 96
        public const int ShortStartupPhaseBlocks = 50;
        public const int LongStartupPhaseBlocks = 200;
        public const int FeatureUpdateWindowSize = 500;
        public const float LrtFeatureThr = 0.5f;
        public const float SfFeatureThr = 0.5f;
        public const float BinSizeLrt = 0.1f;
        public const float BinSizeSpecFlat = 0.05f;
        public const float BinSizeSpecDiff = 0.1f;
        public const float MinS16 = -32768.0f;
        public const float MaxS16 = 32767.0f;
    }

    /// <summary>压缩档位：分别对应 6 / 12 / 18 / 21 dB 的目标抑制量。</summary>
    internal enum NsSuppressionLevel
    {
        K6dB = 0,
        K12dB = 1,
        K18dB = 2,
        K21dB = 3
    }

    internal sealed class NsSuppressionParams
    {
        public float OverSubtractionFactor;
        public float MinimumAttenuatingGain;
        public bool UseAttenuationAdjustment;

        private NsSuppressionParams(float over, float gain, bool adjust)
        {
            OverSubtractionFactor = over;
            MinimumAttenuatingGain = gain;
            UseAttenuationAdjustment = adjust;
        }

        private static readonly NsSuppressionParams K6 = new NsSuppressionParams(1.0f, 0.5f, false);
        private static readonly NsSuppressionParams K12 = new NsSuppressionParams(1.0f, 0.25f, true);
        private static readonly NsSuppressionParams K18 = new NsSuppressionParams(1.1f, 0.125f, true);
        private static readonly NsSuppressionParams K21 = new NsSuppressionParams(1.25f, 0.09f, true);

        public static NsSuppressionParams ForLevel(NsSuppressionLevel lv)
        {
            switch (lv)
            {
                case NsSuppressionLevel.K6dB: return K6;
                case NsSuppressionLevel.K18dB: return K18;
                case NsSuppressionLevel.K21dB: return K21;
                default: return K12;
            }
        }
    }

    /// <summary>
    /// 与 C++ 实现完全一致的快速近似数学。
    /// 这些近似是刻意的（用速度换精度），必须和 C++ 一模一样，
    /// 否则整条降噪链路的数值会和参考实现对不上。
    /// </summary>
    internal static class NsFastMath
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatUint
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        private const float OneBy2Pow23 = 1.1920929e-7f;   // 1 / 2^23
        private const float Log2Bias = 126.942695f;
        private const float Ln2 = 0.6931472f;
        private const float Log10E = 0.4342945f;

        /// <summary>
        /// log2 的位技巧近似：把 float 的位模式当整数读出来、转成 float 再减偏置。
        /// 精度约 1%，但必须和 C++ 的 bit trick 完全一致。
        /// </summary>
        public static float FastLog2(float x)
        {
            FloatUint u = new FloatUint();
            u.F = x;
            float outv = u.U;                 // C++: float out = *(uint32_t*)&in;
            return outv * OneBy2Pow23 - Log2Bias;
        }

        public static float SqrtFast(float f) { return (float)Math.Sqrt(f); }
        public static float Log(float x) { return FastLog2(x) * Ln2; }
        public static float Pow2(float p) { return (float)Math.Pow(2.0, p); }
        public static float Pow(float x, float p) { return Pow2(p * FastLog2(x)); }
        public static float Exp(float x) { return Pow(10.0f, x * Log10E); }

        public static void LogBatch(float[] x, float[] y)
        {
            for (int i = 0; i < x.Length; i++) y[i] = Log(x[i]);
        }

        public static void ExpBatch(float[] x, float[] y)
        {
            for (int i = 0; i < x.Length; i++) y[i] = Exp(x[i]);
        }

        public static void ExpBatchSignFlip(float[] x, float[] y)
        {
            for (int i = 0; i < x.Length; i++) y[i] = Exp(-x[i]);
        }
    }

    /// <summary>从一帧里提取出来的信号特征。</summary>
    internal sealed class NsSignalModel
    {
        public float Lrt;
        public float SpectralDiff;
        public float SpectralFlatness;
        public readonly float[] AvgLogLrt = new float[NsConfig.FftSizeBy2Plus1];

        public NsSignalModel() { Reset(); }

        public void Reset()
        {
            Lrt = NsConfig.LrtFeatureThr;
            SpectralFlatness = NsConfig.SfFeatureThr;
            SpectralDiff = NsConfig.SfFeatureThr;
            for (int i = 0; i < AvgLogLrt.Length; i++) AvgLogLrt[i] = NsConfig.LrtFeatureThr;
        }

        public void CopyFrom(NsSignalModel o)
        {
            Lrt = o.Lrt;
            SpectralDiff = o.SpectralDiff;
            SpectralFlatness = o.SpectralFlatness;
            Array.Copy(o.AvgLogLrt, AvgLogLrt, AvgLogLrt.Length);
        }
    }

    /// <summary>先验信号模型：把 LRT、谱平坦度、谱差三个特征加权组合。</summary>
    internal sealed class NsPriorSignalModel
    {
        public float Lrt;
        public float FlatnessThreshold;
        public float TemplateDiffThreshold;
        public float LrtWeighting;
        public float FlatnessWeighting;
        public float DifferenceWeighting;

        public NsPriorSignalModel(float lrtInitialValue)
        {
            Lrt = lrtInitialValue;
            FlatnessThreshold = 0.5f;
            TemplateDiffThreshold = 0.5f;
            LrtWeighting = 1.0f;
            FlatnessWeighting = 0.0f;
            DifferenceWeighting = 0.0f;
        }
    }

    /// <summary>三个特征的直方图，用来估计特征阈值。</summary>
    internal sealed class NsHistograms
    {
        public const int HistogramSize = 1000;

        public readonly int[] LrtHist = new int[HistogramSize];
        public readonly int[] SpectralFlatnessHist = new int[HistogramSize];
        public readonly int[] SpectralDiffHist = new int[HistogramSize];

        public void Clear()
        {
            Array.Clear(LrtHist, 0, HistogramSize);
            Array.Clear(SpectralFlatnessHist, 0, HistogramSize);
            Array.Clear(SpectralDiffHist, 0, HistogramSize);
        }

        public void Update(NsSignalModel f)
        {
            const float oneByBinLrt = 1.0f / NsConfig.BinSizeLrt;
            if (f.Lrt < HistogramSize * NsConfig.BinSizeLrt && f.Lrt >= 0.0f)
            {
                int b = (int)(oneByBinLrt * f.Lrt);
                if (b >= 0 && b < HistogramSize) LrtHist[b]++;
            }

            const float oneByBinFlat = 1.0f / NsConfig.BinSizeSpecFlat;
            if (f.SpectralFlatness < HistogramSize * NsConfig.BinSizeSpecFlat && f.SpectralFlatness >= 0.0f)
            {
                int b = (int)(f.SpectralFlatness * oneByBinFlat);
                if (b >= 0 && b < HistogramSize) SpectralFlatnessHist[b]++;
            }

            const float oneByBinDiff = 1.0f / NsConfig.BinSizeSpecDiff;
            if (f.SpectralDiff < HistogramSize * NsConfig.BinSizeSpecDiff && f.SpectralDiff >= 0.0f)
            {
                int b = (int)(f.SpectralDiff * oneByBinDiff);
                if (b >= 0 && b < HistogramSize) SpectralDiffHist[b]++;
            }
        }
    }
}
