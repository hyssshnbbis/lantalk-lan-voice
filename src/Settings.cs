// Settings.cs —— 设置页
//
// 参照用户给的参考图：左边一条分组导航（顶部「返回」，下面按组列条目），
// 右边是内容区。内容是"页面"（Panel），切导航就切页面。
//
// 这里顺带把主页面上的两张卡搬进来（音频设备 / 通话模式与音量）。
// 因为卡片已经是独立模块，设置页直接 new 出来就行，外部通过属性拿引用。
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace LanTalk
{
    /// <summary>拖放更新包的投放区：虚线圈 + 拖动高亮。</summary>
    internal sealed class DropZone : Panel
    {
        private bool _hot;

        public DropZone()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AllowDrop = true;
            BackColor = Theme.MainBg;

            DragEnter += delegate(object s, DragEventArgs e)
            {
                if (TryGetPackage(e) != null) { e.Effect = DragDropEffects.Copy; _hot = true; Invalidate(); }
                else e.Effect = DragDropEffects.None;
            };
            DragLeave += delegate { _hot = false; Invalidate(); };
            DragDrop += delegate(object s, DragEventArgs e)
            {
                _hot = false;
                Invalidate();
                string file = TryGetPackage(e);
                if (file == null) return;
                EventHandler<string> h = PackageDropped;
                if (h != null) h(this, file);
            };
        }

        /// <summary>拖进来一个合法安装包时触发（参数是文件路径）。</summary>
        public event EventHandler<string> PackageDropped;

        private static string TryGetPackage(DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return null;
                string f = files[0];
                if (!File.Exists(f)) return null;
                if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !SelfUpdate.LooksLikeUpdateZip(f)) return null;
                return f;
            }
            catch { return null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            using (SolidBrush b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.MainBg))
                g.FillRectangle(b, ClientRectangle);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
            int rad = Theme.Radius(this, 10);

            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(_hot ? Color.FromArgb(38, 52, 42) : Color.FromArgb(30, 30, 30)))
                    g.FillPath(b, p);
                using (Pen pen = new Pen(_hot ? Theme.Accent : Color.FromArgb(70, 70, 70), 1.6f))
                {
                    pen.DashStyle = DashStyle.Dash;
                    pen.DashPattern = new float[] { 4f, 3f };
                    g.DrawPath(pen, p);
                }
            }

            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(_hot ? Theme.Accent : Theme.TextSecondary))
                    g.DrawString("把更新包（LanTalk-x.y.z-update.zip）或完整安装包（.exe）拖到这里", Theme.FontUi, b,
                                 new RectangleF(8, 8, Width - 16, Height - 16), sf);
            }
        }
    }

    /// <summary>设置页：左导航 + 右内容。</summary>
    internal sealed class SettingsPanel : Panel
    {
        public const string AppVersion = "1.2.0";
        public const string ReleasesUrl = "https://github.com/hyssshnbbis/lantalk-lan-voice/releases";

        public readonly DeviceCard Device;
        public readonly ModeCard Mode;

        public readonly FlatButton Back;
        public readonly FlatButton NavSettings;
        public readonly FlatButton NavProfile;
        public readonly FlatButton NavUpdate;

        public readonly Panel PageSettings;
        public readonly Panel PageProfile;

        public readonly DarkInput NameInput;
        public readonly PictureBox AvatarBox;
        public readonly FlatButton AvatarPick, AvatarClear;
        public readonly Label ProfileHint;
        public readonly Panel PageUpdate;

        public readonly Label VersionLabel;
        public readonly Label UpdateHint;
        public readonly FlatButton OpenReleases;
        public readonly DropZone Drop;

        private const int NavW = 214;

        public SettingsPanel(int x, int y, int w, int h)
        {
            SetBounds(x, y, w, h);
            BackColor = Theme.MainBg;
            Visible = false;

            // ---------------- 左导航 ----------------
            Panel nav = new Panel();
            nav.SetBounds(0, 0, NavW, h);
            nav.BackColor = Theme.SidebarBg;

            Back = new FlatButton(FlatButton.Look.Ghost);
            Back.Text = "‹  返回";
            Back.TabStop = false;
            Back.SetBounds(14, 14, 96, 30);
            Back.Click += delegate { if (BackClicked != null) BackClicked(this, EventArgs.Empty); };

            NavProfile = NavItem("个人资料", 14, 60);
            NavSettings = NavItem("设置", 14, 100);
            NavSettings.Click += delegate { ShowPageSettings(); };

            Label g2 = SectionLabel("应用", 14, 148);
            NavUpdate = NavItem("检查更新", 14, 172);
            NavUpdate.Click += delegate { ShowUpdate(); };

            NavProfile.Click += delegate { ShowPageProfile(); };
            nav.Controls.AddRange(new Control[] { Back, NavProfile, NavSettings, g2, NavUpdate });

            // ---------------- 右内容 ----------------
            Panel body = new Panel();
            body.SetBounds(NavW, 0, w - NavW, h);
            body.BackColor = Theme.MainBg;

            // 语音页：两张卡并排
            PageSettings = new Panel();
            PageSettings.SetBounds(0, 0, w - NavW, h);
            PageSettings.BackColor = Theme.MainBg;

            Device = new DeviceCard(18, 18);
            Mode = new ModeCard(442, 18);
            PageSettings.Controls.AddRange(new Control[] { Device, Mode });

            // 更新页
            PageUpdate = new Panel();
            PageUpdate.SetBounds(0, 0, w - NavW, h);
            PageUpdate.BackColor = Theme.MainBg;
            PageUpdate.Visible = false;

            Label t = new Label();
            t.Text = "检查更新";
            t.Font = Theme.FontTitle;
            t.ForeColor = Theme.TextPrimary;
            t.BackColor = Color.Transparent;
            t.SetBounds(24, 20, 300, 26);

            VersionLabel = new Label();
            VersionLabel.Text = "当前版本 v" + AppVersion;
            VersionLabel.Font = Theme.FontUi;
            VersionLabel.ForeColor = Theme.TextSecondary;
            VersionLabel.BackColor = Color.Transparent;
            VersionLabel.SetBounds(24, 52, 420, 20);

            OpenReleases = new FlatButton(FlatButton.Look.Primary);
            OpenReleases.Text = "打开下载页";
            OpenReleases.TabStop = false;
            OpenReleases.SetBounds(24, 86, 140, 34);
            OpenReleases.Click += delegate { OpenReleasesPage(); };

            Label steps = new Label();
            steps.Text = "更新方法：① 点「打开下载页」到 GitHub Releases 选最新的 LanTalk-Setup-*.exe 下载\r\n"
                       + "② 把下载好的安装包直接拖到下面的方框里，确认后会自动启动安装程序";
            steps.Font = Theme.FontSmall;
            steps.ForeColor = Theme.TextMuted;
            steps.BackColor = Color.Transparent;
            steps.SetBounds(24, 132, 620, 40);

            Drop = new DropZone();
            Drop.SetBounds(24, 182, 640, 96);
            Drop.PackageDropped += delegate(object s, string file) { OnPackageDropped(file); };

            UpdateHint = new Label();
            UpdateHint.Text = "";
            UpdateHint.Font = Theme.FontSmall;
            UpdateHint.ForeColor = Theme.TextMuted;
            UpdateHint.BackColor = Color.Transparent;
            UpdateHint.SetBounds(24, 286, 640, 20);

            PageUpdate.Controls.AddRange(new Control[] { t, VersionLabel, OpenReleases, steps, Drop, UpdateHint });

            // ---------------- 个人资料页 ----------------
            PageProfile = new Panel();
            PageProfile.SetBounds(0, 0, w - NavW, h);
            PageProfile.BackColor = Theme.MainBg;
            PageProfile.Visible = false;

            Label pt = new Label();
            pt.Text = "个人资料";
            pt.Font = Theme.FontTitle;
            pt.ForeColor = Theme.TextPrimary;
            pt.BackColor = Color.Transparent;
            pt.SetBounds(24, 20, 300, 26);

            Label ln = new Label();
            ln.Text = "显示名字";
            ln.Font = Theme.FontUi;
            ln.ForeColor = Theme.TextSecondary;
            ln.BackColor = Color.Transparent;
            ln.SetBounds(24, 56, 200, 20);

            NameInput = new DarkInput();
            NameInput.Placeholder = Profile.FallbackName;
            NameInput.SetBounds(24, 80, 340, 32);
            NameInput.Text = Profile.HasCustomName ? Profile.Name : "";
            NameInput.Box.Leave += delegate
            {
                Profile.SetName(NameInput.Text);
                ProfileHint.ForeColor = Theme.TextMuted;
                ProfileHint.Text = "已保存。当前显示名：" + Profile.Name + (Profile.HasCustomName ? "" : "（机器名）");
            };

            Label la = new Label();
            la.Text = "头像";
            la.Font = Theme.FontUi;
            la.ForeColor = Theme.TextSecondary;
            la.BackColor = Color.Transparent;
            la.SetBounds(24, 128, 200, 20);

            AvatarBox = new PictureBox();
            AvatarBox.SetBounds(24, 152, 96, 96);
            AvatarBox.BackColor = Color.FromArgb(18, 18, 18);
            AvatarBox.BorderStyle = BorderStyle.FixedSingle;
            AvatarBox.SizeMode = PictureBoxSizeMode.Zoom;
            AvatarBox.Image = Profile.Avatar;

            AvatarPick = new FlatButton(FlatButton.Look.Primary);
            AvatarPick.Text = "选择图片";
            AvatarPick.TabStop = false;
            AvatarPick.SetBounds(136, 152, 120, 32);
            AvatarPick.Click += delegate
            {
                string msg;
                if (Profile.PickAvatar(FindForm(), out msg))
                {
                    AvatarBox.Image = Profile.Avatar;
                    ProfileHint.ForeColor = Theme.Accent;
                }
                else { ProfileHint.ForeColor = Theme.TextMuted; }
                ProfileHint.Text = msg;
            };

            AvatarClear = new FlatButton(FlatButton.Look.Normal);
            AvatarClear.Text = "清除头像";
            AvatarClear.TabStop = false;
            AvatarClear.SetBounds(136, 192, 120, 32);
            AvatarClear.Click += delegate
            {
                Profile.ClearAvatar();
                AvatarBox.Image = null;
                ProfileHint.ForeColor = Theme.TextMuted;
                ProfileHint.Text = "头像已清除。";
            };

            ProfileHint = new Label();
            ProfileHint.Text = "名字会随通话发给对方；头像目前只有本机可见（协议暂不传头像）。";
            ProfileHint.Font = Theme.FontSmall;
            ProfileHint.ForeColor = Theme.TextMuted;
            ProfileHint.BackColor = Color.Transparent;
            ProfileHint.SetBounds(24, 268, 640, 40);

            PageProfile.Controls.AddRange(new Control[] { pt, ln, NameInput, la, AvatarBox, AvatarPick, AvatarClear, ProfileHint });
            body.Controls.AddRange(new Control[] { PageProfile, PageSettings, PageUpdate });

            Controls.AddRange(new Control[] { nav, body });

            ShowPageSettings();
        }

        public event EventHandler BackClicked;
        public event EventHandler<string> PackageDropped;

        private Label SectionLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontSmall;
            l.ForeColor = Theme.TextMuted;
            l.BackColor = Color.Transparent;
            l.SetBounds(x, y, NavW - 28, 18);
            return l;
        }

        private FlatButton NavItem(string text, int x, int y)
        {
            FlatButton b = new FlatButton(FlatButton.Look.Ghost);
            b.Text = text;
            b.TabStop = false;
            b.SetBounds(x, y, NavW - 28, 34);
            return b;
        }

        public void ShowPageProfile()
        {
            PageProfile.Visible = true;
            PageSettings.Visible = false;
            PageUpdate.Visible = false;
            NavProfile.Selected = true;
            NavSettings.Selected = false;
            NavUpdate.Selected = false;
        }
        public void ShowPageSettings()
        {
            PageProfile.Visible = false;
            PageSettings.Visible = true;
            PageUpdate.Visible = false;
            NavProfile.Selected = false;
            NavSettings.Selected = true;
            NavUpdate.Selected = false;
        }

        public void ShowUpdate()
        {
            PageProfile.Visible = false;
            PageSettings.Visible = false;
            PageUpdate.Visible = true;
            NavProfile.Selected = false;
            NavSettings.Selected = false;
            NavUpdate.Selected = true;
            VersionLabel.Text = "当前版本 v" + AppVersion;
        }

        private void OpenReleasesPage()
        {
            try { Process.Start(ReleasesUrl); }
            catch (Exception ex) { UpdateHint.Text = "打不开浏览器：" + ex.Message; }
        }

        private void OnPackageDropped(string file)
        {
            EventHandler<string> h = PackageDropped;
            if (h != null) h(this, file);
        }
    }
}
