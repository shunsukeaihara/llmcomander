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
