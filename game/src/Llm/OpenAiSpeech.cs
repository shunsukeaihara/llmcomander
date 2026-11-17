using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LlmCommander.Command;
using OpenAI.Audio;

namespace LlmCommander.Llm;

/// <summary>OpenAI の音声認識API。地名や作戦名を prompt で渡して、固有名詞の認識を助ける。</summary>
public sealed class OpenAiSpeech : ISpeech
{
    private readonly AudioClient _client;

    public OpenAiSpeech(string apiKey, string model)
    {
        _client = new AudioClient(model, apiKey);
    }

    public async Task<string> Transcribe(byte[] wav, string hint, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(wav);
        var options = new AudioTranscriptionOptions
        {
            Language = "ja",
            Prompt = "RTSゲームの指揮の音声です。用語: " + hint,
        };
        var result = await _client.TranscribeAudioAsync(stream, "speech.wav", options, ct);
        return result.Value.Text;
    }
}
