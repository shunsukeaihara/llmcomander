using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>
/// 村人が村の中心にこもる仕組み。中にいる村人は攻撃されず、村の中心は中の人数と同じ本数の矢を撃つ。
/// </summary>
public sealed partial class World
{
    public const int GarrisonCap = 15;
    public const float TowerRange = 7f;
    public const float TowerCooldown = 2f;
    public const int ArrowDamage = 4;

    public int InsideCount(Building b) => Units.Values.Count(u => u.Alive && u.Inside == b.Id);

    private Building? GarrisonSpot(Unit v) =>
        TeamBuildings(v.Team).Where(b => b.Kind == BuildingKind.TownCenter && b.Done)
            .Where(b => InsideCount(b) + Units.Values.Count(o => o.Alive && o.GarrisonTarget == b.Id) < GarrisonCap)
            .OrderBy(b => Vector2.DistanceSquared(b.Pos, v.Pos)).FirstOrDefault();

    /// <summary>
    /// 村人を村の中心にこもらせる。ids を渡せばその村人、渡さなければ村の中心に近い村人から count 人(-1 で全員)。
    /// 向かわせた人数を返す。
    /// </summary>
    public int Garrison(int team, int count, IReadOnlyCollection<int>? ids = null)
    {
        var villagers = TeamUnits(team).Where(u => u.Kind == UnitKind.Villager && !u.IsInside && u.GarrisonTarget == 0);
        if (ids != null) villagers = villagers.Where(u => ids.Contains(u.Id));
        var home = BasePos(team);
        var list = villagers.OrderBy(u => Vector2.DistanceSquared(u.Pos, home)).ToList();
        if (count >= 0) list = list.Take(count).ToList();
        int sent = 0;
        foreach (var v in list)
        {
            var b = GarrisonSpot(v);
            if (b == null) break;
            Deposit(v);
            v.Job = JobKind.None;
            v.ManualTarget = null;
            v.GarrisonTarget = b.Id;
            sent++;
        }
        Teams[team].Version++;
        return sent;
    }

    /// <summary>村の中心にこもっている(向かっている)村人を全員外に出す。出した人数を返す。</summary>
    public int Ungarrison(int team)
    {
        int n = 0;
        foreach (var u in TeamUnits(team).Where(u => u.IsInside || u.GarrisonTarget != 0).ToList())
        {
            if (u.IsInside && Buildings.TryGetValue(u.Inside, out var b)) Eject(u, b);
            u.Inside = 0;
            u.GarrisonTarget = 0;
            n++;
        }
        Teams[team].Version++;
        return n;
    }

    private void Eject(Unit u, Building b)
    {
        var dir = Vector2.Normalize(BasePos(1 - u.Team) - b.Pos + new Vector2(0.01f, 0.02f * (u.Id % 7)));
        u.Pos = Map.NearestPassable(b.Pos - dir * (b.Radius + 0.8f));
        u.PrevPos = u.Pos;
        u.Inside = 0;
    }

    /// <summary>こもりに向かう村人の1ステップ。中にいる村人は何もしない。処理したら true。</summary>
    private bool StepGarrison(Unit v)
    {
        if (v.IsInside)
        {
            if (!Buildings.TryGetValue(v.Inside, out var home) || !home.Alive) v.Inside = 0;
            return v.IsInside;
        }
        if (v.GarrisonTarget == 0) return false;
        if (!Buildings.TryGetValue(v.GarrisonTarget, out var b) || !b.Alive)
        {
            v.GarrisonTarget = 0;
            return false;
        }
        if (MoveTo(v, b.Pos, b.Radius + 0.6f))
        {
            v.GarrisonTarget = 0;
            if (InsideCount(b) < GarrisonCap)
            {
                v.Inside = b.Id;
                v.Pos = b.Pos;
            }
        }
        return true;
    }

    /// <summary>村の中心から矢を撃つ。中の人数と同じ本数を、近くの見えている敵兵に順番に割り振る。</summary>
    private void StepTowers()
    {
        foreach (var b in Buildings.Values)
        {
            if (!b.Alive || !b.Done || b.Kind != BuildingKind.TownCenter) continue;
            b.ArrowCooldown = MathF.Max(0, b.ArrowCooldown - Defs.Dt);
            if (b.ArrowCooldown > 0) continue;
            int arrows = InsideCount(b);
            if (arrows == 0) continue;
            var targets = Units.Values
                .Where(o => o.Team != b.Team && o.Alive && !o.IsInside && Vision[b.Team].IsVisible(o.Pos) &&
                            Vector2.Distance(o.Pos, b.Pos) - b.Radius <= TowerRange)
                .OrderBy(o => o.Kind == UnitKind.Villager ? 1 : 0).ThenBy(o => Vector2.DistanceSquared(o.Pos, b.Pos))
                .ToList();
            if (targets.Count == 0) continue;
            for (int i = 0; i < arrows; i++)
            {
                var t = targets[i % targets.Count];
                if (!t.Alive) continue;
                t.Hp -= MathF.Max(1, ArrowDamage - t.Stats.Armor);
                t.LastHitAt = (float)Time;
                t.LastAttacker = b.Id;
                t.LastFoughtAt = (float)Time;
                Shots.Add(new Shot(b.Pos, t.Pos, Time, true));
            }
            b.ArrowCooldown = TowerCooldown;
        }
    }
}
