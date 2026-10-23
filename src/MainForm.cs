// MainForm.cs —— 图形界面（WinForms，纯代码构建，无设计器文件）
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LanTalk
{
    internal sealed partial class MainForm : Form
    {
        private const int Rate = 16000;
        private const int FrameMs = 20;

        private readonly Args _args;

        // 无边框窗口的自绘标题条
        private TopBar _topBar;

        // 两栏容器
        private Panel _viewMain, _sidebar, _main;
        private SettingsPanel _settings;


        // 板块：各自持有并布局自己的控件，本类只摆放和连事件
        private LocalCard _cardLocal;
        private RoomCard _cardRoom;
        private MeterCard _cardMeter;
        private DeviceCard _cardDev;
        private ModeCard _cardMode;
        private LogCard _cardLog;
        private ChatCard _cardChat;
        private MembersCard _cardMembers;
        private MemberBoardCard _cardBoard;

        // 下面全是"别名"：指向板块内部的控件，让既有逻辑可以照旧直接读写。
        // 本类不再创建、也不再给它们定坐标。

        private DarkSelect _cmbLocalIp;
        private DarkInput _txtLocalPort;
        private Label _lblLocalHint;


        private DarkInput _txtRoomPort, _txtManualIp, _txtManualPort;
        private Label _lblRoomState;
        private FlatButton _btnJoin, _btnLeave, _btnAddManual;

        private Label _lblState, _lblPeerState;

        private DarkSelect _cmbIn, _cmbOut, _cmbCodec, _cmbEngine;
        private Label _lblRecHint;
        private FlatButton _btnLoopTest, _btnRecordAb;

        private OptionRow _rdoFull, _rdoPtt;
        private FlatButton _btnPtt;
        private Label _lblVolVal;
        private DarkSlider _trkVolume;
        private DarkSlider _trkMicGain;
        private Label _lblMicGainVal;
        private CheckBox _chkSpkMute, _chkMicMute;
        private DarkSelect _cmbDenoise;

        private MeterBar _pbIn, _pbOut;
        private Label _lblStats;

        private TextBox _txtLog;
        private MemberListView _lstMembers;

        // 成员卡头像缓存（按成员 Key）。真头像到了就换，不用每次重解码。
        private readonly Dictionary<string, Image> _avatars = new Dictionary<string, Image>();

        private System.Windows.Forms.Timer _tick;
        private UdpPeer _net;
        private AudioPath _audio;
        private Conference _conference;
        // 一对一模式的会话对象。界面入口已移除，这里暂时保留以免状态面板取不到音频等级；
        // 下一阶段连同 Session.cs 一起删除。
        private string _myName;
        private bool _pttHeld;
        private int _lastMemberCount;
        private bool _closing;
        private DateTime _lastMemberRefreshUtc = DateTime.MinValue;
        private Icon _iconSmall, _iconBig;

        public MainForm(Args args)
        {
            _args = args;
            Profile.Load();
            // 自定义名字优先，其次命令行 --name，最后才是机器名
            _myName = !string.IsNullOrEmpty(args.Name) ? args.Name : Profile.Name;

            BuildUi();
            BindLog();
            ApplyArgsToUi();
            LoadDevices();
            LoadLocalIps();
            RebuildSession();
            OnModeChanged(null, EventArgs.Empty);
            OnVolumeChanged(null, EventArgs.Empty);
            OnMuteChanged(null, EventArgs.Empty);
            ApplyWay(false);

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 100;
            _tick.Tick += OnTick;
            _tick.Start();

            Log.Write("启动 " + _myName);
            Log.Write("提示：房间号就是端口，同一个号就是同一个会议。");
            Log.Write("提示：首次运行若弹防火墙，请允许「专用网络」。");
        }

        // ================= 界面构建 =================

        private void BuildUi()
        {
            Text = "LanTalk —— 局域网语音通话";
            Theme.InitFonts();
            Font = Theme.FontUi;
            AutoScaleMode = AutoScaleMode.None;
            // 去掉系统标题栏（那条绿框），改成自绘的窄条
            FormBorderStyle = FormBorderStyle.None;
            ClientSize = new Size(1120, 840);
            MinimumSize = new Size(1120, 840);
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            KeyDown += OnFormKeyDown;
            KeyUp += OnFormKeyUp;
            FormClosing += OnFormClosing;
            BackColor = Theme.MainBg;
            ForeColor = Theme.TextPrimary;

            _topBar = new TopBar();
            BuildSidebar();
            BuildMain();
            BuildSettings();

            _viewMain = new Panel();
            _viewMain.SetBounds(0, TopBar.BarHeight, 1120, 780);
            _viewMain.BackColor = Theme.MainBg;
            _viewMain.Controls.AddRange(new Control[] { _sidebar, _main });

            Controls.AddRange(new Control[] { _topBar, _viewMain, _settings });

            BindCards();
            ApplyDpiScale();
            AcceptButton = null;

            // 默认是多人会议室；但如果启动参数里指定了 --room，或给了 --peer，
            // 就按参数意图切模式，免得命令行场景被界面默认值带偏。

            try { SelfUpdate.CleanupOld(Application.ExecutablePath); } catch { }
            BindChatHistoryActions();
            LoadChatHistory();
        }

        // ==================== 侧边栏 ====================
        private void BuildSidebar()
        {
            _sidebar = new Panel();
            _sidebar.SetBounds(0, 0, 278, 780);
            _sidebar.BackColor = Theme.SidebarBg;

            // 侧栏从上到下排满：本机 / 房间号 / 会议成员 / 电平统计 / 运行日志。
            // 间距统一 12，运行日志吃掉最后剩下的高度，整列不会留空位。
            int sideX = 12, sideW = 254, gap = 12;
            int y = gap;

            _cardLocal = new LocalCard(sideX, y);
            y += LocalCard.CardH + gap;

            _cardRoom = new RoomCard(sideX, y);
            _cardRoom.Visible = false;
            y += RoomCard.CardH + gap;

            // 侧栏保留紧凑的成员名单；主区另有一块大头像看板，两边数据同源。
            _cardMembers = new MembersCard(sideX, y, 158);
            y += 158 + gap;

            _cardMeter = new MeterCard(sideX, y);
            y += MeterCard.CardH + gap;

            _cardLog = new LogCard(sideX, y, sideW, 780 - gap - y);
            _cardLog.CornerText = "v" + SettingsPanel.AppVersion + AppPaths.Suffix;

            _sidebar.Controls.AddRange(new Control[] { _cardLocal, _cardRoom, _cardMembers, _cardMeter, _cardLog });
        }

        // ==================== 主区 ====================
        private void BuildMain()
        {
            _main = new Panel();
            _main.SetBounds(278, 0, 842, 780);
            _main.BackColor = Theme.MainBg;

            // 主区只有两块：成员看板 + 聊天。聊天一直拉到底，运行日志已经挪到侧栏。
            _cardBoard = new MemberBoardCard(12, 12, 818, MemberBoardCard.BoardH);
            int chatY = 12 + MemberBoardCard.BoardH + 12;
            _cardChat = new ChatCard(12, chatY, 818, 780 - 12 - chatY);

            _main.Controls.AddRange(new Control[] { _cardBoard, _cardChat });
        }

        // ==================== 把板块控件挂到别名 + 连事件 ====================
        private void BindCards()
        {
            // 本机
            _cmbLocalIp = _cardLocal.Ip;
            _txtLocalPort = _cardLocal.Port;
            _lblLocalHint = _cardLocal.Hint;
            _cardLocal.Apply.Click += OnApplyClick;

            // 连接模式

            // 对方

            // 会议室
            _txtRoomPort = _cardRoom.Port;
            _txtManualIp = _cardRoom.ManualIp;
            _txtManualPort = _cardRoom.ManualPort;
            _lblRoomState = _cardRoom.State;
            _btnJoin = _cardRoom.Join;
            _btnLeave = _cardRoom.LeaveButton;
            _btnAddManual = _cardRoom.AddManual;
            _btnJoin.Click += OnJoinClick;
            _btnLeave.Click += OnLeaveClick;
            _btnAddManual.Click += OnAddManualClick;

            // 状态
            _lblState = _cardMembers.Head.Title;
            _lblPeerState = _cardMembers.Head.Sub;

            // 电平
            _pbIn = _cardMeter.In;
            _pbOut = _cardMeter.Out;
            _lblStats = _cardMeter.Stats;

            // 音频设备
            _cmbIn = _cardDev.In;
            _cmbOut = _cardDev.Out;
            _cmbCodec = _cardDev.Codec;
            _cmbEngine = _cardDev.Engine;
            _lblRecHint = _cardDev.RecHint;
            _cardDev.Codec.SelectedIndexChanged += OnCodecChanged;
            _cardDev.Engine.SelectedIndexChanged += OnEngineChanged;
            _btnLoopTest = _cardDev.LoopTest;
            _btnRecordAb = _cardDev.RecordAb;
            _cardDev.RefreshButton.Click += OnRefreshDevClick;
            _btnLoopTest.Click += OnLoopTestClick;
            _btnRecordAb.Click += OnRecordAbClick;

            // 通话模式
            _rdoFull = _cardMode.Full;
            _rdoPtt = _cardMode.Ptt;
            _btnPtt = _cardMode.PttButton;
            _trkVolume = _cardMode.Volume;
            _lblVolVal = _cardMode.VolVal;
            _trkMicGain = _cardMode.MicGain;
            _lblMicGainVal = _cardMode.MicGainVal;
            _chkSpkMute = _cardMode.SpkMute;
            _chkMicMute = _cardMode.MicMute;
            _cmbDenoise = _cardMode.Denoise;
            _rdoFull.CheckedChanged += OnModeChanged;
            _rdoPtt.CheckedChanged += OnModeChanged;
            _btnPtt.MouseDown += OnPttDown;
            _btnPtt.MouseUp += OnPttUp;
            _btnPtt.MouseLeave += OnPttUp;
            _trkVolume.ValueChanged += OnVolumeChanged;
            _trkMicGain.ValueChanged += OnMicGainChanged;
            _chkSpkMute.CheckedChanged += OnMuteChanged;
            _chkMicMute.CheckedChanged += OnMuteChanged;
            _cmbDenoise.SelectedIndexChanged += OnDenoiseChanged;

            // 顶栏胶囊直接驱动既有的复选框/挂断逻辑（只有一套状态）
            _topBar.Pill.MicToggled += delegate { _chkMicMute.Checked = !_chkMicMute.Checked; };
            _topBar.Pill.SpeakerToggled += delegate { _chkSpkMute.Checked = !_chkSpkMute.Checked; };
            _topBar.Pill.HangupClicked += delegate
            {
                if (_conference != null && _conference.Joined) _conference.Leave();
            };
            _topBar.SettingsClicked += delegate { ShowSettings(); };

            // 日志与成员
            _cardChat.SendRequested += delegate(string t) { OnChatSend(t); };
            _cardChat.ImageSendRequested += delegate(byte[] j) { OnChatSendImage(j); };

            _txtLog = _cardLog.Log;
            // 侧栏名单：右键弹成员小窗
            _lstMembers = _cardMembers.List;
            _lstMembers.MemberMenuRequested += ShowMemberMenu;
            _lstMembers.ForeColor = Theme.TextPrimary;

            // 主区看板：点自己卡上的闭麦/静音/退出，点别人卡上的音量/静音，右键一律弹小窗。
            // 能力全部复用已有链路（复选框、Conference、成员小窗），不另起一套状态。
            _cardBoard.Cards.IconClicked += OnMemberIconClicked;

            // 滚轮：指针落在看板上就滚看板（滚动条本身也照样能用）
            MouseWheel += OnBoardMouseWheel;
        }

        /// <summary>
        /// 界面上所有尺寸都是按 96 DPI 写的。字体按磅渲染会随 DPI 自动变大，
        /// 所以控件框也必须同比放大，否则高 DPI 下文字会被截断。
        /// </summary>
        private void ApplyDpiScale()
        {
            float scale = 1f;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            }
            catch { }
            if (scale < 1f) scale = 1f;
            if (Math.Abs(scale - 1f) < 0.02f) return;

            ClientSize = new Size(Sc(ClientSize.Width, scale), Sc(ClientSize.Height, scale));
            MinimumSize = new Size(Sc(MinimumSize.Width, scale), Sc(MinimumSize.Height, scale));
            ScaleTree(this, scale);
        }

        private static void ScaleTree(Control parent, float scale)
        {
            foreach (Control c in parent.Controls)
            {
                c.SetBounds(Sc(c.Left, scale), Sc(c.Top, scale), Sc(c.Width, scale), Sc(c.Height, scale));
                if (c.Controls.Count > 0) ScaleTree(c, scale);
            }
        }

        private static int Sc(int v, float scale) { return (int)Math.Round(v * scale); }

        private void ApplyDarkChrome()
        {
            try
            {
                DarkChrome.RoundWindowCorners(this);
                DarkChrome.DarkTitleBar(this);
                if (_settings != null) _settings.MainAudioChanged += ApplyNoiseFilters;
            ThemeSweep(this);
            }
            catch { }
        }

        /// <summary>
        /// 把需要深色化的原生控件按类型扫一遍。
        /// 以前是一个个写死控件名，加个新文本框就得回来改一遍；
        /// 现在按类型递归处理，新控件自动纳入。
        /// </summary>
        private static void ThemeSweep(Control root)
        {
            foreach (Control c in root.Controls)
            {
                TextBox tb = c as TextBox;
                if (tb != null && tb.Multiline)
                {
                    DarkChrome.StripVisualStyles(tb);
                    DarkChrome.DarkScrollbars(tb);
                }

                ListView lv = c as ListView;
                if (lv != null && !(lv is DarkListView))
                {
                    DarkChrome.StripVisualStyles(lv);
                    DarkChrome.DarkScrollbars(lv);
                }

                if (c.Controls.Count > 0) ThemeSweep(c);
            }
        }

        private void BindLog()
        {
            Log.LineAdded += OnLogLine;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 控件句柄就绪后，把启动阶段产生的日志一次性补进文本框
            try
            {
                _txtLog.Clear();
                string[] all = Log.Snapshot();
                for (int i = 0; i < all.Length; i++) AppendLog(all[i]);
            }
            catch { }
            ApplyAppIcon();
        }


        // WinForms 的 Form.Icon 默认是它自带的那个通用图标，并不会自动用 exe 里的图标，
        // 所以标题栏和任务栏得自己设。这里直接从 exe 自己的图标组里取 16 和 32 两帧：
        // 标题栏用 16x16、任务栏/Alt+Tab 用 32x32，各自清晰，也不用把图标在文件里存两份。
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, int count);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr h);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void ApplyAppIcon()
        {
            try
            {
                IntPtr lg, sm;
                if (ExtractIconExW(Application.ExecutablePath, 0, out lg, out sm, 1) <= 0) return;

                // FromHandle 不接管句柄，Clone 出一份自己的副本后立刻把提取出来的句柄销毁
                if (sm != IntPtr.Zero) { _iconSmall = (Icon)Icon.FromHandle(sm).Clone(); DestroyIcon(sm); }
                if (lg != IntPtr.Zero) { _iconBig = (Icon)Icon.FromHandle(lg).Clone(); DestroyIcon(lg); }

                if (_iconBig != null) Icon = _iconBig;
                else if (_iconSmall != null) Icon = _iconSmall;
                if (!IsHandleCreated) return;
                if (_iconSmall != null) SendMessage(Handle, WM_SETICON, (IntPtr)ICON_SMALL, _iconSmall.Handle);
                if (_iconBig != null) SendMessage(Handle, WM_SETICON, (IntPtr)ICON_BIG, _iconBig.Handle);
            }
            catch { }
        }

        /// <summary>窗口真正显示之后再自动入会（此时句柄已就绪，控件时序也稳了）。</summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyDarkChrome();
            if (_args.Room > 0)
            {
                try { AutoJoinRoomIfRequested(); }
                catch (Exception ex) { Log.Write("自动加入会议失败：" + ex.Message); }
            }
        }

        /// <summary>带 --room 启动时自动进会议室（可当常驻会议终端用）。</summary>
        private void AutoJoinRoomIfRequested()
        {
            if (_args.Room <= 0) return;
            _txtRoomPort.Text = _args.Room.ToString();
            _txtLocalPort.Text = _args.Room.ToString();
            RebuildSession();
            if (_net == null || _net.LocalPort != _args.Room)
            {
                Log.Write("自动加入会议失败：无法在端口 " + _args.Room + " 上监听。");
                return;
            }
            _conference.Join(_args.Room);
            for (int i = 0; i < _args.ManualPeers.Count; i++) AddManualFromSpec(_args.ManualPeers[i]);
            RefreshMemberList();
        }

        private void AddManualFromSpec(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return;
            string s = spec.Trim();
            string ip = s;
            int port = _args.Room > 0 ? _args.Room : 47800;
            int c = s.LastIndexOf(':');
            if (c > 0)
            {
                ip = s.Substring(0, c);
                int.TryParse(s.Substring(c + 1), out port);
            }
            _conference.AddManualPeer(ip, port);
        }

        private void OnLogLine(string line)
        {
            if (_closing || !IsHandleCreated) return;
            try { BeginInvoke(new Action<string>(AppendLog), line); }
            catch { }
        }

        private void AppendLog(string line)
        {
            if (_txtLog == null) return;
            if (_txtLog.TextLength > 200000) _txtLog.Clear();
            _txtLog.AppendText(line + Environment.NewLine);
        }

        private void ApplyArgsToUi()
        {
            _txtLocalPort.Text = _args.LocalPort.ToString();
            if (_args.Codec == VoiceCodec.MuLaw) _cmbCodec.SelectedIndex = 1;
        }

        private void LoadLocalIps()
        {
            _cmbLocalIp.Items.Clear();
            _cmbLocalIp.Items.Add("自动（监听全部网卡）");
            List<IfaceInfo> list = NetInfo.Ifaces();
            for (int i = 0; i < list.Count; i++) _cmbLocalIp.Items.Add(list[i].ToString());
            int idx = 0;
            if (_args.BindIp != "0.0.0.0")
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Ip == _args.BindIp) { idx = i + 1; break; }
                }
            }
            _cmbLocalIp.SelectedIndex = idx;
        }

        private void LoadDevices()
        {
            int keepIn = SelectedInputId();
            int keepOut = SelectedOutputId();

            _cmbIn.Items.Clear();
            List<DeviceInfo> ins = AudioDevices.Inputs();
            for (int i = 0; i < ins.Count; i++) _cmbIn.Items.Add(ins[i]);
            _cmbIn.SelectedIndex = 0;

            _cmbOut.Items.Clear();
            List<DeviceInfo> outs = AudioDevices.Outputs();
            for (int i = 0; i < outs.Count; i++) _cmbOut.Items.Add(outs[i]);
            _cmbOut.SelectedIndex = 0;

            SelectId(_cmbIn, keepIn);
            SelectId(_cmbOut, keepOut);
        }

        private static void SelectId(DarkSelect c, int id)
        {
            for (int i = 0; i < c.Items.Count; i++)
            {
                DeviceInfo d = c.Items[i] as DeviceInfo;
                if (d != null && d.Id == id) { c.SelectedIndex = i; return; }
            }
        }

        private int SelectedInputId()
        {
            DeviceInfo d = _cmbIn.SelectedItem as DeviceInfo;
            return d == null ? Mm.WAVE_MAPPER : d.Id;
        }

        private int SelectedOutputId()
        {
            DeviceInfo d = _cmbOut.SelectedItem as DeviceInfo;
            return d == null ? Mm.WAVE_MAPPER : d.Id;
        }

        private VoiceCodec SelectedCodec()
        {
            return _cmbCodec.SelectedIndex == 1 ? VoiceCodec.MuLaw : VoiceCodec.Pcm16;
        }

        /// <summary>录音源工厂。带 --null-audio 时改用内置测试音源（自动化测试用，不碰真实声卡）。</summary>
        private Func<IAudioSource> InputFactory()
        {
            if (_args.NullAudio) return delegate() { return new ToneSource(Rate, FrameMs); };
            return delegate() { return new WaveInSource(SelectedInputId(), Rate, FrameMs, 8); };
        }

        /// <summary>放音工厂。带 --null-audio 时改用空播放设备。</summary>
        private Func<IAudioSink> OutputFactory()
        {
            if (_args.NullAudio) return delegate() { return new NullSink(Rate, FrameMs); };
            return delegate() { return new WaveOutSink(SelectedOutputId(), Rate, FrameMs, 8); };
        }

        private string SelectedBindIp()
        {
            if (_cmbLocalIp.SelectedIndex <= 0) return "0.0.0.0";
            List<IfaceInfo> list = NetInfo.Ifaces();
            int i = _cmbLocalIp.SelectedIndex - 1;
            if (i >= 0 && i < list.Count) return list[i].Ip;
            return "0.0.0.0";
        }

        // ================= 会话生命周期 =================

        private void RebuildSession()
        {
            try
            {
                if (_conference != null) { _conference.Dispose(); _conference = null; }
                    if (_audio != null) { _audio.Dispose(); _audio = null; }
                if (_net != null) { _net.Dispose(); _net = null; }
            }
            catch { }

            int port = 47800;
            int.TryParse(_txtLocalPort.Text.Trim(), out port);
            if (port <= 0 || port > 65535) port = 47800;

            _net = new UdpPeer();
            try
            {
                _net.Start(SelectedBindIp(), port);
            }
            catch (Exception ex)
            {
                Log.Write("监听端口 " + port + " 失败：" + ex.Message + "（可能被占用，换一个端口试试）");
                _lblLocalHint.Text = "监听失败";
                _lblLocalHint.ForeColor = Theme.Danger;
                return;
            }


            // 一对一和会议共用同一条音频通道（同一套设备、编码、混音器）
            _audio = new AudioPath(_net, SelectedCodec(), Rate, FrameMs, InputFactory(), OutputFactory());


            _audio.UseNsEngine = _args.UseNsEngine;
            _audio.HumFilter = Profile.HumFilter;
            _audio.ClickFilter = Profile.ClickFilter;
            _audio.Sidetone = Profile.Sidetone;
            _audio.MicGainPercent = Profile.MicGain;
            _audio.SetDenoiseLevel(_args.EffectiveDenoise());
            if (_args.NullAudio && !_args.DenoiseSpecified)
                Log.Write("内置测试音源，降噪默认关");
            if (_cmbEngine != null) _cmbEngine.SelectedIndex = _args.UseNsEngine ? 0 : 1;
Log.Write("降噪引擎 " + (_args.UseNsEngine ? "WebRTC NS" : "轻量版"));
            SyncDenoiseCombo(_args.EffectiveDenoise());

            _conference = new Conference(_net, _audio, _myName);
            _conference.ChatReceived += OnChatReceived;
            _conference.ImageReceived += OnChatImageReceived;
            _conference.Notice += delegate(string m) { Log.Write(m); };
            _conference.Changed += OnConferenceChanged;
            _conference.AvatarChanged += OnMemberAvatar;

            _lblLocalHint.Text = "监听 " + _net.LocalEndPointText;
            _lblLocalHint.ForeColor = Theme.Accent;
            Log.Write("监听 " + _net.LocalEndPointText);
        }

        private void SyncDenoiseCombo(DenoiseLevel lv)
        {
            if (_cmbDenoise == null) return;
            int idx = 2;
            if (lv == DenoiseLevel.Off) idx = 0;
            else if (lv == DenoiseLevel.Low) idx = 1;
            else if (lv == DenoiseLevel.High) idx = 3;
            if (_cmbDenoise.SelectedIndex != idx) _cmbDenoise.SelectedIndex = idx;
            else if (_audio != null) _audio.SetDenoiseLevel(lv);
        }

        private void OnConferenceChanged()
        {
            if (_closing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(RefreshMemberList)); }
            catch { }
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            if (_conference != null && _conference.Joined)
            {
                Log.Write("先离开会议再改监听。");
                return;
            }
            RebuildSession();
        }

        private void OnRefreshDevClick(object sender, EventArgs e)
        {
            if (_conference != null && _conference.Joined)
            {
                Log.Write("先离开会议再换设备。");
                return;
            }
            LoadDevices();
            Log.Write("设备列表已刷新。");
        }

        private void OnCodecChanged(object sender, EventArgs e)
        {
            if (_conference != null && _conference.Joined)
            {
                Log.Write("编码下次生效。");
                return;
            }
            if (_audio != null) _audio.Codec = SelectedCodec();
            Log.Write("编码 " + CodecUtil.Name(SelectedCodec()));
        }

        // ================= 通话方式：一对一 / 会议室 =================

        /// <summary>现在只有会议室一种模式（两个人的房间就是一对一）。</summary>
        private bool RoomMode { get { return true; } }

        private void OnWayChanged(object sender, EventArgs e)
        {
            ApplyWay(true);
        }

        /// <summary>按当前模式显示/隐藏对应的配置区。</summary>
        private void ApplyWay(bool log)
        {
            bool room = RoomMode;
            if (_cardRoom != null) _cardRoom.Visible = room;


            if (!room && _conference != null && _conference.Joined)
            {
                Log.Write("已退出会议。");
                _conference.Leave();
            }

            if (room && _txtRoomPort != null && _txtRoomPort.Text.Trim().Length == 0 && _txtLocalPort != null)
            {
                _txtRoomPort.Text = _txtLocalPort.Text.Trim();
            }
            if (log) Log.Write(room ? "已切到「多人会议室」。" : "已切到「一对一通话」。");
            RefreshStatus();
        }

        // ================= 会议操作 =================

        private void OnJoinClick(object sender, EventArgs e)
        {
            if (_conference == null || _net == null) return;
            if (_conference.Joined) { Log.Write("已经在会议里了。"); return; }

            int room;
            if (!int.TryParse(_txtRoomPort.Text.Trim(), out room) || room <= 0 || room > 65535)
            {
                Log.Write("房间号要是 1~65535 的数字。");
                return;
            }
            // 房间号 = 监听端口；不一致就先按房间号重新监听
            if (_net.LocalPort != room)
            {
                _txtLocalPort.Text = room.ToString();
                Log.Write("监听改到 " + room + "…");
                RebuildSession();
                if (_net == null || _net.LocalPort != room)
                {
                    Log.Write("无法在端口 " + room + " 上监听，会议未加入。");
                    return;
                }
            }
            _conference.Join(room);
            RefreshMemberList();
            RefreshStatus();
        }

        private void OnLeaveClick(object sender, EventArgs e)
        {
            if (_conference == null || !_conference.Joined) return;
            _conference.Leave();
            RefreshMemberList();
            RefreshStatus();
        }

        private void OnAddManualClick(object sender, EventArgs e)
        {
            if (_conference == null || !_conference.Joined)
            {
                Log.Write("请先点「加入会议」，再手工添加成员。");
                return;
            }
            int port;
            if (!int.TryParse(_txtManualPort.Text.Trim(), out port) || port <= 0 || port > 65535) port = _conference.RoomPort;
            _conference.AddManualPeer(_txtManualIp.Text.Trim(), port);
            RefreshMemberList();
        }

        /// <summary>右键成员弹出小窗：单独调这个人的音量、单独静音。
        /// 只做本项目有的功能——私信、禁言、移出频道之类不属于这个软件。</summary>
        private void ShowMemberMenu(string key)
        {
            if (_conference == null || string.IsNullOrEmpty(key)) return;
            Member m = _conference.MemberByKey(key);
            if (m == null) return;

            string title = string.IsNullOrEmpty(m.Name) ? "成员" : m.Name;

            ContextMenuStrip menu = new ContextMenuStrip();
            DarkMenu.Apply(menu);
            menu.MinimumSize = new Size(240, 0);

            // 标题行：用禁用的菜单项而不是 ToolStripLabel——
            // Label 的文字缩进和菜单项不是同一个基准，三行会错开一截。
            ToolStripMenuItem head = new ToolStripMenuItem(title);
            head.Enabled = false;
            head.Font = Theme.FontSmall;
            head.Padding = new Padding(DarkMenu.TextPad, 4, DarkMenu.TextPad, 6);
            menu.Items.Add(head);
            menu.Items.Add(new ToolStripSeparator());

            // 文本跟着状态走：已静音就显示「解除静音」，否则显示「静音」
            ToolStripMenuItem mute = new ToolStripMenuItem(m.Muted ? "解除静音" : "静音");
            mute.Padding = new Padding(DarkMenu.TextPad + 2, 5, DarkMenu.TextPad, 5);
            mute.Click += delegate
            {
                if (_conference == null) return;
                bool next = !m.Muted;
                _conference.SetMemberMuted(key, next);
                mute.Text = next ? "解除静音" : "静音";
            };
            menu.Items.Add(mute);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem volLabel = new ToolStripMenuItem("用户音量   " + m.Volume + "%");
            volLabel.Enabled = false;
            volLabel.Font = Theme.FontSmall;
            volLabel.Padding = new Padding(DarkMenu.TextPad, 6, DarkMenu.TextPad, 2);
            menu.Items.Add(volLabel);

            DarkSlider slider = new DarkSlider();
            slider.Minimum = 0;
            slider.Maximum = 200;
            slider.Value = m.Volume;
            slider.Width = 208;
            slider.Height = 28;

            slider.BackColor = DarkMenu.Bg;
            ToolStripControlHost host = new ToolStripControlHost(slider);
            host.BackColor = DarkMenu.Bg;
            host.Padding = new Padding(10, 2, 10, 8);
            slider.ValueChanged += delegate
            {
                volLabel.Text = "用户音量   " + slider.Value + "%";
                if (_conference != null) _conference.SetMemberVolume(key, slider.Value);
            };
            menu.Items.Add(host);

            // 两种入口（侧栏名单右键、看板图标/右键）都弹同一个成员小窗，位置就在鼠标处
            menu.Show(Cursor.Position);
        }

        /// <summary>
        /// 成员卡上的图标点击。自己卡：闭麦 / 静音 / 退出；别人卡：音量 / 静音。
        /// 这里只做映射——能力全部复用已有链路（复选框、Conference、成员小窗），不另起一套状态。
        /// </summary>
        private void OnMemberIconClicked(string key, string action)
        {
            if (_conference == null || string.IsNullOrEmpty(key)) return;

            if (key == MemberBoardCard.SelfKey)
            {
                switch (action)
                {
                    case "mic":
                        // 改复选框即可：OnMuteChanged 会同步音频链路与顶栏胶囊
                        if (_chkMicMute != null) _chkMicMute.Checked = !_chkMicMute.Checked;
                        RefreshMemberList();
                        break;
                    case "speaker":
                        if (_chkSpkMute != null) _chkSpkMute.Checked = !_chkSpkMute.Checked;
                        RefreshMemberList();
                        break;
                    case "leave":
                        OnLeaveClick(null, EventArgs.Empty);
                        break;
                }
                return;
            }

            if (action == "mute")
            {
                Member m = _conference.MemberByKey(key);
                if (m == null) return;
                bool next = !m.Muted;
                _conference.SetMemberMuted(key, next);
                Log.Write((string.IsNullOrEmpty(m.Name) ? "该成员" : m.Name) + (next ? " 已被你静音。" : " 已解除静音。"));
                RefreshMemberList();
                return;
            }

            if (action == "volume")
            {
                ShowVolumePopup(key);
                return;
            }

            // menu（右键）才出成员小窗
            ShowMemberMenu(key);
        }

        /// <summary>
        /// 别人卡片上点「音量」图标弹出的音量条：只有一条滑块，不再把整个成员小窗摆出来。
        /// 边拉边生效，关掉弹层时再统一刷新卡片（避免拖动过程中每帧都重排）。
        /// </summary>
        private void ShowVolumePopup(string key)
        {
            if (_conference == null || string.IsNullOrEmpty(key)) return;
            Member m = _conference.MemberByKey(key);
            if (m == null) return;

            ContextMenuStrip bar = new ContextMenuStrip();
            DarkMenu.Apply(bar);
            bar.Padding = new Padding(0, 4, 0, 4);

            ToolStripMenuItem tip = new ToolStripMenuItem("音量 " + m.Volume + "%");
            tip.Enabled = false;
            tip.Font = Theme.FontSmall;
            tip.Padding = new Padding(DarkMenu.TextPad, 2, DarkMenu.TextPad, 0);
            bar.Items.Add(tip);

            DarkSlider slider = new DarkSlider();
            slider.Minimum = 0;
            slider.Maximum = 200;
            slider.Value = m.Volume;
            slider.Width = 176;
            slider.Height = 28;
            slider.BackColor = DarkMenu.Bg;

            ToolStripControlHost host = new ToolStripControlHost(slider);
            host.BackColor = DarkMenu.Bg;
            host.Padding = new Padding(DarkMenu.TextPad - 4, 0, DarkMenu.TextPad - 4, 6);

            slider.ValueChanged += delegate
            {
                tip.Text = "音量 " + slider.Value + "%";
                if (_conference != null) _conference.SetMemberVolume(key, slider.Value);
            };
            bar.Items.Add(host);

            // 拉完关掉时把卡片上的图标状态（音量低于 100 会变黄）同步一次
            bar.Closed += delegate { RefreshMemberList(); };

            // 以图标为中心往左挪半个弹层宽度，别让鼠标正好压在滑块上
            Point at = Cursor.Position;
            at.Offset(-96, 10);
            bar.Show(at);
        }

        /// <summary>把会议成员刷到看板上。自己永远排第一张卡，其余按名字排，免得每 500ms 跳序。</summary>
        private void RefreshMemberList()
        {
            if (_cardMembers == null || _conference == null) return;

            bool joined = _conference.Joined;
            // 人数口径含自己：看板上本来就有一张自己的卡
            int total = joined ? _conference.MemberCount + 1 : 0;

            List<MemberView> rows = new List<MemberView>();
            if (joined)
            {
                int mins = (int)(_conference.JoinedSeconds / 60.0);
                string dur = "在本房间 " + mins + " 分钟";

                MemberView me = new MemberView();
                me.Key = MemberBoardCard.SelfKey;
                me.Name = _myName;
                me.Sub = dur;
                me.IsSelf = true;
                me.Muted = _audio != null && _audio.MicMuted;
                me.SpeakerMuted = _audio != null && _audio.SpeakerMuted;
                me.Level = _audio != null ? _audio.InputLevel : 0;
                me.Volume = 100;
                me.Avatar = Profile.Avatar;
                rows.Add(me);

                Member[] ms = _conference.Members;
                Array.Sort(ms, CompareMember);
                for (int i = 0; i < ms.Length; i++)
                {
                    Member m = ms[i];
                    MemberView v = new MemberView();
                    v.Key = m.Key;
                    v.Name = m.Name;
                    v.Sub = dur;
                    v.Muted = m.Muted;
                    v.SpeakerMuted = false;
                    v.Level = m.Level;
                    v.Volume = m.Volume;
                    v.Avatar = AvatarFor(m);
                    rows.Add(v);
                }
            }

            // 主区看板：大卡片
            _cardBoard.Cards.SetMembers(rows);
            _cardBoard.SetCount(total);

            // 侧栏名单：紧凑行，方便一眼扫过去
            if (_lstMembers != null)
            {
                _lstMembers.BeginUpdate();
                try
                {
                    _lstMembers.Items.Clear();
                    for (int i = 0; i < rows.Count; i++)
                    {
                        MemberView v = rows[i];
                        ListViewItem it = new ListViewItem(string.IsNullOrEmpty(v.Name) ? "(未命名)" : v.Name);
                        it.SubItems.Add(v.IsSelf ? MemberBoardCard.SelfKey : v.Key);
                        it.SubItems.Add(v.Muted ? "已静音" : (v.Level >= 3 ? "正在说话" : (v.IsSelf ? "我" : "在线")));
                        it.Tag = v.IsSelf ? MemberBoardCard.SelfKey : v.Key;
                        if (v.Muted) it.ForeColor = Color.Gray;
                        else if (v.Level >= 3) it.ForeColor = Theme.Accent;
                        if (v.Avatar != null && it.Tag != null) _lstMembers.Avatars[it.Tag.ToString()] = v.Avatar;
                        _lstMembers.Items.Add(it);
                    }
                }
                finally { _lstMembers.EndUpdate(); }
            }

            _cardMembers.SetCount(total);

            if (_lblRoomState != null)
            {
                _lblRoomState.Text = joined ? ("会议中 " + total + " 人") : "未加入";
                _lblRoomState.ForeColor = joined ? Theme.Accent : Theme.TextMuted;
            }

            if (joined)
            {
                // 有新人进来（或刚进房）就补发一次自己的头像
                if (_conference.MemberCount > _lastMemberCount) PushMyAvatar();
                _lastMemberCount = _conference.MemberCount;
            }
            else
            {
                _lastMemberCount = 0;
                _avatars.Clear();
                if (_lstMembers != null) _lstMembers.Avatars.Clear();
            }
        }

        /// <summary>
        /// 成员看板的滚轮。滚轮消息会一路冒泡到窗体，这里判断指针是否落在看板上：
        /// 落在就看板滚，不落在就交给原本该滚的地方（聊天、日志各管各的）。
        /// </summary>
        private void OnBoardMouseWheel(object sender, MouseEventArgs e)
        {
            if (_cardBoard == null) return;
            Point p = _cardBoard.Cards.PointToClient(Cursor.Position);
            if (!_cardBoard.Cards.ClientRectangle.Contains(p)) return;
            _cardBoard.Cards.ScrollBy(-e.Delta * 60 / 120);
        }
        /// <summary>成员排序：按名字（没有名字就用 Key），保证刷新前后卡片顺序稳定。</summary>
        private static int CompareMember(Member a, Member b)
        {
            string na = string.IsNullOrEmpty(a.Name) ? a.Key : a.Name;
            string nb = string.IsNullOrEmpty(b.Name) ? b.Key : b.Name;
            int c = string.Compare(na, nb, StringComparison.CurrentCulture);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Key, b.Key);
        }

        /// <summary>取某个成员的头像：先看本次运行的缓存，再看「连接过的人」落盘缓存。</summary>
        private Image AvatarFor(Member m)
        {
            if (m == null) return null;
            Image cached;
            if (_avatars.TryGetValue(m.Key, out cached) && cached != null) return cached;
            try
            {
                Image disk = PeerStore.GetAvatar(m.ClientId);
                if (disk != null) { _avatars[m.Key] = disk; return disk; }
            }
            catch { }
            return null;
        }



        private void OnModeChanged(object sender, EventArgs e)
        {
            bool ptt = _rdoPtt.Checked;
            _btnPtt.Enabled = ptt;
            if (_audio != null) _audio.PttMode = ptt;
            Log.Write(ptt ? "按键说话（按住空格才发送）" : "全双工（建议戴耳机，否则回声）");
        }

        private DenoiseLevel SelectedDenoise()
        {
            if (_cmbDenoise == null) return DenoiseLevel.Mid;
            switch (_cmbDenoise.SelectedIndex)
            {
                case 0: return DenoiseLevel.Off;
                case 1: return DenoiseLevel.Low;
                case 3: return DenoiseLevel.High;
                default: return DenoiseLevel.Mid;
            }
        }

        private void OnDenoiseChanged(object sender, EventArgs e)
        {
            if (_audio == null) return;
            DenoiseLevel lv = SelectedDenoise();
            _audio.SetDenoiseLevel(lv);
            int lat = _audio.UseNsEngine ? NsDenoiser.Latency : Denoiser.Latency;
            Log.Write("降噪 " + DenoiseLevels.Name(lv) + (_audio.UseNsEngine ? " · NS" : " · 轻量") +
                      (lv == DenoiseLevel.Off ? " · 关" : " · " + (lat * 1000 / Rate) + "ms"));
        }

        /// <summary>切换降噪引擎。取的是 AudioPath 上的开关，下一帧就生效，通话中也能切。</summary>
        private void OnEngineChanged(object sender, EventArgs e)
        {
            if (_audio == null) return;
            bool ns = _cmbEngine.SelectedIndex == 0;
            _audio.UseNsEngine = ns;
            Log.Write("降噪引擎 " + (ns ? "WebRTC NS · 6ms" : "轻量版 · 16ms"));
        }

        // ================== 录音对比（降噪 A/B） ==================

        private void OnRecordAbClick(object sender, EventArgs e)
        {
            if (_conference != null && _conference.Joined)
            {
                MessageBox.Show(this, "请先离开会议，再做降噪对比录音。", "录音对比",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DenoiseLevel lv = SelectedDenoise();
            if (lv == DenoiseLevel.Off)
            {
                MessageBox.Show(this, "降噪当前是「关闭」，没有可比的对象。\n请先把降噪切到 弱/中/强。", "录音对比",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _btnRecordAb.Enabled = false;
            Log.Write("========== 录音对比开始 ==========");
            Log.Write("10 秒内正常说话，中间停几秒（要静音段测底噪）。");
            Log.Write("对比档位：" + DenoiseLevels.Name(lv));

            ThreadPool.QueueUserWorkItem(delegate(object st) { RecordAbWorker(lv); });
        }

        private void RecordAbWorker(DenoiseLevel lv)
        {
            string outDir = RecordAb.DefaultOutDir();
            try
            {
                short[] raw = RecordAb.Record(10, delegate(int done) { Log.Write("录音中… " + done + "/10 秒"); });
                if (raw.Length == 0)
                {
                    Log.Write("没有录到任何数据（麦克风可能被占用或被静音）。");
                    return;
                }
                Log.Write("录音结束，共 " + (raw.Length / (double)RecordAb.Rate).ToString("F1") + " 秒，开始处理…");

                AbResult res = DenoiseCompare.RunEx(raw, RecordAb.Rate, lv, outDir,
                    delegate(string s) { Log.Write(s); });

                ShowAbResult(res);
            }
            catch (Exception ex)
            {
                Log.Write("录音对比失败：" + ex.Message);
            }
            finally
            {
                try { BeginInvoke(new Action(delegate() { _btnRecordAb.Enabled = true; })); } catch { }
            }
        }

        private void ShowAbResult(AbResult res)
        {
            try
            {
                BeginInvoke(new Action(delegate()
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("档位：" + DenoiseLevels.Name(SelectedDenoise()) +
                                  "　录音 " + res.Raw.Frames + " 帧");
                    sb.AppendLine();
                    sb.AppendLine("                噪声底      语音电平    估计信噪比");
                    sb.AppendLine("原始录音      " + Fix(res.Raw.NoiseFloorDb) + "  " + Fix(res.Raw.SpeechLevelDb) + "  " + Fix(res.Raw.EstimatedSnrDb));
                    sb.AppendLine("降噪 · NS     " + Fix(res.Ns.NoiseFloorDb) + "  " + Fix(res.Ns.SpeechLevelDb) + "  " + Fix(res.Ns.EstimatedSnrDb));
                    sb.AppendLine("降噪 · 轻量版 " + Fix(res.Classic.NoiseFloorDb) + "  " + Fix(res.Classic.SpeechLevelDb) + "  " + Fix(res.Classic.EstimatedSnrDb));
                    sb.AppendLine();
                    sb.AppendLine("估计信噪比提升：NS " + Signed(res.NsImprove) + " dB　轻量版 " + Signed(res.ClassicImprove) + " dB");
                    sb.AppendLine("（噪声降得越多、语音掉得越少，提升就越大）");

                    if (res.TooLittleContrast)
                    {
                        sb.AppendLine();
                        sb.AppendLine("【提醒】录音里语音和静音的差别不足 6 dB，数据不可信。");
                        sb.AppendLine("请重新录一次：正常说话，中间停顿几秒。");
                    }

                    sb.AppendLine();
                    sb.AppendLine("三个 wav 和详细报告已写入：");
                    sb.AppendLine(res.OutDir);
                    sb.AppendLine();
                    sb.Append("现在打开这个文件夹吗？（用播放器挨个听一遍最直观）");

                    DialogResult r = MessageBox.Show(this, sb.ToString(), "录音对比完成",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes) OpenFolder(res.OutDir);
                }));
            }
            catch { }
        }

        private static string Fix(double v)
        {
            string s = v.ToString("F1");
            if (s.Length < 7) s = new string(' ', 7 - s.Length) + s;
            return s;
        }

        private static string Signed(double v)
        {
            return (v >= 0 ? "+" : "") + v.ToString("F1");
        }

        private static void OpenFolder(string path)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log.Write("打开文件夹失败：" + ex.Message); }
        }

        private void OnVolumeChanged(object sender, EventArgs e)
        {
            if (_audio != null) _audio.VolumePercent = _trkVolume.Value;
            _lblVolVal.Text = _trkVolume.Value + "%";
        }

        /// <summary>麦克风增益 0..150%：立刻生效，并记进设置（下次启动还是这个值）。</summary>
        private void OnMicGainChanged(object sender, EventArgs e)
        {
            int v = _trkMicGain.Value;
            _lblMicGainVal.Text = v + "%";
            // 超过 200% 会把噪声一起放大、还容易削顶，标黄提醒一下
            _lblMicGainVal.ForeColor = v > 200 ? Theme.Warn : Theme.TextSecondary;
            Profile.MicGain = v;
            if (_audio != null) _audio.MicGainPercent = Profile.MicGain;
        }

        private void OnMuteChanged(object sender, EventArgs e)
        {
            // 顶栏胶囊与复选框状态双向同步（要在 _audio 判空之前，否则空闲时不同步）
            if (_topBar != null)
            {
                _topBar.SetMicMuted(_chkMicMute.Checked);
                _topBar.SetSpeakerMuted(_chkSpkMute.Checked);
            }

            if (_audio == null) return;
            _audio.MicMuted = _chkMicMute.Checked;
            _audio.SpeakerMuted = _chkSpkMute.Checked;
        }

        // ================= 呼叫操作 =================


        // ================= 按键说话 =================

        private void OnPttDown(object sender, EventArgs e)
        {
            if (!_rdoPtt.Checked || _pttHeld) return;
            _pttHeld = true;
            if (_audio != null) _audio.PttTalking = true;
        }

        private void OnPttUp(object sender, EventArgs e)
        {
            if (!_pttHeld) return;
            _pttHeld = false;
            if (_audio != null) _audio.PttTalking = false;
        }

        private void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!_rdoPtt.Checked) return;
            OnPttDown(null, EventArgs.Empty);
        }

        private void OnFormKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Space) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnPttUp(null, EventArgs.Empty);
        }

        // ================= 本机回环测试 =================

        private void OnLoopTestClick(object sender, EventArgs e)
        {
            if (_conference != null && _conference.Joined)
            {
                Log.Write("会议中不能做本机回环测试，请先离开会议。");
                return;
            }
            Log.Write("回环测试：话筒 → 编码 → 解码 → 扬声器（约 0.1 秒延迟）。");
            _btnLoopTest.Enabled = false;
            Thread t = new Thread(delegate()
            {
                try
                {
                    UdpPeer dummy = new UdpPeer();
                    AudioPath ap = new AudioPath(dummy, SelectedCodec(), Rate, FrameMs,
                        InputFactory(), OutputFactory());
                    ap.Start(true);
                    Thread.Sleep(3000);
                    long sent = ap.SentFrames;
                    long played = ap.PlayedFrames;
                    long concealed = ap.ConcealedFrames;
                    string sdesc = ap.SourceDescription;
                    string kdesc = ap.SinkDescription;
                    ap.Stop();

                    Log.Write("回环：录音 " + sdesc + " / 放音 " + kdesc);
                    Log.Write("3 秒 发 " + sent + " 帧、播 " + played + " 帧、补静音 " + concealed + " 次 " +
                              (sent >= 100 && played >= 80 ? "正常" : "偏低，换个设备再试"));
                }
                catch (Exception ex)
                {
                    Log.Write("本机回环测试失败：" + ex.Message);
                }
                finally
                {
                    try { BeginInvoke(new Action(delegate() { _btnLoopTest.Enabled = true; })); }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        // ================= 定时刷新 =================

        private void OnSessionStateChanged()
        {
            if (_closing || !IsHandleCreated) return;
            try { BeginInvoke(new Action(RefreshStatus)); }
            catch { }
        }

        private void OnTick(object sender, EventArgs e)
        {
            try { if (_conference != null && _conference.Joined) _conference.Tick(); }
            catch (Exception ex) { Log.Write("会议异常：" + ex.Message); }

            try
            {

                RefreshStatus();
                RefreshMeters();

                // 成员列表每 500ms 刷一次（说话状态会变）
                if (_conference != null && _conference.Joined)
                {
                    if ((DateTime.UtcNow - _lastMemberRefreshUtc).TotalMilliseconds > 500)
                    {
                        _lastMemberRefreshUtc = DateTime.UtcNow;
                        RefreshMemberList();
                    }
                }
            }
            catch { }
        }

        private void RefreshStatus()
        {

                bool joined = _conference != null && _conference.Joined;
                _lblState.Text = joined ? "会议中" : "空闲";
                _lblState.ForeColor = joined ? Theme.Accent : Theme.Info;

                string sub = joined
                    ? ("房间号 " + _conference.RoomPort + "　成员 " + (_conference.MemberCount + 1) + " 人")
                    : "填入房间号后点「加入会议」";
                if (joined && _rdoPtt.Checked) sub += _pttHeld ? "　● 正在发送" : "　○ 按住空格或按钮说话";
                _lblPeerState.Text = sub;

                _btnJoin.Enabled = !joined;
                _btnLeave.Enabled = joined;
                _btnAddManual.Enabled = joined;


        }

        private void RefreshMeters()
        {
            if (_audio == null) return;
            int inLvl = _audio.InputLevel;
            int outLvl = _audio.OutputLevel;
            _pbIn.Value = inLvl > 100 ? 100 : inLvl;
            _pbOut.Value = outLvl > 100 ? 100 : outLvl;
            // 右边那两个百分比以前从来没更新过，一直挂 0%（只有进度条在动）
            if (_cardMeter != null)
            {
                _cardMeter.InPct.Text = _pbIn.Value + "%";
                _cardMeter.OutPct.Text = _pbOut.Value + "%";
            }

            UdpPeer n = _net;
            if (n == null) return;
            int bufMs = _audio.BufferDepthMs;
            long concealed = _audio.ConcealedFrames;

            long rx = n.RxPackets;
            long lost = n.LostPackets;
            double lossPct = (rx + lost) > 0 ? (100.0 * lost / (rx + lost)) : 0;

            double secs = _conference != null ? _conference.JoinedSeconds : 0;
            double upKbps = 0, downKbps = 0;
            if (_conference != null && _conference.Joined && secs > 0.5)
            {
                upKbps = n.TxBytes * 8.0 / 1000.0 / secs;
                downKbps = n.RxBytes * 8.0 / 1000.0 / secs;
            }

            // 侧栏只有 226 宽，统计压成三行短句
            string l1 = "发 " + Units.Count(n.TxPackets) + " / 收 " + Units.Count(rx) +
                        "　丢 " + Units.Count(lost) + "（" + lossPct.ToString("F1") + "%）";
            string l2 = "往返 " + (n.RttMs >= 0 ? n.RttMs.ToString("F1") + " ms" : "—") +
                        "　抖动 " + bufMs + " ms　补静音 " + concealed;
            string l3 = "码率 ↑" + upKbps.ToString("F0") + " ↓" + downKbps.ToString("F0") + " kbps　" +
                        CodecUtil.Name(_audio.Codec).Split('（')[0];
            _lblStats.Text = l1 + Environment.NewLine + l2 + Environment.NewLine + l3;
        }



        /// <summary>把「噪声处理」开关应用到音频链路（设备切换后也会用到）。</summary>
        private void ApplyNoiseFilters()
        {
            if (_audio == null) return;
            _audio.HumFilter = Profile.HumFilter;
            _audio.ClickFilter = Profile.ClickFilter;
            _audio.Sidetone = Profile.Sidetone;
            _audio.MicGainPercent = Profile.MicGain;
            Log.Write("噪声处理：电流声 " + (Profile.HumFilter ? "开" : "关") + "、键盘声 " + (Profile.ClickFilter ? "开" : "关") + "、耳返 " + (Profile.Sidetone ? "开" : "关"));
        }
        // ==================== 头像 ====================

        /// <summary>收到某个成员的头像，存进缓存并刷新看板（卡片会就地换图，不重建）。</summary>
        private void OnMemberAvatar(string key, byte[] jpeg)
        {
            try
            {
                if (_cardMembers == null || string.IsNullOrEmpty(key)) return;
                Bitmap b = ChatFeed.DecodeThumb(jpeg);
                if (b == null) return;
                _avatars[key] = b;
                RefreshMemberList();
            }
            catch { }
        }

        /// <summary>把自己的头像发给房间里所有人（进房时、换头像时、有新成员时调）。</summary>
        private void PushMyAvatar()
        {
            try
            {
                if (_conference == null || !_conference.Joined) return;
                byte[] a = Profile.AvatarBytes;
                if (a == null || a.Length == 0) return;
                _conference.SetAvatar(a);
            }
            catch { }
        }
        // ================= 退出 =================

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            _closing = true;
            try { if (_tick != null) _tick.Stop(); } catch { }
            try { if (_conference != null) _conference.Dispose(); } catch { }
            try { if (_audio != null) _audio.Dispose(); } catch { }
            try { if (_net != null) _net.Dispose(); } catch { }
            try { Log.LineAdded -= OnLogLine; } catch { }
        }
    }
}
