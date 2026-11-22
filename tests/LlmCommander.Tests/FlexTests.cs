using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

/// <summary>場所の相対表現、目印の道具、作戦の担当による規則の変更と分割。</summary>
public class FlexTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static World WithMarker(out Marker m)
    {
        var w = MapLayout.CreateValley();
        m = new Marker { Name = "指示点1", Points = { new Vector2(24, 56) } };
        w.Teams[0].Markers.Add(m);
        return w;
    }

    [Fact]
    public void RelativePlaces()
    {
        var w = WithMarker(out var m);
        var center = new Vector2(40, 40);
        var toward = w.ResolvePlace(0, "指示点1から中央へ3マス")!;
        Assert.InRange(Vector2.Distance(m.Points[0], toward.Center), 2, 4);
        Assert.True(Vector2.Distance(toward.Center, center) < Vector2.Distance(m.Points[0], center));

        var little = w.ResolvePlace(0, "指示点1から中央へ少し")!;
        Assert.InRange(Vector2.Distance(m.Points[0], little.Center), 2, 4);

        // 北は画面の上 = x と y が両方減る向き
        var north = w.ResolvePlace(0, "指示点1の北5マス")!;
        Assert.True(north.Center.X < 24 && north.Center.Y < 56);

        // 敵側 = 自軍基地から地図の中央へ
        var fwd = w.ResolvePlace(0, "指示点1の敵側4マス")!;
        Assert.True(Vector2.Distance(fwd.Center, center) < Vector2.Distance(m.Points[0], center));

        var mid = w.ResolvePlace(0, "指示点1と中央の間")!;
        Assert.InRange(Vector2.Distance(mid.Center, (m.Points[0] + center) / 2), 0, 1.5f);

        var line = w.ResolvePlace(0, "指示点1〜北の丘")!;
        Assert.True(line.IsLine);

        // 解決した名前(かっこ付き)を、もう一度書いても同じ場所になる
        var again = w.ResolvePlace(0, toward.Name)!;
        Assert.Equal(toward.Center, again.Center);

        Assert.Null(w.ResolvePlace(0, "どこか知らない場所"));
    }

    [Fact]
    public void UnitPositionPlaces()
    {
        var w = MapLayout.CreateValley();
        for (int i = 0; i < 4; i++) w.SpawnUnit(0, UnitKind.Spearman, new Vector2(30, 46));
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "前線", "change_summary": "", "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "中央"}]}
            """)), "", Array.Empty<int>());
        var a = w.ResolvePlace(0, "槍の位置")!;
        var b = w.ResolvePlace(0, "前線/槍の位置")!;
        Assert.InRange(Vector2.Distance(a.Center, new Vector2(30, 46)), 0, 1.5f);
        Assert.Equal(a.Center, b.Center);
        var op = w.Teams[0].FindOp("前線")!;
        Assert.NotNull(w.ResolvePlace(0, "現在地", op));
        Assert.Null(w.ResolvePlace(0, "現在地"));
    }

    [Fact]
    public void SetMarkerMovesMarkerAndTheRolesUsingIt()
    {
        var w = WithMarker(out var m);
        for (int i = 0; i < 4; i++) w.SpawnUnit(0, UnitKind.Spearman, new Vector2(30, 46));
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "守り", "change_summary": "", "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "指示点1"}]}
            """)), "", Array.Empty<int>());
        var before = m.Points[0];
        var r = ap.Apply(new ToolCall("set_marker", J("""{"name": "指示点1", "place": "指示点1から中央へ3マス"}""")), "", Array.Empty<int>());
        Assert.Contains("動かす", r);
        Assert.NotEqual(before, m.Points[0]);
        Assert.Equal(m.Points[0], w.Teams[0].FindOp("守り")!.FindRole("槍")!.Anchor.Center);

        // 部隊の位置に新しい目印を置く
        ap.Apply(new ToolCall("set_marker", J("""{"name": "集合地点C", "place": "槍の位置"}""")), "", Array.Empty<int>());
        Assert.Contains(w.Teams[0].Markers, x => x.Name == "集合地点C");
        ap.Apply(new ToolCall("delete_marker", J("""{"name": "集合地点C"}""")), "", Array.Empty<int>());
        Assert.DoesNotContain(w.Teams[0].Markers, x => x.Name == "集合地点C");
    }

    [Fact]
    public void GoalIsKeptAndOfficerCanSplitAndRelocate()
    {
        var w = MapLayout.CreateValley();
        for (int i = 0; i < 6; i++) w.SpawnUnit(0, UnitKind.Spearman, new Vector2(30, 46));
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "前線", "goal": "中央で受け止め、一部で右から回り込む", "change_summary": "",
             "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "中央"}]}
            """)), "槍で中央を守って、何体か右から回して", Array.Empty<int>());
        var op = w.Teams[0].FindOp("前線")!;
        Assert.Equal("中央で受け止め、一部で右から回り込む", op.Goal);
        Assert.Equal(0, op.OfficerDue);

        ap.ApplyOfficer(op, new ToolCall("split_role", J("""
            {"from_role": "槍", "count": 2, "new_role": "槍(右翼)", "stance": "advance", "place": "中央の東5マス", "reason": "右から回り込む"}
            """)));
        Assert.Equal(4, w.Members(op, op.FindRole("槍")!).Count());
        var wing = op.FindRole("槍(右翼)")!;
        Assert.Equal(2, w.Members(op, wing).Count());
        Assert.Equal(Stance.Advance, wing.Stance);

        // 担当は「現在地」で、その場に止める
        var r = ap.ApplyOfficer(op, new ToolCall("adjust_role", J("""{"role": "槍", "place": "現在地", "reason": "その場で待つ"}""")));
        Assert.Equal("", r);
    }

    /// <summary>考えている間に作戦が変わった担当の判断は捨てる。</summary>
    private sealed class SlowOfficer : ILlm
    {
        public readonly SemaphoreSlim Gate = new(0);
        public int Calls;
        public async Task<LlmReply> Call(LlmRequest request, Action<ToolCall>? onToolCall = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            await Gate.WaitAsync(ct);
            var calls = new[]
            {
                new ToolCall("adjust_role", J("""{"role": "槍", "stance": "retreat", "place": "own_base", "reason": "古い判断"}""")),
                new ToolCall("report", J("""{"status": "様子見"}""")),
            };
            return new LlmReply(calls, "", 0.5);
        }
    }

    [Fact]
    public void StaleOfficerDecisionsAreDropped()
    {
        var w = MapLayout.CreateValley();
        for (int i = 0; i < 4; i++) w.SpawnUnit(0, UnitKind.Spearman, new Vector2(30, 46));
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "前線", "change_summary": "", "roles": [{"role": "槍", "unit_kind": "spearman", "stance": "hold", "place": "中央"}]}
            """)), "中央を守れ", Array.Empty<int>());
        var llm = new SlowOfficer();
        var cc = new CommandCenter(w, 0, llm, null, new LlmSettings());
        cc.Update(); // 担当を呼ぶ(返事は止めてある)
        for (int i = 0; i < 200 && Volatile.Read(ref llm.Calls) == 0; i++) Thread.Sleep(5);
        Assert.Equal(1, llm.Calls);
        var op = w.Teams[0].FindOp("前線")!;
        // 返事が来る前にプレイヤーが作戦を変える
        ap.Apply(new ToolCall("update_operation", J("""
            {"operation": "前線", "change_summary": "", "roles": [{"role": "槍", "stance": "advance", "place": "北の丘"}]}
            """)), "北の丘へ進め", Array.Empty<int>());
        llm.Gate.Release();
        for (int i = 0; i < 500 && op.Status == ""; i++) { Thread.Sleep(5); cc.Update(); }
        Assert.Equal(Stance.Advance, op.FindRole("槍")!.Stance); // 古い判断(撤退)は捨てた
        Assert.Equal("様子見", op.Status);                      // 報告は使う
        llm.Gate.Release(5);
    }
}
