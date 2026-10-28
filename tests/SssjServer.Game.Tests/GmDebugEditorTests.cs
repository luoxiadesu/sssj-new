using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 原服 GM 通道（<c>sFunction = "DebugEditor"</c>）的兼容层。
///
/// 这一族的价值在于「客户端自带的 GM 界面与自动化用例能直接驱动本服务端」，
/// 所以断言围绕两件事：
/// <list type="number">
/// <item>★ 参数是 **Lua 源码**而不是 JSON —— 必须走 <see cref="LuaCallRouter.MapRawFunction"/>。
///   用 <c>MapFunction</c> 注册的话 <c>JsonNode.Parse("GM:UnlockAllChapter();")</c> 直接抛，
///   handler 永远收不到（历史上就是这么静默丢掉的）。</item>
/// <item>命令名/参数个数与原服取证一致（<c>AutoTestCase.lua:190..539</c>、
///   <c>GMTable.txt</c> 的 Target=2 行）。</item>
/// </list>
/// </summary>
public class GmDebugEditorTests
{
    private static Task SendAsync(FeatureHarness h, string luaCode)
        => h.LuaCalls.DispatchAsync(h.Session, GmDebugEditorFeature.Function, luaCode);

    // ================= 参数解析 =================

    [Fact]
    public void ParseArgs_IntegersCommasAndTables()
    {
        Assert.Equal(new[] { 1, 1, 1 }, GmDebugEditorFeature.ParseArgs("1,1,1"));
        Assert.Equal(new[] { 300 }, GmDebugEditorFeature.ParseArgs(" 300 "));
        Assert.Empty(GmDebugEditorFeature.ParseArgs(""));
        // 表参数摊平（GM:AddNCafeItem({{100}}) 一族）
        Assert.Equal(new[] { 100 }, GmDebugEditorFeature.ParseArgs("{{100}}"));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, GmDebugEditorFeature.ParseArgs("1, 2,3 ,4,  5"));
        // 非整数忽略，不抛
        Assert.Equal(new[] { 7 }, GmDebugEditorFeature.ParseArgs("'name', 7, nil"));
    }

    // ================= 全量类命令 =================

    /// <summary>
    /// <c>GM:UnlockAllChapter();</c> —— 原服「全关卡」（取证 <c>UI_GM.lua:596/:719</c>、
    /// <c>AutoTestCase.lua:520</c>），无参 = 全部关卡三星。
    /// </summary>
    [Fact]
    public async Task UnlockAllChapter_NoArgs_ThreeStarsEverything()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-chapter");
        Assert.Empty(h.Player.Levels);

        await SendAsync(h, "GM:UnlockAllChapter();");

        Assert.Equal(h.Module.Chapters.Count, h.Player.Levels.Count);
        Assert.All(h.Player.Levels.Values, s => Assert.Equal(MapList.StarMask, MapList.Stars(s)));
    }

    /// <summary>
    /// <c>GM:UnlockAllChapter(ch,idx,diff)</c> —— 带参 = 只记那一关
    /// （取证 <c>UI_GM.lua:587</c> 的 <c>string.format('GM:UnlockAllChapter(%d,%d,%d);', …)</c>）。
    /// </summary>
    [Fact]
    public async Task UnlockAllChapter_WithArgs_MarksOnlyThatLevel()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-one");

        await SendAsync(h, "GM:UnlockAllChapter(2,4,1);");

        Assert.Single(h.Player.Levels);
        uint mapId = MapList.MapId(2, 4, 1);
        Assert.Equal(MapList.StarMask, MapList.Stars(h.Player.GetLevelStar(mapId)));
        Assert.Equal(1, MapList.PassCount(h.Player.GetLevelStar(mapId)));
    }

    [Fact]
    public async Task AddAllNormalItem_FillsBag()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-item");
        int before = h.Player.Items.Count;

        await SendAsync(h, "GM:AddAllNormalItem();");

        // 2120 条模板 - 货币类 - 图标缺失被跳过的那批（GmSkipMissingIcon 缺省 true）
        Assert.True(h.Player.Items.Count > before + 1800,
            $"背包只从 {before} 涨到 {h.Player.Items.Count}");
        Assert.Equal(h.Options.GmMoneyAmount, h.Player.GetMoney(MoneyType.Gold));
    }

    [Fact]
    public async Task AddAllCardItem_GrantsEveryCardAndGirl()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-card");

        await SendAsync(h, "GM:AddAllCardItem();");

        var expected = CharacterCardTable.Load(TestPaths.SettingDir).All
            .Where(c => GmUnlock.IsValidGirlId(c.D)).ToArray();
        Assert.Equal(expected.Select(c => c.D).Distinct().Order(), h.Player.Girls.Select(g => g.Girlid).Order());
        Assert.Equal(expected.Select(c => (c.G, c.D, c.P, c.L)).Order(),
            h.Player.Items.Where(i => i.Genre == CharacterCardTable.CardGenre)
                .Select(i => (i.Genre, i.Detail, i.Particular, i.Level)).Order());
    }

    // ================= 玩家数值 =================

    /// <summary><c>GM:SetPlayerLevel(n)</c>（取证 <c>AutoTestCase.lua:211</c>）。</summary>
    [Fact]
    public async Task SetPlayerLevel_SetsExactLevel()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-level");

        await SendAsync(h, "GM:SetPlayerLevel(57);");

        Assert.Equal(57, h.Player.Level);
        Assert.Equal(0, h.Player.Exp);
    }

    /// <summary>无参/0 时取配置表满级（本服务端的便利扩展，原服要求必填）。</summary>
    [Fact]
    public async Task SetPlayerLevel_NoArgs_UsesTableMaxLevel()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-maxlevel");

        await SendAsync(h, "GM:SetPlayerLevel();");

        Assert.Equal(PlayerExpTable.Load(TestPaths.SettingDir).MaxLevel, h.Player.Level);
    }

    [Theory]
    [InlineData("GM:SetPlayerGold(12345);", MoneyType.Gold, 12345)]
    [InlineData("GM:SetPlayerDiamond(777);", MoneyType.FreeDiamond, 777)]
    [InlineData("GM:SetPlayerPayDiamond(88);", MoneyType.PayDiamond, 88)]
    [InlineData("GM:SetPlayerVigour(66);", MoneyType.Vigour, 66)]
    public async Task SetPlayerMoney_Commands(string code, int moneyId, int expected)
    {
        using var h = await FeatureHarness.CreateAsync($"gm-dbg-money-{moneyId}");

        await SendAsync(h, code);

        Assert.Equal(expected, h.Player.GetMoney(moneyId));
    }

    /// <summary><c>GM:SetPlayerItem(g,d,p,l,n)</c>（取证 <c>AutoTestCase.lua:277</c>）。</summary>
    [Fact]
    public async Task SetPlayerItem_GrantsThatTuple()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-setitem");

        await SendAsync(h, "GM:SetPlayerItem(5, 1, 1, 2, 7);");

        var item = h.Player.Items.FirstOrDefault(i => i.Genre == 5 && i.Detail == 1
                                                   && i.Particular == 1 && i.Level == 2);
        Assert.NotNull(item);
        Assert.Equal(7u, item!.Count);
    }

    // ================= 任务 / 成就 =================

    /// <summary>
    /// <c>me:SetTaskValue(g,id,v)</c>（取证 <c>AutoTestCase.lua:190</c>）——
    /// 注意它的调用对象是 <c>me</c> 而不是 <c>GM</c>，正则必须两者都吃。
    /// </summary>
    [Fact]
    public async Task SetTaskValue_OnMeObject_Works()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-task");

        await SendAsync(h, "me:SetTaskValue(20, 123, 456);");

        Assert.Equal(456, h.Player.GetTask(20, 123));
    }

    /// <summary>
    /// <c>GM:AchievementComplete(type,id)</c>，type 1 任务 / 2 成就 / 3 活动
    /// （取证 <c>GMTable.txt</c> 的 <c>ENUM.Task.Achievement.Activity|Task:1:Achive:2:Act:3</c>）。
    /// </summary>
    [Fact]
    public async Task AchievementComplete_MarksThatEntryComplete()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-achieve");
        var entry = h.Module.Missions.Achieve.Values.First();

        await SendAsync(h, $"GM:AchievementComplete(2,{entry.Id});");

        int value = h.Player.GetTask(entry.Group, entry.Id);
        Assert.Equal(entry.RequiredCount(achieveKey: true), MissionFeature.ProgressOf(value));
        Assert.Equal(1, MissionFeature.ClaimedOf(value));   // 缺省 GmMissionClaimed=true
    }

    [Fact]
    public async Task AchievementComplete_UnknownType_DoesNotThrow()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-achieve-bad");

        await SendAsync(h, "GM:AchievementComplete(9,1);");   // type 9 不存在

        Assert.DoesNotContain(h.Player.Tasks, kv => Player.TaskGroupOf(kv.Key) == 6);
    }

    // ================= 多语句 / 未知命令 / 健壮性 =================

    /// <summary>
    /// 一条 sJsonParam 里可以有多条语句（<c>GMTable.txt</c> 的 <c>Code</c> 列有用 <c>;</c> 串联的，
    /// 例如 OpenVowsDebug 那行）。
    /// </summary>
    [Fact]
    public async Task MultipleStatements_AllExecuted()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-multi");

        await SendAsync(h, "GM:SetPlayerGold(11); me:SetTaskValue(44, 102, 1);");

        Assert.Equal(11, h.Player.GetMoney(MoneyType.Gold));
        Assert.Equal(1, h.Player.GetTask(44, 102));
    }

    /// <summary>未知命令只记 WARN、不抛、不改数据 —— 它是「下一个要实现什么」的信号。</summary>
    [Fact]
    public async Task UnknownCommand_IsIgnoredSafely()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-unknown");
        int items = h.Player.Items.Count;

        await SendAsync(h, "GM:SomethingNobodyImplemented(1,2,3);");

        Assert.Equal(items, h.Player.Items.Count);
    }

    /// <summary>已知但未实现的原服命令（武器/符文/养成域）同样安全。</summary>
    [Fact]
    public async Task KnownButUnmappedCommand_IsIgnoredSafely()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-unmapped");
        Assert.Contains("AddAllWeaponItem", GmDebugEditorFeature.Unmapped);

        await SendAsync(h, "GM:AddAllWeaponItem();");

        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 2);
    }

    /// <summary>解析不出「模块:方法(…)」时不抛（例如客户端发来一段别的 Lua）。</summary>
    [Fact]
    public async Task Garbage_DoesNotThrow()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-garbage");

        await SendAsync(h, "local x = 1 + 1");
        await SendAsync(h, "");
    }

    /// <summary>
    /// ★ 回归：这条命令必须注册成 <b>raw</b> 处理器。
    /// 若被误改回 <c>MapFunction</c>，<c>JsonNode.Parse</c> 会在 handler 之前就失败，
    /// 于是下面的等级不会变 —— 这条断言就是那道闸门。
    /// </summary>
    [Fact]
    public async Task Channel_MustBeRegisteredAsRawFunction()
    {
        using var h = await FeatureHarness.CreateAsync("gm-dbg-raw");

        // 一段绝对不是合法 JSON 的 Lua 源码
        await SendAsync(h, "GM:SetPlayerLevel(42);");

        Assert.Equal(42, h.Player.Level);
    }
}
