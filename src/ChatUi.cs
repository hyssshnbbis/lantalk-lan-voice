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
    }

    internal sealed class ChatFeed : Control
    {
        private readonly List<ChatMsg> _msgs = new List<ChatMsg>();
        private readonly List<int> _tops = new List<int>();     // 每条消息的绘制 y
        private readonly List<int> _heights = new List<int>();  // 每条消息占的高度
        private readonly List<int> _days = new List<int>();     // 是 1 表示这条前面插了日期分隔
        private int _scroll;
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

                    int textH = MeasureText(g, m.Text, ContentWidth);
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

                // 正文（自动换行）
                Rectangle tr = new Rectangle(TextLeft, y + 18, ContentWidth, h - 18);
                TextRenderer.DrawText(g, m.Text, Theme.FontUi, tr, Color.FromArgb(225, 225, 225),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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
