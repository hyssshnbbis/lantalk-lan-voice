// SetupUi.cs —— 安装向导界面与卸载界面（纯代码构建，自带 DPI 缩放）
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace LanTalk.Setup
{
    /// <summary>按 96 DPI 写尺寸，最后统一乘 DPI 系数，避免高 DPI 下文字被截断。</summary>
    internal static class DpiUi
    {
        public static float Factor()
        {
            float scale = 1f;
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            }
            catch { }
            if (scale < 1f) scale = 1f;
            if (Math.Abs(scale - 1f) < 0.02f) return 1f;
            return scale;
        }

        public static int Sc(int v, float s) { return (int)Math.Round(v * s); }

        public static void Apply(Form f, float s)
        {
            if (s <= 1f) return;
            f.ClientSize = new Size(Sc(f.ClientSize.Width, s), Sc(f.ClientSize.Height, s));
            f.MinimumSize = new Size(Sc(f.MinimumSize.Width, s), Sc(f.MinimumSize.Height, s));
            Walk(f, s);
        }

        private static void Walk(Control parent, float s)
        {
            foreach (Control c in parent.Controls)
            {
                c.SetBounds(Sc(c.Left, s), Sc(c.Top, s), Sc(c.Width, s), Sc(c.Height, s));
                if (c.Controls.Count > 0) Walk(c, s);
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly SetupArgs _args;
        private readonly float _s;

        private TextBox _txtDir;
        private Button _btnBrowse;
        private CheckBox _chkDesktop, _chkStart, _chkFw, _chkRun;
        private ProgressBar _pb;
        private Label _lblStatus;
        private TextBox _txtLog;
        private Button _btnInstall, _btnQuit;

        private bool _busy;
        private bool _done;
        private bool _ok;

        public bool Succeeded { get { return _ok; } }

        public SetupForm(SetupArgs a)
        {
            _args = a;
            _s = DpiUi.Factor();
            Build();
            _txtDir.Text = string.IsNullOrEmpty(a.Dir) ? InstallEngine.DefaultDir() : a.Dir;
        }

        private void Build()
        {
            Text = "LanTalk 安装程序";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(560, 508);
            MinimumSize = new Size(576, 546);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = SystemColors.Control;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "LanTalk 局域网语音通话";
            title.SetBounds(20, 16, 520, 30);
            title.Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold);

            Label sub = new Label();
            sub.Text = "安装程序 v" + InstallEngine.Version + "　·　局域网点对点语音通话　·　免服务器　·　免联网　·　免运行库";
            sub.SetBounds(22, 46, 520, 20);
            sub.ForeColor = Color.DimGray;

            GroupBox grpDir = new GroupBox();
            grpDir.Text = "安装位置";
            grpDir.SetBounds(16, 76, 528, 68);
            _txtDir = new TextBox();
            _txtDir.SetBounds(28, 28, 410, 24);
            _btnBrowse = new Button();
            _btnBrowse.Text = "浏览…";
            _btnBrowse.SetBounds(446, 27, 70, 26);
            _btnBrowse.TabStop = false;
            _btnBrowse.Click += OnBrowse;
            grpDir.Controls.AddRange(new Control[] { _txtDir, _btnBrowse });

            GroupBox grpOpt = new GroupBox();
            grpOpt.Text = "安装选项";
            grpOpt.SetBounds(16, 152, 528, 144);
            _chkDesktop = NewCheck("创建桌面快捷方式", 28, 26, 300, true);
            _chkStart = NewCheck("创建开始菜单快捷方式", 28, 52, 300, true);
            _chkFw = NewCheck("添加 Windows 防火墙放行规则（会弹一次管理员授权）", 28, 78, 480, true);
            _chkRun = NewCheck("安装完成后立即运行 LanTalk", 28, 104, 300, true);
            grpOpt.Controls.AddRange(new Control[] { _chkDesktop, _chkStart, _chkFw, _chkRun });

            Label hint = new Label();
            hint.Text = "提示：装到当前用户目录，不需要管理员权限；只有放行防火墙这一步要同意一次 UAC 授权。";
            hint.SetBounds(16, 300, 528, 20);
            hint.ForeColor = Color.DimGray;

            _pb = new ProgressBar();
            _pb.SetBounds(16, 324, 528, 20);
            _pb.Minimum = 0;
            _pb.Maximum = 100;

            _lblStatus = new Label();
            _lblStatus.Text = "准备就绪。";
            _lblStatus.SetBounds(16, 348, 528, 20);

            GroupBox grpLog = new GroupBox();
            grpLog.Text = "安装日志";
            grpLog.SetBounds(16, 372, 528, 84);
            _txtLog = new TextBox();
            _txtLog.SetBounds(12, 20, 504, 56);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(250, 250, 250);
            _txtLog.Font = new Font("Consolas", 8.5F);
            grpLog.Controls.Add(_txtLog);

            _btnInstall = new Button();
            _btnInstall.Text = "安装";
            _btnInstall.SetBounds(330, 462, 100, 30);
            _btnInstall.TabStop = false;
            _btnInstall.Click += OnInstall;

            _btnQuit = new Button();
            _btnQuit.Text = "退出";
            _btnQuit.SetBounds(440, 462, 104, 30);
            _btnQuit.TabStop = false;
            _btnQuit.Click += delegate(object s, EventArgs e) { Close(); };

            Controls.AddRange(new Control[] { title, sub, grpDir, grpOpt, hint, _pb, _lblStatus, grpLog, _btnInstall, _btnQuit });
            AcceptButton = _btnInstall;
            CancelButton = _btnQuit;

            DpiUi.Apply(this, _s);
        }

        private static CheckBox NewCheck(string text, int x, int y, int w, bool def)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.SetBounds(x, y, w, 22);
            c.Checked = def;
            return c;
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "选择 LanTalk 的安装目录";
                d.ShowNewFolderButton = true;
                string cur = InstallEngine.Normalize(_txtDir.Text);
                try { if (Directory.Exists(cur)) d.SelectedPath = cur; } catch { }
                if (d.ShowDialog(this) == DialogResult.OK) _txtDir.Text = d.SelectedPath;
            }
        }

        private void Append(string line)
        {
            if (_txtLog.TextLength > 100000) _txtLog.Clear();
            _txtLog.AppendText(line + Environment.NewLine);
        }

        private void OnInstall(object sender, EventArgs e)
        {
            if (_busy) return;

            if (_done) { Close(); return; }

            string dir = InstallEngine.Normalize(_txtDir.Text);
            _txtDir.Text = dir;
            if (dir.Length < 4 || !Path.IsPathRooted(dir))
            {
                MessageBox.Show(this, "请填写一个完整的安装路径。", "LanTalk 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (InstallEngine.IsRunning())
            {
                MessageBox.Show(this, "LanTalk 正在运行，请先关闭它再安装。", "LanTalk 安装程序",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            InstallOptions o = new InstallOptions();
            o.Dir = dir;
            o.DesktopShortcut = _chkDesktop.Checked;
            o.StartMenuShortcut = _chkStart.Checked;
            o.FirewallRule = _chkFw.Checked;
            o.LaunchAfter = _chkRun.Checked;
            o.WriteRegistry = true;
            o.ShortcutRootOverride = _args.ShortcutRoot;

            _busy = true;
            _btnInstall.Enabled = false;
            _btnQuit.Enabled = false;
            _btnBrowse.Enabled = false;
            _pb.Value = 0;
            _lblStatus.Text = "正在安装…";
            Append("安装开始，目标目录：" + dir);

            Thread t = new Thread(delegate()
            {
                bool ok = false;
                try
                {
                    ok = InstallEngine.Install(o, delegate(int pct, string msg)
                    {
                        try
                        {
                            BeginInvoke(new Action(delegate()
                            {
                                if (pct >= 0 && pct <= 100) _pb.Value = pct;
                                _lblStatus.Text = msg;
                                Append(msg);
                            }));
                        }
                        catch { }
                    });
                }
                catch (Exception ex)
                {
                    try { BeginInvoke(new Action(delegate() { Append("异常：" + ex.Message); })); }
                    catch { }
                }

                try
                {
                    BeginInvoke(new Action(delegate()
                    {
                        _busy = false;
                        _done = true;
                        _ok = ok;
                        _btnInstall.Enabled = true;
                        _btnQuit.Enabled = true;
                        _btnInstall.Text = ok ? "完成" : "关闭";
                        _lblStatus.Text = ok ? "安装完成，点「完成」关闭。" : "安装失败，请看下面的日志。";
                        Append(ok ? "=== 安装成功 ===" : "=== 安装失败 ===");
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }

    internal sealed class UninstallForm : Form
    {
        private readonly SetupArgs _args;
        private readonly float _s;

        private CheckBox _chkData;
        private ProgressBar _pb;
        private Label _lblStatus;
        private TextBox _txtLog;
        private Button _btnGo, _btnCancel;
        private bool _busy;
        private bool _ok;

        public bool Succeeded { get { return _ok; } }

        public UninstallForm(SetupArgs a)
        {
            _args = a;
            _s = DpiUi.Factor();
            Build();
        }

        private void Build()
        {
            string dir = InstallEngine.InstalledDir();
            if (string.IsNullOrEmpty(dir)) dir = Path.GetDirectoryName(Application.ExecutablePath);

            Text = "卸载 LanTalk";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(470, 300);
            MinimumSize = new Size(486, 338);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            BackColor = SystemColors.Control;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Label title = new Label();
            title.Text = "卸载 LanTalk 局域网语音通话";
            title.SetBounds(20, 16, 430, 28);
            title.Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold);

            Label info = new Label();
            info.Text = "将删除程序文件和快捷方式，并移除「应用和程序」里的登记。\r\n安装目录：" + dir;
            info.SetBounds(20, 52, 430, 40);
            info.ForeColor = Color.DimGray;

            _chkData = new CheckBox();
            _chkData.Text = "同时删除日志文件（LanTalk.log）";
            _chkData.SetBounds(20, 100, 430, 22);
            _chkData.Checked = !_args.KeepData;

            _pb = new ProgressBar();
            _pb.SetBounds(20, 132, 430, 20);
            _pb.Maximum = 100;

            _lblStatus = new Label();
            _lblStatus.Text = "准备就绪。";
            _lblStatus.SetBounds(20, 156, 430, 20);

            _txtLog = new TextBox();
            _txtLog.SetBounds(20, 180, 430, 62);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(250, 250, 250);
            _txtLog.Font = new Font("Consolas", 8.5F);

            _btnGo = new Button();
            _btnGo.Text = "卸载";
            _btnGo.SetBounds(230, 254, 110, 30);
            _btnGo.TabStop = false;
            _btnGo.Click += OnGo;

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.SetBounds(352, 254, 98, 30);
            _btnCancel.TabStop = false;
            _btnCancel.Click += delegate(object s, EventArgs e) { Close(); };

            Controls.AddRange(new Control[] { title, info, _chkData, _pb, _lblStatus, _txtLog, _btnGo, _btnCancel });
            CancelButton = _btnCancel;

            DpiUi.Apply(this, _s);
        }

        private void Append(string line)
        {
            if (_txtLog.TextLength > 100000) _txtLog.Clear();
            _txtLog.AppendText(line + Environment.NewLine);
        }

        private void OnGo(object sender, EventArgs e)
        {
            if (_busy) return;
            if (InstallEngine.IsRunning())
            {
                MessageBox.Show(this, "LanTalk 正在运行，请先关闭它再卸载。", "卸载 LanTalk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, "确定要卸载 LanTalk 吗？", "卸载 LanTalk",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            bool deleteLogs = _chkData.Checked;
            _busy = true;
            _btnGo.Enabled = false;
            _btnCancel.Enabled = false;
            _lblStatus.Text = "正在卸载…";

            Thread t = new Thread(delegate()
            {
                string err = null;
                try
                {
                    err = InstallEngine.Uninstall(deleteLogs, delegate(int pct, string msg)
                    {
                        try
                        {
                            BeginInvoke(new Action(delegate()
                            {
                                if (pct >= 0 && pct <= 100) _pb.Value = pct;
                                _lblStatus.Text = msg;
                                Append(msg);
                            }));
                        }
                        catch { }
                    });
                }
                catch (Exception ex) { err = ex.Message; }

                bool ok = err == null;
                try
                {
                    BeginInvoke(new Action(delegate()
                    {
                        _busy = false;
                        _ok = ok;
                        _btnCancel.Enabled = true;
                        _btnCancel.Text = "关闭";
                        _btnGo.Enabled = false;
                        _lblStatus.Text = ok ? "卸载完成。" : ("卸载失败：" + err);
                        Append(ok ? "=== 卸载完成 ===" : "=== 卸载失败 ===");
                    }));
                }
                catch { }

                if (ok)
                {
                    Thread.Sleep(700);
                    InstallEngine.FinishRemoval(InstallEngine.LastUninstallDir);
                    try { BeginInvoke(new Action(delegate() { Close(); })); }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
