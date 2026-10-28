using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 特训位置一行（`NCafe/TrainPos.txt`，现网 24 行）。
///
/// 列（表头第 3 行的英文名）：<c>OutdoorID | Pos | Name | Type</c> ——
/// 「哪个外景的哪个位置，对应哪种特训」。例：外景 `72` 的 `Pos=11` 是 `Type=1`。
///
/// ★ 客户端另有一条算式 <c>nType = math.floor(nPos / 10)</c>（`GirlLogic.lua:107`），
/// 与本表的 `Type` 列在现网数据上一致（`Pos=11` → `11/10=1` = `Type`），
/// 但**以表为准** —— 万一将来有 `Pos=25` 这种就只有表说得清。
/// </summary>
public sealed record TrainPosRow(int OutdoorId, int Pos, int Type);

/// <summary>
/// 一种特训（`NCafe/TrainingGirl.txt`，现网 40 行）。
///
/// ★ 该表**表头是 GBK 被按 UTF-8 导入的乱码**，列名只能看第 3 行的英文行：
/// <c>OutdoorID | Explain | Id | ClothingId | Time | Award1Icon | Award1 | Award2 |
/// MaxNum | Name | SpecialAward | VigorCost | PlayerMakerStartName | PlayerMakerName |
/// TrainItemID | TrainModel | TrainModelSpecial | VisitPos</c>
///
/// 现网样例（外景 72 / Id=1「跳舞」）：
/// <c>Time=3600</c> 秒、<c>Award1=60</c>（好感度）、
/// <c>Award2=15-1-1-1-1000</c>（GDPLN 五元组 ⇒ 1000 个 `15-1-1-1`）、
/// <c>MaxNum=3</c>（特训栏位上限）、<c>VigorCost=0</c>。
/// </summary>
/// <param name="OutdoorId">所属外景。</param>
/// <param name="Id">特训类型（与 <see cref="TrainPosRow.Type"/> 对应）。</param>
/// <param name="Time">特训时长（秒）。</param>
/// <param name="LoveAward">好感度奖励（<c>Award1</c>）。</param>
/// <param name="PlayerAwards">玩家奖励（<c>Award2</c>，已解析成 GDPLN 五元组数组）。</param>
/// <param name="MaxNum">特训栏位上限（<c>MaxNum</c>）。</param>
/// <param name="VigorCost">体力消耗。</param>
public sealed record TrainingRow(int OutdoorId, int Id, int Time, int LoveAward,
                                IReadOnlyList<int[]> PlayerAwards, int MaxNum, int VigorCost);

/// <summary>
/// 少女特训的两张配置表。取证与用法见 <see cref="Handlers.GirlTrainFeature"/> 与 `docs/05` §17.4。
/// 缺表一律降级成空表 + WARN，绝不抛（与其它配置表一致）。
/// </summary>
public sealed class TrainTable
{
    /// <summary>空表（缺文件时的降级值，也是单测基线）。</summary>
    public static TrainTable Empty { get; } = new();

    /// <summary>特训位置表文件名。</summary>
    public const string PosFileName = "TrainPos.txt";

    /// <summary>特训内容表文件名。</summary>
    public const string TrainingFileName = "TrainingGirl.txt";

    /// <summary>表里没给时长时的兜底秒数（1 小时，与现网外景 72 的 `Time=3600` 一致）。</summary>
    public const int FallbackSeconds = 3600;

    private readonly Dictionary<int, TrainPosRow> _pos = new();
    private readonly List<TrainingRow> _training = new();

    /// <summary>按 <c>Pos</c> 索引的位置表。</summary>
    public IReadOnlyDictionary<int, TrainPosRow> Positions => _pos;

    /// <summary>全部特训内容行。</summary>
    public IReadOnlyList<TrainingRow> Trainings => _training;

    /// <summary>查某个位置。</summary>
    public TrainPosRow? PosOf(int pos) => _pos.TryGetValue(pos, out var v) ? v : null;

    /// <summary>
    /// 严格按「外景 + 类型」读取；不同外景的奖励并不相同。
    /// </summary>
    public TrainingRow? TrainingFor(int outdoorId, int type)
        => _training.FirstOrDefault(t => t.OutdoorId == outdoorId && t.Id == type);

    public static TrainTable Load(string settingDir)
    {
        var table = new TrainTable();
        table.LoadPos(Path.Combine(settingDir, PosFileName));
        table.LoadTraining(Path.Combine(settingDir, TrainingFileName));

        Log.Info($"[setting] 少女特训配置：位置 {table._pos.Count} 个、特训内容 {table._training.Count} 条" +
                 (table._training.Count > 0
                     ? $"（样例：外景 {table._training[0].OutdoorId} 类型 {table._training[0].Id} " +
                       $"{table._training[0].Time}s 好感+{table._training[0].LoveAward}）"
                     : ""));
        return table;
    }

    private void LoadPos(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"[setting] 找不到 {PosFileName}，特训位置表降级为空（StartTrain 会一律拒绝）");
            return;
        }

        foreach (var cells in ReadRows(path))
        {
            if (cells.Length < 4)
                continue;
            if (!int.TryParse(cells[0], out int outId) || !int.TryParse(cells[1], out int pos) || pos <= 0)
                continue;
            int.TryParse(cells[3], out int type);
            // GirlConfig.LoadTrainPos intentionally shares Pos -> Type across outdoors.
            // OutdoorId here is provenance only; rewards use the equipped outdoor.
            _pos.TryAdd(pos, new TrainPosRow(outId, pos, type));
        }
    }

    private void LoadTraining(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"[setting] 找不到 {TrainingFileName}，特训内容表降级为空（开始特训会拒绝）");
            return;
        }

        foreach (var cells in ReadRows(path))
        {
            if (cells.Length < 12)
                continue;
            if (!int.TryParse(cells[0], out int outId))
                continue;
            if (!int.TryParse(cells[2], out int id) || id <= 0)
                continue;

            int.TryParse(cells[4], out int time);
            int.TryParse(cells[6], out int love);
            int.TryParse(cells[8], out int maxNum);
            int.TryParse(cells[11], out int vigor);

            _training.Add(new TrainingRow(outId, id, time, love,
                                          ParseGdpln(cells[7]), maxNum, vigor));
        }
    }

    /// <summary>
    /// 解析 `Award2` 的 GDPLN 五元组。现网形如 <c>15-1-1-1-1000</c>
    /// （genre-detail-particular-level-count），可能有多组用 `|` 或 `,` 分隔。
    /// </summary>
    internal static IReadOnlyList<int[]> ParseGdpln(string raw)
    {
        var result = new List<int[]>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var group in raw.Split(['|', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = group.Trim().Trim('[', ']').Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
                continue;
            var tuple = new int[5];
            bool ok = true;
            for (int i = 0; i < 5; i++)
            {
                if (!int.TryParse(parts[i].Trim(), out tuple[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (ok && tuple[4] > 0)
                result.Add(tuple);
        }
        return result;
    }

    /// <summary>读制表符分隔表：跳过 3 行表头，跳过纯空行。</summary>
    private static IEnumerable<string[]> ReadRows(string path)
    {
        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (++n <= 3)
                continue;
            if (string.IsNullOrWhiteSpace(line.Replace("\t", "")))
                continue;
            yield return line.Split('\t');
        }
    }
}
