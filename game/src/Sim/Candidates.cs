using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>建設候補地。ゲームが計算し、LLM は特徴を見て選ぶ。</summary>
public sealed record SiteCandidate(
    BuildingKind Kind,
    int TileX,
    int TileY,
    string Region,
    string Note,
    float ToGold,
    float ToWood,
    float ToFood,
    float FromOwnBase,
    float? FromEnemyBase,
    int EnemiesSeenNearby)
{
    public Vector2 Center => new(TileX + Defs.Buildings[Kind].Size / 2f, TileY + Defs.Buildings[Kind].Size / 2f);
}

public sealed partial class World
{
    /// <summary>建物の種類ごとの建設候補地。</summary>
    public List<SiteCandidate> BuildCandidates(int team, BuildingKind kind)
    {
        var points = new List<(Vector2 pos, string note)>();
        var home = BasePos(team);
        var toFront = Vector2.Normalize(Teams[team].Rally - home + new Vector2(0.001f, 0));
        if (kind == BuildingKind.TownCenter)
        {
            // まだ自軍の村の中心から遠い資源のまとまりごとに1か所
            var tcs = TeamBuildings(team).Where(b => b.Kind == BuildingKind.TownCenter).Select(b => b.Pos).ToList();
            foreach (var (center, res) in ResourceClusters())
            {
                // まだ見ていない資源のことは知らない
                if (!Vision[team].IsExplored((int)center.X, (int)center.Y)) continue;
                if (tcs.Any(t => Vector2.Distance(t, center) < 15)) continue;
                // 見つけた敵の建物のそばの資源は候補にしない(見ていない敵の基地のことは知らない)
                if (Vision[team].Buildings.Values.Any(b => Vector2.Distance(center, new Vector2(b.TileX + 1.5f, b.TileY + 1.5f)) < 15)) continue;
                var toward = Vector2.Normalize(home - center + new Vector2(0.001f, 0));
                points.Add((center + toward * 4.5f, $"{Defs.Ja(res)}の資源のそば"));
            }
        }
        else if (kind == BuildingKind.Storehouse)
        {
            // 今の置き場(村の中心・採集所)から遠い、見つけた資源のそば
            var drops = DropoffPositions(team);
            foreach (var (center, res) in ResourceClusters())
            {
                if (!Vision[team].IsExplored((int)center.X, (int)center.Y)) continue;
                float toDrop = drops.Count > 0 ? drops.Min(d => Vector2.Distance(d, center)) : 99;
                if (toDrop < 7) continue;
                if (Vision[team].Buildings.Values.Any(b => Vector2.Distance(center, new Vector2(b.TileX + 1.5f, b.TileY + 1.5f)) < 12)) continue;
                var toward = Vector2.Normalize(home - center + new Vector2(0.001f, 0));
                points.Add((center + toward * 2.5f, $"{Defs.Ja(res)}の資源のそば。今の置き場から{toDrop:0}"));
            }
            foreach (var (edge, toDrop) in ForestEdges(team, drops))
                points.Add((edge, $"森の縁。今の置き場から{toDrop:0}"));
        }
        else
        {
            var side = new Vector2(-toFront.Y, toFront.X);
            points.Add((home + toFront * 7, "基地の前線側"));
            points.Add((home - toFront * 6, "基地の後方"));
            points.Add((home + side * 7, "基地の横"));
            points.Add((home - side * 7, "基地の反対の横"));
            foreach (var tc in TeamBuildings(team).Where(b => b.Kind == BuildingKind.TownCenter && Vector2.Distance(b.Pos, home) > 15))
                points.Add((tc.Pos + Vector2.Normalize(home - tc.Pos) * 6, "第2拠点のそば"));
        }

        var result = new List<SiteCandidate>();
        foreach (var (pos, note) in points)
        {
            var site = FindBuildSite(kind, pos);
            if (site == null) continue;
            var c = new Vector2(site.Value.X + Defs.Buildings[kind].Size / 2f, site.Value.Y + Defs.Buildings[kind].Size / 2f);
            if (result.Any(r => Vector2.Distance(r.Center, c) < 4)) continue;
            int enemies = Vision[team].LastSeen.Values.Count(s => s.Kind != UnitKind.Villager && Vector2.Distance(s.Pos, c) < 12);
            result.Add(new SiteCandidate(kind, site.Value.X, site.Value.Y, Map.RegionName(c), note,
                NearestNode(Res.Gold, c), NearestTree(c), NearestNode(Res.Food, c),
                Vector2.Distance(c, home), KnownEnemyBase(team) is { } eb ? Vector2.Distance(c, eb) : null, enemies));
        }
        return result.OrderBy(r => r.FromOwnBase).Take(kind == BuildingKind.Barracks ? 4 : 5).ToList();
    }

    /// <summary>今の置き場から離れた、自軍基地に近い森の縁(採集所の候補)。互いに離れた2か所まで。</summary>
    private List<(Vector2 pos, float toDrop)> ForestEdges(int team, List<Vector2> drops)
    {
        var home = BasePos(team);
        var found = new List<(Vector2 pos, float toDrop, float fromHome)>();
        for (int y = 3; y < Map.H - 3; y++)
        for (int x = 3; x < Map.W - 3; x++)
        {
            if (Map.At(x, y) != Tile.Grass || !Vision[team].IsExplored(x, y)) continue;
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float fromHome = Vector2.Distance(p, home);
            if (fromHome > 30) continue;
            bool edge = Map.At(x + 1, y) == Tile.Forest || Map.At(x - 1, y) == Tile.Forest || Map.At(x, y + 1) == Tile.Forest || Map.At(x, y - 1) == Tile.Forest;
            if (!edge) continue;
            float toDrop = drops.Count > 0 ? drops.Min(d => Vector2.Distance(d, p)) : 99;
            if (toDrop < 9) continue;
            found.Add((p, toDrop, fromHome));
        }
        var picked = new List<(Vector2, float)>();
        foreach (var f in found.OrderBy(f => f.fromHome))
        {
            if (picked.Any(q => Vector2.Distance(q.Item1, f.pos) < 10)) continue;
            picked.Add((f.pos, f.toDrop));
            if (picked.Count == 2) break;
        }
        return picked;
    }

    /// <summary>「金の近くに採集所」の場所。置き場から遠いのに使われている(使われそうな)資源のそば。</summary>
    public Vector2? StorehouseSpot(int team, Res r)
    {
        var home = BasePos(team);
        var drops = DropoffPositions(team);
        float ToDrop(Vector2 p) => drops.Count > 0 ? drops.Min(d => Vector2.Distance(d, p)) : 99;
        // 今その資源を採っている村人がいれば、その仕事場のそば
        var working = TeamUnits(team).Where(v => v.Kind == UnitKind.Villager && v.Job == JobKind.Gather && v.JobRes == r && !v.Returning && ToDrop(v.Pos) > 6).ToList();
        if (working.Count > 0)
        {
            var c = new Vector2(working.Average(v => v.Pos.X), working.Average(v => v.Pos.Y));
            return c + Vector2.Normalize(home - c + new Vector2(0.001f, 0)) * 2f;
        }
        if (r == Res.Wood)
            return ForestEdges(team, drops).Select(e => (Vector2?)e.pos).FirstOrDefault() ?? NearestResourcePos(team, r, home);
        var node = Nodes.Where(n => n.Kind == r && n.Amount > 0 && Vision[team].IsExplored((int)n.Pos.X, (int)n.Pos.Y))
            .OrderBy(n => ToDrop(n.Pos) > 6 ? 0 : 1).ThenBy(n => Vector2.Distance(n.Pos, home)).FirstOrDefault();
        if (node == null) return null;
        return node.Pos + Vector2.Normalize(home - node.Pos + new Vector2(0.001f, 0)) * 2.5f;
    }

    /// <summary>金と食の資源を、近いものどうしでまとめた中心。</summary>
    public List<(Vector2 center, Res res)> ResourceClusters() =>
        ResourceClustersWithAmount().Select(c => (c.center, c.res)).ToList();

    /// <summary>金と食の資源のまとまり(中心、種類、残量)。</summary>
    public List<(Vector2 center, Res res, float amount)> ResourceClustersWithAmount()
    {
        var clusters = new List<(Vector2 sum, int n, Res res, float amount)>();
        foreach (var n in Nodes.Where(n => n.Amount > 0 && n.Kind != Res.Wood))
        {
            int i = clusters.FindIndex(c => c.res == n.Kind && Vector2.Distance(c.sum / c.n, n.Pos) < 4);
            if (i < 0) clusters.Add((n.Pos, 1, n.Kind, n.Amount));
            else clusters[i] = (clusters[i].sum + n.Pos, clusters[i].n + 1, n.Kind, clusters[i].amount + n.Amount);
        }
        return clusters.Select(c => (c.sum / c.n, c.res, c.amount)).ToList();
    }

    private float NearestNode(Res r, Vector2 p)
    {
        var d = Nodes.Where(n => n.Kind == r && n.Amount > 0).Select(n => Vector2.Distance(n.Pos, p)).DefaultIfEmpty(99).Min();
        return MathF.Round(d);
    }

    private float NearestTree(Vector2 p)
    {
        float best = 99;
        int cx = (int)p.X, cy = (int)p.Y;
        for (int y = Math.Max(0, cy - 20); y < Math.Min(Map.H, cy + 20); y++)
        for (int x = Math.Max(0, cx - 20); x < Math.Min(Map.W, cx + 20); x++)
            if (Map.At(x, y) == Tile.Forest)
                best = MathF.Min(best, Vector2.Distance(p, new Vector2(x + 0.5f, y + 0.5f)));
        return MathF.Round(best);
    }
}
