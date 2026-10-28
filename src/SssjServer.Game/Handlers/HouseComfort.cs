using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>UI_CafeComfort.OnToggleInfo / CoffeeCommon.DecorType:
/// owned instances ranked by score, with category limits summed across rooms.</summary>
internal static class HouseComfort
{
    private static readonly int[][] Categories =
    [
        [50, 51, 55, 56, 58, 60, 61, 62, 64, 80, 83, 81, 84, 73],
        [201], [104], [103], [101], [100]
    ];

    internal static int Calculate(HouseTable table, HouseState inventory, int level, int maximum)
    {
        long total = 0;
        foreach (var types in Categories)
        {
            int limit = table.ItemTypes.Values.Where(r => r.Types.Any(types.Contains))
                .Sum(r => r.Limit.Count == 0 || level <= 0 ? 0
                    : Math.Max(0, r.Limit[Math.Min(level, r.Limit.Count) - 1]));
            total += inventory.Furniture.Select(f => table.Items.GetValueOrDefault(f.TemplateId))
                .Where(r => r is not null && !r.IsSystem && types.Contains(r.Type) && r.AddScore > 0)
                .Select(r => r!.AddScore).OrderByDescending(score => score).Take(limit).Sum(score => (long)score);
        }
        return (int)Math.Clamp(total, 0, Math.Max(0, maximum));
    }
}
