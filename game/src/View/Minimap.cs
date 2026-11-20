using System;
using System.Linq;
using Godot;
using LlmCommander.Sim;
using SVec = System.Numerics.Vector2;

namespace LlmCommander.View;

/// <summary>
/// ミニマップ。地図と同じ菱形で、地形と霧(未探索=真っ黒、探索済み=暗い、見えている=明るい)、
/// ユニット・建物・資源・目印・警報と、カメラが映している範囲を描く。
/// クリックやドラッグでその場所を知らせる(Main がカメラを動かすか、話している最中なら指示点にする)。
/// </summary>
public partial class Minimap : Control
{
    /// <summary>タイル1枚あたりの横の画素数(縦はその半分)。</summary>
    public const float TileScale = 1.4f;
    public World? World;
    /// <summary>カメラが映している範囲(地図の画面座標)。</summary>
    public Rect2 View;
    /// <summary>ミニマップ上の場所が指された(タイル座標、押した瞬間なら true)。</summary>
    public event Action<SVec, bool>? Picked;

    private Image? _image;
    private ImageTexture? _texture;
    private int _mapVersion = -1, _visVersion = -1;
    private bool _dragging;

    public static Vector2 SizeFor(int tiles) => new(tiles * TileScale * 2, tiles * TileScale);

    private Transform2D TileToLocal =>
        new(new Vector2(TileScale, TileScale / 2), new Vector2(-TileScale, TileScale / 2), new Vector2(Size.X / 2, 0));

    public override void _Process(double delta) => QueueRedraw();

    private void UpdateTexture()
    {
        var w = World!;
        var vis = w.Vision[0];
        int vv = vis.Enabled ? vis.Version : -2;
        if (w.Map.Version == _mapVersion && vv == _visVersion && _texture != null) return;
        _mapVersion = w.Map.Version;
        _visVersion = vv;
        var map = w.Map;
        _image ??= Image.CreateEmpty(map.W, map.H, false, Image.Format.Rgba8);
        for (int y = 0; y < map.H; y++)
        for (int x = 0; x < map.W; x++)
        {
            var c = map.At(x, y) switch
            {
                Tile.Forest => new Color(0.13f, 0.30f, 0.14f),
                Tile.Water => Palette.Water,
                _ => new Color(0.33f, 0.47f, 0.25f),
            };
            if (!vis.IsExplored(x, y)) c = new Color(0.02f, 0.03f, 0.04f);
            else if (vis.Enabled && !vis.Visible[y * map.W + x]) c = c.Darkened(0.55f);
            _image.SetPixel(x, y, c);
        }
        if (_texture == null) _texture = ImageTexture.CreateFromImage(_image);
        else _texture.Update(_image);
    }

    public override void _Draw()
    {
        if (World == null) return;
        var w = World;
        var vis = w.Vision[0];
        UpdateTexture();
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0, 0, 0, 0.55f));

        DrawSetTransformMatrix(TileToLocal);
        DrawTexture(_texture!, Vector2.Zero);
        // 未探索の真っ黒でもマップの形がわかるよう、外枠を薄く描く
        var edge = new[] { Vector2.Zero, new Vector2(w.Map.W, 0), new Vector2(w.Map.W, w.Map.H), new Vector2(0, w.Map.H), Vector2.Zero };
        DrawPolyline(edge, new Color(Palette.Muted, 0.6f), 0.6f);
        foreach (var n in w.Nodes.Where(n => n.Amount > 0 && vis.IsExplored((int)n.Pos.X, (int)n.Pos.Y)))
            DrawRect(new Rect2(n.Pos.X - 0.6f, n.Pos.Y - 0.6f, 1.2f, 1.2f), n.Kind == Res.Gold ? Palette.Gold : Palette.Berry);
        foreach (var b in w.Buildings.Values)
        {
            bool own = b.Team == 0;
            if (!own && !(vis.Buildings.ContainsKey(b.Id) || !vis.Enabled)) continue;
            DrawRect(new Rect2(b.TileX, b.TileY, b.Size, b.Size), own ? Palette.Team[0] : Palette.Team[1]);
        }
        foreach (var u in w.Units.Values)
        {
            if (u.Team != 0 && !vis.IsVisible(u.Pos)) continue;
            var c = u.Team == 0 ? Palette.Team[0].Lightened(0.3f) : Palette.Team[1].Lightened(0.2f);
            DrawRect(new Rect2(u.Pos.X - 0.7f, u.Pos.Y - 0.7f, 1.4f, 1.4f), c);
        }
        foreach (var m in w.Teams[0].Markers)
        {
            if (m.Points.Count >= 2)
                DrawLine(new Vector2(m.Points[0].X, m.Points[0].Y), new Vector2(m.Points[^1].X, m.Points[^1].Y), Palette.Overlay, 0.8f);
            else
                DrawRect(new Rect2(m.Points[0].X - 0.8f, m.Points[0].Y - 0.8f, 1.6f, 1.6f), Palette.Overlay, false, 0.5f);
        }
        DrawSetTransformMatrix(Transform2D.Identity);

        // 警報の点滅
        foreach (var a in w.Teams[0].Alerts.Where(a => w.Time - a.Time < 6))
        {
            float t = (float)((w.Time - a.Time) * 2 % 1);
            DrawArc(TileToLocal * new Vector2(a.Pos.X, a.Pos.Y), 4 + t * 8, 0, Mathf.Tau, 16, new Color(Palette.Alert, 1 - t), 1.5f);
        }

        // カメラが映している範囲(画面の四隅をタイル座標にして、ミニマップに描く)
        var corners = new[] { View.Position, new Vector2(View.End.X, View.Position.Y), View.End, new Vector2(View.Position.X, View.End.Y) }
            .Select(p => { var t = Iso.ToTile(p); return TileToLocal * new Vector2(t.X, t.Y); }).ToArray();
        for (int i = 0; i < 4; i++) DrawLine(corners[i], corners[(i + 1) % 4], Palette.Overlay, 1);
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                _dragging = mb.Pressed;
                if (mb.Pressed) Pick(mb.Position, true);
                AcceptEvent();
                break;
            case InputEventMouseMotion mm when _dragging:
                Pick(mm.Position, false);
                AcceptEvent();
                break;
        }
    }

    private void Pick(Vector2 local, bool press)
    {
        if (World == null) return;
        var t = TileToLocal.AffineInverse() * local;
        var tile = new SVec(Math.Clamp(t.X, 0, World.Map.W - 0.01f), Math.Clamp(t.Y, 0, World.Map.H - 0.01f));
        Picked?.Invoke(tile, press);
    }
}
