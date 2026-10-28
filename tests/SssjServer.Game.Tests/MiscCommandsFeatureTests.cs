using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 杂项常驻三条（LuaCall 171 / 240 / 353）。台账 `docs/05` §54。
///
/// 断言重点（三条的回包约定**各不相同**，这正是「不能整域一刀切」的又一例）：
/// <list type="number">
///   <item>★ <b>171</b> 回 <c>{ret=true}</c>（客户端判 <c>ret == false</c> 才拒绝）；</item>
///   <item>★★★ <b>240</b> <b>不回包</b> —— 客户端 else 分支整段是空的；</item>
///   <item>★ <b>353</b> 回**无参**空对象（客户端只靠「收到回包」清延迟发送态，
///     不回会导致它反复重发）。</item>
/// </list>
/// </summary>
public class MiscCommandsFeatureTests
{
    private static JsonNode LastTb(FeatureHarness h)
    {
        var (_, json) = h.LastOutbound;
        return JsonNode.Parse(json)!["tbParam"]!;
    }

    // ================= 171 随机事件完成 =================

    /// <summary>★ 必须回 <c>{ret=true}</c>：客户端据此才走 <c>Reward()</c>。</summary>
    [Fact]
    public async Task 随机事件完成回ret为true()
    {
        var h = await FeatureHarness.CreateAsync(nameof(随机事件完成回ret为true));

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdEventComplete, "{}");

        Assert.Equal(before + 1, h.Outbound.Count);
        var o = Assert.IsType<JsonObject>(LastTb(h));
        Assert.True(o["ret"]!.GetValue<bool>());
    }

    /// <summary>★ 上行是裸数组也不能抛。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task 随机事件完成容忍非对象上行(string tbParam)
    {
        var h = await FeatureHarness.CreateAsync("misc-171-" + tbParam.Length);

        var ex = await Record.ExceptionAsync(() =>
            h.DispatchLuaCallAsync(MiscCommandsFeature.CmdEventComplete, tbParam));

        Assert.Null(ex);
    }

    // ================= 240 少女体力 =================

    /// <summary>
    /// ★★★ 本命令**不能回包** —— 客户端 <c>else</c> 分支整段是空的（只有注释）。
    /// 回了不会崩，但那是多余下行；这条断言锁住「按原厂约定不回」。
    /// </summary>
    [Fact]
    public async Task 少女体力同步不回包()
    {
        var h = await FeatureHarness.CreateAsync(nameof(少女体力同步不回包));

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdCafeGirlVigor,
            """{"girlid":[1,2,3]}""");

        Assert.Equal(before, h.Outbound.Count);
    }

    /// <summary>体力同步会给每个少女记时间戳（跨会话保留）。</summary>
    [Fact]
    public async Task 少女体力同步会落库()
    {
        var h = await FeatureHarness.CreateAsync(nameof(少女体力同步会落库));

        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdCafeGirlVigor,
            """{"girlid":[1,5]}""");

        // Unassigned girls do not recover; obsolete group-3 offset 17 is no longer written.
        Assert.NotNull(h.Player.Extra.GetValueOrDefault(GirlMoodRecovery.ExtraKey));
        Assert.Equal(0, h.Player.GetTask(3, GmUnlock.GirlTaskId(1, 17)));
        Assert.NotNull(new PlayerStore(h.Options).Load(h.Player.Account));
    }

    /// <summary>★ <c>girlid</c> 可能是单个数字而不是数组，两种都要吃下。</summary>
    [Theory]
    [InlineData("""{"girlid":[1,2]}""", 2)]
    [InlineData("""{"girlid":7}""", 1)]
    [InlineData("""{"girlid":[]}""", 0)]
    [InlineData("""{}""", 0)]
    [InlineData("""[]""", 0)]
    public void 少女id解析容忍数组与单值(string json, int expected)
    {
        var ids = MiscCommandsFeature.ReadGirlIds(JsonNode.Parse(json));
        Assert.Equal(expected, ids.Count);
    }

    // ================= 353 推送开关 =================

    /// <summary>
    /// ★ 必须回包（哪怕是空对象）：客户端靠「收到回包」执行
    /// <c>ClearDelaySendState()</c>，不回会**反复重发**这条命令。
    /// </summary>
    [Fact]
    public async Task 推送开关必须回包且是空对象()
    {
        var h = await FeatureHarness.CreateAsync(nameof(推送开关必须回包且是空对象));

        int before = h.Outbound.Count;
        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdSetPushEnable,
            """{"Switch":1,"Silence":0,"GetEnergy":1,"EnergyCover":0}""");

        Assert.Equal(before + 1, h.Outbound.Count);
        var o = Assert.IsType<JsonObject>(LastTb(h));
        Assert.Empty(o);           // ★ 无参
    }

    /// <summary>四个开关都要落库，且 0/1 语义正确。</summary>
    [Fact]
    public async Task 推送开关落库()
    {
        var h = await FeatureHarness.CreateAsync(nameof(推送开关落库));

        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdSetPushEnable,
            """{"Switch":1,"Silence":0,"GetEnergy":1,"EnergyCover":0}""");

        var saved = Assert.IsType<JsonObject>(h.Player.Extra[MiscCommandsFeature.PushExtraKey]);
        Assert.Equal(1, saved["Switch"]!.GetValue<int>());
        Assert.Equal(0, saved["Silence"]!.GetValue<int>());
        Assert.Equal(1, saved["GetEnergy"]!.GetValue<int>());
        Assert.Equal(0, saved["EnergyCover"]!.GetValue<int>());
    }

    /// <summary>★ 缺字段时按「开启」兜底（与客户端默认值一致）。</summary>
    [Fact]
    public async Task 推送开关缺字段按开启兜底()
    {
        var h = await FeatureHarness.CreateAsync(nameof(推送开关缺字段按开启兜底));

        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdSetPushEnable, "{}");

        var saved = Assert.IsType<JsonObject>(h.Player.Extra[MiscCommandsFeature.PushExtraKey]);
        Assert.Equal(1, saved["Switch"]!.GetValue<int>());
    }

    /// <summary>★ 布尔形式的上行也要吃下（Lua 可能编成 true/false）。</summary>
    [Fact]
    public async Task 推送开关容忍布尔上行()
    {
        var h = await FeatureHarness.CreateAsync(nameof(推送开关容忍布尔上行));

        await h.DispatchLuaCallAsync(MiscCommandsFeature.CmdSetPushEnable,
            """{"Switch":false,"Silence":true}""");

        var saved = Assert.IsType<JsonObject>(h.Player.Extra[MiscCommandsFeature.PushExtraKey]);
        Assert.Equal(0, saved["Switch"]!.GetValue<int>());
        Assert.Equal(1, saved["Silence"]!.GetValue<int>());
    }

    // ================= 装配 =================

    [Fact]
    public async Task 装配()
    {
        var h = await FeatureHarness.CreateAsync(nameof(装配));

        Assert.NotNull(h.Module.FindFeature<MiscCommandsFeature>());
        Assert.Equal(new[] { 171, 240, 353 }, MiscCommandsFeature.HandledCmds);
    }
}
