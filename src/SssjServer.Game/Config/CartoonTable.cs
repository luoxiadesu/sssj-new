using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>Original path/id mapping recovered from CartoonData.tbFile2Id.</summary>
public sealed class CartoonTable
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    public static CartoonTable Load(string settingDir)
    {
        var result = new CartoonTable();
        var table = SettingTable.Load(Path.Combine(settingDir, "CartoonFileId.txt"), headerRow: 0);
        if (table is null) return result;
        foreach (var row in table.Rows)
        {
            string path = table.Str(row, "File");
            if (path.Length > 0 && int.TryParse(table.Str(row, "Id"), out int id) && id > 0)
                result._ids[path] = id;
        }
        return result;
    }

    public bool IsRead(Player player, string path)
    {
        if (!path.EndsWith(".txt", StringComparison.Ordinal)) path += ".txt";
        if (!_ids.TryGetValue(path, out int id)) return false;
        var (taskId, bit) = TaskBit(id);
        return taskId <= 400 && (player.GetTask(39, taskId) & (1 << bit)) != 0;
    }

    // Match CartoonData.__GetTaskId, including its special storage for bit 31.
    internal static (int TaskId, int Bit) TaskBit(int id)
    {
        int taskId = id / 32, bit = id % 32;
        if (bit == 31)
        {
            taskId += 201;
            bit = taskId % 30;
        }
        return (taskId, bit);
    }
}
