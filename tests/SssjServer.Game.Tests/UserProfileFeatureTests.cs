using System.Text.Json.Nodes;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 个人资料页（`UI_User`）的六个命令。
///
/// 这一族与本项目其它域最大的不同：**回包是裸数组，不是对象**
/// （取证 `LuaCall.lua:1207/1254/1278` 的 `MODULE_SERVER` 分支 +
/// `UI_User.lua:1337/1368/1477` 的消费点）。包成 `{list:[...]}` 客户端会当空处理。
///
/// 好消息是三个消费点**都有 nil 保护**（`#tbParam > 0` / `if tbParam` + 内部 `{0,0,0}` 兜底），
/// 所以空数组是新号正解、不存在本项目常见的「少个字段就抛」风险 —— 断言重点因此放在
/// **形状**（必须是数组）与**过滤规则**（只回实际持有的 guid）上。
/// </summary>
public class UserProfileFeatureTests
{
    private static JsonNode Payload(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var root = Assert.IsType<JsonObject>(JsonNode.Parse(json));
        return root["tbParam"]!;
    }

    /// <summary>★ 三个 getter 的 tbParam 必须是 JSON **数组**，不能是对象。</summary>
    [Theory]
    [InlineData(UserProfileFeature.GetSignCmd)]
    [InlineData(UserProfileFeature.GetExhibitionItemCmd)]
    [InlineData(UserProfileFeature.GetExhibitionWeaponCmd)]
    public async Task Getters_ReplyWithBareArray(int cmd)
    {
        using var h = await FeatureHarness.CreateAsync($"prof-shape-{cmd}");

        await h.DispatchLuaCallAsync(cmd, "{}");

        var tbParam = Payload(h);
        Assert.IsType<JsonArray>(tbParam);
        // LuaCall 数字命令族：成功回包绝不能带 nError（客户端是真值判断，0 也为真）
        Assert.DoesNotContain("nError", tbParam.ToJsonString());
    }

    /// <summary>新号签名是空串 —— 客户端据此显示 `ui.UI_User.DefaultSign`（`UI_User.lua:1339`）。</summary>
    [Fact]
    public async Task GetSign_NewPlayer_ReturnsSingleEmptyString()
    {
        using var h = await FeatureHarness.CreateAsync("prof-sign-new");

        await h.DispatchLuaCallAsync(UserProfileFeature.GetSignCmd, "{}");

        var arr = Assert.IsType<JsonArray>(Payload(h));
        // ★ 必须有 1 个元素：客户端 `if tbParam and #tbParam > 0` 才会处理
        Assert.Single(arr);
        Assert.Equal("", arr[0]!.GetValue<string>());
    }

    [Fact]
    public async Task SetSign_ThenGet_RoundTrips()
    {
        using var h = await FeatureHarness.CreateAsync("prof-sign-set");

        await h.DispatchLuaCallAsync(UserProfileFeature.SetSignCmd, """["今天也要元气满满"]""");
        Assert.Equal("今天也要元气满满", UserProfileFeature.ReadSign(h.Player));

        await h.DispatchLuaCallAsync(UserProfileFeature.GetSignCmd, "{}");
        var arr = Assert.IsType<JsonArray>(Payload(h));
        Assert.Equal("今天也要元气满满", arr[0]!.GetValue<string>());
    }

    /// <summary>超长签名被截断（存档保护，客户端未取证上限）。</summary>
    [Fact]
    public async Task SetSign_TooLong_IsTruncated()
    {
        using var h = await FeatureHarness.CreateAsync("prof-sign-long");
        string longSign = new string('啊', UserProfileFeature.SignMaxLength + 50);

        await h.DispatchLuaCallAsync(UserProfileFeature.SetSignCmd, $"[\"{longSign}\"]");

        Assert.Equal(UserProfileFeature.SignMaxLength, UserProfileFeature.ReadSign(h.Player).Length);
    }

    /// <summary>签名的上行形状既可能是裸数组也可能是对象包装，两种都要吃。</summary>
    [Theory]
    [InlineData("""["abc"]""")]
    [InlineData("""{"sign":"abc"}""")]
    [InlineData("""{"szSign":"abc"}""")]
    public async Task SetSign_AcceptsBothShapes(string json)
    {
        using var h = await FeatureHarness.CreateAsync($"prof-sign-{json.Length}");
        await h.DispatchLuaCallAsync(UserProfileFeature.SetSignCmd, json);
        Assert.Equal("abc", UserProfileFeature.ReadSign(h.Player));
    }

    // ================= 展示角色卡 =================

    /// <summary>
    /// ★ 只回**实际持有**的卡：客户端 `Game.EquipManager.GetCharacterCard(guid)` 拿不到
    /// 就把那个槽画空（`UI_User.lua:1372`），回一个已不存在的 guid 等于白占一个槽。
    /// </summary>
    [Fact]
    public async Task GetExhibitionItem_FiltersOutGuidsPlayerNoLongerOwns()
    {
        using var h = await FeatureHarness.CreateAsync("prof-card-filter");
        var card = h.Player.AddItem(1, 1, 1, 3, count: 1);      // genre 1 = 角色卡

        // 存两个：一个真持有、一个不存在
        await h.DispatchLuaCallAsync(UserProfileFeature.SetExhibitionItemCmd,
                                     $"[{card.Guid},999999]");
        await h.DispatchLuaCallAsync(UserProfileFeature.GetExhibitionItemCmd, "{}");

        var arr = Assert.IsType<JsonArray>(Payload(h));
        Assert.Equal(3, arr.Count);
        Assert.Equal(card.Guid, arr[0]!.GetValue<uint>());
        Assert.Equal(0u, arr[1]!.GetValue<uint>());
        Assert.Equal(0u, arr[2]!.GetValue<uint>());
    }

    /// <summary>最多 3 个槽（`UI_User.lua:1370` 的 `{0,0,0}`）。</summary>
    [Fact]
    public async Task SetExhibitionItem_KeepsAtMostThreeSlots()
    {
        using var h = await FeatureHarness.CreateAsync("prof-card-cap");
        var guids = Enumerable.Range(0, 5)
            .Select(i => h.Player.AddItem(1, 1, (uint)(i + 1), 3, count: 1).Guid).ToList();

        await h.DispatchLuaCallAsync(UserProfileFeature.SetExhibitionItemCmd,
                                     $"[{string.Join(",", guids)}]");

        Assert.Equal(UserProfileFeature.ExhibitionSlots,
                     UserProfileFeature.ReadSlots(h.Player, "cards").Count);
    }

    // ================= 展示武器 =================

    /// <summary>
    /// ★ 展示武器现在必然是空数组：服务端还不下发 genre 2 武器
    /// （`docs/05` §0.3 的四字段红线），玩家背包里没有武器实例。
    /// 回 guid 只会让 `GetWeapon` 返回 nil、那个槽画空 —— 所以过滤掉才是对的。
    /// </summary>
    [Fact]
    public async Task GetExhibitionWeapon_IsEmptyBecauseServerCannotGrantWeapons()
    {
        using var h = await FeatureHarness.CreateAsync("prof-weapon-empty");
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 2);

        // 就算存了 guid，也要因为「没真持有」而被过滤掉
        await h.DispatchLuaCallAsync(UserProfileFeature.SetExhibitionWeaponCmd, "[12345]");
        await h.DispatchLuaCallAsync(UserProfileFeature.GetExhibitionWeaponCmd, "{}");

        Assert.Equal(new uint[] { 0, 0, 0 }, Assert.IsType<JsonArray>(Payload(h)).Select(n => n!.GetValue<uint>()));
    }

    // ================= 持久化 / 健壮性 =================

    [Fact]
    public async Task Profile_SurvivesStoreRoundTrip()
    {
        using var h = await FeatureHarness.CreateAsync("prof-persist");
        await h.DispatchLuaCallAsync(UserProfileFeature.SetSignCmd, """["存档测试"]""");

        h.Module.Store.Save(h.Player);
        var reloaded = h.Module.Store.Load("prof-persist");

        Assert.NotNull(reloaded);
        Assert.Equal("存档测试", UserProfileFeature.ReadSign(reloaded!));
    }

    /// <summary>畸形上行不抛（客户端理论上不会发，但服务端不能因此崩）。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""["x","y"]""")]
    public async Task Setters_MalformedParam_DoNotThrow(string json)
    {
        using var h = await FeatureHarness.CreateAsync($"prof-bad-{json.Length}");
        await h.DispatchLuaCallAsync(UserProfileFeature.SetSignCmd, json);
        await h.DispatchLuaCallAsync(UserProfileFeature.SetExhibitionItemCmd, json);
        await h.DispatchLuaCallAsync(UserProfileFeature.SetExhibitionWeaponCmd, json);
    }

    /// <summary>guid 解析：0 与非数字都要被丢掉（0 是客户端的「空槽」占位）。</summary>
    [Fact]
    public void ParseGuids_PreservesPositionsAndCapsAtThree()
    {
        Assert.Equal(new uint[] { 7, 0, 0 },
            UserProfileFeature.ParseGuids(JsonNode.Parse("""[7,0,"x",9,null]""")));
        // 对象包装的几种键名都吃
        Assert.Equal(new uint[] { 5 },
            UserProfileFeature.ParseGuids(JsonNode.Parse("""{"tbItems":[5]}""")));
        Assert.Empty(UserProfileFeature.ParseGuids(JsonNode.Parse("{}")));
        Assert.Empty(UserProfileFeature.ParseGuids(null));
    }

    [Theory]
    [InlineData(1, 74, 73, "cards")]
    [InlineData(2, 78, 77, "weapons")]
    public async Task ExhibitionSlotsPreserveGapsReplacementAndPersistence(int genre, int setter, int getter, string key)
    {
        using var h = await FeatureHarness.CreateAsync("profile-gaps-" + genre);
        var first = h.Player.AddItem((uint)genre, 1, 1, 3, count: 1);
        var second = h.Player.AddItem((uint)genre, 1, 2, 3, count: 1);
        await h.DispatchLuaCallAsync(setter, $"[0,{first.Guid},{second.Guid}]");
        Assert.Equal(new uint[] { 0, first.Guid, second.Guid }, UserProfileFeature.ReadSlots(h.Player, key));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(new uint[] { 0, first.Guid, second.Guid }, UserProfileFeature.ReadSlots(saved, key));
        first.Count = 0;
        await h.DispatchLuaCallAsync(getter, "{}");
        Assert.Equal(new uint[] { 0, 0, second.Guid }, Assert.IsType<JsonArray>(Payload(h)).Select(n => n!.GetValue<uint>()));
        await h.DispatchLuaCallAsync(setter, $"[{second.Guid},0,0]");
        Assert.Equal(new uint[] { second.Guid, 0, 0 }, UserProfileFeature.ReadSlots(h.Player, key));
        await h.DispatchLuaCallAsync(setter, "{}");
        Assert.Equal(new uint[] { second.Guid, 0, 0 }, UserProfileFeature.ReadSlots(h.Player, key));
        await h.DispatchLuaCallAsync(setter, "[]");
        Assert.Equal(new uint[] { 0, 0, 0 }, UserProfileFeature.ReadSlots(h.Player, key));
    }

    [Theory]
    [InlineData(1, 74, "cards")]
    [InlineData(2, 78, "weapons")]
    public async Task ExhibitionRejectsWrongGenreAndDuplicateInstancesWithoutShifting(int genre, int setter, string key)
    {
        using var h = await FeatureHarness.CreateAsync("profile-owned-" + genre);
        var held = h.Player.AddItem((uint)genre, 1, 1, 3, count: 1);
        var other = h.Player.AddItem((uint)(genre == 1 ? 2 : 1), 1, 1, 3, count: 1);
        await h.DispatchLuaCallAsync(setter, $"[{other.Guid},{held.Guid},{held.Guid}]");
        Assert.Equal(new uint[] { 0, held.Guid, 0 }, UserProfileFeature.ReadSlots(h.Player, key));
    }

    /// <summary>命令号与 `LuaCall.lua:69..77` 的定义一致。</summary>
    [Fact]
    public void CommandIds_MatchLuaCallDefinition()
    {
        Assert.Equal(71, UserProfileFeature.GetSignCmd);
        Assert.Equal(72, UserProfileFeature.SetSignCmd);
        Assert.Equal(73, UserProfileFeature.GetExhibitionItemCmd);
        Assert.Equal(74, UserProfileFeature.SetExhibitionItemCmd);
        Assert.Equal(77, UserProfileFeature.GetExhibitionWeaponCmd);
        Assert.Equal(78, UserProfileFeature.SetExhibitionWeaponCmd);
        Assert.Equal(3, UserProfileFeature.ExhibitionSlots);
    }
}
