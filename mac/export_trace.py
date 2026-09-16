#!/usr/bin/env python3
"""Lossless local export. Never infer missing actions or certify fidelity."""
from pathlib import Path
import argparse,json,hashlib,datetime,shutil
ROOT=Path(__file__).resolve().parent;P=ROOT/'.private'
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
 if debug_mode:problems.append('debug console/999-block fixture: gameplay changed; never certify as ordinary continuous replay')
 raw=session/'actions.jsonl'
 if not raw.exists():problems.append('recorder did not initialize')
 else:
  captured=raw.read_bytes()
  (output/'actions.raw.jsonl').write_bytes(captured)
  for n,line in enumerate(captured.splitlines(),1):
   try:rows.append(json.loads(line))
   except ValueError:problems.append(f'truncated/invalid JSON line {n}')
 expected=1;actions={};successors={};statuses={};roots=set();nested_closed=set();nested_results=[]
 for row in rows:
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
 if len(roots)>1:problems.append('multiple run segments; never concatenate for strict replay')
 if not actions:problems.append('no human run actions recorded yet')
 if not any(r['kind']=='ready' for r in rows) or not any(r['kind']=='run_started' for r in rows):problems.append('missing recorder readiness or run provenance')
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
  if not obs or d['actionId'] not in [a['id'] for a in obs['actions']]:problems.append(f'action {seq} unavailable in recorded observation');continue
  if d['actionId'].startswith(('claim_relic:','deselect_hand:','next_act')):adapters.append({'sourceActionSequence':seq,'actionId':d['actionId']})
  out.append({'kind':'submit','utc':row['utc'],'actionId':d['actionId'],'controller':'computer_use_debug' if debug_mode else 'human_gui','reason':'recorded original callback; see raw status lifecycle','sourceActionSequence':seq,'sourceRootRunId':row['rootRunId'],'observation':{'processRunId':row['processRunId'],'decisionId':row['decisionId'],**obs}})
 # Proposed commands stay explicitly non-certifying; consumers must read conversion.json first.
 (output/'commands.proposed.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in out))
 (output/'successors.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in successors.values()))
 (output/'rejected-attempts.jsonl').write_text(''.join(json.dumps(r,ensure_ascii=False)+'\n' for r in rejected))
 for name in ['launch.json','loaded-files.json','profile-before-launch.json','snapshot.json','replay-input.private.json','launcher-exit.json','RECORDER-FAILED.txt','debug-interventions.private.jsonl','native-input-actions.private.jsonl','debug-validation.private.json','debug-console-receipts.private.json']:
  if (session/name).exists():shutil.copyfile(session/name,output/name)
 for name in ['profile-input-manifest.json','harmony-path-patch.json','game-manifest.json']:
  if (P/name).exists():shutil.copyfile(P/name,output/name)
 report={'schema':'sts2-gui-conversion-v1','source':str(session),'actions':len(actions),'proposedCommands':len(out),'roots':sorted(roots),'issues':problems,'structuralReady':bool(actions) and not problems,'recordingMode':'debug_console' if debug_mode else 'normal','crossPlatformFidelity':'not applicable to unmodified gameplay' if debug_mode else 'not tested','requiredLinuxAdaptation':['Use same original progression hashes and Standard Silent A0; seed is in private replay input.','Replay commands.proposed.jsonl using the existing action interface; stop on the first unmatched observation or unavailable action.','Current Linux replay ending expects Linux runtime.json and terminal.json. Add GUI trace end/successor validation; never use --mode auto to fill a missing suffix.','Only the existing initial Neow greeting normalization is allowed. All other observations, options, rewards and state differences remain failures.','next_act and GUI potion-menu/treasure variants may require Linux action bindings; retain unsupported input as an explicit stop.']}
 observation_v2=any(r['kind']=='recorder_initialized' and r.get('data',{}).get('observationRevision')=='card-state-v2' for r in rows)
 if observation_v2:
  report['requiredObservationBindings']=['cardDetails.state: affliction and enchantment ids/amounts/display amounts, current upgrade, dynamic keywords, Wither visible scaling', 'status.deckCards and hand/choice/result cardDetails: map recorder_process instance ids bijectively to Linux card objects using actual actions and positions; never match by model id alone', 'visible powers: Surrounded facing, Flanking applierCombatId, Sandpit targetCombatId; map creature identities', 'cardDetails.rendered: displayed description/title/lock state. Classify only proven display differences; do not drop mechanical numbers or effects.']
  report['structuralReady']=False
  report['issues'].append('card-state-v2 observation mapping has not been validated on Linux; do not strip the new fields to obtain agreement')
 report.update({'rejectedAttempts':len(rejected),'requiredActionBindings':adapters,'capturedEvents':len(rows),'lastEventSequence':rows[-1]['eventSequence'] if rows else None})
 if adapters:
  report['structuralReady']=False
  report['issues'].append('Recorded actions require explicit Linux bindings; see requiredActionBindings')
 (output/'conversion.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
 (output/'README.txt').write_text('PRIVATE — keep on the owner Mac / approved Linux project.\nRead conversion.json before replay. commands.proposed.jsonl is NOT proof of accepted/complete/fidelity-passing gameplay. Raw failures and boundaries are preserved. GUI pre-observations extend the Linux state/actions projection; card-state-v2 requires explicit mapping and comparison of card effects and local identities. Per-action successors are separate; no checkpoint-guided inference.\n')
 (output/'SHA256SUMS').write_text(''.join(hashlib.sha256(f.read_bytes()).hexdigest()+'  '+f.name+'\n' for f in sorted(output.iterdir()) if f.is_file()))
 return report
if __name__=='__main__':
 a=argparse.ArgumentParser(description=__doc__);a.add_argument('--session',type=Path);a.add_argument('--output',type=Path);args=a.parse_args()
 session=args.session or Path((P/'latest-session.txt').read_text().strip())
 output=args.output or P/'exports'/datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
 result=export(session,output);print(json.dumps({'output':str(output),**result},ensure_ascii=False,indent=2))
