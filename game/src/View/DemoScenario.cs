using System.Linq;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using SVec = System.Numerics.Vector2;

namespace LlmCommander.View;

/// <summary>
/// 画面確認用の場面(環境変数 LLMC_DEMO=1)。LLM を使わず、LLM が返すはずのツール呼び出しを直接適用して、
/// 「戦線Aで交戦中に『弓は引き撃ち、他は撤退』と言った直後」を作る。
/// </summary>
public static class DemoScenario
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static void Say(World w, Applier ap, string utterance, double llm, params ToolCall[] calls)
    {
        var results = calls.Select(c => ap.Apply(c, utterance, System.Array.Empty<int>())).ToList();
        w.Teams[0].History.Add(new HistoryEntry
        {
            Time = w.Time, Utterance = utterance, Result = string.Join(" / ", results),
            SttSeconds = 0.8, LlmSeconds = llm, Pending = false,
        });
    }

    public static void Setup(World w)
    {
        var ts = w.Teams[0];
        ts.Stock[Res.Wood] = 900;
        ts.Stock[Res.Gold] = 400;
        w.SpawnBuilding(0, BuildingKind.Barracks, 21, 58);
        // 木の実のそばの採集所
        if (w.FindBuildSite(BuildingKind.Storehouse, new SVec(12.5f, 61)) is { } st) w.SpawnBuilding(0, BuildingKind.Storehouse, st.X, st.Y);
        for (int i = 0; i < 16; i++) w.SpawnUnit(0, UnitKind.Villager, new SVec(19, 62));
        for (int i = 0; i < 9; i++) w.SpawnUnit(0, UnitKind.Spearman, new SVec(26 + i % 3, 50 + i / 3));
        for (int i = 0; i < 10; i++) w.SpawnUnit(0, UnitKind.Archer, new SVec(24 + i % 4, 54 + i / 4));
        for (int i = 0; i < 7; i++) w.SpawnUnit(0, UnitKind.Cavalry, new SVec(30 + i % 3, 54 + i / 3));
        ts.Markers.Add(new Marker { Name = "防衛線A", Points = { new SVec(27, 36), new SVec(35, 44) } });
        ts.Markers.Add(new Marker { Name = "集合地点B", Points = { new SVec(24, 56) } });

        var ap = new Applier(w, 0);
        Say(w, ap, "木を5人残して、残りは金に。村人は作り続けて", 2.1,
            new ToolCall("set_economy", J("""{"gather": {"food": 6, "wood": 5, "gold": 6}, "villagers_continuous": true, "change_summary": "木5人を残して金へ"}""")));
        Say(w, ap, "槍は防衛線Aで受け止めて、弓は後ろから軍事ユニット優先。騎兵は右で待って、接触したら回り込め", 2.9,
            new ToolCall("update_operation", J("""
            {"operation": "戦線A", "change_summary": "防衛線Aに布陣",
             "roles": [
               {"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "防衛線A", "formation": "line"},
               {"role": "弓", "unit_kind": "archer", "stance": "hold", "place": "防衛線A", "formation": "line", "priority": ["spearman", "archer", "cavalry"]},
               {"role": "騎兵", "unit_kind": "cavalry", "stance": "hold", "place": "北西の林",
                "trigger": {"condition": "role_engaged", "role": "槍", "then": {"stance": "advance", "place": "北の丘"}, "description": "槍が接触したら右から回り込む"}}
             ]}
            """)));
        Say(w, ap, "東の金鉱の近く…いや、北の金鉱の近くに村の中心", 2.4,
            new ToolCall("propose_build", J("""{"building": "town_center", "near": "北の金鉱"}""")));

        // 敵の攻撃隊
        for (int i = 0; i < 12; i++) w.SpawnUnit(1, UnitKind.Spearman, new SVec(34 + i % 4, 22 + i / 4));
        for (int i = 0; i < 6; i++) w.SpawnUnit(1, UnitKind.Archer, new SVec(38 + i % 3, 20 + i / 3));
        new Applier(w, 1).Apply(new ToolCall("update_operation", J("""
            {"operation": "攻撃隊", "change_summary": "",
             "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "advance", "place": "西の平地"},
                       {"role": "弓", "unit_kind": "archer", "stance": "advance", "place": "西の平地", "kite": true}]}
            """)), "", System.Array.Empty<int>());
    }

    /// <summary>交戦が始まってから呼ぶ。</summary>
    public static void Retreat(World w)
    {
        var ap = new Applier(w, 0);
        Say(w, ap, "弓は引き撃ち、他は撤退", 1.7,
            new ToolCall("update_operation", J("""
            {"operation": "戦線A", "change_summary": "弓は引き撃ち、槍・騎兵は集合地点Bへ撤退",
             "roles": [{"role": "弓", "kite": true},
                       {"role": "槍", "stance": "retreat", "place": "集合地点B"},
                       {"role": "騎兵", "stance": "retreat", "place": "集合地点B", "clear_trigger": true}]}
            """)));
        var op = w.Teams[0].FindOp("戦線A")!;
        op.Status = "北の丘で敵の槍12・弓6と交戦。槍が押され気味で後退開始、弓が援護中";
        op.StatusAt = w.Time - 2;
    }
}
