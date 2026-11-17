using System;
using System.Collections.Generic;
using System.Numerics;

namespace LlmCommander.Sim;

public enum Tile : byte { Grass, Forest, Water }

/// <summary>名前の付いた地域。LLMとプレイヤーが共有する地名。</summary>
public sealed record Region(string Key, string Name, Vector2 Center, float Radius);

/// <summary>
/// タイルマップと地名、経路探索。座標はタイル単位で、(x, y) のタイルの中心は (x + 0.5, y + 0.5)。
/// 画面上は x - y が右方向、x + y が下方向になる(アイソメトリック)。
/// </summary>
public sealed class GameMap
{
    public readonly int W;
    public readonly int H;
    public readonly Tile[] Tiles;
    public readonly float[] Wood;
    /// <summary>建物が占有しているタイル(建物の Id、なければ 0)。</summary>
    public readonly int[] Occupant;
    public readonly List<Region> Regions = new();
    public int Version;

    public GameMap(int w, int h)
    {
        W = w;
        H = h;
        Tiles = new Tile[w * h];
        Wood = new float[w * h];
        Occupant = new int[w * h];
    }

    public bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
    public Tile At(int x, int y) => Tiles[y * W + x];

    public bool Passable(int x, int y) =>
        In(x, y) && Tiles[y * W + x] == Tile.Grass && Occupant[y * W + x] == 0;

    public bool Passable(Vector2 p) => Passable((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

    public void SetForest(int x, int y, float wood = 100f)
    {
        if (!In(x, y)) return;
        Tiles[y * W + x] = Tile.Forest;
        Wood[y * W + x] = wood;
    }

    /// <summary>木を切る。切り尽くしたら草地に戻る。</summary>
    public float Chop(int x, int y, float amount)
    {
        int i = y * W + x;
        if (Tiles[i] != Tile.Forest) return 0;
        float got = MathF.Min(amount, Wood[i]);
        Wood[i] -= got;
        if (Wood[i] <= 0.01f)
        {
            Tiles[i] = Tile.Grass;
            Version++;
        }
        return got;
    }

    public Region? FindRegion(string keyOrName)
    {
        string s = keyOrName.Trim();
        foreach (var r in Regions)
            if (string.Equals(r.Key, s, StringComparison.OrdinalIgnoreCase) || r.Name == s)
                return r;
        return null;
    }

    /// <summary>座標がどの地域にあるか(一番近い地域の名前)。</summary>
    public string RegionName(Vector2 p)
    {
        Region? best = null;
        float bestD = float.MaxValue;
        foreach (var r in Regions)
        {
            float d = Vector2.Distance(p, r.Center) / r.Radius;
            if (d < bestD) { bestD = d; best = r; }
        }
        return best?.Name ?? "不明";
    }

    /// <summary>p に一番近い通行可能なタイルの中心。</summary>
    public Vector2 NearestPassable(Vector2 p, int maxR = 12)
    {
        int cx = (int)MathF.Floor(p.X), cy = (int)MathF.Floor(p.Y);
        if (Passable(cx, cy)) return p;
        for (int r = 1; r <= maxR; r++)
        {
            Vector2? best = null;
            float bestD = float.MaxValue;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Passable(cx + dx, cy + dy)) continue;
                var c = new Vector2(cx + dx + 0.5f, cy + dy + 0.5f);
                float d = Vector2.DistanceSquared(c, p);
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best is { } b) return b;
        }
        return p;
    }

    // ------------------------------------------------------------ 経路探索(8近傍 A*)

    private int[]? _gScore, _came, _stamp;
    private int _search;

    /// <summary>from から to への経路(タイル中心の列)。到達不能なら to に一番近づける位置まで。</summary>
    public List<Vector2> FindPath(Vector2 from, Vector2 to, int maxNodes = 6000)
    {
        int n = W * H;
        _gScore ??= new int[n];
        _came ??= new int[n];
        _stamp ??= new int[n];
        _search++;

        int sx = Math.Clamp((int)from.X, 0, W - 1), sy = Math.Clamp((int)from.Y, 0, H - 1);
        var goal = NearestPassable(to);
        int gx = Math.Clamp((int)goal.X, 0, W - 1), gy = Math.Clamp((int)goal.Y, 0, H - 1);
        int start = sy * W + sx, target = gy * W + gx;

        var open = new PriorityQueue<int, int>();
        _gScore[start] = 0;
        _came[start] = -1;
        _stamp[start] = _search;
        open.Enqueue(start, 0);
        int best = start, bestH = Heuristic(sx, sy, gx, gy), expanded = 0;

        while (open.Count > 0 && expanded++ < maxNodes)
        {
            int cur = open.Dequeue();
            if (cur == target) { best = cur; break; }
            int cx = cur % W, cy = cur / W;
            int h = Heuristic(cx, cy, gx, gy);
            if (h < bestH) { bestH = h; best = cur; }
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = cx + dx, ny = cy + dy;
                if (!Passable(nx, ny)) continue;
                // 斜め移動で角をすり抜けない
                if (dx != 0 && dy != 0 && (!Passable(cx + dx, cy) || !Passable(cx, cy + dy))) continue;
                int ni = ny * W + nx;
                int g = _gScore[cur] + (dx != 0 && dy != 0 ? 14 : 10);
                if (_stamp[ni] == _search && g >= _gScore[ni]) continue;
                _stamp[ni] = _search;
                _gScore[ni] = g;
                _came[ni] = cur;
                open.Enqueue(ni, g + Heuristic(nx, ny, gx, gy));
            }
        }

        var path = new List<Vector2>();
        for (int c = best; c != -1 && c != start; c = _came[c])
            path.Add(new Vector2(c % W + 0.5f, c / W + 0.5f));
        path.Reverse();
        if (best == target && path.Count > 0) path[^1] = goal; // 最後はタイル中心ではなく目的地そのもの
        return Smooth(from, path);
    }

    private static int Heuristic(int x, int y, int gx, int gy)
    {
        int dx = Math.Abs(x - gx), dy = Math.Abs(y - gy);
        return 10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy);
    }

    /// <summary>見通しの通る中間点を飛ばして、ジグザグを減らす。</summary>
    private List<Vector2> Smooth(Vector2 from, List<Vector2> path)
    {
        if (path.Count < 3) return path;
        var result = new List<Vector2>();
        var cur = from;
        int i = 0;
        while (i < path.Count)
        {
            int j = path.Count - 1;
            while (j > i && !LineClear(cur, path[j])) j--;
            result.Add(path[j]);
            cur = path[j];
            i = j + 1;
        }
        return result;
    }

    public bool LineClear(Vector2 a, Vector2 b)
    {
        float d = Vector2.Distance(a, b);
        int steps = (int)(d / 0.3f) + 1;
        for (int s = 1; s <= steps; s++)
        {
            var p = Vector2.Lerp(a, b, s / (float)steps);
            if (!Passable(p)) return false;
            // ユニットの幅ぶん、左右も確認する
            var dir = Vector2.Normalize(b - a);
            var side = new Vector2(-dir.Y, dir.X) * 0.3f;
            if (!Passable(p + side) || !Passable(p - side)) return false;
        }
        return true;
    }
}
