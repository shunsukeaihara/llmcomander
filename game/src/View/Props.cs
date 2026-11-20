using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace LlmCommander.View;

/// <summary>
/// 木・資源・建物のドット絵(tools/sprites/render_props.py で作ったもの)。
/// props.json に各画像の「地面の基準点」があり、そこを地図上の位置に合わせて描く。画像がなければ null を返す。
/// </summary>
public sealed class Props
{
    private readonly Dictionary<string, (Texture2D tex, Vector2 anchor)> _items = new();

    public Props()
    {
        const string dir = "res://assets/props/";
        if (!FileAccess.FileExists(dir + "props.json")) return;
        using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(dir + "props.json"));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var anchor = new Vector2(p.Value.GetProperty("ax").GetSingle(), p.Value.GetProperty("ay").GetSingle());
            // 建物は陣営ごとの画像、それ以外は1枚
            foreach (var file in new[] { p.Name, p.Name + "_blue", p.Name + "_red" })
            {
                string path = dir + file + ".png";
                if (!FileAccess.FileExists(path)) continue;
                var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(path));
                if (img != null) _items[file] = (ImageTexture.CreateFromImage(img), anchor);
            }
        }
    }

    public int Count => _items.Count;

    /// <summary>name の画像を、地面の基準点が ground に来るように描く。画像がなければ false。</summary>
    public bool Draw(CanvasItem ci, string name, Vector2 ground, Color? modulate = null)
    {
        if (!_items.TryGetValue(name, out var it)) return false;
        ci.DrawTexture(it.tex, ground - it.anchor, modulate ?? Colors.White);
        return true;
    }
}
