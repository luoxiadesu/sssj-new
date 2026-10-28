using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 武器抽卡（游戏内叫「武器补给」，配置/代码里叫「打造 Forge」）——
/// <c>sFunction = WeaponLogicMsg</c> 的 <b><c>nCmd = 0</c></b>。
///
/// <para><b>★★★ 为什么这是一个独立 Feature 而不是写进 <see cref="WeaponFeature"/></b></para>
/// <c>WeaponLogicMsg</c> 已被 <see cref="WeaponFeature"/> 用 <c>MapFunction</c> 占了主 handler。
/// 按 `AGENTS.md` §5 那条架构约定（第八轮踩出来的），**后来者绝不能再抢 <c>MapFunction</c>**
/// （覆盖式注册 ⇒ 会把武器养成整域吃掉，且**连 unhandled 日志都没有**）。
/// 所以本域用 <c>ctx.AddFunctionCoHandler</c> 追加协处理器，只认 <c>nCmd == 0</c>、
/// 其余一律返回 <c>false</c> 交回 <see cref="WeaponFeature"/>。
/// ★ 也**不能**用「功能域里的 static 字典」代替（并行单测会串，同一条教训）。
///
/// <para><b>1. 上行（真机取证，不是读代码推的）</b></para>
/// `work/server.log` 里第八轮留下的这条就是唯一判据：
/// <code>
/// [WRN] [session 1] unhandled sFunction=WeaponLogicMsg param={"nCmd":0,"Id":1,"isTen":true}
/// </code>
/// 构造点 <c>UI_WeaponSystemNEW.lua:726..730</c>，发出点 <c>:803 SendServerMsg</c>：
/// <code>
/// tbParam = { nCmd = 0, Id = self.tbConfig.nId, isTen = isTen }
/// function tbClass:SendServerMsg(tbParam)
///     Game.WQGachaCamera.ins:SetIsInAnimation(true)
///     Game.Blocking.Show(300);                                   -- ★★★ 见下
///     WeaponCommon:SendToServer('WeaponLogicMsg', tbParam, true);
/// end
/// </code>
/// ⇒ 字段只有三个：<c>nCmd</c> / <c>Id</c>(池 id) / <c>isTen</c>(bool)。
/// ★ 第八轮注释里写的 <c>Type</c> 字段**上行压根不存在**（那是把 <c>UI_CreateWeaponNew</c> 的
/// <c>nForgeType</c> 误当成上行字段了）。
///
/// <para><b>★★★ 2. 不回包会卡遮罩 5 分钟（推翻第八轮「不回包不影响」的结论）</b></para>
/// <c>SendToServer</c> 的第 3 参 <c>bNoWait = true</c> 只是让 <c>WeaponCommon</c> 自己不挂遮罩，
/// 但 <c>SendServerMsg</c> **外面那一层**紧接着调了 <c>Game.Blocking.Show(300)</c>
/// ⇒ 实际有 <b>300 秒</b>遮罩，只有 <c>S2C.WeaponLogicMsg</c> 第一句
/// <c>Game.Blocking.Hide()</c>（`S2CCall.lua:153`）能解。
/// ⇒ **这是本域必须实现的最硬理由**，也是一条通用教训：
/// 判断「不回包会不会卡」要看**整条调用链上所有 Blocking.Show**，不能只看发包函数的 bNoWait。
///
/// <para><b>3. 下行契约</b></para>
/// <code>
/// -- S2CCall.lua:152..166
/// function S2C.WeaponLogicMsg(tbParam)
///     Game.Blocking.Hide()
///     if tbParam and tbParam.nError > 0 then                     -- ★ nError 必须存在（数值比较）
///         if tbParam.nError == 20096 then
///             UI.Call('UI_WeaponSystemNEW', 'DialogReflashPool'); return   -- 池过期 → 提示刷新
///         end
///         ...弹 ui.Error.&lt;nError&gt;
///     elseif tbParam then
///         if tbParam.nCmd == 0 then
///             UI.Call('UI_WeaponSystemNEW', 'OnServerSuccess', tbParam.tbParam)
/// </code>
/// ⇒ 成功回包 <c>{ nError = 0, nCmd = 0, tbParam = [ {nGuid=…}, … ] }</c>。
///
/// <b><c>tbParam</c> 元素是「带 nGuid 字段的对象」，不是裸 guid</b>
/// （<c>UI_WeaponSystemNEW.lua:871 OnServerSuccess(nGuid)</c>）：
/// <code>
/// :903  local item = Game.EquipManager.GetWeapon(v.nGuid)     -- v 是元素 ⇒ 元素.nGuid
/// :925  Game.WQGachaCamera.ins:ShowWeapon(nGuid[i].nGuid, function() end)
/// :877  local bTen = self.nRewardData and #self.nRewardData > 1;   -- 十连由长度判定
/// </code>
///
/// <para><b>★★★ 4. 必须「先推 pf 1031、再回包」</b></para>
/// 客户端拿 <c>nGuid</c> 去 <c>Game.EquipManager.GetWeapon()</c> **重读本地缓存**（`:903/:925`），
/// 而那份缓存只由 pf 1031/1104 填充。顺序反了的症状：<c>item</c> 恒 nil ⇒
/// <c>rarity</c> 恒 0 ⇒ 演出走非 4★ 分支、<c>ShowWeapon</c> 拿不到武器（画面空转）。
/// 这与 `docs/05` §22.2（角色卡升级）是同一类陷阱。
///
/// <para><b>5. 算法（<c>WeaponForge.lua</c> 的移植，与角色卡抽卡的四处差异）</b></para>
/// 逐条见 <see cref="WeaponGachaTable"/> 类注释。这里只强调最致命的一条：
/// <b>星级取池表的 <c>Rarity</c> 列，不是从 GDPL 的 L 位算</b>
/// —— 武器池所有行的 L 都是 1，照角色卡写会让 4★ 永远抽不出来且不抛不报错。
///
/// <para><b>6. 现网状态（真机自报）</b></para>
/// 只有池 <b>1</b>「武器补给」开着，但开到 <b>2030-03-02</b>；池内 80 件
/// （1★5 / 2★20 / 3★25 / 4★30）；玩家单抽券与十连券各 999 张 ⇒ 可立刻验收。
/// 池 1 的 <c>FreeType</c> 为空 ⇒ <c>WeaponForge:CanFree</c> 恒 false ⇒
/// 「免费抽」与主城红点（<c>UI_Main.lua:803</c>）在池 1 上都走不到，
/// 但免费次数仍按配置实现（换池即生效）。
/// </summary>
public sealed class WeaponForgeFeature : IGameFeature, IGamePulseFeature
{
    /// <summary>与 <see cref="WeaponFeature.Function"/> 同名（协处理器挂在同一个 sFunction 上）。</summary>
    public const string Function = WeaponFeature.Function;

    /// <summary>本域只认这个 nCmd。</summary>
    public const int CmdForge = WeaponFeature.CmdForge;

    /// <summary>十连的抽数。</summary>
    public const int TenCount = 10;

    /// <summary>抽卡任务值组（<c>GachaCommon.GID</c>，真机自报 = 15）。</summary>
    public const int TaskGroup = GachaFeature.TaskGroup;

    /// <summary>
    /// 武器保底计数起点（<c>WeaponForge.PROTECT_TASK_BEGIN_WP</c>，`WeaponForge.lua:4`）。
    /// 键 = <c>(15, 1002 + 池id)</c> ⇒ 池 1 是 <c>(15, 1003)</c>。
    /// ★ 与角色卡的 <c>(15, 2 + 池id)</c> 是**两套独立计数**。
    /// </summary>
    public const int TaskProtectBeginWeapon = 1002;

    /// <summary>
    /// 武器免费次数起点（<c>GachaCommon.FREE_TIME_WEAPON</c>，真机自报 = 9001）。
    /// 键 = <c>(15, 9001 + 池id)</c>，`WeaponForge.lua:170`。
    /// </summary>
    public const int TaskFreeTimeWeapon = 9001;

    /// <summary>成功（`S2CCall.lua:153` 是数值比较 ⇒ 必须带 0）。</summary>
    public const int ErrOk = 0;

    /// <summary>通用失败码。</summary>
    public const int ErrGeneric = 1;

    /// <summary>
    /// 池子不在开放期 ⇒ 客户端走 <c>DialogReflashPool</c>（`S2CCall.lua:155`）提示刷新卡池，
    /// 而不是弹一个查不到文案的 <c>ui.Error.x</c>。
    /// </summary>
    public const int ErrPoolClosed = 20096;

    /// <summary>武器的 genre。</summary>
    public const uint WeaponGenre = 2;

    private GameFeatureContext _ctx = null!;
    private WeaponGachaTable _table = WeaponGachaTable.Empty;
    private GachaRequestLimits _requestLimits = null!;
    private readonly Random _rng = new();

    public string Name => "武器抽卡（WeaponLogicMsg nCmd=0「武器补给/打造」）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = WeaponGachaTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _requestLimits = new GachaRequestLimits(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.OnPlayerPrepare((session, player) =>
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (GachaFreeDraws.RefreshWeapons(player, _table.OpenAt(now), now, [])) ctx.Store.Save(player);
        });
        // ★ 协处理器，不抢 MapFunction（见类注释）
        ctx.AddFunctionCoHandler(Function, OnCommand);
    }

    /// <summary>只处理 <c>nCmd == 0</c>；其余交回 <see cref="WeaponFeature"/>。</summary>
    private Task<bool> OnCommand(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.FromResult(false);

        // ★ 真机上行可能是 JsonArray（本项目踩过：JsonNode["key"] 对 JsonArray 会抛）
        if (param is not JsonObject obj)
            return Task.FromResult(false);

        if (ReadInt(obj["nCmd"], -1) != CmdForge)
            return Task.FromResult(false);
        if (!GachaRequestLimits.TryInteger(obj["nCmd"], out int cmd) || cmd != CmdForge)
        {
            Fail(session, ErrGeneric);
            return Task.FromResult(true);
        }

        lock(player) Forge(session, player, obj);
        return Task.FromResult(true);
    }

    private void Forge(ClientSession session, Player player, JsonObject param)
    {
        if (!GachaRequestLimits.TryInteger(param["Id"], out int poolId) || poolId <= 0
            || !GachaRequestLimits.TryMode(param["isTen"], out bool ten))
        {
            Fail(session, ErrGeneric);
            return;
        }
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ---- ① 池子存在性与档期 ----
        var pool = _table.Get(poolId, now);
        if (pool is null)
        {
            // 分两种情况打日志，便于判断是「配置缺失」还是「档期过了」
            string why = _table.Exists(poolId)
                ? $"池 {poolId} 的 {_table.Segments(poolId).Count} 个档期都不在开放期"
                : $"池 {poolId} 在 gacha.txt(Type=0) 里不存在" +
                  "（★ 本地表可能比设备少行，见 AGENTS.md §5）";
            Log.Warn($"[session {session.Id}] 武器抽卡 Id={poolId} 被拒：{why} → nError={ErrPoolClosed}");
            Fail(session, ErrPoolClosed);
            return;
        }

        if (pool.Entries.Count == 0)
        {
            Log.Warn($"[session {session.Id}] 武器抽卡 Id={poolId} 的卡组 {pool.PoolFile}.txt 为空" +
                     $" → nError={ErrPoolClosed}");
            Fail(session, ErrPoolClosed);
            return;
        }

        // 该池没有十连配置时降级成单抽（与角色卡域同口径）
        if (ten && !pool.HasTen)
        {
            Log.Warn($"[session {session.Id}] 武器抽卡 Id={poolId} 没有十连配置，按单抽处理");
            ten = false;
        }

        int draws = ten ? TenCount : 1;
        if (!_requestLimits.HasRoom(player, WeaponGenre, draws))
        {
            Fail(session, 20082);
            return;
        }
        int operationKey = ten ? 10004 : 10003;
        if (player.GetTask(TaskGroup, operationKey) == int.MaxValue ||
            player.GetTask(TaskGroup, 1001) > int.MaxValue - draws ||
            player.GetTask(TaskGroup, TaskProtectBeginWeapon + pool.Id) > int.MaxValue - draws)
        {
            Fail(session, ErrGeneric);
            return;
        }
        RefreshFreeState(session, player, now);

        // ---- ② 扣费（免费次数优先，其次道具，其次免费钻石）----
        var paymentBefore = new GachaPaymentSnapshot(player);
        var spend = TrySpend(player, pool, ten, now);
        if (!spend.Ok)
        {
            Log.Warn($"[session {session.Id}] 武器抽卡 Id={poolId} bTen={ten} 消耗不足：{spend.Reason}");
            Fail(session, ErrGeneric);
            return;
        }

        var payment = paymentBefore.After(player);

        // ---- ③ 抽取 ----
        var seed = _table.GetSeed(pool.CardStarSeedId);
        var results = new List<WeaponDraw>(draws);
        for (int i = 0; i < draws; i++)
            results.Add(DrawOne(player, pool, seed));

        // ---- ④ 入账（每件武器一个独立实例 guid）----
        var granted = new RewardGrantResult();
        foreach (var r in results)
        {
            _ctx.Rewards.Grant(player,
                new[] { new[] { r.Entry.G, r.Entry.D, r.Entry.P, r.Entry.L, 1 } },
                RewardSource.Extra, granted);
        }

        player.SetTask(TaskGroup, 1001, player.GetTask(TaskGroup, 1001) + draws);
        player.SetTask(TaskGroup, operationKey, player.GetTask(TaskGroup, operationKey) + 1);
        var history = GachaHistory.Append(player,0,pool.Id,now,
            results.Select(r=>new[]{r.Entry.G,r.Entry.D,r.Entry.P,r.Entry.L}),payment);
        _ctx.Store.Save(player);

        GachaHistory.Push(session,0,history);

        // ---- ⑤ 推送：★★★ 必须在回包之前（客户端要按 nGuid 读本地缓存）----
        GameNotify.PushMoney(session, player, spend.ChangedMoney);
        if (spend.ChangedItems.Count > 0)
            GameNotify.PushItems(session, spend.ChangedItems);
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, spend.ChangedTasks.Concat(TaskDeltaOf(player, pool)));

        // ---- ⑥ 回包 ----
        var guids = CollectGuids(granted, results.Count);
        Reply(session, BuildForgePayload(guids));

        Log.Info($"[session {session.Id}] 武器抽卡 Id={poolId}（{pool.Name}/{pool.PoolFile}）" +
                 $"bTen={ten} 消耗={spend.Describe()} → " +
                 string.Join(" ", results.Select(r =>
                     $"{r.Entry.Id}({r.Entry.G},{r.Entry.D},{r.Entry.P},{r.Entry.L})★{r.Entry.Rarity}" +
                     (r.Entry.UpFlag == 1 ? "[UP]" : ""))) +
                 $"；guid=[{string.Join(",", guids)}]" +
                 $"；保底计数 ({TaskGroup},{TaskProtectBeginWeapon + poolId})=" +
                 $"{player.GetTask(TaskGroup, TaskProtectBeginWeapon + poolId)}");
    }

    // =================================================================
    //  抽取算法
    // =================================================================

    /// <summary>一次抽取的结果。</summary>
    internal sealed record WeaponDraw(WeaponGachaEntry Entry, int Times);

    /// <summary>
    /// 抽一次。维护保底计数 <c>(15, 1002+池id)</c>：
    /// <c>times = 已抽次数 + 1</c>（`WeaponForge.lua:211`），用它去 <c>CardStarSeed</c> 阶梯里取权重。
    ///
    /// ★ 武器池**没有** <c>ProtectNum</c>「第 N 抽强制 4★」那一层（`WeaponForge.lua` 全文无此列），
    /// 保底纯粹体现为权重随 times 爬坡 ⇒ 这里也就**不清零**计数
    /// （角色卡出 4★ 会清零，武器不会）。
    /// </summary>
    private WeaponDraw DrawOne(Player player, WeaponGachaPool pool, CardStarSeedRow? seed)
    {
        int protectKey = TaskProtectBeginWeapon + pool.Id;
        int times = player.GetTask(TaskGroup, protectKey) + 1;
        player.SetTask(TaskGroup, protectKey, times);

        int star = PickStar(pool, seed, times);
        var entry = PickEntry(pool, star);
        return new WeaponDraw(entry, times);
    }

    /// <summary>
    /// 按 <c>CardStarSeed.txt</c> 阶梯抽星级（`WeaponForge.lua:216..231` 的移植）。
    ///
    /// <para><b>★★★ 阶梯会「用尽」——必须夹到最后一档，否则第 101 抽起权重全 0</b></para>
    /// <c>WeightsAt</c> 的规则是「找第一个 <c>times &lt;= step.Times</c> 的档」，
    /// 而现网 <c>CardStarSeed.txt</c> 五行的最后一档 <c>Times</c> 只到 <b>50 或 100</b>
    /// （池 1 用的 seed=3 是 <c>[1,1200],[100,1200]</c> ⇒ 最大 100）。
    /// 武器池又**不清零**保底计数（见 <see cref="DrawOne"/>）⇒ <c>times</c> 会一路涨到 101、102…
    /// ⇒ 四个星级**都找不到匹配档** ⇒ 权重全 0 ⇒ 每次都走「退化」分支
    /// ⇒ 从此**恒定抽到池里最低星级（1★）**，且不抛不报错。
    ///
    /// 这不是客户端的行为：客户端 <c>WeaponForge:GetRandomWeaponRate</c> 只用于**界面显示概率**，
    /// 真正的抽取在原服，而原服显然不会让玩家抽到第 101 次就只出 1★。
    /// ⇒ 本实现把 <c>times</c> **夹到该 seed 的最大档位**（<see cref="CardStarSeedRow.MaxTimes"/>），
    /// 即「阶梯用尽后维持最后一档的权重」—— 与现网数据「两档权重完全相同」的事实
    /// （5 行 × 4 星级全部满足，见 <c>WeaponForgeFeatureTests</c> 的探针测试）
    /// 合起来意味着：**本版本的星级概率其实是恒定的**，夹取不会改变任何一次抽取的分布。
    /// ⇒ 所以这既修掉了「101 抽后全 1★」的 bug，又不引入与配置不符的行为。
    /// </summary>
    private int PickStar(WeaponGachaPool pool, CardStarSeedRow? seed, int times)
    {
        int[] weights;
        if (seed is not null)
        {
            // ★ 夹到最后一档（理由见上）。MaxTimes == 0 表示该 seed 没有任何阶梯，
            //   此时 WeightsAt 返回全 0，下面的退化分支会接住。
            int lookup = seed.MaxTimes > 0 ? Math.Min(times, seed.MaxTimes) : times;
            weights = seed.WeightsAt(lookup);
            // 池里没有该星级的武器 ⇒ 权重作废（否则会抽到空星级）
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
            int fallback = pool.StarTotal.Keys.DefaultIfEmpty(1).Min();
            Log.Warn($"[weapon-gacha] 池 {pool.Id} 星级权重合计为 0（seed={pool.CardStarSeedId}）" +
                     $"，退化到 {fallback}★");
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

    /// <summary>在指定星级内按原始权重抽一件武器。星级内没有时退化到全池。</summary>
    private WeaponGachaEntry PickEntry(WeaponGachaPool pool, int star)
    {
        var candidates = pool.Entries.Values.Where(e => e.Rarity == star).ToList();
        if (candidates.Count == 0)
        {
            Log.Warn($"[weapon-gacha] 池 {pool.Id} 里没有 {star}★ 的武器，退化到全池随机");
            candidates = pool.Entries.Values.ToList();
        }

        int sum = candidates.Sum(e => Math.Max(0, e.RawRate));
        if (sum <= 0)
            return candidates[_rng.Next(candidates.Count)];

        int roll = _rng.Next(sum);
        int acc = 0;
        foreach (var e in candidates)
        {
            acc += Math.Max(0, e.RawRate);
            if (roll < acc)
                return e;
        }
        return candidates[^1];
    }

    // =================================================================
    //  扣费
    // =================================================================

    /// <summary>扣费结果。</summary>
    internal sealed class SpendResult
    {
        public bool Ok { get; set; }
        public string Reason { get; set; } = "";
        public List<int> ChangedMoney { get; } = new();
        public List<Item> ChangedItems { get; } = new();
        public List<KeyValuePair<uint, int>> ChangedTasks { get; } = new();
        private readonly List<string> _notes = new();

        public void Note(string text) => _notes.Add(text);
        public string Describe() => _notes.Count == 0 ? "（免费）" : string.Join(" + ", _notes);
    }

    /// <summary>
    /// 扣费，顺序照 <c>WeaponForge:JudgeCurrency</c>（`WeaponForge.lua:186..205`）+
    /// <c>CanFree</c>（`:160`）：
    /// <list type="number">
    ///   <item>**免费次数**可用且是单抽 ⇒ 不扣任何东西，只 +1 免费计数
    ///     （`UI_WeaponSystemNEW.lua:737` 那条分支，十连不享受免费）。</item>
    ///   <item>券够 ⇒ 扣券。★★ <b>十连也只扣 1 张</b>
    ///     （`WeaponForge.lua:190` <c>local n = 1</c> 恒定），因为单抽券 <c>(7,7,1,4)</c>
    ///     与十连券 <c>(7,10,1,4)</c> 是两种不同道具。</item>
    ///   <item>券不够 ⇒ 用**免费钻石**按 <c>ExchangeRate × (isTen and 10 or 1)</c> 折算
    ///     （`WeaponForge.lua:196` 判的是 <c>eMoneyType_Free_Diamond</c>，
    ///     `UI_WeaponSystemNEW.lua:723` 算的 rate）。</item>
    /// </list>
    /// </summary>
    internal SpendResult TrySpend(Player player, WeaponGachaPool pool, bool ten, long now)
    {
        var r = new SpendResult();

        // ① 免费抽（只对单抽生效）
        if (!ten)
        {
            int freeKey = TaskFreeTimeWeapon + pool.Id;
            int used = player.GetTask(TaskGroup, freeKey);
            if (pool.FreeAvailableAt(now, used))
            {
                player.SetTask(TaskGroup, freeKey, used + 1);
                r.ChangedTasks.Add(new(Player.ComposeTaskId(TaskGroup, freeKey), used + 1));
                r.Note($"免费次数 {used + 1}/{pool.FreeCount}");
                r.Ok = true;
                return r;
            }
        }

        // ② 货币池（CashType != 0）
        if (pool.CashType != 0)
        {
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

        // ③ 道具池（CashType == 0）
        var cost = ten ? pool.CostTenItem : pool.CostOneItem;
        if (cost is null || cost.Length < 4)
        {
            r.Reason = $"CashType=0 但没有{(ten ? "十连" : "单抽")}道具配置";
            return r;
        }

        uint g = (uint)cost[0], d = (uint)cost[1], p = (uint)cost[2], l = (uint)cost[3];
        // ★ n 恒为 1，不是 cost[4] 也不是 10（WeaponForge.lua:190）
        const int need = 1;

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

        // ④ 券不够 → 免费钻石折算
        if (pool.CanUseFreeDiamond == 0)
        {
            r.Reason = $"券 {_ctx.ItemTemplates.NameOf(g, d, p, l)} 不足（有 {stack?.Count ?? 0}）" +
                       "，且该池 CanUseFreeDiamond=0 不允许用钻石";
            return r;
        }

        int rate = pool.ExchangeRate * (ten ? TenCount : 1);
        if (rate <= 0)
        {
            r.Reason = $"券 {_ctx.ItemTemplates.NameOf(g, d, p, l)} 不足（有 {stack?.Count ?? 0}）" +
                       $"，且 ExchangeRate={pool.ExchangeRate} 无法折算钻石价";
            return r;
        }

        int freeDiamond = player.GetMoney(MoneyType.FreeDiamond);
        if (freeDiamond < rate)
        {
            r.Reason = $"券 {_ctx.ItemTemplates.NameOf(g, d, p, l)} 不足（有 {stack?.Count ?? 0}）" +
                       $"，免费钻石也不足（有 {freeDiamond}，需 {rate}）";
            return r;
        }

        player.SetMoney(MoneyType.FreeDiamond, freeDiamond - rate);
        r.ChangedMoney.Add(MoneyType.FreeDiamond);
        r.Note($"免费钻石×{rate}");
        r.Ok = true;
        return r;
    }

    // =================================================================
    //  回包构造
    // =================================================================

    /// <summary>
    /// 从发奖结果里取出本次新造的武器 guid（按发放顺序）。
    ///
    /// <see cref="RewardGrantResult.ChangedItems"/> 里就是本次 <c>AddItem</c> 出来的实例，
    /// 且武器（genre 2）走 <c>RewardService.GrantOne</c> 的「每份一个独立实例」分支
    /// ⇒ 数量与抽数一致。这里再按 genre 过滤一次防御（万一将来发奖里混进别的东西）。
    /// </summary>
    internal static List<uint> CollectGuids(RewardGrantResult granted, int expected)
    {
        var guids = granted.ChangedItems
            .Where(i => i.Genre == WeaponGenre && i.Guid != 0)
            .Select(i => i.Guid)
            .ToList();

        if (guids.Count != expected)
        {
            Log.Warn($"[weapon-gacha] 期望 {expected} 个武器 guid，实际拿到 {guids.Count} 个" +
                     "（回包元素数与抽数不一致，客户端十连判定 #tbParam>1 可能受影响）");
        }
        return guids;
    }

    /// <summary>
    /// 组 <c>nCmd=0</c> 的 <c>tbParam</c>：<c>[ {nGuid=…}, … ]</c>。
    /// ★ 元素是**对象**不是裸数字（`UI_WeaponSystemNEW.lua:903` <c>v.nGuid</c>）；
    /// ★ 十连由 <c>#tbParam > 1</c> 判定（`:877`）⇒ 元素个数必须等于抽数。
    /// </summary>
    internal static JsonArray BuildForgePayload(IReadOnlyList<uint> guids)
    {
        var arr = new JsonArray();
        foreach (uint guid in guids)
            arr.Add(new JsonObject { ["nGuid"] = guid });
        return arr;
    }

    /// <summary>本次变动的任务值（保底计数；免费计数在 <see cref="TrySpend"/> 里已记）。</summary>
    private IEnumerable<KeyValuePair<uint, int>> TaskDeltaOf(Player player, WeaponGachaPool pool)
    {
        int protectKey = TaskProtectBeginWeapon + pool.Id;
        yield return new(Player.ComposeTaskId(TaskGroup, protectKey),
                         player.GetTask(TaskGroup, protectKey));
        foreach (int key in new[] { 1001, 10003, 10004 })
            yield return new(Player.ComposeTaskId(TaskGroup, key), player.GetTask(TaskGroup, key));
    }

    /// <summary>成功回包（带 tbParam）。</summary>
    private void Reply(ClientSession session, JsonNode tbParam)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["nError"] = ErrOk,
            ["nCmd"] = CmdForge,
            ["tbParam"] = tbParam,
        });

    /// <summary>失败回包。<c>nError</c> 必须 &gt; 0（`S2CCall.lua:153` 数值比较）。</summary>
    private void Fail(ClientSession session, int code)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new JsonObject
        {
            ["nError"] = code <= 0 ? ErrGeneric : code,
            ["nCmd"] = CmdForge,
        });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;

    private void RefreshFreeState(ClientSession session, Player player, long now)
    {
        var tasks = new List<KeyValuePair<uint, int>>();
        if (GachaFreeDraws.RefreshWeapons(player, _table.OpenAt(now), now, tasks)) _ctx.Store.Save(player);
        if (tasks.Count > 0) GameNotify.PushTaskValues(session, tasks);
    }

    void IGamePulseFeature.OnPlayerPulse(ClientSession session, Player player)
        => RefreshFreeState(session, player, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>读 bool；Lua/JSON 侧可能给 true/false 或 0/1。</summary>
    private static bool ReadBool(JsonNode? node)
    {
        if (node is null)
            return false;
        string s = node.ToString();
        if (bool.TryParse(s, out var b))
            return b;
        return int.TryParse(s, out var n) && n != 0;
    }
}
