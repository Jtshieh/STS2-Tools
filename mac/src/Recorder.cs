// Passive hooks adapted from RunReplays b0d2302ee69bf2ad735e0b6b51aea02408e9ef62 (MIT).
// See ATTRIBUTION.md. Never PatchAll, dispatch inputs, mutate rules, or query RNG.
using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using File=System.IO.File;
using FileAccess=System.IO.FileAccess;
using Environment=System.Environment;
namespace Sts2Recorder;
[ModInitializer(nameof(Initialize))]
public static class Entry
{
    internal static Observer? Recorder;
    public static void Initialize()
    {
        try {Recorder=new Observer();Recorder.Install();}
        catch(Exception e){GD.PrintErr("RECORDER FAILED: "+e); var root=Environment.GetEnvironmentVariable("STS2_RECORDER_LOG");root??=ProjectSettings.GlobalizePath("user://sts2-recorder");Directory.CreateDirectory(root);File.AppendAllText(Path.Combine(root,"RECORDER-FAILED.txt"),e.ToString());}
    }
}
public sealed partial class Observer
{
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    private readonly object gate=new();
    private readonly FileStream stream;
    private readonly string logRoot,processId,sessionId;
    private string? rootRun;
    private long eventSequence,actionSequence,decisionSequence,roomEpoch;
    private readonly Dictionary<object,List<long>> purchasePending=new(ReferenceEqualityComparer.Instance);
    private readonly Stopwatch timer=Stopwatch.StartNew();
    private double nextPoll,lastChange;
    private string? signature,lastEmittedSignature,lastError;
    private object? snapshot;
    private bool hadRun,dirty,everIncomplete,resumedSegment;
    private Label? label;
    private object? lastRunState;
    private readonly Dictionary<long,ActionRecord> active=new();
    private readonly Dictionary<GameAction,long> executing=new(ReferenceEqualityComparer.Instance);
    private readonly List<long> awaiting=new();
    private long notificationSequence;
    private readonly Dictionary<long,long> notificationParents=new();
    internal sealed class ActionRecord {public long Seq;public string Id="",Kind="";public double Started;public bool Nested;public bool HasSuccessor;public bool Delivered,Rejected,NestedResolved,OutcomePending;public string Before="",StartPhase="",StartContext="",NestedContext="",NestedObservation="";public long RoomEpoch;}
    internal Observer()
    {
        processId=Environment.GetEnvironmentVariable("STS2_PROCESS_ID")??DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ")+"-"+Guid.NewGuid().ToString("N")[..8];sessionId=processId;
        logRoot=Environment.GetEnvironmentVariable("STS2_RECORDER_LOG")??ProjectSettings.GlobalizePath("user://sts2-recorder/logs/"+processId);
        var expected=Environment.GetEnvironmentVariable("STS2_EXPECTED_USERDATA");
        string gameHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Game.Location))).ToLowerInvariant();
        if(gameHash!="9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4")throw new InvalidDataException("Unsupported game assembly: recorder expects macOS arm64 v0.111.0 / 41cef1ea");
        Directory.CreateDirectory(logRoot);
        if(OperatingSystem.IsMacOS())File.SetUnixFileMode(logRoot,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        stream=new FileStream(Path.Combine(logRoot,"actions.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read,4096,FileOptions.WriteThrough);
        Write("recorder_initialized",new{schema="sts2-gui-semantic-v1",recorderRevision="0.2.4",attributionRevision="callback-scope-v1",observationRevision="card-state-v2",recorderSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Entry).Assembly.Location))).ToLowerInvariant(),upstream="RunReplays@b0d2302ee69bf2ad735e0b6b51aea02408e9ef62",runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),assemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Game.Location))).ToLowerInvariant(),userData=ProjectSettings.GlobalizePath("user://"),cwd=Environment.CurrentDirectory,launchMode=expected==null?"drop_in_mod":"isolated_launcher",userDataIsolationVerified=expected!=null,slVerified=false});
        if(expected!=null&&Path.GetFullPath(ProjectSettings.GlobalizePath("user://")).TrimEnd('/')!=Path.GetFullPath(expected).TrimEnd('/'))throw new InvalidDataException("User-data isolation mismatch");
        GD.Print("STS2 RECORDER LOGS: "+logRoot);
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{try{Write("process_exit",new{continuity="interrupted_or_unverified",pending=active.Keys.ToArray()});stream.Dispose();}catch{}};
    }
    internal void Write(string kind,object? data,long? action=null)
    {
        lock(gate)
        {
            try {var row=new{schema="sts2-gui-semantic-v1",kind,utc=DateTimeOffset.UtcNow,elapsedSeconds=timer.Elapsed.TotalSeconds,sessionId,processRunId=processId,rootRunId=rootRun,eventSequence=++eventSequence,actionSequence=action,decisionId=decisionSequence,data};var bytes=System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row,Json)+"\n");stream.Write(bytes);stream.Flush(true);}
            catch(Exception e){everIncomplete=true;GD.PrintErr("RECORDER WRITE FAILED: "+e.Message);throw;}
        }
    }
    internal void Error(string kind,Exception e){everIncomplete=true;var message=e.GetBaseException().Message;if(message!=lastError){lastError=message;try{Write(kind,new{error=e.GetBaseException().ToString(),trajectory="incomplete"});File.AppendAllText(Path.Combine(logRoot,"RECORDER-FAILED.txt"),kind+": "+message+"\n");}catch{}}}
    private static MethodInfo Method(string type,string name)=>Game.GetTypes().Single(t=>t.Name==type).GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).Single(m=>m.Name==name);
    private void Patch(Harmony h,string type,string name,string? prefix=null,string? postfix=null,string? finalizer=null)
    {
        var m=Method(type,name);HarmonyMethod? P(string? s)=>s==null?null:new HarmonyMethod(typeof(Hooks),s);
        h.Patch(m,P(prefix),P(postfix),null,P(finalizer));Write("hook_registered",new{type,name,token=m.MetadataToken,prefix,postfix,finalizer});
    }
    internal void Install()
    {
        var h=new Harmony("owner.sts2.passive.recorder");
        Patch(h,"NClickableControl","OnReleaseHandler",nameof(Hooks.ClickPrefix),nameof(Hooks.Delivered),nameof(Hooks.ClickFault));
        Patch(h,"NCardHolder","EmitPressed",nameof(Hooks.HolderPrefix),nameof(Hooks.Delivered),nameof(Hooks.Fault));
        Patch(h,"NCardHolder","OnMouseReleased",nameof(Hooks.HolderReleasePrefix),nameof(Hooks.Delivered),nameof(Hooks.Fault));
        Patch(h,"NPlayerHand","SelectCardInSimpleMode",nameof(Hooks.HandSemanticPrefix),nameof(Hooks.HandApplied),nameof(Hooks.Fault));
        Patch(h,"NPlayerHand","SelectCardInUpgradeMode",nameof(Hooks.HandSemanticPrefix),nameof(Hooks.HandApplied),nameof(Hooks.Fault));
        Patch(h,"NTreasureRoomRelicCollection","PickRelic",nameof(Hooks.TreasurePrefix),nameof(Hooks.SelectionAccepted),nameof(Hooks.Fault));
        Patch(h,"CardModel","EnqueueManualPlay",nameof(Hooks.CardPrefix),nameof(Hooks.Accepted),nameof(Hooks.Fault));
        Patch(h,"PotionModel","EnqueueManualUse",nameof(Hooks.PotionPrefix),nameof(Hooks.Accepted),nameof(Hooks.Fault));
        Patch(h,"MerchantEntry","OnTryPurchaseWrapper",nameof(Hooks.BuyPrefix),nameof(Hooks.Delivered),nameof(Hooks.Fault));
        Patch(h,"MerchantCardRemovalEntry","OnTryPurchaseWrapper",nameof(Hooks.BuyPrefix),nameof(Hooks.Delivered),nameof(Hooks.Fault));
        Patch(h,"PlayerChoiceSynchronizer","SyncLocalChoice",postfix:nameof(Hooks.ChoicePrefix));
        Patch(h,"EventSynchronizer","ChooseLocalOption",nameof(Hooks.EventPrefix),nameof(Hooks.Accepted),nameof(Hooks.Fault));
        Patch(h,"RestSiteSynchronizer","ChooseLocalOption",nameof(Hooks.RestPrefix),nameof(Hooks.Accepted),nameof(Hooks.Fault));
        Patch(h,"MerchantEntry","InvokePurchaseCompleted",postfix:nameof(Hooks.PurchaseCompleted));
        Patch(h,"MerchantEntry","InvokePurchaseFailed",postfix:nameof(Hooks.PurchaseFailed));
        Patch(h,"ActChangeSynchronizer","SetLocalPlayerReady",nameof(Hooks.ActPrefix),nameof(Hooks.ActAccepted),nameof(Hooks.ActFault));
        Patch(h,"RunManager","SetUpSavedSingleplayer",nameof(Hooks.LoadPrefix));
        var ctor=typeof(ActionExecutor).GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Single(c=>c.GetParameters().Length==1&&c.GetParameters()[0].ParameterType==typeof(ActionQueueSet));
        h.Patch(ctor,postfix:new HarmonyMethod(typeof(Hooks),nameof(Hooks.Executor)));Write("hook_registered",new{type="ActionExecutor",name=".ctor",token=ctor.MetadataToken});
        RunManager.Instance.RoomEntered+=()=>{roomEpoch++;dirty=true;try{Write("room_entered",new{roomEpoch,source="engine lifecycle; not player input"});}catch(Exception e){Error("recording_error",e);}};
        GetTree().ProcessFrame+=Tick;
        Write("ready",new{mode="passive",automaticInput=false,shortHumanTimeout=false,coverage="hooks installed; natural gameplay coverage unverified"});
    }
    private object Observe()
    {
        object state=Snapshot();return new{state,actions=choices.Select(c=>new{id=c.Id,kind=c.Kind,label=c.Label,detail=c.Detail}).ToArray()};
    }
    private void Tick()
    {
        if(timer.Elapsed.TotalSeconds<nextPoll)return;nextPoll=timer.Elapsed.TotalSeconds+0.15;
        try
        {
            if(label==null){label=new Label{Text="RECORDER: boot",MouseFilter=Control.MouseFilterEnum.Ignore,Position=new Vector2(10,8),ZIndex=4095};label.AddThemeFontSizeOverride("font_size",16);GetTree().Root.AddChild(label);}
            var state=Prop(RunManager.Instance,"State");
            if(state==null)
            {
                if(hadRun){Write("continuity_boundary",new{reason="returned_to_menu",continuity="interrupted",pending=active.Keys.ToArray()});active.Clear();awaiting.Clear();purchasePending.Clear();rootRun=null;hadRun=false;}
                label.Text="RECORDER READY | 日志: "+logRoot;return;
            }
            if(!ReferenceEquals(lastRunState,state))
            {
                if(hadRun)Write("continuity_boundary",new{reason="run_state_replaced",continuity="unverified"});
                lastRunState=state;rootRun=Guid.NewGuid().ToString("N");hadRun=true;active.Clear();awaiting.Clear();signature=null;lastEmittedSignature=null;
                var player=Items(Read(state,"Players")).Single();
                Write("run_started",new{character=ModelId(Read(player,"Character")),ascension=Read(state,"AscensionLevel"),playerCount=Items(Read(state,"Players")).Length,continuity=resumedSegment?"resumed_segment_unverified":"new_segment",progression="copied owner profile; no fabricated unlocks"});
                // Seed and full game state are deliberately excluded from player observations.
                var seed=Read(Read(state,"Rng"),"StringSeed").ToString();
                File.WriteAllText(Path.Combine(logRoot,"replay-input.private.json"),JsonSerializer.Serialize(new{rootRunId=rootRun,seed,version="v0.111.0",commit="41cef1ea",platform="macos-arm64",initialization="original GUI Standard; verify profile and unlock parity on Linux"},Json));
            }
            snapshot=Observe();var serialized=JsonSerializer.Serialize(snapshot,Json);
            if(serialized!=signature){signature=serialized;lastChange=timer.Elapsed.TotalSeconds;if(Phase(snapshot)=="game_wait")Write("observation_wait",new{observation=snapshot,waiting="game_transition_or_unclassified",notPlayerAction=true});}
            string phase=JsonSerializer.SerializeToElement(snapshot,Json).GetProperty("state").GetProperty("phase").GetString()!;
            if(phase!="game_wait"&&(timer.Elapsed.TotalSeconds-lastChange>=0.25)&&(serialized!=lastEmittedSignature||dirty))
            {
                decisionSequence++;Write("observation",new{observation=snapshot,wait="human",coverage=everIncomplete?"incomplete":"unverified"});lastEmittedSignature=serialized;dirty=false;
                Settle(snapshot,false);
            }
            label.Text=$"RECORDER {(everIncomplete?"INCOMPLETE":"ON")} | 动作 {actionSequence} | {phase} | 人工等待不限时";
            File.WriteAllText(Path.Combine(logRoot,"live-status.json"),JsonSerializer.Serialize(new{processId,rootRun,actions=actionSequence,phase,incomplete=everIncomplete,waiting=phase=="game_wait"?"game_resolution":"human",pending=active.Keys.ToArray()},Json));
        }
        catch(Exception e){Error("observer_error",e);if(label!=null)label.Text="RECORDER INCOMPLETE — 查看日志；游戏未被终止";}
    }
    internal long Begin(string id,string source,object? detail=null,bool reuse=false,bool knownAttempt=false)
    {
        try
        {
            if(reuse){var pending=active.Values.LastOrDefault(a=>a.Id==id&&!a.HasSuccessor);if(pending!=null)return pending.Seq;}
            object? before=snapshot;try{before=Observe();}catch(Exception e){Error("observer_error_at_input",e);}var choice=choices.SingleOrDefault(c=>c.Id==id);
            if(before!=null)Settle(before,true);
            long seq=++actionSequence;
            var a=new ActionRecord{Seq=seq,Id=id,Kind=choice?.Kind??id.Split(':')[0],Started=timer.Elapsed.TotalSeconds,
                Before=JsonSerializer.Serialize(before,Json),StartPhase=Phase(before),StartContext=DecisionContext(),RoomEpoch=roomEpoch};
            var parent=active.Values.LastOrDefault(x=>x.Nested)?.Seq;active[seq]=a;awaiting.Add(seq);dirty=true;
            if(choice==null&&!knownAttempt){everIncomplete=true;Write("unmapped_action",new{actionId=id,source,detail,reason="not in current Linux-compatible legal choices",trajectory="incomplete"},seq);}
            if(choice==null&&knownAttempt)Write("unavailable_attempt",new{actionId=id,source,detail,available=false,meaning="preserved human attempt; await original rejection or acceptance"},seq);
            Write("action_initiated",new{actionId=id,kind=a.Kind,source,detail,parentActionSequence=parent,role=parent==null?"player_input":"nested_input",choice,observation=before},seq);return seq;
        }
        catch(Exception e){Error("recording_error",e);return 0;}
    }
    internal long BeginAct(long parent)
    {
        // Only the currently executing original click callback proves synchronous ownership.
        // A pending nested choice or a nearby observation is not a callback parent.
        if(parent<=0||!active.TryGetValue(parent,out var a)||a.Id!="proceed"||a.HasSuccessor)
            return Begin("next_act","RunReplays.ActChangeSynchronizer.SetLocalPlayerReady");
        long id=++notificationSequence;notificationParents.Add(id,parent);
        ActNotification(id,parent,"callback_entered",null);return -id;
    }
    private void ActNotification(long id,long parent,string status,Exception? error)
        =>Write("engine_notification",new{notificationSequence=id,actionId="next_act",role="engine_notification",
            source="RunReplays.ActChangeSynchronizer.SetLocalPlayerReady",attributionRevision="callback-scope-v1",
            relation="synchronous_callback",parentActionSequence=parent,status,error=error?.ToString()});
    internal void EndAct(long state,Exception? error)
    {
        if(state>=0){State(state,error==null?"accepted_into_original_queue":"failed",error);return;}
        long id=-state;
        if(!notificationParents.Remove(id,out long parent))throw new InvalidDataException("Missing internal notification parent");
        ActNotification(id,parent,error==null?"callback_returned":"failed",error);
        if(error!=null)everIncomplete=true;
    }
    internal long Bound(Node n,string source)
    {
        try
        {
            if(Prop(RunManager.Instance,"State")==null){Write("menu_input",new{type=n.GetType().Name,node=n.Name.ToString(),source});return 0;}
            // Hand selection is captured at its original semantic consumer, not duplicate mouse/key wrappers.
            if(IsA(n,"NHandCardHolder")&&source!="NPlayerHand.SelectCard")return 0;
            if(n.GetType().Name=="NTreasureRoomRelicHolder"&&source=="NClickableControl.OnReleaseHandler")return 0;
            Observe();
            if(source=="NClickableControl.OnReleaseHandler")
                for(Node? parent=n.GetParent();parent!=null;parent=parent.GetParent())if(IsA(parent,"NCardHolder"))return 0;
            if(bindings.TryGetValue(n.GetInstanceId(),out var id))return Begin(id,source,new{node=n.GetPath().ToString(),type=n.GetType().Name});
            // Combat hand presses select/drag a card, not yet manual play; don't double-record them.
            if(IsA(n,"NHandCardHolder")&&VisibleType("NPlayerHand") is Node hand&&!(bool)Read(hand,"IsInCardSelection"))return 0;
            Write("unmapped_ui_input",new{type=n.GetType().Name,node=n.GetPath().ToString(),source,classification="unverified UI input; may be presentation only"});
            return 0;
        }
        catch(Exception e){Error("recording_error",e);return 0;}
    }
    internal long Card(CardModel card,Creature? target)
    {
        var hand=card.Owner.PlayerCombatState!.Hand.Cards;int index=hand.ToList().IndexOf(card);
        return Begin("play:"+index+(target==null?"":":"+target.CombatId),"RunReplays.CardModel.EnqueueManualPlay",new{card=card.Id.ToString(),cardDetails=CardDetails(card),handIndex=index,targetCombatId=target?.CombatId,cardInstance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(card)});
    }
    internal long Potion(PotionModel potion,Creature? target)
    {
        int slot=Items(Read(potion.Owner,"PotionSlots")).ToList().FindIndex(x=>ReferenceEquals(x,potion));
        return Begin("potion:"+slot+(target==null?"":":"+target.CombatId),"PotionModel.EnqueueManualUse",new{potion=potion.Id.ToString(),slot,targetCombatId=target?.CombatId});
    }
    internal long CurrentSemantic(string kind,string source)
    {
        var a=active.Values.LastOrDefault(x=>x.Kind==kind);
        if(a!=null)return a.Seq;
        Error("unmatched_semantic_callback",new InvalidDataException(source+": no active "+kind+" input"));return 0;
    }
    internal long Buy(object entry)
    {
        try
        {
            if(purchasePending.TryGetValue(entry,out var existing))
            {
                var inner=existing.LastOrDefault(n=>active.TryGetValue(n,out var a)&&!a.Delivered);
                if(inner!=0)return inner; // override calling base within this one wrapper invocation
            }
            Observe();var slot=Descendants(GetTree().Root).Single(n=>IsA(n,"NMerchantSlot")&&ReferenceEquals(Prop(n,"Entry"),entry));
            if(!bindings.TryGetValue(slot.GetInstanceId(),out var id))throw new InvalidDataException("Merchant slot has no stable index");
            var seq=Begin(id,"RunReplays.MerchantEntry.OnTryPurchaseWrapper",new{itemType=entry.GetType().Name,cost=Read(entry,"Cost"),enoughGold=Read(entry,"EnoughGold"),stocked=Read(entry,"IsStocked"),visibleItem=UiText(slot)},knownAttempt:true);
            if(seq!=0){if(!purchasePending.TryGetValue(entry,out var list))purchasePending[entry]=list=new();list.Add(seq);active[seq].OutcomePending=true;}
            return seq;
        }catch(Exception e){Error("recording_error",e);return 0;}
    }
    internal void PurchaseOutcome(object entry,string status,object? reason)
    {
        if(!purchasePending.TryGetValue(entry,out var list)||list.Count!=1)
        {Error("unmatched_purchase_outcome",new InvalidDataException("Purchase outcome has zero or ambiguous originating attempts"));return;}
        long seq=list[0];purchasePending.Remove(entry);
        if(active.TryGetValue(seq,out var a)){a.Rejected=status=="purchase_rejected";a.OutcomePending=false;}
        Write("purchase_result",new{status,reason=reason?.ToString(),source="original merchant result; never a new input"},seq);State(seq,status);
    }
    internal void State(long seq,string state,Exception? fault=null)
    {
        if(seq==0)return;try{if(active.TryGetValue(seq,out var a)){a.Delivered=true;if(fault!=null)a.OutcomePending=false;}Write("action_status",new{status=state,error=fault?.ToString()},seq);if(fault!=null)everIncomplete=true;dirty=true;}catch(Exception e){Error("recording_error",e);}
    }
    private static string Phase(object? observation)=>observation==null?"game_wait":JsonSerializer.SerializeToElement(observation,Json).GetProperty("state").GetProperty("phase").GetString()!;
    private string DecisionContext()
    {
        var node=Descendants(GetTree().Root).FirstOrDefault(n=>n is Control c&&c.IsVisibleInTree()&&
            (IsA(n,"NCardGridSelectionScreen")||n.GetType().Name is "NCardRewardSelectionScreen" or "NChooseACardSelectionScreen"||n.GetType().Name=="NPlayerHand"&&(bool)Read(n,"IsInCardSelection")));
        return node==null?"":node.GetType().Name+":"+node.GetInstanceId();
    }
    private void Settle(object observation,bool nextInput)
    {
        string phase=Phase(observation),current=JsonSerializer.Serialize(observation,Json),context=DecisionContext();
        if(phase=="game_wait")return;
        foreach(var old in active.Values.Where(a=>a.Nested&&a.HasSuccessor&&BoundaryRules.CloseNested(a.NestedResolved,a.NestedContext,context,a.NestedObservation,current,nextInput)).ToArray())
        {Write("nested_action_closed",new{status=old.NestedResolved?"choice_accepted_and_decision_advanced":"nested_ui_left",observation},old.Seq);active.Remove(old.Seq);}
        int options=JsonSerializer.SerializeToElement(observation,Json).GetProperty("actions").GetArrayLength();
        foreach(var seq in awaiting.ToArray())
        {
            if(!active.TryGetValue(seq,out var a))continue;
            if(!BoundaryRules.CanSettle(a.Id,a.Before,current,phase,options,a.Delivered,a.Rejected,a.OutcomePending,a.RoomEpoch,roomEpoch,nextInput))continue;
            // Selecting within an existing nested screen does not become a new parent.
            bool nested=BoundaryRules.StartsNested(phase,a.StartContext,context);
            a.Nested=nested;a.NestedContext=context;a.NestedObservation=current;a.HasSuccessor=true;
            Write("action_successor",new{actionId=a.Id,status=nested?"entered_nested_decision":a.Rejected?"rejected_no_game_action":nextInput?"next_input_boundary":"next_decision_observed",observation,mechanicalCompletion="original lifecycle evidence; cross-platform comparison still required"},seq);
            awaiting.Remove(seq);if(!nested)active.Remove(seq);
        }
    }
    internal void HandSelectionApplied(object hand,long seq)
    {
        if(seq==0)return;
        var selected=Items(Field(hand,"_selectedCards")).Select(c=>new{card=ModelId(c),upgrade=Prop(c,"CurrentUpgradeLevel"),instance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c),cardDetails=CardDetails(c)}).ToArray();
        Write("selection_input_applied",new{selected,source="original NPlayerHand selection state; no synthetic input",meaning="actual game-selected cards immediately after the semantic operation"},seq);
    }
    internal void ChoiceAccepted(Player player,uint id,PlayerChoiceResult result)
    {
        try
        {
            // AsNet* conversions may allocate/register identities; read only the actual finalized fields.
            var fields=result.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Where(f=>f.FieldType.IsPrimitive||f.FieldType.IsEnum).ToDictionary(f=>f.Name,f=>f.GetValue(result)?.ToString());
            var selected=new Dictionary<string,object?>();
            foreach(string field in new[]{"_canonicalCards","_combatCards","_deckCards","_mutableCards"})
                selected[field]=Items(Field(result,field)).Select(c=>new{id=ModelId(c),upgrade=Prop(c,"CurrentUpgradeLevel"),instance=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c),cardDetails=CardDetails(c)}).ToArray();
            selected["indexes"]=Items(Field(result,"_indexes"));
            var current=active.Values.LastOrDefault(a=>a.Nested);
            if(current!=null)current.NestedResolved=true;
            Write("nested_choice_accepted",new{choiceId=id,type=result.ChoiceType.ToString(),fields,selected,parentActionSequence=current?.Seq,related=active.Keys.ToArray()});dirty=true;
        }catch(Exception e){Error("recording_error",e);}
    }
    internal void Executor(ActionExecutor ex)
    {
        ex.BeforeActionExecuted+=action=>{try{Write("game_action_started",new{type=action.GetType().Name,source="engine outcome, not a new player input"});var a=active.Values.LastOrDefault(x=>!x.HasSuccessor);if(a!=null)executing[action]=a.Seq;}catch(Exception e){Error("recording_error",e);}};
        ex.AfterActionExecuted+=action=>{try{long? seq=executing.Remove(action,out long n)?n:null;Write("game_action_completed",new{type=action.GetType().Name,source="engine outcome; never replay as extra player input"},seq);dirty=true;}catch(Exception e){Error("recording_error",e);}};
    }
    internal void Interrupted(string reason){resumedSegment=true;Write("continuity_boundary",new{reason,continuity="unverified",pending=active.Keys.ToArray()});active.Clear();awaiting.Clear();purchasePending.Clear();rootRun=null;lastRunState=null;hadRun=false;}
}
public static class Hooks
{
    private static Observer? R=>Entry.Recorder;
    [ThreadStatic] private static Stack<long>? clickScopes;
    public static void EventPrefix(int index,out long __state)=>__state=Safe(()=>R?.Begin("event:"+index,"RunReplays.EventSynchronizer.ChooseLocalOption",reuse:true)??0);
    public static void RestPrefix(out long __state)=>__state=Safe(()=>R?.CurrentSemantic("rest","RunReplays.RestSiteSynchronizer.ChooseLocalOption")??0);
    public static void PurchaseCompleted(object __instance)=>Safe(()=>R?.PurchaseOutcome(__instance,"purchase_completed",null));
    public static void PurchaseFailed(object __instance,object __0)=>Safe(()=>R?.PurchaseOutcome(__instance,"purchase_rejected",__0));
    private static long Safe(Func<long> fn){try{return fn();}catch(Exception e){try{R?.Error("hook_error",e);}catch{}return 0;}}
    private static void Safe(Action fn){try{fn();}catch(Exception e){try{R?.Error("hook_error",e);}catch{}}}
    public static void ClickPrefix(object __instance,out long __state)
    {
        __state=Safe(()=>(bool)Observer.Field(__instance,"_isPressed")?(R?.Bound((Node)__instance,"NClickableControl.OnReleaseHandler")??0):0);
        (clickScopes??=new()).Push(__state);
    }
    public static void ClickFault(Exception? __exception,long __state)
    {
        try{Fault(__exception,__state);}
        finally{if(clickScopes?.Count>0)clickScopes.Pop();}
    }
    public static void HolderPrefix(object __instance,out long __state)=>__state=Safe(()=>R?.Bound((Node)__instance,"NCardHolder.EmitPressed")??0);
    public static void HolderReleasePrefix(object __instance,InputEvent __0,out long __state)=>__state=Safe(()=>
        __0 is InputEventMouseButton mouse&&mouse.ButtonIndex==MouseButton.Left&&Observer.Prop(__instance,"CardNode")!=null&&
        (bool)Observer.Field(__instance,"_isHovered")&&(bool)Observer.Field(__instance,"_isClickable")&&
        Observer.Field(__instance,"_currentPressedAction") is InputEventMouseButton pressed&&pressed.ButtonIndex==mouse.ButtonIndex
        ?R?.Bound((Node)__instance,"NCardHolder.OnMouseReleased")??0:0);
    public static void HandSemanticPrefix(Node __0,out long __state)=>__state=Safe(()=>R?.Bound(__0,"NPlayerHand.SelectCard")??0);
    public static void HandApplied(object __instance,long __state)=>Safe(()=>{R?.State(__state,"selection_input_applied");R?.HandSelectionApplied(__instance,__state);});
    public static void TreasurePrefix(object __instance,Node __0,out long __state)=>__state=Safe(()=>
        Godot.Time.GetTicksMsec()-(ulong)Observer.Field(__instance,"_openedTicks")>200
        ?R?.Bound(__0,"NTreasureRoomRelicCollection.PickRelic")??0:0);
    public static void SelectionAccepted(long __state)=>Safe(()=>R?.State(__state,"accepted_original_selection"));
    public static void CardPrefix(CardModel __instance,Creature? target,out long __state)=>__state=Safe(()=>R?.Card(__instance,target)??0);
    public static void PotionPrefix(PotionModel __instance,Creature? target,out long __state)=>__state=Safe(()=>R?.Potion(__instance,target)??0);
    public static void BuyPrefix(object __instance,out long __state)=>__state=Safe(()=>R?.Buy(__instance)??0);
    public static void ActPrefix(out long __state)=>__state=Safe(()=>R?.BeginAct(clickScopes?.Count>0?clickScopes.Peek():0)??0);
    public static void ActAccepted(long __state)=>Safe(()=>R?.EndAct(__state,null));
    public static void ActFault(Exception? __exception,long __state){if(__exception!=null)Safe(()=>R?.EndAct(__state,__exception));}
    public static void ChoicePrefix(Player player,uint choiceId,PlayerChoiceResult result)=>Safe(()=>R?.ChoiceAccepted(player,choiceId,result));
    public static void LoadPrefix()=>Safe(()=>R?.Interrupted("SetUpSavedSingleplayer: resumed segment, SL not verified"));
    public static void Delivered(long __state)=>Safe(()=>R?.State(__state,"callback_returned"));
    public static void Accepted(long __state)=>Safe(()=>R?.State(__state,"accepted_into_original_queue"));
    public static void Fault(Exception? __exception,long __state){if(__exception!=null)Safe(()=>R?.State(__state,"failed",__exception));}
    public static void Executor(ActionExecutor __instance)=>Safe(()=>R?.Executor(__instance));
}
