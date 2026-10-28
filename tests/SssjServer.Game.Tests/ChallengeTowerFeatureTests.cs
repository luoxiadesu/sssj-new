using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 排行塔（LuaCall 53/54/55/56 + 206 积分领奖 / 207 保留成绩）。台账 `docs/05` §49。
///
/// 断言重点：
/// <list type="number">
///   <item>★★ 期表 **clamp**：本地只排到 2026-06-29，当前周次必须夹在表值域内；</item>
///   <item>★★ 54 的 <c>tbData</c> 是**位置数组**（佣兵 323 是对象 —— 别抄错）；</item>
///   <item>★★★ 55 的 <c>isPass</c> 必须是**布尔**（客户端 <c>== false</c> 显式比较）；</item>
///   <item>★★★ 206 的 <c>res</c> 必须是**布尔 true</c>，且 <c>Reward</c> 必须存在；
///         没得领时回 <c>res=false</c>（否则开空弹窗）；</item>
///   <item>★★ 领取记录存「已领到的 <c>Goal</c> 数值」，不是位掩码。</item>
/// </list>
/// </summary>
public class ChallengeTowerFeatureTests
{
    private static ChallengeTable Table() => ChallengeTable.Load(TestPaths.SettingDir);

    private static JsonObject? Reply(FeatureHarness h, int cmd)
    {
        for (int i = h.Outbound.Count - 1; i >= 0; i--)
        {
            var (fn, json) = h.Outbound[i];
            if (fn != "LuaCall") continue;
            var root = JsonNode.Parse(json);
            if (root?["sCmd"]?.GetValue<int>() == cmd)
                return root["tbParam"] as JsonObject;
        }
        return null;
    }

    private static int Task(FeatureHarness h, int off)
        => h.Player.GetTask(ChallengeTable.Group, off);

    private static void SetTask(FeatureHarness h, int off, int v)
        => h.Player.SetTask(ChallengeTable.Group, off, v);

    // ================================================================ 配置探针

    /// <summary>★ 三张表都能载入（表头解错 ⇒ 静默读空 的回归闸）。</summary>
    [Fact]
    public void 排行塔三张表能载入()
    {
        var t = Table();
        Assert.True(t.WeekCount > 300, $"期表只解出 {t.WeekCount} 期，现网应有 349 期");
        Assert.True(t.Rewards.Count > 20, $"奖励表只解出 {t.Rewards.Count} 档，现网应有 34 档");
        Assert.True(t.FirstBeginTime > 0, "首期开始时间没算出来");
        Assert.Equal(3, t.AttackTimes);
    }

    /// <summary>
    /// ★★★ 期表公式的独立印证：表尾 <c>WeekIndex=349</c> 的备注列自报「2026年6月23日」，
    /// 公式算出的开始时间应与之吻合。
    /// </summary>
    [Fact]
    public void 期表公式与表尾备注吻合()
    {
        var t = Table();
        long begin = t.BeginTimeOf(349);
        var d = DateTimeOffset.FromUnixTimeSeconds(begin).UtcDateTime;
        Assert.Equal(2026, d.Year);
        Assert.Equal(6, d.Month);
        Assert.Equal(23, d.Day);
    }

    /// <summary>
    /// ★★ 期表已用尽时当前周次要 **clamp 到表尾**，不能返回越界值 ——
    /// 否则 <c>WeekOf()</c> 查不到、界面数据全空。
    /// </summary>
    [Fact]
    public void 当前周次被clamp到表值域内()
    {
        var t = Table();
        // 用一个远超表尾的时间点（2030 年）
        long far = new DateTimeOffset(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

        int week = t.CurrentWeekIndex(far);

        Assert.InRange(week, 1, t.WeekCount);
        Assert.NotNull(t.WeekOf(week));      // ★ clamp 后必须能查到配置
    }

    /// <summary>★ 活动开始前返回 0（不能返回负数或 1）。</summary>
    [Fact]
    public void 活动开始前当前周次为0()
    {
        var t = Table();
        long before = t.FirstBeginTime - 3600;
        Assert.Equal(0, t.CurrentWeekIndex(before));
    }

    /// <summary>
    /// ★★ 可领档位判定：<c>Goal ≤ 总分</c> 且 <c>Goal &gt; 已领档位</c>
    /// （逐字对应 <c>UI_TowerScoreInfo.lua:137..150</c>）。
    /// </summary>
    [Fact]
    public void 可领档位按总分与已领档位判定()
    {
        var t = Table();
        var first = t.Rewards[0];

        // 分数不够 ⇒ 一档都不能领
        Assert.Empty(t.ClaimableRewards(first.Goal - 1, 0));
        // 刚好够第一档
        Assert.Single(t.ClaimableRewards(first.Goal, 0));
        // 已领过第一档 ⇒ 不再重复
        Assert.Empty(t.ClaimableRewards(first.Goal, first.Goal));
    }

    // ================================================================ 53 / 54 / 55 / 56

    /// <summary>★ 53 回 <c>{ActivityType = 6}</c>（客户端据此分流刷界面）。</summary>
    [Fact]
    public async Task 取塔信息回ActivityType6()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-gettower");

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdGetTower, "{}");

        var r = Reply(h, ChallengeTowerFeature.CmdGetTower);
        Assert.NotNull(r);
        Assert.Equal(ChallengeTowerFeature.ActivityTypeChallenge, r!["ActivityType"]!.GetValue<int>());
    }

    /// <summary>
    /// ★★ 54 的 <c>tbData</c> 是**位置数组** <c>[type, 塔Id, NodeId, LevelId]</c>
    /// （客户端 <c>tbParam.tbData[1..4]</c>）。★ 佣兵 323 那边是**对象** —— 两种形状别抄错。
    /// </summary>
    [Fact]
    public async Task 进关回位置数组形式的tbData()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-enter");

        // 上行位置数组：[type, 塔Id, NodeId, LevelId, 编队Id]
        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdEnterTower, "[6,349,907,2153,1]");

        var r = Reply(h, ChallengeTowerFeature.CmdEnterTower);
        Assert.NotNull(r);
        var arr = Assert.IsType<JsonArray>(r!["tbData"]);
        Assert.Equal(4, arr.Count);
        Assert.Equal(6, arr[0]!.GetValue<int>());
        Assert.Equal(349, arr[1]!.GetValue<int>());
        Assert.Equal(907, arr[2]!.GetValue<int>());
        Assert.Equal(2153, arr[3]!.GetValue<int>());
    }

    /// <summary>★ 非排行塔类型（其它已过期塔玩法）要忽略、不回包。</summary>
    [Fact]
    public async Task 非排行塔类型被忽略()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-othertype");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdEnterTower, "[2,1,0,1,1]");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>
    /// ★★★ 55 的 <c>isPass</c> 必须是**布尔** ——
    /// 客户端 <c>UI_MissionSuccess.lua:240/264</c> 是 <c>if isPass == false then</c>，
    /// 给 0/1 或省略都会走错分支。
    /// </summary>
    [Fact]
    public async Task 通关回包的isPass是布尔()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-pass");

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdEnterTower, "[6,349,907,2153,1]");
        await ChallengeTestHarness.SettleAsync(h,ChallengeTowerFeature.CmdPassTower, "[6,349,907,2153,1,{\"isPass\":true}]");

        var r = Reply(h, ChallengeTowerFeature.CmdPassTower);
        Assert.NotNull(r);
        // ★ 必须能以 bool 取出（不是 0/1）
        Assert.True(r!["isPass"]!.GetValue<bool>());
        Assert.IsType<JsonArray>(r["tbData"]);
    }

    /// <summary>★ 56 按原厂回空包（排行塔链路本不该走这条）。</summary>
    [Fact]
    public async Task 失败塔回空包()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-fail");

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdFailTower, "{}");

        Assert.NotNull(Reply(h, ChallengeTowerFeature.CmdFailTower));
    }

    // ================================================================ 206 积分领奖

    /// <summary>
    /// ★★★ 有可领档位时回 <c>{res = true, Reward = [...]}</c>，
    /// 且把「已领到的 Goal」写进任务值 45906。
    /// </summary>
    [Fact]
    public async Task 领积分奖励回res为true并记已领档位()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-award");
        var t = Table();
        var first = t.Rewards[0];
        SetTask(h, ChallengeTable.TaskTotalScore, first.Goal);

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdAwardSome, "{}");

        var r = Reply(h, ChallengeTowerFeature.CmdAwardSome);
        Assert.NotNull(r);
        Assert.True(r!["res"]!.GetValue<bool>());          // ★ 布尔 true
        Assert.IsType<JsonArray>(r["Reward"]);             // ★ 必须存在（pairs 对 nil 抛）
        Assert.Equal(first.Goal, Task(h, ChallengeTable.TaskRewardLevel));
    }

    /// <summary>
    /// ★★★ 没得领时必须回 <c>res = false</c> ——
    /// 回 true + 空 Reward 会让客户端开一个空奖励弹窗。
    /// </summary>
    [Fact]
    public async Task 无可领档位时回res为false()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-award-none");
        SetTask(h, ChallengeTable.TaskTotalScore, 0);

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdAwardSome, "{}");

        var r = Reply(h, ChallengeTowerFeature.CmdAwardSome);
        Assert.NotNull(r);
        Assert.False(r!["res"]!.GetValue<bool>());
        Assert.Equal(0, Task(h, ChallengeTable.TaskRewardLevel));
    }

    /// <summary>★★ 重复领取要被拒（已领档位起了作用）。</summary>
    [Fact]
    public async Task 重复领取被拒绝()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-award-twice");
        var first = Table().Rewards[0];
        SetTask(h, ChallengeTable.TaskTotalScore, first.Goal);

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdAwardSome, "{}");
        Assert.True(Reply(h, ChallengeTowerFeature.CmdAwardSome)!["res"]!.GetValue<bool>());

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdAwardSome, "{}");
        Assert.False(Reply(h, ChallengeTowerFeature.CmdAwardSome)!["res"]!.GetValue<bool>());
    }

    /// <summary>★ 高分时可一次领多档（一键领取的语义）。</summary>
    [Fact]
    public async Task 高分时可一次领多档()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-award-many");
        var t = Table();
        // 给足够覆盖前 3 档的分数
        int goal3 = t.Rewards.Take(3).Max(r => r.Goal);
        SetTask(h, ChallengeTable.TaskTotalScore, goal3);

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdAwardSome, "{}");

        Assert.Equal(goal3, Task(h, ChallengeTable.TaskRewardLevel));
    }

    // ================================================================ 207 保留成绩

    /// <summary>★ 207 上行是**裸布尔**，保留时推任务值。</summary>
    [Fact]
    public async Task 无场次令牌的保留请求不能修改积分()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-save");
        SetTask(h, ChallengeTable.TaskTotalScore, 5000);

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdExitSave, "true");

        Assert.NotNull(Reply(h, ChallengeTowerFeature.CmdExitSave));
        Assert.NotNull(Reply(h, ChallengeTowerFeature.CmdExitSave)!["nError"]);
        Assert.Equal(5000, h.Player.GetTask(9, ChallengeTable.TaskTotalScore));
    }

    /// <summary>★ 放弃（false）也要回包（否则退出流程的回调不执行、界面卡住）。</summary>
    [Fact]
    public async Task 放弃成绩也要回包()
    {
        using var h = await ChallengeTestHarness.CreateAsync("ct-save-no");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(ChallengeTowerFeature.CmdExitSave, "false");

        Assert.Equal(before + 1, h.Outbound.Count);
    }

    /// <summary>★ <c>ReadBool</c> 对各种形状的容忍度。</summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    public void ReadBool解析各种形状(string json, bool expected)
        => Assert.Equal(expected, ChallengeTowerFeature.ReadBool(JsonNode.Parse(json)));

    /// <summary>
    /// ★★ <c>ReadPositional</c> 必须**保持下标不错位** ——
    /// 55 的上行第 7 位是 <c>nil</c>，跳过它会让后面的击杀信息/助战 id 全部前移。
    /// </summary>
    [Fact]
    public void ReadPositional对nil占位不错位()
    {
        var args = ChallengeTowerFeature.ReadPositional(
            JsonNode.Parse("[6,349,907,1,1,{},null,[],12345]"));

        Assert.Equal(9, args.Count);
        Assert.Equal(6, args[0]);
        Assert.Equal(1, args[3]);
    }

    /// <summary>★ 六条命令对非法参数都不崩。</summary>
    [Theory]
    [InlineData(ChallengeTowerFeature.CmdGetTower, "[]")]
    [InlineData(ChallengeTowerFeature.CmdEnterTower, "{}")]
    [InlineData(ChallengeTowerFeature.CmdEnterTower, "null")]
    [InlineData(ChallengeTowerFeature.CmdPassTower, "{}")]
    [InlineData(ChallengeTowerFeature.CmdFailTower, "[]")]
    [InlineData(ChallengeTowerFeature.CmdAwardSome, "[]")]
    [InlineData(ChallengeTowerFeature.CmdExitSave, "[]")]
    public async Task 排行塔命令非法参数不崩(int cmd, string json)
    {
        using var h = await ChallengeTestHarness.CreateAsync($"ct-bad-{cmd}-{json.GetHashCode()}");

        await h.DispatchLuaCallAsync(cmd, json);
        Assert.True(true);      // 不抛即通过
    }
}
