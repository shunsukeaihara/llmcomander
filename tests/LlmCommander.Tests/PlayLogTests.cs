using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

public class PlayLogTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>司令役には作戦を、担当には状況報告を返す偽物の LLM。</summary>
    private sealed class ScriptedLlm : ILlm
    {
        public Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
        {
            if (request.Tools.Any(t => t.Name == "report"))
                return Task.FromResult(new LlmReply(new[] { new ToolCall("report", J("""{"status": "北東の林で待機中"}""")) }, "", 0.3));
            var calls = new[]
            {
                new ToolCall("update_operation", J("""
                    {"operation": "前線", "change_summary": "北東の林に布陣",
                     "roles": [
                       {"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "北東の林"},
                       {"role": "騎兵", "unit_kind": "cavalry", "stance": "hold", "place": "中央",
                        "trigger": {"condition": "role_engaged", "role": "槍", "then": {"stance": "advance", "place": "北東の林"}, "description": "槍が接触したら突撃"}}
                     ]}
                    """)),
            };
            return Task.FromResult(new LlmReply(calls, "", 1.2));
        }
    }

    [Fact]
    public void RecordsCommandsOfficersEventsAndEnd()
    {
        // LLMC_TEST_LOG_DIR を指定すると、ログを消さずに残す(要約スクリプトの確認用)
        var keep = Environment.GetEnvironmentVariable("LLMC_TEST_LOG_DIR");
        var dir = keep ?? Path.Combine(Path.GetTempPath(), "llmc-log-" + Guid.NewGuid().ToString("N"));
        var w = MapLayout.CreateValley();
        for (int i = 0; i < 4; i++) w.SpawnUnit(0, UnitKind.Spearman, new Vector2(40, 30));
        for (int i = 0; i < 3; i++) w.SpawnUnit(0, UnitKind.Cavalry, new Vector2(30, 40));
        var settings = new LlmSettings { OfficerInterval = 2 };
        var cc = new CommandCenter(w, 0, new ScriptedLlm(), null, settings);
        var log = PlayLog.Open(dir, w)!;
        cc.Log = log;
        log.Start(settings);

        var entry = cc.SubmitText("槍は北東の林で受け止めて、騎兵は接触したら突撃", new UtteranceContext());
        for (int i = 0; i < 2000 && entry.Pending; i++)
        {
            Thread.Sleep(5);
            cc.Update();
        }
        for (int i = 0; i < 20 * 8; i++)
        {
            w.Step();
            if (i == 20) for (int k = 0; k < 3; k++) w.SpawnUnit(1, UnitKind.Archer, new Vector2(44, 25));
            if (i % 20 == 0) log.Poll();
            Thread.Sleep(i % 20 == 0 ? 5 : 0);
            cc.Update();
        }
        // 実行中の LLM 呼び出しの結果を取り込みきる
        for (int i = 0; i < 2000 && cc.InFlight > 0; i++)
        {
            Thread.Sleep(5);
            cc.Update();
        }
        log.Snapshot();
        log.End();
        log.Dispose();

        var lines = File.ReadAllLines(log.Path).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var types = lines.Select(l => l.GetProperty("type").GetString()).ToList();
        Assert.Equal("start", types[0]);
        Assert.Equal("snapshot", types[^1]);
        Assert.Contains("end", types);
        Assert.True(types.Contains("officer"), string.Join(",", types));

        var cmd = lines.Single(l => l.GetProperty("type").GetString() == "command");
        Assert.Equal("槍は北東の林で受け止めて、騎兵は接触したら突撃", cmd.GetProperty("utterance").GetString());
        Assert.Contains("北東の林", cmd.GetProperty("prompt").GetString()); // LLM に渡した状況が残る
        Assert.StartsWith("作成", cmd.GetProperty("calls")[0].GetProperty("result").GetString());
        Assert.Equal(1.2, cmd.GetProperty("llm_seconds").GetDouble());

        // 条件の発動が出来事として記録される
        Assert.Contains(lines, l => l.GetProperty("type").GetString() == "event" &&
                                    l.GetProperty("text").GetString()!.StartsWith("条件発動"));
        // 日本語はエスケープせずに書く
        Assert.Contains("槍は北東の林", File.ReadAllText(log.Path));
        if (keep == null) Directory.Delete(dir, true);
    }
}
