using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 关卡奖励的真实发放（<see cref="RewardService"/>）。
///
/// 断言口径全部来自客户端「收到回包后怎么用」这一侧：
/// <list type="bullet">
///   <item><b>tbAwards 形状</b>：<c>extracted/lua/UI_MissionSuccess.lua</c>
///     :1257 <c>GetItemBaseInfo(tbAward[1..4])</c>、:1300 <c>txtNum.text = tbAward[5]</c>、
///     :1311 <c>SetItemFlag(item, tbAward[6])</c>、:1542 <c>local g,d,p,l = table.unpack(one)</c>
///     → 元素是**纯数组** <c>{G,D,P,L,N,Flag}</c>。</item>
///   <item><b>标签值</b>：:1267/:1269/:1271 的普通格/追加格判定 + :1364 <c>RewardsSort</c> +
///     <c>Lib.lua:1426 SetItemIconFlagType</c>（<c>nil</c>/100 隐藏角标）+
///     <c>LevelManager.lua:611</c>（其它掉落缺省 <c>i or 100</c>）+
///     <c>UI_SeventeenthTowerLevelInfo.lua:252</c>（首通奖励插 <c>{g,d,p,l,n,0}</c>）。</item>
///   <item><b>叠加语义</b>：<c>Game.EquipManager.AddItem</c>（RVA 0x1D9A82C）按 guid upsert、
///     <c>set_Count</c> 是绝对值；<c>GetItemCount</c>（0x1D9B840）按四元组累加。</item>
///   <item><b>第 6 位语义</b>：<c>Chapter.txt</c> 表头
///     <c>FirstAward(G-D-P-L-N-Level)</c> / <c>FixedAward(G-D-P-L-N-Rate)</c>，
///     实测 FixedAward 324 项第 6 位恒 10000，FirstAward 只有 11 项带第 6 位且**全是 G==1 角色卡**。</item>
/// </list>
/// </summary>
public class RewardServiceTests
{
    private static ItemBaseTable Templates() => ItemBaseTable.Load(RepoPaths.SettingDir);

    private static Player NewPlayer() => new("tester", 1);

    /// <summary>确定性随机源（同一种子两次构造给出同一序列）。</summary>
    private static RewardService Service(ItemBaseTable? templates = null, int seed = 20240819)
        => new(templates ?? Templates(), new Random(seed));

    // ---------------- tbAwards 形状 ----------------

    /// <summary>
    /// 每个元素恰好 6 个整数，顺序 G,D,P,L,N,Flag —— 客户端 :1257/:1300/:1311 就是按这个下标读的。
    /// </summary>
    [Fact]
    public void ToAwardTuples_IsArrayOfSixIntArrays_InGdplnFlagOrder()
    {
        var player = NewPlayer();
        var svc = Service();

        // 1-1 的固定奖励：15-1-1-1-1000-10000（Rate=10000 → 必掉）
        var result = svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 1000, 10000 } }, RewardSource.FixedAward);

        var tuples = result.ToAwardTuples();
        var one = Assert.Single(tuples);
        Assert.Equal(6, one.Length);
        Assert.Equal(new[] { 15, 1, 1, 1, 1000, RewardService.FlagNone }, one);
    }

    /// <summary>三种来源的展示标签。</summary>
    [Theory]
    [InlineData(RewardSource.FirstAward, RewardService.FlagFirstClear)]  // 0   首通（普通格 + 角标）
    [InlineData(RewardSource.FixedAward, RewardService.FlagNone)]        // 100 无角标
    [InlineData(RewardSource.RandomAward, RewardService.FlagRandom)]     // 7   随机（追加格）
    [InlineData(RewardSource.Extra, RewardService.FlagNone)]
    public void Grant_Flag_MatchesSource(RewardSource source, int expectedFlag)
    {
        var svc = Service();
        Assert.Equal(expectedFlag, RewardService.FlagFor(source));

        // FirstAward 的第 6 位是 Level 而不是概率，这里刻意不带第 6 位，避免混淆
        var result = svc.Grant(NewPlayer(), new[] { new[] { 7, 1, 4, 1, 2 } }, source);
        Assert.Equal(expectedFlag, Assert.Single(result.Rewards).Flag);
    }

    /// <summary>什么都没配 / 传 null 时结果为空，且不抛。</summary>
    [Fact]
    public void Grant_NullOrEmpty_ProducesEmptyResult()
    {
        var svc = Service();
        Assert.True(svc.Grant(NewPlayer(), null, RewardSource.FixedAward).IsEmpty);
        Assert.True(svc.Grant(NewPlayer(), Array.Empty<int[]>(), RewardSource.FixedAward).IsEmpty);
        Assert.Empty(svc.Grant(NewPlayer(), Array.Empty<int[]>(), RewardSource.FixedAward).ToAwardTuples());
    }

    // ---------------- 货币折算 ----------------

    /// <summary>
    /// 货币类道具进 <c>Player.Money</c> 而不是背包，并在 <see cref="RewardGrantResult.ChangedMoney"/> 里报账。
    /// (15,1,1,1)=晶币→货币 2、(15,2,1,1)=地球币→货币 3（Lib.lua:1370..1373 + ItemList 的 ClassName）。
    /// </summary>
    [Fact]
    public void Grant_CurrencyItems_GoToMoneyNotInventory()
    {
        var player = NewPlayer();
        var svc = Service();

        var result = svc.Grant(player, new[]
        {
            new[] { 15, 1, 1, 1, 2000 },   // 晶币
            new[] { 15, 2, 1, 1, 20 },     // 地球币
        }, RewardSource.FirstAward);

        Assert.Equal(2000, player.GetMoney(MoneyType.Gold));
        Assert.Equal(20, player.GetMoney(MoneyType.FreeDiamond));
        Assert.Empty(player.Items);
        Assert.Empty(result.ChangedItems);

        Assert.Equal(new[] { MoneyType.Gold, MoneyType.FreeDiamond }, result.ChangedMoney);
        Assert.All(result.Rewards, r =>
        {
            Assert.True(r.IsMoney);
            Assert.Equal(0u, r.ItemGuid);
        });
    }

    /// <summary>同一种货币被发两次只报账一次（<c>MoneyUpdateNtf</c> 推一条就够）。</summary>
    [Fact]
    public void Grant_SameCurrencyTwice_AccumulatesAndReportsOnce()
    {
        var player = NewPlayer();
        var svc = Service();
        var result = new RewardGrantResult();

        svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 2000 } }, RewardSource.FirstAward, result);
        svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 1000, 10000 } }, RewardSource.FixedAward, result);

        Assert.Equal(3000, player.GetMoney(MoneyType.Gold));
        Assert.Equal(new[] { MoneyType.Gold }, result.ChangedMoney);
        // 展示上仍然是两条（首通 2000 + 固定 1000，角标不同）
        Assert.Equal(2, result.Rewards.Count);
        Assert.Equal(RewardService.FlagFirstClear, result.Rewards[0].Flag);
        Assert.Equal(RewardService.FlagNone, result.Rewards[1].Flag);
    }

    /// <summary>缺模板表时仍能把晶币/地球币折算成货币（降级路径）。</summary>
    [Fact]
    public void Grant_WithEmptyTemplateTable_StillResolvesMainCurrencies()
    {
        var player = NewPlayer();
        var svc = Service(ItemBaseTable.Empty);

        svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 500 } }, RewardSource.FixedAward);

        Assert.Equal(500, player.GetMoney(MoneyType.Gold));
        Assert.Empty(player.Items);
    }

    // ---------------- 背包：叠加 vs 独立实例 ----------------

    /// <summary>
    /// 消耗品/材料（genre &gt;= 5）按四元组叠加到同一个 guid，
    /// 且 <see cref="RewardGrantResult.ChangedItems"/> 里的 <c>Count</c> 是叠加后的**绝对值**
    /// （客户端 <c>set_Count</c> 是覆盖，不是累加）。
    /// </summary>
    [Fact]
    public void Grant_StackableItem_MergesOntoOneGuid_WithAbsoluteCount()
    {
        var player = NewPlayer();
        var svc = Service();
        var result = new RewardGrantResult();

        svc.Grant(player, new[] { new[] { 7, 1, 4, 1, 1 } }, RewardSource.FirstAward, result);
        svc.Grant(player, new[] { new[] { 7, 1, 4, 1, 3 } }, RewardSource.Extra, result);

        var item = Assert.Single(player.Items);
        Assert.Equal(7u, item.Genre);
        Assert.Equal(4u, item.Count);

        var changed = Assert.Single(result.ChangedItems);
        Assert.Same(item, changed);
        Assert.Equal(4u, changed.Count);          // 绝对值 = 1 + 3

        // 展示清单仍是两条「本次获得」，数量各是本次的增量
        Assert.Equal(new uint[] { 1, 3 }, result.Rewards.Select(r => r.Count));
    }

    /// <summary>叠加时不能新建 guid（否则客户端会多出一件同名物品）。</summary>
    [Fact]
    public void Grant_StackableItem_ReusesExistingInventoryGuid()
    {
        var player = NewPlayer();
        var existing = player.AddItem(7, 1, 4, 1, 5);
        var svc = Service();

        svc.Grant(player, new[] { new[] { 7, 1, 4, 1, 2 } }, RewardSource.FixedAward);

        Assert.Single(player.Items);
        Assert.Equal(existing.Guid, player.Items[0].Guid);
        Assert.Equal(7u, player.Items[0].Count);
    }

    /// <summary>
    /// 角色卡 / 武器 / 模块（genre 1..4）每份一个独立 guid、每个实例 count=1 —— 它们要独立强化上阵，
    /// 绝不能叠。依据 <c>EquipManager</c> 的五个以 guid 为键的字典（dump.cs:519570..519576）
    /// 与 <c>OnUpdateItem</c>（0x1D9D72C）按 genre 的分派。
    /// </summary>
    [Theory]
    [InlineData(1u)]   // Card
    [InlineData(2u)]   // Weapon
    [InlineData(3u)]   // Rune
    [InlineData(4u)]   // Original
    public void Grant_InstanceGenres_CreateOneGuidPerUnit(uint genre)
    {
        var player = NewPlayer();
        var svc = Service();

        var result = svc.Grant(player, new[] { new[] { (int)genre, 1, 1, 1, 3 } }, RewardSource.Extra);

        Assert.Equal(3, player.Items.Count);
        Assert.All(player.Items, it => Assert.Equal(1u, it.Count));
        Assert.Equal(3, player.Items.Select(it => it.Guid).Distinct().Count());
        Assert.Equal(3, result.ChangedItems.Count);

        // 展示上合成一条 x3（客户端 txtNum.text = tbAward[5]）
        var one = Assert.Single(result.Rewards);
        Assert.Equal(3u, one.Count);
    }

    /// <summary>
    /// 赠卡（G==1）同时补一条 <see cref="Girl"/>，等级取 FirstAward 元组的第 6 位。
    /// 取证：FirstAward 全表只有 11 个 6 位元组，且**全是 G==1**，取值随章节递增
    /// （1-5→5、2-4→10、3-2→15、4-6→25），与表头 <c>G-D-P-L-N-Level</c> 吻合。
    /// 用 1-5 的真实配置 <c>[1-2-1-1-1-5]</c>。
    /// </summary>
    [Fact]
    public void Grant_FirstAwardCard_AddsGirlAtConfiguredLevel()
    {
        var player = NewPlayer();
        var svc = Service();

        svc.Grant(player, new[] { new[] { 1, 2, 1, 1, 1, 5 } }, RewardSource.FirstAward);

        var item = Assert.Single(player.Items);
        Assert.Equal(1u, item.Genre);
        Assert.Equal(2u, item.Detail);
        Assert.Equal(1u, item.Count);

        var girl = Assert.Single(player.Girls);
        Assert.Equal(2u, girl.Girlid);
        Assert.Equal(5u, girl.Level);
        Assert.Equal(2u, girl.Modelid);
    }

    /// <summary>
    /// **回归重点**：FirstAward 的第 6 位是 Level，不是 Rate。
    /// 如果错按 <c>FixedAward</c> 的万分比解释，<c>[1-2-1-1-1-5]</c> 就变成 5/10000 概率，
    /// 首通角色卡几乎永远发不出来。这里跑 200 次，必须每次都发。
    /// </summary>
    [Fact]
    public void Grant_FirstAwardSixthField_IsLevelNotProbability()
    {
        for (int i = 0; i < 200; i++)
        {
            var player = NewPlayer();
            var svc = new RewardService(ItemBaseTable.Empty, new Random(i));
            var result = svc.Grant(player, new[] { new[] { 1, 2, 1, 1, 1, 5 } }, RewardSource.FirstAward);
            Assert.Single(result.Rewards);
            Assert.Single(player.Items);
        }
    }

    /// <summary>同一张卡再赠一次：多一个卡实例，但少女条目不重复建；配置等级更高时抬等级。</summary>
    [Fact]
    public void Grant_SameCardTwice_KeepsOneGirlEntry_AndRaisesLevel()
    {
        var player = NewPlayer();
        var svc = Service();

        svc.Grant(player, new[] { new[] { 1, 2, 1, 1, 1, 5 } }, RewardSource.FirstAward);
        svc.Grant(player, new[] { new[] { 1, 2, 1, 1, 1, 10 } }, RewardSource.FirstAward);

        Assert.Equal(2, player.Items.Count);
        var girl = Assert.Single(player.Girls);
        Assert.Equal(10u, girl.Level);
    }

    /// <summary>非角色卡带第 6 位时按 Level 语义忽略（不当概率、不当数量）。</summary>
    [Fact]
    public void Grant_FirstAwardSixthField_IgnoredForNonCards()
    {
        var player = NewPlayer();
        var svc = Service();

        svc.Grant(player, new[] { new[] { 7, 1, 4, 1, 2, 9 } }, RewardSource.FirstAward);

        Assert.Equal(2u, Assert.Single(player.Items).Count);
        Assert.Empty(player.Girls);
    }

    // ---------------- FixedAward 概率 ----------------

    /// <summary>
    /// <c>FixedAward</c> 第 6 位是万分比：10000（现网全部 324 项的取值）必掉，0 必不掉。
    /// </summary>
    [Theory]
    [InlineData(10000, true)]
    [InlineData(20000, true)]   // 超过基数也当 100%
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Grant_FixedAwardRate_BoundaryValues(int rate, bool granted)
    {
        var player = NewPlayer();
        var svc = Service();

        var result = svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 1000, rate } }, RewardSource.FixedAward);

        Assert.Equal(granted, !result.IsEmpty);
        Assert.Equal(granted ? 1000 : 0, player.GetMoney(MoneyType.Gold));
    }

    /// <summary>没有第 6 位的 FixedAward 元组按 100% 处理（宽容而不是静默丢弃）。</summary>
    [Fact]
    public void Grant_FixedAwardWithoutRate_TreatedAsCertain()
    {
        var player = NewPlayer();
        Service().Grant(player, new[] { new[] { 15, 1, 1, 1, 777 } }, RewardSource.FixedAward);
        Assert.Equal(777, player.GetMoney(MoneyType.Gold));
    }

    /// <summary>中间概率确实在掷骰：5000/10000 跑 4000 次应落在 40%..60% 之间。</summary>
    [Fact]
    public void Grant_FixedAwardRate_IsActuallyRolled()
    {
        var svc = Service(ItemBaseTable.Empty, seed: 12345);
        int hits = 0;
        const int trials = 4000;
        for (int i = 0; i < trials; i++)
        {
            var player = NewPlayer();
            var result = svc.Grant(player, new[] { new[] { 15, 1, 1, 1, 1, 5000 } }, RewardSource.FixedAward);
            if (!result.IsEmpty)
                hits++;
        }
        Assert.InRange(hits, trials * 0.40, trials * 0.60);
    }

    /// <summary><see cref="RewardService.Roll"/> 的边界。</summary>
    [Fact]
    public void Roll_Boundaries()
    {
        var svc = Service();
        Assert.True(svc.Roll(RewardService.RateBase));
        Assert.True(svc.Roll(RewardService.RateBase + 1));
        Assert.False(svc.Roll(0));
        Assert.False(svc.Roll(-100));
    }

    // ---------------- 脏配置防御 ----------------

    /// <summary>长度不足 5 / 数量 &lt;= 0 / 四元组含负值的元组一律跳过，不抛异常。</summary>
    [Fact]
    public void Grant_MalformedTuples_AreSkipped()
    {
        var player = NewPlayer();
        var svc = Service();

        var result = svc.Grant(player, new[]
        {
            new[] { 15, 1, 1, 1 },          // 少一位
            new[] { 15, 1, 1, 1, 0 },       // 数量 0
            new[] { 15, 1, 1, 1, -5 },      // 数量负
            new[] { -1, 1, 1, 1, 10 },      // G 负
            Array.Empty<int>(),
        }, RewardSource.Extra);

        Assert.True(result.IsEmpty);
        Assert.Empty(player.Items);
        Assert.Equal(0, player.GetMoney(MoneyType.Gold));
    }

    /// <summary>独立实例数量有上限，配置异常时只发上限个并打 WARN（不做无界 guid 分配）。</summary>
    [Fact]
    public void Grant_InstanceGenre_RespectsInstanceCap()
    {
        var player = NewPlayer();
        var svc = Service();

        svc.Grant(player, new[] { new[] { 3, 1, 1, 1, RewardService.InstanceCap + 50 } }, RewardSource.Extra);

        Assert.Equal(RewardService.InstanceCap, player.Items.Count);
    }

    // ---------------- 随机奖励抽取 ----------------

    /// <summary>
    /// <c>RandomAward</c> 的第 6 位是万分权重，且**同一行的权重之和 ≤ 10000**，
    /// 差额就是「空掉落」的概率。这里用一个权重全 0 之外只有 1 的池：
    /// 抽 10000 次里绝大多数应该空掉落。
    /// </summary>
    [Fact]
    public void DrawRandom_WeightGap_MeansNothingDropped()
    {
        var svc = Service(ItemBaseTable.Empty, seed: 777);
        var pool = new List<int[]> { new[] { 7, 1, 1, 1, 1, 1, 0, 0 } }; // 权重 1/10000

        int drops = 0;
        for (int i = 0; i < 10000; i++)
            drops += svc.DrawRandom(pool, 1).Count;

        Assert.InRange(drops, 0, 50);   // 期望 1 次左右
    }

    /// <summary>权重和恰好 10000 时必掉一件，且掉的一定来自池子。</summary>
    [Fact]
    public void DrawRandom_FullWeight_AlwaysPicksFromPool()
    {
        var svc = Service(ItemBaseTable.Empty, seed: 42);
        var pool = new List<int[]>
        {
            new[] { 9, 16, 1, 1, 1, 120,  0, 0 },
            new[] { 7, 1,  3, 1, 1, 1976, 0, 0 },
            new[] { 7, 1,  1, 1, 1, 1976, 0, 0 },
            new[] { 7, 1,  2, 1, 1, 1976, 0, 0 },
            new[] { 7, 1,  5, 1, 1, 1976, 0, 0 },
            new[] { 7, 1,  6, 1, 1, 1976, 0, 0 },
        };
        Assert.Equal(RewardService.RateBase, pool.Sum(t => t[5]));

        for (int i = 0; i < 500; i++)
        {
            var picked = Assert.Single(svc.DrawRandom(pool, 1));
            Assert.Contains(picked, pool);
        }
    }

    /// <summary>抽 0 次 / 空池 / null 池都返回空清单。</summary>
    [Fact]
    public void DrawRandom_DegenerateInputs()
    {
        var svc = Service();
        Assert.Empty(svc.DrawRandom(new List<int[]> { new[] { 7, 1, 1, 1, 1, 10000, 0, 0 } }, 0));
        Assert.Empty(svc.DrawRandom(new List<int[]>(), 3));
        Assert.Empty(svc.DrawRandom(null, 3));
    }

    /// <summary>抽 N 次就发 N 件（权重满时），<c>RandDropNum</c> 的语义。</summary>
    [Fact]
    public void DrawRandom_DrawCount_ControlsNumberOfPicks()
    {
        var svc = Service(ItemBaseTable.Empty, seed: 5);
        var pool = new List<int[]> { new[] { 7, 1, 1, 1, 1, 10000, 0, 0 } };
        Assert.Equal(3, svc.DrawRandom(pool, 3).Count);
    }

    /// <summary>同种子可复现（结算日志/回归可对照）。</summary>
    [Fact]
    public void DrawRandom_IsReproducibleForSameSeed()
    {
        var pool = new List<int[]>
        {
            new[] { 9, 16, 1, 1, 1, 5000, 0, 0 },
            new[] { 7, 1,  3, 1, 1, 5000, 0, 0 },
        };
        var a = new RewardService(ItemBaseTable.Empty, new Random(99)).DrawRandom(pool, 20);
        var b = new RewardService(ItemBaseTable.Empty, new Random(99)).DrawRandom(pool, 20);
        Assert.Equal(a, b);
    }

    // ---------------- 真实配置端到端 ----------------

    /// <summary>
    /// 用 <c>extracted/setting/Chapter.txt</c> 里 1-1 的真实配置跑一遍首通结算：
    /// <code>
    /// FirstAward = [[15-2-1-1-20],[7-1-4-1-1],[7-3-1-1-1],[15-1-1-1-2000]]
    /// FixedAward = [15-1-1-1-1000-10000]
    /// </code>
    /// 期望：晶币 2000+1000、地球币 20、两件材料各 1，tbAwards 5 条（4 条首通 + 1 条固定）。
    /// </summary>
    [Fact]
    public void Grant_RealChapter11Config_MatchesConfiguredRewards()
    {
        var chapters = ChapterTable.Load(RepoPaths.SettingDir);
        var cfg = chapters.Get(1, 1, 1);
        Assert.NotNull(cfg);

        var player = NewPlayer();
        var svc = Service();
        var result = new RewardGrantResult();

        svc.Grant(player, cfg!.FirstAward, RewardSource.FirstAward, result);
        svc.Grant(player, cfg.FixedAward, RewardSource.FixedAward, result);

        Assert.Equal(3000, player.GetMoney(MoneyType.Gold));          // 首通 2000 + 固定 1000
        Assert.Equal(20, player.GetMoney(MoneyType.FreeDiamond));     // 首通 20

        Assert.Equal(2, player.Items.Count);                          // (7,1,4,1) 与 (7,3,1,1)
        Assert.All(player.Items, it => Assert.Equal(1u, it.Count));

        var tuples = result.ToAwardTuples();
        Assert.Equal(5, tuples.Length);
        Assert.All(tuples, t => Assert.Equal(6, t.Length));

        // 前 4 条是首通（flag 0），第 5 条是固定（flag 100）
        Assert.Equal(4, tuples.Count(t => t[5] == RewardService.FlagFirstClear));
        Assert.Equal(1, tuples.Count(t => t[5] == RewardService.FlagNone));

        Assert.Equal(new[] { 15, 2, 1, 1, 20, RewardService.FlagFirstClear }, tuples[0]);
        Assert.Equal(new[] { 7, 1, 4, 1, 1, RewardService.FlagFirstClear }, tuples[1]);
        Assert.Equal(new[] { 7, 3, 1, 1, 1, RewardService.FlagFirstClear }, tuples[2]);
        Assert.Equal(new[] { 15, 1, 1, 1, 2000, RewardService.FlagFirstClear }, tuples[3]);
        Assert.Equal(new[] { 15, 1, 1, 1, 1000, RewardService.FlagNone }, tuples[4]);

        // 推送面：两种货币各一条 1032，两件材料合一条 1031
        Assert.Equal(new[] { MoneyType.FreeDiamond, MoneyType.Gold }, result.ChangedMoney);
        Assert.Equal(2, result.ChangedItems.Count);
    }

    /// <summary>非首通只发固定奖励（首通那 4 条不再出现）。</summary>
    [Fact]
    public void Grant_RealChapter11Config_RepeatClearOnlyGrantsFixed()
    {
        var cfg = ChapterTable.Load(RepoPaths.SettingDir).Get(1, 1, 1);
        Assert.NotNull(cfg);

        var player = NewPlayer();
        var result = Service().Grant(player, cfg!.FixedAward, RewardSource.FixedAward);

        Assert.Equal(1000, player.GetMoney(MoneyType.Gold));
        Assert.Equal(0, player.GetMoney(MoneyType.FreeDiamond));
        Assert.Empty(player.Items);
        Assert.Single(result.ToAwardTuples());
    }

    /// <summary>
    /// <see cref="ChapterRandomAwardTable"/>：1-1 的随机池是 6 项、抽 1 次、权重和 10000
    /// （<c>RandDropNum=1</c>，<c>RandomAward=[9-16-1-1-1-120-0-0],[7-1-*-1-1-1976-0-0]x5</c>）。
    /// </summary>
    [Fact]
    public void ChapterRandomAwardTable_Chapter11_HasSixWeightedEntries()
    {
        var table = ChapterRandomAwardTable.Load(RepoPaths.SettingDir);
        Assert.True(table.Count > 0);

        var entry = table.Get(1, 1, 1);
        Assert.NotNull(entry);
        Assert.Equal(1, entry!.DrawCount);
        Assert.Equal(6, entry.Pool.Count);
        Assert.All(entry.Pool, t => Assert.Equal(8, t.Length));
        Assert.Equal(RewardService.RateBase, entry.Pool.Sum(t => t[5]));
    }

    /// <summary>
    /// 全表不变量：任何一关的随机池权重之和都不超过 10000（否则「空掉落空档」的模型就不成立，
    /// 抽取会偏向池尾）。实测直方图只有 10000 / 9998 / 9996 / 9300 四种值。
    /// </summary>
    [Fact]
    public void ChapterRandomAwardTable_AllPools_WeightSumNeverExceedsRateBase()
    {
        var table = ChapterRandomAwardTable.Load(RepoPaths.SettingDir);
        var chapters = ChapterTable.Load(RepoPaths.SettingDir);

        foreach (var lv in chapters.All)
        {
            var entry = table.GetByMapId(lv.MapId);
            if (entry is null)
                continue;
            int sum = entry.Pool.Where(t => t.Length >= 6).Sum(t => t[5]);
            Assert.InRange(sum, 1, RewardService.RateBase);
        }
    }

    /// <summary>缺表时随机奖励表降级为空，不抛。</summary>
    [Fact]
    public void ChapterRandomAwardTable_MissingFile_ReturnsEmpty()
    {
        var table = ChapterRandomAwardTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-setting-dir"));
        Assert.Equal(0, table.Count);
        Assert.Null(table.Get(1, 1, 1));
    }

    /// <summary>
    /// 全表不变量（第 6 位语义的强证据）：
    /// FixedAward 的每一项都必须带第 6 位且值在 (0,10000]；
    /// FirstAward 里带第 6 位的项必须**全是角色卡**（G==1）。
    /// 这两条一旦被将来的表更新打破，说明第 6 位的解释需要重新取证。
    /// </summary>
    [Fact]
    public void ChapterTable_SixthFieldInvariants_HoldForWholeTable()
    {
        var chapters = ChapterTable.Load(RepoPaths.SettingDir);
        Assert.True(chapters.Count > 0);

        int fixedCount = 0, firstWithSixth = 0;
        foreach (var lv in chapters.All)
        {
            foreach (var t in lv.FixedAward)
            {
                Assert.Equal(6, t.Length);
                Assert.InRange(t[5], 1, RewardService.RateBase);
                fixedCount++;
            }
            foreach (var t in lv.FirstAward)
            {
                Assert.InRange(t.Length, 5, 6);
                if (t.Length < 6)
                    continue;
                Assert.Equal(RewardService.GenreCard, (uint)t[0]);   // 只有角色卡带 Level
                Assert.InRange(t[5], 1, 200);                        // 是等级，不是万分比
                firstWithSixth++;
            }
        }
        Assert.Equal(324, fixedCount);
        Assert.Equal(11, firstWithSixth);
    }
}

/// <summary>
/// 结算的**线上形状**：用真实 <see cref="SssjServer.Core.ClientSession"/>（loopback socket）
/// 跑一次 1-1 首通结算，把发出去的封包逐个解回来，验证
/// <list type="number">
///   <item>pf 1034 <c>ChapterUpdateNtf</c>（关卡位域）</item>
///   <item>pf 1032 <c>MoneyUpdateNtf</c> 每种变动货币一条，<c>Curnum</c> 是余额绝对值</item>
///   <item>pf 1031 <c>ItemUpdateNtf</c> 一条带上全部变动实例</item>
///   <item>pf 1024 <c>NtfS2CCall</c>（DoSettle 外壳）里的 <c>tbAwards</c> 真的是
///         「数组的数组」<c>[[G,D,P,L,N,Flag],...]</c> —— 客户端
///         <c>UI_MissionSuccess.lua:1257/1300/1311/1542</c> 就是按这个下标读的</item>
/// </list>
/// 这里直接 new <see cref="ChapterHandler"/> 并显式注入奖励服务/随机表，
/// 不碰任何静态状态（<c>ItemBaseTable.Shared</c> 之类），测试之间互不影响。
/// </summary>
public class ChapterSettlementWireTests : IAsyncLifetime
{
    private System.Net.Sockets.TcpListener? _listener;
    private System.Net.Sockets.TcpClient? _client;
    private System.Net.Sockets.NetworkStream? _clientStream;
    private SssjServer.Core.ClientSession? _session;
    private string _dataDir = "";

    public async Task InitializeAsync()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "sssj-reward-tests", Guid.NewGuid().ToString("N"));

        _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;

        _client = new System.Net.Sockets.TcpClient();
        var connect = _client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var accepted = await _listener.AcceptTcpClientAsync();
        await connect;
        _clientStream = _client.GetStream();

        // 不启动读循环：本测试只关心服务端**发出**的封包（Push/Send 是同步直写）
        _session = new SssjServer.Core.ClientSession(1, accepted);
    }

    public Task DisposeAsync()
    {
        try { _session?.Dispose(); } catch { /* 测试收尾，忽略 */ }
        try { _client?.Dispose(); } catch { /* 同上 */ }
        _listener?.Stop();
        try { Directory.Delete(_dataDir, recursive: true); } catch { /* 同上 */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Settle_Chapter11FirstClear_PushesNtfsAndPopulatesTbAwards()
    {
        var options = new SssjServer.Core.ServerOptions
        {
            DataDirectory = _dataDir,
            SettingDirectory = RepoPaths.SettingDir,
        };
        var store = new PlayerStore(options);
        var luaCalls = new SssjServer.Core.LuaCallRouter();
        luaCalls.NtfS2CCallSender = (s, function, sJsonParam) =>
        {
            s.Push(GamePf.NtfS2CCall, new SssjServer.Protocol.GsProto.NtfS2CCall
            {
                SFunction = function,
                SJsonParam = System.Text.Encoding.UTF8.GetString(sJsonParam),
            });
            return Task.CompletedTask;
        };

        var player = new Player("wire_tester", 42);
        _session!.Tag = player;

        var handler = new ChapterHandler(
            store,
            luaCalls,
            ChapterTable.Load(RepoPaths.SettingDir),
            spendVigour: false,
            rewards: new RewardService(ItemBaseTable.Load(RepoPaths.SettingDir), new Random(7)),
            randomAwards: ChapterRandomAwardTable.Empty);

        // Chapter.lua:349..361 fnSettlement 的 tbParam（只保留服务端会读的字段）
        var param = System.Text.Json.Nodes.JsonNode.Parse(
            """{"nState":1,"Chapter":1,"Index":1,"Difficult":1,"nStar":7,"nTime":42,"nZDL":1234}""");

        await handler.OnScriptCall(_session!, ChapterHandler.Function,
            System.Text.Json.Nodes.JsonNode.Parse("""{"nState":0,"Chapter":1,"Index":1,"Difficult":1}"""));
        var entryPackets = await ReadAllPacketsAsync(_clientStream!, expected: 1);
        var entry = SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.NtfS2CCall>(entryPackets[0].Payload);
        string token = System.Text.Json.Nodes.JsonNode.Parse(entry.SJsonParam)!["battleToken"]!.GetValue<string>();
        param!["battleToken"] = token;
        param["bSuccess"] = true;
        await handler.OnSettle(_session, param);

        var packets = await ReadAllPacketsAsync(_clientStream!, expected: 6);

        // ---- pf 1034 关卡进度 ----
        var chapterNtf = SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.ChapterUpdateNtf>(
            Assert.Single(packets, p => p.Pf == GamePf.ChapterUpdateNtf).Payload);
        Assert.Equal(MapList.MapId(1, 1, 1), Assert.Single(chapterNtf.Info).Id);

        // ---- pf 1032 每种货币一条；晶币 3000（首通2000+固定1000）、地球币 20 ----
        var moneyNtfs = packets.Where(p => p.Pf == GamePf.MoneyUpdateNtf)
            .Select(p => SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.MoneyUpdateNtf>(p.Payload))
            .ToList();
        Assert.Equal(2, moneyNtfs.Count);
        var byMoney = moneyNtfs.ToDictionary(n => n.Info!.Id, n => n.Info!.Curnum);
        Assert.Equal(3000, byMoney[MoneyType.Gold]);
        Assert.Equal(20, byMoney[MoneyType.FreeDiamond]);

        // ---- pf 1031 背包增量：两件材料，一条封包带全 ----
        var itemNtf = SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.ItemUpdateNtf>(
            Assert.Single(packets, p => p.Pf == GamePf.ItemUpdateNtf).Payload);
        Assert.Equal(2, itemNtf.Items.Count);
        Assert.All(itemNtf.Items, it =>
        {
            Assert.Equal(7u, it.Genre);
            Assert.Equal(1u, it.Count);
            Assert.True(it.Guid > 0);
        });
        Assert.Contains(itemNtf.Items, it => it.Detail == 1 && it.Particular == 4);   // (7,1,4,1)
        Assert.Contains(itemNtf.Items, it => it.Detail == 3 && it.Particular == 1);   // (7,3,1,1)

        // ---- pf 1024 DoSettle 外壳里的 tbAwards ----
        var call = SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.NtfS2CCall>(
            Assert.Single(packets, p => p.Pf == GamePf.NtfS2CCall).Payload);
        Assert.Equal("LuaCall", call.SFunction);

        var root = System.Text.Json.Nodes.JsonNode.Parse(call.SJsonParam)!;
        Assert.Equal(ChapterHandler.DoSettleCmd, (int)root["sCmd"]!);
        var inner = root["tbParam"]!;
        Assert.Equal(ChapterHandler.Function, (string)inner["sCmd"]!);

        var body = inner["tbParam"]!;
        Assert.Equal(0, (int)body["nError"]!);
        Assert.Equal(1, (int)body["nState"]!);
        Assert.Equal(6, (int)body["tbExp"]!["MasterExp"]!);   // Chapter.txt 1-1 MasterExp
        Assert.Equal(300, (int)body["tbExp"]!["CardExp"]!);   // Chapter.txt 1-1 CardExp

        // tbAwards 必须是数组的数组，每项 6 个整数
        var awards = Assert.IsType<System.Text.Json.Nodes.JsonArray>(body["tbAwards"]);
        Assert.Equal(5, awards.Count);
        var decoded = awards
            .Select(a => Assert.IsType<System.Text.Json.Nodes.JsonArray>(a).Select(v => (int)v!).ToArray())
            .ToList();
        Assert.All(decoded, t => Assert.Equal(6, t.Length));

        Assert.Equal(new[] { 15, 2, 1, 1, 20, RewardService.FlagFirstClear }, decoded[0]);
        Assert.Equal(new[] { 7, 1, 4, 1, 1, RewardService.FlagFirstClear }, decoded[1]);
        Assert.Equal(new[] { 7, 3, 1, 1, 1, RewardService.FlagFirstClear }, decoded[2]);
        Assert.Equal(new[] { 15, 1, 1, 1, 2000, RewardService.FlagFirstClear }, decoded[3]);
        Assert.Equal(new[] { 15, 1, 1, 1, 1000, RewardService.FlagNone }, decoded[4]);

        // JSON 里绝不能出现 \u 转义（客户端 Json.lua 的解码器不支持 \u，见 LuaCallRouter.ClientJson 注释）
        Assert.DoesNotContain("\\u", call.SJsonParam);
    }

    /// <summary>第二次通关（非首通）只发固定奖励：tbAwards 只剩 1 条，且不再有卡/材料。</summary>
    [Fact]
    public async Task Settle_Chapter11SecondClear_OnlyFixedAwardInTbAwards()
    {
        var options = new SssjServer.Core.ServerOptions
        {
            DataDirectory = _dataDir,
            SettingDirectory = RepoPaths.SettingDir,
        };
        var luaCalls = new SssjServer.Core.LuaCallRouter();
        luaCalls.NtfS2CCallSender = (s, function, sJsonParam) =>
        {
            s.Push(GamePf.NtfS2CCall, new SssjServer.Protocol.GsProto.NtfS2CCall
            {
                SFunction = function,
                SJsonParam = System.Text.Encoding.UTF8.GetString(sJsonParam),
            });
            return Task.CompletedTask;
        };

        var player = new Player("wire_tester2", 43);
        // 预置一次通关记录 → firstClear = false
        player.RecordLevelPass(MapList.MapId(1, 1, 1), 7);
        _session!.Tag = player;

        var handler = new ChapterHandler(
            new PlayerStore(options), luaCalls, ChapterTable.Load(RepoPaths.SettingDir), spendVigour: false,
            rewards: new RewardService(ItemBaseTable.Load(RepoPaths.SettingDir), new Random(7)),
            randomAwards: ChapterRandomAwardTable.Empty);

        await handler.OnScriptCall(_session!, ChapterHandler.Function,
            System.Text.Json.Nodes.JsonNode.Parse("""{"nState":0,"Chapter":1,"Index":1,"Difficult":1}"""));
        var entryPackets = await ReadAllPacketsAsync(_clientStream!, expected: 1);
        var entry = SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.NtfS2CCall>(entryPackets[0].Payload);
        string token = System.Text.Json.Nodes.JsonNode.Parse(entry.SJsonParam)!["battleToken"]!.GetValue<string>();
        var second = System.Text.Json.Nodes.JsonNode.Parse(
            """{"nState":1,"Chapter":1,"Index":1,"Difficult":1,"nStar":7}""")!;
        second["battleToken"] = token;
        second["bSuccess"] = true;
        await handler.OnSettle(_session, second);

        // 只有 1034（进度）+ 1032（晶币）+ 1024（回包）—— 没有 1031，因为没发任何物品
        var packets = await ReadAllPacketsAsync(_clientStream!, expected: 4);
        Assert.DoesNotContain(packets, p => p.Pf == GamePf.ItemUpdateNtf);
        Assert.Single(packets, p => p.Pf == GamePf.MoneyUpdateNtf);

        var call = SssjServer.Protocol.ProtoMessage.Parse<SssjServer.Protocol.GsProto.NtfS2CCall>(
            Assert.Single(packets, p => p.Pf == GamePf.NtfS2CCall).Payload);
        var awards = Assert.IsType<System.Text.Json.Nodes.JsonArray>(
            System.Text.Json.Nodes.JsonNode.Parse(call.SJsonParam)!["tbParam"]!["tbParam"]!["tbAwards"]);

        var one = Assert.IsType<System.Text.Json.Nodes.JsonArray>(Assert.Single(awards));
        Assert.Equal(new[] { 15, 1, 1, 1, 1000, RewardService.FlagNone }, one.Select(v => (int)v!).ToArray());
        Assert.Equal(1000, player.GetMoney(MoneyType.Gold));
        Assert.Empty(player.Items);
    }

    /// <summary>把服务端发出的封包全读回来（16 字节头 + payload，明文，见 AGENTS.md §2.3）。</summary>
    private static async Task<List<SssjServer.Core.Packet>> ReadAllPacketsAsync(
        System.Net.Sockets.NetworkStream stream, int expected)
    {
        var buffer = new List<byte>();
        var chunk = new byte[8192];
        var packets = new List<SssjServer.Core.Packet>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (packets.Count < expected)
        {
            int read = await stream.ReadAsync(chunk, cts.Token);
            if (read == 0)
                break;
            buffer.AddRange(chunk.AsSpan(0, read));

            var span = buffer.ToArray().AsSpan();
            int offset = 0;
            packets.Clear();
            while (SssjServer.Core.PacketCodec.TryDecode(span[offset..], out var packet, out int consumed))
            {
                packets.Add(packet);
                offset += consumed;
            }
        }
        Assert.Equal(expected, packets.Count);
        return packets;
    }
}
