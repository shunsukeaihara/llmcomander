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
    private Vector2? _dragStart;      // 範囲選択の開始(地図の画面座標)
    private Vector2? _pressAt;        // 左ボタンを押した位置(ウィンドウ上)
    private bool _boxSelecting;       // 左ドラッグで範囲選択している
    private Tween? _camTween;
    private const float DragThreshold = 6;
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
        var setup = LlmSetup.From(config);
        var settings = setup.Settings;
        var speech = setup.Speech;
        if (setup.Llm != null) _cc = new CommandCenter(_world, 0, setup.Llm, speech, settings);
        _llmProblem = setup.Problem;

        // プレイログ: ユーザーデータの logs/ に1試合1ファイル(LLMC_LOG_DIR で変更可)
        var logDir = OS.GetEnvironment("LLMC_LOG_DIR");
        if (logDir == "") logDir = OS.GetUserDataDir() + "/logs";
        _log = PlayLog.Open(logDir, _world);
        _log?.Start(settings);
        if (_cc != null) _cc.Log = _log;
        if (speech == null) _micNote = "  (音声認識なし: OPENAI_API_KEY 未設定)";

        AddChild(new GroundView { World = _world });
        _view = new WorldView { World = _world, Sprites = new Sprites(), Props = new Props(), Selected = _selected };
        AddChild(_view);
        AddChild(new FogView { World = _world });
        _hud = new Hud();
        AddChild(_hud);
        _overlay = new OverlayView { World = _world, Font = _hud.Font, Selected = _selected };
        _view.Font = _hud.Font;
        AddChild(_overlay);
        _voice = new Voice();
        AddChild(_voice);

        _cam = new Camera2D { Zoom = new Vector2(1.25f, 1.25f) };
        AddChild(_cam);
        _cam.Position = Iso.ToScreen(_world.BasePos(0)) + new Vector2(Hud.PanelWidth / 2f / 1.25f, 0);
        _cam.MakeCurrent();

        if (OS.GetEnvironment("LLMC_DEMO") == "1") RunDemo();
        if (OS.GetEnvironment("LLMC_PANEL_CLOSED") == "1") CallDeferred(nameof(ClosePanel));
        // 4K などの大きな画面では、全体を整数倍に拡大する(ドット絵が崩れないように)
        // (拡大中は見かけの画面サイズが変わらず通知が来ないので、毎フレーム確かめる)
        UpdateContentScale();

        // 画面確認用: LLMC_WINDOW="幅x高さ" でウィンドウの大きさを決める
        if (OS.GetEnvironment("LLMC_WINDOW") is { Length: > 0 } win && win.Split('x') is [var ww, var wh])
        {
            DisplayServer.WindowSetSize(new Vector2I(int.Parse(ww), int.Parse(wh)));
            DisplayServer.WindowSetPosition(Vector2I.Zero);
        }
        // 画面確認用: LLMC_CAM="x,y" でカメラをそのタイルに向ける
        if (OS.GetEnvironment("LLMC_CAM") is { Length: > 0 } cam && cam.Split(',') is [var cx, var cy])
            _cam.Position = Iso.ToScreen(float.Parse(cx), float.Parse(cy)) + new Vector2(Hud.PanelWidth / 2f / _cam.Zoom.X, 0);
        _hud.TextSubmitted += t => Submit(t, null);
        _hud.Minimap.World = _world;
        _hud.CardPicked += JumpToCard;
        _hud.AlertPicked += () => { _alertCursor = 0; JumpToAlert(); };
        _hud.Minimap.Picked += (tile, press) =>
        {
            if (_listening)
            {
                // 話しながら指した場所は指示点になる(押した瞬間だけ)
                if (!press) return;
                _clicks.Add(tile);
                _overlay.SpeakingClicks = _clicks.ToList();
                return;
            }
            CenterOn(tile, smooth: !press);
        };
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
        UpdateContentScale();
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
        move += EdgeScroll();
        if (move != Vector2.Zero) _camTween?.Kill();
        _cam.Position += move.Normalized() * (float)delta * 700 / _cam.Zoom.X;

        var vp = GetViewportRect().Size / _cam.Zoom;
        _view.ViewRect = new Rect2(_cam.Position - vp / 2, vp);
        _hud.Minimap.View = _view.ViewRect;

        _hudTimer -= delta;
        if (_hudTimer <= 0)
        {
            _hudTimer = 0.3;
            _selected.RemoveWhere(id => !_world.Units.ContainsKey(id));
            long hp = ViewPerf.Begin();
            _hud.Refresh(_world, _cc, _listening, _micNote, _llmProblem);
            ViewPerf.End("UI更新", hp);
        }
    }

    private int _perfFrames;
    private double _perfTimer;
    private PlayLog? _log;
    private string? _llmProblem;
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

    /// <summary>
    /// ウィンドウに 1600×900 が何個入るかを整数で数え、その倍率で画面全体を拡大する。
    /// 余った分は表示範囲を広げて埋める(4K なら2倍で 1920×1080 相当、1440p なら1倍で広く表示)。
    /// </summary>
    private void UpdateContentScale()
    {
        var size = DisplayServer.WindowGetSize();
        int k = Mathf.Max(1, Mathf.Min(size.X / 1600, size.Y / 900));
        var logical = new Vector2I(size.X / k, size.Y / k);
        if (GetTree().Root.ContentScaleSize != logical) GetTree().Root.ContentScaleSize = logical;
    }

    /// <summary>マウスが画面の端にあれば、その方向にスクロールする(ウィンドウが前面で、マウスが中にあるときだけ)。</summary>
    private Vector2 EdgeScroll()
    {
        const float margin = 6;
        if (!DisplayServer.WindowIsFocused() || _hud.Typing) return Vector2.Zero;
        var size = GetViewportRect().Size;
        var m = GetViewport().GetMousePosition();
        if (m.X < 0 || m.Y < 0 || m.X > size.X || m.Y > size.Y) return Vector2.Zero;
        var dir = Vector2.Zero;
        if (m.X <= margin) dir.X -= 1;
        if (m.X >= size.X - margin) dir.X += 1;
        if (m.Y <= margin) dir.Y -= 1;
        if (m.Y >= size.Y - margin) dir.Y += 1;
        return dir;
    }

    /// <summary>作戦カードを押したら、その作戦の部隊の中心(兵がいなければ持ち場)へ移る。</summary>
    private void JumpToCard(string key)
    {
        var ts = _world.Teams[0];
        if (key == "economy")
        {
            CenterOn(_world.BasePos(0), smooth: true);
            return;
        }
        if (key == "reserve")
        {
            var reserve = _world.Members(ts.Reserve).ToList();
            CenterOn(reserve.Count > 0 ? new SVec(reserve.Average(u => u.Pos.X), reserve.Average(u => u.Pos.Y)) : ts.Rally, smooth: true);
            return;
        }
        if (!key.StartsWith("op") || !int.TryParse(key[2..], out int id)) return;
        var op = ts.Ops.FirstOrDefault(o => o.Id == id);
        if (op == null) return;
        var members = _world.Members(op).ToList();
        if (members.Count > 0)
            CenterOn(new SVec(members.Average(u => u.Pos.X), members.Average(u => u.Pos.Y)), smooth: true);
        else if (op.Roles.FirstOrDefault() is { } r)
            CenterOn(r.Anchor.Center, smooth: true);
    }

    /// <summary>右クリックの直接指示。結果は指示履歴に残す。</summary>
    private void ManualOrder(SVec tile)
    {
        var applier = new Applier(_world, 0);
        string result = applier.ManualOrder(_selected.ToList(), tile);
        if (result == "") return;
        _world.Teams[0].History.Add(new HistoryEntry
        {
            Time = _world.Time, Utterance = "(右クリック)", Result = result, Pending = false,
        });
        _log?.Write("ui", new { action = "manual_order", region = _world.Map.RegionName(tile), selected = _selected.Count, result });
        _overlay.FlashAt = tile;
        _overlay.FlashTime = _world.Time;
    }

    private int _alertCursor;
    private double _lastAlertJump = -999;

    /// <summary>直近の警報の場所へ移る。続けて押すと一つ前の警報へさかのぼる。</summary>
    private void JumpToAlert()
    {
        var alerts = _world.Teams[0].Alerts.Where(a => _world.Time - a.Time < 120).Reverse().ToList();
        if (alerts.Count == 0) return;
        if (_world.Time - _lastAlertJump > 4) _alertCursor = 0; // しばらく押さなければ最新から
        var a = alerts[_alertCursor % alerts.Count];
        _alertCursor++;
        _lastAlertJump = _world.Time;
        CenterOn(a.Pos, smooth: true);
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
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(shot);
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
                Result = $"LLMが使えません: {_llmProblem}。game/.env に書いてください",
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
                    case Key.N: JumpToAlert(); break;
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
                if (_pressAt is { } pa && _tool == Tool.Select && !_listening)
                {
                    var screen = GetViewport().GetMousePosition();
                    // 少し動かしたら範囲選択(動かさずに離せばクリック)
                    if (!_boxSelecting && screen.DistanceTo(pa) > DragThreshold) _boxSelecting = true;
                    if (_boxSelecting && _dragStart is { } ds)
                        _overlay.SelectBox = new Rect2(ds, GetGlobalMousePosition() - ds).Abs();
                }
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
        {
            // 選択していれば直接指示(移動・攻撃・採集・建設・こもり)、していなければ目印を消す
            if (_selected.Count > 0) ManualOrder(MouseTile());
            else RemoveMarkerNear(MouseTile());
        }
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
                    _pressAt = GetViewport().GetMousePosition();
                    _boxSelecting = false;
                    break;
                case Tool.Select when !mb.Pressed && _dragStart is { } ds:
                    if (_boxSelecting) SelectIn(ds, GetGlobalMousePosition(), Input.IsKeyPressed(Key.Shift));
                    else ClickAt(ds, Input.IsKeyPressed(Key.Shift));
                    _dragStart = null;
                    _pressAt = null;
                    _boxSelecting = false;
                    _overlay.SelectBox = null;
                    break;
            }
        }
    }

    /// <summary>
    /// 左クリック: 自軍のユニットの上なら選択(Shift で追加)、何もない地面ならそこを画面の中心にする。
    /// </summary>
    private void ClickAt(Vector2 world, bool add)
    {
        var t = Iso.ToTile(world);
        var u = _world.TeamUnits(0).OrderBy(u => SVec.Distance(u.Pos, t)).FirstOrDefault();
        if (u != null && SVec.Distance(u.Pos, t) < 1.2f)
        {
            if (!add) _selected.Clear();
            _selected.Add(u.Id);
            return;
        }
        // 何もない地面のクリックは選択を外すだけ
        if (!add) _selected.Clear();
    }

    /// <summary>
    /// タイルを、地図が見えている部分(右の作戦パネルを除いた範囲)の中央に持ってくる。
    /// </summary>
    private void CenterOn(SVec tile, bool smooth)
    {
        var vp = GetViewportRect().Size;
        float visibleWidth = vp.X - (_hud.PanelOpen ? Hud.PanelWidth : 0);
        // 画面の中央と、地図が見えている部分の中央のずれ(ウィンドウ上の画素)
        var offset = new Vector2(visibleWidth / 2 - vp.X / 2, 0);
        var target = Iso.ToScreen(tile) - offset / _cam.Zoom;
        _camTween?.Kill();
        if (!smooth)
        {
            _cam.Position = target;
            return;
        }
        _camTween = CreateTween();
        _camTween.TweenProperty(_cam, "position", target, 0.18).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
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
