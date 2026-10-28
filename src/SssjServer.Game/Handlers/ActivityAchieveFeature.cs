using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 福利页「活动成就」领奖（上行 <c>NormalActivityGetAward</c> 的 <c>nType == 1</c> 分支，
/// 覆盖Template=2活动与重开的Template=5活动262）。
/// 取证见 `work/activity-award-forensics.md` §4、台账 `docs/05` §29。
///
/// <para><b>0. ★★★ 同一个上行、两条下行通道、两种<u>相反</u>的错误约定</b></para>
/// <list type="table">
///   <item><term>活动 8 领电力（<c>nType=4</c>）</term>
///     <description>下行 <c>NormalActivityMsg</c>：**全链路无 <c>nError</c> 检查**，
///       成功不带、失败**静默不回**（<see cref="EnergyGetFeature"/>）。</description></item>
///   <item><term>本域（<c>nType=1</c>）</term>
///     <description>下行是 **<c>MissionMgrMsg</c>**（不是 `NormalActivityMsg`！），
///       <c>nMission = 0</c>，且 `MissionManager.lua:663` 是 <c>if tbParam.nError &gt; 0</c>
///       ⇒ **必须带 <c>nError</c>**（缺了 `nil &gt; 0` **抛错**），
///       **失败也必须回**（否则客户端卡遮罩 + 不弹提示）。</description></item>
/// </list>
///
/// <para><b>1. ★★★ 下行为什么是 <c>MissionMgrMsg</c>（取证 §4.4 的四步证据链）</b></para>
/// <list type="number">
///   <item><c>OnNormalActivityMsg</c>（`NormalActivityConfig.lua:1683..1788`）**没有 <c>nType == 1</c> 分支**；</item>
///   <item>客户端回调 <c>OnGetAchieveAward</c> 是**事件订阅**（`UI_NormalActivity.lua:31`
///     订阅 <c>eEventName.OnGetAchieveAward = 80</c>），不是 UI.Call；</item>
///   <item>全仓**唯一**派发点是 `MissionManager.lua:668`：<c>if tbParam.nMission == 0 then DispatchLuaEvent(...)</c>；</item>
///   <item><c>MissionManager:Dispatcher</c> 的唯一调用者是 `S2CCall.lua:85` 的 <c>S2C.MissionMgrMsg</c>。</item>
/// </list>
/// ⇒ 正确回包 = <c>sFunction="MissionMgrMsg"</c> + <c>{nMission = 0, nError = 0}</c>，
/// 也就是 <see cref="MissionFeature"/> 的成就路**已经在发的东西**。
///
/// <para><b>2. ★★ 必须「先推 pf 1026、再回包」</b></para>
/// 客户端 <c>OnGetAchieveAward()</c> **不收参数**，第一句就重查配置 + <c>OpenRightPanel</c> 重刷整页
/// ⇒ 按钮变灰靠**重读 group 18 的任务值**。反了就是「领到了但按钮还在」。
/// 与角色卡（`docs/05` §22.2）、活动 8（§3.6）同一个模式。
///
/// <para><b>3. ★ 奖励内容服务端不下发</b></para>
/// 弹窗是客户端自己按 <c>AchievementOther.awardsList</c> 拼的（§4.5）
/// ⇒ 回包只要 <c>{nMission, nError}</c>；但服务端**必须真的发那些道具**（走 pf 1031/1032）。
///
/// <para><b>4. ★★ 已知边界：进度不会自动增长（与成就域整体同一个缺口）</b></para>
/// 判定值 <c>GetTaskPro(18, missionId)</c> 的 bit1..31 需要各玩法**埋点累加**，
/// 而 <c>achieveType</c> 的语义（76/137/75/…）**未取证**（§4.6/§4.7）。
/// ⇒ 本实现做的是**领奖闸门**：进度够就能领、不够正确拒绝、领过不能重领。
/// 进度怎么涨是独立的一件事（`MissionFeature.DerivedProgress` 也只实现了「通关次数」一种）。
/// ★ GM 可用 <c>gmsrv.sh 'task 18 &lt;成就id&gt; &lt;(进度&lt;&lt;1)|已领bit&gt;'</c> 造进度来验收。
/// </summary>
public sealed class ActivityAchieveFeature : IGameFeature
{
    /// <summary>上行 sFunction（与活动 8 共用，见 <see cref="Install"/>）。</summary>
    public const string UpFunction = EnergyGetFeature.UpFunction;

    /// <summary>下行 sFunction（★ 不是 `NormalActivityMsg`）。</summary>
    public const string DownFunction = "MissionMgrMsg";

    /// <summary><c>nType == 1</c> = 成就奖励（`UI_NormalActivity.lua:657` 硬编码）。</summary>
    public const int TypeAchieve = 1;

    /// <summary><c>nMission == 0</c> = 成就路（`MissionManager.lua:667`）。</summary>
    public const int MissionAchieve = 0;

    /// <summary>成功（★ `MissionManager.lua:663` 是 <c>nError &gt; 0</c> 的数值比较 ⇒ 必须带 0）。</summary>
    public const int ErrOk = 0;

    /// <summary>该成就不存在 / 不属于这个活动。</summary>
    public const int ErrNotExist = 1;

    /// <summary>已经领过（bit0 已置）。</summary>
    public const int ErrAlreadyGot = 2;

    /// <summary>进度不足。</summary>
    public const int ErrNotFinished = 3;

    private GameFeatureContext _ctx = null!;
    private ActivityAchieveTable _activities = ActivityAchieveTable.Empty;

    public string Name => "福利页活动成就领奖（NormalActivityGetAward nType=1 → MissionMgrMsg nMission=0）";

    /// <summary>
    /// 装配。
    ///
    /// ★★ <b>用协处理器而不是 <c>MapFunction</c></b>：<c>NormalActivityGetAward</c> 的主 handler
    /// 已被 <see cref="EnergyGetFeature"/>（活动 8 领电力）持有，两边都 <c>MapFunction</c>
    /// 会**互相吞整域**（第八轮踩过并修掉的架构缺陷，`docs/05` §27.9）。
    /// 本域只认 <c>nType == 1</c>，其余返回 <c>false</c> 交回活动 8 那条路。
    /// </summary>
    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _activities = ActivityAchieveTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.AddFunctionCoHandler(UpFunction, OnGetAward);
    }

    /// <summary>只处理 <c>nType == 1</c>；其余（含活动 8 的 <c>nType=4</c>）交回主 handler。</summary>
    private async Task<bool> OnGetAward(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return false;

        // ★ 真机上行可能是 JsonArray（本项目踩过：JsonNode["key"] 对 JsonArray 会抛）
        if (param is not JsonObject obj)
            return false;

        int type = ReadInt(obj["nType"]);
        if (type != TypeAchieve)
            return false;

        await DoClaim(session, player,
                      activityId: ReadInt(obj["nActivityId"]),
                      achieveId: ReadInt(obj["nId"]));
        return true;
    }

    /// <summary>
    /// 领奖：校验归属 → 校验没领过 → 校验进度够 → 置 bit0 → **先推 pf 1026 + 奖励，再回包**。
    /// </summary>
    private Task DoClaim(ClientSession session, Player player, int activityId, int achieveId)
    {
        lock (player)
        {
            if (activityId == 262)
            {
                var changes = new List<KeyValuePair<uint, int>>();
                bool reset = Tower21Missions.RefreshDaily(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), changes);
                Tower21Missions.SyncSp(player, _ctx.Missions, changes);
                if (reset || changes.Count > 0) _ctx.Store.Save(player);
                if (changes.Count > 0) GameNotify.PushTaskValues(session, changes);
            }
            return ClaimLocked(session, player, activityId, achieveId);
        }
    }

    private Task ClaimLocked(ClientSession session, Player player, int activityId, int achieveId)
    {
        // ① 该成就必须属于这个活动（防越活动领奖）
        if (!_activities.Contains(activityId, achieveId))
        {
            Fail(session, ErrNotExist);
            Log.Warn($"[session {session.Id}] 活动成就领奖：成就 {achieveId} 不属于活动 {activityId}" +
                     $"（该活动有 {_activities.AchievesOf(activityId).Count} 条）");
            return Task.CompletedTask;
        }

        // ② 成就配置必须存在（AchievementOther.txt / group 18）
        var entry = _ctx.Missions.GetAchieveOther(achieveId);
        if (entry is null)
        {
            Fail(session, ErrNotExist);
            Log.Warn($"[session {session.Id}] 活动成就领奖：成就 {achieveId} 不在 AchievementOther.txt 里");
            return Task.CompletedTask;
        }

        int taskValue = player.GetTask(ActivityAchieveTable.AchieveGroup, achieveId);
        int progress = MissionFeature.ProgressOf(taskValue);
        int claimed = MissionFeature.ClaimedOf(taskValue);

        // ③ 领过就不能再领（bit0）
        if (claimed != 0)
        {
            Fail(session, ErrAlreadyGot);
            Log.Warn($"[session {session.Id}] 活动成就领奖：活动 {activityId} 成就 {achieveId} 已领过" +
                     $"（任务值 {taskValue}，bit0=1）");
            return Task.CompletedTask;
        }

        // ④ 进度必须达到阈值。★ 成就用 assistparam[1] 选 missionMax 的第几项（achieveKey: true）
        int required = entry.RequiredCount(achieveKey: true);
        if (progress < required)
        {
            Fail(session, ErrNotFinished);
            Log.Warn($"[session {session.Id}] 活动成就领奖：活动 {activityId} 成就 {achieveId} " +
                     $"进度不足（{progress}/{required}，achieveType={entry.AchieveType}）");
            return Task.CompletedTask;
        }

        // ⑤ 置 bit0（保留进度），发奖
        int newValue = MissionFeature.ComposeTaskValue(progress, 1);
        player.SetTask(ActivityAchieveTable.AchieveGroup, achieveId, newValue);
        var granted = _ctx.Rewards.Grant(player, entry.Rewards, RewardSource.Extra);
        _ctx.Store.Save(player);

        // ★★ 先推数据、再回包 —— 客户端回调不收参数、靠重读 group 18 变灰按钮（类注释 §2）
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(ActivityAchieveTable.AchieveGroup, achieveId), newValue),
        });
        GameNotify.PushRewards(session, player, granted);
        Ok(session);

        Log.Info($"[session {session.Id}] 活动成就领奖：活动 {activityId} 成就 {achieveId}" +
                 $"（进度 {progress}/{required}）→ 置 bit0、发奖 {granted.Describe()}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 成功回包：<c>MissionMgrMsg{nMission = 0, nError = 0}</c>。
    /// ★ <c>nError</c> **必须带**（`MissionManager.lua:663` 数值比较，缺了 `nil &gt; 0` 抛）。
    /// 奖励内容不下发（客户端自己按配置拼弹窗，类注释 §3）。
    /// </summary>
    private void Ok(ClientSession session)
        => _ctx.LuaCalls.CallClientFunction(session, DownFunction, new JsonObject
        {
            ["nMission"] = MissionAchieve,
            ["nError"] = ErrOk,
        });

    /// <summary>
    /// 失败回包。★ **必须回** —— 客户端 `MissionManager.lua:664` 会
    /// <c>ShowError(nError, nMission)</c> 弹提示；不回则卡遮罩且玩家不知道为什么。
    /// </summary>
    private void Fail(ClientSession session, int code)
        => _ctx.LuaCalls.CallClientFunction(session, DownFunction, new JsonObject
        {
            ["nMission"] = MissionAchieve,
            ["nError"] = code <= 0 ? ErrNotExist : code,
        });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;
}
