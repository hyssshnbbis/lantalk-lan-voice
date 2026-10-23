// Chat.cs —— 文本消息的报文编解码
//
// 会议室和一对一都用同一种载荷格式：
//   [实例ID 4][名字长度 1][名字 utf8][文本 utf8]
// 长度字段只用于名字，文本直接取剩下的字节。
//
// 单包上限按 700 字节文本算：会议室音频包是 20ms 一帧，文本包偶发一次，
// 不会和音频抢带宽；同时保证整个 UDP 载荷远小于 MTU，不会被 IP 分片。

using System;
using System.Text;

namespace LanTalk
{
    /// <summary>
    /// 聊天记录落盘。一行一条，用 | 分隔，换行和 | 都做转义。
    /// 只追加，超过上限就整份重写到 .bak 再重新开始，避免文件无限长大。
    /// </summary>
    internal static class ChatHistory
    {
        private const long MaxBytes = 2 * 1024 * 1024;

        public static string Dir
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.DataFolder);
            }
        }

        public static string FilePath { get { return System.IO.Path.Combine(Dir, "chat.log"); } }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("|", "\\p").Replace("\r", "").Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'p') sb.Append('|');
                    else if (n == 'n') sb.Append('\n');
                    else sb.Append(n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static void Append(DateTime when, string who, string text, bool self)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Dir);
                string path = FilePath;

                // 太长就滚到 .bak
                try
                {
                    System.IO.FileInfo fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string bak = path + ".bak";
                        if (System.IO.File.Exists(bak)) System.IO.File.Delete(bak);
                        System.IO.File.Move(path, bak);
                    }
                }
                catch { }

                System.IO.File.AppendAllText(path, LineOf(when, who, text, self) + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>落盘时的一行（不含换行）。删除时按同一格式找那一行。</summary>
        private static string LineOf(DateTime when, string who, string text, bool self)
        {
            return when.ToString("yyyy-MM-dd HH:mm:ss") + "|" + (self ? "1" : "0") + "|" +
                   Escape(who) + "|" + Escape(text);
        }

        /// <summary>
        /// 删掉一条本地记录：按「时间|自己|名字|正文」重建那一行，删掉第一处匹配。
        /// 只动本机的 chat.log，对方那边的记录不受影响。返回是否真的删到了。
        /// </summary>
        public static bool Delete(ChatMsg m)
        {
            if (m == null || m.Image != null) return false;   // 图片本来就不进记录
            try
            {
                string path = FilePath;
                if (!System.IO.File.Exists(path)) return false;

                string target = LineOf(m.When, m.Who, m.Text, m.Self);
                string[] all = System.IO.File.ReadAllLines(path, Encoding.UTF8);
                System.Collections.Generic.List<string> keep = new System.Collections.Generic.List<string>();
                bool removed = false;
                for (int i = 0; i < all.Length; i++)
                {
                    if (!removed && all[i] == target) { removed = true; continue; }
                    keep.Add(all[i]);
                }
                if (!removed) return false;
                System.IO.File.WriteAllLines(path, keep.ToArray(), Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }

        /// <summary>清空本地记录：连滚动备份 .bak 一起删掉。</summary>
        public static void Clear()
        {
            try { if (System.IO.File.Exists(FilePath)) System.IO.File.Delete(FilePath); } catch { }
            try
            {
                string bak = FilePath + ".bak";
                if (System.IO.File.Exists(bak)) System.IO.File.Delete(bak);
            }
            catch { }
        }

        /// <summary>读出最后 maxLines 条，返回结构化消息（给消息流用）。</summary>
        public static System.Collections.Generic.List<ChatMsg> LoadTailMsgs(int maxLines)
        {
            System.Collections.Generic.List<ChatMsg> outp = new System.Collections.Generic.List<ChatMsg>();
            try
            {
                if (!System.IO.File.Exists(FilePath)) return outp;
                string[] all = System.IO.File.ReadAllLines(FilePath, Encoding.UTF8);
                int start = all.Length > maxLines ? all.Length - maxLines : 0;
                for (int i = start; i < all.Length; i++)
                {
                    string l = all[i];
                    if (string.IsNullOrEmpty(l)) continue;
                    string[] f = l.Split('|');
                    if (f.Length < 4) continue;
                    DateTime when;
                    if (!DateTime.TryParse(f[0], out when)) continue;
                    ChatMsg m = new ChatMsg();
                    m.When = when;
                    m.Self = f[1] == "1";
                    m.Who = Unescape(f[2]);
                    m.Text = Unescape(f[3]);
                    outp.Add(m);
                }
            }
            catch { }
            return outp;
        }

        /// <summary>读出最后 maxLines 条，按时间正序返回显示行。</summary>
        public static string[] LoadTail(int maxLines)
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return new string[0];
                string[] all = System.IO.File.ReadAllLines(FilePath, Encoding.UTF8);
                int start = all.Length > maxLines ? all.Length - maxLines : 0;
                System.Collections.Generic.List<string> outp = new System.Collections.Generic.List<string>();
                for (int i = start; i < all.Length; i++)
                {
                    string l = all[i];
                    if (string.IsNullOrEmpty(l)) continue;
                    string[] f = l.Split('|');
                    if (f.Length < 4) continue;
                    DateTime when;
                    if (!DateTime.TryParse(f[0], out when)) continue;
                    bool self = f[1] == "1";
                    string who = Unescape(f[2]);
                    string text = Unescape(f[3]);
                    outp.Add(ChatFormat.Line(when, self ? "我" : who, text));
                }
                return outp.ToArray();
            }
            catch { return new string[0]; }
        }
    }

    /// <summary>聊天行的统一显示格式，落盘回读和实时显示共用。</summary>
    internal static class ChatFormat
    {
        public static string Line(DateTime when, string who, string text)
        {
            if (who == null) who = "";
            if (text == null) text = "";
            return "[" + when.ToString("HH:mm:ss") + "] " + who + "：" + text;
        }
    }

    internal static class ChatWire
    {
        /// <summary>单条消息正文的最大字节数（UTF-8 计）。</summary>
        public const int MaxTextBytes = 700;

        /// <summary>名字最长的字节数，用一个字节存长度，所以最多 255。</summary>
        public const int MaxNameBytes = 60;

        public static byte[] Build(uint senderId, string senderName, string text)
        {
            if (text == null) text = "";
            if (senderName == null) senderName = "";

            byte[] tb = Encoding.UTF8.GetBytes(text);
            if (tb.Length > MaxTextBytes)
            {
                // 按字符截断直到不超限，避免把一个多字节字符切成半个
                int n = text.Length;
                while (n > 0 && Encoding.UTF8.GetByteCount(text.Substring(0, n)) > MaxTextBytes) n--;
                tb = Encoding.UTF8.GetBytes(text.Substring(0, n));
            }

            byte[] nb = Encoding.UTF8.GetBytes(senderName);
            if (nb.Length > MaxNameBytes)
            {
                int n = senderName.Length;
                while (n > 0 && Encoding.UTF8.GetByteCount(senderName.Substring(0, n)) > MaxNameBytes) n--;
                nb = Encoding.UTF8.GetBytes(senderName.Substring(0, n));
            }

            byte[] p = new byte[4 + 1 + nb.Length + tb.Length];
            p[0] = (byte)(senderId & 0xFF);
            p[1] = (byte)((senderId >> 8) & 0xFF);
            p[2] = (byte)((senderId >> 16) & 0xFF);
            p[3] = (byte)((senderId >> 24) & 0xFF);
            p[4] = (byte)nb.Length;
            if (nb.Length > 0) Buffer.BlockCopy(nb, 0, p, 5, nb.Length);
            if (tb.Length > 0) Buffer.BlockCopy(tb, 0, p, 5 + nb.Length, tb.Length);
            return p;
        }

        public static bool Parse(byte[] payload, int len, out uint senderId, out string senderName, out string text)
        {
            senderId = 0;
            senderName = "";
            text = "";
            if (payload == null || len < 5) return false;

            senderId = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
            int nameLen = payload[4];
            if (5 + nameLen > len) return false;

            if (nameLen > 0) senderName = Encoding.UTF8.GetString(payload, 5, nameLen);
            int textLen = len - 5 - nameLen;
            if (textLen > 0) text = Encoding.UTF8.GetString(payload, 5 + nameLen, textLen);
            return true;
        }
    }
}
