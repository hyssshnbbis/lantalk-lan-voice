// DenoiseCompare.cs —— 降噪对比：一次录音，两个引擎各出一份，附客观指标
//
// 关键设计：**真实录音没有"干净参考"，所以不能用信噪比改善那套指标**。
// 这里改用三个不需要参考信号的量：
//
//   噪声底   = 最安静的那 10% 帧的平均功率（dBFS）
//   语音电平 = 最响的那 20% 帧的平均功率（dBFS）
//   估计信噪比 = 语音电平 − 噪声底
//
// 三个量都是**按帧能量**算的，与算法的固定延迟无关——省掉了对齐这件麻烦事，
// 也就不会出现"因为没对齐所以指标全错"的假象。
//
// 改善 = 处理后的估计信噪比 − 原始估计信噪比（同时给出噪声降了多少、语音掉了多少，
// 两者之差就是改善，用户一眼能看出"是压了噪声还是连人声一起压了"）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanTalk
{
    internal sealed class AbMetrics
    {
        public int Frames;
        public double NoiseFloorDb;      // dBFS，越负越好
        public double SpeechLevelDb;     // dBFS
        public double EstimatedSnrDb;    // SpeechLevel - NoiseFloor
        /// <summary>语音与静音的对比不足，指标仅供参考（录音里可能没说话或没停顿）。</summary>
        public bool TooLittleContrast;

        public string Format(string name)
        {
            return Pad(name, 20) + Pad(NoiseFloorDb.ToString("F1"), 13) + Pad(SpeechLevelDb.ToString("F1"), 13)
                 + EstimatedSnrDb.ToString("F1");
        }

        private static string Pad(string s, int width)
        {
            if (s.Length >= width) return s + " ";
            return s + new string(' ', width - s.Length);
        }
    }

    /// <summary>一次对比的完整结果。</summary>
    internal sealed class AbResult
    {
        public AbMetrics Raw;
        public AbMetrics Ns;
        public AbMetrics Classic;
        public string Report = "";
        public string OutDir = "";
        public string RawWav = "", NsWav = "", ClassicWav = "";

        public double NsImprove { get { return Ns.EstimatedSnrDb - Raw.EstimatedSnrDb; } }
        public double ClassicImprove { get { return Classic.EstimatedSnrDb - Raw.EstimatedSnrDb; } }
        public bool TooLittleContrast { get { return Raw == null || Raw.TooLittleContrast; } }
    }

    internal static class DenoiseCompare
    {
        public const int FrameMs = 20;
        private const double Silence = -120.0;

        /// <summary>算三个不需要参考信号的指标。</summary>
        public static AbMetrics Analyze(short[] pcm, int rate)
        {
            AbMetrics m = new AbMetrics();
            int frame = rate / (1000 / FrameMs);          // 20ms = 320 样本
            if (frame < 1) frame = 1;

            int n = pcm.Length / frame;
            m.Frames = n;
            if (n == 0)
            {
                m.NoiseFloorDb = Silence;
                m.SpeechLevelDb = Silence;
                m.EstimatedSnrDb = 0;
                m.TooLittleContrast = true;
                return m;
            }

            double[] power = new double[n];
            for (int f = 0; f < n; f++)
            {
                double sum = 0;
                int off = f * frame;
                for (int i = 0; i < frame; i++)
                {
                    double v = pcm[off + i] / 32768.0;
                    sum += v * v;
                }
                power[f] = sum / frame;
            }

            double[] sorted = (double[])power.Clone();
            Array.Sort(sorted);

            int kNoise = n / 10; if (kNoise < 1) kNoise = 1;
            int kSpeech = n / 5; if (kSpeech < 1) kSpeech = 1;

            double noise = 0;
            for (int i = 0; i < kNoise; i++) noise += sorted[i];
            noise /= kNoise;

            double speech = 0;
            for (int i = n - kSpeech; i < n; i++) speech += sorted[i];
            speech /= kSpeech;

            m.NoiseFloorDb = ToDb(noise);
            m.SpeechLevelDb = ToDb(speech);
            m.EstimatedSnrDb = m.SpeechLevelDb - m.NoiseFloorDb;
            // 对比不足 6dB 说明录进来的东西基本是"一种声音"（要么一直没人说话，要么没停顿）
            m.TooLittleContrast = m.EstimatedSnrDb < 6.0;
            return m;
        }

        private static double ToDb(double power)
        {
            if (power <= 1e-12) return Silence;
            return 10.0 * Math.Log10(power);
        }

        /// <summary>把原始录音过一遍指定的降噪引擎。</summary>
        public static short[] Process(short[] raw, DenoiseLevel level, bool useNs)
        {
            byte[] buf = new byte[raw.Length * 2];
            for (int i = 0; i < raw.Length; i++)
            {
                buf[i * 2] = (byte)(raw[i] & 0xFF);
                buf[i * 2 + 1] = (byte)((raw[i] >> 8) & 0xFF);
            }

            if (useNs)
            {
                NsDenoiser ns = new NsDenoiser();
                ns.SetLevel(level);
                // 按 320 样本（20ms）分块，和真实采集链路一致
                for (int off = 0; off + 640 <= buf.Length; off += 640)
                {
                    byte[] seg = new byte[640];
                    Array.Copy(buf, off, seg, 0, 640);
                    ns.ProcessPcm(seg, 640);
                    Array.Copy(seg, 0, buf, off, 640);
                }
                // 不足一整块的尾巴原样保留（避免把尾巴丢了）
            }
            else
            {
                Denoiser cl = new Denoiser();
                cl.SetLevel(level);
                cl.ProcessPcm(buf, buf.Length);
            }

            short[] outp = new short[raw.Length];
            for (int i = 0; i < outp.Length; i++)
                outp[i] = (short)(buf[i * 2] | (buf[i * 2 + 1] << 8));
            return outp;
        }

        /// <summary>生成一段合成语音+噪声，用于没有麦克风时（或自检）验证整条对比链路。</summary>
        public static short[] MakeSyntheticSpeech(int n, int rate, double snrDb, uint seed)
        {
            float[] clean = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;
                double wp = (t / 1.3) % 1.0;
                double env = 0.0;
                double f1 = 700.0, f2 = 1800.0;
                if (wp < 0.72)
                {
                    double sp = wp / 0.72;
                    double syl = (sp * 2.5) % 1.0;
                    env = syl < 0.78 ? Math.Sin(Math.PI * syl / 0.78) : 0.0;
                    f1 = 500.0 + 520.0 * sp;
                    f2 = 2400.0 - 1200.0 * sp;
                }
                if (env <= 0.0) { clean[i] = 0f; continue; }

                double f0 = 205.0 + 22.0 * Math.Sin(2.0 * Math.PI * 0.6 * t);
                double v = 0.0;
                for (int h = 1; h <= 40; h++)
                {
                    double f = f0 * h;
                    if (f > 3400.0) break;
                    double a = 1.0 / h;
                    a *= 1.0 + 4.0 / (1.0 + Math.Pow((f - f1) / 130.0, 2));
                    a *= 1.0 + 2.5 / (1.0 + Math.Pow((f - f2) / 200.0, 2));
                    v += a * Math.Sin(2.0 * Math.PI * f * t);
                }
                clean[i] = (float)(v * env * 0.09);
            }

            uint s = seed * 2654435761u + 1u;
            double sp2 = 0;
            for (int i = 0; i < n; i++) sp2 += clean[i] * (double)clean[i];
            double np = 0;
            float[] noise = new float[n];
            for (int i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u;
                noise[i] = ((s >> 9) & 0x7FFFFF) / 4194304f - 1f;
                np += noise[i] * (double)noise[i];
            }
            double scale = np > 1e-12 ? Math.Sqrt((sp2 / Math.Pow(10.0, snrDb / 10.0)) / np) : 0;

            short[] outp = new short[n];
            for (int i = 0; i < n; i++)
            {
                double v = (clean[i] + noise[i] * scale) * 32768.0;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                outp[i] = (short)v;
            }
            return outp;
        }

        /// <summary>
        /// 跑完整流程：分析原始录音、分别过两个引擎、写 3 个 wav + 1 份报告。
        /// </summary>
        public static AbResult RunEx(short[] raw, int rate, DenoiseLevel level, string outDir, Action<string> log)
        {
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            string fRaw = Path.Combine(outDir, "1-原始录音.wav");
            string fNs = Path.Combine(outDir, "2-降噪-WebRTC-NS.wav");
            string fCl = Path.Combine(outDir, "3-降噪-轻量版.wav");

            if (log != null) log("正在分析原始录音…");
            WavFile.Write16(fRaw, raw, rate, 1);
            AbMetrics mRaw = Analyze(raw, rate);

            if (log != null) log("正在处理：WebRTC NS 引擎…");
            short[] ns = Process(raw, level, true);
            WavFile.Write16(fNs, ns, rate, 1);
            AbMetrics mNs = Analyze(ns, rate);

            if (log != null) log("正在处理：轻量版引擎…");
            short[] cl = Process(raw, level, false);
            WavFile.Write16(fCl, cl, rate, 1);
            AbMetrics mCl = Analyze(cl, rate);

            double nsImprove = mNs.EstimatedSnrDb - mRaw.EstimatedSnrDb;
            double clImprove = mCl.EstimatedSnrDb - mRaw.EstimatedSnrDb;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LanTalk 降噪对比报告");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("录音长度：" + (raw.Length / (double)rate).ToString("F1") + " 秒，"
                          + rate + " Hz，单声道 16bit，共 " + mRaw.Frames + " 帧（每帧 " + FrameMs + "ms）");
            sb.AppendLine("对比档位：" + DenoiseLevels.Name(level));
            sb.AppendLine();
            sb.AppendLine("指标说明（真实录音没有干净参考，所以不用信噪比那套）：");
            sb.AppendLine("  噪声底   = 最安静的 10% 帧的平均功率，越负说明底噪越干净");
            sb.AppendLine("  语音电平 = 最响的 20% 帧的平均功率，掉得越少说明人声保留得越好");
            sb.AppendLine("  估计信噪比 = 语音电平 − 噪声底，这个差越大越好");
            sb.AppendLine();
            sb.AppendLine(PadRight("", 20) + PadRight("噪声底dBFS", 13) + PadRight("语音电平dBFS", 13) + "估计信噪比dB");
            sb.AppendLine(PadRight("原始录音", 20) + PadRight(mRaw.NoiseFloorDb.ToString("F1"), 13)
                          + PadRight(mRaw.SpeechLevelDb.ToString("F1"), 13) + mRaw.EstimatedSnrDb.ToString("F1"));
            sb.AppendLine(PadRight("降噪 · NS", 20) + PadRight(mNs.NoiseFloorDb.ToString("F1"), 13)
                          + PadRight(mNs.SpeechLevelDb.ToString("F1"), 13) + mNs.EstimatedSnrDb.ToString("F1"));
            sb.AppendLine(PadRight("降噪 · 轻量版", 20) + PadRight(mCl.NoiseFloorDb.ToString("F1"), 13)
                          + PadRight(mCl.SpeechLevelDb.ToString("F1"), 13) + mCl.EstimatedSnrDb.ToString("F1"));
            sb.AppendLine();
            sb.AppendLine("改善（估计信噪比提升）：");
            sb.AppendLine("  NS      ：" + Sign(nsImprove) + " dB"
                          + "   （噪声 " + Sign(mNs.NoiseFloorDb - mRaw.NoiseFloorDb) + " dB，"
                          + "语音 " + Sign(mNs.SpeechLevelDb - mRaw.SpeechLevelDb) + " dB）");
            sb.AppendLine("  轻量版  ：" + Sign(clImprove) + " dB"
                          + "   （噪声 " + Sign(mCl.NoiseFloorDb - mRaw.NoiseFloorDb) + " dB，"
                          + "语音 " + Sign(mCl.SpeechLevelDb - mRaw.SpeechLevelDb) + " dB）");
            sb.AppendLine();
            sb.AppendLine("文件：");
            sb.AppendLine("  " + fRaw);
            sb.AppendLine("  " + fNs);
            sb.AppendLine("  " + fCl);

            if (mRaw.TooLittleContrast)
            {
                sb.AppendLine();
                sb.AppendLine("【提醒】原始录音的语音与静音对比不足 6 dB，指标不可信。");
                sb.AppendLine("       录音时请对着麦克风正常说话，中间自然停顿几秒（好让程序测到底噪）。");
            }

            string report = sb.ToString();
            File.WriteAllText(Path.Combine(outDir, "对比报告.txt"), report, new UTF8Encoding(false));
            if (log != null) log("对比完成，文件已写入：" + outDir);

            AbResult res = new AbResult();
            res.Raw = mRaw;
            res.Ns = mNs;
            res.Classic = mCl;
            res.Report = report;
            res.OutDir = outDir;
            res.RawWav = fRaw;
            res.NsWav = fNs;
            res.ClassicWav = fCl;
            return res;
        }

        /// <summary>只要报告文本的便捷入口。</summary>
        public static string Run(short[] raw, int rate, DenoiseLevel level, string outDir, Action<string> log)
        {
            return RunEx(raw, rate, level, outDir, log).Report;
        }

        private static string Sign(double v)
        {
            return (v >= 0 ? "+" : "") + v.ToString("F1");
        }

        private static string PadRight(string s, int width)
        {
            if (s.Length >= width) return s + " ";
            return s + new string(' ', width - s.Length);
        }
    }
}
