using SssjServer.Game.Config;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 咖啡馆收益配置的读取校验（116 喝完结算的数值来源）。
/// 用真实 `extracted/setting/` 数据，防止「列名写错 ⇒ 静默读成 0」。
/// </summary>
public class CafeTableProbeTests
{
    private static CafeTable T => CafeTable.Load(TestPaths.SettingDir);

    /// <summary>咖啡表的四属性与三档收益要真读到（不能全 0）。</summary>
    [Fact]
    public void 咖啡表_收益与属性非空()
    {
        var c = T.Coffees[1];
        Assert.Equal(1, c.Star);
        // 取证 CoffeeList.txt 第一行数据：AwardA=10 / AwardB=1 / AwardC=1
        Assert.Equal(10, c.AwardA);
        Assert.Equal(1, c.AwardB);
        Assert.Equal(1, c.AwardC);
        Assert.Equal(10, c.Price);
        Assert.Equal(5, c.Cost);
        // 四属性：Id=1 是 Appearance=55（醇度高），其余 25
        Assert.Equal(55, c.Appearance);
        Assert.Equal(25, c.Sweet);
        Assert.Equal(25, c.Concentration);
        Assert.Equal(25, c.Flavor);
    }

    /// <summary>`AttrByName` 要能按顾客 Prefer 的列名取到对应属性（CoffeeCommon.lua:499）。</summary>
    [Fact]
    public void 咖啡表_按列名取属性()
    {
        var c = T.Coffees[1];
        Assert.Equal(c.Appearance, c.AttrByName("Appearance"));
        Assert.Equal(c.Sweet, c.AttrByName("Sweet"));
        Assert.Equal(c.Concentration, c.AttrByName("Concentration"));
        Assert.Equal(c.Flavor, c.AttrByName("Flavor"));
        Assert.Equal(0, c.AttrByName("不存在的列"));   // 兜底不抛
    }

    /// <summary>顾客表的 Prefer / Preferfactor / Rate 要真读到。</summary>
    [Fact]
    public void 顾客表_偏好与概率非空()
    {
        // 201：1★，Prefer=Appearance，Preferfactor=51，Rate 全 0
        var g201 = T.Guests[201];
        Assert.Equal(1, g201.Star);
        Assert.Equal("Appearance", g201.Prefer);
        Assert.Equal(51, g201.PreferFactor);
        Assert.Equal(0, g201.Rate1);

        // 205：2★，Preferfactor=61，Rate1=15
        var g205 = T.Guests[205];
        Assert.Equal(2, g205.Star);
        Assert.Equal(61, g205.PreferFactor);
        Assert.Equal(15, g205.Rate1);
    }

    /// <summary>
    /// ★ 匹配判定：`coffee[Prefer] >= Preferfactor`（CoffeeCommon.lua:499）。
    /// 顾客 201 要 Appearance>=51，咖啡 1 的 Appearance=55 ⇒ 匹配；
    /// 顾客 205 要 Appearance>=61 ⇒ 同一杯咖啡**不**匹配。
    /// </summary>
    [Fact]
    public void 匹配判定_按取证公式()
    {
        var c = T.Coffees[1];
        var g201 = T.Guests[201];
        var g205 = T.Guests[205];

        Assert.True(c.AttrByName(g201.Prefer) >= g201.PreferFactor, "201 应匹配");
        Assert.False(c.AttrByName(g205.Prefer) >= g205.PreferFactor, "205 不应匹配");
    }

    /// <summary>
    /// Cond1 关卡条件不能解释为前置咖啡。
    /// 原始 Lua 使用独立 PrevCoffee 列；当前资源没有该列。
    /// </summary>
    [Fact]
    public void 咖啡前置_关卡条件不产生咖啡依赖()
    {
        var t = T;
        Assert.Equal(0, t.CoffeePrereq(6));
        Assert.Equal(0, t.CoffeePrereq(8));
        Assert.Equal(0, t.CoffeePrereq(10));
        Assert.Equal(0, t.CoffeePrereq(12));

        // 其余咖啡没有前置
        Assert.Equal(0, t.CoffeePrereq(1));
        Assert.Equal(0, t.CoffeePrereq(2));
        Assert.Equal(0, t.CoffeePrereq(9999));    // 不存在的 id 也不抛
    }

    /// <summary>
    /// ★★ 钉住一条**否证**：`CoffeeList.txt` 现网**没有**客户端
    /// `CheckCanLeanCoffee`（`CoffeeCommon.lua:428..458`）要查的那四列
    /// （<c>LearnCostPoint</c>/<c>Level</c>/<c>PrevCoffee</c>/<c>LearnCostCash</c>）
    /// ⇒ 客户端读它们走 `or 0` 兜底、四项检查恒通过
    /// ⇒ 服务端也无从校验技能点/等级/金币，只能校验 <c>Cond1</c> 前置。
    ///
    /// 若将来换了配置表、这些列出现了，本测试会红 —— 提醒补齐校验。
    /// </summary>
    [Fact]
    public void 咖啡表_没有技能点与等级列()
    {
        var raw = SettingTable.Load(
            System.IO.Path.Combine(TestPaths.SettingDir, "CoffeeList.txt"))!;
        foreach (var col in new[] { "LearnCostPoint", "Level", "PrevCoffee", "LearnCostCash" })
        {
            // 整表任意一行都取不到非 0 值 ⇒ 该列不存在（或全空）
            bool any = raw.Rows.Any(r => raw.Int(r, col, 0) != 0);
            Assert.False(any, $"CoffeeList.txt 出现了 {col} 列 ⇒ 需要补对应的学习条件校验");
        }
    }

    /// <summary>
    /// 家具表要真读到。
    /// ★ `NCafeItems.txt` 里有一批 `Id` 为空/非法的**占位行**（纯制表符），必须跳过
    /// ⇒ 合法唯一 id 是 **908** 个、零重复（已程序化核对）。
    /// 断言按 908 写，若将来读到明显更少就说明列名或表头行数出错了。
    /// ★★ 第十轮更新：换新版 `setting.u` 后 863 → 908。
    /// </summary>
    [Fact]
    public void 家具表_读到全部并有售价()
    {
        var t = T;
        Assert.Equal(908, t.Furniture.Count);
        int sellable = t.Furniture.Values.Count(f => f.SellPrice > 0);
        Assert.True(sellable > 500, $"可卖家具应 >500 件，实际 {sellable}");
    }

    /// <summary>
    /// 主题表要真读到，且 <c>Item0..N</c> 的**五段**（家具 id + 摆位）都解析正确。
    /// 取证 `NCafeThemes.txt` 第一行数据：Item0 = <c>"401,6,0,5,0"</c>
    /// ⇒ 家具 401、parent 6、(0,5)、rotate 0。
    ///
    /// ★★★ 摆位那四段是第十二轮补上的：早先只取家具 id，导致 <c>OnBuyTheme</c>
    /// 把 <c>parentId</c> 写 0 ⇒ 买过主题的号进不了咖啡馆（见 <c>CafeThemeItem</c>）。
    /// </summary>
    [Fact]
    public void 主题表_家具清单解析正确()
    {
        var t = T;
        Assert.True(t.Themes.Count >= 50, $"主题应 ≥50 个，实际 {t.Themes.Count}");

        var th1 = t.Themes[1];
        Assert.Contains(401, th1.Items.Select(i => i.TemplateId));   // Item0 = "401,6,0,5,0"
        Assert.Contains(501, th1.Items.Select(i => i.TemplateId));   // Item1 = "501,2,29,9,0"
        Assert.Equal(1, th1.Area);
        Assert.False(th1.CanSell);            // CanSell = 0

        // 主题 3 是 NCI_XD 族、可卖
        var th3 = t.Themes[3];
        Assert.Equal("NCI_XD", th3.ThemeType);
        Assert.True(th3.CanSell);

        // ★ 摆位探针：主题 3 的 Item0 = "401,6,19,4,0"、Item19 = "502,2,0,24,90"
        //   （对着真实表文件取的具体数值，见 AGENTS.md §5「先写探针测试」那条）
        var t3i0 = th3.Items[0];
        Assert.Equal(401u, (uint)t3i0.TemplateId);
        Assert.Equal(6u, t3i0.ParentId);
        Assert.Equal(19u, t3i0.PosX);
        Assert.Equal(4u, t3i0.PosY);
        Assert.Equal(0u, t3i0.Rotate);

        // ★★ 关键回归：整个表里**不允许**出现 parentId=0 —— 它正是卡加载条的那个值。
        //    现网 53 个主题全部照表配了 parent，所以这条应恒成立。
        foreach (var th in t.Themes.Values)
        {
            foreach (var it in th.Items)
            {
                Assert.True(it.ParentId > 0,
                            $"主题 {th.Id} 的家具 {it.TemplateId} 解出 parentId=0 —— " +
                            "下发它会让客户端进店卡在 40% 加载条（UpdateNavMeshObstacle NRE）");
            }
        }

        // 每个主题的家具 id 都应在家具表里（否则摆位会指向不存在的家具）
        foreach (var th in t.Themes.Values)
        {
            foreach (var it in th.Items)
                Assert.True(t.Furniture.ContainsKey(it.TemplateId),
                            $"主题 {th.Id} 的家具 {it.TemplateId} 不在 NCafeItems.txt 里");
        }
    }

    /// <summary>
    /// 委托积分奖励表要真读到 5 档，门槛 2/4/6/8/10，且奖励是 <c>[G-D-P-L-N]</c> 五元组。
    /// 取证 `VisitAward.txt`：第 1 档 <c>Point=2</c>、<c>Item1=[15-1-1-1-10000]</c>。
    /// </summary>
    [Fact]
    public void 委托奖励表_五档与门槛()
    {
        var t = T;
        Assert.Equal(5, t.VisitAwards.Count);
        Assert.Equal(new[] { 2, 4, 6, 8, 10 }, t.VisitAwards.OrderBy(r => r.Id).Select(r => r.Point));

        var first = t.VisitAwards.First(r => r.Id == 1);
        Assert.Single(first.Items);
        Assert.Equal(new[] { 15, 1, 1, 1, 10000 }, first.Items[0]);

        // 第 2 档有 3 项奖励
        Assert.Equal(3, t.VisitAwards.First(r => r.Id == 2).Items.Count);

        // 所有奖励都必须是五元组（GDPL+N），否则 Rewards.Grant 会拒
        foreach (var row in t.VisitAwards)
            foreach (var it in row.Items)
                Assert.Equal(5, it.Length);
    }

    /// <summary>
    /// 委托任务表要真读到 **2 个**（`VisitMission.txt` 共 5 行 = 3 行表头 + **2 行数据**：
    /// `1 擦墙 CleanWall1` / `2 扫地 RandomSaodi`），且 <c>Action</c> 非空
    /// （客户端 `girl:PlayAnimator(config.Action, ...)` 要用它播动画，`CafeLogic.lua:899`）。
    /// </summary>
    [Fact]
    public void 委托任务表_两个且有动作名()
    {
        var t = T;
        Assert.Equal(2, t.VisitMissions.Count);
        Assert.Equal(new[] { "CleanWall1", "RandomSaodi" },
                     t.VisitMissions.OrderBy(m => m.MissionId).Select(m => m.Action));
        foreach (var m in t.VisitMissions)
        {
            Assert.True(m.MissionId > 0);
            Assert.False(string.IsNullOrEmpty(m.Action), $"任务 {m.MissionId} 缺 Action");
        }
    }


    /// <summary>升级条件三件套要真读到（111 用）。</summary>
    [Fact]
    public void 等级表_升级条件非空()
    {
        var l2 = T.LevelRowFor(2)!;
        Assert.Equal(20, l2.NeedHot);      // NCafeLevels.txt L2: NeedHot=20
        Assert.Equal(500, l2.NeedGold);    //                     NeedGold=500
        Assert.Equal(1, l2.NeedLevel);
        Assert.True(l2.TotalHot > 0);
    }
}
