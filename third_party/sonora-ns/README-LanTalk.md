# 这里的代码不是本项目的

`sonora-ns/` 是上游 **sonora-ns** 的 Rust 源码，**原样复制、未做任何修改**：
https://github.com/dignifiedquire/sonora  （BSD-3-Clause，版权方见 `sonora-ns/LICENSE`）

放在这里的唯一目的，是让 `src/Ns*.cs`（本项目对它的 C# 移植）**可以被逐行核对**。

- **它不参与本项目的编译**：LanTalk 只编译 `src/*.cs`，构建脚本不会碰这个目录。
- 本项目的 WebRTC 降噪引擎移植自它；完整的署名与许可证说明见仓库根目录的
  [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md)。
- 若要更新上游版本，请从上面的仓库重新拉取，不要就地修改——保持它和上游一致，
  核对移植时才有意义。
