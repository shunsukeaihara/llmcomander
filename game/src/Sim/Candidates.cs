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
    float FromEnemyBase,
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
                if (tcs.Any(t => Vector2.Distance(t, center) < 15)) continue;
                if (Vector2.Distance(center, BasePos(1 - team)) < 15) continue; // 敵の基地の資源は候補にしない
                var toward = Vector2.Normalize(home - center + new Vector2(0.001f, 0));
                points.Add((center + toward * 4.5f, $"{Defs.Ja(res)}の資源のそば"));
            }
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
                Vector2.Distance(c, home), Vector2.Distance(c, BasePos(1 - team)), enemies));
        }
        return result.OrderBy(r => r.FromOwnBase).Take(kind == BuildingKind.TownCenter ? 5 : 4).ToList();
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
