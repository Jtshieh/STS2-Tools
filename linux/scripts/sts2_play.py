#!/usr/bin/env python3
"""Bounded local STS2 interactive/automatic production session. Private materials required."""
import argparse,datetime,fcntl,hashlib,json,os,select,signal,subprocess,sys,time,uuid,resource,re
from pathlib import Path
ROOT=Path(__file__).absolute().parents[1]; PRIVATE=ROOT/'.private'
def policy_limits():
    resource.setrlimit(resource.RLIMIT_AS,(256*1024**2,256*1024**2));resource.setrlimit(resource.RLIMIT_CPU,(90,91))
def atomic(path,obj):
    tmp=path.with_suffix('.tmp');tmp.write_text(json.dumps(obj,ensure_ascii=False));tmp.replace(path)
def append(path,obj):
    with path.open('a') as f:f.write(json.dumps(obj,ensure_ascii=False)+'\n')
def normalized(o,exact_presentation=False):
    # Comparison-only projection. Never alter the observation delivered to a controller.
    value=json.loads(json.dumps({'state':o['state'],'actions':o['actions']}))
    state=value['state'];actions=value['actions']
    if (not exact_presentation and state.get('phase')=='event'
        and state.get('status',{}).get('act')==1 and state.get('status',{}).get('floor')==1
        and actions and all((a.get('detail') or {}).get('textKey','').startswith('NEOW.pages.INITIAL.options.') for a in actions)):
        # Audited original NAncientEventLayout greeting; keep every option/result field.
        state['text']=re.sub(r'^\[sine\][^\[]*\[/sine\](?= )','<cosmetic-neow-greeting>',state.get('text',''),count=1)
    return value
def manual_action(obs,proc):
    print(json.dumps(obs['state'],ensure_ascii=False,indent=2),flush=True)
    for i,a in enumerate(obs['actions'],1):print(f"  {i}: {a['label']} ({a['id']})",flush=True)
    print('等待人工输入动作编号（900秒上限）：',flush=True);deadline=time.monotonic()+900
    while True:
        if proc.poll() is not None:raise RuntimeError('game stopped while awaiting human')
        if time.monotonic()>deadline:raise TimeoutError('human_wait_budget_truncated')
        if not select.select([sys.stdin],[],[],0.2)[0]:continue
        line=sys.stdin.readline()
        if not line:raise EOFError('human input closed')
        try:idx=int(line)-1;assert 0<=idx<len(obs['actions'])
        except (ValueError,AssertionError):print('请输入所列动作编号',flush=True);continue
        return obs['actions'][idx]['id']

def main():
    from runtime import configured, stage, command
    import compare_gui
    from export_log import export
    from work_materials import finish_materials
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--config',type=Path,required=True)
    p.add_argument('--mode',choices=['manual','auto','replay'],default='manual')
    p.add_argument('--seed',required=True)
    p.add_argument('--decisions',type=int,default=30)
    p.add_argument('--root-run',default=None)
    p.add_argument('--tutorials',choices=['yes','no'],required=True)
    p.add_argument('--checkpoint',type=Path)
    p.add_argument('--trace',type=Path,help='Versioned complete replay bundle from import_trace.py')
    p.add_argument('--allow-timing-differences',action='store_true',help='Mechanical comparison only; retain every timing mismatch')
    args=p.parse_args()
    if not 1<=args.decisions<=1000:p.error('decisions must be 1..1000')
    if args.mode=='replay' and not args.trace:p.error('replay requires --trace')
    if args.mode!='replay' and args.trace:p.error('--trace only applies to replay')
    os.umask(0o077)
    config,root,private=configured(args.config)
    lock=(private/'runtime.lock').open('a');fcntl.flock(lock,fcntl.LOCK_EX|fcntl.LOCK_NB)
    source=stage(config,private)
    evidence_before=set((private/'evidence').glob('vm-*'))
    process_id=uuid.uuid4().hex
    root_run=args.root_run or process_id
    receipt=private/('session-'+datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'-'+process_id[:8]);receipt.mkdir(mode=0o700)
    trace=json.loads(args.trace.read_text()) if args.trace else None
    if trace:
        from import_trace import validate_bundle
        validate_bundle(trace)
        if len(trace['commands'])!=args.decisions:raise ValueError('Decision bound must equal complete trace length; export an explicit shorter segment')
        if trace['seed']!=args.seed:raise ValueError('Trace seed differs from explicit seed')
        if trace['start']!=config['start']:raise ValueError('Trace initial new/continue boundary differs')
        if config['start']=='continue':
            current=Path(config['source'])/'current_run.save'
            if hashlib.sha256(current.read_bytes()).hexdigest()!=trace['inputRunSha256']:raise ValueError('Trace initial current-run hash differs')
    argv=command(config,root,source,args.seed,root_run,args.decisions,args.tutorials)
    atomic(receipt/'lineage.json',dict(argv=argv,rootRun=root_run,processRunId=process_id,mode=args.mode,seed=args.seed,slAttempt=0,recovery=None,start=config['start'],continuity='resumed_segment_unverified' if config['start']=='continue' else 'new_run',version='v0.111.0',build=24724944,architecture='x86_64',decisionLimit=args.decisions))
    print(json.dumps({'launchEvidence':str(receipt)}),flush=True)
    worker=proc=None;failure=None;evidence=None;seen=set();index=0;start=time.monotonic();comparisons=[]
    def compare(expected,actual):
        result=compare_gui.compare(normalized(expected),normalized(actual))
        comparisons.append(result)
        append(receipt/'comparison.jsonl',result)
        if not result['matched'] or (not result['strictMatched'] and not args.allow_timing_differences):
            raise ValueError('replay observation/timing divergence; see comparison.jsonl')
        return result
    try:
        if args.mode=='auto':
            policy=receipt/'policy-source.py';policy.write_bytes((ROOT/'scripts/sts2_policy.py').read_bytes())
            cmd=['/usr/bin/bwrap','--unshare-all','--die-with-parent','--new-session','--clearenv','--setenv','PATH','/usr/bin','--ro-bind','/usr','/usr','--symlink','usr/bin','/bin','--symlink','usr/lib','/lib','--symlink','usr/lib64','/lib64','--proc','/proc','--dev','/dev','--tmpfs','/tmp','--ro-bind',str(policy),'/policy.py']
            if args.checkpoint:
                checkpoint=receipt/'checkpoint.json';checkpoint.write_bytes(args.checkpoint.read_bytes())
                model=receipt/'model.py';model.write_bytes((ROOT/'scripts/sts2_small_policy.py').read_bytes())
                cmd+=['--ro-bind',str(checkpoint),'/checkpoint.json','--ro-bind',str(model),'/model-code.py']
            cmd+=['--chdir','/tmp','/usr/bin/python3','-I','-B','/policy.py']
            atomic(receipt/'policy-isolation.json',dict(argv=cmd,input='player observation only; no game/profile/logs mounted'))
            worker=subprocess.Popen(cmd,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=(receipt/'policy-stderr.log').open('w'),text=True,bufsize=1,preexec_fn=policy_limits)
        with (receipt/'launcher.log').open('w') as output:
            proc=subprocess.Popen(argv,stdout=output,stderr=subprocess.STDOUT)
            def interrupt(sig,frame):raise KeyboardInterrupt('signal '+str(sig))
            signal.signal(signal.SIGTERM,interrupt);signal.signal(signal.SIGINT,interrupt)
            while proc.poll() is None:
                if time.monotonic()-start>1500:raise TimeoutError('whole session wall budget')
                if evidence is None:
                    for line in (receipt/'launcher.log').read_text().splitlines():
                        try:r=json.loads(line)
                        except ValueError:continue
                        if 'evidenceRoot' in r:
                            evidence=Path(r['evidenceRoot']);atomic(receipt/'runtime.json',{'evidenceRoot':str(evidence)});break
                if evidence:
                    bridge=evidence/'runtime-work/bridge';path=bridge/'observation.json'
                    if path.exists():
                        obs=json.loads(path.read_text());ident=(obs['processRunId'],obs['decisionId'])
                        if ident not in seen:
                            seen.add(ident)
                            if (bridge/'terminal.json').exists():continue
                            append(receipt/'controller.jsonl',dict(kind='observation',observation=obs))
                            if trace:
                                if index>=len(trace['commands']):raise ValueError('Missing recorded input; no inferred continuation')
                                old=trace['commands'][index];comparison=compare(old['observation'],obs)
                                aid=comparison['actionIdMap'].get(old['actionId'],old['actionId'])
                            elif args.mode=='manual':aid=manual_action(obs,proc)
                            else:
                                worker.stdin.write(json.dumps(obs)+'\n');worker.stdin.flush()
                                if not select.select([worker.stdout],[],[],10)[0]:raise TimeoutError('policy timeout')
                                reply=json.loads(worker.stdout.readline())
                                if 'error' in reply:raise ValueError('policy unsupported: '+reply['error'])
                                aid=reply['actionId']
                            if aid not in [a['id'] for a in obs['actions']]:raise ValueError('Action not currently legal: '+aid)
                            request=dict(processRunId=obs['processRunId'],decisionId=obs['decisionId'],actionId=aid)
                            append(receipt/'controller.jsonl',dict(kind='submit',observation=obs,actionId=aid,sequence=index+1))
                            atomic(bridge/'action.json',request)
                            print(f"{index+1}: {obs['state']['phase']} → {aid}",flush=True)
                            # No silent re-delivery. A stale/illegal request interrupts this segment.
                            deadline=time.monotonic()+8
                            while True:
                                heartbeat=json.loads((bridge/'heartbeat.json').read_text())
                                if heartbeat['submittedCount']>=index+1:break
                                reject=bridge/'rejection.json'
                                if reject.exists() and json.loads(reject.read_text()).get('request')==request:raise ValueError('action_rejected; recorded input not consumed')
                                if proc.poll() is not None or time.monotonic()>deadline:raise TimeoutError('action acceptance unconfirmed')
                                time.sleep(.05)
                            index+=1
                time.sleep(.1)
            result=proc.wait()
            if result:raise RuntimeError(f'Native supervisor exited {result}; inspect private launcher.log')
            terminal=json.loads((evidence/'runtime-work/bridge/terminal.json').read_text())
            if trace:
                compare(dict(state=trace['endpoint']['state'],actions=[]),dict(state=terminal['state'],actions=[]))
                if index!=len(trace['commands']):raise ValueError('Trace not fully consumed')
                atomic(receipt/'replay-result.json',dict(mechanicalMatched=True,strictDecisionTimingMatched=all(c['strictMatched'] for c in comparisons),inputs=index,scope='bounded recorded segment; no whole-run fidelity or restart continuity claim'))
    except BaseException as e:
        failure=repr(e);result=1
        if proc is not None and proc.poll() is None:
            proc.send_signal(signal.SIGINT)
            try:proc.wait(timeout=15)
            except subprocess.TimeoutExpired:proc.terminate();proc.wait(timeout=15)
    finally:
        if worker:
            worker.stdin.close()
            try:worker.wait(timeout=2)
            except subprocess.TimeoutExpired:worker.terminate();worker.wait(timeout=3)
    atomic(receipt/'exit.json',dict(exitCode=result,failure=failure,evidenceRoot=str(evidence) if evidence else None,acceptedInputs=index,elapsedSeconds=time.monotonic()-start))
    try:export(receipt,receipt/'action-log.jsonl')
    except Exception as error:
        atomic(receipt/'export-failure.json',dict(error=repr(error)));result=1
    # Only after native supervisor/policy exit, protection checks and log export.
    # Missing/failed exit checks retain work, regardless of gameplay outcome.
    finish_materials(config,private,evidence_before)
    print(json.dumps(dict(exitCode=result,failure=failure,launchEvidence=str(receipt))),flush=True)
    return result
if __name__=='__main__':sys.exit(main())
