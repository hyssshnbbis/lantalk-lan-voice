// Mm.cs —— winmm.dll (waveIn / waveOut) 互操作与音频设备封装
// 目标：.NET Framework 4.0 / C# 5 语法，零第三方依赖。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LanTalk
{
    /// <summary>音频设备相关错误。</summary>
    internal sealed class AudioException : Exception
    {
        public readonly int Code;
        public AudioException(string what, int code)
            : base(what + "（错误码 " + code + "：" + Mmsyserr.Text(code) + "）")
        {
            Code = code;
        }
    }

    /// <summary>MMSYSERR_* / WAVERR_* 错误码中文说明。</summary>
    internal static class Mmsyserr
    {
        public const int NOERROR = 0;
        public const int WAVERR_BADFORMAT = 32;
        public const int WAVERR_STILLPLAYING = 33;
        public const int WAVERR_UNPREPARED = 34;
        public const int WAVERR_SYNC = 35;

        public static string Text(int code)
        {
            switch (code)
            {
                case 0: return "成功";
                case 1: return "未指明错误";
                case 2: return "设备 ID 越界";
                case 3: return "驱动程序未启用";
                case 4: return "设备已被占用";
                case 5: return "句柄无效";
                case 6: return "没有驱动程序";
                case 7: return "内存不足";
                case 8: return "不支持该功能";
                case 9: return "错误编号无效";
                case 10: return "标志无效";
                case 11: return "参数无效";
                case 12: return "句柄正忙";
                case 13: return "别名无效";
                case 14: return "注册表数据库损坏";
                case 15: return "找不到注册表项";
                case 16: return "读取注册表出错";
                case 17: return "写入注册表出错";
                case 18: return "删除注册表项出错";
                case 19: return "找不到注册表值";
                case 20: return "驱动不支持回调";
                case 32: return "设备不支持该音频格式（采样率/声道/位宽）";
                case 33: return "缓冲区仍在播放中";
                case 34: return "缓冲区未准备";
                case 35: return "同步调用失败";
                default: return "未知错误";
            }
        }
    }

    internal static class Mm
    {
        public const int WAVE_MAPPER = -1;
        public const ushort WAVE_FORMAT_PCM = 1;

        public const uint CALLBACK_NULL = 0x00000000;
        public const uint CALLBACK_FUNCTION = 0x00030000;

        public const uint WHDR_DONE = 0x00000001;
        public const uint WHDR_PREPARED = 0x00000002;
        public const uint WHDR_INQUEUE = 0x00000010;

        public const uint WIM_OPEN = 0x03BE;
        public const uint WIM_CLOSE = 0x03BF;
        public const uint WIM_DATA = 0x03C0;
        public const uint WOM_OPEN = 0x03BB;
        public const uint WOM_CLOSE = 0x03BC;
        public const uint WOM_DONE = 0x03BD;

        public const uint WAVE_FORMAT_QUERY = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;

            public static WAVEFORMATEX Pcm(int rate, int channels, int bits)
            {
                WAVEFORMATEX f = new WAVEFORMATEX();
                f.wFormatTag = WAVE_FORMAT_PCM;
                f.nChannels = (ushort)channels;
                f.nSamplesPerSec = (uint)rate;
                f.wBitsPerSample = (ushort)bits;
                f.nBlockAlign = (ushort)(channels * bits / 8);
                f.nAvgBytesPerSec = (uint)(rate * f.nBlockAlign);
                f.cbSize = 0;
                return f;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEINCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPS
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            // WAVEOUTCAPS 比 WAVEINCAPS 多这一个字段；漏掉它会导致结构体大小不对，
            // waveOutGetDevCapsW 直接返回参数错误、设备名读出来是空的。
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        public delegate void WaveCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInGetNumDevs();

        // 注意：设备名是 WCHAR，必须显式指向 W 版本，否则会调到 ANSI 版本而读出乱码
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveInGetDevCapsW",
            ExactSpelling = true, SetLastError = true)]
        public static extern int waveInGetDevCaps(IntPtr uDeviceID, out WAVEINCAPS pwic, uint cbwic);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInOpen(out IntPtr phwi, IntPtr uDeviceID, ref WAVEFORMATEX pwfx,
            WaveCallback dwCallback, IntPtr dwCallbackInstance, uint fdwOpen);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveInClose(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW",
            ExactSpelling = true, SetLastError = true)]
        public static extern int waveOutGetDevCaps(IntPtr uDeviceID, out WAVEOUTCAPS pwoc, uint cbwoc);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutOpen(out IntPtr phwo, IntPtr uDeviceID, ref WAVEFORMATEX pwfx,
            WaveCallback dwCallback, IntPtr dwCallbackInstance, uint fdwOpen);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll", SetLastError = true)]
        public static extern int waveOutClose(IntPtr hwo);

        public static int HeaderSize { get { return Marshal.SizeOf(typeof(WAVEHDR)); } }
    }

    /// <summary>设备列表项；Id 为 winmm 设备编号，-1 表示系统默认设备。</summary>
    internal sealed class DeviceInfo
    {
        public int Id;
        public string Name;
        public DeviceInfo(int id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }

    internal static class AudioDevices
    {
        public static List<DeviceInfo> Inputs()
        {
            List<DeviceInfo> list = new List<DeviceInfo>();
            list.Add(new DeviceInfo(Mm.WAVE_MAPPER, "（系统默认录音设备）"));
            int n = 0;
            try { n = Mm.waveInGetNumDevs(); }
            catch { n = 0; }
            for (int i = 0; i < n; i++)
            {
                Mm.WAVEINCAPS caps;
                int rc = Mm.waveInGetDevCaps(new IntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(Mm.WAVEINCAPS)));
                string name = (rc == 0 && caps.szPname != null && caps.szPname.Length > 0) ? caps.szPname : ("录音设备 " + i);
                list.Add(new DeviceInfo(i, name));
            }
            return list;
        }

        public static List<DeviceInfo> Outputs()
        {
            List<DeviceInfo> list = new List<DeviceInfo>();
            list.Add(new DeviceInfo(Mm.WAVE_MAPPER, "（系统默认播放设备）"));
            int n = 0;
            try { n = Mm.waveOutGetNumDevs(); }
            catch { n = 0; }
            for (int i = 0; i < n; i++)
            {
                Mm.WAVEOUTCAPS caps;
                int rc = Mm.waveOutGetDevCaps(new IntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(Mm.WAVEOUTCAPS)));
                string name = (rc == 0 && caps.szPname != null) ? caps.szPname : ("播放设备 " + i);
                list.Add(new DeviceInfo(i, name));
            }
            return list;
        }

        public static string InputName(int id)
        {
            List<DeviceInfo> l = Inputs();
            for (int i = 0; i < l.Count; i++) if (l[i].Id == id) return l[i].Name;
            return "录音设备 " + id;
        }

        public static string OutputName(int id)
        {
            List<DeviceInfo> l = Outputs();
            for (int i = 0; i < l.Count; i++) if (l[i].Id == id) return l[i].Name;
            return "播放设备 " + id;
        }
    }

    /// <summary>非托管音频缓冲区：一个 WAVEHDR + 一块数据内存 + 一份托管镜像。</summary>
    internal sealed class RawBuffer : IDisposable
    {
        public IntPtr Header;
        public IntPtr Data;
        public int Length;
        public byte[] Managed;
        public bool Prepared;

        public RawBuffer(int length)
        {
            Length = length;
            Managed = new byte[length];
            Header = Marshal.AllocHGlobal(Mm.HeaderSize);
            Data = Marshal.AllocHGlobal(length);
            for (int i = 0; i < length; i++) Marshal.WriteByte(Data, i, 0);
            Mm.WAVEHDR h = new Mm.WAVEHDR();
            h.lpData = Data;
            h.dwBufferLength = (uint)length;
            h.dwBytesRecorded = 0;
            h.dwFlags = 0;
            h.dwLoops = 0;
            Marshal.StructureToPtr(h, Header, false);
        }

        public void Dispose()
        {
            if (Header != IntPtr.Zero) { Marshal.FreeHGlobal(Header); Header = IntPtr.Zero; }
            if (Data != IntPtr.Zero) { Marshal.FreeHGlobal(Data); Data = IntPtr.Zero; }
        }
    }

    internal interface IAudioSource : IDisposable
    {
        string Description { get; }
        void Start();
        void Stop();
        event Action<byte[], int> DataAvailable;
    }

    internal interface IAudioFeeder
    {
        /// <summary>把最多 count 字节的 PCM 写入 buffer[offset..]；返回实际写入字节数（不足部分由播放端补静音）。</summary>
        int Fill(byte[] buffer, int offset, int count);
    }

    internal interface IAudioSink : IDisposable
    {
        string Description { get; }
        void SetFeeder(IAudioFeeder feeder);
        void Start();
        void Stop();
    }

    /// <summary>waveIn 采集：固定 16 位单声道 PCM，按 frameMs 切成小块回调。</summary>
    internal sealed class WaveInSource : IAudioSource
    {
        private readonly int _deviceId;
        private readonly int _sampleRate;
        private readonly int _frameBytes;
        private readonly int _bufferCount;
        private readonly string _desc;

        private IntPtr _hwi = IntPtr.Zero;
        private RawBuffer[] _buffers;
        private Mm.WaveCallback _cb;      // 必须保持强引用，否则会被 GC 回收导致进程崩溃
        private volatile bool _running;
        // 回调线程与 Stop() 互斥：Stop 在持锁状态下释放缓冲区，回调必须先拿锁再碰缓冲区，
        // 否则可能出现"回调正在写、主线程已释放"的野指针访问。
        private readonly object _freeLock = new object();

        public event Action<byte[], int> DataAvailable;

        public string Description { get { return _desc; } }
        public int FrameBytes { get { return _frameBytes; } }
        public int SampleRate { get { return _sampleRate; } }

        public WaveInSource(int deviceId, int sampleRate, int frameMs, int bufferCount)
        {
            _deviceId = deviceId;
            _sampleRate = sampleRate;
            _frameBytes = sampleRate * 2 * frameMs / 1000;
            _bufferCount = bufferCount < 2 ? 2 : bufferCount;
            _desc = AudioDevices.InputName(deviceId) + " @ " + sampleRate + "Hz";
        }

        public void Start()
        {
            if (_running) return;
            Mm.WAVEFORMATEX fmt = Mm.WAVEFORMATEX.Pcm(_sampleRate, 1, 16);
            _cb = new Mm.WaveCallback(OnWaveIn);
            IntPtr dev = new IntPtr(_deviceId);
            int rc = Mm.waveInOpen(out _hwi, dev, ref fmt, _cb, IntPtr.Zero, Mm.CALLBACK_FUNCTION);
            if (rc != 0)
            {
                _hwi = IntPtr.Zero;
                throw new AudioException("打开录音设备失败：" + _desc, rc);
            }

            _buffers = new RawBuffer[_bufferCount];
            for (int i = 0; i < _bufferCount; i++)
            {
                RawBuffer rb = new RawBuffer(_frameBytes);
                rc = Mm.waveInPrepareHeader(_hwi, rb.Header, (uint)Mm.HeaderSize);
                if (rc != 0) { CleanupAfterFailedStart(); throw new AudioException("准备录音缓冲区失败", rc); }
                rb.Prepared = true;
                _buffers[i] = rb;
                rc = Mm.waveInAddBuffer(_hwi, rb.Header, (uint)Mm.HeaderSize);
                if (rc != 0) { CleanupAfterFailedStart(); throw new AudioException("提交录音缓冲区失败", rc); }
            }

            rc = Mm.waveInStart(_hwi);
            if (rc != 0) { CleanupAfterFailedStart(); throw new AudioException("启动录音失败", rc); }
            _running = true;
        }

        private void CleanupAfterFailedStart()
        {
            IntPtr h = _hwi;
            _hwi = IntPtr.Zero;
            try
            {
                if (h != IntPtr.Zero)
                {
                    Mm.waveInReset(h);
                    lock (_freeLock)
                    {
                        FreeBuffers(h);
                        Mm.waveInClose(h);
                    }
                }
            }
            catch { }
            _buffers = null;
        }

        /// <summary>释放所有录音缓冲区。调用方必须持有 _freeLock。</summary>
        private void FreeBuffers(IntPtr h)
        {
            if (_buffers == null) return;
            for (int i = 0; i < _buffers.Length; i++)
            {
                RawBuffer rb = _buffers[i];
                if (rb == null) continue;
                if (rb.Prepared)
                {
                    try { Mm.waveInUnprepareHeader(h, rb.Header, (uint)Mm.HeaderSize); } catch { }
                }
                rb.Prepared = false;
                rb.Dispose();
            }
            _buffers = null;
        }

        private void OnWaveIn(IntPtr hwi, uint msg, IntPtr inst, IntPtr p1, IntPtr p2)
        {
            // 绝不能把异常抛回非托管代码
            try
            {
                if (msg != Mm.WIM_DATA) return;
                lock (_freeLock)
                {
                    if (!_running || _hwi == IntPtr.Zero) return;
                    RawBuffer rb = Find(p1);
                    if (rb == null) return;

                    Mm.WAVEHDR h = (Mm.WAVEHDR)Marshal.PtrToStructure(rb.Header, typeof(Mm.WAVEHDR));
                    int got = (int)h.dwBytesRecorded;
                    if (got > rb.Length) got = rb.Length;

                    if (got > 0)
                    {
                        byte[] copy = new byte[got];
                        Marshal.Copy(rb.Data, copy, 0, got);
                        Action<byte[], int> eh = DataAvailable;
                        if (eh != null) eh(copy, got);
                    }

                    // 重新入队，保持采集连续
                    h.dwBytesRecorded = 0;
                    h.dwFlags = h.dwFlags & ~Mm.WHDR_DONE;
                    h.dwBufferLength = (uint)rb.Length;
                    Marshal.StructureToPtr(h, rb.Header, false);
                    int rc = Mm.waveInAddBuffer(_hwi, rb.Header, (uint)Mm.HeaderSize);
                    if (rc != 0) { /* 设备可能正在关闭，忽略 */ }
                }
            }
            catch { }
        }

        private RawBuffer Find(IntPtr header)
        {
            RawBuffer[] bs = _buffers;
            if (bs == null) return null;
            for (int i = 0; i < bs.Length; i++)
            {
                if (bs[i] != null && bs[i].Header == header) return bs[i];
            }
            return null;
        }

        public void Stop()
        {
            IntPtr h = _hwi;
            if (h == IntPtr.Zero) return;
            _running = false;
            // 先在锁外停驱动，避免与回调互等；再持锁释放，保证没有回调正在使用缓冲区
            try { Mm.waveInStop(h); } catch { }
            try { Mm.waveInReset(h); } catch { }
            lock (_freeLock)
            {
                _hwi = IntPtr.Zero;
                FreeBuffers(h);
            }
            try { Mm.waveInClose(h); } catch { }
        }

        public void Dispose() { Stop(); }
    }

    /// <summary>waveOut 播放：内部维持 bufferCount 个循环缓冲区，每个播完立即向 feeder 取下一块。</summary>
    internal sealed class WaveOutSink : IAudioSink
    {
        private readonly int _deviceId;
        private readonly int _sampleRate;
        private readonly int _frameBytes;
        private readonly int _bufferCount;
        private readonly string _desc;

        private IntPtr _hwo = IntPtr.Zero;
        private RawBuffer[] _buffers;
        private Mm.WaveCallback _cb;
        private volatile bool _running;
        private IAudioFeeder _feeder;
        private long _starved;
        private readonly object _freeLock = new object();   // 见 WaveInSource 里的说明

        public string Description { get { return _desc; } }
        public long StarvedBuffers { get { return _starved; } }

        public WaveOutSink(int deviceId, int sampleRate, int frameMs, int bufferCount)
        {
            _deviceId = deviceId;
            _sampleRate = sampleRate;
            _frameBytes = sampleRate * 2 * frameMs / 1000;
            _bufferCount = bufferCount < 2 ? 2 : bufferCount;
            _desc = AudioDevices.OutputName(deviceId) + " @ " + sampleRate + "Hz";
        }

        public void SetFeeder(IAudioFeeder feeder) { _feeder = feeder; }

        public void Start()
        {
            if (_running) return;
            Mm.WAVEFORMATEX fmt = Mm.WAVEFORMATEX.Pcm(_sampleRate, 1, 16);
            _cb = new Mm.WaveCallback(OnWaveOut);
            IntPtr dev = new IntPtr(_deviceId);
            int rc = Mm.waveOutOpen(out _hwo, dev, ref fmt, _cb, IntPtr.Zero, Mm.CALLBACK_FUNCTION);
            if (rc != 0)
            {
                _hwo = IntPtr.Zero;
                throw new AudioException("打开播放设备失败：" + _desc, rc);
            }

            _buffers = new RawBuffer[_bufferCount];
            for (int i = 0; i < _bufferCount; i++)
            {
                RawBuffer rb = new RawBuffer(_frameBytes);
                rc = Mm.waveOutPrepareHeader(_hwo, rb.Header, (uint)Mm.HeaderSize);
                if (rc != 0) { CleanupAfterFailedStart(); throw new AudioException("准备播放缓冲区失败", rc); }
                rb.Prepared = true;
                _buffers[i] = rb;
            }

            _running = true;
            // 预填满所有缓冲区，之后靠 WOM_DONE 循环续填
            for (int i = 0; i < _buffers.Length; i++) Submit(_buffers[i], true);
        }

        private void CleanupAfterFailedStart()
        {
            IntPtr h = _hwo;
            _hwo = IntPtr.Zero;
            try
            {
                if (h != IntPtr.Zero)
                {
                    Mm.waveOutReset(h);
                    lock (_freeLock)
                    {
                        FreeBuffers(h);
                        Mm.waveOutClose(h);
                    }
                }
            }
            catch { }
            _buffers = null;
        }

        /// <summary>释放所有播放缓冲区。调用方必须持有 _freeLock。</summary>
        private void FreeBuffers(IntPtr h)
        {
            if (_buffers == null) return;
            for (int i = 0; i < _buffers.Length; i++)
            {
                RawBuffer rb = _buffers[i];
                if (rb == null) continue;
                if (rb.Prepared)
                {
                    try { Mm.waveOutUnprepareHeader(h, rb.Header, (uint)Mm.HeaderSize); } catch { }
                }
                rb.Prepared = false;
                rb.Dispose();
            }
            _buffers = null;
        }

        private void OnWaveOut(IntPtr hwo, uint msg, IntPtr inst, IntPtr p1, IntPtr p2)
        {
            try
            {
                if (msg != Mm.WOM_DONE) return;
                lock (_freeLock)
                {
                    if (!_running || _hwo == IntPtr.Zero) return;
                    RawBuffer rb = Find(p1);
                    if (rb == null) return;
                    Submit(rb, false);
                }
            }
            catch { }
        }

        private void Submit(RawBuffer rb, bool initial)
        {
            IAudioFeeder f = _feeder;
            int n = 0;
            if (f != null)
            {
                try { n = f.Fill(rb.Managed, 0, rb.Length); }
                catch { n = 0; }
            }
            if (n < 0) n = 0;
            if (n > rb.Length) n = rb.Length;
            if (n < rb.Length)
            {
                Array.Clear(rb.Managed, n, rb.Length - n);
                if (!initial) _starved++;
            }
            Marshal.Copy(rb.Managed, 0, rb.Data, rb.Length);

            Mm.WAVEHDR h = (Mm.WAVEHDR)Marshal.PtrToStructure(rb.Header, typeof(Mm.WAVEHDR));
            h.dwBufferLength = (uint)rb.Length;
            h.dwBytesRecorded = 0;
            h.dwFlags = h.dwFlags & ~Mm.WHDR_DONE;
            Marshal.StructureToPtr(h, rb.Header, false);

            int rc = Mm.waveOutWrite(_hwo, rb.Header, (uint)Mm.HeaderSize);
            if (rc != 0 && !initial) { /* 设备正在关闭 */ }
        }

        private RawBuffer Find(IntPtr header)
        {
            RawBuffer[] bs = _buffers;
            if (bs == null) return null;
            for (int i = 0; i < bs.Length; i++)
            {
                if (bs[i] != null && bs[i].Header == header) return bs[i];
            }
            return null;
        }

        public void Stop()
        {
            IntPtr h = _hwo;
            if (h == IntPtr.Zero) return;
            _running = false;
            try { Mm.waveOutReset(h); } catch { }
            lock (_freeLock)
            {
                _hwo = IntPtr.Zero;
                FreeBuffers(h);
            }
            try { Mm.waveOutClose(h); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
