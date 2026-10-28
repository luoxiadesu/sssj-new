using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal sealed class GachaRequestLimits
{
    private readonly Dictionary<uint, int> _limits = new();
    internal GachaRequestLimits(string directory)
    {
        var table = SettingTable.Load(Path.Combine(directory, "LimitInfo.txt"));
        if (table is null) return;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "ID"), limit = table.Int(row, "MaxValue");
            if (id is 1 or 2 && limit > 0) _limits[(uint)id] = limit;
        }
    }

    // PlayerLogic.CheckMaxCard/Weapon reject count + draws >= configured limit.
    internal bool HasRoom(Player player, uint genre, int draws)
        => draws > 0 && _limits.TryGetValue(genre, out int limit)
            && (long)player.Items.Count(i => i.Genre == genre && i.Count > 0) + draws < limit;

    internal static bool TryInteger(JsonNode? node, out int value)
    {
        value = 0;
        if (node is not JsonValue scalar || scalar.GetValueKind() != JsonValueKind.Number) return false;
        if (!decimal.TryParse(scalar.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
            || number != decimal.Truncate(number) || number < 0 || number > int.MaxValue) return false;
        value = (int)number;
        return true;
    }

    internal static bool TryMode(JsonNode? node, out bool ten)
    {
        ten = false;
        return node is null || node is JsonValue scalar && scalar.TryGetValue(out ten);
    }
}
