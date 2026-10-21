// MainForm.Settings.cs —— MainForm 的 设置页与更新 部分（partial class）
//
// 把这块单独拆出来，是为了以后改聊天/设置时不用在 1500 行里翻。
// 字段、构建、DPI 缩放仍在 MainForm.cs。
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace LanTalk
{
    internal sealed partial class MainForm
    {
        // ==================== 设置页 ====================
        private void BuildSettings()
        {
            _settings = new SettingsPanel(0, TopBar.BarHeight, 1120, 780);
            _settings.BackClicked += delegate { ShowMainView(); };
            _settings.PackageDropped += delegate(object s, string file) { OnUpdatePackageDropped(file); };

            _cardDev = _settings.Device;
            _cardMode = _settings.Mode;
        }

        private void ShowSettings()
        {
            if (_viewMain == null || _settings == null) return;
            _viewMain.Visible = false;
            _settings.Visible = true;
            _settings.ShowPageSettings();
            Log.Write("打开设置页。");
        }

        private void ShowMainView()
        {
            if (_viewMain == null || _settings == null) return;
            _settings.Visible = false;
            _viewMain.Visible = true;
        }

        /// <summary>用户把更新包拖进来了：确认后启动安装程序并退出自己。</summary>
        private void OnUpdatePackageDropped(string file)
        {
            try
            {
                // 更新包（zip）：就地替换本程序然后重启
                if (SelfUpdate.LooksLikeUpdateZip(file))
                {
                    FileInfo zi = new FileInfo(file);
                    DialogResult rz = MessageBox.Show(this,
                        "检测到更新包：\r\n" + zi.Name + "（" + (zi.Length / 1024) + " KB）\r\n\r\n"
                        + "确认后会把 LanTalk.exe 换成更新包里的版本，然后自动重启。继续吗？",
                        "安装更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (rz != DialogResult.OK) return;

                    string exePath = Application.ExecutablePath;
                    string msg;
                    Log.Write("开始就地更新：" + file);
                    if (!SelfUpdate.ApplyZip(file, exePath, out msg))
                    {
                        Log.Write("更新失败：" + msg);
                        MessageBox.Show(this, msg, "更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    Log.Write("更新成功：" + msg + "，正在重启。");
                    try { Process.Start(exePath); } catch { }
                    Close();
                    return;
                }

                // 完整安装包（exe）：交给安装程序
                FileInfo fi = new FileInfo(file);
                DialogResult r = MessageBox.Show(this,
                    "检测到完整安装包：\r\n" + fi.Name + "（" + (fi.Length / 1024) + " KB）\r\n\r\n"
                    + "确认后 LanTalk 会退出并启动安装程序。继续吗？",
                    "安装更新", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;

                Log.Write("准备安装更新：" + file);
                Process.Start(file);
                Close();
            }
            catch (Exception ex)
            {
                Log.Write("启动更新失败：" + ex.Message);
                MessageBox.Show(this, "更新失败：" + ex.Message, "更新",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
