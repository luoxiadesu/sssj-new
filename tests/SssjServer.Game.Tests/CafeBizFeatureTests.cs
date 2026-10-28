using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Core;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 咖啡馆经营四命令（LuaCall 113 排班 / 114 座位 / 115 顾客 / 119 咖啡）。
///
/// 这四条是咖啡馆场景打通后客户端才开始发的（`docs/05` §16.3），
/// 它们共同填满 112 回包的六组清单 —— 所以断言重点有两类：
/// ① 回包形状（每条都有自己的怪癖，见各测试的注释）；
/// ② **数据要能回流到 112**（客户端算 → 上报 → 存档 → 下次进店发回，§15.3 的闭环）。
/// </summary>
public class CafeBizFeatureTests
{
    private static JsonNode Payload(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json))["tbParam"]!;
    }

    // Production/serving tests start with the requested recipe already learned.
    // Keep this setup local to production calls so learning tests retain a fresh player.
    private static Task MakeLearnedCoffeeAsync(FeatureHarness h, string payload)
    {
        var body = JsonNode.Parse(payload) as JsonObject;
        int type = body?["coffeetype"] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : 0;
        if (type > 0 && type < CafeBizFeature.MaxCoffeeId)
            h.Player.SetTask(CafeBizFeature.CoffeeTaskGroup, type,
                h.Player.GetTask(CafeBizFeature.CoffeeTaskGroup, type) | (1 << CafeBizFeature.CoffeeLearnedBit));
        return h.DispatchLuaCallAsync(CafeBizFeature.MakeCoffeeCmd, payload);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public async Task LearnCoffee_PlayerLevelBoundary(int level, bool allowed)
    {
        using var h = await FeatureHarness.CreateAsync("cafe-learn-level");
        h.Player.Level = level;
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":5}");
        Assert.Equal(allowed, Learned(h.Player, 5));
        Assert.Equal(allowed, (h.Player.GetTask(23, 5) & 4) != 0);
    }

    [Fact]
    public async Task LearnCoffee_StageRequiresPassCountAndExactDifficulty()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-learn-stage");
        h.Player.Levels[MapList.MapId(4, 12, 1)] = 7; // stars alone are not a pass
        h.Player.RecordLevelPass(MapList.MapId(4, 12, 2), 7);
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":6}");
        Assert.False(Learned(h.Player, 6));
        h.Player.RecordLevelPass(MapList.MapId(4, 12, 1), 7);
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":6}");
        Assert.True(Learned(h.Player, 6));
        Assert.Equal(257, h.Player.GetTask(23, 6));
    }

    [Fact]
    public async Task ChapterFinishImmediatelyPersistsCoffeeConditionWithoutLearningRequest()
    {
        using var h = await FeatureHarness.CreateAsync("coffee-stage-immediate");
        h.Player.SetTask(23, 6, 256 | 2);
        await h.DispatchFunctionAsync("ChapterMsg", new JsonObject { ["nState"] = 0, ["Chapter"] = 4, ["Index"] = 12, ["Difficult"] = 1 }.ToJsonString());
        await h.CompleteChapterAsync(4, 12, 1);
        Assert.Equal(259, h.Player.GetTask(23, 6));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(259, saved.GetTask(23, 6));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    [Fact]
    public async Task GuestSelectionRequiresLearnedRecipeAndPositiveDailyWeight()
    {
        using var h = await FeatureHarness.CreateAsync("guest-eligibility");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var locked = table.Guests.Values.First(g => g.NeedCoffee > 0);
        var basic = table.Guests.Values.First(g => g.NeedCoffee == 0);
        var weights = new List<CafeFeature.CafeGuestWeight>
            { new(basic.Id, 17), new(locked.Id, 123), new(999999, 50), new(basic.Id, -1) };
        Assert.Equal(17, Assert.Single(CafeBizFeature.EligibleGuestWeights(table, h.Player, 30, weights)).Weight);
        h.Player.SetTask(23, locked.NeedCoffee, 7);
        Assert.Single(CafeBizFeature.EligibleGuestWeights(table, h.Player, 30, weights));
        h.Player.SetTask(23, locked.NeedCoffee, 256);
        Assert.Equal(2, CafeBizFeature.EligibleGuestWeights(table, h.Player, 30, weights).Count);
        Assert.Empty(CafeBizFeature.EligibleGuestWeights(table, h.Player, 0, weights));
        Assert.Empty(CafeBizFeature.EligibleGuestWeights(table, h.Player, 30, []));
    }

    [Fact]
    public async Task DailyGuestsExcludeLockedRecipesAndRepairOldSavedList()
    {
        using var h = await FeatureHarness.CreateAsync("daily-guest-filter");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var state = new CafeFeature.CafeState(1, 0, 0);
        int day = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        var locked = table.Guests.Values.First(g => g.NeedCoffee > 0);
        CafeFeature.WriteState(h.Player, state, new CafeFeature.CafeContent
        {
            WeightDay = day,
            Weights = [new CafeFeature.CafeGuestWeight(locked.Id, 123)]
        });
        var result = CafeFeature.ReadContent(h.Player, state, table, day);
        Assert.NotEmpty(result.Weights);
        Assert.All(result.Weights, w => Assert.Equal(0, table.Guests[w.Id].NeedCoffee));
        CafeFeature.WriteState(h.Player, state, result);
        var again = CafeFeature.ReadContent(h.Player, state, table, day);
        Assert.Equal(result.Weights, again.Weights);
        foreach (var coffee in table.Coffees.Keys) h.Player.SetTask(23, coffee, 256);
        var rolled = CafeFeature.RollDailyGuests(table, 30, h.Player.Roleid, day, h.Player);
        Assert.Contains(rolled, w => table.Guests[w.Id].NeedCoffee > 0);
    }

    [Fact]
    public async Task CustomerIdsAreNotReusedAfterQueueAndSeatsEmpty()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-guest-sequence");
        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "[]");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var state = CafeFeature.ReadState(h.Player);
        int day = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        var first = CafeFeature.ReadContent(h.Player, state, table, day).Queue.Single().CustomerIdx;
        CafeFeature.WriteState(h.Player, state, new CafeFeature.CafeContent());
        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "[]");
        int second = CafeFeature.ReadContent(h.Player, state, table, day).Queue.Single().CustomerIdx;
        Assert.True(second > first);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(second, saved.Extra["cafeCustomerSequence"]!.GetValue<int>());
    }

    [Fact]
    public async Task RepeatedDrinkSettlementDoesNotGrantOrAccumulateAgain()
    {
        var (h, idx) = await SeatedAsync("cafe-repeat-settlement");
        using var cleanup = h;
        var payload = $"{{\"customeridx\":{idx}}}";
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, payload);
        int gold = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        int coin = h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId);
        int hot = CafeFeature.ReadState(h.Player).Hot;
        string report = h.Player.Extra["cafereport"]!.ToJsonString();
        int replies = h.Outbound.Count;
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, payload);
        Assert.Equal(replies, h.Outbound.Count);
        Assert.Equal(gold, h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        Assert.Equal(coin, h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId));
        Assert.Equal(hot, CafeFeature.ReadState(h.Player).Hot);
        Assert.Equal(report, h.Player.Extra["cafereport"]!.ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettlementOverflowLeavesWholeTransactionUnchanged(bool hotOverflow)
    {
        var (h, idx) = await SeatedAsync("cafe-settle-overflow");
        using var cleanup = h;
        var state = CafeFeature.ReadState(h.Player);
        if (hotOverflow)
        {
            var table = CafeTable.Load(TestPaths.SettingDir);
            var content = CafeFeature.ReadContent(h.Player, state, table,
                CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
            CafeFeature.WriteState(h.Player, state with { Hot = int.MaxValue }, content);
        }
        else h.Player.AddMoney(CafeBizFeature.GoldMoneyId,
            int.MaxValue - h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        string cafeBefore = h.Player.Extra["cafe"]!.ToJsonString();
        int goldBefore = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        int coinBefore = h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId);
        int replies = h.Outbound.Count;
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{idx}}}");
        Assert.Equal(replies, h.Outbound.Count);
        Assert.Equal(cafeBefore, h.Player.Extra["cafe"]!.ToJsonString());
        Assert.Equal(goldBefore, h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        Assert.Equal(coinBefore, h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId));
        Assert.False(h.Player.Extra.ContainsKey("cafereport"));
    }

    [Fact]
    public async Task CafeDownloadSettlesElapsedSeatExactlyOnce()
    {
        var (h, idx) = await SeatedAsync("cafe-offline-seat");
        using var cleanup = h;
        int before = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "[]");
        int after = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        Assert.True(after > before);
        Assert.Equal(CafeFeature.ReqCafeDataCmd,
            JsonNode.Parse(h.LastOutbound.Json)!["sCmd"]!.GetValue<int>());
        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "[]");
        Assert.Equal(after, h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{idx}}}");
        Assert.Equal(after, h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(after, saved.GetMoney(CafeBizFeature.GoldMoneyId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task DailyEarningsCapClipsPayoutAndStillClearsSeat(int remaining)
    {
        var (h, idx) = await SeatedAsync("cafe-daily-limit");
        using var cleanup = h;
        h.Player.Extra["cafeDailyEarnings"] = new JsonObject
        {
            ["day"] = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow),
            ["gold"] = 400000 - remaining, ["coin"] = 200000
        };
        int before = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        int coin = h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId);
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{idx}}}");
        Assert.Equal(before + remaining, h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        Assert.Equal(coin, h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId));
        Assert.Equal(400000, h.Player.Extra["cafeDailyEarnings"]!["gold"]!.GetValue<long>());
        Assert.Equal(0, Assert.IsType<JsonArray>(Payload(h)["earn_base"])[1]!.GetValue<int>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CoffeeCoinCapacityDoesNotDeleteExistingBalance(int remaining)
    {
        var (h, idx) = await SeatedAsync("cafe-coin-cap");
        using var cleanup = h;
        int max = CafeTable.Load(TestPaths.SettingDir).LevelRowFor(5)!.CoffeeCoinMax;
        h.Player.AddMoney(CafeBizFeature.CoffeeCoinMoneyId,
            max - remaining - h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId));
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{idx}}}");
        Assert.Equal(max, h.Player.GetMoney(CafeBizFeature.CoffeeCoinMoneyId));
        Assert.Equal(remaining, Assert.IsType<JsonArray>(Payload(h)["earn_base"])[1]!.GetValue<int>());
    }

    // ================= 111 咖啡馆升级 =================

    /// <summary>造一个「钱和人气都够」的号，并把咖啡馆等级设为 <paramref name="level"/>。</summary>
    private static async Task<FeatureHarness> CafeAsync(string account, int level = 1,
                                                        int gold = 1_000_000, int hot = 100_000,
                                                        int playerLevel = 300)
    {
        var h = await FeatureHarness.CreateAsync(account);
        h.Player.Level = playerLevel;
        h.Player.AddMoney(CafeBizFeature.GoldMoneyId,
                          gold - h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(level, hot, 0));
        return h;
    }

    /// <summary>
    /// ★ 成功回包**必须带 `level`、绝不能带 `nError`**。
    /// 取证 `LuaCall.lua:1534/1536`：失败是 `if tbParam.nError then`（**真值**判断，
    /// 0 也为真！），成功是 `elseif tbParam.level then`。
    /// </summary>
    [Fact]
    public async Task LevelUp_成功回包带level且不带nError()
    {
        using var h = await CafeAsync("cafe-lvup-ok", level: 1);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.Equal(2, p["level"]!.GetValue<int>());
        Assert.Null(p["nError"]);          // ★ 真值判断 ⇒ 成功绝不能带
        Assert.Equal(2, CafeFeature.ReadState(h.Player).Level);
    }

    /// <summary>升级要真扣金币（<c>NeedGold</c>），并推 pf 1029 让客户端刷新显示。</summary>
    [Fact]
    public async Task LevelUp_扣金币并推1032()
    {
        using var h = await CafeAsync("cafe-lvup-gold", level: 1);
        int before = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        int spent = before - h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        Assert.True(spent > 0, "升级必须扣金币（NCafeLevels.txt 的 NeedGold）");
        // ★ 货币变更走 pf 1032 MoneyUpdateNtf（GameNotify.PushMoney:40），不是 1029
        Assert.True(await h.WaitForPfAsync(GamePf.MoneyUpdateNtf),
                    "没推 pf 1032 ⇒ 客户端金币显示不会变");
    }

    /// <summary>★ 升级**只扣金币、不扣人气**（`CafeLogic.lua:466` 对人气只做门槛判断）。</summary>
    [Fact]
    public async Task LevelUp_不扣人气()
    {
        using var h = await CafeAsync("cafe-lvup-hot", level: 1, hot: 12345);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        Assert.Equal(12345, CafeFeature.ReadState(h.Player).Hot);
    }

    /// <summary>金币不足 ⇒ 回 <c>nError = 3</c>、等级不变、不扣钱。</summary>
    [Fact]
    public async Task LevelUp_金币不足被拒()
    {
        using var h = await CafeAsync("cafe-lvup-poor", level: 1, gold: 0);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.Equal(CafeBizFeature.ErrNotEnoughGold, p["nError"]!.GetValue<int>());
        Assert.Null(p["level"]);
        Assert.Equal(1, CafeFeature.ReadState(h.Player).Level);
    }

    /// <summary>人气不足 ⇒ 回 <c>nError = 4</c>（L1→L2 的 <c>NeedHot</c> 是 20）。</summary>
    [Fact]
    public async Task LevelUp_人气不足被拒()
    {
        using var h = await CafeAsync("cafe-lvup-nohot", level: 1, hot: 0);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.Equal(CafeBizFeature.ErrNotEnoughHot, p["nError"]!.GetValue<int>());
        Assert.Equal(1, CafeFeature.ReadState(h.Player).Level);
    }

    /// <summary>玩家等级不足 ⇒ 回 <c>nError = 5</c>（服务端补的门槛，客户端预检没查）。</summary>
    [Fact]
    public async Task LevelUp_玩家等级不足被拒()
    {
        using var h = await CafeAsync("cafe-lvup-lowlv", level: 1, playerLevel: 0);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        // NeedLevel 现网 L2 是 1；玩家 0 级 ⇒ 被拒
        Assert.Equal(CafeBizFeature.ErrNotEnoughPlayerLevel, p["nError"]!.GetValue<int>());
    }

    /// <summary>★ 已达上限 ⇒ 回 <c>nError = 1</c>（不是崩、也不是静默）。</summary>
    [Fact]
    public async Task LevelUp_已达上限被拒()
    {
        using var h = await CafeAsync("cafe-lvup-max");
        var table = CafeTable.Load(TestPaths.SettingDir);
        CafeFeature.WriteState(h.Player,
            new CafeFeature.CafeState(table.MaxCafeLevel, 999_999, 0));

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.Equal(CafeBizFeature.ErrAlreadyMaxLevel, p["nError"]!.GetValue<int>());
    }

    /// <summary>连续升两级：等级递增、金币累计扣除（验证状态真的落盘）。</summary>
    [Fact]
    public async Task LevelUp_连续升两级()
    {
        using var h = await CafeAsync("cafe-lvup-twice", level: 1);
        int start = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);

        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");
        await h.DispatchLuaCallAsync(CafeBizFeature.LevelUpCmd, "{}");

        Assert.Equal(3, CafeFeature.ReadState(h.Player).Level);
        Assert.True(h.Player.GetMoney(CafeBizFeature.GoldMoneyId) < start);
    }

    // ================= 135 / 136 / 139 三种兑换 =================

    /// <summary>
    /// ★ 135 回包**必须带 <c>hot</c>**（新值）—— 消费点
    /// `NCafeNet.lua:45` <c>if tbParam and tbParam.hot then</c>，没有它整段跳过。
    /// 且必须已满级 + 人气 ≥ `NCafeLevels.txt` 的 `Level=10001` 行的 `NeedHot`（现网 30000）。
    /// </summary>
    [Fact]
    public async Task MaxHotReward_满级达标可兑换()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-maxhot-ok");
        var table = CafeTable.Load(TestPaths.SettingDir);
        // ★ 用 CafeLevels 精确查，不用 LevelRowFor —— 后者对缺失等级会兜底返回 Level 1
        int need = table.CafeLevels[CafeTable.MaxRewardLevelId].NeedHot;
        // 满级 + 人气够
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(table.MaxCafeLevel, need + 500, 0));

        await h.DispatchLuaCallAsync(CafeBizFeature.GetMaxHotRewardCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.Equal(500, p["hot"]!.GetValue<int>());          // ★ 扣掉 NeedHot 后的新值
        Assert.Equal(500, CafeFeature.ReadState(h.Player).Hot);
    }

    /// <summary>★ 未满级 ⇒ 不回包（`CafeLogic.lua:789` 要求「下一级无配置」）。</summary>
    [Fact]
    public async Task MaxHotReward_未满级不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-maxhot-lowlv");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(1, 999_999, 0));
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.GetMaxHotRewardCmd, "{}");

        Assert.Equal(n, h.Outbound.Count);
    }

    /// <summary>人气不够 ⇒ 不回包、不扣人气。</summary>
    [Fact]
    public async Task MaxHotReward_人气不够不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-maxhot-nohot");
        var table = CafeTable.Load(TestPaths.SettingDir);
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(table.MaxCafeLevel, 10, 0));
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.GetMaxHotRewardCmd, "{}");

        Assert.Equal(n, h.Outbound.Count);
        Assert.Equal(10, CafeFeature.ReadState(h.Player).Hot);
    }

    /// <summary>兑换要真发奖（`Level=10001` 行的 `LevelAwards`，现网 `[8-2-1-3-1]`）。</summary>
    [Fact]
    public async Task MaxHotReward_真发奖()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-maxhot-award");
        var table = CafeTable.Load(TestPaths.SettingDir);
        Assert.NotEmpty(table.MaxRewardAwards);      // 配置要真读到
        int need = table.CafeLevels[CafeTable.MaxRewardLevelId].NeedHot;
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(table.MaxCafeLevel, need, 0));
        int itemsBefore = h.Player.Items.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.GetMaxHotRewardCmd, "{}");

        Assert.True(h.Player.Items.Count > itemsBefore || h.Player.Money.Count > 0, "要真发出奖励");
    }

    // ---- 136 生日奖励 ----

    /// <summary>
    /// ★★★ 第八种错误约定：**失败必须完全不回包**。
    /// 消费点 `NCafeNet.lua:442..449`：<c>nRet == 0</c> 才成功，否则 **`error()` 直接抛**
    /// ⇒ 今天没人过生日时不能回 `{nRet: 1}` 之类。
    /// </summary>
    [Fact]
    public async Task BirthdayReward_今天无人过生日时不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-bd-none");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var now = DateTimeOffset.UtcNow;
        bool anyToday = table.Birthdays.Any(b => b.Month == now.Month && b.Day == now.Day);
        if (anyToday)
            return;      // 今天恰好有少女过生日 ⇒ 本用例不适用（换 BirthdayReward_有生日时成功 覆盖）

        int n = h.Outbound.Count;
        await h.DispatchLuaCallAsync(CafeBizFeature.GetBirthdayRewardCmd, "{}");
        Assert.Equal(n, h.Outbound.Count);
    }

    /// <summary>★ 未拥有该少女 ⇒ 不回包（与客户端 `GirlCommon:Has` 同源）。</summary>
    [Fact]
    public async Task BirthdayReward_未拥有少女时不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-bd-nogirl");
        h.Player.Girls.Clear();
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.GetBirthdayRewardCmd, "{}");

        Assert.Equal(n, h.Outbound.Count);
    }

    /// <summary>生日表要真读到 15 个，且奖励是五元组。</summary>
    [Fact]
    public void BirthdayReward_配置表读到()
    {
        var table = CafeTable.Load(TestPaths.SettingDir);
        Assert.True(table.Birthdays.Count >= 14, $"生日应 ≥14 个，实际 {table.Birthdays.Count}");
        foreach (var b in table.Birthdays)
        {
            Assert.InRange(b.Month, 1, 12);
            Assert.InRange(b.Day, 1, 31);
            Assert.NotEmpty(b.Awards);
            foreach (var a in b.Awards)
                Assert.Equal(5, a.Length);
        }
    }

    // ---- 139 好感度满级兑换 ----

    /// <summary>
    /// ★ 139 的客户端消费点只是 <c>ui:UpdateUI()</c>（不读任何字段），
    /// 但**必须回包**才会刷新界面。
    /// ★★ 兑换规则**未取证** ⇒ 本实现只回包、**不发奖不扣资源**（见 handler 注释）。
    /// </summary>
    [Fact]
    public async Task MaxExpReward_非法参数不发奖()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-maxexp");
        int itemsBefore = h.Player.Items.Count;
        int n = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.GetMaxExpRewardCmd, "{}");

        Assert.Equal(n, h.Outbound.Count); // invalid claim produces no success response
        Assert.Equal(itemsBefore, h.Player.Items.Count);        // ★ 不发奖
    }

    /// <summary>三条兑换命令的非法上行都不能崩。</summary>
    [Theory]
    [InlineData(CafeBizFeature.GetMaxHotRewardCmd)]
    [InlineData(CafeBizFeature.GetBirthdayRewardCmd)]
    [InlineData(CafeBizFeature.GetMaxExpRewardCmd)]
    public async Task 三种兑换_非法上行不崩(int cmd)
    {
        using var h = await FeatureHarness.CreateAsync($"cafe-ex-bad{cmd}");
        await h.DispatchLuaCallAsync(cmd, "[]");
        await h.DispatchLuaCallAsync(cmd, "{}");
        // 没抛就算过
    }

    // ================= 120 / 121 咖啡技能 =================

    /// <summary>某杯咖啡是否已学（任务值 group 23 的 bit 8）。</summary>
    private static bool Learned(Player p, int coffeeId)
        => (p.GetTask(CafeBizFeature.CoffeeTaskGroup, coffeeId)
            & (1 << CafeBizFeature.CoffeeLearnedBit)) != 0;

    /// <summary>
    /// ★★ 学习成功 = 任务值 <c>(23, 咖啡id)</c> 的 **bit 8 置 1** + 推 pf 1026，
    /// 且回包**绝不能带 <c>nError</c>**（A 族真值判断，`LuaCall.lua:1763`）。
    /// </summary>
    [Fact]
    public async Task LearnCoffee_置bit8并推1026()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-learn-ok");
        Assert.False(Learned(h.Player, 1));

        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":1}");

        Assert.True(Learned(h.Player, 1), "bit 8 必须置位");
        Assert.Null(Assert.IsType<JsonObject>(Payload(h))["nError"]);
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp),
                    "没推 pf 1026 ⇒ 客户端 OnCoffeeLearned 重读本地任务值会看到旧值");
    }

    /// <summary>★ 上行键是**大写 `Id`**（`UI_CoffeeList.lua:677`）；小写也兼容。</summary>
    [Theory]
    [InlineData("{\"Id\":2}")]
    [InlineData("{\"id\":2}")]
    public async Task LearnCoffee_大小写Id都认(string payload)
    {
        using var h = await FeatureHarness.CreateAsync($"cafe-learn-case{payload.Length}{payload[2]}");
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, payload);
        Assert.True(Learned(h.Player, 2));
    }

    /// <summary>重复学习 ⇒ 回 <c>nError = 7</c>（已学过）。</summary>
    [Fact]
    public async Task LearnCoffee_重复学习被拒()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-learn-dup");
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":1}");

        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":1}");

        Assert.Equal(CafeBizFeature.ErrCoffeeAlreadyLearned,
                     Assert.IsType<JsonObject>(Payload(h))["nError"]!.GetValue<int>());
    }

    /// <summary>
    /// 关卡条件不能产生虚假的前置咖啡限制。
    /// 咖啡6要求普通4-12通关，不要求先学习咖啡4。
    /// </summary>
    [Fact]
    public async Task LearnCoffee_不误用关卡号为前置咖啡()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-learn-prereq");
        h.Player.RecordLevelPass(MapList.MapId(4, 12, 1), 7);

        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":6}");

        Assert.Null(Assert.IsType<JsonObject>(Payload(h))["nError"]);
        Assert.True(Learned(h.Player, 6));
    }

    /// <summary>连续学习不同咖啡均保存学习位。</summary>
    [Fact]
    public async Task LearnCoffee_连续学习保存位域()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-learn-chain");
        h.Player.RecordLevelPass(MapList.MapId(4, 12, 1), 7);

        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":4}");
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":6}");

        Assert.True(Learned(h.Player, 4));
        Assert.True(Learned(h.Player, 6));
        Assert.Null(Assert.IsType<JsonObject>(Payload(h))["nError"]);
    }

    /// <summary>非法 id ⇒ 回 <c>nError = 6</c>（含 ≥1000 的哨兵，`CoffeeCommon.lua:556`）。</summary>
    [Theory]
    [InlineData("{\"Id\":0}")]
    [InlineData("{\"Id\":-1}")]
    [InlineData("{\"Id\":1000}")]
    [InlineData("{\"Id\":99999}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task LearnCoffee_非法id被拒(string payload)
    {
        using var h = await FeatureHarness.CreateAsync($"cafe-learn-bad{payload.GetHashCode() & 0xffff}");
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, payload);
        Assert.Equal(CafeBizFeature.ErrCoffeeNotExists,
                     Assert.IsType<JsonObject>(Payload(h))["nError"]!.GetValue<int>());
    }

    /// <summary>121 重置：清掉所有已学标记，回包不带 <c>nError</c>。</summary>
    [Fact]
    public async Task ResetCoffee_清掉所有已学()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-reset");
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":1}");
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":2}");
        Assert.True(Learned(h.Player, 1) && Learned(h.Player, 2));

        await h.DispatchLuaCallAsync(CafeBizFeature.ResetCoffeeCmd, "{}");

        Assert.False(Learned(h.Player, 1));
        Assert.False(Learned(h.Player, 2));
        Assert.Null(Assert.IsType<JsonObject>(Payload(h))["nError"]);
    }

    /// <summary>★ 重置只清 bit 8，**不动位域里的其它位**（bit 0..2 是前置条件标记）。</summary>
    [Fact]
    public async Task ResetCoffee_只清bit8不动其它位()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-reset-bits");
        // 先手工把 bit 0/1 置上（模拟前置条件已达成）
        h.Player.SetTask(CafeBizFeature.CoffeeTaskGroup, 1, 0b011);
        await h.DispatchLuaCallAsync(CafeBizFeature.LearnCoffeeCmd, "{\"Id\":1}");

        await h.DispatchLuaCallAsync(CafeBizFeature.ResetCoffeeCmd, "{}");

        int bits = h.Player.GetTask(CafeBizFeature.CoffeeTaskGroup, 1);
        Assert.Equal(0b011, bits);      // bit 0/1 保留，bit 8 被清
    }

    /// <summary>没学过任何咖啡时重置 ⇒ 不崩、回 0。</summary>
    [Fact]
    public async Task ResetCoffee_空状态不崩()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-reset-empty");
        await h.DispatchLuaCallAsync(CafeBizFeature.ResetCoffeeCmd, "{}");
        Assert.Equal(0, Assert.IsType<JsonObject>(Payload(h))["reset"]!.GetValue<int>());
    }

    // ================= 122 营业报告 / 125 离店 =================

    /// <summary>
    /// ★★★ 122 的六个收益字段**全部裸解引用** —— 缺一个客户端就 NRE。
    /// 取证 `CoffeeCommon.lua:345..347`：
    /// <c>earnings.gold + earnings_ex.gold</c>（hot / coffeecoin 同理，三对）。
    /// ⇒ <c>earnings</c> 与 <c>earnings_ex</c> 都必须是对象且三键齐全（0 也要给）。
    /// </summary>
    [Fact]
    public async Task Report_六个收益字段齐全()
    {
        using var h = await ReadyForSeatAsync("cafe-report-shape");

        // 走一遍 117 入座 + 116 结算，让报告有内容
        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var seated = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player),
                                            table, CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow))
                                .Seats.First(x => x.CustomerIdx is not null);
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{seated.CustomerIdx}}}");

        // 125 离店 ⇒ 推 122
        await h.DispatchLuaCallAsync(CafeBizFeature.OnLeaveCmd, "{\"usetime\":60}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        foreach (var key in new[] { "earnings", "earnings_ex" })
        {
            var o = Assert.IsType<JsonObject>(p[key]);
            Assert.NotNull(o["gold"]);          // ★ 三个键一个都不能少
            Assert.NotNull(o["coffeecoin"]);
            Assert.NotNull(o["hot"]);
        }
    }

    /// <summary>
    /// ★★ <c>outlist</c> 是**映射** <c>{"咖啡id": 数量}</c>，不是数组。
    /// 取证 `UI_BusinessReport.lua:88` <c>for coffeetype, count in pairs(outlist)</c>。
    /// （该文件 `:42` 的注释示例写成数组，**那是过时的**，以代码为准。）
    /// </summary>
    [Fact]
    public async Task Report_outlist是映射不是数组()
    {
        using var h = await ReadyForSeatAsync("cafe-report-outlist");
        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var seated = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player),
                                            table, CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow))
                                .Seats.First(x => x.CustomerIdx is not null);
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{seated.CustomerIdx}}}");
        await h.DispatchLuaCallAsync(CafeBizFeature.OnLeaveCmd, "{\"usetime\":60}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        var outList = Assert.IsType<JsonObject>(p["outlist"]);   // ★ 对象，不是 JsonArray
        Assert.NotEmpty(outList);
        // 键是咖啡 id 的字符串、值是数量
        var first = outList.First();
        Assert.True(int.TryParse(first.Key, out int ct) && ct > 0);
        Assert.True(first.Value!.GetValue<int>() > 0);
    }

    /// <summary>★ 本期无成交 ⇒ **不推**（客户端判据 `CountTB(outlist) > 0`，空报告会被丢弃）。</summary>
    [Fact]
    public async Task Report_无成交时不推()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-report-empty");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 0, 0));
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.OnLeaveCmd, "{\"usetime\":30}");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>多杯累计：卖 2 杯同种咖啡 ⇒ outlist 该项计数为 2、收益累加。</summary>
    [Fact]
    public async Task Report_多杯累计()
    {
        using var h = await ReadyForSeatAsync("cafe-report-multi");
        var table = CafeTable.Load(TestPaths.SettingDir);

        for (int i = 0; i < 2; i++)
        {
            await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");
            await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");
            var seated = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player),
                                                table, CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow))
                                    .Seats.FirstOrDefault(x => x.CustomerIdx is not null);
            if (seated is null)
                break;
            await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
                $"{{\"customeridx\":{seated.CustomerIdx}}}");
        }

        var acc = CafeBizFeature.ReadReport(h.Player);
        Assert.True(acc.OutList.Values.Sum() >= 2, $"应累计 ≥2 杯，实际 {acc.OutList.Values.Sum()}");
        Assert.True(acc.BaseGold > 0);
    }

    /// <summary>★ 推送后累计清零（一份报告只汇报「上次报告以来」的成果）。</summary>
    [Fact]
    public async Task Report_推送后清零()
    {
        using var h = await ReadyForSeatAsync("cafe-report-reset");
        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var seated = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player),
                                            table, CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow))
                                .Seats.First(x => x.CustomerIdx is not null);
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{seated.CustomerIdx}}}");
        Assert.NotEmpty(CafeBizFeature.ReadReport(h.Player).OutList);

        await h.DispatchLuaCallAsync(CafeBizFeature.OnLeaveCmd, "{\"usetime\":60}");

        Assert.Empty(CafeBizFeature.ReadReport(h.Player).OutList);
        Assert.Equal(0, CafeBizFeature.ReadReport(h.Player).BaseGold);
    }

    /// <summary>125 的非法上行不能崩（客户端 else 分支为空 ⇒ 不需要回执）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"usetime\":0}")]
    public async Task OnLeave_非法上行不崩(string payload)
    {
        using var h = await FeatureHarness.CreateAsync($"cafe-leave-bad{payload.Length}");
        await h.DispatchLuaCallAsync(CafeBizFeature.OnLeaveCmd, payload);
        // 没抛就算过
    }

    /// <summary>报告累计的存档损坏时安全降级（全 0 + 空清单，不抛）。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"outlist\":[]}")]
    [InlineData("{\"outlist\":{\"abc\":1}}")]
    public void Report_存档损坏时降级(string json)
    {
        var p = new Player("t", 1);
        p.Extra[CafeBizFeature.ReportExtraKey] = JsonNode.Parse(json);
        var acc = CafeBizFeature.ReadReport(p);
        Assert.Empty(acc.OutList);      // 非法键被丢弃
        Assert.Equal(0, acc.BaseGold);
    }

    // ================= 124 生成顾客权重 =================

    /// <summary>
    /// ★★ 124 的回包是**裸数组**（不是对象），且字段名是
    /// <c>addguesttype</c>/<c>addguestvalue</c> —— **不是** 112 那套大写的 <c>Id</c>/<c>Weight</c>。
    ///
    /// 取证：`LuaCall.lua:1820..1825` 的原厂注释 + 消费点
    /// `CafeLogic:AddGuestWight`（`CafeLogic.lua:284..287`）里
    /// <c>v['addguesttype']</c> / <c>v['addguestvalue']</c>。
    /// 照抄 112 的字段名会让 `Add_Weight[nil] = nil` **静默失效、不报错**。
    /// </summary>
    [Fact]
    public async Task AddGuestWeight_回裸数组且字段名正确()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-weight");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 0, 0));
        // 先进一次 112 让 weightlist 生成（今日顾客按天派生）
        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "{}");

        await h.DispatchLuaCallAsync(CafeBizFeature.AddGuestWeightCmd, "{}");

        // ★ tbParam 本身就是数组
        var arr = Assert.IsType<JsonArray>(Payload(h));
        Assert.NotEmpty(arr);
        foreach (var e in arr)
        {
            var o = Assert.IsType<JsonObject>(e);
            Assert.NotNull(o["addguesttype"]);      // ★ 不是 "Id"
            Assert.NotNull(o["addguestvalue"]);     // ★ 不是 "Weight"
            Assert.Null(o["Id"]);
            Assert.Null(o["Weight"]);
            Assert.True(o["addguesttype"]!.GetValue<int>() > 0);
        }
    }

    /// <summary>124 的权重要与 112 的 <c>weightlist</c> **同源**（同一份今日顾客）。</summary>
    [Fact]
    public async Task AddGuestWeight_与112同源()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-weight-same");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 0, 0));

        await h.DispatchLuaCallAsync(CafeFeature.ReqCafeDataCmd, "{}");
        var from112 = ((JsonArray)Assert.IsType<JsonObject>(Payload(h))["weightlist"]!)
            .Select(x => (x!["Id"]!.GetValue<int>(), x["Weight"]!.GetValue<int>()))
            .OrderBy(t => t.Item1).ToList();

        await h.DispatchLuaCallAsync(CafeBizFeature.AddGuestWeightCmd, "{}");
        var from124 = ((JsonArray)Payload(h))
            .Select(x => (x!["addguesttype"]!.GetValue<int>(), x["addguestvalue"]!.GetValue<int>()))
            .OrderBy(t => t.Item1).ToList();

        Assert.Equal(from112, from124);
    }

    /// <summary>★ 上行只有 `{"sCmd":124}`（连 tbParam 都没有）时不能崩 —— 真机实测形态。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task AddGuestWeight_非法上行不崩(string payload)
    {
        using var h = await FeatureHarness.CreateAsync($"cafe-weight-bad{payload.Length}");
        await h.DispatchLuaCallAsync(CafeBizFeature.AddGuestWeightCmd, payload);
        // 没抛就算过
    }

    // ================= 117 座位空，请顾客入位 =================



    /// <summary>造「橱窗有咖啡 + 有空座 + 队列有人」的场景（117 的三个前置条件）。</summary>
    private static async Task<FeatureHarness> ReadyForSeatAsync(string account)
    {
        var h = await FeatureHarness.CreateAsync(account);
        h.Player.Level = 300;
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 100, 0));

        // ★ 119 的上行是**扁平**的 {coffeetype, count}（不是数组），见 OnMakeCoffee:442
        await MakeLearnedCoffeeAsync(h,
            "{\"coffeetype\":1,\"count\":3}");
        // ★ 座位必须先用 114 造出来 —— ReadContent 对新号不会凭空生成空座位
        //   （字段名 position 且必须 > 0，见 OnUpdateSeatList:312）
        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd,
            "{\"seatlist\":[{\"position\":1},{\"position\":2}]}");
        // 队列里放一位顾客（115）
        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");
        return h;
    }

    /// <summary>
    /// ★★★ 117 回包的硬约束（消费点 `NCafeNet.lua:266..301`）：
    /// <c>newguest</c> 是**判据**（没有它整段跳过 ⇒ 顾客不入座、循环停）；
    /// 三个清单 <c>seatlist</c>/<c>customerqueue</c>/<c>coffeelist</c> 都被
    /// <c>ipairs</c> **无条件遍历** ⇒ 缺一个就抛。
    /// </summary>
    [Fact]
    public async Task NewSeat_回包四件套齐全()
    {
        using var h = await ReadyForSeatAsync("cafe-seat-shape");

        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.NotNull(p["newguest"]);                       // ★ 判据
        Assert.IsType<JsonArray>(p["seatlist"]);             // ★ ipairs 无条件遍历
        Assert.IsType<JsonArray>(p["customerqueue"]);
        Assert.IsType<JsonArray>(p["coffeelist"]);
    }

    /// <summary>
    /// ★★ <c>newguest.position</c> **必须能在 `seatlist` 里找到** ——
    /// 客户端 `GetGuestSeat(newguest.position)` 再 `LoadGuest(seat.guest)`，
    /// 对不上就 NRE（`NCafeNet.lua:297..299`）。
    /// </summary>
    [Fact]
    public async Task NewSeat_newguest的position在seatlist里()
    {
        using var h = await ReadyForSeatAsync("cafe-seat-pos");

        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        int pos = p["newguest"]!["position"]!.GetValue<int>();
        var seats = (JsonArray)p["seatlist"]!;
        var hit = seats.FirstOrDefault(s => s!["position"]!.GetValue<int>() == pos);
        Assert.NotNull(hit);
        // 且那个座位上确实是这位顾客
        Assert.Equal(p["newguest"]!["customeridx"]!.GetValue<int>(),
                     hit!["customeridx"]!.GetValue<int>());
    }

    /// <summary>顾客从队列移到座位：队列少一个、座位多一个有人的。</summary>
    [Fact]
    public async Task NewSeat_队列出队且座位落座()
    {
        using var h = await ReadyForSeatAsync("cafe-seat-move");
        var table = CafeTable.Load(TestPaths.SettingDir);
        var before = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player), table, 0);
        int qBefore = before.Queue.Count;
        Assert.True(qBefore > 0, "前置条件：队列要有人");

        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");

        var after = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player), table, 0);
        Assert.Equal(qBefore - 1, after.Queue.Count);
        Assert.Contains(after.Seats, s => s.CustomerIdx is not null);
    }

    /// <summary>★ 入座要消耗橱窗一杯咖啡（否则咖啡永远用不完）。</summary>
    [Fact]
    public async Task NewSeat_消耗一杯咖啡()
    {
        using var h = await ReadyForSeatAsync("cafe-seat-coffee");
        var table = CafeTable.Load(TestPaths.SettingDir);
        int before = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player), table, 0)
                                .Coffees.Sum(c => c.Count);

        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");

        int after = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player), table, 0)
                               .Coffees.Sum(c => c.Count);
        Assert.Equal(before - 1, after);
    }

    /// <summary>队列为空 ⇒ 不回包（客户端本来也不该发）。</summary>
    [Fact]
    public async Task NewSeat_队列空时不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-seat-noq");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 0, 0));
        await MakeLearnedCoffeeAsync(h,
            "{\"coffeetype\":1,\"count\":1}");
        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd,
            "{\"seatlist\":[{\"position\":1}]}");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>橱窗无咖啡 ⇒ 不回包（客户端的前置条件之一）。</summary>
    [Fact]
    public async Task NewSeat_无咖啡时不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-seat-nocoffee");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 0, 0));
        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd,
            "{\"seatlist\":[{\"position\":1}]}");
        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>★ 上行是**空数组**时不能崩（真机实测就是 `tbParam = []`）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task NewSeat_非法上行不崩(string payload)
    {
        using var h = await ReadyForSeatAsync($"cafe-seat-bad{payload.Length}");
        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, payload);
        // 没抛就算过
    }

    /// <summary>★ 完整闭环：入座（117）→ 喝完（116）→ 座位重新变空，可再次入座。</summary>
    [Fact]
    public async Task NewSeat_与116构成闭环()
    {
        using var h = await ReadyForSeatAsync("cafe-seat-loop");
        var table = CafeTable.Load(TestPaths.SettingDir);

        // 117 入座
        await h.DispatchLuaCallAsync(CafeBizFeature.NewSeatPosEmptyCmd, "{}");
        var seated = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player), table, 0)
                                .Seats.First(s => s.CustomerIdx is not null);

        // 116 结算
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd,
            $"{{\"customeridx\":{seated.CustomerIdx}}}");

        // 座位重新变空 ⇒ 循环可继续
        var after = CafeFeature.ReadContent(h.Player, CafeFeature.ReadState(h.Player), table, 0);
        Assert.DoesNotContain(after.Seats, s => s.CustomerIdx == seated.CustomerIdx);
    }

    // ================= 116 顾客喝完结算 =================

    /// <summary>造一个「座位上有顾客且有咖啡」的场景，返回那位顾客的 customeridx。</summary>
    private static async Task<(FeatureHarness H, int Idx)> SeatedAsync(
        string account, int coffeeType = 1, int customerType = 201)
    {
        var h = await FeatureHarness.CreateAsync(account);
        h.Player.Level = 300;
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 1000, 0));

        // 先让 115 生成一位排队顾客，再用 114 把他放到座位上（走正规链路造数据）
        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");

        const int idx = 7;
        // ★ 字段名是 `position`（不是 pos），且必须 > 0 —— 0 会被 114 当无意义跳过
        //   （CafeBizFeature.OnUpdateSeatList:312）
        string seat = $"[{{\"position\":1,\"customeridx\":{idx},\"customertype\":{customerType}," +
                      $"\"coffeetype\":{coffeeType},\"starttime\":1}}]";
        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd, $"{{\"seatlist\":{seat}}}");
        return (h, idx);
    }

    /// <summary>
    /// ★★★ 116 回包的三条硬约束（消费点 `NCafeNet.lua:320..342`）：
    /// <c>customeridx</c> + <c>earn_base</c> + <c>earn_ex</c> 三者缺一整段跳过
    /// ⇒ 顾客永不离场、`ReqCustomerQueue` 不触发 ⇒ **经营循环卡死**；
    /// 且两个 earn 必须是**三元素数组**、<c>hot</c> 必填。
    /// </summary>
    [Fact]
    public async Task DrinkOff_回包三字段齐全且earn是三元素数组()
    {
        var (h, idx) = await SeatedAsync("cafe-drink-shape");
        using var _ = h;

        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{idx}}}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.Equal(idx, p["customeridx"]!.GetValue<int>());

        var b = Assert.IsType<JsonArray>(p["earn_base"]);
        var e = Assert.IsType<JsonArray>(p["earn_ex"]);
        Assert.Equal(3, b.Count);                       // ★ 客户端逐个取 [1]/[2]/[3]
        Assert.Equal(3, e.Count);
        Assert.NotNull(p["hot"]);                       // ★ :338 无条件赋值
    }

    /// <summary>结算后该座位要被清空（顾客离场），否则下次进店还坐在那里。</summary>
    [Fact]
    public async Task DrinkOff_座位被清空()
    {
        var (h, idx) = await SeatedAsync("cafe-drink-seat");
        using var _ = h;

        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{idx}}}");

        var state = CafeFeature.ReadState(h.Player);
        var content = CafeFeature.ReadContent(h.Player, state, CafeTable.Load(TestPaths.SettingDir), 0);
        Assert.DoesNotContain(content.Seats, s => s.CustomerIdx == idx);
    }

    /// <summary>★ 匹配的顾客拿全额基础收益（1★ 咖啡 AwardA=10 / AwardB=1 / AwardC=1）。</summary>
    [Fact]
    public async Task DrinkOff_匹配时拿全额收益()
    {
        // 顾客 201 要 Appearance>=51；咖啡 1 的 Appearance=55 ⇒ 匹配
        var (h, idx) = await SeatedAsync("cafe-drink-match", coffeeType: 1, customerType: 201);
        using var _ = h;
        int goldBefore = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);

        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{idx}}}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        var b = (JsonArray)p["earn_base"]!;
        Assert.Equal(10, b[0]!.GetValue<int>());        // AwardA 全额
        Assert.Equal(1, b[1]!.GetValue<int>());         // AwardB
        Assert.Equal(1, b[2]!.GetValue<int>());         // AwardC
        Assert.True(h.Player.GetMoney(CafeBizFeature.GoldMoneyId) > goldBefore, "金币要真到账");
    }

    /// <summary>★ 不匹配的顾客只拿一半（本实现的取舍，见 OnDrinkOffCoffee 注释）。</summary>
    [Fact]
    public async Task DrinkOff_不匹配时收益减半()
    {
        // 顾客 205 要 Appearance>=61；咖啡 1 只有 55 ⇒ 不匹配
        var (h, idx) = await SeatedAsync("cafe-drink-nomatch", coffeeType: 1, customerType: 205);
        using var _ = h;

        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{idx}}}");

        var b = (JsonArray)Assert.IsType<JsonObject>(Payload(h))["earn_base"]!;
        Assert.Equal(5, b[0]!.GetValue<int>());         // 10 / 2
    }

    /// <summary>人气要累加进存档，并在回包里回**新**值（客户端直接赋给 NCafe.Data.Hot）。</summary>
    [Fact]
    public async Task DrinkOff_人气累加且回新值()
    {
        var (h, idx) = await SeatedAsync("cafe-drink-hot");
        using var _ = h;
        int hotBefore = CafeFeature.ReadState(h.Player).Hot;

        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{idx}}}");

        int hotAfter = CafeFeature.ReadState(h.Player).Hot;
        Assert.True(hotAfter > hotBefore, "人气要累加");
        Assert.Equal(hotAfter, Assert.IsType<JsonObject>(Payload(h))["hot"]!.GetValue<int>());
    }

    /// <summary>找不到顾客 ⇒ **不回包**（对应原厂注释的 error=-2；客户端没有错误分支）。</summary>
    [Fact]
    public async Task DrinkOff_顾客不存在时不回包()
    {
        using var h = await FeatureHarness.CreateAsync("cafe-drink-404");
        CafeFeature.WriteState(h.Player, new CafeFeature.CafeState(5, 0, 0));
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, "{\"customeridx\":9999}");

        Assert.Equal(before, h.Outbound.Count);        // 一条都没回
    }

    /// <summary>★ 缺 <c>customeridx</c> / 上行是数组时不能崩（`JsonNode["k"]` 对数组会抛）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"customeridx\":-1}")]
    public async Task DrinkOff_非法上行不崩(string payload)
    {
        using var h = await FeatureHarness.CreateAsync($"cafe-drink-bad{payload.Length}");
        await h.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, payload);
        // 没抛就算过
    }

    /// <summary>收益计算是**确定性**的：同一输入重放得到同一结果（便于排障）。</summary>
    [Fact]
    public async Task DrinkOff_收益确定性()
    {
        var (h1, i1) = await SeatedAsync("cafe-det-a");
        var (h2, i2) = await SeatedAsync("cafe-det-a2");
        using var _1 = h1;
        using var _2 = h2;

        await h1.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{i1}}}");
        await h2.DispatchLuaCallAsync(CafeBizFeature.DrinkOffCoffeeCmd, $"{{\"customeridx\":{i2}}}");

        var b1 = ((JsonArray)Assert.IsType<JsonObject>(Payload(h1))["earn_base"]!).ToJsonString();
        var b2 = ((JsonArray)Assert.IsType<JsonObject>(Payload(h2))["earn_base"]!).ToJsonString();
        Assert.Equal(b1, b2);      // 基础收益只看咖啡与匹配 ⇒ 必然相同
    }

    // ================= 115 生成排队顾客 =================

    /// <summary>
    /// ★ 115 的回包**必须带 `basetime`** —— 客户端 `NCafeNet.lua:230` 是
    /// `if tbParam and tbParam.basetime then`，没有它整段跳过（等于白回一趟，队列不会加载）。
    /// 且队列里每个元素的 `customertype`/`customeridx`/`starttime` 三者都必填
    /// （`:238 AddGuest2Queue` 三个参数全无 nil 保护）。
    /// </summary>
    [Fact]
    public async Task GenCustomer_ReplyHasBasetimeAndFullQueueEntries()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-gen");

        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.True(p["basetime"]!.GetValue<long>() > 1_700_000_000, "basetime 必须是服务器 unix 秒");

        var queue = Assert.IsType<JsonArray>(p["customerqueue"]);
        Assert.NotEmpty(queue);
        foreach (var item in queue)
        {
            var o = Assert.IsType<JsonObject>(item);
            Assert.NotNull(o["customertype"]);      // 三个都必填
            Assert.NotNull(o["customeridx"]);
            Assert.NotNull(o["starttime"]);
        }
        // LuaCall 数字族：成功回包不能带 nError（客户端是真值判断，0 也算失败）
        Assert.DoesNotContain("nError", p.ToJsonString());
    }

    /// <summary>生成的顾客类型必须是 `NCafeGuests.txt` 里的合法值 —— 非法值会让客户端卡加载 0.75。</summary>
    [Fact]
    public async Task GenCustomer_PicksOnlyKnownGuestTemplates()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-known");

        for (int i = 0; i < 12; i++)
            await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");

        var queue = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Payload(h))["customerqueue"]);
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        foreach (var item in queue)
        {
            int type = Assert.IsType<JsonObject>(item)["customertype"]!.GetValue<int>();
            Assert.True(table.Guests.ContainsKey(type),
                $"顾客类型 {type} 不在 NCafeGuests.txt 里 —— 会让 LoadGuest NRE、加载条卡 0.75");
        }
    }

    /// <summary>`customeridx` 不能重复（客户端拿它当顾客实例 Guid，重复会互相顶掉）。</summary>
    [Fact]
    public async Task GenCustomer_AssignsUniqueCustomerIdx()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-idx");

        for (int i = 0; i < 5; i++)
            await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");

        var queue = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Payload(h))["customerqueue"]);
        var ids = queue.Select(x => Assert.IsType<JsonObject>(x)["customeridx"]!.GetValue<int>()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// ★ 队列上限**来自配置表**（`NCafeLevels.txt` 当前等级的 `MaxQSize`，lv=1 实测是 2），
    /// 不是服务端拍的常量 —— `CafeFeature.ReadQueue` 读回时按它截断，
    /// 写入侧必须用同一个来源，否则「写 8 个读出 2 个」。
    /// </summary>
    [Fact]
    public async Task GenCustomer_StopsAtQueueCapFromLevelTable()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-cap");
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        var state = CafeFeature.ReadState(h.Player, table);
        int cap = table.LevelRowFor(state.Level) is { MaxQSize: > 0 } lv ? lv.MaxQSize : 1;
        Assert.True(cap > 0, "NCafeLevels.txt 的 MaxQSize 应 > 0");

        for (int i = 0; i < cap + 6; i++)
            await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, "{}");

        var queue = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Payload(h))["customerqueue"]);
        Assert.Equal(cap, queue.Count);
    }

    // ================= 114 座位上报 =================

    /// <summary>
    /// ★ 114 的回包内容客户端**完全不读**（`NCafeNet.lua:204` 整个函数体只有
    /// `NCafeNet.ReqNewCustomer()`），但**必须回** —— 不回客户端就不会继续让顾客入座。
    /// </summary>
    [Fact]
    public async Task UpdateSeatList_StoresSeatsAndReplies()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-seat");

        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd, """
            {"seatlist":[{"position":1},{"position":2},{"position":3},{"position":4}]}
            """);

        // 回包存在即可（内容不被读）
        Assert.IsType<JsonObject>(Payload(h));

        // 存档里有 4 个座位，且下次 112 能读出来（§15.3 的回流闭环）
        var state = CafeFeature.ReadState(h.Player);
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        var content = CafeFeature.ReadContent(h.Player, state, table,
                                              CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
        Assert.Equal(4, content.Seats.Count);
        Assert.All(content.Seats, s => Assert.Null(s.CustomerIdx));   // 空座位
    }

    /// <summary>
    /// ★★ 空座位与有顾客的座位靠 **`customeridx` 键在不在**区分 ——
    /// 客户端自己上传时就是这个形状（`NCafeNet.lua:191..197`：没顾客只发 `position`）。
    /// </summary>
    [Fact]
    public async Task UpdateSeatList_DistinguishesEmptyByMissingCustomerIdx()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-seat-guest");
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        int goodType = table.Guests.Keys.First();

        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd, $$"""
            {"seatlist":[
              {"position":1},
              {"position":2,"customeridx":7,"customertype":{{goodType}},"coffeetype":1,"starttime":123}
            ]}
            """);

        var state = CafeFeature.ReadState(h.Player);
        var content = CafeFeature.ReadContent(h.Player, state, table,
                                              CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
        var seats = content.Seats.OrderBy(s => s.Position).ToList();
        Assert.Null(seats[0].CustomerIdx);                 // 只发了 position ⇒ 空
        Assert.Equal(7, seats[1].CustomerIdx);             // 发了 customeridx ⇒ 有人
        Assert.Equal(goodType, seats[1].CustomerType);
    }

    /// <summary>
    /// ★ 座位上的 `customertype` 非法时必须**降级成空座位**而不是原样存 ——
    /// 否则下次 112 把它发回去会让客户端 `LoadGuest` NRE、**加载条卡 0.75**
    /// （logcat 打 `NCafe: cannot find guest template`）。
    /// </summary>
    [Fact]
    public async Task UpdateSeatList_RejectsUnknownCustomerType()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-badtype");

        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd, """
            {"seatlist":[{"position":1,"customeridx":3,"customertype":999999,"starttime":1}]}
            """);

        var state = CafeFeature.ReadState(h.Player);
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        var content = CafeFeature.ReadContent(h.Player, state, table,
                                              CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
        var seat = Assert.Single(content.Seats);
        Assert.Null(seat.CustomerIdx);                     // 已降级为空座位
    }

    // ================= 113 排班少女 =================

    /// <summary>
    /// ★ 113 的回包是**数组的数组**，外层三项（经营/休息/宿舍），
    /// 内层每个少女是 **`[girlId, modelId]` 二元数组**
    /// （`NCafeNet.lua:386` `UpdateAreaGirl(area, girl[1], girl[2])`，两个下标都无保护）。
    /// </summary>
    [Fact]
    public async Task SetWaiterList_ReplyIsArrayOfPairs()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-waiter");
        uint girl = h.Player.Girls.First().Girlid;

        await h.DispatchLuaCallAsync(CafeBizFeature.SetWaiterListCmd, $"[[[{girl},0]],[],[]]");

        var outer = Assert.IsType<JsonArray>(Payload(h));
        Assert.Equal(CafeFeature.AreaCount, outer.Count);          // 外层必须三项
        var pair = Assert.IsType<JsonArray>(Assert.IsType<JsonArray>(outer[0]!).First());
        Assert.Equal(2, pair.Count);                                // [girlId, modelId]
        Assert.Equal((int)girl, pair[0]!.GetValue<int>());
    }

    /// <summary>只认玩家真正持有的少女（客户端 `ReqSetWaiterList` 也会先 `GetGirl` 过滤）。</summary>
    [Fact]
    public async Task SetWaiterList_IgnoresGirlsPlayerDoesNotOwn()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-waiter-bad");

        await h.DispatchLuaCallAsync(CafeBizFeature.SetWaiterListCmd, "[[[999999,0]],[],[]]");

        var outer = Assert.IsType<JsonArray>(Payload(h));
        Assert.Empty(Assert.IsType<JsonArray>(outer[0]!));
    }

    [Fact]
    public async Task AssignmentsRespectClosedRoomsAndCapacityBeforeReplyAndSave()
    {
        using var h = await CafeAsync("cafe-closed-assignment", level: 1);
        h.Player.Girls.Clear();
        for (uint id = 1; id <= 4; id++) h.Player.Girls.Add(new Girl { Girlid = id, Modelid = id + 10 });
        await h.DispatchLuaCallAsync(CafeBizFeature.SetWaiterListCmd,
            "[[[1,999],[2,999]],[[3,999]],[[4,999]]]");
        var reply = Assert.IsType<JsonArray>(Payload(h));
        var cafe = Assert.IsType<JsonArray>(reply[0]);
        Assert.Single(cafe);
        Assert.Equal(11, cafe[0]![1]!.GetValue<int>());
        Assert.Empty(Assert.IsType<JsonArray>(reply[1]));
        Assert.Empty(Assert.IsType<JsonArray>(reply[2]));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        var table = CafeTable.Load(TestPaths.SettingDir);
        var content = CafeFeature.ReadContent(saved, CafeFeature.ReadState(saved, table), table,
            CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow), 0);
        Assert.Single(content.AreaGirls[0]);
        Assert.Empty(content.AreaGirls[1]);
        Assert.Empty(content.AreaGirls[2]);
    }

    [Fact]
    public async Task AssignmentsDeduplicateWithinAndAcrossRoomsAndKeepOpenDorm()
    {
        using var h = await CafeAsync("cafe-unique-assignment", level: 30);
        h.Player.Girls.Clear();
        for (uint id = 1; id <= 3; id++) h.Player.Girls.Add(new Girl { Girlid = id, Modelid = id });
        await h.DispatchLuaCallAsync(CafeBizFeature.SetWaiterListCmd,
            "[[[1,99],[1,99]],[[1,99],[2,99]],[[2,99],[3,99]]]");
        var reply = Assert.IsType<JsonArray>(Payload(h));
        for (int i = 0; i < 3; i++)
        {
            var area = Assert.IsType<JsonArray>(reply[i]);
            Assert.Single(area);
            Assert.Equal(i + 1, area[0]![0]!.GetValue<int>());
            Assert.Equal(i + 1, area[0]![1]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task ExistingAssignmentsAreNormalizedOnReadIncludingZeroCapacity()
    {
        using var h = await CafeAsync("cafe-existing-assignment", level: 1);
        h.Player.Girls.Clear();
        h.Player.Girls.Add(new Girl { Girlid = 1, Modelid = 7 });
        var table = CafeTable.Load(TestPaths.SettingDir);
        var raw = JsonNode.Parse("[[[1,999],[1,999]],[[1,999]],[[1,999]]]")!.AsArray();
        var areas = CafeFeature.ReadAreaGirls(raw, h.Player, table.LevelRowFor(1), 0);
        Assert.Single(areas[0]);
        Assert.Equal(7, areas[0][0].ModelId);
        Assert.Empty(areas[1]);
        Assert.Empty(areas[2]);
        areas = CafeFeature.ReadAreaGirls(raw, h.Player, table.LevelRowFor(30), 0);
        Assert.Single(areas[0]);
        Assert.Empty(areas[1]);
        Assert.Empty(areas[2]);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(30)]
    public async Task EachAreaIsCappedAtConfiguredSlots(int level)
    {
        using var h = await CafeAsync("cafe-assignment-cap", level: level);
        h.Player.Girls.Clear();
        var table = CafeTable.Load(TestPaths.SettingDir);
        var row = table.LevelRowFor(level)!;
        int[] limits = [row.CafeUnlockNum, row.RestUnlockNum, row.DormUnlockNum];
        var request = new JsonArray();
        uint id = 1;
        for (int area = 0; area < 3; area++)
        {
            var slot = new JsonArray();
            for (int n = 0; n < limits[area] + 1; n++, id++)
            {
                h.Player.Girls.Add(new Girl { Girlid = id, Modelid = 1 });
                slot.Add(new JsonArray((int)id, 999));
            }
            request.Add(slot);
        }
        await h.DispatchLuaCallAsync(CafeBizFeature.SetWaiterListCmd, request.ToJsonString());
        var reply = Assert.IsType<JsonArray>(Payload(h));
        for (int area = 0; area < 3; area++)
            Assert.Equal(limits[area], reply[area]!.AsArray().Count);
    }
    // ================= 119 制作咖啡 =================

    /// <summary>
    /// ★ 成功回包**绝不能带 `nError`** —— `LuaCall.lua:1727` 是 `if tbParam.nError then`
    /// 真值判断，`nError = 0` 也会被当失败弹提示。
    /// </summary>
    [Fact]
    public async Task MakeCoffee_SuccessHasCoffeeListAndNoNError()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-coffee");
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        int type = table.Coffees.Keys.First();

        await MakeLearnedCoffeeAsync(h,
                                     $"{{\"coffeetype\":{type},\"count\":3}}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.False(p.ContainsKey("nError"), "成功回包带了 nError，客户端会弹错误提示");
        var list = Assert.IsType<JsonArray>(p["coffeelist"]);
        var first = Assert.IsType<JsonObject>(list.First());
        Assert.Equal(type, first["coffeetype"]!.GetValue<int>());
        Assert.Equal(3, first["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task MakeCoffee_LevelOneRejectsSecondKindWithoutCharging()
    {
        using var h = await CafeAsync("cafe-slot-limit", level: 1);
        await MakeLearnedCoffeeAsync(h, "{\"coffeetype\":1,\"count\":2}");
        Assert.Null(Assert.IsType<JsonObject>(Payload(h))["nError"]);
        var beforeMoney = h.Player.GetMoney(CafeBizFeature.GoldMoneyId);
        await MakeLearnedCoffeeAsync(h, "{\"coffeetype\":2,\"count\":3}");
        Assert.NotNull(Assert.IsType<JsonObject>(Payload(h))["nError"]);
        Assert.Equal(beforeMoney, h.Player.GetMoney(CafeBizFeature.GoldMoneyId));
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        var state = CafeFeature.ReadState(h.Player);
        var content = CafeFeature.ReadContent(h.Player, state, table,
            CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
        var coffee = Assert.Single(content.Coffees);
        Assert.Equal(1, coffee.CoffeeType);
        Assert.Equal(2, coffee.Count);
        await MakeLearnedCoffeeAsync(h, "{\"coffeetype\":1,\"count\":1}");
        Assert.Null(Assert.IsType<JsonObject>(Payload(h))["nError"]);
    }

    /// <summary>
    /// ★ `coffeetype` 必须在 `CoffeeList.txt` 里 —— 客户端
    /// `GetCoffeeListConfig(v.coffeetype)` 之后直接取 `template.Time`（`:1738`，无 nil 保护）。
    /// 非法值要**在服务端就拒掉**，并且这一族**失败时才带** `nError`。
    /// </summary>
    [Fact]
    public async Task MakeCoffee_UnknownTypeIsRejectedWithNError()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-coffee-bad");

        await MakeLearnedCoffeeAsync(h,
                                     """{"coffeetype":999999,"count":1}""");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.True(p.ContainsKey("nError"), "非法 coffeetype 必须回 nError，否则客户端 template.Time 会 nil 解引用");
    }

    /// <summary>同种咖啡累加而不是新增一项；种类数不能超过上限（超了客户端点橱窗会抛）。</summary>
    [Fact]
    public async Task MakeCoffee_AccumulatesSameTypeAndCapsKinds()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-coffee-cap");
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        int type = table.Coffees.Keys.First();

        await MakeLearnedCoffeeAsync(h, $"{{\"coffeetype\":{type},\"count\":2}}");
        await MakeLearnedCoffeeAsync(h, $"{{\"coffeetype\":{type},\"count\":5}}");

        var list = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Payload(h))["coffeelist"]);
        var only = Assert.IsType<JsonObject>(Assert.Single(list));
        Assert.Equal(7, only["count"]!.GetValue<int>());            // 2 + 5 累加

        // 灌满种类上限
        foreach (int t in table.Coffees.Keys.Skip(1).Take(CafeBizFeature.MaxCoffeeKinds + 2))
            await MakeLearnedCoffeeAsync(h, $"{{\"coffeetype\":{t},\"count\":1}}");

        var state = CafeFeature.ReadState(h.Player);
        var content = CafeFeature.ReadContent(h.Player, state, table,
                                              CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
        Assert.True(content.Coffees.Count <= CafeBizFeature.MaxCoffeeKinds,
            $"橱窗有 {content.Coffees.Count} 种，超过 {CafeBizFeature.MaxCoffeeKinds} 会让客户端点橱窗时抛");
    }

    // ================= 健壮性 / 常量 =================

    /// <summary>畸形上行一律不抛。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"seatlist":"x"}""")]
    [InlineData("""{"seatlist":[{"position":0}]}""")]
    public async Task AllCommands_MalformedParam_DoNotThrow(string json)
    {
        using var h = await FeatureHarness.CreateAsync($"cafebiz-bad-{json.Length}");
        await h.DispatchLuaCallAsync(CafeBizFeature.GenCustomerCmd, json);
        await h.DispatchLuaCallAsync(CafeBizFeature.UpdateSeatListCmd, json);
        await h.DispatchLuaCallAsync(CafeBizFeature.SetWaiterListCmd, json);
        await MakeLearnedCoffeeAsync(h, json);
    }

    /// <summary>命令号与 `LuaCall.lua:113..119` 的定义一致。</summary>
    [Fact]
    public void CommandIds_MatchLuaCallDefinition()
    {
        Assert.Equal(113, CafeBizFeature.SetWaiterListCmd);
        Assert.Equal(114, CafeBizFeature.UpdateSeatListCmd);
        Assert.Equal(115, CafeBizFeature.GenCustomerCmd);
        Assert.Equal(119, CafeBizFeature.MakeCoffeeCmd);
        Assert.Equal(118, CafeBizFeature.RemoveCoffeeCmd);
    }

    // ================================================================ 118 清理橱窗（第十三轮）

    /// <summary>
    /// ★ 118 成功回**清除之后的完整橱窗**（全量覆盖语义：客户端先
    /// `ClearCoffeeItems()` 再遍历重建），且不带 `nError`。
    /// </summary>
    [Fact]
    public async Task RemoveCoffee_成功回完整橱窗且不带nError()
    {
        // ★★ 必须用高等级咖啡馆：橱窗能放几**种**咖啡由 NCafeLevels.SlotCoffee 决定，
        //    1 级只有 **1 种**（实测日志「橱窗咖啡种类超过上限 1」）⇒ 用 1 级号放两种
        //    会被静默丢弃，用例会红在「清完只剩 0 种」而不是实现有问题。
        using var h = await CafeAsync("cafebiz-rmcoffee", level: 30);
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        var types = table.Coffees.Keys.Take(2).ToList();
        Assert.True(types.Count == 2, "CoffeeList.txt 至少要有两种咖啡，用例前提不成立");

        // 先放两种咖啡进橱窗
        await MakeLearnedCoffeeAsync(h,
                                     $"{{\"coffeetype\":{types[0]},\"count\":3}}");
        await MakeLearnedCoffeeAsync(h,
                                     $"{{\"coffeetype\":{types[1]},\"count\":5}}");

        // 清掉第一种
        await h.DispatchLuaCallAsync(CafeBizFeature.RemoveCoffeeCmd,
                                     $"{{\"coffeetype\":{types[0]}}}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.False(p.ContainsKey("nError"), "成功回包带了 nError，客户端会弹错误提示");
        var list = Assert.IsType<JsonArray>(p["coffeelist"]);

        // 只剩第二种（★ 回的是「清除之后的完整橱窗」，不是被删的那一项）
        Assert.Single(list);
        var only = Assert.IsType<JsonObject>(list[0]);
        Assert.Equal(types[1], only["coffeetype"]!.GetValue<int>());
        Assert.Equal(5, only["count"]!.GetValue<int>());
    }

    /// <summary>
    /// ★ 清空最后一种咖啡后要回**空数组**（不是省略 `coffeelist`）——
    /// 客户端 `for i, v in ipairs(tbParam.coffeelist)` 对 nil 会抛。
    /// </summary>
    [Fact]
    public async Task RemoveCoffee_清完后回空数组而不是省略字段()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-rmcoffee-last");
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        int type = table.Coffees.Keys.First();

        await MakeLearnedCoffeeAsync(h,
                                     $"{{\"coffeetype\":{type},\"count\":1}}");
        await h.DispatchLuaCallAsync(CafeBizFeature.RemoveCoffeeCmd,
                                     $"{{\"coffeetype\":{type}}}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.False(p.ContainsKey("nError"));
        var list = Assert.IsType<JsonArray>(p["coffeelist"]);
        Assert.Empty(list);
    }

    /// <summary>
    /// ★ 橱窗里没有该种咖啡时要拒绝（带 `nError`），**不能静默成功** ——
    /// 静默成功会让客户端以为清掉了、界面与服务端不一致。
    /// </summary>
    [Fact]
    public async Task RemoveCoffee_橱窗没有该咖啡时被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-rmcoffee-none");
        var table = SssjServer.Game.Config.CafeTable.Load(TestPaths.SettingDir);
        int type = table.Coffees.Keys.First();

        await h.DispatchLuaCallAsync(CafeBizFeature.RemoveCoffeeCmd,
                                     $"{{\"coffeetype\":{type}}}");

        var p = Assert.IsType<JsonObject>(Payload(h));
        Assert.True(p.ContainsKey("nError"));
    }

    /// <summary>★ 非法参数（空对象 / 数组）都不能崩，且要有回包。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task RemoveCoffee_非法参数不崩且有回包(string json)
    {
        using var h = await FeatureHarness.CreateAsync("cafebiz-rmcoffee-bad" + json.GetHashCode());

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(CafeBizFeature.RemoveCoffeeCmd, json);

        Assert.Equal(before + 1, h.Outbound.Count);
    }
}
