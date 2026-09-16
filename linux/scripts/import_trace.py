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
    if not isinstance(b.get('endpoint', {}).get('state'), dict):
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

def import_raw(raw, seed, start, run_hash, limit):
    rows = [json.loads(x) for x in raw.splitlines()]
    if not rows or any(r.get('schema') != 'sts2-gui-semantic-v1' for r in rows):
        raise ValueError('Unsupported Mac recording schema')
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
    end = successors[limit]['eventSequence']
    first = selected[0]['eventSequence']
    segment = [r for r in rows if first <= r['eventSequence'] <= end]
    if len({r['processRunId'] for r in segment}) != 1 or len({r['rootRunId'] for r in segment}) != 1:
        raise ValueError('Process/run identity changed inside segment')
    forbidden = {'continuity_boundary', 'unmapped_action', 'unmapped_ui_input', 'observation_failed', 'action_failed'}
    known = {'action_initiated', 'action_status', 'action_successor', 'observation', 'observation_wait', 'game_action_started', 'game_action_completed', 'nested_choice_accepted', 'nested_action_closed', 'selection_input_applied', 'room_entered', 'purchase_result'}
    for r in segment:
        if r['kind'] not in known: raise ValueError('Unsupported raw event kind: ' + r['kind'])
        if r['kind'] in forbidden or r.get('data', {}).get('error'):
            raise ValueError(f"Explicit incomplete/unknown boundary at event {r['eventSequence']}: {r['kind']}")
    commands = []
    for r in selected:
        d = r['data']
        statuses = [x for x in segment if x['kind'] == 'action_status' and x.get('actionSequence') == r['actionSequence']]
        if not statuses or not any(x['data'].get('status') in {'callback_returned', 'accepted_into_original_queue', 'purchase_completed'} for x in statuses):
            raise ValueError('Missing action acceptance/callback result')
        # Old 0.2.2 next_act lacks reliable parent identity. Reject, never silently skip.
        if d.get('actionId') == 'next_act':
            raise ValueError('next_act needs audited parent attribution; old recorder cannot certify it')
        commands.append(dict(kind='submit', actionId=d['actionId'], macActionSequence=r['actionSequence'], macEventSequence=r['eventSequence'], observation=d['observation']))
    result = dict(schema=SCHEMA, seed=seed, start=start, inputRunSha256=run_hash, sourceSha256=hashlib.sha256(raw).hexdigest(), sourceProcessRunId=selected[0]['processRunId'], sourceRootRunId=selected[0]['rootRunId'], commands=commands, endpoint=successors[limit]['data']['observation'], sourceEventRange=[events[0], end], truncatedAfterExplicitCompleteInput=limit < len(initiated), continuity='initial_resume_unverified' if start == 'continue' else 'new_run')
    return validate_bundle(result)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--input', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--seed', required=True)
    p.add_argument('--start', choices=['new', 'continue'], required=True)
    p.add_argument('--current-run', type=Path)
    p.add_argument('--actions', type=int, required=True)
    a = p.parse_args()
    if not 1 <= a.actions <= 1000: p.error('actions must be 1..1000')
    if a.start == 'continue' and not a.current_run: p.error('Continue requires --current-run')
    digest = hashlib.sha256(a.current_run.read_bytes()).hexdigest() if a.current_run else None
    b = import_raw(a.input.read_bytes(), a.seed, a.start, digest, a.actions)
    os.umask(0o077)
    with a.output.open('x') as f: json.dump(b, f, ensure_ascii=False)
    print(json.dumps(dict(inputs=len(b['commands']), output=str(a.output), continuity=b['continuity'])))

if __name__ == '__main__': main()
