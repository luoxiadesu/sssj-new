using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 少女养成 = **「特训」系统**（`sFunction = GirlLogic`，即编队/咖啡馆里让少女去练舞、瑜伽等）。
/// 完整取证见 `docs/05` §17.1..§17.4，这里只复述实现必须遵守的部分。
///
/// <para><b>0. ★★ 这是本项目的第五种路由/错误约定</b></para>
/// `S2CCall.lua:68` → `GirlLogic:Dispatcher(tbParam.sCmd, tbParam)`，
/// 而 Dispatcher（`GirlLogic.lua:18..42`）与前四族都不一样：
/// <code>
/// if string.find(sCmd, 'Notify') == 1 then GirlLogic[sCmd](…); return end  -- ① Notify 前缀走另一条路
/// Game.Blocking.Hide()                     -- ② ★ 解遮罩在 Dispatcher 里 ⇒ 不回包会一直卡着
/// sCmd = 'On' .. sCmd                      -- ③ ★ 客户端找 On&lt;sCmd&gt;，所以服务端**原样回 sCmd**
/// if tbParam.nError then                   -- ④ ★ 真值判断 ⇒ 成功绝不能带 nError（0 也算错）
///     sCmd = sCmd .. 'Error'               --    失败找 On&lt;sCmd&gt;Error
/// end
/// if GirlLogic[sCmd] then
///     if tbParam.nError then GirlLogic[sCmd](GirlLogic, tbParam.nError)   -- ⑤ ★ 失败只传错误码！
///     else GirlLogic[sCmd](GirlLogic, tbParam) end
/// end
/// </code>
/// ⇒ 失败时客户端**只拿到一个数字**，别的字段全丢，所以错误必须靠 `nError` 的**数值**区分
/// （例 `OnAddTrainPosError` 里 `nCode == 2` 才弹「栏位已满」）。
///
/// <para><b>1. ★★ `OnStartTrain` 只从回包拿 `nId`，其余全读客户端本地</b></para>
/// `GirlLogic.lua:98..108`：
/// <code>
/// function GirlLogic:OnStartTrain(tbParam)
///     local nGirlId = tbParam.nId                                  -- ← 回包只给这一个
///     …
///     local nPos     = GirlCommon:Get(nGirlId, 'TrainPos')          -- ← 从**少女属性**读
///     local nEndTime = GirlCommon:Get(nGirlId, 'TrainTime')
///     local nType    = math.floor(nPos / 10)
///     local nTotalTime = GirlCommon.tbConfig:GetTrainingConfig(nType).Time
///     NCafe.Character:StartTrain(nGirlId, nPos, nEndTime, nTotalTime)
/// </code>
/// ⇒ **必须先把属性写好并推 pf 1026，再回 `StartTrain`**，否则客户端读到 0、特训表现不出来。
///
/// <para><b>2. 少女属性在 group 3</b></para>
/// `GirlCommon:Get(nId, sKey)` → `GetGirlTaskEx(3, nId, GirlVarOffset[sKey])`（`GirlCommon.lua:90..98`），
/// 编码复用 <see cref="GmUnlock.GirlTaskId"/>、group 用 <see cref="GmUnlock.GirlVarGroup"/>。
/// 偏移（`GirlCommon.lua:30..56`）：<b>11 = TrainPos、12 = TrainTime、16 = TrainOutId</b>。
///
/// ★ `GirlCommon:Get` 开头有 `if not … IsInSelfCafe() then return 0`（`:92`）——
/// **不在自己咖啡馆时读不到任何少女属性**，所以特训表现只在 map 6 内生效，验收要先进咖啡馆。
///
/// <para><b>3. 两张配置表</b></para>
/// <see cref="TrainTable"/>：`TrainPos.txt`（外景+位置 → 类型）与
/// `TrainingGirl.txt`（时长/好感/奖励/体力消耗）。
/// </summary>
public sealed class GirlTrainFeature : IGameFeature
{
    /// <summary>下行入口（`S2CCall.lua:68` <c>S2C.GirlLogic</c>）。</summary>
    public const string Function = "GirlLogic";

    /// <summary>加特训栏位（`GirlLogic.lua:52`）。</summary>
    public const string CmdAddTrainPos = "AddTrainPos";

    /// <summary>开始特训（`:66`，上行 <c>{nId, nPos}</c>）。</summary>
    public const string CmdStartTrain = "StartTrain";

    /// <summary>多人特训（`:215`，上行 <c>{data:[{nId,nPos},…]}</c>）。</summary>
    public const string CmdStartManyTrain = "StartManyTrain";

    /// <summary>打断特训（`:274`，上行 <c>{nId}</c>）。</summary>
    public const string CmdInterruptTrain = "InterruptTrain";

    /// <summary>特训结算（`:300`，上行 <c>{nId}</c>）。</summary>
    public const string CmdEndTrain = "EndTrain";

    /// <summary>摸头（真机实测见过 <c>{"sCmd":"HeadTouched"}</c>）。</summary>
    public const string CmdHeadTouched = "HeadTouched";

    /// <summary>少女属性偏移：特训位置（`GirlCommon.lua:38`）。</summary>
    public const int VarTrainPos = 11;

    /// <summary>少女属性偏移：特训结束时间戳（`:39`）。</summary>
    public const int VarTrainTime = 12;

    /// <summary>少女属性偏移：开始特训时的外景 id（`:47`）。</summary>
    public const int VarTrainOutId = 16;

    /// <summary>
    /// 特训栏位数存在玩家级任务值 <c>(group 3, taskId 1)</c>。
    /// ★ 这个坐标是**服务端自定**的：`GirlCommon` 的偏移表从 7 起（7..16 + 100+ 段），
    /// 而 <see cref="GmUnlock.GirlTaskId"/> 的编码是 `(girlId-1)*stride + off`
    /// ⇒ girlId=1 时最小可用 taskId 是 `0*stride+7 = 7`，taskId 1..6 这一段没有少女占用，
    /// 拿来存玩家级的栏位数是安全的。
    /// </summary>
    public const int TrainPosCountTaskId = 1;

    /// <summary>默认特训栏位数（未取证，取一个保守值；客户端只在超限时弹提示）。</summary>
    public const int DefaultTrainPosCount = 2;

    /// <summary>栏位上限（`TrainingGirl.txt` 的 `MaxNum` 列现网是 3）。</summary>
    public const int MaxTrainPosCount = 3;

    /// <summary>错误码 2 = 栏位已满（`OnAddTrainPosError` 里 `nCode == 2` 弹 `UI_GirlGift.Tips1`）。</summary>
    public const int ErrPosFull = 2;

    /// <summary>错误码 1 = 通用失败（客户端只 print，不弹框）。</summary>
    public const int ErrGeneric = 1;

    private GameFeatureContext _ctx = null!;
    private PledgeTable _pledge = PledgeTable.Empty;
    private GirlMoodRecovery _mood = null!;
    private TrainTable _train = TrainTable.Empty;
    private HouseTable _trainHouse = HouseTable.Empty;
    private readonly HashSet<string> _trainThemes = new();
    private GirlLoveTable _headTouchLove = GirlLoveTable.Empty;
    private CafeTable _trainCafe = CafeTable.Empty;
    private MissionTable _headTouchMissions = MissionTable.Empty;
    internal const int HeadTouchLoveReward = 50;

    public string Name => "少女养成/特训（sFunction GirlLogic）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _pledge = PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _mood = new GirlMoodRecovery(Path.GetFullPath(ctx.Options.SettingDirectory));
        _train = TrainTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _trainHouse = HouseTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        var sorts = SettingTable.Load(Path.Combine(ctx.Options.SettingDirectory, "NCafeSort.txt"));
        if (sorts is not null)
            foreach (var sort in sorts.Rows)
            {
                var ignored = sorts.Str(sort, "EditorIgnore").Trim('[', ']').Split(',');
                // This restoration runs the Chinese client (GetLangType() == 1).
                if (!ignored.Any(v => v.Trim() is "1" or "-1"))
                    _trainThemes.Add(sorts.Str(sort, "Type"));
            }
        _headTouchLove = GirlLoveTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _trainCafe = CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _headTouchMissions = MissionTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        // ★ 本域持有 sFunction=GirlLogic 的**主 handler**。同一个 sFunction 还被
        // GirlLoveFeature（好感度/剧情）瓜分，它走 ctx.AddFunctionCoHandler 追加子命令
        // ——「协处理器先跑、都不认才落到这里」，所以下面 Unknown 的告警仍然可信。
        // ★★ 两边都用 MapFunction 会互相覆盖（后装配者把前者整域吃掉），
        //    见 LuaCallRouter.AddFunctionCoHandler 的注释。
        ctx.MapFunction(Function, OnCommand);
        ctx.OnPlayerPrepare((session, player) =>
        {
            if (RefreshHeadTouches(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
                ctx.Store.Save(player);
        });
    }

    private Task OnCommand(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        string sCmd = param?["sCmd"]?.ToString() ?? "";
        lock (player) return sCmd switch
        {
            CmdAddTrainPos => OnAddTrainPos(session, player),
            CmdStartTrain => OnStartTrain(session, player, param),
            CmdStartManyTrain => OnStartManyTrain(session, player, param),
            CmdInterruptTrain => OnInterruptTrain(session, player, param),
            CmdEndTrain => OnEndTrain(session, player, param),
            CmdHeadTouched => OnHeadTouched(session, player, param),
            _ => Unknown(session, sCmd, param),
        };
    }

    // ================= AddTrainPos =================

    /// <summary>
    /// 加特训栏位。★ <c>OnAddTrainPos</c> **完全不读回包内容**
    /// （`GirlLogic.lua:55` 只有 `UI.Call('UI_GirlTrain','OnAddTrainPos')`），
    /// 但必须回 —— 否则 <see cref="Function"/> 这一族的 `Blocking.Hide()` 不会执行、界面卡遮罩。
    /// </summary>
    private Task OnAddTrainPos(ClientSession session, Player player)
    {
        int cur = TrainPosCount(player);
        if (cur >= MaxTrainPosCount)
        {
            // ★ 失败只传错误码，所以必须用**数值**表达「栏位已满」= 2
            Fail(session, CmdAddTrainPos, ErrPosFull);
            Log.Info($"[session {session.Id}] GirlLogic/AddTrainPos 栏位已满（{cur}/{MaxTrainPosCount}）→ nError=2");
            return Task.CompletedTask;
        }

        player.SetTask(GmUnlock.GirlVarGroup, TrainPosCountTaskId, cur + 1);
        _ctx.Store.Save(player);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(GmUnlock.GirlVarGroup, TrainPosCountTaskId), cur + 1),
        });

        Ok(session, CmdAddTrainPos, new JsonObject());
        Log.Info($"[session {session.Id}] GirlLogic/AddTrainPos 栏位 {cur} → {cur + 1}");
        return Task.CompletedTask;
    }

    // ================= StartTrain =================

    /// <summary>
    /// 开始特训。上行 <c>{nId, nPos}</c>。
    /// ★ 顺序很关键：**先写少女属性 + 推 pf 1026，再回包** —— 见类注释 §1。
    /// </summary>
    private Task OnStartTrain(ClientSession session, Player player, JsonNode? param)
    {
        uint girlId = (uint)ReadInt(param?["nId"]);
        int pos = ReadInt(param?["nPos"]);

        if (!StartOne(session, player, girlId, pos, out string why))
        {
            Fail(session, CmdStartTrain, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/StartTrain 少女 {girlId} pos={pos} 拒绝：{why}");
            return Task.CompletedTask;
        }

        _ctx.Store.Save(player);
        // ★ 回包只需 nId（客户端其余全从属性读）
        Ok(session, CmdStartTrain, new JsonObject { ["nId"] = girlId });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 单个少女开始特训：校验 → 写 group 3 的 TrainPos/TrainTime/TrainOutId → 推 pf 1026。
    /// 返回 false 时 <paramref name="why"/> 说明原因（写进日志，客户端只会收到错误码）。
    /// </summary>
    internal bool StartOne(ClientSession session, Player player, uint girlId, int pos, out string why)
    {
        why = "";
        if (girlId == 0 || !player.Girls.Any(g => g.Girlid == girlId))
        {
            why = "玩家未持有该少女";
            return false;
        }
        if (pos <= 0)
        {
            why = "nPos 非法";
            return false;
        }

        var slot = _train.PosOf(pos);
        if (slot is null)
        {
            why = $"pos={pos} 不在 {TrainTable.PosFileName} 里";
            return false;
        }

        // 同一个位置不能同时塞两个少女
        foreach (var g in player.Girls)
        {
            if (g.Girlid == girlId)
                continue;
            if (GetVar(player, g.Girlid, VarTrainPos) == pos)
            {
                why = $"pos={pos} 已被少女 {g.Girlid} 占用";
                return false;
            }
        }

        // 已在特训中的不能重复开始
        if (GetVar(player, girlId, VarTrainPos) > 0)
        {
            why = "该少女已在特训中";
            return false;
        }

        int outdoorId = CurrentOutdoor(player, _trainHouse);
        var row = _train.TrainingFor(outdoorId, slot.Type);
        int cafeLevel = CafeFeature.ReadState(player, _trainCafe).Level;
        int capacity = _trainCafe.LevelRowFor(cafeLevel)?.OpenTrain ?? 0;
        var active = player.Girls.Where(g => GetVar(player, g.Girlid, VarTrainPos) > 0).ToList();
        if (row is null || row.Time <= 0 || capacity <= 0 || active.Count >= capacity)
        {
            why = "特训配置不可用或总栏位已满";
            return false;
        }
        int sameType = active.Count(g => _train.PosOf(GetVar(player, g.Girlid, VarTrainPos))?.Type == slot.Type);
        if (row.MaxNum <= 0 || sameType >= row.MaxNum)
        {
            why = "该类型特训栏位已满";
            return false;
        }
        int seconds = row.Time;
        long endAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;

        _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        SetVar(session, player, girlId, VarTrainPos, pos);
        SetVar(session, player, girlId, VarTrainTime, (int)endAt);
        SetVar(session, player, girlId, VarTrainOutId, outdoorId);
        _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Log.Info($"[session {session.Id}] GirlLogic/StartTrain 少女 {girlId} → pos={pos}" +
                 $"（外景 {outdoorId} 类型 {slot.Type}）{seconds}s 后结束（{endAt}）");
        return true;
    }

    /// <summary>多人特训：逐个调 <see cref="StartOne"/>，全部失败才回错误。</summary>
    private Task OnStartManyTrain(ClientSession session, Player player, JsonNode? param)
    {
        int ok = 0, fail = 0;
        if (param?["data"] is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o)
                    continue;
                if (StartOne(session, player, (uint)ReadInt(o["nId"]), ReadInt(o["nPos"]), out _))
                    ok++;
                else
                    fail++;
            }
        }

        if (ok == 0)
        {
            Fail(session, CmdStartManyTrain, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/StartManyTrain 全部失败（{fail} 个）");
            return Task.CompletedTask;
        }

        _ctx.Store.Save(player);
        Ok(session, CmdStartManyTrain, new JsonObject());
        Log.Info($"[session {session.Id}] GirlLogic/StartManyTrain 成功 {ok} 个、失败 {fail} 个");
        return Task.CompletedTask;
    }

    // ================= InterruptTrain / EndTrain =================

    /// <summary>打断特训：清属性、不发奖。</summary>
    private Task OnInterruptTrain(ClientSession session, Player player, JsonNode? param)
    {
        if (!GachaRequestLimits.TryInteger(param?["nId"], out int requestedGirl) || requestedGirl <= 0)
        {
            Fail(session, CmdInterruptTrain, ErrGeneric);
            return Task.CompletedTask;
        }
        uint girlId = (uint)requestedGirl;
        if (girlId == 0 || GetVar(player, girlId, VarTrainPos) <= 0)
        {
            Fail(session, CmdInterruptTrain, ErrGeneric);
            return Task.CompletedTask;
        }

        _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        ClearTrain(session, player, girlId);
        _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _ctx.Store.Save(player);
        Ok(session, CmdInterruptTrain, new JsonObject { ["nId"] = girlId });
        Log.Info($"[session {session.Id}] GirlLogic/InterruptTrain 少女 {girlId} 特训已打断（不发奖）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 特训结算。上行 <c>{nId}</c>；回包字段取证 `GirlLogic.lua:305..307`：
    /// <c>nId</c> / <c>nMoney</c> / <c>AddExpInfo</c>。
    /// 奖励来自 `TrainingGirl.txt`：`Award1` = 好感度、`Award2` = GDPLN 格式的玩家奖励。
    /// </summary>
    private Task OnEndTrain(ClientSession session, Player player, JsonNode? param)
    {
        if (!GachaRequestLimits.TryInteger(param?["nId"], out int requestedGirl) || requestedGirl <= 0)
        {
            Fail(session, CmdEndTrain, ErrGeneric);
            return Task.CompletedTask;
        }
        uint girlId = (uint)requestedGirl;
        int pos = girlId == 0 ? 0 : GetVar(player, girlId, VarTrainPos);
        if (pos <= 0)
        {
            Fail(session, CmdEndTrain, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/EndTrain 少女 {girlId} 不在特训中");
            return Task.CompletedTask;
        }

        long endAt = GetVar(player, girlId, VarTrainTime);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (endAt > now)
        {
            // 还没到点：拒绝（客户端本地也会拦，这里是服务端兜底）
            Fail(session, CmdEndTrain, ErrGeneric);
            Log.Warn($"[session {session.Id}] GirlLogic/EndTrain 少女 {girlId} 还差 {endAt - now}s");
            return Task.CompletedTask;
        }

        int outId = GetVar(player, girlId, VarTrainOutId);
        var slot = _train.PosOf(pos);
        var row = _train.TrainingFor(outId > 0 ? outId : CurrentOutdoor(player, _trainHouse), slot?.Type ?? 0);

        var girl = player.GetGirl(girlId);
        if (row is null || girl is null || endAt <= 0)
        {
            Fail(session, CmdEndTrain, ErrGeneric);
            return Task.CompletedTask;
        }
        var reward = TrainingReward(player, row);
        if (reward.LoveAward < 0 || (long)girl.Exp + reward.LoveAward > int.MaxValue
            || _headTouchLove.MaxLevel(girlId) <= 1)
        {
            Fail(session, CmdEndTrain, ErrGeneric);
            return Task.CompletedTask;
        }
        GirlLoveFeature.AddExp(_headTouchLove, player, girlId, reward.LoveAward, out var delta);
        if (girl.Level >= _headTouchLove.MaxLevel(girlId))
        {
            girl.Exp += (uint)(reward.LoveAward - delta.Added);
            delta = delta with { Added = reward.LoveAward, NewExp = (int)girl.Exp };
        }
        var unlocked = GirlLoveFeature.UnlockStories(_headTouchLove, player, girlId);
        var granted = _ctx.Rewards.Grant(player, reward.PlayerAwards, RewardSource.Extra);
        _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var actionAchievements = GirlActionAchievements.Record(_headTouchMissions, player, girlId, row.Id);
        PledgeFeature.RecordAction(_pledge, player, (int)girlId, "Training", 1, row.Id);
        ClearTrain(session, player, girlId);
        _ctx.PledgeTimes.Refresh(player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _mood.Refresh(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _ctx.Store.Save(player);
        GameNotify.PushGirl(session, girl);
        GameNotify.PushTaskValues(session, unlocked);
        GameNotify.PushTaskValues(session, actionAchievements);
        GameNotify.PushRewards(session, player, granted);
        var money = reward.PlayerAwards.FirstOrDefault(a => a.Length >= 5) ?? new[] { 0, 0, 0, 0, 0 };
        var reply = new JsonObject
        {
            ["nId"] = girlId,
            ["nMoney"] = new JsonArray(money.Take(5).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["AddExpInfo"] = new JsonArray { delta.Added, delta.OldExp, delta.NewExp,
                delta.OldLevel, delta.NewLevel },
        };
        Ok(session, CmdEndTrain, reply);

        Log.Info($"[session {session.Id}] GirlLogic/EndTrain 少女 {girlId} 结算：" +
                 $"好感 +{reward.LoveAward}、玩家奖励 {granted.Describe()}");
        return Task.CompletedTask;
    }

    // ================= HeadTouched =================

    // GirlConfig.MaxTouchHeadCount=5, TouchHeadTime=3600;
    // GirlCommon reads remaining uses/time from task group 2, ids 101/102.
    internal const int HeadTouchGroup = 2;
    internal const int HeadTouchCount = 101;
    internal const int HeadTouchTime = 102;
    internal const int MaxHeadTouches = 5;
    internal const int HeadTouchInterval = 3600;

    /// <summary>Repair missing legacy counters and settle elapsed recharge intervals.</summary>
    internal static bool RefreshHeadTouches(Player player, long now)
    {
        int count = player.GetTask(HeadTouchGroup, HeadTouchCount);
        int time = player.GetTask(HeadTouchGroup, HeadTouchTime);
        int oldCount = count, oldTime = time;
        bool missing = !player.Tasks.ContainsKey(Player.ComposeTaskId(HeadTouchGroup, HeadTouchCount));
        // Compatibility default for saves made before this system was implemented.
        if (missing && time == 0) count = MaxHeadTouches;
        count = Math.Max(0, count);
        if (count >= MaxHeadTouches)
            time = 0;
        else if (time <= 0)
            time = (int)now;
        else if (now > time)
        {
            long intervals = (now - time) / HeadTouchInterval;
            count = (int)Math.Min(MaxHeadTouches, count + intervals);
            time = count >= MaxHeadTouches ? 0 : (int)(time + intervals * HeadTouchInterval);
        }
        if (!missing && count == oldCount && time == oldTime) return false;
        player.SetTask(HeadTouchGroup, HeadTouchCount, count);
        player.SetTask(HeadTouchGroup, HeadTouchTime, time);
        return true;
    }

    /// <summary>
    /// GirlLogic.OnHeadTouched exists and requires nType to route the completion
    /// action to UI_Main, UI_Girl or the tutorial. The restored reward is 50 affection.
    /// </summary>
    private Task OnHeadTouched(ClientSession session, Player player, JsonNode? param)
    {
        if (!GachaRequestLimits.TryInteger(param?["nId"], out int requestedGirl) || requestedGirl <= 0)
        {
            Fail(session, CmdHeadTouched, ErrGeneric);
            return Task.CompletedTask;
        }
        uint girlId = (uint)requestedGirl;
        int source = 0;
        if (param?["nType"] is { } sourceNode && !GachaRequestLimits.TryInteger(sourceNode, out source))
        {
            Fail(session, CmdHeadTouched, ErrGeneric);
            return Task.CompletedTask;
        }
        if (player.GetGirl(girlId) is null || source < 0 || source > 2)
        {
            Fail(session, CmdHeadTouched, ErrGeneric);
            return Task.CompletedTask;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool refreshed = RefreshHeadTouches(player, now);
        int count = player.GetTask(HeadTouchGroup, HeadTouchCount);
        if (count <= 0)
        {
            if (refreshed) _ctx.Store.Save(player);
            PushHeadTouches(session, player);
            Fail(session, CmdHeadTouched, 2); // Girl.NullTouch
            return Task.CompletedTask;
        }
        // Restored historical head-touch reward, authorized by the user: +50 affection.
        // Reuse the same level curve and cap as gifts; never write a raw +50 to Exp.
        GirlLoveFeature.AddExp(_headTouchLove, player, girlId, HeadTouchLoveReward, out var delta);
        player.SetTask(HeadTouchGroup, HeadTouchCount, count - 1);
        if (player.GetTask(HeadTouchGroup, HeadTouchTime) == 0)
            player.SetTask(HeadTouchGroup, HeadTouchTime, (int)now);
        PledgeFeature.RecordAction(_pledge, player, (int)girlId, "TouchHead");
        var achievements = RecordHeadTouchAchievements(_headTouchMissions, player, girlId);
        var unlockedStories = GirlLoveFeature.UnlockStories(_headTouchLove, player, girlId);
        _ctx.Store.Save(player);
        PushHeadTouches(session, player);
        GameNotify.PushTaskValues(session, achievements);
        GameNotify.PushTaskValues(session, unlockedStories);
        GameNotify.PushGirl(session, player.GetGirl(girlId)!);
        var reply = new JsonObject
        {
            ["nId"] = girlId, ["nType"] = source,
            ["nAddExp"] = delta.Added,
            ["nOldExp"] = delta.OldExp, ["nNewExp"] = delta.NewExp,
            ["nOldLevel"] = delta.OldLevel, ["nNewLevel"] = delta.NewLevel,
        };
        if (delta.IsMaxLevel) reply["nIsMaxlevel"] = true;
        Ok(session, CmdHeadTouched, reply);
        Log.Info($"[session {session.Id}] GirlLogic/HeadTouched girl={girlId} source={source} remaining={count - 1}");
        return Task.CompletedTask;
    }

    /// <summary>Achievement type 15 counts completed head touches, shared across girls.</summary>
    internal static List<KeyValuePair<uint, int>> RecordHeadTouchAchievements(MissionTable missions, Player player, uint girlId = 0)
        => GirlActionAchievements.Record(missions, player, girlId, null);

    private static void PushHeadTouches(ClientSession session, Player player)
        => GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(HeadTouchGroup, HeadTouchCount), player.GetTask(HeadTouchGroup, HeadTouchCount)),
            new KeyValuePair<uint, int>(Player.ComposeTaskId(HeadTouchGroup, HeadTouchTime), player.GetTask(HeadTouchGroup, HeadTouchTime)),
        });

    private Task Unknown(ClientSession session, string sCmd, JsonNode? param)
    {
        Log.Warn($"[session {session.Id}] unhandled GirlLogic/{sCmd} param={param?.ToJsonString()}");
        return Task.CompletedTask;
    }

    // ================= 工具 =================

    /// <summary>当前特训栏位数（缺省 <see cref="DefaultTrainPosCount"/>）。</summary>
    internal static int TrainPosCount(Player player)
    {
        int v = player.GetTask(GmUnlock.GirlVarGroup, TrainPosCountTaskId);
        return v > 0 ? v : DefaultTrainPosCount;
    }

    // GirlConfig.GetTrainingAward: take affection and currency maxima independently
    // over owned, region-visible outdoors, including the built-in default 72.
    internal TrainingRow TrainingReward(Player player, TrainingRow training)
    {
        var owned = HouseFeature.ReadOrCreate(player, _trainHouse).Furniture
            .Select(f => _trainHouse.ItemOf(f.TemplateId))
            .Where(t => t is not null && t.Type == _trainHouse.OutDoorScene
                && (string.IsNullOrEmpty(t.ThemeType) || _trainThemes.Contains(t.ThemeType)))
            .Select(t => t!.Id).Append((int)HouseTable.OutdoorObjId).Distinct();
        var rows = owned.Select(id => _train.TrainingFor(id, training.Id))
            .Where(r => r is not null).Select(r => r!).ToList();
        int love = rows.Select(r => r.LoveAward).DefaultIfEmpty(0).Max();
        // The original table has one Award2 GDPLN per training. Match its item key
        // and maximize only its count, without mutating the shared config arrays.
        var awards = training.PlayerAwards.Select(a =>
        {
            var copy = a.ToArray();
            if (copy.Length >= 5)
                copy[4] = rows.SelectMany(r => r.PlayerAwards)
                    .Where(b => b.Length >= 5 && b.Take(4).SequenceEqual(copy.Take(4)))
                    .Select(b => b[4]).DefaultIfEmpty(0).Max();
            return copy;
        }).ToList();
        return training with { LoveAward = love, PlayerAwards = awards };
    }

    // Outdoor templates share Type=OutDoorScene; only placed furniture belongs to
    // the scene. The built-in default outdoor is client-owned and absent in saves.
    internal static int CurrentOutdoor(Player player, HouseTable table)
        => HouseFeature.ReadOrCreate(player, table).Furniture
            .Where(f => f.RoomId == HouseFeature.OutdoorRoomId && f.ParentId != 0
                && table.ItemOf(f.TemplateId)?.Type == table.OutDoorScene)
            .Select(f => f.TemplateId).FirstOrDefault(table.OutDoorScene);

    internal static int GetVar(Player player, uint girlId, int off)
        => player.GetTask(GirlLoveFeature.VarGroup(girlId), GirlLoveFeature.VarTaskId(girlId, off));

    /// <summary>写少女属性并**立刻推 pf 1026** —— 客户端要靠它读 TrainPos/TrainTime。</summary>
    private static void SetVar(ClientSession session, Player player, uint girlId, int off, int value)
    {
        int taskId = GirlLoveFeature.VarTaskId(girlId, off);
        int group = GirlLoveFeature.VarGroup(girlId);
        player.SetTask(group, taskId, value);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(group, taskId), value),
        });
    }

    private static void ClearTrain(ClientSession session, Player player, uint girlId)
    {
        SetVar(session, player, girlId, VarTrainPos, 0);
        SetVar(session, player, girlId, VarTrainTime, 0);
        SetVar(session, player, girlId, VarTrainOutId, 0);
    }

    /// <summary>成功回包。★ <b>原样回 sCmd</b>（客户端自己加 On 前缀）且**绝不带 nError**。</summary>
    private void Ok(ClientSession session, string sCmd, JsonObject payload)
    {
        payload["sCmd"] = sCmd;
        _ctx.LuaCalls.CallClientFunction(session, Function, payload);
    }

    /// <summary>
    /// 失败回包。★ 客户端**只会拿到 <c>nError</c> 这个数字**（Dispatcher 第 ⑤ 条），
    /// 所以语义必须编码在数值里。
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
