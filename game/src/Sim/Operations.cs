using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace LlmCommander.Sim;

/// <summary>
/// Patrol: 場所の周りを、決めた半径で回り続ける(遠巻きの偵察など)。
/// </summary>
public enum Stance { Hold, Advance, Retreat, Patrol }

public enum Formation { Cluster, Line }

/// <summary>場所。点(1点)か線(2点)。</summary>
public sealed record Place(string Name, IReadOnlyList<Vector2> Points)
{
    public Vector2 Center => Points.Count == 1 ? Points[0] : (Points[0] + Points[^1]) / 2;
    public bool IsLine => Points.Count >= 2;

    public float Distance(Vector2 p)
    {
        if (!IsLine) return Vector2.Distance(p, Points[0]);
        var a = Points[0];
        var b = Points[^1];
        var ab = b - a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / MathF.Max(ab.LengthSquared(), 1e-4f), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
}

/// <summary>交戦規則の部分的な更新。null の項目は変更しない。</summary>
public sealed class RulePatch
{
    public Stance? Stance;
    public Place? Anchor;
    public List<UnitKind>? Priority;
    public bool? Kite;
    public Formation? Formation;
    public float? Radius;
    public bool? Evade;

    public bool IsEmpty => Stance is null && Anchor is null && Priority is null && Kite is null && Formation is null &&
                           Radius is null && Evade is null;
}

public enum ConditionKind { EnemyNear, RoleEngaged, RoleHpBelow }

public sealed class Trigger
{
    public ConditionKind Kind;
    public Place? Place;
    public float Radius = 5;
    public string Role = "";
    public float Fraction = 0.5f;
    public RulePatch Then = new();
    public string Text = "";
}

/// <summary>作戦の中の役割(「弓」「槍」など)。役割ごとに交戦規則を持つ。</summary>
public sealed class Role
{
    public string Name = "";
    public UnitKind? Kind;
    public Stance Stance = Stance.Hold;
    public Place Anchor;
    public List<UnitKind> Priority = new();
    public bool Kite;
    public Formation Formation = Formation.Cluster;
    public Trigger? Trigger;
    /// <summary>巡回の半径(タイル)。null なら既定。</summary>
    public float? Radius;
    /// <summary>戦わない: 敵兵が近づいたり攻撃されたりしたら逃げる。</summary>
    public bool Evade;
    /// <summary>この敵兵を狙い続ける(右クリックで敵兵を指したとき)。倒すか見失うまで追う。</summary>
    public int FocusTarget;
    /// <summary>今の行き先に全員が着いたか(行き先が変わると false に戻る)。</summary>
    public bool Arrived;
    public double ArrivedAt;

    /// <summary>直前の更新で置き換えられた内容(取り消し線で見せる)。</summary>
    public string Superseded = "";
    public double ChangedAt = -999;
    public int InitialCount;

    public Role(string name, Place anchor)
    {
        Name = name;
        Anchor = anchor;
    }

    /// <summary>偵察任務の役割の説明: 行き先と、移動中か到着済みか。</summary>
    public string DescribeScouting() => $"行き先: {Anchor.Name}({(Arrived ? "到着、次を検討中" : "移動中")})・交戦せず逃げる";

    public string Describe()
    {
        var parts = new List<string>
        {
            Stance switch
            {
                Stance.Hold => $"{Anchor.Name}で{(Formation == Formation.Line ? "横隊で" : "")}待機・迎撃",
                Stance.Advance => $"{Anchor.Name}へ進撃",
                Stance.Retreat => $"撤退 → {Anchor.Name}",
                Stance.Patrol => $"{Anchor.Name}の周りを半径{Radius ?? 10:0}で巡回",
                _ => "",
            },
        };
        if (Kite) parts.Add("引き撃ち");
        if (Evade && Stance != Stance.Patrol) parts.Add("交戦せず逃げる");
        if (Priority.Count > 0) parts.Add("優先: " + string.Join("・", Priority.Select(Defs.Ja)));
        return string.Join(" ・ ", parts);
    }

    /// <summary>更新を当てる。変わった項目の旧内容を Superseded に残す。</summary>
    public bool Apply(RulePatch p, double now)
    {
        var old = new List<string>();
        if (p.Stance is { } s && s != Stance || p.Anchor is { } a && a.Name != Anchor.Name)
            old.Add(Stance switch
            {
                Stance.Hold => $"{Anchor.Name}で待機",
                Stance.Advance => $"{Anchor.Name}へ進撃",
                Stance.Patrol => $"{Anchor.Name}の周りを巡回",
                _ => $"撤退 → {Anchor.Name}",
            });
        if (p.Kite is { } k && k != Kite) old.Add(Kite ? "引き撃ち" : "引き撃ちなし");
        if (p.Priority is { } pr && !pr.SequenceEqual(Priority))
            old.Add(Priority.Count > 0 ? "優先: " + string.Join("・", Priority.Select(Defs.Ja)) : "優先なし");
        if (p.Formation is { } f && f != Formation) old.Add(Formation == Formation.Line ? "横隊" : "密集");
        if (p.Evade is { } e && e != Evade) old.Add(Evade ? "交戦せず逃げる" : "交戦する");
        if (p.Radius is { } rad && rad != Radius && p.Stance is null) old.Add(Radius is { } r0 ? $"半径{r0:0}" : "半径なし");

        if (p.Anchor is { } na && na.Center != Anchor.Center) Arrived = false;
        if (p.Stance is { } s2) Stance = s2;
        if (p.Radius is { } r2) Radius = r2;
        if (p.Evade is { } e2) Evade = e2;
        if (p.Anchor is { } a2) Anchor = a2;
        if (p.Kite is { } k2) Kite = k2;
        if (p.Priority is { } pr2) Priority = pr2.ToList();
        if (p.Formation is { } f2) Formation = f2;
        if (old.Count == 0) return false;
        Superseded = string.Join(" ・ ", old);
        ChangedAt = now;
        return true;
    }
}

public sealed record OpChange(double Time, string Text);

/// <summary>作戦。一つのカードとして表示され、軍事の作戦ごとに担当LLMが付く。</summary>
public sealed class Operation
{
    public int Id;
    public int Team;
    public string Name = "";
    public bool IsEconomy;
    public bool IsReserve;
    /// <summary>右クリックの直接指示でできた作戦(担当LLMは付けない。兵がいなくなったら消える)。</summary>
    public bool IsManual;
    /// <summary>偵察任務。行き先は偵察の担当LLMが決める(遅ければゲームが自動で決める)。</summary>
    public bool IsScouting;
    /// <summary>偵察任務の目的(プレイヤーの言葉)と、探索の中心。</summary>
    public string ScoutObjective = "";
    public System.Numerics.Vector2 ScoutCenter;
    public readonly List<Role> Roles = new();
    public readonly List<OpChange> Changes = new();
    /// <summary>プレイヤーがこの作戦に対して言ったこと(担当LLMが意図を汲むため)。</summary>
    public readonly List<string> Intents = new();
    /// <summary>
    /// 作戦の目的(司令役がプレイヤーの言葉で書く)。規則の部品に収まらない意図も、担当LLMがこれを見て実現する。
    /// </summary>
    public string Goal = "";
    /// <summary>プレイヤーの指示で作戦が変わるたびに増える。担当LLMの古い判断を捨てるのに使う。</summary>
    public int IntentVersion;
    /// <summary>担当LLMの直前の命令で通らなかったこと(次の呼び出しで伝える)。</summary>
    public string OfficerProblems = "";
    /// <summary>担当LLMが書く状況の要約。</summary>
    public string Status = "";
    public double StatusAt = -999;
    public double CreatedAt;
    public bool Disbanded;

    // 担当LLMを呼ぶ時期の管理
    public double OfficerDue;
    public bool OfficerBusy;
    public bool Eventful;

    public Role? FindRole(string name) =>
        Roles.FirstOrDefault(r => r.Name == name) ??
        (Defs.TryParseUnit(name, out var k) ? Roles.FirstOrDefault(r => r.Kind == k) : null);

    public void Log(double now, string text)
    {
        Changes.Add(new OpChange(now, text));
        if (Changes.Count > 20) Changes.RemoveAt(0);
    }
}

public enum PlanStatus { Proposed, Confirmed, Building, Done, Cancelled }

public sealed class BuildPlan
{
    public int Id;
    public BuildingKind Kind;
    public int TileX, TileY;
    public PlanStatus Status;
    public string Near = "";
    public int BuildingId;
    public Vector2 Center => new(TileX + Defs.Buildings[Kind].Size / 2f, TileY + Defs.Buildings[Kind].Size / 2f);
}

public sealed class Marker
{
    public string Name = "";
    public List<Vector2> Points = new();
    public Place ToPlace() => new(Name, Points.ToArray());
}

public sealed class HistoryEntry
{
    public double Time;
    public string Utterance = "";
    public string Result = "";
    public double SttSeconds;
    public double LlmSeconds;
    /// <summary>送信から最初の命令がゲームに反映されるまでの秒数(0 は未反映)。</summary>
    public double FirstActionSeconds;
    public bool Pending = true;
    public bool Failed;
}

public sealed record Alert(double Time, Vector2 Pos, string Text);

public sealed class TeamState
{
    public int Team;
    public readonly Dictionary<Res, float> Stock = new() { [Res.Food] = 200, [Res.Wood] = 200, [Res.Gold] = 100 };
    /// <summary>資源ごとの村人の割り当て目標。</summary>
    public readonly Dictionary<Res, int> Gather = new() { [Res.Food] = 6, [Res.Wood] = 0, [Res.Gold] = 0 };
    public bool VillagersContinuous = true;
    public int VillagerTarget = 40;
    /// <summary>兵種ごとの目標数。-1 は上限まで生産し続ける、0 は生産しない。</summary>
    public readonly Dictionary<UnitKind, int> TrainGoal = new()
    {
        [UnitKind.Spearman] = 0,
        [UnitKind.Archer] = 0,
        [UnitKind.Cavalry] = 0,
        [UnitKind.Scout] = 0,
    };
    public int BuildersPerPlan = 2;
    public readonly List<BuildPlan> Plans = new();
    public readonly List<Marker> Markers = new();
    public readonly List<Operation> Ops = new();
    public readonly List<HistoryEntry> History = new();
    public readonly List<Alert> Alerts = new();
    public int ReinforceOp;
    public string ReinforceRole = "";
    /// <summary>新兵の集合場所(予備の部隊が待つ場所)。</summary>
    public Vector2 Rally;
    public string RallyName = "集合地点";
    public string EconomyNote = "";
    public double EconomyNoteAt = -999;
    public int Version;

    public Operation Economy => Ops.First(o => o.IsEconomy);
    public Operation Reserve => Ops.First(o => o.IsReserve);
    public IEnumerable<Operation> ActiveOps => Ops.Where(o => !o.Disbanded);

    public Operation? FindOp(string name) =>
        ActiveOps.FirstOrDefault(o => o.Name == name.Trim()) ??
        (name.Trim() is "予備" or "reserve" ? Reserve : name.Trim() is "経済" or "economy" ? Economy : null);
}
