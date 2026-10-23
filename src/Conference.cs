// Conference.cs —— 多人会议：房间发现、成员管理、音频路由
//
// 设计要点：
//   · 房间号就是一个 UDP 端口。每台电脑在本机绑同一个端口号（端口是按机器隔离的，不冲突），
//     再往各网段的广播地址的该端口发公告，同网段的人就互相看得见了。不需要任何服务器。
//   · 全网状直连：每个人的语音直接发给房间里的其他所有人，收到的每一路各自进抖动缓冲，
//     最后由混音器叠加后放出来。
//   · 身份用 4 字节实例 ID（和语音包头里的是同一个），成员和音频通道都按它来认，
//     所以同一台机器有多条网络路径时也不会被当成两个人。
//   · 广播被挡住时，可以手工填 IP 补联系点，语音照样通。
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace LanTalk
{
    internal sealed class Member
    {
        /// <summary>对方持久身份（16 字节十六进制）。缓存头像要用它做键。</summary>
        public string ClientId = "";
        /// <summary>发送者标识：和语音包头里的 ID 一致，用于把声音归到人。</summary>
        public uint IdNum;
        public string Name = "";
        public IPEndPoint EndPoint;
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public bool Muted;

        /// <summary>单独音量 0..100，只影响本机播放。</summary>
        public int Volume = 100;
        public int Level;
        public long Received;

        public string Id { get { return IdNum.ToString("x8"); } }
        public string Key { get { return EndPoint == null ? Id : (EndPoint.Address + ":" + EndPoint.Port); } }

        public string Display
        {
            get
            {
                string n = string.IsNullOrEmpty(Name) ? Key : Name;
                return Muted ? (n + "（已静音）") : n;
            }
        }
    }

    internal sealed class Conference : IDisposable
    {
        private const int AnnounceMs = 3000;        // 多久广播一次"我在这个房间"
        private const int MemberTimeoutMs = 10000;  // 多久没动静就认为对方已离开
        private const int TickMs = 100;             // Tick 的调用间隔（界面定时器）

        private readonly UdpPeer _net;
        private readonly AudioPath _audio;
        private readonly string _myName;
        private readonly List<Member> _members = new List<Member>();
        // 刚主动退出的人：短时间内忽略他发来的公告，避免"退出前在途的 Hello"
        // 或并发时序把他又加回成员列表（曾经导致退出后对方还挂着人 10 秒）。
        private readonly Dictionary<uint, DateTime> _departed = new Dictionary<uint, DateTime>();
        private const int DepartedGuardMs = 5000;
        // 刚进房时会把房间里已有的人一次性发现出来，那不是"有人进来"，
        // 所以进房后这段时间内不发成员加入提示音。
        private const double JoinChimeSettleSec = 2.5;
        private DateTime _joinedAt = DateTime.MinValue;
        private readonly object _lock = new object();
        private readonly ImageAssembler _imgAsm = new ImageAssembler();
        private readonly ImageAssembler _avatarAsm = new ImageAssembler();
        private DateTime _joinedUtc;

        /// <summary>本次加入会议已经过去了多少秒；未加入时为 0。</summary>
        public double JoinedSeconds
        {
            get
            {
                if (!Joined) return 0;
                double s = (DateTime.UtcNow - _joinedUtc).TotalSeconds;
                return s < 0 ? 0 : s;
            }
        }
        private uint _avatarSeq;
        private byte[] _myAvatar;

        /// <summary>测试用：覆盖上报的 ClientId。为 null 时用 Profile.ClientIdBytes。</summary>
        internal byte[] ClientIdOverride;

        /// <summary>已经发出过多少个头像分片（自检用它断言"没有重传"）。</summary>
        public long AvatarChunksSent;
        private uint _imgSeq;

        private volatile bool _joined;
        private int _roomPort;
        private int _tickAccumMs;
        private volatile bool _disposed;

        public event Action Changed;
        public event Action<string> Notice;

        /// <summary>收到一条聊天消息：(发送者名字, 正文)。</summary>
        public event Action<string, string> ChatReceived;

        /// <summary>收到某个成员的头像：(成员Key, JPEG 字节)。</summary>
        public event Action<string, byte[]> AvatarChanged;

        /// <summary>收齐一张图片：(发送者, JPEG 字节)。</summary>
        public event Action<string, byte[]> ImageReceived;

        public bool Joined { get { return _joined; } }
        public int RoomPort { get { lock (_lock) return _roomPort; } }
        public string MyName { get { return _myName; } }
        public uint MyId { get { return _net.InstanceId; } }
        public int MemberCount { get { lock (_lock) return _members.Count; } }

        public Member[] Members { get { lock (_lock) { return _members.ToArray(); } } }

        public Conference(UdpPeer net, AudioPath audio, string myName)
        {
            _net = net;
            _audio = audio;
            _myName = string.IsNullOrEmpty(myName) ? "未命名" : myName;
            _net.ControlReceived += OnControl;
            _net.AudioReceived += OnAnyAudio;
        }

        private void Log(string s)
        {
            Action<string> h = Notice;
            if (h != null) { try { h(s); } catch { } }
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) { try { h(); } catch { } }
        }

        // ================== 加入 / 离开 ==================

        public void Join(int roomPort)
        {
            if (roomPort <= 0 || roomPort > 65535) roomPort = 47800;
            lock (_lock)
            {
                if (_joined) return;
                _joined = true;
            _joinedUtc = DateTime.UtcNow;
                _roomPort = roomPort;
                _joinedAt = DateTime.UtcNow;
                _members.Clear();
                _departed.Clear();
                _tickAccumMs = AnnounceMs;   // 让 Tick 立刻广播一次
            }
            Log("进房 " + roomPort + "，广播中…");
            Log("我的标识 " + MyId.ToString("x8"));
            try { _audio.Start(false); } catch (Exception ex) { Log("打开音频设备失败：" + ex.Message); }
            _audio.PlayChime(ChimeKind.JoinRoom);
            RaiseChanged();
        }

        public void Leave()
        {
            lock (_lock)
            {
                if (!_joined) return;
                // 先清标志、再发 Bye：否则并发的 Tick/Announce 可能在 Bye 之后
                // 又补发一个 Hello，对方收到后会把已经退出的人重新加回来。
                _joined = false;
            }
            SendBye();
            _net.ClearPeers();
            lock (_lock) { _members.Clear(); }
            try { _audio.Stop(); } catch { }
            Log("已离房。");
            RaiseChanged();
        }

        /// <summary>
        /// 手工补一个联系点（广播被挡时用）。
        /// 只把它记成"要往这里发公告"的地址，不直接当成成员——
        /// 对方真在房间里的话，它的公告到达后会自动出现在成员列表里。
        /// </summary>
        public void AddManualPeer(string ip, int port)
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a))
            {
                Log("IP 不合法：" + ip);
                return;
            }
            IPEndPoint ep = new IPEndPoint(a, port);
            _net.AddPeer(ep);
            Log("联系点 " + ep);
        }

        public void RemoveMember(string key)
        {
            Member m = FindByKey(key);
            if (m == null) return;
            lock (_lock) { _members.Remove(m); }
            _net.RemovePeer(m.EndPoint);
            _audio.RemoveChannel(m.IdNum);
            Log("移出：" + m.Display);
            RaiseChanged();
        }

        public void SetMemberMuted(string key, bool muted)
        {
            Member m = FindByKey(key);
            if (m == null) return;
            m.Muted = muted;
            _audio.SetChannelMuted(m.IdNum, muted);
            RaiseChanged();
        }

        /// <summary>单独设置某个成员的音量（0..100），只影响本机播放。</summary>
        public void SetMemberVolume(string key, int percent)
        {
            Member m = FindByKey(key);
            if (m == null) return;
            if (percent < 0) percent = 0; else if (percent > 200) percent = 200;   // 与「放音音量」一致，0–200%
            m.Volume = percent;
            _audio.SetChannelVolume(m.IdNum, percent);
            RaiseChanged();
            Log("音量：" + (string.IsNullOrEmpty(m.Name) ? m.Key : m.Name) + " → " + percent + "%");
        }

        /// <summary>按 Key 取成员（供界面查询当前音量/静音状态）。</summary>
        public Member MemberByKey(string key) { return FindByKey(key); }

        private Member FindByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            lock (_lock)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    if (_members[i].Key == key) return _members[i];
                }
            }
            return null;
        }

        // ================== 成员维护 ==================

        private Member AddOrUpdate(IPEndPoint ep, uint idNum, string name)
        {
            if (ep == null) return null;
            lock (_lock)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    Member x = _members[i];
                    if (idNum != 0 && x.IdNum == idNum)      // 身份优先
                    {
                        x.LastSeenUtc = DateTime.UtcNow;
                        if (x.EndPoint == null || !SameEp(x.EndPoint, ep))
                        {
                            // 同一个人可能从不同地址被看到（例如广播来自网卡地址、
                            // 单播来自 127.0.0.1）。换端点时必须把旧的对端摘掉，
                            // 否则会越攒越多，退出时也清理不干净。
                            if (x.EndPoint != null) _net.RemovePeer(x.EndPoint);
                            x.EndPoint = new IPEndPoint(ep.Address, ep.Port);
                        }
                        if (!string.IsNullOrEmpty(name)) x.Name = name;
                        _net.AddPeer(x.EndPoint);
                        return x;
                    }
                    if (idNum == 0 && SameEp(x.EndPoint, ep))
                    {
                        x.LastSeenUtc = DateTime.UtcNow;
                        return x;
                    }
                }

                Member m = new Member();
                m.IdNum = idNum;
                m.Name = name == null ? "" : name;
                m.EndPoint = new IPEndPoint(ep.Address, ep.Port);
                m.LastSeenUtc = DateTime.UtcNow;
                _members.Add(m);

                _net.AddPeer(m.EndPoint);
                _audio.EnsureChannel(m.IdNum, m.EndPoint, m.Name);

                // 提示音放在这里而不是公告分支：新成员可能先被"音频包"发现，
                // 也可能先被"房间公告"发现，只有挂在真正建成员的这一处才两种都能覆盖，
                // 且不会因为两条路径都跑而响两次。
                // 刚进房时会把房间里既有的人一次性发现出来，那不是"有人进来"，要跳过。
                if ((DateTime.UtcNow - _joinedAt).TotalSeconds > JoinChimeSettleSec)
                {
                    _audio.PlayChime(ChimeKind.MemberJoin);
                }
                return m;
            }
        }

        private static bool SameEp(IPEndPoint a, IPEndPoint b)
        {
            if (a == null || b == null) return false;
            return a.Port == b.Port && a.Address.Equals(b.Address);
        }

        private void DropMember(Member m, string why)
        {
            if (m == null) return;
            _net.RemovePeer(m.EndPoint);
            _audio.RemoveChannel(m.IdNum);
            _audio.PlayChime(ChimeKind.MemberLeave);
            Log("成员离开：" + (string.IsNullOrEmpty(m.Name) ? m.Key : m.Name) + (why.Length > 0 ? " · " + why : ""));
        }

        // ================== 收包 ==================

        private void OnAnyAudio(byte[] buf, int offset, int len, long seq, uint senderId, IPEndPoint src)
        {
            if (!_joined) return;
            // 刚主动退出的人，在守期内忽略他还在途中的音频包——
            // 否则"声音即在线"的机制会把已经告别的人重新登记回来。
            lock (_lock)
            {
                DateTime when;
                if (_departed.TryGetValue(senderId, out when)
                    && (DateTime.UtcNow - when).TotalMilliseconds < DepartedGuardMs)
                    return;
            }
            // 声音本身就是"这个人还在"的证据。如果是不认识的人，顺手把他登记进来。
            Member m = FindById(senderId);
            if (m == null)
            {
                if (senderId == 0) return;
                AddOrUpdate(src, senderId, null);
                RaiseChanged();
                return;
            }
            m.LastSeenUtc = DateTime.UtcNow;
        }

        private Member FindById(uint idNum)
        {
            if (idNum == 0) return null;
            lock (_lock)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    if (_members[i].IdNum == idNum) return _members[i];
                }
            }
            return null;
        }

        private void OnControl(Pkt p, byte[] payload, int len, IPEndPoint src)
        {
            if (!_joined) return;

            // 对方缺头像，请求我们发一份（空载荷）
            if (p == Pkt.AvatarWant)
            {
                SendAvatarTo(src);
                return;
            }

            // 头像：载荷重组后是 [实例ID 4][jpeg]，按 ID 找到成员再抛事件
            if (p == Pkt.Avatar)
            {
                _avatarAsm.Add(payload, len, delegate(uint mid, byte[] blob)
                {
                    if (blob == null || blob.Length < 5) return;
                    uint who = (uint)(blob[0] | (blob[1] << 8) | (blob[2] << 16) | (blob[3] << 24));
                    if (who == MyId) return;
                    byte[] jpeg = new byte[blob.Length - 4];
                    Buffer.BlockCopy(blob, 4, jpeg, 0, jpeg.Length);
                    Member m = FindById(who);
                    if (m == null) return;
                    if (!string.IsNullOrEmpty(m.ClientId))
                    {
                        int ah = jpeg.Length ^ (jpeg.Length > 0 ? (jpeg[0] << 8) : 0);
                        PeerStore.SaveAvatar(m.ClientId, jpeg, ah);
                        PeerStore.Prune();
                    }
                    if (m == null) return;
                    Log("头像 " + ((string.IsNullOrEmpty(m.Name) ? m.Key : m.Name)) + " " + (jpeg.Length / 1024) + "KB");
                    Action<string, byte[]> h = AvatarChanged;
                    if (h != null) { try { h(m.Key, jpeg); } catch { } }
                });
                return;
            }

            // 图片分片：拼齐后抛事件（按来源地址标注发送者）
            if (p == Pkt.ImageChunk)
            {
                string imgWho = src == null ? "对方" : src.Address.ToString();
                _imgAsm.Add(payload, len, delegate(uint id, byte[] data)
                {
                    Log("图片 " + (data.Length / 1024) + "KB 来自 " + imgWho);
                    Action<string, byte[]> h = ImageReceived;
                    if (h != null) { try { h(imgWho, data); } catch { } }
                });
                return;
            }

            // 文本消息：同样要求和公告一样"已加入"，但不需要房间号校验
            // （只发给已认识的成员，走单播而不是广播）
            if (p == Pkt.Chat)
            {
                uint chatId;
                string chatName, chatText;
                if (!ChatWire.Parse(payload, len, out chatId, out chatName, out chatText)) return;
                if (chatId == MyId) return;
                if (string.IsNullOrEmpty(chatText)) return;
                if (string.IsNullOrEmpty(chatName)) chatName = src.Address.ToString();
                Log("聊天 " + chatName + "：" + chatText);
                RaiseChat(chatName, chatText);
                return;
            }

            if (p == Pkt.RoomHello || p == Pkt.RoomHere || p == Pkt.RoomBye)
            {
                int port;
                uint idNum;
                string name;
                if (!ParseRoom(payload, len, out port, out idNum, out name)) return;
                if (port != RoomPort) return;          // 不是我这个房间
                if (idNum == MyId) return;             // 自己发的广播（含本机多条路径转回来）

                if (p == Pkt.RoomBye)
                {
                    lock (_lock) { _departed[idNum] = DateTime.UtcNow; }
                    Member gone = FindById(idNum);
                    if (gone != null)
                    {
                        lock (_lock) { _members.Remove(gone); }
                        DropMember(gone, "主动退出");
                        RaiseChanged();
                    }
                    return;
                }

                // 刚退出的人发来的公告一律忽略（守期见 DepartedGuardMs）
                lock (_lock)
                {
                    DateTime when;
                    if (_departed.TryGetValue(idNum, out when)
                        && (DateTime.UtcNow - when).TotalMilliseconds < DepartedGuardMs)
                        return;
                }

                bool isNew = FindById(idNum) == null;
                Member m = AddOrUpdate(src, idNum, name);
                    if (m != null)
                    {
                        m.ClientId = PeerStore.KeyOf(lastClientId);
                        PeerStore.Touch(m.ClientId, name, lastAvatarHash);
                        // 手里没有他当前这一版头像，才去要一份；有就一个字节都不传。
                        if (lastAvatarHash != 0 && !PeerStore.HasAvatar(m.ClientId, lastAvatarHash))
                        {
                            try { _net.SendControl(Pkt.AvatarWant, null, 0, src); } catch { }
                        }
                    }
                if (m != null && isNew)
                {
                    Log("成员加入 " + (string.IsNullOrEmpty(name) ? src.ToString() : name));
                    RaiseChanged();
                    if (p == Pkt.RoomHello)
                    {
                        // 单播回一个应答，让新来的人立刻也知道我（不必等下一轮广播）
                        byte[] mine = BuildRoomPayload();
                        _net.SendControl(Pkt.RoomHere, mine, mine.Length, new IPEndPoint(src.Address, src.Port));
                    }
                }
                return;
            }

            // 其它控制包（心跳等）也算对方还在
            Member mm = FindByEndpoint(src);
            if (mm != null) mm.LastSeenUtc = DateTime.UtcNow;
        }

        private Member FindByEndpoint(IPEndPoint ep)
        {
            if (ep == null) return null;
            lock (_lock)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    if (SameEp(_members[i].EndPoint, ep)) return _members[i];
                }
            }
            return null;
        }

        // ================== 定时驱动 ==================

        public void Tick()
        {
            if (_disposed || !_joined) return;

            // 清理过期的"退出守期"记录
            lock (_lock)
            {
                if (_departed.Count > 0)
                {
                    List<uint> stale = null;
                    DateTime now = DateTime.UtcNow;
                    foreach (KeyValuePair<uint, DateTime> kv in _departed)
                    {
                        if ((now - kv.Value).TotalMilliseconds >= DepartedGuardMs)
                        {
                            if (stale == null) stale = new List<uint>();
                            stale.Add(kv.Key);
                        }
                    }
                    if (stale != null) for (int i = 0; i < stale.Count; i++) _departed.Remove(stale[i]);
                }
            }

            _tickAccumMs += TickMs;
            if (_tickAccumMs >= AnnounceMs)
            {
                _tickAccumMs = 0;
                Announce();
            }

            // 同步每路的音量与收包数给界面用；不说话时让音量条自然衰减下去
            lock (_lock)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    Member m = _members[i];
                    RemoteChannel c = _audio.FindChannel(m.IdNum);
                    if (c == null) continue;
                    int lvl = c.Level;
                    if (lvl > 0)
                    {
                        lvl -= 12;                       // 每次 Tick（100ms）衰减一点
                        if (lvl < 0) lvl = 0;
                        c.Level = lvl;
                    }
                    m.Level = lvl;
                    m.Received = c.Received;
                }
            }

            // 超时清理
            List<Member> dead = null;
            lock (_lock)
            {
                DateTime now = DateTime.UtcNow;
                for (int i = _members.Count - 1; i >= 0; i--)
                {
                    Member m = _members[i];
                    if ((now - m.LastSeenUtc).TotalMilliseconds > MemberTimeoutMs)
                    {
                        if (dead == null) dead = new List<Member>();
                        dead.Add(m);
                        _members.RemoveAt(i);
                    }
                }
            }
            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++) DropMember(dead[i], "长时间无响应");
                RaiseChanged();
            }
        }

        private void Announce()
        {
            byte[] p = BuildRoomPayload();
            int port = RoomPort;

            // 常规路径：往房间端口广播，让同网段的人自己认出我
            if (port > 0)
            {
                try { _net.SendBroadcast(Pkt.RoomHello, p, p.Length, port); }
                catch { }
            }

            // 补充路径：已经认识的成员和手工补的联系点，直接单播一份。
            // 这样即使广播被网络挡住，组网依然能建立和维持。
            IPEndPoint[] peers = _net.Peers;
            for (int i = 0; i < peers.Length; i++)
            {
                try { _net.SendControl(Pkt.RoomHello, p, p.Length, peers[i]); }
                catch { }
            }
        }

        private void SendBye()
        {
            byte[] p = BuildRoomPayload();
            IPEndPoint[] peers = _net.Peers;
            for (int i = 0; i < peers.Length; i++)
            {
                try { _net.SendControl(Pkt.RoomBye, p, p.Length, peers[i]); }
                catch { }
            }
        }

        // ================== 报文编解码 ==================
        // 房间公告见 RoomHeaderLen 处的说明

        // 新格式（1.3 起，不与旧版兼容）：
        //   [端口 2][实例ID 4][ClientId 16][头像hash 4][名字 utf8...]
        // 定长字段都放在名字前面，因为名字是变长的、占满剩余字节。
        private const int RoomHeaderLen = 26;

        private byte[] BuildRoomPayload()
        {
            byte[] nb = Encoding.UTF8.GetBytes(_myName);
            byte[] p = new byte[RoomHeaderLen + nb.Length];
            int port = RoomPort;
            p[0] = (byte)(port & 0xFF);
            p[1] = (byte)((port >> 8) & 0xFF);
            uint id = MyId;
            p[2] = (byte)(id & 0xFF);
            p[3] = (byte)((id >> 8) & 0xFF);
            p[4] = (byte)((id >> 16) & 0xFF);
            p[5] = (byte)((id >> 24) & 0xFF);
            // ClientId + 头像 hash：同一个人换台机器/换个端口也认得出来
            byte[] cid = ClientIdOverride != null ? ClientIdOverride : Profile.ClientIdBytes;
            Buffer.BlockCopy(cid, 0, p, 6, 16);
            int ah = 0;
            if (_myAvatar != null && _myAvatar.Length > 0) ah = _myAvatar.Length ^ (_myAvatar[0] << 8);
            p[22] = (byte)(ah & 0xFF);
            p[23] = (byte)((ah >> 8) & 0xFF);
            p[24] = (byte)((ah >> 16) & 0xFF);
            p[25] = (byte)((ah >> 24) & 0xFF);

            if (nb.Length > 0) Buffer.BlockCopy(nb, 0, p, RoomHeaderLen, nb.Length);
            return p;
        }

        internal static byte[] lastClientId;
        internal static int lastAvatarHash;

        private static bool ParseRoom(byte[] payload, int len, out int port, out uint idNum, out string name)
        {
            port = 0;
            idNum = 0;
            name = "";
            if (payload == null || len < RoomHeaderLen) return false;
            port = payload[0] | (payload[1] << 8);
            idNum = (uint)(payload[2] | (payload[3] << 8) | (payload[4] << 16) | (payload[5] << 24));
            if (len >= 26)
            {
                byte[] cid = new byte[16];
                Buffer.BlockCopy(payload, 6, cid, 0, 16);
                lastClientId = cid;
                lastAvatarHash = payload[22] | (payload[23] << 8) | (payload[24] << 16) | (payload[25] << 24);
            }
            if (len > RoomHeaderLen) name = Encoding.UTF8.GetString(payload, RoomHeaderLen, len - RoomHeaderLen);
            return true;
        }

        /// <summary>把一条文本消息发给房间里所有已认识的成员（单播，不广播）。</summary>
        public bool SendChat(string text)
        {
            if (!_joined || string.IsNullOrEmpty(text)) return false;
            byte[] p = ChatWire.Build(MyId, _myName, text);
            IPEndPoint[] peers = _net.Peers;
            bool any = false;
            for (int i = 0; i < peers.Length; i++)
            {
                try { _net.SendControl(Pkt.Chat, p, p.Length, peers[i]); any = true; }
                catch { }
            }
            return any;
        }

        /// <summary>把一张 JPEG 切成分片发给房间里所有成员。返回片数，0 表示没发出去。</summary>
        public int SendImage(byte[] jpeg)
        {
            if (!_joined || jpeg == null || jpeg.Length == 0) return 0;
            if (jpeg.Length > ImageWire.MaxImageBytes) return -1;

            uint msgId = ((uint)(MyId & 0xFFFF) << 16) | (uint)((++_imgSeq) & 0xFFFF);
            int total = ImageWire.ChunkCount(jpeg.Length);
            int sent = 0;

            IPEndPoint[] peers = _net.Peers;
            for (int ci = 0; ci < total; ci++)
            {
                int off = ci * ImageWire.ChunkPayload;
                int len = Math.Min(ImageWire.ChunkPayload, jpeg.Length - off);
                byte[] chunk = ImageWire.BuildChunk(msgId, ci, total, jpeg, off, len);
                bool any = false;
                for (int i = 0; i < peers.Length; i++)
                {
                    try { _net.SendControl(Pkt.ImageChunk, chunk, chunk.Length, peers[i]); any = true; }
                    catch { }
                }
                if (!any) break;
                sent++;
            }
            Log("发图 " + (jpeg.Length / 1024) + "KB / " + total + " 片");
            return sent;
        }

        /// <summary>设置并广播自己的头像（传 null 表示清除）。</summary>
        public void SetAvatar(byte[] jpeg)
        {
            _myAvatar = jpeg;
            // 不再主动广播：对方发现自己没有这个 hash 时会来要。
            // 下次 Announce 里带的 hash 变化会让对方重新要一次。
        }

        /// <summary>只发给某一个对端（对方要了才发）。</summary>
        private void SendAvatarTo(IPEndPoint peer)
        {
            if (!_joined || peer == null || _myAvatar == null || _myAvatar.Length == 0) return;

            byte[] blob = new byte[4 + _myAvatar.Length];
            uint id = MyId;
            blob[0] = (byte)(id & 0xFF); blob[1] = (byte)((id >> 8) & 0xFF);
            blob[2] = (byte)((id >> 16) & 0xFF); blob[3] = (byte)((id >> 24) & 0xFF);
            Buffer.BlockCopy(_myAvatar, 0, blob, 4, _myAvatar.Length);

            uint msgId = ((uint)(MyId & 0x7FFF) << 16) | (uint)((++_avatarSeq) & 0xFFFF) | 0x80000000u;
            int total = ImageWire.ChunkCount(blob.Length);
            for (int ci = 0; ci < total; ci++)
            {
                int off = ci * ImageWire.ChunkPayload;
                int len = Math.Min(ImageWire.ChunkPayload, blob.Length - off);
                byte[] chunk = ImageWire.BuildChunk(msgId, ci, total, blob, off, len);
                try { _net.SendControl(Pkt.Avatar, chunk, chunk.Length, peer); AvatarChunksSent++; } catch { return; }
            }
            Log("发头像 " + (_myAvatar.Length / 1024) + "KB / " + total + " 片");
        }

        /// <summary>把当前头像发给所有已知成员。</summary>
        public void SendAvatarToAll()
        {
            if (!_joined || _myAvatar == null || _myAvatar.Length == 0) return;

            byte[] blob = new byte[4 + _myAvatar.Length];
            uint id = MyId;
            blob[0] = (byte)(id & 0xFF); blob[1] = (byte)((id >> 8) & 0xFF);
            blob[2] = (byte)((id >> 16) & 0xFF); blob[3] = (byte)((id >> 24) & 0xFF);
            Buffer.BlockCopy(_myAvatar, 0, blob, 4, _myAvatar.Length);

            uint msgId = ((uint)(MyId & 0x7FFF) << 16) | (uint)((++_avatarSeq) & 0xFFFF) | 0x80000000u;
            int total = ImageWire.ChunkCount(blob.Length);
            IPEndPoint[] peers = _net.Peers;
            for (int ci = 0; ci < total; ci++)
            {
                int off = ci * ImageWire.ChunkPayload;
                int len = Math.Min(ImageWire.ChunkPayload, blob.Length - off);
                byte[] chunk = ImageWire.BuildChunk(msgId, ci, total, blob, off, len);
                for (int i = 0; i < peers.Length; i++)
                {
                    try { _net.SendControl(Pkt.Avatar, chunk, chunk.Length, peers[i]); } catch { }
                }
            }
            Log("广播头像 " + (_myAvatar.Length / 1024) + "KB / " + total + " 片");
        }

        private void RaiseChat(string who, string text)
        {
            Action<string, string> h = ChatReceived;
            if (h == null) return;
            try { h(who, text); } catch { }
        }

        public void Dispose()
        {
            // 把攒着的对端缓存落盘，别丢掉最后几秒的记录
            try { PeerStore.Flush(); } catch { }
            if (_disposed) return;
            _disposed = true;
            try { Leave(); } catch { }
            try { _net.ControlReceived -= OnControl; } catch { }
            try { _net.AudioReceived -= OnAnyAudio; } catch { }
        }
    }
}
