namespace Sts2Recorder;
// Pure ordering policy, tested using the retained human trace. No game access.
public static class BoundaryRules
{
    public static bool IsNested(string phase)=>phase is "grid_selection" or "hand_selection" or "choose_card" or "card_reward";
    public static bool CloseNested(bool accepted,string openedContext,string nowContext,string openedObservation,string nowObservation,bool nextInput)
        =>accepted&&(openedContext!=nowContext||nextInput||openedObservation!=nowObservation);
    public static bool StartsNested(string phase,string beforeContext,string nowContext)=>IsNested(phase)&&beforeContext!=nowContext;
    public static bool CanSettle(string actionId,string before,string current,string phase,int options,bool delivered,bool rejected,bool outcomePending,long startedRoom,long currentRoom,bool nextInput)
    {
        if(!delivered||phase=="game_wait"||(options==0&&phase!="terminal"))return false;
        if(actionId.StartsWith("map:")&&currentRoom<=startedRoom)return false;
        if(outcomePending&&!IsNested(phase))return false;
        if(before==current&&!nextInput&&!rejected)return false;
        return true;
    }
}
