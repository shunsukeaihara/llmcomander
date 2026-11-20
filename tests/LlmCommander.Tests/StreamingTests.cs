using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

public class StreamingTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>1つ目の命令を先に渡し、合図があるまで残りの応答を止めておく偽物の LLM。</summary>
    private sealed class GatedLlm : ILlm
    {
        public readonly SemaphoreSlim Gate = new(0);

        public async Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
        {
            var first = new ToolCall("set_economy", J("""{"gather": {"wood": 3}, "change_summary": "木に3人"}"""));
            var second = new ToolCall("set_economy", J("""{"gather": {"gold": 2}, "change_summary": "金に2人"}"""));
            onToolCall?.Invoke(first);
            await Gate.WaitAsync(ct);
            onToolCall?.Invoke(second);
            return new LlmReply(new[] { first, second }, "", 2.0, null, 0.1);
        }
    }

    private static void Pump(CommandCenter cc, Func<bool> until)
    {
        for (int i = 0; i < 2000 && !until(); i++)
        {
            Thread.Sleep(5);
            cc.Update();
        }
    }

    [Fact]
    public void FirstCommandAppliesBeforeTheReplyFinishes()
    {
        var w = MapLayout.CreateValley();
        var llm = new GatedLlm();
        var cc = new CommandCenter(w, 0, llm, null, new LlmSettings { OfficersEnabled = false });
        var entry = cc.SubmitText("木に3人、金に2人", new UtteranceContext());

        Pump(cc, () => w.Teams[0].Gather[Res.Wood] == 3);
        Assert.Equal(3, w.Teams[0].Gather[Res.Wood]);   // 1つ目は反映済み
        Assert.Equal(0, w.Teams[0].Gather[Res.Gold]);   // 2つ目はまだ
        Assert.True(entry.Pending);                     // 応答はまだ終わっていない
        Assert.True(entry.FirstActionSeconds > 0);
        Assert.StartsWith("内政", entry.Result);         // 途中の結果も履歴に出る

        llm.Gate.Release();
        Pump(cc, () => !entry.Pending);
        Assert.False(entry.Pending);
        Assert.Equal(2, w.Teams[0].Gather[Res.Gold]);
        Assert.Equal(3, w.Teams[0].Gather[Res.Wood]);   // 1つ目を二重に適用しない
        Assert.Equal(2, entry.Result.Split(" / ").Length);
    }

    /// <summary>応答が途中で失敗しても、それまでに完成した命令は残る。</summary>
    private sealed class FailsAfterFirstLlm : ILlm
    {
        public Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
        {
            var first = new ToolCall("set_economy", J("""{"gather": {"wood": 4}, "change_summary": "木に4人"}"""));
            onToolCall?.Invoke(first);
            return Task.FromResult(new LlmReply(new[] { first }, "", 1.0, "応答が長すぎて途中で切れました", 0.2));
        }
    }

    [Fact]
    public void PartialReplyKeepsAppliedCommandsAndReportsTheError()
    {
        var w = MapLayout.CreateValley();
        var cc = new CommandCenter(w, 0, new FailsAfterFirstLlm(), null, new LlmSettings { OfficersEnabled = false });
        var entry = cc.SubmitText("木に4人と、あと…", new UtteranceContext());
        Pump(cc, () => !entry.Pending);
        Assert.Equal(4, w.Teams[0].Gather[Res.Wood]);
        Assert.False(entry.Failed);
        Assert.Contains("※応答が長すぎて", entry.Result);
    }
}
