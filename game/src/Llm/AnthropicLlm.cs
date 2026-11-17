using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    public AnthropicLlm(string apiKey)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
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

    public async Task<LlmReply> Call(LlmRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
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
}
