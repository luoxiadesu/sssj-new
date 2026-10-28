using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 任务 / 成就系统（主城「任务」面板 + 新手指引任务链）。
///
/// <para><b>0. 这一族命令的形状：上行 7 个名字，下行只有 1 个</b></para>
/// 这**不是** LuaCall 数字命令，而是 <c>S2CCall.lua</c> 那一族「字符串 sFunction」命令。
/// 上行统一走 <c>MissionManager:SendToServer</c>（MissionManager.lua:919..924）：
/// <code>
/// function MissionManager:SendToServer(strFunc, nId , nType)
///     Game.Blocking.Show();                                     -- 挂遮罩，等回包才 Hide
///     local tbParam = { nId = nId, nType = nType or 0;};
///     Game.Player.CallServer(strFunc, Json.Encode(tbParam));    -- pf 1022，sFunction = strFunc
/// end
/// </code>
/// 所以**上行 tbParam 只有 <c>nId</c> / <c>nType</c> 两个字段**，strFunc 共 7 个（全仓 grep 结果）：
/// <list type="table">
/// <item><term>AchievementGetAward</term><description>成就领奖 —— UI_Achievement.lua:550</description></item>
/// <item><term>MissionGetAward</term><description>日常任务领奖 —— UI_Achievement.lua:561（单领）/
///   UI_Achievement.lua:864 <c>('MissionGetAward', 0, 1)</c>（一键领取，nType=1）/
///   UI_AchieveReward.lua:12 / GirlMissionManager.lua:386（少女事件）</description></item>
/// <item><term>MissionActiveAward</term><description>活跃点数进度奖励 —— UI_Achievement.lua:559</description></item>
/// <item><term>GuideMissionGetAward</term><description>指引任务领奖 —— UI_Achievement.lua:555 / UI_Guidancetask.lua:367</description></item>
/// <item><term>GuideProgressGetAward</term><description>指引任务完成数进度奖励 —— UI_Achievement.lua:553 / UI_Guidancetask.lua:365</description></item>
/// <item><term>ClubMissionGetAward</term><description>社团任务领奖 —— UI_ClubTask.lua:297</description></item>
/// <item><term>ClubProgressGetAward</term><description>社团周任务值进度奖励 —— UI_ClubTask.lua:292</description></item>
/// </list>
/// 下行**只有一个名字** <c>MissionMgrMsg</c>（S2CCall.lua:85，与 PhoneMsg/ServerPhoneMsg 一样是
/// 上下行不同名的先例）：
/// <code>
/// function S2C.MissionMgrMsg(tbParam)
///     Game.Blocking.Hide()
///     MissionManager:Dispatcher(tbParam);
/// end
/// </code>
///
/// <para><b>1. <c>MissionManager:Dispatcher</c> 不是「子命令分发器」，是「领奖结果处理器」</b></para>
/// 它（MissionManager.lua:657..899）按 <c>tbParam.nMission</c> 分 7 路，含义写在
/// MissionManager.lua:656 的注释里：
/// <code>
/// nMission  0 成就 / 1 任务 / 2 任务活动奖励(活跃点数) / 3 指引任务
///           4 指引任务进度奖励 / 5 社团任务 / 6 社团任务进度奖励
/// </code>
/// 与上行 7 个函数名一一对应。**整个函数只解引用 4 个字段**（逐行核过）：
/// <list type="number">
/// <item><c>tbParam.nError</c> —— **必填**。MissionManager.lua:663 <c>if tbParam.nError &gt; 0 then</c>
///   直接和 0 比大小，nil 会抛「attempt to compare nil with number」。
///   &gt;0 时走 <c>ShowError</c>（:902）弹 <c>ui.Error.&lt;nError&gt;</c> 并 return。</item>
/// <item><c>tbParam.nMission</c> —— **必填**，7 路分发依据（:667/:675/:722/:733/:851/:863 …）。</item>
/// <item><c>tbParam.tbIdList</c> —— 仅 nMission==1 用，**nil 安全且 nil 有语义**：
///   MissionManager.lua:734..740 <c>if not tbList then tbList={}; insert(ui.nCurId) else ui.HaveGetMission=false end</c>
///   —— 给空数组 ≠ 不给！空数组会让客户端认为「一键领取返回 0 条」，
///   奖励弹窗空白且清掉一键按钮。所以**单领时必须省略这个字段**。</item>
/// <item><c>tbParam.tbItems</c> —— 仅 nMission==2 用，nil 安全：
///   MissionManager.lua:785..786 <c>if tbItems and next(tbItems) then</c>。</item>
/// </list>
/// 除此之外 <c>Dispatcher</c> 全部数据来自**客户端自己的配置表 + 本地任务值**，
/// 奖励弹窗内容是它自己从 <c>tbReward</c> 拼的（:746..783），服务端不需要下发奖励内容。
///
/// <para><b>2. 「任务列表」根本没有上行请求 —— 面板是纯本地渲染</b></para>
/// <c>UI_Achievement.lua</c>（866 行）里**没有任何 <c>CallServer</c>/<c>SendCmd</c>/<c>LuaCall:CallServer</c>**，
/// 唯一的上行就是上面 7 个领奖命令（<c>grep -n "CallServer\|SendCmd\|LuaCall\." UI_Achievement.lua</c> 无命中）。
/// 列表由 <c>MissionManager:GetMissionConfig()</c>（配置表）+ <c>me:GetTaskValue(group,id)</c>（任务值）拼出：
/// <code>
/// MissionManager:GetTaskPro(g,id)   = LoadBits(GetTaskValue(g,id), 1, 31)   -- 进度 = 值 &gt;&gt; 1   (:595)
/// MissionManager:GetTaskAward(g,id) = LoadBits(GetTaskValue(g,id), 0, 0)    -- 已领标记 = bit0   (:602)
/// CheckInfoState: 进度 &gt;= missionMax[nKey] → 1 可领；再有 bit0 → 2 已领                    (:944..949)
/// </code>
/// 任务值的下行通道是 **pf 1026 <c>TaskValueSyncRsp</c>**（<c>Game.Player.OnTaskValueSyncRsp</c>，
/// RVA 0x1852B18 —— 反汇编确认它是 <b>逐条 merge</b>：<c>ContainsKey</c> → <c>set_Item</c> / <c>Add</c>，
/// **不清表**，所以可以只推增量）。键是 <c>(group &lt;&lt; 16) | taskId</c>
/// （<c>Game.Player.GetTask</c> RVA 0x18448F0 的 <c>orr w19, w19, w20, lsl #16</c>），
/// 正是 <see cref="Player.ComposeTaskId"/>。
///
/// 所以「最小可用闭环」在本项目里等价于：
/// <list type="number">
/// <item>面板打开不需要服务端配合 —— 新号全部任务值缺省为 0，
///   <c>GetTaskValue</c> 找不到键返回 0（<c>Game.Player.GetTask</c> 的 ContainsKey 分支），
///   于是「进度 0 / 不可领 / 不报 nil」，这就是任务书里那份「按配置表生成、进度为 0 的合法列表」。</item>
/// <item>服务端要做的是把**进度与已领标记**维护正确并推 pf 1026，再把 7 个领奖命令答完
///   （否则 <c>Game.Blocking</c> 遮罩要等 2 秒超时才消失，且面板不刷新）。</item>
/// </list>
///
/// <para><b>3. 服务端进度从哪来</b></para>
/// 目前只有一处**有取证**的服务端可算进度：<c>GuideMission.txt</c> 的 <c>targetChapter</c> 列
/// （表头第 2 行写着「完成条件(通关关卡)」，如 40001 = <c>[1-1-1]</c>）。
/// 服务端有 <see cref="Player.Levels"/>（通关位域），于是
/// <c>进度 = MapList.PassCount(GetLevelStar(mapId))</c>，与 <c>missionMax=[1]</c> 比即可。
/// 日常任务（战斗 5 次、消耗体力 100 …）与成就的计数器需要战斗/咖啡/商店等各系统埋点，
/// 本实现**不猜**：这些任务的进度一律沿用已存的任务值（客户端 pf 1027 写入或将来别的功能写入）。
///
/// <para><b>4. 玩家侧存档</b></para>
/// 权威记录放 <c>Player.Extra["mission"]</c>（随存档持久化），结构见 <see cref="MissionRecord"/>；
/// <see cref="Player.Tasks"/> 只是它 + 派生进度投影出来的「客户端可见视图」，
/// 每次变更后用 pf 1026 推增量。这样即使别的功能也写同一个任务值，也只会被「或」上已领 bit，
/// 不会互相清掉进度位。
///
/// <para><b>5. 已知未实现 / 需继续取证（详见各处 TODO 注释）</b></para>
/// <list type="bullet">
/// <item><b>已发实物奖励</b>（本轮补上）：判定通过后走 <c>ctx.Rewards.Grant</c> 把
///   <c>(G,D,P,L,N)</c> 折算成货币/背包实例，再用 <see cref="GameNotify.PushRewards"/>
///   推 pf 1032/1031。领奖弹窗的**内容**仍由客户端自己按 <c>tbReward</c> 拼
///   （MissionManager.lua:746..783），服务端不下发奖励清单。</item>
/// <item><b>已有登录钩子</b>（本轮补上）：<c>ctx.OnPlayerLogin</c> 在登录四条基础包之后
///   重算派生进度并推 pf 1026，不再依赖「收到本族上行才刷新」。</item>
/// <item>错误码 <c>nError</c> 的具体数值无客户端取证（见 <see cref="ErrNotExist"/> 注释）。</item>
/// <item>日常任务与成就的**计数器**仍未埋点（战斗次数、消耗体力、强化次数…）：
///   进度沿用已存任务值（客户端 pf 1027 写入或将来别的功能写入），服务端不猜。</item>
/// </list>
/// </summary>
public sealed class MissionFeature : IGameFeature, IGamePulseFeature
{
    // ---------------- 上行 sFunction（客户端 → 服务端）----------------

    /// <summary>成就领奖（UI_Achievement.lua:550）→ nMission 0。</summary>
    public const string FnAchievementGetAward = "AchievementGetAward";

    /// <summary>日常任务领奖（UI_Achievement.lua:561 单领 / :864 一键 nType=1）→ nMission 1。</summary>
    public const string FnMissionGetAward = "MissionGetAward";

    /// <summary>活跃点数进度奖励（UI_Achievement.lua:559）→ nMission 2。</summary>
    public const string FnMissionActiveAward = "MissionActiveAward";

    /// <summary>指引任务领奖（UI_Guidancetask.lua:367 / UI_Achievement.lua:555）→ nMission 3。</summary>
    public const string FnGuideMissionGetAward = "GuideMissionGetAward";

    /// <summary>指引任务完成数进度奖励（UI_Guidancetask.lua:365 / UI_Achievement.lua:553）→ nMission 4。</summary>
    public const string FnGuideProgressGetAward = "GuideProgressGetAward";

    /// <summary>社团任务领奖（UI_ClubTask.lua:297）→ nMission 5。</summary>
    public const string FnClubMissionGetAward = "ClubMissionGetAward";

    /// <summary>社团任务进度奖励（UI_ClubTask.lua:292）→ nMission 6。</summary>
    public const string FnClubProgressGetAward = "ClubProgressGetAward";

    /// <summary>全部上行名（注册用；顺序即注册顺序）。</summary>
    public static readonly string[] UpFunctions =
    {
        FnAchievementGetAward, FnMissionGetAward, FnMissionActiveAward,
        FnGuideMissionGetAward, FnGuideProgressGetAward,
        FnClubMissionGetAward, FnClubProgressGetAward,
    };

    // ---------------- 下行 sFunction（服务端 → 客户端）----------------

    /// <summary>
    /// 唯一的下行名：<c>S2C.MissionMgrMsg</c>（S2CCall.lua:85）。
    /// ★ 必须是这个名字：全仓 805 个 .lua 里没有 <c>S2C.AchievementGetAward</c> 等同名函数（逐个 grep 均 0 命中）
    /// （<c>grep -rn "MissionMgrMsg\|SendCmd(\"Mission" extracted/lua</c> 只命中 S2CCall.lua:85），
    /// 若按上行名回，客户端 <c>S2C.ProcCmd</c> 只打一行
    /// <c>S2C.ProcCmd error &lt;名字&gt;</c> 就静默丢包（S2CCall.lua:11..12），连遮罩都不会消。
    /// </summary>
    public const string DownFunction = "MissionMgrMsg";

    // ---------------- nMission 取值（MissionManager.lua:656 的注释）----------------

    /// <summary>0 成就。</summary>
    public const int MissionAchieve = 0;

    /// <summary>1 日常任务。</summary>
    public const int MissionDaily = 1;

    /// <summary>2 任务活动奖励（活跃点数进度奖励）。</summary>
    public const int MissionActivePoint = 2;

    /// <summary>3 指引任务。</summary>
    public const int MissionGuide = 3;

    /// <summary>4 指引任务进度奖励。</summary>
    public const int MissionGuideProgress = 4;

    /// <summary>5 社团任务。</summary>
    public const int MissionClub = 5;

    /// <summary>6 社团任务进度奖励。</summary>
    public const int MissionClubProgress = 6;

    // ---------------- LuaCall 200 ----------------

    /// <summary>
    /// LuaCall 200 <c>MissionNotify</c>「成就和任务通知」（LuaCall.lua:172 定义、:2828 注册）。
    /// **只有 <c>MODULE_CLIENT</c> 分支，没有 <c>MODULE_SERVER</c> 分支** —— 即纯服务端推送：
    /// <code>
    /// -- 任务和成就通知 {Type = 0, Id = achieve.id,}  Type 0日常任务 1成就 Id 配置表id
    /// RegisterCMD(LuaCall.MissionNotify, function(sCmd, tbParam)
    ///     if MODULE_CLIENT then
    ///         RemindManager:NewMissionRemind(tbParam)
    ///         RemindManager:ShowRemind()
    ///     end
    /// end);
    /// </code>
    /// </summary>
    public const int MissionNotifyCmd = 200;

    /// <summary>MissionNotify.Type = 0 → 日常任务（读 <c>MissionManager:GetMission(Id)</c>，RemindManager.lua:61..69）。</summary>
    public const int NotifyTypeMission = 0;

    /// <summary>MissionNotify.Type = 1 → 成就（读 <c>MissionManager:GetNormalAchieve(Id)</c>，RemindManager.lua:70..78）。</summary>
    public const int NotifyTypeAchieve = 1;

    // ---------------- 任务值里的固定 taskId（MissionManager.lua:7..13）----------------

    /// <summary>每日随机 girl id（<c>MissionManager.Girl_TaskId = 20000</c>）。</summary>
    public const int GirlTaskId = 20000;

    /// <summary>每日随机少女事件 id（<c>MissionManager.GirlEvent_TaskId = 20001</c>）。</summary>
    public const int GirlEventTaskId = 20001;

    /// <summary>
    /// 活跃点数（<c>MissionManager.ActivePoint_TaskId = 30000</c>）。
    /// ★ 这一格存的是**裸值**，不是 (progress&lt;&lt;1|claimed) 位域 ——
    /// <c>GetMissionPoint()</c> 直接 <c>return me:GetTaskValue(...)</c>（MissionManager.lua:619..621），
    /// 没有过 <c>LoadBits</c>。
    /// </summary>
    public const int ActivePointTaskId = 30000;

    /// <summary>
    /// 活跃点数奖励标记（<c>MissionManager.PointAward_TaskId = 30001</c>）。
    /// 位标记：档位 i 的已领标记 = <c>LoadBits(nFlag, i, i)</c>（MissionManager.lua:625..627），
    /// i 从 **1** 起（UI_Achievement.lua:715 <c>for i=1,5</c>），所以 bit0 空着不用。
    /// </summary>
    public const int PointAwardTaskId = 30001;

    /// <summary>指引任务完成数奖励标记（<c>MissionManager.GuideCompleteAward_TaskId = 41001</c>，位标记，bit 1..5）。</summary>
    public const int GuideCompleteAwardTaskId = 41001;

    /// <summary>社团任务完成数奖励标记（<c>MissionManager.ClubCompleteAward_TaskId = 51001</c>，位标记，bit 1..5）。</summary>
    public const int ClubCompleteAwardTaskId = 51001;

    // ---------------- 错误码 ----------------

    /// <summary>
    /// 配置里没有这个 id。
    ///
    /// ★★ <b>以下 4 个错误码没有客户端取证</b>：<c>MissionManager:ShowError</c>（MissionManager.lua:902..916）
    /// 只是 <c>string.format(Game.Localization.Get('ui.Error.'..nCode), '任务'|'成就')</c>，
    /// 而 <c>ui.Error.*</c> 的码表在 bundle 里的本地化文件中，extracted 里拿不到
    /// （<c>grep -rn "ui\.Error\.[0-9]" extracted/lua</c> 只见 20012/20014/20015/20056/20062/29999 这些**别的系统**的码）。
    /// 之所以敢用「自定义正数」：<c>Game.Localization.Get</c>（RVA 0x1C84E38）在
    /// <c>SimpleDictionary.TryGetValue</c> 失败时返回**空串**（0x1C84F88 分支 → STR ""），
    /// 不是 nil，所以最坏情况只是弹一个空白 MessageBox，不会抛异常、不会卡死。
    /// 真实码值需要后续从本地化 bundle 里取证。
    /// </summary>
    public const int ErrNotExist = 1;

    /// <summary>进度不足（未完成）。同上，数值无取证。</summary>
    public const int ErrNotFinished = 2;

    /// <summary>已经领过了。同上，数值无取证。</summary>
    public const int ErrAlreadyGot = 3;

    /// <summary>服务端没有判定该条件所需的数据（当前只有社团周任务值）。同上，数值无取证。</summary>
    public const int ErrNoServerData = 4;

    // ---------------- Player.Extra 键 ----------------

    /// <summary><see cref="Player.Extra"/> 里本功能的键。</summary>
    public const string ExtraKey = "mission";

    private GameFeatureContext _ctx = null!;
    private MissionTable _table = MissionTable.Empty;

    public string Name => "任务/成就(sFunction MissionMgrMsg)";

    /// <summary>本功能加载到的配置表（测试与日志用）。</summary>
    internal MissionTable Table => _table;

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        // 八张表由 GameModule 统一加载一次（ctx.Missions），本功能与 GmUnlock 共用同一份实例 ——
        // 原先这里自己 Load 一遍，AchievementOther.txt 的 1857 行会被读两次。
        _table = ctx.Missions;

        ctx.MapFunction(FnAchievementGetAward, (s, f, p) => Handle(s, f, p, MissionAchieve));
        ctx.MapFunction(FnMissionGetAward, (s, f, p) => Handle(s, f, p, MissionDaily));
        ctx.MapFunction(FnMissionActiveAward, (s, f, p) => Handle(s, f, p, MissionActivePoint));
        ctx.MapFunction(FnGuideMissionGetAward, (s, f, p) => Handle(s, f, p, MissionGuide));
        ctx.MapFunction(FnGuideProgressGetAward, (s, f, p) => Handle(s, f, p, MissionGuideProgress));
        ctx.MapFunction(FnClubMissionGetAward, (s, f, p) => Handle(s, f, p, MissionClub));
        ctx.MapFunction(FnClubProgressGetAward, (s, f, p) => Handle(s, f, p, MissionClubProgress));

        // LuaCall 200 只有 MODULE_CLIENT 分支（LuaCall.lua:2828..2833）= 纯下行推送。
        // 这里注册它只为两件事：①登记进「已实现」集合，客户端万一回声不会刷 unhandled 告警；
        // ②把「它是下行、上行不存在」这条结论固定在代码里。
        ctx.MapLuaCall(MissionNotifyCmd, (session, cmd, param) =>
        {
            Log.Warn($"[session {session.Id}] LuaCall {cmd} MissionNotify 是纯服务端推送命令" +
                     $"（LuaCall.lua:2828 只有 MODULE_CLIENT 分支），收到上行属异常，忽略：{param?.ToJsonString()}");
            return Task.CompletedTask;
        });

        // 登录钩子：把指引通关、角色补给累计数等可派生进度算好并推 pf 1026。
        // 没有它的话，玩家「通关 → 重登 → 打开指引任务面板」会看到进度 0，
        // 直到本族任意一条上行命令触发 SyncAndPush 才补上。
        ctx.OnPlayerLogin((session, player) =>
        {
            var changed = SyncTaskValues(player);
            if (changed.Count == 0)
                return;
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session,
                changed.Select(c => new KeyValuePair<uint, int>(c.Id, c.Value)));
            Log.Info($"[session {session.Id}] 登录时补齐任务派生进度 {changed.Count} 条");
        });
    }

    // =================================================================
    //  上行分发
    // =================================================================

    /// <summary>
    /// 7 个上行命令的公共入口。<paramref name="nMission"/> 就是回包里要带的那一路编号
    /// —— 上行名与 nMission 的对应关系见类注释（MissionManager.lua:656）。
    /// </summary>
    private Task Handle(ClientSession session, string function, JsonNode? param, int nMission)
    {
        if (session.Tag is Player player)
        {
            lock (player) return HandleLocked(session, function, param, nMission);
        }
        return Task.CompletedTask;
    }

    void IGamePulseFeature.OnPlayerPulse(ClientSession session, Player player)
    {
        lock (player) SyncAndPush(session, player);
    }

    private Task HandleLocked(ClientSession session, string function, JsonNode? param, int nMission)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] {function} 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        // 上行只有 nId / nType 两个字段（MissionManager.lua:922）
        int nId = ReadInt(param?["nId"], 0);
        int nType = ReadInt(param?["nType"], 0);

        // 先把「服务端能自己算的进度」刷新一遍（指引通关、角色补给累计数），
        // 再做判定 —— 否则玩家刚通关就领奖会被判「进度不足」。
        SyncAndPush(session, player);

        var record = ReadRecord(player);
        int error;
        List<int>? idList = null;
        // 本次领取命中的配置奖励元组（(G,D,P,L,N)），由各 Claim* 方法累积；
        // 判定通过后统一交给 ctx.Rewards 真正发放（见下方注释）。
        var awards = new List<int[]>();

        switch (nMission)
        {
            case MissionAchieve:
                error = ClaimSimple(player, record, "achieve", _table.GetAchieve(nId), nId, achieveKey: true, awards);
                break;

            case MissionDaily when nType == 1:
                // 一键领取（UI_Achievement.lua:863..866 `SendToServer('MissionGetAward', 0, 1)`）
                idList = ClaimAllDaily(player, record, awards);
                error = 0;
                break;

            case MissionDaily:
                error = ClaimSimple(player, record, "mission", _table.GetDaily(nId), nId, achieveKey: false, awards);
                break;

            case MissionActivePoint:
                error = ClaimPointAward(player, record, "activePoint", _table.GetActivePointAward(nId), nId,
                                        PointAwardTaskId, CurrentActivePoint(player), awards);
                break;

            case MissionGuide:
                error = ClaimSimple(player, record, "guide", _table.GetGuide(nId), nId, achieveKey: false, awards);
                break;

            case MissionGuideProgress:
                error = ClaimPointAward(player, record, "guideProgress", _table.GetGuideProgressAward(nId), nId,
                                        GuideCompleteAwardTaskId, GuideCompleteCount(player), awards);
                break;

            case MissionClub:
                error = ClaimSimple(player, record, "club", _table.GetClub(nId), nId, achieveKey: false, awards);
                break;

            case MissionClubProgress:
                // 判定值是 ClubCommon.MyClubInfo.primary.weektaskvalue（MissionManager.lua:968），
                // 服务端没有社团模块、也没有这个字段，宁可拒绝也不错发。
                Log.Warn($"[session {session.Id}] {function} 无法判定：社团周任务值(weektaskvalue) " +
                         $"服务端未建模（MissionManager.lua:968），拒绝 nId={nId}");
                error = ErrNoServerData;
                break;

            default:
                error = ErrNotExist;
                break;
        }

        if (error == 0)
        {
            // ★ 真正发实物奖励。
            // 之所以能放心发：领奖弹窗的**内容**是客户端自己按配置表 tbReward 拼的
            // （MissionManager.lua:746..783），服务端不下发奖励清单；
            // 但货币/背包必须由服务端真的变，并走 pf 1032/1031 推下去，
            // 否则玩家看到弹窗却什么也没拿到。
            // 顺序：先记账存盘 → pf 1026（已领标记，客户端面板据此变灰）→ pf 1032/1031（数值到位）。
            var granted = _ctx.Rewards.Grant(player, awards, RewardSource.Extra);

            WriteRecord(player, record);
            _ctx.Store.Save(player);
            SyncAndPush(session, player);   // 把新的已领标记推给客户端（pf 1026）
            GameNotify.PushRewards(session, player, granted);

            if (!granted.IsEmpty)
                Log.Info($"[session {session.Id}] 领奖发放 {granted.Describe()}");
            else if (awards.Count > 0)
                Log.Warn($"[session {session.Id}] 领奖命中 {awards.Count} 条配置奖励但一件也没发出" +
                         "（元组非法或概率未中？见 [reward] 日志）");
        }

        Log.Info($"[session {session.Id}] {function} nId={nId} nType={nType} " +
                 $"→ MissionMgrMsg nMission={nMission} nError={error}" +
                 (idList is null ? "" : $" tbIdList=[{string.Join(",", idList)}]"));

        _ctx.LuaCalls.CallClientFunction(session, DownFunction, BuildReply(nMission, error, idList));
        return Task.CompletedTask;
    }

    // =================================================================
    //  领奖：单条任务 / 成就 / 指引 / 社团任务
    // =================================================================

    /// <summary>
    /// 「一条有 missionMax 的任务/成就」的通用领奖判定：进度够 + 没领过 → 置 bit0。
    /// 判定完全对齐 <c>MissionManager:CheckInfoState</c>（MissionManager.lua:927..954）：
    /// 成就用 <c>assistparam[1]</c> 选 missionMax 的第几项，其余固定第 1 项。
    /// 判定通过时把该条的配置奖励（<see cref="MissionEntry.Rewards"/>）追加到
    /// <paramref name="awards"/>，由调用方统一发放。
    /// </summary>
    private int ClaimSimple(Player player, MissionRecord record, string kind,
                            MissionEntry? entry, int id, bool achieveKey, List<int[]> awards)
    {
        if (entry is null || id <= 0)
            return ErrNotExist;

        if (IsClaimed(player, record, kind, entry))
            return ErrAlreadyGot;

        int required = entry.RequiredCount(achieveKey);
        int progress = CurrentProgress(player, entry);
        if (progress < required)
            return ErrNotFinished;

        record.Claimed(kind).Add(id);
        awards.AddRange(entry.Rewards);
        return 0;
    }

    /// <summary>
    /// 一键领取全部可领的日常任务（nType==1）。
    ///
    /// 只收 <c>missionType != 2</c> 的任务：一键按钮的可见性与计数在
    /// UI_Achievement.lua:257 <c>if tbInfo.type ~= 2 then</c> 之内，少女事件（type==2）
    /// 走的是另一条展示路径（<c>Dispatcher</c> :763..765 会去查当前少女事件 id）。
    ///
    /// 返回值就是回包的 <c>tbIdList</c>。**里面每个 id 都必须能被
    /// <c>MissionManager:GetMission(v)</c> 查到**，否则客户端 <c>if not tbGetConfig then return end</c>
    /// （MissionManager.lua:744）会中途 return，奖励弹窗与面板刷新都没了 ——
    /// 这里的 id 全部来自同一份 DailyMission.txt，天然满足。
    /// </summary>
    private List<int> ClaimAllDaily(Player player, MissionRecord record, List<int[]> awards)
    {
        var claimed = new List<int>();
        foreach (var entry in _table.Daily.Values.OrderBy(e => e.Id))
        {
            if (entry.Type == 2)
                continue;
            if (IsClaimed(player, record, "mission", entry))
                continue;
            if (CurrentProgress(player, entry) < entry.RequiredCount(achieveKey: false))
                continue;

            record.Claimed("mission").Add(entry.Id);
            claimed.Add(entry.Id);
            awards.AddRange(entry.Rewards);
        }
        return claimed;
    }

    /// <summary>
    /// 「进度奖励」（活跃点数 / 指引完成数 / 社团周任务值）的领奖判定。
    /// 条件与客户端红点判定一致：<c>当前值 &gt;= 档位 Point</c> 且 <c>该档 bit == 0</c>
    /// （活跃点数 MissionManager.lua:1030..1037；指引 MissionManager.lua:988..994）。
    /// 判定通过时把档位奖励（<see cref="MissionPointAward.Items"/>）追加到 <paramref name="awards"/>。
    /// </summary>
    private int ClaimPointAward(Player player, MissionRecord record, string kind,
                                MissionPointAward? award, int id, int flagTaskId, int currentValue,
                                List<int[]> awards)
    {
        if (award is null || id <= 0)
            return ErrNotExist;

        // 位标记 bit 下标就是档位 id（LoadBits(nFlag, i, i)，i 从 1 起）
        int flags = player.GetTask(MissionTable.MissionGroup, flagTaskId);
        bool already = record.Flags(kind).Contains(id) || (flags & (1 << id)) != 0;
        if (already)
            return ErrAlreadyGot;

        if (currentValue < award.Point)
            return ErrNotFinished;

        record.Flags(kind).Add(id);
        awards.AddRange(award.Items);
        return 0;
    }

    // =================================================================
    //  进度：读 / 派生
    // =================================================================

    /// <summary>
    /// 某条任务的当前进度。基线是已存任务值的 bit1..31
    /// （<c>GetTaskPro = LoadBits(v,1,31)</c>，MissionManager.lua:595..599）；
    /// 指引任务若配了 <c>targetChapter</c>，再取「该关通关次数」与基线的较大值。
    /// </summary>
    internal int CurrentProgress(Player player, MissionEntry entry)
    {
        int stored = ProgressOf(player.GetTask(entry.Group, entry.Id));
        int derived = DerivedProgress(player, entry);
        return derived > stored ? derived : stored;
    }

    /// <summary>
    /// 服务端能自己算出来的进度：角色补给累计数，以及 <c>GuideMission.txt</c> 的
    /// <c>targetChapter</c>「完成条件(通关关卡)」列（形如 <c>[1-1-1]</c>）→
    /// 用 <see cref="Player.Levels"/> 的通关次数（<c>MapList.PassCount</c>）当进度。
    /// 其余任务（战斗次数、消耗体力、强化…）需要各系统埋点，没有取证，返回 0。
    /// </summary>
    internal static int DerivedProgress(Player player, MissionEntry entry)
    {
        // AchieveType 1: player level; 135: specified girl's affection level.
        // These are current-state conditions used by CardRefit, not historical counters.
        if (entry.Group == MissionTable.OtherGroup)
        {
            // AchieveType 69: [card count, enhance level]; 172: [girl, enhance level, count].
            // Count owned quantities (including locked cards), never rarity Level.
            if (entry.AchieveType is 69 or 172 && entry.Counts.Count >= 2 && entry.Counts[1] > 0)
            {
                if (entry.AchieveType == 172 && (entry.Counts.Count < 3 || entry.Counts[0] <= 0)) return 0;
                long count = 0;
                foreach (var card in player.Items)
                    if (card.Genre == 1 && card.Count > 0 && card.EnhanceLevel >= (uint)entry.Counts[1] &&
                        (entry.AchieveType == 69 || card.Detail == (uint)entry.Counts[0]))
                        count = Math.Min(int.MaxValue, count + card.Count);
                return (int)count;
            }
            if (entry.AchieveType == 1) return Math.Max(0, player.Level);
            if (entry.AchieveType == 135 && entry.Counts.Count >= 2 && entry.Counts[1] > 0)
                return (int)Math.Min(int.MaxValue, player.GetGirl((uint)entry.Counts[1])?.Level ?? 0u);
        }
        // AchieveType.txt 60: 在补给中招募角色卡累计数量；普通与已确认新人补给共用此总数。
        if (entry.Group == MissionTable.NormalGroup && entry.AchieveType == 60)
            return Math.Max(0, player.GetTask(GachaFeature.TaskGroup, GachaFeature.TaskTotal));
        if (entry.TargetChapter.Count < 3)
            return 0;

        uint mapId = MapList.MapId(entry.TargetChapter[0], entry.TargetChapter[1], entry.TargetChapter[2]);
        return MapList.PassCount(player.GetLevelStar(mapId));
    }

    /// <summary>
    /// 当前活跃点数 = 任务值 (5, 30000) 的**裸值**（MissionManager.lua:619..621，没过 LoadBits）。
    /// </summary>
    internal static int CurrentActivePoint(Player player)
        => player.GetTask(MissionTable.MissionGroup, ActivePointTaskId);

    /// <summary>
    /// 已完成的指引任务数 = <c>MissionManager:GetGuideMissionCompleteNum</c>
    /// （MissionManager.lua:631..641）：对每条指引任务跑 <c>CheckInfoState</c>，
    /// 结果 1（可领）或 2（已领）都算「完成」，即「进度 &gt;= missionMax」。
    /// </summary>
    internal int GuideCompleteCount(Player player)
    {
        int n = 0;
        foreach (var entry in _table.Guide.Values)
        {
            if (CurrentProgress(player, entry) >= entry.RequiredCount(achieveKey: false))
                n++;
        }
        return n;
    }

    // =================================================================
    //  任务值投影 + pf 1026 推送
    // =================================================================

    /// <summary>
    /// 把 <c>Extra["mission"]</c> 的记账 + 派生进度投影成客户端可见的任务值，返回**有变化**的条目。
    ///
    /// 三类键：
    /// <list type="number">
    /// <item>任务/成就/指引/社团的每条 id → <c>(进度 &lt;&lt; 1) | 已领bit</c>
    ///   （MissionManager.lua:595/:602 的 LoadBits 拆法反过来）。
    ///   进度取 max(已存进度, 派生进度)，**只升不降**，避免把别的功能写进来的计数清掉。</item>
    /// <item>三个位标记键 30001 / 41001 / 51001 → 把已领档位的 bit 或上去。</item>
    /// <item>活跃点数 30000 不动（裸值，由别的系统累加）。</item>
    /// </list>
    /// 纯函数（只写 <see cref="Player.Tasks"/>，不碰网络），方便单测。
    /// </summary>
    internal List<(uint Id, int Value)> SyncTaskValues(Player player)
    {
        var record = ReadRecord(player);
        var changed = new List<(uint, int)>();

        void Apply(int group, int taskId, int value)
        {
            uint key = Player.ComposeTaskId(group, taskId);
            int old = player.GetTask(group, taskId);
            if (old == value)
                return;
            player.SetTask(group, taskId, value);
            changed.Add((key, value));
        }

        foreach (var (kind, entries) in AllEntryBuckets())
        {
            var claimedSet = record.Claimed(kind);
            foreach (var entry in entries.Values.OrderBy(e => e.Id))
            {
                int stored = player.GetTask(entry.Group, entry.Id);
                int progress = ProgressOf(stored);
                int derived = DerivedProgress(player, entry);
                if (derived > progress)
                    progress = derived;

                int claimedBit = claimedSet.Contains(entry.Id) || ClaimedOf(stored) != 0 ? 1 : 0;
                Apply(entry.Group, entry.Id, ComposeTaskValue(progress, claimedBit));
            }
        }

        ApplyFlags(record, "activePoint", PointAwardTaskId);
        ApplyFlags(record, "guideProgress", GuideCompleteAwardTaskId);
        ApplyFlags(record, "clubProgress", ClubCompleteAwardTaskId);

        return changed;

        void ApplyFlags(MissionRecord rec, string kind, int taskId)
        {
            int value = player.GetTask(MissionTable.MissionGroup, taskId);
            foreach (int idx in rec.Flags(kind))
            {
                if (idx is >= 0 and < 31)
                    value |= 1 << idx;
            }
            Apply(MissionTable.MissionGroup, taskId, value);
        }
    }

    /// <summary>重算任务值并把增量用 pf 1026 <c>TaskValueSyncRsp</c> 推给客户端（无变化则不发包）。</summary>
    private void SyncAndPush(ClientSession session, Player player)
    {
        var changed = SyncTaskValues(player);
        if (changed.Count == 0)
            return;

        _ctx.Store.Save(player);
        session.Push(GamePf.TaskValueSyncRsp, BuildTaskValueSync(changed));

        Log.Info($"[session {session.Id}] 任务值增量推送 pf 1026 共 {changed.Count} 条：" +
                 string.Join(" ", changed.Take(12).Select(c =>
                     $"{Player.TaskGroupOf(c.Id)}/{Player.TaskIdOf(c.Id)}={c.Value}")) +
                 (changed.Count > 12 ? " …" : ""));
    }

    /// <summary>
    /// 组 pf 1026 <c>TaskValueSyncRsp{ repeated TaskInt arr }</c>。
    /// 客户端 <c>Game.Player.OnTaskValueSyncRsp</c>（RVA 0x1852B18）是**逐条 merge**
    /// （<c>Dictionary&lt;uint,int&gt;.ContainsKey</c> → <c>set_Item</c>，否则 <c>Add</c>），
    /// 不清表，所以只推变化的条目是安全的。
    /// </summary>
    internal static TaskValueSyncRsp BuildTaskValueSync(IEnumerable<(uint Id, int Value)> changed)
    {
        var rsp = new TaskValueSyncRsp();
        foreach (var (id, value) in changed)
            rsp.Arr.Add(new TaskInt { Id = id, Value = value });
        return rsp;
    }

    private IEnumerable<(string Kind, IReadOnlyDictionary<int, MissionEntry> Entries)> AllEntryBuckets()
    {
        yield return ("mission", _table.Daily);
        yield return ("achieve", _table.Achieve);
        yield return ("other", _table.AchieveOther);
        yield return ("guide", _table.Guide);
        yield return ("club", _table.Club);
    }

    // =================================================================
    //  回包
    // =================================================================

    /// <summary>
    /// 组 <c>MissionMgrMsg</c> 的回包。
    ///
    /// 字段与依据（<c>MissionManager:Dispatcher</c> 逐行核过，只有这 4 个会被解引用）：
    /// <list type="bullet">
    /// <item><c>nError</c> 必填 —— MissionManager.lua:663 直接 <c>&gt; 0</c> 比较，nil 会抛。</item>
    /// <item><c>nMission</c> 必填 —— 7 路分发依据（MissionManager.lua:667 起）。</item>
    /// <item><c>tbIdList</c> 仅「一键领取日常任务」时给 —— MissionManager.lua:734..740，
    ///   **单领时必须省略**（给了空数组等于告诉客户端「一键领了 0 条」）。</item>
    /// <item><c>tbItems</c> 仅 nMission==2 时给，且**必须是空数组**：
    ///   MissionManager.lua:785 <c>if tbItems and next(tbItems)</c> 对空表等价于不给；
    ///   而非空表会走到 :791 <c>table.insert(tbCharacterCard, tbGDPLN)</c> —— 那里的
    ///   <c>tbGDPLN</c> 是**未定义的局部变量（nil）**，是客户端的 bug，
    ///   只要奖励里出现 G==1（角色卡）就会踩到。所以宁可空着，让它走配置表分支。</item>
    /// </list>
    /// </summary>
    internal static JsonObject BuildReply(int nMission, int nError, IReadOnlyList<int>? idList)
    {
        var obj = new JsonObject
        {
            ["nError"] = nError,
            ["nMission"] = nMission,
        };

        if (idList is not null)
        {
            var arr = new JsonArray();
            foreach (int id in idList)
                arr.Add(id);
            obj["tbIdList"] = arr;
        }

        if (nMission == MissionActivePoint)
            obj["tbItems"] = new JsonArray();   // 恒空，理由见方法注释

        return obj;
    }

    /// <summary>
    /// 组 LuaCall 200 <c>MissionNotify</c> 的 tbParam。
    /// 字段取自 LuaCall.lua:2827 的注释 <c>{Type = 0, Id = achieve.id,}</c> 与
    /// <c>RemindManager:NewMissionRemind</c>（RemindManager.lua:58..79）实际读到的三个字段：
    /// <c>tbParam.Type</c>（0 任务 / 1 成就）、<c>tbParam.Id</c>（配置表 id）、<c>tbParam.Count</c>
    /// （提醒条数，UI_Remind.lua:115 <c>if tbConf.Count ~= nil and tbConf.Count &gt; 1</c> 是 nil 安全的，
    /// 但既然有取证就填上）。
    /// </summary>
    internal static JsonObject BuildNotifyPayload(int type, int id, int count) => new()
    {
        ["Type"] = type,
        ["Id"] = id,
        ["Count"] = count,
    };

    /// <summary>
    /// 推一条 LuaCall 200 <c>MissionNotify</c>。
    ///
    /// ★ <b>Id 必须能在对应配置表里查到</b>，否则客户端会抛：
    /// <c>RemindManager:NewMissionRemind</c> 里 <c>tbConfig = GetMission(Id) or {}</c> 之后立刻
    /// <c>temp.desc = UIText('UI_Remind.Mission') .. tbConfig.desc</c>（RemindManager.lua:62..65）
    /// —— 查不到时 <c>tbConfig.desc</c> 是 nil，字符串连接 nil 直接报错。
    /// Type 0 查 DailyMission.txt，Type 1 查 Achievement.txt（group 6，<c>GetNormalAchieve</c>）。
    /// 所以这里先校验再发，校验不过只记 WARN。
    /// </summary>
    public bool TryPushMissionNotify(ClientSession session, int type, int id, int count = 1)
    {
        if (!CanPushMissionNotify(type, id))
        {
            Log.Warn($"[session {session.Id}] 拒绝推送 MissionNotify Type={type} Id={id}：" +
                     "配置表里查不到该 id，客户端 RemindManager.lua:65 会因 tbConfig.desc 为 nil 抛错");
            return false;
        }

        _ctx.LuaCalls.CallClient(session, MissionNotifyCmd, BuildNotifyPayload(type, id, count));
        return true;
    }

    /// <summary>
    /// <see cref="TryPushMissionNotify"/> 的纯判定部分：Type 0 的 Id 必须在 DailyMission.txt 里、
    /// Type 1 的 Id 必须在 Achievement.txt（group 6）里，其它 Type 一律拒绝
    /// （<c>RemindManager:NewMissionRemind</c> 只认 0/1，RemindManager.lua:61/:70）。
    /// </summary>
    internal bool CanPushMissionNotify(int type, int id) => type switch
    {
        NotifyTypeMission => _table.GetDaily(id) is not null,
        NotifyTypeAchieve => _table.GetAchieve(id) is not null,
        _ => false,
    };

    // =================================================================
    //  Player.Extra["mission"] 存档
    // =================================================================

    /// <summary>
    /// <c>Player.Extra["mission"]</c> 的内存形态。JSON 形状：
    /// <code>
    /// { "v":1,
    ///   "claimed":{ "mission":[..], "achieve":[..], "guide":[..], "club":[..] },
    ///   "flags":{ "activePoint":[..], "guideProgress":[..], "clubProgress":[..] } }
    /// </code>
    /// <c>claimed</c> 是「已领奖的配置表 id」，<c>flags</c> 是「已领的进度奖励档位」。
    /// 这份记录是权威，<see cref="Player.Tasks"/> 只是它的投影（见 <see cref="SyncTaskValues"/>）。
    /// </summary>
    internal sealed class MissionRecord
    {
        /// <summary>存档结构版本（将来加字段时用；当前恒 1）。</summary>
        public const int Version = 1;

        private readonly Dictionary<string, SortedSet<int>> _claimed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedSet<int>> _flags = new(StringComparer.Ordinal);

        /// <summary>已领奖 id 集合（kind ∈ mission/achieve/guide/club）。</summary>
        public SortedSet<int> Claimed(string kind) => Bucket(_claimed, kind);

        /// <summary>已领进度奖励档位集合（kind ∈ activePoint/guideProgress/clubProgress）。</summary>
        public SortedSet<int> Flags(string kind) => Bucket(_flags, kind);

        private static SortedSet<int> Bucket(Dictionary<string, SortedSet<int>> map, string kind)
        {
            if (!map.TryGetValue(kind, out var set))
            {
                set = new SortedSet<int>();
                map[kind] = set;
            }
            return set;
        }

        public JsonObject ToJson()
        {
            var claimed = new JsonObject();
            foreach (var (kind, set) in _claimed.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                claimed[kind] = ToArray(set);

            var flags = new JsonObject();
            foreach (var (kind, set) in _flags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                flags[kind] = ToArray(set);

            return new JsonObject
            {
                ["v"] = Version,
                ["claimed"] = claimed,
                ["flags"] = flags,
            };

            static JsonArray ToArray(SortedSet<int> set)
            {
                var arr = new JsonArray();
                foreach (int v in set)
                    arr.Add(v);
                return arr;
            }
        }

        public static MissionRecord FromJson(JsonNode? node)
        {
            var record = new MissionRecord();
            if (node is not JsonObject obj)
                return record;

            Fill(obj["claimed"], record._claimed);
            Fill(obj["flags"], record._flags);
            return record;

            static void Fill(JsonNode? section, Dictionary<string, SortedSet<int>> into)
            {
                if (section is not JsonObject sec)
                    return;
                foreach (var (kind, value) in sec)
                {
                    if (value is not JsonArray arr)
                        continue;
                    var set = Bucket(into, kind);
                    foreach (var item in arr)
                    {
                        if (item is null)
                            continue;
                        try { set.Add(item.GetValue<int>()); }
                        catch { /* 存档被手改成非数字：跳过，不让整份记录失效 */ }
                    }
                }
            }
        }
    }

    /// <summary>读 <c>Player.Extra["mission"]</c>；缺失或结构异常时返回空记录（不抛）。</summary>
    internal static MissionRecord ReadRecord(Player player)
        => MissionRecord.FromJson(player.Extra.TryGetValue(ExtraKey, out var node) ? node : null);

    /// <summary>写回 <c>Player.Extra["mission"]</c>（调用方负责 <c>Store.Save</c>）。</summary>
    internal static void WriteRecord(Player player, MissionRecord record)
        => player.Extra[ExtraKey] = record.ToJson();

    /// <summary>某条任务是否已领奖：记账里有，或任务值 bit0 已置（别的通道写过也认）。</summary>
    private static bool IsClaimed(Player player, MissionRecord record, string kind, MissionEntry entry)
        => record.Claimed(kind).Contains(entry.Id)
           || ClaimedOf(player.GetTask(entry.Group, entry.Id)) != 0;

    // =================================================================
    //  任务值位域（MissionManager.lua:595..606 + Lib.lua:519 LoadBits）
    // =================================================================

    /// <summary>进度 = <c>LoadBits(v, 1, 31)</c>（MissionManager.lua:597）。负值按 0。</summary>
    internal static int ProgressOf(int taskValue) => taskValue <= 0 ? 0 : taskValue >> 1;

    /// <summary>已领标记 = <c>LoadBits(v, 0, 0)</c>（MissionManager.lua:604）。</summary>
    internal static int ClaimedOf(int taskValue) => taskValue <= 0 ? 0 : taskValue & 1;

    /// <summary>
    /// 反过来拼任务值：<c>(进度 &lt;&lt; 1) | 已领bit</c>。
    /// 进度夹在 [0, 0x3FFFFFFF]，保证左移一位后仍是正的 int32
    /// （客户端 <c>LoadBits</c> 用 <c>%</c> 与 <c>/</c> 做位运算，负数会算错）。
    /// </summary>
    internal static int ComposeTaskValue(int progress, int claimedBit)
    {
        if (progress < 0)
            progress = 0;
        if (progress > 0x3FFFFFFF)
            progress = 0x3FFFFFFF;
        return (progress << 1) | (claimedBit != 0 ? 1 : 0);
    }

    /// <summary>读整数；Lua/JSON 侧可能给 float 或字符串，兜一层（与 PhoneMsgFeature 同款）。</summary>
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
}
