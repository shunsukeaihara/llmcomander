using System;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>
/// 決定論的な敵AI。プレイヤーと同じ仕組み(経済の目標、建設計画、作戦と交戦規則)を使って動かす。
/// 時間割で内政を進め、一定間隔で攻撃の波を送る。
/// </summary>
public sealed class EnemyAI
{
    private readonly World _w;
    private readonly int _team;
    public float FirstWave = 600;
    public float WaveInterval = 240;
    private double _nextWave;
    private double _nextThink;
    private int _wave;

    public EnemyAI(World w, int team = 1)
    {
        _w = w;
        _team = team;
        _nextWave = FirstWave;
    }

    private TeamState T => _w.Teams[_team];

    public void Step()
    {
        if (_w.Time < _nextThink) return;
        _nextThink = _w.Time + 2;
        Economy();
        Waves();
    }

    private void Economy()
    {
        var villagers = _w.TeamUnits(_team).Count(u => u.Kind == UnitKind.Villager);
        T.VillagerTarget = 32;
        bool hasBarracks = _w.TeamBuildings(_team).Any(b => b.Kind == BuildingKind.Barracks);
        int forBuild = T.Plans.Any(p => p.Status == PlanStatus.Building) ? T.BuildersPerPlan : 0;
        int n = villagers - forBuild;
        T.Gather[Res.Food] = (int)MathF.Round(n * (hasBarracks ? 0.45f : 0.55f));
        T.Gather[Res.Gold] = hasBarracks ? (int)MathF.Round(n * 0.22f) : 0;
        T.Gather[Res.Wood] = Math.Max(0, n - T.Gather[Res.Food] - T.Gather[Res.Gold]);

        int barracks = _w.TeamBuildings(_team).Count(b => b.Kind == BuildingKind.Barracks) +
                       T.Plans.Count(p => p.Kind == BuildingKind.Barracks && p.Status is PlanStatus.Confirmed or PlanStatus.Building);
        int wantBarracks = _w.Time > 480 ? 2 : _w.Time > 150 ? 1 : 0;
        if (barracks < wantBarracks)
        {
            var site = _w.FindBuildSite(BuildingKind.Barracks, _w.BasePos(_team) + Vector2.Normalize(T.Rally - _w.BasePos(_team)) * 6);
            if (site is { } s)
                T.Plans.Add(new BuildPlan { Id = _w.NextId(), Kind = BuildingKind.Barracks, TileX = s.X, TileY = s.Y, Status = PlanStatus.Confirmed, Near = "base" });
        }
        // 往復が長い資源のそばに採集所を建てる(プレイヤーと同じ建物、同じ費用)
        bool storePending = T.Plans.Any(p => p.Kind == BuildingKind.Storehouse && p.Status is PlanStatus.Confirmed or PlanStatus.Building);
        int stores = _w.TeamBuildings(_team).Count(b => b.Kind == BuildingKind.Storehouse);
        if (!storePending && stores < 4 && _w.Time > 90)
        {
            var haul = _w.HaulDistance(_team);
            foreach (var r in new[] { Res.Wood, Res.Gold, Res.Food })
            {
                if (T.Gather[r] < 3 || !haul.TryGetValue(r, out var d) || d < 6) continue;
                if (_w.StorehouseSpot(_team, r) is not { } spot || _w.FindBuildSite(BuildingKind.Storehouse, spot) is not { } ss) continue;
                T.Plans.Add(new BuildPlan { Id = _w.NextId(), Kind = BuildingKind.Storehouse, TileX = ss.X, TileY = ss.Y, Status = PlanStatus.Confirmed, Near = Defs.Key(r) });
                break;
            }
        }
        if (hasBarracks)
        {
            T.TrainGoal[UnitKind.Spearman] = _w.Time > 900 ? -1 : 8;
            T.TrainGoal[UnitKind.Archer] = _w.Time > 900 ? -1 : 8;
            T.TrainGoal[UnitKind.Cavalry] = _w.Time > 700 ? 6 : 3;
        }
    }

    private void Waves()
    {
        // 攻撃中の部隊が減ったら退却させ、予備に戻す
        foreach (var op in T.ActiveOps.Where(o => o.Name.StartsWith("攻撃隊")).ToList())
        {
            int alive = _w.Members(op).Count();
            int initial = op.Roles.Sum(r => r.InitialCount);
            if (alive == 0)
            {
                op.Disbanded = true;
                continue;
            }
            if (alive < initial * 0.3f && op.Roles.All(r => r.Stance != Stance.Retreat))
            {
                var home = new Place("自軍基地", new[] { T.Rally });
                foreach (var r in op.Roles) r.Apply(new RulePatch { Stance = Stance.Retreat, Anchor = home }, _w.Time);
            }
            else if (op.Roles.All(r => r.Stance == Stance.Retreat) &&
                     _w.Members(op).All(u => Vector2.Distance(u.Pos, T.Rally) < 6))
            {
                foreach (var u in _w.Members(op))
                {
                    u.OpId = T.Reserve.Id;
                    u.Role = _w.EnsureRole(T.Reserve, u.Kind).Name;
                }
                op.Disbanded = true;
            }
        }

        if (_w.Time < _nextWave) return;
        var army = _w.Members(T.Reserve).ToList();
        if (army.Count < 6) return;
        _nextWave = _w.Time + WaveInterval;
        _wave++;

        var op2 = new Operation { Id = _w.NextId(), Team = _team, Name = $"攻撃隊{_wave}", CreatedAt = _w.Time };
        var target = new Place("敵基地", new[] { _w.BasePos(1 - _team) });
        foreach (var g in army.GroupBy(u => u.Kind))
        {
            var role = new Role(Defs.Ja(g.Key), target) { Kind = g.Key, Stance = Stance.Advance };
            role.Priority = g.Key switch
            {
                UnitKind.Spearman => new() { UnitKind.Cavalry },
                UnitKind.Cavalry => new() { UnitKind.Archer, UnitKind.Villager },
                UnitKind.Archer => new() { UnitKind.Spearman },
                _ => new(),
            };
            role.Kite = g.Key == UnitKind.Archer;
            op2.Roles.Add(role);
            foreach (var u in g)
            {
                u.OpId = op2.Id;
                u.Role = role.Name;
            }
            role.InitialCount = g.Count();
        }
        T.Ops.Add(op2);
    }
}
