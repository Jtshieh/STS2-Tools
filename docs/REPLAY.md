# Replay Mac demonstrations on Linux

English | [简体中文](REPLAY.zh-CN.md) | [Project overview](../README.md)

Replay executes the recorded human choices through the Linux engine interface and compares the observations at decision boundaries. Use it to inspect demonstration data, reproduce interactions, and obtain Linux action logs for further processing.

## Prepare the starting state

Both platforms use STS2 v0.111.0 / 41cef1ea / Steam build 24724944: the Mac arm64 game for recording and the Linux x86_64 game for replay. Each platform has its own file-hash manifest.

Choose a starting boundary before recording:

| Start | Materials to preserve |
| --- | --- |
| Continue | Initial `current_run.save`, account `profile.save`, and the active profile's `progress.save` and `prefs.save`. |
| New run | Matching profile progression, character Silent, Ascension 0, Standard mode, tutorial settings, and recorded seed. |

For Continue, the [Mac separate-workspace launcher](../mac/README.md#optional-separate-game-and-profile-workspace) captures the exact pre-launch saves in the session's `initial-profile/` directory. Select files from the active modded profile there. With the drop-in mod, preserve those starting files yourself before recording. The save written after playing represents a later state.

Read the seed from `replay-input.private.json`. Keep initialization metadata separate from the observations used by your policy.

## Collect the replay inputs

Transfer these files to your Linux workspace's `.private/` directory, with directory permissions `0700`:

- Original `actions.jsonl`, or the byte-preserving `actions.raw.jsonl` from [Mac export](../mac/README.md#export-and-process-demonstrations).
- `replay-input.private.json` for the seed and version.
- Starting profile files from the table above. Transfer `initial-profile/` separately when using the Mac launcher; the session exporter copies events and metadata.

The Linux importer reads the raw semantic event stream. `commands.proposed.jsonl` is a preprocessing output for reviewing mappings and building datasets.

## Prepare Linux and import a segment

Follow [Linux setup](../linux/README.md#prepare-a-workspace) with the starting profile. For Continue, add `--current-run` with the preserved initial run save to `prepare.py`. Keep the resulting `STS2_WORK` value. Run the following commands from the repository's `linux/` directory:

```bash
python3 -B scripts/import_trace.py \
  --input '/absolute/path/to/actions.jsonl' \
  --output "$STS2_WORK/.private/replay.json" \
  --seed 'RECORDED_SEED' --start continue \
  --current-run '/absolute/path/to/initial/current_run.save' --actions 12

python3 -B scripts/sts2_play.py \
  --config "$STS2_WORK/.private/config.json" \
  --mode replay --trace "$STS2_WORK/.private/replay.json" \
  --seed 'RECORDED_SEED' --root-run replay-1 --decisions 12 --tutorials no
```

Replace `RECORDED_SEED` with the recorded seed. Replace `12` with the number of consecutive inputs to replay, starting at action 1; each input must have a recorded successor. `--decisions` must equal the imported input count. The importer stores the initial run-file hash and the final recorded observation in the bundle.

For a new-run workspace, import with `--start new` and omit `--current-run`; keep the seed and other starting conditions matched. The same replay command consumes that bundle.

## Read the result

`sts2_play.py` prints a `launchEvidence` directory. When replay finishes successfully, its `replay-result.json` contains:

| Field | Interpretation |
| --- | --- |
| `inputs` | Number of recorded inputs consumed. |
| `mechanicalMatched` | Recorded decision states and the endpoint matched under the comparator's defined normalizations. |
| `strictDecisionTimingMatched` | Decision availability matched, including button timing. |

`action-log.jsonl` contains action lifecycle records and successor observations. On an import or replay error, inspect the reported event/action and the session's `exit.json` and `launcher.log`. Preserve the original recording when investigating a mismatch.

Comparison covers card identities and effects, event options, rewards, resources, and transition states. Card instance IDs are mapped across processes. The exact display normalizations are listed in the [protocol](../linux/PROTOCOL.md#replay-comparison).

Mouse dragging can change when `end_turn` is exposed. To study this specific timing difference, add `--allow-timing-differences` to the replay command. When used to accept a timing difference, the result records `strictDecisionTimingMatched: false` while reporting the mechanical comparison separately.

## Handle recording boundaries

The importer requires one process/run identity, consecutive events, accepted inputs, and actual successors. Real nested inputs remain separate commands, and their parent decisions must close within the selected segment. Unknown UI inputs, missing inputs, failures, or a continuity boundary inside the segment stop import. `claim_relic:*` and `deselect_hand:*` still lack matching Linux Bridge bindings and are rejected.

Source recorder revision 0.2.4 records `SetLocalPlayerReady` inside an original synchronous Proceed callback as paired `engine_notification` events, with `callback-scope-v1` attribution, a notification sequence, and the parent action sequence. The importer verifies callback entry/return ordering and executes only the parent Proceed. Old `next_act` events without reliable attribution remain rejected. The metadata source compiles against Linux reference assemblies; native Mac arm64 build and GUI validation remain pending. Existing released DLLs are unchanged.

Treat segments after a menu return, load, or restart as separate recordings. Continue bundles carry `initial_resume_unverified`, identifying the load as their starting boundary.

## Adapt the retained cross-act recording

Only the reviewed 2026-09-15 raw recording and its exact starting save can use `--legacy-adapter mac-20260915-cross-act`. The option checks the complete raw-file SHA-256 and starting-save SHA-256; edited files, other recordings, and new-run starts are rejected. It derives notification ownership from a unique Proceed callback and its completed Vote evidence, without hardcoded action numbers. Reviewed pile-view, preview, and pause/resume UI events retain individual conversion reasons. These exceptions do not apply to unknown inputs in other traces. Out-of-segment load and exit events remain in the original bytes.

For the complete historical segment, import with `--actions 158 --legacy-adapter mac-20260915-cross-act`, then replay with `--decisions 157`. `--actions` counts source `action_initiated` events; `--decisions` counts imported executable commands. The 158 initiated events yield 157 inputs, retaining all 38 nested inputs with parent relationships. The bundle preserves `sourceRaw`, source sequences, parent relationships, and `transformations` as private validation data.

Offline validation confirms that converted commands and the endpoint equal the retained evidence. Comparison against the retained Linux delivery records passes for 157 decisions and the final state/action list. The 19 previously verified cross-act successors are reused; 77 `end_turn` timing differences still fail strict timing consistency. This is not a new native game or Mac GUI test and does not certify other traces. Import always reports `replayEvidence.status: not_run`.

## Use the output in your workflow

Process the accepted/completed action pairs described in the [trajectory protocol](../linux/PROTOCOL.md#exported-action-log), or use the [Linux combat sample collector](../linux/README.md#training-and-datasets). Your data pipeline can retain replay outcomes alongside each demonstration segment and decide which samples to include.
