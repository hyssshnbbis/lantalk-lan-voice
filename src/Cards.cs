// Cards.cs —— 界面的模块化板块
//
// 每个板块都是独立的一类：自己持有自己那套控件、自己负责布局，对外只暴露
// 字段（读值）和事件（通知）。MainForm 退化成"编排者"：
//   new XxxCard(...) → 连事件 → 读属性
// 它不再知道任何控件坐标。
//
// 盒子模型：
//   PadH        左右内边距
//   ContentTop  标题区下方的内容起点（卡片标题画在顶部约 8..26）
//   行位置一律写成 ContentTop + 偏移，间距靠常量而不是散落的魔法数。
// 所有尺寸都是 96 DPI 逻辑值，最后统一由 MainForm.ApplyDpiScale 乘系数。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LanTalk
{
    /// <summary>
    /// 大数字缩写。通话久了包数会到几十万、上百万，直接印出来会把侧栏撑破，
    /// 所以按 1000 进位缩成 K / M / G。
    /// </summary>
    internal static class Units
    {
        public static string Count(long n)
        {
            if (n < 0) n = 0;
            if (n < 1000) return n.ToString();
            if (n < 1000000) return (n / 1000.0).ToString("F1") + "K";
            if (n < 1000000000) return (n / 1000000.0).ToString("F1") + "M";
            return (n / 1000000000.0).ToString("F1") + "G";
        }
    }

    /// <summary>所有板块的基类。</summary>
    internal abstract class CardBase : CardPanel
    {
        protected const int PadH = 14;         // 左右内边距
        protected const int ContentTop = 30;   // 内容区起点（标题区下方）
        protected const int CaptionH = 18;     // 小标题高度
        protected const int CaptionGap = 2;    // 标题与控件的间距
        protected const int FieldH = 26;       // 输入框 / 下拉高度
        protected const int RowGap = 6;        // 行间距

        protected CardBase(string title, int x, int y, int w, int h)
        {
            Title = title;
            Background = Theme.CardBg;
            SetBounds(x, y, w, h);
        }

        /// <summary>内容区可用宽度。</summary>
        protected int InnerW { get { return Width - PadH * 2; } }

        /// <summary>第 n 行（标题+控件）的标题 y，行高 = 标题 + 控件 + 行距。</summary>
        protected int RowY(int n)
        {
            return ContentTop + n * (CaptionH + CaptionGap + FieldH + RowGap);
        }

        // ---------------- 控件工厂（统一主题，位置由调用方给） ----------------

        protected Label Caption(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontUi;
            l.ForeColor = Theme.TextSecondary;
            l.BackColor = System.Drawing.Color.Transparent;
            l.SetBounds(x, y, w, CaptionH);
            Controls.Add(l);
            return l;
        }

        protected Label Small(string text, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontSmall;
            l.ForeColor = Theme.TextMuted;
            l.BackColor = System.Drawing.Color.Transparent;
            l.SetBounds(x, y, w, h);
            Controls.Add(l);
            return l;
        }

        protected Label Value(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = Theme.FontUi;
            l.ForeColor = Theme.TextSecondary;
            l.BackColor = System.Drawing.Color.Transparent;
            l.TextAlign = ContentAlignment.MiddleRight;
            l.SetBounds(x, y, w, CaptionH);
            Controls.Add(l);
            return l;
        }

        protected DarkInput Input(string placeholder, int x, int y, int w, bool number)
        {
            DarkInput t = new DarkInput();
            t.Placeholder = placeholder;
            if (number)
            {
                t.TextAlign = HorizontalAlignment.Center;
                t.MaxLength = 5;
            }
            t.SetBounds(x, y, w, FieldH);
            Controls.Add(t);
            return t;
        }

        protected DarkSelect Select(string placeholder, int x, int y, int w)
        {
            DarkSelect s = new DarkSelect();
            if (!string.IsNullOrEmpty(placeholder)) s.Placeholder = placeholder;
            s.SetBounds(x, y, w, FieldH);
            Controls.Add(s);
            return s;
        }

        protected FlatButton Button(string text, int x, int y, int w, int h, FlatButton.Look look)
        {
            FlatButton b = new FlatButton(look);
            b.Text = text;
            b.TabStop = false;
            b.SetBounds(x, y, w, h);
            Controls.Add(b);
            return b;
        }

        protected OptionRow Option(string title, string sub, int x, int y, int w, int h)
        {
            OptionRow o = new OptionRow();
            o.Title = title;
            o.Subtitle = sub;
            o.BackColor = Theme.CardBg;
            o.SetBounds(x, y, w, h);
            Controls.Add(o);
            return o;
        }

        protected CheckBox Check(string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            FieldStyle.Apply(c);
            c.SetBounds(x, y, w, 22);
            Controls.Add(c);
            return c;
        }

        /// <summary>把两个选项连成互斥组。</summary>
        /// <summary>
        /// 一行「开关 + 标题 + 说明」。开关用 Switch 而不是 CheckBox：
        /// 开关表达"持续状态"，CheckBox 表达"勾选内容"，前者更适合这类设置项。
        /// </summary>
        /// <summary>统一的两行状态块，样式与其它卡片一致。</summary>
        protected StatusBlock StatusLine(int x, int y, int w)
        {
            return new StatusBlock(this, x, y, w);
        }
        protected Switch SwitchRow(int x, int y, string title, string desc, int titleW)
        {
            Switch sw = new Switch();
            sw.SetBounds(x, y, 46, 26);
            Controls.Add(sw);

            Label t = new Label();
            t.Text = title;
            t.Font = Theme.FontUi;
            t.ForeColor = Theme.TextPrimary;
            t.BackColor = Color.Transparent;
            t.SetBounds(x + 58, y + 3, titleW, 20);
            Controls.Add(t);

            if (!string.IsNullOrEmpty(desc))
            {
                Label d = new Label();
                d.Text = desc;
                d.Font = Theme.FontSmall;
                d.ForeColor = Theme.TextMuted;
                d.BackColor = Color.Transparent;
                d.SetBounds(x + 58 + titleW + 12, y + 6, Width - (x + 58 + titleW + 12) - PadH, 18);
                Controls.Add(d);
            }
            return sw;
        }
        protected static void LinkOptions(OptionRow a, OptionRow b)
        {
            a.CheckedChanged += delegate { if (a.Checked) b.SetCheckedSilently(false); };
            b.CheckedChanged += delegate { if (b.Checked) a.SetCheckedSilently(false); };
        }
    }

    // ============================ 本机 ============================
    internal sealed class LocalCard : CardBase
    {
        public readonly DarkSelect Ip;
        public readonly DarkInput Port;
        public readonly FlatButton Apply;
        public readonly Label Hint;

        public const int CardW = 254;
        public const int CardH = 160;

        public LocalCard(int x, int y) : base("本机", x, y, CardW, CardH)
        {
            Caption("本机地址", PadH, ContentTop, InnerW);
            Ip = Select("自动（监听全部网卡）", PadH, ContentTop + CaptionH + CaptionGap, InnerW);

            // 端口这一行：标签在上、控件在下，所以输入框必须回到 PadH 左对齐。
            // （之前这里按"行内标签"留了 38px 缩进，标签却在上方，看起来就像没对齐。）
            int py = ContentTop + CaptionH + CaptionGap + FieldH + RowGap;
            Caption("端口", PadH, py, 36);
            int fy = py + CaptionH + CaptionGap;
            Port = Input("", PadH, fy, 64, true);
            Apply = Button("应用并重启监听", PadH + 72, fy, InnerW - 72, FieldH, FlatButton.Look.Normal);

            Hint = Small("", PadH, fy + FieldH + RowGap, InnerW, 22);
        }
    }

    // ========================== 连接模式 ==========================
    // 用和设置页一致的下拉卡片（DarkSelect），外观与其它输入框完全统一。
    // 默认「多人会议室」。
    internal sealed class WayCard : CardBase
    {
        public readonly DarkSelect Mode;
        public readonly Label Hint;

        public const int CardW = 254;
        public const int CardH = 78;

        public const int IndexRoom = 0;   // 默认项
        public const int IndexP2P = 1;

        public WayCard(int x, int y) : base("连接模式", x, y, CardW, CardH)
        {
            Mode = Select(null, PadH, ContentTop, InnerW);
            Mode.Items.Add("多人会议室");
            Mode.Items.Add("一对一通话");
            Mode.SelectedIndex = IndexRoom;   // 默认多人通话
            Mode.Font = Theme.FontUi;
            Mode.Height = 34;

            Hint = null;
        }
    }


    // =========================== 会议室 ===========================
    internal sealed class RoomCard : CardBase
    {
        public readonly DarkInput Port;
        public readonly FlatButton Join;
        public readonly FlatButton LeaveButton;
        public readonly DarkInput ManualIp;
        public readonly DarkInput ManualPort;
        public readonly FlatButton AddManual;
        public readonly Label State;

        public const int CardW = 254;
        public const int CardH = 154;

        public RoomCard(int x, int y) : base("", x, y, CardW, CardH)
        {
            Caption("房间号", PadH, 12, 54);
            int fy = 12 + CaptionH + CaptionGap;
            Port = Input("", PadH, fy, 80, true);
            Join = Button("加入会议", PadH + 86, fy - 1, 70, 28, FlatButton.Look.Primary);
            LeaveButton = Button("离开会议", PadH + 162, fy - 1, 74, 28, FlatButton.Look.Normal);

            int my = fy + FieldH + RowGap + CaptionH + CaptionGap;
            Caption("手工补成员（广播被挡时用）", PadH, my - CaptionH - CaptionGap, InnerW);
            ManualIp = Input("对方 IP", PadH, my, 130, false);
            ManualPort = Input("", PadH + 136, my, 50, true);
            AddManual = Button("添加", PadH + 192, my - 1, 44, FieldH, FlatButton.Look.Normal);

            State = Small("未加入", PadH, my + FieldH + RowGap, InnerW, 24);
        }
    }


    // ========================== 电平与统计 ==========================
    // 侧栏版：宽度只有 226，所以竖排——两行"标签 + 值 + 满宽电平条"，下面三行紧凑统计。
    internal sealed class MeterCard : CardBase
    {
        public readonly MeterBar In;
        public readonly MeterBar Out;
        public readonly Label Stats;
        public readonly Label InPct;
        public readonly Label OutPct;

        public const int CardW = 254;
        public const int CardH = 126;

        public MeterCard(int x, int y) : base("", x, y, CardW, CardH)
        {
            InPct = Value("0%", Width - PadH - 60, 12, 60);
            Caption("话筒电平", PadH, 12, 70);
            In = new MeterBar();
            In.BarColor = Theme.Accent;
            In.SetBounds(PadH, 12 + 20, InnerW, 12);
            Controls.Add(In);

            OutPct = Value("0%", Width - PadH - 60, 12 + 40, 60);
            Caption("对方声音", PadH, 12 + 40, 70);
            Out = new MeterBar();
            Out.BarColor = Theme.Info;
            Out.SetBounds(PadH, 12 + 60, InnerW, 12);
            Controls.Add(Out);

            Stats = Small("", PadH, 12 + 80, InnerW, 46);
        }
    }

    // =========================== 音频设备 ===========================
    internal sealed class DeviceCard : CardBase
    {
        public readonly DarkSelect In;
        public readonly DarkSelect Out;
        public readonly DarkSelect Codec;
        public readonly DarkSelect Engine;
        public readonly FlatButton RefreshButton;
        public readonly FlatButton LoopTest;
        public readonly FlatButton RecordAb;
        public readonly Label RecHint;

        public const int CardW = 402;
        public const int CardH = 340;

        public DeviceCard(int x, int y) : base("音频设备", x, y, CardW, CardH)
        {
            Caption("录音设备", PadH, RowY(0), InnerW);
            In = Select("选择录音设备", PadH, RowY(0) + CaptionH + CaptionGap, InnerW);

            Caption("播放设备", PadH, RowY(1), InnerW);
            Out = Select("选择播放设备", PadH, RowY(1) + CaptionH + CaptionGap, InnerW);

            Caption("语音编码", PadH, RowY(2), InnerW);
            Codec = Select(null, PadH, RowY(2) + CaptionH + CaptionGap, InnerW);
            Codec.Items.Add("PCM16 高保真 256k");
            Codec.Items.Add("G.711 µ-law 省流量 128k");
            Codec.SelectedIndex = 0;

            Engine = Select(null, PadH, RowY(3), InnerW);
            Engine.Items.Add("降噪引擎：WebRTC NS");
            Engine.Items.Add("降噪引擎：轻量版");
            Engine.SelectedIndex = 0;

            int by = RowY(3) + FieldH + RowGap + 6;
            RefreshButton = Button("刷新设备", PadH, by, 116, 28, FlatButton.Look.Normal);
            LoopTest = Button("本机回环测试", PadH + 122, by, 116, 28, FlatButton.Look.Normal);
            RecordAb = Button("录音对比", PadH + 244, by, InnerW - 244, 28, FlatButton.Look.Normal);

            int cy = by + 28 + RowGap + 2;
            RecHint = Small("录音对比：录 10 秒，两个引擎各出一份 wav + 客观指标", PadH, cy + 26, InnerW, 32);
        }
    }

    // ========================= 通话模式与音量 =========================
    /// <summary>噪声处理：工频陷波、键盘瞬态抑制、耳返试听。三项都是独立开关。</summary>
    internal sealed class NoiseCard : CardBase
    {
        public readonly Switch Hum;
        public readonly Switch KeyClick;
        public readonly Switch Side;
        public readonly Label Status;

        public const int CardW = 828;
        public const int CardH = 196;

        public NoiseCard(int x, int y) : base("噪声处理", x, y, CardW, CardH)
        {
            Small("电流声与键盘敲击是两类特殊噪声，通用降噪压不住，这里单独处理。",
                  PadH, ContentTop - 4, InnerW, 18);

            int y0 = ContentTop + 22;
            int rowH = 40;
            int titleW = 96;

            Hum = SwitchRow(PadH, y0, "电流声抑制", "针对工频嗡声（50/60 Hz 及谐波），自动识别后只挖掉那几个频点", titleW);
            KeyClick = SwitchRow(PadH, y0 + rowH, "键盘声抑制", "针对敲击类冲击噪声，检测到瞬间能量暴涨时快速压低", titleW);
            Side = SwitchRow(PadH, y0 + rowH * 2, "耳返试听", "把自己处理后的声音放给自己听。务必戴耳机，外放会啸叫", titleW);

            Status = Small("", PadH, y0 + rowH * 3, InnerW, 18);
        }
    }
    internal sealed class ModeCard : CardBase
    {
        public readonly OptionRow Full;
        public readonly OptionRow Ptt;
        public readonly FlatButton PttButton;
        public readonly DarkSlider Volume;
        public readonly Label VolVal;
        public readonly CheckBox SpkMute;
        public readonly CheckBox MicMute;
        public readonly DarkSelect Denoise;

        public const int CardW = 404;
        public const int CardH = 340;

        public ModeCard(int x, int y) : base("通话模式与音量", x, y, CardW, CardH)
        {
            Full = Option("全双工", "像打电话一样，建议戴耳机避免回声", PadH, ContentTop, InnerW, 48);
            Full.Checked = true;
            Ptt = Option("按键说话", "不戴耳机也不啸叫，按住才发话筒", PadH, ContentTop + 54, InnerW, 48);
            LinkOptions(Full, Ptt);

            int py = ContentTop + 54 + 48 + RowGap + 4;
            PttButton = Button("按住说话（也可按空格）", PadH, py, InnerW, 34, FlatButton.Look.Normal);

            int vy = py + 34 + RowGap + 6;
            Caption("放音音量", PadH, vy, 60);
            VolVal = Value("100%", Width - PadH - 64, vy, 64);
            Volume = new DarkSlider();
            Volume.Minimum = 0;
            Volume.Maximum = 200;
            Volume.Value = 100;
            Volume.SetBounds(PadH, vy + 20, InnerW, 28);
            Controls.Add(Volume);

            int my = vy + 20 + 28 + RowGap + 2;
            SpkMute = Check("静音扬声器", PadH, my, 120);
            MicMute = Check("静音话筒", PadH + 128, my, 116);

            int dy = my + 22 + RowGap + 4;
            Caption("降噪", PadH, dy + 3, 44);
            Denoise = new DarkSelect();
            Denoise.Items.Add("关闭");
            Denoise.Items.Add("弱");
            Denoise.Items.Add("中（推荐）");
            Denoise.Items.Add("强");
            Denoise.SelectedIndex = 2;
            Denoise.SetBounds(PadH + 44, dy, InnerW - 44, FieldH);
            Controls.Add(Denoise);
        }
    }

    // =========================== 运行日志 ===========================
    internal sealed class LogCard : CardBase
    {
        public readonly Panel PanelLog;
        public readonly TextBox Log;

        public const int CardH = 184;

        public LogCard(int x, int y, int w) : this(x, y, w, CardH) { }

        public LogCard(int x, int y, int w, int h) : base("运行日志", x, y, w, h)
        {
            int pw = w - PadH * 2;
            int ph = h - ContentTop - PadH;

            PanelLog = new Panel();
            PanelLog.BackColor = Theme.CardBg;
            PanelLog.SetBounds(PadH, ContentTop, pw, ph);

            RoundedSurface surface = new RoundedSurface();
            surface.Dock = DockStyle.Fill;
            surface.Padding = new Padding(10, 8, 10, 8);

            Log = new TextBox();
            Log.Multiline = true;
            Log.ReadOnly = true;
            Log.ScrollBars = ScrollBars.Vertical;
            Log.BorderStyle = BorderStyle.None;
            Log.BackColor = Color.FromArgb(18, 18, 18);
            Log.ForeColor = Color.FromArgb(190, 200, 190);
            Log.Font = Theme.FontMono;
            Log.Dock = DockStyle.Fill;

            surface.Controls.Add(Log);
            PanelLog.Controls.Add(surface);
            Controls.Add(PanelLog);
        }
    }

    // =========================== 会议成员 ===========================
    /// <summary>
    /// 成员看板里一张卡需要的数据。渲染层只认这个结构，不直接碰 Conference.Member——
    /// 这样卡片控件不依赖网络层，没开会时也能画出空状态。
    /// </summary>
    internal struct MemberView
    {
        public string Key;          // 身份：自己固定是 MemberBoardCard.SelfKey，别人是 Member.Key
        public string Name;
        public string Sub;          // 副标题，例如"在本房间 12 分钟"
        public bool IsSelf;
        public bool Muted;          // 自己=闭麦；别人=我这边把他静音了
        public bool SpeakerMuted;   // 只对自己的卡片有意义
        public int Level;           // 0..100 说话电平
        public int Volume;          // 别人的单独音量 0..100
        public Image Avatar;
    }

    /// <summary>
    /// 成员卡片容器：自动换行排列，超出可视区就竖向滚动。
    ///
    /// 同一个 Key 的卡片会被复用（只更新字段），绝不重建——否则每 500ms 刷新一次
    /// 就会把鼠标悬停状态、已解码的头像全丢掉，还平白制造一批控件句柄。
    /// </summary>
    internal sealed class MemberCardPanel : Panel
    {
        /// <summary>卡片之间的间距（96 DPI 逻辑值）。</summary>
        public const int GapX = 10;
        public const int GapY = 8;

        /// <summary>看板底色：比卡片暗一档，卡片才有浮起来的感觉。</summary>
        public static readonly Color PanelBg = Color.FromArgb(30, 30, 30);

        /// <summary>点图标或右键：(成员 Key, 动作)。动作取值 mic|speaker|leave|volume|mute|menu。</summary>
        public event Action<string, string> IconClicked;

        /// <summary>一个人都没有时显示的提示。</summary>
        public string EmptyText = "加入会议后，这里会显示房间里的成员";

        private readonly Dictionary<string, MemberCard> _cards = new Dictionary<string, MemberCard>();
        private readonly List<string> _order = new List<string>();

        public MemberCardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            BackColor = PanelBg;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 系统滚动条默认是浅色的，深色界面里像贴了一条白胶布
            DarkChrome.DarkScrollbars(this);
        }

        /// <summary>按给定数据刷新看板：增删卡片、更新字段、重新排版。</summary>
        public void SetMembers(List<MemberView> rows)
        {
            SuspendLayout();
            try
            {
                _order.Clear();
                HashSet<string> keep = new HashSet<string>();

                for (int i = 0; i < rows.Count; i++)
                {
                    string key = rows[i].Key == null ? "" : rows[i].Key;
                    MemberCard c;
                    if (!_cards.TryGetValue(key, out c))
                    {
                        c = new MemberCard();
                        c.IconClicked += OnCardIcon;
                        _cards[key] = c;
                        Controls.Add(c);
                    }
                    c.Apply(rows[i]);
                    _order.Add(key);
                    keep.Add(key);
                }

                // 已经离开的人：删卡并释放（Bitmap 引用由调用方的头像缓存持有，这里不碰）
                List<string> gone = new List<string>();
                foreach (string key in _cards.Keys) if (!keep.Contains(key)) gone.Add(key);
                for (int i = 0; i < gone.Count; i++)
                {
                    MemberCard dead = _cards[gone[i]];
                    _cards.Remove(gone[i]);
                    Controls.Remove(dead);
                    dead.Dispose();
                }

                Relayout();
            }
            finally { ResumeLayout(); }
        }

        private void OnCardIcon(string key, string action)
        {
            Action<string, string> h = IconClicked;
            if (h != null) h(key, action);
        }

        /// <summary>当前 DPI 相对 96 的倍数。卡片按 96 逻辑值画好再整体缩放，排位置也要用同一个倍数。</summary>
        private float DpiFactor()
        {
            try { using (Graphics g = CreateGraphics()) return g.DpiX / 96f; }
            catch { return 1f; }
        }

        /// <summary>重排所有卡片：每行居中、自动换行，并把滚动范围算准。</summary>
        public void Relayout()
        {
            float k = DpiFactor();
            int cw = Math.Max(32, (int)Math.Round(MemberCard.CardW * k));
            int ch = Math.Max(32, (int)Math.Round(MemberCard.CardH * k));
            int gx = Math.Max(2, (int)Math.Round(GapX * k));
            int gy = Math.Max(1, (int)Math.Round(GapY * k));

            // 先按"已经出现滚动条"算可用宽度，避免滚动条一出现就换行、换完又消失的抖动
            int avail = ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
            int perRow = Math.Max(1, (avail - gx) / (cw + gx));
            int rows = (int)Math.Ceiling(_order.Count / (double)perRow);
            int contentH = rows == 0 ? 0 : gy + rows * ch + (rows - 1) * gy + gy;

            // 已经滚过的距离。AutoScroll 是靠移动子控件实现的，所以重排时必须把它加回去，
            // 否则每 500ms 刷新一次就把滚动位置"拉回顶部"（踩过）。
            int scrolled = AutoScrollPosition.Y;

            for (int i = 0; i < _order.Count; i++)
            {
                MemberCard c;
                if (!_cards.TryGetValue(_order[i], out c)) continue;

                int row = i / perRow, col = i % perRow;
                // 一律贴左边排，人多了就往下换行；滚动条由 AutoScroll 负责
                c.SetBounds(col * (cw + gx), gy + row * (ch + gy) + scrolled, cw, ch);
            }

            if (AutoScrollMinSize.Height != contentH) AutoScrollMinSize = new Size(0, contentH);
            Invalidate();
        }

        /// <summary>滚动看板（窗体把滚轮事件转过来时调）。dy 为正向下滚。</summary>
        public void ScrollBy(int dy)
        {
            int max = Math.Max(0, AutoScrollMinSize.Height - ClientSize.Height);
            if (max <= 0) return;
            int cur = -AutoScrollPosition.Y;
            int next = cur + dy;
            if (next < 0) next = 0;
            if (next > max) next = max;
            if (next == cur) return;
            AutoScrollPosition = new Point(0, next);
            Invalidate();
        }

        /// <summary>
        /// 滚轮不在卡片上处理：鼠标停在卡片里时消息要能继续冒泡到窗体，
        /// 由窗体统一判断"指针在不在看板上"再滚。否则停在卡上就滚不动。
        /// </summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 故意不调用 base
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_order.Count > 0) Relayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            if (_order.Count > 0 || string.IsNullOrEmpty(EmptyText)) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Theme.TextMuted))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(EmptyText, Theme.FontSmall, b, ClientRectangle, sf);
            }
        }
    }

    /// <summary>
    /// 大头像成员卡：圆角大图 + 名字 + 副标题 + 一行图标。
    ///
    ///   自己卡片  闭麦 / 静音 / 退出
    ///   别人卡片  音量 / 静音
    ///
    /// 说话时头像外圈跟着电平发绿光（闭麦时是红圈）。
    /// 表情包、共享设备音频、屏幕共享这些没有的功能不做。
    /// </summary>
    internal sealed class MemberCard : Control
    {
        /// <summary>卡片逻辑尺寸（96 DPI）。控件实际尺寸由 DPI 放大，绘制时整体缩放。</summary>
        public const int CardW = 176;
        public const int CardH = 232;

        private const int Pad = 14;          // 卡片内边距
        private const int Av = 148;          // 头像边长
        private const int NameY = 168;       // 名字
        private const int SubY = 189;        // 副标题
        private const int IconY = 206;       // 图标行
        private const int IconH = 24;
        private const int IconW = 34;
        private const int IconGap = 8;
        private const int CardRadius = 12;
        private const int AvatarRadius = 16;

        private static readonly string[] SelfIcons = new string[] { "mic", "speaker", "leave" };
        private static readonly string[] PeerIcons = new string[] { "volume", "mute" };

        public string MemberKey = "";
        public string DisplayName = "";
        public string Sub = "";
        public bool IsSelf;
        public bool Muted;
        public bool SpeakerMuted;
        public int Level;
        public int Volume = 100;
        public Image Avatar;

        /// <summary>点图标或右键：(成员 Key, 动作)。</summary>
        public event Action<string, string> IconClicked;

        private string _hot = "";    // 鼠标悬停在哪个图标上
        private bool _hotCard;       // 鼠标是否在卡片里（边框提亮）

        public MemberCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = MemberCardPanel.PanelBg;   // 圆角外露出的底色必须和容器一致
            Size = new Size(CardW, CardH);
            Cursor = Cursors.Default;
        }

        /// <summary>把新数据合进来；一个字段都没变就不重绘（500ms 刷一次，能省就省）。</summary>
        public void Apply(MemberView v)
        {
            bool dirty = false;
            string key = v.Key == null ? "" : v.Key;
            string name = v.Name == null ? "" : v.Name;
            string sub = v.Sub == null ? "" : v.Sub;

            if (MemberKey != key) { MemberKey = key; dirty = true; }
            if (DisplayName != name) { DisplayName = name; dirty = true; }
            if (Sub != sub) { Sub = sub; dirty = true; }
            if (IsSelf != v.IsSelf) { IsSelf = v.IsSelf; dirty = true; }
            if (Muted != v.Muted) { Muted = v.Muted; dirty = true; }
            if (SpeakerMuted != v.SpeakerMuted) { SpeakerMuted = v.SpeakerMuted; dirty = true; }
            if (Level != v.Level) { Level = v.Level; dirty = true; }
            if (Volume != v.Volume) { Volume = v.Volume; dirty = true; }
            if (Avatar != v.Avatar) { Avatar = v.Avatar; dirty = true; }

            // 无障碍名：读屏软件和自动化测试靠它认出这是谁（卡片是自绘的，没有文字子控件）
            string label = (string.IsNullOrEmpty(DisplayName) ? "(未命名)" : DisplayName) + (Muted ? "（已静音）" : "");
            if (Text != label) Text = label;

            if (dirty) Invalidate();
        }

        // ---------------- 几何 ----------------

        private string[] Keys { get { return IsSelf ? SelfIcons : PeerIcons; } }

        /// <summary>图标行等分头像宽度，第 index 个图标的位置（逻辑坐标）。</summary>
        private static Rectangle IconRect(int index, int total)
        {
            int totalW = total * IconW + (total - 1) * IconGap;
            int x = Pad + (Av - totalW) / 2 + index * (IconW + IconGap);
            return new Rectangle(x, IconY, IconW, IconH);
        }

        private string HitIcon(Point logical)
        {
            string[] keys = Keys;
            for (int i = 0; i < keys.Length; i++) if (IconRect(i, keys.Length).Contains(logical)) return keys[i];
            return "";
        }

        /// <summary>把鼠标坐标换算回 96 DPI 的逻辑坐标。</summary>
        private Point ToLogical(Point p)
        {
            float k = Width / (float)CardW;
            if (k <= 0.05f) k = 1f;
            return new Point((int)(p.X / k), (int)(p.Y / k));
        }

        // ---------------- 交互 ----------------

        private void Fire(string action)
        {
            Action<string, string> h = IconClicked;
            if (h != null) h(MemberKey, action);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string hit = HitIcon(ToLogical(e.Location));
            if (hit == _hot && _hotCard) return;
            _hot = hit;
            _hotCard = true;
            Cursor = hit.Length > 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot.Length == 0 && !_hotCard) return;
            _hot = "";
            _hotCard = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { Fire("menu"); return; }
            if (e.Button != MouseButtons.Left) return;
            string key = HitIcon(ToLogical(e.Location));
            if (key.Length > 0) Fire(key);
        }

        // ---------------- 绘制 ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 先铺满底色：圆角外面露出来的部分必须和容器颜色一致，看起来才是圆角
            using (SolidBrush bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);

            float k = Width / (float)CardW;
            if (k <= 0.05f) k = 1f;
            GraphicsState st = g.Save();
            try
            {
                g.ScaleTransform(k, k);
                PaintCard(g);
            }
            finally { g.Restore(st); }
        }

        private void PaintCard(Graphics g)
        {
            // 卡片底：圆角 + 细描边，靠这两样和看板底色区分开
            Rectangle card = new Rectangle(0, 0, CardW - 1, CardH - 1);
            Color fill = _hotCard ? Color.FromArgb(45, 45, 45) : Color.FromArgb(40, 40, 40);
            Color edge = _hotCard ? Color.FromArgb(68, 68, 68) : Color.FromArgb(52, 52, 52);
            using (GraphicsPath p = Theme.RoundRect(card, CardRadius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(edge, 1f)) g.DrawPath(pen, p);
            }

            // 头像：有图就把图居中裁成正方形贴上去，没图就画首字色块
            Rectangle ar = new Rectangle(Pad, Pad, Av, Av);
            using (GraphicsPath ap = Theme.RoundRect(ar, AvatarRadius))
            {
                if (Avatar != null && Avatar.Width > 1 && Avatar.Height > 1)
                {
                    GraphicsState s = g.Save();
                    try
                    {
                        g.SetClip(ap);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        int side = Math.Min(Avatar.Width, Avatar.Height);
                        int sx = (Avatar.Width - side) / 2, sy = (Avatar.Height - side) / 2;
                        g.DrawImage(Avatar, ar, sx, sy, side, side, GraphicsUnit.Pixel);
                    }
                    finally { g.Restore(s); }
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(AvatarColor(DisplayName))) g.FillPath(b, ap);
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(242, 242, 242)))
                    using (StringFormat sf = Centre())
                    using (GraphicsPath ip = Theme.RoundRect(ar, AvatarRadius))
                    {
                        g.SetClip(ip);
                        g.DrawString(Initial(DisplayName), InitialFont, fb, ar, sf);
                        g.ResetClip();
                    }
                }
            }

            // 说话绿圈：闭麦时换成红圈，免得"闭着麦还在发光"
            if (Level > 4 && !Muted)
            {
                int alpha = Math.Min(230, 70 + Level * 2);
                using (Pen pen = new Pen(Color.FromArgb(alpha, Theme.Accent), 3f))
                using (GraphicsPath rp = Theme.RoundRect(ar, AvatarRadius))
                    g.DrawPath(pen, rp);
            }
            else if (IsSelf && Muted)
            {
                using (Pen pen = new Pen(Color.FromArgb(140, Theme.Danger), 3f))
                using (GraphicsPath rp = Theme.RoundRect(ar, AvatarRadius))
                    g.DrawPath(pen, rp);
            }

            // 名字与副标题
            using (StringFormat sf = SingleLine())
            {
                using (SolidBrush tb = new SolidBrush(Muted && IsSelf ? Theme.TextSecondary : Theme.TextPrimary))
                    g.DrawString(string.IsNullOrEmpty(DisplayName) ? "(未命名)" : DisplayName,
                                 Theme.FontTitle, tb, new RectangleF(Pad, NameY, Av, 20), sf);
                if (!string.IsNullOrEmpty(Sub))
                    using (SolidBrush sb = new SolidBrush(Theme.TextMuted))
                        g.DrawString(Sub, Theme.FontSmall, sb, new RectangleF(Pad, SubY, Av, 16), sf);
            }

            // 图标行：关掉的一律红色，悬停变绿
            string[] keys = Keys;
            for (int i = 0; i < keys.Length; i++)
            {
                string key = keys[i];
                bool on = key == "mic" ? !Muted
                        : key == "speaker" ? !SpeakerMuted
                        : key == "mute" ? !Muted
                        : true;
                Color c = (key == "leave" || !on) ? Theme.Danger
                        : (key == "volume" && Volume < 100) ? Theme.Warn
                        : (_hot == key ? Theme.Accent : Theme.TextSecondary);
                DrawGlyph(g, key, IconRect(i, keys.Length), c);
            }
        }

        /// <summary>名字首字取色：同一个名字永远同一个颜色。</summary>
        private static Color AvatarColor(string name)
        {
            int h = 0;
            if (!string.IsNullOrEmpty(name)) for (int i = 0; i < name.Length; i++) h = h * 31 + name[i];
            int r = 52 + Math.Abs(h) % 96, gg = 52 + Math.Abs(h / 7) % 96, b = 52 + Math.Abs(h / 13) % 96;
            return Color.FromArgb(r, gg, b);
        }

        private static string Initial(string name)
        {
            return string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1);
        }

        private static StringFormat Centre()
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            return sf;
        }

        private static StringFormat SingleLine()
        {
            StringFormat sf = new StringFormat(StringFormatFlags.NoWrap);
            sf.Trimming = StringTrimming.EllipsisCharacter;
            return sf;
        }

        private static Font _initialFont;

        /// <summary>首字大号字体，只建一次（每次绘制都 new Font 会拖慢刷新）。</summary>
        private static Font InitialFont
        {
            get
            {
                if (_initialFont == null) _initialFont = new Font("Microsoft YaHei UI", 34F, FontStyle.Bold);
                return _initialFont;
            }
        }

        /// <summary>矢量图标：话筒 / 扬声器 / 音量 / 静音 / 退出。</summary>
        private static void DrawGlyph(Graphics g, string kind, Rectangle r, Color c)
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (Pen pen = new Pen(c, 1.8f))
            {
                switch (kind)
                {
                    case "mic":
                        g.DrawEllipse(pen, cx - 3, cy - 7, 6, 8);
                        g.DrawArc(pen, cx - 5, cy - 4, 10, 10, 20, 140);
                        g.DrawLine(pen, cx, cy + 6, cx, cy + 8);
                        break;
                    case "speaker":
                        g.DrawPolygon(pen, new Point[] {
                            new Point(cx - 6, cy - 3), new Point(cx - 3, cy - 3),
                            new Point(cx + 2, cy - 7), new Point(cx + 2, cy + 7),
                            new Point(cx - 3, cy + 3), new Point(cx - 6, cy + 3) });
                        break;
                    case "volume":
                        g.DrawPolygon(pen, new Point[] {
                            new Point(cx - 5, cy - 3), new Point(cx - 2, cy - 3),
                            new Point(cx + 2, cy - 6), new Point(cx + 2, cy + 6),
                            new Point(cx - 2, cy + 3), new Point(cx - 5, cy + 3) });
                        g.DrawArc(pen, cx + 1, cy - 5, 8, 10, -60, 120);
                        break;
                    case "mute":
                        g.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
                        g.DrawLine(pen, cx - 6, cy + 6, cx + 6, cy - 6);
                        break;
                    case "leave":
                        g.DrawRectangle(pen, cx - 7, cy - 6, 7, 12);
                        g.DrawLine(pen, cx - 2, cy, cx + 7, cy);
                        g.DrawLine(pen, cx + 4, cy - 3, cx + 7, cy);
                        g.DrawLine(pen, cx + 4, cy + 3, cx + 7, cy);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 侧栏用的紧凑成员行：圆形头像（名字首字）+ 名字 + 右侧状态点。
    /// 状态色：绿=正在说话，橙=已静音，灰=在线。
    /// </summary>
    internal sealed class MemberListView : DarkListView
    {
        private int _hot = -1;   // 鼠标悬停在哪一行（-1 表示没有）

        /// <summary>成员 Key -> 头像。有就用真头像，没有就退回"首字方块"。</summary>
        public readonly Dictionary<string, Image> Avatars = new Dictionary<string, Image>();

        public MemberListView()
        {
            HeaderStyle = ColumnHeaderStyle.None;
            MultiSelect = false;
            Font = Theme.FontUi;
            Columns.Add("成员", 200);
        }

        /// <summary>右键某个成员时触发，参数是成员 Key。</summary>
        public event Action<string> MemberMenuRequested;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Right) return;

            ListViewItem hit = GetItemAt(e.X, e.Y);
            if (hit == null) return;

            SelectedItems.Clear();
            hit.Selected = true;
            Invalidate();

            object key = hit.Tag;
            if (key == null) return;

            Action<string> h = MemberMenuRequested;
            if (h != null) h(key.ToString());
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            ListViewItem hit = GetItemAt(e.X, e.Y);
            int idx = hit == null ? -1 : hit.Index;
            if (idx != _hot)
            {
                _hot = idx;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != -1)
            {
                _hot = -1;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ListViewItem hit = GetItemAt(e.X, e.Y);
            int idx = hit == null ? -1 : hit.Index;
            if (idx != _hot) { _hot = idx; Invalidate(); }
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
            int i2 = Math.Abs(h) % palette.Length;
            return palette[i2];
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != 0) return;

            Graphics g = e.Graphics;
            Rectangle b = e.Bounds;

            // 优先：选中 > 悬停 > 交替行色
            Color bg = e.Item.Selected ? RowSel
                     : (e.ItemIndex == _hot ? Color.FromArgb(40, 44, 40)
                     : (e.ItemIndex % 2 == 0 ? RowA : RowB));
            using (SolidBrush br = new SolidBrush(bg)) g.FillRectangle(br, b);

            string name = e.Item.Text;
            if (string.IsNullOrEmpty(name)) name = "(未命名)";

            // 注意：WinForms 里 SubItems[0] 就是本项自己的文本（名字），
            // 所以 MainForm 后续 Add 进去的是 [1]=Key、[2]=状态、[3]=收包。
            string st = e.Item.SubItems.Count > 2 ? e.Item.SubItems[2].Text : "";
            Color dot = st == "正在说话" ? Theme.Accent
                      : st == "已静音" ? Theme.Warn
                      : Color.FromArgb(110, 110, 110);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            int d = Theme.Radius(this, 18);
            int ax = b.X + Theme.Radius(this, 5);
            int ay = b.Y + (b.Height - d) / 2;

            // 有真头像就画真头像（裁剪成圆角矩形），否则退回"首字方块"
            Image real = null;
            object key = e.Item.Tag;
            if (key != null) Avatars.TryGetValue(key.ToString(), out real);

            Rectangle ar = new Rectangle(ax, ay, d, d);
            using (GraphicsPath ap = Theme.RoundRect(ar, Theme.Radius(this, 7)))
            {
                if (real != null)
                {
                    GraphicsState gs = g.Save();
                    g.SetClip(ap);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(real, ar);
                    g.Restore(gs);
                }
                else
                {
                    using (SolidBrush br = new SolidBrush(AvatarColor(name)))
                        g.FillPath(br, ap);
                }
            }

            using (StringFormat sf = Cell())
            {
                sf.Alignment = StringAlignment.Center;
                using (SolidBrush br = new SolidBrush(Color.FromArgb(16, 16, 16)))
                    g.DrawString(name.Substring(0, 1), Theme.FontSmall, br, new RectangleF(ax, ay, d, d), sf);
            }

            // 状态点贴右边
            int dotD = Theme.Radius(this, 7);
            int dx = b.Right - dotD - Theme.Radius(this, 8);
            int dy = b.Y + (b.Height - dotD) / 2;
            using (SolidBrush br = new SolidBrush(dot))
                g.FillEllipse(br, new Rectangle(dx, dy, dotD, dotD));

            int tx = ax + d + Theme.Radius(this, 7);
            int tw = dx - tx - Theme.Radius(this, 4);
            using (StringFormat sf = Cell())
            using (SolidBrush br = new SolidBrush(e.Item.ForeColor))
            {
                Rectangle r = new Rectangle(tx, b.Y, Math.Max(8, tw), b.Height);
                g.DrawString(name, Font, br, r, sf);
            }
        }
    }

    internal sealed class MembersCard : CardBase
    {
        public readonly MemberListView List;

        private readonly Label _count;

        /// <summary>房间状态块（主状态 + 副标题），公共控件，样式统一。</summary>
        public readonly StatusBlock Head;

        public const int CardW = 254;
        public const int CardH = 210;

        public MembersCard(int x, int y) : this(x, y, CardH) { }

        public MembersCard(int x, int y, int h) : base("会议成员", x, y, CardW, h)
        {
            _count = new Label();
            _count.Text = "0 人";
            _count.Font = Theme.FontSmall;
            _count.ForeColor = Theme.TextMuted;
            _count.BackColor = Color.Transparent;
            _count.TextAlign = ContentAlignment.MiddleRight;
            _count.SetBounds(Width - PadH - 64, 8, 64, 18);
            Controls.Add(_count);

            // 列表占满剩余高度，底部留 42 给两个按钮
            Head = StatusLine(PadH, ContentTop - 2, InnerW);
            Head.Set("未加入", "", null);

            RoundedSurface surface = new RoundedSurface();
            surface.SetBounds(PadH, ContentTop + 40, InnerW, h - ContentTop - 40 - 8);
            surface.Padding = new Padding(5, 4, 5, 5);

            List = new MemberListView();
            List.Dock = DockStyle.Fill;
            surface.Controls.Add(List);


            Controls.Add(surface);
        }

        public void SetCount(int n)
        {
            _count.Text = n + " 人";
        }
    }

    /// <summary>
    /// 主区的成员看板：标题 + 人数，下面是一排会自己换行的大头像成员卡。
    /// 侧栏的「会议成员」卡片仍在，负责紧凑名单和房间状态；这里负责看得清的大图。
    /// </summary>
    internal sealed class MemberBoardCard : CardBase
    {
        /// <summary>自己那张卡的 Key，和成员真实 Key（IP:端口）区分开。</summary>
        public const string SelfKey = "self";

        /// <summary>看板高度（96 DPI）：正好放得下一行卡片。</summary>
        public const int BoardH = 286;

        /// <summary>成员卡片容器。</summary>
        public readonly MemberCardPanel Cards;

        private readonly Label _count;

        public MemberBoardCard(int x, int y, int w, int h) : base("会议成员", x, y, w, h)
        {
            _count = new Label();
            _count.Text = "0 人";
            _count.Font = Theme.FontSmall;
            _count.ForeColor = Theme.TextMuted;
            _count.BackColor = Color.Transparent;
            _count.TextAlign = ContentAlignment.MiddleRight;
            _count.SetBounds(w - PadH - 64, 8, 64, 18);
            Controls.Add(_count);

            Cards = new MemberCardPanel();
            Cards.SetBounds(PadH, ContentTop, InnerW, h - ContentTop - 8);
            Controls.Add(Cards);
        }

        public void SetCount(int n)
        {
            _count.Text = n + " 人";
        }
    }

    // ============================= 聊天 =============================
    internal sealed class ChatCard : CardBase
    {
        public readonly ChatFeed View;
        public readonly DarkInput Draft;
        public readonly FlatButton SendButton;

        public event System.Action<string> SendRequested;
        public event System.Action<byte[]> ImageSendRequested;

        public const int CardH = 380;

        public ChatCard(int x, int y, int w) : this(x, y, w, CardH) { }

        public ChatCard(int x, int y, int w, int h) : base("聊天", x, y, w, h)
        {
            int pw = w - PadH * 2;
            int rowH = 32;
            int viewH = h - ContentTop - rowH - RowGap - PadH - 4;

            RoundedSurface surface = new RoundedSurface();
            surface.SetBounds(PadH, ContentTop, pw, viewH);
            surface.Padding = new Padding(10, 8, 12, 8);

            View = new ChatFeed();
            View.Dock = DockStyle.Fill;
            View.TabStop = false;
            surface.Controls.Add(View);

            // 拖图片文件进来也能发
            AllowDrop = true;
            DragEnter += OnImageDragEnter;
            DragDrop += OnImageDragDrop;

            // 输入框与发送按钮必须等高、同基线，否则看着是歪的
            int iy = ContentTop + viewH + RowGap + 4;
            Draft = Input("说点什么，回车发送（Ctrl+V 粘图／拖入图片也能发）", PadH, iy, pw - 96, false);
            Draft.Height = rowH;
            Draft.Box.KeyDown += OnDraftKeyDown;

            SendButton = Button("发送", PadH + pw - 88, iy, 88, rowH, FlatButton.Look.Primary);
            SendButton.Click += delegate { Fire(); };

            Controls.Add(surface);
        }

        private static bool IsImageFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string e = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif";
        }

        private static string DroppedImage(DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return null;
                return IsImageFile(files[0]) ? files[0] : null;
            }
            catch { return null; }
        }

        private void OnImageDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = DroppedImage(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnImageDragDrop(object sender, DragEventArgs e)
        {
            string file = DroppedImage(e);
            if (file == null) return;
            try
            {
                using (Image raw = ImageCodec.LoadFile(file))
                {
                    if (raw == null) return;
                    using (Bitmap small = ImageCodec.Shrink(raw))
                    {
                        int w, h;
                        byte[] jpeg = ImageCodec.ToJpeg(small, out w, out h);
                        if (jpeg == null || jpeg.Length == 0) return;
                        System.Action<byte[]> cb = ImageSendRequested;
                        if (cb != null) cb(jpeg);
                    }
                }
            }
            catch { }
        }

        private void OnDraftKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V：如果剪贴板里是图片，就直接发图片
            if (e.Control && e.KeyCode == Keys.V && TryPasteImage())
            {
                e.SuppressKeyPress = true;
                return;
            }
            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                e.SuppressKeyPress = true;
                Fire();
            }
        }

        /// <summary>剪贴板里有图就压缩后发出去；没有就返回 false 让 Ctrl+V 走普通文字粘贴。</summary>
        private bool TryPasteImage()
        {
            try
            {
                if (!Clipboard.ContainsImage()) return false;
                using (Image raw = Clipboard.GetImage())
                {
                    if (raw == null) return false;
                    using (Bitmap small = ImageCodec.Shrink(raw))
                    {
                        int w, h;
                        byte[] jpeg = ImageCodec.ToJpeg(small, out w, out h);
                        if (jpeg == null || jpeg.Length == 0) return false;
                        System.Action<byte[]> cb = ImageSendRequested;
                        if (cb == null) return false;
                        cb(jpeg);
                        return true;
                    }
                }
            }
            catch { return false; }
        }

        /// <summary>追加一条图片消息。</summary>
        public void AddImage(string who, byte[] jpeg, bool self, DateTime when)
        {
            ChatMsg m = new ChatMsg();
            m.Who = who;
            m.Self = self;
            m.When = when;
            m.Image = jpeg;
            m.Text = "";
            View.Add(m);
        }

        private void Fire()
        {
            string t = (Draft.Text ?? "").Trim();
            if (t.Length == 0) return;
            System.Action<string> h = SendRequested;
            if (h != null) h(t);
            Draft.Text = "";
            Draft.Box.Focus();
        }

        /// <summary>追加一条消息（自动滚到底，除非用户正在往上翻）。</summary>
        public void Add(string who, string text, bool self, DateTime when)
        {
            ChatMsg m = new ChatMsg();
            m.Who = who;
            m.Text = text;
            m.Self = self;
            m.When = when;
            View.Add(m);
        }

        public void AddRange(System.Collections.Generic.List<ChatMsg> list)
        {
            View.AddRange(list);
        }

        public void Clear()
        {
            View.ClearAll();
        }
    }
}