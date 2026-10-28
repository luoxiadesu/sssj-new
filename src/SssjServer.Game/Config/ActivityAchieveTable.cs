using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 活动成就（Template=2）的「活动 → 成就 id 列表」映射表。
/// 取证见 `work/activity-award-forensics.md` §4.2、台账 `docs/05` §29。
///
/// <para><b>0. 只有一张表，其余全部复用已有的</b></para>
/// <c>NormalActivity/ActivityTemplate2.txt</c>（表头 **3** 行，
/// `NormalActivityConfig.lua:125` <c>LoadTabFile("NormalActivity/ActivityTemplate2.txt", 3)</c>）。
/// 成就本体在 <c>AchievementOther.txt</c>（group **18**），
/// 而 <see cref="MissionTable"/> **早就装载好了**（`GetAchieveOther`）⇒ 本表只提供「哪个活动有哪些成就」。
///
/// <para><b>1. ★ <c>AchievementList</c> 是花括号包的 Lua 数组</b></para>
/// 实测形如 <c>{4,5,6,7,8,9}</c>（`NormalActivityConfig.lua:135` 用 <c>Eval()</c> 直接当 Lua 求值）
/// —— 与本项目其它表的 <c>[...]</c> 中括号风格**不同**，所以单独写解析
/// （`ChapterTable.ParseTuples` 那套按 <c>[</c> 找，对花括号一条都读不出来）。
///
/// <para><b>2. 现网哪些活动是 Template=2 且开放</b></para>
/// 取证 §4.2 + §5：**2 / 3 / 14 / 15 / 16 / 108** 六个（表里另有 4/5/17/18/25 但已过期）。
/// 那 74 个成就 id 在 <c>AchievementOther.txt</c> 里**一个都不缺**（取证脚本核对过）。
/// ★ 本实现**不按活动 id 白名单过滤** —— 领奖闸门只关心「这个成就属不属于这个活动」，
/// 过期活动客户端压根不显示、玩家点不到；硬编码白名单反而会在改档期时失效。
/// </summary>
public sealed class ActivityAchieveTable
{
    /// <summary>活动成就所在的任务值 group（<c>MissionManager.Other_Group = 18</c>）。</summary>
    public const int AchieveGroup = MissionTable.OtherGroup;

    private readonly Dictionary<int, List<int>> _byActivity = new();

    private ActivityAchieveTable() { }

    /// <summary>空表（配置缺失时降级：任何活动都查不到成就 ⇒ 领奖一律拒绝）。</summary>
    public static ActivityAchieveTable Empty { get; } = new();

    /// <summary>载入 <c>ActivityTemplate2.txt</c>；缺表只降级、不抛。</summary>
    public static ActivityAchieveTable Load(string settingDir)
    {
        var t = new ActivityAchieveTable();
        var table = SettingTable.LoadAuto(Path.Combine(settingDir, "ActivityTemplate2.txt"), "AchievementList");
        if (table is null)
            return t;

        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "Id", -1);
            if (id <= 0)
                continue;
            var list = ParseBraceList(table.Str(row, "AchievementList"));
            if (list.Count > 0)
                t._byActivity[id] = list;
        }

        // Restored tower 21 uses Template5, with permanent and daily achievement lists.
        var towerTasks = SettingTable.Load(Path.Combine(settingDir, "ActivityTemplate5.txt"));
        if (towerTasks is not null)
            foreach (var row in towerTasks.Rows)
                if (towerTasks.Int(row, "Id") == 262)
                    t._byActivity[262] = ParseBraceList(towerTasks.Str(row, "AchievementList"))
                        .Concat(ParseBraceList(towerTasks.Str(row, "DailyAchievementList"))).Distinct().ToList();

        Log.Info($"[setting] 活动成就配置：{t._byActivity.Count} 个活动映射（含重开262）、" +
                 $"共 {t._byActivity.Values.Sum(l => l.Count)} 条成就" +
                 $"（现网开放的是 2/3/14/15/16/108）");
        return t;
    }

    /// <summary>该活动的成就 id 列表；未登记活动返回空。</summary>
    public IReadOnlyList<int> AchievesOf(int activityId)
        => _byActivity.TryGetValue(activityId, out var l) ? l : Array.Empty<int>();

    /// <summary>该成就是否属于该活动（领奖闸门的第一道校验）。</summary>
    public bool Contains(int activityId, int achieveId)
        => _byActivity.TryGetValue(activityId, out var l) && l.Contains(achieveId);

    /// <summary>已载入的活动数（日志/测试用）。</summary>
    public int ActivityCount => _byActivity.Count;

    /// <summary>
    /// 解析 <c>{4,5,6}</c> 这种**花括号**包的整数数组。
    /// ★ 与 <c>[...]</c> 风格分开写的理由见类注释 §1。
    /// </summary>
    internal static List<int> ParseBraceList(string text)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var cleaned = text.Replace("{", "").Replace("}", "")
                          .Replace("[", "").Replace("]", "")
                          .Replace("\"", "").Replace(" ", "");
        foreach (var seg in cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(seg, out int v) && v > 0)
                result.Add(v);
        }
        return result;
    }
}
