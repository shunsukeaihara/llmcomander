using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

public class ScoutTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void Run(World w, double seconds)
    {
        for (int i = 0; i < seconds / Defs.Dt; i++) w.Step();
    }

    private static int Explored(World w, int team) => w.Vision[team].Explored.Count(e => e);

    private static Unit StartingScout(World w, int team) => w.TeamUnits(team).Single(u => u.Kind == UnitKind.Scout);

    [Fact]
    public void ScoutMissionKeepsMovingAndExpandsWithoutAnLlm()
    {
        var w = MapLayout.CreateValley();
        var scout = StartingScout(w, 0);
        int before = Explored(w, 0);
        var r = new Applier(w, 0).Apply(new ToolCall("scout", J("""
            {"name": "偵察", "objective": "村の中心を中心に同心円状に少しずつ探索", "center": "own_base"}
            """)), "斥候で周りを探索して", Array.Empty<int>());
        Assert.StartsWith("偵察開始", r);
        var op = w.Teams[0].FindOp("偵察")!;
        Assert.True(op.IsScouting);
        Assert.Contains(scout, w.Members(op));

        // LLM がいなくても、着くたびに自動で次の行き先へ向かい、止まらない
        var start = scout.Pos;
        Run(w, 20);
        Assert.True(Vector2.Distance(scout.Pos, start) > 3, "動いている");
        int after20 = Explored(w, 0);
        Assert.True(after20 > before * 1.3, $"{before} → {after20}");
        Assert.True(Vector2.Distance(scout.Pos, w.BasePos(0)) < 25, "最初は中心の近く");
        Run(w, 60);
        Assert.True(Explored(w, 0) > after20);
        Assert.Contains(op.Changes, c => c.Text.StartsWith("到着"));
        Assert.Contains(op.Changes, c => c.Text.StartsWith("自動: 次の行き先"));
    }

    [Fact]
    public void ASecondScoutOrderRetargetsTheRunningMission()
    {
        var w = MapLayout.CreateValley();
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("scout", J("""{"name": "偵察", "objective": "周りを探索"}""")), "周りを探索して", Array.Empty<int>());
        var r = ap.Apply(new ToolCall("scout", J("""{"name": "敵偵察", "objective": "敵を探して遠巻きに見る"}""")), "敵を探して", Array.Empty<int>());
        Assert.StartsWith("偵察の目的を変更", r);
        var op = w.Teams[0].FindOp("偵察")!;
        Assert.Equal("敵を探して遠巻きに見る", op.ScoutObjective);
        Assert.Single(w.Teams[0].ActiveOps, o => o.IsScouting);
    }

    [Fact]
    public void ScoutMissionNeedsAScout()
    {
        var w = MapLayout.CreateValley();
        StartingScout(w, 0).Hp = 0;
        Run(w, 0.1);
        var r = new Applier(w, 0).Apply(new ToolCall("scout", J("""{"objective": "探索"}""")), "", Array.Empty<int>());
        Assert.Contains("いません", r);
    }

    /// <summary>偵察の担当は、見せた未探索点から次の行き先を選ぶ。</summary>
    private sealed class WaypointLlm : ILlm
    {
        public int Calls;
        public LlmRequest? Last;
        public Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            Last = request;
            var calls = new[]
            {
                new ToolCall("set_waypoint", J("""{"place": "未探索点9", "reason": "遠くを確認"}""")),
                new ToolCall("report", J("""{"status": "北側を探索中"}""")),
            };
            return Task.FromResult(new LlmReply(calls, "", 0.2));
        }
    }

    [Fact]
    public void ScoutingLlmPicksTheNextWaypoint()
    {
        var w = MapLayout.CreateValley();
        var llm = new WaypointLlm();
        var cc = new CommandCenter(w, 0, llm, null, new LlmSettings());
        new Applier(w, 0).Apply(new ToolCall("scout", J("""{"name": "偵察", "objective": "敵の金鉱を探して", "center": "enemy_base"}""")), "", Array.Empty<int>());
        var op = w.Teams[0].FindOp("偵察")!;
        for (int i = 0; i < 400 && op.Status == ""; i++)
        {
            w.Step();
            Thread.Sleep(2);
            cc.Update();
        }
        Assert.Equal("北側を探索中", op.Status);
        Assert.StartsWith("未探索点9", op.Roles[0].Anchor.Name);
        Assert.Contains(op.Changes, c => c.Text.StartsWith("次の行き先: 未探索点9"));
        // 敵の本拠地はまだ見つけていないので、知っているかのような情報は渡さない
        Assert.Contains("敵の本拠地: まだ見つけていない", llm.Last!.User);
        Assert.DoesNotContain("敵基地から", llm.Last.User);
        Assert.Contains("敵の金鉱を探して", llm.Last!.User);
        Assert.Contains("未探索点1", llm.Last.User);
        Assert.Equal(Tools.ScoutSystem, llm.Last.System);
    }

    [Fact]
    public void PatrolCirclesAtTheGivenRadius()
    {
        var w = MapLayout.CreateValley();
        var scout = StartingScout(w, 0);
        new Applier(w, 0).Apply(new ToolCall("update_operation", J("""
            {"operation": "遠巻き", "change_summary": "", "roles": [{"role": "斥候", "unit_kind": "scout", "stance": "patrol", "place": "中央", "radius": 10}]}
            """)), "", Array.Empty<int>());
        var center = w.Map.FindRegion("center")!.Center;
        Run(w, 25); // 輪に着くまで
        var angles = new List<float>();
        for (int i = 0; i < 30; i++)
        {
            Run(w, 1);
            float d = Vector2.Distance(scout.Pos, center);
            Assert.InRange(d, 5f, 16f);
            angles.Add(MathF.Atan2(scout.Pos.Y - center.Y, scout.Pos.X - center.X));
        }
        // 輪の上を動いている(角度が広く散らばる)
        Assert.True(angles.Max() - angles.Min() > 1.5f);
    }

    [Fact]
    public void EvadingLookoutDoesNotFightAndRunsAway()
    {
        var w = MapLayout.CreateValley();
        var scout = StartingScout(w, 0);
        w.Teams[0].Markers.Add(new Marker { Name = "見張り場", Points = { new Vector2(30, 50) } });
        new Applier(w, 0).Apply(new ToolCall("update_operation", J("""
            {"operation": "見張り", "change_summary": "", "roles": [{"role": "斥候", "unit_kind": "scout", "stance": "hold", "place": "見張り場", "evade": true}]}
            """)), "", Array.Empty<int>());
        Run(w, 15);
        Assert.True(Vector2.Distance(scout.Pos, new Vector2(30, 50)) < 3);
        var enemy = w.SpawnUnit(1, UnitKind.Spearman, new Vector2(33, 50));
        w.Teams[1].Markers.Add(new Marker { Name = "陣", Points = { new Vector2(33, 50) } });
        new Applier(w, 1).Apply(new ToolCall("update_operation", J("""
            {"operation": "守り", "change_summary": "", "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "陣"}]}
            """)), "", Array.Empty<int>());
        Run(w, 2);
        Assert.Equal(enemy.MaxHp, enemy.Hp);                         // 攻撃しない
        Assert.True(Vector2.Distance(scout.Pos, enemy.Pos) > 4.5f);   // 距離を取る
        Assert.Contains("交戦せず逃げる", w.Teams[0].FindOp("見張り")!.FindRole("斥候")!.Describe());
    }
}

public class GarrisonTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void Run(World w, double seconds)
    {
        for (int i = 0; i < seconds / Defs.Dt; i++) w.Step();
    }

    [Fact]
    public void VillagersGarrisonAndTheTownCenterShootsArrowsPerVillager()
    {
        var w = MapLayout.CreateValley();
        var tc = w.TeamBuildings(0).Single(b => b.Kind == BuildingKind.TownCenter);
        var r = new Applier(w, 0).Apply(new ToolCall("garrison", J("""{"action": "enter"}""")), "", Array.Empty<int>());
        Assert.Contains("6人", r);
        Run(w, 10);
        Assert.Equal(6, w.InsideCount(tc));
        Assert.Contains("こもっている村人: 6人", Context.EconomyText(w, w.Teams[0]));

        // 近づいた敵兵に、中の人数ぶんの矢が飛ぶ
        var enemy = w.SpawnUnit(1, UnitKind.Cavalry, tc.Pos + new Vector2(4, -4));
        w.UpdateVision();
        int shotsBefore = w.Shots.Count;
        Run(w, 0.1);
        Assert.Equal(6, w.Shots.Count(s => s.From == tc.Pos) - 0);
        Assert.True(enemy.Hp < enemy.MaxHp);
        // 中の村人は攻撃されない
        Assert.All(w.TeamUnits(0).Where(u => u.IsInside), u => Assert.Equal(u.MaxHp, u.Hp));

        var r2 = new Applier(w, 0).Apply(new ToolCall("garrison", J("""{"action": "leave"}""")), "", Array.Empty<int>());
        Assert.Contains("6人", r2);
        Assert.Equal(0, w.InsideCount(tc));
    }

    [Fact]
    public void EmptyTownCenterDoesNotShoot()
    {
        var w = MapLayout.CreateValley();
        var tc = w.TeamBuildings(0).Single(b => b.Kind == BuildingKind.TownCenter);
        var enemy = w.SpawnUnit(1, UnitKind.Cavalry, tc.Pos + new Vector2(4, -4));
        Run(w, 2);
        Assert.DoesNotContain(w.Shots, s => s.From == tc.Pos);
    }
}

public class ManualOrderTests
{
    private static void Run(World w, double seconds)
    {
        for (int i = 0; i < seconds / Defs.Dt; i++) w.Step();
    }

    [Fact]
    public void RightClickMovesAttacksGathersAndGarrisons()
    {
        var w = MapLayout.CreateValley();
        var ap = new Applier(w, 0);
        var spears = Enumerable.Range(0, 3).Select(_ => w.SpawnUnit(0, UnitKind.Spearman, new Vector2(24, 56))).ToList();

        // 地面: 移動して待機
        var r = ap.ManualOrder(spears.Select(u => u.Id).ToList(), new Vector2(30, 46));
        Assert.Contains("移動して待機", r);
        var op = w.Teams[0].ActiveOps.Single(o => o.IsManual);
        Run(w, 20);
        Assert.All(spears, u => Assert.True(Vector2.Distance(u.Pos, new Vector2(30, 46)) < 3));

        // 敵兵: 攻撃
        // (敵の弓はその場で待機させる。歩き去る弓兵には槍は追いつけない)
        var enemy = w.SpawnUnit(1, UnitKind.Archer, new Vector2(33, 44));
        w.Teams[1].Markers.Add(new Marker { Name = "陣", Points = { new Vector2(33, 44) } });
        new Applier(w, 1).Apply(new ToolCall("update_operation", JsonDocument.Parse("""
            {"operation": "守り", "change_summary": "", "roles": [{"role": "弓", "unit_kind": "archer", "stance": "hold", "place": "陣"}]}
            """).RootElement.Clone()), "", Array.Empty<int>());
        w.UpdateVision();
        r = ap.ManualOrder(spears.Select(u => u.Id).ToList(), enemy.Pos);
        Assert.Contains("攻撃", r);
        Assert.Equal(enemy.Id, op.Roles.Last().FocusTarget);
        Run(w, 15);
        Assert.False(enemy.Alive);

        // 村人で金鉱: 採集し、割り当ても増える
        var villagers = w.TeamUnits(0).Where(u => u.Kind == UnitKind.Villager).Take(2).ToList();
        var gold = w.Nodes.Where(n => n.Kind == Res.Gold).OrderBy(n => Vector2.Distance(n.Pos, w.BasePos(0))).First();
        int goldBefore = w.Teams[0].Gather[Res.Gold];
        r = ap.ManualOrder(villagers.Select(u => u.Id).ToList(), gold.Pos);
        Assert.Contains("金を集める", r);
        Assert.Equal(goldBefore + 2, w.Teams[0].Gather[Res.Gold]);
        Assert.All(villagers, v => Assert.Equal(Res.Gold, v.JobRes));

        // 村人で村の中心: こもる
        var tc = w.TeamBuildings(0).Single(b => b.Kind == BuildingKind.TownCenter);
        r = ap.ManualOrder(villagers.Select(u => u.Id).ToList(), tc.Pos);
        Assert.Contains("こもる", r);
        Run(w, 10);
        Assert.Equal(2, w.InsideCount(tc));

        // 兵がいなくなった直接指示の役割は消え、作戦も解散する
        foreach (var u in spears) u.Hp = 0;
        Run(w, 2);
        Assert.True(op.Disbanded);
    }

    [Fact]
    public void RallyChangesWhereNewUnitsWait()
    {
        var w = MapLayout.CreateValley();
        var r = new Applier(w, 0).Apply(new ToolCall("set_economy", JsonDocument.Parse("""{"rally": "北の丘", "change_summary": ""}""").RootElement.Clone()), "", Array.Empty<int>());
        Assert.Contains("北の丘", r);
        var u = w.SpawnUnit(0, UnitKind.Spearman, w.BasePos(0) + new Vector2(3, 0));
        Run(w, 40);
        Assert.True(Vector2.Distance(u.Pos, w.Map.FindRegion("north_hill")!.Center) < 4);
        Assert.Contains("北の丘", Context.EconomyText(w, w.Teams[0]));
    }
}
