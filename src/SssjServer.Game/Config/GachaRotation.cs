using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SssjServer.Core;

namespace SssjServer.Game.Config;

public sealed record GachaRotationEntry(int Type, int Id, int Segment, long Start, long End);
public sealed record GachaRotationPlan(bool Enabled, GachaRotationEntry[] Entries);

/// <summary>Local schedule override. Immutable snapshots; invalid edits retain the last valid plan.</summary>
public sealed class GachaRotation
{
    public const string FileName = "gacha-rotation.json";
    private static readonly ConcurrentDictionary<string, GachaRotation> Instances = new(StringComparer.OrdinalIgnoreCase);
    public static GachaRotation For(string directory) => Instances.GetOrAdd(Path.GetFullPath(directory), d => new(d));
    private readonly string _path;
    private readonly Dictionary<(int Type, int Id), int> _segments = new();
    private readonly object _gate = new();
    private string? _lastText;
    private GachaRotationPlan _plan = new(false, []);
    public bool HasConfiguration => File.Exists(_path);

    private GachaRotation(string directory)
    {
        _path = Path.Combine(directory, FileName);
        var table = SettingTable.Load(Path.Combine(directory, "gacha.txt"));
        if (table is null) return;
        foreach (var row in table.Rows)
        {
            int type = table.Int(row, "Type", -1), id = table.Int(row, "ID");
            if (type is not (0 or 1) || id <= 0) continue;
            var key = (type, id);
            _segments[key] = _segments.GetValueOrDefault(key) + 1;
        }
    }

    public GachaRotationPlan Read()
    {
        lock (_gate)
        {
            try
            {
                string text = File.Exists(_path) ? File.ReadAllText(_path) : "{\"enabled\":false,\"entries\":[]}";
                if (text == _lastText) return _plan;
                _lastText = text;
                var next = JsonSerializer.Deserialize<GachaRotationPlan>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("排期不能为空");
                Validate(next, _segments);
                _plan = next;
            }
            catch (Exception e) when (e is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Warn($"[gacha-rotation] 保留上次有效排期：{e.Message}");
            }
            return _plan;
        }
    }

    internal static void Validate(GachaRotationPlan plan, IReadOnlyDictionary<(int Type, int Id), int> catalog)
    {
        if (plan.Entries is null || plan.Entries.Length > 1000) throw new InvalidDataException("排期数量无效");
        foreach (var entry in plan.Entries)
            if (entry is null || !catalog.TryGetValue((entry.Type, entry.Id), out int count)
                || entry.Segment < 1 || entry.Segment > count || entry.Start < 1
                || entry.End <= entry.Start || entry.End > 253402300799)
                throw new InvalidDataException("卡池、档期或时间无效");
        foreach (var group in plan.Entries.GroupBy(e => (e.Type, e.Id)))
        {
            long end = 0;
            foreach (var entry in group.OrderBy(e => e.Start))
            {
                if (entry.Start < end) throw new InvalidDataException("同类型同ID的排期不能重叠");
                end = entry.End;
            }
        }
    }

    // null: original configuration; 0: managed and closed; >0: selected segment.
    public int? SegmentAt(int type, int id, long now)
    {
        var plan = Read();
        if (!plan.Enabled) return null;
        return plan.Entries.FirstOrDefault(e => e.Type == type && e.Id == id && e.Start <= now && now < e.End)?.Segment ?? 0;
    }

    public string ClientLua()
    {
        var plan = Read();
        var script = new StringBuilder("_G.__localGachaRotationApply=function() local function apply(root,typ) if not root or not root.tbConf then return end for id,segs in pairs(root.tbConf) do for idx,v in ipairs(segs) do if not v._rotationOriginal then v._rotationOriginal={v.nStart,v.nEnd} end ");
        script.Append(plan.Enabled ? "v.nStart=1 v.nEnd=1 " : "v.nStart=v._rotationOriginal[1] v.nEnd=v._rotationOriginal[2] ");
        script.Append("end end end apply(GachaCommon,1) apply(WeaponForge,0) ");
        if (plan.Enabled)
        {
            // Merge multiple windows into a GetTime-dependent selector: each segment
            // retains all windows so consecutive schedules need no server timer UI action.
            foreach (var group in plan.Entries.GroupBy(e => (e.Type, e.Id, e.Segment)))
            {
                string root = group.Key.Type == 1 ? "GachaCommon" : "WeaponForge";
                script.Append($"do local root={root} local v=root and root.tbConf and root.tbConf[{group.Key.Id}] and root.tbConf[{group.Key.Id}][{group.Key.Segment}] if v then v._rotationWindows={{");
                foreach (var e in group) script.Append($"{{{e.Start},{e.End}}},");
                script.Append("} end end ");
            }
        }
        // The app sends the script each second only when the effective windows change.
        script.Append("local now=GetTime() for _,root in pairs({GachaCommon,WeaponForge}) do if root and root.tbConf then for _,segs in pairs(root.tbConf) do for _,v in ipairs(segs) do if v._rotationWindows then for _,w in ipairs(v._rotationWindows) do if w[1]<=now and now<w[2] then v.nStart=w[1] v.nEnd=w[2] end end v._rotationWindows=nil end end end end end");
        // Login can precede LoadConfig, and configuration reloads replace tbConf.
        // Keep the server-selected schedule effective after both events.
        script.Append(" end for _,root in pairs({GachaCommon,WeaponForge}) do if root then root._localRotationHooks=root._localRotationHooks or {} for _,name in ipairs({'LoadConfig','GetCurPools'}) do if type(root[name])=='function' and not root._localRotationHooks[name] then local base=root[name] local after=name=='LoadConfig' root._localRotationHooks[name]=base root[name]=function(self,...) if not after then _G.__localGachaRotationApply() return base(self,...) end base(self,...) _G.__localGachaRotationApply() end end end end end _G.__localGachaRotationApply()");
        return script.ToString();
    }

    public string Revision(long now)
    {
        var plan = Read();
        return JsonSerializer.Serialize(plan) + ":" + string.Join(',', plan.Entries.Select(e => e.Start <= now && now < e.End ? 1 : 0));
    }
}
