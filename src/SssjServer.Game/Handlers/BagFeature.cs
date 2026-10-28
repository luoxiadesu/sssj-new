using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 背包 / 仓库功能域（<c>UI_Warehouse</c> 那一屏 + 物品使用 / 锁定 / 红点）。
///
/// <para><b>0. 这一族命令的形状</b></para>
/// 两族混用（见 <c>docs/04-level-flow.md §1</c>）：
/// <list type="bullet">
/// <item><b>字符串命令</b>（<c>Lib.lua:378 SendCmd</c> / <c>WeaponCommon.lua:286 SendToServer</c>）：
///   <c>ItemFlushFlag</c>（使用/清红点/清限时）、<c>LockItem</c>（锁定）。</item>
/// <item><b>LuaCall 数字命令</b>：25 / 26 / 32 / 349（四种分解）。</item>
/// </list>
///
/// <para><b>1. ★ 两族的 <c>nError</c> 语义相反（最容易踩的坑）</b></para>
/// <code>
/// LuaCall 族：UI_Warehouse.lua:1913/1936/1946  if tbParam.nError then ...   -- Lua 真值判断！
///            → 成功回包**绝对不能带 nError**（Lua 里 0 也是真，带 nError=0 会走错误分支）
/// 字符串族：S2CCall.lua:154/227/247           if tbParam.nError > 0 then ... -- 数值比较
///            → 成功回包**必须带 nError=0**（缺字段会 nil > 0 抛错）
/// </code>
/// 成因在原服务端的回包封装 <c>LuaCall:CallClient</c>（<c>LuaCall.lua:512..516</c>）：
/// <c>local nError = tonumber(tbParam); if nError then tbParam = {nError = nError} end</c>
/// —— 出错时它回的是**裸数字**，成功时回 table，所以成功 table 里根本不会出现 nError。
///
/// <para><b>2. ★ 背包变更必须走 pf 1031，不能用 pf 1104</b></para>
/// 客户端 <c>OnUpdateItem</c>（pf 1031，RVA 0x1D9D72C）给 <c>Add*</c> 传 <c>ntf=1</c>
/// （四处 <c>mov w2,#1</c>：0x1D9D8A0/8E0/920/960）→ 会 <c>DispatchLuaEvent</c>
/// （道具 8 <c>OnNotifyItemDataChange</c> / 武器 9 / 角色卡 10 / 零件 25，dump.cs:581714..581731），
/// 仓库正是靠它刷新（<c>UI_Warehouse.lua:294..295</c> 订阅、<c>:385..386</c> 退订）；
/// 而 <c>InitItem</c>（pf 1104）传 <c>ntf=0</c>（<c>mov w2,wzr</c>）**不派发事件**，
/// 拿它推变更的话界面不会刷新。
///
/// <para><b>3. ★ 分解之后只有「回包」能刷新界面（<c>bDoSell</c> 死锁）</b></para>
/// <code>
/// UI_Warehouse.lua:1815  DoSure()            self.bDoSell = true
/// UI_Warehouse.lua:1906  NotifyItemDataChange if not tbClass.bDoSell then UpdateWhenServerMsg() end
/// </code>
/// 即点了分解之后，pf 1031 的刷新被 <c>bDoSell</c> 挡住，而 <c>bDoSell</c> 只在
/// <c>OnOpen</c>（:298）与 <c>OnServerSell</c>（:2064，仅 <c>S2C.WeaponLogicMsg</c> 会调）里清零。
/// 所以 25/26/32/349 的**回包是唯一的刷新途径** —— 不回包 = 物品扣了但界面纹丝不动，
/// 且此后整个界面对 pf 1031 免疫，直到重开。
///
/// <para><b>4. 服务端必须自己做的校验</b></para>
/// 客户端那两条保护在本版本**是坏的**：<c>UI_Warehouse.lua:1297</c> 用
/// <c>RuneItem.weaponGuid</c> 判「已装备」，而该 setter 唯一调用者是 XLua wrapper
/// （协议从不写它，恒为 0）；紧邻的 <c>bSolution</c>（在编队中）检查被注释掉了（:1301..1304）。
/// 于是「分解掉出战装备/卡」只能靠服务端拦，见 <see cref="IsEquipped"/>。
/// </summary>
public sealed class BagFeature : IGameFeature
{
    // ---------------- 字符串命令名 ----------------

    /// <summary>
    /// 上行 <c>ItemFlushFlag</c>：使用道具 / 清红点 / 清剧情未读 / 批量清限时。
    /// 15 个发送点，全部带 <c>bNoWait=true</c>（不挂 Blocking 遮罩）。
    /// </summary>
    public const string FnItemFlushFlag = "ItemFlushFlag";

    /// <summary>上行 <c>LockItem{nGuid,nLockOn}</c>（12 个发送点，无回包）。</summary>
    public const string FnLockItem = "LockItem";

    /// <summary>
    /// 「使用道具」的下行名（<c>S2CCall.lua:397 S2C.UseItemMsg</c>）——
    /// 与上行 <c>ItemFlushFlag</c> **不同名**（PhoneMsg→ServerPhoneMsg 的同类先例）。
    /// </summary>
    public const string FnUseItemMsg = "UseItemMsg";

    // ---------------- ItemFlushFlag 的 nCmd ----------------

    /// <summary>nCmd=1：清「新道具」红点（<c>Flag</c> bit0 → 0）。无回包。</summary>
    public const int FlushClearNew = 1;

    /// <summary>nCmd=2：**使用道具**（回 <c>UseItemMsg</c>）。</summary>
    public const int FlushUseItem = 2;

    /// <summary>nCmd=3：清角色卡「剧情未读」（<c>Flag</c> bit1 → 1）。无回包。</summary>
    public const int FlushStoryRead = 3;

    /// <summary>
    /// nCmd=4：**批量删除已过期的限时道具**。
    /// 由客户端 <c>EquipManager.DealLimitedItem</c>（RVA 0x1D9B460，登录后自动跑）
    /// → <c>ItemLogic.DoUseLimitItem</c>（<c>ItemLogic.lua:149..160</c>）发起。
    /// 判定式 <c>BaseItem.get_IsLimit</c>（RVA 0x174E338）：
    /// <c>LimitTime &gt;= 1 &amp;&amp; !CloseLimit &amp;&amp; (CreateTime + LimitTime) &lt; 服务器时间</c>
    /// —— 所以 <c>createtime</c> 必须是**真实 unix 秒**，写 0 会让限时道具每次登录都被判过期。
    /// </summary>
    public const int FlushDropExpired = 4;

    // ---------------- LuaCall 数字命令 ----------------

    /// <summary>LuaCall 25 <c>Item_SellItem</c>：用品/其他面板分解，tbParam = <c>[[G,D,P,L,num],…]</c>。</summary>
    public const int CmdSellItem = 25;

    /// <summary>LuaCall 26 <c>Item_SellStuff</c>：物资面板分解，tbParam = <c>[{guid,num},…]</c>。</summary>
    public const int CmdSellStuff = 26;

    /// <summary>LuaCall 32 <c>Rune_Sell</c>：模块/符文分解，tbParam = <c>[guid,…]</c>。</summary>
    public const int CmdSellRune = 32;

    /// <summary>LuaCall 349 <c>WeaponParts_Sell</c>：零件分解，tbParam = <c>{bConvert,tbGuid:[guid,…]}</c>。</summary>
    public const int CmdSellWeaponParts = 349;

    /// <summary>客户端单次分解的选择上限（<c>UI_Warehouse.lua:20 MAX_SELL_SELECT</c>）。</summary>
    public const int MaxSellSelect = 40;

    // ---------------- 错误码 ----------------
    // 全部取自客户端**自己**的预检分支（本地化表不在 extracted 里，只能这样反推）。

    /// <summary>物品已锁定（<c>UI_Warehouse.lua:1294</c> 的 tip 用 20015）。</summary>
    public const int ErrLocked = 20015;

    /// <summary>物品已装备 / 在编队中（<c>UI_Warehouse.lua:1298</c> 用 20016）。</summary>
    public const int ErrEquipped = 20016;

    /// <summary>物品不存在（无客户端取证，走 <c>sMsg</c> 原文提示，不依赖码表）。</summary>
    public const int ErrNoItem = 20009;

    private GameFeatureContext _ctx = null!;

    public string Name => "背包/仓库（ItemFlushFlag / LockItem / 分解 25,26,32,349）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;

        ctx.MapFunction(FnItemFlushFlag, OnItemFlushFlag);
        ctx.MapFunction(FnLockItem, OnLockItem);

        ctx.MapLuaCall(CmdSellItem, (s, c, p) => OnSellByTemplate(s, p));
        ctx.MapLuaCall(CmdSellStuff, (s, c, p) => OnSellStuff(s, p));
        ctx.MapLuaCall(CmdSellRune, (s, c, p) => OnSellByGuidList(s, p, CmdSellRune));
        ctx.MapLuaCall(CmdSellWeaponParts, (s, c, p) => OnSellByGuidList(s, p, CmdSellWeaponParts));
    }

    // =================================================================
    //  字符串命令 ItemFlushFlag
    // =================================================================

    /// <summary>
    /// <c>ItemFlushFlag</c> 四个 nCmd 的入口。
    /// nCmd=1/3/4 **不需要回包**（客户端已本地改好视图），但服务端必须落库，
    /// 否则重登红点复活 / 过期道具反复上报。
    /// </summary>
    private Task OnItemFlushFlag(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int nCmd = ReadInt(param?["nCmd"], 0);
        switch (nCmd)
        {
            case FlushClearNew:
                return FlushFlagBit(session, player, ReadUInt(param?["nGuid"], 0), Item.FlagNewBit, set: false,
                                    what: "清新道具红点");

            case FlushStoryRead:
                return FlushFlagBit(session, player, ReadUInt(param?["nGuid"], 0), Item.FlagStoryReadBit, set: true,
                                    what: "标记剧情已读");

            case FlushUseItem:
                return UseItem(session, player, ReadUInt(param?["nGuid"], 0), ReadStr(param?["Class"]));

            case FlushDropExpired:
                return DropExpired(session, player, param?["tbGuid"] as JsonArray);

            default:
                Log.Warn($"[session {session.Id}] ItemFlushFlag 未知 nCmd={nCmd} param={param?.ToJsonString()}");
                return Task.CompletedTask;
        }
    }

    /// <summary>置/清一件物品的 <c>Flag</c> 位（bit0 新道具 / bit1 剧情已读）。无回包。</summary>
    private Task FlushFlagBit(ClientSession session, Player player, uint guid, uint bit, bool set, string what)
    {
        var item = player.GetItem(guid);
        if (item is null)
        {
            Log.Warn($"[session {session.Id}] ItemFlushFlag {what} 找不到 guid={guid}");
            return Task.CompletedTask;
        }

        uint before = item.Flag;
        item.Flag = set ? item.Flag | bit : item.Flag & ~bit;
        if (item.Flag != before)
            _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] {what} guid={guid} " +
                 $"{_ctx.ItemTemplates.NameOf(item.Genre, item.Detail, item.Particular, item.Level)} " +
                 $"flag {before}→{item.Flag}（无回包）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// nCmd=2 使用道具。
    ///
    /// 本实现只覆盖**有取证的那一类**：能折算成货币的道具（<c>ItemBaseTable.TryGetMoneyId</c>，
    /// 即 <c>*CoinAuto</c> 行的 <c>ExtParam2</c> 与 <c>Lib.lua:1364 GetMoneyGDPL</c> 的硬映射）。
    /// 用掉一个 → 货币 +N → pf 1032 → 回 <c>UseItemMsg{Class, MoneyIdx, MoneyNum}</c>。
    ///
    /// ★ <c>MoneyIdx</c> 只能是 1..5：客户端 <c>GetMoneyGDPL</c>（<c>Lib.lua:1365..1386</c>）
    /// 只认「1 体力 / 2 金币 / 3 钻石 / 4 约会点数 / 5 小塔小代币」，其它值返回 nil，
    /// 而 <c>S2C.UseItemMsg</c>（<c>S2CCall.lua:406</c>）拿它当 <c>{tbAward}</c> 的唯一元素 ——
    /// nil 会让奖励框显示空白（不抛错）。所以非这 5 种货币时**不带** MoneyIdx/MoneyNum，
    /// 只回 <c>Class</c>，让客户端安静地关掉遮罩。
    ///
    /// 其余「功能性道具」（改名卡、经验书、礼包…）需要 <c>ItemBaseInfo.ClassName</c> 的
    /// 逐类语义，本仓的 <c>ItemList.txt</c> 有 ClassName 但没有效果表，故一律拒绝并记日志，
    /// 避免凭猜测扣掉玩家道具。
    /// </summary>
    private Task UseItem(ClientSession session, Player player, uint guid, string uiClass)
    {
        var item = player.GetItem(guid);
        if (item is null)
        {
            Log.Warn($"[session {session.Id}] 使用道具找不到 guid={guid}，回空 UseItemMsg 解遮罩");
            _ctx.LuaCalls.CallClientFunction(session, FnUseItemMsg, new { Class = uiClass });
            return Task.CompletedTask;
        }

        string name = _ctx.ItemTemplates.NameOf(item.Genre, item.Detail, item.Particular, item.Level);

        if (!_ctx.ItemTemplates.TryGetMoneyId(item.Genre, item.Detail, item.Particular, item.Level, out int moneyId))
        {
            Log.Warn($"[session {session.Id}] 使用道具 {name}(guid={guid}) 未实现：" +
                     $"该模板不是货币道具，效果需要 ItemBaseInfo.ClassName 的逐类语义（未取证）。" +
                     $"只回空 UseItemMsg 解遮罩，不扣道具");
            _ctx.LuaCalls.CallClientFunction(session, FnUseItemMsg, new { Class = uiClass });
            return Task.CompletedTask;
        }

        // 一次用一个（客户端 UI_ItemNormal.lua:721 没有数量参数）
        uint used = 1;
        var template = _ctx.ItemTemplates.Get(item.Genre, item.Detail, item.Particular, item.Level);
        int perUse = ParsePositive(template?.ExtParam1) ?? 1;   // 每个道具折算多少货币（ExtParam1，缺省 1）
        int gain = perUse * (int)used;

        item.Count -= used;
        var removed = new List<Item>();
        if (item.Count == 0)
        {
            player.Items.Remove(item);
            removed.Add(item);
        }
        player.AddMoney(moneyId, gain);
        _ctx.Store.Save(player);

        GameNotify.PushMoney(session, player, moneyId);
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        else
            GameNotify.PushItem(session, item);

        Log.Info($"[session {session.Id}] 使用道具 {name}(guid={guid}) ×{used} → " +
                 $"货币 {moneyId}({MoneyType.NameOf(moneyId)}) +{gain}（余 {player.GetMoney(moneyId)}）");

        // MoneyIdx 只在客户端认识的 1..5 范围内才下发（理由见方法注释）
        object payload = moneyId is >= 1 and <= 5
            ? new { Class = uiClass, MoneyIdx = moneyId, MoneyNum = gain }
            : new { Class = uiClass };
        _ctx.LuaCalls.CallClientFunction(session, FnUseItemMsg, payload);
        return Task.CompletedTask;
    }

    /// <summary>
    /// nCmd=4 批量删除已过期的限时道具。无回包。
    ///
    /// ★ 服务端**独立复核过期**，不盲信客户端给的 guid：判定所需的两列就在
    /// <c>ItemList.txt</c> 里（表头第 18/19 列 <c>LimitTime</c>「限时(秒)」与
    /// <c>ExpiredDelete</c>「过期删除」），复核式与客户端
    /// <c>BaseItem.get_IsLimit</c>（RVA 0x174E338）一致：
    /// <c>LimitTime &gt;= 1 &amp;&amp; createtime + LimitTime &lt; now</c>，
    /// 实现见 <see cref="ItemTemplate.IsExpired"/>。
    ///
    /// 这样做的价值：万一设备时间被改快、或服务端曾下发过 <c>createtime=0</c> 的脏数据，
    /// 也不会误删玩家的限时道具 —— 只在日志里留一条「拒绝删除」。
    /// </summary>
    private Task DropExpired(ClientSession session, Player player, JsonArray? guids)
    {
        if (guids is null || guids.Count == 0)
            return Task.CompletedTask;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var removed = new List<Item>();
        int rejected = 0;

        foreach (var node in guids)
        {
            uint guid = ReadUInt(node, 0);
            var item = player.GetItem(guid);
            if (item is null)
                continue;

            var tpl = _ctx.ItemTemplates.Get(item.Genre, item.Detail, item.Particular, item.Level);
            if (tpl is null || !tpl.IsExpired(item.CreateTime, now))
            {
                rejected++;
                Log.Warn($"[session {session.Id}] 拒绝删除 guid={guid} " +
                         $"{_ctx.ItemTemplates.NameOf(item.Genre, item.Detail, item.Particular, item.Level)}：" +
                         $"服务端复核未过期（LimitTime={tpl?.LimitTime ?? -1} createtime={item.CreateTime} now={now}）");
                continue;
            }

            player.Items.Remove(item);
            removed.Add(item);
        }

        if (removed.Count == 0)
        {
            if (rejected > 0)
                Log.Warn($"[session {session.Id}] ItemFlushFlag nCmd=4：{rejected} 件均未通过服务端过期复核，未删除任何物品");
            return Task.CompletedTask;
        }

        _ctx.Store.Save(player);
        GameNotify.PushItemsRemoved(session, removed);
        Log.Info($"[session {session.Id}] 清理过期限时道具 {removed.Count} 件" +
                 (rejected > 0 ? $"（另有 {rejected} 件被拒）" : "") + "：" +
                 string.Join(" ", removed.Take(8).Select(i => $"guid={i.Guid}")) +
                 "（客户端 DealLimitedItem 发起，无回包）");
        return Task.CompletedTask;
    }

    // =================================================================
    //  字符串命令 LockItem
    // =================================================================

    /// <summary>
    /// <c>LockItem{nGuid, nLockOn}</c>：锁定 / 解锁一件物品。**无回包**
    /// （客户端已本地写 <c>pItem.LockOn = nLock</c>，如 <c>UI_ItemTip.lua:404</c>）。
    /// 服务端的义务有两条：①落库，让下次 1104/1031 的 <c>lockon</c> 一致；
    /// ②分解请求里拒绝 <c>lockon &gt; 0</c> 的物品（见 <see cref="CheckSellable"/>）。
    /// </summary>
    private Task OnLockItem(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        uint guid = ReadUInt(param?["nGuid"], 0);
        uint lockOn = ReadUInt(param?["nLockOn"], 0);
        var item = player.GetItem(guid);
        if (item is null)
        {
            Log.Warn($"[session {session.Id}] LockItem 找不到 guid={guid}");
            return Task.CompletedTask;
        }

        item.LockOn = lockOn;
        _ctx.Store.Save(player);
        Log.Info($"[session {session.Id}] {(lockOn > 0 ? "锁定" : "解锁")} " +
                 $"{_ctx.ItemTemplates.NameOf(item.Genre, item.Detail, item.Particular, item.Level)}" +
                 $"(guid={guid})（无回包）");
        return Task.CompletedTask;
    }

    // =================================================================
    //  分解：25（按模板四元组）
    // =================================================================

    /// <summary>
    /// LuaCall 25 <c>Item_SellItem</c>：tbParam 是**数组的数组** <c>[[G,D,P,L,num],…]</c>
    /// （构造 <c>UI_Warehouse.lua:1757</c>，发送 <c>:1812</c>）——注意**不给 guid**，
    /// 服务端要自己按四元组找实例。本实现按 <see cref="Item.CreateTime"/> 升序消耗
    /// （与客户端 ItemSort 的 CreateTime 口径一致；原服顺序未取证）。
    ///
    /// 回包 <c>UI_Warehouse:OnSellItem</c>（<c>:1934..1941</c>）：
    /// <c>{Gold=<返还金币>}</c>，**成功不带 nError**；失败带 <c>{nError, sMsg}</c>，
    /// 其中 <c>sMsg</c> 是**原文直显**（不过本地化表），所以可以直接写中文。
    /// </summary>
    private Task OnSellByTemplate(ClientSession session, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        if (param is not JsonArray list || list.Count == 0)
        {
            ReplyLuaCallError(session, CmdSellItem, ErrNoItem, "没有选择要分解的物品");
            return Task.CompletedTask;
        }
        if (list.Count > MaxSellSelect)
            Log.Warn($"[session {session.Id}] 分解 25 选择数 {list.Count} 超过客户端上限 {MaxSellSelect}，仍按请求处理");

        int gold = 0;
        var changed = new List<Item>();
        var removed = new List<Item>();
        var sold = new List<string>();

        foreach (var node in list)
        {
            if (node is not JsonArray t || t.Count < 5)
                continue;
            uint g = ReadUInt(t[0], 0), d = ReadUInt(t[1], 0), p = ReadUInt(t[2], 0), l = ReadUInt(t[3], 0);
            int want = ReadInt(t[4], 0);
            if (want <= 0)
                continue;

            int price = _ctx.ItemTemplates.Get(g, d, p, l)?.SellingPrice ?? 0;
            int taken = TakeByTemplate(player, g, d, p, l, want, changed, removed);
            if (taken <= 0)
            {
                Log.Warn($"[session {session.Id}] 分解 25 跳过 ({g},{d},{p},{l})：无可分解实例（锁定/装备中/不存在）");
                continue;
            }
            gold += price * taken;
            sold.Add($"{_ctx.ItemTemplates.NameOf(g, d, p, l)}×{taken}");
        }

        if (sold.Count == 0)
        {
            ReplyLuaCallError(session, CmdSellItem, ErrNoItem, "没有可分解的物品（已锁定或已装备）");
            return Task.CompletedTask;
        }

        FinishSell(session, player, gold, changed, removed, sold, "分解(25)");
        // 成功回包：只带 Gold，绝不带 nError（Lua 真值判断，见类注释 §1）
        _ctx.LuaCalls.CallClient(session, CmdSellItem, new { Gold = gold });
        return Task.CompletedTask;
    }

    // =================================================================
    //  分解：26（按 guid+num，回包是「数字下标表」）
    // =================================================================

    /// <summary>
    /// LuaCall 26 <c>Item_SellStuff</c>：tbParam = <c>[{guid,num},…]</c>
    /// （构造 <c>UI_Warehouse.lua:1758..1762</c>，发送 <c>:1808</c>）。
    ///
    /// 回包 <c>UI_Warehouse:OnSellStuff</c>（<c>:1944..1963</c>）读的是**数字下标**：
    /// <code>
    /// :1949  if tbParam[1] &gt; 0 then                       -- [1] 返还金币，必填 number
    /// :1950     if tbParam[2] and #tbParam[2] &gt; 0 then    -- [2] 返还道具数组（可省，[] 等价）
    /// :1951        UI.OpenAsChild('UI_RewardNormal', {{15,1,1,1,tbParam[1]}, {tbParam[2][1][1..5]}})
    /// </code>
    /// 所以回包必须是 **JSON 数组** <c>[gold, [[G,D,P,L,N],…]]</c>；
    /// <c>tbParam[1]</c> 为 nil 会 <c>nil &gt; 0</c> 直接抛 Lua 错。
    /// 客户端只显示 <c>tbParam[2][1]</c>（第一件），但所有返还项都要真的进背包（走 pf 1031）。
    ///
    /// ★ 与原服的已知偏差：原服的返还量走 <c>ItemRarityItem.txt</c> 的
    /// <c>Param/DisItem</c> 公式（<c>UI_Warehouse.lua:1333..1364</c> 的客户端预算即其镜像），
    /// 需要 <c>ItemBaseInfo.Rarity</c>；本仓 <c>ItemList.txt</c> 只有 <c>RarityColor</c>，
    /// 两者是否同一列**未取证**，所以这里统一用 <c>SellingPrice</c> 折金币、不发额外材料。
    /// 数值偏小但链路完整；等 Rarity 取证后替换 <see cref="SellPriceOf"/> 即可。
    /// </summary>
    private Task OnSellStuff(ClientSession session, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        if (param is not JsonArray list || list.Count == 0)
        {
            ReplyLuaCallError(session, CmdSellStuff, ErrNoItem, "没有选择要分解的物资");
            return Task.CompletedTask;
        }

        int gold = 0;
        var changed = new List<Item>();
        var removed = new List<Item>();
        var sold = new List<string>();

        foreach (var node in list)
        {
            uint guid = ReadUInt(node?["guid"], 0);
            int want = ReadInt(node?["num"], 0);
            if (guid == 0 || want <= 0)
                continue;

            var item = player.GetItem(guid);
            if (item is null)
                continue;
            int err = CheckSellable(player, item);
            if (err != 0)
            {
                Log.Warn($"[session {session.Id}] 分解 26 跳过 guid={guid}：{DescribeError(err)}");
                continue;
            }

            int taken = Math.Min(want, (int)item.Count);
            gold += SellPriceOf(item) * taken;
            sold.Add($"{_ctx.ItemTemplates.NameOf(item.Genre, item.Detail, item.Particular, item.Level)}×{taken}");
            ConsumeInstance(player, item, (uint)taken, changed, removed);
        }

        if (sold.Count == 0)
        {
            ReplyLuaCallError(session, CmdSellStuff, ErrNoItem, "没有可分解的物资（已锁定或已装备）");
            return Task.CompletedTask;
        }

        FinishSell(session, player, gold, changed, removed, sold, "分解(26)");

        // 回包是数字下标数组：[gold, [返还道具…]]（这里不发额外材料，故给空数组）
        var reply = new JsonArray { gold, new JsonArray() };
        _ctx.LuaCalls.CallClient(session, CmdSellStuff, reply);
        return Task.CompletedTask;
    }

    // =================================================================
    //  分解：32 / 349（按 guid 列表，共用 OnSellRune 回包形状）
    // =================================================================

    /// <summary>
    /// LuaCall 32 <c>Rune_Sell</c>（tbParam = <c>[guid,…]</c>，<c>UI_Warehouse.lua:1797</c>）与
    /// 349 <c>WeaponParts_Sell</c>（tbParam = <c>{bConvert, tbGuid:[guid,…]}</c>，<c>:1810</c>）。
    /// 两者的下行都进 <c>UI_Warehouse:OnSellRune</c>（<c>:1912..1931</c>，LuaCall.lua:855/3589）：
    /// <code>
    /// :1921  if tbParam.Gold &gt; 0 then    -- Gold 必填 number，nil 会抛
    /// :1924  if tbParam.items and #tbParam.items &gt; 0 then  -- items 可省；元素 {G,D,P,L,N}，**全部**都会显示
    /// </code>
    /// ★ 错误分支（<c>:1913</c>）**不会** <c>UpdateWhenServerMsg()</c> 也不清 <c>bDoSell</c>
    /// → 仓库界面会卡在「分解中」直到重开。所以失败也尽量给出 <c>Gold=0</c> 的成功形状更友好？
    /// 不 —— 那会骗客户端弹空奖励框。这里仍按语义回 nError，但把原因写进服务端日志。
    /// </summary>
    private Task OnSellByGuidList(ClientSession session, JsonNode? param, int cmd)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        JsonArray? guids = cmd == CmdSellWeaponParts
            ? param?["tbGuid"] as JsonArray
            : param as JsonArray;
        bool convert = cmd == CmdSellWeaponParts && ReadBool(param?["bConvert"]);

        if (guids is null || guids.Count == 0)
        {
            ReplyLuaCallError(session, cmd, ErrNoItem, "没有选择要分解的对象");
            return Task.CompletedTask;
        }

        int gold = 0;
        var changed = new List<Item>();
        var removed = new List<Item>();
        var sold = new List<string>();

        foreach (var node in guids)
        {
            uint guid = ReadUInt(node, 0);
            var item = guid == 0 ? null : player.GetItem(guid);
            if (item is null)
                continue;
            int err = CheckSellable(player, item);
            if (err != 0)
            {
                Log.Warn($"[session {session.Id}] 分解({cmd}) 跳过 guid={guid}：{DescribeError(err)}");
                continue;
            }

            gold += SellPriceOf(item) * (int)Math.Max(1u, item.Count);
            sold.Add($"{_ctx.ItemTemplates.NameOf(item.Genre, item.Detail, item.Particular, item.Level)}" +
                     $"{(item.Count > 1 ? $"×{item.Count}" : "")}");
            ConsumeInstance(player, item, item.Count, changed, removed);
        }

        if (sold.Count == 0)
        {
            ReplyLuaCallError(session, cmd, ErrNoItem, "没有可分解的对象（已锁定或已装备）");
            return Task.CompletedTask;
        }

        FinishSell(session, player, gold, changed, removed, sold,
                   cmd == CmdSellWeaponParts ? $"零件分解(349,bConvert={convert})" : "模块分解(32)");

        // Gold 必填；items 这里为空（返还材料需要 Rarity 取证，见 OnSellStuff 注释）
        _ctx.LuaCalls.CallClient(session, cmd, new { Gold = gold, items = Array.Empty<int[]>() });
        return Task.CompletedTask;
    }

    // =================================================================
    //  公共：扣除 / 校验 / 推送
    // =================================================================

    /// <summary>
    /// 按四元组扣除 <paramref name="want"/> 个（跨实例，按 <c>CreateTime</c> 升序），
    /// 跳过锁定与装备中的实例。返回实际扣掉的数量。
    /// </summary>
    private int TakeByTemplate(Player player, uint g, uint d, uint p, uint l, int want,
                               List<Item> changed, List<Item> removed)
    {
        int taken = 0;
        var candidates = player.Items
            .Where(i => i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l)
            .Where(i => CheckSellable(player, i) == 0)
            .OrderBy(i => i.CreateTime)
            .ThenBy(i => i.Guid)
            .ToList();

        foreach (var item in candidates)
        {
            if (taken >= want)
                break;
            uint use = (uint)Math.Min(want - taken, (int)item.Count);
            if (use == 0)
                continue;
            ConsumeInstance(player, item, use, changed, removed);
            taken += (int)use;
        }
        return taken;
    }

    /// <summary>
    /// 从一个实例上扣 <paramref name="count"/> 个：扣完就从背包摘掉并进 <paramref name="removed"/>，
    /// 否则进 <paramref name="changed"/>（推 pf 1031 时 count 是绝对值）。
    /// </summary>
    private static void ConsumeInstance(Player player, Item item, uint count,
                                        List<Item> changed, List<Item> removed)
    {
        if (count >= item.Count)
        {
            player.Items.Remove(item);
            removed.Add(item);
            return;
        }
        item.Count -= count;
        if (!changed.Contains(item))
            changed.Add(item);
    }

    /// <summary>
    /// 分解前的服务端校验：锁定 / 在编队中。返回 0 表示可分解。
    /// 客户端这两条保护在本版本是坏的（见类注释 §4），必须由服务端把关。
    ///
    /// ★ <c>internal</c> 而非 <c>private</c>：<see cref="CardFeature"/> 分解角色卡
    /// （<c>LuaCall.Card_Decompose</c>）走的是另一条上行通道，但**必须做同样的校验**
    /// —— 否则玩家能把已锁定 / 正在上阵的卡分解掉。两处共用这一份，避免规则漂移。
    /// </summary>
    internal static int CheckSellable(Player player, Item item)
    {
        if (item.Genre == 1 && item.Guid != 0 && PledgeFeature.LoadStateStatic(player).Girls.Values.Any(g => g.CardGuid == item.Guid))
            return ErrLocked;
        if (item.LockOn > 0)
            return ErrLocked;
        if (IsEquipped(player, item.Guid))
            return ErrEquipped;
        return 0;
    }

    /// <summary>
    /// 该实例是否被任何编队引用（主卡 / 上阵卡 / 武器 / 副卡 / 符文槽）。
    /// 编队结构见 <see cref="Formation"/>（上行 <c>LuaCall.Formation_Update</c> 写入）。
    /// </summary>
    private static bool IsEquipped(Player player, uint guid)
    {
        if (guid == 0)
            return false;
        foreach (var f in player.Formations)
        {
            foreach (var slot in f.Slots)
            {
                if (slot.MainCardGuid == guid || slot.UsedCardGuid == guid || slot.WeaponGuid == guid)
                    return true;
                if (slot.SecondaryCardGuids.Contains(guid) || slot.RuneItems.Contains(guid))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 单个实例的分解返还金币。
    /// 取证：<c>UI_Warehouse.lua:1409</c> 的客户端预算即
    /// <c>tbInfo.TemplateInfo.SellingPrice * count</c>（<c>ItemBaseInfo.SellingPrice</c>，
    /// 对应 <c>ItemList.txt</c> 的 <c>SellingPrice</c> 列）。
    /// </summary>
    private int SellPriceOf(Item item)
        => _ctx.ItemTemplates.Get(item.Genre, item.Detail, item.Particular, item.Level)?.SellingPrice ?? 0;

    /// <summary>落库 + 加金币 + 推 pf 1032/1031 + 记日志（四种分解共用尾巴）。</summary>
    private void FinishSell(ClientSession session, Player player, int gold,
                            List<Item> changed, List<Item> removed, List<string> sold, string what)
    {
        if (gold > 0)
            player.AddMoney(MoneyType.Gold, gold);
        _ctx.Store.Save(player);

        if (gold > 0)
            GameNotify.PushMoney(session, player, MoneyType.Gold);
        GameNotify.PushItems(session, changed);
        GameNotify.PushItemsRemoved(session, removed);

        Log.Info($"[session {session.Id}] {what} {string.Join(" ", sold)} → 金币+{gold}" +
                 $"（余 {player.GetMoney(MoneyType.Gold)}）删除 {removed.Count} 件，改数量 {changed.Count} 件");
    }

    /// <summary>
    /// LuaCall 族的错误回包：<c>{nError, sMsg}</c>。
    /// <c>sMsg</c> 只在 25/26 的 handler 里被 <c>Game.MessageBox.Simple(tbParam.sMsg)</c>
    /// **原文直显**（<c>UI_Warehouse.lua:1937</c>/<c>:1946</c>），32/349 走的是
    /// <c>UIText('Error', nError)</c> 码表（<c>:1914</c>）—— 两者都带上最省事。
    /// </summary>
    private void ReplyLuaCallError(ClientSession session, int cmd, int error, string message)
    {
        Log.Warn($"[session {session.Id}] LuaCall {cmd} 失败：nError={error} {message}");
        _ctx.LuaCalls.CallClient(session, cmd, new { nError = error, sMsg = message });
    }

    private static string DescribeError(int err) => err switch
    {
        ErrLocked => "已锁定(20015)",
        ErrEquipped => "在编队中/已装备(20016)",
        _ => $"错误码 {err}",
    };

    // ---------------- JSON 小工具 ----------------

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try { return node.GetValue<int>(); }
        catch { return double.TryParse(node.ToString(), out var d) ? (int)d : fallback; }
    }

    private static uint ReadUInt(JsonNode? node, uint fallback)
    {
        int v = ReadInt(node, unchecked((int)fallback));
        return v < 0 ? fallback : (uint)v;
    }

    private static string ReadStr(JsonNode? node)
    {
        if (node is null)
            return "";
        try { return node.GetValue<string>(); }
        catch { return node.ToString(); }
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null)
            return false;
        try { return node.GetValue<bool>(); }
        catch { return ReadInt(node, 0) != 0; }
    }

    /// <summary>把 <c>ExtParam1</c> 之类的字符串列解析成正整数；空/非法返回 null。</summary>
    private static int? ParsePositive(string? raw)
        => int.TryParse(raw, out var v) && v > 0 ? v : null;
}
