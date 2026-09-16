using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using File=System.IO.File;
public partial class Main
{
    private sealed record Choice(string Id,string Kind,string Label,object? Detail,Action Apply);
    private readonly CardProjection cardProjection=new();
    private readonly Dictionary<object,object> renderedCards=new(ReferenceEqualityComparer.Instance);
    private void ReadRenderedCards()
    {
        renderedCards.Clear();
        foreach(var n in Descendants(GetTree().Root).Where(n=>n.GetType().Name=="NCard"&&n.IsNodeReady()&&n is Control c&&c.IsVisibleInTree()))
        {
            if(Prop(n,"Visibility")?.ToString()!="Visible")continue;
            var card=Field(n,"_model");if(card==null)continue;
            var lockIcon=Field(n,"_lock") as CanvasItem;
            renderedCards[card]=new{description=Prop(Field(n,"_descriptionLabel"),"Text")?.ToString(),
                title=Prop(Field(n,"_titleLabel"),"Text")?.ToString(),uiLocked=lockIcon!=null&&lockIcon.IsVisibleInTree()};
        }
    }
    private object CardDetails(object card)=>new{state=cardProjection.Read(card),rendered=renderedCards.GetValueOrDefault(card)};
    private object VisiblePower(object p)=>new{id=ModelId(p),amount=Read(p,"DisplayAmount"),
        facing=p.GetType().Name=="SurroundedPower"?Read(p,"Facing").ToString():null,
        applierCombatId=p.GetType().Name=="FlankingPower"?Prop(Prop(p,"Applier"),"CombatId"):null,
        targetCombatId=p.GetType().Name=="SandpitPower"?Prop(Prop(p,"Target"),"CombatId"):null};
    private readonly List<Choice> choices=new();
    private string? pendingSignature, stableSignature, submittedSignature;
    private int decisionId, submittedCount;
    private double stableAt, progressAt, submittedAt;
    private bool waitingController,runValidated;
    private object? lastPlayerObservation,lastPlayerSnapshot;
    private int decisionLimit=300;
    private string configuredSeed="",submittedKind="",submittedPhase="";
    private int submittedFloor,legacyPrefixLimit;
    private static object? Prop(object? x,string name) => x?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(x);
    private static object Field(object x,string name) { for(Type? t=x.GetType();t!=null;t=t.BaseType) {var f=t.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);if(f!=null)return f.GetValue(x)!;} throw new MissingFieldException(x.GetType().Name,name); }
    private static object Singleton(int token)=>((MethodInfo)NormalGameLoader.GameAssembly.ManifestModule.ResolveMethod(token)!).Invoke(null,null)!;
    private static object? Call(object x,string name,params object?[] args)
    {
        var ms=x.GetType().GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(m=>m.Name==name && m.GetParameters().Length==args.Length).ToArray();
        ManagedBootstrap.Require(ms.Length==1,"Ambiguous/missing production method "+name);
        return ms[0].Invoke(x,args);
    }
    private static object[] Items(object? x)=>x is IEnumerable e?e.Cast<object>().ToArray():Array.Empty<object>();
    private static string ModelId(object x)=>Read(x,"Id").ToString()!;
    private static bool IsA(Node n,string type){for(Type? t=n.GetType();t!=null;t=t.BaseType)if(t.Name==type)return true;return false;}
    private static string UiText(Node n)=>string.Join(" ",Descendants(n).OfType<Control>().Where(c=>Valid(c)&&c.IsVisibleInTree())
        .Select(c=>Prop(c,"Text") as string).Where(s=>!string.IsNullOrEmpty(s)));
    private static void Atomic(string name,object value)
    {
        var text=JsonSerializer.Serialize(value,JournalJson); File.WriteAllText(name+".tmp",text);File.Move(name+".tmp",name,true);
    }
    private void Trace(string kind,object value)=>File.AppendAllText("/work/trajectory.jsonl",JsonSerializer.Serialize(new {kind,utc=DateTimeOffset.UtcNow,elapsedSeconds=elapsed.Elapsed.TotalSeconds,decisionId,value},JournalJson)+"\n");
    private void Add(string id,string kind,string label,object? detail,Action apply)=>choices.Add(new(id,kind,label,detail,apply));
    private void AddClick(Node n,string id,string kind,string label,object? detail=null)
    {if(Enabled(n))Add(id,kind,label,detail,()=>Click(n,kind,detail));}
    private void AddHolder(Node h,string id,string kind)
    {
        if(h is not Control c||!c.IsVisibleInTree()||!(bool)Field(h,"_isClickable"))return;
        var card=Read(h,"CardModel");
        Add(id,kind,Read(card,"Title").ToString()!,new {card=ModelId(card),text=UiText(h),cardDetails=CardDetails(card)},()=>{
            RecordAction(kind,h,new {actionId=id,card=ModelId(card)});
            var key=(StringName)NormalGameLoader.GameAssembly.ManifestModule.ResolveField(0x04001b0f)!.GetValue(null)!;
            using var input=new InputEventAction {Action=key,Pressed=true};
            if(kind=="select_hand") {
                var hand=VisibleType("NPlayerHand") ?? throw new InvalidDataException("Hand selection unavailable");
                string mode=Read(hand,"CurrentMode").ToString()!;
                if(mode=="SimpleSelect") Call(hand,"SelectCardInSimpleMode",h);
                else if(mode=="UpgradeSelect") Call(hand,"SelectCardInUpgradeMode",h);
                else throw new InvalidDataException("Unadapted hand selection mode "+mode);
            } else Invoke(0x06008d5a,h,input);
        });
    }
    private object Status(object state,object player)
    {
        object creature=Read(player,"Creature");object? pc=Prop(player,"PlayerCombatState");
        return new {act=(int)Read(state,"CurrentActIndex")+1,floor=Read(state,"TotalFloor"),hp=Read(creature,"CurrentHp"),maxHp=Read(creature,"MaxHp"),
            block=Read(creature,"Block"),powers=Items(Read(creature,"Powers")).Where(p=>(bool)Read(p,"IsVisible")).Select(VisiblePower).ToArray(),gold=Read(player,"Gold"),energy=pc is null?0:Read(pc,"Energy"),
            potions=Items(Read(player,"Potions")).Select(ModelId).ToArray(),relics=Items(Read(player,"Relics")).Select(ModelId).ToArray(),deckCards=Items(Read(Read(player,"Deck"),"Cards")).Select((c,index)=>new{index,cardDetails=CardDetails(c)}).ToArray(),deck=Items(Read(Read(player,"Deck"),"Cards")).Select(c=>ModelId(c)+((int)Read(c,"CurrentUpgradeLevel")>0?"+"+Read(c,"CurrentUpgradeLevel"):"")).OrderBy(s=>s).ToArray()};
    }
    private object Snapshot()
    {
        choices.Clear();ReadRenderedCards();var manager=Singleton(0x06000bdf);var state=Invoke(0x06000c30,manager)!;
        var player=Items(Read(state,"Players")).Single();object status=Status(state,player);
        if((bool)Read(manager,"IsGameOver"))return new {phase="terminal",status,result=(int)Read(Read(player,"Creature"),"CurrentHp")==0?"death":"game_over_unclassified"};
        var modal=NormalGameRead.Call(0x06008743,NormalGameRead.Call(0x06008741,null)!) as Node;
        if(modal!=null)throw new InvalidDataException("Unsupported in-run modal: "+modal.GetType().Name);
        var potionPopup=VisibleType("NPotionPopup");
        if(potionPopup!=null && !(bool)Read(potionPopup,"IsMarkedForRemoval"))
        {
            AddClick((Node)Field(potionPopup,"_discardButton"),"discard_potion","discard_potion","Discard selected potion");
            Add("cancel_potion","cancel_potion","Close potion menu",null,()=>Call(potionPopup,"Remove"));
            return new {phase="potion_menu",status,text=UiText(potionPopup)};
        }
        // Highest visible overlay first; no underlying combat/map actions while selecting a card.
        var cardReward=VisibleType("NCardRewardSelectionScreen");
        if(cardReward!=null)
        {
            int i=0;foreach(var h in Descendants(cardReward).Where(n=>IsA(n,"NCardHolder")&&n is Control c&&c.IsVisibleInTree()))
            {var card=Prop(h,"CardModel");if(card==null)continue;string id="reward_card:"+i++;AddHolder(h,id,"reward_card");}
            foreach(var skip in Descendants(cardReward).Where(n=>n.GetType().Name=="NBackButton"))AddClick(skip,"skip_card","skip_card","Skip card reward");
            int alt=0;foreach(var n in Descendants(cardReward).Where(n=>n.GetType().Name=="NCardRewardAlternativeButton"))AddClick(n,"reward_alternative:"+alt++,"reward_alternative",UiText(n));
            return new {phase="card_reward",status};
        }
        var chooseCard=VisibleType("NChooseACardSelectionScreen");
        if(chooseCard!=null)
        {
            var row=(Control)Field(chooseCard,"_cardRow");int i=0;
            if(row.FocusBehaviorRecursive.ToString()!="Disabled")foreach(var h in Descendants(row).Where(n=>IsA(n,"NCardHolder")))AddHolder(h,"choose_card:"+i++,"choose_card");
            foreach(var n in Descendants(chooseCard).Where(n=>n.GetType().Name=="NChoiceSelectionSkipButton"))AddClick(n,"skip_choice","skip_choice","Skip card choice");
            return new {phase="choose_card",status,text=UiText(chooseCard)};
        }
        var grid=Descendants(GetTree().Root).SingleOrDefault(n=>IsA(n,"NCardGridSelectionScreen")&& n is Control c&&c.IsVisibleInTree());
        if(grid!=null)
        {
            var actualGrid=(Control)Field(grid,"_grid");
            int i=0;if(actualGrid.FocusBehaviorRecursive.ToString()!="Disabled")foreach(var h in Descendants(actualGrid).Where(n=>IsA(n,"NCardHolder")))AddHolder(h,"select_grid:"+i++,"select_grid");
            foreach(var n in Descendants(grid).Where(n=>IsA(n,"NConfirmButton")))AddClick(n,"confirm_grid:"+n.Name,"confirm_selection",UiText(n));
            foreach(var n in Descendants(grid).Where(n=>IsA(n,"NBackButton")))AddClick(n,"cancel_grid:"+n.Name,"cancel_selection",UiText(n));
            return new {phase="grid_selection",status,screen=grid.GetType().Name,text=UiText(grid)};
        }
        var map=VisibleType("NMapScreen");
        if(map!=null)
        {
            if((bool)Read(map,"IsTraveling"))return new{phase="game_wait",status,reason="original_map_travel"};
            foreach(var n in Descendants(map).Where(n=>IsA(n,"NMapPoint")&& n is Control c&&c.IsVisibleInTree()))
            {
                if(!(bool)Invoke(0x060072d1,n)!||!(bool)Invoke(0x060072d8,n)!||!(bool)Call(map,"IsNodeOnScreen",n)!)continue;
                object point=Read(n,"Point"),coord=Field(point,"coord");int row=(int)Field(coord,"row"),col=(int)Field(coord,"col");
                string type=Read(point,"PointType").ToString()!;
                AddClick(n,$"map:{row}:{col}","map",$"{type} ({col},{row})",new {row,col,roomType=type});
            }
            return new {phase="map",status};
        }
        var rewards=VisibleType("NRewardsScreen");
        if(rewards!=null)
        {
            var unavailable=new List<object>();bool fullPotionReward=false;
            int i=0;foreach(var n in Descendants(rewards).Where(n=>IsA(n,"NRewardButton")))
            {
                var reward=Prop(n,"Reward");string id="reward:"+i++;string label=reward?.GetType().Name+" "+UiText(n);
                if(reward?.GetType().Name=="PotionReward" && !(bool)Read(player,"HasOpenPotionSlots"))
                {if(Enabled(n)){unavailable.Add(new {id,label,reason="potion_belt_full"});fullPotionReward=true;}continue;}
                AddClick(n,id,"reward",label);
            }
            if(fullPotionReward && (bool)Read(player,"CanUseOrRemovePotions"))
            {
                int slot=0;foreach(var n in Descendants(GetTree().Root).Where(n=>n.GetType().Name=="NPotionHolder"&&n is Control c&&c.IsVisibleInTree()))
                {
                    int index=slot++;if(!(bool)Read(n,"HasPotion")||!(bool)Field(n,"_isUsable"))continue;
                    var potion=Read(Read(n,"Potion"),"Model");AddClick(n,"inspect_potion:"+index,"inspect_potion","Potion menu: "+Call(Read(potion,"Title"),"GetFormattedText"),new {potion=ModelId(potion)});
                }
            }
            foreach(var n in Descendants(rewards).Where(n=>n.GetType().Name=="NProceedButton"))AddClick(n,"proceed","proceed","Proceed to map");
            return new {phase="rewards",status,unavailable};
        }
        var overlays=Singleton(0x0600715f);
        if((int)Read(overlays,"ScreenCount")>0)
        {
            var top=Call(overlays,"Peek") as Node;
            if(top!=null&&top is Control tc&&tc.IsVisibleInTree())throw new InvalidDataException("Unsupported overlay "+top.GetType().Name);
        }
        var playerHand=VisibleType("NPlayerHand");
        if(playerHand!=null&&(bool)Read(playerHand,"IsInCardSelection"))
        {
            int i=0;foreach(var h in Items(Read(playerHand,"ActiveHolders")).Cast<Node>())AddHolder(h,"select_hand:"+i++,"select_hand");
            foreach(var n in Descendants(playerHand).Where(n=>n.GetType().Name=="NConfirmButton"))AddClick(n,"confirm_selection","confirm_selection","Confirm selection");
            return new {phase="hand_selection",status,mode=Read(playerHand,"CurrentMode").ToString(),text=UiText(playerHand)};
        }
        var cm=Singleton(0x0600567b);
        if((bool)Read(cm,"IsInProgress"))
        {
            var end=VisibleType("NEndTurnButton");var pc=Prop(player,"PlayerCombatState");
            if(pc is null)return new {phase="game_wait",status,reason="combat_initializing"};
            var cs=Call(cm,"DebugOnlyGetState")!;
            object[] enemies=Items(Read(cs,"Enemies")).Where(e=>Descendants(GetTree().Root).Any(n=>n.GetType().Name=="NCreature"&&n is CanvasItem ci&&ci.IsVisibleInTree()&&ReferenceEquals(Prop(n,"Entity"),e))).ToArray();
            var visibleEnemies=enemies.Select(e=>new {id=Read(e,"CombatId"),name=Read(e,"Name").ToString(),hp=Read(e,"CurrentHp"),maxHp=Read(e,"MaxHp"),block=Read(e,"Block"),
                intents=Descendants(GetTree().Root).Where(n=>n.GetType().Name=="NIntent"&&n is CanvasItem c&&c.IsVisibleInTree()&&ReferenceEquals(Field(n,"_owner"),e)).Select(n=>new {icon=Field(n,"_animationName")?.ToString(),text=UiText(n)}).ToArray(),
                powers=Items(Read(e,"Powers")).Where(p=>(bool)Read(p,"IsVisible")).Select(VisiblePower).ToArray()}).ToArray();
            bool ready=end!=null&&Enabled(end)&&!(bool)Read(cm,"PlayerActionsDisabled") && (bool)Call(cm,"IsPartOfPlayerTurn",player)!
                && !(bool)Call(cm,"IsPlayerReadyToEndTurn",player)! && !(bool)Call(cm,"IsExecutingCardOrPotionEffect",player)!;
            object[] hand=Items(Read(Read(pc,"Hand"),"Cards"));
            var handView=hand.Select((card,index)=>new {index,id=ModelId(card),title=Read(card,"Title"),cost=(bool)Read(Read(card,"EnergyCost"),"CostsX")?"X":Call(Read(card,"EnergyCost"),"GetResolved")!.ToString(),targetType=Read(card,"TargetType").ToString(),cardDetails=CardDetails(card),canPlay=ready?(bool?)Invoke(0x060018e9,card):null}).ToArray();
            if(ready)
            {
                for(int i=0;i<hand.Length;i++)
                {
                    var card=hand[i];if(!(bool)Invoke(0x060018e9,card)!)continue;
                    string targetType=Read(card,"TargetType").ToString()!;
                    if(targetType is "AnyEnemy" or "AnyAlly" or "AnyPlayer")
                    {
                        foreach(var target in enemies.Concat(new[]{Read(player,"Creature")}))
                        {if(!(bool)Invoke(0x060018eb,card,target)!)continue;string id=$"play:{i}:{Read(target,"CombatId")}";
                         Add(id,"play",Read(card,"Title")+" → "+Read(target,"Name"),new {card=ModelId(card),cardDetails=CardDetails(card),targetHp=Read(target,"CurrentHp"),target=Read(target,"CombatId")},()=>ManagedBootstrap.Require((bool)Invoke(0x060018ec,card,target)!,"TryManualPlay rejected legal action"));}
                    }
                    else if(targetType is "Self" or "AllEnemies" or "RandomEnemy" or "None" or "AllAllies")
                    {Add("play:"+i,"play",Read(card,"Title").ToString()!,new {card=ModelId(card),cardDetails=CardDetails(card)},()=>ManagedBootstrap.Require((bool)Invoke(0x060018ec,card,new object?[]{null})!,"TryManualPlay rejected legal action"));}
                    else throw new InvalidDataException("Unsupported card target type "+targetType);
                }
                if((bool)Read(player,"CanUseOrRemovePotions"))
                {
                    var slots=Items(Read(player,"PotionSlots"));
                    for(int slot=0;slot<slots.Length;slot++)
                    {
                        var potion=slots[slot];if(potion is null||(bool)Read(potion,"IsQueued")||!(bool)Read(potion,"PassesCustomUsabilityCheck"))continue;
                        string usage=Read(potion,"Usage").ToString()!;if(usage is "Automatic" or "None")continue;
                        ManagedBootstrap.Require(usage is "CombatOnly" or "AnyTime","Unsupported potion usage "+usage);
                        string tt=Read(potion,"TargetType").ToString()!;string title=Call(Read(potion,"Title"),"GetFormattedText")!.ToString()!;
                        if(tt is "AnyEnemy" or "AnyAlly" or "AnyPlayer")
                        {
                            foreach(var target in enemies.Concat(new[]{Read(player,"Creature")}))
                            {if(!(bool)Invoke(0x06001b37,potion,target)!)continue;
                             Add($"potion:{slot}:{Read(target,"CombatId")}","potion",title+" → "+Read(target,"Name"),new {potion=ModelId(potion),target=Read(target,"CombatId"),targetHp=Read(target,"CurrentHp")},()=>Invoke(0x06001b36,potion,target));}
                        }
                        else if(tt is "Self" or "None" or "AllEnemies" or "RandomEnemy" or "AllAllies")
                            Add("potion:"+slot,"potion",title,new {potion=ModelId(potion)},()=>Invoke(0x06001b36,potion,new object?[]{null}));
                        else throw new InvalidDataException("Unsupported potion target type "+tt);
                    }
                }
                if((bool)Invoke(0x06008a05,end!)!)AddClick(end!,"end_turn","end_turn","End turn");
            }
            return new {phase=ready?"combat":"game_wait",status,reason=ready?"player_action":"enemy_turn_or_resolution",turn=Read(pc,"TurnNumber"),hand=handView,enemies=visibleEnemies};
        }
        var treasure=VisibleType("NTreasureRoom");
        if(treasure!=null)
        {
            foreach(var n in Descendants(treasure).Where(n=>n.GetType().Name=="NTreasureButton"))AddClick(n,"open_chest","open_chest","Open chest");
            AddClick((Node)Read(treasure,"ProceedButton"),"proceed","proceed","Proceed to map");
            return new {phase="treasure",status,text=UiText(treasure)};
        }
        var shop=VisibleType("NMerchantRoom");
        if(shop!=null)
        {
            var inv=(Node)Read(shop,"Inventory");bool open=(bool)Read(inv,"IsOpen");
            if(open)
            {
                bool blocked=(bool)Field(inv,"_isInputBlocked");int i=0;
                foreach(var n in Items(Call(inv,"GetAllSlots")).Cast<Node>())
                {
                    string id="buy:"+i++;var entry=Prop(n,"Entry");
                    if(entry==null||!(bool)Read(entry,"IsStocked")||!(bool)Read(entry,"EnoughGold")||blocked||n is not Control c||!c.IsVisibleInTree())continue;
                    if(n.GetType().Name=="NMerchantPotion"&&!(bool)Read(player,"HasOpenPotionSlots"))continue;
                    Add(id,"buy",n.GetType().Name+" "+UiText(n),new {itemType=n.GetType().Name,cost=Read(entry,"Cost")},()=>{
                        RecordAction("buy",n,new {actionId=id});
                        var key=(StringName)NormalGameLoader.GameAssembly.ManifestModule.ResolveField(0x04001b0f)!.GetValue(null)!;
                        using var input=new InputEventAction {Action=key,Pressed=true};Invoke(0x06006bd4,n,input);
                    });
                }
                if(!blocked)AddClick((Node)Field(inv,"_backButton"),"close_shop","close_shop","Close shop");
            }
            else
            {
                AddClick((Node)Read(shop,"MerchantButton"),"open_shop","open_shop","Open shop");
                AddClick((Node)Read(shop,"ProceedButton"),"proceed","proceed","Proceed to map");
            }
            return new {phase="shop",status,open};
        }
        var rest=VisibleType("NRestSiteRoom");
        if(rest!=null)
        {
            foreach(var n in Descendants(rest).Where(n=>n.GetType().Name=="NRestSiteButton"))
            {var option=Read(n,"Option");if(!(bool)Read(option,"IsEnabled")||(bool)Field(n,"_isUnclickable"))continue;
             AddClick(n,"rest:"+Read(option,"OptionId"),"rest",Call(Read(option,"Title"),"GetFormattedText")!.ToString()!,new {optionId=Read(option,"OptionId"),description=Call(Read(option,"Description"),"GetFormattedText")});}
            AddClick((Node)Read(rest,"ProceedButton"),"proceed","proceed","Proceed to map");
            return new {phase="rest",status,text=UiText(rest)};
        }
        var evt=VisibleType("NEventRoom");
        if(evt!=null)
        {
            foreach(var n in EventButtons(evt))
            {var o=Read(n,"Option");if((bool)Read(o,"IsLocked"))continue;int idx=(int)Invoke(0x06008402,n)!;string key=Read(o,"TextKey").ToString()!;
             AddClick(n,"event:"+idx,key=="PROCEED"?"proceed":"event",UiText(n),new {textKey=key});}
            return new {phase="event",status,text=UiText(evt)};
        }
        return new {phase="game_wait",status,reason="room_or_overlay_not_yet_adapted"};
    }
    private void TickBridge(NormalBootLogs.Snapshot logsNow)
    {
        if(!runValidated){ValidateNewRun();runValidated=true;}
        object state=Snapshot();lastPlayerSnapshot=new {state,actions=choices.Select(c=>new {id=c.Id,kind=c.Kind,label=c.Label,detail=c.Detail}).ToArray()};string signature=JsonSerializer.Serialize(new {state,actions=choices.Select(c=>new {id=c.Id,kind=c.Kind,label=c.Label,detail=c.Detail}).ToArray()},JournalJson);
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(state,JournalJson));string phase=doc.RootElement.GetProperty("phase").GetString()!;
        double now=elapsed.Elapsed.TotalSeconds;
        if(progressAt==0)progressAt=now;
        Atomic("/work/bridge/heartbeat.json",new {decisionId,phase,waiting=waitingController?"controller":"game",elapsedSeconds=now,submittedCount});
        if(phase=="terminal") {if(submittedSignature!=null)Trace("action_response",new {sequence=submittedCount,successor=lastPlayerSnapshot});Trace("terminal",state);Atomic("/work/bridge/terminal.json",new {outcome="game_over",state,submittedCount});Finish("continuous_game_over",null,0,logsNow);return;}
        if(signature!=stableSignature){stableSignature=signature;stableAt=now;}
        if(waitingController)
        {
            if(signature!=pendingSignature)
            {Trace("decision_invalidated",new {reason="game_state_changed",lastPlayerObservation});waitingController=false;pendingSignature=null;progressAt=now;}
            else if(File.Exists("/work/bridge/action.json"))
            {
                string raw=File.ReadAllText("/work/bridge/action.json");File.Delete("/work/bridge/action.json");
                using var request=JsonDocument.Parse(raw);var q=request.RootElement;
                string aid=q.GetProperty("actionId").GetString()!;int did=q.GetProperty("decisionId").GetInt32();
                var choice=choices.SingleOrDefault(x=>x.Id==aid);
                if(did!=decisionId||choice==null||q.GetProperty("processRunId").GetString()!=NormalBootConfig.Current.ProcessRunId)
                {var rejection=new {request=q.Clone(),reason="stale_duplicate_or_illegal",currentDecisionId=decisionId,submittedCount};Trace("action_rejected",rejection);Atomic("/work/bridge/rejection.json",rejection);return;}
                waitingController=false;submittedKind=choice.Kind;submittedPhase=phase;submittedFloor=doc.RootElement.GetProperty("status").GetProperty("floor").GetInt32();submittedCount++;submittedSignature=signature;submittedAt=progressAt=now;
                Trace("action_submitted",new {decisionId,actionId=aid,kind=choice.Kind,sequence=submittedCount,pre=lastPlayerObservation});
                choice.Apply();Trace("action_delivered",new {decisionId,actionId=aid,sequence=submittedCount});
                return;
            }
            else return;
        }
        if(submittedSignature!=null)
        {
            if(now-submittedAt>40)throw new TimeoutException("Submitted action has no stable successor within 40 seconds");
            if(signature==submittedSignature||now-submittedAt<0.4)return;
            // Proceed stabilization will be enabled after the existing recorded repair prefix is exhausted.
            if(submittedKind=="proceed"&&submittedCount>legacyPrefixLimit&&phase==submittedPhase&&doc.RootElement.GetProperty("status").GetProperty("floor").GetInt32()==submittedFloor)return;
            if(phase=="game_wait"||choices.Count==0||now-stableAt<0.4)
            {if(now-progressAt>40)throw new TimeoutException("No successor after submitted action");return;}
            Trace("action_response",new {sequence=submittedCount,successor=JsonDocument.Parse(signature).RootElement.Clone()});submittedSignature=null;progressAt=now;
        }
        if(submittedCount>=decisionLimit)
        {Atomic("/work/bridge/terminal.json",new {outcome="budget_truncated",submittedCount,state});Trace("budget_truncated",state);Finish("continuous_budget_truncated",null,0,logsNow);return;}
        if(phase=="game_wait"||choices.Count==0)
        {
            if(now-progressAt>40)
            {ManagedBootstrap.NewReport("stall-scene.json",new {state,decisionId,submittedCount,visible=Descendants(GetTree().Root).Where(n=>n is Control c&&c.IsVisibleInTree()).Select(n=>new {path=n.GetPath().ToString(),type=n.GetType().Name}).ToArray()});throw new TimeoutException("No supported decision: "+signature);}
            return;
        }
        if(now-stableAt<0.4)return;
        decisionId++;pendingSignature=signature;waitingController=true;progressAt=now;
        lastPlayerObservation=new {schema="sts2-observation-v1",processRunId=NormalBootConfig.Current.ProcessRunId,decisionId,
            state,actions=choices.Select(c=>new {id=c.Id,kind=c.Kind,label=c.Label,detail=c.Detail}).ToArray()};
        Trace("observation",lastPlayerObservation);Atomic("/work/bridge/observation.json",lastPlayerObservation);
    }
}

// Only call for owner-visible cards. Never enumerate combat AllCards or draw order.
// Object identity is recorder-local and must be mapped, not compared across processes.
public sealed class CardProjection
{
    private sealed record Identity(long Value);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object,Identity> identities=new();
    private long nextIdentity;
    private static object? Get(object x,string name)=>
        (x.GetType().GetProperty(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)
         ??throw new MissingMemberException(x.GetType().Name,name)).GetValue(x);
    private static object Required(object x,string name)=>Get(x,name)??throw new InvalidDataException(name+" is null");
    private static object? Modifier(object? value,bool enchantment)=>value==null?null:new {
        id=Required(value,"Id").ToString(),amount=Required(value,"Amount"),
        displayAmount=enchantment?Required(value,"DisplayAmount"):Required(value,"Amount")};
    public object Read(object card)
    {
        var id=Required(card,"Id").ToString();
        // Wither's displayed damage grows independently of CurrentUpgradeLevel.
        // Read the already-existing numeric value; never call FakeUpgrade/MatchWither.
        object? special=id=="CARD.WITHER"?new {
            fakeUpgradeLevel=Required(card,"FakeUpgradeLevel"),
            baseDamage=Required(Required(Required(card,"DynamicVars"),"Damage"),"BaseValue")}:null;
        var keywords=((IEnumerable)Required(card,"Keywords")).Cast<object>().Select(x=>x.ToString()).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        return new {id,instance=identities.GetValue(card,_=>new Identity(++nextIdentity)).Value,
            identityScope="recorder_process",upgrade=Required(card,"CurrentUpgradeLevel"),keywords,
            affliction=Modifier(Get(card,"Affliction"),false),enchantment=Modifier(Get(card,"Enchantment"),true),special};
    }
}
