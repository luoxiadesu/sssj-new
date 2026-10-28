using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using Xunit;

namespace SssjServer.Game.Tests;

public class FurnitureRewardTests
{
    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);

    [Fact]
    public void AutoFurnitureBecomesStorageInstancesAndKeepsExistingLayoutAndPlans()
    {
        var player = new Player("furniture-reward", 123);
        var initial = HouseFeature.ReadOrCreate(player, Table);
        HouseFeature.WriteState(player, initial);
        HouseFeature.WriteHouseKey(player, "customplans", new JsonArray());
        HouseFeature.WriteHouseKey(player, "music", JsonValue.Create(9));
        var rewards = new RewardService(ItemBaseTable.Load(TestPaths.SettingDir)) { Furniture = Table };
        var result = rewards.Grant(player, [[11, 1, 2, 1, 3]], RewardSource.Extra);
        Assert.Empty(result.ChangedItems);
        Assert.Equal(3, result.ChangedFurniture.Count);
        Assert.Equal(3, result.ChangedFurniture.Select(f => f.ObjId).Distinct().Count());
        Assert.All(result.ChangedFurniture, f =>
        {
            Assert.Equal(102, f.TemplateId);
            Assert.Equal(0, f.RoomId);
            Assert.Equal(0u, f.ParentId);
            Assert.Equal(2, f.BizCount);
        });
        Assert.DoesNotContain(player.Items, i => i.Genre == 11);
        var after = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(initial.Furniture.Count + 3, after.Furniture.Count);
        Assert.All(initial.Furniture, f => Assert.Contains(f, after.Furniture));
        Assert.Equal(9, player.Extra["house"]!["music"]!.GetValue<int>());
        Assert.NotNull(player.Extra["house"]!["customplans"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurchaseRejectsFullStorageOrOwnMaxBeforeCharging(bool ownMax)
    {
        using var h = await FeatureHarness.CreateAsync("furniture-buy-cap");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 10000);
        int count = ownMax ? Table.Items[101].OwnMax : 2000;
        var items = Enumerable.Range(0, count).Select(n => new HouseFurniture((uint)(1000+n),
            ownMax ? 101 : 501, 0, 0, 0, 0, 0, 1, 0)).ToList();
        HouseFeature.WriteState(h.Player, new HouseState { Furniture = items, NextObjId = (uint)(1000+count) });
        string before = h.Player.Extra["house"]!.ToJsonString();
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyGoods,
            "{\"shopid\":40001,\"goodsid\":101,\"buynum\":1}");
        Assert.Equal(10000, h.Player.GetMoney(MoneyType.CoffeeCoin));
        Assert.Equal(before, h.Player.Extra["house"]!.ToJsonString());
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 11);
    }

    [Fact]
    public async Task BatchPurchasePushesStorageAndPersistsWithoutOpeningRooms()
    {
        using var h = await FeatureHarness.CreateAsync("furniture-buy-reload");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 10000);
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(1, 0, 0));
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyFurList,
            "[{\"shopid\":40001,\"goodsid\":101,\"buynum\":2},{\"shopid\":40001,\"goodsid\":102,\"buynum\":1}]");
        Assert.True(await h.WaitForPfAsync(Pf.AddFornituresRsp));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        var state = HouseFeature.ReadOrCreate(saved, Table);
        Assert.Equal(3, state.Furniture.Count(f => f.RoomId == 0));
        Assert.Equal(4, state.Furniture.Count(f => f.TemplateId == 101 && f.RoomId == 1));
        Assert.Equal(10000 - 420 * 2 - 475, saved.GetMoney(MoneyType.CoffeeCoin));
        Assert.DoesNotContain(saved.Items, i => i.Genre == 11);
    }

    [Fact]
    public void BatchRewardMergeKeepsItemsGirlsTasksAndFurniture()
    {
        var merged = new RewardGrantResult();
        var source = new RewardGrantResult();
        var girl = new Girl { Girlid = 1 };
        source.NoteGirl(girl);
        source.NoteTask(123, 2);
        source.NoteFurniture(new HouseFurniture(1000, 101, 0, 0, 0, 0, 0, 1, 0));
        merged.Absorb(source);
        Assert.Same(girl, Assert.Single(merged.ChangedGirls));
        Assert.Equal(2, Assert.Single(merged.ChangedTasks).Value);
        Assert.Single(merged.ChangedFurniture);
    }
}
