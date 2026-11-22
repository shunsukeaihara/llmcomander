using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using LlmCommander.Sim;

namespace LlmCommander.Command;

/// <summary>
/// 偵察の担当LLMに渡す情報: 地域ごとの探索済みの割合と、未探索点(次の行き先の候補)。
/// 行き先を決めるのは LLM。ゲームは候補を位置と距離付きで出すだけ。
/// </summary>
public static class Scouting
{
    /// <summary>地域ごとの探索済みの割合(0..1)。</summary>
    public static List<(Region region, float explored)> RegionExploration(World w, int team)
    {
        var vis = w.Vision[team];
        var result = new List<(Region, float)>();
        foreach (var r in w.Map.Regions)
        {
            int total = 0, seen = 0;
            for (int y = (int)(r.Center.Y - r.Radius); y <= (int)(r.Center.Y + r.Radius); y++)
            for (int x = (int)(r.Center.X - r.Radius); x <= (int)(r.Center.X + r.Radius); x++)
            {
                if (!w.Map.In(x, y) || Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), r.Center) > r.Radius) continue;
                total++;
                if (vis.IsExplored(x, y)) seen++;
            }
            result.Add((r, total == 0 ? 1 : seen / (float)total));
        }
        return result;
    }

    /// <summary>未探索点に名前(未探索点1, 2…)を付ける。担当LLMはこの名前で行き先を選ぶ。</summary>
    public static Dictionary<string, Place> Frontier(World w, int team, Vector2 from, int count = 10)
    {
        var d = new Dictionary<string, Place>();
        var points = w.FrontierPoints(team, from, count);
        for (int i = 0; i < points.Count; i++)
        {
            string name = $"未探索点{i + 1}";
            d[name] = new Place($"{name}({w.Map.RegionName(points[i])})", new[] { points[i] });
        }
        return d;
    }

    /// <summary>偵察の担当LLMに渡す状況。</summary>
    public static string Prompt(World w, Operation op, Vector2 scoutPos, IReadOnlyDictionary<string, Place> frontier, IReadOnlyList<string> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"偵察任務「{op.Name}」の担当です。経過 {Context.Clock(w.Time)}。");
        sb.AppendLine($"目的(プレイヤーの言葉): 「{op.ScoutObjective}」");
        foreach (var i in op.Intents.Skip(1).TakeLast(3)) sb.AppendLine($"追加の指示: 「{i}」");
        sb.AppendLine($"探索の中心: {w.Map.RegionName(op.ScoutCenter)}付近(自軍基地から{Vector2.Distance(op.ScoutCenter, w.BasePos(op.Team)):0})");
        var role = op.Roles.FirstOrDefault();
        if (role != null)
            sb.AppendLine($"今の行き先: {role.Anchor.Name}({(role.Arrived ? "到着済み" : "移動中")})、斥候 {w.Members(op, role).Count()}騎");
        if (events.Count > 0) sb.AppendLine("前回からの出来事:\n" + string.Join("\n", events.Select(e => "- " + e)));
        sb.Append(Text(w, op.Team, scoutPos, frontier));
        if (op.Status != "") sb.AppendLine($"\n前回のあなたの報告: {op.Status}");
        return sb.ToString();
    }

    public static string Text(World w, int team, Vector2 scoutPos, IReadOnlyDictionary<string, Place> frontier)
    {
        var sb = new StringBuilder();
        var home = w.BasePos(team);
        // 敵の本拠地は見つけるまでわからない。見つけていれば距離を出す
        var enemy = w.KnownEnemyBase(team);
        string FromEnemy(Vector2 p) => enemy is { } e ? $"、敵基地から{Vector2.Distance(p, e):0}" : "";
        sb.AppendLine($"\n## 偵察の情報(斥候の現在地: {w.Map.RegionName(scoutPos)}付近、自軍基地から{Vector2.Distance(scoutPos, home):0}{FromEnemy(scoutPos)})");
        sb.AppendLine(enemy is { } eb ? $"敵の本拠地: {w.Map.RegionName(eb)}付近で見つけている" : "敵の本拠地: まだ見つけていない");
        sb.AppendLine("地域の探索済みの割合: " + string.Join("、",
            RegionExploration(w, team).Select(r => $"{r.region.Name} {r.explored:P0}")));
        if (frontier.Count == 0) sb.AppendLine("未探索点: なし(マップをほぼ見終わった)");
        else
        {
            sb.AppendLine("未探索点(次の行き先の候補。place に名前を書く):");
            foreach (var (name, p) in frontier)
                sb.AppendLine($"- {name}: {w.Map.RegionName(p.Center)}付近。斥候から{Vector2.Distance(p.Center, scoutPos):0}、" +
                              $"自軍基地から{Vector2.Distance(p.Center, home):0}{FromEnemy(p.Center)}");
        }
        var found = Context.FoundResources(w, team);
        if (found != "") sb.Append("見つけた資源:\n" + found);
        var remembered = Context.RememberedText(w, team);
        if (remembered != "") sb.AppendLine("最後に見た敵:\n" + remembered);
        return sb.ToString();
    }
}
