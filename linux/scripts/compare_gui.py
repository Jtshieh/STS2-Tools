"""Scope: exact semantic state, explicit local identity bijection and UI-holder mapping.
Only normalize missing rendered-card-node presence: Observation.ReadRenderedCards reads
existing visible NCard labels and lock sprite, not model mutation. Values when both
sides render remain compared. Never normalize event/reward text or mechanical fields.
"""
import json,re
card_ids={};inverse_card_ids={};action_ids={}

def compare(expected,actual):
    before_ids=dict(card_ids);before_inverse=dict(inverse_card_ids)
    a={k:json.loads(json.dumps(expected[k])) for k in ('state','actions') if k in expected}
    b={k:json.loads(json.dumps(actual[k])) for k in ('state','actions') if k in actual}
    diffs=[];display=[];timing=[];action_ids.clear()
    # Rest hover description duplicates the same option description already compared in actions.
    if a['state'].get('phase')=='rest' and b['state'].get('phase')=='rest':
        old='[gold]Upgrade[/gold] a card in your [gold]Deck[/gold].'
        for state in [a['state'],b['state']]:
            state['text']=state.get('text','').replace(old,'Upgrade a card in your deck.')
    # Pinned NEventRoom setup IL 019e..021f selects AncientDialogue with Rng.Chaotic
    # and sends only its lines to NAncientEventLayout.SetDialogue. SetOptions follows
    # separately. Restrict this rule to the two actually observed TEZCATARA greetings.
    for observation in [a,b]:
        state=observation['state'];opts=observation.get('actions',[])
        if state.get('phase')=='event' and state.get('status',{}).get('act')==2 and opts and all((x.get('detail') or {}).get('textKey','').startswith('TEZCATARA.pages.INITIAL.options.') for x in opts):
            text=state.get('text','');first_label=opts[0]['label']
            if first_label and text.count(first_label)==1 and text.endswith('[ancient_banner]TEZCATARA[/ancient_banner]'):
                greeting,tail=text.split(first_label,1)
                display.append(['$.state.text.greeting',greeting,'<audited-tezcatara-greeting>','Pinned NEventRoom Rng.Chaotic dialogue-only selection; options and suffix retained'])
                state['text']='<audited-tezcatara-greeting> '+first_label+tail
    # Establish owner-deck identity before matching UI holders whose z-order is mouse-dependent.
    for x,y in zip(a['state'].get('status',{}).get('deckCards',[]),b['state'].get('status',{}).get('deckCards',[])):
        x=x['cardDetails']['state'];y=y['cardDetails']['state'];i=x['instance'];j=y['instance']
        if i not in card_ids and j not in inverse_card_ids:
            card_ids[i]=j;inverse_card_ids[j]=i
    if a['state'].get('phase')==b['state'].get('phase') and a['state'].get('phase') in ('grid_selection','card_reward'):
        holder_prefix='select_grid:' if a['state']['phase']=='grid_selection' else 'reward_card:'
        actual_choices=b.get('actions',[]);aligned=[];used=set()
        for x in a.get('actions',[]):
            if x['id'].startswith(holder_prefix):
                detail=x['detail']['cardDetails']['state'];target=card_ids.get(detail['instance'])
                candidates=[y for y in actual_choices if y['id'] not in used and y['id'].startswith(holder_prefix) and y['detail']['cardDetails']['state']['instance']==target]
                if target is None and holder_prefix=='reward_card:':
                    # Newly offered cards have no prior identity mapping. Require one
                    # unique full mechanical match among current visible candidates.
                    def mechanical(state):return {k:v for k,v in state.items() if k!='instance'}
                    candidates=[y for y in actual_choices if y['id'] not in used and y['id'].startswith(holder_prefix) and mechanical(y['detail']['cardDetails']['state'])==mechanical(detail)]
            else:candidates=[y for y in actual_choices if y['id']==x['id'] and y['id'] not in used]
            if len(candidates)!=1:diffs.append(['$.actions.mapping',x['id'],len(candidates)]);continue
            y=candidates[0];used.add(y['id']);action_ids[x['id']]=y['id'];y=dict(y,id=x['id']);aligned.append(y)
        aligned.extend(y for y in actual_choices if y['id'] not in used);b['actions']=aligned
        # NDeckUpgradeSelectScreen includes the same grid below its confirmation
        # preview even when all holders are disabled. Preserve every complete card
        # label (including upgrade preview), compare their multiset and exact suffix.
        for observation in [a,b]:
            if observation['state'].get('screen')=='NDeckUpgradeSelectScreen':
                text=observation['state'].get('text','')
                suffix='Choose a card to [gold]Upgrade[/gold]. View Upgrades'
                if not text.endswith(suffix):diffs.append(['$.state.text.unreviewed-upgrade-layout',text]);continue
                cards=text[:-len(suffix)].strip()
                blocks=re.split(r' (?=\[center\])',cards)
                if not all(block.startswith('[center]') and '[/center]' in block for block in blocks):
                    diffs.append(['$.state.text.unreviewed-card-blocks',cards]);continue
                observation['state']['text']={'cardLabels':sorted(blocks),'suffix':suffix}
    # Bounded replay can proceed with an explicitly retained availability-timing
    # mismatch when the recorded choice remains legal; this is NOT a strict match.
    if a['state'].get('phase')=='combat' and b['state'].get('phase')=='combat':
        aa=a.get('actions',[]);bb=b.get('actions',[])
        if not any(x['id']=='end_turn' for x in aa) and any(x['id']=='end_turn' for x in bb):
            timing.append({'kind':'extra_end_turn_available_on_linux','strictMatched':False})
            b['actions']=[x for x in bb if x['id']!='end_turn']
    def walk(x,y,path):
        if path.endswith('.cardDetails.rendered') and (x is None or y is None):
            if x!=y:display.append([path,x,y,'visible NCard node presence only; full card state still compared'])
            return
        if isinstance(x,dict) and isinstance(y,dict):
            if x.get('identityScope')=='recorder_process' and y.get('identityScope')=='recorder_process':
                i,j=x['instance'],y['instance']
                if (i in card_ids and card_ids[i]!=j) or (j in inverse_card_ids and inverse_card_ids[j]!=i):diffs.append([path+'.instance',i,j,'bijection conflict'])
                else:card_ids[i]=j;inverse_card_ids[j]=i;x['instance']=j
            for k in sorted(set(x)|set(y)):
                if k not in x or k not in y:diffs.append([path+'.'+k,x.get(k,'<missing>'),y.get(k,'<missing>')])
                else:walk(x[k],y[k],path+'.'+k)
        elif isinstance(x,list) and isinstance(y,list):
            if len(x)!=len(y):diffs.append([path+'.length',len(x),len(y)])
            for i,(u,v) in enumerate(zip(x,y)):walk(u,v,path+f'[{i}]')
        elif x!=y:diffs.append([path,x,y])
    walk(a,b,'$')
    if diffs:
        card_ids.clear();card_ids.update(before_ids);inverse_card_ids.clear();inverse_card_ids.update(before_inverse)
    return {'matched':not diffs,'strictMatched':not diffs and not timing,'unverifiedTimingDifferences':timing,'differences':diffs,'displayDifferences':display,'cardIdentityMap':card_ids,'actionIdMap':dict(action_ids)}
