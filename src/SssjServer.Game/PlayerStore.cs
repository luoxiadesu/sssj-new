using System.Text.Json;
using System.Text.Json.Serialization;
using SssjServer.Core;

namespace SssjServer.Game;

/// <summary>
/// 玩家存档：System.Text.Json，每账号一个文件，位于 <see cref="ServerOptions.DataDirectory"/> 下。
///
/// 要点（AGENTS.md / docs 02）：
///  * 目录按需创建；
///  * 登录（World.Login）与登出（World.Logout）都落盘；
///  * 缺失/损坏文件一律容忍：从空新建并 WARN，绝不使登录失败。
///
/// 线程安全：落盘用 <see cref="object"/> 锁串行化；文件写入用临时文件 + 原子替换，避免半写。
/// </summary>
public sealed class PlayerStore
{
    private readonly string _dir;
    private readonly object _fileLock = new();

    public PlayerStore(ServerOptions options)
    {
        _dir = Path.GetFullPath(options.DataDirectory);
        ServerZone = options.Sid;
    }

    /// <summary>本服区号（存档里新号写入，Ntf 下发用）。</summary>
    public int ServerZone { get; }

    public string DirectoryPath => _dir;

    /// <summary>加载账号存档；不存在或损坏返回 null（并 WARN）。</summary>
    public Player? Load(string account)
    {
        string path = PathFor(account);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<PlayerDto>(
                json, PlayerStore.JsonOptions);
            if (dto is null)
                throw new InvalidDataException("JSON 反序列化为 null");
            return dto.ToPlayer();
        }
        catch (Exception e)
        {
            Log.Warn($"[store] 读取玩家存档失败，将重新建档 account={account} path={path}：{e.Message}");
            return null;
        }
    }

    /// <summary>保存账号存档（目录按需创建，原子写）。</summary>
    public void Save(Player player)
        => SaveCore(player, required: false);

    /// <summary>Persist a transaction before acknowledging it; failures must reach its rollback.</summary>
    internal void SaveRequired(Player player)
        => SaveCore(player, required: true);

    private void SaveCore(Player player, bool required)
    {
        EnsureDir();
        var dto = PlayerDto.FromPlayer(player);
        string json;
        try
        {
            json = JsonSerializer.Serialize(dto, PlayerStore.JsonOptions);
        }
        catch (Exception e)
        {
            Log.Error($"[store] 序列化玩家存档失败 account={player.Account}：{e.Message}");
            if (required) throw;
            return;
        }

        string path = PathFor(player.Account);
        string tmp = path + ".tmp";
        lock (_fileLock)
        {
            try
            {
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e)
            {
                Log.Error($"[store] 写玩家存档失败 account={player.Account} path={path}：{e.Message}");
                if (required) throw;
            }
        }
    }

    private void EnsureDir()
    {
        if (!Directory.Exists(_dir))
            Directory.CreateDirectory(_dir);
    }

    private string PathFor(string account)
    {
        // 账号可能含路径特殊字符，做一次文件名安全化 + 固定后缀
        var safe = SanitizeFileName(account);
        return Path.Combine(_dir, safe + ".player.json");
    }

    private static string SanitizeFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars);
        return string.IsNullOrWhiteSpace(name) ? "_" : name;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 存档 DTO。序列化层面与 Player 解耦，便于稳定格式与容错（损坏时从空起步）。
    /// </summary>
    internal sealed class PlayerDto
    {
        public string? Account { get; set; }
        public ulong Roleid { get; set; }
        public string? Name { get; set; }
        public int Level { get; set; }
        public int Exp { get; set; }
        public int Fightpower { get; set; }
        public ulong RegisterTime { get; set; }
        public int ServerZone { get; set; }
        public int HeadFrame { get; set; }
        public uint ChatChannelId { get; set; }
        public ulong ClubId { get; set; }
        public Dictionary<int, int>? Money { get; set; }
        public List<GirlDto>? Girls { get; set; }
        public List<ItemDto>? Items { get; set; }
        public uint NextGuid { get; set; } = 1;
        public List<FormationDto>? Formations { get; set; }

        /// <summary>
        /// 任务值表。键是组合 id = (group &lt;&lt; 16) | taskId（uint）。System.Text.Json 把
        /// uint 字典键序列化成字符串，反序列化也能解析，故可直接用默认转换器（见测试）。
        /// </summary>
        public Dictionary<uint, int>? Tasks { get; set; }

        /// <summary>关卡进度：mapId（(chapter&lt;&lt;16)|(index&lt;&lt;8)|diff）→ star 位域。</summary>
        public Dictionary<uint, int>? Levels { get; set; }

        /// <summary>体力自然恢复计时基准（unix 秒）。</summary>
        public ulong VigourTime { get; set; }

        /// <summary>功能模块自有状态袋（见 Player.Extra）。</summary>
        public Dictionary<string, System.Text.Json.Nodes.JsonNode?>? Extra { get; set; }

        public static PlayerDto FromPlayer(Player p) => new()
        {
            Account = p.Account,
            Roleid = p.Roleid,
            Name = p.Name,
            Level = p.Level,
            Exp = p.Exp,
            Fightpower = p.Fightpower,
            RegisterTime = p.RegisterTime,
            ServerZone = p.ServerZone,
            HeadFrame = p.HeadFrame,
            ChatChannelId = p.ChatChannelId,
            ClubId = p.ClubId,
            Money = p.Money.Count > 0 ? new Dictionary<int, int>(p.Money) : null,
            Girls = p.Girls.Count > 0 ? p.Girls.Select(GirlDto.FromGirl).ToList() : null,
            Items = p.Items.Count > 0 ? p.Items.Select(ItemDto.FromItem).ToList() : null,
            NextGuid = p.NextGuid,
            Formations = p.Formations.Count > 0 ? p.Formations.Select(FormationDto.FromFormation).ToList() : null,
            Tasks = p.Tasks.Count > 0 ? new Dictionary<uint, int>(p.Tasks) : null,
            Levels = p.Levels.Count > 0 ? new Dictionary<uint, int>(p.Levels) : null,
            VigourTime = p.VigourTime,
            Extra = p.Extra.Count > 0 ? new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(p.Extra) : null,
        };

        public Player ToPlayer()
        {
            var p = new Player(Account ?? "", Roleid)
            {
                Name = Name ?? "",
                Level = Level,
                Exp = Exp,
                Fightpower = Fightpower,
                RegisterTime = RegisterTime,
                ServerZone = ServerZone,
                HeadFrame = HeadFrame,
                ChatChannelId = ChatChannelId,
                ClubId = ClubId,
            };
            if (Money is not null)
                foreach (var (k, v) in Money)
                    p.Money[k] = v;
            if (Girls is not null)
                foreach (var g in Girls)
                    p.Girls.Add(g.ToGirl());
            if (Items is not null)
                foreach (var it in Items)
                    p.Items.Add(it.ToItem());
            p.NextGuid = NextGuid;
            if (Formations is not null)
                foreach (var f in Formations)
                    p.Formations.Add(f.ToFormation());
            if (Tasks is not null)
                foreach (var (k, v) in Tasks)
                    p.Tasks[k] = v;
            if (Levels is not null)
                foreach (var (k, v) in Levels)
                    p.Levels[k] = v;
            p.VigourTime = VigourTime;
            if (Extra is not null)
                foreach (var (k, v) in Extra)
                    p.Extra[k] = v;
            return p;
        }
    }

    internal sealed class GirlDto
    {
        public uint Girlid { get; set; }
        public uint Level { get; set; }
        public uint Exp { get; set; }
        public uint Modelid { get; set; }
        public uint Moodvalue { get; set; }
        public uint Vigor { get; set; }
        public uint Flag { get; set; }

        public static GirlDto FromGirl(Girl g) => new()
        {
            Girlid = g.Girlid,
            Level = g.Level,
            Exp = g.Exp,
            Modelid = g.Modelid,
            Moodvalue = g.Moodvalue,
            Vigor = g.Vigor,
            Flag = g.Flag,
        };

        public Girl ToGirl() => new()
        {
            Girlid = Girlid,
            Level = Level,
            Exp = Exp,
            Modelid = Modelid,
            Moodvalue = Moodvalue,
            Vigor = Vigor,
            Flag = Flag,
        };
    }

    internal sealed class ItemDto
    {
        public uint Guid { get; set; }
        public uint Genre { get; set; }
        public uint Detail { get; set; }
        public uint Particular { get; set; }
        public uint Level { get; set; }
        public uint Count { get; set; }
        public uint CreateTime { get; set; }
        public uint EnhanceLevel { get; set; } = 1;
        public uint EnhanceExp { get; set; }
        public uint BreakLevel { get; set; }

        // ★★ 以下四个是「一直在下发、但此前从不持久化」的字段（第六轮补上）。
        //    GameNotify.BuildItemInfo 一直在发它们，只是存档 DTO 漏了这几列
        //    ⇒ 症状是「界面上的状态重登就回退」：玩家锁定的道具（LockOn）锁没了、
        //    角色卡「剧情已读」标记（Flag bit1）红点又回来、GM 塞的同步率被抹掉。
        //    ★ 这类 bug 用常规 e2e 验不出来（同会话内一切正常），只有重登才暴露。
        public uint LockOn { get; set; }
        public uint Flag { get; set; }
        public uint SyncRate { get; set; }
        public uint RefitNum { get; set; }

        // ---- 装备类（genre 2/3/4）字段，语义见 Item.Skills / Inlay / Attrs / OwnGuid ----
        // ★ 必须持久化：客户端每次收 pf 1031/1104 都先 Clear 再从这些字段填回，
        //   服务端重登后读不回来 = 「玩家的符文/技能/零件属性被清空」。
        public List<ItemSkillDto>? Skills { get; set; }
        public WeaponInlayDto? Inlay { get; set; }
        public List<ItemAttrDto>? Attrs { get; set; }
        public uint OwnGuid { get; set; }

        public static ItemDto FromItem(Item it) => new()
        {
            Guid = it.Guid,
            Genre = it.Genre,
            Detail = it.Detail,
            Particular = it.Particular,
            Level = it.Level,
            Count = it.Count,
            CreateTime = it.CreateTime,
            EnhanceLevel = it.EnhanceLevel,
            EnhanceExp = it.EnhanceExp,
            BreakLevel = it.BreakLevel,
            LockOn = it.LockOn,
            Flag = it.Flag,
            SyncRate = it.SyncRate,
            RefitNum = it.RefitNum,
            // 空集合一律存 null：否则会给近 2000 件普通道具每件都写一个 "Skills": []
            Skills = it.Skills.Count > 0 ? it.Skills.Select(ItemSkillDto.From).ToList() : null,
            Inlay = it.Inlay is { } inlay && !inlay.IsEmpty ? WeaponInlayDto.From(inlay) : null,
            Attrs = it.Attrs.Count > 0 ? it.Attrs.Select(ItemAttrDto.From).ToList() : null,
            OwnGuid = it.OwnGuid,
        };

        public Item ToItem()
        {
            var it = new Item
            {
                Guid = Guid,
                Genre = Genre,
                Detail = Detail,
                Particular = Particular,
                Level = Level,
                Count = Count,
                CreateTime = CreateTime,
                EnhanceLevel = EnhanceLevel,
                EnhanceExp = EnhanceExp,
                BreakLevel = BreakLevel,
                LockOn = LockOn,
                Flag = Flag,
                SyncRate = SyncRate,
                RefitNum = RefitNum,
                OwnGuid = OwnGuid,
                Inlay = Inlay?.ToInlay(),
            };
            if (Skills is not null)
                foreach (var s in Skills)
                    it.Skills.Add(s.ToSkill());
            if (Attrs is not null)
                foreach (var a in Attrs)
                    it.Attrs.Add(a.ToAttr());
            return it;
        }
    }

    /// <summary>存档里的一条技能（<see cref="ItemSkill"/>）。</summary>
    internal sealed class ItemSkillDto
    {
        public uint SkillId { get; set; }
        public uint SkillLevel { get; set; }
        public int SkillType { get; set; }

        public static ItemSkillDto From(ItemSkill s) => new()
        {
            SkillId = s.SkillId,
            SkillLevel = s.SkillLevel,
            SkillType = s.SkillType,
        };

        public ItemSkill ToSkill() => new()
        {
            SkillId = SkillId,
            SkillLevel = SkillLevel,
            SkillType = SkillType,
        };
    }

    /// <summary>存档里的四个符文槽（<see cref="WeaponInlay"/>），0 = 空槽。</summary>
    internal sealed class WeaponInlayDto
    {
        public uint Barrel { get; set; }
        public uint Ammo { get; set; }
        public uint Sight { get; set; }
        public uint Grip { get; set; }

        public static WeaponInlayDto From(WeaponInlay w) => new()
        {
            Barrel = w.Barrel,
            Ammo = w.Ammo,
            Sight = w.Sight,
            Grip = w.Grip,
        };

        public WeaponInlay ToInlay() => new()
        {
            Barrel = Barrel,
            Ammo = Ammo,
            Sight = Sight,
            Grip = Grip,
        };
    }

    /// <summary>存档里的一条属性（<see cref="ItemAttr"/>）。</summary>
    internal sealed class ItemAttrDto
    {
        public uint AType { get; set; }
        public int AValue { get; set; }

        public static ItemAttrDto From(ItemAttr a) => new() { AType = a.AType, AValue = a.AValue };

        public ItemAttr ToAttr() => new() { AType = AType, AValue = AValue };
    }

    internal sealed class FormationDto
    {
        public uint Id { get; set; }
        public string? Title { get; set; }
        public List<FormationCardSlotDto>? Slots { get; set; }

        public static FormationDto FromFormation(Formation f) => new()
        {
            Id = f.Id,
            Title = f.Title.Length > 0 ? f.Title : null,
            Slots = f.Slots.Count > 0 ? f.Slots.Select(FormationCardSlotDto.FromSlot).ToList() : null,
        };

        public Formation ToFormation()
        {
            var f = new Formation { Id = Id, Title = Title ?? "" };
            if (Slots is not null)
                foreach (var s in Slots)
                    f.Slots.Add(s.ToSlot());
            return f;
        }
    }

    internal sealed class FormationCardSlotDto
    {
        public uint MainCardGuid { get; set; }
        public uint UsedCardGuid { get; set; }
        public uint WeaponGuid { get; set; }
        public List<uint>? SecondaryCardGuids { get; set; }
        public List<uint>? RuneItems { get; set; }

        public static FormationCardSlotDto FromSlot(FormationCardSlot s) => new()
        {
            MainCardGuid = s.MainCardGuid,
            UsedCardGuid = s.UsedCardGuid,
            WeaponGuid = s.WeaponGuid,
            SecondaryCardGuids = s.SecondaryCardGuids.Count > 0 ? new List<uint>(s.SecondaryCardGuids) : null,
            RuneItems = s.RuneItems.Count > 0 ? new List<uint>(s.RuneItems) : null,
        };

        public FormationCardSlot ToSlot()
        {
            var slot = new FormationCardSlot
            {
                MainCardGuid = MainCardGuid,
                UsedCardGuid = UsedCardGuid,
                WeaponGuid = WeaponGuid,
            };
            if (SecondaryCardGuids is not null)
                slot.SecondaryCardGuids.AddRange(SecondaryCardGuids);
            if (RuneItems is not null)
                slot.RuneItems.AddRange(RuneItems);
            return slot;
        }
    }
}
