using System.Globalization;
using System.Text;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// **服务端侧** GM 控制台：改服务端玩家数据并把变更推给在线客户端。
///
/// <para>与 <see cref="GmConsoleFeature"/> 的分工</para>
/// <list type="bullet">
/// <item><c>work/gm.lua</c>（<see cref="GmConsoleFeature"/>）→ 在**客户端**执行 Lua，读客户端状态、驱动 UI。</item>
/// <item><c>work/gmsrv.txt</c>（本类）→ 改**服务端**存档（关卡记录 / 货币 / 背包 / 等级），
///   顺带按正规通道（pf 1026/1029/1031/1032/1034）推给客户端，等价于「原服的 GM 后台」。</item>
/// </list>
///
/// <para>为什么需要它</para>
/// 主城几乎所有功能（活动/终端/社团/角色/Cafe/补给/商店/编队/仓库…）都由
/// <c>SystemUnlock</c> 按「某一关是否通关」判定（取证见 <see cref="SystemUnlockTable"/>），
/// 新号只有 1-1 通关，绝大多数入口是灰锁 —— 也就**点不开、抓不到上行命令**。
/// 要继续按「用真实客户端的请求序列决定下一个要实现什么」推进，就必须能一键造出
/// 「已通关到第 N 章」的账号。这就是本控制台的第一用途。
///
/// <para>命令集（每行一条，<c>#</c> 或 <c>--</c> 开头为注释）</para>
/// <code>
/// unlock all                 把 SystemUnlock.txt 里全部真实解锁关卡记为已通关（跳过哨兵/占位行）
/// unlock &lt;Key&gt;               只解锁某个模块，如 unlock UI_UnlockShop
/// pass &lt;ch&gt; &lt;idx&gt; &lt;diff&gt; [starMask]   记一关通关（starMask 缺省 7 = 三星全亮）
/// passchapter &lt;ch&gt; [diff]    把该章 Chapter.txt 里的全部关卡记为三星通关
/// money &lt;id&gt; &lt;value&gt;         设某种货币的绝对值（1 体力 / 2 金币 / 3 免费钻）
/// addmoney &lt;id&gt; &lt;delta&gt;      增减某种货币
/// item &lt;G&gt; &lt;D&gt; &lt;P&gt; &lt;L&gt; &lt;N&gt;   走 RewardService 发一份奖励（自动折算货币 / 叠加背包）
/// level &lt;n&gt;                  直接设指挥官等级（同时推 pf 1029）
/// exp &lt;n&gt;                    加指挥官经验（走 PlayerExp.txt 连续升级）
/// task &lt;group&gt; &lt;id&gt; &lt;value&gt;  设任务值并推 pf 1026（调试指引/成就进度用）
/// godmode                    ★ 一键全解锁 = allitem + allgirl + alllevel + allmission + allguide + maxlevel
/// allitem                    全道具（ItemList.txt 逐条；货币折算成绝对值、其余入包）
/// allgirl                    全角色卡（CharacterCard.txt 逐张 + 衣服/卡面获得标记）
/// alllevel                   全关卡三星通关（Chapter.txt 逐关，覆盖面 ⊇ unlock all）
/// allmission                 全任务/成就完成（缺省「已领奖」；加 open 则为「可领取」）
/// allguide                   全新手指引完成（UIGuide.txt → 任务值 group 10 = 100）
/// maxlevel                   指挥官满级（PlayerExp.txt 的最高级）
/// who                        打印在线会话与玩家摘要
/// dump                        打印当前玩家的完整存档 JSON 路径与关键字段
/// save                        立刻存盘
/// </code>
///
/// 命令一律作用于**全部在线会话**（本地验收只有一个客户端，不做会话选择）。
/// 未登录的会话（<c>Tag</c> 非 <see cref="Player"/>）被跳过。
/// </summary>
public sealed class ServerConsoleFeature : IGameFeature
{
    /// <summary>轮询间隔（与客户端 GM 通道一致，500ms）。</summary>
    public const int PollMilliseconds = 500;

    private GameFeatureContext _ctx = null!;
    private Config.CafeTable _coffeeConditions = Config.CafeTable.Empty;
    private SystemUnlockTable _unlock = SystemUnlockTable.Empty;

    public string Name => "服务端 GM 控制台（work/gmsrv.txt）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _coffeeConditions = Config.CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _unlock = SystemUnlockTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        string path = ctx.Options.ServerGmFile;
        if (string.IsNullOrWhiteSpace(path))
        {
            Log.Info("[gmsrv] 未配置 ServerGmFile，服务端 GM 控制台关闭");
            return;
        }

        // 后台轮询：与 App 层的 WatchGmFileAsync 同构（只比对内容、启动时不重放）。
        // 放在 feature 里而不是 App 里，是为了让「命令实现」与「功能域」同处一个程序集，
        // 新增命令不必改 App。
        _ = Task.Run(() => WatchAsync(Path.GetFullPath(path)));
    }

    private async Task WatchAsync(string full)
    {
        string? last = null;
        try
        {
            if (File.Exists(full))
                last = await File.ReadAllTextAsync(full);
        }
        catch (Exception e)
        {
            Log.Warn($"[gmsrv] 读取 {full} 失败：{e.Message}");
        }
        Log.Info($"[gmsrv] 服务端 GM 就绪：把命令写进 {full}（每行一条，`help` 看命令集）");

        while (true)
        {
            try
            {
                await Task.Delay(PollMilliseconds);
                if (!File.Exists(full))
                    continue;
                string cur = await File.ReadAllTextAsync(full);
                if (cur == last)
                    continue;
                last = cur;
                foreach (var raw in cur.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("--"))
                        continue;
                    Execute(line);
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[gmsrv] 处理 {full} 失败：{e.Message}");
            }
        }
    }

    /// <summary>执行一条命令（公开以便单测直接调用）。</summary>
    public void Execute(string line)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        string cmd = parts[0].ToLowerInvariant();
        Log.Info($"[gmsrv] > {line}");

        switch (cmd)
        {
            case "help":
                Log.Info("[gmsrv] 命令：unlock all|<Key> / pass ch idx diff [star] / passchapter ch [diff] / " +
                         "money id v / addmoney id d / item G D P L N / " +
                         "equip <genre 2|3|4> D P L [skills=t:id:lv,..] [inlay=b,a,s,g] [attrs=t:v,..] [own=guid] [lv=n] [exp=n] [brk=n] / " +
                         "level n / exp n / task g id v / mood <girlId|all> v / housefix / " +
                         "luacall <cmd> [json] / call <sFunction> [json] / " +
                         "godmode / allitem / allgirl / alllevel / allmission [open] / allguide / maxlevel / " +
                         "who / dump / save");
                return;
            case "who":
                foreach (var (session, player) in OnlinePlayers())
                {
                    Log.Info($"[gmsrv] session {session.Id} account='{player.Account}' roleid={player.Roleid} " +
                             $"LV.{player.Level} exp={player.Exp} 体力={player.Vigour} " +
                             $"金币={player.GetMoney(MoneyType.Gold)} 钻={player.GetMoney(MoneyType.FreeDiamond)} " +
                             $"关卡={player.Levels.Count} 物品={player.Items.Count} 少女={player.Girls.Count}");
                }
                return;
        }

        int applied = 0;
        foreach (var (session, player) in OnlinePlayers())
        {
            try
            {
                if (Apply(cmd, parts, session, player))
                    applied++;
            }
            catch (Exception e)
            {
                Log.Error($"[gmsrv] 执行 `{line}` 失败（session {session.Id}）：{e.Message}");
            }
        }

        if (applied == 0)
            Log.Warn($"[gmsrv] `{line}` 未生效（没有已登录会话，或命令无法识别）");
    }

    /// <summary>对一个已登录玩家执行命令；返回是否识别并执行。</summary>
    private bool Apply(string cmd, string[] parts, ClientSession session, Player player)
    {
        switch (cmd)
        {
            case "unlock":
                return DoUnlock(parts, session, player);

            case "pass":
            {
                if (parts.Length < 4)
                    return Usage("pass <chapter> <index> <diff> [starMask]");
                int ch = Int(parts[1]), idx = Int(parts[2]), diff = Int(parts[3]);
                int star = parts.Length >= 5 ? Int(parts[4]) : MapList.StarMask;
                RecordPass(session, player, MapList.MapId(ch, idx, diff), star, push: true);
                Save(player);
                return true;
            }

            case "passchapter":
            {
                if (parts.Length < 2)
                    return Usage("passchapter <chapter> [diff]");
                int ch = Int(parts[1]);
                int diff = parts.Length >= 3 ? Int(parts[2]) : 1;
                var levels = new List<KeyValuePair<uint, int>>();
                foreach (var cfg in _ctx.Chapters.All.Where(c => c.Chapter == ch && c.Diff == diff)
                                                     .OrderBy(c => c.Index))
                {
                    uint mapId = MapList.MapId(cfg.Chapter, cfg.Index, cfg.Diff);
                    levels.Add(new(mapId, RecordPass(session, player, mapId, MapList.StarMask, push: false)));
                }
                GameNotify.PushChapters(session, levels);
                Save(player);
                Log.Info($"[gmsrv] 第 {ch} 章 diff{diff} 记为三星通关：{levels.Count} 关");
                return true;
            }

            case "money":
            {
                if (parts.Length < 3)
                    return Usage("money <id> <value>");
                int id = Int(parts[1]), v = Int(parts[2]);
                player.SetMoney(id, v);
                Save(player);
                GameNotify.PushMoney(session, player, id);
                Log.Info($"[gmsrv] 货币 {id}({MoneyType.NameOf(id)}) = {player.GetMoney(id)}");
                return true;
            }

            case "addmoney":
            {
                if (parts.Length < 3)
                    return Usage("addmoney <id> <delta>");
                int id = Int(parts[1]), d = Int(parts[2]);
                player.AddMoney(id, d);
                if (player.GetMoney(id) < 0)
                    player.SetMoney(id, 0);
                Save(player);
                GameNotify.PushMoney(session, player, id);
                Log.Info($"[gmsrv] 货币 {id}({MoneyType.NameOf(id)}) {(d >= 0 ? "+" : "")}{d} → {player.GetMoney(id)}");
                return true;
            }

            case "item":
            {
                if (parts.Length < 6)
                    return Usage("item <G> <D> <P> <L> <N>");
                var tuple = new[] { Int(parts[1]), Int(parts[2]), Int(parts[3]), Int(parts[4]), Int(parts[5]) };
                var granted = _ctx.Rewards.Grant(player, new[] { tuple }, RewardSource.Extra);
                Save(player);
                GameNotify.PushRewards(session, player, granted);
                Log.Info($"[gmsrv] 发放 {granted.Describe()}");
                return true;
            }

            // ★ 专用于验收「genre 2/3/4 安全下发」的造数据命令（第六轮新增）。
            //   与 `item` 的区别：`item` 走 RewardService（只适合货币/道具），
            //   这条直接构造带 skillinfo/weaponinlay/attrlist/ownguid 的装备实例，
            //   用来在真机上验证客户端 AddWeapon/AddOriginal/AddItem 不炸、数据不丢。
            //   取证见 work/weapon-genre-forensics.md 与 work/weapon-mainsession.md。
            case "equip":
            {
                // equip <genre> <D> <P> <L> [skills=type:id:lv,...] [inlay=b,a,s,g] [attrs=t:v,...] [own=guid]
                if (parts.Length < 5)
                    return Usage("equip <genre 2|3|4> <D> <P> <L> " +
                                 "[skills=type:id:lv,...] [inlay=barrel,ammo,sight,grip] " +
                                 "[attrs=atype:avalue,...] [own=<宿主武器guid>] " +
                                 "[lv=<强化等级>] [exp=<当前经验>] [brk=<已突破次数>]");

                uint genre = (uint)Int(parts[1]);
                if (genre is not (2 or 3 or 4))
                    return Usage("equip 的 genre 只能是 2(武器) / 3(符文) / 4(零件)");

                var item = player.AddItem(genre, (uint)Int(parts[2]), (uint)Int(parts[3]),
                                          (uint)Int(parts[4]), count: 1);
                item.CreateTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var opt in parts.Skip(5))
                {
                    int eq = opt.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = opt[..eq].ToLowerInvariant();
                    string val = opt[(eq + 1)..];

                    switch (key)
                    {
                        case "skills":
                            // type:id:lv 三元，逗号分隔。武器 type 1=主技能 2=被动 4=校准；符文 2/6；零件 7/8/9
                            foreach (var one in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var f = one.Split(':');
                                if (f.Length < 2)
                                    continue;
                                item.Skills.Add(new ItemSkill
                                {
                                    SkillType = Int(f[0]),
                                    SkillId = (uint)Int(f[1]),
                                    SkillLevel = f.Length > 2 ? (uint)Int(f[2]) : 1,
                                });
                            }
                            break;

                        case "inlay":
                        {
                            var f = val.Split(',');
                            item.Inlay = new WeaponInlay
                            {
                                Barrel = f.Length > 0 ? (uint)Int(f[0]) : 0,
                                Ammo = f.Length > 1 ? (uint)Int(f[1]) : 0,
                                Sight = f.Length > 2 ? (uint)Int(f[2]) : 0,
                                Grip = f.Length > 3 ? (uint)Int(f[3]) : 0,
                            };
                            break;
                        }

                        case "attrs":
                            // atype:avalue 逗号分隔。★ 下发时会自动补齐到 6 个（Item.NormalizedAttrs）
                            foreach (var one in val.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var f = one.Split(':');
                                if (f.Length < 2)
                                    continue;
                                item.Attrs.Add(new ItemAttr { AType = (uint)Int(f[0]), AValue = Int(f[1]) });
                            }
                            break;

                        case "own":
                            item.OwnGuid = (uint)Int(val);
                            break;

                        // ★ 本轮新增：直接造「已升级 / 已突破」的装备。
                        // 验收武器突破（nCmd=3）必须先让武器达到当前等级上限，
                        // 否则服务端会按「未达上限」正确拒绝 —— 而靠强化一级一级升要造几十把素材。
                        case "lv":
                            item.EnhanceLevel = (uint)Math.Max(1, Int(val));
                            break;

                        case "exp":
                            item.EnhanceExp = (uint)Math.Max(0, Int(val));
                            break;

                        case "brk":
                            item.BreakLevel = (uint)Math.Max(0, Int(val));
                            break;
                    }
                }

                Save(player);
                // ★ 必须用 pf 1031（会派发 Lua 事件刷界面）；pf 1104 不派发，见 docs/05 §0.2
                GameNotify.PushItem(session, item);
                Log.Info($"[gmsrv] 发装备 guid={item.Guid} genre={genre} GDPL=({genre},{item.Detail}," +
                         $"{item.Particular},{item.Level}) skills={item.Skills.Count} " +
                         $"inlay={(item.Inlay is null ? "无" : $"{item.Inlay.Barrel}/{item.Inlay.Ammo}/{item.Inlay.Sight}/{item.Inlay.Grip}")} " +
                         $"attrs={item.Attrs.Count}(下发补齐到 {Item.AttrSlotCount}) own={item.OwnGuid}");
                return true;
            }

            // ★★ 诊断专用：**故意**下发一条 attrlist 元素不足 6 个的零件，
            //    用来在真机上验证 docs/05 §0.3.3 那条红线（少于 6 个 ⇒ 客户端抛
            //    ArgumentOutOfRangeException ⇒ 整包物品全丢）。
            //    正常业务路径永远不会这样发（BuildItemInfo 走 NormalizedAttrs 恒补齐到 6）。
            //    ⇒ 这条命令只应在验证红线时用，验完即可忘记它存在。
            case "equipbad":
            {
                // equipbad <D> <P> <L> <要发几个attr>
                if (parts.Length < 5)
                    return Usage("equipbad <D> <P> <L> <attr个数>  ★ 诊断用：故意发不足 6 个 attrlist");

                int want = Math.Max(0, Int(parts[4]));
                var bad = player.AddItem(GameNotify.EquipGenreOriginal, (uint)Int(parts[1]),
                                         (uint)Int(parts[2]), (uint)Int(parts[3]), count: 1);
                bad.CreateTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Save(player);

                // 绕过 BuildItemInfo 的补齐，手工组一条「残缺」的 ItemInfo
                var info = new SssjServer.Protocol.GsProto.ItemInfo
                {
                    Guid = bad.Guid,
                    Genre = bad.Genre,
                    Detail = bad.Detail,
                    Particular = bad.Particular,
                    Level = bad.Level,
                    Count = bad.Count,
                    Createtime = bad.CreateTime,
                };
                for (int i = 0; i < want; i++)
                {
                    info.Attrlist.Add(new SssjServer.Protocol.GsProto.BaseAttribute
                    {
                        Atype = (uint)(i + 1),
                        Avalue = 100 * (i + 1),
                    });
                }

                var ntf = new SssjServer.Protocol.GsProto.ItemUpdateNtf();
                ntf.Items.Add(info);
                session.Push(GamePf.ItemUpdateNtf, ntf);
                Log.Warn($"[gmsrv] ★诊断★ 故意下发残缺零件 guid={bad.Guid} GDPL=(4,{bad.Detail}," +
                         $"{bad.Particular},{bad.Level}) attrlist **只有 {want} 个**（正常必须 " +
                         $"{Item.AttrSlotCount} 个）—— 预期客户端抛 ArgumentOutOfRangeException");
                return true;
            }

            case "cafe":
            {
                // cafe <level> [hot] [comfort] —— 直接设咖啡馆标量（验收 111/135 等用）
                if (parts.Length < 2)
                    return Usage("cafe <level> [hot] [comfort]");
                var cur = CafeFeature.ReadState(player);
                int lv = Math.Max(1, Int(parts[1]));
                int hot = parts.Length > 2 ? Int(parts[2]) : cur.Hot;
                int comfort = parts.Length > 3 ? Int(parts[3]) : cur.Comfort;
                CafeFeature.WriteState(player, new CafeFeature.CafeState(lv, hot, comfort));
                Save(player);
                Log.Info($"[gmsrv] 咖啡馆 lv={cur.Level}→{lv} hot={cur.Hot}→{hot} comfort={cur.Comfort}→{comfort}"
                         + "（★ 等级/人气要重进 map 6 才会在界面上刷新）");
                return true;
            }

            case "maillist":
            {
                // 重推 pf 1014 全量列表（不改数据）。
                // ★ 存在的理由：验「flag bit0 = 清客户端缓存」这条修复**必须在同一会话内
                //   重推**才能暴露 —— force-stop 重登时客户端缓存本来是空的，
                //   追加与替换看不出区别（见 MailFeature.PushList 的取证）。
                new MailFeature().PushList(session, MailFeature.Load(player));
                Log.Info($"[gmsrv] 已重推邮件列表");
                return true;
            }

            case "mail":
            {
                // mail <标题> [附件 G-D-P-L-N,...]
                // ★ 标题里不能带空格（gmsrv 按空格切词）；附件用逗号分隔多项。
                if (parts.Length < 2)
                    return Usage("mail <标题不含空格> [G-D-P-L-N,G-D-P-L-N]");

                var attach = new List<int[]>();
                if (parts.Length >= 3)
                {
                    foreach (var chunk in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var seg = chunk.Split('-', StringSplitOptions.RemoveEmptyEntries);
                        if (seg.Length < 4)
                        {
                            Log.Warn($"[gmsrv] 附件 \"{chunk}\" 段数不足 4，跳过");
                            continue;
                        }
                        attach.Add(new[]
                        {
                            Int(seg[0]), Int(seg[1]), Int(seg[2]), Int(seg[3]),
                            seg.Length > 4 ? Int(seg[4]) : 1,
                        });
                    }
                }

                var mailFeature = new MailFeature();
                mailFeature.SendMail(session, player,
                                     parts[1], "GM", "这是一封 GM 邮件。", attach);
                Save(player);
                Log.Info($"[gmsrv] 已发邮件《{parts[1]}》，附件 {attach.Count} 项");
                return true;
            }

            case "level":
            {
                if (parts.Length < 2)
                    return Usage("level <n>");
                int n = Math.Max(1, Int(parts[1]));
                player.Level = n;
                player.Exp = 0;
                Save(player);
                RefreshCoffeeAfterLevelChange(session, player);
                GameNotify.PushLevelExp(session, player);
                Log.Info($"[gmsrv] 等级 → LV.{player.Level}（体力上限 {_ctx.PlayerExp.MaxVigour(player.Level)}）");
                return true;
            }

            // ★ 少女心情复位（第十二轮新增）。
            //
            // 为什么需要它：`LuaCall 175 GirlMoodBoom` 会把少女心情打到 0
            // （`MoodBoom.txt` 唯一一行是 PlotID=1 GirlID=7 MoodChangeValue=-100），
            // 而**现网没有任何让心情回升的通道**（摸头只加好感度）⇒ 一旦验收过 175，
            // 那个少女就永久 UnHappy。后果是**私信「回复」按钮点了没反应**：
            // `UI_Phone.lua:658` 判 `GirlCommon:GetHappyMode(NpcId) == UnHappy` 就
            // `MessageBox.Simple('本小姐才没看你呢!')` 并 return，**上行包压根不发**
            // ⇒ 服务端日志干净、也没有 unhandled，极易误判成「私信域没实现」。
            //
            // ★★ 必须推 **pf 1030**（少女整体覆盖），不是 1026 —— 心情存 protobuf
            //     `GirlInfo.moodvalue` 而不是任务值，取证见 docs/05 §27。
            case "mood":
            {
                if (parts.Length < 3)
                    return Usage("mood <girlId|all> <value 0..100>");

                int want = Math.Clamp(Int(parts[2]), 0, 100);
                var targets = new List<Girl>();
                if (parts[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    targets.AddRange(player.Girls);
                }
                else
                {
                    uint gid = (uint)Math.Max(0, Int(parts[1]));
                    var g = player.GetGirl(gid);
                    if (g is null)
                    {
                        Log.Warn($"[gmsrv] mood：玩家没有少女 {gid}（现有 " +
                                 $"{string.Join(",", player.Girls.Select(x => x.Girlid))}）");
                        return true;
                    }
                    targets.Add(g);
                }

                foreach (var g in targets)
                    g.Moodvalue = (uint)want;
                Save(player);
                foreach (var g in targets)
                    GameNotify.PushGirl(session, g);

                Log.Info($"[gmsrv] 少女心情 → {want}：{targets.Count} 个" +
                         $"（{string.Join(",", targets.Select(g => g.Girlid))}）—— 已推 pf 1030" +
                         (want > 1 ? "；私信「回复」不再被 UnHappy 拦" : ""));
                return true;
            }

            // ★★★ 修复存量存档里「摆位全 0」的家具（第十二轮新增）。
            //
            // 光修 OnBuyTheme 的代码不够 —— 坏数据已经落盘了。存量存档里
            // parentId=0 的家具会让客户端进店时在
            // NCafeUtilsController.UpdateNavMeshObstacle(0x1808C18) 抛 NRE、
            // LoadSceneTreeItems 回调链断、加载条永远停在 40%。
            // 这条命令按「已购主题 → NCafeThemes.txt」回查并补回原厂摆位。
            case "housefix":
            {
                string dir = Path.GetFullPath(_ctx.Options.SettingDirectory);
                var (n, removed) = HouseFeature.RepairPlacements(
                    player, HouseTable.Load(dir), CafeTable.Load(dir), session.Id);
                if (n == 0 && removed == 0)
                {
                    Log.Info("[gmsrv] housefix：没有摆位为 0 的家具，无需修复");
                    return true;
                }
                Save(player);
                Log.Info($"[gmsrv] housefix：修好 {n} 件、删除 {removed} 件 —— " +
                         "★ 家具经 pf 1048 在登录时下发，**要重启客户端**才会生效");
                return true;
            }

            case "exp":
            {
                if (parts.Length < 2)
                    return Usage("exp <n>");
                var (gained, lv, exp) = PlayerProgress.AddMasterExp(player, _ctx.PlayerExp, Int(parts[1]));
                Save(player);
                RefreshCoffeeAfterLevelChange(session, player);
                GameNotify.PushLevelExp(session, player);
                Log.Info($"[gmsrv] 经验 +{Int(parts[1])} → LV.{lv} exp={exp}（升 {gained} 级）");
                return true;
            }

            case "task":
            {
                if (parts.Length < 4)
                    return Usage("task <group> <id> <value>");
                int g = Int(parts[1]), id = Int(parts[2]), v = Int(parts[3]);
                player.SetTask(g, id, v);
                Save(player);
                GameNotify.PushTaskValues(session, new[]
                {
                    new KeyValuePair<uint, int>(Player.ComposeTaskId(g, id), v),
                });
                Log.Info($"[gmsrv] 任务值 ({g},{id}) = {v}");
                return true;
            }

            case "dump":
            {
                Log.Info($"[gmsrv] account='{player.Account}' roleid={player.Roleid} LV.{player.Level} " +
                         $"exp={player.Exp} 体力={player.Vigour}/{_ctx.PlayerExp.MaxVigour(player.Level)}");
                Log.Info($"[gmsrv] 货币：{string.Join(" ", player.Money.OrderBy(k => k.Key).Select(k => $"{k.Key}({MoneyType.NameOf(k.Key)})={k.Value}"))}");
                Log.Info($"[gmsrv] 关卡：{DescribeLevels(player)}");
                Log.Info($"[gmsrv] 物品 {player.Items.Count} 件，少女 {player.Girls.Count} 个，编队 {player.Formations.Count} 组");
                return true;
            }

            case "save":
                Save(player);
                Log.Info("[gmsrv] 已存盘");
                return true;

            // ================================================================
            //  ★★★ luacall / call：代客户端注入一条上行（第十四轮新增）
            // ================================================================
            //
            // 用法：
            //   gmsrv.sh 'luacall <cmd> [tbParam 的 JSON]'
            //   gmsrv.sh 'call <sFunction> [tbParam 的 JSON]'
            //
            // 例：
            //   luacall 320                                  佣兵进塔（无参）
            //   luacall 321 {"nWeekId":331,"buff":[1]}        对象参数
            //   luacall 53  [1,1]                            ★ 裸数组参数（排行塔就是这种）
            //   call PhoneMsg {"nCmd":3,"nMsgId":71000,"nSelectId":1}
            //
            // ★★★ 为什么需要它（本轮验收被 `tools/gm.sh` 卡住三次才补的）：
            // `gm.sh` 走「服务端下发 Lua → 客户端 DoLuaString 执行 → 客户端再发上行」，
            // 链路长且**会静默失效** —— 实测同一客户端生命周期内下发若干次后就不再执行，
            // 客户端连 `print` 都不输出，而服务端日志仍显示「已下发到 1 个会话」。
            // 症状是「服务端收不到包」，**极易被误判成功能没实现**（本轮就误判过 321/322/53）。
            //
            // 本命令直接在服务端侧调 <see cref="LuaCallRouter.DispatchAsync"/>，
            // 走的是与真实上行**完全相同**的路由与 handler，只跳过网络与客户端脚本层
            // ⇒ 验证服务端逻辑时结果稳定可复现；
            // 回包仍照常下发给在线客户端，所以界面表现也能一并看。
            //
            // ★ 局限：它不验证「客户端是否真的会发这个包」与「上行参数的真实形状」。
            //   那两件事仍要靠 `probe_ui.sh` 点真实按钮来采集。
            case "luacall":
            case "call":
            {
                if (parts.Length < 2)
                {
                    Log.Warn("[gmsrv] 用法：luacall <cmd> [json] / call <sFunction> [json]");
                    return true;
                }

                // ★ 参数 JSON 可能被空格切成多段（例 {"a":1, "b":2}）⇒ 把第 2 个 token
                //   之后的所有段重新拼起来。JSON 的值内部若含空格会被压成一个空格，
                //   对结构无影响；要传含空格的字符串值请改用不带空格的写法。
                string payload = parts.Length > 2
                    ? string.Join(' ', parts.Skip(2))
                    : "";
                if (payload.Length == 0)
                    payload = "{}";

                string function;
                string json;
                if (int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int luaCmd))
                {
                    // LuaCall 数字命令族：外壳是 {sCmd, tbParam}
                    function = LuaCallRouter.LuaCallFunction;
                    json = $"{{\"sCmd\":{luaCmd},\"tbParam\":{payload}}}";
                }
                else
                {
                    // sFunction 族：param 就是 tbParam 本体（没有 sCmd/tbParam 外壳）
                    function = parts[1];
                    json = payload;
                }

                Log.Info($"[gmsrv] 注入上行 sFunction={function} json={json}");
                _ = _ctx.LuaCalls.DispatchAsync(session, function, json);
                return true;
            }

            // ---- GM 全解锁（实现在 GmUnlock，这里只负责「执行 + 按报告推增量」）----
            case "godmode":
            case "allunlock":
                return DoUnlockAll(session, player,
                    gm => gm.ApplyAll(player, force: true), "全解锁(道具+角色+关卡+任务)");

            case "allitem":
                return DoUnlockAll(session, player, gm => gm.ApplyItems(player), "全道具");

            case "allgirl":
            case "allcard":
                return DoUnlockAll(session, player, gm => gm.ApplyCards(player), "全角色卡");

            case "alllevel":
            case "allchapter":
                return DoUnlockAll(session, player, gm => gm.ApplyLevels(player), "全关卡三星");

            case "allmission":
            {
                // `allmission open` = 标记为「可领取」而不是「已领奖」，用于验领奖链路。
                bool open = parts.Length >= 2
                         && string.Equals(parts[1], "open", StringComparison.OrdinalIgnoreCase);
                bool saved = _ctx.Options.GmMissionClaimed;
                _ctx.Options.GmMissionClaimed = !open;
                try
                {
                    return DoUnlockAll(session, player, gm => gm.ApplyMissions(player),
                        open ? "全任务(可领取)" : "全任务(已领奖)");
                }
                finally
                {
                    _ctx.Options.GmMissionClaimed = saved;
                }
            }

            case "allguide":
                return DoUnlockAll(session, player, gm => gm.ApplyGuides(player), "全新手指引完成");

            case "maxlevel":
                return DoUnlockAll(session, player, gm => gm.ApplyMaxLevel(player), "指挥官满级");

            default:
                return false;
        }
    }

    /// <summary>
    /// 跑一段 <see cref="GmUnlock"/> 操作，然后按 <see cref="GmUnlockReport"/> 把增量推给在线客户端。
    ///
    /// <para><b>为什么运行期触发必须推增量</b></para>
    /// <see cref="GmProvisionFeature"/> 走的是登录前钩子，数据随 1005/1104/1026 全量下发；
    /// 而 gmsrv 是「玩家已经在线」时执行的，客户端内存里那份数据不会自己变，
    /// 必须按 <see cref="GameNotify"/> 的四条专用通道推：
    /// <list type="bullet">
    ///   <item>货币 → pf 1032（一次一种，消息体只有单个 <c>MoneyInfo</c>）</item>
    ///   <item>背包 → pf 1031（<b>不能</b>用 1104，1104 的 ntf=0 不派发刷新事件，docs/05 §0.2）</item>
    ///   <item>关卡 → pf 1034（star 必须是完整位域，客户端整值覆盖）</item>
    ///   <item>任务值 → pf 1026（逐条 merge 不清表，只推变化的键即可）</item>
    ///   <item>少女 → pf 1030（一次一个 <c>GirlInfo</c>，整体覆盖）</item>
    /// </list>
    ///
    /// <para><b>背包增量分批</b></para>
    /// 全道具一次能产生 2000+ 个实例，塞进一条 pf 1031 是 ~80KB 的单包。
    /// 客户端 <c>UNet.Net</c> 用 <c>MemoryStream</c> + <c>tail</c> 拼包、没有硬上限，
    /// 但一条巨包会让 <c>OnUpdateItem</c> 在一帧里 upsert 两千次并派发同样多的 Lua 事件。
    /// 所以这里按 <see cref="ItemPushBatch"/> 分批发，把压力摊到多帧。
    /// 登录路径没有这个问题（1104 全量本来就是一条，且客户端 <c>ntf=0</c> 不派发事件）。
    /// </summary>
    private bool DoUnlockAll(ClientSession session, Player player,
                             Func<GmUnlock, GmUnlockReport> action, string what)
    {
        var report = action(_ctx.GmUnlock);
        Save(player);

        GameNotify.PushMoney(session, player, report.ChangedMoney);

        for (int i = 0; i < report.ChangedItems.Count; i += ItemPushBatch)
        {
            var slice = report.ChangedItems.Skip(i).Take(ItemPushBatch).ToList();
            GameNotify.PushItems(session, slice);
        }

        // 被摘掉的实例：pf 1031 里 count=0 就是删除
        for (int i = 0; i < report.RemovedItems.Count; i += ItemPushBatch)
        {
            var slice = report.RemovedItems.Skip(i).Take(ItemPushBatch).ToList();
            GameNotify.PushItemsRemoved(session, slice);
        }

        if (report.ChangedLevels.Count > 0)
            GameNotify.PushChapters(session, report.ChangedLevels);

        if (report.ChangedTasks.Count > 0)
            GameNotify.PushTaskValues(session, report.ChangedTasks);

        foreach (var girl in report.NewGirls)
            GameNotify.PushGirl(session, girl);

        if (report.LevelChanged)
            RefreshCoffeeAfterLevelChange(session, player);
                GameNotify.PushLevelExp(session, player);

        Log.Info($"[gmsrv] {what} 完成：{report.Describe()}");
        Log.Info($"[gmsrv] 当前存量：LV.{player.Level} / 背包 {player.Items.Count} 项 / " +
                 $"少女 {player.Girls.Count} 位 / 关卡 {player.Levels.Count} 关 / " +
                 $"任务值 {player.Tasks.Count} 条 / 金币 {player.GetMoney(MoneyType.Gold)}");
        Log.Info("[gmsrv] 提示：仓库/角色/任务界面的**列表**是打开时构建的，" +
                 "已开着的界面需要关掉重开（或重登）才会重绘");
        return true;
    }

    /// <summary>运行期推 pf 1031 增量时的单包物品数上限（见 <see cref="DoUnlockAll"/>）。</summary>
    public const int ItemPushBatch = 200;

    // ---- unlock ----
    private bool DoUnlock(string[] parts, ClientSession session, Player player)    {
        if (parts.Length < 2)
            return Usage("unlock all | unlock <Key>");

        List<SystemUnlockTable.Entry> targets;
        if (string.Equals(parts[1], "all", StringComparison.OrdinalIgnoreCase))
        {
            targets = _unlock.Entries.Where(e => !e.IsPlaceholder).ToList();
        }
        else
        {
            var e = _unlock.Get(parts[1]);
            if (e is null)
            {
                Log.Warn($"[gmsrv] SystemUnlock.txt 里没有 Key='{parts[1]}'");
                return true;
            }
            if (e.IsPlaceholder)
                Log.Warn($"[gmsrv] '{e.Key}' 是哨兵/占位行（{e.Chapter}-{e.Index}-{e.Diff}），仍按要求记录");
            targets = new List<SystemUnlockTable.Entry> { e };
        }

        var levels = new List<KeyValuePair<uint, int>>();
        var seen = new HashSet<uint>();
        foreach (var e in targets)
        {
            if (!seen.Add(e.MapId))
                continue;
            levels.Add(new(e.MapId, RecordPass(session, player, e.MapId, MapList.StarMask, push: false)));
        }

        GameNotify.PushChapters(session, levels);
        Save(player);
        Log.Info($"[gmsrv] 解锁完成：{targets.Count} 个模块 → {levels.Count} 个关卡记为三星通关" +
                 $"（客户端 SystemUnlock 立即生效，UI 需要重开界面才会重绘）");
        return true;
    }

    /// <summary>记一关通关（已通关的不重复累加次数），返回新的 star 位域。</summary>
    private static int RecordPass(ClientSession session, Player player, uint mapId, int starMask, bool push)
    {
        int cur = player.GetLevelStar(mapId);
        int composed;
        if (MapList.PassCount(cur) > 0)
        {
            // 已有通关记录：只补三星位，不再增加通关次数（避免反复执行 unlock 把次数刷爆）
            composed = MapList.Compose(MapList.Stars(cur) | MapList.Stars(starMask), MapList.PassCount(cur));
            player.Levels[mapId] = composed;
        }
        else
        {
            composed = player.RecordLevelPass(mapId, starMask);
        }
        if (push)
            GameNotify.PushChapter(session, mapId, composed);
        return composed;
    }

    private static string DescribeLevels(Player player)
    {
        if (player.Levels.Count == 0)
            return "（无）";
        var sb = new StringBuilder();
        foreach (var (mapId, star) in player.Levels.OrderBy(kv => kv.Key).Take(40))
        {
            var (ch, idx, diff) = MapList.Decode(mapId);
            sb.Append($"{ch}-{idx}(d{diff})★{MapList.Stars(star)}×{MapList.PassCount(star)} ");
        }
        if (player.Levels.Count > 40)
            sb.Append($"… 共 {player.Levels.Count} 关");
        return sb.ToString().TrimEnd();
    }

    private void RefreshCoffeeAfterLevelChange(ClientSession session, Player player)
    {
        var changes = CafeBizFeature.RefreshCoffeeConditions(_coffeeConditions, player);
        if (changes.Count == 0) return;
        Save(player);
        GameNotify.PushTaskValues(session, changes);
    }

    private void Save(Player player) => _ctx.Store.Save(player);

    private IEnumerable<(ClientSession Session, Player Player)> OnlinePlayers()
    {
        foreach (var s in _ctx.World.OnlineSessions())
        {
            if (s is SessionAdapter adapter && adapter.Inner.Tag is Player p)
                yield return (adapter.Inner, p);
        }
    }

    private static bool Usage(string usage)
    {
        Log.Warn($"[gmsrv] 用法：{usage}");
        return true;
    }

    private static int Int(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
