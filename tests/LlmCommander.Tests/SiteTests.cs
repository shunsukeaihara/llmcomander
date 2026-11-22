using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;
using Xunit.Abstractions;

public class SiteTests
{
    private readonly ITestOutputHelper _out;
    public SiteTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void CandidatesAreListedAndUsedForProposals()
    {
        var w = MapLayout.CreateValley();
        var sites = Context.Sites(w, 0);
        string text = Context.SitesText(sites);
        _out.WriteLine(text);

        var tcs = sites.Where(kv => kv.Value.Kind == BuildingKind.TownCenter).ToList();
        var barracks = sites.Where(kv => kv.Value.Kind == BuildingKind.Barracks).ToList();
        Assert.InRange(tcs.Count, 3, 5);
        Assert.InRange(barracks.Count, 2, 4);
        // 自軍の基地の金鉱の横には村の中心の候補を出さない(既に村の中心がある)
        Assert.DoesNotContain(tcs, kv => Vector2.Distance(kv.Value.Center, w.BasePos(0)) < 12);
        // 北の金鉱のそばの候補がある
        Assert.Contains(tcs, kv => kv.Value.Region == "北の金鉱" && kv.Value.ToGold <= 6);
        Assert.Contains("候補地1", Context.Commander(w, 0, new UtteranceContext(), "金の近くに村の中心", sites));

        // site で選んだ候補地に仮置きされる
        var (name, cand) = tcs.First(kv => kv.Value.Region == "北の金鉱");
        w.Teams[0].Stock[Res.Wood] = 1000;
        var r = new Applier(w, 0).Apply(new ToolCall("propose_build",
            JsonDocument.Parse($$"""{"building": "town_center", "site": "{{name}}"}""").RootElement.Clone()), "", Array.Empty<int>(), sites);
        _out.WriteLine(r);
        var plan = w.Teams[0].Plans.Single();
        Assert.Equal(PlanStatus.Proposed, plan.Status);
        Assert.True(Vector2.Distance(plan.Center, cand.Center) < 1.5f);
        Assert.Contains(name, plan.Near);

        // 存在しない候補地だけを指定されたら、建てずに理由を返す
        var bad = new Applier(w, 0).Apply(new ToolCall("propose_build",
            JsonDocument.Parse("""{"building": "barracks", "site": "候補地99"}""").RootElement.Clone()), "", Array.Empty<int>(), sites);
        Assert.Contains("見つかりません", bad);
    }

    [Fact]
    public void EnemySightingsAreShownOnNearbyCandidates()
    {
        var w = MapLayout.CreateValley();
        // 北の金鉱のそばで敵兵を見たことにする
        for (int i = 0; i < 3; i++) w.SpawnUnit(1, UnitKind.Spearman, new Vector2(27 + i * 0.5f, 25));
        w.SpawnUnit(0, UnitKind.Cavalry, new Vector2(25, 30));
        for (int i = 0; i < 10; i++) w.Step();
        var north = Context.Sites(w, 0).Values.First(c => c.Kind == BuildingKind.TownCenter && c.Region == "北の金鉱");
        Assert.True(north.EnemiesSeenNearby >= 3);
    }
}

public class SiteFilterTests
{
    [Fact]
    public void NoTownCenterCandidatesNextToTheEnemyBase()
    {
        var w = MapLayout.CreateValley();
        var tcs = Context.Sites(w, 0).Values.Where(c => c.Kind == BuildingKind.TownCenter);
        Assert.All(tcs, c => Assert.True(c.FromEnemyBase > 15, $"{c.Region} は敵基地から {c.FromEnemyBase}"));
    }
}

public class LatestPlanTests
{
    [Fact]
    public void TheNewestProposalIsMarked()
    {
        var w = MapLayout.CreateValley();
        var ap = new Applier(w, 0);
        ap.Apply(new ToolCall("propose_build", JsonDocument.Parse("""{"building": "town_center", "near": "北の金鉱"}""").RootElement.Clone()), "", Array.Empty<int>());
        ap.Apply(new ToolCall("propose_build", JsonDocument.Parse("""{"building": "barracks", "near": "own_base"}""").RootElement.Clone()), "", Array.Empty<int>());
        var text = Context.EconomyText(w, w.Teams[0]);
        var lines = text.Split('\n').Where(l => l.StartsWith("建設計画")).ToList();
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain("直前の提案", lines[0]);
        Assert.Contains("兵舎", lines[1]);
        Assert.Contains("直前の提案", lines[1]);
    }
}
