// MainForm.cs —— 图形界面（WinForms，纯代码构建，无设计器文件）
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LanTalk
{
    internal sealed class MainForm : Form
    {
        private const int Rate = 16000;
        private const int FrameMs = 20;

        private readonly Args _args;

        // 通话方式
        private GroupBox _grpWay;
        private RadioButton _rdo1v1, _rdoRoom;
        private Label _lblWayHint;

        // 本机
        private GroupBox _grpLocal;
        private Label _lblLocalIp, _lblLocalPort, _lblLocalHint;
        private ComboBox _cmbLocalIp;
        private TextBox _txtLocalPort;
        private Button _btnApply;

        // 对方（一对一）
        private GroupBox _grpPeer;
        private Label _lblPeerIp, _lblPeerPort, _lblPresence;
        private TextBox _txtPeerIp, _txtPeerPort;
        private Button _btnProbe, _btnScan;

        // 会议室
        private GroupBox _grpRoom;
        private Label _lblRoomPort, _lblRoomState, _lblManual;
        private TextBox _txtRoomPort, _txtManualIp, _txtManualPort;
        private Button _btnJoin, _btnLeave, _btnAddManual;

        // 状态
        private GroupBox _grpStatus;
        private Label _lblState, _lblPeerState;
        private Button _btnCall, _btnAnswer, _btnHangup;

        // 设备
        private GroupBox _grpDev;
        private Label _lblIn, _lblOut, _lblCodec;
        private ComboBox _cmbIn, _cmbOut, _cmbCodec;
        private Button _btnRefreshDev, _btnLoopTest, _btnRecordAb;
        private ComboBox _cmbEngine;
        private Label _lblRecHint;
        private CheckBox _chkAutoAnswer;

        // 模式与音量
        private GroupBox _grpMode;
        private RadioButton _rdoFull, _rdoPtt;
        private Button _btnPtt;
        private Label _lblVolume, _lblVolVal;
        private TrackBar _trkVolume;
        private CheckBox _chkSpkMute, _chkMicMute;
        private Label _lblDenoise;
        private ComboBox _cmbDenoise;

        // 电平与统计
        private GroupBox _grpMeter;
        private Label _lblInLevel, _lblOutLevel, _lblStats;
        private ProgressBar _pbIn, _pbOut;

        // 日志与成员
        private TabControl _tabs;
        private TabPage _tabLog, _tabMembers;
        private TextBox _txtLog;
        private ListView _lstMembers;
        private Button _btnMuteMember, _btnRemoveMember;
        private Label _lblMembersHint;

        private System.Windows.Forms.Timer _tick;
        private UdpPeer _net;
        private AudioPath _audio;
        private Session _session;
        private Conference _conference;
        private string _myName;
        private bool _pttHeld;
        private bool _closing;
        private DateTime _lastMemberRefreshUtc = DateTime.MinValue;
        private Icon _iconSmall, _iconBig;

        public MainForm(Args args)
        {
            _args = args;
            _myName = string.IsNullOrEmpty(args.Name)
                ? (Environment.MachineName + " / " + Environment.UserName)
                : args.Name;

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
            Font = new Font("Microsoft YaHei UI", 9F);
            // 布局尺寸全部按 96 DPI 写，最后统一乘 DPI 系数（见 ApplyDpiScale），
            // 这样字体（按磅渲染、随 DPI 变大）和控件框始终成比例。
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(790, 784);
            MinimumSize = new Size(806, 796);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            KeyDown += OnFormKeyDown;
            KeyUp += OnFormKeyUp;
            FormClosing += OnFormClosing;
            BackColor = SystemColors.Control;

            // ---- 通话方式 ----
            _grpWay = NewGroup("通话方式", 8, 8, 774, 52);
            _rdo1v1 = new RadioButton();
            _rdo1v1.Text = "一对一通话";
            _rdo1v1.SetBounds(14, 20, 110, 22);
            _rdo1v1.Checked = true;
            _rdo1v1.CheckedChanged += OnWayChanged;
            _rdoRoom = new RadioButton();
            _rdoRoom.Text = "多人会议室";
            _rdoRoom.SetBounds(134, 20, 110, 22);
            _rdoRoom.CheckedChanged += OnWayChanged;
            _lblWayHint = NewLabel("", 254, 22, 508);
            _lblWayHint.ForeColor = Color.DimGray;
            _grpWay.Controls.AddRange(new Control[] { _rdo1v1, _rdoRoom, _lblWayHint });

            // ---- 本机 ----
            _grpLocal = NewGroup("本机", 8, 68, 774, 58);
            _lblLocalIp = NewLabel("本机地址", 14, 27, 60);
            _cmbLocalIp = new ComboBox();
            _cmbLocalIp.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLocalIp.SetBounds(78, 23, 210, 24);
            _lblLocalPort = NewLabel("端口", 298, 27, 34);
            _txtLocalPort = new TextBox();
            _txtLocalPort.SetBounds(336, 23, 60, 24);
            _btnApply = NewButton("应用并重启监听", 406, 22, 120, 26);
            _btnApply.Click += OnApplyClick;
            _lblLocalHint = NewLabel("", 536, 27, 232);
            _lblLocalHint.ForeColor = Color.DimGray;
            _grpLocal.Controls.AddRange(new Control[] { _lblLocalIp, _cmbLocalIp, _lblLocalPort, _txtLocalPort, _btnApply, _lblLocalHint });

            // ---- 对方（一对一） ----
            _grpPeer = NewGroup("对方", 8, 134, 774, 60);
            _lblPeerIp = NewLabel("对方 IP", 14, 27, 54);
            _txtPeerIp = new TextBox();
            _txtPeerIp.SetBounds(72, 23, 160, 24);
            _txtPeerIp.Leave += OnPeerTextChanged;
            _txtPeerIp.TextChanged += OnPeerTextChanged;
            _lblPeerPort = NewLabel("端口", 242, 27, 34);
            _txtPeerPort = new TextBox();
            _txtPeerPort.SetBounds(280, 23, 60, 24);
            _txtPeerPort.TextChanged += OnPeerTextChanged;
            _btnProbe = NewButton("测试连通", 350, 22, 90, 26);
            _btnProbe.Click += OnProbeClick;
            _btnScan = NewButton("搜索局域网", 448, 22, 100, 26);
            _btnScan.Click += OnScanClick;
            _lblPresence = NewLabel("对方未设置", 558, 27, 210);
            _grpPeer.Controls.AddRange(new Control[] { _lblPeerIp, _txtPeerIp, _lblPeerPort, _txtPeerPort, _btnProbe, _btnScan, _lblPresence });

            // ---- 会议室（和"对方"同一位置，按模式互相切换显示） ----
            _grpRoom = NewGroup("会议室", 8, 134, 774, 60);
            _lblRoomPort = NewLabel("房间号", 14, 27, 54);
            _txtRoomPort = new TextBox();
            _txtRoomPort.SetBounds(72, 23, 70, 24);
            _btnJoin = NewButton("加入会议", 152, 22, 90, 26);
            _btnJoin.Click += OnJoinClick;
            _btnLeave = NewButton("离开会议", 248, 22, 90, 26);
            _btnLeave.Click += OnLeaveClick;
            _lblManual = NewLabel("手工补成员", 350, 27, 76);
            _txtManualIp = new TextBox();
            _txtManualIp.SetBounds(428, 23, 130, 24);
            _txtManualPort = new TextBox();
            _txtManualPort.SetBounds(564, 23, 60, 24);
            _btnAddManual = NewButton("添加", 630, 22, 60, 26);
            _btnAddManual.Click += OnAddManualClick;
            _lblRoomState = NewLabel("未加入", 696, 27, 72);
            _grpRoom.Controls.AddRange(new Control[] { _lblRoomPort, _txtRoomPort, _btnJoin, _btnLeave, _lblManual, _txtManualIp, _txtManualPort, _btnAddManual, _lblRoomState });
            _grpRoom.Visible = false;

            // ---- 状态 ----
            _grpStatus = NewGroup("通话", 8, 202, 774, 92);
            _lblState = new Label();
            _lblState.SetBounds(18, 18, 380, 36);
            _lblState.Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Bold);
            _lblState.Text = "空闲";
            _lblPeerState = NewLabel("", 20, 56, 380);
            _lblPeerState.Height = 24;
            _lblPeerState.ForeColor = Color.DimGray;
            _btnCall = NewButton("呼叫", 410, 26, 110, 40);
            _btnCall.Click += OnCallClick;
            _btnAnswer = NewButton("接听", 530, 26, 110, 40);
            _btnAnswer.Click += OnAnswerClick;
            _btnHangup = NewButton("挂断", 650, 26, 110, 40);
            _btnHangup.Click += OnHangupClick;
            _grpStatus.Controls.AddRange(new Control[] { _lblState, _lblPeerState, _btnCall, _btnAnswer, _btnHangup });

            // ---- 设备 ----
            _grpDev = NewGroup("音频设备", 8, 302, 774, 114);
            _lblIn = NewLabel("录音设备", 14, 27, 60);
            _cmbIn = new ComboBox();
            _cmbIn.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbIn.SetBounds(78, 23, 300, 24);
            _lblOut = NewLabel("播放设备", 14, 59, 60);
            _cmbOut = new ComboBox();
            _cmbOut.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbOut.SetBounds(78, 55, 300, 24);
            _btnRefreshDev = NewButton("刷新设备", 390, 23, 100, 26);
            _btnRefreshDev.Click += OnRefreshDevClick;
            _btnLoopTest = NewButton("本机回环测试", 390, 55, 112, 26);
            _btnLoopTest.Click += OnLoopTestClick;
            _chkAutoAnswer = new CheckBox();
            _chkAutoAnswer.Text = "自动接听";
            _chkAutoAnswer.SetBounds(514, 26, 102, 22);
            _lblCodec = NewLabel("语音编码", 514, 59, 62);
            _cmbCodec = new ComboBox();
            _cmbCodec.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbCodec.SetBounds(578, 55, 182, 24);
            _cmbCodec.Items.Add("PCM16 高保真 256k");
            _cmbCodec.Items.Add("G.711 µ-law 省流量 128k");
            _cmbCodec.SelectedIndex = 0;
            _cmbCodec.SelectedIndexChanged += OnCodecChanged;
            // 降噪引擎选择：直接占用原来「录音对比」的位置。
            // 不另加标签，让下拉框自己的文字带上「引擎：」前缀，省一行宽度。
            _cmbEngine = new ComboBox();
            _cmbEngine.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbEngine.SetBounds(620, 24, 140, 24);
            _cmbEngine.Items.Add("引擎：WebRTC NS");
            _cmbEngine.Items.Add("引擎：轻量版");
            _cmbEngine.SelectedIndex = 0;
            _cmbEngine.SelectedIndexChanged += OnEngineChanged;

            // 「录音对比」挪到新加的第三行
            _lblRecHint = NewLabel("录 10 秒，两个引擎各出一份 wav + 客观指标，方便自己听感对比", 14, 88, 360);
            _lblRecHint.ForeColor = Color.FromArgb(90, 90, 90);
            _btnRecordAb = NewButton("录音对比", 390, 84, 112, 26);
            _btnRecordAb.Click += OnRecordAbClick;
            _grpDev.Controls.AddRange(new Control[] { _lblIn, _cmbIn, _lblOut, _cmbOut, _btnRefreshDev, _btnLoopTest, _chkAutoAnswer, _lblCodec, _cmbCodec, _cmbEngine, _lblRecHint, _btnRecordAb });

            // ---- 模式与音量 ----
            _grpMode = NewGroup("通话模式与音量", 8, 424, 774, 92);
            _rdoFull = new RadioButton();
            _rdoFull.Text = "全双工（建议戴耳机）";
            _rdoFull.SetBounds(14, 24, 220, 22);
            _rdoFull.Checked = true;
            _rdoFull.CheckedChanged += OnModeChanged;
            _rdoPtt = new RadioButton();
            _rdoPtt.Text = "按键说话（不戴耳机也不啸叫）";
            _rdoPtt.SetBounds(242, 24, 270, 22);
            _rdoPtt.CheckedChanged += OnModeChanged;
            _btnPtt = NewButton("按住说话（也可按空格）", 520, 20, 240, 32);
            _btnPtt.MouseDown += OnPttDown;
            _btnPtt.MouseUp += OnPttUp;
            _btnPtt.MouseLeave += OnPttUp;
            _lblVolume = NewLabel("放音音量", 14, 60, 64);
            _trkVolume = new TrackBar();
            _trkVolume.SetBounds(82, 56, 226, 26);
            _trkVolume.Minimum = 0;
            _trkVolume.Maximum = 200;
            _trkVolume.TickFrequency = 20;
            _trkVolume.Value = 100;
            _trkVolume.ValueChanged += OnVolumeChanged;
            _lblVolVal = NewLabel("100%", 312, 60, 50);
            _chkSpkMute = new CheckBox();
            _chkSpkMute.Text = "静音扬声器";
            _chkSpkMute.SetBounds(370, 59, 106, 22);
            _chkSpkMute.CheckedChanged += OnMuteChanged;
            _chkMicMute = new CheckBox();
            _chkMicMute.Text = "静音话筒";
            _chkMicMute.SetBounds(484, 59, 98, 22);
            _chkMicMute.CheckedChanged += OnMuteChanged;
            _lblDenoise = NewLabel("降噪", 590, 60, 36);
            _cmbDenoise = new ComboBox();
            _cmbDenoise.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDenoise.SetBounds(628, 56, 132, 24);
            _cmbDenoise.Items.Add("关闭");
            _cmbDenoise.Items.Add("弱");
            _cmbDenoise.Items.Add("中（推荐）");
            _cmbDenoise.Items.Add("强");
            _cmbDenoise.SelectedIndex = 2;
            _cmbDenoise.SelectedIndexChanged += OnDenoiseChanged;
            _grpMode.Controls.AddRange(new Control[] { _rdoFull, _rdoPtt, _btnPtt, _lblVolume, _trkVolume, _lblVolVal, _chkSpkMute, _chkMicMute, _lblDenoise, _cmbDenoise });

            // ---- 电平与统计 ----
            _grpMeter = NewGroup("电平与统计", 8, 524, 774, 84);
            _lblInLevel = NewLabel("话筒电平", 14, 26, 64);
            _pbIn = new ProgressBar();
            _pbIn.SetBounds(82, 24, 180, 16);
            _pbIn.Maximum = 100;
            _lblOutLevel = NewLabel("对方声音", 14, 54, 64);
            _pbOut = new ProgressBar();
            _pbOut.SetBounds(82, 52, 180, 16);
            _pbOut.Maximum = 100;
            _lblStats = new Label();
            _lblStats.SetBounds(274, 18, 492, 62);
            _lblStats.ForeColor = Color.FromArgb(40, 40, 40);
            _grpMeter.Controls.AddRange(new Control[] { _lblInLevel, _pbIn, _lblOutLevel, _pbOut, _lblStats });

            // ---- 底部：运行日志 / 会议成员（用标签页，不额外占高度） ----
            _tabs = new TabControl();
            _tabs.SetBounds(8, 616, 774, 160);

            _tabLog = new TabPage("运行日志");
            _txtLog = new TextBox();
            _txtLog.SetBounds(12, 16, 744, 112);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(250, 250, 250);
            _txtLog.Font = new Font("Consolas", 8.5F);
            _tabLog.Controls.Add(_txtLog);

            _tabMembers = new TabPage("会议成员");
            _lstMembers = new ListView();
            _lstMembers.SetBounds(12, 16, 560, 112);
            _lstMembers.View = View.Details;
            _lstMembers.FullRowSelect = true;
            _lstMembers.MultiSelect = false;
            _lstMembers.HideSelection = false;
            _lstMembers.Columns.Add("成员", 190);
            _lstMembers.Columns.Add("地址", 170);
            _lstMembers.Columns.Add("状态", 90);
            _lstMembers.Columns.Add("收包", 90);
            _lstMembers.SelectedIndexChanged += OnMemberSelected;
            _btnMuteMember = NewButton("静音选中", 584, 16, 90, 30);
            _btnMuteMember.Click += OnMuteMemberClick;
            _btnRemoveMember = NewButton("移除选中", 584, 54, 90, 30);
            _btnRemoveMember.Click += OnRemoveMemberClick;
            _lblMembersHint = NewLabel("", 584, 92, 180);
            _lblMembersHint.ForeColor = Color.DimGray;
            _tabMembers.Controls.AddRange(new Control[] { _lstMembers, _btnMuteMember, _btnRemoveMember, _lblMembersHint });

            _tabs.TabPages.Add(_tabLog);
            _tabs.TabPages.Add(_tabMembers);

            Controls.AddRange(new Control[] { _grpWay, _grpLocal, _grpPeer, _grpRoom, _grpStatus, _grpDev, _grpMode, _grpMeter, _tabs });

            ApplyDpiScale();
            AcceptButton = null;
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
            if (scale < 1f) scale = 1f;                       // 只放大，不缩小
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

        private GroupBox NewGroup(string text, int x, int y, int w, int h)
        {
            GroupBox g = new GroupBox();
            g.Text = text;
            g.SetBounds(x, y, w, h);
            return g;
        }

        private Label NewLabel(string text, int x, int y, int w)
        {
            Label l = new Label();
            l.Text = text;
            l.SetBounds(x, y, w, 20);
            return l;
        }

        private Button NewButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.SetBounds(x, y, w, h);
            b.TabStop = false;
            return b;
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
            _rdoRoom.Checked = true;
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

        private static void SelectId(ComboBox c, int id)
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
                _lblLocalHint.ForeColor = Color.Firebrick;
                return;
            }

            _net.PeerDiscovered += OnPeerDiscovered;

            // 一对一和会议共用同一条音频通道（同一套设备、编码、混音器）
            _audio = new AudioPath(_net, SelectedCodec(), Rate, FrameMs, InputFactory(), OutputFactory());

            _session = new Session(_net, _audio);
            _session.Notice += delegate(string m) { Log.Write(m); };
            _session.StateChanged += OnSessionStateChanged;
            _session.PresencePing = true;

            _audio.UseNsEngine = _args.UseNsEngine;
            _audio.SetDenoiseLevel(_args.EffectiveDenoise());
            if (_args.NullAudio && !_args.DenoiseSpecified)
                Log.Write("内置测试音源模式：降噪默认关闭（可用 --denoise 强制开启）");
            if (_cmbEngine != null) _cmbEngine.SelectedIndex = _args.UseNsEngine ? 0 : 1;
            Log.Write("降噪引擎：" + (_args.UseNsEngine
                ? "WebRTC NS 移植版（可在「音频设备」里随时切换）"
                : "轻量版（可在「音频设备」里随时切换）"));
            SyncDenoiseCombo(_args.EffectiveDenoise());

            _conference = new Conference(_net, _audio, _myName);
            _conference.Notice += delegate(string m) { Log.Write(m); };
            _conference.Changed += OnConferenceChanged;

            _lblLocalHint.Text = "监听 " + _net.LocalEndPointText;
            _lblLocalHint.ForeColor = Color.FromArgb(0, 100, 0);
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

        private bool RoomMode { get { return _rdoRoom != null && _rdoRoom.Checked; } }

        private void OnWayChanged(object sender, EventArgs e)
        {
            ApplyWay(true);
        }

        /// <summary>按当前模式显示/隐藏对应的配置区。</summary>
        private void ApplyWay(bool log)
        {
            bool room = RoomMode;
            if (_grpPeer != null) _grpPeer.Visible = !room;
            if (_grpRoom != null) _grpRoom.Visible = room;

            if (room && _session != null && _session.State != CallState.Idle)
            {
                Log.Write("会议模式不支持一对一通话，请先挂断。");
                _rdo1v1.Checked = true;
                return;
            }
            if (!room && _conference != null && _conference.Joined)
            {
                Log.Write("已切回一对一模式，自动退出会议。");
                _conference.Leave();
            }

            if (_tabs != null && _tabMembers != null && _tabLog != null)
            {
                _tabs.SelectedTab = room ? _tabMembers : _tabLog;
            }
            if (_lblWayHint != null)
            {
                _lblWayHint.Text = room
                    ? "房间号就是 UDP 端口：填号码点「加入会议」，同网段自动互相发现。"
                    : "两台电脑之间直接对拨：把对方 IP 填到下面，点「呼叫」。";
            }
            if (room && _txtRoomPort != null && _txtRoomPort.Text.Trim().Length == 0 && _txtLocalPort != null)
            {
                _txtRoomPort.Text = _txtLocalPort.Text.Trim();
            }
            if (_lblMembersHint != null)
            {
                _lblMembersHint.Text = "静音后他那一路不进混音。";
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
                _lblRoomState.ForeColor = _conference.Joined ? Color.FromArgb(0, 120, 0) : Color.DimGray;
            }
            OnMemberSelected(null, EventArgs.Empty);
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
                _lblState.ForeColor = joined ? Color.FromArgb(0, 120, 0) : Color.FromArgb(40, 90, 170);

                string sub = joined
                    ? ("房间号 " + _conference.RoomPort + "　成员 " + _conference.MemberCount + " 人")
                    : "填入房间号后点「加入会议」";
                if (joined && _rdoPtt.Checked) sub += _pttHeld ? "　● 正在发送" : "　○ 按住空格或按钮说话";
                _lblPeerState.Text = sub;

                _btnCall.Enabled = false;
                _btnAnswer.Enabled = false;
                _btnHangup.Enabled = false;
                _btnJoin.Enabled = !joined;
                _btnLeave.Enabled = joined;
                _btnAddManual.Enabled = joined;

                _lblPresence.Text = joined ? ("会议中 " + _conference.MemberCount + " 人") : "未加入会议";
                _lblPresence.ForeColor = joined ? Color.FromArgb(0, 120, 0) : Color.DimGray;
                return;
            }

            _btnJoin.Enabled = false;
            _btnLeave.Enabled = false;
            _btnAddManual.Enabled = false;

            CallState st = _session.State;
            _lblState.Text = CallStateText.Of(st);

            if (st == CallState.InCall) _lblState.ForeColor = Color.FromArgb(0, 120, 0);
            else if (st == CallState.Incoming) _lblState.ForeColor = Color.FromArgb(200, 90, 0);
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

            _btnCall.Enabled = st == CallState.Idle;
            _btnAnswer.Enabled = st == CallState.Incoming;
            _btnHangup.Enabled = st != CallState.Idle;

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

            string l1 = "收发：发出 " + n.TxPackets + " 包 / 收到 " + rx + " 包　丢包 " + lost +
                        "（" + lossPct.ToString("F2") + "%）　乱序 " + n.LatePackets;
            string l2 = "时延：往返 " + (n.RttMs >= 0 ? n.RttMs.ToString("F2") + " ms" : "—") +
                        "　抖动缓冲 " + bufMs + " ms" +
                        "　补静音 " + concealed + " 次";
            string l3 = "码率：上传 " + upKbps.ToString("F0") + " kbps　下载 " + downKbps.ToString("F0") +
                        " kbps　编码 " + CodecUtil.Name(_session.Audio.Codec).Split('（')[0];
            _lblStats.Text = l1 + Environment.NewLine + l2 + Environment.NewLine + l3;
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
