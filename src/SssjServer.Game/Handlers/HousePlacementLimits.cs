using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal sealed class HousePlacementLimits
{
    private readonly SettingTable? _levels;
    private readonly List<(int Id, int Room, HashSet<int> Types, int[] Limits)> _rules = [];

    internal HousePlacementLimits(string directory)
    {
        _levels = SettingTable.Load(Path.Combine(directory, CafeTable.LevelFileName));
        var table = SettingTable.Load(Path.Combine(directory, CafeTable.ItemTypeFileName));
        if (table is null) return;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "Id");
            int room = table.Int(row, "Area");
            if (id <= 0 || !HouseRooms.Ids.Contains(room)) continue;
            _rules.Add((id, room, Integers(table.Str(row, "Types")).ToHashSet(),
                Integers(table.Str(row, "Limit"))));
        }
    }

    private static int[] Integers(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s.Trim(), out int v) ? v : -1).ToArray();

    internal bool RoomOpen(int level, int room)
    {
        if (room is 0 or HouseFeature.OutdoorRoomId) return true;
        var row = _levels?.Rows.FirstOrDefault(r => _levels.Int(r, "Level") == level);
        if (row is null) return false;
        return room == 1 || room == 2 && _levels!.Int(row, "OpenRest") > 0
            || room == 6 && _levels!.Int(row, "OpenDorm") > 0;
    }

    // Existing over-cap inventories remain editable. Only increased excess is
    // refused, so moving/reducing legacy furniture does not destroy user layouts.
    internal bool WithinLimits(HouseTable table, HouseState before, HouseState after, int level)
    {
        foreach (var rule in _rules)
        {
            int Count(HouseState state) => state.Furniture.Where(f => f.RoomId == rule.Room
                    && f.ParentId != 0 && table.Items.TryGetValue(f.TemplateId, out var row)
                    && !row.IsSystem && rule.Types.Contains(row.Type))
                .Sum(f => rule.Id == CafeTable.SeatItemTypeId ? table.Items[f.TemplateId].BizPointCount : 1);
            int oldCount = Count(before), newCount = Count(after);
            int limit = level > 0 && level <= rule.Limits.Length ? rule.Limits[level - 1] : 0;
            if (newCount > Math.Max(0, limit) && newCount > oldCount) return false;
        }
        // Missing type configuration cannot authorize increased placements.
        return _rules.Count > 0 || after.Furniture.Count(f => f.RoomId != 0 && f.ParentId != 0)
            <= before.Furniture.Count(f => f.RoomId != 0 && f.ParentId != 0);
    }

    // The outdoor selector selects one scene. This is a saved-layout constraint,
    // not a claim to reproduce Unity GameObject.activeInHierarchy on the server.
    internal static bool OutdoorLimit(HouseTable table, HouseState before, HouseState after)
    {
        int Count(HouseState state) => state.Furniture.Count(f => f.RoomId != 0 && f.ParentId != 0
            && table.ItemOf(f.TemplateId)?.Type == table.OutDoorScene);
        return Count(after) <= Math.Max(1, Count(before));
    }
}
