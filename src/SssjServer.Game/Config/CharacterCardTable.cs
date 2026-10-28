using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一张角色卡的模板（客户端 <c>Game.CharacterCardItemBaseInfo</c>，dump.cs:541764）。
///
/// 本类**只收录服务端会用到的列**（发卡 / 建 GirlInfo / 日志）；
/// 战斗数值（AddHp / AttackDamage / 技能 id / 立绘偏移…）在
/// <c>extracted/setting/CharacterCard.txt</c> 里是齐的，将来做养成/战斗时再加字段。
/// </summary>
public sealed class CharacterCardTemplate
{
    /// <summary>大类，角色卡恒 1（<c>emGenreType.Card</c>，dump.cs:372496）。</summary>
    public uint G { get; init; } = 1;

    /// <summary>少女 id（<c>Detail</c>）。与 <c>GirlInfo.txt</c> 的 <c>GirlId</c> 同一空间。</summary>
    public uint D { get; init; }

    /// <summary>服装/变体（<c>Particular</c>）。</summary>
    public uint P { get; init; }

    /// <summary>阶（<c>Level</c>）。</summary>
    public uint L { get; init; }

    /// <summary>星级（<c>Rarity</c> 列）。实测与 <c>L</c> 一致，但语义不同，各自保留。</summary>
    public int Rarity { get; init; }

    /// <summary>
    /// 武器类型（<c>WeaponType</c> 列，值域 1..5）。
    ///
    /// ★ 本字段是**突破配置的 key 的一半**：
    /// <c>CharacterCardBreak.Type = WeaponType * 10 + Rarity</c>
    /// （<see cref="CardTable.BreakTypeOf"/>，269/269 程序化验证过，
    /// 见 <c>work/gap-analysis.md</c> §2.4②）。
    /// </summary>
    public int WeaponType { get; init; }

    /// <summary>
    /// 属性（<c>AttributeType</c> 列）—— 决定「哪种经验素材能享受 1.5 倍加成」。
    ///
    /// 映射**不是恒等的**：<c>CardCommon.tbMapC2I</c>（<c>CardCommon.lua:33..39</c>）是
    /// <c>0→1, 1→3, 2→2, 3→5, 4→6</c>，即属性值 → 同属性经验素材的 <c>P</c> 值。
    /// 见 <see cref="CardTable.ExpRate"/>。
    /// </summary>
    public int AttributeType { get; init; }

    /// <summary>
    /// 升级经验曲线类型（<c>ExpType</c> 列）→ 决定读 <c>ItemExp.txt</c> 的哪一列。
    /// ★ 实测 269 张卡**全部为 1**（<c>cut -f21 CharacterCard.txt | sort | uniq -c</c>），
    /// 所以 <see cref="CardTable.CardExpType"/> 直接用常量 1；保留本字段是为了
    /// 「万一将来换 dump 后不再全为 1」时能被单测逮到。
    /// </summary>
    public int ExpType { get; init; } = 1;

    /// <summary>多语言 key（<c>DescriptionIndex</c>），中文名查 <c>item.&lt;key&gt;</c>。</summary>
    public string DescriptionIndex { get; init; } = "";

    /// <summary>
    /// 模型 id（<c>ModelId</c> 列）—— 即「这张卡对应哪套衣服」，**不是**少女 id。
    ///
    /// 取证：<c>UI_GirlClothes.lua:479</c> <c>if tbGirl.ModelId == tbBaseInfo.ModelId</c>
    /// （拿少女**当前穿的** ModelId 与卡模板的 ModelId 比）、
    /// <c>GirlConfig.lua:340</c> <c>self.tbSuitConfig[GirlId][tb.ModelId] = tb</c>
    /// （服装配置按 <c>(GirlId, ModelId)</c> 建索引）。
    /// 这一条纠正了 <see cref="StarterLoadout"/> 早期把 <c>GirlInfo.modelid</c> 填成少女 id 的写法。
    ///
    /// <para><b>与 <see cref="P"/> 的关系（实测 269 条统计）</b></para>
    /// <list type="bullet">
    ///   <item>242 张：<c>ModelId == P</c>；</item>
    ///   <item>13 张 <c>P == 81</c> → <c>ModelId == 8001</c>（约会服一类，<c>cc_yuehui_*</c>）；</item>
    ///   <item>3 张 <c>P == 82</c> → <c>ModelId == 8002</c>（儿童节/汉服）；</item>
    ///   <item>11 张 <c>P == 71</c> → <c>ModelId == 7001</c>（DescriptionIndex 全是
    ///     <c>cc_gongshifu_luokeke</c>，疑为未完工/占位内容）。</item>
    /// </list>
    /// ⇒ **不能把 P 当 ModelId 用**，也**不能直接把 ModelId 当任务值偏移**：
    /// &gt;= 2000 的要先过 <c>GirlCommon:FixModelId</c>（GirlCommon.lua:609..629），
    /// 移植见 <see cref="GmUnlock.FixModelId"/>。
    /// </summary>
    public int ModelId { get; init; }

    /// <summary>套装类型（<c>SuitType</c>：0 无 / 1 战术 / 2 常规 / 3 社保 / 4 前卫）。</summary>
    public int SuitType { get; init; }

    /// <summary>职业（<c>Pro</c> 列）。</summary>
    public int Pro { get; init; }

    /// <summary>
    /// 战术技能 id（<c>TacticalSkill</c> 列，第 60 列）。
    ///
    /// <para><b>★★★ 为什么服务端必须发它（第十四轮真机验收暴露）</b></para>
    /// 助战列表 <c>UI_Support.lua:230</c> 是**无 nil 保护的直接下标**：
    /// <code>
    /// local id, level = MainCardInfo.Skill[1].id, MainCardInfo.Skill[1].level
    /// </code>
    /// ⇒ 服务端下发的 <c>Card.Skill</c> 若是**空数组**，这一行就抛
    /// <c>attempt to index a nil value</c>，把后面整段渲染（技能图标/名称/
    /// 战力/攻防血盾）全部吞掉 —— 界面**看起来正常**（预制体默认值：
    /// 技能名显示占位符「技能名称」、四维显示 9999），极易被误判成「已经好了」。
    ///
    /// ⇒ <b>「非 nil」不等于「非空」</b>：<c>pairs(tbSkill)</c>（<c>:327</c>）空数组安全，
    /// 但 <c>Skill[1].id</c>（<c>:230</c>）要求**至少一个元素**。
    /// 两处消费点的强度不同，取证时必须都找出来。
    ///
    /// <para><b>数据完整性</b></para>
    /// <c>awk -F'\t' 'NR&gt;3{print $60}' CharacterCard.txt</c> ⇒ **269/269 全部有值**，
    /// 所以不需要兜底常量。
    /// </summary>
    public int TacticalSkill { get; init; }

    /// <summary>被动技能 1（<c>PassiveSkill1</c> 列，第 57 列）。0 表示无。</summary>
    public int PassiveSkill1 { get; init; }

    /// <summary>被动技能 2（<c>PassiveSkill2</c> 列，第 58 列）。0 表示无。</summary>
    public int PassiveSkill2 { get; init; }
    public int ResistanceSkill { get; init; }

    /// <summary>中文名（来自旁表 <c>CharacterCardName.txt</c>，仅日志用；缺表为空串）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>日志友好名：优先中文名，退化到多语言 key，再退化到四元组。</summary>
    public string LogName
        => DisplayName.Length > 0 ? DisplayName
         : DescriptionIndex.Length > 0 ? DescriptionIndex
         : $"({G},{D},{P},{L})";

    public override string ToString() => $"({G},{D},{P},{L}) {LogName} r{Rarity}";
}

/// <summary>
/// 角色卡模板表：<c>(G,D,P,L)</c> → <see cref="CharacterCardTemplate"/>。
///
/// <para><b>数据来源取证</b></para>
/// 客户端入口 <c>Game.FileCharacterCardItemBaseInfoReader</c>（dump.cs:558941）：
/// <code>
///   public const string FilePath = "Item/CharacterCard.txt";   // dump.cs:558944
///   public static CharacterCardItemBaseInfo[] LoadFile()       // RVA 0x178AF70
/// </code>
/// 与 <see cref="ItemBaseTable"/> 完全同构：<c>LoadFile()</c> 把 <c>.txt</c> 换成 <c>.asset</c>
/// 后走 <c>Game.Assets.Load&lt;FileCharacterCardItemBaseInfo&gt;</c>，
/// 即运行期读的是**烘进 setting.u 的 ScriptableObject**，APK 的 assets 里没有这张 txt。
/// 本仓库已把它解出来落成同口径的三行表头文本：
/// <code>
///   work/apk/assets/setting.u --(tools/bundle_decrypt.py)--> work/setting.dec.u
///     --(UnityPy, MonoBehaviour path_id=-5940657855288507350, m_Name="CharacterCard")-->
///     work/parse_charactercard.py      → work/charactercard_dump.tsv   (269 条，tail 0 字节)
///     work/charactercard_to_setting.py → extracted/setting/CharacterCard.txt
///                                      + extracted/setting/CharacterCardName.txt（zh_CN 名称旁表）
/// </code>
///
/// <para><b>为什么必须用它，而不是抽卡池表</b></para>
/// 「全部角色卡」的其它候选来源都不全：
/// <list type="bullet">
///   <item><c>card_*.txt</c> / <c>Hedge*.txt</c>（抽卡池）汇总只有 170 张、覆盖 16 个少女的一部分变体；</item>
///   <item><c>CardRefit.txt</c> 只有 20 张改造卡；</item>
///   <item><c>GirlInfo.txt</c> 有 46 个 GirlId，但 101..127 / 201..204 / 999 是 NPC / 咖啡厅客人，
///         **没有对应角色卡**。</item>
/// </list>
/// 本表 269 张卡、Detail ∈ {1..16, 102}，Genre 恒 1 —— 这才是可发的全集。
///
/// <para><b>缺表降级</b></para>
/// 与 <see cref="ChapterTable"/>/<see cref="ItemBaseTable"/> 一致：<see cref="Load"/> 绝不抛，
/// 缺表返回空表并打 WARN，调用方（GM 全解锁）自然退化为「不发卡」。
/// </summary>
public sealed class CharacterCardTable
{
    /// <summary>表文件名（客户端口径 "Item/CharacterCard.txt"，本目录是扁平化的）。</summary>
    public const string FileName = "CharacterCard.txt";

    /// <summary>zh_CN 名称旁表文件名（本仓库自造，客户端无此文件，只影响日志可读性）。</summary>
    public const string NameFileName = "CharacterCardName.txt";

    /// <summary>角色卡的 genre（<c>emGenreType.Card = 1</c>，dump.cs:372496）。</summary>
    public const uint CardGenre = 1;

    private readonly Dictionary<(uint G, uint D, uint P, uint L), CharacterCardTemplate> _byKey = new();

    private CharacterCardTable() { }

    /// <summary>永久空表（缺表降级用）。</summary>
    public static CharacterCardTable Empty { get; } = new();

    /// <summary>卡片条数。0 表示处于降级状态（缺表）。</summary>
    public int Count => _byKey.Count;

    /// <summary>全部卡片（顺序即文件顺序 = 资产内顺序）。</summary>
    public IEnumerable<CharacterCardTemplate> All => _byKey.Values;

    /// <summary>出现过的少女 id（升序去重）。</summary>
    public IEnumerable<uint> GirlIds => _byKey.Values.Select(c => c.D).Distinct().OrderBy(v => v);

    /// <summary>按四元组查卡；不存在返回 null。</summary>
    public CharacterCardTemplate? Get(uint g, uint d, uint p, uint l)
        => _byKey.TryGetValue((g, d, p, l), out var t) ? t : null;

    /// <summary>某个少女的全部卡（按 (P,L) 升序）。</summary>
    public IEnumerable<CharacterCardTemplate> ForGirl(uint girlId)
        => _byKey.Values.Where(c => c.D == girlId).OrderBy(c => c.P).ThenBy(c => c.L);

    /// <summary>日志用名：有模板取模板名，没有就退化成四元组字符串。</summary>
    public string NameOf(uint g, uint d, uint p, uint l)
        => Get(g, d, p, l)?.LogName ?? $"({g},{d},{p},{l})";

    /// <summary>
    /// 从 setting 目录加载 CharacterCard.txt（+ 可选的中文名旁表）。
    /// 缺表返回空表并打 WARN（不抛异常）。
    /// </summary>
    public static CharacterCardTable Load(string settingDir)
    {
        var table = new CharacterCardTable();

        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失（客户端把它烘进了 setting.u，需用 " +
                     "work/parse_charactercard.py + work/charactercard_to_setting.py 解出）：" +
                     "角色卡模板查询降级为空表，GM 全解锁将不发角色卡");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int g = raw.Int(row, "Genre", -1);
            int d = raw.Int(row, "Detail", -1);
            int p = raw.Int(row, "Particular", -1);
            int l = raw.Int(row, "Level", -1);
            if (g < 0 || d < 0 || p < 0 || l < 0)
                continue;

            var tpl = new CharacterCardTemplate
            {
                G = (uint)g,
                D = (uint)d,
                P = (uint)p,
                L = (uint)l,
                Rarity = raw.Int(row, "Rarity"),
                WeaponType = raw.Int(row, "WeaponType"),
                AttributeType = raw.Int(row, "AttributeType"),
                ExpType = raw.Int(row, "ExpType", 1),
                DescriptionIndex = raw.Str(row, "DescriptionIndex"),
                ModelId = raw.Int(row, "ModelId"),
                SuitType = raw.Int(row, "SuitType"),
                Pro = raw.Int(row, "Pro"),
                TacticalSkill = raw.Int(row, "TacticalSkill"),
                PassiveSkill1 = raw.Int(row, "PassiveSkill1"),
                PassiveSkill2 = raw.Int(row, "PassiveSkill2"),
                ResistanceSkill = raw.Int(row, "ResistanceSkill"),
            };
            table._byKey[(tpl.G, tpl.D, tpl.P, tpl.L)] = tpl;
        }

        int named = 0;
        var names = SettingTable.Load(Path.Combine(settingDir, NameFileName));
        if (names is not null)
        {
            foreach (var row in names.Rows)
            {
                int g = names.Int(row, "Genre", -1);
                int d = names.Int(row, "Detail", -1);
                int p = names.Int(row, "Particular", -1);
                int l = names.Int(row, "Level", -1);
                if (g < 0 || d < 0 || p < 0 || l < 0)
                    continue;
                if (!table._byKey.TryGetValue(((uint)g, (uint)d, (uint)p, (uint)l), out var tpl))
                    continue;
                var name = names.Str(row, "Name");
                if (name.Length == 0)
                    continue;
                tpl.DisplayName = name;
                named++;
            }
        }

        Log.Info($"[setting] {FileName} 载入 {table._byKey.Count} 张角色卡" +
                 $"（少女 {table.GirlIds.Count()} 位，中文名 {named} 条）");
        return table;
    }
}
