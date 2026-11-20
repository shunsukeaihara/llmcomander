using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using LlmCommander.Sim;

namespace LlmCommander.Command;

/// <summary>発話した瞬間のGUIの状態(選択と、話しながらクリックした地点)。</summary>
public sealed class UtteranceContext
{
    public List<int> Selected = new();
    public List<Vector2> Clicks = new();
}

/// <summary>LLMに渡す状況の要約を作る。座標は出さず、地名と数で伝える。</summary>
public static class Context
{
    public static string Clock(double t) => $"{(int)t / 60}:{(int)t % 60:00}";

    private static string Counts(IEnumerable<Unit> units) =>
        string.Join(" ", units.GroupBy(u => u.Kind).OrderBy(g => g.Key).Select(g => $"{Defs.Ja(g.Key)}{g.Count()}"));

    public static string PlaceText(World w, Place p) =>
        p.IsLine
            ? $"線: {w.Map.RegionName(p.Points[0])}付近〜{w.Map.RegionName(p.Points[^1])}付近"
            : $"点: {w.Map.RegionName(p.Center)}付近";

    public static string RoleText(World w, Operation op, Role r)
    {
        var members = w.Members(op, r).ToList();
        var sb = new StringBuilder($"{r.Name}({(r.Kind is { } k ? Defs.Key(k) : "混成")}) {members.Count}体: {r.Describe()}");
        if (r.Trigger is { } tr) sb.Append($" [条件待ち: {tr.Text}]");
        return sb.ToString();
    }

    public static string EconomyText(World w, TeamState ts)
    {
        var sb = new StringBuilder();
        var villagers = w.TeamUnits(ts.Team).Where(u => u.Kind == UnitKind.Villager).ToList();
        var counts = w.GatherCounts(ts.Team);
        int building = villagers.Count(v => v.Job == JobKind.Build);
        int idle = villagers.Count(v => v.Job == JobKind.None);
        sb.AppendLine($"村人 {villagers.Count}人: " +
                      string.Join(", ", Enum.GetValues<Res>().Select(r => $"{Defs.Ja(r)}({Defs.Key(r)}) {counts[r]}/{ts.Gather[r]}")) +
                      $", 建設中 {building}, 待機 {idle}");
        sb.AppendLine($"村人の生産: {(ts.VillagersContinuous ? $"継続(目標{ts.VillagerTarget}人)" : "停止")}");
        sb.AppendLine("兵の生産目標: " + string.Join(", ", ts.TrainGoal.Select(kv =>
            $"{Defs.Ja(kv.Key)} {(kv.Value < 0 ? "上限まで継続" : kv.Value == 0 ? "なし" : kv.Value + "体")}")));
        var reinforce = ts.Ops.FirstOrDefault(o => o.Id == ts.ReinforceOp);
        sb.AppendLine($"新兵の配属先: {reinforce?.Name ?? "予備"}{(ts.ReinforceRole != "" ? "/" + ts.ReinforceRole : "")}");
        var blds = w.TeamBuildings(ts.Team).GroupBy(b => (b.Kind, b.Done))
            .Select(g => $"{Defs.Ja(g.Key.Kind)}{(g.Key.Done ? "" : "(建設中)")}×{g.Count()}");
        sb.AppendLine("建物: " + string.Join(", ", blds));
        foreach (var p in ts.Plans.Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Confirmed))
            sb.AppendLine($"建設計画 #{p.Id}: {Defs.Ja(p.Kind)}({w.Map.RegionName(p.Center)}) " +
                          (p.Status == PlanStatus.Proposed ? "確認待ち" : "確定・資源待ち"));
        return sb.ToString();
    }

    /// <summary>今見えている敵の兵を地域ごとにまとめる(霧の中の敵は含めない)。</summary>
    public static string EnemyText(World w, int team, Func<Unit, bool>? filter = null)
    {
        var vis = w.Vision[team];
        var enemies = w.Units.Values.Where(u => u.Alive && u.Team != team && u.Kind != UnitKind.Villager && vis.IsVisible(u.Pos));
        if (filter != null) enemies = enemies.Where(filter);
        var groups = enemies.GroupBy(u => w.Map.RegionName(u.Pos)).OrderByDescending(g => g.Count()).ToList();
        if (groups.Count == 0) return "見えている敵兵なし";
        return string.Join("\n", groups.Select(g => $"- {g.Key}付近: {Counts(g)}"));
    }

    /// <summary>今は見えていないが、最後に見た敵兵と、見たことのある敵の建物。</summary>
    public static string RememberedText(World w, int team)
    {
        var vis = w.Vision[team];
        var lines = new List<string>();
        var groups = vis.Remembered(w).Where(s => s.Kind != UnitKind.Villager)
            .GroupBy(s => w.Map.RegionName(s.Pos)).OrderByDescending(g => g.Max(s => s.Time)).Take(6);
        foreach (var g in groups)
        {
            string counts = string.Join(" ", g.GroupBy(s => s.Kind).OrderBy(k => k.Key).Select(k => $"{Defs.Ja(k.Key)}{k.Count()}"));
            lines.Add($"- {g.Key}付近: {counts}({Clock(w.Time - g.Max(s => s.Time))}前に目撃)");
        }
        var blds = vis.Buildings.Values.GroupBy(b => (b.Kind, w.Map.RegionName(new Vector2(b.TileX + 1.5f, b.TileY + 1.5f))))
            .Select(g => $"{Defs.Ja(g.Key.Kind)}×{g.Count()}({g.Key.Item2})");
        var bl = string.Join("、", blds);
        if (bl != "") lines.Add("見たことのある敵の建物: " + bl);
        return lines.Count == 0 ? "" : string.Join("\n", lines);
    }

    public static string Commander(World w, int team, UtteranceContext ctx, string utterance)
    {
        var ts = w.Teams[team];
        var sb = new StringBuilder();
        sb.AppendLine($"# 現在の状況(経過 {Clock(w.Time)})");
        sb.AppendLine($"資源: 食 {(int)ts.Stock[Res.Food]} / 木 {(int)ts.Stock[Res.Wood]} / 金 {(int)ts.Stock[Res.Gold]} ・ 人口 {w.Pop(team)}/{Defs.PopCap}");
        sb.Append(EconomyText(w, ts));

        sb.AppendLine("\n## 作戦");
        foreach (var op in ts.ActiveOps.Where(o => !o.IsEconomy))
        {
            var members = w.Members(op).ToList();
            if (op.IsReserve && members.Count == 0) { sb.AppendLine("- 予備: なし"); continue; }
            sb.AppendLine($"- {op.Name}({w.OpTag(op)}){(op.Status != "" ? " 状況: " + op.Status : "")}");
            foreach (var r in op.Roles.Where(r => !op.IsReserve || w.Members(op, r).Any()))
                sb.AppendLine("  - " + RoleText(w, op, r));
        }

        sb.AppendLine("\n## 場所");
        sb.AppendLine("地名: " + string.Join("、", w.Map.Regions.Select(r => r.Name)) + "、自軍基地(own_base)、敵基地(enemy_base)、集合地点(rally)");
        if (ts.Markers.Count > 0)
        {
            sb.AppendLine("プレイヤーの目印:");
            foreach (var m in ts.Markers) sb.AppendLine($"- {m.Name}({PlaceText(w, m.ToPlace())})");
        }

        sb.AppendLine("\n## 敵(霧があるので、見えているものだけ)");
        sb.AppendLine(EnemyText(w, team));
        var remembered = RememberedText(w, team);
        if (remembered != "") sb.AppendLine("今は見えていない敵(最後に見た位置):\n" + remembered);
        var alerts = ts.Alerts.Where(a => w.Time - a.Time < 30).Select(a => a.Text).Distinct().ToList();
        if (alerts.Count > 0) sb.AppendLine("警報: " + string.Join(" / ", alerts));

        if (ctx.Selected.Count > 0)
        {
            var sel = ctx.Selected.Select(id => w.Units.GetValueOrDefault(id)).Where(u => u is { Alive: true }).Cast<Unit>().ToList();
            var byOp = sel.GroupBy(u => ts.Ops.FirstOrDefault(o => o.Id == u.OpId)?.Name ?? "?")
                .Select(g => $"{Counts(g)}(所属: {g.Key})");
            sb.AppendLine($"\n## 選択中のユニット(selected)\n{string.Join("、", byOp)}");
        }
        if (ctx.Clicks.Count > 0)
            sb.AppendLine("\n## 発話中にクリックした地点\n" +
                          string.Join("\n", ctx.Clicks.Select((c, i) => $"- 指示点{i + 1}({w.Map.RegionName(c)}付近)")));

        var recent = ts.History.Where(h => !h.Pending).TakeLast(4).ToList();
        if (recent.Count > 0)
        {
            sb.AppendLine("\n## 直前の発話");
            foreach (var h in recent) sb.AppendLine($"- 「{h.Utterance}」→ {h.Result}");
        }

        sb.AppendLine($"\n# プレイヤーの発話\n「{utterance}」");
        return sb.ToString();
    }

    public static string Officer(World w, Operation op, IReadOnlyList<string> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"作戦「{op.Name}」の担当です。経過 {Clock(w.Time)}。");
        if (op.Intents.Count > 0)
            sb.AppendLine("プレイヤーの指示(古い順):\n" + string.Join("\n", op.Intents.TakeLast(5).Select(i => $"- 「{i}」")));
        sb.AppendLine("\n## 役割");
        foreach (var r in op.Roles)
        {
            var m = w.Members(op, r).ToList();
            if (m.Count == 0 && r.InitialCount == 0) continue;
            int fighting = m.Count(u => w.Time - u.LastFoughtAt < 2);
            float hp = m.Count > 0 ? m.Average(u => u.Hp / u.MaxHp) : 0;
            sb.AppendLine($"- {RoleText(w, op, r)}(当初{Math.Max(r.InitialCount, m.Count)}体、交戦中{fighting}体、平均体力{hp:P0}、隊列: {w.FormationState(op, r)}、位置: {(m.Count > 0 ? w.Map.RegionName(new Vector2(m.Average(u => u.Pos.X), m.Average(u => u.Pos.Y))) : "-")}付近)");
        }
        var center = w.Members(op).Select(u => u.Pos).DefaultIfEmpty(op.Roles.FirstOrDefault()?.Anchor.Center ?? Vector2.Zero);
        var c = new Vector2(center.Average(p => p.X), center.Average(p => p.Y));
        sb.AppendLine("\n## 近く(15マス以内)の敵");
        sb.AppendLine(EnemyText(w, op.Team, u => Vector2.Distance(u.Pos, c) < 15));
        if (events.Count > 0) sb.AppendLine("\n## 前回からの出来事\n" + string.Join("\n", events.Select(e => "- " + e)));
        if (op.Status != "") sb.AppendLine($"\n前回のあなたの報告: {op.Status}");
        return sb.ToString();
    }

    /// <summary>音声認識に渡す語彙。</summary>
    public static string SpeechHint(World w, int team)
    {
        var ts = w.Teams[team];
        var words = new List<string> { "槍", "弓", "騎兵", "村人", "引き撃ち", "撤退", "横隊", "兵舎", "村の中心", "金鉱" };
        words.AddRange(w.Map.Regions.Select(r => r.Name));
        words.AddRange(ts.Markers.Select(m => m.Name));
        words.AddRange(ts.ActiveOps.Select(o => o.Name));
        return string.Join("、", words.Distinct());
    }
}
