# v0.1.3-alpha

Original-engine tooling for ML/RL workflows in Slay the Spire 2.

## Changes

- Mac recorder revision 0.2.4 records synchronous cross-act notifications with their Proceed parent. Nested player selections remain separate inputs.
- Linux import checks callback attribution, preserves source events and conversion reasons, and supports an explicitly selected, hash-bound legacy adapter.
- Replay compares the final available actions as well as the final state, using the recorded successor and terminal receipt.
- The Mac export report separates recording completeness, action-mapping requirements, and replay results.

Install the new recorder DLL and manifest with the game closed, and use the exporter and Linux tools from this release. See the [Mac guide](mac/README.md) and [replay guide](docs/REPLAY.md).

## Downloads and setup

- [Mac recorder ZIP](https://github.com/Jtshieh/STS2-Tools/releases/download/v0.1.3-alpha/Sts2Recorder-macos-arm64-v0.1.3-alpha.zip)
- [Release source ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/tags/v0.1.3-alpha.zip)
- [English setup overview](README.md) / [简体中文使用入口](README.zh-CN.md)

Compatible game: STS2 v0.111.0 / 41cef1ea / Steam build 24724944, Mac arm64 and Linux x86_64, single-player Silent A0.

Recorder ZIP SHA-256: `cce4d7070cd721e5b153560a9364de27630667d3ae789b65f45a0dfc3fe3ac03`.

## Validation scope

25 contract tests passed. The retained cross-act recording still produces the same 157 executable inputs, including 38 nested inputs; comparison with the retained Linux run matches those decisions and the final state/action list. Its 77 decision-timing differences remain reported separately.

Recorder 0.2.4 builds against the Mac arm64 game, loads in an isolated native game, and reaches `RECORDER READY` without recorder errors. This release has no new cross-act GUI gameplay test of the callback metadata or full-run/SL validation.

## License

[MIT](LICENSE) and [third-party attribution](NOTICE.md).
