using LlmCommander.Command;

namespace LlmCommander.Llm;

/// <summary>
/// 設定から LLM と音声認識を用意する。既定は OpenAI だけで動く(必要なキーは OPENAI_API_KEY の1つ)。
///   LLM_PROVIDER     openai(既定)/ anthropic
///   COMMANDER_MODEL  司令役のモデル(既定 openai: gpt-5.4-mini、anthropic: claude-opus-5-5)
///   COMMANDER_EFFORT 推論の深さ(既定 low)
///   OFFICER_MODEL    作戦ごとの担当のモデル(既定 openai: gpt-5.4-nano、anthropic: claude-haiku-5-5)
///   OFFICER_EFFORT   推論の深さ(既定 low)
///   STT_MODEL        音声認識のモデル(既定 gpt-4o-transcribe)
/// </summary>
public sealed record LlmSetup(ILlm? Llm, ISpeech? Speech, LlmSettings Settings, string Provider, string? Problem)
{
    public static LlmSetup From(Config config)
    {
        string provider = config.Get("LLM_PROVIDER", "openai").Trim().ToLowerInvariant();
        bool anthropic = provider == "anthropic";
        var settings = new LlmSettings
        {
            CommanderModel = config.Get("COMMANDER_MODEL", anthropic ? "claude-opus-5-5" : "gpt-5.4-mini"),
            CommanderEffort = config.Get("COMMANDER_EFFORT", "low"),
            OfficerModel = config.Get("OFFICER_MODEL", anthropic ? "claude-haiku-5-5" : "gpt-5.4-nano"),
            OfficerEffort = config.Get("OFFICER_EFFORT", "low"),
        };
        var openaiKey = config.Get("OPENAI_API_KEY");
        ISpeech? speech = openaiKey != null ? new OpenAiSpeech(openaiKey, config.Get("STT_MODEL", "gpt-4o-transcribe")) : null;

        if (anthropic)
        {
            var key = config.Get("ANTHROPIC_API_KEY");
            return key == null
                ? new LlmSetup(null, speech, settings, provider, "ANTHROPIC_API_KEY がありません(LLM_PROVIDER=anthropic)")
                : new LlmSetup(new AnthropicLlm(key, config.Get("ANTHROPIC_API_URL")), speech, settings, provider, null);
        }
        return openaiKey == null
            ? new LlmSetup(null, null, settings, "openai", "OPENAI_API_KEY がありません")
            : new LlmSetup(new OpenAiLlm(openaiKey), speech, settings, "openai", null);
    }
}
