using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;
namespace SssjServer.Game.Tests;

public class Tower21CompanionTests
{
    private static Task Claim(FeatureHarness h, int id) => h.DispatchFunctionAsync("NormalActivityGetAward",
        $"{{\"nActivityId\":262,\"nType\":1,\"nId\":{id}}}");
    private static int ClaimError(FeatureHarness h)
    {
        Assert.Equal("MissionMgrMsg", h.LastOutbound.Function);
        return JsonNode.Parse(h.LastOutbound.Json)!["nError"]!.GetValue<int>();
    }

    [Fact]
    public async Task ActSuccess_UnlocksDailyClaimAndCountsItsTokenRewardOnce()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-daily");
        h.Player.SetMoney(MoneyType.Vigour, 100);
        h.Player.SetTask(96, 1802, 1);
        await Claim(h, 5600);
        Assert.NotEqual(0, ClaimError(h));
        await h.DispatchLuaCallAsync(54, "[4,21,0,2,1]");
        await ChallengeTestHarness.SettleAsync(h,55, "[4,21,0,2,1,{}]");
        Assert.Equal(1, MissionFeature.ProgressOf(h.Player.GetTask(18, 5600)));
        int before = h.Player.GetMoney(87);
        await Claim(h, 5600);
        Assert.Equal(0, ClaimError(h));
        Assert.Equal(before + 70, h.Player.GetMoney(87));
        await Claim(h, 5600);
        Assert.NotEqual(0, ClaimError(h));
        Assert.Equal(before + 70, h.Player.GetMoney(87));
        Assert.Equal(before + 70, MissionFeature.ProgressOf(h.Player.GetTask(18, 5608)));
        Assert.Equal(1, MissionFeature.ClaimedOf(h.Module.Store.Load(h.Player.Account)!.GetTask(18, 5600)));
    }

    [Fact]
    public async Task TokenProgress_CountsAcquisitionRatherThanBalanceAndCanBeClaimedOnce()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-cumulative");
        h.Module.Rewards.Grant(h.Player, [new[] { 15, 127, 1, 1, 150 }], RewardSource.Extra);
        h.Player.AddMoney(87, -150);
        h.Module.Rewards.Grant(h.Player, [new[] { 15, 127, 1, 1, 50 }], RewardSource.Extra);
        Assert.Equal(50, h.Player.GetMoney(87));
        Assert.Equal(200, MissionFeature.ProgressOf(h.Player.GetTask(18, 5608)));
        Assert.Equal(200, MissionFeature.ProgressOf(h.Player.GetTask(18, 5609)));
        await Claim(h, 5608);
        Assert.Equal(0, ClaimError(h));
        int diamonds = h.Player.GetMoney(MoneyType.FreeDiamond);
        await Claim(h, 5608);
        Assert.NotEqual(0, ClaimError(h));
        Assert.Equal(diamonds, h.Player.GetMoney(MoneyType.FreeDiamond));
        await Claim(h, 5609);
        Assert.NotEqual(0, ClaimError(h));
    }

    [Fact]
    public async Task SpSum_DerivesAcrossStagesAndKeepsPermanentClaimedBits()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-sp-missions");
        h.Player.SetTask(96, 60018, 70);
        h.Player.SetTask(96, 60019, 70);
        await Claim(h, 5602);
        Assert.Equal(0, ClaimError(h));
        Assert.Equal(140, MissionFeature.ProgressOf(h.Player.GetTask(18, 5602)));
        await Claim(h, 5603);
        Assert.NotEqual(0, ClaimError(h));
        Assert.Equal(1, MissionFeature.ClaimedOf(h.Player.GetTask(18, 5602)));
        await Claim(h, 61);
        Assert.NotEqual(0, ClaimError(h));
    }

    [Fact]
    public void DailyRefresh_ResetsOnlyDailyAtBeijingFourAndResistsClockRollback()
    {
        var player = new Player("tower21-day", 1);
        long before = new DateTimeOffset(2026, 10, 25, 3, 59, 59, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
        var changes = new List<KeyValuePair<uint, int>>();
        player.SetTask(18, 5600, MissionFeature.ComposeTaskValue(1, 1));
        player.SetTask(18, 5601, MissionFeature.ComposeTaskValue(100, 1));
        Assert.True(Tower21Missions.RefreshDaily(player, before, changes));
        Assert.Empty(changes); // no day marker: retain legacy claim on first observation
        Assert.False(Tower21Missions.RefreshDaily(player, before, changes));
        Assert.True(Tower21Missions.RefreshDaily(player, before + 1, changes));
        Assert.Equal(0, player.GetTask(18, 5600));
        Assert.Equal(MissionFeature.ComposeTaskValue(100, 1), player.GetTask(18, 5601));
        player.SetTask(18, 5600, 3);
        Assert.False(Tower21Missions.RefreshDaily(player, before, changes));
        Assert.Equal(3, player.GetTask(18, 5600));
    }

    [Fact]
    public async Task DailyClaim_RefreshesExpiredMarkerEvenWithoutHeartbeat()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-late-claim");
        h.Player.Extra[Tower21Missions.DayKey] = JsonValue.Create(GachaFreeDraws.Day(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - 1);
        h.Player.SetTask(18, 5600, MissionFeature.ComposeTaskValue(1, 0));
        await Claim(h, 5600);
        Assert.NotEqual(0, ClaimError(h));
        Assert.Equal(0, h.Player.GetTask(18, 5600));
    }

    [Fact]
    public async Task EventShop_ChargesTokensPersistsLimitAndRejectsRebuy()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-shop");
        var table = ShopTable.Load(TestPaths.SettingDir);
        Assert.True(table.GetShop(30045)!.IsOpenAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.False(table.GetShop(30044)!.IsOpenAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        h.Player.SetMoney(87, 3000);
        await h.DispatchLuaCallAsync(11, "{\"shopid\":30045}");
        var list = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.Equal(1, list["isopen"]!.GetValue<int>());
        Assert.Contains((JsonArray)list["goodslist"]!, n => n!["goodsid"]!.GetValue<int>() == 34501);
        string buy = """{"shopid":30045,"goodsid":34501,"buynum":1}""";
        await h.DispatchLuaCallAsync(13, buy);
        Assert.Equal(1500, h.Player.GetMoney(87));
        Assert.Contains(h.Player.Items, i => i.Genre == 7 && i.Detail == 5 && i.Particular == 2 && i.Level == 4);
        var saved = h.Module.Store.Load(h.Player.Account)!;
        h.Session.Tag = saved;
        await h.DispatchLuaCallAsync(13, buy);
        Assert.Equal(1500, saved.GetMoney(87));
    }

    [Fact]
    public async Task EventShop_InsufficientTokensDoesNotAddGoods()
    {
        using var h = await FeatureHarness.CreateAsync("tower21-shop-poor");
        h.Player.SetMoney(87, 1499);
        int count = h.Player.Items.Count;
        await h.DispatchLuaCallAsync(13, """{"shopid":30045,"goodsid":34501,"buynum":1}""");
        Assert.Equal(1499, h.Player.GetMoney(87));
        Assert.Equal(count, h.Player.Items.Count);
    }
}
