using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 誓约的一个阶段（<c>PldgeSteps.txt</c> 的一行）。
///
/// 客户端同构实现是 <c>Pldge:LoadStepConfig</c>（<c>extracted/lua/pldgecommon.lua:93..135</c>），
/// 加载路径 <c>LoadTabFile('Pldge/PldgeSteps.txt', 3)</c>（<c>:96</c>）⇒ **表头 3 行**。
/// </summary>
public sealed class PledgeStepRow
{
    /// <summary>少女 id（<c>girlid</c>）。</summary>
    public int GirlId { get; init; }

    /// <summary>
    /// 阶段号（<c>step</c>）。
    /// ★ <b>step 0 是「未激活」占位</b>：<c>Pldge:GetBeginCfg</c>（<c>pldgecommon.lua:153</c>）
    /// 取的就是它，其 <c>tasklist[1]</c> 是**激活本少女的消耗任务**。
    /// 真正的养成阶段从 1 开始（实测每人 7~8 个 step）。
    /// </summary>
    public int Step { get; init; }

    /// <summary>是否更换卡片（<c>changecard</c>）。</summary>
    public int ChangeCard { get; init; }

    public string SimpleCartoon { get; init; } = "";

    /// <summary>本阶段发放的卡片四元组（<c>cardgdpl</c>，**短横**分隔）。</summary>
    public IReadOnlyList<int> CardGdpl { get; init; } = Array.Empty<int>();

    /// <summary>卡片突破等级（<c>cardbreaklv</c>）。</summary>
    public int CardBreakLv { get; init; }

    /// <summary>卡片等级（<c>cardlv</c>）。</summary>
    public int CardLv { get; init; }

    /// <summary>卡片技能等级（<c>cardskilllv</c>，**短横**分隔）。</summary>
    public IReadOnlyList<int> CardSkillLv { get; init; } = Array.Empty<int>();

    /// <summary>卡片 4 心（<c>heart</c>）。</summary>
    public int Heart { get; init; }

    /// <summary>
    /// 本阶段的任务 id 列表（<c>tasklist</c>，**短横**分隔）。
    /// 全部完成后才能 <c>Pldge_StepUpGirl</c> 进下一阶段。
    /// </summary>
    public IReadOnlyList<int> TaskList { get; init; } = Array.Empty<int>();

    /// <summary>第7阶段奖励：G-D-P-L-N-完成任务数；第6位不是概率或卡片等级。</summary>
    public IReadOnlyList<int[]> Rewards { get; init; } = Array.Empty<int[]>();

    /// <summary>
    /// ★★★ <c>isopen</c> 列 —— <b>不是 0/1 布尔，是「开放日期」字符串</b>
    /// （实测值形如 <c>2019-11-5-10-40</c> / <c>2022-06-29-8-0</c>，17 行 step0 全部有值）。
    ///
    /// <para><b>原厂客户端在这里有个 bug（第十五轮实测）</b></para>
    /// <c>pldgecommon.lua:114</c> 是 <c>tonumber(tbRow['isopen']) or 0</c>
    /// ⇒ 日期字符串 <c>tonumber</c> 得 <b>nil</b> ⇒ 恒为 0
    /// ⇒ <c>Pldge:GetOpenGirlList()</c>（<c>:157</c> 判 <c>isopen == 1</c>）**恒返回空表**。
    /// 真机自报佐证：<c>GetOpenGirlList n=0</c> 而 <c>GetAllGirlList n=17</c>。
    ///
    /// <para><b>★★ 但这影响不到我们</b></para>
    /// 客户端界面用的 <c>GirlPromise.OpenGirlList</c> **完全来自服务端下发**
    /// （<c>GirlPromise.lua:550</c>），不读本地那份
    /// ⇒ <b>开放名单由服务端说了算</b>。本服按「日期已到」判定（见 <see cref="IsOpenAt"/>）。
    /// </summary>
    public string OpenTimeRaw { get; init; } = "";

    /// <summary>
    /// 按 <see cref="OpenTimeRaw"/> 判断该阶段在给定时刻是否已开放。
    /// 解析失败（空/格式怪）一律视为**已开放** —— 与「本服要让玩家玩到」的目标一致，
    /// 且避免因表结构再变而整域静默关闭。
    /// </summary>
    public bool IsOpenAt(DateTime now)
    {
        var t = ParseOpenTime(OpenTimeRaw);
        return t is null || t.Value <= now;
    }

    /// <summary>
    /// 解析 <c>2019-11-5-10-40</c> / <c>2022-06-29-8-0</c> 这类「年-月-日-时-分」。
    /// 解析不了返回 null（调用方按「已开放」处理）。
    /// </summary>
    internal static DateTime? ParseOpenTime(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var p = raw.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3)
            return null;
        try
        {
            int y = int.Parse(p[0]), mo = int.Parse(p[1]), d = int.Parse(p[2]);
            int h = p.Length > 3 ? int.Parse(p[3]) : 0;
            int mi = p.Length > 4 ? int.Parse(p[4]) : 0;
            if (y < 1970 || y > 9999 || mo is < 1 or > 12 || d is < 1 or > 31
                || h is < 0 or > 23 || mi is < 0 or > 59)
                return null;
            return new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local);
        }
        catch
        {
            return null;
        }
    }

    public override string ToString() => $"pldge girl={GirlId} step={Step} tasks={TaskList.Count}";
}

/// <summary>
/// 誓约任务（<c>PldgeTask.txt</c> 的一行）。
/// 客户端同构实现 <c>Pldge:LoadTaskConfig</c>（<c>pldgecommon.lua:20..57</c>），表头 3 行。
/// </summary>
public sealed class PledgeTaskRow
{
    /// <summary>任务 id（<c>id</c>）。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 任务类型（<c>strtype</c>）。实测 16 种、693 条，分布：
    /// <c>ReduceItem 308 / FriendLevel 97 / CardCount 56 / ArbitraryFight 37 / GiftCount 32 /
    /// TouchHead 32 / GreeterTime 29 / Training 17 / CafeTime 16 / RestTime 16 / EventFight 11 /
    /// PvpFight 9 / ChapterFight 7 / TowerFight 1 / ReduceMoney 1 / RecuseCount 1</c>。
    /// </summary>
    public string StrType { get; init; } = "";

    /// <summary>归属少女（<c>girlid</c>）。</summary>
    public int GirlId { get; init; }

    /// <summary>目标次数/数量（<c>count</c>）。</summary>
    public int Count { get; init; }

    /// <summary>新阶段是否重新计数（<c>recount</c>）。</summary>
    public int Recount { get; init; }

    /// <summary>
    /// 第 1 个参数（<c>para1</c>）的原文与列名（<c>paraname1</c>）。
    /// ★ <c>strtype == "ReduceItem"</c> 时 <c>para1</c> 是**短横**分隔的四元组
    /// （<c>pldgecommon.lua:40</c> <c>Parse2Item(para, "-")</c>），例 <c>9-16-1-1</c>。
    /// </summary>
    public string ParaName1 { get; init; } = "";

    /// <inheritdoc cref="ParaName1"/>
    public string Para1 { get; init; } = "";

    /// <summary>第 2 个参数（<c>para2</c>）。★ 几种 Fight 类型下是**竖线**分隔（<c>:42</c>）。</summary>
    public string ParaName2 { get; init; } = "";

    /// <inheritdoc cref="ParaName2"/>
    public string Para2 { get; init; } = "";

    /// <summary>
    /// <c>ReduceItem</c> 类型的消耗物四元组（解析自 <see cref="Para1"/>）；其它类型为空。
    /// ★ 激活各少女的 step0 首个任务全是 <c>ReduceItem count=10 gdpl=9-16-1-1</c>
    /// （誓约碎片 ×10）。
    /// </summary>
    public IReadOnlyList<int> ReduceGdpl { get; init; } = Array.Empty<int>();

    public override string ToString() => $"task#{Id} {StrType} girl={GirlId} count={Count}";
}

/// <summary>
/// 誓约配置表：<c>PldgeSteps.txt</c> + <c>PldgeTask.txt</c>。
///
/// <para><b>为什么这个域可以做（第十五轮推翻的旧结论）</b></para>
/// <c>MainCityFeature</c> 旧注释称「打开开关玩家就会加载本服没实现的誓约地图」——
/// **实测证伪**：<c>CS.Game.Map.Find(11001)</c> 真机返回有效 Info、场景名 <c>Promise</c>、
/// 资源包 <c>mappromise_prefab.u</c> 在设备与资源站都在、且已登记进 <c>version.data</c>（2 处）。
/// ★ 当时误判的原因是用了**不存在的 API** <c>Map.GetInfo</c>（正确的是 <c>Map.Find</c>，
/// <c>dump.cs:555419</c>）得到 nil ⇒ 教训：判「数据不存在」前先用**已知存在的同类值**
/// （map 2/6/7）验证查询手段本身。
///
/// 详见 <c>work/pledge-forensics.md</c>。
/// </summary>
public sealed class PledgeTable
{
    /// <summary>阶段表文件名（客户端口径 <c>Pldge/PldgeSteps.txt</c>，本目录扁平）。</summary>
    public const string StepFileName = "PldgeSteps.txt";

    /// <summary>任务表文件名（客户端口径 <c>Pldge/PldgeTask.txt</c>）。</summary>
    public const string TaskFileName = "PldgeTask.txt";

    /// <summary>
    /// ★★★ 英文列名所在行号（<b>0-based</b>，= <see cref="SettingTable.Load"/> 的 <c>headerRow</c>）。
    ///
    /// 两张表都是**三行表头**（客户端 <c>LoadTabFile(_, 3)</c>，<c>pldgecommon.lua:22/96</c>
    /// —— 那个 3 是「表头**行数**」），换算到 0-based 行号就是 <b>2</b>。
    ///
    /// ★ 这里踩过一次：直接把客户端的 3 填进来 ⇒ 把第一行数据当列名
    /// ⇒ 所有列查不到、整表**静默返回 0 行**（不抛不报错），
    /// 症状是「誓约少女数 0」。正是 AGENTS.md §5 记的那类坑。
    /// </summary>
    public const int HeaderRow = 2;

    /// <summary>「未激活」的占位阶段号（<c>Pldge:GetBeginCfg</c> 取 step 0）。</summary>
    public const int BeginStep = 0;

    // girlid → (step → row)
    private readonly Dictionary<int, Dictionary<int, PledgeStepRow>> _steps = new();
    private readonly Dictionary<int, PledgeTaskRow> _tasks = new();

    private PledgeTable() { }

    /// <summary>永久空表（缺表降级用）。</summary>
    public static PledgeTable Empty { get; } = new();

    /// <summary>少女数（有 step 配置的）。</summary>
    public int GirlCount => _steps.Count;

    public bool IsSimple(int girlId) => Step(girlId, 0)?.SimpleCartoon == "1";

    /// <summary>阶段行总数。</summary>
    public int StepCount => _steps.Values.Sum(d => d.Count);

    /// <summary>任务条数。</summary>
    public int TaskCount => _tasks.Count;

    /// <summary>全部少女 id（升序）。</summary>
    public IEnumerable<int> AllGirlIds => _steps.Keys.OrderBy(v => v);

    /// <summary>取某少女某阶段的配置；不存在返回 null（同 <c>Pldge:GetStepCfg</c>）。</summary>
    public PledgeStepRow? Step(int girlId, int step)
        => _steps.TryGetValue(girlId, out var d) && d.TryGetValue(step, out var r) ? r : null;

    /// <summary>某少女的最大阶段号（用于判「已满级」）。</summary>
    public int MaxStep(int girlId)
        => _steps.TryGetValue(girlId, out var d) && d.Count > 0 ? d.Keys.Max() : 0;

    /// <summary>取任务配置；不存在返回 null（同 <c>Pldge:GetTaskCfg</c>）。</summary>
    public PledgeTaskRow? Task(int taskId)
        => _tasks.TryGetValue(taskId, out var t) ? t : null;

    /// <summary>
    /// 已开放的少女 id 列表（下发给 <c>Pldge_ReqOpenGirls</c>）。
    ///
    /// ★ 判据是 <b>step 0 的 <c>isopen</c> 日期已到</b>，而不是客户端那个恒 false 的
    /// <c>isopen == 1</c>（见 <see cref="PledgeStepRow.OpenTimeRaw"/> 的说明）。
    /// </summary>
    public IReadOnlyList<int> OpenGirlIds(DateTime now)
    {
        var list = new List<int>();
        foreach (int gid in AllGirlIds)
        {
            var begin = Step(gid, BeginStep);
            if (begin is not null && begin.IsOpenAt(now))
                list.Add(gid);
        }
        return list;
    }

    /// <summary>激活某少女要完成的那个任务（step0 的 <c>tasklist[1]</c>）。</summary>
    public PledgeTaskRow? ActivateTask(int girlId)
    {
        var begin = Step(girlId, BeginStep);
        if (begin is null || begin.TaskList.Count == 0)
            return null;
        return Task(begin.TaskList[0]);
    }

    /// <summary>从 setting 目录加载。缺表返回空表并打 WARN（不抛）。</summary>
    public static PledgeTable Load(string settingDir)
    {
        var table = new PledgeTable();

        var taskRaw = SettingTable.Load(Path.Combine(settingDir, TaskFileName), HeaderRow);
        if (taskRaw is null)
        {
            Log.Warn($"[setting] {TaskFileName} 缺失：誓约域降级为空表");
            return table;
        }
        foreach (var row in taskRaw.Rows)
        {
            int id = taskRaw.Int(row, "id", -1);
            if (id <= 0)
                continue;
            string type = taskRaw.Str(row, "strtype");
            string p1 = taskRaw.Str(row, "para1");
            table._tasks[id] = new PledgeTaskRow
            {
                Id = id,
                StrType = type,
                GirlId = taskRaw.Int(row, "girlid"),
                Count = taskRaw.Int(row, "count"),
                Recount = taskRaw.Int(row, "recount"),
                ParaName1 = taskRaw.Str(row, "paraname1"),
                Para1 = p1,
                ParaName2 = taskRaw.Str(row, "paraname2"),
                Para2 = taskRaw.Str(row, "para2"),
                // ★ 只有 ReduceItem 的 para1 是短横四元组（pldgecommon.lua:40）
                ReduceGdpl = type == "ReduceItem" ? ParseDashInts(p1) : Array.Empty<int>(),
            };
        }

        var stepRaw = SettingTable.Load(Path.Combine(settingDir, StepFileName), HeaderRow);
        if (stepRaw is null)
        {
            Log.Warn($"[setting] {StepFileName} 缺失：誓约域降级（任务表已载入 {table._tasks.Count} 条）");
            return table;
        }
        foreach (var row in stepRaw.Rows)
        {
            int gid = stepRaw.Int(row, "girlid", -1);
            // ★ girlid = 0 是表里的**模板/占位行**（实测仅 1 行：step0、tasklist 为空），
            //   不是真少女。放进来会让 OpenGirlIds 混进一个 0，
            //   客户端 GirlPromise.lua:553 把它 Add 进 int 列表后界面上多出一个空少女。
            if (gid <= 0)
                continue;
            var r = new PledgeStepRow
            {
                GirlId = gid,
                Step = stepRaw.Int(row, "step"),
                ChangeCard = stepRaw.Int(row, "changecard"),
                SimpleCartoon = stepRaw.Str(row, "SimpleCartoon"),
                CardGdpl = ParseDashInts(stepRaw.Str(row, "cardgdpl")),
                CardBreakLv = stepRaw.Int(row, "cardbreaklv"),
                CardLv = stepRaw.Int(row, "cardlv"),
                CardSkillLv = ParseDashInts(stepRaw.Str(row, "cardskilllv")),
                Heart = stepRaw.Int(row, "heart"),
                TaskList = ParseDashInts(stepRaw.Str(row, "tasklist")),
                Rewards = ParseRewards(stepRaw.Str(row, "rewards")),
                OpenTimeRaw = stepRaw.Str(row, "isopen"),
            };
            if (!table._steps.TryGetValue(gid, out var d))
                table._steps[gid] = d = new Dictionary<int, PledgeStepRow>();
            d[r.Step] = r;
        }

        int openNow = table.OpenGirlIds(DateTime.Now).Count;
        Log.Info($"[setting] 誓约配置：少女 {table.GirlCount} 位、阶段 {table.StepCount} 段、" +
                 $"任务 {table.TaskCount} 条；当前已开放 {openNow} 位" +
                 "（★ isopen 列是开放日期字符串，不是 0/1；客户端 tonumber 得 nil 恒判未开放，" +
                 "本服按日期判定并由服务端下发名单）");
        return table;
    }

    /// <summary>
    /// 逗号分隔奖励条目，每条内部用短横分隔；保留坏条目以便领取时整批拒绝。
    /// </summary>
    internal static IReadOnlyList<int[]> ParseRewards(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<int[]>();
        // Preserve invalid tuples as empty entries so claims reject the entire
        // configuration instead of advancing the watermark past a missing reward.
        return value.Split(',').Select(tuple =>
        {
            var parts = tuple.Trim().Split('-');
            var numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i].Trim(), out numbers[i])) return Array.Empty<int>();
            return numbers;
        }).ToArray();
    }

    /// <summary>cardgdpl/cardskilllv/tasklist 使用短横分隔，与 rewards 的外层逗号不同。</summary>
    internal static int[] ParseDashInts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<int>();
        var body = value.Trim().Trim('[', ']');
        if (body.Length == 0)
            return Array.Empty<int>();
        var result = new List<int>();
        foreach (var tok in body.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(tok.Trim(), out int n))
                result.Add(n);
        }
        return result.ToArray();
    }
}
