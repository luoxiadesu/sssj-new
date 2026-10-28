using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 主线关卡链路 <c>ChapterMsg</c>（进关 / 通关结算 / 章节奖励）。
///
/// 这一族命令**不是** LuaCall 数字命令，而是 pf=1022 里 <c>sFunction</c> 为字符串的那一族：
/// 客户端 <c>SendCmd(sCmd, tbParam)</c>（<c>Lib.lua:378</c>）→
/// <c>Game.Player.CallServer(sCmd, Json.Encode(tbParam))</c>，
/// 下行由 <c>Game.Player.OnCallClient</c> → <c>S2C.ProcCmd(sFunction, sJsonParam)</c> →
/// <c>S2C[sFunction](param)</c> 分发（<c>extracted/lua/S2CCall.lua</c>）。
///
/// <c>S2C.ChapterMsg</c>（S2CCall.lua:105）三种 nState：
/// <code>
/// nState = 0 进入场景：LevelManager:SetDropInfo(tbEnter, tbDropItems); Map.LoadById(levelInfo.nMapID)
/// nState = 1 通关结算：map:GameEnd(nStar, tbAwards, tbExp.MasterExp, tbExp.CardExp)
/// nState = 2 领取章节奖励：UI_LevelMapNew:ServerResponse()
/// nError ~= 0 时客户端弹 ui.Error.&lt;nError&gt; 并结束关卡
/// </code>
///
/// 上行三处：
/// <code>
/// 进关   UI_LevelInfoNew:EnterChapter()  {nState=0, Chapter, Index, Difficult, nFormationId}
/// 结算   Chapter.lua:365 fnSettlement    经 LuaCall.DoSettle(252) 包一层：
///        {sCmd="ChapterMsg", tbParam={nState=1, Chapter, Index, Difficult, nStar, nTime, nZDL, ...}}
/// 章奖   UI_LevelMapNew                  {nState=2, Chapter, Difficult, Pos}
/// </code>
/// 结算的应答必须**也走 DoSettle 外壳**（LuaCall 252）：客户端 DoSettle 的 else 分支
/// (LuaCall.lua:3187) 会先清掉 <c>GameManager.tbMissionParam/sMissionCmd</c>（避免重连重发），
/// 再 <c>S2C[cmd](param)</c>。直接推 sFunction="ChapterMsg" 能进 S2C，但那两个字段不会被清。
///
/// ---- nState=1 的奖励发放（本文件的主要业务）----
/// 发放本身在 <see cref="RewardService"/>（含第 6 位语义、货币折算、叠加规则的全部取证），
/// 这里只负责「取哪些配置、按什么顺序发、发完推什么」：
/// <code>
/// 首通(FirstAward, flag 0)  →  固定(FixedAward, flag 100)  →  随机(RandomAward, flag 7；缺省关闭)
///   ↓ 一并汇总成 RewardGrantResult
/// pf 1034 ChapterUpdateNtf   关卡星级位域
/// pf 1032 MoneyUpdateNtf     每种变动的货币一条（消息只有单个 Info 字段，无法批量）
/// pf 1031 ItemUpdateNtf      全部变动的背包实例（count 是绝对值）
/// DoSettle 回包 tbAwards     数组的数组 {G,D,P,L,N,Flag}
/// </code>
/// 首通判定用 <c>MapList.PassCount(旧 star 位域) == 0</c>，即**记分之前**的通关次数。
/// </summary>
public sealed class ChapterHandler
{
    /// <summary>S2CCall.lua 里的命令名。</summary>
    public const string Function = "ChapterMsg";

    /// <summary>LuaCall.DoSettle = 252（LuaCall.lua:213），结算的统一外壳。</summary>
    public const int DoSettleCmd = 252;

    /// <summary>体力不足的客户端错误码（MoneyDef.txt 体力行「通用错误」列 = 20014，UI 文本 ui.Error.20014）。</summary>
    public const int ErrNoVigour = 20014;

    private readonly ConditionalWeakTable<ClientSession, Attempt> _attempts = new();
    private sealed class Attempt
    {
        internal Player Player = null!;
        internal uint Map;
        internal string Token = Guid.NewGuid().ToString("N");
        internal bool Cancelled;
        internal int SpentVigour;
        internal uint[] MainCards = Array.Empty<uint>();
        internal BattleMainCard[] CardIdentities = Array.Empty<BattleMainCard>();
        internal Dictionary<int, int> PledgeSteps = new();
        internal object? Settlement;
    }
    private readonly MissionTable _missions;
    private readonly PledgeTable _pledge;
    private readonly CardTable _cards;
    private readonly CharacterCardTable _cardTemplates;
    private readonly PlayerExpTable _playerExp;
    private readonly GirlLoveTable _girlLove;
    private readonly CafeTable _cafe;
    private readonly PlayerStore _store;
    private readonly LuaCallRouter _luaCalls;
    private readonly ChapterTable _chapters;
    private readonly bool _spendVigour;
    private readonly RewardService _rewards;
    private readonly ChapterRandomAwardTable _randomAwards;
    private readonly bool _grantRandomAward;

    /// <param name="rewards">
    /// 奖励发放服务。为 null 时用 <see cref="ItemBaseTable.Shared"/> 现场建一个
    /// （GameModule.cs 的装配签名不在本次改动范围内，所以这里留了个可选参数以便以后注入）。
    /// </param>
    /// <param name="randomAwards">随机奖励表；为 null 时取 <see cref="ChapterRandomAwardTable.Shared"/>。</param>
    /// <param name="grantRandomAward">
    /// 是否发放 <c>RandomAward</c>（随机掉落）。**缺省关闭**，原因：
    /// 现网随机池里 6576/8411 个元组是 genre 3（模块/Rune），而 Rune 的模板不在
    /// ItemList.txt 里（那张表只覆盖 genre 5..22），客户端
    /// <c>EquipManager.AddItem</c> 的 genre==3 分支会把它塞进 <c>_runes</c> 字典并按 RuneItem 处理，
    /// 缺少属性字段时有让整条 pf 1031 处理失败的风险（连同同一包里的金币/材料一起丢）。
    /// 本次交付的验收目标是 FixedAward + FirstAward；随机奖励的抽取逻辑已实现并有单测，
    /// 真机验证过 genre 3 安全之后把这个开关打开即可。
    /// </param>
    public ChapterHandler(PlayerStore store, LuaCallRouter luaCalls, ChapterTable chapters, bool spendVigour,
                          RewardService? rewards = null,
                          ChapterRandomAwardTable? randomAwards = null,
                          bool grantRandomAward = false, CafeTable? cafe = null, GirlLoveTable? girlLove = null, PlayerExpTable? playerExp = null, CardTable? cards = null, CharacterCardTable? cardTemplates = null, MissionTable? missions = null, PledgeTable? pledge = null)
    {
        _missions = missions ?? MissionTable.Empty;
        _pledge = pledge ?? PledgeTable.Empty;
        _cards = cards ?? CardTable.Empty;
        _cardTemplates = cardTemplates ?? CharacterCardTable.Empty;
        _playerExp = playerExp ?? PlayerExpTable.Empty;
        _girlLove = girlLove ?? GirlLoveTable.Empty;
        _cafe = cafe ?? CafeTable.Empty;
        _store = store;
        _luaCalls = luaCalls;
        _chapters = chapters;
        _spendVigour = spendVigour;
        _rewards = rewards ?? new RewardService(ItemBaseTable.Shared);
        _randomAwards = randomAwards ?? ChapterRandomAwardTable.Shared;
        _grantRandomAward = grantRandomAward;
    }

    /// <summary>sFunction=="ChapterMsg" 的直接上行（进关 / 章节奖励）。</summary>
    public Task OnScriptCall(ClientSession session, string function, JsonNode? param)
        => Handle(session, param, viaDoSettle: false);

    /// <summary>LuaCall 252 DoSettle 里包着的 ChapterMsg（通关结算）。</summary>
    public Task OnSettle(ClientSession session, JsonNode? innerParam)
        => Handle(session, innerParam, viaDoSettle: true);

    private Task Handle(ClientSession session, JsonNode? param, bool viaDoSettle)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player) return HandleLocked(session, param, viaDoSettle);
    }

    private Task HandleLocked(ClientSession session, JsonNode? param, bool viaDoSettle)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] ChapterMsg 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        int state = ReadInt(param, "nState", -1);
        int chapter = ReadInt(param, "Chapter", 0);
        int index = ReadInt(param, "Index", 0);
        int diff = ReadInt(param, "Difficult", 0);

        switch (state)
        {
            case 0:
                return HandleEnter(session, player, param, chapter, index, diff, viaDoSettle);
            case 1:
                return HandleFinish(session, player, param, chapter, index, diff, viaDoSettle);
            case 3:
                return HandleCancel(session, player, param, chapter, index, diff, viaDoSettle);
            case 2:
                return HandleChapterAward(session, player, param, chapter, diff, viaDoSettle);
            default:
                Log.Warn($"[session {session.Id}] ChapterMsg 未知 nState={state} param={param?.ToJsonString()}");
                return Task.CompletedTask;
        }
    }

    // ---- nState=0 进入关卡 ----
    private Task HandleEnter(ClientSession session, Player player, JsonNode? param, int chapter, int index, int diff, bool viaDoSettle)
    {
        var cfg = _chapters.Get(chapter, index, diff);
        if (cfg is null || cfg.Chapter != chapter || cfg.Index != index || cfg.Diff != diff)
        {
            Reply(session, viaDoSettle, new { nError = 1, nState = 0 });
            return Task.CompletedTask;
        }
        uint map = cfg.MapId;
        if (_attempts.TryGetValue(session, out var pending) && ReferenceEquals(pending.Player, player)
            && pending.Map == map && pending.Settlement is null && !pending.Cancelled)
        {
            Reply(session, viaDoSettle, new { nError = 0, nState = 0, battleToken = pending.Token });
            return Task.CompletedTask;
        }
        uint[] mainCards = SnapshotMainCards(player, param, out bool validFormation);
        if (!validFormation)
        {
            Reply(session, viaDoSettle, new { nError = 1, nState = 0 });
            return Task.CompletedTask;
        }
        int cost = cfg.TotalVigour;

        if (_spendVigour && cost > 0 && !player.TrySpendVigour(cost))
        {
            Log.Warn($"[session {session.Id}] 进关体力不足 {chapter}-{index}(diff{diff}) 需要{cost} 现有{player.Vigour}");
            Reply(session, viaDoSettle, new { nError = ErrNoVigour, nState = 0 });
            return Task.CompletedTask;
        }

        if (_spendVigour && cost > 0)
        {
            _store.Save(player);
            GameNotify.PushMoney(session, player, MoneyType.Vigour);
        }

        _attempts.Remove(session);
        var attempt = new Attempt { Player = player, Map = map, SpentVigour = _spendVigour ? Math.Max(0, cost) : 0, MainCards = mainCards };
        attempt.CardIdentities = mainCards.Select(g => BattleMainCard.From(player.Items.First(i => i.Guid == g))).ToArray();
        attempt.PledgeSteps = PledgeFeature.LoadStateStatic(player).Girls.ToDictionary(k => k.Key, k => k.Value.Step);
        _attempts.Add(session, attempt);
        Log.Info($"[session {session.Id}] 进关 {chapter}-{index}(diff{diff}) " +
                 $"scene={cfg?.SceneMapId ?? -1} 体力-{(_spendVigour ? cost : 0)} 余{player.Vigour}");

        // tbEnter/tbDropItems 不下发：LevelManager:SetDropInfo 对 nil 完全安全
        // （`if tbDrop then` / `tbOthers or {}`），怪物掉落由客户端配置自算。
        Reply(session, viaDoSettle, new { nError = 0, nState = 0, battleToken = attempt.Token });
        return Task.CompletedTask;
    }

    // ---- nState=1 通关结算 ----
    private Task HandleFinish(ClientSession session, Player player, JsonNode? param,
                              int chapter, int index, int diff, bool viaDoSettle)
    {
        var cfg = _chapters.Get(chapter, index, diff);
        if (cfg is null || cfg.Chapter != chapter || cfg.Index != index || cfg.Diff != diff
            || !_attempts.TryGetValue(session, out var attempt) || !ReferenceEquals(attempt.Player, player)
            || attempt.Map != cfg.MapId || attempt.Cancelled
            || !HasToken(param, attempt))
        {
            Reply(session, viaDoSettle, new { nError = 1, nState = 1, battleToken = (param as JsonObject)?["battleToken"]?.ToString() });
            return Task.CompletedTask;
        }
        // The original nStar=0 also means a valid victory without star goals.
        // Only the patched client's explicit boolean disambiguates victory and defeat.
        if ((param as JsonObject)?["bSuccess"] is not JsonValue outcome || !outcome.TryGetValue<bool>(out bool success))
        {
            Reply(session, viaDoSettle, new { nError = 1, nState = 1, battleToken = (param as JsonObject)?["battleToken"]?.ToString() });
            return Task.CompletedTask;
        }
        if (attempt.Settlement is not null)
        {
            Reply(session, viaDoSettle, attempt.Settlement);
            return Task.CompletedTask;
        }
        int rawStars = ReadInt(param, "nStar", -1);
        if (rawStars < 0 || rawStars > MapList.StarMask)
        {
            Reply(session, viaDoSettle, new { nError = 1, nState = 1, battleToken = (param as JsonObject)?["battleToken"]?.ToString() });
            return Task.CompletedTask;
        }
        if (!success)
        {
            attempt.Settlement = new
            {
                nError = 0, nState = 1, nStar = player.GetLevelStar(cfg.MapId),
                tbExp = new { MasterExp = 0, CardExp = 0 }, tbAwards = Array.Empty<int[]>(),
                bSuccess = false, battleToken = attempt.Token,
            };
            Reply(session, viaDoSettle, attempt.Settlement);
            return Task.CompletedTask;
        }
        int starMask = rawStars;
        uint mapId = MapList.MapId(chapter, index, diff);
        bool firstClear = MapList.PassCount(player.GetLevelStar(mapId)) == 0;

        int composed = player.RecordLevelPass(mapId, starMask);

        int masterExp = cfg?.MasterExp ?? 0;
        int cardExp = cfg?.CardExp ?? 0;
        if (masterExp > 0)
            PlayerProgress.AddMasterExp(player, _playerExp, masterExp);

        // Diamond / RewardGold 是 Chapter.txt 里与 FixedAward 并列的两列（钻石奖励 / 金币奖励）。
        // 现网 495 行里这两列**全为空**（实测），关卡的金币实际来自 FixedAward 的 (15,1,1,1)，
        // 所以这里不会与下面的奖励发放重复计数；它们也不进 tbAwards（没有四元组可填）。
        if (cfg is not null && cfg.RewardGold > 0)
            player.AddMoney(MoneyType.Gold, cfg.RewardGold);
        if (cfg is not null && cfg.Diamond > 0)
            player.AddMoney(MoneyType.FreeDiamond, cfg.Diamond);

        // ---- 奖励发放 ----
        // 顺序即 tbAwards 的顺序：先首通（带「首通」角标，排前面更符合原版观感），再固定，最后随机。
        // 首通判定用的是**本次记分之前**的通关次数（firstClear 在 RecordLevelPass 之前算好）。
        var granted = new RewardGrantResult();
        if (cfg is not null)
        {
            if (firstClear)
                _rewards.Grant(player, cfg.FirstAward, RewardSource.FirstAward, granted);

            _rewards.Grant(player, cfg.FixedAward, RewardSource.FixedAward, granted);

            if (_grantRandomAward)
            {
                var rand = _randomAwards.GetByMapId(mapId);
                if (rand is not null)
                {
                    var picked = _rewards.DrawRandom(rand.Pool, rand.DrawCount);
                    _rewards.Grant(player, picked, RewardSource.RandomAward, granted);
                }
            }
        }

        var combatChanges = ChapterCombatProgress.RecordVictory(_missions, _pledge, player, chapter, index, diff,
            attempt.CardIdentities, attempt.PledgeSteps);
        var trainedCards = GrantBattleCardExp(player, attempt.MainCards, cardExp);
        attempt.Settlement = new
        {
            nError = 0,
            nState = 1,
            bSuccess = true, battleToken = attempt.Token,
            nStar = composed,
            tbExp = new { MasterExp = masterExp, CardExp = cardExp },
            // tbAwards：数组的数组 {G,D,P,L,N,Flag}（形状取证见 GrantedReward.ToTuple）
            tbAwards = granted.ToAwardTuples(),
        };
        var coffeeChanges = CafeBizFeature.RefreshCoffeeConditions(_cafe, player);
        var girlStoryChanges = GirlLoveFeature.UnlockStories(_girlLove, player);
        _store.Save(player);

        Log.Info($"[session {session.Id}] 通关 {chapter}-{index}(diff{diff}) star={starMask} " +
                 $"→ 位域 0x{composed:X}（通关{MapList.PassCount(composed)}次）" +
                 $" exp+{masterExp} 金币+{cfg?.RewardGold ?? 0} 首通={firstClear}" +
                 $" 用时={ReadInt(param, "nTime", 0)}s 战力={ReadInt(param, "nZDL", 0)}");
        Log.Info($"[session {session.Id}] 结算奖励 {granted.Describe()}");

        // 关卡进度增量推送（pf 1034 → Game.Player.OnChapterChange → MapList.ReflashLevelList）
        GameNotify.PushChapter(session, mapId, composed);
        if (combatChanges.Count > 0) GameNotify.PushTaskValues(session, combatChanges);
        if (masterExp > 0) GameNotify.PushLevelExp(session, player);
        if (coffeeChanges.Count > 0) GameNotify.PushTaskValues(session, coffeeChanges);
        if (girlStoryChanges.Count > 0) GameNotify.PushTaskValues(session, girlStoryChanges);

        // 货币增量：每种变动的货币一条 pf 1032（MoneyUpdateNtf 只有一个 Info 字段，无法批量）。
        // RewardGold/Diamond 两列即便被填上也要推，所以先并进集合再统一推，避免重复。
        var moneyToPush = new List<int>(granted.ChangedMoney);
        if (cfg is not null && cfg.RewardGold > 0 && !moneyToPush.Contains(MoneyType.Gold))
            moneyToPush.Add(MoneyType.Gold);
        if (cfg is not null && cfg.Diamond > 0 && !moneyToPush.Contains(MoneyType.FreeDiamond))
            moneyToPush.Add(MoneyType.FreeDiamond);
        GameNotify.PushMoney(session, player, moneyToPush);

        // 背包增量：一条 pf 1031 ItemUpdateNtf 带上全部变动实例（客户端
        // Game.EquipManager.OnUpdateItem 逐条 upsert，count 是绝对值 —— 见 GameNotify.PushItems）
        GameNotify.PushItems(session, granted.ChangedItems.Concat(trainedCards).Distinct().ToList());

        // nStar 必须是**完整位域**：客户端 Chapter.lua:GameEnd 直接
        // CS.Game.MapList.ReflashLevelList(nCurrMapId, tbStar) 整值覆盖本地字典。
        Reply(session, viaDoSettle, attempt.Settlement);
        return Task.CompletedTask;
    }

    // The current client animates the three MainCharacterCard entries; do not infer
    // extra experience for secondary cards, appearance overrides or friend assists.
    internal static uint[] SnapshotMainCards(Player player, JsonNode? param, out bool valid)
    {
        bool explicitId = (param as JsonObject)?.ContainsKey("nFormationId") == true;
        int id = ReadInt(param, "nFormationId", 1);
        var formation = player.Formations.FirstOrDefault(f => f.Id == id);
        valid = !explicitId || id > 0 && formation is not null;
        if (!valid || formation is null) return Array.Empty<uint>();
        var ids = formation.Slots.Select(s => s.MainCardGuid).Where(g => g != 0).ToArray();
        valid = ids.Length <= 3 && (!explicitId || ids.Length > 0) && ids.Distinct().Count() == ids.Length
            && ids.All(g => player.Items.Any(i => i.Guid == g && i.Genre == 1 && i.Count > 0));
        return valid ? ids.ToArray() : Array.Empty<uint>();
    }

    private List<Item> GrantBattleCardExp(Player player, uint[] guids, int gain)
    {
        var changed = new List<Item>();
        if (gain <= 0) return changed;
        foreach (uint guid in guids)
        {
            var item = player.Items.FirstOrDefault(i => i.Guid == guid && i.Genre == 1 && i.Count > 0);
            // Pledge cards grow exclusively from their pledge stage configuration.
            if (item is null || item.Particular == CardFeature.PldgeCardParticular) continue;
            var template = _cardTemplates.Get(item.Genre, item.Detail, item.Particular, item.Level);
            if (template is null) continue;
            int cap = _cards.MaxLevelFor(CardTable.BreakTypeOf(template.WeaponType, template.Rarity), (int)item.BreakLevel);
            int level = (int)item.EnhanceLevel;
            // Do not downgrade legacy cards already above the current break cap.
            if (cap <= 0 || level > cap) continue;
            var next = _cards.ApplyExp(level, item.EnhanceExp, gain, cap, template.ExpType);
            item.EnhanceLevel = (uint)next.Level;
            item.EnhanceExp = (uint)Math.Min(uint.MaxValue, next.Exp);
            changed.Add(item);
        }
        return changed;
    }

    private static bool HasToken(JsonNode? param, Attempt attempt)
        => (param as JsonObject)?["battleToken"] is JsonValue value
            && value.TryGetValue<string>(out var token) && token == attempt.Token;

    // Local protocol extension used only by the patched mainline pause/restart UI.
    private Task HandleCancel(ClientSession session, Player player, JsonNode? param,
        int chapter, int index, int diff, bool viaDoSettle)
    {
        if (!_attempts.TryGetValue(session, out var attempt) || !ReferenceEquals(attempt.Player, player)
            || MapList.Decode(attempt.Map) != (chapter, index, diff) || !HasToken(param, attempt)
            || attempt.Settlement is not null)
        {
            Reply(session, viaDoSettle, new { nError = 1, nState = 3 });
            return Task.CompletedTask;
        }
        // Refund only this attempt's actual debit; never recompute from mutable config.
        if (!attempt.Cancelled)
        {
            int previous = player.Vigour;
            player.SetMoney(MoneyType.Vigour, (int)Math.Min(int.MaxValue, (long)previous + attempt.SpentVigour));
            attempt.Cancelled = true;
            try { _store.Save(player); }
            catch
            {
                player.SetMoney(MoneyType.Vigour, previous);
                attempt.Cancelled = false;
                throw;
            }
            Log.Info($"[session {session.Id}] 主线退出返还电力 {attempt.SpentVigour}，当前 {player.Vigour}");
        }
        // Also resend the absolute balance for a repeated acknowledgement.
        GameNotify.PushMoney(session, player, MoneyType.Vigour);
        Reply(session, viaDoSettle, new { nError = 0, nState = 3, battleToken = attempt.Token });
        return Task.CompletedTask;
    }

    // ---- nState=2 章节累计奖励 ----
    private Task HandleChapterAward(ClientSession session, Player player, JsonNode? param,
                                    int chapter, int diff, bool viaDoSettle)
    {
        int pos = ReadInt(param, "Pos", 0);
        Log.Info($"[session {session.Id}] 章节奖励领取 chapter={chapter} diff={diff} pos={pos}（暂只回执，不发物品）");
        Reply(session, viaDoSettle, new { nError = 0, nState = 2, Chapter = chapter, Difficult = diff, Pos = pos });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 回客户端。<paramref name="viaDoSettle"/> 为 true 时套 LuaCall 252 DoSettle 外壳
    /// （与原服一致，顺带清掉客户端的重发状态）；否则直接推 sFunction="ChapterMsg"。
    /// </summary>
    private void Reply(ClientSession session, bool viaDoSettle, object payload)
    {
        if (viaDoSettle)
            _luaCalls.CallClient(session, DoSettleCmd, new { sCmd = Function, tbParam = payload });
        else
            _luaCalls.CallClientFunction(session, Function, payload);
    }

    private static int ReadInt(JsonNode? node, string name, int fallback)
    {
        var v = (node as JsonObject)?[name];
        if (v is null)
            return fallback;
        try
        {
            return v.GetValue<int>();
        }
        catch
        {
            // Lua 侧可能给 float/字符串，兜一层
            return int.TryParse(v.ToString(), out int parsed) ? parsed : fallback;
        }
    }
}
