using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 少女换装 / 看板娘域（<c>sFunction = GirlLogic</c> 的三条 sCmd）。台账 `docs/05` §37。
///
/// <para><b>0. 三条命令与各自的存储位置（★ 三条各存在不同地方，通道也不同）</b></para>
/// <list type="table">
///   <item><term><c>SetMainGirl</c>（换看板娘）</term>
///     <description>任务值 <b>(2, 2)</b>（<c>GirlCommon:MainGirl</c>，`GirlCommon.lua:676..687`）
///       ⇒ 推 <b>pf 1026</b>。</description></item>
///   <item><term><c>ChangeCloth</c>（换服饰）</term>
///     <description>protobuf <b><c>GirlInfo.modelid</c></b>（`GirlCommon.lua:109/139` 的
///       <c>ModelId</c> 分支走 <c>tbGirl:GetModelId()</c>，偏移为 0 ⇒ 不是任务值）
///       ⇒ 推 <b>pf 1030</b>，推 1026 **毫无效果**（与好感度那条同型，`docs/05` §27）。</description></item>
///   <item><term><c>SetModelInFight</c>（战斗服开关）</term>
///     <description>少女属性 group 3 偏移 <b>9</b>（<c>IsFightModel</c>，`GirlCommon.lua:34`）
///       ⇒ 推 <b>pf 1026</b>。</description></item>
/// </list>
///
/// <para><b>1. ★★★ 必须用协处理器，不能抢 <c>MapFunction</c></b></para>
/// <c>GirlLogic</c> 已被 <see cref="GirlTrainFeature"/>（主 handler）+
/// <see cref="GirlLoveFeature"/>（协处理器）瓜分。<see cref="LuaCallRouter.MapFunction"/>
/// 是**覆盖式**的 ⇒ 再抢会把那两个域**整域吃掉且没有 unhandled 日志**
/// （`AGENTS.md` §5、`docs/05` §27.9，第八轮踩过）。
///
/// <para><b>2. ★★★ <c>ChangeCloth</c> 的回包会让客户端**再自动发一条** <c>SetModelInFight</c></b></para>
/// <c>GirlLogic.lua:621..623</c>：回包处理末尾若
/// 「是 Spine 皮肤 <b>且</b> 该皮肤没有 3D 模型」就调 <c>self:SetModelInFight(nId, false)</c>。
/// ⇒ 两条**必须一起实现**，否则那条变 unhandled，而本族
/// <c>Blocking.Hide()</c> 在 Dispatcher 里 ⇒ **界面一直卡遮罩**（`docs/05` §17.1）。
/// ★ 客户端 <c>UI_GirlClothes.lua:583</c> 也有同一条兜底（列表项点击时）。
///
/// <para><b>3. ★★ <c>OnSetMainGirl</c> 不读回包的任何字段</b></para>
/// <c>GirlLogic.lua:703..712</c> 只是转手调 <c>ui:OnSetMainGirl()</c>，而后者
/// （<c>UI_GirlClothes.lua:927</c> → <c>:451 UpdateBtn</c>）**重读本地
/// <c>GirlCommon:MainGirl()</c>**（即任务值）⇒ **必须先推 pf 1026、再回包**。
/// 这是 `docs/05` §22.2 那条「回包 handler 不收参数 ⇒ 数值必须靠别的通道先到」的又一个实例。
///
/// <para><b>4. ★★ <c>OnChangeClothError</c> 函数体是空的 ⇒ 失败时玩家零反馈</b></para>
/// <c>GirlLogic.lua:629</c> 整个函数只有一行被注释掉的 <c>MessageBox</c>。
/// ⇒ 服务端**必须把拒绝原因写进日志**，否则真机上「点了没反应」完全无从排查。
/// （错误码本身仍要回，因为它是解遮罩的唯一途径。）
///
/// <para><b>5. ★★ 服饰是否已拥有 = 任务值 group 4 的 <c>SuitValue</c>，三态</b></para>
/// <c>GirlCommon:SuitValue</c>（`GirlCommon.lua:637`）：
/// <b>0 未获得 / 1 已获得未穿过（红点 NEW）/ 2 已穿过</b>。
/// <list type="bullet">
///   <item>键要过 <see cref="GmUnlock.FixModelId"/>（<c>ModelId ≥ 2000</c> 的要换算，
///     例 7001 → 1711；漏了就是「发了却显示未获得」且无报错）；</item>
///   <item>联动少女（id 201..232）group <b>4 → 91</b> 且 girlId 先减 200
///     （<see cref="GirlLoveFeature.VarGroup"/> 是 group 3 的版本，本域自己算 group 4 的）；</item>
///   <item>穿上后把 1 → 2 清红点（<c>UI_GirlClothes.lua:846</c>
///     <c>IsNew = (SuitValue == 1)</c>）。</item>
/// </list>
///
/// <para><b>6. ★★★ 皮肤道具（genre 19）入包时必须顺带解锁服饰</b></para>
/// 十面妆造买到的是 genre <b>13</b>「皮肤礼包」（<c>ClassName = IBGiftAuto</c>），
/// 它的 <c>ExtParam1</c> 是 <c>ItemBoxInfo.txt</c> 的 <c>boxId</c>，箱内 <c>Fix</c> 项才是
/// genre <b>19</b> 的 <c>SuitItem</c>（取证 `IBLogic.lua:1196..1205`）。
/// 而 genre 19 的道具**服务端过去只是塞进背包**，从不写 group 4
/// ⇒ 玩家「买到了皮肤但换装界面里还是锁着」。本域在
/// <see cref="OnPlayerPrepare"/> + <see cref="GrantSuitsForItems"/> 里补齐这条链。
/// </summary>
public sealed class GirlDressFeature : IGameFeature
{
    /// <summary>下行入口（与特训域/好感度域共用 <c>sFunction</c>，见类注释 §1）。</summary>
    public const string Function = "GirlLogic";

    /// <summary>换看板娘（`GirlLogic.lua:698`，上行 <c>{nId}</c>）。</summary>
    public const string CmdSetMainGirl = "SetMainGirl";

    /// <summary>换服饰（`GirlLogic.lua:606`，上行 <c>{nId, nSuit}</c>）。</summary>
    public const string CmdChangeCloth = "ChangeCloth";

    /// <summary>战斗服开关（`GirlLogic.lua:719`，上行 <c>{nId, nUse}</c>）。</summary>
    public const string CmdSetModelInFight = "SetModelInFight";

    /// <summary>少女属性偏移 9 = <c>IsFightModel</c>（`GirlCommon.lua:34`）。</summary>
    public const int VarIsFightModel = 9;

    /// <summary>少女属性偏移 13 = <c>SuitCount</c>「衣服数」（`GirlCommon.lua:41`）。</summary>
    public const int VarSuitCount = GmUnlock.GirlVarSuitCount;

    /// <summary>
    /// 服饰获得标记 group（<c>GirlCommon:SuitValue</c> 走 <c>GetGirlTaskEx(4, …)</c>）。
    /// ★ 联动少女重映射到 <see cref="SuitGroupLink"/>。
    /// </summary>
    public const int SuitGroup = GmUnlock.SuitGroup;

    /// <summary>
    /// 联动少女的服饰标记 group（<c>GirlCommon:GetExtGroup</c>，`GirlCommon.lua:1495..1506`：
    /// 3→90 / <b>4→91</b> / 7→92）。
    /// </summary>
    public const int SuitGroupLink = 91;

    /// <summary>服饰三态：未获得。</summary>
    public const int SuitNone = 0;

    /// <summary>服饰三态：已获得但没穿过（客户端显示 NEW 红点）。</summary>
    public const int SuitNew = 1;

    /// <summary>服饰三态：已获得且穿过（无红点）。</summary>
    public const int SuitWorn = GmUnlock.OwnedNotNew;

    /// <summary>
    /// 通用失败码。★ 本族失败时客户端**只拿到这个数字**，而
    /// <c>OnChangeClothError</c>/<c>OnSetMainGirl</c> 都没有按码分支
    /// ⇒ 数值本身没有语义，重点是「有回包让遮罩解开」+ 日志写清原因。
    /// </summary>
    public const int ErrGeneric = 1;

    private GameFeatureContext _ctx = null!;
    private GirlDressTable _dress = GirlDressTable.Empty;

    public string Name => "少女换装/看板娘（GirlLogic: SetMainGirl / ChangeCloth / SetModelInFight）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _dress = GirlDressTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        // ★★★ 协处理器，不是 MapFunction —— 见类注释 §1
        ctx.AddFunctionCoHandler(Function, OnGirlLogicCommand);

        // ★ 登录时把「已持有的皮肤道具 / 角色卡」对应的服饰标记补齐（见类注释 §6）。
        // 用 OnPlayerPrepare（pf 1005 之前）⇒ 变更随登录本来就要发的 pf 1026 全量一起走。
        ctx.OnPlayerPrepare(SyncOwnedSuits);
    }

    /// <summary>只认本域三条 sCmd，其余 <c>false</c> 交回别的处理器。</summary>
    private async Task<bool> OnGirlLogicCommand(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return false;

        string sCmd = (param as JsonObject)?["sCmd"]?.ToString() ?? "";
        switch (sCmd)
        {
            case CmdSetMainGirl:
                await OnSetMainGirl(session, player, param);
                return true;
            case CmdChangeCloth:
                await OnChangeCloth(session, player, param);
                return true;
            case CmdSetModelInFight:
                await OnSetModelInFight(session, player, param);
                return true;
            default:
                return false;
        }
    }

    // ================= SetMainGirl 换看板娘 =================

    /// <summary>
    /// 换看板娘：校验持有 → 写任务值 (2,2) → **先推 pf 1026** → 回包。
    ///
    /// ★ 回包内容无所谓（客户端不读，见类注释 §3），但**必须回**（解遮罩）。
    /// ★ 主城看板娘立绘是 <c>UI_Main:LoadLive2D</c>（`UI_Main.lua:965`）在**界面初始化时**
    /// 读的 ⇒ 换完要**重开主城界面或重登**才会换人，这不是 bug（同 SystemUnlock 锁图标那条）。
    /// 换装界面里的「设为看板娘」勾选状态是立刻刷新的（<c>UpdateBtn</c>）。
    /// </summary>
    private Task OnSetMainGirl(ClientSession session, Player player, JsonNode? param)
    {
        lock (player) return SetMainGirlLocked(session, player, param);
    }

    private Task SetMainGirlLocked(ClientSession session, Player player, JsonNode? param)
    {
        uint girlId = (uint)ReadInt(param?["nId"]);

        if (girlId == 0 || player.GetGirl(girlId) is null)
        {
            Fail(session, CmdSetMainGirl, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/SetMainGirl 少女 {girlId} 未持有"
                   + $"（param={param?.ToJsonString()}）→ nError={ErrGeneric}"
                   + "（★ 客户端 OnSetMainGirlError 不存在 ⇒ 玩家看不到任何提示）");
            return Task.CompletedTask;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try { _ctx.PledgeTimes.SaveRefresh(_ctx.Store, player, now); }
        catch (Exception)
        {
            Fail(session, CmdSetMainGirl, ErrGeneric);
            return Task.CompletedTask;
        }
        int before = player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId);
        if (before == (int)girlId)
        {
            // 客户端 UI_GirlClothes.lua:31 本来就会拦「已经是看板娘」，这里只兜底：
            // 原样回包（不是失败），免得遮罩卡住
            Ok(session, CmdSetMainGirl, new JsonObject { ["nId"] = girlId });
            Log.Info($"[session {session.Id}] GirlLogic/SetMainGirl 少女 {girlId} 已经是看板娘，仅回执");
            return Task.CompletedTask;
        }

        var snapshot = new ChallengeMutationSnapshot(player);
        try
        {
            player.SetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId, (int)girlId);
            _ctx.PledgeTimes.Refresh(player, now);
            _ctx.Store.SaveRequired(player);
        }
        catch (Exception)
        {
            snapshot.Restore(player);
            Fail(session, CmdSetMainGirl, ErrGeneric);
            return Task.CompletedTask;
        }

        // ★ 先推数据、再回包（客户端 handler 不收参数、直接重读任务值）
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId), (int)girlId),
        });
        Ok(session, CmdSetMainGirl, new JsonObject { ["nId"] = girlId });

        Log.Info($"[session {session.Id}] GirlLogic/SetMainGirl 看板娘 {before} → {girlId}"
               + "（★ 主城立绘要重开界面/重登才重绘，换装界面的勾选立刻刷新）");
        return Task.CompletedTask;
    }

    // ================= ChangeCloth 换服饰 =================

    /// <summary>
    /// 换服饰：校验已拥有 → 写 <c>Girl.Modelid</c> → 清 NEW 标记 → **推 pf 1030 + 1026** → 回包。
    ///
    /// 上行 <c>{nId, nSuit}</c>，其中 <c>nSuit</c> 就是 <b>ModelId</b>
    /// （`UI_GirlClothes.lua:496/578` 传的都是 <c>tbBaseInfo.ModelId</c>）。
    ///
    /// ★★ 回包**必须带 <c>nId</c> + <c>nSuit</c>**（`GirlLogic.lua:614..615` 要用它们调
    /// <c>ui:OnDragCloth(nId, nSuit)</c> 播换装动画；`UI_GirlClothes.lua:881`
    /// 第一句 <c>if nGirlId ~= self.nGirlId then return end</c>）。
    ///
    /// ★★★ 回包之后客户端可能**自动再发一条 <c>SetModelInFight</c>**（见类注释 §2），
    /// 那是预期行为、不是重复请求。
    /// </summary>
    private Task OnChangeCloth(ClientSession session, Player player, JsonNode? param)
    {
        uint girlId = (uint)ReadInt(param?["nId"]);
        int modelId = ReadInt(param?["nSuit"]);

        var girl = girlId == 0 ? null : player.GetGirl(girlId);
        if (girl is null || modelId == 0)
        {
            Fail(session, CmdChangeCloth, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/ChangeCloth 参数非法或少女未持有"
                   + $"（nId={girlId} nSuit={modelId} param={param?.ToJsonString()}）"
                   + "→ ★ 客户端 OnChangeClothError 是空函数 ⇒ 玩家零反馈，只能看这条日志");
            return Task.CompletedTask;
        }

        // The client builds its list from exact configured ModelIds, before
        // consulting permanent ownership. FixModelId is only a storage encoding.
        if (modelId < 0 || !GmUnlock.IsValidGirlId(girlId)
            || (!_dress.IsSpineSuit(girlId, modelId)
                && !_ctx.CharacterCards.ForGirl(girlId).Any(c => c.ModelId == modelId)))
        {
            Fail(session, CmdChangeCloth, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/ChangeCloth 少女 {girlId} 无服饰配置 {modelId} → 拒绝换装");
            return Task.CompletedTask;
        }
        // ★ 校验「这套衣服拥有吗」：SuitValue > 0
        int suitValue = GetSuitValue(player, girlId, modelId);
        if (suitValue <= SuitNone)
        {
            Fail(session, CmdChangeCloth, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/ChangeCloth 少女 {girlId} 未拥有服饰 {modelId}"
                   + $"（group {SuitGroupOf(girlId)} taskId {SuitTaskId(girlId, modelId)}"
                   + $"、FixModelId={GmUnlock.FixModelId(modelId)} 的值是 {suitValue}）"
                   + "→ ★ 客户端 OnChangeClothError 是空函数 ⇒ 玩家零反馈");
            return Task.CompletedTask;
        }

        uint before = girl.Modelid;
        girl.Modelid = (uint)modelId;

        // ★ 穿过一次就把 NEW（1）清成已穿（2）—— UI_GirlClothes.lua:846 IsNew = (SuitValue == 1)
        var taskUpdates = new List<KeyValuePair<uint, int>>();
        if (suitValue == SuitNew)
        {
            SetSuitValue(player, girlId, modelId, SuitWorn, taskUpdates);
        }

        // Keep the persisted state valid even if the client's follow-up request
        // is lost: a portrait-only suit cannot enable a nonexistent fight model.
        if (_dress.IsSpineSuit(girlId, modelId) && !_dress.SuitHasModel(girlId, modelId)
            && GetVar(player, girlId, VarIsFightModel) != 0)
        {
            int group = GirlLoveFeature.VarGroup(girlId);
            int taskId = GirlLoveFeature.VarTaskId(girlId, VarIsFightModel);
            player.SetTask(group, taskId, 0);
            taskUpdates.Add(new(Player.ComposeTaskId(group, taskId), 0));
        }

        _ctx.Store.Save(player);

        // ★ 顺序：先 pf 1030（modelid 本体）→ 再 pf 1026（NEW 标记）→ 最后回包。
        // 客户端 OnDragCloth 只播动画，但紧随其后的 UI 刷新会重读 me:GetGirl(id).ModelId。
        GameNotify.PushGirl(session, girl);
        if (taskUpdates.Count > 0)
            GameNotify.PushTaskValues(session, taskUpdates);

        Ok(session, CmdChangeCloth, new JsonObject
        {
            ["nId"] = girlId,
            ["nSuit"] = modelId,
        });

        bool isSpine = _dress.IsSpineSuit(girlId, modelId);
        bool hasModel = _dress.SuitHasModel(girlId, modelId);
        Log.Info($"[session {session.Id}] GirlLogic/ChangeCloth 少女 {girlId} 服饰 {before} → {modelId}"
               + $"（{(isSpine ? $"Spine 皮肤、{(hasModel ? "有" : "无")}战斗模型" : "角色卡服装")}"
               + $"、SuitValue {suitValue}{(suitValue == SuitNew ? $"→{SuitWorn}" : "")}）"
               + (isSpine && !hasModel
                   ? "；★ 客户端接下来会自动补发一条 SetModelInFight（GirlLogic.lua:621）"
                   : ""));
        return Task.CompletedTask;
    }

    // ================= SetModelInFight 战斗服开关 =================

    /// <summary>
    /// 战斗服开关：写少女属性 group 3 偏移 9 → 推 pf 1026 → 回 <c>{bSuccess, nUse}</c>。
    ///
    /// 上行 <c>{nId, nUse}</c>（<c>nUse</c> 0/1；★ 客户端有三处**只传 <c>nId</c>** 的调用
    /// —— `GirlLogic.lua:622`、`UI_GirlClothes.lua:350/583` 都是
    /// <c>SetModelInFight(nId)</c>，<c>bUse</c> 为 nil ⇒ <c>nUse = 0</c>，即「关掉战斗服」）。
    ///
    /// ★★ 回包要 <b><c>bSuccess</c>（布尔）+ <c>nUse</c></b>（`GirlLogic.lua:730..743`）。
    /// <c>bSuccess</c> 为假时客户端**什么都不做**（连提示都没有），
    /// 所以失败路径**也走成功通道**回 <c>bSuccess = false</c> 更符合原意 ——
    /// 但那样遮罩靠 <c>On&lt;sCmd&gt;</c> 分支解开，一样安全。
    ///
    /// ★ 只有「有 3D 战斗模型」的皮肤才允许开（<c>IsModel</c> 列）：
    /// 无模型时客户端自己就把 toggle 隐藏了（`UI_GirlClothes.lua:348`），
    /// 服务端再兜一层，避免存档里出现「穿着没有模型的皮肤却开了战斗服」。
    /// </summary>
    private Task OnSetModelInFight(ClientSession session, Player player, JsonNode? param)
    {
        uint girlId = (uint)ReadInt(param?["nId"]);
        // ★ nUse 缺省视为 0（客户端三个「只传 nId」的调用点就是要关掉它）
        int use = ReadInt(param?["nUse"]) != 0 ? 1 : 0;

        var girl = girlId == 0 ? null : player.GetGirl(girlId);
        if (girl is null)
        {
            // ★ bSuccess = false ⇒ 客户端静默；但回包本身让遮罩解开
            Ok(session, CmdSetModelInFight, new JsonObject
            {
                ["bSuccess"] = false,
                ["nUse"] = use,
            });
            Log.Warn($"[session {session.Id}] GirlLogic/SetModelInFight 少女 {girlId} 未持有"
                   + $"（param={param?.ToJsonString()}）→ bSuccess=false（客户端无提示）");
            return Task.CompletedTask;
        }

        int modelId = (int)girl.Modelid;
        // 想开战斗服，但当前这套皮肤没有 3D 模型 ⇒ 拒绝（照客户端的 IsModel 判据）
        if (use == 1 && _dress.IsSpineSuit(girlId, modelId) && !_dress.SuitHasModel(girlId, modelId))
        {
            Ok(session, CmdSetModelInFight, new JsonObject
            {
                ["bSuccess"] = false,
                ["nUse"] = 0,
            });
            Log.Warn($"[session {session.Id}] GirlLogic/SetModelInFight 少女 {girlId} 当前皮肤 {modelId}"
                   + " 没有 3D 战斗模型（SpineSuit.IsModel 为空）⇒ 拒绝开启 → bSuccess=false");
            return Task.CompletedTask;
        }

        int before = GetVar(player, girlId, VarIsFightModel);
        if (before != use)
        {
            SetVar(session, player, girlId, VarIsFightModel, use);
            _ctx.Store.Save(player);
        }

        Ok(session, CmdSetModelInFight, new JsonObject
        {
            ["bSuccess"] = true,
            ["nUse"] = use,
        });
        Log.Info($"[session {session.Id}] GirlLogic/SetModelInFight 少女 {girlId} 战斗服 {before} → {use}"
               + $"（当前服饰 {modelId}）");
        return Task.CompletedTask;
    }

    // ================= 登录：补齐服饰获得标记 =================

    /// <summary>
    /// 登录时把「玩家已持有的皮肤道具 / 角色卡」对应的服饰标记补成已获得（见类注释 §6）。
    ///
    /// ★★★ 这是本域**最容易漏、漏了整域看起来没实现**的一步，与
    /// <see cref="GirlLoveFeature.SyncStoryUnlocks"/>（ELevel 置 1）完全同型：
    /// 换装列表是客户端按 <c>SuitValue(girlId, modelId) &gt; 0</c> **过滤**出来的
    /// （`UI_GirlClothes.lua:804/846`）⇒ 不置位的服饰**根本不出现在列表里**，
    /// 玩家连点都点不到，自然也发不出 <c>ChangeCloth</c>。
    ///
    /// 两个来源：
    /// <list type="number">
    ///   <item><b>皮肤道具</b>（genre 19 <c>SuitItem</c>）→ <c>SpineSuit.txt</c> 反查 <c>ModelId</c>；</item>
    ///   <item><b>角色卡</b>（genre 1）→ <c>CharacterCard.txt</c> 的 <c>ModelId</c> 列。
    ///     ★ GM 全解锁已经写过这一批，但**普通账号**（抽卡得到的卡）此前没人写
    ///     ⇒ 抽到新卡后换装界面里那套衣服是灰的。</item>
    /// </list>
    ///
    /// ★ 只把 <c>0 → 1</c>（NEW），**不动已穿过（2）的** ⇒ 幂等、重登不会把红点洗回来。
    /// ★ 顺带维护 <c>SuitCount</c>（group 3 偏移 13），它是「衣服数」的展示值。
    /// </summary>
    internal void SyncOwnedSuits(ClientSession session, Player player)
    {
        bool repairedFightModel = false;
        foreach (var girl in player.Girls)
        {
            int modelId = (int)girl.Modelid;
            if (!_dress.IsSpineSuit(girl.Girlid, modelId) || _dress.SuitHasModel(girl.Girlid, modelId)
                || GetVar(player, girl.Girlid, VarIsFightModel) == 0)
                continue;
            player.SetTask(GirlLoveFeature.VarGroup(girl.Girlid),
                GirlLoveFeature.VarTaskId(girl.Girlid, VarIsFightModel), 0);
            repairedFightModel = true;
        }
        if (repairedFightModel) _ctx.Store.Save(player);

        int marked = 0;

        void Note(uint girlId, int modelId)
        {
            if (girlId == 0 || modelId == 0 || player.GetGirl(girlId) is null)
                return;
            if (!GmUnlock.IsValidGirlId(girlId))
                return;
            if (GetSuitValue(player, girlId, modelId) != SuitNone)
                return;     // 已获得（1）或已穿过（2）：不动
            SetSuitValue(player, girlId, modelId, SuitNew, updates: null);
            marked++;
        }

        foreach (var item in player.Items.Where(i => i.Count > 0))
        {
            if (item.Genre == GirlDressTable.SuitItemGenre)
            {
                // ① 皮肤道具 → SpineSuit 反查
                var suit = _dress.SuitForItem(item.Genre, item.Detail, item.Particular, item.Level);
                if (suit is not null)
                    Note(suit.GirlId, suit.ModelId);
                else
                {
                    Log.Warn($"[session {session.Id}] 换装：皮肤道具 {item.Genre}-{item.Detail}-"
                           + $"{item.Particular}-{item.Level} 不在 SpineSuit.txt 里（本地 {_dress.Count} 条），"
                           + "无法解锁对应服饰");
                }
            }
            else if (item.Genre == CharacterCardTable.CardGenre)
            {
                // ② 角色卡 → CharacterCard.ModelId
                var card = _ctx.CharacterCards.Get(item.Genre, item.Detail, item.Particular, item.Level);
                if (card is not null)
                    Note(item.Detail, card.ModelId);
            }
        }

        // SuitCount（group 3 偏移 13）：客户端只用来展示「衣服数」
        int countFixed = 0;
        foreach (var girl in player.Girls.Where(g => GmUnlock.IsValidGirlId(g.Girlid)))
        {
            uint girlId = girl.Girlid;
            // SuitValue is permanent ownership, independent of the source item/card
            // remaining in the bag. Count each configured outfit once.
            int count = CountOwnedSuits(_dress, _ctx.CharacterCards, player, girlId);
            int taskId = GirlLoveFeature.VarTaskId(girlId, VarSuitCount);
            int group = GirlLoveFeature.VarGroup(girlId);
            if (player.GetTask(group, taskId) == count)
                continue;
            player.SetTask(group, taskId, count);
            countFixed++;
        }

        if (marked > 0 || countFixed > 0)
        {
            _ctx.Store.Save(player);
            Log.Info($"[session {session.Id}] 换装：新解锁服饰 {marked} 套"
                   + $"（SuitValue 置 {SuitNew} = 已获得未穿过）、修正 SuitCount {countFixed} 个少女"
                   + "；★ 不置位的服饰不会出现在换装列表里（UI_GirlClothes.lua:847）");
        }
    }

    /// <summary>
    /// 供别的功能域调用：发放皮肤道具后立刻解锁对应服饰并推给客户端。
    ///
    /// ★ 为什么需要它：<see cref="SyncOwnedSuits"/> 只在**登录**时跑，
    /// 而十面妆造买皮肤是**运行期**发生的 ⇒ 不推的话玩家要重登才能看到新衣服。
    /// 静态 + 显式传表，与 <see cref="GirlLoveFeature.AddExp(GirlLoveTable, Player, uint, int, out GirlLoveFeature.GirlExpDelta)"/>
    /// 同一套理由（配置表只读，多份实例无副作用；本项目有意不提供 feature 实例查找）。
    /// </summary>
    /// <returns>解锁的 (girlId, modelId) 列表（可能为空）。</returns>
    internal static List<(uint GirlId, int ModelId)> GrantSuitsForItems(
        GirlDressTable dress, ClientSession session, Player player, IEnumerable<Item> items)
    {
        var granted = new List<(uint, int)>();
        var updates = new List<KeyValuePair<uint, int>>();

        foreach (var item in items)
        {
            if (item.Genre != GirlDressTable.SuitItemGenre || item.Count == 0)
                continue;
            var suit = dress.SuitForItem(item.Genre, item.Detail, item.Particular, item.Level);
            if (suit is null || player.GetGirl(suit.GirlId) is null)
                continue;
            if (GetSuitValue(player, suit.GirlId, suit.ModelId) != SuitNone)
                continue;

            SetSuitValue(player, suit.GirlId, suit.ModelId, SuitNew, updates);
            granted.Add((suit.GirlId, suit.ModelId));
        }

        if (updates.Count > 0)
        {
            GameNotify.PushTaskValues(session, updates);
            Log.Info($"[session {session.Id}] 换装：皮肤道具解锁 {granted.Count} 套服饰 → "
                   + string.Join('、', granted.Select(t => $"少女{t.Item1} model={t.Item2}")));
        }
        return granted;
    }

    internal static int CountOwnedSuits(GirlDressTable dress, CharacterCardTable cards, Player player, uint girlId)
        => cards.ForGirl(girlId).Select(c => c.ModelId)
            .Concat(dress.All.Where(s => s.GirlId == girlId).Select(s => s.ModelId))
            .Where(m => m > 0).DistinctBy(GmUnlock.FixModelId)
            .Count(m => GetSuitValue(player, girlId, m) > 0);

    // Shared by reward paths: source items can unlock once, permanent flags drive count.
    internal static List<KeyValuePair<uint, int>> SyncRewardSuits(
        GirlDressTable dress, CharacterCardTable cards, Player player, uint girlId)
    {
        var updates = new List<KeyValuePair<uint, int>>();
        if (player.GetGirl(girlId) is null || !GmUnlock.IsValidGirlId(girlId)) return updates;
        foreach (var item in player.Items.Where(i => i.Count > 0))
        {
            int model = 0;
            if (item.Genre == CharacterCardTable.CardGenre && item.Detail == girlId)
                model = cards.Get(item.Genre, item.Detail, item.Particular, item.Level)?.ModelId ?? 0;
            else if (item.Genre == GirlDressTable.SuitItemGenre)
            {
                var suit = dress.SuitForItem(item.Genre, item.Detail, item.Particular, item.Level);
                if (suit?.GirlId == girlId) model = suit.ModelId;
            }
            if (model > 0 && GetSuitValue(player, girlId, model) == SuitNone)
                SetSuitValue(player, girlId, model, SuitNew, updates);
        }
        int group = GirlLoveFeature.VarGroup(girlId);
        int task = GirlLoveFeature.VarTaskId(girlId, VarSuitCount);
        int count = CountOwnedSuits(dress, cards, player, girlId);
        if (player.GetTask(group, task) != count)
        {
            player.SetTask(group, task, count);
            updates.Add(new(Player.ComposeTaskId(group, task), count));
        }
        return updates;
    }

    // ================= 任务值编码 =================

    /// <summary>
    /// 服饰标记所在 group：普通少女 <b>4</b>、联动少女 <b>91</b>
    /// （<c>GirlCommon:GetExtGroup</c> 的 <c>4 → 91</c>，`GirlCommon.lua:1495..1506`）。
    /// </summary>
    internal static int SuitGroupOf(uint girlId)
        => GirlLoveTable.IsLinkGirl(girlId) ? SuitGroupLink : SuitGroup;

    /// <summary>
    /// 服饰标记的 taskId：<c>(girlId - 1) * 2000 + FixModelId(modelId)</c>。
    /// ★ 两层都不能省：联动少女先减 200（`GirlCommon.lua:1516`），
    /// <c>ModelId ≥ 2000</c> 要过 <see cref="GmUnlock.FixModelId"/>（例 7001 → 1711）。
    /// </summary>
    internal static int SuitTaskId(uint girlId, int modelId)
    {
        uint id = GirlLoveTable.IsLinkGirl(girlId) ? girlId - GirlLoveTable.LinkGirlId : girlId;
        return ((int)id - 1) * GmUnlock.GirlTaskStride + GmUnlock.FixModelId(modelId);
    }

    /// <summary>读服饰三态（0 未获得 / 1 未穿过 / 2 已穿过）。</summary>
    internal static int GetSuitValue(Player player, uint girlId, int modelId)
        => player.GetTask(SuitGroupOf(girlId), SuitTaskId(girlId, modelId));

    /// <summary>
    /// 写服饰三态。<paramref name="updates"/> 非 null 时把变更累积进去交调用方推 pf 1026；
    /// 为 null 表示「调用方会走登录的全量推送」（<see cref="SyncOwnedSuits"/>）。
    /// </summary>
    internal static void SetSuitValue(Player player, uint girlId, int modelId, int value,
                                      List<KeyValuePair<uint, int>>? updates)
    {
        int group = SuitGroupOf(girlId);
        int taskId = SuitTaskId(girlId, modelId);
        player.SetTask(group, taskId, value);
        updates?.Add(new KeyValuePair<uint, int>(Player.ComposeTaskId(group, taskId), value));
    }

    /// <summary>读少女属性（group 3 / 联动 90），复用好感度域的编码。</summary>
    private static int GetVar(Player player, uint girlId, int off)
        => player.GetTask(GirlLoveFeature.VarGroup(girlId), GirlLoveFeature.VarTaskId(girlId, off));

    /// <summary>写少女属性并立刻推 pf 1026。</summary>
    private static void SetVar(ClientSession session, Player player, uint girlId, int off, int value)
    {
        int group = GirlLoveFeature.VarGroup(girlId);
        int taskId = GirlLoveFeature.VarTaskId(girlId, off);
        player.SetTask(group, taskId, value);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(group, taskId), value),
        });
    }

    // ================= 回包 =================

    /// <summary>成功回包（少女养成族）：★ 原样回 sCmd、**绝不带 <c>nError</c>**。</summary>
    private void Ok(ClientSession session, string sCmd, JsonObject payload)
    {
        payload["sCmd"] = sCmd;
        _ctx.LuaCalls.CallClientFunction(session, Function, payload);
    }

    /// <summary>
    /// 失败回包（少女养成族）：客户端只拿到 <c>nError</c> 这个数字
    /// （`GirlLogic.lua:22`），而本域两个 Error handler 一个不存在
    /// （<c>OnSetMainGirlError</c>）、一个是空函数（<c>OnChangeClothError</c>）
    /// ⇒ **玩家零反馈，原因只能靠服务端日志**。
    /// </summary>
    private void Fail(ClientSession session, string sCmd, int code)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["sCmd"] = sCmd,
            ["nError"] = code,
        });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;
}
