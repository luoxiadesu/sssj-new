using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

public sealed partial class HouseFeature
{
    // Keep these separate from house.themes, which records purchased official sets.
    private const string PlansKey = "customplans";
    private const string ActivePlansKey = "activeplans";
    private const string NextPlanKey = "nextplanid";
    // UI_NCafeEditor:878 rejects adding only when TotalThemeCount() > 100.
    private const int MaxCustomPlans = 101;

    internal sealed record PlanPose(uint Template, uint Parent, uint X, uint Y, uint Rotate);
    internal sealed record CustomPlan(uint Id, string Name, int Room, ulong Created, List<PlanPose> Poses)
    {
        public decorationTheme ToProto()
        {
            var theme = new decorationTheme { ThemeId = Id, TemplateThemeId = 0,
                Name = Name, Roomtype = (uint)Room, Createtime = Created, NewGenerate = false };
            foreach (var p in Poses)
                theme.Poses.Add(new objPose { TemplateId = p.Template, ParentTemplateId = p.Parent,
                    PosX = p.X, PosY = p.Y, Rotate = p.Rotate });
            return theme;
        }
    }

    internal static List<CustomPlan> ReadPlans(Player player)
    {
        var result = new List<CustomPlan>();
        var arr = (player.Extra.GetValueOrDefault(ExtraKey) as JsonObject)?[PlansKey] as JsonArray;
        if (arr is null) return result;
        foreach (var node in arr)
        {
            try
            {
                var plan = node?.Deserialize<CustomPlan>();
                if (plan is { Id: > 0, Name: not null, Poses: not null }
                    && HouseRooms.Ids.Contains(plan.Room) && result.All(p => p.Id != plan.Id))
                    result.Add(plan);
            }
            catch (JsonException) { Log.Warn("[house] Ignored malformed custom plan"); }
        }
        return result;
    }

    private static Dictionary<int, uint> ReadActivePlans(Player player)
    {
        var active = new Dictionary<int, uint>();
        var obj = (player.Extra.GetValueOrDefault(ExtraKey) as JsonObject)?[ActivePlansKey] as JsonObject;
        foreach (int room in HouseRooms.Ids)
            active[room] = ReadUInt(obj?[room.ToString()], 0);
        return active;
    }

    private static void SavePlans(Player player, List<CustomPlan> plans)
        => WriteHouseKey(player, PlansKey, JsonSerializer.SerializeToNode(plans));

    private static void SaveActivePlans(Player player, Dictionary<int, uint> active)
    {
        var obj = new JsonObject();
        foreach (var (room, id) in active) obj[room.ToString()] = id;
        WriteHouseKey(player, ActivePlansKey, obj);
    }

    private static void AddPlansToCache(Player player, HouseCache cache)
    {
        var plans = ReadPlans(player);
        cache.ThemeList.AddRange(plans.Select(p => p.ToProto()));
        var active = ReadActivePlans(player);
        foreach (var room in cache.RoomList)
            if (active.TryGetValue((int)room.RoomId, out uint id)
                && plans.Any(p => p.Id == id && p.Room == room.RoomId)) room.ThemeId = id;
    }

    private bool ValidPlanPoses(decorationTheme theme, HouseState state)
    {
        var available = state.Furniture.GroupBy(f => f.TemplateId)
            .ToDictionary(g => (uint)g.Key, g => g.Count());
        foreach (var pose in theme.Poses)
        {
            if (!pose.HasTemplateId || !pose.HasParentTemplateId || !pose.HasPosX
                || !pose.HasPosY || !pose.HasRotate
                || !_table.Items.TryGetValue((int)pose.TemplateId, out var row)
                || row.Type < 100 || _table.IsPoolBlockedTemplate(row.Id)
                || !_table.IsAllowedParent(row.Id, pose.ParentTemplateId)
                || !_table.Items.TryGetValue((int)pose.ParentTemplateId, out var parent)
                || !HousePoseLimits.Valid(parent, pose.PosX, pose.PosY, pose.Rotate, row)
                || (parent.DicPlace != 0 && parent.DicPlace != theme.Roomtype)
                || !available.TryGetValue(pose.TemplateId, out int count) || count <= 0)
                return false;
            available[pose.TemplateId]--;
        }
        var layout = new HouseState { Furniture = theme.Poses.Select((p, i) => new HouseFurniture(
            (uint)(HouseTable.MinPlayerObjId + i), (int)p.TemplateId, (int)theme.Roomtype,
            p.ParentTemplateId, p.PosX, p.PosY, p.Rotate, 0, 0)).ToList() };
        return HouseCollisionLimits.NoWorse(_table, new HouseState(), layout);
    }

    private bool UpdatePlans(Player player, HouseState state, UpdateHouseReq req,
        UpdateHouseRsp reply, int sessionId, bool acceptRoomChanges = true)
    {
        var plans = ReadPlans(player);
        var active = ReadActivePlans(player);
        var house = player.Extra.GetValueOrDefault(ExtraKey) as JsonObject;
        uint next = Math.Max(ReadUInt(house?[NextPlanKey], 1),
            plans.Count == 0 ? 1 : plans.Max(p => p.Id) == uint.MaxValue ? uint.MaxValue : plans.Max(p => p.Id) + 1);
        bool changed = false;
        var seen = new HashSet<uint>();
        foreach (var theme in req.Themes)
        {
            bool create = theme.NewGenerate && theme.ThemeId == 0;
            var old = plans.FirstOrDefault(p => p.Id == theme.ThemeId);
            bool valid = theme.TemplateThemeId == 0 && theme.HasRoomtype
                && HouseRooms.Ids.Contains((int)theme.Roomtype)
                && theme.HasName && !string.IsNullOrWhiteSpace(theme.Name)
                && ValidPlanPoses(theme, state)
                && (create ? plans.Count < MaxCustomPlans && next < uint.MaxValue
                    : !theme.NewGenerate && old is not null && old.Room == theme.Roomtype
                      && seen.Add(theme.ThemeId));
            if (!valid)
            {
                Log.Warn($"[session {sessionId}] Invalid custom plan id={theme.ThemeId}; unchanged");
                if (old is not null) reply.NewThemes.Add(old.ToProto());
                continue;
            }
            uint id = create ? next++ : theme.ThemeId;
            var plan = new CustomPlan(id, theme.Name, (int)theme.Roomtype,
                old?.Created ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                theme.Poses.Select(p => new PlanPose(p.TemplateId, p.ParentTemplateId,
                    p.PosX, p.PosY, p.Rotate)).ToList());
            if (old is not null) plans.Remove(old);
            plans.Add(plan);
            reply.NewThemes.Add(plan.ToProto());
            changed = true;
        }
        foreach (var room in req.Rooms)
        {
            if (!acceptRoomChanges) break;
            if (!room.HasThemeId || !HouseRooms.Ids.Contains((int)room.RoomId)) continue;
            if (room.ThemeId != 0 && _placementLimits is not null
                && !_placementLimits.RoomOpen(CafeFeature.ReadState(player, _cafe).Level, (int)room.RoomId))
                continue;
            if (room.ThemeId != 0 && !plans.Any(p => p.Id == room.ThemeId && p.Room == room.RoomId))
                continue;
            if (active[(int)room.RoomId] == room.ThemeId) continue;
            active[(int)room.RoomId] = room.ThemeId;
            changed = true;
        }
        if (changed)
        {
            SavePlans(player, plans);
            SaveActivePlans(player, active);
            WriteHouseKey(player, NextPlanKey, JsonValue.Create(next));
        }
        return changed;
    }

    internal Task HandleDeletePlan(IGameSession session, Packet packet)
    {
        var reply = new DelDecorationThemeRsp();
        if (session.Tag is Player player)
        {
            reply.Roleid = player.Roleid;
            try
            {
                var req = ProtoMessage.Parse<DelDecorationThemeReq>(packet.Payload);
                if ((!req.HasRoleid || req.Roleid == player.Roleid) && req.ThemeId > 0)
                {
                    var plans = ReadPlans(player);
                    if (plans.RemoveAll(p => p.Id == req.ThemeId) > 0)
                    {
                        SavePlans(player, plans);
                        var active = ReadActivePlans(player);
                        foreach (int room in HouseRooms.Ids)
                            if (active[room] == req.ThemeId) active[room] = 0;
                        SaveActivePlans(player, active);
                        _ctx?.Store.Save(player);
                    }
                    reply.ThemeId = req.ThemeId; // deletion retries are harmless
                }
            }
            catch (Exception e) { Log.Warn($"[session {session.Id}] Invalid delete-plan request: {e.Message}"); }
        }
        session.Send(Pf.DelDecorationThemeRsp, packet.Serial, reply);
        return Task.CompletedTask;
    }
}
