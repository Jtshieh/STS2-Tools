// Read-only projection adapted from the existing Linux Bridge.cs (frozen local reference).
// All dispatch lambdas and training/controller code were removed. No writes to game state.
using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Runs;
namespace Sts2Recorder;
public sealed partial class Observer
{
    internal sealed record Choice(string Id,string Kind,string Label,object? Detail);
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
    internal readonly List<Choice> choices=new();
    internal readonly Dictionary<ulong,string> bindings=new();
    internal static readonly Assembly Game=typeof(RunManager).Assembly;
    internal static object Read(object x,string name)=>Prop(x,name) ?? throw new MissingMemberException(x.GetType().Name,name);
    internal static object? Prop(object? x,string name)=>x?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)?.GetValue(x);
    internal static object Field(object x,string name) {for(Type? t=x.GetType();t!=null;t=t.BaseType){var f=t.GetField(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);if(f!=null)return f.GetValue(x)!;}throw new MissingFieldException(x.GetType().Name,name);}
    private static object? Invoke(int token,object? x,params object?[] args)=>((MethodInfo)Game.ManifestModule.ResolveMethod(token)!).Invoke(x,args);
    private static object Singleton(int token)=>Invoke(token,null)!;
    internal static object? Call(object x,string name,params object?[] args){var ms=x.GetType().GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(m=>m.Name==name&&m.GetParameters().Length==args.Length).ToArray();if(ms.Length!=1)throw new MissingMethodException(name);return ms[0].Invoke(x,args);}
    internal static object[] Items(object? x)=>x is IEnumerable e?e.Cast<object>().ToArray():Array.Empty<object>();
    internal static string ModelId(object x)=>Read(x,"Id").ToString()!;
    private static bool Valid(GodotObject? n)=>n!=null&&GodotObject.IsInstanceValid(n);
    internal static SceneTree GetTree()=>(SceneTree)Engine.GetMainLoop();
    private static IEnumerable<Node> Descendants(Node n){yield return n;foreach(Node c in n.GetChildren())foreach(var d in Descendants(c))yield return d;}
    private Node? VisibleType(string name)=>Descendants(GetTree().Root).SingleOrDefault(n=>Valid(n)&&n.IsNodeReady()&&n.GetType().Name==name&&n is Control c&&c.IsVisibleInTree()&&(name!="NPotionPopup"||!(bool)Read(n,"IsMarkedForRemoval")));
    private static bool Enabled(Node n)=>Valid(n)&&n.IsNodeReady()&&n is Control c&&c.IsVisibleInTree()&&(bool)Read(n,"IsEnabled");
    private static bool IsA(Node n,string name){for(Type? t=n.GetType();t!=null;t=t.BaseType)if(t.Name==name)return true;return false;}
    private static string UiText(Node n)=>string.Join(" ",Descendants(n).OfType<Control>().Where(c=>Valid(c)&&c.IsVisibleInTree()).Select(c=>Prop(c,"Text") as string).Where(s=>!string.IsNullOrEmpty(s)));
    private static Node[] EventButtons(Node n)=>Descendants(n).Where(x=>Valid(x)&&x.IsNodeReady()&&x.GetType().Name=="NEventOptionButton"&&x is Control c&&c.IsVisibleInTree()).ToArray();
    private void Bind(Node n,string id)=>bindings[n.GetInstanceId()]=id;
    private void Add(string id,string kind,string label,object? detail,object? unused=null)=>choices.Add(new(id,kind,label,detail));
    private void AddClick(Node n,string id,string kind,string label,object? detail=null){if(Enabled(n)){Bind(n,id);Add(id,kind,label,detail);}}
    private void AddHolder(Node h,string id,string kind){if(h is not Control c||!c.IsVisibleInTree()||!(bool)Field(h,"_isClickable"))return;var card=Read(h,"CardModel");Bind(h,id);Add(id,kind,Read(card,"Title").ToString()!,new{card=ModelId(card),text=UiText(h),cardDetails=CardDetails(card)});}
    private object Status(object state,object player)
    {
        object creature=Read(player,"Creature");object? pc=Prop(player,"PlayerCombatState");
        return new {act=(int)Read(state,"CurrentActIndex")+1,floor=Read(state,"TotalFloor"),hp=Read(creature,"CurrentHp"),maxHp=Read(creature,"MaxHp"),
            block=Read(creature,"Block"),powers=Items(Read(creature,"Powers")).Where(p=>(bool)Read(p,"IsVisible")).Select(VisiblePower).ToArray(),gold=Read(player,"Gold"),energy=pc is null?0:Read(pc,"Energy"),
            potions=Items(Read(player,"Potions")).Select(ModelId).ToArray(),relics=Items(Read(player,"Relics")).Select(ModelId).ToArray(),deckCards=Items(Read(Read(player,"Deck"),"Cards")).Select((c,index)=>new{index,cardDetails=CardDetails(c)}).ToArray(),deck=Items(Read(Read(player,"Deck"),"Cards")).Select(c=>ModelId(c)+((int)Read(c,"CurrentUpgradeLevel")>0?"+"+Read(c,"CurrentUpgradeLevel"):"")).OrderBy(s=>s).ToArray()};
    }
    private object Snapshot()
    {
        choices.Clear(); bindings.Clear();ReadRenderedCards();var manager=Singleton(0x06000bdf);var state=Invoke(0x06000c30,manager)!;
        if(state==null)return new{phase="game_wait",reason="run_initializing"};
        var players=Items(Read(state,"Players"));if(players.Length==0)return new{phase="game_wait",reason="player_initializing"};
        var player=players.Single();if(Prop(player,"Creature")==null)return new{phase="game_wait",reason="creature_initializing"};
        object status=Status(state,player);
        var transition=Descendants(GetTree().Root).FirstOrDefault(n=>n.GetType().Name=="NTransition");
        if(transition!=null&&(bool)Read(transition,"InTransition"))return new{phase="game_wait",status,reason="original_scene_transition"};
        foreach(string browseType in new[]{"NInspectCardScreen","NDeckViewScreen","NCardPileScreen","NPauseMenu"})
            if(VisibleType(browseType)!=null)return new{phase="ui_browse",status,screen=browseType};
        if((bool)Read(manager,"IsGameOver"))return new {phase="terminal",status,result=(int)Read(Read(player,"Creature"),"CurrentHp")==0?"death":"game_over_unclassified"};
        var modalContainer=Invoke(0x06008741,null);
        var modal=modalContainer==null?null:Invoke(0x06008743,modalContainer) as Node;
        if(modal!=null)throw new InvalidDataException("Unsupported in-run modal: "+modal.GetType().Name);
        var potionPopup=VisibleType("NPotionPopup");
        if(potionPopup!=null && !(bool)Read(potionPopup,"IsMarkedForRemoval"))
        {
            AddClick((Node)Field(potionPopup,"_discardButton"),"discard_potion","discard_potion","Discard selected potion");
            Add("cancel_potion","cancel_potion","Close potion menu",null,null);
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
        if(VisibleType("NCrystalSphereScreen")!=null)
            throw new InvalidDataException("Unsupported decision: crystal_sphere (tool and cell inputs not adapted; hidden board must not be read)");
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
        if(overlays==null)return new{phase="game_wait",status,reason="overlay_stack_initializing"};
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
            var cs=Call(cm,"DebugOnlyGetState");
            if(cs==null)return new{phase="game_wait",status,reason="combat_state_initializing"};
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
                         Add(id,"play",Read(card,"Title")+" → "+Read(target,"Name"),new {card=ModelId(card),cardDetails=CardDetails(card),targetHp=Read(target,"CurrentHp"),target=Read(target,"CombatId")},null);}
                    }
                    else if(targetType is "Self" or "AllEnemies" or "RandomEnemy" or "None" or "AllAllies")
                    {Add("play:"+i,"play",Read(card,"Title").ToString()!,new {card=ModelId(card),cardDetails=CardDetails(card)},null);}
                    else throw new InvalidDataException("Unsupported card target type "+targetType);
                }
                if((bool)Read(player,"CanUseOrRemovePotions"))
                {
                    var slots=Items(Read(player,"PotionSlots"));
                    for(int slot=0;slot<slots.Length;slot++)
                    {
                        var potion=slots[slot];if(potion is null||(bool)Read(potion,"IsQueued")||!(bool)Read(potion,"PassesCustomUsabilityCheck"))continue;
                        string usage=Read(potion,"Usage").ToString()!;if(usage is "Automatic" or "None")continue;
                        Require(usage is "CombatOnly" or "AnyTime","Unsupported potion usage "+usage);
                        string tt=Read(potion,"TargetType").ToString()!;string title=Call(Read(potion,"Title"),"GetFormattedText")!.ToString()!;
                        if(tt is "AnyEnemy" or "AnyAlly" or "AnyPlayer")
                        {
                            foreach(var target in enemies.Concat(new[]{Read(player,"Creature")}))
                            {if(!(bool)Invoke(0x06001b37,potion,target)!)continue;
                             Add($"potion:{slot}:{Read(target,"CombatId")}","potion",title+" → "+Read(target,"Name"),new {potion=ModelId(potion),target=Read(target,"CombatId"),targetHp=Read(target,"CurrentHp")},null);}
                        }
                        else if(tt is "Self" or "None" or "AllEnemies" or "RandomEnemy" or "AllAllies")
                            Add("potion:"+slot,"potion",title,new {potion=ModelId(potion)},null);
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
            foreach(var n in Descendants(treasure).Where(n=>n.GetType().Name=="NTreasureRoomRelicHolder"))
            {if(!Enabled(n))continue;var relic=Prop(n,"Relic");var model=relic==null?null:Field(relic,"_model");if(model!=null)AddClick(n,"claim_relic:"+Read(n,"Index"),"claim_relic","Claim "+ModelId(model),new{relic=ModelId(model),index=Read(n,"Index"),linuxBinding="required"});}
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
                    if(entry!=null)Bind(n,id); // stable identity includes unaffordable attempts
                    if(entry==null||!(bool)Read(entry,"IsStocked")||!(bool)Read(entry,"EnoughGold")||blocked||n is not Control c||!c.IsVisibleInTree())continue;
                    if(n.GetType().Name=="NMerchantPotion"&&!(bool)Read(player,"HasOpenPotionSlots"))continue;
                    Bind(n,id); Add(id,"buy",n.GetType().Name+" "+UiText(n),new {itemType=n.GetType().Name,cost=Read(entry,"Cost")},null);
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
    private static void Require(bool condition,string why){if(!condition)throw new InvalidDataException(why);}
}
