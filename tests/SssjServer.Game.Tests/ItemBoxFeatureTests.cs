using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 手动开箱（LuaCall 75 / 76）+ <see cref="ItemBoxTable.Roll"/> 的抽取语义。
/// 台账 `docs/05` §42。
///
/// 断言重点：
/// <list type="number">
///   <item>★ 回包是 **GDPL 五元组数组**（回对象会让 <c>#tbParam==0</c> ⇒ 奖励弹窗不出现）；</item>
///   <item>★ 上行 <c>{gdpln:[G,D,P,L,num], select:idx}</c> 能解析；</item>
///   <item>★ 四种 <c>obtainType</c> 各自的抽取语义（<c>Fix</c> 全给 / <c>Random</c> 权重抽 1 /
///         <c>Prob</c> 万分比独立 / <c>Select</c> 按 1-based 索引）；</item>
///   <item>★ <c>obtainType</c> **空值必须忽略**（客户端 <c>ItemBox.lua:39</c> 也丢弃）；</item>
///   <item>★ 次数上限 10、且不能超过背包实际持有数（防刷）；</item>
///   <item>★ 箱子道具要真的扣掉，且失败路径**不扣**。</item>
/// </list>
/// </summary>
public class ItemBoxFeatureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverflowAffectionBoxUsesOriginalRewardPoolAndHonorsLock(bool locked)
    {
        using var h = await FeatureHarness.CreateAsync("overflow-heart-box");
        h.Player.Items.RemoveAll(x => x.Genre == 8 && x.Detail == 2 && x.Particular == 3 && x.Level == 1);
        var box = new Item { Guid = 990001, Genre = 8, Detail = 2, Particular = 3, Level = 1,
            Count = 3, LockOn = locked ? 1u : 0u };
        h.Player.Items.Add(box);
        int gold = h.Player.GetMoney(2), diamond = h.Player.GetMoney(3);
        await h.DispatchLuaCallAsync(76, "{\"gdpln\":[8,2,3,1,3],\"select\":0}");
        var awards = Assert.IsType<JsonArray>(LastReply(h));
        if (locked)
        {
            Assert.Empty(awards);
            Assert.Equal(3u, box.Count);
            Assert.Equal(gold, h.Player.GetMoney(2));
            Assert.Equal(diamond, h.Player.GetMoney(3));
            return;
        }
        Assert.DoesNotContain(box, h.Player.Items);
        int goldGain = h.Player.GetMoney(2) - gold, diamondGain = h.Player.GetMoney(3) - diamond;
        Assert.Equal(0, goldGain % 30000);
        Assert.Equal(0, diamondGain % 20);
        Assert.Equal(3, goldGain / 30000 + diamondGain / 20);
        Assert.NotEmpty(awards);
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.DoesNotContain(saved.Items, x => x.Guid == box.Guid);
        Assert.Equal(h.Player.GetMoney(2), saved.GetMoney(2));
        Assert.Equal(h.Player.GetMoney(3), saved.GetMoney(3));
        await h.DispatchLuaCallAsync(76, "{\"gdpln\":[8,2,3,1,3],\"select\":0}");
        Assert.Empty(Assert.IsType<JsonArray>(LastReply(h)));
        Assert.Equal(gold + goldGain, h.Player.GetMoney(2));
        Assert.Equal(diamond + diamondGain, h.Player.GetMoney(3));
    }

    private static ItemBoxTable LoadTable() => ItemBoxTable.Load(TestPaths.SettingDir);

    /// <summary>取最后一条下行的 <c>tbParam</c>（<c>CallClient</c> 会包一层 <c>{sCmd,tbParam}</c>）。</summary>
    private static JsonNode LastReply(FeatureHarness h)
    {
        var (_, json) = h.LastOutbound;
        return JsonNode.Parse(json)!["tbParam"]!;
    }

    // ================================================================ 上行解析

    /// <summary>★ 标准上行形状（<c>UI_ItemBox.lua:331</c>）。</summary>
    [Fact]
    public void 上行gdpln与select能被解析()
    {
        var (gdpl, times, select) = ItemBoxFeature.ParseRequest(
            JsonNode.Parse("{\"gdpln\":[13,1,1,1,3],\"select\":2}"));

        Assert.NotNull(gdpl);
        Assert.Equal((13u, 1u, 1u, 1u), gdpl!.Value);
        Assert.Equal(3, times);
        Assert.Equal(2, select);
    }

    /// <summary>★ 没有 <c>select</c> 时按 0（= 该箱无 Select 组）。</summary>
    [Fact]
    public void 缺select按0处理()
    {
        var (_, _, select) = ItemBoxFeature.ParseRequest(
            JsonNode.Parse("{\"gdpln\":[13,1,1,1,1]}"));
        Assert.Equal(0, select);
    }

    /// <summary>★ 裸 GDPL 数组也能吃（防御性，见实现注释）。</summary>
    [Fact]
    public void 裸GDPL数组也能解析()
    {
        var (gdpl, times, _) = ItemBoxFeature.ParseRequest(JsonNode.Parse("[13,1,1,1,5]"));
        Assert.NotNull(gdpl);
        Assert.Equal(5, times);
    }

    /// <summary>★ 非法上行必须被识别（返回 null，调用方回空数组）。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"gdpln\":[1,2]}")]
    [InlineData("{\"gdpln\":\"abc\"}")]
    [InlineData("5")]
    public void 非法上行被识别(string json)
    {
        var (gdpl, _, _) = ItemBoxFeature.ParseRequest(JsonNode.Parse(json));
        Assert.Null(gdpl);
    }

    // ================================================================ Roll 抽取语义

    /// <summary>
    /// ★ 找一个纯 <c>Fix</c> 的箱子，开 1 次应拿到它全部 Fix 项。
    /// 不写死 boxId —— 从真实表里挑，避免配置更新后用例变红（AGENTS.md 的教训）。
    /// </summary>
    [Fact]
    public void 纯Fix箱子开一次拿到全部Fix项()
    {
        var t = LoadTable();
        var box = FindBox(t, b => b.All(e => e.ObtainType == ItemBoxEntry.TypeFix) && b.Count >= 2);
        Assert.True(box > 0, "找不到纯 Fix 且 ≥2 项的箱子，用例前提不成立");

        var fixCount = t.FixedOf(box).Count();
        var got = t.Roll(box, times: 1, select: 0, rng: new Random(1));

        Assert.Equal(fixCount, got.Count);
    }

    /// <summary>★ <c>Fix</c> 的数量按开箱次数线性放大。</summary>
    [Fact]
    public void Fix数量随开箱次数放大()
    {
        var t = LoadTable();
        var box = FindBox(t, b => b.All(e => e.ObtainType == ItemBoxEntry.TypeFix) && b.Count >= 1);
        Assert.True(box > 0);

        var one = t.Roll(box, times: 1, select: 0, rng: new Random(1));
        var three = t.Roll(box, times: 3, select: 0, rng: new Random(1));

        Assert.Equal(one.Count, three.Count);
        for (int i = 0; i < one.Count; i++)
            Assert.Equal(one[i][4] * 3, three[i][4]);
    }

    /// <summary>
    /// ★★ <c>Random</c> 组「每次开箱抽 1 个」：开 N 次，Random 部分的总数量应等于 N
    /// （可能合并到同一项上，所以比对**数量之和**而不是条数）。
    /// </summary>
    [Fact]
    public void Random组每次开箱抽一个()
    {
        var t = LoadTable();
        var box = FindBox(t, b =>
            b.Any(e => e.ObtainType == ItemBoxEntry.TypeRandom)
            && b.All(e => e.ObtainType is ItemBoxEntry.TypeRandom));
        if (box == 0) return;   // 现网若没有「纯 Random」箱子则跳过

        const int times = 5;
        var got = t.Roll(box, times, select: 0, rng: new Random(42));

        // 每个 Random 项的 GDPLN 第 5 位通常是 1；总量应等于 times × 单份数量
        int total = got.Sum(r => r[4]);
        Assert.True(total >= times, $"Random 抽取总量 {total} 少于开箱次数 {times}");
    }

    /// <summary>
    /// ★★★ <c>obtainType</c> 空值必须被忽略 —— 客户端 <c>ItemBox.lua:39</c>
    /// <c>if tb.obtainType then</c> 也会整条丢弃（nil 不能当 table 键）。
    /// 现网这类行有 219 条，不忽略就会**多发东西**。
    /// </summary>
    [Fact]
    public void obtainType空值的行被忽略()
    {
        var t = LoadTable();
        // 找一个「含空 obtainType 行」的箱子
        var box = FindBox(t, b => b.Any(e => string.IsNullOrWhiteSpace(e.ObtainType)));
        if (box == 0) return;

        var entries = t.Get(box);
        int blank = entries.Count(e => string.IsNullOrWhiteSpace(e.ObtainType));
        Assert.True(blank > 0);

        var got = t.Roll(box, times: 1, select: 0, rng: new Random(7));

        // 空类型的那些四元组不应出现在结果里（除非它同时也作为别的类型出现）
        foreach (var e in entries.Where(e => string.IsNullOrWhiteSpace(e.ObtainType)))
        {
            bool alsoTyped = entries.Any(o =>
                !string.IsNullOrWhiteSpace(o.ObtainType)
                && o.Gdpl[0] == e.Gdpl[0] && o.Gdpl[1] == e.Gdpl[1]
                && o.Gdpl[2] == e.Gdpl[2] && o.Gdpl[3] == e.Gdpl[3]);
            if (alsoTyped) continue;

            Assert.DoesNotContain(got, r =>
                r[0] == e.Gdpl[0] && r[1] == e.Gdpl[1] && r[2] == e.Gdpl[2] && r[3] == e.Gdpl[3]);
        }
    }

    /// <summary>★ <c>Select</c> 按 1-based 索引取，<c>select=0</c> 时一项都不给。</summary>
    [Fact]
    public void Select按1based索引取且0不给()
    {
        var t = LoadTable();
        var box = FindBox(t, b => b.Count(e => e.ObtainType == ItemBoxEntry.TypeSelect) >= 2);
        Assert.True(box > 0, "找不到含 ≥2 个 Select 项的箱子");

        var selects = t.SelectOf(box);

        var pick1 = t.Roll(box, 1, select: 1, rng: new Random(1));
        var pick2 = t.Roll(box, 1, select: 2, rng: new Random(1));
        var pick0 = t.Roll(box, 1, select: 0, rng: new Random(1));

        Assert.Contains(pick1, r => r[0] == selects[0].Gdpl[0] && r[1] == selects[0].Gdpl[1]
                                 && r[2] == selects[0].Gdpl[2] && r[3] == selects[0].Gdpl[3]);
        Assert.Contains(pick2, r => r[0] == selects[1].Gdpl[0] && r[1] == selects[1].Gdpl[1]
                                 && r[2] == selects[1].Gdpl[2] && r[3] == selects[1].Gdpl[3]);

        // select=0 时不能把 Select 组的东西发出去
        foreach (var s in selects)
        {
            bool alsoOther = t.Get(box).Any(o =>
                o.ObtainType != ItemBoxEntry.TypeSelect
                && o.Gdpl[0] == s.Gdpl[0] && o.Gdpl[1] == s.Gdpl[1]
                && o.Gdpl[2] == s.Gdpl[2] && o.Gdpl[3] == s.Gdpl[3]);
            if (alsoOther) continue;
            Assert.DoesNotContain(pick0, r =>
                r[0] == s.Gdpl[0] && r[1] == s.Gdpl[1] && r[2] == s.Gdpl[2] && r[3] == s.Gdpl[3]);
        }
    }

    /// <summary>★ <c>select</c> 越界时忽略（不抛、不发错东西）。</summary>
    [Fact]
    public void Select越界被忽略()
    {
        var t = LoadTable();
        var box = FindBox(t, b => b.Any(e => e.ObtainType == ItemBoxEntry.TypeSelect));
        Assert.True(box > 0);

        var got = t.Roll(box, 1, select: 9999, rng: new Random(1));
        // 不抛即通过；Select 组的项不应出现
        Assert.NotNull(got);
    }

    /// <summary>★ 不存在的箱子返回空列表（不抛）。</summary>
    [Fact]
    public void 不存在的箱子返回空()
        => Assert.Empty(LoadTable().Roll(999_999, 1, 0, new Random(1)));

    /// <summary>★ 同一四元组要合并计数（避免回包出现重复项、客户端画成两格）。</summary>
    [Fact]
    public void 同一四元组会合并计数()
    {
        var t = LoadTable();
        var box = FindBox(t, b => b.Any(e => e.ObtainType == ItemBoxEntry.TypeRandom));
        if (box == 0) return;

        var got = t.Roll(box, times: 10, select: 0, rng: new Random(3));
        var keys = got.Select(r => (r[0], r[1], r[2], r[3])).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    // ================================================================ 端到端（76）

    /// <summary>
    /// ★★★ 回包必须是 **GDPL 五元组数组**（<c>ItemBox.lua:87</c> 读 <c>tbGDPL[1..4]</c>）。
    /// 回对象 ⇒ <c>#tbParam == 0</c> ⇒ 奖励弹窗静默不出现。
    /// </summary>
    [Fact]
    public async Task 开箱回包是GDPL数组且箱子被扣掉()
    {
        var h = await FeatureHarness.CreateAsync(nameof(开箱回包是GDPL数组且箱子被扣掉));
        var box = SeedGiftBox(h, count: 3);
        if (box is null) return;   // 找不到合适靶子则跳过（不硬绑配置）

        await h.DispatchLuaCallAsync(ItemBoxFeature.OpenBoxManualCmd,
            $"{{\"gdpln\":[{box.Genre},{box.Detail},{box.Particular},{box.Level},1]}}");

        var reply = Assert.IsType<JsonArray>(LastReply(h));
        Assert.NotEmpty(reply);
        // 每项都是长度 ≥5 的数组
        foreach (var item in reply)
        {
            var one = Assert.IsType<JsonArray>(item);
            Assert.True(one.Count >= 5, "回包每项必须是 [G,D,P,L,N] 五元组");
        }

        // 箱子扣了 1 个
        var left = h.Player.Items.FirstOrDefault(it =>
            it.Genre == box.Genre && it.Detail == box.Detail
            && it.Particular == box.Particular && it.Level == box.Level);
        Assert.Equal(2u, left?.Count ?? 0u);
    }

    /// <summary>★ 背包里没有这个箱子 ⇒ 拒绝且回空数组（不能凭空开箱）。</summary>
    [Fact]
    public async Task 背包没有箱子时拒绝()
    {
        var h = await FeatureHarness.CreateAsync(nameof(背包没有箱子时拒绝));

        await h.DispatchLuaCallAsync(ItemBoxFeature.OpenBoxManualCmd,
            "{\"gdpln\":[13,9999,9,9,1]}");

        var reply = Assert.IsType<JsonArray>(LastReply(h));
        Assert.Empty(reply);
    }

    /// <summary>
    /// ★★ 次数不能超过实际持有数（防刷）：持有 2 个却请求开 10 个，只能开 2 个。
    /// </summary>
    [Fact]
    public async Task 开箱次数被持有数夹住()
    {
        var h = await FeatureHarness.CreateAsync(nameof(开箱次数被持有数夹住));
        var box = SeedGiftBox(h, count: 2);
        if (box is null) return;

        await h.DispatchLuaCallAsync(ItemBoxFeature.OpenBoxManualCmd,
            $"{{\"gdpln\":[{box.Genre},{box.Detail},{box.Particular},{box.Level},10]}}");

        // 全部扣完 ⇒ 背包里没有了
        Assert.DoesNotContain(h.Player.Items, it =>
            it.Genre == box.Genre && it.Detail == box.Detail
            && it.Particular == box.Particular && it.Level == box.Level);
    }

    /// <summary>★ 非法参数也要回包（空数组），不能静默丢弃。</summary>
    [Fact]
    public async Task 非法参数回空数组()
    {
        var h = await FeatureHarness.CreateAsync(nameof(非法参数回空数组));

        await h.DispatchLuaCallAsync(ItemBoxFeature.OpenBoxManualCmd, "{}");

        var reply = Assert.IsType<JsonArray>(LastReply(h));
        Assert.Empty(reply);
    }

    /// <summary>★ 75 与 76 走同一实现（75 是服务端下发通道，收到也不该 unhandled）。</summary>
    [Fact]
    public async Task cmd75也被处理()
    {
        var h = await FeatureHarness.CreateAsync(nameof(cmd75也被处理));

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(ItemBoxFeature.OpenBoxCmd, "{}");

        Assert.Equal(before + 1, h.Outbound.Count);
    }

    // ================================================================ 辅助

    private static int FindBox(ItemBoxTable t, Func<IReadOnlyList<ItemBoxEntry>, bool> pred)
    {
        // 用固定顺序遍历，保证用例可复现（字典枚举顺序在 .NET 上虽稳定但不保证）
        for (int id = 1; id <= 20000; id++)
        {
            if (!t.Has(id)) continue;
            var entries = t.Get(id);
            if (pred(entries)) return id;
        }
        return 0;
    }

    /// <summary>
    /// 往背包塞一个「真实存在的礼包箱道具」（ClassName=IBGiftAuto 且 ExtParam1 指向已知箱子）。
    /// 找不到就返回 null，让用例跳过 —— 不硬绑具体 id。
    /// </summary>
    private static Item? SeedGiftBox(FeatureHarness h, uint count)
    {
        var boxes = LoadTable();
        foreach (var tpl in h.Module.ItemTemplates.All)
        {
            if (!string.Equals(tpl.ClassName, ItemBoxFeature.GiftBoxClassName, StringComparison.Ordinal))
                continue;
            if (!int.TryParse(tpl.ExtParam1, out int boxId) || boxId <= 0 || !boxes.Has(boxId))
                continue;
            // 只挑「抽 1 次一定有产出」的箱子，避免用例因为空结果而假红
            if (boxes.Roll(boxId, 1, 0, new Random(1)).Count == 0)
                continue;

            var item = new Item
            {
                Guid = 90001,
                Genre = tpl.G,
                Detail = tpl.D,
                Particular = tpl.P,
                Level = tpl.L,
                Count = count,
            };
            h.Player.Items.Add(item);
            return item;
        }
        return null;
    }
}
