using SssjServer.Core;
using SssjServer.Game;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 装备类物品（genre 2 武器 / 3 符文 / 4 零件）下发与存档的测试。
///
/// <para>背景：这几个 genre 此前<b>完全不能下发</b>（<c>docs/05</c> §0.3 的四字段红线）。
/// 本组测试把「为什么不能发」变成「发成什么样才安全」的可执行断言。
/// 完整取证见 <c>work/weapon-genre-forensics.md</c> 与 <c>work/weapon-mainsession.md</c>。</para>
///
/// <para><b>★★★ 最重要的一条是 <see cref="零件_attrlist必须恰好6个元素"/></b>：
/// 客户端 <c>AddOriginal</c>(0x1D99094) 有两个<b>固定次数、无 Count 检查、无 has_ 保护</b>
/// 的取值循环（<c>0x1D99AB8 cmp w23,#3</c> + <c>0x1D99E7C cmp w21,#6</c>），
/// 少于 6 个元素会抛 <c>ArgumentOutOfRangeException</c>，
/// <b>导致该条 pf 1031/1104 里后续所有物品全部丢失</b>。</para>
/// </summary>
public class EquipItemNotifyTests
{
    private const uint GenreCard = 1;
    private const uint GenreWeapon = 2;
    private const uint GenreRune = 3;
    private const uint GenreOriginal = 4;
    private const uint GenreItem = 5;

    // ================== attrlist 的 6 元素红线 ==================

    /// <summary>
    /// ★★★ 零件的 <c>attrlist</c> 必须恰好 6 个元素 —— 这是防客户端崩溃的硬约束。
    /// 无论服务端侧存了几条，下发时都要补齐到 6。
    /// </summary>
    [Theory]
    [InlineData(0)]   // 全新零件，一条属性都没配
    [InlineData(1)]
    [InlineData(3)]   // 只有四维属性、没有新词条 —— 最容易踩的真实场景
    [InlineData(5)]   // 差一个，最隐蔽
    [InlineData(6)]   // 正好
    [InlineData(9)]   // 超了，应截断
    public void 零件_attrlist必须恰好6个元素(int stored)
    {
        var it = new Item { Guid = 7, Genre = GenreOriginal, Count = 1 };
        for (int i = 0; i < stored; i++)
            it.Attrs.Add(new ItemAttr { AType = (uint)(i + 1), AValue = 100 + i });

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(Item.AttrSlotCount, info.Attrlist.Count);
        Assert.Equal(6, Item.AttrSlotCount);
    }

    /// <summary>
    /// 补齐用的占位元素必须是 <c>atype = 0</c> —— 客户端两个循环都对 0 有跳过分支
    /// （循环① <c>0x1D997F8 sub w8,w0,#1</c>+<c>cmp w8,#3</c>+<c>b.hi</c>、
    /// 循环② <c>0x1D99D74 cbz w0</c>），所以 0 会被静默忽略、不污染数据。
    /// ★ 如果占位用了非 0 的 atype，会被当成真实属性写进零件，是「静默数据污染」。
    /// </summary>
    [Fact]
    public void 零件_补齐的占位元素atype必须为0()
    {
        var it = new Item { Guid = 7, Genre = GenreOriginal, Count = 1 };
        it.Attrs.Add(new ItemAttr { AType = 1, AValue = 500 });   // 攻击

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(6, info.Attrlist.Count);
        Assert.Equal(1u, info.Attrlist[0].Atype);
        Assert.Equal(500, info.Attrlist[0].Avalue);
        for (int i = 1; i < 6; i++)
        {
            Assert.Equal(0u, info.Attrlist[i].Atype);
            Assert.Equal(0, info.Attrlist[i].Avalue);
        }
    }

    /// <summary>
    /// 前 6 个必须<b>按原顺序</b>下发：<c>attrlist</c> 的语义是分段的
    /// （下标 0..2 = 四维属性、3..5 = 新词条等级），顺序错了语义就错了。
    /// </summary>
    [Fact]
    public void 零件_attrlist保持顺序且截断多余项()
    {
        var it = new Item { Guid = 7, Genre = GenreOriginal, Count = 1 };
        for (int i = 0; i < 8; i++)
            it.Attrs.Add(new ItemAttr { AType = (uint)(i + 1), AValue = i * 10 });

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(6, info.Attrlist.Count);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal((uint)(i + 1), info.Attrlist[i].Atype);
            Assert.Equal(i * 10, info.Attrlist[i].Avalue);
        }
    }

    /// <summary><c>avalue</c> 在 proto 里是 <b>int32</b>（可为负），负值要能保真下发。</summary>
    [Fact]
    public void 零件_属性值可以为负()
    {
        var it = new Item { Guid = 7, Genre = GenreOriginal, Count = 1 };
        it.Attrs.Add(new ItemAttr { AType = 3, AValue = -250 });

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(-250, info.Attrlist[0].Avalue);
    }

    /// <summary><c>ownguid</c>（宿主武器）必须下发，漏了等于「零件与武器解绑」。</summary>
    [Fact]
    public void 零件_ownguid必须下发()
    {
        var it = new Item { Guid = 7, Genre = GenreOriginal, Count = 1, OwnGuid = 4242 };

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(4242u, info.Ownguid);
    }

    // ================== weaponinlay ==================

    /// <summary>
    /// 四个符文槽必须逐槽保真 —— 客户端把它们分别写进
    /// <c>BarrelGuid</c>/<c>AmmoGuid</c>/<c>SightGuid</c>/<c>GripGuid</c>
    /// （0x1D978DC / 0x1D97930 / 0x1D97984 / 0x1D979D8）。
    /// ★ 顺序写错的后果是「符文装在了错误的槽位上」，不会报错。
    /// </summary>
    [Fact]
    public void 武器_四个符文槽逐槽保真()
    {
        var it = new Item
        {
            Guid = 11, Genre = GenreWeapon, Count = 1,
            Inlay = new WeaponInlay { Barrel = 101, Ammo = 102, Sight = 103, Grip = 104 },
        };

        var info = GameNotify.BuildItemInfo(it);

        Assert.NotNull(info.Weaponinlay);
        Assert.Equal(101u, info.Weaponinlay!.Barrel);
        Assert.Equal(102u, info.Weaponinlay.Ammo);
        Assert.Equal(103u, info.Weaponinlay.Sight);
        Assert.Equal(104u, info.Weaponinlay.Grip);
    }

    /// <summary>
    /// 「无符文的新武器」不带 <c>weaponinlay</c> 是<b>安全</b>的：
    /// 客户端 <c>has_weaponinlay</c> 为假时走 <c>0x1D979F8</c>，自己把四槽全写 0
    /// （<c>mov w1, wzr</c>）—— 与「发四个 0」终点相同。
    /// ★ 这条把红线从「不发就出事」精确成「**发的必须与当前状态一致**」。
    /// </summary>
    [Fact]
    public void 武器_无符文时不带weaponinlay是安全的()
    {
        var it = new Item { Guid = 11, Genre = GenreWeapon, Count = 1, Inlay = null };

        var info = GameNotify.BuildItemInfo(it);

        Assert.Null(info.Weaponinlay);
    }

    /// <summary>四槽全 0 的 <see cref="WeaponInlay"/> 视为空（<see cref="WeaponInlay.IsEmpty"/>）。</summary>
    [Fact]
    public void 武器_全0符文槽算空()
    {
        Assert.True(new WeaponInlay().IsEmpty);
        Assert.False(new WeaponInlay { Sight = 1 }.IsEmpty);
    }

    /// <summary>
    /// 非武器 genre 不下发 <c>weaponinlay</c>：符文/零件走的 Add 方法根本不读它。
    /// </summary>
    [Theory]
    [InlineData(GenreRune)]
    [InlineData(GenreOriginal)]
    public void 非武器不下发weaponinlay(uint genre)
    {
        var it = new Item
        {
            Guid = 11, Genre = genre, Count = 1,
            Inlay = new WeaponInlay { Barrel = 1, Ammo = 2, Sight = 3, Grip = 4 },
        };

        var info = GameNotify.BuildItemInfo(it);

        Assert.Null(info.Weaponinlay);
    }

    /// <summary>非零件 genre 不下发 <c>attrlist</c>/<c>ownguid</c>（只有 <c>AddOriginal</c> 读它们）。</summary>
    [Theory]
    [InlineData(GenreWeapon)]
    [InlineData(GenreRune)]
    public void 非零件不下发attrlist(uint genre)
    {
        var it = new Item { Guid = 11, Genre = genre, Count = 1, OwnGuid = 9 };
        it.Attrs.Add(new ItemAttr { AType = 1, AValue = 1 });

        var info = GameNotify.BuildItemInfo(it);

        Assert.Empty(info.Attrlist);
        Assert.Equal(0u, info.Ownguid);
    }

    // ================== skillinfo ==================

    /// <summary>
    /// <c>skillinfo</c> 三字段保真。<c>skilltype</c> 是分流键，
    /// 武器认 1(主技能)/2(被动)/4(校准)，符文认 2/6，零件认 7/8/9。
    /// </summary>
    [Theory]
    [InlineData(GenreWeapon, 1)]
    [InlineData(GenreWeapon, 2)]
    [InlineData(GenreWeapon, 4)]
    [InlineData(GenreRune, 2)]
    [InlineData(GenreRune, 6)]
    [InlineData(GenreOriginal, 7)]
    public void 装备_skillinfo三字段保真(uint genre, int skillType)
    {
        var it = new Item { Guid = 11, Genre = genre, Count = 1 };
        it.Skills.Add(new ItemSkill { SkillId = 8801, SkillLevel = 3, SkillType = skillType });

        var info = GameNotify.BuildItemInfo(it);

        Assert.Single(info.Skillinfo);
        Assert.Equal(8801u, info.Skillinfo[0].Skillid);
        Assert.Equal(3u, info.Skillinfo[0].Skilllevel);
        Assert.Equal(skillType, info.Skillinfo[0].Skilltype);
    }

    /// <summary>多条技能按原顺序下发（武器按数组下标写入，顺序决定落在哪个槽）。</summary>
    [Fact]
    public void 装备_多条技能保持顺序()
    {
        var it = new Item { Guid = 11, Genre = GenreWeapon, Count = 1 };
        it.Skills.Add(new ItemSkill { SkillId = 1, SkillLevel = 1, SkillType = 2 });
        it.Skills.Add(new ItemSkill { SkillId = 2, SkillLevel = 2, SkillType = 2 });
        it.Skills.Add(new ItemSkill { SkillId = 3, SkillLevel = 3, SkillType = 4 });

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(3, info.Skillinfo.Count);
        Assert.Equal(new uint[] { 1, 2, 3 }, info.Skillinfo.Select(s => s.Skillid));
        Assert.Equal(new[] { 2, 2, 4 }, info.Skillinfo.Select(s => s.Skilltype));
    }

    /// <summary>空技能列表 = 「这件装备没有技能」，等价于 Clear 后不填回（全 0）。</summary>
    [Fact]
    public void 装备_无技能时skillinfo为空()
    {
        var it = new Item { Guid = 11, Genre = GenreWeapon, Count = 1 };

        var info = GameNotify.BuildItemInfo(it);

        Assert.Empty(info.Skillinfo);
    }

    // ================== 非装备 genre 的门控 ==================

    /// <summary>
    /// ★ 非装备 genre 不带武器槽、属性和宿主字段；角色卡仍需传输自己的 MiniSkill。
    /// genre 5 分支只读 6 个标量、无任何 Clear（<c>0x1D9B168..0x1D9B2BC</c>）；
    /// 角色卡走 <c>AddCharacterCard</c> 另一套（<c>syncrate</c>/<c>refitnum</c>）。
    /// </summary>
    [Theory]
    [InlineData(GenreCard)]
    [InlineData(GenreItem)]
    public void 非装备genre不带装备四字段(uint genre)
    {
        var it = new Item
        {
            Guid = 11, Genre = genre, Count = 1, OwnGuid = 5,
            Inlay = new WeaponInlay { Barrel = 1 },
        };
        it.Skills.Add(new ItemSkill { SkillId = 1, SkillType = 2 });
        it.Attrs.Add(new ItemAttr { AType = 1, AValue = 1 });

        var info = GameNotify.BuildItemInfo(it);

        if (genre == GenreCard)
        {
            var skill = Assert.Single(info.Skillinfo);
            Assert.Equal(1u, skill.Skillid);
            Assert.Equal(2, skill.Skilltype);
        }
        else Assert.Empty(info.Skillinfo);
        Assert.Null(info.Weaponinlay);
        Assert.Empty(info.Attrlist);
        Assert.Equal(0u, info.Ownguid);
    }

    /// <summary><see cref="Item.IsEquipGenre"/> 的边界（<c>emGenreType</c>：Card1/Weapon2/Rune3/Original4/Item5）。</summary>
    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(5u, false)]
    [InlineData(22u, false)]
    public void IsEquipGenre边界(uint genre, bool expected)
        => Assert.Equal(expected, new Item { Genre = genre }.IsEquipGenre);

    /// <summary>角色卡的 <c>syncrate</c>/<c>refitnum</c> 不受本次改动影响（回归保护）。</summary>
    [Fact]
    public void 回归_角色卡syncrate与refitnum仍然下发()
    {
        var it = new Item
        {
            Guid = 42, Genre = GenreCard, Detail = 1, Particular = 1, Level = 3,
            Count = 1, EnhanceLevel = 5, SyncRate = 77777, RefitNum = 3,
        };

        var info = GameNotify.BuildItemInfo(it);

        Assert.Equal(77777u, info.Syncrate);
        Assert.Equal(3u, info.Refitnum);
        Assert.Equal(5u, info.Enhancelevel);
    }
}
