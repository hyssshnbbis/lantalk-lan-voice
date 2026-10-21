// Net.cs —— UDP 点对点传输：报文协议、收发线程、丢包/RTT 统计、局域网广播发现
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanTalk
{
    /// <summary>报文类型。音频包第 0 字节为 1，其余为控制包。</summary>
    internal enum Pkt : byte
    {
        Audio = 1,
        Ring = 2,
        Ringing = 3,
        Accept = 4,
        Bye = 5,
        Ping = 6,
        Pong = 7,
        Busy = 8,
        Hello = 9,
        HelloReply = 10,
        TalkState = 11,
        RoomHello = 12,     // 会议室公告（广播）：房间号 + 成员ID + 名字
        RoomHere = 13,      // 会议室应答（单播）：同上
        RoomBye = 14,       // 离开会议室
        Chat = 15,          // 文本消息（会议室与一对一通用）
        ImageChunk = 16,    // 图片分片
        Avatar = 17         // 头像（同样走分片）
    }

    internal sealed class IfaceInfo
    {
        public string Name;
        public string Ip;
        public bool HasGateway;
        public bool IsVirtual;
        public override string ToString()
        {
            return Ip + "  [" + Name + (HasGateway ? " · 默认路由" : "") + (IsVirtual ? " · 虚拟网卡" : "") + "]";
        }
    }

    internal static class NetInfo
    {
        public static bool LooksVirtual(string s)
        {
            if (s == null) return false;
            string[] keys = new string[] {
                "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "radmin", "tap-", "tap ",
                "openvpn", "wireguard", "zerotier", "tailscale", "hamachi", "npcap", "loopback",
                "bluetooth", "virtual", "虚拟", "wsl", "docker"
            };
            string t = s.ToLowerInvariant();
            for (int i = 0; i < keys.Length; i++) if (t.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static List<IfaceInfo> Ifaces()
        {
            List<IfaceInfo> list = new List<IfaceInfo>();
            try
            {
                NetworkInterface[] nis = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nis.Length; i++)
                {
                    NetworkInterface ni = nis[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool gw = props != null && props.GatewayAddresses != null && props.GatewayAddresses.Count > 0;
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(ua.Address)) continue;
                        IfaceInfo fi = new IfaceInfo();
                        fi.Name = ni.Name;
                        fi.Ip = ua.Address.ToString();
                        fi.HasGateway = gw;
                        fi.IsVirtual = LooksVirtual(ni.Name) || LooksVirtual(ni.Description);
                        list.Add(fi);
                    }
                }
            }
            catch { }
            list.Sort(CompareIface);
            return list;
        }

        private static int CompareIface(IfaceInfo a, IfaceInfo b)
        {
            // 优先：有默认路由 > 非虚拟 > 私有网段
            int c = (b.HasGateway ? 1 : 0) - (a.HasGateway ? 1 : 0);
            if (c != 0) return c;
            c = (a.IsVirtual ? 1 : 0) - (b.IsVirtual ? 1 : 0);
            if (c != 0) return c;
            return string.CompareOrdinal(a.Ip, b.Ip);
        }

        /// <summary>挑一个最可能连到对方电脑的本机地址。</summary>
        public static string BestLocalIp()
        {
            List<IfaceInfo> l = Ifaces();
            for (int i = 0; i < l.Count; i++) if (!l[i].IsVirtual) return l[i].Ip;
            return l.Count > 0 ? l[0].Ip : "127.0.0.1";
        }

        /// <summary>本机所有广播地址（含 255.255.255.255）。</summary>
        public static List<IPAddress> BroadcastAddresses()
        {
            List<IPAddress> res = new List<IPAddress>();
            res.Add(IPAddress.Broadcast);
            try
            {
                NetworkInterface[] nis = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nis.Length; i++)
                {
                    NetworkInterface ni = nis[i];
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes();
                        byte[] mask = ua.IPv4Mask.GetAddressBytes();
                        if (ip.Length != 4 || mask.Length != 4) continue;
                        byte[] bc = new byte[4];
                        for (int k = 0; k < 4; k++) bc[k] = (byte)(ip[k] | (~mask[k] & 0xFF));
                        IPAddress a = new IPAddress(bc);
                        bool dup = false;
                        for (int k = 0; k < res.Count; k++) if (res[k].Equals(a)) { dup = true; break; }
                        if (!dup) res.Add(a);
                    }
                }
            }
            catch { }
            return res;
        }
    }

    /// <summary>
    /// 一个 UDP 端点。双方各绑一个本地端口、互相往对方的 IP:端口 发。
    /// 没有客户端/服务端之分，所以任意一方都能主动呼叫。
    /// </summary>
    internal sealed class UdpPeer : IDisposable
    {
        private const int MaxDatagram = 4096;
        /// <summary>语音包头：类型(1) + 序号(4) + 发送者ID(4)。</summary>
        private const int AudioHeaderLen = 9;

        private readonly object _sendLock = new object();
        private readonly object _statLock = new object();
        private byte[] _txbuf = new byte[MaxDatagram];

        private Socket _sock;
        private Thread _rx;
        private volatile bool _running;
        // 一对一模式里只有一个对端；会议室模式里是全体成员。两种模式共用这套名单。
        private readonly List<IPEndPoint> _peers = new List<IPEndPoint>();
        private readonly object _peerLock = new object();
        private int _localPort;
        private string _bindIp = "0.0.0.0";

        private Thread _helloThread;
        private volatile bool _discovering;
        private string _myName = "";
        private int _helloIntervalMs = 2000;

        private long _rxPackets, _rxBytes, _txPackets, _txBytes, _lost, _late;
        // 每个来源各有自己的序号空间（会议里每个成员都是独立发送方），
        // 所以必须按来源分别记账，否则多个发送方的序号交错会被误判成丢包。
        private readonly Dictionary<string, long> _seqByPeer = new Dictionary<string, long>();
        private readonly object _seqLock = new object();
        private double _rttMs = -1;
        private long _lastRxTicks;
        private long _lastAudioRxTicks;

        public event Action<byte[], int, int, long, uint, IPEndPoint> AudioReceived;   // 数据报, 负载偏移, 负载长度, 序号, 发送者ID, 来源
        public event Action<Pkt, byte[], int, IPEndPoint> ControlReceived;
        public event Action<string, IPEndPoint> PeerDiscovered;

        public int LocalPort { get { return _localPort; } }
        public string BindIp { get { return _bindIp; } }

        /// <summary>
        /// 本实例的标识（4 字节），会写进每个语音包。
        /// 收方据此把同一发送者的数据归到一路——即使对方有多张网卡、
        /// 从两个地址都能到达，也不会被当成两个人。
        /// </summary>
        public uint InstanceId { get { return _instanceId; } }
        private uint _instanceId = 1;

        /// <summary>一对一模式下的那个对端；会议室模式下是名单里的第一个。</summary>
        public IPEndPoint Remote
        {
            get { lock (_peerLock) { return _peers.Count > 0 ? _peers[0] : null; } }
        }

        public IPEndPoint[] Peers { get { lock (_peerLock) { return _peers.ToArray(); } } }
        public int PeerCount { get { lock (_peerLock) { return _peers.Count; } } }
        public bool Running { get { return _running; } }

        public long RxPackets { get { return Interlocked.Read(ref _rxPackets); } }
        public long RxBytes { get { return Interlocked.Read(ref _rxBytes); } }
        public long TxPackets { get { return Interlocked.Read(ref _txPackets); } }
        public long TxBytes { get { return Interlocked.Read(ref _txBytes); } }
        public long LostPackets { get { return Interlocked.Read(ref _lost); } }
        public long LatePackets { get { return Interlocked.Read(ref _late); } }
        public double RttMs { get { return _rttMs; } }
        public DateTime LastRxUtc { get { return new DateTime(Interlocked.Read(ref _lastRxTicks), DateTimeKind.Utc); } }
        public DateTime LastAudioRxUtc { get { return new DateTime(Interlocked.Read(ref _lastAudioRxTicks), DateTimeKind.Utc); } }

        public string LocalEndPointText
        {
            get
            {
                string ip = _bindIp;
                if (ip == "0.0.0.0") ip = NetInfo.BestLocalIp();
                return ip + ":" + _localPort;
            }
        }

        public void Start(string bindIp, int port)
        {
            if (_running) return;
            byte[] gb = Guid.NewGuid().ToByteArray();
            _instanceId = BitConverter.ToUInt32(gb, 0);
            if (_instanceId == 0) _instanceId = 0xA5A5A5A5;
            _bindIp = string.IsNullOrEmpty(bindIp) ? "0.0.0.0" : bindIp;
            IPAddress addr = IPAddress.Any;
            if (_bindIp != "0.0.0.0")
            {
                IPAddress parsed;
                if (IPAddress.TryParse(_bindIp, out parsed)) addr = parsed;
            }
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            s.EnableBroadcast = true;
            try { s.ReceiveBufferSize = 256 * 1024; } catch { }
            try { s.SendBufferSize = 256 * 1024; } catch { }
            s.Bind(new IPEndPoint(addr, port));
            _localPort = ((IPEndPoint)s.LocalEndPoint).Port;
            _sock = s;
            _running = true;

            _rx = new Thread(RxLoop);
            _rx.IsBackground = true;
            _rx.Priority = ThreadPriority.AboveNormal;
            _rx.Name = "LanTalk-Rx";
            _rx.Start();
        }

        public void SetRemote(IPAddress ip, int port)
        {
            lock (_peerLock)
            {
                _peers.Clear();
                _peers.Add(new IPEndPoint(ip, port));
            }
        }

        public bool SetRemote(string ip, int port)
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) return false;
            SetRemote(a, port);
            return true;
        }

        public void ClearPeers() { lock (_peerLock) _peers.Clear(); }

        public void AddPeer(IPEndPoint ep)
        {
            if (ep == null) return;
            lock (_peerLock)
            {
                for (int i = 0; i < _peers.Count; i++)
                {
                    if (SamePeer(_peers[i], ep)) return;
                }
                _peers.Add(new IPEndPoint(ep.Address, ep.Port));
            }
        }

        public bool RemovePeer(IPEndPoint ep)
        {
            if (ep == null) return false;
            lock (_peerLock)
            {
                for (int i = 0; i < _peers.Count; i++)
                {
                    if (SamePeer(_peers[i], ep)) { _peers.RemoveAt(i); return true; }
                }
            }
            return false;
        }

        public bool HasPeer(IPEndPoint ep)
        {
            if (ep == null) return false;
            lock (_peerLock)
            {
                for (int i = 0; i < _peers.Count; i++) if (SamePeer(_peers[i], ep)) return true;
            }
            return false;
        }

        private static bool SamePeer(IPEndPoint a, IPEndPoint b)
        {
            return a.Port == b.Port && a.Address.Equals(b.Address);
        }

        private void RxLoop()
        {
            byte[] buf = new byte[MaxDatagram];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                int n;
                IPEndPoint src;
                try
                {
                    from = new IPEndPoint(IPAddress.Any, 0);
                    n = _sock.ReceiveFrom(buf, 0, buf.Length, SocketFlags.None, ref from);
                    src = (IPEndPoint)from;
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    continue;
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { if (!_running) break; continue; }
                if (n <= 0) continue;

                Interlocked.Increment(ref _rxPackets);
                Interlocked.Add(ref _rxBytes, n);
                Interlocked.Exchange(ref _lastRxTicks, DateTime.UtcNow.Ticks);

                byte type = buf[0];
                if (type == (byte)Pkt.Audio)
                {
                    if (n < AudioHeaderLen) continue;
                    long seq = (long)((uint)(buf[1] | (buf[2] << 8) | (buf[3] << 16) | (buf[4] << 24)));
                    uint sid = (uint)(buf[5] | (buf[6] << 8) | (buf[7] << 16) | (buf[8] << 24));
                    if (sid == _instanceId) continue;      // 自己发出去的（广播/环回时可能转回来）
                    Interlocked.Exchange(ref _lastAudioRxTicks, DateTime.UtcNow.Ticks);

                    string pkey = sid.ToString("x8");
                    lock (_seqLock)
                    {
                        long hi;
                        if (!_seqByPeer.TryGetValue(pkey, out hi))
                        {
                            // 这个来源的第一包只用来建立基准。
                            // 会议里成员是陆续加入的，对方的序号早就在跑，
                            // 把首个包的序号当成"前面丢了多少包"就全错了。
                            if (_seqByPeer.Count >= 64) _seqByPeer.Clear();
                            _seqByPeer[pkey] = seq;
                        }
                        else if (seq > hi)
                        {
                            long gap = seq - hi - 1;
                            if (gap > 0) Interlocked.Add(ref _lost, gap);
                            _seqByPeer[pkey] = seq;
                        }
                        else if (seq < hi)
                        {
                            Interlocked.Increment(ref _late);
                        }
                    }

                    Action<byte[], int, int, long, uint, IPEndPoint> h = AudioReceived;
                    if (h != null)
                    {
                        try { h(buf, AudioHeaderLen, n - AudioHeaderLen, seq, sid, src); } catch { }
                    }
                }
                else
                {
                    byte[] payload = new byte[n - 1];
                    if (n > 1) Buffer.BlockCopy(buf, 1, payload, 0, n - 1);
                    Pkt p = (Pkt)type;

                    if (p == Pkt.Ping)
                    {
                        SendControl(Pkt.Pong, payload, payload.Length, src);
                        continue;
                    }
                    if (p == Pkt.Pong)
                    {
                        if (payload.Length >= 8)
                        {
                            long sent = BitConverter.ToInt64(payload, 0);
                            double ms = (DateTime.UtcNow.Ticks - sent) / (double)TimeSpan.TicksPerMillisecond;
                            if (ms >= 0 && ms < 60000) _rttMs = ms;
                        }
                        continue;
                    }
                    if (p == Pkt.Hello || p == Pkt.HelloReply)
                    {
                        HandleHello(p, payload, src);
                        continue;
                    }

                    Action<Pkt, byte[], int, IPEndPoint> h = ControlReceived;
                    if (h != null)
                    {
                        try { h(p, payload, payload.Length, src); } catch { }
                    }
                }
            }
        }

        private void HandleHello(Pkt p, byte[] payload, IPEndPoint src)
        {
            string name = "";
            int port = src.Port;
            if (payload.Length >= 2) port = payload[0] | (payload[1] << 8);
            if (payload.Length > 2) name = Encoding.UTF8.GetString(payload, 2, payload.Length - 2);

            if (p == Pkt.Hello)
            {
                // 别人在找同伴：回一个 HelloReply，带上自己的名字与端口
                byte[] mine = Encoding.UTF8.GetBytes(_myName == null ? "" : _myName);
                byte[] rep = new byte[2 + mine.Length];
                rep[0] = (byte)(_localPort & 0xFF);
                rep[1] = (byte)((_localPort >> 8) & 0xFF);
                Buffer.BlockCopy(mine, 0, rep, 2, mine.Length);
                SendControl(Pkt.HelloReply, rep, rep.Length, src);
            }

            if (name.Length > 0 && name == _myName) return;   // 忽略自己发的广播
            Action<string, IPEndPoint> h = PeerDiscovered;
            if (h != null)
            {
                try { h(name.Length == 0 ? src.Address.ToString() : name, new IPEndPoint(src.Address, port)); }
                catch { }
            }
        }

        // ---------------- 发送 ----------------

        public void SendAudio(byte[] payload, int len, long seq)
        {
            if (!_running) return;
            IPEndPoint[] targets = Peers;
            if (targets.Length == 0) return;
            if (len < 0) len = 0;
            if (len > MaxDatagram - AudioHeaderLen) len = MaxDatagram - AudioHeaderLen;
            lock (_sendLock)
            {
                Socket s = _sock;
                if (s == null) return;
                _txbuf[0] = (byte)Pkt.Audio;
                uint u = (uint)seq;
                _txbuf[1] = (byte)(u & 0xFF);
                _txbuf[2] = (byte)((u >> 8) & 0xFF);
                _txbuf[3] = (byte)((u >> 16) & 0xFF);
                _txbuf[4] = (byte)((u >> 24) & 0xFF);
                uint d = _instanceId;
                _txbuf[5] = (byte)(d & 0xFF);
                _txbuf[6] = (byte)((d >> 8) & 0xFF);
                _txbuf[7] = (byte)((d >> 16) & 0xFF);
                _txbuf[8] = (byte)((d >> 24) & 0xFF);
                if (len > 0) Buffer.BlockCopy(payload, 0, _txbuf, AudioHeaderLen, len);
                for (int i = 0; i < targets.Length; i++)
                {
                    try
                    {
                        s.SendTo(_txbuf, 0, AudioHeaderLen + len, SocketFlags.None, targets[i]);
                        Interlocked.Increment(ref _txPackets);
                        Interlocked.Add(ref _txBytes, AudioHeaderLen + len);
                    }
                    catch { }
                }
            }
        }

        /// <summary>发给名单里的所有人。</summary>
        public void SendControl(Pkt type, byte[] payload, int len)
        {
            IPEndPoint[] targets = Peers;
            for (int i = 0; i < targets.Length; i++) SendControl(type, payload, len, targets[i]);
        }

        public void SendControl(Pkt type, byte[] payload, int len, IPEndPoint to)
        {
            if (to == null || !_running) return;
            if (len < 0) len = 0;
            if (len > MaxDatagram - 1) len = MaxDatagram - 1;
            lock (_sendLock)
            {
                Socket s = _sock;
                if (s == null) return;
                _txbuf[0] = (byte)type;
                if (len > 0 && payload != null) Buffer.BlockCopy(payload, 0, _txbuf, 1, len);
                try
                {
                    s.SendTo(_txbuf, 0, 1 + len, SocketFlags.None, to);
                    Interlocked.Increment(ref _txPackets);
                    Interlocked.Add(ref _txBytes, 1 + len);
                }
                catch { }
            }
        }

        public void SendControl(Pkt type) { SendControl(type, null, 0); }

        public void SendPing()
        {
            byte[] b = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
            SendControl(Pkt.Ping, b, b.Length);
        }

        /// <summary>发送一次握手用的字节（编码方式/采样率/帧长），呼叫与接听共用。</summary>
        public void SendHandshake(Pkt type, VoiceCodec codec, int sampleRate, int frameMs)
        {
            byte[] b = new byte[7];
            b[0] = (byte)codec;
            uint r = (uint)sampleRate;
            b[1] = (byte)(r & 0xFF);
            b[2] = (byte)((r >> 8) & 0xFF);
            b[3] = (byte)((r >> 16) & 0xFF);
            b[4] = (byte)((r >> 24) & 0xFF);
            b[5] = (byte)frameMs;
            b[6] = 0;
            SendControl(type, b, b.Length);
        }

        // ---------------- 局域网发现 ----------------

        public void StartDiscovery(string myName, int intervalMs)
        {
            _myName = myName == null ? "" : myName;
            if (_discovering) return;
            _discovering = true;
            if (intervalMs > 0) _helloIntervalMs = intervalMs;
            _helloThread = new Thread(HelloLoop);
            _helloThread.IsBackground = true;
            _helloThread.Name = "LanTalk-Hello";
            _helloThread.Start();
        }

        public void StopDiscovery() { _discovering = false; }

        public void BroadcastHelloNow()
        {
            _myName = _myName == null ? "" : _myName;
            byte[] mine = Encoding.UTF8.GetBytes(_myName);
            byte[] payload = new byte[2 + mine.Length];
            payload[0] = (byte)(_localPort & 0xFF);
            payload[1] = (byte)((_localPort >> 8) & 0xFF);
            Buffer.BlockCopy(mine, 0, payload, 2, mine.Length);

            List<IPAddress> targets = NetInfo.BroadcastAddresses();
            for (int i = 0; i < targets.Count; i++)
            {
                SendControl(Pkt.Hello, payload, payload.Length, new IPEndPoint(targets[i], _localPort));
            }
        }

        /// <summary>往本机各网段的广播地址发一个控制包（会议室公告用）。</summary>
        public void SendBroadcast(Pkt type, byte[] payload, int len, int port)
        {
            if (!_running) return;
            if (port <= 0) port = _localPort;
            List<IPAddress> targets = NetInfo.BroadcastAddresses();
            for (int i = 0; i < targets.Count; i++)
            {
                SendControl(type, payload, len, new IPEndPoint(targets[i], port));
            }
        }

        private void HelloLoop()        {
            while (_discovering && _running)
            {
                try { BroadcastHelloNow(); }
                catch { }
                for (int i = 0; i < _helloIntervalMs / 100 && _discovering && _running; i++) Thread.Sleep(100);
            }
        }

        public void Dispose()
        {
            _discovering = false;
            _running = false;
            Socket s = _sock;
            _sock = null;
            if (s != null)
            {
                try { s.Close(); } catch { }
            }
        }
    }
}
