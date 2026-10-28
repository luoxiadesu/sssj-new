using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class HouseCollisionLimits
{
    private sealed record Rect(HouseFurniture Item, int Type, long X, long Y, long Right, long Top);

    private static List<Rect> Rectangles(HouseTable table, HouseState state)
    {
        var result = new List<Rect>();
        foreach (var f in state.Furniture)
        {
            var row = table.ItemOf(f.TemplateId);
            if (f.RoomId == 0 || f.ParentId == 0 || row is null || row.Type < 100
                || row.Size.Count != 2 || row.Size[0] == 0 || row.Size[1] == 0 || f.Rotate % 90 != 0) continue;
            bool swap = f.Rotate % 180 != 0;
            long w = row.Size[swap ? 1 : 0], h = row.Size[swap ? 0 : 1];
            // The native comparator skips other objects whose Rect has zero width
            // AND height. Leave point-sized instances outside pair validation.
            result.Add(new(f, row.Type, f.PosX, f.PosY, f.PosX + w - 1, f.PosY + h - 1));
        }
        return result;
    }

    private static long Area(long x, long y, long right, long top, long bx, long by, long br, long bt)
    {
        long width = Math.Min(right, br) - Math.Max(x, bx) + 1;
        long height = Math.Min(top, bt) - Math.Max(y, by) + 1;
        return width > 0 && height > 0 ? width * height : 0;
    }

    private static Dictionary<(uint A, uint B, uint Parent, int Room), long> Conflicts(HouseTable table, HouseState state)
    {
        var result = new Dictionary<(uint, uint, uint, int), long>();
        var rects = Rectangles(table, state);
        foreach (var r in rects)
        {
            if (!table.Blocks.TryGetValue(r.Item.ParentId, out var blocks)) continue;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                long area = Area(r.X, r.Y, r.Right, r.Top, b[0], b[1], b[2], b[3]);
                if (area > 0) result[(r.Item.ObjId, (uint)i, r.Item.ParentId, -r.Item.RoomId)] = area;
            }
        }
        foreach (var group in rects.GroupBy(r => (r.Item.RoomId, r.Item.ParentId, Carpet: r.Type == table.CarpetTypeId)))
        {
            var list = group.Where(r => r.Right != r.X || r.Top != r.Y).ToArray();
            for (int i = 0; i < list.Length; i++)
            for (int j = i + 1; j < list.Length; j++)
            {
                var a = list[i]; var b = list[j];
                long area = Area(a.X, a.Y, a.Right, a.Top, b.X, b.Y, b.Right, b.Top);
                if (area > 0) result[(Math.Min(a.Item.ObjId, b.Item.ObjId), Math.Max(a.Item.ObjId, b.Item.ObjId), a.Item.ParentId, a.Item.RoomId)] = area;
            }
        }
        return result;
    }

    internal static bool NoWorse(HouseTable table, HouseState before, HouseState after)
    {
        var previous = Conflicts(table, before);
        return Conflicts(table, after).All(c => c.Value <= previous.GetValueOrDefault(c.Key));
    }
}
