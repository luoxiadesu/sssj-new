using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 手机短信系统（私信 / 论坛 BBS）。
///
/// ★ 这**不是** LuaCall 数字命令，是 <c>S2CCall.lua</c> 那一族「字符串 sFunction」命令：
/// <code>
/// 上行 客户端 WeaponCommon:SendToServer("PhoneMsg", tbParam, bNoWait)  (WeaponCommon.lua:286)
///        → SendCmd("PhoneMsg", tbParam)                               (Lib.lua:378)
///        → Game.Player.CallServer("PhoneMsg", Json.Encode(tbParam))
///        → pf 1022 C2SCallReq{ sFunction="PhoneMsg", sJsonParam=&lt;tbParam 的 JSON&gt; }
/// 下行 pf 1024 NtfS2CCall{ sFunction=&lt;命令名&gt;, sJsonParam=&lt;param 的 JSON&gt; }
///        → Game.Player.OnCallClient → S2C.ProcCmd(sFunction, sJsonParam)  (S2CCall.lua:10)
///        → S2C[sFunction](Json.Decode(sJsonParam))                        (S2CCall.lua:64)
/// </code>
/// 不分片：<c>S2C.ProcCmd</c> 只对 <c>sFunction == "LuaCall"</c> 做
/// <c>{CallTime,CallEnd,CallCmd}</c> 分片重组（S2CCall.lua:29），其它命令一律整包 Json.Decode
/// —— 所以下行必须走 <c>LuaCallRouter.CallClientFunction</c>（它本身就拒绝分片）。
///
/// ★★ 上行与下行的命令名**不同**（这是本次实现里最容易踩错的一点）：
/// <list type="bullet">
/// <item>上行 sFunction = <c>"PhoneMsg"</c>（13 处发起点全是这个名字，见下）。</item>
/// <item>下行 sFunction = <c>"ServerPhoneMsg"</c> —— <c>S2CCall.lua</c> 里**只有**
///   <c>S2C.ServerPhoneMsg</c>（S2CCall.lua:245），全仓 806 个 .lua **没有** <c>S2C.PhoneMsg</c>
///   （<c>grep -rn "S2C\.PhoneMsg" extracted/lua</c> 无命中）。
///   若按 "PhoneMsg" 回，客户端 <c>S2C.ProcCmd</c> 的 <c>type(S2C[sCmd]) ~= 'function'</c> 分支
///   会直接打 <c>S2C.ProcCmd error PhoneMsg</c> 并丢弃（S2CCall.lua:11..12），
///   连 <c>Game.Blocking.Hide()</c> 都不会执行。</item>
/// </list>
///
/// ---- 本次实现的子命令：nCmd = 8（TYPE_BBSAPPLYLIST，论坛「可发表帖子 id 列表」）----
/// 上行取证（登录进主城后立刻发的就是这一条）：
/// <code>
/// UI_Main:OnOpen                     (UI_Main.lua:722)  → PhoneMsgLogic:ToGetBSSApplyList()
/// PhoneMsgLogic:MakeBBSAll           (PhoneMsgLogic.lua:1484) → 同上
/// PhoneMsgLogic:ToGetBSSApplyList    (PhoneMsgLogic.lua:1796):
///     if self.LOAD_BBS_APPLY_LIST then return end        -- 每次登录只发一次
///     WeaponCommon:SendToServer("PhoneMsg", {nCmd = self.TYPE_BBSAPPLYLIST}, true)
///     self.LOAD_BBS_APPLY_LIST = true
/// </code>
/// <c>TYPE_BBSAPPLYLIST = 8</c>（PhoneMsgLogic.lua:38）；第三个参数 <c>bNoWait = true</c>
/// 所以**不挂** <c>Game.Blocking</c> 遮罩（WeaponCommon.lua:287）。
/// 上行 tbParam 只有 nCmd 一个字段，与实测 <c>{"nCmd":8}</c> 一致。
///
/// 下行 <c>S2C.ServerPhoneMsg</c> 对 nCmd==8 的处理（S2CCall.lua:246..305）要求的字段：
/// <list type="number">
/// <item><c>nError</c> —— **必须存在且为数字**：函数第 2 行就是
///   <c>if tbParam and tbParam.nError &gt; 0 then</c>（S2CCall.lua:247），
///   Lua 里 <c>nil &gt; 0</c> 直接抛「attempt to compare nil with number」。
///   0 = 成功；&gt;0 会弹 <c>ui.Error.&lt;nError&gt;</c> 消息框并 return。</item>
/// <item><c>nCmd</c> —— 分发依据（<c>elseif tbParam.nCmd == 8 then</c>，S2CCall.lua:297）。</item>
/// <item><c>tbList</c> —— <c>PhoneMsgLogic:ServerResponseBBSApplyList(tbParam.tbList)</c>
///   （S2CCall.lua:298 → PhoneMsgLogic.lua:1809）：<c>if not tbList then return end</c> 后
///   <c>self.tbBBSApplyList = tbList</c> 整体覆盖。
///   元素是**扁平的帖子 id 数字**：新增走 <c>table.insert(self.tbBBSApplyList, nMsgId)</c>
///   （PhoneMsgLogic.lua:1823），消费处 <c>for i,v in ipairs(...)</c> 后
///   <c>PhoneMsgLogic:GetBBSInfo(v)</c> 当配置 id 用（UI_Phone.lua:1747..1750）。
///   虽然 nil 也安全，但必须给空数组：<c>ServerResponseAddBBSApply</c>（PhoneMsgLogic.lua:1822）
///   直接 <c>table.insert(self.tbBBSApplyList, ...)</c>，表不存在会抛。</item>
/// </list>
/// 之后客户端刷新红点：UI_Phone 开着就 <c>ShowMainPanel(TYPE_BBS)</c>，否则
/// <c>UI_Main:ShowPhoneMsgRedPoint()</c>（S2CCall.lua:299..307 → UI_Main.lua:1385）。
/// 空列表 → <c>CalBBSRedPoint</c>（PhoneMsgLogic.lua:1838）不点亮红点，符合新号预期。
///
/// ---- 与 pf 1035 PhoneMsgNtf 的关系（本次**不实现**）----
/// docs/pf-table.txt:34 有 <c>1035 Register&lt;gsproto.PhoneMsgNtf&gt; → Game.Login.PlayerPhoneMsg</c>。
/// 那是**另一条通道**：protobuf 推送，登录时由 <c>Game.Login</c> 灌入
/// <c>Game.PhoneMsgMgr</c> 的存量私信/帖子（客户端侧的 <c>AddLetterReplyInfo</c> /
/// <c>AddBBSReplyInfo</c> 等都读这份缓存）。本文件实现的字符串命令族只负责**增量交互**
/// （请求列表、回帖、领奖、删私信）。两者互不替代：1035 不发，客户端就只有配置表默认数据；
/// 本命令不回，红点/可发表列表就一直是空。按任务要求本次只做字符串命令这一条。
///
/// ---- 第九轮：其余 nCmd 已全部实现 ----
/// 本域现已覆盖 <b>1/3/4/7/8/10/11/12/13</b> 九条上行 + <b>5/6/9</b> 三条纯下行通道，
/// 并新增 <b>pf 1035 登录全量下发</b>（那才是整域的真正前置，见下）。
/// 逐条契约、状态模型与踩坑见 `docs/05-feature-domains.md` §35 与
/// `work/round9-mainsession-forensics.md` §5。
///
/// <para><b>★★★ pf 1035 是整域的前置：不推它，两个页签永远空白</b></para>
/// 客户端私信列表的唯一数据源是 <c>PhoneMsgLogic:MakeLetterAll</c>
/// （`PhoneMsgLogic.lua:709..734`）里的
/// <c>Game.PhoneMsgMgr.GetAllLetterInfo(list)</c> —— 读的是 C# 侧缓存，
/// 而那份缓存**只由 pf 1035 `PhoneMsgNtf` 填充**
/// （<c>Game.Login.PlayerPhoneMsg</c> RVA `0x1C8CC98` →
/// <c>Game.PhoneMsgMgr.PhoneMsgData</c> `0x1844BE0`）。
/// ⇒ 12 个 nCmd 实现得再全，不推 1035 也**什么都看不见**
/// （与 §27.2「`ELevel` 不置 1 ⇒ 整域看起来没实现」同型的陷阱）。
///
/// ★★ <c>PhoneMsgData</c> 对两个字典都是**先 `Clear()` 再逐条 Add**
/// （`0x1844C78` / `0x1844CF0`）⇒ **pf 1035 是全量覆盖语义**，
/// 任何增量改动都要重推全量（本域在每条命令处理后调 <see cref="PushAll"/>）。
///
/// <para><b>★★ 关键简化：回包只回显 id，客户端自己造对话数据</b></para>
/// <c>S2C.ServerPhoneMsg</c> 每个分支都是「先调 <c>Game.PhoneMsgMgr.AddXxx(...)</c>
/// 拿 <c>tbInfo</c>，再交给 <c>PhoneMsgLogic:ServerResponseXxx</c>」，
/// 而那些 <c>AddXxx</c> 全是**客户端 static 方法**（`dump.cs:557711..557750`），
/// 自己 <c>new Game.LetterInfo</c>（`0x18456AC`）、自己取
/// <c>ServerStatus.Timestamp</c>（`0x18456E4`）。
/// ⇒ 服务端**不需要构造 LetterInfo 结构体**，只回 <c>nNpcId/nMsgId/nSelectId</c> 几个数字即可。
/// 这与邮件域「必须构造完整 CMailInfo」完全不同。
///
/// </summary>
public sealed class PhoneMsgFeature : IGameFeature, IGamePulseFeature
{
    /// <summary>上行 sFunction（客户端 → 服务端）。</summary>
    public const string UpFunction = "PhoneMsg";

    /// <summary>
    /// 下行 sFunction（服务端 → 客户端）= <c>S2C.ServerPhoneMsg</c>（S2CCall.lua:245）。
    /// 注意与 <see cref="UpFunction"/> 不同名，理由见类注释。
    /// </summary>
    public const string DownFunction = "ServerPhoneMsg";

    // ---- 子命令（PhoneMsgLogic.lua:34..43 的 TYPE_* 常量 + UI_Phone.lua:8..10）----

    /// <summary>论坛回帖 / 发新帖（<c>UI_Phone.TYPE_BBS = 1</c>）。</summary>
    public const int CmdBbs = 1;

    /// <summary>私信回复（<c>UI_Phone.TYPE_LETTER = 3</c>）。</summary>
    public const int CmdLetter = 3;

    /// <summary>私信改标题（<c>TYPE_LETTERTITLE</c>）。</summary>
    public const int CmdLetterTitle = 4;

    /// <summary>生气回复（<c>TYPE_LETTERANGRY</c>）—— ★ 纯下行，无上行发起点。</summary>
    public const int CmdLetterAngry = 5;

    /// <summary>不生气了（<c>TYPE_LETTERANGRYDONE</c>）—— ★ 纯下行。</summary>
    public const int CmdLetterAngryDone = 6;

    /// <summary>私信领奖（<c>TYPE_LETTERAWARD</c>）。</summary>
    public const int CmdLetterAward = 7;

    /// <summary>论坛可发表帖子 id 列表（<c>TYPE_BBSAPPLYLIST</c>）。</summary>
    public const int CmdBbsApplyList = 8;

    /// <summary>论坛新增可发表帖子（<c>TYPE_BBSNEWAPPLY</c>）—— ★ 纯下行。</summary>
    public const int CmdBbsNewApply = 9;

    /// <summary>删除私信（<c>TYPE_LETTERDEL</c>）。</summary>
    public const int CmdLetterDel = 10;

    /// <summary>获取私信（<c>TYPE_LETTERGET</c>）。</summary>
    public const int CmdLetterGet = 11;

    /// <summary>剧情触发私信（<c>TYPE_LETTEREVENT</c>）。</summary>
    public const int CmdLetterEvent = 12;

    /// <summary>剧情触发论坛（<c>TYPE_BBSEVENT</c>）。</summary>
    public const int CmdBbsEvent = 13;

    /// <summary>成功（`S2CCall.lua:247` 是 <c>nError &gt; 0</c> 数值比较 ⇒ 必须带 0）。</summary>
    public const int ErrOk = 0;

    /// <summary>通用失败码。</summary>
    public const int ErrGeneric = 1;

    /// <summary>「清空标题」的固定词（`UI_Phone.lua:892` 注释「固定词汇 配合服务器修改」）。</summary>
    public const string ClearTitleToken = "ClearLetterTitle";

    /// <summary>不允许改标题的 NPC（`UI_Phone.lua:862` 客户端也拦，服务端兜一层）。</summary>
    public const int FixedTitleNpcId = 111;

    /// <summary><see cref="Player.Extra"/> 里本功能的键。</summary>
    public const string ExtraKey = "phonemsg";

    /// <summary>Extra["phonemsg"] 里存「可发表帖子 id 列表」的字段名。</summary>
    public const string BbsApplyField = "bbsApply";

    private GameFeatureContext _ctx = null!;
    private PhoneMsgTable _table = PhoneMsgTable.Empty;
    private CartoonTable _cartoons = null!;

    /// <summary>
    /// 好感度曲线（私信/回帖领奖要加少女经验）。★ 本域自己 Load 一份而不是
    /// 「跨 feature 拿 GirlLoveFeature 实例」—— 理由见 <see cref="AttachGirlExp"/>。
    /// </summary>
    private GirlLoveTable _girlLove = GirlLoveTable.Empty;

    /// <summary>关卡表（触发类型 8「通过关卡」要把 章-关-难度 换成 mapId）。</summary>
    private ChapterTable _chapters = ChapterTable.Empty;


    public string Name => "终端私信 / 朋友圈（sFunction PhoneMsg + pf 1035）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string dir = Path.GetFullPath(ctx.Options.SettingDirectory);
        _table = PhoneMsgTable.Load(dir);
        _cartoons = CartoonTable.Load(dir);
        _girlLove = GirlLoveTable.Load(dir);
        _chapters = ChapterTable.Load(dir);
        ctx.MapFunction(UpFunction, OnPhoneMsg);

        // ★★★ 登录时全量下发 pf 1035 —— 整域的前置（见类注释）。
        // 用 OnPlayerLogin（1005 之后）而不是 OnPlayerPrepare：这是一条独立封包，
        // 且客户端 MakeLetterAll 是「进界面时才读缓存」，不要求在 1005 之前到达。
        //
        // ★★★ 下发**之前**先补发非剧情类触发（第十二轮新增，见 BackfillTriggers）：
        // 顺序很重要 —— 先落状态再 PushAll，玩家一进游戏就能看到该有的内容。
        ctx.OnPlayerLogin((session, player) =>
        {
            MigrateLegacyReplyIds(session, player);
            BackfillTriggers(session, player);
            PushAll(session, player);
        });
    }

    /// <summary>
    /// ★★★ 存量修复：把历史存档里的**裸候选序号**重算成 <c>位置*10+分支</c> 编码。
    ///
    /// <para><b>为什么必须做</b></para>
    /// 第十四轮之前，<c>HandleLetterReply</c> 把上行的候选序号（1/2/3…）原样存进
    /// <c>MsgIds</c>。这些值经客户端 <c>SplitReplyId</c> 解出的**位置恒为 0**
    /// （<c>floor(1*0.1)=0</c>）⇒ <c>GetLetterNpcReplyInfo</c> 永远查不到
    /// ⇒ 那些话题**永久卡住**，光修 handler 不迁移数据，老号依旧「点了没反应」。
    ///
    /// <para><b>判据</b></para>
    /// 一个元素是「脏数据」当且仅当 <c>0 &lt; v &lt; ReplyIdBase</c>
    /// （即位置部分为 0 而分支非 0）。合法编码最小是 <c>1*10+0 = 10</c>。
    /// ★ <c>v == 0</c> 是「NPC 稍后回复」的合法值，**不能动**。
    ///
    /// <para><b>重算方式</b></para>
    /// 按存档里的顺序重放一遍：从位置 1 起，用 <see cref="PhoneMsgTable.NextReplyPos"/>
    /// 逐个求下一句位置，与原分支号重新组合。这样既保住玩家的**选择历史**
    /// （分支号不变 ⇒ 领奖好感度、对话走向都不变），又让位置变成客户端查得到的值。
    /// </summary>
    internal void MigrateLegacyReplyIds(ClientSession session, Player player)
    {
        var state = LoadState(player);
        int fixedTopics = 0, fixedItems = 0;

        foreach (var letter in state.Letters.Values)
        {
            foreach (var topic in letter.Topics.Values)
            {
                var cfg = _table.Letter(topic.TopicId);
                if (cfg is null || !PhoneDialog.TryRepairLegacy(cfg, topic, out var rebuilt))
                    continue;
                fixedItems += rebuilt.Count;
                topic.MsgIds.Clear();
                topic.MsgIds.AddRange(rebuilt);
                fixedTopics++;
            }
        }

        if (fixedTopics == 0)
            return;

        SaveState(player, state);
        Log.Info($"[session {session.Id}] 私信存量修复：{fixedTopics} 个话题、{fixedItems} 条回复" +
                 "的历史 replyId 已按玩家回复路径重算为「位置*10+分支」" +
                 "—— 修复前这些话题在客户端永远无法推进");
    }


    /// <summary>
    /// sFunction=="PhoneMsg" 的上行分发。param 就是 tbParam 本体
    /// （这一族没有 sCmd/tbParam 外壳，见 <c>LuaCallRouter.ScriptCallHandler</c> 注释）。
    /// </summary>
    private Task OnPhoneMsg(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is Player player)
        {
            lock (player) return HandlePhoneMsgLocked(session, function, param);
        }
        return Task.CompletedTask;
    }

    void IGamePulseFeature.OnPlayerPulse(ClientSession session, Player player)
    {
        lock (player)
        {
            var initial = LoadState(player);
            var beforeBbs = initial.Bbs.Keys.ToHashSet();
            var before = initial.Letters.Values
                .SelectMany(letter => letter.Topics.Keys).ToHashSet();
            if (!BackfillTriggers(session, player)) return;
            var current = LoadState(player);
            // The existing nCmd=3 path rebuilds Lua UI data and refreshes unread
            // indicators. Send only newly delivered topics, then restore the native
            // cache from the authoritative snapshot (AddLetterReplyInfo mutates it).
            foreach (var letter in current.Letters.Values.OrderBy(l => l.NpcId))
                foreach (int topicId in letter.Topics.Keys.Where(id => !before.Contains(id)).Order())
                    Reply(session, CmdLetter, new JsonObject
                    {
                        ["nNpcId"] = letter.NpcId,
                        ["nMsgId"] = topicId,
                        ["nSelectId"] = 0,
                    });
            foreach (var post in current.Bbs.Values.Where(b => !beforeBbs.Contains(b.TopicId)).OrderBy(b => b.TopicId))
            {
                var config = _table.Bbs(post.TopicId)!;
                if (config.NpcId == 0)
                    Reply(session, CmdBbsNewApply, new JsonObject { ["nMsgId"] = post.TopicId });
                else
                    Reply(session, CmdBbs, new JsonObject
                    {
                        ["nNpcId"] = config.NpcId,
                        ["nMsgId"] = post.TopicId,
                        ["nSelectId"] = 0,
                    });
            }
            PushAll(session, player);
        }
    }

    private Task HandlePhoneMsgLocked(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] PhoneMsg 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        // ★ 真机上行可能是数组形态（本项目踩过 JsonNode["key"] 对 JsonArray 抛）
        var obj = param as JsonObject;
        int nCmd = ReadInt(obj?["nCmd"], -1);

        switch (nCmd)
        {
            case CmdBbsApplyList:
                return HandleBbsApplyList(session, player);
            case CmdLetter:
                return HandleLetterReply(session, player, obj);
            case CmdBbs:
                return HandleBbsReply(session, player, obj);
            case CmdLetterTitle:
                return HandleLetterTitle(session, player, obj);
            case CmdLetterAward:
                return HandleLetterAward(session, player, obj);
            case CmdLetterDel:
                return HandleLetterDelete(session, player, obj);
            case CmdLetterGet:
                return HandleLetterGet(session, player, obj);
            case CmdLetterEvent:
                return HandleLetterEvent(session, player, obj);
            case CmdBbsEvent:
                return HandleBbsEvent(session, player, obj);

            default:
                Log.Warn($"[session {session.Id}] PhoneMsg 未知子命令 nCmd={nCmd}" +
                         $"({SubCmdName(nCmd)}) param={param?.ToJsonString()}");
                // ★ 多数发起点挂遮罩 ⇒ 认不出来也要回包
                Fail(session, nCmd, ErrGeneric);
                return Task.CompletedTask;
        }
    }

    // =================================================================
    //  pf 1035：全量下发（整域前置）
    // =================================================================

    /// <summary>
    /// 推 pf 1035 <c>PhoneMsgNtf</c> 全量私信 + 朋友圈。
    ///
    /// ★★ **全量覆盖语义**：客户端 <c>PhoneMsgData</c> 先 <c>Clear()</c> 两个字典再重填
    /// ⇒ 空列表也要推（能把上次登录的残留清干净），且每次状态变更都要重推。
    ///
    /// 宠物仍由咖啡馆域管理；角色与武器补给历史随每次全量同步一起发送，避免覆盖为空。
    /// </summary>
    internal void PushAll(ClientSession session, Player player)
    {
        var state = LoadState(player);
        var ntf = new PhoneMsgNtf();

        foreach (var letter in state.Letters.Values.OrderBy(l => l.NpcId))
        {
            var info = new LetterInfo
            {
                Initiator = (uint)letter.NpcId,
                Msgtitle = letter.Title,
                Createtime = (ulong)letter.CreateTime,
                Replypos = (uint)letter.ReplyPos,
            };
            foreach (var topic in letter.Topics.Values.OrderBy(t => t.TopicId))
            {
                var ti = new TopicInfo
                {
                    Topicid = (uint)topic.TopicId,
                    Awardflag = (uint)(state.ClaimedLetters.Contains(topic.TopicId) ? 1 : topic.AwardFlag),
                };
                foreach (int m in topic.MsgIds)
                    ti.Msgid.Add((uint)m);
                foreach (int p in topic.NpcMsgPos)
                    ti.Npcmsgpos.Add((uint)p);
                info.Msginfo.Add(ti);
            }
            ntf.Letterlist.Add(info);
        }

        foreach (var bbs in state.Bbs.Values.OrderBy(b => b.TopicId))
        {
            ntf.Bbsinfolist.Add(new BBSInfo
            {
                Topicid = (uint)bbs.TopicId,
                Createtime = (ulong)bbs.CreateTime,
                Replyinfo = (uint)bbs.ReplyInfo,
            });
        }

        GachaHistory.Fill(ntf, player);
        session.Push(GamePf.PhoneMsgNtf, ntf);
        Log.Info($"[session {session.Id}] pf 1035 下发终端数据：私信 {ntf.Letterlist.Count} 条" +
                 $"（话题 {ntf.Letterlist.Sum(l => l.Msginfo.Count)} 个）、帖子 {ntf.Bbsinfolist.Count} 条");
    }

    // =================================================================
    //  nCmd 3：私信回复
    // =================================================================

    /// <summary>
    /// 私信回复。上行 <c>{nCmd=3, nMsgId=&lt;话题id&gt;, nSelectId=&lt;候选序号&gt;}</c>
    /// （`UI_Phone.lua:786..793`）。
    ///
    /// 下行要 <c>nNpcId/nMsgId/nSelectId</c> 三个（`S2CCall.lua:266..268`）：
    /// 客户端 <c>AddLetterReplyInfo(nNpcId, nMsgId, nSelectId)</c> 自己造数据，
    /// 再 <c>ServerResponseLetter</c> 渲染。
    ///
    /// <para><b>★★★ nSelectId 上行与下行不是同一个东西（第十四轮修复的核心）</b></para>
    /// <list type="bullet">
    ///   <item><b>上行</b>是**候选序号**（1/2/3…）：
    ///     <c>UI_Phone.lua:707 table.insert(self.tbSelectList, i)</c> 存的就是循环下标；</item>
    ///   <item><b>下行/存档</b>要的是 <c>位置*10 + 分支</c> 的复合编码：
    ///     客户端 <c>MakeLetterSingleInfo</c> 用
    ///     <c>GetLetterNpcReplyInfo(npcId, topicId, nTopicPos)</c> **按位置**查
    ///     服务端下发的 <c>MsgId</c> 数组，查不到就 <c>return</c>（丢弃该句）。</item>
    /// </list>
    /// 原实现把上行值**原样**存进 <c>MsgIds</c> ⇒ 序号 1 经 <c>SplitReplyId</c> 得位置 <b>0</b>
    /// ⇒ 永远查不到真实位置（3、5、7…）⇒ 玩家回复与 NPC 后续台词全部被丢弃
    /// ⇒ <b>界面纹丝不动 =「点了没反应」</b>。
    ///
    /// ★★ 这个 bug 骗过了全部四条常规判据：服务端日志「回复成功」、零 unhandled、
    /// 客户端零 Lua 报错、存档也记了一笔。**只有真机点进界面看对话有没有推进才能发现。**
    ///
    /// ★ <c>nSelectId == 0</c> 是客户端的新话题通知路径
    /// （`PhoneMsgLogic.lua:MakeNewLetterReplyInfo`）；兼容响应但不记录玩家选择。
    /// </summary>
    private Task HandleLetterReply(ClientSession session, Player player, JsonObject? param)
    {
        if (!GachaRequestLimits.TryInteger(param?["nMsgId"], out int msgId) || msgId <= 0 ||
            !GachaRequestLimits.TryInteger(param?["nSelectId"], out int rawSelect))
        {
            Fail(session, CmdLetter, ErrGeneric);
            return Task.CompletedTask;
        }

        var topicCfg = _table.Letter(msgId);
        if (topicCfg is null)
        {
            Log.Warn($"[session {session.Id}] PhoneMsg nCmd=3 私信话题 {msgId} 不在 " +
                     $"{PhoneMsgTable.LetterFileName} → nError={ErrGeneric}");
            Fail(session, CmdLetter, ErrGeneric);
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (!state.Letters.TryGetValue(topicCfg.NpcId, out var letter) ||
            !letter.Topics.TryGetValue(msgId, out var topic))
        {
            Fail(session, CmdLetter, ErrGeneric);
            return Task.CompletedTask;
        }

        // Lua MakeNewLetterReplyInfo treats zero as a new-topic notification,
        // not a player choice. Preserve the compatibility response without saving
        // fake progress, changing ordering time, or growing the history on retries.
        if (rawSelect == 0)
        {
            Reply(session, CmdLetter, new JsonObject
            {
                ["nNpcId"] = topicCfg.NpcId,
                ["nMsgId"] = msgId,
                ["nSelectId"] = 0,
            });
            PushAll(session, player);
            return Task.CompletedTask;
        }

        // ★ 把「候选序号」翻译成客户端要的 位置*10+分支
        if (rawSelect > 0)
        {
            var (position, previousBranch) = PhoneDialog.Last(topic);
            int next = PhoneMsgTable.NextReplyPos(topicCfg, position, previousBranch, playerReply: true);
            if (PhoneDialog.Complete(topicCfg, topic) || next == 0 ||
                rawSelect > topicCfg.Lines[next - 1].Info.Count ||
                topicCfg.Lines[next - 1].Info[rawSelect - 1] is "NULL" or "0")
            {
                Fail(session, CmdLetter, ErrGeneric);
                return Task.CompletedTask;
            }
        }
        int replyId = BuildReplyId(topicCfg, topic, rawSelect);

        topic.MsgIds.Add(replyId);
        letter.CreateTime = Now();
        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=3 私信回复 npc={topicCfg.NpcId} " +
                 $"话题={msgId} 候选序号={rawSelect} → replyId={replyId}" +
                 $"（位置 {PhoneMsgTable.ReplyPosOf(replyId)} 分支 {PhoneMsgTable.ReplyBranchOf(replyId)}；" +
                 $"该话题已选 {topic.MsgIds.Count} 次）");

        // ★ 先推全量（客户端 ServerResponseLetter 会重读本地缓存），再回包
        PushAll(session, player);
        Reply(session, CmdLetter, new JsonObject
        {
            ["nNpcId"] = topicCfg.NpcId,
            ["nMsgId"] = msgId,
            // ★ 回包也要给编码后的值：客户端 AddLetterReplyInfo 拿它写 C# 缓存，
            //   随后 MakeNewLetterReplyInfo 用 SplitReplyId(nSelectId) 取位置。
            ["nSelectId"] = replyId,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 由「当前进度 + 玩家选的候选序号」算出客户端要的 <c>replyId</c>（<c>位置*10+分支</c>）。
    ///
    /// <para><b>算法</b></para>
    /// <list type="number">
    ///   <item>取当前已推进到的位置 <c>curPos</c>（存档里最后一个 replyId 的位置部分；
    ///     没有记录时为 <b>1</b> —— 对话总是从第 1 句开始）；</item>
    ///   <item>用移植的 <see cref="PhoneMsgTable.NextReplyPos"/> 求下一句位置；</item>
    ///   <item>与候选序号组合成 <c>位置*10 + 分支</c>。</item>
    /// </list>
    ///
    /// <para><b>★ 边界</b></para>
    /// <list type="bullet">
    ///   <item><c>rawSelect == 0</c>（NPC 稍后回复）⇒ 直接回 0，客户端有专门分支；</item>
    ///   <item>算不出下一句（对话已结束/配置缺失）⇒ 退化成把序号当分支、
    ///     位置沿用当前位置，至少不会写出「位置 0」这种必然失效的值。</item>
    /// </list>
    /// </summary>
    internal static int BuildReplyId(LetterTopic topicCfg, TopicState topic, int rawSelect)
    {
        // nSelectId == 0 是「NPC 稍后回复」，客户端 ServerResponseLetter 有独立分支
        if (rawSelect <= 0)
            return 0;

        int curPos = 1;
        for (int i = topic.MsgIds.Count - 1; i >= 0; i--)
        {
            int pos = PhoneMsgTable.ReplyPosOf(topic.MsgIds[i]);
            if (pos > 0) { curPos = pos; break; }
        }

        // ★ 候选序号即分支号：客户端 UI_Phone.lua:707 的 tbSelectList 存的是
        //   「第 i 个可选项」，而 InfoType=2 行的 InfoNum 恰好就是 [1,2,…]
        //   （实测 358 个选择行全是这个形状）⇒ 两者天然对齐，无需再映射。
        int branch = rawSelect;

        int previousBranch = PhoneDialog.Last(topic).Branch;
        int nextPos = PhoneMsgTable.NextReplyPos(topicCfg, curPos, previousBranch, playerReply: true);
        if (nextPos <= 0)
            nextPos = curPos;   // 退化：至少保证位置非 0

        return PhoneMsgTable.EncodeReplyId(nextPos, branch);
    }

    // =================================================================
    //  nCmd 1：论坛回帖 / 发新帖
    // =================================================================

    /// <summary>
    /// 论坛回帖或发新帖。上行 <c>{nCmd=1, nMsgId, nSelectId}</c>
    /// （回帖 `UI_Phone.lua:796..803`；发新帖 `:1795..1803`，其 <c>nSelectId = 0</c>）。
    ///
    /// 下行要 <c>nNpcId/nMsgId/nSelectId</c>（`S2CCall.lua:254..255`）。
    /// ★ <c>nNpcId == 0</c> 时客户端会把该帖从「可发表列表」里移除
    /// （`PhoneMsgLogic.lua:1781..1789`）—— 服务端同步移除，两边保持一致。
    /// </summary>
    private Task HandleBbsReply(ClientSession session, Player player, JsonObject? param)
    {
        if (!GachaRequestLimits.TryInteger(param?["nMsgId"], out int msgId) || msgId <= 0 ||
            !GachaRequestLimits.TryInteger(param?["nSelectId"], out int selectId))
        {
            Fail(session, CmdBbs, ErrGeneric);
            return Task.CompletedTask;
        }

        var cfg = _table.Bbs(msgId);
        if (cfg is null)
        {
            Log.Warn($"[session {session.Id}] PhoneMsg nCmd=1 帖子 {msgId} 不在 " +
                     $"{PhoneMsgTable.BbsFileName} → nError={ErrGeneric}");
            Fail(session, CmdBbs, ErrGeneric);
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if ((selectId > 0 && (!state.Bbs.ContainsKey(msgId) || state.BbsApply.Contains(msgId))) ||
            (selectId == 0 && (cfg.NpcId != 0 || !state.BbsApply.Contains(msgId))) ||
            (selectId > 0 && (selectId > cfg.PlayerOptions.Count || cfg.PlayerOptions[selectId - 1] is "NULL" or "0")) ||
            (state.Bbs.TryGetValue(msgId, out var existing) && existing.ReplyInfo > 0))
        {
            Fail(session, CmdBbs, ErrGeneric);
            return Task.CompletedTask;
        }
        bool isNew = !state.Bbs.ContainsKey(msgId);
        var bbs = EnsureBbs(state, msgId);
        if (selectId > 0)
            bbs.ReplyInfo = selectId;

        // 玩家自己发的帖（NpcId == 0）发出后就不在「可发表」列表里了
        if (cfg.NpcId == 0)
            state.BbsApply.Remove(msgId);

        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=1 " +
                 $"{(isNew ? "发新帖" : "回帖")} 帖子={msgId} npc={cfg.NpcId} 选项={selectId}" +
                 (cfg.NpcId == 0 ? "（玩家帖，已从可发表列表移除）" : ""));

        PushAll(session, player);

        var reply = new JsonObject
        {
            ["nNpcId"] = cfg.NpcId,
            ["nMsgId"] = msgId,
            ["nSelectId"] = selectId,
        };
        // 回帖给好感度：AwardInfo[nSelectId]（与私信领奖同一套，见 HandleLetterAward）
        AttachGirlExp(session, player, reply, cfg.NpcId, cfg.AwardExp, selectId);
        Reply(session, CmdBbs, reply);
        return Task.CompletedTask;
    }

    // =================================================================
    //  nCmd 4：改私信标题
    // =================================================================

    /// <summary>
    /// 改标题。上行 <c>{nCmd=4, nNpcId, strTitle}</c>
    /// （`UI_Phone.lua:866..874` / `:888..896`、`UI_CafeTalkPhone.lua:318..324` / `:339..345`）。
    ///
    /// ★ <c>strTitle == "ClearLetterTitle"</c> 是**约定的清空指令**
    /// （`UI_Phone.lua:892` 注释「固定词汇 配合服务器修改」）⇒ 服务端要把标题置空，
    /// 而下行 <c>strTitle</c> 回**空串**（客户端 <c>ServerResponseLetterTitle</c>
    /// 判 <c>strTitle == ""</c> 才把 <c>tbTitle.Title</c> 置 nil，`PhoneMsgLogic.lua:1345`）。
    /// </summary>
    private Task HandleLetterTitle(ClientSession session, Player player, JsonObject? param)
    {
        int npcId = ReadInt(param?["nNpcId"], 0);
        string title = param?["strTitle"]?.ToString() ?? "";

        if (npcId <= 0)
        {
            Fail(session, CmdLetterTitle, ErrGeneric);
            return Task.CompletedTask;
        }

        // 客户端也拦（UI_Phone.lua:862），服务端兜一层防改包
        if (npcId == FixedTitleNpcId)
        {
            Log.Warn($"[session {session.Id}] PhoneMsg nCmd=4 npc={npcId} 不允许改标题 → 拒绝");
            Fail(session, CmdLetterTitle, ErrGeneric);
            return Task.CompletedTask;
        }

        bool clear = title == ClearTitleToken;
        var state = LoadState(player);
        var letter = EnsureLetter(state, npcId);
        letter.Title = clear ? "" : title;
        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=4 npc={npcId} " +
                 (clear ? "清空标题" : $"改标题为「{title}」"));

        PushAll(session, player);
        Reply(session, CmdLetterTitle, new JsonObject
        {
            ["nNpcId"] = npcId,
            // ★ 清空时回空串（客户端据此置 nil）
            ["strTitle"] = letter.Title,
        });
        return Task.CompletedTask;
    }

    // =================================================================
    //  nCmd 7：私信领奖
    // =================================================================

    /// <summary>
    /// 私信领奖。上行 <c>{nCmd=7, nMsgId}</c>（`PhoneMsgLogic.lua:1261..1272`）。
    ///
    /// ★★★ 奖励是**少女好感度经验**，不是物品：
    /// <c>LetterInfoNew.AwardInfo</c> 是逗号分隔的经验值（如 <c>25,20</c>），
    /// 客户端 <c>UI_Phone.lua:1006</c> 拿 <c>AwardInfo[nSelectId]</c> 直接显示
    /// 「好感度 +N」，而下行的 <c>tbExp</c> 被
    /// <c>GirlLogic:OnGirlAddExp(nNpcId, unpack(tbParam.tbExp))</c> 消费（`S2CCall.lua:283`）。
    ///
    /// ⇒ 本处复用 <see cref="GirlLoveFeature"/> 的好感度曲线（见 <see cref="AttachGirlExp"/>）。
    ///
    /// ★ 客户端发这条前会自查 <c>GetLetterAwardFlag(nNpcId, nMsgId) &gt; 0 then return</c>
    /// （`PhoneMsgLogic.lua:1262`）⇒ 已领过压根不会发；服务端仍然要拦（防改包 + 数据不一致）。
    /// </summary>
    private Task HandleLetterAward(ClientSession session, Player player, JsonObject? param)
    {
        int msgId = StrictPositiveId(param?["nMsgId"]);
        var cfg = _table.Letter(msgId);
        if (cfg is null)
        {
            Log.Warn($"[session {session.Id}] PhoneMsg nCmd=7 话题 {msgId} 不存在 → nError={ErrGeneric}");
            Fail(session, CmdLetterAward, ErrGeneric);
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (!state.Letters.TryGetValue(cfg.NpcId, out var letter) ||
            !letter.Topics.TryGetValue(msgId, out var topic))
        {
            Fail(session, CmdLetterAward, ErrGeneric);
            return Task.CompletedTask;
        }

        if (topic.AwardFlag > 0 || state.ClaimedLetters.Contains(msgId))
        {
            Log.Warn($"[session {session.Id}] PhoneMsg nCmd=7 话题 {msgId} 已领过奖" +
                     $"（awardflag={topic.AwardFlag}）→ nError={ErrGeneric}");
            Fail(session, CmdLetterAward, ErrGeneric);
            return Task.CompletedTask;
        }

        if (!PhoneDialog.Complete(cfg, topic))
        {
            Fail(session, CmdLetterAward, ErrGeneric);
            return Task.CompletedTask;
        }

        topic.AwardFlag = 1;
        state.ClaimedLetters.Add(msgId);
        SaveState(player, state);

        // 选项：客户端按最后一次选择结算（AwardInfo 下标是 nSelectId，1-based）
        // ★★ MsgIds 里存的是 `位置*10+分支` 的复合编码（第十四轮改），
        //    取好感度经验要用**分支**部分，不能直接拿整个 replyId 当下标 ——
        //    否则 31 会去查 AwardInfo[31]（越界 ⇒ 恒 0 好感度）。
        int selectId = PhoneDialog.AwardSelection(topic);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=7 私信领奖 npc={cfg.NpcId} 话题={msgId} " +
                 $"选项={selectId} 好感度经验={PhoneMsgTable.AwardExpAt(cfg.AwardExp, selectId)}");

        PushAll(session, player);

        var reply = new JsonObject
        {
            ["nNpcId"] = cfg.NpcId,
            ["nMsgId"] = msgId,
        };
        AttachGirlExp(session, player, reply, cfg.NpcId, cfg.AwardExp, selectId);
        Reply(session, CmdLetterAward, reply);
        return Task.CompletedTask;
    }

    // =================================================================
    //  nCmd 10 / 11：删除 / 获取私信
    // =================================================================

    /// <summary>
    /// 删除私信话题。上行 <c>{nCmd=10, nMsgId}</c>（`PhoneMsgLogic.lua:1385..1394`）。
    /// 下行要 <c>nNpcId/nMsgId</c>（`S2CCall.lua:319..320`
    /// → <c>RemoveLetterMsg</c> + <c>ServerResponseDeleteLetter</c>）。
    /// </summary>
    private Task HandleLetterDelete(ClientSession session, Player player, JsonObject? param)
    {
        int msgId = StrictPositiveId(param?["nMsgId"]);
        var cfg = _table.Letter(msgId);
        if (cfg is null)
        {
            Fail(session, CmdLetterDel, ErrGeneric);
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        RememberLetterHistory(state);
        bool removed = false;
        if (state.Letters.TryGetValue(cfg.NpcId, out var letter))
        {
            if (letter.Topics.TryGetValue(msgId, out var deletedTopic))
                state.ArchivedTopics[msgId] = TopicState.FromJson(deletedTopic.ToJson());
            removed = letter.Topics.Remove(msgId);
            // 该 NPC 一条话题都不剩 ⇒ 整条对话也删掉（客户端列表按 NPC 分组）
            if (letter.Topics.Count == 0)
                state.Letters.Remove(cfg.NpcId);
        }
        if (removed) state.DeletedLetters.Add(msgId);
        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=10 删除私信 npc={cfg.NpcId} 话题={msgId}" +
                 (removed ? "" : "（本来就不存在）"));

        PushAll(session, player);
        Reply(session, CmdLetterDel, new JsonObject
        {
            ["nNpcId"] = cfg.NpcId,
            ["nMsgId"] = msgId,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取（新增）一条私信话题。上行 <c>{nCmd=11, nMsgId}</c>（`PhoneMsgLogic.lua:1440..1449`）。
    ///
    /// ★ <c>S2C.ServerPhoneMsg</c> **没有 nCmd 11 的分支**（`S2CCall.lua:252..322` 只到 10）
    /// ⇒ 回包的 <c>nCmd</c> 用 **3**（私信）让客户端走
    /// <c>AddLetterReplyInfo</c> + <c>ServerResponseLetter</c> 把新话题渲染出来
    /// —— 与「服务端主动塞一条新私信」等价。
    /// </summary>
    private Task HandleLetterGet(ClientSession session, Player player, JsonObject? param)
    {
        int msgId = StrictPositiveId(param?["nMsgId"]);
        var cfg = _table.Letter(msgId);
        if (cfg is null)
        {
            Fail(session, CmdLetterGet, ErrGeneric);
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (!CanReceiveLetter(player, state, cfg))
        {
            Fail(session, CmdLetterGet, ErrGeneric);
            return Task.CompletedTask;
        }
        state.DeletedLetters.Remove(msgId);
        var letter = EnsureLetter(state, cfg.NpcId);
        bool isNew = !letter.Topics.ContainsKey(msgId);
        if (isNew && state.ArchivedTopics.Remove(msgId, out var archived))
            letter.Topics[msgId] = archived;
        else
            EnsureTopic(letter, msgId);
        if (isNew) letter.CreateTime = Now();
        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=11 获取私信 npc={cfg.NpcId} 话题={msgId}" +
                 (isNew ? "（新增）" : "（已存在）"));

        PushAll(session, player);
        // ★ 用 nCmd=3 回（客户端没有 11 的分支），nSelectId=0 = 「NPC 稍后回复」
        Reply(session, CmdLetter, new JsonObject
        {
            ["nNpcId"] = cfg.NpcId,
            ["nMsgId"] = msgId,
            ["nSelectId"] = 0,
        });
        // The delta callback may mutate native cache through AddLetterReplyInfo.
        // History must finish with the exact saved branch/award snapshot.
        if (letter.Topics[msgId].MsgIds.Count > 0 || state.ClaimedLetters.Contains(msgId))
            PushAll(session, player);
        return Task.CompletedTask;
    }

    // =================================================================
    //  ★★★ 登录补发：非剧情类触发（第十二轮新增）
    // =================================================================

    /// <summary>
    /// 登录时按 <c>LetterEvent.txt</c> / <c>BBSEvent.txt</c> 补发**已满足条件**的私信与帖子。
    ///
    /// <para><b>★★★ 为什么必须由服务端做（这是用户报的「没办法回复」的深层根因）</b></para>
    /// 客户端 <c>PhoneMsgLogic:CheckEvent</c>（<c>PhoneMsgLogic.lua:1928..1960</c>）
    /// **只实现了 <c>eventType == 1</c>（剧情）一个分支**，其余 8 种类型
    /// （2 私信完成 / 3 好感度等级 / 4 时间 / 5 少女信息 / 6 角色卡 / 7 成就 /
    /// 8 通过关卡 / 9 好感度总和）走到函数末尾直接返回 nil；
    /// 而 <c>CheckLetterForServer</c>/<c>CheckBBSForServer</c> 的**唯一**调用点是
    /// <c>CartoonData.lua:81..82</c>（播剧情 CG 时），且只传 <c>eventType = 1</c>。
    /// ⇒ 那 8 类触发**原本是原厂服务端的职责**，客户端永远不会替我们发上行。
    ///
    /// 于是在 GM 全解锁账号上（跳过正常推关、不播剧情 CG）终端几乎是空的：
    /// 私信只有验收时手工造的 2 条、帖子 1 个，且都已回到对话尽头
    /// ⇒ 玩家点「回复」时被客户端前置检查拦掉（<c>UI_Phone.lua:969</c> 的
    /// <c>not tbList</c> 分支），表现为「点了没反应」。
    ///
    /// <para><b>实现口径</b></para>
    /// <list type="bullet">
    ///   <item><b>只增不减、幂等</b>：已存在的话题/帖子跳过，所以重复登录不会刷新内容；</item>
    ///   <item><b>类型 1（剧情）不在这里补</b> —— 那条链客户端是正常工作的，
    ///     由 <c>CartoonData</c> 在真的看完剧情时触发，抢跑会剧透；</item>
    ///   <item><b>多条件按 AND</b>；含**任何**无法判定的条件（如类型 1）则整条跳过；</item>
    ///   <item><b>孤儿配置优雅跳过</b>：<c>LetterEvent</c> 318 条里只有 184 条能在
    ///     <c>LetterInfoNew</c> 找到话题（其余指向旧表/已下线内容）。</item>
    /// </list>
    /// </summary>
    internal bool BackfillTriggers(ClientSession session, Player player)
    {
        var state = LoadState(player);
        int addedLetters = 0, addedBbs = 0;
        var reasons = new List<string>();

        // ---- 私信 ----
        foreach (var ev in _table.AllLetterEvents)
        {
            var cfg = _table.Letter(ev.Id);
            if (state.DeletedLetters.Contains(ev.Id)) continue;
            if (cfg is null)
                continue;                       // 孤儿配置（指向旧表 LetterInfo.txt）

            var letter = state.Letters.TryGetValue(cfg.NpcId, out var l) ? l : null;
            if (letter is not null && letter.Topics.ContainsKey(ev.Id))
                continue;                       // 幂等：已有该话题

            if (!Satisfied(player, state, ev, cfg.NpcId, out string why))
                continue;

            var target = EnsureLetter(state, cfg.NpcId);
            EnsureTopic(target, ev.Id);
            if (target.CreateTime == 0)
                target.CreateTime = Now();
            addedLetters++;
            if (reasons.Count < 8)
                reasons.Add($"私信{ev.Id}(npc{cfg.NpcId}:{why})");
        }

        // ---- 论坛帖子 ----
        foreach (var ev in _table.AllBbsEvents)
        {
            var cfg = _table.Bbs(ev.Id);
            if (cfg is null)
                continue;
            if (state.Bbs.ContainsKey(ev.Id))
                continue;

            if (!Satisfied(player, state, ev, cfg.NpcId, out string why, forum: true))
                continue;

            EnsureBbs(state, ev.Id);
            // ★ NpcId == 0 的帖子是「玩家可发表」的 ⇒ 同时进可发表列表（同 nCmd 13 的语义）
            if (cfg.NpcId == 0 && !state.BbsApply.Contains(ev.Id))
                state.BbsApply.Add(ev.Id);
            addedBbs++;
            if (reasons.Count < 8)
                reasons.Add($"帖子{ev.Id}(npc{cfg.NpcId}:{why})");
        }

        if (addedLetters == 0 && addedBbs == 0)
            return false;

        SaveState(player, state);
        Log.Info($"[session {session.Id}] PhoneMsg 补发触发内容：私信 +{addedLetters} 条、" +
                 $"帖子 +{addedBbs} 个（客户端只判剧情类，其余 8 类是服务端职责）" +
                 $"；样例 {string.Join(" ", reasons)}");
        return true;
    }

    /// <summary>
    /// 判定一条触发是否已满足（多条件 AND）。<paramref name="why"/> 回填命中原因（写日志用）。
    ///
    /// ★ 含无法判定的条件（缺失剧情路径、或未知类型）时返回 false —— **保守不补发**。
    /// </summary>
    private bool Satisfied(Player player, PhoneState state, PhoneEvent ev,
                           int ownerNpcId, out string why, bool forum = false)
    {
        why = "";
        if (ev.Conds.Count == 0)
            return false;

        var hits = new List<string>();
        foreach (var c in ev.Conds)
        {
            if (forum && c.Type == 2)
            {
                int need = LastInt(c.Info);
                var config = _table.Bbs(need);
                if (config is null || !state.Bbs.TryGetValue(need, out var post) || state.BbsApply.Contains(need) ||
                    (config.ReplyIdx > 0 && (post.ReplyInfo <= 0 || post.ReplyInfo > config.PlayerOptions.Count ||
                        config.PlayerOptions[post.ReplyInfo - 1] is "0" or "NULL")))
                    return false;
                hits.Add($"论坛{need}已完成");
                continue;
            }
            if (!SatisfiedOne(player, state, c, ownerNpcId, out string one))
                return false;
            hits.Add(one);
        }
        why = string.Join("+", hits);
        return true;
    }

    /// <summary>
    /// 判定单个条件。编码取证 <c>PhoneMsgLogic:SplitLimitInfo</c>（<c>:499..545</c>）。
    /// </summary>
    /// <param name="ownerNpcId">
    /// 该条私信/帖子归属的 NPC（<c>LetterInfoNew.NpcId</c> / <c>BBSInfo.NpcId</c>）。
    /// ★★★ 类型 3「好感度等级」判的是**这个 NPC 自己**的等级，见该分支注释。
    /// </param>
    private bool SatisfiedOne(Player player, PhoneState state, PhoneEventCond c,
                              int ownerNpcId, out string why)
    {
        why = "";
        switch (c.Type)
        {
            // Resolve the exact original story bit; unrelated read stories must not
            // unlock this event. Unknown paths stay locked rather than guessing.
            case 1:
            {
                if (!_cartoons.IsRead(player, c.Info)) return false;
                why = "剧情已读";
                return true;
            }

            // 2 私信完成：配置路径已走到结束，不以“曾回复过一次”代替。
            case 2:
            {
                int need = LastInt(c.Info);
                if (need <= 0)
                    return false;
                if (state.CompletedLetters.Contains(need) || state.ClaimedLetters.Contains(need))
                {
                    why = $"私信{need}历史已完成";
                    return true;
                }
                foreach (var l in state.Letters.Values)
                {
                    if (l.Topics.TryGetValue(need, out var t) && _table.Letter(need) is { } config &&
                        PhoneDialog.Complete(config, t))
                    {
                        why = $"私信{need}已回";
                        return true;
                    }
                }
                return false;
            }

            // 3 好感度等级：★★★ 判的是**该私信/帖子归属 NPC 自己**的好感等级，
            //   不是「任一少女」。
            //   取证（本地表统计，两张表口径一致）：阈值严格按 NpcId 成组递增 ——
            //     LetterEvent: npc1 → 15/50/60、npc5 → 25/45/55/60、npc14 → 11/14/47/52
            //     BBSEvent:    npc6 → 13/23、npc8 → 13/33、npc11 → 3/3
            //   若按「任一少女达到」判，好感最高的那个少女会把**所有** NPC 的
            //   门槛一次性顶开 ⇒ 一登录就把几十条尚未解锁的私信全塞出来（剧透且乱序）。
            case 3:
            {
                int need = LastInt(c.Info);
                if (need <= 0 || ownerNpcId <= 0)
                    return false;
                var girl = player.GetGirl((uint)ownerNpcId);
                if (girl is null || girl.Level < (uint)need)
                    return false;
                why = $"少女{ownerNpcId}好感Lv{girl.Level}≥{need}";
                return true;
            }


            // 4 时间窗：yyyyMMddHH-yyyyMMddHH，按**本地时间**判（客户端口径，见 HANDOFF 时区条）
            case 4:
            {
                if (!PhoneEventWindow.Contains(c.Info, DateTime.Now)) return false;
                why = $"档期{c.Info}内";
                return true;
            }

            // Secret TypeDec keys identify both the girl and the information row.
            // Owning an unrelated girl parsed from the last number is insufficient.
            case 5:
            {
                var key = c.Info.Trim().Split('.');
                if (key.Length != 3 || key[2] != "TypeDec" ||
                    !uint.TryParse(key[0], out uint girlId) || girlId == 0 ||
                    !int.TryParse(key[1], out int secretId) || secretId <= 0 ||
                    player.GetGirl(girlId) is null) return false;
                var secret = _girlLove.SecretsOf(girlId).FirstOrDefault(s => s.Index == secretId);
                if (secret is null) return false;
                bool gated = secret.Level > 0 || secret.Gift.Length >= 4;
                if (gated && GirlLoveFeature.GetVar(player, girlId, 100 + secretId - 1) == 0)
                    return false;
                why = $"少女{girlId}信息{secretId}已解锁";
                return true;
            }

            // 6 角色卡 G-D-P-L：背包里有这张卡
            case 6:
            {
                var p = ParseDashInts(c.Info);
                if (p.Length < 4)
                    return false;
                bool has = player.Items.Any(i =>
                    i.Count > 0 && i.Genre == (uint)p[0] && i.Detail == (uint)p[1] &&
                    i.Particular == (uint)p[2] && i.Level == (uint)p[3]);
                if (!has)
                    return false;
                why = $"已有卡{p[0]}-{p[1]}-{p[2]}-{p[3]}";
                return true;
            }

            // 7 完成成就：成就进度存任务值 group 18 的 bit1..31（见 ActivityAchieveFeature）
            case 7:
            {
                int need = LastInt(c.Info);
                if (need <= 0)
                    return false;
                var entry = _ctx.Missions.GetAchieveOther(need);
                if (entry is null) return false;
                int required = entry.RequiredCount(achieveKey: true);
                int progress = Math.Max(MissionFeature.ProgressOf(player.GetTask(entry.Group, need)),
                    MissionFeature.DerivedProgress(player, entry));
                if (required <= 0 || progress < required) return false;
                why = $"成就{need}进度{progress}≥{required}";
                return true;
            }

            // 8 通过关卡 章-关-难度：查 Chapter.txt 换成 mapId 再看星级位域
            case 8:
            {
                var p = ParseDashInts(c.Info);
                if (p.Length < 3)
                    return false;
                var lv = _chapters.Get(p[0], p[1], p[2]);
                if (lv is null)
                    return false;
                if (player.GetLevelStar(lv.MapId) <= 0)
                    return false;
                why = $"已过{p[0]}-{p[1]}(难{p[2]})";
                return true;
            }

            // 9 好感度等级总和
            case 9:
            {
                int need = LastInt(c.Info);
                if (need <= 0)
                    return false;
                long sum = player.Girls.Sum(g => (long)g.Level);
                if (sum < need)
                    return false;
                why = $"好感总和{sum}≥{need}";
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 取字符串里**最后一个**整数 —— 精确复刻客户端
    /// <c>string.gsub(strInfo, "(%d+)", function(g) tbRet[1] = tonumber(g) end)</c>
    /// （<c>PhoneMsgLogic.lua:509..527</c>）：<c>gsub</c> 对**每个**数字都回调一次、
    /// 每次都覆写 <c>tbRet[1]</c> ⇒ **最后一次赋值胜出**。
    ///
    /// ★★ 现网只有一处会暴露差异，但必须对齐：<c>LetterEvent</c> 的
    /// <c>ev=219 type=5 LimitInfo="16.12.TypeDec"</c> ⇒ 客户端得 **12**（不是 16）。
    /// 早先本函数取「第一个整数」，那条会判成少女 16 而非 12。
    /// </summary>
    private static int LastInt(string s)
    {
        int result = 0;
        int i = 0;
        while (i < s.Length)
        {
            if (!char.IsAsciiDigit(s[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i]))
                i++;
            if (int.TryParse(s[start..i], out int v))
                result = v;
        }
        return result;
    }

    /// <summary>解析 <c>[2-1-1]</c> / <c>1-11-1-3</c> 这类**短横**分隔的整数组
    /// （★ 与逗号分隔是两个函数，见 AGENTS.md §5 那条「同表两列分隔符可不同」）。</summary>
    private static int[] ParseDashInts(string s)
    {
        var body = s.Trim().Trim('[', ']');
        if (body.Length == 0)
            return Array.Empty<int>();
        var parts = body.Split('-');
        var list = new List<int>(parts.Length);
        foreach (var p in parts)
        {
            if (int.TryParse(p.Trim(), out int v))
                list.Add(v);
        }
        return list.ToArray();
    }

    // =================================================================
    //  nCmd 12 / 13：剧情触发
    // =================================================================

    /// <summary>
    /// 剧情触发私信。上行 <c>{nCmd=12, nMsgId}</c>（`PhoneMsgLogic.lua:1967..1978`）。
    ///
    /// ★★ **触发条件由客户端自己判**：<c>CheckLetterForServer</c> 遍历
    /// <c>LetterEvent.txt</c>、自己跑 <c>CheckEvent</c>，命中后只把 <c>nMsgId</c> 报上来
    /// （唯一调用者是 `CartoonData.lua:81`，即「看完某段剧情」）。
    /// ⇒ 服务端只做**校验 + 落库 + 下发**，不需要复刻条件判定。
    ///
    /// ★ 幂等：同一段剧情可能被重复触发（`CartoonData` 每次播放都会调），
    /// 已存在的话题**不重复添加**，也不回包（避免重复弹「新私信」气泡）。
    /// </summary>
    private Task HandleLetterEvent(ClientSession session, Player player, JsonObject? param)
    {
        int msgId = StrictPositiveId(param?["nMsgId"]);
        var cfg = _table.Letter(msgId);
        if (cfg is null)
        {
            // ★ LetterEvent 的 308 个 id 里有 134 个在 LetterInfoNew 找不到对应话题
            //   （孤儿配置，指向旧表或已下线内容）⇒ 优雅跳过，不当错误
            Log.Info($"[session {session.Id}] PhoneMsg nCmd=12 触发 id={msgId} 在 " +
                     $"{PhoneMsgTable.LetterFileName} 里没有对应话题（孤儿配置），跳过");
            Reply(session, CmdLetterEvent, new JsonObject());
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (state.DeletedLetters.Contains(msgId))
        {
            Reply(session, CmdLetterEvent, new JsonObject());
            return Task.CompletedTask;
        }
        if (!CanReceiveLetter(player, state, cfg))
        {
            Fail(session, CmdLetterEvent, ErrGeneric);
            return Task.CompletedTask;
        }
        var letter = EnsureLetter(state, cfg.NpcId);
        if (letter.Topics.ContainsKey(msgId))
        {
            Log.Info($"[session {session.Id}] PhoneMsg nCmd=12 私信 {msgId} 已存在，幂等跳过");
            Reply(session, CmdLetterEvent, new JsonObject());
            return Task.CompletedTask;
        }

        EnsureTopic(letter, msgId);
        letter.CreateTime = Now();
        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=12 剧情触发私信 npc={cfg.NpcId} 话题={msgId}");

        PushAll(session, player);
        // 用 nCmd=3 让客户端把新私信渲染出来（同 nCmd 11 的理由）
        Reply(session, CmdLetter, new JsonObject
        {
            ["nNpcId"] = cfg.NpcId,
            ["nMsgId"] = msgId,
            ["nSelectId"] = 0,
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 剧情触发论坛帖子。上行 <c>{nCmd=13, nMsgId}</c>（`PhoneMsgLogic.lua:1987..1998`）。
    /// 语义与 <see cref="HandleLetterEvent"/> 同构。
    /// </summary>
    private Task HandleBbsEvent(ClientSession session, Player player, JsonObject? param)
    {
        int msgId = StrictPositiveId(param?["nMsgId"]);
        var cfg = _table.Bbs(msgId);
        if (cfg is null)
        {
            Log.Info($"[session {session.Id}] PhoneMsg nCmd=13 触发 id={msgId} 在 " +
                     $"{PhoneMsgTable.BbsFileName} 里没有对应帖子，跳过");
            Reply(session, CmdBbsEvent, new JsonObject());
            return Task.CompletedTask;
        }

        var state = LoadState(player);
        if (state.Bbs.ContainsKey(msgId))
        {
            Log.Info($"[session {session.Id}] PhoneMsg nCmd=13 帖子 {msgId} 已存在，幂等跳过");
            Reply(session, CmdBbsEvent, new JsonObject());
            return Task.CompletedTask;
        }

        if (_table.BbsEvent(msgId) is not { } ev ||
            !Satisfied(player, state, ev, cfg.NpcId, out _, forum: true))
        {
            Fail(session, CmdBbsEvent, ErrGeneric);
            return Task.CompletedTask;
        }
        EnsureBbs(state, msgId);

        // ★ NpcId == 0 的帖子是「玩家可发表」的 ⇒ 同时进可发表列表（nCmd 9 的语义）
        bool playerPost = cfg.NpcId == 0;
        if (playerPost && !state.BbsApply.Contains(msgId))
            state.BbsApply.Add(msgId);

        SaveState(player, state);

        Log.Info($"[session {session.Id}] PhoneMsg nCmd=13 剧情触发帖子 {msgId} npc={cfg.NpcId}" +
                 (playerPost ? "（玩家可发表，已加入可发表列表）" : ""));

        PushAll(session, player);

        if (playerPost)
        {
            // 走 nCmd 9「新增可发表」通道，客户端会刷新红点
            Reply(session, CmdBbsNewApply, new JsonObject { ["nMsgId"] = msgId });
        }
        else
        {
            // 走 nCmd 1 让客户端把新帖渲染出来
            Reply(session, CmdBbs, new JsonObject
            {
                ["nNpcId"] = cfg.NpcId,
                ["nMsgId"] = msgId,
                ["nSelectId"] = 0,
            });
        }
        return Task.CompletedTask;
    }

    // =================================================================
    //  nCmd 8：论坛「可发表帖子 id 列表」
    // =================================================================

    private Task HandleBbsApplyList(ClientSession session, Player player)
    {
        var list = ReadBbsApplyList(player);
        Log.Info($"[session {session.Id}] PhoneMsg nCmd=8 (论坛可发表列表) 下发 {list.Count} 条" +
                 (list.Count > 0 ? $" [{string.Join(",", list)}]" : "（新号为空）"));

        _ctx.LuaCalls.CallClientFunction(session, DownFunction, BuildBbsApplyListPayload(list));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 组 nCmd=8 的回包（下行走 sFunction="ServerPhoneMsg"）。
    /// 三个字段与理由见类注释：<c>nError</c> 必须存在（S2CCall.lua:247 会直接和 0 比大小）、
    /// <c>nCmd</c> 用于分发（S2CCall.lua:297）、<c>tbList</c> 是扁平帖子 id 数组
    /// （PhoneMsgLogic.lua:1816 整体覆盖 <c>tbBBSApplyList</c>）。
    /// </summary>
    internal static JsonObject BuildBbsApplyListPayload(IReadOnlyList<int> msgIds)
    {
        var arr = new JsonArray();
        foreach (int id in msgIds)
            arr.Add(id);

        return new JsonObject
        {
            ["nError"] = 0,
            ["nCmd"] = CmdBbsApplyList,
            ["tbList"] = arr,
        };
    }

    /// <summary>
    /// 读出本玩家「可发表帖子 id 列表」（<c>Player.Extra["phonemsg"].bbsApply</c>）。
    /// </summary>
    internal static List<int> ReadBbsApplyList(Player player)
        => new(LoadStateStatic(player).BbsApply);

    // =================================================================
    //  纯下行通道（无上行）：nCmd 5 / 6 / 9
    // =================================================================

    /// <summary>
    /// nCmd 5「生气回复」：把某个 NPC 的私信标成生气状态（<c>LetterInfo.replypos</c>）。
    /// ★ 无上行发起点 —— 由服务端剧情/GM 主动推。
    /// 客户端 <c>AddLetterNpcReplyPos(nNpcId, nMsgId, nSelectId)</c>（`S2CCall.lua:274`）。
    /// </summary>
    internal void PushLetterAngry(ClientSession session, Player player,
                                  int npcId, int msgId, int pos)
    {
        var state = LoadState(player);
        var letter = EnsureLetter(state, npcId);
        letter.ReplyPos = pos;
        SaveState(player, state);

        PushAll(session, player);
        Reply(session, CmdLetterAngry, new JsonObject
        {
            ["nNpcId"] = npcId,
            ["nMsgId"] = msgId,
            ["nSelectId"] = pos,
        });
        Log.Info($"[session {session.Id}] PhoneMsg nCmd=5 推「生气」npc={npcId} 话题={msgId} pos={pos}");
    }

    /// <summary>
    /// nCmd 6「不生气了」：清掉生气标记。
    /// 客户端 <c>ClearLetterNpcReplyPos(nNpcId)</c> 返回「最后一条话题 id」
    /// 再交给 <c>ServerResponseLetterNpcMsgPos</c>（`S2CCall.lua:277..279`）
    /// ⇒ 服务端只回 <c>nNpcId</c> 即可。
    /// </summary>
    internal void PushLetterAngryDone(ClientSession session, Player player, int npcId)
    {
        var state = LoadState(player);
        var letter = EnsureLetter(state, npcId);
        letter.ReplyPos = 0;
        SaveState(player, state);

        PushAll(session, player);
        Reply(session, CmdLetterAngryDone, new JsonObject { ["nNpcId"] = npcId });
        Log.Info($"[session {session.Id}] PhoneMsg nCmd=6 推「不生气了」npc={npcId}");
    }

    /// <summary>
    /// nCmd 9「新增可发表帖子」：往可发表列表里追加一个 id 并通知客户端。
    /// ★ 无上行 —— 服务端主动推（`S2CCall.lua:308` → <c>ServerResponseAddBBSApply</c>）。
    /// </summary>
    internal void PushBbsNewApply(ClientSession session, Player player, int msgId)
    {
        var state = LoadState(player);
        if (!state.BbsApply.Contains(msgId))
            state.BbsApply.Add(msgId);
        SaveState(player, state);

        Reply(session, CmdBbsNewApply, new JsonObject { ["nMsgId"] = msgId });
        Log.Info($"[session {session.Id}] PhoneMsg nCmd=9 推「新增可发表帖子」{msgId}");
    }

    // =================================================================
    //  好感度联动
    // =================================================================

    /// <summary>
    /// 把「好感度经验」加到少女身上并往回包里塞 <c>tbExp</c>。
    ///
    /// ★★★ <c>tbExp</c> 是**六元位置数组**，对应
    /// <c>GirlLogic:OnGirlAddExp(nId, nAdd, nOldExp, nNewExp, nOldLevel, nNewLevel, bHasL2D)</c>
    /// （`GirlLogic.lua:979`）—— 首参 <c>nId</c> 由 <c>nNpcId</c> 单独传，
    /// 所以 <c>tbExp</c> 里是后 6 个。
    /// ★ 客户端 <c>:980</c> 第一句就判 <c>nOldExp == nil or ...</c> 直接 return
    /// ⇒ 六个都必须给，缺一个整段静默失效。
    ///
    /// ★ 只在 <c>UI_Phone</c> 开着时才播升级动画（`S2CCall.lua:259..264`），
    /// 不影响数据落库。
    ///
    /// 复用 <see cref="GirlLoveFeature.AddExp(Config.GirlLoveTable, Player, uint, int, out GirlLoveFeature.GirlExpDelta)"/>
    /// 的**静态版本**（显式传配置表）——
    /// 本项目 <see cref="GameFeatureContext"/> 有意不提供「跨 feature 取实例」，
    /// 因为那类共享在并行单测里会串（`HANDOFF` §8.0 教训 2）。
    /// 配置表是只读的，本域自己 Load 一份没有副作用。
    /// </summary>
    private void AttachGirlExp(ClientSession session, Player player, JsonObject reply,
                               int npcId, int[] awards, int selectId)
    {
        int exp = PhoneMsgTable.AwardExpAt(awards, selectId);
        if (exp <= 0 || npcId <= 0)
            return;

        if (!GirlLoveFeature.AddExp(_girlLove, player, (uint)npcId, exp, out var delta))
        {
            Log.Info($"[session {session.Id}] PhoneMsg 好感度未加成（少女 {npcId} 不存在或已满级）");
            return;
        }

        var unlockedStories = GirlLoveFeature.UnlockStories(_girlLove, player, (uint)npcId);
        _ctx.Store.Save(player);
        GameNotify.PushTaskValues(session, unlockedStories);

        // ★ 好感度存 protobuf 不存任务值 ⇒ 必须推 pf 1030（docs/05 §27 那条教训）
        var girl = player.GetGirl((uint)npcId);
        if (girl is not null)
            GameNotify.PushGirl(session, girl);

        reply["tbExp"] = new JsonArray
        {
            delta.Added, delta.OldExp, delta.NewExp, delta.OldLevel, delta.NewLevel, delta.IsMaxLevel,
        };
        Log.Info($"[session {session.Id}] PhoneMsg 好感度 npc={npcId} +{delta.Added}" +
                 $"（{delta.OldLevel}级 {delta.OldExp} → {delta.NewLevel}级 {delta.NewExp}）");
    }

    // =================================================================
    //  状态存取
    // =================================================================

    private PhoneState LoadState(Player player) => LoadStateStatic(player);

    internal static PhoneState LoadStateStatic(Player player)
    {
        player.Extra.TryGetValue(ExtraKey, out var node);
        return PhoneState.FromJson(node as JsonObject);
    }

    private static int StrictPositiveId(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<int>(out int id) && id > 0 ? id : 0;

    private bool CanReceiveLetter(Player player, PhoneState state, LetterTopic cfg)
        => state.ReceivedLetters.Contains(cfg.Id) || state.CompletedLetters.Contains(cfg.Id) ||
           state.ClaimedLetters.Contains(cfg.Id) ||
           (state.Letters.TryGetValue(cfg.NpcId, out var letter) && letter.Topics.ContainsKey(cfg.Id)) ||
           (_table.LetterEvent(cfg.Id) is { } ev && Satisfied(player, state, ev, cfg.NpcId, out _));

    private void RememberLetterHistory(PhoneState state)
    {
        foreach (var letter in state.Letters.Values)
            foreach (var topic in letter.Topics.Values)
            {
                state.ReceivedLetters.Add(topic.TopicId);
                if (_table.Letter(topic.TopicId) is { } cfg && PhoneDialog.Complete(cfg, topic))
                    state.CompletedLetters.Add(topic.TopicId);
            }
    }

    private void SaveState(Player player, PhoneState state)
    {
        RememberLetterHistory(state);
        player.Extra[ExtraKey] = state.ToJson();
        _ctx.Store.Save(player);
    }

    private static LetterState EnsureLetter(PhoneState state, int npcId)
    {
        if (!state.Letters.TryGetValue(npcId, out var l))
        {
            state.Letters[npcId] = l = new LetterState
            {
                NpcId = npcId,
                CreateTime = Now(),
            };
        }
        return l;
    }

    private static TopicState EnsureTopic(LetterState letter, int topicId)
    {
        if (!letter.Topics.TryGetValue(topicId, out var t))
            letter.Topics[topicId] = t = new TopicState { TopicId = topicId };
        return t;
    }

    private static BbsState EnsureBbs(PhoneState state, int topicId)
    {
        if (!state.Bbs.TryGetValue(topicId, out var b))
        {
            state.Bbs[topicId] = b = new BbsState
            {
                TopicId = topicId,
                CreateTime = Now(),
            };
        }
        return b;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // =================================================================
    //  回包
    // =================================================================

    /// <summary>
    /// 成功回包。★ <c>nError</c> 必须带 0（`S2CCall.lua:247` 是无条件数值比较），
    /// <c>nCmd</c> 用于分发。其余字段由各 handler 填。
    /// </summary>
    private void Reply(ClientSession session, int nCmd, JsonObject body)
    {
        body["nError"] = ErrOk;
        body["nCmd"] = nCmd;
        _ctx.LuaCalls.CallClientFunction(session, DownFunction, body);
    }

    /// <summary>失败回包（<c>nError &gt; 0</c> ⇒ 客户端弹 `ui.Error.&lt;code&gt;`）。</summary>
    private void Fail(ClientSession session, int nCmd, int code)
        => _ctx.LuaCalls.CallClientFunction(session, DownFunction, new JsonObject
        {
            ["nError"] = code <= 0 ? ErrGeneric : code,
            ["nCmd"] = nCmd,
        });

    /// <summary>
    /// nCmd → 人类可读名（仅日志用）。
    /// 1/2/3 取自 <c>UI_Phone.lua:8..10</c>（TYPE_BBS=1 / TYPE_LETTER=3）与
    /// <c>S2C.ServerPhoneMsg</c> 的中文注释（S2CCall.lua:252 论坛 / :265 私信）；
    /// 4..13 取自 <c>PhoneMsgLogic.lua:34..43</c> 的 TYPE_* 常量。
    /// </summary>
    internal static string SubCmdName(int nCmd) => nCmd switch
    {
        1 => "TYPE_BBS 论坛回帖",
        2 => "邮件（UI_Phone 第 2 页，无服务端取证）",
        3 => "TYPE_LETTER 私信回复",
        4 => "TYPE_LETTERTITLE 私信标题",
        5 => "TYPE_LETTERANGRY 生气回复",
        6 => "TYPE_LETTERANGRYDONE 不生气了",
        7 => "TYPE_LETTERAWARD 私信奖励",
        8 => "TYPE_BBSAPPLYLIST 论坛可发表列表",
        9 => "TYPE_BBSNEWAPPLY 论坛新增可发表",
        10 => "TYPE_LETTERDEL 删除私信",
        11 => "TYPE_LETTERGET 获取私信",
        12 => "TYPE_LETTEREVENT 剧情触发私信",
        13 => "TYPE_BBSEVENT 剧情触发论坛",
        _ => "未知",
    };

    /// <summary>读整数；Lua/JSON 侧可能给 float 或字符串，兜一层。</summary>
    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var d) ? (int)d : fallback;
        }
    }

    /// <summary>给 <see cref="PhoneState"/> 等状态类用的 JSON 读数（容忍字符串/浮点）。</summary>
    internal static int JsonInt(JsonNode? node) => ReadInt(node, 0);

    /// <summary>同上，64 位（时间戳）。</summary>
    internal static long JsonLong(JsonNode? node)
    {
        if (node is null)
            return 0;
        try
        {
            return node.GetValue<long>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var d) ? (long)d : 0;
        }
    }
}
