using Godot;
using LlmCommander.Sim;

namespace LlmCommander.View;

/// <summary>
/// 霧の層。タイル1枚を1画素にした小さな画像を、アイソメトリックの形に変形して地図に重ねる。
/// まだ見ていない場所は暗く、一度見たが今は見えていない場所は薄暗くする。画素の間は補間されて境目がぼける。
/// </summary>
public partial class FogView : Node2D
{
    public World? World;
    private Image? _image;
    private ImageTexture? _texture;
    private int _version = -1;
    private static readonly Color Unexplored = new(0.02f, 0.03f, 0.04f, 0.97f);
    private static readonly Color Remembered = new(0.02f, 0.03f, 0.05f, 0.5f);

    public override void _Ready()
    {
        // タイル (x, y) → 画面 ((x - y) * 32, (x + y) * 16) と同じ変形
        Transform = new Transform2D(new Vector2(Iso.HalfW, Iso.HalfH), new Vector2(-Iso.HalfW, Iso.HalfH), Vector2.Zero);
        TextureFilter = TextureFilterEnum.Linear;
    }

    public override void _Process(double delta)
    {
        if (World == null) return;
        var vis = World.Vision[0];
        int v = vis.Enabled ? vis.Version : -2;
        if (v == _version) return;
        _version = v;
        var map = World.Map;
        _image ??= Image.CreateEmpty(map.W, map.H, false, Image.Format.Rgba8);
        for (int y = 0; y < map.H; y++)
        for (int x = 0; x < map.W; x++)
        {
            var c = !vis.Enabled || vis.Visible[y * map.W + x] ? Colors.Transparent
                : vis.Explored[y * map.W + x] ? Remembered : Unexplored;
            _image.SetPixel(x, y, c);
        }
        if (_texture == null) _texture = ImageTexture.CreateFromImage(_image);
        else _texture.Update(_image);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_texture != null) DrawTexture(_texture, Vector2.Zero);
    }
}
