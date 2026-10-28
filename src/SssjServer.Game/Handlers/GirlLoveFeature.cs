using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 少女好感度 / 羁绊剧情域。完整取证见 `work/girl-story-forensics.md`、台账 `docs/05` §27。
///
/// <para><b>0. ★★★ 本域跨【两个 sFunction】、含【三种】错误约定</b></para>
/// <list type="table">
///   <item><term><c>GirlLogic/GirlGift</c>（送礼）</term>
///     <description>少女养成族（第五种）：客户端找 <c>On&lt;sCmd&gt;</c>、<c>nError</c> **真值**判断
///       ⇒ 成功**绝不带** <c>nError</c>；失败**只传 <c>nError</c> 数值**（其余字段全丢）。
///       ★★ <b>有遮罩</b>（`GirlCommon.lua:346` 无条件 <c>Blocking.Show()</c>）⇒ **不回包必卡死**。</description></item>
///   <item><term><c>GirlLogic/ViewStory</c>（看剧情）</term>
///     <description>同上族。★ 回包**必须原样带回 <c>nId</c>/<c>nType</c>/<c>nStoryId</c>** ——
///       `GirlLogic.lua:776` 第一句 <c>if not nType then return end</c>，漏了整条静默失效。</description></item>
///   <item><term>LuaCall <b>174</b>（少女任务领奖）</term>
///     <description>LuaCall 数字族：成功**绝不带** <c>nError</c>。★ 且**必须带
///       <c>reward</c> 字符串 + <c>love</c> 表**，两个守卫各自 return。</description></item>
///   <item><term>LuaCall <b>255</b> / <b>256</b> / <b>175</b> / <b>173</b></term>
///     <description>255 原样回显（else 分支是空的）；<b>256/175/173 一定不要回包</b>
///       （原服 <c>MODULE_SERVER</c> 分支里 256 连 <c>CallClient</c> 都没有，173/175 是
///       未定义变量 <c>tbRet</c> 的 bug）。</description></item>
/// </list>
///
/// <para><b>1. ★★★ 好感度存 protobuf，不存任务值</b></para>
/// 等级 = <c>gsproto.GirlInfo.level</c>(字段 2)、经验 = <c>.exp</c>(字段 3)
/// （取证 B.1：<c>GirlVarOffset.Exp/Level</c> 偏移都是 **0** ⇒ <c>GirlCommon:Get</c> 的
/// <c>if GirlVarOffset[sKey] &gt; 0</c> 分支走不到任务值那条路，走 <c>me:GetGirl</c>）。
/// ⇒ 通道是 **pf 1030 <c>GirlDataNtf</c>**（<see cref="GameNotify.PushGirl"/>），不是 pf 1026。
///
/// <para><b>2. ★★ 剧情已读位 <c>ELevel&lt;n&gt;</c> 是三态，且下标不是「第几个剧情」</b></para>
/// 任务值 group 3 偏移 <b>300 + n</b>，其中 <c>n</c> = <c>Fetter.txt</c> 的
/// <c>Main[1]</c>/<c>Sub&lt;j&gt;[1]</c>（**不是** <c>Index</c> 列、**不是** UI 顺序号）。
/// 三态：<b>0 = 未解锁 / 1 = 已解锁未读 / &gt;1 = 已读</b>（取证 C.0 ③）。
/// ★★★ **必须由服务端在登录时把够条件的档位置 1**，否则
/// `UI_GirlLove.lua:142` 的 <c>nValue == 1</c> 恒假 ⇒ 没有「NEW」标记，
/// 且 `CartoonAutoPlay.lua:89` 的 <c>state == 1</c> 恒假 ⇒ **自动播放路径完全不触发**。
///
/// <para><b>3. ★★ 「发奖 + 置已读」必须原子，否则可重复领</b></para>
/// 取证 E.3 ③：<c>ELevel</c> 置 &gt;1 与发奖是同一个动作，只发奖不置位 ⇒ 剧情奖励无限领。
/// 174 同理必须置任务值 <c>(5, missionId)</c> 的 **bit0 = 已领**（E.3 ①），
/// 否则客户端 <c>EventStep</c> 停在 1、按钮持续可点。
///
/// <para><b>4. ★ 联动少女走另一套 group（3→90）且 girlId 要减 200</b></para>
/// <c>GetExtGroup</c>（`GirlCommon.lua:1495`）+ <c>GetGirlTaskEx</c>（`:1508`）。
/// 见 <see cref="VarGroup"/> / <see cref="VarTaskId"/> —— <see cref="GmUnlock.GirlTaskId"/>
/// 本身**没有**处理这个重映射，所以本域自己实现了一份。
/// </summary>
public sealed class GirlLoveFeature : IGameFeature
{
    /// <summary>下行入口（与特训域共用 <c>sFunction</c>，见 <see cref="Install"/> 的说明）。</summary>
    public const string Function = "GirlLogic";

    /// <summary>送礼（`GirlLogic.lua:646`，上行 <c>{nId, tbItem[4], ItemNum}</c>）。</summary>
    public const string CmdGirlGift = "GirlGift";

    /// <summary>看剧情（`GirlLogic.lua:764`，上行 <c>{nId, nType, nStoryId}</c>）。</summary>
    public const string CmdViewStory = "ViewStory";

    /// <summary>看联动剧情（`GirlLogic.lua:813`，上行 <c>{nId, Level}</c>）。</summary>
    public const string CmdViewLinkStory = "ViewLinkStory";

    /// <summary>LuaCall 173 少女任务完成（★ 只记账、原服回空表）。</summary>
    public const int CmdGirlMissionFinish = 173;

    /// <summary>LuaCall 174 少女任务领奖（★ 六条里唯一真发奖的）。</summary>
    public const int CmdGirlMissionReward = 174;

    /// <summary>LuaCall 175 少女剧情爆炸选项（心情值）。</summary>
    public const int CmdGirlMoodBoom = 175;

    /// <summary>LuaCall 255 清「新少女」红点（上行字段是**大写** <c>GirlId</c>）。</summary>
    public const int CmdGirlUpdateFlag = 255;

    /// <summary>LuaCall 256 记录大剧情观看进度（★ 一定不回包）。</summary>
    public const int CmdGirlSetMainStoryId = 256;

    /// <summary>少女属性偏移：<c>MainStoryId</c>（`GirlCommon.lua:44`）。</summary>
    public const int VarMainStoryId = 14;

    /// <summary>
    /// 少女属性区段起点：<c>ELevel&lt;index&gt;</c> = 偏移 <b>300 + index</b>
    /// （`GirlCommon.lua:70..78` 的 <c>RegisterGirlVar('ELevel', 300, 399)</c>）。
    /// </summary>
    public const int VarELevelBase = 300;

    /// <summary><c>ELevel</c> 区段上界（含）。</summary>
    public const int VarELevelMax = 399;

    /// <summary><c>ELevel</c> 三态：已解锁未读（客户端据此显示 NEW + 允许自动播放）。</summary>
    public const int ELevelUnread = 1;

    /// <summary><c>ELevel</c> 三态：已读（&gt;1 即已读，取 2）。</summary>
    public const int ELevelRead = 2;

    /// <summary>
    /// 送礼失败码 **4** = 已满级 —— ★ 客户端 <c>OnGirlGiftError</c> **只认这一个值**
    /// （`GirlLogic.lua:691..693`：<c>if nCode == 4 then</c> 弹 `Girl.MaxLevel`）。
    /// 其余码一律无提示（静默），所以「道具不足」这类只能靠日志排查。
    /// </summary>
    public const int ErrGiftMaxLevel = 4;

    /// <summary>送礼失败码：通用（客户端无提示）。</summary>
    public const int ErrGeneric = 1;

    /// <summary>礼物道具的 genre（`UI_GirlGift.lua:220` <c>GetItemListByGenre(.., 5)</c>）。</summary>
    public const int GiftGenre = 5;

    /// <summary>
    /// 少女任务的任务值 group（`MissionManager.lua` 的 <c>Mission_Group</c>）——
    /// 与 <see cref="MissionFeature"/> 用的是同一个 group 5，bit0 = 已领、bit1..31 = 进度。
    /// </summary>
    public const int MissionGroup = 5;

    /// <summary>
    /// 「当前少女事件 id」存在 <c>(5, 20001)</c>（`MissionManager.lua:609..611`
    /// <c>GetCurGirlEventId</c>）。174 的上行 <c>nId</c> 就是它的值，**不是少女 id**。
    /// </summary>
    public const int CurGirlEventTaskId = 20001;

    /// <summary>
    /// 「今天是哪个少女」存在 <c>(5, 20000)</c>（`MissionManager.lua:7` <c>Girl_TaskId</c> /
    /// `:614` <c>GetCurGirlEventGirlId</c>）。★ 少女 id **不在配置表里**，只在这个任务值里。
    /// </summary>
    public const int CurGirlTaskId = 20000;

    private GameFeatureContext _ctx = null!;
    private PledgeTable _pledge = PledgeTable.Empty;
    private GirlLoveTable _love = GirlLoveTable.Empty;

    public string Name => "少女好感度/羁绊剧情（GirlGift/ViewStory + LuaCall 173,174,175,255,256）";

    /// <summary>
    /// 装配。
    ///
    /// ★★ <b>为什么这里不调 <c>ctx.MapFunction(Function, ...)</c></b>：
    /// <c>sFunction = "GirlLogic"</c> 已被 <see cref="GirlTrainFeature"/> 注册，而
    /// <see cref="LuaCallRouter.MapFunction"/> 是**覆盖式**的（<c>_funcMap[function] = handler</c>）
    /// —— 谁后装配谁赢，另一个域的命令**全部静默失效**（连 unhandled 日志都不会有，
    /// 因为路由压根没到那个域）。这类「同一个 sFunction 被两个功能域瓜分」的情况
    /// 本项目第一次出现，解法是本域用 <see cref="GameFeatureContext.AddFunctionCoHandler"/>
    /// **追加**一个协处理器：认自己的三个 sCmd、其余返回 <c>false</c> 交回特训域。
    ///
    /// ★★★ <b>不要改回「功能域里的 static 字典」</b>（本轮试过并被单测抓到）：
    /// 并行测试里每条用例新建一个 <c>GameModule</c>，后装配的实例会把静态键覆盖成
    /// 自己的闭包，于是别的 harness 的命令被路由到**另一个会话的 feature 实例**
    /// （其 <c>_ctx</c>/store 都不同）⇒ 表现为「随机几条用例没有任何下行」。
    /// 协处理器挂在 **router 实例**上才与 module 同生命周期。
    /// </summary>
    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _pledge = PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _love = GirlLoveTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.AddFunctionCoHandler(Function, OnGirlLogicCommand);

        ctx.MapLuaCall(CmdGirlMissionFinish, OnMissionFinish);
        ctx.MapLuaCall(CmdGirlMissionReward, OnMissionReward);
        ctx.MapLuaCall(CmdGirlMoodBoom, OnMoodBoom);
        ctx.MapLuaCall(CmdGirlUpdateFlag, OnUpdateFlag);
        ctx.MapLuaCall(CmdGirlSetMainStoryId, OnSetMainStoryId);

        // ★ 登录时补 ELevel 初值 —— 见类注释 §2。用 OnPlayerPrepare（1005 之前）
        // 可以让任务值随登录时本来就要发的 pf 1026 全量一起走，不额外发包。
        ctx.OnPlayerPrepare(SyncStoryUnlocks);
    }

    /// <summary>
    /// <c>GirlLogic</c> 的协处理器：只认本域的三个 sCmd，其余返回 <c>false</c>
    /// 让路由交回 <see cref="GirlTrainFeature"/> 的主 handler。
    /// </summary>
    private async Task<bool> OnGirlLogicCommand(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return false;

        string sCmd = (param as JsonObject)?["sCmd"]?.ToString() ?? "";
        switch (sCmd)
        {
            case CmdGirlGift:
                await OnGirlGift(session, player, param);
                return true;
            case CmdViewStory:
                await OnViewStory(session, player, param);
                return true;
            case CmdViewLinkStory:
                await OnViewLinkStory(session, player, param);
                return true;
            default:
                return false;
        }
    }

    // ================= 好感度核心算法 =================

    /// <summary>
    /// 加好感度经验，**1:1 移植 `GirlCommon:Add`**（`GirlCommon.lua:388..455`）。
    ///
    /// 原文的关键行为（逐条对应，别「优化」掉）：
    /// <list type="number">
    ///   <item><c>nValue &lt; 0</c> 直接返回（不允许扣经验）；</item>
    ///   <item>少女不存在返回；</item>
    ///   <item><b>已满级直接返回</b>（<c>nOldLevel &gt;= nMaxLevel</c>）—— 调用方据此回 <c>nError = 4</c>；</item>
    ///   <item><c>while nTotalExp &gt;= tbConfig.Exp</c> 逐级扣减（<c>Exp</c> 是**单级增量**）；</item>
    ///   <item>★ 撞上限时 <c>nValue -= nTotalExp; nTotalExp = 0</c>
    ///     —— 即<b>返回的「实际加了多少」会被截断</b>，且余下经验**丢弃**（原注释写明「舍弃」）。</item>
    /// </list>
    /// </summary>
    /// <returns>false = 没加成（少女不存在 / 已满级 / 曲线缺失）。</returns>
    internal bool AddExp(Player player, uint girlId, int value, out GirlExpDelta delta)
        => AddExp(_love, player, girlId, value, out delta);

    /// <summary>
    /// <see cref="AddExp(Player, uint, int, out GirlExpDelta)"/> 的静态版本 ——
    /// 把依赖的配置表**显式传进来**，供**别的功能域**复用同一条好感度曲线。
    ///
    /// ★★ 为什么做成静态 + 显式传表，而不是「跨 feature 拿实例」：
    /// 本项目 <see cref="GameFeatureContext"/> 有意**不提供** feature 实例查找
    /// （`HANDOFF` §8.0 教训 2：任何跨域共享状态都容易在并行单测里串）。
    /// 调用方（如 <see cref="PhoneMsgFeature"/> 的私信/回帖领奖）自己 <c>Load</c> 一份
    /// <see cref="GirlLoveTable"/> 即可 —— 配置表是**只读**的，多份实例无副作用。
    /// </summary>
    internal static bool AddExp(GirlLoveTable love, Player player, uint girlId, int value,
                                out GirlExpDelta delta)
    {
        delta = default;
        if (value < 0)
            return false;

        var girl = player.GetGirl(girlId);
        if (girl is null)
            return false;

        var _love = love;
        int maxLevel = _love.MaxLevel(girlId);
        int oldLevel = (int)girl.Level;
        if (oldLevel >= maxLevel)
        {
            delta = new GirlExpDelta(girlId, 0, (int)girl.Exp, (int)girl.Exp, oldLevel, oldLevel, true);
            return false;
        }

        int oldExp = (int)girl.Exp;
        int total = oldExp + value;
        int virLevel = oldLevel;
        int added = value;
        bool levelUp = false;

        int stepExp = _love.LevelUpExp(girlId, virLevel);
        if (stepExp <= 0)
        {
            // 曲线缺失或本级封顶（下一级被 ban）：等同满级
            delta = new GirlExpDelta(girlId, 0, oldExp, oldExp, oldLevel, oldLevel, true);
            return false;
        }

        while (total >= stepExp)
        {
            total -= stepExp;
            virLevel++;
            levelUp = true;

            if (virLevel >= maxLevel)
            {
                // ★ 撞上限：截断本次实际加的经验、丢弃余量（照原文）
                added -= total;
                total = 0;
                break;
            }

            stepExp = _love.LevelUpExp(girlId, virLevel);
            if (stepExp <= 0)
            {
                // 下一级不开放 ⇒ 停在这级、余量丢弃
                added -= total;
                total = 0;
                break;
            }
        }

        girl.Exp = (uint)total;
        if (levelUp)
            girl.Level = (uint)virLevel;

        delta = new GirlExpDelta(girlId, added, oldExp, total, oldLevel, virLevel, virLevel >= maxLevel);
        return true;
    }

    /// <summary>一次加经验的前后快照（回包要逐字段回填，见 B.4 / A.2）。</summary>
    /// <param name="GirlId">少女 id。</param>
    /// <param name="Added">★ **实际**加的经验（满级会被截断）。</param>
    /// <param name="OldExp">加之前的当级内经验。</param>
    /// <param name="NewExp">加之后的当级内经验。</param>
    /// <param name="OldLevel">加之前等级。</param>
    /// <param name="NewLevel">加之后等级。</param>
    /// <param name="IsMaxLevel">加完后是否已达上限（客户端据此弹「已满级」）。</param>
    internal readonly record struct GirlExpDelta(
        uint GirlId, int Added, int OldExp, int NewExp, int OldLevel, int NewLevel, bool IsMaxLevel);

    // ================= GirlGift 送礼 =================

    /// <summary>
    /// 送礼：扣道具 → 加好感度 → 推 pf 1030/1031 → 回 <c>OnGirlGift</c> 九个字段。
    ///
    /// 上行（`GirlLogic.lua:646..654`）：<c>{nId, tbItem = [G,D,P,L], ItemNum}</c>。
    /// ★ <c>tbItem</c> 是**位置数组**，不是对象。
    ///
    /// 经验来源：<c>ItemList.txt</c> 的 <b><c>ExtParam1</c></b>（30/60/150/300 四档，
    /// 随阶 L 递增，取证 B.4）。★ 该断言在客户端侧只有数值形态吻合、**服务端侧无确证**
    /// （<c>gift</c> 类的原服实现不在包内），属「推测（部分取证）」—— 但它是唯一
    /// 量级正确的候选列（<c>SellingPrice</c> 是 900/2300，明显是售价）。
    /// </summary>
    private Task OnGirlGift(ClientSession session, Player player, JsonNode? param)
    {
        var body = param as JsonObject;
        uint girlId = (uint)ReadInt(body?["nId"]);
        if (body?["ItemNum"] is not JsonValue number || !number.TryGetValue<int>(out int num) || num <= 0)
        {
            Fail(session, CmdGirlGift, ErrGeneric);
            return Task.CompletedTask;
        }
        var gdpl = ReadTuple(body?["tbItem"]);
        if (girlId == 0 || gdpl is null)
        {
            Fail(session, CmdGirlGift, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/GirlGift 参数非法 param={param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        var (g, d, p, l) = gdpl.Value;
        if (player.GetGirl(girlId) is null)
        {
            Fail(session, CmdGirlGift, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/GirlGift 少女 {girlId} 未持有");
            return Task.CompletedTask;
        }

        // ★ 满级先判：客户端只认 nCode == 4，这是唯一有提示的失败
        int maxLevel = _love.MaxLevel(girlId);
        if (maxLevel <= 1)
        {
            Fail(session, CmdGirlGift, ErrGiftMaxLevel);
            Log.Info($"[session {session.Id}] GirlLogic/GirlGift 少女 {girlId} 已满级（{maxLevel}）→ nError=4");
            return Task.CompletedTask;
        }

        if (g != GiftGenre)
        {
            Fail(session, CmdGirlGift, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/GirlGift 道具 {g}-{d}-{p}-{l} 不是礼物（genre 应为 {GiftGenre}）");
            return Task.CompletedTask;
        }

        // 单个礼物的好感经验
        int perExp = GiftExp(_ctx.ItemTemplates.Get((uint)g, (uint)d, (uint)p, (uint)l));
        if (perExp <= 0)
        {
            Fail(session, CmdGirlGift, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/GirlGift 道具 {g}-{d}-{p}-{l} 的 ExtParam1（好感经验）为 0，拒绝");
            return Task.CompletedTask;
        }

        // Validate the complete batch before changing either gifts or affection.
        long available = player.Items.Where(i => i.Genre == (uint)g && i.Detail == (uint)d
            && i.Particular == (uint)p && i.Level == (uint)l && i.LockOn == 0)
            .Sum(i => (long)i.Count);
        bool love = _love.IsLoveGift(girlId, g, d, p, l);
        long requestedExp = GiftExperiencePerItem(perExp, love, GirlLoveTable.IsLinkGirl(girlId)) * num;
        if (available < num || requestedExp > int.MaxValue - (long)player.GetGirl(girlId)!.Exp)
        {
            Fail(session, CmdGirlGift, ErrGeneric);
            return Task.CompletedTask;
        }

        bool gained = AddExp(player, girlId, (int)requestedExp, out var delta);
        var giftedGirl = player.GetGirl(girlId)!;
        int cap = _love.MaxLevel(girlId);
        if (!gained && (cap <= 1 || giftedGirl.Level < cap))
        {
            Fail(session, CmdGirlGift, ErrGiftMaxLevel);
            return Task.CompletedTask;
        }
        if (giftedGirl.Level >= cap)
        {
            int overflow = (int)requestedExp - delta.Added;
            giftedGirl.Exp += (uint)overflow;
            delta = delta with { Added = (int)requestedExp, NewExp = (int)giftedGirl.Exp };
        }
        var changed = new List<Item>();
        var removed = new List<Item>();
        int taken = TakeItems(player, g, d, p, l, num, changed, removed);
        PledgeFeature.RecordAction(_pledge, player, (int)girlId, "GiftCount", taken, l);
        var unlocked = UnlockStories(player, girlId);
        unlocked.AddRange(UnlockSecrets(_love, player, girlId, new[] { g, d, p, l }));

        _ctx.Store.Save(player);

        // ★ 先推数据、再回包：客户端 OnGirlAddExp 会重读本地 girl 缓存（同 AGENTS §5 的
        // 「回包 handler 不收参数 ⇒ 数值必须先到」那条经验）
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        if (changed.Count > 0)
            GameNotify.PushItems(session, changed);
        GameNotify.PushGirl(session, player.GetGirl(girlId)!);
        GameNotify.PushTaskValues(session, unlocked);

        var reply = new JsonObject
        {
            ["nId"] = girlId,
            ["bLove"] = love,
            ["bAct"] = false,           // 活动礼物：现网无活动礼物配置，恒 false
            ["bSp"] = false,            // 特殊活动礼物：同上
            ["nAddExp"] = delta.Added,
            ["nOldExp"] = delta.OldExp,
            ["nNewExp"] = delta.NewExp,
            ["nOldLevel"] = delta.OldLevel,
            ["nNewLevel"] = delta.NewLevel,
        };
        // ★ nIsMaxlevel 是**真值判断**（`GirlLogic.lua:675` if nIsMaxlevel then）
        // ⇒ 没满级时**绝不能带**（带 false 在 Lua 里也是假，但带 0 就会被判成真，
        //   统一用「不带」最安全）
        if (delta.IsMaxLevel)
            reply["nIsMaxlevel"] = true;

        Ok(session, CmdGirlGift, reply);
        Log.Info($"[session {session.Id}] GirlLogic/GirlGift 少女 {girlId} 收 {taken}×{g}-{d}-{p}-{l}" +
                 $"（{(love ? "喜爱" : "普通")}、单个 {perExp} 经验）→ 好感 " +
                 $"LV{delta.OldLevel}({delta.OldExp}) → LV{delta.NewLevel}({delta.NewExp})、实加 {delta.Added}" +
                 $"{(delta.IsMaxLevel ? "、已满级" : "")}");
        return Task.CompletedTask;
    }

    // ================= ViewStory 看剧情 =================

    /// <summary>
    /// 看剧情：发奖 + 置 <c>ELevel</c> 已读。
    ///
    /// 上行 <c>{nId, nType, nStoryId}</c>（`GirlLogic.lua:764`）；
    /// <c>nType</c> 1 = 好感剧情、3 = 卡面相册（C.4）。
    ///
    /// ★★ 回包**必须原样带回三个字段** —— `GirlLogic.lua:776` 第一句
    /// <c>if not nType then return end</c>；且**奖励弹窗内容由客户端自己从 <c>Fetter.txt</c> 查</c>
    /// （<c>pFindAward</c>，`:784..796`）⇒ 回包**不需要**带奖励列表，
    /// 但服务端**必须真的发那些道具**（pf 1031），否则「弹窗说得到了、背包里没有」。
    ///
    /// ★★★ 发奖与置已读必须原子（E.3 ③）：只发奖不置位 ⇒ 剧情奖励可无限领。
    /// </summary>
    private Task OnViewStory(ClientSession session, Player player, JsonNode? param)
    {
        uint girlId = (uint)ReadInt(param?["nId"]);
        int type = ReadInt(param?["nType"]);
        int storyId = ReadInt(param?["nStoryId"]);

        // ★ 无论成功失败都要原样回三字段（否则客户端 return，且这一族有遮罩）
        var reply = new JsonObject
        {
            ["nId"] = girlId,
            ["nType"] = type,
            ["nStoryId"] = storyId,
        };

        if (girlId == 0 || type == 0)
        {
            Ok(session, CmdViewStory, reply);
            Log.Warn($"[session {session.Id}] GirlLogic/ViewStory 参数非法 param={param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        // nType == 3 是卡面相册，只刷新界面、无奖励无已读位
        if (type != 1)
        {
            Ok(session, CmdViewStory, reply);
            Log.Info($"[session {session.Id}] GirlLogic/ViewStory 少女 {girlId} nType={type}（非好感剧情，仅回执）");
            return Task.CompletedTask;
        }

        var story = _love.StoryOf(girlId, storyId);
        var girl = player.GetGirl(girlId);
        if (story is null || girl is null || ELevelOffset(storyId) < 0 ||
            girl.Level < story.Level || !_love.IsFriendlinessUnlocked(girlId, story.Level) ||
            !StoryConditionMet(player, story))
        {
            Fail(session, CmdViewStory, ErrGeneric);
            return Task.CompletedTask;
        }
        int cur = GetVar(player, girlId, ELevelOffset(storyId));
        if (cur >= ELevelRead)
        {
            // 已读：不重复发奖（★ 这就是防无限领的那一道闸）
            Ok(session, CmdViewStory, reply);
            Log.Info($"[session {session.Id}] GirlLogic/ViewStory 少女 {girlId} 剧情 {storyId} 已读（ELevel={cur}），不重复发奖");
            return Task.CompletedTask;
        }

        if (story.IsMain && !GirlLoveTable.IsLinkGirl(girlId))
        {
            var mains = _love.StoriesOf(girlId).Where(kv => kv.Value.IsMain)
                .OrderBy(kv => kv.Value.Level).ThenBy(kv => kv.Key).ToList();
            int ordinal = mains.FindIndex(kv => kv.Key == storyId) + 1;
            if (ordinal <= 0 || GetVar(player, girlId, VarMainStoryId) < ordinal - 1)
            {
                Fail(session, CmdViewStory, ErrGeneric);
                return Task.CompletedTask;
            }
        }

        var granted = _ctx.Rewards.Grant(player, story?.Award, RewardSource.Extra);

        SetVar(session, player, girlId, ELevelOffset(storyId), ELevelRead);
        _ctx.Store.Save(player);

        GameNotify.PushRewards(session, player, granted);
        Ok(session, CmdViewStory, reply);

        Log.Info($"[session {session.Id}] GirlLogic/ViewStory 少女 {girlId} 剧情 {storyId}" +
                 $"（门槛 LV{story?.Level ?? 0}、{(story?.IsMain == true ? "主线" : "支线")}）" +
                 $"→ 置已读、发奖 {granted.Describe()}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 看联动剧情（`GirlLogic.lua:813`，上行 <c>{nId, Level}</c>）。
    /// 客户端 <c>OnViewLinkStory</c> 用 <c>nId</c>/<c>Level</c> 自己查 <c>LinkFetter</c> 拿奖励弹窗，
    /// 所以回包原样回显即可。现网无联动少女卡（<c>ItemList</c> 里 D 都 ≤ 32），
    /// 这条基本走不到，实现它只为「点得到时不卡遮罩」。
    /// </summary>
    private Task OnViewLinkStory(ClientSession session, Player player, JsonNode? param)
    {
        uint girlId = (uint)ReadInt(param?["nId"]);
        int level = ReadInt(param?["Level"]);

        var story = _love.LinkStoryOf(girlId, level);
        var girl = player.GetGirl(girlId);
        if (!GirlLoveTable.IsLinkGirl(girlId) || girl is null || story is null || girl.Level < level)
        {
            Fail(session, CmdViewLinkStory, ErrGeneric);
            return Task.CompletedTask;
        }
        int old = GetVar(player, girlId, VarMainStoryId);
        uint mask = 1u << story.Index;
        if (((uint)old & mask) != 0)
        {
            Fail(session, CmdViewLinkStory, ErrGeneric);
            return Task.CompletedTask;
        }
        var granted = _ctx.Rewards.Grant(player, story.Award, RewardSource.Extra);
        SetVar(session, player, girlId, VarMainStoryId, unchecked((int)((uint)old | mask)));
        _ctx.Store.Save(player);
        GameNotify.PushRewards(session, player, granted);
        Ok(session, CmdViewLinkStory, new JsonObject { ["nId"] = girlId, ["Level"] = level });
        return Task.CompletedTask;
    }

    // ================= LuaCall 173 / 174 =================

    /// <summary>
    /// 173 少女任务完成。★ 原服这里是个 bug：<c>LuaCall:CallClient(sCmd, tbRet)</c> 里
    /// <c>tbRet</c> **从未定义**（对比 174 有 <c>local tbRet</c>）⇒ 恒 nil ⇒ 回空表；
    /// 而客户端**没有 else 分支**（`docs/luacall-table.json` 里 <c>has_client_branch: false</c>）
    /// ⇒ 回包无人消费。本族无遮罩 ⇒ **不回包也安全**。
    ///
    /// 这里只记账（把当前少女事件标记为「进度达成」）+ 记一条日志，不回包。
    /// </summary>
    private Task OnMissionFinish(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int eventId = player.GetTask(MissionGroup, CurGirlEventTaskId);
        Log.Info($"[session {session.Id}] LuaCall 173 (GirlMissionFinish) 当前少女事件 id={eventId}" +
                 $" param={param?.ToJsonString()}（原服 tbRet 未定义 ⇒ 本实现不回包）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 174 少女任务领奖 —— **本域唯一真发奖、也唯一要认真写回包的命令**。
    ///
    /// 上行 <c>{nId}</c>，其中 <c>nId</c> = **少女事件 id**（任务值 <c>(5, 20001)</c> 的值），
    /// ★ **不是少女 id**（`GirlMissionManager.lua:272..275` + `MissionManager.lua:609`）。
    ///
    /// 回包两个必填字段（缺一个客户端就 return、界面无反应）：
    /// <list type="bullet">
    ///   <item><c>reward</c> = **分号分隔的字符串**（不是数组！）<c>"15-2-1-1-20;10-1-1-1-2"</c>，
    ///     每项 5 段 <c>G-D-P-L-N</c>（`GirlMissionManager.lua:278/283`）；</item>
    ///   <item><c>love</c> = <c>{girlID, nOldLevel, nOldExp, nNewLevel, nNewExp, nAdd}</c> 六个字段全必填
    ///     （`:296/:301` 位置数组 <c>UI_RewardLove</c>）。</item>
    /// </list>
    ///
    /// ★★★ 必须同时置 <c>(5, missionId)</c> 的 **bit0 = 已领**（E.3 ①），
    /// 否则客户端 <c>EventStep</c> 停在 1 ⇒ **可无限领奖**，且每次进咖啡馆还会再发一条 173。
    /// </summary>
    private Task OnMissionReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int missionId = ReadInt(param?["nId"]);
        if (missionId <= 0)
        {
            // ★ LuaCall 数字族：失败带 nError（非 0）。客户端无 nError 分支 ⇒ 表现为「点了没反应」
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            Log.Warn($"[session {session.Id}] LuaCall 174 (GirlMissionReward) nId 非法 param={param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        int packed = player.GetTask(MissionGroup, missionId);
        if ((packed & 1) != 0)
        {
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            Log.Warn($"[session {session.Id}] LuaCall 174 少女事件 {missionId} 已领过（任务值 {packed} bit0=1），拒绝");
            return Task.CompletedTask;
        }

        var mission = _ctx.Missions.GetGirlEvent(missionId);
        if (mission is null)
        {
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            Log.Warn($"[session {session.Id}] LuaCall 174 少女事件 {missionId} 不在配置表里，拒绝");
            return Task.CompletedTask;
        }

        // 发道具奖励
        var granted = _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra);

        // ★★ 加好感度给「今天这个少女」——她的 id 存在任务值 (5, 20000)
        // （`MissionManager.lua:7` <c>Girl_TaskId = 20000</c> / `:614` <c>GetCurGirlEventGirlId</c>），
        // **不在配置表里**（DailyMission.txt 的少女事件行没有少女 id 列）。
        // 没有值时退化到玩家的第一个少女，保证 love 表始终有个合法 girlID（客户端会用它查满级）。
        uint girlId = (uint)player.GetTask(MissionGroup, CurGirlTaskId);
        if (girlId == 0 || player.GetGirl(girlId) is null)
            girlId = player.Girls.Count > 0 ? player.Girls[0].Girlid : 0;
        AddExp(player, girlId, mission.AddGirlLove, out var delta);
        var unlockedStories = UnlockStories(player, girlId);

        // ★★ 置 bit0 = 已领（保留 bit1..31 的进度）
        player.SetTask(MissionGroup, missionId, packed | 1);
        _ctx.Store.Save(player);

        GameNotify.PushTaskValues(session, unlockedStories);
        GameNotify.PushRewards(session, player, granted);
        if (girlId != 0 && player.GetGirl(girlId) is { } girl)
            GameNotify.PushGirl(session, girl);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(MissionGroup, missionId), packed | 1),
        });

        // ★ reward 是**分号分隔字符串**，每项 G-D-P-L-N
        string reward = string.Join(';', mission.Rewards
            .Where(a => a.Length >= 5)
            .Select(a => $"{a[0]}-{a[1]}-{a[2]}-{a[3]}-{a[4]}"));

        var reply = new JsonObject
        {
            ["reward"] = reward,
            ["love"] = new JsonObject
            {
                ["girlID"] = girlId,
                ["nOldLevel"] = delta.OldLevel,
                ["nOldExp"] = delta.OldExp,
                ["nNewLevel"] = delta.NewLevel,
                ["nNewExp"] = delta.NewExp,
                ["nAdd"] = delta.Added,
            },
        };
        // ★ LuaCall 数字族：成功**绝不带** nError
        _ctx.LuaCalls.CallClient(session, cmd, reply);

        Log.Info($"[session {session.Id}] LuaCall 174 少女事件 {missionId} 领奖：{granted.Describe()}" +
                 $"、少女 {girlId} 好感 LV{delta.OldLevel}({delta.OldExp}) → LV{delta.NewLevel}({delta.NewExp})" +
                 $"（+{delta.Added}）、已置 bit0；reward=\"{reward}\"");
        return Task.CompletedTask;
    }

    // ================= LuaCall 175 / 255 / 256 =================

    /// <summary>
    /// 175 少女剧情爆炸选项：按 <c>MoodBoom.txt</c> 给若干少女改心情值。
    /// ★ 与 173 同型的 <c>tbRet</c> nil bug、客户端无 else 分支 ⇒ **不回包**。
    /// ★ 配置表只有 **1 行数据**（<c>PlotID=1 GirlID=7 MoodChangeValue=-100</c>）⇒ 价值极低，
    /// 实现它纯粹为了「收到不刷 unhandled + 心情值真的变」。
    /// </summary>
    private Task OnMoodBoom(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int boomId = ReadInt(param?["boomID"]);
        var rows = _love.MoodBoomOf(boomId);
        if (rows.Count == 0)
        {
            Log.Warn($"[session {session.Id}] LuaCall 175 (GirlMoodBoom) boomID={boomId} 不在 MoodBoom.txt 里（不回包）");
            return Task.CompletedTask;
        }

        var touched = new List<string>();
        foreach (var (girlId, change) in rows)
        {
            var girl = player.GetGirl(girlId);
            if (girl is null)
                continue;
            // 心情值域 0..100（`GirlInfo.moodvalue`），按配置增减后夹紧
            int v = Math.Clamp((int)girl.Moodvalue + change, 0, 100);
            girl.Moodvalue = (uint)v;
            GameNotify.PushGirl(session, girl);
            touched.Add($"少女{girlId} 心情→{v}({change:+#;-#;0})");
        }
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] LuaCall 175 (GirlMoodBoom) boomID={boomId}：" +
                 $"{(touched.Count > 0 ? string.Join('、', touched) : "无匹配少女")}（原服不回包，本实现同）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 255 清「新少女」红点：把 <c>GirlInfo.flag</c> 置 0 + 推 pf 1030。
    ///
    /// ★ 上行字段是**大写 <c>GirlId</c>**（`UI_Girl.lua:1089`），与 174 的 <c>nId</c>、
    /// 256 的 <c>nId</c> 命名**不一致** —— 写成 nId 会读到 0。
    /// ★ 客户端 else 分支是**空的**（只有一行被注释的 print）⇒ 回什么都行；
    /// 为对齐原服（`LuaCall.lua:3219` <c>CallClient(sCmd, tbParam)</c>）这里**原样回显**。
    /// ★★ 发送闸门：只有 <c>Get(nGirlId,'Flag') == 1</c> 才发，而 <c>GirlCommon:Get</c>
    /// 开头判 <c>IsInSelfCafe()</c> ⇒ **不在 map 6 时这条永远不会发出**。
    /// </summary>
    private Task OnUpdateFlag(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        uint girlId = (uint)ReadInt(param?["GirlId"]);
        var girl = girlId == 0 ? null : player.GetGirl(girlId);
        if (girl is null)
        {
            Log.Warn($"[session {session.Id}] LuaCall 255 (Girl_UpdateFlag) 少女 {girlId} 未持有 param={param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        uint before = girl.Flag;
        girl.Flag = 0;
        _ctx.Store.Save(player);
        GameNotify.PushGirl(session, girl);

        // ★ 原样回显（成功不带 nError）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["GirlId"] = girlId });
        Log.Info($"[session {session.Id}] LuaCall 255 (Girl_UpdateFlag) 少女 {girlId} 新标记 {before} → 0");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 256 记录大剧情观看进度：<c>SetTask(group, (girlId-1)*2000 + 14, nValue)</c> **原样存整数**。
    ///
    /// ★★ <c>nValue</c> 有**两种语义**（C.0）：普通少女是**顺序计数器**、联动少女是**位域**。
    /// 普通少女按配置顺序逐段推进并校验好感门槛；重播允许回退。联动位域保留原样处理。
    /// 原服转发本身不校验；本地增加与普通少女UI顺序一致的服务器保护。
    /// ★★★ 原服**不回包**（<c>MODULE_SERVER</c> 分支里连 <c>CallClient</c> 都没有）⇒ 本实现也不回。
    /// 但**必须推 pf 1026**，否则重登后客户端读回 0 ⇒ 所有剧情又变「未看」。
    /// </summary>
    private Task OnSetMainStoryId(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        uint girlId = (uint)ReadInt(param?["nId"]);
        int value = ReadInt(param?["nValue"]);
        if (girlId == 0)
        {
            Log.Warn($"[session {session.Id}] LuaCall 256 (Girl_SetMainStoryId) nId 非法 param={param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        int before = GetVar(player, girlId, VarMainStoryId);
        // Ordinary girls use an ordinal; link girls use a bit field.
        if (!GirlLoveTable.IsLinkGirl(girlId))
        {
            var mains = _love.StoriesOf(girlId).Where(kv => kv.Value.IsMain)
                .OrderBy(kv => kv.Value.Level).ThenBy(kv => kv.Key).ToList();
            if (player.GetGirl(girlId) is not { } girl || value < 0 || value > mains.Count ||
                (long)value > (long)before + 1) return Task.CompletedTask;
            if (value > before && (girl.Level < mains[value - 1].Value.Level ||
                !_love.IsFriendlinessUnlocked(girlId, mains[value - 1].Value.Level)))
                return Task.CompletedTask;
        }

        if (GirlLoveTable.IsLinkGirl(girlId))
        {
            // Link UI submits ViewLinkStory on completion. Its reward bits are server-owned.
            // A generic progress upload must neither create nor clear claimed rewards.
            if (player.GetGirl(girlId) is not null)
                GameNotify.PushTaskValues(session, new[]
                {
                    new KeyValuePair<uint, int>(Player.ComposeTaskId(VarGroup(girlId),
                        VarTaskId(girlId, VarMainStoryId)), before)
                });
            return Task.CompletedTask;
        }
        SetVar(session, player, girlId, VarMainStoryId, value);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] LuaCall 256 (Girl_SetMainStoryId) 少女 {girlId} " +
                 $"MainStoryId {before} → {value}（group {VarGroup(girlId)}，原服不回包）");
        return Task.CompletedTask;
    }

    // ================= 登录：ELevel 初值 =================

    /// <summary>
    /// 登录时把「够条件但还没解锁位」的剧情档位置成 <see cref="ELevelUnread"/>（= 1，已解锁未读）。
    ///
    /// ★★★ 这是本域**最容易漏、漏了整域没反应**的一步（E.2 步 d）：
    /// <c>ELevel</c> 的三态里 <b>0 表示「未解锁」</b>，而它只能由服务端置 1。
    /// 不置的后果：
    /// <list type="bullet">
    ///   <item>`UI_GirlLove.lua:142` 的 <c>nValue == 1</c> 恒假 ⇒ 剧情列表**没有 NEW 标记**；</item>
    ///   <item>`CartoonAutoPlay.lua:89` 的 <c>state == 1</c> 恒假 ⇒ **自动播放路径完全不触发**
    ///     ⇒ 连 <c>ViewStory</c> 上行都不会发 ⇒ 剧情奖励永远领不到。</item>
    /// </list>
    ///
    /// 判据（C.1，两个条件 AND）：
    /// ① 好感度等级 ≥ <c>Fetter.txt</c> 的 <c>Level</c>；
    /// ② <c>FriendlinessUnlock.txt</c> 里**有该 (girlId, level) 行**（没有 = 未开放，显灰锁）。
    ///
    /// ★ 只把 <c>0 → 1</c>，**不动已读（&gt;1）的**，所以幂等、重登不会把已读洗回未读。
    /// 在 <see cref="GameFeatureContext.OnPlayerPrepare"/> 里跑 ⇒ 变更随登录本来就要发的
    /// pf 1026 全量一起走，不额外发包。
    /// </summary>
    internal void SyncStoryUnlocks(ClientSession session, Player player)
    {
        var unlocked = UnlockStories(player);
        if (unlocked.Count > 0)
            Log.Info($"少女剧情：新解锁 {unlocked.Count} 段");
    }

    internal List<KeyValuePair<uint, int>> UnlockStories(Player player, uint? onlyGirl = null)
        => UnlockStories(_love, player, onlyGirl);

    internal static List<KeyValuePair<uint, int>> UnlockStories(GirlLoveTable love, Player player, uint? onlyGirl = null)
    {
        var changes = new List<KeyValuePair<uint, int>>();
        foreach (var girl in player.Girls.Where(g => onlyGirl is null || g.Girlid == onlyGirl))
        {
            uint girlId = girl.Girlid;
            changes.AddRange(UnlockSecrets(love, player, girlId));
            foreach (var (storyIndex, story) in love.StoriesOf(girlId))
            {
                if (girl.Level < story.Level)
                    continue;
                if (!StoryConditionMet(player, story))
                    continue;
                if (!love.IsFriendlinessUnlocked(girlId, story.Level))
                    continue;

                int off = ELevelOffset(storyIndex);
                if (off < 0)
                    continue;
                if (GetVar(player, girlId, off) != 0)
                    continue;       // 已解锁（1）或已读（>1）：不动

                player.SetTask(VarGroup(girlId), VarTaskId(girlId, off), ELevelUnread);
                changes.Add(new KeyValuePair<uint, int>(
                    Player.ComposeTaskId(VarGroup(girlId), VarTaskId(girlId, off)), ELevelUnread));
            }
        }

        return changes;
    }

    internal static List<KeyValuePair<uint, int>> UnlockSecrets(GirlLoveTable love, Player player,
        uint girlId, int[]? gifted = null)
    {
        var changes = new List<KeyValuePair<uint, int>>();
        var girl = player.GetGirl(girlId);
        if (girl is null) return changes;
        foreach (var secret in love.SecretsOf(girlId))
        {
            bool hasGift = secret.Gift.Length >= 4;
            bool eligible = hasGift ? gifted is not null && secret.Gift.Take(4).SequenceEqual(gifted)
                : secret.Level > 0 && girl.Level >= secret.Level;
            int offset = 100 + secret.Index - 1;
            if (!eligible || GetVar(player, girlId, offset) != 0) continue;
            int task = VarTaskId(girlId, offset), group = VarGroup(girlId);
            player.SetTask(group, task, 1);
            changes.Add(new(Player.ComposeTaskId(group, task), 1));
        }
        return changes;
    }

    // UI_GirlGift.lua: favorite gifts use floor(base * 1.25), or *2 for link girls.
    // Round each item before multiplying by the requested count.
    internal static long GiftExperiencePerItem(int baseExp, bool favorite, bool linkGirl)
        => baseExp <= 0 ? 0 : !favorite ? baseExp : linkGirl ? (long)baseExp * 2 : (long)baseExp * 5 / 4;

    internal static bool StoryConditionMet(Player player, GirlLoveTable.StoryAward story)
    {
        var cond = story.Condition;
        if (cond is null || cond.Length == 0) return true;
        if (cond[0] == -1) return false;
        if (cond[0] == 1 && cond.Length >= 4 && cond[1] > 0 && cond[2] > 0 && cond[3] > 0)
            return MapList.PassCount(player.GetLevelStar(MapList.MapId(cond[1], cond[2], cond[3]))) > 0;
        return false; // Unknown conditions must not grant rewards.
    }

    // ================= 工具 =================

    /// <summary>
    /// <c>ELevel&lt;index&gt;</c> 的任务值偏移 = <see cref="VarELevelBase"/> + index。
    /// 越界（index ≤ 0 或超出 <see cref="VarELevelMax"/>）返回 -1，调用方跳过。
    /// </summary>
    internal static int ELevelOffset(int storyIndex)
    {
        if (storyIndex <= 0)
            return -1;
        int off = VarELevelBase + storyIndex - 1;
        return off <= VarELevelMax ? off : -1;
    }

    /// <summary>
    /// 少女属性所在的任务值 group：普通少女 <b>3</b>、联动少女 <b>90</b>
    /// （`GirlCommon.lua:1495..1506` <c>GetExtGroup</c>：3→90 / 4→91 / 7→92）。
    /// </summary>
    internal static int VarGroup(uint girlId)
        => GirlLoveTable.IsLinkGirl(girlId) ? 90 : GmUnlock.GirlVarGroup;

    /// <summary>
    /// 少女属性的 taskId：<c>(girlId - 1) * 2000 + off</c>，
    /// ★ 联动少女先减 200（`GirlCommon.lua:1516..1518`）。
    /// 与 <see cref="GmUnlock.GirlTaskId"/> 的联动局部ID映射一致。
    /// </summary>
    internal static int VarTaskId(uint girlId, int off)
    {
        uint id = GirlLoveTable.IsLinkGirl(girlId) ? girlId - GirlLoveTable.LinkGirlId : girlId;
        return ((int)id - 1) * GmUnlock.GirlTaskStride + off;
    }

    internal static int GetVar(Player player, uint girlId, int off)
        => off < 0 ? 0 : player.GetTask(VarGroup(girlId), VarTaskId(girlId, off));

    /// <summary>写少女属性并**立刻推 pf 1026**（客户端要靠它读，且重登才不丢）。</summary>
    private static void SetVar(ClientSession session, Player player, uint girlId, int off, int value)
    {
        if (off < 0)
            return;
        int group = VarGroup(girlId);
        int taskId = VarTaskId(girlId, off);
        player.SetTask(group, taskId, value);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(group, taskId), value),
        });
    }

    /// <summary>
    /// 从背包扣道具（按 <c>CreateTime</c>/<c>Guid</c> 稳定顺序、跳过上锁的），
    /// 与 <see cref="CardFeature"/> 的取素材同构。返回实际扣到的个数。
    /// </summary>
    private static int TakeItems(
        Player player, int g, int d, int p, int l, int want,
        List<Item> changed, List<Item> removed)
    {
        int taken = 0;
        var candidates = player.Items
            .Where(i => i.Genre == (uint)g && i.Detail == (uint)d
                        && i.Particular == (uint)p && i.Level == (uint)l)
            .Where(i => i.LockOn == 0)
            .OrderBy(i => i.CreateTime)
            .ThenBy(i => i.Guid)
            .ToList();

        foreach (var item in candidates)
        {
            if (taken >= want)
                break;
            uint use = (uint)Math.Min((long)want - taken, item.Count);
            if (use == 0)
                continue;
            if (use >= item.Count)
            {
                player.Items.Remove(item);
                removed.Add(item);
            }
            else
            {
                item.Count -= use;
                if (!changed.Contains(item))
                    changed.Add(item);
            }
            taken += (int)use;
        }
        return taken;
    }

    /// <summary>
    /// 读 <c>tbItem</c> 这种「位置数组」上行（<c>[G,D,P,L]</c>）。
    /// ★ 必须容忍它不是数组（客户端版本差异 / 参数缺失），否则真机上会抛 ——
    /// AGENTS.md 记过「<c>JsonNode["key"]</c> 对 <c>JsonArray</c> 会抛」这条坑。
    /// </summary>
    private static (int G, int D, int P, int L)? ReadTuple(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count != 4)
            return null;
        int[] v = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (arr[i] is not JsonValue value || !value.TryGetValue<int>(out v[i]) || v[i] <= 0)
                return null;
        }
        return (v[0], v[1], v[2], v[3]);
    }

    /// <summary>成功回包（少女养成族）：★ **原样回 sCmd**、**绝不带 nError**。</summary>
    private void Ok(ClientSession session, string sCmd, JsonObject payload)
    {
        payload["sCmd"] = sCmd;
        _ctx.LuaCalls.CallClientFunction(session, Function, payload);
    }

    /// <summary>
    /// 失败回包（少女养成族）：★ 客户端**只会拿到 <c>nError</c> 这个数字**
    /// （`GirlLogic.lua:22` <c>GirlLogic[sCmd](GirlLogic, tbParam.nError)</c>），
    /// 所以语义必须编码在数值里。送礼只有 <see cref="ErrGiftMaxLevel"/>(4) 有提示。
    /// </summary>
    private void Fail(ClientSession session, string sCmd, int code)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["sCmd"] = sCmd,
            ["nError"] = code,
        });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;

    /// <summary>
    /// 一个礼物道具能加多少好感经验 = <c>ItemList.txt</c> 的 <b><c>ExtParam1</c></b>。
    ///
    /// ★ <see cref="Config.ItemTemplate.ExtParam1"/> 是 <b>string</b>（该列在别的 genre 上放的是非数字），
    /// 所以要显式 <c>TryParse</c>；解析不出来返回 0，调用方据此拒绝送礼（而不是白送 0 经验）。
    /// ★ 取证 B.4：genre 5 的 44 行 <c>ClassName</c> 全是 <c>gift</c>，
    /// <c>ExtParam1</c> 实测四档 30/60/150/300 随阶 L 递增（<c>SellingPrice</c> 是 900/2300，量级明显不同）。
    /// 该断言在客户端侧只有数值形态吻合、服务端侧无确证 ⇒ **推测（部分取证）**。
    /// </summary>
    private static int GiftExp(Config.ItemTemplate? tpl)
        => tpl is not null && int.TryParse(tpl.ExtParam1, out int v) && v > 0 ? v : 0;
}
