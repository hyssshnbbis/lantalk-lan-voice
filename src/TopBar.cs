// TopBar.cs —— 顶部栏
//
// 拆成三个互不依赖的小模块，TopBar 只负责摆放：
//   BrandBlock    品牌字（点击进设置）
//   ControlPill   胶囊控件组（话筒 / 扬声器 / 挂断）
//   WindowButtons 最小化 / 关闭
// 每个模块自己管自己的尺寸和外观，改其中一个不会牵动另外两个。
// TopBar 不把宽度传给它们——位置是写死的固定坐标，与顶栏宽度无关。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LanTalk
{
    /// <summary>左上角品牌字。点击 = 进设置。</summary>
    internal sealed class BrandBlock : Label
    {
        public BrandBlock()
        {
            Text = "LanTalk";
            AutoSize = false;
            TextAlign = ContentAlignment.MiddleLeft;
            Font = Theme.FontLogo;
            ForeColor = Theme.TextPrimary;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;

            MouseEnter += delegate { ForeColor = Theme.Accent; };
            MouseLeave += delegate { ForeColor = Theme.TextPrimary; };

            ToolTip tip = new ToolTip();
            tip.SetToolTip(this, "点击进入设置");
        }
    }

    /// <summary>胶囊里用的图标按钮：矢量绘制，不依赖图标资源。</summary>
    internal sealed class IconButton : Control
    {
        public enum Glyph { Mic, Speaker, Hangup, Minimize, Close }

        public readonly Glyph Kind;
        public readonly bool Danger;
        private bool _selected;
        private bool _hover;

        public IconButton(Glyph kind, bool danger)
        {
            Kind = kind;
            Danger = danger;
            // 图标按钮不显示文字，但 Text 会写进窗口文本：
            // 读屏软件能念出来，界面冒烟测试也能按文字找到它。
            switch (kind)
            {
                case Glyph.Mic: Text = "话筒"; break;
                case Glyph.Speaker: Text = "扬声器"; break;
                case Glyph.Hangup: Text = "挂断"; break;
                case Glyph.Minimize: Text = "最小化"; break;
                default: Text = "关闭"; break;
            }
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.InitFonts();
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(30, 30, 30);
            SetBounds(0, 0, 34, 34);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(this, Text);
        }

        public bool Selected { get { return _selected; } set { _selected = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 背景：选中 / 悬停
            if (_selected || _hover)
            {
                Color bg = _selected ? Color.FromArgb(48, 66, 54) : Color.FromArgb(44, 44, 44);
                using (GraphicsPath p = Theme.RoundRect(new Rectangle(1, 1, Width - 3, Height - 3), Theme.Radius(this, 8)))
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
            }

            Color fg;
            if (!Enabled) fg = Color.FromArgb(90, 90, 90);
            else if (Danger) fg = _hover ? Color.FromArgb(245, 96, 100) : Theme.Danger;
            else if (_selected) fg = Theme.Accent;
            else fg = _hover ? Theme.TextPrimary : Theme.TextSecondary;

            int cx = Width / 2;
            int cy = Height / 2;
            using (Pen pen = new Pen(fg, 1.7f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (Kind == Glyph.Mic)
                {
                    // 胶囊头 + 支架
                    using (GraphicsPath cap = Theme.RoundRect(new Rectangle(cx - 3, cy - 9, 7, 12), 3))
                        g.DrawPath(pen, cap);
                    g.DrawArc(pen, cx - 7, cy - 2, 15, 14, 0, 180);
                    g.DrawLine(pen, cx, cy + 5, cx, cy + 8);
                    g.DrawLine(pen, cx - 4, cy + 8, cx + 4, cy + 8);
                }
                else if (Kind == Glyph.Speaker)
                {
                    // 头戴式耳机：上半圆 + 两侧耳罩（互不重叠）
                    g.DrawArc(pen, cx - 7, cy - 8, 15, 15, 180, 180);
                    using (GraphicsPath l = Theme.RoundRect(new Rectangle(cx - 7, cy - 1, 4, 8), 2))
                    using (GraphicsPath r = Theme.RoundRect(new Rectangle(cx + 4, cy - 1, 4, 8), 2))
                    {
                        g.DrawPath(pen, l);
                        g.DrawPath(pen, r);
                    }
                }
                else if (Kind == Glyph.Minimize)
                {
                    g.DrawLine(pen, cx - 6, cy, cx + 6, cy);
                }
                else if (Kind == Glyph.Close)
                {
                    g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                }
                else
                {
                    // 离开：左括号 + 向右的箭头
                    g.DrawLine(pen, cx + 3, cy - 8, cx - 5, cy - 8);
                    g.DrawLine(pen, cx - 5, cy - 8, cx - 5, cy + 8);
                    g.DrawLine(pen, cx - 5, cy + 8, cx + 3, cy + 8);
                    g.DrawLine(pen, cx - 1, cy, cx + 8, cy);
                    g.DrawLine(pen, cx + 5, cy - 3, cx + 8, cy);
                    g.DrawLine(pen, cx + 8, cy, cx + 5, cy + 3);
                }
            }
        }
    }

    /// <summary>胶囊控件组：话筒 / 扬声器 / 挂断。只管显示和转发，不持有业务状态。</summary>
    internal sealed class ControlPill : CardPanel
    {
        private readonly IconButton _mic, _spk, _hangup;

        public event Action MicToggled;
        public event Action SpeakerToggled;
        public event Action HangupClicked;

        public ControlPill()
        {
            Title = "";
            RadiusLogical = 21;
            Background = Color.FromArgb(30, 30, 30);
            SetBounds(0, 0, 118, 42);

            _mic = Icon(IconButton.Glyph.Mic, 4, false);
            _spk = Icon(IconButton.Glyph.Speaker, 42, false);
            _hangup = Icon(IconButton.Glyph.Hangup, 80, true);
            Controls.AddRange(new Control[] { _mic, _spk, _hangup });

            _mic.Click += delegate { Fire(MicToggled); };
            _spk.Click += delegate { Fire(SpeakerToggled); };
            _hangup.Click += delegate { Fire(HangupClicked); };
        }

        private IconButton Icon(IconButton.Glyph kind, int x, bool danger)
        {
            IconButton b = new IconButton(kind, danger);
            b.SetBounds(x, 4, 34, 34);
            return b;
        }

        public void SetMicMuted(bool muted) { _mic.Selected = muted; }
        public void SetSpeakerMuted(bool muted) { _spk.Selected = muted; }
        public void SetHangupEnabled(bool on) { _hangup.Enabled = on; }

        private static void Fire(Action a)
        {
            if (a != null) { try { a(); } catch { } }
        }
    }

    /// <summary>右上角窗口按钮：分隔线 + 最小化 + 关闭。</summary>
    internal sealed class WindowButtons : Panel
    {
        private readonly IconButton _min, _close;

        public WindowButtons()
        {
            BackColor = Color.Transparent;
            SetBounds(0, 0, 74, 26);

            // 窗口按钮也走矢量绘制，避免任何字体差异
            _min = new IconButton(IconButton.Glyph.Minimize, false);
            _min.SetBounds(0, 0, 32, 26);
            _min.Click += delegate { Form f = FindForm(); if (f != null) f.WindowState = FormWindowState.Minimized; };

            _close = new IconButton(IconButton.Glyph.Close, false);
            _close.SetBounds(42, 0, 32, 26);
            _close.Click += delegate { Form f = FindForm(); if (f != null) f.Close(); };

            Controls.AddRange(new Control[] { _min, _close });
        }
    }

    /// <summary>顶部栏：只负责摆放上面三个模块，并处理拖动。</summary>
    internal sealed class TopBar : Panel
    {
        public const int BarHeight = 60;

        public readonly BrandBlock Brand;
        public readonly ControlPill Pill;
        public readonly WindowButtons Windows;

        private bool _dragging;
        private Point _dragOffset;

        public event Action SettingsClicked;

        public TopBar()
        {
            SetBounds(0, 0, 1120, BarHeight);
            BackColor = Theme.RailBg;

            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;

            Brand = new BrandBlock();
            Brand.SetBounds(20, 0, 220, BarHeight);
            Brand.Click += delegate
            {
                Action h = SettingsClicked;
                if (h != null) { try { h(); } catch { } }
            };

            Pill = new ControlPill();
            Pill.SetBounds(238, 9, 118, 42);

            Windows = new WindowButtons();
            Windows.SetBounds(1006, 17, 74, 26);

            Controls.AddRange(new Control[] { Brand, Pill, Windows });
        }

        public void SetMicMuted(bool muted) { Pill.SetMicMuted(muted); }
        public void SetSpeakerMuted(bool muted) { Pill.SetSpeakerMuted(muted); }
        public void SetHangupEnabled(bool on) { Pill.SetHangupEnabled(on); }

        // ---------------- 拖动 ----------------
        private void OnDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Form f = FindForm();
            if (f == null) return;
            _dragging = true;
            Point screen = PointToScreen(e.Location);
            _dragOffset = new Point(screen.X - f.Location.X, screen.Y - f.Location.Y);
            Capture = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Form f = FindForm();
            if (f == null) return;
            Point screen = PointToScreen(e.Location);
            f.Location = new Point(screen.X - _dragOffset.X, screen.Y - _dragOffset.Y);
        }

        private void OnUp(object sender, MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
        }
    }
}
