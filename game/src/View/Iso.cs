using Godot;
using SVec = System.Numerics.Vector2;

namespace LlmCommander.View;

/// <summary>タイル座標と画面座標の変換。タイルは 64×32 ピクセルの菱形。</summary>
public static class Iso
{
    public const float HalfW = 32f;
    public const float HalfH = 16f;

    public static Vector2 ToScreen(float x, float y) => new((x - y) * HalfW, (x + y) * HalfH);
    public static Vector2 ToScreen(SVec p) => ToScreen(p.X, p.Y);

    public static SVec ToTile(Vector2 s)
    {
        float a = s.X / HalfW, b = s.Y / HalfH;
        return new SVec((a + b) / 2, (b - a) / 2);
    }

    /// <summary>タイル平面での向きを、8方向のスプライトの行(e, se, s, sw, w, nw, n, ne)にする。</summary>
    public static int DirIndex(SVec facing)
    {
        var s = ToScreen(facing);
        float ang = Mathf.RadToDeg(Mathf.Atan2(s.Y, s.X));
        int i = Mathf.RoundToInt(ang / 45f);
        return ((i % 8) + 8) % 8;
    }

    public static Vector2[] Diamond(float x, float y, float w = 1, float h = 1) => new[]
    {
        ToScreen(x, y), ToScreen(x + w, y), ToScreen(x + w, y + h), ToScreen(x, y + h),
    };
}
