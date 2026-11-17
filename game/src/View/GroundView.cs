using Godot;
using LlmCommander.Sim;

namespace LlmCommander.View;

/// <summary>地面のタイル。木が切られて地形が変わったときだけ描き直す。</summary>
public partial class GroundView : Node2D
{
    public World? World;
    private int _version = -1;

    public override void _Process(double delta)
    {
        if (World != null && World.Map.Version != _version) QueueRedraw();
    }

    private static float Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h & 0xffff) / 65535f;
    }

    public override void _Draw()
    {
        if (World == null) return;
        var map = World.Map;
        _version = map.Version;
        for (int y = 0; y < map.H; y++)
        for (int x = 0; x < map.W; x++)
        {
            float n = Hash(x, y);
            var c = map.At(x, y) switch
            {
                Tile.Water => Palette.Water.Lerp(new Color(0.25f, 0.5f, 0.7f), n * 0.3f),
                Tile.Forest => Palette.ForestFloor.Lerp(Palette.GrassAlt, n * 0.25f),
                _ => Palette.Grass.Lerp(Palette.GrassAlt, n),
            };
            // 丘は少し明るく
            foreach (var r in map.Regions)
                if (r.Key.EndsWith("_hill") && System.Numerics.Vector2.Distance(r.Center, new System.Numerics.Vector2(x + 0.5f, y + 0.5f)) < r.Radius)
                    c = c.Lightened(0.08f * (1 - System.Numerics.Vector2.Distance(r.Center, new System.Numerics.Vector2(x + 0.5f, y + 0.5f)) / r.Radius));
            DrawColoredPolygon(Iso.Diamond(x, y), c);
        }
    }
}
