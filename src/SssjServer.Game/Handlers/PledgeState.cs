using System.Text.Json.Nodes;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 一条誓约任务的玩家进度（回包里 <c>taskdatas</c> 的元素）。
/// 形状取证：<c>LuaCall.lua:3000</c> 原服注释 <c>taskdatas = [{id, count, status}, ...]</c>。
/// </summary>
internal sealed class PledgeTaskState
{
    /// <summary>任务 id（对应 <c>PldgeTask.txt</c> 的 <c>id</c>）。</summary>
    public int Id { get; set; }

    /// <summary>当前计数。</summary>
    public int Count { get; set; }

    /// <summary>
    /// 状态：<see cref="PledgeFeature.TaskStatusDoing"/> 1 未完成 /
    /// <see cref="PledgeFeature.TaskStatusDone"/> 2 已完成。
    /// </summary>
    public int Status { get; set; } = PledgeFeature.TaskStatusDoing;

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["count"] = Count,
        ["status"] = Status,
    };

    public static PledgeTaskState FromJson(JsonObject o) => new()
    {
        Id = ReadInt(o["id"]),
        Count = ReadInt(o["count"]),
        Status = ReadInt(o["status"], PledgeFeature.TaskStatusDoing),
    };

    internal static int ReadInt(JsonNode? n, int fallback = 0)
        => n is JsonValue v && v.TryGetValue<int>(out int x) ? x : fallback;
}

/// <summary>
/// 一个已激活的誓约少女。
/// 回包形状 <c>{girlid, step, cardguid, taskdatas}</c>（<c>LuaCall.lua:3000</c>）。
/// </summary>
internal sealed class PledgeGirlState
{
    /// <summary>少女 id。</summary>
    public int GirlId { get; set; }

    /// <summary>
    /// 当前阶段。★ <b>1 起</b> —— step 0 在配置表里是「未激活」占位
    /// （<c>Pldge:GetBeginCfg</c>，<c>pldgecommon.lua:153</c>），激活后直接进 step 1。
    /// </summary>
    public int Step { get; set; } = 1;

    /// <summary>
    /// 当前誓约奖励卡的背包 guid。首次完成阶段前为0；换模板时跟随新实例。
    /// </summary>
    public uint CardGuid { get; set; }

    /// <summary>已结算的最高阶段，客户端用于剧情按钮、最终卡面和完成状态。</summary>
    public int RewardStep { get; set; }

    /// <summary>当前阶段的任务进度。</summary>
    public List<PledgeTaskState> Tasks { get; } = new();

    /// <summary>第7阶段起的已领奖任务数；Lua rewardlist[step-6]，不是任务进度。</summary>
    public List<int> RewardList { get; } = new();

    internal int RewardCount(int step)
        => step >= 7 && step - 7 < RewardList.Count ? RewardList[step - 7] : 0;

    internal void SetRewardCount(int step, int count)
    {
        if (step < 7) throw new ArgumentOutOfRangeException(nameof(step));
        while (RewardList.Count <= step - 7) RewardList.Add(0);
        RewardList[step - 7] = count;
    }

    internal JsonArray BuildRewardList(int maxStep = 6)
    {
        var result = new JsonArray();
        for (int i = 0; i < Math.Max(RewardList.Count, maxStep - 6); i++)
            result.Add(i < RewardList.Count ? RewardList[i] : 0);
        return result;
    }

    public JsonObject ToJson()
    {
        var arr = new JsonArray();
        foreach (var t in Tasks)
            arr.Add(t.ToJson());
        return new JsonObject
        {
            ["girlid"] = GirlId,
            ["step"] = Step,
            ["cardguid"] = CardGuid,
            ["rewardstep"] = RewardStep,
            ["tasks"] = arr,
            ["rewardlist"] = BuildRewardList(),
        };
    }

    public static PledgeGirlState FromJson(JsonObject o)
    {
        var g = new PledgeGirlState
        {
            GirlId = PledgeTaskState.ReadInt(o["girlid"]),
            Step = PledgeTaskState.ReadInt(o["step"], 1),
            CardGuid = o["cardguid"] is JsonValue guid && uint.TryParse(guid.ToString(), out uint id) ? id : 0,
            RewardStep = Math.Max(0, PledgeTaskState.ReadInt(o["rewardstep"])),
        };
        if (o["tasks"] is JsonArray arr)
        {
            foreach (var el in arr)
            {
                if (el is JsonObject to)
                    g.Tasks.Add(PledgeTaskState.FromJson(to));
            }
        }
        if (o["rewardlist"] is JsonArray rewards)
            foreach (var count in rewards)
                g.RewardList.Add(Math.Max(0, PledgeTaskState.ReadInt(count)));
        return g;
    }
}

/// <summary>
/// 誓约域的玩家存档（<c>Player.Extra["pledge"]</c>）。
/// </summary>
internal sealed class PledgeState
{
    /// <summary>已激活的少女：girlid → 状态。</summary>
    public Dictionary<int, PledgeGirlState> Girls { get; } = new();

    public JsonObject ToJson()
    {
        var arr = new JsonArray();
        foreach (var g in Girls.Values.OrderBy(g => g.GirlId))
            arr.Add(g.ToJson());
        return new JsonObject { ["girls"] = arr };
    }

    public static PledgeState FromJson(JsonObject? o)
    {
        var s = new PledgeState();
        if (o?["girls"] is JsonArray arr)
        {
            foreach (var el in arr)
            {
                if (el is not JsonObject go)
                    continue;
                var g = PledgeGirlState.FromJson(go);
                if (g.GirlId > 0)
                    s.Girls[g.GirlId] = g;
            }
        }
        return s;
    }
}
