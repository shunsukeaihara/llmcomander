using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>敵を最後に見た記録。</summary>
public sealed record Sighting(int Id, UnitKind Kind, Vector2 Pos, double Time);

/// <summary>敵の建物を見た記録(壊れたのを見るまで覚えておく)。</summary>
public sealed record SeenBuilding(int Id, BuildingKind Kind, int TileX, int TileY, bool Done);

/// <summary>
/// チームごとの視界(霧)。ユニットと建物の周りだけが見えている。
/// 一度見たタイルは「探索済み」として残り、敵の建物はそこで見た姿を覚えておく。
/// </summary>
public sealed class Visibility
{
    public const float UnitSight = 7f;
    public const float BuildingSight = 8f;
    public const double ForgetAfter = 240; // 最後に見てからこの秒数が過ぎた目撃は忘れる

    private readonly GameMap _map;
    /// <summary>今見えているタイル。</summary>
    public readonly bool[] Visible;
    /// <summary>一度でも見たタイル。</summary>
    public readonly bool[] Explored;
    public readonly Dictionary<int, Sighting> LastSeen = new();
    public readonly Dictionary<int, SeenBuilding> Buildings = new();
    /// <summary>見え方が変わるたびに増える(描画の更新用)。</summary>
    public int Version;
    /// <summary>false にすると全部見える(デバッグ用)。</summary>
    public bool Enabled = true;

    public Visibility(GameMap map)
    {
        _map = map;
        Visible = new bool[map.W * map.H];
        Explored = new bool[map.W * map.H];
    }

    public bool IsVisible(Vector2 p)
    {
        if (!Enabled) return true;
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y);
        return _map.In(x, y) && Visible[y * _map.W + x];
    }

    public bool IsExplored(int x, int y) => !Enabled || (_map.In(x, y) && Explored[y * _map.W + x]);

    private void Reveal(Vector2 c, float r)
    {
        int x0 = Math.Max(0, (int)(c.X - r)), x1 = Math.Min(_map.W - 1, (int)(c.X + r));
        int y0 = Math.Max(0, (int)(c.Y - r)), y1 = Math.Min(_map.H - 1, (int)(c.Y + r));
        float r2 = r * r;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float dx = x + 0.5f - c.X, dy = y + 0.5f - c.Y;
            if (dx * dx + dy * dy > r2) continue;
            int i = y * _map.W + x;
            Visible[i] = true;
            Explored[i] = true;
        }
    }

    /// <summary>見えている範囲を計算し直し、敵の目撃を記録する。</summary>
    public void Update(World w, int team)
    {
        Array.Clear(Visible);
        foreach (var u in w.Units.Values)
            if (u.Team == team && u.Alive) Reveal(u.Pos, Defs.SightOf(u.Kind));
        foreach (var b in w.Buildings.Values)
            if (b.Team == team && b.Alive) Reveal(b.Pos, BuildingSight + b.Radius);

        foreach (var u in w.Units.Values)
            if (u.Team != team && u.Alive && IsVisible(u.Pos))
                LastSeen[u.Id] = new Sighting(u.Id, u.Kind, u.Pos, w.Time);
        // 目撃した場所が今見えているのにいなければ、もうそこにはいない
        foreach (var s in LastSeen.Values.ToList())
        {
            bool gone = !w.Units.TryGetValue(s.Id, out var u) || !u.Alive;
            bool moved = !gone && IsVisible(s.Pos) && !IsVisible(u!.Pos);
            if (gone && IsVisible(s.Pos) || moved || w.Time - s.Time > ForgetAfter)
                LastSeen.Remove(s.Id);
        }

        foreach (var b in w.Buildings.Values)
            if (b.Team != team && b.Alive && AnyTileVisible(b))
                Buildings[b.Id] = new SeenBuilding(b.Id, b.Kind, b.TileX, b.TileY, b.Done);
        foreach (var sb in Buildings.Values.ToList())
        {
            var center = new Vector2(sb.TileX + 1.5f, sb.TileY + 1.5f);
            if (IsVisible(center) && (!w.Buildings.TryGetValue(sb.Id, out var b) || !b.Alive))
                Buildings.Remove(sb.Id);
        }
        Version++;
    }

    /// <summary>建物の敷地のどこかが見えているか。</summary>
    public bool AnyTileVisible(Building b)
    {
        for (int y = b.TileY; y < b.TileY + b.Size; y++)
        for (int x = b.TileX; x < b.TileX + b.Size; x++)
            if (!Enabled || _map.In(x, y) && Visible[y * _map.W + x]) return true;
        return false;
    }

    /// <summary>今は見えていないが、最後に見た敵兵(古い順ではなく新しい順)。</summary>
    public IEnumerable<Sighting> Remembered(World w) =>
        LastSeen.Values.Where(s => !(w.Units.TryGetValue(s.Id, out var u) && u.Alive && IsVisible(u.Pos)))
            .OrderByDescending(s => s.Time);
}
