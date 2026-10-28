using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 武器抽卡（「武器补给」= <c>WeaponLogicMsg</c> 的 <c>nCmd = 0</c>）。
///
/// 断言分四类：
/// <list type="number">
/// <item><b>探针：钉住真实配置表的具体数值</b> —— 照 `HANDOFF` §8.0 教训 1
///   （「先写对着真实表文件的探针测试，探针红了先怀疑取证错」）。
///   本域最致命的取证点是**星级取 <c>Rarity</c> 列而不是 GDPL 的 L 位**，
///   而武器池所有行的 L 都是 1 ⇒ 写错的话「4★ 永远抽不出来」且不抛不报错，
///   只有探针能抓住。</item>
/// <item><b>协处理器路由</b> —— 本域与 <see cref="WeaponFeature"/> 共用
///   <c>sFunction = WeaponLogicMsg</c>，必须验证「两域互不吞命令」
///   （第八轮踩过的架构坑，`AGENTS.md` §5）。</item>
/// <item><b>回包形状</b> —— <c>tbParam</c> 元素必须是 <c>{nGuid=…}</c> **对象**、
///   个数必须等于抽数（客户端 <c>#tbParam &gt; 1</c> 判十连）。</item>
/// <item><b>顺序</b> —— pf 1031 必须在回包**之前**（客户端按 nGuid 读本地缓存）。</item>
/// </list>
/// </summary>
public class WeaponForgeFeatureTests
{
    /// <summary>现网唯一开放的武器池（真机自报「武器补给」，开到 2030-03-02）。</summary>
    private const int OpenPoolId = 1;

    /// <summary>池 1 的单抽券 <c>CostOne = [7-7-1-4-1]</c>。</summary>
    private const uint OneG = 7, OneD = 7, OneP = 1, OneL = 4;

    /// <summary>池 1 的十连券 <c>CostTen = [7-10-1-4-1]</c>。</summary>
    private const uint TenG = 7, TenD = 10, TenP = 1, TenL = 4;

    /// <summary>档期已全部过期的武器池（本地表里 2/503..518 全过期）。</summary>
    private const int ExpiredPoolId = 2;

    private static Task SendAsync(FeatureHarness h, string json)
        => h.LuaCalls.DispatchAsync(h.Session, WeaponForgeFeature.Function, json);

    private static JsonObject LastReply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(WeaponForgeFeature.Function, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    private static string Forge(int poolId, bool ten)
        => $"{{\"nCmd\":0,\"Id\":{poolId},\"isTen\":{(ten ? "true" : "false")}}}";

    // ================= ① 探针：钉住配置事实 =================

    [Fact]
    public void Table_LoadsWeaponPools_AndPoolOneIsOpen()
    {
        var table = WeaponGachaTable.Load(TestPaths.SettingDir);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 本地快照：17 个池 id / 26 个档期（★ 真机是 19 个池 —— 本地表少行，
        // 见 AGENTS.md §5；所以这里只断言「至少」而不是精确相等，避免换快照就红）
        Assert.True(table.Count >= 17, $"武器池 id 数 {table.Count} 少于预期 17");

        // ★ 一个池 id 有多段档期（池 1 本地 10 段）——用覆盖式字典存会让过期档期看起来还开着
        Assert.True(table.Segments(OpenPoolId).Count >= 10,
            $"池 1 只有 {table.Segments(OpenPoolId).Count} 段档期 —— 多段被覆盖了？");

        var open = table.OpenAt(now).ToList();
        var plan = GachaRotation.For(TestPaths.SettingDir).Read();
        if (plan.Enabled)
            Assert.Equal(plan.Entries.Where(e => e.Type == 0 && e.Start <= now && now < e.End)
                .Select(e => e.Id).Distinct().OrderBy(x => x), open.Select(p => p.Id).OrderBy(x => x));
        else
            Assert.Equal(OpenPoolId, Assert.Single(open).Id);

        var pool = open.Single(p => p.Id == OpenPoolId);
        Assert.Equal(0, pool.CashType);             // 用道具抽
        Assert.Equal(0, pool.JudgeType);            // 不是自选 UP 池
        Assert.Equal(3, pool.CardStarSeedId);       // 真机自报 seed=3
        Assert.Equal(new[] { 7, 7, 1, 4, 1 }, pool.CostOneItem);
        Assert.Equal(new[] { 7, 10, 1, 4, 1 }, pool.CostTenItem);
        Assert.True(pool.HasTen);
        Assert.Equal(90, pool.ExchangeRate);
        Assert.Equal(1, pool.CanUseFreeDiamond);
        // 池 1 没配免费次数 ⇒ CanFree 恒 false（主城红点也不会因它亮）
        Assert.Equal(0, pool.FreeType);
        Assert.False(pool.FreeAvailableAt(now, usedFree: 0));

        // 过期池：存在但取不到当前档期
        Assert.True(table.Exists(ExpiredPoolId));
        if (!plan.Enabled) Assert.Null(table.Get(ExpiredPoolId, now));
    }

    /// <summary>
    /// ★★★ 本域最致命的一条：**星级取 <c>Rarity</c> 列，不是 GDPL 的 L 位**。
    /// 武器池所有行的 L 都是 1，而 Rarity 是 1..4 —— 写错会让 4★ 永远抽不出来。
    /// </summary>
    [Fact]
    public void Entries_StarComesFromRarityColumn_NotFromGdplLevel()
    {
        var table = WeaponGachaTable.Load(TestPaths.SettingDir);
        var pool = table.Get(OpenPoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;

        // 本地快照 78 件（真机 80 件 —— 同样只断言「至少」）
        Assert.True(pool.Entries.Count >= 78, $"池 1 只解出 {pool.Entries.Count} 件武器");

        // ★ 全部武器的 GDPL 第 4 位（L）都是 1 —— 所以 L 完全不能当星级用
        Assert.All(pool.Entries.Values, e => Assert.Equal(1, e.L));
        // ★ 而 Rarity 覆盖 1..4
        Assert.Equal(new[] { 1, 2, 3, 4 }, pool.Entries.Values.Select(e => e.Rarity).Distinct().OrderBy(x => x).ToArray());
        // 全部是武器（genre 2）
        Assert.All(pool.Entries.Values, e => Assert.Equal(2, e.G));

        // 星级分布（本地快照）：1★5 / 2★20 / 3★25 / 4★28（真机 4★ 是 30）
        var byStar = pool.Entries.Values.GroupBy(e => e.Rarity).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(5, byStar[1]);
        Assert.Equal(20, byStar[2]);
        Assert.Equal(25, byStar[3]);
        Assert.True(byStar[4] >= 28, $"4★ 只有 {byStar[4]} 件");

        // 具体一件 4★ 的取证靶子：ID=131 [2-1-3-1] Rarity=4（群星道标）
        var star4 = pool.Entries[131];
        Assert.Equal(4, star4.Rarity);
        Assert.Equal(new[] { 2, 1, 3, 1 }, star4.Gdpl);
    }

    /// <summary>星级内归一化（`WeaponForge.lua:93..97`）：同星级的 RateInStar 之和为 1。</summary>
    [Fact]
    public void Entries_RateIsNormalizedWithinEachStar()
    {
        var table = WeaponGachaTable.Load(TestPaths.SettingDir);
        var pool = table.Get(OpenPoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;

        foreach (var group in pool.Entries.Values.GroupBy(e => e.Rarity))
        {
            double sum = group.Sum(e => e.RateInStar);
            Assert.True(Math.Abs(sum - 1.0) < 1e-9,
                $"{group.Key}★ 的 RateInStar 之和是 {sum}，应为 1");
        }

        // 各星级的原始权重合计（本地快照）
        Assert.Equal(500, pool.StarTotal[1]);
        Assert.Equal(2000, pool.StarTotal[2]);
        Assert.Equal(2500, pool.StarTotal[3]);
        Assert.True(pool.StarTotal[4] >= 2800);
    }

    /// <summary>
    /// ★★★ 探针：<c>CardStarSeed.txt</c> 的阶梯**会用尽**（最大档位只到 50/100），
    /// 且每个星级的两档权重**完全相同**。
    ///
    /// 这两条合起来支撑 <see cref="WeaponForgeFeature"/> 里「把 times 夹到最后一档」的做法：
    /// 不夹 ⇒ 武器池（不清零计数）第 101 抽起权重全 0 ⇒ 静默恒出 1★；
    /// 夹了 ⇒ 因为两档权重相同，分布完全不变。
    /// </summary>
    [Fact]
    public void Seed_StepsAreExhaustible_AndBothStepsShareTheSameWeight()
    {
        var table = WeaponGachaTable.Load(TestPaths.SettingDir);
        var seed = table.GetSeed(3);
        Assert.NotNull(seed);

        // 池 1 用的 seed=3 最大档位是 100
        Assert.Equal(100, seed!.MaxTimes);

        // 第 100 抽还有权重，第 101 抽（不夹取时）全 0 —— 这就是那个静默 bug 的根源
        Assert.True(seed.WeightsAt(100).Sum() > 0);
        Assert.Equal(0, seed.WeightsAt(101).Sum());

        // 每个星级的两档权重相同 ⇒ 夹取不改变分布
        Assert.Equal(seed.WeightsAt(1), seed.WeightsAt(100));

        // seed=3 的四个星级权重（1200/7000/1500/300，合计 10000）
        Assert.Equal(new[] { 1200, 7000, 1500, 300 }, seed.WeightsAt(1));
    }

    // ================= ② 协处理器路由 =================

    /// <summary>
    /// ★ 免费抽的判定规则（<c>WeaponForge:CanFree</c>，`WeaponForge.lua:160..175`）。
    ///
    /// ★★ 为什么用**构造出来的池**验而不是现网池：现网唯一开放的池 1 的
    /// <c>FreeType</c> 列为空 ⇒ <c>CanFree</c> 恒 false（见
    /// <see cref="Table_LoadsWeaponPools_AndPoolOneIsOpen"/> 里那两条断言）
    /// ⇒ 端到端路径覆盖不到「免费抽」这一支。
    /// 而 <c>UI_Main.lua:803</c> 的主城红点
    /// （<c>GachaCommon:CanFreeAll() or WeaponForge:CanFreeAll()</c>）
    /// **完全在客户端本地算、不发包** ⇒ 服务端唯一的责任就是把免费次数
    /// 任务值 <c>(15, 9001 + 池id)</c> 维护正确，让客户端算得对。
    /// 这条测试守住那份规则表（换池/换配置即生效）。
    /// </summary>
    [Theory]
    // freeType=0 ⇒ 无论如何都没有免费次数（现网池 1 就是这种）
    [InlineData(0, 3, 0, false)]
    // freeType!=0 但 freeCount=0 ⇒ 也没有
    [InlineData(1, 0, 0, false)]
    // 有额度且没用完 ⇒ 可以
    [InlineData(1, 1, 0, true)]
    // 用完了 ⇒ 不可以
    [InlineData(1, 1, 1, false)]
    [InlineData(1, 3, 3, false)]
    // 还剩一次 ⇒ 可以
    [InlineData(1, 3, 2, true)]
    public void FreeAvailableAt_FollowsCanFreeRules(int freeType, int freeCount,
                                                    int usedFree, bool expected)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pool = new WeaponGachaPool
        {
            Id = 999,
            FreeType = freeType,
            FreeCount = freeCount,
        };

        Assert.Equal(expected, pool.FreeAvailableAt(now, usedFree));
    }

    /// <summary>
    /// ★ 免费时段：<c>nFreeStart</c>/<c>nFreeEnd</c> 都配了才判时段
    /// （Lua 侧 <c>if tbData.nFreeStart and tbData.nFreeEnd then</c>），
    /// 且判据是 <c>now &lt; start or now &gt; end</c>（**两端闭**）。
    /// </summary>
    [Fact]
    public void FreeAvailableAt_RespectsFreeWindow_WhenConfigured()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 时段内
        var inside = new WeaponGachaPool
        {
            Id = 999, FreeType = 1, FreeCount = 1,
            FreeStart = now - 3600, FreeEnd = now + 3600,
        };
        Assert.True(inside.FreeAvailableAt(now, usedFree: 0));

        // 时段已过
        var past = new WeaponGachaPool
        {
            Id = 999, FreeType = 1, FreeCount = 1,
            FreeStart = now - 7200, FreeEnd = now - 3600,
        };
        Assert.False(past.FreeAvailableAt(now, usedFree: 0));

        // 时段未到
        var future = new WeaponGachaPool
        {
            Id = 999, FreeType = 1, FreeCount = 1,
            FreeStart = now + 3600, FreeEnd = now + 7200,
        };
        Assert.False(future.FreeAvailableAt(now, usedFree: 0));

        // ★ 没配时段（都是 0）⇒ 不判时段，只看额度
        var noWindow = new WeaponGachaPool { Id = 999, FreeType = 1, FreeCount = 1 };
        Assert.True(noWindow.FreeAvailableAt(now, usedFree: 0));
    }

    /// <summary>
    /// ★ 与 <see cref="WeaponFeature"/> 共用同一个 sFunction，两域必须互不吞命令。
    /// 症状若出现：被吞的那一侧「一条都不响应，连 unhandled 日志都没有」。
    /// </summary>
    [Fact]
    public async Task WeaponLogicMsg_ForgeAndOtherCmds_BothReachTheirOwnFeature()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-route");
        h.Player.AddItem(OneG, OneD, OneP, OneL, count: 5);

        // nCmd=0 → 本域（成功，带 tbParam）
        await SendAsync(h, Forge(OpenPoolId, ten: false));
        var forge = LastReply(h);
        Assert.Equal(0, forge["nError"]!.GetValue<int>());
        Assert.Equal(0, forge["nCmd"]!.GetValue<int>());
        Assert.IsType<JsonArray>(forge["tbParam"]);

        // nCmd=2（分解，属 WeaponFeature）→ 不该被本域吞掉：
        // 传一个不存在的 guid，应由 WeaponFeature 回失败 —— 关键是 nCmd 回显为 2
        await SendAsync(h, "{\"nCmd\":2,\"tbGuid\":[999999]}");
        var decompose = LastReply(h);
        Assert.Equal(2, decompose["nCmd"]!.GetValue<int>());
    }

    // ================= ③ 单抽 / 十连成功路径 =================

    [Fact]
    public async Task Forge_Single_ConsumesOneTicket_AndGrantsOneWeapon()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-one");
        var ticket = h.Player.AddItem(OneG, OneD, OneP, OneL, count: 5);
        int before = h.Player.Items.Count(i => i.Genre == 2);

        await SendAsync(h, Forge(OpenPoolId, ten: false));

        var reply = LastReply(h);
        Assert.Equal(0, reply["nError"]!.GetValue<int>());
        var arr = Assert.IsType<JsonArray>(reply["tbParam"]);
        Assert.Single(arr);

        // 扣 1 张单抽券、发 1 把武器
        Assert.Equal(4u, ticket.Count);
        Assert.Equal(before + 1, h.Player.Items.Count(i => i.Genre == 2));

        // 保底计数 +1，键是 (15, 1002 + 1) = (15, 1003)
        Assert.Equal(1, h.Player.GetTask(WeaponForgeFeature.TaskGroup,
                                        WeaponForgeFeature.TaskProtectBeginWeapon + OpenPoolId));
    }

    /// <summary>
    /// ★★ 十连：抽 10 件，但**只扣 1 张十连券**
    /// （`WeaponForge.lua:190` <c>local n = 1</c> 恒定 —— 十连券本身就是一张）。
    /// </summary>
    [Fact]
    public async Task Forge_Ten_DrawsTenWeapons_ButConsumesOnlyOneTenTicket()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-ten");
        var tenTicket = h.Player.AddItem(TenG, TenD, TenP, TenL, count: 3);
        var oneTicket = h.Player.AddItem(OneG, OneD, OneP, OneL, count: 7);
        int before = h.Player.Items.Count(i => i.Genre == 2);

        await SendAsync(h, Forge(OpenPoolId, ten: true));

        var arr = Assert.IsType<JsonArray>(LastReply(h)["tbParam"]);
        Assert.Equal(WeaponForgeFeature.TenCount, arr.Count);

        Assert.Equal(2u, tenTicket.Count);                 // ★ 只扣 1 张
        Assert.Equal(7u, oneTicket.Count);                 // 单抽券不动
        Assert.Equal(before + 10, h.Player.Items.Count(i => i.Genre == 2));
        Assert.Equal(10, h.Player.GetTask(WeaponForgeFeature.TaskGroup,
                                         WeaponForgeFeature.TaskProtectBeginWeapon + OpenPoolId));
    }

    /// <summary>
    /// ★★★ 回包元素必须是 <c>{nGuid=…}</c> **对象**，且 guid 必须真的能在背包里找到
    /// （客户端 `UI_WeaponSystemNEW.lua:903` 用 <c>v.nGuid</c> 去
    /// <c>Game.EquipManager.GetWeapon()</c> 重读本地缓存）。
    /// </summary>
    [Fact]
    public async Task Forge_ReplyElements_AreObjectsWithNGuid_ResolvableInBag()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-shape");
        h.Player.AddItem(TenG, TenD, TenP, TenL, count: 1);

        await SendAsync(h, Forge(OpenPoolId, ten: true));

        var arr = Assert.IsType<JsonArray>(LastReply(h)["tbParam"]);
        Assert.Equal(10, arr.Count);

        var guids = new List<uint>();
        foreach (var el in arr)
        {
            // 必须是对象（裸数字会让客户端 v.nGuid 取到 nil）
            var obj = Assert.IsType<JsonObject>(el);
            uint guid = obj["nGuid"]!.GetValue<uint>();
            Assert.NotEqual(0u, guid);
            guids.Add(guid);
        }

        // guid 互不相同，且每个都是背包里的一把武器
        Assert.Equal(10, guids.Distinct().Count());
        foreach (uint guid in guids)
        {
            var item = h.Player.Items.FirstOrDefault(i => i.Guid == guid);
            Assert.NotNull(item);
            Assert.Equal(2u, item!.Genre);
        }
    }

    /// <summary>
    /// ★★★ 顺序：pf 1031（背包）必须在 pf 1024（回包）**之前**到达 ——
    /// 客户端拿到 nGuid 就立刻读本地缓存，反了就是「rarity 恒 0、演出空转」。
    /// </summary>
    [Fact]
    public async Task Forge_PushesItemsBeforeReplying()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-order");
        h.Player.AddItem(OneG, OneD, OneP, OneL, count: 2);

        await SendAsync(h, Forge(OpenPoolId, ten: false));

        // 背包增量推送已发出
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf),
            "武器抽卡必须推 pf 1031，否则客户端 GetWeapon(nGuid) 取不到武器");
        // 且回包确实带了 guid
        var arr = Assert.IsType<JsonArray>(LastReply(h)["tbParam"]);
        Assert.Single(arr);
    }

    // ================= ④ 失败路径 =================

    /// <summary>档期过期 ⇒ 必须回 <b>20096</b>（客户端走 DialogReflashPool 提示刷新卡池）。</summary>
    [Fact]
    public async Task Forge_ExpiredPool_RepliesPoolClosedCode()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-expired");
        // 本地全池轮换已经开放池 2；其合法档期不应再按旧快照拒绝。
        if (GachaRotation.For(TestPaths.SettingDir).Read().Enabled)
        {
            Assert.NotNull(WeaponGachaTable.Load(TestPaths.SettingDir).Get(ExpiredPoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            return;
        }
        h.Player.AddItem(OneG, OneD, OneP, OneL, count: 5);

        await SendAsync(h, Forge(ExpiredPoolId, ten: false));

        var reply = LastReply(h);
        Assert.Equal(WeaponForgeFeature.ErrPoolClosed, reply["nError"]!.GetValue<int>());
        Assert.Equal(0, reply["nCmd"]!.GetValue<int>());
        // 没扣券、没发武器
        Assert.Equal(0, h.Player.Items.Count(i => i.Genre == 2));
    }

    /// <summary>池子压根不存在（本地表可能比设备少行）⇒ 同样回 20096，且不崩。</summary>
    [Fact]
    public async Task Forge_UnknownPool_RepliesPoolClosedCode()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-unknown");

        await SendAsync(h, Forge(999999, ten: false));

        Assert.Equal(WeaponForgeFeature.ErrPoolClosed,
                     LastReply(h)["nError"]!.GetValue<int>());
    }

    /// <summary>
    /// 券不足且钻石也不足 ⇒ 回失败、且**不发武器**。
    /// ★ 关键是「必须回包」：客户端此刻挂着 <c>Blocking.Show(300)</c> 5 分钟遮罩。
    /// </summary>
    [Fact]
    public async Task Forge_NotEnoughCurrency_RepliesError_AndGrantsNothing()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-poor");
        h.Player.SetMoney(MoneyType.FreeDiamond, 0);

        await SendAsync(h, Forge(OpenPoolId, ten: false));

        var reply = LastReply(h);
        Assert.True(reply["nError"]!.GetValue<int>() > 0);
        Assert.Equal(0, h.Player.Items.Count(i => i.Genre == 2));
        Assert.Equal(0, h.Player.GetTask(WeaponForgeFeature.TaskGroup,
                                        WeaponForgeFeature.TaskProtectBeginWeapon + OpenPoolId));
    }

    /// <summary>
    /// 券不足但**免费钻石够** ⇒ 用钻石顶替（`WeaponForge.lua:196`），
    /// 单抽扣 <c>ExchangeRate × 1</c>、十连扣 <c>× 10</c>（`UI_WeaponSystemNEW.lua:723`）。
    /// </summary>
    [Fact]
    public async Task Forge_NoTicketButHasFreeDiamond_SpendsDiamondByExchangeRate()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-diamond");
        var pool = WeaponGachaTable.Load(TestPaths.SettingDir)
                                   .Get(OpenPoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;
        h.Player.SetMoney(MoneyType.FreeDiamond, 1000);

        await SendAsync(h, Forge(OpenPoolId, ten: false));

        Assert.Equal(0, LastReply(h)["nError"]!.GetValue<int>());
        Assert.Equal(1000 - pool.ExchangeRate, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(1, h.Player.Items.Count(i => i.Genre == 2));
    }

    // ================= ⑤ 保底计数长期行为 =================

    /// <summary>
    /// ★★★ 抽满 100 次之后**仍然能抽出高星**（回归 `PickStar` 的夹取逻辑）。
    ///
    /// 不夹取的话：seed=3 最大档位 100、武器池又不清零计数 ⇒ 第 101 抽起权重全 0
    /// ⇒ 退化成「恒出池内最低星级 1★」。这条测试就是那个 bug 的回归闸。
    /// </summary>
    [Fact]
    public async Task Forge_AfterExceedingSeedSteps_StillDrawsVariedStars()
    {
        using var h = await FeatureHarness.CreateAsync("wforge-beyond");
        // 直接把保底计数顶到 150（超过 seed=3 的最大档位 100）
        h.Player.SetTask(WeaponForgeFeature.TaskGroup,
                         WeaponForgeFeature.TaskProtectBeginWeapon + OpenPoolId, 150);
        h.Player.AddItem(TenG, TenD, TenP, TenL, count: 20);

        var stars = new HashSet<int>();
        var table = WeaponGachaTable.Load(TestPaths.SettingDir);
        var pool = table.Get(OpenPoolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds())!;

        // 抽 5 次十连 = 50 件，统计星级分布
        for (int i = 0; i < 5; i++)
        {
            await SendAsync(h, Forge(OpenPoolId, ten: true));
            Assert.Equal(0, LastReply(h)["nError"]!.GetValue<int>());
        }

        foreach (var item in h.Player.Items.Where(i => i.Genre == 2))
        {
            var entry = pool.Entries.Values.FirstOrDefault(e =>
                e.D == (int)item.Detail && e.P == (int)item.Particular && e.L == (int)item.Level);
            if (entry is not null)
                stars.Add(entry.Rarity);
        }

        // 50 件里若还只出 1★，说明权重塌了（seed=3 的 2★ 权重高达 7000/10000，
        // 正常情况下 50 件必然出现 2★）
        Assert.True(stars.Count > 1,
            $"抽了 50 件却只出现星级 [{string.Join(",", stars)}] —— 阶梯用尽后权重塌成 0 了？");
        Assert.Contains(2, stars);
    }
}
