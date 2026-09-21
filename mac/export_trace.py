#!/usr/bin/env python3
"""Lossless local export. Never infer missing actions or certify fidelity."""
from pathlib import Path
import argparse,json,hashlib,datetime,shutil
ROOT=Path(__file__).resolve().parent;P=ROOT/'.private'
# These are capture events, including outcomes that must never become extra inputs.
KNOWN_EVENTS = {
 'recorder_initialized', 'hook_registered', 'ready', 'run_started', 'process_exit',
 'menu_input', 'observation', 'observation_wait', 'action_initiated', 'action_status',
 'action_successor', 'nested_choice_accepted', 'nested_action_closed',
 'selection_input_applied', 'room_entered', 'purchase_result', 'game_action_started',
 'game_action_completed', 'engine_notification', 'unavailable_attempt', 'continuity_boundary',
 'unmapped_action', 'unmapped_ui_input', 'recording_error', 'observer_error',
 'observer_error_at_input', 'hook_error', 'unmatched_purchase_outcome',
 'unmatched_semantic_callback',
}

def action_binding_review(seq, action_id):
 # Review requirements for the current Linux adapters, not capture corruption.
 if action_id == 'next_act':
  reason='The generic importer rejects next_act; establish parent/input attribution before replay.'
 elif action_id.startswith('claim_relic:'):
  reason='The current Linux bridge has no direct claim_relic action; review the original reward callback mapping.'
 elif action_id.startswith('deselect_hand:'):
  reason='The current Linux bridge exposes select_hand; review the deselection callback and card identity mapping.'
 else:
  return None
 return {'sourceActionSequence':seq,'actionId':action_id,'reason':reason}

def notification_issues(rows, actions):
 issues=[];groups={}
 rows=[r for r in rows if isinstance(r,dict) and isinstance(r.get('data'),dict) and type(r.get('eventSequence')) is int]
 for row in rows:
  if row.get('kind')=='engine_notification':
   ident=row['data'].get('notificationSequence')
   if type(ident) is not int or ident<=0:
    issues.append('invalid internal notification sequence');continue
   groups.setdefault(ident,[]).append(row)
 for ident,pair in groups.items():
  d=pair[0]['data'];parent_seq=d.get('parentActionSequence')
  parent=actions.get(parent_seq) if type(parent_seq) is int else None
  returns=[r for r in rows if r.get('kind')=='action_status' and parent
           and r.get('actionSequence')==parent['actionSequence'] and r['data'].get('status')=='callback_returned']
  if (type(ident) is not int or ident<=0 or len(pair)!=2
      or [r['data'].get('status') for r in pair]!=['callback_entered','callback_returned']
      or d.get('actionId')!='next_act' or d.get('role')!='engine_notification'
      or d.get('source')!='RunReplays.ActChangeSynchronizer.SetLocalPlayerReady'
      or d.get('attributionRevision')!='callback-scope-v1' or d.get('relation')!='synchronous_callback'
      or not parent or parent['data'].get('actionId')!='proceed'
      or parent['data'].get('source')!='NClickableControl.OnReleaseHandler'
      or len(returns)!=1 or not parent['eventSequence']<pair[0]['eventSequence']<pair[-1]['eventSequence']<returns[0]['eventSequence']
      or any(r.get('actionSequence') is not None or r.get('rootRunId')!=parent.get('rootRunId')
             or {k:v for k,v in r['data'].items() if k!='status'}!={k:v for k,v in d.items() if k!='status'} for r in pair)):
   issues.append(f'internal notification {ident} lacks a complete synchronous Proceed parent')
 return issues

def _has_empty_hand_confirmation(choice,candidates,statuses):
 # A real zero-card result plus its delivered confirmation is an input, not a missing click.
 d=choice['data'];selected=d.get('selected',{})
 keys=('_canonicalCards','_combatCards','_deckCards','_mutableCards','indexes')
 if d.get('type')!='CombatCard' or not all(selected.get(k)==[] for k in keys):return False
 for action in candidates:
  a=action['data'];obs=a.get('observation') or {};available=obs.get('actions',[])
  if (a.get('actionId')=='confirm_selection'
      and action['actionSequence'] in d.get('related',[])
      and a.get('parentActionSequence')==d.get('parentActionSequence')
      and obs.get('state',{}).get('phase')=='hand_selection'
      and any(x['id']=='confirm_selection' for x in available)
      and not any(x['id'].startswith('deselect_hand:') for x in available)
      and 'callback_returned' in statuses.get(action['actionSequence'],[])):
   return True
 return False

def export(session,output):
 output.mkdir(parents=True,exist_ok=False,mode=0o700)
 launch=json.loads((session/'launch.json').read_text()) if (session/'launch.json').exists() else {}
 debug_mode=launch.get('recordingMode')=='debug_console' or (session/'debug-interventions.private.jsonl').exists()
 rows=[];problems=[]
 raw=session/'actions.jsonl'
 if not raw.exists():problems.append('recorder did not initialize')
 else:
  captured=raw.read_bytes()
  (output/'actions.raw.jsonl').write_bytes(captured)
  for n,line in enumerate(captured.splitlines(),1):
   try:rows.append(json.loads(line))
   except ValueError:problems.append(f'truncated/invalid JSON line {n}')
 expected=1;actions={};successors={};statuses={};roots=set();processes=set();nested_closed=set();nested_results=[]
 for row in rows:
  if (not isinstance(row,dict) or row.get('schema')!='sts2-gui-semantic-v1'
      or type(row.get('eventSequence')) is not int or not isinstance(row.get('kind'),str)
      or not isinstance(row.get('data'),dict) or not row.get('processRunId')):
   problems.append('invalid semantic event envelope');continue
  processes.add(row['processRunId'])
  if row['kind'] not in KNOWN_EVENTS:problems.append(f'unknown event kind {row["kind"]} at event {row["eventSequence"]}')
  if row['data'].get('error'):problems.append(f'event error at event {row["eventSequence"]}')
  if row['eventSequence']!=expected:problems.append(f'event sequence gap at {expected}')
  expected=row['eventSequence']+1
  kind=row['kind'];seq=row.get('actionSequence')
  if row.get('rootRunId'):roots.add(row['rootRunId'])
  if kind=='action_initiated':
   if seq in actions:problems.append(f'duplicate action {seq}')
   actions[seq]=row
  elif kind=='action_successor':
   if seq in successors:problems.append(f'duplicate successor for action {seq}')
   successors[seq]=row
  elif kind=='action_status':statuses.setdefault(seq,[]).append(row['data']['status'])
  elif kind=='nested_action_closed':nested_closed.add(seq)
  elif kind=='nested_choice_accepted':nested_results.append(row)
  if kind in ['unmapped_action','recording_error','observer_error','observer_error_at_input','hook_error','unmatched_purchase_outcome','unmatched_semantic_callback']:problems.append(f'{kind} at event {row["eventSequence"]}')
  if kind=='unmapped_ui_input':problems.append(f'unclassified UI input at event {row["eventSequence"]}; review before strict replay')
  if kind=='continuity_boundary':problems.append('continuity boundary: '+row['data']['reason'])
 if len(processes)>1:problems.append('multiple process identities in one recording')
 if len(roots)>1:problems.append('multiple run segments; never concatenate for strict replay')
 if not actions:problems.append('no human run actions recorded yet')
 if not any(isinstance(r,dict) and r.get('kind')=='ready' for r in rows) or not any(isinstance(r,dict) and r.get('kind')=='run_started' for r in rows):problems.append('missing recorder readiness or run provenance')
 problems.extend(notification_issues(rows,actions))
 out=[];rejected=[];adapters=[]
 if list(actions)!=list(range(1,len(actions)+1)):problems.append('action sequence gap or reordered actions')
 previous_choice_event=0
 for choice in nested_results:
  # A finalized result is evidence, never permission to fabricate missing inputs.
  candidates=[a for a in actions.values() if previous_choice_event<a['eventSequence']<choice['eventSequence']]
  choice_type=choice['data']['type']
  prefixes=('select_hand:',) if choice_type=='CombatCard' else ('select_grid:',) if choice_type=='DeckCard' else ('reward_card:','choose_card:','skip_card','skip_choice','reward_alternative:') if choice_type=='Index' else ()
  if prefixes and not any(a['data']['actionId'].startswith(prefixes) for a in candidates) and not _has_empty_hand_confirmation(choice,candidates,statuses):problems.append(f'choice result at event {choice["eventSequence"]} lacks recorded selection input')
  previous_choice_event=choice['eventSequence']
 for seq,row in actions.items():
  d=row['data'];obs=d.get('observation');states=statuses.get(seq,[])
  if 'failed' in states:problems.append(f'action {seq} failed')
  if seq not in successors:problems.append(f'action {seq} missing next decision observation')
  elif successors[seq]['data']['status']=='entered_nested_decision' and seq not in nested_closed:problems.append(f'action {seq} nested lifecycle not closed in this segment')
  if not any(x in states for x in ['accepted_into_original_queue','callback_returned','selection_input_applied','accepted_original_selection']):problems.append(f'action {seq} lacks delivery evidence')
  if 'purchase_rejected' in states:
   after=successors.get(seq,{}).get('data',{}).get('observation')
   unchanged=bool(obs and after and obs==after)
   rejected.append({'sourceActionSequence':seq,'actionId':d['actionId'],'statuses':states,'unchangedObservation':unchanged,'original':row})
   if not unchanged:problems.append(f'rejected purchase {seq} lacks an identical next observation; manual review required')
   continue
  binding=action_binding_review(seq,d['actionId'])
  if binding:adapters.append(binding)
  if not obs or d['actionId'] not in [a['id'] for a in obs['actions']]:problems.append(f'action {seq} unavailable in recorded observation');continue
  out.append({'kind':'submit','utc':row['utc'],'actionId':d['actionId'],'controller':'computer_use_debug' if debug_mode else 'human_gui','reason':'recorded original callback; see raw status lifecycle','sourceActionSequence':seq,'sourceEventSequence':row['eventSequence'],'parentActionSequence':d.get('parentActionSequence'),'role':d.get('role','nested_input' if d.get('parentActionSequence') is not None else 'player_input'),'sourceRootRunId':row['rootRunId'],'observation':{'processRunId':row['processRunId'],'decisionId':row['decisionId'],**obs}})
 # Proposed commands stay explicitly non-certifying; consumers must read conversion.json first.
 (output/'commands.proposed.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in out))
 (output/'successors.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in successors.values()))
 (output/'rejected-attempts.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in rejected))
 for name in ['launch.json','loaded-files.json','profile-before-launch.json','snapshot.json','replay-input.private.json','launcher-exit.json','RECORDER-FAILED.txt','debug-interventions.private.jsonl','native-input-actions.private.jsonl','debug-validation.private.json','debug-console-receipts.private.json']:
  if (session/name).exists():shutil.copyfile(session/name,output/name)
 for name in ['profile-input-manifest.json','harmony-path-patch.json','game-manifest.json']:
  if (P/name).exists():shutil.copyfile(P/name,output/name)
 revisions=sorted({r['data']['observationRevision'] for r in rows
   if isinstance(r,dict) and r.get('kind')=='recorder_initialized'
   and isinstance(r.get('data'),dict) and isinstance(r['data'].get('observationRevision'),str)})
 report={
  'schema':'sts2-gui-conversion-v2','source':str(session),
  'actions':len(actions),'proposedCommands':len(out),'roots':sorted(roots),
  'issues':problems,'structuralReady':bool(actions) and not problems,
  'recordingMode':'debug_console' if debug_mode else 'normal',
  'crossPlatformFidelity':'not applicable to unmodified gameplay' if debug_mode else 'not tested',
  'requiredActionBindings':adapters,
  'requiredLinuxAdaptation':[a['reason'] for a in adapters],
  'observationComparison':{
   'revisions':revisions,'status':'not_run',
   'comparator':'linux/scripts/compare_gui.py',
   'instruction':'Compare the recorded fields and card identities with Linux observations using the current comparator.'},
  'linuxReplay':{
   'status':'not_applicable' if debug_mode else 'not_run',
   'reason':'Debug interventions changed gameplay.' if debug_mode else 'Export does not execute Linux import or replay.',
   'input':'actions.raw.jsonl','importer':'linux/scripts/import_trace.py',
   'runner':'linux/scripts/sts2_play.py'},
  'replayInstructions':[
   'Prepare matching game/profile inputs and the recorded seed; Continue requires the exact starting run save.',
   'Import actions.raw.jsonl with linux/scripts/import_trace.py, then use sts2_play.py --mode replay.',
   'The Linux runner compares decision observations and the recorded endpoint. Read its replay-result.json for mechanical and decision-timing results.',
   'Use the current comparator and protocol for display normalization; preserve mechanical state, rewards, options and card effects.'],
  'rejectedAttempts':len(rejected),'capturedEvents':len(rows),
  'lastEventSequence':rows[-1].get('eventSequence') if rows and isinstance(rows[-1],dict) else None,
 }
 (output/'conversion.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
 (output/'README.txt').write_text(
  'Read conversion.json before processing this private export.\n'
  'sts2-gui-conversion-v2: structuralReady describes capture completeness; issues lists capture/continuity problems.\n'
  'requiredActionBindings lists adapter reviews separately. observationComparison and linuxReplay record that export has not run comparison/replay.\n'
  'Import actions.raw.jsonl with linux/scripts/import_trace.py. The Linux replay runner compares decisions and the endpoint.\n'
  'commands.proposed.jsonl contains preprocessing candidates. Original events, failed attempts and boundaries remain in the export.\n')
 (output/'SHA256SUMS').write_text(''.join(hashlib.sha256(f.read_bytes()).hexdigest()+'  '+f.name+'\n' for f in sorted(output.iterdir()) if f.is_file()))
 return report
if __name__=='__main__':
 a=argparse.ArgumentParser(description=__doc__);a.add_argument('--session',type=Path);a.add_argument('--output',type=Path);args=a.parse_args()
 session=args.session or Path((P/'latest-session.txt').read_text().strip())
 output=args.output or P/'exports'/datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
 result=export(session,output);print(json.dumps({'output':str(output),**result},ensure_ascii=False,indent=2))
