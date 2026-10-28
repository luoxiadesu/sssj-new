using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

public class HousePlanTests
{
    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);
    private static decorationTheme Plan(string name = "休息室方案")
    {
        var theme = new decorationTheme { ThemeId = 0, TemplateThemeId = 0,
            Roomtype = 2, Name = name, NewGenerate = true, Createtime = 1 };
        theme.Poses.Add(new objPose { TemplateId = 703, ParentTemplateId = 10,
            PosX = 10, PosY = 18, Rotate = 180 });
        return theme;
    }
    private static async Task<UpdateHouseRsp> Save(Player player, decorationTheme theme,
        params roomData[] rooms)
    {
        var req = new UpdateHouseReq { Roleid = player.Roleid };
        req.Themes.Add(theme);
        req.Rooms.AddRange(rooms);
        var fake = new FakeSession { Tag = player };
        await new HouseFeature(Table).HandleUpdateHouse(fake, new Packet
            { Pf = Pf.ClickUpdateHouseReq, Serial = 92, Payload = req.ToByteArray() });
        Assert.Equal(92u, fake.LastOf(Pf.UpdateHouseRsp).Serial);
        return ProtoMessage.Parse<UpdateHouseRsp>(fake.LastOf(Pf.UpdateHouseRsp).Message.ToByteArray());
    }
    private static async Task Delete(Player player, uint id, ulong? role = null)
    {
        var req = new DelDecorationThemeReq { Roleid = role ?? player.Roleid, ThemeId = id };
        var fake = new FakeSession { Tag = player };
        await new HouseFeature(Table).HandleDeletePlan(fake, new Packet
            { Pf = Pf.ClickDelThemeReq, Serial = 93, Payload = req.ToByteArray() });
        Assert.Equal(93u, fake.LastOf(Pf.DelDecorationThemeRsp).Serial);
    }

    [Fact]
    public async Task CustomPlanLifecyclePreservesInventoryAndOfficialPurchases()
    {
        var player = new Player("plan-life", 77);
        var furniture = HouseFeature.ReadOrCreate(player, Table);
        HouseFeature.WriteState(player, furniture);
        HouseFeature.WriteOwnedThemes(player, [1, 2]);
        HouseFeature.WriteHouseKey(player, "music", JsonValue.Create(8));
        string inventory = player.Extra["house"]!["furniturelist"]!.ToJsonString();
        var reply = await Save(player, Plan());
        var theme = Assert.Single(reply.NewThemes);
        Assert.True(theme.ThemeId > 0);
        Assert.Equal(0u, theme.TemplateThemeId);
        Assert.False(theme.NewGenerate);
        Assert.True(theme.Createtime > 1);
        Assert.Single(theme.Poses);
        uint id = theme.ThemeId;
        ulong created = theme.Createtime;
        var cache = HouseFeature.BuildResponse(player.Roleid, furniture, player).HouseInfo!;
        Assert.Equal(id, Assert.Single(cache.ThemeList).ThemeId);
        theme.Name = "覆盖后的名称";
        theme.Poses[0].PosX = 15;
        reply = await Save(player, theme, new roomData { RoomId = 2, ThemeId = id });
        Assert.Single(HouseFeature.ReadPlans(player));
        Assert.Equal(created, Assert.Single(reply.NewThemes).Createtime);
        Assert.Equal(id, reply.NewRooms.Single(r => r.RoomId == 2).ThemeId);
        Assert.Equal("覆盖后的名称", HouseFeature.ReadPlans(player)[0].Name);
        Assert.Equal(15u, HouseFeature.ReadPlans(player)[0].Poses[0].X);
        await Delete(player, id, role: 999);
        Assert.Single(HouseFeature.ReadPlans(player));
        await Delete(player, id);
        await Delete(player, id);
        Assert.Empty(HouseFeature.ReadPlans(player));
        cache = HouseFeature.BuildResponse(player.Roleid, furniture, player).HouseInfo!;
        Assert.Equal(0u, cache.RoomList.Single(r => r.RoomId == 2).ThemeId);
        Assert.Equal(inventory, player.Extra["house"]!["furniturelist"]!.ToJsonString());
        Assert.Equal(new HashSet<int> { 1, 2 }, HouseFeature.ReadOwnedThemes(player));
        Assert.Equal(8, player.Extra["house"]!["music"]!.GetValue<int>());
        Assert.True(Assert.Single((await Save(player, Plan())).NewThemes).ThemeId > id);
    }

    [Theory]
    [InlineData("official")]
    [InlineData("room")]
    [InlineData("unowned")]
    [InlineData("parent")]
    [InlineData("quantity")]
    [InlineData("partial")]
    [InlineData("unknown-update")]
    [InlineData("x-outside")]
    [InlineData("y-outside")]
    [InlineData("rotation")]
    [InlineData("footprint")]
    public async Task InvalidPlansDoNotCreateLayoutsOrFurniture(string kind)
    {
        var player = new Player("plan-invalid", 77);
        HouseFeature.WriteState(player, HouseFeature.ReadOrCreate(player, Table));
        string before = player.Extra["house"]!.ToJsonString();
        var theme = Plan();
        switch (kind)
        {
            case "official": theme.TemplateThemeId = 1; break;
            case "room": theme.Roomtype = 7; break;
            case "unowned": theme.Poses[0].TemplateId = 99999; break;
            case "parent": theme.Poses[0].ParentTemplateId = 30; break;
            case "quantity": theme.Poses.Add(ProtoMessage.Parse<objPose>(theme.Poses[0].ToByteArray())); break;
            case "partial": theme.Poses[0].ClearPosY(); break;
            case "unknown-update": theme.ThemeId = 1234; theme.NewGenerate = false; break;
            case "x-outside": theme.Poses[0].PosX = uint.MaxValue; break;
            case "y-outside": theme.Poses[0].PosY = 29; break;
            case "rotation": theme.Poses[0].Rotate = 45; break;
            case "footprint": theme.Poses[0].PosY = 24; break;
        }
        Assert.Empty((await Save(player, theme)).NewThemes);
        Assert.Equal(before, player.Extra["house"]!.ToJsonString());
    }

    [Fact]
    public async Task PlanCapacityMatchesClientBoundaryAndOverwriteStillWorks()
    {
        var player = new Player("plan-cap", 77);
        var plans = Enumerable.Range(1, 100).Select(i => new HouseFeature.CustomPlan(
            (uint)i, "方案", 2, 100, [])).ToList();
        HouseFeature.WriteHouseKey(player, "customplans", JsonSerializer.SerializeToNode(plans));
        var last = Assert.Single((await Save(player, Plan())).NewThemes);
        Assert.Equal(101, HouseFeature.ReadPlans(player).Count);
        Assert.Empty((await Save(player, Plan())).NewThemes);
        last.Name = "满容量下覆盖";
        Assert.Single((await Save(player, last)).NewThemes);
        Assert.Equal(101, HouseFeature.ReadPlans(player).Count);
    }

    [Fact]
    public async Task ProtocolRoutesPersistPlansAcrossStoreReloadAndDelete()
    {
        using var h = await FeatureHarness.CreateAsync("plan-routing");
        var req = new UpdateHouseReq { Roleid = h.Player.Roleid };
        req.Themes.Add(Plan());
        await h.DispatchPfAsync(Pf.ClickUpdateHouseReq, req);
        Assert.True(await h.WaitForPfAsync(Pf.UpdateHouseRsp));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        var plan = Assert.Single(HouseFeature.ReadPlans(saved));
        var fake = new FakeSession { Tag = saved };
        await new HouseFeature(Table).HandleGetHouseInfo(fake, new Packet
            { Pf = GamePf.GetHouseInfo, Serial = 94,
                Payload = new GetHouseInfoReq { Roleid = saved.Roleid }.ToByteArray() });
        var cache = Assert.IsType<GetHouseInfoRsp>(fake.LastOf(GamePf.GetHouseInfoRsp).Message).HouseInfo!;
        Assert.Equal(plan.Id, Assert.Single(cache.ThemeList).ThemeId);
        await h.DispatchPfAsync(Pf.ClickDelThemeReq,
            new DelDecorationThemeReq { Roleid = h.Player.Roleid, ThemeId = plan.Id });
        Assert.True(await h.WaitForPfAsync(Pf.DelDecorationThemeRsp));
        saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Empty(HouseFeature.ReadPlans(saved));
    }
}
