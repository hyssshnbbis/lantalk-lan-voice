// NsSuppressor.cs —— WebRTC 降噪移植（第 7 层：完整主流程 + 对外接口）
// 移植自 sonora-ns / WebRTC `ns/noise_suppressor.cc`。BSD-3-Clause。
//
// 每 10ms（160 样本）走一次：analyze() 估噪声与语音概率，process() 算增益并合成。
// 注意 analyze 与 process 用的是**两条独立的历史缓冲**——上游这么设计是为了让
// analyze 能在回声消除之前看到原始信号。
using System;

namespace LanTalk
{
    /// <summary>单通道的全部状态。</summary>
    internal sealed class NsChannelState
    {
        public readonly float[] AnalyzeAnalysisMemory = new float[NsConfig.OverlapSize];
        public readonly float[] PrevAnalysisSignalSpectrum = new float[NsConfig.FftSizeBy2Plus1];
        public readonly float[] ProcessAnalysisMemory = new float[NsConfig.OverlapSize];
        public readonly float[] ProcessSynthesisMemory = new float[NsConfig.OverlapSize];
        public NsWienerFilter WienerFilter;
        public NsNoiseEstimator NoiseEstimator;
        public NsSpeechProbabilityEstimator SpeechProbabilityEstimator;

        public NsChannelState(NsSuppressionParams p)
        {
            WienerFilter = new NsWienerFilter(p);
            NoiseEstimator = new NsNoiseEstimator(p);
            SpeechProbabilityEstimator = new NsSpeechProbabilityEstimator();
        }

        public void Reset(NsSuppressionParams p)
        {
            Array.Clear(AnalyzeAnalysisMemory, 0, AnalyzeAnalysisMemory.Length);
            Array.Clear(PrevAnalysisSignalSpectrum, 0, PrevAnalysisSignalSpectrum.Length);
            Array.Clear(ProcessAnalysisMemory, 0, ProcessAnalysisMemory.Length);
            Array.Clear(ProcessSynthesisMemory, 0, ProcessSynthesisMemory.Length);
            WienerFilter.SetParams(p);
            WienerFilter.Reset();
            NoiseEstimator.SetParams(p);
            NoiseEstimator.Reset();
            SpeechProbabilityEstimator.Reset();
        }
    }

    internal sealed class NsNoiseSuppressor
    {
        private const int Bins = NsConfig.FftSizeBy2Plus1;

        private int _numAnalyzedFrames;
        private NsSuppressionParams _params;
        private readonly NsChannelState _channel;

        // 滤波器组的暂存区
        private readonly float[] _real = new float[NsConfig.FftSize];
        private readonly float[] _imag = new float[NsConfig.FftSize];
        private readonly float[] _extendedFrame = new float[NsConfig.FftSize];
        private readonly float[] _signalSpectrum = new float[Bins];

        private readonly float[] _postSnr = new float[Bins];
        private readonly float[] _priorSnr = new float[Bins];

        public NsNoiseSuppressor(NsSuppressionLevel level)
        {
            _params = NsSuppressionParams.ForLevel(level);
            _channel = new NsChannelState(_params);
            Reset();
        }

        public void SetLevel(NsSuppressionLevel level)
        {
            _params = NsSuppressionParams.ForLevel(level);
            Reset();
        }

        public void Reset()
        {
            _numAnalyzedFrames = -1;          // 上游初值就是 -1
            _channel.Reset(_params);
            Array.Clear(_real, 0, _real.Length);
            Array.Clear(_imag, 0, _imag.Length);
            Array.Clear(_extendedFrame, 0, _extendedFrame.Length);
            Array.Clear(_signalSpectrum, 0, Bins);
            Array.Clear(_priorSnr, 0, Bins);
            Array.Clear(_postSnr, 0, Bins);
        }

        /// <summary>分析一帧（160 样本）：估计噪声谱与逐频点语音概率。</summary>
        public void Analyze(float[] frame)
        {
            NsChannelState ch = _channel;
            ch.NoiseEstimator.PrepareAnalysis();

            // 全零帧直接跳过（注意不递增帧计数）
            float energy = 0f;
            for (int i = 0; i < ch.AnalyzeAnalysisMemory.Length; i++) energy += ch.AnalyzeAnalysisMemory[i] * ch.AnalyzeAnalysisMemory[i];
            for (int i = 0; i < NsConfig.NsFrameSize; i++) energy += frame[i] * frame[i];
            if (energy == 0f) return;

            _numAnalyzedFrames++;
            if (_numAnalyzedFrames < 0) _numAnalyzedFrames = 0;

            NsFft.FormExtendedFrame(frame, ch.AnalyzeAnalysisMemory, _extendedFrame);
            NsFft.ApplyFilterbankWindow(_extendedFrame);

            NsFft.Forward(_extendedFrame, _real, _imag);
            NsFft.ComputeMagnitudeSpectrum(_real, _imag, _signalSpectrum);

            float signalEnergy = 0f;
            for (int i = 0; i < Bins; i++)
                signalEnergy += _real[i] * _real[i] + _imag[i] * _imag[i];
            signalEnergy /= Bins;

            float signalSpectralSum = 0f;
            for (int i = 0; i < Bins; i++) signalSpectralSum += _signalSpectrum[i];

            ch.NoiseEstimator.PreUpdate(_numAnalyzedFrames, _signalSpectrum, signalSpectralSum);

            NsFft.ComputeSnr(ch.WienerFilter.Filter, ch.PrevAnalysisSignalSpectrum, _signalSpectrum,
                             ch.NoiseEstimator.PrevNoiseSpectrum, ch.NoiseEstimator.NoiseSpectrum,
                             _priorSnr, _postSnr);

            NsSignalAnalysis a = new NsSignalAnalysis();
            a.NumAnalyzedFrames = _numAnalyzedFrames;
            a.PriorSnr = _priorSnr;
            a.PostSnr = _postSnr;
            a.ConservativeNoiseSpectrum = ch.NoiseEstimator.ConservativeNoiseSpectrum;
            a.SignalSpectrum = _signalSpectrum;
            a.SignalSpectralSum = signalSpectralSum;
            a.SignalEnergy = signalEnergy;
            ch.SpeechProbabilityEstimator.Update(a);

            ch.NoiseEstimator.PostUpdate(ch.SpeechProbabilityEstimator.Probability, _signalSpectrum);

            Array.Copy(_signalSpectrum, ch.PrevAnalysisSignalSpectrum, Bins);
        }

        /// <summary>抑制一帧（160 样本，就地改写）。</summary>
        public void Process(float[] frame)
        {
            NsChannelState ch = _channel;

            NsFft.FormExtendedFrame(frame, ch.ProcessAnalysisMemory, _extendedFrame);
            NsFft.ApplyFilterbankWindow(_extendedFrame);

            float energyBefore = NsFft.ComputeEnergy(_extendedFrame);

            NsFft.Forward(_extendedFrame, _real, _imag);
            NsFft.ComputeMagnitudeSpectrum(_real, _imag, _signalSpectrum);

            ch.WienerFilter.Update(_numAnalyzedFrames, ch.NoiseEstimator.NoiseSpectrum,
                                   ch.NoiseEstimator.PrevNoiseSpectrum,
                                   ch.NoiseEstimator.ParametricNoiseSpectrum, _signalSpectrum);

            float[] filter = ch.WienerFilter.Filter;
            for (int i = 0; i < Bins; i++)
            {
                _real[i] *= filter[i];
                _imag[i] *= filter[i];
            }

            NsFft.Inverse(_real, _imag, _extendedFrame);
            float energyAfter = NsFft.ComputeEnergy(_extendedFrame);

            NsFft.ApplyFilterbankWindow(_extendedFrame);

            float gainAdjustment = ch.WienerFilter.ComputeOverallScalingFactor(
                _numAnalyzedFrames, ch.SpeechProbabilityEstimator.PriorProbability,
                energyBefore, energyAfter);

            for (int i = 0; i < NsConfig.FftSize; i++) _extendedFrame[i] *= gainAdjustment;

            NsFft.OverlapAndAdd(_extendedFrame, ch.ProcessSynthesisMemory, frame);
            NsFft.ClampFrame(frame);
        }
    }

    /// <summary>
    /// 对外接口：把 16kHz / 20ms（320 样本）的 PCM16 交给 WebRTC NS 处理。
    /// 内部按 10ms（160 样本）切分。算法延迟 96 样本 = 6ms。
    /// </summary>
    internal sealed class NsDenoiser
    {
        private const int SamplesPerFrame = NsConfig.NsFrameSize;   // 160

        private bool _enabled;
        private DenoiseLevel _level = DenoiseLevel.Mid;
        private NsSuppressionLevel _nsLevel = NsSuppressionLevel.K12dB;
        private NsNoiseSuppressor _ns;
        private readonly float[] _frame = new float[SamplesPerFrame];
        private readonly float[] _scratch = new float[SamplesPerFrame];
        private readonly short[] _samples = new short[NsConfig.NsFrameSize];

        public bool Enabled { get { return _enabled; } }
        public DenoiseLevel Level { get { return _level; } }

        /// <summary>算法延迟（样本）：96 样本 = 6ms。</summary>
        public static int Latency { get { return NsConfig.OverlapSize; } }

        public NsDenoiser()
        {
            // 与第一版 Denoiser 保持一致：未显式调用 SetLevel 之前不处理任何数据。
            // （曾经这里直接启用了中档，结果没设档位的自检项被悄悄降噪、端到端校验全错。）
            _level = DenoiseLevel.Mid;
            _nsLevel = NsSuppressionLevel.K12dB;
            _ns = new NsNoiseSuppressor(_nsLevel);
            _enabled = false;
        }

        /// <summary>
        /// 界面档位 → WebRTC NS 的抑制目标。
        /// 实测中档用 K12dB 的信噪比改善最好：K18dB 压得更狠但语音失真更大，指标反而掉。
        /// 所以按 NS 自己的最优点映射，而不是硬凑成和轻量版相同的谱底。
        /// </summary>
        private static NsSuppressionLevel MapLevel(DenoiseLevel l)
        {
            switch (l)
            {
                case DenoiseLevel.Low: return NsSuppressionLevel.K6dB;
                case DenoiseLevel.High: return NsSuppressionLevel.K21dB;
                default: return NsSuppressionLevel.K12dB;
            }
        }

        public void SetLevel(DenoiseLevel l)
        {
            _level = l;
            _enabled = l != DenoiseLevel.Off;
            NsSuppressionLevel target = MapLevel(l);
            if (_ns == null)
            {
                _nsLevel = target;
                _ns = new NsNoiseSuppressor(target);
            }
            else if (target != _nsLevel)
            {
                _nsLevel = target;
                _ns.SetLevel(target);
            }
        }

        public void Reset()
        {
            if (_ns != null) _ns.Reset();
        }

        /// <summary>就地处理一段 16 位小端单声道 PCM（长度须为 160 样本的整数倍）。</summary>
        public void ProcessPcm(byte[] pcm, int len)
        {
            if (!_enabled) return;      // 关闭时逐字节直通

            int total = len / 2;
            int offset = 0;
            while (offset + SamplesPerFrame <= total)
            {
                for (int i = 0; i < SamplesPerFrame; i++)
                    _samples[i] = (short)(pcm[(offset + i) * 2] | (pcm[(offset + i) * 2 + 1] << 8));
                for (int i = 0; i < SamplesPerFrame; i++) _frame[i] = _samples[i];

                _ns.Analyze(_frame);
                Array.Copy(_frame, _scratch, SamplesPerFrame);
                _ns.Process(_scratch);

                for (int i = 0; i < SamplesPerFrame; i++)
                {
                    int v = (int)(_scratch[i] + (_scratch[i] >= 0f ? 0.5f : -0.5f));
                    if (v > 32767) v = 32767;
                    else if (v < -32768) v = -32768;
                    int idx = (offset + i) * 2;
                    pcm[idx] = (byte)(v & 0xFF);
                    pcm[idx + 1] = (byte)((v >> 8) & 0xFF);
                }
                offset += SamplesPerFrame;
            }
        }
    }
}
