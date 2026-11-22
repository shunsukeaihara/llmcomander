using System.Collections.Generic;
using Godot;
using LlmCommander.Sim;

namespace LlmCommander.View;

/// <summary>
/// tools/sprites で作ったスプライトシート。行が8方向(e, se, s, sw, w, nw, n, ne)、
/// 列が待機2・歩き4・攻撃4コマ。シートがない兵種は簡易な図形で描く。
/// </summary>
public sealed class Sprites
{
    public const int Frame = 48;
    /// <summary>コマの中で足元が来る位置。</summary>
    public static readonly Vector2 Foot = new(24, 35);
    private static readonly string[] TeamFile = { "blue", "red" };
    private readonly Dictionary<(UnitKind, int), Texture2D> _sheets = new();

    public Sprites()
    {
        foreach (var kind in new[] { UnitKind.Villager, UnitKind.Spearman, UnitKind.Archer, UnitKind.Cavalry, UnitKind.Scout })
        for (int team = 0; team < 2; team++)
        {
            string path = $"res://assets/sprites/{Defs.Key(kind)}_{TeamFile[team]}.png";
            if (!FileAccess.FileExists(path)) continue;
            // インポート設定に依存しないよう、実行時に PNG を読む
            var img = Image.LoadFromFile(ProjectSettings.GlobalizePath(path));
            if (img != null) _sheets[(kind, team)] = ImageTexture.CreateFromImage(img);
        }
    }

    public bool Has(UnitKind kind, int team) => _sheets.ContainsKey((kind, team));

    public void Draw(CanvasItem ci, Unit u, Vector2 screen, float time)
    {
        if (!_sheets.TryGetValue((u.Kind, u.Team), out var tex)) return;
        int row = Iso.DirIndex(u.Facing);
        int col = u.Anim switch
        {
            Anim.Walk => 2 + (int)(u.AnimTime / 0.15f) % 4,
            Anim.Attack => 6 + System.Math.Min(3, (int)(u.AnimTime / 0.18f)),
            _ => (int)(time * 1.5f + u.Id * 0.37f) % 2,
        };
        var src = new Rect2(col * Frame, row * Frame, Frame, Frame);
        ci.DrawTextureRectRegion(tex, new Rect2(screen - Foot, new Vector2(Frame, Frame)), src);
    }
}
