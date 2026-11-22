using System.Collections.Generic;
using System.Linq;
using Godot;
using LlmCommander.Sim;
using SVec = System.Numerics.Vector2;

namespace LlmCommander.View;

/// <summary>地図に重ねる「意図」: 目印、部隊のバッジ、予定の移動、条件付きの動き、警報、選択範囲。</summary>
public partial class OverlayView : Node2D
{
    public World? World;
    public Font? Font;
    public bool ShowIntent = true;
    /// <summary>選択中のユニット。その作戦だけ地図上に詳しい内容を出す。</summary>
    public HashSet<int> Selected = new();
    public Rect2? SelectBox;
    public List<SVec> SpeakingClicks = new();
    public (SVec a, SVec b)? DraftLine;
    /// <summary>右クリックで指示した場所(少しの間だけ印を出す)。</summary>
    public SVec FlashAt;
    public double FlashTime = -999;

    public override void _Process(double delta) => QueueRedraw();

    private void Label(Vector2 pos, string text, Color fg, Color bg, int size = 12, bool center = true)
    {
        if (Font == null) return;
        var sz = Font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        var tl = center ? pos - new Vector2(sz.X / 2 + 6, sz.Y / 2 + 2) : pos;
        DrawRect(new Rect2(tl, sz + new Vector2(12, 4)), bg);
        DrawString(Font, tl + new Vector2(6, sz.Y - 2), text, HorizontalAlignment.Left, -1, size, fg);
    }

    private void Arrow(Vector2 a, Vector2 b, Color c, float width, bool dashed)
    {
        if (a.DistanceTo(b) < 8) return;
        if (dashed) DrawDashedLine(a, b, c, width, 7);
        else DrawLine(a, b, c, width, true);
        var dir = (b - a).Normalized();
        var side = new Vector2(-dir.Y, dir.X);
        DrawColoredPolygon(new[] { b, b - dir * 10 + side * 5, b - dir * 10 - side * 5 }, c);
    }

    /// <summary>複数行のラベルを、下端が anchor に来るように描く。</summary>
    private void DrawBlock(Vector2 anchor, List<(string text, Color color, bool strike)> lines, Color bg)
    {
        if (Font == null) return;
        const int size = 11;
        float lh = 15;
        float width = lines.Max(l => Font.GetStringSize(l.text, HorizontalAlignment.Left, -1, size).X) + 14;
        var tl = anchor - new Vector2(width / 2, lh * lines.Count + 6);
        DrawRect(new Rect2(tl, new Vector2(width, lh * lines.Count + 6)), bg);
        DrawRect(new Rect2(tl, new Vector2(3, lh * lines.Count + 6)), Palette.Accent);
        for (int i = 0; i < lines.Count; i++)
        {
            var p = tl + new Vector2(8, 3 + lh * (i + 1) - 3);
            DrawString(Font, p, lines[i].text, HorizontalAlignment.Left, -1, size, lines[i].color);
            if (lines[i].strike)
            {
                float tw = Font.GetStringSize(lines[i].text.TrimStart(), HorizontalAlignment.Left, -1, size).X;
                float indent = Font.GetStringSize("    ", HorizontalAlignment.Left, -1, size).X;
                DrawLine(p + new Vector2(indent, -4), p + new Vector2(indent + tw, -4), Palette.Muted, 1);
            }
        }
    }

    public override void _Draw()
    {
        long perf = ViewPerf.Begin();
        DrawOverlay();
        ViewPerf.End("意図表示", perf);
    }

    private void DrawOverlay()
    {
        if (World == null) return;
        var w = World;
        var ts = w.Teams[0];
        var bg = new Color(0.07f, 0.1f, 0.13f, 0.88f);

        foreach (var m in ts.Markers)
        {
            var pts = m.Points.Select(Iso.ToScreen).ToList();
            if (pts.Count >= 2)
            {
                DrawDashedLine(pts[0], pts[^1], Palette.Overlay, 2.5f, 9);
                Label((pts[0] + pts[^1]) / 2 + new Vector2(0, -14), m.Name, new Color(0.05f, 0.07f, 0.09f), Palette.Overlay, 12);
            }
            else
            {
                DrawArc(pts[0], 9, 0, Mathf.Tau, 20, Palette.Overlay, 2.5f);
                Label(pts[0] + new Vector2(0, 18), m.Name, new Color(0.05f, 0.07f, 0.09f), Palette.Overlay, 12);
            }
        }
        if (DraftLine is { } dl) DrawDashedLine(Iso.ToScreen(dl.a), Iso.ToScreen(dl.b), Palette.Accent, 2.5f, 9);

        if (ShowIntent)
        {
            foreach (var op in ts.ActiveOps.Where(o => !o.IsEconomy && !o.IsReserve))
            {
                var all = w.Members(op).ToList();
                if (all.Count == 0) continue;
                // 役割ごとの矢印(予定の移動と、条件付きの動き)
                foreach (var role in op.Roles)
                {
                    var members = all.Where(u => u.Role == role.Name).ToList();
                    if (members.Count == 0) continue;
                    var c = new SVec(members.Average(u => u.RenderPos(Iso.Alpha).X), members.Average(u => u.RenderPos(Iso.Alpha).Y));
                    var sc = Iso.ToScreen(c);
                    if (role.Stance != Stance.Hold && SVec.Distance(c, role.Anchor.Center) > 4)
                        Arrow(sc, Iso.ToScreen(role.Anchor.Center), Palette.Accent, 2.5f, false);
                    if (role.Trigger is { Then.Anchor: { } ta })
                        Arrow(sc, Iso.ToScreen(ta.Center), Palette.Muted, 2f, true);
                }
                // 地図には作戦名と状態だけの小さな札。詳しい内容は右パネルと、選択したときだけ
                var oc = new SVec(all.Average(u => u.RenderPos(Iso.Alpha).X), all.Average(u => u.RenderPos(Iso.Alpha).Y));
                if (!all.Any(u => Selected.Contains(u.Id)))
                {
                    Label(Iso.ToScreen(oc) + new Vector2(0, -46), $"{op.Name} {w.OpTag(op)}", Palette.Overlay, bg, 10);
                    continue;
                }
                var lines = new List<(string text, Color color, bool strike)> { ($"{op.Name}  {w.OpTag(op)}", Palette.Overlay, false) };
                foreach (var role in op.Roles)
                {
                    int n = all.Count(u => u.Role == role.Name);
                    if (n == 0) continue;
                    lines.Add(($"{role.Name}×{n}  {role.Describe()}{(role.Trigger != null ? "  [条件待ち]" : "")}", new Color(0.77f, 0.82f, 0.86f), false));
                    if (role.Superseded != "" && w.Time - role.ChangedAt < 20)
                        lines.Add(($"    {role.Superseded}", Palette.Muted, true));
                }
                DrawBlock(Iso.ToScreen(oc) + new Vector2(0, -60), lines, bg);
            }

            foreach (var p in ts.Plans.Where(p => p.Status == PlanStatus.Proposed))
                Label(Iso.ToScreen(p.Center) + new Vector2(0, 30), $"#{p.Id} 確認待ち", Palette.Overlay, bg, 10);
        }

        foreach (var a in ts.Alerts.Where(a => w.Time - a.Time < 5))
        {
            var s = Iso.ToScreen(a.Pos);
            float t = (float)(w.Time - a.Time);
            DrawArc(s, 18 + (t * 20 % 30), 0, Mathf.Tau, 28, new Color(Palette.Alert, 1 - (t * 20 % 30) / 30), 2);
            Label(s + new Vector2(0, -36), a.Text, Palette.Overlay, new Color(0.23f, 0.09f, 0.09f, 0.85f), 10);
        }

        for (int i = 0; i < SpeakingClicks.Count; i++)
        {
            var s = Iso.ToScreen(SpeakingClicks[i]);
            DrawArc(s, 10, 0, Mathf.Tau, 20, Palette.Warn, 2.5f);
            Label(s + new Vector2(0, 20), $"指示点{i + 1}", new Color(0.05f, 0.07f, 0.09f), Palette.Warn, 11);
        }

        if (w.Time - FlashTime < 0.6)
        {
            float t = (float)((w.Time - FlashTime) / 0.6);
            var c = Iso.ToScreen(FlashAt);
            DrawArc(c, 6 + 14 * t, 0, Mathf.Tau, 20, new Color(Palette.Good, 1 - t), 2);
        }

        if (SelectBox is { } box)
        {
            DrawRect(box, new Color(Palette.Accent, 0.12f));
            DrawRect(box, Palette.Accent, false, 1.5f);
        }
    }
}
