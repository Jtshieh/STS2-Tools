#!/usr/bin/env python3
"""Run bounded local policy update in isolation using validated player-only samples."""
import argparse,hashlib,json,os,subprocess,sys,time,resource
from pathlib import Path
ROOT=Path(__file__).absolute().parents[1]
def main():
    p=argparse.ArgumentParser(description=__doc__);sub=p.add_subparsers(dest='command',required=True)
    collect=sub.add_parser('collect');collect.add_argument('--launch',type=Path,required=True);collect.add_argument('--gate',type=Path,required=True);collect.add_argument('--output',type=Path,required=True)
    for name in ['update','resume']:
        q=sub.add_parser(name);q.add_argument('--data',type=Path,required=True);q.add_argument('--output',type=Path,required=True);q.add_argument('--updates',type=int,default=4)
        if name=='resume':q.add_argument('--checkpoint',type=Path,required=True)
    a=p.parse_args();os.umask(0o077)
    if a.command=='collect':
        gate=json.loads(a.gate.read_text());assert gate['trainingCombatPathApproved'] is True
        runtime=json.loads((a.launch/'runtime.json').read_text());evidence=Path(runtime['evidenceRoot']);assert hashlib.sha256((evidence/'input-source/Bridge.cs').read_bytes()).hexdigest() in gate['approvedBridgeSha256'];rows=[json.loads(line) for line in (evidence/'runtime-work/trajectory.jsonl').read_text().splitlines()]
        delivered={r['value']['sequence'] for r in rows if r['kind']=='action_delivered'};responded={r['value']['sequence'] for r in rows if r['kind']=='action_response'}
        samples=[]
        for r in rows:
            if r['kind']!='action_submitted':continue
            d=r['value'];obs=d['pre'];seq=d['sequence']
            if seq not in delivered or seq not in responded or obs['state']['phase']!='combat':continue
            samples.append({'observation':obs,'actionId':d['actionId'],'source':{'processRunId':obs['processRunId'],'decisionId':obs['decisionId'],'sequence':seq}})
        assert 0<len(samples)<=1000
        result={'schema':'sts2-sanitized-samples-v1','sourceTrajectorySha256':hashlib.sha256((evidence/'runtime-work/trajectory.jsonl').read_bytes()).hexdigest(),'sourceLaunch':str(a.launch.absolute()),'gateSha256':hashlib.sha256(a.gate.read_bytes()).hexdigest(),'samples':samples}
        a.output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
        with a.output.open('x') as f:json.dump(result,f)
        print(json.dumps({'samples':len(samples),'output':str(a.output)}));return
    if not 1<=a.updates<=20:raise ValueError('Update bound')
    out=a.output.absolute();out.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    cmd=['/usr/bin/bwrap','--unshare-all','--die-with-parent','--new-session','--clearenv','--setenv','PATH','/usr/bin','--ro-bind','/usr','/usr','--symlink','usr/bin','/bin','--symlink','usr/lib','/lib','--symlink','usr/lib64','/lib64','--proc','/proc','--dev','/dev','--tmpfs','/tmp','--ro-bind',str(ROOT/'scripts/sts2_small_policy.py'),'/trainer.py','--ro-bind',str(a.data.absolute()),'/samples.json','--bind',str(out.parent),'/output','--chdir','/tmp']
    if a.command=='resume':cmd+=['--ro-bind',str(a.checkpoint.absolute()),'/resume.json']
    cmd+=['/usr/bin/python3','-I','-B','/trainer.py','--data','/samples.json','--output','/output/'+out.name,'--updates',str(a.updates)]
    if a.command=='resume':cmd+=['--resume','/resume.json']
    def limits():
        resource.setrlimit(resource.RLIMIT_AS,(256*1024**2,256*1024**2));resource.setrlimit(resource.RLIMIT_CPU,(30,31))
    log=out.with_suffix('.update.json')
    if out.exists() or log.exists():raise FileExistsError('Refuse to overwrite checkpoint or update evidence')
    proc=subprocess.Popen(cmd,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True,preexec_fn=limits)
    try:stdout,stderr=proc.communicate(timeout=60)
    except subprocess.TimeoutExpired:proc.kill();stdout,stderr=proc.communicate();stderr+='\ntraining wall limit exceeded'
    log.write_text(json.dumps({'argv':cmd,'hostPid':proc.pid,'wrapperPid':os.getpid(),'exitCode':proc.returncode,'stdout':stdout,'stderr':stderr,'utc':time.time(),'memoryLimitBytes':256*1024**2,'cpuLimitSeconds':30,'wallLimitSeconds':60},indent=2));print(stdout,end='');return proc.returncode
if __name__=='__main__':sys.exit(main() or 0)
