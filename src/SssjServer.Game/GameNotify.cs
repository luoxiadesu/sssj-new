using SssjServer.Core;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game;

/// <summary>
/// 统一的「玩家数据变更下行」推送层。
///
/// <para><b>为什么要有这一层</b></para>
/// 每个功能域（关卡 / 任务 / 背包 / 商店 / 抽卡 …）改完玩家数据后，都要把变更推给客户端，
/// 而客户端刷新界面**只认这几条专用 pf**（都是 <c>Register</c>-only 的纯服务端推送，
/// 见 <c>docs/pf-table.txt</c> 第 28..34 行）：
///
/// | pf | 消息 | 客户端 handler | 语义 |
/// | --- | --- | --- | --- |
/// | 1029 | <c>PlayerDataNtf</c> | <c>Game.Player.OnPlayerDataChange</c> | 等级 / 经验 / 头像框 / 社团（updatetype 见 <see cref="PlayerProgress"/>） |
/// | 1030 | <c>GirlDataNtf</c> | <c>Game.Player.OnGirlUpdate</c> | 单个少女整体覆盖 |
/// | 1031 | <c>ItemUpdateNtf</c> | <c>Game.EquipManager.OnUpdateItem</c> | 背包**增量**，count 是绝对值、0 = 删除 |
/// | 1032 | <c>MoneyUpdateNtf</c> | <c>Game.Player.OnMoneyChange</c> | 单种货币（消息只有一个 Info 字段，无法批量） |
/// | 1033 | <c>FormationInfoUpdateNtf</c> | <c>Game.EquipManager.UpdateFormationNtf</c> | 单个编队整体覆盖 |
/// | 1034 | <c>ChapterUpdateNtf</c> | <c>Game.Player.OnChapterChange</c> | 关卡星级位域（<c>ReflashLevelList</c> 整值覆盖） |
/// | 1026 | <c>TaskValueSyncRsp</c> | <c>Game.Player.OnTaskValueSyncRsp</c> | 任务值，**逐条 merge 不清表**（RVA 0x1852B18） |
/// | 1104 | <c>ItemNtf</c> | <c>Game.Login.ItemNtfMsg</c> | 背包**全量** |
///
/// 在这一层集中实现的收益：①功能域不必各自重复这些细节（<c>ChapterHandler</c> 原先有一份私有副本）；
/// ②「count 绝对值」「1032 一次一种货币」「先 level 后 exp」这些坑只需要踩一次；
/// ③给 <see cref="RewardGrantResult"/> 一个「一把梭推送」的入口，
/// 让所有发奖的功能域（结算 / 任务 / 商店 / 邮件）表现一致。
/// </summary>
public static class GameNotify
{
    /// <summary>
    /// 推一条 pf 1032 <c>MoneyUpdateNtf</c>（客户端 <c>Game.Player.OnMoneyChange</c>）。
    /// 消息体只有单个 <c>Info</c>（<c>MoneyInfo{id,curnum}</c>），**一次只能一种货币**，
    /// 所以批量变更要调多次（见 <see cref="PushMoney(ClientSession, Player, IEnumerable{int})"/>）。
    /// <c>curnum</c> 是变更后的绝对值。
    /// </summary>
    public static void PushMoney(ClientSession session, Player player, int moneyId)
    {
        session.Push(GamePf.MoneyUpdateNtf, new MoneyUpdateNtf
        {
            Info = new MoneyInfo { Id = moneyId, Curnum = player.GetMoney(moneyId) },
        });
    }

    /// <summary>按货币 id 集合逐条推 pf 1032（自动去重、保持给定顺序）。</summary>
    public static void PushMoney(ClientSession session, Player player, IEnumerable<int> moneyIds)
    {
        var seen = new HashSet<int>();
        foreach (int id in moneyIds)
        {
            if (seen.Add(id))
                PushMoney(session, player, id);
        }
    }

    /// <summary>
    /// 推一条 pf 1031 <c>ItemUpdateNtf</c>（<c>repeated ItemInfo items</c>）。
    ///
    /// 客户端 <c>Game.EquipManager.OnUpdateItem</c>（RVA 0x1D9D72C）逐条按 <c>guid</c> upsert：
    /// <c>count</c> 是**绝对值**（<c>set_Count</c>，不是累加），<c>count == 0</c> 表示从背包移除。
    /// 所以调用方传的必须是「变更后的当前状态」；删除就把 <see cref="Item.Count"/> 置 0 再推。
    /// 空清单不推（避免无意义封包）。
    /// </summary>
    public static void PushItems(ClientSession session, IReadOnlyList<Item> items)
    {
        if (items.Count == 0)
            return;

        var ntf = new ItemUpdateNtf();
        foreach (var it in items)
            ntf.Items.Add(BuildItemInfo(it));
        session.Push(GamePf.ItemUpdateNtf, ntf);
    }

    /// <summary>推单件物品的 pf 1031。</summary>
    public static void PushItem(ClientSession session, Item item)
        => PushItems(session, new[] { item });

    /// <summary>
    /// 推「物品已删除」：pf 1031 里 <c>count = 0</c> 就是删除
    /// （客户端 <c>AddItem</c> RVA 0x1D9A940 / <c>AddWeapon</c> 0x1D9709C /
    /// <c>AddCharacterCard</c> 0x1D9541C / <c>AddOriginal</c> 0x1D99144 四个分支都一样）。
    /// 传入的 <paramref name="removed"/> 是**已从 <see cref="Player.Items"/> 摘掉**的实例，
    /// 这里只借它的 guid/genre/GDPL 组一条 count=0 的通知。
    /// </summary>
    public static void PushItemsRemoved(ClientSession session, IReadOnlyList<Item> removed)
    {
        if (removed.Count == 0)
            return;

        var ntf = new ItemUpdateNtf();
        foreach (var it in removed)
        {
            var info = BuildItemInfo(it);
            info.Count = 0;
            ntf.Items.Add(info);
        }
        session.Push(GamePf.ItemUpdateNtf, ntf);
    }

    /// <summary>
    /// 弹「获得物品」奖励框：LuaCall <b>75</b> <c>ItemBox_Open</c>。
    ///
    /// 取证：<c>LuaCall.lua:74</c> 定义、<c>:1302</c> 注册；客户端分支
    /// <c>ItemBox:OnOpenBox(tbParam)</c>（<c>ItemBox.lua:53</c>）——
    /// <b>全仓没有任何上行发送点</b>（grep <c>ItemBox_Open</c> 只命中定义/注册与 <c>_MO</c>），
    /// 即这是一条**纯服务端推送**的「获得物品」通道。
    /// tbParam 是元组数组，元素 <c>[G,D,P,L,N]</c>（<c>ItemBox.lua:64</c> 起遍历）。
    ///
    /// 附带效果（很有用）：<c>ItemBox.lua:77</c> 会
    /// <c>UI.Call('UI_Warehouse', "UpdateWhenServerMsg")</c> 刷新仓库列表 ——
    /// 这正好绕开 <c>UI_Warehouse.bDoSell</c> 把 pf 1031 屏蔽掉的那个坑。
    ///
    /// 客户端会在这些情况下**静默丢弃**（<c>ItemBox.lua:56..75</c>）：战斗中、
    /// <c>UI_NormalActivity</c>/<c>UI_ItemBox</c>/<c>UI_ItemBox2</c>/抽奖界面开着时。
    /// 所以它只是「表现」，真实入账必须靠 pf 1031/1032。
    /// </summary>
    public static void ShowRewardBox(LuaCallRouter luaCalls, ClientSession session, IReadOnlyList<int[]> tuples)
    {
        if (tuples.Count == 0)
            return;
        luaCalls.CallClient(session, LuaCallIds.ItemBoxOpen, tuples);
    }

    /// <summary>用一次发奖结果弹奖励框（只取 (G,D,P,L,N) 五元，去掉第 6 位标签）。</summary>
    public static void ShowRewardBox(LuaCallRouter luaCalls, ClientSession session, RewardGrantResult granted)
    {
        if (granted.IsEmpty)
            return;
        var tuples = granted.Rewards
            .Select(r => new[] { (int)r.Genre, (int)r.Detail, (int)r.Particular, (int)r.Level, (int)r.Count })
            .ToList();
        ShowRewardBox(luaCalls, session, tuples);
    }

    /// <summary>
    /// 推 pf 1104 <c>ItemNtf</c> 全量背包（客户端 <c>Game.Login.ItemNtfMsg</c>）。
    /// 正常只在登录时用；功能域改背包请用增量 pf 1031，避免客户端整表重建导致 UI 闪烁。
    ///
    /// <para><b>★★★ 必须分片：单包超过 0x10000 字节会让尾部物品静默丢失</b></para>
    /// 客户端 <c>UNet.Connection.Update()</c>（RVA <c>0x1B4D340</c>）**每帧只做一次**
    /// <c>Socket.Receive</c>，且缓冲区大小写死在 <c>0x1B4D3E8 mov w3, #0x10000</c>
    /// ⇒ 单次最多收 <b>65536</b> 字节。超出的部分虽然还在内核缓冲里，
    /// 但那一帧的 <c>Net.Dispatch()</c> 只能看到半个包 ——
    /// 实测症状是**整包被丢弃、尾部物品全部不见**（不抛异常、logcat 零报错）。
    ///
    /// ★ 实测取证（第九轮发现）：2362 件物品的 pf 1104 是 **66150 字节**（超出 614），
    /// 客户端最终只收到前 2342 件，**尾部 20 件全丢**
    /// （16 把武器 + 2 个零件 + 2 件皮肤；而 317 张角色卡因为都在前段，一张不少）。
    /// 判据是「服务端存档有、客户端 <c>GetWeapon(guid)</c> 取不到」，
    /// 且 <b>logcat 完全没有报错</b> —— 这是本项目最隐蔽的一类 bug。
    ///
    /// ★★ 为什么分片是安全的：<c>Game.EquipManager.InitItem</c>（<c>0x1D9DD10</c>）
    /// **不清空背包**，只是遍历 <c>items</c> 逐个 <c>AddCharacterCard</c>/<c>AddWeapon</c>/
    /// <c>AddOriginal</c>/<c>AddItem</c>（<c>0x1D9DE58</c> 起按 genre 分流）
    /// ⇒ 多条 pf 1104 的效果是**累加**，与一条大包等价。
    ///
    /// ★ <c>itemnum</c> 字段：客户端在 <c>0x1D9DFF4</c> 把它与内部计数比较，
    /// 不等就跳过 <c>DealLimitedItem</c>（<c>0x1D9E114</c>，限时道具清理）后正常返回
    /// —— 不是致命路径。这里让**最后一片**带上全量总数，
    /// 使「全部收完」这一刻的比较与单包时完全一致。
    /// </summary>
    public static void PushAllItems(ClientSession session, Player player)
    {
        int total = player.Items.Count;
        var chunks = new List<ItemNtf>();
        var current = new ItemNtf();
        int currentBytes = 0;

        foreach (var it in player.Items)
        {
            var info = BuildItemInfo(it);
            // 估算这一条的字节数：protobuf 嵌套字段 = tag(1~2) + varint 长度(1~2) + 消息体
            int size = info.ComputeSize() + 4;

            if (current.Items.Count > 0 && currentBytes + size > ItemNtfChunkBudget)
            {
                chunks.Add(current);
                current = new ItemNtf();
                currentBytes = 0;
            }

            current.Items.Add(info);
            currentBytes += size;
        }
        chunks.Add(current);

        for (int i = 0; i < chunks.Count; i++)
        {
            // ★ 只有最后一片带全量总数（见方法注释）
            chunks[i].Itemnum = i == chunks.Count - 1 ? total : 0;
            session.Push(GamePf.ItemNtf, chunks[i]);
        }

        if (chunks.Count > 1)
        {
            Log.Info($"[session {session.Id}] pf 1104 全量背包 {total} 件分 {chunks.Count} 片下发" +
                     $"（单片预算 {ItemNtfChunkBudget} 字节；客户端单次 Socket.Receive 上限 0x10000）");
        }
    }

    /// <summary>
    /// 单条 pf 1104 的**物品字节预算**。
    ///
    /// 客户端单次 <c>Socket.Receive</c> 上限是 <c>0x10000</c> = 65536，
    /// 减去 16 字节封包头、<c>itemnum</c> 字段，以及 3DES 加密后的 PKCS7 padding，
    /// 这里取 **48 KiB** 留足余量（同一帧里还可能夹着别的包，例如 pf 1026/1031）。
    /// ★ 不要贪心贴着 65536：一帧内多个包**共享**这一次 Receive 的 65536 字节。
    /// </summary>
    public const int ItemNtfChunkBudget = 48 * 1024;

    /// <summary>
    /// 推一条 pf 1030 <c>GirlDataNtf</c>（客户端 <c>Game.Player.OnGirlUpdate</c>）。
    /// 消息体是**单个** <c>GirlInfo</c>，整体覆盖该 girlid 的本地记录。
    /// </summary>
    public static void PushGirl(ClientSession session, Girl girl)
    {
        session.Push(GamePf.GirlDataNtf, new GirlDataNtf
        {
            Girl = BuildGirlInfo(girl),
        });
    }

    /// <summary>
    /// 推 pf 1029 <c>PlayerDataNtf</c>：等级 + 经验（顺序固定「先 level 后 exp」，
    /// 因为只有 level 那一条会触发客户端的升级表现，见 <see cref="PlayerProgress"/>）。
    /// </summary>
    public static void PushLevelExp(ClientSession session, Player player)
        => session.Push(GamePf.PlayerDataNtf, PlayerProgress.BuildLevelExpNtf(player));

    /// <summary>推 pf 1029 的单条 (updatetype, updatevalue)（头像框等）。</summary>
    public static void PushPlayerData(ClientSession session, int updateType, long value)
    {
        var ntf = new PlayerDataNtf();
        ntf.Updatetype.Add(updateType);
        ntf.Updatevalue.Add(value);
        session.Push(GamePf.PlayerDataNtf, ntf);
    }

    /// <summary>
    /// 推一条 pf 1034 <c>ChapterUpdateNtf</c>（客户端 <c>Game.Player.OnChapterChange</c> →
    /// <c>Game.MapList.ReflashLevelList(mapId, star)</c>）。
    /// <c>star</c> 必须是**完整位域**（三星 bit0..2 | 通关次数 &lt;&lt; 3），因为客户端是整值覆盖。
    /// </summary>
    public static void PushChapter(ClientSession session, uint mapId, int star)
    {
        var ntf = new ChapterUpdateNtf();
        ntf.Info.Add(new Level { Id = mapId, Star = star });
        session.Push(GamePf.ChapterUpdateNtf, ntf);
    }

    /// <summary>批量推关卡进度（一条包里多个 Level）。</summary>
    public static void PushChapters(ClientSession session, IEnumerable<KeyValuePair<uint, int>> levels)
    {
        var ntf = new ChapterUpdateNtf();
        int n = 0;
        foreach (var (mapId, star) in levels)
        {
            ntf.Info.Add(new Level { Id = mapId, Star = star });
            n++;
        }
        if (n > 0)
            session.Push(GamePf.ChapterUpdateNtf, ntf);
    }

    /// <summary>
    /// 推 pf 1026 <c>TaskValueSyncRsp</c> 的**增量**任务值。
    /// 客户端 <c>Game.Player.OnTaskValueSyncRsp</c>（RVA 0x1852B18）逐条
    /// <c>ContainsKey</c> → <c>set_Item</c>/<c>Add</c>，**不清表**，所以只推变化的键即可。
    /// 键是 <c>(group &lt;&lt; 16) | taskId</c>（<see cref="Player.ComposeTaskId"/>）。
    /// </summary>
    public static void PushTaskValues(ClientSession session, IEnumerable<KeyValuePair<uint, int>> values)
    {
        var rsp = new TaskValueSyncRsp();
        int n = 0;
        foreach (var (id, value) in values)
        {
            rsp.Arr.Add(new TaskInt { Id = id, Value = value });
            n++;
        }
        if (n > 0)
            session.Push(GamePf.TaskValueSyncRsp, rsp);
    }

    /// <summary>推玩家全部任务值（登录时用）。</summary>
    public static void PushAllTaskValues(ClientSession session, Player player)
        => PushTaskValues(session, player.Tasks.OrderBy(kv => kv.Key));

    /// <summary>
    /// 推一条 pf 1033 <c>FormationInfoUpdateNtf</c>（客户端 <c>Game.EquipManager.UpdateFormationNtf</c>）。
    /// 消息体是单个 <c>FormationInfo</c>，整体覆盖该编队 id。
    /// </summary>
    public static void PushFormation(ClientSession session, Formation formation)
    {
        session.Push(GamePf.FormationInfoUpdateNtf, new FormationInfoUpdateNtf
        {
            Info = Handlers.FormationHandler.BuildFormationInfo(formation),
        });
    }

    /// <summary>
    /// 把一次发奖的结果一把梭推给客户端：先全部货币（pf 1032，逐种一条），
    /// 再一条 pf 1031 带上所有变动的背包实例，最后一条 pf 1026 带上顺带改到的任务值。
    ///
    /// 顺序理由：客户端的奖励弹窗常按「货币先到位」渲染数值；而背包实例的 upsert
    /// 与货币互不依赖，放后面能让一条 1031 覆盖全部实例（少一次封包）。
    /// ★ 任务值放最后：目前唯一来源是皮肤道具解锁服饰（group 4），
    /// 客户端要先有背包实例再读标记才自然（见 <see cref="RewardGrantResult.ChangedTasks"/>）。
    /// </summary>
    public static void PushRewards(ClientSession session, Player player, RewardGrantResult granted)
    {
        if (granted.IsEmpty)
            return;
        foreach (var girl in granted.ChangedGirls) PushGirl(session, girl);
        PushMoney(session, player, granted.ChangedMoney);
        PushItems(session, granted.ChangedItems);
        foreach (uint id in granted.ChangedRiki)
            session.Push(SssjServer.Protocol.Pf.RikiData, new RikiData { Rikiid = id });
          if (granted.ChangedFurniture.Count > 0)
          {
              var response = new AddFornituresRsp { Roleid = player.Roleid };
              response.NewFurnitures.AddRange(granted.ChangedFurniture.Select(Handlers.HouseFeature.BuildFurniture));
              session.Push(SssjServer.Protocol.Pf.AddFornituresRsp, response);
          }
        if (granted.ChangedTasks.Count > 0)
            PushTaskValues(session, granted.ChangedTasks);
    }

    /// <summary>
    /// <see cref="Item"/> → proto <c>ItemInfo</c>。
    /// <c>enhancelevel</c> 就是界面上的 "LV.x"（取证 <c>FormationCommon.lua:621</c>）；
    /// <c>lockon</c>/<c>flag</c> 的位语义见 <see cref="Item.LockOn"/> / <see cref="Item.Flag"/>。
    ///
    /// <para><b>★★ 装备类四字段（<c>skillinfo</c>/<c>weaponinlay</c>/<c>attrlist</c>/<c>ownguid</c>）</b></para>
    /// 现在**会下发**（此前是留空）。完整取证见 <c>work/weapon-genre-forensics.md</c>
    /// 与 <c>work/weapon-mainsession.md</c>（主会话独立反汇编复核）。
    /// 机制：客户端 <c>AddWeapon</c>(0x1D96FEC) / <c>AddOriginal</c>(0x1D99094) /
    /// <c>AddItem</c>(0x1D9A82C) 在填回前**无条件**清空目标数组，只从这些字段填回。
    /// ⇒ 判据不是「必须发」而是「**发的必须与当前状态一致**」，见 <see cref="Item.Skills"/>。
    ///
    /// <para><b>★★★ <c>attrlist</c> 走 <see cref="Item.NormalizedAttrs"/>（恒 6 元素）</b></para>
    /// <c>AddOriginal</c> 的两个取值循环是**固定次数、无 <c>Count</c> 检查、无 <c>has_</c> 保护</c>
    /// （<c>0x1D99AB8 cmp w23,#3</c> + <c>0x1D99E7C cmp w21,#6</c>）⇒ 少于 6 个元素会抛
    /// <c>ArgumentOutOfRangeException</c>，**该包后续物品全部丢失**。这是 genre 4
    /// 此前「一发就崩」的真因。
    ///
    /// <para><b>非装备 genre 一律不带这四个字段</b></para>
    /// genre 5(道具) 分支只读 6 个标量、无任何 Clear（取证 <c>0x1D9B168..0x1D9B2BC</c>），
    /// genre 1(角色卡) 走 <c>AddCharacterCard</c> 另一套（<c>syncrate</c>/<c>refitnum</c>）。
    /// 给它们带上是无害但无意义的字节，故按 <see cref="Item.IsEquipGenre"/> 门控。
    /// </summary>
    public static ItemInfo BuildItemInfo(Item it)
    {
        var info = new ItemInfo
        {
            Guid = it.Guid,
            Genre = it.Genre,
            Detail = it.Detail,
            Particular = it.Particular,
            Level = it.Level,
            Count = it.Count,
            Createtime = it.CreateTime,
            Enhancelevel = it.EnhanceLevel,
            Enhanceexp = it.EnhanceExp,
            Breaklevel = it.BreakLevel,
            Lockon = it.LockOn,
            Flag = it.Flag,
            // ★★ 这两个必须显式写出（哪怕是 0）：客户端 AddCharacterCard（RVA 0x1D95368）
            //   在 0x1D957BC get_syncrate / 0x1D957F8 get_refitnum 处**无条件**读并 set，
            //   漏了等于「每次推背包就把同步率与改造序号清零」——
            //   与 docs/05 §0.3 那条红线同族，但那张表列的是 genre 2/3/4 的字段，
            //   这两个是**角色卡(genre 1)** 的。见 Item.SyncRate / Item.RefitNum。
            Syncrate = it.SyncRate,
            Refitnum = it.RefitNum,
        };

        // Character cards match MiniSkill by configured skill ID (AddCharacterCard
        // 0x1D95A4C..0x1D95D5C), unlike weapon/rune skilltype dispatch.
        foreach (var s in it.Genre == 1 ? it.Skills : Enumerable.Empty<ItemSkill>())
            info.Skillinfo.Add(new MiniSkill { Skillid = s.SkillId, Skilllevel = s.SkillLevel, Skilltype = s.SkillType });
        if (!it.IsEquipGenre)
            return info;

        // ---- 以下只对 genre 2/3/4 下发 ----

        // skillinfo(11)：三种 genre 的 skilltype 语义不同，但下发结构一致。
        foreach (var s in it.Skills)
        {
            info.Skillinfo.Add(new MiniSkill
            {
                Skillid = s.SkillId,
                Skilllevel = s.SkillLevel,
                Skilltype = s.SkillType,
            });
        }

        // weaponinlay(15)：只有武器有意义；单值子消息（不是 repeated）。
        // 空槽用 0 表示，与客户端 has_weaponinlay 为假时自己写的 0 一致（0x1D979F8）。
        if (it.Genre == EquipGenreWeapon && it.Inlay is { } inlay)
        {
            info.Weaponinlay = new WeaponRune
            {
                Barrel = inlay.Barrel,
                Ammo = inlay.Ammo,
                Sight = inlay.Sight,
                Grip = inlay.Grip,
            };
        }

        // attrlist(16) + ownguid(17)：只有零件有意义。
        // ★ attrlist 必须恰好 6 个元素，见方法注释与 Item.Attrs。
        if (it.Genre == EquipGenreOriginal)
        {
            foreach (var a in it.NormalizedAttrs())
            {
                info.Attrlist.Add(new BaseAttribute
                {
                    Atype = a.AType,
                    Avalue = a.AValue,
                });
            }
            info.Ownguid = it.OwnGuid;
        }

        return info;
    }

    /// <summary>genre 2 = 武器（<c>emGenreType.Weapon</c>，<c>dump.cs:372492..372502</c>）。</summary>
    public const uint EquipGenreWeapon = 2;

    /// <summary>genre 3 = 符文（<c>emGenreType.Rune</c>）。</summary>
    public const uint EquipGenreRune = 3;

    /// <summary>genre 4 = 零件（<c>emGenreType.Original</c>）。</summary>
    public const uint EquipGenreOriginal = 4;

    /// <summary><see cref="Girl"/> → proto <c>GirlInfo</c>。</summary>
    public static GirlInfo BuildGirlInfo(Girl g) => new()
    {
        Girlid = g.Girlid,
        Level = g.Level,
        Exp = g.Exp,
        Modelid = g.Modelid,
        Moodvalue = g.Moodvalue,
        Vigor = g.Vigor,
        Flag = g.Flag,
    };
}
