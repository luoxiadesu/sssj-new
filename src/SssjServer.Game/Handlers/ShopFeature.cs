using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 商店域（<c>UI_ShopNew</c> / <c>UI_ShopEntry</c> / <c>UI_NCafeShop</c> 那三屏）。
///
/// <para><b>0. 四条上行都是 LuaCall 数字命令</b></para>
/// <list type="table">
/// <item><term>11 <c>Shop_ReqGoodsList</c></term><description><c>{shopid}</c> ——
///   <c>LuaCall.lua:19</c> 定义 / <c>:671</c> 注册 → 原服 <c>Mall:ReqGoodsList</c>（模块不在包内）。
///   发送点 4 处：<c>UI_ShopNew.lua:1008</c>（开界面/切页签）、<c>UI_ShopEntry.lua:80</c>（商店大厅）、
///   <c>UI_NCafeShop.lua:367</c>、<c>UI_2ndAnniversaryShop.lua:283</c>，
///   外加 <c>GoodsLogicC.lua:421</c>（咖啡馆买完自动重拉）。</description></item>
/// <item><term>12 <c>Shop_ReqRefreshGoodsList</c></term><description><c>{shopid}</c> ——
///   <c>:20</c> / <c>:689</c>。唯一发送点 <c>UI_ShopNew.lua:296</c>。</description></item>
/// <item><term>13 <c>Shop_ReqBuyGoods</c></term><description><c>{shopid, goodsid, buynum}</c> ——
///   <c>:21</c> / <c>:702</c>。发送点 4 处：<c>UI_ShopTips.lua:408</c>、<c>UI_CafeItemTips.lua:573</c>、
///   <c>UI_Exchange.lua:181</c>、<c>GoodsLogicC.lua</c> 间接。</description></item>
/// <item><term>14 <c>Shop_ReqBuyFurList</c></term><description>**数组**
///   <c>[{shopid,goodsid,buynum},…]</c> —— <c>:22</c> / <c>:716</c>。
///   唯一发送点 <c>UI_NCafeShop.lua:181</c>（咖啡馆「按主题一键买齐」）。</description></item>
/// </list>
/// 本族是 <b>LuaCall 数字命令</b>，所以 <b>成功回包绝对不能带 <c>nError</c></b>
/// （AGENTS.md §5：客户端 <c>if tbParam.nError then</c> 是 Lua 真值判断，0 也是真）。
/// 失败一律走 <c>S2C.CallClientTip</c> / <c>S2C.CallClientError</c>
/// （<c>S2CCall.lua:89..99</c>，两者都会先 <c>Game.Blocking.Hide()</c>）。
///
/// <para><b>1. ★ cmd 11/12 的四字段门禁 —— 本域最难查的失败</b></para>
/// <c>GoodsLogicC:OnReqGoodsList</c>（<c>GoodsLogicC.lua:273..279</c>）与
/// <c>OnReqRefreshGoodsList</c>（<c>:330..336</c>）开头一模一样：
/// <code>
/// if type(tbParam) ~= "table"
///         or type(tbParam.shopid) ~= "number"
///         or type(tbParam.isopen) ~= "number"
///         or type(tbParam.refreshcount) ~= "number"
///         or type(tbParam.goodslist) ~= "table" then
///     return
/// </code>
/// 缺任一 ⇒ **静默 return**：没有报错、界面完全不刷新、服务端日志一切正常。
/// 其中 <c>isopen</c> 除了这道门禁**全仓没有第二处读取**（只有 <c>:275</c>/<c>:332</c> 两行），
/// 但仍然必填。<c>goodslist</c> 给 <c>[]</c> 与省略语义不同：<c>[]</c> 是「空货架」，省略是「整包丢弃」。
///
/// <para><b>2. ★ 每个货架项都必须带 <c>discount</c>（否则 96% 的商品变免费）</b></para>
/// 见 <see cref="ShopTable"/> 类注释的「折扣」段：不给 <c>discount</c> 时客户端回落到
/// <c>DiscountList[1].discount</c> —— 20001/20002 变**一折**，其余 1816 件商品因为
/// 缺省值 <c>'[100,100]'</c> 被 <c>-</c> 分隔符解坏而变成 **0 折（免费）**。
///
/// <para><b>3. ★ 五个 <c>*buynum</c> 一律下发（客户端详情弹窗做 nil 算术）</b></para>
/// 列表页有 <c>or 0</c> 保护（<c>UI_ShopNew.lua:447..467</c>），但详情弹窗
/// <c>UI_ShopTips:_ShowLmtCtrl</c>（<c>:237..258</c>）是裸减：
/// <code>
/// self.MaxCanBuy = tbGoodsCfg.RefreshLimitNum - tbParam.refreshbuynum   -- nil ⇒ 报错
/// </code>
/// 本实现**无条件**下发 refresh/daily/week/total/shelf 五个计数，理由有三：
/// ①按需下发要精确复刻「限量列 ↔ RefreshRule」的 1:1 关系，多一处出错点；
/// ②多发不会改变客户端行为 —— <c>_getNextPriceIdx</c>(<c>GoodsLogicC.lua:157..173</c>)、
/// <c>IsInStock</c>(<c>:185..205</c>)、<c>_ShowLmtCtrl</c> 的分支**都由配置决定**，
/// 记录里多几个键不影响判定；
/// ③<c>ShopData:HaveNum</c>（<c>ShopData.lua:103</c>）拿 <c>totalbuynum</c> 显示咖啡馆的「已拥有」，
/// 而 40001 一个限量列都没配，按需下发就永远不发它。
/// ★ 反之，货架项里**绝不能出现 <c>buynum</c> 键** —— <c>_getNextPriceIdx</c> 的最后一档
/// <c>elseif mebuyrec.buynum then idx = mebuyrec.buynum + 1</c> 会因此改变取价档位。
///
/// <para><b>4. ★ <c>begintime</c> 必须与 <c>endtime</c> 成对出现</b></para>
/// <c>UI_ShopEntry:OnShowGoods</c>（<c>UI_ShopEntry.lua:96..105</c>）：
/// <code>
/// if not tbParam and tbParam.shopid ~= ... then return end     -- and 写错了，等于永不 return
/// if tbParam.endtime and tbParam.endtime ~= 0 then
///     local dval1 = GetTime() - tbParam.begintime or 0         -- or 绑在整个表达式上，没有保护
/// </code>
/// 第一行的 <c>and</c>（应为 <c>or</c>）让这段对**任何 shopid** 都会执行 ⇒
/// 只给 endtime 不给 begintime = 商店大厅必炸。本实现只在两者都配了时成对下发。
///
/// <para><b>5. 随机规则（<c>Group</c> / <c>Weight</c> / <c>DiscountList</c> 三列全是纯服务端列）</b></para>
/// <list type="number">
///   <item>**每个 <c>Group</c> 按 <c>Weight</c> 加权抽 1 件** —— 依据见
///     <see cref="GoodsRow.Group"/>：<c>ShopTabList.Columns</c> 恰好等于该店的 Group 数，
///     且 <c>pos</c> 在客户端的唯一用途是社团商店的「第几栏」。
///     不分组的店（现网 1643/1896 行没 Group，其 <c>Columns</c> 都是 999）= 全部上架、不随机。</item>
///   <item>**折扣按 <c>DiscountList</c> 加权随机**（20001 的权重和恰为 100）。</item>
///   <item>**价格是数组**，档位 = <see cref="PriceIndex"/>（<c>_getNextPriceIdx</c> 的移植）。</item>
/// </list>
/// 抽出来的货架**必须持久化**（否则每次开界面价格/商品都在变），存在
/// <c>Player.Extra["shop"]</c>；对「不分组且无折扣表」的店（如 40001 的 499 件家具）
/// 结果是确定的，不入存档、每次现算，省下几十 KB。
///
/// <para><b>6. 刷新（cmd 12）</b></para>
/// 费用 = <c>ShopTabList.MoneyType</c>（**空列缺省 3 钻石**）× <c>RefreshFee[min(refreshcount+1,#fee)]</c>，
/// 上限 <c>RefreshLimits</c>（<c>UI_ShopNew.lua:283..297</c> 客户端也用同一式自检）。
/// 现网只有 20001/20002 支持手动刷新（<c>15|30|60|90|120</c>、MoneyType=3、上限 5），
/// 其余店 <c>RefreshLimits</c> 空 ⇒ 0 ⇒ 客户端 <c>refreshcount(0) &gt;= 0</c> 就先弹
/// <c>ui.shop.RefreshOverLimit</c>，cmd 12 根本发不出来。
///
/// <para><b>7. 扣费：金币/钻石是「免费 + 付费」合并语义</b></para>
/// <c>MoneyLogic:GetMoneyValue</c>（<c>MoneyLogic.lua:302..312</c>）把
/// <c>Gold(2) + Pay_Gold(17)</c>、<c>Free_Diamond(3) + Pay_Diamond(12) + Pay_IOS_Diamond(18)</c>
/// 相加当余额；客户端的「买得起吗」判定（<c>UI_ShopTips.lua:270</c> 的 <c>MaxCanBuy</c>）
/// 用的就是它。服务端必须同口径，且**先扣免费后扣付费**（见 <see cref="TrySpendMoney"/>）。
/// </summary>
public sealed class ShopFeature : IGameFeature
{
    // ---------------- LuaCall 数字命令 id ----------------

    /// <summary>11 <c>Shop_ReqGoodsList</c>：请求商品列表（<c>LuaCall.lua:19</c>）。</summary>
    public const int CmdReqGoodsList = 11;

    /// <summary>12 <c>Shop_ReqRefreshGoodsList</c>：手动刷新商品列表（<c>:20</c>）。</summary>
    public const int CmdReqRefreshGoodsList = 12;

    /// <summary>13 <c>Shop_ReqBuyGoods</c>：购买（<c>:21</c>）。</summary>
    public const int CmdReqBuyGoods = 13;

    /// <summary>14 <c>Shop_ReqBuyFurList</c>：咖啡馆家具批量购买（<c>:22</c>），上行是**数组**。</summary>
    public const int CmdReqBuyFurList = 14;

    // ---------------- 下行提示通道（S2CCall.lua:89..99）----------------

    /// <summary><c>S2C.CallClientError{err}</c> → <c>ShowMsgByKey("ui.Error." .. err)</c>（<c>S2CCall.lua:89</c>）。</summary>
    public const string FnCallClientError = "CallClientError";

    /// <summary><c>S2C.CallClientTip</c> → <c>ShowMsgByKey(table.unpack(tbParam))</c>，
    /// 参数是**数组**（<c>S2CCall.lua:94</c>，上行侧封装见 <c>LuaCall.lua:577..583</c>）。</summary>
    public const string FnCallClientTip = "CallClientTip";

    // ---------------- 客户端自带的提示 key（本地化表不在 extracted 里，只能复用客户端用过的）----------------

    /// <summary>商品 id 查不到（<c>UI_ShopNew.lua:301</c> / <c>:1073</c> 用的就是这个 key）。</summary>
    public const string TipBadGoodsId = "ui.shop.badgoodsId";

    /// <summary>商店 id 对不上/没有页签配置（<c>UI_ShopNew.lua:1073</c> / <c>:1080</c>）。</summary>
    public const string TipShopUnmatch = "ui.shop.shopId unmatch2";

    /// <summary>刷新次数已达上限（<c>UI_ShopNew.lua:285</c>）。</summary>
    public const string TipRefreshOverLimit = "ui.shop.RefreshOverLimit";

    /// <summary>货币不足（<c>MoneyLogic.lua:191</c> <c>Text("ui.MessageBox.NotSufficientFunds")</c>）。</summary>
    public const string TipNotEnoughMoney = "ui.MessageBox.NotSufficientFunds";

    /// <summary>购买次数已用尽 —— 客户端自己也用 20020 这个码（<c>UI_Exchange.lua:187</c>）。</summary>
    public const int ErrNoBuyTimes = 20020;

    /// <summary>cmd 11 专属的错误分支：<c>LuaCall.lua:677</c>
    /// <c>if tbParam.errorKey then ShowMsgByKey("ui." .. tbParam.errorKey)</c>
    /// —— 注意客户端会自己拼 <c>"ui."</c> 前缀，所以这里的值**不带** <c>ui.</c>。
    /// cmd 12/13/14 **没有**这个分支。</summary>
    public const string ErrorKeyShopUnmatch = "shop.shopId unmatch2";

    // ---------------- 货币「免费 + 付费」合并池（MoneyLogic.lua:12..28）----------------

    /// <summary>付费金币（<c>MoneyLogic.eMoneyType_Pay_Gold = 17</c>）。<see cref="MoneyType"/> 里没有，
    /// 而合并扣费必须用到，所以在本域声明（值必须与 <c>MoneyLogic.lua:27</c> 一致）。</summary>
    public const int MoneyPayGold = 17;

    /// <summary>iOS 付费钻石（<c>MoneyLogic.eMoneyType_Pay_IOS_Diamond = 18</c>，<c>MoneyLogic.lua:28</c>）。</summary>
    public const int MoneyPayIosDiamond = 18;

    /// <summary><see cref="Player.Extra"/> 里存商店状态的键。</summary>
    public const string ExtraKey = "shop";

    /// <summary>
    /// 每日/每周计数的换日时刻（当天 0 点起的秒数）。现网 <c>ShopTabList.RefreshTime</c>
    /// 全部以 <c>040000</c> 开头（20001 是 <c>040000|180000</c>），所以缺省取 04:00。
    /// 商店自己配了 <c>RefreshTime</c> 就用它的第一档。
    /// </summary>
    public const int DefaultRefreshSecondOfDay = 4 * 3600;

    /// <summary>单次购买数量上限（客户端 <c>UI_ShopTips</c> 的 <c>MaxCanBuy</c> 不限购时是 99）。</summary>
    public const int MaxBuyNum = 99;

    private GameFeatureContext _ctx = null!;
    private ShopTable _table = ShopTable.Empty;
    private CafeComfortSync _comfort = null!;
    private readonly Random _rng = new();

    public string Name => "商店（LuaCall 11/12/13/14）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _comfort = new CafeComfortSync(ctx);
        // 自己加载配置表：不往 GameFeatureContext 加属性，避免多 agent 并行时改到公共文件
        _table = ShopTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapLuaCall(CmdReqGoodsList, (s, c, p) => OnReqGoodsList(s, p));
        ctx.MapLuaCall(CmdReqRefreshGoodsList, (s, c, p) => OnReqRefreshGoodsList(s, p));
        ctx.MapLuaCall(CmdReqBuyGoods, (s, c, p) => OnReqBuyGoods(s, p));
        ctx.MapLuaCall(CmdReqBuyFurList, (s, c, p) => OnReqBuyFurList(s, p));
    }

    /// <summary>本功能加载到的配置（测试与日志用）。</summary>
    internal ShopTable Table => _table;

    // =====================================================================
    //  cmd 11 Shop_ReqGoodsList
    // =====================================================================

    private Task OnReqGoodsList(ClientSession session, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Shop_ReqGoodsList 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        int shopId = ReadInt(param?["shopid"], 0);
        long now = Now();

        if (!KnowsShop(shopId))
        {
            // cmd 11 是唯一有 errorKey 分支的命令（LuaCall.lua:677），用它比静默丢包好排查
            Log.Warn($"[session {session.Id}] Shop_ReqGoodsList shopid={shopId} 在 " +
                     $"{ShopTable.ShopFileName}/{ShopTable.GoodsFileName} 里都不存在 → errorKey");
            Reply(session, CmdReqGoodsList, new JsonObject { ["errorKey"] = ErrorKeyShopUnmatch });
            return Task.CompletedTask;
        }

        var shelf = EnsureShelf(player, shopId, now, out bool rerolled);
        if (rerolled)
            _ctx.Store.Save(player);

        var reply = BuildListReply(player, shopId, shelf, now);
        Reply(session, CmdReqGoodsList, reply);

        Log.Info($"[session {session.Id}] Shop_ReqGoodsList shopid={shopId}" +
                 $"（{DescribeShop(shopId)}）→ {shelf.Count} 件" +
                 $"，refreshcount={GetRefreshCount(player, shopId)}" +
                 (rerolled ? "，货架已按刷新周期重抽" : "") +
                 $"；{DescribeShelf(shelf)}");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  cmd 12 Shop_ReqRefreshGoodsList
    // =====================================================================

    private Task OnReqRefreshGoodsList(ClientSession session, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Shop_ReqRefreshGoodsList 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        int shopId = ReadInt(param?["shopid"], 0);
        long now = Now();

        var shop = _table.GetShop(shopId);
        if (shop is null)
        {
            Log.Warn($"[session {session.Id}] Shop_ReqRefreshGoodsList shopid={shopId} 没有页签配置 → 提示");
            Tip(session, TipShopUnmatch);
            return Task.CompletedTask;
        }

        // 先把「自动刷新周期」结算掉，再判手动次数（否则跨了 4 点还会拿旧的 refreshcount）
        EnsureShelf(player, shopId, now, out _);
        int used = GetRefreshCount(player, shopId);

        if (!shop.CanManualRefresh || used >= shop.RefreshLimits)
        {
            // 客户端自己也会先拦一道（UI_ShopNew.lua:284），走到这里说明是改包或跨周期竞态
            Log.Warn($"[session {session.Id}] Shop_ReqRefreshGoodsList shopid={shopId} 拒绝：" +
                     $"已刷 {used} 次 / 上限 {shop.RefreshLimits}" +
                     (shop.RefreshFee.Length == 0 ? "（该店没配 RefreshFee）" : ""));
            Tip(session, TipRefreshOverLimit);
            return Task.CompletedTask;
        }

        int fee = shop.RefreshFeeAt(used);
        var changedMoney = new List<int>();
        if (fee > 0 && !TrySpendMoney(player, shop.MoneyType, fee, changedMoney))
        {
            Log.Warn($"[session {session.Id}] Shop_ReqRefreshGoodsList shopid={shopId} 拒绝：" +
                     $"刷新费 {MoneyType.NameOf(shop.MoneyType)}×{fee} 不足" +
                     $"（持有 {PoolValue(player, shop.MoneyType)}）");
            Tip(session, TipNotEnoughMoney);
            return Task.CompletedTask;
        }

        // 重抽货架 + 手动次数 +1 + 清「单次刷新限量」计数
        var state = ShopStateOf(player, shopId);
        var shelf = RollShelf(shopId, now);
        WriteShelf(state, shopId, shelf);
        state["rc"] = used + 1;
        state["rb"] = new JsonObject();
        _ctx.Store.Save(player);

        if (changedMoney.Count > 0)
            GameNotify.PushMoney(session, player, changedMoney);

        var reply = BuildListReply(player, shopId, shelf, now);
        Reply(session, CmdReqRefreshGoodsList, reply);

        Log.Info($"[session {session.Id}] Shop_ReqRefreshGoodsList shopid={shopId} 第 {used + 1} 次" +
                 $"（上限 {shop.RefreshLimits}）扣 {MoneyType.NameOf(shop.MoneyType)}×{fee}" +
                 $" → {shelf.Count} 件；{DescribeShelf(shelf)}");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  cmd 13 Shop_ReqBuyGoods
    // =====================================================================

    private Task OnReqBuyGoods(ClientSession session, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Shop_ReqBuyGoods 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        int shopId = ReadInt(param?["shopid"], 0);
        int goodsId = ReadInt(param?["goodsid"], 0);
        int buyNum = ReadInt(param?["buynum"], 1);

        var result = TryBuy(session, player, shopId, goodsId, buyNum, out var failTip, out int failErr);
        if (result is null)
        {
            if (failErr > 0)
                Error(session, failErr);
            else
                Tip(session, failTip ?? TipBadGoodsId);
            return Task.CompletedTask;
        }

        bool comfortChanged = result.Granted.ChangedFurniture.Count > 0 && _comfort.Refresh(session, player);
        _ctx.Store.Save(player);
        PushBuyResult(session, player, result);
        _comfort.Push(session, player, comfortChanged);

        // ★ 回包必须回显请求三要素（GoodsLogicC:OnReqBuyGoods）：
        //   goodsid —— :393 拿它查配置，查不到就静默跳过奖励弹窗；
        //   buynum  —— :396 (GoodsNum or 1) * tbParam.buynum，**无 nil 保护**；
        //   shopid  —— :385/:388 判普通商店/活动商店（ShopData:IsNoramShop 有 type 保护，但
        //              :400 CheckGirlCardShop(goodsCfg.ShopId) 与 :419 的重拉都依赖它）。
        // 奖励弹窗内容由客户端自己按配置算（:395..396），所以回包不需要带物品。
        // 也**不能**带 nError —— 本族是 Lua 真值判断（AGENTS.md §5）。
        Reply(session, CmdReqBuyGoods, new JsonObject
        {
            ["shopid"] = shopId,
            ["goodsid"] = goodsId,
            ["buynum"] = result.BuyNum,
        });

        Log.Info($"[session {session.Id}] Shop_ReqBuyGoods shopid={shopId} goodsid={goodsId} " +
                 $"buynum={result.BuyNum} → {result.Describe()}");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  cmd 14 Shop_ReqBuyFurList（咖啡馆按主题一键买齐）
    // =====================================================================

    /// <summary>
    /// 上行是**数组** <c>[{shopid,goodsid,buynum},…]</c>（<c>UI_NCafeShop.lua:155..168</c> 逐件拼进去，
    /// <c>buynum</c> 恒为 1）。
    ///
    /// 回包形状取自客户端唯一的消费者 <c>UI_NCafeShop:UpdateThemeShopWhenReturn</c>
    /// （<c>UI_NCafeShop.lua:303..337</c>）：
    /// <code>
    /// for k,v in pairs(tbParam) do
    ///     if v.ret then                                  -- ★ Lua 真值：false/nil 都算失败
    ///         local tb = GoodsLogicC:GetGoodsCfg(v.goodsid)
    ///         ...累加 tb.GoodsNum 并 UI.Open('UI_RewardNormal', tbItems)
    /// </code>
    /// ⇒ 每项只需要 <c>goodsid</c> + <c>ret</c>；<c>ret</c> 给 <c>false</c> 就是「这件没买成」。
    /// <c>tbParam</c> 为空数组时客户端 <c>:304 CountTB(tbParam) == 0</c> 直接 return（安全）。
    /// 这里额外回显 <c>shopid</c>/<c>buynum</c> 只为服务端日志与单测好读，客户端不读。
    /// </summary>
    private Task OnReqBuyFurList(ClientSession session, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Shop_ReqBuyFurList 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        if (param is not JsonArray requests)
        {
            Log.Warn($"[session {session.Id}] Shop_ReqBuyFurList 的 tbParam 不是数组：" +
                     $"{param?.ToJsonString() ?? "nil"}，忽略");
            return Task.CompletedTask;
        }

        var reply = new JsonArray();
        var merged = new BuyOutcome();
        int ok = 0;

        foreach (var node in requests)
        {
            int shopId = ReadInt(node?["shopid"], 0);
            int goodsId = ReadInt(node?["goodsid"], 0);
            int buyNum = ReadInt(node?["buynum"], 1);

            var one = TryBuy(session, player, shopId, goodsId, buyNum, out var failTip, out int failErr);
            bool success = one is not null;
            if (success)
            {
                merged.Absorb(one!);
                ok++;
            }
            else
            {
                Log.Warn($"[session {session.Id}] Shop_ReqBuyFurList 第 goodsid={goodsId} 件失败" +
                         $"（{failTip ?? ("err" + failErr)}）");
            }

            reply.Add(new JsonObject
            {
                ["shopid"] = shopId,
                ["goodsid"] = goodsId,
                ["buynum"] = success ? one!.BuyNum : buyNum,
                ["ret"] = success,      // ★ Lua 真值判断：false 会被 :312 的 if v.ret 跳过
            });
        }

        if (ok > 0)
        {
            bool comfortChanged = merged.Granted.ChangedFurniture.Count > 0 && _comfort.Refresh(session, player);
            _ctx.Store.Save(player);
            PushBuyResult(session, player, merged);
            _comfort.Push(session, player, comfortChanged);
        }

        Reply(session, CmdReqBuyFurList, reply);
        Log.Info($"[session {session.Id}] Shop_ReqBuyFurList {requests.Count} 件请求 → 成功 {ok} 件；" +
                 $"{merged.Describe()}");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  购买核心（cmd 13 与 cmd 14 共用）
    // =====================================================================

    /// <summary>一次成功购买的产物（用于推送与日志）。</summary>
    internal sealed class BuyOutcome
    {
        public int BuyNum { get; set; }
        public int TotalPrice { get; set; }
        public int MoneyId { get; set; }
        public List<int> ChangedMoney { get; } = new();
        public RewardGrantResult Granted { get; } = new();
        private readonly List<string> _parts = new();

        public void Note(string part) => _parts.Add(part);

        public void Absorb(BuyOutcome other)
        {
            BuyNum += other.BuyNum;
            TotalPrice += other.TotalPrice;
            foreach (int id in other.ChangedMoney)
            {
                if (!ChangedMoney.Contains(id))
                    ChangedMoney.Add(id);
            }
            _parts.AddRange(other._parts);
            Granted.Absorb(other.Granted);
        }

        public string Describe()
            => _parts.Count == 0 ? "（无）" : string.Join("；", _parts);
    }

    /// <summary>
    /// 走完一次购买的全部校验与结算。成功返回产物；失败返回 null 并给出提示
    /// （<paramref name="errCode"/> &gt; 0 时用 <c>CallClientError</c>，否则用 <c>CallClientTip</c>）。
    /// **不存盘、不推送**，由调用方统一做（cmd 14 要合并成一次）。
    /// </summary>
    private BuyOutcome? TryBuy(ClientSession session, Player player, int shopId, int goodsId, int buyNum,
                               out string? tipKey, out int errCode)
    {
        tipKey = null;
        errCode = 0;
        long now = Now();

        var goods = _table.GetGoods(goodsId);
        if (goods is null)
        {
            Log.Warn($"[session {session.Id}] 购买失败：goodsid={goodsId} 不在 {ShopTable.GoodsFileName} 里");
            tipKey = TipBadGoodsId;
            return null;
        }
        if (goods.ShopId != shopId)
        {
            Log.Warn($"[session {session.Id}] 购买失败：goodsid={goodsId} 属于商店 {goods.ShopId}，" +
                     $"但请求写的是 {shopId}");
            tipKey = TipShopUnmatch;
            return null;
        }

        var shop = _table.GetShop(shopId);
        if (shop is not null && !shop.IsOpenAt(now))
        {
            Log.Warn($"[session {session.Id}] 购买失败：商店 {shopId} 不在开放期" +
                     $"（{shop.BeginTime}..{shop.EndTime}，now={now}）");
            tipKey = TipShopUnmatch;
            return null;
        }
        if (!goods.IsInTimeAt(now))
        {
            Log.Warn($"[session {session.Id}] 购买失败：goodsid={goodsId} 不在上架期" +
                     $"（{goods.BeginTime}..{goods.EndTime}，now={now}）");
            tipKey = TipBadGoodsId;
            return null;
        }
        if (buyNum <= 0 || buyNum > MaxBuyNum)
        {
            Log.Warn($"[session {session.Id}] 购买失败：buynum={buyNum} 不在 1..{MaxBuyNum} 内");
            tipKey = TipBadGoodsId;
            return null;
        }

        // ★ 只能买「当前货架上」的东西：分组商店每期只上架每组 1 件，不然玩家可以买全表
        var shelf = EnsureShelf(player, shopId, now, out bool rerolled);
        if (rerolled)
            _ctx.Store.Save(player);        // 恰好跨了刷新周期：先把新货架落盘，别让下一次请求又抽一份
        var entry = shelf.FirstOrDefault(e => e.GoodsId == goodsId);
        if (entry is null)
        {
            Log.Warn($"[session {session.Id}] 购买失败：goodsid={goodsId} 不在商店 {shopId} 本期货架上" +
                     $"（货架 {shelf.Count} 件）");
            tipKey = TipBadGoodsId;
            return null;
        }

        // 限量：口径与客户端 GoodsLogicC:IsInStock（GoodsLogicC.lua:185..205）一致
        int bought = GetBuyCount(player, shopId, goods.LimitKind, goodsId);
        int? limit = goods.LimitNum;
        if (limit.HasValue && bought + buyNum > limit.Value)
        {
            Log.Warn($"[session {session.Id}] 购买失败：goodsid={goodsId} 限量 {goods.LimitKind}=" +
                     $"{limit.Value}，已买 {bought}，本次 {buyNum}");
            errCode = ErrNoBuyTimes;
            return null;
        }

        // 价格：档位与折扣都必须与客户端算的一致，否则「显示 20 实扣 80」
        int priceIdx = PriceIndex(goods, player, shopId, goodsId);
        int unit = goods.UnitPrice(priceIdx, entry.Discount);
        int total = unit * buyNum;

        long grantCount = (long)goods.GoodsNum * buyNum;
        if (grantCount <= 0 || grantCount > int.MaxValue
            || !_ctx.Rewards.CanReceiveFurniture(player, (uint)goods.G, (uint)goods.D,
                (uint)goods.P, (uint)goods.L, (int)grantCount))
        {
            Log.Warn($"[session {session.Id}] Furniture purchase exceeds ownership/storage capacity or has invalid configuration");
            tipKey = TipBadGoodsId;
            return null;
        }

        var outcome = new BuyOutcome { BuyNum = buyNum, TotalPrice = total, MoneyId = goods.MoneyType };
        if (total > 0 && !TrySpendMoney(player, goods.MoneyType, total, outcome.ChangedMoney))
        {
            Log.Warn($"[session {session.Id}] 购买失败：goodsid={goodsId} 需 " +
                     $"{MoneyType.NameOf(goods.MoneyType)}×{total}" +
                     $"（{unit}/件 × {buyNum}，第 {priceIdx} 档价 {goods.PriceAt(priceIdx)} × {entry.Discount}%）" +
                     $"，持有 {PoolValue(player, goods.MoneyType)}");
            tipKey = TipNotEnoughMoney;
            return null;
        }

        // 发货：数量 = GoodsNum * buynum（与客户端奖励弹窗 GoodsLogicC.lua:396 同式）
        int count = goods.GoodsNum * buyNum;
        _ctx.Rewards.Grant(player, new[] { new[] { goods.G, goods.D, goods.P, goods.L, count } },
                           RewardSource.Extra, outcome.Granted);

        // ★ 武器(2)/符文(3)/零件(4) 的 skillinfo/weaponinlay/attrlist/ownguid 服务端还不会填，
        //   下发后客户端会「技能清零 / 符文卸下」（docs/05 §0.3）。20005/20006 卖符文，会踩到。
        if (goods.G is >= 2 and <= 4)
        {
            Log.Warn($"[session {session.Id}] goodsid={goodsId} 是 genre={goods.G}" +
                     "（武器/符文/零件），本服务端还不会下发 skillinfo/weaponinlay/attrlist/ownguid，" +
                     "客户端表现可能异常（docs/05 §0.3）");
        }

        // 计数：只累加本商品生效的那一种限量口径 + 永久的 totalbuynum（咖啡馆「已拥有」要用）
        AddBuyCount(player, shopId, goods.LimitKind, goodsId, buyNum);
        if (goods.LimitKind != ShopLimitKind.Total)
            AddBuyCount(player, shopId, ShopLimitKind.Total, goodsId, buyNum);

        outcome.Note($"{goods.Name}({goods.G},{goods.D},{goods.P},{goods.L})×{count} " +
                     $"花 {MoneyType.NameOf(goods.MoneyType)}×{total}");
        return outcome;
    }

    /// <summary>把一次（或一批）购买的结果推给客户端：先 pf 1032 货币，再 pf 1031 背包。</summary>
    private static void PushBuyResult(ClientSession session, Player player, BuyOutcome outcome)
    {
        if (outcome.ChangedMoney.Count > 0)
            GameNotify.PushMoney(session, player, outcome.ChangedMoney);
        GameNotify.PushRewards(session, player, outcome.Granted);
    }

    // =====================================================================
    //  回包构造
    // =====================================================================

    /// <summary>
    /// 组 cmd 11 / cmd 12 的回包。两条命令的客户端门禁与处理函数形状完全相同
    /// （<c>GoodsLogicC.lua:271</c> vs <c>:327</c>），所以共用。
    /// </summary>
    internal JsonObject BuildListReply(Player player, int shopId, List<ShelfEntry> shelf, long now)
    {
        var shop = _table.GetShop(shopId);
        var state = ShopStateOf(player, shopId);      // 只取一次：40001 有 499 件，别在循环里反复查
        var list = new JsonArray();

        var furnitureCounts = shop?.IsCafeShop == true && _ctx.Rewards.Furniture is { } furnitureTable
            ? HouseFeature.ReadOrCreate(player, furnitureTable).Furniture.GroupBy(f => f.TemplateId)
                .ToDictionary(g => g.Key, g => g.Count()) : null;

        foreach (var e in shelf)
        {
            var goods = _table.GetGoods(e.GoodsId);
            if (goods is null || !goods.IsInTimeAt(now))
                continue;   // 货架是上一期抽的，期间可能已下架（客户端 UI_ShopNew.lua:1101 也会再筛一次）

            var item = new JsonObject
            {
                ["goodsid"] = e.GoodsId,
                // ★ 必给：不给会被回落成 DiscountList[1]（20001 = 一折）或 0 折（免费）
                ["discount"] = e.Discount,
                // ★ 五个计数一律给（详情弹窗 UI_ShopTips:_ShowLmtCtrl 做裸减法），
                //   但**不要**给 buynum —— 那会改变 _getNextPriceIdx 的取价档位
                ["refreshbuynum"] = CountIn(state, ShopLimitKind.Refresh, e.GoodsId),
                ["dailybuynum"] = CountIn(state, ShopLimitKind.Daily, e.GoodsId),
                ["weekbuynum"] = CountIn(state, ShopLimitKind.Week, e.GoodsId),
                ["totalbuynum"] = CountIn(state, ShopLimitKind.Total, e.GoodsId),
                ["shelfbuynum"] = CountIn(state, ShopLimitKind.Shelf, e.GoodsId),
            };
            if (furnitureCounts is not null
                && _ctx.Rewards.IsCafeFurniture((uint)goods.G, (uint)goods.D, (uint)goods.P, (uint)goods.L)
                && int.TryParse(_ctx.Rewards.Templates.Get((uint)goods.G, (uint)goods.D,
                    (uint)goods.P, (uint)goods.L)?.ExtParam1, out int furnitureId))
                item["totalbuynum"] = furnitureCounts.GetValueOrDefault(furnitureId);
            // pos = Group 号。只有社团商店 20006 会读它（GoodsLogicC.lua:227..241），
            // 但那里的默认值是「锁住」：pos 为 nil 时 bClublock 保持 true，整店只显示 1 件锁着的商品。
            if (e.Pos.HasValue)
                item["pos"] = e.Pos.Value;
            list.Add(item);
        }

        var reply = new JsonObject
        {
            // ★★ 四个门禁字段，缺任一 → GoodsLogicC.lua:273..279 静默 return
            ["shopid"] = shopId,
            ["isopen"] = shop is null || shop.IsOpenAt(now) ? 1 : 0,
            ["refreshcount"] = ReadInt(state["rc"], 0),
            ["goodslist"] = list,
        };

        // nextrefreshtime：到点客户端自己重拉一次（UI_ShopNew.lua:1035..1040）。可选，nil 安全。
        long next = NextAutoRefresh(shop, now);
        if (next > 0)
            reply["nextrefreshtime"] = next;

        // ★ begintime / endtime 必须成对（UI_ShopEntry.lua:105 对 begintime 做裸减法）。
        //   现网 9 个永久店两列都空 ⇒ 两个都不下发，正好避开那段坑。
        if (shop is not null && shop.HasTimeWindow)
        {
            reply["begintime"] = shop.BeginTime;
            reply["endtime"] = shop.EndTime;
        }

        // 不下发 refreshtype：它只影响滚动条（<c>== "PosFixed"</c> 时 RefreshCells 保位置、
        // 否则 RefillCells 回到顶部，UI_ShopNew.lua:1188..1192）。手动刷新本来就该回顶部，
        // 而「买完自动重拉」那条路径（GoodsLogicC.lua:421）是咖啡馆界面，不看这个字段。
        return reply;
    }

    // =====================================================================
    //  货架：生成 / 持久化 / 周期滚动
    // =====================================================================

    /// <summary>货架上的一件商品。</summary>
    /// <param name="GoodsId">商品 id。</param>
    /// <param name="Pos">Group 号（= 客户端的 <c>pos</c>）；不分组的店为 null。</param>
    /// <param name="Discount">本期折扣百分比（100 = 原价）。</param>
    internal sealed record ShelfEntry(int GoodsId, int? Pos, int Discount);

    /// <summary>
    /// 取该店本期货架，必要时按刷新周期重抽。
    /// <paramref name="rerolled"/> 为 true 表示本次动了存档（调用方要 Save）。
    /// </summary>
    internal List<ShelfEntry> EnsureShelf(Player player, int shopId, long now, out bool rerolled)
    {
        rerolled = false;
        var shop = _table.GetShop(shopId);
        var state = ShopStateOf(player, shopId);

        // ---- 每日 / 每周计数的换期（与货架刷新周期无关，独立结算）----
        int boundary = shop is { RefreshSecondsOfDay.Length: > 0 }
            ? shop.RefreshSecondsOfDay[0] : DefaultRefreshSecondOfDay;

        string dayKey = DayBoundaryBefore(now, boundary).ToString();
        if (ReadString(state["dk"]) != dayKey)
        {
            state["dk"] = dayKey;
            state["db"] = new JsonObject();
            rerolled = true;
        }
        string weekKey = WeekBoundaryBefore(now, boundary).ToString();
        if (ReadString(state["wk"]) != weekKey)
        {
            state["wk"] = weekKey;
            state["wb"] = new JsonObject();
            rerolled = true;
        }

        // ---- 货架本身 ----
        bool persist = NeedsPersistentShelf(shopId);
        string periodKey = AutoPeriodKey(shop, now).ToString();

        if (!persist)
        {
            // 不分组且无折扣表 ⇒ 结果完全确定（全部在架商品、折扣 100），不入存档
            return RollShelf(shopId, now);
        }

        var stored = ReadShelf(state);
        if (stored is null || ReadString(state["pk"]) != periodKey)
        {
            var fresh = RollShelf(shopId, now);
            WriteShelf(state, shopId, fresh);
            state["pk"] = periodKey;
            // 换期 ⇒ 手动刷新次数与「单次刷新限量」计数一起清零
            state["rc"] = 0;
            state["rb"] = new JsonObject();
            rerolled = true;
            return fresh;
        }
        return stored;
    }

    /// <summary>
    /// 抽一期货架。
    /// <list type="bullet">
    ///   <item><b>分组商店</b>：每个 Group 在「本组当前在架的商品」里按 <see cref="GoodsRow.Weight"/>
    ///     加权抽 1 件（组号升序，保证顺序可复现）。</item>
    ///   <item><b>不分组商店</b>：全部在架商品按文件顺序上架（<c>Columns == 999</c> 的语义）。</item>
    /// </list>
    /// 折扣按 <see cref="GoodsRow.DiscountList"/> 加权随机；没配折扣表的一律 100（原价）。
    /// </summary>
    internal List<ShelfEntry> RollShelf(int shopId, long now)
    {
        var result = new List<ShelfEntry>();

        // 不在开放期的商店一律空货架 —— 与 TryBuy 的拒绝条件保持一致，
        // 也让商店大厅（UI_ShopEntry 请求的就是 3000x 活动店）显示「未开放」而不是一堆买不了的货。
        var shop = _table.GetShop(shopId);
        if (shop is not null && !shop.IsOpenAt(now))
            return result;

        var all = _table.GoodsOf(shopId).Where(g => g.IsInTimeAt(now)).ToList();
        if (all.Count == 0)
            return result;

        if (!_table.IsGroupedShop(shopId))
        {
            foreach (var g in all)
                result.Add(new ShelfEntry(g.GoodsId, g.Group, RollDiscount(g)));
            return result;
        }

        foreach (int group in all.Where(g => g.Group.HasValue).Select(g => g.Group!.Value)
                                 .Distinct().OrderBy(v => v))
        {
            var candidates = all.Where(g => g.Group == group).ToList();
            var picked = PickWeighted(candidates);
            if (picked is not null)
                result.Add(new ShelfEntry(picked.GoodsId, group, RollDiscount(picked)));
        }

        // 没配 Group 的散货照样上架（现网不存在「同店部分配 Group」的情况，纯防御）
        foreach (var g in all.Where(g => !g.Group.HasValue))
            result.Add(new ShelfEntry(g.GoodsId, null, RollDiscount(g)));

        return result;
    }

    /// <summary>按 <see cref="GoodsRow.Weight"/> 加权抽一件；权重全 ≤ 0 时退化为均匀随机。</summary>
    private GoodsRow? PickWeighted(List<GoodsRow> candidates)
    {
        if (candidates.Count == 0)
            return null;
        int sum = candidates.Sum(c => Math.Max(0, c.Weight));
        if (sum <= 0)
            return candidates[_rng.Next(candidates.Count)];

        int roll = _rng.Next(sum);
        int acc = 0;
        foreach (var c in candidates)
        {
            acc += Math.Max(0, c.Weight);
            if (roll < acc)
                return c;
        }
        return candidates[^1];
    }

    /// <summary>
    /// 按 <c>DiscountList</c> 的权重抽一档折扣。**没配折扣表就返回 100（原价）** ——
    /// 绝不能照客户端那个坏掉的 <c>'[100,100]'</c> 缺省值（会算出 0 折）。
    /// </summary>
    internal int RollDiscount(GoodsRow goods)
    {
        var steps = goods.DiscountList;
        if (steps.Count == 0)
            return 100;

        int sum = steps.Sum(s => Math.Max(0, s.Weight));
        if (sum <= 0)
            return Math.Clamp(steps[0].Discount, 0, 100);

        int roll = _rng.Next(sum);
        int acc = 0;
        foreach (var s in steps)
        {
            acc += Math.Max(0, s.Weight);
            if (roll < acc)
                return Math.Clamp(s.Discount, 0, 100);
        }
        return Math.Clamp(steps[^1].Discount, 0, 100);
    }

    /// <summary>
    /// 该店的货架是否需要写进存档：**分组**（要记住抽中了哪几件）或
    /// **有折扣表**（要记住抽中了几折）时才需要。
    /// 40001 那种 499 件、不分组、无折扣的店每次现算即可，能省下几十 KB 存档。
    /// </summary>
    internal bool NeedsPersistentShelf(int shopId)
        => _table.IsGroupedShop(shopId) || _table.HasDiscountShop(shopId);

    // =====================================================================
    //  刷新周期
    // =====================================================================

    /// <summary>
    /// 该店「自动刷新周期」的标识（用最近一次已过的刷新时刻的 unix 秒当 key）。
    /// 0 表示该店不自动刷新（<c>RefreshRule</c> 4 活动期 / 5 货架期，或没配规则）。
    ///
    /// <c>RefreshRule</c> 与刷新节奏的对应关系来自 <see cref="ShopLimitKind"/> 的 1:1 绑定：
    /// 1 = 一天多次（<c>RefreshTime</c> 列出全部时刻，20001 是 <c>040000|180000</c>）、
    /// 2 = 每日一次、3 = 每周一次。
    /// ★ 「每周从周一算」在 lua 里**没有**取证（<c>RefreshRule=3</c> 的四家店 <c>RefreshTime</c> 都是空列），
    /// 这是本实现的选择，见 <see cref="WeekBoundaryBefore"/>。
    /// </summary>
    internal static long AutoPeriodKey(ShopTabRow? shop, long now)
    {
        if (shop is null)
            return 0;
        int[] clocks = shop.RefreshSecondsOfDay.Length > 0
            ? shop.RefreshSecondsOfDay
            : new[] { DefaultRefreshSecondOfDay };

        return shop.RefreshRule switch
        {
            1 => LastBoundary(now, clocks),
            2 => DayBoundaryBefore(now, clocks[0]),
            3 => WeekBoundaryBefore(now, clocks[0]),
            _ => 0,
        };
    }

    /// <summary>下一次自动刷新的 unix 秒；不自动刷新返回 0（回包就不带 <c>nextrefreshtime</c>）。</summary>
    internal static long NextAutoRefresh(ShopTabRow? shop, long now)
    {
        if (shop is null)
            return 0;
        int[] clocks = shop.RefreshSecondsOfDay.Length > 0
            ? shop.RefreshSecondsOfDay
            : new[] { DefaultRefreshSecondOfDay };

        return shop.RefreshRule switch
        {
            1 => NextBoundary(now, clocks),
            2 => DayBoundaryBefore(now, clocks[0]) + 86400,
            3 => WeekBoundaryBefore(now, clocks[0]) + 7 * 86400,
            _ => 0,
        };
    }

    /// <summary>「最近一次已过的每日 <paramref name="secondOfDay"/> 时刻」的 unix 秒。</summary>
    internal static long DayBoundaryBefore(long now, int secondOfDay)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime;
        var boundary = utc.Date.AddSeconds(secondOfDay);
        if (utc < boundary)
            boundary = boundary.AddDays(-1);
        return new DateTimeOffset(boundary, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    /// <summary>
    /// 「最近一次已过的『周一 <paramref name="secondOfDay"/>』」的 unix 秒。
    /// ★ 周界取周一是本实现的选择（配置里没有任何一列指明），只影响
    /// <c>WeekLimitNum</c> 的清零时刻；改这里不影响协议形状。
    /// </summary>
    internal static long WeekBoundaryBefore(long now, int secondOfDay)
    {
        long day = DayBoundaryBefore(now, secondOfDay);
        var utc = DateTimeOffset.FromUnixTimeSeconds(day).UtcDateTime;
        int back = ((int)utc.DayOfWeek + 6) % 7;    // 周一 → 0，周日 → 6
        return day - back * 86400L;
    }

    /// <summary>一天有多个刷新时刻时，取最近一次已过的那个。</summary>
    private static long LastBoundary(long now, int[] clocks)
    {
        long best = long.MinValue;
        foreach (int c in clocks)
        {
            long b = DayBoundaryBefore(now, c);
            if (b > best)
                best = b;
        }
        return best == long.MinValue ? 0 : best;
    }

    /// <summary>
    /// 一天有多个刷新时刻时，取下一次未到的那个（如 20001 的 <c>040000|180000</c>：
    /// 现在 10:00 ⇒ 今天 18:00；现在 20:00 ⇒ 明天 04:00）。
    /// </summary>
    private static long NextBoundary(long now, int[] clocks)
    {
        var utc = DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime;
        long best = long.MaxValue;
        foreach (int c in clocks)
        {
            // 今天、明天各算一个候选，取第一个严格大于 now 的
            for (int dayOffset = 0; dayOffset <= 1; dayOffset++)
            {
                long candidate = new DateTimeOffset(utc.Date.AddDays(dayOffset).AddSeconds(c), TimeSpan.Zero)
                    .ToUnixTimeSeconds();
                if (candidate > now && candidate < best)
                    best = candidate;
            }
        }
        return best == long.MaxValue ? 0 : best;
    }

    // =====================================================================
    //  取价档位（GoodsLogicC:_getNextPriceIdx 的移植）
    // =====================================================================

    /// <summary>
    /// 取价档位（1 起）。**逐字移植** <c>GoodsLogicC:_getNextPriceIdx</c>
    /// （<c>GoodsLogicC.lua:157..173</c>）：
    /// <code>
    /// if     mebuyrec.shelfbuynum   and cfg.ShelfLifeLmt    then idx = shelfbuynum   + 1
    /// elseif mebuyrec.totalbuynum   and cfg.TotalLimitNum   then idx = totalbuynum   + 1
    /// elseif mebuyrec.weekbuynum    and cfg.WeekLimitNum    then idx = weekbuynum    + 1
    /// elseif mebuyrec.dailybuynum   and cfg.DailyLimitNum   then idx = dailybuynum   + 1
    /// elseif mebuyrec.refreshbuynum and cfg.RefreshLimitNum then idx = refreshbuynum + 1
    /// elseif mebuyrec.buynum then idx = buynum + 1 end
    /// return math.min(idx, #cfg.SellingPrice)
    /// </code>
    /// 因为本实现**五个计数全下发**，客户端那五个 <c>and</c> 只由配置决定 ⇒
    /// 这里只判配置就与客户端完全同步。最后一档 <c>mebuyrec.buynum</c> 永不命中
    /// （货架项里不放 <c>buynum</c> 键），所以此处也不实现它。
    /// 夹到价格数组长度由 <see cref="GoodsRow.PriceAt"/> 完成。
    /// </summary>
    internal int PriceIndex(GoodsRow goods, Player player, int shopId, int goodsId)
    {
        if (goods.ShelfLifeLmt.HasValue)
            return GetBuyCount(player, shopId, ShopLimitKind.Shelf, goodsId) + 1;
        if (goods.TotalLimitNum.HasValue)
            return GetBuyCount(player, shopId, ShopLimitKind.Total, goodsId) + 1;
        if (goods.WeekLimitNum.HasValue)
            return GetBuyCount(player, shopId, ShopLimitKind.Week, goodsId) + 1;
        if (goods.DailyLimitNum.HasValue)
            return GetBuyCount(player, shopId, ShopLimitKind.Daily, goodsId) + 1;
        if (goods.RefreshLimitNum.HasValue)
            return GetBuyCount(player, shopId, ShopLimitKind.Refresh, goodsId) + 1;
        return 1;
    }

    // =====================================================================
    //  扣费（MoneyLogic:GetMoneyValue 的合并语义）
    // =====================================================================

    /// <summary>
    /// 某种货币的「可用余额」。金币与钻石是**免费 + 付费合并**
    /// （<c>MoneyLogic.lua:302..312</c>）：
    /// <code>
    /// Gold(2)         → GetMoney(2)  + GetMoney(17 Pay_Gold)
    /// Free_Diamond(3) → GetMoney(3)  + GetMoney(12 Pay_Diamond) + GetMoney(18 Pay_IOS_Diamond)
    /// 其它            → GetMoney(id)
    /// </code>
    /// </summary>
    internal static int PoolValue(Player player, int moneyId)
    {
        int sum = 0;
        foreach (int id in PoolOf(moneyId))
            sum += player.GetMoney(id);
        return sum;
    }

    /// <summary>该货币的扣费顺序：**先免费、后付费**（数组顺序即扣费顺序）。</summary>
    internal static int[] PoolOf(int moneyId) => moneyId switch
    {
        MoneyType.Gold => new[] { MoneyType.Gold, MoneyPayGold },
        MoneyType.FreeDiamond => new[] { MoneyType.FreeDiamond, MoneyType.PayDiamond, MoneyPayIosDiamond },
        // 付费钻单独当货币用时（Price1 里确实有 12）仍然按合并池扣，与客户端
        // UseMoneyDialog 把 12 当 3 处理（MoneyLogic.lua:186..187）一致
        MoneyType.PayDiamond => new[] { MoneyType.FreeDiamond, MoneyType.PayDiamond, MoneyPayIosDiamond },
        _ => new[] { moneyId },
    };

    /// <summary>
    /// 扣钱。余额不足时**一分不动**返回 false。
    /// 成功时把动过的货币 id 追加进 <paramref name="changed"/>（每个要推一条 pf 1032）。
    /// </summary>
    internal static bool TrySpendMoney(Player player, int moneyId, int amount, List<int> changed)
    {
        if (amount <= 0)
            return true;
        if (PoolValue(player, moneyId) < amount)
            return false;

        int left = amount;
        foreach (int id in PoolOf(moneyId))
        {
            if (left <= 0)
                break;
            int have = player.GetMoney(id);
            if (have <= 0)
                continue;
            int take = Math.Min(have, left);
            player.SetMoney(id, have - take);
            left -= take;
            if (!changed.Contains(id))
                changed.Add(id);
        }
        return left == 0;
    }

    // =====================================================================
    //  存档状态（Player.Extra["shop"]）
    // =====================================================================

    /// <summary>取（并按需创建）某店的状态对象。键是 shopId 的十进制字符串。</summary>
    internal static JsonObject ShopStateOf(Player player, int shopId)
    {
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is not JsonObject root)
        {
            root = new JsonObject();
            player.Extra[ExtraKey] = root;
        }
        string key = shopId.ToString();
        if (root[key] is not JsonObject state)
        {
            state = new JsonObject();
            root[key] = state;
        }
        return state;
    }

    /// <summary>某店的手动刷新已用次数（回包的 <c>refreshcount</c>）。</summary>
    internal static int GetRefreshCount(Player player, int shopId)
        => ReadInt(ShopStateOf(player, shopId)["rc"], 0);

    /// <summary>限量口径 → 存档里的桶名。</summary>
    private static string BucketOf(ShopLimitKind kind) => kind switch
    {
        ShopLimitKind.Refresh => "rb",
        ShopLimitKind.Daily => "db",
        ShopLimitKind.Week => "wb",
        ShopLimitKind.Total => "tb",
        ShopLimitKind.Shelf => "sb",
        _ => "",
    };

    /// <summary>读某商品在某口径下的已购数量（没有记录返回 0）。</summary>
    internal static int GetBuyCount(Player player, int shopId, ShopLimitKind kind, int goodsId)
        => CountIn(ShopStateOf(player, shopId), kind, goodsId);

    /// <summary>在已取好的店状态里读计数（<see cref="BuildListReply"/> 的热路径用这个，避免反复取 state）。</summary>
    private static int CountIn(JsonObject state, ShopLimitKind kind, int goodsId)
    {
        string bucket = BucketOf(kind);
        if (bucket.Length == 0)
            return 0;
        return state[bucket] is JsonObject bag ? ReadInt(bag[goodsId.ToString()], 0) : 0;
    }

    /// <summary>累加某商品在某口径下的已购数量。</summary>
    internal static void AddBuyCount(Player player, int shopId, ShopLimitKind kind, int goodsId, int delta)
    {
        string bucket = BucketOf(kind);
        if (bucket.Length == 0 || delta == 0)
            return;
        var state = ShopStateOf(player, shopId);
        if (state[bucket] is not JsonObject bag)
        {
            bag = new JsonObject();
            state[bucket] = bag;
        }
        string key = goodsId.ToString();
        bag[key] = ReadInt(bag[key], 0) + delta;
    }

    /// <summary>把货架写进存档（紧凑三键：g=goodsid / p=pos / d=discount）。</summary>
    private static void WriteShelf(JsonObject state, int shopId, List<ShelfEntry> shelf)
    {
        var arr = new JsonArray();
        foreach (var e in shelf)
        {
            var one = new JsonObject { ["g"] = e.GoodsId, ["d"] = e.Discount };
            if (e.Pos.HasValue)
                one["p"] = e.Pos.Value;
            arr.Add(one);
        }
        state["shelf"] = arr;
    }

    /// <summary>从存档读货架；没有记录返回 null。</summary>
    private static List<ShelfEntry>? ReadShelf(JsonObject state)
    {
        if (state["shelf"] is not JsonArray arr)
            return null;
        var result = new List<ShelfEntry>(arr.Count);
        foreach (var node in arr)
        {
            if (node is not JsonObject one)
                continue;
            int goodsId = ReadInt(one["g"], 0);
            if (goodsId == 0)
                continue;
            int? pos = one["p"] is null ? null : ReadInt(one["p"], 0);
            result.Add(new ShelfEntry(goodsId, pos, ReadInt(one["d"], 100)));
        }
        return result;
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    /// <summary>该 shopid 服务端认不认（页签表或商品表里出现过就算认）。</summary>
    internal bool KnowsShop(int shopId)
        => shopId > 0 && (_table.GetShop(shopId) is not null || _table.GoodsOf(shopId).Count > 0);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Reply(ClientSession session, int cmd, JsonNode payload)
        => _ctx.LuaCalls.CallClient(session, cmd, payload);

    /// <summary>发一条 <c>S2C.CallClientTip</c>（参数是数组，见 <see cref="FnCallClientTip"/>）。</summary>
    private void Tip(ClientSession session, string key)
        => _ctx.LuaCalls.CallClientFunction(session, FnCallClientTip, new JsonArray { key });

    /// <summary>发一条 <c>S2C.CallClientError</c>（客户端会自己拼 <c>"ui.Error."</c> 前缀）。</summary>
    private void Error(ClientSession session, int err)
        => _ctx.LuaCalls.CallClientFunction(session, FnCallClientError, new JsonObject { ["err"] = err });

    private string DescribeShop(int shopId)
    {
        var shop = _table.GetShop(shopId);
        if (shop is null)
            return $"表外商店，商品 {_table.GoodsOf(shopId).Count} 件";
        return $"{shop.Name} rule={shop.RefreshRule} columns={shop.Columns} " +
               $"{(_table.IsGroupedShop(shopId) ? $"{_table.GroupsOf(shopId).Count} 组" : "不分组")}";
    }

    private string DescribeShelf(List<ShelfEntry> shelf)
        => string.Join(" ", shelf.Take(12).Select(e =>
        {
            var g = _table.GetGoods(e.GoodsId);
            return $"{e.GoodsId}({g?.Name})" + (e.Pos.HasValue ? $"@{e.Pos}" : "")
                 + (e.Discount == 100 ? "" : $" {e.Discount}%");
        })) + (shelf.Count > 12 ? $" …共 {shelf.Count} 件" : "");

    /// <summary>读一个 JSON 数字（宽容：字符串数字、浮点、布尔都能吃下），失败给 <paramref name="fallback"/>。
    /// 用 <c>TryGetValue</c> 而不是 <c>GetValue&lt;int&gt;</c>+catch —— 上行是客户端发来的，
    /// 类型不可控，走异常路径会把日志刷满。</summary>
    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is not JsonValue v)
            return fallback;
        if (v.TryGetValue(out int i))
            return i;
        if (v.TryGetValue(out double d))
            return (int)d;
        if (v.TryGetValue(out bool b))
            return b ? 1 : 0;
        if (v.TryGetValue(out string? s) && double.TryParse(s, out double sd))
            return (int)sd;
        return fallback;
    }

    private static string ReadString(JsonNode? node)
    {
        if (node is null)
            return "";
        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }
}
