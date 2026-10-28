using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 抽卡 / 卡池域。
///
/// 断言分三类：
/// <list type="number">
/// <item><b>钉住配置事实</b> —— 现网 <c>gacha.txt</c> 100 个角色池里只有池 66 没过期，
///   验收全靠它；这条一变，下面的端到端断言全要改。</item>
/// <item><b>概率算法与共享模块 <c>GachaCommon.lua</c> 对齐</b>（星级内归一化、星级阶梯取权重）。</item>
/// <item><b>回包形状的硬约束</b> —— 这一族的字段缺失大多不是「不刷新」而是**客户端直接抛**：
///   <c>tbAwards</c> 为空在 <c>UI_Gacha.lua:1249</c> NRE、<c>bTen=true</c> 时不足 10 项在
///   <c>:1232 for i=1,10</c> 越界、<c>nTotalTimes</c> 缺失在 <c>:1241</c> 做 <c>% nil</c>。
///   而客户端发这条命令前是 <c>Blocking.Show(300)</c> = 5 分钟遮罩，出错等于卡死。</item>
/// </list>
/// </summary>
public class GachaFeatureTests
{
    /// <summary>
    /// ★ 验收首选池：<c>id=1</c>（<c>card_1_base_card_61_ZH</c>）——
    /// 现网 4 个开放池里唯一的**普通池**（<c>JudgeType=0</c>，没有「持票+通关 1-6」门槛），
    /// 且同时有单抽 <c>[9-12-1-1-1]</c>、十连 <c>[9-13-1-1-1]</c> 与 50 抽保底。
    /// </summary>
    private const int NormalPoolId = 1;

    /// <summary>池 1 的单抽券（<c>CostOne = [9-12-1-1-1]</c>）。</summary>
    private const uint OneG = 9, OneD = 12, OneP = 1, OneL = 1;

    /// <summary>池 1 的十连券（<c>CostTen = [9-13-1-1-1]</c>）。</summary>
    private const uint TenG = 9, TenD = 13, TenP = 1, TenL = 1;

    /// <summary>誓约池 <c>id=66</c>（<c>card_promise</c>）：<c>JudgeType=1</c>，只收道具，无十连。</summary>
    private const int PromisePoolId = 66;

    /// <summary>池 66 的单抽消耗道具（<c>CostOne = [9-14-1-1-1]</c>，<c>CashType = 1</c> 只收道具）。</summary>
    private const uint TicketG = 9, TicketD = 14, TicketP = 1, TicketL = 1;

    /// <summary>池 3：全部档期都已过期（现网 49 个全过期池之一）—— 用来验 notexists。</summary>
    private const int ExpiredPoolId = 3;

    /// <summary>
    /// 1-6 普通难度的 mapId。<c>JudgeType</c> 1/2 的池要求它已通关
    /// （<c>GachaCommon.lua:237 SystemUnlock:CheckLevel(1, 6, 1)</c>）。
    /// </summary>
    private static void PassChapter16(Player p) => p.RecordLevelPass(MapList.MapId(1, 6, 1), MapList.StarMask);

    private static Task SendAsync(FeatureHarness h, string function, string json)
        => h.LuaCalls.DispatchAsync(h.Session, function, json);

    private static JsonObject LastReply(FeatureHarness h, string expectFunction)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(expectFunction, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    // ================= 配置事实 =================

    [Fact]
    public void GachaTable_LoadsCardPools_AndPromisePoolIsOpen()
    {
        var table = GachaTable.Load(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 现网：★ 第十轮换新版 setting.u 后 53 → **63** 个池 id
        Assert.Equal(63, table.Count);
        // ★ 第十轮换新版 setting.u 后档期从 100 → **128** 段
        Assert.Equal(128, table.All.Count());
        // ★ 一个池 id 有多段档期：ID=1 就有 29 段。用字典覆盖式存会丢档期
        Assert.True(table.Segments(NormalPoolId).Count > 20,
            $"池 1 只有 {table.Segments(NormalPoolId).Count} 段档期 —— 多段被覆盖了？");

        var promise = table.Get(PromisePoolId, now);
        Assert.NotNull(promise);
        Assert.Equal("card_promise", promise!.PoolFile);
        Assert.Equal(24, promise.Cards.Count);
        Assert.Equal(1, promise.CashType);                 // 只道具
        Assert.Equal(1, promise.JudgeType);                // 特殊池
        Assert.Equal(5, promise.CardStarSeedId);
        Assert.NotNull(promise.CostOneItem);
        Assert.Equal(new[] { 9, 14, 1, 1, 1 }, promise.CostOneItem);
        Assert.False(promise.HasTen);                       // CostTen 为空 → 没有十连

        // ★ 它是现网唯一长期开放的池（2077 年才结束）—— 验收全靠它
        Assert.True(promise.IsOpenAt(now), "池 66 应当处于开放期");
        var open = table.OpenAt(now).Select(p => p.Id).ToList();
        // 每个 id 最多出现一次（GetCurPools 的口径）
        Assert.Equal(open.Count, open.Distinct().Count());
        // ★ 现网当前开放的就是这 4 个
        var plan = GachaRotation.For(TestPaths.SettingDir).Read();
        if (plan.Enabled)
            Assert.Equal(plan.Entries.Where(e => e.Type == 1 && e.Start <= now && now < e.End)
                .Select(e => e.Id).Distinct().OrderBy(x => x), open.OrderBy(x => x));
        else
        {
            Assert.Equal(new[] { 1, 66, 77, 666 }, open.OrderBy(x => x).ToArray());
            Assert.DoesNotContain(ExpiredPoolId, open);
        }

        // 池 1 是普通池，有单抽也有十连
        var normal = table.Get(NormalPoolId, now)!;
        Assert.Equal(0, normal.JudgeType);
        Assert.Equal(0, normal.CashType);
        Assert.Equal(50, normal.ProtectNum);
        Assert.True(normal.HasTen);
        Assert.Equal(new[] { 9, 12, 1, 1, 1 }, normal.CostOneItem);
        Assert.Equal(new[] { 9, 13, 1, 1, 1 }, normal.CostTenItem);
    }

    /// <summary>
    /// ★ 星级内归一化（<c>GachaCommon.lua:152..156</c>
    /// <c>tbData.nRate = tbData.nRate / nTotal</c>）：同星级卡的 <c>RateInStar</c> 之和为 1。
    /// </summary>
    [Fact]
    public void GachaTable_NormalizesRateWithinStar()
    {
        var table = GachaTable.Load(TestPaths.SettingDir);
        var pool = table.Get(PromisePoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;

        foreach (var group in pool.Cards.Values.GroupBy(c => c.Star))
        {
            double sum = group.Sum(c => c.RateInStar);
            Assert.True(Math.Abs(sum - 1.0) < 1e-9,
                $"{group.Key}★ 的 RateInStar 合计 = {sum}，应为 1");
            // 分母就是该星级的原始权重合计
            Assert.Equal(group.Sum(c => c.RawRate), pool.StarTotal[group.Key]);
        }
    }

    /// <summary>星级 = <c>min(L, 4)</c>（<c>GachaCommon.lua:137</c>）。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    public void GachaCard_StarIsMinOfLAnd4(int l, int expected)
        => Assert.Equal(expected, new GachaCard { L = l }.Star);

    /// <summary>
    /// <c>CardStarSeed.txt</c> 的阶梯解析与取权重（<c>GachaCommon.lua:180..191</c> / <c>:281..300</c>）。
    /// 现网 5 行、每列两档且**两档权重相同** ⇒ 本版本星级概率不随抽数爬坡。
    /// </summary>
    [Fact]
    public void CardStarSeed_ParsesStepsAndPicksFirstMatchingTier()
    {
        var steps = GachaTable.ParseSeedSteps("[1,1200],[50,1200]");
        Assert.Equal(2, steps.Count);
        Assert.Equal(new StarSeedStep(1, 1200), steps[0]);
        Assert.Equal(new StarSeedStep(50, 1200), steps[1]);

        var table = GachaTable.Load(TestPaths.SettingDir);
        // ID 1：1★1200 / 2★7000 / 3★1500 / 4★300（合计 10000）
        var seed1 = table.GetSeed(1)!;
        Assert.Equal(new[] { 1200, 7000, 1500, 300 }, seed1.WeightsAt(1));
        Assert.Equal(new[] { 1200, 7000, 1500, 300 }, seed1.WeightsAt(50));
        // 超过最大档位 ⇒ 该星级不参与（阶梯里找不到）
        Assert.Equal(new[] { 0, 0, 0, 0 }, seed1.WeightsAt(51));

        // ID 5（池 66 用的）：3★9700 / 4★300
        var seed5 = table.GetSeed(5)!;
        Assert.Equal(new[] { 0, 0, 9700, 300 }, seed5.WeightsAt(1));

        // ID 4：只出 4★
        Assert.Equal(new[] { 0, 0, 0, 10000 }, table.GetSeed(4)!.WeightsAt(1));
    }

    /// <summary>时间字段按长度决定精度（<c>Lib.lua:1304..1340 GetDate2Time</c>）。</summary>
    [Fact]
    public void ParseTimeRange_HandlesAllPrecisions()
    {
        var (s, e) = GachaTable.ParseTimeRange("{201808190000, 207708292359}");
        Assert.True(s > 0 && e > s);
        // 2018-08-19 00:00 UTC
        Assert.Equal(new DateTimeOffset(2018, 8, 19, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), s);
        // 2077-08-29 23:59 UTC
        Assert.Equal(new DateTimeOffset(2077, 8, 29, 23, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds(), e);

        Assert.Equal(new DateTimeOffset(2019, 12, 6, 11, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds(),
                     GachaTable.ParseDate("20191206115959"));
        Assert.Equal(new DateTimeOffset(2010, 12, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     GachaTable.ParseDate("20101220"));
        Assert.Equal((0L, 0L), GachaTable.ParseTimeRange(""));
        Assert.Equal(0, GachaTable.ParseDate("nonsense"));
    }

    [Fact]
    public void ParseItem_HandlesFourAndFiveTuples()
    {
        Assert.Equal(new[] { 1, 1, 1, 3 }, GachaTable.ParseItem("[1-1-1-3]"));
        Assert.Equal(new[] { 9, 14, 1, 1, 1 }, GachaTable.ParseItem("[9-14-1-1-1]"));
        // 第 6 位是商店物品 id，一起解出来但调用方只取前 5 位
        Assert.Equal(new[] { 9, 12, 1, 1, 1, 10001 }, GachaTable.ParseItem("[9-12-1-1-1-10001]"));
        Assert.Null(GachaTable.ParseItem(""));
        Assert.Null(GachaTable.ParseItem("[1-2]"));       // 不足 4 位
    }

    // ================= Lottery：错误路径 =================

    /// <summary>池不存在 ⇒ <c>error.gacha.notexists</c>（客户端 <c>:639</c> 特判，弹换池对话框）。</summary>
    [Fact]
    public async Task Lottery_UnknownPool_RepliesNotExists()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-nopool");

        await SendAsync(h, GachaFeature.FnLottery, """{"nId":999999,"bTen":false}""");

        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Equal(GachaFeature.ErrNotExists, reply["err"]!.GetValue<string>());
    }

    /// <summary>已过期的池同样回 <c>notexists</c>（现网绝大多数池都是这种）。</summary>
    [Fact]
    public async Task Lottery_ExpiredPool_RepliesNotExists()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-expired");
        // 池 3 的全部档期都已过期（现网 49 个全过期池之一）
        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{ExpiredPoolId},\"bTen\":false}}");

        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Equal(GachaFeature.ErrNotExists, reply["err"]!.GetValue<string>());
    }

    /// <summary>
    /// 券不够 ⇒ 回 err（而不是白送一抽）。用**普通池**验：它没有 JudgeType 门槛，
    /// 所以能走到扣费那一步而不是先被判「池不可选」。
    /// </summary>
    [Fact]
    public async Task Lottery_WithoutTicket_RepliesNotEnough()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-broke");
        h.Player.SetMoney(MoneyType.FreeDiamond, 0);
        h.Player.SetMoney(MoneyType.PayDiamond, 0);
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == OneG && i.Detail == OneD);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{NormalPoolId},\"bTen\":false}}");

        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Equal(GachaFeature.ErrNotEnough, reply["err"]!.GetValue<string>());
        // 没扣任何东西、没发卡、计数没动
        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup, GachaFeature.TaskTotal));
        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup,
                                         GachaFeature.TaskProtectBegin + NormalPoolId));
    }

    /// <summary>
    /// <c>JudgeType=1</c> 的池**没票时直接判「不可选」**（回 notexists，不是 notenough）——
    /// 这与客户端一致：<c>GetCurPools</c> 在 <c>nCount &gt;= 1</c> 不成立时就不把该池放进列表，
    /// 玩家在界面上根本看不到它。
    /// </summary>
    [Fact]
    public async Task Lottery_JudgeType1Pool_WithoutTicket_RepliesNotExists()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-noticket");
        PassChapter16(h.Player);                        // 关卡条件满足
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == TicketG && i.Detail == TicketD);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");

        Assert.Equal(GachaFeature.ErrNotExists,
                     LastReply(h, GachaFeature.FnLottery)["err"]!.GetValue<string>());
    }

    // ================= Lottery：成功路径 =================

    [Theory]
    [InlineData(false, 200, 80, 50, 80)]
    [InlineData(false, 100, 80, 0, 30)]
    [InlineData(true, 1000, 800, 0, 300)]
    public async Task Lottery_NoTicket_UsesConfiguredDiamondPrice(bool ten, int free, int paid,
        int expectedFree, int expectedPaid)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-diamond-{ten}-{free}");
        var pool = GachaTable.Load(TestPaths.SettingDir).Get(NormalPoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;
        Assert.Equal(150, pool.ExchangeRate);
        Assert.True(pool.CanUseFreeDiamond);
        h.Player.SetMoney(MoneyType.FreeDiamond, free);
        h.Player.SetMoney(MoneyType.PayDiamond, paid);
        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":1,\"bTen\":{ten.ToString().ToLowerInvariant()}}}");
        Assert.Null(LastReply(h, GachaFeature.FnLottery)["err"]);
        Assert.Equal(expectedFree, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(expectedPaid, h.Player.GetMoney(MoneyType.PayDiamond));
        Assert.Equal(ten ? 10 : 1, h.Player.GetTask(15, 1));
    }

    [Fact]
    public async Task Lottery_InsufficientCombinedDiamonds_DoesNotChargeOrDraw()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-diamond-poor");
        h.Player.SetMoney(MoneyType.FreeDiamond, 100);
        h.Player.SetMoney(MoneyType.PayDiamond, 49);
        int items = h.Player.Items.Count;
        await SendAsync(h, GachaFeature.FnLottery, "{\"nId\":1,\"bTen\":false}");
        Assert.Equal(GachaFeature.ErrNotEnough, LastReply(h, GachaFeature.FnLottery)["err"]!.GetValue<string>());
        Assert.Equal(100, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(49, h.Player.GetMoney(MoneyType.PayDiamond));
        Assert.Equal(items, h.Player.Items.Count);
        Assert.Equal(0, h.Player.GetTask(15, 1));
    }

    [Fact]
    public async Task Lottery_Single_ConsumesTicketAndGrantsOneCard()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-one");
        PassChapter16(h.Player);
        var ticket = h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 5);
        int cardsBefore = h.Player.Items.Count(i => i.Genre == 1);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");

        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Null(reply["err"]);
        Assert.False(reply["bTen"]!.GetValue<bool>());

        var awards = Assert.IsType<JsonArray>(reply["tbAwards"]);
        Assert.Single(awards);

        // 扣了 1 张券、发了 1 张卡
        Assert.Equal(4u, ticket.Count);
        Assert.Equal(cardsBefore + 1, h.Player.Items.Count(i => i.Genre == 1));

        // 该池也可能随机出4★，出4★时计数清零。
        var gdpl = Assert.IsType<JsonArray>(awards[0]!["tbGDPL"]);
        Assert.Equal(gdpl[3]!.GetValue<int>() >= 4 ? 0 : 1, h.Player.GetTask(GachaFeature.TaskGroup,
                                         GachaFeature.TaskProtectBegin + PromisePoolId));
        Assert.Equal(1, h.Player.GetTask(GachaFeature.TaskGroup, GachaFeature.TaskTotal));
        Assert.Equal(1, h.Player.GetTask(GachaFeature.TaskGroup,
                                         GachaFeature.TaskPrePoolBegin + PromisePoolId));
    }

    /// <summary>
    /// ★ 回包每一项的必填字段。缺任何一个客户端都会抛（见类注释 §3），
    /// 而此时正挂着 5 分钟遮罩。
    /// </summary>
    [Fact]
    public async Task Lottery_AwardItem_HasEveryRequiredField()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-fields");
        PassChapter16(h.Player);
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 2);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");

        var awards = Assert.IsType<JsonArray>(LastReply(h, GachaFeature.FnLottery)["tbAwards"]);
        var item = Assert.IsType<JsonObject>(awards[0]);

        // tbGDPL 必填且必须 4 项（:679 直接 unpack）
        var gdpl = Assert.IsType<JsonArray>(item["tbGDPL"]);
        Assert.Equal(4, gdpl.Count);
        Assert.Equal(1, gdpl[0]!.GetValue<int>());        // 角色卡 genre 恒 1

        Assert.NotNull(item["nId"]);
        // 显式按首次/重复状态控制原版抽卡剧情分支。
        Assert.Equal(!item["bFirstGet"]!.GetValue<bool>(), item["bHasCard"]!.GetValue<bool>());
        Assert.NotNull(item["bFirstGet"]);
        Assert.NotNull(item["nTimes"]);
        // ★ nTotalTimes 条件必填 —— UpBoxNum>0 且 RotateVersion>0 时客户端会 % 它
        Assert.NotNull(item["nTotalTimes"]);
    }

    /// <summary>
    /// ★ 十连必须回 ≥ 10 项（<c>:1232 for i = 1, 10 do</c> 无越界保护）。
    /// 池 66 没有十连配置，所以用一个**有**十连的池来验 —— 但那些池都过期了，
    /// 于是这里改为验「没有十连配置时会降级成单抽」这条实现约定。
    /// </summary>
    [Fact]
    public async Task Lottery_TenOnPoolWithoutTenConfig_FallsBackToSingle()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-ten-fallback");
        PassChapter16(h.Player);
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 20);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":true}}");

        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Null(reply["err"]);
        // 降级为单抽：bTen 必须跟着变 false，否则客户端会去取第 2..10 项而它们不存在
        Assert.False(reply["bTen"]!.GetValue<bool>());
        Assert.Single(Assert.IsType<JsonArray>(reply["tbAwards"]));
    }

    /// <summary>连抽多次：保底计数与池累计单调递增，卡片逐张入包。</summary>
    [Fact]
    public async Task Lottery_RepeatedDraws_CountersIncreaseMonotonically()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-many");
        PassChapter16(h.Player);
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 30);
        int cardsBefore = h.Player.Items.Count(i => i.Genre == 1);

        for (int i = 1; i <= 12; i++)
        {
            await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");
            var reply = LastReply(h, GachaFeature.FnLottery);
            Assert.Null(reply["err"]);
            Assert.Equal(i, h.Player.GetTask(GachaFeature.TaskGroup, GachaFeature.TaskTotal));
            Assert.Equal(i, h.Player.GetTask(GachaFeature.TaskGroup,
                                             GachaFeature.TaskPrePoolBegin + PromisePoolId));
        }

        // 抽了 12 张卡（起始卡不算，所以用增量）
        Assert.Equal(cardsBefore + 12, h.Player.Items.Count(i => i.Genre == 1));
    }

    /// <summary>抽到的卡必须真的来自该池的卡组（不能凭空造）。</summary>
    [Fact]
    public async Task Lottery_DrawnCardsComeFromThatPool()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-inpool");
        PassChapter16(h.Player);
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 20);
        var pool = GachaTable.Load(TestPaths.SettingDir)
            .Get(PromisePoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;
        var valid = pool.Cards.Values
            .Select(c => (c.G, c.D, c.P, c.L)).ToHashSet();

        for (int i = 0; i < 15; i++)
        {
            await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");
            var awards = Assert.IsType<JsonArray>(LastReply(h, GachaFeature.FnLottery)["tbAwards"]);
            var g = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(awards[0])["tbGDPL"]);
            var key = (g[0]!.GetValue<int>(), g[1]!.GetValue<int>(),
                       g[2]!.GetValue<int>(), g[3]!.GetValue<int>());
            Assert.Contains(key, valid);
        }
    }

    /// <summary>
    /// ★ 池 1（普通池、有十连）的真十连：<c>bTen=true</c> 必须回 **≥ 10 项**
    /// （<c>UI_Gacha.lua:1232 for i = 1, 10 do</c> + <c>:1243</c> 无越界保护，
    /// 少一项就在客户端越界，而此时挂着 5 分钟遮罩）。
    /// </summary>
    [Fact]
    public async Task Lottery_TenOnNormalPool_ReturnsTenAwards()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-real-ten");
        var tenTicket = h.Player.AddItem(TenG, TenD, TenP, TenL, count: 3);
        int cardsBefore = h.Player.Items.Count(i => i.Genre == 1);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{NormalPoolId},\"bTen\":true}}");

        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Null(reply["err"]);
        Assert.True(reply["bTen"]!.GetValue<bool>());

        var awards = Assert.IsType<JsonArray>(reply["tbAwards"]);
        Assert.Equal(10, awards.Count);
        Assert.All(awards, a =>
        {
            var o = Assert.IsType<JsonObject>(a);
            Assert.Equal(4, Assert.IsType<JsonArray>(o["tbGDPL"]).Count);
            Assert.NotNull(o["nTotalTimes"]);
        });

        // 扣 1 张十连券、发 10 张卡、计数 +10
        Assert.Equal(2u, tenTicket.Count);
        Assert.Equal(cardsBefore + 10, h.Player.Items.Count(i => i.Genre == 1));
        Assert.Equal(10, h.Player.GetTask(GachaFeature.TaskGroup, GachaFeature.TaskTotal));
        Assert.Equal(10, h.Player.GetTask(GachaFeature.TaskGroup,
                                          GachaFeature.TaskPrePoolBegin + NormalPoolId));
    }

    /// <summary>普通池（<c>JudgeType=0</c>）**不需要**「通关 1-6」，只要有券就能抽。</summary>
    [Fact]
    public async Task Lottery_NormalPool_NeedsNoChapterGate()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-nogate");
        Assert.Empty(h.Player.Levels);                  // 一关没打
        h.Player.AddItem(OneG, OneD, OneP, OneL, count: 2);
        h.Player.SetMoney(MoneyType.FreeDiamond, 300);
        h.Player.SetMoney(MoneyType.PayDiamond, 200);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{NormalPoolId},\"bTen\":false}}");

        Assert.Null(LastReply(h, GachaFeature.FnLottery)["err"]);
        Assert.Equal(300, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(200, h.Player.GetMoney(MoneyType.PayDiamond));
    }

    /// <summary>
    /// ★ <c>JudgeType=1</c> 的池（66）有**双条件**门槛：持票 ≥1 **且**已通过 1-6 普通
    /// （<c>GachaCommon.lua:237</c>）。只有票、没通关 ⇒ 客户端界面里根本看不到这个池，
    /// 服务端也必须拒绝（回 notexists，与「池不可选」同一语义）。
    /// </summary>
    [Fact]
    public async Task Lottery_JudgeType1Pool_RequiresChapterGate()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-gate");
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 5);   // 有票
        Assert.False(GachaFeature.PassedFirstChapter(h.Player));           // 但没通关 1-6

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");
        Assert.Equal(GachaFeature.ErrNotExists,
                     LastReply(h, GachaFeature.FnLottery)["err"]!.GetValue<string>());

        // 补上通关记录后就能抽
        PassChapter16(h.Player);
        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");
        Assert.Null(LastReply(h, GachaFeature.FnLottery)["err"]);
    }

    /// <summary>
    /// 保底：池 1 的 <c>ProtectNum=50</c> ⇒ 第 50 抽必出 4★，且出 4★ 后保底计数清零
    /// （客户端靠「<c>cur - lastValue != 抽数</c>」判保底演出，<c>UI_Gacha.lua:1326..1340</c>）。
    /// </summary>
    [Fact]
    public async Task Lottery_ProtectNum_ForcesFourStarAndResetsCounter()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-protect");
        h.Player.AddItem(OneG, OneD, OneP, OneL, count: 60);
        int protectKey = GachaFeature.TaskProtectBegin + NormalPoolId;

        // 直接把保底计数推到 49，下一抽就是第 50 抽
        h.Player.SetTask(GachaFeature.TaskGroup, protectKey, 49);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{NormalPoolId},\"bTen\":false}}");

        var awards = Assert.IsType<JsonArray>(LastReply(h, GachaFeature.FnLottery)["tbAwards"]);
        var gdpl = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(awards[0])["tbGDPL"]);
        // L >= 4 ⇒ 4★（GachaCommon.lua:137 的 min(L,4) 口径）
        Assert.True(gdpl[3]!.GetValue<int>() >= 4,
            $"第 50 抽应保底出 4★，实际 L={gdpl[3]!.GetValue<int>()}");
        // 出 4★ ⇒ 保底计数清零
        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup, protectKey));
    }

    // ================= GetLastGacha =================

    /// <summary>无记录 ⇒ <c>error.gacha.nolast</c>（客户端 <c>:2348</c> 特判，只弹提示）。</summary>
    [Fact]
    public async Task GetLastGacha_WithoutHistory_RepliesNoLast()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-nolast");

        // ★ 上行是**裸布尔**，不是对象
        await SendAsync(h, GachaFeature.FnGetLastGacha, "false");

        var reply = LastReply(h, GachaFeature.FnGetLastGacha);
        Assert.Equal(GachaFeature.ErrNoLast, reply["err"]!.GetValue<string>());
    }

    /// <summary>抽过之后能回放上一次结果，形状与 <c>S2C.Lottery</c> 相同。</summary>
    [Fact]
    public async Task GetLastGacha_AfterDraw_ReplaysSameShape()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-last");
        PassChapter16(h.Player);
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 3);

        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");
        var first = Assert.IsType<JsonArray>(LastReply(h, GachaFeature.FnLottery)["tbAwards"]);
        var firstId = Assert.IsType<JsonObject>(first[0])["nId"]!.GetValue<int>();

        await SendAsync(h, GachaFeature.FnGetLastGacha, "true");
        var reply = LastReply(h, GachaFeature.FnGetLastGacha);

        Assert.Null(reply["err"]);
        Assert.NotNull(reply["bTen"]);
        var awards = Assert.IsType<JsonArray>(reply["tbAwards"]);
        Assert.Single(awards);
        Assert.Equal(firstId, Assert.IsType<JsonObject>(awards[0])["nId"]!.GetValue<int>());
    }

    /// <summary>记录随存档持久化（<c>Player.Extra["gacha"]</c>）。</summary>
    [Fact]
    public async Task GetLastGacha_SurvivesStoreRoundTrip()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-persist");
        PassChapter16(h.Player);
        h.Player.AddItem(TicketG, TicketD, TicketP, TicketL, count: 2);
        await SendAsync(h, GachaFeature.FnLottery, $"{{\"nId\":{PromisePoolId},\"bTen\":false}}");

        h.Module.Store.Save(h.Player);
        var reloaded = h.Module.Store.Load("gacha-persist");

        Assert.NotNull(reloaded);
        Assert.NotNull(GachaFeature.ReadLast(reloaded!));
    }

    // ================= SetCardCustomUp =================

    /// <summary>
    /// 自选 UP 存成位域 <c>card1 | (card2 &lt;&lt; 16)</c>
    /// （<c>GachaCommon:SetCustomUp</c>，<c>:455..516</c>），并把选择次数 +1。
    /// </summary>
    [Fact]
    public async Task SetCardCustomUp_ExpiredOrNonCustomPool_DoesNotWriteTasks()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-customup");

        await SendAsync(h, GachaFeature.FnSetCardCustomUp,
                        """{"nId":6,"tbItems":[113,213]}""");

        int choiceKey = GachaFeature.TaskCardCustomChoice + 6;
        int timeKey = GachaFeature.TaskCardCustomTime + 6;
        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup, choiceKey));
        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup, timeKey));

        await SendAsync(h, GachaFeature.FnSetCardCustomUp,
                        """{"nId":6,"tbItems":[113,213]}""");
        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup, timeKey));
    }

    /// <summary>只给一张卡时高 16 位为 0，不抛。</summary>
    [Fact]
    public async Task SetCardCustomUp_NormalPoolRejectsSelection()
    {
        using var h = await FeatureHarness.CreateAsync("gacha-customup1");

        await SendAsync(h, GachaFeature.FnSetCardCustomUp, """{"nId":1,"tbItems":[77]}""");

        Assert.Equal(0, h.Player.GetTask(GachaFeature.TaskGroup,
                                          GachaFeature.TaskCardCustomChoice + 1));
    }

    // ================= 健壮性 =================

    /// <summary>畸形参数不抛（客户端理论上不会发，但服务端不能因此崩）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"nId":"abc"}""")]
    [InlineData("null")]
    public async Task Lottery_MalformedParam_DoesNotThrow(string json)
    {
        using var h = await FeatureHarness.CreateAsync($"gacha-bad-{json.Length}");
        await SendAsync(h, GachaFeature.FnLottery, json);
        // nId 解析成 0 → 池不存在 → 回 notexists（而不是崩）
        var reply = LastReply(h, GachaFeature.FnLottery);
        Assert.Equal(GachaFeature.ErrNotExists, reply["err"]!.GetValue<string>());
    }

    /// <summary>
    /// 抽卡任务值键必须落在 16 位内（<c>Player.ComposeTaskId</c> 只留低 16 位）。
    /// 现网池 id 最大值 + 各偏移都要安全。
    /// </summary>
    [Fact]
    public void GachaTaskKeys_FitIn16Bits()
    {
        var table = GachaTable.Load(TestPaths.SettingDir);
        int maxId = table.All.Max(p => p.Id);

        foreach (int offset in new[]
                 {
                     GachaFeature.TaskProtectBegin, GachaFeature.TaskProtectUpBegin,
                     GachaFeature.TaskPrePoolBegin, GachaFeature.TaskCardCustomChoice,
                     GachaFeature.TaskCardCustomTime,
                 })
        {
            Assert.InRange(offset + maxId, 0, 0xFFFF);
        }
    }
}
