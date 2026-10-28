using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;
public class BountyRewardRulesTests
{
    private static readonly BountyTable Table=BountyTable.Load(TestPaths.SettingDir);
    private static long At(string local)=>DateTimeOffset.Parse(local+"+08:00").ToUnixTimeSeconds();
    [Fact]
    public void DailyBonusSharesCategoryAndRefreshesAtFourWithoutRefillingLegacyCounts()
    {
        var player=new Player("bounty-daily",1);var changes=new List<KeyValuePair<uint,int>>();
        Assert.True(BountyDaily.Refresh(player,Table,At("2026-10-26T03:59:59"),changes));
        Assert.Equal(2,player.GetTask(9,4002));
        Assert.True(BountyDaily.Consume(player,Table.Get(2,1)!,changes));
        Assert.True(BountyDaily.Consume(player,Table.Get(3,1)!,changes));
        Assert.False(BountyDaily.Consume(player,Table.Get(4,1)!,changes));
        Assert.False(BountyDaily.Refresh(player,Table,At("2026-10-26T03:59:59"),changes));
        Assert.Equal(0,player.GetTask(9,4002));
        Assert.True(BountyDaily.Refresh(player,Table,At("2026-10-26T04:00:00"),changes));
        Assert.Equal(2,player.GetTask(9,4002));
        Assert.False(BountyDaily.Refresh(player,Table,At("2026-10-25T04:00:00"),changes));
        var legacy=new Player("legacy-bounty",2);legacy.SetTask(9,4002,0);
        BountyDaily.Refresh(legacy,Table,At("2026-10-26T12:00:00"),new());Assert.Equal(0,legacy.GetTask(9,4002));
    }
    [Fact]
    public void OpenDaysUseSameFourAmBoundaryAsClientAndKeyCategoriesAreExact()
    {
        Assert.True(Table.Get(3,1)!.IsOpen(At("2026-10-26T03:59:59"))); // Sunday
        Assert.False(Table.Get(3,1)!.IsOpen(At("2026-10-26T04:00:00"))); // Monday
        Assert.Equal((1u,3u),Table.Get(2,1)!.Key(1)!.Value);
        Assert.Equal((2u,4u),Table.Get(2,1)!.Key(2)!.Value);
        Assert.Equal((3u,3u),Table.Get(8,1)!.Key(1)!.Value);
        Assert.Equal((4u,4u),Table.Get(8,1)!.Key(2)!.Value);
        Assert.Null(Table.Get(1,1)!.Key(1));
    }
    [Fact]
    public void AllConfiguredRandomAwardsAndDailyBonusesAreLoaded()
    {
        foreach(var level in Table.All)
        {
            Assert.NotEmpty(level.Fixed);Assert.NotEmpty(level.Daily);Assert.NotEmpty(level.Random);
            Assert.Equal(1,level.DrawCount);Assert.Equal(2,level.DailyLimit);
            Assert.All(level.Random,row=>Assert.True(row.Length>=6));
            Assert.InRange(level.Random.Sum(row=>row[5]),1,10000);
        }
    }
    [Fact]
    public void ThiefLootIsFrozenAndClaimsCannotExceedGeneratedDropOrInventItems()
    {
        var thieves=BountyThieves.Load(TestPaths.SettingDir);
        foreach(var level in Table.All)
        {
            var plan=thieves.Prepare(level,new Random(7));Assert.NotNull(plan);Assert.NotEmpty(plan!.Drops);Assert.NotEmpty(plan.Awards);
        }
        var chosen=thieves.Prepare(Table.Get(1,6)!,new Random(7))!;
        var drops=new JsonArray();foreach(var a in chosen.Awards)drops.Add(new JsonArray(a.Select(n=>(JsonNode?)JsonValue.Create(n)).ToArray()));
        var report=new JsonObject{["tbThiefDeath"]=new JsonArray(new JsonObject{["monId"]=chosen.Monster,["bDeath"]=1}),["tbThiefDropItem"]=drops};
        Assert.Equal(chosen.Awards.Sum(a=>a[4]),BountyThieves.Claimed(chosen,report).Sum(a=>a[4]));
        drops.Add(new JsonArray(9,16,1,1,999999));Assert.Empty(BountyThieves.Claimed(chosen,report));
        report["tbThiefDeath"]=new JsonArray();Assert.Empty(BountyThieves.Claimed(chosen,report));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task KeysAllowClosedDayChargeOnceAndRefundOnExit(int mode)
    {
        using var h=await FeatureHarness.CreateAsync($"bounty-key-{mode}");h.Player.SetMoney(MoneyType.Vigour,100);
        var key=new Item{Guid=9998,Genre=9,Detail=4,Particular=(uint)mode,Level=mode==1?3u:4u,Count=2};h.Player.Items.Add(key);
        string entry=new JsonObject{["id"]=3,["diff"]=1,["key"]=mode,["nFormationId"]=1}.ToJsonString();
        await h.DispatchLuaCallAsync(50,entry);
        string token=JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!["battleToken"]!.GetValue<string>();
        int balance=h.Player.Vigour;Assert.Equal(1u,key.Count);
        await h.DispatchLuaCallAsync(50,entry);Assert.Equal(1u,key.Count);Assert.Equal(balance,h.Player.Vigour);
        string cancel=new JsonObject{["nActivityId"]=3,["nDiff"]=1,["battleToken"]=token}.ToJsonString();
        await h.DispatchFunctionAsync("BountyCancel",cancel);await h.DispatchFunctionAsync("BountyCancel",cancel);
        Assert.Equal(2u,key.Count);Assert.Equal(100,h.Player.Vigour);
    }    [Fact]
    public async Task PremiumKeyDoublesOnlyFixedRewardsAndDefeatDoesNotConsumeDailyBonus()
    {
        using var h=await FeatureHarness.CreateAsync("bounty-premium-fixed");
        h.Player.SetMoney(MoneyType.Vigour,100);h.Player.SetTask(9,4002,0);
        var key=new Item{Guid=9997,Genre=9,Detail=4,Particular=2,Level=4,Count=2};h.Player.Items.Add(key);
        await h.DispatchLuaCallAsync(50,"{\"id\":3,\"diff\":1,\"key\":2,\"nFormationId\":1}");
        var entry=JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        var fixedRows=entry["tbDropItems"]!.AsArray().Where(a=>a![5]!.GetValue<int>()==100).ToArray();
        Assert.Equal(Table.Get(3,1)!.Fixed.Count,fixedRows.Length);
        for(int n=0;n<fixedRows.Length;n++)Assert.Equal(Table.Get(3,1)!.Fixed[n][4]*2,fixedRows[n]![4]!.GetValue<int>());
        string token=entry["battleToken"]!.GetValue<string>();
        var body=new JsonObject{["nActivityId"]=3,["nDiff"]=1,["battleToken"]=token};
        await h.DispatchLuaCallAsync(252,new JsonObject{["sCmd"]=52,["tbParam"]=body}.ToJsonString());
        Assert.Equal(0,h.Player.GetTask(9,4002));Assert.Equal(1u,key.Count);
    }
    [Fact]
    public void AllThiefVariantsCanBeSelectedIncludingRareFiveDropPool()
    {
        var thieves=BountyThieves.Load(TestPaths.SettingDir);var monsters=new HashSet<int>();
        for(int seed=0;seed<1000;seed++)
        {
            var plan=thieves.Prepare(Table.Get(1,6)!,new Random(seed))!;monsters.Add(plan.Monster);
            Assert.Equal(plan.Monster==1310?5:3,plan.Drops.Count);
        }
        Assert.Contains(1310,monsters);Assert.Contains(1311,monsters);
    }

}
