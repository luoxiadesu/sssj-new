using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 邮件域（pf 1014/1015 + 1016..1021）测试。取证见 <c>work/mail-domain-forensics.md</c>
/// 与 <see cref="MailFeature"/> 的类注释。
///
/// ★ 本域最关键的断言是 **required 字段** ——
/// <c>CMailInfo</c> 的 required 掩码是 <c>0x7F</c>（字段 1..7 全必填，
/// <c>IsInitialized</c> RVA <c>0x33D891C</c> 的 <c>mov w9, #0x7f</c>），
/// 漏任意一个会让客户端**整包 Parse 失败**（不是单封异常，是整个 1014 全丢）。
/// 所以这里用「序列化 → 反序列化 → 逐字段比对」把它钉死。
/// </summary>
public class MailFeatureTests
{
    [Fact]
    public void ProducerMailPersistsOnceAndDoesNotReturnAfterDeletion()
    {
        var p = new Player("producer-test", 1);
        var mails = new List<MailEntry> { Sample(12) };
        Assert.True(MailFeature.EnsureProducerMail(p, mails));
        Assert.Equal(13, mails.Last().Id);
        Assert.Equal("制作人：SteveS，勿擅自转发", mails.Last().Content);
        Assert.Empty(mails.Last().Items);
        MailFeature.Save(p, mails);
        var loaded = MailFeature.Load(p);
        Assert.False(MailFeature.EnsureProducerMail(p, loaded));
        loaded.RemoveAll(m => m.Sender == "SteveS");
        MailFeature.Save(p, loaded);
        Assert.False(MailFeature.EnsureProducerMail(p, MailFeature.Load(p)));
        Assert.Single(MailFeature.Load(p));
    }
    private static MailEntry Sample(int id = 7, bool withItems = true, bool taken = false) => new()
    {
        Id = id,
        ReadFlag = 0,
        Color = 0,
        RecvTime = 1_700_000_000,
        Title = "标题",
        Sender = "发件人",
        Content = "正文",
        ExpiryTime = 1_700_600_000,
        Taken = taken,
        Items = withItems
            ? new List<int[]> { new[] { 15, 1, 1, 1, 10000 }, new[] { 7, 1, 4, 4, 3 } }
            : new List<int[]>(),
    };

    // =================================================================
    //  一、required 字段（本域最高危点）
    // =================================================================

    /// <summary>
    /// ★★★ <c>CMailInfo</c> 字段 #1..#7 **全部**要被写出来（required 掩码 <c>0x7F</c>）。
    /// 判据方式：序列化再解回来，逐个 <c>Has*</c> 断言 —— 只要 <c>BuildMailInfo</c>
    /// 漏设任何一个，这里就会红。
    /// </summary>
    [Fact]
    public void 必填字段_CMailInfo的七个字段全部写出()
    {
        var info = MailFeature.BuildMailInfo(Sample());
        var back = ProtoMessage.Parse<CMailInfo>(info.ToByteArray());

        Assert.True(back.HasId, "#1 id 未写出");
        Assert.True(back.HasReadflag, "#2 readflag 未写出");
        Assert.True(back.HasColor, "#3 color 未写出（语义未取证但必须发）");
        Assert.True(back.HasRecvtime, "#4 recvtime 未写出");
        Assert.True(back.HasTitle, "#5 title 未写出");
        Assert.True(back.HasSender, "#6 sender 未写出");
        Assert.True(back.HasContent, "#7 content 未写出");
    }

    /// <summary>
    /// ★ 三个字符串**为空时也必须写出**（`""` 而不是「不设」）——
    /// 它们在 required 掩码里，不设就整包 Parse 失败。
    /// </summary>
    [Fact]
    public void 必填字段_空字符串也要写出()
    {
        var m = Sample();
        m.Title = "";
        m.Sender = "";
        m.Content = "";

        var back = ProtoMessage.Parse<CMailInfo>(MailFeature.BuildMailInfo(m).ToByteArray());

        Assert.True(back.HasTitle);
        Assert.True(back.HasSender);
        Assert.True(back.HasContent);
        Assert.Equal("", back.Title);
    }

    /// <summary>null 字符串要被兜成 `""`，不能让它变成「未设」或抛。</summary>
    [Fact]
    public void 必填字段_null字符串兜成空串()
    {
        var m = Sample();
        m.Title = null!;
        m.Sender = null!;
        m.Content = null!;

        var back = ProtoMessage.Parse<CMailInfo>(MailFeature.BuildMailInfo(m).ToByteArray());

        Assert.True(back.HasTitle);
        Assert.Equal("", back.Title);
        Assert.Equal("", back.Sender);
        Assert.Equal("", back.Content);
    }

    /// <summary>字段值本身要正确往返（不只是「有写」）。</summary>
    [Fact]
    public void 字段往返_数值与文本一致()
    {
        var m = Sample(id: 42);
        var back = ProtoMessage.Parse<CMailInfo>(MailFeature.BuildMailInfo(m).ToByteArray());

        Assert.Equal(42, back.Id);
        Assert.Equal(m.RecvTime, back.Recvtime);
        Assert.Equal("标题", back.Title);
        Assert.Equal("发件人", back.Sender);
        Assert.Equal("正文", back.Content);
        Assert.Equal(m.ExpiryTime, back.Expirytime);
    }

    /// <summary>
    /// 附件 <c>CMailItem</c> 的 <c>g/d/p/l</c> 必填（掩码 <c>0xF</c>，RVA <c>0x33D93B8</c>），
    /// <c>count</c> 业务上也必须给。
    /// </summary>
    [Fact]
    public void 必填字段_附件四元组与数量()
    {
        var back = ProtoMessage.Parse<CMailInfo>(MailFeature.BuildMailInfo(Sample()).ToByteArray());

        Assert.Equal(2, back.Itemlist.Count);
        var first = back.Itemlist[0];
        Assert.True(first.HasG);
        Assert.True(first.HasD);
        Assert.True(first.HasP);
        Assert.True(first.HasL);
        Assert.True(first.HasCount);
        Assert.Equal(15u, first.G);
        Assert.Equal(10000u, first.Count);
    }

    /// <summary>
    /// ★ 已领过的邮件**不再下发附件** —— 否则界面还显示「可领取」，
    /// 玩家再点服务端只能拒绝，表现成「点了没反应」。
    /// </summary>
    [Fact]
    public void 已领取的邮件不再下发附件()
    {
        var back = ProtoMessage.Parse<CMailInfo>(
            MailFeature.BuildMailInfo(Sample(taken: true)).ToByteArray());
        Assert.Empty(back.Itemlist);
    }

    /// <summary>无附件的邮件：`itemlist` 空（它不在 required 掩码里，省掉是合法的）。</summary>
    [Fact]
    public void 无附件邮件_itemlist为空()
    {
        var back = ProtoMessage.Parse<CMailInfo>(
            MailFeature.BuildMailInfo(Sample(withItems: false)).ToByteArray());
        Assert.Empty(back.Itemlist);
        Assert.True(back.HasId);          // 但必填字段仍在
    }

    /// <summary>`expirytime = 0`（不过期）时不写该字段 —— 它不是 required，省掉合法。</summary>
    [Fact]
    public void 不过期的邮件_不写expirytime()
    {
        var m = Sample();
        m.ExpiryTime = 0;
        var back = ProtoMessage.Parse<CMailInfo>(MailFeature.BuildMailInfo(m).ToByteArray());
        Assert.False(back.HasExpirytime);
        Assert.True(back.HasId);
    }

    /// <summary>脏附件（长度不足 4）要被丢弃而不是抛或写出残缺项。</summary>
    [Fact]
    public void 脏附件被丢弃()
    {
        var m = Sample(withItems: false);
        m.Items.Add(new[] { 15, 1 });            // 段数不足
        m.Items.Add(null!);                       // null
        m.Items.Add(new[] { 15, 1, 1, 1 });      // 只有四元组，count 应兜 1

        var back = ProtoMessage.Parse<CMailInfo>(MailFeature.BuildMailInfo(m).ToByteArray());

        Assert.Single(back.Itemlist);
        Assert.Equal(1u, back.Itemlist[0].Count);
    }

    /// <summary>
    /// ★★★ <c>GetMailListRsp.flag</c> 的 <b>bit0 = 「先清空客户端本地缓存」</b>，必须发。
    ///
    /// 取证（<c>LoadMailInfo</c> RVA <c>0x1877600</c>）：
    /// <code>
    /// 0x18776BC has_flag → 0x18776C0 tbz w0,#0,#0x1877734   ← 没 flag 就跳过
    /// 0x18776D8 get_flag → 0x18776DC tbz w0,#0,#0x1877734   ← bit0=0 也跳过
    /// 0x1877730 _maillist.Clear()                            ← 只有 bit0=1 才执行
    /// 0x1877734 汇合点，之后无条件 get_mail 并逐封**追加**
    /// </code>
    /// ⇒ 不发 flag ⇒ 客户端**追加而非替换** ⇒ 同一会话重复推 1014（重连/顶号）
    /// 会把邮件累积成重复项。
    ///
    /// ★★ 这个 bug **用常规 e2e 验不出来**：`force-stop` + 重登时客户端缓存本来是空的，
    /// 追加和替换看不出区别（实测确实显示正常）。所以必须靠这条单测守住。
    /// </summary>
    [Fact]
    public async Task 必填字段_1014必须带flag清缓存位()
    {
        using var h = await FeatureHarness.CreateAsync("mail-flag");
        h.RunLoginHooks();
        Assert.True(await h.WaitForPfAsync(GamePf.GetMailListRsp));

        // 直接验构造：flag 必须带且 bit0 = 1
        var rsp = new GetMailListRsp { Mail = new CMailList(), Flag = MailFeature.MailListFlagClear };
        var back = ProtoMessage.Parse<GetMailListRsp>(rsp.ToByteArray());

        Assert.True(back.HasFlag, "1014 缺 flag ⇒ 客户端不清缓存，重复推会累积重复邮件");
        Assert.Equal(1, back.Flag & 1);
        // ★ 不要给 3：bit1 那一支（0x18776F0 tbz w8,#1）读的是 EmailManager 的
        //   静态字段 +0x127，不是 flag 的位，语义未取证。
        Assert.Equal(1, MailFeature.MailListFlagClear);
    }

    // =================================================================
    //  二、三个 Rsp 的「另一种」required 模式
    // =================================================================

    /// <summary>
    /// ★★ <c>GetMailItemsRsp</c> / <c>SetReadFlagRsp</c> 的 <c>IsInitialized</c> 是
    /// <c>ubfx w0, w8, #1, #1</c>（RVA <c>0x31F0B44</c> / <c>0x338A8C0</c>）
    /// ⇒ **<c>autodel</c>(#2) 才是唯一 required**，必须显式设（哪怕 0）。
    /// </summary>
    [Fact]
    public void 必填字段_两个Rsp必须带autodel()
    {
        var a = new GetMailItemsRsp { Autodel = 0 };
        a.Mailidlist.Add(1);
        Assert.True(ProtoMessage.Parse<GetMailItemsRsp>(a.ToByteArray()).HasAutodel,
            "GetMailItemsRsp 缺 autodel ⇒ 客户端整包 Parse 失败");

        var b = new SetReadFlagRsp { Autodel = 1 };
        b.Mailidlist.Add(2);
        Assert.True(ProtoMessage.Parse<SetReadFlagRsp>(b.ToByteArray()).HasAutodel,
            "SetReadFlagRsp 缺 autodel ⇒ 客户端整包 Parse 失败");
    }

    /// <summary><c>mailidlist</c> 要能正确往返（客户端靠它定位本地邮件）。</summary>
    [Fact]
    public void 回执_mailidlist往返()
    {
        var rsp = new GetMailItemsRsp { Autodel = 0 };
        rsp.Mailidlist.Add(3);
        rsp.Mailidlist.Add(9);

        var back = ProtoMessage.Parse<GetMailItemsRsp>(rsp.ToByteArray());
        Assert.Equal(new[] { 3, 9 }, back.Mailidlist);
    }

    // =================================================================
    //  三、存档往返
    // =================================================================

    /// <summary>邮件写进 <c>Player.Extra["mail"]</c> 再读回来要逐字段一致。</summary>
    [Fact]
    public void 存档_往返一致()
    {
        var p = new Player("t", 1);
        var src = new List<MailEntry> { Sample(id: 1), Sample(id: 2, withItems: false, taken: true) };
        MailFeature.Save(p, src);

        var back = MailFeature.Load(p);
        Assert.Equal(2, back.Count);

        var a = back.First(m => m.Id == 1);
        Assert.Equal("标题", a.Title);
        Assert.Equal("发件人", a.Sender);
        Assert.Equal("正文", a.Content);
        Assert.Equal(1_700_000_000, a.RecvTime);
        Assert.Equal(2, a.Items.Count);
        Assert.Equal(new[] { 15, 1, 1, 1, 10000 }, a.Items[0]);
        Assert.False(a.Taken);

        var b = back.First(m => m.Id == 2);
        Assert.True(b.Taken);
        Assert.Empty(b.Items);
    }

    /// <summary>没有 <c>Extra["mail"]</c>（新号）时读出空列表，不抛。</summary>
    [Fact]
    public void 存档_缺失时返回空列表()
    {
        Assert.Empty(MailFeature.Load(new Player("t", 1)));
    }

    /// <summary>存档损坏（类型不对）时安全降级成空列表，不抛。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"list\":{}}")]
    [InlineData("{\"list\":[1,2,3]}")]
    [InlineData("{\"list\":[{\"id\":0}]}")]
    public void 存档_损坏时安全降级(string json)
    {
        var p = new Player("t", 1);
        p.Extra[MailFeature.ExtraKey] = System.Text.Json.Nodes.JsonNode.Parse(json);
        Assert.Empty(MailFeature.Load(p));      // 不抛、不产出非法条目
    }

    /// <summary>邮件 id 自增：空列表从 1 起，非空取 max+1。</summary>
    [Fact]
    public void 邮件id自增()
    {
        Assert.Equal(1, MailFeature.NextId(new List<MailEntry>()));
        Assert.Equal(8, MailFeature.NextId(new List<MailEntry> { Sample(id: 7), Sample(id: 3) }));
    }

    /// <summary>
    /// ★ 种子邮件的「发过就不再发」标记要能持久化 ——
    /// 否则玩家删掉欢迎邮件后每次登录都会重新出现，像 bug。
    /// </summary>
    [Fact]
    public void 存档_种子标记持久化()
    {
        var p = new Player("t", 1);
        MailFeature.Save(p, new List<MailEntry>(), seeded: true);

        var state = p.Extra[MailFeature.ExtraKey] as System.Text.Json.Nodes.JsonObject;
        Assert.NotNull(state);
        Assert.True(state![MailFeature.SeedFlagKey]!.GetValue<bool>());

        // 再存一次（不传 seeded）应保留原标记
        MailFeature.Save(p, new List<MailEntry>());
        state = p.Extra[MailFeature.ExtraKey] as System.Text.Json.Nodes.JsonObject;
        Assert.True(state![MailFeature.SeedFlagKey]!.GetValue<bool>());
    }

    // =================================================================
    //  四、三条上行链路（端到端）
    // =================================================================

    /// <summary>取一封带附件、未领取的邮件（新号种子邮件）。</summary>
    private static MailEntry FirstUnclaimed(FeatureHarness h)
        => MailFeature.Load(h.Player).First(m => !m.Taken && m.Items.Count > 0);

    /// <summary>
    /// pf 1016 领附件：物品真正进背包（走 pf 1031）、邮件被标记已领+已读、回执带 autodel。
    /// </summary>
    [Fact]
    public async Task 上行_领附件闭环()
    {
        using var h = await FeatureHarness.CreateAsync("mail-get");
        h.RunLoginHooks();
        var mail = FirstUnclaimed(h);
        int goldBefore = h.Player.GetMoney(2);

        var req = new GetMailItemsReq { Autodel = 0 };
        req.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.GetMailItems, req);

        // 服务端状态：已领 + 已读
        var after = MailFeature.Load(h.Player).First(m => m.Id == mail.Id);
        Assert.True(after.Taken, "领取后必须标记 Taken，否则能重复领");
        Assert.NotEqual(0, after.ReadFlag);

        // 奖励真的发了（种子邮件第一项是金币 15-1-1-1）
        Assert.True(h.Player.GetMoney(2) > goldBefore, "附件里的金币没有进账");

        Assert.True(await h.WaitForPfAsync(GamePf.GetMailItemsRsp), "没有回 pf 1017");
    }

    /// <summary>★ 同一封邮件不能重复领（第二次应被拒、不回包、不再发奖）。</summary>
    [Fact]
    public async Task 上行_不能重复领附件()
    {
        using var h = await FeatureHarness.CreateAsync("mail-get2");
        h.RunLoginHooks();
        var mail = FirstUnclaimed(h);

        var req = new GetMailItemsReq { Autodel = 0 };
        req.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.GetMailItems, req);
        int goldAfterFirst = h.Player.GetMoney(2);

        await h.DispatchPfAsync(GamePf.GetMailItems, req);      // 再来一次
        Assert.Equal(goldAfterFirst, h.Player.GetMoney(2));     // 没有二次发奖
    }

    /// <summary>`autodel != 0` 时领完顺带删除该邮件（遵从客户端请求）。</summary>
    [Fact]
    public async Task 上行_autodel领完即删()
    {
        using var h = await FeatureHarness.CreateAsync("mail-get3");
        h.RunLoginHooks();
        var mail = FirstUnclaimed(h);

        var req = new GetMailItemsReq { Autodel = 1 };
        req.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.GetMailItems, req);

        Assert.DoesNotContain(MailFeature.Load(h.Player), m => m.Id == mail.Id);
    }

    /// <summary>pf 1018 标已读。</summary>
    [Fact]
    public async Task 上行_标已读()
    {
        using var h = await FeatureHarness.CreateAsync("mail-read");
        h.RunLoginHooks();
        var mail = MailFeature.Load(h.Player).First();
        Assert.Equal(0, mail.ReadFlag);

        var req = new SetReadFlagReq { Autodel = 0 };
        req.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.SetReadFlag, req);

        Assert.NotEqual(0, MailFeature.Load(h.Player).First(m => m.Id == mail.Id).ReadFlag);
        Assert.True(await h.WaitForPfAsync(GamePf.SetReadFlagRsp), "没有回 pf 1019");
    }

    /// <summary>
    /// ★★ pf 1020 删除：**附件未领的邮件不允许删** ——
    /// 否则玩家一键删除会把没领的奖励一起丢掉。
    /// </summary>
    [Fact]
    public async Task 上行_附件未领不允许删()
    {
        using var h = await FeatureHarness.CreateAsync("mail-del");
        h.RunLoginHooks();
        var mail = FirstUnclaimed(h);

        var req = new DelMailReq();
        req.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.DelMail, req);

        Assert.Contains(MailFeature.Load(h.Player), m => m.Id == mail.Id);   // 还在
    }

    /// <summary>领过附件之后就可以删了。</summary>
    [Fact]
    public async Task 上行_领过之后可删()
    {
        using var h = await FeatureHarness.CreateAsync("mail-del2");
        h.RunLoginHooks();
        var mail = FirstUnclaimed(h);

        var get = new GetMailItemsReq { Autodel = 0 };
        get.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.GetMailItems, get);

        var del = new DelMailReq();
        del.Mailidlist.Add(mail.Id);
        await h.DispatchPfAsync(GamePf.DelMail, del);

        Assert.DoesNotContain(MailFeature.Load(h.Player), m => m.Id == mail.Id);
        Assert.True(await h.WaitForPfAsync(GamePf.DelMailRsp), "没有回 pf 1021");
    }

    /// <summary>不存在的邮件 id：三条上行都要安全拒绝、不崩。</summary>
    [Fact]
    public async Task 上行_不存在的id安全拒绝()
    {
        using var h = await FeatureHarness.CreateAsync("mail-bad");
        h.RunLoginHooks();

        var get = new GetMailItemsReq { Autodel = 0 };
        get.Mailidlist.Add(999999);
        await h.DispatchPfAsync(GamePf.GetMailItems, get);

        var read = new SetReadFlagReq { Autodel = 0 };
        read.Mailidlist.Add(999999);
        await h.DispatchPfAsync(GamePf.SetReadFlag, read);

        var del = new DelMailReq();
        del.Mailidlist.Add(999999);
        await h.DispatchPfAsync(GamePf.DelMail, del);
        // 到这里没抛就算过
    }

    /// <summary>空 mailidlist：不崩、不回包。</summary>
    [Fact]
    public async Task 上行_空列表不崩()
    {
        using var h = await FeatureHarness.CreateAsync("mail-empty");
        h.RunLoginHooks();
        await h.DispatchPfAsync(GamePf.GetMailItems, new GetMailItemsReq { Autodel = 0 });
        await h.DispatchPfAsync(GamePf.SetReadFlag, new SetReadFlagReq { Autodel = 0 });
        await h.DispatchPfAsync(GamePf.DelMail, new DelMailReq());
    }

    /// <summary>`SendMail` 发新邮件：写档 + 推 pf 1015。</summary>
    [Fact]
    public async Task 发新邮件_推1015()
    {
        using var h = await FeatureHarness.CreateAsync("mail-send");
        h.RunLoginHooks();
        int before = MailFeature.Load(h.Player).Count;

        var feature = new MailFeature();
        // 未 Install 的实例没有 ctx，SendMail 里对 _ctx 全是 ?. 调用 ⇒ 只测写档与推送
        feature.SendMail(h.Session, h.Player, "测试标题", "测试发件人", "测试正文",
                         new[] { new[] { 15, 1, 1, 1, 500 } });

        var mails = MailFeature.Load(h.Player);
        Assert.Equal(before + 1, mails.Count);
        var m = mails.Last();
        Assert.Equal("测试标题", m.Title);
        Assert.Single(m.Items);
        Assert.True(await h.WaitForPfAsync(GamePf.AddMailNtf), "没有推 pf 1015");
    }

    // =================================================================
    //  五、装配
    // =================================================================

    /// <summary>
    /// 登录时必须**主动推 pf 1014** —— 客户端从不索要列表
    /// （实测打开邮件页签服务端零请求）。不推就永远空白。
    /// </summary>
    [Fact]
    public async Task 装配_登录主动推1014()
    {
        using var h = await FeatureHarness.CreateAsync("mail-login");
        h.RunLoginHooks();          // harness 不走登录流程，需显式触发
        Assert.True(await h.WaitForPfAsync(GamePf.GetMailListRsp),
            "登录时没有推 pf 1014，邮件页签会永远是空的");
    }

    /// <summary>新号会被塞一封欢迎邮件（否则没法验收领取/删除链路）。</summary>
    [Fact]
    public async Task 装配_新号有欢迎邮件()
    {
        using var h = await FeatureHarness.CreateAsync("mail-seed");
        h.RunLoginHooks();
        var mails = MailFeature.Load(h.Player);
        Assert.NotEmpty(mails);
        Assert.All(mails, m =>
        {
            Assert.True(m.Id > 0);
            Assert.NotEqual("", m.Title);
        });
    }
}
