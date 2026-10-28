using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>Local replay of tower 21 using its original mainline, skills and SP goals.</summary>
internal sealed class TwentyFirstTowerHandler
{
    internal const int Group = 96;
    internal const int ProgressBase = 1801;
    internal const int SkillBase = 1711;
    private readonly GameFeatureContext _ctx;
    private readonly TwentyFirstTowerTable _table;
    private readonly PledgeTable _pledge;
    internal const string AttemptKey = "tower21Run";
    private sealed class Attempt
    {
        public Attempt() { }
        public int Level { get; set; }
        public int Formation { get; set; }
        public int Spent { get; set; }
        public string Token { get; set; } = "";
        public bool Active { get; set; }
        public bool Cancelled { get; set; }
        public int ResultCmd { get; set; }
        public JsonObject? Reply { get; set; }
        public BattleMainCard[] Cards { get; set; } = Array.Empty<BattleMainCard>();
        public Dictionary<int,int> PledgeSteps { get; set; } = new();
    }

    // Replay tower 21 together with its own task page and exchange shop.
    internal const string EntryLua = "local t=ActivitiesConfig and ActivitiesConfig.tbSmallTowerConfig and ActivitiesConfig.tbSmallTowerConfig[21] if t then t.EndTime=3376684800 t.ClearTime=3376684800 end local s=ShopTabLogicC and ShopTabLogicC.tbShopTabConfig and ShopTabLogicC.tbShopTabConfig[30045] if s then s.endTime='2077-01-01-08-00-00' s.End={year=2077,month=1,day=1,hour=8,min=0,sec=0} end local a=NormalActivityConfig and NormalActivityConfig.tbActivityListConfig and NormalActivityConfig.tbActivityListConfig[262] if a then a.nEndTime=3376684800 end";
    internal TwentyFirstTowerHandler(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = TwentyFirstTowerTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _pledge = PledgeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(527, UpgradeSkill);
        ctx.MapFunction("Tower21Cancel", Cancel);
        ctx.OnPlayerLogin((session, player) => ctx.LuaCalls.CallClient(session,
            GmConsoleFeature.DoLuaStringCmd, new { luaStr = EntryLua }));
    }

    internal static bool IsGet(JsonNode? node) => node is JsonArray args && Number(args, 0) == 21;
    internal static bool IsRun(JsonNode? node) => node is JsonArray args && Number(args, 0) == 4 && Number(args, 1) == 21;
    internal Task Get(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player)
            _ctx.LuaCalls.CallClient(session, cmd, new { ActivityType = 4, TowerId = 21 });
        return Task.CompletedTask;
    }

    internal Task Enter(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (param is not JsonArray args || !ValidLevel(args, out var level)
                || level.Cost<0 || level.Previous.Any(id => id > 0 && player.GetTask(Group, ProgressBase + id) <= 0))
                return Error(session, cmd, 1);
            int formation = Number(args, 4);
            var mainCards=ChapterHandler.SnapshotMainCards(player,new JsonObject { ["nFormationId"]=formation },out bool validFormation);
            if (!validFormation) return Error(session, cmd, 1);
            var attempt = ReadAttempt(player);
            if (attempt is { Active: true } && (attempt.Level != level.Id || attempt.Formation != formation))
                return Error(session, cmd, 1);
            // Retransmitting an outstanding enter request must not spend stamina twice.
            if (attempt is not { Active: true })
            {
                int previous = player.Vigour;
                var oldRecord = player.Extra.GetValueOrDefault(AttemptKey)?.DeepClone();
                if (_ctx.Options.SpendVigour && !player.TrySpendVigour(level.Cost))
                    return Error(session, cmd, 5);
                attempt = new Attempt { Level=level.Id, Formation=formation, Active=true,
                    Token=Guid.NewGuid().ToString("N"), Spent=_ctx.Options.SpendVigour ? level.Cost : 0,
                    Cards=mainCards.Select(id=>BattleMainCard.From(player.Items.First(i=>i.Guid==id))).ToArray(),
                    PledgeSteps=PledgeFeature.LoadStateStatic(player).Girls.ToDictionary(g=>g.Key,g=>g.Value.Step) };
                try { SaveAttempt(player,attempt); _ctx.Store.Save(player); }
                catch
                {
                    player.SetMoney(MoneyType.Vigour,previous);
                    if(oldRecord is null)player.Extra.Remove(AttemptKey);else player.Extra[AttemptKey]=oldRecord;
                    throw;
                }
                if (_ctx.Options.SpendVigour && level.Cost > 0)
                    GameNotify.PushMoney(session, player, MoneyType.Vigour);
            }
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
            {
                ["tbData"] = new JsonArray(4, 21, 0, level.Id),
                ["battleToken"] = attempt.Token,
            });
        }
        return Task.CompletedTask;
    }

    internal Task Pass(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        string? token = Token(param);
        lock (player)
        {
            if (param is not JsonArray args || !ValidLevel(args, out var level)
                || ReadAttempt(player) is not { } attempt || token is null || token != attempt.Token
                || attempt.Level != level.Id || attempt.Formation != Number(args,4))
                return Error(session, cmd, 1, token);
            if (!attempt.Active)
            {
                if (attempt.Reply is null || attempt.ResultCmd != cmd) return Error(session, cmd, 1, token);
                _ctx.LuaCalls.CallClient(session, cmd, attempt.Reply);
                return Task.CompletedTask;
            }
            // Story callbacks send true; combat callbacks send the score/goal table.
            if (level.Type == 2 && (args.Count < 6 || args[5] is not JsonValue value
                || !value.TryGetValue<bool>(out bool story) || !story)) return Error(session, cmd, 1, token);
            if (level.Type != 2 && (args.Count < 6 || args[5] is not JsonObject && args[5] is not JsonArray))
                return Error(session, cmd, 1, token);

            var changed = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            bool first = player.GetTask(Group, ProgressBase + level.Id) <= 0;
            if (first) _ctx.Rewards.Grant(player, level.First, RewardSource.FirstAward, rewards);
            _ctx.Rewards.Grant(player, level.Fixed, RewardSource.FixedAward, rewards);
            Write(player, changed, ProgressBase + level.Id, Math.Max(1, player.GetTask(Group, ProgressBase + level.Id)));
            var newGoals = new JsonArray();
            if (level.Type == 3) CompleteGoals(player, level.Id, args[5], rewards, changed, newGoals);
            if (level.Type == 0) Tower21Missions.ActPassed(player, _ctx.Missions, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), changed);
            if (level.Type is 0 or 1 or 3)
                changed.AddRange(ChapterCombatProgress.RecordTowerVictory(_ctx.Missions,_pledge,player,attempt.Cards,attempt.PledgeSteps));
            Tower21Missions.SyncSp(player, _ctx.Missions, changed);
            PlayerProgress.AddMasterExp(player, _ctx.PlayerExp, level.MasterExp);
            attempt.Active = false;
            attempt.ResultCmd = cmd;
            var data = new JsonArray();
            foreach (var reward in rewards.ToAwardTuples()) data.Add(new JsonArray(reward.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
            attempt.Reply = new JsonObject
            {
                ["tbData"] = data, ["nMasterExp"] = level.MasterExp, ["nCardExp"] = 0,
                ["newGoalIds"] = newGoals,
                ["battleToken"] = token,
            };
            SaveAttempt(player, attempt);
            _ctx.Store.Save(player);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            if (level.MasterExp > 0) GameNotify.PushLevelExp(session, player);
            _ctx.LuaCalls.CallClient(session, cmd, attempt.Reply);
        }
        return Task.CompletedTask;
    }

    private void CompleteGoals(Player player, int levelId, JsonNode? report, RewardGrantResult rewards,
        List<KeyValuePair<uint, int>> changes, JsonArray newGoals)
    {
        if (!_table.Goals.TryGetValue(levelId, out var goals)) return;
        int before = player.GetTask(Group, 60000 + levelId);
        foreach (var goal in goals)
        {
            JsonNode? node = report is JsonObject obj ? obj[goal.Id.ToString()]
                : report is JsonArray array && goal.Id > 0 && goal.Id <= array.Count ? array[goal.Id - 1] : null;
            if (node is not JsonValue value || !value.TryGetValue<bool>(out bool reached) || !reached
                || player.GetTask(Group, 60000 + levelId * 100 + goal.Id) > 0) continue;
            Write(player, changes, 60000 + levelId * 100 + goal.Id, 1);
            newGoals.Add(goal.Id);
        }
        int score = Math.Max(before, goals.Where(g => player.GetTask(Group, 60000 + levelId * 100 + g.Id) > 0).Sum(g => g.Points));
        Write(player, changes, 60000 + levelId, score);
        if (_table.GoalRewards.TryGetValue(levelId, out var thresholds))
            foreach (var threshold in thresholds.Where(t => t.Points > before && t.Points <= score))
                _ctx.Rewards.Grant(player, threshold.Rewards, RewardSource.Extra, rewards);
    }

    internal Task Fail(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        string? token = Token(param);
        lock (player)
        {
            if (param is not JsonArray args || !ValidLevel(args, out var level)
                || ReadAttempt(player) is not { } attempt || token is null || token != attempt.Token
                || attempt.Level != level.Id || attempt.Formation != Number(args,4))
                return Error(session, cmd, 1, token);
            if (!attempt.Active)
            {
                if (attempt.ResultCmd != cmd || attempt.Reply is null) return Error(session,cmd,1,token);
                _ctx.LuaCalls.CallClient(session,cmd,attempt.Reply);
                return Task.CompletedTask;
            }
            attempt.Active = false;
            attempt.ResultCmd = cmd;
            attempt.Reply = new JsonObject { ["battleToken"]=token };
            SaveAttempt(player,attempt);_ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, attempt.Reply);
        }
        return Task.CompletedTask;
    }

    private Task UpgradeSkill(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (param is not JsonArray args || Number(args, 0) != 21
                || !_table.Skills.TryGetValue(Number(args, 1), out var skill) || skill.Item.Length != 4)
                return Error(session, cmd, 1);
            int current = player.GetTask(Group, SkillBase + skill.Id);
            if (current < 0 || current >= skill.Costs.Length)
                return Error(session, cmd, 1);
            int cost = skill.Costs[current];
            var item = skill.Item;
            if (cost <= 0) return Error(session, cmd, 1);
            var changedItems = new List<Item>();
            var removedItems = new List<Item>();
            bool currency = _ctx.ItemTemplates.TryGetMoneyId((uint)item[0], (uint)item[1], (uint)item[2], (uint)item[3], out int money);
            if (currency)
            {
                if (player.GetMoney(money) < cost) return Error(session, cmd, 8);
                player.AddMoney(money, -cost);
            }
            else
            {
                var stacks = player.Items.Where(i => i.Genre == item[0] && i.Detail == item[1]
                    && i.Particular == item[2] && i.Level == item[3] && i.Count > 0).ToArray();
                if (stacks.Sum(i => (long)i.Count) < cost) return Error(session, cmd, 8);
                uint left = (uint)cost;
                foreach (var stack in stacks)
                {
                    uint used = Math.Min(stack.Count, left);
                    stack.Count -= used;
                    left -= used;
                    if (stack.Count == 0) { player.Items.Remove(stack); removedItems.Add(stack); }
                    else changedItems.Add(stack);
                    if (left == 0) break;
                }
            }
            var changes = new List<KeyValuePair<uint, int>>();
            Write(player, changes, SkillBase + skill.Id, current + 1);
            _ctx.Store.Save(player);
            if (currency) GameNotify.PushMoney(session, player, money);
            if (removedItems.Count > 0) GameNotify.PushItemsRemoved(session, removedItems);
            if (changedItems.Count > 0) GameNotify.PushItems(session, changedItems);
            GameNotify.PushTaskValues(session, changes);
            _ctx.LuaCalls.CallClient(session, cmd, new { isOk = 1, skillId = skill.Id });
        }
        return Task.CompletedTask;
    }

    private bool ValidLevel(JsonArray args, out RestoredTowerLevel level)
    {
        level = null!;
        return Number(args, 0) == 4 && Number(args, 1) == 21 && Number(args, 2) == 0
            && _table.Levels.TryGetValue(Number(args, 3), out level!);
    }
    private static int Number(JsonArray args, int index) => index < args.Count
        && args[index] is JsonValue value && int.TryParse(value.ToString(), out int n) ? n : -1;
    private static void Write(Player player, List<KeyValuePair<uint, int>> changes, int key, int value)
    {
        if (player.GetTask(Group, key) == value) return;
        player.SetTask(Group, key, value);
        changes.Add(new(Player.ComposeTaskId(Group, key), value));
    }
    private Attempt? ReadAttempt(Player player)
    {
        try
        {
            var a=player.Extra.GetValueOrDefault(AttemptKey)?.Deserialize<Attempt>();
            if(a is null || !_table.Levels.TryGetValue(a.Level,out var level) || a.Formation<=0
                || !Guid.TryParseExact(a.Token,"N",out _) || a.Spent<0 || a.Spent!=0 && a.Spent!=level.Cost)return null;
            a.Cards??=Array.Empty<BattleMainCard>();a.PledgeSteps??=new();
            return a;
        }
        catch(JsonException){return null;}
    }
    private static void SaveAttempt(Player player,Attempt attempt)=>player.Extra[AttemptKey]=JsonSerializer.SerializeToNode(attempt);
    private static string? Token(JsonNode? value)=>value is JsonArray { Count: >=10 } args
        && args[9] is JsonValue v && v.TryGetValue<string>(out string? token) && Guid.TryParseExact(token,"N",out _) ? token : null;
    private Task Cancel(ClientSession session,string function,JsonNode? value)
    {
        if(session.Tag is not Player player || value is not JsonObject request
            || request["battleToken"] is not JsonValue v || !v.TryGetValue<string>(out string? token))return Task.CompletedTask;
        lock(player)
        {
            var attempt=ReadAttempt(player);
            if(attempt is null || !attempt.Active || attempt.Token!=token)return Task.CompletedTask;
            int previous=player.Vigour;
            var before=player.Extra[AttemptKey]!.DeepClone();
            attempt.Active=false;attempt.Cancelled=true;
            player.SetMoney(MoneyType.Vigour,(int)Math.Min(int.MaxValue,(long)previous+attempt.Spent));
            try { SaveAttempt(player,attempt);_ctx.Store.Save(player); }
            catch { player.SetMoney(MoneyType.Vigour,previous);player.Extra[AttemptKey]=before;throw; }
            if(attempt.Spent>0)GameNotify.PushMoney(session,player,MoneyType.Vigour);
        }
        return Task.CompletedTask;
    }
    private Task Error(ClientSession session, int cmd, int code,string? token=null)
    {
        var reply=new JsonObject{["nError"]=code};if(token is not null)reply["battleToken"]=token;
        _ctx.LuaCalls.CallClient(session, cmd, reply);
        return Task.CompletedTask;
    }
}
