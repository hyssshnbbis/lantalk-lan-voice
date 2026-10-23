// Theme.cs —— 深色主题与自绘控件
//
// 设计参考了 oopz 那种"左侧图标栏 + 侧边栏 + 主区"的深色布局：
// 近黑背景、圆角卡片、扁平按钮、克制的强调色。
//
// 两个必须守住的技术约束：
//  1) 自定义按钮必须继承 WinForms 的 Button —— 这样窗口类仍然是 "BUTTON"，
//     界面冒烟测试靠 Win32 FindWindow 找 "BUTTON" 类 + 文本定位「接听」「挂断」才不会失效。
//  2) 所有控件都是自绘（OnPaint），所以要打开 UserPaint / AllPaintingInWmPaint /
//     OptimizedDoubleBuffer，否则会闪烁。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LanTalk
{
    /// <summary>全局配色与绘制工具。</summary>
    internal static class Theme
    {
        // 背景层次（越往里越亮一点，制造层次感）
        public static readonly Color RailBg = Color.FromArgb(19, 19, 19);
        public static readonly Color SidebarBg = Color.FromArgb(25, 25, 25);
        public static readonly Color MainBg = Color.FromArgb(30, 30, 30);
        public static readonly Color CardBg = Color.FromArgb(36, 36, 36);
        public static readonly Color CardBgHover = Color.FromArgb(42, 42, 42);
        public static readonly Color CardBorder = Color.FromArgb(46, 46, 46);

        // 输入控件
        public static readonly Color FieldBg = Color.FromArgb(27, 27, 27);
        public static readonly Color FieldBorder = Color.FromArgb(54, 54, 54);

        // 文字
        public static readonly Color TextPrimary = Color.FromArgb(230, 230, 230);
        public static readonly Color TextSecondary = Color.FromArgb(150, 150, 150);
        public static readonly Color TextMuted = Color.FromArgb(110, 110, 110);

        // 语义色
        public static readonly Color Accent = Color.FromArgb(61, 220, 132);      // 主操作 / 通话中
        public static readonly Color AccentDark = Color.FromArgb(34, 160, 92);
        public static readonly Color Danger = Color.FromArgb(229, 72, 77);       // 挂断
        public static readonly Color DangerDark = Color.FromArgb(180, 50, 55);
        public static readonly Color Warn = Color.FromArgb(245, 166, 35);        // 来电
        public static readonly Color Info = Color.FromArgb(90, 150, 240);        // 呼叫中

        // 字体
        public static Font FontUi;
        public static Font FontTitle;
        public static Font FontBrand;
        public static Font FontLogo;
        public static Font FontBig;
        public static Font FontSmall;
        public static Font FontMono;

        public static void InitFonts()
        {
            if (FontUi != null) return;
            FontUi = new Font("Microsoft YaHei UI", 9F);
            FontTitle = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
            FontBrand = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold);
            FontLogo = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold);
            FontBig = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold);
            FontSmall = new Font("Microsoft YaHei UI", 8F);
            FontMono = new Font("Consolas", 8.5F);
        }

        /// <summary>圆角矩形路径。radius 会被自动夹到不超过矩形短边的一半。</summary>
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0 || r.Width <= 2 || r.Height <= 2)
            {
                p.AddRectangle(r);
                return p;
            }
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d < 2) d = 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>按当前 DPI 算出合适的圆角半径，避免高 DPI 下圆角看起来过小。</summary>
        public static int Radius(Control c, int logical)
        {
            if (c == null) return logical;
            using (Graphics g = c.CreateGraphics()) return (int)Math.Round(logical * (g.DpiX / 96f));
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }
    }

    /// <summary>
    /// 圆角深色卡片，替代原来的 GroupBox。标题画在卡片内左上角，
    /// 因为 GroupBox 在深色主题下边框和标题很难看。
    /// </summary>
    internal class CardPanel : Panel
    {
        public string Title = "";
        /// <summary>标题行右端的文字（版本号、人数这类），为空就不画。</summary>
        public string CornerText = "";
        public int RadiusLogical = 8;
        public Color BorderColor = Theme.CardBorder;

        private Color _background = Theme.CardBg;

        /// <summary>
        /// 卡片底色。同时写回 BackColor —— 因为子控件（输入框、按钮）
        /// 需要用 Parent.BackColor 铺一层底色来遮掉自己圆角外侧的残留，
        /// 如果这里不同步，它们就会用错颜色，把圆角和边框糊掉。
        /// </summary>
        public Color Background
        {
            get { return _background; }
            set
            {
                _background = value;
                BackColor = value;
                Invalidate();
            }
        }

        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = _background;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 内缩 1px：1px 的笔画居中在路径上，画到边界会被裁掉外侧那半个像素
            Rectangle r = new Rectangle(0, 0, Math.Max(2, Width - 2), Math.Max(2, Height - 2));
            int rad = Theme.Radius(this, RadiusLogical);

            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(Background)) g.FillPath(b, p);
                using (Pen pen = new Pen(BorderColor, 1f)) g.DrawPath(pen, p);
            }

            if (!string.IsNullOrEmpty(Title))
            {
                Theme.InitFonts();
                using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    int pad = Theme.Radius(this, 14);
                    g.DrawString(Title, Theme.FontSmall, b, new RectangleF(pad, Theme.Radius(this, 8), Width - pad * 2, Theme.Radius(this, 18)), sf);
                }
            }

            // 右端文字自己画在卡片上：用 Label 贴在卡片上会因为父容器底色不同而露出一块深色矩形
            if (!string.IsNullOrEmpty(CornerText))
            {
                Theme.InitFonts();
                using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Alignment = StringAlignment.Far;
                    int pad = Theme.Radius(this, 14);
                    g.DrawString(CornerText, Theme.FontSmall, b, new RectangleF(pad, Theme.Radius(this, 8), Width - pad * 2, Theme.Radius(this, 18)), sf);
                }
            }
        }
    }

    /// <summary>
    /// 扁平圆角按钮。继承 Button 是为了让窗口类仍是 "BUTTON"——
    /// 界面冒烟测试靠它来定位「接听」「挂断」。
    /// </summary>
    internal sealed class FlatButton : Button
    {
        public enum Look { Normal, Primary, Danger, Ghost, Rail }

        private readonly Look _look;
        private bool _hover, _press, _selected;

        /// <summary>选中态（用于标签页切换这类"当前项"提示）。</summary>
        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        public FlatButton(Look look)
        {
            _look = look;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Theme.InitFonts();
            Font = Theme.FontUi;
            ForeColor = Theme.TextPrimary;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _press = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        private void PickColors(out Color fill, out Color text, out Color border)
        {
            bool on = Enabled;
            switch (_look)
            {
                case Look.Primary:
                    fill = _press ? Theme.AccentDark : Theme.Blend(Theme.Accent, Color.Black, _hover ? 0.12f : 0f);
                    text = Color.FromArgb(12, 20, 14);
                    border = Color.Transparent;
                    break;
                case Look.Danger:
                    fill = _press ? Theme.DangerDark : Theme.Blend(Theme.Danger, Color.Black, _hover ? 0.12f : 0f);
                    text = Color.White;
                    border = Color.Transparent;
                    break;
                case Look.Ghost:
                    if (_selected) { fill = Color.FromArgb(54, 54, 54); text = Theme.TextPrimary; }
                    else { fill = _press ? Color.FromArgb(48, 48, 48) : (_hover ? Color.FromArgb(40, 40, 40) : Color.Transparent); text = _selected ? Theme.TextPrimary : Theme.TextSecondary; }
                    border = _selected ? Theme.Accent : Color.FromArgb(56, 56, 56);
                    break;
                case Look.Rail:
                    fill = _press ? Color.FromArgb(56, 56, 56) : (_hover ? Color.FromArgb(44, 44, 44) : Color.Transparent);
                    text = Theme.TextSecondary;
                    border = Color.Transparent;
                    break;
                default:
                    fill = _press ? Color.FromArgb(56, 56, 56) : (_hover ? Theme.CardBgHover : Color.FromArgb(46, 46, 46));
                    text = Theme.TextPrimary;
                    border = Color.FromArgb(60, 60, 60);
                    break;
            }
            if (!on)
            {
                fill = Color.FromArgb(40, 40, 40);
                text = Color.FromArgb(120, 120, 120);   // 禁用也要看得清
                border = Color.FromArgb(50, 50, 50);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill, text, border;
            PickColors(out fill, out text, out border);

            // 父容器可能是透明背景，先铺一层与父同色的底，避免边缘出现黑角
            Control parent = Parent;
            Color baseColor = parent != null ? parent.BackColor : Theme.CardBg;
            using (SolidBrush bb = new SolidBrush(baseColor)) g.FillRectangle(bb, ClientRectangle);

            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
            int rad = Theme.Radius(this, _look == Look.Rail ? 12 : 6);
            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                if (fill != Color.Transparent)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                }
                if (border != Color.Transparent)
                {
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
                }
            }

            using (SolidBrush tb = new SolidBrush(text))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(Text, Font, tb, new RectangleF(2, 0, Width - 4, Height), sf);
            }

            if (Focused && ShowFocusCues)
            {
                using (Pen pen = new Pen(Theme.Accent, 1f))
                {
                    Rectangle fr = new Rectangle(2, 2, Width - 5, Height - 5);
                    using (GraphicsPath fp = Theme.RoundRect(fr, rad)) g.DrawPath(pen, fp);
                }
            }
        }
    }

    /// <summary>
    /// 选项卡片式单选：圆角行 + 圆形指示 + 标题 + 副标题，选中时整行高亮。
    /// 比系统 RadioButton 更适合深色界面，也更能把"这个选项是什么意思"讲清楚。
    /// 分组由外部负责（两个一起用 LinkOptions 连起来即可）。
    /// </summary>
    internal sealed class OptionRow : Control
    {
        public string Title = "";
        public string Subtitle = "";

        private bool _checked;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public OptionRow()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            Theme.InitFonts();
            Font = Theme.FontUi;
            BackColor = Theme.CardBg;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>不触发事件地改选中状态（给另一个选项互斥用，避免递归）。</summary>
        public void SetCheckedSilently(bool v)
        {
            if (_checked == v) return;
            _checked = v;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e)
        {
            if (!Checked) Checked = true;
            base.OnClick(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                if (!Checked) Checked = true;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color bg;
            if (_checked) bg = Color.FromArgb(46, 56, 48);
            else if (_hover) bg = Color.FromArgb(44, 44, 44);
            else bg = Color.FromArgb(38, 38, 38);
            Color border = _checked ? Theme.Accent : Color.FromArgb(54, 54, 54);

            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
            int rad = Theme.Radius(this, 8);
            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                using (Pen pen = new Pen(border, 1.2f)) g.DrawPath(pen, p);
            }

            // 圆形指示器
            int d = Theme.Radius(this, 16);
            int ix = Theme.Radius(this, 12);
            int iy = (Height - d) / 2;
            using (Pen pen = new Pen(_checked ? Theme.Accent : Color.FromArgb(96, 96, 96), 1.6f))
                g.DrawEllipse(pen, new Rectangle(ix, iy, d, d));
            if (_checked)
            {
                int dot = d / 2;
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, new Rectangle(ix + (d - dot) / 2, iy + (d - dot) / 2, dot, dot));
            }

            int tx = ix + d + Theme.Radius(this, 10);
            int tw = Width - tx - Theme.Radius(this, 8);
            if (tw < Theme.Radius(this, 20)) return;

            using (StringFormat sf = new StringFormat())
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;

                if (string.IsNullOrEmpty(Subtitle))
                {
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush b = new SolidBrush(_checked ? Theme.TextPrimary : Theme.TextSecondary))
                        g.DrawString(Title, Theme.FontUi, b, new RectangleF(tx, 0, tw, Height), sf);
                }
                else
                {
                    int th = Theme.Radius(this, 17);
                    int sh = Theme.Radius(this, 15);
                    int top = (Height - th - sh) / 2;
                    using (SolidBrush b = new SolidBrush(_checked ? Theme.TextPrimary : Color.FromArgb(205, 205, 205)))
                        g.DrawString(Title, Theme.FontUi, b, new RectangleF(tx, top, tw, th), sf);
                    using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                        g.DrawString(Subtitle, Theme.FontSmall, b, new RectangleF(tx, top + th, tw, sh), sf);
                }
            }

            if (Focused && ShowFocusCues)
            {
                using (Pen pen = new Pen(Theme.Accent, 1f))
                using (GraphicsPath fp = Theme.RoundRect(new Rectangle(3, 3, Width - 7, Height - 7), rad))
                    g.DrawPath(pen, fp);
            }
        }
    }

    /// <summary>
    /// 黑底圆角容器：日志框、成员列表那种"内容的凹槽"用。
    /// 子控件（TextBox / ListView）本身是方的，所以给这个容器设 Padding，
    /// 让子控件内缩，圆角才露得出来；子控件底色与容器一致，接缝就看不见。
    /// </summary>
    internal sealed class RoundedSurface : Panel
    {
        public int RadiusLogical = 8;
        public Color Surface = Color.FromArgb(18, 18, 18);

        public RoundedSurface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // 先铺父容器底色，盖掉自己圆角外侧的残留
            Color outer = Parent != null ? Parent.BackColor : Theme.CardBg;
            using (SolidBrush b = new SolidBrush(outer)) g.FillRectangle(b, ClientRectangle);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
            using (GraphicsPath p = Theme.RoundRect(r, Theme.Radius(this, RadiusLogical)))
            using (SolidBrush b = new SolidBrush(Surface))
                g.FillPath(b, p);
        }
    }

    /// <summary>
    /// 深色 ListView：系统 ListView 的表头是浅底深字，改不掉（SetWindowTheme 对它无效），
    /// 只能整份自绘——表头、行、子项、选中态全部接管。
    /// 行的前景色仍然沿用 Item.ForeColor，所以"正在说话"的绿色能保留。
    /// </summary>
    internal class DarkListView : ListView
    {
        protected static readonly Color RowA = Color.FromArgb(18, 18, 18);
        protected static readonly Color RowB = Color.FromArgb(23, 23, 23);
        protected static readonly Color RowSel = Color.FromArgb(46, 58, 48);
        private static readonly Color HeaderBg = Color.FromArgb(38, 38, 38);
        private static readonly Color HeaderLine = Color.FromArgb(58, 58, 58);

        public DarkListView()
        {
            Theme.InitFonts();
            View = View.Details;
            OwnerDraw = true;
            FullRowSelect = true;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            BackColor = RowA;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontUi;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DarkChrome.DarkScrollbars(this);   // 滚动条也走深色，别在深色列表里留一条白杠
            SizeLastColumn();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SizeLastColumn();
        }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            SizeLastColumn();
        }

        /// <summary>
        /// 让最后一列吃掉剩余宽度。
        /// 系统表头控件会在最后一列右边留一块白色空白区，而 OnDrawColumnHeader 只对
        /// 真实列触发，那块白底没人画。把宽度补满，空白区就不存在了。
        /// </summary>
        private void SizeLastColumn()
        {
            if (Columns.Count == 0) return;
            int fixedW = 0;
            for (int i = 0; i < Columns.Count - 1; i++) fixedW += Columns[i].Width;

            int avail = ClientSize.Width - 2;
            if (avail <= 0) return;

            int last = avail - fixedW;
            if (last < 40) last = 40;
            if (Columns[Columns.Count - 1].Width != last) Columns[Columns.Count - 1].Width = last;
        }

        protected static StringFormat Cell()
        {
            StringFormat sf = new StringFormat();
            sf.LineAlignment = StringAlignment.Center;
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            return sf;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(HeaderBg)) g.FillRectangle(b, e.Bounds);

            using (Pen pen = new Pen(HeaderLine))
            {
                g.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                if (e.ColumnIndex > 0)
                    g.DrawLine(pen, e.Bounds.Left, e.Bounds.Top + 4, e.Bounds.Left, e.Bounds.Bottom - 5);
            }

            using (StringFormat sf = Cell())
            using (SolidBrush b = new SolidBrush(Theme.TextSecondary))
            {
                Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(4, e.Bounds.Width - 10), e.Bounds.Height);
                g.DrawString(e.Header.Text, Theme.FontUi, b, r, sf);
            }

            // 保险：每次画表头顺手校正最后一列宽度。
            // 系统表头在最后一列右侧会留一块白底，只有把列撑满才盖得住。
            SizeLastColumn();
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            // 整行铺底，避免最后一列右侧露出默认背景
            Color bg = e.Item.Selected ? RowSel : (e.ItemIndex % 2 == 0 ? RowA : RowB);
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            e.DrawDefault = false;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            Color bg = e.Item.Selected ? RowSel : (e.ItemIndex % 2 == 0 ? RowA : RowB);
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            using (StringFormat sf = Cell())
            using (SolidBrush b = new SolidBrush(e.Item.ForeColor))
            {
                Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, Math.Max(4, e.Bounds.Width - 10), e.Bounds.Height);
                e.Graphics.DrawString(e.SubItem.Text, Font, b, r, sf);
            }
        }
    }

    /// <summary>
    /// 圆角输入框：外圈自绘圆角深色底 + 内边距，里面塞一个无边框 TextBox。
    /// TextBox 自己是方的、边框色也改不了，所以必须这样包一层。
    /// 注意：底色一律用深色，绝不用白色。
    /// </summary>
    // 注意基类是 Panel 而不是 Control：
    // 裸 Control 的默认布局引擎不认 Padding，Dock.Fill 的子控件会占满整个控件，
    // 圆角边框就全被盖住了。Panel（ScrollableControl）才真正支持盒子模型的内边距。
    internal sealed class DarkInput : Panel
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        private const int EM_SETCUEBANNER = 0x1501;

        private readonly TextBox _box;
        private bool _focus;
        private string _placeholder = "";

        /// <summary>
        /// 空框里的灰色提示文字。用 EM_SETCUEBANNER 让系统自己画，
        /// 这样 Text 仍然是空字符串——不会污染 int.TryParse 之类的解析。
        /// </summary>
        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                _placeholder = value == null ? "" : value;
                ApplyPlaceholder();
            }
        }

        private void ApplyPlaceholder()
        {
            if (_box == null || _box.Handle == IntPtr.Zero) return;
            try { SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, _placeholder); } catch { }
        }

        public DarkInput()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.InitFonts();
            BackColor = Theme.FieldBg;

            _box = new TextBox();
            _box.AutoSize = false;          // 允许由 Dock 决定高度（单行 EDIT 内部垂直居中）
            _box.Dock = DockStyle.Fill;
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Theme.FieldBg;
            _box.ForeColor = Theme.TextPrimary;
            _box.Font = Theme.FontUi;
            _box.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            _box.GotFocus += delegate { _focus = true; Invalidate(); };
            _box.LostFocus += delegate { _focus = false; Invalidate(); OnLeave(EventArgs.Empty); };
            _box.HandleCreated += delegate { ApplyPlaceholder(); ApplyBoxModel(); };
            Controls.Add(_box);
        }

        public TextBox Box { get { return _box; } }

        public override string Text
        {
            get { return _box.Text; }
            set { _box.Text = value; }
        }

        public HorizontalAlignment TextAlign
        {
            get { return _box.TextAlign; }
            set { _box.TextAlign = value; }
        }

        public int MaxLength
        {
            get { return _box.MaxLength; }
            set { _box.MaxLength = value; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyBoxModel();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyBoxModel();
        }

        /// <summary>
        /// 用盒子模型摆放内层文本框：Padding 是"内容区的内边距"，子控件 Dock=Fill
        /// 就会自动填满内容区，位置和尺寸全由布局引擎算——不再手动 SetBounds。
        ///
        /// 垂直内边距取 (框高 − 文字所需高度)/2，于是内容区高度恰好等于一行文字，
        /// 文字自然上下居中；水平方面 TextAlign=Center 让它左右居中。
        /// </summary>
        private void ApplyBoxModel()
        {
            if (_box == null) return;

            int padH = Theme.Radius(this, 10);
            int textH = _box.PreferredHeight;
            if (textH < 8) textH = Theme.Radius(this, 16);

            int padV = (Height - textH) / 2;
            if (padV < 0) padV = 0;

            // 先设 Padding 再 Dock.Fill：布局引擎会按 Padding 算出内容区
            Padding = new Padding(padH, padV, padH, padV);
            if (_box.Dock != DockStyle.Fill) _box.Dock = DockStyle.Fill;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            try { _box.Focus(); } catch { }
            base.OnMouseDown(e);
        }

        /// <summary>点在外框的任何位置都应该把焦点给内层文本框。</summary>
        protected override void OnClick(EventArgs e)
        {
            try
            {
                _box.Focus();
                _box.SelectionStart = _box.TextLength;
            }
            catch { }
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color baseColor = Parent != null ? Parent.BackColor : Theme.CardBg;
            using (SolidBrush bb = new SolidBrush(baseColor)) g.FillRectangle(bb, ClientRectangle);

            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
            int rad = Theme.Radius(this, 7);
            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(Theme.FieldBg)) g.FillPath(b, p);
                using (Pen pen = new Pen(_focus ? Theme.Accent : Color.FromArgb(74, 74, 74), 1.2f))
                    g.DrawPath(pen, p);
            }
        }
    }

    /// <summary>
    /// 圆角卡片式下拉：外观和输入框一致（圆角深色底 + 右侧 V 形），
    /// 点击弹出深色列表选择。不用系统 ComboBox，因为它的边框和下拉箭头无法做成深色。
    /// </summary>
    internal sealed class DarkSelect : Control
    {
        public readonly List<object> Items = new List<object>();
        public string Placeholder = "请选择";

        private int _index = -1;
        private bool _hover;

        public event EventHandler SelectedIndexChanged;

        public DarkSelect()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.InitFonts();
            BackColor = Theme.FieldBg;
            Font = Theme.FontUi;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int v = value;
                if (v < -1) v = -1;
                if (v >= Items.Count) v = Items.Count - 1;
                if (v == _index) return;
                _index = v;
                Invalidate();
                EventHandler h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return (_index >= 0 && _index < Items.Count) ? Items[_index] : null; }
        }

        private string DisplayText()
        {
            if (_index < 0 || _index >= Items.Count) return "";
            object o = Items[_index];
            return o == null ? "" : o.ToString();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            ShowList();
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && Items.Count > 0) { SelectedIndex = Math.Min(Items.Count - 1, _index + 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up && Items.Count > 0) { SelectedIndex = Math.Max(0, _index - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { ShowList(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        private void ShowList()
        {
            if (Items.Count == 0) return;
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
            menu.BackColor = Color.FromArgb(34, 34, 34);
            menu.ForeColor = Theme.TextPrimary;
            menu.Font = Theme.FontUi;
            menu.ShowImageMargin = false;
            menu.Padding = new Padding(0, 4, 0, 4);

            for (int i = 0; i < Items.Count; i++)
            {
                int idx = i;
                object o = Items[i];
                string text = o == null ? "" : o.ToString();
                ToolStripMenuItem mi = new ToolStripMenuItem(text);
                mi.ForeColor = Theme.TextPrimary;
                mi.BackColor = Color.FromArgb(34, 34, 34);
                mi.Padding = new Padding(6, 3, 10, 3);
                if (i == _index) mi.Font = new Font(Theme.FontUi, FontStyle.Bold);
                mi.Click += delegate { SelectedIndex = idx; };
                menu.Items.Add(mi);
            }

            try { menu.Show(this, new Point(0, Height + 2)); }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color baseColor = Parent != null ? Parent.BackColor : Theme.CardBg;
            using (SolidBrush bb = new SolidBrush(baseColor)) g.FillRectangle(bb, ClientRectangle);

            Rectangle r = new Rectangle(1, 1, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
            int rad = Theme.Radius(this, 7);
            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(_hover ? Color.FromArgb(32, 32, 32) : Theme.FieldBg)) g.FillPath(b, p);
                using (Pen pen = new Pen(_hover ? Theme.Accent : Color.FromArgb(74, 74, 74), 1.2f))
                    g.DrawPath(pen, p);
            }

            int pad = Theme.Radius(this, 10);
            int chevW = Theme.Radius(this, 22);
            string text = DisplayText();
            bool isPlaceholder = string.IsNullOrEmpty(text);
            if (isPlaceholder) text = Placeholder;

            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                using (SolidBrush b = new SolidBrush(isPlaceholder ? Theme.TextMuted : Theme.TextPrimary))
                    g.DrawString(text, Font, b, new RectangleF(pad, 0, Width - pad - chevW, Height), sf);
            }

            // 右侧 V 形
            int cx = Width - Theme.Radius(this, 15);
            int cy = Height / 2;
            int aw = Theme.Radius(this, 4);
            using (Pen pen = new Pen(Theme.TextSecondary, 1.6f))
            {
                g.DrawLine(pen, cx - aw, cy - 2, cx, cy + 2);
                g.DrawLine(pen, cx, cy + 2, cx + aw, cy - 2);
            }
        }
    }

    /// <summary>
    /// 自绘滑块：细圆角轨道 + 小圆点手柄。
    /// 系统 TrackBar 在深色主题下是浅色轨道配绿色三角手柄、还带一li排刻度，很割裂，
    /// 又没法只改颜色，所以整个自绘。
    /// </summary>
    internal sealed class DarkSlider : Control
    {
        private int _min;
        private int _max = 100;
        private int _value;
        private bool _dragging;
        private bool _hover;

        public event EventHandler ValueChanged;

        public DarkSlider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.InitFonts();
            BackColor = Theme.CardBg;
            Height = 28;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public int Minimum
        {
            get { return _min; }
            set { _min = value; if (_max < _min) _max = _min; ClampValue(); Invalidate(); }
        }

        public int Maximum
        {
            get { return _max; }
            set { _max = value; if (_max < _min) _min = _max; ClampValue(); Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < _min) v = _min;
                if (v > _max) v = _max;
                if (v == _value) return;
                _value = v;
                Invalidate();
                EventHandler h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        private void ClampValue()
        {
            if (_value < _min) _value = _min;
            if (_value > _max) _value = _max;
        }

        private int Pad { get { return Theme.Radius(this, 9); } }
        private int TrackHeight { get { return Math.Max(4, Theme.Radius(this, 5)); } }
        private int ThumbD { get { return Math.Max(10, Theme.Radius(this, 14)); } }

        private int ThumbCenterX()
        {
            int left = Pad;
            int right = Width - Pad - 1;
            int span = right - left;
            if (span <= 0) return left;
            int range = _max - _min;
            if (range <= 0) return left;
            return left + (int)Math.Round(span * ((_value - _min) / (double)range));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                Capture = true;
                SetFromX(e.X);
            }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) SetFromX(e.X);
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = Math.Max(1, (_max - _min) / 20);
            if (e.KeyCode == Keys.Left) { Value = _value - step; e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { Value = _value + step; e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { Value = _min; e.Handled = true; }
            else if (e.KeyCode == Keys.End) { Value = _max; e.Handled = true; }
            base.OnKeyDown(e);
        }

        private void SetFromX(int x)
        {
            int left = Pad;
            int right = Width - Pad - 1;
            int span = right - left;
            if (span <= 0) return;
            double t = (x - left) / (double)span;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            Value = _min + (int)Math.Round(t * (_max - _min));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int cy = Height / 2;
            int th = TrackHeight;
            int left = Pad;
            int right = Width - Pad - 1;
            int span = right - left;
            if (span < 2) return;

            // 底轨
            Rectangle track = new Rectangle(left, cy - th / 2, span, th);
            int tr = th / 2;
            using (GraphicsPath p = Theme.RoundRect(track, tr))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(58, 58, 58)))
                g.FillPath(b, p);

            // 已填充部分
            int cx = ThumbCenterX();
            int fillW = cx - left;
            if (fillW > 1)
            {
                Rectangle filled = new Rectangle(left, cy - th / 2, fillW, th);
                using (GraphicsPath p = Theme.RoundRect(filled, tr))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, p);
            }

            // 手柄：小圆，悬停时略微放大并加深描边
            int d = ThumbD + (_hover || _dragging ? 2 : 0);
            Rectangle thumb = new Rectangle(cx - d / 2, cy - d / 2, d, d);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(236, 240, 238))) g.FillEllipse(b, thumb);
            using (Pen pen = new Pen(Theme.Accent, 2f)) g.DrawEllipse(pen, thumb);

            if (Focused && ShowFocusCues)
            {
                using (Pen pen = new Pen(Color.FromArgb(80, Theme.Accent), 1f))
                    g.DrawEllipse(pen, new Rectangle(thumb.X - 3, thumb.Y - 3, thumb.Width + 6, thumb.Height + 6));
            }
        }
    }

    /// <summary>圆角电平条，替代 ProgressBar（后者在深色主题下是浅色块，很突兀）。</summary>
    internal sealed class MeterBar : Control
    {
        private int _value;
        public Color BarColor = Theme.Accent;

        public MeterBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CardBg;
        }

        /// <summary>0~100。</summary>
        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                if (v == _value) return;
                _value = v;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle r = new Rectangle(0, 0, Math.Max(2, Width - 2), Math.Max(2, Height - 2));
            int rad = Theme.Radius(this, Height / 2);
            using (GraphicsPath p = Theme.RoundRect(r, rad))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(44, 44, 44))) g.FillPath(b, p);
            }

            if (_value > 0)
            {
                int w = (int)Math.Round((Width - 1) * (_value / 100.0));
                if (w < Height) w = Math.Min(Height, Width - 1);   // 太小的时候至少画成一个小圆头
                Rectangle fr = new Rectangle(0, 0, Math.Max(w, rad), Height - 1);
                int frad = Theme.Radius(this, (Height - 1) / 2);
                using (GraphicsPath fp = Theme.RoundRect(fr, frad))
                using (SolidBrush b = new SolidBrush(BarColor)) g.FillPath(b, fp);
            }
        }
    }

    /// <summary>深色下拉框：自绘条目，否则弹出列表还是刺眼的浅色。</summary>
    internal sealed class DarkComboBox : ComboBox
    {
        public DarkComboBox()
        {
            Theme.InitFonts();
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.FieldBg;
            ForeColor = Theme.TextPrimary;
            Font = Theme.FontUi;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 22;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0)
            {
                base.OnDrawItem(e);
                return;
            }
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color bg = sel ? Color.FromArgb(56, 56, 56) : Theme.FieldBg;
            using (SolidBrush b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            using (SolidBrush tb = new SolidBrush(Theme.TextPrimary))
            {
                Rectangle r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                using (StringFormat sf = new StringFormat())
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags |= StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    e.Graphics.DrawString(Items[e.Index].ToString(), Font, tb, r, sf);
                }
            }
        }
    }

    /// <summary>
    /// 把系统控件"去视觉样式"（SetWindowTheme 传空格），
    /// 这是让 ComboBox 的箭头区、ListView 的滚动条不再是一片浅色的经典办法。
    /// </summary>
    internal static class DarkChrome
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpRound = 2;

        /// <summary>让系统控件按经典方式绘制，从而吃我们自己设的深色底。</summary>
        public static void StripVisualStyles(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, " ", " "); } catch { }
        }

        /// <summary>Win10 1809+ 才有 DarkMode_Explorer，能让滚动条变深色；老系统忽略即可。</summary>
        public static void DarkScrollbars(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        /// <summary>
        /// 用一块小面板盖住 ComboBox 右侧那个"箭头按钮"。
        /// ComboBox 的下拉箭头是系统画的，颜色不跟 BackColor，深色主题下会露出一个刺眼的浅色方块；
        /// 又没法靠 DrawItem 覆盖它，所以干脆盖住、自己画一个 V 形，点击时手动展开下拉。
        /// </summary>
        public static void CoverComboArrow(Control parent, ComboBox combo)
        {
            if (parent == null || combo == null) return;

            Panel cover = new Panel();
            int w = 24;
            cover.SetBounds(combo.Right - w, combo.Top + 2, w - 2, combo.Height - 4);
            cover.BackColor = Theme.FieldBg;
            cover.Cursor = Cursors.Hand;
            cover.Paint += delegate(object s, PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Panel pp = (Panel)s;
                int cx = pp.Width / 2;
                int cy = pp.Height / 2;
                using (Pen pen = new Pen(Theme.TextSecondary, 1.6f))
                {
                    e.Graphics.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
                    e.Graphics.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
                }
            };
            cover.Click += delegate { try { combo.DroppedDown = true; } catch { } };
            parent.Controls.Add(cover);
            cover.BringToFront();
        }

        /// <summary>
        /// 让窗口本身带圆角（Win11 的 DWM 圆角）。
        /// 无边框窗口默认是直角，和内部的圆角卡片很不搭。老系统会静默失败，退回直角。
        /// </summary>
        public static void RoundWindowCorners(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                int pref = DwmwcpRound;
                DwmSetWindowAttribute(f.Handle, DwmwaWindowCornerPreference, ref pref, 4);
            }
            catch { }
        }

        /// <summary>
        /// Windows 11 才支持自定义标题栏颜色。老系统会返回错误，忽略即可。
        /// 不设的话标题栏会跟随系统强调色，和深色界面撞色很厉害。
        /// </summary>
        public static void DarkTitleBar(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                int caption = ColorTranslator.ToWin32(Theme.RailBg);
                int text = ColorTranslator.ToWin32(Theme.TextPrimary);
                int border = ColorTranslator.ToWin32(Theme.RailBg);
                DwmSetWindowAttribute(f.Handle, DwmwaCaptionColor, ref caption, 4);
                DwmSetWindowAttribute(f.Handle, DwmwaTextColor, ref text, 4);
                DwmSetWindowAttribute(f.Handle, DwmwaBorderColor, ref border, 4);
            }
            catch { }
        }
    }

    /// <summary>统一的输入框外观（TextBox 的边框色改不了，只能用深底 + 浅字）。</summary>
    /// <summary>深色下拉/右键菜单的统一配色，DarkSelect 与聊天消息菜单共用。</summary>
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(34, 34, 34); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(34, 34, 34); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(34, 34, 34); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(34, 34, 34); } }
        public override Color MenuBorder { get { return Color.FromArgb(58, 58, 58); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(58, 58, 58); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(56, 56, 56); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(56, 56, 56); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(56, 56, 56); } }
        public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(56, 56, 56); } }
        public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(56, 56, 56); } }
    }
    /// <summary>
    /// 真正的拨动开关：一条圆角轨道 + 一个圆形滑块。
    ///
    /// 之前用 FlatButton + Selected 表示开关，语义是错的——按钮是"按一下做事"，
    /// 开关是"保持某个状态"。这里做成独立控件，Checked 表示状态，点击翻转。
    /// </summary>
    /// <summary>
    /// 统一的「主状态 + 副标题」两行文字块。
    ///
    /// 之前各页面各自 new Label 再逐个设字体颜色，容易走样；
    /// 这里收成公共控件，样式由 Theme 统一决定，卡片经 CardBase.StatusLine() 取用。
    /// </summary>
    internal sealed class StatusBlock
    {
        public readonly Label Title;
        public readonly Label Sub;

        internal StatusBlock(Control parent, int x, int y, int w)
        {
            Title = new Label();
            Title.Text = "";
            Title.Font = Theme.FontUi;
            Title.ForeColor = Theme.TextPrimary;
            Title.BackColor = Color.Transparent;
            Title.AutoEllipsis = true;
            Title.SetBounds(x, y, w, 20);
            parent.Controls.Add(Title);

            Sub = new Label();
            Sub.Text = "";
            Sub.Font = Theme.FontSmall;
            Sub.ForeColor = Theme.TextMuted;
            Sub.BackColor = Color.Transparent;
            Sub.AutoEllipsis = true;
            Sub.SetBounds(x, y + 20, w, 18);
            parent.Controls.Add(Sub);
        }

        public void Set(string title, string sub, Color? titleColor)
        {
            Title.Text = title ?? "";
            Title.ForeColor = titleColor.HasValue ? titleColor.Value : Theme.TextPrimary;
            Sub.Text = sub ?? "";
        }
    }
    internal sealed class Switch : Control
    {
        private bool _checked;
        private float _pos;          // 滑块位置 0..1，用来做过渡
        private readonly Timer _anim;

        public event EventHandler CheckedChanged;

        public Switch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(46, 26);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            TabStop = true;

            _anim = new Timer();
            _anim.Interval = 15;
            _anim.Tick += delegate
            {
                float target = _checked ? 1f : 0f;
                float d = target - _pos;
                if (Math.Abs(d) < 0.02f) { _pos = target; _anim.Stop(); }
                else _pos += d * 0.35f;
                Invalidate();
            };
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _anim.Start();
                EventHandler h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        /// <summary>不触发事件、不播放动画地设置状态（用于从配置初始化）。</summary>
        public void SetCheckedSilently(bool value)
        {
            _checked = value;
            _pos = value ? 1f : 0f;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) Checked = !_checked;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) Checked = !_checked;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int w = Width, h = Height;
            int trackH = h - 6;
            int radius = trackH / 2;

            Color off = Color.FromArgb(58, 58, 58);
            Color on = Theme.Accent;
            int r = (int)(off.R + (on.R - off.R) * _pos);
            int gg = (int)(off.G + (on.G - off.G) * _pos);
            int b = (int)(off.B + (on.B - off.B) * _pos);
            Color trackCol = Color.FromArgb(r, gg, b);

            Rectangle track = new Rectangle(0, 3, w - 1, trackH);
            using (GraphicsPath path = Theme.RoundRect(track, radius))
            using (SolidBrush br = new SolidBrush(trackCol))
                g.FillPath(br, path);

            int knobD = trackH - 6;
            int x0 = 3, x1 = w - 3 - knobD;
            int kx = (int)(x0 + (x1 - x0) * _pos);
            Rectangle knob = new Rectangle(kx, 6, knobD, knobD);
            using (SolidBrush kb = new SolidBrush(Color.FromArgb(245, 245, 245)))
                g.FillEllipse(kb, knob);

            if (Focused)
                using (Pen fp = new Pen(Color.FromArgb(90, Theme.Accent), 1))
                    g.DrawPath(fp, Theme.RoundRect(track, radius));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _anim != null) { _anim.Stop(); _anim.Dispose(); }
            base.Dispose(disposing);
        }
    }
    internal static class FieldStyle
    {
        public static void Apply(TextBox t)
        {
            Theme.InitFonts();
            t.BackColor = Theme.FieldBg;
            t.ForeColor = Theme.TextPrimary;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = Theme.FontUi;
        }

        public static void Apply(ComboBox c)
        {
            Theme.InitFonts();
            c.BackColor = Theme.FieldBg;
            c.ForeColor = Theme.TextPrimary;
            c.FlatStyle = FlatStyle.Flat;
            c.Font = Theme.FontUi;
        }

        public static void Apply(Label l, bool secondary)
        {
            Theme.InitFonts();
            l.ForeColor = secondary ? Theme.TextSecondary : Theme.TextPrimary;
            l.BackColor = Color.Transparent;
            l.Font = Theme.FontUi;
        }

        public static void Apply(CheckBox c)
        {
            Theme.InitFonts();
            c.ForeColor = Theme.TextPrimary;
            c.BackColor = Color.Transparent;
            c.Font = Theme.FontUi;
        }

        public static void Apply(RadioButton r)
        {
            Theme.InitFonts();
            r.ForeColor = Theme.TextPrimary;
            r.BackColor = Color.Transparent;
            r.Font = Theme.FontUi;
        }
    }
}
