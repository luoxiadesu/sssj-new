using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.PvpProto;
using Xunit;

namespace SssjServer.Game.Tests;

// Every peer shares ONE module/router. All accounts and saves are in a unique temporary directory.
public sealed class CoopLobbyTests
{
    private const uint Map = (4u << 16) | 1;
    private const uint Hard = (4u << 16) | 2;
    private sealed class Peer : IDisposable
    {
        public required ClientSession Session;
        public required TcpClient Client;
        public required Player Player;
        private readonly List<byte> _bytes = new();
        public readonly List<Packet> Packets = new();
        public void Drain()
        {
            while (Client.Available > 0)
            {
                var bytes = new byte[Client.Available];
                int n = Client.GetStream().Read(bytes);
                _bytes.AddRange(bytes.AsSpan(0, n).ToArray());
            }
            while (PacketCodec.TryDecode(_bytes.ToArray(), out var packet, out int consumed))
            { Packets.Add(packet); _bytes.RemoveRange(0, consumed); }
        }
        public async Task<T> Read<T>(int pf) where T : ProtoMessage, new()
        {
            for (int i = 0; i < 100; i++)
            {
                Drain();
                var packet = Packets.FirstOrDefault(p => p.Pf == pf);
                if (packet is not null) { Packets.Remove(packet); return ProtoMessage.Parse<T>(packet.Payload); }
                await Task.Delay(20);
            }
            throw new TimeoutException($"No pf {pf} for {Player.Roleid}");
        }
        public void Dispose() { Session.Dispose(); Client.Dispose(); }
    }
    private sealed class Lobby : IDisposable
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "sssj-coop-tests", Guid.NewGuid().ToString("N"));
        public readonly PfRouter Router = new();
        public readonly LuaCallRouter Lua = new();
        public readonly List<(ClientSession Session, string Function)> Errors = new();
        public readonly List<Peer> Peers = new();
        public readonly GameModule Module;
        public CoopLobbyFeature Feature => Module.FindFeature<CoopLobbyFeature>()!;
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00+08:00");
        public Lobby()
        {
            Module = new GameModule(new ServerOptions { DataDirectory = DirectoryPath,
                SettingDirectory = TestPaths.SettingDir, GmFile = "", ServerGmFile = "", EnableBots = false });
            Module.Install(Router, Lua);
            Assert.NotNull(Feature);
            Feature.Clock = () => Now;
            Lua.NtfS2CCallSender = (s, f, _) => { Errors.Add((s, f)); return Task.CompletedTask; };
        }
        public async Task<Peer> Add(string? account = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient { NoDelay = true };
            var connect = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var accepted = await listener.AcceptTcpClientAsync();
            await connect; listener.Stop(); accepted.NoDelay = true;
            var player = Module.World.GetOrCreatePlayer(account ?? "coop-" + Peers.Count);
            var peer = new Peer { Player = player, Client = client, Session = new ClientSession(Peers.Count + 1, accepted) { Tag = player } };
            if (!Peers.Any(p => p.Player.Roleid == player.Roleid))
            {
                player.Formations.Clear();
                player.Name = "Real-" + player.Roleid;
                var card = player.AddItem(1, 2, 1, 3, 1);
                var formation = new Formation { Id = 1 };
                formation.Slots.Add(new FormationCardSlot { MainCardGuid = card.Guid, UsedCardGuid = card.Guid });
                player.Formations.Add(formation);
                player.SetTask(38, 5, 5);
            }
            Peers.Add(peer);
            return peer;
        }
        public Task Send(Peer peer, int pf, ProtoMessage req, uint serial = 71)
            => Router.DispatchAsync(peer.Session, new Packet { Pf = pf, Serial = serial, Payload = req.ToByteArray() });
        public async Task<Team> Create(Peer p, bool isPublic = true, uint map = Map)
        {
            await Send(p, 1627, new ApplyCreateTeamReq { RoleId = p.Player.Roleid, MapId = map, SolutionId = 1, IsPublic = isPublic });
            return (await p.Read<ApplyCreateTeamRsp>(1628)).Team!;
        }
        public async Task<ApplyJoinTeamRsp> Join(Peer p, Team room)
        {
            await Send(p, 1633, new ApplyJoinTeamReq { RoleId = p.Player.Roleid, TeamId = room.TeamId, SolutionId = 1, Mgrid = 1 });
            return await p.Read<ApplyJoinTeamRsp>(1634);
        }
        public async Task<Team?> Fetch(Peer p)
        {
            await Send(p, 1639, new ApplyTeamDataReq { RoleId = p.Player.Roleid });
            return (await p.Read<ApplyTeamDataRsp>(1641)).Team;
        }
        public async Task Match(Peer p, uint map = Map, bool any = false)
        {
            await Send(p, 1623, new ApplyBeginMatchReq { MapId = map, RoleId = p.Player.Roleid, SolutionId = 1, AllDiffMatch = any });
            Assert.Equal(1u, (await p.Read<ApplyBeginMatchRsp>(1624)).Ret);
        }
        public Task LuaCall(Peer p, int cmd, string param)
            => Lua.DispatchAsync(p.Session, "LuaCall", $"{{\"sCmd\":{cmd},\"tbParam\":{param}}}");
        public void Dispose()
        {
            foreach (var p in Peers) p.Dispose();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }

    [Fact]
    public void OriginalTableUsesEncodedRoomMapsAndOnlyVisibleNormalRows()
    {
        var table = CoopLevelTable.Load(TestPaths.SettingDir);
        Assert.Equal(73, table.Rows.Count);
        Assert.Equal(30, table.Rows.Count(r => r.Type == 1 && !r.Hidden));
        Assert.Equal(43, table.Rows.Count(r => r.Type == 1));
        Assert.Equal(5, table.MaxDifficulty(4));
        Assert.NotEqual(Map, (uint)table.Get(Map)!.Map);
        Assert.Null(table.Get((uint)table.Get(Map)!.Map));
    }
    [Theory]
    [InlineData("2026-10-28T03:59:59+08:00", false)]
    [InlineData("2026-10-28T04:00:00+08:00", true)]
    [InlineData("2026-10-29T03:59:59+08:00", true)]
    [InlineData("2026-10-29T04:00:00+08:00", false)]
    public void WeekdayChangesAtBeijingFour(string now, bool open)
        => Assert.Equal(open, CoopLevelTable.Load(TestPaths.SettingDir).Get(Map)!.IsOpen(DateTimeOffset.Parse(now)));

    [Fact]
    public async Task CreateUsesOwnedServerRosterAndEchoesRequestSerial()
    {
        using var h = new Lobby(); var p = await h.Add();
        await h.Send(p, 1627, new ApplyCreateTeamReq { MapId = Map, SolutionId = 1, IsPublic = true,
            Info = new PlayerBaseInfo { RoleId = 999, Name = "forged", Level = 999, Solution = new EquipSolution { Teampower = uint.MaxValue } } }, 123);
        var rsp = await p.Read<ApplyCreateTeamRsp>(1628);
        var info = Assert.Single(rsp.Team!.Players);
        Assert.Equal(p.Player.Roleid, info.RoleId); Assert.Equal(p.Player.Name, info.Name);
        Assert.Equal((uint)Math.Max(1, p.Player.Level), info.Level);
        Assert.Equal((uint)Math.Max(0, p.Player.Fightpower), info.Solution!.Teampower);
        Assert.Equal(3, info.Solution.Groups.Count);
        Assert.Equal(2u, info.Solution.Groups[0].Maincard!.D);
        Assert.All(info.Solution.Groups, g => { Assert.NotNull(g.Maincard); Assert.NotNull(g.Deputy3); Assert.NotNull(g.Weapon); Assert.NotNull(g.Rune4); });
        Assert.Equal(p.Player.Roleid, rsp.Team.LeaderId);
        Assert.Equal(Map, rsp.Team.Mapid);
        Assert.InRange(rsp.TeamId, 1ul, (ulong)int.MaxValue);
        // Serial is validated directly from a second reply to avoid consuming its decoded packet twice.
        await h.Send(p, 1639, new ApplyTeamDataReq(), 123);
        await Task.Delay(30); p.Drain();
        Assert.Contains(p.Packets, packet => packet.Pf == 1641 && packet.Serial == 123);
    }

    [Theory]
    [InlineData("scene")] [InlineData("hidden")] [InlineData("other-mode")] [InlineData("locked")]
    [InlineData("wrong-day")] [InlineData("role")] [InlineData("activity")] [InlineData("formation")]
    [InlineData("missing-card")] [InlineData("duplicate-card")] [InlineData("foreign-weapon")]
    [InlineData("orphan-equipment")] [InlineData("overflow")]
    public async Task InvalidCreationNeverReturnsAnEmptyNativeSuccessOrMutatesPlayer(string kind)
    {
        using var h = new Lobby(); var p = await h.Add();
        var table = CoopLevelTable.Load(TestPaths.SettingDir);
        var req = new ApplyCreateTeamReq { MapId = Map, SolutionId = 1 };
        switch (kind)
        {
            case "scene": req.MapId = (uint)table.Get(Map)!.Map; break;
            case "hidden": req.MapId = table.Rows.First(r => r.Hidden).RoomMap; break;
            case "other-mode": req.MapId = table.Rows.First(r => r.Type == 2).RoomMap; break;
            case "locked": p.Player.SetTask(38, 5, 1); req.MapId = Hard; break;
            case "wrong-day": h.Now = h.Now.AddDays(1); break;
            case "role": req.RoleId = p.Player.Roleid + 1; break;
            case "activity": req.ActivityId = 1; break;
            case "formation": req.SolutionId = 999; break;
            case "missing-card": p.Player.Items.Clear(); break;
            case "duplicate-card": p.Player.Formations[0].Slots.Add(new FormationCardSlot { MainCardGuid = p.Player.Formations[0].Slots[0].MainCardGuid }); break;
            case "foreign-weapon": p.Player.Formations[0].Slots[0].WeaponGuid = uint.MaxValue; break;
            case "orphan-equipment": p.Player.Formations[0].Slots.Add(new FormationCardSlot { WeaponGuid = uint.MaxValue }); break;
            case "overflow": req.SolutionId = uint.MaxValue; break;
        }
        string before = JsonSerializer.Serialize(p.Player);
        await h.Send(p, 1627, req);
        Assert.Contains(h.Errors, e => e.Session == p.Session && e.Function == "PvpMsg");
        Assert.Null(await h.Fetch(p)); p.Drain();
        Assert.DoesNotContain(p.Packets, packet => packet.Pf == 1628);
        Assert.Equal(before, JsonSerializer.Serialize(p.Player));
    }

    [Fact]
    public async Task ThreeRealPlayersOnlyAndJoinReplayIsIdempotent()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add(); var c = await h.Add(); var d = await h.Add();
        var room = await h.Create(a);
        Assert.Equal(room.TeamId, (await h.Create(a)).TeamId);
        Assert.Equal(0, (await h.Join(b, room)).Ret);
        Assert.Equal(0, (await h.Join(b, room)).Ret);
        Assert.Equal(0, (await h.Join(c, room)).Ret);
        Assert.NotEqual(0, (await h.Join(d, room)).Ret);
        var final = await h.Fetch(a);
        Assert.Equal(new[] { a.Player.Roleid, b.Player.Roleid, c.Player.Roleid }, final!.Players.Select(p => p.RoleId));
        Assert.Null(await h.Fetch(d));
        Assert.Equal(room.TeamId, (await b.Read<NotifyTeamData>(1653)).Team!.TeamId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MatchingHonorsPublicFlagButExplicitLinksAllowPrivateRooms(bool isPublic)
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add();
        var room = await h.Create(a, isPublic);
        await h.Match(b);
        if (isPublic) Assert.Equal(room.TeamId, (await h.Fetch(b))!.TeamId);
        else
        {
            Assert.Null(await h.Fetch(b));
            Assert.Equal(0, (await h.Join(b, room)).Ret);
        }
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, true)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task QueuedAnyDifficultyWorksInBothArrivalOrders(bool firstAny, bool secondAny, bool expected)
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add();
        await h.Match(a, Map, firstAny); await h.Match(b, Hard, secondAny);
        var first = await h.Fetch(a); var second = await h.Fetch(b);
        if (expected) { Assert.NotNull(first); Assert.Equal(first.TeamId, second!.TeamId); Assert.Equal(2, first.Players.Count); }
        else { Assert.Null(first); Assert.Null(second); }
    }
    [Fact]
    public async Task AnyDifficultyCannotBypassPlayersUnlock()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add();
        await h.Create(a, true, Hard); b.Player.SetTask(38, 5, 1);
        await h.Match(b, Map, true);
        Assert.Null(await h.Fetch(b));
    }
    [Fact]
    public async Task CancelledQueueCannotBeRecruitedByLaterRoom()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add();
        await h.Match(a); await h.Send(a, 1625, new ApplyEndMatchReq());
        Assert.Equal(1u, (await a.Read<ApplyEndMatchRsp>(1626)).Ret);
        Assert.Single((await h.Create(b)).Players); Assert.Null(await h.Fetch(a));
    }
    [Fact]
    public async Task LeaderCanOpenPrivateRoomAndRecruitWaitingPlayer()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add();
        var room = await h.Create(a, false); await h.Match(b);
        await h.Send(b, 1637, new ApplyModifyRoomPublicAttrReq { IsPublic = true });
        Assert.Null(await h.Fetch(b));
        await h.Send(a, 1637, new ApplyModifyRoomPublicAttrReq { IsPublic = true });
        var recruited = (await h.Fetch(b))!;
        Assert.Equal(room.TeamId, recruited.TeamId); Assert.True(recruited.IsPublic);
        Assert.True((await a.Read<NotifyRoomPublicAttr>(1655)).IsPublic);
    }

    [Fact]
    public async Task ReadyBroadcastsNativeStateAndStartNeverInventsFightOrRewards()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add();
        var room = await h.Create(a); await h.Join(b, room);
        string beforeA = JsonSerializer.Serialize(a.Player), beforeB = JsonSerializer.Serialize(b.Player);
        await h.Send(a, 1635, new ApplyTeamReadyReq { Ready = true, MapId = Map });
        Assert.False((await a.Read<ApplyTeamReadyRsp>(1636)).Ret);
        Assert.Equal("need_ready", (await a.Read<NotifyTeamMsg>(1656)).Msg);
        await h.Send(b, 1635, new ApplyTeamReadyReq { Ready = true, MapId = Map });
        Assert.True((await b.Read<ApplyTeamReadyRsp>(1636)).Ret);
        var first = await b.Read<NotifyTeamReadyData>(1654);
        Assert.Equal(b.Player.Roleid, first.RoleId); Assert.Equal(2, first.Team!.Players.Count);
        await h.Send(a, 1635, new ApplyTeamReadyReq { Ready = true, MapId = Map });
        Assert.False((await a.Read<ApplyTeamReadyRsp>(1636)).Ret);
        Assert.Equal("no_room", (await a.Read<NotifyTeamMsg>(1656)).Msg);
        Assert.Equal(beforeA, JsonSerializer.Serialize(a.Player)); Assert.Equal(beforeB, JsonSerializer.Serialize(b.Player));
        foreach (var p in h.Peers) { p.Drain(); Assert.DoesNotContain(p.Packets, q => q.Pf == 1652 || q.Pf >= 1690 && q.Pf < 1800); }
    }
    [Fact]
    public async Task SoloLeaderGetsNativeNeedPlayerMessage()
    {
        using var h = new Lobby(); var a = await h.Add(); await h.Create(a);
        await h.Send(a, 1635, new ApplyTeamReadyReq { Ready = true });
        Assert.Equal("need_player", (await a.Read<NotifyTeamMsg>(1656)).Msg);
    }
    [Fact]
    public async Task FormationEditingClearsReadyAndInvalidEquipmentIsPaddedUnavailable()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add(); var room = await h.Create(a); await h.Join(b, room);
        await h.Send(b, 1635, new ApplyTeamReadyReq { Ready = true });
        Assert.True((await b.Read<ApplyTeamReadyRsp>(1636)).Ret);
        await h.Send(b, 1635, new ApplyTeamReadyReq { Ready = false, IsInFormation = true });
        Assert.True((await b.Read<ApplyTeamReadyRsp>(1636)).Ret);
        var state = (await h.Fetch(a))!.Players[1]; Assert.False(state.Ready); Assert.True(state.IsInFormation);
        b.Player.Items.Clear(); state = (await h.Fetch(a))!.Players[1];
        Assert.Equal(1, state.Solution!.State); Assert.False(state.Ready); Assert.Equal(3, state.Solution.Groups.Count);
        Assert.All(state.Solution.Groups, g => Assert.NotNull(g.Maincard));
    }
    [Fact]
    public async Task UpdateUsesServerLoadoutAndRejectsMissingFormation()
    {
        using var h = new Lobby(); var a = await h.Add(); var room = await h.Create(a);
        await h.Send(a, 1631, new ApplyUpdatePlayerInfoReq { SolutionId = 999 });
        Assert.Equal(1u, (await a.Read<ApplyUpdatePlayerInfoRsp>(1632)).Ret);
        Assert.Equal(1, (await h.Fetch(a))!.Players[0].Solution!.SolutionId);
        await h.Send(a, 1631, new ApplyUpdatePlayerInfoReq { SolutionId = 1, Info = new PlayerBaseInfo { Name = "forged" } });
        Assert.Equal(0u, (await a.Read<ApplyUpdatePlayerInfoRsp>(1632)).Ret);
        Assert.Equal(a.Player.Name, (await h.Fetch(a))!.Players[0].Name);
    }
    [Fact]
    public async Task LeaderExitTransfersRoomToNextRealPlayer()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add(); var room = await h.Create(a); await h.Join(b, room);
        await h.Send(a, 1629, new ApplyExitTeamReq()); Assert.Equal(1u, (await a.Read<ApplyExitTeamRsp>(1630)).Ret);
        var remaining = (await h.Fetch(b))!; Assert.Equal(b.Player.Roleid, remaining.LeaderId); Assert.Single(remaining.Players);
        Assert.Null(await h.Fetch(a));
        await h.Send(b, 1629, new ApplyExitTeamReq()); await b.Read<ApplyExitTeamRsp>(1630);
        Assert.NotEqual(0, (await h.Join(a, room)).Ret);
    }
    [Fact]
    public async Task HeartbeatsKeepActiveMembersAndExpiredMembersCannotResurrect()
    {
        using var h = new Lobby(); var a = await h.Add(); var b = await h.Add(); var room = await h.Create(a); await h.Join(b, room);
        h.Now = h.Now.AddSeconds(20); h.Feature.OnPlayerPulse(b.Session, b.Player);
        h.Now = h.Now.AddSeconds(11); h.Feature.OnPlayerPulse(a.Session, a.Player);
        Assert.Null(await h.Fetch(a)); Assert.Single((await h.Fetch(b))!.Players);
        Assert.Equal(b.Player.Roleid, (await h.Fetch(b))!.LeaderId);
    }
    [Fact]
    public async Task ThirtyMinuteRoomLimitSurvivesRegularHeartbeats()
    {
        using var h = new Lobby(); var a = await h.Add(); await h.Create(a);
        for (int i = 0; i < 90; i++) { h.Now = h.Now.AddSeconds(20); h.Feature.OnPlayerPulse(a.Session, a.Player); }
        Assert.Null(await h.Fetch(a));
    }
    [Fact]
    public async Task ReplacementLoginRejectsOldTcpControlIncludingLuaExit()
    {
        using var h = new Lobby(); var old = await h.Add("same-account"); await h.Create(old);
        var fresh = await h.Add("same-account");
        h.Module.RunLoginHooksForTest(fresh.Session, fresh.Player);
        var room = await h.Create(fresh);
        await h.LuaCall(old, 41, "{}"); await h.LuaCall(old, 40, "{\"ret\":false}");
        await h.Send(old, 1629, new ApplyExitTeamReq());
        Assert.Equal(room.TeamId, (await h.Fetch(fresh))!.TeamId);
        Assert.Contains(h.Errors, e => e.Session == old.Session && e.Function == "PvpMsg");
    }
    [Theory]
    [InlineData(40)] [InlineData(41)]
    public async Task NativeLuaRoomRecoveryAndExitUseOwnedMembership(int exitCommand)
    {
        using var h = new Lobby(); var a = await h.Add(); var room = await h.Create(a);
        await h.LuaCall(a, 40, "{\"ret\":true}");
        Assert.Equal(room.TeamId, (await a.Read<NotifyTeamData>(1653)).Team!.TeamId);
        await h.LuaCall(a, exitCommand, "{\"ret\":false}"); Assert.Null(await h.Fetch(a));
    }
    [Fact]
    public async Task MalformedProtobufDoesNotCreateRoomOrThrowFromRouter()
    {
        using var h = new Lobby(); var a = await h.Add();
        await h.Router.DispatchAsync(a.Session, new Packet { Pf = 1627, Payload = new byte[] { 0x80 } });
        Assert.Contains(h.Errors, e => e.Function == "PvpMsg"); Assert.Null(await h.Fetch(a));
    }
}
