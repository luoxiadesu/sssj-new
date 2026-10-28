using System.Text.Json.Nodes;
using SssjServer.Game.Config;
using SssjServer.Game.Handlers;
using Xunit;

namespace SssjServer.Game.Tests;

/// <summary>
/// 少女养成/特训（`sFunction GirlLogic`）。取证见 `docs/05` §17.1..§17.4。
///
/// 断言重点是这一族**独有**的第五种约定（`GirlLogic.lua:18..42` 的 Dispatcher）：
/// ① 回包 **原样回 sCmd**（客户端自己加 `On` 前缀）；
/// ② 成功**绝不能带 `nError`**（真值判断，0 也算错）；
/// ③ 失败时客户端**只拿到错误码**，所以语义必须编码在数值里（2 = 栏位已满）；
/// ④ `Blocking.Hide()` 在 Dispatcher 里 ⇒ 每条命令都必须回包，否则界面卡遮罩。
/// </summary>
public class GirlTrainFeatureTests
{
    [Fact]
    public async Task OwnedUnplacedOutdoorImprovesRewardButUnownedOutdoorDoesNot()
    {
        using var h = await FeatureHarness.CreateAsync("train-owned-best");
        // Owning an outdoor in storage is sufficient; it need not be equipped.
        HouseFeature.WriteState(h.Player, new HouseState
        {
            Furniture = new() { new HouseFurniture(1000, 1400, 0, 0, 0, 0, 0, 0, 0) },
            NextObjId = 1001,
        });
        var girl = h.Player.GetGirl(1)!;
        girl.Level = 100;
        girl.Exp = 0;
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainTime, 1);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainOutId, 72);
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, "\"nId\":1");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(72u, girl.Exp);
        Assert.Equal(1300, Reply(h)["nMoney"]![4]!.GetValue<int>());
        // Shared table data must remain untouched by per-player maxima.
        var train = TrainTable.Load(TestPaths.SettingDir);
        Assert.Equal(60, train.TrainingFor(72, 1)!.LoveAward);
        Assert.Equal(1000, train.TrainingFor(72, 1)!.PlayerAwards[0][4]);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(201, true)]
    [InlineData(201, false)]
    public async Task TrainingOverflowSurvivesCapAndReloadAndCannotBeClaimedTwice(int id, bool alreadyMax)
    {
        using var h = await FeatureHarness.CreateAsync("train-overflow-" + id + alreadyMax);
        uint girlId = (uint)id;
        if (h.Player.GetGirl(girlId) is null) h.Player.Girls.Add(new Girl { Girlid = girlId });
        var girl = h.Player.GetGirl(girlId)!;
        var love = GirlLoveTable.Load(TestPaths.SettingDir);
        int cap = love.MaxLevel(girlId);
        girl.Level = (uint)(alreadyMax ? cap : cap - 1);
        girl.Exp = alreadyMax ? 2990u : (uint)(love.LevelUpExp(girlId, cap - 1) - 1);
        int group = GirlLoveFeature.VarGroup(girlId);
        foreach (var pair in new[] { (11, 11), (12, 1), (16, 72) })
            h.Player.SetTask(group, GirlLoveFeature.VarTaskId(girlId, pair.Item1), pair.Item2);
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, $"\"nId\":{id}");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal((uint)cap, girl.Level);
        Assert.Equal(alreadyMax ? 3050u : 59u, girl.Exp);
        Assert.Equal(60, Reply(h)["AddExpInfo"]![0]!.GetValue<int>());
        Assert.Equal(girl.Exp, new PlayerStore(h.Options).Load(h.Player.Account)!.GetGirl(girlId)!.Exp);
        uint saved = girl.Exp;
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, $"\"nId\":{id}");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(saved, girl.Exp);
    }

    [Fact]
    public async Task TrainingUsesEquippedOutdoorAndKeepsItsRewardAfterSwitch()
    {
        using var h = await FeatureHarness.CreateAsync("train-outdoor-reward");
        var house = HouseTable.Load(TestPaths.SettingDir);
        Assert.Equal(72, GirlTrainFeature.CurrentOutdoor(h.Player, house));
        await SendAsync(h, GirlTrainFeature.CmdStartTrain, "\"nId\":1,\"nPos\":11");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(72, GirlTrainFeature.GetVar(h.Player, 1, GirlTrainFeature.VarTrainOutId));
        await SendAsync(h, GirlTrainFeature.CmdInterruptTrain, "\"nId\":1");
        HouseFeature.WriteState(h.Player, new HouseState
        {
            Furniture = new() { new HouseFurniture(1000, 1000, 5, 22, 0, 0, 0, 0, 0) },
            NextObjId = 1001,
        });
        Assert.Equal(1000, GirlTrainFeature.CurrentOutdoor(h.Player, house));
        await SendAsync(h, GirlTrainFeature.CmdStartTrain, "\"nId\":1,\"nPos\":11");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(1000, GirlTrainFeature.GetVar(h.Player, 1, GirlTrainFeature.VarTrainOutId));
        // Switch back while training: completion must retain outdoor 1000's 70 affection.
        HouseFeature.WriteState(h.Player, new HouseState
        {
            Furniture = new() { new HouseFurniture(1000, 1000, 0, 0, 0, 0, 0, 0, 0) },
            NextObjId = 1001,
        });
        Assert.Equal(72, GirlTrainFeature.CurrentOutdoor(h.Player, house));
        h.Player.SetTask(3, GirlTrainFeature.VarTrainTime, 1);
        h.Player.GetGirl(1)!.Level = 1;
        h.Player.GetGirl(1)!.Exp = 0;
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, "\"nId\":1");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(70, Reply(h)["AddExpInfo"]![0]!.GetValue<int>());
        // Outdoor 1000 improves love (70), default 72 improves currency (1000 vs 800).
        Assert.Equal(1000, Reply(h)["nMoney"]![4]!.GetValue<int>());
    }

    [Fact]
    public void TrainingDoesNotBorrowRewardsFromAnotherOutdoor()
    {
        var train = TrainTable.Load(TestPaths.SettingDir);
        Assert.Equal(60, train.TrainingFor(72, 1)!.LoveAward);
        Assert.Equal(70, train.TrainingFor(1000, 1)!.LoveAward);
        Assert.Null(train.TrainingFor(int.MaxValue, 1));
        Assert.NotNull(train.TrainingFor(1400, 1));
    }

    [Fact]
    public async Task EndTrainActuallyAddsAffectionUnlocksStoryAndCannotClaimTwice()
    {
        using var h = await FeatureHarness.CreateAsync("train-affection-fix");
        var table = GirlLoveTable.Load(TestPaths.SettingDir);
        var girl = h.Player.GetGirl(1)!;
        girl.Level = 9;
        girl.Exp = (uint)(table.LevelUpExp(1, 9) - 1);
        var train = TrainTable.Load(TestPaths.SettingDir);
        var slot = train.PosOf(11)!;
        int storyTask = GirlLoveFeature.VarTaskId(1, GirlLoveFeature.ELevelOffset(5));
        h.Player.SetTask(3, storyTask, 0);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 11);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainTime, 1);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainOutId, slot.OutdoorId);
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, "\"nId\":1");
        var reply = Reply(h);
        Assert.Null(reply["nError"]);
        Assert.Equal(5, Assert.IsType<JsonArray>(reply["AddExpInfo"]).Count);
        Assert.Equal(5, Assert.IsType<JsonArray>(reply["nMoney"]).Count);
        Assert.True(girl.Level >= 10);
        Assert.Equal(1, h.Player.GetTask(3, storyTask));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(girl.Exp, saved.GetGirl(1)!.Exp);
        Assert.Equal(1, saved.GetTask(3, storyTask));
        uint after = girl.Exp;
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, "\"nId\":1");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(after, girl.Exp);
    }

    [Fact]
    public async Task LinkGirlTrainingUsesGroup90AndDoesNotTouchOrdinaryGirl()
    {
        using var h = await FeatureHarness.CreateAsync("link-train-vars");
        h.Player.Girls.Add(new Girl { Girlid = 201, Level = 1 });
        int pos = TrainTable.Load(TestPaths.SettingDir).Positions.Keys.First();
        int linkTask = GirlLoveFeature.VarTaskId(201, GirlTrainFeature.VarTrainPos);
        h.Player.SetTask(3, GirlTrainFeature.VarTrainPos, 777);
        await SendAsync(h, GirlTrainFeature.CmdStartTrain, $"\"nId\":201,\"nPos\":{pos}");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(pos, h.Player.GetTask(90, linkTask));
        Assert.Equal(777, h.Player.GetTask(3, GirlTrainFeature.VarTrainPos));
        var saved = new PlayerStore(h.Options).Load(h.Player.Account)!;
        Assert.Equal(pos, saved.GetTask(90, linkTask));
        await SendAsync(h, GirlTrainFeature.CmdInterruptTrain, "\"nId\":201");
        Assert.Null(Reply(h)["nError"]);
        Assert.Equal(0, h.Player.GetTask(90, linkTask));
        Assert.Equal(777, h.Player.GetTask(3, GirlTrainFeature.VarTrainPos));
    }

    [Fact]
    public async Task TrainingCannotExceedCafeCapacityAndFailedStartKeepsState()
    {
        using var h = await FeatureHarness.CreateAsync("training-total-cap");
        int cap = CafeTable.Load(TestPaths.SettingDir).LevelRowFor(1)!.OpenTrain;
        Assert.Equal(4, cap);
        int[] positions = { 11, 12, 13, 21, 22 };
        for (uint id = 1; id <= 5; id++)
            if (h.Player.GetGirl(id) is null) h.Player.Girls.Add(new Girl { Girlid = id, Level = 1 });
        var girls = Enumerable.Range(1, 5).Select(id => h.Player.GetGirl((uint)id)!).ToList();
        for (int i = 0; i < 4; i++)
        {
            await SendAsync(h, GirlTrainFeature.CmdStartTrain,
                $"\"nId\":{girls[i].Girlid},\"nPos\":{positions[i]}");
            Assert.Null(Reply(h)["nError"]);
        }
        await SendAsync(h, GirlTrainFeature.CmdStartTrain,
            $"\"nId\":{girls[4].Girlid},\"nPos\":{positions[4]}");
        Assert.NotNull(Reply(h)["nError"]);
        Assert.Equal(0, GirlTrainFeature.GetVar(h.Player, girls[4].Girlid, GirlTrainFeature.VarTrainPos));
        await SendAsync(h, GirlTrainFeature.CmdInterruptTrain, $"\"nId\":{girls[0].Girlid}");
        await SendAsync(h, GirlTrainFeature.CmdStartTrain,
            $"\"nId\":{girls[4].Girlid},\"nPos\":{positions[4]}");
        Assert.Null(Reply(h)["nError"]);
    }

    private static JsonObject Reply(FeatureHarness h)
    {
        var (function, json) = h.LastOutbound;
        Assert.Equal(GirlTrainFeature.Function, function);
        return Assert.IsType<JsonObject>(JsonNode.Parse(json));
    }

    private static Task SendAsync(FeatureHarness h, string sCmd, string extra = "")
        => h.DispatchFunctionAsync(GirlTrainFeature.Function,
            $"{{\"sCmd\":\"{sCmd}\"{(extra.Length > 0 ? "," + extra : "")}}}");

    /// <summary>★ 成功回包必须原样带回 sCmd（客户端找 `On<sCmd>`），且不能带 nError。</summary>
    [Theory]
    [InlineData(GirlTrainFeature.CmdAddTrainPos, "")]
    [InlineData(GirlTrainFeature.CmdHeadTouched, "\"nId\":1")]
    public async Task Success_EchoesSCmdAndOmitsNError(string sCmd, string extra)
    {
        using var h = await FeatureHarness.CreateAsync($"train-ok-{sCmd}");

        await SendAsync(h, sCmd, extra);

        var r = Reply(h);
        Assert.Equal(sCmd, r["sCmd"]!.GetValue<string>());
        Assert.False(r.ContainsKey("nError"),
            "成功回包带了 nError —— 客户端是真值判断，会走 On<sCmd>Error 分支");
    }

    /// <summary>★ 栏位满时必须回**错误码 2** —— 客户端只拿到这个数字，`nCode == 2` 才弹「已满」提示。</summary>
    [Fact]
    public async Task AddTrainPos_WhenFull_RepliesErrorCodeTwo()
    {
        using var h = await FeatureHarness.CreateAsync("train-full");
        h.Player.SetTask(GmUnlock.GirlVarGroup, GirlTrainFeature.TrainPosCountTaskId,
                         GirlTrainFeature.MaxTrainPosCount);

        await SendAsync(h, GirlTrainFeature.CmdAddTrainPos);

        var r = Reply(h);
        Assert.Equal(GirlTrainFeature.ErrPosFull, r["nError"]!.GetValue<int>());
        Assert.Equal(GirlTrainFeature.CmdAddTrainPos, r["sCmd"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddTrainPos_IncrementsCount()
    {
        using var h = await FeatureHarness.CreateAsync("train-add");
        int before = GirlTrainFeature.TrainPosCount(h.Player);

        await SendAsync(h, GirlTrainFeature.CmdAddTrainPos);

        Assert.Equal(before + 1, GirlTrainFeature.TrainPosCount(h.Player));
        // 属性变更必须推 pf 1026，客户端才读得到
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    // ================= StartTrain =================

    /// <summary>
    /// ★★ 开始特训必须把 **TrainPos(11) / TrainTime(12) / TrainOutId(16)** 写进 group 3
    /// 并推 pf 1026 —— 因为 `OnStartTrain` 只从回包拿 `nId`，其余全靠
    /// `GirlCommon:Get(nId,'TrainPos'/'TrainTime')` 读属性（`GirlLogic.lua:98..108`）。
    /// </summary>
    [Fact]
    public async Task StartTrain_WritesGirlVarsAndPushesThem()
    {
        using var h = await FeatureHarness.CreateAsync("train-start");
        var table = TrainTable.Load(TestPaths.SettingDir);
        int pos = table.Positions.Keys.First();
        uint girl = h.Player.Girls.First().Girlid;

        await SendAsync(h, GirlTrainFeature.CmdStartTrain, $"\"nId\":{girl},\"nPos\":{pos}");

        var r = Reply(h);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal((int)girl, r["nId"]!.GetValue<int>());

        Assert.Equal(pos, GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainPos));
        long endAt = GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainTime);
        Assert.True(endAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "TrainTime 必须是未来的时间戳");
        Assert.True(GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainOutId) > 0);
        Assert.True(await h.WaitForPfAsync(GamePf.TaskValueSyncRsp));
    }

    /// <summary>非法输入一律回错误码而不是崩：未持有的少女 / 表外 pos / pos<=0。</summary>
    [Theory]
    [InlineData("\"nId\":999999,\"nPos\":11")]
    [InlineData("\"nId\":1,\"nPos\":0")]
    [InlineData("\"nId\":1,\"nPos\":999999")]
    public async Task StartTrain_InvalidInput_RepliesError(string extra)
    {
        using var h = await FeatureHarness.CreateAsync($"train-bad-{extra.Length}");

        await SendAsync(h, GirlTrainFeature.CmdStartTrain, extra);

        Assert.True(Reply(h).ContainsKey("nError"));
    }

    /// <summary>同一个位置不能塞两个少女；同一个少女不能重复开始。</summary>
    [Fact]
    public async Task StartTrain_RejectsDuplicatePosOrGirl()
    {
        using var h = await FeatureHarness.CreateAsync("train-dup");
        var table = TrainTable.Load(TestPaths.SettingDir);
        int pos = table.Positions.Keys.First();
        var girls = h.Player.Girls.Take(2).Select(g => g.Girlid).ToList();
        Assert.Equal(2, girls.Count);

        await SendAsync(h, GirlTrainFeature.CmdStartTrain, $"\"nId\":{girls[0]},\"nPos\":{pos}");
        Assert.False(Reply(h).ContainsKey("nError"));

        // 同一个位置换个少女 → 拒
        await SendAsync(h, GirlTrainFeature.CmdStartTrain, $"\"nId\":{girls[1]},\"nPos\":{pos}");
        Assert.True(Reply(h).ContainsKey("nError"));

        // 同一个少女换个位置（她已在特训中）→ 拒
        int other = table.Positions.Keys.Skip(1).First();
        await SendAsync(h, GirlTrainFeature.CmdStartTrain, $"\"nId\":{girls[0]},\"nPos\":{other}");
        Assert.True(Reply(h).ContainsKey("nError"));
    }

    // ================= EndTrain / InterruptTrain =================

    /// <summary>时间没到就结算 → 拒（服务端兜底，客户端本地也会拦）。</summary>
    [Fact]
    public async Task EndTrain_BeforeDeadline_IsRejected()
    {
        using var h = await FeatureHarness.CreateAsync("train-early");
        var table = TrainTable.Load(TestPaths.SettingDir);
        uint girl = h.Player.Girls.First().Girlid;

        await SendAsync(h, GirlTrainFeature.CmdStartTrain,
                        $"\"nId\":{girl},\"nPos\":{table.Positions.Keys.First()}");
        await SendAsync(h, GirlTrainFeature.CmdEndTrain, $"\"nId\":{girl}");

        Assert.True(Reply(h).ContainsKey("nError"));
        // 属性没被清掉
        Assert.True(GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainPos) > 0);
    }

    /// <summary>时间到了就结算：清属性、回 nId/nMoney/AddExpInfo（`GirlLogic.lua:305..307`）。</summary>
    [Fact]
    public async Task EndTrain_AfterDeadline_ClearsVarsAndPaysLove()
    {
        using var h = await FeatureHarness.CreateAsync("train-end");
        var table = TrainTable.Load(TestPaths.SettingDir);
        uint girl = h.Player.Girls.First().Girlid;

        await SendAsync(h, GirlTrainFeature.CmdStartTrain,
                        $"\"nId\":{girl},\"nPos\":{table.Positions.Keys.First()}");
        // 把结束时间拨到过去
        h.Player.SetTask(GmUnlock.GirlVarGroup,
                         GmUnlock.GirlTaskId(girl, GirlTrainFeature.VarTrainTime), 1);

        await SendAsync(h, GirlTrainFeature.CmdEndTrain, $"\"nId\":{girl}");

        var r = Reply(h);
        Assert.False(r.ContainsKey("nError"));
        Assert.Equal((int)girl, r["nId"]!.GetValue<int>());
        Assert.NotNull(r["nMoney"]);            // 客户端 OnEndTrain 会读这两个
        Assert.NotNull(r["AddExpInfo"]);
        // 属性已清
        Assert.Equal(0, GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainPos));
        Assert.Equal(0, GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainTime));
    }

    /// <summary>打断：清属性、不发奖。</summary>
    [Fact]
    public async Task InterruptTrain_ClearsVars()
    {
        using var h = await FeatureHarness.CreateAsync("train-interrupt");
        var table = TrainTable.Load(TestPaths.SettingDir);
        uint girl = h.Player.Girls.First().Girlid;

        await SendAsync(h, GirlTrainFeature.CmdStartTrain,
                        $"\"nId\":{girl},\"nPos\":{table.Positions.Keys.First()}");
        await SendAsync(h, GirlTrainFeature.CmdInterruptTrain, $"\"nId\":{girl}");

        Assert.False(Reply(h).ContainsKey("nError"));
        Assert.Equal(0, GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainPos));
    }

    /// <summary>没在特训的少女被打断/结算 → 回错误码，不崩。</summary>
    [Theory]
    [InlineData(GirlTrainFeature.CmdInterruptTrain)]
    [InlineData(GirlTrainFeature.CmdEndTrain)]
    public async Task NotTraining_RepliesError(string sCmd)
    {
        using var h = await FeatureHarness.CreateAsync($"train-none-{sCmd}");
        await SendAsync(h, sCmd, $"\"nId\":{h.Player.Girls.First().Girlid}");
        Assert.True(Reply(h).ContainsKey("nError"));
    }

    /// <summary>多人特训：部分成功即算成功。</summary>
    [Fact]
    public async Task StartManyTrain_PartialSuccessIsSuccess()
    {
        using var h = await FeatureHarness.CreateAsync("train-many");
        var table = TrainTable.Load(TestPaths.SettingDir);
        int pos = table.Positions.Keys.First();
        uint girl = h.Player.Girls.First().Girlid;

        await h.DispatchFunctionAsync(GirlTrainFeature.Function,
            $"{{\"sCmd\":\"{GirlTrainFeature.CmdStartManyTrain}\",\"data\":" +
            $"[{{\"nId\":{girl},\"nPos\":{pos}}},{{\"nId\":999999,\"nPos\":{pos}}}]}}");

        Assert.False(Reply(h).ContainsKey("nError"));
        Assert.Equal(pos, GirlTrainFeature.GetVar(h.Player, girl, GirlTrainFeature.VarTrainPos));
    }

    // ================= 配置表 / 常量 =================

    /// <summary>两张表能载入，且现网数字钉住（换表时会提醒）。</summary>
    [Fact]
    public void TrainTable_LoadsBothFiles()
    {
        var table = TrainTable.Load(TestPaths.SettingDir);
        Assert.NotEmpty(table.Positions);
        Assert.NotEmpty(table.Trainings);
        // 现网：TrainingGirl.txt ★ 第十轮换新版 setting.u 后 40 → **44** 条
        Assert.Equal(44, table.Trainings.Count);
        // 时长与好感必须解析出来（外景 72 类型 1 是 3600s / +60）
        var row = table.Trainings.First(t => t.Time > 0);
        Assert.True(row.Time > 0 && row.LoveAward > 0);
    }

    /// <summary>`Award2` 的 GDPLN 解析（现网 `15-1-1-1-1000`）。</summary>
    [Fact]
    public void ParseGdpln_HandlesRealFormat()
    {
        var one = TrainTable.ParseGdpln("15-1-1-1-1000");
        Assert.Equal(new[] { 15, 1, 1, 1, 1000 }, Assert.Single(one));
        Assert.Empty(TrainTable.ParseGdpln(""));
        Assert.Empty(TrainTable.ParseGdpln("1-2-3"));           // 位数不足丢弃
        Assert.Empty(TrainTable.ParseGdpln("15-1-1-1-0"));      // 数量 0 丢弃
    }

    /// <summary>少女属性偏移与 `GirlCommon.lua:30..56` 一致。</summary>
    [Fact]
    public void GirlVarOffsets_MatchClient()
    {
        Assert.Equal(11, GirlTrainFeature.VarTrainPos);
        Assert.Equal(12, GirlTrainFeature.VarTrainTime);
        Assert.Equal(16, GirlTrainFeature.VarTrainOutId);
        Assert.Equal(3, GmUnlock.GirlVarGroup);
        Assert.Equal(2, GirlTrainFeature.ErrPosFull);
    }

    /// <summary>畸形上行不抛。</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"sCmd":"NoSuchCommand"}""")]
    [InlineData("""{"sCmd":"StartTrain"}""")]
    public async Task MalformedParam_DoesNotThrow(string json)
    {
        using var h = await FeatureHarness.CreateAsync($"train-mal-{json.Length}");
        await h.DispatchFunctionAsync(GirlTrainFeature.Function, json);
    }

    /// <summary>
    /// ★★ <b>两个功能域共用 <c>sFunction=GirlLogic</c> 时互不吞命令</b>（本轮新增的回归闸）。
    ///
    /// 背景：<see cref="SssjServer.Core.LuaCallRouter.MapFunction"/> 是覆盖式的，
    /// 两边都用它会让**后装配者把前者整域吃掉**（连 unhandled 日志都没有）。
    /// 现在特训域持有主 handler、好感度域走
    /// <see cref="SssjServer.Core.LuaCallRouter.AddFunctionCoHandler"/> 协处理器。
    ///
    /// 这条用例同时发**两域各一条**命令并断言两条都被各自的域应答 ——
    /// 任何一边被吞掉都会红。
    /// </summary>
    [Fact]
    public async Task GirlLogic_TwoFeaturesShareFunction_NeitherSwallowsTheOther()
    {
        using var h = await FeatureHarness.CreateAsync("girllogic-share");

        // ① 特训域的命令（主 handler）
        await SendAsync(h, GirlTrainFeature.CmdAddTrainPos);
        var trainReply = Reply(h);
        Assert.Equal(GirlTrainFeature.CmdAddTrainPos, trainReply["sCmd"]!.GetValue<string>());

        // ② 好感度域的命令（协处理器）—— 未持有少女 99 ⇒ 回错误码，但**必须有回包**
        await h.DispatchFunctionAsync(GirlTrainFeature.Function,
            $"{{\"sCmd\":\"{GirlLoveFeature.CmdGirlGift}\",\"nId\":99," +
            "\"tbItem\":[5,1,1,2],\"ItemNum\":1}");
        var giftReply = Reply(h);
        Assert.Equal(GirlLoveFeature.CmdGirlGift, giftReply["sCmd"]!.GetValue<string>());
        Assert.True(giftReply.ContainsKey("nError"),
            "好感度域的命令没有被协处理器接住 —— 它被特训域的 Unknown 吞了");
    }
}
