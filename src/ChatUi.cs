// ChatUi.cs —— 聊天消息流（参照 oopz 的正文排版）
//
// 一条消息的排布：
//   [圆头像]  名字   时间
//             正文（自动换行，左边缘和名字对齐）
// 日期变化时插一条居中的分隔线。
//
// 为什么自绘：WinForms 的 TextBox/ListView 都做不出"头像 + 两行 + 换行"的行，
// 而 RichTextBox 的颜色控制又很别扭。自绘反而最简单，而且能精确控制行高与滚动。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace LanTalk
{
    /// <summary>一条聊天消息。</summary>
    internal sealed class ChatMsg
    {
        public DateTime When;
        public string Who = "";
        public string Text = "";
        public bool Self;
        public byte[] Image;      // 非空表示这是一条图片消息（JPEG）
        public Bitmap Thumb;      // 解码后的缩略图（缓存，避免每帧重解）
    }

    /// <summary>把图片字节存到用户选的位置（默认给个带时间戳的文件名）。</summary>
    internal static class ImageSaver
    {
        public static bool Save(IWin32Window owner, byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return false;
            try
            {
                SaveFileDialog dlg = new SaveFileDialog();
                dlg.Title = "保存图片";
                dlg.Filter = "JPEG 图片 (*.jpg)|*.jpg|PNG 图片 (*.png)|*.png|所有文件 (*.*)|*.*";
                dlg.FilterIndex = 1;
                dlg.FileName = "LanTalk-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jpg";
                dlg.AddExtension = true;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;

                string path = dlg.FileName;
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    // 选了 png 就转一次，避免存下来的扩展名和内容不符
                    using (MemoryStream ms = new MemoryStream(jpeg))
                    using (Image img = Image.FromStream(ms))
                        img.Save(path, ImageFormat.Png);
                }
                else
                {
                    File.WriteAllBytes(path, jpeg);
                }
                return true;
            }
            catch { return false; }
        }
    }
    /// <summary>点缩略图弹出的原图查看器：按屏幕大小等比缩放，Esc 或点击关闭。</summary>
    internal static class ImageViewer
    {
        public static void Show(Control owner, byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;
            try
            {
                MemoryStream ms = new MemoryStream(jpeg);
                Image img = Image.FromStream(ms);
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int maxW = (int)(wa.Width * 0.8), maxH = (int)(wa.Height * 0.8);
                double k = Math.Min(1.0, Math.Min((double)maxW / img.Width, (double)maxH / img.Height));
                int w = Math.Max(200, (int)(img.Width * k));
                int h = Math.Max(150, (int)(img.Height * k));

                Form f = new Form();
                f.Text = "图片  " + img.Width + " × " + img.Height;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.BackColor = Color.FromArgb(24, 24, 24);
                int barH = 46;
                f.ClientSize = new Size(w + 16, h + 16 + barH);
                f.KeyPreview = true;
                f.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) f.Close(); };

                PictureBox pb = new PictureBox();
                pb.SizeMode = PictureBoxSizeMode.Zoom;
                pb.Image = img;
                pb.BackColor = Color.FromArgb(18, 18, 18);
                pb.SetBounds(0, 0, w + 16, h + 16);
                pb.Cursor = Cursors.Hand;
                pb.Click += delegate { f.Close(); };
                Panel bar = new Panel();
                bar.SetBounds(0, h + 16, w + 16, barH);
                bar.BackColor = Color.FromArgb(30, 30, 30);

                FlatButton save = new FlatButton(FlatButton.Look.Primary);
                save.Text = "保存图片";
                save.TabStop = false;
                save.SetBounds(Math.Max(12, (w + 16) / 2 - 60), 8, 120, 30);
                save.Click += delegate { ImageSaver.Save(f, jpeg); };

                Label hint = new Label();
                hint.Text = "Esc 或点击图片关闭";
                hint.Font = Theme.FontSmall;
                hint.ForeColor = Theme.TextMuted;
                hint.BackColor = Color.Transparent;
                hint.SetBounds(bar.Controls.Count >= 0 ? 12 : 12, (barH - 16) / 2, 200, 16);

                bar.Controls.Add(save);
                bar.Controls.Add(hint);
                f.Controls.Add(bar);
                f.Controls.Add(pb);

                f.ShowDialog(owner == null ? null : owner.FindForm());
                f.Dispose();
                pb.Dispose();
                img.Dispose();
                ms.Dispose();
            }
            catch { }
        }
    }
    internal sealed class ChatFeed : Control
    {
        private readonly List<ChatMsg> _msgs = new List<ChatMsg>();
        private readonly List<int> _tops = new List<int>();     // 每条消息的绘制 y
        private readonly List<int> _heights = new List<int>();  // 每条消息占的高度
        private readonly List<int> _days = new List<int>();     // 是 1 表示这条前面插了日期分隔
        private int _scroll;
        private readonly List<int> _imgHitIdx = new List<int>();
        private readonly List<Rectangle> _imgHitRect = new List<Rectangle>();
        private bool _atBottom = true;

        private const int AvatarD = 26;
        private const int LeftPad = 12;
        private const int TopPad = 10;
        private const int Gap = 14;
        private const int DateH = 30;

        public ChatFeed()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Theme.InitFonts();
            BackColor = Color.FromArgb(18, 18, 18);
            Font = Theme.FontUi;
        }

        public int Count { get { return _msgs.Count; } }

        public void AddRange(IEnumerable<ChatMsg> list)
        {
            foreach (ChatMsg m in list) _msgs.Add(m);
            Relayout();
            ScrollToBottom();
        }

        public void Add(ChatMsg m)
        {
            if (m != null && m.Image != null && m.Thumb == null) m.Thumb = DecodeThumb(m.Image);
            _msgs.Add(m);
            Relayout();
            if (_atBottom) ScrollToBottom();
            else Invalidate();
        }

        public void ClearAll()
        {
            _msgs.Clear();
            _scroll = 0;
            Relayout();
        }

        /// <summary>把 JPEG 解成缩略图：最长边不超过 240x150，省内存。</summary>
        public static Bitmap DecodeThumb(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return null;
            try
            {
                using (MemoryStream ms = new MemoryStream(jpeg))
                using (Image raw = Image.FromStream(ms))
                {
                    double k = Math.Min(240.0 / raw.Width, 150.0 / raw.Height);
                    if (k > 1.0) k = 1.0;
                    int w = Math.Max(1, (int)(raw.Width * k));
                    int h = Math.Max(1, (int)(raw.Height * k));
                    Bitmap b = new Bitmap(w, h);
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(raw, 0, 0, w, h);
                    }
                    return b;
                }
            }
            catch { return null; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private int ContentWidth
        {
            get { return Math.Max(60, Width - LeftPad * 2 - AvatarD - 10 - 14); }
        }

        private int TextLeft { get { return LeftPad + AvatarD + 10; } }

        /// <summary>重新算每条消息的 y 与高度（宽度变化、字体变化后都要重算）。</summary>
        private void Relayout()
        {
            _tops.Clear();
            _heights.Clear();
            _days.Clear();

            int y = TopPad;
            int lastDay = -1;

            using (Graphics g = CreateGraphics())
            {
                for (int i = 0; i < _msgs.Count; i++)
                {
                    ChatMsg m = _msgs[i];

                    int day = m.When.Date.DayOfYear + m.When.Year * 1000;
                    bool newDay = day != lastDay;
                    lastDay = day;
                    _days.Add(newDay ? 1 : 0);
                    if (newDay) y += DateH;

                    int textH;
                    if (m.Image != null)
                        textH = m.Thumb != null ? m.Thumb.Height : 60;
                    else
                        textH = MeasureText(g, m.Text, ContentWidth);
                    int h = Math.Max(AvatarD, 18 + textH);
                    _tops.Add(y);
                    _heights.Add(h);
                    y += h + Gap;
                }
            }

            ClampScroll();
            Invalidate();
        }

        private static int MeasureText(Graphics g, string text, int width)
        {
            if (string.IsNullOrEmpty(text)) return 18;
            Size s = TextRenderer.MeasureText(g, text, Theme.FontUi,
                new Size(Math.Max(20, width), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return Math.Max(18, s.Height);
        }

        private int ContentHeight
        {
            get
            {
                if (_tops.Count == 0) return TopPad * 2;
                return _tops[_tops.Count - 1] + _heights[_heights.Count - 1] + TopPad;
            }
        }

        private void ClampScroll()
        {
            int max = Math.Max(0, ContentHeight - Height);
            if (_scroll < 0) _scroll = 0;
            if (_scroll > max) _scroll = max;
            _atBottom = _scroll >= max - 2;
        }

        public void ScrollToBottom()
        {
            _scroll = Math.Max(0, ContentHeight - Height);
            _atBottom = true;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Right) return;

            for (int i = _imgHitRect.Count - 1; i >= 0; i--)
            {
                if (!_imgHitRect[i].Contains(e.Location)) continue;
                int idx = _imgHitIdx[i];
                if (idx < 0 || idx >= _msgs.Count || _msgs[idx].Image == null) return;
                byte[] data = _msgs[idx].Image;

                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
                menu.BackColor = Color.FromArgb(34, 34, 34);
                menu.ForeColor = Theme.TextPrimary;
                menu.Font = Theme.FontUi;
                menu.ShowImageMargin = false;
                menu.Padding = new Padding(0, 4, 0, 4);

                ToolStripMenuItem miView = new ToolStripMenuItem("查看原图");
                miView.ForeColor = Theme.TextPrimary;
                miView.Padding = new Padding(10, 3, 14, 3);
                miView.Click += delegate { ImageViewer.Show(this, data); };

                ToolStripMenuItem miSave = new ToolStripMenuItem("保存图片…");
                miSave.ForeColor = Theme.TextPrimary;
                miSave.Padding = new Padding(10, 3, 14, 3);
                miSave.Click += delegate { ImageSaver.Save(FindForm(), data); };

                menu.Items.Add(miView);
                menu.Items.Add(miSave);
                menu.Show(this, e.Location);
                return;
            }
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = _imgHitRect.Count - 1; i >= 0; i--)
            {
                if (!_imgHitRect[i].Contains(e.Location)) continue;
                int idx = _imgHitIdx[i];
                if (idx >= 0 && idx < _msgs.Count && _msgs[idx].Image != null)
                    ImageViewer.Show(this, _msgs[idx].Image);
                return;
            }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _scroll -= e.Delta / 120 * 60;
            ClampScroll();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            using (SolidBrush b = new SolidBrush(Parent != null ? Parent.BackColor : BackColor))
                g.FillRectangle(b, ClientRectangle);

            // 黑底（圆角由外面的 RoundedSurface 负责，这里只铺满）
            using (SolidBrush b = new SolidBrush(Color.FromArgb(18, 18, 18)))
                g.FillRectangle(b, ClientRectangle);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            _imgHitIdx.Clear();
            _imgHitRect.Clear();

            // 空状态：告诉用户这里会有东西
            if (_msgs.Count == 0)
            {
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                        g.DrawString("还没有消息。接通通话或进入会议室后就能聊了。",
                                     Theme.FontUi, b, new RectangleF(12, 12, Width - 24, Height - 24), sf);
                }
                return;
            }

            for (int i = 0; i < _msgs.Count; i++)
            {
                ChatMsg m = _msgs[i];
                int y = _tops[i] - _scroll;
                int h = _heights[i];
                if (y + h < 0) continue;
                if (y > Height) break;

                if (_days[i] == 1)
                {
                    DrawDate(g, m.When, y - DateH + DateH / 2);
                }

                DrawAvatar(g, m, LeftPad, y);

                // 名字 + 时间
                using (SolidBrush b = new SolidBrush(m.Self ? Theme.Accent : Color.FromArgb(200, 205, 210)))
                    g.DrawString(m.Self ? "我" : m.Who, Theme.FontUi, b, TextLeft, y);
                using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                    g.DrawString(m.When.ToString("HH:mm"), Theme.FontSmall, b,
                        TextLeft + MeasureNameWidth(g, m.Self ? "我" : m.Who) + 8, y + 2);

                if (m.Image != null)
                {
                    // 图片消息：画缩略图 + 一圈细边
                    if (m.Thumb != null)
                    {
                        int tw = m.Thumb.Width, th = m.Thumb.Height;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(m.Thumb, TextLeft, y + 18, tw, th);
                        using (Pen pen = new Pen(Color.FromArgb(60, 60, 60)))
                            g.DrawRectangle(pen, TextLeft, y + 18, tw, th);
                        _imgHitIdx.Add(i);
                        _imgHitRect.Add(new Rectangle(TextLeft, y + 18, tw, th));
                    }
                    else
                    {
                        using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                            g.DrawString("（图片解码失败）", Theme.FontUi, b, TextLeft, y + 18);
                    }
                }
                else
                {
                    // 正文（自动换行）
                    Rectangle tr = new Rectangle(TextLeft, y + 18, ContentWidth, h - 18);
                    TextRenderer.DrawText(g, m.Text, Theme.FontUi, tr, Color.FromArgb(225, 225, 225),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
            }

            DrawScrollbar(g);
        }

        private int MeasureNameWidth(Graphics g, string name)
        {
            return TextRenderer.MeasureText(g, name, Theme.FontUi, new Size(400, 40),
                TextFormatFlags.NoPadding).Width;
        }

        private void DrawDate(Graphics g, DateTime when, int cy)
        {
            string t = when.ToString("yyyy年MM月dd日");
            Size s = TextRenderer.MeasureText(g, t, Theme.FontSmall);
            int cx = Width / 2;
            using (Pen pen = new Pen(Color.FromArgb(48, 48, 48)))
            {
                g.DrawLine(pen, LeftPad, cy, cx - s.Width / 2 - 10, cy);
                g.DrawLine(pen, cx + s.Width / 2 + 10, cy, Width - LeftPad, cy);
            }
            using (SolidBrush b = new SolidBrush(Theme.TextMuted))
                g.DrawString(t, Theme.FontSmall, b, cx - s.Width / 2, cy - s.Height / 2);
        }

        private void DrawAvatar(Graphics g, ChatMsg m, int x, int y)
        {
            string name = string.IsNullOrEmpty(m.Who) ? "?" : m.Who;
            int d = AvatarD;
            using (SolidBrush b = new SolidBrush(AvatarColor(name)))
                g.FillEllipse(b, x, y, d, d);

            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(16, 16, 16)))
                    g.DrawString(name.Substring(0, 1), Theme.FontSmall, b,
                        new RectangleF(x, y, d, d), sf);
            }
        }

        private static Color AvatarColor(string s)
        {
            int h = 0;
            for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
            Color[] palette =
            {
                Color.FromArgb(61, 220, 132), Color.FromArgb(90, 150, 240),
                Color.FromArgb(245, 166, 35), Color.FromArgb(229, 72, 77),
                Color.FromArgb(168, 120, 240), Color.FromArgb(58, 190, 200)
            };
            return palette[Math.Abs(h) % palette.Length];
        }

        /// <summary>内容超出一屏时，右侧画一条细滚动条。</summary>
        private void DrawScrollbar(Graphics g)
        {
            int total = ContentHeight;
            if (total <= Height) return;
            int barH = Math.Max(24, Width == 0 ? 24 : Height * Height / total);
            int maxScroll = total - Height;
            int barY = maxScroll <= 0 ? 0 : (Height - barH) * _scroll / maxScroll;
            int x = Width - 9;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(52, 52, 52)))
                g.FillRectangle(b, new Rectangle(x, 0, 5, Height));
            using (SolidBrush b = new SolidBrush(Color.FromArgb(96, 96, 96)))
                g.FillRectangle(b, new Rectangle(x, barY, 5, barH));
        }
    }
}
