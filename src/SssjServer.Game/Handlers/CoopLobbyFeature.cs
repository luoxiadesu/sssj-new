using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol;
using SssjServer.Protocol.PvpProto;

namespace SssjServer.Game.Handlers;

/// <summary>Native three-player cooperative lobby. No fake teammates, battle server or victory reports.</summary>
public sealed class CoopLobbyFeature : IGameFeature, IGamePulseFeature
{
    private sealed record Member(Player Player, ClientSession Session, uint Formation, DateTimeOffset Seen)
    {
        public bool Ready { get; set; }
        public bool InFormation { get; set; }
        public DateTimeOffset LastSeen { get; set; } = Seen;
    }
    private sealed class Room
    {
        public ulong Id;
        public uint Map;
        public bool Public;
        public DateTimeOffset Expires;
        public List<Member> Members = new();
    }
    private sealed record Waiting(Member Member, uint Map, bool AnyDifficulty);
    private readonly object _sync = new();
    private readonly Dictionary<ulong, Room> _rooms = new();
    private readonly Dictionary<ulong, Room> _joined = new();
    private readonly Dictionary<ulong, Waiting> _waiting = new();
    private readonly Dictionary<ulong, ClientSession> _currentSessions = new();
    private GameFeatureContext _ctx = null!;
    private CoopLevelTable _levels = null!;
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public string Name => "协同出击原生房间与真实玩家匹配";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _levels = CoopLevelTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        Map<ApplyCreateTeamReq>(1627, Create);
        Map<ApplyExitTeamReq>(1629, Exit);
        Map<ApplyBeginMatchReq>(1623, Match);
        Map<ApplyEndMatchReq>(1625, EndMatch);
        Map<ApplyJoinTeamReq>(1633, Join);
        Map<ApplyTeamReadyReq>(1635, Ready);
        Map<ApplyUpdatePlayerInfoReq>(1631, Update);
        Map<ApplyModifyRoomPublicAttrReq>(1637, Access);
        Map<ApplyTeamDataReq>(1639, Fetch);
        ctx.MapLuaCall(40, ReturnRoom);
        ctx.MapLuaCall(41, ForceExit);
        ctx.MapLuaCall(42, (_, _, _) => Task.CompletedTask); // Client matching telemetry; no gameplay effect.
        ctx.OnPlayerLogin((session, player) =>
        {
            lock (_sync)
            {
                // A replacement login must not let the old connection control a lobby.
                if (_joined.TryGetValue(player.Roleid, out var room)
                    && room.Members.Any(m => m.Player.Roleid == player.Roleid && !ReferenceEquals(m.Session, session))) Remove(player.Roleid);
                _waiting.Remove(player.Roleid);
                _currentSessions[player.Roleid] = session;
            }
        });
    }

    private void Map<T>(int pf, Action<ClientSession, Player, Packet, T> action) where T : ProtoMessage, new()
        => _ctx.MapPf(pf, (session, packet) =>
        {
            if (session.Tag is not Player player) return Task.CompletedTask;
            T request;
            try { request = ProtoMessage.Parse<T>(packet.Payload); }
            catch (Exception e) { Log.Warn($"[coop] Invalid pf {pf}: {e.Message}"); Error(session); return Task.CompletedTask; }
            lock (_sync)
            {
                if (!Current(session, player)) { Error(session); return Task.CompletedTask; }
                Sweep();
                Touch(session, player);
                action(session, player, packet, request);
            }
            return Task.CompletedTask;
        });

    private bool Identity(Player player, ulong role) => role == 0 || role == player.Roleid;
    private bool Current(ClientSession session, Player player)
    {
        if (_currentSessions.TryGetValue(player.Roleid, out var current)) return ReferenceEquals(current, session);
        _currentSessions.Add(player.Roleid, session);
        return true;
    }
    private Member? MakeMember(ClientSession session, Player player, uint formation, uint map)
    {
        if (formation > int.MaxValue || !_levels.CanEnter(player, map, Clock())) return null;
        lock (player)
            if (CoopLobbyRoster.Build(_ctx, player, formation) is null) return null;
        return new(player, session, formation, Clock());
    }
    private PlayerBaseInfo Roster(Member member)
    {
        PlayerBaseInfo? value;
        lock (member.Player) value = CoopLobbyRoster.Build(_ctx, member.Player, member.Formation);
        // An edited/deleted loadout keeps the player in the room but marks it unavailable.
        if (value is null)
        {
            member.Ready = false;
            value = new PlayerBaseInfo { RoleId = member.Player.Roleid, Name = member.Player.Name,
                Level = (uint)Math.Max(1, member.Player.Level), Solution = CoopLobbyRoster.Unavailable(member.Player) };
        }
        value.Ready = member.Ready;
        value.IsInFormation = member.InFormation;
        return value;
    }
    private Team Snapshot(Room room)
    {
        var team = new Team { TeamId = room.Id, LeaderId = room.Members[0].Player.Roleid,
            Mapid = room.Map, IsPublic = room.Public, CloseTime = (int)Math.Max(0, (room.Expires - Clock()).TotalSeconds),
            CloseTimeClient = 0, ActivityId = 0, Mgrid = 1 };
        foreach (var member in room.Members) team.Players.Add(Roster(member));
        return team;
    }
    private void Broadcast(Room room)
    {
        if (room.Members.Count == 0) return;
        foreach (var member in room.Members)
            member.Session.Push(1653, new NotifyTeamData { RoleId = member.Player.Roleid, Team = Snapshot(room) });
    }
    private void Error(ClientSession session)
        => _ctx.LuaCalls.CallClientFunction(session, "PvpMsg", new JsonObject { ["nError"] = 29998 });
    private void Message(ClientSession session, string message)
        => session.Push(1656, new NotifyTeamMsg { RoleId = (session.Tag as Player)?.Roleid ?? 0, Msg = message });
    private Room NewRoom(Member member, uint map, bool isPublic)
    {
        ulong id;
        do { id = (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue); } while (_rooms.ContainsKey(id));
        var room = new Room { Id = id, Map = map, Public = isPublic, Expires = Clock().AddMinutes(30) };
        room.Members.Add(member); _rooms.Add(id, room); _joined.Add(member.Player.Roleid, room);
        _waiting.Remove(member.Player.Roleid);
        return room;
    }
    private void Create(ClientSession session, Player player, Packet packet, ApplyCreateTeamReq req)
    {
        if (!Identity(player, req.RoleId) || req.ActivityId != 0) { Error(session); return; }
        if (_joined.TryGetValue(player.Roleid, out var current))
        {
            if (current.Map != req.MapId || !OwnSession(current, session, player)) { Error(session); return; }
            session.Send(1628, packet.Serial, new ApplyCreateTeamRsp { TeamId = current.Id, Team = Snapshot(current), RoleId = player.Roleid, MapId = current.Map });
            return;
        }
        var member = MakeMember(session, player, req.SolutionId, req.MapId);
        if (member is null) { Error(session); return; }
        var room = NewRoom(member, req.MapId, req.IsPublic);
        session.Send(1628, packet.Serial, new ApplyCreateTeamRsp { TeamId = room.Id, Team = Snapshot(room), RoleId = player.Roleid, MapId = room.Map, ActivityId = 0 });
        Fill(room);
    }
    private bool OwnSession(Room room, ClientSession session, Player player)
        => room.Members.Any(m => m.Player.Roleid == player.Roleid && ReferenceEquals(m.Session, session));
    private void Remove(ulong role)
    {
        _waiting.Remove(role);
        if (!_joined.Remove(role, out var room)) return;
        room.Members.RemoveAll(m => m.Player.Roleid == role);
        if (room.Members.Count == 0) _rooms.Remove(room.Id);
        else { foreach (var m in room.Members) m.Ready = false; Broadcast(room); }
    }
    private void Exit(ClientSession session, Player player, Packet packet, ApplyExitTeamReq req)
    {
        if (!Identity(player, req.RoleId) || _joined.TryGetValue(player.Roleid, out var room) && !OwnSession(room, session, player)) { Error(session); return; }
        Remove(player.Roleid);
        session.Send(1630, packet.Serial, new ApplyExitTeamRsp { RoleId = player.Roleid, Ret = 1 });
    }
    private void Fetch(ClientSession session, Player player, Packet packet, ApplyTeamDataReq req)
    {
        if (!Identity(player, req.RoleId)) { Error(session); return; }
        _joined.TryGetValue(player.Roleid, out var room);
        var response = new ApplyTeamDataRsp { RoleId = player.Roleid };
        // Optional-message setters mark the field present even for null; omit it for no room.
        if (room is not null && OwnSession(room, session, player)) response.Team = Snapshot(room);
        session.Send(1641, packet.Serial, response);
    }
    private void Join(ClientSession session, Player player, Packet packet, ApplyJoinTeamReq req)
    {
        if (!Identity(player, req.RoleId) || req.ActivityId != 0 || !_rooms.TryGetValue(req.TeamId, out var room)
            || req.Mgrid != 0 && req.Mgrid != 1 || _joined.TryGetValue(player.Roleid, out var other) && other != room)
        { session.Send(1634, packet.Serial, new ApplyJoinTeamRsp { RoleId = player.Roleid, Ret = 1 }); return; }
        if (OwnSession(room, session, player))
        { session.Send(1634, packet.Serial, new ApplyJoinTeamRsp { RoleId = player.Roleid, Ret = 0, TeamId = room.Id, Team = Snapshot(room) }); return; }
        var member = MakeMember(session, player, req.SolutionId, room.Map);
        // Private rooms can be entered by their explicit room link; matchmaking never selects them.
        if (member is null || room.Members.Count >= 3 || _joined.ContainsKey(player.Roleid))
        { session.Send(1634, packet.Serial, new ApplyJoinTeamRsp { RoleId = player.Roleid, Ret = 1 }); return; }
        room.Members.Add(member); _joined.Add(player.Roleid, room); _waiting.Remove(player.Roleid);
        session.Send(1634, packet.Serial, new ApplyJoinTeamRsp { RoleId = player.Roleid, Ret = 0, TeamId = room.Id, Team = Snapshot(room) });
        Broadcast(room);
    }
    private bool Matches(Waiting waiting, Room room) => room.Public && room.Members.Count < 3
        && (waiting.Map == room.Map || waiting.AnyDifficulty && waiting.Map >> 16 == room.Map >> 16)
        && _levels.CanEnter(waiting.Member.Player, room.Map, Clock());
    private void Fill(Room room)
    {
        bool changed = false;
        foreach (var waiting in _waiting.Values.ToArray())
        {
            if (!Matches(waiting, room)) continue;
            var member = MakeMember(waiting.Member.Session, waiting.Member.Player, waiting.Member.Formation, room.Map);
            _waiting.Remove(waiting.Member.Player.Roleid);
            if (member is null) continue;
            room.Members.Add(member); _joined.Add(member.Player.Roleid, room); changed = true;
        }
        if (changed) Broadcast(room);
    }
    private void Match(ClientSession session, Player player, Packet packet, ApplyBeginMatchReq req)
    {
        if (!Identity(player, req.RoleId) || req.ActivityId != 0 || _joined.ContainsKey(player.Roleid)) { Error(session); return; }
        var member = MakeMember(session, player, req.SolutionId, req.MapId);
        if (member is null) { Error(session); return; }
        var waiting = new Waiting(member, req.MapId, req.AllDiffMatch);
        _waiting[player.Roleid] = waiting;
        session.Send(1624, packet.Serial, new ApplyBeginMatchRsp { RoleId = player.Roleid, MapId = req.MapId, Ret = 1 });
        var room = _rooms.Values.FirstOrDefault(r => Matches(waiting, r));
        if (room is null)
        {
            foreach (var peer in _waiting.Values.ToArray())
            {
                if (peer.Member.Player.Roleid == player.Roleid) continue;
                bool Accepts(Waiting w, uint map) => (w.Map == map || w.AnyDifficulty && w.Map >> 16 == map >> 16)
                    && _levels.CanEnter(w.Member.Player, map, Clock());
                uint chosen = new[] { req.MapId, peer.Map }.FirstOrDefault(map => Accepts(waiting, map) && Accepts(peer, map));
                if (chosen == 0) continue;
                var fresh = MakeMember(peer.Member.Session, peer.Member.Player, peer.Member.Formation, chosen);
                if (fresh is null) { _waiting.Remove(peer.Member.Player.Roleid); continue; }
                room = NewRoom(fresh, chosen, true);
                break;
            }
            if (room is null) return;
        }
        Fill(room);
    }
    private void EndMatch(ClientSession session, Player player, Packet packet, ApplyEndMatchReq req)
    {
        if (!Identity(player, req.RoleId)) { Error(session); return; }
        _waiting.Remove(player.Roleid);
        session.Send(1626, packet.Serial, new ApplyEndMatchRsp { RoleId = player.Roleid, Ret = 1 });
    }
    private void Ready(ClientSession session, Player player, Packet packet, ApplyTeamReadyReq req)
    {
        bool valid = Identity(player, req.RoleId) && _joined.TryGetValue(player.Roleid, out _);
        var room = valid ? _joined[player.Roleid] : null;
        var member = room?.Members.FirstOrDefault(m => m.Player.Roleid == player.Roleid && ReferenceEquals(m.Session, session));
        valid = member is not null && (req.MapId == 0 || req.MapId == room!.Map)
            && _levels.CanEnter(player, room!.Map, Clock());
        if (!valid) { session.Send(1636, packet.Serial, new ApplyTeamReadyRsp { RoleId = player.Roleid, Ret = false }); return; }
        var roster = Roster(member!);
        foreach (var other in room!.Members.Where(m => m != member)) _ = Roster(other);
        member!.InFormation = req.IsInFormation;
        member.Ready = req.Ready && !req.IsInFormation && roster.Solution?.State == 0;
        bool leaderStart = room!.Members[0] == member && req.Ready && !req.IsInFormation;
        if (leaderStart)
        {
            member.Ready = false;
            Message(session, room.Members.Count < 2 ? "need_player" : room.Members.Skip(1).Any(m => !m.Ready || m.InFormation) ? "need_ready" : "no_room");
        }
        session.Send(1636, packet.Serial, new ApplyTeamReadyRsp { RoleId = player.Roleid, Ret = !leaderStart && member.Ready == req.Ready });
        var data = new TeamReadyData { WhoId = player.Roleid, WhoName = player.Name };
        foreach (var m in room.Members) data.Players.Add(new PlayerReadyData { RoleId = m.Player.Roleid, Ready = m.Ready, IsInFormation = m.InFormation });
        foreach (var m in room.Members) m.Session.Push(1654, new NotifyTeamReadyData { RoleId = m.Player.Roleid, Team = data });
    }
    private void Update(ClientSession session, Player player, Packet packet, ApplyUpdatePlayerInfoReq req)
    {
        if (!Identity(player, req.RoleId) || !_joined.TryGetValue(player.Roleid, out var room) || !OwnSession(room, session, player)) { Error(session); return; }
        var fresh = MakeMember(session, player, req.SolutionId, room.Map);
        if (fresh is null) { session.Send(1632, packet.Serial, new ApplyUpdatePlayerInfoRsp { RoleId = player.Roleid, Ret = 1 }); return; }
        int index = room.Members.FindIndex(m => m.Player.Roleid == player.Roleid);
        room.Members[index] = fresh;
        session.Send(1632, packet.Serial, new ApplyUpdatePlayerInfoRsp { RoleId = player.Roleid, Ret = 0 });
        Broadcast(room);
    }
    private void Access(ClientSession session, Player player, Packet packet, ApplyModifyRoomPublicAttrReq req)
    {
        if (!Identity(player, req.RoleId) || !_joined.TryGetValue(player.Roleid, out var room)
            || room.Members[0].Player.Roleid != player.Roleid || !OwnSession(room, session, player)) { Error(session); return; }
        room.Public = req.IsPublic;
        foreach (var member in room.Members) member.Session.Push(1655, new NotifyRoomPublicAttr { IsPublic = room.Public });
        if (room.Public) Fill(room);
    }
    private void Touch(ClientSession session, Player player)
    {
        if (_joined.TryGetValue(player.Roleid, out var room))
            foreach (var member in room.Members.Where(m => m.Player.Roleid == player.Roleid && ReferenceEquals(m.Session, session))) member.LastSeen = Clock();
        if (_waiting.TryGetValue(player.Roleid, out var waiting) && ReferenceEquals(waiting.Member.Session, session)) waiting.Member.LastSeen = Clock();
    }
    private void Sweep()
    {
        var now = Clock();
        foreach (var room in _rooms.Values.ToArray())
            foreach (var member in room.Members.ToArray())
                if (room.Expires <= now || now - member.LastSeen > TimeSpan.FromSeconds(30))
                { member.Session.Push(1630, new ApplyExitTeamRsp { RoleId = member.Player.Roleid, Ret = 1 }); Remove(member.Player.Roleid); }
        foreach (var w in _waiting.Values.ToArray())
            if (now - w.Member.LastSeen > TimeSpan.FromSeconds(30))
            { _waiting.Remove(w.Member.Player.Roleid); w.Member.Session.Push(1626, new ApplyEndMatchRsp { RoleId = w.Member.Player.Roleid, Ret = 1 }); }
    }
    public void OnPlayerPulse(ClientSession session, Player player)
    { lock (_sync) { if (!Current(session, player)) return; Sweep(); Touch(session, player); } }
    private Task ReturnRoom(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (_sync)
        {
            if (!Current(session, player)) return Task.CompletedTask;
            Sweep();
            bool back = param is JsonObject o && o["ret"] is JsonValue v && v.TryGetValue<bool>(out bool value) && value;
            if (!back) Remove(player.Roleid);
            else if (_joined.TryGetValue(player.Roleid, out var room) && OwnSession(room, session, player)) Broadcast(room);
            else Message(session, "no_room");
        }
        return Task.CompletedTask;
    }
    private Task ForceExit(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player) lock (_sync)
        {
            if (!Current(session, player)) return Task.CompletedTask;
            Remove(player.Roleid); session.Push(1630, new ApplyExitTeamRsp { RoleId = player.Roleid, Ret = 1 });
        }
        return Task.CompletedTask;
    }
}
