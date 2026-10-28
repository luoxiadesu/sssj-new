using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using SssjServer.Protocol;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game;

/// <summary>
/// Game 层唯一入口：注册全部 pf / LuaCall 处理函数，并注入 Core 的 NtfS2CCallSender 接缝。
/// 登录链路实现严格按 docs/03-login-flow.md（pf ↔ 客户端处理函数、时序、字段要求）。
/// </summary>
public sealed class GameModule
{
    private readonly ServerOptions _options;
    private readonly PlayerStore _store;
    private readonly World _world;
    private readonly ChapterTable _chapters;
    private readonly PlayerExpTable _playerExp;
    private readonly ItemBaseTable _itemTemplates;
    private readonly CharacterCardTable _characterCards;
    private readonly MissionTable _missions;
    private readonly GuideTable _guides;
    private readonly MissingIconTable _missingIcons;
    private readonly RewardService _rewards;
    private readonly GmUnlock _gmUnlock;
    private readonly HashSet<int> _handledLuaCall = new();
    private readonly List<PlayerLoginHook> _loginHooks = new();
    private readonly List<PlayerLoginHook> _prepareHooks = new();
    private readonly List<IGameFeature> _features = new();
    private LuaCallRouter _luaCalls = null!;
    private ChapterHandler _chapterHandler = null!;
    private FormationHandler _formationHandler = null!;

    /// <summary>
    /// 跑一遍功能域的登录钩子（<c>ctx.OnPlayerLogin</c> 注册的那些）。
    ///
    /// <para><b>★ 存在的理由：给单测用</b></para>
    /// <c>FeatureHarness</c> 那套测试脚手架**不走真正的登录流程**
    /// （只 <c>GetOrCreatePlayer</c> + 设 <c>session.Tag</c>），所以登录钩子从不触发。
    /// 结果是「靠登录钩子主动推数据」的功能域 —— 例如邮件 pf 1014（客户端**从不索要**列表）
    /// —— **在单测里完全测不到**，只能靠真机验，回归风险大。
    ///
    /// 这里复刻生产路径（<see cref="Install"/> 末尾第 ⑥ 步）的行为，
    /// 包含**单个钩子异常不影响其它钩子**这一条 —— 否则测试环境与线上行为不一致。
    /// </summary>
    internal void RunLoginHooksForTest(ClientSession session, Player player)
    {
        foreach (var hook in _loginHooks)
        {
            try
            {
                hook(session, player);
            }
            catch (Exception e)
            {
                Log.Error($"[session {session.Id}] 登录钩子异常：{e}");
            }
        }
    }

    /// <summary>
    /// 触发全部「玩家数据准备」钩子（<see cref="GameFeatureContext.OnPlayerPrepare"/>），单测用。
    ///
    /// ★ 与 <see cref="RunLoginHooksForTest"/> 同理，但这一批钩子跑在 **pf 1005 之前**，
    /// 用途是「改登录时本来就要全量下发的数据」（货币 / 背包 / 任务值 / 少女）。
    /// 少女剧情域的 <c>ELevel</c> 初值就挂在这里 —— 没有这个入口就只能靠真机验。
    /// 同样保证「单个钩子异常不影响其它钩子」，与生产路径一致。
    /// </summary>
    internal void RunPrepareHooksForTest(ClientSession session, Player player)
    {
        foreach (var hook in _prepareHooks)
        {
            try
            {
                hook(session, player);
            }
            catch (Exception e)
            {
                Log.Error($"[session {session.Id}] 数据准备钩子异常：{e}");
            }
        }
    }

    public GameModule(ServerOptions options)
    {
        _options = options;
        _store = new PlayerStore(options);
        string settingDir = Path.GetFullPath(options.SettingDirectory);
        _chapters = ChapterTable.Load(settingDir);
        _playerExp = PlayerExpTable.Load(settingDir);
        // ItemList.txt 显式按 --setting-dir 载入（**不要**去改 ItemBaseTable.DefaultDirectory：
        // 那是个全局静态，一旦被某个实例写坏，同进程里其它代码/单测的
        // ResolveSettingDirectory() 会跟着读到错目录 —— 实测踩过，表现为
        // 「分解不返金币」这种静默降级。每个 GameModule 自己持有一份表。）
        _itemTemplates = ItemBaseTable.Load(settingDir);
        // CharacterCard.txt 同样是从 setting.u 解出来的（见 CharacterCardTable 的取证注释）。
        _characterCards = CharacterCardTable.Load(settingDir);
        // 任务/成就八张表在这里加载一次，MissionFeature 与 GmUnlock 共用（原先 MissionFeature
        // 自己 Load 一份，AchievementOther.txt 1857 行会被读两遍）。
        _missions = MissionTable.Load(settingDir);
        // UIGuide.txt：GM 全解锁要把 114 条新手指引标记为已完成（group 10 = 100）。
        // 不做这一步，GM 账号登录后仍会触发引导剧情，而事后用客户端的
        // GuideManager:DebugSetGuideComplete() 去关会把 UI 栈打空（见 GuideTable 的注释）。
        _guides = GuideTable.Load(settingDir);
        // MissingIcon.txt：本地资源快照缺失的道具图标清单（tools/probe_icons.sh 生成）。
        // GM 全解锁用它跳过并清理会画成问号的道具，见 MissingIconTable 的取证注释。
        _missingIcons = MissingIconTable.Load(settingDir);
        _rewards = new RewardService(_itemTemplates);
        // ★★★ ItemBoxInfo.txt：礼包（ClassName = IBGiftAuto）的箱内内容。
        // 挂上它之后 RewardService 会把礼包**就地展开**成箱内 Fix 物品，
        // 而不是把礼包本体塞进背包 —— 这是「十面妆造买了皮肤却什么都没得到」的修法
        // （皮肤本体是 genre 19，藏在箱子里；取证 IBLogic.lua:1196 与 ItemBoxTable 类注释 §0）。
        _rewards.Boxes = ItemBoxTable.Load(settingDir);
        // ★ SpineSuit.txt：让「发放 genre 19 皮肤道具」顺带解锁 group 4 的服饰标记。
        // 与上面的 Boxes 是同一条链的两环（礼包→皮肤道具→服饰可穿），缺一环都是「钱花了没东西」。
        _rewards.Suits = GirlDressTable.Load(settingDir);
        _rewards.CharacterCards = _characterCards;
        _rewards.Missions = _missions;
        _rewards.RikiCards = RikiCardTable.Load(settingDir);
        _rewards.Furniture = HouseTable.Load(settingDir);
        _gmUnlock = new GmUnlock(options, _chapters, _itemTemplates, _characterCards, _missions,
                                 _guides, _playerExp, _missingIcons);
        _world = new World(_store, options, _playerExp);
    }

    public World World => _world;
    public PlayerStore Store => _store;

    /// <summary>Chapter.txt 主线关卡表（体力消耗 / 通关经验 / 奖励）。</summary>
    public ChapterTable Chapters => _chapters;

    /// <summary>ItemList.txt 物品模板表（发奖 / 卖价 / 货币折算）。</summary>
    public ItemBaseTable ItemTemplates => _itemTemplates;

    /// <summary>CharacterCard.txt 角色卡模板表（269 张）。</summary>
    public CharacterCardTable CharacterCards => _characterCards;

    /// <summary>
    /// 已成功装配的功能域实例（装配顺序即 <c>GameFeatureLoader.Discover()</c> 顺序）。
    /// 用于单测取具体 feature、以及运维诊断「到底装上了哪些域」。
    /// 装配抛异常的那个**不会**进这个列表。
    /// </summary>
    public IReadOnlyList<IGameFeature> Features => _features;

    /// <summary>按类型取已装配的功能域；没装上返回 null。</summary>
    public T? FindFeature<T>() where T : class, IGameFeature
        => _features.OfType<T>().FirstOrDefault();

    /// <summary>任务/成就八张配置表（MissionFeature 与 GmUnlock 共用）。</summary>
    public MissionTable Missions => _missions;

    /// <summary>全服共用的奖励发放服务。</summary>
    public RewardService Rewards => _rewards;

    /// <summary>GM 测试账号全解锁服务。</summary>
    public GmUnlock GmUnlock => _gmUnlock;

    /// <summary>
    /// 注册所有处理函数：
    ///  * LuaCallRouter.NtfS2CCallSender —— 把 sJsonParam 包成真实 GsProto.NtfS2CCall 推在 pf 1024
    ///    （否则 Core 会退回“原样推 raw 字节”，不是合法 protobuf）。
    ///  * pf 处理器：1102/1001/1008/1105/1048/1109/1022。
    /// </summary>
    public void Install(PfRouter router, LuaCallRouter luaCalls)
    {
        _luaCalls = luaCalls;

        luaCalls.NtfS2CCallSender = (session, function, sJsonParam) =>
        {
            var ntf = new NtfS2CCall
            {
                SFunction = function,
                SJsonParam = Encoding.UTF8.GetString(sJsonParam),
            };
            session.Push(GamePf.NtfS2CCall, ntf);
            return Task.CompletedTask;
        };

        router.Map(GamePf.Verify,          (s, p) => HandleVerify(new SessionAdapter(s), p));
        router.Map(GamePf.Login,           (s, p) => HandleLogin(new SessionAdapter(s), p));
        router.Map(GamePf.Alive,           (s, p) => HandleKeepAlive(new SessionAdapter(s), p));
        router.Map(GamePf.Time,            (s, p) => HandleTimeSync(new SessionAdapter(s), p));
        router.Map(GamePf.GetHouseInfo,    (s, p) => HandleGetHouseInfo(new SessionAdapter(s), p));
        router.Map(GamePf.HouseRandomList, (s, p) => HandleHouseRandomList(new SessionAdapter(s), p));
        router.Map(GamePf.C2SCall,         (s, p) => HandleC2SCall(new SessionAdapter(s), p));
        router.Map(GamePf.Rename,          (s, p) => HandleRename(new SessionAdapter(s), p));
        router.Map(GamePf.TaskValueChangeReq, (s, p) => HandleTaskValueChange(new SessionAdapter(s), p));

        // 进主城第一波唯一需要的服务器应答（docs/03-login-flow.md §7）：
        // Club_ReqClubInfo → 回空对象即“没有社团”的合法答案。
        // 依据 ClubCommon.lua:452：if not club or not club.primary then pFuncNoClub()。
        RegisterLuaCall(10002, (session, cmd, param) =>
        {
            _luaCalls.CallClient(session, 10002, new { });
            return Task.CompletedTask;
        });

        // 客户端高频 fire-and-forget 的指引日志（WriteGuideLog 指引日志）：
        // 只收不发应答（除 C2SCallRsp 的统一 ack 外），避免刷 WARN。
        // 其余未注册 LuaCall 仍走 C2SCall 里的“带名称表”增强告警。
        RegisterLuaCall(102, (session, cmd, param) =>
        {
            Log.Debug($"[session {session.Id}] LuaCall cmd=102 (WriteGuideLog 指引日志) param={param}");
            return Task.CompletedTask;
        });

        // ---- 主线关卡：ChapterMsg（进关 / 结算 / 章节奖励）----
        // ① 进关与章节奖励是 sFunction="ChapterMsg" 的字符串命令（Lib.lua:378 SendCmd）
        // ② 通关结算被 LuaCall.DoSettle(252) 包了一层（GameManager.SendInfoToServer）
        _chapterHandler = new ChapterHandler(_store, luaCalls, _chapters, _options.SpendVigour, _rewards,
            cafe: CafeTable.Load(Path.GetFullPath(_options.SettingDirectory)),
            girlLove: GirlLoveTable.Load(Path.GetFullPath(_options.SettingDirectory)), playerExp: _playerExp, cards: CardTable.Load(Path.GetFullPath(_options.SettingDirectory)), cardTemplates: _characterCards, missions: _missions, pledge: PledgeTable.Load(Path.GetFullPath(_options.SettingDirectory)));
        luaCalls.MapFunction(ChapterHandler.Function, _chapterHandler.OnScriptCall);

        // ---- 编队保存：出击流程的第一道服务器闸门（UI_Formation:ApplySave 等回包）----
        _formationHandler = new FormationHandler(_store, luaCalls);
        RegisterLuaCall(FormationHandler.UpdateCmd, _formationHandler.OnUpdate);
        RegisterLuaCall(FormationHandler.ChangeNameCmd, _formationHandler.OnChangeName);

        RegisterLuaCall(ChapterHandler.DoSettleCmd, (session, cmd, param) =>
        {
            // DoSettle 的 tbParam 形如 { sCmd = <内层命令>, tbParam = {...} }
            if (param is not JsonObject envelope) return Task.CompletedTask;
            var innerCmd = envelope["sCmd"];
            var innerParam = envelope["tbParam"];
            string? name = innerCmd?.GetValueKind() == JsonValueKind.String ? innerCmd.GetValue<string>() : null;

            if (innerCmd is JsonValue numeric && numeric.TryGetValue<int>(out int settlementCmd) && settlementCmd is 51 or 52 or 55 or 56)
                return luaCalls.DispatchSettlementAsync(session, settlementCmd, innerParam);

            if (string.Equals(name, ChapterHandler.Function, StringComparison.Ordinal))
                return _chapterHandler.OnSettle(session, innerParam);

            Log.Warn($"[session {session.Id}] DoSettle(252) 内层命令未实现 sCmd={innerCmd?.ToJsonString() ?? "nil"} " +
                     $"param={innerParam?.ToJsonString()}");
            return Task.CompletedTask;
        });

        // ---- 功能模块（反射装配）----
        // 每个功能域一个 IGameFeature 实现（Handlers/*Feature.cs），自己声明处理哪些命令，
        // 便于多人/多 agent 并行开发时互不冲突。见 GameFeature.cs 的说明。
        var ctx = new GameFeatureContext(
            _options, _store, _world, luaCalls, router, _chapters, _playerExp,
            _itemTemplates, _characterCards, _missions, _rewards, _gmUnlock,
            RegisterLuaCall, luaCalls.MapFunction, _loginHooks.Add, _prepareHooks.Add);
        foreach (var feature in GameFeatureLoader.Discover())
        {
            try
            {
                feature.Install(ctx);
                _features.Add(feature);
                Log.Info($"[feature] 装配 {feature.Name}");
            }
            catch (Exception e)
            {
                Log.Error($"[feature] 装配 {feature.Name} 失败：{e}");
            }
        }
    }

    // ---- 1102 Verify（明文） ----
    internal Task HandleVerify(IGameSession session, Packet packet)
    {
        var req = TryParse<VerifyReq>(session, packet);
        if (req is null)
            return Task.CompletedTask;

        Log.Info($"[session {session.Id}] VerifyReq platform={req.Platform} account={req.Account}");

        var player = _world.GetOrCreatePlayer(req.Account);
        string clientKey = NewClientKey();

        var rsp = new VerifyRsp
        {
            Roleid = player.Roleid,
            Aid = _options.Aid,
            Sid = _options.Sid,
            Account = req.Account,
            Optime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ClientKey = clientKey,
            Retid = 0,
            Createrole = false,
        };

        // 必须先发出（明文）VerifyRsp，客户端据此 Init client_key，之后才启用 3DES
        session.Send(GamePf.Verify, packet.Serial, rsp);
        session.EnableCrypto(clientKey);
        return Task.CompletedTask;
    }

    // ---- 1001 Login → 1002 LoginRsp + 1005 PlayerNtf（登录链路关键） ----
    internal Task HandleLogin(IGameSession session, Packet packet)
    {
        var req = TryParse<LoginReq>(session, packet);
        if (req is null)
            return Task.CompletedTask;

        var player = _world.Login(session, req.Account);
        Log.Info($"[session {session.Id}] LoginReq account={req.Account} roleid={player.Roleid}");

        // ⓪ 功能域的「数据准备」钩子（ctx.OnPlayerPrepare 注册）——★ 必须在 1005 之前。
        //    这里改的是「登录时本来就会全量下发的那部分数据」（货币/背包/关卡/任务值/少女），
        //    改完之后下面四条包自然带上，**不需要任何增量推送**。
        //    GM 全解锁（GmProvisionFeature）就挂在这里：2300 件物品 + 3900 条任务值
        //    若放到 OnPlayerLogin 里做，就得额外推几十 KB 的 pf 1031/1026。
        if (_prepareHooks.Count > 0 && session is SessionAdapter prepareAdapter)
        {
            foreach (var hook in _prepareHooks)
            {
                try
                {
                    hook(prepareAdapter.Inner, player);
                }
                catch (Exception e)
                {
                    Log.Error($"[session {session.Id}] 登录准备钩子异常：{e}");
                }
            }
        }

        // Completed-tutorial saves with a persisted name must not re-enter initial naming.
        if (player.GetTask(1, 15) >= 6 && player.GetTask(1, 14) == 0 &&
            !string.IsNullOrWhiteSpace(player.Name) && player.Name != player.Account)
        {
            player.SetTask(1, 14, 1);
            _store.Save(player);
        }

        // 顺序很关键（实测得出）：pf=1005 必须先发。
        // pf=1002 的 Game.Login.PlayerLogineRsp 会调用 OnEnterGame()，
        // 而 OnEnterGame 读 Game.Player.me（account/id/name/registerTime）建 XGSDK RoleInfo；
        // Player.me 是 pf=1005 的 PlayerLogined → Game.Player.Login(ntf) 里才赋值的。
        // 先发 1002 会让客户端抛 NullReferenceException：
        //   Failed to parse network message : Object reference not set to an instance of an object.
        //     at Game.Login.OnEnterGame ()
        //     at Game.Login.PlayerLogineRsp (...)
        //   Failed to process message. PF : 1002

        // ① pf=1005（serial=0）：Game.Login.PlayerLogined → Game.Player.Login(ntf) 建立 me
        //    → Game.Lua.Call("__init") 进游戏
        var ntf = BuildPlayerNtf(player);
        session.Push(GamePf.PlayerNtf, ntf);

        // ② ★ pf=1026（serial=0）「教程闸门」小包 —— 必须紧跟 1005，且必须在 1104 之前。
        //    见 BuildTutorialGateSyncRsp 的取证注释与 work/tutorial-replay-fix.md：
        //    客户端在 1005 之后约 300ms（UpdateEndNode.OnEnter → PlayerLogic.OnUpdateEnd）
        //    读 GetTaskValue(1,15)/(1,14) 决定「进主城还是重播新手剧情」，
        //    而 65KB 的 pf 1104 会把排在它后面的 1026 推迟 ~230ms —— 正好压在判定线上。
        //    这条小包只有 2 条任务值（约 20 字节），保证与 1005 同一批到达。
        var gate = BuildTutorialGateSyncRsp(player);
        if (gate is not null)
            session.Push(GamePf.TaskValueSyncRsp, gate);

        // ③ pf=1002 + 请求 serial：触发 Game.Login.PlayerLogineRsp → OnEnterGame()（此时 me 已就绪）
        var loginRsp = new LoginRsp
        {
            Retid = 0,
            Optime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // Strret 不设，避免走提示分支
        };
        session.Send(GamePf.LoginRsp, packet.Serial, loginRsp);

        // ④ pf=1026（serial=0）TaskValueSyncRsp：登录后推送当前全部任务值。
        //    客户端 handler 是 Game.Player.OnTaskValueSyncRsp（pf-table.txt 只 Register、无 Send）。
        //    携带全部存盘任务；首次建号且 SkipTutorial 时会含种子值 (1,15)→6。
        //    ★ 必须排在 ⑤ pf=1104 之前（原先在它之后）：1104 是全链路最大的一个包
        //    （GM 账号 2318 件物品 = 65009 字节），客户端 UNet.Connection.Update() 每帧只做
        //    一次 Socket.Receive（上限 0x10000，RVA 0x1B4D3E8）、再调一次 Net.Dispatch()，
        //    所以排在 1104 后面的包会被整整推迟一个「收 64KB + 解析 2318 件物品」的时间。
        //    真机实测：A(1002 之后)=+31ms，B(1104+1026 之后)=+261ms，而判定线在 +300~324ms。
        session.Push(GamePf.TaskValueSyncRsp, BuildTaskValueSyncRsp(player));

        // ⑤ pf=1104（serial=0）ItemNtf：推送全量背包。客户端 handler Game.Login.ItemNtfMsg
        //    是纯服务端推送（pf-table.txt 只 Register、无 Send）。itemnum = 物品总数。
        //    放在最后：它体积最大且没人在登录早期依赖它 ——
        //    `EquipManager.Init()` 是在 1005 的 Game.Player.Login 里同步跑的，
        //    无论 1104 排第几它都读不到（原注释「让 EquipManager.Init() 已能读到全部 item」
        //    是错的，实测 1104 永远晚于 EquipManager.Init）；界面要用背包时已是 1 秒之后。
        //
        //    ★★★ 第九轮修：改走 GameNotify.PushAllItems，而不是自己 Push 一个大包 ——
        //    那边会按客户端单次 Socket.Receive 的上限（0x10000）**自动分片**。
        //    此前这里一条 68122 字节的包会让**尾部 20 件物品静默丢失**：真机实测
        //    存档里 18 把武器，客户端 GetWeaponCount() 只有 3，且 logcat 零报错。
        //    取证与回归闸见 docs/05 §36 与 ItemNtfChunkingTests。
        //    ★ 分片需要真的 ClientSession（GameNotify 的签名）；非 adapter 的场景
        //    （单测里的假会话）退回单包，行为与修复前一致、不影响那些用例。
        if (session is SessionAdapter itemAdapter)
            GameNotify.PushAllItems(itemAdapter.Inner, player);
        else
            session.Push(GamePf.ItemNtf, BuildItemNtf(player));

        // ⑥ 功能域的登录钩子（ctx.OnPlayerLogin 注册）。
        //    放在五条基础包之后：此时客户端 Game.Player.me / 背包 / 任务值都已建立，
        //    功能域可以安全地推自己那部分增量（pf 1026/1031/1032/1034 …）。
        //    单个钩子异常不影响其它钩子与登录流程本身。
        if (_loginHooks.Count > 0 && session is SessionAdapter adapter)
        {
            foreach (var hook in _loginHooks)
            {
                try
                {
                    hook(adapter.Inner, player);
                }
                catch (Exception e)
                {
                    Log.Error($"[session {session.Id}] 登录钩子异常：{e}");
                }
            }
        }
        return Task.CompletedTask;
    }

    // ---- 1006 建立角色名（Game.Player.SendRename）→ 1006 RenameRsp（空消息，按 serial 回调） ----
    // 客户端 pf=1006 无 dispatcher，只注册了 serial 回调（docs/pf-table.txt），所以回包必须用
    // 相同 pf + 请求 serial，空 body 即可。它只在乎回调触发，不在乎内容。
    internal Task HandleRename(IGameSession session, Packet packet)
    {
        var req = TryParse<RenameReq>(session, packet);
        var player = session.Tag as Player;
        if (req is null || player is null || !req.HasStrName || string.IsNullOrWhiteSpace(req.StrName))
        {
            // 非法/找不到玩家：回空 RenameRsp（客户端回调仍会触发；名称未改）。
            // 成功路径不在这里 —— 空字符串名拒绝写入。
            session.Send(GamePf.RenameRsp, packet.Serial, new RenameRsp());
            return Task.CompletedTask;
        }

        // 正常化名称：去除首尾空白，接受其余原样
        string name = req.StrName.Trim();
        if (name.Length == 0)
        {
            // 纯空白也按拒绝处理（原名保持不变）
            session.Send(GamePf.RenameRsp, packet.Serial, new RenameRsp());
            return Task.CompletedTask;
        }

        player.Name = name;
        Log.Info($"[session {session.Id}] rename roleid={player.Roleid} → name={name}");

        // 建名后置“已命名”标记 task (1,14)=1。
        // 依据 PlayerLogic.lua:516 IsFirstLevelComplete() 第二条件 GetTaskValue(1,14) > 0，
        // 源码注释「玩家进入过新手后剧情并且已经设置过名字」——(1,14) 即“已命名”标志。
        player.SetTask(1, 14, 1);

        _store.Save(player);   // 名称 + (1,14) 一并持久化

        // Publish the naming flag before the serial callback advances the tutorial UI.
        session.Push(GamePf.TaskValueSyncRsp, new TaskValueSyncRsp
        {
            Arr = { new TaskInt { Id = Player.ComposeTaskId(1, 14), Value = 1 } }
        });

        // 成功：pf=1006 + 请求 serial，空 RenameRsp（客户端仅需回调触发）
        session.Send(GamePf.RenameRsp, packet.Serial, new RenameRsp());
        return Task.CompletedTask;
    }

    // ---- 1027 任务值变更 → 1028 应答（echo 已存值） ----
    internal Task HandleTaskValueChange(IGameSession session, Packet packet)
    {
        var req = TryParse<TaskValueChangeReq>(session, packet);
        // 正常路径：登录后 World.Login 已把 Player 挂到 session.Tag。
        var player = session.Tag as Player;
        if (req is null || player is null)
        {
            // 无法解析或找不到玩家：回 1028 空包，避免客户端挂起
            session.Send(GamePf.TaskValueChangeRsp, packet.Serial, new TaskValueChangeRsp());
            return Task.CompletedTask;
        }

        var rsp = new TaskValueChangeRsp();
        foreach (var task in req.Task)
        {
            if (!task.HasId)
                continue;
            uint id = task.Id;
            int group = Player.TaskGroupOf(id);
            int taskId = Player.TaskIdOf(id);
            int value = task.HasValue ? task.Value : 0;
            player.SetTask(group, taskId, value);
            // 原始组合 id 不可读，日志按 (group/taskId) 分解
            Log.Info($"[session {session.Id}] task {group}/{taskId} = {value} (rawId={id})");
            rsp.Task.Add(new TaskInt { Id = id, Value = player.GetTask(group, taskId) });
        }
        _store.Save(player);
        session.Send(GamePf.TaskValueChangeRsp, packet.Serial, rsp);
        return Task.CompletedTask;
    }

    // ---- 1008 心跳 → 1009 KeepAlive（用 GsProto.KeepAlive，勿用 XgSdk 同名类） ----
    internal Task HandleKeepAlive(IGameSession session, Packet packet)
    {
        session.Send(GamePf.KeepAlive, packet.Serial, new KeepAlive());
        if (session is SessionAdapter adapter && session.Tag is Player player)
            foreach (var feature in _features.OfType<IGamePulseFeature>())
            {
                try { feature.OnPlayerPulse(adapter.Inner, player); }
                catch (Exception e) { Log.Warn($"[session {session.Id}] 心跳维护失败：{e.Message}"); }
            }
        return Task.CompletedTask;
    }

    // ---- 1105 时间同步 → 1106 TimeRsp ----
    internal Task HandleTimeSync(IGameSession session, Packet packet)
    {
        var rsp = new TimeRsp
        {
            Servertime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Serverzone = _options.Sid,
        };
        session.Send(GamePf.TimeRsp, packet.Serial, rsp);
        return Task.CompletedTask;
    }

    // ---- 1048 家园信息 → 1049 应答（进咖啡厅自动请求，必须有回复否则挂起） ----
    // 客户端 handler Game.NCafeNetController.RsqHouseInfos 会直接解引用两个嵌套消息，
    // 只回 roleid 会让它抛 "Failed to process message. PF : 1049"。
    // 所以这里把 HouseInfo(HouseCache) 与 Visitinfo(VisitedData) 都默认构造并置为“存在”，
    // 其内部 repeated 集合为空（客户端能安全遍历空集合）。
    internal Task HandleGetHouseInfo(IGameSession session, Packet packet)
    {
        var req = TryParse<GetHouseInfoReq>(session, packet);
        var rsp = new GetHouseInfoRsp();
        if (req is not null && req.HasRoleid)
            rsp.Roleid = req.Roleid;
        rsp.HouseInfo = new HouseCache();   // 内嵌 FurnitureList/ThemeList/RoomList/PetList 均默认空
        rsp.Visitinfo = new VisitedData();  // 内嵌 Entrustlog/Visitedlist/Tasklist 均默认空
        session.Send(GamePf.GetHouseInfoRsp, packet.Serial, rsp);
        return Task.CompletedTask;
    }

    // ---- 1109 随机家园列表 → 1110 空响应 ----
    internal Task HandleHouseRandomList(IGameSession session, Packet packet)
    {
        session.Send(GamePf.HouseRandomListRsp, packet.Serial, new HouseRandomListRsp());
        return Task.CompletedTask;
    }

    // ---- 1022 通用 RPC → 交给 LuaCallRouter + 1023 ack ----
    //
    // 两族命令共用 pf=1022（C2SCallReq{sFunction, sJsonParam}）：
    //   ① sFunction == "LuaCall"     → sJsonParam = {"sCmd":<数字>,"tbParam":{...}}（LuaCall.lua）
    //   ② sFunction == 其它字符串    → sJsonParam = tbParam 本体（Lib.lua:378 SendCmd → S2CCall.lua）
    // 二者都由 LuaCallRouter.DispatchAsync 分流；未注册的一律打增强日志。
    internal async Task HandleC2SCall(IGameSession session, Packet packet)
    {
        var req = TryParse<C2SCallReq>(session, packet);
        if (req is null)
            return;

        string function = req.HasSFunction ? req.SFunction : "";
        bool isLuaCall = string.Equals(function, LuaCallRouter.LuaCallFunction, StringComparison.Ordinal);

        // 提取 sCmd 供增强日志判断（Core 也解析一遍用于分发，这里只为名称表）
        int cmd = isLuaCall ? ParseCmdFromJson(req.SJsonParam) : -1;
        bool handled = isLuaCall ? _handledLuaCall.Contains(cmd) : _luaCalls.HasFunction(function);

        if (handled)
        {
            // 已注册：走 Core 分发（需真实 ClientSession 以便 LuaCallRouter.CallClient 推 1024）
            if (session is SessionAdapter sa)
            {
                try
                {
                    await _luaCalls.DispatchAsync(sa.Inner, function, req.SJsonParam);
                }
                catch (Exception e)
                {
                    Log.Error($"[session {session.Id}] {(isLuaCall ? $"LuaCall {cmd}" : function)} 分发异常: {e}");
                }
            }
            else
            {
                Log.Warn($"[session {session.Id}] 无法分发已注册命令（测试用无 ClientSession）sFunction={function} cmd={cmd}");
            }
        }
        else if (isLuaCall)
        {
            // 未注册：这里打“带名称表”的增强日志（推进开发的主要工具）
            Log.Warn($"[session {session.Id}] unhandled LuaCall {LuaCallNames.FormatLog(cmd, req.SJsonParam)}");
        }
        else
        {
            // 字符串脚本命令（S2CCall.lua 那一族），名字本身就是语义
            Log.Warn($"[session {session.Id}] unhandled sFunction={function} param={req.SJsonParam}");
        }

        // ack：成功 SErrMsg 为空
        var rsp = new C2SCallRsp { SErrMsg = "" };
        session.Send(GamePf.C2SCallRsp, packet.Serial, rsp);
    }

    /// <summary>注册一个 LuaCall 处理函数并记入已处理集合（供 C2SCall 的分发/增强日志判断）。</summary>
    public void RegisterLuaCall(int cmd, LuaCallHandler handler)
    {
        _luaCalls.Map(cmd, handler);
        _handledLuaCall.Add(cmd);
    }

    private static int ParseCmdFromJson(string jsonParam)
    {
        try
        {
            var root = JsonNode.Parse(jsonParam);
            return root?["sCmd"]?.GetValue<int>() ?? -1;
        }
        catch (JsonException)
        {
            return -1;
        }
    }

    // ---- Ntf 构造 ----
    /// <summary>把玩家全部任务值打包成 pf=1026 TaskValueSyncRsp（repeated TaskInt{id,value}）。</summary>
    internal TaskValueSyncRsp BuildTaskValueSyncRsp(Player player)
    {
        var rsp = new TaskValueSyncRsp();
        foreach (var (id, value) in player.Tasks.OrderBy(kv => kv.Key))
        {
            rsp.Arr.Add(new TaskInt { Id = id, Value = value });
        }
        return rsp;
    }

    /// <summary>
    /// 「教程闸门」任务值组：只含 <c>(1,15)</c> 与 <c>(1,14)</c> 两条，用于在登录下行序列的
    /// **最前面**（紧跟 pf 1005）单独推一条极小的 pf 1026。玩家两条都没有时返回 null（不发）。
    ///
    /// 为什么需要这条「重复」的小包（真机取证，详见 <c>work/tutorial-replay-fix.md</c>）：
    ///  * 判据：<c>PlayerLogic:IsFirstLevelComplete()</c>（<c>extracted/lua/PlayerLogic.lua:516</c>）
    ///    <code>
    ///    local n = me:GetTaskValue(1, 15); if n >= 6 then return true end
    ///    return me:GetTaskValue(1,14) > 0
    ///    </code>
    ///    为假 → <c>PlayerLogic.OnUpdateEnd</c>(:436) 走 <c>Map.LoadById(7)</c> 重播新手剧情
    ///    （途中还会弹改名 → 服务端收到 pf 1006）。
    ///  * 判定时刻**不在** <c>__init</c> 里：本包 <c>PackageInfo.packType = 0 (Login)</c>
    ///    ⇒ <c>Game.LuaUtils.IsPackFirstLevel()</c>（RVA 0x1D3E4D0，末尾 <c>cmp packType,#1</c>）
    ///    恒为 false ⇒ <c>DoFirstLogin</c>(:414) 的第一条分支走不到，且登录时 <c>Game.Map.id==1</c>
    ///    ⇒ 走 <c>Game.LuaUtils.GotoCheckUpdateNode()</c>（RVA 0x1D3E388 → <c>EntryMgr.Goto(5)</c>），
    ///    真正的判定发生在 <c>UpdateEndNode.OnEnter()</c> —— 真机实测在 1005 之后 **+300~324ms**。
    ///  * 而任务值只能由 pf 1026 送达（<c>PlayerNtf</c> 22 个字段里没有任务值字段）。
    ///    客户端 <c>UNet.Connection.Update()</c>（RVA 0x1B4D340）**每帧只做一次**
    ///    <c>Socket.Receive(buf, 0, 0x10000, ...)</c>（0x1B4D3E8，recvBuffer 也只有 0x10000 字节，
    ///    见 .ctor 0x1B4F4D8）再调一次 <c>Net.Dispatch()</c>；<c>Net.Dispatch</c>（0x1B4DAF8）本身
    ///    是「把缓冲区里所有完整包依次分发完」的循环（回边 0x1B4E364 → 0x1B4DBC4）。
    ///    所以决定延迟的是**排在前面的字节数**：GM 账号的 pf 1104 全量背包有 65009 字节
    ///    （2318 件物品），排在它后面的 1026 被推迟 ~230ms。
    ///  * 真机探针实测（work/cap-probe1.logcat）：
    ///    <code>
    ///    19:21:03.862 Goto: CheckUpdate            ← 1005 分发中（__init）
    ///    19:21:03.893 [PROBE] A-after1002 t15=0    ← +31ms，任务值还没到
    ///    19:21:04.123 [PROBE] B-after1026 t15=6    ← +261ms，1026 才被应用
    ///    19:21:04.166 UpdateEndNode:OnEnter()      ← +304ms 判定（这次侥幸赢了 40ms）
    ///    </code>
    ///    另一次冷启动（work/cap-repro1.logcat）判定线是 +324ms，1026 没赶上 →
    ///    <c>load map 7</c>（+341ms）重播开场剧情。**即两者只差几十毫秒，是掷硬币。**
    ///  * <c>Game.Player.OnTaskValueSyncRsp</c>（RVA 0x1852B18）是**纯增量合并**
    ///    （ContainsKey → set_Item，否则 Add；无事件、无副作用，0x1852C6C 循环体），
    ///    而任务值字典是 <c>Game.Player</c> 的**静态**字段（cctor 0x1857930 建，
    ///    <c>Game.Player.Login</c> 只 Clear 了 <c>Dictionary&lt;int,Girl&gt;</c>、不动它），
    ///    所以「先小后大」重复下发同一条任务值完全幂等、也不会被 1005 清掉。
    /// </summary>
    internal TaskValueSyncRsp? BuildTutorialGateSyncRsp(Player player)
    {
        var rsp = new TaskValueSyncRsp();
        foreach (uint id in TutorialGateTaskIds)
        {
            if (player.Tasks.TryGetValue(id, out int value))
                rsp.Arr.Add(new TaskInt { Id = id, Value = value });
        }
        return rsp.Arr.Count > 0 ? rsp : null;
    }

    /// <summary>
    /// 教程闸门任务值的组合键：<c>(group &lt;&lt; 16) | id</c>
    /// （<c>Game.Player.GetTask</c> RVA 0x184497C <c>orr w19, w19, w20, lsl #16</c>）。
    /// 65551 = (1&lt;&lt;16)|15 —— 新手关进度；65550 = (1&lt;&lt;16)|14 —— 进过新手后剧情且已设名字。
    /// </summary>
    internal static readonly uint[] TutorialGateTaskIds = { (1u << 16) | 15u, (1u << 16) | 14u };

    internal PlayerNtf BuildPlayerNtf(Player player)
    {
        var ntf = new PlayerNtf
        {
            Roleid = player.Roleid,
            Name = player.Name,
            Level = player.Level,
            Exp = player.Exp,
            Fightpower = player.Fightpower,
            Servertime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Starttime = player.StartTime,
            RegisterTime = player.RegisterTime,
            Serverzone = player.ServerZone,
            Headframe = player.HeadFrame,
            Chatchannelid = player.ChatChannelId,
            Clubid = player.ClubId,
        };
        foreach (var (id, num) in player.Money.OrderBy(kv => kv.Key))
        {
            ntf.Moneyinfo.Add(new MoneyInfo { Id = id, Curnum = num });
        }
        if (player.Girls.Count > 0)
        {
            var gl = new GirlList();
            foreach (var g in player.Girls)
            {
                gl.Girls.Add(new GirlInfo
                {
                    Girlid = g.Girlid,
                    Level = g.Level,
                    Exp = g.Exp,
                    Modelid = g.Modelid,
                    Moodvalue = g.Moodvalue,
                    Vigor = g.Vigor,
                    Flag = g.Flag,
                });
            }
            ntf.Girllist = gl;
        }
        // formationlist：每个编队 → FormationInfo{fightcard[]}，槽位字段见 FormationHandler
        foreach (var f in player.Formations)
        {
            ntf.Formationlist.Add(FormationHandler.BuildFormationInfo(f));
        }
        // levellist：关卡进度（客户端 Game.MapList.GetLevel 读的那张 Dictionary<uint,int>）。
        // id = (chapter<<16)|(index<<8)|diff，star = bit0..2 三星 | bit3..31 通关次数（见 MapList）。
        if (player.Levels.Count > 0)
        {
            var ll = new LevelList();
            foreach (var (mapId, star) in player.Levels.OrderBy(kv => kv.Key))
            {
                ll.Levels.Add(new Level { Id = mapId, Star = star });
            }
            ntf.Levellist = ll;
        }
        return ntf;
    }

    /// <summary>
    /// 把玩家全部背包打包成 pf=1104 ItemNtf（repeated ItemInfo items + itemnum）。
    /// ItemInfo 只填有证据的字段（guid/genre/detail/particular/level/count/createtime）。
    /// </summary>
    internal ItemNtf BuildItemNtf(Player player)
    {
        var ntf = new ItemNtf();
        foreach (var it in player.Items)
        {
            ntf.Items.Add(BuildItemInfo(it));
        }
        ntf.Itemnum = player.Items.Count;
        return ntf;
    }

    /// <summary>
    /// Player.Item → proto ItemInfo。只填有证据的字段：
    /// guid/genre/detail/particular/level/count/createtime + enhancelevel/enhanceexp/breaklevel
    /// （enhancelevel 就是界面上的 "LV.x"，取证 FormationCommon.lua:621）。
    /// </summary>
    internal static ItemInfo BuildItemInfo(Item it) => new()
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
    };

    private static T? TryParse<T>(IGameSession session, Packet packet) where T : ProtoMessage, new()
    {
        try
        {
            return ProtoMessage.Parse<T>(packet.Payload);
        }
        catch (Exception e)
        {
            Log.Warn($"[session {session.Id}] pf={packet.Pf} 解析 {typeof(T).Name} 失败: {e.Message}");
            return null;
        }
    }

    /// <summary>生成 32 字符大写十六进制 client_key（8 字节随机 → 16 字节 → 32 hex）。</summary>
    private static string NewClientKey()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToUpperInvariant();
}
