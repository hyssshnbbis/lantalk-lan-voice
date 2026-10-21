// SetupProgram.cs —— 安装程序入口、命令行、静默模式
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LanTalk.Setup
{
    internal sealed class SetupArgs
    {
        public bool Silent;
        public bool Uninstall;
        public bool NoDesktop;
        public bool NoStartMenu;
        public bool NoFirewall;
        public bool NoLaunch;
        public bool NoRegistry;
        public bool KeepData;
        public bool Help;
        public string Dir = "";
        public string ShortcutRoot = "";
        public string LogPath = "";

        public static SetupArgs Parse(string[] a)
        {
            SetupArgs r = new SetupArgs();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                string v = (i + 1 < a.Length) ? a[i + 1] : "";
                switch (k)
                {
                    case "--silent": case "/s": r.Silent = true; break;
                    case "--uninstall": case "/u": r.Uninstall = true; break;
                    case "--no-desktop": r.NoDesktop = true; break;
                    case "--no-startmenu": r.NoStartMenu = true; break;
                    case "--no-firewall": r.NoFirewall = true; break;
                    case "--no-launch": r.NoLaunch = true; break;
                    case "--no-registry": r.NoRegistry = true; break;
                    case "--keep-data": r.KeepData = true; break;
                    case "--help": case "-h": case "/?": r.Help = true; break;
                    case "--dir": r.Dir = v; i++; break;
                    case "--shortcut-root": r.ShortcutRoot = v; i++; break;
                    case "--log": r.LogPath = v; i++; break;
                }
            }
            return r;
        }

        public static string Usage()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LanTalk 安装程序 v" + InstallEngine.Version);
            sb.AppendLine();
            sb.AppendLine("不带参数双击运行 = 图形安装向导。命令行参数：");
            sb.AppendLine("  --silent              静默安装（使用默认选项，不弹界面）");
            sb.AppendLine("  --dir PATH            指定安装目录（默认 %LOCALAPPDATA%\\Programs\\LanTalk）");
            sb.AppendLine("  --no-desktop          不创建桌面快捷方式");
            sb.AppendLine("  --no-startmenu        不创建开始菜单快捷方式");
            sb.AppendLine("  --no-firewall         不添加防火墙规则");
            sb.AppendLine("  --no-launch           安装完成后不自动运行");
            sb.AppendLine("  --uninstall           卸载（配合 --silent 可静默卸载）");
            sb.AppendLine("  --keep-data           卸载时保留日志文件");
            sb.AppendLine("  --log PATH            把过程写入指定日志文件");
            sb.AppendLine("  --shortcut-root PATH  把快捷方式建到这个目录下（自动化测试用）");
            sb.AppendLine("  --help                显示本帮助");
            return sb.ToString();
        }
    }

    /// <summary>GUI 子系统的 exe 想往控制台输出，需要先附着到父进程的控制台。</summary>
    internal static class SetupCon
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();
        private static bool _done;

        public static void Prepare()
        {
            if (_done) return;
            _done = true;
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    if (!AttachConsole(-1)) AllocConsole();
                }
                StreamWriter w = new StreamWriter(Console.OpenStandardOutput());
                w.AutoFlush = true;
                Console.SetOut(w);
            }
            catch { }
        }
    }

    internal static class SetupLog
    {
        private static readonly object _lock = new object();
        private static string _path;
        private static bool _echo;

        public static void Init(string path, bool echo)
        {
            lock (_lock)
            {
                _echo = echo;
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(path, "", Encoding.UTF8);
                        _path = path;
                    }
                    catch { _path = null; }
                }
            }
        }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(_path))
                {
                    try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
                if (_echo) { try { Console.WriteLine("[SETUP] " + msg); } catch { } }
            }
        }
    }

    internal static class SetupProgram
    {
        [STAThread]
        private static int Main(string[] argv)
        {
            SetupArgs a = SetupArgs.Parse(argv);

            // 安装时会把自身复制成 <安装目录>\Uninstall.exe。那份副本必须默认就是"卸载"，
            // 否则用户从「应用和程序」点卸载，它会又安装一遍。
            if (!a.Uninstall && LooksLikeUninstaller()) a.Uninstall = true;

            if (a.Help)
            {
                SetupCon.Prepare();
                Console.WriteLine(SetupArgs.Usage());
                return 0;
            }

            if (a.Silent)
            {
                SetupCon.Prepare();
                string logPath = a.LogPath;
                if (string.IsNullOrEmpty(logPath))
                    logPath = Path.Combine(Path.GetTempPath(), "LanTalkSetup.log");
                SetupLog.Init(logPath, true);
                return RunSilent(a);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (a.Uninstall)
                {
                    UninstallForm uf = new UninstallForm(a);
                    Application.Run(uf);
                    return uf.Succeeded ? 0 : 1;
                }
                SetupForm sf = new SetupForm(a);
                Application.Run(sf);
                return sf.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show("安装程序发生异常：\r\n" + ex.Message, "LanTalk 安装程序",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }
        }

        /// <summary>判断当前运行的 exe 是不是安装目录里那份卸载程序副本。</summary>
        private static bool LooksLikeUninstaller()
        {
            try
            {
                string n = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
                return n != null && n.IndexOf("uninstall", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static int RunSilent(SetupArgs a)
        {
            Action<int, string> progress = delegate(int pct, string msg) { SetupLog.Write(msg); };

            if (a.Uninstall)
            {
                SetupLog.Write("开始静默卸载…");
                string err = InstallEngine.Uninstall(!a.KeepData, progress);
                if (err != null) { SetupLog.Write("卸载失败：" + err); return 1; }
                InstallEngine.FinishRemoval(InstallEngine.LastUninstallDir);
                return 0;
            }

            InstallOptions o = new InstallOptions();
            o.Dir = string.IsNullOrEmpty(a.Dir) ? (InstallEngine.LocateExisting() ?? InstallEngine.DefaultDir()) : a.Dir;
            o.DesktopShortcut = !a.NoDesktop;
            o.StartMenuShortcut = !a.NoStartMenu;
            o.FirewallRule = !a.NoFirewall;
            o.LaunchAfter = !a.NoLaunch;
            o.WriteRegistry = !a.NoRegistry;
            o.ShortcutRootOverride = a.ShortcutRoot;

            SetupLog.Write("开始静默安装到 " + o.Dir);
            bool ok = InstallEngine.Install(o, progress);
            if (!ok) SetupLog.Write("安装失败。");
            return ok ? 0 : 1;
        }
    }
}
