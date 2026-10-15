// WavFile.cs —— 极简 WAV 读写（16kHz 单声道 16bit，正好是 LanTalk 的音频格式）
// 只为了"降噪对比"能产出能用播放器直接听的 wav 文件，所以刻意做得小而明确：
// 不支持的格式直接报错，而不是猜。
using System;
using System.IO;

namespace LanTalk
{
    internal static class WavFile
    {
        /// <summary>写 16bit PCM 的 wav（RIFF 小端）。</summary>
        public static void Write16(string path, short[] samples, int sampleRate, int channels)
        {
            if (samples == null) throw new ArgumentNullException("samples");
            if (channels < 1) channels = 1;

            int dataBytes = samples.Length * 2;
            int byteRate = sampleRate * channels * 2;
            short blockAlign = (short)(channels * 2);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataBytes);
                w.Write(new char[] { 'W', 'A', 'V', 'E' });

                w.Write(new char[] { 'f', 'm', 't', ' ' });
                w.Write(16);                       // fmt 块长度
                w.Write((short)1);                 // PCM
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(byteRate);
                w.Write(blockAlign);
                w.Write((short)16);                // 位深

                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(dataBytes);

                byte[] buf = new byte[dataBytes];
                for (int i = 0; i < samples.Length; i++)
                {
                    buf[i * 2] = (byte)(samples[i] & 0xFF);
                    buf[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xFF);
                }
                w.Write(buf);
            }
        }

        /// <summary>
        /// 读 16bit PCM 的 wav。只接受单声道/立体声、16 位；其它格式抛异常。
        /// 立体声会混成单声道。
        /// </summary>
        public static short[] Read16(string path, out int sampleRate, out int channels)
        {
            sampleRate = 0;
            channels = 0;

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader r = new BinaryReader(fs))
            {
                if (new string(r.ReadChars(4)) != "RIFF") throw new Exception("不是 RIFF 文件");
                r.ReadInt32();
                if (new string(r.ReadChars(4)) != "WAVE") throw new Exception("不是 WAVE 文件");

                int bits = 0;
                short fmtChannels = 0;
                int rate = 0;
                byte[] data = null;

                while (fs.Position + 8 <= fs.Length)
                {
                    string id = new string(r.ReadChars(4));
                    int size = r.ReadInt32();
                    long next = fs.Position + size + (size & 1);

                    if (id == "fmt ")
                    {
                        r.ReadInt16();                 // 格式标签
                        fmtChannels = r.ReadInt16();
                        rate = r.ReadInt32();
                        r.ReadInt32();                 // 字节率
                        r.ReadInt16();                 // 块对齐
                        bits = r.ReadInt16();
                    }
                    else if (id == "data")
                    {
                        data = r.ReadBytes(size);
                    }

                    if (next > fs.Length) break;
                    fs.Position = next;
                }

                if (data == null) throw new Exception("wav 里没有 data 块");
                if (bits != 16) throw new Exception("只支持 16 位 wav，实际 " + bits + " 位");
                if (fmtChannels < 1) throw new Exception("wav 声道数无效");

                sampleRate = rate;
                channels = fmtChannels;

                int frames = data.Length / 2 / fmtChannels;
                short[] outp = new short[frames];
                for (int i = 0; i < frames; i++)
                {
                    int acc = 0;
                    for (int c = 0; c < fmtChannels; c++)
                    {
                        int idx = (i * fmtChannels + c) * 2;
                        acc += (short)(data[idx] | (data[idx + 1] << 8));
                    }
                    outp[i] = (short)(acc / fmtChannels);
                }
                return outp;
            }
        }
    }
}
