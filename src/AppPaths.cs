// AppPaths.cs —— 正式版与测试版的数据隔离
//
// 用 /define:PREVIEW 编译出来的「测试版」把数据写进 LanTalkPreview，
// 这样试新降噪算法时不会污染正式版的资料、聊天记录和对端缓存。
// 两个版本可以同时装、同时跑，互不干扰。

using System;
using System.IO;

namespace LanTalk
{
    internal static class AppPaths
    {
#if PREVIEW
        public const string DataFolder = "LanTalkPreview";
        public const bool IsPreview = true;
#else
        public const string DataFolder = "LanTalk";
        public const bool IsPreview = false;
#endif

        public static string DataDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolder);
            }
        }

        /// <summary>界面上显示的版本后缀。</summary>
        public static string Suffix { get { return IsPreview ? "  ·  测试版" : ""; } }
    }
}