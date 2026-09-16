# Linux engine interface

English | [简体中文](README.zh-CN.md) | [Project overview](../README.md)

Drive the original STS2 engine from structured observations and actions. Use the supplied controller for manual decisions, a JSONL policy worker for automatic decisions, or the file bridge for your own controller. Export trajectories for external training and replay [Mac human demonstrations](../docs/REPLAY.md) against the engine.

## Compatibility

| Item | Supported configuration |
| --- | --- |
| Game | v0.111.0 / 41cef1ea / Steam build 24724944 |
| Host | Linux x86_64; reference host Ubuntu 24.04.5 |
| Isolated runtime | Ubuntu 22.04.5 userland / glibc 2.35 |
| Gameplay | Single-player Silent A0 with your profile's progression |
| Starting state | New run, or Continue from an explicitly supplied `current_run.save` |

## What you need

### Provided by this repository

| Item | Purpose and access |
| --- | --- |
| Linux source and entry points | [Download source ZIP](https://github.com/Jtshieh/STS2-Tools/archive/refs/heads/main.zip) or clone the repository. [scripts/](scripts/) provides prepare, build, play, export, import, and training commands. |
| Engine adapters | [src/host/](src/host/) contains the observation/action bridge; [src/guard/](src/guard/) contains runtime isolation code. Build these with your game and toolchain. |
| Configuration | [Game manifest](config/game-linux.json) and [public userland archive list](config/userland-archives.json) specify versions, URLs, and hashes. |
| Protocol and examples | [Protocol](PROTOCOL.md), [log schema](schemas/action-log-v1.json), [policy worker](scripts/sts2_policy.py), and [learning example](scripts/sts2_train.py). |

### Prepare yourself

The Linux route builds locally. Prepare these inputs before running the setup commands:

| Input | Version and purpose |
| --- | --- |
| Game | Compatible Linux x86_64 installation, including the release executable, `.pck`, and managed dependencies. |
| Profile | Your account's `profile.save` selector and the selected `profileN/saves/prefs.save` and `progress.save`. Continue also requires the initial `current_run.save`. |
| CLI runtime | Python 3.12, git, bubblewrap, zstd; the host must allow bubblewrap's user namespaces. Python scripts use the standard library. |
| Native build tools | GCC 13.3.0 (reference Ubuntu package `13.3.0-6ubuntu2~24.04.1`) and binutils 2.42. The guard build is checked against a fixed output hash. |
| .NET | SDK 9.0.303 with runtime 9.0.7. Supply its installation directory as `--dotnet-root`. |
| Godot build inputs | Complete Godot 4.5.1 stable mono Linux x86_64 directory, including `GodotSharp/Tools/nupkgs`. |
| Offline runtime packages | `microsoft.netcore.app.runtime.linux-x64.9.0.7.nupkg` and `microsoft.aspnetcore.app.runtime.linux-x64.9.0.7.nupkg`, together in one directory. |
| Runtime userland archives | The 18 public archives listed in [config/userland-archives.json](config/userland-archives.json), saved using the listed filenames. |
| Storage | Allow at least 8 GiB of free space for initial preparation and one session. |

## Prepare a workspace

From the repository root, enter `linux/` and set the paths below. `STS2_WORK` can also point to a separate new workspace. Subsequent commands run from `linux/`.

```bash
cd linux
export STS2_WORK="$PWD"
export STS2_GAME='/absolute/path/to/compatible-linux-game'
export STS2_PROFILE='/absolute/path/to/profile2/saves'
export STS2_SELECTOR='/absolute/path/to/account/profile.save'
export STS2_DOTNET='/absolute/path/to/dotnet-9.0.303'
export STS2_GODOT='/absolute/path/to/Godot_v4.5.1-stable_mono_linux_x86_64'
export STS2_FEED='/absolute/path/to/two-runtime-nupkgs'
export STS2_ARCHIVES='/absolute/path/to/public-userland-archives'
mkdir -m 700 -p "$STS2_WORK/.private"
```

Place a copy of `config/userland-archives.json` at `$STS2_ARCHIVES/archive-manifest.json` beside the downloaded archives. Build the local userland, prepare the game/profile inputs, and build the host integration:

```bash
python3 -B scripts/build_private_userland.py \
  --inputs "$STS2_ARCHIVES" --output "$STS2_WORK/.private/userland" \
  --zstd /usr/bin/zstd

python3 -B scripts/prepare.py --workspace "$STS2_WORK" \
  --game "$STS2_GAME" --profile "$STS2_PROFILE" \
  --selector "$STS2_SELECTOR" --profile-id 2 \
  --dotnet-root "$STS2_DOTNET" --godot-root "$STS2_GODOT" \
  --runtime-feed "$STS2_FEED" \
  --userland-root "$STS2_WORK/.private/userland/root" \
  --userland-manifest "$STS2_WORK/.private/userland/runtime-manifest.json"

python3 -B scripts/build.py --config "$STS2_WORK/.private/config.json"
```

Preparation verifies the game files, creates a game baseline and profile copies, and generates `.private/config.json`. The profile selected by `profile.save` must match `--profile-id` and the `profileN` directory. `progress.save` supplies long-term progression; `current_run.save` supplies a particular run.

For Continue, add `--current-run '/absolute/path/to/current_run.save'` to `prepare.py`. Use the exact save from the desired starting boundary. For a different starting state, choose a new workspace. If you already maintain an ordinary, immutable game baseline, `--readonly-game` reuses it as the baseline input.

## Run a decision loop

For a prepared new-run workspace, try manual control:

```bash
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode manual --seed STS2DEMO01 --root-run demo-1 --decisions 30 --tutorials no
```

The terminal presents the current state and available actions. Enter the displayed action number. `--decisions` bounds the session to 1–1000 inputs; `--tutorials` explicitly selects tutorial behavior. For a Continue workspace, supply the seed stored in its initial run.

To use the policy worker:

```bash
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode auto --seed STS2DEMO01 --root-run demo-2 --decisions 30 --tutorials no
```

At each decision, the controller reads an observation, obtains an action ID from the policy, submits it to the engine, and waits for the next decision. Combat, map, event, reward, shop, rest-site, and nested-selection decisions use the same loop.

## Connect your policy

`--mode auto` runs a copy of [scripts/sts2_policy.py](scripts/sts2_policy.py). Replace its `choose(obs)` function to connect your decision rule or model. Keep the JSONL input/output loop at the bottom of the file. For example, this working selection rule samples from the currently available actions:

```python
import random

policy_rng = random.Random(0)

def choose(obs):
    action = policy_rng.choice(obs["actions"])
    return {"actionId": action["id"], "controller": "custom"}
```

Replace the sampling expression with your policy's scoring or inference. The input contains `schema`, `processRunId`, `decisionId`, `state`, and `actions`. Each action has an `id`, `kind`, `label`, and `detail`. Return an `actionId` from that observation's `actions`; the controller adds the process and decision identities to the engine request.

The worker receives one JSON object per line on stdin and returns one JSON object per line on stdout, flushing each response. Send diagnostics to stderr. The default worker has a 10-second response timeout, a 256 MiB address-space limit, and a 90-second CPU budget. It runs in a separate namespace with Python/system files and the policy source; game files and run saves stay in the engine process.

For a framework-backed model or external service, adapt the worker launch block in [scripts/sts2_play.py](scripts/sts2_play.py) to provide the required model/runtime and resources. That block is the extension point for replacing the supplied worker. `--checkpoint` loads the included `sts2-small-policy-v1` model format through [sts2_small_policy.py](scripts/sts2_small_policy.py).

For a custom controller, follow the [file bridge protocol](PROTOCOL.md#file-bridge): read `observation.json`, atomically write `action.json`, then consume the successor observation or terminal result. Use one controller per session.

## Results and trajectories

The launch command prints `launchEvidence`, the session directory beneath the workspace's `.private/` directory.

| File | Content |
| --- | --- |
| `controller.jsonl` | Observations and submissions made by the controller. |
| `action-log.jsonl` | Exported action lifecycle records with pre-action and successor observations. |
| `runtime.json` | Location of the engine's detailed output and working profile. |
| `lineage.json` | Run identity and starting-input metadata. |
| `exit.json`, `launcher.log` | Exit status and launch/runtime diagnostics. |
| `replay-result.json` | Comparison result for replay mode. |

To export a session again:

```bash
python3 -B scripts/export_log.py --launch '/absolute/path/from/launchEvidence' \
  --output "$STS2_WORK/.private/export.jsonl"
```

Group action-log rows by `processRunId` and `sequence`. An `accepted` row carries the pre-action observation and selected action; a `completed` row carries the observed successor. Read failures and interruptions as separate outcomes. A decision budget ends with `interrupted/budget_truncated`, which is a truncated episode for dataset processing. See the [protocol](PROTOCOL.md) for nested decisions and lifecycle fields.

## Training and datasets

Your training code can consume the action logs, define features and rewards from player observations, update a model, and send its next decisions through the worker or bridge. Keep replay initialization metadata separate from policy inputs.

The repository includes a 64-parameter masked-softmax combat imitation-learning example using Adam. Its collection command selects delivered combat inputs with observed successors from a Linux session. Before collection, review that session's observation adapter and create a review JSON with `trainingCombatPathApproved: true` and `approvedBridgeSha256: ["<reviewed SHA-256>"]`. The hash must match the session's `input-source/Bridge.cs`, under the `evidenceRoot` in `runtime.json`.

```bash
python3 -B scripts/sts2_train.py collect \
  --launch '/absolute/path/from/launchEvidence' \
  --gate '/absolute/path/to/observation-review.json' \
  --output "$STS2_WORK/.private/samples.json"

python3 -B scripts/sts2_train.py update --data "$STS2_WORK/.private/samples.json" \
  --output "$STS2_WORK/.private/checkpoint-4.json" --updates 4

python3 -B scripts/sts2_train.py resume --data "$STS2_WORK/.private/samples.json" \
  --checkpoint "$STS2_WORK/.private/checkpoint-4.json" \
  --output "$STS2_WORK/.private/checkpoint-8.json" --updates 4
```

Outputs must use new filenames. `resume` restores the model, optimizer, sampler, and training RNG for the same dataset; the example permits up to 20 total updates. To evaluate the resulting combat policy in a new-run workspace:

```bash
python3 -B scripts/sts2_play.py --config "$STS2_WORK/.private/config.json" \
  --mode auto --checkpoint "$STS2_WORK/.private/checkpoint-8.json" \
  --seed STS2EVAL01 --root-run eval-1 --decisions 30 --tutorials no
```

This model handles combat decisions; the supplied worker uses rules for other phases. For human demonstrations, process the [Mac export](../mac/README.md#export-and-process-demonstrations) into your dataset format, or [replay it on Linux](../docs/REPLAY.md) and collect from that session. The provided collector reads Linux session output.

## Workspace storage

Runs use independent game/profile working copies. After a session exits and its protection checks complete, the tools reclaim copied game assets and rebuildable caches while retaining logs, profiles, results, and inputs. To retry reclamation for finished sessions:

```bash
python3 -B scripts/work_materials.py --config "$STS2_WORK/.private/config.json"
```

The command uses the session lock and records results in `work-materials-cleanup.jsonl`. If cleanup reports an incomplete exit check, inspect the session's `exit.json` and `launcher.log` before retrying. Retained trajectories and profiles continue to occupy space.

## License

[MIT](LICENSE) and [third-party attribution](NOTICE.md).
