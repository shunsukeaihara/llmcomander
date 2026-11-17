using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LlmCommander.Command;

public sealed record ToolSpec(string Name, string Description, JsonElement Schema);

public sealed record ToolCall(string Name, JsonElement Input);

public sealed record LlmRequest(
    string Model,
    string Effort,
    string System,
    string User,
    IReadOnlyList<ToolSpec> Tools,
    int MaxTokens = 4000);

public sealed record LlmReply(IReadOnlyList<ToolCall> Calls, string Text, double Seconds, string? Error = null);

/// <summary>LLMの呼び出し口。本番は Claude、テストは決め打ちの返答を返す偽物。</summary>
public interface ILlm
{
    Task<LlmReply> Call(LlmRequest request, CancellationToken ct = default);
}

/// <summary>音声認識の呼び出し口。</summary>
public interface ISpeech
{
    /// <param name="wav">16bit PCM の WAV ファイルの中身。</param>
    /// <param name="hint">認識を助ける語彙(地名・作戦名など)。</param>
    Task<string> Transcribe(byte[] wav, string hint, CancellationToken ct = default);
}
