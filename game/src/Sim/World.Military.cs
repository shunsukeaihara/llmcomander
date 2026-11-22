using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

public sealed partial class World
{
    /// <summary>このステップでの各ユニットの持ち場(隊列の中の位置)。</summary>
    private readonly Dictionary<int, Vector2> _slots = new();

    private void BuildSlots()
    {
        _slots.Clear();
        foreach (var ts in Teams)
        foreach (var op in ts.ActiveOps)
        {
            if (op.IsEconomy) continue;
            var members = Members(op).ToList();
            foreach (var role in op.Roles)
            {
                var list = members.Where(u => u.Role == role.Name).OrderBy(u => u.Id).ToList();
                if (list.Count == 0) continue;
                var slots = Slots(op.Team, role, list.Count);
                // 持ち場の割り振りは、近いユニットから順に取っていく(隊列の入れ替わりを減らす)
                var free = slots.ToList();
                foreach (var u in list.OrderBy(u => Vector2.DistanceSquared(u.Pos, role.Anchor.Center)))
                {
                    int bi = 0;
                    float bd = float.MaxValue;
                    for (int i = 0; i < free.Count; i++)
                    {
                        float d = Vector2.DistanceSquared(free[i], u.Pos);
                        if (d < bd) { bd = d; bi = i; }
                    }
                    _slots[u.Id] = free[bi];
                    free.RemoveAt(bi);
                }
            }
        }
    }

    /// <summary>役割の持ち場。線なら線に沿って横隊、点なら密集。引き撃ちの役割は線の後ろに下がる。</summary>
    public List<Vector2> Slots(int team, Role role, int n)
    {
        var result = new List<Vector2>(n);
        var anchor = role.Anchor;
        var home = BasePos(team);
        if (anchor.IsLine && role.Stance != Stance.Retreat)
        {
            var a = anchor.Points[0];
            var b = anchor.Points[^1];
            var along = b - a;
            var normal = Vector2.Normalize(new Vector2(-along.Y, along.X));
            if (Vector2.Dot(normal, home - anchor.Center) < 0) normal = -normal; // 自軍側を向く
            float back = role.Kite || role.Kind == UnitKind.Archer ? 3f : role.Kind == UnitKind.Cavalry ? 1.5f : 0f;
            int perRow = Math.Max(1, (int)(along.Length() / 0.75f));
            for (int i = 0; i < n; i++)
            {
                int row = i / perRow;
                int col = i % perRow;
                int inRow = Math.Min(perRow, n - row * perRow);
                float t = inRow == 1 ? 0.5f : col / (float)(inRow - 1);
                result.Add(Map.NearestPassable(a + along * t + normal * (back + row * 0.8f)));
            }
            return result;
        }
        // 密集隊形: 中心から渦巻き状に並べる
        var c = anchor.Center;
        int side = (int)MathF.Ceiling(MathF.Sqrt(n));
        for (int i = 0; i < n; i++)
        {
            float x = (i % side - (side - 1) / 2f) * 0.8f;
            float y = (i / side - (side - 1) / 2f) * 0.8f;
            result.Add(Map.NearestPassable(c + new Vector2(x, y)));
        }
        return result;
    }

    private void StepMilitary(Unit u)
    {
        var role = RoleOf(u);
        var slot = _slots.TryGetValue(u.Id, out var s) ? s : Teams[u.Team].Rally;
        var stance = role?.Stance ?? Stance.Hold;
        var anchor = role?.Anchor.Center ?? slot;

        // 戦わない役割(巡回は常に): 敵兵が近いか攻撃されたら逃げる
        bool evade = role is { Evade: true } || stance == Stance.Patrol;
        if (evade && Flee(u)) return;
        // 指名された敵兵がいれば、見えている限り追いかけて攻撃する
        if (role is { FocusTarget: > 0 } && !evade)
        {
            if (Units.TryGetValue(role.FocusTarget, out var focus) && focus.Alive && !focus.IsInside && Vision[u.Team].IsVisible(focus.Pos))
            {
                Engage(u, role, focus);
                return;
            }
            role.FocusTarget = 0; // 倒したか見失った。あとは目的地の周りの敵を探す
        }
        if (stance == Stance.Patrol)
        {
            Patrol(u, role!);
            return;
        }
        if (evade)
        {
            // 戦わずに持ち場(進撃なら目的地)へ
            MoveTo(u, slot, 0.3f);
            return;
        }

        if (stance == Stance.Retreat)
        {
            if (Vector2.Distance(u.Pos, anchor) > 4f)
            {
                MoveTo(u, slot, 0.3f);
                return;
            }
            stance = Stance.Hold;
        }

        if (stance == Stance.Hold)
        {
            const float leash = 9f;
            Entity? t = null;
            if (Vector2.Distance(u.Pos, slot) < leash)
                t = PickTarget(u, role, u.Pos, 5.5f, false) ?? PickTarget(u, role, anchor, 6f, false);
            if (t == null && Time - u.LastHitAt < 1.5 && Units.TryGetValue(u.LastAttacker, out var atk) && atk.Alive &&
                Vector2.Distance(atk.Pos, slot) < leash)
                t = atk;
            if (t != null) Engage(u, role, t);
            else MoveTo(u, slot, 0.2f);
            return;
        }

        // 進撃: 途中の敵と戦いながら目的地へ。着いたら周囲の敵と建物を探して攻撃する
        var target = PickTarget(u, role, u.Pos, Defs.Sight, false);
        if (target == null && Vector2.Distance(u.Pos, anchor) < 6f)
            target = PickTarget(u, role, anchor, 12f, true);
        if (target != null) Engage(u, role, target);
        else MoveTo(u, slot, 0.2f);
    }

    // ------------------------------------------------------------ 偵察(逃げる・巡回)

    /// <summary>
    /// 場所を中心に、内側の輪から順に未探索の場所を回る。戦わず、敵兵が近づいたら距離を取る。
    /// </summary>
    /// <summary>見えている敵兵が近いか、少し前に攻撃されていたら、離れる方向へ逃げる。逃げたら true。</summary>
    private bool Flee(Unit u)
    {
        var threat = Units.Values
            .Where(o => o.Team != u.Team && o.Alive && !o.IsInside && o.Kind != UnitKind.Villager && Vision[u.Team].IsVisible(o.Pos))
            .OrderBy(o => Vector2.DistanceSquared(o.Pos, u.Pos)).FirstOrDefault();
        Vector2? from = null;
        if (threat != null && Vector2.Distance(threat.Pos, u.Pos) < Math.Max(5f, threat.Stats.Range + 2.5f)) from = threat.Pos;
        else if (Time - u.LastHitAt < 1.5)
            from = Units.TryGetValue(u.LastAttacker, out var a) ? a.Pos : Buildings.TryGetValue(u.LastAttacker, out var b) ? b.Pos : null;
        if (from is not { } f) return false;
        var away = Vector2.Normalize(u.Pos - f + new Vector2(0.001f, 0));
        var dest = Map.NearestPassable(u.Pos + away * 4);
        if (Vector2.Distance(dest, u.Pos) < 0.5f) dest = Map.NearestPassable(u.Pos + new Vector2(-away.Y, away.X) * 4);
        MoveTo(u, dest, 0.3f);
        u.ExploreGoal = null;
        return true;
    }

    /// <summary>場所の周りを半径 Radius(既定10)で回り続ける。</summary>
    private void Patrol(Unit u, Role role)
    {
        var c = role.Anchor.Center;
        float r = role.Radius ?? 10f;
        if (u.ExploreGoal is { } g && Vector2.Distance(u.Pos, g) > 1.5f && Time - u.ExploreAt < 15)
        {
            MoveTo(u, g, 1f);
            return;
        }
        // 今いる角度から少し先の、輪の上の地点へ(右回り)
        var rel = u.Pos - c;
        float ang = rel.LengthSquared() < 0.01f ? 0 : MathF.Atan2(rel.Y, rel.X);
        float next = Vector2.Distance(u.Pos, c) < r * 0.6f || Vector2.Distance(u.Pos, c) > r * 1.4f ? ang : ang + 0.6f;
        var p = c + new Vector2(MathF.Cos(next), MathF.Sin(next)) * r;
        p = new Vector2(Math.Clamp(p.X, 1, Map.W - 1), Math.Clamp(p.Y, 1, Map.H - 1));
        u.ExploreGoal = Map.NearestPassable(p, 6);
        u.ExploreAt = (float)Time;
        MoveTo(u, u.ExploreGoal.Value, 1f);
    }

    // ------------------------------------------------------------ 条件付きの動き

    /// <summary>
    /// 役割の全員が行き先に着いたら、作戦の出来事として記録し、担当LLMを早めに呼ぶ(偵察で次の行き先を決めるため)。
    /// </summary>
    private void CheckArrivals(TeamState ts)
    {
        foreach (var op in ts.ActiveOps)
        {
            if (op.IsEconomy || op.IsReserve) continue;
            foreach (var role in op.Roles)
            {
                if (role.Arrived || role.Stance is not (Stance.Hold or Stance.Advance)) continue;
                var members = Members(op, role).ToList();
                if (members.Count == 0) continue;
                if (members.All(u => role.Anchor.Distance(u.Pos) < 3f))
                {
                    role.Arrived = true;
                    role.ArrivedAt = Time;
                    op.Log(Time, $"到着: {role.Name}が{role.Anchor.Name}に着いた");
                    op.Eventful = true;
                }
            }
        }
    }

    private void CheckTriggers(TeamState ts)
    {
        foreach (var op in ts.ActiveOps)
        foreach (var role in op.Roles)
        {
            var tr = role.Trigger;
            if (tr == null || !TriggerMet(op, tr)) continue;
            role.Trigger = null;
            role.Apply(tr.Then, Time);
            op.Log(Time, $"条件発動: {tr.Text}");
            op.Eventful = true;
            ts.Version++;
        }
    }

    public bool TriggerMet(Operation op, Trigger tr)
    {
        switch (tr.Kind)
        {
            case ConditionKind.EnemyNear:
                if (tr.Place == null) return false;
                // 見えている敵だけで判断する(霧の中の敵には反応しない)
                return Units.Values.Any(o => o.Team != op.Team && o.Alive && o.Kind != UnitKind.Villager &&
                                             Vision[op.Team].IsVisible(o.Pos) &&
                                             tr.Place.Distance(o.Pos) <= tr.Radius);
            case ConditionKind.RoleEngaged:
            {
                var role = op.FindRole(tr.Role);
                return role != null && Members(op, role).Any(u => Time - u.LastFoughtAt < 1.5);
            }
            case ConditionKind.RoleHpBelow:
            {
                var role = op.FindRole(tr.Role);
                if (role == null || role.InitialCount == 0) return false;
                return Members(op, role).Count() < role.InitialCount * tr.Fraction;
            }
        }
        return false;
    }

    /// <summary>作戦の状態を一言で(カードの右上に出す)。</summary>
    public string OpTag(Operation op)
    {
        if (op.IsEconomy) return "";
        var members = Members(op).ToList();
        if (members.Count == 0) return "兵なし";
        if (members.Any(u => Time - u.LastFoughtAt < 2.5)) return "交戦中";
        if (op.Roles.Any(r => r.Stance == Stance.Retreat && Members(op, r).Any())) return "後退中";
        int moving = members.Count(u => u.Anim == Anim.Walk);
        if (moving * 2 > members.Count) return "移動中";
        if (op.Roles.Any(r => r.Trigger != null)) return "条件待ち";
        return "待機中";
    }

    /// <summary>役割の隊列がどれくらい保たれているか(持ち場からの平均のずれ)。</summary>
    public string FormationState(Operation op, Role role)
    {
        var list = Members(op, role).ToList();
        if (list.Count == 0) return "";
        float avg = list.Average(u => _slots.TryGetValue(u.Id, out var s) ? Vector2.Distance(u.Pos, s) : 0f);
        if (list.Any(u => u.Anim == Anim.Walk) && avg > 3f) return "移動中";
        return avg < 1.2f ? "維持" : avg < 3f ? "乱れ" : "崩れ";
    }
}
