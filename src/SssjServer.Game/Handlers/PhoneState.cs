using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 一条私信对话的服务端状态（存进 <c>Player.Extra["phonemsg"].letters</c>）。
///
/// 对应 protobuf 的 <see cref="LetterInfo"/>：一个 NPC 一条，里面若干话题（<see cref="TopicState"/>）。
/// </summary>
public sealed class LetterState
{
    /// <summary>NPC id（= <c>LetterInfo.initiator</c>，也是少女 id）。</summary>
    public int NpcId { get; set; }

    /// <summary>玩家自定义标题（<c>LetterInfo.msgtitle</c>；空 = 用 NPC 默认昵称）。</summary>
    public string Title { get; set; } = "";

    /// <summary>创建时间（unix 秒，<c>LetterInfo.createtime</c>，客户端用它排序）。</summary>
    public long CreateTime { get; set; }

    /// <summary>生气位置标记（<c>LetterInfo.replypos</c>，nCmd 5 置 / nCmd 6 清）。</summary>
    public int ReplyPos { get; set; }

    /// <summary>该 NPC 下的话题（key = 话题 id）。</summary>
    public Dictionary<int, TopicState> Topics { get; } = new();

    public JsonObject ToJson()
    {
        var topics = new JsonArray();
        foreach (var t in Topics.Values.OrderBy(t => t.TopicId))
            topics.Add(t.ToJson());

        return new JsonObject
        {
            ["npc"] = NpcId,
            ["title"] = Title,
            ["time"] = CreateTime,
            ["replypos"] = ReplyPos,
            ["topics"] = topics,
        };
    }

    public static LetterState FromJson(JsonObject o)
    {
        var s = new LetterState
        {
            NpcId = PhoneMsgFeature.JsonInt(o["npc"]),
            Title = o["title"]?.ToString() ?? "",
            CreateTime = PhoneMsgFeature.JsonLong(o["time"]),
            ReplyPos = PhoneMsgFeature.JsonInt(o["replypos"]),
        };
        if (o["topics"] is JsonArray arr)
        {
            foreach (var el in arr)
            {
                if (el is not JsonObject to)
                    continue;
                var t = TopicState.FromJson(to);
                s.Topics[t.TopicId] = t;
            }
        }
        return s;
    }
}

/// <summary>
/// 一个私信话题的进度（对应 protobuf <see cref="TopicInfo"/>）。
/// </summary>
public sealed class TopicState
{
    /// <summary>话题 id（<c>TopicInfo.topicid</c>）= <c>LetterInfoNew.txt</c> 的 ID。</summary>
    public int TopicId { get; set; }

    /// <summary>
    /// 玩家已选过的选项序列（<c>TopicInfo.msgid</c>）。
    /// ★ 客户端 <c>MakeSingleNpcLetter</c> 用它重建对话分支走向。
    /// </summary>
    public List<int> MsgIds { get; } = new();

    /// <summary>NPC 已回复到第几句（<c>TopicInfo.npcmsgpos</c>）。</summary>
    public List<int> NpcMsgPos { get; } = new();

    /// <summary>是否已领奖（<c>TopicInfo.awardflag</c>，nCmd 7 置 1）。</summary>
    public int AwardFlag { get; set; }

    public JsonObject ToJson()
    {
        var msgIds = new JsonArray();
        foreach (int v in MsgIds)
            msgIds.Add(v);
        var pos = new JsonArray();
        foreach (int v in NpcMsgPos)
            pos.Add(v);

        return new JsonObject
        {
            ["id"] = TopicId,
            ["msgids"] = msgIds,
            ["npcpos"] = pos,
            ["award"] = AwardFlag,
        };
    }

    public static TopicState FromJson(JsonObject o)
    {
        var t = new TopicState
        {
            TopicId = PhoneMsgFeature.JsonInt(o["id"]),
            AwardFlag = PhoneMsgFeature.JsonInt(o["award"]),
        };
        if (o["msgids"] is JsonArray a)
        {
            foreach (var el in a)
                t.MsgIds.Add(PhoneMsgFeature.JsonInt(el));
        }
        if (o["npcpos"] is JsonArray b)
        {
            foreach (var el in b)
                t.NpcMsgPos.Add(PhoneMsgFeature.JsonInt(el));
        }
        return t;
    }
}

/// <summary>
/// 一条朋友圈帖子的服务端状态（对应 protobuf <see cref="BBSInfo"/>）。
/// </summary>
public sealed class BbsState
{
    /// <summary>帖子 id（<c>BBSInfo.topicid</c>）。</summary>
    public int TopicId { get; set; }

    /// <summary>创建时间（<c>BBSInfo.createtime</c>）。</summary>
    public long CreateTime { get; set; }

    /// <summary>
    /// 玩家的回复选项（<c>BBSInfo.replyinfo</c>）。
    /// 0 = 还没回复 ⇒ 配合 <c>BbsTopic.ReplyIdx &gt; 0</c> 点亮红点
    /// （<c>PhoneMsgLogic:CheckBBSReply</c>，`PhoneMsgLogic.lua:1827`）。
    /// </summary>
    public int ReplyInfo { get; set; }

    public JsonObject ToJson() => new()
    {
        ["id"] = TopicId,
        ["time"] = CreateTime,
        ["reply"] = ReplyInfo,
    };

    public static BbsState FromJson(JsonObject o) => new()
    {
        TopicId = PhoneMsgFeature.JsonInt(o["id"]),
        CreateTime = PhoneMsgFeature.JsonLong(o["time"]),
        ReplyInfo = PhoneMsgFeature.JsonInt(o["reply"]),
    };
}

/// <summary>
/// 玩家的终端（手机）状态：私信 + 朋友圈 + 可发表帖子列表。
/// 整体序列化进 <c>Player.Extra["phonemsg"]</c>（<c>PlayerStore</c> 已持久化 Extra）。
/// </summary>
public sealed class PhoneState
{
    /// <summary>私信（key = NpcId）。</summary>
    public Dictionary<int, LetterState> Letters { get; } = new();

    /// <summary>朋友圈帖子（key = 帖子 id）。</summary>
    public Dictionary<int, BbsState> Bbs { get; } = new();

    /// <summary>可发表帖子 id 列表（nCmd 8 下发、nCmd 9 追加、发帖后移除）。</summary>
    public List<int> BbsApply { get; } = new();
    public Dictionary<int, TopicState> ArchivedTopics { get; } = new();
    public HashSet<int> DeletedLetters { get; } = new();
    public HashSet<int> ReceivedLetters { get; } = new();
    public HashSet<int> CompletedLetters { get; } = new();
    public HashSet<int> ClaimedLetters { get; } = new();

    public JsonObject ToJson()
    {
        var letters = new JsonArray();
        foreach (var l in Letters.Values.OrderBy(l => l.NpcId))
            letters.Add(l.ToJson());

        var bbs = new JsonArray();
        foreach (var b in Bbs.Values.OrderBy(b => b.TopicId))
            bbs.Add(b.ToJson());

        var apply = new JsonArray();
        foreach (int id in BbsApply)
            apply.Add(id);

        return new JsonObject
        {
            ["letters"] = letters,
            ["archivedTopics"] = new JsonArray(ArchivedTopics.Values.OrderBy(t => t.TopicId).Select(t => (JsonNode?)t.ToJson()).ToArray()),
            ["deletedLetters"] = new JsonArray(DeletedLetters.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["receivedLetters"] = new JsonArray(ReceivedLetters.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["completedLetters"] = new JsonArray(CompletedLetters.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["claimedLetters"] = new JsonArray(ClaimedLetters.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["bbs"] = bbs,
            [PhoneMsgFeature.BbsApplyField] = apply,
        };
    }

    public static PhoneState FromJson(JsonObject? o)
    {
        var s = new PhoneState();
        if (o is null)
            return s;
        if (o["archivedTopics"] is JsonArray archived)
            foreach (var node in archived)
                if (node is JsonObject entry)
                {
                    var topic = TopicState.FromJson(entry);
                    if (topic.TopicId > 0) s.ArchivedTopics[topic.TopicId] = topic;
                }
        foreach (var (key, target) in new[] { ("deletedLetters", s.DeletedLetters), ("receivedLetters", s.ReceivedLetters), ("completedLetters", s.CompletedLetters) })
            if (o[key] is JsonArray history)
                foreach (var value in history)
                    if (PhoneMsgFeature.JsonInt(value) is > 0 and var id) target.Add(id);
        if (o["claimedLetters"] is JsonArray claimed)
            foreach (var value in claimed)
                if (PhoneMsgFeature.JsonInt(value) is > 0 and var id) s.ClaimedLetters.Add(id);

        if (o["letters"] is JsonArray la)
        {
            foreach (var el in la)
            {
                if (el is not JsonObject lo)
                    continue;
                var l = LetterState.FromJson(lo);
                s.Letters[l.NpcId] = l;
                s.ReceivedLetters.UnionWith(l.Topics.Keys);
                foreach (var topic in l.Topics.Values.Where(t => t.AwardFlag > 0))
                    s.ClaimedLetters.Add(topic.TopicId);
            }
        }
        if (o["bbs"] is JsonArray ba)
        {
            foreach (var el in ba)
            {
                if (el is not JsonObject bo)
                    continue;
                var b = BbsState.FromJson(bo);
                s.Bbs[b.TopicId] = b;
            }
        }
        // ★ 与既有 nCmd 8 的存储键兼容（第八轮就用的是 Extra["phonemsg"].bbsApply）
        if (o[PhoneMsgFeature.BbsApplyField] is JsonArray aa)
        {
            foreach (var el in aa)
            {
                int id = PhoneMsgFeature.JsonInt(el);
                if (id > 0 && !s.BbsApply.Contains(id))
                    s.BbsApply.Add(id);
            }
        }
        return s;
    }
}
