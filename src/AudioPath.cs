// AudioPath.cs —— 语音编解码（PCM16 / G.711 µ-law）、抖动缓冲、采集↔网络↔播放的编排
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

namespace LanTalk
{
    internal enum VoiceCodec
    {
        Pcm16 = 0,
        MuLaw = 1
    }

    internal static class CodecUtil
    {
        public static string Name(VoiceCodec c)
        {
            return c == VoiceCodec.MuLaw ? "G.711 µ-law（省流量）" : "PCM16（高保真）";
        }
        public static int BytesPerSample(VoiceCodec c) { return c == VoiceCodec.MuLaw ? 1 : 2; }
        public static VoiceCodec Parse(string s)
        {
            if (s != null && s.IndexOf("ulaw", StringComparison.OrdinalIgnoreCase) >= 0) return VoiceCodec.MuLaw;
            if (s != null && s.IndexOf("mu", StringComparison.OrdinalIgnoreCase) == 0) return VoiceCodec.MuLaw;
            return VoiceCodec.Pcm16;
        }
    }

    /// <summary>G.711 µ-law 压扩。查表实现，编解码都是逐样本 O(1)。</summary>
    internal static class G711
    {
        private const int BIAS = 0x84;
        private const int CLIP = 32635;

        private static readonly byte[] ExpLut;
        private static readonly short[] DecLut;

        static G711()
        {
            ExpLut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int e = 0;
                while (e < 7 && (1 << (e + 1)) <= i) e++;
                ExpLut[i] = (byte)e;
            }

            DecLut = new short[256];
            for (int i = 0; i < 256; i++)
            {
                int u = (~i) & 0xFF;
                int t = ((u & 0x0F) << 3) + BIAS;
                t <<= (u & 0x70) >> 4;
                t -= BIAS;
                DecLut[i] = (short)((u & 0x80) != 0 ? -t : t);
            }
        }

        public static byte Encode(short sample)
        {
            int sign = (sample >> 8) & 0x80;
            int s = sample;
            if (sign != 0) s = -s;
            if (s > CLIP) s = CLIP;
            s += BIAS;
            int exponent = ExpLut[s >> 7];
            int mantissa = (s >> (exponent + 3)) & 0x0F;
            return (byte)(~(sign | (exponent << 4) | mantissa));
        }

        public static short Decode(byte u) { return DecLut[u]; }
    }

    /// <summary>
    /// 抖动缓冲：按序号有序排队，容忍乱序到达；丢包时补静音，迟到帧丢弃；
    /// 队列堆得太深就丢最旧的，防止延迟越积越大。
    /// </summary>
    internal sealed class JitterBuffer : IAudioFeeder
    {
        private sealed class Frame
        {
            public long Seq;
            public byte[] Pcm;
            public int Len;
        }

        private readonly object _lock = new object();
        private readonly List<Frame> _q = new List<Frame>();
        private readonly int _frameBytes;

        private int _targetFrames;
        private int _maxFrames;
        private long _lastPlayed = -1;
        private bool _priming = true;

        private long _played;
        private long _concealed;
        private long _lateDropped;
        private long _overflowDropped;
        private int _depth;
        private int _maxDepthSeen;

        public long Played { get { return Interlocked.Read(ref _played); } }
        public long Concealed { get { return Interlocked.Read(ref _concealed); } }
        public long LateDropped { get { return Interlocked.Read(ref _lateDropped); } }
        public long OverflowDropped { get { return Interlocked.Read(ref _overflowDropped); } }
        public int Depth { get { lock (_lock) return _depth; } }
        public int MaxDepthSeen { get { lock (_lock) return _maxDepthSeen; } }

        /// <summary>targetMs 为期望缓冲深度，maxMs 为超过就丢弃的上限（防止延迟越积越大）。</summary>
        public JitterBuffer(int frameBytes, int frameMs, int targetMs, int maxMs)
        {
            _frameBytes = frameBytes;
            int fm = frameMs <= 0 ? 20 : frameMs;
            _targetFrames = Math.Max(1, targetMs / fm);
            _maxFrames = Math.Max(_targetFrames + 1, maxMs / fm);
        }

        public void SetTarget(int targetMs, int maxMs, int frameMs)
        {
            lock (_lock)
            {
                int fm = frameMs <= 0 ? 20 : frameMs;
                _targetFrames = Math.Max(1, targetMs / fm);
                _maxFrames = Math.Max(_targetFrames + 1, maxMs / fm);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _q.Clear();
                _lastPlayed = -1;
                _priming = true;
                _depth = 0;
            }
        }

        /// <summary>收到一帧（已经解码成 PCM16）。按序号插入，乱序到达也能排回正确位置。</summary>
        public void Push(long seq, byte[] pcm, int len)
        {
            lock (_lock)
            {
                if (_lastPlayed >= 0 && seq <= _lastPlayed)
                {
                    Interlocked.Increment(ref _lateDropped);   // 已经播过的位置，迟到或重复
                    return;
                }

                int i = _q.Count;
                while (i > 0 && _q[i - 1].Seq > seq) i--;
                if (i < _q.Count && _q[i].Seq == seq)
                {
                    Interlocked.Increment(ref _lateDropped);   // 同一序号的重复包
                    return;
                }

                Frame f = new Frame();
                f.Seq = seq;
                f.Pcm = pcm;
                f.Len = len;
                _q.Insert(i, f);

                if (_q.Count > _maxFrames)
                {
                    int drop = _q.Count - _targetFrames;
                    if (drop > 0)
                    {
                        _q.RemoveRange(0, drop);              // 丢最旧的，把延迟压回目标深度
                        Interlocked.Add(ref _overflowDropped, drop);
                    }
                }
                _depth = _q.Count;
                if (_q.Count > _maxDepthSeen) _maxDepthSeen = _q.Count;
            }
        }

        /// <summary>播放端取数据；返回真正写入的字节数，不足部分由播放层补静音。</summary>
        public int Fill(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_priming)
                {
                    if (_q.Count < _targetFrames) { _depth = _q.Count; return 0; }  // 先攒够目标深度再开声
                    _priming = false;
                }

                while (_q.Count > 0 && _q[0].Seq <= _lastPlayed)
                {
                    _q.RemoveAt(0);
                    Interlocked.Increment(ref _lateDropped);
                }
                _depth = _q.Count;

                if (_q.Count == 0)
                {
                    Interlocked.Increment(ref _concealed);
                    return 0;
                }

                Frame f = _q[0];
                _q.RemoveAt(0);
                _depth = _q.Count;

                if (_lastPlayed >= 0 && f.Seq > _lastPlayed + 1)
                {
                    // 中间确实缺帧：缺几帧就记几帧掩盖
                    Interlocked.Add(ref _concealed, f.Seq - _lastPlayed - 1);
                }
                _lastPlayed = f.Seq;
                Interlocked.Increment(ref _played);

                int n = f.Len < count ? f.Len : count;
                System.Buffer.BlockCopy(f.Pcm, 0, buffer, offset, n);
                return n;
            }
        }
    }

    /// <summary>会议里的一路远端声音：独立的抖动缓冲 + 该成员的音量/静音状态。</summary>
    internal sealed class RemoteChannel
    {
        public uint SenderId;              // 发送者标识（按它路由，而不是按 IP）
        public IPEndPoint EndPoint;       // 环回测试时为 null
        public JitterBuffer Buffer;
        public string Name = "";
        public volatile bool Muted;
        /// <summary>这个人单独的音量百分比（0..100），100 为原始音量。</summary>
        public volatile int Volume = 100;
        public long Received;
        public volatile int Level;
        public DateTime LastSeenUtc = DateTime.UtcNow;

        public string Key
        {
            get { return EndPoint == null ? "(环回)" : (SenderId.ToString("x8") + "@" + EndPoint.Address + ":" + EndPoint.Port); }
        }
    }

    /// <summary>
    /// 混音器：把每一路远端声音叠加成一路输出。
    /// 只有一路时就是单纯的搬运（和单人通话的行为完全一致）。
    /// </summary>
    internal sealed class Mixer : IAudioFeeder
    {
        private sealed class ActiveChime
        {
            public short[] Samples;
            public int Pos;
        }

        private readonly AudioPath _owner;
        private byte[] _tmp;
        private int[] _acc;
        private int _peak;

        private readonly List<ActiveChime> _chimes = new List<ActiveChime>();
        private readonly object _chimeLock = new object();
        private const int MaxConcurrentChimes = 4;

        public int Peak { get { return _peak; } }

        public Mixer(AudioPath owner) { _owner = owner; }

        // ---- 耳返：把处理后的麦克风样本排进播放队列，混进同一路输出 ----
        private readonly short[] _mon = new short[8192];
        private int _monRead, _monCount;
        private readonly object _monLock = new object();

        /// <summary>耳返入队。跟不上播放（设备卡顿）时丢最旧的，宁可断续也不让延迟越积越大。</summary>
        public long MonitorPushed;   // 已送进混音器的耳返样本数（自检用）

        public void PushMonitor(short[] src, int len)
        {
            if (src == null || len <= 0) return;
            if (len > _mon.Length) { src = null; return; }
            lock (_monLock)
            {
                if (_monCount + len > _mon.Length)
                {
                    int drop = _monCount + len - _mon.Length;
                    _monRead = (_monRead + drop) % _mon.Length;
                    _monCount -= drop;
                }
                for (int i = 0; i < len; i++) _mon[(_monRead + _monCount + i) % _mon.Length] = src[i];
                _monCount += len;
                MonitorPushed += len;
            }
        }

        public void ClearMonitor() { lock (_monLock) { _monRead = 0; _monCount = 0; } }

        private int MixMonitor(int samples)
        {
            lock (_monLock)
            {
                if (_monCount <= 0) return 0;
                int n = samples < _monCount ? samples : _monCount;
                for (int i = 0; i < n; i++)
                {
                    int v = _mon[(_monRead + i) % _mon.Length];
                    _acc[i] += (int)(v * 0.7);   // 耳返压低一些，避免吓到自己
                }
                _monRead = (_monRead + n) % _mon.Length;
                _monCount -= n;
                return n;
            }
        }

        /// <summary>把一个提示音排进混音队列。多个同时触发会叠加，所以限制了并发数。</summary>
        public void Enqueue(short[] samples)
        {
            if (samples == null || samples.Length == 0) return;
            lock (_chimeLock)
            {
                if (_chimes.Count >= MaxConcurrentChimes) return;
                ActiveChime c = new ActiveChime();
                c.Samples = samples;
                c.Pos = 0;
                _chimes.Add(c);
            }
        }

        /// <summary>
        /// 把当前在播的提示音混进累加器。返回仍在播的数量。
        /// 音量与静音在这里单独应用——因为 ApplyGain 是在**收包时逐帧**做的，
        /// 混音器里加进来的音效不走那条路径，必须自己对一下。
        /// </summary>
        private int MixChimes(int samples)
        {
            lock (_chimeLock)
            {
                if (_chimes.Count == 0) return 0;

                int vol = _owner.SpeakerMuted ? 0 : _owner.VolumePercent;
                if (vol <= 0)
                {
                    // 静音时直接丢弃，避免解除静音后突然补放一个迟到的提示音
                    _chimes.Clear();
                    return 0;
                }

                for (int k = _chimes.Count - 1; k >= 0; k--)
                {
                    ActiveChime c = _chimes[k];
                    int remain = c.Samples.Length - c.Pos;
                    int n = remain < samples ? remain : samples;
                    for (int i = 0; i < n; i++)
                        _acc[i] += c.Samples[c.Pos + i] * vol / 100;
                    c.Pos += n;
                    if (c.Pos >= c.Samples.Length) _chimes.RemoveAt(k);
                }
                return _chimes.Count;
            }
        }

        public int Fill(byte[] buffer, int offset, int count)
        {
            int samples = count / 2;
            if (samples <= 0) return 0;
            if (_tmp == null || _tmp.Length < count) _tmp = new byte[count];
            if (_acc == null || _acc.Length < samples) _acc = new int[samples];
            for (int i = 0; i < samples; i++) _acc[i] = 0;

            RemoteChannel[] chans = _owner.SnapshotChannels();
            int alive = 0;
            for (int c = 0; c < chans.Length; c++)
            {
                int n;
                try { n = chans[c].Buffer.Fill(_tmp, 0, count); }
                catch { n = 0; }
                if (n <= 0) continue;
                alive++;
                int chVol = chans[c].Volume;
                if (chVol < 0) chVol = 0; else if (chVol > 200) chVol = 200;   // 单成员音量也支持到 200%
                for (int i = 0; i + 1 < n; i += 2)
                {
                    int s = _tmp[i] | (_tmp[i + 1] << 8);
                    if (s > 32767) s -= 65536;
                    _acc[i >> 1] += chVol == 100 ? s : (s * chVol) / 100;
                }
            }

            // 提示音混进来（没人说话时也要能响）
            int chimeActive = MixChimes(samples);

            int monActive = MixMonitor(samples);

            if (alive == 0 && chimeActive == 0 && monActive == 0) { _peak = 0; return 0; }   // 全部无声：交给播放层补静音

            int peak = 0;
            for (int i = 0; i < samples; i++)
            {
                int v = _acc[i];
                if (v > 32767) v = 32767;
                else if (v < -32768) v = -32768;
                int a = v < 0 ? -v : v;
                if (a > peak) peak = a;
                buffer[offset + i * 2] = (byte)(v & 0xFF);
                buffer[offset + i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            _peak = (int)(100.0 * Math.Sqrt(peak / 32768.0));
            if (_peak > 100) _peak = 100;
            return count;
        }
    }

    /// <summary>
    /// 一次通话或一场会议的音频通道：采集 -> （静音/按键说话判断、编码）-> 发给名单里所有人；
    /// 收到的每一路各自解码、各自进抖动缓冲，最后由混音器叠加后播放。
    /// </summary>
    internal sealed class AudioPath : IDisposable
    {
        private readonly object _lock = new object();
        private readonly UdpPeer _net;
        private readonly int _sampleRate;
        private readonly int _frameMs;
        private readonly int _frameBytes;
        private readonly Func<IAudioSource> _sourceFactory;
        private readonly Func<IAudioSink> _sinkFactory;

        private IAudioSource _source;
        private IAudioSink _sink;
        private VoiceCodec _codec;
        private volatile bool _running;
        private volatile bool _loopback;
        private long _seq;

        private readonly List<RemoteChannel> _channels = new List<RemoteChannel>();
        private readonly object _chanLock = new object();
        private Mixer _mixer;
        private long _retiredPlayed, _retiredConcealed;   // 已移除通道结转过来的累计值
        private readonly Denoiser _denoiser = new Denoiser();
        private readonly NsDenoiser _nsDenoiser = new NsDenoiser();

        // 工频陷波（电流声）与键盘瞬态抑制。放在降噪之前：
        // 嗡声是窄带、先挖掉最干净；瞬态先压住，免得带坏降噪的噪声估计。
        private HumNotch _humNotch;
        private ClickGuard _clickGuard;
        private short[] _fxBuf;
        private short[] _monBuf;

        /// <summary>耳返：把处理后的麦克风声音混进本地播放，用来自己试听降噪效果。</summary>
        public bool Sidetone;

        /// <summary>麦克风增益百分比 0..150：100 原样，&gt;100 放大（可能削顶）。</summary>
        public int MicGainPercent = 100;

        /// <summary>电流声抑制（工频陷波）。</summary>
        public bool HumFilter;
        /// <summary>键盘等冲击噪声抑制。</summary>
        public bool ClickFilter;

        public HumNotch Hum { get { return _humNotch; } }
        public ClickGuard Click { get { return _clickGuard; } }

        /// <summary>已送进混音器的耳返样本数（自检用它确认耳返真的在工作）。</summary>
        public long MonitorPushed { get { Mixer m = _mixer; return m == null ? 0 : m.MonitorPushed; } }
        private bool _useNsEngine = true;

        private volatile bool _micMuted;
        private volatile bool _spkMuted;
        private volatile bool _pttMode;
        private volatile bool _pttTalking = true;
        private volatile int _volumePercent = 100;
        private int _inLevel;
        private int _outLevel;

        private long _sent, _sentBytes, _decodedFrames, _decodedBytes;

        /// <summary>每收到并解码一帧就触发（seq, pcm, len）——供自检校验端到端一致性。</summary>
        public event Action<long, byte[], int> FrameDecoded;

        public int InputLevel { get { return _inLevel; } }
        /// <summary>混音之后的实际输出电平（比单看某一路更准）。</summary>
        public int OutputLevel { get { Mixer m = _mixer; return m != null ? m.Peak : _outLevel; } }
        public long SentFrames { get { return Interlocked.Read(ref _sent); } }
        public long SentBytes { get { return Interlocked.Read(ref _sentBytes); } }
        public long DecodedFrames { get { return Interlocked.Read(ref _decodedFrames); } }
        public long DecodedBytes { get { return Interlocked.Read(ref _decodedBytes); } }
        public int FrameBytes { get { return _frameBytes; } }
        /// <summary>采集侧降噪器（第一版轻量实现；关闭时逐字节直通）。</summary>
        public Denoiser Denoise { get { return _denoiser; } }
        /// <summary>采集侧降噪器（WebRTC NS 移植版）。</summary>
        public NsDenoiser NsDenoise { get { return _nsDenoiser; } }
        /// <summary>是否使用 WebRTC NS 引擎（默认是；可用 --denoise-engine classic 切回轻量版）。</summary>
        public bool UseNsEngine
        {
            get { return _useNsEngine; }
            set { _useNsEngine = value; }
        }

        /// <summary>两个引擎的档位一起设，界面只要调这一个入口。</summary>
        public void SetDenoiseLevel(DenoiseLevel lv)
        {
            _denoiser.SetLevel(lv);
            _nsDenoiser.SetLevel(lv);
        }
        public int SampleRate { get { return _sampleRate; } }
        public int FrameMs { get { return _frameMs; } }

        /// <summary>
        /// 所有远端通道的汇总统计。已经移除的通道会把它的累计值结转过来，
        /// 所以会议里有人退出后数字也不会回退。
        /// </summary>
        public long PlayedFrames
        {
            get
            {
                long n = Interlocked.Read(ref _retiredPlayed);
                RemoteChannel[] cs = SnapshotChannels();
                for (int i = 0; i < cs.Length; i++) n += cs[i].Buffer.Played;
                return n;
            }
        }

        public long ConcealedFrames
        {
            get
            {
                long n = Interlocked.Read(ref _retiredConcealed);
                RemoteChannel[] cs = SnapshotChannels();
                for (int i = 0; i < cs.Length; i++) n += cs[i].Buffer.Concealed;
                return n;
            }
        }

        /// <summary>最深的那个通道的缓冲深度（毫秒），用于界面显示。</summary>
        public int BufferDepthMs
        {
            get
            {
                int max = 0;
                RemoteChannel[] cs = SnapshotChannels();
                for (int i = 0; i < cs.Length; i++)
                {
                    int d = cs[i].Buffer.Depth * _frameMs;
                    if (d > max) max = d;
                }
                return max;
            }
        }

        public VoiceCodec Codec
        {
            get { return _codec; }
            set { _codec = value; }
        }

        public bool MicMuted { get { return _micMuted; } set { _micMuted = value; } }
        public bool SpeakerMuted { get { return _spkMuted; } set { _spkMuted = value; } }
        public bool PttMode { get { return _pttMode; } set { _pttMode = value; } }
        public bool PttTalking { get { return _pttTalking; } set { _pttTalking = value; } }
        public int VolumePercent
        {
            get { return _volumePercent; }
            set { _volumePercent = value < 0 ? 0 : (value > 200 ? 200 : value); }
        }

        /// <summary>播放一个提示音（进房 / 成员进出）。走混音器，因此受放音音量和静音控制。</summary>
        public void PlayChime(ChimeKind kind)
        {
            Mixer m = _mixer;
            if (m == null) return;
            try { m.Enqueue(Chimes.Make(kind, _sampleRate)); }
            catch { }
        }

        public AudioPath(UdpPeer net, VoiceCodec codec, int sampleRate, int frameMs,
                         Func<IAudioSource> sourceFactory, Func<IAudioSink> sinkFactory)
        {
            _net = net;
            _codec = codec;
            _sampleRate = sampleRate;
            _frameMs = frameMs;
            _frameBytes = sampleRate * 2 * frameMs / 1000;
            _humNotch = new HumNotch(sampleRate);
            _clickGuard = new ClickGuard(sampleRate, Math.Max(1, sampleRate * frameMs / 1000));
            _sourceFactory = sourceFactory;
            _sinkFactory = sinkFactory;
        }

        public void Start(bool loopback)
        {
            lock (_lock)
            {
                if (_running) return;
                _loopback = loopback;
                _seq = 0;
                _denoiser.Reset();
                _nsDenoiser.Reset();
                _sent = 0; _sentBytes = 0; _decodedFrames = 0; _decodedBytes = 0;

                lock (_chanLock) { _channels.Clear(); }
                _mixer = new Mixer(this);
                _retiredPlayed = 0;
                _retiredConcealed = 0;

                _sink = _sinkFactory();
                _sink.SetFeeder(_mixer);
                _sink.Start();

                if (loopback)
                {
                    // 环回测试：自己就是唯一的那一路
                    EnsureChannel(0, null, "本机环回");
                }
                else
                {
                    _net.AudioReceived += OnAudioReceived;
                }

                _source = _sourceFactory();
                _source.DataAvailable += OnCaptured;
                _source.Start();

                _running = true;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                try { if (_source != null) { _source.DataAvailable -= OnCaptured; _source.Stop(); _source.Dispose(); } }
                catch { }
                try { if (_sink != null) { _sink.Stop(); _sink.Dispose(); } }
                catch { }
                if (!_loopback) { try { _net.AudioReceived -= OnAudioReceived; } catch { } }
                _source = null;
                _sink = null;
                // 先结转累计值再清空，这样挂断后读到的播放/补静音次数不会归零
                lock (_chanLock)
                {
                    for (int i = 0; i < _channels.Count; i++)
                    {
                        Interlocked.Add(ref _retiredPlayed, _channels[i].Buffer.Played);
                        Interlocked.Add(ref _retiredConcealed, _channels[i].Buffer.Concealed);
                    }
                    _channels.Clear();
                }
                _inLevel = 0;
                _outLevel = 0;
            }
        }

        // ---------------- 远端通道管理（会议室会有多路） ----------------

        public RemoteChannel[] SnapshotChannels()
        {
            lock (_chanLock) { return _channels.ToArray(); }
        }

        public int ChannelCount { get { lock (_chanLock) return _channels.Count; } }

        /// <summary>按发送者标识找到（必要时创建）通道。senderId 为 0 表示环回。</summary>
        public RemoteChannel EnsureChannel(uint senderId, IPEndPoint ep, string name)
        {
            lock (_chanLock)
            {
                for (int i = 0; i < _channels.Count; i++)
                {
                    RemoteChannel x = _channels[i];
                    // 优先按身份匹配：同一台机器有多条网络路径时，同一个人的声音不会变成两路
                    if (senderId != 0 && x.SenderId == senderId) return x;
                    if (senderId == 0 && x.SenderId == 0 && SameEp(x.EndPoint, ep)) return x;
                }
                RemoteChannel c = new RemoteChannel();
                c.SenderId = senderId;
                c.EndPoint = ep == null ? null : new IPEndPoint(ep.Address, ep.Port);
                c.Name = name == null ? "" : name;
                c.Buffer = new JitterBuffer(_frameBytes, _frameMs, 60, 400);
                _channels.Add(c);
                return c;
            }
        }

        public RemoteChannel FindChannel(uint senderId)
        {
            lock (_chanLock)
            {
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (_channels[i].SenderId == senderId) return _channels[i];
                }
                return null;
            }
        }

        public RemoteChannel FindChannel(IPEndPoint ep)
        {
            lock (_chanLock)
            {
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (SameEp(_channels[i].EndPoint, ep)) return _channels[i];
                }
                return null;
            }
        }

        public bool RemoveChannel(uint senderId)
        {
            RemoteChannel gone = null;
            lock (_chanLock)
            {
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (_channels[i].SenderId == senderId) { gone = _channels[i]; _channels.RemoveAt(i); break; }
                }
            }
            if (gone == null) return false;
            Interlocked.Add(ref _retiredPlayed, gone.Buffer.Played);
            Interlocked.Add(ref _retiredConcealed, gone.Buffer.Concealed);
            return true;
        }

        public bool RemoveChannel(IPEndPoint ep)
        {
            RemoteChannel gone = null;
            lock (_chanLock)
            {
                for (int i = 0; i < _channels.Count; i++)
                {
                    if (SameEp(_channels[i].EndPoint, ep)) { gone = _channels[i]; _channels.RemoveAt(i); break; }
                }
            }
            if (gone == null) return false;
            Interlocked.Add(ref _retiredPlayed, gone.Buffer.Played);
            Interlocked.Add(ref _retiredConcealed, gone.Buffer.Concealed);
            return true;
        }

        /// <summary>单独设置某个人的音量（0..100）。</summary>
        public void SetChannelVolume(uint senderId, int percent)
        {
            RemoteChannel c = FindChannel(senderId);
            if (c == null) return;
            if (percent < 0) percent = 0; else if (percent > 100) percent = 100;
            c.Volume = percent;
        }

        public void SetChannelMuted(uint senderId, bool muted)
        {
            RemoteChannel c = FindChannel(senderId);
            if (c != null) c.Muted = muted;
        }

        private static bool SameEp(IPEndPoint a, IPEndPoint b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.Port == b.Port && a.Address.Equals(b.Address);
        }

        public void Dispose() { Stop(); }

        public string SourceDescription { get { IAudioSource s = _source; return s == null ? "（未打开）" : s.Description; } }
        public string SinkDescription { get { IAudioSink s = _sink; return s == null ? "（未打开）" : s.Description; } }

        // ---------------- 采集侧 ----------------

        /// <summary>电流声陷波 + 键盘瞬态抑制（原地修改 PCM16 字节流）。</summary>
        private void ApplyHumFix(byte[] pcm, int len)
        {
            int n = len / 2;
            if (n <= 0) return;

            bool fx = HumFilter || ClickFilter;
            // 两个开关都关：原样直通，连搬运都省了（耳返不在这里做，见 OnCaptured）
            if (!fx) return;

            if (_fxBuf == null || _fxBuf.Length < n) _fxBuf = new short[n];
            for (int i = 0; i < n; i++) _fxBuf[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));

            if (HumFilter)
            {
                _humNotch.Analyze(_fxBuf, n);
                _humNotch.Process(_fxBuf, n);
            }
            if (ClickFilter) _clickGuard.Process(_fxBuf, n);

            if (!fx) return;   // 什么都不处理就直接直通，保证降噪关闭时 bit-exact

            for (int i = 0; i < n; i++)
            {
                pcm[i * 2] = (byte)(_fxBuf[i] & 0xFF);
                pcm[i * 2 + 1] = (byte)((_fxBuf[i] >> 8) & 0xFF);
            }
        }

        /// <summary>麦克风增益：原地放大/衰减 PCM16，超出范围就削顶。</summary>
        private void ApplyMicGain(byte[] pcm, int len)
        {
            int g = MicGainPercent;
            if (g == 100) return;
            if (g < 0) g = 0;
            if (g > 150) g = 150;
            for (int i = 0; i + 1 < len; i += 2)
            {
                int s = pcm[i] | (pcm[i + 1] << 8);
                if (s > 32767) s -= 65536;
                int v = s * g / 100;
                if (v > 32767) v = 32767;
                else if (v < -32768) v = -32768;
                pcm[i] = (byte)(v & 0xFF);
                pcm[i + 1] = (byte)((v >> 8) & 0xFF);
            }
        }

        /// <summary>把 PCM16 字节流转成 short 后送进混音器（耳返）。</summary>
        private void PushSidetone(byte[] pcm, int len)
        {
            int n = len / 2;
            if (n <= 0) return;
            if (_monBuf == null || _monBuf.Length < n) _monBuf = new short[n];
            for (int i = 0; i < n; i++) _monBuf[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            _mixer.PushMonitor(_monBuf, n);
        }

        private void OnCaptured(byte[] pcm, int len)
        {
            if (!_running) return;
            // 采集后立刻降噪；关闭档位时两条链路都是 bit-exact 直通
            ApplyHumFix(pcm, len);

            if (_useNsEngine) _nsDenoiser.ProcessPcm(pcm, len);
            else _denoiser.ProcessPcm(pcm, len);

            // 麦克风增益放在降噪之后、编码之前：电平表、耳返、发出去的音频看到的是同一个信号
            ApplyMicGain(pcm, len);
            _inLevel = Peak(pcm, len);

            // 耳返取最终信号（过完降噪和增益），听到的就是对方会听到的
            if (Sidetone && _mixer != null) PushSidetone(pcm, len);

            bool silent = _micMuted || (_pttMode && !_pttTalking);
            byte[] payload = Encode(pcm, len, silent);
            int plen = payload.Length;
            long seq = Interlocked.Increment(ref _seq) - 1;

            Interlocked.Increment(ref _sent);
            Interlocked.Add(ref _sentBytes, plen);

            if (_loopback)
            {
                int backLen;
                byte[] back = Decode(payload, 0, plen, out backLen);
                DeliverLocal(seq, back, backLen);
            }
            else if (_net != null)
            {
                _net.SendAudio(payload, plen, seq);
            }
        }

        // ---------------- 网络侧 ----------------

        private void OnAudioReceived(byte[] payload, int offset, int len, long seq, uint senderId, IPEndPoint src)
        {
            if (!_running) return;
            int pcmLen;
            byte[] pcm = Decode(payload, offset, len, out pcmLen);
            ApplyGain(pcm, pcmLen);

            Interlocked.Increment(ref _decodedFrames);
            Interlocked.Add(ref _decodedBytes, pcmLen);

            Action<long, byte[], int> h = FrameDecoded;
            if (h != null) { try { h(seq, pcm, pcmLen); } catch { } }

            RemoteChannel c = EnsureChannel(senderId, src, null);
            c.Level = Peak(pcm, pcmLen);
            c.LastSeenUtc = DateTime.UtcNow;
            Interlocked.Increment(ref c.Received);

            if (c.Muted) return;                      // 点名静音：这一路直接不进混音
            int peak = c.Level;
            if (peak > _outLevel) _outLevel = peak;   // 粗略的"对方声音"指示，混音器会给出精确值
            c.Buffer.Push(seq, pcm, pcmLen);
        }

        private void DeliverLocal(long seq, byte[] pcm, int len)
        {
            ApplyGain(pcm, len);

            Interlocked.Increment(ref _decodedFrames);
            Interlocked.Add(ref _decodedBytes, len);

            Action<long, byte[], int> h = FrameDecoded;
            if (h != null) { try { h(seq, pcm, len); } catch { } }

            RemoteChannel c = EnsureChannel(0, null, "本机环回");
            c.Level = Peak(pcm, len);
            c.LastSeenUtc = DateTime.UtcNow;
            Interlocked.Increment(ref c.Received);
            c.Buffer.Push(seq, pcm, len);
        }

        // ---------------- 编解码与增益 ----------------

        private byte[] Encode(byte[] pcm, int len, bool silent)
        {
            int samples = len / 2;
            if (_codec == VoiceCodec.MuLaw)
            {
                byte[] outb = new byte[samples];
                for (int i = 0; i < samples; i++)
                {
                    short s = silent ? (short)0 : (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                    outb[i] = G711.Encode(s);
                }
                return outb;
            }
            else
            {
                byte[] outb = new byte[len];
                if (!silent) System.Buffer.BlockCopy(pcm, 0, outb, 0, len);
                return outb;
            }
        }

        private byte[] Decode(byte[] payload, int offset, int len, out int pcmLen)
        {
            if (_codec == VoiceCodec.MuLaw)
            {
                byte[] outb = new byte[len * 2];
                for (int i = 0; i < len; i++)
                {
                    short s = G711.Decode(payload[offset + i]);
                    outb[i * 2] = (byte)(s & 0xFF);
                    outb[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                pcmLen = outb.Length;
                return outb;
            }
            else
            {
                byte[] outb = new byte[len];
                System.Buffer.BlockCopy(payload, offset, outb, 0, len);
                pcmLen = len;
                return outb;
            }
        }

        private void ApplyGain(byte[] pcm, int len)
        {
            if (_spkMuted)
            {
                Array.Clear(pcm, 0, len);
                return;
            }
            int vol = _volumePercent;
            if (vol == 100) return;
            for (int i = 0; i + 1 < len; i += 2)
            {
                int s = pcm[i] | (pcm[i + 1] << 8);
                if (s > 32767) s -= 65536;
                s = s * vol / 100;
                if (s > 32767) s = 32767;
                if (s < -32768) s = -32768;
                pcm[i] = (byte)(s & 0xFF);
                pcm[i + 1] = (byte)((s >> 8) & 0xFF);
            }
        }

        private static int Peak(byte[] pcm, int len)
        {
            int peak = 0;
            for (int i = 0; i + 1 < len; i += 2)
            {
                int s = pcm[i] | (pcm[i + 1] << 8);
                if (s > 32767) s -= 65536;
                if (s < 0) s = -s;
                if (s > peak) peak = s;
            }
            double r = peak / 32768.0;
            int lvl = (int)(100.0 * Math.Sqrt(r));
            return lvl > 100 ? 100 : lvl;
        }
    }

    /// <summary>自检用的确定性音源：第 n 个样本固定为 ((n*37) % 20000) - 10000，接收端可逐样本验算。</summary>
    internal sealed class ToneSource : IAudioSource
    {
        private readonly int _sampleRate;
        private readonly int _frameMs;
        private readonly int _frameBytes;
        private Thread _thread;
        private volatile bool _running;
        private long _n;

        public event Action<byte[], int> DataAvailable;
        public string Description { get { return "内置测试音源（确定性锯齿波）"; } }

        public ToneSource(int sampleRate, int frameMs)
        {
            _sampleRate = sampleRate;
            _frameMs = frameMs;
            _frameBytes = sampleRate * 2 * frameMs / 1000;
        }

        public static short SampleAt(long index)
        {
            long v = (index * 37) % 20000;
            if (v < 0) v += 20000;
            return (short)(v - 10000);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
        }

        private void Loop()
        {
            int samples = _frameBytes / 2;
            long next = DateTime.UtcNow.Ticks;
            long step = _frameMs * TimeSpan.TicksPerMillisecond;
            while (_running)
            {
                byte[] buf = new byte[_frameBytes];
                for (int i = 0; i < samples; i++)
                {
                    short s = SampleAt(_n + i);
                    buf[i * 2] = (byte)(s & 0xFF);
                    buf[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                }
                _n += samples;
                Action<byte[], int> h = DataAvailable;
                if (h != null) { try { h(buf, buf.Length); } catch { } }

                next += step;
                long wait = (next - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -200) next = DateTime.UtcNow.Ticks;   // 落后太多就重新对齐
            }
        }

        public void Stop() { _running = false; }
        public void Dispose() { Stop(); }
    }

    /// <summary>自检用的空播放设备：照常按节拍向 feeder 取数据，只是不发声。</summary>
    internal sealed class NullSink : IAudioSink
    {
        private readonly int _frameBytes;
        private readonly int _frameMs;
        private IAudioFeeder _feeder;
        private Thread _thread;
        private volatile bool _running;
        private long _pulls;

        public string Description { get { return "空播放设备（自检）"; } }
        public long Pulls { get { return Interlocked.Read(ref _pulls); } }

        public NullSink(int sampleRate, int frameMs)
        {
            _frameMs = frameMs;
            _frameBytes = sampleRate * 2 * frameMs / 1000;
        }

        public void SetFeeder(IAudioFeeder feeder) { _feeder = feeder; }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Start();
        }

        private void Loop()
        {
            byte[] buf = new byte[_frameBytes];
            long next = DateTime.UtcNow.Ticks;
            long step = _frameMs * TimeSpan.TicksPerMillisecond;
            while (_running)
            {
                IAudioFeeder f = _feeder;
                if (f != null) { try { f.Fill(buf, 0, buf.Length); } catch { } }
                Interlocked.Increment(ref _pulls);
                next += step;
                long wait = (next - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                if (wait > 0) Thread.Sleep((int)wait);
                else if (wait < -200) next = DateTime.UtcNow.Ticks;
            }
        }

        public void Stop() { _running = false; }
        public void Dispose() { Stop(); }
    }
}
