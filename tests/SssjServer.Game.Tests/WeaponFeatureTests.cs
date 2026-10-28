using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 武器养成（`sFunction WeaponLogicMsg`）。取证见 `docs/05` §5.4 / §28。
///
/// 断言重点是这一族与别人**相反**的两条约定：
/// <list type="number">
///   <item>`S2CCall` 字符串族 ⇒ 成功**必须带 <c>nError = 0</c>**
///     （客户端 `S2CCall.lua:153` 是 <c>nError &gt; 0</c> 的**数值比较**，缺字段 `nil &gt; 0` 抛）；</item>
///   <item>★★★ 分解回包**必须无条件带 <c>tbParam[2]</c>**（可空数组）——
///     金币为 0 时客户端 `#tbParam.tbParam[2]` 对 nil 取长度会抛（`S2CCall.lua:177`）。</item>
/// </list>
/// </summary>
public class WeaponFeatureTests
{
    private static JsonObject Reply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(WeaponFeature.Function, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    private static Task SendAsync(FeatureHarness h, string body)
        => h.DispatchFunctionAsync(WeaponFeature.Function, body);

    /// <summary>造一把武器进背包。默认取 <c>2-1-2-2</c>（Rarity 1，上限 40/50/60/70）。</summary>
    private static Item SeedWeapon(FeatureHarness h, uint guid, uint level = 1, uint exp = 0,
                                   uint breakLevel = 0, uint d = 1, uint p = 2, uint l = 2,
                                   uint lockOn = 0)
    {
        var w = new Item
        {
            Guid = guid,
            Genre = WeaponTable.WeaponGenre,
            Detail = d, Particular = p, Level = l,
            Count = 1,
            EnhanceLevel = level,
            EnhanceExp = exp,
            BreakLevel = breakLevel,
            LockOn = lockOn,
        };
        h.Player.Items.Add(w);
        return w;
    }

    // ================= 通用约定 =================

    [Theory]
    [InlineData(WeaponFeature.CmdStrong)]
    [InlineData(WeaponFeature.CmdBreak)]
    [InlineData(WeaponFeature.CmdRecast)]
    [InlineData(WeaponFeature.CmdRecastChoose)]
    [InlineData(WeaponFeature.CmdDecompose)]
    public async Task 任何命令都必须回包_否则卡遮罩(int cmd)
    {
        using var h = await FeatureHarness.CreateAsync($"weapon-reply-{cmd}");

        // 故意给非法参数：即便失败也必须回（这一族 SendToServer 默认挂 Blocking.Show）
        await SendAsync(h, $"{{\"nCmd\":{cmd}}}");

        var r = Reply(h);
        Assert.True(r.ContainsKey("nError"), "回包没有 nError —— 客户端 nil > 0 会抛");
        Assert.Equal(cmd, r["nCmd"]!.GetValue<int>());
    }

    [Fact]
    public async Task 未知nCmd也要回包()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-unknown");

        await SendAsync(h, "{\"nCmd\":99}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task 上行是数组时不抛()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-array");

        // ★ AGENTS.md 记过「JsonNode["key"] 对 JsonArray 会抛」（咖啡馆 115 真机崩过）
        await SendAsync(h, "[]");
    }

    /// <summary>
    /// ★★★ 第九轮：<b>打造(nCmd=0) 已实现</b>，实现在 <see cref="WeaponForgeFeature"/>
    /// （通过 <c>AddFunctionCoHandler</c> 挂在同一个 sFunction 上）。
    ///
    /// 本测试原名「打造未实现_回错误码而不是静默」、断言 <c>nError &gt; 0</c>，
    /// 第九轮实现后**它是第一个变红的测试** —— 这正是它该做的事：
    /// 行为变更被回归闸捕获，而不是悄悄改掉。
    ///
    /// 现在的契约（详见 <see cref="WeaponForgeFeature"/> 与
    /// `work/round9-mainsession-forensics.md` §1）：
    /// <list type="bullet">
    ///   <item>上行只有 <c>{nCmd, Id, isTen}</c> 三个字段
    ///     ——第八轮以为有的 <c>Type</c> 字段**压根不存在**；</item>
    ///   <item>无论成败都必须回包（客户端挂着 <c>Blocking.Show(300)</c> 5 分钟遮罩）；</item>
    ///   <item><c>nCmd</c> 必须回显为 0（客户端按它分流到
    ///     <c>UI_WeaponSystemNEW:OnServerSuccess</c>）。</item>
    /// </list>
    /// 成功/失败/形状/顺序的完整断言在 <c>WeaponForgeFeatureTests</c>（14 条），
    /// 这里只守住「本域不再吞掉 nCmd=0、且一定回包」这条边界。
    /// </summary>
    [Fact]
    public async Task 打造已实现_由WeaponForgeFeature接管且一定回包()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-forge");

        // 池 1 是现网唯一开放的武器池；不给券也一定要回包（够不够是另一回事）
        await SendAsync(h, "{\"nCmd\":0,\"Id\":1,\"isTen\":false}");

        var r = Reply(h);
        // ★ 关键：nCmd 必须回显为 0（否则客户端不知道分流到哪个界面回调）
        Assert.Equal(WeaponFeature.CmdForge, r["nCmd"]!.GetValue<int>());
        // ★ nError 必须存在（S2CCall.lua:153 是无条件数值比较，缺了 nil > 0 抛）
        Assert.NotNull(r["nError"]);

        // 成功时必须带 tbParam（元素是 {nGuid=…}）；失败时不带 —— 两者都合法，
        // 但「既没有 tbParam 又 nError==0」是非法的（客户端会拿 nil 去 #nil）
        int err = r["nError"]!.GetValue<int>();
        if (err == 0)
            Assert.IsType<JsonArray>(r["tbParam"]);
    }

    // ================= nCmd 1：强化 =================

    [Fact]
    public async Task 强化_吃素材涨经验并升级()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-strong");
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, 100000);
        var target = SeedWeapon(h, 1001);
        SeedWeapon(h, 1002);
        SeedWeapon(h, 1003);

        await SendAsync(h, "{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[1002,1],[1003,1]]}");

        var r = Reply(h);
        Assert.Equal(WeaponFeature.ErrOk, r["nError"]!.GetValue<int>());
        Assert.Equal(WeaponFeature.CmdStrong, r["nCmd"]!.GetValue<int>());

        // 两把 Rarity 1 素材 = 2 × RarityExp(1)=500 ⇒ 1000 经验；1 级需 133
        var t = WeaponTable.Load(TestPaths.SettingDir);
        int expect = 2 * t.RarityExp(1);
        var (lv, exp) = t.DestLevel(1, 0, expect, t.MaxLevel(1, 0));
        Assert.Equal((uint)lv, target.EnhanceLevel);
        Assert.Equal((uint)exp, target.EnhanceExp);
        Assert.True(target.EnhanceLevel > 1, "吃了 1000 点经验还是 1 级 —— 疑似读到空的 NeedExp2 列");

        // 素材被消耗
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 1002);
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 1003);
    }

    [Fact]
    public async Task 强化_推送pf1031让界面能刷新()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-strong-push");
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, 100000);
        SeedWeapon(h, 1001);
        SeedWeapon(h, 1002);

        await SendAsync(h, "{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[1002,1]]}");

        // ★ 客户端 OnServerSuccess 不收数据、从本地缓存重读 ⇒ 必须先推 1031
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf),
            "强化后没有推 pf 1031 —— 客户端界面不会更新");
    }

    [Fact]
    public async Task 强化_上锁的素材不被吃()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-strong-lock");
        var target = SeedWeapon(h, 1001);
        SeedWeapon(h, 1002, lockOn: 1);

        await SendAsync(h, "{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[1002,1]]}");

        // 唯一素材上锁 ⇒ 一个都不可用 ⇒ 失败
        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Contains(h.Player.Items, i => i.Guid == 1002);
        Assert.Equal(1u, target.EnhanceLevel);
    }

    [Fact]
    public async Task 强化_不能拿自己当素材()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-strong-self");
        var target = SeedWeapon(h, 1001);

        await SendAsync(h, "{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[1001,1]]}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Contains(h.Player.Items, i => i.Guid == 1001);
        Assert.Equal(1u, target.EnhanceLevel);
    }

    [Fact]
    public async Task 强化_不是自己的武器被拒()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-strong-nog");

        await SendAsync(h, "{\"nCmd\":1,\"nGuid\":777,\"tbGuid\":[[1,1]]}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
    }

    [Fact]
    public async Task 强化_不会超过当前等级上限()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-strong-cap");
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, 100000);
        var t = WeaponTable.Load(TestPaths.SettingDir);
        int cap = t.MaxLevel(1, 0);                 // 0 突破时 40
        var target = SeedWeapon(h, 1001, level: (uint)cap);

        // 塞 20 把素材
        var guids = new List<string>();
        for (uint g = 2000; g < 2020; g++)
        {
            SeedWeapon(h, g);
            guids.Add($"[{g},1]");
        }
        await SendAsync(h, $"{{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[{string.Join(',', guids)}]}}");

        Assert.Equal(WeaponFeature.ErrOk, Reply(h)["nError"]!.GetValue<int>());
        // ★ 等级封在上限，但经验仍累加（`Lib.lua:689..691`）
        Assert.Equal((uint)cap, target.EnhanceLevel);
        Assert.True(target.EnhanceExp > 0, "撞上限时经验应继续累加，不是丢弃");
    }

    [Fact]
    public async Task StrongFluid_ConsumesStackAndGold_AndPersists()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-fluid");
        var target = SeedWeapon(h, 1001);
        var fluid = h.Player.AddItem(7, 3, 1, 1, 5);
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, 2000);
        await SendAsync(h, $"{{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[{fluid.Guid},2]]}}");
        Assert.Equal(WeaponFeature.ErrOk, Reply(h)["nError"]!.GetValue<int>());
        var table = WeaponTable.Load(TestPaths.SettingDir);
        var (level, exp) = table.DestLevel(1, 0, 1000, 40);
        Assert.Equal((uint)level, target.EnhanceLevel);
        Assert.Equal((uint)exp, target.EnhanceExp);
        Assert.Equal(3u, fluid.Count);
        Assert.Equal(1000, h.Player.GetMoney(WeaponFeature.GoldMoneyId));
        Assert.True(await h.WaitForPfAsync(GamePf.ItemUpdateNtf));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(target.EnhanceLevel, saved.Items.Single(i => i.Guid == 1001).EnhanceLevel);
        Assert.Equal(target.EnhanceExp, saved.Items.Single(i => i.Guid == 1001).EnhanceExp);
        Assert.Equal(3u, saved.Items.Single(i => i.Guid == fluid.Guid).Count);
        Assert.Equal(1000, saved.GetMoney(WeaponFeature.GoldMoneyId));
    }

    [Theory]
    [InlineData("insufficient-money", 499, 1, false)]
    [InlineData("insufficient-stack", 10000, 4, false)]
    [InlineData("duplicate-guid", 10000, 1, true)]
    [InlineData("zero-count", 10000, 0, false)]
    [InlineData("negative-count", 10000, -1, false)]
    public async Task StrongFluid_InvalidRequestDoesNotMutate(string name, int money, int count, bool duplicate)
    {
        using var h = await FeatureHarness.CreateAsync("weapon-fluid-" + name);
        var target = SeedWeapon(h, 1001);
        var fluid = h.Player.AddItem(7, 3, 1, 1, 3);
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, money);
        string entries = $"[{fluid.Guid},{count}]";
        if (duplicate) entries += $",[{fluid.Guid},1]";
        await SendAsync(h, $"{{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[{entries}]}}");
        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Equal(1u, target.EnhanceLevel);
        Assert.Equal(0u, target.EnhanceExp);
        Assert.Equal(3u, fluid.Count);
        Assert.Equal(money, h.Player.GetMoney(WeaponFeature.GoldMoneyId));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[0,1]")]
    [InlineData("[4294967296,1]")]
    [InlineData("[123]")]
    [InlineData("[123,1,99]")]
    [InlineData("{}")]
    public async Task StrongFluid_MalformedEntryRejectsEntireBatch(string invalid)
    {
        using var h = await FeatureHarness.CreateAsync("weapon-fluid-malformed");
        var target = SeedWeapon(h, 1001);
        var fluid = h.Player.AddItem(7, 3, 1, 1, 3);
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, 10000);
        await SendAsync(h, $"{{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[{fluid.Guid},1],{invalid}]}}");
        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Equal(1u, target.EnhanceLevel);
        Assert.Equal(0u, target.EnhanceExp);
        Assert.Equal(3u, fluid.Count);
        Assert.Equal(10000, h.Player.GetMoney(WeaponFeature.GoldMoneyId));
    }

    [Fact]
    public async Task StrongWeapon_UsesClientThirtyPercentFormulaAndGold()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-fed-exp");
        var target = SeedWeapon(h, 1001);
        SeedWeapon(h, 1002, level: 10);
        h.Player.SetMoney(WeaponFeature.GoldMoneyId, 10000);
        await SendAsync(h, "{\"nCmd\":1,\"nGuid\":1001,\"tbGuid\":[[1002,1]]}");
        Assert.Equal(WeaponFeature.ErrOk, Reply(h)["nError"]!.GetValue<int>());
        var table = WeaponTable.Load(TestPaths.SettingDir);
        int gained = table.RarityExp(1) + (int)Math.Floor(table.SumExpBelow(10) * 0.3);
        var (level, exp) = table.DestLevel(1, 0, gained, 40);
        Assert.Equal((uint)level, target.EnhanceLevel);
        Assert.Equal((uint)exp, target.EnhanceExp);
        Assert.Equal(10000 - table.RarityExp(1), h.Player.GetMoney(WeaponFeature.GoldMoneyId));
    }

    // ================= nCmd 2：分解 =================

    [Fact]
    public async Task 分解_必须无条件带tbParam第二项()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-dec-shape");
        SeedWeapon(h, 1001);

        await SendAsync(h, "{\"nCmd\":2,\"tbGuid\":[1001]}");

        var r = Reply(h);
        Assert.Equal(WeaponFeature.ErrOk, r["nError"]!.GetValue<int>());
        Assert.Equal(WeaponFeature.CmdDecompose, r["nCmd"]!.GetValue<int>());

        // ★★★ tbParam = [gold, [[G,D,P,L,N],…]]，**第 2 项必须存在**
        // 否则金币为 0 时客户端 `#nil` 抛（S2CCall.lua:177）
        var tb = Assert.IsType<JsonArray>(r["tbParam"]);
        Assert.Equal(2, tb.Count);
        Assert.NotNull(tb[1]);
        Assert.IsType<JsonArray>(tb[1]);
    }

    [Fact]
    public async Task 分解_1级武器金币为0时也带空数组()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-dec-zero");
        // 1 级武器累计经验为 0 ⇒ 金币公式只剩 rarity * c1，可能为 0
        SeedWeapon(h, 1001, level: 1);

        await SendAsync(h, "{\"nCmd\":2,\"tbGuid\":[1001]}");

        var tb = Assert.IsType<JsonArray>(Reply(h)["tbParam"]);
        // 无论金币是否为 0，第 2 项都在
        Assert.IsType<JsonArray>(tb[1]);
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 1001);
    }

    [Fact]
    public async Task 分解_金币与客户端公式同源()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-dec-gold");
        var target = SeedWeapon(h, 1001, level: 20);
        long goldBefore = h.Player.GetMoney((int)MoneyType.Gold);

        await SendAsync(h, "{\"nCmd\":2,\"tbGuid\":[1001]}");

        var tb = Assert.IsType<JsonArray>(Reply(h)["tbParam"]);
        long gold = tb[0]!.GetValue<long>();
        Assert.True(gold > 0, "20 级武器分解金币为 0 —— 疑似 Param 组号用错（武器是第 2 组）");
        // 金币真的进账了
        Assert.True(h.Player.GetMoney((int)MoneyType.Gold) >= goldBefore + gold);
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == target.Guid);
    }

    [Fact]
    public async Task 分解_上锁的不分解()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-dec-lock");
        SeedWeapon(h, 1001, lockOn: 1);

        await SendAsync(h, "{\"nCmd\":2,\"tbGuid\":[1001]}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Contains(h.Player.Items, i => i.Guid == 1001);
    }

    [Fact]
    public async Task 分解_多把一次结算()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-dec-multi");
        SeedWeapon(h, 1001, level: 10);
        SeedWeapon(h, 1002, level: 10);

        await SendAsync(h, "{\"nCmd\":2,\"tbGuid\":[1001,1002]}");

        Assert.Equal(WeaponFeature.ErrOk, Reply(h)["nError"]!.GetValue<int>());
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 1001);
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 1002);
    }

    // ================= nCmd 3：突破 =================

    [Fact]
    public async Task 突破_扣货币并提升上限()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-break");
        var t = WeaponTable.Load(TestPaths.SettingDir);
        int cap = t.MaxLevel(1, 0);
        var target = SeedWeapon(h, 1001, level: (uint)cap);

        var cost = t.BreakCostFor(1, 0)!;
        h.Player.SetMoney(cost.CurrencyId, cost.CurrencyCount + 1000);
        SeedWeapon(h, 1002);
        long before = h.Player.GetMoney(cost.CurrencyId);

        await SendAsync(h, "{\"nCmd\":3,\"nGuid\":1001,\"tbGuid\":[[1002,1]]}");

        Assert.Equal(WeaponFeature.ErrOk, Reply(h)["nError"]!.GetValue<int>());
        Assert.Equal(1u, target.BreakLevel);
        // ★ 扣款精确
        Assert.Equal(before - cost.CurrencyCount, h.Player.GetMoney(cost.CurrencyId));
        Assert.DoesNotContain(h.Player.Items, i => i.Guid == 1002);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(1u, saved.Items.Single(i => i.Guid == 1001).BreakLevel);
        Assert.DoesNotContain(saved.Items, i => i.Guid == 1002);
        // 上限真的提升了
        Assert.True(t.MaxLevel(1, 1) > cap);
    }

    [Fact]
    public async Task 突破_未达当前上限时拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-break-low");
        var target = SeedWeapon(h, 1001, level: 1);
        h.Player.SetMoney(2, 9_999_999);

        await SendAsync(h, "{\"nCmd\":3,\"nGuid\":1001,\"tbGuid\":[]}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Equal(0u, target.BreakLevel);
    }

    [Fact]
    public async Task 突破_货币不足时拒绝且不扣款()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-break-poor");
        var t = WeaponTable.Load(TestPaths.SettingDir);
        var target = SeedWeapon(h, 1001, level: (uint)t.MaxLevel(1, 0));
        SeedWeapon(h, 1002);
        var cost = t.BreakCostFor(1, 0)!;
        h.Player.SetMoney(cost.CurrencyId, cost.CurrencyCount - 1);

        await SendAsync(h, "{\"nCmd\":3,\"nGuid\":1001,\"tbGuid\":[[1002,1]]}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Equal(0u, target.BreakLevel);
        Assert.Contains(h.Player.Items, i => i.Guid == 1002);
        Assert.Equal(cost.CurrencyCount - 1, h.Player.GetMoney(cost.CurrencyId));
    }

    [Fact]
    public async Task 突破_次数用尽时拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-break-max");
        var t = WeaponTable.Load(TestPaths.SettingDir);
        int maxBreak = t.BreakNumFor(1);
        var target = SeedWeapon(h, 1001,
            level: (uint)t.MaxLevel(1, maxBreak), breakLevel: (uint)maxBreak);
        h.Player.SetMoney(2, 9_999_999);

        await SendAsync(h, "{\"nCmd\":3,\"nGuid\":1001,\"tbGuid\":[]}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Equal((uint)maxBreak, target.BreakLevel);
    }

    [Theory]
    [InlineData("empty", "[]", 0, false)]
    [InlineData("self", "[[1001,1]]", 0, false)]
    [InlineData("duplicate", "[[1002,1],[1002,1]]", 0, false)]
    [InlineData("locked", "[[1002,1]]", 1, false)]
    [InlineData("wrong-template", "[[1002,1]]", 0, true)]
    public async Task Break_InvalidMaterialsLeaveMoneyAndInventoryUnchanged(string name, string materials, uint locked, bool wrong)
    {
        using var h = await FeatureHarness.CreateAsync("weapon-break-invalid-" + name);
        var target = SeedWeapon(h, 1001, level: 40);
        SeedWeapon(h, 1002, d: wrong ? 5u : 1u, lockOn: locked);
        h.Player.SetMoney(2, 100000);
        await SendAsync(h, $"{{\"nCmd\":3,\"nGuid\":1001,\"tbGuid\":{materials}}}");
        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
        Assert.Equal(0u, target.BreakLevel);
        Assert.Equal(100000, h.Player.GetMoney(2));
        Assert.Contains(h.Player.Items, i => i.Guid == 1002);
    }

    [Theory]
    [InlineData(10001, true)]
    [InlineData(10000, false)]
    public async Task Break_UniversalWeaponMatchesClientRarityRule(uint universalParticular, bool allowed)
    {
        using var h = await FeatureHarness.CreateAsync("weapon-break-universal");
        var target = SeedWeapon(h, 1001, level: 40);
        SeedWeapon(h, 1002, d: 2, p: universalParticular, l: 1);
        h.Player.SetMoney(2, 100000);
        await SendAsync(h, "{\"nCmd\":3,\"nGuid\":1001,\"tbGuid\":[[1002,1]]}");
        Assert.Equal(allowed, Reply(h)["nError"]!.GetValue<int>() == 0);
        Assert.Equal(allowed ? 1u : 0u, target.BreakLevel);
        Assert.Equal(allowed, h.Player.Items.All(i => i.Guid != 1002));
        Assert.Equal(allowed ? 80000 : 100000, h.Player.GetMoney(2));
    }

    // ================= nCmd 4/5：校准 =================

    [Fact]
    public async Task 校准_两步都回执成功()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-recast");
        SeedWeapon(h, 1001);

        await SendAsync(h, "{\"nCmd\":4,\"nGuid\":1001}");
        var r4 = Reply(h);
        Assert.Equal(WeaponFeature.ErrOk, r4["nError"]!.GetValue<int>());
        Assert.Equal(WeaponFeature.CmdRecast, r4["nCmd"]!.GetValue<int>());

        await SendAsync(h, "{\"nCmd\":5,\"nGuid\":1001,\"nSelect\":2}");
        var r5 = Reply(h);
        Assert.Equal(WeaponFeature.ErrOk, r5["nError"]!.GetValue<int>());
        Assert.Equal(WeaponFeature.CmdRecastChoose, r5["nCmd"]!.GetValue<int>());
    }

    [Fact]
    public async Task 校准_非自己的武器被拒()
    {
        using var h = await FeatureHarness.CreateAsync("weapon-recast-nog");

        await SendAsync(h, "{\"nCmd\":4,\"nGuid\":888}");

        Assert.True(Reply(h)["nError"]!.GetValue<int>() > 0);
    }

    // ================= 上行解析 =================

    [Fact]
    public void 读裸guid数组()
    {
        var v = WeaponFeature.ReadGuidList(JsonNode.Parse("[1,2,3]"));
        Assert.Equal(new uint[] { 1, 2, 3 }, v.ToArray());

        // 也容忍二元组形态
        var v2 = WeaponFeature.ReadGuidList(JsonNode.Parse("[[7,1],[8,2]]"));
        Assert.Equal(new uint[] { 7, 8 }, v2.ToArray());

        Assert.Empty(WeaponFeature.ReadGuidList(JsonNode.Parse("{}")));
        Assert.Empty(WeaponFeature.ReadGuidList(null));
    }

    [Fact]
    public void 读guid与数量二元组()
    {
        var v = WeaponFeature.ReadGuidCountList(JsonNode.Parse("[[7,3],[8,1]]"));
        Assert.Equal(2, v.Count);
        Assert.Equal((7u, 3), v[0]);
        Assert.Equal((8u, 1), v[1]);

        // 裸 guid ⇒ count 记 1
        var v2 = WeaponFeature.ReadGuidCountList(JsonNode.Parse("[9]"));
        Assert.Equal((9u, 1), Assert.Single(v2));

        Assert.Empty(WeaponFeature.ReadGuidCountList(JsonNode.Parse("{}")));
    }
}
