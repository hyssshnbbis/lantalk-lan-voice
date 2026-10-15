// NsSpeechProbability.cs —— WebRTC 降噪移植（第 6 层：逐频点语音概率）
// 移植自 sonora-ns / WebRTC `ns/speech_probability_estimator.cc`。BSD-3-Clause。
//
// 三个特征各过一个 tanh 型"S"曲线映射成 0~1 的指示量，按先验模型给的权重加权，
// 得到这一帧的"先验语音概率"；再逐频点用对数似然比细化。
// 这个逐频点概率是 WebRTC NS 的关键——噪声估计和增益都靠它区分语音/噪声。
using System;

namespace LanTalk
{
    /// <summary>一帧的分析数据，喂给语音概率估计器。</summary>
    internal sealed class NsSignalAnalysis
    {
        public int NumAnalyzedFrames;
        public float[] PriorSnr;
        public float[] PostSnr;
        public float[] ConservativeNoiseSpectrum;
        public float[] SignalSpectrum;
        public float SignalSpectralSum;
        public float SignalEnergy;
    }

    internal sealed class NsSpeechProbabilityEstimator
    {
        private const int Bins = NsConfig.FftSizeBy2Plus1;
        private const int LongStartup = NsConfig.LongStartupPhaseBlocks;

        private readonly NsSignalModelEstimator _signalModelEstimator = new NsSignalModelEstimator();
        private float _priorSpeechProb;
        private readonly float[] _speechProbability = new float[Bins];

        public NsSpeechProbabilityEstimator()
        {
            Reset();
        }

        public void Reset()
        {
            _signalModelEstimator.Reset();
            _priorSpeechProb = 0.5f;
            Array.Clear(_speechProbability, 0, Bins);
        }

        public float PriorProbability { get { return _priorSpeechProb; } }
        public float[] Probability { get { return _speechProbability; } }

        public void Update(NsSignalAnalysis a)
        {
            if (a.NumAnalyzedFrames < LongStartup)
                _signalModelEstimator.AdjustNormalization(a.NumAnalyzedFrames, a.SignalEnergy);

            _signalModelEstimator.Update(a.PriorSnr, a.PostSnr, a.ConservativeNoiseSpectrum,
                                         a.SignalSpectrum, a.SignalSpectralSum, a.SignalEnergy);

            NsSignalModel model = _signalModelEstimator.Features;
            NsPriorSignalModel priorModel = _signalModelEstimator.PriorModel;

            const float widthPrior0 = 4.0f;
            const float widthPrior1 = 2.0f * widthPrior0;   // 停顿区用更宽的过渡带

            float widthPrior = model.Lrt < priorModel.Lrt ? widthPrior1 : widthPrior0;
            float indicator0 = 0.5f * ((float)Math.Tanh(widthPrior * (model.Lrt - priorModel.Lrt)) + 1.0f);

            widthPrior = model.SpectralFlatness > priorModel.FlatnessThreshold ? widthPrior1 : widthPrior0;
            float indicator1 = 0.5f * ((float)Math.Tanh(
                widthPrior * (priorModel.FlatnessThreshold - model.SpectralFlatness)) + 1.0f);

            widthPrior = model.SpectralDiff < priorModel.TemplateDiffThreshold ? widthPrior1 : widthPrior0;
            float indicator2 = 0.5f * ((float)Math.Tanh(
                widthPrior * (model.SpectralDiff - priorModel.TemplateDiffThreshold)) + 1.0f);

            float indPrior = priorModel.LrtWeighting * indicator0
                           + priorModel.FlatnessWeighting * indicator1
                           + priorModel.DifferenceWeighting * indicator2;

            _priorSpeechProb += 0.1f * (indPrior - _priorSpeechProb);
            if (_priorSpeechProb < 0.01f) _priorSpeechProb = 0.01f;
            else if (_priorSpeechProb > 1.0f) _priorSpeechProb = 1.0f;

            float gainPrior = (1.0f - _priorSpeechProb) / (_priorSpeechProb + 0.0001f);

            float[] invLrt = new float[Bins];
            NsFastMath.ExpBatchSignFlip(model.AvgLogLrt, invLrt);
            for (int i = 0; i < Bins; i++)
                _speechProbability[i] = 1.0f / (1.0f + gainPrior * invLrt[i]);
        }
    }

    internal static class NsSpeechProbabilityTests
    {
        public static string Run()
        {
            int pass = 0, fail = 0;
            string firstFail = "";
            int bins = NsConfig.FftSizeBy2Plus1;

            // --- 初始状态 ---
            {
                NsSpeechProbabilityEstimator e = new NsSpeechProbabilityEstimator();
                bool ok = e.PriorProbability == 0.5f;
                for (int i = 0; i < bins && ok; i++) if (e.Probability[i] != 0f) ok = false;
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "语音概率估计器初值不符"; }
            }

            // --- 概率值必须落在 [0,1] ---
            {
                NsSpeechProbabilityEstimator e = new NsSpeechProbabilityEstimator();
                float[] prior = new float[bins], post = new float[bins],
                        cons = new float[bins], sig = new float[bins];
                for (int i = 0; i < bins; i++) { prior[i] = 1f; post[i] = 1f; cons[i] = 1f; sig[i] = 10f; }
                float sum = 10f * bins;
                NsSignalAnalysis a = new NsSignalAnalysis();
                a.NumAnalyzedFrames = 0; a.PriorSnr = prior; a.PostSnr = post;
                a.ConservativeNoiseSpectrum = cons; a.SignalSpectrum = sig;
                a.SignalSpectralSum = sum; a.SignalEnergy = sum;
                e.Update(a);
                bool ok = true;
                for (int i = 0; i < bins; i++)
                    if (e.Probability[i] < 0f || e.Probability[i] > 1f) { ok = false; break; }
                if (ok && e.PriorProbability >= 0.01f && e.PriorProbability <= 1f) pass++;
                else { fail++; if (firstFail == "") firstFail = "语音概率越界"; }
            }

            // --- 长时间高信噪比：语音概率应该高 ---
            {
                NsSpeechProbabilityEstimator e = new NsSpeechProbabilityEstimator();
                float[] prior = new float[bins], post = new float[bins],
                        noise = new float[bins], sig = new float[bins];
                for (int i = 0; i < bins; i++) { prior[i] = 10f; post[i] = 10f; noise[i] = 1f; sig[i] = 100f; }
                float sum = 100f * bins;
                for (int f = 0; f < 300; f++)
                {
                    NsSignalAnalysis a = new NsSignalAnalysis();
                    a.NumAnalyzedFrames = f; a.PriorSnr = prior; a.PostSnr = post;
                    a.ConservativeNoiseSpectrum = noise; a.SignalSpectrum = sig;
                    a.SignalSpectralSum = sum; a.SignalEnergy = sum;
                    e.Update(a);
                }
                float avg = 0;
                for (int i = 0; i < bins; i++) avg += e.Probability[i];
                avg /= bins;
                if (avg > 0.5f) pass++;
                else { fail++; if (firstFail == "") firstFail = "高信噪比下语音概率偏低：" + avg.ToString("F3"); }
            }

            // --- 长时间低信噪比：语音概率应该低 ---
            {
                NsSpeechProbabilityEstimator e = new NsSpeechProbabilityEstimator();
                float[] prior = new float[bins], post = new float[bins],
                        noise = new float[bins], sig = new float[bins];
                for (int i = 0; i < bins; i++) { prior[i] = 0f; post[i] = 0f; noise[i] = 100f; sig[i] = 100f; }
                float sum = 100f * bins;
                for (int f = 0; f < 300; f++)
                {
                    NsSignalAnalysis a = new NsSignalAnalysis();
                    a.NumAnalyzedFrames = f; a.PriorSnr = prior; a.PostSnr = post;
                    a.ConservativeNoiseSpectrum = noise; a.SignalSpectrum = sig;
                    a.SignalSpectralSum = sum; a.SignalEnergy = sum;
                    e.Update(a);
                }
                float avg = 0;
                for (int i = 0; i < bins; i++) avg += e.Probability[i];
                avg /= bins;
                if (avg < 0.5f) pass++;
                else { fail++; if (firstFail == "") firstFail = "低信噪比下语音概率偏高：" + avg.ToString("F3"); }
            }

            // --- 先验概率始终被夹在 [0.01, 1] ---
            {
                NsSpeechProbabilityEstimator e = new NsSpeechProbabilityEstimator();
                float[] z = new float[bins];
                bool ok = true;
                for (int f = 0; f < 50 && ok; f++)
                {
                    NsSignalAnalysis a = new NsSignalAnalysis();
                    a.NumAnalyzedFrames = f; a.PriorSnr = z; a.PostSnr = z;
                    a.ConservativeNoiseSpectrum = z; a.SignalSpectrum = z;
                    a.SignalSpectralSum = 0f; a.SignalEnergy = 0f;
                    e.Update(a);
                    if (e.PriorProbability < 0.01f || e.PriorProbability > 1f) ok = false;
                }
                if (ok) pass++; else { fail++; if (firstFail == "") firstFail = "先验概率越界"; }
            }

            return "NS语音概率 通过 " + pass + " 失败 " + fail + (fail > 0 ? "（首个失败：" + firstFail + "）" : "");
        }
    }
}
