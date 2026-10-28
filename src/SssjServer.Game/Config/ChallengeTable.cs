using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 排行塔（Challenge Tower）的一期（<c>ChallengeTower.txt</c> 的一行）。
/// </summary>
/// <param name="Id">行 id（<c>ID</c> 列，与 <see cref="WeekIndex"/> 现网一致）。</param>
/// <param name="WeekIndex">第几周（1..349）。</param>
/// <param name="NodeIds">三个 boss 节点 id（<c>NodeID1/2/3</c>）。</param>
public sealed record ChallengeWeekRow(int Id, int WeekIndex, IReadOnlyList<int> NodeIds);

/// <summary>
/// 排行塔的积分奖励一档（<c>ChallengePointReward.txt</c> 的一行）。
/// </summary>
/// <param name="RewardId">奖励 id。</param>
/// <param name="Goal">
/// 目标积分。★★ 客户端把「**已领到的 Goal 数值**」存进任务值
/// （<c>challengeRewardLevel</c>，见 <see cref="ChallengeTable.TaskRewardLevel"/>）
/// 而不是存位掩码 —— 判据 <c>UI_TowerScoreInfo.lua:137..150</c>：
/// <code>
/// if tbLevelConfig.Goal &lt;= latestGet then GetMark = 0    -- 已领取
/// elseif tbLevelConfig.Goal &gt; newTotal then GetMark = 2   -- 不可领取
/// else GetMark = 1 end                                     -- 可领取
/// </code>
/// ⇒ 服务端领奖后要把这个任务值更新成「本次领到的最大 Goal」。
/// </param>
/// <param name="Rewards">奖励元组（<c>Rewards</c> 列，**短横**分隔的 GDPLN）。</param>
public sealed record ChallengeRewardRow(int RewardId, int Goal, List<int[]> Rewards);

/// <summary>
/// 排行塔配置：期表（按周公式）+ 积分奖励表。
///
/// <para><b>1. ★★★ 为什么它「活着」（第十三轮档期筛查结论）</b></para>
/// 与「小塔」等限时活动不同，排行塔的三层准入**都不看档期**：
/// <list type="number">
///   <item><b>入口按钮</b>：<c>UI_FightMode.lua:27/192..201</c> 只查
///     <c>SystemUnlock:GetUnlock("UI_UnlockChallengeTower")</c>（<c>SystemUnlock.txt:51</c>
///     「排行塔 5-12 解锁」）—— **没有时间判断**；</item>
///   <item><b>取当期</b>：<c>ActivitiesLogic:GetCurTowerConfig</c>（<c>ActivitiesLogic.lua:396..412</c>）
///     的 Challenge 分支只有下界 <c>if nCurTime &lt; BeginTime then break end</c>、
///     **没有上界** ⇒ 现在会取到表尾那一期，非 nil ⇒ 界面正常打开；</item>
///   <item><b>界面</b>：<c>UI_ChallengeTower.lua:103..145</c> 对「过了 EndTime」只
///     隐藏倒计时（<c>leftTime:SetActive(false)</c>）、**不 return**，
///     积分/排名/奖励红点照常渲染。</item>
/// </list>
///
/// <para><b>2. ★★ 期表可能比「现在」旧 —— 必须 clamp，不能直接用表尾期</b></para>
/// 本地 <c>ChallengeTower.txt</c> 只排到 <c>WeekIndex=349</c>（按公式 = 2026-06-23..06-29），
/// 而取证基准日 2026-08-25 已在其后 8 周。
/// ⇒ <see cref="CurrentWeekIndex"/> 按公式算出「真实周次」后**夹到表的值域内**，
/// 与客户端 <c>GetCurTowerConfig</c>「取最后一个 BeginTime ≤ now 的期」结果一致。
/// ★ 这与佣兵那边同型（`docs/05` §31.1 记过「本地表比设备少行」的坑），
/// 但处理方式不同：佣兵是**反解**（表有 891 期、够用），排行塔是**clamp**（表已用尽）。
///
/// <para><b>3. 期表公式（<c>ActivitiesConfig.lua:287..312</c>，与佣兵同构）</b></para>
/// <code>
/// FirstBeginTime = BeginTime(20191022) + WeekBeginTime 的时分(04:00)
/// spanDay        = WeekEndTime[1] - WeekBeginTime[1]（≤0 则 +7）
/// FirstEndTime   = FirstBeginTime + spanDay 天
/// 第 n 期：Begin = FirstBeginTime + (n-1)*7天、End = FirstEndTime + (n-1)*7天
/// </code>
/// 实测 <c>BeginTime=20191022  WeekBeginTime=2,4:00:00  WeekEndTime=1,4:00:00  AttackTimes=3</c>
/// ⇒ <c>spanDay = 1-2 = -1 → +7 = 6</c>，即每期 6 天、周期 7 天。
/// ★ 表尾 <c>WeekIndex=349</c> 的备注列自报「2026年6月23日」，与公式**完全吻合**
/// —— 这是公式正确性的独立印证。
/// </summary>
public sealed class ChallengeTable
{
    /// <summary>基准表（一行）。</summary>
    public const string TotalFileName = "ChallengeTowerTotal.txt";

    /// <summary>期表（349 期）。</summary>
    public const string WeekFileName = "ChallengeTower.txt";

    /// <summary>积分奖励表（34 档）。</summary>
    public const string RewardFileName = "ChallengePointReward.txt";

    /// <summary>一周的秒数。</summary>
    public const long OneWeek = 7L * 24 * 3600;

    /// <summary>
    /// 排行塔的任务值 group = <b>9</b>（<c>ActivitiesLogic.lua:315</c>
    /// <c>tbLogic.nTaskGroup = 9</c>）。
    /// </summary>
    public const int Group = 9;

    /// <summary>
    /// 总最高分的任务值偏移 = <b>45901</b>（<c>ActivitiesLogic.lua:780</c>
    /// <c>challengeTotalScore</c>）。
    /// </summary>
    public const int TaskTotalScore = 45901;

    /// <summary>
    /// 「已领到的积分奖励档位（Goal 数值）」的偏移 = <b>45906</b>
    /// （<c>ActivitiesLogic.lua:784</c> <c>challengeRewardLevel</c>）。
    /// ★ 存的是 **Goal 数值**不是位掩码，见 <see cref="ChallengeRewardRow.Goal"/>。
    /// </summary>
    public const int TaskRewardLevel = 45906;

    /// <summary>关卡进度偏移基址 = <b>45000</b>（<c>ActivitiesLogic.lua:790</c>）。</summary>
    public const int TaskLevelProcessBase = 45000;

    /// <summary>关卡最高分偏移基址 = <b>46000</b>（<c>ActivitiesLogic.lua:791</c>）。</summary>
    public const int TaskLevelScoreBase = 46000;

    private readonly List<ChallengeWeekRow> _weeks = new();
    private readonly List<ChallengeRewardRow> _rewards = new();

    /// <summary>空表（配置缺失时的降级实例）。</summary>
    public static ChallengeTable Empty { get; } = new();

    /// <summary>首期开始时间（Unix 秒）。</summary>
    public long FirstBeginTime { get; private set; }

    /// <summary>首期结束时间（Unix 秒）。</summary>
    public long FirstEndTime { get; private set; }

    /// <summary>每期可挑战次数（<c>AttackTimes</c>）。</summary>
    public int AttackTimes { get; private set; } = 3;

    /// <summary>期数（本地快照）。</summary>
    public int WeekCount => _weeks.Count;

    /// <summary>积分奖励档位（按 <c>Goal</c> 升序）。</summary>
    public IReadOnlyList<ChallengeRewardRow> Rewards => _rewards;

    /// <summary>取某一期；不存在返回 null。</summary>
    public ChallengeWeekRow? WeekOf(int weekIndex)
        => _weeks.FirstOrDefault(w => w.WeekIndex == weekIndex);

    /// <summary>
    /// 按公式算「现在是第几期」，并 **clamp 到表的值域**（见类注释 §2）。
    /// 返回 0 表示配置缺失/尚未开始。
    /// </summary>
    public int CurrentWeekIndex(long nowUnix)
    {
        if (FirstBeginTime <= 0 || _weeks.Count == 0)
            return 0;
        if (nowUnix < FirstBeginTime)
            return 0;

        long elapsed = nowUnix - FirstBeginTime;
        int raw = (int)(elapsed / OneWeek) + 1;

        // ★ clamp：表已用尽时退化成表尾期（与客户端「取最后一个 BeginTime ≤ now」一致）
        int maxIndex = _weeks.Max(w => w.WeekIndex);
        return Math.Clamp(raw, 1, maxIndex);
    }

    /// <summary>某一期的开始时间（Unix 秒）。</summary>
    public long BeginTimeOf(int weekIndex)
        => FirstBeginTime <= 0 ? 0 : FirstBeginTime + (weekIndex - 1) * OneWeek;

    /// <summary>某一期的结束时间（Unix 秒）。</summary>
    public long EndTimeOf(int weekIndex)
        => FirstEndTime <= 0 ? 0 : FirstEndTime + (weekIndex - 1) * OneWeek;

    /// <summary>
    /// Actual calendar cycle, separate from the last available content row.
    /// Beyond the archive, replay its final content while retaining weekly isolation.
    /// The regional schedule is UTC+8; the old FirstBeginTime value used UTC date
    /// parsing, so normalize that historical value here without rewriting content IDs.
    /// </summary>
    internal ChallengeCycle CycleAt(long now)
    {
        long first = FirstBeginTime - 8 * 3600L;
        if (FirstBeginTime <= 0 || _weeks.Count == 0 || now < first) return new(0, 0, 0, 0, 0, false);
        int cycle = checked((int)((now - first) / OneWeek + 1));
        long begin = first + (cycle - 1L) * OneWeek;
        long end = begin + FirstEndTime - FirstBeginTime;
        int content = Math.Clamp(cycle, _weeks.Min(w => w.WeekIndex), _weeks.Max(w => w.WeekIndex));
        return new(cycle, content, begin, end, begin + OneWeek, cycle > _weeks.Max(w => w.WeekIndex));
    }

    /// <summary>
    /// 按「总积分」算出可领取的奖励档位（<c>Goal ≤ 总分</c> 且 <c>Goal &gt; 已领档位</c>）。
    /// 逐字对应 <c>UI_TowerScoreInfo.lua:137..150</c> 的 <c>GetMark == 1</c> 判定。
    /// </summary>
    public List<ChallengeRewardRow> ClaimableRewards(int totalScore, int claimedGoal)
        => _rewards.Where(r => r.Goal <= totalScore && r.Goal > claimedGoal)
                   .OrderBy(r => r.Goal)
                   .ToList();

    /// <summary>载入三张表。缺表不抛，只降级。</summary>
    public static ChallengeTable Load(string settingDir)
    {
        var t = new ChallengeTable();

        // ---- ChallengeTowerTotal.txt（基准，表头 3 行）----
        var total = SettingTable.LoadAuto(Path.Combine(settingDir, TotalFileName), "BeginTime");
        var row0 = total?.Rows.FirstOrDefault();
        if (total is not null && row0 is not null)
        {
            long baseDay = ParseDate(total.Str(row0, "BeginTime"));
            var wb = total.Str(row0, "WeekBeginTime").Split(',', StringSplitOptions.TrimEntries);
            var we = total.Str(row0, "WeekEndTime").Split(',', StringSplitOptions.TrimEntries);
            t.AttackTimes = Math.Max(1, total.Int(row0, "AttackTimes", 3));

            // WeekBeginTime 形如 "2,4:00:00" ⇒ [0]=星期、[1]=时分秒
            int hour = 0, minute = 0;
            if (wb.Length >= 2)
            {
                var hms = wb[1].Split(':', StringSplitOptions.TrimEntries);
                if (hms.Length >= 1) int.TryParse(hms[0], out hour);
                if (hms.Length >= 2) int.TryParse(hms[1], out minute);
            }
            t.FirstBeginTime = baseDay + hour * 3600L + minute * 60L;

            // spanDay = WeekEndTime 星期 - WeekBeginTime 星期，≤0 则 +7
            int wbDay = wb.Length >= 1 && int.TryParse(wb[0], out int a) ? a : 0;
            int weDay = we.Length >= 1 && int.TryParse(we[0], out int b) ? b : 0;
            int spanDay = weDay - wbDay;
            if (spanDay <= 0)
                spanDay += 7;
            t.FirstEndTime = t.FirstBeginTime + spanDay * 24L * 3600;
        }

        // ---- ChallengeTower.txt（期表，表头 3 行）----
        var weeks = SettingTable.LoadAuto(Path.Combine(settingDir, WeekFileName), "WeekIndex");
        if (weeks is not null)
        {
            foreach (var row in weeks.Rows)
            {
                int id = weeks.Int(row, "ID", 0);
                int idx = weeks.Int(row, "WeekIndex", 0);
                if (idx <= 0)
                    continue;
                var nodes = new List<int>();
                for (int n = 1; n <= 3; n++)
                {
                    int node = weeks.Int(row, $"NodeID{n}", 0);
                    if (node > 0)
                        nodes.Add(node);
                }
                t._weeks.Add(new ChallengeWeekRow(id, idx, nodes));
            }
        }

        // ---- ChallengePointReward.txt（积分奖励，表头 3 行）----
        var rewards = SettingTable.LoadAuto(Path.Combine(settingDir, RewardFileName), "RewardId");
        if (rewards is not null)
        {
            foreach (var row in rewards.Rows)
            {
                int id = rewards.Int(row, "RewardId", 0);
                int goal = rewards.Int(row, "Goal", 0);
                if (id <= 0 || goal <= 0)
                    continue;
                t._rewards.Add(new ChallengeRewardRow(
                    RewardId: id,
                    Goal: goal,
                    // ★ Rewards 是短横分隔的 GDPLN（样例 [7-1-4-2-1]）
                    Rewards: MercenaryTable.ParseRewards(rewards.Str(row, "Rewards"))));
            }
            t._rewards.Sort((x, y) => x.Goal.CompareTo(y.Goal));
        }

        int cur = t.CurrentWeekIndex(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Log.Info($"[setting] 排行塔配置：首期 {Fmt(t.FirstBeginTime)}..{Fmt(t.FirstEndTime)}、"
                 + $"每期 {t.AttackTimes} 次、本地 {t._weeks.Count} 期、奖励 {t._rewards.Count} 档、"
                 + $"当前第 {cur} 期（★ 表已用尽时为 clamp 后的表尾期）");
        return t;
    }

    private static string Fmt(long unix)
        => unix <= 0 ? "-" : DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd HH:mm");

    /// <summary>解析 <c>20191022</c> 形式的日期（UTC 零点）。</summary>
    private static long ParseDate(string text)
    {
        text = text.Trim();
        if (text.Length < 8
            || !int.TryParse(text[..4], out int y)
            || !int.TryParse(text[4..6], out int m)
            || !int.TryParse(text[6..8], out int d))
            return 0;
        try
        {
            return new DateTimeOffset(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }
        catch
        {
            return 0;
        }
    }
}

internal sealed record ChallengeCycle(int Cycle, int Content, long Begin, long End, long Clear, bool Replay)
{
    internal bool IsOpen(long now) => Cycle > 0 && now >= Begin && now < End;
}
