using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
namespace Sts2Recorder;

// Only call for owner-visible cards. Never enumerate combat AllCards or draw order.
// Object identity is recorder-local and must be mapped, not compared across processes.
public sealed class CardProjection
{
    private sealed record Identity(long Value);
    private readonly ConditionalWeakTable<object,Identity> identities=new();
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
