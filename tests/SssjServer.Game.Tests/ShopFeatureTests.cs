using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 商店域（LuaCall 11 列表 / 12 刷新 / 13 购买 / 14 家具批量买）。
///
/// 断言分四类：
/// <list type="number">
/// <item><b>钉住现网配置事实</b> —— 52 个商店 / 1896 件商品、20001 的刷新阶梯、
///   「1643 行没 Group、1816 行没 DiscountList」这些数字一变，下面的端到端期望值全要改。</item>
/// <item><b>★ cmd 11/12 的四字段门禁</b>（<c>shopid</c>/<c>isopen</c>/<c>refreshcount</c>/<c>goodslist</c>）——
///   缺任一客户端 <c>GoodsLogicC.lua:273..279</c> **静默 return**：没有报错、界面不刷新、
///   服务端日志一切正常。本域最难查的失败，必须有断言守住，还要守住**类型**
///   （门禁判的是 <c>type(...) ~= "number"</c>，字符串化的 shopid 一样过不了）。</item>
/// <item><b>★ 货架项的必填字段</b> —— <c>discount</c> 不给会被回落成「一折」或「免费」；
///   五个 <c>*buynum</c> 不给会让 <c>UI_ShopTips:_ShowLmtCtrl</c>（<c>UI_ShopTips.lua:237..258</c>）
///   做 nil 减法直接报错；而 <c>buynum</c> 键**不能出现**（会改 <c>_getNextPriceIdx</c> 的档位）。</item>
/// <item><b>真实扣费 + 真实发货 + 失败路径不抛</b> —— 货币「免费+付费」合并语义
///   （<c>MoneyLogic.lua:302..312</c>）、限量、取价档位都要与客户端算的一致，
///   否则出现「显示 20 实扣 80」。</item>
/// </list>
/// </summary>
public class ShopFeatureTests
{
    // ---------------- 靶子：20001「普通商店」 ----------------

    /// <summary>常驻普通商店。无时间窗、8 个 Group、RefreshRule=1、可手动刷新 5 次。</summary>
    private const int NormalShopId = 20001;

    /// <summary>
    /// ★ 确定性锚点：20001 的 Group <c>[3]</c> 与 <c>[4]</c> **各只有 1 件商品**
    /// （20111 / 20112，实测），所以「每组抽 1 件」的货架**必然**包含它们 ——
    /// 端到端购买测试不用碰随机性。
    /// </summary>
    private const int AnchorGoodsId = 20111;      // [7-3-1-3-1] 3级武器升级零件箱，Price1 = 3|20
    private const int AnchorGroup = 3;
    private const int AnchorPrice = 20;
    private const uint AnchorG = 7, AnchorD = 3, AnchorP = 1, AnchorL = 3;

    /// <summary>20001 组 [4] 的独苗（20112，Price1 = 3|80），用来验「同一店多件商品」。</summary>
    private const int Anchor2GoodsId = 20112;

    // ---------------- 靶子：20008「演习商店」 ----------------

    /// <summary>无 Group、无折扣表 ⇒ 全部上架且折扣恒 100，价格可精确预期。</summary>
    private const int ExerciseShopId = 20008;

    /// <summary>20008 的多档价商品：<c>Price1 = 24|[500,500,1000,1000]</c>、<c>WeekLimitNum = 4</c>。</summary>
    private const int StairGoodsId = 20804;
    private const int StairMoneyId = 24;          // 演习积分（MoneyLogic.eMoneyType_RankScore）

    // ---------------- 靶子：40001「咖啡馆家具店」 ----------------

    private const int CafeShopId = 40001;
    private const int CafeGoodsA = 101;           // [11-1-1-1-1] 廉价单人桌椅，Price1 = 7|420
    private const int CafeGoodsB = 102;           // [11-1-2-1-1] 廉价双人桌椅，Price1 = 7|475
    private const int CafePriceA = 420, CafePriceB = 475;

    /// <summary>已过期的活动商店（2019-11-23 ~ 2019-12-06），用来验 <c>isopen=0</c> 与时间窗成对下发。</summary>
    private const int ExpiredShopId = 30001;

    // =====================================================================
    //  工具
    // =====================================================================

    private static ShopTable Table() => ShopTable.Load(TestPaths.SettingDir);

    /// <summary>取最后一条 LuaCall 下行的 <c>tbParam</c>（并校验 sCmd）。</summary>
    private static JsonNode LastLuaCall(FeatureHarness h, int expectCmd)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal("LuaCall", function);
        var root = JsonNode.Parse(json)!;
        Assert.Equal(expectCmd, root["sCmd"]!.GetValue<int>());
        return root["tbParam"]!;
    }

    private static JsonObject LastLuaCallObject(FeatureHarness h, int expectCmd)
        => Assert.IsType<JsonObject>(LastLuaCall(h, expectCmd));

    /// <summary>发 cmd 11 并返回回包。</summary>
    private static async Task<JsonObject> ReqListAsync(FeatureHarness h, int shopId)
    {
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqGoodsList, $"{{\"shopid\":{shopId}}}");
        return LastLuaCallObject(h, ShopFeature.CmdReqGoodsList);
    }

    private static JsonArray GoodsListOf(JsonObject reply)
        => Assert.IsType<JsonArray>(reply["goodslist"]);

    private static JsonObject EntryOf(JsonObject reply, int goodsId)
    {
        foreach (var node in GoodsListOf(reply))
        {
            if (node is JsonObject o && o["goodsid"]!.GetValue<int>() == goodsId)
                return o;
        }
        throw new Xunit.Sdk.XunitException($"货架里找不到 goodsid={goodsId}");
    }

    private static Task BuyAsync(FeatureHarness h, int shopId, int goodsId, int buyNum)
        => h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyGoods,
            $"{{\"shopid\":{shopId},\"goodsid\":{goodsId},\"buynum\":{buyNum}}}");

    // =====================================================================
    //  一、配置表事实（现网数字，改表要同步改这里）
    // =====================================================================

    [Fact]
    public void ShopTable_LoadsBothTables_WithLiveCounts()
    {
        var t = Table();

        // ShopTabList.txt：★ 第十轮换新版 setting.u 后 52 → **58** 个店（没有 OnOff <= 0 的行）
        Assert.Equal(58, t.ShopCount);
        // GoodsList.txt：★ 第十轮 1896 → **2082** 件（GoodsId 为空的行仍被跳过，GoodsLogicC.lua:31）
        Assert.Equal(2082, t.GoodsCount);
        // GoodsId 全表唯一
        Assert.Equal(t.GoodsCount, t.AllGoods.Select(g => g.GoodsId).Distinct().Count());

        // ★ 无时间窗（= 永久开放）的店，其余全是带档期的 3000x 活动店。
        // ★★ 第十轮换新版 setting.u 后多了 **20011**（9 → 10 个）。
        var permanent = t.Shops.Where(s => !s.HasTimeWindow).Select(s => s.ShopId).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 20001, 20002, 20005, 20006, 20007, 20008, 20009, 20010, 20011, 40001 }, permanent);

        // 3000x 全部已过期（30001 的窗口是 2019-11-23 ~ 2019-12-06）
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expired = t.GetShop(ExpiredShopId)!;
        Assert.True(expired.HasTimeWindow);
        Assert.False(expired.IsOpenAt(now));
        // 永久店恒开（口径照 ShopTabLogicC:CheckShopOpen 的「没配时间窗 ⇒ return true」）
        Assert.True(t.GetShop(NormalShopId)!.IsOpenAt(now));
    }

    /// <summary>
    /// ★ <c>OnOff</c> 语义：1 = 普通商店（<c>shopIdList</c>）、2 = 咖啡馆商店（<c>cafeShopIdList</c>）、
    /// ≤0 整行跳过（<c>ShopTabLogicC.lua:23/:59..63</c>）。现网 <b>57 : 1</b>，没有被跳过的行
    /// （★ 第十轮换新版 setting.u 后普通店 51 → 57，咖啡馆店仍是唯一一家）。
    /// </summary>
    [Fact]
    public void ShopTable_OnOffSemantics_57NormalAnd1Cafe()
    {
        var t = Table();

        Assert.Equal(57, t.Shops.Count(s => s.IsNormalShop));
        Assert.Equal(1, t.Shops.Count(s => s.IsCafeShop));
        Assert.All(t.Shops, s => Assert.True(s.OnOff > 0, $"店 {s.ShopId} 的 OnOff={s.OnOff} 应当已被跳过"));

        var cafe = t.GetShop(CafeShopId)!;
        Assert.Equal(ShopTable.OnOffCafe, cafe.OnOff);
        Assert.True(cafe.IsCafeShop);
        Assert.Equal(ShopTable.CafeShopId, cafe.ShopId);
        // ★ MoneyType 空列的缺省是 3（ShopTabLogicC.lua:43 的 `or 3`），不是 0
        Assert.Equal(ShopTable.DefaultRefreshMoneyType, cafe.MoneyType);
        // 不分组 ⇒ Columns = 999「全部上架」
        Assert.Equal(ShopTable.ColumnsAll, cafe.Columns);
        Assert.False(t.IsGroupedShop(CafeShopId));
        Assert.Empty(t.GroupsOf(CafeShopId));
        Assert.Equal(499, t.GoodsOf(CafeShopId).Count);
        // 空 RefreshFee / RefreshLimits ⇒ 不支持手动刷新
        Assert.False(cafe.CanManualRefresh);
        Assert.Equal(0, cafe.RefreshLimits);
    }

    /// <summary>20001 的刷新配置（cmd 12 的全部输入都在这一行里）。</summary>
    [Fact]
    public void ShopTable_Shop20001_RefreshConfig()
    {
        var t = Table();
        var s = t.GetShop(NormalShopId)!;

        Assert.Equal(1, s.OnOff);
        Assert.Equal(8, s.Columns);                                     // = Group 数
        Assert.Equal(1, s.RefreshRule);                                 // 1 ⇔ RefreshLimitNum
        Assert.Equal(new[] { 15, 30, 60, 90, 120 }, s.RefreshFee);      // 原文 "15| 30| 60| 90| 120"（带空格）
        Assert.Equal(MoneyType.FreeDiamond, s.MoneyType);               // 3
        Assert.Equal(5, s.RefreshLimits);
        Assert.True(s.CanManualRefresh);
        // RefreshTime = "040000|180000" ⇒ 一天两次：04:00 与 18:00
        Assert.Equal(new[] { 4 * 3600, 18 * 3600 }, s.RefreshSecondsOfDay);
        Assert.Equal(new[] { 3, 12 }, s.ShopMoneyType);

        // 费用阶梯：第 1..5 次分别 15/30/60/90/120，超出后夹在最后一档
        Assert.Equal(15, s.RefreshFeeAt(0));
        Assert.Equal(30, s.RefreshFeeAt(1));
        Assert.Equal(60, s.RefreshFeeAt(2));
        Assert.Equal(90, s.RefreshFeeAt(3));
        Assert.Equal(120, s.RefreshFeeAt(4));
        Assert.Equal(120, s.RefreshFeeAt(99));

        // 8 个 Group，每组至少 1 件；[3]/[4] 恰好 1 件（测试的确定性锚点）
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, t.GroupsOf(NormalShopId));
        Assert.Equal(56, t.GoodsOf(NormalShopId).Count);
        Assert.Single(t.GoodsOf(NormalShopId), g => g.Group == AnchorGroup);
        Assert.Single(t.GoodsOf(NormalShopId), g => g.Group == 4);
        Assert.True(t.IsGroupedShop(NormalShopId));
        Assert.True(t.HasDiscountShop(NormalShopId));
    }

    /// <summary>锚点商品 20111 的每一列（价格数组、限量口径、折扣阶梯）。</summary>
    [Fact]
    public void ShopTable_Goods20111_AllColumns()
    {
        var g = Table().GetGoods(AnchorGoodsId)!;

        Assert.Equal(2, g.ShopType);                     // ShopId / 10000
        Assert.Equal(NormalShopId, g.ShopId);
        Assert.Equal(new[] { 7, 3, 1, 3 }, g.Gdpl);
        Assert.Equal(1, g.GoodsNum);                     // GDPLNX 第 5 位
        Assert.Equal(AnchorGroup, g.Group);
        Assert.Equal(10, g.Weight);
        Assert.Equal(MoneyType.FreeDiamond, g.MoneyType);
        Assert.Equal(new[] { AnchorPrice }, g.Prices);
        Assert.Null(g.MoneyType2);

        // ★ 限量列 ↔ RefreshRule 的 1:1（GoodsLogicC.lua:59..78）：20001 是 rule 1
        Assert.Equal(1, g.RefreshLimitNum);
        Assert.Null(g.DailyLimitNum);
        Assert.Null(g.WeekLimitNum);
        Assert.Equal(ShopLimitKind.Refresh, g.LimitKind);
        Assert.Equal(1, g.LimitNum);

        // 折扣阶梯 [1-10],[4-20],[10-30],[10-50],[20-70],[25-90],[30-100]，权重和恰为 100
        Assert.Equal(7, g.DiscountList.Count);
        Assert.Equal(new DiscountStep(1, 10), g.DiscountList[0]);
        Assert.Equal(new DiscountStep(30, 100), g.DiscountList[^1]);
        Assert.Equal(100, g.DiscountList.Sum(d => d.Weight));

        // 无 Begin/End ⇒ 永久在架
        Assert.Equal(0, g.BeginTime);
        Assert.Equal(0, g.EndTime);
        Assert.True(g.IsInTimeAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>
    /// ★ 现网 2082 件商品里 **2002 件没有 DiscountList**（只有 20001/20002 少量有），
    /// **1809 件没有 Group**（★ 第十轮换新版 setting.u 后的数字，旧快照是 1816 / 1643）。这两个数字是「为什么必须无条件下发 discount」与
    /// 「为什么不分组商店全部上架」的依据。
    /// </summary>
    [Fact]
    public void ShopTable_MostGoodsHaveNeitherGroupNorDiscount()
    {
        var t = Table();

        Assert.Equal(2002, t.AllGoods.Count(g => g.DiscountList.Count == 0));
        Assert.Equal(1809, t.AllGoods.Count(g => !g.Group.HasValue));

        // 有折扣表的商店只有这两家
        var discountShops = t.AllGoods.Where(g => g.DiscountList.Count > 0)
            .Select(g => g.ShopId).Distinct().OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 20001, 20002 }, discountShops);

        // 分组商店：Columns 与 Group 数的关系。
        // ★★★ 第十轮更正一条旧结论：换新版 setting.u 后**不再全部相等** ——
        // 20010 是 Columns=10 / 11 组、20011 是 Columns=12 / 9 组，其余 7 家仍相等。
        // ⇒ 「Columns 恰好等于 Group 数」只是**多数情况**，不是不变式。
        // 好消息是 RollShelf 的实现是「每组抽 1 件」、**压根不读 Columns**
        // ⇒ 这个不一致不影响任何行为。这里把断言降级成「记录事实 + 钉住不会退化成 0」。
        var groupedShops = t.AllGoods.Where(g => g.Group.HasValue)
                            .Select(g => g.ShopId).Distinct().ToList();
        Assert.True(groupedShops.Count >= 7, $"分组商店只剩 {groupedShops.Count} 家，疑似 Group 列没读到");
        foreach (int shopId in groupedShops)
        {
            var shop = t.GetShop(shopId);
            if (shop is null)
                continue;
            int groups = t.GroupsOf(shopId).Count;
            Assert.True(groups > 0, $"店 {shopId} 有 Group 列却算出 0 组");
            // Columns 不是 999（哨兵）时应与组数接近（相差 ≤ 3）—— 防「列名读错导致 Columns 恒 0」
            if (shop.Columns != ShopTable.ColumnsAll)
                Assert.InRange(shop.Columns, groups - 3, groups + 3);
        }
    }

    /// <summary>20008 的多档价 + 每周限量（价格档位测试的数据来源）。</summary>
    [Fact]
    public void ShopTable_Goods20804_HasFourPriceTiersAndWeekLimit()
    {
        var t = Table();
        var g = t.GetGoods(StairGoodsId)!;

        // 原文 "24|[500,500,1000,1000]" —— 价格段自己还套了一层方括号
        Assert.Equal(StairMoneyId, g.MoneyType);
        Assert.Equal(new[] { 500, 500, 1000, 1000 }, g.Prices);
        Assert.Equal(4, g.WeekLimitNum);
        Assert.Equal(ShopLimitKind.Week, g.LimitKind);
        Assert.Empty(g.DiscountList);                    // ⇒ 服务端必须自己填 discount=100

        // 20008 不分组、无折扣表 ⇒ 货架确定（全部上架），不必进存档
        Assert.False(t.IsGroupedShop(ExerciseShopId));
        Assert.False(t.HasDiscountShop(ExerciseShopId));
        Assert.Equal(7, t.GoodsOf(ExerciseShopId).Count);
    }

    // =====================================================================
    //  二、解析器（照 Lib.lua 的口径）
    // =====================================================================

    [Fact]
    public void ParsePrice_SplitsMoneyTypeThenPriceArray()
    {
        var (money1, prices1) = ShopTable.ParsePrice("3|20");
        Assert.Equal(3, money1);
        Assert.Equal(new[] { 20 }, prices1);

        // 价格段带方括号（现网确有）：Parse2ItemList 先整串去括号，所以不影响
        var (money2, prices2) = ShopTable.ParsePrice("5|[60,90,120,150,200,255]");
        Assert.Equal(5, money2);
        Assert.Equal(new[] { 60, 90, 120, 150, 200, 255 }, prices2);

        var (money3, prices3) = ShopTable.ParsePrice("24|[500,500,1000,1000]");
        Assert.Equal(24, money3);
        Assert.Equal(new[] { 500, 500, 1000, 1000 }, prices3);

        // 空列 ⇒ (0, [])（客户端在这里会 index nil 报错，服务端只跳过该行）
        var (money4, prices4) = ShopTable.ParsePrice("");
        Assert.Equal(0, money4);
        Assert.Empty(prices4);
    }

    [Fact]
    public void ParseDiscountList_UsesDashAsInnerSeparator()
    {
        var steps = ShopTable.ParseDiscountList("[1-10],[4-20],[10-30],[10-50],[20-70],[25-90],[30-100]");
        Assert.Equal(7, steps.Count);
        Assert.Equal(new DiscountStep(1, 10), steps[0]);
        Assert.Equal(new DiscountStep(25, 90), steps[5]);
        Assert.Equal(100, steps.Sum(s => s.Weight));

        // ★ 空列返回空表 —— 绝不模仿客户端那个坏掉的 '[100,100]' 缺省值：
        //   它被 Parse2ItemList（sep2 = "-"）解成 {{100},{100}} ⇒ discount == nil
        //   ⇒ GoodsLogicC:GetDisCount 走 `or 0` ⇒ 0 折 = 免费
        Assert.Empty(ShopTable.ParseDiscountList(""));
        Assert.Empty(ShopTable.ParseDiscountList("[100,100]"));
    }

    [Fact]
    public void ParseGroupAndNullableInt_DistinguishEmptyFromZero()
    {
        Assert.Equal(3, ShopTable.ParseGroup("[3]"));
        Assert.Equal(17, ShopTable.ParseGroup("[17]"));
        Assert.Null(ShopTable.ParseGroup(""));

        // ★ 「没配」与「配了 0」语义不同：客户端 IsInStock / _getNextPriceIdx 都是
        //   `if cfg.XLimitNum and rec.Xbuynum then`，判的是「在不在」
        Assert.Null(ShopTable.ParseNullableInt(""));
        Assert.Equal(0, ShopTable.ParseNullableInt("0"));
        Assert.Equal(30, ShopTable.ParseNullableInt("30"));
    }

    [Fact]
    public void ParseDates_FollowGetDate2TimePrecisionRules()
    {
        // GoodsList 的 [201912061200]（12 位 = yyyyMMddHHmm）
        Assert.Equal(new DateTimeOffset(2019, 12, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopTable.ParseBracketDate("[201912061200]"));
        // ShopTabList 的 2021-11-01-08-00-00（去掉 '-' 后 14 位）
        Assert.Equal(new DateTimeOffset(2021, 11, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopTable.ParseDashDate("2021-11-01-08-00-00"));
        // 8 位 = yyyyMMdd
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopTable.ParseDateDigits("20200102"));
        // 其它位数 / 非数字 ⇒ 0（客户端也是 return nil）
        Assert.Equal(0, ShopTable.ParseDateDigits("2020"));
        Assert.Equal(0, ShopTable.ParseBracketDate(""));
        Assert.Equal(0, ShopTable.ParseDashDate("nonsense"));
    }

    [Fact]
    public void ParseClockList_AcceptsHhmmssWithAndWithoutLeadingZero()
    {
        Assert.Equal(new[] { 4 * 3600, 18 * 3600 }, ShopTable.ParseClockList("040000|180000"));
        Assert.Equal(new[] { 4 * 3600 }, ShopTable.ParseClockList("40000"));
        Assert.Empty(ShopTable.ParseClockList(""));
    }

    /// <summary>
    /// ★ 折后单价必须**逐字**等于客户端的
    /// <c>math.ceil(SellingPrice[idx] * discount / 100)</c>（<c>UI_ShopNew.lua:402</c>），
    /// 总价再乘 buynum —— 「先 ceil 单价再乘数量」，不是「总价 ceil」。
    /// </summary>
    [Theory]
    [InlineData(20, 100, 20)]
    [InlineData(20, 10, 2)]
    [InlineData(85, 70, 60)]      // 59.5 → 60
    [InlineData(85, 90, 77)]      // 76.5 → 77
    [InlineData(1, 10, 1)]        // 0.1  → 1
    [InlineData(0, 100, 0)]
    public void GoodsRow_UnitPrice_MatchesClientCeilFormula(int price, int discount, int expected)
    {
        var g = new GoodsRow { Prices = new[] { price } };
        Assert.Equal(expected, g.UnitPrice(1, discount));
    }

    [Fact]
    public void GoodsRow_PriceAt_ClampsToArrayLength()
    {
        var g = new GoodsRow { Prices = new[] { 500, 500, 1000, 1000 } };
        Assert.Equal(500, g.PriceAt(1));
        Assert.Equal(1000, g.PriceAt(4));
        Assert.Equal(1000, g.PriceAt(99));      // math.min(idx, #SellingPrice)
        Assert.Equal(500, g.PriceAt(0));        // 下界防御
        Assert.Equal(0, new GoodsRow().PriceAt(1));
    }

    [Fact]
    public void ShopTable_MissingFiles_DegradeToEmptyWithoutThrowing()
    {
        var empty = ShopTable.Load(Path.Combine(Path.GetTempPath(), "sssj-no-such-dir-" + Guid.NewGuid()));
        Assert.Equal(0, empty.ShopCount);
        Assert.Equal(0, empty.GoodsCount);
        Assert.Null(empty.GetShop(NormalShopId));
        Assert.Null(empty.GetGoods(AnchorGoodsId));
        Assert.Empty(empty.GoodsOf(NormalShopId));
        Assert.Empty(empty.GroupsOf(NormalShopId));
        Assert.Equal(0, ShopTable.Empty.ShopCount);
    }

    // =====================================================================
    //  三、刷新周期（纯函数）
    // =====================================================================

    [Fact]
    public void DayBoundaryBefore_PicksMostRecentPassedClock()
    {
        // 2026-08-20 10:00 UTC
        long now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopFeature.DayBoundaryBefore(now, 4 * 3600));
        // 02:00 时「最近一次 04:00」是**前一天**的
        long early = new DateTimeOffset(2026, 8, 20, 2, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(new DateTimeOffset(2026, 8, 19, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopFeature.DayBoundaryBefore(early, 4 * 3600));
    }

    [Fact]
    public void WeekBoundaryBefore_LandsOnMonday()
    {
        // 2026-08-20 是星期四
        long now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        long week = ShopFeature.WeekBoundaryBefore(now, 4 * 3600);
        var dt = DateTimeOffset.FromUnixTimeSeconds(week).UtcDateTime;
        Assert.Equal(DayOfWeek.Monday, dt.DayOfWeek);
        Assert.Equal(new DateTime(2026, 8, 17, 4, 0, 0, DateTimeKind.Utc), dt);
    }

    [Fact]
    public void AutoRefresh_FollowsRefreshRule()
    {
        var t = Table();
        var rule1 = t.GetShop(NormalShopId)!;      // 一天两次：04:00 / 18:00
        var rule2 = t.GetShop(20010)!;             // 每日 04:00
        var rule3 = t.GetShop(20005)!;             // 每周（RefreshTime 空列 ⇒ 缺省 04:00）
        var rule4 = t.GetShop(CafeShopId)!;        // 活动期 ⇒ 不自动刷新

        long at10 = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        long at20 = new DateTimeOffset(2026, 8, 20, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        // rule 1：10:00 时下一档是当天 18:00；20:00 时是次日 04:00
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 18, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopFeature.NextAutoRefresh(rule1, at10));
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopFeature.NextAutoRefresh(rule1, at20));
        // 跨过 18:00 ⇒ 周期 key 改变（货架要重抽）
        Assert.NotEqual(ShopFeature.AutoPeriodKey(rule1, at10), ShopFeature.AutoPeriodKey(rule1, at20));

        // rule 2：一天一次
        Assert.Equal(ShopFeature.AutoPeriodKey(rule2, at10), ShopFeature.AutoPeriodKey(rule2, at20));
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 4, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
                     ShopFeature.NextAutoRefresh(rule2, at10));

        // rule 3：周界在周一
        long nextWeek = ShopFeature.NextAutoRefresh(rule3, at10);
        Assert.Equal(DayOfWeek.Monday, DateTimeOffset.FromUnixTimeSeconds(nextWeek).UtcDateTime.DayOfWeek);

        // rule 4：不自动刷新 ⇒ 回包不带 nextrefreshtime
        Assert.Equal(0, ShopFeature.NextAutoRefresh(rule4, at10));
        Assert.Equal(0, ShopFeature.AutoPeriodKey(rule4, at10));
        Assert.Equal(0, ShopFeature.NextAutoRefresh(null, at10));
    }

    // =====================================================================
    //  四、扣费（免费 + 付费合并语义）
    // =====================================================================

    /// <summary>
    /// ★ 金币 = <c>Gold(2) + Pay_Gold(17)</c>、钻石 = <c>Free_Diamond(3) + Pay_Diamond(12)
    /// + Pay_IOS_Diamond(18)</c>（<c>MoneyLogic.lua:302..312</c>），且**先扣免费后扣付费**。
    /// </summary>
    [Fact]
    public void TrySpendMoney_MergesFreeAndPaid_SpendingFreeFirst()
    {
        var p = new Player("spend", 1);

        p.SetMoney(MoneyType.Gold, 5);
        p.SetMoney(ShopFeature.MoneyPayGold, 100);
        Assert.Equal(105, ShopFeature.PoolValue(p, MoneyType.Gold));

        var changed = new List<int>();
        Assert.True(ShopFeature.TrySpendMoney(p, MoneyType.Gold, 30, changed));
        Assert.Equal(0, p.GetMoney(MoneyType.Gold));            // 免费的先花光
        Assert.Equal(75, p.GetMoney(ShopFeature.MoneyPayGold));
        Assert.Equal(new[] { MoneyType.Gold, ShopFeature.MoneyPayGold }, changed);

        // 钻石三档：免费 → 安卓付费 → iOS 付费
        p.SetMoney(MoneyType.FreeDiamond, 10);
        p.SetMoney(MoneyType.PayDiamond, 5);
        p.SetMoney(ShopFeature.MoneyPayIosDiamond, 100);
        Assert.Equal(115, ShopFeature.PoolValue(p, MoneyType.FreeDiamond));

        changed.Clear();
        Assert.True(ShopFeature.TrySpendMoney(p, MoneyType.FreeDiamond, 20, changed));
        Assert.Equal(0, p.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(0, p.GetMoney(MoneyType.PayDiamond));
        Assert.Equal(95, p.GetMoney(ShopFeature.MoneyPayIosDiamond));
    }

    [Fact]
    public void TrySpendMoney_Insufficient_ChangesNothing()
    {
        var p = new Player("broke", 1);
        p.SetMoney(MoneyType.FreeDiamond, 1);
        p.SetMoney(MoneyType.PayDiamond, 1);

        var changed = new List<int>();
        Assert.False(ShopFeature.TrySpendMoney(p, MoneyType.FreeDiamond, 5, changed));
        Assert.Equal(1, p.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(1, p.GetMoney(MoneyType.PayDiamond));
        Assert.Empty(changed);

        // 非金币/钻石的货币按单一 id 处理
        p.SetMoney(StairMoneyId, 3);
        Assert.Equal(3, ShopFeature.PoolValue(p, StairMoneyId));
        Assert.False(ShopFeature.TrySpendMoney(p, StairMoneyId, 4, changed));
        Assert.Equal(3, p.GetMoney(StairMoneyId));
    }

    // =====================================================================
    //  五、cmd 11：★ 四字段门禁
    // =====================================================================

    /// <summary>
    /// ★★ 本域最重要的一条断言：<c>shopid</c>/<c>isopen</c>/<c>refreshcount</c>/<c>goodslist</c>
    /// 四个字段**必须齐全且类型正确**。<c>GoodsLogicC.lua:273..279</c> 判的是
    /// <c>type(...) ~= "number"</c> / <c>~= "table"</c>，缺一个或类型不对就**静默 return**：
    /// 界面完全不刷新、客户端不报错、服务端日志一切正常。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_ReplyHasAllFourGateFieldsWithRightTypes()
    {
        using var h = await FeatureHarness.CreateAsync("shop-gate");

        var reply = await ReqListAsync(h, NormalShopId);

        Assert.NotNull(reply["shopid"]);
        Assert.NotNull(reply["isopen"]);
        Assert.NotNull(reply["refreshcount"]);
        Assert.NotNull(reply["goodslist"]);

        // type(x) == "number"
        Assert.Equal(JsonValueKind.Number, reply["shopid"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, reply["isopen"]!.GetValueKind());
        Assert.Equal(JsonValueKind.Number, reply["refreshcount"]!.GetValueKind());
        // type(x) == "table" 且必须是**数组**（客户端用 ipairs 遍历）
        Assert.Equal(JsonValueKind.Array, reply["goodslist"]!.GetValueKind());

        Assert.Equal(NormalShopId, reply["shopid"]!.GetValue<int>());
        Assert.Equal(1, reply["isopen"]!.GetValue<int>());
        Assert.Equal(0, reply["refreshcount"]!.GetValue<int>());
        // ★ LuaCall 族：成功回包不能带 nError（Lua 真值判断，0 也是真）
        Assert.Null(reply["nError"]);
    }

    /// <summary>
    /// ★ 每个货架项：<c>discount</c> 必给（否则回落成一折/免费）、五个 <c>*buynum</c> 必给
    /// （详情弹窗做裸减法），而 <c>buynum</c> 键**不能有**（会改 <c>_getNextPriceIdx</c> 的档位）。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_EveryEntryHasDiscountAndFiveBuyNums_ButNoBuyNumKey()
    {
        using var h = await FeatureHarness.CreateAsync("shop-entry-fields");

        var reply = await ReqListAsync(h, NormalShopId);
        var list = GoodsListOf(reply);
        Assert.NotEmpty(list);

        foreach (var node in list)
        {
            var e = Assert.IsType<JsonObject>(node);
            Assert.Equal(JsonValueKind.Number, e["goodsid"]!.GetValueKind());

            // ★ discount：0..100，必须是数字
            Assert.NotNull(e["discount"]);
            Assert.Equal(JsonValueKind.Number, e["discount"]!.GetValueKind());
            int discount = e["discount"]!.GetValue<int>();
            Assert.InRange(discount, 0, 100);

            // ★ 五个计数一律给（UI_ShopTips.lua:238/242/246/250/254 全是裸减法）
            foreach (string key in new[]
                     { "refreshbuynum", "dailybuynum", "weekbuynum", "totalbuynum", "shelfbuynum" })
            {
                Assert.NotNull(e[key]);
                Assert.Equal(JsonValueKind.Number, e[key]!.GetValueKind());
            }

            // ★ 绝不能出现 buynum —— GoodsLogicC.lua:169 的最后一档会拿它当档位
            Assert.Null(e["buynum"]);
        }
    }

    /// <summary>
    /// 20001 配了 <c>RefreshLimitNum</c> ⇒ <c>refreshbuynum</c> 是「条件必填」字段里真正被消费的那个，
    /// 且它的值必须随购买真的动（客户端拿 <c>RefreshLimitNum - refreshbuynum</c> 当「还能买几个」）。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_RefreshBuyNumReflectsRealPurchases()
    {
        using var h = await FeatureHarness.CreateAsync("shop-refreshbuynum");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);

        var before = await ReqListAsync(h, NormalShopId);
        Assert.Equal(0, EntryOf(before, AnchorGoodsId)["refreshbuynum"]!.GetValue<int>());

        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);

        var after = await ReqListAsync(h, NormalShopId);
        var entry = EntryOf(after, AnchorGoodsId);
        Assert.Equal(1, entry["refreshbuynum"]!.GetValue<int>());
        // totalbuynum 是永久计数，也跟着 +1（ShopData:HaveNum 拿它显示咖啡馆「已拥有」）
        Assert.Equal(1, entry["totalbuynum"]!.GetValue<int>());
        // 与本商品无关的口径保持 0
        Assert.Equal(0, entry["weekbuynum"]!.GetValue<int>());
    }

    /// <summary>分组商店：每个 Group 恰好上架 1 件，<c>pos</c> = Group 号。</summary>
    [Fact]
    public async Task ReqGoodsList_GroupedShop_YieldsExactlyOneGoodsPerGroup()
    {
        using var h = await FeatureHarness.CreateAsync("shop-group");
        var table = Table();

        var reply = await ReqListAsync(h, NormalShopId);
        var list = GoodsListOf(reply);

        // 8 个 Group ⇒ 8 件（= ShopTabList.Columns）
        Assert.Equal(table.GroupsOf(NormalShopId).Count, list.Count);
        Assert.Equal(table.GetShop(NormalShopId)!.Columns, list.Count);

        var positions = new List<int>();
        foreach (var node in list)
        {
            var e = Assert.IsType<JsonObject>(node);
            // pos 必给（社团商店 20006 会读它；给了也不影响别的店）
            Assert.NotNull(e["pos"]);
            int pos = e["pos"]!.GetValue<int>();
            positions.Add(pos);

            // 抽中的商品必须真的属于这个店、这个组
            var goods = table.GetGoods(e["goodsid"]!.GetValue<int>());
            Assert.NotNull(goods);
            Assert.Equal(NormalShopId, goods!.ShopId);
            Assert.Equal(pos, goods.Group);
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, positions.OrderBy(x => x).ToArray());

        // 组 [3]/[4] 只有一件候选 ⇒ 必然出现（确定性锚点）
        Assert.Contains(AnchorGoodsId, list.Select(n => n!["goodsid"]!.GetValue<int>()));
        Assert.Contains(Anchor2GoodsId, list.Select(n => n!["goodsid"]!.GetValue<int>()));
    }

    /// <summary>
    /// 货架必须持久化：重复请求 cmd 11 得到**同一批商品、同一个折扣**。
    /// 不持久化的表现是「每次开商店价格都在变」。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_ShelfIsStableAcrossRequests()
    {
        using var h = await FeatureHarness.CreateAsync("shop-stable");

        var first = await ReqListAsync(h, NormalShopId);
        var firstPairs = GoodsListOf(first)
            .Select(n => (n!["goodsid"]!.GetValue<int>(), n["discount"]!.GetValue<int>()))
            .OrderBy(x => x.Item1).ToArray();

        for (int i = 0; i < 3; i++)
        {
            var again = await ReqListAsync(h, NormalShopId);
            var pairs = GoodsListOf(again)
                .Select(n => (n!["goodsid"]!.GetValue<int>(), n["discount"]!.GetValue<int>()))
                .OrderBy(x => x.Item1).ToArray();
            Assert.Equal(firstPairs, pairs);
        }
    }

    /// <summary>
    /// ★ 永久商店**不能**下发 <c>begintime</c>/<c>endtime</c>：
    /// <c>UI_ShopEntry.lua:105</c> 的 <c>GetTime() - tbParam.begintime</c> 没有 nil 保护，
    /// 而 <c>:99</c> 只要 <c>endtime ~= 0</c> 就会进那段（且 <c>:96</c> 的 <c>and</c> 写错，
    /// 对任何 shopid 都会执行）。给一个不给另一个 = 商店大厅必炸。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_PermanentShop_OmitsBothBeginAndEndTime()
    {
        using var h = await FeatureHarness.CreateAsync("shop-notime");

        var reply = await ReqListAsync(h, NormalShopId);

        Assert.Null(reply["begintime"]);
        Assert.Null(reply["endtime"]);
        // rule 1 有自动刷新 ⇒ 会带 nextrefreshtime（可选字段，nil 安全）
        Assert.NotNull(reply["nextrefreshtime"]);
        Assert.True(reply["nextrefreshtime"]!.GetValue<long>() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>
    /// 已过期的活动商店：<c>isopen=0</c> + 空货架，且 <c>begintime</c>/<c>endtime</c>
    /// **成对**下发（配了时间窗才给，给就一起给）。
    /// 这条同时覆盖「商店大厅 <c>UI_ShopEntry</c> 请求的就是 3000x」这条真实路径。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_ExpiredShop_IsOpenZeroEmptyListAndPairedTimes()
    {
        using var h = await FeatureHarness.CreateAsync("shop-expired");

        var reply = await ReqListAsync(h, ExpiredShopId);

        Assert.Equal(ExpiredShopId, reply["shopid"]!.GetValue<int>());
        Assert.Equal(0, reply["isopen"]!.GetValue<int>());
        // ★ 空数组与「省略」语义不同：[] 是空货架（界面空但不报错），省略会触发静默 return
        Assert.Empty(GoodsListOf(reply));
        Assert.Equal(JsonValueKind.Array, reply["goodslist"]!.GetValueKind());

        Assert.NotNull(reply["begintime"]);
        Assert.NotNull(reply["endtime"]);
        Assert.True(reply["endtime"]!.GetValue<long>() > reply["begintime"]!.GetValue<long>());
        // rule 4（活动期）不自动刷新
        Assert.Null(reply["nextrefreshtime"]);
    }

    /// <summary>咖啡馆家具店：499 件全上架、折扣一律 100（**这就是「必须自己填 discount」的现场**）。</summary>
    [Fact]
    public async Task ReqGoodsList_CafeShop_ListsAllGoodsAtFullPrice()
    {
        using var h = await FeatureHarness.CreateAsync("shop-cafe-list");

        var reply = await ReqListAsync(h, CafeShopId);
        var list = GoodsListOf(reply);

        Assert.Equal(499, list.Count);
        Assert.All(list, n => Assert.Equal(100, n!["discount"]!.GetValue<int>()));
        // 咖啡馆靠 totalbuynum 显示「已拥有」（ShopData.lua:103），必须有
        Assert.All(list, n => Assert.NotNull(n!["totalbuynum"]));
    }

    /// <summary>
    /// shopid 两张表里都没有 ⇒ 回 <c>errorKey</c>（cmd 11 **唯一**有这个分支，
    /// <c>LuaCall.lua:677</c>：<c>ShowMsgByKey("ui." .. tbParam.errorKey)</c>，
    /// 前缀由客户端自己拼，所以值里不带 <c>ui.</c>）。
    /// </summary>
    [Fact]
    public async Task ReqGoodsList_UnknownShop_RepliesErrorKey()
    {
        using var h = await FeatureHarness.CreateAsync("shop-unknown");

        var reply = await ReqListAsync(h, 987654);

        Assert.Equal(ShopFeature.ErrorKeyShopUnmatch, reply["errorKey"]!.GetValue<string>());
        Assert.DoesNotContain("ui.", reply["errorKey"]!.GetValue<string>());
        // 走 errorKey 分支时客户端不会再进 OnReqGoodsList，所以不需要门禁字段
        Assert.Null(reply["goodslist"]);
    }

    /// <summary>未登录的会话不能崩，也不能回包。</summary>
    [Fact]
    public async Task ReqGoodsList_WithoutPlayer_IsIgnoredWithoutThrowing()
    {
        using var h = await FeatureHarness.CreateAsync("shop-nologin", attachPlayer: false);

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqGoodsList, $"{{\"shopid\":{NormalShopId}}}");
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyGoods,
            $"{{\"shopid\":{NormalShopId},\"goodsid\":{AnchorGoodsId},\"buynum\":1}}");

        Assert.Empty(h.Outbound);
    }

    // =====================================================================
    //  六、cmd 12：刷新
    // =====================================================================

    [Fact]
    public async Task RefreshGoodsList_ChargesFeeBumpsCountAndReplaysGateFields()
    {
        using var h = await FeatureHarness.CreateAsync("shop-refresh");
        h.Player.SetMoney(MoneyType.FreeDiamond, 1_000);

        await ReqListAsync(h, NormalShopId);                        // 先建一期货架
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, $"{{\"shopid\":{NormalShopId}}}");

        var reply = LastLuaCallObject(h, ShopFeature.CmdReqRefreshGoodsList);
        // cmd 12 的门禁与 cmd 11 完全相同（GoodsLogicC.lua:330..336）
        Assert.Equal(NormalShopId, reply["shopid"]!.GetValue<int>());
        Assert.Equal(1, reply["isopen"]!.GetValue<int>());
        Assert.Equal(1, reply["refreshcount"]!.GetValue<int>());
        Assert.Equal(8, GoodsListOf(reply).Count);
        Assert.Null(reply["nError"]);

        // 第一次刷新扣 RefreshFee[1] = 15 钻
        Assert.Equal(1_000 - 15, h.Player.GetMoney(MoneyType.FreeDiamond));

        // 之后的 cmd 11 也要报同一个 refreshcount
        var list = await ReqListAsync(h, NormalShopId);
        Assert.Equal(1, list["refreshcount"]!.GetValue<int>());
    }

    /// <summary>
    /// 费用阶梯 15/30/60/90/120，共 315；第 6 次被拒（<c>RefreshLimits = 5</c>），
    /// 且**一分钱不扣**、<c>refreshcount</c> 不动。
    /// </summary>
    [Fact]
    public async Task RefreshGoodsList_FeeEscalatesThenHitsLimit()
    {
        using var h = await FeatureHarness.CreateAsync("shop-refresh-limit");
        h.Player.SetMoney(MoneyType.FreeDiamond, 1_000);

        int[] expected = { 15, 30, 60, 90, 120 };
        int spent = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, $"{{\"shopid\":{NormalShopId}}}");
            spent += expected[i];
            var r = LastLuaCallObject(h, ShopFeature.CmdReqRefreshGoodsList);
            Assert.Equal(i + 1, r["refreshcount"]!.GetValue<int>());
            Assert.Equal(1_000 - spent, h.Player.GetMoney(MoneyType.FreeDiamond));
        }
        Assert.Equal(315, spent);

        // 第 6 次：走提示通道，不是 cmd 12 回包
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, $"{{\"shopid\":{NormalShopId}}}");
        var (function, json) = h.LastOutbound;
        Assert.Equal(ShopFeature.FnCallClientTip, function);
        Assert.Contains(ShopFeature.TipRefreshOverLimit, json);
        Assert.Equal(1_000 - 315, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(5, ShopFeature.GetRefreshCount(h.Player, NormalShopId));
    }

    [Fact]
    public async Task RefreshGoodsList_NotEnoughMoney_ChangesNothing()
    {
        using var h = await FeatureHarness.CreateAsync("shop-refresh-broke");
        h.Player.SetMoney(MoneyType.FreeDiamond, 5);          // 需要 15

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, $"{{\"shopid\":{NormalShopId}}}");

        var (function, json) = h.LastOutbound;
        Assert.Equal(ShopFeature.FnCallClientTip, function);
        Assert.Contains(ShopFeature.TipNotEnoughMoney, json);
        Assert.Equal(5, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.Equal(0, ShopFeature.GetRefreshCount(h.Player, NormalShopId));
    }

    /// <summary>
    /// 没配 <c>RefreshFee</c>/<c>RefreshLimits</c> 的商店（现网除 20001/20002 外全部）
    /// 一律拒绝刷新 —— 客户端自己也会先拦（<c>RefreshLimits</c> 空列 ⇒ 0 ⇒ <c>0 &gt;= 0</c>）。
    /// </summary>
    [Fact]
    public async Task RefreshGoodsList_ShopWithoutRefreshConfig_IsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("shop-refresh-none");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, $"{{\"shopid\":{ShopTable.GirlCardShopId}}}");

        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.TipRefreshOverLimit, h.LastOutbound.Json);
        Assert.Equal(10_000, h.Player.GetMoney(MoneyType.FreeDiamond));
    }

    [Fact]
    public async Task RefreshGoodsList_UnknownShop_IsRejectedWithoutThrowing()
    {
        using var h = await FeatureHarness.CreateAsync("shop-refresh-unknown");

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, "{\"shopid\":987654}");

        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.TipShopUnmatch, h.LastOutbound.Json);
    }

    // =====================================================================
    //  七、cmd 13：购买
    // =====================================================================

    /// <summary>
    /// ★ 回包必须回显请求三要素：<c>goodsid</c>（<c>GoodsLogicC.lua:393</c> 拿它查配置，
    /// 查不到就静默跳过奖励弹窗）、<c>buynum</c>（<c>:396</c> 做
    /// <c>(GoodsNum or 1) * buynum</c>，**无 nil 保护**）、<c>shopid</c>（<c>:385/:400/:419</c>）。
    /// 且成功回包**不能**带 <c>nError</c>。
    /// </summary>
    [Fact]
    public async Task BuyGoods_ReplyEchoesRequestTriple_AndHasNoNError()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-echo");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);
        await ReqListAsync(h, NormalShopId);

        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);

        var reply = LastLuaCallObject(h, ShopFeature.CmdReqBuyGoods);
        Assert.Equal(NormalShopId, reply["shopid"]!.GetValue<int>());
        Assert.Equal(AnchorGoodsId, reply["goodsid"]!.GetValue<int>());
        Assert.Equal(1, reply["buynum"]!.GetValue<int>());
        Assert.Equal(JsonValueKind.Number, reply["buynum"]!.GetValueKind());
        Assert.Null(reply["nError"]);
    }

    /// <summary>
    /// 真实扣费 + 真实发货：扣的钱必须**恰好等于**客户端显示的价
    /// （<c>ceil(Price1[idx] * discount / 100) × buynum</c>，discount 从 cmd 11 的回包里读），
    /// 物品按 <c>GoodsNum × buynum</c> 入包。
    /// </summary>
    [Fact]
    public async Task BuyGoods_DeductsExactlyWhatClientShows_AndDelivers()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-price");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);

        var list = await ReqListAsync(h, NormalShopId);
        int discount = EntryOf(list, AnchorGoodsId)["discount"]!.GetValue<int>();
        int unit = (AnchorPrice * discount + 99) / 100;        // = ceil(20 * d / 100)
        Assert.InRange(unit, 1, AnchorPrice);

        int before = h.Player.GetMoney(MoneyType.FreeDiamond);
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);

        Assert.Equal(before - unit, h.Player.GetMoney(MoneyType.FreeDiamond));

        // 发货：(7,3,1,3) × GoodsNum(1) × buynum(1)
        var stack = h.Player.Items.SingleOrDefault(i => i.Genre == AnchorG && i.Detail == AnchorD
                                                    && i.Particular == AnchorP && i.Level == AnchorL);
        Assert.NotNull(stack);
        Assert.Equal(1u, stack!.Count);

        // ★ 必须把变更**推**给客户端，否则数据对了但界面不动：
        //   pf 1032 扣币、pf 1031 发货（改背包只能用 1031，1104 不派发 Lua 事件 —— AGENTS.md §5）。
        //   用 WaitForPfAsync 而不是裸读 PushedPf：Push 是异步写 socket，会 flaky。
        Assert.True(await h.WaitForPfAsync((int)GamePf.MoneyUpdateNtf),
            $"没等到 pf 1032（扣币），实际推了：[{string.Join(",", h.PushedPf)}]");
        Assert.True(await h.WaitForPfAsync((int)GamePf.ItemUpdateNtf),
            $"没等到 pf 1031（发货），实际推了：[{string.Join(",", h.PushedPf)}]");
    }

    /// <summary>
    /// ★ 扣费走「免费 + 付费」合并池：免费钻不够时自动用付费钻补足，且**先扣免费**。
    /// 客户端的「买得起吗」判定用的就是合并余额（<c>UI_ShopTips.lua:270</c> →
    /// <c>MoneyLogic:GetMoneyValue</c>），服务端不合并会出现「界面能买、服务端说没钱」。
    /// </summary>
    [Fact]
    public async Task BuyGoods_UsesMergedMoneyPool_FreeBeforePaid()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-merged");
        h.Player.SetMoney(MoneyType.FreeDiamond, 1);          // 免费钻只有 1
        h.Player.SetMoney(MoneyType.PayDiamond, 1_000);

        var list = await ReqListAsync(h, NormalShopId);
        int discount = EntryOf(list, Anchor2GoodsId)["discount"]!.GetValue<int>();
        int unit = (80 * discount + 99) / 100;                  // 20112 的 Price1 = 3|80
        Assert.True(unit > 1, "折后价应当大于 1，才能验到「免费钻不够、付费钻补足」");

        // 20112 = (7,3,1,4)，先记下已有数量（新号的初始装备里可能有同四元组）
        uint before = h.Player.Items
            .Where(i => i.Genre == 7 && i.Detail == 3 && i.Particular == 1 && i.Level == 4)
            .Aggregate(0u, (acc, i) => acc + i.Count);

        await BuyAsync(h, NormalShopId, Anchor2GoodsId, 1);

        Assert.Equal(0, h.Player.GetMoney(MoneyType.FreeDiamond));               // 免费的先花光
        Assert.Equal(1_000 - (unit - 1), h.Player.GetMoney(MoneyType.PayDiamond));
        uint after = h.Player.Items
            .Where(i => i.Genre == 7 && i.Detail == 3 && i.Particular == 1 && i.Level == 4)
            .Aggregate(0u, (acc, i) => acc + i.Count);
        Assert.Equal(before + 1, after);
    }

    /// <summary>买不起：回提示、**一分不扣、一件不发**、不抛异常。</summary>
    [Fact]
    public async Task BuyGoods_NotEnoughMoney_FailsCleanly()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-broke");
        // 新号默认带 ServerOptions.StartDiamond = 3000 钻，先清空三档钻石
        h.Player.SetMoney(MoneyType.FreeDiamond, 0);
        h.Player.SetMoney(MoneyType.PayDiamond, 0);
        h.Player.SetMoney(ShopFeature.MoneyPayIosDiamond, 0);

        await ReqListAsync(h, NormalShopId);
        Assert.Equal(0, ShopFeature.PoolValue(h.Player, MoneyType.FreeDiamond));
        int itemsBefore = h.Player.Items.Count;

        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);

        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.TipNotEnoughMoney, h.LastOutbound.Json);
        Assert.Equal(0, ShopFeature.PoolValue(h.Player, MoneyType.FreeDiamond));
        Assert.Equal(itemsBefore, h.Player.Items.Count);
        // 限量计数也不能动
        Assert.Equal(0, ShopFeature.GetBuyCount(h.Player, NormalShopId, ShopLimitKind.Refresh, AnchorGoodsId));
    }

    /// <summary>goodsid 不存在 / shopid 不存在 / buynum 非法：全部走提示，不抛异常。</summary>
    [Fact]
    public async Task BuyGoods_BadRequests_AllFailWithoutThrowing()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-bad");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);

        // ① goodsid 不存在
        await BuyAsync(h, NormalShopId, 999999, 1);
        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.TipBadGoodsId, h.LastOutbound.Json);

        // ② shopid 不存在（商品存在但不属于它）
        await BuyAsync(h, 987654, AnchorGoodsId, 1);
        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.TipShopUnmatch, h.LastOutbound.Json);

        // ③ buynum 非法
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 0);
        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1_000_000);
        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);

        // ④ 商店已过期
        await BuyAsync(h, ExpiredShopId, 30101, 1);
        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);

        // ⑤ 空 tbParam（客户端 GoodsLogicC.lua:381 的 type 检查写成了 and，等于没检查）
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyGoods, "{}");
        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);

        // 全程一分钱没扣、一件货没发
        Assert.Equal(10_000, h.Player.GetMoney(MoneyType.FreeDiamond));
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == AnchorG && i.Detail == AnchorD);
    }

    /// <summary>
    /// 不在本期货架上的商品不能买（否则分组商店等于「全表随便买」）。
    /// 20001 的组 [1] 有 5 件候选，只有 1 件会上架，拿一件没上架的来试。
    /// </summary>
    [Fact]
    public async Task BuyGoods_GoodsNotOnCurrentShelf_IsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-offshelf");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);

        var list = await ReqListAsync(h, NormalShopId);
        var onShelf = GoodsListOf(list).Select(n => n!["goodsid"]!.GetValue<int>()).ToHashSet();
        int offShelf = Table().GoodsOf(NormalShopId).First(g => !onShelf.Contains(g.GoodsId)).GoodsId;

        await BuyAsync(h, NormalShopId, offShelf, 1);

        Assert.Equal(ShopFeature.FnCallClientTip, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.TipBadGoodsId, h.LastOutbound.Json);
        Assert.Equal(10_000, h.Player.GetMoney(MoneyType.FreeDiamond));
    }

    /// <summary>
    /// 限量：20001 的 <c>RefreshLimitNum = 1</c> ⇒ 一期只能买 1 个。
    /// 超限走 <c>S2C.CallClientError{err=20020}</c>（客户端自己也用 20020 表示「次数用尽」，
    /// <c>UI_Exchange.lua:187</c>）。
    /// </summary>
    [Fact]
    public async Task BuyGoods_RespectsPerRefreshLimit()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-limit");
        h.Player.SetMoney(MoneyType.FreeDiamond, 10_000);
        await ReqListAsync(h, NormalShopId);

        // 一次买 2 个就超了限量 1 ⇒ 直接拒
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 2);
        Assert.Equal(ShopFeature.FnCallClientError, h.LastOutbound.Function);
        Assert.Contains(ShopFeature.ErrNoBuyTimes.ToString(), h.LastOutbound.Json);
        Assert.Equal(10_000, h.Player.GetMoney(MoneyType.FreeDiamond));

        // 买 1 个可以
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);
        LastLuaCallObject(h, ShopFeature.CmdReqBuyGoods);

        // 再买第 2 个 ⇒ 超限
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);
        Assert.Equal(ShopFeature.FnCallClientError, h.LastOutbound.Function);
        Assert.Equal(1u, h.Player.Items.Single(i => i.Genre == AnchorG && i.Detail == AnchorD
                                                 && i.Particular == AnchorP && i.Level == AnchorL).Count);

        // 手动刷新会把「单次刷新限量」清零 ⇒ 又能买了（若刷新后该组换了商品则跳过本段）
        await h.DispatchLuaCallAsync(ShopFeature.CmdReqRefreshGoodsList, $"{{\"shopid\":{NormalShopId}}}");
        Assert.Equal(0, ShopFeature.GetBuyCount(h.Player, NormalShopId, ShopLimitKind.Refresh, AnchorGoodsId));
        // 组 [3] 只有 20111 一件候选，刷新后必然还在架上
        await BuyAsync(h, NormalShopId, AnchorGoodsId, 1);
        LastLuaCallObject(h, ShopFeature.CmdReqBuyGoods);
        Assert.Equal(2u, h.Player.Items.Single(i => i.Genre == AnchorG && i.Detail == AnchorD
                                                 && i.Particular == AnchorP && i.Level == AnchorL).Count);
    }

    /// <summary>
    /// ★ 取价档位随已购数量前进（<c>_getNextPriceIdx</c> 的移植）：
    /// 20804 的 <c>Price1 = 24|[500,500,1000,1000]</c> + <c>WeekLimitNum = 4</c>
    /// ⇒ 第 1/2 次各 500、第 3/4 次各 1000，第 5 次超限。
    /// 20008 没有折扣表 ⇒ discount 恒 100，价格可精确预期。
    /// </summary>
    [Fact]
    public async Task BuyGoods_PriceTierAdvancesWithBuyCount()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-stairs");
        h.Player.SetMoney(StairMoneyId, 10_000);

        var list = await ReqListAsync(h, ExerciseShopId);
        Assert.Equal(100, EntryOf(list, StairGoodsId)["discount"]!.GetValue<int>());

        int[] tiers = { 500, 500, 1000, 1000 };
        int spent = 0;
        for (int i = 0; i < tiers.Length; i++)
        {
            await BuyAsync(h, ExerciseShopId, StairGoodsId, 1);
            LastLuaCallObject(h, ShopFeature.CmdReqBuyGoods);   // 成功
            spent += tiers[i];
            Assert.Equal(10_000 - spent, h.Player.GetMoney(StairMoneyId));
            Assert.Equal(i + 1, ShopFeature.GetBuyCount(h.Player, ExerciseShopId,
                                                        ShopLimitKind.Week, StairGoodsId));
        }
        Assert.Equal(3_000, spent);

        // 第 5 次：WeekLimitNum = 4 已满
        await BuyAsync(h, ExerciseShopId, StairGoodsId, 1);
        Assert.Equal(ShopFeature.FnCallClientError, h.LastOutbound.Function);
        Assert.Equal(10_000 - 3_000, h.Player.GetMoney(StairMoneyId));

        // 回包里的 weekbuynum 也要跟着走（客户端拿 WeekLimitNum - weekbuynum 显示「本周可购买」）
        var after = await ReqListAsync(h, ExerciseShopId);
        Assert.Equal(4, EntryOf(after, StairGoodsId)["weekbuynum"]!.GetValue<int>());
    }

    /// <summary>咖啡馆家具：无限量、单价固定，买 3 个就是 3 倍价。</summary>
    [Fact]
    public async Task BuyGoods_CafeFurniture_MultipliesByBuyNum()
    {
        using var h = await FeatureHarness.CreateAsync("shop-buy-cafe");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 10_000);

        await ReqListAsync(h, CafeShopId);
        await BuyAsync(h, CafeShopId, CafeGoodsA, 3);

        var reply = LastLuaCallObject(h, ShopFeature.CmdReqBuyGoods);
        Assert.Equal(3, reply["buynum"]!.GetValue<int>());
        Assert.Equal(10_000 - CafePriceA * 3, h.Player.GetMoney(MoneyType.CoffeeCoin));
        var furniture = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir)).Furniture;
        Assert.Equal(3, furniture.Count(f => f.TemplateId == 101 && f.RoomId == 0));
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 11 && i.Detail == 1 && i.Particular == 1);
        Assert.True(await h.WaitForPfAsync(SssjServer.Protocol.Pf.AddFornituresRsp));
    }

    // =====================================================================
    //  八、cmd 14：家具批量购买
    // =====================================================================

    /// <summary>
    /// 上行是数组，回包也是数组，每项靠 <c>ret</c> 的 **Lua 真值**判成败
    /// （<c>UI_NCafeShop.lua:312</c> <c>if v.ret then</c>）。
    /// </summary>
    [Fact]
    public async Task BuyFurList_BuysEveryEntryAndFlagsRet()
    {
        using var h = await FeatureHarness.CreateAsync("shop-fur-ok");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 10_000);

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyFurList,
            $"[{{\"shopid\":{CafeShopId},\"goodsid\":{CafeGoodsA},\"buynum\":1}}," +
            $"{{\"shopid\":{CafeShopId},\"goodsid\":{CafeGoodsB},\"buynum\":1}}]");

        var reply = Assert.IsType<JsonArray>(LastLuaCall(h, ShopFeature.CmdReqBuyFurList));
        Assert.Equal(2, reply.Count);
        Assert.All(reply, n =>
        {
            var o = Assert.IsType<JsonObject>(n);
            Assert.NotNull(o["goodsid"]);
            Assert.True(o["ret"]!.GetValue<bool>());
        });

        Assert.Equal(10_000 - CafePriceA - CafePriceB, h.Player.GetMoney(MoneyType.CoffeeCoin));
        var furniture = HouseFeature.ReadOrCreate(h.Player, HouseTable.Load(TestPaths.SettingDir)).Furniture;
        Assert.Single(furniture, f => f.TemplateId == 101 && f.RoomId == 0);
        Assert.Single(furniture, f => f.TemplateId == 102 && f.RoomId == 0);
        Assert.DoesNotContain(h.Player.Items, i => i.Genre == 11);
        Assert.True(await h.WaitForPfAsync(SssjServer.Protocol.Pf.AddFornituresRsp));
    }

    /// <summary>
    /// 部分失败：失败项 <c>ret = false</c>（Lua 里是假值，客户端 <c>:312</c> 会跳过它），
    /// 成功项照常发货 —— 整批不因为一件失败而回滚。
    /// </summary>
    [Fact]
    public async Task BuyFurList_PartialFailure_MarksRetFalseOnlyForThatEntry()
    {
        using var h = await FeatureHarness.CreateAsync("shop-fur-partial");
        h.Player.SetMoney(MoneyType.CoffeeCoin, 10_000);

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyFurList,
            $"[{{\"shopid\":{CafeShopId},\"goodsid\":{CafeGoodsA},\"buynum\":1}}," +
            $"{{\"shopid\":{CafeShopId},\"goodsid\":999999,\"buynum\":1}}]");

        var reply = Assert.IsType<JsonArray>(LastLuaCall(h, ShopFeature.CmdReqBuyFurList));
        Assert.Equal(2, reply.Count);
        Assert.True(reply[0]!["ret"]!.GetValue<bool>());
        Assert.False(reply[1]!["ret"]!.GetValue<bool>());
        Assert.Equal(10_000 - CafePriceA, h.Player.GetMoney(MoneyType.CoffeeCoin));
    }

    /// <summary>tbParam 不是数组时（例如客户端发了对象）只记 WARN，不回包、不抛。</summary>
    [Fact]
    public async Task BuyFurList_NonArrayParam_IsIgnoredWithoutThrowing()
    {
        using var h = await FeatureHarness.CreateAsync("shop-fur-badparam");

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyFurList, "{\"shopid\":40001}");

        Assert.Empty(h.Outbound);
    }

    /// <summary>空数组：客户端 <c>:304 CountTB(tbParam) == 0</c> 直接 return，服务端也只是空回包。</summary>
    [Fact]
    public async Task BuyFurList_EmptyArray_RepliesEmptyArray()
    {
        using var h = await FeatureHarness.CreateAsync("shop-fur-empty");

        await h.DispatchLuaCallAsync(ShopFeature.CmdReqBuyFurList, "[]");

        var reply = Assert.IsType<JsonArray>(LastLuaCall(h, ShopFeature.CmdReqBuyFurList));
        Assert.Empty(reply);
    }
}
