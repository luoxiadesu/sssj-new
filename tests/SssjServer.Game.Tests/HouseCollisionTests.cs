using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

public class HouseCollisionTests
{
    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);
    private static HouseFurniture Item(uint id, int template = 101, uint x = 5, uint y = 5, int room = 1, uint parent = 2)
        => new(id, template, room, parent, x, y, 0, 0, 0);
    private static HouseState State(params HouseFurniture[] items) => new() { Furniture = items.ToList(), NextObjId = 2000 };
    private static async Task<UpdateHouseRsp> Save(Player player, params HouseFurniture[] items)
    {
        var req = new UpdateHouseReq { Roleid = player.Roleid };
        req.Furnitures.AddRange(items.Select(HouseFeature.BuildFurniture));
        var fake = new FakeSession { Tag = player };
        await new HouseFeature(Table, TestPaths.SettingDir).HandleUpdateHouse(fake,
            new Packet { Pf = Pf.ClickUpdateHouseReq, Serial = 99, Payload = req.ToByteArray() });
        return Assert.IsType<UpdateHouseRsp>(fake.LastOf(Pf.UpdateHouseRsp).Message);
    }

    [Theory]
    [InlineData(5, 0, false)]
    [InlineData(10, 0, false)]
    [InlineData(11, 0, true)]
    [InlineData(10, 90, false)]
    public async Task SaveRejectsOverlappingGridCellsAndAcceptsAdjacentFurniture(int x, int rotation, bool accepted)
    {
        var a = Item(1000); var b = Item(1001, x: 20, y: 20);
        var player = new Player("collision", 77);
        HouseFeature.WriteState(player, State(a, b));
        var move = b with { PosX = (uint)x, PosY = 5, Rotate = (uint)rotation };
        var reply = await Save(player, move);
        var saved = HouseFeature.ReadOrCreate(player, Table).Furniture[1];
        Assert.Equal(accepted ? move : b, saved);
        Assert.Equal(saved.PosX, Assert.Single(reply.NewFurnitures).Pose!.PosX);
    }

    [Fact]
    public void CarpetCanOverlapFurnitureButNotAnotherCarpet()
    {
        var rug = Table.Items.Values.First(r => r.Type == Table.CarpetTypeId && r.Size.Count == 2 && r.Size.All(s => s > 1));
        var a = Item(1000); var b = Item(1001, rug.Id);
        Assert.True(HouseCollisionLimits.NoWorse(Table, State(), State(a, b)));
        Assert.False(HouseCollisionLimits.NoWorse(Table, State(), State(b, b with { ObjId = 1002 })));
        Assert.True(HouseCollisionLimits.NoWorse(Table, State(), State(a, a with { ObjId = 1001, RoomId = 2, ParentId = 10 })));
        Assert.True(HouseCollisionLimits.NoWorse(Table, State(), State(a, a with { ObjId = 1001, RoomId = 0, ParentId = 0 })));
    }

    [Theory]
    [InlineData(27, true)]
    [InlineData(28, false)]
    public async Task FixedBlockBoundaryUsesInclusiveGridEndpoints(int x, bool accepted)
    {
        Assert.Equal(new[] { 33, 0, 45, 38 }, Assert.Single(Table.Blocks[2]));
        Assert.Empty(Table.Blocks[10]);
        var item = Item(1000, room: 0, parent: 0);
        var player = new Player("fixed-block", 77);
        HouseFeature.WriteState(player, State(item));
        await Save(player, item with { RoomId = 1, ParentId = 2, PosX = (uint)x });
        Assert.Equal(accepted ? 1 : 0, HouseFeature.ReadOrCreate(player, Table).Furniture[0].RoomId);
    }

    [Fact]
    public void LegacyOverlapCanRemainOrDecreaseButCannotGrow()
    {
        var a = Item(1000); var b = Item(1001, x: 10);
        var original = State(a, b);
        Assert.True(HouseCollisionLimits.NoWorse(Table, original, original));
        Assert.False(HouseCollisionLimits.NoWorse(Table, original, State(a, b with { PosX = 9 })));
        Assert.True(HouseCollisionLimits.NoWorse(Table, original, State(a, b with { PosX = 11 })));
        Assert.True(HouseCollisionLimits.NoWorse(Table, original, State(a, b with { RoomId = 0, ParentId = 0 })));
    }

    [Fact]
    public async Task FinalBatchStateAllowsSwappingFurnitureWithoutTransientCollision()
    {
        var a = Item(1000); var b = Item(1001, x: 20, y: 20);
        var player = new Player("collision-swap", 77);
        HouseFeature.WriteState(player, State(a, b));
        await Save(player, a with { PosX = 20, PosY = 20 }, b with { PosX = 5, PosY = 5 });
        Assert.Equal(20u, HouseFeature.ReadOrCreate(player, Table).Furniture[0].PosX);
        Assert.Equal(5u, HouseFeature.ReadOrCreate(player, Table).Furniture[1].PosX);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomPlansRejectOverlapAndFixedBlocks(bool block)
    {
        var player = new Player("plan-collision", 77);
        HouseFeature.WriteState(player, State(Item(1000), Item(1001, x: 20, y: 20)));
        var theme = new decorationTheme { Roomtype = 1, Name = "布局", NewGenerate = true, ThemeId = 0, TemplateThemeId = 0 };
        theme.Poses.Add(new objPose { TemplateId = 101, ParentTemplateId = 2, PosX = block ? 28u : 5u, PosY = 5, Rotate = 0 });
        if (!block) theme.Poses.Add(ProtoMessage.Parse<objPose>(theme.Poses[0].ToByteArray()));
        var req = new UpdateHouseReq { Roleid = player.Roleid };
        req.Themes.Add(theme);
        var fake = new FakeSession { Tag = player };
        await new HouseFeature(Table).HandleUpdateHouse(fake, new Packet { Pf = Pf.ClickUpdateHouseReq, Payload = req.ToByteArray() });
        Assert.Empty(HouseFeature.ReadPlans(player));
        Assert.Equal(2, HouseFeature.ReadOrCreate(player, Table).Furniture.Count);
    }
}
