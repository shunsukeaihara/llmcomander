using System.Numerics;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

public class FogTests
{
    private static void Run(World w, double seconds)
    {
        for (int i = 0; i < seconds / Defs.Dt; i++) w.Step();
    }

    [Fact]
    public void EnemiesAppearOnlyWhenSeenAndAreRememberedAfter()
    {
        var w = MapLayout.CreateValley();
        var vis = w.Vision[0];
        Assert.True(vis.IsVisible(w.BasePos(0)));
        Assert.False(vis.IsVisible(w.BasePos(1)));            // 敵の基地は見えない
        Assert.False(vis.IsExplored(64, 16));

        // 敵の兵を、自軍から見えない中央に置く
        var scout = w.SpawnUnit(1, UnitKind.Cavalry, new Vector2(46, 36));
        Run(w, 0.5);
        string ctx = Context.Commander(w, 0, new UtteranceContext(), "様子は?");
        Assert.Contains("見えている敵兵なし", ctx);

        // 自軍の兵を近くに出すと見える
        var watcher = w.SpawnUnit(0, UnitKind.Spearman, new Vector2(42, 34));
        Run(w, 0.5);
        Assert.True(vis.IsVisible(scout.Pos));
        Assert.Contains("騎兵1", Context.EnemyText(w, 0));

        // 見張りがいなくなると、最後に見た位置として残る
        watcher.Hp = 0;
        Run(w, 0.5);
        Assert.False(vis.IsVisible(scout.Pos));
        Assert.Equal("見えている敵兵なし", Context.EnemyText(w, 0));
        Assert.Contains("前に目撃", Context.RememberedText(w, 0));
        Assert.True(vis.IsExplored(42, 34));                 // 一度見た場所は探索済み
    }

    [Fact]
    public void EnemyBuildingsAreRememberedOnceSeen()
    {
        var w = MapLayout.CreateValley();
        Assert.DoesNotContain("見たことのある敵の建物", Context.RememberedText(w, 0));
        var scout = w.SpawnUnit(0, UnitKind.Cavalry, new Vector2(61, 21));
        Run(w, 0.5);
        Assert.Contains("村の中心", Context.RememberedText(w, 0));
        scout.Hp = 0;
        Run(w, 0.5);
        Assert.Contains("村の中心", Context.RememberedText(w, 0)); // 見えなくなっても覚えている
    }

    [Fact]
    public void UnitsDoNotTargetEnemiesInTheFog()
    {
        // 進撃して目的地に着いた部隊は、周囲12マスの敵を探して攻撃する。視界(7マス)の外の敵は狙わない
        static (World w, Unit enemy) Setup()
        {
            var w = MapLayout.CreateValley();
            w.Teams[0].Markers.Add(new Marker { Name = "目的地", Points = { new Vector2(30, 40) } });
            w.Teams[1].Markers.Add(new Marker { Name = "陣地", Points = { new Vector2(30, 50) } });
            w.SpawnUnit(0, UnitKind.Archer, new Vector2(28, 38));
            var enemy = w.SpawnUnit(1, UnitKind.Spearman, new Vector2(30, 50));
            new Applier(w, 0).Apply(new ToolCall("update_operation", System.Text.Json.JsonDocument.Parse("""
                {"operation": "進撃", "change_summary": "", "roles": [{"role": "弓", "unit_kind": "archer", "stance": "advance", "place": "目的地"}]}
                """).RootElement.Clone()), "", Array.Empty<int>());
            new Applier(w, 1).Apply(new ToolCall("update_operation", System.Text.Json.JsonDocument.Parse("""
                {"operation": "守り", "change_summary": "", "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "陣地"}]}
                """).RootElement.Clone()), "", Array.Empty<int>());
            return (w, enemy);
        }

        var (fog, e1) = Setup();
        Run(fog, 15);
        Assert.False(fog.Vision[0].IsVisible(e1.Pos));
        Assert.Equal(e1.MaxHp, e1.Hp);           // 霧の中の敵は撃たない

        var (clear, e2) = Setup();
        clear.Vision[0].Enabled = false;         // 霧なしなら
        Run(clear, 15);
        Assert.True(e2.Hp < e2.MaxHp);           // 見つけて攻撃する
    }
}
