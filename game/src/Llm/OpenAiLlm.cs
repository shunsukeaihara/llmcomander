using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LlmCommander.Command;
using OpenAI.Chat;

namespace LlmCommander.Llm;

/// <summary>
/// OpenAI のチャットAPIで LLM を呼ぶ。ツール(命令の形)は関数呼び出しとして渡す。
/// ストリーミング時は、次の関数呼び出しが始まった時点(または応答の終わり)で直前の呼び出しが完成したとみなして渡す。
/// システムプロンプトとツールは毎回同じ内容を先頭に置くので、OpenAI 側の自動キャッシュが効く。
/// </summary>
public sealed class OpenAiLlm : ILlm
{
    private readonly string _apiKey;
    private readonly Dictionary<string, ChatClient> _clients = new();
    private readonly Dictionary<IReadOnlyList<ToolSpec>, List<ChatTool>> _toolCache = new();

    public OpenAiLlm(string apiKey)
    {
        _apiKey = apiKey;
    }

    private ChatClient Client(string model)
    {
        lock (_clients)
        {
            if (!_clients.TryGetValue(model, out var c)) _clients[model] = c = new ChatClient(model, _apiKey);
            return c;
        }
    }

    private List<ChatTool> ToolsFor(IReadOnlyList<ToolSpec> specs)
    {
        lock (_toolCache)
        {
            if (!_toolCache.TryGetValue(specs, out var tools))
                _toolCache[specs] = tools = specs
                    .Select(t => ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.Schema.GetRawText())))
                    .ToList();
            return tools;
        }
    }

    private ChatCompletionOptions Options(LlmRequest req)
    {
        var o = new ChatCompletionOptions
        {
            MaxOutputTokenCount = req.MaxTokens,
            AllowParallelToolCalls = true,
        };
        // 推論の深さの指定は SDK では「評価用」扱いだが、API としては正式な引数なので使う
#pragma warning disable OPENAI001
        o.ReasoningEffortLevel = new ChatReasoningEffortLevel(req.Effort);
#pragma warning restore OPENAI001
        foreach (var t in ToolsFor(req.Tools)) o.Tools.Add(t);
        return o;
    }

    private static List<ChatMessage> Messages(LlmRequest req) =>
        new() { new SystemChatMessage(req.System), new UserChatMessage(req.User) };

    /// <summary>関数呼び出しの引数を厳密に JSON として読む。読めなければ null(途中で切れたものなど)。</summary>
    private static ToolCall? Parse(string name, string args, List<string> problems)
    {
        try
        {
            return new ToolCall(name, JsonDocument.Parse(args.Trim() == "" ? "{}" : args).RootElement.Clone());
        }
        catch (JsonException)
        {
            problems.Add($"{name} の入力が読めませんでした");
            return null;
        }
    }

    public async Task<LlmReply> Call(LlmRequest req, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var client = Client(req.Model);
        var problems = new List<string>();
        var calls = new List<ToolCall>();

        if (onToolCall == null)
        {
            ClientResult<ChatCompletion> result = await client.CompleteChatAsync(Messages(req), Options(req), ct);
            var c = result.Value;
            foreach (var tc in c.ToolCalls)
                if (Parse(tc.FunctionName, tc.FunctionArguments.ToString(), problems) is { } call)
                    calls.Add(call);
            string text = string.Concat(c.Content.Select(p => p.Text));
            return new LlmReply(calls, text, sw.Elapsed.TotalSeconds, Error(c.FinishReason, problems));
        }

        // 関数呼び出しは index ごとに断片で届く。次の index が始まったら、前の呼び出しは完成している
        var names = new Dictionary<int, string>();
        var args = new Dictionary<int, StringBuilder>();
        var textSb = new StringBuilder();
        int current = -1;
        double? first = null;
        ChatFinishReason? finish = null;

        void Complete(int index)
        {
            if (index < 0 || !names.TryGetValue(index, out var name)) return;
            if (Parse(name, args[index].ToString(), problems) is not { } call) return;
            calls.Add(call);
            first ??= sw.Elapsed.TotalSeconds;
            onToolCall(call);
        }

        await foreach (var update in client.CompleteChatStreamingAsync(Messages(req), Options(req), ct))
        {
            foreach (var part in update.ContentUpdate) textSb.Append(part.Text);
            foreach (var tu in update.ToolCallUpdates)
            {
                if (tu.Index != current)
                {
                    Complete(current);
                    current = tu.Index;
                }
                if (!string.IsNullOrEmpty(tu.FunctionName)) names[tu.Index] = tu.FunctionName;
                if (!args.TryGetValue(tu.Index, out var sb)) args[tu.Index] = sb = new StringBuilder();
                if (tu.FunctionArgumentsUpdate != null) sb.Append(tu.FunctionArgumentsUpdate.ToString());
            }
            if (update.FinishReason != null) finish = update.FinishReason;
        }
        // 長さの上限で切れた場合、最後の呼び出しは不完全なので渡さない
        if (finish != ChatFinishReason.Length) Complete(current);
        return new LlmReply(calls, textSb.ToString(), sw.Elapsed.TotalSeconds, Error(finish, problems), first);
    }

    private static string? Error(ChatFinishReason? finish, List<string> problems) => finish switch
    {
        ChatFinishReason.ContentFilter => "モデルが応答を拒否しました",
        ChatFinishReason.Length => "応答が長すぎて途中で切れました",
        _ => problems.Count > 0 ? string.Join("、", problems) : null,
    };
}
