using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

public class HouseUpdateTests
{
    private static HouseTable Table => HouseTable.Load(TestPaths.SettingDir);
    private static furniture Move(HouseFurniture f, uint room = 2, uint parent = 10) => new()
    {
        ObjId = f.ObjId, TemplateId = (uint)f.TemplateId, RoomId = room, ParentId = parent,
        Pose = new objPose { TemplateId = (uint)f.TemplateId, ParentTemplateId = parent,
            PosX = 12, PosY = 19, Rotate = 90 },
        Bizcount = 999, Status = 999, IsNew = true,
    };
    private static async Task<UpdateHouseRsp> Update(Player player, UpdateHouseReq req)
    {
        var session = new FakeSession { Tag = player };
        await new HouseFeature(Table).HandleUpdateHouse(session, new Packet
            { Pf = Pf.ClickUpdateHouseReq, Serial = 73, Payload = req.ToByteArray() });
        var sent = session.LastOf(Pf.UpdateHouseRsp);
        Assert.Equal(73u, sent.Serial);
        var reply = Assert.IsType<UpdateHouseRsp>(sent.Message);
        Assert.Equal(player.Roleid, reply.Roleid);
        return ProtoMessage.Parse<UpdateHouseRsp>(reply.ToByteArray());
    }

    [Fact]
    public async Task ChangedFurnitureMergesAndMembershipFollowsAcceptedPlacement()
    {
        var player = new Player("house-update", 123);
        var initial = HouseFeature.ReadOrCreate(player, Table);
        var f = initial.Furniture.First(x => x.TemplateId == 703);
        player.Extra["house"] = new JsonObject { ["music"] = 7 };
        var req = new UpdateHouseReq { Roleid = player.Roleid };
        req.Furnitures.Add(Move(f, 6, 30));
        var reply = await Update(player, req);
        var state = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(initial.Furniture.Count, state.Furniture.Count);
        var moved = state.Furniture.Single(x => x.ObjId == f.ObjId);
        Assert.Equal(6, moved.RoomId);
        Assert.Equal(30u, moved.ParentId);
        Assert.Equal(12u, moved.PosX);
        Assert.Equal(19u, moved.PosY);
        Assert.Equal(90u, moved.Rotate);
        Assert.Equal(f.BizCount, moved.BizCount);
        Assert.Equal(f.Status, moved.Status);
        Assert.Equal(7, player.Extra["house"]!["music"]!.GetValue<int>());
        Assert.Equal(initial.NextObjId, state.NextObjId);
        Assert.Single(reply.NewFurnitures);
        Assert.Contains(f.ObjId, reply.NewRooms.Single(x => x.RoomId == 6).FurnitureIds);
        Assert.DoesNotContain(f.ObjId, reply.NewRooms.Single(x => x.RoomId == 2).FurnitureIds);
        Assert.All(initial.Furniture.Where(x => x.ObjId != f.ObjId),
            other => Assert.Equal(other, state.Furniture.Single(x => x.ObjId == other.ObjId)));
        var before = player.Extra["house"]!.ToJsonString();
        await Update(player, req);
        Assert.Equal(before, player.Extra["house"]!.ToJsonString());
    }

    [Fact]
    public async Task StorageMoveDoesNotDeleteOrRecreateFurniture()
    {
        var player = new Player("house-storage", 123);
        var f = HouseFeature.ReadOrCreate(player, Table).Furniture.First();
        var req = new UpdateHouseReq();
        req.Furnitures.Add(Move(f, 0, 0));
        await Update(player, req);
        var stored = HouseFeature.ReadOrCreate(player, Table).Furniture.Single(x => x.ObjId == f.ObjId);
        Assert.Equal(0, stored.RoomId);
        Assert.Equal(0u, stored.ParentId);
        Assert.Contains(f.ObjId, HouseFeature.BuildResponse(player.Roleid,
            HouseFeature.ReadOrCreate(player, Table)).HouseInfo!.FurnitureList.Select(x => x.ObjId));
    }

    [Theory]
    [InlineData("template")]
    [InlineData("parent")]
    [InlineData("room")]
    [InlineData("crossroom")]
    [InlineData("pose")]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("x-outside")]
    [InlineData("y-outside")]
    [InlineData("wrapped-coordinate")]
    [InlineData("rotation")]
    [InlineData("footprint")]
    public async Task InvalidPlacementReturnsOriginalAndDoesNotChangeState(string kind)
    {
        var player = new Player("house-invalid", 123);
        var state = HouseFeature.ReadOrCreate(player, Table);
        HouseFeature.WriteState(player, state);
        string before = player.Extra["house"]!.ToJsonString();
        var f = state.Furniture.First(x => x.TemplateId == 703);
        var change = Move(f);
        switch (kind)
        {
            case "template": change.TemplateId++; break;
            case "parent": change.ParentId = 99999; break;
            case "room": change.RoomId = 7; break;
            case "crossroom": change.RoomId = 6; break;
            case "pose": change.Pose!.ParentTemplateId = 2; break;
            case "partial": change.Pose!.ClearPosX(); break;
            case "x-outside": change.Pose!.PosX = 29; break;
            case "y-outside": change.Pose!.PosY = 29; break;
            case "wrapped-coordinate": change.Pose!.PosX = uint.MaxValue; break;
            case "rotation": change.Pose!.Rotate = 45; break;
            case "footprint": change.Pose!.PosX = 24; break;
        }
        var req = new UpdateHouseReq();
        req.Furnitures.Add(change);
        if (kind == "duplicate") req.Furnitures.Add(Move(f, 6, 30));
        var reply = await Update(player, req);
        Assert.Equal(before, player.Extra["house"]!.ToJsonString());
        Assert.Single(reply.NewFurnitures);
        Assert.Equal(f.PosX, reply.NewFurnitures[0].Pose!.PosX);
        Assert.Equal((uint)f.RoomId, reply.NewFurnitures[0].RoomId);
    }

    [Fact]
    public async Task UnownedAndForeignRequestsCannotCreateOrModifyFurniture()
    {
        var player = new Player("house-unowned", 123);
        var state = HouseFeature.ReadOrCreate(player, Table);
        HouseFeature.WriteState(player, state);
        string before = player.Extra["house"]!.ToJsonString();
        var req = new UpdateHouseReq();
        var change = Move(state.Furniture.First());
        change.ObjId = 999999;
        req.Furnitures.Add(change);
        Assert.Empty((await Update(player, req)).NewFurnitures);
        req.Roleid = 999;
        req.Furnitures[0] = Move(state.Furniture.First());
        Assert.Empty((await Update(player, req)).NewFurnitures);
        Assert.Equal(before, player.Extra["house"]!.ToJsonString());
    }

    [Fact]
    public async Task RegisteredHandlerSavesToDiskAndGetHouseReloadsPlacement()
    {
        using var h = await FeatureHarness.CreateAsync("house-update-route");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(30, 0, 0));
        var f = HouseFeature.ReadOrCreate(h.Player, Table).Furniture.First(x => x.TemplateId == 703);
        var req = new UpdateHouseReq { Roleid = h.Player.Roleid };
        req.Furnitures.Add(Move(f, 6, 30));
        await h.DispatchPfAsync(Pf.ClickUpdateHouseReq, req);
        Assert.True(await h.WaitForPfAsync(Pf.UpdateHouseRsp));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        var session = new FakeSession { Tag = saved };
        await new HouseFeature(Table).HandleGetHouseInfo(session, new Packet
            { Pf = GamePf.GetHouseInfo, Serial = 74,
                Payload = new GetHouseInfoReq { Roleid = saved.Roleid }.ToByteArray() });
        var reply = Assert.IsType<GetHouseInfoRsp>(session.LastOf(GamePf.GetHouseInfoRsp).Message);
        var moved = reply.HouseInfo!.FurnitureList.Single(x => x.ObjId == f.ObjId);
        Assert.Equal(6u, moved.RoomId);
        Assert.Equal(12u, moved.Pose!.PosX);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedAndUnauthenticatedRequestsStillReply(bool loggedIn)
    {
        var session = new FakeSession { Tag = loggedIn ? new Player("house-malformed", 123) : null };
        await new HouseFeature(Table).HandleUpdateHouse(session,
            new Packet { Pf = Pf.ClickUpdateHouseReq, Serial = 81, Payload = [0xff] });
        Assert.Equal(81u, session.LastOf(Pf.UpdateHouseRsp).Serial);
    }

    [Fact]
    public async Task EmptyRoomDescriptorCannotEraseInventoryOrAssignGirls()
    {
        var player = new Player("house-room-only", 123);
        HouseFeature.WriteState(player, HouseFeature.ReadOrCreate(player, Table));
        string before = player.Extra["house"]!.ToJsonString();
        var req = new UpdateHouseReq();
        var room = new roomData { RoomId = 2, ThemeId = 999, FriendGirlId = 999 };
        room.GirlIds.Add(999);
        req.Rooms.Add(room);
        var reply = await Update(player, req);
        Assert.Equal(before, player.Extra["house"]!.ToJsonString());
        Assert.Empty(reply.NewThemes);
        Assert.All(reply.NewRooms, r => Assert.Empty(r.GirlIds));
        Assert.NotEmpty(reply.NewRooms.Single(r => r.RoomId == 2).FurnitureIds);
        await Update(player, new UpdateHouseReq());
        Assert.Equal(before, player.Extra["house"]!.ToJsonString());
    }

    [Fact]
    public async Task MixedBatchKeepsRejectedFurnitureAndAppliesValidMove()
    {
        var player = new Player("house-mixed-save", 123);
        var state = HouseFeature.ReadOrCreate(player, Table);
        var valid = state.Furniture.First(x => x.TemplateId == 703);
        var invalid = state.Furniture.First(x => x.ObjId != valid.ObjId);
        var req = new UpdateHouseReq();
        req.Furnitures.Add(Move(valid, 6, 30));
        var bad = Move(invalid);
        bad.TemplateId = 999999;
        req.Furnitures.Add(bad);
        var reply = await Update(player, req);
        var saved = HouseFeature.ReadOrCreate(player, Table);
        Assert.Equal(6, saved.Furniture.Single(x => x.ObjId == valid.ObjId).RoomId);
        Assert.Equal(invalid, saved.Furniture.Single(x => x.ObjId == invalid.ObjId));
        Assert.Equal(2, reply.NewFurnitures.Count);
    }
}
