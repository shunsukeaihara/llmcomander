using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>
/// 偵察任務。斥候は「戦わない」で行き先へ向かい、着いたら担当LLMが次の行き先を決める。
/// LLMが一定時間決めなければ、任務の中心に近い未探索点へ自動で向かう(同心円状に広がる)。
/// </summary>
public sealed partial class World
{
    /// <summary>着いてからこの秒数のあいだに次の行き先が決まらなければ、自動で決める。</summary>
    public const double ScoutAutoDelay = 1.5;

    /// <summary>
    /// 未探索点: まだ見ていない、通れる場所の候補。互いに離れた地点を最大 count 個。
    /// 並びは order(既定は from から近い順)。
    /// </summary>
    public List<Vector2> FrontierPoints(int team, Vector2 from, int count = 10, Func<Vector2, float>? order = null)
    {
        var vis = Vision[team];
        var cands = new List<Vector2>();
        for (int y = 3; y < Map.H - 3; y += 5)
        for (int x = 3; x < Map.W - 3; x += 5)
        {
            if (vis.IsExplored(x, y)) continue;
            var p = Map.NearestPassable(new Vector2(x + 0.5f, y + 0.5f), 3);
            if (!Map.Passable(p)) continue;
            cands.Add(p);
        }
        order ??= c => Vector2.Distance(c, from);
        var chosen = new List<Vector2>();
        foreach (var c in cands.OrderBy(order))
        {
            if (chosen.Any(o => Vector2.Distance(o, c) < 9)) continue;
            chosen.Add(c);
            if (chosen.Count >= count) break;
        }
        return chosen;
    }

    /// <summary>偵察任務を作る。units を任務に移し、最初の行き先を自動で決める。</summary>
    public Operation StartScouting(int team, string name, string objective, Vector2 center, IEnumerable<Unit> units)
    {
        var ts = Teams[team];
        var op = new Operation
        {
            Id = NextId(), Team = team, Name = name, IsScouting = true, ScoutObjective = objective,
            ScoutCenter = center, CreatedAt = Time,
        };
        var role = new Role("偵察", new Place("開始地点", new[] { center })) { Stance = Stance.Hold, Evade = true };
        op.Roles.Add(role);
        foreach (var u in units)
        {
            u.OpId = op.Id;
            u.Role = role.Name;
        }
        role.InitialCount = Members(op, role).Count();
        ts.Ops.Add(op);
        AutoWaypoint(op, role);
        op.Eventful = true;
        ts.Version++;
        return op;
    }

    /// <summary>任務の中心に近い未探索点から、斥候に近いものを次の行き先にする。</summary>
    public bool AutoWaypoint(Operation op, Role role)
    {
        var members = Members(op, role).ToList();
        if (members.Count == 0) return false;
        var pos = new Vector2(members.Average(u => u.Pos.X), members.Average(u => u.Pos.Y));
        // 同心円状に広げる: 中心からの距離を4マス刻みの輪にまとめ、内側の輪を先に。同じ輪では斥候に近い順
        var next = FrontierPoints(op.Team, pos, 1,
            c => MathF.Floor(Vector2.Distance(c, op.ScoutCenter) / 4) * 1000 + Vector2.Distance(c, pos)).FirstOrDefault();
        if (next == default) return false;
        role.Apply(new RulePatch { Anchor = new Place($"自動の行き先({Map.RegionName(next)})", new[] { next }) }, Time);
        role.Superseded = "";
        return true;
    }

    /// <summary>着いたまま次の行き先が決まらない偵察任務に、自動で行き先を出す。</summary>
    private void CheckScouting(TeamState ts)
    {
        foreach (var op in ts.ActiveOps.Where(o => o.IsScouting))
        foreach (var role in op.Roles)
        {
            if (!role.Arrived || Time - role.ArrivedAt < ScoutAutoDelay) continue;
            if (AutoWaypoint(op, role)) op.Log(Time, $"自動: 次の行き先 {role.Anchor.Name}");
        }
    }
}
