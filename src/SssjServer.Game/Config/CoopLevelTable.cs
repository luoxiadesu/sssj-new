namespace SssjServer.Game.Config;

public sealed record CoopLevelRow(int Id, int Difficulty, int Type, int Map, int PreCost, int Vigour,
    int[] OpenDays, bool Hidden)
{
    public uint RoomMap => checked((uint)((Id << 16) | Difficulty));
    public bool IsOpen(DateTimeOffset now)
    {
        var day = now.ToOffset(TimeSpan.FromHours(8)).AddHours(-4).DayOfWeek;
        int weekday = day == DayOfWeek.Sunday ? 7 : (int)day;
        return OpenDays.Contains(weekday);
    }
}

/// <summary>Original Pvp/Levels, extracted by container path; flat Levels.txt belongs to another mode.</summary>
public sealed class CoopLevelTable
{
    public const string FileName = "PvpLevels.txt";
    private readonly Dictionary<uint, CoopLevelRow> _rows = new();
    public IReadOnlyCollection<CoopLevelRow> Rows => _rows.Values;
    public CoopLevelRow? Get(uint map) => _rows.GetValueOrDefault(map);
    public int MaxDifficulty(int id) => _rows.Values.Where(r => r.Id == id && r.Type == 1 && !r.Hidden)
        .Select(r => r.Difficulty).DefaultIfEmpty(0).Max();

    public bool CanEnter(Player player, uint map, DateTimeOffset now)
    {
        var row = Get(map);
        if (row is null || row.Type != 1 || row.Hidden || !row.IsOpen(now)) return false;
        int unlocked = Math.Clamp(player.GetTask(38, row.Id + 1), 1, Math.Max(1, MaxDifficulty(row.Id)));
        return row.Difficulty <= unlocked;
    }

    public static CoopLevelTable Load(string directory)
    {
        var table = new CoopLevelTable();
        var raw = SettingTable.Load(Path.Combine(directory, FileName), 2);
        if (raw is null || !raw.Columns.ContainsKey("Type")) return table;
        foreach (var row in raw.Rows)
        {
            int id = raw.Int(row, "Id"), diff = raw.Int(row, "Diff");
            if (id is <= 0 or > 32767 || diff is <= 0 or > 65535) continue;
            var days = raw.Str(row, "OpenDay").Trim('[', ']').Split(',')
                .Select(v => int.TryParse(v.Trim(), out int d) ? d : 0).Where(d => d is >= 1 and <= 7).ToArray();
            var value = new CoopLevelRow(id, diff, raw.Int(row, "Type"), raw.Int(row, "MapId"),
                raw.Int(row, "PreCost"), raw.Int(row, "Vigour"), days, raw.Int(row, "Hide") == 1);
            table._rows[value.RoomMap] = value;
        }
        return table;
    }
}
