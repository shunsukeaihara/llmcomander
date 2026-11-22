using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LlmCommander.Sim;

namespace LlmCommander.Command;

public sealed class LlmSettings
{
    public string CommanderModel = "gpt-5.4-mini";
    public string CommanderEffort = "low";
    public string OfficerModel = "gpt-5.4-nano";
    public string OfficerEffort = "low";
    /// <summary>担当LLMを呼ぶ間隔(秒、ゲーム内時間)。出来事があればもっと早く呼ぶ。</summary>
    public double OfficerInterval = 6;
    public double OfficerMinInterval = 2;
    public bool OfficersEnabled = true;
}

/// <summary>
/// 発話を司令LLMに渡し、作戦ごとの担当LLMを並列に回す。
/// LLM の呼び出しは別スレッドで行い、結果はメインスレッドの Update でゲームに適用する(ゲームは止まらない)。
/// </summary>
public sealed class CommandCenter
{
    private readonly World _w;
    private readonly int _team;
    private readonly ILlm _llm;
    private readonly ISpeech? _speech;
    private readonly Applier _applier;
    private readonly ConcurrentQueue<Action> _main = new();
    private readonly Dictionary<int, Dictionary<string, int>> _lastCounts = new();
    private readonly Dictionary<int, int> _lastChangeIndex = new();
    public readonly LlmSettings Settings;

    /// <summary>実行中の LLM 呼び出しの数(表示用)。</summary>
    public int InFlight;
    public string LastError = "";
    /// <summary>プレイログ(なければ記録しない)。</summary>
    public PlayLog? Log;

    public CommandCenter(World w, int team, ILlm llm, ISpeech? speech, LlmSettings settings)
    {
        _w = w;
        _team = team;
        _llm = llm;
        _speech = speech;
        Settings = settings;
        _applier = new Applier(w, team);
    }

    private TeamState T => _w.Teams[_team];

    /// <summary>メインスレッドで毎フレーム呼ぶ。</summary>
    public void Update()
    {
        while (_main.TryDequeue(out var a)) a();
        if (Settings.OfficersEnabled) ScheduleOfficers();
    }

    // ------------------------------------------------------------ 発話

    public HistoryEntry SubmitAudio(byte[] wav, UtteranceContext ctx)
    {
        var entry = new HistoryEntry { Time = _w.Time, Utterance = "(音声を認識中…)" };
        T.History.Add(entry);
        if (_speech == null)
        {
            entry.Pending = false;
            entry.Failed = true;
            entry.Result = "音声認識が設定されていません(OPENAI_API_KEY)";
            return entry;
        }
        string hint = Context.SpeechHint(_w, _team);
        var sw = Stopwatch.StartNew();
        Interlocked.Increment(ref InFlight);
        Task.Run(async () =>
        {
            try
            {
                string text = (await _speech.Transcribe(wav, hint)).Trim();
                double stt = sw.Elapsed.TotalSeconds;
                _main.Enqueue(() =>
                {
                    Log?.Write("stt", new { text, seconds = Math.Round(stt, 3), wav_bytes = wav.Length });
                    if (text == "")
                    {
                        entry.Pending = false;
                        entry.Failed = true;
                        entry.Utterance = "(聞き取れませんでした)";
                        entry.SttSeconds = stt;
                        return;
                    }
                    T.History.Remove(entry);
                    SubmitText(text, ctx, stt);
                });
            }
            catch (Exception ex)
            {
                _main.Enqueue(() =>
                {
                    entry.Pending = false;
                    entry.Failed = true;
                    entry.Result = "音声認識に失敗: " + ex.Message;
                    LastError = ex.Message;
                    Log?.Write("stt", new { error = ex.Message, seconds = Math.Round(sw.Elapsed.TotalSeconds, 3) });
                });
            }
            finally
            {
                Interlocked.Decrement(ref InFlight);
            }
        });
        return entry;
    }

    public HistoryEntry SubmitText(string utterance, UtteranceContext ctx, double sttSeconds = 0)
    {
        var entry = new HistoryEntry { Time = _w.Time, Utterance = utterance, SttSeconds = sttSeconds };
        T.History.Add(entry);
        if (T.History.Count > 50) T.History.RemoveAt(0);

        // 話しながらクリックした地点は「指示点N」という目印にする(前回の指示点は消す)
        if (ctx.Clicks.Count > 0)
        {
            T.Markers.RemoveAll(m => m.Name.StartsWith("指示点"));
            for (int i = 0; i < ctx.Clicks.Count; i++)
                T.Markers.Add(new Marker { Name = $"指示点{i + 1}", Points = { ctx.Clicks[i] } });
        }
        var selected = ctx.Selected.ToList();
        var sites = Context.Sites(_w, _team);
        string user = Context.Commander(_w, _team, ctx, utterance, sites);
        var req = new LlmRequest(Settings.CommanderModel, Settings.CommanderEffort, Tools.CommanderSystem, user, Tools.CommanderTools);
        var submitted = Stopwatch.StartNew();
        double submittedAt = _w.Time;
        // 適用結果(メインスレッドだけが触る)と、ストリーミングで先に渡された命令の数
        var results = new List<string>();
        int streamed = 0;

        Interlocked.Increment(ref InFlight);
        Task.Run(async () =>
        {
            LlmReply reply;
            try
            {
                // 命令が1つ完成するたびに、応答の残りを待たずにゲームへ反映する
                reply = await _llm.Call(req, call =>
                {
                    Interlocked.Increment(ref streamed);
                    _main.Enqueue(() => ApplyOne(entry, call, selected, results, submitted, sites));
                });
            }
            catch (Exception ex)
            {
                reply = new LlmReply(Array.Empty<ToolCall>(), "", 0, ex.Message);
            }
            int alreadyApplied = Volatile.Read(ref streamed);
            // 実行中の数は、結果をゲームに取り込んだ時点で減らす
            _main.Enqueue(() =>
            {
                Interlocked.Decrement(ref InFlight);
                Finish(entry, reply, selected, results, alreadyApplied, submitted, sites);
                Log?.Write("command", new
                {
                    utterance,
                    source = sttSeconds > 0 ? "voice" : "text",
                    submitted_t = Math.Round(submittedAt, 2),
                    stt_seconds = Math.Round(sttSeconds, 3),
                    llm_seconds = Math.Round(reply.Seconds, 3),
                    // 送信してから最初の命令がゲームに反映されるまで
                    first_action_seconds = Math.Round(entry.FirstActionSeconds, 3),
                    // 送信してから全部の命令がゲームに反映されるまで
                    applied_seconds = Math.Round(submitted.Elapsed.TotalSeconds, 3),
                    streamed = alreadyApplied,
                    model = req.Model,
                    effort = req.Effort,
                    selected = selected.Count,
                    clicks = ctx.Clicks.Count,
                    prompt = user,
                    calls = reply.Calls.Select((c, i) => new { name = c.Name, input = c.Input, result = i < results.Count ? results[i] : "" }),
                    text = reply.Text,
                    error = reply.Error,
                    result = entry.Result,
                });
            });
        });
        return entry;
    }

    /// <summary>命令を1つ適用し、履歴の結果欄を更新する(メインスレッド)。</summary>
    private void ApplyOne(HistoryEntry entry, ToolCall call, IReadOnlyList<int> selected, List<string> results, Stopwatch submitted,
                          IReadOnlyDictionary<string, SiteCandidate> sites)
    {
        results.Add(_applier.Apply(call, entry.Utterance, selected, sites));
        if (entry.FirstActionSeconds == 0) entry.FirstActionSeconds = submitted.Elapsed.TotalSeconds;
        entry.Result = string.Join(" / ", results.Where(r => r != ""));
        T.Version++;
    }

    /// <summary>応答が終わったときの後始末。ストリーミングで渡されなかった命令があればここで適用する。</summary>
    private void Finish(HistoryEntry entry, LlmReply reply, IReadOnlyList<int> selected, List<string> results, int alreadyApplied, Stopwatch submitted,
                        IReadOnlyDictionary<string, SiteCandidate> sites)
    {
        foreach (var c in reply.Calls.Skip(alreadyApplied)) ApplyOne(entry, c, selected, results, submitted, sites);
        entry.Pending = false;
        entry.LlmSeconds = reply.Seconds;
        var shown = results.Where(r => r != "").ToList();
        if (reply.Error != null)
        {
            LastError = reply.Error;
            if (shown.Count == 0)
            {
                entry.Failed = true;
                entry.Result = "LLMの呼び出しに失敗: " + reply.Error;
                return;
            }
            // 途中までの命令は反映済み。残りが失敗したことを添える
            entry.Result = string.Join(" / ", shown) + " ※" + reply.Error;
            return;
        }
        if (shown.Count == 0) shown.Add(reply.Text != "" ? "返答: " + reply.Text : "命令なし");
        entry.Result = string.Join(" / ", shown);
        T.Version++;
    }

    // ------------------------------------------------------------ 担当LLM

    private void ScheduleOfficers()
    {
        foreach (var op in T.ActiveOps)
        {
            if (op.IsEconomy || op.IsReserve || op.IsManual || op.OfficerBusy) continue;
            if (!_w.Members(op).Any()) continue;
            bool due = _w.Time >= op.OfficerDue || (op.Eventful && _w.Time >= op.OfficerDue - Settings.OfficerInterval + Settings.OfficerMinInterval);
            if (!due) continue;
            RunOfficer(op);
        }
    }

    private List<string> CollectEvents(Operation op)
    {
        var events = new List<string>();
        int from = _lastChangeIndex.GetValueOrDefault(op.Id, 0);
        foreach (var c in op.Changes.Skip(Math.Min(from, op.Changes.Count))) events.Add(c.Text);
        _lastChangeIndex[op.Id] = op.Changes.Count;

        var now = op.Roles.ToDictionary(r => r.Name, r => _w.Members(op, r).Count());
        if (_lastCounts.TryGetValue(op.Id, out var prev))
            foreach (var (role, n) in now)
                if (prev.TryGetValue(role, out var p) && p > n) events.Add($"{role}が{p - n}体やられた");
        _lastCounts[op.Id] = now;
        return events;
    }

    private void RunOfficer(Operation op)
    {
        op.OfficerBusy = true;
        op.Eventful = false;
        op.OfficerDue = _w.Time + Settings.OfficerInterval;
        string user = Context.Officer(_w, op, CollectEvents(op));
        var req = new LlmRequest(Settings.OfficerModel, Settings.OfficerEffort, Tools.OfficerSystem, user, Tools.OfficerTools, 1500);
        Interlocked.Increment(ref InFlight);
        Task.Run(async () =>
        {
            LlmReply reply;
            try
            {
                reply = await _llm.Call(req);
            }
            catch (Exception ex)
            {
                reply = new LlmReply(Array.Empty<ToolCall>(), "", 0, ex.Message);
            }
            // 実行中の数は、結果をゲームに取り込んだ時点で減らす
            _main.Enqueue(() =>
            {
                Interlocked.Decrement(ref InFlight);
                op.OfficerBusy = false;
                Log?.Write("officer", new
                {
                    op = op.Name,
                    seconds = Math.Round(reply.Seconds, 3),
                    model = req.Model,
                    prompt = user,
                    calls = reply.Calls.Select(c => new { name = c.Name, input = c.Input }),
                    error = reply.Error,
                });
                if (reply.Error != null)
                {
                    LastError = reply.Error;
                    return;
                }
                if (op.Disbanded) return;
                foreach (var c in reply.Calls) _applier.ApplyOfficer(op, c);
            });
        });
    }
}
