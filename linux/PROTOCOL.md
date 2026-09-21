# Observation, action, and trajectory protocol

English | [简体中文](PROTOCOL.zh-CN.md) | [Linux guide](README.md)

The Linux interface exposes decisions from the original game through a file bridge. The supplied Python controller wraps that bridge with a JSONL policy worker. This page describes the contracts used by controllers, dataset processors, and replay adapters.

| Data | Schema |
| --- | --- |
| Linux observation | `sts2-observation-v1` |
| Linux action log | `sts2-action-log-v1` |
| Mac semantic events | `sts2-gui-semantic-v1` |
| Imported replay bundle | `sts2-replay-bundle-v1` |

## Observation and policy response

An observation contains:

| Field | Meaning |
| --- | --- |
| `schema` | Observation schema version. |
| `processRunId`, `decisionId` | Identity of this process and decision. |
| `state` | Current phase and player-visible state, including phase-specific card, target, option, and resource information. |
| `actions` | Currently available choices, each with `id`, `kind`, `label`, and `detail`. |

The policy receives one observation per stdin line and returns a JSON object with `actionId` on stdout. Select that value from the current `actions[].id`. Additional fields such as `controller` and `reason` are used by the example worker; the controller consumes `actionId`. See [policy integration](README.md#connect-your-policy).

`actions[].detail` preserves the choice's card, target CombatId, option, cost, or other relevant identity. Card `instance` values are stable within a process. Across processes, maintain a one-to-one identity mapping; card display positions can change between observations.

Policy observations contain player-visible state. Replay seeds and starting saves belong to initialization metadata, outside the policy observation.

## File bridge

The session's `runtime.json` points to `evidenceRoot`; bridge files are under `evidenceRoot/runtime-work/bridge/`. The engine writes observations and status files atomically.

1. Read `observation.json` and remember its `(processRunId, decisionId)`.
2. Choose an action from that observation.
3. Write a temporary file in the bridge directory and atomically rename it to `action.json`. Use the observation's process/decision identity and the selected action ID.
4. Check `heartbeat.json` for an increased `submittedCount` and `rejection.json` for a matching rejected request.
5. Read the next decision with a new identity, or `terminal.json` for game-over or decision-budget completion.

Request shape, with illustrative identifiers:

```json
{"processRunId":"example-process","decisionId":1,"actionId":"example-action"}
```

The engine rejects stale, duplicate, wrong-process, or unavailable actions. A changed game state can invalidate an observation, so submit against the latest decision. Use a single controller and keep one request in flight. To use a custom controller, replace the decision/submission loop in [sts2_play.py](scripts/sts2_play.py) or reuse its launch orchestration with your bridge consumer.

`heartbeat.json` reports `decisionId`, `phase`, `waiting`, `elapsedSeconds`, and `submittedCount`. `waiting` distinguishes controller input from game progress. A matching `rejection.json` contains the request and reason. `terminal.json` contains `outcome`, `submittedCount`, and the terminal state.

## Decision boundaries

Each player input is a decision, including selecting, deselecting, and confirming cards in nested choices. A `completed` record means that input reached an observed successor boundary. If the successor phase is `hand_selection`, `grid_selection`, or `choose_card`, continue with the choices offered there to finish the interaction.

Automatic draws, damage, enemy actions, and relic triggers are reflected in the successor state. Selection results describe what the engine accepted after the input sequence. Dataset processors should preserve the relation between parent actions, nested inputs, and final results.

## Exported action log

[schemas/action-log-v1.json](schemas/action-log-v1.json) and [scripts/protocol.py](scripts/protocol.py) define each JSONL row:

| Field | Meaning |
| --- | --- |
| `schema` | `sts2-action-log-v1`. |
| `rootRunId`, `processRunId` | Root run and actual engine process identities; launch-attempt identity is retained if the engine never reaches observations. |
| `slAttempt`, `recovery` | `0` and `null` in this version. |
| `sequence` | Input sequence number; startup failures/interruption can use `0`. Several lifecycle rows share a sequence. |
| `status` | `initiated`, `accepted`, `delivered`, `completed`, `rejected`, `failed`, or `interrupted`. |
| `action` | Action ID and decision ID; accepted actions include the observed kind, label, and detail. |
| `observation` | Pre-action observation on initiation/acceptance, or the successor on completion. May be null for other statuses. |
| `error` | Reason for rejection, failure, or interruption; otherwise null. |

`initiated` records the controller's submission. `accepted` records bridge validation. `delivered` records the original callback being called. `completed` requires an observed successor. Exported rows are sorted by sequence and lifecycle status; raw controller/engine logs retain their original event order.

For a training transition, group by `(processRunId, sequence)` and pair the accepted observation/action with its completed successor. Completed successors contain `state` and `actions`; pre-action observations also carry the schema and decision identity. Retain rejection/failure/interruption labels separately when deciding which rows belong in a dataset.

## Time budgets and continuity

The Linux manual controller allows 900 seconds for input. An accepted action has 40 seconds to reach a stable successor. The engine supervisor has a 1200-second wall budget, and the outer controller has a 1500-second budget including launch and shutdown. The policy worker has its own response and resource limits described in the [Linux guide](README.md#connect-your-policy).

A decision-budget stop exports `interrupted` with `budget_truncated`. Menu returns, loads, and restarts create continuity boundaries. A Continue replay begins at its supplied save and carries `initial_resume_unverified`. Treat the resulting episode as beginning at that boundary; model checkpoint resume restores training state.

## Mac input mapping

[import_trace.py](scripts/import_trace.py) converts raw Mac semantic events into a replay bundle. It selects the complete prefix requested by `--actions N`, beginning at `actionSequence=1`, and stores the raw-file hash and recorded endpoint. Each input needs acceptance/callback evidence and a successor, with consecutive events and one process/run identity.

The bundle contains recorded commands and observations, the seed, the start mode, and the initial save hash for Continue. [Mac-to-Linux replay](../docs/REPLAY.md) explains how to prepare these files. Unsupported events and unbound `next_act` inputs produce import errors identifying the required adaptation.

The bundle retains the UTF-8 representation of exact raw bytes in `sourceRaw`, plus `sourceSha256`, the selected `sourceEventRange`, `sourceActionCount`, and individual `transformations`. Commands carry `macActionSequence`, `macEventSequence`, `parentActionSequence`, `role`, and `conversionReason`. Roles distinguish `player_input` from `nested_input`. Internal `engine_notification` events are evidence and never generate a submit. They require a notification sequence, parent action sequence, `attributionRevision=callback-scope-v1`, `relation=synchronous_callback`, and paired `callback_entered/callback_returned` statuses fully inside the original Proceed callback. Legacy traces require the explicit source-bound adapter described in the [replay guide](../docs/REPLAY.md#adapt-the-retained-cross-act-recording).

Final comparison reads the actual last `action_response`, verifies its sequence and uniqueness and that its state equals `terminal.json`, then compares the complete endpoint state and legal actions. The Mac v2 export report continues to separate structure, action adaptation requirements, and unexecuted Linux replay. Import success never writes a native replay pass.

## Replay comparison

The comparator preserves event options, rewards, costs, resources, card upgrades/effects, and transition state. It maps card instances across processes, matches reordered grids by identity, and allows a newly encountered reward card to match by unique full mechanical properties. Ambiguous candidates fail comparison.

The defined display normalizations cover Neow greetings, the Tezcatara greeting prefix, equivalent rest-site display text, ordering of complete grid-card labels, and absent NCard display nodes. Original observations remain intact. See [compare_gui.py](scripts/compare_gui.py) for the exact transformations.

An `end_turn` availability difference during Mac card dragging is reported as a timing difference. Replay rejects it by default; `--allow-timing-differences` allows this case while retaining a separate `strictDecisionTimingMatched: false` result. Other state/action differences stop comparison. The [replay guide](../docs/REPLAY.md#read-the-result) explains the output fields.
