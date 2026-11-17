using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

public sealed partial class World
{
    // ------------------------------------------------------------ 建物(生産・建設)

    private void StepBuildings()
    {
        foreach (var b in Buildings.Values.ToList())
        {
            if (!b.Alive || !b.Done || b.Queue.Count == 0) continue;
            b.QueueTimer -= Defs.Dt;
            if (b.QueueTimer > 0) continue;
            var kind = b.Queue.Dequeue();
            if (b.Queue.Count > 0) b.QueueTimer = Defs.Units[b.Queue.Peek()].TrainTime;
            // 建物の、敵から遠い側の縁に出す
            var dir = Vector2.Normalize(BasePos(b.Team) - BasePos(1 - b.Team));
            SpawnUnit(b.Team, kind, b.Pos + dir * (b.Radius + 0.6f) + new Vector2((b.Id + Units.Count) % 3 - 1, 0) * 0.5f);
        }
    }

    private bool Afford(TeamState ts, int food, int wood, int gold) =>
        ts.Stock[Res.Food] >= food && ts.Stock[Res.Wood] >= wood && ts.Stock[Res.Gold] >= gold;

    private void Pay(TeamState ts, int food, int wood, int gold)
    {
        ts.Stock[Res.Food] -= food;
        ts.Stock[Res.Wood] -= wood;
        ts.Stock[Res.Gold] -= gold;
    }

    /// <summary>1秒ごとの経済の管理: 建設計画、生産、村人の割り当て。</summary>
    private void ManageEconomy(TeamState ts)
    {
        StartPlans(ts);
        Train(ts);
        Allocate(ts);
    }

    private void StartPlans(TeamState ts)
    {
        foreach (var plan in ts.Plans)
        {
            if (plan.Status == PlanStatus.Building)
            {
                if (Buildings.TryGetValue(plan.BuildingId, out var bb) && bb.Done)
                {
                    plan.Status = PlanStatus.Done;
                    ts.EconomyNote = $"{Defs.Ja(plan.Kind)}が完成({Map.RegionName(plan.Center)})";
                    ts.EconomyNoteAt = Time;
                    ts.Version++;
                    if (plan.Kind == BuildingKind.TownCenter || ts.Rally == default) { }
                }
                continue;
            }
            if (plan.Status != PlanStatus.Confirmed) continue;
            var s = Defs.Buildings[plan.Kind];
            if (!Afford(ts, 0, s.Wood, s.Gold)) continue;
            if (!SiteFree(plan.TileX, plan.TileY, s.Size))
            {
                var site = FindBuildSite(plan.Kind, plan.Center);
                if (site == null) { plan.Status = PlanStatus.Cancelled; continue; }
                (plan.TileX, plan.TileY) = site.Value;
            }
            Pay(ts, 0, s.Wood, s.Gold);
            var b = SpawnBuilding(ts.Team, plan.Kind, plan.TileX, plan.TileY, done: false);
            b.PlanId = plan.Id;
            plan.BuildingId = b.Id;
            plan.Status = PlanStatus.Building;
            ts.Version++;
            foreach (var v in TeamUnits(ts.Team).Where(v => v.Kind == UnitKind.Villager && v.Job != JobKind.Build)
                         .OrderBy(v => Vector2.DistanceSquared(v.Pos, b.Pos)).Take(ts.BuildersPerPlan))
            {
                Deposit(v);
                v.Job = JobKind.Build;
                v.JobTarget = b.Id;
            }
        }
    }

    /// <summary>未着工の確定済み計画に必要な資源(生産より建設を優先するために取り置く)。</summary>
    private (int wood, int gold) Reserved(TeamState ts)
    {
        int w = 0, g = 0;
        foreach (var p in ts.Plans.Where(p => p.Status == PlanStatus.Confirmed))
        {
            w += Defs.Buildings[p.Kind].Wood;
            g += Defs.Buildings[p.Kind].Gold;
        }
        return (w, g);
    }

    private void Train(TeamState ts)
    {
        int pop = Pop(ts.Team);
        var units = TeamUnits(ts.Team).ToList();
        var buildings = TeamBuildings(ts.Team).Where(b => b.Done).ToList();
        int Count(UnitKind k) => units.Count(u => u.Kind == k) + buildings.Sum(b => b.Queue.Count(q => q == k));
        var (rw, rg) = Reserved(ts);

        bool TryQueue(Building b, UnitKind k, bool respectReserve)
        {
            var s = Defs.Units[k];
            int w = s.Wood + (respectReserve ? rw : 0), g = s.Gold + (respectReserve ? rg : 0);
            if (pop >= Defs.PopCap || b.Queue.Count >= 2 || !Afford(ts, s.Food, w, g)) return false;
            Pay(ts, s.Food, s.Wood, s.Gold);
            if (b.Queue.Count == 0) b.QueueTimer = s.TrainTime;
            b.Queue.Enqueue(k);
            pop++;
            return true;
        }

        if (ts.VillagersContinuous)
            foreach (var tc in buildings.Where(b => b.Kind == BuildingKind.TownCenter))
                if (Count(UnitKind.Villager) < ts.VillagerTarget) TryQueue(tc, UnitKind.Villager, false);

        foreach (var bar in buildings.Where(b => b.Kind == BuildingKind.Barracks))
        {
            // 目標との差が一番大きい兵種から作る
            var want = ts.TrainGoal
                .Where(kv => kv.Value != 0 && (kv.Value < 0 || Count(kv.Key) < kv.Value))
                .OrderBy(kv => kv.Value < 0 ? Count(kv.Key) / 20f : Count(kv.Key) / (float)kv.Value)
                .Select(kv => kv.Key).ToList();
            foreach (var k in want)
                if (TryQueue(bar, k, true)) break;
        }
    }

    // ------------------------------------------------------------ 村人の割り当て

    public Dictionary<Res, int> GatherCounts(int team)
    {
        var d = new Dictionary<Res, int> { [Res.Food] = 0, [Res.Wood] = 0, [Res.Gold] = 0 };
        foreach (var v in TeamUnits(team).Where(v => v.Kind == UnitKind.Villager && v.Job == JobKind.Gather))
            d[v.JobRes]++;
        return d;
    }

    /// <summary>割り当て目標に合わせて村人を動かす。どの村人を動かすかはここで決める。</summary>
    private void Allocate(TeamState ts)
    {
        var villagers = TeamUnits(ts.Team).Where(v => v.Kind == UnitKind.Villager && v.Job != JobKind.Build).ToList();
        var idle = villagers.Where(v => v.Job == JobKind.None).ToList();
        foreach (Res r in Enum.GetValues<Res>())
        {
            var on = villagers.Where(v => v.Job == JobKind.Gather && v.JobRes == r).ToList();
            int surplus = on.Count - ts.Gather[r];
            if (surplus <= 0) continue;
            // 運んでいない者、資源から遠い者から外す
            foreach (var v in on.OrderBy(v => v.Carrying > 0 ? 1 : 0).ThenByDescending(v => v.Returning ? 0 : 1).Take(surplus))
            {
                v.Job = JobKind.None;
                idle.Add(v);
            }
        }
        foreach (Res r in Enum.GetValues<Res>().OrderByDescending(r => ts.Gather[r] - villagers.Count(v => v.Job == JobKind.Gather && v.JobRes == r)))
        {
            int deficit = ts.Gather[r] - villagers.Count(v => v.Job == JobKind.Gather && v.JobRes == r);
            if (deficit <= 0 || idle.Count == 0) continue;
            var near = NearestResourcePos(ts.Team, r, BasePos(ts.Team));
            foreach (var v in idle.OrderBy(v => near is { } p ? Vector2.DistanceSquared(v.Pos, p) : 0).Take(deficit).ToList())
            {
                v.Job = JobKind.Gather;
                v.JobRes = r;
                v.JobTarget = 0;
                v.JobTile = (-1, -1);
                idle.Remove(v);
            }
        }
    }

    private Vector2? NearestResourcePos(int team, Res r, Vector2 from)
    {
        if (r == Res.Wood)
        {
            var t = FindTree(from, team);
            return t is { } tt ? new Vector2(tt.X + 0.5f, tt.Y + 0.5f) : null;
        }
        return FindNode(r, from, team)?.Pos;
    }

    private Building? NearestDropoff(Unit u) =>
        TeamBuildings(u.Team).Where(b => b.Kind == BuildingKind.TownCenter && b.Done)
            .OrderBy(b => Vector2.DistanceSquared(b.Pos, u.Pos)).FirstOrDefault();

    private ResourceNode? FindNode(Res r, Vector2 from, int team)
    {
        var drops = TeamBuildings(team).Where(b => b.Kind == BuildingKind.TownCenter && b.Done).Select(b => b.Pos).ToList();
        return Nodes.Where(n => n.Kind == r && n.Amount > 0)
            .Where(n => drops.Count == 0 || drops.Min(d => Vector2.Distance(d, n.Pos)) < 18)
            .OrderBy(n => Vector2.Distance(n.Pos, from) + (drops.Count > 0 ? drops.Min(d => Vector2.Distance(d, n.Pos)) : 0))
            .FirstOrDefault();
    }

    private (int X, int Y)? FindTree(Vector2 from, int team)
    {
        var drop = BasePos(team);
        (int, int)? best = null;
        float bestScore = float.MaxValue;
        int cx = (int)from.X, cy = (int)from.Y;
        const int R = 22;
        for (int y = Math.Max(0, cy - R); y < Math.Min(Map.H, cy + R); y++)
        for (int x = Math.Max(0, cx - R); x < Math.Min(Map.W, cx + R); x++)
        {
            if (Map.At(x, y) != Tile.Forest) continue;
            if (!(Map.Passable(x + 1, y) || Map.Passable(x - 1, y) || Map.Passable(x, y + 1) || Map.Passable(x, y - 1))) continue;
            var c = new Vector2(x + 0.5f, y + 0.5f);
            float score = Vector2.Distance(c, from) + 1.2f * Vector2.Distance(c, drop);
            if (score < bestScore) { bestScore = score; best = (x, y); }
        }
        return best;
    }

    private void Deposit(Unit v)
    {
        if (v.Carrying <= 0) return;
        Teams[v.Team].Stock[v.CarryingRes] += v.Carrying;
        v.Carrying = 0;
    }

    private void StepVillager(Unit v)
    {
        // 攻撃されたら少し逃げる
        if (Time - v.LastHitAt < 1.0 && Units.TryGetValue(v.LastAttacker, out var atk) && atk.Alive)
        {
            MoveStep(v, v.Pos + Vector2.Normalize(v.Pos - atk.Pos + new Vector2(0.001f, 0)) * 2);
            return;
        }
        switch (v.Job)
        {
            case JobKind.Build:
            {
                if (!Buildings.TryGetValue(v.JobTarget, out var b) || b.Done)
                {
                    v.Job = JobKind.None;
                    return;
                }
                if (!MoveTo(v, b.Pos, b.Radius + 0.6f)) return;
                Face(v, b.Pos - v.Pos);
                var s = Defs.Buildings[b.Kind];
                b.Progress = MathF.Min(1, b.Progress + Defs.Dt / s.BuildTime * 0.75f);
                b.Hp = MathF.Min(b.MaxHp, b.Hp + b.MaxHp * 0.9f * Defs.Dt / s.BuildTime * 0.75f);
                v.Anim = Anim.Attack;
                if (b.Done) v.Job = JobKind.None;
                return;
            }
            case JobKind.Gather:
                StepGather(v);
                return;
            default:
                // 手の空いた村人は本部のそばで待つ
                var home = NearestDropoff(v);
                if (home != null) MoveTo(v, home.Pos + new Vector2(-2.5f, 1.5f), 1.5f);
                return;
        }
    }

    private void StepGather(Unit v)
    {
        if (v.Carrying > 0 && v.CarryingRes != v.JobRes) v.Returning = true;
        if (v.Carrying >= Defs.Carry) v.Returning = true;
        if (v.Returning)
        {
            var drop = NearestDropoff(v);
            if (drop == null) return;
            if (MoveTo(v, drop.Pos, drop.Radius + 0.5f))
            {
                Deposit(v);
                v.Returning = false;
            }
            return;
        }
        if (v.JobRes == Res.Wood)
        {
            var (tx, ty) = v.JobTile;
            if (tx < 0 || Map.At(tx, ty) != Tile.Forest)
            {
                var t = FindTree(v.Pos, v.Team);
                if (t == null) { v.Job = JobKind.None; return; }
                v.JobTile = t.Value;
                (tx, ty) = t.Value;
            }
            var c = new Vector2(tx + 0.5f, ty + 0.5f);
            if (!MoveTo(v, c, 1.05f)) return;
            Face(v, c - v.Pos);
            v.Anim = Anim.Attack;
            v.CarryingRes = Res.Wood;
            v.Carrying += Map.Chop(tx, ty, Defs.GatherRate * Defs.Dt);
            return;
        }
        var node = Nodes.FirstOrDefault(n => n.Id == v.JobTarget && n.Amount > 0);
        if (node == null)
        {
            node = FindNode(v.JobRes, v.Pos, v.Team);
            if (node == null) { v.Job = JobKind.None; return; }
            v.JobTarget = node.Id;
        }
        if (!MoveTo(v, node.Pos, node.Radius + 0.4f)) return;
        Face(v, node.Pos - v.Pos);
        v.Anim = Anim.Attack;
        v.CarryingRes = node.Kind;
        float got = MathF.Min(node.Amount, Defs.GatherRate * Defs.Dt);
        node.Amount -= got;
        v.Carrying += got;
    }

    // ------------------------------------------------------------ 建設地点

    public bool SiteFree(int tx, int ty, int size)
    {
        for (int y = ty - 1; y <= ty + size; y++)
        for (int x = tx - 1; x <= tx + size; x++)
        {
            if (!Map.In(x, y)) return false;
            bool inner = x >= tx && y >= ty && x < tx + size && y < ty + size;
            if (inner && !Map.Passable(x, y)) return false;
            if (!inner && Map.Occupant[y * Map.W + x] != 0) return false; // 建物同士は1マス空ける
        }
        foreach (var n in Nodes)
            if (n.Amount > 0 && n.Pos.X > tx - 1.5f && n.Pos.X < tx + size + 1.5f && n.Pos.Y > ty - 1.5f && n.Pos.Y < ty + size + 1.5f)
                return false;
        foreach (var p in Teams.SelectMany(t => t.Plans).Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Confirmed))
        {
            int ps = Defs.Buildings[p.Kind].Size;
            if (tx < p.TileX + ps + 1 && p.TileX < tx + size + 1 && ty < p.TileY + ps + 1 && p.TileY < ty + size + 1) return false;
        }
        return true;
    }

    /// <summary>near の近くで建てられる場所(左上のタイル)。</summary>
    public (int X, int Y)? FindBuildSite(BuildingKind kind, Vector2 near)
    {
        int size = Defs.Buildings[kind].Size;
        int cx = (int)(near.X - size / 2f), cy = (int)(near.Y - size / 2f);
        for (int r = 0; r < 14; r++)
        {
            (int, int)? best = null;
            float bestD = float.MaxValue;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                if (!SiteFree(cx + dx, cy + dy, size)) continue;
                float d = dx * dx + dy * dy;
                if (d < bestD) { bestD = d; best = (cx + dx, cy + dy); }
            }
            if (best != null) return best;
        }
        return null;
    }

    /// <summary>資源の種類で指定された場所の近く、建物が建てやすい地点(村の中心を資源の近くに、など)。</summary>
    public Vector2? NearResource(int team, Res r, Vector2 from, bool unclaimed)
    {
        if (r == Res.Wood) return NearestResourcePos(team, r, from);
        var nodes = Nodes.Where(n => n.Kind == r && n.Amount > 0);
        if (unclaimed)
        {
            // まだ自軍の本部から遠い資源(新しい拠点向け)
            var tcs = TeamBuildings(team).Where(b => b.Kind == BuildingKind.TownCenter).Select(b => b.Pos).ToList();
            nodes = nodes.Where(n => tcs.All(t => Vector2.Distance(t, n.Pos) > 15));
        }
        var node = nodes.OrderBy(n => Vector2.Distance(n.Pos, from)).FirstOrDefault();
        if (node == null) return null;
        // 資源と基地を結ぶ側に少しずらす(資源を囲むように)
        var toward = Vector2.Normalize(BasePos(team) - node.Pos + new Vector2(0.01f, 0));
        return node.Pos + toward * 4.5f;
    }
}
