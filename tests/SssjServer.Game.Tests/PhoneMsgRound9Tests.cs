using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 终端私信 / 朋友圈的**第九轮新增部分**（pf 1035 + 除 nCmd 8 之外的全部子命令）。
///
/// nCmd 8 与「上行外壳/未登录」等既有断言在 <see cref="PhoneMsgFeatureTests"/>，
/// 本文件只覆盖本轮新增的行为。
///
/// 断言重点：
/// <list type="number">
/// <item>★★★ <b>pf 1035 登录必推</b> —— 它是整域唯一数据源，不推两个页签永远空白。</item>
/// <item>★★ <b>pf 1035 全量覆盖</b> —— 客户端先 Clear 再填，所以每次变更都要重推全量。</item>
/// <item>★★ <b>回包只回显 id</b> —— 客户端自己造 LetterInfo，服务端不构造结构体。</item>
/// <item>私信领奖加的是**少女好感度经验**（不是物品），且 <c>tbExp</c> 必须是**六元数组**。</item>
/// <item>状态要**持久化**（重登后私信还在）。</item>
/// </list>
/// </summary>
public class PhoneMsgRound9Tests
{
    /// <summary>私信靶子：话题 135，NPC 1，AwardInfo = <c>10,5</c>（本地表实测）。</summary>
    private const int LetterId = 135;
    private const int LetterNpc = 1;

    /// <summary>朋友圈靶子：帖子 1，NPC 7，AwardInfo = <c>5,10,10</c>，ReplyIdx = 1。</summary>
    private const int BbsId = 1;
    private const int BbsNpc = 7;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    public async Task LetterRewardCrossesLevelTenAndPersistsStoryUnlock(int prior, int expected)
    {
        using var h = await FeatureHarness.CreateAsync("letter-love-threshold");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":1}}");
        await PhoneDialogTests.Finish(h);
        var table = GirlLoveTable.Load(TestPaths.SettingDir);
        var girl = h.Player.GetGirl(1)!;
        girl.Level = 9;
        girl.Exp = (uint)(table.LevelUpExp(1, 9) - 1);
        int task = GirlLoveFeature.VarTaskId(1, GirlLoveFeature.ELevelOffset(5));
        h.Player.SetTask(3, task, prior);
        await SendAsync(h, $"{{\"nCmd\":7,\"nMsgId\":{LetterId}}}");
        Assert.Equal(10u, girl.Level);
        Assert.Equal(expected, h.Player.GetTask(3, task));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(expected, saved.GetTask(3, task));
        Assert.Equal(girl.Level, saved.GetGirl(1)!.Level);
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    private static Task SendAsync(FeatureHarness h, string json)
        => h.LuaCalls.DispatchAsync(h.Session, PhoneMsgFeature.UpFunction, json);

    private static JsonObject LastReply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(PhoneMsgFeature.DownFunction, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    // ================= ① 配置表探针 =================

    [Fact]
    public void Table_LoadsAllFiveFiles_WithExpectedShape()
    {
        var t = PhoneMsgTable.Load(TestPaths.SettingDir);

        // 本地快照：LetterInfoNew ★ 第十轮换新版 setting.u 后归并 539 → **549** 条话题
        Assert.Equal(549, t.LetterCount);
        // ★ 第十轮换新版 setting.u 后 BBSInfo.txt 274 → **287** 条
        Assert.Equal(287, t.BbsCount);
        // ★ 第十轮换新版 setting.u 后 LetterEvent.txt 308 → **318** 条
        Assert.Equal(318, t.LetterEventCount);
        // ★ 第十轮换新版 setting.u 后 BBSEvent.txt 152 → **165** 条
        Assert.Equal(165, t.BbsEventCount);
        // ★ 26 而不是 30：NpcIdList.txt 共 33 行，但含空行/占位行
        //   （`awk -F'\t' '$1 ~ /^[0-9]+$/'` 实测 26，与 HANDOFF §4.2.5 记的 26 一致）。
        //   写这条测试时先按「文件行数减表头」猜成 30，被本探针抓到 ——
        //   正是 HANDOFF §8.0 教训 1「探针红了先怀疑取证、不是代码」。
        // ★ 第十轮换新版 setting.u 后 NpcIdList.txt 26 → **29** 条
        Assert.Equal(29, t.NpcCount);

        // 私信靶子：多行归并后 NpcId / AwardInfo 都取到了
        var letter = t.Letter(LetterId);
        Assert.NotNull(letter);
        Assert.Equal(LetterNpc, letter!.NpcId);
        Assert.Equal(new[] { 10, 5 }, letter.AwardExp);
        Assert.True(letter.LineCount > 1, "话题应当由多行归并而成");

        // 帖子靶子
        var bbs = t.Bbs(BbsId);
        Assert.NotNull(bbs);
        Assert.Equal(BbsNpc, bbs!.NpcId);
        Assert.Equal(new[] { 5, 10, 10 }, bbs.AwardExp);
        Assert.Equal(1, bbs.ReplyIdx);
    }

    /// <summary>
    /// ★ 钉住一条实测事实：<c>LetterEvent</c> 的 308 个 id 里有相当一部分
    /// 在 <c>LetterInfoNew</c> 找不到对应话题（孤儿配置，指向旧表/已下线内容）
    /// ⇒ nCmd 12 必须对「找不到话题」优雅跳过而不是当错误。
    /// </summary>
    [Fact]
    public void LetterEvents_ContainOrphanIds_WhichMustBeToleratedNotTreatedAsError()
    {
        var t = PhoneMsgTable.Load(TestPaths.SettingDir);
        int orphan = 0;
        for (int id = 1; id <= 320000; id++)
        {
            if (t.LetterEvent(id) is not null && t.Letter(id) is null)
                orphan++;
        }
        Assert.True(orphan > 100, $"孤儿触发配置只有 {orphan} 条 —— 与实测 134 条不符？");
    }

    /// <summary>
    /// ★ 钉住另一条实测事实：<c>BBSInfo.txt</c> 里**没有任何 <c>NpcId == 0</c> 的行**
    /// ⇒ 「玩家可发表帖子」在现网配置里是**空集**，
    /// 所以 nCmd 8 回空列表是正确的，nCmd 9「新增可发表」现阶段也没有数据来源。
    /// 这解释了为什么朋友圈只能「看 + 回帖」，不能自己发帖。
    /// </summary>
    [Fact]
    public void Bbs_HasNoPlayerAuthoredTopics_InCurrentConfig()
    {
        var t = PhoneMsgTable.Load(TestPaths.SettingDir);
        Assert.DoesNotContain(t.AllBbs, b => b.NpcId == 0);
    }

    // ================= ② pf 1035 =================

    /// <summary>★★★ 登录必推 pf 1035（哪怕一条私信都没有）。</summary>
    [Fact]
    public async Task Login_PushesPhoneMsgNtf_EvenWhenEmpty()
    {
        using var h = await FeatureHarness.CreateAsync("phone-login");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        h.Module.RunLoginHooksForTest(h.Session, h.Player);

        Assert.True(await h.WaitForPfAsync(GamePf.PhoneMsgNtf),
            "登录必须推 pf 1035，否则私信/朋友圈两个页签永远空白");
    }

    /// <summary>★★ 每条命令处理后都要重推全量 pf 1035（客户端 Clear 后重填）。</summary>
    [Fact]
    public async Task EachCommand_RepushesFullPhoneMsgNtf()
    {
        using var h = await FeatureHarness.CreateAsync("phone-repush");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":12,\"nMsgId\":{LetterId}}}");

        Assert.True(await h.WaitForPfAsync(GamePf.PhoneMsgNtf),
            "剧情触发私信后必须重推 pf 1035");
    }

    // ================= ③ nCmd 12/13：剧情触发 =================

    [Fact]
    public async Task LetterEvent_CreatesTopic_AndRepliesAsNewLetter()
    {
        using var h = await FeatureHarness.CreateAsync("phone-levent");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":12,\"nMsgId\":{LetterId}}}");

        var reply = LastReply(h);
        Assert.Equal(PhoneMsgFeature.ErrOk, reply["nError"]!.GetValue<int>());
        // ★ 用 nCmd=3 回（客户端没有 12 的分支）
        Assert.Equal(PhoneMsgFeature.CmdLetter, reply["nCmd"]!.GetValue<int>());
        Assert.Equal(LetterNpc, reply["nNpcId"]!.GetValue<int>());
        Assert.Equal(LetterId, reply["nMsgId"]!.GetValue<int>());
        Assert.Equal(0, reply["nSelectId"]!.GetValue<int>());

        // 状态落库
        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.True(state.Letters.ContainsKey(LetterNpc));
        Assert.True(state.Letters[LetterNpc].Topics.ContainsKey(LetterId));
    }

    /// <summary>★ 幂等：同一段剧情反复触发不重复添加（<c>CartoonData</c> 每次播放都会调）。</summary>
    [Fact]
    public async Task LetterEvent_IsIdempotent()
    {
        using var h = await FeatureHarness.CreateAsync("phone-levent-idem");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":12,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":12,\"nMsgId\":{LetterId}}}");

        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.Single(state.Letters[LetterNpc].Topics);
        // 第二次也要回包（否则遮罩不解）
        Assert.Equal(PhoneMsgFeature.ErrOk, LastReply(h)["nError"]!.GetValue<int>());
    }

    /// <summary>孤儿触发 id（不在 LetterInfoNew）要优雅跳过并回包。</summary>
    [Fact]
    public async Task LetterEvent_OrphanId_RepliesOkWithoutCreatingState()
    {
        using var h = await FeatureHarness.CreateAsync("phone-levent-orphan");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, "{\"nCmd\":12,\"nMsgId\":9}");

        Assert.Equal(PhoneMsgFeature.ErrOk, LastReply(h)["nError"]!.GetValue<int>());
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
    }

    [Fact]
    public async Task BbsEvent_CreatesTopic_AndRepliesAsNewPost()
    {
        using var h = await FeatureHarness.CreateAsync("phone-bevent");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        h.Player.Levels[ChapterTable.Load(TestPaths.SettingDir).Get(2, 6, 1)!.MapId] = 7;
        await SendAsync(h, $"{{\"nCmd\":13,\"nMsgId\":{BbsId}}}");

        var reply = LastReply(h);
        Assert.Equal(PhoneMsgFeature.ErrOk, reply["nError"]!.GetValue<int>());
        // NpcId != 0 ⇒ 走 nCmd 1（新帖），不是 nCmd 9
        Assert.Equal(PhoneMsgFeature.CmdBbs, reply["nCmd"]!.GetValue<int>());
        Assert.Equal(BbsNpc, reply["nNpcId"]!.GetValue<int>());

        Assert.True(PhoneMsgFeature.LoadStateStatic(h.Player).Bbs.ContainsKey(BbsId));
    }

    // ================= ④ nCmd 3/1：回复 =================

    /// <summary>
    /// 私信回复：记录选择并回显。
    ///
    /// ★★★ 第十四轮修正：<c>nSelectId</c> 的**上行**是候选序号（1/2/3…），
    /// 而**下行与存档**必须是 <c>位置*10 + 分支</c> 的复合编码 —— 客户端
    /// <c>MakeLetterSingleInfo</c> 用 <c>GetLetterNpcReplyInfo(npc, topic, nTopicPos)</c>
    /// **按位置**查这个数组，位置对不上就丢弃整句 ⇒ 界面「点了没反应」。
    /// 本用例原先断言「原样回显 2」，那正是 bug 的形状。
    /// </summary>
    [Fact]
    public async Task LetterReply_RecordsSelection_AndEchoesIds()
    {
        using var h = await FeatureHarness.CreateAsync("phone-lreply");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":2}}");

        var reply = LastReply(h);
        Assert.Equal(PhoneMsgFeature.CmdLetter, reply["nCmd"]!.GetValue<int>());
        Assert.Equal(LetterNpc, reply["nNpcId"]!.GetValue<int>());
        Assert.Equal(LetterId, reply["nMsgId"]!.GetValue<int>());

        int replyId = reply["nSelectId"]!.GetValue<int>();

        // ★ 分支部分必须是玩家选的候选序号
        Assert.Equal(2, PhoneMsgTable.ReplyBranchOf(replyId));
        // ★★ 位置部分**必须非 0** —— 位置 0 正是那个「点了没反应」的 bug
        Assert.True(PhoneMsgTable.ReplyPosOf(replyId) > 0,
            $"replyId={replyId} 的位置部分为 0，客户端 GetLetterNpcReplyInfo 将永远查不到");

        var topic = PhoneMsgFeature.LoadStateStatic(h.Player).Letters[LetterNpc].Topics[LetterId];
        Assert.Equal(new[] { replyId }, topic.MsgIds);
    }

    /// <summary>论坛回帖：记录选项 + 加好感度（<c>AwardInfo[nSelectId]</c>）。</summary>
    [Fact]
    public async Task BbsReply_RecordsReply_AndGrantsGirlExp()
    {
        using var h = await FeatureHarness.CreateAsync("phone-breply");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        // 好感度要有少女实例才加得上
        var girl = new Girl { Girlid = (uint)BbsNpc };
        h.Player.Girls.Add(girl);
        int expBefore = (int)girl.Exp;

        h.Player.Levels[ChapterTable.Load(TestPaths.SettingDir).Get(2, 6, 1)!.MapId] = 7;
        await SendAsync(h, $"{{\"nCmd\":13,\"nMsgId\":{BbsId}}}");
        await SendAsync(h, $"{{\"nCmd\":1,\"nMsgId\":{BbsId},\"nSelectId\":2}}");

        var reply = LastReply(h);
        Assert.Equal(PhoneMsgFeature.CmdBbs, reply["nCmd"]!.GetValue<int>());
        Assert.Equal(2, PhoneMsgFeature.LoadStateStatic(h.Player).Bbs[BbsId].ReplyInfo);

        // AwardInfo[2] = 10 ⇒ 好感度 +10，且 tbExp 必须是六元数组
        var tbExp = Assert.IsType<JsonArray>(reply["tbExp"]);
        Assert.Equal(6, tbExp.Count);
        Assert.Equal(10, tbExp[0]!.GetValue<int>());
        Assert.Equal(expBefore + 10, (int)h.Player.GetGirl((uint)BbsNpc)!.Exp);
        // 好感度存 protobuf ⇒ 必须推 pf 1030
        Assert.True(await h.WaitForPfAsync(GamePf.GirlDataNtf), "加好感度必须推 pf 1030");
    }

    // ================= ⑤ nCmd 4：改标题 =================

    [Fact]
    public async Task LetterTitle_SetAndClear()
    {
        using var h = await FeatureHarness.CreateAsync("phone-title");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":4,\"nNpcId\":{LetterNpc},\"strTitle\":\"小樱\"}}");
        Assert.Equal("小樱", LastReply(h)["strTitle"]!.GetValue<string>());
        Assert.Equal("小樱", PhoneMsgFeature.LoadStateStatic(h.Player).Letters[LetterNpc].Title);

        // ★ 固定词 ClearLetterTitle ⇒ 清空，且下行回**空串**（客户端据此置 nil）
        await SendAsync(h,
            $"{{\"nCmd\":4,\"nNpcId\":{LetterNpc},\"strTitle\":\"{PhoneMsgFeature.ClearTitleToken}\"}}");
        Assert.Equal("", LastReply(h)["strTitle"]!.GetValue<string>());
        Assert.Equal("", PhoneMsgFeature.LoadStateStatic(h.Player).Letters[LetterNpc].Title);
    }

    /// <summary>NPC 111 不允许改标题（客户端也拦，服务端兜一层防改包）。</summary>
    [Fact]
    public async Task LetterTitle_FixedNpc_IsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("phone-title-fixed");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h,
            $"{{\"nCmd\":4,\"nNpcId\":{PhoneMsgFeature.FixedTitleNpcId},\"strTitle\":\"x\"}}");

        Assert.True(LastReply(h)["nError"]!.GetValue<int>() > 0);
    }

    // ================= ⑥ nCmd 7：领奖 =================

    [Fact]
    public async Task LetterAward_GrantsGirlExp_AndSetsAwardFlag()
    {
        using var h = await FeatureHarness.CreateAsync("phone-award");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        h.Player.Girls.Add(new Girl { Girlid = (uint)LetterNpc });

        // 先回复一次（选项 1），领奖按最后一次选择结算 ⇒ AwardInfo[1] = 10
        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":1}}");
        int expBefore = (int)h.Player.GetGirl((uint)LetterNpc)!.Exp;
        await PhoneDialogTests.Finish(h);

        await SendAsync(h, $"{{\"nCmd\":7,\"nMsgId\":{LetterId}}}");

        var reply = LastReply(h);
        Assert.Equal(PhoneMsgFeature.CmdLetterAward, reply["nCmd"]!.GetValue<int>());
        var tbExp = Assert.IsType<JsonArray>(reply["tbExp"]);
        Assert.Equal(6, tbExp.Count);
        Assert.Equal(10, tbExp[0]!.GetValue<int>());
        Assert.Equal(expBefore + 10, (int)h.Player.GetGirl((uint)LetterNpc)!.Exp);

        // awardflag 置 1
        Assert.Equal(1, PhoneMsgFeature.LoadStateStatic(h.Player)
                                       .Letters[LetterNpc].Topics[LetterId].AwardFlag);
    }

    /// <summary>重复领奖要拒绝（客户端自查 + 服务端兜底）。</summary>
    [Fact]
    public async Task LetterAward_Twice_SecondIsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("phone-award-twice");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        h.Player.Girls.Add(new Girl { Girlid = (uint)LetterNpc });
        await PhoneDialogTests.Finish(h);
        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");

        await SendAsync(h, $"{{\"nCmd\":7,\"nMsgId\":{LetterId}}}");
        Assert.Equal(PhoneMsgFeature.ErrOk, LastReply(h)["nError"]!.GetValue<int>());

        await SendAsync(h, $"{{\"nCmd\":7,\"nMsgId\":{LetterId}}}");
        Assert.True(LastReply(h)["nError"]!.GetValue<int>() > 0);
    }

    // ================= ⑦ nCmd 10/11 =================

    [Fact]
    public async Task LetterGet_AddsTopic_ThenDeleteRemovesIt()
    {
        using var h = await FeatureHarness.CreateAsync("phone-getdel");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        Assert.True(PhoneMsgFeature.LoadStateStatic(h.Player)
                                   .Letters[LetterNpc].Topics.ContainsKey(LetterId));

        await SendAsync(h, $"{{\"nCmd\":10,\"nMsgId\":{LetterId}}}");
        var reply = LastReply(h);
        Assert.Equal(PhoneMsgFeature.CmdLetterDel, reply["nCmd"]!.GetValue<int>());
        Assert.Equal(LetterNpc, reply["nNpcId"]!.GetValue<int>());
        // 该 NPC 一条话题不剩 ⇒ 整条对话删掉
        Assert.Empty(PhoneMsgFeature.LoadStateStatic(h.Player).Letters);
    }

    // ================= ⑧ 持久化 =================

    /// <summary>★ 状态存 <c>Player.Extra</c> ⇒ 重登后私信/帖子/领奖标记都还在。</summary>
    [Fact]
    public async Task State_SurvivesRoundTripThroughExtra()
    {
        using var h = await FeatureHarness.CreateAsync("phone-persist");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        h.Player.Girls.Add(new Girl { Girlid = (uint)LetterNpc });

        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":2}}");
        await PhoneDialogTests.Finish(h);
        await SendAsync(h, $"{{\"nCmd\":4,\"nNpcId\":{LetterNpc},\"strTitle\":\"阿P\"}}");
        await SendAsync(h, $"{{\"nCmd\":7,\"nMsgId\":{LetterId}}}");
        h.Player.Levels[ChapterTable.Load(TestPaths.SettingDir).Get(2, 6, 1)!.MapId] = 7;
        await SendAsync(h, $"{{\"nCmd\":13,\"nMsgId\":{BbsId}}}");

        // 模拟「存档 → 读档」：把 Extra 里的 JSON 重新解一遍
        var reloaded = PhoneState.FromJson(
            (JsonObject)h.Player.Extra[PhoneMsgFeature.ExtraKey]!);

        var letter = reloaded.Letters[LetterNpc];
        Assert.Equal("阿P", letter.Title);
        // ★ 存的是 `位置*10+分支` 复合编码（第十四轮），不再是裸候选序号；
        //   这里只校验「往返不丢」+「位置非 0」，不写死具体数值。
        var msgIds = letter.Topics[LetterId].MsgIds;
        Assert.NotEmpty(msgIds);
        Assert.Equal(2, PhoneMsgTable.ReplyBranchOf(msgIds[0]));
        Assert.True(PhoneMsgTable.ReplyPosOf(msgIds[0]) > 0);
        Assert.Equal(1, letter.Topics[LetterId].AwardFlag);
        Assert.True(reloaded.Bbs.ContainsKey(BbsId));
    }

    /// <summary>
    /// ★★★ 存量迁移：历史存档里的裸候选序号（位置恒 0）必须在登录时被重算。
    /// 不迁移的话，老号那些话题永久卡住 —— 光修 handler 不够。
    /// </summary>
    [Fact]
    public async Task 登录时把裸候选序号迁移成合法编码()
    {
        using var h = await FeatureHarness.CreateAsync("phone-migrate");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        // 造一份「第十四轮之前」的脏存档：msgids 全是裸序号
        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":1}}");
        var st = PhoneMsgFeature.LoadStateStatic(h.Player);
        var topic = st.Letters[LetterNpc].Topics[LetterId];
        topic.MsgIds.Clear();
        topic.MsgIds.AddRange(new[] { 2 });      // 全是位置 0 的脏值
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = st.ToJson();

        h.RunLoginHooks();

        var after = PhoneMsgFeature.LoadStateStatic(h.Player)
                                   .Letters[LetterNpc].Topics[LetterId].MsgIds;

        Assert.Single(after);
        // ★ 位置全部非 0（这正是修复的目标）
        Assert.All(after, v => Assert.True(PhoneMsgTable.ReplyPosOf(v) > 0,
            $"迁移后仍有位置为 0 的 replyId：{v}"));
        // ★ 分支号（= 玩家的选择历史）必须原样保留
        Assert.Equal(new[] { 2 }, after.Select(PhoneMsgTable.ReplyBranchOf).ToArray());
    }

    /// <summary>★ <c>0</c>（NPC 稍后回复）是合法值，迁移不能动它。</summary>
    [Fact]
    public async Task 迁移不改动表示稍后回复的0()
    {
        using var h = await FeatureHarness.CreateAsync("phone-migrate-zero");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":1}}");
        var st = PhoneMsgFeature.LoadStateStatic(h.Player);
        var topic = st.Letters[LetterNpc].Topics[LetterId];
        topic.MsgIds.Clear();
        topic.MsgIds.AddRange(new[] { 0, 1, 0 });
        h.Player.Extra[PhoneMsgFeature.ExtraKey] = st.ToJson();

        h.RunLoginHooks();

        var after = PhoneMsgFeature.LoadStateStatic(h.Player)
                                   .Letters[LetterNpc].Topics[LetterId].MsgIds;
        Assert.Equal(0, after[0]);
        Assert.Equal(0, after[2]);
        Assert.True(PhoneMsgTable.ReplyPosOf(after[1]) > 0);
    }

    /// <summary>★ 已经是合法编码的存档，迁移必须**幂等**（不能反复重算改坏）。</summary>
    [Fact]
    public async Task 迁移对已修复存档幂等()
    {
        using var h = await FeatureHarness.CreateAsync("phone-migrate-idem");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.

        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":2}}");

        h.RunLoginHooks();
        var first = PhoneMsgFeature.LoadStateStatic(h.Player)
                                   .Letters[LetterNpc].Topics[LetterId].MsgIds.ToArray();
        h.RunLoginHooks();
        var second = PhoneMsgFeature.LoadStateStatic(h.Player)
                                    .Letters[LetterNpc].Topics[LetterId].MsgIds.ToArray();

        Assert.Equal(first, second);
    }

    // ========== ⑧b ★★★ 第十四轮：replyId 编码（「回复没反应」的回归闸） ==========

    /// <summary>
    /// <c>EncodeReplyId</c> / <c>SplitReplyId</c> 必须与客户端**逐值**一致。
    ///
    /// 真机自报（<c>PhoneMsgLogic:SplitReplyId</c>）：
    /// <c>1→(0,1)</c>、<c>2→(0,2)</c>、<c>31→(3,1)</c>、<c>32→(3,2)</c>、<c>41→(4,1)</c>。
    /// ★ 其中 <c>1→位置 0</c> 就是 bug 的根源：服务端曾把候选序号当 replyId 存。
    /// </summary>
    [Theory]
    [InlineData(3, 1, 31)]
    [InlineData(3, 2, 32)]
    [InlineData(4, 1, 41)]
    [InlineData(12, 3, 123)]
    public void ReplyId编码与客户端SplitReplyId一致(int pos, int branch, int expected)
    {
        Assert.Equal(expected, PhoneMsgTable.EncodeReplyId(pos, branch));
        Assert.Equal(pos, PhoneMsgTable.ReplyPosOf(expected));
        Assert.Equal(branch, PhoneMsgTable.ReplyBranchOf(expected));
    }

    /// <summary>
    /// ★★★ 裸候选序号（1/2/3）解出的位置必须是 0 —— 用它反证
    /// 「直接存候选序号」为什么会让客户端永远查不到。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void 裸候选序号的位置部分是0说明不能直接下发(int rawSelect)
    {
        Assert.Equal(0, PhoneMsgTable.ReplyPosOf(rawSelect));
    }

    /// <summary>
    /// <c>NextReplyPos</c>（移植 <c>GetNextReply</c>）：
    /// 无分支的线性对话应逐句 +1，走到末尾返回 0。
    /// </summary>
    [Fact]
    public void NextReplyPos_线性对话逐句推进到末尾归零()
    {
        var topic = new LetterTopic
        {
            Id = 1, NpcId = 1, LineCount = 3,
            Lines = new[]
            {
                new LetterLine { InfoType = 0, Info = new[] { "a" } },
                new LetterLine { InfoType = 1, Info = new[] { "b" } },
                new LetterLine { InfoType = 0, Info = new[] { "c" } },
            },
        };

        Assert.Equal(2, PhoneMsgTable.NextReplyPos(topic, 1, 1));
        Assert.Equal(3, PhoneMsgTable.NextReplyPos(topic, 2, 1));
        Assert.Equal(0, PhoneMsgTable.NextReplyPos(topic, 3, 1));
    }

    /// <summary>
    /// <c>"NULL"</c> 是「该分支到此为止」的语义标记（<c>GetNextReply</c> 第一条判断），
    /// 不能在解析时被过滤掉。
    /// </summary>
    [Fact]
    public void NextReplyPos_遇到NULL分支终止()
    {
        var topic = new LetterTopic
        {
            Id = 1, NpcId = 1, LineCount = 2,
            Lines = new[]
            {
                new LetterLine { InfoType = 0, Info = new[] { "a" } },
                new LetterLine { InfoType = 0, Info = new[] { "NULL" }, InfoNum = new[] { 1 } },
            },
        };

        Assert.Equal(0, PhoneMsgTable.NextReplyPos(topic, 1, 1));
    }

    /// <summary>
    /// ★ 真表验证：拿 <c>LetterInfoNew.txt</c> 里真实存在的话题跑一遍，
    /// 保证「首句 → 下一句」的位置是**非 0 且递增**的。
    /// 这条盯的是「表结构变了但代码没跟上」。
    /// </summary>
    [Fact]
    public void NextReplyPos_真实配置表首句能推进()
    {
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);
        var topic = table.AllLetters.FirstOrDefault(t => t.Lines.Count >= 3);
        Assert.NotNull(topic);

        int next = PhoneMsgTable.NextReplyPos(topic!, 1, 1);
        Assert.True(next > 1, $"话题 {topic!.Id}（{topic.Lines.Count} 句）首句推进失败：next={next}");
    }

    // ================= ⑨ ★★★ 登录补发非剧情类触发（第十二轮） =================

    /// <summary>
    /// ★★★ 配置表探针：<c>CheckEvent</c> 只实现了类型 1，其余 8 类**必须**由服务端判。
    /// 这条断言「本地表里确实有大量非剧情类触发」—— 否则整个补发机制就是空转。
    /// </summary>
    [Fact]
    public void 触发表_非剧情类占多数()
    {
        var t = PhoneMsgTable.Load(TestPaths.SettingDir);

        int letterNonStory = t.AllLetterEvents.Count(e => e.LimitType != 1);
        int bbsNonStory = t.AllBbsEvents.Count(e => e.LimitType != 1);

        // 真机实测：LetterEvent 318 行里 108 行是类型 1，其余 210 行非剧情
        Assert.True(letterNonStory > 100,
                    $"LetterEvent 非剧情类应 >100 条，实际 {letterNonStory}");
        Assert.True(bbsNonStory > 50,
                    $"BBSEvent 非剧情类应 >50 条，实际 {bbsNonStory}");

        // ★ 多条件行必须被完整解出来（早先只存 LimitType1 ⇒ AND 判定会误命中）
        Assert.Contains(t.AllLetterEvents, e => e.Conds.Count > 1);
    }

    /// <summary>
    /// ★★ 探针：<c>ev=219 type=5 LimitInfo="16.12.TypeDec"</c> 这类**含多个数字**的单元格，
    /// 客户端 <c>string.gsub(s,"(%d+)",...)</c> 每个数字都回调一次、每次覆写
    /// ⇒ **最后一次胜出** ⇒ 语义值是 **12**，不是 16。
    ///
    /// 服务端早先用「取第一个整数」，会把这条判成少女 16。本条钉住表里确实存在这种单元格，
    /// 免得以后有人把 <c>LastInt</c> 又改回 <c>FirstInt</c>。
    /// </summary>
    [Fact]
    public void 触发表_存在含多个数字的单值条件()
    {
        var t = PhoneMsgTable.Load(TestPaths.SettingDir);

        var multi = t.AllLetterEvents
            .SelectMany(e => e.Conds.Select(c => (e.Id, c)))
            .Where(x => x.c.Type is 2 or 3 or 5 or 7 or 9
                        && x.c.Info.Count(char.IsAsciiDigit) > 0
                        && System.Text.RegularExpressions.Regex
                            .Matches(x.c.Info, @"\d+").Count > 1)
            .ToList();

        Assert.NotEmpty(multi);
        // 现网就这一条：ev 219、type 5、"16.12.TypeDec"
        Assert.Contains(multi, x => x.Id == 219 && x.c.Type == 5);
    }

    /// <summary>
    /// ★★★ 核心回归：<b>通关型触发（类型 8）会在登录时补发</b>。
    ///
    /// 这是「GM 全解锁账号终端没内容」的修复点 —— 全解锁账号有通关记录，
    /// 但客户端永远不会为类型 8 发上行（<c>CheckEvent</c> 无该分支）。
    /// </summary>
    [Fact]
    public async Task Backfill_通关型触发在登录时补发()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-pass");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);
        var chapters = ChapterTable.Load(TestPaths.SettingDir);

        // 找一条「类型 8 单条件、且话题存在」的私信触发做靶子
        var target = table.AllLetterEvents.FirstOrDefault(e =>
            e.Conds.Count == 1 && e.Conds[0].Type == 8 && table.Letter(e.Id) is not null);
        Assert.NotNull(target);

        // 把它要求的那一关记成三星通关
        var seg = target!.Conds[0].Info.Trim().Trim('[', ']').Split('-');
        var lv = chapters.Get(int.Parse(seg[0]), int.Parse(seg[1]), int.Parse(seg[2]));
        Assert.NotNull(lv);
        h.Player.Levels[lv!.MapId] = 7;

        Assert.DoesNotContain(PhoneMsgFeature.LoadStateStatic(h.Player).Letters.Values,
                              l => l.Topics.ContainsKey(target.Id));

        h.RunLoginHooks();

        int npcId = table.Letter(target.Id)!.NpcId;
        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.True(state.Letters.TryGetValue(npcId, out var letter),
                    $"应为 npc {npcId} 建立私信");
        Assert.True(letter!.Topics.ContainsKey(target.Id),
                    $"话题 {target.Id} 应被补发（条件：过关 {target.Conds[0].Info}）");
    }

    /// <summary>
    /// ★★ <b>条件不满足时绝不补发</b> —— 否则会把没解锁的内容提前塞给玩家。
    /// </summary>
    [Fact]
    public async Task Backfill_条件不满足不补发()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-none");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);

        var target = table.AllLetterEvents.FirstOrDefault(e =>
            e.Conds.Count == 1 && e.Conds[0].Type == 8 && table.Letter(e.Id) is not null);
        Assert.NotNull(target);

        h.Player.Levels.Clear();
        h.Player.Girls.Clear();

        h.RunLoginHooks();

        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.False(state.Letters.Values.Any(l => l.Topics.ContainsKey(target!.Id)),
                     $"话题 {target!.Id} 的条件未满足，不该补发");
    }

    /// <summary>
    /// ★★★ <b>剧情类（类型 1）：剧情位域没填时不补发</b>。
    ///
    /// 第十二轮改了语义（原先是「一律不补发」）：现在按「剧情已读」判定。
    /// 本条守的是**非全解锁账号**那一半 —— 没看过剧情就不该凭空出现私信。
    /// </summary>
    [Fact]
    public async Task Backfill_剧情类未读时不补发()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-story");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);

        var story = table.AllLetterEvents.FirstOrDefault(e =>
            e.Conds.Count == 1 && e.Conds[0].Type == 1 && table.Letter(e.Id) is not null);
        Assert.NotNull(story);

        // 明确保证剧情位域是空的（新号本来就是，写清楚是为了表达意图）
        h.Player.Tasks.Clear();

        h.RunLoginHooks();

        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.DoesNotContain(state.Letters.Values, l => l.Topics.ContainsKey(story!.Id));
    }

    /// <summary>
    /// ★★★ <b>剧情位域全填后，剧情类触发要能补发</b> —— 这是本轮「剧情+CG 全解锁」
    /// 的最终目的：让那 177 条类型 1 的私信/帖子也能出现在终端里。
    /// </summary>
    [Fact]
    public async Task Backfill_剧情已读后补发剧情类()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-story-read");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);

        var story = table.AllLetterEvents.FirstOrDefault(e =>
            e.Conds.Count == 1 && e.Conds[0].Type == 1 && table.Letter(e.Id) is not null);
        Assert.NotNull(story);

        // 模拟 GmUnlock.ApplyCartoons 的效果（填满剧情已读位域）
        for (int t = GmUnlock.CartoonMinTaskId; t <= GmUnlock.CartoonMaxTaskId; t++)
            h.Player.SetTask(GmUnlock.CartoonGroup, t, int.MaxValue);

        h.RunLoginHooks();

        int npcId = table.Letter(story!.Id)!.NpcId;
        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.True(state.Letters.TryGetValue(npcId, out var letter)
                    && letter!.Topics.ContainsKey(story.Id),
                    $"剧情已读后，类型 1 话题 {story.Id}（npc {npcId}）应被补发");
    }

    /// <summary>
    /// ★★ <b>幂等</b>：重复登录不重复补发（否则每次重登都刷一批「新私信」气泡）。
    /// </summary>
    [Fact]
    public async Task Backfill_重复登录幂等()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-idem");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var chapters = ChapterTable.Load(TestPaths.SettingDir);
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);

        foreach (var e in table.AllLetterEvents.Where(x => x.Conds.Count == 1 && x.Conds[0].Type == 8))
        {
            var seg = e.Conds[0].Info.Trim().Trim('[', ']').Split('-');
            if (seg.Length < 3 || !int.TryParse(seg[0], out int a)
                || !int.TryParse(seg[1], out int b) || !int.TryParse(seg[2], out int c))
                continue;
            var lv = chapters.Get(a, b, c);
            if (lv is not null)
                h.Player.Levels[lv.MapId] = 7;
        }

        h.RunLoginHooks();
        int first = PhoneMsgFeature.LoadStateStatic(h.Player)
            .Letters.Values.Sum(l => l.Topics.Count);
        Assert.True(first > 0, "测试前提：第一次登录要补发出内容");

        h.RunLoginHooks();
        int second = PhoneMsgFeature.LoadStateStatic(h.Player)
            .Letters.Values.Sum(l => l.Topics.Count);

        Assert.Equal(first, second);
    }

    /// <summary>★ 补发**不能覆盖已有进度**：已回过的话题保留 <c>MsgIds</c>。</summary>
    [Fact]
    public async Task Backfill_不覆盖已有进度()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-keep");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        h.Player.Girls.Add(new Girl { Girlid = (uint)LetterNpc });

        await SendAsync(h, $"{{\"nCmd\":11,\"nMsgId\":{LetterId}}}");
        await SendAsync(h, $"{{\"nCmd\":3,\"nMsgId\":{LetterId},\"nSelectId\":2}}");
        var before = PhoneMsgFeature.LoadStateStatic(h.Player)
            .Letters[LetterNpc].Topics[LetterId].MsgIds.ToList();
        Assert.NotEmpty(before);

        h.RunLoginHooks();

        var after = PhoneMsgFeature.LoadStateStatic(h.Player)
            .Letters[LetterNpc].Topics[LetterId].MsgIds;
        Assert.Equal(before, after);
    }

    /// <summary>
    /// ★★★ 类型 3「好感度等级」判的是**该私信归属 NPC 自己**的等级，不是「任一少女」。
    ///
    /// 这条是实现过程中**自己踩过并纠正**的一个语义错误，值得一条专门的闸：
    /// 本地表里阈值严格按 NpcId 成组（npc1 → 15/50/60、npc5 → 25/45/55/60），
    /// 若按「任一少女达到」判，好感最高的少女会把所有 NPC 的门槛一次性顶开
    /// ⇒ 一登录塞出几十条未解锁私信（剧透 + 乱序）。
    ///
    /// 构造：给**别的**少女拉满好感，靶子 NPC 自己保持 Lv1 ⇒ 必须不补发。
    /// </summary>
    [Fact]
    public async Task Backfill_好感度触发只看归属少女()
    {
        using var h = await FeatureHarness.CreateAsync("phone-backfill-love-scope");
        h.Player.SetTask(18, 662, 200); // Topic 135 requires achievement 662 before first receipt.
        var table = PhoneMsgTable.Load(TestPaths.SettingDir);

        // 纯类型 3 单条件、话题存在、且归属 NPC 明确的靶子
        var target = table.AllLetterEvents.FirstOrDefault(e =>
            e.Conds.Count == 1 && e.Conds[0].Type == 3
            && table.Letter(e.Id) is { NpcId: > 0 });
        Assert.NotNull(target);

        int ownerNpc = table.Letter(target!.Id)!.NpcId;
        int need = int.Parse(target.Conds[0].Info.Trim().Trim('[', ']'));

        // 归属少女保持 Lv1；**另一个**少女拉到远超门槛
        h.Player.Girls.Clear();
        h.Player.Girls.Add(new Girl { Girlid = (uint)ownerNpc, Level = 1 });
        uint other = ownerNpc == 1 ? 2u : 1u;
        h.Player.Girls.Add(new Girl { Girlid = other, Level = (uint)(need + 50) });

        h.RunLoginHooks();

        var state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.False(state.Letters.TryGetValue(ownerNpc, out var l0)
                     && l0!.Topics.ContainsKey(target.Id),
                     $"话题 {target.Id} 归属少女 {ownerNpc} 只有 Lv1（门槛 {need}），" +
                     $"不该因为少女 {other} 等级高就补发");

        // 反向：把归属少女提到门槛，就应该补发
        h.Player.GetGirl((uint)ownerNpc)!.Level = (uint)need;
        h.RunLoginHooks();

        state = PhoneMsgFeature.LoadStateStatic(h.Player);
        Assert.True(state.Letters.TryGetValue(ownerNpc, out var l1)
                    && l1!.Topics.ContainsKey(target.Id),
                    $"归属少女 {ownerNpc} 已达 Lv{need}，话题 {target.Id} 应补发");
    }
}

