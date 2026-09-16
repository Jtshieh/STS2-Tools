#!/usr/bin/env python3
"""64-parameter masked softmax behavior cloning; stdlib only. Sanitized JSON in/out."""
import argparse,hashlib,json,math,os,random,sys
DIM=64

def features(obs,action):
    x=[0.0]*DIM; d=action.get('detail') or {}; status=obs['state']['status']
    for label in ['kind:'+action['kind'],'card:'+d.get('card','')]:
        x[8+int(hashlib.sha256(label.encode()).hexdigest()[:8],16)%(DIM-8)]+=1
    x[0]=1; x[1]=status['hp']/max(1,status['maxHp']);x[2]=status['block']/30;x[3]=status['energy']/5
    x[4]=d.get('targetHp',0)/100
    x[5]=float(action['kind']=='end_turn')*status['energy']/5
    x[6]=float('DEFEND' in d.get('card',''))*status['block']/20
    x[7]=float('STRIKE' in d.get('card',''))*d.get('targetHp',0)/100
    return x

def probs(weights,xs):
    logits=[sum(a*b for a,b in zip(weights,x)) for x in xs];peak=max(logits)
    z=[math.exp(v-peak) for v in logits];total=sum(z);return [v/total for v in z]
def digest(value):return hashlib.sha256(json.dumps(value,sort_keys=True).encode()).hexdigest()
def tuples(x):return tuple(tuples(v) for v in x) if isinstance(x,list) else x

def train(a):
    raw=open(a.data,'rb').read();data=json.loads(raw);samples=data['samples']
    if not samples or any(s['observation']['state']['phase']!='combat' for s in samples):raise ValueError('Only validated combat samples accepted')
    dataset_hash=hashlib.sha256(raw).hexdigest();rng=random.Random(20260914)
    if a.resume:
        cp=json.load(open(a.resume));assert cp['datasetSha256']==dataset_hash
        assert cp['implementationSha256']==hashlib.sha256(open(__file__,'rb').read()).hexdigest()
        assert {k:cp['optimizer'][k] for k in ['type','lr','beta1','beta2','epsilon']}=={'type':'Adam','lr':0.03,'beta1':0.9,'beta2':0.999,'epsilon':1e-8}
        assert len(cp['weights'])==len(cp['optimizer']['m'])==len(cp['optimizer']['v'])==DIM
        rng.setstate(tuples(cp['rng']));w=cp['weights'];m=cp['optimizer']['m'];v=cp['optimizer']['v'];step=cp['updates'];order=cp['order'];cursor=cp['cursor']
    else:
        w=[rng.uniform(-0.01,0.01) for _ in range(DIM)];m=[0.0]*DIM;v=[0.0]*DIM;step=0;order=list(range(len(samples)));rng.shuffle(order);cursor=0
    assert 1<=a.updates<=20 and step+a.updates<=20
    initial=digest(w);initial_opt=digest({'m':m,'v':v,'updates':step});initial_rng=digest(rng.getstate());initial_sampler=digest({'order':order,'cursor':cursor});logs=[]
    for _ in range(a.updates):
        batch=[]
        for j in range(min(16,len(samples))):
            if cursor==len(order):rng.shuffle(order);cursor=0
            batch.append(order[cursor]);cursor+=1
        grad=[0.0]*DIM;loss=0.0
        for index in batch:
            sample=samples[index];obs=sample['observation'];acts=obs['actions'];target=[t['id'] for t in acts].index(sample['actionId'])
            xs=[features(obs,t) for t in acts];ps=probs(w,xs);loss-=math.log(max(ps[target],1e-300))
            for i,x in enumerate(xs):
                error=ps[i]-float(i==target)
                for k in range(DIM):grad[k]+=error*x[k]/len(batch)
        before=w[:];step+=1
        for k in range(DIM):
            m[k]=0.9*m[k]+0.1*grad[k];v[k]=0.999*v[k]+0.001*grad[k]**2
            w[k]-=0.03*(m[k]/(1-0.9**step))/(math.sqrt(v[k]/(1-0.999**step))+1e-8)
        logs.append({'update':step,'loss':loss/len(batch),'parameterDeltaL2':math.sqrt(sum((x-y)**2 for x,y in zip(w,before))),'parameterSha256':digest(w),'sampleIndices':batch})
    checkpoint={'implementationSha256':hashlib.sha256(open(__file__,'rb').read()).hexdigest(),'schema':'sts2-small-policy-v1','algorithm':'masked softmax behavior cloning from actual visible-state rule demonstrations','reward':'none; supervised negative log likelihood, no invented environment reward','weights':w,'optimizer':{'type':'Adam','lr':0.03,'beta1':0.9,'beta2':0.999,'epsilon':1e-8,'m':m,'v':v},'rng':rng.getstate(),'updates':step,'order':order,'cursor':cursor,'datasetSha256':dataset_hash,'sampleCount':len(samples),'gameRecovery':'none; actual source game outcome retained in sampling trajectory; model checkpoint cannot restore a game process'}
    with open(a.output,'x') as f:json.dump(checkpoint,f)
    report={'pid':os.getpid(),'resume':a.resume,'initialParameterSha256':initial,'initialOptimizerSha256':initial_opt,'initialRngSha256':initial_rng,'initialSamplerSha256':initial_sampler,'finalParameterSha256':digest(w),'finalOptimizerSha256':digest({'m':m,'v':v,'updates':step}),'finalRngSha256':digest(rng.getstate()),'finalSamplerSha256':digest({'order':order,'cursor':cursor}),'updates':logs,'checkpointSha256':hashlib.sha256(open(a.output,'rb').read()).hexdigest(),'datasetSha256':dataset_hash}
    print(json.dumps(report),flush=True)
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--data',required=True);p.add_argument('--output',required=True);p.add_argument('--resume');p.add_argument('--updates',type=int,default=4);train(p.parse_args())
