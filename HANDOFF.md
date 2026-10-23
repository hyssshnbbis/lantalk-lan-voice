# LanTalk 交接文档

> 这份文档用于把项目和上下文交给另一个对话/Agent 继续。读它就等于读完了之前的对话。
> 最后更新：1.4.0 测试版阶段，**大头像成员卡已接进界面并验证通过**（提交见 §3.1）。

---

## 1. 这是什么项目

**LanTalk** —— 局域网语音通话软件。零第三方依赖、免服务器、免联网。

- 目标平台：Windows
- 语言/框架：**C# 5 + .NET Framework 4.0**（不是 .NET Core，不能用新语法）
- 编译方式：用 Windows 自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- **硬约束**：纯 IL、零原生 DLL、零 NuGet、AnyCPU、`asInvoker`
- 音频：`winmm.dll` 的 waveIn/waveOut，16 kHz 单声道 16-bit，20 ms 一帧（320 样本 / 640 字节）
- 传输：UDP P2P，会议室模式（**一对一功能已整体删除**）

**仓库**：https://github.com/hyssshnbbis/lantalk-lan-voice （public，MIT）

---

## 2. 立刻要知道的事

### 2.1 构建与测试命令

```powershell
.\build.ps1              # 编译正式版 -> dist\LanTalk.exe
.\build-preview.ps1      # 编译测试版 -> dist\preview\LanTalk-preview.exe（/define:PREVIEW）
.\build-update.ps1       # 打更新包 zip（只含 exe）
.\build-setup.ps1        # 打安装程序 exe
.\tools\ui-smoke-test.ps1  # 界面冒烟测试

.\dist\LanTalk.exe --selftest --log '.\dist\selftest.log'   # 自检
```

**所有 `.ps1` 必须存成 UTF-8 带 BOM**。Windows PowerShell 5.1 会把无 BOM 的文件当 GBK 读，
中文注释变乱码并连带破坏引号结构，脚本直接报废。`build-setup.ps1` 和 `build-update.ps1`
曾经因为这个坑挂过。

### 2.2 当前版本状态

| 版本 | 状态 | 说明 |
| --- | --- | --- |
| 1.3.2 | **已发布**（正式版） | 自检 26/26，冒烟 6/6 |
| 1.4.0-beta | **已发布**（pre-release） | 噪声处理 + 耳返 + 成员右键小窗；协议与 1.3.2 一致 |
| 1.4.0 | **已发布** | 成员看板 + 麦克风增益 + 音量条 + 耳返修复；自检 26/26，冒烟 6/6 |
| 1.5.0 | **已发布** | 聊天记录可删/可清空 + 电平改满幅线性 + 头像修复；自检 26/26，冒烟 6/6 |
| 1.5.1 | **已发布** | 单成员音量 >100% 修复 + 麦克风增益到 400% + SliderRow 公共控件；自检 26/26，冒烟 6/6 |

正式版 exe 版本号 **1.5.1.0**（`AssemblyInfo.cs`）；测试版同样 1.5.1.0，靠「· 测试版」后缀与独立数据目录区分。

### 2.3 不要做这些

- **不要终止用户的游戏进程**（`java` 跑着 Minecraft「DeceasedCraft」，PID 可能变）
- **不要改用户的 Xray 代理设置**（如需联网，只在单次请求上加 `-Proxy 'http://127.0.0.1:18080'`）
- **不要动 `hyssshnbbis.github.io`**（用户明确说过「算了别动」）
- 测试端口只用 **309xx 段**（自检房间 30900、会议 30950 成员 30951-30953、界面冒烟 30921/30922/30930）。**绝不要用 47800/47830**
- 发布前必须**用户拍板**（用户原话：「我拍板再上传发布」）

---

## 3. 当前最要紧的待办

### 3.1 大头像成员卡（**已完成**）

**控件**（提交 `49825b0`）：`src/Cards.cs` 的 `MemberCard`（176×232 圆角卡、148 大圆角头像、
说话绿圈、矢量图标）+ `MemberCardPanel`（自动换行 + 竖向滚动）。

**接线**（本轮完成）：

| 位置 | 内容 |
| --- | --- |
| `MemberView`（新） | 渲染层的数据结构：Key / 名字 / 副标题 / 是否自己 / 闭麦 / 扬声器静音 / 电平 / 音量 / 头像 |
| `MemberCardPanel.SetMembers()` | 按 Key **差异更新**，卡片复用不重建（每 500ms 刷一次，重建会丢悬停态和头像） |
| `MemberBoardCard`（新） | 主区顶部的看板卡片，标题 + 人数 + 卡片流；高度 `BoardH = 286` |
| `MainForm.RefreshMemberList()` | 组装 `List<MemberView>`（自己排第一，其余按名字稳定排序），**同时**刷新看板和侧栏名单 |
| `MainForm.OnMemberIconClicked()` | 自己卡：`mic`→切 `_chkMicMute`（走 OnMuteChanged 同步音频+顶栏）、`speaker`→`_chkSpkMute`、`leave`→离开会议；别人卡：`mute`→`SetMemberMuted`、`volume`/`menu`→成员小窗 |

**布局**（用户拍板选「主区顶部横条，日志留在底部」）：

```
侧栏 278 宽（间距统一 12，从上到下排满）：
  本机(12,160) 房间号(184,154) 会议成员(350,158) 电平统计(520,126) 运行日志(658,110,高度=剩余)
  版本号画在运行日志卡片的标题行右端（CardPanel.CornerText），不再单独占一行
主区 842 宽：成员看板(12,12,818x286) + 聊天(310,458 高，一直拉到底)
```

**侧栏的「会议成员」卡片保留**（用户明确要求）：它负责紧凑名单 + 房间状态块
（`_lblState`/`_lblPeerState` 仍指向它的 `Head`），主区看板负责看得清的大图，两边数据同源。

**人数口径改了**：以前 `MemberCount` 只算别人，看板上有了自己的卡就会「3 张卡写 2 人」。
现在侧栏状态、看板人数、RoomCard 状态统一用 `MemberCount + 1`，冒烟测试的断言也跟着改成
「成员 3 人」（含界面自己）。

**电平百分比**：`MeterCard.InPct/OutPct` 以前只有进度条在动、两个百分数一直挂 0%，
现在 `RefreshMeters()` 会同步写入。测耳返时可以看「对方声音」那一条：
耳返开着、麦克风有声音时它也应该跟着动。

**日志（用户要求）**：运行日志放侧栏，长行照常自动换行（`WordWrap` 保持默认开），
但**日志文案要短**——侧栏只有 206px 宽，长句会被折成三行还只看得到尾巴。
已经精简过一轮：`监听 ip:port`、`进房 30930，广播中…`、`我的标识 xxxxxxxx`、`联系点 ip:port`、
`成员离开：甲 · 主动退出`、`发图 12KB / 3 片`、`降噪 中 · NS`。新增日志请照这个长度写。

**版本号不要用 Label 贴在卡片上**：Label 的透明背景取的是**父容器**底色，
父容器是侧栏(25,25,25)而卡片是(36,36,36)，结果卡片标题行上出现一块深色矩形。
改成 `CardPanel.CornerText`，由卡片自己画在标题行右端。

**看板排布规则（用户明确要求）**：卡片**一律贴左边**摆，宽度放不下就自动换行，
行数超过看板高度就**竖向滚动**（`AutoScroll`）。滚轮在窗体上统一处理
（`MainForm.OnBoardMouseWheel`：指针落在看板里才滚），因为滚动消息只会送到有焦点的控件，
鼠标停在卡片上时不一定有人有焦点。每格 60px。

**踩过的坑（新）**：
- `Control` 直接 `BackColor = Color.Transparent` 会抛 `ArgumentException`（`SupportsTransparentBackColor`
  没开）。这里改成：卡片铺满父容器底色再自己画圆角，不做透明。
- 自绘控件的坐标是 96 DPI 逻辑值，但 `ApplyDpiScale` 会把控件尺寸放大。`MemberCard.OnPaint` 里用
  `g.ScaleTransform(Width / 176f, Width / 176f)` 整体缩放，鼠标坐标再除以同一个系数，这样
  1.25 DPI（本机）下既不变形也不错位。
- **`AutoScroll` 是靠移动子控件实现的**：每 500ms 的 `RefreshMemberList` → `Relayout` 一旦重新
  `SetBounds`，滚动位置就会被拉回顶部（实测滚轮滚到底、半秒后自己弹回去）。所以 `Relayout`
  里必须把 `AutoScrollPosition.Y` 加回每个卡片的 y 上。这个坑很隐蔽，改看板一定会碰到。
- 系统滚动条是浅色的，深色界面里像贴了条白胶布。`MemberCardPanel` 和 `DarkListView` 都在
  `OnHandleCreated` 里调 `DarkChrome.DarkScrollbars(this)`（`SetWindowTheme(..., "DarkMode_Explorer")`）。

**验证**：
- 编译零警告（原来 5 条 CS0649 没了），正式版 + 测试版都过；
- 自检 `pass=26 fail=0`；
- 界面冒烟 `pass=6 fail=0`（截图 `dist/ui-conference.png`，已同步到 `assets/ui-conference.png`）；
- 另做了一次**真实点击**验证（临时脚本，未入库）：按无障碍名找到自己的卡片窗口 → 发
  `WM_LBUTTONDOWN/UP` 点「话筒」→ 卡片红色像素 0→95（闭麦生效）→ 再点一次回到 0 →
  点「退出」→ 卡片消失、界面不再显示「成员 N 人」。
- **6 人房间**验证（临时脚本，未入库）：`成员 6 人` → 卡片贴左 4 张一行、第 2 行需要滚动，
  `GetScrollInfo` 读出可滚 299px；给窗体发 `WM_MOUSEWHEEL` 后看板区域有 6868 个采样点变化，
  第二行（队友4/队友5）滚出来了。

### 3.2 其他欠账

| 项 | 说明 |
| --- | --- |
| **README 严重过时** | 还在大篇幅讲「填对方 IP → 点呼叫」的一对一流程，而该功能已删除。需要系统性重写，不是打补丁 |
| 神经网络降噪 | 想做但**卡在网络**：`raw.githubusercontent.com` DNS 解析失败，拿不到 RNNoise 源码和权重。用户需自行下载 `rnn_data.c` / `rnn.c` / `denoise.c` 放进 `ref\` 目录 |
| 环境人声串扰 | **明确无解**。传统降噪做不到，需要神经网络模型（RNNoise 类） |
| `image_p2p` 已删 | 因为它依赖已删除的 `Session.SendImage`；自检从 27 项变 26 项 |
| 侧栏名单没有右键提示 | 侧栏「会议成员」和看板卡片都能右键弹成员小窗，但界面上没写提示，新用户不一定知道 |

**用户明确不要的功能**（参考截图上有，本项目没有）：表情包发送、共享设备音频一起听歌、
屏幕共享、邀请/分享、开黑时长。

---

## 4. 代码结构

| 文件 | 职责 |
| --- | --- |
| `src/Program.cs` | 入口、命令行参数解析（`Args` 类）、帮助文本 |
| `src/MainForm.cs` | 主窗口编排：实例化板块、摆放、连事件、读写属性（~1300 行） |
| `src/MainForm.Chat.cs` / `.Settings.cs` | 上面那个类的分部类 |
| `src/Theme.cs` | **自绘控件库**：配色、`RoundRect`、字体、`CardPanel`、`FlatButton`、`MeterBar`、`DarkSlider`、`DarkInput`、`DarkSelect`、`OptionRow`、`RoundedSurface`、`DarkListView`、`DarkChrome`、`DarkMenuColors`、**`Switch`**、**`StatusBlock`** |
| `src/Cards.cs` | **板块**：`CardBase` 抽象基类 + 各卡片（`LocalCard`/`RoomCard`/`MembersCard`/`MeterCard`/`DeviceCard`/`ModeCard`/`LogCard`/`ChatCard`/`NoiseCard`）+ `MemberListView`（侧栏紧凑名单）+ **`MemberBoardCard`**（主区看板）+ **`MemberCard`** / **`MemberCardPanel`** / `MemberView` |
| `src/AudioPath.cs` | 音频链路：采集→处理→编码→发送；接收→抖动缓冲→混音→播放。内含 `JitterBuffer`/`Mixer`/`RemoteChannel`/`NullSink`/`ToneSource` |
| `src/Conference.cs` | 会议室：成员发现、公告、音频路由、聊天/图片/头像收发 |
| `src/Net.cs` | UDP 收发、`Pkt` 枚举、环回、广播发现 |
| `src/Denoise.cs` / `src/Ns*.cs` | 两套降噪引擎（轻量版 / WebRTC NS 移植版） |
| `src/HumFix.cs` | **工频陷波 `HumNotch` + 键盘瞬态抑制 `ClickGuard`** |
| `src/Chat.cs` / `ChatUi.cs` | 聊天协议/持久化 + 聊天流渲染。**聊天区右键**：消息上 →「删除这条」（同时删 `chat.log` 里那一行，`ChatHistory.Delete` 按「时间\|自己\|名字\|正文」重建整行后删第一处匹配）；任意位置 →「清空聊天记录」（确认后删 `chat.log` 与 `.bak`）。`ChatFeed` 抛 `MsgDeleted` / `ClearRequested`，`MainForm.Chat.cs` 负责落盘与确认框 |
| `src/ImageWire.cs` | 图片分片协议（9 字节头，900 字节/片，3MB 上限，8 秒超时） |
| `src/Profile.cs` | 用户资料（名字 / 头像 / 持久 ClientId / 噪声开关），存 `%LOCALAPPDATA%\LanTalk\profile.txt` |
| `src/PeerStore.cs` | 「连接过的人」缓存（头像按 ClientId 落盘，hash 命中就不重传） |
| `src/AppPaths.cs` | 正式版/测试版数据目录分流（`LanTalk` vs `LanTalkPreview`） |
| `src/SelfUpdate.cs` | 从 update.zip 就地替换 exe（手写 ZIP 解析，.NET 4.0 没有 `ZipFile`） |
| `src/SelfTest.cs` | 自检 26 项 + `Headless.RunConference`（无界面会议室模式） |
| `src/Setup/*.cs` | 安装程序 |
| `tools/ui-smoke-test.ps1` | 界面冒烟测试（6 个用例，全是会议室路径） |

### 4.1 界面约定（很重要，别破坏）

**三层结构**（写在 README §15）：
- `Theme.cs` = 自绘控件库
- `Cards.cs` = 板块，每个板块独立类，自己持有并布局控件，对外只暴露字段和事件
- `MainForm.cs` = 只负责实例化、摆放、连事件，**不创建控件、不写坐标**

**盒子模型**：卡片用 `PadH`（左右内边距）、`ContentTop`（内容起点）、`RowY(n)`，
不要散落魔法数。尺寸都是 96 DPI 逻辑值，最后由 `ApplyDpiScale()` 统一放大
（这台机器 DPI 是 1.25，实际 1400×1050）。

**公共控件要复用**（用户强调过「先写控件，再按需调用」）：
- `CardBase.SwitchRow(x, y, title, desc, titleW)` → 一行「开关 + 标题 + 说明」
- `CardBase.StatusLine(x, y, w)` → 统一的两行状态块（返回 `StatusBlock`）
- `Theme.SliderRow(title, min, max, value, width)` → **一行「标题 + 滑块 + 右侧百分比」**，
  `WarnAbove` 设多少，超过就自动把数字标琥珀色（放音音量 150、麦克风增益 200）。
  **宽度必须在构造时传进去**：子控件位置只在构造里算一次，之后交给 `ApplyDpiScale` 等比放大；
  如果放到 `OnResize` 里按 `Width` 重算，DPI 放大后 `Width` 已是设备像素，再减逻辑宽度会把
  数值标签挤出面板（踩过，标签直接看不见）。
- **大头卡片的图标点法**：自己卡 = 闭麦/扬声器静音/退出（点一下直接生效）；
  别人卡 = 音量图标弹出**一条音量条**（`MainForm.ShowVolumePopup`，212x65，只有滑块，
  边拉边生效，关掉时刷新卡片），静音图标点一下直接静音/解除；**右键**才弹成员小窗。
- `Theme.DarkMenu.Apply(menu)` → **所有 ContextMenuStrip 都走它**（右键菜单、成员小窗、下拉列表）：
  背景＝卡片色、圆角 8、1px 深色描边、悬停＝列表选中那种绿灰、分隔线细且两侧留白。
  别再造 `new ToolStripProfessionalRenderer(new DarkMenuColors())` 了——那样分隔线和边框还是系统浅色。
- `Theme.Switch` → 拨动开关（有 0.15 秒过渡动画）

**头像的规矩**：有真头像就**只画头像**，首字色块是"没有头像"时的兜底——两者不能叠着画。
`MemberListView.OnDrawSubItem` 曾经把首字无条件画在头像上（截图上能看到方块压在照片上），已修。
注意聊天流（`ChatFeed.DrawAvatar`）目前**只用首字**、不认真头像——想统一的话要按成员 Key 建映射，
别按名字匹配（重名会串）。

**冒烟测试怎么找控件**：按 Win32 class 名含 `BUTTON` + 可见文字匹配。所以：
- 自定义按钮必须继承 `System.Windows.Forms.Button`
- 图标按钮要设 `Text` 作为无障碍名（「话筒」「扬声器」「挂断」「最小化」「关闭」）

---

## 5. 关键技术细节

### 5.1 协议

`Pkt` 枚举（`src/Net.cs`）：

```
Audio=1, Ring=2, Ringing=3, Accept=4, Bye=5, Ping=6, Pong=7, Busy=8,
Hello=9, HelloReply=10, TalkState=11,
RoomHello=12, RoomHere=13, RoomBye=14, Chat=15, ImageChunk=16,
Avatar=17, AvatarWant=18
```

**房间公告（1.3.0 起，与旧版不兼容）**：
```
[端口 2][实例ID 4][ClientId 16][头像hash 4][名字 utf8...]
                                    ↑ 头部固定 26 字节
```
定长字段必须放在名字**前面**，因为名字是变长的、占满剩余字节。

**图片/头像分片**（9 字节头）：`[消息号 4][分片序号 2][分片总数 2][flags 1]`，
每片 900 字节，乱序容忍、重复片忽略、8 秒超时丢弃、单张 3MB 上限。

**头像**：载荷是 `[实例ID 4][JPEG]`。按需拉取——对方发现手里没有这个 hash 的头像时
才发 `Pkt.AvatarWant`（空载荷），发送方单播回应。`PeerStore` 按 ClientId 落盘头像。

### 5.2 噪声处理（1.4.0 新增）

**`HumNotch`**（`src/HumFix.cs`）：用 Goertzel 统计 50/60 Hz 能量占比，
判定电网频率后对 1–8 次谐波挂二阶陷波（Q=24）。只在检出时启用，否则完全旁路。

**`ClickGuard`**：快/慢双时间常数能量包络，比值超 6 倍判定冲击，快速压到 0.25 再慢放。

**接入位置**：`AudioPath.OnCaptured()` 里，降噪**之前**（`ApplyHumFix`）。

**电平表的尺子**：满幅线性——`100%` 就是满刻度 32768（`AudioPath.Peak` 与 `Mixer.Peak` 同一个公式）。
以前是 `sqrt` 感知压缩，读数好看但和增益不成比例（麦克风增益 100→150%，读数只从 48% 涨到 67%，
看着像没效果）。改成线性后 50%→15%、100%→30%、150%→45%，成整倍数。
**代价**：轻声的信号在读数上更小了，所以「正在说话」的阈值从 `Level >= 8` 降到 3、
卡片绿圈从 `> 4` 降到 `> 1`（都是 `MemberCard` / 侧栏名单里的判定）。

**音量与增益（三条独立的路）**：
- `放音音量` 0–200%（`AudioPath.VolumePercent`，混音后按比例缩放，>100 会削顶）
- `单成员音量` 0–200%（`Conference.SetMemberVolume` + `RemoteChannel.Volume` + `AudioPath.SetChannelVolume`，
  **三处都要夹 0–200**；混音时按该路缩放）。2026-10-23 修过一次：`SetChannelVolume` 还夹在 100，
  导致界面拉过 100% 日志照写、声音不动——现在自检 `mixer` 里钉了「200% 必须正好 2468 = 1234×2」。
  —— 卡片上点音量图标弹出的那条音量条就是这个，原来上限写死 100，已对齐到 200
- `麦克风增益` 0–**400%**（`AudioPath.MicGainPercent` + `Profile.MicGain`，默认 100）——上限给到 +12 dB 是因为入耳式/线控麦克风普遍很小声，150% 只够 +3.5 dB，UI 在设置页「通话模式与音量」卡片里，
  存在 `Profile.MicGain`（profile.txt 的 `micgain=`）。处理顺序：工频/键盘 → 降噪 → **增益** → 电平表 → 编码，
  耳返取的是增益之后的最终信号，所以听到的就是对方听到的。

**开关**：`Profile.HumFilter` / `Profile.ClickFilter` 默认 `AppPaths.IsPreview`（测试版开、正式版关）；
**`Profile.Sidetone`（耳返）默认是关的，测试版也关**（外放会啸叫，只让用户在设置页自己开）。
交接文档早先写成"三个都跟 IsPreview"是错的，以代码为准。

**耳返的两个坑（已修，别踩回去）**：
1. 耳返曾经被 `ApplyHumFix()` 开头的 `if (!HumFilter && !ClickFilter) return;` 挡掉，
   两个噪声开关都关时"耳返开着却没声"（正式版默认就是两个都关，等于这功能从没生效）。
   **现在耳返统一在 `OnCaptured` 里、降噪与增益之后推给混音器**（`PushSidetone()`），
   `ApplyHumFix` 只管陷波和键盘抑制，两者彻底解耦。
2. `_clickGuard.Process(_fxBuf, n)` 被连着写了两遍，键盘抑制其实是按双倍强度跑的。
   自检 `hum_fix` 直接调 `ClickGuard`，所以测试一直是 12dB、看不出这个重复。
   修完真机上的键盘抑制会比 1.4.0-beta 轻一些，这是对的（以前过头了）。

**耳返（sidetone）**：`Mixer.PushMonitor()` 把处理后的麦克风样本排进混音队列，
和对方声音叠加。音量压到 0.7。

**实测效果**（自检 `hum_fix`）：
```
50 Hz 及谐波压低 17.2 dB，1 kHz 语音仅变化 -0.01 dB
键盘冲击峰值压低 12.0 dB
```

### 5.3 自检的坑（已解决，别踩回去）

`codec_path_pattern` 曾经间歇失败，每次报同一组数：
```
实际 1840,1877,1914   期望 -10000,-9963,-9926
(1840 - (-10000)) / 37 = 320 样本 = 正好一帧
```

**根因**：`AudioPath.Start(true)` 会先消费一帧做预热，那帧没进入 `seq` 编号流，
所以音源游标比帧编号领先整帧。原测试用绝对索引做期望，假设不成立。

**修复**：首帧用前 8 个样本在音源里搜出真实起点 `n0`，之后按
`SampleAt(n0 + seq * SamplesPerFrame + i)` 比对。依然严格到 1 LSB。
相位偏移量会打进日志。

**注意**：`TestCodecPathPattern` 里必须显式 `ap.HumFilter = false; ap.ClickFilter = false;`
——它测的是编解码往返，不能让 DSP 改动信号。同理，界面端到端的逐样本校验用例
（`gui_audio_end_to_end` 已随一对一删除）也依赖这一点。

### 5.4 曾经踩过的坑

| 坑 | 教训 |
| --- | --- |
| `Control.BackColor = Color.Transparent` 抛 `ArgumentException` | 必须开 `ControlStyles.SupportsTransparentBackColor`，否则异常会让 `BuildUi()` 后半段整段跳过，界面少一半功能却照常运行 |
| `PeerStore.Touch` 每次公告都写盘 | 等于在音频线程做磁盘 I/O，把音频帧挤掉。改成攒批写（5 秒一次），`Conference.Dispose` 时 `Flush` |
| 用 `IndexOf`/`Substring` 盲切文件 | 切掉过构造函数整段。**改成：先 `HashSet` 记录待删行号，再一次性重建文件** |
| 按"包含某名字"删行 | 把方法体也删了，只剩孤立大括号。**改成按签名定位 + 花括号配对** |
| PowerShell 函数 `Write-Output` 混进返回值 | 把整个源文件的换行冲掉。**不要在"返回数据"的函数里 `Write-Output`** |
| 一次性替换整块代码 | 行尾差异（CRLF vs LF）导致静默失效。**逐行替换更可靠** |
| 安装包覆盖安装装到默认目录 | 旧版在自定义目录时会装出两份副本。`InstallEngine.LocateExisting()` 三级定位：注册表 → 常见位置 → 快捷方式反查 |

---

## 6. 最近的重大重构（已完成）

用户决定：**删除一对一通话**。理由——「两个人进同一个房间号就是一对一」。

分五阶段完成，每阶段单独提交：

| 提交 | 内容 |
| --- | --- |
| `fe1369b` | 删除「连接模式」卡片，模式恒为会议室 |
| `1ff087f` | 删除状态卡，房间状态并入「会议成员」 |
| `d3286b5` | **删除 `Session.cs`**、`Headless.Run`、`TestImageP2P`、30 余处 `_session` 引用 |
| `2a0e7ee` | 删除「对方」卡片（`PeerCard`）；`StatusBlock` 提升为公共控件 |
| `fa76c2e` | 删除 `--peer`/`--peer-port`/`--auto-call`/`--auto-answer` 参数与「自动接听」复选框 |

**成果**：exe 385 KB → 369.5 KB，自检 27 → 26 项（删了 `image_p2p`），
冒烟 12 → 6 项（删了 5 个一对一用例）。

改造而非删除的部分：
- `_session.Audio.*` → `_audio.*`
- `_session.StateSeconds` → `Conference.JoinedSeconds`（新增 API）
- `_session.SetTalking()` → `_audio.PttTalking`
- 顶栏挂断按钮 → 离开会议

---

## 7. 发布流程

```powershell
# 1. 编译
.\build.ps1
.\build-update.ps1 -Version 1.4.0
.\build-setup.ps1

# 2. 推送（token 在桌面的 Github令牌.txt，是 ghp_ 开头的 classic token）
$tok = (Get-Content 'C:\Users\34746\Desktop\Github令牌.txt' -Raw).Trim()
$url = "https://x-access-token:$tok@github.com/hyssshnbbis/lantalk-lan-voice.git"
git -c credential.helper= push $url main

# 3. 建 tag 再建 release（GitHub API 不接受 short SHA 作为 target_commitish）
git tag -f v1.4.0 <sha>; git -c credential.helper= push $url refs/tags/v1.4.0
# 然后 POST /repos/.../releases，只给 tag_name

# 4. 上传资源时需要 UTF8 编码 JSON
[System.Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
```

**包体分工**（用户明确纠正过，别搞反）：
- **更新包** = `LanTalk-x.y.z-update.zip`（只含 exe，程序内拖入就地替换）
- **完整包** = `LanTalk-Setup-x.y.z.exe`（安装程序）

推送偶尔会撞上 `schannel: failed to receive handshake`，**重试 2-3 次即可**。

---

## 8. 用户偏好

- **话少、直接执行**。不要反复问，能判断的自己判断；确实不懂的**一次问清**
- 讨厌偷懒：控件要复用、不要硬编码坐标、不要绕过架构约定
- 版本策略：新功能 → `1.x.0`；修补 → `1.0.x`（不过 1.3.1 的对端缓存按用户要求算小更新）
- 说「不要问我，直接做」时要真的直接做
- 中文交流，注释也用中文
- 报告要**诚实**：做了一半就说做了一半，验证不了就说验证不了

---

## 9. 快速自检清单

接手后先跑这几条，确认环境正常：

```powershell
.\build.ps1                                                      # 应输出 体积：386.5 KB、零警告
.\dist\LanTalk.exe --selftest --log '.\dist\selftest.log'        # 应 pass=26 fail=0
.\tools\ui-smoke-test.ps1                                        # 应 pass=6 fail=0
```

注意：`LanTalk.exe` 是 GUI 子系统程序，**PowerShell 直接调用不会等待**。
要拿退出码和完整日志就用 `Start-Process -Wait -PassThru`，否则只能看到日志的后半截。

如果 `codec_path_pattern` 失败，看日志里的「相位偏移」——正常情况下应该是 0，
标定逻辑会自动处理整帧偏移。真的挂了再看是不是别的测试污染了音频设备。
