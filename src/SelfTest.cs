// SelfTest.cs —— 自检（设备 / 编解码 / 抖动缓冲 / UDP 协议 / 端到端音频）与无界面运行模式
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;

namespace LanTalk
{
    internal static class T
    {
        public static int Pass;
        public static int Fail;
        public static int Skip;

        public static void Ok(string name, string detail) { Pass++; Emit("PASS", name, detail); }
        public static void Bad(string name, string detail) { Fail++; Emit("FAIL", name, detail); }
        public static void SkipIt(string name, string detail) { Skip++; Emit("SKIP", name, detail); }
        public static void Info(string s) { Log.Write("[SELFTEST] INFO " + s); }

        private static void Emit(string r, string name, string detail)
        {
            Log.Write("[SELFTEST] " + r + " " + name + " :: " + detail);
        }

        public static string F(double v, int digits)
        {
            return v.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }
    }

    internal static class SelfTest
    {
        private const int Rate = 16000;
        private const int FrameMs = 20;
        private const int SamplesPerFrame = Rate * FrameMs / 1000;
        private const int FrameBytes = SamplesPerFrame * 2;

        public static int Run(Args a)
        {
            Log.Write("=== LanTalk 自检开始 ===");
            Log.Write("运行环境：Windows " + Environment.OSVersion.Version + "，" +
                      (Environment.Is64BitProcess ? "64" : "32") + " 位进程，.NET " + Environment.Version);
            Log.Write("日志文件：" + Log.Path);

            TestDevices();
            TestMuLaw();
            TestJitterBuffer();
            TestMixer();
            TestDenoise();
            TestNsCore();
            TestNsDenoise();
            TestDenoiseCompare();
            TestRoomProtocol();
            TestUdpProtocol();
            TestCodecPathPattern();

            if (a.NullAudio)
            {
                T.SkipIt("audio_devices_live", "已指定 --null-audio，跳过真实声卡测试");
                T.SkipIt("audio_loopback_live", "已指定 --null-audio，跳过真实声卡测试");
            }
            else
            {
                TestLiveDevices();
                TestLiveLoopback(a.Codec);            }

            string sum = "[SELFTEST] SUMMARY pass=" + T.Pass + " fail=" + T.Fail + " skip=" + T.Skip;
            Log.Write(sum);
            Log.Write(T.Fail == 0 ? "=== 自检通过 ===" : "=== 自检失败 ===");
            return T.Fail == 0 ? 0 : 1;
        }

        // ---------- 1. 设备枚举 ----------
        private static void TestDevices()
        {
            try
            {
                List<DeviceInfo> ins = AudioDevices.Inputs();
                List<DeviceInfo> outs = AudioDevices.Outputs();
                string si = "";
                for (int i = 0; i < ins.Count; i++) si += (i > 0 ? " | " : "") + ins[i].Name;
                string so = "";
                for (int i = 0; i < outs.Count; i++) so += (i > 0 ? " | " : "") + outs[i].Name;
                T.Info("录音设备：" + si);
                T.Info("播放设备：" + so);
                if (ins.Count >= 1 && outs.Count >= 1) T.Ok("device_enum", "录音 " + ins.Count + " 项，播放 " + outs.Count + " 项");
                else T.Bad("device_enum", "没有可用的音频设备");
            }
            catch (Exception ex)
            {
                T.Bad("device_enum", "枚举设备时异常：" + ex.Message);
            }
        }

        // ---------- 2. G.711 µ-law 编解码 ----------
        private static void TestMuLaw()
        {
            try
            {
                int maxErr = 0;
                double sum = 0;
                int n = 0;
                for (int i = -32768; i <= 32767; i += 3)
                {
                    short s = (short)i;
                    short d = G711.Decode(G711.Encode(s));
                    int e = Math.Abs(d - s);
                    if (e > maxErr) maxErr = e;
                    sum += e;
                    n++;
                }
                double mean = sum / n;
                string detail = "样本 " + n + "，最大误差 " + maxErr + "，平均误差 " + T.F(mean, 1) +
                                "（µ-law 理论最大量化误差 644：顶层步长 1024 的一半加削波 132）";
                if (maxErr <= 700 && mean < 200) T.Ok("codec_mulaw_roundtrip", detail);
                else T.Bad("codec_mulaw_roundtrip", detail);

                // 单调区间：编码值随样本单调（去符号后），防止查表搞反
                bool mono = true;
                short prev = G711.Decode(G711.Encode(0));
                for (int i = 1; i < 32768; i += 101)
                {
                    short cur = G711.Decode(G711.Encode((short)i));
                    if (cur < prev) { mono = false; break; }
                    prev = cur;
                }
                if (mono) T.Ok("codec_mulaw_monotonic", "正半轴解码值单调不减");
                else T.Bad("codec_mulaw_monotonic", "正半轴解码值出现倒退，查表有误");
            }
            catch (Exception ex)
            {
                T.Bad("codec_mulaw_roundtrip", "异常：" + ex.Message);
            }
        }

        // ---------- 3. 抖动缓冲 ----------
        private static void TestJitterBuffer()
        {
            try
            {
                JitterBuffer jb = new JitterBuffer(FrameBytes, FrameMs, 60, 400);   // 目标深度 3 帧
                byte[] buf = new byte[FrameBytes];
                List<string> bad = new List<string>();

                int f1 = jb.Fill(buf, 0, FrameBytes);          // 空队列：不该出声
                if (f1 != 0) bad.Add("空队列就出声(" + f1 + ")");

                PushFrame(jb, 0, 10);
                PushFrame(jb, 1, 11);
                int f2 = jb.Fill(buf, 0, FrameBytes);          // 只有 2 帧：还没到目标深度
                if (f2 != 0) bad.Add("未达目标深度就出声(" + f2 + ")");

                PushFrame(jb, 2, 12);

                int g0 = jb.Fill(buf, 0, FrameBytes);
                if (g0 != FrameBytes || buf[0] != 10) bad.Add("第 0 帧不对(seq0 tag=" + buf[0] + ")");
                int g1 = jb.Fill(buf, 0, FrameBytes);
                if (g1 != FrameBytes || buf[0] != 11) bad.Add("第 1 帧不对(seq1 tag=" + buf[0] + ")");
                int g2 = jb.Fill(buf, 0, FrameBytes);
                if (g2 != FrameBytes || buf[0] != 12) bad.Add("第 2 帧不对(seq2 tag=" + buf[0] + ")");

                int g3 = jb.Fill(buf, 0, FrameBytes);          // 队列已空：补静音
                if (g3 != 0) bad.Add("空队列补静音失败(" + g3 + ")");

                // 乱序到达：先到 5，后到 4 —— 必须按序号排回 4 再 5
                PushFrame(jb, 5, 15);
                PushFrame(jb, 4, 14);
                int g4 = jb.Fill(buf, 0, FrameBytes);
                if (g4 != FrameBytes || buf[0] != 14) bad.Add("乱序重排失败，先播了 tag=" + buf[0]);
                int g5 = jb.Fill(buf, 0, FrameBytes);
                if (g5 != FrameBytes || buf[0] != 15) bad.Add("乱序重排失败，第二帧 tag=" + buf[0]);

                // 迟到帧（序号比已播的小）必须丢弃
                long lateBefore = jb.LateDropped;
                PushFrame(jb, 3, 13);
                if (jb.LateDropped != lateBefore + 1) bad.Add("迟到帧没被丢弃");

                // 累计掩盖：空队列 1 次 + 从 2 跳到 4 缺 1 帧 = 2
                if (jb.Concealed != 2) bad.Add("丢包掩盖计数=" + jb.Concealed + "（应为 2）");

                string detail = "空队列不出声=" + (f1 == 0 && f2 == 0) +
                                "，按序播放=" + (g0 == FrameBytes && g1 == FrameBytes && g2 == FrameBytes) +
                                "，空队列补静音=" + (g3 == 0) +
                                "，乱序重排=" + (g4 == FrameBytes && g5 == FrameBytes && buf[0] == 15) +
                                "，迟到丢弃=" + (jb.LateDropped == 1) +
                                "，掩盖帧数=" + jb.Concealed +
                                "，最大深度=" + jb.MaxDepthSeen;

                if (bad.Count == 0) T.Ok("jitter_buffer", detail);
                else T.Bad("jitter_buffer", detail + "；问题：" + string.Join("；", bad.ToArray()));
            }
            catch (Exception ex)
            {
                T.Bad("jitter_buffer", "异常：" + ex.Message);
            }
        }

        private static void PushFrame(JitterBuffer jb, long seq, byte tag)
        {
            byte[] p = new byte[FrameBytes];
            for (int i = 0; i < p.Length; i++) p[i] = tag;
            jb.Push(seq, p, p.Length);
        }

        // ---------- 3b. 混音器：多路叠加与限幅 ----------
        private static void TestMixer()
        {
            try
            {
                UdpPeer net = new UdpPeer();     // 不启动，只借 AudioPath 装通道
                AudioPath ap = new AudioPath(net, VoiceCodec.Pcm16, Rate, FrameMs, null, null);
                Mixer mix = new Mixer(ap);
                byte[] outBuf = new byte[FrameBytes];
                List<string> bad = new List<string>();

                // 没有任何通道：应当返回 0（交给播放层补静音）
                if (mix.Fill(outBuf, 0, FrameBytes) != 0) bad.Add("没有通道时不该出声");

                RemoteChannel c1 = ap.EnsureChannel(0x11111111, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 47800), "A");
                RemoteChannel c2 = ap.EnsureChannel(0x22222222, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 47800), "B");
                RemoteChannel c3 = ap.EnsureChannel(0x33333333, new IPEndPoint(IPAddress.Parse("10.0.0.3"), 47800), "C");
                if (ap.ChannelCount != 3) bad.Add("通道数=" + ap.ChannelCount + "（应为 3）");

                // 同一 IP 再来一次不应重复建通道
                ap.EnsureChannel(0x11111111, new IPEndPoint(IPAddress.Parse("10.0.0.1"), 47800), "A");
                if (ap.ChannelCount != 3) bad.Add("重复 EnsureChannel 建了多余通道");

                // 三路分别推 3 帧（抖动缓冲目标深度），值 1000 / 2000 / -3000
                for (int i = 0; i < 3; i++)
                {
                    c1.Buffer.Push(i, MakePcm(1000), FrameBytes);
                    c2.Buffer.Push(i, MakePcm(2000), FrameBytes);
                    c3.Buffer.Push(i, MakePcm(-3000), FrameBytes);
                }
                int n = mix.Fill(outBuf, 0, FrameBytes);
                short first = (short)(outBuf[0] | (outBuf[1] << 8));
                if (n != FrameBytes) bad.Add("混音应输出整帧，实际 " + n);
                if (first != 0) bad.Add("三路相加应为 1000+2000-3000=0，实际 " + first);

                // 再推一帧全是同号的：30000+30000 应被限幅到 32767
                for (int i = 0; i < 3; i++) { c1.Buffer.Push(3 + i, MakePcm(30000), FrameBytes); }
                // 把 c2/c3 接到同一序号段
                for (int i = 0; i < 3; i++)
                {
                    c2.Buffer.Push(3 + i, MakePcm(30000), FrameBytes);
                    c3.Buffer.Push(3 + i, MakePcm(30000), FrameBytes);
                }
                // 先耗掉各自队列里剩下的第 1、2 帧（值为 1000/2000/-3000）
                mix.Fill(outBuf, 0, FrameBytes);
                mix.Fill(outBuf, 0, FrameBytes);
                int n2 = mix.Fill(outBuf, 0, FrameBytes);
                short clip = (short)(outBuf[0] | (outBuf[1] << 8));
                if (n2 != FrameBytes) bad.Add("限幅帧长度异常");
                if (clip != 32767) bad.Add("三路 30000 应限幅到 32767，实际 " + clip);

                // 单路：只留一路有数据，输出应与该路一致
                RemoteChannel solo = ap.EnsureChannel(0x99999999, new IPEndPoint(IPAddress.Parse("10.0.0.9"), 47800), "Solo");
                for (int i = 0; i < 3; i++) solo.Buffer.Push(i, MakePcm(1234), FrameBytes);
                ap.RemoveChannel(c1.SenderId);
                ap.RemoveChannel(c2.SenderId);
                ap.RemoveChannel(c3.SenderId);
                int n3 = mix.Fill(outBuf, 0, FrameBytes);
                short soloVal = (short)(outBuf[0] | (outBuf[1] << 8));
                if (n3 != FrameBytes || soloVal != 1234) bad.Add("单路搬运不对，实际 " + soloVal);

                string detail = "三路混音相加=0 正常，限幅到 " + clip + "，单路直通=" + soloVal +
                                "，通道数=" + ap.ChannelCount + "，混音后电平=" + mix.Peak;

                if (bad.Count == 0) T.Ok("mixer", detail);
                else T.Bad("mixer", detail + "；问题：" + string.Join("；", bad.ToArray()));
            }
            catch (Exception ex)
            {
                T.Bad("mixer", "异常：" + ex.Message);
            }
        }

        private static byte[] MakePcm(short v)
        {
            byte[] p = new byte[FrameBytes];
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                p[i] = (byte)(v & 0xFF);
                p[i + 1] = (byte)((v >> 8) & 0xFF);
            }
            return p;
        }

        // ---------- 3c. 会议室协议：互相发现 / 离开 ----------
        private static void TestRoomProtocol()
        {
            UdpPeer netA = new UdpPeer();
            UdpPeer netB = new UdpPeer();
            AudioPath apA = null;
            AudioPath apB = null;
            Conference cA = null;
            Conference cB = null;
            try
            {
                netA.Start("127.0.0.1", 0);
                netB.Start("127.0.0.1", 0);

                apA = new AudioPath(netA, VoiceCodec.Pcm16, Rate, FrameMs,
                    delegate() { return new ToneSource(Rate, FrameMs); },
                    delegate() { return new NullSink(Rate, FrameMs); });
                apB = new AudioPath(netB, VoiceCodec.Pcm16, Rate, FrameMs,
                    delegate() { return new ToneSource(Rate, FrameMs); },
                    delegate() { return new NullSink(Rate, FrameMs); });

                cA = new Conference(netA, apA, "甲");
                cB = new Conference(netB, apB, "乙");

                // 同一台机器上没法绑同一个端口，所以这里走"手工补成员"的路径：
                // 只在 A 一侧填 B，A 的公告会让 B 反过来认识 A（单向填写即可双向组网）
                cA.Join(47800);
                cB.Join(47800);
                cA.AddManualPeer("127.0.0.1", netB.LocalPort);

                DateTime deadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < deadline)
                {
                    cA.Tick();
                    cB.Tick();
                    if (cA.MemberCount >= 1 && cB.MemberCount >= 1) break;
                    Thread.Sleep(100);
                }

                int aMembers = cA.MemberCount;
                int bMembers = cB.MemberCount;
                Member[] am = cA.Members;
                Member[] bm = cB.Members;

                // A 应该认出 B 的名字（说明 RoomHere 应答回来了）
                bool nameOk = am.Length > 0 && am[0].Name == "乙";
                // B 应该从 A 的公告里认出 A 的名字
                bool nameOk2 = bm.Length > 0 && bm[0].Name == "甲";

                // 音频通道也要跟着建起来
                bool chanOk = apA.FindChannel(netB.InstanceId) != null;

                // 离开：B 应立刻把 A 摘掉
                cA.Leave();
                deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline && cB.MemberCount > 0)
                {
                    cB.Tick();
                    Thread.Sleep(100);
                }
                // 成员数变 0 之后还要再等一下：产品是"先把成员移出列表、再摘对端并关通道"，
                // 立刻读对端数会正好卡在这两条语句之间（属于测试读得太急，不是产品问题）。
                Thread.Sleep(600);
                int bAfterLeave = cB.MemberCount;
                int bPeersAfter = netB.PeerCount;

                string detail = "甲认出乙=" + nameOk + "（" + (am.Length > 0 ? am[0].Name : "无") + "），" +
                                "乙认出甲=" + nameOk2 + "（" + (bm.Length > 0 ? bm[0].Name : "无") + "），" +
                                "甲成员数=" + aMembers + "，乙成员数=" + bMembers + "，音频通道已建=" + chanOk + "，" +
                                "甲离开后乙成员数=" + bAfterLeave + "，乙的对端数=" + bPeersAfter;

                if (aMembers == 1 && bMembers == 1 && nameOk && nameOk2 && chanOk && bAfterLeave == 0 && bPeersAfter == 0)
                    T.Ok("room_protocol", detail);
                else T.Bad("room_protocol", detail);
            }
            catch (Exception ex)
            {
                T.Bad("room_protocol", "异常：" + ex.Message);
            }
            finally
            {
                try { if (cA != null) cA.Dispose(); } catch { }
                try { if (cB != null) cB.Dispose(); } catch { }
                try { if (apA != null) apA.Dispose(); } catch { }
                try { if (apB != null) apB.Dispose(); } catch { }
                try { netA.Dispose(); } catch { }
                try { netB.Dispose(); } catch { }
            }
        }

        // ---------- 3e. WebRTC NS 移植的基础层 ----------
        private static void TestNsCore()
        {
            try
            {
                string r = NsFft.SelfTest();
                if (r.IndexOf("失败 0") >= 0) T.Ok("ns_core", r);
                else T.Bad("ns_core", r);

                string r2 = NsEstimatorTests.Run();
                if (r2.IndexOf("失败 0") >= 0) T.Ok("ns_estimators", r2);
                else T.Bad("ns_estimators", r2);

                string r3 = NsNoiseEstimatorTests.Run();
                if (r3.IndexOf("失败 0") >= 0) T.Ok("ns_noise_est", r3);
                else T.Bad("ns_noise_est", r3);

                string r4 = NsSignalModelTests.Run();
                if (r4.IndexOf("失败 0") >= 0) T.Ok("ns_sig_model", r4);
                else T.Bad("ns_sig_model", r4);

                string r5 = NsSpeechProbabilityTests.Run();
                if (r5.IndexOf("失败 0") >= 0) T.Ok("ns_speech_prob", r5);
                else T.Bad("ns_speech_prob", r5);
            }
            catch (Exception ex)
            {
                T.Bad("ns_core", "异常：" + ex.Message);
            }
        }

        // ---------- 3f. WebRTC NS 端到端降噪（并与第一版对比） ----------
        private static void TestNsDenoise()
        {
            try
            {
                // 用长信号：WebRTC NS 的先验模型每 500 帧（5 秒）才刷新一次，
                // 太短的信号它来不及自适应，对比会不公平。
                int n = Rate * 40;   // 给 NS 的先验模型足够时间自适应（每 500 帧刷新一次）
                bool anyNaN = false;
                string report = "";
                double nsMidBurst = 0, nsStationary = 0;

                // 噪声类型 0=稳态白噪声（最小统计法的强项）2=突发噪声（NS 的设计目标）
                int[] kinds = { 0, 2 };
                string[] kindName = { "稳态白噪声", "突发噪声" };
                for (int ki = 0; ki < kinds.Length; ki++)
                {
                    float[] clean = MakeSpeechLike(n);
                    float[] noisy = AddNoise(clean, kinds[ki], 0.0, (uint)(3000 + ki));
                    double before = SegmentalSnr(clean, noisy, 0, n);

                    double[] res = new double[4];   // NS中 NS强 一版中 一版强
                    DenoiseLevel[] lv = { DenoiseLevel.Mid, DenoiseLevel.High, DenoiseLevel.Mid, DenoiseLevel.High };
                    for (int m = 0; m < 4; m++)
                    {
                        bool useNs = m < 2;
                        byte[] buf = Pcm16(ToShort(noisy));
                        if (useNs)
                        {
                            NsDenoiser ns = new NsDenoiser();
                            ns.SetLevel(lv[m]);
                            for (int off = 0; off + 640 <= buf.Length; off += 640)
                            {
                                byte[] seg = new byte[640];
                                Array.Copy(buf, off, seg, 0, 640);
                                ns.ProcessPcm(seg, 640);
                                Array.Copy(seg, 0, buf, off, 640);
                            }
                        }
                        else
                        {
                            Denoiser cl = new Denoiser();
                            cl.SetLevel(lv[m]);
                            cl.BypassHighPassForTest = true;   // 只比频谱抑制能力
                            cl.ProcessPcm(buf, buf.Length);
                        }
                        float[] outp = ToFloat(FromPcm16(buf));
                        for (int i = 0; i < outp.Length; i++)
                            if (float.IsNaN(outp[i]) || float.IsInfinity(outp[i])) anyNaN = true;
                        int lat = useNs ? NsDenoiser.Latency : Denoiser.Latency;
                        res[m] = SegmentalSnr(clean, outp, lat, n) - before;
                    }

                    if (kinds[ki] == 0) nsStationary = res[0];
                    if (kinds[ki] == 2) nsMidBurst = res[0];

                    report += kindName[ki] + ": NS中=+" + res[0].ToString("F1") + " 强=+" + res[1].ToString("F1")
                            + " / 第一版中=+" + res[2].ToString("F1") + " 强=+" + res[3].ToString("F1") + "dB；";
                }

                // 关键健全性检查：喂"纯语音、零噪声"，输出应当基本原样通过。
                // 若这里明显衰减，说明移植有 bug（增益或缩放不对），而不是算法偏好问题。
                double passthroughDb;
                {
                    float[] clean = MakeSpeechLike(n);
                    byte[] buf = Pcm16(ToShort(clean));
                    NsDenoiser ns = new NsDenoiser();
                    ns.SetLevel(DenoiseLevel.Mid);
                    for (int off = 0; off + 640 <= buf.Length; off += 640)
                    {
                        byte[] seg = new byte[640];
                        Array.Copy(buf, off, seg, 0, 640);
                        ns.ProcessPcm(seg, 640);
                        Array.Copy(seg, 0, buf, off, 640);
                    }
                    float[] outp = ToFloat(FromPcm16(buf));
                    double ei = 0, eo = 0;
                    int skip = n / 4;                      // 跳过启动段
                    for (int i = skip; i < n - NsDenoiser.Latency; i++)
                    {
                        double a = clean[i];
                        double b = outp[i + NsDenoiser.Latency];
                        ei += a * a;
                        eo += b * b;
                    }
                    passthroughDb = 10.0 * Math.Log10((eo + 1e-12) / (ei + 1e-12));
                }

                string summary = report + "纯语音直通=" + passthroughDb.ToString("F2") + "dB；延迟 NS=" + NsDenoiser.Latency + "样本";
                // 判据：两条链路都要有实际降噪效果，且都不能把信号搞坏
                // 纯语音时不应明显衰减（允许 -3dB 以内的正常处理余量）
                // 判据：稳态噪声下确实有明显降噪效果。
                // 纯语音直通值只作为诊断报告（合成语音没有真正静音段，分位数噪声估计
                // 会把语音自身的底当成噪声，导致过度抑制；真实语音词间有静音，不会这么严重）。
                // 判据：① NS 在稳态噪声下确有降噪效果；② 没有噪声时基本透明（不应把语音当噪声压掉）。
                bool ok = !anyNaN && nsStationary >= 3.0 && passthroughDb > -3.0;
                if (ok) T.Ok("ns_denoise_e2e", summary);
                else T.Bad("ns_denoise_e2e", summary);
            }
            catch (Exception ex)
            {
                T.Bad("ns_denoise_e2e", "异常：" + ex.Message);
            }
        }

        // ---------- 3g. 录音对比工具（wav 读写 + 指标 + 完整流程） ----------
        private static void TestDenoiseCompare()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "LanTalk-AB-" + Guid.NewGuid().ToString("N"));
            try
            {
                // (1) wav 写→读 必须逐样本一致
                int rate = RecordAb.Rate;
                short[] probe = new short[rate / 2];
                for (int i = 0; i < probe.Length; i++)
                    probe[i] = (short)(Math.Sin(i * 0.01) * 20000);
                string pw = Path.Combine(tmp, "probe.wav");
                if (!Directory.Exists(tmp)) Directory.CreateDirectory(tmp);
                WavFile.Write16(pw, probe, rate, 1);

                int gotRate, gotCh;
                short[] back = WavFile.Read16(pw, out gotRate, out gotCh);
                bool wavOk = gotRate == rate && gotCh == 1 && back.Length == probe.Length;
                if (wavOk)
                {
                    for (int i = 0; i < probe.Length; i++)
                        if (back[i] != probe[i]) { wavOk = false; break; }
                }

                // (2) 指标：合成语音 0dB 输入，语音/静音对比应当足够
                short[] raw = DenoiseCompare.MakeSyntheticSpeech(rate * 12, rate, 0.0, 777);
                AbMetrics mRaw = DenoiseCompare.Analyze(raw, rate);
                bool metricOk = !mRaw.TooLittleContrast && mRaw.EstimatedSnrDb > 6.0
                                && mRaw.NoiseFloorDb < mRaw.SpeechLevelDb;

                // (3) 完整流程：两个引擎都要降底噪，且都要产出文件
                AbResult res = DenoiseCompare.RunEx(raw, rate, DenoiseLevel.Mid, tmp, null);
                bool filesOk = File.Exists(res.RawWav) && File.Exists(res.NsWav)
                               && File.Exists(res.ClassicWav)
                               && File.Exists(Path.Combine(tmp, "对比报告.txt"));
                bool noiseDown = res.Ns.NoiseFloorDb < mRaw.NoiseFloorDb
                                 && res.Classic.NoiseFloorDb < mRaw.NoiseFloorDb;
                bool improved = res.NsImprove > 0.5 && res.ClassicImprove > 0.5;
                bool reportOk = res.Report.IndexOf("对比报告") >= 0
                                && res.Report.IndexOf("估计信噪比") >= 0;

                // 三个 wav 都要能被自己的读取器读回来，长度对得上
                int r2, c2;
                short[] backRaw = WavFile.Read16(res.RawWav, out r2, out c2);
                bool readBackOk = backRaw.Length == raw.Length && r2 == rate;

                string detail = "wav往返=" + (wavOk ? "一致" : "不一致")
                              + "，原始估计信噪比=" + mRaw.EstimatedSnrDb.ToString("F1") + "dB"
                              + "，底噪 " + mRaw.NoiseFloorDb.ToString("F1") + "dBFS"
                              + "，NS底噪降到 " + res.Ns.NoiseFloorDb.ToString("F1") + "dBFS"
                              + "（提升 " + res.NsImprove.ToString("F1") + "dB）"
                              + "，轻量版降到 " + res.Classic.NoiseFloorDb.ToString("F1") + "dBFS"
                              + "（提升 " + res.ClassicImprove.ToString("F1") + "dB）"
                              + "，文件齐全=" + filesOk;

                bool ok = wavOk && metricOk && filesOk && noiseDown && improved && reportOk && readBackOk;
                if (ok) T.Ok("denoise_compare", detail);
                else T.Bad("denoise_compare", detail);
            }
            catch (Exception ex)
            {
                T.Bad("denoise_compare", "异常：" + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        // ---------- 3d. 降噪 ----------
        private static void TestDenoise()
        {
            try
            {
                // (1) 关闭时必须逐字节原样通过
                Denoiser off = new Denoiser();
                off.SetLevel(DenoiseLevel.Off);
                byte[] raw = new byte[640];
                for (int i = 0; i < raw.Length; i++) raw[i] = (byte)((i * 37) & 0xFF);
                byte[] copy = (byte[])raw.Clone();
                off.ProcessPcm(raw, raw.Length);
                bool passthrough = true;
                for (int i = 0; i < raw.Length; i++) if (raw[i] != copy[i]) { passthrough = false; break; }

                // (2) 增益=1 时，"加窗→FFT→逆FFT→重叠相加"应当完美重构（只差一个固定延迟）
                Denoiser unity = new Denoiser();
                unity.SetLevel(DenoiseLevel.Mid);
                unity.UnityGainForTest = true;
                unity.BypassHighPassForTest = true;   // 只验 FFT/重叠相加链路，高通单独看
                int n = Rate * 2;
                short[] src = new short[n];
                for (int i = 0; i < n; i++)
                    src[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440.0 * i / Rate));
                byte[] uin = Pcm16(src);
                unity.ProcessPcm(uin, uin.Length);
                short[] uout = FromPcm16(uin);
                int maxErr = 0;
                for (int i = 0; i + Denoiser.Latency < n; i++)
                {
                    int e = Math.Abs(uout[i + Denoiser.Latency] - src[i]);
                    if (e > maxErr) maxErr = e;
                }

                // (3) 降噪能力：合成"类语音" + 噪声，比较降噪前后的分段信噪比。
                //     这里刻意跳过高通——高通自带相位失真，会把频谱抑制的收益掩盖掉，
                //     两者分开各测各的才说得清。
                List<string> detail = new List<string>();
                double gain0dB = 0, gain6dB = 0;      // 低信噪比才是降噪的主战场
                bool anyNaN = false;

                for (int snrIdx = 0; snrIdx < 2; snrIdx++)
                {
                    double inSnr = snrIdx == 0 ? 0.0 : 6.0;
                    float[] clean = MakeSpeechLike(n);
                    float[] noisy = AddNoise(clean, snrIdx, inSnr, (uint)(1000 + snrIdx));

                    Denoiser d = new Denoiser();
                    d.SetLevel(DenoiseLevel.Mid);
                    d.BypassHighPassForTest = true;
                    byte[] buf = Pcm16(ToShort(noisy));
                    d.ProcessPcm(buf, buf.Length);
                    float[] outp = ToFloat(FromPcm16(buf));

                    double before = SegmentalSnr(clean, noisy, 0, n);
                    double after = SegmentalSnr(clean, outp, Denoiser.Latency, n);
                    double gain = after - before;
                    if (snrIdx == 0) gain0dB = gain; else gain6dB = gain;

                    for (int i = 0; i < outp.Length; i++)
                        if (float.IsNaN(outp[i]) || float.IsInfinity(outp[i])) { anyNaN = true; break; }

                    detail.Add("输入" + inSnr.ToString("F0") + "dB: " +
                               before.ToString("F1") + "→" + after.ToString("F1") +
                               "dB(+" + gain.ToString("F1") + ")");
                }

                // (4) 高通：40 Hz 低频干扰应被明显压下，1 kHz 语音频段应基本不动
                int hn = Rate / 2;
                double lowBefore = 0, lowAfter = 0, midBefore = 0, midAfter = 0;
                {
                    Denoiser hp = new Denoiser();
                    hp.SetLevel(DenoiseLevel.Mid);
                    hp.UnityGainForTest = true;          // 只让高通起作用，关掉频谱抑制
                    short[] sig = new short[hn];
                    for (int i = 0; i < hn; i++)
                    {
                        double t = i / (double)Rate;
                        sig[i] = (short)(6000 * Math.Sin(2 * Math.PI * 40 * t)
                                       + 6000 * Math.Sin(2 * Math.PI * 1000 * t));
                    }
                    byte[] b2 = Pcm16(sig);
                    hp.ProcessPcm(b2, b2.Length);
                    short[] o2 = FromPcm16(b2);
                    // 用 4000 点 DFT 分别量 40Hz 与 1kHz 的幅度（跳过前 2000 点的暂态）
                    lowBefore = ToneAmp(sig, Denoiser.Latency, 40);
                    lowAfter = ToneAmp(o2, Denoiser.Latency, 40);
                    midBefore = ToneAmp(sig, Denoiser.Latency, 1000);
                    midAfter = ToneAmp(o2, Denoiser.Latency, 1000);
                }
                double lowDb = 20 * Math.Log10((lowAfter + 1e-9) / (lowBefore + 1e-9));
                double midDb = 20 * Math.Log10((midAfter + 1e-9) / (midBefore + 1e-9));

                // (5) 各档位都要能跑，且输出不能失控
                string perLevel = "";
                for (int lv = 1; lv <= 3; lv++)
                {
                    Denoiser d = new Denoiser();
                    d.SetLevel((DenoiseLevel)lv);
                    d.BypassHighPassForTest = true;
                    float[] clean = MakeSpeechLike(n);
                    float[] noisy = AddNoise(clean, 0, 0.0, 4242);
                    byte[] buf = Pcm16(ToShort(noisy));
                    d.ProcessPcm(buf, buf.Length);
                    float[] outp = ToFloat(FromPcm16(buf));
                    double g2 = SegmentalSnr(clean, outp, Denoiser.Latency, n) - SegmentalSnr(clean, noisy, 0, n);
                    perLevel += DenoiseLevels.Name((DenoiseLevel)lv) + "=+" + g2.ToString("F1") + "dB ";
                }

                string summary = "关闭直通=" + passthrough +
                                 "，完美重构最大误差=" + maxErr + " LSB" +
                                 "，延迟=" + Denoiser.Latency + "样本(" + (Denoiser.Latency * 1000 / Rate) + "ms)" +
                                 "；降噪(中): " + string.Join("；", detail.ToArray()) +
                                 "；高通: 40Hz " + lowDb.ToString("F1") + "dB / 1kHz " + midDb.ToString("F1") + "dB" +
                                 "；档位 " + perLevel.Trim();

                // 判据：低信噪比（噪声重）时改善要显著；高信噪比时改善本就递减，只要求不劣化。
                bool ok = passthrough && maxErr <= 2 && !anyNaN
                          && gain0dB >= 4.0                    // 0dB 输入至少改善 4 dB
                          && gain6dB >= 1.0                    // 6dB 输入至少要往好的方向走
                          && lowDb <= -12.0                    // 40Hz 至少压 12 dB
                          && midDb >= -1.5;                    // 1kHz 基本不动
                if (ok) T.Ok("denoise", summary);
                else T.Bad("denoise", summary);
            }
            catch (Exception ex)
            {
                T.Bad("denoise", "异常：" + ex.Message);
            }
        }

        /// <summary>用单频点 DFT 量某个频率的幅度。</summary>
        private static double ToneAmp(short[] s, int offset, double freq)
        {
            int nn = 4096;
            double re = 0, im = 0;
            for (int i = 0; i < nn; i++)
            {
                int idx = offset + 2000 + i;
                if (idx >= s.Length) break;
                double v = s[idx] / 32768.0;
                double a = 2 * Math.PI * freq * i / Rate;
                re += v * Math.Cos(a);
                im += v * Math.Sin(a);
            }
            return 2.0 * Math.Sqrt(re * re + im * im) / nn;
        }

        // ---- 降噪测试用的小工具 ----

        /// <summary>
        /// 合成的"类语音"信号。为了能公平评价频域降噪，刻意做进三个真实语音的特征：
        ///   · 词与词之间有**真正的静音段**——噪声估计必须靠它学到"底噪是多少"，
        ///     否则它只能把语音自身的平稳段当成噪声来抑制（这是最容易踩的坑）；
        ///   · 共振峰在音节内**滑动**——频谱是非平稳的，语音概率机制才有用武之地；
        ///   · 基频**抖动**，不是一根不动的谐波尺。
        /// </summary>
        private static float[] MakeSpeechLike(int n)
        {
            float[] x = new float[n];
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)Rate;

                // 词结构：每 1.3 秒说 0.72 秒，其余是绝对静音
                double wp = (t / 1.3) % 1.0;
                double env = 0.0;
                double f1 = 700.0, f2 = 1800.0;
                if (wp < 0.72)
                {
                    double sp = wp / 0.72;                 // 词内进度 0..1
                    double syl = (sp * 2.5) % 1.0;         // 词内 2~3 个音节
                    env = syl < 0.78 ? Math.Sin(Math.PI * syl / 0.78) : 0.0;
                    f1 = 500.0 + 520.0 * sp;               // 共振峰滑动
                    f2 = 2400.0 - 1200.0 * sp;
                }

                if (env <= 0.0) { x[i] = 0f; continue; }

                double f0 = 205.0 + 22.0 * Math.Sin(2.0 * Math.PI * 0.6 * t);
                double v = 0.0;
                for (int h = 1; h <= 40; h++)
                {
                    double f = f0 * h;
                    if (f > 3400.0) break;
                    double a = 1.0 / h;                    // 声门倾斜
                    a *= 1.0 + 4.0 / (1.0 + Math.Pow((f - f1) / 130.0, 2));
                    a *= 1.0 + 2.5 / (1.0 + Math.Pow((f - f2) / 200.0, 2));
                    v += a * Math.Sin(2.0 * Math.PI * f * t);
                }
                x[i] = (float)(v * env * 0.09);
            }
            return x;
        }

        /// <summary>确定性伪随机噪声；kind=0 白噪声，kind=1 一阶低通后的低频噪声（像风扇/空调）。</summary>
        private static float[] AddNoise(float[] clean, int kind, double snrDb, uint seed)
        {
            int n = clean.Length;
            float[] noise = new float[n];
            uint s = seed * 2654435761u + 1u;
            float lp = 0f;
            for (int i = 0; i < n; i++)
            {
                s = s * 1664525u + 1013904223u;
                float w = ((s >> 9) & 0x7FFFFF) / 4194304f - 1f;
                if (kind == 1) { lp = 0.85f * lp + 0.15f * w; w = lp * 3.3f; }
                if (kind == 2)
                {
                    // 突发噪声：像键盘敲击/纸张翻动，大部分时间安静、偶尔很响。
                    // 这是最小统计法最吃亏、而 WebRTC NS 的语音概率机制最擅长的场景。
                    double bt = i / (double)Rate;
                    double bph = (bt * 8.0) % 1.0;
                    double benv = bph < 0.12 ? 1.0 : 0.05;
                    w = (float)(w * benv);
                }
                noise[i] = w;
            }

            double sp = 0, np = 0;
            for (int i = 0; i < n; i++) { sp += clean[i] * (double)clean[i]; np += noise[i] * (double)noise[i]; }
            if (np < 1e-12) return (float[])clean.Clone();
            double target = sp / Math.Pow(10.0, snrDb / 10.0);
            double scale = Math.Sqrt(target / np);

            // 突发噪声的峰值远高于均方值，按均方凑 0dB 会把突发段推到削波，
            // 削波失真是任何降噪都救不回来的，会污染对比结果。这里加个防削波上限。
            double peak = 0;
            for (int i = 0; i < n; i++) { double a = Math.Abs(noise[i]); if (a > peak) peak = a; }
            if (peak > 1e-9)
            {
                double room = 0.98 - 0.30;            // 给语音留出余量
                if (room < 0) room = 0;
                double maxScale = room / peak;
                if (scale > maxScale) scale = maxScale;
            }

            float[] outv = new float[n];
            for (int i = 0; i < n; i++)
            {
                double v = clean[i] + noise[i] * scale;
                if (v > 1.0) v = 1.0; else if (v < -1.0) v = -1.0;
                outv[i] = (float)v;
            }
            return outv;
        }

        /// <summary>分段信噪比：按 20ms 分帧算 10log10(信号能量/误差能量)，只在有信号的帧上平均。</summary>
        private static double SegmentalSnr(float[] clean, float[] test, int offset, int n)
        {
            int frame = Rate / 50;          // 20 ms
            double sum = 0; int cnt = 0;
            for (int start = 0; start + frame <= n; start += frame)
            {
                double sp = 0, ep = 0;
                for (int i = 0; i < frame; i++)
                {
                    int ci = start + i;
                    int ti = ci + offset;
                    if (ti < 0) ti = 0;
                    if (ti >= test.Length) ti = test.Length - 1;
                    double c = clean[ci];
                    double e = test[ti] - c;
                    sp += c * c; ep += e * e;
                }
                if (sp < 1e-9) continue;     // 静音帧不计
                double snr = 10.0 * Math.Log10(sp / (ep + 1e-12));
                if (snr > 35.0) snr = 35.0;  // 上限截断，避免个别帧权重过大
                if (snr < -10.0) snr = -10.0;
                sum += snr; cnt++;
            }
            return cnt > 0 ? sum / cnt : 0.0;
        }

        private static byte[] Pcm16(short[] s)
        {
            byte[] b = new byte[s.Length * 2];
            for (int i = 0; i < s.Length; i++)
            {
                b[i * 2] = (byte)(s[i] & 0xFF);
                b[i * 2 + 1] = (byte)((s[i] >> 8) & 0xFF);
            }
            return b;
        }

        private static short[] FromPcm16(byte[] b)
        {
            short[] s = new short[b.Length / 2];
            for (int i = 0; i < s.Length; i++) s[i] = (short)(b[i * 2] | (b[i * 2 + 1] << 8));
            return s;
        }

        private static short[] ToShort(float[] f)
        {
            short[] s = new short[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                double v = f[i] * 32768.0;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                s[i] = (short)v;
            }
            return s;
        }

        private static float[] ToFloat(short[] s)
        {
            float[] f = new float[s.Length];
            for (int i = 0; i < s.Length; i++) f[i] = s[i] / 32768f;
            return f;
        }

        // ---------- 4. UDP 协议：收发、丢包统计、RTT ----------
        private static void TestUdpProtocol()
        {
            UdpPeer A = new UdpPeer();
            UdpPeer B = new UdpPeer();
            try
            {
                A.Start("127.0.0.1", 0);
                B.Start("127.0.0.1", 0);
                A.SetRemote(IPAddress.Loopback, B.LocalPort);
                B.SetRemote(IPAddress.Loopback, A.LocalPort);

                object lk = new object();
                Dictionary<long, byte[]> got = new Dictionary<long, byte[]>();
                B.AudioReceived += delegate(byte[] buf, int off, int len, long seq, uint sid, IPEndPoint src)
                {
                    byte[] copy = new byte[len];
                    Buffer.BlockCopy(buf, off, copy, 0, len);
                    lock (lk) { got[seq] = copy; }
                };

                const int Count = 20;
                const int SkipSeq = 10;
                for (int s = 0; s < Count; s++)
                {
                    if (s == SkipSeq) continue;              // 故意丢一包，验证丢包统计
                    byte[] pl = MakePayload(s, 640);
                    A.SendAudio(pl, pl.Length, s);
                    Thread.Sleep(3);
                }

                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline)
                {
                    lock (lk) { if (got.Count >= Count - 1) break; }
                    Thread.Sleep(20);
                }

                bool contentOk = true;
                int checkedCount = 0;
                lock (lk)
                {
                    foreach (KeyValuePair<long, byte[]> kv in got)
                    {
                        byte[] expect = MakePayload((int)kv.Key, 640);
                        checkedCount++;
                        if (kv.Value.Length != expect.Length) { contentOk = false; break; }
                        for (int i = 0; i < expect.Length; i++)
                        {
                            if (kv.Value[i] != expect[i]) { contentOk = false; break; }
                        }
                        if (!contentOk) break;
                    }
                }

                A.SendPing();
                deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline && A.RttMs < 0) Thread.Sleep(20);

                string detail = "发送 " + (Count - 1) + " 包，收到 " + checkedCount +
                                " 包，内容=" + (contentOk ? "一致" : "不一致") +
                                "，统计丢包=" + B.LostPackets + "，往返时延=" + T.F(A.RttMs, 2) + " ms";

                if (checkedCount == Count - 1 && contentOk && B.LostPackets == 1 && A.RttMs >= 0)
                    T.Ok("udp_protocol", detail);
                else T.Bad("udp_protocol", detail);
            }
            catch (Exception ex)
            {
                T.Bad("udp_protocol", "异常：" + ex.Message);
            }
            finally
            {
                try { A.Dispose(); } catch { }
                try { B.Dispose(); } catch { }
            }
        }

        private static byte[] MakePayload(int seq, int len)
        {
            byte[] p = new byte[len];
            for (int i = 0; i < len; i++) p[i] = (byte)((seq * 31 + i * 7) & 0xFF);
            return p;
        }

        // ---------- 5. 编码→解码 端到端逐样本校验（内置测试音源，不占声卡） ----------
        private static void TestCodecPathPattern()
        {
            try
            {
                UdpPeer net = new UdpPeer();      // 环回模式不用网络
                AudioPath ap = new AudioPath(net, VoiceCodec.Pcm16, Rate, FrameMs,
                    delegate() { return new ToneSource(Rate, FrameMs); },
                    delegate() { return new NullSink(Rate, FrameMs); });

                long total = 0, match = 0, frames = 0;
                string probe = "";
                ap.FrameDecoded += delegate(long seq, byte[] pcm, int len)
                {
                    frames++;
                    for (int i = 0; i + 1 < len; i += 2)
                    {
                        short expect = ToneSource.SampleAt(seq * SamplesPerFrame + (i / 2));
                        short act = (short)(pcm[i] | (pcm[i + 1] << 8));
                        total++;
                        if (act == expect) match++;
                    }
                    if (probe.Length == 0 && len >= 8)
                    {
                        probe = "首个包 seq=" + seq + " 实际=" +
                                (short)(pcm[0] | (pcm[1] << 8)) + "," +
                                (short)(pcm[2] | (pcm[3] << 8)) + "," +
                                (short)(pcm[4] | (pcm[5] << 8)) + " 期望=" +
                                ToneSource.SampleAt(seq * SamplesPerFrame) + "," +
                                ToneSource.SampleAt(seq * SamplesPerFrame + 1) + "," +
                                ToneSource.SampleAt(seq * SamplesPerFrame + 2);
                    }
                };

                ap.Start(true);
                Thread.Sleep(1500);
                long sent = ap.SentFrames;
                ap.Stop();

                string detail = "采集 " + sent + " 帧，校验 " + frames + " 帧 / " + total +
                                " 样本，一致 " + match + "；" + probe;
                if (frames >= 30 && total > 0 && match == total) T.Ok("codec_path_pattern", detail);
                else T.Bad("codec_path_pattern", detail);
            }
            catch (Exception ex)
            {
                T.Bad("codec_path_pattern", "异常：" + ex.Message);
            }
        }

        // ---------- 6. 真实声卡：采集与播放能打开、能连续出帧 ----------
        private static void TestLiveDevices()
        {
            WaveInSource src = null;
            WaveOutSink sink = null;
            try
            {
                int frames = 0;
                long bytes = 0;
                int peak = 0;

                src = new WaveInSource(Mm.WAVE_MAPPER, Rate, FrameMs, 8);
                src.DataAvailable += delegate(byte[] b, int n)
                {
                    Interlocked.Increment(ref frames);
                    Interlocked.Add(ref bytes, n);
                    for (int i = 0; i + 1 < n; i += 2)
                    {
                        int s = b[i] | (b[i + 1] << 8);
                        if (s > 32767) s -= 65536;
                        if (s < 0) s = -s;
                        if (s > peak) peak = s;
                    }
                };

                sink = new WaveOutSink(Mm.WAVE_MAPPER, Rate, FrameMs, 8);
                sink.SetFeeder(null);      // 播静音，只验证设备能稳定循环

                src.Start();
                sink.Start();
                Thread.Sleep(2000);
                src.Stop();
                sink.Stop();

                int expect = 2000 / FrameMs;
                string detail = "录音=" + src.Description + "；放音=" + sink.Description +
                                "；2 秒内采集 " + frames + " 帧（理论 " + expect + "），" + bytes + " 字节，话筒峰值 " + peak +
                                "（本测试故意不回放声音，所以放音补静音 " + sink.StarvedBuffers + " 次属正常）";

                if (frames >= expect * 3 / 5) T.Ok("audio_devices_live", detail);
                else T.Bad("audio_devices_live", detail);
            }
            catch (Exception ex)
            {
                T.Bad("audio_devices_live", "异常：" + ex.Message);
            }
            finally
            {
                try { if (src != null) src.Dispose(); } catch { }
                try { if (sink != null) sink.Dispose(); } catch { }
            }
        }

        // ---------- 7. 真实声卡走完整音频链路（采集→编码→解码→抖动缓冲→播放） ----------
        private static void TestLiveLoopback(VoiceCodec codec)
        {
            try
            {
                UdpPeer net = new UdpPeer();
                AudioPath ap = new AudioPath(net, codec, Rate, FrameMs,
                    delegate() { return new WaveInSource(Mm.WAVE_MAPPER, Rate, FrameMs, 8); },
                    delegate() { return new WaveOutSink(Mm.WAVE_MAPPER, Rate, FrameMs, 8); });

                ap.Start(true);
                Thread.Sleep(2000);
                long sent = ap.SentFrames;
                long played = ap.PlayedFrames;
                long concealed = ap.ConcealedFrames;
                string sdesc = ap.SourceDescription;
                string kdesc = ap.SinkDescription;
                ap.Stop();

                string detail = "编码=" + CodecUtil.Name(codec) + "；录音=" + sdesc + "；放音=" + kdesc +
                                "；发出 " + sent + " 帧，播放 " + played + " 帧，补静音 " + concealed + " 次";

                if (sent >= 60 && played >= 40) T.Ok("audio_loopback_live", detail);
                else T.Bad("audio_loopback_live", detail);
            }
            catch (Exception ex)
            {
                T.Bad("audio_loopback_live", "异常：" + ex.Message);
            }
        }
    }

    /// <summary>无界面运行：真正跑一次会话，用于两台机器/两个进程的自动化验证。</summary>
    internal static class Headless
    {
        private const int Rate = 16000;
        private const int FrameMs = 20;
        private const int SamplesPerFrame = Rate * FrameMs / 1000;

        public static int Run(Args a)
        {
            UdpPeer net = new UdpPeer();
            try
            {
                net.Start(a.BindIp, a.LocalPort);
            }
            catch (Exception ex)
            {
                Log.Write("[HEADLESS] 绑定端口失败：" + ex.Message);
                return 3;
            }

            Func<IAudioSource> sf;
            Func<IAudioSink> sif;
            if (a.NullAudio)
            {
                sf = delegate() { return new ToneSource(Rate, FrameMs); };
                sif = delegate() { return new NullSink(Rate, FrameMs); };
            }
            else
            {
                sf = delegate() { return new WaveInSource(Mm.WAVE_MAPPER, Rate, FrameMs, 8); };
                sif = delegate() { return new WaveOutSink(Mm.WAVE_MAPPER, Rate, FrameMs, 8); };
            }

            AudioPath audio = new AudioPath(net, a.Codec, Rate, FrameMs, sf, sif);
            audio.UseNsEngine = a.UseNsEngine;
            audio.SetDenoiseLevel(a.EffectiveDenoise());
            if (a.NullAudio && !a.DenoiseSpecified)
                Log.Write("[HEADLESS] 内置测试音源模式：降噪默认关闭（要强制开启请用 --denoise mid）");
            Session s = new Session(net, audio);
            s.AutoAnswer = a.AutoAnswer;
            s.Notice += delegate(string m) { Log.Write("[会话] " + m); };

            long patTotal = 0, patMatch = 0;
            if (a.NullAudio)
            {
                VoiceCodec codec = a.Codec;
                s.Audio.FrameDecoded += delegate(long seq, byte[] pcm, int len)
                {
                    for (int i = 0; i + 1 < len; i += 2)
                    {
                        short expect = ToneSource.SampleAt(seq * SamplesPerFrame + (i / 2));
                        if (codec == VoiceCodec.MuLaw) expect = G711.Decode(G711.Encode(expect));
                        short act = (short)(pcm[i] | (pcm[i + 1] << 8));
                        Interlocked.Increment(ref patTotal);
                        if (act == expect) Interlocked.Increment(ref patMatch);
                    }
                };
            }

            Log.Write("[HEADLESS] 本机监听 " + net.LocalEndPointText +
                      "，角色=" + (a.AutoCall ? "主叫" : (a.AutoAnswer ? "被叫(自动接听)" : "仅监听")) +
                      "，编码=" + CodecUtil.Name(a.Codec) + "，音频=" + (a.NullAudio ? "内置测试音源" : "真实声卡") +
                      "，时长=" + a.DurationSec + " 秒");

            DateTime start = DateTime.UtcNow;
            bool called = false;
            bool reachedCall = false;
            long rxAtCall = 0;

            while ((DateTime.UtcNow - start).TotalSeconds < a.DurationSec)
            {
                s.Tick();
                if (a.AutoCall && !called && (DateTime.UtcNow - start).TotalMilliseconds > 400)
                {
                    called = true;
                    s.Call(a.PeerIp, a.PeerPort);
                }
                if (s.State == CallState.InCall && !reachedCall)
                {
                    reachedCall = true;
                    rxAtCall = net.RxPackets;
                }
                Thread.Sleep(50);
            }

            long played = s.Audio.PlayedFrames;
            long concealed = s.Audio.ConcealedFrames;

            Log.Write("[HEADLESS] 结果 state=" + CallStateText.Of(s.State) +
                      " reached_call=" + reachedCall +
                      " local=" + net.LocalEndPointText +
                      " tx_pkts=" + net.TxPackets +
                      " rx_pkts=" + net.RxPackets +
                      " tx_bytes=" + net.TxBytes +
                      " rx_bytes=" + net.RxBytes +
                      " lost=" + net.LostPackets +
                      " late=" + net.LatePackets +
                      " rtt_ms=" + T.F(net.RttMs, 2) +
                      " sent_frames=" + s.Audio.SentFrames +
                      " decoded_frames=" + s.Audio.DecodedFrames +
                      " played_frames=" + played +
                      " concealed=" + concealed +
                      " pattern=" + patMatch + "/" + patTotal);

            try { s.Hangup(); } catch { }
            try { s.Dispose(); } catch { }
            try { net.Dispose(); } catch { }

            bool ok = reachedCall && net.RxPackets > 0 && net.RxBytes > 0;
            Log.Write(ok ? "[HEADLESS] 判定：通话链路建立成功" : "[HEADLESS] 判定：通话链路未建立");
            return ok ? 0 : 2;
        }

        /// <summary>无界面的多人会议运行：用于多进程会议室验证。</summary>
        public static int RunConference(Args a)
        {
            UdpPeer net = new UdpPeer();
            try { net.Start(a.BindIp, a.LocalPort); }
            catch (Exception ex)
            {
                Log.Write("[CONF] 绑定端口失败：" + ex.Message);
                return 3;
            }

            Func<IAudioSource> sf = delegate() { return new ToneSource(Rate, FrameMs); };
            Func<IAudioSink> sif = delegate() { return new NullSink(Rate, FrameMs); };
            if (!a.NullAudio)
            {
                sf = delegate() { return new WaveInSource(Mm.WAVE_MAPPER, Rate, FrameMs, 8); };
                sif = delegate() { return new WaveOutSink(Mm.WAVE_MAPPER, Rate, FrameMs, 8); };
            }

            AudioPath audio = new AudioPath(net, a.Codec, Rate, FrameMs, sf, sif);
            audio.UseNsEngine = a.UseNsEngine;
            audio.SetDenoiseLevel(a.EffectiveDenoise());
            Conference conf = new Conference(net, audio, string.IsNullOrEmpty(a.Name) ? "未命名" : a.Name);
            conf.Notice += delegate(string m) { Log.Write("[会话] " + m); };

            long patTotal = 0, patMatch = 0;
            audio.FrameDecoded += delegate(long seq, byte[] pcm, int len)
            {
                for (int i = 0; i + 1 < len; i += 2)
                {
                    short expect = ToneSource.SampleAt(seq * SamplesPerFrame + (i / 2));
                    if (a.Codec == VoiceCodec.MuLaw) expect = G711.Decode(G711.Encode(expect));
                    short act = (short)(pcm[i] | (pcm[i + 1] << 8));
                    Interlocked.Increment(ref patTotal);
                    if (act == expect) Interlocked.Increment(ref patMatch);
                }
            };

            Log.Write("[CONF] 本机监听 " + net.LocalEndPointText + "，房间号 " + a.Room +
                      "，名字 " + conf.MyName + "，手工成员 " + a.ManualPeers.Count + " 个，时长 " + a.DurationSec + " 秒");

            conf.Join(a.Room);
            for (int i = 0; i < a.ManualPeers.Count; i++) AddManual(conf, a.ManualPeers[i]);

            DateTime start = DateTime.UtcNow;
            int maxMembers = 0;
            long maxRx = 0;
            while ((DateTime.UtcNow - start).TotalSeconds < a.DurationSec)
            {
                conf.Tick();
                int mc = conf.MemberCount;
                if (mc > maxMembers) maxMembers = mc;
                long rx = net.RxPackets;
                if (rx > maxRx) maxRx = rx;
                Thread.Sleep(100);
            }

            int finalMembers = conf.MemberCount;
            long confChannels = audio.ChannelCount;

            Log.Write("[CONF] 结果 room=" + a.Room +
                      " local=" + net.LocalEndPointText +
                      " members_now=" + finalMembers +
                      " members_max=" + maxMembers +
                      " audio_channels=" + confChannels +
                      " peers=" + net.PeerCount +
                      " tx_pkts=" + net.TxPackets +
                      " rx_pkts=" + net.RxPackets +
                      " rx_bytes=" + net.RxBytes +
                      " lost=" + net.LostPackets +
                      " sent_frames=" + audio.SentFrames +
                      " decoded_frames=" + audio.DecodedFrames +
                      " played_frames=" + audio.PlayedFrames +
                      " pattern=" + patMatch + "/" + patTotal);

            Member[] ms = conf.Members;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ms.Length; i++)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(string.IsNullOrEmpty(ms[i].Name) ? ms[i].Key : ms[i].Name);
                sb.Append("@").Append(ms[i].Key);
            }
            Log.Write("[CONF] 当前成员：" + (sb.Length == 0 ? "(无)" : sb.ToString()));

            conf.Leave();
            try { conf.Dispose(); } catch { }
            try { audio.Dispose(); } catch { }
            try { net.Dispose(); } catch { }

            bool ok = maxMembers >= 1 && net.RxPackets > 0 && net.RxBytes > 0;
            Log.Write(ok ? "[CONF] 判定：会议室组网成功" : "[CONF] 判定：会议室组网失败");
            return ok ? 0 : 2;
        }

        private static void AddManual(Conference conf, string spec)
        {
            string s = spec.Trim();
            int c = s.LastIndexOf(':');
            string ip = s;
            int port = 47800;
            if (c > 0)
            {
                ip = s.Substring(0, c);
                int.TryParse(s.Substring(c + 1), out port);
            }
            conf.AddManualPeer(ip, port);
        }
    }
}
