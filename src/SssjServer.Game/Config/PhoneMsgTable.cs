using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一条私信话题（<c>LetterInfoNew.txt</c> 里**同一个 <c>ID</c> 的连续多行**归并成一条）。
///
/// ★★ 这张表是「多行一条」结构：每行是对话里的一句
/// （<c>InfoType</c> 0=npc / 1=玩家 / 2=玩家选择，<c>PhoneMsgLogic.lua:275</c>），
/// <c>InfoFlag</c> 1=分支结束 2=彻底结束（<c>:276</c>）。
/// 服务端只需要「这条话题存在吗 / 属于哪个 NPC / 领奖给多少好感度」，
/// **对话内容全由客户端本地渲染**（见 <see cref="Handlers.PhoneMsgFeature"/> 的「只回显 id」结论）。
/// </summary>
public sealed class LetterTopic
{
    /// <summary>话题 id（<c>ID</c> 列）= 上行的 <c>nMsgId</c>。</summary>
    public int Id { get; init; }

    /// <summary>发起 NPC（<c>NpcId</c> 列）= 下行的 <c>nNpcId</c>，也是少女 id。</summary>
    public int NpcId { get; init; }

    /// <summary>
    /// 各选项对应的**好感度经验**（<c>AwardInfo</c> 列，**逗号**分隔，如 <c>25,20</c>）。
    ///
    /// ★★★ 这不是物品元组，是**加给少女的好感度经验**：
    /// <c>UI_Phone.lua:1006</c> <c>nAwardValue = tbConfig.AwardInfo[nSelectId]</c>
    /// → <c>:1038</c> 直接塞进 <c>ui.UI_Phone.AwardExp</c> 文案，
    /// 且 <c>S2CCall.lua:283</c> 的回包 <c>tbExp</c> 被
    /// <c>GirlLogic:OnGirlAddExp(nNpcId, unpack(tbParam.tbExp))</c> 消费。
    /// ⇒ 领奖要走**好感度加经验**（复用 <see cref="Handlers.GirlLoveFeature"/> 的 AddExp），
    /// 不是发物品。
    /// ★ 下标是 <c>nSelectId</c>（1-based，Lua 口径）。
    /// </summary>
    public int[] AwardExp { get; init; } = Array.Empty<int>();

    /// <summary>
    /// <c>Index</c> 列。<c>&gt; 0</c> 时客户端会在主城弹「新私信」气泡
    /// （<c>PhoneMsgLogic.lua:1302/1319</c> <c>letter.Index > 0</c> → <c>RemindManager:NewLetter</c>）。
    /// 服务端不用，仅备查。
    /// </summary>
    public int Index { get; init; }

    /// <summary>该话题共有几句（归并的行数）。仅日志/校验用。</summary>
    public int LineCount { get; init; }

    /// <summary>
    /// 逐句内容（<c>LetterInfoNew.txt</c> 里属于本话题的每一行，顺序即文件顺序）。
    ///
    /// <para><b>★★★ 为什么服务端需要它（第十四轮修「私信回复没反应」时补的）</b></para>
    /// 本类原注释断言「**对话内容全由客户端本地渲染**，服务端只需回显 id」——
    /// **那条只对了一半**。渲染确实是本地的，但**推进到第几句**是服务端存的：
    /// 客户端 <c>MakeLetterSingleInfo</c>（<c>PhoneMsgLogic.lua:...</c>）靠
    /// <c>Game.PhoneMsgMgr.GetLetterNpcReplyInfo(npcId, topicId, <b>nTopicPos</b>)</c>
    /// **按位置**在服务端下发的 <c>MsgId</c> 数组里查，查不到就 <c>return</c>（丢弃该句）。
    /// 而 <c>MsgId</c> 里的每个元素都是 <c>位置*10 + 分支</c> 的复合编码
    /// （<c>SplitReplyId</c>：<c>nPos = floor(id*0.1)</c>、<c>nSel = 个位</c>）。
    ///
    /// ⇒ 服务端要算出「玩家选的这个候选对应第几句」，就必须知道对话结构，
    /// 即移植 <c>GetNextReply</c>（见 <see cref="PhoneMsgTable.NextReplyPos"/>）。
    /// </summary>
    public IReadOnlyList<LetterLine> Lines { get; init; } = Array.Empty<LetterLine>();

    public override string ToString() => $"letter#{Id} npc={NpcId} lines={LineCount}";
}

/// <summary>
/// 私信话题里的**一句**（<c>LetterInfoNew.txt</c> 的一行）。
/// 对应客户端的 <c>tbConfig.SingleInfo[n]</c>（1-based）。
/// </summary>
public sealed class LetterLine
{
    /// <summary>
    /// 内容类型（<c>InfoType</c> 列）：0=NPC 说 / 1=玩家说 / 2=玩家**选择**（多选项）。
    /// 空单元格按 <b>0</b> 处理（表里 549 行为空）。
    /// </summary>
    public int InfoType { get; init; }

    /// <summary>
    /// 文本 key 数组（<c>Info</c> 列，**逗号**分隔）。
    /// <c>InfoType=2</c> 时有多个（每个候选一条），其余一般只有一个。
    /// ★ 值可能是字面量 <c>"NULL"</c> —— <c>GetNextReply</c> 用它表示「该分支到此为止」。
    /// </summary>
    public IReadOnlyList<string> Info { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 分支编号数组（<c>InfoNum</c> 列，**逗号**分隔；9329 行里 3417 行非空）。
    /// 空 = 这句对所有分支都显示（<c>GetNextReply</c> 里的
    /// <c>(not data.InfoNum or #data.InfoNum == 0)</c> 分支）。
    /// </summary>
    public IReadOnlyList<int> InfoNum { get; init; } = Array.Empty<int>();

    /// <summary>结束标记（<c>InfoFlag</c> 列）：1=阶段性结束 / 2=彻底结束。</summary>
    public int InfoFlag { get; init; }
}

/// <summary>一条朋友圈帖子（<c>BBSInfo.txt</c> 的一行）。</summary>
public sealed class BbsTopic
{
    /// <summary>帖子 id（<c>ID</c> 列）= 上行的 <c>nMsgId</c>。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 发帖 NPC（<c>NpcId</c> 列）。
    /// ★ <c>0</c> = **玩家可发表的帖子**（<c>PhoneMsgLogic.lua:1781</c>
    /// <c>if nNpcId == 0 then</c> 会把它从「可发表列表」里移除）。
    /// </summary>
    public int NpcId { get; init; }

    /// <summary>各回复选项对应的好感度经验（<c>AwardInfo</c> 列，逗号分隔）。语义同私信。</summary>
    public int[] AwardExp { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 「玩家可回复」的那一档下标（<c>PhoneMsgLogic.lua:591</c>：
    /// <c>ReplyId&lt;i&gt;</c> 的第 1 个数是 0 时 <c>ReplyIdx = i</c>）。
    /// <c>&gt; 0</c> 表示这帖可以回复；配合 <c>BBSInfo.replyinfo == 0</c>
    /// 决定红点（<c>PhoneMsgLogic:CheckBBSReply</c>，<c>:1827</c>）。
    /// </summary>
    public int ReplyIdx { get; init; }
    public IReadOnlyList<string> PlayerOptions { get; init; } = Array.Empty<string>();

    /// <summary>回复档数（<c>ReplyId1..N</c> 里实际有值的个数）。</summary>
    public int ReplyCount { get; init; }

    public override string ToString() => $"bbs#{Id} npc={NpcId} replyIdx={ReplyIdx}";
}

/// <summary>
/// 一条触发条件（<c>LetterEvent.txt</c> / <c>BBSEvent.txt</c> 的一行）。
///
/// <para><b>★★★ 第十二轮更正了一条重要旧结论</b></para>
/// 旧注释写「服务端不需要自己判定触发条件 —— 客户端自己判」。**只对了 1/9**：
/// 客户端 <c>CheckEvent</c>（<c>PhoneMsgLogic.lua:1928..1960</c>）**只实现了
/// <c>eventType == 1</c>（剧情）那一个分支**，其余 8 种类型走到函数末尾返回 nil；
/// 而唯一的调用点是 <c>CartoonData.lua:81..82</c>（播剧情 CG 时）。
/// ⇒ 类型 2/3/4/5/6/7/8/9 的触发**原本是原厂服务端的职责**，客户端压根不会发上行。
///
/// 后果（用户报的「私信/朋友圈没办法回复」的深层根因）：GM 全解锁账号跳过了
/// 正常推关与剧情播放 ⇒ 剧情触发从未发生、其余 8 类又没人判 ⇒ 终端里几乎没有内容
/// （实测存档只有 2 条私信 + 1 个帖子，且都已回到尽头）。
/// 服务端补发实现见 <c>PhoneMsgFeature.BackfillTriggers</c>。
///
/// <para><b>条件编码（取证 <c>PhoneMsgLogic:SplitLimitInfo</c>，<c>:499..545</c>）</b></para>
/// <list type="bullet">
///   <item><b>1 剧情</b>：字符串路径（<c>girl09/girl09_love03</c>）；</item>
///   <item><b>2 私信完成</b>：单个话题 id；</item>
///   <item><b>3 好感度等级</b>：单个等级数；</item>
///   <item><b>4 时间</b>：<c>2020022508-2020032704</c> 两段 <c>yyyyMMddHH</c>；</item>
///   <item><b>5 少女信息解锁</b>：单个 id；</item>
///   <item><b>6 角色卡</b>：<c>[1-11-1-3]</c> 四段 G-D-P-L；</item>
///   <item><b>7 完成成就</b>：单个成就 id；</item>
///   <item><b>8 通过关卡</b>：<c>[2-1-1]</c> 三段 章-关-难度；</item>
///   <item><b>9 好感度等级总和</b>：单个总和门槛。</item>
/// </list>
///
/// <para><b>多条件</b></para>
/// 一行最多 10 组（<c>LimitType1..10</c>）；实测 <c>LetterEvent</c> 71 行、
/// <c>BBSEvent</c> 36 行是多组的，且**单元格内从不含逗号**（客户端支持
/// <c>Split(",")</c> 但现网没用到）。多组语义按 <b>AND</b> 处理。
/// </summary>
public sealed class PhoneEvent
{
    /// <summary>事件 id（<c>ID</c> 列）。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 第一组触发类型（<c>LimitType1</c>）。真机分布：
    /// 1 剧情(108 条) / 3 好感度等级(64) / 8 关卡(56) / 7 成就(41) / 2 私信(17) / 4 时间(18) / 6 角色卡(11)。
    /// 语义见 <c>PhoneMsgLogic:SplitLimitInfo</c>（<c>:499..545</c>）。
    /// </summary>
    public int LimitType { get; init; }

    /// <summary>
    /// 第一组触发参数的原文（<c>LimitInfo1</c>）。
    /// ★ 类型 1 时是**剧情路径字符串**（如 <c>girl09/girl09_love03</c>），不是数字。
    /// </summary>
    public string LimitInfo { get; init; } = "";

    /// <summary>
    /// **全部**条件组（<c>LimitType1..10</c> 与 <c>LimitInfo1..10</c> 配对，AND 关系）。
    /// 第 1 项与 <see cref="LimitType"/>/<see cref="LimitInfo"/> 内容相同，
    /// 保留后两者是为了不破坏既有调用点。
    /// </summary>
    public IReadOnlyList<PhoneEventCond> Conds { get; init; } = Array.Empty<PhoneEventCond>();

    public override string ToString() => $"event#{Id} type={LimitType} info={LimitInfo}";
}

/// <summary>一组触发条件（<c>LimitType&lt;i&gt;</c> + <c>LimitInfo&lt;i&gt;</c>）。</summary>
public sealed record PhoneEventCond(int Type, string Info);


/// <summary>
/// 终端（手机）私信 + 朋友圈的配置表：
/// <c>LetterInfoNew.txt</c> / <c>LetterEvent.txt</c> / <c>BBSInfo.txt</c> /
/// <c>BBSEvent.txt</c> / <c>NpcIdList.txt</c>。
///
/// <para><b>表头全是 3 行</b></para>
/// 权威取证是客户端自己的 <c>LoadTabFile(path, 3)</c> 第 2 实参
/// （<c>PhoneMsgLogic.lua</c> 的 `:57/:229/:459/:565/:617/:676` 六处全是 3）——
/// 照 `AGENTS.md` §5，这是判断表头行数的唯一可靠依据。
///
/// <para><b>★★★ 服务端在本域的角色非常轻</b></para>
/// 客户端自带全套渲染逻辑（<c>Game.PhoneMsgMgr</c> 的 <c>AddLetterReplyInfo</c> 等
/// 都是 **client static**，自己 <c>new Game.LetterInfo</c>、自己取时间戳）
/// ⇒ 服务端只做三件事：
/// <list type="number">
///   <item><b>登录时用 pf 1035 全量下发</b>「有哪些私信/帖子、进行到哪、领没领奖」；</item>
///   <item>校验上行 id 合法（在配置表里）；</item>
///   <item>把状态变更**持久化**并回显 id。</item>
/// </list>
///
/// <para><b>本地表规模</b></para>
/// <c>LetterInfoNew.txt</c> 9122 行 → 归并成 **539 条话题**；
/// <c>LetterEvent.txt</c> 308 条；<c>BBSInfo.txt</c> 274 条；<c>BBSEvent.txt</c> 152 条；
/// <c>NpcIdList.txt</c> 30 条。
/// ★ <c>LetterEvent</c> 的 308 个 id 里只有 **174 个**能在 <c>LetterInfoNew</c> 找到对应话题
/// （134 个是孤儿，大概率指向旧表 <c>LetterInfo.txt</c> 或已下线内容）
/// ⇒ **校验时找不到话题要优雅跳过，不能当错误**。
/// </summary>
public sealed class PhoneMsgTable
{
    public const string LetterFileName = "LetterInfoNew.txt";
    public const string LetterEventFileName = "LetterEvent.txt";
    public const string BbsFileName = "BBSInfo.txt";
    public const string BbsEventFileName = "BBSEvent.txt";
    public const string NpcFileName = "NpcIdList.txt";

    /// <summary>论坛回复档位上限（<c>PhoneMsgLogic.Max_BBS_Re</c>，<c>PhoneMsgLogic.lua:31</c>）。</summary>
    public const int MaxBbsReply = 30;

    private readonly Dictionary<int, LetterTopic> _letters = new();
    private readonly Dictionary<int, BbsTopic> _bbs = new();
    private readonly Dictionary<int, PhoneEvent> _letterEvents = new();
    private readonly Dictionary<int, PhoneEvent> _bbsEvents = new();
    private readonly Dictionary<int, string> _npcNames = new();

    private PhoneMsgTable() { }

    /// <summary>永久空表（缺表降级：所有 nCmd 回错误码，但**仍然回包**以解遮罩）。</summary>
    public static PhoneMsgTable Empty { get; } = new();

    public int LetterCount => _letters.Count;
    public int BbsCount => _bbs.Count;
    public int LetterEventCount => _letterEvents.Count;
    public int BbsEventCount => _bbsEvents.Count;
    public int NpcCount => _npcNames.Count;

    public IEnumerable<LetterTopic> AllLetters => _letters.Values.OrderBy(l => l.Id);
    public IEnumerable<BbsTopic> AllBbs => _bbs.Values.OrderBy(b => b.Id);

    /// <summary>全部私信触发条件（按 id 升序），<c>PhoneMsgFeature.BackfillTriggers</c> 用。</summary>
    public IEnumerable<PhoneEvent> AllLetterEvents => _letterEvents.Values.OrderBy(e => e.Id);

    /// <summary>全部论坛触发条件（按 id 升序）。</summary>
    public IEnumerable<PhoneEvent> AllBbsEvents => _bbsEvents.Values.OrderBy(e => e.Id);

    public LetterTopic? Letter(int id) => _letters.TryGetValue(id, out var v) ? v : null;
    public BbsTopic? Bbs(int id) => _bbs.TryGetValue(id, out var v) ? v : null;
    public PhoneEvent? LetterEvent(int id) => _letterEvents.TryGetValue(id, out var v) ? v : null;
    public PhoneEvent? BbsEvent(int id) => _bbsEvents.TryGetValue(id, out var v) ? v : null;
    public string NpcName(int id) => _npcNames.TryGetValue(id, out var v) ? v : $"npc{id}";

    /// <summary>该 NPC 是否在 <c>NpcIdList.txt</c> 里。</summary>
    public bool HasNpc(int id) => _npcNames.ContainsKey(id);

    /// <summary>
    /// 取某个选项的好感度经验（1-based 的 <c>nSelectId</c>，越界返回 0）。
    /// </summary>
    public static int AwardExpAt(int[] awards, int selectId)
        => selectId >= 1 && selectId <= awards.Length ? awards[selectId - 1] : 0;

    public static PhoneMsgTable Load(string settingDir)
    {
        var table = new PhoneMsgTable();

        // ---- LetterInfoNew.txt：多行一条，按 ID 归并 ----
        var letterRaw = SettingTable.Load(Path.Combine(settingDir, LetterFileName));
        if (letterRaw is null)
        {
            Log.Warn($"[setting] {LetterFileName} 缺失：私信域降级");
        }
        else
        {
            // 同一个 ID 的多行里，NpcId/InfoTitle/AwardInfo/Index 只在**首个有值的行**上
            var lineCount = new Dictionary<int, int>();
            var npcOf = new Dictionary<int, int>();
            var awardOf = new Dictionary<int, int[]>();
            var indexOf = new Dictionary<int, int>();
            // ★ 逐句结构（顺序即文件顺序 = 客户端 SingleInfo 的 1-based 下标）
            var linesOf = new Dictionary<int, List<LetterLine>>();

            foreach (var row in letterRaw.Rows)
            {
                int id = letterRaw.Int(row, "ID", -1);
                if (id <= 0)
                    continue;

                lineCount[id] = lineCount.TryGetValue(id, out int n) ? n + 1 : 1;

                int npc = letterRaw.Int(row, "NpcId", 0);
                if (npc > 0 && !npcOf.ContainsKey(id))
                    npcOf[id] = npc;

                // ★ 客户端只取「第一个带 AwardInfo 的行」（PhoneMsgLogic.lua:268
                //   `if not tbLetterInfo.SelectIdx and tbRow["AwardInfo"] then`）
                string awardText = letterRaw.Str(row, "AwardInfo");
                if (awardText.Length > 0 && !awardOf.ContainsKey(id))
                    awardOf[id] = ParseCommaInts(awardText);

                int idx = letterRaw.Int(row, "Index", 0);
                if (idx > 0 && !indexOf.ContainsKey(id))
                    indexOf[id] = idx;

                // ★★ Info 与 InfoNum 都是**逗号**分隔（实测：InfoType=2 的行形如
                //    Info=[girl10_905_1,girl10_905_2] InfoNum=[1,2]）。
                //    AGENTS.md §5 有「同表两个 INTARR 列分隔符可能不同」的坑，
                //    这两列已逐个核对过，都是逗号。
                if (!linesOf.TryGetValue(id, out var list))
                    linesOf[id] = list = new List<LetterLine>();
                list.Add(new LetterLine
                {
                    // 空单元格按 0（NPC 说）处理 —— 表里 549 行为空
                    InfoType = letterRaw.Int(row, "InfoType", 0),
                    Info = ParseCommaStrings(letterRaw.Str(row, "Info")),
                    InfoNum = ParseCommaInts(letterRaw.Str(row, "InfoNum")),
                    InfoFlag = letterRaw.Int(row, "InfoFlag", 0),
                });
            }

            foreach (var (id, lines) in lineCount)
            {
                table._letters[id] = new LetterTopic
                {
                    Id = id,
                    NpcId = npcOf.TryGetValue(id, out int npc) ? npc : 0,
                    AwardExp = awardOf.TryGetValue(id, out var aw) ? aw : Array.Empty<int>(),
                    Index = indexOf.TryGetValue(id, out int ix) ? ix : 0,
                    LineCount = lines,
                    Lines = linesOf.TryGetValue(id, out var ls)
                        ? ls
                        : (IReadOnlyList<LetterLine>)Array.Empty<LetterLine>(),
                };
            }
        }

        // ---- BBSInfo.txt ----
        var bbsRaw = SettingTable.Load(Path.Combine(settingDir, BbsFileName));
        if (bbsRaw is null)
        {
            Log.Warn($"[setting] {BbsFileName} 缺失：朋友圈域降级");
        }
        else
        {
            foreach (var row in bbsRaw.Rows)
            {
                int id = bbsRaw.Int(row, "ID", -1);
                if (id <= 0)
                    continue;

                // ReplyIdx：ReplyId<i> 的第 1 个数为 0 的那一档（PhoneMsgLogic.lua:591）
                int replyIdx = 0, replyCount = 0;
                for (int i = 1; i <= MaxBbsReply; i++)
                {
                    string idText = bbsRaw.Str(row, $"ReplyId{i}");
                    string infoText = bbsRaw.Str(row, $"ReplyInfo{i}");
                    if (idText.Length == 0 || infoText.Length == 0)
                        continue;
                    replyCount++;
                    var ids = ParseCommaInts(idText);
                    if (ids.Length > 0 && ids[0] == 0)
                        replyIdx = i;
                }

                table._bbs[id] = new BbsTopic
                {
                    Id = id,
                    NpcId = bbsRaw.Int(row, "NpcId", 0),
                    AwardExp = ParseCommaInts(bbsRaw.Str(row, "AwardInfo")),
                    ReplyIdx = replyIdx,
                    PlayerOptions = replyIdx > 0 ? ParseCommaStrings(bbsRaw.Str(row, $"ReplyInfo{replyIdx}")) : Array.Empty<string>(),
                    ReplyCount = replyCount,
                };
            }
        }

        LoadEvents(settingDir, LetterEventFileName, table._letterEvents);
        LoadEvents(settingDir, BbsEventFileName, table._bbsEvents);

        // ---- NpcIdList.txt ----
        var npcRaw = SettingTable.Load(Path.Combine(settingDir, NpcFileName));
        if (npcRaw is not null)
        {
            foreach (var row in npcRaw.Rows)
            {
                int id = npcRaw.Int(row, "ID", -1);
                if (id <= 0)
                    continue;
                table._npcNames[id] = npcRaw.Str(row, "Name");
            }
        }

        Log.Info($"[setting] 终端私信/朋友圈载入：私信话题 {table.LetterCount} 条" +
                 $"（{LetterFileName}）、触发 {table.LetterEventCount} 条；" +
                 $"帖子 {table.BbsCount} 条、触发 {table.BbsEventCount} 条；NPC {table.NpcCount} 个");
        return table;
    }

    private static void LoadEvents(string settingDir, string file, Dictionary<int, PhoneEvent> into)
    {
        var raw = SettingTable.Load(Path.Combine(settingDir, file));
        if (raw is null)
        {
            Log.Warn($"[setting] {file} 缺失：剧情触发校验降级为「不校验」");
            return;
        }

        foreach (var row in raw.Rows)
        {
            int id = raw.Int(row, "ID", -1);
            if (id <= 0)
                continue;

            // ★ LimitType1..10 与 LimitInfo1..10 配对（客户端 PhoneMsgLogic.lua:466 也是 1..10）。
            //   用 Columns.ContainsKey 判列是否存在 —— SettingTable.Str 对不存在的列返回空串，
            //   靠返回值判断会多跑 10 次（无害但语义不清）。
            var conds = new List<PhoneEventCond>();
            for (int j = 1; j <= 10; j++)
            {
                if (!raw.Columns.ContainsKey($"LimitType{j}"))
                    break;
                var type = raw.Str(row, $"LimitType{j}").Trim();
                var info = raw.Str(row, $"LimitInfo{j}").Trim();
                if (type.Length == 0)
                    continue;
                if (!int.TryParse(type, out int t) || t <= 0)
                    continue;
                conds.Add(new PhoneEventCond(t, info));
            }

            into[id] = new PhoneEvent
            {
                Id = id,
                LimitType = raw.Int(row, "LimitType1", 0),
                LimitInfo = raw.Str(row, "LimitInfo1"),
                Conds = conds,
            };
        }
    }

    /// <summary>
    /// 解析逗号分隔的整数列表（客户端 <c>GetArrayFromStr(s, true)</c>，`Lib.lua:980`）。
    /// ★ 第 2 参 <c>bNotSub = true</c> ⇒ **不**掐首尾字符，所以这些列是**裸值**
    /// （如 <c>25,20</c>），不带方括号 —— 与 <c>PurchaseIBShop.GDPL</c> 的
    /// <c>[13,2,1,6026,1]</c> 不同。仍然 Trim 一次方括号以防个别行带括号。
    /// </summary>
    internal static int[] ParseCommaInts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<int>();
        var body = value.Trim().Trim('[', ']');
        if (body.Length == 0)
            return Array.Empty<int>();

        var result = new List<int>();
        foreach (var tok in body.Split(',', StringSplitOptions.TrimEntries))
        {
            // ★ 空位要保留 0（AwardInfo 里 "0,Lgirl07_002,0" 这类混合内容也不炸）
            result.Add(int.TryParse(tok, out int n) ? n : 0);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 逗号分隔的字符串数组（<c>Info</c> 列）。空单元格 → 空数组。
    /// ★ 不做 <c>"NULL"</c> 过滤 —— 那是 <see cref="NextReplyPos"/> 的语义标记，必须保留。
    /// </summary>
    internal static string[] ParseCommaStrings(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<string>();
        var body = value.Trim().Trim('[', ']');
        if (body.Length == 0)
            return Array.Empty<string>();
        return body.Split(',', StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 私信「回复 id」的编码基数：<c>replyId = 位置 * 10 + 分支</c>。
    ///
    /// 取证：客户端 <c>PhoneMsgLogic:SplitReplyId</c>
    /// <code>
    /// local nPos, nSelectId = math.modf(nReplyId * 0.1);
    /// nSelectId = nSelectId * 10 + 0.1;   -- 81会出问题..（原厂注释）
    /// return math.floor(nPos), math.floor(nSelectId);
    /// </code>
    /// 真机实测：<c>SplitReplyId(31) → (3,1)</c>、<c>SplitReplyId(1) → (0,1)</c>。
    /// </summary>
    public const int ReplyIdBase = 10;

    /// <summary>
    /// 把「对话位置 + 玩家选的分支」编码成客户端要的 <c>replyId</c>。
    ///
    /// <para><b>★★★ 这是「私信回复点了没反应」的修复核心（第十四轮）</b></para>
    /// 服务端此前把上行的 <c>nSelectId</c>（**候选序号**，UI_Phone.lua:707
    /// <c>table.insert(self.tbSelectList, i)</c> ⇒ 恒为 1/2/3…）**原样**存进
    /// <c>MsgId</c> 数组下发。而客户端拿这个数组是**按位置查**的：
    /// <code>
    /// MakeLetterSingleInfo: nReplyId = GetLetterNpcReplyInfo(npcId, topicId, nTopicPos)
    ///                       if nReplyId == 0 then return end   -- ★ 丢弃这一句
    /// </code>
    /// 序号 1 经 <c>SplitReplyId</c> 得位置 <b>0</b>，永远匹配不上真实位置（3、5、7…）
    /// ⇒ 每次查都返回 0 ⇒ 玩家的回复与 NPC 的后续台词**全部被丢弃**
    /// ⇒ 界面纹丝不动 =「点了没反应」。
    ///
    /// ★ 症状极具迷惑性：服务端日志显示「私信回复 成功」、零 unhandled、
    /// 客户端零 Lua 报错、存档也确实记了一笔 —— **四条常规判据全是绿的**。
    /// 只有真机点进去看界面才会发现对话没推进。
    /// </summary>
    /// <param name="pos">对话位置（1-based，即客户端 <c>SingleInfo</c> 的下标）。</param>
    /// <param name="branch">玩家选的分支号（1-based；无分支时传 1）。</param>
    public static int EncodeReplyId(int pos, int branch)
        => pos * ReplyIdBase + Math.Clamp(branch, 0, ReplyIdBase - 1);

    /// <summary>位置部分（<c>math.floor(id * 0.1)</c>）。</summary>
    public static int ReplyPosOf(int replyId) => replyId / ReplyIdBase;

    /// <summary>分支部分（个位）。</summary>
    public static int ReplyBranchOf(int replyId) => replyId % ReplyIdBase;

    /// <summary>
    /// 移植客户端 <c>PhoneMsgLogic:GetNextReply</c>：从 <paramref name="curPos"/>
    /// 向后找「下一句该显示的位置」，找不到返回 0（= 对话结束）。
    ///
    /// <para><b>原文（<c>PhoneMsgLogic.lua</c>）</b></para>
    /// <code>
    /// for i = nCurPos+1, #SingleInfo do
    ///     local data = SingleInfo[i];
    ///     if data.Info[1] == "NULL" and (not data.InfoNum or data.InfoNum[1] == selectId) then return end
    ///     if data.InfoType == 2 then return i;
    ///     elseif (not data.InfoNum or #data.InfoNum == 0) and data.InfoType >= nCheckType then return i; end
    ///     if tbCurInfo.InfoNum and #tbCurInfo.InfoNum == 1 then
    ///         if data.InfoNum and data.InfoNum[1] == tbCurInfo.InfoNum[1] and data.InfoType >= nCheckType then return i; end
    ///     elseif tbCurInfo.InfoType == 2 and data.InfoNum and data.InfoNum[1] == selectId and data.InfoType >= nCheckType then
    ///         return i;
    ///     end
    /// end
    /// </code>
    /// ★ <paramref name="playerReply"/> 对应原文的 <c>bPlayerReply</c>：
    /// true 时 <c>nCheckType = 1</c>（只要玩家说的那些句），false 时 0（NPC 的也要）。
    /// </summary>
    /// <param name="topic">话题配置。</param>
    /// <param name="curPos">当前位置（1-based）。</param>
    /// <param name="selectId">玩家当前选择的分支号。</param>
    /// <param name="playerReply">是否只找「玩家说」的句子。</param>
    /// <returns>下一句的位置（1-based）；没有下一句返回 0。</returns>
    public static int NextReplyPos(LetterTopic topic, int curPos, int selectId, bool playerReply = false)
    {
        var lines = topic.Lines;
        if (lines.Count == 0 || curPos < 1 || curPos > lines.Count)
            return 0;

        var cur = lines[curPos - 1];
        int checkType = playerReply ? 1 : 0;

        for (int i = curPos + 1; i <= lines.Count; i++)
        {
            var data = lines[i - 1];

            // data.Info[1] == "NULL" ⇒ 该分支到此为止
            if (data.Info.Count > 0 && data.Info[0] == "NULL"
                && (data.InfoNum.Count == 0 || data.InfoNum[0] == selectId))
                return 0;

            if (data.InfoType == 2)
                return i;
            if (data.InfoNum.Count == 0 && data.InfoType >= checkType)
                return i;

            if (cur.InfoNum.Count == 1)
            {
                if (data.InfoNum.Count > 0 && data.InfoNum[0] == cur.InfoNum[0]
                    && data.InfoType >= checkType)
                    return i;
            }
            else if (cur.InfoType == 2 && data.InfoNum.Count > 0
                     && data.InfoNum[0] == selectId && data.InfoType >= checkType)
            {
                return i;
            }
        }
        return 0;
    }
}
