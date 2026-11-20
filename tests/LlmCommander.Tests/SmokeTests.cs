using LlmCommander.Sim;
using Xunit;

public class SmokeTests
{
    [Fact]
    public void ValleyRunsFiveMinutesWithEnemyAi()
    {
        var w = MapLayout.CreateValley();
        var ai = new EnemyAI(w);
        for (int i = 0; i < 20 * 300; i++)
        {
            w.Step();
            ai.Step();
        }
        Assert.Null(w.Winner);
        // 敵AIは村人を増やし、兵舎を建てている
        Assert.True(w.TeamUnits(1).Count(u => u.Kind == UnitKind.Villager) > 12);
        Assert.Contains(w.TeamBuildings(1), b => b.Kind == BuildingKind.Barracks);
    }
}

public class MatchTests
{
    [Fact]
    public void EnemyWavesEventuallyAttackAnIdlePlayer()
    {
        // プレイヤーが何もしなければ、敵の攻撃の波で負ける(30分以内)
        var w = MapLayout.CreateValley();
        var ai = new EnemyAI(w);
        for (int i = 0; i < 20 * 60 * 30 && w.Winner == null; i++)
        {
            w.Step();
            ai.Step();
        }
        Assert.Equal(1, w.Winner);
    }
}

public class InterpolationTests
{
    [Fact]
    public void RenderPositionMovesBetweenSteps()
    {
        var w = MapLayout.CreateValley();
        var u = w.SpawnUnit(0, UnitKind.Cavalry, new System.Numerics.Vector2(30, 50));
        Assert.Equal(u.Pos, u.PrevPos); // 出現した直後は補間しない
        var ap = new LlmCommander.Command.Applier(w, 0);
        ap.Apply(new LlmCommander.Command.ToolCall("update_operation", System.Text.Json.JsonDocument.Parse("""
            {"operation": "移動", "change_summary": "", "roles": [{"role": "騎兵", "unit_kind": "cavalry", "stance": "advance", "place": "中央"}]}
            """).RootElement.Clone()), "", Array.Empty<int>());
        for (int i = 0; i < 5; i++) w.Step();
        Assert.NotEqual(u.PrevPos, u.Pos);
        Assert.Equal(u.PrevPos, u.RenderPos(0));
        Assert.Equal(u.Pos, u.RenderPos(1));
        // 1ステップで動く距離は速さ×Dt 程度
        Assert.True(System.Numerics.Vector2.Distance(u.PrevPos, u.Pos) < u.Stats.Speed * Defs.Dt * 3);
    }
}
