using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 咖啡馆进入数据。两个 LuaCall 数字命令，登录进主城后客户端立刻成对发出：
/// <code>
/// PlayerLogic.OnUpdateEnd        (PlayerLogic.lua:436) 主线已过 → Map.LoadById(2) + UI.Open('UI_Main')
///   → NCafeNet.ReqDownloadCafeData(pCall)              (PlayerLogic.lua:450 / NCafeNet.lua:20)
///       → LuaCall:CallServer(LuaCall.Cafe_ReqCafeData, {})   112  上行 tbParam = {} → JSON "[]"
///       → CoffeeCommon:UpdateFurCount()                (NCafeNet.lua:41 → CoffeeCommon.lua:724)
///           → LuaCall:CallServer(LuaCall.CafeFurNum)    241  **不带 tbParam** → 上行只有 {"sCmd":241}
/// </code>
/// 两条都是 <c>LuaCall:CallServer</c> 且没传 blockTime（LuaCall.lua:503），所以不会挂
/// <c>Game.Blocking</c> 遮罩；但 112 不回包会让 <c>NCafeNet.IsAlreadyRequest</c> 卡 5 秒
/// （NCafeNet.lua:87..90 的重置定时器 <c>Game.Timer.Add(5, ...)</c>），
/// 期间再点咖啡馆会弹 "Cafe.Click" 提示（NCafeNet.lua:31..33）。
///
/// <para><b>★ 0. 进咖啡馆的完整等待链（本轮取证，写这个域必读）</b></para>
/// 主城「咖啡馆」按钮的真实调用是
/// <c>NCafeNet.ReqDownloadCafeData(function() Map.LoadById(6) end)</c>（<c>UI_Main.lua:19</c>）。
/// 那个 <c>pCall</c> 被存进 <c>NCafeNet.DownloadCafeDatatCallback</c>（<c>NCafeNet.lua:35</c>），
/// 而**唯一**的调用点是 <c>RspDownloadCafeData</c> 的**最后一句**（<c>NCafeNet.lua:80..82</c>）：
/// <code>
/// function NCafeNet.RspDownloadCafeData(tbParam)      -- NCafeNet.lua:52
///     NCafe.Data.ReqCustomerTime = tbParam.basetime            -- :57  必须是数字
///     NCafe.Data.CafeLevel       = tbParam.level               -- :59  必须是数字
///     if tbParam.hot then NCafe.Data.Hot = tbParam.hot end     -- :61  可选（Lua 里 0 为真）
///     NCafe.Data:SetCafeComfort(tbParam.comfort)               -- :65  必须是数字
///     NCafeNet:updateSeatData(tbParam.seatlist)                -- :67
///     NCafeNet:updateQueueData(tbParam.customerqueue)          -- :68
///     NCafeNet:updateCoffeeData(tbParam.coffeelist)            -- :69  ★ 唯一会抛的一组
///     NCafeNet:updateGirlData(tbParam.roomgirlslist)           -- :70
///     NCafeNet:updateWeightData(tbParam.weightlist)            -- :71
///     NCafeNet:updateVisitedData(tbParam.visitedList)          -- :72  ★ 驼峰
///     NCafePetLogic:InitFoodBoxs(tbParam.boxstatelist)         -- :73
///     NCafePetLogic:InitPetsInCafe(tbParam.petstatelist)       -- :74
///     NCafePetLogic:UpdatePetNum(tbParam.nextpetid)            -- :75
///     GirlVigourManager.RecordCafeGirl()                       -- :78  空数据安全
///     if NCafeNet.DownloadCafeDatatCallback then
///         NCafeNet.DownloadCafeDatatCallback()                 -- :81  ← Map.LoadById(6) 在这里
///     end
/// end
/// </code>
/// ⇒ <b>客户端在 <c>Map.LoadById(6)</c> 之前只等一条回包：LuaCall 112。</b>
/// 241（家具数）是 fire-and-forget，家园 pf 1048/1049 在登录时就取过了，不参与这条链。
/// 反过来说：<b>112 回包里任何一处解引用抛错，<c>Map.LoadById(6)</c> 就永远不会被调用</b>
/// （连加载条都不会出现）—— 所以这条链上「哪些字段没有 nil 保护」是硬指标，逐字段取证见
/// <see cref="BuildCafeDataPayload"/> 与各 <c>Build*List</c>。
///
/// <para><b>★ 0b. map 6 加载完成之前还要过的三道「非回包」关（都在咖啡馆自己的 Lua 里）</b></para>
/// <list type="number">
///   <item><c>NCafe.lua:19</c> <c>self:FixSeatOverLimit()</c> → <c>:119</c>
///     <c>CafeEditor.GetItemTypeInfoLimit(typeInfo)</c>（<c>CafeEditor.lua:103..116</c>）——
///     <c>error()</c> 写在 <c>return</c> 之前，<b>等级 &gt; <c>NCafeItemTypes.txt</c> 餐位类
///     <c>Limit</c> 列长度（现网 30）就直接抛</b>，等级 &lt;= 0 则 <c>Limit[-1]</c> 越界。
///     这一步在 <c>NCafeMgr.Ins:OnEnter(...)</c>（<c>NCafe.lua:23</c>）之前，抛了场景一个对象都不初始化。
///     ⇒ 服务端把 <c>level</c> 钳在 <c>[1, CafeTable.MaxCafeLevel]</c>，见 <see cref="ReadState"/>。</item>
///   <item>C# 协程 <c>NCafeManager.&lt;onEnter&gt;d__34.MoveNext</c>（RVA <c>0x1D31400</c>，
///     跳转表 <c>0x3D2E9F8</c>）四个状态依次
///     <c>SetProgress(0.4)</c>+<c>ChangeArea</c>/<c>InitObjects</c>/<c>RandomSun</c> →
///     <c>SetProgress(0.6)</c>+<c>InitCharacters</c>（RVA <c>0x1D57F2C</c>，内部 0.65/0.75/0.8）→
///     <c>set_InScene(true)</c>+<c>SetProgress(0.9)</c> → 建 <c>NCafeUpdater</c> 后调 <c>pComplete</c>。
///     只有 <c>pComplete</c> 里最后两句
///     <c>Map.ExecLoadingEndCall()</c> + <c>self.super:OnEnter()</c>（→ <c>Game.Loading.Hide()</c>，
///     <c>NCafe.lua:45..46</c> / <c>Map.lua:12</c>）才会收掉加载条。
///     ⇒ <b>回调里任何一句抛错 = 加载条永久停在 0.9</b>，且那段代码在 C# 协程里跑，
///     不在 <c>Map.OnEnter</c> 的 <c>xpcall</c>（<c>Map.lua:51..58</c>）保护范围内。</item>
///   <item>回调里的 <c>GirlVigourManager.Init()</c>（<c>NCafe.lua:26</c>）注册了**每秒**定时器
///     <c>CheckMoodValue</c>（<c>GirlVigourManager.lua:297</c>）→ <c>:192</c> <c>UpdateGapTime()</c> →
///     <c>:229</c> <c>conf.ReduceVigor</c>，其中 <c>conf = CafeLogic:GetCafeLvCfg(CafeLevel)</c>
///     （<c>CafeLogic.lua:233</c> 是裸字典取值，等级不在 <c>NCafeLevels.txt</c> 里就是 <b>nil</b>）
///     —— **无 nil 保护**，也就是「等级非法 ⇒ 进店后每秒抛一次、少女心情永久停更」。
///     ⇒ 又一条要求 <c>level</c> 必须是 <c>NCafeLevels.txt</c> 实有键的理由。
///     （同一函数 <c>:231</c> 的 <c>comfortConf.AddVigor</c> **不会**抛：
///     <c>GetComfortConfig()</c> 的字典键是 <c>ComfortEff.Id</c> = 0..9，
///     匹配不上时返回的 <c>ComfortConfig[0]</c> 正是「Id=0 / 0..0 / 全 0 加成」那一行 ——
///     舒适度越界属于**静默退化成零加成**，见 <see cref="CafeTable.MaxComfort"/>。）</item>
/// </list>
///
/// ---- LuaCall 112 Cafe_ReqCafeData（LuaCall.lua:112 / :1595）----
/// <code>
/// MODULE_SERVER 分支（LuaCall.lua:1596..1603，原服务端行为）：
///     local tbRet = CafeLogic:OnCmdReqCafeData(tbParam)      -- 咖啡馆本体数据
///     if not tbRet then return end
///     tbRet.visitedList = HouseLogic:OnReqVisitedList(tbParam)  -- 访客记录并入同一个回包
///     LuaCall:CallClient(sCmd, tbRet)
/// else 分支（LuaCall.lua:1606）：NCafeNet.RspDownloadCafeData(tbParam)
/// </code>
/// <c>CafeLogic:OnCmdReqCafeData</c> 与 <c>HouseLogic:OnReqVisitedList</c> **不在包内**
/// （CafeLogic.lua 只含客户端那一半，HouseLogic 整个模块缺失，符合 AGENTS.md §2.5：
/// 纯服务端模块需自己补）。因此回包字段清单**只能**以 else 分支
/// <c>NCafeNet.RspDownloadCafeData</c>（NCafeNet.lua:52..83）为准。
///
/// 交叉验证：proto <c>gsproto.CafePb</c>（docs/proto-schema.json）的字段名与这些 JSON key
/// 一一对应 —— level/comfort/hot/basetime/seatlist/customerqueue/coffeelist/roomgirlslist，
/// 说明原服务端就是把 CafePb 转成 JSON 塞进 tbParam，再补上 visitedList 与宠物三件套。
/// ★ 注意 <c>CafePb</c> 里**没有** <c>weightlist</c> 也没有 <c>visitedList</c>：
/// 前者是原服务端每日现算的「今日顾客」（见 <see cref="RollDailyGuests"/>），
/// 后者来自 <c>HouseLogic:OnReqVisitedList</c>。
/// CafePb 里客户端**没有**解引用的 3 个字段（nextidx 下一顾客序号 / touchtime / fixedtime）
/// 按 AGENTS.md「没有证据的字段不要编」一律不下发。
///
/// ---- LuaCall 241 CafeFurNum（LuaCall.lua:206 / :4474）----
/// <code>
/// MODULE_SERVER 分支（LuaCall.lua:4476..4483）：
///     local house = LuaHouse.LuaGetHouse(me.Id)
///     if house then tbRet.nRet = 0; tbRet.nNum = #house.furniturelist; LuaCall:CallClient(sCmd, tbRet) end
/// else 分支（LuaCall.lua:4485..4491）：
///     if tbParam.nRet then                       -- Lua 里 0 为真，所以 nRet=0 会进来
///         CoffeeCommon.NowFurNum = tbParam.nNum   -- 必须有 nNum
///         UI_CafeStore:updateCapacity()
///     end
/// </code>
/// 注意 <c>if house then</c>：原服务端没有家园数据时**根本不回包**，所以「不回」也是合法状态；
/// 本实现按「有家园、家具为空」回 <c>nNum=0</c>，与 GameModule 对 pf 1049 回的空
/// <c>HouseCache.furnitureList</c> 保持一致。
/// </summary>
public sealed class CafeFeature : IGameFeature
{
    /// <summary>LuaCall.Cafe_ReqCafeData = 112（LuaCall.lua:112「进入时请求咖啡馆数据」）。</summary>
    public const int ReqCafeDataCmd = 112;

    /// <summary>LuaCall.CafeFurNum = 241（LuaCall.lua:206「咖啡馆家具数量」）。</summary>
    public const int FurNumCmd = 241;

    /// <summary><see cref="Player.Extra"/> 里本功能的键。</summary>
    public const string ExtraKey = "cafe";

    /// <summary>
    /// 咖啡馆等级在任务值里的坐标 = (group 1, id 32)。
    /// 取证 CafeLogic.lua:20「存在task变量中的cafe等级 因为咖啡数据下发延迟, 取等级先用这个」
    /// + CafeLogic.lua:449 <c>GetCafeLevel() = me:GetTaskValue(LvGroup, LvId)</c>。
    /// 客户端多处（UI_NCafeShop.lua:709 / UI_ShopNew.lua:142 / UI_NCafe.lua:312）走这条路读等级，
    /// 所以服务端把等级同时写进任务值，保证下次登录（pf 1026 TaskValueSyncRsp 全量下发）与
    /// 本回包的 <c>level</c> 一致。
    /// </summary>
    public const int LevelTaskGroup = 1;

    /// <inheritdoc cref="LevelTaskGroup"/>
    public const int LevelTaskId = 32;

    /// <summary>
    /// 咖啡馆最低等级 = 1。取证 extracted/setting/NCafeLevels.txt 第一行数据 Level=1
    /// （SlotEat=6/SlotCoffee=1/MaxQSize=1）；等级 0 在配置表里不存在，
    /// <c>CafeLogic:GetCafeLvCfg(0)</c> 会返回 nil，客户端 <c>GetGuestDelayTime</c>
    /// （CafeLogic.lua:85）等处就只能吃 nil 分支，
    /// 而 <c>CafeEditor.GetItemTypeInfoLimit</c>（CafeEditor.lua:108）会用 <c>0 - 1 = -1</c>
    /// 去索引 C# <c>int[]</c> 直接越界。
    /// </summary>
    public const int MinLevel = 1;

    /// <summary>
    /// 第一只宠物的 uid = 1，即「还没养过宠物」时的 <c>nextpetid</c>。
    /// 取证：<c>NCafePetLogic:UpdatePetNum(maxNum)</c>（NCafePetLogic.lua:872）算
    /// <c>petMaxNum = (maxNum or 2) - 1</c>，而 <c>petMaxNum</c> 的语义是「已拥有过的最大宠物 uid」
    /// （NCafePetLogic.lua:736 领养成功时 <c>self.petMaxNum = newInfo.Id</c>，Id 来自 <c>petInfo.uid</c>）。
    /// 无宠物 → 下一个 uid = 1 → petMaxNum = 0 → <c>GetPetAreaNum()</c>（NCafePetLogic.lua:890）
    /// 返回 1，安全。
    /// </summary>
    public const int FirstPetUid = 1;

    /// <summary>
    /// <c>roomgirlslist</c> 的槽位数 = 3。取证 <c>NCafeNet.lua:5..9</c>：
    /// <c>local AreaType = { Game.NCafeAreaType.Cafe, .Rest, .Dorm }</c>，
    /// 而 <c>updateGirlData</c>（<c>:146</c>）是 <c>for index = 1, #AreaType</c> 再取
    /// <c>roomGirlsList[index]</c> ——<b>下标就是区域序号，是定位数组不是字典</b>。
    /// 枚举实际值 <c>Cafe = 1 / Rest = 2 / Dorm = 6</c>（<c>dump.cs:534523..534528</c>），
    /// 但下发时用的是 1/2/3 这个**序号**，不是枚举值。
    /// </summary>
    public const int AreaCount = 3;

    /// <summary>经营区（服务生）在 <c>roomgirlslist</c> 里的序号 = 1。</summary>
    public const int AreaIndexCafe = 0;

    /// <summary>休息区在 <c>roomgirlslist</c> 里的序号 = 2（0 基下标 1）。</summary>
    public const int AreaIndexRest = 1;

    /// <summary>宿舍在 <c>roomgirlslist</c> 里的序号 = 3（0 基下标 2）。</summary>
    public const int AreaIndexDorm = 2;

    /// <summary>
    /// 咖啡橱窗的界面格子数 = 4（<c>UI_CoffeeMake.lua:17</c> <c>for idx = 1, 4</c>）。
    /// ★ <c>UpdateUI</c>（<c>:157</c>）用 <c>self.leftCoffeeGroup[idx + 1].SaleObj</c> 直接解引用，
    /// <b>coffeelist 比 4 长就会在这里抛</b>（并且它是「点橱窗」才触发，比进店更晚，更难查）。
    /// 与 <c>NCafeLevels.txt</c> 的 <c>SlotCoffee</c> 最大值恰好一致（L15+ = 4）。
    /// </summary>
    public const int CoffeeUiSlotCount = 4;

    /// <summary>
    /// 「今日顾客」轮换的日切偏移 = 4 小时。
    /// 取证 <c>Lib.lua:1701..1707</c>：<c>local n4AM_Sec = 3600 * 4</c>，
    /// <c>GetLocalDay4AM = floor((localSec - n4AM_Sec) / 86400)</c>。
    /// 与 <see cref="SignUpFeature.DayResetOffsetSeconds"/> 同一口径。
    /// （客户端还会加 <c>3600 * Game.ServerStatus.Zone</c> 的时区偏移，
    /// 服务端这里不加 —— 只影响「几点换今日顾客」，不影响任何合法性判断。）
    /// </summary>
    public const int DayResetOffsetSeconds = 4 * 3600;

    private GameFeatureContext _ctx = null!;
    private GirlMoodRecovery _mood = null!;
    private CafeTable _table = CafeTable.Empty;
    private HouseTable _house = HouseTable.Empty;

    public string Name => "咖啡馆进入数据(LuaCall 112/241)";

    /// <summary>咖啡馆经营配置（顾客 / 咖啡 / 等级 / 舒适度 / 餐位上限），单测可注入。</summary>
    internal CafeTable Table => _table;

    public CafeFeature() { }

    /// <summary>单测用：直接注入配置表，跳过 <see cref="Install"/>。</summary>
    internal CafeFeature(CafeTable table) => _table = table;

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _mood = new GirlMoodRecovery(Path.GetFullPath(ctx.Options.SettingDirectory));
        // CafeTable.Load 按目录做了进程级缓存，与 CafePetFeature 共用同一实例（见其注释）
        _table = CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _house = HouseTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(ReqCafeDataCmd, OnReqCafeData);
        ctx.MapLuaCall(FurNumCmd, OnFurNum);
    }

    // ---- LuaCall 112：进入时请求咖啡馆数据 ----
    private Task OnReqCafeData(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Cafe_ReqCafeData(112) 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        _ctx.PrepareCafeDownload(session, player);
        var now = DateTimeOffset.UtcNow;
        long baseTime = now.ToUnixTimeSeconds();

        var state = ReadState(player, _table);
        // Settle elapsed recovery at the old rate before inventory-derived comfort changes it.
        bool moodChanged = _mood.Refresh(session, player, baseTime);
        var inventory = HouseFeature.ReadOrCreate(player, _house, session.Id);
        state = state with { Comfort = HouseComfort.Calculate(_house, inventory, state.Level, _table.MaxComfort) };
        var content = ReadContent(player, state, _table, CafeDayIndex(now), session.Id);
        bool cafeChanged = WriteState(player, state, content);
        moodChanged |= _mood.Enter(session, player, baseTime);
        if (cafeChanged || moodChanged) _ctx.Store.Save(player);

        var pet = CafePetState.Read(player);
        var payload = BuildCafeDataPayload(state, baseTime, pet, content);

        Log.Info($"[session {session.Id}] 咖啡馆数据下发 lv={state.Level} hot={state.Hot} " +
                 $"comfort={state.Comfort} basetime={baseTime}；" +
                 $"座位 {content.Seats.Count}（有人 {content.Seats.Count(s => s.CustomerIdx is not null)}）、" +
                 $"排队 {content.Queue.Count}、橱窗咖啡 {content.Coffees.Count} 种、" +
                 $"少女 经营{AreaGirlCount(content, AreaIndexCafe)}/休息{AreaGirlCount(content, AreaIndexRest)}/" +
                 $"宿舍{AreaGirlCount(content, AreaIndexDorm)}、今日顾客 {content.Weights.Count} 类" +
                 $"（{string.Join(',', content.Weights.Select(w => $"{w.Id}:{w.Weight}"))}）、访客 {content.Visitors.Count}；" +
                 $"食盆 {pet.UnlockedCount}/{CafePetFeature.FoodBoxCount} 已解锁、" +
                 $"宠物 {pet.Pets.Count} 只（店内 {pet.PetsInCafeCount}）、nextpetid={pet.NextUid}");

        _ctx.LuaCalls.CallClient(session, ReqCafeDataCmd, payload);

        // ★ 紧接着把宠物属性推过去。理由见 CafePetFeature.PushUpdatePet：
        // petstatelist 只带 uid，宠物的名字/等级/性别/立绘全靠这条 UpdatePet；
        // 而 C# NCafeCharacterController 加载完模型后会调
        // NCafePetLogic.OnCafeItemLoaded（NCafePetLogic.lua:1049..1058）拿 PetsInCafe_Sort 里的 uid
        // 去 PetController:InitPet(v)，客户端缓存里没有对应 NPetInfo 就摆不出宠物。
        // 必须在 112 回包**之后**发：InitPetsInCafe 是 RspDownloadCafeData(:74) 里才建立的。
        CafePetFeature.PushUpdatePet(_ctx.LuaCalls, session, pet.Pets);
        return Task.CompletedTask;
    }

    // ---- LuaCall 241：咖啡馆家具数量 ----
    private Task OnFurNum(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] CafeFurNum(241) 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        int num = FurnitureCount(player, _house);
        Log.Info($"[session {session.Id}] 咖啡馆家具数量下发 nNum={num}");
        _ctx.LuaCalls.CallClient(session, FurNumCmd, BuildFurNumPayload(num));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 玩家家具数量，即原服务端的 <c>#house.furniturelist</c>（LuaCall.lua:4480）。
    ///
    /// 目前恒为 0：全服唯一的家园数据源是 GameModule 对 pf 1048 的应答，它回的
    /// <c>GetHouseInfoRsp.HouseInfo = new HouseCache()</c> 里 <c>furnitureList</c> 为空
    /// （GameModule.cs:318）。等家园/装修功能落地后，这里改成读它那份清单即可，
    /// 两边必须是同一个数字，否则 <c>UI_CafeStore:updateCapacity()</c> 显示的容量会和实际家具数不符。
    /// </summary>
    internal static int FurnitureCount(Player player, HouseTable? table = null)
        => table is null ? 0 : HouseFeature.ReadOrCreate(player, table).Furniture.Count;

    /// <summary>本地「4 点日」的日序号，与客户端 <c>GetLocalDay4AM</c>（Lib.lua:1702）同口径。</summary>
    internal static int CafeDayIndex(DateTimeOffset now)
        => (int)Math.Floor((now.ToUnixTimeSeconds() - DayResetOffsetSeconds) / 86400.0);

    private static int AreaGirlCount(CafeContent content, int areaIndex)
        => areaIndex < content.AreaGirls.Count ? content.AreaGirls[areaIndex].Count : 0;

    // =================================================================
    //  回包组装
    // =================================================================

    /// <summary>
    /// 组 LuaCall 112 的回包 tbParam。**只放 <c>NCafeNet.RspDownloadCafeData</c> 会解引用的字段**，
    /// 每个字段的取证与「空但合法」的理由：
    ///
    /// <list type="table">
    /// <item><term>basetime</term><description>
    ///   NCafeNet.lua:57 <c>NCafe.Data.ReqCustomerTime = tbParam.basetime</c>。
    ///   目标是 C# <c>NCafeDataController.ReqCustomerTime</c>（ulong，dump.cs:531764），
    ///   nil 会让 xLua setter（<c>_s_set_ReqCustomerTime</c>，dump.cs:619489）报错，**必须给数字**。
    ///   语义 = 服务器 unix 秒：客户端自己兜底时用的就是 <c>GetTime()</c>
    ///   （NCafeNet.lua:242 <c>NCafe.Data.ReqCustomerTime = GetTime()</c>），
    ///   而 <c>GetTime = Game.ServerStatus.Timestamp</c>（Preload.lua:350），
    ///   即 pf 1105/1106 同步下来的服务器时间（GameModule.HandleTimeSync 用同一个时钟）。
    /// </description></item>
    /// <item><term>level</term><description>
    ///   NCafeNet.lua:59 <c>NCafe.Data.CafeLevel = tbParam.level</c>
    ///   → C# 属性 <c>CafeLevel</c>（int，dump.cs:531790），同样不能是 nil。
    ///   取值范围 <c>[1, CafeTable.MaxCafeLevel]</c>，越界的后果见类注释 §0b 第 1 条。
    /// </description></item>
    /// <item><term>hot</term><description>
    ///   NCafeNet.lua:61..63 <c>if tbParam.hot then NCafe.Data.Hot = tbParam.hot end</c>。
    ///   Lua 里 0 为真，所以 <c>hot=0</c> 会正常写进去（人气 0 = 新号）。
    ///   人气是升级条件（CafeLogic.lua:465 <c>NextLevelCfg.NeedHot</c>），等级 1→2 需要 20。
    /// </description></item>
    /// <item><term>comfort</term><description>
    ///   NCafeNet.lua:65 <c>NCafe.Data:SetCafeComfort(tbParam.comfort)</c>
    ///   → C# <c>SetCafeComfort(int)</c>（dump.cs:532026），不能是 nil。
    ///   0 是合法值：ComfortEff.txt 第一行 Id=0 / Low=0 / High=0 / 全部加成 0。
    ///   上界见类注释 §0b 第 3 条（<c>GetComfortConfig()</c> 会返回 nil）。
    /// </description></item>
    /// <item><term>seatlist</term><description>见 <see cref="BuildSeatList"/>。</description></item>
    /// <item><term>customerqueue</term><description>见 <see cref="BuildQueueList"/>。</description></item>
    /// <item><term>coffeelist</term><description>见 <see cref="BuildCoffeeList"/>。</description></item>
    /// <item><term>roomgirlslist</term><description>见 <see cref="BuildRoomGirlsList"/>。</description></item>
    /// <item><term>weightlist</term><description>见 <see cref="BuildWeightList"/>。</description></item>
    /// <item><term>visitedList</term><description>见 <see cref="BuildVisitedList"/>。</description></item>
    /// <item><term>boxstatelist</term><description>
    ///   NCafeNet.lua:73 → <c>NCafePetLogic:InitFoodBoxs</c>（NCafePetLogic.lua:58）：
    ///   nil 提前返回（:59..61）；空表也安全 —— 它按 <c>FoodBoxCount = 3</c>（NCafePetLogic.lua:10）
    ///   遍历，找不到 <c>uid == i</c> 的元素就置 <c>foodBox{i} = 0</c>（= 食盆未解锁）、时间 0、无宠物。
    ///   ★ 本域已实现（<see cref="CafePetFeature"/>），这里下发的是**真实食盆状态** ——
    ///   由 <see cref="CafePetFeature.BuildBoxStateList"/> 组装（只发已解锁的格子，新号仍是空数组）。
    /// </description></item>
    /// <item><term>petstatelist</term><description>
    ///   NCafeNet.lua:74 → <c>NCafePetLogic:InitPetsInCafe</c>（NCafePetLogic.lua:862）：
    ///   只在非 nil 时 <c>ipairs</c> 填 <c>PetsInCafe_Sort[k] = v.uid</c>，空表 → 无宠物在店。
    ///   ★ <b>k 是数组下标 = 指派位</b>，所以必须是稠密数组，见
    ///   <see cref="CafePetFeature.BuildPetStateList"/>。
    /// </description></item>
    /// <item><term>nextpetid</term><description>
    ///   NCafeNet.lua:75 → 见 <see cref="FirstPetUid"/>；有宠物之后由
    ///   <c>CafePetState.NextUid</c> 提供（= 已领养过的最大 uid + 1）。
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="state">服务端持有的咖啡馆标量（等级/人气/舒适度）。</param>
    /// <param name="baseTime">服务器 unix 秒，作为顾客排队计时基准。</param>
    /// <param name="pet">
    /// 宠物子域状态（<c>Player.Extra["cafepet"]</c>）。为 null 时按新号处理
    /// （三个食盆全锁、无宠物、nextpetid = 1），单测里用得上。
    /// </param>
    /// <param name="content">
    /// 咖啡馆经营内容（座位 / 排队 / 咖啡 / 少女 / 今日顾客 / 访客）。
    /// 为 null 时等价于 <see cref="CafeContent.Empty"/> —— 即「空咖啡馆基线」，
    /// 六个清单全是空数组。这也是历史回归用例锁的那份 wire 形态。
    /// </param>
    internal static JsonObject BuildCafeDataPayload(CafeState state, long baseTime,
                                                    CafePetState? pet = null, CafeContent? content = null)
    {
        var c = content ?? CafeContent.Empty;
        return new JsonObject
        {
            ["basetime"] = baseTime,
            ["level"] = state.Level,
            ["hot"] = state.Hot,
            ["comfort"] = state.Comfort,
            ["seatlist"] = BuildSeatList(c.Seats),
            ["customerqueue"] = BuildQueueList(c.Queue),
            ["coffeelist"] = BuildCoffeeList(c.Coffees),
            ["roomgirlslist"] = BuildRoomGirlsList(c.AreaGirls),
            ["weightlist"] = BuildWeightList(c.Weights),
            ["visitedList"] = BuildVisitedList(c.Visitors),
            ["boxstatelist"] = pet is null ? new JsonArray() : CafePetFeature.BuildBoxStateList(pet),
            ["petstatelist"] = pet is null ? new JsonArray() : CafePetFeature.BuildPetStateList(pet),
            ["nextpetid"] = pet?.NextUid ?? FirstPetUid,
        };
    }

    /// <summary>
    /// <c>seatlist</c>：餐位清单。消费点 <c>NCafeNet:updateSeatData</c>（NCafeNet.lua:101..113）：
    /// <code>
    /// NCafe.Data.Seats:Clear();                     -- :102 无条件清空
    /// if (not seatList) then return; end            -- :104 nil 与空表都安全
    /// for i, v in ipairs(seatList) do
    ///     if v.customeridx then                     -- :107 ★ Lua 真值判断，0 也是真！
    ///         NCafe.Data:UpdateSeatGuest(v.position, v.customeridx, v.customertype, v.coffeetype, v.starttime)
    ///     else
    ///         NCafe.Data:UpdateSeatGuest(v.position, 0, 0, 0, 0)
    ///     end
    /// end
    /// </code>
    /// <list type="bullet">
    ///   <item><b><c>position</c> 必填</b>（两个分支都传它，nil 会让 xLua 绑 <c>UpdateSeatGuest(uint,…)</c> 失败）。</item>
    ///   <item>★★ <b>空座位必须整个<u>省略</u> <c>customeridx</c> 键，不能填 0</b>：
    ///     <c>:107</c> 是真值判断，<c>customeridx = 0</c> 会走进「有顾客」分支，
    ///     于座位上摆一个 <c>customertype = 0</c> 的顾客 —— 而 0 不在 <c>NCafeGuests.txt</c>
    ///     （现网 201..212）里，后面 <c>UI_CafeGuestRect.lua:64</c>
    ///     <c>CafeLogic:GetCustomerCfg(guest_id).PreferDec</c> 就会在 nil 上抛。
    ///     这正是客户端自己上传时的写法：<c>ReqUploadSeatData</c>（NCafeNet.lua:189..199）
    ///     先 <c>local tb = { position = o.Guid }</c>，只有 <c>if o.guest then</c> 才补那四个键。</item>
    ///   <item>有顾客时 <c>customertype</c>/<c>coffeetype</c>/<c>starttime</c> 一并必填（同一句实参）。</item>
    ///   <item>★★ <b><c>position</c> 还有一条「静默丢弃」的硬约束</b>：C# <c>UpdateSeatGuest</c>
    ///     （RVA <c>0x1D67248</c>）新建座位前先过 <c>CheckSeatValid</c>（RVA <c>0x1D67590</c>）=
    ///     <c>GetItem(seat.Guid / 10) != null &amp;&amp; it.Room == 1</c>
    ///     （除 10 见 <c>NCafeUtilsController.GetSeatItem</c> RVA <c>0x1803F58</c>），
    ///     不通过就**不 Add 进座位表**、不报错、不显示。
    ///     也就是 <c>position / 10</c> 必须是一件**已经通过 pf 1049 下发过、且 <c>roomId == 1</c>
    ///     （咖啡馆经营区）的餐位家具的 <c>objId</c>**。
    ///     ⇒ 服务端现在回的是空 <c>HouseCache</c>（无任何家具），所以此刻**任何** <c>seatlist</c>
    ///     都会被整条丢掉；只有等家园域把 <c>furnitureList</c> 下发起来，座位才可能生效。</item>
    /// </list>
    /// 空数组是新号的正解：座位不是服务端造的 —— 客户端
    /// <c>NCafe:FixNoSeatProblem()</c>（NCafe.lua:155..189）在 <c>GetSeatCount() == 0</c> 时
    /// 按「餐位类家具的 biz_ 挂点」自己算出 <c>guid = 家具Guid * 10 + i</c>（:179）并
    /// <c>UpdateSeatGuest</c>（:180），再主动 <c>NCafeNet.ReqUploadSeatData()</c>（:188）上传
    /// （LuaCall 114 <c>Cafe_UpdateSeatList</c>，尚未实现）。服务端只负责把上传的清单存下来再回放。
    /// </summary>
    internal static JsonArray BuildSeatList(IReadOnlyList<CafeSeat> seats)
    {
        var arr = new JsonArray();
        foreach (var s in seats)
        {
            var o = new JsonObject { ["position"] = s.Position };
            if (s.CustomerIdx is int idx)
            {
                // 顺序照抄客户端上传时的写法（NCafeNet.lua:191..197），便于与真机抓包逐字比对
                o["customeridx"] = idx;
                o["customertype"] = s.CustomerType;
                o["coffeetype"] = s.CoffeeType;
                o["starttime"] = s.StartTime;
            }
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>
    /// <c>customerqueue</c>：排队顾客。消费点 <c>NCafeNet:updateQueueData</c>（NCafeNet.lua:116..124）：
    /// <code>
    /// NCafe.Data:ClearGuestQueue();                 -- :117 无条件清空
    /// if (not customerQueue) then return; end       -- :119 nil 与空表都安全
    /// for _, v in ipairs(customerQueue) do
    ///     NCafe.Data:AddGuest2Queue(v.customertype, v.customeridx, v.starttime)   -- :122 三个都必填
    /// end
    /// </code>
    /// 三个字段都是同一句的实参，任缺一个都会让 xLua 绑定失败 ⇒ <b>元素里三键必须齐全</b>
    /// （和 <c>seatlist</c> 的「空座位省略键」正好相反 —— 排队顾客不存在「空元素」这种状态）。
    /// <c>customertype</c> 必须是 <c>NCafeGuests.txt</c> 里的编号，理由同 <see cref="BuildSeatList"/>。
    /// 长度上限 = <c>NCafeLevels.txt</c> 的 <c>MaxQSize</c>（★ 现网 31 行全是 <b>1</b>）。
    /// 空数组是新号的正解：排队顾客由 <c>LuaCall.Cafe_GenCustomer</c>(115) 单独请求生成，
    /// 回包形状见 <c>NCafeNet.RspCustomerQueue</c>（:229，只认 <c>basetime</c> + <c>customerqueue</c>）。
    /// </summary>
    internal static JsonArray BuildQueueList(IReadOnlyList<CafeQueueGuest> queue)
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

    /// <summary>
    /// <c>coffeelist</c>：橱窗里在售的咖啡。消费点 <c>NCafeNet:updateCoffeeData</c>（NCafeNet.lua:127..139）：
    /// <code>
    /// NCafe.Data:ClearCoffeeItems();                     -- :128 无条件清空
    /// if (not coffeeList) then return; end               -- :130 nil 与空表都安全
    /// for _, v in ipairs(coffeeList) do
    ///     repeat
    ///         if not next(v)  then break end             -- :134 ★ 空表元素 {} 被跳过（break 在 repeat..until true 里 = continue）
    ///         local template = CoffeeCommon:GetCoffeeListConfig(v.coffeetype)
    ///         NCafe.Data:AddCoffeeItems(v.coffeetype, v.count, template.Time)   -- :136 ★★ 无 nil 保护
    ///     until true
    /// end
    /// </code>
    /// <list type="bullet">
    ///   <item>★★ <b>这是整条进店链上唯一会「因为字段值不对」直接抛的地方</b>：
    ///     <c>:136</c> 的 <c>template.Time</c> 没有 nil 检查，
    ///     <c>coffeetype</c> 不在 <c>CoffeeList.txt</c>（现网 <b>1..12</b>）里就抛，
    ///     一抛 <c>RspDownloadCafeData</c> 就断在 <c>:69</c>，<c>Map.LoadById(6)</c>（<c>:81</c>）永远不会执行。
    ///     同一段代码在 <c>LuaCall.lua:1709</c>（清橱窗 118）/<c>:1738</c>（制作咖啡 119）复用。</item>
    ///   <item><c>:134</c> 的 <c>if not next(v)</c> 是**原服务端会发空元素**的铁证：
    ///     <c>CafePb.coffeelist</c> 是 <c>repeated CoffeePb{coffeetype,count}</c>，
    ///     未占用的槽位两个字段都是 0，转成 JSON 就是 <c>{}</c>。
    ///     ⇒ 「空槽用 <c>{}</c> 占位」与「整体省略」在客户端等价，本实现选**不发空槽**（更省字节、语义更清楚）。</item>
    ///   <item><c>count</c> 必填（<c>:136</c> 第二个实参）。</item>
    ///   <item>长度上限 = min(<c>SlotCoffee</c>, <see cref="CoffeeUiSlotCount"/> = 4) —— 见该常量注释。</item>
    /// </list>
    /// 空数组是新号的正解：咖啡是玩家在 <c>UI_CoffeeMake</c> 里制作的（LuaCall 119），新号橱窗是空的。
    /// </summary>
    internal static JsonArray BuildCoffeeList(IReadOnlyList<CafeCoffee> coffees)
    {
        var arr = new JsonArray();
        foreach (var c in coffees)
        {
            arr.Add(new JsonObject
            {
                ["coffeetype"] = c.CoffeeType,
                ["count"] = c.Count,
            });
        }
        return arr;
    }

    /// <summary>
    /// <c>roomgirlslist</c>：三个区域的值班少女。消费点 <c>NCafeNet:updateGirlData</c>（NCafeNet.lua:143..153）：
    /// <code>
    /// if (not roomGirlsList) then return; end            -- :144 nil 与空表都安全
    /// for index = 1, #AreaType do                        -- :146 AreaType = {Cafe, Rest, Dorm}（:5..9）
    ///     if (roomGirlsList[index]) then                 -- :147
    ///         for _, girl in ipairs(roomGirlsList[index]) do
    ///             NCafe.Data:UpdateAreaGirl(AreaType[index], girl[1], girl[2]);   -- :149
    ///         end
    ///     end
    /// end
    /// </code>
    /// <list type="bullet">
    ///   <item>★★ <b>外层是「定位数组」，下标 1/2/3 = 经营区/休息区/宿舍</b>。
    ///     所以「休息区有人、经营区没人」<b>不能</b>写成 <c>[[[id,model]]]</c> ——
    ///     那会把休息区的人摆到经营区。必须用<b>空数组占位</b>：<c>[[], [[id,model]], []]</c>。
    ///     这就是本域「空数组 vs 省略键」的分界：<b>内层可以空，外层不能错位</b>。
    ///     三个区域全空时 <c>[]</c> 与 <c>[[],[],[]]</c> 行为完全等价（<c>:147</c> 三次取到 nil），
    ///     本实现在「全空」时发 <c>[]</c>（与历史 wire 形态一致），有人时发满 3 槽。</item>
    ///   <item>★ <b>内层元素是二元<u>数组</u>，不是带键的对象</b>：<c>:149</c> 用 <c>girl[1]</c>/<c>girl[2]</c>。
    ///     取证同源：<c>ReqSetWaiterList</c>（<c>:362</c>）构造的就是 <c>t[#t+1] = { id, girlInfo.ModelId }</c>，
    ///     <c>RspSetWaiterList</c>（<c>:385..386</c>）也照同一形状读回。
    ///     写成 <c>{girlid=…, modelid=…}</c> 的话 <c>girl[1]</c> 是 nil，
    ///     <c>UpdateAreaGirl(area, nil, nil)</c> 直接绑定失败。
    ///     （注意 proto 侧 <c>CafePb.girlswrap</c> 是 <c>girls[]</c> + <c>modelid[]</c> 两条平行数组，
    ///     原服务端转 JSON 时把它们 zip 成了二元组 —— 以客户端 Lua 为准。）</item>
    ///   <item><c>girl[2]</c> = <c>ModelId</c>。服务端一律用自己存档里的 <c>Girl.Modelid</c>
    ///     （与 pf 1005 <c>PlayerNtf.girllist</c> 同源），不采信客户端上传值；
    ///     ★ <b>0 是合法值</b> —— C# <c>InitGirls</c>（<c>0x1D5C00C</c>）在 <c>0x1D5C204</c> 判
    ///     <c>IsInSelfCafe()</c>，为 true 时把 modelId **强制置 0**（<c>0x1D5C20C</c>），
    ///     而 <c>CreateGirl</c>（<c>0x1D5E504</c>）拼 prefab 用的是 <c>girlId</c>
    ///     （<c>"CafeGirl{0}"</c> @<c>0x1D5E5FC</c>），modelId 只参与后续换装。见 <see cref="ReadAreaGirls"/>。</item>
    /// </list>
    /// 空数组是新号的正解（「没排班少女」）：排班由玩家在 <c>UI_CafeAssign</c> 里设置
    /// （LuaCall 113 <c>Cafe_SetWaiterList</c>，尚未实现）。
    /// 客户端进店后会用 <c>FixCafeAreaNoGirl()</c>（NCafe.lua:72..107）自动补一个人并主动上传，
    /// 所以 113 落地之后这里才会有内容。
    /// </summary>
    internal static JsonArray BuildRoomGirlsList(IReadOnlyList<IReadOnlyList<CafeAreaGirl>> areaGirls)
    {
        var arr = new JsonArray();
        int total = 0;
        foreach (var area in areaGirls)
            total += area.Count;
        if (total == 0)
            return arr;   // 全空 → [] ，与「三个空槽」等价（NCafeNet.lua:147 三次取到 nil）

        for (int i = 0; i < AreaCount; i++)
        {
            var slot = new JsonArray();
            if (i < areaGirls.Count)
            {
                foreach (var g in areaGirls[i])
                    slot.Add(new JsonArray { g.GirlId, g.ModelId });
            }
            arr.Add(slot);   // ★ 即使这一区没人也要占位，否则后面的区域会错位
        }
        return arr;
    }

    /// <summary>
    /// <c>weightlist</c>：「今日顾客」清单 + 各自出现权重。
    /// 消费点 <c>NCafeNet:updateWeightData</c>（NCafeNet.lua:156..169）：
    /// <code>
    /// NCafe.Data:ClearGuestWight()                     -- :157 无条件清空
    /// if (not newList) then return; end                -- :159 nil 与空表都安全
    /// local total = 0
    /// for _, v in pairs(newList) do total = total + v.Weight end        -- :162..164
    /// for _, v in pairs(newList) do
    ///     NCafe.Data:AddGuestWight(v.Id, v.Weight / total * 100)        -- :167
    /// end
    /// </code>
    /// <list type="bullet">
    ///   <item>★ <b>键名是大写的 <c>Id</c> / <c>Weight</c></b>（全包里唯一一处大写驼峰的下行字段），
    ///     正好是 <c>NCafeGuests.txt</c> 的列名 —— 说明原服务端是把配置表行直接塞下来的。
    ///     小写 <c>id</c>/<c>weight</c> 会让 <c>:163</c> 变成 <c>total + nil</c> 直接抛。</item>
    ///   <item>★ <b><c>Id</c> 必须是 <c>NCafeGuests.txt</c> 里的顾客编号（现网 201..212）</b>：
    ///     <c>UI_CafeHelp.lua:195..196</c> <c>local d = CafeLogic:GetCustomerCfg(v[1]); d.weight = …</c>
    ///     与 <c>CoffeeCommon.lua:498..499</c> <c>Customer_Info.Prefer</c> 都**没有 nil 保护**，
    ///     表外的 id 会在「顾客信息」面板 / 咖啡收益预估处抛。</item>
    ///   <item>★ <b>权重之和不能为 0</b>：<c>:167</c> 是 <c>v.Weight / total</c>。
    ///     Lua 里 <c>x/0</c> 得到 inf/nan（不抛），但会把 <c>AddGuestWight</c> 里的 float 污染成 nan，
    ///     界面显示 <c>-nan(ind)%</c>。⇒ 每条权重一律 &gt;= 1，见 <see cref="RollDailyGuests"/>。
    ///     ★★ 注意 <c>NCafeGuests.txt</c> 的 <c>Weight</c> 列现网 <b>12 行全是 0</b>，
    ///     真正的权重在 <c>WeightMin</c>/<c>WeightMax</c> 两列（1 星 1500~2000 / 2 星 1200~1499 /
    ///     3 星 900~1199）——「照抄 Weight 列」正好会做出一个 total = 0 的回包。</item>
    ///   <item>空数组安全但**不是正解**：客户端 <c>UI_CafeInfo:UpdateTodaySale</c>（<c>:232..255</c>）
    ///     用 <c>if tbRateData[tbRow.Id] then</c> 筛「今日顾客」，空清单 ⇒ 今日顾客面板全空、
    ///     <c>CoffeeCommon:Get_WhichTodayCustomers_ByCafe</c>（<c>:491</c>）返回空表 ⇒
    ///     制作咖啡界面的「咖啡币/人气收益」恒显示 0。</item>
    /// </list>
    /// </summary>
    internal static JsonArray BuildWeightList(IReadOnlyList<CafeGuestWeight> weights)
    {
        var arr = new JsonArray();
        foreach (var w in weights)
        {
            arr.Add(new JsonObject
            {
                ["Id"] = w.Id,          // ★ 大写，见方法注释
                ["Weight"] = w.Weight,
            });
        }
        return arr;
    }

    /// <summary>
    /// <c>visitedList</c>：来访过本店的其他玩家。
    /// 消费点 <c>NCafeNet:updateVisitedData</c>（NCafeNet.lua:172..181）：
    /// <code>
    /// NCafe.Data.HouseInfo.VisitedList:Clear();        -- :173 ★ 无条件，HouseInfo 为 nil 会抛
    /// if (not newList) then return; end                -- :175 nil 与空表都安全
    /// for _, v in pairs(newList) do
    ///     NCafe.Data.HouseInfo:UpdateVisitedInfo(v.roleid, v.girlid, v.modelid,
    ///         v.name, v.time, v.comfortscore);         -- :178 六个都必填
    /// end
    /// </code>
    /// <list type="bullet">
    ///   <item>★ 这个 key 是**驼峰** <c>visitedList</c>，与其余全小写的 CafePb 字段不同 ——
    ///     因为它不来自 <c>CafePb</c>，是原服务端把 <c>HouseLogic:OnReqVisitedList</c> 的返回值
    ///     另外挂上去的（<c>LuaCall.lua:1601</c> <c>tbRet.visitedList = tbVisitedList</c>）。
    ///     写成 <c>visitedlist</c> 就变成「没这个字段」，静默丢访客记录且不报错。</item>
    ///   <item>六个字段是同一句的实参，缺一个 xLua 就绑不上 <c>UpdateVisitedInfo</c>；
    ///     字段名与 proto <c>gsproto.VisitedInfo</c> 完全同名
    ///     （<c>roleid</c> uint64 / <c>name</c> string / <c>time</c> uint64 /
    ///     <c>girlid</c>、<c>modelid</c>、<c>comfortscore</c> uint32）。</item>
    ///   <item><c>name</c> 必须是**字符串**（<c>UpdateVisitedInfo</c> 第 4 参是 string）；
    ///     JSON null 会让 xLua 传 nil 进 string 形参。</item>
    /// </list>
    /// 空数组是新号的正解（本地服只有一个玩家，不会有人来访）。
    /// </summary>
    internal static JsonArray BuildVisitedList(IReadOnlyList<CafeVisitor> visitors)
    {
        var arr = new JsonArray();
        foreach (var v in visitors)
        {
            arr.Add(new JsonObject
            {
                ["roleid"] = v.RoleId,
                ["girlid"] = v.GirlId,
                ["modelid"] = v.ModelId,
                ["name"] = v.Name,      // ★ 必须是字符串，不能是 null
                ["time"] = v.Time,
                ["comfortscore"] = v.ComfortScore,
            });
        }
        return arr;
    }

    /// <summary>
    /// 组 LuaCall 241 的回包 tbParam：<c>{nRet = 0, nNum = &lt;家具数&gt;}</c>。
    /// 字段名与原服务端分支逐字一致（LuaCall.lua:4478..4480）；
    /// <c>nRet</c> 必须存在（else 分支的 <c>if tbParam.nRet then</c> 是唯一闸门，
    /// Lua 里 0 为真所以 0 能过），<c>nNum</c> 必须存在（直接赋给 <c>CoffeeCommon.NowFurNum</c>，
    /// 默认值 0，CoffeeCommon.lua:130）。
    /// </summary>
    internal static JsonObject BuildFurNumPayload(int furnitureCount) => new()
    {
        ["nRet"] = 0,
        ["nNum"] = furnitureCount,
    };

    // =================================================================
    //  服务端状态：Player.Extra["cafe"]
    // =================================================================

    /// <summary>
    /// 读出本玩家的咖啡馆标量。优先取 <c>Player.Extra["cafe"]</c>；
    /// 等级缺失时回落到任务值 (1,32)（客户端读等级的正式通道，见 <see cref="LevelTaskGroup"/>），
    /// 再兜底到 <see cref="MinLevel"/>。
    ///
    /// <paramref name="table"/> 非 null 时还会按配置表钳位（这两条都是「不钳就会让客户端抛」的硬约束，
    /// 取证见类注释 §0b）：
    /// <list type="bullet">
    ///   <item><c>level</c> → <c>[1, CafeTable.MaxCafeLevel]</c>（现网 30）</item>
    ///   <item><c>comfort</c> → <c>[0, CafeTable.MaxComfort]</c>（现网 40200，越界会静默退化成零加成）</item>
    /// </list>
    /// </summary>
    internal static CafeState ReadState(Player player, CafeTable? table = null)
    {
        var node = player.Extra.TryGetValue(ExtraKey, out var v) ? v as JsonObject : null;

        int level = ReadInt(node?["level"], 0);
        if (level <= 0)
            level = player.GetTask(LevelTaskGroup, LevelTaskId);
        if (level < MinLevel)
            level = MinLevel;

        int comfort = Math.Max(0, ReadInt(node?["comfort"], 0));

        if (table is not null)
        {
            int maxLevel = Math.Max(MinLevel, table.MaxCafeLevel);
            if (level > maxLevel)
            {
                Log.Warn($"[cafe] 咖啡馆等级 {level} 超出可下发上限 {maxLevel}" +
                         $"（{CafeTable.LevelFileName} 最大普通等级 ∩ {CafeTable.ItemTypeFileName} 餐位 Limit 长度），" +
                         "钳位后下发 —— 否则 CafeEditor.lua:112 会 error()，进店直接失败");
                level = maxLevel;
            }
            if (comfort > table.MaxComfort)
            {
                Log.Warn($"[cafe] 舒适度 {comfort} 超出 {CafeTable.ComfortFileName} 的最大区间上限 " +
                         $"{table.MaxComfort}，钳位后下发 —— 否则 GirlVigourManager.lua:208 匹配不到区间、" +
                         "会静默退化成「零加成」（收益/心情恢复加成全变 0）");
                comfort = table.MaxComfort;
            }
        }

        return new CafeState(level, Math.Max(0, ReadInt(node?["hot"], 0)), comfort);
    }

    /// <summary>
    /// 读出（并在需要时重算）咖啡馆的经营内容。
    ///
    /// 存档里的清单一律**先按配置表校验再下发**，理由见各 <c>Build*List</c> 的「无 nil 保护」条目：
    /// 一条脏数据（比如 <c>coffeetype = 99</c>）就能让客户端在 <c>RspDownloadCafeData</c> 里抛，
    /// 而那一抛就等于「点咖啡馆没反应、连加载条都不出现」。
    ///
    /// 「今日顾客」<c>weightlist</c> 按 <see cref="CafeDayIndex"/> 每日重算一次
    /// （同一天内重复进店必须拿到同一份，否则界面上的出现概率会跳），见 <see cref="RollDailyGuests"/>。
    /// </summary>
    internal static CafeContent ReadContent(Player player, CafeState state, CafeTable table,
                                            int dayIndex, int sessionId = 0)
    {
        var node = player.Extra.TryGetValue(ExtraKey, out var v) ? v as JsonObject : null;
        var lv = table.LevelRowFor(state.Level);

        var seats = ReadSeats(node?["seatlist"] as JsonArray, table, lv, sessionId);
        var queue = ReadQueue(node?["customerqueue"] as JsonArray, table, lv, sessionId);
        var coffees = ReadCoffees(node?["coffeelist"] as JsonArray, table, lv, sessionId);
        var girls = ReadAreaGirls(node?["roomgirlslist"] as JsonArray, player, lv, sessionId);
        var visitors = ReadVisitors(node?["visitedList"] as JsonArray);

        // 今日顾客：存档里的那天不是今天（或压根没有）就重算
        int storedDay = ReadInt(node?["weightday"], int.MinValue);
        List<CafeGuestWeight> weights;
        if (storedDay == dayIndex)
        {
            weights = ReadWeights(node?["weightlist"] as JsonArray, table);
            if (weights.Count == 0 || weights.Any(w => !IsGuestUnlocked(table.Guests[w.Id], player, state.Level)))
                weights = RollDailyGuests(table, state.Level, player.Roleid, dayIndex, player);
        }
        else
        {
            weights = RollDailyGuests(table, state.Level, player.Roleid, dayIndex, player);
        }

        return new CafeContent
        {
            Seats = seats,
            Queue = queue,
            Coffees = coffees,
            AreaGirls = girls,
            Weights = weights,
            Visitors = visitors,
            WeightDay = dayIndex,
        };
    }

    /// <summary>
    /// 把标量 + 经营内容写回 <c>Player.Extra["cafe"]</c>，并把等级镜像进任务值 (1,32)。
    /// 返回是否发生了变化（调用方据此决定要不要落盘，避免每次进主城都写一遍存档）。
    ///
    /// ★ 实现上是「克隆原对象再覆写」而不是「新建对象」：
    /// <list type="bullet">
    ///   <item><paramref name="content"/> 为 null 时（只想改标量）**不会**抹掉已存的清单；</item>
    ///   <item>将来别的地方往 <c>["cafe"]</c> 里加键也不会被本方法吃掉。</item>
    /// </list>
    /// 至于宠物域：它用的是**另一个** Extra 键 <c>["cafepet"]</c>
    /// （<see cref="CafePetFeature.ExtraKey"/>），本方法只碰 <c>["cafe"]</c>，两者互不影响
    /// —— 这条有单测把关。
    /// </summary>
    internal static bool WriteState(Player player, CafeState state, CafeContent? content = null)
    {
        bool changed = false;

        string? cur = player.Extra.TryGetValue(ExtraKey, out var old) ? old?.ToJsonString() : null;
        var obj = old is JsonObject prev ? (JsonObject)prev.DeepClone() : new JsonObject();

        obj["level"] = state.Level;
        obj["hot"] = state.Hot;
        obj["comfort"] = state.Comfort;

        if (content is not null)
        {
            obj["seatlist"] = BuildSeatList(content.Seats);
            obj["customerqueue"] = BuildQueueList(content.Queue);
            obj["coffeelist"] = BuildCoffeeList(content.Coffees);
            obj["roomgirlslist"] = BuildRoomGirlsList(content.AreaGirls);
            obj["weightday"] = content.WeightDay;
            obj["weightlist"] = BuildWeightList(content.Weights);
            obj["visitedList"] = BuildVisitedList(content.Visitors);
        }

        if (!string.Equals(cur, obj.ToJsonString(), StringComparison.Ordinal))
        {
            player.Extra[ExtraKey] = obj;
            changed = true;
        }

        // 客户端 CafeLogic:GetCafeLevel() 只认任务值 (1,32)，必须同步，
        // 否则商店解锁（UI_ShopNew.lua:142 NeedCafeLevel）与休息区解锁（UI_NCafe.lua:312）判定会用 0。
        if (player.GetTask(LevelTaskGroup, LevelTaskId) != state.Level)
        {
            player.SetTask(LevelTaskGroup, LevelTaskId, state.Level);
            changed = true;
        }

        return changed;
    }

    // =================================================================
    //  「今日顾客」每日重算
    // =================================================================

    /// <summary>
    /// 抽出本日的「今日顾客」清单及其权重。
    ///
    /// <para><b>为什么必须是服务端现算的</b></para>
    /// <c>NCafeGuests.txt</c> 的 <c>Weight</c>（出现权重）列现网 <b>12 行全是 0</b>，
    /// 真值在 <c>WeightMin</c>/<c>WeightMax</c> 两列；而 <c>NCafeLevels.txt</c> 有四列
    /// <c>MaxGuestTypes</c>/<c>Type1Num</c>/<c>Type2Num</c>/<c>OtherTypeNum</c>
    /// **在 806 个 lua 文件里零引用**（<c>grep -rn 'MaxGuestTypes\|Type1Num\|Type2Num\|OtherTypeNum' extracted/lua</c>
    /// 只命中 <c>PhoneMsgLogic.lua:68</c> 的一个同名局部变量）⇒ 它们是**纯服务端列**。
    /// 两条合起来只能解释成：原服务端每日从解锁的顾客里按
    /// 「<c>Type1Num</c> 个 1 星 + <c>Type2Num</c> 个 2 星 + <c>OtherTypeNum</c> 个 3 星，
    /// 合计 <c>MaxGuestTypes</c> 种」抽签，每种的权重在 <c>[WeightMin, WeightMax]</c> 里随机，
    /// 再把 <c>{Id, Weight}</c> 列表塞进 <c>weightlist</c>。
    /// 1 级的那组数就是 <c>4 = 2 + 1 + 1</c>，31 行等级里三者之和恒等于 <c>MaxGuestTypes</c>，自洽。
    ///
    /// <para><b>随机必须可复现</b></para>
    /// 种子 = <c>splitmix64(roleid ^ splitmix64(dayIndex))</c> —— 只用 roleid 与日序号，
    /// 于是「同一天内反复进店」拿到同一份（客户端界面上的出现概率不会跳），
    /// 跨天自动换一批。**不能用 <c>HashCode.Combine</c>**：.NET 的字符串/结构哈希种子每进程随机，
    /// 服务端重启后同一天的今日顾客就变了。
    /// </summary>
    internal static bool IsGuestUnlocked(CafeGuestRow guest, Player player, int level)
        => guest.NeedLv <= level && (guest.NeedCoffee <= 0 ||
            (player.GetTask(CafeBizFeature.CoffeeTaskGroup, guest.NeedCoffee) &
                (1 << CafeBizFeature.CoffeeLearnedBit)) != 0);

    internal static List<CafeGuestWeight> RollDailyGuests(CafeTable table, int cafeLevel,
                                                          ulong roleid, int dayIndex, Player? player = null)
    {
        var result = new List<CafeGuestWeight>();
        var pool = table.GuestsUnlockedAt(cafeLevel)
            .Where(g => player is null || IsGuestUnlocked(g, player, cafeLevel)).ToList();
        if (pool.Count == 0)
            return result;   // NCafeGuests.txt 缺失 → 只能发空清单（安全，见 BuildWeightList）

        var lv = table.LevelRowFor(cafeLevel);
        int want1 = Math.Max(0, lv?.Type1Num ?? 0);
        int want2 = Math.Max(0, lv?.Type2Num ?? 0);
        int wantOther = Math.Max(0, lv?.OtherTypeNum ?? 0);
        int total = Math.Max(0, lv?.MaxGuestTypes ?? 0);
        if (total <= 0)
            total = want1 + want2 + wantOther;
        if (total <= 0)
            total = pool.Count;   // NCafeLevels.txt 缺失 → 退化成「全部顾客都是今日顾客」

        ulong seed = Mix(roleid ^ Mix(unchecked((ulong)(long)dayIndex)));

        // 先按确定性哈希打乱（同分时按 Id 兜底，保证完全可复现），再按星级分档取
        var ordered = pool
            .OrderBy(g => Mix(seed ^ unchecked((ulong)g.Id * 0x9E3779B1UL)))
            .ThenBy(g => g.Id)
            .ToList();

        var picked = new List<CafeGuestRow>();
        Pick(ordered, picked, total, want1, g => g.Star == 1);
        Pick(ordered, picked, total, want2, g => g.Star == 2);
        Pick(ordered, picked, total, wantOther, g => g.Star >= 3);
        // 某一档不够（比如低等级还没解锁 3 星顾客）就用剩下的补齐，保证凑到 MaxGuestTypes
        Pick(ordered, picked, total, total - picked.Count, _ => true);

        foreach (var g in picked.OrderBy(g => g.Id))
        {
            // ★ 下界至少 1：weightlist 的权重之和为 0 会让客户端 NCafeNet.lua:167 除出 nan
            int lo = Math.Max(1, g.WeightMin);
            int hi = Math.Max(lo, g.WeightMax);
            int span = hi - lo + 1;
            int w = lo + (int)(Mix(seed ^ (0xC0FFEEUL + (ulong)g.Id)) % (ulong)span);
            result.Add(new CafeGuestWeight(g.Id, w));
        }
        return result;
    }

    private static void Pick(List<CafeGuestRow> ordered, List<CafeGuestRow> picked,
                             int total, int want, Func<CafeGuestRow, bool> match)
    {
        foreach (var g in ordered)
        {
            if (want <= 0 || picked.Count >= total)
                return;
            if (picked.Contains(g) || !match(g))
                continue;
            picked.Add(g);
            want--;
        }
    }

    /// <summary>splitmix64 的 finalizer：给定输入产出稳定的 64 位散列（与进程、平台无关）。</summary>
    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    // =================================================================
    //  存档 → 内存（含校验）
    // =================================================================

    private static List<CafeSeat> ReadSeats(JsonArray? arr, CafeTable table, CafeLevelRow? lv, int sessionId)
    {
        var result = new List<CafeSeat>();
        if (arr is null)
            return result;

        int limit = lv is { SlotEat: > 0 } ? lv.SlotEat : int.MaxValue;
        foreach (var node in arr)
        {
            if (node is not JsonObject o)
                continue;
            int position = ReadInt(o["position"], 0);
            if (position <= 0)
                continue;   // position 是 UpdateSeatGuest 的第一参，0/缺失一律丢
            if (result.Count >= limit)
            {
                Log.Warn($"[session {sessionId}] 咖啡馆座位数超过 {CafeTable.LevelFileName} 的 SlotEat={limit}，" +
                         "多余的丢弃（客户端 NCafe.lua:110 FixSeatOverLimit 也会自己回收）");
                break;
            }

            int? idx = o.ContainsKey("customeridx") ? ReadInt(o["customeridx"], 0) : null;
            int type = ReadInt(o["customertype"], 0);
            if (idx is not null && (idx <= 0 || !table.Guests.ContainsKey(type)))
            {
                // 顾客非法：保留座位、去掉顾客（★ 必须整个省略 customeridx，填 0 会被 Lua 当真，见 BuildSeatList）
                Log.Warn($"[session {sessionId}] 座位 {position} 上的顾客 customertype={type} 不在 " +
                         $"{CafeTable.GuestFileName} 里，按空座位下发");
                idx = null;
            }

            result.Add(idx is null
                ? new CafeSeat(position, null, 0, 0, 0)
                : new CafeSeat(position, idx, type, ReadInt(o["coffeetype"], 0), ReadLong(o["starttime"], 0)));
        }
        return result;
    }

    private static List<CafeQueueGuest> ReadQueue(JsonArray? arr, CafeTable table, CafeLevelRow? lv, int sessionId)
    {
        var result = new List<CafeQueueGuest>();
        if (arr is null)
            return result;

        int limit = lv is { MaxQSize: > 0 } ? lv.MaxQSize : int.MaxValue;
        foreach (var node in arr)
        {
            if (node is not JsonObject o)
                continue;
            int type = ReadInt(o["customertype"], 0);
            int idx = ReadInt(o["customeridx"], 0);
            if (idx <= 0 || !table.Guests.ContainsKey(type))
            {
                Log.Warn($"[session {sessionId}] 排队顾客 customertype={type} customeridx={idx} 非法，丢弃");
                continue;
            }
            if (result.Count >= limit)
            {
                Log.Warn($"[session {sessionId}] 排队人数超过 {CafeTable.LevelFileName} 的 MaxQSize={limit}，多余的丢弃");
                break;
            }
            result.Add(new CafeQueueGuest(type, idx, ReadLong(o["starttime"], 0)));
        }
        return result;
    }

    private static List<CafeCoffee> ReadCoffees(JsonArray? arr, CafeTable table, CafeLevelRow? lv, int sessionId)
    {
        var result = new List<CafeCoffee>();
        if (arr is null)
            return result;

        int limit = Math.Min(CoffeeUiSlotCount, lv is { SlotCoffee: > 0 } ? lv.SlotCoffee : CoffeeUiSlotCount);
        foreach (var node in arr)
        {
            if (node is not JsonObject o)
                continue;
            int type = ReadInt(o["coffeetype"], 0);
            int count = ReadInt(o["count"], 0);
            if (!table.Coffees.ContainsKey(type))
            {
                // ★★ 这一条是「不校验就会让 map 6 永远进不去」的那个字段，见 BuildCoffeeList
                Log.Warn($"[session {sessionId}] 橱窗咖啡 coffeetype={type} 不在 {CafeTable.CoffeeFileName} 里，" +
                         "丢弃 —— 否则客户端 NCafeNet.lua:136 会在 template.Time 上抛，Map.LoadById(6) 永不执行");
                continue;
            }
            if (count <= 0)
                continue;   // 空槽不发（客户端 :134 的 if not next(v) 说明原服会发 {}，两者等价）
            if (result.Count >= limit)
            {
                Log.Warn($"[session {sessionId}] 橱窗咖啡种类超过上限 {limit}" +
                         $"（min(SlotCoffee, UI 4 格)），多余的丢弃");
                break;
            }
            result.Add(new CafeCoffee(type, count));
        }
        return result;
    }

    internal static List<IReadOnlyList<CafeAreaGirl>> ReadAreaGirls(JsonArray? arr, Player player,
                                                                   CafeLevelRow? lv, int sessionId)
    {
        var result = new List<IReadOnlyList<CafeAreaGirl>>();
        for (int i = 0; i < AreaCount; i++)
            result.Add(new List<CafeAreaGirl>());
        if (arr is null)
            return result;

        int[] limits =
        [
            lv?.CafeUnlockNum ?? 0,
            lv?.RestUnlockNum ?? 0,
            lv?.DormUnlockNum ?? 0,
        ];

        var assigned = new HashSet<int>();
        for (int area = 0; area < AreaCount && area < arr.Count; area++)
        {
            if (arr[area] is not JsonArray slot)
                continue;
            var list = (List<CafeAreaGirl>)result[area];
            foreach (var node in slot)
            {
                // 内层是二元数组 [girlId, modelId]（NCafeNet.lua:149 girl[1]/girl[2]）
                if (node is not JsonArray pair || pair.Count < 1)
                    continue;
                int girlId = ReadInt(pair[0], 0);
                if (girlId <= 0)
                    continue;

                // 只保留玩家真拥有的少女：客户端 LoadGirl（RVA 0x1D59C30）在
                // 0x1D59D60 处 `Player.me.GetGirl(girlId)` 为 null 就直接 return（安全跳过），
                // 所以这里丢掉只是让存档干净，不是崩溃防线。
                var owned = player.GetGirl((uint)girlId);
                if (owned is null)
                {
                    Log.Warn($"[session {sessionId}] 排班少女 girlId={girlId} 不在玩家角色卡里，丢弃");
                    continue;
                }
                if (list.Count >= Math.Max(0, limits[area]))
                {
                    Log.Warn($"[session {sessionId}] 区域 {area + 1} 的少女数超过上限 {limits[area]}" +
                             $"（{CafeTable.LevelFileName} 的 Cafe/Rest/DormUnlockNum），多余的丢弃");
                    break;
                }
                // ★ ModelId 以服务端存档为准，且 **0 是合法值**：
                //   C# `NCafeCharacterController.InitGirls`（0x1D5C00C）在 0x1D5C204 判
                //   `IsInSelfCafe()`，为 true 时把 modelId **强制置 0**（0x1D5C20C）——
                //   也就是「自己的咖啡馆」根本不用下行的 modelId；
                //   而 `CreateGirl`（0x1D5E504）拼 prefab 路径用的是 `girlId`
                //   （格式串 "CafeGirl{0}" @0x1D5E5FC），modelId 只参与后续换装。
                //   所以这里既不采信客户端上传值，也不因为 0 而丢弃。
                if (!assigned.Add(girlId)) continue;
                list.Add(new CafeAreaGirl(girlId, (int)owned.Modelid));
            }
        }
        return result;
    }

    private static List<CafeGuestWeight> ReadWeights(JsonArray? arr, CafeTable table)
    {
        var result = new List<CafeGuestWeight>();
        if (arr is null)
            return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject o)
                continue;
            int id = ReadInt(o["Id"], 0);       // ★ 大写键，与下发形态一致
            int weight = ReadInt(o["Weight"], 0);
            if (!table.Guests.ContainsKey(id))
                continue;                       // 表外的 id 会让 UI_CafeHelp.lua:196 抛
            result.Add(new CafeGuestWeight(id, Math.Max(1, weight)));
        }
        return result;
    }

    private static List<CafeVisitor> ReadVisitors(JsonArray? arr)
    {
        var result = new List<CafeVisitor>();
        if (arr is null)
            return result;
        foreach (var node in arr)
        {
            if (node is not JsonObject o)
                continue;
            ulong roleid = (ulong)Math.Max(0, ReadLong(o["roleid"], 0));
            if (roleid == 0)
                continue;
            result.Add(new CafeVisitor(
                roleid,
                ReadInt(o["girlid"], 0),
                ReadInt(o["modelid"], 0),
                o["name"]?.ToString() ?? "",    // ★ 绝不下发 null（UpdateVisitedInfo 第 4 参是 string）
                ReadLong(o["time"], 0),
                ReadInt(o["comfortscore"], 0)));
        }
        return result;
    }

    /// <summary>读整数；Lua/JSON 侧可能给 float 或字符串，兜一层。</summary>
    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var d) ? (int)d : fallback;
        }
    }

    /// <summary>读 64 位整数（<c>starttime</c>/<c>time</c>/<c>roleid</c> 在 proto 里是 uint64）。</summary>
    private static long ReadLong(JsonNode? node, long fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<long>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var d) ? (long)d : fallback;
        }
    }

    // =================================================================
    //  数据类型
    // =================================================================

    /// <summary>
    /// 咖啡馆的服务端标量三件套，对应 proto <c>gsproto.CafePb</c> 的 level/hot/comfort。
    /// 清单类数据在 <see cref="CafeContent"/> 里。
    /// </summary>
    internal readonly record struct CafeState(int Level, int Hot, int Comfort);

    /// <summary>
    /// 一个餐位（proto <c>gsproto.CafePb.GuestPb</c>）。
    /// <paramref name="CustomerIdx"/> 为 null = 空座位，下发时会**整个省略**四个顾客键 ——
    /// 填 0 会被 <c>NCafeNet.lua:107</c> 的真值判断当成「有顾客」，见 <see cref="BuildSeatList"/>。
    /// </summary>
    internal sealed record CafeSeat(int Position, int? CustomerIdx, int CustomerType, int CoffeeType, long StartTime);

    /// <summary>一个排队顾客（同样是 <c>GuestPb</c>，但只用到三个字段）。</summary>
    internal sealed record CafeQueueGuest(int CustomerType, int CustomerIdx, long StartTime);

    /// <summary>橱窗里的一种咖啡（proto <c>gsproto.CafePb.CoffeePb</c>）。</summary>
    internal sealed record CafeCoffee(int CoffeeType, int Count);

    /// <summary>某个区域里的一个少女（下发时是二元数组 <c>[GirlId, ModelId]</c>）。</summary>
    internal sealed record CafeAreaGirl(int GirlId, int ModelId);

    /// <summary>一条「今日顾客」权重（下发键是大写的 <c>Id</c>/<c>Weight</c>）。</summary>
    internal sealed record CafeGuestWeight(int Id, int Weight);

    /// <summary>一条访客记录（proto <c>gsproto.VisitedInfo</c>）。</summary>
    internal sealed record CafeVisitor(ulong RoleId, int GirlId, int ModelId, string Name,
                                       long Time, int ComfortScore);

    /// <summary>
    /// 咖啡馆的经营内容：LuaCall 112 回包里六个清单字段的服务端形态。
    /// <see cref="Empty"/> = 空咖啡馆（没座位、没排队、没上咖啡、没排班少女、没今日顾客、没访客）。
    /// </summary>
    internal sealed class CafeContent
    {
        /// <summary>空咖啡馆基线。</summary>
        public static CafeContent Empty { get; } = new();

        /// <summary>餐位（<c>seatlist</c>）。</summary>
        public IReadOnlyList<CafeSeat> Seats { get; init; } = [];

        /// <summary>排队顾客（<c>customerqueue</c>）。</summary>
        public IReadOnlyList<CafeQueueGuest> Queue { get; init; } = [];

        /// <summary>橱窗咖啡（<c>coffeelist</c>）。</summary>
        public IReadOnlyList<CafeCoffee> Coffees { get; init; } = [];

        /// <summary>
        /// 三个区域的少女（<c>roomgirlslist</c>）：下标 0/1/2 = 经营区/休息区/宿舍。
        /// 长度要么是 0（完全没数据），要么是 <see cref="AreaCount"/>。
        /// </summary>
        public IReadOnlyList<IReadOnlyList<CafeAreaGirl>> AreaGirls { get; init; } = [];

        /// <summary>今日顾客及权重（<c>weightlist</c>）。</summary>
        public IReadOnlyList<CafeGuestWeight> Weights { get; init; } = [];

        /// <summary>访客记录（<c>visitedList</c>）。</summary>
        public IReadOnlyList<CafeVisitor> Visitors { get; init; } = [];

        /// <summary><see cref="Weights"/> 是哪一天抽的（<see cref="CafeDayIndex"/> 的日序号）。</summary>
        public int WeightDay { get; init; }
    }
}
