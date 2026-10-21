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
                if (!f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
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
                    g.DrawString("把下载好的安装包（LanTalk-Setup-x.y.z.exe）拖到这里", Theme.FontUi, b,
                                 new RectangleF(8, 8, Width - 16, Height - 16), sf);
            }
        }
    }

    /// <summary>设置页：左导航 + 右内容。</summary>
    internal sealed class SettingsPanel : Panel
    {
        public const string AppVersion = "1.1.0";
        public const string ReleasesUrl = "https://github.com/hyssshnbbis/lantalk-lan-voice/releases";

        public readonly DeviceCard Device;
        public readonly ModeCard Mode;

        public readonly FlatButton Back;
        public readonly FlatButton NavSettings;
        public readonly FlatButton NavUpdate;

        public readonly Panel PageSettings;
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

            NavSettings = NavItem("设置", 14, 60);
            NavSettings.Click += delegate { ShowPageSettings(); };

            Label g2 = SectionLabel("应用", 14, 116);
            NavUpdate = NavItem("检查更新", 14, 140);
            NavUpdate.Click += delegate { ShowUpdate(); };

            nav.Controls.AddRange(new Control[] { Back, NavSettings, g2, NavUpdate });

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

            body.Controls.AddRange(new Control[] { PageSettings, PageUpdate });

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

        public void ShowPageSettings()
        {
            PageSettings.Visible = true;
            PageUpdate.Visible = false;
            NavSettings.Selected = true;
            NavUpdate.Selected = false;
        }

        public void ShowUpdate()
        {
            PageSettings.Visible = false;
            PageUpdate.Visible = true;
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
