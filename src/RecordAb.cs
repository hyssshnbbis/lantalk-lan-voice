// RecordAb.cs —— 降噪对比的录制与命令行入口
//
// 界面上的「录音对比」按钮和命令行的 --record-ab 都走这里，
// 保证两条路径行为完全一致（同一份录制逻辑、同一份分析、同一份输出）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace LanTalk
{
    internal static class RecordAb
    {
        public const int Rate = 16000;
        public const int FrameMs = 20;

        /// <summary>
        /// 默认输出目录：我的文档\LanTalk降噪对比\时间戳\
        /// 用时间戳分开，多录几次不会互相覆盖，也方便直接对比。
        /// </summary>
        public static string DefaultOutDir()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Path.GetTempPath();
            return Path.Combine(Path.Combine(docs, "LanTalk降噪对比"),
                                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        /// <summary>
        /// 录 N 秒麦克风。progress 每秒回调一次（用于界面显示倒计时）。
        /// 麦克风打不开时抛异常，由调用方决定怎么提示。
        /// </summary>
        public static short[] Record(int seconds, Action<int> progress)
        {
            if (seconds < 1) seconds = 1;

            WaveInSource src = new WaveInSource(Mm.WAVE_MAPPER, Rate, FrameMs, 8);
            List<byte[]> chunks = new List<byte[]>();
            object gate = new object();

            src.DataAvailable += delegate(byte[] b, int n)
            {
                byte[] copy = new byte[n];
                Array.Copy(b, 0, copy, 0, n);
                lock (gate) chunks.Add(copy);
            };

            try
            {
                src.Start();
                for (int s = 0; s < seconds; s++)
                {
                    Thread.Sleep(1000);
                    if (progress != null) progress(s + 1);
                }
            }
            finally
            {
                try { src.Stop(); } catch { }
            }

            int total = 0;
            lock (gate) for (int i = 0; i < chunks.Count; i++) total += chunks[i].Length;

            short[] outp = new short[total / 2];
            int pos = 0;
            lock (gate)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    byte[] c = chunks[i];
                    for (int j = 0; j + 1 < c.Length; j += 2)
                    {
                        if (pos >= outp.Length) break;
                        outp[pos++] = (short)(c[j] | (c[j + 1] << 8));
                    }
                }
            }
            if (pos < outp.Length) Array.Resize(ref outp, pos);
            return outp;
        }

        /// <summary>命令行入口：--record-ab（麦克风）/ --record-ab-sim（合成信号）。</summary>
        public static int Run(Args a)
        {
            string outDir = string.IsNullOrEmpty(a.RecordAbOut) ? DefaultOutDir() : a.RecordAbOut;
            int seconds = a.RecordAb > 0 ? a.RecordAb : 10;
            DenoiseLevel level = a.EffectiveDenoise();
            if (level == DenoiseLevel.Off) level = DenoiseLevel.Mid;   // 对比模式不比较"关闭"

            short[] raw;
            if (a.RecordAbSim)
            {
                Log.Write("[AB] 使用合成语音 + 白噪声（0dB）作为输入，不占用麦克风");
                raw = DenoiseCompare.MakeSyntheticSpeech(Rate * seconds, Rate, 0.0, 12345);
            }
            else
            {
                Log.Write("[AB] 开始录音 " + seconds + " 秒，请对着麦克风正常说话，中间自然停顿几秒");
                try
                {
                    raw = Record(seconds, delegate(int done) { Log.Write("[AB] 已录 " + done + "/" + seconds + " 秒"); });
                }
                catch (Exception ex)
                {
                    Log.Write("[AB] 录音失败：" + ex.Message);
                    return 2;
                }
            }

            if (raw.Length == 0)
            {
                Log.Write("[AB] 没有录到任何数据，放弃。");
                return 2;
            }

            string report;
            try
            {
                report = DenoiseCompare.Run(raw, Rate, level, outDir, delegate(string s) { Log.Write("[AB] " + s); });
            }
            catch (Exception ex)
            {
                Log.Write("[AB] 处理失败：" + ex.Message);
                return 3;
            }

            // 报告同时打到日志，方便命令行直接看
            string[] lines = report.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) Log.Write("[AB] " + lines[i]);

            Log.Write("[AB] 完成。输出目录：" + outDir);
            return 0;
        }
    }
}
