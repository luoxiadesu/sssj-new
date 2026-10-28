using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 一封邮件的服务端状态（落在 <c>Player.Extra["mail"]</c>）。
/// 字段与 <c>gsproto.CMailInfo</c> 一一对应，见 <see cref="MailFeature"/> 的取证段。
/// </summary>
public sealed class MailEntry
{
    /// <summary>邮件 id（<c>CMailInfo.id</c> #1）。玩家内唯一，是三条上行的引用键。</summary>
    public int Id { get; set; }

    /// <summary>已读标记（<c>readflag</c> #2）。0 未读 / 非 0 已读。</summary>
    public int ReadFlag { get; set; }

    /// <summary>
    /// 颜色/类型（<c>color</c> #3）。★ 语义**未取证** —— 客户端读它做什么没查到，
    /// 一律给 0（required，必须发，见 <see cref="MailFeature"/> §2）。
    /// </summary>
    public int Color { get; set; }

    /// <summary>收件时间（<c>recvtime</c> #4，unix 秒）。</summary>
    public long RecvTime { get; set; }

    /// <summary>标题（<c>title</c> #5）。</summary>
    public string Title { get; set; } = "";

    /// <summary>发件人（<c>sender</c> #6）。</summary>
    public string Sender { get; set; } = "";

    /// <summary>正文（<c>content</c> #7）。</summary>
    public string Content { get; set; } = "";

    /// <summary>
    /// 过期时间（<c>expirytime</c> #9，unix 秒）。**不是 required**（掩码 0x7F 只到 #7）。
    /// 0 表示不过期。
    /// </summary>
    public long ExpiryTime { get; set; }

    /// <summary>附件（<c>itemlist</c> #8），每项 <c>(G,D,P,L,Count)</c>。可空。</summary>
    public List<int[]> Items { get; set; } = new();

    /// <summary>附件是否已被领取（服务端侧标记，协议里没有对应字段）。</summary>
    public bool Taken { get; set; }
}

/// <summary>
/// 邮件域：**终端界面（<c>UI_Phone</c>）的 <c>TYPE_EMAIL = 2</c> 页签**。
/// pf 1014/1015 下行 + 1016/1017、1018/1019、1020/1021 三对收发。
///
/// 取证见 <c>work/mail-domain-forensics.md</c>；协议字段见 <c>docs/proto-schema.json</c>。
///
/// <para><b>★★★ 1. 列表是「服务端主动推」，客户端从不索要</b></para>
/// <c>GetMailListReq</c>（pf 1013 侧）在 <c>docs/proto-schema.json</c> 里是**零字段**消息，
/// 而全客户端**没有任何地方发它** —— 实测「打开终端邮件页签，服务端收到零请求」，
/// 界面显示「当前没有相关内容」、零 Lua 报错、零 unhandled。
/// ⇒ 服务端必须在**登录时主动推 pf 1014**，否则邮件页签永远是空的。
/// 这也是为什么这个域「不实现就完全看不出问题」——它不会报错，只是永远空白。
///
/// <para><b>★★ 2. <c>CMailInfo</c> 的 required 掩码是 <c>0x7F</c> —— 字段 1..7 全部必填</b></para>
/// <c>gsproto.CMailInfo$$IsInitialized</c>（<c>0x33D891C</c>）：
/// <code>
/// 0x033D891C  mov  w9, #0x7f      ; 掩码 = 字段 #1..#7
/// 0x033D8920  bics wzr, w9, w8
/// </code>
/// ⇒ <c>id / readflag / color / recvtime / title / sender / content</c> **一个都不能少**，
/// 漏任意一个 ⇒ 客户端 <b>整包 Parse 失败</b>（不是那一封显示异常，是整个 1014 全丢）。
/// ★ 特别注意 <c>color</c>(#3)：语义未取证，但**必须发**（给 0）；
/// 三个字符串即使为空也要显式设成 <c>""</c> 而不是不设。
/// <c>itemlist</c>(#8) 与 <c>expirytime</c>(#9) 在掩码之外 ⇒ 可省。
///
/// <c>CMailItem</c> 掩码是 <c>0xF</c>（<c>0x33D93B8</c>）⇒ <c>g/d/p/l</c> 必填、
/// <c>count</c>(#5) 可选 —— 但业务上必须给，否则客户端按 0 个展示。
///
/// <para><b>★★ 3. 三个 Rsp 的 required 是<u>另一种</u>模式：只认字段 #2</b></para>
/// <c>GetMailItemsRsp$$IsInitialized</c>（<c>0x31F0B44</c>）与
/// <c>SetReadFlagRsp$$IsInitialized</c>（<c>0x338A8C0</c>）都是：
/// <code>
/// ldrb w8, [x0, #0x14] ; ubfx w0, w8, #1, #1 ; ret     ← 只取 has 位图的 bit 1
/// </code>
/// ⇒ **<c>autodel</c>(#2) 才是唯一 required**，`mailidlist`(#1) 反而可省（repeated 不占 has 位）。
/// ⇒ 回这两个包时**必须显式设 <c>Autodel</c>**（哪怕是 0），否则整包 Parse 失败。
/// 而 <c>DelMailRsp</c> / <c>GetMailListRsp</c> / <c>AddMailNtf</c> / <c>CMailList</c> 的
/// <c>IsInitialized</c> 都是 <c>mov w0, #1</c>（恒真）⇒ 无 required。
///
/// <para><b>★ 4. 五个 handler 第一句都是 <c>if (code != 0)</c></b></para>
/// <c>LoadMailInfo 0x1877600</c> / <c>AddMailInfo 0x1877988</c> / <c>GetMailItem 0x1877CC4</c> /
/// <c>SetMailRead 0x18786D4</c> / <c>DelMail 0x1878BF4</c> 都以
/// <c>and w8, w20, #0xffff; cbz w8, ...</c> 开头（w20 = 16 字节包头的 <c>ushort code</c>），
/// 非 0 就 <c>Localization.Get</c> + <c>MessageBox.Simple</c> 弹错并 return。
/// ⇒ 失败时**不用改协议体**，用包头 code 即可让客户端弹提示（<c>AGENTS.md</c> §2.3）。
/// 本实现的失败路径一律「记日志 + 不回包」，因为具体错误码文案未取证。
///
/// <para><b>★★ 5. pf 1014 是<u>全量替换</u>，1015 是<u>单封追加</u></b></para>
/// <c>LoadMailInfo</c> 在 <c>0x1877730</c> 先 <c>_maillist.Clear()</c>（<c>_maillist</c> 在
/// <c>EmailManager</c> +0x10，<c>dump.cs:519278</c>）再逐封填 ⇒ 每次 1014 都是整表替换。
/// 所以「删了一封之后」既可以推 1021 让客户端自己删、也可以重推 1014 全量 —— 本实现用前者
/// （省流量，且客户端 <c>DelMail</c> 本来就按 <c>mailidlist</c> 移除）。
///
/// <para><b>★★ 6. 领附件的奖励展示由<u>客户端</u>按本地缓存拼</b></para>
/// <c>GetMailItem</c>（1017）拿到回包后：按 <c>mailidlist</c> 找到**本地那封邮件**
/// → <c>set_readflag</c>（<c>0x1877F40</c>）→ 读该邮件的 <c>itemlist</c> 的
/// <c>get_g/get_d/get_p/get_l</c>（<c>0x1877FE4..0x187815C</c>）→ <c>String.Format</c> 拼 key
/// → 用 <c>Dictionary</c> 汇总数量展示。
/// ⇒ **1017 回包只需回显 <c>mailidlist</c> + <c>autodel</c>，不带任何奖励内容**；
/// 但这意味着 **1014 里的 <c>itemlist</c> 必须准确** —— 弹窗内容完全来自它。
/// 真正的物品入包仍要服务端走 pf 1031（见 <see cref="GameNotify.PushRewards"/>）。
///
/// <para><b>★ 7. <c>autodel</c> 的语义</b></para>
/// 三个 Req 都带 <c>autodel</c>（<c>ClickGetMailItem</c> 在 <c>0x18790F8 set_autodel</c>、
/// <c>ClickSetMailRead</c> 在 <c>0x1879244</c>）。服务端**原样回显**即可：
/// 客户端拿它决定「领完是否顺带把邮件删掉」。本实现遵从客户端的请求值。
/// </summary>
public sealed class MailFeature : IGameFeature
{
    /// <summary><c>Player.Extra</c> 里的键。</summary>
    public const string ExtraKey = "mail";

    /// <summary>
    /// 邮件保留天数（客户端 <c>UI_Phone.lua:15</c> <c>MAIL_OUT_TIME = 7 * 24 * 3600</c>）。
    /// 服务端用它给新邮件算 <c>expirytime</c>，与客户端的显示口径同源。
    /// </summary>
    public const long MailOutSeconds = 7 * 24 * 3600;

    private GameFeatureContext? _ctx;

    public string Name => "邮件（pf 1014/1015 + 1016..1021）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;

        ctx.MapPf(GamePf.GetMailItems, OnGetItems);
        ctx.MapPf(GamePf.SetReadFlag, OnSetRead);
        ctx.MapPf(GamePf.DelMail, OnDelete);

        // ★ 登录时主动推列表 —— 客户端从不索要（类注释 §1）。
        ctx.OnPlayerLogin((session, player) =>
        {
            var mails = Load(player);
            SeedIfEmpty(player, mails);
            if (EnsureProducerMail(player, mails)) { Save(player, mails); ctx.Store.Save(player); }
            PushList(session, mails);
        });
    }

    internal const string ProducerNotice = "制作人：SteveS，勿擅自转发";
    internal static bool EnsureProducerMail(Player player, List<MailEntry> mails)
    {
        const string flag = "producerNoticeSteveS20261028";
        if (player.Extra.TryGetValue(flag, out var sent) && sent?.GetValue<bool>() == true) return false;
        mails.Add(new MailEntry {
            Id = mails.Count == 0 ? 1 : mails.Max(m => m.Id) + 1,
            RecvTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Title = "制作人声明", Sender = "SteveS", Content = ProducerNotice, ExpiryTime = 0,
        });
        player.Extra[flag] = true;
        return true;
    }

    // =================================================================
    //  下行：pf 1014 全量列表
    // =================================================================

    /// <summary>
    /// 推全量邮件列表。★ 即使**一封都没有也要推**：客户端 `LoadMailInfo` 会
    /// `Clear()` 本地缓存，推空列表能把「上次登录残留」清干净；而且空列表不会卡界面
    /// （`UI_Phone` 自己显示「当前没有相关内容」，实测零报错）。
    /// </summary>
    internal void PushList(ClientSession session, List<MailEntry> mails)
    {
        var list = new CMailList();
        foreach (var m in mails.OrderByDescending(m => m.RecvTime).ThenByDescending(m => m.Id))
            list.Maillist.Add(BuildMailInfo(m));

        // ★★★ flag 的 bit0 = 「先清空客户端本地缓存」，**必须发 1**。
        // 取证（LoadMailInfo 0x1877600）：
        //   0x18776BC has_flag → 0x18776C0 tbz w0,#0,#0x1877734   ← 没有 flag 就跳过
        //   0x18776D8 get_flag → 0x18776DC tbz w0,#0,#0x1877734   ← bit0 为 0 也跳过
        //   0x1877730 _maillist.Clear()                            ← 只有 bit0=1 才执行
        //   0x1877734 是汇合点，之后无条件 get_mail 并**逐封追加**
        // ⇒ 不发 flag ⇒ 客户端**追加而不是替换** ⇒ 同一会话内重复推 1014
        //   （重连、顶号）会把邮件累加成重复项。
        // ★ 「杀进程重启」时本地缓存本来是空的，所以这个 bug 不会在常规
        //   e2e（force-stop + 重登）里暴露 —— 别用那种方式验它。
        // ★★★ 真机 A/B 对照实测（同一会话内用 `gmsrv.sh maillist` 重推 1014）：
        //     不发 flag  →  客户端邮件数 1 → **2**（累积！）
        //     发 flag=1  →  客户端邮件数 1 → **1**（正确替换）
        //   这是一次干净的对照实验，可复现：临时去掉 Flag 赋值、重启、重登、
        //   `tools/gmsrv.sh maillist`，再用 GetAllEmail 读 Count。
        // ★ bit1 与它无关：0x18776F0 tbz 读的是 EmailManager 的静态字段 +0x127，
        //   不是 flag 的位；那一支语义未取证，给 1 即可，不要给 3。
        var rsp = new GetMailListRsp { Mail = list, Flag = MailListFlagClear };
        session.Push(GamePf.GetMailListRsp, rsp);

        Log.Info($"[session {session.Id}] 推邮件列表 pf {GamePf.GetMailListRsp}：{mails.Count} 封" +
                 $"（未读 {mails.Count(m => m.ReadFlag == 0)}、带附件未领 {mails.Count(m => !m.Taken && m.Items.Count > 0)}）");
    }

    /// <summary>
    /// <see cref="MailEntry"/> → <c>CMailInfo</c>。
    /// ★★ 字段 #1..#7 全部显式设值（required 掩码 <c>0x7F</c>，见类注释 §2）——
    /// 三个字符串即使为空也要设 <c>""</c>，<c>color</c> 语义未取证但必须发。
    /// </summary>
    internal static CMailInfo BuildMailInfo(MailEntry m)
    {
        var info = new CMailInfo
        {
            Id = m.Id,
            Readflag = m.ReadFlag,
            Color = m.Color,
            Recvtime = m.RecvTime,
            Title = m.Title ?? "",
            Sender = m.Sender ?? "",
            Content = m.Content ?? "",
        };
        if (m.ExpiryTime > 0)
            info.Expirytime = m.ExpiryTime;

        // ★ 已领过的邮件不再下发附件：否则客户端界面还会显示「可领取」，
        //   玩家再点一次，服务端只能拒绝 —— 表现成「点了没反应」。
        if (!m.Taken)
        {
            foreach (var t in m.Items)
            {
                if (t is null || t.Length < 4)
                    continue;
                info.Itemlist.Add(new CMailItem
                {
                    G = (uint)t[0],
                    D = (uint)t[1],
                    P = (uint)t[2],
                    L = (uint)t[3],
                    Count = (uint)(t.Length > 4 ? t[4] : 1),
                });
            }
        }
        return info;
    }

    // =================================================================
    //  上行 pf 1016：领附件
    // =================================================================

    private Task OnGetItems(ClientSession session, Packet packet)
    {
        var req = TryParse<GetMailItemsReq>(session, packet, GamePf.GetMailItems);
        if (req is null)
            return Task.CompletedTask;

        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] pf {GamePf.GetMailItems} 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var mails = Load(player);
        var ids = req.Mailidlist.Distinct().ToList();
        var granted = new RewardGrantResult();
        var okIds = new List<int>();
        var rejected = new List<string>();

        foreach (int id in ids)
        {
            var m = mails.FirstOrDefault(x => x.Id == id);
            if (m is null)
            {
                rejected.Add($"{id}(不存在)");
                continue;
            }
            if (m.Taken)
            {
                rejected.Add($"{id}(已领过)");
                continue;
            }
            if (m.Items.Count == 0)
            {
                rejected.Add($"{id}(无附件)");
                continue;
            }

            _ctx?.Rewards.Grant(player, m.Items, RewardSource.Extra, granted);
            m.Taken = true;
            m.ReadFlag = 1;                 // 领取即已读（客户端 0x1877F40 也会本地置上）
            okIds.Add(id);
        }

        if (okIds.Count == 0)
        {
            Log.Warn($"[session {session.Id}] pf {GamePf.GetMailItems} 没有可领取的邮件" +
                     $"（{string.Join(',', rejected)}）—— 不回包");
            return Task.CompletedTask;
        }

        // autodel：客户端自己决定领完是否删（原样遵从）
        bool autodel = req.Autodel != 0;
        if (autodel)
            mails.RemoveAll(x => okIds.Contains(x.Id));

        Save(player, mails);
        _ctx?.Store.Save(player);

        Log.Info($"[session {session.Id}] 邮件领附件：{okIds.Count} 封（id {string.Join(',', okIds)}）" +
                 $"、发出 {granted.Rewards.Count} 项奖励、autodel={req.Autodel}" +
                 $"{(rejected.Count > 0 ? $"；拒绝 {rejected.Count} 封（{string.Join(',', rejected)}）" : "")}");

        // ★ 先把物品真正推进背包（pf 1031/1032），再回执 —— 与养成域同一条时序规矩
        GameNotify.PushRewards(session, player, granted);

        // ★★ 回包必须显式设 Autodel（唯一 required，类注释 §3）
        var rsp = new GetMailItemsRsp { Autodel = req.Autodel };
        foreach (int id in okIds)
            rsp.Mailidlist.Add(id);
        session.Push(GamePf.GetMailItemsRsp, rsp);
        return Task.CompletedTask;
    }

    // =================================================================
    //  上行 pf 1018：标已读
    // =================================================================

    private Task OnSetRead(ClientSession session, Packet packet)
    {
        var req = TryParse<SetReadFlagReq>(session, packet, GamePf.SetReadFlag);
        if (req is null)
            return Task.CompletedTask;

        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] pf {GamePf.SetReadFlag} 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var mails = Load(player);
        var okIds = new List<int>();
        foreach (int id in req.Mailidlist.Distinct())
        {
            var m = mails.FirstOrDefault(x => x.Id == id);
            if (m is null)
                continue;
            m.ReadFlag = 1;
            okIds.Add(id);
        }

        if (okIds.Count == 0)
        {
            Log.Warn($"[session {session.Id}] pf {GamePf.SetReadFlag} 没有匹配的邮件 id" +
                     $"（{string.Join(',', req.Mailidlist)}）—— 不回包");
            return Task.CompletedTask;
        }

        if (req.Autodel != 0)
            mails.RemoveAll(x => okIds.Contains(x.Id));

        Save(player, mails);
        _ctx?.Store.Save(player);

        Log.Info($"[session {session.Id}] 邮件标已读：{okIds.Count} 封（id {string.Join(',', okIds)}）" +
                 $"、autodel={req.Autodel}，剩余未读 {mails.Count(m => m.ReadFlag == 0)}");

        var rsp = new SetReadFlagRsp { Autodel = req.Autodel };   // ★ required
        foreach (int id in okIds)
            rsp.Mailidlist.Add(id);
        session.Push(GamePf.SetReadFlagRsp, rsp);
        return Task.CompletedTask;
    }

    // =================================================================
    //  上行 pf 1020：删除
    // =================================================================

    private Task OnDelete(ClientSession session, Packet packet)
    {
        var req = TryParse<DelMailReq>(session, packet, GamePf.DelMail);
        if (req is null)
            return Task.CompletedTask;

        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] pf {GamePf.DelMail} 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        var mails = Load(player);
        var okIds = new List<int>();
        var rejected = new List<string>();

        foreach (int id in req.Mailidlist.Distinct())
        {
            var m = mails.FirstOrDefault(x => x.Id == id);
            if (m is null)
            {
                rejected.Add($"{id}(不存在)");
                continue;
            }
            // ★ 附件未领的邮件不允许删 —— 否则玩家一键删除会把没领的奖励一起丢掉。
            //   客户端界面本来也不给删（UI_Phone 的删除按钮对有附件的邮件是禁用的，
            //   具体控件条件未取证）⇒ 服务端把关。
            if (!m.Taken && m.Items.Count > 0)
            {
                rejected.Add($"{id}(附件未领)");
                continue;
            }
            mails.Remove(m);
            okIds.Add(id);
        }

        if (okIds.Count == 0)
        {
            Log.Warn($"[session {session.Id}] pf {GamePf.DelMail} 没有可删除的邮件" +
                     $"（{string.Join(',', rejected)}）—— 不回包");
            return Task.CompletedTask;
        }

        Save(player, mails);
        _ctx?.Store.Save(player);

        Log.Info($"[session {session.Id}] 邮件删除：{okIds.Count} 封（id {string.Join(',', okIds)}）" +
                 $"，剩余 {mails.Count} 封" +
                 $"{(rejected.Count > 0 ? $"；拒绝 {rejected.Count} 封（{string.Join(',', rejected)}）" : "")}");

        // DelMailRsp 的 IsInitialized 恒真（0x31EA790 mov w0,#1）⇒ 无 required
        var rsp = new DelMailRsp();
        foreach (int id in okIds)
            rsp.Mailidlist.Add(id);
        session.Push(GamePf.DelMailRsp, rsp);
        return Task.CompletedTask;
    }

    // =================================================================
    //  新邮件推送（供别的功能域调用）
    // =================================================================

    /// <summary>
    /// 给玩家发一封新邮件：写入存档并推 pf 1015（单封追加）。
    /// 供别的功能域用（例如「活动奖励补发」「GM 发件」）。
    /// </summary>
    public void SendMail(ClientSession? session, Player player, string title, string sender,
                         string content, IEnumerable<int[]>? items = null, long expirySeconds = MailOutSeconds)
    {
        var mails = Load(player);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var m = new MailEntry
        {
            Id = NextId(mails),
            ReadFlag = 0,
            Color = 0,
            RecvTime = now,
            Title = title ?? "",
            Sender = sender ?? "",
            Content = content ?? "",
            ExpiryTime = expirySeconds > 0 ? now + expirySeconds : 0,
            Items = items?.Where(t => t is not null && t.Length >= 4).Select(t => t.ToArray()).ToList() ?? new(),
        };
        mails.Add(m);
        Save(player, mails);
        _ctx?.Store.Save(player);

        Log.Info($"[mail] 新邮件 id={m.Id} → {player.Name}：《{m.Title}》" +
                 $"（发件人 {m.Sender}、附件 {m.Items.Count} 项）");

        if (session is not null)
            session.Push(GamePf.AddMailNtf, new AddMailNtf { Mail = BuildMailInfo(m) });
    }

    /// <summary>下一个可用邮件 id（玩家内自增，从 1 起）。</summary>
    internal static int NextId(List<MailEntry> mails)
        => mails.Count == 0 ? 1 : mails.Max(m => m.Id) + 1;

    // =================================================================
    //  新号种子邮件
    // =================================================================

    /// <summary>
    /// 新号（邮箱为空）时塞一封欢迎邮件 —— 否则邮件页签永远空白，
    /// 既看不出「域已实现」，也没法验收领取/删除链路。
    /// ★ 只在**一封都没有**时补，且补过之后玩家删掉就不再补（靠 <see cref="SeedFlagKey"/> 标记），
    ///   否则「删了又出现」会像 bug。
    /// </summary>
    internal void SeedIfEmpty(Player player, List<MailEntry> mails)
    {
        if (mails.Count > 0)
            return;
        var state = player.Extra.TryGetValue(ExtraKey, out var node) ? node as JsonObject : null;
        if (state?[SeedFlagKey]?.GetValue<bool>() == true)
            return;          // 发过了，玩家自己删的，不要再塞

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        mails.Add(new MailEntry
        {
            Id = 1,
            ReadFlag = 0,
            Color = 0,
            RecvTime = now,
            Title = "欢迎回到双生视界",
            Sender = "运营团队",
            Content = "指挥官，欢迎回来！\n这里是本地服务端的邮件系统。\n" +
                      "附件是一点点启动资源，点「领取」即可放入背包。",
            ExpiryTime = 0,                      // 种子邮件不过期
            Items = new List<int[]>
            {
                new[] { 15, 1, 1, 1, 10000 },    // 金币
                new[] { 15, 2, 1, 1, 300 },      // 钻石类货币
            },
        });
        Save(player, mails, seeded: true);
        _ctx?.Store.Save(player);
        Log.Info($"[mail] 新号 {player.Name} 邮箱为空，补一封欢迎邮件（含 2 项附件）");
    }

    /// <summary>
    /// <c>GetMailListRsp.flag</c> 的取值：**bit0 = 先清空客户端本地缓存**。
    /// 见 <see cref="PushList"/> 里的逐行取证；不发它会让客户端「追加而非替换」。
    /// </summary>
    public const int MailListFlagClear = 1;

    /// <summary>「已经发过种子邮件」的标记键。</summary>
    internal const string SeedFlagKey = "seeded";

    // =================================================================
    //  存档：Player.Extra["mail"]
    // =================================================================

    /// <summary>从 <c>Player.Extra["mail"]</c> 读邮件列表；缺失/损坏时返回空列表（不抛）。</summary>
    internal static List<MailEntry> Load(Player player)
    {
        var result = new List<MailEntry>();
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is not JsonObject obj)
            return result;
        if (obj["list"] is not JsonArray arr)
            return result;

        foreach (var el in arr)
        {
            if (el is not JsonObject e)
                continue;
            var m = new MailEntry
            {
                Id = ReadInt(e["id"]),
                ReadFlag = ReadInt(e["read"]),
                Color = ReadInt(e["color"]),
                RecvTime = ReadLong(e["time"]),
                Title = e["title"]?.ToString() ?? "",
                Sender = e["sender"]?.ToString() ?? "",
                Content = e["content"]?.ToString() ?? "",
                ExpiryTime = ReadLong(e["expiry"]),
                Taken = ReadInt(e["taken"]) != 0,
            };
            if (e["items"] is JsonArray items)
            {
                foreach (var it in items)
                {
                    if (it is not JsonArray t || t.Count < 4)
                        continue;
                    m.Items.Add(new[]
                    {
                        ReadInt(t[0]), ReadInt(t[1]), ReadInt(t[2]), ReadInt(t[3]),
                        t.Count > 4 ? ReadInt(t[4]) : 1,
                    });
                }
            }
            if (m.Id > 0)
                result.Add(m);
        }
        return result;
    }

    /// <summary>写回 <c>Player.Extra["mail"]</c>。</summary>
    internal static void Save(Player player, List<MailEntry> mails, bool? seeded = null)
    {
        var old = player.Extra.TryGetValue(ExtraKey, out var node) ? node as JsonObject : null;
        bool seedFlag = seeded ?? (old?[SeedFlagKey]?.GetValue<bool>() ?? false);

        var arr = new JsonArray();
        foreach (var m in mails)
        {
            var items = new JsonArray();
            foreach (var t in m.Items)
                items.Add(new JsonArray { t[0], t[1], t[2], t[3], t.Length > 4 ? t[4] : 1 });

            arr.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["read"] = m.ReadFlag,
                ["color"] = m.Color,
                ["time"] = m.RecvTime,
                ["title"] = m.Title,
                ["sender"] = m.Sender,
                ["content"] = m.Content,
                ["expiry"] = m.ExpiryTime,
                ["taken"] = m.Taken ? 1 : 0,
                ["items"] = items,
            });
        }

        player.Extra[ExtraKey] = new JsonObject
        {
            ["list"] = arr,
            [SeedFlagKey] = seedFlag,
        };
    }

    /// <summary>解上行 payload；失败记日志并返回 null（不抛，不回包）。</summary>
    private static T? TryParse<T>(ClientSession session, Packet packet, int pf)
        where T : SssjServer.Protocol.ProtoMessage, new()
    {
        try
        {
            return SssjServer.Protocol.ProtoMessage.Parse<T>(packet.Payload);
        }
        catch (Exception e)
        {
            Log.Warn($"[session {session.Id}] 解析 pf {pf} 的 {typeof(T).Name} 失败：{e.Message}");
            return null;
        }
    }

    private static int ReadInt(JsonNode? n)
    {
        if (n is null)
            return 0;
        try { return n.GetValue<int>(); }
        catch (Exception) { return int.TryParse(n.ToString(), out int v) ? v : 0; }
    }

    private static long ReadLong(JsonNode? n)
    {
        if (n is null)
            return 0;
        try { return n.GetValue<long>(); }
        catch (Exception) { return long.TryParse(n.ToString(), out long v) ? v : 0; }
    }
}
