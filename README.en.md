# LanTalk — LAN Voice Calling for Windows

[中文文档](README.md) | **English**

A voice-call app for two or more PCs on the same LAN. **If the two machines can ping each other,
it should just work** — no server, no account, no configuration file.

**Zero third-party dependencies.** Pure C# 5 compiled against .NET Framework 4.0 with the
in-box `csc.exe`. No NuGet packages, no native DLLs, no Rust/C toolchain, no model files.

> Noise suppression is a **two-stage** effort: a lightweight classical engine (high-pass + 256-point
> FFT + minimum-statistics noise estimation + Wiener gain + overlap-add), and a full **C# port of
> WebRTC's noise suppressor** (via the Rust rewrite [sonora-ns]). Both engines are kept and are
> switchable in the UI. See [§5.4](#54-noise-suppression-engine-a--lightweight-c) and
> [§5.5](#55-noise-suppression-engine-b--webrtc-ns-ported-to-c).

---

## Contents

1. [Why "if it pings, it works" mostly holds](#1-why-if-it-pings-it-works-mostly-holds)
2. [Two ways to use it](#2-two-ways-to-use-it)
3. [One-to-one call in three steps](#3-one-to-one-call-in-three-steps)
4. [Multi-party conference (3–8 people)](#4-multi-party-conference-38-people)
5. [How it works](#5-how-it-works)
6. [Verified test results](#6-verified-test-results)
7. [UI guide](#7-ui-guide)
8. [Command-line arguments](#8-command-line-arguments)
9. [File layout](#9-file-layout)
10. [Known limitations](#10-known-limitations)
11. [Troubleshooting](#11-troubleshooting)
12. [Third-party content and licensing](#12-third-party-content-and-licensing)

---

## 1. Why "if it pings, it works" mostly holds

LanTalk uses **UDP peer-to-peer with no server**. It only needs unicast reachability between the two
machines, which is exactly what `ping` proves.

Two cases where ping succeeds but audio does not:

1. **Windows Firewall blocks the inbound UDP port** → a rule is created by the installer, or you can
   approve the prompt that appears on first run.
2. **A switch has client isolation enabled** → devices on the same Wi-Fi cannot talk to each other
   even though they can both reach the gateway. LanTalk cannot fix this; use a cable or a hotspot.

Broadcast discovery additionally needs broadcast to be permitted, which many enterprise networks
block. That is why there is always a manual "type the peer's IP" path.

---

## 2. Two ways to use it

### Option A — Installer (recommended)

Download `LanTalk-Setup-*.exe` from the Releases page and run it. It installs per-user to
`%LOCALAPPDATA%\Programs\LanTalk`, creates Desktop and Start Menu shortcuts, registers an
uninstaller, and offers to add a firewall rule (that step is the only one that needs elevation).

```
LanTalk-Setup-1.0.0.exe --silent --dir D:\LanTalk --no-firewall --no-launch
Uninstall.exe --silent
```

### Option B — Portable single file

Just run `LanTalk.exe`. Nothing is written outside its own folder and its log file.

### Building from source (fully offline)

Requirements: Windows with the in-box .NET Framework 4.0 compiler. Nothing else.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1          # -> dist\LanTalk.exe
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-setup.ps1    # -> dist\LanTalk-Setup-1.0.0.exe
```

`build.ps1` invokes `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` directly.
The build scripts are UTF-8 **with BOM** on purpose — Windows PowerShell 5.1 would otherwise read
them as GBK and fail to parse the Chinese strings.

---

## 3. One-to-one call in three steps

1. **Both sides**: enter the same **port** (default `47800`) and click **Apply and re-listen**.
2. **Caller**: type the other machine's IP into **Peer IP** and click **Call**
   (or click **Scan LAN** to find it by broadcast).
3. **Callee**: click **Answer** when it rings (tick **Auto answer** to skip this).

The **Test connection** button sends a probe and reports round-trip time without starting a call.

---

## 4. Multi-party conference (3–8 people)

### 4.1 How to join

Everyone enters the **same room number** and switches to **Conference** mode. The room number *is*
the UDP port, so there is no server to run. Members appear automatically within a few seconds.

### 4.2 If broadcast is blocked

Add the other members' `IP:port` under **Manual peers**. One-sided entry is enough — once your
announcement reaches them, they learn about you and the mesh forms in both directions.

### 4.3 Full mesh + local mixing

Every participant sends audio directly to every other participant and mixes whatever arrives.
There is no forwarding, so the host does not carry anyone else's traffic.

### 4.4 Bandwidth

At 16 kHz mono: PCM16 ≈ 256 kbps each way per person, G.711 µ-law ≈ 128 kbps.
With N participants each machine sends N−1 streams.

### 4.5 Always-on room terminal

```
LanTalk.exe --headless --room 47800 --name "Meeting room" --peers 192.168.1.10:47800,192.168.1.11:47800
```

---

## 5. How it works

### 5.1 Transport: UDP peer-to-peer, no client/server split

Each packet is one type byte plus a payload. Audio packets carry a 9-byte header:
`type(1) + seq(4, LE) + sender InstanceId(4, LE)`. The sender ID is what lets a multi-homed peer be
recognised as one person instead of two.

### 5.2 Audio parameters

| Item | Value |
| --- | --- |
| Sample rate | 16000 Hz, mono, 16-bit |
| Frame | 20 ms (320 samples, 640 bytes) |
| Codec | PCM16 (256 kbps) or G.711 µ-law (128 kbps) |
| Jitter buffer | 60 ms target, 400 ms cap |

### 5.3 Jitter buffer — why it doesn't sound choppy

Frames are kept sorted by sequence number, so out-of-order arrival is tolerated. Packets later than
the buffer depth are dropped, and gaps are filled by repeating the previous frame (concealment)
rather than by silence.

### 5.4 Noise suppression engine A — lightweight (C#)

```
capture ─▶ high-pass (90 Hz) ─▶ window ─▶ 256-pt FFT
                                            │
                     minimum-statistics noise estimate ◀──┤ per bin
                     decision-directed a priori SNR   ◀──┤
                     Wiener gain + over-subtraction + floor ┘
                                            │
output ◀─ overlap-add ◀─ window ◀─ inverse FFT ◀─ gain
```

- 16 kHz native, 256-point FFT, 128-sample hop (50 % overlap), **16 ms** algorithmic delay
- Noise floor tracked by minimum statistics over a 0.5 s window — no voice-activity detector needed,
  which makes it steady on stationary noise (fans, air conditioning, mains hum)
- Four levels: off / low (−12 dB floor) / **mid (−18 dB, default)** / high (−24 dB)
- When set to *off* it is **bit-exact passthrough** — no delay, not a single sample changed

Measured on synthetic speech + white noise (segmental SNR): **+5.1 dB** at 0 dB input;
high-pass attenuates 40 Hz by −15.9 dB while touching 1 kHz by only −0.4 dB.

### 5.5 Noise suppression engine B — WebRTC NS ported to C#

A module-by-module C# port of **WebRTC's noise suppressor**, via the Rust rewrite
[sonora-ns] — 1,700 lines across 7 files, still pure C#, still zero native binaries.

It works nothing like engine A: it extracts three features (log-likelihood ratio, spectral
flatness, spectral difference), accumulates them into histograms, **re-derives the prior signal
model every 500 frames**, and estimates a per-bin *speech probability* to separate voice from noise.

Algorithmic delay is only **96 samples = 6 ms**.

| File | Upstream modules |
| --- | --- |
| `src/NsFoundation.cs` | `config`, `fast_math`, `suppression_params`, `signal_model`, `prior_signal_model`, `histograms` |
| `src/NsFft.cs` | `ns_fft` (256-pt real FFT, filterbank window, framing, overlap-add, SNR) |
| `src/NsEstimators.cs` | `quantile_noise_estimator`, `wiener_filter` |
| `src/NsNoiseEstimator.cs` | `noise_estimator` |
| `src/NsSignalModel.cs` | `signal_model_estimator`, `prior_signal_model_estimator` |
| `src/NsSpeechProbability.cs` | `speech_probability_estimator` |
| `src/NsSuppressor.cs` | `noise_suppressor` (pipeline) + public interface |

**Numeric conventions that had to match exactly** — get any of these wrong and nothing lines up
with the reference implementation:

1. The input is **int16-scaled float** (range ±32768), *not* normalised to ±1.
2. The FFT is the **unnormalised standard DFT**. Upstream wraps Ooura's fft4g; rather than copying
   Ooura, `src/NsFft.cs` implements an equivalent standard transform and uses the standard
   normalised inverse so the pair is exactly invertible. Upstream's `fft_ifft_roundtrip` test was
   ported to verify this.
3. The analysis window is `sin(πi/192)`, applied only to the ends of the 256-sample extended frame;
   the middle 65 samples are left unweighted.
4. `fast_log2f` uses an IEEE-754 bit trick that must be **bit-for-bit identical** to the C++
   (implemented here with a `[StructLayout(Explicit)]` union equivalent to `*(uint32_t*)&in`).
5. In `noise_estimator`, `gamma` **carries over between bins** inside the loop. That is how upstream
   writes it, and the port preserves the behaviour.

#### Upstream unit tests, ported and passing

**32 module tests pass**, exposed as the `ns_*` entries in the self-test:

| Self-test item | Contents | Result |
| --- | --- | --- |
| `ns_core` | `fast_log2f` identical to the C++ bit trick; FFT round-trip/DC/impulse; window table comparison; histogram binning | 8/8 |
| `ns_estimators` | Wiener filter at high/low SNR; quantile estimator convergence and tracking; startup scaling factor | 6/6 |
| `ns_noise_est` | initial state, `LOG_TABLE`, `prepare_analysis` copy, startup parametric blend, update direction under speech/no-speech probability | 7/7 |
| `ns_sig_model` | prior model initial values and weight normalisation, empty histogram → noise, flatness feature activation, closed-form LRT value, 500-frame refresh | 6/6 |
| `ns_speech_prob` | initial value, probability range, probability direction at high/low SNR, prior clamping | 5/5 |

#### Measured comparison

| Scenario | NS mid | NS high | Engine A mid | Engine A high |
| --- | --- | --- | --- | --- |
| Stationary white noise, 0 dB | +3.6 dB | +2.7 dB | **+4.5 dB** | +3.5 dB |
| Burst noise (15 ms transients) | **−2.0 dB** | **−3.3 dB** | −2.3 dB | −4.8 dB |
| Clean speech, no noise (passthrough) | **−0.50 dB** | — | — | — |

(40 s synthetic speech with true inter-word silence, sweeping formants and pitch jitter.)

**How to read this.** The two engines are **effectively tied** on my synthetic bench, with different
strengths: engine A leads by ~0.9 dB on stationary noise, NS wins at high levels on burst noise, and
NS has less than half the algorithmic delay. The −0.50 dB clean-speech passthrough shows NS is
essentially transparent when there is no noise to remove.

**A mistake worth recording.** My first synthetic test signal had a constant 0.18 noise floor and
**no true silence**. NS scored **−4.70 dB** on clean-speech passthrough, which looked exactly like a
porting bug. It was not: with no silence anywhere, the quantile noise estimator has no opportunity to
learn what the noise floor is, so it tracks the speech's own stationary part and suppresses it.
Switching to a realistic signal (true inter-word silence + sweeping formants + pitch jitter) moved
it to −0.50 dB. **Lesson: any evaluation signal for a spectral suppressor must contain genuine
silence, otherwise you are measuring whether the suppressor can distinguish a stationary signal from
noise — which is unsolvable by construction.**

Both engines are kept and are **switchable in the UI** (see §7); the CLI equivalent is
`--denoise-engine ns|classic`. Switching takes effect on the next frame, even mid-call.

### 5.6 Verifying it yourself: the one-click recording comparison

Synthetic signals only separate these engines by about 1 dB, so **the decision belongs to your ears
and your room**. The built-in comparison tool gives you something to judge:

- **UI**: the **Record & compare** button in the *Audio devices* group → records 10 seconds →
  processes with both engines → shows a metric summary and offers to open the output folder.
  **Speak normally and pause for a few seconds in the middle** — the tool needs silence to measure
  the noise floor.
- **CLI**:

```
LanTalk.exe --record-ab 10                            # record 10 s from the microphone
LanTalk.exe --record-ab-sim --record-ab 8             # synthetic input, does not use the mic
LanTalk.exe --record-ab 10 --record-ab-out D:\compare  # choose the output directory
```

Output goes to `Documents\LanTalk降噪对比\<timestamp>\`:

| File | Contents |
| --- | --- |
| `1-原始录音.wav` | the raw recording, as a reference |
| `2-降噪-WebRTC-NS.wav` | processed by NS |
| `3-降噪-轻量版.wav` | processed by engine A |
| `对比报告.txt` | the full metric report |

**How to read the metrics.** A real recording has no clean reference, so the usual SNR-improvement
metric is unavailable. Three reference-free quantities are used instead:

| Metric | Meaning | Want |
| --- | --- | --- |
| Noise floor | mean power of the quietest 10 % of frames | **more negative** after processing |
| Speech level | mean power of the loudest 20 % of frames | **drops as little as possible** |
| Estimated SNR | speech level − noise floor | **larger is better** |

The report gives both "how much the noise dropped" and "how much the speech dropped"; the difference
between them is the improvement. That makes it immediately visible whether an engine is suppressing
noise or suppressing the person along with it. If the recording has less than 6 dB of
speech-to-silence contrast, the report says so explicitly and asks you to re-record.

The metrics are computed from **per-frame energy**, so they are independent of each engine's fixed
delay — no alignment step, and no risk of a misalignment masquerading as a bad score.

> These three numbers only capture **energy**. Timbre artefacts such as musical noise or muffled
> speech are invisible to them, so the recommended workflow is: use the metrics to rule out anything
> obviously broken, then **listen to all three WAV files on headphones**.

### 5.7 One-to-one state machine

`idle → calling → (peer answers) → in call → hang up → idle`, handling simultaneous calls,
rejection, timeouts and busy states. There is no ringing timeout race: the caller retries the invite
until the callee answers or the call is cancelled.

---

## 6. Verified test results

Every number below was produced by running the build in this repository.

### 6.1 Single-process self-test: 18/18 pass

Run it with `LanTalk.exe --selftest`. It covers device enumeration, the codecs, the jitter buffer,
the mixer, both noise-suppression engines, the WebRTC port's 32 upstream module tests, the recording
comparison tool, the UDP protocol, the room protocol, and a real-sound-card capture/playback cycle.

### 6.2 Two-process one-to-one call

```
reached_call=True  lost=0  late=0  rtt_ms=0.00
sent_frames=359  decoded_frames=359  played_frames=356  concealed=0
pattern=114880/114880        <- every sample bit-exact
```

### 6.3 Three-process conference

```
members_max=2  audio_channels=2  lost=0  pattern=305920/305920
members_max=2  audio_channels=1  lost=0  pattern=311040/311040
members_max=2  audio_channels=0  lost=0  pattern=305920/305920
```

### 6.4 GUI smoke test: 13/13 pass

Drives the real window: launch, incoming ring, answer button, established call, end-to-end sample
verification, hang up, join a room, two members, per-member audio, mesh channel count, zero loss.

### 6.5 Installer

Install → verified file hashes match the source build → uninstall → install directory removed,
registry entry removed, no elevation prompt during uninstall.

---

## 7. UI guide

| Group | What it does |
| --- | --- |
| **Call mode** | One-to-one vs conference |
| **Local** | Bind address and port, plus *Apply and re-listen* |
| **Peer** / **Room** | Peer IP and port, *Test connection*, *Scan LAN* / room number and manual peers |
| **Call** | State, duration, *Call* / *Answer* / *Hang up* |
| **Audio devices** | Input/output device, *Refresh*, *Auto answer*, **noise-suppression engine dropdown (WebRTC NS / Lightweight)**, *Loopback test*, codec, **Record & compare** |
| **Call mode & volume** | Full duplex vs push-to-talk, playback volume 0–200 %, mute speaker/mic, **noise suppression level (off/low/mid/high)** |
| **Levels & stats** | Mic and peer level meters, packet counters, loss, jitter buffer depth, bitrate |
| **Log / Members** | Runtime log and the conference member list (mute or remove a member) |

---

## 8. Command-line arguments

```
--name 名字          本机显示名
--port 端口          监听端口（默认 47800）
--bind 地址          绑定地址（默认自动）
--peer IP             对端地址（可重复）
--peer-port 端口      对端端口
--auto-call           启动后自动呼叫
--auto-answer         自动接听
--codec pcm|ulaw      语音编码（默认 pcm）
--denoise 档位        降噪：off|low|mid|high（默认 mid）
--denoise-engine      降噪引擎：ns（默认）| classic
--record-ab 秒数      录音对比（默认 10 秒）
--record-ab-sim       录音对比但用合成语音
--record-ab-out 目录  录音对比输出目录
--room 房间号         进入会议室模式
--peers IP:端口,...   手工联系点
--local-port 端口     本机端口（会议室模式）
--headless            无界面运行
--duration 秒数       运行指定时长后退出
--null-audio          使用内置测试音源（不占声卡）
--selftest            运行自检
--log 文件            日志文件路径
--help                显示帮助
```

---

## 9. File layout

```
src/                  全部源码（构建脚本只编译这一层）
  Mm.cs               winmm.dll 互操作、设备枚举、采集/播放
  Net.cs              UDP 传输、包类型、对端管理
  AudioPath.cs        抖动缓冲、G.711、多路混音、编码切换
  Session.cs          一对一状态机
  Conference.cs       房间广播发现、成员管理、全网状发送
  Denoise.cs          降噪引擎 A（轻量版）
  Ns*.cs              降噪引擎 B（WebRTC NS 的 C# 移植，7 个文件）
  DenoiseCompare.cs   录音对比的指标与流程
  WavFile.cs          WAV 读写
  RecordAb.cs         录音与命令行入口
  MainForm.cs         图形界面
  SelfTest.cs         自检与无界面模式
  Setup/              安装程序
tools/
  make_icon.py        图标生成（去白底、圆角、多尺寸）
  ui-smoke-test.ps1   界面冒烟测试
assets/               图标源图、生成的 ico、界面截图
third_party/sonora-ns 上游参考源码（不参与编译，见第 12 节）
licenses/             第三方许可证原文
```

---

## 10. Known limitations

1. **Windows only.** The audio layer is `winmm.dll` P/Invoke.
2. **IPv4 only.**
3. **No NAT traversal.** Both machines must be on the same LAN, or on a VPN that provides direct
   reachability.
4. **No encryption.** Audio is plain UDP. Do not use it over untrusted networks.
5. **No acoustic echo cancellation.** Full-duplex mode needs headphones or a headset; without them
   use push-to-talk.
6. **Conference discovery relies on broadcast.** Blocked on many enterprise networks — use manual
   peers instead.
7. **Full mesh, ≤8 people.** Each machine encodes and sends N−1 streams. CPU and bandwidth grow
   linearly with the number of participants.
8. **Broadcast discovery could not be verified on a single machine** (you cannot bind the same port
   twice), so it has only been exercised through the manual-peer path and the room protocol.
9. **The noise suppressors are not deep-learning based.** Engine A is classical DSP; engine B is
   WebRTC's classical algorithm. Neither matches a modern neural suppressor on non-stationary noise.
10. **Short transients (keyboard, paper) are hard for both engines.** A 15 ms click gets smeared in
    time by any FFT-based suppressor and ends up bleeding into the speech — see the negative
    improvements in the comparison table above. This is a shared limitation of frequency-domain
    suppression, not a defect of one implementation.
11. **When bound to a specific NIC**, broadcast discovery goes out through that NIC only. Other
    interfaces need their own instance or the "listen on all NICs" option.

---

## 11. Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| The call connects but there is no sound | Windows Firewall is blocking inbound UDP. Approve the prompt, or re-run the installer and allow the firewall step. |
| *Scan LAN* finds nobody | Broadcast is blocked, or the switch has client isolation. Enter the peer IP manually. |
| Audio is choppy | The jitter buffer depth in the stats panel is too low, or the network is congested. Try G.711 µ-law to halve the bitrate. |
| The other side hears an echo | You are on speakers in full-duplex mode. Use headphones, or switch to push-to-talk. |
| The recording comparison says the data is unreliable | The recording has less than 6 dB between speech and silence. Record again, speaking normally with a few seconds of pause. |
| The console shows garbled Chinese | Windows PowerShell 5.1 reads BOM-less UTF-8 as GBK. Keep the build scripts UTF-8 **with BOM**. |

---

## 12. Third-party content and licensing

Attribution matters here, so it is listed in full and in one place:
**[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)**.

**Summary:**

| Content | Origin | Licence | Where |
| --- | --- | --- | --- |
| WebRTC noise suppressor, ported to C# | [sonora-ns] (Rust) → WebRTC `modules/audio_processing/ns/` | **BSD-3-Clause** | `src/Ns*.cs` |
| Upstream unit tests, ported | same | **BSD-3-Clause** | the `ns_*` self-test items |
| Upstream Rust sources, verbatim | [sonora-ns] | **BSD-3-Clause** | `third_party/sonora-ns/` |
| Ooura fft4g | — | — | **No code copied.** `src/NsFft.cs` is an original standard DFT; only the numeric convention was matched. |
| Minimum-statistics noise estimation | R. Martin, IEEE Trans. SAP, 2001 | published method | `src/Denoise.cs` |
| Decision-directed a priori SNR | Ephraim & Malah, IEEE Trans. ASSP, 1984 | published method | `src/Denoise.cs` |
| G.711 µ-law | ITU-T Recommendation G.711 | public standard | `src/AudioPath.cs` |
| `winmm.dll` API | Microsoft Windows SDK docs | — | `src/Mm.cs` |
| **App icon source image** | **provided by the repository owner** | **unverified** | `assets/icon-source.png` |

⚠️ **The app icon is the one asset whose provenance I cannot vouch for.** `assets/icon-source.png`
was supplied by the repository owner, not downloaded by the author of the code. If it came from
somewhere on the internet, **please confirm you have the right to use it — or replace it with your
own artwork — before publishing this repository.**

Everything else is original work: the transport, session state machine, conference protocol, jitter
buffer, mixer, both UI layers, the installer, the lightweight denoise engine, the recording
comparison tool, the icon generator, the build scripts and all documentation.

**Licensing:** LanTalk itself is **MIT** (see [LICENSE](LICENSE)). The portions ported from
sonora-ns / WebRTC remain subject to **BSD-3-Clause**; that licence permits redistribution under
other terms provided the copyright notice and disclaimer are retained, which
`THIRD-PARTY-NOTICES.md` and `licenses/sonora-ns-LICENSE.txt` do. When distributing a **binary**
(such as the installer in Releases), include `THIRD-PARTY-NOTICES.md` alongside it to satisfy
BSD-3-Clause's binary-redistribution clause.

---

[sonora-ns]: https://github.com/dignifiedquire/sonora
