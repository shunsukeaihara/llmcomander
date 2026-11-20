using System.Collections.Generic;
using System.Linq;
using Godot;
using LlmCommander.Sim;

namespace LlmCommander.View;

/// <summary>木・資源・建物・ユニットを奥から順に描く。</summary>
public partial class WorldView : Node2D
{
    public World? World;
    public Sprites? Sprites;
    public Props? Props;
    private static readonly string[] TreeNames =
        { "tree_broad_0", "tree_pine_0", "tree_broad_1", "tree_pine_1", "tree_broad_2", "tree_pine_2" };
    public HashSet<int> Selected = new();
    public Rect2 ViewRect;

    private readonly List<(float depth, int kind, int id, int x, int y)> _items = new();

    public override void _Process(double delta) => QueueRedraw();

    private static float Hash(int x, int y)
    {
        uint h = (uint)(x * 92837111 + y * 689287499);
        h = (h ^ (h >> 15)) * 2246822519;
        return (h & 0xffff) / 65535f;
    }

    private bool OnScreen(Vector2 s, float margin = 80) => ViewRect.Grow(margin).HasPoint(s);

    public override void _Draw()
    {
        long perf = ViewPerf.Begin();
        DrawWorld();
        ViewPerf.End("地図の描画", perf);
    }

    private void DrawWorld()
    {
        if (World == null) return;
        var w = World;
        _items.Clear();

        // 見えている範囲のタイルだけ調べる
        var corners = new[] { ViewRect.Position, ViewRect.End, new Vector2(ViewRect.Position.X, ViewRect.End.Y), new Vector2(ViewRect.End.X, ViewRect.Position.Y) }
            .Select(Iso.ToTile).ToList();
        int x0 = System.Math.Max(0, (int)corners.Min(c => c.X) - 2), x1 = System.Math.Min(w.Map.W - 1, (int)corners.Max(c => c.X) + 2);
        int y0 = System.Math.Max(0, (int)corners.Min(c => c.Y) - 2), y1 = System.Math.Min(w.Map.H - 1, (int)corners.Max(c => c.Y) + 2);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            if (w.Map.At(x, y) == Tile.Forest)
                _items.Add((x + y + 1f, 0, 0, x, y));
        foreach (var n in w.Nodes.Where(n => n.Amount > 0))
            _items.Add((n.Pos.X + n.Pos.Y, 1, n.Id, 0, 0));
        var vis = w.Vision[0];
        foreach (var b in w.Buildings.Values)
            // 敵の建物は、一度見たものだけ描く
            if (b.Team == 0 || !vis.Enabled || vis.Buildings.ContainsKey(b.Id))
                _items.Add((b.TileX + b.TileY + b.Size * 2 - 1, 2, b.Id, 0, 0));
        foreach (var p in w.Teams[0].Plans.Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Confirmed))
            _items.Add((p.TileX + p.TileY + Defs.Buildings[p.Kind].Size * 2 - 1, 4, p.Id, 0, 0));
        foreach (var u in w.Units.Values)
        {
            if (u.Team != 0 && !vis.IsVisible(u.Pos)) continue; // 霧の中の敵兵は描かない
            var rp = u.RenderPos(Iso.Alpha);
            _items.Add((rp.X + rp.Y, 3, u.Id, 0, 0));
        }
        _items.Sort((a, b) => a.depth.CompareTo(b.depth));

        foreach (var it in _items)
        {
            switch (it.kind)
            {
                case 0: DrawTree(it.x, it.y); break;
                case 1: DrawNode(w.Nodes.First(n => n.Id == it.id)); break;
                case 2: DrawBuilding(w.Buildings[it.id], false); break;
                case 3: DrawUnit(w.Units[it.id]); break;
                case 4: DrawPlan(w.Teams[0].Plans.First(p => p.Id == it.id)); break;
            }
        }

        foreach (var s in w.Shots)
        {
            if (!vis.IsVisible(s.From) && !vis.IsVisible(s.To)) continue;
            var a = Iso.ToScreen(s.From) + new Vector2(0, -10);
            var b = Iso.ToScreen(s.To) + new Vector2(0, -8);
            float t = (float)((w.Time - s.Time) / 0.4);
            if (s.Arrow)
            {
                var p = a.Lerp(b, Mathf.Clamp(t * 1.6f, 0, 1)) + new Vector2(0, -12 * Mathf.Sin(Mathf.Pi * Mathf.Clamp(t * 1.6f, 0, 1)));
                DrawLine(p, p + (b - a).Normalized() * 6, new Color(0.95f, 0.9f, 0.75f), 1.5f);
            }
        }
    }

    private void DrawTree(int x, int y)
    {
        var c = Iso.ToScreen(x + 0.5f, y + 0.5f);
        if (!OnScreen(c, 140)) return;
        if (Props != null && Props.Draw(this, TreeNames[(int)(Hash(x, y) * TreeNames.Length) % TreeNames.Length], c)) return;
        float h = Hash(x, y);
        float size = 13 + h * 6;
        var top = c + new Vector2((h - 0.5f) * 8, -size - 10);
        DrawRect(new Rect2(c.X - 2, c.Y - 12, 4, 12), Palette.Trunk);
        DrawCircle(top + new Vector2(0, 4), size * 0.8f, Palette.Canopy);
        DrawCircle(top + new Vector2(-3, 0), size * 0.55f, Palette.CanopyLight.Lerp(Palette.Canopy, h));
    }

    private void DrawNode(ResourceNode n)
    {
        var c = Iso.ToScreen(n.Pos);
        if (!OnScreen(c)) return;
        if (Props != null && Props.Draw(this, n.Kind == Res.Gold ? "gold" : "berry", c)) return;
        if (n.Kind == Res.Gold)
        {
            var pts = new[] { c + new Vector2(-14, 0), c + new Vector2(-8, -14), c + new Vector2(4, -18), c + new Vector2(14, -6), c + new Vector2(10, 4), c + new Vector2(-6, 6) };
            DrawColoredPolygon(pts, Palette.Gold.Darkened(0.25f));
            DrawColoredPolygon(new[] { c + new Vector2(-8, -14), c + new Vector2(4, -18), c + new Vector2(2, -6), c + new Vector2(-6, -4) }, Palette.Gold.Lightened(0.2f));
        }
        else
        {
            DrawCircle(c + new Vector2(0, -8), 11, new Color(0.18f, 0.42f, 0.2f));
            for (int i = 0; i < 5; i++)
                DrawCircle(c + new Vector2(Mathf.Cos(i * 1.3f) * 6, -8 + Mathf.Sin(i * 1.3f) * 5), 2.2f, Palette.Berry);
        }
    }

    /// <summary>建物は直方体と屋根で描く。</summary>
    private void DrawBox(int tx, int ty, int size, float height, Color roof, float alpha, float progress)
    {
        var wall = Palette.Wall;
        wall.A = alpha;
        var wallDark = Palette.WallDark;
        wallDark.A = alpha;
        roof.A = alpha;
        float inset = 0.15f;
        float x = tx + inset, y = ty + inset, s = size - inset * 2;
        float hh = height * progress;
        var up = new Vector2(0, -hh);
        var p0 = Iso.ToScreen(x, y);
        var p1 = Iso.ToScreen(x + s, y);
        var p2 = Iso.ToScreen(x + s, y + s);
        var p3 = Iso.ToScreen(x, y + s);
        DrawColoredPolygon(new[] { p3, p2, p2 + up, p3 + up }, wallDark); // 左手前の壁
        DrawColoredPolygon(new[] { p1, p2, p2 + up, p1 + up }, wall);     // 右手前の壁
        var ridge = new Vector2(0, -hh - 18 * progress);
        var mid = (p0 + p2) / 2 + up;
        DrawColoredPolygon(new[] { p0 + up, p1 + up, mid + ridge, }, roof.Darkened(0.15f));
        DrawColoredPolygon(new[] { p1 + up, p2 + up, mid + ridge }, roof);
        DrawColoredPolygon(new[] { p2 + up, p3 + up, mid + ridge }, roof.Darkened(0.3f));
        DrawColoredPolygon(new[] { p3 + up, p0 + up, mid + ridge }, roof.Darkened(0.1f));
    }

    private static string BuildingSprite(BuildingKind k, int team) =>
        $"{(k == BuildingKind.TownCenter ? "town_center" : "barracks")}_{(team == 0 ? "blue" : "red")}";

    private void DrawBuilding(Building b, bool ghost)
    {
        if (Props != null && DrawBuildingSprite(b)) return;
        var c = Iso.ToScreen(b.Pos);
        if (!OnScreen(c, 140)) return;
        float height = b.Kind == BuildingKind.TownCenter ? 34 : 24;
        var roof = b.Kind == BuildingKind.TownCenter ? Palette.Team[b.Team] : Palette.Team[b.Team].Darkened(0.2f);
        if (!b.Done)
        {
            DrawColoredPolygon(Iso.Diamond(b.TileX, b.TileY, b.Size, b.Size), new Color(0.55f, 0.45f, 0.3f, 0.6f));
            DrawBox(b.TileX, b.TileY, b.Size, height, roof, 0.85f, Mathf.Max(0.15f, b.Progress));
            DrawBar(c + new Vector2(0, -height - 30), b.Progress, Palette.Accent);
        }
        else DrawBox(b.TileX, b.TileY, b.Size, height, roof, 1, 1);
        if (b.Hp < b.MaxHp && b.Done) DrawBar(c + new Vector2(0, -height - 30), b.Hp / b.MaxHp, Palette.Good);
    }

    /// <summary>建物のドット絵。建設中は半透明にして進み具合の棒を出す。</summary>
    private bool DrawBuildingSprite(Building b)
    {
        var c = Iso.ToScreen(b.Pos);
        if (!OnScreen(c, 200)) return true;
        var mod = b.Done ? Colors.White : new Color(1, 1, 1, 0.35f + 0.5f * b.Progress);
        if (!Props!.Draw(this, BuildingSprite(b.Kind, b.Team), c, mod)) return false;
        if (!b.Done) DrawBar(c + new Vector2(0, -70), b.Progress, Palette.Accent);
        else if (b.Hp < b.MaxHp) DrawBar(c + new Vector2(0, -70), b.Hp / b.MaxHp, b.Team == 0 ? Palette.Good : Palette.Alert);
        return true;
    }

    private void DrawPlan(BuildPlan p)
    {
        int size = Defs.Buildings[p.Kind].Size;
        var col = p.Status == PlanStatus.Proposed ? Palette.Accent : Palette.Good;
        var fill = col;
        fill.A = 0.18f;
        var poly = Iso.Diamond(p.TileX, p.TileY, size, size);
        DrawColoredPolygon(poly, fill);
        for (int i = 0; i < 4; i++) DrawDashedLine(poly[i], poly[(i + 1) % 4], col, 2, 6);
        // 仮置きは建物の絵を薄く重ねる(絵がなければ箱)
        if (Props == null || !Props.Draw(this, BuildingSprite(p.Kind, 0), Iso.ToScreen(p.Center), new Color(0.75f, 0.9f, 1f, 0.45f)))
            DrawBox(p.TileX, p.TileY, size, p.Kind == BuildingKind.TownCenter ? 34 : 24, Palette.Team[0], 0.35f, 1);
    }

    private void DrawBar(Vector2 center, float frac, Color color)
    {
        var r = new Rect2(center.X - 16, center.Y, 32, 4);
        DrawRect(r, new Color(0, 0, 0, 0.6f));
        DrawRect(new Rect2(r.Position, new Vector2(32 * Mathf.Clamp(frac, 0, 1), 4)), color);
    }

    private void DrawUnit(Unit u)
    {
        var s = Iso.ToScreen(u.RenderPos(Iso.Alpha));
        if (!OnScreen(s)) return;
        if (Selected.Contains(u.Id))
            DrawArc(s, 10, 0, Mathf.Tau, 20, Palette.Overlay, 1.5f);
        if (Sprites != null && Sprites.Has(u.Kind, u.Team))
            Sprites.Draw(this, u, s, (float)World!.Time);
        else
            DrawPlaceholder(u, s);
        if (u.Hp < u.MaxHp)
            DrawBar(s + new Vector2(0, -34), u.Hp / u.MaxHp, u.Team == 0 ? Palette.Good : Palette.Alert);
    }

    /// <summary>スプライトがない兵種の仮の絵。</summary>
    private void DrawPlaceholder(Unit u, Vector2 s)
    {
        var team = Palette.Team[u.Team];
        var dark = new Color(0.1f, 0.08f, 0.06f);
        DrawCircle(s, 5, new Color(0, 0, 0, 0.25f));
        switch (u.Kind)
        {
            case UnitKind.Villager:
                DrawRect(new Rect2(s.X - 3, s.Y - 14, 6, 10), new Color(0.55f, 0.42f, 0.28f));
                DrawRect(new Rect2(s.X - 3, s.Y - 10, 6, 3), team);
                DrawCircle(s + new Vector2(0, -17), 3, new Color(0.85f, 0.65f, 0.5f));
                break;
            case UnitKind.Cavalry:
                DrawRect(new Rect2(s.X - 9, s.Y - 12, 18, 7), new Color(0.45f, 0.3f, 0.2f));
                DrawRect(new Rect2(s.X - 3, s.Y - 22, 6, 10), team);
                DrawCircle(s + new Vector2(0, -25), 3, new Color(0.85f, 0.65f, 0.5f));
                DrawLine(s + new Vector2(-8, -5), s + new Vector2(-8, 0), dark, 2);
                DrawLine(s + new Vector2(8, -5), s + new Vector2(8, 0), dark, 2);
                break;
            default:
                DrawRect(new Rect2(s.X - 4, s.Y - 16, 8, 12), team);
                DrawCircle(s + new Vector2(0, -19), 3.5f, new Color(0.85f, 0.65f, 0.5f));
                if (u.Kind == UnitKind.Archer)
                    DrawArc(s + new Vector2(5, -12), 6, -1.2f, 1.2f, 8, new Color(0.5f, 0.35f, 0.2f), 1.5f);
                break;
        }
    }
}
