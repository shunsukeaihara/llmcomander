using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace LlmCommander.Sim;

/// <summary>
/// LLM が書く場所の表現を読む。生の座標は使わせず、名前の付いた場所からの相対で書かせる。
/// <list type="bullet">
/// <item>名前: 目印・地名・own_base・rally・enemy_base(発見後)・「槍の位置」「戦線A/弓の位置」「現在地」</item>
/// <item>「AからBへ3マス」「AからBへ少し」: A から B の方向へ動かした点(B は場所か方角)</item>
/// <item>「Aの北5マス」「Aの敵側4マス」: A から方角へ動かした点</item>
/// <item>「AとBの間」: 中間点</item>
/// <item>「A〜B」「AからBまでの線」: 線(横隊を敷く場所)</item>
/// </list>
/// 方角は画面の向き(北=上、東=右)。「敵側/前」は自軍基地から地図の中央へ向かう向き、「自陣側/後ろ」はその逆。
/// </summary>
public static class PlaceExpr
{
    private static readonly Regex Between = new(@"^(?<a>.+?)と(?<b>.+?)の(間|中間|あいだ)$");
    private static readonly Regex LineTo = new(@"^(?<a>.+?)(〜|~|から)(?<b>.+?)(までの線|を結ぶ線|の線)$|^(?<a>.+?)(〜|~)(?<b>.+)$");
    private static readonly Regex Toward = new(
        @"^(?<a>.+?)から(?<b>.+?)(の方向|方向|の方|側|寄り)?(へ|に)(?<n>[0-9０-９.]+マス|少し|ちょっと|半分)(ほど|くらい|ぐらい)?$");
    private static readonly Regex Offset = new(@"^(?<a>.+?)の(?<d>北東|北西|南東|南西|北|南|東|西|敵側|前方|前|自陣側|後方|後ろ)(へ|に)?(?<n>[0-9０-９.]+)マス$");

    public static Place? Resolve(World w, int team, string text, Operation? self = null)
    {
        string t = text.Trim().Replace(" ", "").Replace("　", "");
        if (t == "") return null;
        if (w.ResolveBasePlace(team, t, self) is { } basic) return basic;
        // 前に解決した場所の名前には「(地名)」が付いている。外して読み直す
        t = Regex.Replace(t, @"([（(][^）)]*[）)])+$", "");
        text = t;
        if (w.ResolveBasePlace(team, t, self) is { } basic2) return basic2;

        var m = Between.Match(t);
        if (m.Success && Point(w, team, m.Groups["a"].Value, self) is { } a1 && Point(w, team, m.Groups["b"].Value, self) is { } b1)
            return Make(w, text, (a1 + b1) / 2);

        m = Toward.Match(t);
        if (m.Success && Point(w, team, m.Groups["a"].Value, self) is { } a2)
        {
            string b = m.Groups["b"].Value;
            Vector2? dir = Direction(w, team, b);
            Vector2? target = null;
            if (dir == null && Point(w, team, b, self) is { } bp)
            {
                target = bp;
                var d = bp - a2;
                dir = d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : Vector2.Zero;
            }
            if (dir != null)
            {
                string n = m.Groups["n"].Value;
                float dist = n switch
                {
                    "少し" or "ちょっと" => 3,
                    "半分" => target is { } tg ? Vector2.Distance(a2, tg) / 2 : 3,
                    _ => ParseTiles(n),
                };
                // 目標の場所を通り越さない
                if (target is { } tg2) dist = MathF.Min(dist, Vector2.Distance(a2, tg2));
                return Make(w, text, a2 + dir.Value * dist);
            }
        }

        m = Offset.Match(t);
        if (m.Success && Point(w, team, m.Groups["a"].Value, self) is { } a3 && Direction(w, team, m.Groups["d"].Value) is { } d3)
            return Make(w, text, a3 + d3 * ParseTiles(m.Groups["n"].Value));

        m = LineTo.Match(t);
        if (m.Success && Point(w, team, m.Groups["a"].Value, self) is { } a4 && Point(w, team, m.Groups["b"].Value, self) is { } b4
            && Vector2.Distance(a4, b4) > 1)
            return new Place(text.Trim(), new[] { w.Map.NearestPassable(a4), w.Map.NearestPassable(b4) });
        return null;
    }

    private static Vector2? Point(World w, int team, string name, Operation? self) => Resolve(w, team, name, self)?.Center;

    private static Place Make(World w, string text, Vector2 p)
    {
        var c = new Vector2(Math.Clamp(p.X, 1, w.Map.W - 2), Math.Clamp(p.Y, 1, w.Map.H - 2));
        c = w.Map.NearestPassable(c);
        return new Place($"{text.Trim()}({w.Map.RegionName(c)})", new[] { c });
    }

    private static float ParseTiles(string s)
    {
        s = s.Replace("マス", "").Normalize(System.Text.NormalizationForm.FormKC);
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 60) : 3;
    }

    /// <summary>方角の単位ベクトル(タイル座標)。画面の上が北。</summary>
    public static Vector2? Direction(World w, int team, string word)
    {
        static Vector2 N(float x, float y) => Vector2.Normalize(new Vector2(x, y));
        var forward = w.Map.W / 2f - w.BasePos(team).X is var dx && w.Map.H / 2f - w.BasePos(team).Y is var dy
            ? N(dx, dy) : Vector2.UnitX;
        return word switch
        {
            "北" or "上" => N(-1, -1),
            "南" or "下" => N(1, 1),
            "東" or "右" => N(1, -1),
            "西" or "左" => N(-1, 1),
            "北東" or "右上" => new Vector2(0, -1),
            "北西" or "左上" => new Vector2(-1, 0),
            "南東" or "右下" => new Vector2(1, 0),
            "南西" or "左下" => new Vector2(0, 1),
            "敵側" or "前" or "前方" => forward,
            "自陣側" or "後ろ" or "後方" => -forward,
            _ => null,
        };
    }
}
