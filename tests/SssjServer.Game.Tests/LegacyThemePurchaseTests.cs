using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

public class LegacyThemePurchaseTests
{
    private static HouseTable House => HouseTable.Load(TestPaths.SettingDir);
    private static CafeThemeRow Theme => CafeTable.Load(TestPaths.SettingDir).Themes[4];
    private static Dictionary<int, int> Missing(HouseState state) => Theme.Items.GroupBy(i => i.TemplateId)
        .Select(g => (Id: g.Key, Count: Math.Max(0, g.Count() - state.Furniture.Count(f => f.TemplateId == g.Key))))
        .Where(g => g.Count > 0).ToDictionary(g => g.Id, g => g.Count);
    private static string Request(Dictionary<int, int> missing) => new JsonObject
    {
        ["themeid"] = 4,
        ["furidlist"] = new JsonArray(missing.SelectMany(p => Enumerable.Repeat(p.Key, p.Value))
            .Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
    }.ToJsonString();

    [Fact]
    public async Task ChargesCatalogCoffeePriceForMissingItemsAndKeepsLayout()
    {
        using var h = await FeatureHarness.CreateAsync("legacy-theme-price");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 1000000);
        h.Player.SetMoney(HouseFeature.GoldMoneyId, 999);
        var initial = HouseFeature.ReadOrCreate(h.Player, House);
        HouseFeature.WriteState(h.Player, initial);
        var missing = Missing(initial);
        var shop = ShopTable.Load(TestPaths.SettingDir);
        int cost = missing.Sum(p => shop.GoodsOf(40001).Single(g => g.GoodsId == p.Key).UnitPrice(1, 100) * p.Value);
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, Request(missing));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        var after = HouseFeature.ReadOrCreate(saved, House);
        Assert.Equal(1000000 - cost, saved.GetMoney(MoneyType.CoffeeCoin));
        Assert.Equal(999, saved.GetMoney(HouseFeature.GoldMoneyId));
        Assert.Equal(initial.Furniture.Count + missing.Values.Sum(), after.Furniture.Count);
        Assert.All(initial.Furniture, f => Assert.Contains(f, after.Furniture));
        Assert.All(after.Furniture.Where(f => !initial.Furniture.Any(old => old.ObjId == f.ObjId)),
            f => { Assert.Equal(0, f.RoomId); Assert.Equal(0u, f.ParentId); });
        Assert.Contains(4, HouseFeature.ReadOwnedThemes(saved));
        int balance = h.Player.GetMoney(MoneyType.CoffeeCoin);
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, Request(missing));
        Assert.Equal(balance, h.Player.GetMoney(MoneyType.CoffeeCoin));
        Assert.Equal(after.Furniture.Count, HouseFeature.ReadOrCreate(h.Player, House).Furniture.Count);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[999999]")]
    [InlineData("\"bad\"")]
    public async Task StaleOrForgedMissingListCannotChargeOrGrant(string list)
    {
        using var h = await FeatureHarness.CreateAsync("legacy-theme-badlist");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 1000000);
        HouseFeature.WriteState(h.Player, HouseFeature.ReadOrCreate(h.Player, House));
        string before = h.Player.Extra["house"]!.ToJsonString();
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, "{\"themeid\":4,\"furidlist\":" + list + "}");
        Assert.Equal(before, h.Player.Extra["house"]!.ToJsonString());
        Assert.Equal(1000000, h.Player.GetMoney(MoneyType.CoffeeCoin));
        Assert.Empty(HouseFeature.ReadOwnedThemes(h.Player));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullStorageOrInsufficientCoffeeBalanceLeavesPurchaseUnchanged(bool full)
    {
        using var h = await FeatureHarness.CreateAsync("legacy-theme-no-capacity");
        h.Player.SetMoney(MoneyType.CoffeeCoin, full ? 1000000 : 0);
        h.Player.SetMoney(HouseFeature.GoldMoneyId, 1000000);
        var state = HouseFeature.ReadOrCreate(h.Player, House);
        if (full)
            for (int n = state.Furniture.Count; n < 2000; n++)
                state.Furniture.Add(new HouseFurniture((uint)(10000+n), 501, 0, 0, 0, 0, 0, 0, 0));
        HouseFeature.WriteState(h.Player, state);
        string before = h.Player.Extra["house"]!.ToJsonString();
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, Request(Missing(state)));
        Assert.Equal(before, h.Player.Extra["house"]!.ToJsonString());
        Assert.Equal(full ? 1000000 : 0, h.Player.GetMoney(MoneyType.CoffeeCoin));
        Assert.Equal(1000000, h.Player.GetMoney(HouseFeature.GoldMoneyId));
    }

    [Fact]
    public async Task AlreadyOwnedAllPiecesCanCollectThemeWithoutBuyingDuplicates()
    {
        using var h = await FeatureHarness.CreateAsync("legacy-theme-complete");
        var items = Theme.Items.Select((i, n) => new HouseFurniture((uint)(1000+n),
            i.TemplateId, 0, 0, 0, 0, 0, 0, 0)).ToList();
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = items, NextObjId = 2000 });
        h.Player.SetMoney(MoneyType.CoffeeCoin, 0);
        await h.DispatchLuaCallAsync(HouseFeature.BuyThemeCmd, "{\"themeid\":4,\"furidlist\":[]}");
        Assert.Contains(4, HouseFeature.ReadOwnedThemes(h.Player));
        Assert.Equal(items.Count, HouseFeature.ReadOrCreate(h.Player, House).Furniture.Count);
        Assert.Equal(0, h.Player.GetMoney(MoneyType.CoffeeCoin));
    }

    [Fact]
    public async Task CafeShelfReportsActualFurnitureInsteadOfHistoricalPurchases()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-shelf-owned");
        HouseFeature.WriteState(h.Player, new HouseState { Furniture =
            [new HouseFurniture(1000, 101, 0, 0, 0, 0, 0, 1, 0)], NextObjId = 1001 });
        ShopFeature.AddBuyCount(h.Player, 40001, ShopLimitKind.Total, 101, 55);
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqGoodsList, "{\"shopid\":40001}");
        var payload = JsonNode.Parse(h.LastOutbound.Json)!["tbParam"]!;
        var goods = payload["goodslist"]!.AsArray().Single(g => g!["goodsid"]!.GetValue<int>() == 101)!;
        Assert.Equal(1, goods["totalbuynum"]!.GetValue<int>());
        Assert.Equal(55, ShopFeature.GetBuyCount(h.Player, 40001, ShopLimitKind.Total, 101));
    }
}
