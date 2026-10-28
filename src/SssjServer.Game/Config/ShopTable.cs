using System.Globalization;
using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 商品的「限量口径」—— 一条商品只会有其中一种。
///
/// 取证：<c>GoodsLogicC:LoadGoodsConfig</c>（<c>GoodsLogicC.lua:59..78</c>）对五个限量列各写了一条
/// 配置自检，要求商品配了某个限量列时，它所属商店的 <c>ShopTabList.RefreshRule</c> **必须等于对应值**：
/// <code>
/// RefreshLimitNum ⇔ RefreshRule == 1   -- 单次刷新限量
/// DailyLimitNum   ⇔ RefreshRule == 2   -- 每日限量
/// WeekLimitNum    ⇔ RefreshRule == 3   -- 每周限量
/// TotalLimitNum   ⇔ RefreshRule == 4   -- 活动期限量
/// ShelfLifeLmt    ⇔ RefreshRule == 5   -- 货架期限量
/// </code>
/// 也就是「限量列 ↔ 刷新规则」是 1:1 的，所以服务端只需要认一种。
/// 现网实测（<c>extracted/setting/GoodsList.txt</c>）：20001/20002 用 RefreshLimitNum、
/// 20010 用 DailyLimitNum、20005/20006/20008/20009 用 WeekLimitNum，40001 一个都不配。
/// </summary>
public enum ShopLimitKind
{
    /// <summary>不限购。</summary>
    None = 0,

    /// <summary>单次刷新限量（<c>RefreshLimitNum</c>，回包字段 <c>refreshbuynum</c>）。</summary>
    Refresh = 1,

    /// <summary>每日限量（<c>DailyLimitNum</c>，回包字段 <c>dailybuynum</c>）。</summary>
    Daily = 2,

    /// <summary>每周限量（<c>WeekLimitNum</c>，回包字段 <c>weekbuynum</c>）。</summary>
    Week = 3,

    /// <summary>活动期限量（<c>TotalLimitNum</c>，回包字段 <c>totalbuynum</c>）。</summary>
    Total = 4,

    /// <summary>货架期限量（<c>ShelfLifeLmt</c>，回包字段 <c>shelfbuynum</c>）。</summary>
    Shelf = 5,
}

/// <summary>
/// 折扣表的一档：<c>[权重-折扣]</c>。
///
/// 取证 <c>GoodsLogicC.lua:53..56</c>：
/// <code>
/// local discountlist = Parse2ItemList(tbRow["DiscountList"] or '[100,100]')
/// for _, v in pairs(discountlist) do
///     table.insert(tb.DiscountList, { weight = v[1], discount = v[2] })
/// </code>
/// <c>Parse2ItemList</c> 默认 <c>sep1=","</c>、<c>sep2="-"</c>（<c>Lib.lua:282..291</c>），
/// 所以现网的 <c>[1-10],[4-20],…</c> 解出来就是 (weight=1,discount=10)、(4,20)…
/// <see cref="Discount"/> 是**百分比**：100 = 原价、10 = 一折。
/// </summary>
/// <param name="Weight">加权随机的权重。</param>
/// <param name="Discount">折扣百分比（0..100）。</param>
public sealed record DiscountStep(int Weight, int Discount);

/// <summary>
/// 一个商店页签（<c>ShopTabList.txt</c> 的一行）。字段名与列名一致。
/// 客户端侧的同构实现是 <c>ShopTabLogicC:LoadTabsConfig</c>（<c>ShopTabLogicC.lua:14..77</c>）。
/// </summary>
public sealed class ShopTabRow
{
    /// <summary>商店 id（<c>ShopId</c>）。<c>ShopId / 10000</c> 就是 <see cref="GoodsRow.ShopType"/>。</summary>
    public int ShopId { get; init; }

    /// <summary>本地化 key（<c>Name</c>，如 <c>shop20001.Name</c>），仅日志用。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 开关（<c>OnOff</c>）：<b>1 = 普通商店</b>（进 <c>shopIdList</c>）、<b>2 = 咖啡馆商店</b>
    /// （进 <c>cafeShopIdList</c>）、<b>≤0 或非数字 = 整行跳过</b>
    /// —— 取证 <c>ShopTabLogicC.lua:23</c>（<c>break</c>）与 <c>:59..63</c>。
    /// 现网 52 行里 51 行是 1、只有 40001 是 2，没有 ≤0 的行。
    /// </summary>
    public int OnOff { get; init; }

    /// <summary>开放起（unix 秒）；0 = 未配置。原文形如 <c>2021-11-01-08-00-00</c>。</summary>
    public long BeginTime { get; init; }

    /// <summary>开放止（unix 秒）；0 = 未配置。</summary>
    public long EndTime { get; init; }

    /// <summary>
    /// 每次展示的商品数（<c>Columns</c>，表头「每次展示商品数」）。
    /// ★ 对**分组商店**它恰好等于该店的 <see cref="GoodsRow.Group"/> 个数
    /// （20001→8、20002→8、20005→9、20006→17、20007→6、20010→10、30008→15、30009→14，实测吻合）；
    /// 不分组的店一律 999 = 「全都展示」。
    /// </summary>
    public int Columns { get; init; }

    /// <summary>刷新规则，语义见 <see cref="ShopLimitKind"/>（同一套 1..5）。</summary>
    public int RefreshRule { get; init; }

    /// <summary>
    /// 每日自动刷新的时刻（<c>RefreshTime</c>，原文 <c>040000|180000</c> 或 <c>40000</c>）
    /// → 换算成「当天 0 点起的秒数」。空列为空数组。
    /// </summary>
    public int[] RefreshSecondsOfDay { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 手动刷新的费用阶梯（<c>RefreshFee</c>，原文 <c>15|30|60|90|120</c>）。
    /// 取证 <c>ShopTabLogicC.lua:42</c> <c>SplitStr2NumTB(tbRow["RefreshFee"], "|")</c>。
    /// </summary>
    public int[] RefreshFee { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 刷新费用的货币 id（<c>MoneyType</c>）。★ **空列缺省是 3（钻石）**，不是 0 ——
    /// 取证 <c>ShopTabLogicC.lua:43</c> <c>tonumber(tbRow["MoneyType"]) or 3</c>。
    /// </summary>
    public int MoneyType { get; init; } = ShopTable.DefaultRefreshMoneyType;

    /// <summary>
    /// 手动刷新次数上限（<c>RefreshLimits</c>，空列 = 0）。
    /// ★ 客户端自己就用它拦：<c>UI_ShopNew.lua:284</c>
    /// <c>if self.tbParam.refreshcount &gt;= tbTabCfg.RefreshLimits then SendClientTip(...)</c>
    /// —— 所以上限为 0 的商店（现网除 20001/20002 外全部）**根本发不出 cmd 12**。
    /// </summary>
    public int RefreshLimits { get; init; }

    /// <summary>该店顶栏要显示的货币 id 列表（<c>ShopMoneyType</c>，如 <c>3|12</c>）。服务端不用，收录备查。</summary>
    public int[] ShopMoneyType { get; init; } = Array.Empty<int>();

    /// <summary>关联活动（<c>tbActType</c>，如 <c>5|1</c>）。服务端不用，收录备查。</summary>
    public int[] ActType { get; init; } = Array.Empty<int>();

    /// <summary>页签图标（<c>IconId</c>）。§12.3 已验证 52 个页签图标一个都不缺。</summary>
    public string IconId { get; init; } = "";

    /// <summary>咖啡馆 BP 等级要求（<c>NeedCafeLevel</c>）。</summary>
    public int NeedCafeLevel { get; init; }

    /// <summary>显示类型（<c>ShowType</c>）：1 = 周年庆特殊商店（<c>ShopTabLogicC.lua:167</c>）。</summary>
    public int ShowType { get; init; }

    /// <summary>咖啡馆商店（<c>OnOff == 2</c>），走 <c>UI_NCafeShop</c> 而不是 <c>UI_ShopNew</c>。</summary>
    public bool IsCafeShop => OnOff == ShopTable.OnOffCafe;

    /// <summary>普通/活动商店（<c>OnOff == 1</c>）。</summary>
    public bool IsNormalShop => OnOff == ShopTable.OnOffNormal;

    /// <summary>配了时间窗（两端都有）。单端配置在现网不存在。</summary>
    public bool HasTimeWindow => BeginTime > 0 && EndTime > 0;

    /// <summary>
    /// 此刻是否在开放期。口径**照抄** <c>ShopTabLogicC:CheckShopOpen</c>（<c>ShopTabLogicC.lua:83..94</c>）：
    /// <c>if not beginTime or not endTime then return true end</c>（没配时间窗 ⇒ 恒开），
    /// 否则 <c>currTime &gt;= begin and currTime &lt;= end</c> —— **两端都是闭区间**
    /// （与抽卡的左闭右开不同，别照抄 <see cref="GachaPool.IsOpenAt"/>）。
    /// </summary>
    public bool IsOpenAt(long now)
        // Tower 21 is replayed locally; its original exchange shop follows that replay window.
        => !HasTimeWindow || (now >= BeginTime && now <= (ShopId == 30045 ? 3376684800L : EndTime));

    /// <summary>
    /// 下一次手动刷新要花多少钱。
    /// 取证 <c>UI_ShopNew.lua:288..291</c>（也是 <c>:1113..1116</c> 的显示口径）：
    /// <code>
    /// local nextRefreshNum = math.min(self.tbParam.refreshcount + 1, #tbRefreshFee)
    /// local RefreshFee     = tbRefreshFee[nextRefreshNum]
    /// </code>
    /// 空费用表返回 0（该店不支持手动刷新）。
    /// </summary>
    public int RefreshFeeAt(int refreshCount)
    {
        if (RefreshFee.Length == 0)
            return 0;
        int idx = Math.Min(Math.Max(refreshCount, 0) + 1, RefreshFee.Length);
        return RefreshFee[idx - 1];
    }

    /// <summary>该店是否支持手动刷新（上限 &gt; 0 且配了费用阶梯）。</summary>
    public bool CanManualRefresh => RefreshLimits > 0 && RefreshFee.Length > 0;

    public override string ToString()
        => $"#{ShopId} {Name} onoff={OnOff} columns={Columns} rule={RefreshRule} " +
           $"limits={RefreshLimits} money={MoneyType}";
}

/// <summary>
/// 一条商品（<c>GoodsList.txt</c> 的一行）。
/// 客户端侧的同构实现是 <c>GoodsLogicC:LoadGoodsConfig</c>（<c>GoodsLogicC.lua:17..106</c>），
/// 但注意**它不读 <see cref="Group"/> / <see cref="Weight"/> 之外的随机相关列**
/// —— 见 <see cref="Group"/> 的注释。
/// </summary>
public sealed class GoodsRow
{
    /// <summary>商店大类（<c>ShopType</c>）：0 无限 / 1 礼包 / 2 普通限量 / 3 活动商店 / 4 家具。
    /// 表头自注「需满足 ShopId/10000 = ShopType」，全表 1896 行实测无一例外。</summary>
    public int ShopType { get; init; }

    /// <summary>所属商店 id（<c>ShopId</c>）。</summary>
    public int ShopId { get; init; }

    /// <summary>商品 id（<c>GoodsId</c>，表头注「有限量的 id 需 &lt; 65536」）。全表唯一。</summary>
    public int GoodsId { get; init; }

    /// <summary>商品名（<c>Name</c>，中文原文，日志用；界面上显示的是 ItemList 的名字）。</summary>
    public string Name { get; init; } = "";

    // ---- GDPLNX ----

    public int G { get; init; }
    public int D { get; init; }
    public int P { get; init; }
    public int L { get; init; }

    /// <summary>
    /// 一份商品给几个（<c>GDPLNX</c> 的第 5 位 N）。
    /// 取证 <c>GoodsLogicC.lua:33</c> <c>tb.GoodsNum = gdpl[5] or 1</c>；
    /// 客户端算奖励是 <c>GoodsLogicC.lua:396</c> <c>(goodsCfg.GoodsNum or 1) * tbParam.buynum</c>。
    /// 现网 1894/1896 行是 5 段（有 N），2 行只有 4 段（N 缺省 1）。
    /// </summary>
    public int GoodsNum { get; init; } = 1;

    /// <summary>(G,D,P,L) 四元组。</summary>
    public int[] Gdpl => new[] { G, D, P, L };

    /// <summary>
    /// 位置分组（<c>Group</c>，原文形如 <c>[3]</c>）。null = 该行没配。
    ///
    /// ★ **这是纯服务端列**：全仓 lua 里 <c>tbRow["Group"]</c> 出现 0 次
    /// （<c>GoodsLogicC:LoadGoodsConfig</c> 从 <c>ShopType</c> 一路读到 <c>NeedCafeLevel</c>，
    /// 唯独跳过 Group）。它的语义只能从两处硬事实反推：
    /// <list type="number">
    ///   <item><see cref="ShopTabRow.Columns"/> 恰好等于该店的 Group 个数（8 家分组商店全部吻合）；</item>
    ///   <item>回包里 <c>goodslist[i].pos</c> 就是 Group 号 ——
    ///     <c>GoodsLogicC:_CheckClubLock</c>（<c>GoodsLogicC.lua:229..241</c>）拿
    ///     <c>tbInfo.pos</c> 去查社团等级的 <c>OpenColumnList[pos]</c>，即「第几栏」。</item>
    /// </list>
    /// ⇒ 原服的随机规则是「**每个 Group 按 <see cref="Weight"/> 加权抽 1 件**」。
    /// 现网 1643/1896 行没有 Group（那些店 Columns 都是 999）⇒ 不分组 = 全部上架、不随机。
    /// </summary>
    public int? Group { get; init; }

    /// <summary>同组内加权随机的权重（<c>Weight</c>）。现网 20001 全部是 10（等概率）。</summary>
    public int Weight { get; init; }

    // ---- 价格 ----

    /// <summary>主货币 id（<c>Price1</c> 的 <c>|</c> 前半）。</summary>
    public int MoneyType { get; init; }

    /// <summary>
    /// 主货币价格**数组**（<c>Price1</c> 的 <c>|</c> 后半，可能是 <c>[60,90,120,…]</c>）。
    /// 取哪一档见 <see cref="PriceAt"/>。至少 1 个元素（现网 1896 行 Price1 全部非空）。
    /// </summary>
    public int[] Prices { get; init; } = Array.Empty<int>();

    /// <summary>备用货币 id（<c>Price2</c>），null = 没配。</summary>
    public int? MoneyType2 { get; init; }

    /// <summary>备用货币价格数组。</summary>
    public int[] Prices2 { get; init; } = Array.Empty<int>();

    // ---- 限量 ----

    public int? RefreshLimitNum { get; init; }
    public int? DailyLimitNum { get; init; }
    public int? WeekLimitNum { get; init; }
    public int? TotalLimitNum { get; init; }
    public int? ShelfLifeLmt { get; init; }

    /// <summary>本行生效的限量口径（五个列里最多一个非空，见 <see cref="ShopLimitKind"/> 的取证）。</summary>
    public ShopLimitKind LimitKind
        => RefreshLimitNum.HasValue ? ShopLimitKind.Refresh
         : DailyLimitNum.HasValue ? ShopLimitKind.Daily
         : WeekLimitNum.HasValue ? ShopLimitKind.Week
         : TotalLimitNum.HasValue ? ShopLimitKind.Total
         : ShelfLifeLmt.HasValue ? ShopLimitKind.Shelf
         : ShopLimitKind.None;

    /// <summary>本行生效的限量值；不限购返回 null。判定顺序与客户端
    /// <c>GoodsLogicC:IsInStock</c>（<c>GoodsLogicC.lua:189..204</c>）一致。</summary>
    public int? LimitNum => LimitKind switch
    {
        ShopLimitKind.Refresh => RefreshLimitNum,
        ShopLimitKind.Daily => DailyLimitNum,
        ShopLimitKind.Week => WeekLimitNum,
        ShopLimitKind.Total => TotalLimitNum,
        ShopLimitKind.Shelf => ShelfLifeLmt,
        _ => null,
    };

    /// <summary>标签 id（<c>Promotion</c>：新品 1 / 热销 2）。</summary>
    public int Promotion { get; init; }

    /// <summary>
    /// 折扣加权表（<c>DiscountList</c>）。**空列表示该行没配**。
    ///
    /// ★ 空列的后果非常反直觉（见 <see cref="ShopTable"/> 类注释的「折扣」段）：
    /// 客户端缺省值 <c>'[100,100]'</c> 被 <c>Parse2ItemList</c> 用 <c>-</c> 当内分隔符解析，
    /// 解出来 <c>discount</c> 是 **nil**，于是 <c>GetDisCount</c> 走 <c>or 0</c> ⇒ **0 折 = 免费**。
    /// 所以服务端必须**无条件**下发 <c>discount</c>。
    /// </summary>
    public List<DiscountStep> DiscountList { get; init; } = new();

    /// <summary>上架起（unix 秒），0 = 未配置。原文形如 <c>[201912061200]</c>（带方括号）。</summary>
    public long BeginTime { get; init; }

    /// <summary>下架止（unix 秒），0 = 未配置。</summary>
    public long EndTime { get; init; }

    /// <summary>商店当日排序（<c>DayIndex</c>，仅家具商店用）。</summary>
    public int DayIndex { get; init; }

    /// <summary>
    /// 咖啡馆 BP 等级要求（<c>NeedCafeLevel</c>）。
    /// 表头自注「**只用于 client 过滤，不影响随机规则**」，客户端确实只在
    /// <c>UI_ShopNew.lua:1102</c> 与 <c>GoodsLogicC:_CheckBattlePassLock</c> 里用它筛显示。
    /// ⇒ 服务端照常下发，不做过滤。
    /// </summary>
    public int NeedCafeLevel { get; init; }

    /// <summary>
    /// 此刻是否在上架期。口径照 <c>GoodsLogicC:CheckGoodInTime</c>（<c>GoodsLogicC.lua:123..150</c>）：
    /// Begin 为 0 视为「早就上架」，End 为 0 视为「永不下架」，两端都是闭区间
    /// （<c>GetTime() &gt;= sec1</c> / <c>GetTime() &lt;= sec2</c>）。
    ///
    /// ★ 这条**客户端自己也会再筛一遍**（<c>UI_ShopNew.lua:1101</c>：
    /// <c>if GoodsLogicC:CheckGoodInTime(tbGoodsCfg) then list:Add(GoodsInfo) end</c>），
    /// 所以服务端下发过期商品的表现是「悄悄不显示」而不是报错 —— 但那会让
    /// 分组商店少一格，所以服务端在生成货架时就得过滤。
    /// </summary>
    public bool IsInTimeAt(long now)
    {
        if (BeginTime > 0 && now < BeginTime)
            return false;
        if (EndTime > 0 && now > EndTime)
            return false;
        return true;
    }

    /// <summary>
    /// 取第 <paramref name="index1Based"/> 档价格（1 起）。
    /// 越界按客户端 <c>math.min(idx, #tbGoodsCfg.SellingPrice)</c> 夹到最后一档
    /// （<c>GoodsLogicC.lua:172</c>）。价格数组为空返回 0。
    /// </summary>
    public int PriceAt(int index1Based)
    {
        if (Prices.Length == 0)
            return 0;
        int idx = Math.Min(Math.Max(index1Based, 1), Prices.Length);
        return Prices[idx - 1];
    }

    /// <summary>
    /// 折后单价。**必须与客户端逐字一致**：<c>math.ceil(SellingPrice[idx] * discount / 100)</c>
    /// （<c>UI_ShopNew.lua:402</c> 列表页、<c>UI_ShopTips.lua:190</c> 详情页、
    /// <c>:401</c> 点「购买」时的扣费判定都是这一式），总价再乘 <c>buynum</c>
    /// —— 注意是「**先 ceil 单价再乘数量**」，不是「总价 ceil」。
    /// </summary>
    public int UnitPrice(int index1Based, int discount)
    {
        long raw = (long)PriceAt(index1Based) * discount;
        return (int)((raw + 99) / 100);     // = ceil(raw / 100)，raw 恒非负
    }

    public override string ToString()
        => $"#{GoodsId} {Name} ({G},{D},{P},{L})x{GoodsNum} shop={ShopId} " +
           $"group={(Group?.ToString() ?? "-")} w={Weight} price={MoneyType}|{string.Join(',', Prices)}";
}

/// <summary>
/// 商店域的两张配置表：<c>ShopTabList.txt</c>（商店页签）+ <c>GoodsList.txt</c>（商品）。
///
/// <para><b>为什么两张表放一个文件</b></para>
/// 它们必须互相校验才有意义：商品的限量列与商店的 <c>RefreshRule</c> 是 1:1 绑定
/// （见 <see cref="ShopLimitKind"/>），商品的 <c>Group</c> 个数与商店的 <c>Columns</c> 一致
/// （见 <see cref="GoodsRow.Group"/>）。分成两个加载器会让这些校验没处放。
///
/// <para><b>现网数字（实测，改表要同步改测试）</b></para>
/// <list type="bullet">
///   <item><c>ShopTabList.txt</c>：<b>55 行文件 / 53 段数据 / 52 行非空 ⇒ 52 个商店</b>，
///     其中 <c>OnOff==1</c> 51 个、<c>OnOff==2</c> 1 个（40001 咖啡馆家具店），无 ≤0 的行。
///     <b>没有时间窗（永久开放）的 9 个</b>：20001、20002、20005、20006、20007、20008、
///     20009、20010、40001；3000x 全部带时间窗且**当前全部不在期内**。</item>
///   <item><c>GoodsList.txt</c>：<b>1973 行文件 / 1971 段数据 / 1897 行非空 / 1896 条有效商品</b>
///     （1 行 GoodsId 为空被 <c>GoodsLogicC.lua:31</c> 的 <c>if tb.GoodsId == 0 then break</c> 跳过），
///     GoodsId 全表唯一。<b>1643 行没有 Group</b>、<b>1816 行没有 DiscountList</b>
///     （有折扣表的只有 20001 的 56 行 + 20002 的 24 行）。</item>
///   <item>靶子店 <b>20001</b>：56 件商品、8 个 Group（[1]..[8]，各 1 件上架 ⇒ 货架恒 8 格）、
///     Weight 全 10、<c>RefreshLimitNum=1</c>、<c>Price1=3|N</c>（钻石）、
///     折扣表 <c>[1-10],[4-20],[10-30],[10-50],[20-70],[25-90],[30-100]</c>（权重和恰为 100）。</item>
///   <item>靶子店 <b>40001</b>（咖啡馆家具）：499 件、无 Group（Columns=999 ⇒ 全上架）、
///     无限量、<c>Price1=7|N</c>（咖啡币）、无折扣表 ⇒ <c>discount</c> 必须由服务端填 100。</item>
/// </list>
///
/// <para><b>★ 折扣：为什么「不给 discount」比想象的更糟</b></para>
/// <c>GoodsLogicC:GetDisCount</c>（<c>GoodsLogicC.lua:175..182</c>）：
/// <code>
/// if mebuyrec and mebuyrec.discount then return mebuyrec.discount
/// else return ... and tbGoodsCfg.DiscountList[1].discount or 0 end
/// </code>
/// 回落到 <c>DiscountList[1]</c>：
/// <list type="bullet">
///   <item>20001/20002 的第一档是 <c>[1-10]</c> ⇒ discount=10 ⇒ **一折**；</item>
///   <item>其余 1816 件商品**没配** DiscountList，客户端缺省值 <c>'[100,100]'</c> 会被
///     <c>Parse2ItemList</c>（sep1=<c>,</c>、sep2=<c>-</c>）解成 <c>{{100},{100}}</c>，
///     即 <c>discount == nil</c> ⇒ 走 <c>or 0</c> ⇒ **0 折（价格显示 0）**。</item>
/// </list>
/// 所以「省略 discount」在现网 96% 的商品上是「白送」，不是「一折」。
///
/// <para><b>缺表降级</b></para>
/// 任一表缺失都返回空表并打 WARN（**绝不抛异常**，与其它配置表一致）：
/// <see cref="GetShop"/>/<see cref="GetGoods"/> 一律 null，商店域会回错误提示而不是崩。
/// </summary>
public sealed class ShopTable
{
    /// <summary>商店页签表文件名（客户端口径 <c>"Shop/ShopTabList.txt"</c>，本目录是扁平化的）。</summary>
    public const string ShopFileName = "ShopTabList.txt";

    /// <summary>商品表文件名（客户端口径 <c>"Shop/GoodsList.txt"</c>）。</summary>
    public const string GoodsFileName = "GoodsList.txt";

    /// <summary><c>OnOff == 1</c>：普通/活动商店（<c>ShopTabLogicC.lua:59</c>）。</summary>
    public const int OnOffNormal = 1;

    /// <summary><c>OnOff == 2</c>：咖啡馆商店（<c>ShopTabLogicC.lua:61</c>）。</summary>
    public const int OnOffCafe = 2;

    /// <summary>刷新费货币的缺省 id（<c>ShopTabLogicC.lua:43</c> 的 <c>or 3</c>）。</summary>
    public const int DefaultRefreshMoneyType = 3;

    /// <summary>不分组商店的 <c>Columns</c> 取值（= 全部上架）。</summary>
    public const int ColumnsAll = 999;

    /// <summary>咖啡馆家具店 id（<c>ShopData.lua:8</c> <c>ShopData.CafeShopId = 40001</c>）。</summary>
    public const int CafeShopId = 40001;

    /// <summary>角色卡商店 id（<c>GoodsLogicC.lua:4</c>）。</summary>
    public const int GirlCardShopId = 20005;

    /// <summary>社团商店 id（<c>GoodsLogicC.lua:5</c>）—— **唯一会读 <c>pos</c> 的店**。</summary>
    public const int ClubShopId = 20006;

    private readonly Dictionary<int, ShopTabRow> _shops = new();
    private readonly Dictionary<int, GoodsRow> _goodsById = new();
    private readonly Dictionary<int, List<GoodsRow>> _goodsByShop = new();

    private ShopTable() { }

    /// <summary>永久空表（缺表降级用）。</summary>
    public static ShopTable Empty { get; } = new();

    /// <summary>商店个数。</summary>
    public int ShopCount => _shops.Count;

    /// <summary>有效商品条数。</summary>
    public int GoodsCount => _goodsById.Count;

    /// <summary>全部商店（按 ShopId 升序，保证遍历可复现）。</summary>
    public IEnumerable<ShopTabRow> Shops => _shops.Values.OrderBy(s => s.ShopId);

    /// <summary>全部商品（按 GoodsId 升序）。</summary>
    public IEnumerable<GoodsRow> AllGoods => _goodsById.Values.OrderBy(g => g.GoodsId);

    /// <summary>按商店 id 查页签配置；不存在返回 null。</summary>
    public ShopTabRow? GetShop(int shopId) => _shops.TryGetValue(shopId, out var s) ? s : null;

    /// <summary>按商品 id 查配置；不存在返回 null（客户端同名接口 <c>GoodsLogicC:GetGoodsCfg</c>）。</summary>
    public GoodsRow? GetGoods(int goodsId) => _goodsById.TryGetValue(goodsId, out var g) ? g : null;

    /// <summary>某店的全部商品（**文件顺序**，与原表一致，便于对照）。</summary>
    public IReadOnlyList<GoodsRow> GoodsOf(int shopId)
        => _goodsByShop.TryGetValue(shopId, out var list) ? list : Array.Empty<GoodsRow>();

    /// <summary>某店的 Group 号（升序去重）。不分组的店返回空。</summary>
    public IReadOnlyList<int> GroupsOf(int shopId)
        => GoodsOf(shopId).Where(g => g.Group.HasValue)
                          .Select(g => g.Group!.Value)
                          .Distinct().OrderBy(v => v).ToList();

    /// <summary>该店是否按 Group 随机上架（有任何一条商品配了 Group）。</summary>
    public bool IsGroupedShop(int shopId) => GoodsOf(shopId).Any(g => g.Group.HasValue);

    /// <summary>该店是否有商品配了折扣表（决定货架是否必须持久化，见 ShopFeature）。</summary>
    public bool HasDiscountShop(int shopId) => GoodsOf(shopId).Any(g => g.DiscountList.Count > 0);

    /// <summary>
    /// 从 setting 目录加载两张表。任一缺失 → 该部分降级为空并打 WARN，**不抛异常**。
    /// </summary>
    public static ShopTable Load(string settingDir)
    {
        var table = new ShopTable();

        // ---------------- ShopTabList.txt ----------------
        var shopRaw = SettingTable.Load(Path.Combine(settingDir, ShopFileName));
        if (shopRaw is null)
        {
            Log.Warn($"[setting] {ShopFileName} 缺失：商店域降级（cmd 11 一律回 errorKey，不会崩）");
        }
        else
        {
            int skipped = 0;
            foreach (var row in shopRaw.Rows)
            {
                // ★ OnOff 非数字或 <= 0 ⇒ 整行跳过（ShopTabLogicC.lua:23 的 break）
                string onOffText = shopRaw.Str(row, "OnOff");
                if (!int.TryParse(onOffText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int onOff)
                    || onOff <= 0)
                {
                    skipped++;
                    continue;
                }

                int shopId = shopRaw.Int(row, "ShopId", -1);
                if (shopId <= 0)
                {
                    skipped++;
                    continue;
                }

                var shop = new ShopTabRow
                {
                    ShopId = shopId,
                    Name = shopRaw.Str(row, "Name"),
                    OnOff = onOff,
                    BeginTime = ParseDashDate(shopRaw.Str(row, "Begin")),
                    EndTime = ParseDashDate(shopRaw.Str(row, "End")),
                    Columns = shopRaw.Int(row, "Columns"),
                    RefreshRule = shopRaw.Int(row, "RefreshRule"),
                    RefreshSecondsOfDay = ParseClockList(shopRaw.Str(row, "RefreshTime")),
                    RefreshFee = ParseIntList(shopRaw.Str(row, "RefreshFee")),
                    // ★ 缺省 3（钻石），不是 0
                    MoneyType = shopRaw.Int(row, "MoneyType", DefaultRefreshMoneyType),
                    RefreshLimits = shopRaw.Int(row, "RefreshLimits"),
                    ShopMoneyType = ParseIntList(shopRaw.Str(row, "ShopMoneyType")),
                    ActType = ParseIntList(shopRaw.Str(row, "tbActType")),
                    IconId = shopRaw.Str(row, "IconId"),
                    NeedCafeLevel = shopRaw.Int(row, "NeedCafeLevel"),
                    ShowType = shopRaw.Int(row, "ShowType"),
                };

                if (table._shops.ContainsKey(shopId))
                {
                    // 客户端这里是 print("ShopId.. repeated")（ShopTabLogicC.lua:54），服务端只 WARN
                    Log.Warn($"[setting] {ShopFileName} 存在重复 ShopId={shopId}，保留先出现的那行");
                    continue;
                }
                table._shops[shopId] = shop;
            }
            if (skipped > 0)
                Log.Info($"[setting] {ShopFileName} 跳过 {skipped} 行（OnOff 非法或 ≤0）");
        }

        // ---------------- GoodsList.txt ----------------
        var goodsRaw = SettingTable.Load(Path.Combine(settingDir, GoodsFileName));
        if (goodsRaw is null)
        {
            Log.Warn($"[setting] {GoodsFileName} 缺失：全部商店的货架为空（界面空但不报错）");
            return table;
        }

        int noGoodsId = 0, badGdpl = 0, badPrice = 0, typeMismatch = 0, ruleMismatch = 0;
        foreach (var row in goodsRaw.Rows)
        {
            // GoodsId 为 0/空 ⇒ 跳过（GoodsLogicC.lua:31）
            int goodsId = goodsRaw.Int(row, "GoodsId", 0);
            if (goodsId == 0)
            {
                noGoodsId++;
                continue;
            }

            var gdpl = ParseTuple(goodsRaw.Str(row, "GDPLNX"));
            if (gdpl.Length < 4)
            {
                badGdpl++;
                Log.Warn($"[setting] {GoodsFileName} GoodsId={goodsId} 的 GDPLNX 不足 4 段，跳过");
                continue;
            }

            var (money1, prices1) = ParsePrice(goodsRaw.Str(row, "Price1"));
            if (prices1.Length == 0)
            {
                // 客户端这里会直接 index nil 报错（GoodsLogicC.lua:40 money[1][1]），服务端只跳过
                badPrice++;
                Log.Warn($"[setting] {GoodsFileName} GoodsId={goodsId} 的 Price1 无法解析，跳过");
                continue;
            }
            var (money2, prices2) = ParsePrice(goodsRaw.Str(row, "Price2"));

            int shopId = goodsRaw.Int(row, "ShopId", 0);
            int shopType = goodsRaw.Int(row, "ShopType", -1);
            if (shopId > 0 && shopType != shopId / 10000)
            {
                // 与客户端同一条自检（GoodsLogicC.lua:27..29 的 Game.Logger.Error），现网 0 命中
                typeMismatch++;
                Log.Warn($"[setting] {GoodsFileName} GoodsId={goodsId} 的 ShopType={shopType} " +
                         $"与 ShopId={shopId} 不匹配（应为 {shopId / 10000}）");
            }

            var goods = new GoodsRow
            {
                ShopType = shopType,
                ShopId = shopId,
                GoodsId = goodsId,
                Name = goodsRaw.Str(row, "Name"),
                G = gdpl[0],
                D = gdpl[1],
                P = gdpl[2],
                L = gdpl[3],
                GoodsNum = gdpl.Length >= 5 && gdpl[4] > 0 ? gdpl[4] : 1,
                Group = ParseGroup(goodsRaw.Str(row, "Group")),
                Weight = goodsRaw.Int(row, "Weight"),
                MoneyType = money1,
                Prices = prices1,
                MoneyType2 = prices2.Length > 0 ? money2 : null,
                Prices2 = prices2,
                RefreshLimitNum = ParseNullableInt(goodsRaw.Str(row, "RefreshLimitNum")),
                DailyLimitNum = ParseNullableInt(goodsRaw.Str(row, "DailyLimitNum")),
                WeekLimitNum = ParseNullableInt(goodsRaw.Str(row, "WeekLimitNum")),
                TotalLimitNum = ParseNullableInt(goodsRaw.Str(row, "TotalLimitNum")),
                ShelfLifeLmt = ParseNullableInt(goodsRaw.Str(row, "ShelfLifeLmt")),
                Promotion = goodsRaw.Int(row, "Promotion"),
                DiscountList = ParseDiscountList(goodsRaw.Str(row, "DiscountList")),
                BeginTime = ParseBracketDate(goodsRaw.Str(row, "Begin")),
                EndTime = ParseBracketDate(goodsRaw.Str(row, "End")),
                DayIndex = goodsRaw.Int(row, "DayIndex"),
                NeedCafeLevel = goodsRaw.Int(row, "NeedCafeLevel"),
            };

            if (table._goodsById.ContainsKey(goodsId))
            {
                // 客户端是 Game.Logger.Error（GoodsLogicC.lua:93），服务端只 WARN 并保留先出现的
                Log.Warn($"[setting] {GoodsFileName} 存在重复 GoodsId={goodsId}，保留先出现的那行");
                continue;
            }
            table._goodsById[goodsId] = goods;

            if (!table._goodsByShop.TryGetValue(shopId, out var list))
                table._goodsByShop[shopId] = list = new List<GoodsRow>();
            list.Add(goods);

            // 「限量列 ↔ RefreshRule」1:1 自检（GoodsLogicC.lua:59..78 的五条 Logger.Error）
            var shopCfg = table.GetShop(shopId);
            if (shopCfg is not null && goods.LimitKind != ShopLimitKind.None
                && shopCfg.RefreshRule != (int)goods.LimitKind)
            {
                ruleMismatch++;
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int openShops = table.Shops.Count(s => s.IsOpenAt(now));
        Log.Info($"[setting] {ShopFileName} 载入 {table._shops.Count} 个商店" +
                 $"（普通 {table.Shops.Count(s => s.IsNormalShop)} / 咖啡馆 {table.Shops.Count(s => s.IsCafeShop)}，" +
                 $"当前开放 {openShops} 个）；{GoodsFileName} 载入 {table._goodsById.Count} 条商品" +
                 $"（分组商店 {table._goodsByShop.Keys.Count(table.IsGroupedShop)} 个" +
                 (noGoodsId > 0 ? $"，跳过 GoodsId 为空 {noGoodsId} 行" : "") +
                 (badGdpl > 0 ? $"，GDPLNX 非法 {badGdpl} 行" : "") +
                 (badPrice > 0 ? $"，Price1 非法 {badPrice} 行" : "") +
                 (typeMismatch > 0 ? $"，ShopType 不匹配 {typeMismatch} 行" : "") +
                 (ruleMismatch > 0 ? $"，限量列与 RefreshRule 不匹配 {ruleMismatch} 行" : "") + "）");
        return table;
    }

    // =====================================================================
    //  解析工具（全部照客户端 Lib.lua 的口径，internal 以便单测精确校验）
    // =====================================================================

    /// <summary>
    /// 解析 <c>[7-1-1-3-1]</c> 这类元组（客户端 <c>Parse2Item</c>，<c>Lib.lua:275..280</c>：
    /// 先 <c>gsub('[%[" %]]', "")</c> 去掉方括号/引号/空格，再按 <c>-</c> 拆）。
    /// 解析不出数字的段按 0 处理；空串返回空数组。
    /// </summary>
    internal static int[] ParseTuple(string value)
    {
        string body = StripBrackets(value);
        if (body.Length == 0)
            return Array.Empty<int>();
        var parts = body.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var result = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            result[i] = int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
                ? v : 0;
        }
        return result;
    }

    /// <summary>
    /// 解析 <c>Price1</c> / <c>Price2</c>：<c>代币类型|价1,价2,…</c>，价格段可能再套一层方括号
    /// （现网确有 <c>5|[60,90,120,150,200,255]</c> 这种写法）。
    ///
    /// 口径照 <c>GoodsLogicC.lua:39..44</c>：
    /// <code>
    /// local money = Parse2ItemList(tbRow["Price1"], "|", ",")
    /// tb.MoneyType, tb.SellingPrice = money[1][1], money[2]
    /// </code>
    /// <c>Parse2ItemList</c> **先整串去掉方括号**再按 <c>|</c> 拆、每段再按 <c>,</c> 拆
    /// ⇒ 内层的方括号根本不影响结果。空串返回 <c>(0, [])</c>。
    /// </summary>
    internal static (int MoneyType, int[] Prices) ParsePrice(string value)
    {
        string body = StripBrackets(value);
        if (body.Length == 0)
            return (0, Array.Empty<int>());

        var segs = body.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0)
            return (0, Array.Empty<int>());

        if (!int.TryParse(segs[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int money))
            return (0, Array.Empty<int>());
        if (segs.Length < 2)
            return (money, Array.Empty<int>());

        var prices = new List<int>();
        foreach (var tok in segs[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(tok.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                prices.Add(p);
        }
        return (money, prices.ToArray());
    }

    /// <summary>
    /// 解析 <c>DiscountList</c>：<c>[1-10],[4-20],…</c> → <c>(weight, discount)</c> 列表
    /// （<c>GoodsLogicC.lua:53..56</c>，内分隔符是 <c>-</c>）。
    /// **空串返回空列表** —— 不要模仿客户端那个坏掉的 <c>'[100,100]'</c> 缺省值。
    /// </summary>
    internal static List<DiscountStep> ParseDiscountList(string value)
    {
        var result = new List<DiscountStep>();
        string body = StripBrackets(value);
        if (body.Length == 0)
            return result;

        foreach (var seg in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = seg.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (pair.Length < 2)
                continue;
            if (!int.TryParse(pair[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w))
                continue;
            if (!int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int d))
                continue;
            result.Add(new DiscountStep(w, d));
        }
        return result;
    }

    /// <summary>解析 <c>Group</c> 列（原文 <c>[3]</c>）；空列返回 null。</summary>
    internal static int? ParseGroup(string value)
    {
        string body = StripBrackets(value);
        if (body.Length == 0)
            return null;
        return int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    /// <summary>空列 ⇒ null（**不是 0**）。客户端也是 <c>tonumber(...)</c> 不带 <c>or 0</c>，
    /// 而 <c>IsInStock</c> / <c>_getNextPriceIdx</c> 都用 <c>if cfg.XLimitNum and ...</c> 判在不在，
    /// 所以「配了 0」和「没配」语义不同，必须用可空整型表达。</summary>
    internal static int? ParseNullableInt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : null;
    }

    /// <summary>解析用 <c>|</c> 分隔的整数列表（<c>SplitStr2NumTB</c>，<c>Lib.lua:586..597</c>）。</summary>
    internal static int[] ParseIntList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<int>();
        var result = new List<int>();
        foreach (var tok in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(tok.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                result.Add(v);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 解析 <c>RefreshTime</c>（<c>040000|180000</c> 或 <c>40000</c>）→「当天 0 点起的秒数」升序去重。
    /// 每段是 <c>HHMMSS</c>（不足 6 位按数值补前导 0，与 <c>GetDate2Time</c> 的数值口径一致）。
    /// 非法时刻（时 &gt; 23 等）会被丢弃并打 WARN。
    /// </summary>
    internal static int[] ParseClockList(string value)
    {
        var result = new List<int>();
        foreach (int raw in ParseIntList(value))
        {
            int hour = raw / 10000, min = raw / 100 % 100, sec = raw % 100;
            if (hour is < 0 or > 23 || min is < 0 or > 59 || sec is < 0 or > 59)
            {
                Log.Warn($"[setting] {ShopFileName} 的 RefreshTime 段 {raw} 不是合法 HHMMSS，忽略");
                continue;
            }
            int ofs = hour * 3600 + min * 60 + sec;
            if (!result.Contains(ofs))
                result.Add(ofs);
        }
        result.Sort();
        return result.ToArray();
    }

    /// <summary>
    /// 解析 <c>GoodsList</c> 的 <c>Begin</c>/<c>End</c>（原文 <c>[201912061200]</c>）。
    /// 客户端是 <c>tonumber(string.sub(tbRow["Begin"] or "", 2, -2)) or 0</c>
    /// （<c>GoodsLogicC.lua:80..81</c>）—— 就是「掐掉首尾各一个字符」，所以方括号是必须的。
    /// 本实现更宽松：去掉方括号后按位数解析，解析失败返回 0。
    /// </summary>
    internal static long ParseBracketDate(string value) => ParseDateDigits(StripBrackets(value));

    /// <summary>
    /// 解析 <c>ShopTabList</c> 的 <c>Begin</c>/<c>End</c>（原文 <c>2021-11-01-08-00-00</c>）。
    /// 客户端把原文直接喂 <c>GetDate2Time</c>，而它第一步就是 <c>string.gsub(nDate,'-','')</c>
    /// （<c>Lib.lua:1306</c>）⇒ 变成 14 位数字串。
    /// </summary>
    internal static long ParseDashDate(string value) => ParseDateDigits(StripBrackets(value));

    /// <summary>
    /// <c>yyyyMMdd[HH[mm[ss]]]</c> 数字串 → unix 秒。位数决定精度，
    /// 与客户端 <c>GetDate2Time</c>（<c>Lib.lua:1304..1345</c>）的 8/10/12/14 位分支一一对应；
    /// 其它位数返回 0（客户端也是 <c>return</c> nil）。
    ///
    /// ★ 时区：一律按 **UTC** 解释。客户端用的是 <c>os.time</c>（设备本地时区），
    /// 本服务端全链路（登录时间、抽卡档期 <see cref="GachaTable.ParseDate"/>）都按 UTC，
    /// 保持一致比逐处纠结时区更重要 —— 影响只有「活动商店开关时刻差几小时」。
    /// </summary>
    internal static long ParseDateDigits(string text)
    {
        string s = text.Replace("-", "").Replace(":", "").Replace(" ", "").Trim();
        if (s.Length == 0 || !long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return 0;

        int year, mon, day, hour = 0, min = 0, sec = 0;
        switch (s.Length)
        {
            case 8:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                break;
            case 10:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                hour = int.Parse(s[8..10]);
                break;
            case 12:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                hour = int.Parse(s[8..10]); min = int.Parse(s[10..12]);
                break;
            case 14:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                hour = int.Parse(s[8..10]); min = int.Parse(s[10..12]); sec = int.Parse(s[12..14]);
                break;
            default:
                return 0;
        }

        try
        {
            var dt = new DateTime(year, mon, day, hour, min, sec, DateTimeKind.Utc);
            return new DateTimeOffset(dt).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    /// <summary>去掉方括号 / 引号 / 空格（客户端 <c>gsub('[%[" %]]', "")</c> 的等价实现）。</summary>
    private static string StripBrackets(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        Span<char> buf = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        int n = 0;
        foreach (char c in value)
        {
            if (c is '[' or ']' or '"' or ' ' or '\t')
                continue;
            buf[n++] = c;
        }
        return new string(buf[..n]);
    }
}
