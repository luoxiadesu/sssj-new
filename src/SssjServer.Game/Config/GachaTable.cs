using System.Globalization;
using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>卡池里的一张卡（<c>Gacha/Pools/&lt;Pool&gt;.txt</c> 的一行）。</summary>
public sealed class GachaCard
{
    /// <summary>池内卡 id（<c>ID</c> 列）。回包 <c>tbAwards[i].nId</c> 用它。</summary>
    public int Id { get; init; }

    /// <summary><c>GDPLN</c> 列，形如 <c>[1-1-1-1]</c>（实测只有 4 位，没有 N）。</summary>
    public int G { get; init; }
    public int D { get; init; }
    public int P { get; init; }
    public int L { get; init; }

    /// <summary>
    /// 星级 = <c>min(L, 4)</c>。取证 <c>GachaCommon.lua:137</c>
    /// <c>local star = tbConfig.tbGDPLN[4] >= 4 and 4 or tbConfig.tbGDPLN[4]</c>。
    /// </summary>
    public int Star => L >= 4 ? 4 : L;

    /// <summary>配置原始权重（<c>Rate</c> 列）。</summary>
    public int RawRate { get; init; }

    /// <summary>
    /// **星级内归一化后**的概率。取证 <c>GachaCommon.lua:152..156</c>：
    /// <c>tbData.nRate = tbData.nRate / nTotal</c>，其中 <c>nTotal = tbInfo.tbStar[nStar]</c>
    /// 是该星级全部卡的 <c>Rate</c> 之和 —— 即「先按星级概率抽星级，再在星级内按此概率抽卡」。
    /// </summary>
    public double RateInStar { get; internal set; }

    /// <summary>UP 标记（<c>UpFlag</c> 列，1 = UP 卡）。</summary>
    public int UpFlag { get; init; }

    /// <summary>剧情文件（<c>Story</c> 列，非空表示抽到会播剧情）。</summary>
    public string Story { get; init; } = "";

    /// <summary>是否在「概率预览」里展示（<c>IsPreview</c> 列）。</summary>
    public bool IsPreview { get; init; }

    /// <summary>(G,D,P,L) 四元组，方便发奖。</summary>
    public int[] Gdpl => new[] { G, D, P, L };

    public override string ToString() => $"#{Id} ({G},{D},{P},{L}) star={Star} rate={RawRate}";
}

/// <summary>
/// 一档星级权重（<c>CardStarSeed.txt</c> 的 <c>&lt;i&gt;StarCardSeed</c> 列里的一对
/// <c>[times, weight]</c>）。语义见 <see cref="CardStarSeedRow"/>。
/// </summary>
public sealed record StarSeedStep(int Times, int Weight);

/// <summary>
/// <c>CardStarSeed.txt</c> 的一行：四个星级各自的「抽数 → 权重」阶梯。
///
/// 取证 <c>GachaCommon:LoadCardStarSeedConfig</c>（GachaCommon.lua:170..195）：
/// 每列形如 <c>[1,1200],[50,1200]</c>，按 <c>,</c> 拆成两两一组
/// （奇数项 = <c>times</c>，偶数项 = <c>weight</c>）。
///
/// 取权重的规则见 <c>GachaCommon:GetStarRate</c>（GachaCommon.lua:281..300）：
/// 对每个星级，从阶梯里找**第一个** <c>times &lt;= step.Times</c> 的档取其 weight，
/// 找不到就**该星级不参与本次抽取**。
/// </summary>
public sealed class CardStarSeedRow
{
    public int Id { get; init; }

    /// <summary>四个星级的阶梯，下标 0..3 对应 1★..4★。</summary>
    public List<StarSeedStep>[] Steps { get; } =
    {
        new(), new(), new(), new(),
    };

    /// <summary><c>ChangeRate</c> 列（只用于界面文案的 string.format，服务端不用）。</summary>
    public string ChangeRate { get; init; } = "";

    /// <summary>
    /// 四个星级里**最大的档位 <c>Times</c>**（没有任何阶梯时为 0）。
    ///
    /// ★★★ 用途：阶梯的规则是「找第一个 <c>times &lt;= step.Times</c> 的档」，
    /// 一旦实际抽数超过所有档位，<see cref="WeightsAt"/> 就会返回**全 0**。
    /// 现网五行的最大档位只到 50 或 100，而**武器池不清零保底计数**
    /// （`WeaponForge.lua` 全文无 <c>ProtectNum</c>）⇒ 第 101 抽起权重全 0
    /// ⇒ 静默退化成「恒出池内最低星级」。
    /// 调用方（<see cref="Handlers.WeaponForgeFeature"/>）用它把查询次数夹到最后一档。
    /// ★ 角色卡域不受影响：那边出 4★ 会把计数清零，永远碰不到上限。
    /// </summary>
    public int MaxTimes
    {
        get
        {
            int max = 0;
            foreach (var steps in Steps)
            {
                foreach (var step in steps)
                {
                    if (step.Times > max)
                        max = step.Times;
                }
            }
            return max;
        }
    }

    /// <summary>
    /// 按「已抽次数」算出四个星级的权重。返回的数组下标 0..3 = 1★..4★，
    /// 值 0 表示该星级本次不参与（阶梯里没有覆盖该次数的档，或权重就是 0）。
    /// </summary>
    public int[] WeightsAt(int times)
    {
        var w = new int[4];
        for (int i = 0; i < 4; i++)
        {
            foreach (var step in Steps[i])
            {
                if (times <= step.Times)
                {
                    w[i] = step.Weight;
                    break;
                }
            }
        }
        return w;
    }
}

/// <summary>一个角色卡池（<c>gacha.txt</c> 里 <c>Type == 1</c> 的一行 + 它的 Pool 文件）。</summary>
public sealed class GachaPool
{
    public int Id { get; init; }

    /// <summary><c>Name</c> 列（本地化 key，日志用）。</summary>
    public string Name { get; init; } = "";

    /// <summary>Pool 文件名（<c>Pool</c> 列，如 <c>card_1</c>）。</summary>
    public string PoolFile { get; init; } = "";

    /// <summary>
    /// 消耗类型（<c>CashType</c> 列）：**0 道具+钻石 / 1 只道具 / 3 钻石**
    /// （表头第 2 行原文「货币种类0道具+钻石 1只道具 3钻石」；实测数据里还有 2）。
    /// 0/1 时 <see cref="CostOneItem"/>/<see cref="CostTenItem"/> 是四元组；
    /// 其它值时 <see cref="CostOneMoney"/>/<see cref="CostTenMoney"/> 是货币数量，
    /// 货币 id 就是 <c>CashType</c> 本身（取证 <c>UI_Gacha.lua:651</c>
    /// <c>MoneyLogic:GetInfo(self.tbCurrent.nCash)</c>）。
    /// </summary>
    public int CashType { get; init; }
    public int ExchangeRate { get; init; }
    public bool CanUseFreeDiamond { get; init; } = true;

    /// <summary>单抽消耗的道具四元组（<c>CashType</c> 0/1 时有效）。</summary>
    public int[]? CostOneItem { get; init; }

    /// <summary>十连消耗的道具四元组（<c>CashType</c> 0/1 时有效；为空表示该池没有十连）。</summary>
    public int[]? CostTenItem { get; init; }

    /// <summary>单抽消耗的货币数量（<c>CashType</c> 非 0/1 时有效）。</summary>
    public int CostOneMoney { get; init; }

    /// <summary>十连消耗的货币数量。</summary>
    public int CostTenMoney { get; init; }

    /// <summary>
    /// 判断类型（<c>JudgeType</c>）：角色池 0 普通 / 1 特殊 / 2 新人补给 / 3 自选UP /
    /// 4 自选必得单抽（表头第 2 行原文）。
    /// ★ <c>JudgeType == 2</c> 时客户端**跳过保底演出判定**（<c>UI_Gacha.lua:1326</c>
    /// <c>if self.tbCurrent.JudgeType ~= 2 then</c>）。
    /// </summary>
    public int JudgeType { get; init; }
    public int SetUpNum { get; init; }

    /// <summary>1 隐藏十连 / 2 隐藏单抽（<c>HideTenBtn</c>）。</summary>
    public int HideTenBtn { get; init; }

    /// <summary>保底抽数（<c>ProtectNum</c>，实测角色池普遍 50）。0 = 无保底。</summary>
    public int ProtectNum { get; init; }

    /// <summary>UP 保底抽数（<c>ProtectUpNum</c>，实测 100/150）。</summary>
    public int ProtectUpNum { get; init; }

    /// <summary>获得 UP 自选箱所需抽数（<c>UpBoxNum</c>，实测 300）。</summary>
    public int UpBoxNum { get; init; }

    /// <summary>UP 自选箱物品（<c>UpBox</c> 列，四元组+N）。</summary>
    public int[]? UpBox { get; init; }
    public int[]? GetItem { get; init; }
    public int FreeType { get; init; }
    public int FreeCount { get; init; }
    public long FreeStart { get; init; }
    public long FreeEnd { get; init; }

    public bool FreeAvailableAt(long now, int used) => FreeType is 1 or 2 && FreeCount > 0
        && used >= 0 && used < FreeCount
        && !(FreeStart > 0 && FreeEnd > 0 && (now < FreeStart || now > FreeEnd));

    /// <summary>用哪一行 <c>CardStarSeed.txt</c>（<c>CardStarSeed</c> 列）。</summary>
    public int CardStarSeedId { get; init; }

    /// <summary>轮替版本（<c>RotateVersion</c>）。&gt;0 时 UpBox 按 <c>% UpBoxNum</c> 反复给。</summary>
    public int RotateVersion { get; init; }

    /// <summary>开放起止（unix 秒）。<see cref="StartTime"/> == 0 表示没配时间。</summary>
    public long StartTime { get; init; }
    public long EndTime { get; init; }

    /// <summary>本池全部卡（key = 池内卡 id）。</summary>
    public Dictionary<int, GachaCard> Cards { get; } = new();

    /// <summary>星级 → 该星级全部卡的 <c>Rate</c> 之和（归一化的分母）。</summary>
    public Dictionary<int, int> StarTotal { get; } = new();

    /// <summary>UP 卡（<c>UpFlag == 1</c>）。</summary>
    public List<GachaCard> UpCards { get; } = new();

    /// <summary>
    /// 该档期此刻是否在开放期内。
    /// ★ 边界口径**照抄** <c>GachaCommon:GetCurPools</c>（GachaCommon.lua:224）：
    /// <c>now &gt;= info.nStart and now &lt; info.nEnd</c> —— 左闭右开，
    /// 不是两边都开（开始那一秒算开放、结束那一秒不算）。
    /// </summary>
    public bool IsOpenAt(long now) => now >= StartTime && now < EndTime;

    /// <summary>
    /// 该档期是否**对这个玩家可选**。这是 <c>GachaCommon:GetCurPools</c>
    /// （GachaCommon.lua:219..249）里 <c>JudgeType</c> 那一层判定的移植 ——
    /// 客户端界面就是按它决定「这个池出不出现在可抽列表里」的：
    /// <code>
    /// if info.JudgeType ~= 0 then
    ///     if info.JudgeType == 3 or info.JudgeType == 4 then
    ///         self:SetCustomUp(info.nId, info); table.insert(tb, info)      -- 自选：直接可见
    ///     else
    ///         local g,d,p,l                                                 -- 1 特殊 / 2 新人补给
    ///         if info.JudgeType == 1 then g,d,p,l = table.unpack(info.nOne)
    ///         elseif info.JudgeType == 2 then g,d,p,l = table.unpack(info.nTen) end
    ///         local nCount = GetItemCount(g,d,p,l)
    ///         if nCount &gt;= 1 and SystemUnlock:CheckLevel(1, 6, 1) then      -- ★ 双条件
    ///             table.insert(tb, info)
    ///         end
    ///     end
    /// else
    ///     table.insert(tb, info)                                            -- 0 普通：直接可见
    /// end
    /// </code>
    /// 即 <c>JudgeType</c> 1/2 的池要求**持有对应门票 ≥ 1** 且**已通过 1-6 普通难度**
    /// （<c>SystemUnlock:CheckLevel</c> 读的就是服务端下发的 <c>levellist</c>，
    /// 见 <see cref="SystemUnlockTable"/>）。服务端照同一口径校验，
    /// 客户端界面里看不到的池，服务端也拒绝 —— 两边一致才不会出现「能点但被拒」。
    /// </summary>
    /// <param name="hasTicket">该池 <c>JudgeType</c> 1/2 所需门票的持有数量。</param>
    /// <param name="passedFirstChapter">是否已通过 1-6 普通难度。</param>
    public bool IsSelectable(int hasTicket, bool passedFirstChapter) => JudgeType switch
    {
        0 => true,                                  // 普通池
        3 or 4 => true,                             // 自选 UP / 自选必得
        _ => hasTicket >= 1 && passedFirstChapter,  // 1 特殊 / 2 新人补给
    };

    /// <summary>
    /// <c>JudgeType</c> 1/2 时「门票」是哪一个四元组：1 看 <c>nOne</c>（单抽消耗）、
    /// 2 看 <c>nTen</c>（十连消耗）——GachaCommon.lua:231..234。
    /// 其它 JudgeType 返回 null（不需要门票）。
    /// </summary>
    public int[]? TicketItem => JudgeType switch
    {
        1 => CostOneItem,
        2 => CostTenItem,
        _ => null,
    };

    /// <summary>该池是否支持十连（十连消耗配置存在）。</summary>
    public bool HasTen => CashType is 0 or 1 ? CostTenItem is not null : CostTenMoney > 0;

    public override string ToString()
        => $"#{Id} {Name} pool={PoolFile} cash={CashType} judge={JudgeType} " +
           $"cards={Cards.Count} seed={CardStarSeedId} protect={ProtectNum}";
}

/// <summary>
/// 抽卡配置：<c>gacha.txt</c>（角色池）+ <c>Gacha/Pools/*.txt</c>（卡组）+ <c>CardStarSeed.txt</c>（星级概率）。
///
/// <para><b>为什么可以照抄客户端</b></para>
/// <c>extracted/lua/GachaCommon.lua</c>（517 行）是**客户端与原服共用的共享模块**
/// （AGENTS.md §2.5），概率算法整份在包内。本类是它 <c>LoadConfig</c>（:50..166）+
/// <c>LoadCardStarSeedConfig</c>（:168..196）的 C# 移植，逐行对齐：
/// <list type="number">
///   <item>只收 <c>Type == 1</c> 的行（角色池；<c>Type == 0</c> 是武器池，本轮不做）；</item>
///   <item>按星级（<c>min(L,4)</c>）汇总 <c>Rate</c> 到 <see cref="GachaPool.StarTotal"/>；</item>
///   <item>再把每张卡的 <c>Rate</c> **除以所在星级的合计**，得到
///     <see cref="GachaCard.RateInStar"/> —— 抽取是「两级」的：先星级、再星级内选卡。</item>
/// </list>
///
/// <para><b>★ 一个池 id 对应「多个时间段」，不是一行</b></para>
/// 这是最容易写错的一处：<c>gacha.txt</c> 里**同一个 ID 会出现很多行**
/// （实测 ID=1 有 29 行、ID=5 有 11 行），每行是该池的一个**轮替档期**，
/// 各自有独立的 <c>Time</c> 与 <c>Pool</c>（卡组也换）。客户端就是这么存的：
/// <code>
/// -- GachaCommon.lua:158..159
/// self.tbConf[tbInfo.nId] = self.tbConf[tbInfo.nId] or {};
/// table.insert(self.tbConf[tbInfo.nId], tbInfo)
/// </code>
/// 而 <c>GetCurPools</c> 再从该 id 的段列表里挑「当前时间落在其中」的那一段。
/// 用 <c>Dictionary&lt;int, GachaPool&gt;</c> 覆盖式存会让**过期池看起来还开着**
/// （ID=1 的最后一段是 <c>{202110190800, 207703012359}</c>，一路开到 2077 年），
/// 于是服务端会拿错卡组、也不会回 <c>error.gacha.notexists</c>。
/// 所以这里是 <c>id → List&lt;GachaPool&gt;</c>，取用一律走 <see cref="Get(int, long)"/>。
///
/// <para><b>现网数据的两个事实</b></para>
/// <list type="bullet">
///   <item><c>gacha.txt</c> 的角色池行归并后是 <b>53 个池 id / 100 个档期</b>，
///     按「当前时间」筛选后**只有 4 个池**在开放期内（多数档期停在 2019-2021）：
///     <code>
///     id=1   card_1_base_card_61_ZH  JudgeType=0 CashType=0 单抽[9-12-1-1-1] 十连[9-13-1-1-1] seed=1 ProtectNum=50
///     id=66  card_promise            JudgeType=1 CashType=1 单抽[9-14-1-1-1] 无十连          seed=5
///     id=77  card_platinum_ZH_20210310 JudgeType=1 CashType=1 单抽[9-15-1-1-1] 无十连        seed=4（只出 4★）
///     id=666 card_1_20210303         JudgeType=2 CashType=1 十连[9-19-1-1-1] 无单抽          seed=1
///     </code>
///     ⇒ **验收首选 id=1**：它是普通池（<c>JudgeType=0</c> 无额外门槛）、
///     既有单抽也有十连、还带 50 抽保底，四条路径一次能验全。</item>
///   <item><c>CardStarSeed.txt</c> 只有 5 行、每列只有两档且**两档权重相同**
///     （如 <c>[1,1200],[50,1200]</c>）⇒ 本版本的星级概率**不随抽数递增**，
///     保底完全靠 <see cref="GachaPool.ProtectNum"/> 的「第 N 抽强制出 4★」，
///     而不是靠权重爬坡。</item>
/// </list>
/// </summary>
public sealed class GachaTable
{
    /// <summary>主表文件名（客户端口径 "Gacha/gacha.txt"，本目录扁平化）。</summary>
    public const string FileName = "gacha.txt";

    /// <summary>星级概率表文件名（客户端口径 "Gacha/CardStarSeed.txt"）。</summary>
    public const string SeedFileName = "CardStarSeed.txt";

    /// <summary>角色池的 <c>Type</c> 值（0 武器池 / 1 角色池，gacha.txt 表头第 2 行）。</summary>
    public const int TypeCard = 1;

    /// <summary>池 id → 该池的**全部档期**（顺序即文件顺序）。</summary>
    private readonly Dictionary<int, List<GachaPool>> _pools = new();
    private readonly Dictionary<int, CardStarSeedRow> _seeds = new();
    private GachaRotation? _rotation;

    private GachaTable() { }

    /// <summary>永久空表（缺表降级：抽卡域一律回 <c>error.gacha.notexists</c>）。</summary>
    public static GachaTable Empty { get; } = new();

    /// <summary>池 id 的个数（**不是**配置行数）。</summary>
    public int Count => _pools.Count;

    /// <summary>全部档期（按 id 再按文件顺序）。</summary>
    public IEnumerable<GachaPool> All => _pools.OrderBy(kv => kv.Key).SelectMany(kv => kv.Value);

    /// <summary>某个池 id 的全部档期。</summary>
    public IReadOnlyList<GachaPool> Segments(int id)
        => _pools.TryGetValue(id, out var list) ? list : Array.Empty<GachaPool>();

    /// <summary>
    /// 取该池**当前生效**的那一段（口径同 <c>GachaCommon:GetCurPools</c>：
    /// <c>nStart &lt; now and nEnd &gt; now</c>）。没有任何一段在期内返回 null。
    /// </summary>
    public GachaPool? Get(int id, long now)
    {
        int? selected = _rotation?.SegmentAt(1, id, now);
        if (selected.HasValue)
            return selected > 0 ? Segments(id).ElementAtOrDefault(selected.Value - 1) : null;
        foreach (var seg in Segments(id))
        {
            if (seg.IsOpenAt(now))
                return seg;
        }
        return null;
    }

    /// <summary>该池是否存在（不管档期）。</summary>
    public bool Exists(int id) => _pools.ContainsKey(id);

    public CardStarSeedRow? GetSeed(int id) => _seeds.TryGetValue(id, out var s) ? s : null;

    /// <summary>此刻开放的池（每个 id 最多一段，口径同 <c>GachaCommon:GetCurPools</c>）。</summary>
    public IEnumerable<GachaPool> OpenAt(long now)
    {
        foreach (int id in _pools.Keys.OrderBy(k => k))
        {
            var seg = Get(id, now);
            if (seg is not null)
                yield return seg;
        }
    }

    /// <summary>从 setting 目录加载。缺表返回空表并打 WARN（不抛）。</summary>
    public static GachaTable Load(string settingDir)
    {
        var table = new GachaTable { _rotation = GachaRotation.For(settingDir) };

        // ---- CardStarSeed.txt ----
        var seedRaw = SettingTable.Load(Path.Combine(settingDir, SeedFileName));
        if (seedRaw is not null)
        {
            foreach (var row in seedRaw.Rows)
            {
                int id = seedRaw.Int(row, "ID", -1);
                if (id <= 0)
                    continue;
                var seed = new CardStarSeedRow { Id = id, ChangeRate = seedRaw.Str(row, "ChangeRate") };
                for (int i = 1; i <= 4; i++)
                {
                    foreach (var step in ParseSeedSteps(seedRaw.Str(row, $"{i}StarCardSeed")))
                        seed.Steps[i - 1].Add(step);
                }
                table._seeds[id] = seed;
            }
        }
        else
        {
            Log.Warn($"[setting] {SeedFileName} 缺失：抽卡的星级概率无法计算");
        }

        // ---- gacha.txt ----
        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失：抽卡域降级（一律回 error.gacha.notexists）");
            return table;
        }

        int poolFileMissing = 0;
        foreach (var row in raw.Rows)
        {
            if (raw.Int(row, "Type", -1) != TypeCard)
                continue;
            int id = raw.Int(row, "ID", -1);
            if (id <= 0)
                continue;

            int cash = raw.Int(row, "CashType");
            var (start, end) = ParseTimeRange(raw.Str(row, "Time"));

            var pool = new GachaPool
            {
                Id = id,
                Name = raw.Str(row, "Name"),
                PoolFile = raw.Str(row, "Pool"),
                CashType = cash,
                ExchangeRate = raw.Int(row, "ExchangeRate"),
                CanUseFreeDiamond = raw.Int(row, "CanUseFreeDiamond", 1) != 0,
                CostOneItem = cash is 0 or 1 ? ParseItem(raw.Str(row, "CostOne")) : null,
                CostTenItem = cash is 0 or 1 ? ParseItem(raw.Str(row, "CostTen")) : null,
                CostOneMoney = cash is 0 or 1 ? 0 : raw.Int(row, "CostOne"),
                CostTenMoney = cash is 0 or 1 ? 0 : raw.Int(row, "CostTen"),
                JudgeType = raw.Int(row, "JudgeType"),
                SetUpNum = raw.Int(row, "SetUpNum"),
                HideTenBtn = raw.Int(row, "HideTenBtn"),
                ProtectNum = raw.Int(row, "ProtectNum"),
                ProtectUpNum = raw.Int(row, "ProtectUpNum"),
                UpBoxNum = raw.Int(row, "UpBoxNum"),
                UpBox = ParseItem(raw.Str(row, "UpBox")),
                GetItem = ParseItem(raw.Str(row, "GetItem")),
                FreeType = raw.Int(row, "FreeType"),
                FreeCount = raw.Int(row, "FreeCount"),
                FreeStart = ParseTimeRange(raw.Str(row, "FreeTime")).Start,
                FreeEnd = ParseTimeRange(raw.Str(row, "FreeTime")).End,
                CardStarSeedId = raw.Int(row, "CardStarSeed"),
                RotateVersion = raw.Int(row, "RotateVersion"),
                StartTime = start,
                EndTime = end,
            };

            if (!LoadPoolCards(settingDir, pool))
                poolFileMissing++;

            // ★ 同一个 id 会有多段档期（见类注释），追加而不是覆盖
            if (!table._pools.TryGetValue(id, out var segs))
                table._pools[id] = segs = new List<GachaPool>();
            segs.Add(pool);
        }

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int open = table.OpenAt(nowUnix).Count();
        int rows = table._pools.Values.Sum(v => v.Count);
        Log.Info($"[setting] {FileName} 载入 {table._pools.Count} 个角色卡池 / {rows} 个档期" +
                 $"（星级概率行 {table._seeds.Count} 条，当前开放 {open} 个" +
                 (poolFileMissing > 0 ? $"，{poolFileMissing} 个档期的卡组文件缺失" : "") + "）");
        if (open > 0)
        {
            Log.Info($"[setting] 当前开放的池：" + string.Join("、", table.OpenAt(nowUnix)
                .Select(p => $"{p.Id}({p.PoolFile}, {p.Cards.Count} 张)")));
        }
        return table;
    }

    /// <summary>
    /// 加载一个池的卡组（<c>Gacha/Pools/&lt;Pool&gt;.txt</c> → 本目录扁平化为 <c>&lt;Pool&gt;.txt</c>）
    /// 并做星级内归一化。返回卡组文件是否存在。
    /// </summary>
    private static bool LoadPoolCards(string settingDir, GachaPool pool)
    {
        if (pool.PoolFile.Length == 0)
            return false;

        var raw = SettingTable.Load(Path.Combine(settingDir, pool.PoolFile + ".txt"));
        if (raw is null)
            return false;

        foreach (var row in raw.Rows)
        {
            int cardId = raw.Int(row, "ID", -1);
            if (cardId <= 0)
                continue;   // GachaCommon.lua:131 `if tbConfig.nId > 0 then` —— 0/空行不进 tbAll

            var gdpl = ParseItem(raw.Str(row, "GDPLN"));
            if (gdpl is null || gdpl.Length < 4)
                continue;

            var card = new GachaCard
            {
                Id = cardId,
                G = gdpl[0],
                D = gdpl[1],
                P = gdpl[2],
                L = gdpl[3],
                RawRate = raw.Int(row, "Rate"),
                UpFlag = raw.Int(row, "UpFlag"),
                Story = raw.Str(row, "Story"),
                IsPreview = raw.Int(row, "IsPreview") == 1,
            };

            if (pool.Cards.ContainsKey(cardId))
            {
                // 客户端这里是 Game.Logger.Error（GachaCommon.lua:133），服务端只 WARN
                Log.Warn($"[setting] 卡池 {pool.PoolFile}.txt 存在重复 ID={cardId}，后者覆盖前者");
            }
            pool.Cards[cardId] = card;

            pool.StarTotal.TryGetValue(card.Star, out int sum);
            pool.StarTotal[card.Star] = sum + card.RawRate;

            if (card.UpFlag == 1)
                pool.UpCards.Add(card);
        }

        // 星级内归一化（GachaCommon.lua:152..156）
        foreach (var card in pool.Cards.Values)
        {
            int total = pool.StarTotal.TryGetValue(card.Star, out int t) && t > 0 ? t : 1;
            card.RateInStar = (double)card.RawRate / total;
        }
        return true;
    }

    /// <summary>
    /// 解析 <c>[1,1200],[50,1200]</c> 这类星级阶梯：去掉方括号后按 <c>,</c> 拆，
    /// 奇数项是 times、偶数项是 weight（<c>GachaCommon.lua:180..191</c>）。
    /// </summary>
    internal static List<StarSeedStep> ParseSeedSteps(string value)
    {
        var result = new List<StarSeedStep>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        var nums = new List<int>();
        foreach (var tok in value.Replace("[", "").Replace("]", "").Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                nums.Add(n);
        }
        for (int i = 0; i + 1 < nums.Count; i += 2)
            result.Add(new StarSeedStep(nums[i], nums[i + 1]));
        return result;
    }

    /// <summary>
    /// 解析 <c>[9-12-1-1-1]</c> / <c>[1-1-1-1]</c> 这类物品四/五元组（客户端 <c>Parse2Item</c>）。
    /// 空串返回 null。第 6 位（商店物品 ID）会被一起解出来，调用方只取前 4~5 位。
    /// </summary>
    internal static int[]? ParseItem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var body = value.Trim().Trim('[', ']');
        if (body.Length == 0)
            return null;
        var parts = body.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var nums = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (!int.TryParse(p.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                return null;
            nums.Add(n);
        }
        return nums.Count >= 4 ? nums.ToArray() : null;
    }

    /// <summary>
    /// 解析 <c>{201808190000, 20191206115959}</c> 这类开放时间。
    /// 长度决定精度（<c>GetDate2Time</c>，Lib.lua:1304..1340）：
    /// 8 = yyyyMMdd / 10 = yyyyMMddHH / 12 = yyyyMMddHHmm / 14 = yyyyMMddHHmmss。
    /// 解析失败返回 (0,0)（视为「没配时间」⇒ <see cref="GachaPool.IsOpenAt"/> 恒 false）。
    /// </summary>
    internal static (long Start, long End) ParseTimeRange(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (0, 0);
        var body = value.Trim().Trim('{', '}');
        var parts = body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return (0, 0);
        return (ParseDate(parts[0]), ParseDate(parts[1]));
    }

    /// <summary>把 <c>yyyyMMdd[HH[mm[ss]]]</c> 数字串转成 unix 秒（本地时区，与客户端 <c>os.time</c> 一致）。</summary>
    internal static long ParseDate(string text)
    {
        var s = text.Replace("-", "").Trim();
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return 0;

        int year, mon, day, hour = 0, min = 0, sec = 0;
        switch (s.Length)
        {
            case 8:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                break;
            case 10:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                hour = int.Parse(s[8..10]);
                break;
            case 12:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                hour = int.Parse(s[8..10]); min = int.Parse(s[10..12]);
                break;
            case 14:
                year = int.Parse(s[..4]); mon = int.Parse(s[4..6]); day = int.Parse(s[6..8]);
                hour = int.Parse(s[8..10]); min = int.Parse(s[10..12]); sec = int.Parse(s[12..14]);
                break;
            default:
                return 0;
        }

        try
        {
            var dt = new DateTime(year, mon, day, hour, min, sec, DateTimeKind.Utc);
            return new DateTimeOffset(dt).ToUnixTimeSeconds();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }
}
