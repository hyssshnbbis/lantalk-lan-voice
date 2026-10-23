// AssemblyInfo.cs —— 程序集元数据（LanTalk.exe 与安装程序共用）
// Windows 的资源管理器"详细信息"和"应用和程序"列表会读这些信息。
using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("LanTalk 局域网语音通话")]
[assembly: AssemblyDescription("两台电脑只要互相 ping 通就能直接通话的局域网语音软件。免服务器、免联网、零第三方依赖。")]
[assembly: AssemblyProduct("LanTalk")]
[assembly: AssemblyCompany("LanTalk")]
[assembly: AssemblyCopyright("Copyright © 2025")]
// 测试版用独立版本号，便于在「详细信息」里一眼区分，也不会和正式版混淆
#if PREVIEW
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]
#else
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]
#endif
[assembly: ComVisible(false)]
