using System.Globalization;
using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 武器池里的一件武器（<c>Gacha/Pools/&lt;Pool&gt;.txt</c> 的一行，武器池与角色池共用同一套列）。
///
/// ★★★ 与角色卡池（<see cref="GachaCard"/>）**最关键的区别：星级取自 <c>Rarity</c> 列，
/// 不是从 GDPL 的 L 位算**。
/// 取证 <c>WeaponForge.lua:77</c> <c>tbConfig.nRarity = tonumber( tbData.Rarity )</c>
/// ——而角色卡那边是 <c>GachaCommon.lua:137</c>
/// <c>local star = tbConfig.tbGDPLN[4] >= 4 and 4 or tbConfig.tbGDPLN[4]</c>（用 L）。
/// 实测武器池数据里两者**必然不一致**：`weapon_1_base_weapon_516_up_Mul.txt` 全部 78 行的
/// GDPL 第 4 位（L，武器的「阶」）都是 <b>1</b>，而 <c>Rarity</c> 列是 1..4
/// （例 <c>131 [2-1-3-1] Rarity=4</c> 群星道标是 4★ 武器但 L=1）。
/// ⇒ 若照抄角色卡的 <c>Star => L >= 4 ? 4 : L</c>，**所有武器都会被当成 1★**
/// ⇒ 星级权重全压在 1★ 那一档、4★ 永远抽不出来，且**不抛不报错**。
/// </summary>
public sealed class WeaponGachaEntry
{
    /// <summary>池内 id（<c>ID</c> 列）。日志用；回包不需要它（回包只给背包 guid）。</summary>
    public int Id { get; init; }

    /// <summary><c>GDPLN</c> 列，形如 <c>[2-1-2-1]</c>。武器的 genre 恒为 2。</summary>
    public int G { get; init; }
    public int D { get; init; }
    public int P { get; init; }
    public int L { get; init; }

    /// <summary>
    /// 星级，直接取 <c>Rarity</c> 列（<c>WeaponForge.lua:77</c>）。值域实测 1..4。
    /// </summary>
    public int Rarity { get; init; }

    /// <summary>配置原始权重（<c>Rate</c> 列）。</summary>
    public int RawRate { get; init; }

    /// <summary>
    /// **星级内归一化后**的概率（<c>WeaponForge.lua:93..97</c>：
    /// <c>tbData.nRate = tbData.nRate / nTotal</c>，<c>nTotal = tbInfo.tbStar[nStar]</c>）。
    /// 与角色卡同构：先掷星级、再在星级内按此概率掷具体武器。
    /// </summary>
    public double RateInStar { get; internal set; }

    /// <summary>UP 标记（<c>UpFlag</c> 列，1 = UP）。</summary>
    public int UpFlag { get; init; }

    /// <summary>是否进概率预览（<c>IsPreview</c> 列）。服务端不用，仅对齐配置。</summary>
    public bool IsPreview { get; init; }

    /// <summary>排序（<c>Sort</c> 列）。服务端不用。</summary>
    public int Sort { get; init; }

    /// <summary>(G,D,P,L) 四元组，发奖用。</summary>
    public int[] Gdpl => new[] { G, D, P, L };

    public override string ToString() => $"#{Id} ({G},{D},{P},{L})★{Rarity} rate={RawRate}";
}

/// <summary>
/// 一个武器池的一个档期（<c>gacha.txt</c> 里 <c>Type == 0</c> 的一行 + 它的 Pool 文件）。
/// </summary>
public sealed class WeaponGachaPool
{
    /// <summary>池 id（<c>ID</c> 列）。上行 <c>WeaponLogicMsg{nCmd=0, Id=&lt;这个&gt;}</c>。</summary>
    public int Id { get; init; }

    /// <summary><c>Name</c> 列（本地化 key，日志用）。现网池 1 是 <c>gacha.gacha.weapon.1.name</c> =「武器补给」。</summary>
    public string Name { get; init; } = "";

    /// <summary>卡组文件名（<c>Pool</c> 列，如 <c>weapon_1_base_weapon_516_up_Mul</c>）。</summary>
    public string PoolFile { get; init; } = "";

    /// <summary>
    /// 消耗类型（<c>CashType</c> 列）。<c>WeaponForge.lua:40..47</c>：
    /// <c>0</c> ⇒ <see cref="CostOneItem"/>/<see cref="CostTenItem"/> 是四元组（用道具抽）；
    /// 非 0 ⇒ <see cref="CostOneMoney"/>/<see cref="CostTenMoney"/> 是货币数量。
    /// 现网池 1 是 <b>0</b>（真机自报 <c>cash=0</c>）。
    /// </summary>
    public int CashType { get; init; }

    /// <summary>单抽消耗道具（<c>CostOne</c>，现网 <c>[7-7-1-4-1]</c>）。</summary>
    public int[]? CostOneItem { get; init; }

    /// <summary>十连消耗道具（<c>CostTen</c>，现网 <c>[7-10-1-4-1]</c>）。</summary>
    public int[]? CostTenItem { get; init; }

    /// <summary>单抽消耗货币量（<see cref="CashType"/> != 0 时有效）。</summary>
    public int CostOneMoney { get; init; }

    /// <summary>十连消耗货币量（<see cref="CashType"/> != 0 时有效）。</summary>
    public int CostTenMoney { get; init; }

    /// <summary>
    /// <c>JudgeType</c> 列。<c>1</c> = 自选 UP 池（<c>WeaponForge:SetCustomUp</c>，
    /// <c>WeaponForge.lua:272</c>）。现网池 1 是 <b>0</b>（真机自报 <c>judge=0</c>）
    /// ⇒ 自选 UP 这一层本轮不实现，见 <see cref="WeaponGachaTable"/> 类注释。
    /// </summary>
    public int JudgeType { get; init; }

    /// <summary><c>SetUpNum</c> 列（自选 UP 可选数量，JudgeType=1 才用）。</summary>
    public int SetUpNum { get; init; }

    /// <summary>星级权重表 id（<c>CardStarSeed</c> 列）。现网池 1 是 <b>3</b>（真机自报 <c>seed=3</c>）。</summary>
    public int CardStarSeedId { get; init; }

    /// <summary>
    /// 免费类型（<c>FreeType</c> 列）。<c>0</c>/空 = 无免费次数。
    /// 现网池 1 该列为空 ⇒ <c>WeaponForge:CanFree</c>（<c>:163</c>）第一个条件
    /// <c>tbData.freeType ~= 0</c> 不成立 ⇒ **恒 false**。
    /// </summary>
    public int FreeType { get; init; }

    /// <summary>免费次数上限（<c>FreeCount</c> 列）。</summary>
    public int FreeCount { get; init; }

    /// <summary>免费时段（<c>FreeTime</c> 列）。0 = 未配。</summary>
    public long FreeStart { get; init; }
    public long FreeEnd { get; init; }

    /// <summary>免费钻石折算比例（<c>ExchangeRate</c> 列，<c>WeaponForge.lua:60</c>）。</summary>
    public int ExchangeRate { get; init; }

    /// <summary>
    /// 是否允许用免费钻石顶替（<c>CanUseFreeDiamond</c> 列，<c>WeaponForge.lua:59</c>）。
    /// </summary>
    public int CanUseFreeDiamond { get; init; }

    /// <summary>档期开始/结束（<c>Time</c> 列，unix 秒，本地时区）。</summary>
    public long StartTime { get; init; }
    public long EndTime { get; init; }

    /// <summary>池内全部武器（key = 池内 id）。</summary>
    public Dictionary<int, WeaponGachaEntry> Entries { get; } = new();

    /// <summary>各星级的 <c>Rate</c> 合计（归一化用）。key = <see cref="WeaponGachaEntry.Rarity"/>。</summary>
    public Dictionary<int, int> StarTotal { get; } = new();

    /// <summary>十连是否可用（<see cref="CostTenItem"/> 或 <see cref="CostTenMoney"/> 有配）。</summary>
    public bool HasTen => CashType == 0 ? CostTenItem is not null : CostTenMoney > 0;

    /// <summary>
    /// 该档期此刻是否开放。
    ///
    /// ★★ 口径说明（`work/round9-mainsession-forensics.md` §1.4）：客户端**有两处判据且不一致** ——
    /// <list type="bullet">
    ///   <item><c>WeaponForge:GetWeaponForgeById</c>（<c>WeaponForge.lua:111</c>）：
    ///     <c>now &gt;= v.nStart and now &lt; v.nEnd</c>（**右开**）</item>
    ///   <item><c>UI_WeaponSystemNEW.lua:710</c>：<c>now &lt; nStart or now &gt; nEnd</c> 才拒绝
    ///     （即 **右闭**）</item>
    /// </list>
    /// 服务端取**交集**（右开），这样「服务端认为开着」⇒「客户端两处都认为开着」，
    /// 绝不会出现「服务端发了货、客户端却弹 DialogReflashPool」的错配。
    /// </summary>
    public bool IsOpenAt(long now) => StartTime > 0 && EndTime > 0 && now >= StartTime && now < EndTime;

    /// <summary>免费抽是否可用（<c>WeaponForge:CanFree</c>，<c>WeaponForge.lua:160..175</c>）。</summary>
    public bool FreeAvailableAt(long now, int usedFree)
    {
        if (FreeType is not (1 or 2) || FreeCount <= 0 || usedFree < 0)
            return false;
        // nFreeStart/nFreeEnd 都配了才判时段（Lua 侧 `if tbData.nFreeStart and tbData.nFreeEnd then`）
        if (FreeStart > 0 && FreeEnd > 0 && (now < FreeStart || now > FreeEnd))
            return false;
        return usedFree < FreeCount;
    }

    public override string ToString()
        => $"pool#{Id}({PoolFile}, {Entries.Count} 件, seed={CardStarSeedId})";
}

/// <summary>
/// 武器抽卡（「武器补给 / 打造」）配置：<c>gacha.txt</c> 的 <c>Type == 0</c> 行
/// + <c>Gacha/Pools/*.txt</c> 卡组 + <c>CardStarSeed.txt</c> 星级权重。
///
/// <para><b>★★★ 这就是「武器域 nCmd 0 打造」——不是两件事</b></para>
/// 真机日志（`work/server.log`）里躺着的这条是唯一判据：
/// <code>
/// [WRN] [session 1] unhandled sFunction=WeaponLogicMsg param={"nCmd":0,"Id":1,"isTen":true}
/// </code>
/// ⇒ 上行是 <c>WeaponLogicMsg</c> 的 <c>nCmd = 0</c>，<c>Id</c> 就是本表的池 id。
/// 第八轮 <c>WeaponFeature.OnForge</c> 注释里「依赖未取证的武器池档期表」
/// 与「Id/Type 不是主键、无法确定抽到哪把武器」两条论证**均已被推翻**
/// （档期表就是 <c>gacha.txt</c>，池表 <c>GDPLN</c> 列就是武器主键）。
/// 详见 `work/round9-mainsession-forensics.md` §1.2。
///
/// <para><b>与角色卡抽卡（<see cref="GachaTable"/>）的异同</b></para>
/// <list type="table">
///   <item><term>同</term><description>都读 <c>gacha.txt</c>；同一个 id 有**多个档期**
///     （所以这里也是 <c>id → List</c>，不能用覆盖式字典 —— 否则过期档期看起来还开着）；
///     都是「先掷星级、再在星级内按归一化概率掷具体物品」两级抽取；
///     都用 <c>CardStarSeed.txt</c> 的阶梯权重。</description></item>
///   <item><term>异 ①</term><description>★★★ **星级来源不同**：武器取 <c>Rarity</c> 列，
///     角色卡从 GDPL 的 L 位算。见 <see cref="WeaponGachaEntry.Rarity"/> 的注释
///     ——写错会让 4★ 永远抽不出来且不报错。</description></item>
///   <item><term>异 ②</term><description>**保底任务值是两套独立计数**：
///     武器 <c>(15, nId + 1002)</c>（<c>WeaponForge.PROTECT_TASK_BEGIN_WP = 1002</c>，
///     <c>WeaponForge.lua:4/211</c>）；角色卡 <c>(15, nId + 2)</c>。
///     免费次数也独立：武器 <c>(15, 9001 + nId)</c>
///     （<c>GachaCommon.FREE_TIME_WEAPON = 9001</c>，真机自报）。</description></item>
///   <item><term>异 ③</term><description>武器池**没有** <c>ProtectNum</c>「第 N 抽强制 4★」那一层
///     （<c>WeaponForge.lua</c> 全文无 <c>ProtectNum</c>）—— 武器的「保底」纯粹体现为
///     星级权重随 <c>times</c> 爬坡（<c>:216..227</c>）。</description></item>
///   <item><term>异 ④</term><description>武器池的消耗**十连也只扣 1 张券**
///     （<c>JudgeCurrency</c> 里 <c>n = 1</c> 恒定，<c>WeaponForge.lua:190</c>），
///     因为单抽券 <c>(7,7,1,4)</c> 与十连券 <c>(7,10,1,4)</c> 是两种不同道具。</description></item>
/// </list>
///
/// <para><b>现网数据（★ 真机自报，非本地表推断）</b></para>
/// <c>tools/gm.sh</c> 让客户端自己数（`round9-mainsession-forensics.md` §1.3）：
/// <code>
/// [GMQ] WeaponForge.tbConf pools = 19
/// [GMQ] WeaponForge open now = 1
/// [GMQ]   open pool id=1 name=武器补给 start=1645599600 end=1898636340   -- 2022-02-23 → 2030-03-02
/// [GMQ] pool1 cards=80  star1=5 star2=20 star3=25 star4=30  preview=30
/// [GMQ] ticketCount=999 tenTicket=999
/// </code>
/// ⇒ **只有池 1 开着，但一路开到 2030 年**，且玩家券充足 ⇒ 可立刻验收。
///
/// ★★ 注意本地快照比设备少：本地 <c>weapon_1_base_weapon_516_up_Mul.txt</c> 是 78 件
/// （star4 = 28），真机 80 件（star4 = 30）。这是 `AGENTS.md` §5「本地表可能比真机少行」
/// 的又一实例 ⇒ **本类不依赖任何行数/id 值域做判定**，读到什么用什么。
/// </summary>
public sealed class WeaponGachaTable
{
    /// <summary>主表文件名（客户端口径 <c>Gacha/gacha.txt</c>，本目录扁平化）。</summary>
    public const string FileName = "gacha.txt";

    /// <summary>星级权重表（与角色卡共用同一张 <c>CardStarSeed.txt</c>）。</summary>
    public const string SeedFileName = "CardStarSeed.txt";

    /// <summary>武器池的 <c>Type</c> 值（<c>gacha.txt</c> 表头第 2 行：0 武器池 / 1 角色池）。</summary>
    public const int TypeWeapon = 0;

    private readonly Dictionary<int, List<WeaponGachaPool>> _pools = new();
    private readonly Dictionary<int, CardStarSeedRow> _seeds = new();
    private GachaRotation? _rotation;

    private WeaponGachaTable() { }

    /// <summary>永久空表（缺表降级：武器抽卡一律回错误码）。</summary>
    public static WeaponGachaTable Empty { get; } = new();

    /// <summary>池 id 的个数（不是配置行数）。</summary>
    public int Count => _pools.Count;

    /// <summary>某个池 id 的全部档期（顺序即文件顺序）。</summary>
    public IReadOnlyList<WeaponGachaPool> Segments(int id)
        => _pools.TryGetValue(id, out var list) ? list : Array.Empty<WeaponGachaPool>();

    /// <summary>该池是否存在（不管档期）。</summary>
    public bool Exists(int id) => _pools.ContainsKey(id);

    /// <summary>取该池**当前生效**的那一段；没有任何一段在期内返回 null。</summary>
    public WeaponGachaPool? Get(int id, long now)
    {
        int? selected = _rotation?.SegmentAt(0, id, now);
        if (selected.HasValue)
            return selected > 0 ? Segments(id).ElementAtOrDefault(selected.Value - 1) : null;
        foreach (var seg in Segments(id))
        {
            if (seg.IsOpenAt(now))
                return seg;
        }
        return null;
    }

    /// <summary>此刻开放的池（每个 id 最多一段）。</summary>
    public IEnumerable<WeaponGachaPool> OpenAt(long now)
    {
        foreach (int id in _pools.Keys.OrderBy(k => k))
        {
            var seg = Get(id, now);
            if (seg is not null)
                yield return seg;
        }
    }

    /// <summary>星级权重行（与角色卡共用 <c>CardStarSeed.txt</c>）。</summary>
    public CardStarSeedRow? GetSeed(int id) => _seeds.TryGetValue(id, out var s) ? s : null;

    /// <summary>从 setting 目录加载。缺表返回空表并打 WARN（不抛）。</summary>
    public static WeaponGachaTable Load(string settingDir)
    {
        var table = new WeaponGachaTable { _rotation = GachaRotation.For(settingDir) };

        // ---- CardStarSeed.txt（复用 GachaTable 的解析：同一张表、同一套语义）----
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
                    foreach (var step in GachaTable.ParseSeedSteps(seedRaw.Str(row, $"{i}StarCardSeed")))
                        seed.Steps[i - 1].Add(step);
                }
                table._seeds[id] = seed;
            }
        }
        else
        {
            Log.Warn($"[setting] {SeedFileName} 缺失：武器抽卡的星级概率无法计算");
        }

        // ---- gacha.txt 的 Type == 0 行 ----
        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失：武器抽卡域降级（一律回错误码）");
            return table;
        }

        int poolFileMissing = 0;
        foreach (var row in raw.Rows)
        {
            if (raw.Int(row, "Type", -1) != TypeWeapon)
                continue;
            int id = raw.Int(row, "ID", -1);
            if (id <= 0)
                continue;

            int cash = raw.Int(row, "CashType");
            var (start, end) = GachaTable.ParseTimeRange(raw.Str(row, "Time"));
            var (freeStart, freeEnd) = GachaTable.ParseTimeRange(raw.Str(row, "FreeTime"));

            var pool = new WeaponGachaPool
            {
                Id = id,
                Name = raw.Str(row, "Name"),
                PoolFile = raw.Str(row, "Pool"),
                CashType = cash,
                // ★ WeaponForge.lua:41 判的是 `nCash == 0`（只有 0 才是道具），
                //   与角色卡的 `cash is 0 or 1` 不同 —— 照武器自己那一侧写。
                CostOneItem = cash == 0 ? GachaTable.ParseItem(raw.Str(row, "CostOne")) : null,
                CostTenItem = cash == 0 ? GachaTable.ParseItem(raw.Str(row, "CostTen")) : null,
                CostOneMoney = cash == 0 ? 0 : raw.Int(row, "CostOne"),
                CostTenMoney = cash == 0 ? 0 : raw.Int(row, "CostTen"),
                JudgeType = raw.Int(row, "JudgeType"),
                SetUpNum = raw.Int(row, "SetUpNum"),
                CardStarSeedId = raw.Int(row, "CardStarSeed"),
                FreeType = raw.Int(row, "FreeType"),
                FreeCount = raw.Int(row, "FreeCount"),
                FreeStart = freeStart,
                FreeEnd = freeEnd,
                ExchangeRate = raw.Int(row, "ExchangeRate"),
                CanUseFreeDiamond = raw.Int(row, "CanUseFreeDiamond"),
                StartTime = start,
                EndTime = end,
            };

            if (!LoadPoolEntries(settingDir, pool))
                poolFileMissing++;

            // ★ 同一个 id 有多段档期（池 1 实测 11 段），追加而不是覆盖
            if (!table._pools.TryGetValue(id, out var segs))
                table._pools[id] = segs = new List<WeaponGachaPool>();
            segs.Add(pool);
        }

        long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int open = table.OpenAt(nowUnix).Count();
        int rows = table._pools.Values.Sum(v => v.Count);
        Log.Info($"[setting] {FileName} 载入 {table._pools.Count} 个武器池 / {rows} 个档期" +
                 $"（当前开放 {open} 个" +
                 (poolFileMissing > 0 ? $"，{poolFileMissing} 个档期的卡组文件缺失" : "") + "）");
        if (open > 0)
        {
            Log.Info("[setting] 当前开放的武器池：" + string.Join("、", table.OpenAt(nowUnix)
                .Select(p => $"{p.Id}({p.PoolFile}, {p.Entries.Count} 件)")));
        }
        return table;
    }

    /// <summary>
    /// 加载一个武器池的卡组并做星级内归一化（<c>WeaponForge.lua:71..97</c>）。
    /// 返回卡组文件是否存在。
    /// </summary>
    private static bool LoadPoolEntries(string settingDir, WeaponGachaPool pool)
    {
        if (pool.PoolFile.Length == 0)
            return false;

        var raw = SettingTable.Load(Path.Combine(settingDir, pool.PoolFile + ".txt"));
        if (raw is null)
            return false;

        foreach (var row in raw.Rows)
        {
            int entryId = raw.Int(row, "ID", -1);
            if (entryId <= 0)
                continue;

            var gdpl = GachaTable.ParseItem(raw.Str(row, "GDPLN"));
            if (gdpl is null || gdpl.Length < 4)
                continue;

            // ★★★ 星级取 Rarity 列（WeaponForge.lua:77），不是从 L 位算 —— 见类注释
            int rarity = raw.Int(row, "Rarity", -1);
            if (rarity <= 0)
            {
                Log.Warn($"[setting] 武器池 {pool.PoolFile}.txt 的 ID={entryId} 缺 Rarity 列，跳过");
                continue;
            }

            var entry = new WeaponGachaEntry
            {
                Id = entryId,
                G = gdpl[0],
                D = gdpl[1],
                P = gdpl[2],
                L = gdpl[3],
                Rarity = rarity,
                RawRate = raw.Int(row, "Rate"),
                UpFlag = raw.Int(row, "UpFlag"),
                IsPreview = raw.Int(row, "IsPreview") == 1,
                Sort = raw.Int(row, "Sort"),
            };

            if (pool.Entries.ContainsKey(entryId))
                Log.Warn($"[setting] 武器池 {pool.PoolFile}.txt 存在重复 ID={entryId}，后者覆盖前者");
            pool.Entries[entryId] = entry;

            pool.StarTotal.TryGetValue(entry.Rarity, out int sum);
            pool.StarTotal[entry.Rarity] = sum + entry.RawRate;
        }

        // 星级内归一化（WeaponForge.lua:93..97）
        foreach (var entry in pool.Entries.Values)
        {
            int total = pool.StarTotal.TryGetValue(entry.Rarity, out int t) && t > 0 ? t : 1;
            entry.RateInStar = (double)entry.RawRate / total;
        }
        return true;
    }
}
