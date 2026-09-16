#!/usr/bin/env python3
"""Player-observation-only rule policy. Runs with no project, save, log or network access."""
import json,sys,hashlib,os
# This executable is mounted alone into a fresh filesystem and network namespace.
assert not any(os.path.exists(p) for p in ['/home','/work','/game','/root'])
sys.stderr.write(json.dumps({'boundary':'private roots absent','pid':os.getpid()})+'\n')

closed_shops=set()
model=None
if os.path.exists('/checkpoint.json'):
    import runpy
    model=json.load(open('/checkpoint.json'));assert model['schema']=='sts2-small-policy-v1'
    assert model['implementationSha256']==hashlib.sha256(open('/model-code.py','rb').read()).hexdigest()
    funcs=runpy.run_path('/model-code.py')

def choose(obs):
    state=obs['state']; actions=obs['actions']; phase=state['phase']
    if not actions: raise ValueError('No legal action')
    allowed={'event':{'event','proceed'},'map':{'map'},'combat':{'play','end_turn','potion'},'rewards':{'reward','proceed','inspect_potion'},'potion_menu':{'discard_potion','cancel_potion'},'choose_card':{'choose_card','skip_choice'},'card_reward':{'reward_card','skip_card','reward_alternative'},'hand_selection':{'select_hand','confirm_selection'},'grid_selection':{'select_grid','confirm_selection','cancel_selection'},'rest':{'rest','proceed'},'shop':{'open_shop','buy','close_shop','proceed'},'treasure':{'open_chest','proceed'}}
    if phase not in allowed or any(a['kind'] not in allowed[phase] for a in actions):
        raise ValueError('Unsupported decision type: '+phase)
    if model is not None and phase=='combat':
        xs=[funcs['features'](obs,a) for a in actions];ps=funcs['probs'](model['weights'],xs)
        selected=max(range(len(actions)),key=lambda i:(ps[i],hashlib.sha256(actions[i]['id'].encode()).hexdigest()))
        return {'actionId':actions[selected]['id'],'reason':'frozen masked softmax argmax','controller':'local_trained_64_parameter_policy','probabilities':ps,'updates':model['updates']}
    def score(a):
        d=a.get('detail') or {}; k=a['kind']; label=a['label']
        if k=='play':
            card=d['card']; priority=50
            if 'NEUTRALIZE' in card: priority=100
            elif 'STRIKE' in card: priority=80
            elif 'DEFEND' in card: priority=40 if state['status']['block']<12 else 5
            return priority-d.get('targetHp',0)*0.01
        if k=='potion':return 95 if state['status']['hp']<state['status']['maxHp']*0.8 else -150
        if k=='end_turn':return -100
        if k=='event':return 100 if 'FISHING_ROD' in d.get('textKey','') else 10
        if k=='confirm_selection':return 100
        if k=='cancel_selection':return -10
        if k=='select_grid':return 50
        if k=='rest':return 80 if d['optionId']=='HEAL' and state['status']['hp']<state['status']['maxHp']*0.8 else 50
        if k=='select_hand':return 50 if 'DEFEND' in d['card'] else 20
        if k=='open_shop':return -20 if (state['status']['act'],state['status']['floor']) in closed_shops else 100
        if k=='open_chest':return 100
        if k=='close_shop':return 0
        if k=='buy':return (80 if d['itemType']=='NMerchantCardRemoval' else 40)-d['cost']*0.05
        if k=='inspect_potion':return 25
        if k=='discard_potion':return 50
        if k=='cancel_potion':return 0
        if k=='reward':return 50
        if k=='choose_card':return 50
        if k=='skip_choice':return -20
        if k=='reward_card':return 50
        if k in ('skip_card','reward_alternative'):return -20
        if k=='map':return {'RestSite':80,'Treasure':70,'Monster':50,'Unknown':40,'Shop':30,'Elite':10,'Boss':0}.get(d['roomType'],20)
        if k=='proceed':return 0
        raise ValueError('Unknown action')
    # Known-domain equal-score ties use stable visible action identity hash, never implicit first-item fallback.
    selected=max(actions,key=lambda a:(score(a),hashlib.sha256(a['id'].encode()).hexdigest()))
    if selected['kind']=='close_shop':closed_shops.add((state['status']['act'],state['status']['floor']))
    return {'actionId':selected['id'],'reason':'visible-state-rule-v1','controller':'rule_not_model'}
for line in sys.stdin:
    try:
        obs=json.loads(line)
        if '_historyAction' in obs:
            if obs['_historyAction']=='close_shop':closed_shops.add((obs['state']['status']['act'],obs['state']['status']['floor']))
            print(json.dumps({'historyAccepted':True}),flush=True)
        else:print(json.dumps(choose(obs)),flush=True)
    except Exception as e: print(json.dumps({'error':str(e)}),flush=True)
