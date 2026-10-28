using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 咖啡馆「经营循环」的四个上行命令 —— 咖啡馆场景打通（`docs/05` §16）之后客户端才开始发它们：
///
/// <list type="table">
///   <item><term>113 <c>Cafe_SetWaiterList</c></term><description>设置服务生（三个区的排班少女）</description></item>
///   <item><term>114 <c>Cafe_UpdateSeatList</c></term><description>客户端把算好的座位上报</description></item>
///   <item><term>115 <c>Cafe_GenCustomer</c></term><description>生成一个排队顾客（★ 真机已在刷）</description></item>
///   <item><term>119 <c>Cafe_MakeCoffee</c></term><description>制作咖啡加到橱窗</description></item>
/// </list>
///
/// 命令号取证 `LuaCall.lua:113..119`；四个 `RegisterCMD` 的 `MODULE_SERVER` 分支
/// （`:1611` / `:1622` / `:1632` / `:1723`）给出了原服函数名与上行形状。
///
/// <para><b>0. 为什么这四个是一组</b></para>
/// 它们共同填满 LuaCall 112 回包的六组清单（<see cref="CafeFeature.CafeContent"/>）——
/// `docs/05` §15.3 的分工表说得很清楚：**座位/少女/咖啡/排队都不是服务端凭空造的**，
/// 而是客户端算好后经这四个命令上报、服务端存档、下次进店再由 112 发回去。
/// 服务端唯一自己算的是「今日顾客」`weightlist`（已由 <c>CafeFeature.RollDailyGuests</c> 实现）。
///
/// <para><b>1. ★ 115 的回包只认 <c>basetime</c> 这一个开关</b></para>
/// 取证 `NCafeNet.lua:229..247`：
/// <code>
/// function NCafeNet.RspCustomerQueue(tbParam)
///     if tbParam and tbParam.basetime then          -- ★ 没有 basetime 就整段跳过
///         NCafe.Data.ReqCustomerTime = tbParam.basetime
///         NCafe.Data:ClearGuestQueue()
///         if tbParam.customerqueue then              -- 这一层也有保护
///             for i, v in ipairs(tbParam.customerqueue) do
///                 NCafe.Data:AddGuest2Queue(v.customertype, v.customeridx, v.starttime)  -- ★ 三个都无保护
///             end
///         end
///     else
///         NCafe.Data.ReqCustomerTime = GetTime();     -- 兜底：不加载队列
///     end
///     NCafeNet.ReqNewCustomer()
/// end
/// </code>
/// ⇒ `basetime` **必须给**（不给等于白回一趟）；`customerqueue` 可省，
/// 但只要给了，**每个元素的 `customertype`/`customeridx`/`starttime` 三者都必填**。
///
/// <para><b>2. ★ 客户端只在「本地队列为空」时才发 115</b></para>
/// `ReqCustomerQueue`（`:213..227`）的门禁：
/// <code>
/// if (nCoffee &lt;= 0 and nCount &lt;= 0 and nGuest &gt; 0) then return; end   -- 没咖啡没空位又已有排队 ⇒ 不请求
/// if (nGuest &gt; 0) then NCafeNet.RspCustomerQueue()                        -- 本地已有队列 ⇒ 自己处理，不发服务端
/// else LuaCall:CallServer(LuaCall.Cafe_GenCustomer, {}, true) end          -- 只有队列空了才发
/// </code>
/// 这解释了真机日志里 115 只出现一次（上行 `tbParam` 是**空数组**，没有任何参数）。
///
/// <para><b>3. ★ 114 的回包内容完全不被读</b></para>
/// `RspUploadSeatData`（`:204..207`）整个函数体只有一句 `NCafeNet.ReqNewCustomer()` ——
/// 不碰 `tbParam`。所以回包只需「存在」即可推进流程。
/// 而它的**上行**（`ReqUploadSeatData` `:188..203`）恰好印证了 §15.2 里那条
/// 「空座位必须整个省略 `customeridx`」——客户端自己上传时就是这个形状：
/// <code>
/// local tb = { position = o.Guid }        -- 座位 guid，必给
/// if o.guest then                          -- ★ 没顾客就只发 position，四个顾客字段一个都不加
///     tb.customeridx = o.guest.Guid; tb.customertype = o.guest.Id
///     tb.coffeetype  = o.guest.Coffee; tb.starttime  = o.guest.StartTime
/// end
/// </code>
///
/// <para><b>4. 113 的回包是「数组的数组」</b></para>
/// `RspSetWaiterList`（`:377..391`）按 `AreaType`（Cafe/Rest/Dorm 三个区）逐个索引：
/// <code>
/// for index = 1, #AreaType do
///     if (tbParam[index]) then                    -- 这层有 nil 保护
///         NCafe.Data:ClearAreaGirl(AreaType[index]);
///         for _, girl in ipairs(tbParam[index]) do
///             NCafe.Data:UpdateAreaGirl(AreaType[index], girl[1], girl[2]);   -- ★ girl = {girlId, modelId}，两个下标都无保护
///         end
/// </code>
/// ⇒ 回包是 `[[[girlId,modelId],…], […], […]]`，**外层三项**、内层每个少女是**二元数组**。
///
/// <para><b>5. ★ 119 成功时绝不能带 <c>nError</c></b></para>
/// `LuaCall.lua:1727` 是 `if tbParam.nError then ShowMsgByKey(...)` —— Lua **真值**判断，
/// `nError = 0` 也会被当成失败弹提示（AGENTS.md §5 的第一族约定）。
/// 成功分支会 `ClearCoffeeItems()` 后遍历 `tbParam.coffeelist`，
/// 且 `CoffeeCommon:GetCoffeeListConfig(v.coffeetype)` 之后**直接取 `template.Time`**
/// ⇒ **`coffeetype` 必须是 `CoffeeList.txt` 里的合法值**，否则 nil 解引用。
/// 好在同一段有 `if not next(v) then break end`，空元素是安全的。
/// 制作成功后客户端会自动再发一次 115（`:1745 NCafeNet.ReqCustomerQueue()`）。
///
/// <para><b>6. 存档位置</b></para>
/// 四个命令都读写 <see cref="CafeFeature.ExtraKey"/>（`Player.Extra["cafe"]`）里的六组清单，
/// 复用 <see cref="CafeFeature.ReadContent"/> / <see cref="CafeFeature.WriteState"/> ——
/// 这样 112 不需要任何改动就能把数据发回去。
/// ★ 不能另起一个键：`CafeFeature.WriteState` 每次进店会整体覆写 `["cafe"]`，
/// 数据必须落在它管理的那个对象里才不会被冲掉。
/// </summary>
public sealed class CafeBizFeature : IGameFeature
{
    /// <summary>LuaCall.Cafe_SetWaiterList = 113（`LuaCall.lua:113`「设置服务生」）。</summary>
    public const int SetWaiterListCmd = 113;

    /// <summary>LuaCall.Cafe_UpdateSeatList = 114（`:114`「更新座位」）。</summary>
    public const int UpdateSeatListCmd = 114;

    /// <summary>LuaCall.Cafe_GenCustomer = 115（`:115`「生成一个排队顾客」）。</summary>
    public const int GenCustomerCmd = 115;

    /// <summary>LuaCall.Cafe_MakeCoffee = 119（`:119`「制作咖啡加到橱窗」）。</summary>
    public const int MakeCoffeeCmd = 119;

    /// <summary>
    /// LuaCall.Cafe_RemoveCoffee = 118（`LuaCall.lua:118`「清理橱窗」，第十三轮补）。
    /// 上行点唯一：<c>UI_CoffeeMake.lua:264</c>。
    /// </summary>
    public const int RemoveCoffeeCmd = 118;

    /// <summary>LuaCall.Cafe_LevelUp = 111（`LuaCall.lua:1528`「咖啡馆升级」）。</summary>
    public const int LevelUpCmd = 111;

    // ---- 111 的错误码（与客户端预检 CafeLogic:CheckCafeCanLevelUp 同源）----
    //
    // ★ 客户端 LuaCall.lua:1534 是 `if tbParam.nError then`（**真值**判断）
    //   ⇒ 成功回包**绝不能带 nError**（LuaCall 数字族的通用约定，AGENTS.md §5）；
    //   失败则 `ShowMsgByKey('ui.Error.' .. tbParam.nError)` ⇒ 错误码要能拼出本地化键。
    // ★ 成功时客户端看 `elseif tbParam.level then` ⇒ **必须带 level**
    //   （LuaCall.lua:1536，然后弹 ui.UI_CafeInfo.LevelUpSuccess）。
    //
    // 四个码取自 CafeLogic:CheckCafeCanLevelUp（CafeLogic.lua:453..475）的第二返回值：

    /// <summary>已达 <c>Settings.MaxCafeLevel</c>（`CafeLogic.lua:456`）。</summary>
    public const int ErrAlreadyMaxLevel = 1;

    /// <summary>下一级没有配置行 = 已满级（`CafeLogic.lua:461`）。</summary>
    public const int ErrNoNextLevelCfg = 2;

    /// <summary>金币不足（`CafeLogic.lua:471`）。</summary>
    public const int ErrNotEnoughGold = 3;

    /// <summary>人气不足（`CafeLogic.lua:466`，仅当 <c>NeedHot &gt; 0</c> 才检查）。</summary>
    public const int ErrNotEnoughHot = 4;

    /// <summary>玩家等级不足（`NeedLevel` 列；客户端预检没查这条，服务端补上）。</summary>
    public const int ErrNotEnoughPlayerLevel = 5;

    /// <summary>咖啡馆升级消耗的货币 id = 金币（`CoffeeCommon.GetCurGold` 读 `me.gold`）。</summary>
    public const int GoldMoneyId = 2;

    /// <summary>LuaCall.Cafe_GetMaxHotReward = 135（`LuaCall.lua:1571`「满级咖啡馆人气值兑换」）。</summary>
    public const int GetMaxHotRewardCmd = 135;

    /// <summary>LuaCall.Cafe_GetBirthDayReward = 136（`LuaCall.lua:1581`「少女生日奖励」）。</summary>
    public const int GetBirthdayRewardCmd = 136;

    /// <summary>LuaCall.Cafe_GetMaxExpReward = 139（`LuaCall.lua:1558`「少女好感度满级兑换」）。</summary>
    public const int GetMaxExpRewardCmd = 139;

    /// <summary>生日奖励「今年已领」的任务值 group（★ 本服自定，原厂位置未取证）。</summary>
    public const int BirthdayClaimGroup = 50;

    /// <summary>LuaCall.Cafe_DrinkOffCoffee = 116（`LuaCall.lua:1647`「顾客喝完咖啡结算」）。</summary>
    public const int DrinkOffCoffeeCmd = 116;

    /// <summary>LuaCall.Cafe_NewSeatPosEmpty = 117（`LuaCall.lua:1675`「座位空，请顾客入位」）。</summary>
    public const int NewSeatPosEmptyCmd = 117;

    /// <summary>LuaCall.Cafe_AddGuestWight = 124（`LuaCall.lua:1815`「生成顾客权重」）。</summary>
    public const int AddGuestWeightCmd = 124;

    /// <summary>
    /// LuaCall.Cafe_Report = 122（`LuaCall.lua:1788`「营业报告」）。
    /// ★ **纯下行** —— `:1790` 的 `MODULE_SERVER` 分支只有一句注释「仅服务器主动发送」，
    /// 客户端从不请求它。服务端在「玩家离店结算」时主动推。
    /// </summary>
    public const int ReportCmd = 122;

    /// <summary>营业报告的累计键（存在 <c>Player.Extra["cafe"]</c> 之外的独立键）。</summary>
    internal const string ReportExtraKey = "cafereport";

    /// <summary>
    /// LuaCall.Cafe_OnLeave = 125（`LuaCall.lua:1838`「离开咖啡馆」）。
    /// 上行 <c>{usetime: 停留秒数}</c>，由 `UI_NCafe.lua:665` 在 `OnRelease` 时发出
    /// （仅当 `staytime ~= 0`）。★ 客户端的 `else` 分支是**空实现** ⇒ 不消费回包，
    /// 所以本实现拿它当「玩家离店」的时机来**推营业报告（122）**。
    /// </summary>
    public const int OnLeaveCmd = 125;

    /// <summary>LuaCall.Cafe_LearnCoffee = 120（`LuaCall.lua:1758`「咖啡制作技能学习」）。</summary>
    public const int LearnCoffeeCmd = 120;

    /// <summary>LuaCall.Cafe_ResetCoffee = 121（`LuaCall.lua:1773`「重置咖啡制作技能」）。</summary>
    public const int ResetCoffeeCmd = 121;

    /// <summary>
    /// 咖啡技能的任务值 group = **23**（`CoffeeCommon.lua:17` <c>CoffeeCommon.GroupId = 23</c>）。
    /// 该 group 下 <c>id = 咖啡 id</c> 存解锁位域，另有几个特殊 id（见 <see cref="SkillPointTaskId"/>）。
    /// </summary>
    public const int CoffeeTaskGroup = 23;

    /// <summary>
    /// 「已学」标记在解锁位域的 **bit 8**
    /// （`CoffeeCommon.lua:559` <c>LoadBits(lockinfo, 8, 8)</c>）。
    /// bit 0/1/2 分别是三个前置条件 <c>Cond1..3</c>（`:568/572/576`）。
    /// </summary>
    public const int CoffeeLearnedBit = 8;

    /// <summary>已用技能点的任务值 id（`CoffeeCommon.lua:18` <c>TaskId_SkillPoint = 1002</c>）。</summary>
    public const int SkillPointTaskId = 1002;

    /// <summary>咖啡 id 的上界 —— <c>&gt;= 1000</c> 不是真实咖啡（`CoffeeCommon.lua:556` 直接 return）。</summary>
    public const int MaxCoffeeId = 1000;

    // ---- 120/121 的错误码（A 族：nError 真值判断，成功绝不带）----

    /// <summary>咖啡 id 非法（不在 <c>CoffeeList.txt</c> 里，或 ≥ 1000）。</summary>
    public const int ErrCoffeeNotExists = 6;

    /// <summary>已经学过了。</summary>
    public const int ErrCoffeeAlreadyLearned = 7;

    /// <summary>前置咖啡未学（<c>Cond1</c> 指向的那杯）。</summary>
    public const int ErrCoffeePrevNotLearned = 8;

    /// <summary>咖啡币的货币 id = <see cref="MoneyType.CoffeeCoin"/>（<c>MoneyLogic.eMoneyType_CoffeeCoin = 7</c>）。</summary>
    public const int CoffeeCoinMoneyId = MoneyType.CoffeeCoin;

    /// <summary>
    /// ★ 排队队列上限**不是常量，来自配置表** —— `NCafeLevels.txt` 当前等级行的 `MaxQSize`
    /// （lv=1 实测是 2）。`CafeFeature.ReadQueue`（`CafeFeature.cs:978`）读回时就按它截断，
    /// 所以这里写入时必须用**同一个来源**，否则会出现「写进去 8 个、读出来 2 个」的不一致
    /// （本轮单测 `GenCustomer_StopsAtQueueCap` 就是这么暴露的）。
    /// </summary>
    internal int QueueCapFor(int cafeLevel)
    {
        var lv = _cafe.LevelRowFor(cafeLevel);
        return lv is { MaxQSize: > 0 } ? lv.MaxQSize : 1;
    }

    /// <summary>橱窗咖啡的种类上限。★ 取证见 `CafeFeature` 的 `coffeelist` 注释（比 4 长会抛）。</summary>
    public const int MaxCoffeeKinds = 4;

    private GameFeatureContext _ctx = null!;
    private GirlMoodRecovery _mood = null!;
    private CafeTable _cafe = CafeTable.Empty;
    private HouseTable _house = HouseTable.Empty;

    public string Name => "咖啡馆经营（LuaCall 113 排班 / 114 座位 / 115 顾客 / 119 咖啡）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _mood = new GirlMoodRecovery(Path.GetFullPath(ctx.Options.SettingDirectory));
        _cafe = CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _house = HouseTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.OnPlayerPrepare((session, player) => RefreshCoffeeConditions(player));
        ctx.BeforeCafeDownload += SettleElapsedSeats;
        ctx.MapLuaCall(SetWaiterListCmd, OnSetWaiterList);
        ctx.MapLuaCall(UpdateSeatListCmd, OnUpdateSeatList);
        ctx.MapLuaCall(GenCustomerCmd, OnGenCustomer);
        ctx.MapLuaCall(MakeCoffeeCmd, OnMakeCoffee);
        ctx.MapLuaCall(RemoveCoffeeCmd, OnRemoveCoffee);
        ctx.MapLuaCall(LevelUpCmd, OnLevelUp);
        ctx.MapLuaCall(DrinkOffCoffeeCmd, OnDrinkOffCoffee);
        ctx.MapLuaCall(NewSeatPosEmptyCmd, OnNewSeatPosEmpty);
        ctx.MapLuaCall(AddGuestWeightCmd, OnAddGuestWeight);
        ctx.MapLuaCall(OnLeaveCmd, OnLeave);
        ctx.MapLuaCall(LearnCoffeeCmd, OnLearnCoffee);
        ctx.MapLuaCall(ResetCoffeeCmd, OnResetCoffee);
        ctx.MapLuaCall(GetMaxHotRewardCmd, OnGetMaxHotReward);
        ctx.MapLuaCall(GetBirthdayRewardCmd, OnGetBirthdayReward);
        ctx.MapLuaCall(GetMaxExpRewardCmd, OnGetMaxExpReward);
    }

    internal List<KeyValuePair<uint, int>> RefreshCoffeeConditions(Player player)
        => RefreshCoffeeConditions(_cafe, player);

    internal static List<KeyValuePair<uint, int>> RefreshCoffeeConditions(CafeTable cafe, Player player)
    {
        var changes = new List<KeyValuePair<uint, int>>();
        foreach (var row in cafe.Coffees.Values)
        {
            int old = player.GetTask(CoffeeTaskGroup, row.Id);
            int bits = old;
            if (!string.IsNullOrWhiteSpace(row.Cond1))
                bits = CoffeeStagePassed(player, row.Cond1) ? bits | 1 : bits & ~1;
            if (row.RequiredPlayerLevel > 0)
                bits = player.Level >= row.RequiredPlayerLevel ? bits | 4 : bits & ~4;
            if (bits == old) continue;
            player.SetTask(CoffeeTaskGroup, row.Id, bits);
            changes.Add(new(Player.ComposeTaskId(CoffeeTaskGroup, row.Id), bits));
        }
        return changes;
    }

    internal static bool CoffeeStagePassed(Player player, string condition)
    {
        var parts = condition.Trim().Trim('[', ']').Split('-');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int chapter) ||
            !int.TryParse(parts[1], out int index) || !int.TryParse(parts[2], out int diff) ||
            chapter <= 0 || index <= 0 || diff <= 0) return false;
        return MapList.PassCount(player.GetLevelStar(MapList.MapId(chapter, index, diff))) > 0;
    }

    internal static void LimitDailyEarn(int[] baseEarn, int[] extraEarn, int slot, int used, int limit)
    {
        long remaining = Math.Max(0L, (long)limit - used);
        baseEarn[slot] = (int)Math.Min(Math.Max(0, baseEarn[slot]), remaining);
        remaining -= baseEarn[slot];
        extraEarn[slot] = (int)Math.Min(Math.Max(0, extraEarn[slot]), remaining);
    }

    private void SettleElapsedSeats(ClientSession session, Player player)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var state = CafeFeature.ReadState(player, _cafe);
        var content = CafeFeature.ReadContent(player, state, _cafe,
            CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow), session.Id);
        foreach (var seat in content.Seats)
        {
            if (seat.CustomerIdx is not int idx || seat.StartTime <= 0 || seat.StartTime > now ||
                !_cafe.Coffees.TryGetValue(seat.CoffeeType, out var coffee) || coffee.Time <= 0 ||
                now - seat.StartTime < coffee.Time) continue;
            SettleDrink(session, DrinkOffCoffeeCmd, new JsonObject { ["customeridx"] = idx }, false);
        }
    }

    // ================= 115 生成排队顾客 =================

    /// <summary>
    /// 115：生成 1 个排队顾客。上行**无参数**（真机实测 `tbParam` 是空数组）。
    ///
    /// 顾客按 <c>NCafeGuests.txt</c> 的 <c>Weight</c> 加权随机，且过滤掉
    /// <c>NeedLv</c> 高于当前咖啡馆等级的（与 `CafeFeature.RollDailyGuests` 同一口径）。
    /// </summary>
    private Task OnGenCustomer(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var state = CafeFeature.ReadState(player, _cafe);
        var utcNow = DateTimeOffset.UtcNow;
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(utcNow), session.Id);

        var queue = content.Queue.ToList();
        long now = utcNow.ToUnixTimeSeconds();

        var picked = PickGuest(player, state.Level, content.Weights);
        if (picked is null)
        {
            // 无符合解锁与权重条件的顾客时，保留现有队列。
            Reply(session, cmd, new JsonObject
            {
                ["basetime"] = now,
                ["customerqueue"] = BuildQueue(queue),
            });
            Log.Warn($"[session {session.Id}] Cafe_GenCustomer：等级 {state.Level} 下没有可用顾客模板，保留当前队列");
            return Task.CompletedTask;
        }

        int cap = QueueCapFor(state.Level);
        if (queue.Count >= cap)
        {
            // 队列已满：不再新增，原样回当前队列（客户端会重建本地队列）
            Log.Info($"[session {session.Id}] Cafe_GenCustomer 队列已满" +
                     $"（{queue.Count}/{cap}，上限来自 NCafeLevels.MaxQSize），原样回发");
        }
        else
        {
            long previous = player.Extra.TryGetValue("cafeCustomerSequence", out var sequence) &&
                sequence is JsonValue seq && seq.TryGetValue<int>(out var savedSequence) ? savedSequence : 0;
            long nextIndex = Math.Max(previous + 1, NextCustomerIdx(content, queue));
            if (nextIndex >= int.MaxValue) return Task.CompletedTask;
            int idx = (int)nextIndex;
            player.Extra["cafeCustomerSequence"] = idx;
            queue.Add(new CafeFeature.CafeQueueGuest(picked.Id, idx, now));
        }

        var next = CloneWith(content, queue: queue);
        WriteMoodState(session, player, state, next);
        _ctx.Store.Save(player);

        Reply(session, cmd, new JsonObject
        {
            // ★ basetime 必给：没有它客户端整段跳过（NCafeNet.lua:230）
            ["basetime"] = now,
            ["customerqueue"] = BuildQueue(queue),
        });

        Log.Info($"[session {session.Id}] Cafe_GenCustomer → 新顾客 type={picked.Id}" +
                 $"（{picked.Star}★ NeedLv={picked.NeedLv}），队列 {queue.Count}/{cap}，basetime={now}");
        return Task.CompletedTask;
    }

    internal CafeGuestRow? PickGuest(Player player, int level,
        IReadOnlyList<CafeFeature.CafeGuestWeight> weights)
    {
        var pool = EligibleGuestWeights(_cafe, player, level, weights);
        long total = pool.Sum(w => (long)w.Weight);
        if (total <= 0) return null;
        long roll = Random.Shared.NextInt64(total);
        foreach (var entry in pool)
        {
            roll -= entry.Weight;
            if (roll < 0) return _cafe.Guests[entry.Id];
        }
        return null;
    }

    internal static List<CafeFeature.CafeGuestWeight> EligibleGuestWeights(CafeTable cafe,
        Player player, int level, IReadOnlyList<CafeFeature.CafeGuestWeight> weights)
        => weights.Where(w => w.Weight > 0 && cafe.Guests.TryGetValue(w.Id, out var g) &&
            CafeFeature.IsGuestUnlocked(g, player, level))
            .GroupBy(w => w.Id).Select(g => g.First()).ToList();

    /// <summary>
    /// 分配一个不与「已在座位上」和「已在队列里」重复的 <c>customeridx</c>。
    /// 客户端拿它当顾客实例的 Guid（`NCafeNet.lua:193` <c>tb.customeridx = o.guest.Guid</c>），
    /// 重复会让两个顾客互相顶掉。
    /// </summary>
    internal static int NextCustomerIdx(CafeFeature.CafeContent content,
                                        IEnumerable<CafeFeature.CafeQueueGuest> queue)
    {
        int max = 0;
        foreach (var s in content.Seats)
            if (s.CustomerIdx is int v && v > max)
                max = v;
        foreach (var q in queue)
            if (q.CustomerIdx > max)
                max = q.CustomerIdx;
        return max == int.MaxValue ? int.MaxValue : max + 1;
    }

    // Settle the old guest/assignment state before mutating it, then establish the
    // new mode's clock. Guest arrival/departure cannot retroactively charge time.
    private void WriteMoodState(ClientSession session, Player player, CafeFeature.CafeState state,
        CafeFeature.CafeContent? content = null, bool notify = true)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _ctx.PledgeTimes.Refresh(player, now);
        _mood.Refresh(session, player, now, notify);
        CafeFeature.WriteState(player, state, content);
        _mood.Refresh(session, player, now, notify);
        _ctx.PledgeTimes.Refresh(player, now);
    }

    // ================= 114 座位上报 =================

    /// <summary>
    /// 114：客户端把它从餐位家具的 <c>biz_</c> 挂点算出的座位上报，服务端只负责存。
    /// 上行 <c>{ seatlist = [ {position, [customeridx, customertype, coffeetype, starttime]} … ] }</c>。
    /// ★ 回包内容不被读（`NCafeNet.lua:204`），但**必须回**，否则客户端不会继续
    /// <c>ReqNewCustomer</c>（顾客永远不入座）。
    /// </summary>
    private Task OnUpdateSeatList(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var state = CafeFeature.ReadState(player, _cafe);
        var utcNow = DateTimeOffset.UtcNow;
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(utcNow), session.Id);

        var seats = new List<CafeFeature.CafeSeat>();
        // ★ 不能直接 param?["seatlist"] —— JsonNode 的索引器对 **JsonArray** 会抛
        // InvalidOperationException（而 115 的真机上行就是空数组 `[]`）。
        if (param is JsonObject req && req["seatlist"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o)
                    continue;
                int pos = ReadInt(o["position"]);
                if (pos <= 0)
                    continue;                       // position 是座位 guid，0 无意义

                // ★ 有没有顾客，看 customeridx 这个键在不在（客户端没顾客时整个省略它）
                int? idx = o["customeridx"] is null ? null : ReadInt(o["customeridx"]);
                int type = ReadInt(o["customertype"]);
                // 顾客类型必须合法，否则下次 112 发回去会让客户端 LoadGuest NRE、卡 0.75
                if (idx is not null && !_cafe.Guests.ContainsKey(type))
                {
                    Log.Warn($"[session {session.Id}] Cafe_UpdateSeatList 座位 {pos} 的 customertype={type} " +
                             "不在 NCafeGuests.txt 里，按空座位存（否则下次进店会卡加载 0.75）");
                    idx = null;
                    type = 0;
                }

                seats.Add(new CafeFeature.CafeSeat(
                    pos,
                    idx,
                    idx is null ? 0 : type,
                    idx is null ? 0 : ReadInt(o["coffeetype"]),
                    idx is null ? 0 : ReadLong(o["starttime"])));
            }
        }

        var next = CloneWith(content, seats: seats);
        WriteMoodState(session, player, state, next);
        _ctx.Store.Save(player);

        int occupied = seats.Count(s => s.CustomerIdx is not null);
        // 回执：客户端不读内容，回个空对象即可（但不能不回）
        Reply(session, cmd, new JsonObject());
        Log.Info($"[session {session.Id}] Cafe_UpdateSeatList → 存 {seats.Count} 个座位（有人 {occupied}）");
        return Task.CompletedTask;
    }

    // ================= 113 排班少女 =================

    /// <summary>
    /// 113：设置三个区的少女。上行与下行都是**数组的数组**
    /// （`LuaCall.lua:1613` 注释 <c>[[waiterid,waiterid], [roomgirlid,roomgirlid]]</c>）。
    /// 下行每个少女是 <c>[girlId, modelId]</c> 二元数组（`NCafeNet.lua:386`）。
    /// </summary>
    private Task OnSetWaiterList(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            void ReplyCurrent()
            {
                var state = CafeFeature.ReadState(player, _cafe);
                var content = CafeFeature.ReadContent(player, state, _cafe, CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow));
                _ctx.LuaCalls.CallClient(session, cmd, CafeFeature.BuildRoomGirlsList(content.AreaGirls));
            }
            try { _ctx.PledgeTimes.SaveRefresh(_ctx.Store, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
            catch (Exception) { ReplyCurrent(); return Task.CompletedTask; }
            var before = new ChallengeMutationSnapshot(player);
            try { return SetWaiterListLocked(session, cmd, param); }
            catch (Exception error)
            {
                before.Restore(player);
                Log.Warn($"[cafe] 排班保存失败，排班、计时和心情已回滚：{error.Message}");
                ReplyCurrent();
                return Task.CompletedTask;
            }
        }
    }

    private Task SetWaiterListLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var oldMood = player.Girls.ToDictionary(g => g.Girlid, g => g.Moodvalue);
        var oldClocks = player.Tasks.Where(p => p.Key >> 16 == 41).ToDictionary(p => p.Key, p => p.Value);

        var state = CafeFeature.ReadState(player, _cafe);
        var utcNow = DateTimeOffset.UtcNow;
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(utcNow), session.Id);

        var areas = new List<IReadOnlyList<CafeFeature.CafeAreaGirl>>();
        for (int i = 0; i < CafeFeature.AreaCount; i++)
        {
            var list = new List<CafeFeature.CafeAreaGirl>();
            if (param is JsonArray outer && i < outer.Count && outer[i] is JsonArray inner)
            {
                foreach (var g in inner)
                {
                    // 上行可能是裸 girlId，也可能是 [girlId, modelId]
                    int girlId, modelId;
                    if (g is JsonArray pair && pair.Count > 0)
                    {
                        girlId = ReadInt(pair[0]);
                        modelId = pair.Count > 1 ? ReadInt(pair[1]) : 0;
                    }
                    else
                    {
                        girlId = ReadInt(g);
                        modelId = 0;
                    }
                    if (girlId <= 0)
                        continue;
                    // 只认玩家真正拥有的少女（客户端 ReqSetWaiterList 也会先 GetGirl 过滤）
                    if (!player.Girls.Any(x => x.Girlid == (uint)girlId))
                    {
                        Log.Warn($"[session {session.Id}] Cafe_SetWaiterList 少女 {girlId} 玩家未持有，忽略");
                        continue;
                    }
                    list.Add(new CafeFeature.CafeAreaGirl(girlId, modelId));
                }
            }
            areas.Add(list);
        }

        areas = CafeFeature.ReadAreaGirls(CafeFeature.BuildRoomGirlsList(areas), player,
            _cafe.LevelRowFor(state.Level), session.Id);
        var next = CloneWith(content, areaGirls: areas);
        WriteMoodState(session, player, state, next, notify: false);
        _ctx.Store.SaveRequired(player);
        foreach (var girl in player.Girls)
            if (oldMood.GetValueOrDefault(girl.Girlid) != girl.Moodvalue) GameNotify.PushGirl(session, girl);
        GameNotify.PushTaskValues(session, player.Tasks.Where(p => p.Key >> 16 == 41
            && oldClocks.GetValueOrDefault(p.Key) != p.Value));

        // ★ 回包必须是三项的数组的数组，内层每个少女是 [girlId, modelId]
        var reply = new JsonArray();
        foreach (var area in areas)
        {
            var a = new JsonArray();
            foreach (var g in area)
                a.Add(new JsonArray { g.GirlId, g.ModelId });
            reply.Add(a);
        }
        _ctx.LuaCalls.CallClient(session, cmd, reply);

        Log.Info($"[session {session.Id}] Cafe_SetWaiterList → 经营{areas[0].Count}/休息{areas[1].Count}/宿舍{areas[2].Count} 位少女");
        return Task.CompletedTask;
    }

    // ================= 119 制作咖啡 =================

    /// <summary>
    /// 119：制作咖啡加到橱窗。上行 <c>{ coffeetype, count }</c>，下行 <c>{ coffeelist }</c>。
    /// ★ 成功回包**绝不能带 <c>nError</c>**（`LuaCall.lua:1727` 是真值判断）。
    /// ★ <c>coffeetype</c> 必须在 <c>CoffeeList.txt</c> 里，否则客户端
    /// <c>template.Time</c> 会 nil 解引用（`:1738`）。
    /// </summary>
    private Task OnMakeCoffee(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var state = CafeFeature.ReadState(player, _cafe);
        var utcNow = DateTimeOffset.UtcNow;
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(utcNow), session.Id);

        // 同上：只在 JsonObject 上取字段
        var body = param as JsonObject;
        int type = ReadInt(body?["coffeetype"]);
        if (body?["count"] is not JsonValue countNode ||
            !countNode.TryGetValue<int>(out int count) || count <= 0)
            return Fail(session, cmd, 20062);

        if (!_cafe.Coffees.ContainsKey(type))
        {
            // 非法类型：回 nError 让客户端弹提示（这一族**失败**时才带 nError）
            Log.Warn($"[session {session.Id}] Cafe_MakeCoffee coffeetype={type} 不在 CoffeeList.txt" +
                     $"（现网 {_cafe.Coffees.Count} 种），拒绝");
            Reply(session, cmd, new JsonObject { ["nError"] = 1 });
            return Task.CompletedTask;
        }

        // Only bit 8 means learned; condition flags in bits 0..2 do not permit production.
        if ((player.GetTask(CoffeeTaskGroup, type) & (1 << CoffeeLearnedBit)) == 0)
            return Fail(session, cmd, ErrCoffeePrevNotLearned);

        int maxKinds = Math.Clamp(_cafe.LevelRowFor(state.Level)?.SlotCoffee ?? 1, 0, MaxCoffeeKinds);
        var coffees = content.Coffees.ToList();
        int at = coffees.FindIndex(c => c.CoffeeType == type);
        long existing = at >= 0 ? coffees[at].Count : 0;
        int maxCups = _cafe.LevelRowFor(state.Level)?.MaxCoffee ?? 0;
        if (maxCups <= 0 || existing + count > maxCups)
            return Fail(session, cmd, 20062);
        long cost = (long)_cafe.Coffees[type].Cost * count;
        if (existing + count > int.MaxValue || cost < 0 || cost > player.GetMoney(GoldMoneyId))
            return Fail(session, cmd, 20012);
        if (at >= 0)
            coffees[at] = new CafeFeature.CafeCoffee(type, coffees[at].Count + count);
        else if (coffees.Count < maxKinds)
            coffees.Add(new CafeFeature.CafeCoffee(type, count));
        else
        {
            Log.Warn($"[session {session.Id}] Cafe_MakeCoffee 橱窗已有 {coffees.Count} 种" +
                     $"（当前等级上限 {maxKinds}），拒绝新种类 {type}");
            Reply(session, cmd, new JsonObject { ["nError"] = 1 });
            return Task.CompletedTask;
        }

        var next = CloneWith(content, coffees: coffees);
        player.AddMoney(GoldMoneyId, -(int)cost);
        WriteMoodState(session, player, state, next);
        _ctx.Store.Save(player);
        GameNotify.PushMoney(session, player, GoldMoneyId);

        // ★ 成功：只发 coffeelist，不带 nError
        var list = new JsonArray();
        foreach (var c in coffees)
            list.Add(new JsonObject { ["coffeetype"] = c.CoffeeType, ["count"] = c.Count });
        Reply(session, cmd, new JsonObject { ["coffeelist"] = list });

        Log.Info($"[session {session.Id}] Cafe_MakeCoffee 咖啡 {type}×{count} → 橱窗 {coffees.Count} 种" +
                 $"（合计 {coffees.Sum(c => c.Count)} 杯）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 118：清除橱窗里某一种咖啡（「清理橱窗」按钮）。
    ///
    /// 上行 <c>{ coffeetype }</c>（<c>UI_CoffeeMake.lua:264</c>，只在
    /// <c>UI_CoffeeCleanTips</c> 的回调里 <c>tbInfo == nil</c> 那支发出，
    /// 即玩家确认「清空这个栏位」而不是换一种咖啡）。
    ///
    /// 下行与 119 完全同构：<c>{ coffeelist }</c>，**全量覆盖**语义 ——
    /// 客户端 <c>LuaCall.lua</c> 的成功分支先 <c>ClearCoffeeItems()</c> 再遍历重建
    /// ⇒ 回包必须给**清除之后的完整橱窗**，不是「被删掉的那一项」。
    ///
    /// ★ 与 119 同一个坑：遍历里 <c>CoffeeCommon:GetCoffeeListConfig(v.coffeetype)</c>
    /// 之后**直接取 <c>template.Time</c>** ⇒ 下发的每个 <c>coffeetype</c>
    /// 都必须是 <c>CoffeeList.txt</c> 里的合法值。
    /// ★ 错误约定同 119（LuaCall 数字族）：成功**绝不能带 <c>nError</c>**。
    /// </summary>
    private Task OnRemoveCoffee(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var state = CafeFeature.ReadState(player, _cafe);
        var utcNow = DateTimeOffset.UtcNow;
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(utcNow), session.Id);

        // ★★ 只在 JsonObject 上取字段（JsonNode["key"] 对 JsonArray 会抛）
        var body = param as JsonObject;
        int type = ReadInt(body?["coffeetype"]);

        int maxKinds = Math.Clamp(_cafe.LevelRowFor(state.Level)?.SlotCoffee ?? 1, 0, MaxCoffeeKinds);
        var coffees = content.Coffees.ToList();
        int at = coffees.FindIndex(c => c.CoffeeType == type);
        if (at < 0)
        {
            // 橱窗里本来就没有这一种：回 nError 让客户端弹提示。
            // ★ 不能静默成功 —— 那会让客户端以为清掉了、界面与服务端不一致。
            Log.Warn($"[session {session.Id}] Cafe_RemoveCoffee coffeetype={type} 不在橱窗里"
                   + $"（现有 {coffees.Count} 种），拒绝");
            Reply(session, cmd, new JsonObject { ["nError"] = 1 });
            return Task.CompletedTask;
        }

        int removed = coffees[at].Count;
        coffees.RemoveAt(at);

        var next = CloneWith(content, coffees: coffees);
        WriteMoodState(session, player, state, next);
        _ctx.Store.Save(player);

        // ★ 成功：回**清除之后**的完整橱窗，不带 nError
        var list = new JsonArray();
        foreach (var c in coffees)
            list.Add(new JsonObject { ["coffeetype"] = c.CoffeeType, ["count"] = c.Count });
        Reply(session, cmd, new JsonObject { ["coffeelist"] = list });

        Log.Info($"[session {session.Id}] Cafe_RemoveCoffee 清除咖啡 {type}（{removed} 杯）"
               + $" → 橱窗剩 {coffees.Count} 种（合计 {coffees.Sum(c => c.Count)} 杯）");
        return Task.CompletedTask;
    }

    // ================= 工具 =================

    /// <summary>把队列转成下行数组。★ 三个字段都必填（`AddGuest2Queue` 无 nil 保护）。</summary>
    internal static JsonArray BuildQueue(IEnumerable<CafeFeature.CafeQueueGuest> queue)
    {
        var arr = new JsonArray();
        foreach (var g in queue)
        {
            arr.Add(new JsonObject
            {
                ["customertype"] = g.CustomerType,
                ["customeridx"] = g.CustomerIdx,
                ["starttime"] = g.StartTime,
            });
        }
        return arr;
    }

    /// <summary><see cref="CafeFeature.CafeContent"/> 是 init-only 的，改一组要整体重建。</summary>
    internal static CafeFeature.CafeContent CloneWith(
        CafeFeature.CafeContent src,
        IReadOnlyList<CafeFeature.CafeSeat>? seats = null,
        IReadOnlyList<CafeFeature.CafeQueueGuest>? queue = null,
        IReadOnlyList<CafeFeature.CafeCoffee>? coffees = null,
        IReadOnlyList<IReadOnlyList<CafeFeature.CafeAreaGirl>>? areaGirls = null)
        => new()
        {
            Seats = seats ?? src.Seats,
            Queue = queue ?? src.Queue,
            Coffees = coffees ?? src.Coffees,
            AreaGirls = areaGirls ?? src.AreaGirls,
            Weights = src.Weights,
            Visitors = src.Visitors,
            WeightDay = src.WeightDay,
        };

    // =================================================================
    //  111 Cafe_LevelUp —— 咖啡馆升级
    // =================================================================

    /// <summary>
    /// LuaCall 111 咖啡馆升级。上行 <b>无参数</b>（`LuaCall.lua:1530` 注释 `tbParam = nil`）。
    ///
    /// <para><b>★ 回包契约（`LuaCall.lua:1528..1540`）</b></para>
    /// <list type="bullet">
    ///   <item>失败：<c>if tbParam.nError then ShowMsgByKey('ui.Error.' .. nError)</c>
    ///     —— **真值判断**，所以成功时绝不能带 <c>nError</c>（LuaCall 数字族通用约定）。</item>
    ///   <item>成功：<c>elseif tbParam.level then</c> ⇒ **必须带 <c>level</c>**，
    ///     客户端随后弹 <c>ui.UI_CafeInfo.LevelUpSuccess</c>。</item>
    /// </list>
    ///
    /// <para><b>★ 升级条件与客户端预检同源</b></para>
    /// <c>CafeLogic:CheckCafeCanLevelUp</c>（`CafeLogic.lua:453..475`）的四个判定
    /// 逐条复刻（满级 1 / 无下一级配置 2 / 金币不足 3 / 人气不足 4），
    /// 并**额外**补一条玩家等级门槛（<c>NeedLevel</c> 列，码 5）——
    /// 客户端预检没查它，但配置表有这一列，服务端把关更严不会有副作用。
    ///
    /// ★ <c>NeedHot</c> 只在 <b>&gt; 0</b> 时才检查（`CafeLogic.lua:466` 的
    /// <c>NextLevelCfg.NeedHot > 0 and ...</c>）—— L1→L2 的 NeedHot 是 20，但
    /// 表里存在 NeedHot=0 的行，那种情况下不拦。
    ///
    /// ★★ 升级**只扣金币、不扣人气**：`CafeLogic.lua:466` 对人气是「门槛判断」
    /// （<c>Hot &lt; NeedHot</c> 才拒），没有任何扣减动作；金币则由 `:471` 比较、
    /// 服务端这里实扣。人气的消耗途径是另一条命令（135 满级兑换）。
    ///
    /// ★ 升级成功后**必须推 pf 1029 更新货币**，否则客户端金币显示不变
    /// （与商店购买同一条规矩）。等级本身通过本命令的回包 <c>level</c> 送达，
    /// 客户端 `NCafeNet.lua:59` 会写进 <c>NCafe.Data.CafeLevel</c>。
    /// </summary>
    private Task OnLevelUp(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_LevelUp({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var state = CafeFeature.ReadState(player, _cafe);
        int cur = state.Level;
        int next = cur + 1;

        // ① 已达可下发上限（CafeLogic.lua:456）
        if (cur >= _cafe.MaxCafeLevel)
        {
            Log.Info($"[session {session.Id}] 咖啡馆升级被拒：已达上限 lv={cur}/{_cafe.MaxCafeLevel}");
            return Fail(session, cmd, ErrAlreadyMaxLevel);
        }

        // ② 下一级没有配置行（CafeLogic.lua:461）
        var row = _cafe.LevelRowFor(next);
        if (row is null)
        {
            Log.Info($"[session {session.Id}] 咖啡馆升级被拒：lv={next} 无配置行（NCafeLevels.txt）");
            return Fail(session, cmd, ErrNoNextLevelCfg);
        }

        // ③ 玩家等级门槛（NeedLevel 列 —— 客户端预检没查，服务端补）
        if (row.NeedLevel > 0 && player.Level < row.NeedLevel)
        {
            Log.Info($"[session {session.Id}] 咖啡馆升级被拒：玩家 LV.{player.Level} < 需求 {row.NeedLevel}");
            return Fail(session, cmd, ErrNotEnoughPlayerLevel);
        }

        // ④ 人气门槛 —— ★ 仅 NeedHot > 0 时检查（CafeLogic.lua:466）
        if (row.NeedHot > 0 && state.Hot < row.NeedHot)
        {
            Log.Info($"[session {session.Id}] 咖啡馆升级被拒：人气 {state.Hot} < 需求 {row.NeedHot}");
            return Fail(session, cmd, ErrNotEnoughHot);
        }

        // ⑤ 金币（CafeLogic.lua:471）
        int gold = player.GetMoney(GoldMoneyId);
        if (gold < row.NeedGold)
        {
            Log.Info($"[session {session.Id}] 咖啡馆升级被拒：金币 {gold} < 需求 {row.NeedGold}");
            return Fail(session, cmd, ErrNotEnoughGold);
        }

        // ---- 通过：扣钱、写等级 ----
        if (row.NeedGold > 0)
            player.AddMoney(GoldMoneyId, -row.NeedGold);
        var inventory = HouseFeature.ReadOrCreate(player, _house, session.Id);
        var upgraded = state with
        {
            Level = next,
            Comfort = HouseComfort.Calculate(_house, inventory, next, _cafe.MaxComfort)
        };
        WriteMoodState(session, player, upgraded);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 咖啡馆升级 lv={cur} → {next}" +
                 $"（消耗金币 {row.NeedGold} → 余 {player.GetMoney(GoldMoneyId)}；" +
                 $"人气门槛 {row.NeedHot}/当前 {state.Hot}、玩家等级门槛 {row.NeedLevel}/LV.{player.Level}）");

        // ★ 先推货币变更（pf 1029），再回业务包 —— 与商店/养成域同一条时序规矩
        GameNotify.PushMoney(session, player, GoldMoneyId);

        // LuaCall.lua's level-up callback applies comfort, including zero.
        Reply(session, cmd, new JsonObject { ["level"] = next, ["comfort"] = upgraded.Comfort });
        return Task.CompletedTask;
    }

    // =================================================================
    //  116 Cafe_DrinkOffCoffee —— 顾客喝完咖啡结算
    // =================================================================

    /// <summary>
    /// LuaCall 116 顾客喝完结算。上行 <c>{"customeridx": N}</c>
    /// （`LuaCall.lua:1649` 注释 `tbParam = { customeridx = ...,}`）。
    ///
    /// <para><b>★★★ 回包的三条硬约束（消费点 `NCafeNet.lua:320..342`）</b></para>
    /// <code>
    /// function NCafeNet.RspEndCustomer(tbParam)
    ///     if (tbParam.customeridx and tbParam.earn_base and tbParam.earn_ex) then   -- ★ 判据
    ///         guest:SetEarn(earn_base[1], earn_base[2], earn_base[3],
    ///                       earn_ex[1],  earn_ex[2],  earn_ex[3])                   -- ★ 各取 3 个下标
    ///         guest.State = (earn_ex 三项之和 > 0) and 'S_AwardLeave' or 'S_Leave'
    ///         guest:AiThink()
    ///         NCafeNet.ReqCustomerQueue()          -- ★ 离场后自动请求下一位 ⇒ 循环闭合
    ///         NCafe.Data.Hot = tbParam.hot
    ///         CoffeeCommon.AddCacheGold(earn_base[1] + earn_ex[1])
    ///     end
    /// end
    /// </code>
    /// <list type="number">
    ///   <item><c>customeridx</c> + <c>earn_base</c> + <c>earn_ex</c> **三者缺一整段跳过**
    ///     ⇒ 顾客永远不离场、`ReqCustomerQueue` 不触发 ⇒ **经营循环卡死**。</item>
    ///   <item>两个 earn **必须是三元素数组** <c>[金币, 咖啡币, 人气]</c>
    ///     —— 客户端逐个取 <c>[1]/[2]/[3]</c>。</item>
    ///   <item><c>hot</c> **必填**（`:338` 无条件赋值；Lua 里 0 也为真，但 nil 会污染客户端状态）。</item>
    /// </list>
    ///
    /// ★ <c>error</c> 字段**客户端压根没读** —— `LuaCall.lua:1654` 那段
    /// （`error = 0 / -1 / -2`）只是原厂留下的**服务端返回形状注释**。
    /// 所以本族没有错误通道：要么回正确形状，要么**不回包**（本实现选后者，并记日志）。
    ///
    /// <para><b>★ 收益公式（全部来自配置表，不猜）</b></para>
    /// <list type="bullet">
    ///   <item>基础收益取该座位上**那杯咖啡**的 <c>AwardA/AwardB/AwardC</c>
    ///     （`CoffeeList.txt`「匹配收益 Base 晶币/咖啡币/人气」）。</item>
    ///   <item>**匹配**判定：<c>coffee[顾客.Prefer] &gt;= 顾客.Preferfactor</c>
    ///     （取证 `CoffeeCommon.lua:499`）。不匹配则只给基础收益的一半（向下取整，
    ///     ★ 这一步是**本实现的取舍** —— 原厂「不匹配」的确切倍率未取证，
    ///     取半是为了让「选对咖啡」有正反馈且不为 0）。</item>
    ///   <item>附加收益 <c>earn_ex</c> 按顾客的 <c>Rate1/2/3</c>（百分比概率）判定，
    ///     命中则给对应的 <c>RateA/RateB/RateC</c>。1★ 顾客三个 Rate 全 0 ⇒ 恒无附加。</item>
    /// </list>
    ///
    /// ★★ 服务端**实发**金币与咖啡币（`AddMoney` + 推 pf 1032）、**实加**人气写存档。
    /// 客户端那边 `AddCacheGold` 只是本地预显示，真值仍以服务端为准。
    /// </summary>
    private Task OnDrinkOffCoffee(ClientSession session, int cmd, JsonNode? param)
        => SettleDrink(session, cmd, param, true);

    private Task SettleDrink(ClientSession session, int cmd, JsonNode? param, bool reply)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_DrinkOffCoffee({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        // ★ 上行可能是 JsonArray（真机上 115 就是空数组）⇒ 必须先判类型再取键，
        //   否则 JsonNode["key"] 对 JsonArray 会抛（docs/05 §16.4 的坑）。
        var obj = param as JsonObject;
        int idx = ReadInt(obj?["customeridx"], -1);
        if (idx < 0)
        {
            Log.Warn($"[session {session.Id}] Cafe_DrinkOffCoffee 缺 customeridx（param={param?.ToJsonString()}）—— 不回包");
            return Task.CompletedTask;
        }

        var state = CafeFeature.ReadState(player, _cafe);
        // ★ 第 4 个参数是 **dayIndex**（今日顾客按天重算），不是 sessionId ——
        //   传错会让每次调用都重算「今日顾客」，同一天内两次拿到不同集合。
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow), session.Id);

        // 找到这位顾客所在的座位（座位上带 CoffeeType = 他喝的那杯）
        var seat = content.Seats.FirstOrDefault(s => s.CustomerIdx == idx);
        if (seat is null)
        {
            // 对应原厂注释里的 error = -2「找不到顾客」。客户端没有错误分支 ⇒ 不回包。
            Log.Warn($"[session {session.Id}] Cafe_DrinkOffCoffee customeridx={idx} 不在任何座位上" +
                     $"（座位 {content.Seats.Count} 个）—— 不回包（对应原厂 error=-2）");
            return Task.CompletedTask;
        }

        var (earnBase, earnEx, guestStar) = ComputeEarn(seat, player.Roleid, idx);

        int settlementDay = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        var daily = player.Extra.TryGetValue("cafeDailyEarnings", out var dn) ? dn as JsonObject : null;
        bool sameDay = ReadInt(daily?["day"], int.MinValue) == settlementDay;
        int dailyGold = sameDay ? Math.Max(0, ReadInt(daily?["gold"])) : 0;
        int dailyCoin = sameDay ? Math.Max(0, ReadInt(daily?["coin"])) : 0;
        var limits = _cafe.LevelRowFor(state.Level);
        LimitDailyEarn(earnBase, earnEx, 0, dailyGold, limits?.DailyLmtGold ?? 0);
        LimitDailyEarn(earnBase, earnEx, 1, dailyCoin, limits?.DailyLmtCoffeeCoin ?? 0);

        LimitDailyEarn(earnBase, earnEx, 1,
            Math.Max(0, player.GetMoney(CoffeeCoinMoneyId)), limits?.CoffeeCoinMax ?? 0);
        var reportBefore = ReadReport(player);
        long[] reportTotals = {
            (long)reportBefore.BaseGold + earnBase[0], (long)reportBefore.BaseCoin + earnBase[1],
            (long)reportBefore.BaseHot + earnBase[2], (long)reportBefore.ExGold + earnEx[0],
            (long)reportBefore.ExCoin + earnEx[1], (long)reportBefore.ExHot + earnEx[2]
        };
        if (reportTotals.Any(v => v < 0 || v > int.MaxValue) ||
            reportBefore.OutList.GetValueOrDefault(seat.CoffeeType) == int.MaxValue)
            return Task.CompletedTask;

        // Validate the whole settlement before changing money, seats or report counters.
        long goldGain = (long)earnBase[0] + earnEx[0];
        long coinGain = (long)earnBase[1] + earnEx[1];
        long hotGain = (long)earnBase[2] + earnEx[2];
        if (goldGain < 0 || coinGain < 0 || hotGain < 0 ||
            (long)player.GetMoney(GoldMoneyId) + goldGain > int.MaxValue ||
            (long)player.GetMoney(CoffeeCoinMoneyId) + coinGain > int.MaxValue ||
            (long)state.Hot + hotGain > int.MaxValue)
            return Task.CompletedTask;

        if (goldGain > 0) player.AddMoney(GoldMoneyId, (int)goldGain);
        if (coinGain > 0) player.AddMoney(CoffeeCoinMoneyId, (int)coinGain);
        int newHot = (int)(state.Hot + hotGain);

        player.Extra["cafeDailyEarnings"] = new JsonObject
        {
            ["day"] = settlementDay, ["gold"] = (long)dailyGold + goldGain,
            ["coin"] = (long)dailyCoin + coinGain
        };

        // ---- 顾客离场：清空该座位、消耗那杯咖啡 ----
        var seats = content.Seats.ToList();
        seats[seats.IndexOf(seat)] = seat with { CustomerIdx = null, CustomerType = 0, CoffeeType = 0, StartTime = 0 };
        // ★ CafeContent 是 init-only ⇒ 用 CloneWith 整体重建（不能直接赋值）
        var updated = CloneWith(content, seats: seats);

        // ★ 累加进营业报告（122 纯下行时用）
        AccumulateReport(player, seat.CoffeeType, earnBase, earnEx);

        WriteMoodState(session, player, state with { Hot = newHot }, updated);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 顾客喝完结算 customeridx={idx}（{guestStar}★、座位 {seat.Position}、" +
                 $"咖啡 {seat.CoffeeType}）：基础 [金 {earnBase[0]}/咖啡币 {earnBase[1]}/人气 {earnBase[2]}]、" +
                 $"附加 [{earnEx[0]}/{earnEx[1]}/{earnEx[2]}]；人气 {state.Hot} → {newHot}");

        // ★ 先推货币变更，再回业务包
        GameNotify.PushMoney(session, player, new[] { GoldMoneyId, CoffeeCoinMoneyId });

        // ★★ 三个字段缺一不可，且两个 earn 必须是三元素数组
        if (reply) Reply(session, cmd, new JsonObject
        {
            ["customeridx"] = idx,
            ["earn_base"] = new JsonArray { earnBase[0], earnBase[1], earnBase[2] },
            ["earn_ex"] = new JsonArray { earnEx[0], earnEx[1], earnEx[2] },
            ["hot"] = newHot,
        });
        return Task.CompletedTask;
    }

    // =================================================================
    //  135 / 136 / 139 三种兑换
    // =================================================================

    /// <summary>
    /// LuaCall 135「满级咖啡馆人气值兑换」。
    ///
    /// <para><b>★ 回包判据：必须带 <c>hot</c>（兑换后的新值）</b></para>
    /// 消费点 `NCafeNet.RspCafeHotData`（`NCafeNet.lua:44..50`）：
    /// <code>
    /// if tbParam and tbParam.hot then
    ///     NCafe.Data.Hot = tbParam.hot            -- ★ 直接赋值
    ///     UI.Call("UI_CafeInfo", "OnGetMaxReward")
    /// end
    /// </code>
    /// ⇒ 没有 <c>hot</c> 整段跳过（界面不刷新）；**本族无错误通道**，失败不回包。
    ///
    /// <para><b>★★ 兑换条件与奖励全部来自「Level = 10001」这一行</b></para>
    /// 取证 `CafeLogic:GetMaxRewardState`（`CafeLogic.lua:785..800`）：
    /// <code>
    /// local NextLevelCfg = CafeLogic:GetCafeLvCfg(CafeLevel + 1)
    /// if (not NextLevelCfg) then                            -- ★ 必须已满级
    ///     local data = CafeLogic:GetCafeLvCfg(CafeLogic.MaxRewardLvId)   -- MaxRewardLvId = 10001（:44）
    ///     if (Game.NCafeManager.Data.Hot >= data.NeedHot) then nFlag = 2 end   -- 可领
    /// </code>
    /// 现网 10001 行：<c>NeedHot = 30000</c>、<c>LevelAwards = [8-2-1-3-1]</c>。
    /// ⇒ 兑换 = **扣 30000 人气 + 发 LevelAwards**，可重复（只要人气够）。
    /// </summary>
    private Task OnGetMaxHotReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_GetMaxHotReward({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        var state = CafeFeature.ReadState(player, _cafe);

        // ① 必须已满级
        //
        // ★★ 判「有没有下一级」**不能用 LevelRowFor** —— 它对缺失等级会**兜底返回
        //    Level 1 那一行**（`CafeTable.cs:637`），于是 `LevelRowFor(31)` 也非 null，
        //    条件永远成立、兑换永远被拒（本轮踩过：测试报「没有任何下行」）。
        // ⇒ 用 `CafeLevels` 字典**精确**查键。
        if (_cafe.CafeLevels.ContainsKey(state.Level + 1))
        {
            Log.Info($"[session {session.Id}] 满级人气兑换被拒：lv={state.Level} 未满级" +
                     $"（表里存在 lv={state.Level + 1}）—— 不回包");
            return Task.CompletedTask;
        }

        // ② 取 Level=10001 的专用奖励行
        _cafe.CafeLevels.TryGetValue(CafeTable.MaxRewardLevelId, out var cfg);
        if (cfg is null)
        {
            Log.Warn($"[session {session.Id}] 满级人气兑换：NCafeLevels.txt 缺 Level={CafeTable.MaxRewardLevelId} 行 —— 不回包");
            return Task.CompletedTask;
        }

        // ③ 人气门槛
        if (cfg.NeedHot <= 0 || state.Hot < cfg.NeedHot)
        {
            Log.Info($"[session {session.Id}] 满级人气兑换被拒：人气 {state.Hot} < 需求 {cfg.NeedHot} —— 不回包");
            return Task.CompletedTask;
        }

        int newHot = state.Hot - cfg.NeedHot;
        WriteMoodState(session, player, state with { Hot = newHot });

        var granted = _ctx.Rewards.Grant(player, _cafe.MaxRewardAwards, RewardSource.Extra);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 满级人气兑换：人气 {state.Hot} → {newHot}" +
                 $"（扣 {cfg.NeedHot}）、发出 {granted.Rewards.Count} 项（{granted.Describe()}）");

        GameNotify.PushRewards(session, player, granted);

        // ★ 必须带 hot（新值）
        Reply(session, cmd, new JsonObject { ["hot"] = newHot });
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 136「少女生日奖励」。
    ///
    /// <para><b>★★★ 第八种错误约定：失败必须<u>完全不回包</u></b></para>
    /// 消费点 `NCafeNet.RspGetBirthdayPresent`（`NCafeNet.lua:439..449`）：
    /// <code>
    /// local nRet = tbParam.nRet;
    /// if (nRet and nRet == 0) then
    ///     local tbData = GirlCommon.GetBirthdayGirl()
    ///     if (tbData) then UI.OpenAsChild("UI_RewardNormal", tbData.tbAward) end
    /// else
    ///     error("[NCafeNet.RspGetBirthdayPresent] 领取生日奖励失败")   -- ★★ 直接抛
    /// end
    /// </code>
    /// ⇒ 成功必须回 <c>{nRet = 0}</c>（**数值 0**，不是省略）；
    /// **失败绝不能回包** —— 回任何 <c>nRet != 0</c> 都会让客户端 <c>error()</c>
    /// （`LuaCall.lua:1586..1587` 的原厂注释也写明「正常返回 {nRet=0}、**错误情况没有返回值**」）。
    ///
    /// <para><b>★ 奖励内容由客户端自己取</b></para>
    /// 客户端用 <c>GirlCommon.GetBirthdayGirl()</c> 拿当天生日少女的 <c>tbAward</c> 弹窗
    /// ⇒ **服务端不需要下发奖励清单**，但要**真发物品**（走 pf 1031/1032）。
    ///
    /// <para><b>★ 条件（与客户端 `GirlCommon.lua:1394..1408` 同源）</b></para>
    /// ① 今天是某少女生日（<c>GirlBirthday.txt</c> 的 <c>Month</c>/<c>Day</c>）；
    /// ② 玩家拥有该少女；③ 今年还没领过（★ 本服用任务值
    /// <c>(<see cref="BirthdayClaimGroup"/>, girlId)</c> 存年份，原厂位置未取证）。
    /// </summary>
    private Task OnGetBirthdayReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_GetBirthDayReward({cmd}) 未登录，忽略");
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var today = _cafe.Birthdays.FirstOrDefault(b => b.Month == now.Month && b.Day == now.Day);
        if (today is null)
        {
            Log.Info($"[session {session.Id}] 生日奖励：今天（{now.Month}/{now.Day}）没有少女过生日 —— " +
                     "★ 不回包（客户端 nRet != 0 会 error()）");
            return Task.CompletedTask;
        }

        // 玩家是否拥有该少女
        if (!player.Girls.Any(g => g.Girlid == (uint)today.GirlId))
        {
            Log.Info($"[session {session.Id}] 生日奖励：未拥有少女 {today.GirlId} —— 不回包");
            return Task.CompletedTask;
        }

        // 今年是否已领
        int claimed = player.GetTask(BirthdayClaimGroup, today.GirlId);
        if (claimed == now.Year)
        {
            Log.Info($"[session {session.Id}] 生日奖励：少女 {today.GirlId} 今年（{now.Year}）已领过 —— 不回包");
            return Task.CompletedTask;
        }

        var granted = _ctx.Rewards.Grant(player, today.Awards, RewardSource.Extra);
        player.SetTask(BirthdayClaimGroup, today.GirlId, now.Year);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 生日奖励：少女 {today.GirlId}（{today.Month}/{today.Day}）" +
                 $"、发出 {granted.Rewards.Count} 项（{granted.Describe()}）");

        GameNotify.PushRewards(session, player, granted);

        // ★★ 成功必须回 nRet = 0（数值），失败则完全不回包
        Reply(session, cmd, new JsonObject { ["nRet"] = 0 });
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 139「少女好感度满级兑换」。
    ///
    /// ★ 客户端消费点极简（`LuaCall.lua:1562..1567`）：
    /// <code>
    /// local ui = UI.GetClass('UI_CafeBonusTip')
    /// if ui then ui:UpdateUI() end            -- 只是刷新界面，**不读任何字段**
    /// </code>
    /// ⇒ 回包内容不限（连空对象都行），但**必须回包**才会刷新界面。
    ///
    /// ★★ **兑换规则未取证** —— 服务端侧函数 `CafeLogic:OnCmdGirlMaxExpReward`
    /// 不在包内（`CafeLogic.lua` 里 0 命中），且没有找到对应的配置表列
    /// （`NCafeLevels.txt` 的 `TotalHot`/`CoffeeCoinMax` 都不像好感度兑换）。
    /// ⇒ 本实现**只回包让界面刷新、不发奖、不扣任何资源**，并记一条日志。
    /// 这比「编一个兑换规则」安全：界面能正常响应，而不会凭空产出物品。
    /// </summary>
    private Task OnGetMaxExpReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = ReadInt(param, 0);
        var girl = id > 0 ? player.GetGirl((uint)id) : null;
        var love = GirlLoveTable.Load(Path.GetFullPath(_ctx.Options.SettingDirectory));
        if (girl is null || love.MaxLevel((uint)id) <= 1 ||
            girl.Level < love.MaxLevel((uint)id) || girl.Exp < 3000)
            return Task.CompletedTask;
        int count = (int)(girl.Exp / 3000);
        var reward = _ctx.Rewards.Grant(player, new[] { new[] { 8, 2, 3, 1, count } }, RewardSource.Extra);
        girl.Exp %= 3000;
        _ctx.Store.Save(player);
        GameNotify.PushGirl(session, girl);
        GameNotify.PushRewards(session, player, reward);
        Reply(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // =================================================================
    //  120 / 121 咖啡制作技能：学习 / 重置
    // =================================================================

    /// <summary>
    /// LuaCall 120「咖啡制作技能学习」。上行 <c>{Id: 咖啡id}</c>
    /// （★ **大写 `Id`**，取证 `UI_CoffeeLearnTips.lua:171` / `UI_CoffeeList.lua:677`）。
    ///
    /// <para><b>★ 「已学」存在任务值位域里，不是独立存档</b></para>
    /// 取证 `CoffeeCommon:GetCoffeeUnlockInfo`（`CoffeeCommon.lua:555..588`）：
    /// <code>
    /// local lockinfo = me:GetTaskValue(CoffeeCommon.GroupId, coffeeid)   -- GroupId = 23（:17）
    /// local unlock   = LoadBits(lockinfo, 8, 8)                          -- ★ bit 8 = 已学
    /// cond1 = LoadBits(lockinfo, 0, 0) > 0                               -- bit 0..2 = 三个前置条件
    /// return unlock > 0, { cond1, cond2, cond3, cond4 }
    /// </code>
    /// ⇒ 学习成功 = 把 <c>(23, 咖啡id)</c> 的 **bit 8 置 1**，然后**推 pf 1026**
    /// （任务值只能由它送达）。
    ///
    /// <para><b>★★ 本版本的学习条件被简化了 —— 只能校验前置咖啡</b></para>
    /// 客户端 `CheckCanLeanCoffee`（`CoffeeCommon.lua:428..458`）查四项：
    /// 技能点 <c>LearnCostPoint</c> / 等级 <c>Level</c> / 前置 <c>PrevCoffee</c> / 金币 <c>LearnCostCash</c>。
    /// **但 `CoffeeList.txt` 现网只有 24 列，这四列全都不存在**
    /// （实测列名：<c>Id/NUL/NameIndex/IconId/Desc/star/Sweet/Appearance/Concentration/Flavor/
    /// Time/Cost/Price/Max/AwardA/RateA/AwardB/RateB/AwardC/RateC/Cond1/Cond2/Cond3/UnlockCostItem</c>）
    /// ⇒ 客户端读它们时走 <c>tonumber(tbRow["X"] or 0)</c> 兜底 ⇒ **恒为 0 ⇒ 四项检查恒通过**。
    /// ⇒ 服务端也就**没有技能点/等级/金币可校验**，只保留「前置咖啡」这一条
    /// （来自实际存在的 <c>Cond1</c> 列，格式 <c>[关卡章节-关卡-难度]</c>，
    /// Cond1 为关卡条件，不能解释为咖啡依赖）。
    /// ★ <c>UnlockCostItem</c>（= <c>Cond4</c>，道具消耗）现网**全空** ⇒ 无需扣道具。
    ///
    /// <para><b>回包（A 族）</b></para>
    /// `LuaCall.lua:1763` 是 <c>if tbParam.nError then</c>（**真值**）
    /// ⇒ 成功**绝不能带** <c>nError</c>；成功时客户端调
    /// <c>UI.Call("UI_CoffeeList", "OnCoffeeLearned")</c>（**无参**，`:1767`）
    /// ⇒ 它自己 `UpdateCoffeeList` 重读本地任务值
    /// ⇒ **必须先推 pf 1026 再回包**（与养成域同一条时序规矩）。
    /// </summary>
    private Task OnLearnCoffee(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_LearnCoffee({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        // ★ 上行键是大写 Id；兼容小写以防别处发小写
        var obj = param as JsonObject;
        int id = ReadInt(obj?["Id"], 0);
        if (id <= 0)
            id = ReadInt(obj?["id"], 0);

        if (id <= 0 || id >= MaxCoffeeId || !_cafe.Coffees.ContainsKey(id))
        {
            Log.Warn($"[session {session.Id}] Cafe_LearnCoffee 咖啡 id={id} 非法" +
                     $"（须 1..{MaxCoffeeId - 1} 且在 CoffeeList.txt 里）");
            return Fail(session, cmd, ErrCoffeeNotExists);
        }

        int bits = player.GetTask(CoffeeTaskGroup, id);
        if ((bits & (1 << CoffeeLearnedBit)) != 0)
        {
            Log.Info($"[session {session.Id}] Cafe_LearnCoffee 咖啡 {id} 已学过（位域 {bits}）");
            return Fail(session, cmd, ErrCoffeeAlreadyLearned);
        }

        var conditionChanges = RefreshCoffeeConditions(player);
        if (conditionChanges.Count > 0)
        {
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session, conditionChanges);
        }
        bits = player.GetTask(CoffeeTaskGroup, id);
        var recipe = _cafe.Coffees[id];
        if ((!string.IsNullOrWhiteSpace(recipe.Cond1) && (bits & 1) == 0) ||
            (recipe.RequiredPlayerLevel > 0 && (bits & 4) == 0))
            return Fail(session, cmd, ErrCoffeePrevNotLearned);

        // 前置咖啡仅对应 PrevCoffee；当前配置未提供该列。
        int prev = _cafe.CoffeePrereq(id);
        if (prev > 0 && (player.GetTask(CoffeeTaskGroup, prev) & (1 << CoffeeLearnedBit)) == 0)
        {
            Log.Info($"[session {session.Id}] Cafe_LearnCoffee 咖啡 {id} 的前置 {prev} 未学 ⇒ 拒绝");
            return Fail(session, cmd, ErrCoffeePrevNotLearned);
        }

        // ---- 通过：置 bit 8 ----
        int updated = bits | (1 << CoffeeLearnedBit);
        player.SetTask(CoffeeTaskGroup, id, updated);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 学会咖啡 {id}（{_cafe.Coffees[id].Star}★）：" +
                 $"任务值 (23,{id}) {bits} → {updated}（bit8 置位）" +
                 $"{(prev > 0 ? $"；前置 {prev} 已满足" : "")}");

        // ★★ 先推任务值（pf 1026），再回包 —— OnCoffeeLearned 无参、要重读本地状态
        GameNotify.PushTaskValues(session,
            new[] { new KeyValuePair<uint, int>(Player.ComposeTaskId(CoffeeTaskGroup, id), updated) });

        // ★ 成功回包不带 nError（真值判断）；客户端只看「有没有 nError」，内容不限
        Reply(session, cmd, new JsonObject { ["Id"] = id });
        return Task.CompletedTask;
    }

    /// <summary>
    /// LuaCall 121「重置咖啡制作技能」。上行**无参**
    /// （`LuaCall.lua:1775` 是 <c>CafeLogic:OnCmdResetCoffee()</c>，连 tbParam 都不传）。
    ///
    /// ★ **客户端没有任何发起点**（`grep -rn 'Cafe_ResetCoffee' extracted/lua/UI_*.lua` → 0 命中）
    /// ⇒ 只可能由 GM/调试触发。这里仍实现，因为成本极低且能让 GM 重置技能树。
    ///
    /// 行为：把 group 23 下**所有咖啡 id** 的 bit 8 清掉，并把已用技能点归零，
    /// 然后推 pf 1026。回包同样是 A 族（成功不带 <c>nError</c>，
    /// 客户端弹 <c>ui.UI_CoffeeLearnTips.ResetSuccess</c>，`:1781`）。
    /// </summary>
    private Task OnResetCoffee(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_ResetCoffee({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var changed = new List<KeyValuePair<uint, int>>();
        int cleared = 0;
        foreach (int id in _cafe.Coffees.Keys.Where(k => k > 0 && k < MaxCoffeeId).OrderBy(k => k))
        {
            int bits = player.GetTask(CoffeeTaskGroup, id);
            if ((bits & (1 << CoffeeLearnedBit)) == 0)
                continue;
            int now = bits & ~(1 << CoffeeLearnedBit);
            player.SetTask(CoffeeTaskGroup, id, now);
            changed.Add(new KeyValuePair<uint, int>(Player.ComposeTaskId(CoffeeTaskGroup, id), now));
            cleared++;
        }

        // 已用技能点归零
        if (player.GetTask(CoffeeTaskGroup, SkillPointTaskId) != 0)
        {
            player.SetTask(CoffeeTaskGroup, SkillPointTaskId, 0);
            changed.Add(new KeyValuePair<uint, int>(
                Player.ComposeTaskId(CoffeeTaskGroup, SkillPointTaskId), 0));
        }

        _ctx.Store.Save(player);
        Log.Info($"[session {session.Id}] 重置咖啡技能：清掉 {cleared} 杯的已学标记、已用技能点归零");

        if (changed.Count > 0)
            GameNotify.PushTaskValues(session, changed);

        Reply(session, cmd, new JsonObject { ["reset"] = cleared });
        return Task.CompletedTask;
    }

    // =================================================================
    //  125 Cafe_OnLeave —— 离开咖啡馆（顺带推营业报告）
    // =================================================================

    /// <summary>
    /// LuaCall 125「离开咖啡馆」。上行 <c>{usetime: 停留秒数}</c>
    /// （`UI_NCafe.lua:665`，在 `OnRelease` 时发出，且仅当 <c>staytime ~= 0</c>）。
    ///
    /// ★ 客户端的 <c>else</c> 分支是**空实现**（`LuaCall.lua:1842..1843`）
    /// ⇒ **不消费任何回包**，所以本命令不需要回执。
    ///
    /// ★★ 本实现拿它当「玩家离店」的时机 **推营业报告（122）** ——
    /// 122 是纯下行、客户端从不请求，需要服务端选一个时机；
    /// 「离店时汇总这一趟赚了多少」与原厂 `UI_NCafe` 打开时
    /// `CoffeeCommon:TryBusinessReport()`（`UI_NCafe.lua:513`）的时序吻合：
    /// 我们在离店时推，客户端存进 `self.BusinessReport`，**下次进店**时弹出报告。
    /// </summary>
    private Task OnLeave(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_OnLeave({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        if (_mood.Leave(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            _ctx.Store.Save(player);
        int useTime = ReadInt((param as JsonObject)?["usetime"]);
        Log.Info($"[session {session.Id}] 玩家离开咖啡馆（停留 {useTime} 秒）");

        // ★ 顺带把这一趟的经营成果推成营业报告（下次进店弹出）
        PushReport(session, player);

        // 客户端 else 分支为空 ⇒ 不需要回执
        return Task.CompletedTask;
    }

    // =================================================================
    //  122 Cafe_Report —— 营业报告（纯下行）
    // =================================================================

    /// <summary>
    /// 推一份营业报告（LuaCall 122）。★ **纯下行**：客户端从不请求
    /// （`LuaCall.lua:1790` 的 `MODULE_SERVER` 分支只写了「仅服务器主动发送」）。
    ///
    /// <para><b>★★★ 六个字段全部裸解引用 —— 缺一个就 NRE</b></para>
    /// 取证 `CoffeeCommon:TryBusinessReport`（`CoffeeCommon.lua:341..347`）：
    /// <code>
    /// local bReport = self.BusinessReport and self.BusinessReport.outlist
    ///                 and CountTB(self.BusinessReport.outlist) &gt; 0      -- ★ 判据
    /// reportdata.gold       = earnings.gold       + earnings_ex.gold       -- ★ 裸解引用
    /// reportdata.hot        = earnings.hot        + earnings_ex.hot        -- ★
    /// reportdata.coffeeCoin = earnings.coffeecoin + earnings_ex.coffeecoin -- ★
    /// </code>
    /// ⇒ <c>earnings</c> 与 <c>earnings_ex</c> **都必须是对象**，且各自
    /// <c>gold</c>/<c>coffeecoin</c>/<c>hot</c> **三个键齐全**（0 也要给，不能省）。
    ///
    /// 另一处消费点 `CoffeeCommon:SetCoffeeBusinessReportData`（`:298..308`）：
    /// <c>if tbParam and tbParam.outlist then</c> ⇒ **没有 <c>outlist</c> 整份报告被丢弃**；
    /// 且在 map 6 内会立刻 <c>earnings.gold + earnings_ex.gold</c>（同样裸解引用）。
    ///
    /// <para><b>★ <c>outlist</c> 是「映射」不是数组</b></para>
    /// 界面 `UI_BusinessReport.lua:88` 是
    /// <c>for coffeetype, count in pairs(tbParam.outlist)</c>
    /// ⇒ 形状是 <c>{ "咖啡id": 数量 }</c>。
    /// ★ 该文件 `:42..45` 的注释示例写成了数组 <c>{{coffeetype=1,count=100}}</c>，
    /// **那是过时的** —— 以代码为准（`LuaCall.lua:1797` 的注释
    /// 「`outlist = [coffeetype = count,...]`」也佐证映射形态）。
    /// 若真按数组发，`pairs` 会把**下标**当 coffeetype、把**整个子表**当 count，
    /// 界面显示成乱码数量。
    ///
    /// <para><b>累计口径</b></para>
    /// 每次 116 结算都往 <c>Player.Extra["cafereport"]</c> 累加；
    /// 推送后清零（一次报告只汇报「上次报告以来」的经营成果）。
    /// </summary>
    internal void PushReport(ClientSession session, Player player)
    {
        var acc = ReadReport(player);
        if (acc.OutList.Count == 0)
        {
            Log.Info($"[session {session.Id}] 营业报告：本期无成交 ⇒ 不推"
                     + "（客户端判据 CountTB(outlist) > 0，空报告会被丢弃）");
            return;
        }

        var outList = new JsonObject();
        foreach (var (coffeeType, count) in acc.OutList.OrderBy(kv => kv.Key))
            outList[coffeeType.ToString()] = count;     // ★ 映射：{ "1": 3, "2": 1 }

        // ★★ 六个键一个都不能少（全是裸解引用）
        var payload = new JsonObject
        {
            ["earnings"] = new JsonObject
            {
                ["gold"] = acc.BaseGold,
                ["coffeecoin"] = acc.BaseCoin,
                ["hot"] = acc.BaseHot,
            },
            ["earnings_ex"] = new JsonObject
            {
                ["gold"] = acc.ExGold,
                ["coffeecoin"] = acc.ExCoin,
                ["hot"] = acc.ExHot,
            },
            ["outlist"] = outList,
        };

        Log.Info($"[session {session.Id}] 推营业报告：卖出 {acc.OutList.Values.Sum()} 杯"
                 + $"（{acc.OutList.Count} 种）、基础 [金 {acc.BaseGold}/咖啡币 {acc.BaseCoin}/人气 {acc.BaseHot}]"
                 + $"、附加 [{acc.ExGold}/{acc.ExCoin}/{acc.ExHot}]");

        _ctx.LuaCalls.CallClient(session, ReportCmd, payload);

        // 推完清零 —— 一份报告只汇报「上次报告以来」的成果
        player.Extra.Remove(ReportExtraKey);
        _ctx.Store.Save(player);
    }

    /// <summary>营业报告的累计值。</summary>
    internal sealed record ReportAcc(
        int BaseGold, int BaseCoin, int BaseHot,
        int ExGold, int ExCoin, int ExHot,
        Dictionary<int, int> OutList);

    /// <summary>读累计（缺失/损坏 ⇒ 全 0 + 空清单，不抛）。</summary>
    internal static ReportAcc ReadReport(Player player)
    {
        var o = player.Extra.TryGetValue(ReportExtraKey, out var n) ? n as JsonObject : null;
        var list = new Dictionary<int, int>();
        if (o?["outlist"] is JsonObject m)
        {
            foreach (var kv in m)
            {
                if (int.TryParse(kv.Key, out int ct) && ct > 0)
                    list[ct] = ReadInt(kv.Value);
            }
        }
        return new ReportAcc(
            ReadInt(o?["bgold"]), ReadInt(o?["bcoin"]), ReadInt(o?["bhot"]),
            ReadInt(o?["egold"]), ReadInt(o?["ecoin"]), ReadInt(o?["ehot"]),
            list);
    }

    /// <summary>把一笔 116 结算累加进营业报告。</summary>
    internal static void AccumulateReport(Player player, int coffeeType,
                                          int[] baseEarn, int[] exEarn)
    {
        var acc = ReadReport(player);
        var list = new Dictionary<int, int>(acc.OutList);
        list[coffeeType] = list.TryGetValue(coffeeType, out int c) ? c + 1 : 1;

        var outList = new JsonObject();
        foreach (var (k, v) in list.OrderBy(kv => kv.Key))
            outList[k.ToString()] = v;

        player.Extra[ReportExtraKey] = new JsonObject
        {
            ["bgold"] = acc.BaseGold + baseEarn[0],
            ["bcoin"] = acc.BaseCoin + baseEarn[1],
            ["bhot"] = acc.BaseHot + baseEarn[2],
            ["egold"] = acc.ExGold + exEarn[0],
            ["ecoin"] = acc.ExCoin + exEarn[1],
            ["ehot"] = acc.ExHot + exEarn[2],
            ["outlist"] = outList,
        };
    }

    // =================================================================
    //  124 Cafe_AddGuestWight —— 生成顾客权重
    // =================================================================

    /// <summary>
    /// LuaCall 124「生成顾客权重」。上行**无参**（`LuaCall.lua:1817` 注释 `tbParam = nil`；
    /// 真机实测整个 `param` 只有 `{"sCmd":124}`，连 `tbParam` 都没有）。
    ///
    /// <para><b>★★ 回包是「裸数组」，不是对象</b></para>
    /// `LuaCall.lua:1820..1825` 的原厂注释：
    /// <code>
    /// tbParam = [
    ///   {addguesttype = k, addguestvalue = v},
    ///   {addguesttype = k, addguestvalue = v},
    /// ]
    /// </code>
    /// 消费点 `CafeLogic:AddGuestWight`（`CafeLogic.lua:280..292`）：
    /// <code>
    /// for k, v in pairs(tbWeight) do
    ///     self.Add_Weight[v['addguesttype']] = v['addguestvalue']
    ///     self.AllWeight = self.AllWeight + v['addguestvalue']
    /// end
    /// for _, cfg in pairs(CafeLogic.tbCustomerListCfg) do
    ///     self.AllWeight = self.AllWeight + cfg.Weight        -- 再叠上配置表的基础权重
    /// end
    /// </code>
    /// ⇒ 这是**顾客类型的「额外」权重**，客户端把它与 `NCafeGuests.txt` 的 `Weight`
    /// 相加得到出现概率的分母。
    ///
    /// ★ 客户端有 nil 保护（`:1826` `if tbParam then`）⇒ 回空数组也安全，
    /// 但那等于「没有额外权重」，出现概率就只由配置表决定。
    ///
    /// <para><b>★★ 与 112 的 <c>weightlist</c> 是<u>同一份数据、两套字段名</u></b></para>
    /// | 命令 | 字段名 |
    /// | --- | --- |
    /// | 112 进店数据 | <c>Id</c> / <c>Weight</c>（**大写**，见 <see cref="CafeFeature.BuildWeightList"/>） |
    /// | **124 本命令** | <c>addguesttype</c> / <c>addguestvalue</c> |
    ///
    /// 照抄 112 的字段名会让客户端把 `Add_Weight[nil] = nil`（`pairs` 取不到键），
    /// 静默失效、不报错 —— 属于最难查的一类。
    /// </summary>
    private Task OnAddGuestWeight(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_AddGuestWight({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var state = CafeFeature.ReadState(player, _cafe);
        // ★ 第 4 个参数是 **dayIndex**（今日顾客按天重算），不是 sessionId ——
        //   传错会让每次调用都重算「今日顾客」，同一天内两次拿到不同集合。
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow), session.Id);

        // ★ 回包是**裸数组**（不是 { list = [...] }）
        var arr = new JsonArray();
        foreach (var w in content.Weights)
        {
            arr.Add(new JsonObject
            {
                ["addguesttype"] = w.Id,        // ★ 不是 "Id"
                ["addguestvalue"] = w.Weight,   // ★ 不是 "Weight"
            });
        }

        Log.Info($"[session {session.Id}] Cafe_AddGuestWight → 回顾客额外权重 {arr.Count} 类" +
                 $"（{string.Join(',', content.Weights.Select(w => $"{w.Id}:{w.Weight}"))}）");

        _ctx.LuaCalls.CallClient(session, cmd, arr);
        return Task.CompletedTask;
    }

    // =================================================================
    //  117 Cafe_NewSeatPosEmpty —— 座位空，请顾客入位
    // =================================================================

    /// <summary>
    /// LuaCall 117「座位空，请顾客入位」。上行**无参**
    /// （`LuaCall.lua:1677` 注释 `tbParam = nil`；真机实测 `tbParam` 是**空数组** `[]`）。
    ///
    /// <para><b>★ 客户端会主动发它 —— 三个前置条件（`NCafeNet.lua:253..264`）</b></para>
    /// <code>
    /// if (nCoffee &lt;= 0) then return end                  -- 橱窗必须有咖啡
    /// if (NCafe.Data:GetEmptySeatCount() &lt;= 0) then return end   -- 必须有空座
    /// if (NCafe.Data:GuestQueueCount() &lt;= 0) then return end     -- 队列必须有人
    /// LuaCall:CallServer(LuaCall.Cafe_NewSeatPosEmpty, {}, true)
    /// </code>
    /// ⇒ 真机上「造好座位数据」时服务端就会连续收到本命令（本轮实测）。
    ///
    /// <para><b>★★ 回包硬约束（消费点 `NCafeNet.lua:266..301`）</b></para>
    /// <code>
    /// if tbParam.newguest then                             -- ★ 判据：没有它整段跳过
    ///     for i, v in ipairs(tbParam.seatlist) do ...       -- ★ 无条件遍历，必带
    ///     NCafe.Data:ClearGuestQueue()
    ///     for i, v in ipairs(tbParam.customerqueue) do ...  -- ★ 无条件遍历，必带
    ///     NCafe.Data:ClearCoffeeItems()
    ///     for i, v in ipairs(tbParam.coffeelist) do ...     -- ★ 无条件遍历，必带
    ///     local seat  = NCafe.Data:GetGuestSeat(tbParam.newguest.position)
    ///     local guest = NCafe.Character:LoadGuest(seat.guest, nil)   -- ★ 会解引用 seat
    ///     guest:DoBizLogic(seat)
    /// end
    /// </code>
    /// <list type="number">
    ///   <item><c>newguest</c> **必带**，否则整段跳过（顾客不入座、循环停）。</item>
    ///   <item>三个清单 <c>seatlist</c>/<c>customerqueue</c>/<c>coffeelist</c>
    ///     **都要带**（`ipairs(nil)` 会抛）。</item>
    ///   <item>★★ <c>newguest.position</c> **必须是 <c>seatlist</c> 里那个刚坐下的座位** ——
    ///     客户端用它 <c>GetGuestSeat</c> 再 <c>LoadGuest(seat.guest)</c>，
    ///     对不上就会在 <c>seat</c> 或 <c>seat.guest</c> 上 NRE。</item>
    /// </list>
    ///
    /// <para><b>本实现的行为</b></para>
    /// 从队列头取一位顾客 → 放进第一个空座位 → 落盘 → 回「新座位 + 三个更新后的清单」。
    /// 队列空或无空座时**不回包**（客户端本来也不该发；发了说明它的本地状态与服务端不同步，
    /// 回一个没有 <c>newguest</c> 的包客户端会整段跳过，等价于不回）。
    /// </summary>
    private Task OnNewSeatPosEmpty(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_NewSeatPosEmpty({cmd}) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var state = CafeFeature.ReadState(player, _cafe);
        // ★ 第 4 个参数是 **dayIndex**（今日顾客按天重算），不是 sessionId ——
        //   传错会让每次调用都重算「今日顾客」，同一天内两次拿到不同集合。
        var content = CafeFeature.ReadContent(player, state, _cafe,
                                             CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow), session.Id);

        // 队列头的顾客
        if (content.Queue.Count == 0)
        {
            Log.Info($"[session {session.Id}] Cafe_NewSeatPosEmpty：排队为空 ⇒ 无人可入座，不回包");
            return Task.CompletedTask;
        }

        // 第一个空座位（CustomerIdx 为 null 即空）
        var empty = content.Seats.FirstOrDefault(s => s.CustomerIdx is null);
        if (empty is null)
        {
            Log.Info($"[session {session.Id}] Cafe_NewSeatPosEmpty：{content.Seats.Count} 个座位全满 ⇒ 不回包");
            return Task.CompletedTask;
        }

        // ★ 咖啡也要有 —— 客户端的前置条件之一；没有咖啡让顾客坐下会让它 DoBizLogic 无事可做
        if (content.Coffees.Count == 0)
        {
            Log.Info($"[session {session.Id}] Cafe_NewSeatPosEmpty：橱窗无咖啡 ⇒ 不回包");
            return Task.CompletedTask;
        }

        var guest = content.Queue[0];
        // 顾客点的那杯：取橱窗第一种（★ 原厂的选择规则未取证，这里取「先到先用」）
        var pick = content.Coffees[0];
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ---- 顾客入座 ----
        var seats = content.Seats.ToList();
        int at = seats.IndexOf(empty);
        seats[at] = empty with
        {
            CustomerIdx = guest.CustomerIdx,
            CustomerType = guest.CustomerType,
            CoffeeType = pick.CoffeeType,
            StartTime = now,
        };

        // ---- 队列去掉他 ----
        var queue = content.Queue.Skip(1).ToList();

        // ---- 橱窗消耗一杯 ----
        var coffees = content.Coffees.ToList();
        if (pick.Count <= 1)
            coffees.RemoveAt(0);
        else
            coffees[0] = pick with { Count = pick.Count - 1 };

        var updated = CloneWith(content, seats: seats, queue: queue, coffees: coffees);
        WriteMoodState(session, player, state, updated);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 顾客入座 customeridx={guest.CustomerIdx}" +
                 $"（{guest.CustomerType} 型）→ 座位 {empty.Position}，点咖啡 {pick.CoffeeType}" +
                 $"（橱窗余 {coffees.Sum(c => c.Count)} 杯）；排队 {content.Queue.Count} → {queue.Count}");

        // ★★ newguest.position 必须与 seatlist 里那条一致（客户端 GetGuestSeat 要用）
        Reply(session, cmd, new JsonObject
        {
            ["newguest"] = new JsonObject
            {
                ["position"] = empty.Position,
                ["customeridx"] = guest.CustomerIdx,
                ["customertype"] = guest.CustomerType,
                ["coffeetype"] = pick.CoffeeType,
                ["starttime"] = now,
            },
            ["seatlist"] = CafeFeature.BuildSeatList(seats),
            ["customerqueue"] = CafeFeature.BuildQueueList(queue),
            ["coffeelist"] = CafeFeature.BuildCoffeeList(coffees),
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 算一位顾客的收益。返回 <c>(基础三元组, 附加三元组, 顾客星级)</c>，
    /// 三元组都是 <c>[金币, 咖啡币, 人气]</c>。
    ///
    /// ★ 概率用**确定性**种子（roleid + customeridx + 咖啡 id）而不是 <c>Random</c>：
    /// 同一次结算重放得到同一结果，便于单测与排障（与社团/好友的假数据同一套思路）。
    /// </summary>
    internal (int[] Base, int[] Ex, int Star) ComputeEarn(CafeFeature.CafeSeat seat, ulong roleid, int idx)
    {
        var coffee = _cafe.Coffees.TryGetValue(seat.CoffeeType, out var c) ? c : null;
        var guest = _cafe.Guests.TryGetValue(seat.CustomerType, out var g) ? g : null;

        // 配置缺失时给 0 收益但**仍回合法形状**（客户端只要三元素数组就不崩）
        if (coffee is null)
        {
            Log.Warn($"[cafe] 结算时咖啡 {seat.CoffeeType} 不在 CoffeeList.txt ⇒ 收益按 0 计");
            return (new[] { 0, 0, 0 }, new[] { 0, 0, 0 }, guest?.Star ?? 1);
        }

        // ★ 匹配判定：coffee[顾客.Prefer] >= 顾客.Preferfactor（CoffeeCommon.lua:499）
        bool matched = guest is not null
                       && guest.PreferFactor > 0
                       && coffee.AttrByName(guest.Prefer) >= guest.PreferFactor;

        // 匹配拿全额，不匹配拿一半（★ 后者是本实现的取舍，原厂倍率未取证）
        int Scale(int v) => matched ? v : v / 2;
        var baseEarn = new[] { Scale(coffee.AwardA), Scale(coffee.AwardB), Scale(coffee.AwardC) };

        // 附加：按顾客的 Rate1/2/3 百分比命中
        var ex = new[] { 0, 0, 0 };
        if (guest is not null)
        {
            if (Hit(roleid, idx, seat.CoffeeType, 1, guest.Rate1)) ex[0] = coffee.RateA;
            if (Hit(roleid, idx, seat.CoffeeType, 2, guest.Rate2)) ex[1] = coffee.RateB;
            if (Hit(roleid, idx, seat.CoffeeType, 3, guest.Rate3)) ex[2] = coffee.RateC;
        }

        return (baseEarn, ex, guest?.Star ?? 1);
    }

    /// <summary>确定性的百分比判定（<paramref name="ratePercent"/> 为 0 恒不命中）。</summary>
    private static bool Hit(ulong roleid, int idx, int coffeeType, int slot, int ratePercent)
    {
        if (ratePercent <= 0)
            return false;
        if (ratePercent >= 100)
            return true;
        // splitmix64 风格混淆 —— 与 ClubFeature.Mix 同思路，跨进程可复现
        ulong x = roleid ^ ((ulong)idx << 17) ^ ((ulong)coffeeType << 33) ^ ((ulong)slot << 49);
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (int)(x % 100) < ratePercent;
    }

    /// <summary>回一条失败（带 <c>nError</c>，客户端拼 <c>ui.Error.&lt;码&gt;</c> 弹提示）。</summary>
    private Task Fail(ClientSession session, int cmd, int nError)
    {
        Reply(session, cmd, new JsonObject { ["nError"] = nError });
        return Task.CompletedTask;
    }

    private void Reply(ClientSession session, int cmd, JsonObject payload)
        => _ctx.LuaCalls.CallClient(session, cmd, payload);

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;

    private static long ReadLong(JsonNode? node, long fallback = 0)
        => node is null ? fallback : long.TryParse(node.ToString(), out var v) ? v : fallback;
}
