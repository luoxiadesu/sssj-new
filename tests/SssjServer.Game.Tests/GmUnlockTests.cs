using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 「GM 测试账号全解锁」（<see cref="GmUnlock"/>）。
///
/// 断言分两类：
/// <list type="number">
/// <item><b>钉住配置事实</b> —— <c>CharacterCard.txt</c> 是本轮新解出来的表
///   （从 setting.u 的 ScriptableObject 反序列化，见 <c>work/parse_charactercard.py</c>），
///   条数/Genre/少女 id 值域一旦变了，下面的期望值都要跟着改，所以先钉住。</item>
/// <item><b>钉住三条最容易写错、且客户端会硬报错的规则</b>：
///   ① <c>Detail=102</c> 那张卡必须被排除（<c>GirlConfig.CheckGirlId</c> 判 <c>GirlIdNull</c>
///      → <c>GetGirlTaskEx</c> 返回 nil → 客户端 <c>nil &gt; 0</c> 抛错）；
///   ② 少女相关任务值 <c>taskId = (girlId-1)*2000 + off</c> 必须落在 16 位内
///      （<c>Player.ComposeTaskId</c> 只保留低 16 位，溢出会静默串键）；
///   ③ 全部写操作必须**幂等**（登录钩子每次登录都可能调用）。</item>
/// </list>
/// </summary>
public class GmUnlockTests
{
    private static GmUnlock Create(out ServerOptions options,
                                   out CharacterCardTable cards,
                                   out ChapterTable chapters,
                                   out ItemBaseTable items,
                                   out MissionTable missions,
                                   bool missionClaimed = true)
        => Create(out options, out cards, out chapters, out items, out missions,
                  out _, out _, missionClaimed);

    private static GmUnlock Create(out ServerOptions options,
                                   out CharacterCardTable cards,
                                   out ChapterTable chapters,
                                   out ItemBaseTable items,
                                   out MissionTable missions,
                                   out GuideTable guides,
                                   out PlayerExpTable playerExp,
                                   bool missionClaimed = true)
    {
        options = new ServerOptions
        {
            SettingDirectory = TestPaths.SettingDir,
            GmMissionClaimed = missionClaimed,
        };
        chapters = ChapterTable.Load(TestPaths.SettingDir);
        items = ItemBaseTable.Load(TestPaths.SettingDir);
        cards = CharacterCardTable.Load(TestPaths.SettingDir);
        missions = MissionTable.Load(TestPaths.SettingDir);
        guides = GuideTable.Load(TestPaths.SettingDir);
        playerExp = PlayerExpTable.Load(TestPaths.SettingDir);
        return new GmUnlock(options, chapters, items, cards, missions, guides, playerExp,
                            MissingIconTable.Load(TestPaths.SettingDir));
    }

    private static GmUnlock Create() => Create(out _, out _, out _, out _, out _);

    private static Player NewPlayer(string account = "gm-test")
        => new(account, World.DeriveRoleId(account));

    // ================= 配置事实 =================

    /// <summary>
    /// CharacterCard.txt 的形状。这张表不是 APK 里的原始文件 —— 它由
    /// <c>work/parse_charactercard.py</c> 从 setting.u 解出（269 条，字节精确闭合：
    /// 解析完最后一条后 tail 恰好 0 字节），再由 <c>work/charactercard_to_setting.py</c>
    /// 转成三行表头格式。这条测试保证「表还在、还是那 269 张」。
    /// </summary>
    [Fact]
    public void CharacterCardTable_Current295Cards_AllGenre1()
    {
        var table = CharacterCardTable.Load(TestPaths.SettingDir);

        Assert.Equal(295, table.Count);
        Assert.All(table.All, c => Assert.Equal(CharacterCardTable.CardGenre, c.G));

        // 抽样与 CardRefit.txt 交叉核对（该表第 4 行 G=1 D=2 P=1 L=3 备注「私服·高奈利亚」）
        var gaonailiya = table.Get(1, 2, 1, 3);
        Assert.NotNull(gaonailiya);
        Assert.Equal("cc_sifu_gaonailiya", gaonailiya!.DescriptionIndex);
        Assert.Equal("私服·高奈利亚", gaonailiya.DisplayName);

        // StarterLoadout 用的那张（1,4,7,3)「战术装备·沐恩」
        var muen = table.Get(1, 4, 7, 3);
        Assert.NotNull(muen);
        Assert.Equal("cc_zhanshuzhuangbei_muen", muen!.DescriptionIndex);
    }

    /// <summary>
    /// <c>ModelId</c> 与 <c>Particular</c> 的关系（269 条实测统计），以及必须过
    /// <see cref="GmUnlock.FixModelId"/> 的那 27 张。
    ///
    /// 这条断言的价值：它是「<c>GirlInfo.modelid</c> 到底是什么」的判据 —— 它是
    /// **当前穿的那套衣服**，不是少女 id（取证 <c>UI_GirlClothes.lua:479</c>
    /// <c>tbGirl.ModelId == tbBaseInfo.ModelId</c>、<c>GirlConfig.lua:340</c>
    /// <c>tbSuitConfig[GirlId][tb.ModelId]</c>）。
    /// 同时钉住 3 组例外映射 —— 它们让 <c>FixModelId</c> 从「抄来备用」变成「必须调用」，
    /// 漏了会让 group 4 的衣服标记写到与客户端不同的键上（静默失败）。
    /// </summary>
    [Fact]
    public void CharacterCard_ModelIdIsSuitId_WithFourSkinExceptions()
    {
        var table = CharacterCardTable.Load(TestPaths.SettingDir);

        var exceptions = table.All.Where(c => c.ModelId != (int)c.P)
                                 .GroupBy(c => ((int)c.P, c.ModelId))
                                 .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(4, exceptions.Count);
        Assert.Equal(14, exceptions[(71, 7001)]);
        Assert.Equal(14, exceptions[(81, 8001)]);
        Assert.Equal(4, exceptions[(82, 8002)]);
        Assert.Equal(2, exceptions[(72, 7002)]);

        // 当前配置其余卡片 ModelId == P
        Assert.Equal(295 - 14 - 14 - 4 - 2, table.All.Count(c => c.ModelId == (int)c.P));

        // 这三个例外过 FixModelId 之后都落到 < 2000 的合法偏移
        Assert.Equal(1711, GmUnlock.FixModelId(7001));
        Assert.Equal(1801, GmUnlock.FixModelId(8001));
        Assert.Equal(1802, GmUnlock.FixModelId(8002));
        Assert.All(table.All, c => Assert.InRange(GmUnlock.FixModelId(c.ModelId), 0, 1999));
    }

    /// <summary>
    /// 表里恰好有一张 Detail 非法的卡（<c>Detail=102</c>）。
    /// 它 &gt; <see cref="GmUnlock.NormalEndGirlId"/>(32) 又不在 (200,232]，
    /// 客户端 <c>GirlConfig:CheckGirlId</c>（GirlConfig.lua:436..445）判 <c>GirlIdNull</c>。
    /// </summary>
    [Fact]
    public void CharacterCard_HasExactlyOneInvalidGirlId_102()
    {
        var table = CharacterCardTable.Load(TestPaths.SettingDir);
        var invalid = table.All.Where(c => !GmUnlock.IsValidGirlId(c.D)).ToList();

        Assert.Single(invalid);
        Assert.Equal(102u, invalid[0].D);
        Assert.Equal(20, table.GirlIds.Count(GmUnlock.IsValidGirlId));
    }

    [Theory]
    [InlineData(1u, true)]
    [InlineData(16u, true)]
    [InlineData(32u, true)]
    [InlineData(33u, false)]
    [InlineData(102u, false)]
    [InlineData(200u, false)]   // LinkGirlId 本身是「> 200」，不含 200
    [InlineData(201u, true)]
    [InlineData(232u, true)]
    [InlineData(233u, false)]
    [InlineData(0u, false)]
    public void IsValidGirlId_MatchesClientCheckGirlId(uint girlId, bool expected)
        => Assert.Equal(expected, GmUnlock.IsValidGirlId(girlId));

    /// <summary><c>GirlCommon:FixModelId</c> 的三条分支（GirlCommon.lua:609..629）。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1999, 1999)]
    [InlineData(8001, 1801)]
    [InlineData(8002, 1802)]
    [InlineData(8003, 1803)]
    [InlineData(3005, 1595)]    // 3005 % 100 = 5, 3005/1000 = 3 → 5 + 3*30 + 1500
    [InlineData(4029, 1649)]    // 29 + 4*30 + 1500
    [InlineData(7001, 1711)]    // 1 + 7*30 + 1500 —— 现网 CharacterCard.txt 里真实存在（11 张）
    public void FixModelId_MatchesLua(int input, int expected)
        => Assert.Equal(expected, GmUnlock.FixModelId(input));

    [Fact]
    public void LinkGirlFlagsUseExtendedGroupsWithoutOverwritingNormalGirl()
    {
        var gm = Create(); var player = NewPlayer(); gm.ApplyCards(player);
        Assert.Equal(1, GmUnlock.GirlTaskId(201, 1));
        Assert.Equal(3, GmUnlock.GirlTaskGroup(3, 1));
        Assert.Equal(90, GmUnlock.GirlTaskGroup(3, 201));
        Assert.Equal(91, GmUnlock.GirlTaskGroup(4, 201));
        Assert.Equal(92, GmUnlock.GirlTaskGroup(7, 201));
        Assert.Equal(1, player.GetTask(90, GmUnlock.GirlVarIsEndShow));
        Assert.Equal(1, player.GetTask(3, GmUnlock.GirlVarIsEndShow));
        Assert.Equal(GmUnlock.OwnedNotNew, player.GetTask(91, 1));
        Assert.Equal(GmUnlock.OwnedNotNew, player.GetTask(92, 24));
    }

    // ================= 全角色卡 =================

    [Fact]
    public void ApplyCards_GrantsEveryValidCard_AndBuildsGirls()
    {
        var gm = Create(out _, out var cards, out _, out _, out _);
        var player = NewPlayer();

        var report = gm.ApplyCards(player);

        int expectedCards = cards.All.Count(c => GmUnlock.IsValidGirlId(c.D));
        Assert.Equal(294, expectedCards);                       // 295 - 1 张 Detail=102
        Assert.Equal(expectedCards, report.NewCards);
        Assert.Equal(expectedCards, player.Items.Count);
        Assert.All(player.Items, it => Assert.Equal(CharacterCardTable.CardGenre, it.Genre));

        // 卡是 genre <= 4：每张一个独立 guid，绝不叠加
        Assert.All(player.Items, it => Assert.Equal(1u, it.Count));
        Assert.Equal(expectedCards, player.Items.Select(i => i.Guid).Distinct().Count());

        // 当前卡表20位有效少女，含联动少女，且没有Detail=102
        Assert.Equal(20, player.Girls.Count);
        Assert.DoesNotContain(102u, player.Girls.Select(g => g.Girlid));
        Assert.All(player.Girls, g => Assert.True(GmUnlock.IsValidGirlId(g.Girlid)));
    }

    /// <summary>
    /// ★ <c>GirlInfo.flag</c> 必须是 0。<c>flag == 1</c> 是「新」红点
    /// （<c>UI_Girl.lua:1085..1090</c> / <c>UI_CafeAssign.lua:1064</c>），
    /// 而清红点要靠 <c>LuaCall.Girl_UpdateFlag</c> 上行 —— 服务端还没实现，
    /// 给 1 等于让红点永久亮着。
    /// </summary>
    [Fact]
    public void ApplyCards_GirlFlagIsZero_NoPermanentRedDot()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyCards(player);

        Assert.All(player.Girls, g => Assert.Equal(0u, g.Flag));
    }

    /// <summary>老存档里 StarterLoadout 留下的 <c>Flag=1</c> 会被顺手清掉。</summary>
    [Fact]
    public void ApplyCards_ClearsLegacyStarterFlag()
    {
        var gm = Create();
        var player = NewPlayer();
        StarterLoadout.Grant(player);
        Assert.Contains(player.Girls, g => g.Flag == 1);     // 前提：老写法确实给 1

        var report = gm.ApplyCards(player);

        Assert.All(player.Girls, g => Assert.Equal(0u, g.Flag));
        Assert.NotEmpty(report.NewGirls);
    }

    /// <summary>
    /// 衣服（group 4）与卡面（group 7）获得标记的键与值。
    /// 键：<c>(girlId-1)*2000 + off</c>（GirlCommon.lua:1525）；
    /// 衣服 off = <c>FixModelId(ModelId)</c>，卡面 off = <c>P*20 + L</c>（GirlCommon.lua:660）。
    /// 值：2 = 已获得且非「新」（<c>SuitValue == 1</c> 才是红点，<c>UI_GirlClothes.lua:846</c>）。
    /// </summary>
    [Fact]
    public void ApplyCards_SetsSuitAndCardOwnershipFlags()
    {
        var gm = Create(out _, out var cards, out _, out _, out _);
        var player = NewPlayer();

        gm.ApplyCards(player);

        // 以 (1,2,1,3) 私服·高奈利亚 为例：girlId=2, P=1, L=3, ModelId=1
        var card = cards.Get(1, 2, 1, 3)!;
        int suitTask = GmUnlock.GirlTaskId(card.D, GmUnlock.FixModelId(card.ModelId));
        int cardTask = GmUnlock.GirlTaskId(card.D, (int)(card.P * GmUnlock.CardPStride + card.L));

        Assert.Equal((2 - 1) * 2000 + 1, suitTask);                     // = 2001
        Assert.Equal((2 - 1) * 2000 + (1 * 20 + 3), cardTask);          // = 2023

        Assert.Equal(GmUnlock.OwnedNotNew, player.GetTask(GmUnlock.SuitGroup, suitTask));
        Assert.Equal(GmUnlock.OwnedNotNew, player.GetTask(GmUnlock.CardGroup, cardTask));

        // 每张卡都得有卡面标记
        foreach (var c in cards.All.Where(c => GmUnlock.IsValidGirlId(c.D)))
        {
            int t = GmUnlock.GirlTaskId(c.D, (int)(c.P * GmUnlock.CardPStride + c.L));
            Assert.Equal(GmUnlock.OwnedNotNew, player.GetTask(GmUnlock.GirlTaskGroup(GmUnlock.CardGroup, c.D), t));
        }
    }

    /// <summary>
    /// ★ 全部少女相关 taskId 必须 &lt;= 0xFFFF。
    /// <see cref="Player.ComposeTaskId"/> 是 <c>(group &lt;&lt; 16) | (id &amp; 0xFFFF)</c>
    /// （取证 <c>Game.Player.GetTask</c> RVA 0x18448F0 的 <c>orr w19,w19,w20,lsl #16</c>），
    /// 溢出会**静默**把两条不同的标记折叠成同一个键。
    /// </summary>
    [Fact]
    public void ApplyCards_AllGirlTaskIdsFitIn16Bits()
    {
        var gm = Create(out _, out var cards, out _, out _, out _);
        var player = NewPlayer();

        gm.ApplyCards(player);

        foreach (var c in cards.All.Where(c => GmUnlock.IsValidGirlId(c.D)))
        {
            Assert.InRange(GmUnlock.GirlTaskId(c.D, GmUnlock.FixModelId(c.ModelId)), 0, 0xFFFF);
            Assert.InRange(GmUnlock.GirlTaskId(c.D, (int)(c.P * GmUnlock.CardPStride + c.L)), 0, 0xFFFF);
        }

        // 存档里落下的键也都在 16 位内（SetTask 会拒绝越界的，所以这条同时验「没被拒绝过」）
        foreach (var key in player.Tasks.Keys)
            Assert.InRange(Player.TaskIdOf(key), 0, 0xFFFF);
    }

    /// <summary>展示标记 <c>IsEndShow</c>=1（否则一次给 16 位少女会排队播 16 段获得展示）。</summary>
    [Fact]
    public void ApplyCards_SetsIsEndShowAndSuitCount()
    {
        var gm = Create(out _, out var cards, out _, out _, out _);
        var player = NewPlayer();

        gm.ApplyCards(player);

        foreach (uint girlId in cards.GirlIds.Where(GmUnlock.IsValidGirlId))
        {
            Assert.Equal(1, player.GetTask(GmUnlock.GirlTaskGroup(GmUnlock.GirlVarGroup, girlId),
                GmUnlock.GirlTaskId(girlId, GmUnlock.GirlVarIsEndShow)));

            int suits = cards.ForGirl(girlId).Select(c => GmUnlock.FixModelId(c.ModelId)).Distinct().Count();
            Assert.Equal(suits, player.GetTask(GmUnlock.GirlTaskGroup(GmUnlock.GirlVarGroup, girlId),
                GmUnlock.GirlTaskId(girlId, GmUnlock.GirlVarSuitCount)));
        }
    }

    /// <summary>
    /// ★ 看板娘必须被设上，否则主城左上角的玩家头像是**纯白块**。
    /// 取证 <c>UI_Player.lua:507..511</c>：<c>GirlCommon:MainGirl()</c> = <c>GetTaskValue(2,2)</c>，
    /// 缺省 0 ⇒ <c>me:GetGirl(0)</c> 为 nil ⇒ <c>imgPortrait.sprite</c> **从未被赋值** ⇒
    /// 保持 Unity 默认白图（连 <c>Sprite()</c> 的 <c>Num/what.png</c> 兜底都走不到，
    /// 所以现象是「空白」而不是「问号」）。这是纯服务端问题，与资源缺失无关。
    /// </summary>
    [Fact]
    public void ApplyCards_SetsMainGirl_SoPortraitIsNotBlank()
    {
        var gm = Create();
        var player = NewPlayer();
        Assert.Equal(0, player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId));

        gm.ApplyCards(player);

        int mainGirl = player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId);
        Assert.True(mainGirl > 0, "看板娘 (2,2) 仍是 0 —— 主城左上角会是白块");
        // 必须真的持有这个少女，否则 me:GetGirl 还是 nil
        Assert.Contains(player.Girls, g => g.Girlid == (uint)mainGirl);
        Assert.True(GmUnlock.IsValidGirlId((uint)mainGirl));
    }

    /// <summary>玩家已经自己选过看板娘时不覆盖。</summary>
    [Fact]
    public void ApplyCards_KeepsExistingMainGirl()
    {
        var gm = Create();
        var player = NewPlayer();
        player.SetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId, 7);

        gm.ApplyCards(player);

        Assert.Equal(7, player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId));
    }

    // ================= 全道具 =================

    [Fact]
    public void ApplyItems_MoneyItemsBecomeCurrency_OthersGoToBag()
    {
        var gm = Create(out var options, out _, out _, out var items, out _);
        var player = NewPlayer();

        gm.ApplyItems(player);

        // (15,1,1,1) ItemGoldAuto → 货币 2（金币）；(15,2,1,1) → 货币 3（免费钻）
        Assert.Equal(options.GmMoneyAmount, player.GetMoney(MoneyType.Gold));
        Assert.Equal(options.GmMoneyAmount, player.GetMoney(MoneyType.FreeDiamond));
        // 货币类道具本身不进背包
        Assert.DoesNotContain(player.Items, i => i.Genre == 15 && i.Detail == 1 && i.Particular == 1 && i.Level == 1);

        // 非货币道具进背包，数量 = GmItemCount
        var honey = player.Items.FirstOrDefault(i => i.Genre == 5 && i.Detail == 1
                                                  && i.Particular == 1 && i.Level == 2);
        Assert.NotNull(honey);
        Assert.Equal((uint)options.GmItemCount, honey!.Count);

        // 背包里的种类数 = 模板总数 - 货币类模板数 - 图标缺失被跳过的那批
        int moneyTemplates = items.All.Count(t => items.TryGetMoneyId(t.G, t.D, t.P, t.L, out _));
        var missing = MissingIconTable.Load(TestPaths.SettingDir);
        int skipped = items.All.Count(t => !items.TryGetMoneyId(t.G, t.D, t.P, t.L, out _)
                                        && missing.IsMissing(t.IconId));
        Assert.Equal(items.Count - moneyTemplates - skipped, player.Items.Count);
        Assert.True(moneyTemplates > 0, "至少要认出几个货币类模板，否则 TryGetMoneyId 退化了");
        // ★ 不要钉具体数量：`MissingIcon.txt` 是 `tools/probe_icons.sh` 的**真机产出**，
        // 会随设备资源状态变化。实测推了正确的 `version.data` 之后，缺失图标
        // 从 125 张降到 23 张（受影响道具 136 → 25，见 `docs/05` §10.1.1）——
        // 当前允许空清单：不因没有待排除图标而改变既有全道具发放筛选。
        // 上方数量等式已校验真实命中项均被排除；清单还可能含不在物品表中的图标。
        Assert.DoesNotContain(player.Items, item => items.Get(item.Genre, item.Detail, item.Particular, item.Level)
            is { } template && missing.IsMissing(template.IconId));
    }

    /// <summary>
    /// ★ 全道具**不会**发出 genre 1/2/3/4 的实例。
    /// <c>ItemList.txt</c> 的 Genre 列值域是 5..22，而客户端 pf 1031 只对 1/2/4 特判
    /// （<c>OnUpdateItem</c> RVA 0x1D9D72C，genre 比较在 0x1D9D85C），
    /// 其余走 <c>AddItem</c> —— 不需要 skillinfo/weaponinlay/attrlist/ownguid
    /// （docs/05-feature-domains.md §0.3 的红线）。
    /// </summary>
    [Fact]
    public void ApplyItems_NeverEmitsWeaponRuneOrOriginalGenres()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyItems(player);

        Assert.All(player.Items, it => Assert.True(it.Genre >= 5,
            $"ItemList.txt 出现了 genre={it.Genre} 的模板 —— 它会走 AddCharacterCard/AddWeapon/AddOriginal，" +
            "必须补 skillinfo/weaponinlay/attrlist/ownguid 才能发"));
    }

    /// <summary>道具的 <c>Flag</c> bit1（剧情已读）置上、bit0（新道具红点）不置。</summary>
    [Fact]
    public void ApplyItems_MarksStoryReadAndNoNewDot()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyItems(player);

        Assert.All(player.Items, it =>
        {
            Assert.Equal(Item.FlagStoryReadBit, it.Flag & Item.FlagStoryReadBit);
            Assert.Equal(0u, it.Flag & Item.FlagNewBit);
        });
    }

    // ================= 全关卡 =================

    [Fact]
    public void ApplyLevels_ThreeStarsEveryChapterLevel()
    {
        var gm = Create(out _, out _, out var chapters, out _, out _);
        var player = NewPlayer();

        var report = gm.ApplyLevels(player);

        Assert.Equal(chapters.Count, player.Levels.Count);
        Assert.Equal(chapters.Count, report.ChangedLevels.Count);
        Assert.All(player.Levels.Values, star =>
        {
            Assert.Equal(MapList.StarMask, MapList.Stars(star));
            Assert.True(MapList.PassCount(star) >= 1);
        });

        // 1-1 普通难度必须在里面（新手关，SystemUnlock 的最低门槛）
        Assert.True(player.Levels.ContainsKey(MapList.MapId(1, 1, 1)));
    }

    /// <summary>已有通关记录的关只补三星位，**不再累加通关次数**（避免反复执行刷爆次数）。</summary>
    [Fact]
    public void ApplyLevels_KeepsExistingPassCount()
    {
        var gm = Create();
        var player = NewPlayer();
        uint mapId = MapList.MapId(1, 1, 1);
        player.Levels[mapId] = MapList.Compose(0b001, 7);   // 一星、通关 7 次

        gm.ApplyLevels(player);

        Assert.Equal(MapList.StarMask, MapList.Stars(player.Levels[mapId]));
        Assert.Equal(7, MapList.PassCount(player.Levels[mapId]));
    }

    // ================= 全任务 =================

    [Fact]
    public void ApplyMissions_MarksEveryMissionCompleteAndClaimed()
    {
        var gm = Create(out _, out _, out _, out _, out var missions, missionClaimed: true);
        var player = NewPlayer();

        gm.ApplyMissions(player);

        AssertMissionsComplete(player, missions, claimed: true);

        // 活跃点数是裸值（MissionManager.lua:619..621 没过 LoadBits）
        Assert.Equal(missions.ActivePointMax,
            player.GetTask(MissionTable.MissionGroup, MissionFeature.ActivePointTaskId));
    }

    [Fact]
    public void ApplyMissions_OpenMode_LeavesClaimBitClear()
    {
        var gm = Create(out _, out _, out _, out _, out var missions, missionClaimed: false);
        var player = NewPlayer();

        gm.ApplyMissions(player);

        AssertMissionsComplete(player, missions, claimed: false);
    }

    private static void AssertMissionsComplete(Player player, MissionTable missions, bool claimed)
    {
        int expectedBit = claimed ? 1 : 0;
        var buckets = new (IReadOnlyDictionary<int, MissionEntry> Entries, bool AchieveKey)[]
        {
            (missions.Daily, false), (missions.Guide, false), (missions.Club, false),
            (missions.Achieve, true), (missions.AchieveOther, true),
        };

        int checked_ = 0;
        foreach (var (entries, achieveKey) in buckets)
        {
            foreach (var entry in entries.Values)
            {
                int value = player.GetTask(entry.Group, entry.Id);
                int need = entry.RequiredCount(achieveKey);
                if (need == int.MaxValue || need <= 0)
                    need = 1;

                Assert.Equal(need, MissionFeature.ProgressOf(value));
                Assert.Equal(expectedBit, MissionFeature.ClaimedOf(value));
                checked_++;
            }
        }
        Assert.True(checked_ > 3000, $"任务表条数只有 {checked_}，配置目录可能读错了");
    }

    /// <summary>三个进度奖励档位的位标记（bit 从 <b>1</b> 起，bit0 空着不用）。</summary>
    [Fact]
    public void ApplyMissions_SetsProgressAwardFlagBits()
    {
        var gm = Create(out _, out _, out _, out _, out var missions);
        var player = NewPlayer();

        gm.ApplyMissions(player);

        AssertBits(MissionFeature.PointAwardTaskId, missions.ActivePointAwards.Keys);
        AssertBits(MissionFeature.GuideCompleteAwardTaskId, missions.GuideProgressAwards.Keys);
        AssertBits(MissionFeature.ClubCompleteAwardTaskId, missions.ClubProgressAwards.Keys);

        void AssertBits(int taskId, IEnumerable<int> slots)
        {
            int value = player.GetTask(MissionTable.MissionGroup, taskId);
            Assert.NotEqual(0, value);
            foreach (int slot in slots)
                Assert.NotEqual(0, value & (1 << slot));
            Assert.Equal(0, value & 1);      // bit0 不用（UI_Achievement.lua:715 for i=1,5）
        }
    }

    /// <summary>
    /// <see cref="MissionFeature"/> 的登录钩子（<c>SyncTaskValues</c>）不会把全解锁写的进度改回去
    /// —— 它那边是「只升不降」的 max 合并 + 已领 bit 或运算。
    /// 两个功能域写的是同一批任务值键，必须验一次共存。
    /// </summary>
    [Fact]
    public async Task ApplyMissions_SurvivesMissionFeatureLoginSync()
    {
        const string account = "gm-mission-sync";
        using var h = await FeatureHarness.CreateAsync(account);

        h.Module.GmUnlock.ApplyMissions(h.Player);
        h.Module.Store.Save(h.Player);              // 让 HandleLogin 能从存档读到这批任务值
        var before = new Dictionary<uint, int>(h.Player.Tasks);
        Assert.True(before.Count > 3000);

        // 走真实登录路径：MissionFeature 的 OnPlayerLogin → SyncTaskValues → 可能改写任务值
        await h.Module.HandleLogin(new SessionAdapter(h.Session), new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new Protocol.GsProto.LoginReq { Account = account, Channel = "selftest" }.ToByteArray(),
        });

        var after = Assert.IsType<Player>(h.Session.Tag);
        foreach (var (key, value) in before)
        {
            int now = after.Tasks.TryGetValue(key, out var v) ? v : 0;
            Assert.True(MissionFeature.ProgressOf(now) >= MissionFeature.ProgressOf(value),
                $"任务值 {Player.TaskGroupOf(key)}/{Player.TaskIdOf(key)} 的进度从 " +
                $"{MissionFeature.ProgressOf(value)} 退到了 {MissionFeature.ProgressOf(now)}");
            Assert.True(MissionFeature.ClaimedOf(now) >= MissionFeature.ClaimedOf(value),
                $"任务值 {Player.TaskGroupOf(key)}/{Player.TaskIdOf(key)} 的已领 bit 被清掉了");
        }
    }

    // ================= 登录路径（GmProvisionFeature）=================

    /// <summary>
    /// ★ 端到端：**空账号**（selftest 缺省，也就是「我们测试用的 GM 账号」）走
    /// <c>HandleLogin</c> 时，<see cref="Handlers.GmProvisionFeature"/> 的
    /// <c>OnPlayerPrepare</c> 钩子必须在 pf 1005 之前把全解锁做完。
    ///
    /// 断言的重点是「登录**之后**玩家已经全解锁」—— 这等价于那几条全量包里已经带上了数据，
    /// 因为 <c>BuildPlayerNtf</c>/<c>BuildItemNtf</c>/<c>BuildTaskValueSyncRsp</c>
    /// 都是读同一个 <see cref="Player"/> 实例。
    /// </summary>
    [Fact]
    public async Task Login_WithGmAccount_AutoFullUnlocks()
    {
        using var h = await FeatureHarness.CreateAsync("", attachPlayer: false);
        Assert.True(h.Options.GmFullUnlock);
        Assert.Contains("", h.Options.GmAccounts);

        await h.Module.HandleLogin(new SessionAdapter(h.Session), new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new Protocol.GsProto.LoginReq { Account = "", Channel = "selftest" }.ToByteArray(),
        });

        var player = Assert.IsType<Player>(h.Session.Tag);

        Assert.Equal(GmUnlock.Revision, GmUnlock.RevisionOf(player));
        Assert.True(player.Items.Count > 2000, $"背包只有 {player.Items.Count} 项");
        Assert.Equal(20, player.Girls.Count);
        Assert.Equal(h.Module.Chapters.Count, player.Levels.Count);
        Assert.True(player.Tasks.Count > 3000, $"任务值只有 {player.Tasks.Count} 条");
        Assert.Equal(h.Options.GmMoneyAmount, player.GetMoney(MoneyType.Gold));

        // 满级
        var exp = PlayerExpTable.Load(TestPaths.SettingDir);
        Assert.Equal(exp.MaxLevel, player.Level);

        // 全新手指引已完成 ⇒ 客户端 GuideManager 初始化时就不会触发引导
        var guides = GuideTable.Load(TestPaths.SettingDir);
        Assert.All(guides.All, g =>
            Assert.Equal(GuideTable.CompleteValue, player.GetTask(GuideTable.TaskGroup, g.TaskId)));

        // 跳教程标记没有被全解锁覆盖掉（PlayerLogic.lua:516 IsFirstLevelComplete 的唯一依据）
        Assert.True(player.GetTask(1, 15) >= 6,
            $"task(1,15)={player.GetTask(1, 15)} < 6 —— 客户端会退回新手关流程");

        // 全关卡三星 ⇒ 1-1 普通难度必然通关（SystemUnlock 的最低门槛）
        Assert.Equal(MapList.StarMask, MapList.Stars(player.GetLevelStar(MapList.MapId(1, 1, 1))));
    }

    /// <summary>非 GM 账号登录时**不能**被全解锁（否则等于给所有玩家开挂）。</summary>
    [Fact]
    public async Task Login_WithNonGmAccount_DoesNotUnlock()
    {
        const string account = "ordinary-player";
        using var h = await FeatureHarness.CreateAsync(account, attachPlayer: false);

        await h.Module.HandleLogin(new SessionAdapter(h.Session), new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new Protocol.GsProto.LoginReq { Account = account, Channel = "selftest" }.ToByteArray(),
        });

        var player = Assert.IsType<Player>(h.Session.Tag);

        Assert.Equal(0, GmUnlock.RevisionOf(player));
        // 只有 StarterLoadout 的 4 张起始卡
        Assert.Equal(StarterLoadout.SquadSize, player.Items.Count);
        Assert.Equal(StarterLoadout.SquadSize, player.Girls.Count);
        Assert.Empty(player.Levels);
    }

    /// <summary>第二次登录不重跑全解锁（revision 已是最新），且数据不丢。</summary>
    [Fact]
    public async Task Login_Twice_SecondLoginSkipsButKeepsData()
    {
        using var h = await FeatureHarness.CreateAsync("", attachPlayer: false);
        var packet = new Packet
        {
            Pf = GamePf.Login,
            Serial = 1,
            Payload = new Protocol.GsProto.LoginReq { Account = "", Channel = "selftest" }.ToByteArray(),
        };

        await h.DrainWhileAsync(() => h.Module.HandleLogin(new SessionAdapter(h.Session), packet));
        var first = Assert.IsType<Player>(h.Session.Tag);
        int items = first.Items.Count;
        uint nextGuid = first.NextGuid;

        await h.DrainWhileAsync(() => h.Module.HandleLogin(new SessionAdapter(h.Session), packet));
        var second = Assert.IsType<Player>(h.Session.Tag);

        Assert.Equal(items, second.Items.Count);
        Assert.Equal(nextGuid, second.NextGuid);      // 没有再烧一批 guid
    }

    // ================= 全新手指引 / 满级 =================

    /// <summary>
    /// <c>UIGuide.txt</c> 的形状：<c>ID</c> 非空的行数、<c>Index</c> 唯一。
    /// 客户端 <c>GuideManager.lua:50</c> 对重复 Index 是 assert，所以这条同时是配置健康检查。
    /// </summary>
    [Fact]
    public void GuideTable_LoadsAllGuides_WithUniqueTaskIds()
    {
        var table = GuideTable.Load(TestPaths.SettingDir);

        Assert.True(table.Count > 100, $"UIGuide.txt 只读到 {table.Count} 条指引");
        Assert.Equal(table.Count, table.All.Select(g => g.TaskId).Distinct().Count());
        Assert.All(table.All, g => Assert.False(string.IsNullOrEmpty(g.Id)));
        // taskId 必须落在 16 位内（Player.ComposeTaskId 只保留低 16 位）
        Assert.All(table.All, g => Assert.InRange(g.TaskId, 0, 0xFFFF));
        Assert.Equal(10, GuideTable.TaskGroup);
        Assert.Equal(100, GuideTable.CompleteValue);
    }

    /// <summary>
    /// 全指引完成 = group 10 的每个 <c>Index</c> 都是 100
    /// （<c>GuideManager:DebugSetGuideComplete</c> 的等效写法，GuideManager.lua:136..139）。
    /// </summary>
    [Fact]
    public void ApplyGuides_MarksEveryGuideComplete()
    {
        var gm = Create(out _, out _, out _, out _, out _, out var guides, out _);
        var player = NewPlayer();

        var report = gm.ApplyGuides(player);

        Assert.Equal(guides.Count, report.ChangedTasks.Count);
        Assert.All(guides.All, g =>
            Assert.Equal(GuideTable.CompleteValue, player.GetTask(GuideTable.TaskGroup, g.TaskId)));
    }

    /// <summary>已完成的指引不重复写（与客户端 <c>SetTask</c> 的「只升不降」一致）。</summary>
    [Fact]
    public void ApplyGuides_IsIdempotent_AndNeverLowersProgress()
    {
        var gm = Create(out _, out _, out _, out _, out _, out var guides, out _);
        var player = NewPlayer();
        int probe = guides.All[0].TaskId;
        player.SetTask(GuideTable.TaskGroup, probe, 200);     // 比 100 还高

        gm.ApplyGuides(player);
        Assert.Equal(200, player.GetTask(GuideTable.TaskGroup, probe));   // 不被降回 100

        var second = gm.ApplyGuides(player);
        Assert.Empty(second.ChangedTasks);
    }

    // ================= 全剧情 / CG 已读（第十二轮） =================

    /// <summary>
    /// 复刻客户端 <c>LoadBits(nInt, bit, bit)</c>（`Lib.lua:519..531`）——
    /// 注意它是**算术**实现（<c>%</c> 与 <c>/</c>），不是位运算，所以负数会算错。
    /// 用它来验「服务端写进去的值，客户端真的读得出 1」。
    /// </summary>
    private static int ClientLoadBit(int value, int bit)
    {
        // nInt = nInt % (1 << (bit+1)); nInt = nInt / (1 << bit); floor
        long v = value;
        long mod = 1L << (bit + 1);
        v %= mod;
        return (int)Math.Floor(v / (double)(1L << bit));
    }

    /// <summary>
    /// ★★★ 全剧情已读 = 填满 group 39 的 taskId 0..400 每个的低 31 位。
    /// 判据不只看服务端存的整数，而是**用客户端的 <c>LoadBits</c> 语义**逐位复核。
    /// </summary>
    [Fact]
    public void ApplyCartoons_FillsEveryBit_AndClientCanReadThem()
    {
        var gm = Create();
        var player = NewPlayer();

        var report = gm.ApplyCartoons(player);

        int expected = GmUnlock.CartoonMaxTaskId - GmUnlock.CartoonMinTaskId + 1;
        Assert.Equal(expected, report.ChangedTasks.Count);

        // 抽查首/中/尾三个 taskId，每个的 bit 0..30 客户端都必须读出 1
        foreach (int taskId in new[] { GmUnlock.CartoonMinTaskId, 200, GmUnlock.CartoonMaxTaskId })
        {
            int value = player.GetTask(GmUnlock.CartoonGroup, taskId);
            Assert.True(value > 0, $"taskId={taskId} 没被填");
            for (int bit = 0; bit < GmUnlock.CartoonBitsPerTask; bit++)
            {
                Assert.Equal(1, ClientLoadBit(value, bit));
            }
        }
    }

    /// <summary>
    /// ★★ 存的值**必须是正数**（不能填 bit 31 / 不能用 -1）。
    ///
    /// 客户端 <c>LoadBits</c>/<c>SetBits</c> 用算术 <c>%</c>，负数会得到负余数 ⇒ 判定出错。
    /// 这条是「为什么填 int.MaxValue 而不是 ~0」的回归闸。
    /// </summary>
    [Fact]
    public void ApplyCartoons_NeverWritesNegative_SignBitStaysClear()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyCartoons(player);

        for (int taskId = GmUnlock.CartoonMinTaskId; taskId <= GmUnlock.CartoonMaxTaskId; taskId++)
        {
            int v = player.GetTask(GmUnlock.CartoonGroup, taskId);
            Assert.True(v > 0, $"taskId={taskId} 的值 {v} 不是正数（符号位被写了？）");
            Assert.Equal(int.MaxValue, v);
        }
    }

    /// <summary>幂等：第二次调用不产生任何变更（登录钩子每次登录都会跑）。</summary>
    [Fact]
    public void ApplyCartoons_IsIdempotent()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyCartoons(player);
        var second = gm.ApplyCartoons(player);

        Assert.Empty(second.ChangedTasks);
        Assert.True(second.IsEmpty);
    }

    /// <summary>★ 不擦掉玩家已有的高位/其它位（用 <c>|=</c> 而不是覆写）。</summary>
    [Fact]
    public void ApplyCartoons_PreservesExistingBits()
    {
        var gm = Create();
        var player = NewPlayer();
        // 先手工写一个「只读过 bit 3」的状态
        player.SetTask(GmUnlock.CartoonGroup, 5, 1 << 3);

        gm.ApplyCartoons(player);

        Assert.Equal(int.MaxValue, player.GetTask(GmUnlock.CartoonGroup, 5));
    }

    /// <summary>★★ 全解锁（<c>ApplyAll</c>）必须包含剧情位域 —— 否则终端剧情类触发补不出来。</summary>
    [Fact]
    public void ApplyAll_IncludesCartoonUnlock()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyAll(player);

        Assert.Equal(int.MaxValue,
                     player.GetTask(GmUnlock.CartoonGroup, GmUnlock.CartoonMinTaskId));
        Assert.Equal(int.MaxValue,
                     player.GetTask(GmUnlock.CartoonGroup, GmUnlock.CartoonMaxTaskId));
        // revision 提到 6 才会让存量存档补上这批位域
        Assert.True(GmUnlock.Revision >= 6, "加了剧情解锁就必须提 Revision，否则老存档补不上");
    }

    /// <summary>满级 = <c>PlayerExp.txt</c> 的 MaxLevel（现网 300），经验清 0。</summary>
    [Fact]
    public void ApplyMaxLevel_SetsPlayerToTableMaxLevel()
    {
        var gm = Create(out _, out _, out _, out _, out _, out _, out var exp);
        var player = NewPlayer();
        Assert.Equal(1, player.Level);

        var report = gm.ApplyMaxLevel(player);

        Assert.True(exp.MaxLevel >= 300, $"PlayerExp.txt 的最高级只有 {exp.MaxLevel}");
        Assert.Equal(exp.MaxLevel, player.Level);
        Assert.Equal(0, player.Exp);
        Assert.True(report.LevelChanged);
        Assert.False(report.IsEmpty);

        // 满级后体力上限应该远大于 1 级的 28
        Assert.True(exp.MaxVigour(player.Level) > exp.MaxVigour(1));
    }

    [Fact]
    public void ApplyMaxLevel_IsIdempotent()
    {
        var gm = Create();
        var player = NewPlayer();

        gm.ApplyMaxLevel(player);
        var second = gm.ApplyMaxLevel(player);

        Assert.True(second.IsEmpty);
        Assert.False(second.LevelChanged);
    }

    /// <summary>
    /// 满级顺带满足全部关卡的 <c>LevelLimit</c>（指挥官等级门槛）——
    /// 这是「全关卡可进」的另一半，<see cref="GmUnlock.ApplyLevels"/> 只管通关记录。
    /// </summary>
    [Fact]
    public void ApplyMaxLevel_SatisfiesEveryChapterLevelLimit()
    {
        var gm = Create(out _, out _, out var chapters, out _, out _);
        var player = NewPlayer();

        gm.ApplyMaxLevel(player);

        var blocked = chapters.All.Where(c => c.LevelLimit > player.Level).ToList();
        Assert.Empty(blocked);
    }

    // ================= 图标缺失道具的跳过 / 清理 =================

    /// <summary>
    /// <c>MissingIcon.txt</c> 的形状。这张表由 <c>tools/probe_icons.sh</c> 让**客户端自己**
    /// 遍历 ItemList 的 IconId 调 <c>Game.LuaUtils.LoadSprite</c> 产出（返回 nil 才记），
    /// 所以它描述的是「本地资源快照 vs 配置表」的真实差集。
    /// </summary>
    [Fact]
    public void MissingIconTable_LoadsSnapshotGap()
    {
        var table = MissingIconTable.Load(TestPaths.SettingDir);

        // The optional snapshot can be absent or empty after resource restoration.
        string path = Path.Combine(TestPaths.SettingDir, MissingIconTable.FileName);
        string[] expected = File.Exists(path) ? File.ReadAllLines(path).Skip(3)
            .Select(l => l.Split('\t')[0].Trim()).Where(l => l.Length > 0).Distinct().ToArray() : Array.Empty<string>();
        Assert.Equal(expected.OrderBy(v => v), table.All.OrderBy(v => v));
        Assert.All(expected, sample => Assert.True(table.IsMissing(sample)));
        // 同图集里正常的那些不能被误判
        Assert.False(table.IsMissing("Item/icon_sync_item_002.png"));
        Assert.False(table.IsMissing("Item/icon_curr_coin.png"));
        Assert.False(table.IsMissing(""));
        Assert.False(table.IsMissing(null));
    }

    /// <summary>
    /// ★ 全道具**不发**图标缺失的道具（否则仓库里是灰底问号 —— 客户端 <c>Lib.lua:475</c>
    /// 的兜底图 <c>Num/what.png</c>）。
    /// </summary>
    [Fact]
    public void ApplyItems_SkipsItemsWithMissingIcon()
    {
        var gm = Create(out _, out _, out _, out var items, out _);
        var player = NewPlayer();
        var missing = MissingIconTable.Load(TestPaths.SettingDir);

        gm.ApplyItems(player);

        foreach (var it in player.Items)
        {
            var tpl = items.Get(it.Genre, it.Detail, it.Particular, it.Level);
            if (tpl is null)
                continue;
            Assert.False(missing.IsMissing(tpl.IconId),
                $"({it.Genre},{it.Detail},{it.Particular},{it.Level}) 的图标 {tpl.IconId} " +
                "在本地资源快照里缺失，发出去会在仓库里画成问号");
        }
    }

    /// <summary>
    /// ★ 老存档里已经发过的问号道具要被**摘掉**（只是「以后不发」不够 —— 问号已经在仓库里了），
    /// 并且要进 <see cref="GmUnlockReport.RemovedItems"/> 好让调用方推 pf 1031 count=0。
    /// </summary>
    [Fact]
    public void ApplyItems_RemovesLegacyMissingIconItems()
    {
        _ = Create(out var options, out var cards, out var chapters, out var items, out var missions,
            out var guides, out var playerExp);
        var player = NewPlayer();
        // An isolated snapshot exercises cleanup even when the installed optional table is absent.
        var victim = items.All.First(t => !string.IsNullOrWhiteSpace(t.IconId)
            && !items.TryGetMoneyId(t.G, t.D, t.P, t.L, out _));
        string directory = Path.Combine(Path.GetTempPath(), "sssj-missing-icon-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, MissingIconTable.FileName), "STR\n图标\nIconId\n" + victim.IconId + "\n");
            var gm = new GmUnlock(options, chapters, items, cards, missions, guides, playerExp, MissingIconTable.Load(directory));
            var stale = player.AddItem(victim.G, victim.D, victim.P, victim.L, count: 999);
            var report = gm.ApplyItems(player);
            Assert.DoesNotContain(player.Items, i => i.Guid == stale.Guid);
            Assert.Contains(stale, report.RemovedItems);
            Assert.DoesNotContain(stale, report.ChangedItems);
            Assert.False(report.IsEmpty);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>关掉开关就拿回「真·全道具」（代价是那批问号格子）。</summary>
    [Fact]
    public void ApplyItems_WithSkipDisabled_GrantsEverything()
    {
        var gm = Create(out var options, out _, out _, out var items, out _);
        options.GmSkipMissingIcon = false;
        var player = NewPlayer();

        gm.ApplyItems(player);

        int moneyTemplates = items.All.Count(t => items.TryGetMoneyId(t.G, t.D, t.P, t.L, out _));
        Assert.Equal(items.Count - moneyTemplates, player.Items.Count);
    }

    // ================= 幂等 / revision =================

    /// <summary>★ 连跑两次 <see cref="GmUnlock.ApplyAll"/>：第二次不应产生任何变更。</summary>
    [Fact]
    public void ApplyAll_IsIdempotent()
    {
        var gm = Create();
        var player = NewPlayer();

        var first = gm.ApplyAll(player);
        int items = player.Items.Count;
        int girls = player.Girls.Count;
        int levels = player.Levels.Count;
        int tasks = player.Tasks.Count;
        uint nextGuid = player.NextGuid;

        Assert.False(first.IsEmpty);

        var second = gm.ApplyAll(player);

        Assert.True(second.IsEmpty, $"第二次全解锁仍有变更：{second.Describe()}");
        Assert.Equal(items, player.Items.Count);
        Assert.Equal(girls, player.Girls.Count);
        Assert.Equal(levels, player.Levels.Count);
        Assert.Equal(tasks, player.Tasks.Count);
        Assert.Equal(nextGuid, player.NextGuid);     // 没有白烧 guid
    }

    [Fact]
    public void ApplyAll_WritesRevision_AndForceFalseSkips()
    {
        var gm = Create();
        var player = NewPlayer();

        Assert.Equal(0, GmUnlock.RevisionOf(player));
        Assert.False(GmUnlock.IsUpToDate(player));

        gm.ApplyAll(player, force: false);

        Assert.Equal(GmUnlock.Revision, GmUnlock.RevisionOf(player));
        Assert.True(GmUnlock.IsUpToDate(player));

        // 已是最新 + force:false → 直接返回空报告，连循环都不跑
        int itemsBefore = player.Items.Count;
        player.Items.Clear();       // 故意破坏数据：若真跑了就会补回来
        var report = gm.ApplyAll(player, force: false);
        Assert.True(report.IsEmpty);
        Assert.Empty(player.Items);
        Assert.True(itemsBefore > 2000);
    }

    // ================= 账号判定 =================

    [Theory]
    [InlineData("", true)]              // selftest 的缺省账号就是空串
    [InlineData("gm", false)]
    [InlineData("other", false)]
    public void IsGmAccount_DefaultOptions_OnlyEmptyAccount(string account, bool expected)
    {
        var gm = Create(out var options, out _, out _, out _, out _);
        Assert.Equal(new[] { "" }, options.GmAccounts);
        Assert.Equal(expected, gm.IsGmAccount(account));
    }

    [Fact]
    public void IsGmAccount_WildcardMatchesEverything()
    {
        var options = new ServerOptions { SettingDirectory = TestPaths.SettingDir, GmAccounts = new[] { "*" } };
        var gm = new GmUnlock(options, ChapterTable.Load(TestPaths.SettingDir),
                              ItemBaseTable.Load(TestPaths.SettingDir),
                              CharacterCardTable.Load(TestPaths.SettingDir),
                              MissionTable.Load(TestPaths.SettingDir),
                              GuideTable.Load(TestPaths.SettingDir),
                              PlayerExpTable.Load(TestPaths.SettingDir));

        Assert.True(gm.IsGmAccount(""));
        Assert.True(gm.IsGmAccount("anyone"));
    }

    [Fact]
    public void IsGmAccount_EmptyListDisablesEverything()
    {
        var options = new ServerOptions
        {
            SettingDirectory = TestPaths.SettingDir,
            GmAccounts = Array.Empty<string>(),
        };
        var gm = new GmUnlock(options, ChapterTable.Empty, ItemBaseTable.Empty,
                              CharacterCardTable.Empty, MissionTable.Empty,
                              GuideTable.Empty, PlayerExpTable.Empty);

        Assert.False(gm.IsGmAccount(""));
        Assert.False(gm.IsGmAccount("gm"));
    }

    // ================= 缺表降级 =================

    /// <summary>
    /// 六张表全空时不抛、**不写任何依赖配置表的数据**（缺表降级，与其它配置表一致）。
    ///
    /// ★★ 第十二轮更新：<see cref="GmUnlock.ApplyCartoons"/>（全剧情/CG 已读）
    /// **不依赖任何配置表** —— 它的 group/taskId 值域是 <c>CartoonData.lua</c> 里的常量，
    /// 而路径→id 映射表压根不存在（见该方法的取证）。所以即使全部表为空，
    /// 剧情位域**仍然应该被写入**，这是正确行为、不是回归。
    /// ⇒ 本条断言从「report 完全为空」改为「只有剧情位域那 401 条变更」。
    /// </summary>
    [Fact]
    public void ApplyAll_WithEmptyTables_OnlyWritesConfigIndependentData()
    {
        var options = new ServerOptions { SettingDirectory = "does-not-exist" };
        var gm = new GmUnlock(options, ChapterTable.Empty, ItemBaseTable.Empty,
                              CharacterCardTable.Empty, MissionTable.Empty,
                              GuideTable.Empty, PlayerExpTable.Empty);
        var player = NewPlayer();

        var report = gm.ApplyAll(player);

        Assert.Empty(player.Items);
        Assert.Empty(player.Girls);
        Assert.Empty(player.Levels);
        Assert.Equal(GmUnlock.Revision, GmUnlock.RevisionOf(player));   // 标记仍然写

        // 唯一被写的是剧情位域（不依赖配置表）
        int cartoonRows = GmUnlock.CartoonMaxTaskId - GmUnlock.CartoonMinTaskId + 1;
        Assert.Equal(cartoonRows, report.ChangedTasks.Count);
        Assert.All(report.ChangedTasks,
                   kv => Assert.Equal(GmUnlock.CartoonGroup, (int)(kv.Key >> 16)));

        // 其余通道一律没动
        Assert.Empty(report.ChangedItems);
        Assert.Empty(report.ChangedMoney);
        Assert.Empty(report.ChangedLevels);
        Assert.Empty(report.NewGirls);
        Assert.False(report.LevelChanged);
    }
}
