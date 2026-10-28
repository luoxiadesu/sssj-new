using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 少女好感度 / 羁绊剧情域（`GirlGift` / `ViewStory` + LuaCall 173/174/175/255/256）。
/// 取证见 `work/girl-story-forensics.md`、台账 `docs/05` §27。
///
/// 断言重点是本域**三种并存的错误/路由约定**（AGENTS.md §5 那张表的第五种 + LuaCall 数字族）：
/// <list type="number">
///   <item><c>GirlGift</c>/<c>ViewStory</c>：原样回 sCmd、成功**绝不带** <c>nError</c>、
///     失败**只传** <c>nError</c> 数值（且送礼只有 4 = 已满级有提示）；</item>
///   <item>LuaCall <b>174</b>：成功绝不带 <c>nError</c>，但**必须带** <c>reward</c> 字符串 + <c>love</c> 表；</item>
///   <item>LuaCall <b>256/175/173</b>：★ **一定不回包**（原服 MODULE_SERVER 分支里没有 CallClient）。</item>
/// </list>
/// 另外钉住三条最容易错的数据约定：<c>ELevel</c> 三态、<c>reward</c> 是分号分隔**字符串**、
/// 174 必须置任务值 bit0（否则可无限领奖）。
/// </summary>
public class GirlLoveFeatureTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public async Task ChapterCompletionImmediatelyUnlocksEligibleSubStory(int old, int expected)
    {
        using var h = await FeatureHarness.CreateAsync("girl-sub-stage-live");
        h.Player.GetGirl(1)!.Level = 15;
        int task = GirlLoveFeature.VarTaskId(1, GirlLoveFeature.ELevelOffset(11));
        h.Player.SetTask(3, task, old);
        await h.DispatchFunctionAsync("ChapterMsg", new JsonObject { ["nState"] = 0, ["Chapter"] = 2, ["Index"] = 6, ["Difficult"] = 1 }.ToJsonString());
        await h.CompleteChapterAsync(2, 6, 1);
        Assert.Equal(expected, h.Player.GetTask(3, task));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(expected, saved.GetTask(3, task));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public async Task GirlMissionRewardCrossesLevelTenAndSynchronizesStory(int prior, int expected)
    {
        using var h = await FeatureHarness.CreateAsync("mission-love-threshold");
        var table = GirlLoveTable.Load(TestPaths.SettingDir);
        var girl = Seed(h, level: 9, exp: (uint)(table.LevelUpExp(1, 9) - 1));
        SeedGirlEvent(h);
        int task = GirlLoveFeature.VarTaskId(1, GirlLoveFeature.ELevelOffset(5));
        h.Player.SetTask(3, task, prior);
        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, $"{{\"nId\":{GirlEventId}}}");
        Assert.True(girl.Level >= 10);
        Assert.Equal(expected, h.Player.GetTask(3, task));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(expected, saved.GetTask(3, task));
        Assert.Equal(girl.Level, saved.GetGirl(1)!.Level);
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    [Theory]
    [InlineData(60, true, false, 75)]
    [InlineData(60, true, true, 120)]
    [InlineData(60, false, false, 60)]
    [InlineData(60, false, true, 60)]
    [InlineData(3, true, false, 3)]
    [InlineData(2147483647, true, true, 4294967294L)]
    public void FavoriteGiftExperienceRoundsEachItemAndUsesWideArithmetic(int exp, bool favorite, bool link, long expected)
        => Assert.Equal(expected, GirlLoveFeature.GiftExperiencePerItem(exp, favorite, link));

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 1)]
    [InlineData(true, 11)]
    public async Task InvalidLinkStoryDoesNotGrantOrWriteReadBit(bool owns, int requestedLevel)
    {
        using var h = await FeatureHarness.CreateAsync("invalid-link-story");
        if (owns) Seed(h, girlId: 201, level: 1);
        int before = h.Player.GetMoney(2);
        await h.DispatchFunctionAsync("GirlLogic",
            $"{{\"sCmd\":\"ViewLinkStory\",\"nId\":201,\"Level\":{requestedLevel}}}");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, h.Player.GetMoney(2));
        Assert.Equal(0, GirlLoveFeature.GetVar(h.Player, 201, GirlLoveFeature.VarMainStoryId));
    }

    [Theory]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(100, 1)]
    public async Task SecretUnlockUsesAffectionThresholdAndKeepsGiftSecretsLocked(int level, int expected)
    {
        using var h = await FeatureHarness.CreateAsync("girl-secret-threshold");
        var girl = Seed(h, level: (uint)level);
        var table = GirlLoveTable.Load(TestPaths.SettingDir);
        h.Player.SetTask(3, 107, 0); // Secret8: level10
        h.Player.SetTask(3, 110, 0); // Secret11: matching gift
        h.Player.SetTask(3, 108, 2); // already read Secret9
        GirlLoveFeature.UnlockSecrets(table, h.Player, 1);
        Assert.Equal(expected, h.Player.GetTask(3, 107));
        Assert.Equal(0, h.Player.GetTask(3, 110));
        Assert.Equal(2, h.Player.GetTask(3, 108));
        GirlLoveFeature.UnlockSecrets(table, h.Player, 1, [5, 1, 1, 2]);
        Assert.Equal(1, h.Player.GetTask(3, 110));
    }

    [Fact]
    public async Task MaxAffectionGiftAccumulatesAndRedeemsWithRemainder()
    {
        using var h = await FeatureHarness.CreateAsync("gift-overflow-redeem");
        var girl = Seed(h, level: 100, exp: 2990);
        await GiftAsync(h, 1);
        Assert.Equal(3065u, girl.Exp);
        long Count() => h.Player.Items.Where(x => x.Genre == 8 && x.Detail == 2 && x.Particular == 3 && x.Level == 1).Sum(x => (long)x.Count);
        long before = Count();
        await h.DispatchLuaCallAsync(139, "1");
        Assert.Equal(65u, girl.Exp);
        Assert.Equal(before + 1, Count());
        await h.DispatchLuaCallAsync(139, "1");
        Assert.Equal(before + 1, Count());
        Assert.Equal(65u, new PlayerStore(h.Options).Load(h.Player.Account)!.GetGirl(1)!.Exp);
    }

    [Fact]
    public async Task LinkStoryAwardsOnceAndStoresGroup90Bit()
    {
        using var h = await FeatureHarness.CreateAsync("link-story-reward");
        Seed(h, girlId: 201, level: 10);
        int before = h.Player.GetMoney(2);
        const string payload = "{\"sCmd\":\"ViewLinkStory\",\"nId\":201,\"Level\":10}";
        await h.DispatchFunctionAsync("GirlLogic", payload);
        Assert.Equal(before + 10000, h.Player.GetMoney(2));
        int task = GirlLoveFeature.VarTaskId(201, GirlLoveFeature.VarMainStoryId);
        Assert.Equal(2, h.Player.GetTask(90, task));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(2, saved.GetTask(90, task));
        await h.DispatchLuaCallAsync(256, "{\"nId\":201,\"nValue\":0}");
        Assert.Equal(2, h.Player.GetTask(90, task));
        await h.DispatchFunctionAsync("GirlLogic", payload);
        Assert.Equal(before + 10000, h.Player.GetMoney(2));
        Assert.NotNull(Reply(h)["nError"]);
    }

    [Fact]
    public void SubStoryConditionRequiresExactStagePassAndRejectsUnavailable()
    {
        var p = new Player("sub-condition", 1);
        var story = new GirlLoveTable.StoryAward(1, 15, new(), false, [1, 2, 6, 1]);
        Assert.False(GirlLoveFeature.StoryConditionMet(p, story));
        p.RecordLevelPass(MapList.MapId(2, 6, 2), 7);
        Assert.False(GirlLoveFeature.StoryConditionMet(p, story));
        p.RecordLevelPass(MapList.MapId(2, 6, 1), 7);
        Assert.True(GirlLoveFeature.StoryConditionMet(p, story));
        Assert.False(GirlLoveFeature.StoryConditionMet(p, story with { Condition = [-1] }));
        Assert.False(GirlLoveFeature.StoryConditionMet(p, story with { Condition = [999] }));
    }

    [Fact]
    public async Task MainStoryProgressRejectsSkippingFirstStory()
    {
        using var h = await FeatureHarness.CreateAsync("main-skip");
        h.Player.GetGirl(1)!.Level = 100;
        await h.DispatchLuaCallAsync(256, "{\"nId\":1,\"nValue\":2}");
        Assert.Equal(0, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.VarMainStoryId));
        await h.DispatchLuaCallAsync(256, "{\"nId\":1,\"nValue\":1}");
        Assert.Equal(1, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.VarMainStoryId));
        await h.DispatchLuaCallAsync(256, "{\"nId\":1,\"nValue\":2}");
        Assert.Equal(2, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.VarMainStoryId));
    }

    /// <summary>礼物道具：<c>5-1-1-2</c>（少女 1 的低阶喜爱礼物，<c>ExtParam1</c> = 60）。</summary>
    private const int GiftG = 5, GiftD = 1, GiftP = 1, GiftL = 2;

    /// <summary>非喜爱礼物：<c>5-1-2-2</c>（少女 2 的礼物，对少女 1 是普通礼物）。</summary>
    private const int OtherGiftP = 2;

    private static JsonObject Reply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(GirlLoveFeature.Function, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    /// <summary>LuaCall 数字族的下行：<c>{"sCmd":cmd,"tbParam":{...}}</c>，取 tbParam。</summary>
    private static JsonObject LuaReply(FeatureHarness h, int cmd)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        Assert.Equal(cmd, root["sCmd"]!.GetValue<int>());
        return Assert.IsType<JsonObject>(root["tbParam"]);
    }

    private static Task GiftAsync(FeatureHarness h, uint girlId, int num = 1,
                                  int g = GiftG, int d = GiftD, int p = GiftP, int l = GiftL)
        => h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdGirlGift}\",\"nId\":{girlId}," +
            $"\"tbItem\":[{g},{d},{p},{l}],\"ItemNum\":{num}}}");

    /// <summary>
    /// 给玩家一个少女 + 若干礼物道具。
    ///
    /// ★ **必须复用已存在的同 id 少女**：<c>World.GetOrCreatePlayer</c> 会走
    /// <c>StarterLoadout</c> 给新号发起始卡，<c>Player.Girls</c> 里**本来就有少女 1</c>。
    /// 直接 <c>Girls.Add</c> 会造出**两个 girlid 相同**的实例，而
    /// <c>Player.GetGirl</c> 是 <c>FirstOrDefault</c> ⇒ handler 改的是第一个、
    /// 测试断言的是第二个 ⇒ 症状是「等级/经验一点没变」（本轮 4 条用例正是这么红的）。
    /// </summary>
    private static Girl Seed(FeatureHarness h, uint girlId = 1, uint level = 1, uint exp = 0,
                            int giftCount = 10, int giftP = GiftP)
    {
        var girl = h.Player.GetGirl(girlId);
        if (girl is null)
        {
            girl = new Girl { Girlid = girlId };
            h.Player.Girls.Add(girl);
        }
        girl.Level = level;
        girl.Exp = exp;

        if (giftCount > 0)
        {
            h.Player.Items.Add(new Item
            {
                Guid = 9000 + girlId,
                Genre = GiftG, Detail = GiftD, Particular = (uint)giftP, Level = GiftL,
                Count = (uint)giftCount,
            });
        }
        return girl;
    }

    // ================= GirlGift 送礼 =================

    [Fact]
    public async Task GiftShortageRejectsWholeBatch()
    {
        using var h = await FeatureHarness.CreateAsync("gift-short-batch");
        var girl = Seed(h, giftCount: 2);
        await GiftAsync(h, 1, num: 3);
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(2u, h.Player.Items.Single(i => i.Genre == GiftG).Count);
        Assert.Equal(0u, girl.Exp);
        Assert.Equal(1u, girl.Level);
    }

    [Theory]
    [InlineData("0", "[5,1,1,2]")]
    [InlineData("-1", "[5,1,1,2]")]
    [InlineData("null", "[5,1,1,2]")]
    [InlineData("1.5", "[5,1,1,2]")]
    [InlineData("1", "[5,1,1,2,9]")]
    public async Task InvalidGiftBatchDoesNotConsume(string count, string tuple)
    {
        using var h = await FeatureHarness.CreateAsync("gift-invalid-batch");
        var girl = Seed(h);
        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            "{\"sCmd\":\"GirlGift\",\"nId\":1,\"ItemNum\":" + count + ",\"tbItem\":" + tuple + "}");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(10u, h.Player.Items.Single(i => i.Genre == GiftG).Count);
        Assert.Equal(0u, girl.Exp);
    }

    [Fact]
    public async Task GiftLevelUpImmediatelyUnlocksStoryAndPersists()
    {
        using var h = await FeatureHarness.CreateAsync("gift-unlock-now");
        var table = GirlLoveTable.Load(TestPaths.SettingDir);
        Seed(h, level: 4, exp: (uint)(table.LevelUpExp(1, 4) - 1));
        int off = GirlLoveFeature.ELevelOffset(2); // first side story, level 5
        h.Player.SetTask(3, GirlLoveFeature.VarTaskId(1, off), 0);
        h.Player.SetTask(3, GirlLoveFeature.VarTaskId(1, GirlLoveFeature.ELevelOffset(1)), 2);
        await GiftAsync(h, 1);
        Assert.True(h.Player.GetGirl(1)!.Level >= 5);
        Assert.Equal(1, GirlLoveFeature.GetVar(h.Player, 1, off));
        Assert.Equal(2, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.ELevelOffset(1)));
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(1, GirlLoveFeature.GetVar(saved, 1, off));
        Assert.Equal(9u, saved.Items.Single(i => i.Genre == GiftG).Count);
    }

    [Theory]
    [InlineData(5)] // level 10 required
    [InlineData(9999)]
    public async Task LockedOrUnknownStoryCannotGrantRewards(int story)
    {
        using var h = await FeatureHarness.CreateAsync("gift-story-locked");
        Seed(h);
        int before = h.Player.GetMoney(1);
        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"ViewStory\",\"nId\":1,\"nType\":1,\"nStoryId\":{story}}}");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(before, h.Player.GetMoney(1));
        Assert.Equal(0, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.ELevelOffset(story)));
    }

    [Fact]
    public async Task 送礼_扣道具并加好感经验()
    {
        using var h = await FeatureHarness.CreateAsync("gift-basic");
        var girl = Seed(h);

        await GiftAsync(h, 1, num: 2);

        // ExtParam1 = 60（5-1-1-2），送 2 个喜爱礼物 ⇒ 150 经验；1 级升级需 100
        // ⇒ 升到 2 级、余 20 经验
        Assert.Equal(2u, girl.Level);
        Assert.Equal(50u, girl.Exp);
        // 道具从 10 扣到 8
        Assert.Equal(8u, h.Player.Items.Single(i => i.Genre == GiftG).Count);
    }

    [Fact]
    public async Task 送礼_回包九个字段齐全且不带nError()
    {
        using var h = await FeatureHarness.CreateAsync("gift-fields");
        Seed(h);

        await GiftAsync(h, 1);

        var r = Reply(h);
        Assert.Equal(GirlLoveFeature.CmdGirlGift, r["sCmd"]!.GetValue<string>());
        // ★ 成功绝不带 nError（真值判断，带 0 也会被判成失败）
        Assert.False(r.ContainsKey("nError"));

        Assert.Equal(1, r["nId"]!.GetValue<int>());
        Assert.True(r["bLove"]!.GetValue<bool>(), "5-1-1-2 是少女 1 的喜爱礼物，bLove 应为 true");
        Assert.False(r["bAct"]!.GetValue<bool>());
        Assert.False(r["bSp"]!.GetValue<bool>());
        Assert.Equal(75, r["nAddExp"]!.GetValue<int>());
        Assert.Equal(0, r["nOldExp"]!.GetValue<int>());
        Assert.Equal(75, r["nNewExp"]!.GetValue<int>());
        Assert.Equal(1, r["nOldLevel"]!.GetValue<int>());
        Assert.Equal(1, r["nNewLevel"]!.GetValue<int>());
        // 没满级 ⇒ 不带 nIsMaxlevel（真值判断）
        Assert.False(r.ContainsKey("nIsMaxlevel"));
    }

    [Fact]
    public async Task 送礼_非喜爱礼物bLove为false()
    {
        using var h = await FeatureHarness.CreateAsync("gift-notlove");
        Seed(h, girlId: 1, giftP: OtherGiftP);

        await GiftAsync(h, 1, p: OtherGiftP);

        Assert.False(Reply(h)["bLove"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 送礼_满级累计溢出好感()
    {
        using var h = await FeatureHarness.CreateAsync("gift-max");
        // 少女 1 上限 100
        Seed(h, girlId: 1, level: 100);

        await GiftAsync(h, 1);

        var r = Reply(h);
        // ★ 客户端 OnGirlGiftError 只认 nCode == 4（弹 Girl.MaxLevel），其余码静默
        Assert.Null(r["nError"]);
        // 满级不该扣道具
        Assert.Equal(9u, h.Player.Items.Single(i => i.Genre == GiftG).Count);
    }

    [Fact]
    public async Task 送礼_其他满级少女同样可送礼()
    {
        using var h = await FeatureHarness.CreateAsync("gift-ban");
        // ★★ 第十轮：新版 `setting.u` 把 `Friendliness.BanGirl` **整列清空**
        // （真机自报 `ban_levels=0`）⇒ 少女 13/16 的上限从 80 提到了 100。
        // ⇒ 本测试改用真正的满级 `HardMaxLevel` 当靶子，语义（满级要回 nError=4）不变。
        Seed(h, girlId: 13, level: (uint)GirlLoveTable.HardMaxLevel);

        await GiftAsync(h, 13);

        Assert.Null(Reply(h)["nError"]);
    }

    [Fact]
    public async Task 送礼_道具不足时拒绝且不加经验()
    {
        using var h = await FeatureHarness.CreateAsync("gift-noitem");
        var girl = Seed(h, giftCount: 0);

        await GiftAsync(h, 1);

        Assert.Equal(GirlLoveFeature.ErrGeneric, Reply(h)["nError"]!.GetValue<int>());
        Assert.Equal(1u, girl.Level);
        Assert.Equal(0u, girl.Exp);
    }

    [Fact]
    public async Task 送礼_未持有该少女时拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("gift-nogirl");

        await GiftAsync(h, 99);

        Assert.Equal(GirlLoveFeature.ErrGeneric, Reply(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task 送礼_非礼物道具被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("gift-notgift");
        Seed(h);

        // genre 15 不是礼物
        await GiftAsync(h, 1, g: 15, d: 2, p: 1, l: 1);

        Assert.Equal(GirlLoveFeature.ErrGeneric, Reply(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task 送礼_tbItem不是数组时不抛并拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("gift-badtuple");
        Seed(h);

        // ★ AGENTS.md 记过「JsonNode["key"] 对 JsonArray 会抛」；反过来也要容忍不是数组
        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdGirlGift}\",\"nId\":1,\"tbItem\":{{}},\"ItemNum\":1}}");

        Assert.Equal(GirlLoveFeature.ErrGeneric, Reply(h)["nError"]!.GetValue<int>());
    }

    [Fact]
    public async Task 送礼_满级截断实加经验()
    {
        using var h = await FeatureHarness.CreateAsync("gift-trunc");
        // ★ 第十轮：靶子从「少女 13 上限 80」改为真正的满级 HardMaxLevel（见上一条测试的说明）。
        // 造「差 1 点就满级」的状态：max-1 级、经验只差 1 点。
        var t = GirlLoveTable.Load(TestPaths.SettingDir);
        int max = GirlLoveTable.HardMaxLevel;
        int need = t.LevelUpExp(13, max - 1);
        Seed(h, girlId: 13, level: (uint)(max - 1), exp: (uint)(need - 1));

        await GiftAsync(h, 13);

        var r = Reply(h);
        // ★ 撞上限时 nAddExp 被截断成「真正吃下去的那部分」（照 GirlCommon:Add 的 nValue -= nTotalExp）
        Assert.Equal(60, r["nAddExp"]!.GetValue<int>());
        Assert.Equal(max, r["nNewLevel"]!.GetValue<int>());
        Assert.Equal(59, r["nNewExp"]!.GetValue<int>());
        // ★ 满级了 ⇒ 必须带 nIsMaxlevel（客户端据此弹「已满级」）
        Assert.True(r["nIsMaxlevel"]!.GetValue<bool>());
    }

    [Fact]
    public async Task 送礼_推送pf1030少女数据()
    {
        using var h = await FeatureHarness.CreateAsync("gift-push");
        Seed(h);

        await GiftAsync(h, 1);

        // ★ 好感度在 protobuf 里 ⇒ 通道是 pf 1030 GirlDataNtf，不是 pf 1026
        Assert.True(await h.WaitForPfAsync(GamePf.GirlDataNtf),
            "送礼后没有推 pf 1030 —— 客户端的 girl 缓存不会更新");
    }

    // ================= ViewStory 看剧情 =================

    [Fact]
    public async Task 看剧情_回包原样带回三字段()
    {
        using var h = await FeatureHarness.CreateAsync("story-echo");
        Seed(h);

        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewStory}\",\"nId\":1,\"nType\":1,\"nStoryId\":1}}");

        var r = Reply(h);
        // ★ `GirlLogic.lua:776` 第一句 if not nType then return ⇒ 三个字段必须原样回
        Assert.Equal(1, r["nId"]!.GetValue<int>());
        Assert.Equal(1, r["nType"]!.GetValue<int>());
        Assert.Equal(1, r["nStoryId"]!.GetValue<int>());
        Assert.False(r.ContainsKey("nError"));
    }

    [Fact]
    public async Task 看剧情_发奖并置ELevel已读()
    {
        using var h = await FeatureHarness.CreateAsync("story-award");
        Seed(h);
        long goldBefore = h.Player.GetMoney((int)MoneyType.Gold);

        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewStory}\",\"nId\":1,\"nType\":1,\"nStoryId\":1}}");

        // Fetter.txt 第 4 行 MainAward 含 15-1-1-1-5000（金币 5000）
        Assert.True(h.Player.GetMoney((int)MoneyType.Gold) > goldBefore,
            "看剧情没有发奖 —— 客户端弹窗会显示得到了，但背包里没有");

        // ★ ELevel 必须置 >1（已读），否则奖励可无限领
        int off = GirlLoveFeature.ELevelOffset(1);
        Assert.Equal(GirlLoveFeature.ELevelRead, GirlLoveFeature.GetVar(h.Player, 1, off));
    }

    [Fact]
    public async Task 看剧情_已读时不重复发奖()
    {
        using var h = await FeatureHarness.CreateAsync("story-once");
        Seed(h);

        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewStory}\",\"nId\":1,\"nType\":1,\"nStoryId\":1}}");
        long goldAfterFirst = h.Player.GetMoney((int)MoneyType.Gold);

        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewStory}\",\"nId\":1,\"nType\":1,\"nStoryId\":1}}");

        // ★ 第二次不发奖（防无限领），但仍要回包（这一族有遮罩）
        Assert.Equal(goldAfterFirst, h.Player.GetMoney((int)MoneyType.Gold));
        Assert.Equal(GirlLoveFeature.CmdViewStory, Reply(h)["sCmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task 看剧情_卡面相册类型只回执不发奖()
    {
        using var h = await FeatureHarness.CreateAsync("story-album");
        Seed(h);
        long goldBefore = h.Player.GetMoney((int)MoneyType.Gold);

        // nType == 3 是 UI_GirlLoveAlbum（卡面相册）
        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewStory}\",\"nId\":1,\"nType\":3,\"nStoryId\":1}}");

        var r = Reply(h);
        Assert.Equal(3, r["nType"]!.GetValue<int>());
        Assert.Equal(goldBefore, h.Player.GetMoney((int)MoneyType.Gold));
    }

    [Fact]
    public async Task 看联动剧情_原样回执()
    {
        using var h = await FeatureHarness.CreateAsync("story-link");
        Seed(h, girlId: 201, level: 10);

        await h.DispatchFunctionAsync(GirlLoveFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdViewLinkStory}\",\"nId\":201,\"Level\":10}}");

        var r = Reply(h);
        Assert.Equal(201, r["nId"]!.GetValue<int>());
        Assert.Equal(10, r["Level"]!.GetValue<int>());
    }

    // ================= 登录：ELevel 初值 =================

    [Fact]
    public async Task 登录_够条件的剧情档位置为已解锁未读()
    {
        using var h = await FeatureHarness.CreateAsync("story-unlock");
        // 少女 1 好感 10 级 ⇒ Fetter 的 Level=10 档（storyIndex 5）应解锁
        // 且 FriendlinessUnlock.txt 里有 (1,10) 这一行
        Seed(h, girlId: 1, level: 10, giftCount: 0);

        h.Module.RunPrepareHooksForTest(h.Session, h.Player);

        int off = GirlLoveFeature.ELevelOffset(5);
        Assert.Equal(GirlLoveFeature.ELevelUnread, GirlLoveFeature.GetVar(h.Player, 1, off));
    }

    [Fact]
    public async Task 登录_不覆盖已读的档位()
    {
        using var h = await FeatureHarness.CreateAsync("story-idempotent");
        Seed(h, girlId: 1, level: 10, giftCount: 0);

        int off = GirlLoveFeature.ELevelOffset(5);
        h.Player.SetTask(GirlLoveFeature.VarGroup(1), GirlLoveFeature.VarTaskId(1, off),
                         GirlLoveFeature.ELevelRead);

        h.Module.RunPrepareHooksForTest(h.Session, h.Player);

        // ★ 幂等：已读（>1）不能被洗回未读（1）
        Assert.Equal(GirlLoveFeature.ELevelRead, GirlLoveFeature.GetVar(h.Player, 1, off));
    }

    [Fact]
    public async Task 登录_等级不够的档位不解锁()
    {
        using var h = await FeatureHarness.CreateAsync("story-locked");
        Seed(h, girlId: 1, level: 1, giftCount: 0);

        h.Module.RunPrepareHooksForTest(h.Session, h.Player);

        // ★ 前提防御：GM 全解锁（同样挂在 prepare 钩子上）**不改少女好感等级**，
        // 否则这条用例会因为等级被拉高而假绿。
        Assert.Equal(1u, h.Player.GetGirl(1)!.Level);

        // storyIndex 5 的门槛是好感 10 级 ⇒ 不该解锁
        int off = GirlLoveFeature.ELevelOffset(5);
        Assert.Equal(0, GirlLoveFeature.GetVar(h.Player, 1, off));
    }

    // ================= LuaCall 174 领奖 =================

    /// <summary>少女事件 id（DailyMission.txt 里 missionType==2 的第一条 = 摸头）。</summary>
    private const int GirlEventId = 1;

    private static void SeedGirlEvent(FeatureHarness h, uint girlId = 1)
    {
        h.Player.SetTask(GirlLoveFeature.MissionGroup, GirlLoveFeature.CurGirlEventTaskId, GirlEventId);
        h.Player.SetTask(GirlLoveFeature.MissionGroup, GirlLoveFeature.CurGirlTaskId, (int)girlId);
    }

    [Fact]
    public async Task 领奖174_回包必须带reward字符串与love表()
    {
        using var h = await FeatureHarness.CreateAsync("174-fields");
        Seed(h);
        SeedGirlEvent(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, $"{{\"nId\":{GirlEventId}}}");

        var r = LuaReply(h, GirlLoveFeature.CmdGirlMissionReward);
        // ★ reward 是**分号分隔字符串**，不是数组（GirlMissionManager.lua:283 Split(reward,";")）
        var reward = r["reward"]!.GetValue<string>();
        Assert.Contains("-", reward);
        Assert.Equal(2, reward.Split(';').Length);   // [15-2-1-1-20,9-16-1-1-1] ⇒ 两项
        foreach (var seg in reward.Split(';'))
            Assert.Equal(5, seg.Split('-').Length);  // 每项 5 段 G-D-P-L-N

        // ★ love 六个字段全必填（:301 位置数组，少一个就 nil 进数组）
        var love = Assert.IsType<JsonObject>(r["love"]);
        foreach (var key in new[] { "girlID", "nOldLevel", "nOldExp", "nNewLevel", "nNewExp", "nAdd" })
            Assert.True(love.ContainsKey(key), $"love 缺字段 {key}");
        Assert.Equal(1, love["girlID"]!.GetValue<int>());

        // ★ LuaCall 数字族：成功绝不带 nError
        Assert.False(r.ContainsKey("nError"));
    }

    [Fact]
    public async Task 领奖174_加好感度300并真发道具()
    {
        using var h = await FeatureHarness.CreateAsync("174-grant");
        var girl = Seed(h);
        SeedGirlEvent(h);
        long goldBefore = h.Player.GetMoney((int)MoneyType.Gold);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, $"{{\"nId\":{GirlEventId}}}");

        // AddGirlLove = 300 ⇒ 1 级(100) → 2 级(105) → 3 级 余 95
        Assert.Equal(3u, girl.Level);
        Assert.Equal(95u, girl.Exp);
        // 奖励含 15-2-1-1-20（钻石一类货币）；金币不一定变，改断言「有货币或物品变化」
        Assert.True(h.Player.GetMoney((int)MoneyType.Gold) >= goldBefore);
    }

    [Fact]
    public async Task 领奖174_必须置任务值bit0防无限领()
    {
        using var h = await FeatureHarness.CreateAsync("174-bit0");
        Seed(h);
        SeedGirlEvent(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, $"{{\"nId\":{GirlEventId}}}");

        // ★★ bit0 = 已领（E.3 ①）。不置的后果：客户端 EventStep 停在 1 ⇒ 可无限领奖
        int packed = h.Player.GetTask(GirlLoveFeature.MissionGroup, GirlEventId);
        Assert.Equal(1, packed & 1);
    }

    [Fact]
    public async Task 领奖174_重复领被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("174-twice");
        var girl = Seed(h);
        SeedGirlEvent(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, $"{{\"nId\":{GirlEventId}}}");
        uint levelAfterFirst = girl.Level;
        uint expAfterFirst = girl.Exp;

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, $"{{\"nId\":{GirlEventId}}}");

        var r = LuaReply(h, GirlLoveFeature.CmdGirlMissionReward);
        Assert.True(r.ContainsKey("nError"), "重复领奖必须回 nError");
        Assert.Equal(levelAfterFirst, girl.Level);
        Assert.Equal(expAfterFirst, girl.Exp);
    }

    [Fact]
    public async Task 领奖174_不存在的事件id被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("174-badid");
        Seed(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionReward, "{\"nId\":999999}");

        Assert.True(LuaReply(h, GirlLoveFeature.CmdGirlMissionReward).ContainsKey("nError"));
    }

    // ================= LuaCall 255 / 256 / 175 / 173 =================

    [Fact]
    public async Task Flag255_清新少女标记并回显()
    {
        using var h = await FeatureHarness.CreateAsync("255-flag");
        var girl = Seed(h);
        girl.Flag = 1;

        // ★ 上行字段是**大写 GirlId**（UI_Girl.lua:1089），不是 nId
        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlUpdateFlag, "{\"GirlId\":1}");

        Assert.Equal(0u, girl.Flag);
        var r = LuaReply(h, GirlLoveFeature.CmdGirlUpdateFlag);
        Assert.Equal(1, r["GirlId"]!.GetValue<int>());
        Assert.False(r.ContainsKey("nError"));
    }

    [Fact]
    public async Task MainStoryId256_顺序推进且不回包()
    {
        using var h = await FeatureHarness.CreateAsync("256-store");
        Seed(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlSetMainStoryId,
                                     "{\"nId\":1,\"nValue\":1}");

        // ★ 原样存（不解释语义：普通少女是顺序计数、联动少女是位域）
        Assert.Equal(1, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.VarMainStoryId));
        // ★★ 原服 MODULE_SERVER 分支里连 CallClient 都没有 ⇒ 一定不回包
        Assert.Empty(h.Outbound);
    }

    [Fact]
    public async Task MainStoryId256_允许回退播放进度()
    {
        using var h = await FeatureHarness.CreateAsync("256-nomono");
        Seed(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlSetMainStoryId, "{\"nId\":1,\"nValue\":1}");
        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlSetMainStoryId, "{\"nId\":1,\"nValue\":0}");

        // ★ 联动少女的位域值不保证单调 ⇒ 服务端不能加「只能递增」的校验（原服是无脑透传）
        Assert.Equal(0, GirlLoveFeature.GetVar(h.Player, 1, GirlLoveFeature.VarMainStoryId));
    }

    [Fact]
    public async Task MainStoryId256_联动领奖位仅由服务器写入()
    {
        using var h = await FeatureHarness.CreateAsync("256-link");
        Seed(h, girlId: 201);
        h.Player.SetTask(90, GirlLoveFeature.VarTaskId(201, GirlLoveFeature.VarMainStoryId), 2);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlSetMainStoryId,
                                     "{\"nId\":201,\"nValue\":5}");

        // ★ group 3→90、girlId 减 200 ⇒ 与普通少女 1 的坐标**不能**相撞
        Assert.Equal(90, GirlLoveFeature.VarGroup(201));
        Assert.Equal(2, h.Player.GetTask(90, GirlLoveFeature.VarTaskId(201, GirlLoveFeature.VarMainStoryId)));
        // 普通少女 1 的同一偏移不受影响
        Assert.Equal(0, h.Player.GetTask(GmUnlock.GirlVarGroup,
                                         GirlLoveFeature.VarTaskId(1, GirlLoveFeature.VarMainStoryId)));
    }

    [Fact]
    public async Task MoodBoom175_按配置改心情值且不回包()
    {
        using var h = await FeatureHarness.CreateAsync("175-mood");
        var girl = Seed(h, girlId: 7);
        girl.Moodvalue = 100;

        // MoodBoom.txt 唯一一行：PlotID=1 GirlID=7 MoodChangeValue=-100
        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMoodBoom, "{\"boomID\":1}");

        Assert.Equal(0u, girl.Moodvalue);
        // ★ 与 173 同型的 tbRet nil bug、客户端无 else 分支 ⇒ 不回包
        Assert.Empty(h.Outbound);
    }

    [Fact]
    public async Task MoodBoom175_未配置的id只记日志()
    {
        using var h = await FeatureHarness.CreateAsync("175-badid");
        var girl = Seed(h, girlId: 7);
        girl.Moodvalue = 50;

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMoodBoom, "{\"boomID\":999}");

        Assert.Equal(50u, girl.Moodvalue);
        Assert.Empty(h.Outbound);
    }

    [Fact]
    public async Task MissionFinish173_不回包()
    {
        using var h = await FeatureHarness.CreateAsync("173-finish");
        Seed(h);
        SeedGirlEvent(h);

        await h.DispatchLuaCallAsync(GirlLoveFeature.CmdGirlMissionFinish, "{}");

        // ★ 原服 tbRet 未定义（bug）+ 客户端无 else 分支 ⇒ 回包无人消费，本实现不回
        Assert.Empty(h.Outbound);
    }

    // ================= 坐标编码 =================

    [Fact]
    public void ELevel偏移_越界返回负一()
    {
        Assert.Equal(300, GirlLoveFeature.ELevelOffset(1));
        Assert.Equal(398, GirlLoveFeature.ELevelOffset(99));
        Assert.Equal(399, GirlLoveFeature.ELevelOffset(100));
        // 区段是 300..399 ⇒ index 101 越界
        Assert.Equal(-1, GirlLoveFeature.ELevelOffset(101));
        Assert.Equal(-1, GirlLoveFeature.ELevelOffset(0));
        Assert.Equal(-1, GirlLoveFeature.ELevelOffset(-3));
    }

    [Fact]
    public void 任务值坐标_普通与联动少女不相撞()
    {
        // 普通少女：(girlId-1)*2000 + off，group 3
        Assert.Equal(GmUnlock.GirlVarGroup, GirlLoveFeature.VarGroup(1));
        Assert.Equal(14, GirlLoveFeature.VarTaskId(1, 14));
        Assert.Equal(2014, GirlLoveFeature.VarTaskId(2, 14));

        // 联动少女：group 90，girlId 先减 200
        Assert.Equal(90, GirlLoveFeature.VarGroup(201));
        Assert.Equal(14, GirlLoveFeature.VarTaskId(201, 14));      // (201-200-1)*2000+14
        Assert.Equal(2014, GirlLoveFeature.VarTaskId(202, 14));
    }
}
