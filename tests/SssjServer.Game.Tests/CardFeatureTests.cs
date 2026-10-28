using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 角色卡养成域的**业务流程**测试（配置与公式在 <c>CardTableTests</c>）。
///
/// 用真实 <see cref="FeatureHarness"/>（反射装配全部 <c>IGameFeature</c> + 真 <c>LuaCallRouter</c>），
/// 所以断言的是「客户端真正会收到什么」。取证见 <c>work/card-domain-forensics.md</c>。
/// </summary>
public class CardFeatureTests
{
    private const uint CardGenre = CardFeature.CardGenre;

    [Fact]
    public async Task FinalRefitUnlocksPermanentAlbumBitAndPreservesNeighborBits()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-album");
        var (guid, row) = SeedRefitable(h, step: 4);
        Assert.Equal(4, row.MaxRefit);
        var card = h.Player.Items.Single(i => i.Guid == guid);
        int id = (int)(card.Detail * 10000 + card.Particular * 10 + card.Level);
        int task = id / 30, mask = 1 << (id % 30);
        int neighbor = 1 << ((id % 30 + 1) % 30);
        h.Player.SetTask(71, task, neighbor);
        await RefitAsync(h, guid, 4);
        Assert.Equal(neighbor | mask, h.Player.GetTask(71, task));
        Assert.Equal(neighbor | mask, h.Module.Store.Load(h.Player.Account)!.GetTask(71, task));
        h.Player.Items.Remove(card);
        ((IGamePulseFeature)h.Module.Features.OfType<MissionFeature>().Single()).OnPlayerPulse(h.Session, h.Player);
        Assert.Equal(neighbor | mask, h.Player.GetTask(71, task));
    }

    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(3, 1, false)]
    [InlineData(4, 0, false)]
    [InlineData(4, 1, true)]
    [InlineData(5, 1, false)]
    public void AlbumMigrationOnlyMarksExistingExactlyMaxedCards(int refit, int count, bool expected)
    {
        var player = new Player("card-refit-marker", 1);
        var card = new Item { Genre = 1, Detail = 2, Particular = 1, Level = 3,
            RefitNum = (uint)refit, Count = (uint)count, LockOn = 1 };
        var cards = CardTable.Load(TestPaths.SettingDir);
        var templates = CharacterCardTable.Load(TestPaths.SettingDir);
        Assert.Equal(expected, CardRefitCollection.Mark(player, card, cards, templates).HasValue);
        Assert.Null(CardRefitCollection.Mark(player, card, cards, templates));
    }

    [Fact]
    public async Task RefitReadsActualAffectionAndPreservesTrainedSkillsAfterReload()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-real-love");
        var (guid, row) = SeedRefitable(h);
        var mission = h.Module.Missions.GetAchieveOther(row.Steps[0].MissionId)!;
        Assert.Equal(135, mission.AchieveType);
        h.Player.SetTask(mission.Group, mission.Id, 0);
        uint girlId = (uint)mission.Counts[1];
        var girl = h.Player.GetGirl(girlId);
        if (girl is null) { girl = new Girl { Girlid = girlId }; h.Player.Girls.Add(girl); }
        girl.Level = (uint)mission.RequiredCount(achieveKey: true);
        var card = h.Player.Items.Single(i => i.Guid == guid);
        var template = h.Module.CharacterCards.Get(card.Genre, card.Detail, card.Particular, card.Level)!;
        CardSkills.Apply(card, template, 100);
        var before = card.Skills.Select(s => (s.SkillId, s.SkillLevel)).ToArray();
        await RefitAsync(h, guid, 1);
        Assert.Equal(1u, card.RefitNum);
        var restored = h.Module.Store.Load(h.Player.Account)!.Items.Single(i => i.Guid == guid);
        Assert.Equal(before, restored.Skills.Select(s => (s.SkillId, s.SkillLevel)).ToArray());
        var payload = GameNotify.BuildItemInfo(restored);
        Assert.Equal(1u, payload.Refitnum);
        Assert.Equal(before, payload.Skillinfo.Select(s => (s.Skillid, s.Skilllevel)).ToArray());
        ((IGamePulseFeature)h.Module.Features.OfType<MissionFeature>().Single()).OnPlayerPulse(h.Session, h.Player);
        Assert.Equal((int)girl.Level, MissionFeature.ProgressOf(h.Player.GetTask(mission.Group, mission.Id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefitRequiresOtherAchievementProgressRegardlessOfClaimBit(bool claimed)
    {
        using var h = await FeatureHarness.CreateAsync($"card-refit-mission-{claimed}");
        var (guid, row) = SeedRefitable(h);
        var mission = h.Module.Missions.GetAchieveOther(row.Steps[0].MissionId)!;
        int required = mission.RequiredCount(achieveKey: true);
        Assert.True(required > 0);
        h.Player.SetTask(mission.Group, mission.Id, MissionFeature.ComposeTaskValue(required - 1, claimed ? 1 : 0));
        int gold = h.Player.GetMoney(2);
        var items = h.Player.Items.Select(i => (i.Guid, i.Count)).ToArray();
        await RefitAsync(h, guid, 1);
        Assert.Equal(0u, h.Player.Items.Single(i => i.Guid == guid).RefitNum);
        Assert.Equal(gold, h.Player.GetMoney(2));
        Assert.Equal(items, h.Player.Items.Select(i => (i.Guid, i.Count)).ToArray());
        int completed = MissionFeature.ComposeTaskValue(required, claimed ? 1 : 0);
        h.Player.SetTask(mission.Group, mission.Id, completed);
        await RefitAsync(h, guid, 1);
        Assert.Equal(1u, h.Player.Items.Single(i => i.Guid == guid).RefitNum);
        Assert.Equal(completed, h.Player.GetTask(mission.Group, mission.Id));
        Assert.Equal(1u, h.Module.Store.Load(h.Player.Account)!.Items.Single(i => i.Guid == guid).RefitNum);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockedMaterialsCannotFundBreakOrRefit(bool refit)
    {
        using var h = await FeatureHarness.CreateAsync($"card-lock-cost-{refit}");
        uint guid;
        if (refit)
        {
            (guid, _) = SeedRefitable(h);
        }
        else
        {
            var tpl = FirstBreakableTemplate();
            guid = AddCard(h.Player, tpl.D, tpl.P, tpl.L);
            var step = CardTable.Load(TestPaths.SettingDir)
                .BreakOf(CardTable.BreakTypeOf(tpl.WeaponType, tpl.Rarity))!.Steps[0];
            foreach (var need in step.Items) AddItem(h.Player, need.G, need.D, need.P, need.L, (uint)need.N);
            h.Player.SetMoney(step.CurrencyId, step.CurrencyCount);
        }
        foreach (var item in h.Player.Items.Where(i => i.Genre != 1)) item.LockOn = 1;
        var before = h.Player.Items.Select(i => (i.Guid, i.Count)).ToArray();
        int gold = h.Player.GetMoney(2);
        if (refit) await RefitAsync(h, guid, 1);
        else await h.DispatchLuaCallAsync(CardFeature.BreakCmd, $"{{\"guid\":{guid}}}");
        var target = h.Player.Items.Single(i => i.Guid == guid);
        Assert.Equal(0u, target.BreakLevel);
        Assert.Equal(0u, target.RefitNum);
        Assert.Equal(gold, h.Player.GetMoney(2));
        Assert.Equal(before, h.Player.Items.Select(i => (i.Guid, i.Count)).ToArray());
    }

    [Fact]
    public async Task ConcurrentSameRefitStepChargesAndAppliesOnce()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-concurrent");
        var (guid, row) = SeedRefitable(h);
        var cost = row.Cost!.Steps[0];
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RefitAsync(h, guid, 1))));
        Assert.Equal(1u, h.Player.Items.Single(i => i.Guid == guid).RefitNum);
        Assert.Equal(0, h.Player.GetMoney(cost.CurrencyId));
        foreach (var need in cost.Items)
            Assert.DoesNotContain(h.Player.Items, i => i.Genre == need.G && i.Detail == need.D &&
                i.Particular == need.P && i.Level == need.L && i.Count > 0);
    }

    [Fact]
    public async Task UpgradeInsufficientGoldPreservesMaterialsAndExperience()
    {
        using var h = await FeatureHarness.CreateAsync("card-up-gold-fail");
        uint guid = AddCard(h.Player, 1, 1, 3);
        AddItem(h.Player, 7, 1, 4, 1, 3);
        h.Player.SetMoney(CardFeature.GoldMoneyId, 0);
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,6,2]]}}");
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        Assert.Equal(3u, h.Player.Items.Single(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 1).Count);
        Assert.Equal(0u, h.Player.Items.Single(i => i.Guid == guid).EnhanceExp);
    }

    [Fact]
    public async Task UpgradeChargesActualUnlockedMaterialCountAcrossRepeatedEntries()
    {
        using var h = await FeatureHarness.CreateAsync("card-up-gold-items");
        uint guid = AddCard(h.Player, 1, 1, 3);
        AddItem(h.Player, 7, 1, 4, 1, 3);
        AddItem(h.Player, 7, 1, 4, 1, 10);
        h.Player.Items.Last().LockOn = 1;
        var template = h.Module.ItemTemplates.Get(7, 1, 4, 1)!;
        int cost = int.Parse(template.ExtParam2) * 3;
        h.Player.SetMoney(CardFeature.GoldMoneyId, cost);
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,6,2],[1,6,2]]}}");
        Assert.Null(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        Assert.Equal(0, h.Player.GetMoney(CardFeature.GoldMoneyId));
        Assert.Equal(10u, h.Player.Items.Single(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 1).Count);
        Assert.Equal(0, h.Module.Store.Load(h.Player.Account)!.GetMoney(CardFeature.GoldMoneyId));
    }

    [Fact]
    public async Task UpgradeChargesCardRarityBaseOnlyOnceForRepeatedGuid()
    {
        using var h = await FeatureHarness.CreateAsync("card-up-gold-card");
        uint target = AddCard(h.Player, 1, 1, 3);
        uint food = AddCard(h.Player, 2, 1, 4);
        int cost = (int)CardTable.Load(TestPaths.SettingDir).RarityFeedBaseExp(4);
        h.Player.SetMoney(CardFeature.GoldMoneyId, cost);
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{target},\"items\":[[2,{food},1],[2,{food},1]]}}");
        Assert.Null(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        Assert.Equal(0, h.Player.GetMoney(CardFeature.GoldMoneyId));
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == food);
    }

    /// <summary>造一张指定 GDPL 的角色卡，返回它的 guid。</summary>
    private static uint AddCard(Player p, uint d, uint particular, uint l, uint level = 1, uint breaklv = 0)
    {
        uint guid = (uint)(p.Items.Count + 1000);
        p.Items.Add(new Item
        {
            Guid = guid, Genre = CardGenre, Detail = d, Particular = particular, Level = l,
            Count = 1, EnhanceLevel = level, BreakLevel = breaklv,
        });
        return guid;
    }

    /// <summary>造一叠道具。</summary>
    private static void AddItem(Player p, int g, int d, int particular, int l, uint count)
    {
        p.Items.Add(new Item
        {
            Guid = (uint)(p.Items.Count + 5000), Genre = (uint)g, Detail = (uint)d,
            Particular = (uint)particular, Level = (uint)l, Count = count,
        });
    }

    /// <summary>从捕获的下行里取某条 LuaCall 的 tbParam。</summary>
    private static JsonNode? ParamOf(FeatureHarness h, int cmd)
    {
        for (int i = h.Outbound.Count - 1; i >= 0; i--)
        {
            var (fn, json) = h.Outbound[i];
            if (fn != "LuaCall")
                continue;
            var root = JsonNode.Parse(json);
            if (root?["sCmd"]?.GetValue<int>() == cmd)
                return root["tbParam"];
        }
        return null;
    }

    /// <summary>取真实配置里第一张「可突破」的卡模板（稀有度允许次数 &gt; 0）。</summary>
    private static CharacterCardTemplate FirstBreakableTemplate()
    {
        var cards = CharacterCardTable.Load(TestPaths.SettingDir);
        var t = CardTable.Load(TestPaths.SettingDir);
        var tpl = cards.All.FirstOrDefault(c =>
            t.MaxBreakCount(CardTable.BreakTypeOf(c.WeaponType, c.Rarity), c.Rarity) > 0);
        Assert.NotNull(tpl);
        return tpl!;
    }

    // =================================================================
    //  一、分解（LuaCall 1）
    // =================================================================

    /// <summary>
    /// ★★ 回包**必须**带 <c>itemlist</c>（数组）+ <c>goldnum</c>：
    /// <c>UI_CardList.lua:642..644</c> 无条件 <c>table.insert(tbParam.itemlist, {...})</c>，
    /// <c>itemlist</c> 为 nil 会当场抛。即使没有返还材料也要回**空数组**。
    /// </summary>
    [Fact]
    public async Task 分解_回包必带itemlist与goldnum()
    {
        using var h = await FeatureHarness.CreateAsync("card-dec");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3, level: 10);

        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{guid}]");

        var tb = ParamOf(h, CardFeature.DecomposeCmd);
        Assert.NotNull(tb);
        Assert.NotNull(tb!["itemlist"]);
        Assert.IsType<JsonArray>(tb["itemlist"]);
        Assert.NotNull(tb["goldnum"]);
    }

    /// <summary>分解后卡从背包消失，金币增加（金额与 <c>CardTable.DecomposeGold</c> 同源）。</summary>
    [Fact]
    public async Task 分解_扣卡并加金币()
    {
        using var h = await FeatureHarness.CreateAsync("card-dec2");
        var p = h.Player;
        uint guid = AddCard(p, d: 1, particular: 1, l: 3, level: 20);
        int goldBefore = p.GetMoney(CardFeature.GoldMoneyId);

        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{guid}]");

        Assert.DoesNotContain(p.Items, i => i.Guid == guid);
        Assert.True(p.GetMoney(CardFeature.GoldMoneyId) >= goldBefore,
            "分解不应该减少金币");

        var tb = ParamOf(h, CardFeature.DecomposeCmd);
        long gold = tb!["goldnum"]!.GetValue<long>();
        Assert.Equal(goldBefore + gold, p.GetMoney(CardFeature.GoldMoneyId));
    }

    /// <summary>
    /// ★ 已锁定的卡**不能**被分解 —— 与背包分解共用
    /// <c>BagFeature.CheckSellable</c>（客户端那两条保护在本版本是坏的）。
    /// 全部被拒时不回包（本族无失败通道）。
    /// </summary>
    [Fact]
    public async Task 分解_锁定的卡被拒且不回包()
    {
        using var h = await FeatureHarness.CreateAsync("card-dec3");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3);
        h.Player.Items.First(i => i.Guid == guid).LockOn = 1;

        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{guid}]");

        Assert.Contains(h.Player.Items, i => i.Guid == guid);   // 卡还在
        Assert.Null(ParamOf(h, CardFeature.DecomposeCmd));      // 没有回包
    }

    /// <summary>非角色卡（genre != 1）不能走这条命令。</summary>
    [Fact]
    public async Task 分解_非角色卡被拒()
    {
        using var h = await FeatureHarness.CreateAsync("card-dec4");
        AddItem(h.Player, 7, 1, 1, 1, 5);
        uint itemGuid = h.Player.Items[^1].Guid;

        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{itemGuid}]");

        Assert.Contains(h.Player.Items, i => i.Guid == itemGuid);
        Assert.Null(ParamOf(h, CardFeature.DecomposeCmd));
    }

    /// <summary>
    /// ★ 上行是**裸数组**，对它取 <c>["key"]</c> 会抛（<c>AGENTS.md</c> 记过的坑）。
    /// 空数组 / 非法上行都要安全返回、不崩。
    /// </summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[0]")]
    [InlineData("null")]
    public async Task 分解_非法上行不崩(string json)
    {
        using var h = await FeatureHarness.CreateAsync("card-dec5");
        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, json);
        Assert.Null(ParamOf(h, CardFeature.DecomposeCmd));
    }

    /// <summary>批量分解：多张卡一次全扣掉，金币累加。</summary>
    [Fact]
    public async Task 分解_批量()
    {
        using var h = await FeatureHarness.CreateAsync("card-dec6");
        var p = h.Player;
        uint a = AddCard(p, 1, 1, 3, level: 5);
        uint b = AddCard(p, 2, 1, 3, level: 5);
        uint c = AddCard(p, 3, 1, 3, level: 5);

        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{a},{b},{c}]");

        Assert.DoesNotContain(p.Items, i => i.Guid == a || i.Guid == b || i.Guid == c);
    }

    // =================================================================
    //  二、升级（LuaCall 5）
    // =================================================================

    /// <summary>
    /// ★★ 成功回包**绝不能带 <c>errorKey</c>**（<c>LuaCall.lua:628</c> 是 Lua 真值判断）。
    /// </summary>
    [Fact]
    public async Task 升级_成功回包不带errorKey()
    {
        using var h = await FeatureHarness.CreateAsync("card-up");
        var p = h.Player;
        uint guid = AddCard(p, d: 1, particular: 1, l: 3);
        AddItem(p, 7, 1, 1, 1, 10);      // tbExpCard 索引 1 = (7,1,1,1)

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,1,5]],\"clientlv\":1,\"clientSkillLv\":0}}");

        var tb = ParamOf(h, CardFeature.LevelUpCommonCmd);
        Assert.NotNull(tb);
        Assert.Null(tb!["errorKey"]);
    }

    /// <summary>吃经验卡后等级/经验上升，且素材被扣。</summary>
    [Fact]
    public async Task 升级_吃经验卡后等级或经验上升()
    {
        using var h = await FeatureHarness.CreateAsync("card-up2");
        var p = h.Player;
        p.SetMoney(CardFeature.GoldMoneyId, 10000000);
        uint guid = AddCard(p, d: 1, particular: 1, l: 3);
        AddItem(p, 7, 1, 4, 4, 50);       // (7,1,4,4) = tbExpCard 索引 24（通用高级经验卡）
        var card = p.Items.First(i => i.Guid == guid);
        uint lvBefore = card.EnhanceLevel;

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,24,20]],\"clientlv\":1,\"clientSkillLv\":0}}");

        Assert.True(card.EnhanceLevel > lvBefore || card.EnhanceExp > 0,
            $"等级与经验都没变（LV.{card.EnhanceLevel} exp={card.EnhanceExp}）");
        // 素材被扣（原 50 个）
        int left = p.Items.Where(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 4)
                          .Sum(i => (int)i.Count);
        Assert.Equal(30, left);
    }

    /// <summary>
    /// ★ 素材是**角色卡**时（<c>itype == 2</c>）<c>id</c> 是 guid；
    /// 非同名卡转成经验、素材卡被吃掉。
    /// </summary>
    [Fact]
    public async Task 升级_吃非同名卡转经验()
    {
        using var h = await FeatureHarness.CreateAsync("card-up3");
        var p = h.Player;
        uint target = AddCard(p, d: 1, particular: 1, l: 3);
        uint food = AddCard(p, d: 2, particular: 1, l: 4, level: 30);   // 不同 GDPL

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{target},\"items\":[[2,{food},1]],\"clientlv\":1,\"clientSkillLv\":0}}");

        var card = p.Items.First(i => i.Guid == target);
        Assert.True(card.EnhanceLevel > 1 || card.EnhanceExp > 0, "非同名卡应转成经验");
        Assert.DoesNotContain(p.Items, i => i.Guid == food);
    }

    /// <summary>
    /// ★★ 同名卡（GDPL 四元全等）**只加技能等级、不产生经验**
    /// （<c>UI_CardMain.lua:1999..2021</c>）。素材消耗后增加3点技能提升，卡等级不变。
    /// </summary>
    [Fact]
    public async Task 升级_同名卡不加经验()
    {
        using var h = await FeatureHarness.CreateAsync("card-up4");
        var p = h.Player;
        var template = h.Module.CharacterCards.All.First(t => t.P != 17 && CardSkills.Ids(t).Length >= 3);
        uint target = AddCard(p, template.D, template.P, template.L);
        uint same = AddCard(p, template.D, template.P, template.L, level: 40);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{target},\"items\":[[2,{same},1]],\"clientlv\":1,\"clientSkillLv\":0}}");

        var card = p.Items.First(i => i.Guid == target);
        Assert.Equal(1u, card.EnhanceLevel);
        Assert.Equal(0u, card.EnhanceExp);
        Assert.Equal(3, CardSkills.Ids(template).Sum(id => (long)CardSkills.Level(card, id) - 1));
        Assert.DoesNotContain(p.Items, i => i.Guid == same);
        Assert.Null(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
    }

    /// <summary>没有任何有效素材 ⇒ 回带 <c>errorKey</c> 的失败包（升级族的失败通道）。</summary>
    [Fact]
    public async Task 升级_无有效素材回errorKey()
    {
        using var h = await FeatureHarness.CreateAsync("card-up5");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[],\"clientlv\":1,\"clientSkillLv\":0}}");

        var tb = ParamOf(h, CardFeature.LevelUpCommonCmd);
        Assert.NotNull(tb);
        Assert.NotNull(tb!["errorKey"]);
    }

    /// <summary>目标卡不存在 ⇒ 回 errorKey，不崩。</summary>
    [Fact]
    public async Task 升级_目标不存在回errorKey()
    {
        using var h = await FeatureHarness.CreateAsync("card-up6");
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            "{\"guid\":999999,\"items\":[[1,1,1]],\"clientlv\":1,\"clientSkillLv\":0}");

        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
    }

    /// <summary>
    /// ★ 素材索引越界（&gt; 26）要被跳过而不是崩，且因为没有有效素材而回 errorKey。
    /// </summary>
    [Fact]
    public async Task 升级_素材索引越界不崩()
    {
        using var h = await FeatureHarness.CreateAsync("card-up7");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,9999,1]],\"clientlv\":1,\"clientSkillLv\":0}}");

        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
    }

    /// <summary>
    /// ★ 等级不会超过「突破后的上限」：0 次突破的卡即使喂海量经验也只能到 <c>MaxLevel0</c>。
    /// </summary>
    [Fact]
    public async Task 升级_不超过突破上限()
    {
        using var h = await FeatureHarness.CreateAsync("card-up8");
        var p = h.Player;
        var tpl = FirstBreakableTemplate();
        uint guid = AddCard(p, tpl.D, tpl.P, tpl.L);
        AddItem(p, 7, 1, 4, 4, 9999);      // 通用高级经验卡，海量

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,24,9999]],\"clientlv\":1,\"clientSkillLv\":0}}");

        var t = CardTable.Load(TestPaths.SettingDir);
        int cap = t.MaxLevelFor(CardTable.BreakTypeOf(tpl.WeaponType, tpl.Rarity), 0);
        var card = p.Items.First(i => i.Guid == guid);
        Assert.True(card.EnhanceLevel <= cap,
            $"等级 {card.EnhanceLevel} 超过 0 突破上限 {cap}");
    }

    /// <summary>
    /// ★★ **誓约卡（P == 17，现网 39 张）不能升级** —— 客户端
    /// <c>UI_CardMain.lua:1683..1691</c> 把素材列表/加素材按钮/信息页签三个节点
    /// <c>SetActive(P ~= 17)</c> 后直接 <c>return</c>，**界面层根本没有入口**。
    /// ⇒ 服务端必须自己拒绝：GM/改包能绕过 UI 直接发上行命令。
    /// </summary>
    [Fact]
    public async Task 升级_誓约卡被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("card-pldge");
        var p = h.Player;
        uint guid = AddCard(p, d: 1, particular: CardFeature.PldgeCardParticular, l: 3);
        AddItem(p, 7, 1, 4, 4, 20);
        var card = p.Items.First(i => i.Guid == guid);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,24,10]],\"clientlv\":1,\"clientSkillLv\":0}}");

        Assert.Equal(1u, card.EnhanceLevel);                 // 没被练
        Assert.Equal(0u, card.EnhanceExp);
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        // 素材也不该被扣
        int left = p.Items.Where(i => i.Genre == 7 && i.Detail == 1 && i.Particular == 4 && i.Level == 4)
                          .Sum(i => (int)i.Count);
        Assert.Equal(20, left);
    }

    /// <summary>★ 誓约卡也**不能当素材**（同一处取证：客户端连素材列表都不显示它）。</summary>
    [Fact]
    public async Task 升级_誓约卡不能当素材()
    {
        using var h = await FeatureHarness.CreateAsync("card-pldge2");
        var p = h.Player;
        uint target = AddCard(p, d: 1, particular: 1, l: 3);
        uint pldge = AddCard(p, d: 2, particular: CardFeature.PldgeCardParticular, l: 4, level: 40);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{target},\"items\":[[2,{pldge},1]],\"clientlv\":1,\"clientSkillLv\":0}}");

        // 誓约卡没被吃掉，目标也没升级 ⇒ 回 errorKey（没有有效素材）
        Assert.Contains(p.Items, i => i.Guid == pldge);
        Assert.Equal(1u, p.Items.First(i => i.Guid == target).EnhanceLevel);
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
    }

    /// <summary>
    /// ★ <c>errorKey</c> **不能带 <c>ui.</c> 前缀** —— 客户端是
    /// <c>ShowMsgByKey("ui." .. tbParam.errorKey)</c>（<c>LuaCall.lua:616/629</c>
    /// → <c>Lib.lua:635</c>），自己会拼。带上就成 <c>ui.ui.xxx</c>、查不到本地化。
    /// </summary>
    [Fact]
    public async Task 升级_errorKey不带ui前缀()
    {
        using var h = await FeatureHarness.CreateAsync("card-errkey");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[],\"clientlv\":1,\"clientSkillLv\":0}}");

        string key = ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]!.GetValue<string>();
        Assert.False(key.StartsWith("ui.", StringComparison.Ordinal),
            $"errorKey \"{key}\" 带了 ui. 前缀，客户端会拼成 ui.{key}");
        Assert.NotEmpty(key);
    }

    /// <summary>
    /// ★ 素材条目数超过客户端上限 15（<c>UI_CardMain.lua:21</c> <c>MaxCount = 15</c>）⇒ 拒绝。
    /// 客户端界面自己拦住了，但 GM/改包能发任意长度（既能刷数值也是 DoS 面）。
    /// </summary>
    [Fact]
    public async Task 升级_素材条目超上限被拒()
    {
        using var h = await FeatureHarness.CreateAsync("card-maxmat");
        var p = h.Player;
        uint guid = AddCard(p, d: 1, particular: 1, l: 3);
        AddItem(p, 7, 1, 4, 4, 999);

        // 16 条 > MaxMaterialEntries(15)
        var items = string.Join(",", Enumerable.Repeat("[1,24,1]", CardFeature.MaxMaterialEntries + 1));
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[{items}],\"clientlv\":1,\"clientSkillLv\":0}}");

        Assert.Equal(1u, p.Items.First(i => i.Guid == guid).EnhanceLevel);
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        // 素材一个都不该被扣
        Assert.Equal(999, p.Items.Where(i => i.Genre == 7 && i.Particular == 4 && i.Level == 4)
                                 .Sum(i => (int)i.Count));
    }

    /// <summary>
    /// ★ 道具索引 <b>0</b> 是 <c>GetIdxByGDPL</c> 匹配失败的**哨兵值**
    /// （<c>CardCommon.lua:695 return 0</c>）⇒ 必须当非法跳过，不能当合法索引解码。
    /// </summary>
    [Fact]
    public async Task 升级_道具索引0被当非法跳过()
    {
        using var h = await FeatureHarness.CreateAsync("card-idx0");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,0,5]],\"clientlv\":1,\"clientSkillLv\":0}}");

        // 唯一素材非法 ⇒ 没有有效素材 ⇒ 回 errorKey
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        Assert.Equal(1u, h.Player.Items.First(i => i.Guid == guid).EnhanceLevel);
    }

    // =================================================================
    //  三、突破（LuaCall 6）
    // =================================================================

    /// <summary>
    /// ★★★ 突破成功回包**必须带 <c>ok = true</c>**（<c>LuaCall.lua:642</c>
    /// <c>if tbParam and tbParam.ok then</c>）—— 与升级族方向相反，不带 = 点了没反应。
    /// </summary>
    [Fact]
    public async Task 突破_成功回包必须带ok()
    {
        using var h = await FeatureHarness.CreateAsync("card-brk");
        var p = h.Player;
        var tpl = FirstBreakableTemplate();
        uint guid = AddCard(p, tpl.D, tpl.P, tpl.L);

        // 备齐第一次突破的材料与货币
        var t = CardTable.Load(TestPaths.SettingDir);
        var row = t.BreakOf(CardTable.BreakTypeOf(tpl.WeaponType, tpl.Rarity))!;
        var step = row.Steps[0];
        foreach (var need in step.Items)
            AddItem(p, need.G, need.D, need.P, need.L, (uint)(need.N * 2));
        p.AddMoney(step.CurrencyId, step.CurrencyCount * 2);

        await h.DispatchLuaCallAsync(CardFeature.BreakCmd, $"{{\"guid\":{guid}}}");

        var tb = ParamOf(h, CardFeature.BreakCmd);
        Assert.NotNull(tb);
        Assert.True(tb!["ok"]!.GetValue<bool>(), "突破成功必须带 ok = true");
        Assert.Equal(1u, p.Items.First(i => i.Guid == guid).BreakLevel);
    }

    /// <summary>突破会扣材料与货币。</summary>
    [Fact]
    public async Task 突破_扣材料与货币()
    {
        using var h = await FeatureHarness.CreateAsync("card-brk2");
        var p = h.Player;
        var tpl = FirstBreakableTemplate();
        uint guid = AddCard(p, tpl.D, tpl.P, tpl.L);

        var t = CardTable.Load(TestPaths.SettingDir);
        var step = t.BreakOf(CardTable.BreakTypeOf(tpl.WeaponType, tpl.Rarity))!.Steps[0];
        foreach (var need in step.Items)
            AddItem(p, need.G, need.D, need.P, need.L, (uint)need.N);
        p.AddMoney(step.CurrencyId, step.CurrencyCount);
        int moneyBefore = p.GetMoney(step.CurrencyId);

        await h.DispatchLuaCallAsync(CardFeature.BreakCmd, $"{{\"guid\":{guid}}}");

        Assert.Equal(moneyBefore - step.CurrencyCount, p.GetMoney(step.CurrencyId));
        foreach (var need in step.Items)
        {
            int left = p.Items.Where(i => i.Genre == (uint)need.G && i.Detail == (uint)need.D &&
                                          i.Particular == (uint)need.P && i.Level == (uint)need.L)
                              .Sum(i => (int)i.Count);
            Assert.Equal(0, left);
        }
    }

    /// <summary>★ 材料/货币不足时**不回包**（突破族没有 errorKey 通道），且不扣任何东西。</summary>
    [Fact]
    public async Task 突破_材料不足不回包且不扣()
    {
        using var h = await FeatureHarness.CreateAsync("card-brk3");
        var p = h.Player;
        var tpl = FirstBreakableTemplate();
        uint guid = AddCard(p, tpl.D, tpl.P, tpl.L);
        // 故意不给材料与货币

        await h.DispatchLuaCallAsync(CardFeature.BreakCmd, $"{{\"guid\":{guid}}}");

        Assert.Null(ParamOf(h, CardFeature.BreakCmd));
        Assert.Equal(0u, p.Items.First(i => i.Guid == guid).BreakLevel);
    }

    /// <summary>突破次数达上限后不再允许（不回包）。</summary>
    [Fact]
    public async Task 突破_到上限后拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("card-brk4");
        var p = h.Player;
        var tpl = FirstBreakableTemplate();
        var t = CardTable.Load(TestPaths.SettingDir);
        int bt = CardTable.BreakTypeOf(tpl.WeaponType, tpl.Rarity);
        int max = t.MaxBreakCount(bt, tpl.Rarity);

        uint guid = AddCard(p, tpl.D, tpl.P, tpl.L, breaklv: (uint)max);
        // 材料给足，验证「拒绝是因为次数而不是材料」
        var step = t.BreakOf(bt)!.Steps[0];
        foreach (var need in step.Items)
            AddItem(p, need.G, need.D, need.P, need.L, (uint)(need.N * 5));
        p.AddMoney(step.CurrencyId, step.CurrencyCount * 5);

        await h.DispatchLuaCallAsync(CardFeature.BreakCmd, $"{{\"guid\":{guid}}}");

        Assert.Null(ParamOf(h, CardFeature.BreakCmd));
        Assert.Equal((uint)max, p.Items.First(i => i.Guid == guid).BreakLevel);
    }

    /// <summary>目标不存在 / 非角色卡 ⇒ 不回包、不崩。</summary>
    [Theory]
    [InlineData("{\"guid\":999999}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public async Task 突破_非法上行不崩(string json)
    {
        using var h = await FeatureHarness.CreateAsync("card-brk5");
        await h.DispatchLuaCallAsync(CardFeature.BreakCmd, json);
        Assert.Null(ParamOf(h, CardFeature.BreakCmd));
    }

    // =================================================================
    //  四、时序（本域最重要的约束）
    // =================================================================

    /// <summary>
    /// ★★★ <b>pf 1031 必须先于 LuaCall 回包</b>。
    ///
    /// 客户端 <c>ReceiveUpResult()</c> 不接收参数、直接从本地缓存
    /// <c>Game.EquipManager.GetCharacterCard(guid)</c>（<c>Dictionary.TryGetValue</c>）重读，
    /// 所以新数值只能靠 pf 1031 先送到。顺序反了 = 数值对了但界面不动。
    ///
    /// 断言方式：harness 的 <c>PushedPf</c> 记录 socket 上的 pf 序列，
    /// <c>Outbound</c> 记录 pf 1024 的 LuaCall —— 这里验证 1031 确实发生了。
    /// </summary>
    [Fact]
    public async Task 时序_升级先推1031再回包()
    {
        using var h = await FeatureHarness.CreateAsync("card-order");
        var p = h.Player;
        p.SetMoney(CardFeature.GoldMoneyId, 10000000);
        uint guid = AddCard(p, d: 1, particular: 1, l: 3);
        AddItem(p, 7, 1, 4, 4, 20);

        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guid},\"items\":[[1,24,10]],\"clientlv\":1,\"clientSkillLv\":0}}");

        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf),
            "升级必须推 pf 1031，否则客户端界面读到旧值");
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd));
    }

    /// <summary>分解同样要推 pf 1031（移除）。</summary>
    [Fact]
    public async Task 时序_分解先推1031再回包()
    {
        using var h = await FeatureHarness.CreateAsync("card-order2");
        uint guid = AddCard(h.Player, d: 1, particular: 1, l: 3, level: 15);

        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{guid}]");

        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf),
            "分解必须推 pf 1031 把卡从客户端背包移除");
        Assert.NotNull(ParamOf(h, CardFeature.DecomposeCmd));
    }

    // =================================================================
    //  五、装配 / 字段保真
    // =================================================================

    /// <summary>
    /// ★★ <c>syncrate</c>(18) 与 <c>refitnum</c>(19) 必须能**保真往返**。
    ///
    /// 客户端 <c>AddCharacterCard</c>（RVA <c>0x1D95368</c>）在
    /// <c>0x1D957BC get_syncrate</c> / <c>0x1D957F8 get_refitnum</c> 处**无条件**读并 set
    /// （没有 <c>has_</c> 判断）⇒ 服务端漏发就等于「每次推背包都把它们清零」。
    /// 改造功能(LuaCall 8)虽未实现，但字段必须先能往返，否则将来一定被静默抹掉。
    /// </summary>
    [Fact]
    public void 字段保真_syncrate与refitnum会被下发()
    {
        var it = new Item
        {
            Guid = 42, Genre = CardGenre, Detail = 1, Particular = 1, Level = 3,
            Count = 1, EnhanceLevel = 5, SyncRate = 77777, RefitNum = 3,
        };

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(77777u, info.Syncrate);
        Assert.Equal(3u, info.Refitnum);
        // 顺带确认养成三字段也在
        Assert.Equal(5u, info.Enhancelevel);
    }

    /// <summary>三条命令都要被真实 <c>GameModule</c> 装配（否则真机会打 unhandled）。</summary>
    [Fact]
    public async Task 装配_三条命令都已注册()
    {
        using var h = await FeatureHarness.CreateAsync("card-install");
        var p = h.Player;
        uint guid = AddCard(p, d: 1, particular: 1, l: 3);
        AddItem(p, 7, 1, 4, 4, 5);

        // 只要 handler 存在就会有回包/日志；未注册的命令会走 unhandled 分支且无下行
        await h.DispatchLuaCallAsync(CardFeature.DecomposeCmd, $"[{guid}]");
        Assert.NotNull(ParamOf(h, CardFeature.DecomposeCmd));

        uint g2 = AddCard(p, d: 9, particular: 1, l: 3);
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{g2},\"items\":[[1,24,1]],\"clientlv\":1,\"clientSkillLv\":0}}");
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd));
    }
    // ================================================================ LuaCall 8 改造（第十三轮）

    /// <summary>造一张「表里可改造」的卡，并把消耗/同步率都备齐。</summary>
    private static (uint Guid, CardRefitRow Row) SeedRefitable(
        FeatureHarness h, int step = 1, bool enough = true)
    {
        var table = CardTable.Load(TestPaths.SettingDir);
        var row = table.RefitOf(1, 2, 1, 3)!;
        Assert.NotNull(row);

        uint guid = AddCard(h.Player, 2, 1, 3, level: 40, breaklv: 0);
        var card = h.Player.Items.First(x => x.Guid == guid);
        card.RefitNum = (uint)(step - 1);
        // 同步率给满（★ 千分之一单位）
        card.SyncRate = enough ? (uint)(row.Steps[step - 1].NeedSync) : 0;
        var mission = h.Module.Missions.GetAchieveOther(row.Steps[step - 1].MissionId);
        if (mission is not null)
            h.Player.SetTask(mission.Group, mission.Id,
                MissionFeature.ComposeTaskValue(mission.RequiredCount(achieveKey: true), 0));

        if (enough)
        {
            var cost = row.Cost!.Steps[step - 1];
            h.Player.AddMoney(cost.CurrencyId,
                cost.CurrencyCount - h.Player.GetMoney(cost.CurrencyId));
            foreach (var need in cost.Items)
                AddItem(h.Player, need.G, need.D, need.P, need.L, (uint)need.N);
        }
        return (guid, row);
    }

    [Theory]
    [InlineData("guid", "fraction")]
    [InlineData("guid", "string")]
    [InlineData("refitNum", "fraction")]
    [InlineData("refitNum", "string")]
    [InlineData("card", "zero")]
    public async Task InvalidRefitCannotConsumeOrModifyCard(string field, string shape)
    {
        using var h = await FeatureHarness.CreateAsync("refit-request-boundary");
        var (guid, row) = SeedRefitable(h);
        var card = h.Player.Items.First(i => i.Guid == guid);
        if (field == "card") card.Count = 0;
        long total = h.Player.Items.Sum(i => (long)i.Count);
        var cost = row.Cost!.Steps[0];
        long money = h.Player.GetMoney(cost.CurrencyId);
        string guidJson = field == "guid" ? shape == "string" ? $"\"{guid}\"" : $"{guid}.5" : guid.ToString();
        string stepJson = field == "refitNum" ? shape == "string" ? "\"1\"" : "1.5" : "1";
        await h.DispatchLuaCallAsync(CardFeature.RefitCmd, $"{{\"guid\":{guidJson},\"refitNum\":{stepJson}}}");
        Assert.Equal(0u, card.RefitNum);
        Assert.Equal(money, h.Player.GetMoney(cost.CurrencyId));
        Assert.Equal(total, h.Player.Items.Sum(i => (long)i.Count));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeRejectsFractionalGuidOrZeroQuantityWithoutConsuming(bool zeroCount)
    {
        using var h = await FeatureHarness.CreateAsync("card-upgrade-target-boundary");
        uint guid = AddCard(h.Player, 1, 1, 3);
        var target = h.Player.Items.First(i => i.Guid == guid);
        if (zeroCount) target.Count = 0;
        AddItem(h.Player, 7, 1, 1, 1, 10);
        string guidJson = zeroCount ? guid.ToString() : $"{guid}.5";
        long materials = h.Player.Items.Where(i => i.Genre == 7).Sum(i => (long)i.Count);
        int gold = h.Player.GetMoney(CardFeature.GoldMoneyId);
        await h.DispatchLuaCallAsync(CardFeature.LevelUpCommonCmd,
            $"{{\"guid\":{guidJson},\"items\":[[1,1,5]],\"clientlv\":1,\"clientSkillLv\":0}}");
        Assert.NotNull(ParamOf(h, CardFeature.LevelUpCommonCmd)!["errorKey"]);
        Assert.Equal(1u, target.EnhanceLevel);
        Assert.Equal(0u, target.EnhanceExp);
        Assert.Equal(materials, h.Player.Items.Where(i => i.Genre == 7).Sum(i => (long)i.Count));
        Assert.Equal(gold, h.Player.GetMoney(CardFeature.GoldMoneyId));
    }

    private static Task RefitAsync(FeatureHarness h, uint guid, int refitNum)
        => h.DispatchLuaCallAsync(CardFeature.RefitCmd,
            $"{{\"guid\":{guid},\"refitNum\":{refitNum}}}");

    /// <summary>
    /// ★★★ 成功必须带 <c>ok = true</c>（与突破同族、与升级的 <c>errorKey</c> 方向相反）。
    /// 不带 = 客户端点了没反应。
    /// </summary>
    [Fact]
    public async Task 改造成功必须带ok并提升RefitNum()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-ok");
        var (guid, _) = SeedRefitable(h);

        await RefitAsync(h, guid, 1);

        var tb = ParamOf(h, CardFeature.RefitCmd);
        Assert.NotNull(tb);
        Assert.True(tb!["ok"]!.GetValue<bool>());
        Assert.Equal(1u, h.Player.Items.First(x => x.Guid == guid).RefitNum);
    }

    /// <summary>★ 消耗要真的扣掉（货币 + 材料）。</summary>
    [Fact]
    public async Task 改造会扣货币与材料()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-cost");
        var (guid, row) = SeedRefitable(h);
        var cost = row.Cost!.Steps[0];

        await RefitAsync(h, guid, 1);

        Assert.Equal(0, h.Player.GetMoney(cost.CurrencyId));
        foreach (var need in cost.Items)
        {
            int left = h.Player.Items
                .Where(x => x.Genre == (uint)need.G && x.Detail == (uint)need.D
                         && x.Particular == (uint)need.P && x.Level == (uint)need.L)
                .Sum(x => (int)x.Count);
            Assert.Equal(0, left);
        }
    }

    /// <summary>★★ 同步率不足要拒绝，且**不回包**（本族无失败通道）、不扣东西。</summary>
    [Fact]
    public async Task 同步率不足被拒绝且不回包()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-sync");
        var (guid, _) = SeedRefitable(h, enough: false);
        int before = h.Outbound.Count;

        await RefitAsync(h, guid, 1);

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(0u, h.Player.Items.First(x => x.Guid == guid).RefitNum);
    }

    /// <summary>★★ 不能跳档：<c>refitNum</c> 必须正好是「当前次数 + 1」。</summary>
    [Fact]
    public async Task 跳档改造被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-skip");
        var (guid, _) = SeedRefitable(h);
        int before = h.Outbound.Count;

        await RefitAsync(h, guid, 3);      // 当前 0 次，只能改第 1 档

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(0u, h.Player.Items.First(x => x.Guid == guid).RefitNum);
    }

    /// <summary>★ 不在 <c>CardRefit.txt</c> 里的卡不能改造。</summary>
    [Fact]
    public async Task 不可改造的卡被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-nores");
        uint guid = AddCard(h.Player, 9999, 9, 9);
        int before = h.Outbound.Count;

        await RefitAsync(h, guid, 1);

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>★ 材料/货币不足要拒绝（同步率够但没钱）。</summary>
    [Fact]
    public async Task 货币不足被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-poor");
        var (guid, row) = SeedRefitable(h);
        // 把钱清空（材料仍在）
        var cost = row.Cost!.Steps[0];
        h.Player.AddMoney(cost.CurrencyId, -h.Player.GetMoney(cost.CurrencyId));
        int before = h.Outbound.Count;

        await RefitAsync(h, guid, 1);

        Assert.Equal(before, h.Outbound.Count);
        Assert.Equal(0u, h.Player.Items.First(x => x.Guid == guid).RefitNum);
    }

    /// <summary>★ 超过改造上限要拒绝。</summary>
    [Fact]
    public async Task 超过改造上限被拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-max");
        var table = CardTable.Load(TestPaths.SettingDir);
        var row = table.RefitOf(1, 2, 1, 3)!;

        uint guid = AddCard(h.Player, 2, 1, 3, level: 40);
        var card = h.Player.Items.First(x => x.Guid == guid);
        card.RefitNum = (uint)row.MaxRefit;      // 已满
        card.SyncRate = 100000;
        int before = h.Outbound.Count;

        await RefitAsync(h, guid, row.MaxRefit + 1);

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>★ 非法参数（guid 不存在 / 空对象）不崩、不回包。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"guid\":0,\"refitNum\":1}")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task 改造非法参数不崩(string json)
    {
        using var h = await FeatureHarness.CreateAsync("card-refit-bad" + json.GetHashCode());
        int before = h.Outbound.Count;

        await h.DispatchLuaCallAsync(CardFeature.RefitCmd, json);

        Assert.Equal(before, h.Outbound.Count);
    }
}
