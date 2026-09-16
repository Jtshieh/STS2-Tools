# Linux tools v0.1.0-alpha

The Linux component exposes the original STS2 engine to external decision and training code.

## Included interfaces

- Structured player observations and available actions through the file bridge.
- `sts2_play.py` manual, automatic-policy, and replay modes.
- JSONL action lifecycle export and raw Mac semantic trace import.
- Replay comparison with card-instance mapping and card-effect observations.
- A 64-parameter combat imitation-learning example using Adam, with training checkpoint resume.
- Configurable game, profile, toolchain, and workspace paths; storage checks and reclamation of completed-session work materials.

## Use this version

[English Linux guide](README.md) / [简体中文 Linux 使用说明](README.zh-CN.md)

The guides specify the compatible Linux game, pinned build dependencies, workspace preparation, policy integration, and trajectory processing. For Mac recordings, follow the [replay guide](../docs/REPLAY.md).

[Repository release](../RELEASE.md) · [MIT license](LICENSE) · [Third-party attribution](NOTICE.md)
