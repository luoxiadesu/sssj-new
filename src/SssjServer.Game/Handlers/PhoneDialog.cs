using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

internal static class PhoneDialog
{
    internal static (int Position, int Branch) Last(TopicState topic)
    {
        int value = topic.MsgIds.LastOrDefault(v => v > 0);
        return value > 0 ? (PhoneMsgTable.ReplyPosOf(value), PhoneMsgTable.ReplyBranchOf(value)) : (1, 0);
    }

    internal static int AwardSelection(TopicState topic) => Math.Max(1, Last(topic).Branch);

    // Only repair a fully reproducible old sequence. Validate every choice first,
    // then replace atomically; ambiguous or partially corrupt records stay intact.
    internal static bool TryRepairLegacy(LetterTopic config, TopicState topic, out List<int> rebuilt)
    {
        rebuilt = new();
        bool bare = topic.MsgIds.Any(v => v is > 0 and < PhoneMsgTable.ReplyIdBase);
        bool oldNpc = topic.MsgIds.Any(v => v >= PhoneMsgTable.ReplyIdBase &&
            PhoneMsgTable.ReplyPosOf(v) <= config.Lines.Count &&
            config.Lines[PhoneMsgTable.ReplyPosOf(v) - 1].InfoType == 0);
        if (!bare && !oldNpc) return false;
        var replay = new TopicState();
        int oldPos = 1;
        foreach (int value in topic.MsgIds)
        {
            if (value == 0) { rebuilt.Add(0); replay.MsgIds.Add(0); continue; }
            if (value < 0) return false;
            int branch = value < PhoneMsgTable.ReplyIdBase ? value : PhoneMsgTable.ReplyBranchOf(value);
            var (pos, previous) = Last(replay);
            int next = PhoneMsgTable.NextReplyPos(config, pos, previous, playerReply: true);
            if (Complete(config, replay) || next <= 0 || branch <= 0 ||
                branch > config.Lines[next - 1].Info.Count || config.Lines[next - 1].Info[branch - 1] is "NULL" or "0")
                return false;
            int corrected = PhoneMsgTable.EncodeReplyId(next, branch);
            int oldNext = PhoneMsgTable.NextReplyPos(config, oldPos, branch);
            if (oldNext <= 0) oldNext = oldPos;
            int oldEncoded = PhoneMsgTable.EncodeReplyId(oldNext, branch);
            if (value >= PhoneMsgTable.ReplyIdBase && value != oldEncoded && value != corrected)
                return false;
            rebuilt.Add(corrected);
            replay.MsgIds.Add(corrected);
            oldPos = value < PhoneMsgTable.ReplyIdBase ? oldNext : PhoneMsgTable.ReplyPosOf(value);
        }
        return !rebuilt.SequenceEqual(topic.MsgIds);
    }

    // NPC lines play locally without client requests. A pending player line must
    // be answered before either an end marker or exhaustion can count as finished.
    internal static bool Complete(LetterTopic config, TopicState topic)
    {
        var (pos, branch) = Last(topic);
        if (pos < 1 || pos > config.Lines.Count) return false;
        if (topic.MsgIds.All(v => v <= 0) && config.Lines[pos - 1].InfoType > 0) return false;
        for (int steps = 0; steps <= config.Lines.Count; steps++)
        {
            if (config.Lines[pos - 1].InfoFlag == 2) return true;
            int next = PhoneMsgTable.NextReplyPos(config, pos, branch);
            if (next == 0) return true;
            if (config.Lines[next - 1].InfoType > 0) return false;
            pos = next;
        }
        return false;
    }
}
