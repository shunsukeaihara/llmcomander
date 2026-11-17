using System;
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
        topRow.AddChild(_llmState);
        _clock = new Label();
        topRow.AddChild(_clock);

        // 右パネル
        var right = Panel(new Color(0.05f, 0.07f, 0.09f, 0.97f));
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
        var voice = Panel(new Color(0.07f, 0.1f, 0.13f, 0.93f));
        voice.AnchorTop = 1;
        voice.AnchorBottom = 1;
        voice.AnchorRight = 1;
        voice.OffsetLeft = 16;
        voice.OffsetRight = -PanelWidth - 16;
        voice.OffsetTop = -112;
        voice.OffsetBottom = -16;
        root.AddChild(voice);
        var vbox = new VBoxContainer();
        voice.AddChild(vbox);
        _voiceState = new Label { Modulate = Palette.Muted };
        _voiceLast = new Label();
        _voiceLast.AddThemeFontSizeOverride("font_size", 16);
        _voiceResult = new Label { Modulate = Palette.Muted, AutowrapMode = TextServer.AutowrapMode.WordSmart };
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

    private VBoxContainer Card(string title, string sub, string tag, Color tagColor, Color border)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.086f, 0.114f, 0.141f), BorderColor = border,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        _cards.AddChild(p);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        p.AddChild(box);
        box.AddChild(Rich($"[b]{Esc(title)}[/b]  [color=#{Hex(Palette.Muted)}]{Esc(sub)}[/color]" +
                          (tag != "" ? $"   [color=#{Hex(tagColor)}][b]{tag}[/b][/color]" : ""), 14));
        return box;
    }

    private static string Ago(World w, double t)
    {
        int s = (int)(w.Time - t);
        return s < 60 ? $"{s}秒前" : $"{s / 60}分前";
    }

    // ------------------------------------------------------------ 更新

    public void Refresh(World w, CommandCenter? cc, bool listening, string micNote)
    {
        var ts = w.Teams[0];
        _resources.Text = $"食 {(int)ts.Stock[Res.Food]}   木 {(int)ts.Stock[Res.Wood]}   金 {(int)ts.Stock[Res.Gold]}   人口 {w.Pop(0)}/{Defs.PopCap}";
        _clock.Text = Context.Clock(w.Time);
        _llmState.Text = cc == null ? "LLM未設定(.env に ANTHROPIC_API_KEY)" :
            cc.InFlight > 0 ? $"LLM 実行中 {cc.InFlight}" : cc.LastError != "" ? "LLMエラー: " + cc.LastError[..Math.Min(40, cc.LastError.Length)] : "LLM 待機";

        foreach (var c in _cards.GetChildren()) c.QueueFree();
        foreach (var c in _history.GetChildren()) c.QueueFree();

        foreach (var op in ts.ActiveOps.Where(o => !o.IsEconomy && !o.IsReserve)) OpCard(w, op);
        EconomyCard(w, ts);
        var reserve = w.Members(ts.Reserve).ToList();
        if (reserve.Count > 0)
        {
            var box = Card("予備", "作戦に属していない兵", "", Palette.Muted, Palette.PanelBorder);
            box.AddChild(Rich(string.Join("  ", reserve.GroupBy(u => u.Kind).Select(g => $"{Defs.Ja(g.Key)} ×{g.Count()}")) +
                              $"  [color=#{Hex(Palette.Muted)}]集合地点で待機[/color]", 12));
        }

        foreach (var h in ts.History.AsEnumerable().Reverse().Take(8))
        {
            string lat = h.Pending ? "処理中…" :
                $"認識 {h.SttSeconds:0.0}秒 / 解釈 {h.LlmSeconds:0.0}秒";
            var color = h.Failed ? Palette.Alert : Palette.Muted;
            _history.AddChild(Rich(
                $"[color=#{Hex(Palette.Muted)}]{Ago(w, h.Time)}[/color]  「{Esc(h.Utterance)}」\n" +
                $"[color=#{Hex(color)}]→ {Esc(h.Result)}  ({lat})[/color]", 12));
        }

        var last = ts.History.LastOrDefault();
        _voiceState.Text = listening ? "● 聞き取り中… (Space を離すと送信。話しながらクリックした地点は「指示点」になる)" :
            "Space 長押しで話す ・ Enter で文字入力 ・ 1: 地点の目印 ・ 2: 線の目印(ドラッグ) ・ 右クリック: 目印を消す ・ Tab: 意図表示" + micNote;
        _voiceState.Modulate = listening ? Palette.Warn : Palette.Muted;
        _voiceLast.Text = last != null ? $"「{last.Utterance}」" : "";
        _voiceResult.Text = last == null ? "" : last.Pending ? "解釈中…" : "→ " + last.Result;

        if (w.Winner is { } win)
        {
            _banner.Visible = true;
            _banner.Text = win == 0 ? "勝利" : "敗北";
        }
    }

    private void OpCard(World w, Operation op)
    {
        string tag = w.OpTag(op);
        var tagColor = tag switch { "交戦中" => Palette.Warn, "後退中" => Palette.Warn, "兵なし" => Palette.Alert, _ => Palette.Accent };
        var members = w.Members(op).ToList();
        string where = members.Count > 0
            ? w.Map.RegionName(new System.Numerics.Vector2(members.Average(u => u.Pos.X), members.Average(u => u.Pos.Y)))
            : "";
        var box = Card(op.Name, where, tag, tagColor, tag == "交戦中" ? Palette.Warn : Palette.PanelBorder);
        foreach (var r in op.Roles)
        {
            int n = w.Members(op, r).Count();
            string line = $"[b]{Esc(r.Name)} ×{n}[/b]  {Esc(r.Describe())}";
            if (r.Trigger is { } tr) line += $"\n   [color=#{Hex(Palette.Accent)}]条件待ち: {Esc(tr.Text)}[/color]";
            if (r.Superseded != "" && w.Time - r.ChangedAt < 60)
                line += $"\n   [color=#{Hex(Palette.Muted)}][s]{Esc(r.Superseded)}[/s][/color]";
            string form = w.FormationState(op, r);
            if (form != "") line += $"   [color=#{Hex(Palette.Muted)}]隊列: {form}[/color]";
            box.AddChild(Rich(line, 12));
        }
        if (op.Status != "")
            box.AddChild(Rich($"[color=#{Hex(new Color(0.77f, 0.82f, 0.86f))}]状況: {Esc(op.Status)}[/color] [color=#{Hex(Palette.Muted)}]({Ago(w, op.StatusAt)})[/color]", 12));
        if (op.Changes.Count > 0)
        {
            var c = op.Changes[^1];
            box.AddChild(Rich($"[color=#{Hex(Palette.Muted)}]直前の変更({Ago(w, c.Time)}) {Esc(c.Text)}[/color]", 11));
        }
    }

    private void EconomyCard(World w, TeamState ts)
    {
        var box = Card("経済", "", "", Palette.Good, Palette.PanelBorder);
        var counts = w.GatherCounts(0);
        var villagers = w.TeamUnits(0).Where(u => u.Kind == UnitKind.Villager).ToList();
        string bars = string.Join("   ", Enum.GetValues<Res>().Select(r =>
        {
            var col = counts[r] == ts.Gather[r] ? Palette.Overlay : Palette.Warn;
            return $"[color=#{Hex(col)}]{Defs.Ja(r)} {counts[r]}/{ts.Gather[r]}[/color]";
        }));
        int idle = villagers.Count(v => v.Job == JobKind.None);
        int build = villagers.Count(v => v.Job == JobKind.Build);
        box.AddChild(Rich($"村人 {villagers.Count}人   {bars}" +
                          (build > 0 ? $"   建設 {build}" : "") + (idle > 0 ? $"   [color=#{Hex(Palette.Muted)}]待機 {idle}[/color]" : ""), 12));
        string train = string.Join("  ", ts.TrainGoal.Where(kv => kv.Value != 0)
            .Select(kv => $"{Defs.Ja(kv.Key)} {(kv.Value < 0 ? "継続" : kv.Value + "体まで")}"));
        box.AddChild(Rich($"生産: 村人 {(ts.VillagersContinuous ? $"継続(目標{ts.VillagerTarget})" : "停止")}" +
                          (train != "" ? "  ・  " + train : ""), 12));
        foreach (var p in ts.Plans.Where(p => p.Status is PlanStatus.Proposed or PlanStatus.Confirmed or PlanStatus.Building))
        {
            var row = new HBoxContainer();
            string state = p.Status switch
            {
                PlanStatus.Proposed => "確認待ち",
                PlanStatus.Confirmed => "資源待ち",
                _ => w.Buildings.TryGetValue(p.BuildingId, out var b) ? $"建設中 {b.Progress:P0}" : "建設中",
            };
            row.AddChild(Rich($"#{p.Id} {Defs.Ja(p.Kind)}({w.Map.RegionName(p.Center)})  [color=#{Hex(Palette.Accent)}]{state}[/color]", 12));
            if (p.Status == PlanStatus.Proposed)
            {
                int id = p.Id;
                var ok = new Button { Text = "OK", CustomMinimumSize = new Vector2(52, 28) };
                ok.Pressed += () => PlanDecision?.Invoke(id, true);
                var no = new Button { Text = "取消", CustomMinimumSize = new Vector2(52, 28) };
                no.Pressed += () => PlanDecision?.Invoke(id, false);
                row.AddChild(ok);
                row.AddChild(no);
            }
            box.AddChild(row);
        }
        if (ts.EconomyNote != "")
            box.AddChild(Rich($"[color=#{Hex(Palette.Muted)}]直前の変更({Ago(w, ts.EconomyNoteAt)}) {Esc(ts.EconomyNote)}[/color]", 11));
    }
}
