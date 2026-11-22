using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

public sealed record Shot(Vector2 From, Vector2 To, double Time, bool Arrow);

/// <summary>ゲーム世界。Godot に依存しない。1回の Step で Defs.Dt 秒進む。</summary>
public sealed partial class World
{
    public readonly GameMap Map;
    public readonly Dictionary<int, Unit> Units = new();
    public readonly Dictionary<int, Building> Buildings = new();
    public readonly List<ResourceNode> Nodes = new();
    public readonly TeamState[] Teams = { new() { Team = 0 }, new() { Team = 1 } };
    /// <summary>チームごとの視界(霧)。</summary>
    public readonly Visibility[] Vision;
    public readonly List<Shot> Shots = new();
    public double Time;
    public int? Winner;
    private int _nextId = 1;
    private long _tick;
    private int _pathBudget;
    public const int PathBudgetPerStep = 16;

    public World(GameMap map)
    {
        Map = map;
        foreach (var t in Teams)
        {
            t.Ops.Add(new Operation { Id = NextId(), Team = t.Team, Name = "経済", IsEconomy = true });
            t.Ops.Add(new Operation { Id = NextId(), Team = t.Team, Name = "予備", IsReserve = true });
            t.ReinforceOp = t.Reserve.Id;
        }
        Vision = new[] { new Visibility(map), new Visibility(map) };
    }

    public int NextId() => _nextId++;

    // ------------------------------------------------------------ 生成

    public Unit SpawnUnit(int team, UnitKind kind, Vector2 pos)
    {
        var s = Defs.Units[kind];
        var u = new Unit { Id = NextId(), Team = team, Kind = kind, Pos = Map.NearestPassable(pos), Hp = s.Hp, MaxHp = s.Hp };
        var ts = Teams[team];
        if (kind == UnitKind.Villager)
        {
            u.OpId = ts.Economy.Id;
            u.Role = "村人";
        }
        else
        {
            var op = ts.Ops.FirstOrDefault(o => o.Id == ts.ReinforceOp && !o.Disbanded) ?? ts.Reserve;
            var role = op.FindRole(ts.ReinforceRole) is { } r && (r.Kind is null || r.Kind == kind) ? r : EnsureRole(op, kind);
            u.OpId = op.Id;
            u.Role = role.Name;
        }
        u.PrevPos = u.Pos;
        Units[u.Id] = u;
        return u;
    }

    public Building SpawnBuilding(int team, BuildingKind kind, int tx, int ty, bool done = true)
    {
        var s = Defs.Buildings[kind];
        var b = new Building
        {
            Id = NextId(), Team = team, Kind = kind, TileX = tx, TileY = ty,
            Pos = new Vector2(tx + s.Size / 2f, ty + s.Size / 2f),
            MaxHp = s.Hp, Hp = done ? s.Hp : s.Hp * 0.1f, Progress = done ? 1 : 0,
        };
        for (int y = ty; y < ty + s.Size; y++)
        for (int x = tx; x < tx + s.Size; x++)
            if (Map.In(x, y)) Map.Occupant[y * Map.W + x] = b.Id;
        Map.Version++;
        Buildings[b.Id] = b;
        return b;
    }

    public ResourceNode AddNode(Res kind, Vector2 pos, float amount)
    {
        var n = new ResourceNode { Id = NextId(), Kind = kind, Pos = pos, Amount = amount };
        Nodes.Add(n);
        return n;
    }

    /// <summary>作戦に兵種の役割がなければ作る。</summary>
    public Role EnsureRole(Operation op, UnitKind kind)
    {
        var role = op.Roles.FirstOrDefault(r => r.Kind == kind);
        if (role != null) return role;
        var anchor = op.Roles.FirstOrDefault()?.Anchor ?? new Place(Teams[op.Team].RallyName, new[] { Teams[op.Team].Rally });
        role = new Role(Defs.Ja(kind), anchor) { Kind = kind };
        var like = op.Roles.FirstOrDefault();
        if (like != null) role.Stance = like.Stance;
        op.Roles.Add(role);
        return role;
    }

    // ------------------------------------------------------------ 問い合わせ

    public IEnumerable<Unit> TeamUnits(int team) => Units.Values.Where(u => u.Team == team && u.Alive);
    public IEnumerable<Building> TeamBuildings(int team) => Buildings.Values.Where(b => b.Team == team && b.Alive);
    public IEnumerable<Unit> Members(Operation op) => Units.Values.Where(u => u.Alive && u.Team == op.Team && u.OpId == op.Id);
    public IEnumerable<Unit> Members(Operation op, Role role) => Members(op).Where(u => u.Role == role.Name);

    public int Pop(int team) =>
        TeamUnits(team).Count() + TeamBuildings(team).Sum(b => b.Queue.Count);

    public Vector2 BasePos(int team)
    {
        var tc = TeamBuildings(team).Where(b => b.Kind == BuildingKind.TownCenter).OrderBy(b => b.Id).FirstOrDefault();
        return tc?.Pos ?? Map.FindRegion(team == 0 ? "west_base" : "east_base")!.Center;
    }

    /// <summary>
    /// team が知っている敵の本拠地(見たことのある敵の村の中心)。まだ見つけていなければ null。
    /// 敵AIのような決定論的な処理は BasePos(1 - team) で本当の位置を使ってよいが、LLM に渡す情報はこちらを使う。
    /// </summary>
    public Vector2? KnownEnemyBase(int team)
    {
        var tc = Vision[team].Buildings.Values.Where(b => b.Kind == BuildingKind.TownCenter).OrderBy(b => b.Id).FirstOrDefault();
        return tc == null ? null : new Vector2(tc.TileX + 1.5f, tc.TileY + 1.5f);
    }

    /// <summary>場所の名前を座標にする。プレイヤーの目印、地名、自軍基地・敵基地(見つけていれば)。</summary>
    public Place? ResolvePlace(int team, string name)
    {
        string n = name.Trim();
        var ts = Teams[team];
        var marker = ts.Markers.FirstOrDefault(m => m.Name == n);
        if (marker != null) return marker.ToPlace();
        switch (n)
        {
            case "own_base" or "自軍基地" or "基地":
                return new Place("自軍基地", new[] { BasePos(team) });
            case "enemy_base" or "敵基地":
                // 敵の本拠地は、敵の村の中心を見つけるまでわからない
                return KnownEnemyBase(team) is { } eb ? new Place("敵基地", new[] { eb }) : null;
            case "rally" or "集合地点":
                return new Place(ts.RallyName, new[] { ts.Rally });
        }
        var region = Map.FindRegion(n);
        if (region == null) return null;
        // 地名は両チームから見て同じ。own_base / enemy_base だけは見る側で入れ替える
        return new Place(region.Name, new[] { region.Center });
    }

    public Role? RoleOf(Unit u)
    {
        var op = Teams[u.Team].Ops.FirstOrDefault(o => o.Id == u.OpId);
        return op?.FindRole(u.Role);
    }

    // ------------------------------------------------------------ 1ステップ

    /// <summary>
    /// 目印を動かす。その目印を持ち場・行き先・条件の場所にしている役割も、新しい位置に合わせる。
    /// 合わせた役割の数を返す。
    /// </summary>
    public int MoveMarker(int team, Marker marker, IReadOnlyList<Vector2> points)
    {
        var ts = Teams[team];
        marker.Points = points.Select(p => Map.NearestPassable(p)).ToList();
        var place = marker.ToPlace();
        int changed = 0;
        foreach (var op in ts.ActiveOps)
        {
            bool touched = false;
            foreach (var role in op.Roles)
            {
                if (role.Anchor.Name == marker.Name)
                {
                    role.Anchor = place;
                    role.Arrived = false;
                    touched = true;
                    changed++;
                }
                if (role.Trigger is { } tr)
                {
                    if (tr.Place?.Name == marker.Name) { tr.Place = place; touched = true; }
                    if (tr.Then.Anchor?.Name == marker.Name) { tr.Then.Anchor = place; touched = true; }
                }
            }
            if (touched) op.Log(Time, $"目印「{marker.Name}」を{Map.RegionName(place.Center)}付近へ移動");
        }
        ts.Version++;
        return changed;
    }

    /// <summary>新兵の集合場所を変える。予備の部隊もそこで待つ。</summary>
    public void SetRally(int team, Place place)
    {
        var ts = Teams[team];
        ts.Rally = Map.NearestPassable(place.Center);
        ts.RallyName = place.Name;
        var anchor = new Place(place.Name, new[] { ts.Rally });
        foreach (var r in ts.Reserve.Roles) r.Anchor = anchor;
        ts.Version++;
    }

    /// <summary>直接指示の作戦から、兵のいなくなった役割を消す。全部いなくなったら作戦ごと解散する。</summary>
    private void PruneManualOps(TeamState ts)
    {
        foreach (var op in ts.ActiveOps.Where(o => o.IsManual).ToList())
        {
            int before = op.Roles.Count;
            op.Roles.RemoveAll(r => !Members(op, r).Any());
            if (op.Roles.Count == 0) op.Disbanded = true;
            if (op.Roles.Count != before) ts.Version++;
        }
    }

    /// <summary>両チームの視界を計算し直す(0.25秒ごと。試合の開始時にも呼ぶ)。</summary>
    public void UpdateVision()
    {
        Vision[0].Update(this, 0);
        Vision[1].Update(this, 1);
    }

    public void Step()
    {
        if (Winner != null) return;
        Time += Defs.Dt;
        _tick++;
        _pathBudget = PathBudgetPerStep;
        foreach (var u in Units.Values) u.PrevPos = u.Pos;
        if (_tick % 5 == 1) UpdateVision();
        Shots.RemoveAll(s => Time - s.Time > 0.4);

        long p = Profiler.Begin();
        if (_tick % 20 == 0)
            foreach (var t in Teams) ManageEconomy(t);
        Profiler.End("経済", p);
        p = Profiler.Begin();
        if (_tick % 5 == 0)
            foreach (var t in Teams)
            {
                CheckTriggers(t);
                CheckArrivals(t);
                CheckScouting(t);
            }
        if (_tick % 20 == 10)
            foreach (var t in Teams) PruneManualOps(t);
        Profiler.End("条件", p);

        p = Profiler.Begin();
        StepBuildings();
        StepTowers();
        BuildSlots();
        Profiler.End("隊列", p);
        foreach (var u in Units.Values.ToList())
        {
            if (!u.Alive) continue;
            p = Profiler.Begin();
            u.Cooldown = MathF.Max(0, u.Cooldown - Defs.Dt);
            u.AnimTime += Defs.Dt;
            var before = u.Pos;
            if (u.Kind == UnitKind.Villager) StepVillager(u);
            else StepMilitary(u);
            if (u.Anim != Anim.Attack || u.Cooldown < u.Stats.Cooldown - 0.5f)
                u.Anim = Vector2.DistanceSquared(before, u.Pos) > 1e-6f ? Anim.Walk : Anim.Idle;
            Profiler.End(u.Kind == UnitKind.Villager ? "村人" : "兵", p);
        }
        p = Profiler.Begin();
        Separate();
        Profiler.End("押し合い", p);
        p = Profiler.Begin();
        RemoveDead();
        CheckVictory();
        Profiler.End("後処理", p);
    }

    private void CheckVictory()
    {
        for (int t = 0; t < 2; t++)
            if (!Buildings.Values.Any(b => b.Team == t && b.Alive && b.Kind == BuildingKind.TownCenter))
                Winner = 1 - t;
    }

    private void RemoveDead()
    {
        foreach (var u in Units.Values.Where(u => !u.Alive).ToList())
        {
            Units.Remove(u.Id);
            var op = Teams[u.Team].Ops.FirstOrDefault(o => o.Id == u.OpId);
            if (op != null && !op.IsEconomy) op.Eventful = true;
        }
        foreach (var b in Buildings.Values.Where(b => !b.Alive).ToList())
        {
            // 中にこもっていた村人は外に出る
            foreach (var u in Units.Values.Where(u => u.Inside == b.Id)) Eject(u, b);
            Buildings.Remove(b.Id);
            for (int y = b.TileY; y < b.TileY + b.Size; y++)
            for (int x = b.TileX; x < b.TileX + b.Size; x++)
                if (Map.In(x, y) && Map.Occupant[y * Map.W + x] == b.Id) Map.Occupant[y * Map.W + x] = 0;
            Map.Version++;
            Teams[b.Team].Alerts.Add(new Alert(Time, b.Pos, $"{Defs.Ja(b.Kind)}が破壊されました"));
            var plan = Teams[b.Team].Plans.FirstOrDefault(p => p.BuildingId == b.Id);
            if (plan != null && plan.Status == PlanStatus.Building) plan.Status = PlanStatus.Cancelled;
        }
    }

    // ------------------------------------------------------------ 戦闘

    private void Damage(Unit from, Entity to)
    {
        int bonus = to is Unit tu ? Defs.Bonus(from.Kind, tu.Kind) : 0;
        int armor = to is Unit tu2 ? tu2.Stats.Armor : 0;
        float dmg = MathF.Max(1, from.Stats.Attack + bonus - armor);
        if (to is Building) dmg = from.Kind == UnitKind.Archer ? 1 : dmg * 1.5f;
        to.Hp -= dmg;
        to.LastHitAt = (float)Time;
        to.LastAttacker = from.Id;
        from.LastFoughtAt = (float)Time;
        if (to is Unit victim) victim.LastFoughtAt = (float)Time;
        from.Cooldown = from.Stats.Cooldown;
        from.Anim = Anim.Attack;
        from.AnimTime = 0;
        Shots.Add(new Shot(from.Pos, to.Pos, Time, from.Kind == UnitKind.Archer));

        // 非戦闘員や建物が攻撃されたら警報
        bool civilian = to is Building || to is Unit { Kind: UnitKind.Villager };
        var ts = Teams[to.Team];
        if (civilian && !ts.Alerts.Any(a => Time - a.Time < 20 && Vector2.Distance(a.Pos, to.Pos) < 12))
        {
            string what = to is Building b ? Defs.Ja(b.Kind) : "村人";
            ts.Alerts.Add(new Alert(Time, to.Pos, $"{Map.RegionName(to.Pos)}: {what}が攻撃されています"));
        }
        var op = Teams[from.Team].Ops.FirstOrDefault(o => o.Id == from.OpId);
        if (op != null && Time - op.StatusAt > 3 && !op.IsEconomy) op.Eventful = true;
    }

    private Entity? PickTarget(Unit u, Role? role, Vector2 center, float radius, bool buildings)
    {
        Entity? best = null;
        float bestScore = float.MaxValue;
        float r2 = radius * radius;
        foreach (var o in Units.Values)
        {
            if (o.Team == u.Team || !o.Alive || o.IsInside || !Vision[u.Team].IsVisible(o.Pos)) continue;
            float d2 = Vector2.DistanceSquared(o.Pos, center);
            if (d2 > r2) continue;
            float score = Vector2.Distance(o.Pos, u.Pos);
            if (role != null && role.Priority.Count > 0)
            {
                int idx = role.Priority.IndexOf(o.Kind);
                score += idx >= 0 ? idx * 1.5f : 4f;
            }
            if (o.Kind == UnitKind.Villager) score += 2.5f;
            if (score < bestScore) { bestScore = score; best = o; }
        }
        if (best != null || !buildings) return best;
        foreach (var b in Buildings.Values)
        {
            if (b.Team == u.Team || !b.Alive || !Vision[u.Team].AnyTileVisible(b)) continue;
            float d = Vector2.Distance(b.Pos, center) - b.Radius;
            if (d > radius) continue;
            float score = Vector2.Distance(b.Pos, u.Pos) - b.Radius;
            if (score < bestScore) { bestScore = score; best = b; }
        }
        return best;
    }

    private float Reach(Unit u, Entity t) => u.Stats.Range + u.Radius + t.Radius;

    private void Engage(Unit u, Role? role, Entity t)
    {
        float d = Vector2.Distance(u.Pos, t.Pos);
        Face(u, t.Pos - u.Pos);
        if (role is { Kite: true } && u.Kind == UnitKind.Archer)
        {
            // 近くの近接兵から距離を取る。攻撃の直後(装填中)に下がる
            var melee = Units.Values
                .Where(o => o.Team != u.Team && o.Alive && o.Kind != UnitKind.Archer && o.Kind != UnitKind.Villager)
                .OrderBy(o => Vector2.DistanceSquared(o.Pos, u.Pos)).FirstOrDefault();
            if (melee != null && Vector2.Distance(melee.Pos, u.Pos) < 2.6f && u.Cooldown > 0.2f)
            {
                var away = Vector2.Normalize(u.Pos - melee.Pos + new Vector2(0.001f, 0));
                var dest = u.Pos + away * 2f;
                if (Map.Passable(dest)) MoveStep(u, dest);
                else MoveStep(u, u.Pos + new Vector2(-away.Y, away.X) * 2f);
                return;
            }
        }
        if (d <= Reach(u, t))
        {
            if (u.Cooldown <= 0) Damage(u, t);
            return;
        }
        MoveTo(u, t.Pos, Reach(u, t) * 0.9f);
    }

    // ------------------------------------------------------------ 移動

    private static void Face(Unit u, Vector2 dir)
    {
        if (dir.LengthSquared() > 1e-4f) u.Facing = Vector2.Normalize(dir);
    }

    /// <summary>障害物を考えずに1ステップ進む。</summary>
    private void MoveStep(Unit u, Vector2 dest)
    {
        var d = dest - u.Pos;
        float len = d.Length();
        if (len < 1e-4f) return;
        float step = MathF.Min(u.Stats.Speed * Defs.Dt, len);
        var next = u.Pos + d / len * step;
        if (Map.Passable(next)) u.Pos = next;
        else if (Map.Passable(new Vector2(next.X, u.Pos.Y))) u.Pos = new Vector2(next.X, u.Pos.Y);
        else if (Map.Passable(new Vector2(u.Pos.X, next.Y))) u.Pos = new Vector2(u.Pos.X, next.Y);
        Face(u, d);
    }

    /// <summary>goal まで stop 以内に近づく。到着していれば true。</summary>
    private bool MoveTo(Unit u, Vector2 goal, float stop)
    {
        float dist = Vector2.Distance(u.Pos, goal);
        if (dist <= stop)
        {
            u.Path = null;
            return true;
        }
        if (dist < 3f)
        {
            u.Path = null;
            MoveStep(u, goal);
            return false;
        }
        bool stale = u.Path == null || Vector2.Distance(u.PathGoal, goal) > 1.5f || Time > u.RepathAt;
        if (stale)
        {
            // 1ステップで探す経路の数には上限がある。超えたら今回は直進し、次のステップで探す
            if (_pathBudget <= 0)
            {
                MoveStep(u, goal);
                return false;
            }
            _pathBudget--;
            long pp = Profiler.Begin();
            // 見通しが通るなら経路探索はしない(毎ステップの見通し判定もしない)
            u.Path = Map.LineClear(u.Pos, goal) ? new List<Vector2> { goal } : Map.FindPath(u.Pos, goal);
            Profiler.End("経路探索", pp);
            if (Profiler.Enabled) Profiler.Totals["経路探索の回数"] = Profiler.Totals.GetValueOrDefault("経路探索の回数") + 1;
            u.PathIndex = 0;
            u.PathGoal = goal;
            u.RepathAt = (float)Time + 4f + (u.Id % 10) * 0.2f;
        }
        if (u.Path == null || u.PathIndex >= u.Path.Count)
        {
            MoveStep(u, goal);
            return false;
        }
        var wp = u.Path[u.PathIndex];
        if (Vector2.Distance(u.Pos, wp) < 0.25f) u.PathIndex++;
        MoveStep(u, wp);
        return false;
    }

    private void Separate()
    {
        var list = Units.Values.Where(u => u.Alive && !u.IsInside).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            for (int j = i + 1; j < list.Count; j++)
            {
                var b = list[j];
                float min = (a.Radius + b.Radius) * 0.9f;
                var d = b.Pos - a.Pos;
                if (MathF.Abs(d.X) >= min || MathF.Abs(d.Y) >= min) continue;
                float len = d.Length();
                if (len >= min) continue;
                if (len < 1e-4f) { d = new Vector2(0.01f * ((a.Id % 3) - 1 + 0.5f), 0.01f); len = d.Length(); }
                var push = d / len * (min - len) * 0.5f;
                if (Map.Passable(a.Pos - push)) a.Pos -= push;
                if (Map.Passable(b.Pos + push)) b.Pos += push;
            }
        }
        foreach (var u in list)
        {
            u.Pos = new Vector2(Math.Clamp(u.Pos.X, 0.3f, Map.W - 0.3f), Math.Clamp(u.Pos.Y, 0.3f, Map.H - 0.3f));
            if (!Map.Passable(u.Pos)) u.Pos = Map.NearestPassable(u.Pos);
        }
    }
}
