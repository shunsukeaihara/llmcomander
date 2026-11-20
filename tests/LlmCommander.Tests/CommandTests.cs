using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

public class CommandTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void Run(World w, double seconds)
    {
        for (int i = 0; i < seconds / Defs.Dt; i++) w.Step();
    }

    /// <summary>自軍の予備に兵を出す。</summary>
    private static void Army(World w, int team, UnitKind kind, int n, Vector2 at)
    {
        for (int i = 0; i < n; i++) w.SpawnUnit(team, kind, at + new Vector2(i % 4 * 0.8f, i / 4 * 0.8f));
    }

    [Fact]
    public void ToolSchemasAreValidObjects()
    {
        foreach (var t in Tools.CommanderTools.Concat(Tools.OfficerTools))
            Assert.Equal("object", t.Schema.GetProperty("type").GetString());
    }

    [Fact]
    public void RoleUpdateOverridesOnlyMentionedAspects()
    {
        var w = MapLayout.CreateValley();
        Army(w, 0, UnitKind.Spearman, 6, new Vector2(25, 55));
        Army(w, 0, UnitKind.Archer, 6, new Vector2(25, 57));
        Army(w, 0, UnitKind.Cavalry, 4, new Vector2(27, 57));
        var ap = new Applier(w, 0);

        var r1 = ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "戦線A", "change_summary": "北の丘に布陣",
             "roles": [
               {"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "北の丘", "formation": "line"},
               {"role": "弓", "unit_kind": "archer", "stance": "hold", "place": "北の丘", "priority": ["spearman"]},
               {"role": "騎兵", "unit_kind": "cavalry", "stance": "hold", "place": "中央",
                "trigger": {"condition": "role_engaged", "role": "槍", "then": {"stance": "advance", "place": "北の丘"}, "description": "槍が接触したら突撃"}}
             ]}
            """)), "北の丘で迎え撃て", Array.Empty<int>());
        Assert.StartsWith("作成", r1);
        var op = w.Teams[0].FindOp("戦線A")!;
        Assert.Equal(6, w.Members(op, op.FindRole("槍")!).Count());
        Assert.Equal(4, w.Members(op, op.FindRole("騎兵")!).Count());
        Assert.Empty(w.Members(w.Teams[0].Reserve));

        // 「弓は引き撃ち、他は撤退」
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "戦線A", "change_summary": "弓は引き撃ち、他は撤退",
             "roles": [
               {"role": "弓", "kite": true},
               {"role": "槍", "stance": "retreat", "place": "own_base"},
               {"role": "騎兵", "stance": "retreat", "place": "own_base", "clear_trigger": true}
             ]}
            """)), "弓は引き撃ち、他は撤退", Array.Empty<int>());
        var archer = op.FindRole("弓")!;
        Assert.True(archer.Kite);
        Assert.Equal(Stance.Hold, archer.Stance);              // 言及していない項目は残る
        Assert.Equal(new[] { UnitKind.Spearman }, archer.Priority);
        Assert.Equal(Stance.Retreat, op.FindRole("槍")!.Stance);
        Assert.Contains("北の丘で待機", op.FindRole("槍")!.Superseded); // 置き換えた内容を残す
        Assert.Null(op.FindRole("騎兵")!.Trigger);
    }

    [Fact]
    public void TriggerFiresWhenRoleEngages()
    {
        var w = MapLayout.CreateValley();
        Army(w, 0, UnitKind.Spearman, 4, new Vector2(40, 30));
        Army(w, 0, UnitKind.Cavalry, 3, new Vector2(30, 40));
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "前線", "change_summary": "",
             "roles": [
               {"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "北東の林"},
               {"role": "騎兵", "unit_kind": "cavalry", "stance": "hold", "place": "中央",
                "trigger": {"condition": "role_engaged", "role": "槍", "then": {"stance": "advance", "place": "北東の林"}, "description": "槍が接触したら突撃"}}
             ]}
            """)), "", Array.Empty<int>());
        var op = w.Teams[0].FindOp("前線")!;
        Run(w, 3);
        Assert.NotNull(op.FindRole("騎兵")!.Trigger);
        Army(w, 1, UnitKind.Archer, 3, new Vector2(44, 25));
        Run(w, 20);
        Assert.Null(op.FindRole("騎兵")!.Trigger);
        Assert.Equal(Stance.Advance, op.FindRole("騎兵")!.Stance);
        Assert.Contains(op.Changes, c => c.Text.StartsWith("条件発動"));
    }

    [Fact]
    public void EconomyAllocationFollowsTargets()
    {
        var w = MapLayout.CreateValley();
        for (int i = 0; i < 6; i++) w.SpawnUnit(0, UnitKind.Villager, new Vector2(18, 62));
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("set_economy", J("""{"gather": {"food": 4, "wood": 5, "gold": 3}, "change_summary": "x"}""")), "", Array.Empty<int>());
        Run(w, 3);
        var c = w.GatherCounts(0);
        Assert.Equal(4, c[Res.Food]);
        Assert.Equal(5, c[Res.Wood]);
        Assert.Equal(3, c[Res.Gold]);
        Run(w, 60);
        Assert.True(w.Teams[0].Stock[Res.Wood] > 200, "木が集まっている");
        Assert.True(w.Teams[0].Stock[Res.Gold] > 100, "金が集まっている");
    }

    [Fact]
    public void BuildIsProposedThenConfirmed()
    {
        var w = MapLayout.CreateValley();
        w.Teams[0].Stock[Res.Wood] = 1000;
        var ap = new Applier(w, 0);
        var r = ap.Apply(new ToolCall("propose_build", J("""{"building": "barracks", "near": "own_base"}""")), "", Array.Empty<int>());
        Assert.Contains("確認待ち", r);
        Run(w, 5);
        Assert.DoesNotContain(w.TeamBuildings(0), b => b.Kind == BuildingKind.Barracks); // 確認前は建たない
        ap.Apply(new ToolCall("confirm_build", J("{}")), "", Array.Empty<int>());
        Run(w, 60);
        Assert.Contains(w.TeamBuildings(0), b => b.Kind == BuildingKind.Barracks && b.Done);

        var r2 = ap.Apply(new ToolCall("propose_build", J("""{"building": "town_center", "near": "new_gold"}""")), "", Array.Empty<int>());
        Assert.Contains("確認待ち", r2);
    }

    [Fact]
    public void SpearmenBeatCavalry()
    {
        var w = MapLayout.CreateValley();
        Army(w, 0, UnitKind.Spearman, 8, new Vector2(38, 30));
        Army(w, 1, UnitKind.Cavalry, 5, new Vector2(45, 30));
        var ap = new Applier(w, 1);
        ap.Apply(new ToolCall("update_operation", J("""{"operation": "突撃", "change_summary": "", "roles": [{"role": "騎兵", "unit_kind": "cavalry", "stance": "advance", "place": "北東の林"}]}""")), "", Array.Empty<int>());
        new Applier(w, 0).Apply(new ToolCall("update_operation", J("""{"operation": "守り", "change_summary": "", "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "北東の林"}]}""")), "", Array.Empty<int>());
        Run(w, 60);
        Assert.DoesNotContain(w.TeamUnits(1), u => u.Kind == UnitKind.Cavalry);
        Assert.Contains(w.TeamUnits(0), u => u.Kind == UnitKind.Spearman);
    }

    private sealed class FakeLlm : ILlm
    {
        public LlmRequest? Last;
        public Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
        {
            Last = request;
            var calls = new List<ToolCall>
            {
                new("set_economy", J("""{"gather": {"wood": 3}, "change_summary": "木に3人"}""")),
            };
            return Task.FromResult(new LlmReply(calls, "", 0.5));
        }
    }

    [Fact]
    public void CommandCenterAppliesReplyOnMainThread()
    {
        var w = MapLayout.CreateValley();
        var llm = new FakeLlm();
        var cc = new CommandCenter(w, 0, llm, null, new LlmSettings { OfficersEnabled = false });
        var entry = cc.SubmitText("木に3人回して", new UtteranceContext { Clicks = { new Vector2(30, 50) } });
        Assert.True(entry.Pending);
        for (int i = 0; i < 1000 && entry.Pending; i++)
        {
            Thread.Sleep(10);
            cc.Update();
        }
        Assert.False(entry.Pending);
        Assert.Equal(3, w.Teams[0].Gather[Res.Wood]);
        Assert.Contains("指示点1", llm.Last!.User);
        Assert.Contains(w.Teams[0].Markers, m => m.Name == "指示点1");
    }
}
