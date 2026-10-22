// PeerStore.cs —— 「连接过的人」缓存
//
// 目的：同一个人第二次进房间时，头像不必再传一遍。
//
// 索引是 ClientId（profile.txt 里那份持久 ID），不是网络上的 InstanceId ——
// 后者每次启动都是新随机值，没法用来认人。
//
// 磁盘布局（%LOCALAPPDATA%\LanTalk\）：
//   peers.txt            每行 clientId|名字|头像hash|最后见到的时间
//   peers\<id>.jpg       该用户的头像原始字节
//
// 只有 100 条上限，按最后见到时间淘汰，避免无限长大。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace LanTalk
{
    internal static class PeerStore
    {
        public const int MaxEntries = 100;

        internal sealed class Entry
        {
            public string ClientId = "";
            public string Name = "";
            public int AvatarHash;
            public DateTime LastSeen;
        }

        private static readonly Dictionary<string, Entry> _map =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Image> _img =
            new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        /// <summary>测试用：把落盘位置指到临时目录，避免污染真实数据。</summary>
        internal static string DirOverride;

        public static string RootDir { get { return string.IsNullOrEmpty(DirOverride) ? Profile.Dir : DirOverride; } }

        public static string Dir { get { return Path.Combine(RootDir, "peers"); } }
        public static string IndexPath { get { return Path.Combine(RootDir, "peers.txt"); } }

        /// <summary>测试用：清空内存状态，模拟进程重启。</summary>
        internal static void Reset()
        {
            _map.Clear();
            _img.Clear();
            _loaded = false;
        }

        public static string KeyOf(byte[] clientId)
        {
            if (clientId == null || clientId.Length == 0) return "";
            StringBuilder sb = new StringBuilder(clientId.Length * 2);
            for (int i = 0; i < clientId.Length; i++) sb.Append(clientId[i].ToString("x2"));
            return sb.ToString();
        }

        private static string AvatarFile(string clientId) { return Path.Combine(Dir, clientId + ".jpg"); }

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(IndexPath)) return;
                string[] lines = File.ReadAllLines(IndexPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (string.IsNullOrEmpty(l)) continue;
                    string[] f = l.Split('|');
                    if (f.Length < 4) continue;
                    Entry e = new Entry();
                    e.ClientId = f[0];
                    e.Name = f[1];
                    int h;
                    if (int.TryParse(f[2], out h)) e.AvatarHash = h;
                    long ticks;
                    e.LastSeen = long.TryParse(f[3], out ticks) ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.UtcNow;
                    if (e.ClientId.Length > 0) _map[e.ClientId] = e;
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(RootDir);
                StringBuilder sb = new StringBuilder();
                foreach (Entry e in _map.Values)
                    sb.AppendLine(e.ClientId + "|" + e.Name + "|" + e.AvatarHash + "|" + e.LastSeen.Ticks);
                File.WriteAllText(IndexPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>记录见过这个人（名字和头像指纹）。不写头像文件，只更新索引。</summary>
        public static void Touch(string clientId, string name, int avatarHash)
        {
            if (string.IsNullOrEmpty(clientId)) return;
            Load();
            Entry e;
            if (!_map.TryGetValue(clientId, out e))
            {
                e = new Entry();
                e.ClientId = clientId;
                _map[clientId] = e;
            }
            if (!string.IsNullOrEmpty(name)) e.Name = name;
            e.AvatarHash = avatarHash;
            e.LastSeen = DateTime.UtcNow;
            Save();
        }

        /// <summary>这个人现在的头像指纹是不是我们已经有了（有就不必再传）。</summary>
        public static bool HasAvatar(string clientId, int avatarHash)
        {
            if (string.IsNullOrEmpty(clientId) || avatarHash == 0) return false;
            Load();
            Entry e;
            if (!_map.TryGetValue(clientId, out e)) return false;
            if (e.AvatarHash != avatarHash) return false;
            try { return File.Exists(AvatarFile(clientId)); } catch { return false; }
        }

        public static void SaveAvatar(string clientId, byte[] jpeg, int avatarHash)
        {
            if (string.IsNullOrEmpty(clientId) || jpeg == null || jpeg.Length == 0) return;
            Load();
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(AvatarFile(clientId), jpeg);
                _img.Remove(clientId);
                Touch(clientId, null, avatarHash);
            }
            catch { }
        }

        /// <summary>取缓存头像；没有或解码失败返回 null。</summary>
        public static Image GetAvatar(string clientId)
        {
            if (string.IsNullOrEmpty(clientId)) return null;
            Image cached;
            if (_img.TryGetValue(clientId, out cached)) return cached;
            try
            {
                string f = AvatarFile(clientId);
                if (!File.Exists(f)) return null;
                byte[] b = File.ReadAllBytes(f);
                if (b.Length == 0) return null;
                Image img = Image.FromStream(new MemoryStream(b, false));
                _img[clientId] = img;
                return img;
            }
            catch { return null; }
        }

        /// <summary>超出上限就按最后见到时间淘汰最旧的，连头像文件一起删。</summary>
        public static void Prune()
        {
            Load();
            if (_map.Count <= MaxEntries) return;
            List<Entry> all = new List<Entry>(_map.Values);
            all.Sort(delegate(Entry a, Entry b) { return b.LastSeen.CompareTo(a.LastSeen); });  // 新的在前
            for (int i = MaxEntries; i < all.Count; i++)
            {
                string id = all[i].ClientId;
                _map.Remove(id);
                _img.Remove(id);
                try { string f = AvatarFile(id); if (File.Exists(f)) File.Delete(f); } catch { }
            }
            Save();
        }

        public static int Count { get { Load(); return _map.Count; } }
    }
}
