# Read a decision from a trajectory

English | [简体中文](README.zh-CN.md) | [Project overview](../README.md)

This example shows the data passed between an environment and a learning pipeline: an observation, a selected action, and the observed successor. The included fixture is synthetic and requires only Python 3.12.

## Try the data format

From the repository root:

```bash
python3 -B examples/read_trajectory.py examples/synthetic-action-log.jsonl
```

The command prints one JSON object on stdout, with `observation`, `action`, and `nextObservation`. In this fixture, `synthetic:choose` changes the example gold field from 0 to 1. The IDs and result are illustrative; this command reads a file.

The reader pairs `accepted` and `completed` lifecycle records by process and sequence, using the repository's envelope validator. It reports inputs without successors and rejected, failed, or interrupted outcomes on stderr. Use these labels when selecting training samples.

## Read your own engine output

1. Follow [Linux setup and manual control](../linux/README.md#prepare-a-workspace) to prepare your game/profile and run a bounded session.
2. At each decision, choose a displayed action number. The controller submits its `actionId` with the current process and decision identities.
3. After the engine reaches a successor and the session finishes, find `action-log.jsonl` in the printed `launchEvidence` directory.
4. Run the reader on that file:

```bash
python3 -B examples/read_trajectory.py '/absolute/path/from/launchEvidence/action-log.jsonl'
```

For programmatic decisions, replace `choose(obs)` as described in [policy integration](../linux/README.md#connect-your-policy). The policy returns `{"actionId": selected_action["id"]}` from the current choices; the engine supplies the successor. Your training code can consume the reader's JSON output and define its own features and rewards.

For human data, follow [Mac recording](../mac/README.md) and [Linux replay](../docs/REPLAY.md), then read the resulting Linux action log. The [protocol](../linux/PROTOCOL.md) describes nested decisions and status fields.
