using System.Collections.Generic;
using System.Linq;
using Godot;
using LlmCommander.Command;
using LlmCommander.Llm;
using LlmCommander.Sim;
using SVec = System.Numerics.Vector2;

namespace LlmCommander.View;

/// <summary>
/// ゲームの入口。シミュレーションを 20Hz で進め、LLM の結果を毎フレーム取り込み、入力を処理する。
/// 操作: Space 長押しで話す / Enter で文字入力 / 左ドラッグで選択 / 1: 地点の目印 / 2: 線の目印 /
///       右クリック: 目印を消す / WASD・矢印・中ドラッグでスクロール / ホイールでズーム / Tab: 意図表示 / P: 一時停止(デバッグ)
/// </summary>
public partial class Main : Node2D
{
    private World _world = null!;
    private EnemyAI _ai = null!;
    private CommandCenter? _cc;
    private Camera2D _cam = null!;
    private WorldView _view = null!;
    private OverlayView _overlay = null!;
    private Hud _hud = null!;
    private Voice _voice = null!;
    private double _acc;
    private double _hudTimer;
    private bool _paused;

    private enum Tool { Select, Point, Line }
    private Tool _tool = Tool.Select;
    private Vector2? _dragStart;      // 画面上の位置
    private SVec? _lineStart;          // タイル座標
    private bool _panning;
    private readonly HashSet<int> _selected = new();
    private readonly List<SVec> _clicks = new();
    private bool _listening;
    private string _micNote = "";
    private int _markerSeq;

    public override void _Ready()
    {
        // 戦闘や経路探索のコードを先にコンパイルさせるため、裏のスレッドで大規模な戦闘を空回しする
        // (別の World を使うので、本物の試合とは干渉しない)
        if (OS.GetEnvironment("LLMC_NO_WARMUP") != "1")
            System.Threading.Tasks.Task.Run(() => Scenarios.Warmup(60));
        _world = OS.GetEnvironment("LLMC_DEMO") == "battle" ? Scenarios.BigBattle() : MapLayout.CreateValley();
        _ai = new EnemyAI(_world);

        var config = Config.Load(ProjectSettings.GlobalizePath("res://.env"));
        var settings = new LlmSettings
        {
            CommanderModel = config.Get("COMMANDER_MODEL", "claude-opus-5-5"),
            CommanderEffort = config.Get("COMMANDER_EFFORT", "low"),
            OfficerModel = config.Get("OFFICER_MODEL", "claude-haiku-5-5"),
        };
        var anthropicKey = config.Get("ANTHROPIC_API_KEY");
        var openaiKey = config.Get("OPENAI_API_KEY");
        ISpeech? speech = openaiKey != null ? new OpenAiSpeech(openaiKey, config.Get("STT_MODEL", "gpt-4o-transcribe")) : null;
        if (anthropicKey != null) _cc = new CommandCenter(_world, 0, new AnthropicLlm(anthropicKey), speech, settings);

        // プレイログ: ユーザーデータの logs/ に1試合1ファイル(LLMC_LOG_DIR で変更可)
        var logDir = OS.GetEnvironment("LLMC_LOG_DIR");
        if (logDir == "") logDir = OS.GetUserDataDir() + "/logs";
        _log = PlayLog.Open(logDir, _world);
        _log?.Start(settings);
        if (_cc != null) _cc.Log = _log;
        if (speech == null) _micNote = "  (音声認識なし: OPENAI_API_KEY 未設定)";

        AddChild(new GroundView { World = _world });
        _view = new WorldView { World = _world, Sprites = new Sprites(), Selected = _selected };
        AddChild(_view);
        AddChild(new FogView { World = _world });
        _hud = new Hud();
        AddChild(_hud);
        _overlay = new OverlayView { World = _world, Font = _hud.Font, Selected = _selected };
        AddChild(_overlay);
        _voice = new Voice();
        AddChild(_voice);

        _cam = new Camera2D { Zoom = new Vector2(1.25f, 1.25f) };
        AddChild(_cam);
        _cam.Position = Iso.ToScreen(_world.BasePos(0)) + new Vector2(Hud.PanelWidth / 2f / 1.25f, 0);
        _cam.MakeCurrent();

        if (OS.GetEnvironment("LLMC_DEMO") == "1") RunDemo();
        if (OS.GetEnvironment("LLMC_PANEL_CLOSED") == "1") CallDeferred(nameof(ClosePanel));
        _hud.TextSubmitted += t => Submit(t, null);
        _hud.PlanDecision += (id, ok) =>
        {
            var plan = _world.Teams[0].Plans.FirstOrDefault(p => p.Id == id && p.Status == PlanStatus.Proposed);
            if (plan == null) return;
            plan.Status = ok ? PlanStatus.Confirmed : PlanStatus.Cancelled;
            _log?.Write("ui", new { action = ok ? "confirm_build" : "cancel_build", plan = plan.Id, building = Defs.Key(plan.Kind) });
            _world.Teams[0].History.Add(new HistoryEntry
            {
                Time = _world.Time, Utterance = ok ? "(OKボタン)" : "(取消ボタン)", Pending = false,
                Result = $"{Defs.Ja(plan.Kind)}#{plan.Id} を{(ok ? "確定" : "取り消し")}",
            });
        };
    }

    public override void _Process(double delta)
    {
        if (!_paused)
        {
            _acc += delta;
            int steps = 0;
            long perf = ViewPerf.Begin();
            while (_acc >= Defs.Dt && steps++ < 5)
            {
                _acc -= Defs.Dt;
                _world.Step();
                _ai.Step();
            }
            if (steps >= 5) _acc = 0;
            Iso.Alpha = Mathf.Clamp((float)(_acc / Defs.Dt), 0, 1);
            ViewPerf.End("シミュレーション", perf);
        }
        _cc?.Update();
        UpdateLog();
        if (ViewPerf.Enabled)
        {
            _perfFrames++;
            _perfTimer += delta;
            if (_perfTimer >= 5)
            {
                GD.Print($"[perf] {Context.Clock(_world.Time)} ユニット{_world.Units.Count}体 {_perfFrames / _perfTimer:0}fps  {ViewPerf.Flush(_perfFrames)}");
                _perfFrames = 0;
                _perfTimer = 0;
            }
        }

        // カメラのスクロール
        var move = Vector2.Zero;
        if (!_hud.Typing)
        {
            if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) move.X -= 1;
            if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) move.X += 1;
            if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) move.Y -= 1;
            if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) move.Y += 1;
        }
        _cam.Position += move * (float)delta * 700 / _cam.Zoom.X;

        var vp = GetViewportRect().Size / _cam.Zoom;
        _view.ViewRect = new Rect2(_cam.Position - vp / 2, vp);

        _hudTimer -= delta;
        if (_hudTimer <= 0)
        {
            _hudTimer = 0.3;
            _selected.RemoveWhere(id => !_world.Units.ContainsKey(id));
            long hp = ViewPerf.Begin();
            _hud.Refresh(_world, _cc, _listening, _micNote);
            ViewPerf.End("UI更新", hp);
        }
    }

    private int _perfFrames;
    private double _perfTimer;
    private PlayLog? _log;
    private double _nextPoll, _nextSnapshot;
    private bool _logEnded;

    private void UpdateLog()
    {
        if (_log == null || _logEnded) return;
        if (_world.Time >= _nextPoll)
        {
            _nextPoll = _world.Time + 1;
            _log.Poll();
        }
        if (_world.Time >= _nextSnapshot)
        {
            _nextSnapshot = _world.Time + 30;
            _log.Snapshot();
        }
        if (_world.Winner != null)
        {
            _log.Poll();
            _log.End();
            _logEnded = true;
        }
    }

    public override void _ExitTree()
    {
        if (_log != null && !_logEnded) _log.End();
        _log?.Dispose();
    }

    private int _frames;

    private void ClosePanel()
    {
        if (_hud.PanelOpen) _hud.TogglePanel();
    }

    /// <summary>画面確認用: 場面を作って交戦が始まるまで早送りする。</summary>
    private void RunDemo()
    {
        DemoScenario.Setup(_world);
        var op = _world.Teams[0].FindOp("戦線A")!;
        for (int i = 0; i < 20 * 90 && !_world.Members(op).Any(u => _world.Time - u.LastFoughtAt < 1); i++)
        {
            _world.Step();
            _ai.Step();
        }
        for (int i = 0; i < 20 * 3; i++) _world.Step();
        DemoScenario.Retreat(_world);
        for (int i = 0; i < 20 * 3; i++) _world.Step();
        if (OS.GetEnvironment("LLMC_DEMO_SELECT") == "1")
            foreach (var u in _world.Members(op)) _selected.Add(u.Id);
        _cam.Position = Iso.ToScreen(new SVec(30, 44)) + new Vector2(Hud.PanelWidth / 2f / _cam.Zoom.X, 0);
    }

    public override void _PhysicsProcess(double delta)
    {
        // 画面確認用: LLMC_SCREENSHOT にパスがあれば、少し待って画面を保存して終了する
        var shot = OS.GetEnvironment("LLMC_SCREENSHOT");
        if (shot == "" || ++_frames != 40) return;
        GetViewport().GetTexture().GetImage().SavePng(shot);
        GetTree().Quit();
    }

    private SVec MouseTile() => Iso.ToTile(GetGlobalMousePosition());

    private void Submit(string text, byte[]? wav)
    {
        var ctx = new UtteranceContext { Selected = _selected.ToList(), Clicks = _clicks.ToList() };
        _clicks.Clear();
        _overlay.SpeakingClicks = new List<SVec>();
        if (_cc == null)
        {
            _world.Teams[0].History.Add(new HistoryEntry
            {
                Time = _world.Time, Utterance = text != "" ? text : "(音声)", Pending = false, Failed = true,
                Result = "LLMが設定されていません。game/.env に ANTHROPIC_API_KEY を書いてください",
            });
            return;
        }
        if (wav != null) _cc.SubmitAudio(wav, ctx);
        else _cc.SubmitText(text, ctx);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_hud.Typing)
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) _hud.CloseInput();
            return;
        }

        switch (e)
        {
            case InputEventKey { Keycode: Key.Space } k when !k.Echo:
                if (k.Pressed && !_listening)
                {
                    _listening = true;
                    _voice.Start();
                }
                else if (!k.Pressed && _listening)
                {
                    _listening = false;
                    var wav = _voice.Stop();
                    if (wav != null) Submit("", wav);
                    else
                    {
                        _clicks.Clear();
                        _overlay.SpeakingClicks = new List<SVec>();
                    }
                }
                break;
            case InputEventKey { Pressed: true, Echo: false } k:
                switch (k.Keycode)
                {
                    case Key.Enter or Key.KpEnter: _hud.OpenInput(); break;
                    case Key.Key1: _tool = Tool.Point; break;
                    case Key.Key2: _tool = Tool.Line; break;
                    case Key.Escape: _tool = Tool.Select; _selected.Clear(); break;
                    case Key.Tab: _overlay.ShowIntent = !_overlay.ShowIntent; break;
                    case Key.C: _hud.TogglePanel(); break;
                    case Key.F8:
                        // デバッグ用: 霧を切り替える(自軍の視界がすべて見える状態になる)
                        _world.Vision[0].Enabled = !_world.Vision[0].Enabled;
                        break;
                    case Key.F9:
                        // ログのフォルダを開く
                        if (_log != null) OS.ShellOpen(System.IO.Path.GetDirectoryName(_log.Path)!);
                        break;
                    case Key.P: _paused = !_paused; break;
                }
                break;
            case InputEventMouseButton mb:
                OnMouseButton(mb);
                break;
            case InputEventMouseMotion mm:
                if (_panning) _cam.Position -= mm.Relative / _cam.Zoom;
                if (_dragStart is { } ds && _tool == Tool.Select && !_listening)
                    _overlay.SelectBox = new Rect2(ds, GetGlobalMousePosition() - ds).Abs();
                if (_lineStart is { } ls) _overlay.DraftLine = (ls, MouseTile());
                break;
        }
    }

    private void OnMouseButton(InputEventMouseButton mb)
    {
        if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
            _cam.Zoom = (_cam.Zoom * 1.1f).Clamp(new Vector2(0.4f, 0.4f), new Vector2(3, 3));
        else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
            _cam.Zoom = (_cam.Zoom / 1.1f).Clamp(new Vector2(0.4f, 0.4f), new Vector2(3, 3));
        else if (mb.ButtonIndex == MouseButton.Middle)
            _panning = mb.Pressed;
        else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            RemoveMarkerNear(MouseTile());
        else if (mb.ButtonIndex == MouseButton.Left)
        {
            var tile = MouseTile();
            if (_listening)
            {
                // 話しながらのクリックは指示点
                if (mb.Pressed)
                {
                    _clicks.Add(tile);
                    _overlay.SpeakingClicks = _clicks.ToList();
                }
                return;
            }
            switch (_tool)
            {
                case Tool.Point when mb.Pressed:
                    AddMarker(new List<SVec> { tile }, "地点");
                    _tool = Tool.Select;
                    break;
                case Tool.Line when mb.Pressed:
                    _lineStart = tile;
                    break;
                case Tool.Line when !mb.Pressed && _lineStart is { } a:
                    if (SVec.Distance(a, tile) > 1.5f) AddMarker(new List<SVec> { a, tile }, "線");
                    _lineStart = null;
                    _overlay.DraftLine = null;
                    _tool = Tool.Select;
                    break;
                case Tool.Select when mb.Pressed:
                    _dragStart = GetGlobalMousePosition();
                    break;
                case Tool.Select when !mb.Pressed && _dragStart is { } ds:
                    SelectIn(ds, GetGlobalMousePosition(), Input.IsKeyPressed(Key.Shift));
                    _dragStart = null;
                    _overlay.SelectBox = null;
                    break;
            }
        }
    }

    private void AddMarker(List<SVec> points, string prefix)
    {
        var ts = _world.Teams[0];
        string name;
        do name = $"{prefix}{(char)('A' + _markerSeq++ % 26)}";
        while (ts.Markers.Any(m => m.Name == name));
        ts.Markers.Add(new Marker { Name = name, Points = points });
        _log?.Write("ui", new { action = "add_marker", name, region = points.Select(p => _world.Map.RegionName(p)) });
    }

    private void RemoveMarkerNear(SVec tile)
    {
        var ts = _world.Teams[0];
        var m = ts.Markers.OrderBy(m => m.ToPlace().Distance(tile)).FirstOrDefault();
        if (m != null && m.ToPlace().Distance(tile) < 2)
        {
            ts.Markers.Remove(m);
            _log?.Write("ui", new { action = "remove_marker", name = m.Name });
        }
    }

    private void SelectIn(Vector2 a, Vector2 b, bool add)
    {
        if (!add) _selected.Clear();
        var rect = new Rect2(a, b - a).Abs();
        if (rect.Size.Length() < 6)
        {
            // クリック: 一番近い自軍ユニット
            var t = Iso.ToTile(a);
            var u = _world.TeamUnits(0).OrderBy(u => SVec.Distance(u.Pos, t)).FirstOrDefault();
            if (u != null && SVec.Distance(u.Pos, t) < 1.2f) _selected.Add(u.Id);
            return;
        }
        var inBox = _world.TeamUnits(0).Where(u => rect.HasPoint(Iso.ToScreen(u.Pos))).ToList();
        // 兵がいれば村人は選ばない
        if (inBox.Any(u => u.Kind != UnitKind.Villager)) inBox.RemoveAll(u => u.Kind == UnitKind.Villager);
        foreach (var u in inBox) _selected.Add(u.Id);
    }
}
