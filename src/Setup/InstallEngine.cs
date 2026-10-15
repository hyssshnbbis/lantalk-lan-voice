// InstallEngine.cs —— 安装/卸载的实际动作：释放文件、建快捷方式、写注册表、放行防火墙
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanTalk.Setup
{
    /// <summary>内嵌在安装程序里的文件载荷。</summary>
    internal static class Payload
    {
        public static Stream Open(string fileName)
        {
            Assembly asm = typeof(Payload).Assembly;
            string[] all = asm.GetManifestResourceNames();
            string want = "Payload." + fileName;
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(all[i], want, StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(all[i]);
            }
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
                    return asm.GetManifestResourceStream(all[i]);
            }
            return null;
        }

        public static bool Exists(string fileName) { return Open(fileName) != null; }

        /// <summary>把内嵌文件释放到指定路径。返回 false 表示这个载荷不存在。</summary>
        public static bool Extract(string fileName, string destPath)
        {
            using (Stream s = Open(fileName))
            {
                if (s == null) return false;
                string dir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (FileStream fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                }
            }
            return true;
        }
    }

    internal sealed class InstallOptions
    {
        public string Dir;
        public bool DesktopShortcut = true;
        public bool StartMenuShortcut = true;
        public bool FirewallRule = true;
        public bool LaunchAfter = true;
        public bool WriteRegistry = true;
        /// <summary>仅用于自动化测试：把快捷方式重定向到这个目录，避免动到真实的桌面和开始菜单。</summary>
        public string ShortcutRootOverride;
    }

    internal static class InstallEngine
    {
        public const string AppName = "LanTalk";
        public const string DisplayName = "LanTalk 局域网语音通话";
        public const string Version = "1.0.0";
        public const string Publisher = "LanTalk";
        public const int DefaultPort = 47800;
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanTalk";
        public const string FirewallRuleName = "LanTalk 局域网语音通话 (UDP)";
        public const string UninstallerName = "Uninstall.exe";

        /// <summary>随安装一起释放的文件（LanTalk.exe 由内嵌载荷提供）。</summary>
        private static readonly string[] PayloadFiles = new string[] { "LanTalk.exe", "README.md", "Allow-Firewall.ps1" };
        /// <summary>卸载时会清理的文件名白名单。绝不会去动不在这个清单里的东西。</summary>
        private static readonly string[] OwnedFiles = new string[] {
            "LanTalk.exe", "README.md", "Allow-Firewall.ps1", "LanTalk.log", UninstallerName
        };

        public static string DefaultDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\LanTalk");
        }

        public static string Normalize(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return DefaultDir();
            string d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"'));
            try { d = Path.GetFullPath(d); } catch { }
            return d.TrimEnd('\\');
        }

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName(AppName).Length > 0; }
            catch { return false; }
        }

        public static string InstalledDir()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("InstallLocation");
                        if (v != null && v.ToString().Length > 0) return v.ToString();
                    }
                }
            }
            catch { }
            return null;
        }

        // ==================== 安装 ====================

        public static bool Install(InstallOptions o, Action<int, string> progress)
        {
            string dir = Normalize(o.Dir);
            o.Dir = dir;
            Step(progress, 2, "安装目录：" + dir);

            if (IsRunning())
            {
                Step(progress, 4, "检测到 LanTalk 正在运行，请先关闭它。");
                return false;
            }

            // 1) 建目录并确认可写
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                Step(progress, 5, "无法写入安装目录：" + ex.Message);
                return false;
            }
            Step(progress, 8, "目录准备完成。");

            // 2) 释放文件
            int i = 0;
            foreach (string f in PayloadFiles)
            {
                i++;
                int pct = 10 + (int)(45.0 * i / PayloadFiles.Length);
                string dest = Path.Combine(dir, f);
                try
                {
                    if (!Payload.Extract(f, dest))
                    {
                        Step(progress, pct, "安装包内缺少 " + f + "，安装中止。");
                        return false;
                    }
                    Step(progress, pct, "已释放 " + f);
                }
                catch (Exception ex)
                {
                    Step(progress, pct, "释放 " + f + " 失败：" + ex.Message);
                    return false;
                }
            }

            // 3) 复制自身作为卸载程序
            try
            {
                string self = Application.ExecutablePath;
                string uninst = Path.Combine(dir, UninstallerName);
                if (!string.Equals(self, uninst, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, uninst, true);
                    Step(progress, 58, "已生成卸载程序 " + UninstallerName);
                }
            }
            catch (Exception ex)
            {
                Step(progress, 58, "生成卸载程序失败（不影响使用）：" + ex.Message);
            }

            // 4) 快捷方式
            string exePath = Path.Combine(dir, "LanTalk.exe");
            string icon = exePath + ",0";
            if (o.DesktopShortcut)
            {
                string p = DesktopShortcutPath(o);
                try { CreateShortcut(p, exePath, dir, icon, DisplayName); Step(progress, 66, "已创建桌面快捷方式"); }
                catch (Exception ex) { Step(progress, 66, "桌面快捷方式创建失败：" + ex.Message); }
            }
            if (o.StartMenuShortcut)
            {
                string p = StartMenuShortcutPath(o);
                try { CreateShortcut(p, exePath, dir, icon, DisplayName); Step(progress, 72, "已创建开始菜单快捷方式"); }
                catch (Exception ex) { Step(progress, 72, "开始菜单快捷方式创建失败：" + ex.Message); }
            }

            // 5) 注册到"应用和程序"
            if (o.WriteRegistry)
            {
                try { WriteUninstallEntry(dir); Step(progress, 80, "已注册到「应用和程序」列表"); }
                catch (Exception ex) { Step(progress, 80, "写入注册表失败（不影响使用）：" + ex.Message); }
            }

            // 6) 防火墙
            if (o.FirewallRule)
            {
                string msg;
                bool ok = AddFirewallRule(DefaultPort, out msg);
                Step(progress, 92, ok ? "已放行防火墙入站 UDP " + DefaultPort : "未添加防火墙规则：" + msg);
            }
            else
            {
                Step(progress, 92, "已跳过防火墙设置（第一次运行 LanTalk 时 Windows 会自己弹提示）。");
            }

            // 7) 启动
            if (o.LaunchAfter)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exePath);
                    psi.WorkingDirectory = dir;
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    Step(progress, 98, "已启动 LanTalk");
                }
                catch (Exception ex) { Step(progress, 98, "启动失败：" + ex.Message); }
            }

            Step(progress, 100, "安装完成。");
            return true;
        }

        public static string DesktopShortcutPath(InstallOptions o)
        {
            if (!string.IsNullOrEmpty(o.ShortcutRootOverride))
                return Path.Combine(o.ShortcutRootOverride, "Desktop", DisplayName + ".lnk");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                DisplayName + ".lnk");
        }

        public static string StartMenuShortcutPath(InstallOptions o)
        {
            if (!string.IsNullOrEmpty(o.ShortcutRootOverride))
                return Path.Combine(o.ShortcutRootOverride, "StartMenu", "LanTalk", DisplayName + ".lnk");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                "LanTalk", DisplayName + ".lnk");
        }

        // ==================== 卸载 ====================

        /// <summary>最近一次 Uninstall 处理过的安装目录，供 FinishRemoval 使用。</summary>
        public static string LastUninstallDir { get; private set; }

        /// <summary>返回 null 表示成功，否则返回错误说明。</summary>
        public static string Uninstall(bool deleteLogs, Action<int, string> progress)
        {
            string dir = InstalledDir();
            if (string.IsNullOrEmpty(dir))
            {
                try { dir = Path.GetDirectoryName(Application.ExecutablePath); }
                catch { }
            }
            dir = Normalize(dir);
            LastUninstallDir = dir;
            Step(progress, 5, "安装目录：" + dir);

            // 安全闸门：只有确认这个目录确实是 LanTalk 的安装目录，才允许删除里面的文件
            bool safeDir = false;
            try
            {
                safeDir = Directory.Exists(dir) && File.Exists(Path.Combine(dir, "LanTalk.exe"));
            }
            catch { }
            if (!safeDir) Step(progress, 8, "该目录里没有 LanTalk.exe，只清理快捷方式和注册信息，不动文件。");

            if (IsRunning())
            {
                Step(progress, 10, "检测到 LanTalk 正在运行，请先关闭它再卸载。");
                return "LanTalk 正在运行";
            }

            // 1) 快捷方式
            InstallOptions o = new InstallOptions();
            RemoveFile(DesktopShortcutPath(o));
            RemoveFile(StartMenuShortcutPath(o));
            try
            {
                string smDir = Path.GetDirectoryName(StartMenuShortcutPath(o));
                if (Directory.Exists(smDir) && Directory.GetFileSystemEntries(smDir).Length == 0)
                    Directory.Delete(smDir, false);
            }
            catch { }
            Step(progress, 30, "已清理快捷方式");

            // 2) 注册表
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                Step(progress, 45, "已移除「应用和程序」里的登记");
            }
            catch (Exception ex) { Step(progress, 45, "清理注册表失败：" + ex.Message); }

            // 3) 文件：只删自己名下的文件，绝不递归删目录
            if (safeDir)
            {
                int n = 0;
                foreach (string f in OwnedFiles)
                {
                    if (!deleteLogs && string.Equals(f, "LanTalk.log", StringComparison.OrdinalIgnoreCase)) continue;
                    string p = Path.Combine(dir, f);
                    if (string.Equals(f, UninstallerName, StringComparison.OrdinalIgnoreCase)) continue;  // 自己最后删
                    if (File.Exists(p)) { RemoveFile(p); n++; }
                }
                Step(progress, 75, "已删除 " + n + " 个文件");

                try
                {
                    string[] left = Directory.GetFileSystemEntries(dir);
                    if (left.Length == 0) { Directory.Delete(dir, false); Step(progress, 85, "安装目录已移除"); }
                    else Step(progress, 85, "目录里还有 " + left.Length + " 个非本程序的文件，已保留：" + dir);
                }
                catch (Exception ex) { Step(progress, 85, "目录清理：" + ex.Message); }
            }

            // 4) 防火墙规则（需要管理员，失败不影响卸载）
            string msg;
            RemoveFirewallRule(out msg);
            Step(progress, 95, "防火墙规则：" + msg);

            Step(progress, 100, "卸载完成。");
            return null;
        }

        /// <summary>安排删除安装目录里的卸载程序，并在目录为空时移除目录。</summary>
        public static void FinishRemoval(string installDir)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir)) return;
                installDir = Normalize(installDir);
                string target = Path.Combine(installDir, UninstallerName);
                string self = Application.ExecutablePath;

                // 安全闸门：绝不删除安装目录以外的任何东西。
                // 只有当前进程本身就在安装目录里（即用户点的是那份卸载程序副本）才需要延迟自删。
                bool selfInside = self != null &&
                    self.StartsWith(installDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

                if (!selfInside)
                {
                    // 当前运行的是别处的安装包，安装目录里的卸载程序没在被占用，直接删
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    try
                    {
                        if (Directory.Exists(installDir) && Directory.GetFileSystemEntries(installDir).Length == 0)
                        {
                            Directory.Delete(installDir, false);
                            return;
                        }
                    }
                    catch { }
                }

                // 正在运行的文件删不掉，交给一个延迟的 cmd 处理；rmdir 不带 /s，只在目录为空时才生效
                string cmd = "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + target + "\" & rmdir \"" + installDir + "\"";
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", cmd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch { }
        }

        private static void RemoveFile(string p)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // ==================== 快捷方式 / 注册表 / 防火墙 ====================

        private static void CreateShortcut(string lnkPath, string target, string workDir, string icon, string desc)
        {
            string parent = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent)) Directory.CreateDirectory(parent);

            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new Exception("系统没有可用的 WScript.Shell");
            object shell = null;
            object lnk = null;
            try
            {
                shell = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { icon });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { desc });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                if (lnk != null) { try { Marshal.FinalReleaseComObject(lnk); } catch { } }
                if (shell != null) { try { Marshal.FinalReleaseComObject(shell); } catch { } }
            }
        }

        private static void WriteUninstallEntry(string dir)
        {
            string exe = Path.Combine(dir, "LanTalk.exe");
            string uninst = Path.Combine(dir, UninstallerName);
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", DisplayName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall --silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = 0;
                try
                {
                    foreach (string f in Directory.GetFiles(dir)) size += new FileInfo(f).Length;
                }
                catch { }
                k.SetValue("EstimatedSize", (int)(size / 1024) + 1, RegistryValueKind.DWord);
            }
        }

        public static bool AddFirewallRule(int port, out string message)
        {
            string args = "advfirewall firewall add rule name=\"" + FirewallRuleName + "\" " +
                          "dir=in action=allow protocol=UDP localport=" + port + " profile=any enable=yes";
            return RunNetsh(args, true, out message);
        }

        public static bool RemoveFirewallRule(out string message)
        {
            string args = "advfirewall firewall delete rule name=\"" + FirewallRuleName + "\"";
            // 卸载时不弹 UAC：拿不到权限就跳过并给出提示，绝不阻塞卸载流程
            return RunNetsh(args, false, out message);
        }

        public static bool IsElevated()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// allowPrompt=false 时绝不会弹 UAC（用于卸载这类不该被打断的流程）。
        /// </summary>
        private static bool RunNetsh(string args, bool allowPrompt, out string message)
        {
            bool alreadyAdmin = IsElevated();

            if (alreadyAdmin)
            {
                try
                {
                    ProcessStartInfo p1 = new ProcessStartInfo("netsh.exe", args);
                    p1.UseShellExecute = false;
                    p1.CreateNoWindow = true;
                    p1.WindowStyle = ProcessWindowStyle.Hidden;
                    using (Process p = Process.Start(p1))
                    {
                        p.WaitForExit();
                        if (p.ExitCode == 0) { message = "成功"; return true; }
                        message = "netsh 返回 " + p.ExitCode;
                        return false;
                    }
                }
                catch (Exception ex) { message = ex.Message; return false; }
            }

            if (!allowPrompt)
            {
                message = "需要管理员权限，已跳过（如需彻底清除，请以管理员运行 Allow-Firewall.ps1 -Remove）";
                return false;
            }

            ProcessStartInfo psi = new ProcessStartInfo("netsh.exe", args);
            psi.UseShellExecute = true;
            psi.Verb = "runas";                 // 触发一次 UAC 提权
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            try
            {
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0) { message = "成功"; return true; }
                    message = "netsh 返回 " + p.ExitCode;
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                message = ex.NativeErrorCode == 1223 ? "用户取消了管理员授权" : ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        private static void Step(Action<int, string> progress, int pct, string msg)
        {
            if (progress != null) { try { progress(pct, msg); } catch { } }
        }
    }
}
