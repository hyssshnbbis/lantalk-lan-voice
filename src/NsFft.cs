// NsFft.cs —— WebRTC 降噪移植（第 2 层：256 点实数 FFT、滤波器组窗、帧操作）
//
// 上游 ns_fft.rs 是对 Ooura fft4g 的封装。这里不照搬 Ooura，而是直接实现
// 它等价的标准变换：
//   正向 = 未归一化 DFT：real[k] = Σ x[n]cos(2πkn/256)，imag[k] = -Σ x[n]sin(2πkn/256)
//        （Ooura rdft 的输出就是这个量纲，ns_fft 只是把它拆成实部/虚部两个数组）
//   反向 = 标准归一化逆变换，保证 ifft(fft(x)) == x
// 上游的 fft_ifft_roundtrip 测试同时被移植过来验证这一点。
using System;

namespace LanTalk
{
    /// <summary>256 点实数 FFT + WebRTC 降噪用的滤波器组窗与帧操作。</summary>
    internal static class NsFft
    {
        private const int N = NsConfig.FftSize;

        // 旋转因子表
        private static readonly float[] CosTab = new float[N / 2 + 1];
        private static readonly float[] SinTab = new float[N / 2 + 1];

        // 滤波器组窗的前半段：sin(π i / 192)，共 96 点（上游以常量表给出，这里按公式算）
        private static readonly float[] WindowFirstHalf = new float[NsConfig.OverlapSize];

        static NsFft()
        {
            for (int i = 0; i <= N / 2; i++)
            {
                double a = 2.0 * Math.PI * i / N;
                CosTab[i] = (float)Math.Cos(a);
                SinTab[i] = (float)Math.Sin(a);
            }
            for (int i = 0; i < NsConfig.OverlapSize; i++)
                WindowFirstHalf[i] = (float)Math.Sin(Math.PI * i / 192.0);
        }

        /// <summary>正向 FFT：把 256 点时域数据变换到 real/imag（只需前 129 个频点）。</summary>
        public static void Forward(float[] timeData, float[] real, float[] imag)
        {
            float[] re = new float[N];
            float[] im = new float[N];
            Array.Copy(timeData, re, N);

            Complex(re, im, false);

            real[0] = re[0];
            imag[0] = 0f;
            real[NsConfig.FftSizeBy2Plus1 - 1] = re[N / 2];
            imag[NsConfig.FftSizeBy2Plus1 - 1] = 0f;
            for (int i = 1; i < N / 2; i++)
            {
                real[i] = re[i];
                imag[i] = im[i];
            }
        }

        /// <summary>反向 FFT：从 real/imag 重建时域（归一化，保证与 Forward 互逆）。</summary>
        public static void Inverse(float[] real, float[] imag, float[] timeData)
        {
            float[] re = new float[N];
            float[] im = new float[N];

            // 按共轭对称铺满整个频谱
            re[0] = real[0]; im[0] = 0f;
            re[N / 2] = real[NsConfig.FftSizeBy2Plus1 - 1]; im[N / 2] = 0f;
            for (int i = 1; i < N / 2; i++)
            {
                re[i] = real[i];
                im[i] = imag[i];
                re[N - i] = real[i];
                im[N - i] = -imag[i];
            }

            Complex(re, im, true);
            Array.Copy(re, timeData, N);
        }

        /// <summary>标准基 2 复数 FFT（就地，inverse=true 时归一化）。</summary>
        private static void Complex(float[] re, float[] im, bool inverse)
        {
            for (int i = 1, j = 0; i < N; i++)
            {
                int bit = N >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    float t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }

            for (int len = 2; len <= N; len <<= 1)
            {
                int half = len >> 1;
                int step = N / len;
                for (int i = 0; i < N; i += len)
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
                float inv = 1f / N;
                for (int i = 0; i < N; i++) { re[i] *= inv; im[i] *= inv; }
            }
        }

        // ================= 帧操作（对应上游的 filterbank 部分） =================

        /// <summary>
        /// 分析/合成窗：两端各 96 点乘 sin(πi/192)，中间 65 点保持 1.0。
        /// 与上游 apply_filterbank_window 完全一致（含"尾段倒序、跳过 i=0"这个细节）。
        /// </summary>
        public static void ApplyFilterbankWindow(float[] x)
        {
            for (int i = 0; i < NsConfig.OverlapSize; i++) x[i] *= WindowFirstHalf[i];
            // x[96..160] 不动
            int tail = N - NsConfig.NsFrameSize - 1;          // 95
            for (int i = 0; i < tail; i++)
                x[NsConfig.NsFrameSize + 1 + i] *= WindowFirstHalf[NsConfig.OverlapSize - 1 - i];
        }

        /// <summary>用 96 点历史 + 160 点新样本拼出 256 点扩展帧，并更新历史。</summary>
        public static void FormExtendedFrame(float[] frame, float[] oldData, float[] extendedFrame)
        {
            Array.Copy(oldData, 0, extendedFrame, 0, oldData.Length);
            Array.Copy(frame, 0, extendedFrame, oldData.Length, NsConfig.NsFrameSize);
            Array.Copy(extendedFrame, NsConfig.NsFrameSize, oldData, 0, oldData.Length);
        }

        /// <summary>重叠相加：前 96 点与上一帧尾巴相加，末 96 点存为下一帧的尾巴。</summary>
        public static void OverlapAndAdd(float[] extendedFrame, float[] overlapMemory, float[] outputFrame)
        {
            for (int i = 0; i < NsConfig.OverlapSize; i++)
                outputFrame[i] = overlapMemory[i] + extendedFrame[i];
            Array.Copy(extendedFrame, NsConfig.OverlapSize, outputFrame, NsConfig.OverlapSize,
                       NsConfig.NsFrameSize - NsConfig.OverlapSize);
            Array.Copy(extendedFrame, NsConfig.NsFrameSize, overlapMemory, 0, NsConfig.OverlapSize);
        }

        /// <summary>幅度谱：sqrt(re²+im²)+1，直流与奈奎斯特只用实部。</summary>
        public static void ComputeMagnitudeSpectrum(float[] real, float[] imag, float[] signalSpectrum)
        {
            signalSpectrum[0] = Math.Abs(real[0]) + 1.0f;
            signalSpectrum[NsConfig.FftSizeBy2Plus1 - 1] =
                Math.Abs(real[NsConfig.FftSizeBy2Plus1 - 1]) + 1.0f;
            for (int i = 1; i < NsConfig.FftSizeBy2Plus1 - 1; i++)
                signalSpectrum[i] = NsFastMath.SqrtFast(real[i] * real[i] + imag[i] * imag[i]) + 1.0f;
        }

        public static float ComputeEnergy(float[] x)
        {
            float e = 0f;
            for (int i = 0; i < x.Length; i++) e += x[i] * x[i];
            return e;
        }

        /// <summary>
        /// 先验/后验信噪比。与上游 compute_snr 一致：
        /// 先验用判决引导（0.98 的系数在这里是硬编码的）。
        /// </summary>
        public static void ComputeSnr(float[] filter, float[] prevSignalSpectrum, float[] signalSpectrum,
                                      float[] prevNoiseSpectrum, float[] noiseSpectrum,
                                      float[] priorSnr, float[] postSnr)
        {
            for (int i = 0; i < NsConfig.FftSizeBy2Plus1; i++)
            {
                float prevEstimate = prevSignalSpectrum[i] / (prevNoiseSpectrum[i] + 0.0001f) * filter[i];
                if (signalSpectrum[i] > noiseSpectrum[i])
                    postSnr[i] = signalSpectrum[i] / (noiseSpectrum[i] + 0.0001f) - 1.0f;
                else
                    postSnr[i] = 0.0f;
                priorSnr[i] = 0.98f * prevEstimate + 0.02f * postSnr[i];
            }
        }

        /// <summary>把一帧裁到 int16 量纲范围内。</summary>
        public static void ClampFrame(float[] frame)
        {
            for (int i = 0; i < frame.Length; i++)
            {
                if (frame[i] < NsConfig.MinS16) frame[i] = NsConfig.MinS16;
                else if (frame[i] > NsConfig.MaxS16) frame[i] = NsConfig.MaxS16;
            }
        }

        // ================= 与上游对齐的自检 =================

        /// <summary>把上游 sonora-ns 的单测搬过来，返回 (通过数, 失败数, 说明)。</summary>
        public static string SelfTest()
        {
            int pass = 0, fail = 0;
            string firstFail = "";

            // --- fast_log2f：必须和 C++ 的位技巧完全一致 ---
            {
                float[] vals = { 0.001f, 0.1f, 0.5f, 1.0f, 2.0f, 10.0f, 100.0f, 1000.0f };
                bool ok = true;
                for (int i = 0; i < vals.Length; i++)
                {
                    // C++ 参考：float out = *(uint32_t*)&in; out *= 1.1920929e-7f; out -= 126.942695f;
                    byte[] b = BitConverter.GetBytes(vals[i]);
                    float cpp = BitConverter.ToUInt32(b, 0) * 1.1920929e-7f - 126.942695f;
                    if (NsFastMath.FastLog2(vals[i]) != cpp) ok = false;
                }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "fast_log2f 与 C++ 位技巧不一致"; }

                // log2 的幂次近似（上游 fast_log2f_powers_of_two）
                ok = Math.Abs(NsFastMath.FastLog2(1.0f)) < 0.1f
                  && Math.Abs(NsFastMath.FastLog2(2.0f) - 1f) < 0.1f
                  && Math.Abs(NsFastMath.FastLog2(4.0f) - 2f) < 0.1f
                  && Math.Abs(NsFastMath.FastLog2(8.0f) - 3f) < 0.1f
                  && Math.Abs(NsFastMath.FastLog2(0.5f) + 1f) < 0.1f;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "fast_log2f 幂次近似超差"; }
            }

            // --- 直方图分箱（上游 update_increments_correct_bins / accumulates / out_of_range） ---
            {
                NsHistograms h = new NsHistograms();
                NsSignalModel m = new NsSignalModel();
                m.Lrt = 0.5f; m.SpectralFlatness = 0.25f; m.SpectralDiff = 1.0f;
                h.Update(m); h.Update(m); h.Update(m);
                bool ok = h.LrtHist[5] == 3 && h.SpectralFlatnessHist[5] == 3
                       && h.SpectralDiffHist[10] == 3 && h.LrtHist[0] == 0 && h.LrtHist[6] == 0;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "直方图分箱错误"; }

                NsHistograms h2 = new NsHistograms();
                NsSignalModel bad = new NsSignalModel();
                bad.Lrt = -1.0f; bad.SpectralFlatness = 1000.0f; bad.SpectralDiff = -0.1f;
                h2.Update(bad);
                ok = true;
                for (int i = 0; i < NsHistograms.HistogramSize; i++)
                    if (h2.LrtHist[i] != 0 || h2.SpectralFlatnessHist[i] != 0 || h2.SpectralDiffHist[i] != 0) { ok = false; break; }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "直方图越界值未被忽略"; }
            }

            // --- FFT 往返（上游 fft_ifft_roundtrip） ---
            {
                float[] time = new float[N];
                for (int i = 0; i < N; i++) time[i] = (float)Math.Sin(i * 0.05);
                float[] origin = (float[])time.Clone();
                float[] real = new float[N];
                float[] imag = new float[N];
                Forward(time, real, imag);
                bool ok = imag[0] == 0f && imag[NsConfig.FftSizeBy2Plus1 - 1] == 0f;
                float[] back = new float[N];
                Inverse(real, imag, back);
                float maxErr = 0f;
                for (int i = 0; i < N; i++)
                {
                    float e = Math.Abs(origin[i] - back[i]);
                    if (e > maxErr) maxErr = e;
                }
                if (ok && maxErr < 1e-4f) pass++;
                else { fail++; if (firstFail == "") firstFail = "FFT 往返误差 " + maxErr; }
            }

            // --- FFT 直流（上游 fft_dc_signal）：直流帧的能量应集中在 real[0] ---
            {
                float[] time = new float[N];
                for (int i = 0; i < N; i++) time[i] = 1.0f;
                float[] real = new float[N];
                float[] imag = new float[N];
                Forward(time, real, imag);
                bool ok = Math.Abs(real[0] - N) < 1e-3f;
                for (int k = 1; k < NsConfig.FftSizeBy2Plus1 && ok; k++)
                    if (Math.Abs(real[k]) > 1e-3f || Math.Abs(imag[k]) > 1e-3f) ok = false;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "FFT 直流响应不正确"; }
            }

            // --- FFT 冲激（上游 fft_impulse）：全 1 的实部 ---
            {
                float[] time = new float[N];
                time[0] = 1.0f;
                float[] real = new float[N];
                float[] imag = new float[N];
                Forward(time, real, imag);
                bool ok = true;
                for (int k = 0; k < NsConfig.FftSizeBy2Plus1 && ok; k++)
                    if (Math.Abs(real[k] - 1.0f) > 1e-4f || Math.Abs(imag[k]) > 1e-4f) ok = false;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "FFT 冲激响应不正确"; }
            }

            // --- 窗表：sin(πi/192)，抽查上游常量表里的几个点 ---
            {
                float[] expect = { 0.00000000f, 0.01636173f, 0.06540313f, 0.50000000f,
                                   0.70710678f, 0.92387953f, 0.99986614f };
                int[] idx = { 0, 1, 4, 32, 48, 72, 95 };
                bool ok = true;
                for (int i = 0; i < idx.Length; i++)
                {
                    float got = (float)Math.Sin(Math.PI * idx[i] / 192.0);
                    if (Math.Abs(got - expect[i]) > 1e-7f) ok = false;
                }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "窗表与上游常量不符"; }
            }

            return "NS核心 通过 " + pass + " 失败 " + fail + (fail > 0 ? "（首个失败：" + firstFail + "）" : "");
        }
    }
}
