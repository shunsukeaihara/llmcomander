using System;
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

/// <param name="FirstCallSeconds">最初のツール呼び出しが完成するまでの秒数(ストリーミング時。なければ Seconds と同じ)。</param>
public sealed record LlmReply(IReadOnlyList<ToolCall> Calls, string Text, double Seconds, string? Error = null, double? FirstCallSeconds = null);

/// <summary>LLMの呼び出し口。本番は Claude、テストは決め打ちの返答を返す偽物。</summary>
public interface ILlm
{
    /// <param name="onToolCall">
    /// 指定すると応答をストリーミングで受け取り、ツール呼び出しが1つ完成するたびに(別スレッドから)呼ぶ。
    /// 戻り値の Calls には、ここで渡したものも含めて全部が入る。
    /// </param>
    Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default);
}

/// <summary>音声認識の呼び出し口。</summary>
public interface ISpeech
{
    /// <param name="wav">16bit PCM の WAV ファイルの中身。</param>
    /// <param name="hint">認識を助ける語彙(地名・作戦名など)。</param>
    Task<string> Transcribe(byte[] wav, string hint, CancellationToken ct = default);
}
