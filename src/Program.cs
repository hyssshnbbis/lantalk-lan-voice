// Program.cs —— 程序入口、命令行参数、日志
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LanTalk
{
    internal sealed class Args
    {
        public bool SelfTest;
        public bool Headless;
        /// <summary>降噪对比：秒数（0 = 不启用）。带 --record-ab-sim 时用合成信号，不占麦克风。</summary>
        public int RecordAb;
        public bool RecordAbSim;
        public string RecordAbOut;
        public bool AutoCall;
        public bool AutoAnswer;
        public bool NullAudio;
        public bool Help;
        public int LocalPort = 47800;
        public int PeerPort = 47800;
        public string BindIp = "0.0.0.0";
        public string PeerIp = "";
        public int DurationSec = 10;
        public VoiceCodec Codec = VoiceCodec.Pcm16;
        public string LogPath = "";
        public string Name = "";
        /// <summary>降噪档位（默认中）。</summary>
        public DenoiseLevel Denoise = DenoiseLevel.Mid;
        public bool DenoiseSpecified;
        /// <summary>
        /// 降噪引擎：true=WebRTC NS 移植版（默认，第二阶段成果），
        /// false=第一版轻量实现（可用 --denoise-engine classic 切回）。
        /// </summary>
        public bool UseNsEngine = true;
        /// <summary>大于 0 表示进会议室模式（房间号）。</summary>
        public int Room;
        /// <summary>会议室里手工补的成员，形如 192.168.1.5:47800。</summary>
        public List<string> ManualPeers = new List<string>();

        public static Args Parse(string[] a)
        {
            Args r = new Args();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                string v = (i + 1 < a.Length) ? a[i + 1] : "";
                switch (k)
                {
                    case "--selftest": r.SelfTest = true; break;
                    case "--record-ab":
                        r.RecordAb = Int(v, 10); i++;
                        if (r.RecordAb < 3) r.RecordAb = 3;
                        if (r.RecordAb > 60) r.RecordAb = 60;
                        break;
                    case "--record-ab-sim": r.RecordAbSim = true; break;
                    case "--record-ab-out": r.RecordAbOut = v; i++; break;
                    case "--headless": r.Headless = true; break;
                    case "--auto-call": r.AutoCall = true; break;
                    case "--auto-answer": r.AutoAnswer = true; break;
                    case "--null-audio": r.NullAudio = true; break;
                    case "--help": case "-h": case "/?": r.Help = true; break;
                    case "--local-port": r.LocalPort = Int(v, r.LocalPort); i++; break;
                    case "--bind": r.BindIp = v; i++; break;
                    case "--peer-port": r.PeerPort = Int(v, r.PeerPort); i++; break;
                    case "--duration": r.DurationSec = Int(v, r.DurationSec); i++; break;
                    case "--codec": r.Codec = CodecUtil.Parse(v); i++; break;
                    case "--log": r.LogPath = v; i++; break;
                    case "--name": r.Name = v; i++; break;
                    case "--room": r.Room = Int(v, 0); i++; break;
                    case "--denoise-engine":
                        {
                            i++;
                            string eng = (v ?? "").Trim().ToLowerInvariant();
                            r.UseNsEngine = !(eng == "classic" || eng == "light");
                            if (eng != "ns" && eng != "webrtc" && eng != "classic" && eng != "light")
                                throw new Exception("--denoise-engine 只接受 classic 或 ns");
                            break;
                        }
                    case "--denoise":
                        {
                            i++;
                            DenoiseLevel dl;
                            if (DenoiseLevels.TryParse(v, out dl)) { r.Denoise = dl; r.DenoiseSpecified = true; }
                            break;
                        }
                    case "--peers":
                        {
                            i++;
                            string[] parts = v.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                            for (int j = 0; j < parts.Length; j++)
                            {
                                string s = parts[j].Trim();
                                if (s.Length > 0) r.ManualPeers.Add(s);
                            }
                            break;
                        }
                    case "--peer":
                        {
                            i++;
                            string ip = v;
                            int c = v.LastIndexOf(':');
                            if (c > 0)
                            {
                                ip = v.Substring(0, c);
                                r.PeerPort = Int(v.Substring(c + 1), r.PeerPort);
                            }
                            r.PeerIp = ip;
                            break;
                        }
                }
            }
            return r;
        }

        /// <summary>
        /// 实际生效的降噪档位。内置测试音源（--null-audio）不是语音，
        /// 默认不降噪，免得把确定性测试信号改掉、影响端到端样本校验。
        /// </summary>
        public DenoiseLevel EffectiveDenoise()
        {
            if (DenoiseSpecified) return Denoise;
            if (NullAudio) return DenoiseLevel.Off;
            return Denoise;
        }

        private static int Int(string s, int dflt)
        {
            int r;
            if (int.TryParse(s, out r)) return r;
            return dflt;
        }

        public static string Usage()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LanTalk —— 局域网语音通话（零依赖，UDP 点对点）");
            sb.AppendLine();
            sb.AppendLine("直接双击运行不带参数即为图形界面。命令行参数：");
            sb.AppendLine("  --local-port N     本机监听端口（默认 47800）");
            sb.AppendLine("  --bind IP          绑定到指定本机地址（默认 0.0.0.0 全部）");
            sb.AppendLine("  --peer IP[:PORT]   对方地址，可只填 IP");
            sb.AppendLine("  --peer-port N      对方端口（默认 47800）");
            sb.AppendLine("  --codec pcm|ulaw   语音编码（默认 pcm）");
            sb.AppendLine("  --denoise 档位     降噪：off|low|mid|high 或 关闭|弱|中|强（默认 mid）");
            sb.AppendLine("  --denoise-engine   引擎：ns（默认，WebRTC NS 移植版）| classic（第一版轻量实现）");
            sb.AppendLine("  --selftest         运行自检（设备、编解码、抖动缓冲、UDP 协议）");
            sb.AppendLine("  --record-ab 秒数   降噪对比：录 N 秒，两个引擎各出一份 wav + 客观指标（默认 10 秒）");
            sb.AppendLine("  --record-ab-sim    同上但用合成语音（不占麦克风，用于验证）");
            sb.AppendLine("  --record-ab-out    对比结果的输出目录");
            sb.AppendLine("  --headless         不开界面（配合下面几个参数可做自动化验证）");
            sb.AppendLine("  --auto-call        启动后自动呼叫 --peer");
            sb.AppendLine("  --auto-answer      自动接听来电");
            sb.AppendLine("  --duration N       无界面模式下运行 N 秒后退出");
            sb.AppendLine("  --null-audio       用内置测试音源/空放音替代真实声卡");
            sb.AppendLine("  --log PATH         日志文件路径");
            sb.AppendLine("  --name 名称        局域网发现时显示的名字");
            sb.AppendLine();
            sb.AppendLine("多人会议（房间号就是一个 UDP 端口，同一房间请填同一个号码）：");
            sb.AppendLine("  --room N           进入会议室模式，房间号 N");
            sb.AppendLine("  --peers A:N,B:N    手工补成员（广播被网络挡住时用），可只在一端添加");
            return sb.ToString();
        }
    }

    /// <summary>无界面模式下把输出接到父控制台（exe 编译为 GUI 子系统，默认没有控制台）。</summary>
    internal static class Con
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        private const int ATTACH_PARENT_PROCESS = -1;
        private static bool _done;

        public static void Prepare()
        {
            if (_done) return;
            _done = true;
            try
            {
                if (!Console.IsOutputRedirected)
                {
                    if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
                }
                StreamWriter w = new StreamWriter(Console.OpenStandardOutput());
                w.AutoFlush = true;
                Console.SetOut(w);
                StreamWriter e = new StreamWriter(Console.OpenStandardError());
                e.AutoFlush = true;
                Console.SetError(e);
            }
            catch { }
        }
    }

    internal static class Log
    {
        private const int MaxLines = 4000;
        private static readonly object _lock = new object();
        private static readonly List<string> _lines = new List<string>();
        private static string _path;
        private static bool _echo;

        public static event Action<string> LineAdded;

        public static string Path { get { return _path; } }

        public static void Init(string path, bool echoConsole)
        {
            lock (_lock)
            {
                _path = path;
                _echo = echoConsole;
                if (!string.IsNullOrEmpty(_path))
                {
                    try
                    {
                        string dir = System.IO.Path.GetDirectoryName(_path);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(_path, "", Encoding.UTF8);
                    }
                    catch { _path = null; }
                }
            }
        }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            Action<string> h;
            lock (_lock)
            {
                _lines.Add(line);
                if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
                if (!string.IsNullOrEmpty(_path))
                {
                    try { File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
                if (_echo)
                {
                    try { Console.WriteLine(line); } catch { }
                }
                h = LineAdded;
            }
            if (h != null) { try { h(line); } catch { } }
        }

        public static string[] Snapshot()
        {
            lock (_lock) return _lines.ToArray();
        }
    }

    internal static class Program
    {
        public static string AppDir
        {
            get
            {
                try { return System.IO.Path.GetDirectoryName(Application.ExecutablePath); }
                catch { return Environment.CurrentDirectory; }
            }
        }

        [STAThread]
        private static int Main(string[] argv)
        {
            Args a = Args.Parse(argv);

            if (a.Help)
            {
                Con.Prepare();
                Console.WriteLine(Args.Usage());
                return 0;
            }

            string logPath = a.LogPath;
            if (string.IsNullOrEmpty(logPath))
            {
                logPath = System.IO.Path.Combine(AppDir, "LanTalk.log");
            }
            Log.Init(logPath, a.Headless || a.SelfTest);

            if (a.SelfTest)
            {
                Con.Prepare();
                return SelfTest.Run(a);
            }
            if (a.RecordAbSim || a.RecordAb > 0)
            {
                return RecordAb.Run(a);
            }
            if (a.Headless)
            {
                Con.Prepare();
                return Headless.RunConference(a);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm(a));
            }
            catch (Exception ex)
            {
                Log.Write("界面异常：" + ex);
                try
                {
                    MessageBox.Show("程序发生异常：\r\n" + ex.Message, "LanTalk",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                return 1;
            }
            return 0;
        }
    }
}
