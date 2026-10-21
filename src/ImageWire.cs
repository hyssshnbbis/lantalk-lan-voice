// ImageWire.cs —— 图片分片传输
//
// UDP 单包上限约 64KB 且不保证送达，一张图必须切块发、按消息号重组。
//
// 分片头（9 字节）：
//   [msgId 4][分片序号 2][分片总数 2][flags 1]
// 序号从 0 开始；flags 位 0 表示这是最后一片（冗余信息，便于校验总数）。
//
// 重组器按 msgId 聚合，容忍乱序与重复，超时（默认 8 秒）整块丢弃，
// 并对单张图总量设上限，避免恶意/异常发包把内存吃满。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace LanTalk
{
    internal static class ImageWire
    {
        public const int HeaderLen = 9;
        /// <summary>每片承载的数据字节数。加上 UDP/IP 头后仍远小于 MTU，不会分片。</summary>
        public const int ChunkPayload = 900;
        /// <summary>单张图允许的最大字节数。</summary>
        public const int MaxImageBytes = 3 * 1024 * 1024;
        /// <summary>重组超时：超过这个时间还没凑齐就整块丢弃。</summary>
        public const int ReasmTimeoutMs = 8000;

        public static int ChunkCount(int totalBytes)
        {
            if (totalBytes <= 0) return 0;
            return (totalBytes + ChunkPayload - 1) / ChunkPayload;
        }

        public static byte[] BuildChunk(uint msgId, int index, int total, byte[] data, int off, int len)
        {
            byte[] p = new byte[HeaderLen + len];
            p[0] = (byte)(msgId & 0xFF);
            p[1] = (byte)((msgId >> 8) & 0xFF);
            p[2] = (byte)((msgId >> 16) & 0xFF);
            p[3] = (byte)((msgId >> 24) & 0xFF);
            p[4] = (byte)(index & 0xFF);
            p[5] = (byte)((index >> 8) & 0xFF);
            p[6] = (byte)(total & 0xFF);
            p[7] = (byte)((total >> 8) & 0xFF);
            p[8] = (byte)(index == total - 1 ? 1 : 0);
            if (len > 0) Buffer.BlockCopy(data, off, p, HeaderLen, len);
            return p;
        }

        public static bool ParseChunk(byte[] p, int len, out uint msgId, out int index, out int total, out int dataOff, out int dataLen)
        {
            msgId = 0; index = 0; total = 0; dataOff = HeaderLen; dataLen = 0;
            if (p == null || len < HeaderLen) return false;

            msgId = (uint)(p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24));
            index = p[4] | (p[5] << 8);
            total = p[6] | (p[7] << 8);
            if (total <= 0 || total > MaxImageBytes / ChunkPayload + 2) return false;
            if (index < 0 || index >= total) return false;

            dataOff = HeaderLen;
            dataLen = len - HeaderLen;
            if (dataLen <= 0) return false;
            return true;
        }
    }

    /// <summary>按消息号把分片拼回完整数据。乱序、重复都容忍；超时或超量整块丢弃。</summary>
    internal sealed class ImageAssembler
    {
        private sealed class Pending
        {
            public byte[][] Parts;
            public bool[] Got;
            public int Total;
            public int Received;
            public long Bytes;
            public DateTime StartedUtc;
        }

        private readonly Dictionary<uint, Pending> _map = new Dictionary<uint, Pending>();
        private readonly object _lock = new object();

        public long DroppedTimeouts;
        public long DroppedOversize;
        public long Completed;

        /// <summary>收到一片；拼齐时通过回调给出完整数据。</summary>
        public void Add(byte[] payload, int len, Action<uint, byte[]> onComplete)
        {
            uint msgId;
            int index, total, off, dataLen;
            if (!ImageWire.ParseChunk(payload, len, out msgId, out index, out total, out off, out dataLen))
                return;

            byte[] done = null;
            lock (_lock)
            {
                PurgeLocked(DateTime.UtcNow);

                Pending pd;
                if (!_map.TryGetValue(msgId, out pd))
                {
                    // 总量超限直接拒，不占内存
                    if ((long)total * ImageWire.ChunkPayload > ImageWire.MaxImageBytes + ImageWire.ChunkPayload)
                    {
                        DroppedOversize++;
                        return;
                    }
                    pd = new Pending();
                    pd.Total = total;
                    pd.Parts = new byte[total][];
                    pd.Got = new bool[total];
                    pd.StartedUtc = DateTime.UtcNow;
                    _map[msgId] = pd;
                }
                if (pd.Total != total) return;      // 同一 msgId 总数不一致，丢弃

                if (!pd.Got[index])
                {
                    pd.Got[index] = true;
                    pd.Parts[index] = new byte[dataLen];
                    Buffer.BlockCopy(payload, off, pd.Parts[index], 0, dataLen);
                    pd.Received++;
                    pd.Bytes += dataLen;
                    if (pd.Bytes > ImageWire.MaxImageBytes)
                    {
                        _map.Remove(msgId);
                        DroppedOversize++;
                        return;
                    }
                }

                if (pd.Received == pd.Total)
                {
                    int all = 0;
                    for (int i = 0; i < pd.Total; i++) all += pd.Parts[i].Length;
                    done = new byte[all];
                    int at = 0;
                    for (int i = 0; i < pd.Total; i++)
                    {
                        Buffer.BlockCopy(pd.Parts[i], 0, done, at, pd.Parts[i].Length);
                        at += pd.Parts[i].Length;
                    }
                    _map.Remove(msgId);
                    Completed++;
                }
            }

            if (done != null && onComplete != null)
            {
                try { onComplete(msgId, done); } catch { }
            }
        }

        /// <summary>清掉超时未拼齐的块（每片到达和定时器里都会调）。</summary>
        public void Purge(DateTime nowUtc)
        {
            lock (_lock) { PurgeLocked(nowUtc); }
        }

        private void PurgeLocked(DateTime nowUtc)
        {
            if (_map.Count == 0) return;
            List<uint> dead = null;
            foreach (KeyValuePair<uint, Pending> kv in _map)
            {
                if ((nowUtc - kv.Value.StartedUtc).TotalMilliseconds > ImageWire.ReasmTimeoutMs)
                {
                    if (dead == null) dead = new List<uint>();
                    dead.Add(kv.Key);
                }
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++)
            {
                _map.Remove(dead[i]);
                DroppedTimeouts++;
            }
        }
    }

    /// <summary>把图片压到可控体积：限制最长边 + JPEG 质量。</summary>
    internal static class ImageCodec
    {
        public const int MaxEdge = 1280;
        public const long JpegQuality = 72L;

        /// <summary>从文件读图（不锁文件）。</summary>
        public static Image LoadFile(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (Image raw = Image.FromStream(fs))
                    return new Bitmap(raw);
            }
            catch { return null; }
        }

        /// <summary>等比缩到最长边不超过 MaxEdge。</summary>
        public static Bitmap Shrink(Image src)
        {
            if (src == null) return null;
            int w = src.Width, h = src.Height;
            if (w <= 0 || h <= 0) return null;

            double k = 1.0;
            int edge = Math.Max(w, h);
            if (edge > ImageWire.MaxImageBytes) k = 1.0;   // 占位：真正的上限由 MaxEdge 控
            if (edge > MaxEdge) k = (double)MaxEdge / edge;

            int nw = Math.Max(1, (int)Math.Round(w * k));
            int nh = Math.Max(1, (int)Math.Round(h * k));

            Bitmap bmp = new Bitmap(nw, nh, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, nw, nh);
            }
            return bmp;
        }

        /// <summary>压成 JPEG 字节流；超过上限就降质量再压一次。</summary>
        public static byte[] ToJpeg(Image img, out int width, out int height)
        {
            width = img == null ? 0 : img.Width;
            height = img == null ? 0 : img.Height;
            if (img == null) return null;

            long q = JpegQuality;
            byte[] last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    EncoderParameters ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                    ImageCodecInfo info = FindJpegEncoder();
                    if (info != null) img.Save(ms, info, ep);
                    else img.Save(ms, ImageFormat.Jpeg);

                    last = ms.ToArray();
                }
                if (last.Length <= ImageWire.MaxImageBytes) return last;
                q = Math.Max(30L, q - 20L);   // 还是太大就降质量重来
            }
            return last;
        }

        private static ImageCodecInfo FindJpegEncoder()
        {
            ImageCodecInfo[] all = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < all.Length; i++)
                if (all[i].FormatID == ImageFormat.Jpeg.Guid) return all[i];
            return null;
        }
    }
}
