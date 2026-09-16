# v0.1.0-alpha

Original-engine tooling for ML/RL workflows in Slay the Spire 2.

## Included in this release

- Linux structured observations and actions for combat, map, event, reward, shop, rest-site, and nested-selection decisions.
- Manual, policy-worker, and recorded-input control through `sts2_play.py`.
- Action lifecycle logs, Mac semantic trajectory import, and decision-state replay comparison.
- A small combat imitation-learning example with checkpoint updates and resume.
- A precompiled Mac arm64 recorder mod, plus source-build and separate-workspace tools. The mod's internal recorder revision is 0.2.3.
- Workspace storage management for completed Linux sessions.

## Downloads and setup

- [Mac mod ZIP](https://github.com/Jtshieh/STS2-Tools/releases/download/v0.1.0-alpha/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip)
- [Release source ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/tags/v0.1.0-alpha.zip)
- [English setup overview](README.md) / [简体中文使用入口](README.zh-CN.md)

Game compatibility: STS2 v0.111.0 / 41cef1ea / Steam build 24724944, Mac arm64 and Linux x86_64, single-player Silent A0. Platform guides list build dependencies, profile inputs, and commands. The [replay guide](docs/REPLAY.md) describes starting-state requirements and comparison results.

## License

[MIT](LICENSE) and [third-party attribution](NOTICE.md).
