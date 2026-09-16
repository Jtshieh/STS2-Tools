#!/usr/bin/env python3
"""Export explicit action lifecycle from a private local session. Output stays private."""
import argparse
import json
import os
from pathlib import Path
from protocol import SCHEMA, validate

def export(launch, output):
    lineage = json.loads((launch / 'lineage.json').read_text())
    runtime = json.loads((launch / 'runtime.json').read_text()) if (launch / 'runtime.json').exists() else None
    evidence = Path(runtime['evidenceRoot']) if runtime else None
    rows = []
    trajectory = evidence / 'runtime-work/trajectory.jsonl' if evidence else None
    if trajectory and trajectory.exists():
        rows = [json.loads(x) for x in trajectory.read_text().splitlines()]
    process = lineage['processRunId']
    # Engine process id is authoritative when it reached observations.
    for r in rows:
        if r['kind'] == 'observation':
            process = r['value']['processRunId']; break
    base = dict(schema=SCHEMA, rootRunId=lineage['rootRun'], processRunId=process, slAttempt=0, recovery=None)
    accepted = {}
    records = []
    def emit(seq, status, action=None, observation=None, error=None):
        records.append(validate(dict(base, sequence=seq, status=status, action=action, observation=observation, error=error)))
    for r in rows:
        d = r['value']; kind = r['kind']
        if kind == 'action_submitted':
            seq = d['sequence']; obs = d['pre']
            matches = [a for a in obs['actions'] if a['id'] == d['actionId']]
            if len(matches) != 1:
                raise ValueError('Accepted action has no unique observed identity')
            action = dict(matches[0], decisionId=d['decisionId'])
            accepted[seq] = action
            emit(seq, 'accepted', action, obs)
        elif kind == 'action_delivered':
            emit(d['sequence'], 'delivered', accepted[d['sequence']])
        elif kind == 'action_response':
            emit(d['sequence'], 'completed', accepted[d['sequence']], d['successor'])
        elif kind == 'action_rejected':
            emit(d['submittedCount'], 'rejected', d['request'], error=d['reason'])
    # Initiations are submitted transport requests, distinct from engine acceptance.
    controller = launch / 'controller.jsonl'
    if controller.exists():
        seq = 0
        for r in map(json.loads, controller.read_text().splitlines()):
            if r['kind'] != 'submit': continue
            seq += 1
            matches = [a for a in r['observation']['actions'] if a['id'] == r['actionId']]
            action = dict(matches[0], decisionId=r['observation']['decisionId']) if len(matches) == 1 else dict(id=r['actionId'], decisionId=r['observation']['decisionId'])
            emit(seq, 'initiated', action, r['observation'])
    outcome = json.loads((launch / 'exit.json').read_text())
    terminal_path = evidence / 'runtime-work/bridge/terminal.json' if evidence else None
    terminal = json.loads(terminal_path.read_text()) if terminal_path and terminal_path.exists() else None
    if outcome['exitCode'] != 0:
        emit(len(accepted), 'failed', error=outcome.get('failure') or 'native_process_failed')
    if not terminal or terminal.get('outcome') == 'budget_truncated':
        emit(len(accepted), 'interrupted', observation=terminal.get('state') if terminal else None, error='budget_truncated' if terminal else 'continuity_unverified')
    os.umask(0o077)
    order = {name:i for i,name in enumerate(['initiated','accepted','delivered','completed','rejected','failed','interrupted'])}
    records.sort(key=lambda x:(x['sequence'],order[x['status']]))
    with output.open('x') as f:
        for r in records:
            f.write(json.dumps(r, ensure_ascii=False) + '\n')
    return len(records)

def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--launch', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    print(json.dumps({'records': export(a.launch, a.output)}))

if __name__ == '__main__': main()
