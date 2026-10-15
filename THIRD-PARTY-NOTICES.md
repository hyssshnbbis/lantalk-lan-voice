# 第三方内容声明 / Third-Party Notices

本文件逐项列出 LanTalk 中**所有来自他人的内容**：移植的代码、附带的参考源码、参考的算法与标准，
以及来源不明的素材。目的是让人能一眼看清"哪些是我写的、哪些是别人的"。

This file lists **every piece of third-party content** in LanTalk: ported code, bundled reference
sources, referenced algorithms and standards, and assets of unclear provenance.

> 如果你在本仓库里发现任何应当署名而未署名的内容，请开 issue，我会立刻补上或移除。
> If you find anything here that should be attributed but isn't, please open an issue.

---

## 一、移植的代码（最重要的一项）

### 1.1 sonora-ns

| 项目 | 内容 |
| --- | --- |
| 名称 | sonora-ns（[Sonora] 音频处理库的一部分） |
| 语言 | Rust |
| 版本 | 0.2.0 |
| 作者 | dignifiedquire 及贡献者 |
| 仓库 | https://github.com/dignifiedquire/sonora |
| 许可证 | **BSD-3-Clause** |
| 用途 | 本项目的 WebRTC 降噪引擎（`src/Ns*.cs`）逐模块移植自它 |

### 1.2 WebRTC 音频处理（原始算法来源）

| 项目 | 内容 |
| --- | --- |
| 名称 | WebRTC — `webrtc/modules/audio_processing/ns/` |
| 作者 | The WebRTC Project Authors |
| 仓库 | https://webrtc.googlesource.com/src |
| 许可证 | **BSD-3-Clause** |
| 用途 | sonora-ns 本身是它的重写；因此本项目的降噪算法最终源自这里 |

### 1.3 本仓库中移植自上述两个项目的文件

以下文件**不是我的原创**，是 sonora-ns / WebRTC NS 的 C# 移植（衍生作品），
受 BSD-3-Clause 约束，许可证原文见 [`licenses/sonora-ns-LICENSE.txt`](licenses/sonora-ns-LICENSE.txt)。

| 本仓库文件 | 对应上游模块 |
| --- | --- |
| `src/NsFoundation.cs` | `config.rs`、`fast_math.rs`、`histograms.rs`、`signal_model.rs`、`prior_signal_model.rs`、`suppression_params.rs` |
| `src/NsFft.cs` | `ns_fft.rs`（接口与数值约定）；**FFT 实现本身是我自己写的标准 DFT**，见下方 §3.1 |
| `src/NsEstimators.cs` | `quantile_noise_estimator.rs`、`wiener_filter.rs` |
| `src/NsNoiseEstimator.cs` | `noise_estimator.rs` |
| `src/NsSignalModel.cs` | `signal_model_estimator.rs`、`prior_signal_model_estimator.rs` |
| `src/NsSpeechProbability.cs` | `speech_probability_estimator.rs` |
| `src/NsSuppressor.cs` | `noise_suppressor.rs` |

**上游的单元测试同样被移植**（不是我的测试设计），出现在：

- `src/NsFft.cs` 的 `NsFft.SelfTest()`
- `src/NsEstimators.cs` 的 `NsEstimatorTests.Run()`
- `src/NsNoiseEstimator.cs` 的 `NsNoiseEstimatorTests.Run()`
- `src/NsSignalModel.cs` 的 `NsSignalModelTests.Run()`
- `src/NsSpeechProbability.cs` 的 `NsSpeechProbabilityTests.Run()`
- `src/SelfTest.cs` 中 `ns_core` / `ns_estimators` / `ns_noise_est` / `ns_sig_model` / `ns_speech_prob` 这几项

这些文件头部都带有出处标注，例如：

```csharp
// NsFft.cs —— WebRTC 降噪移植（第 2 层：256 点实数 FFT、滤波器组窗、帧操作）
// 移植自 sonora-ns / WebRTC `ns/ns_fft.cc`。BSD-3-Clause。
```

---

## 二、随仓库附带的第三方源码（原样保留，未修改）

### 2.1 `third_party/sonora-ns/`

上游 sonora-ns 的 Rust 源码，**原样复制、未做任何修改**，仅作为移植依据保留，
让人可以逐行核对移植是否忠实。**它不参与本项目的编译**。

| 文件 | 说明 |
| --- | --- |
| `LICENSE` | 上游 BSD-3-Clause 原文（版权方为 The WebRTC Project Authors、Arun Raghavan and contributors、dignifiedquire 三方） |
| `README.md` | 上游说明 |
| `Cargo.toml` | 上游 Rust 包清单 |
| `src/*.rs` | 上游 14 个模块源码，共 17 个文件 |

---

## 三、参考了但**未复制代码**的内容

### 3.1 Ooura fft4g

sonora-ns 的 FFT 走的是 [Ooura 的 fft4g](https://www.kurims.kyoto-u.ac.jp/~ooura/fft.html)（经其 `sonora-fft` 包封装）。

**本项目没有复制 Ooura 的任何代码**：`src/NsFft.cs` 里是我自己写的标准基 2 复数 FFT，
只是让它的**数值约定**（未归一化 DFT、`real[k] = Σx[n]cos(2πkn/N)`）与 Ooura `rdft` 的输出一致，
这样下游各个模块的绝对阈值才和参考实现对得上。这一点在上游的 `fft_ifft_roundtrip` 测试里有验证。

### 3.2 学术方法与公开标准

以下是**算法思想或公开标准**，不是他人代码，但既然本项目实现了它们，一并列出：

| 方法 | 出处 | 用在哪 |
| --- | --- | --- |
| 最小统计噪声功率谱估计 | R. Martin, *Noise Power Spectral Density Estimation Based on Optimal Smoothing and Minimum Statistics*, IEEE Trans. Speech and Audio Processing, 2001 | `src/Denoise.cs`（轻量版引擎的噪声估计） |
| 判决引导先验信噪比 | Y. Ephraim, D. Malah, *Speech Enhancement Using a Minimum Mean-Square Error Short-Time Spectral Amplitude Estimator*, IEEE Trans. ASSP, 1984 | `src/Denoise.cs` 的维纳增益 |
| 维纳滤波降噪 | 经典信号处理方法 | `src/Denoise.cs`、`src/NsWienerFilter`（`src/NsEstimators.cs`） |
| G.711 µ-law 压扩 | ITU-T Recommendation G.711 | `src/AudioPath.cs` 的 µ-law 编解码（码表由本项目的 `tools/make_icon.py` 之外的代码按标准公式生成） |
| Windows 多媒体 API | Microsoft Win32 / Windows SDK 文档（`winmm.dll` 的 `waveIn*` / `waveOut*`） | `src/Mm.cs` 的 P/Invoke 声明 |

---

## 四、来源需要你确认的素材

### 4.1 应用图标

| 文件 | 情况 |
| --- | --- |
| `assets/icon-source.png` | **由本仓库使用者提供的图片**，非我从网络下载 |
| `assets/LanTalk.ico`、`assets/LanTalk.png` | 由 `tools/make_icon.py` 从上面那张图生成（去白底、圆角、多尺寸） |

**我无法确认这张原图的权利归属。** 如果它来自网络或他人作品，
**在公开发布前请务必确认你有权使用，或替换成你自己创作/授权明确的图**。
这是本仓库唯一一处我无法替你担保来源的素材。

---

## 五、明确属于原创的部分

除上述内容外，本仓库其余部分均为原创：

- 全部通话功能：`src/Net.cs`（UDP 传输与对端管理）、`src/Session.cs`（一对一状态机）、
  `src/Conference.cs`（房间广播发现与全网状会议）、`src/AudioPath.cs`（抖动缓冲、多路混音、编码切换）
- 全部界面：`src/MainForm.cs`、`src/Setup/SetupUi.cs`
- 轻量版降噪引擎：`src/Denoise.cs`（结构参考了 WebRTC NS 的分层方式，但算法实现、
  参数标定、延迟推导与测试都是本项目自己的）
- 录音对比工具：`src/DenoiseCompare.cs`、`src/WavFile.cs`、`src/RecordAb.cs`
- 安装程序：`src/Setup/InstallEngine.cs`、`src/Setup/SetupProgram.cs`
- 图标生成器：`tools/make_icon.py`
- 界面冒烟测试：`tools/ui-smoke-test.ps1`
- 构建脚本、README 与全部文档

**本项目没有任何第三方二进制依赖**：不使用 NuGet 包，不链接任何原生 DLL，
只用 .NET Framework 自带的 BCL 与 Windows 系统自带的 `winmm.dll`。

---

## 六、BSD-3-Clause 许可证原文

以下为 sonora-ns 的许可证全文（`third_party/sonora-ns/LICENSE` 的副本，
也见 [`licenses/sonora-ns-LICENSE.txt`](licenses/sonora-ns-LICENSE.txt)）：

```
Copyright (c) 2011, The WebRTC Project Authors. All rights reserved.
Copyright (c) 2016, Arun Raghavan and contributors. All rights reserved.
Copyright (c) 2026, dignifiedquire. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google nor the names of its contributors may
    be used to endorse or promote products derived from this software
    without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## 七、许可证兼容性说明

- 本项目自身以 **MIT** 发布（见 [`LICENSE`](LICENSE)）。
- 其中移植自 sonora-ns / WebRTC NS 的部分，**同时受 BSD-3-Clause 约束**；
  BSD-3-Clause 是宽松许可证，允许以其它许可证再发布，但**必须保留上述版权声明与免责声明**。
  本文件与 `licenses/sonora-ns-LICENSE.txt` 即为此目的的保留。
- 分发**二进制**（例如 Release 里的 `LanTalk-Setup-*.exe`）时，
  请一并附带本文件，以满足 BSD-3-Clause 第二条对二进制再分发的署名要求。

[Sonora]: https://github.com/dignifiedquire/sonora
