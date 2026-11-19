using System;
using System.Numerics;
using System.Text.Json;
using LlmCommander.Sim;

namespace LlmCommander.Command;

/// <summary>計測と準備のための場面。</summary>
public static class Scenarios
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    /// <summary>両軍とも人口上限いっぱい(村人24 + 兵70)で、自軍は北と南の2つの戦線、敵は全軍で進撃する。</summary>
    public static World BigBattle()
    {
        var w = MapLayout.CreateValley();
        var rng = new Random(1);
        for (int team = 0; team < 2; team++)
        {
            var basePos = w.BasePos(team);
            for (int i = 0; i < 24; i++) w.SpawnUnit(team, UnitKind.Villager, basePos + new Vector2(rng.Next(-4, 4), rng.Next(-4, 4)));
            foreach (var (kind, n) in new[] { (UnitKind.Spearman, 25), (UnitKind.Archer, 25), (UnitKind.Cavalry, 20) })
                for (int i = 0; i < n; i++)
                    w.SpawnUnit(team, kind, w.Teams[team].Rally + new Vector2(rng.Next(-4, 4), rng.Next(-4, 4)));
            w.Teams[team].Gather[Res.Food] = 10;
            w.Teams[team].Gather[Res.Wood] = 10;
            w.Teams[team].Gather[Res.Gold] = 10;
        }
        new Applier(w, 0).Apply(new ToolCall("update_operation", J("""
            {"operation": "北", "change_summary": "", "roles": [
              {"role": "槍", "unit_kind": "spearman", "assign_count": 13, "stance": "advance", "place": "北の丘"},
              {"role": "弓", "unit_kind": "archer", "assign_count": 13, "stance": "advance", "place": "北の丘", "kite": true},
              {"role": "騎兵", "unit_kind": "cavalry", "assign_count": 10, "stance": "advance", "place": "北の丘", "priority": ["archer"]}]}
            """)), "", Array.Empty<int>());
        new Applier(w, 0).Apply(new ToolCall("update_operation", J("""
            {"operation": "南", "change_summary": "", "roles": [
              {"role": "槍", "unit_kind": "spearman", "stance": "advance", "place": "南の丘"},
              {"role": "弓", "unit_kind": "archer", "stance": "advance", "place": "南の丘", "kite": true},
              {"role": "騎兵", "unit_kind": "cavalry", "stance": "advance", "place": "南の丘",
               "trigger": {"condition": "role_engaged", "role": "槍", "then": {"stance": "advance", "place": "中央"}, "description": "槍が接触したら突撃"}}]}
            """)), "", Array.Empty<int>());
        new Applier(w, 1).Apply(new ToolCall("update_operation", J("""
            {"operation": "全軍", "change_summary": "", "roles": [
              {"role": "槍", "unit_kind": "spearman", "stance": "advance", "place": "西の基地"},
              {"role": "弓", "unit_kind": "archer", "stance": "advance", "place": "西の基地", "kite": true},
              {"role": "騎兵", "unit_kind": "cavalry", "stance": "advance", "place": "西の基地"}]}
            """)), "", Array.Empty<int>());
        return w;
    }

    /// <summary>
    /// 読み込み時に、裏で大規模な戦闘を空回しして、戦闘や経路探索のコードを先にコンパイルさせる。
    /// これをしないと、最初の交戦の瞬間に数十ミリ秒の引っかかりが出る。
    /// </summary>
    public static void Warmup(double seconds = 20)
    {
        var w = BigBattle();
        var ai = new EnemyAI(w);
        for (int i = 0; i < seconds / Defs.Dt; i++)
        {
            w.Step();
            ai.Step();
        }
        Context.Commander(w, 0, new UtteranceContext(), "");
    }
}
