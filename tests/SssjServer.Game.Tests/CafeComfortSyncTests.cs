using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class CafeComfortSyncTests
{
    private static List<JsonNode> Downloads(FeatureHarness h, int start) => h.Outbound.Skip(start)
        .Where(x => x.Function == "LuaCall").Select(x => JsonNode.Parse(x.Json)!)
        .Where(x => x["sCmd"]!.GetValue<int>() == 112).Select(x => x["tbParam"]!).ToList();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PurchaseRefreshesOwnedScoreAndOnlyPushesForActiveCafe(bool active, bool batch)
    {
        using var h = await FeatureHarness.CreateAsync($"comfort-buy-{active}-{batch}");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 100_000);
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = [], NextObjId = 1000 });
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(1, 123, 0));
        if (active) await h.DispatchLuaCallAsync(112, "{}");
        int start = h.Outbound.Count;
        string purchase = "{\"shopid\":40001,\"goodsid\":101,\"buynum\":1}";
        await h.DispatchLuaCallAsync(batch ? 14 : 13, batch ? "[" + purchase + "]" : purchase);

        var score = HouseTable.Load(TestPaths.SettingDir).Items[101].AddScore;
        Assert.True(score > 0);
        Assert.Equal(score, CafeFeature.ReadState(h.Player).Comfort);
        Assert.Equal(score, CafeFeature.ReadState(new PlayerStore(h.Options).Load(h.Player.Account)!).Comfort);
        var downloads = Downloads(h, start);
        Assert.Equal(active ? 1 : 0, downloads.Count);
        if (active) Assert.Equal(score, downloads[0]["comfort"]!.GetValue<int>());
        Assert.Equal(active, GirlMoodRecovery.IsInCafe(h.Session, h.Player));
    }

    [Fact]
    public async Task SaleToZeroSendsCompleteSnapshotWithoutChangingBusinessState()
    {
        using var h = await FeatureHarness.CreateAsync("comfort-sale-zero");
        var house = HouseTable.Load(TestPaths.SettingDir);
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        var row = house.Items.Values.First(r => r.Type == 104 && r.AddScore > 0 && !r.IsSystem
            && cafe.Furniture.TryGetValue(r.Id, out var price) && price.SellPrice > 0);
        HouseFeature.WriteState(h.Player, new HouseState
        {
            Furniture = [new HouseFurniture(1000, row.Id, 0, 0, 0, 0, 0, 0, 0)], NextObjId = 1001
        });
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(1, 123, row.AddScore), new CafeFeature.CafeContent
        {
            Queue = [new CafeFeature.CafeQueueGuest(cafe.Guests.Keys.First(), 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds())],
            Coffees = [new CafeFeature.CafeCoffee(cafe.Coffees.Keys.First(), 10)],
            AreaGirls = [[new CafeFeature.CafeAreaGirl(1, 0)], Array.Empty<CafeFeature.CafeAreaGirl>(), Array.Empty<CafeFeature.CafeAreaGirl>()]
        });
        await h.DispatchLuaCallAsync(112, "{}");
        // Capture the exact lists the normal 112 consumer has just accepted.
        var before = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        Assert.NotEmpty((JsonArray)before["customerqueue"]!);
        Assert.NotEmpty((JsonArray)before["coffeelist"]!);
        Assert.NotEmpty((JsonArray)before["roomgirlslist"]![0]!);
        int start = h.Outbound.Count;
        await h.DispatchLuaCallAsync(137, "{\"objIds\":[1000]}");
        var snapshot = Assert.Single(Downloads(h, start));
        var sale = h.Outbound.Skip(start).Select(x => JsonNode.Parse(x.Json)!)
            .Single(x => x["sCmd"]?.GetValue<int>() == 137)["tbParam"]!;
        Assert.Equal(1000u, sale["objId"]![0]!.GetValue<uint>());
        Assert.True(JsonNode.DeepEquals(sale["objId"], sale["objIds"]));
        Assert.Equal(0, snapshot["comfort"]!.GetValue<int>());
        Assert.Equal(123, snapshot["hot"]!.GetValue<int>());
        foreach (string key in new[] { "seatlist", "customerqueue", "coffeelist", "roomgirlslist", "weightlist", "visitedList", "boxstatelist", "petstatelist" })
            Assert.True(JsonNode.DeepEquals(before[key], snapshot[key]), key);
        Assert.Equal(0, CafeFeature.ReadState(new PlayerStore(h.Options).Load(h.Player.Account)!).Comfort);
        Assert.Empty(HouseFeature.ReadOrCreate(h.Player, house).Furniture);
        Assert.True(GirlMoodRecovery.IsInCafe(h.Session, h.Player));
    }

    [Fact]
    public async Task SaleSettlesOldRestRateThenStartsLowerRateWithoutRetroactiveLoss()
    {
        using var h = await FeatureHarness.CreateAsync("comfort-sale-mood");
        var cafe = CafeTable.Load(TestPaths.SettingDir);
        var house = HouseTable.Load(TestPaths.SettingDir);
        var row = house.Items.Values.First(r => r.Type == 104 && r.AddScore > 0 && !r.IsSystem
            && cafe.Furniture.TryGetValue(r.Id, out var price) && price.SellPrice > 0);
        HouseFeature.WriteState(h.Player, new HouseState
        {
            Furniture = [new HouseFurniture(1000, row.Id, 0, 0, 0, 0, 0, 0, 0)], NextObjId = 1001
        });
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(30, 123, 2400), new CafeFeature.CafeContent
        {
            AreaGirls = [Array.Empty<CafeFeature.CafeAreaGirl>(), [new CafeFeature.CafeAreaGirl(1, 0)], Array.Empty<CafeFeature.CafeAreaGirl>()]
        });
        h.Player.GetGirl(1)!.Moodvalue = 50;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var mood = new GirlMoodRecovery(TestPaths.SettingDir);
        mood.Refresh(h.Session, h.Player, now - 901);
        await h.DispatchLuaCallAsync(137, "{\"objIds\":[1000]}");
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
        Assert.InRange(h.Player.GetTask(41, 1), now, now + 5);
        Assert.Equal("2:4", h.Player.Extra[GirlMoodRecovery.ExtraKey]!["1"]!.GetValue<string>());
        long clock = h.Player.GetTask(41, 1);
        mood.Refresh(h.Session, h.Player, clock + 899);
        Assert.Equal(51u, h.Player.GetGirl(1)!.Moodvalue);
        mood.Refresh(h.Session, h.Player, clock + 900);
        Assert.Equal(52u, h.Player.GetGirl(1)!.Moodvalue);
    }

    [Fact]
    public async Task LegacyThemePurchaseRefreshesComfortAndKeepsStringReply()
    {
        using var h = await FeatureHarness.CreateAsync("comfort-theme-buy");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 1_000_000);
        h.Player.Level = 300;
        var house = HouseTable.Load(TestPaths.SettingDir);
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = [], NextObjId = 1000 });
        await h.DispatchLuaCallAsync(112, "{}");
        int start = h.Outbound.Count;
        var goods = ShopTable.Load(TestPaths.SettingDir).GoodsOf(40001).Select(g => g.GoodsId).ToHashSet();
        int theme = CafeTable.Load(TestPaths.SettingDir).Themes.Values.OrderBy(t => t.Id)
            .First(t => t.Items.Count > 0 && t.ThemeType.Length > 0 && t.Items.All(i => goods.Contains(i.TemplateId))).Id;
        await h.DispatchLuaCallAsync(126, $"{{\"themeid\":{theme}}}");
        int score = CafeFeature.ReadState(h.Player).Comfort;
        Assert.True(score > 0);
        Assert.Equal(score, Assert.Single(Downloads(h, start))["comfort"]!.GetValue<int>());
        Assert.Equal(HouseFeature.BuyThemeOk, JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!.GetValue<string>());
        Assert.Equal(score, CafeFeature.ReadState(new PlayerStore(h.Options).Load(h.Player.Account)!).Comfort);
    }

    [Fact]
    public async Task RejectedPurchaseAndRejectedSaleDoNotRefreshOrPush()
    {
        using var h = await FeatureHarness.CreateAsync("comfort-rejected");
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = [], NextObjId = 1000 });
        await h.DispatchLuaCallAsync(112, "{}");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 0);
        CafeFeature.WriteState(h.Player, CafeFeature.ReadState(h.Player) with { Comfort = 123 });
        int start = h.Outbound.Count;
        await h.DispatchLuaCallAsync(13, "{\"shopid\":40001,\"goodsid\":101,\"buynum\":1}");
        await h.DispatchLuaCallAsync(137, "{\"objIds\":[1000]}");
        Assert.Empty(Downloads(h, start));
        Assert.Equal(123, CafeFeature.ReadState(h.Player).Comfort);
    }
}
