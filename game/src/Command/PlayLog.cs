using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using LlmCommander.Sim;

namespace LlmCommander.Command;

/// <summary>
/// プレイログ。1試合を1つの JSONL ファイル(1行1件)に記録する。
/// 遊んだあとに「なぜその解釈になったか」を調べるため、LLMに渡した状況と返ってきた命令をそのまま残す。
/// 各行は {"type": 種類, "t": ゲーム内の経過秒, "wall": 実時刻, ...} の形。
/// </summary>
public sealed class PlayLog : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま読めるように
    };

    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly World _world;
    private readonly Dictionary<int, double> _loggedChangeAt = new();
    private int _loggedAlerts;
    private bool _ended;

    public string Path { get; }

    private PlayLog(string path, World world)
    {
        Path = path;
        _world = world;
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    /// <summary>dir に日時の名前でログを作る。作れなければ null(ログなしで遊べる)。</summary>
    public static PlayLog? Open(string dir, World world)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
            return new PlayLog(path, world);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Write(string type, object payload)
    {
        var line = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["t"] = Math.Round(_world.Time, 2),
            ["wall"] = DateTime.Now.ToString("O"),
        };
        foreach (var p in JsonSerializer.SerializeToElement(payload, Json).EnumerateObject())
            line[p.Name] = p.Value;
        string text = JsonSerializer.Serialize(line, Json);
        lock (_lock)
        {
            if (_ended) return;
            _writer.WriteLine(text);
        }
    }

    // ------------------------------------------------------------ 記録する内容

    public void Start(LlmSettings settings)
    {
        Write("start", new
        {
            settings = new
            {
                commander_model = settings.CommanderModel,
                commander_effort = settings.CommanderEffort,
                officer_model = settings.OfficerModel,
                officer_effort = settings.OfficerEffort,
                officer_interval = settings.OfficerInterval,
                officers_enabled = settings.OfficersEnabled,
            },
            commander_system = Tools.CommanderSystem,
            officer_system = Tools.OfficerSystem,
            commander_tools = Tools.CommanderTools.Select(t => t.Name),
        });
    }

    /// <summary>試合の状況(30秒ごとと、試合の終わりに)。</summary>
    public void Snapshot()
    {
        var ts = _world.Teams[0];
        var units = _world.TeamUnits(0).ToList();
        var enemy = _world.TeamUnits(1).ToList();
        Write("snapshot", new
        {
            stock = ts.Stock.ToDictionary(kv => Defs.Key(kv.Key), kv => (int)kv.Value),
            pop = units.Count,
            gather_target = ts.Gather.ToDictionary(kv => Defs.Key(kv.Key), kv => kv.Value),
            gather_actual = _world.GatherCounts(0).ToDictionary(kv => Defs.Key(kv.Key), kv => kv.Value),
            army = Counts(units.Where(u => u.Kind != UnitKind.Villager)),
            villagers = units.Count(u => u.Kind == UnitKind.Villager),
            buildings = _world.TeamBuildings(0).GroupBy(b => Defs.Key(b.Kind)).ToDictionary(g => g.Key, g => g.Count()),
            ops = ts.ActiveOps.Where(o => !o.IsEconomy).Select(o => new
            {
                name = o.Name,
                tag = _world.OpTag(o),
                status = o.Status,
                roles = o.Roles.Select(r => new
                {
                    role = r.Name,
                    count = _world.Members(o, r).Count(),
                    rule = r.Describe(),
                    trigger = r.Trigger?.Text,
                    formation = _world.FormationState(o, r),
                }),
            }),
            enemy_army = Counts(enemy.Where(u => u.Kind != UnitKind.Villager)),
            enemy_villagers = enemy.Count(u => u.Kind == UnitKind.Villager),
            losses = new { own = _lostOwn, enemy = _lostEnemy },
        });
    }

    private static Dictionary<string, int> Counts(IEnumerable<Unit> units) =>
        units.GroupBy(u => Defs.Key(u.Kind)).ToDictionary(g => g.Key, g => g.Count());

    private int _lostOwn, _lostEnemy;
    private HashSet<int> _alive = new();

    /// <summary>毎秒呼ぶ。条件の発動・作戦の変更・警報・損害を出来事として記録する。</summary>
    public void Poll()
    {
        var ts = _world.Teams[0];
        foreach (var op in ts.Ops)
        {
            // 作戦の変更の記録は古いものから消えるので、件数ではなく時刻で新しいものを拾う
            double last = _loggedChangeAt.GetValueOrDefault(op.Id, -1);
            foreach (var c in op.Changes.Where(c => c.Time > last))
            {
                if (c.Text.StartsWith("条件発動") || c.Text.StartsWith("部隊長"))
                    Write("event", new { op = op.Name, text = c.Text });
                last = c.Time;
            }
            _loggedChangeAt[op.Id] = last;
        }
        foreach (var a in ts.Alerts.Skip(_loggedAlerts)) Write("alert", new { text = a.Text });
        _loggedAlerts = ts.Alerts.Count;

        var alive = _world.Units.Keys.ToHashSet();
        foreach (var id in _alive.Where(id => !alive.Contains(id)))
        {
            // 消えたユニットの陣営は前回の記録から判断する
            if (_teamOf.TryGetValue(id, out var team))
            {
                if (team == 0) _lostOwn++;
                else _lostEnemy++;
                _teamOf.Remove(id);
            }
        }
        foreach (var u in _world.Units.Values) _teamOf[u.Id] = u.Team;
        _alive = alive;
    }

    private readonly Dictionary<int, int> _teamOf = new();

    public void End()
    {
        Write("end", new { winner = _world.Winner is { } w ? (w == 0 ? "player" : "enemy") : null });
        Snapshot();
        lock (_lock) _ended = true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _ended = true;
            _writer.Dispose();
        }
    }
}
