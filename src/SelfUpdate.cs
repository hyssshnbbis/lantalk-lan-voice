// SelfUpdate.cs —— 从更新包（zip）里替换本程序
//
// 两种包体分工：
//   更新包  LanTalk-x.y.z-update.zip  只含 LanTalk.exe，拖进窗口就地替换，秒级完成
//   完整包  LanTalk-Setup-x.y.z.exe   安装程序，全新安装用
//
// 运行中的 exe 不能被覆盖，所以用"改名 + 换新 + 重启"的经典做法：
//   1. 解压出新 exe 到临时目录
//   2. 把正在运行的 LanTalk.exe 改名成 LanTalk.exe.old
//   3. 把新 exe 拷成 LanTalk.exe
//   4. 重启自己；下次启动时把 .old 删掉
// 任何一步失败都回滚，保证不会把自己搞成打不开的状态。
//
// 解压自己实现（DeflateStream 在 .NET 4.0 就有），不依赖 System.IO.Compression.ZipFile。

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace LanTalk
{
    internal static class SelfUpdate
    {
        /// <summary>更新包内主程序的固定文件名。</summary>
        public const string PayloadName = "LanTalk.exe";

        /// <summary>改名后留在这里，下次启动清掉。</summary>
        public static string BackupPath(string exePath) { return exePath + ".old"; }

        /// <summary>启动时清理上次更新留下的 .old（删不掉就留着，不影响运行）。</summary>
        public static void CleanupOld(string exePath)
        {
            try
            {
                string bak = BackupPath(exePath);
                if (File.Exists(bak)) File.Delete(bak);
            }
            catch { }
        }

        /// <summary>
        /// 把 zip 里的 LanTalk.exe 换到 exePath。成功返回 true 并给出新版本描述。
        /// 只换文件，不负责重启。
        /// </summary>
        public static bool ApplyZip(string zipPath, string exePath, out string message)
        {
            message = "";
            string tempDir = null;
            try
            {
                if (!File.Exists(zipPath)) { message = "找不到更新包"; return false; }

                tempDir = Path.Combine(Path.GetTempPath(), "LanTalk-upd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tempDir);

                string newExe = ExtractPayload(zipPath, tempDir);
                if (newExe == null) { message = "更新包里没有 " + PayloadName; return false; }

                FileInfo fi = new FileInfo(newExe);
                if (fi.Length < 100 * 1024 || fi.Length > 20 * 1024 * 1024)
                {
                    message = "更新包里的主程序大小异常（" + (fi.Length / 1024) + " KB），已拒绝";
                    return false;
                }

                // 先备份正在运行的 exe，再换上新的
                string bak = BackupPath(exePath);
                try { if (File.Exists(bak)) File.Delete(bak); } catch { }

                bool moved = false;
                try
                {
                    File.Move(exePath, bak);          // 运行中的 exe 允许改名，不允许覆盖
                    moved = true;
                    File.Copy(newExe, exePath, true);
                }
                catch (Exception ex)
                {
                    // 回滚：把原来的名字换回去
                    if (moved)
                    {
                        try { if (File.Exists(exePath)) File.Delete(exePath); File.Move(bak, exePath); } catch { }
                    }
                    message = "替换失败：" + ex.Message;
                    return false;
                }

                message = "已更新到 " + fi.Length / 1024 + " KB 的新版本";
                return true;
            }
            catch (Exception ex)
            {
                message = "更新失败：" + ex.Message;
                return false;
            }
            finally
            {
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>从 zip 里找出 LanTalk.exe 并解出来，返回解压后的路径。</summary>
        private static string ExtractPayload(string zipPath, string tempDir)
        {
            byte[] zip = File.ReadAllBytes(zipPath);

            // 1) 找中央目录结尾记录 EOCD（签名 0x06054b50），从尾部往前搜
            int eocd = -1;
            for (int i = zip.Length - 22; i >= 0 && i >= zip.Length - 22 - 65535; i--)
            {
                if (zip[i] == 0x50 && zip[i + 1] == 0x4b && zip[i + 2] == 0x05 && zip[i + 3] == 0x06) { eocd = i; break; }
            }
            if (eocd < 0) return null;

            int count = BitConverter.ToUInt16(zip, eocd + 10);
            int cdOffset = (int)BitConverter.ToUInt32(zip, eocd + 16);

            int p = cdOffset;
            for (int n = 0; n < count; n++)
            {
                if (p + 46 > zip.Length) break;
                if (!(zip[p] == 0x50 && zip[p + 1] == 0x4b && zip[p + 2] == 0x01 && zip[p + 3] == 0x02)) break;

                int method = BitConverter.ToUInt16(zip, p + 10);
                int compSize = (int)BitConverter.ToUInt32(zip, p + 20);
                int nameLen = BitConverter.ToUInt16(zip, p + 28);
                int extraLen = BitConverter.ToUInt16(zip, p + 30);
                int commentLen = BitConverter.ToUInt16(zip, p + 32);
                int localOff = (int)BitConverter.ToUInt32(zip, p + 42);

                string name = Encoding.UTF8.GetString(zip, p + 46, nameLen);
                if (name.EndsWith(PayloadName, StringComparison.OrdinalIgnoreCase))
                {
                    byte[] data = ReadEntry(zip, localOff, compSize, method);
                    if (data == null) return null;
                    string outPath = Path.Combine(tempDir, PayloadName);
                    File.WriteAllBytes(outPath, data);
                    return outPath;
                }
                p += 46 + nameLen + extraLen + commentLen;
            }
            return null;
        }

        /// <summary>按本地文件头定位数据区，再按压缩方式解出来。</summary>
        private static byte[] ReadEntry(byte[] zip, int localOff, int compSize, int method)
        {
            if (localOff + 30 > zip.Length) return null;
            if (!(zip[localOff] == 0x50 && zip[localOff + 1] == 0x4b && zip[localOff + 2] == 0x03 && zip[localOff + 3] == 0x04)) return null;

            int nameLen = BitConverter.ToUInt16(zip, localOff + 26);
            int extraLen = BitConverter.ToUInt16(zip, localOff + 28);
            int dataOff = localOff + 30 + nameLen + extraLen;
            if (dataOff + compSize > zip.Length || compSize <= 0) return null;

            if (method == 0)
            {
                byte[] store = new byte[compSize];
                Buffer.BlockCopy(zip, dataOff, store, 0, compSize);
                return store;
            }
            if (method != 8) return null;   // 只支持 stored / deflate

            using (MemoryStream input = new MemoryStream(zip, dataOff, compSize, false))
            using (DeflateStream ds = new DeflateStream(input, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream())
            {
                byte[] buf = new byte[64 * 1024];
                int total = 0;
                while (true)
                {
                    int r = ds.Read(buf, 0, buf.Length);
                    if (r <= 0) break;
                    outMs.Write(buf, 0, r);
                    total += r;
                    if (total > 20 * 1024 * 1024) return null;   // 防解压炸弹
                }
                return outMs.ToArray();
            }
        }

        /// <summary>判断一个拖进来的文件是不是更新包。</summary>
        public static bool LooksLikeUpdateZip(string path)
        {
            return path != null && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }
    }
}
