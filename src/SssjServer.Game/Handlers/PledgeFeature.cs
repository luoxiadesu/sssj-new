using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 誓约系统（<c>Pldge</c>）—— 少女的「阶段任务 → 进阶」养成线。
///
/// <para><b>0. ★★★ 本域为什么能做（第十五轮推翻的旧结论）</b></para>
/// <see cref="MainCityFeature"/> 旧注释称「打开开关，玩家点进去就会加载**本服没实现的**誓约地图」，
/// 据此把入口写死关闭。**本轮四条判据全部证伪**：
/// <code>
/// 真机 CS.Game.Map.Find(11001)          → Game.Map+Info（有效），scene = "Promise"
/// adb ls .../files/map/ | grep promise   → mappromise_prefab.u          （设备上有）
/// ls work/assets-serve/map/              → mappromise_prefab.u          （资源站有）
/// grep -c mappromise version.data        → 2                            （清单已登记）
/// </code>
/// ★ 当初误判的原因：用了**不存在的 API** <c>Map.GetInfo</c>（正确的是 <c>Map.Find</c>，
/// <c>dump.cs:555419</c>）拿到 nil。
/// ⇒ <b>判「某数据不存在」之前，先用已知存在的同类值验证查询手段本身</b>
/// （我是拿 map 2/6/7 对照才发现是方法名错，不是数据缺失）。
/// 完整取证见 <c>work/pledge-forensics.md</c>。
///
/// <para><b>1. 命令面：7 条里只有 4 条有活跃上行点</b></para>
/// <code>
/// 232 Pldge_ReqOpenGirls   GirlPromise.lua:541  {}                      ✅
/// 233 Pldge_NewGirl        GirlPromise.lua:589  {girlid}                ✅
/// 234 Pldge_SetTaskFinish  GirlPromise.lua:610  {girlid, taskid}        ✅
/// 235 Pldge_StepUpGirl     GirlPromise.lua:627  {girlid}                ✅
/// 236 Pldge_PushGirlData   0 处  ⇒ 纯下行通道，无上行
/// 237 Pldge_ReqOneGirl     0 处  ⇒ 客户端永不发送
/// 238 Pldge_ComplateTask   0 处  ⇒ 客户端永不发送
/// </code>
/// （判据：<c>grep -rn "LuaCall.&lt;名&gt;" extracted/lua/ | grep -v LuaCall.lua</c>）
/// 另接管 <b>231</b> <c>Pldge_ReqGirls</c>（原在 <see cref="MainCityFeature"/> 回空列表）。
///
/// <para><b>2. ★★★ 第八种错误约定：<c>Err</c></b></para>
/// 234 的客户端消费点是
/// <code>
/// GirlPromise.lua:613  if tbParam.Err == nil then ... UI.Call("UI_PromiseTarget","CompleteOneTask", tbParam.id)
/// </code>
/// ⇒ 失败键是 **<c>Err</c>**（首字母大写、**判 nil** 而不是判真值），
/// 成功时**绝不能带**这个键。本项目此前已见 7 种约定，这是**第 8 种**
/// （既不是 <c>nError</c>、不是大写 <c>Error</c>、也不是 <c>err</c>）。
/// ⇒ 再次印证 AGENTS.md §5 那条「**逐条命令确认错误约定，不要整域一刀切**」。
///
/// <para><b>3. 回包形状（逐条取证消费点）</b></para>
/// <list type="bullet">
///   <item><b>232</b> → <c>{OpenGirlList = [id,...]}</c>（<c>GirlPromise.lua:550</c> 取 <c>.OpenGirlList</c> 键）。
///     ★ <c>LuaCall.lua:3023</c> 的原服注释写「返回 <c>[1,2,3]</c>」裸数组，
///     **与真实消费点矛盾** ⇒ 以消费点为准。</item>
///   <item><b>231</b> → **数组**，每项 <c>{girlid, step, cardguid, taskdatas:[{id,count,status}]}</c>
///     （<c>GirlPromise.lua:573</c> <c>GirlList = tbParam</c>）。</item>
///   <item><b>233</b> → **单个少女对象**（同上元素形状）；<c>GirlPromise.lua:598</c> 无保护读 <c>girlid</c>。</item>
///   <item><b>234</b> → <c>{id, count, status}</c>；失败 <c>{Err = 1}</c>。</item>
///   <item><b>235</b> → 单个少女对象；<c>:630</c> 只判 <c>not tbParam</c> 后就重拉 231，
///     ⇒ 内容不被读，但**必须非 nil**。</item>
/// </list>
///
/// <para><b>4. 业务规则</b></para>
/// <list type="bullet">
///   <item>step 0 是「未激活」占位；<c>step0.tasklist[1]</c> 是**激活消耗**任务
///     （实测各少女都是 <c>ReduceItem count=10 gdpl=9-16-1-1</c> = 誓约碎片 ×10）。</item>
///   <item>阶段内任务全部 <c>status=2</c>（已完成）才允许 235 进阶。</item>
///   <item>16 个少女、每人 7~8 个 step、693 条任务、16 种 <c>strtype</c>。</item>
/// </list>
///
/// <para><b>5. 任务进度的实现范围</b></para>
/// ReduceItem/ReduceMoney 真实扣除；FriendLevel 按少女好感度等级校验，
/// CardCount 按对应少女达到强化等级的不同装扮校验，231 同步这两类实际进度。
/// 送礼、摸头、特训按成功事件累计；看板娘、咖啡馆、休息任务按实际分配计时。
/// 其余未恢复事件拒绝未达标领取。
/// </summary>
public sealed class PledgeFeature : IGameFeature
{
    /// <summary>231 <c>Pldge_ReqGirls</c>：拥有的誓约少女列表。</summary>
    public const int CmdReqGirls = 231;
    public const int CmdSetSimpleStep = 228;

    /// <summary>232 <c>Pldge_ReqOpenGirls</c>：开放的誓约少女 id 列表。</summary>
    public const int CmdReqOpenGirls = 232;

    /// <summary>233 <c>Pldge_NewGirl</c>：激活某誓约少女。</summary>
    public const int CmdNewGirl = 233;

    /// <summary>234 <c>Pldge_SetTaskFinish</c>：手动点击完成任务。</summary>
    public const int CmdSetTaskFinish = 234;

    /// <summary>235 <c>Pldge_StepUpGirl</c>：阶段进阶。</summary>
    public const int CmdStepUpGirl = 235;

    /// <summary>239 第7阶段按已完成任务数领取配置奖励；失败字段为 errid。</summary>
    public const int CmdGetReward = 239;

    /// <summary>存档键（<c>Player.Extra</c>）。</summary>
    public const string ExtraKey = "pledge";

    /// <summary>任务状态：未完成。</summary>
    public const int TaskStatusDoing = 1;

    /// <summary>任务状态：已完成。★ 客户端 <c>UI_PromiseTarget</c> 按此判勾选。</summary>
    public const int TaskStatusDone = 2;

    /// <summary>「消耗道具」类任务的 <c>strtype</c>（唯一会真实扣物品的类型）。</summary>
    public const string TypeReduceItem = "ReduceItem";

    private GameFeatureContext _ctx = null!;
    private PledgeTable _table = null!;

    public string Name => "誓约（LuaCall 228/231/232/233/234/235/239）";

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        CmdReqGirls, CmdReqOpenGirls, CmdNewGirl, CmdSetTaskFinish, CmdStepUpGirl, CmdSetSimpleStep, CmdGetReward,
    };

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        // ★ 231 覆盖 MainCityFeature 的空列表实现（那里只回 []，本域接真实数据）
        ctx.MapLuaCall(CmdReqGirls, OnReqGirls);
        ctx.MapLuaCall(CmdSetSimpleStep, OnSetSimpleStep);
        ctx.MapLuaCall(CmdReqOpenGirls, OnReqOpenGirls);
        ctx.MapLuaCall(CmdNewGirl, OnNewGirl);
        ctx.MapLuaCall(CmdSetTaskFinish, OnSetTaskFinish);
        ctx.MapLuaCall(CmdStepUpGirl, OnStepUpGirl);
        ctx.MapLuaCall(CmdGetReward, OnGetReward);
        ctx.OnPlayerPrepare((session, player) =>
            ctx.PledgeTimes.SaveRefresh(ctx.Store, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    // ================================================================ 232

    /// <summary>
    /// 232 开放少女列表 → <c>{OpenGirlList = [id,...]}</c>。
    ///
    /// ★ 名单由**服务端**说了算：客户端本地的 <c>Pldge:GetOpenGirlList()</c> 因
    /// <c>isopen</c> 列是日期字符串而恒返回空（<see cref="PledgeStepRow.OpenTimeRaw"/>），
    /// 但界面读的是本回包写入的 <c>GirlPromise.OpenGirlList</c>（<c>GirlPromise.lua:550</c>）。
    /// </summary>
    internal Task OnReqOpenGirls(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player)
            return Task.CompletedTask;

        var ids = _table.OpenGirlIds(DateTime.Now);
        var arr = new JsonArray();
        foreach (int id in ids)
            arr.Add(id);

        Log.Info($"[session {session.Id}] Pldge_ReqOpenGirls(232) → 开放少女 {ids.Count} 位：" +
                 $"{string.Join(",", ids)}");

        // ★ 必须是「对象包数组」，不是裸数组（GirlPromise.lua:550 取 .OpenGirlList 键）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["OpenGirlList"] = arr });
        return Task.CompletedTask;
    }

    // ================================================================ 231

    /// <summary>
    /// 231 拥有的誓约少女列表 → **数组**，每项 <c>{girlid, step, cardguid, taskdatas}</c>。
    /// </summary>
    internal Task OnReqGirls(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        lock (player) return ReqGirlsLocked(session, cmd, player);
    }

    private Task ReqGirlsLocked(ClientSession session, int cmd, Player player)
    {
        try { _ctx.PledgeTimes.SaveRefresh(_ctx.Store, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
        catch (Exception error) { Log.Warn($"[pledge] 计时未能保存，返回原有进度：{error.Message}"); }

        var state = LoadState(player);
        var arr = new JsonArray();
        foreach (var g in state.Girls.Values.OrderBy(g => g.GirlId))
        {
            foreach (var task in g.Tasks.Where(t => t.Status != TaskStatusDone))
            {
                var cfg = _table.Task(task.Id);
                if (cfg is not null && CurrentOwnedCount(player, cfg) is int count)
                    task.Count = Math.Min(Math.Max(0, cfg.Count), count);
            }
            arr.Add(BuildGirl(g));
        }

        Log.Info($"[session {session.Id}] Pldge_ReqGirls(231) → 已激活 {state.Girls.Count} 位誓约少女" +
                 (state.Girls.Count > 0
                     ? $"（{string.Join(",", state.Girls.Values.OrderBy(g => g.GirlId).Select(g => $"girl{g.GirlId}:step{g.Step}"))}）"
                     : "（新号为空）"));

        _ctx.LuaCalls.CallClient(session, cmd, arr);
        return Task.CompletedTask;
    }

    // ================================================================ 233

    /// <summary>
    /// 233 激活某誓约少女。上行 <c>{girlid}</c>。
    ///
    /// 校验链：少女在开放名单里 → 未激活过 → step0 的激活任务材料够 → 扣材料 → 建档。
    /// ★ 回**单个少女对象**（<c>GirlPromise.lua:599</c> <c>GirlList[girlID] = tbParam</c>）。
    /// </summary>
    internal Task OnNewGirl(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (!RefreshTimeOrFail(session, cmd, player)) return Task.CompletedTask;
            return NewGirlLocked(session, cmd, param);
        }
    }

    private Task NewGirlLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int girlId = ReadInt(param, "girlid");
        if (girlId <= 0)
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) girlid 非法：{girlId}，不回包");
            return Task.CompletedTask;
        }

        if (!_table.OpenGirlIds(DateTime.Now).Contains(girlId))
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) 少女 {girlId} 不在开放名单，拒绝");
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (state.Girls.ContainsKey(girlId))
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) 少女 {girlId} 已激活过，拒绝");
            return Task.CompletedTask;
        }

        // 激活消耗 = step0 的第一个任务（实测都是 ReduceItem 誓约碎片 ×10）
        var actTask = _table.ActivateTask(girlId);
        if (actTask is null || actTask.GirlId != girlId || actTask.StrType != TypeReduceItem
            || actTask.ReduceGdpl.Count != 4 || actTask.Count < 0) return Task.CompletedTask;
        var snapshot = new PledgeMutationSnapshot(player);
        if (!TryConsume(player, actTask, out var consumed, out string why))
        {
            Log.Warn($"[session {session.Id}] Pldge_NewGirl(233) 少女 {girlId} 激活材料不足：{why}，拒绝");
            return Task.CompletedTask;
        }

        var girl = new PledgeGirlState { GirlId = girlId, Step = 1 };
        ResetTasks(girl);
        state.Girls[girlId] = girl;
        if (!TrySaveState(player, state, snapshot)) return Task.CompletedTask;
        if (consumed.Count > 0) GameNotify.PushItems(session, consumed);

        Log.Info($"[session {session.Id}] Pldge_NewGirl(233) 激活誓约少女 {girlId}" +
                 $"（step=1、任务 {girl.Tasks.Count} 条" +
                 (actTask is not null && actTask.ReduceGdpl.Count >= 4
                     ? $"、消耗 ({string.Join(",", actTask.ReduceGdpl)})×{actTask.Count}"
                     : "") + "）");

        _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
        return Task.CompletedTask;
    }

    // ================================================================ 234

    /// <summary>
    /// 234 手动点击完成任务。上行 <c>{girlid, taskid}</c>。
    ///
    /// ★★★ 错误约定是**第八种**：失败回 <c>{Err = 1}</c>，成功**绝不能带 <c>Err</c>**
    /// （<c>GirlPromise.lua:613</c> <c>if tbParam.Err == nil then</c>）。
    /// 成功回 <c>{id, count, status}</c>（<c>:614</c> 读 <c>tbParam.id</c>）。
    /// </summary>
    internal Task OnSetTaskFinish(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (!RefreshTimeOrFail(session, cmd, player)) return Task.CompletedTask;
            return SetTaskFinishLocked(session, cmd, param);
        }
    }

    private Task SetTaskFinishLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int girlId = ReadInt(param, "girlid");
        int taskId = ReadInt(param, "taskid");

        var state = LoadState(player);
        if (!state.Girls.TryGetValue(girlId, out var girl))
        {
            Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 少女 {girlId} 未激活 → Err");
            Fail(session, cmd);
            return Task.CompletedTask;
        }

        var task = girl.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
        {
            Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 任务 {taskId} 不在少女 {girlId} " +
                     $"当前阶段（step={girl.Step}）的任务列表里 → Err");
            Fail(session, cmd);
            return Task.CompletedTask;
        }

        if (task.Status == TaskStatusDone)
        {
            Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 任务 {taskId} 已完成过 → Err");
            Fail(session, cmd);
            return Task.CompletedTask;
        }

        var cfg = _table.Task(taskId);
        if (cfg is null || cfg.Count < 0 || cfg.GirlId != girlId
            || _table.Step(girlId, girl.Step)?.TaskList.Contains(taskId) != true)
        {
            Fail(session, cmd);
            return Task.CompletedTask;
        }
        if (CurrentOwnedCount(player, cfg) is int owned && owned < cfg.Count)
        {
            Fail(session, cmd);
            return Task.CompletedTask;
        }
        // Event/time progress must come from server actions, never from this claim request.
        if (CurrentOwnedCount(player, cfg) is null
            && cfg.StrType is not (TypeReduceItem or "ReduceMoney") && task.Count < cfg.Count)
        {
            Fail(session, cmd);
            return Task.CompletedTask;
        }
        if (cfg.StrType == TypeReduceItem && cfg.ReduceGdpl.Count != 4)
        {
            Fail(session, cmd);
            return Task.CompletedTask;
        }
        var snapshot = new PledgeMutationSnapshot(player);
        var consumed = new List<Item>();
        int changedMoney = 0;
        if (cfg.StrType == "ReduceMoney")
        {
            if (!int.TryParse(cfg.Para1, out int moneyId) || moneyId <= 0 || player.GetMoney(moneyId) < cfg.Count)
            {
                Fail(session, cmd);
                return Task.CompletedTask;
            }
            player.SetMoney(moneyId, player.GetMoney(moneyId) - cfg.Count);
            changedMoney = moneyId;
        }
        if (cfg.StrType == TypeReduceItem)
        {
            if (!TryConsume(player, cfg, out consumed, out string why))
            {
                Log.Warn($"[session {session.Id}] Pldge_SetTaskFinish(234) 任务 {taskId} 材料不足：{why} → Err");
                Fail(session, cmd);
                return Task.CompletedTask;
            }
        }

        task.Count = cfg.Count;
        task.Status = TaskStatusDone;
        if (!TrySaveState(player, state, snapshot)) { Fail(session, cmd); return Task.CompletedTask; }
        if (changedMoney > 0) GameNotify.PushMoney(session, player, changedMoney);
        if (consumed.Count > 0) GameNotify.PushItems(session, consumed);

        int done = girl.Tasks.Count(t => t.Status == TaskStatusDone);
        Log.Info($"[session {session.Id}] Pldge_SetTaskFinish(234) 少女 {girlId} 任务 {taskId} " +
                 $"({cfg?.StrType ?? "?"}) 完成 ⇒ 本阶段进度 {done}/{girl.Tasks.Count}");

        // ★ 成功回包绝不带 Err
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["id"] = task.Id,
            ["count"] = task.Count,
            ["status"] = task.Status,
        });
        return Task.CompletedTask;
    }

    // ================================================================ 235

    /// <summary>
    /// 235 阶段进阶。上行 <c>{girlid}</c>。
    ///
    /// 前置：本阶段任务**全部** <c>status=2</c>；且未到最大阶段。
    /// ★ 回包内容不被客户端读（<c>GirlPromise.lua:630</c> 只判非 nil 后重拉 231），
    /// 但**必须非 nil**，否则界面不刷新。
    /// </summary>
    internal Task OnStepUpGirl(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (!RefreshTimeOrFail(session, cmd, player)) return Task.CompletedTask;
            return StepUpGirlLocked(session, cmd, param);
        }
    }

    private Task StepUpGirlLocked(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int girlId = ReadInt(param, "girlid");
        // Simple pledges advance only after their story callback (228), not an empty task list.
        if (_table.IsSimple(girlId)) { Fail(session, cmd); return Task.CompletedTask; }
        var state = LoadState(player);
        if (!state.Girls.TryGetValue(girlId, out var girl))
        {
            Log.Warn($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 未激活，不回包");
            return Task.CompletedTask;
        }

        var stepCfg = _table.Step(girlId, girl.Step);
        if (stepCfg is null || girl.Step is < 1 or > 6) return Task.CompletedTask;
        var required = stepCfg.TaskList.Distinct().ToArray();
        int unfinished = required.Count(id => !girl.Tasks.Any(t => t.Id == id && t.Status == TaskStatusDone));
        if (required.Length == 0 || girl.Tasks.Count != required.Length || unfinished > 0
            || required.Any(id => _table.Task(id)?.GirlId != girlId))
        {
            Log.Warn($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 还有 {unfinished} " +
                     $"条任务未完成（step={girl.Step}），拒绝进阶");
            return Task.CompletedTask;
        }

        int max = _table.MaxStep(girlId);
        if (girl.Step > max || girl.RewardStep > max)
        {
            Log.Warn($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 已是最高阶段 " +
                     $"{girl.Step}/{max}，拒绝");
            return Task.CompletedTask;
        }

        // Final stage six must still settle its card and story flag even when
        // this girl has no stage seven. A retry acknowledges the saved settlement.
        if (girl.RewardStep >= girl.Step)
        {
            _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
            return Task.CompletedTask;
        }
        int from = girl.Step;
        var snapshot = new PledgeMutationSnapshot(player);
        PledgeCardRewards.Result reward;
        try
        {
            reward = PledgeCardRewards.Apply(_ctx, player, girl, stepCfg);
            girl.RewardStep = from;
            if (girl.Step < max) { girl.Step++; ResetTasks(girl); }
            if (!TrySaveState(player, state, snapshot)) return Task.CompletedTask;
        }
        catch (Exception error)
        {
            snapshot.Restore(player);
            Log.Warn($"[pledge] 阶段卡结算失败，保留当前阶段：{error.Message}");
            return Task.CompletedTask;
        }
        if (reward.Removed is not null) GameNotify.PushItemsRemoved(session, [reward.Removed]);
        if (reward.Rewards.IsEmpty) GameNotify.PushItems(session, reward.Rewards.ChangedItems);
        else GameNotify.PushRewards(session, player, reward.Rewards);
        foreach (var formation in reward.Formations) GameNotify.PushFormation(session, formation);
        Log.Info($"[session {session.Id}] Pldge_StepUpGirl(235) 少女 {girlId} 进阶 step {from} → {girl.Step}" +
                 $"（新阶段任务 {girl.Tasks.Count} 条" +
                 (stepCfg is not null && stepCfg.CardGdpl.Count >= 4
                     ? $"、阶段卡 ({string.Join(",", stepCfg.CardGdpl)}) lv{stepCfg.CardLv}"
                     : "") + "）");

        _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
        return Task.CompletedTask;
    }

    internal Task OnSetSimpleStep(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            int girlId = ReadInt(param, "girlId"), wanted = ReadInt(param, "step");
            var state = LoadState(player);
            if (!_table.IsSimple(girlId) || !state.Girls.TryGetValue(girlId, out var girl)
                || !_table.OpenGirlIds(DateTime.Now).Contains(girlId))
            {
                Fail(session, cmd);
                return Task.CompletedTask;
            }
            int max = _table.MaxStep(girlId);
            // The client sends story index + 1: max+1 means all six stories have been read.
            if (wanted == girl.Step && wanted > 1 && wanted <= max + 1)
            {
                _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
                return Task.CompletedTask;
            }
            if (girl.Step < 1 || girl.Step > max || wanted != girl.Step + 1
                || string.IsNullOrWhiteSpace(_table.Step(girlId, girl.Step)?.SimpleCartoon))
            {
                Fail(session, cmd);
                return Task.CompletedTask;
            }
            var snapshot = new PledgeMutationSnapshot(player);
            girl.RewardStep = Math.Max(girl.RewardStep, girl.Step);
            girl.Step = wanted;
            ResetTasks(girl);
            if (!TrySaveState(player, state, snapshot)) { Fail(session, cmd); return Task.CompletedTask; }
            _ctx.LuaCalls.CallClient(session, cmd, BuildGirl(girl));
        }
        return Task.CompletedTask;
    }

    // ================================================================ 239

    internal Task OnGetReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            int girlId = ReadInt(param, "girlid"), step = ReadInt(param, "step");
            void Reply(int error, int completed = 0, RewardGrantResult? granted = null)
                => _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
                {
                    ["girlid"] = girlId, ["step"] = step, ["errid"] = error,
                    ["ncompletedtasknum"] = completed,
                    ["tbaward"] = JsonSerializer.SerializeToNode(granted?.ToAwardTuples() ?? Array.Empty<int[]>()),
                });

            var state = LoadState(player);
            var cfg = _table.Step(girlId, step);
            // Installed rewards belong to the current final stage. Never infer
            // completion of an unreached stage or of a simple pledge's stories.
            if (step < 7 || cfg is null || cfg.Rewards.Count == 0 || _table.IsSimple(girlId)
                || !state.Girls.TryGetValue(girlId, out var girl) || girl.Step != step)
            {
                Reply(1);
                return Task.CompletedTask;
            }
            var taskIds = cfg.TaskList.Distinct().ToArray();
            if (taskIds.Length == 0 || cfg.Rewards.Any(r => r.Length != 6
                || r.Take(4).Any(v => v <= 0) || r[4] <= 0 || r[5] < 1 || r[5] > taskIds.Length)
                || taskIds.Any(id => _table.Task(id)?.GirlId != girlId))
            {
                Reply(1);
                return Task.CompletedTask;
            }
            int completed = taskIds.Count(id => girl.Tasks.Any(t => t.Id == id && t.Status == TaskStatusDone));
            int claimed = girl.RewardCount(step);
            if (claimed < 0 || claimed > taskIds.Length || completed < claimed)
            {
                Reply(1);
                return Task.CompletedTask;
            }
            // A lost response may be retried; acknowledge the saved watermark
            // without granting anything again or lowering the client's watermark.
            if (completed == claimed)
            {
                Reply(0, claimed);
                return Task.CompletedTask;
            }
            var snapshot = new ChallengeMutationSnapshot(player);
            RewardGrantResult rewards;
            try
            {
                rewards = _ctx.Rewards.Grant(player, cfg.Rewards
                    .Where(r => r[5] > claimed && r[5] <= completed)
                    .Select(r => r.Take(5).ToArray()), RewardSource.Extra);
                girl.SetRewardCount(step, completed);
                if (completed == taskIds.Length) girl.RewardStep = Math.Max(girl.RewardStep, step);
                player.Extra[ExtraKey] = state.ToJson();
                _ctx.Store.SaveRequired(player);
            }
            catch (Exception error)
            {
                snapshot.Restore(player);
                Log.Warn($"[pledge] 第7阶段奖励未保存，回滚本次领取：{error.Message}");
                Reply(1);
                return Task.CompletedTask;
            }
            GameNotify.PushRewards(session, player, rewards);
            Reply(0, completed, rewards);
        }
        return Task.CompletedTask;
    }

    // ================================================================ 内部

    // These are current ownership conditions, not accumulated events; recount does not
    // discard affection or already owned outfits when entering a new pledge stage.
    internal static int? CurrentOwnedCount(Player player, PledgeTaskRow cfg)
    {
        if (cfg.StrType == "FriendLevel")
            return (int)Math.Min(int.MaxValue, player.GetGirl((uint)cfg.GirlId)?.Level ?? 0);
        if (cfg.StrType == "CardCount")
        {
            if (!int.TryParse(cfg.Para1, out int minLevel) || minLevel < 0) return 0;
            return player.Items.Where(i => i.Genre == 1 && i.Detail == cfg.GirlId
                    && i.Count > 0 && i.EnhanceLevel >= minLevel)
                .Select(i => (i.Detail, i.Particular, i.Level)).Distinct().Count();
        }
        return null;
    }

    /// <summary>Record only verified successful actions in the current stage; the caller saves atomically with its action.</summary>
    internal static void RecordAction(PledgeTable table, Player player, int girlId, string type, int amount = 1, int parameter = 0)
    {
        if (amount <= 0 || type is not ("TouchHead" or "GiftCount" or "Training")) return;
        lock (player)
        {
            var state = LoadStateStatic(player);
            if (!state.Girls.TryGetValue(girlId, out var girl)) return;
            bool changed = false;
            foreach (var task in girl.Tasks.Where(t => t.Status != TaskStatusDone))
            {
                var cfg = table.Task(task.Id);
                if (cfg is null || cfg.GirlId != girlId || cfg.StrType != type || cfg.Count < 0) continue;
                if (type == "GiftCount" && (!int.TryParse(cfg.Para1, out int minimum) || parameter < minimum)) continue;
                if (type == "Training" && (!int.TryParse(cfg.Para1, out int training) || (training != -1 && training != parameter))) continue;
                // All live action tasks in the final table use recount=1. Do not guess lifetime history.
                if (cfg.Recount != 1) continue;
                task.Count = (int)Math.Min(cfg.Count, Math.Max(0L, task.Count) + amount);
                changed = true;
            }
            if (changed) player.Extra[ExtraKey] = state.ToJson();
        }
    }

    /// <summary>失败回包：<b>只带 <c>Err</c></b>（第八种错误约定，见类注释 §2）。</summary>
    private void Fail(ClientSession session, int cmd)
        => _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["Err"] = 1 });

    /// <summary>
    /// 造一个誓约少女的回包对象：<c>{girlid, step, cardguid, taskdatas:[{id,count,status}]}</c>
    /// （形状取证 <c>LuaCall.lua:3000</c> 的原服注释 + <c>GirlPromise.lua:573/599</c>）。
    /// </summary>
    private JsonObject BuildGirl(PledgeGirlState g)
    {
        var tasks = new JsonArray();
        foreach (var t in g.Tasks)
        {
            tasks.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["count"] = t.Count,
                ["status"] = t.Status,
            });
        }
        return new JsonObject
        {
            ["girlid"] = g.GirlId,
            ["step"] = g.Step,
            ["cardguid"] = g.CardGuid,
            ["rewardstep"] = g.RewardStep,
            ["taskdatas"] = tasks,
            ["rewardlist"] = g.BuildRewardList(_table.MaxStep(g.GirlId)),
        };
    }

    /// <summary>把少女的任务列表重置成「当前阶段」的那批（全部未完成）。</summary>
    private void ResetTasks(PledgeGirlState girl)
    {
        girl.Tasks.Clear();
        var cfg = _table.Step(girl.GirlId, girl.Step);
        if (cfg is null)
            return;
        foreach (int tid in cfg.TaskList)
        {
            girl.Tasks.Add(new PledgeTaskState
            {
                Id = tid,
                Count = 0,
                Status = TaskStatusDoing,
            });
        }
    }

    /// <summary>
    /// 扣除 <c>ReduceItem</c> 任务要求的材料。成功 true；不足 false 并给出原因。
    /// ★ 扣完要推 <b>pf 1031</b>（不是 1104）让背包界面刷新（AGENTS.md §5）。
    /// </summary>
    private static bool TryConsume(Player player, PledgeTaskRow cfg, out List<Item> touched, out string why)
    {
        touched = new List<Item>();
        why = "";
        var g = cfg.ReduceGdpl;
        if (g.Count != 4 || g.Any(v => v <= 0)) { why = "消耗配置非法"; return false; }
        uint gg = (uint)g[0], dd = (uint)g[1], pp = (uint)g[2], ll = (uint)g[3];
        if (cfg.Count < 0) { why = "消耗配置为负数"; return false; }
        int need = cfg.Count;
        if (need == 0) return true;

        long have = player.Items
            .Where(i => i.Genre == gg && i.Detail == dd && i.Particular == pp && i.Level == ll)
            .Sum(i => (long)i.Count);
        if (have < need)
        {
            why = $"({gg},{dd},{pp},{ll}) 需要 {need}、持有 {have}";
            return false;
        }

        int left = need;
        foreach (var it in player.Items
                     .Where(i => i.Genre == gg && i.Detail == dd && i.Particular == pp && i.Level == ll)
                     .ToList())
        {
            if (left <= 0)
                break;
            int take = (int)Math.Min(left, (long)it.Count);
            it.Count -= (uint)take;
            left -= take;
            touched.Add(it);
        }

        return true;
    }

    private static int ReadInt(JsonNode? param, string key)
    {
        // ★ JsonNode["k"] 对 JsonArray 会抛（AGENTS.md §5）⇒ 必须先判类型
        if (param is not JsonObject o || o[key] is not JsonValue v)
            return 0;
        if (v.TryGetValue<int>(out int n))
            return n;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out int m) ? m : 0;
    }

    // ---------------- 存档 ----------------

    internal PledgeState LoadState(Player player) => LoadStateStatic(player);

    internal static PledgeState LoadStateStatic(Player player)
    {
        player.Extra.TryGetValue(ExtraKey, out var node);
        return PledgeState.FromJson(node as JsonObject);
    }

    private bool TrySaveState(Player player, PledgeState state, PledgeMutationSnapshot snapshot)
    {
        try
        {
            player.Extra[ExtraKey] = state.ToJson();
            _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _ctx.Store.SaveRequired(player);
            return true;
        }
        catch (Exception error)
        {
            snapshot.Restore(player);
            Log.Warn($"[pledge] 操作未保存，恢复原有材料、任务和阶段卡：{error.Message}");
            return false;
        }
    }

    private bool RefreshTimeOrFail(ClientSession session, int cmd, Player player)
    {
        try
        {
            _ctx.PledgeTimes.SaveRefresh(_ctx.Store, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return true;
        }
        catch (Exception error)
        {
            Log.Warn($"[pledge] 计时未保存，拒绝本次任务操作：{error.Message}");
            // 233's client handler unconditionally indexes girlid; an Err-only
            // object is not a valid activation response. Preserve its silent rejection.
            if (cmd != CmdNewGirl) Fail(session, cmd);
            return false;
        }
    }
}
