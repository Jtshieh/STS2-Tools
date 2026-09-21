# v0.1.2-alpha

Original-engine tooling for ML/RL workflows in Slay the Spire 2.

## Changes

The Mac exporter now reports recording completeness, Linux action-mapping requirements, and replay validation separately. A structurally complete recording can be ready for processing while replay validation remains `not_run`. The report schema is `sts2-gui-conversion-v2`.

- Card-state observations use the current Linux comparison entry point.
- Action IDs requiring adapter review appear in `requiredActionBindings`.
- Unknown records, recording errors, incomplete decisions, and interrupted continuity remain explicit in `issues`.

Update `mac/export_trace.py` from this release and export your retained session again. The recorder DLL is unchanged; an installed recorder does not need reinstalling. Linux runtime code remains at v0.1.0-alpha, and the Mac recorder package remains at recorder revision 0.2.3. See the [Mac export guide](mac/README.md) and [replay guide](docs/REPLAY.md).

## Downloads and setup

- [Mac recorder ZIP](https://github.com/Jtshieh/STS2-Tools/releases/download/v0.1.2-alpha/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip)
- [Release source ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/tags/v0.1.2-alpha.zip)
- [English setup overview](README.md) / [简体中文使用入口](README.zh-CN.md)

Game compatibility: STS2 v0.111.0 / 41cef1ea / Steam build 24724944, Mac arm64 and Linux x86_64, single-player Silent A0. Platform guides list build dependencies, profile inputs, and commands.

Recorder ZIP SHA-256: `07ade55c8e4edf18d307642da8b05509e1cc5b1fc277ab20c7720844e5b25f97`.

## License

[MIT](LICENSE) and [third-party attribution](NOTICE.md).
