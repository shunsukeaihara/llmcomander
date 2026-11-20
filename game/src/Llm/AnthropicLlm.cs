using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using LlmCommander.Command;

namespace LlmCommander.Llm;

/// <summary>
/// Claude を呼ぶ。1回の呼び出しで、Claude が返したツール呼び出しをまとめて返す(ツールの結果は送り返さない)。
/// システムプロンプトとツールはキャッシュして、2回目以降の入力を安く速くする。
/// </summary>
public sealed class AnthropicLlm : ILlm
{
    private readonly AnthropicClient _client;
    private readonly Dictionary<IReadOnlyList<ToolSpec>, List<BetaToolUnion>> _toolCache = new();

    /// <param name="baseUrl">API の URL を変えるとき(省略時は SDK の既定)。</param>
    public AnthropicLlm(string apiKey, string? baseUrl = null)
    {
        _client = baseUrl != null
            ? new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl }
            : new AnthropicClient { ApiKey = apiKey };
    }

    private List<BetaToolUnion> ToolsFor(IReadOnlyList<ToolSpec> specs)
    {
        lock (_toolCache)
        {
            if (_toolCache.TryGetValue(specs, out var cached)) return cached;
            var list = new List<BetaToolUnion>();
            foreach (var t in specs)
            {
                var props = t.Schema.GetProperty("properties").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.Clone());
                var required = t.Schema.TryGetProperty("required", out var r)
                    ? r.EnumerateArray().Select(x => x.GetString()!).ToList()
                    : new List<string>();
                list.Add(new BetaTool
                {
                    Name = t.Name,
                    Description = t.Description,
                    InputSchema = new() { Properties = props, Required = required },
                    // ストリーミング時に入力をサーバー側で溜めずに流す(非ストリーミング時は無視される)。
                    // 代わりに検証はこちらの責任: 完成したブロックだけを厳密に JSON として読み、適用側で中身を確かめる
                    EagerInputStreaming = true,
                });
            }
            _toolCache[specs] = list;
            return list;
        }
    }

    private static Effort ParseEffort(string s) => s switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "max" => Effort.Max,
        _ => Effort.High,
    };

    /// <summary>拒否時のサーバー側フォールバックが使えるモデル(Haiku は対象外)。</summary>
    private static bool SupportsFallback(string model) => !model.Contains("haiku");

    private MessageCreateParams Params(LlmRequest req)
    {
        var p = new MessageCreateParams
        {
            Model = req.Model,
            MaxTokens = req.MaxTokens,
            System = new List<BetaTextBlockParam>
            {
                new() { Text = req.System, CacheControl = new BetaCacheControlEphemeral() },
            },
            Messages = [new BetaUserMessageParam { Content = req.User }],
            Tools = ToolsFor(req.Tools),
            OutputConfig = new BetaOutputConfig { Effort = ParseEffort(req.Effort) },
        };
        if (SupportsFallback(req.Model))
        {
            p = p with
            {
                Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                Fallbacks = new BetaFallbacksParam(new Default()),
            };
        }
        return p;
    }

    public async Task<LlmReply> Call(LlmRequest req, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var p = Params(req);
        if (onToolCall != null) return await Stream(p, onToolCall, sw, ct);

        var msg = await _client.Beta.Messages.Create(p, ct);
        var calls = new List<ToolCall>();
        var text = new List<string>();
        foreach (var block in msg.Content)
        {
            if (block.TryPickToolUse(out BetaToolUseBlock? tu))
                calls.Add(new ToolCall(tu.Name, JsonSerializer.SerializeToElement(tu.Input)));
            else if (block.TryPickText(out BetaTextBlock? tb))
                text.Add(tb.Text);
        }
        string? error = msg.StopReason == "refusal" ? "モデルが応答を拒否しました" : null;
        return new LlmReply(calls, string.Join("\n", text), sw.Elapsed.TotalSeconds, error);
    }

    /// <summary>
    /// ストリーミングで受け取り、ツール呼び出しのブロックが閉じた(完成した)時点で onToolCall に渡す。
    /// 入力の JSON は厳密に読み、読めないもの(途中で切れたものなど)は渡さない。
    /// </summary>
    private async Task<LlmReply> Stream(MessageCreateParams p, Action<ToolCall> onToolCall, Stopwatch sw, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        var json = new Dictionary<long, StringBuilder>();
        var texts = new Dictionary<long, StringBuilder>();
        var calls = new List<ToolCall>();
        var problems = new List<string>();
        double? first = null;
        string? stopReason = null;

        await foreach (var ev in _client.Beta.Messages.CreateStreaming(p, ct))
        {
            if (ev.TryPickContentBlockStart(out var start))
            {
                if (start.ContentBlock.TryPickBetaToolUse(out var tu))
                {
                    names[start.Index] = tu.Name;
                    json[start.Index] = new StringBuilder();
                }
                else if (start.ContentBlock.TryPickBetaText(out _))
                    texts[start.Index] = new StringBuilder();
            }
            else if (ev.TryPickContentBlockDelta(out var delta))
            {
                if (delta.Delta.TryPickInputJson(out var ij) && json.TryGetValue(delta.Index, out var sb))
                    sb.Append(ij.PartialJson);
                else if (delta.Delta.TryPickText(out var td) && texts.TryGetValue(delta.Index, out var tsb))
                    tsb.Append(td.Text);
            }
            else if (ev.TryPickContentBlockStop(out var stop))
            {
                if (!names.TryGetValue(stop.Index, out var name)) continue;
                string raw = json[stop.Index].ToString();
                JsonElement input;
                try
                {
                    input = JsonDocument.Parse(raw.Trim() == "" ? "{}" : raw).RootElement.Clone();
                }
                catch (JsonException)
                {
                    problems.Add($"{name} の入力が読めませんでした");
                    continue;
                }
                var call = new ToolCall(name, input);
                calls.Add(call);
                first ??= sw.Elapsed.TotalSeconds;
                onToolCall(call);
            }
            else if (ev.TryPickDelta(out var md))
            {
                if (md.Delta.StopReason == "refusal") stopReason = "refusal";
                else if (md.Delta.StopReason == "max_tokens") stopReason = "max_tokens";
            }
        }
        string? error = stopReason switch
        {
            "refusal" => "モデルが応答を拒否しました",
            "max_tokens" => "応答が長すぎて途中で切れました",
            _ => problems.Count > 0 ? string.Join("、", problems) : null,
        };
        string text = string.Join("\n", texts.OrderBy(kv => kv.Key).Select(kv => kv.Value.ToString()));
        return new LlmReply(calls, text, sw.Elapsed.TotalSeconds, error, first);
    }
}
