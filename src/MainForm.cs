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
        private Label _lblVer;

        // 板块：各自持有并布局自己的控件，本类只摆放和连事件
        private LocalCard _cardLocal;
        private WayCard _cardWay;
        private PeerCard _cardPeer;
        private RoomCard _cardRoom;
        private StatusCard _cardStatus;
        private MeterCard _cardMeter;
        private DeviceCard _cardDev;
        private ModeCard _cardMode;
        private LogCard _cardLog;
        private ChatCard _cardChat;
        private MembersCard _cardMembers;

        // 下面全是"别名"：指向板块内部的控件，让既有逻辑可以照旧直接读写。
        // 本类不再创建、也不再给它们定坐标。
        private DarkSelect _selMode;
        private Label _lblWayHint;

        private DarkSelect _cmbLocalIp;
        private DarkInput _txtLocalPort;
        private Label _lblLocalHint;

        private DarkInput _txtPeerIp, _txtPeerPort;
        private Label _lblPresence;
        private FlatButton _btnProbe, _btnScan;

        private DarkInput _txtRoomPort, _txtManualIp, _txtManualPort;
        private Label _lblRoomState;
        private FlatButton _btnJoin, _btnLeave, _btnAddManual;

        private Label _lblState, _lblPeerState;
        private FlatButton _btnCall, _btnAnswer;

        private DarkSelect _cmbIn, _cmbOut, _cmbCodec, _cmbEngine;
        private Label _lblRecHint;
        private CheckBox _chkAutoAnswer;
        private FlatButton _btnLoopTest, _btnRecordAb;

        private OptionRow _rdoFull, _rdoPtt;
        private FlatButton _btnPtt;
        private Label _lblVolVal;
        private DarkSlider _trkVolume;
        private CheckBox _chkSpkMute, _chkMicMute;
        private DarkSelect _cmbDenoise;

        private MeterBar _pbIn, _pbOut;
        private Label _lblStats;

        private TextBox _txtLog;
        private ListView _lstMembers;
        private FlatButton _btnMuteMember, _btnRemoveMember;

        private System.Windows.Forms.Timer _tick;
        private UdpPeer _net;
        private AudioPath _audio;
        private Session _session;
        private Conference _conference;
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

            Log.Write("LanTalk 已启动。本机名称：" + _myName);
            Log.Write("提示：两台电脑互相 ping 通之后，把对方的 IP 填到「对方 IP」里即可呼叫。");
            Log.Write("提示：第一次运行如果 Windows 弹出防火墙提示，请勾选「专用网络」并允许访问。");
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
            if (_args.Room > 0) _selMode.SelectedIndex = WayCard.IndexRoom;
            else if (_args.PeerIp != null) _selMode.SelectedIndex = WayCard.IndexP2P;
            else _selMode.SelectedIndex = WayCard.IndexRoom;   // 无参数启动：默认多人会议室

            try { SelfUpdate.CleanupOld(Application.ExecutablePath); } catch { }
            LoadChatHistory();
        }

        // ==================== 侧边栏 ====================
        private void BuildSidebar()
        {
            _sidebar = new Panel();
            _sidebar.SetBounds(0, 0, 278, 780);
            _sidebar.BackColor = Theme.SidebarBg;

            _cardLocal = new LocalCard(12, 12);
            _cardWay = new WayCard(12, 178);
            _cardPeer = new PeerCard(12, 262);
            _cardRoom = new RoomCard(12, 262);
            _cardRoom.Visible = false;

            _cardMembers = new MembersCard(12, 440, 158);

            _cardMeter = new MeterCard(12, 604);

            _lblVer = new Label();
            _lblVer.Text = "v1.3.2";
            _lblVer.SetBounds(14, 738, 240, 18);
            _lblVer.Font = Theme.FontSmall;
            _lblVer.ForeColor = Theme.TextMuted;
            _lblVer.BackColor = Color.Transparent;

            _sidebar.Controls.AddRange(new Control[] { _cardLocal, _cardWay, _cardPeer, _cardRoom, _cardMembers, _cardMeter, _lblVer });
        }

        // ==================== 主区 ====================
        private void BuildMain()
        {
            _main = new Panel();
            _main.SetBounds(278, 0, 842, 780);
            _main.BackColor = Theme.MainBg;

            _cardStatus = new StatusCard(12, 12, 818);
            // 音频设备与通话模式已搬进设置页；这块空间之后给聊天框，先让日志占住
            _cardChat = new ChatCard(12, 82, 818, 518);
            _cardLog = new LogCard(12, 612, 818, 156);

            _main.Controls.AddRange(new Control[] { _cardStatus, _cardChat, _cardLog });
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
            _selMode = _cardWay.Mode;
            _lblWayHint = _cardWay.Hint;
            _selMode.SelectedIndexChanged += OnWayChanged;

            // 对方
            _txtPeerIp = _cardPeer.Ip;
            _txtPeerPort = _cardPeer.Port;
            _lblPresence = _cardPeer.Presence;
            _txtPeerIp.Leave += OnPeerTextChanged;
            _txtPeerIp.TextChanged += OnPeerTextChanged;
            _txtPeerPort.TextChanged += OnPeerTextChanged;
            _btnProbe = _cardPeer.Probe;
            _btnScan = _cardPeer.Scan;
            _btnProbe.Click += OnProbeClick;
            _btnScan.Click += OnScanClick;

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
            _lblState = _cardStatus.State;
            _lblPeerState = _cardStatus.PeerState;
            _btnCall = _cardStatus.Call;
            _btnAnswer = _cardStatus.Answer;
            _btnCall.Click += OnCallClick;
            _btnAnswer.Click += OnAnswerClick;

            // 电平
            _pbIn = _cardMeter.In;
            _pbOut = _cardMeter.Out;
            _lblStats = _cardMeter.Stats;

            // 音频设备
            _cmbIn = _cardDev.In;
            _cmbOut = _cardDev.Out;
            _cmbCodec = _cardDev.Codec;
            _cmbEngine = _cardDev.Engine;
            _chkAutoAnswer = _cardDev.AutoAnswer;
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
            _chkSpkMute = _cardMode.SpkMute;
            _chkMicMute = _cardMode.MicMute;
            _cmbDenoise = _cardMode.Denoise;
            _rdoFull.CheckedChanged += OnModeChanged;
            _rdoPtt.CheckedChanged += OnModeChanged;
            _btnPtt.MouseDown += OnPttDown;
            _btnPtt.MouseUp += OnPttUp;
            _btnPtt.MouseLeave += OnPttUp;
            _trkVolume.ValueChanged += OnVolumeChanged;
            _chkSpkMute.CheckedChanged += OnMuteChanged;
            _chkMicMute.CheckedChanged += OnMuteChanged;
            _cmbDenoise.SelectedIndexChanged += OnDenoiseChanged;

            // 顶栏胶囊直接驱动既有的复选框/挂断逻辑（只有一套状态）
            _topBar.Pill.MicToggled += delegate { _chkMicMute.Checked = !_chkMicMute.Checked; };
            _topBar.Pill.SpeakerToggled += delegate { _chkSpkMute.Checked = !_chkSpkMute.Checked; };
            _topBar.Pill.HangupClicked += delegate { OnHangupClick(null, EventArgs.Empty); };
            _topBar.SettingsClicked += delegate { ShowSettings(); };

            // 日志与成员
            _cardChat.SendRequested += delegate(string t) { OnChatSend(t); };
            _cardChat.ImageSendRequested += delegate(byte[] j) { OnChatSendImage(j); };

            _txtLog = _cardLog.Log;
            _lstMembers = _cardMembers.List;
            _btnMuteMember = _cardMembers.MuteButton;
            _btnRemoveMember = _cardMembers.RemoveButton;
            _lstMembers.SelectedIndexChanged += OnMemberSelected;
            _btnMuteMember.Click += OnMuteMemberClick;
            _btnRemoveMember.Click += OnRemoveMemberClick;
            _lstMembers.ForeColor = Theme.TextPrimary;
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
            _selMode.SelectedIndex = WayCard.IndexRoom;
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
            _txtPeerPort.Text = _args.PeerPort.ToString();
            _txtPeerIp.Text = _args.PeerIp == null ? "" : _args.PeerIp;
            if (_args.Codec == VoiceCodec.MuLaw) _cmbCodec.SelectedIndex = 1;
            if (_args.AutoAnswer) _chkAutoAnswer.Checked = true;
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
                if (_session != null) { _session.Dispose(); _session = null; }
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

            _net.PeerDiscovered += OnPeerDiscovered;

            // 一对一和会议共用同一条音频通道（同一套设备、编码、混音器）
            _audio = new AudioPath(_net, SelectedCodec(), Rate, FrameMs, InputFactory(), OutputFactory());

            _session = new Session(_net, _audio);
            _session.MyName = _myName;
            _session.ChatReceived += OnChatReceived;
            _session.ImageReceived += OnChatImageReceived;
            _session.Notice += delegate(string m) { Log.Write(m); };
            _session.StateChanged += OnSessionStateChanged;
            _session.PresencePing = true;

            _audio.UseNsEngine = _args.UseNsEngine;
            _audio.HumFilter = Profile.HumFilter;
            _audio.ClickFilter = Profile.ClickFilter;
            _audio.Sidetone = Profile.Sidetone;
            _audio.SetDenoiseLevel(_args.EffectiveDenoise());
            if (_args.NullAudio && !_args.DenoiseSpecified)
                Log.Write("内置测试音源模式：降噪默认关闭（可用 --denoise 强制开启）");
            if (_cmbEngine != null) _cmbEngine.SelectedIndex = _args.UseNsEngine ? 0 : 1;
            Log.Write("降噪引擎：" + (_args.UseNsEngine
                ? "WebRTC NS 移植版（可在「音频设备」里随时切换）"
                : "轻量版（可在「音频设备」里随时切换）"));
            SyncDenoiseCombo(_args.EffectiveDenoise());

            _conference = new Conference(_net, _audio, _myName);
            _conference.ChatReceived += OnChatReceived;
            _conference.ImageReceived += OnChatImageReceived;
            _conference.Notice += delegate(string m) { Log.Write(m); };
            _conference.Changed += OnConferenceChanged;
            _conference.AvatarChanged += OnMemberAvatar;

            _lblLocalHint.Text = "监听 " + _net.LocalEndPointText;
            _lblLocalHint.ForeColor = Theme.Accent;
            Log.Write("监听已就绪：" + _net.LocalEndPointText + "（一对一模式下对方要连的就是这个地址）");
            UpdatePeerRemote();
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
            if (_session != null && _session.State != CallState.Idle)
            {
                Log.Write("通话中不能改监听设置，请先挂断。");
                return;
            }
            RebuildSession();
        }

        private void OnRefreshDevClick(object sender, EventArgs e)
        {
            if (_session != null && _session.State == CallState.InCall)
            {
                Log.Write("通话中不能切换设备，请先挂断。");
                return;
            }
            LoadDevices();
            Log.Write("设备列表已刷新。");
        }

        private void OnCodecChanged(object sender, EventArgs e)
        {
            if (_session == null) return;
            if (_session.State == CallState.InCall || (_conference != null && _conference.Joined))
            {
                Log.Write("通话/会议中不能改编码，将在下一次生效。");
                return;
            }
            _session.Audio.Codec = SelectedCodec();
            Log.Write("语音编码已切换为：" + CodecUtil.Name(SelectedCodec()));
        }

        // ================= 通话方式：一对一 / 会议室 =================

        private bool RoomMode { get { return _selMode != null && _selMode.SelectedIndex == WayCard.IndexRoom; } }

        private void OnWayChanged(object sender, EventArgs e)
        {
            ApplyWay(true);
        }

        /// <summary>按当前模式显示/隐藏对应的配置区。</summary>
        private void ApplyWay(bool log)
        {
            bool room = RoomMode;
            if (_cardPeer != null) _cardPeer.Visible = !room;
            if (_cardRoom != null) _cardRoom.Visible = room;

            if (room && _session != null && _session.State != CallState.Idle)
            {
                Log.Write("会议模式不支持一对一通话，请先挂断。");
                _selMode.SelectedIndex = WayCard.IndexP2P;
                return;
            }
            if (!room && _conference != null && _conference.Joined)
            {
                Log.Write("已切回一对一模式，自动退出会议。");
                _conference.Leave();
            }

            if (_lblWayHint != null)
            {
                _lblWayHint.Text = room
                    ? "房间号就是 UDP 端口：填号码点「加入会议」。"
                    : "两台电脑直接对拨：填对方 IP，点「呼叫」。";
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
                Log.Write("房间号必须是 1～65535 的数字（它同时就是本程序的监听端口）。");
                return;
            }
            // 房间号 = 监听端口；不一致就先按房间号重新监听
            if (_net.LocalPort != room)
            {
                _txtLocalPort.Text = room.ToString();
                Log.Write("先把监听端口改成房间号 " + room + " 并重新监听…");
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

        private void OnMemberSelected(object sender, EventArgs e)
        {
            string key = SelectedMemberKey();
            bool has = key.Length > 0;
            if (_btnMuteMember != null) _btnMuteMember.Enabled = has;
            if (_btnRemoveMember != null) _btnRemoveMember.Enabled = has;
            if (has && _btnMuteMember != null)
            {
                Member m = FindMember(key);
                if (m != null) _btnMuteMember.Text = m.Muted ? "取消静音" : "静音选中";
            }
        }

        private void OnMuteMemberClick(object sender, EventArgs e)
        {
            string key = SelectedMemberKey();
            if (key.Length == 0 || _conference == null) return;
            Member m = FindMember(key);
            if (m == null) return;
            _conference.SetMemberMuted(key, !m.Muted);
            Log.Write((m.Muted ? "已取消静音：" : "已静音：") + key);
            RefreshMemberList();
        }

        private void OnRemoveMemberClick(object sender, EventArgs e)
        {
            string key = SelectedMemberKey();
            if (key.Length == 0 || _conference == null) return;
            _conference.RemoveMember(key);
            RefreshMemberList();
        }

        private string SelectedMemberKey()
        {
            if (_lstMembers == null || _lstMembers.SelectedItems.Count == 0) return "";
            object tag = _lstMembers.SelectedItems[0].Tag;
            return tag == null ? "" : tag.ToString();
        }

        private Member FindMember(string key)
        {
            if (_conference == null) return null;
            Member[] ms = _conference.Members;
            for (int i = 0; i < ms.Length; i++) if (ms[i].Key == key) return ms[i];
            return null;
        }

        /// <summary>把成员列表刷到界面（保留当前选中项）。</summary>
        private void RefreshMemberList()
        {
            if (_lstMembers == null || _conference == null) return;
            string keep = SelectedMemberKey();

            _lstMembers.BeginUpdate();
            try
            {
                _lstMembers.Items.Clear();
                Member[] ms = _conference.Members;
                for (int i = 0; i < ms.Length; i++)
                {
                    Member m = ms[i];
                    int lvl = m.Level;
                    ListViewItem it = new ListViewItem(string.IsNullOrEmpty(m.Name) ? "(未命名)" : m.Name);
                    it.SubItems.Add(m.Key);
                    it.SubItems.Add(m.Muted ? "已静音" : (lvl >= 8 ? "正在说话" : "在线"));
                    it.SubItems.Add(m.Received.ToString());
                    it.Tag = m.Key;
                    // 先用缓存里的头像顶上，真图到了再替换（省一次传输）
                    if (!string.IsNullOrEmpty(m.ClientId) && !_cardMembers.List.Avatars.ContainsKey(m.Key))
                    {
                        Image cached = PeerStore.GetAvatar(m.ClientId);
                        if (cached != null) _cardMembers.List.Avatars[m.Key] = cached;
                    }
                    if (m.Muted) it.ForeColor = Color.Gray;
                    else if (lvl >= 8) it.ForeColor = Color.FromArgb(0, 120, 0);
                    _lstMembers.Items.Add(it);
                    if (m.Key == keep) it.Selected = true;
                }
            }
            finally { _lstMembers.EndUpdate(); }

            if (_lblRoomState != null)
            {
                _lblRoomState.Text = _conference.Joined ? ("会议中 " + _conference.MemberCount + " 人") : "未加入";
                _lblRoomState.ForeColor = _conference.Joined ? Theme.Accent : Theme.TextMuted;
            }
            if (_cardMembers != null) _cardMembers.SetCount(_conference.MemberCount);
            OnMemberSelected(null, EventArgs.Empty);
            if (_conference != null && _conference.Joined)
            {
                // 有新人进来（或刚进房）就补发一次自己的头像
                if (_conference.MemberCount > _lastMemberCount) PushMyAvatar();
                _lastMemberCount = _conference.MemberCount;
            }
        }



        private void OnModeChanged(object sender, EventArgs e)
        {
            bool ptt = _rdoPtt.Checked;
            _btnPtt.Enabled = ptt;
            if (_session != null)
            {
                _session.Audio.PttMode = ptt;
                _session.SetTalking(!ptt);
            }
            Log.Write(ptt
                ? "已切到「按键说话」：按住按钮或空格键时才把声音发出去，用扬声器也不会啸叫。"
                : "已切到「全双工」：请戴耳机，否则扬声器的声音会被话筒收回去形成回声。");
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
            Log.Write("降噪：" + DenoiseLevels.Name(lv) +
                      (_audio.UseNsEngine ? "（WebRTC NS 引擎）" : "（轻量引擎）") +
                      (lv == DenoiseLevel.Off ? "，不做任何处理" : "，延迟约 " + (lat * 1000 / Rate) + "ms"));
        }

        /// <summary>切换降噪引擎。取的是 AudioPath 上的开关，下一帧就生效，通话中也能切。</summary>
        private void OnEngineChanged(object sender, EventArgs e)
        {
            if (_audio == null) return;
            bool ns = _cmbEngine.SelectedIndex == 0;
            _audio.UseNsEngine = ns;
            Log.Write("降噪引擎已切换为：" + (ns
                ? "WebRTC NS 移植版（非平稳噪声更稳，算法延迟约 6ms）"
                : "轻量版（稳态噪声略好，算法延迟约 16ms）"));
        }

        // ================== 录音对比（降噪 A/B） ==================

        private void OnRecordAbClick(object sender, EventArgs e)
        {
            if (_session != null && _session.State != CallState.Idle)
            {
                MessageBox.Show(this, "请先结束当前通话，再做降噪对比录音。", "录音对比",
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
            Log.Write("接下来 10 秒请对着麦克风正常说话，中间自然停顿几秒——程序需要静音段来测底噪。");
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
            if (_session != null) _session.Audio.VolumePercent = _trkVolume.Value;
            _lblVolVal.Text = _trkVolume.Value + "%";
        }

        private void OnMuteChanged(object sender, EventArgs e)
        {
            // 顶栏胶囊与复选框状态双向同步（要在 _session 判空之前，否则空闲时不同步）
            if (_topBar != null)
            {
                _topBar.SetMicMuted(_chkMicMute.Checked);
                _topBar.SetSpeakerMuted(_chkSpkMute.Checked);
            }

            if (_session == null) return;
            _session.Audio.MicMuted = _chkMicMute.Checked;
            _session.Audio.SpeakerMuted = _chkSpkMute.Checked;
        }

        // ================= 呼叫操作 =================

        private bool ParsePeer(out string ip, out int port)
        {
            ip = _txtPeerIp.Text.Trim();
            port = 47800;
            int.TryParse(_txtPeerPort.Text.Trim(), out port);
            if (port <= 0 || port > 65535) port = 47800;
            IPAddress a;
            if (ip.Length == 0 || !IPAddress.TryParse(ip, out a))
            {
                Log.Write("「对方 IP」不是合法地址，请填写形如 192.168.1.23 的地址。");
                return false;
            }
            return true;
        }

        private void UpdatePeerRemote()
        {
            if (_net == null) return;
            string ip;
            int port;
            if (!ParsePeer(out ip, out port))
            {
                _net.ClearPeers();
                return;
            }
            _net.SetRemote(ip, port);
        }

        private void OnPeerTextChanged(object sender, EventArgs e)
        {
            UpdatePeerRemote();
        }

        private void OnCallClick(object sender, EventArgs e)
        {
            string ip;
            int port;
            if (!ParsePeer(out ip, out port)) return;
            UpdatePeerRemote();
            _session.Call(ip, port);
        }

        private void OnAnswerClick(object sender, EventArgs e)
        {
            _session.Answer();
        }

        private void OnHangupClick(object sender, EventArgs e)
        {
            _session.Hangup();
        }

        private void OnProbeClick(object sender, EventArgs e)
        {
            string ip;
            int port;
            if (!ParsePeer(out ip, out port)) return;
            UpdatePeerRemote();

            _btnProbe.Enabled = false;
            Log.Write("正在向 " + ip + ":" + port + " 发送 5 个探测包（对方需已运行本程序并监听该端口）…");
            Thread t = new Thread(delegate()
            {
                for (int i = 0; i < 5; i++)
                {
                    try { _net.SendPing(); } catch { }
                    Thread.Sleep(200);
                }
                Thread.Sleep(400);
                double rtt = _net.RttMs;
                string msg;
                if (rtt >= 0)
                {
                    msg = "连通正常：往返时延 " + rtt.ToString("F2") + " ms，对方在线。";
                }
                else
                {
                    msg = "没有收到回应。请检查：① 对方程序是否在运行、端口是否一致；② Windows 防火墙是否放行（见 README 的放行脚本）；③ ping 是否真的通。";
                }
                Log.Write(msg);
                try { BeginInvoke(new Action(delegate() { _btnProbe.Enabled = true; })); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnScanClick(object sender, EventArgs e)
        {
            if (_net == null) return;
            Log.Write("正在广播搜索局域网内的 LanTalk（约 6 秒）…");
            _btnScan.Enabled = false;
            _net.StartDiscovery(_myName, 1500);
            Thread t = new Thread(delegate()
            {
                Thread.Sleep(6000);
                try { _net.StopDiscovery(); } catch { }
                try { BeginInvoke(new Action(delegate() { _btnScan.Enabled = true; })); }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnPeerDiscovered(string name, IPEndPoint ep)
        {
            Log.Write("发现同伴：" + name + "  " + ep.Address + ":" + ep.Port);
            try
            {
                BeginInvoke(new Action(delegate()
                {
                    if (_txtPeerIp.Text.Trim().Length == 0)
                    {
                        _txtPeerIp.Text = ep.Address.ToString();
                        _txtPeerPort.Text = ep.Port.ToString();
                        Log.Write("已自动填入对方地址。");
                    }
                }));
            }
            catch { }
        }

        // ================= 按键说话 =================

        private void OnPttDown(object sender, EventArgs e)
        {
            if (!_rdoPtt.Checked || _pttHeld) return;
            _pttHeld = true;
            if (_session != null) _session.SetTalking(true);
        }

        private void OnPttUp(object sender, EventArgs e)
        {
            if (!_pttHeld) return;
            _pttHeld = false;
            if (_session != null) _session.SetTalking(false);
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
            if (_session != null && _session.State != CallState.Idle)
            {
                Log.Write("通话中不能做本机回环测试，请先挂断。");
                return;
            }
            Log.Write("开始本机回环测试：话筒采集 → 编码 → 解码 → 扬声器放回（会有 0.1 秒左右延迟，戴耳机更安全）。");
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

                    Log.Write("回环结果：录音=" + sdesc + "；放音=" + kdesc);
                    Log.Write("3 秒内发出 " + sent + " 帧、播放 " + played + " 帧、补静音 " + concealed + " 次。" +
                              (sent >= 100 && played >= 80 ? "设备工作正常。" : "数值偏低，请在「音频设备」里换一个设备再试。"));
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
            if (_session == null) return;
            try { _session.Tick(); }
            catch (Exception ex) { Log.Write("状态机异常：" + ex.Message); }
            try { if (_conference != null && _conference.Joined) _conference.Tick(); }
            catch (Exception ex) { Log.Write("会议异常：" + ex.Message); }

            try
            {
                _session.AutoAnswer = _chkAutoAnswer.Checked;
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
            if (_session == null) return;
            bool room = RoomMode;

            if (room)
            {
                bool joined = _conference != null && _conference.Joined;
                _lblState.Text = joined ? "会议中" : "空闲";
                _lblState.ForeColor = joined ? Theme.Accent : Theme.Info;

                string sub = joined
                    ? ("房间号 " + _conference.RoomPort + "　成员 " + _conference.MemberCount + " 人")
                    : "填入房间号后点「加入会议」";
                if (joined && _rdoPtt.Checked) sub += _pttHeld ? "　● 正在发送" : "　○ 按住空格或按钮说话";
                _lblPeerState.Text = sub;

                _btnCall.Enabled = false;
                _btnAnswer.Enabled = false;
                _btnJoin.Enabled = !joined;
                _btnLeave.Enabled = joined;
                _btnAddManual.Enabled = joined;

                _lblPresence.Text = joined ? ("会议中 " + _conference.MemberCount + " 人") : "未加入会议";
                _lblPresence.ForeColor = joined ? Theme.Accent : Theme.TextMuted;
                return;
            }

            _btnJoin.Enabled = false;
            _btnLeave.Enabled = false;
            _btnAddManual.Enabled = false;

            CallState st = _session.State;
            _lblState.Text = CallStateText.Of(st);

            if (st == CallState.InCall) _lblState.ForeColor = Theme.Accent;
            else if (st == CallState.Incoming) _lblState.ForeColor = Theme.Warn;
            else if (st == CallState.Outgoing) _lblState.ForeColor = Theme.Info;
            else if (st == CallState.Outgoing) _lblState.ForeColor = Color.FromArgb(0, 80, 180);
            else _lblState.ForeColor = Color.FromArgb(70, 70, 70);

            string sub2 = "";
            if (st == CallState.Outgoing)
            {
                sub2 = _session.PeerRinging ? "对方正在振铃…" : "正在等待对方响应…";
                sub2 += "（已等待 " + ((int)_session.StateSeconds) + " 秒）";
            }
            else if (st == CallState.Incoming)
            {
                sub2 = "有来电，点「接听」按钮接通（已振铃 " + ((int)_session.StateSeconds) + " 秒）";
            }
            else if (st == CallState.InCall)
            {
                sub2 = "通话时长 " + TimeSpan.FromSeconds((int)_session.StateSeconds).ToString(@"mm\:ss");
                if (_rdoPtt.Checked) sub2 += _pttHeld ? "　● 正在发送" : "　○ 按住空格或按钮说话";
                if (_session.PeerTalking) sub2 += "　（对方正在说话）";
            }
            else
            {
                sub2 = _net != null ? "本机 " + _net.LocalEndPointText : "";
            }
            _lblPeerState.Text = sub2;

            // 会议模式下呼叫/接听没有意义，直接隐藏；一对一才显示
            bool callUi = !RoomMode;
            _btnCall.Visible = callUi;
            _btnAnswer.Visible = callUi;
            _btnCall.Enabled = callUi && st == CallState.Idle;
            _btnAnswer.Enabled = callUi && st == CallState.Incoming;
            if (_topBar != null) _topBar.SetHangupEnabled(st != CallState.Idle || RoomMode);

            // 对方在线状态
            if (_net == null || _net.Remote == null)
            {
                _lblPresence.Text = "对方未设置";
                _lblPresence.ForeColor = Color.DimGray;
            }
            else if (_session.PeerSeenRecently(4000))
            {
                double rtt = _net.RttMs;
                _lblPresence.Text = "对方在线" + (rtt >= 0 ? "（RTT " + rtt.ToString("F1") + " ms）" : "");
                _lblPresence.ForeColor = Color.FromArgb(0, 120, 0);
            }
            else
            {
                _lblPresence.Text = "对方无响应";
                _lblPresence.ForeColor = Color.Firebrick;
            }
        }

        private void RefreshMeters()
        {
            int inLvl = _session.Audio.InputLevel;
            int outLvl = _session.Audio.OutputLevel;
            _pbIn.Value = inLvl > 100 ? 100 : inLvl;
            _pbOut.Value = outLvl > 100 ? 100 : outLvl;

            UdpPeer n = _net;
            if (n == null) return;
            int bufMs = _session.Audio.BufferDepthMs;
            long concealed = _session.Audio.ConcealedFrames;

            long rx = n.RxPackets;
            long lost = n.LostPackets;
            double lossPct = (rx + lost) > 0 ? (100.0 * lost / (rx + lost)) : 0;

            double secs = _session.StateSeconds;
            double upKbps = 0, downKbps = 0;
            if (_session.State == CallState.InCall && secs > 0.5)
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
                        CodecUtil.Name(_session.Audio.Codec).Split('（')[0];
            _lblStats.Text = l1 + Environment.NewLine + l2 + Environment.NewLine + l3;
        }



        /// <summary>把「噪声处理」开关应用到音频链路（设备切换后也会用到）。</summary>
        private void ApplyNoiseFilters()
        {
            if (_audio == null) return;
            _audio.HumFilter = Profile.HumFilter;
            _audio.ClickFilter = Profile.ClickFilter;
            _audio.Sidetone = Profile.Sidetone;
            Log.Write("噪声处理：电流声抑制=" + (Profile.HumFilter ? "开" : "关") + "，键盘声抑制=" + (Profile.ClickFilter ? "开" : "关") + "，耳返=" + (Profile.Sidetone ? "开" : "关"));
        }
        // ==================== 头像 ====================

        /// <summary>收到某个成员的头像，存进成员列表并重绘。</summary>
        private void OnMemberAvatar(string key, byte[] jpeg)
        {
            try
            {
                if (_cardMembers == null || string.IsNullOrEmpty(key)) return;
                Bitmap b = ChatFeed.DecodeThumb(jpeg);
                if (b == null) return;
                _cardMembers.List.Avatars[key] = b;
                _cardMembers.List.Invalidate();
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
            try { if (_session != null) _session.Hangup(); } catch { }
            try { if (_session != null) _session.Dispose(); } catch { }
            try { if (_audio != null) _audio.Dispose(); } catch { }
            try { if (_net != null) _net.Dispose(); } catch { }
            try { Log.LineAdded -= OnLogLine; } catch { }
        }
    }
}
