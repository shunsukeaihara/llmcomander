using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using LlmCommander.Command;
using LlmCommander.Sim;

namespace LlmCommander.View;

/// <summary>画面のUI: 上部バー、稼働中の作戦カード、指示履歴、音声バー。</summary>
public partial class Hud : CanvasLayer
{
    public const int PanelWidth = 460;
    public event Action<int, bool>? PlanDecision;
    public event Action<string>? TextSubmitted;
    /// <summary>作戦カードが押された(カードの種類: "op{作戦Id}" / "economy" / "reserve")。</summary>
    public event Action<string>? CardPicked;
    /// <summary>上部バーの警報が押された。</summary>
    public event Action? AlertPicked;
    private Button _alert = null!;

    private Label _resources = null!;
    private Label _clock = null!;
    private Label _llmState = null!;
    private VBoxContainer _cards = null!;
    private VBoxContainer _history = null!;
    private Label _voiceState = null!;
    private Label _voiceLast = null!;
    private Label _voiceResult = null!;
    private LineEdit _input = null!;
    private Label _banner = null!;
    private PanelContainer _right = null!;
    private PanelContainer _voice = null!;
    private PanelContainer _minimapFrame = null!;
    public Minimap Minimap { get; private set; } = null!;
    private Button _toggle = null!;

    /// <summary>右パネルが開いているか。</summary>
    public bool PanelOpen => _right.Visible;

    /// <summary>右パネル(作戦カードと指示履歴)の開閉。閉じると音声バーを画面幅いっぱいに広げる。</summary>
    public void TogglePanel()
    {
        _right.Visible = !_right.Visible;
        _voice.OffsetRight = _right.Visible ? -PanelWidth - 16 : -16;
        PlaceMinimap();
        _toggle.Text = _right.Visible ? "作戦パネルを閉じる (C)" : "作戦パネルを開く (C)";
    }
    private Font _font = null!;

    public bool Typing => _input.HasFocus();

    public override void _Ready()
    {
        _font = new SystemFont
        {
            FontNames = new[] { "Noto Sans CJK JP", "Noto Sans JP", "Yu Gothic UI", "Meiryo", "Hiragino Sans", "IPAexGothic", "IPAGothic", "sans-serif" },
        };
        var theme = new Theme { DefaultFont = _font, DefaultFontSize = 13 };
        var root = new Control { Theme = theme, MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        // 上部バー
        var top = Panel(new Color(0.07f, 0.1f, 0.13f, 0.97f));
        top.SetAnchorsPreset(Control.LayoutPreset.TopWide);
        top.OffsetBottom = 40;
        root.AddChild(top);
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 24);
        top.AddChild(topRow);
        topRow.AddChild(new Label { Text = "LLM Commander", Modulate = Palette.Overlay });
        _resources = new Label();
        topRow.AddChild(_resources);
        topRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        _llmState = new Label { Modulate = Palette.Muted };
        // 直近の警報。押すとその場所へ(N キーでも)
        _alert = new Button { Flat = true, Visible = false, FocusMode = Control.FocusModeEnum.None };
        _alert.AddThemeColorOverride("font_color", Palette.Alert);
        _alert.Pressed += () => AlertPicked?.Invoke();
        topRow.AddChild(_alert);
        topRow.AddChild(_llmState);
        _toggle = new Button { Text = "作戦パネルを閉じる (C)", Flat = true, FocusMode = Control.FocusModeEnum.None };
        _toggle.Pressed += TogglePanel;
        topRow.AddChild(_toggle);
        _clock = new Label();
        topRow.AddChild(_clock);

        // 右パネル
        var right = _right = Panel(new Color(0.05f, 0.07f, 0.09f, 0.97f));
        right.AnchorLeft = 1;
        right.AnchorRight = 1;
        right.AnchorBottom = 1;
        right.OffsetLeft = -PanelWidth;
        right.OffsetTop = 40;
        root.AddChild(right);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        right.AddChild(scroll);
        var rightBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightBox.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(rightBox);
        rightBox.AddChild(Heading("稼働中の作戦"));
        _cards = new VBoxContainer();
        _cards.AddThemeConstantOverride("separation", 8);
        rightBox.AddChild(_cards);
        rightBox.AddChild(Heading("指示履歴"));
        _history = new VBoxContainer();
        _history.AddThemeConstantOverride("separation", 6);
        rightBox.AddChild(_history);

        // 音声バー
        var voice = _voice = Panel(new Color(0.07f, 0.1f, 0.13f, 0.82f));
        voice.AnchorTop = 1;
        voice.AnchorBottom = 1;
        voice.AnchorRight = 1;
        voice.OffsetLeft = 16;
        voice.OffsetRight = -PanelWidth - 16;
        voice.OffsetTop = -66;
        voice.OffsetBottom = -12;
        root.AddChild(voice);
        var vbox = new VBoxContainer();
        voice.AddChild(vbox);
        // 2行だけ: 操作の案内(小さく)と、直前の発話と結果(1行に収める)
        _voiceState = new Label { Modulate = Palette.Muted };
        _voiceState.AddThemeFontSizeOverride("font_size", 11);
        _voiceLast = new Label { TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, ClipText = true };
        _voiceLast.AddThemeFontSizeOverride("font_size", 14);
        _voiceResult = new Label { Visible = false };
        _input = new LineEdit { PlaceholderText = "Enter で文字入力(音声の代わり)。Esc でキャンセル", Visible = false };
        _input.TextSubmitted += t =>
        {
            _input.Text = "";
            _input.Visible = false;
            _input.ReleaseFocus();
            if (t.Trim() != "") TextSubmitted?.Invoke(t.Trim());
        };
        vbox.AddChild(_voiceState);
        vbox.AddChild(_voiceLast);
        vbox.AddChild(_voiceResult);
        vbox.AddChild(_input);

        // ミニマップ(地図の右下、音声バーの上)
        _minimapFrame = Panel(new Color(0.05f, 0.07f, 0.09f, 0.9f));
        Minimap = new Minimap();
        _minimapFrame.AddChild(Minimap);
        root.AddChild(_minimapFrame);
        PlaceMinimap();
        // ウィンドウの大きさが変わったら、ミニマップも同じ割合で大きさを変える
        GetViewport().SizeChanged += PlaceMinimap;

        _banner = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
        };
        _banner.AddThemeFontSizeOverride("font_size", 40);
        _banner.SetAnchorsPreset(Control.LayoutPreset.Center);
        root.AddChild(_banner);
    }

    public Font Font => _font;

    private void PlaceMinimap()
    {
        var inner = Minimap.SizeFor(GetViewport().GetVisibleRect().Size.X);
        Minimap.CustomMinimumSize = inner;
        Minimap.Size = inner;
        var size = inner + new Vector2(24, 16); // 枠の余白ぶん
        float right = _right.Visible ? -PanelWidth - 16 : -16;
        _minimapFrame.AnchorLeft = _minimapFrame.AnchorRight = 1;
        _minimapFrame.AnchorTop = _minimapFrame.AnchorBottom = 1;
        _minimapFrame.OffsetRight = right;
        _minimapFrame.OffsetLeft = right - size.X;
        _minimapFrame.OffsetBottom = -74;
        _minimapFrame.OffsetTop = -74 - size.Y;
    }

    public void OpenInput()
    {
        _input.Visible = true;
        _input.GrabFocus();
    }

    public void CloseInput()
    {
        _input.Text = "";
        _input.Visible = false;
        _input.ReleaseFocus();
    }

    private static PanelContainer Panel(Color bg)
    {
        var p = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = bg, ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 8, ContentMarginBottom = 8 };
        p.AddThemeStyleboxOverride("panel", sb);
        return p;
    }

    private static Label Heading(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 15);
        return l;
    }

    private static RichTextLabel Rich(string bbcode, int size = 13)
    {
        var r = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, Text = bbcode, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        r.AddThemeFontSizeOverride("normal_font_size", size);
        r.AddThemeFontSizeOverride("bold_font_size", size);
        return r;
    }

    private static string Hex(Color c) => c.ToHtml(false);

    private static string Esc(string s) => s.Replace("[", "[lb]");

    // ------------------------------------------------------------ カードの使い回し
    //
    // 毎回作り直すと日本語の組版がやり直しになって重い(1回100ms以上)。
    // カードは作戦ごとに使い回し、文面が変わったときだけ書き換える。

    private sealed class CardView
    {
        public PanelContainer Panel = null!;
        public StyleBoxFlat Style = null!;
        public RichTextLabel Body = null!;
        public VBoxContainer Extra = null!;
        public string Text = "";
        public readonly Dictionary<int, (HBoxContainer Row, RichTextLabel Label, string Text, bool Buttons)> Plans = new();
    }

    private readonly Dictionary<string, CardView> _cardViews = new();
    private RichTextLabel? _historyText;
    private string _historyCache = "";

    private CardView GetCard(string key)
    {
        if (_cardViews.TryGetValue(key, out var v)) return v;
        v = new CardView();
        v.Style = new StyleBoxFlat
        {
            BgColor = new Color(0.086f, 0.114f, 0.141f), BorderColor = Palette.PanelBorder,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        v.Panel = new PanelContainer { MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        // カードを押すと、その作戦の場所へカメラを移す
        v.Panel.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true }) CardPicked?.Invoke(key);
        };
        v.Panel.AddThemeStyleboxOverride("panel", v.Style);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        v.Panel.AddChild(box);
        v.Body = Rich("", 12);
        v.Body.MouseFilter = Control.MouseFilterEnum.Pass; // 押したのをカードに伝える
        box.AddChild(v.Body);
        v.Extra = new VBoxContainer();
        box.AddChild(v.Extra);
        _cards.AddChild(v.Panel);
        _cardViews[key] = v;
        return v;
    }

    private void SetCard(string key, int order, string text, Color border)
    {
        var v = GetCard(key);
        if (v.Text != text)
        {
            v.Text = text;
            v.Body.Text = text;
        }
        if (v.Style.BorderColor != border) v.Style.BorderColor = border;
        if (v.Panel.GetIndex() != order) _cards.MoveChild(v.Panel, order);
    }

    private static string Title(string title, string sub, string tag, Color tagColor) =>
        $"[font_size=14][b]{Esc(title)}[/b]  [color=#{Hex(Palette.Muted)}]{Esc(sub)}[/color]" +
        (tag != "" ? $"   [color=#{Hex(tagColor)}][b]{tag}[/b][/color]" : "") + "[/font_size]";

    private static string At(double t) => Context.Clock(t);

    // ------------------------------------------------------------ 更新

    public void Refresh(World w, CommandCenter? cc, bool listening, string micNote, string? llmProblem = null)
    {
        var ts = w.Teams[0];
        var recent = ts.Alerts.LastOrDefault();
        bool showAlert = recent != null && w.Time - recent.Time < 30;
        _alert.Visible = showAlert;
        if (showAlert && _alert.Text != "⚠ " + recent!.Text + "(N)") _alert.Text = "⚠ " + recent.Text + "(N)";
        SetText(_resources, $"食 {(int)ts.Stock[Res.Food]}   木 {(int)ts.Stock[Res.Wood]}   金 {(int)ts.Stock[Res.Gold]}   人口 {w.Pop(0)}/{Defs.PopCap}");
        SetText(_clock, Context.Clock(w.Time));
        SetText(_llmState, cc == null ? $"LLM未設定({llmProblem ?? "キーがありません"})" :
            cc.InFlight > 0 ? $"LLM 実行中 {cc.InFlight}" : cc.LastError != "" ? "LLMエラー: " + cc.LastError[..Math.Min(40, cc.LastError.Length)] : "LLM 待機");

        var keys = new HashSet<string>();
        int order = 0;
        foreach (var op in ts.ActiveOps.Where(o => !o.IsEconomy && !o.IsReserve))
        {
            var (text, border) = OpCard(w, op);
            string key = "op" + op.Id;
            SetCard(key, order++, text, border);
            keys.Add(key);
        }
        SetCard("economy", order++, EconomyText(w, ts), Palette.PanelBorder);
        PlanRows(w, ts, GetCard("economy"));
        keys.Add("economy");
        var reserve = w.Members(ts.Reserve).ToList();
        if (reserve.Count > 0)
        {
            SetCard("reserve", order++, Title("予備", "作戦に属していない兵", "", Palette.Muted) + "\n" +
                    string.Join("  ", reserve.GroupBy(u => u.Kind).OrderBy(g => g.Key).Select(g => $"{Defs.Ja(g.Key)} ×{g.Count()}")) +
                    $"  [color=#{Hex(Palette.Muted)}]集合地点で待機[/color]", Palette.PanelBorder);
            keys.Add("reserve");
        }
        foreach (var gone in _cardViews.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _cardViews[gone].Panel.QueueFree();
            _cardViews.Remove(gone);
        }

        // 指示履歴は1つのラベルにまとめ、変わったときだけ書き換える
        var hist = string.Join("\n", ts.History.AsEnumerable().Reverse().Take(8).Select(h =>
        {
            string lat = h.Pending ? "処理中…" :
                $"認識 {h.SttSeconds:0.0}秒" +
                (h.FirstActionSeconds > 0 ? $" / 最初の命令 {h.FirstActionSeconds:0.0}秒" : "") +
                $" / 解釈完了 {h.LlmSeconds:0.0}秒";
            var color = h.Failed ? Palette.Alert : Palette.Muted;
            return $"[color=#{Hex(Palette.Muted)}]{At(h.Time)}[/color]  「{Esc(h.Utterance)}」\n" +
                   $"[color=#{Hex(color)}]→ {Esc(h.Result)}  ({lat})[/color]";
        }));
        if (_historyText == null)
        {
            _historyText = Rich("", 12);
            _history.AddChild(_historyText);
        }
        if (hist != _historyCache)
        {
            _historyCache = hist;
            _historyText.Text = hist;
        }

        var last = ts.History.LastOrDefault();
        SetText(_voiceState, listening ? "● 聞き取り中… (Space を離すと送信。話しながらクリックした地点は「指示点」になる)" :
            "Space: 話す ・ Enter: 文字入力 ・ クリック: 選択 ・ ドラッグ: 範囲選択 ・ 右クリック: 選択中のユニットに指示(なければ目印を消す) ・ 1: 地点 ・ 2: 線 ・ N: 警報へ ・ Tab: 表示切替 ・ C: 作戦パネル ・ F9: ログ" + micNote);
        _voiceState.Modulate = listening ? Palette.Warn : Palette.Muted;
        SetText(_voiceLast, last == null ? "" : $"「{last.Utterance}」 " + (last.Pending ? (last.Result != "" ? "→ " + last.Result + " …" : "→ 解釈中…") : "→ " + last.Result));

        if (w.Winner is { } win)
        {
            _banner.Visible = true;
            _banner.Text = win == 0 ? "勝利" : "敗北";
        }
    }

    private static void SetText(Label l, string text)
    {
        if (l.Text != text) l.Text = text;
    }

    private (string text, Color border) OpCard(World w, Operation op)
    {
        string tag = w.OpTag(op);
        var tagColor = tag switch { "交戦中" => Palette.Warn, "後退中" => Palette.Warn, "兵なし" => Palette.Alert, _ => Palette.Accent };
        var members = w.Members(op).ToList();
        string where = members.Count > 0
            ? w.Map.RegionName(new System.Numerics.Vector2(members.Average(u => u.Pos.X), members.Average(u => u.Pos.Y)))
            : "";
        var lines = new List<string> { Title(op.Name, where, tag, tagColor) };
        if (op.IsScouting) lines.Add($"[color=#{Hex(Palette.Accent)}]目的: {Esc(op.ScoutObjective)}[/color]");
        foreach (var r in op.Roles)
        {
            int n = members.Count(u => u.Role == r.Name);
            if (op.IsScouting)
            {
                lines.Add($"[b]{Esc(r.Name)} ×{n}[/b]  {Esc(r.DescribeScouting())}");
                continue;
            }
            string line = $"[b]{Esc(r.Name)} ×{n}[/b]  {Esc(r.Describe())}";
            string form = w.FormationState(op, r);
            if (form != "") line += $"   [color=#{Hex(Palette.Muted)}]隊列: {form}[/color]";
            if (r.Trigger is { } tr) line += $"\n   [color=#{Hex(Palette.Accent)}]条件待ち: {Esc(tr.Text)}[/color]";
            if (r.Superseded != "" && w.Time - r.ChangedAt < 60)
                line += $"\n   [color=#{Hex(Palette.Muted)}][s]{Esc(r.Superseded)}[/s][/color]";
            lines.Add(line);
        }
        if (op.Status != "")
            lines.Add($"[color=#{Hex(new Color(0.77f, 0.82f, 0.86f))}]状況: {Esc(op.Status)}[/color] [color=#{Hex(Palette.Muted)}]({At(op.StatusAt)})[/color]");
        if (op.Changes.Count > 0)
        {
            var c = op.Changes[^1];
            lines.Add($"[font_size=11][color=#{Hex(Palette.Muted)}]直前の変更({At(c.Time)}) {Esc(c.Text)}[/color][/font_size]");
        }
        return (string.Join("\n", lines), tag == "交戦中" ? Palette.Warn : Palette.PanelBorder);
    }

    private static string EconomyText(World w, TeamState ts)
    {
        var counts = w.GatherCounts(0);
        var villagers = w.TeamUnits(0).Where(u => u.Kind == UnitKind.Villager).ToList();
        string bars = string.Join("   ", Enum.GetValues<Res>().Select(r =>
        {
            var col = counts[r] == ts.Gather[r] ? Palette.Overlay : Palette.Warn;
            return $"[color=#{Hex(col)}]{Defs.Ja(r)} {counts[r]}/{ts.Gather[r]}[/color]";
        }));
        int idle = villagers.Count(v => v.Job == JobKind.None);
        int build = villagers.Count(v => v.Job == JobKind.Build);
        string train = string.Join("  ", ts.TrainGoal.Where(kv => kv.Value != 0)
            .Select(kv => $"{Defs.Ja(kv.Key)} {(kv.Value < 0 ? "継続" : kv.Value + "体まで")}"));
        var lines = new List<string>
        {
            Title("経済", "", "", Palette.Good),
            $"村人 {villagers.Count}人   {bars}" + (build > 0 ? $"   建設 {build}" : "") +
            (idle > 0 ? $"   [color=#{Hex(Palette.Muted)}]待機 {idle}[/color]" : ""),
            $"生産: 村人 {(ts.VillagersContinuous ? $"継続(目標{ts.VillagerTarget})" : "停止")}" + (train != "" ? "  ・  " + train : ""),
        };
        if (ts.EconomyNote != "")
            lines.Add($"[font_size=11][color=#{Hex(Palette.Muted)}]直前の変更({At(ts.EconomyNoteAt)}) {Esc(ts.EconomyNote)}[/color][/font_size]");
        return string.Join("\n", lines);
    }

    /// <summary>建設計画の行(確認待ちには OK / 取消 ボタン)。計画ごとに使い回す。</summary>
    private void PlanRows(World w, TeamState ts, CardView card)
    {
        var live = new HashSet<int>();
        foreach (var p in ts.Plans.Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Confirmed or PlanStatus.Building))
        {
            live.Add(p.Id);
            string state = p.Status switch
            {
                PlanStatus.Proposed => "確認待ち",
                PlanStatus.Confirmed => "資源待ち",
                // 進捗は10%刻みにして、書き換えの回数を減らす
                _ => w.Buildings.TryGetValue(p.BuildingId, out var b) ? $"建設中 {(int)(b.Progress * 10) * 10}%" : "建設中",
            };
            string text = $"#{p.Id} {Defs.Ja(p.Kind)}({w.Map.RegionName(p.Center)})  [color=#{Hex(Palette.Accent)}]{state}[/color]";
            bool buttons = p.Status == PlanStatus.Proposed;
            if (card.Plans.TryGetValue(p.Id, out var row) && row.Buttons != buttons)
            {
                row.Row.QueueFree();
                card.Plans.Remove(p.Id);
            }
            if (!card.Plans.TryGetValue(p.Id, out row))
            {
                var hb = new HBoxContainer();
                var label = Rich("", 12);
                hb.AddChild(label);
                if (buttons)
                {
                    int id = p.Id;
                    var ok = new Button { Text = "OK", CustomMinimumSize = new Vector2(52, 28) };
                    ok.Pressed += () => PlanDecision?.Invoke(id, true);
                    var no = new Button { Text = "取消", CustomMinimumSize = new Vector2(52, 28) };
                    no.Pressed += () => PlanDecision?.Invoke(id, false);
                    hb.AddChild(ok);
                    hb.AddChild(no);
                }
                card.Extra.AddChild(hb);
                row = (hb, label, "", buttons);
            }
            if (row.Text != text)
            {
                row.Label.Text = text;
                row = (row.Row, row.Label, text, row.Buttons);
            }
            card.Plans[p.Id] = row;
        }
        foreach (var id in card.Plans.Keys.Where(id => !live.Contains(id)).ToList())
        {
            card.Plans[id].Row.QueueFree();
            card.Plans.Remove(id);
        }
    }
}
