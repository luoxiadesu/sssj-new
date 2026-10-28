using System.Reflection;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game;

/// <summary>
/// 一个「游戏功能模块」的装配上下文：注册 pf / LuaCall / 字符串脚本命令，
/// 以及访问玩家存档、世界、配置表。
///
/// 存在的理由：LuaCall 有 354 个命令、pf 有 113 个，注册代码集中在 GameModule 会
/// ①文件巨大 ②多人（多 agent）并行开发必然冲突。所以每个功能域写成一个
/// <see cref="IGameFeature"/> 实现，自己声明要处理哪些命令，由 GameModule 反射装配。
/// </summary>
public sealed class GameFeatureContext
{
    private readonly Action<int, LuaCallHandler> _mapLuaCall;
    private readonly Action<string, ScriptCallHandler> _mapFunction;
    private readonly Action<PlayerLoginHook> _onLogin;
    private readonly Action<PlayerLoginHook> _onPrepare;
    private readonly PfRouter _pf;
    private readonly Lazy<Handlers.PledgeTimeProgress> _pledgeTimes;
    internal event Action<ClientSession, Player>? BeforeCafeDownload;
    internal void PrepareCafeDownload(ClientSession session, Player player)
        => BeforeCafeDownload?.Invoke(session, player);

    internal GameFeatureContext(
        ServerOptions options,
        PlayerStore store,
        World world,
        LuaCallRouter luaCalls,
        PfRouter pf,
        ChapterTable chapters,
        PlayerExpTable playerExp,
        ItemBaseTable itemTemplates,
        CharacterCardTable characterCards,
        MissionTable missions,
        RewardService rewards,
        GmUnlock gmUnlock,
        Action<int, LuaCallHandler> mapLuaCall,
        Action<string, ScriptCallHandler> mapFunction,
        Action<PlayerLoginHook> onLogin,
        Action<PlayerLoginHook> onPrepare)
    {
        Options = options;
        Store = store;
        World = world;
        LuaCalls = luaCalls;
        Chapters = chapters;
        PlayerExp = playerExp;
        ItemTemplates = itemTemplates;
        CharacterCards = characterCards;
        Missions = missions;
        Rewards = rewards;
        GmUnlock = gmUnlock;
        _pf = pf;
        _mapLuaCall = mapLuaCall;
        _mapFunction = mapFunction;
        _onLogin = onLogin;
        _onPrepare = onPrepare;
        _pledgeTimes = new(() => new Handlers.PledgeTimeProgress(
            PledgeTable.Load(Path.GetFullPath(options.SettingDirectory)),
            CafeTable.Load(Path.GetFullPath(options.SettingDirectory))));
    }

    public ServerOptions Options { get; }
    public PlayerStore Store { get; }
    public World World { get; }
    internal Handlers.PledgeTimeProgress PledgeTimes => _pledgeTimes.Value;

    /// <summary>LuaCall / 字符串命令路由（<c>CallClient</c> / <c>CallClientFunction</c> 用它回包）。</summary>
    public LuaCallRouter LuaCalls { get; }

    /// <summary>Chapter.txt 主线关卡表。</summary>
    public ChapterTable Chapters { get; }

    /// <summary>PlayerExp.txt 等级表（升级经验 / 体力上限）。</summary>
    public PlayerExpTable PlayerExp { get; }

    /// <summary>
    /// ItemList.txt 物品模板表（2120 条）：<c>(G,D,P,L)</c> → 名称 / 售价 / 是否自动使用 /
    /// 货币折算（<c>*CoinAuto</c> 行）。发奖、卖物品、算价格都要它。
    /// </summary>
    public ItemBaseTable ItemTemplates { get; }

    /// <summary>
    /// CharacterCard.txt 角色卡模板表（269 张，Genre 恒 1）。
    /// 来源与坑见 <see cref="CharacterCardTable"/>（客户端把它烘进了 setting.u）。
    /// </summary>
    public CharacterCardTable CharacterCards { get; }

    /// <summary>
    /// 任务 / 成就的八张配置表（日常 / 成就 / 活动成就 / 指引 / 社团 + 三档进度奖励）。
    /// 全进程只加载一次，<see cref="Handlers.MissionFeature"/> 与
    /// <see cref="SssjServer.Game.GmUnlock"/> 共用这一份。
    /// </summary>
    public MissionTable Missions { get; }

    /// <summary>
    /// 奖励发放服务（<c>(G,D,P,L,N)</c> 元组 → 加货币 / 进背包，含叠加与上限规则）。
    /// 全服共用一个实例（内含随机数发生器），功能域直接调
    /// <c>ctx.Rewards.Grant(player, tuples, source, result)</c>，
    /// 然后用 <see cref="GameNotify.PushRewards"/> 把结果推给客户端。
    /// </summary>
    public RewardService Rewards { get; }

    /// <summary>
    /// 「GM 测试账号全解锁」服务（全道具 / 全角色卡 / 全关卡三星 / 全任务完成）。
    /// 纯数据操作、幂等，不碰网络；推送由调用方按 <see cref="GmUnlockReport"/> 决定。
    /// </summary>
    public GmUnlock GmUnlock { get; }

    /// <summary>注册一个 LuaCall 数字命令（自动登记进「已实现」集合，避免打 unhandled 告警）。</summary>
    public void MapLuaCall(int cmd, LuaCallHandler handler) => _mapLuaCall(cmd, handler);

    /// <summary>注册一个字符串脚本命令（S2CCall.lua 那一族，如 "ChapterMsg"/"PhoneMsg"）。</summary>
    public void MapFunction(string function, ScriptCallHandler handler) => _mapFunction(function, handler);

    /// <summary>
    /// 给一个**已被别的功能域注册**的 sFunction 追加子命令处理（不覆盖对方）。
    ///
    /// ★ 用在「一个 sFunction 被多个功能域瓜分」的场合 —— 现网只有 <c>GirlLogic</c>
    /// （特训 <c>GirlTrainFeature</c> + 好感度/剧情 <c>GirlLoveFeature</c>）。
    /// 直接两边都 <see cref="MapFunction"/> 会**互相覆盖**，后装配者把前者整域吃掉。
    /// 语义与坑见 <see cref="LuaCallRouter.AddFunctionCoHandler"/>。
    /// </summary>
    /// <param name="function">sFunction 名。</param>
    /// <param name="handler">返回 <c>true</c> = 这条命令我处理了；<c>false</c> = 交给别人。</param>
    public void AddFunctionCoHandler(string function, ScriptCoHandler handler)
        => LuaCalls.AddFunctionCoHandler(function, handler);

    /// <summary>
    /// 注册一个**不解析 JSON** 的字符串命令，拿到 <c>sJsonParam</c> 原文。
    /// 只有原服 GM 通道 <c>DebugEditor</c>（参数是 Lua 源码）需要它，
    /// 理由见 <see cref="LuaCallRouter.MapRawFunction"/>。
    /// </summary>
    public void MapRawFunction(string function, RawScriptCallHandler handler)
        => LuaCalls.MapRawFunction(function, handler);

    /// <summary>注册一个 pf 处理函数（少数功能需要直接吃 protobuf 包，如邮件 1014）。</summary>
    public void MapPf(int pf, PacketHandler handler) => _pf.Map(pf, handler);

    /// <summary>
    /// 注册「玩家登录完成」钩子。
    ///
    /// 触发点：<c>GameModule.HandleLogin</c> 把 1005/1002/1104/1026 四条发完之后
    /// （即客户端 <c>Game.Player.me</c> 已建立、背包与任务值都已就位），
    /// 按注册顺序同步调用。功能域可以在这里推送自己那部分数据
    /// （例如任务派生进度、邮件列表、活动列表）。
    ///
    /// 约定：钩子里**不要抛异常**（GameModule 会 catch 并只记一条错误日志，
    /// 但抛异常会打断后续钩子的语义清晰性）；耗时操作请自己起后台任务。
    /// </summary>
    public void OnPlayerLogin(PlayerLoginHook hook) => _onLogin(hook);

    /// <summary>
    /// 注册「玩家数据准备」钩子 —— 比 <see cref="OnPlayerLogin"/> **更早**的一个口子。
    ///
    /// 触发点：<c>GameModule.HandleLogin</c> 里 <c>World.Login</c> 之后、
    /// **pf 1005 <c>PlayerNtf</c> 之前**（即客户端还没收到任何数据包）。
    ///
    /// 存在的理由：有些功能域要改的是「登录时本来就会全量下发的那部分数据」
    /// （货币 / 背包 / 关卡进度 / 任务值 / 少女）。在这个钩子里改，变更会随
    /// 1005 PlayerNtf、1104 ItemNtf、1026 TaskValueSyncRsp 一起发出去，
    /// **一个额外封包都不需要**；换到 <see cref="OnPlayerLogin"/> 里做就得自己推
    /// pf 1031/1032/1034/1026 增量 —— 对「GM 全解锁」那种 2300 件物品 + 3900 条任务值的
    /// 规模，等于凭空多发几十 KB。
    ///
    /// 约定：钩子里**只改玩家数据、不要发包**（此刻客户端连 <c>Game.Player.me</c> 都还没建立，
    /// 推什么都会被丢弃或报错）；异常会被 catch 并记一条 ERROR，不影响登录流程。
    /// </summary>
    public void OnPlayerPrepare(PlayerLoginHook hook) => _onPrepare(hook);
}

/// <summary>
/// 「玩家登录完成」钩子签名。<paramref name="session"/> 已完成 3DES 握手与 1005/1002 下发，
/// <paramref name="player"/> 就是 <c>session.Tag</c> 上的那个实例。
/// </summary>
public delegate void PlayerLoginHook(ClientSession session, Player player);

/// <summary>Lightweight, per-session maintenance driven by normal player heartbeats.</summary>
public interface IGamePulseFeature
{
    void OnPlayerPulse(ClientSession session, Player player);
}

/// <summary>
/// 一个游戏功能域（好友 / 咖啡馆 / 商店 / 任务 …）。实现类放在
/// <c>SssjServer.Game.Handlers</c> 下，必须有公开无参构造函数，
/// GameModule 会在 <c>Install</c> 时反射发现并装配（顺序按类型名，保证可复现）。
/// </summary>
public interface IGameFeature
{
    /// <summary>功能名（日志用）。</summary>
    string Name { get; }

    /// <summary>注册本功能域的全部命令。</summary>
    void Install(GameFeatureContext ctx);
}

/// <summary>反射发现 <see cref="IGameFeature"/> 实现的小工具。</summary>
internal static class GameFeatureLoader
{
    /// <summary>
    /// 找出本程序集里所有可实例化的 <see cref="IGameFeature"/>，按类型全名排序后返回实例。
    /// 排序保证注册顺序稳定（同一命令被两个功能注册时，后者覆盖前者且日志可见）。
    /// </summary>
    public static List<IGameFeature> Discover()
    {
        var result = new List<IGameFeature>();
        foreach (var type in typeof(GameFeatureLoader).Assembly
                     .GetTypes()
                     .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IGameFeature).IsAssignableFrom(t))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var ctor = type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            if (ctor is null)
            {
                Log.Warn($"[feature] {type.FullName} 实现了 IGameFeature 但没有公开无参构造函数，跳过");
                continue;
            }
            if (ctor.Invoke(null) is IGameFeature feature)
                result.Add(feature);
        }
        return result;
    }
}
