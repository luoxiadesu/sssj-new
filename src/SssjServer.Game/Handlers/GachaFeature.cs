using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 抽卡 / 卡池（<c>sFunction = "Lottery"</c> 一族）。
///
/// <para><b>0. 为什么这个域最不能拖</b></para>
/// 客户端发 <c>Lottery</c> 之前是 <c>Game.Blocking.Show(300)</c>
/// （<c>UI_Gacha.lua:1642</c> / <c>:2299</c>）—— **5 分钟**遮罩，
/// 不回包的观感等同卡死。其余域都是不带参的 <c>Show()</c>（默认 2 秒自动消失），
/// 所以「不实现」只是点了没反应。这是 <c>docs/05</c> §0.4 的结论。
///
/// <para><b>1. 上下行</b></para>
/// <list type="table">
/// <item><term>C2S <c>Lottery</c></term><description><c>{nId, bTen}</c> ——
///   <c>UI_Gacha.lua:1641 SendServerMsg</c> 里 <c>SendCmd('Lottery', {nId=id, bTen=ten})</c></description></item>
/// <item><term>C2S <c>GetLastGacha</c></term><description>**裸布尔**（sJsonParam 就是
///   <c>"true"</c>/<c>"false"</c>，不是对象）—— <c>UI_Gacha.lua:347</c> / <c>:1643</c></description></item>
/// <item><term>C2S <c>SetCardCustomUp</c></term><description><c>{nId, tbItems:[cardId1, cardId2]}</c>
///   —— <c>UI_GachaCardSelected.lua:168</c></description></item>
/// <item><term>S2C <c>Lottery</c></term><description><c>S2CCall.lua:193</c> →
///   <c>UI_Gacha:OnServerResponse</c>（<c>UI_Gacha.lua:631</c>）</description></item>
/// <item><term>S2C <c>GetLastGacha</c></term><description><c>S2CCall.lua:197</c> →
///   <c>UI_Gacha:ShowLastGacha</c>（<c>:2346</c>）</description></item>
/// </list>
/// ★ 本族用 <c>err</c>（字符串 errorKey）报错，**不是** <c>nError</c>
/// （<c>OnServerResponse</c> 第一句就是 <c>if tbData.err then</c>，`:638`）——
/// 与 AGENTS.md §5 记的那两族都不同，是第三种。
///
/// <para><b>2. 回包字段（逐行核过 <c>OnServerResponse</c> 与 <c>:1232..1345</c>）</b></para>
/// <list type="bullet">
///   <item><c>bTen</c> **必填**：<c>true</c> ⇒ <c>tbAwards</c> 必须 ≥ 10 项
///     （<c>:1232 for i = 1, 10 do</c> + <c>:1243</c> 无越界保护）。</item>
///   <item><c>tbAwards</c> **必填非空**：<c>:680 #tbData.tbAwards</c>，
///     空数组会在 <c>:1249</c> NRE。</item>
///   <item><c>tbAwards[i].tbGDPL</c> **必填** <c>[g,d,p,l]</c>（<c>:679</c> 直接 unpack）。</item>
///   <item><c>bHasCard</c>：**必须显式 <c>false</c> 才播抽卡剧情**，
///     使用抽取前的永久收藏及持有状态，首次为false，重复为true。</item>
///   <item><c>nTotalTimes</c> **条件必填**：池子 <c>UpBoxNum &gt; 0</c> 且
///     <c>RotateVersion &gt; 0</c> 时 <c>:1241</c> 会做 <c>nTotalTimes % nUpBoxNum</c>，
///     nil 直接抛。本实现无条件填。</item>
///   <item><c>isUp</c> / <c>nUpTimes</c>：<c>isUp == true</c> 时后者必填。</item>
/// </list>
///
/// <para><b>3. 概率：照抄共享模块，不自己发明</b></para>
/// <c>GachaCommon.lua</c> 是客户端与原服**共用**的（AGENTS.md §2.5），算法整份在包内：
/// <list type="number">
///   <item>星级权重 = <c>GetStarRate</c>（<c>:279..307</c>）：
///     <c>times = me:GetTaskValue(15, nId + 2) + 1</c>，对 1★..4★ 各自从
///     <c>CardStarSeed.txt</c> 的阶梯里取**第一个** <c>times &lt;= step.times</c> 的 weight，
///     再归一化 —— 移植见 <see cref="CardStarSeedRow.WeightsAt"/>。</item>
///   <item>星级内选卡 = <see cref="GachaCard.RateInStar"/>
///     （<c>LoadConfig</c> 已把 <c>Rate</c> 除以所在星级的合计，<c>:152..156</c>）。</item>
///   <item>扣费 = <c>JudgeCurrency</c>（<c>:397..429</c>）：**道具 → 免费钻 → 免费钻+付费钻补足**，
///     移植见 <see cref="TrySpend"/>。</item>
/// </list>
///
/// <para><b>4. ★ 保底计数必须真的动</b></para>
/// <c>(15, nId + 2)</c>（<c>PROTECT_TASK_BEGIN = 2</c>）每抽 +1、十连 +10；
/// **出 4★ 时清零**。客户端 <c>:1326..1340</c> 用
/// <c>cur - lastValue == (bTen and 10 or 1)</c> 判「正常抽」，否则走**保底动画**——
/// 不维护这个值会永远播保底演出。<c>JudgeType == 2</c>（新人补给）跳过该判定。
/// 同时维护 <c>ProtectNum</c> 的硬保底：第 N 抽必出 4★。
///
/// <para><b>5. 现网数据的坑</b></para>
/// <c>gacha.txt</c> 100 个角色池里**只有池 66（誓约池）**没过期（结束时间 2077 年），
/// 其余 <c>Time</c> 都是 2019-2020。验收就用 66，或用客户端自带的
/// <c>Gacha:GmOpenAll()</c>（<c>GachaCommon.lua:432</c>）临时放开。
/// 池不存在/已过期时回 <c>error.gacha.notexists</c> —— 客户端 <c>:639</c> 特判它，
/// 会弹「换池」对话框而不是普通错误框。
/// </summary>
public sealed partial class GachaFeature : IGameFeature, IGamePulseFeature
{
    /// <summary>抽卡（上下行同名）。</summary>
    public const string FnLottery = "Lottery";

    /// <summary>查上一次抽卡记录（上下行同名）。</summary>
    public const string FnGetLastGacha = "GetLastGacha";

    /// <summary>自选 UP（<c>UI_GachaCardSelected.lua:168</c>）。</summary>
    public const string FnSetCardCustomUp = "SetCardCustomUp";

    // ---- 任务值（GachaCommon.lua:4..29，group 15）----

    /// <summary>抽卡任务值 group（<c>GachaCommon.GID = 15</c>）。</summary>
    public const int TaskGroup = 15;

    /// <summary>总抽数（不分池，<c>TID_TOTAL = 1</c>）。</summary>
    public const int TaskTotal = 1;

    /// <summary>该池保底计数的起始偏移（<c>PROTECT_TASK_BEGIN = 2</c>）⇒ 键是 <c>2 + nId</c>。</summary>
    public const int TaskProtectBegin = 2;

    /// <summary>该池 UP 保底计数起始偏移（<c>PROTECT_UP_TASK_BEGIN = 2001</c>）。</summary>
    public const int TaskProtectUpBegin = 2001;

    /// <summary>该池累计抽数起始偏移（<c>PRE_POOL_TASK_BEGIN = 3001</c>）。</summary>
    public const int TaskPrePoolBegin = 3001;

    /// <summary>角色自选 UP 选择（<c>CARD_CUSTOM_CHOICE = 6001</c>）：<c>card1 | (card2 &lt;&lt; 16)</c>。</summary>
    public const int TaskCardCustomChoice = 6001;

    /// <summary>角色自选次数（<c>CARD_CUSTOM_TIME = 7001</c>）。</summary>
    public const int TaskCardCustomTime = 7001;

    /// <summary>角色单抽次数（不分池，<c>CARD_GACHA_SINGLE = 10001</c>）。</summary>
    public const int TaskCardSingle = 10001;

    /// <summary>角色十连次数（不分池，<c>CARD_GACHA_MUL = 10002</c>）。</summary>
    public const int TaskCardMul = 10002;
    public const int TaskFreeTime = 8001;

    // ---- errorKey（客户端硬编码特判的两个）----

    /// <summary>池不存在/已过期 —— 客户端 <c>UI_Gacha.lua:639</c> 特判，弹「换池」对话框。</summary>
    public const string ErrNotExists = "error.gacha.notexists";

    /// <summary>没有上一次记录 —— 客户端 <c>UI_Gacha.lua:2348</c> 特判，只弹提示。</summary>
    public const string ErrNoLast = "error.gacha.nolast";

    /// <summary>钱/券不够。errorKey 的本地化表不在 extracted 里（见 docs/05 未取证清单 §7），
    /// 客户端 <c>Text()</c> 查不到会返回空串、只弹空白框，不会崩。</summary>
    public const string ErrNotEnough = "error.gacha.notenough";

    /// <summary>十连的抽数。</summary>
    public const int TenCount = 10;

    /// <summary><see cref="Player.Extra"/> 里存「上一次抽卡结果」的键。</summary>
    public const string ExtraKey = "gacha";

    private GameFeatureContext _ctx = null!;
    private GachaTable _table = GachaTable.Empty;
    private GachaRequestLimits _requestLimits = null!;
    private readonly Random _rng;
    public GachaFeature() => _rng = new Random();
    internal GachaFeature(Random random) => _rng = random;

    public string Name => "抽卡/卡池（sFunction Lottery）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = GachaTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _requestLimits = new GachaRequestLimits(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.OnPlayerPrepare((session, player) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool changed = CardCollection.SyncOwned(player, ctx.CharacterCards);
            changed |= GachaFreeDraws.Refresh(player, _table.OpenAt(now), now, []);
            changed |= GachaVersionState.Refresh(player, _table.OpenAt(now), []);
            if (changed) ctx.Store.Save(player);
        });

        ctx.MapFunction(FnLottery, OnLottery);
        // GetLastGacha 的 sJsonParam 是**裸布尔**，JsonNode.Parse("true") 能过，所以走普通通道即可
        ctx.MapFunction(FnGetLastGacha, OnGetLastGacha);
        ctx.MapFunction(FnSetCardCustomUp, OnSetCardCustomUp);
    }

    // =================================================================
    //  Lottery
    // =================================================================

    private Task OnLottery(ClientSession session, string function, JsonNode? param)
    {
        if(session.Tag is not Player player)return Task.CompletedTask;
        lock(player)return OnLotteryLocked(session,function,param);
    }
    private Task OnLotteryLocked(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Lottery 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var request = param as JsonObject;
        if (!GachaRequestLimits.TryInteger(request?["nId"], out int poolId) || poolId <= 0
            || !GachaRequestLimits.TryMode(request?["bTen"], out bool ten))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ★ 一个池 id 有多段档期，Get(id, now) 只返回当前生效的那一段（见 GachaTable 类注释）
        var pool = _table.Get(poolId, now);
        if (pool is null)
        {
            Log.Warn($"[session {session.Id}] Lottery nId={poolId} " +
                     (_table.Exists(poolId)
                         ? $"存在 {_table.Segments(poolId).Count} 个档期但都不在开放期"
                         : "池不存在") +
                     $" → {ErrNotExists}");
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        if (pool.Cards.Count == 0)
        {
            Log.Warn($"[session {session.Id}] Lottery nId={poolId}（{pool.Name}）当前档期的卡组 " +
                     $"{pool.PoolFile}.txt 为空 → {ErrNotExists}");
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        // ★ JudgeType 门槛：1 特殊 / 2 新人补给的池要求「持票 >= 1 且已通过 1-6 普通」
        // （GachaCommon:GetCurPools，GachaCommon.lua:225..240）。客户端界面按同一条件决定
        // 池子出不出现在列表里，所以服务端照同口径校验，两边一致。
        if (!IsSelectable(player, pool))
        {
            Log.Warn($"[session {session.Id}] Lottery nId={poolId}（{pool.Name}）JudgeType=" +
                     $"{pool.JudgeType} 的门槛未满足（持票 {TicketCount(player, pool)}，" +
                     $"1-6 普通通关 {PassedFirstChapter(player)}）→ {ErrNotExists}");
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }

        if (ten && !pool.HasTen)
        {
            Log.Warn($"[session {session.Id}] Lottery nId={poolId} 该池没有十连配置，按单抽处理");
            ten = false;
        }

        int draws = ten ? TenCount : 1;
        if (!_requestLimits.HasRoom(player, 1, draws))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = "error.gacha.maxcard" });
            return Task.CompletedTask;
        }
        if (pool.JudgeType is 3 or 4 && !ValidChoice(pool,
            player.GetTask(TaskGroup, TaskCardCustomChoice + pool.Id)))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        if (pool.ProtectUpNum > 0 && !pool.Cards.Values.Any(c => c.Star == 4 && c.UpFlag == 1))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        List<int[]> bonus;
        try { bonus = PlanBonusRewards(pool, player.GetTask(TaskGroup, TaskPrePoolBegin + pool.Id), draws); }
        catch (InvalidDataException)
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        if (bonus.Any(tuple => _ctx.ItemTemplates.Get((uint)tuple[0], (uint)tuple[1],
            (uint)tuple[2], (uint)tuple[3]) is null))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }
        if (pool.JudgeType == 2)
            return PreviewNewcomer(session, player, pool, ten, bonus);

        int operationKey = ten ? TaskCardMul : TaskCardSingle;
        if (player.GetTask(TaskGroup, operationKey) == int.MaxValue ||
            new[] { TaskTotal, TaskPrePoolBegin + pool.Id, TaskProtectBegin + pool.Id,
                TaskProtectUpBegin + pool.Id }.Any(key => player.GetTask(TaskGroup, key) > int.MaxValue - draws))
        {
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotExists });
            return Task.CompletedTask;
        }

        // ---- 扣费（GachaCommon:JudgeCurrency 的顺序：道具 → 免费钻 → 免费钻+付费钻）----
        RefreshFreeState(session, player, now);
        var paymentBefore = new GachaPaymentSnapshot(player);
        var spend = TrySpend(player, pool, ten, now);
        if (!spend.Ok)
        {
            Log.Warn($"[session {session.Id}] Lottery nId={poolId} bTen={ten} 消耗不足：{spend.Reason}");
            Reply(session, FnLottery, new JsonObject { ["err"] = ErrNotEnough });
            return Task.CompletedTask;
        }

        var payment = paymentBefore.After(player);

        // ---- 抽卡 ----
        var seed = _table.GetSeed(pool.CardStarSeedId);
        var results = new List<DrawResult>(draws);
        var granted = new RewardGrantResult();
        for (int i = 0; i < draws; i++)
        {
            var r = DrawOne(player, pool, seed);
            results.Add(r);
            _ctx.Rewards.Grant(player,
                new[] { new[] { r.Card.G, r.Card.D, r.Card.P, r.Card.L, 1 } },
                RewardSource.Extra, granted);
        }
        _ctx.Rewards.Grant(player, bonus, RewardSource.Extra, granted);
        player.SetTask(TaskGroup, operationKey, player.GetTask(TaskGroup, operationKey) + 1);

        var record = BuildAwards(player, pool, results);
        JsonArray? companion = pool.GetItem is { Length: >= 4 } && bonus.Count > 0
            ? new JsonArray(bonus[0].Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) : null;
        WriteLast(player, ten, record, companion);
        var history = GachaHistory.Append(player,1,pool.Id,now,results.Select(r=>new[]{r.Card.G,r.Card.D,r.Card.P,r.Card.L}),payment,
            results.FindIndex(r=>r.ByProtect)+1);
        _ctx.Store.Save(player);

        GachaHistory.Push(session,1,history);

        // 货币/券的扣除与卡片入包
        GameNotify.PushMoney(session, player, spend.ChangedMoney);
        if (spend.ChangedItems.Count > 0)
            GameNotify.PushItems(session, spend.ChangedItems);
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, spend.ChangedTasks.Concat(TaskDeltaOf(player, pool)));

        var reply = new JsonObject
        {
            ["bTen"] = ten,
            ["tbAwards"] = record.DeepClone(),
        };
        if (companion is not null) reply["getItem"] = companion.DeepClone();
        Reply(session, FnLottery, reply);

        Log.Info($"[session {session.Id}] Lottery nId={poolId}（{pool.Name}）bTen={ten} " +
                 $"消耗={spend.Describe()} → " +
                 string.Join(" ", results.Select(r =>
                     $"{r.Card.Id}({r.Card.G},{r.Card.D},{r.Card.P},{r.Card.L})★{r.Card.Star}" +
                     (r.ByProtect ? "[保底]" : "") + (r.Card.UpFlag == 1 ? "[UP]" : ""))) +
                 $"；保底计数 (15,{TaskProtectBegin + poolId})=" +
                 $"{player.GetTask(TaskGroup, TaskProtectBegin + poolId)}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 1-6 普通难度的 mapId。<c>JudgeType</c> 1/2 的池要求它已通关 ——
    /// 客户端是 <c>SystemUnlock:CheckLevel(1, 6, 1)</c>（GachaCommon.lua:237），
    /// 而它读的就是服务端下发的 <c>levellist</c>（见 <see cref="Config.SystemUnlockTable"/>）。
    /// 顺带一提：每日签到也是这道门槛（docs/05 §3.4）。
    /// </summary>
    public static uint FirstChapterMapId => MapList.MapId(1, 6, 1);

    /// <summary>是否已通过 1-6 普通难度。</summary>
    internal static bool PassedFirstChapter(Player player)
        => MapList.PassCount(player.GetLevelStar(FirstChapterMapId)) > 0;

    /// <summary>该池 <c>JudgeType</c> 1/2 所需门票的持有数量（不需要门票时返回 int.MaxValue）。</summary>
    internal int TicketCount(Player player, GachaPool pool)
    {
        var ticket = pool.TicketItem;
        if (ticket is null || ticket.Length < 4)
            return int.MaxValue;
        uint g = (uint)ticket[0], d = (uint)ticket[1], p = (uint)ticket[2], l = (uint)ticket[3];
        return (int)player.Items
            .Where(i => i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l)
            .Sum(i => (long)i.Count);
    }

    /// <summary>该池此刻对这个玩家是否可抽（<see cref="GachaPool.IsSelectable"/> 的绑定）。</summary>
    internal bool IsSelectable(Player player, GachaPool pool)
        => pool.IsSelectable(TicketCount(player, pool), PassedFirstChapter(player));

    // =================================================================
    //  抽取
    // =================================================================

    /// <summary>一次抽取的结果。</summary>
    internal sealed record DrawResult(GachaCard Card, bool ByProtect, bool FirstGet,
                                     int Times, int TotalTimes, bool IsUp, int UpTimes);

    /// <summary>
    /// 抽一次并就地更新 group 15 的计数。
    ///
    /// 顺序（与原服语义一致）：
    /// <list type="number">
    ///   <item>保底计数 +1、池累计 +1、总抽数 +1；</item>
    ///   <item>若 <c>ProtectNum &gt; 0</c> 且保底计数达到它 ⇒ **强制 4★**；</item>
    ///   <item>否则按 <see cref="CardStarSeedRow.WeightsAt"/> 抽星级、再按
    ///     <see cref="GachaCard.RateInStar"/> 在星级内抽卡；</item>
    ///   <item>出 4★ ⇒ 保底计数清零（客户端靠「差值 != 抽数」判保底演出）。</item>
    /// </list>
    /// </summary>
    internal DrawResult DrawOne(Player player, GachaPool pool, CardStarSeedRow? seed)
    {
        int protectKey = TaskProtectBegin + pool.Id;
        int prePoolKey = TaskPrePoolBegin + pool.Id;

        int protectCount = player.GetTask(TaskGroup, protectKey) + 1;
        int poolTimes = player.GetTask(TaskGroup, prePoolKey) + 1;
        int total = player.GetTask(TaskGroup, TaskTotal) + 1;

        player.SetTask(TaskGroup, protectKey, protectCount);
        player.SetTask(TaskGroup, prePoolKey, poolTimes);
        player.SetTask(TaskGroup, TaskTotal, total);

        bool byProtect = pool.ProtectNum > 0 && protectCount >= pool.ProtectNum;
        int upKey = TaskProtectUpBegin + pool.Id;
        int upCount = player.GetTask(TaskGroup, upKey);
        bool upActive = pool.ProtectUpNum > 0 && upCount != -1;
        int nextUp = upActive ? Math.Max(0, upCount) + 1 : upCount;
        bool byUpProtect = upActive && nextUp >= pool.ProtectUpNum;

        // times 传给 GetStarRate 的口径是「已抽次数 + 1」（GachaCommon.lua:284），
        // 而这里 protectCount 已经含本次，所以直接用它。
        int star = byProtect || byUpProtect ? 4 : PickStar(pool, seed, protectCount);
        int choice = player.GetTask(TaskGroup, TaskCardCustomChoice + pool.Id);
        var card = PickCard(pool, star, choice, byUpProtect);
        bool isUp = IsUpCard(pool, card, choice);
        if (upActive) player.SetTask(TaskGroup, upKey, isUp ? -1 : nextUp);

        if (card.Star >= 4)
            player.SetTask(TaskGroup, protectKey, 0);   // 出 4★ 清零保底

        bool firstGet = !CardCollection.HasOwned(player, (uint)card.G, (uint)card.D, (uint)card.P, (uint)card.L);

        return new DrawResult(card, byProtect, firstGet, protectCount, poolTimes, isUp,
            byUpProtect ? nextUp : 0);
    }

    /// <summary>
    /// 按 <c>CardStarSeed.txt</c> 的阶梯抽星级（<c>GachaCommon:GetStarRate</c> 的移植）。
    /// 缺表或权重全 0 时退化成「用池内各星级的原始权重合计」。
    /// </summary>
    private int PickStar(GachaPool pool, CardStarSeedRow? seed, int times)
    {
        int[] weights;
        if (seed is not null)
        {
            weights = seed.WeightsAt(times);
            // 该星级池子里没有卡的话，权重作废（否则会抽到空星级）
            for (int i = 0; i < 4; i++)
            {
                if (!pool.StarTotal.ContainsKey(i + 1))
                    weights[i] = 0;
            }
        }
        else
        {
            weights = new int[4];
            for (int i = 0; i < 4; i++)
                weights[i] = pool.StarTotal.TryGetValue(i + 1, out var t) ? t : 0;
        }

        int sum = weights.Sum();
        if (sum <= 0)
        {
            // 兜底：取池里存在的最低星级
            int fallback = pool.StarTotal.Keys.DefaultIfEmpty(1).Min();
            Log.Warn($"[gacha] 池 {pool.Id} 星级权重合计为 0（seed={pool.CardStarSeedId}），退化到 {fallback}★");
            return fallback;
        }

        int roll = _rng.Next(sum);
        int acc = 0;
        for (int i = 0; i < 4; i++)
        {
            acc += weights[i];
            if (roll < acc)
                return i + 1;
        }
        return 4;
    }

    /// <summary>在指定星级内按归一化概率抽一张卡。星级内没有卡时退化到全池。</summary>
    internal static bool ValidChoice(GachaPool pool, int value)
    {
        int first = value & 0xFFFF, second = (int)((uint)value >> 16);
        bool Eligible(int id) => id > 0 && pool.Cards.TryGetValue(id, out var card)
            && card.Star == 4 && (pool.JudgeType == 4 || card.UpFlag == 1);
        return pool.JudgeType == 4 ? second == 0 && Eligible(first)
            : pool.JudgeType == 3 && first != second && Eligible(first) && Eligible(second);
    }

    // GachaCommon.SetCustomUp: selected cards each .25; remaining four-stars share .5.
    internal static List<(GachaCard Card, double Weight)> CardWeights(GachaPool pool, int star, int choice)
    {
        if (pool.JudgeType == 4 && ValidChoice(pool, choice))
            return [(pool.Cards[choice], 1)];
        var cards = pool.Cards.Values.Where(c => c.Star == star).ToList();
        if (pool.JudgeType == 3 && star == 4 && ValidChoice(pool, choice))
        {
            int first = choice & 0xFFFF, second = (int)((uint)choice >> 16);
            int others = cards.Count - 2;
            return cards.Select(c => (c, c.Id == first || c.Id == second ? .25
                : others > 0 ? .5 / others : 0)).ToList();
        }
        return cards.Select(c => (c, (double)Math.Max(0, c.RawRate))).ToList();
    }

    private static bool IsUpCard(GachaPool pool, GachaCard card, int choice)
        => card.Star == 4 && (pool.JudgeType == 3
            ? card.Id == (choice & 0xFFFF) || card.Id == (int)((uint)choice >> 16)
            : card.UpFlag == 1);

    private GachaCard PickCard(GachaPool pool, int star, int choice, bool upOnly = false)
    {
        var candidates = CardWeights(pool, star, choice);
        if (upOnly) candidates = candidates.Where(c => IsUpCard(pool, c.Card, choice)).ToList();
        if (candidates.Count == 0)
        {
            Log.Warn($"[gacha] 池 {pool.Id} 里没有 {star}★ 的卡，退化到全池随机");
            candidates = pool.Cards.Values.Select(c => (c, (double)Math.Max(0, c.RawRate))).ToList();
        }

        double sum = candidates.Sum(c => c.Weight);
        if (sum <= 0)
            return candidates[_rng.Next(candidates.Count)].Card;

        double roll = _rng.NextDouble() * sum;
        double acc = 0;
        foreach (var c in candidates)
        {
            acc += c.Weight;
            if (roll < acc)
                return c.Card;
        }
        return candidates[^1].Card;
    }

    // =================================================================
    //  扣费（GachaCommon:JudgeCurrency 的移植）
    // =================================================================

    /// <summary>扣费结果。</summary>
    internal sealed class SpendResult
    {
        public bool Ok { get; set; }
        public string Reason { get; set; } = "";
        public List<int> ChangedMoney { get; } = new();
        public List<Item> ChangedItems { get; } = new();
        public List<KeyValuePair<uint, int>> ChangedTasks { get; } = new();
        private readonly List<string> _parts = new();

        public void Note(string part) => _parts.Add(part);
        public string Describe() => _parts.Count == 0 ? "免费" : string.Join("+", _parts);
    }

    /// <summary>
    /// 按 <c>GachaCommon:JudgeCurrency</c>（<c>:397..429</c>）的顺序扣费：
    /// **① 道具够就用道具 → ② 免费钻够就用免费钻 → ③ 免费钻 + 付费钻补足 → 否则失败**。
    ///
    /// <c>CashType</c> 0/1 用道具（<c>CostOne</c>/<c>CostTen</c> 是四元组），
    /// 其它值直接扣 id == <c>CashType</c> 的货币。
    /// <c>CanUseFreeDiamond == 0</c> 时不许用免费钻。
    /// </summary>
    internal SpendResult TrySpend(Player player, GachaPool pool, bool ten, long now)
    {
        var r = new SpendResult();
        int freeKey = TaskFreeTime + pool.Id;
        int used = player.GetTask(TaskGroup, freeKey);
        if (!ten && pool.FreeAvailableAt(now, used))
        {
            player.SetTask(TaskGroup, freeKey, used + 1);
            r.ChangedTasks.Add(new(Player.ComposeTaskId(TaskGroup, freeKey), used + 1));
            r.Note($"免费次数 {used + 1}/{pool.FreeCount}");
            r.Ok = true;
            return r;
        }

        if (pool.CashType is 0 or 1)
        {
            var cost = ten ? pool.CostTenItem : pool.CostOneItem;
            if (cost is null || cost.Length < 4)
            {
                r.Reason = $"CashType={pool.CashType} 但没有{(ten ? "十连" : "单抽")}道具配置";
                return r;
            }

            uint g = (uint)cost[0], d = (uint)cost[1], p = (uint)cost[2], l = (uint)cost[3];
            int need = cost.Length >= 5 && cost[4] > 0 ? cost[4] : 1;

            var stack = player.Items.FirstOrDefault(i => i.Genre == g && i.Detail == d
                                                      && i.Particular == p && i.Level == l);
            if (stack is not null && stack.Count >= need)
            {
                stack.Count -= (uint)need;
                r.ChangedItems.Add(stack);
                r.Note($"{_ctx.ItemTemplates.NameOf(g, d, p, l)}×{need}");
                r.Ok = true;
                return r;
            }

            // 道具不够：CashType==1 是「只道具」，不能用钻石兜底
            if (pool.CashType == 1)
            {
                r.Reason = $"道具 {_ctx.ItemTemplates.NameOf(g, d, p, l)} 不足（有 " +
                           $"{stack?.Count ?? 0}，需 {need}）且该池 CashType=1 只接受道具";
                return r;
            }

            // UI_Gacha.lua:2215: exchangeRate * (ten ? 10 : 1),
            // GachaCommon.JudgeCurrency: free first, then paid covers the remainder.
            long price = (long)pool.ExchangeRate * (ten ? TenCount : 1);
            if (price <= 0 || price > int.MaxValue)
            {
                r.Reason = "无有效地球币兑换价格";
                return r;
            }
            int free = pool.CanUseFreeDiamond ? Math.Max(0, player.GetMoney(MoneyType.FreeDiamond)) : 0;
            int paid = Math.Max(0, player.GetMoney(MoneyType.PayDiamond));
            if ((long)free + paid < price)
            {
                r.Reason = $"地球币不足（需 {price}）";
                return r;
            }
            int useFree = (int)Math.Min(free, price);
            int usePaid = (int)price - useFree;
            if (useFree > 0)
            {
                player.SetMoney(MoneyType.FreeDiamond, free - useFree);
                r.ChangedMoney.Add(MoneyType.FreeDiamond);
                r.Note($"免费地球币×{useFree}");
            }
            if (usePaid > 0)
            {
                player.SetMoney(MoneyType.PayDiamond, paid - usePaid);
                r.ChangedMoney.Add(MoneyType.PayDiamond);
                r.Note($"付费地球币×{usePaid}");
            }
            r.Ok = true;
            return r;
        }

        // 货币池：货币 id 就是 CashType（UI_Gacha.lua:651 MoneyLogic:GetInfo(nCash)）
        int moneyId = pool.CashType;
        int amount = ten ? pool.CostTenMoney : pool.CostOneMoney;
        if (amount <= 0)
        {
            r.Reason = $"CashType={moneyId} 但{(ten ? "十连" : "单抽")}价格为 {amount}";
            return r;
        }

        int have = player.GetMoney(moneyId);
        if (have < amount)
        {
            r.Reason = $"货币 {moneyId}({MoneyType.NameOf(moneyId)}) 不足（有 {have}，需 {amount}）";
            return r;
        }

        player.SetMoney(moneyId, have - amount);
        r.ChangedMoney.Add(moneyId);
        r.Note($"{MoneyType.NameOf(moneyId)}×{amount}");
        r.Ok = true;
        return r;
    }

    // =================================================================
    //  回包构造
    // =================================================================

    /// <summary>
    /// 组 <c>tbAwards</c>。每项的字段与必填性见类注释 §2。
    /// bHasCard按抽取前的收藏历史填写，让客户端走原有首次剧情/重复展示分支。
    /// </summary>
    private JsonArray BuildAwards(Player player, GachaPool pool, List<DrawResult> results)
    {
        var arr = new JsonArray();
        foreach (var r in results)
        {
            var gdpl = new JsonArray { r.Card.G, r.Card.D, r.Card.P, r.Card.L };
            var item = new JsonObject
            {
                ["tbGDPL"] = gdpl,
                ["nId"] = r.Card.Id,
                ["bHasCard"] = !r.FirstGet,
                ["bFirstGet"] = r.FirstGet,
                ["nTimes"] = r.Times,
                ["nTotalTimes"] = r.TotalTimes,   // 条件必填，无条件填更安全
            };
            if (r.IsUp)
            {
                item["isUp"] = true;
                item["nUpTimes"] = r.UpTimes;
            }
            arr.Add(item);
        }
        return arr;
    }

    /// <summary>本次动过的 group 15 任务值（推 pf 1026 用）。</summary>
    private IEnumerable<KeyValuePair<uint, int>> TaskDeltaOf(Player player, GachaPool pool)
    {
        foreach (int taskId in new[]
                 {
                     TaskTotal, TaskProtectBegin + pool.Id, TaskPrePoolBegin + pool.Id,
                     TaskProtectUpBegin + pool.Id,
                     TaskCardSingle, TaskCardMul,
                 })
        {
            yield return new(Player.ComposeTaskId(TaskGroup, taskId),
                             player.GetTask(TaskGroup, taskId));
        }
    }

    // =================================================================
    //  GetLastGacha
    // =================================================================

    /// <summary>
    /// 查上一次抽卡记录。上行是**裸布尔**（<c>SendCmd('GetLastGacha', true/false)</c>），
    /// 所以 <paramref name="param"/> 是一个 JSON 布尔而不是对象。
    ///
    /// 没有记录时回 <c>{"err":"error.gacha.nolast"}</c> —— 客户端 <c>UI_Gacha.lua:2348</c>
    /// 特判它，只弹一个提示，不会崩。
    /// </summary>
    private Task OnGetLastGacha(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;
        if (param is not JsonValue value || !value.TryGetValue<bool>(out bool confirm))
        {
            Reply(session, FnGetLastGacha, new JsonObject { ["err"] = ErrNoLast });
            return Task.CompletedTask;
        }

        RefreshFreeState(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (HandleNewcomerLast(session, player, confirm)) return Task.CompletedTask;

        var last = ReadLast(player);
        if (last is null)
        {
            Log.Info($"[session {session.Id}] GetLastGacha（param={param?.ToJsonString()}）→ 无记录，回 {ErrNoLast}");
            Reply(session, FnGetLastGacha, new JsonObject { ["err"] = ErrNoLast });
            return Task.CompletedTask;
        }

        Log.Info($"[session {session.Id}] GetLastGacha（param={param?.ToJsonString()}）→ " +
                 $"回放上次结果（bTen={last["bTen"]?.ToJsonString()}）");
        Reply(session, FnGetLastGacha, last);
        return Task.CompletedTask;
    }

    // =================================================================
    //  SetCardCustomUp
    // =================================================================

    /// <summary>
    /// 自选 UP：<c>{nId, tbItems:[cardId1, cardId2]}</c>。
    /// 存法照 <c>GachaCommon:SetCustomUp</c>（<c>:455..516</c>）：
    /// <c>val = card1 | (card2 &lt;&lt; 16)</c> 存进 <c>(15, 6001 + nId)</c>，
    /// 并把 <c>(15, 7001 + nId)</c> 的选择次数 +1。
    /// </summary>
    private Task OnSetCardCustomUp(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var request = param as JsonObject;
        if (!GachaRequestLimits.TryInteger(request?["nId"], out int poolId) || poolId <= 0)
            return RejectCustomSelection(session);
        var items = request?["tbItems"] as JsonArray;
        if (items is null || items.Count is < 1 or > 2
            || !GachaRequestLimits.TryInteger(items[0], out int card1)) return RejectCustomSelection(session);
        int card2 = 0;
        if (items.Count > 1 && !GachaRequestLimits.TryInteger(items[1], out card2)) return RejectCustomSelection(session);

        int value = (card1 & 0xFFFF) | ((card2 & 0xFFFF) << 16);
        int choiceKey = TaskCardCustomChoice + poolId;
        int timeKey = TaskCardCustomTime + poolId;

        var pool = _table.Get(poolId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (pool is null || items?.Count != (pool.JudgeType == 4 ? 1 : 2)
            || card1 is <= 0 or > 0xFFFF || card2 is < 0 or > 0xFFFF
            || !ValidChoice(pool, value) || pool.SetUpNum <= 0
            || player.GetTask(TaskGroup, timeKey) >= pool.SetUpNum)
            return RejectCustomSelection(session);

        if (choiceKey <= 0 || timeKey <= 0 || choiceKey > 0xFFFF || timeKey > 0xFFFF)
        {
            // Player.ComposeTaskId 只留低 16 位
            Log.Warn($"[session {session.Id}] SetCardCustomUp nId={poolId} 的任务键越界（{choiceKey}/{timeKey}），忽略");
            return RejectCustomSelection(session);
        }

        player.SetTask(TaskGroup, choiceKey, value);
        player.SetTask(TaskGroup, timeKey, player.GetTask(TaskGroup, timeKey) + 1);
        _ctx.Store.Save(player);

        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, choiceKey), value),
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, timeKey),
                                        player.GetTask(TaskGroup, timeKey)),
        });

        Log.Info($"[session {session.Id}] SetCardCustomUp nId={poolId} card1={card1} card2={card2} " +
                 $"→ (15,{choiceKey})={value}（次数 {player.GetTask(TaskGroup, timeKey)}）");
        // S2C.SetCardCustomUp hides Blocking and refreshes the pool after task updates.
        Reply(session, FnSetCardCustomUp, new JsonObject { ["nId"] = poolId });
        return Task.CompletedTask;
    }

    private Task RejectCustomSelection(ClientSession session)
    {
        _ctx.LuaCalls.CallClient(session, GmConsoleFeature.DoLuaStringCmd,
            new JsonObject { ["luaStr"] = "Game.Blocking.Hide()" });
        return Task.CompletedTask;
    }

    // =================================================================
    //  存档 / 工具
    // =================================================================

    /// <summary>把本次结果存进 <c>Player.Extra["gacha"]</c>，供 <c>GetLastGacha</c> 回放。</summary>
    internal static List<int[]> PlanBonusRewards(GachaPool pool, int previous, int draws)
    {
        var rewards = new List<int[]>();
        void Add(int[]? tuple, long multiplier)
        {
            if (tuple is not { Length: >= 4 } || multiplier <= 0) return;
            long quantity = (tuple.Length >= 5 ? tuple[4] : 1L) * multiplier;
            if (tuple.Take(4).Any(n => n <= 0) || quantity <= 0 || quantity > int.MaxValue)
                throw new InvalidDataException("抽卡伴随奖励配置无效");
            rewards.Add([tuple[0], tuple[1], tuple[2], tuple[3], (int)quantity]);
        }
        Add(pool.GetItem, draws);
        long before = Math.Max(0, previous), after = before + draws;
        if (pool.UpBoxNum > 0)
            Add(pool.UpBox, pool.RotateVersion > 0 ? after / pool.UpBoxNum - before / pool.UpBoxNum
                : before < pool.UpBoxNum && after >= pool.UpBoxNum ? 1 : 0);
        return rewards;
    }

    private static void WriteLast(Player player, bool ten, JsonArray awards, JsonArray? companion = null)
    {
        player.Extra[ExtraKey] = new JsonObject
        {
            ["bTen"] = ten,
            ["tbAwards"] = awards.DeepClone(),
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        if (companion is not null) ((JsonObject)player.Extra[ExtraKey]!)["getItem"] = companion.DeepClone();
    }

    /// <summary>读上一次结果（形状与 <c>S2C.Lottery</c> 相同，可直接回给客户端）。</summary>
    internal static JsonObject? ReadLast(Player player)
    {
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is not JsonObject obj)
            return null;
        if (obj["tbAwards"] is not JsonArray arr || arr.Count == 0)
            return null;
        var result = new JsonObject
        {
            ["bTen"] = obj["bTen"]?.DeepClone() ?? false,
            ["tbAwards"] = arr.DeepClone(),
        };
        if (obj["getItem"] is JsonArray companion) result["getItem"] = companion.DeepClone();
        return result;
    }

    private void RefreshFreeState(ClientSession session, Player player, long now)
    {
        var tasks = new List<KeyValuePair<uint, int>>();
        var pools = _table.OpenAt(now).ToArray();
        bool changed = GachaFreeDraws.Refresh(player, pools, now, tasks);
        changed |= GachaVersionState.Refresh(player, pools, tasks);
        if (changed) _ctx.Store.Save(player);
        if (tasks.Count > 0) GameNotify.PushTaskValues(session, tasks);
    }

    void IGamePulseFeature.OnPlayerPulse(ClientSession session, Player player)
        => RefreshFreeState(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private void Reply(ClientSession session, string function, JsonObject payload)
        => _ctx.LuaCalls.CallClientFunction(session, function, payload);

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try { return node.GetValue<int>(); }
        catch { return double.TryParse(node.ToString(), out var d) ? (int)d : fallback; }
    }

    private static bool ReadBool(JsonNode? node)
    {
        if (node is null)
            return false;
        try { return node.GetValue<bool>(); }
        catch
        {
            var s = node.ToString();
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
        }
    }

    /// <summary>本功能加载到的配置（测试与日志用）。</summary>
    internal GachaTable Table => _table;
}
