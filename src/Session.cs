// Session.cs —— 通话状态机：呼叫、振铃、接听、挂断、忙线、超时、对端失联检测
using System;
using System.Media;
using System.Net;
using System.Threading;

namespace LanTalk
{
    internal enum CallState
    {
        Idle = 0,       // 空闲
        Outgoing = 1,   // 已呼出，等待对方接听
        Incoming = 2,   // 有来电，正在振铃
        InCall = 3      // 通话中
    }

    internal static class CallStateText
    {
        public static string Of(CallState s)
        {
            switch (s)
            {
                case CallState.Idle: return "空闲";
                case CallState.Outgoing: return "正在呼叫…";
                case CallState.Incoming: return "来电振铃中";
                case CallState.InCall: return "通话中";
                default: return "未知";
            }
        }
    }

    internal sealed class Session : IDisposable
    {
        private const int RingTimeoutMs = 35000;      // 呼叫无人接听
        private const int IncomingTimeoutMs = 35000;  // 来电无人处理
        private const int PeerSilenceMs = 15000;      // 通话中对方长时间无任何数据
        private const int RingRepeatMs = 1200;
        private const int PingIntervalMs = 1000;
        private const int PresencePingMs = 2500;

        private readonly UdpPeer _net;
        private readonly AudioPath _audio;
        private readonly object _lock = new object();

        private CallState _state = CallState.Idle;
        private DateTime _stateSinceUtc = DateTime.UtcNow;
        private DateTime _lastRingUtc = DateTime.MinValue;
        private DateTime _lastPingUtc = DateTime.MinValue;
        private DateTime _lastRingToneUtc = DateTime.MinValue;
        private DateTime _lastPeerPacketUtc = DateTime.UtcNow;

        private bool _peerRinging;
        private readonly ImageAssembler _imgAsm = new ImageAssembler();
        private uint _imgSeq;
        private bool _peerTalking;
        private volatile bool _autoAnswer;
        private volatile bool _presence;
        private volatile bool _disposed;
        private string _lastError = "";

        public event Action StateChanged;
        public event Action<string> Notice;

        /// <summary>收到一条聊天消息：(发送者名字, 正文)。</summary>
        public event Action<string, string> ChatReceived;

        /// <summary>收齐一张图片：(发送者, JPEG 字节)。</summary>
        public event Action<string, byte[]> ImageReceived;

        /// <summary>本机在对端屏幕上显示的名字（默认用机器名）。</summary>
        public string MyName = Environment.MachineName;

        /// <summary>把一张 JPEG 切成分片发给当前对端。返回片数。</summary>
        public int SendImage(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return 0;
            if (jpeg.Length > ImageWire.MaxImageBytes) return -1;

            uint msgId = ((uint)(_net.InstanceId & 0xFFFF) << 16) | (uint)((++_imgSeq) & 0xFFFF);
            int total = ImageWire.ChunkCount(jpeg.Length);
            int sent = 0;
            for (int ci = 0; ci < total; ci++)
            {
                int off = ci * ImageWire.ChunkPayload;
                int len = Math.Min(ImageWire.ChunkPayload, jpeg.Length - off);
                byte[] chunk = ImageWire.BuildChunk(msgId, ci, total, jpeg, off, len);
                try { _net.SendControl(Pkt.ImageChunk, chunk, chunk.Length); sent++; }
                catch { break; }
            }
            Log("发出图片：" + (jpeg.Length / 1024) + " KB，切成 " + total + " 片");
            return sent;
        }

        /// <summary>把一条文本消息发给当前对端。</summary>
        public bool SendChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            byte[] p = ChatWire.Build(_net.InstanceId, MyName, text);
            try { _net.SendControl(Pkt.Chat, p, p.Length); return true; }
            catch { return false; }
        }

        public CallState State { get { lock (_lock) return _state; } }
        public bool PeerRinging { get { lock (_lock) return _peerRinging; } }
        public bool PeerTalking { get { lock (_lock) return _peerTalking; } }
        public AudioPath Audio { get { return _audio; } }
        public UdpPeer Net { get { return _net; } }
        public string LastError { get { return _lastError; } }

        public bool AutoAnswer { get { return _autoAnswer; } set { _autoAnswer = value; } }

        /// <summary>空闲时是否向已填写的对方地址发心跳（用来显示在线状态）。</summary>
        public bool PresencePing { get { return _presence; } set { _presence = value; } }

        public Session(UdpPeer net, AudioPath audio)
        {
            _net = net;
            _audio = audio;
            _net.ControlReceived += OnControl;
            _net.AudioReceived += OnAnyAudio;
        }

        private void Log(string s)
        {
            Action<string> h = Notice;
            if (h != null) { try { h(s); } catch { } }
        }

        private void SetState(CallState s)
        {
            bool changed;
            lock (_lock)
            {
                changed = _state != s;
                if (changed)
                {
                    _state = s;
                    _stateSinceUtc = DateTime.UtcNow;
                    if (s != CallState.Outgoing) _peerRinging = false;
                }
            }
            if (changed)
            {
                Action h = StateChanged;
                if (h != null) { try { h(); } catch { } }
            }
        }

        // ---------------- 主动操作 ----------------

        public bool Call(string peerIp, int peerPort)
        {
            if (State != CallState.Idle)
            {
                Log("当前不是空闲状态，无法发起呼叫。");
                return false;
            }
            IPAddress a;
            if (!IPAddress.TryParse(peerIp, out a))
            {
                _lastError = "对方地址不是合法的 IP：" + peerIp;
                Log(_lastError);
                return false;
            }
            _net.SetRemote(a, peerPort);
            _lastError = "";
            Log("正在呼叫 " + peerIp + ":" + peerPort + " …");
            SetState(CallState.Outgoing);
            SendRing();
            return true;
        }

        public bool Answer()
        {
            if (State != CallState.Incoming)
            {
                Log("当前没有来电可接听。");
                return false;
            }
            Log("已接听。");
            _net.SendHandshake(Pkt.Accept, _audio.Codec, _audio.SampleRate, 20);
            StartAudio();
            SetState(CallState.InCall);
            return true;
        }

        public void Hangup()
        {
            CallState s = State;
            if (s == CallState.Idle) return;
            try { _net.SendControl(Pkt.Bye); } catch { }
            StopAudio();
            Log(s == CallState.InCall ? "通话已结束。" : "呼叫已取消。");
            SetState(CallState.Idle);
        }

        public void SetTalking(bool talking)
        {
            _audio.PttTalking = talking;
            if (State == CallState.InCall)
            {
                byte[] b = new byte[1];
                b[0] = talking ? (byte)1 : (byte)0;
                _net.SendControl(Pkt.TalkState, b, 1);
            }
        }

        private void SendRing()
        {
            _net.SendHandshake(Pkt.Ring, _audio.Codec, _audio.SampleRate, 20);
            _lastRingUtc = DateTime.UtcNow;
        }

        private void StartAudio()
        {
            try
            {
                _audio.Start(false);
                Log("音频通道已打开：录音=" + _audio.SourceDescription + "；放音=" + _audio.SinkDescription);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log("打开音频设备失败：" + ex.Message);
                try { _net.SendControl(Pkt.Bye); } catch { }
                StopAudio();
                SetState(CallState.Idle);
            }
        }

        private void StopAudio()
        {
            try { _audio.Stop(); } catch { }
        }

        // ---------------- 收包处理 ----------------

        private void OnAnyAudio(byte[] buf, int offset, int len, long seq, uint senderId, IPEndPoint src)
        {
            _lastPeerPacketUtc = DateTime.UtcNow;
        }

        private void OnControl(Pkt p, byte[] payload, int len, IPEndPoint src)
        {
            _lastPeerPacketUtc = DateTime.UtcNow;
            CallState st = State;

            switch (p)
            {
                case Pkt.Ring:
                    if (st == CallState.InCall)
                    {
                        _net.SendControl(Pkt.Busy, null, 0, src);
                        Log("对方 " + src.Address + " 来电，但正在通话中，已回忙音。");
                        break;
                    }
                    if (st == CallState.Outgoing)
                    {
                        // 双方同时呼叫（glare）：直接接受对方的来电，避免两边互相等待
                        Log("双方同时在呼叫，自动接通。");
                        _net.SetRemote(src.Address, src.Port);
                        _net.SendControl(Pkt.Accept);
                        StartAudio();
                        SetState(CallState.InCall);
                        break;
                    }
                    _net.SetRemote(src.Address, src.Port);
                    Log("收到来自 " + src.Address + ":" + src.Port + " 的来电。");
                    _net.SendControl(Pkt.Ringing);
                    _lastRingToneUtc = DateTime.MinValue;
                    SetState(CallState.Incoming);
                    if (_autoAnswer)
                    {
                        Log("已开启自动接听，直接应答。");
                        Answer();
                    }
                    break;

                case Pkt.Ringing:
                    if (st == CallState.Outgoing)
                    {
                        lock (_lock) _peerRinging = true;
                        Action h = StateChanged;
                        if (h != null) { try { h(); } catch { } }
                    }
                    break;

                case Pkt.Accept:
                    if (st == CallState.Outgoing)
                    {
                        Log("对方已接听。");
                        StartAudio();
                        SetState(CallState.InCall);
                    }
                    break;

                case Pkt.Busy:
                    if (st == CallState.Outgoing)
                    {
                        Log("对方正忙，呼叫未接通。");
                        StopAudio();
                        SetState(CallState.Idle);
                    }
                    break;

                case Pkt.Bye:
                    if (st != CallState.Idle)
                    {
                        Log(st == CallState.InCall ? "对方已挂断。" : "对方取消了呼叫。");
                        StopAudio();
                        SetState(CallState.Idle);
                    }
                    break;

                case Pkt.ImageChunk:
                    _imgAsm.Add(payload, len, delegate(uint id, byte[] data)
                    {
                        Log("收到图片 " + (data.Length / 1024) + " KB");
                        Action<string, byte[]> ih = ImageReceived;
                        if (ih != null) { try { ih("对方", data); } catch { } }
                    });
                    break;

                case Pkt.Chat:
                    {
                        uint cid;
                        string cname, ctext;
                        if (!ChatWire.Parse(payload, len, out cid, out cname, out ctext)) break;
                        if (string.IsNullOrEmpty(ctext)) break;
                        if (string.IsNullOrEmpty(cname)) cname = "对方";
                        Log("聊天 " + cname + "：" + ctext);
                        Action<string, string> ch = ChatReceived;
                        if (ch != null) { try { ch(cname, ctext); } catch { } }
                        break;
                    }

                case Pkt.TalkState:
                    lock (_lock) _peerTalking = len > 0 && payload[0] != 0;
                    break;
            }
        }

        // ---------------- 定时驱动 ----------------

        public void Tick()
        {
            if (_disposed) return;
            DateTime now = DateTime.UtcNow;
            CallState st = State;

            if (st == CallState.Outgoing)
            {
                if ((now - _stateSinceUtc).TotalMilliseconds > RingTimeoutMs)
                {
                    Log("对方无应答，呼叫超时。");
                    try { _net.SendControl(Pkt.Bye); } catch { }
                    SetState(CallState.Idle);
                    return;
                }
                if ((now - _lastRingUtc).TotalMilliseconds >= RingRepeatMs) SendRing();
            }
            else if (st == CallState.Incoming)
            {
                if ((now - _stateSinceUtc).TotalMilliseconds > IncomingTimeoutMs)
                {
                    Log("来电长时间无人接听，已自动挂断。");
                    try { _net.SendControl(Pkt.Bye); } catch { }
                    SetState(CallState.Idle);
                    return;
                }
                if ((now - _lastRingUtc).TotalMilliseconds >= RingRepeatMs * 2)
                {
                    _net.SendControl(Pkt.Ringing);
                    _lastRingUtc = now;
                }
                if ((now - _lastRingToneUtc).TotalMilliseconds >= 2000)
                {
                    _lastRingToneUtc = now;
                    try { SystemSounds.Hand.Play(); } catch { }
                }
            }
            else if (st == CallState.InCall)
            {
                if ((now - _lastPingUtc).TotalMilliseconds >= PingIntervalMs)
                {
                    _lastPingUtc = now;
                    _net.SendPing();
                }
                if ((now - _lastPeerPacketUtc).TotalMilliseconds > PeerSilenceMs)
                {
                    Log("对方已 " + (PeerSilenceMs / 1000) + " 秒没有任何数据，判定连接中断。");
                    try { _net.SendControl(Pkt.Bye); } catch { }
                    StopAudio();
                    SetState(CallState.Idle);
                }
            }
            else if (st == CallState.Idle && _presence)
            {
                if (_net.Remote != null && (now - _lastPingUtc).TotalMilliseconds >= PresencePingMs)
                {
                    _lastPingUtc = now;
                    _net.SendPing();
                }
            }
        }

        /// <summary>是否收到过对方任何数据（用于界面显示"对方在线"）。</summary>
        public bool PeerSeenRecently(int ms)
        {
            return (DateTime.UtcNow - _lastPeerPacketUtc).TotalMilliseconds <= ms;
        }

        public double StateSeconds { get { lock (_lock) return (DateTime.UtcNow - _stateSinceUtc).TotalSeconds; } }

        public void Dispose()
        {
            _disposed = true;
            try { _net.ControlReceived -= OnControl; } catch { }
            try { _net.AudioReceived -= OnAnyAudio; } catch { }
            StopAudio();
            try { _audio.Dispose(); } catch { }
        }
    }
}
