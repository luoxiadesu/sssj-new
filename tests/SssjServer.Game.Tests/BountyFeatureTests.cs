using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class BountyFeatureTests
{
    private static JsonNode Body(FeatureHarness h)
    {
        var p=JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        return p["tbParam"]??p;
    }
    private static Task Join(FeatureHarness h,int diff=1,int key=0)=>h.DispatchLuaCallAsync(50,new JsonObject{["id"]=1,["diff"]=diff,["key"]=key,["nFormationId"]=1}.ToJsonString());
    private static Task Finish(FeatureHarness h,string token,bool win=true,int diff=1)=>h.DispatchLuaCallAsync(252,new JsonObject{
        ["sCmd"]=win?51:52,["tbParam"]=new JsonObject{["nActivityId"]=1,["nDiff"]=diff,["battleToken"]=token,["nGold"]=int.MaxValue}}.ToJsonString());
    private static Task Cancel(FeatureHarness h,string token)=>h.DispatchFunctionAsync("BountyCancel",new JsonObject{["nActivityId"]=1,["nDiff"]=1,["battleToken"]=token}.ToJsonString());
    [Fact]
    public void CollisionSafeConfigLoadsSeventyTwoOrdinaryLevels()
    {
        var table=BountyTable.Load(TestPaths.SettingDir);
        Assert.Equal(72,table.All.Count());
        Assert.Equal(2,table.Get(2,6)!.Fixed.Count);
        Assert.Equal(3,table.Get(2,6)!.Random.Count);
        Assert.Equal(2,table.Get(2,6)!.Daily.Count);
        Assert.Equal(30,table.Get(1,6)!.Cost);
        Assert.Equal(30,table.Get(1,6)!.MasterExp);
        Assert.Equal(26400,table.Get(1,6)!.Fixed[0][4]);
    }
    [Fact]
    public async Task EntryAndVictoryAreIdempotentAndGrantConfiguredRewards()
    {
        using var h=await FeatureHarness.CreateAsync("bounty-win");h.Player.SetMoney(MoneyType.Vigour,100);
        int gold=h.Player.GetMoney(MoneyType.Gold);
        await Join(h);string token=Body(h)["battleToken"]!.GetValue<string>();
        await Join(h);Assert.Equal(token,Body(h)["battleToken"]!.GetValue<string>());Assert.Equal(90,h.Player.Vigour);
        await Finish(h,token);Assert.Equal(gold+18000,h.Player.GetMoney(MoneyType.Gold));
        string reply=h.LastOutbound.Json;
        await Finish(h,token);Assert.Equal(reply,h.LastOutbound.Json);Assert.Equal(gold+18000,h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(1,h.Player.GetTask(9,101));
        var saved=new PlayerStore(h.Options).Load(h.Player.Account)!;Assert.Equal(gold+18000,saved.GetMoney(MoneyType.Gold));
        Assert.Equal(1,saved.GetTask(9,101));
    }
    [Fact]
    public async Task ExitRefundsOnceAndOldAttemptCannotSettleNewOne()
    {
        using var h=await FeatureHarness.CreateAsync("bounty-refund");h.Player.SetMoney(MoneyType.Vigour,100);
        await Join(h);string token=Body(h)["battleToken"]!.GetValue<string>();
        await Cancel(h,token);await Cancel(h,token);Assert.Equal(100,h.Player.Vigour);
        await Join(h);string next=Body(h)["battleToken"]!.GetValue<string>();Assert.NotEqual(token,next);
        await Finish(h,token);Assert.Equal(6,Body(h)["nError"]!.GetValue<int>());
        await Cancel(h,token);Assert.Equal(90,h.Player.Vigour);
        await Finish(h,next);Assert.Equal(0,Body(h)["nError"]!.GetValue<int>());
    }
    [Fact]
    public async Task DefeatCannotGrantRewardOrThenBecomeVictory()
    {
        using var h=await FeatureHarness.CreateAsync("bounty-fail");h.Player.SetMoney(MoneyType.Vigour,100);int gold=h.Player.GetMoney(MoneyType.Gold);
        await Join(h);string token=Body(h)["battleToken"]!.GetValue<string>();
        await Finish(h,token,false);await Finish(h,token,false);await Finish(h,token,true);
        Assert.Equal(6,Body(h)["nError"]!.GetValue<int>());Assert.Equal(gold,h.Player.GetMoney(MoneyType.Gold));Assert.Equal(0,h.Player.GetTask(9,101));
    }
    [Theory]
    [InlineData(6,0,1)]
    [InlineData(1,1,2)]
    public async Task LockedDifficultyAndUnrestoredKeyModeDoNotSpend(int diff,int key,int error)
    {
        using var h=await FeatureHarness.CreateAsync("bounty-locked");h.Player.SetMoney(MoneyType.Vigour,100);
        await Join(h,diff,key);Assert.Equal(error,Body(h)["nError"]!.GetValue<int>());Assert.Equal(100,h.Player.Vigour);
    }
    [Theory]
    [InlineData(4,17,1)]
    [InlineData(2,17,0)]
    [InlineData(4,1,0)]
    public void BountyPledgeMatchesEventTypeAndPledgeOutfit(int eventType,int outfit,int expected)
    {
        var player=new Player("bounty-pledge",1);
        var state=new PledgeState();var girl=new PledgeGirlState{GirlId=3,Step=1};
        girl.Tasks.Add(new PledgeTaskState{Id=331});state.Girls[3]=girl;
        player.Extra[PledgeFeature.ExtraKey]=state.ToJson();
        ChapterCombatProgress.RecordBounty(MissionTable.Load(TestPaths.SettingDir),PledgeTable.Load(TestPaths.SettingDir),
            player,eventType,6,new[]{new BattleMainCard(1,1,3,(uint)outfit,3,0)},new Dictionary<int,int>{{3,1}});
        Assert.Equal(expected,PledgeFeature.LoadStateStatic(player).Girls[3].Tasks[0].Count);
    }

    [Theory]
    [InlineData(54,6,4,0)]
    [InlineData(55,6,1,0)]
    [InlineData(55,6,4,1)]
    public async Task RefitBattleConditionUsesExactEntryCardAndPredecessor(int level,int girl,int outfit,int expected)
    {
        using var h=await FeatureHarness.CreateAsync($"bounty-refit-{level}-{outfit}");h.Player.Level=level;h.Player.SetMoney(MoneyType.Vigour,100);h.Player.SetTask(9,101,5);
        h.Player.Items.Add(new Item{Guid=9999,Genre=1,Detail=(uint)girl,Particular=(uint)outfit,Level=3,Count=1,EnhanceLevel=1});
        var team=h.Player.Formations.First(f=>f.Id==1);team.Slots.Clear();team.Slots.Add(new FormationCardSlot{MainCardGuid=9999});
        await Join(h,6);string token=Body(h)["battleToken"]!.GetValue<string>();team.Slots.Clear();
        await Finish(h,token,true,6);await Finish(h,token,true,6);
        Assert.Equal(expected,MissionFeature.ProgressOf(h.Player.GetTask(18,2292)));
    }
}
