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

    // ------------------------------------------------------------ 条件付きの動き

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
                return Units.Values.Any(o => o.Team != op.Team && o.Alive && o.Kind != UnitKind.Villager &&
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
