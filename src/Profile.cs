// Profile.cs —— 用户自定义资料（名字 + 头像）
//
// 存在 %LOCALAPPDATA%\LanTalk\ 下：
//   profile.txt   一行 name=<自定义名字>
//   avatar.jpg    头像（用户选图后统一转成 JPEG 存起来，避免原图格式五花八门）
//
// 名字会随协议发给对方（RoomHello / Chat / 会议公告里都带名字），
// 所以这里改的是"全网可见的显示名"。
//
// 头像目前只在本机使用：协议载荷里没有头像字段，要让别人看到得另外传，
// 那是分片传输层的事，不在这个模块里做。

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace LanTalk
{
    internal static class Profile
    {
        private static string _name = "";
        private static string _clientId = "";
        // 默认值：测试版默认开（方便试听），正式版默认关（不惊动现有用户）
        private static bool _humFilter = AppPaths.IsPreview;
        private static bool _clickFilter = AppPaths.IsPreview;
        private static bool _sidetone;
        private static int _micGain = 100;
        private static Image _avatar;
        private static bool _avatarLoaded;

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.DataFolder);
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "profile.txt"); } }

        /// <summary>电流声抑制（工频陷波），默认开。</summary>
        public static bool HumFilter
        {
            get { return _humFilter; }
            set { _humFilter = value; Save(); }
        }

        /// <summary>耳返：把自己的麦克风（处理后的）混进本地播放，便于自己试听。</summary>
        public static bool Sidetone
        {
            get { return _sidetone; }
            set { _sidetone = value; Save(); }
        }

        /// <summary>键盘等冲击噪声抑制，默认开。</summary>
        /// <summary>麦克风增益百分比 0..150。100 = 原样，>100 会放大（可能削顶）。</summary>
        public static int MicGain
        {
            get { return _micGain; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 150) v = 150;
                if (v == _micGain) return;
                _micGain = v;
                Save();
            }
        }

        public static bool ClickFilter
        {
            get { return _clickFilter; }
            set { _clickFilter = value; Save(); }
        }

        /// <summary>ClientId 的 16 字节二进制形式（协议里用它，省得传 32 个字符）。</summary>
        public static byte[] ClientIdBytes
        {
            get
            {
                byte[] b = new byte[16];
                string h = ClientId;
                for (int i = 0; i < 16 && (i * 2 + 1) < h.Length; i++)
                {
                    try { b[i] = Convert.ToByte(h.Substring(i * 2, 2), 16); }
                    catch { b[i] = 0; }
                }
                return b;
            }
        }

        /// <summary>
        /// 本机持久身份（32 位十六进制）。第一次运行时生成并落盘，之后一直不变。
        /// 网络上的 InstanceId 每次启动都是新的随机值，没法用来索引"连接过的人"，
        /// 所以对端缓存、头像复用都必须以这个 ClientId 为键。
        /// </summary>
        public static string ClientId
        {
            get
            {
                if (string.IsNullOrEmpty(_clientId))
                {
                    _clientId = Guid.NewGuid().ToString("N");
                    Save();
                }
                return _clientId;
            }
        }
        public static string AvatarPath { get { return Path.Combine(Dir, "avatar.jpg"); } }

        /// <summary>没设过名字时的回退值。</summary>
        public static string FallbackName
        {
            get { return Environment.MachineName + " / " + Environment.UserName; }
        }

        /// <summary>界面上和网络上显示的名字。</summary>
        public static string Name
        {
            get { return string.IsNullOrEmpty(_name) ? FallbackName : _name; }
        }

        /// <summary>用户是否真的设过自定义名字（用于界面提示"当前用机器名"）。</summary>
        public static bool HasCustomName { get { return !string.IsNullOrEmpty(_name); } }

        /// <summary>头像图；没设过返回 null。失败不会抛。</summary>
        public static Image Avatar
        {
            get
            {
                if (!_avatarLoaded)
                {
                    _avatarLoaded = true;
                    _avatar = LoadImage(AvatarPath);
                }
                return _avatar;
            }
        }

        public static bool HasAvatar { get { return Avatar != null; } }

        /// <summary>头像原始 JPEG 字节（发给别人用）。没设过返回 null。</summary>
        public static byte[] AvatarBytes
        {
            get
            {
                try { return File.Exists(AvatarPath) ? File.ReadAllBytes(AvatarPath) : null; }
                catch { return null; }
            }
        }

        private static Image LoadImage(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] b = File.ReadAllBytes(path);
                if (b.Length == 0) return null;
                // 用 MemoryStream 包一层：Image.FromStream 会持有流，文件被占用就删不掉了
                return Image.FromStream(new MemoryStream(b, false));
            }
            catch { return null; }
        }

        /// <summary>启动时读一次。</summary>
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = lines[i];
                    if (l == null) continue;
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = l.Substring(0, eq).Trim();
                    string v = l.Substring(eq + 1).Trim();
                    if (k == "name") _name = v;
                    else if (k == "clientId") _clientId = v;
                    else if (k == "humfilter") _humFilter = v != "0";
                    else if (k == "clickfilter") _clickFilter = v != "0";
                    else if (k == "sidetone") _sidetone = v != "0";
            else if (k == "micgain") { int g; if (int.TryParse(v, out g)) { if (g < 0) g = 0; if (g > 150) g = 150; _micGain = g; } }
                }
            }
            catch { }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("name=" + _name);
                sb.AppendLine("clientId=" + _clientId);
                sb.AppendLine("humfilter=" + (_humFilter ? "1" : "0"));
                sb.AppendLine("clickfilter=" + (_clickFilter ? "1" : "0"));
                sb.AppendLine("sidetone=" + (_sidetone ? "1" : "0"));
            sb.AppendLine("micgain=" + _micGain);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>设名字；传空串表示恢复成机器名。</summary>
        public static void SetName(string name)
        {
            _name = name == null ? "" : name.Trim();
            if (_name.Length > 40) _name = _name.Substring(0, 40);
            Save();
        }

        /// <summary>从文件设头像：统一转成 256×256 以内的 JPEG 存到 appdata。</summary>
        public static bool SetAvatarFromFile(string path, out string message)
        {
            message = "";
            try
            {
                if (!File.Exists(path)) { message = "找不到图片文件"; return false; }

                byte[] raw = File.ReadAllBytes(path);
                Image src;
                try { src = Image.FromStream(new MemoryStream(raw, false)); }
                catch { message = "这个文件不是可识别的图片"; return false; }

                int w = 0, h = 0;
                using (src)
                {
                    // 等比缩到最长边 256，控制体积
                    double k = Math.Min(1.0, 256.0 / Math.Max(src.Width, src.Height));
                    w = Math.Max(1, (int)(src.Width * k));
                    h = Math.Max(1, (int)(src.Height * k));

                    using (Bitmap small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        Directory.CreateDirectory(Dir);
                        if (File.Exists(AvatarPath)) File.Delete(AvatarPath);
                        small.Save(AvatarPath, ImageFormat.Jpeg);
                    }
                }

                _avatar = LoadImage(AvatarPath);
                _avatarLoaded = true;
                message = "头像已更新（" + w + "×" + h + "）";
                return true;
            }
            catch (Exception ex)
            {
                message = "设置头像失败：" + ex.Message;
                return false;
            }
        }

        public static void ClearAvatar()
        {
            try { if (File.Exists(AvatarPath)) File.Delete(AvatarPath); } catch { }
            _avatar = null;
            _avatarLoaded = true;
        }

        /// <summary>弹文件选择框选一张图当头像。返回是否真的换了。</summary>
        public static bool PickAvatar(IWin32Window owner, out string message)
        {
            message = "";
            try
            {
                OpenFileDialog dlg = new OpenFileDialog();
                dlg.Title = "选择头像";
                dlg.Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件 (*.*)|*.*";
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                return SetAvatarFromFile(dlg.FileName, out message);
            }
            catch (Exception ex)
            {
                message = "选择头像失败：" + ex.Message;
                return false;
            }
        }
    }
}
