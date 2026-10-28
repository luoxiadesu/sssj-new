using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 少女好感度 / 羁绊剧情的四张配置表。取证见 `work/girl-story-forensics.md` B/C 节、
/// 台账见 `docs/05` §27。
///
/// <para><b>0. 四张表与表头行数（★ 全部 3 行，权威判据是客户端自己的加载调用）</b></para>
/// <list type="table">
///   <item><term><c>Girl/Friendliness.txt</c></term>
///     <description>好感度经验曲线。`GirlConfig.lua:116` <c>LoadTabFile(FRIENDLINESS_PATH, 3)</c></description></item>
///   <item><term><c>Girl/Fetter.txt</c></term>
///     <description>羁绊剧情主体（192 行）。`GirlConfig.lua:737`</description></item>
///   <item><term><c>Girl/FriendlinessUnlock.txt</c></term>
///     <description>好感度档位是否开放（200 行）。`GirlConfig.lua:146`</description></item>
///   <item><term><c>Girl/Secret.txt</c></term>
///     <description>少女秘密，含「喜爱礼物」<c>FitItem</c> 列。`GirlConfig.lua:244`</description></item>
/// </list>
///
/// <para><b>1. ★★ 一张 Friendliness.txt 装了两套曲线</b></para>
/// 普通少女用 <c>Level</c>/<c>Exp</c>/<c>BanGirl</c> 三列，联动少女（id 201..232）用
/// <c>LinkLevel</c>/<c>LinkExp</c>/<c>LinkBanGirl</c>（`GirlConfig.lua:112..141` <c>LoadExpConfig</c>）。
/// <c>Exp</c> 列语义是「**从本级升到下一级**所需经验」的**单级增量**，不是累计值 ——
/// 取证 `GirlCommon.lua:412..449` 的 <c>nTotalExp = nTotalExp - tbConfig.Exp</c>。
/// 实测普通曲线 `1→100`、`2→105`、`99→4500`、**`100→0`**（末级 0 = 封顶）；
/// 联动曲线是线性 `1→100`、`2→200`、`3→300`。
///
/// <para><b>2. ★★ 等级上限受 <c>BanGirl</c> 动态限制，不是常量 100</b></para>
/// `GirlCommon:GetMaxLevelConfig`（`:459..486`）**从后向前**找第一个 <c>BanGirl</c> 不含该少女的等级段。
/// ★ 实测 <c>BanGirl = [13,16]</c> 从 **Level 81 一直到 100**（连续 20 行）
/// ⇒ **少女 13 与 16 的上限是 80**，其余少女是 100。写死 100 会让这两个少女能超上限。
/// ★★ 注意 `work/girl-story-forensics.md` B.3 把这个区间写成「96..100 ⇒ 上限 95」——
/// **那是错的**（只看了表尾 5 行）。本轮单测按真实表数据纠正为 81..100 ⇒ 上限 80。
///
/// <para><b>2b. ★ 两个 <c>INTARR</c> 列的分隔符不同（最容易静默读空的地方）</b></para>
/// <c>BanGirl</c> 是 <c>[13,16]</c>（**逗号**），而 <c>Secret.txt</c> 的 <c>FitItem</c>
/// 是 <c>[5-1-1-2]</c>（**短横**）。两者都标 <c>INTARR</c>，混用解析器会得到空数组、
/// 不抛不报错 ⇒ 「喜爱礼物永远判 false」。见 <see cref="ParseIntArray"/> / <see cref="ParseDashArray"/>。
///
/// <para><b>3. ★ 三个「index 类」概念必须分清（C.1）</b></para>
/// <list type="bullet">
///   <item><c>Main[1]</c> / <c>Sub[1]</c>（数组第 1 元素）= **<c>ELevel&lt;n&gt;</c> 的下标**（任务值偏移 300+n）</item>
///   <item><c>Index</c> 列 = 联动羁绊的**位域 bit 号**</item>
///   <item><c>MainId</c> = UI 运行期顺序计数，存 <c>MainStoryId</c>（偏移 14）—— **服务端不算它**</item>
/// </list>
///
/// <para><b>4. ★ 奖励列是「逗号分隔」而不是「每个中括号一个元组」</b></para>
/// 实测 <c>MainAward = [15-2-1-1-30,15-1-1-1-5000,7-1-4-1-3,16-2-1-1-1]</c>
/// ⇒ 必须走 <see cref="ClubTable.ParseAwardList"/> 的语义（照客户端 `Lib.lua:282` <c>Parse2ItemList</c>）；
/// 用 <see cref="ChapterTable.ParseTuples"/> 会把 <c>30,15</c> 当一段、整行丢掉。
/// </summary>
public sealed class GirlLoveTable
{
    /// <summary>普通少女 id 上界（`GirlConfig.lua:79` <c>NormalEndGirlId = 32</c>）。</summary>
    public const uint NormalEndGirlId = 32;

    /// <summary>联动少女 id 基数（`GirlConfig.lua:81` <c>LinkGirlId = 200</c>）。</summary>
    public const uint LinkGirlId = 200;

    /// <summary>联动少女 id 上界（`GirlConfig.lua:82` <c>LinkEndGirlId = 232</c>）。</summary>
    public const uint LinkEndGirlId = 232;

    /// <summary>好感度硬上限（表里 100 行）。实际上限还要过 <c>BanGirl</c>，见 <see cref="MaxLevel"/>。</summary>
    public const int HardMaxLevel = 100;

    private readonly Dictionary<int, LevelRow> _normalExp = new();
    private readonly Dictionary<int, LevelRow> _linkExp = new();

    /// <summary>(girlId, level) → 该档的剧情行（一档可能有多行，实测每档 1 行）。</summary>
    private readonly Dictionary<(uint Girl, int Level), List<FetterRow>> _fetter = new();

    /// <summary>girlId → 该少女全部剧情档位的 Level（升序，去重）。</summary>
    private readonly Dictionary<uint, List<int>> _fetterLevels = new();

    /// <summary>storyIndex（<c>Main[1]</c>/<c>Sub[1]</c>）→ 奖励元组 + 所属少女/等级。</summary>
    private readonly Dictionary<(uint Girl, int Story), StoryAward> _storyAwards = new();

    /// <summary>(girlId, level) → 该档是否在 <c>FriendlinessUnlock.txt</c> 里登记（= 是否开放）。</summary>
    private readonly HashSet<(uint Girl, int Level)> _unlocked = new();

    /// <summary>喜爱礼物：(girlId, G, D, P, L) 的集合（来自 <c>Secret.txt</c> 的 <c>FitItem</c>）。</summary>
    private readonly HashSet<(uint Girl, int G, int D, int P, int L)> _loveGifts = new();

    /// <summary>心情爆炸配置：<c>PlotID</c> → 「(少女 id, 心情增减)」列表（<c>MoodBoom.txt</c>）。</summary>
    private readonly Dictionary<int, List<(uint GirlId, int Change)>> _moodBoom = new();

    public sealed record LinkStory(int Index, List<int[]> Award);
    private readonly Dictionary<(uint Girl, int Level), LinkStory> _linkStories = new();
    public LinkStory? LinkStoryOf(uint girl, int level)
        => _linkStories.GetValueOrDefault((girl, level));
    public sealed record SecretRow(int Index, int Level, int[] Gift);
    private readonly Dictionary<uint, List<SecretRow>> _secrets = new();
    public IEnumerable<SecretRow> SecretsOf(uint girl) => _secrets.GetValueOrDefault(girl) ?? Enumerable.Empty<SecretRow>();
    private GirlLoveTable() { }

    /// <summary>空表（配置缺失时的降级形态：等级上限 1、无剧情、无喜爱礼物）。</summary>
    public static GirlLoveTable Empty { get; } = new();

    /// <summary>好感度曲线的一级。</summary>
    /// <param name="Level">等级。</param>
    /// <param name="Exp">★ 从本级升到下一级所需经验（单级增量）；末级为 0 = 封顶。</param>
    /// <param name="BanGirl">该等级段**不开放**给哪些少女。</param>
    public sealed record LevelRow(int Level, int Exp, int[] BanGirl);

    /// <summary>羁绊剧情的一档（<c>Fetter.txt</c> 一行）。</summary>
    /// <param name="GirlId">少女 id（<c>Id</c> 列）。</param>
    /// <param name="Level">好感度门槛（<c>Level</c> 列）。</param>
    /// <param name="MainStory">主线剧情下标 = <c>Main[1]</c>；无主线时 0。</param>
    /// <param name="MainAward">主线奖励元组。</param>
    /// <param name="Index">★ <c>Index</c> 列 = 联动羁绊的位域 bit 号。</param>
    public sealed record FetterRow(
        uint GirlId, int Level, int MainStory, List<int[]> MainAward, int Index);

    /// <summary>一个剧情下标对应的奖励与门槛。</summary>
    /// <param name="GirlId">少女 id。</param>
    /// <param name="Level">要求的好感度等级。</param>
    /// <param name="Award">奖励元组（<c>G-D-P-L-N</c>）。</param>
    /// <param name="IsMain">true = 主线剧情，false = 支线。</param>
    public sealed record StoryAward(uint GirlId, int Level, List<int[]> Award, bool IsMain, int[]? Condition = null);

    /// <summary>某少女是否是联动少女（`GirlConfig.lua:79..82` 的 id 区间）。</summary>
    public static bool IsLinkGirl(uint girlId)
        => girlId > LinkGirlId && girlId <= LinkEndGirlId;

    /// <summary>
    /// 载入五张表。缺表不抛，只降级（Empty 语义）—— 与其它配置表一致。
    /// </summary>
    /// <param name="settingDir"><c>extracted/setting</c> 目录。</param>
    public static GirlLoveTable Load(string settingDir)
    {
        var t = new GirlLoveTable();
        t.LoadFriendliness(Path.Combine(settingDir, "Friendliness.txt"));
        t.LoadFetter(Path.Combine(settingDir, "Fetter.txt"));
        var link = SettingTable.LoadAuto(Path.Combine(settingDir, "LinkFetter.txt"), "Level");
        if (link is not null)
            foreach (var row in link.Rows)
            {
                uint girl = (uint)link.Int(row, "Id");
                int level = link.Int(row, "Level"), index = link.Int(row, "Index", -1);
                if (IsLinkGirl(girl) && level > 0 && index >= 0 && index < 32)
                    t._linkStories[(girl, level)] = new LinkStory(index,
                        ClubTable.ParseAwardList(link.Str(row, "Award")));
            }
        t.LoadUnlock(Path.Combine(settingDir, "FriendlinessUnlock.txt"));
        t.LoadSecret(Path.Combine(settingDir, "Secret.txt"));
        t.LoadMoodBoom(Path.Combine(settingDir, "MoodBoom.txt"));

        int girlsWithStory = t._fetterLevels.Count;
        Log.Info($"[setting] 少女好感度配置：经验曲线 普通 {t._normalExp.Count} 级 / 联动 {t._linkExp.Count} 级" +
                 $"（少女 13/16 上限 {t.MaxLevel(13)}、其余 {t.MaxLevel(1)}）、" +
                 $"羁绊剧情 {t._storyAwards.Count} 段（覆盖 {girlsWithStory} 个少女）、" +
                 $"开放档位 {t._unlocked.Count} 条、喜爱礼物 {t._loveGifts.Count} 项、" +
                 $"心情爆炸 {t._moodBoom.Count} 条");
        return t;
    }

    private void LoadFriendliness(string path)
    {
        var table = SettingTable.LoadAuto(path, "Level");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int lv = table.Int(row, "Level");
            if (lv > 0)
            {
                _normalExp[lv] = new LevelRow(
                    lv, table.Int(row, "Exp"), ParseIntArray(table.Str(row, "BanGirl")));
            }

            // ★ 同一行的后三列是联动曲线（LinkLevel 可能为空）
            int linkLv = table.Int(row, "LinkLevel");
            if (linkLv > 0)
            {
                _linkExp[linkLv] = new LevelRow(
                    linkLv, table.Int(row, "LinkExp"), ParseIntArray(table.Str(row, "LinkBanGirl")));
            }
        }
    }

    private void LoadFetter(string path)
    {
        var table = SettingTable.LoadAuto(path, "Level");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            uint girlId = (uint)table.Int(row, "Id");
            int level = table.Int(row, "Level");
            if (girlId == 0 || level <= 0)
                continue;

            var main = ParseFetterItem(table.Str(row, "Main"));
            int mainStory = main.Count > 0 ? main[0] : 0;
            var mainAward = ClubTable.ParseAwardList(table.Str(row, "MainAward"));
            int index = table.Int(row, "Index");

            var fr = new FetterRow(girlId, level, mainStory, mainAward, index);
            if (!_fetter.TryGetValue((girlId, level), out var list))
                _fetter[(girlId, level)] = list = new List<FetterRow>();
            list.Add(fr);

            if (!_fetterLevels.TryGetValue(girlId, out var levels))
                _fetterLevels[girlId] = levels = new List<int>();
            if (!levels.Contains(level))
                levels.Add(level);

            if (mainStory > 0)
                _storyAwards[(girlId, mainStory)] = new StoryAward(girlId, level, mainAward, true);

            // 四条支线：Sub<j> 数组第 1 元素是 ELevel 下标，门槛是 SubLevel<j>，奖励是 SubAward<j>
            for (int j = 1; j <= 4; j++)
            {
                var sub = ParseFetterItem(table.Str(row, $"Sub{j}"));
                if (sub.Count == 0)
                    continue;
                int subStory = sub[0];
                if (subStory <= 0)
                    continue;
                int subLevel = table.Int(row, $"SubLevel{j}", level);
                var subAward = ClubTable.ParseAwardList(table.Str(row, $"SubAward{j}"));
                _storyAwards[(girlId, subStory)] = new StoryAward(girlId, subLevel, subAward, false,
                    ParseFetterItem(table.Str(row, $"SubCond{j}") ?? "").ToArray());
            }
        }

        foreach (var levels in _fetterLevels.Values)
            levels.Sort();
    }

    private void LoadUnlock(string path)
    {
        var table = SettingTable.LoadAuto(path, "Level");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            uint girlId = (uint)table.Int(row, "GirlId");
            int level = table.Int(row, "Level");
            if (girlId > 0 && level > 0)
                _unlocked.Add((girlId, level));
        }
    }

    private void LoadSecret(string path)
    {
        var table = SettingTable.LoadAuto(path, "GirlId");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            uint girlId = (uint)table.Int(row, "GirlId");
            if (girlId == 0)
                continue;
            // FitItem 是 INTARR，★ 实测是 **'-' 分隔**：[5-1-1-2]（G-D-P-L），不是逗号分隔。
            // 取证 `extracted/setting/Secret.txt`：GirlId=1/SecretId=11 → [5-1-1-2]、12 → [5-1-1-4]
            // ⇒ 规律是「少女 N 的喜爱礼物 = 5-1-N-2（低阶）与 5-1-N-4（高阶）」。
            // ★★ `work/girl-story-forensics.md` B.4 把它写成逗号分隔（`Parse2Item`），
            //    按逗号解析会**一条都读不出来**（本轮单测抓到）。
            var fit = ParseDashArray(table.Str(row, "FitItem"));
            int index = table.Int(row, "SecretId");
            if (index >= 1 && index <= 20)
            {
                if (!_secrets.TryGetValue(girlId, out var secrets)) _secrets[girlId] = secrets = new();
                secrets.Add(new SecretRow(index, table.Int(row, "FitLove"), fit));
            }
            if (fit.Length >= 4)
                _loveGifts.Add((girlId, fit[0], fit[1], fit[2], fit[3]));
        }
    }

    /// <summary>
    /// 载入 <c>MoodBoom.txt</c>（剧情「爆炸选项」对少女心情值的影响）。
    ///
    /// ★ <c>GirlID</c> 与 <c>MoodChangeValue</c> 是**两个平行的逗号分隔列表**，按下标配对
    /// （`GirlConfig.lua:636..658` <c>LoadMoodBoomConfig</c>：<c>Split(IDS, ",")</c> +
    /// <c>Split(Moods, ",")</c>，再 <c>for c = 1, #tb_girls</c> 逐个 tonumber）。
    /// ★ 全表只有 **1 行数据**（<c>PlotID=1 GirlID=7 MoodChangeValue=-100</c>）。
    /// </summary>
    private void LoadMoodBoom(string path)
    {
        var table = SettingTable.LoadAuto(path, "PlotID");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int plotId = table.Int(row, "PlotID");
            if (plotId <= 0)
                continue;

            var girls = ParseIntArray(table.Str(row, "GirlID"));
            var moods = ParseIntArray(table.Str(row, "MoodChangeValue"));
            var list = new List<(uint, int)>();
            for (int i = 0; i < girls.Length && i < moods.Length; i++)
            {
                if (girls[i] > 0)
                    list.Add(((uint)girls[i], moods[i]));
            }
            if (list.Count > 0)
                _moodBoom[plotId] = list;
        }
    }

    // ================= 查询 =================

    /// <summary>该少女走哪条曲线（联动少女用 <c>LinkLevel/LinkExp</c>）。</summary>
    private Dictionary<int, LevelRow> CurveFor(uint girlId)
        => IsLinkGirl(girlId) ? _linkExp : _normalExp;

    /// <summary>
    /// 该少女的好感度上限。★ 1:1 移植 `GirlCommon:GetMaxLevelConfig`（`:459..486`）：
    /// **从后向前**找第一个 <c>BanGirl</c> 不含该少女的等级段。全表都 ban 时返回 1（照 `MaxLevel` 的 <c>or 1</c>）。
    /// </summary>
    public int MaxLevel(uint girlId)
    {
        var curve = CurveFor(girlId);
        if (curve.Count == 0)
            return 1;

        for (int lv = curve.Keys.Max(); lv >= 1; lv--)
        {
            if (!curve.TryGetValue(lv, out var row))
                continue;
            if (!row.BanGirl.Contains((int)girlId))
                return row.Level;
        }
        return 1;
    }

    /// <summary>
    /// 本级升下一级所需经验（<c>Exp</c> 列）。★ 还要过「下一级是否被 ban」——
    /// 被 ban 时返回 0（照 `GirlCommon:MaxLevelExp` `:494..524`，表现为经验条封顶）。
    /// </summary>
    public int LevelUpExp(uint girlId, int level)
    {
        var curve = CurveFor(girlId);
        if (!curve.TryGetValue(level, out var row))
            return 0;

        if (curve.TryGetValue(level + 1, out var next) && next.BanGirl.Contains((int)girlId))
            return 0;
        return row.Exp;
    }

    /// <summary>该少女的剧情档位（好感度等级，升序）。</summary>
    public IReadOnlyList<int> FetterLevels(uint girlId)
        => _fetterLevels.TryGetValue(girlId, out var l) ? l : Array.Empty<int>();

    /// <summary>某档的剧情行。</summary>
    public IReadOnlyList<FetterRow> FetterAt(uint girlId, int level)
        => _fetter.TryGetValue((girlId, level), out var l) ? l : Array.Empty<FetterRow>();

    /// <summary>
    /// 按剧情下标查奖励（<c>ViewStory</c> 用）。
    /// 下标 = <c>Fetter.txt</c> 的 <c>Main[1]</c> 或 <c>Sub&lt;j&gt;[1]</c>。
    /// </summary>
    public StoryAward? StoryOf(uint girlId, int storyIndex)
        => _storyAwards.TryGetValue((girlId, storyIndex), out var s) ? s : null;

    /// <summary>该少女的全部剧情下标（用于登录时算 <c>ELevel</c> 初值）。</summary>
    public IEnumerable<KeyValuePair<int, StoryAward>> StoriesOf(uint girlId)
    {
        foreach (var kv in _storyAwards)
        {
            if (kv.Key.Girl == girlId)
                yield return new KeyValuePair<int, StoryAward>(kv.Key.Story, kv.Value);
        }
    }

    /// <summary>
    /// Match UI_GirlLove.IsFriendlinessUnlocked: require an experience row
    /// without this girl in BanGirl. FriendlinessUnlock is a different reward table.
    /// </summary>
    public bool IsFriendlinessUnlocked(uint girlId, int level)
        => CurveFor(girlId).TryGetValue(level, out var row) && !row.BanGirl.Contains((int)girlId);

    /// <summary>是否是该少女的「喜爱礼物」（<c>Secret.txt</c> 的 <c>FitItem</c>）。</summary>
    public bool IsLoveGift(uint girlId, int g, int d, int p, int l)
        => _loveGifts.Contains((girlId, g, d, p, l));

    /// <summary>
    /// 心情爆炸配置：<c>PlotID</c> → 要改哪些少女的心情值多少。
    /// 无该配置返回空列表（LuaCall 175 据此只记日志、不回包）。
    /// </summary>
    public IReadOnlyList<(uint GirlId, int Change)> MoodBoomOf(int plotId)
        => _moodBoom.TryGetValue(plotId, out var l)
            ? l
            : Array.Empty<(uint, int)>();

    /// <summary>心情爆炸配置条数（日志/测试用）。</summary>
    public int MoodBoomCount => _moodBoom.Count;

    /// <summary>喜爱礼物条目数（日志/测试用）。</summary>
    public int LoveGiftCount => _loveGifts.Count;

    /// <summary>剧情段数（日志/测试用）。</summary>
    public int StoryCount => _storyAwards.Count;

    // ================= 解析工具 =================

    /// <summary>
    /// 解析 <c>INTARR</c> 列（<c>[13,16]</c> / 空）。非整数元素忽略。
    /// </summary>
    internal static int[] ParseIntArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();
        var cleaned = text.Replace("[", "").Replace("]", "").Replace("\"", "").Replace(" ", "");
        var result = new List<int>();
        foreach (var seg in cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(seg, out int v))
                result.Add(v);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 解析 <b>'-' 分隔</b>的四元组列（<c>Secret.txt</c> 的 <c>FitItem</c>：<c>[5-1-1-2]</c>）。
    ///
    /// ★ 单独一个函数而不是复用 <see cref="ParseIntArray"/>：这两列虽然都标 <c>INTARR</c>，
    /// 分隔符却不同（<c>BanGirl</c> 用 <c>,</c>、<c>FitItem</c> 用 <c>-</c>）。
    /// 混用会静默得到空数组 —— 本轮单测正是这么抓到的。
    /// </summary>
    internal static int[] ParseDashArray(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();
        var cleaned = text.Replace("[", "").Replace("]", "").Replace("\"", "").Replace(" ", "");
        var result = new List<int>();
        foreach (var seg in cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(seg, out int v))
                result.Add(v);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 解析 <c>Fetter.txt</c> 的 <c>Main</c>/<c>Sub&lt;j&gt;</c> 列，**只取前导的整数元素**。
    ///
    /// 该列是混合数组（`GirlConfig.lua:747` <c>Parse2FetterItem</c>），实测 9 个元素：
    /// <c>[1, 1, Card/xxx.png, girl01/girl01_love01, girl01/girl01_love01, girl01_love_sum01, -44.9, -27.6, Card/xxx.png]</c>
    /// —— 第 1、2 个是整数（index、框体 id），其余是字符串/浮点。
    /// 服务端只需要 <b>第 1 个（index）</b>，所以遇到第一个非整数就停。
    /// </summary>
    internal static List<int> ParseFetterItem(string text)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        var cleaned = text.Replace("[", "").Replace("]", "").Replace("\"", "").Replace(" ", "");
        foreach (var seg in cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(seg, out int v))
                break;
            result.Add(v);
        }
        return result;
    }
}
