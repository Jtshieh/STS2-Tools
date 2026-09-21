#!/usr/bin/env python3
"""Import a bounded complete Mac semantic segment, without guessing missing inputs."""
import argparse
import hashlib
import json
import os
from pathlib import Path

SCHEMA = 'sts2-replay-bundle-v1'

def validate_bundle(b):
    if b.get('schema') != SCHEMA or b.get('start') not in ('new', 'continue'):
        raise ValueError('Unknown replay bundle/start boundary')
    if not b.get('seed') or not isinstance(b.get('commands'), list) or not 1 <= len(b['commands']) <= 1000:
        raise ValueError('Missing seed or bounded inputs')
    if not isinstance(b.get('endpoint', {}).get('state'), dict) or not isinstance(b.get('endpoint', {}).get('actions'), list):
        raise ValueError('Missing recorded endpoint')
    if b['start'] == 'continue' and (len(b.get('inputRunSha256', '')) != 64):
        raise ValueError('Continue requires initial run-file hash')
    for row in b['commands']:
        obs = row.get('observation', {})
        if row.get('kind') != 'submit' or not isinstance(obs.get('state'), dict) or not isinstance(obs.get('actions'), list):
            raise ValueError('Incomplete recorded command/observation')
        if sum(a.get('id') == row.get('actionId') for a in obs['actions']) != 1:
            raise ValueError('Unknown/ambiguous action in its recorded observation')
    return b

# Explicitly scoped to the reviewed, unmodified historical source and starting save.
LEGACY_ADAPTER = 'mac-20260915-cross-act'
LEGACY_SOURCE_SHA256 = 'd2210fe1b1a2d650bc3598615eb176253c2c922a45d37d55f3c63a14f63143cd'
LEGACY_RUN_SHA256 = '00b74491631d1a9382d804a97657e9130a6217ef8819bfb42c868c039e5e3c50'
ACT_SOURCE = 'RunReplays.ActChangeSynchronizer.SetLocalPlayerReady'
CALLBACK_SOURCE = 'NClickableControl.OnReleaseHandler'
ACCEPTED = {'callback_returned', 'accepted_into_original_queue', 'purchase_completed',
            'selection_input_applied', 'accepted_original_selection'}


def notification_parent(row, actions, statuses, segment):
    """Accept only recorder-declared synchronous nesting inside an original Proceed."""
    d = row['data']; parent_seq = d.get('parentActionSequence')
    parent = actions.get(parent_seq) if type(parent_seq) is int else None
    if (d.get('actionId') != 'next_act' or d.get('role') != 'engine_notification'
            or d.get('source') != ACT_SOURCE or d.get('attributionRevision') != 'callback-scope-v1'
            or d.get('relation') != 'synchronous_callback' or row.get('actionSequence') is not None
            or parent is None or parent['data'].get('actionId') != 'proceed'
            or parent['data'].get('source') != CALLBACK_SOURCE
            or parent['eventSequence'] >= row['eventSequence']):
        raise ValueError('Internal notification lacks reliable synchronous Proceed parent')
    returns = [x for x in statuses.get(parent['actionSequence'], [])
               if x['data'].get('status') == 'callback_returned']
    if len(returns) != 1 or returns[0]['eventSequence'] <= row['eventSequence']:
        raise ValueError('Internal notification is outside its parent callback')
    ident = d.get('notificationSequence')
    pair = [x for x in segment if x['kind'] == 'engine_notification'
            and x['data'].get('notificationSequence') == ident]
    if (type(ident) is not int or ident <= 0 or len(pair) != 2
            or [x['data'].get('status') for x in pair] != ['callback_entered', 'callback_returned']
            or pair[0]['eventSequence'] <= parent['eventSequence']
            or pair[-1]['eventSequence'] >= returns[0]['eventSequence']
            or any({k: v for k, v in x['data'].items() if k != 'status'} !=
                   {k: v for k, v in d.items() if k != 'status'} for x in pair)):
        raise ValueError('Internal notification lacks a complete synchronous callback pair')
    return parent['actionSequence']


def historical_parent(row, actions, statuses, segment):
    """Only called after the exact historical source/save checks, never by proximity alone."""
    candidates = []
    for parent in actions.values():
        returns = [x for x in statuses.get(parent['actionSequence'], [])
                   if x['data'].get('status') == 'callback_returned']
        if (parent['data'].get('actionId') == 'proceed'
                and parent['data'].get('source') == CALLBACK_SOURCE and len(returns) == 1
                and parent['eventSequence'] < row['eventSequence'] < returns[0]['eventSequence']):
            inside = [x for x in segment if row['eventSequence'] < x['eventSequence'] < returns[0]['eventSequence']]
            if any(x['kind'] == 'game_action_completed' and x.get('actionSequence') == parent['actionSequence']
                   and x['data'].get('type') == 'VoteToMoveToNextActAction' for x in inside):
                candidates.append(parent['actionSequence'])
    if row['data'].get('source') != ACT_SOURCE or len(candidates) != 1:
        raise ValueError('Historical notification has no unique audited callback parent')
    return candidates[0]


def historical_ui_reason(row):
    d = row['data']; node = d.get('node', '')
    # Reviewed view-only UI callbacks in the hash-bound historical segment.
    suffixes = {
        'NDrawPileButton': '/CombatPileContainer/DrawPile',
        'NBackButton': ('/NCardPileScreen-Draw/BackButton', '/DeckViewScreen/BackButton'),
        'NTopBarPauseButton': '/RightAlignedStuff/PauseButton',
        'NPauseMenuButton': '/ButtonContainer/Resume',
        'NUpgradePreviewTickbox': '/InspectCardScreen/Upgrade',
        'NButton': '/InspectCardScreen/Backstop',
        'NTopBarDeckButton': '/RightAlignedStuff/Deck',
    }
    suffix = suffixes.get(d.get('type'))
    if d.get('source') != CALLBACK_SOURCE or suffix is None or not node.endswith(suffix):
        raise ValueError('Unreviewed historical UI input')
    return 'Source-bound historical view-only UI callback; retained, not an engine input or generic UI support'


def import_raw(raw, seed, start, run_hash, limit, legacy_adapter=None):
    if not 1 <= limit <= 1000:
        raise ValueError('actions must be 1..1000 source action events')
    digest = hashlib.sha256(raw).hexdigest()
    legacy = legacy_adapter is not None
    if legacy and (legacy_adapter != LEGACY_ADAPTER or digest != LEGACY_SOURCE_SHA256
                   or start != 'continue' or run_hash != LEGACY_RUN_SHA256):
        raise ValueError('Historical adapter requires its exact reviewed source and starting save')
    rows = [json.loads(x) for x in raw.splitlines()]
    if not rows or any(not isinstance(r, dict) or r.get('schema') != 'sts2-gui-semantic-v1'
                       or type(r.get('eventSequence')) is not int or not isinstance(r.get('data'), dict)
                       for r in rows):
        raise ValueError('Unsupported Mac recording schema/envelope')
    events = [r['eventSequence'] for r in rows]
    if events != list(range(events[0], events[-1]+1)):
        raise ValueError('Missing, duplicate or reordered raw events')
    initiated = [r for r in rows if r['kind'] == 'action_initiated']
    if len(initiated) < limit:
        raise ValueError('Requested inputs absent')
    selected = initiated[:limit]
    if [r['actionSequence'] for r in selected] != list(range(1, limit+1)):
        raise ValueError('Segment must begin at action 1 without gaps')
    successors = {}
    for r in rows:
        if r['kind'] == 'action_successor' and r.get('actionSequence') in range(1, limit+1):
            if r['actionSequence'] in successors:
                raise ValueError('Duplicate action successor')
            successors[r['actionSequence']] = r
    if len(successors) != limit:
        raise ValueError('Incomplete trace: an input lacks its recorded successor')
    end = max(r['eventSequence'] for r in successors.values())
    first = selected[0]['eventSequence']
    segment = [r for r in rows if first <= r['eventSequence'] <= end]
    if (len({r.get('processRunId') for r in segment}) != 1 or not selected[0].get('processRunId')
            or len({r.get('rootRunId') for r in segment}) != 1 or not selected[0].get('rootRunId')):
        raise ValueError('Process/run identity changed inside segment')
    if [r for r in segment if r['kind'] == 'action_initiated'] != selected:
        raise ValueError('Segment boundary splits an overlapping input')
    actions = {r['actionSequence']: r for r in selected}
    statuses = {seq: [x for x in segment if x['kind'] == 'action_status' and x.get('actionSequence') == seq]
                for seq in actions}
    transformations = []; internal = {}
    for r in selected:
        if r['data'].get('actionId') == 'next_act':
            if not legacy:
                raise ValueError('next_act needs audited parent attribution; old recorder cannot certify it')
            internal[r['actionSequence']] = historical_parent(r, actions, statuses, segment)
    known = {'action_initiated', 'action_status', 'action_successor', 'observation', 'observation_wait',
             'game_action_started', 'game_action_completed', 'nested_choice_accepted', 'nested_action_closed',
             'selection_input_applied', 'room_entered', 'purchase_result', 'engine_notification'}
    for r in segment:
        kind = r['kind']; d = r['data']; seq = r.get('actionSequence')
        reason = None; parent = None
        if d.get('error') or (kind in {'action_status', 'purchase_result'} and d.get('status') in {'failed', 'purchase_rejected'}):
            raise ValueError(f"Failed/rejected event {r['eventSequence']}; no silent replay")
        if kind == 'engine_notification':
            parent = notification_parent(r, actions, statuses, segment)
            reason = 'Synchronous engine Vote notification; executed by parent Proceed, never submitted twice'
        elif legacy and kind == 'unmapped_ui_input':
            reason = historical_ui_reason(r)
        elif legacy and kind == 'unmapped_action' and seq in internal and d.get('actionId') == 'next_act' and d.get('source') == ACT_SOURCE:
            parent = internal[seq]; reason = 'Historical recorder diagnostic for the audited internal Vote notification'
        elif kind not in known:
            raise ValueError('Unsupported raw event kind: ' + kind)
        if kind == 'action_initiated' and seq in internal:
            parent = internal[seq]; reason = 'Source-bound historical synchronous Vote notification; parent executes it once'
        if reason:
            transformations.append(dict(sourceEventSequence=r['eventSequence'], sourceActionSequence=seq,
                                        parentActionSequence=parent, execute=False, reason=reason))
    commands = []
    for r in selected:
        d = r['data']; seq = r['actionSequence']; successor = successors[seq]
        if successor['eventSequence'] <= r['eventSequence']:
            raise ValueError('Successor precedes its input')
        receipts = statuses[seq]
        if not any(x['data'].get('status') in ACCEPTED and r['eventSequence'] < x['eventSequence'] <= successor['eventSequence'] for x in receipts):
            raise ValueError('Missing action acceptance/callback result')
        if seq in internal:
            if successor['data']['observation'] != successors[internal[seq]]['data']['observation']:
                raise ValueError('Historical notification differs from its parent successor')
            continue
        parent = d.get('parentActionSequence')
        if parent is not None:
            if (parent not in actions or parent >= seq or parent in internal
                    or successors[parent]['data'].get('status') != 'entered_nested_decision'
                    or successors[parent]['eventSequence'] >= r['eventSequence']
                    or any(x['kind'] == 'nested_action_closed' and x.get('actionSequence') == parent
                           and x['eventSequence'] <= r['eventSequence'] for x in segment)):
                raise ValueError('Nested input has no open recorded parent decision')
        if successor['data'].get('status') == 'entered_nested_decision':
            if not any(x['kind'] == 'nested_action_closed' and x.get('actionSequence') == seq
                       and x['eventSequence'] > successor['eventSequence'] for x in segment):
                raise ValueError('Nested lifecycle not closed within selected segment')
        if d.get('role', 'player_input' if parent is None else 'nested_input') != ('player_input' if parent is None else 'nested_input'):
            raise ValueError('Unknown or inconsistent input role')
        if d.get('actionId', '').startswith(('claim_relic:', 'deselect_hand:')):
            raise ValueError('Linux binding not implemented: ' + d['actionId'])
        commands.append(dict(kind='submit', actionId=d['actionId'], macActionSequence=seq,
                             macEventSequence=r['eventSequence'], observation=d['observation'],
                             parentActionSequence=parent, role='player_input' if parent is None else 'nested_input',
                             conversionReason='Recorded original input; no inferred action'))
    # Keep exact raw bytes, including excluded prefix/suffix failures and load/menu boundaries.
    # The input source hash identifies those exact bytes; evidence is never provided to a policy.
    result = dict(schema=SCHEMA, seed=seed, start=start, inputRunSha256=run_hash, sourceSha256=digest,
                  sourceProcessRunId=selected[0]['processRunId'], sourceRootRunId=selected[0]['rootRunId'],
                  commands=commands, endpoint=successors[limit]['data']['observation'],
                  sourceEventRange=[first, end], sourceActionCount=limit,
                  sourceRaw=raw.decode('utf-8'), transformations=transformations,
                  adapter=legacy_adapter or 'callback-scope-v1',
                  replayEvidence={'status': 'not_run'},
                  truncatedAfterExplicitCompleteInput=limit < len(initiated),
                  continuity='initial_resume_unverified' if start == 'continue' else 'new_run')
    return validate_bundle(result)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--input', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--seed', required=True)
    p.add_argument('--start', choices=['new', 'continue'], required=True)
    p.add_argument('--current-run', type=Path)
    p.add_argument('--actions', type=int, required=True, help='Source action_initiated count, including legacy internal events')
    p.add_argument('--legacy-adapter', choices=[LEGACY_ADAPTER], help='Explicit source-bound historical adaptation')
    a = p.parse_args()
    if not 1 <= a.actions <= 1000: p.error('actions must be 1..1000')
    if a.start == 'continue' and not a.current_run: p.error('Continue requires --current-run')
    digest = hashlib.sha256(a.current_run.read_bytes()).hexdigest() if a.current_run else None
    b = import_raw(a.input.read_bytes(), a.seed, a.start, digest, a.actions, a.legacy_adapter)
    os.umask(0o077)
    with a.output.open('x') as f: json.dump(b, f, ensure_ascii=False)
    print(json.dumps(dict(inputs=len(b['commands']), output=str(a.output), continuity=b['continuity'])))

if __name__ == '__main__': main()
