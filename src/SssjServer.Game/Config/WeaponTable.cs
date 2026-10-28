using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 武器养成（`sFunction WeaponLogicMsg`）用到的配置表。取证见
/// `work/weapon-genre-forensics.md` §E、台账 `docs/05` §28。
///
/// <para><b>0. 五张表与表头行数</b></para>
/// <list type="table">
///   <item><term><c>Item/Weapon.txt</c></term><description>武器模板（138 把）。★ 由 C#
///     <c>FileWeaponItemBaseInfoReader</c> 读，Lua 里没有 <c>LoadTabFile</c> 调用
///     ⇒ 表头行数无 Lua 取证，按文件结构判定为 <b>3</b>（与 <c>ItemList.txt</c> 同族）。</description></item>
///   <item><term><c>Item/WeaponBreak.txt</c></term><description>突破：等级上限 / 消耗。
///     表头 <b>3</b>（`WeaponCommon.lua:609`）。</description></item>
///   <item><term><c>Item/ItemExp.txt</c></term><description>升级所需经验。★ 表头 <b>2</b>
///     （`ItemLogic.lua:14` <c>LoadTabFile("Item/ItemExp.txt", 2)</c>）。</description></item>
///   <item><term><c>Item/ItemRarityExp.txt</c></term><description>「喂一把武器提供多少经验」。
///     ★ 表头 <b>2</b>。</description></item>
///   <item><term><c>Item/FixedSkillList.txt</c></term><description>打造出的固定随机技能。
///     表头 <b>3</b>（`WeaponCommon.lua:722`）。</description></item>
/// </list>
///
/// <para><b>1. ★★★ 武器的经验列是 <c>NeedExp3</c>，不是 <c>NeedExp2</c></b></para>
/// 取证：`WeaponCommon.lua:58` <c>WeaponCommon.ITEMEXP_WEAOPN = 3</c>，
/// 而 `ItemLogic.lua:105` 的查询是 <c>tb["NeedExp" .. type]</c> ⇒ type=3 ⇒ **`NeedExp3`**。
/// ★★ `work/weapon-genre-forensics.md` §E.3 把它写成「武器升级经验看 `NeedExp2`」——**是错的**；
/// 实测 `ItemExp.txt` 的 `NeedExp2` 那一列**整列为空**（表里第 2 个数据列是空列），
/// 照它实现会让「所有武器都升不了级」（每级所需经验恒 0 ⇒ `GetItemDestLevel` 的
/// <c>while needExp > 0</c> 一次都不进）。同理 <c>ItemRarityExp.txt</c> 也取 <c>NeedExp3</c>。
///
/// <para><b>2. 等级上限是「稀有度 + 突破次数」的函数，不是常量</b></para>
/// `WeaponCommon:GetMaxLevel`（`:523..535`）= <c>min(经验表最大等级, GetBreakMaxLevel(Rarity, 突破次数))</c>，
/// 后者查 <c>WeaponBreak.txt</c> 的 <c>MaxLevel&lt;n&gt;</c> 列（n = 已突破次数）。
/// 实测 Rarity 1..4 的 <c>MaxLevel0</c> 都是 40，之后 50/60/70。
/// </summary>
public sealed class WeaponTable
{
    /// <summary>武器的经验类型（`WeaponCommon.lua:58` <c>ITEMEXP_WEAOPN = 3</c>）。</summary>
    public const int WeaponExpType = 3;

    /// <summary>武器 genre（`emGenreType.Weapon`）。</summary>
    public const uint WeaponGenre = 2;

    /// <summary>被动技能的 <c>skilltype</c>（`skillinfo` 里，见 §D.4）。</summary>
    public const int SkillTypePassive = 2;

    /// <summary>主技能等级的 <c>skilltype</c>。</summary>
    public const int SkillTypeMain = 1;

    /// <summary>重铸（校准）技能的 <c>skilltype</c>。</summary>
    public const int SkillTypeRecast = 4;

    private readonly Dictionary<(uint D, uint P, uint L), WeaponRow> _weapons = new();
    private readonly Dictionary<int, BreakRow> _breaks = new();

    /// <summary>等级 → 该级升下一级所需经验（<c>ItemExp.txt</c> 的 <c>NeedExp3</c>）。</summary>
    private readonly Dictionary<int, int> _levelExp = new();

    /// <summary>稀有度 → 喂一把该稀有度的武器提供多少经验（<c>ItemRarityExp.txt</c> 的 <c>NeedExp3</c>）。</summary>
    private readonly Dictionary<int, int> _rarityExp = new();

    /// <summary><c>FixedSkillList.txt</c>：Idx → (技能 id, 等级) 两组。</summary>
    private readonly Dictionary<int, FixedSkillRow> _fixedSkills = new();

    private WeaponTable() { }

    /// <summary>空表（配置缺失时降级：无模板、上限 1）。</summary>
    public static WeaponTable Empty { get; } = new();

    /// <summary>一把武器的模板（<c>Weapon.txt</c> 一行，只取服务端要用的列）。</summary>
    /// <param name="D">Detail。</param>
    /// <param name="P">Particular。</param>
    /// <param name="L">Level（= 阶，不是等级）。</param>
    /// <param name="Rarity">稀有度（决定等级上限与突破次数）。</param>
    /// <param name="SellingPrice">售价（分解折金币用）。</param>
    /// <param name="BaseSkill">主技能 id。</param>
    /// <param name="PassiveSkill1">被动技能 1（0 = 无）。</param>
    /// <param name="PassiveSkill2">被动技能 2（0 = 无）。</param>
    public sealed record WeaponRow(
        uint D, uint P, uint L, int Rarity, int SellingPrice,
        int BaseSkill, int PassiveSkill1, int PassiveSkill2);

    /// <summary>
    /// 突破配置（<c>WeaponBreak.txt</c> 一行）。
    /// <paramref name="MaxLevels"/> 下标 = 已突破次数：<c>[0]</c> = <c>MaxLevel0</c>（0 次突破的上限）。
    /// </summary>
    /// <param name="Rarity">稀有度。</param>
    /// <param name="MaxLevels">按突破次数递增的等级上限（含 0 次）。</param>
    /// <param name="Costs">每次突破的消耗：(材料数量, 货币 id, 货币数)；下标 0 = 第 1 次突破。</param>
    public sealed record BreakRow(int Rarity, List<int> MaxLevels, List<BreakCost> Costs)
    {
        /// <summary>可突破次数 = <c>MaxLevel1..4</c> 里有值的个数（`GetBreakMaxNum`）。</summary>
        public int BreakNum => Math.Max(0, MaxLevels.Count - 1);
    }

    /// <summary>一次突破的消耗。</summary>
    /// <param name="BreakItem">突破材料的数量（<c>BreakItem&lt;n&gt;</c> 列）。</param>
    /// <param name="CurrencyId">货币 id。</param>
    /// <param name="CurrencyCount">货币数量。</param>
    public sealed record BreakCost(int BreakItem, int CurrencyId, int CurrencyCount);

    /// <summary><c>FixedSkillList.txt</c> 一行（打造时给的固定技能）。</summary>
    /// <param name="Idx">索引。</param>
    /// <param name="Skill1">技能 1 id。</param>
    /// <param name="SkillLevel1">技能 1 等级。</param>
    /// <param name="Skill2">技能 2 id。</param>
    /// <param name="SkillLevel2">技能 2 等级。</param>
    public sealed record FixedSkillRow(int Idx, int Skill1, int SkillLevel1, int Skill2, int SkillLevel2);

    /// <summary>载入五张表；缺表只降级、不抛。</summary>
    public static WeaponTable Load(string settingDir)
    {
        var t = new WeaponTable();
        t.LoadWeapons(Path.Combine(settingDir, "Weapon.txt"));
        t.LoadBreaks(Path.Combine(settingDir, "WeaponBreak.txt"));
        t.LoadLevelExp(Path.Combine(settingDir, "ItemExp.txt"));
        t.LoadRarityExp(Path.Combine(settingDir, "ItemRarityExp.txt"));
        t.LoadFixedSkills(Path.Combine(settingDir, "FixedSkillList.txt"));

        Log.Info($"[setting] 武器配置：模板 {t._weapons.Count} 把、突破档 {t._breaks.Count} 档" +
                 $"（Rarity1 上限 {t.MaxLevelFor(1, 0)}→{t.MaxLevelFor(1, t.BreakNumFor(1))}、" +
                 $"可突破 {t.BreakNumFor(1)} 次）、升级经验曲线 {t._levelExp.Count} 级" +
                 $"（经验列 NeedExp{WeaponExpType}）、喂料经验 {t._rarityExp.Count} 档、" +
                 $"固定技能 {t._fixedSkills.Count} 组");
        return t;
    }

    private void LoadWeapons(string path)
    {
        var table = SettingTable.LoadAuto(path, "Detail");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            uint g = (uint)table.Int(row, "Genre");
            if (g != WeaponGenre)
                continue;       // 该表理论上全是 genre 2，防御性过滤
            uint d = (uint)table.Int(row, "Detail");
            uint p = (uint)table.Int(row, "Particular");
            uint l = (uint)table.Int(row, "Level");
            if (d == 0)
                continue;

            _weapons[(d, p, l)] = new WeaponRow(
                d, p, l,
                Rarity: table.Int(row, "Rarity"),
                SellingPrice: table.Int(row, "SellingPrice"),
                BaseSkill: table.Int(row, "BaseSkill"),
                PassiveSkill1: table.Int(row, "PassiveSkill1"),
                PassiveSkill2: table.Int(row, "PassiveSkill2"));
        }
    }

    private void LoadBreaks(string path)
    {
        var table = SettingTable.LoadAuto(path, "MaxLevel0");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int rarity = table.Int(row, "Rarity");
            if (rarity <= 0)
                continue;

            var maxLevels = new List<int> { table.Int(row, "MaxLevel0") };
            var costs = new List<BreakCost>();
            // MaxLevel1..4 / BreakItem1..4 / CurrencyId1..4 / CurrencyCount1..4
            // ★ 空列即「没有这一次突破」⇒ 遇到空就停（`GetBreakMaxNum` 数的就是有几个）
            for (int i = 1; i <= 4; i++)
            {
                string col = $"MaxLevel{i}";
                if (!table.Columns.ContainsKey(col))
                    break;
                string raw = table.Str(row, col);
                if (raw.Length == 0 || !int.TryParse(raw, out int lv) || lv <= 0)
                    break;
                maxLevels.Add(lv);
                costs.Add(new BreakCost(
                    table.Int(row, $"BreakItem{i}"),
                    table.Int(row, $"CurrencyId{i}"),
                    table.Int(row, $"CurrencyCount{i}")));
            }
            _breaks[rarity] = new BreakRow(rarity, maxLevels, costs);
        }
    }

    /// <summary>
    /// 载入升级经验曲线。★ <c>ItemExp.txt</c> 只有 <b>2 行表头</b>
    /// （`ItemLogic.lua:14`）；按默认 3 行读会把第一行数据当列名 ⇒ 整表静默读空。
    /// 用 <see cref="SettingTable.LoadAuto"/> 探测更稳。
    /// </summary>
    private void LoadLevelExp(string path)
    {
        var table = SettingTable.LoadAuto(path, "Lv");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int lv = table.Int(row, "Lv", -1);
            if (lv <= 0)
                continue;
            _levelExp[lv] = table.Int(row, $"NeedExp{WeaponExpType}");
        }
    }

    private void LoadRarityExp(string path)
    {
        var table = SettingTable.LoadAuto(path, "Rarity");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int rarity = table.Int(row, "Rarity", -1);
            if (rarity < 0)
                continue;
            _rarityExp[rarity] = table.Int(row, $"NeedExp{WeaponExpType}");
        }
    }

    private void LoadFixedSkills(string path)
    {
        var table = SettingTable.LoadAuto(path, "Idx");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int idx = table.Int(row, "Idx", -1);
            if (idx < 0)
                continue;
            _fixedSkills[idx] = new FixedSkillRow(
                idx,
                table.Int(row, "Skill1"), table.Int(row, "SkillLevel1"),
                table.Int(row, "Skill2"), table.Int(row, "SkillLevel2"));
        }
    }

    // ================= 查询 =================

    /// <summary>武器模板；不存在返回 null（调用方据此拒绝操作）。</summary>
    public WeaponRow? Find(uint d, uint p, uint l)
        => _weapons.TryGetValue((d, p, l), out var v) ? v : null;

    /// <summary>模板数（日志/测试用）。</summary>
    public int WeaponCount => _weapons.Count;

    /// <summary>某稀有度可突破几次（`WeaponCommon:GetBreakMaxNum`）。</summary>
    public int BreakNumFor(int rarity)
        => _breaks.TryGetValue(rarity, out var b) ? b.BreakNum : 0;

    /// <summary>
    /// 某稀有度在「已突破 <paramref name="breakLevel"/> 次」时的等级上限
    /// （`WeaponCommon:GetBreakMaxLevel` → <c>WeaponBreak.txt</c> 的 <c>MaxLevel&lt;n&gt;</c>）。
    /// 越界时取最后一档（照客户端「数有几个 MaxLevelN」的语义）。
    /// </summary>
    public int MaxLevelFor(int rarity, int breakLevel)
    {
        if (!_breaks.TryGetValue(rarity, out var b) || b.MaxLevels.Count == 0)
            return 1;
        int idx = Math.Clamp(breakLevel, 0, b.MaxLevels.Count - 1);
        return b.MaxLevels[idx];
    }

    /// <summary>
    /// 等级上限，**再与经验表的最大等级取小**（1:1 照 `WeaponCommon:GetMaxLevel` `:523..535`）。
    /// </summary>
    public int MaxLevel(int rarity, int breakLevel)
    {
        int byBreak = MaxLevelFor(rarity, breakLevel);
        int byExp = ExpMaxLevel;
        return byExp > 0 && byExp < byBreak ? byExp : byBreak;
    }

    /// <summary>
    /// 经验表能支撑的最高等级 = **第一个 <c>NeedExp3 == 0</c> 的等级**
    /// （`ItemLogic.lua:43..47` 的 <c>tbItemMaxLevel</c> 算法：遇到 0 就记下当前 level）。
    /// </summary>
    public int ExpMaxLevel
    {
        get
        {
            if (_levelExp.Count == 0)
                return 0;
            for (int lv = 1; lv <= _levelExp.Keys.Max(); lv++)
            {
                if (_levelExp.TryGetValue(lv, out int need) && need == 0)
                    return lv;
            }
            return _levelExp.Keys.Max();
        }
    }

    /// <summary>该等级升到下一级所需经验（0 = 已封顶）。</summary>
    public int LevelUpExp(int level)
        => _levelExp.TryGetValue(level, out var v) ? v : 0;

    /// <summary>把一把该稀有度的武器当素材喂掉，能提供多少经验（不含它自身已积累的）。</summary>
    public int RarityExp(int rarity)
        => _rarityExp.TryGetValue(rarity, out var v) ? v : 0;

    /// <summary>突破消耗；<paramref name="breakLevel"/> = 当前已突破次数（0 = 第 1 次）。</summary>
    public BreakCost? BreakCostFor(int rarity, int breakLevel)
    {
        if (!_breaks.TryGetValue(rarity, out var b))
            return null;
        return breakLevel >= 0 && breakLevel < b.Costs.Count ? b.Costs[breakLevel] : null;
    }

    /// <summary>固定技能组（打造用）。</summary>
    public FixedSkillRow? FixedSkill(int idx)
        => _fixedSkills.TryGetValue(idx, out var v) ? v : null;

    /// <summary>固定技能组数（测试用）。</summary>
    public int FixedSkillCount => _fixedSkills.Count;

    /// <summary>
    /// 「一把 N 级武器已累计投入多少经验」（`GetPreAllItemExp`）——
    /// 把一把已升级的武器当素材时，它已投入的经验会一并返还。
    /// </summary>
    public int SumExpBelow(int level)
    {
        int sum = 0;
        for (int lv = 1; lv < level; lv++)
            sum += LevelUpExp(lv);
        return sum;
    }

    /// <summary>
    /// 升级推演：1:1 移植 `GetItemDestLevel`（`Lib.lua:685..711`）。
    ///
    /// 四条不能改的行为：
    /// ① <c>nAdd == 0</c> 原样返回；
    /// ② **已达上限时经验仍然累加**（`:689..691` <c>return maxLevel, srcExp + nAdd</c>）——
    ///    不是丢弃，与少女好感度那边相反；
    /// ③ 循环条件是 <c>needExp &gt; 0 &amp;&amp; destExp &gt;= needExp</c>（<c>needExp == 0</c> 即封顶）；
    /// ④ 升到上限时**保留余下经验**（`:702`）。
    /// </summary>
    public (int Level, int Exp) DestLevel(int srcLevel, int srcExp, int add, int maxLevel)
    {
        if (add == 0)
            return (srcLevel, srcExp);
        if (maxLevel > 0 && srcLevel >= maxLevel)
            return (maxLevel, srcExp + add);

        int level = srcLevel;
        int exp = srcExp + add;
        int need = LevelUpExp(level);

        while (need > 0 && exp >= need)
        {
            exp -= need;
            level++;
            if (maxLevel > 0 && level >= maxLevel)
                return (maxLevel, exp);
            need = LevelUpExp(level);
            if (need == 0)
                return (level, 0);
        }
        return (level, exp);
    }
}
