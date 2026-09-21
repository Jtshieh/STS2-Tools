# Mac gameplay recorder

English | [简体中文](README.zh-CN.md) | [Project overview](../README.md)

Collect human demonstrations while playing STS2 with the mouse. The recorder captures semantic inputs, targets, selection choices, and the player-visible observation at the next decision. Use these trajectories for dataset processing, imitation learning, or [Linux replay](../docs/REPLAY.md).

## Compatibility

| Item | Supported configuration |
| --- | --- |
| Game | v0.111.0 / 41cef1ea / Steam build 24724944 |
| Platform | macOS Apple Silicon / arm64 |
| Gameplay | Single-player Silent A0, using your own progression |
| Recorder package | v0.1.0-alpha; recorder revision 0.2.3 |

The [Mac manifest](config/) contains platform-specific file hashes.

## What you need

### Provided by this repository

| Item | Purpose and access |
| --- | --- |
| Precompiled mod | [Download Mac ZIP](https://github.com/Jtshieh/STS2-Tools/releases/download/v0.1.2-alpha/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip). Install `Sts2Recorder.dll` and `Sts2Recorder.json`; retain the included license files. |
| Recorder source and build script | [src/](src/) and [build.py](build.py), available in the [source ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/heads/main.zip). Build or modify the mod locally. |
| Workspace and export tools | [prepare.py](prepare.py), [launch.py](launch.py), and [export_trace.py](export_trace.py) create a separate game/profile workspace and organize recorded trajectories. |
| Version configuration | [config/](config/) identifies the compatible Mac game files. |

### Prepare yourself

| Route | Requirements |
| --- | --- |
| Install the precompiled mod | Compatible Mac game and your game profile. The game supplies .NET and Harmony for mod loading. |
| Export a session | Python 3.12 and the repository source. |
| Build from source | Python 3.12, arm64 .NET 9 SDK (reference version 9.0.303), and the compatible game app as a local assembly reference. |
| Create a separate workspace | Source-build requirements, macOS `/usr/bin/sandbox-exec`, and your account's `profile.save` plus `profileN/saves/prefs.save` and `progress.save`. Continue also uses the starting `current_run.save`. |

## Install the mod

1. Exit the game and download the mod ZIP above.
2. In Steam, browse the game's local files. In Finder, choose **Show Package Contents** for `SlayTheSpire2.app` and open `Contents/MacOS/mods`. Create `mods` if needed.
3. Copy `Sts2Recorder.dll` and `Sts2Recorder.json` into that directory.
4. Launch the game and complete its mod-loading prompt. For recordings to replay with this environment, enable the recorder alone.
5. At the main menu, check for `RECORDER READY` and the log path. Select your profile and start Silent A0. During play, the indicator shows `RECORDER ON`.

This installation uses the game's normal modded profile and save location. To start from an existing progression in a separate profile workspace, use the preparation flow below.

To uninstall, exit the game and remove the two recorder files. Your saves and recorded sessions remain available.

## Record and find your logs

Play through combat, rewards, map choices, events, shops, rest sites, and nested card selections. Each game process writes a separate session under Godot's `user://sts2-recorder/logs/<process-id>/`. The main menu and game log display its absolute path.

| File | Use |
| --- | --- |
| `actions.jsonl` | Append-only semantic events: observations, inputs, acceptance/delivery status, nested choices, and successors. |
| `replay-input.private.json` | Run seed and version information for initializing replay. Keep this separate from model observations. |

The `INCOMPLETE` indicator means the session contains a recording issue. Inspect the export report before selecting a segment for training or replay. Menu returns, loads, and restarts create continuity boundaries; process separate segments individually.

## Export and process demonstrations

After exiting the game, run this command from the repository root. Use the session path displayed by the recorder and a new output directory:

```bash
python3 -B mac/export_trace.py \
  --session '/absolute/path/to/session' \
  --output '/absolute/path/to/new-private-export'
```

| Export file | Meaning |
| --- | --- |
| `actions.raw.jsonl` | Unmodified captured event stream. This or the original `actions.jsonl` is the Linux import input. |
| `commands.proposed.jsonl` | Candidate observation/action pairs for your dataset processing. |
| `successors.jsonl` | Recorded observations following inputs, including nested decision boundaries. |
| `rejected-attempts.jsonl` | Purchase attempts that were rejected by the game. |
| `conversion.json` | Event counts, continuity/recording issues, and mappings requiring review. |
| `SHA256SUMS` | Hashes of the exported files. |

For imitation learning, pair each input's observation with its recorded choice and check delivery and successor events. Use `conversion.json` when filtering or labeling samples. A selection input and its final selection result describe different stages of the same interaction; count the input as the decision. `conversion.json` uses `sts2-gui-conversion-v2`: `structuralReady` and `issues` describe capture structure and continuity, while `requiredActionBindings` lists action-adapter reviews separately. `observationComparison.status` and `linuxReplay.status` are `not_run` at export; debug-intervention recordings use `not_applicable` for replay. Use the Linux replay result for cross-platform comparison.

Continue with [Mac-to-Linux replay](../docs/REPLAY.md) or the [trajectory protocol](../linux/PROTOCOL.md) to build your own processing pipeline.

## Build the mod from source

Run from the repository root:

```bash
python3 -B mac/build.py \
  --game-app '/absolute/path/to/SlayTheSpire2.app' \
  --dotnet '/absolute/path/to/dotnet'
```

The ZIP is written to `mac/.private/dist/Sts2Recorder-macos-arm64-v0.1.0-alpha.zip`. Install it using the steps above. The build references the game's local `sts2.dll`, `GodotSharp.dll`, and `0Harmony.dll` and packages the recorder's own assembly.

## Optional: separate game and profile workspace

Use this route to record with an independent game/profile copy and preserve the exact starting saves for replay. `profile.save` selects the profile; `progress.save` stores progression; `current_run.save` stores an in-progress run. Match `--profile-id` to the selector and `profileN` directory.

Run from the repository root:

```bash
export STS2_MAC_WORK='/absolute/path/to/recorder-workspace'

python3 -B mac/prepare.py --workspace "$STS2_MAC_WORK" \
  --game-app '/absolute/path/to/clean/SlayTheSpire2.app' \
  --selector '/absolute/path/to/account/profile.save' \
  --profile '/absolute/path/to/account/profile2/saves' --profile-id 2

python3 -B mac/build.py --workspace "$STS2_MAC_WORK" \
  --dotnet '/absolute/path/to/dotnet'

python3 -B mac/launch.py --workspace "$STS2_MAC_WORK"
```

For Continue, add `--current-run '/absolute/path/to/current_run.save'` to `prepare.py`. To reuse game settings, add `--settings '/absolute/path/to/settings.save'`. Use a new workspace for a different starting state.

Preparation creates ordinary copies of the app and your profile. The launcher restricts writes to the workspace and logs, with game networking disabled. The working profile saves normally. Complete the game's initial mod prompt, then exit and relaunch if it requests a restart.

Paths beneath `$STS2_MAC_WORK/.private/`:

| Path | Content |
| --- | --- |
| `work/SlayTheSpire2.app` | Playable game copy with the built recorder installed. |
| `work/profile/default/1/modded/profileN/saves/` | Working modded profile. |
| `profile-input/` | Profile files captured during preparation. |
| `logs/<process-id>/` | Session events and launch metadata. |
| `logs/<process-id>/initial-profile/` | Exact profile files copied before that launch; retain these for Continue replay. |

The session exporter copies event files and metadata. Transfer `initial-profile/` separately when preparing Linux replay.

## License

[MIT](../LICENSE), with [recorder attribution](src/ATTRIBUTION.md) and [third-party notices](../NOTICE.md).

### Callback metadata awaiting Mac validation

The repair source uses recorder revision 0.2.4 to record synchronous cross-act notifications and their Proceed parent. `engine_notification` retains callback entry/return evidence without increasing the player-input count; real nested selections remain inputs. The v2 export report implementation is retained, with structural checks added for the new notification. Source compiles offline against pinned Linux reference assemblies. Native arm64 build and GUI validation remain required, including a cross-act Proceed, callback cleanup after failure/exit, and nested card selections. The released DLL remains 0.2.3; no asset has been republished.
