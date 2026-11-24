using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;
using Xunit;

public class StorehouseTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static float WoodIncome(bool withStorehouse)
    {
        var w = MapLayout.CreateValley();
        w.Vision[0].Enabled = false;
        var ts = w.Teams[0];
        for (int i = 0; i < 4; i++) w.SpawnUnit(0, UnitKind.Villager, new Vector2(16, 67));
        ts.VillagersContinuous = false;
        foreach (var k in ts.Gather.Keys.ToList()) ts.Gather[k] = 0;
        ts.Gather[Res.Wood] = 10;
        if (withStorehouse)
        {
            var spot = w.StorehouseSpot(0, Res.Wood)!.Value;
            var site = w.FindBuildSite(BuildingKind.Storehouse, spot)!.Value;
            w.SpawnBuilding(0, BuildingKind.Storehouse, site.X, site.Y);
        }
        for (int i = 0; i < 20 * 40; i++) w.Step();
        float s0 = ts.Stock[Res.Wood];
        for (int i = 0; i < 20 * 90; i++) w.Step();
        return (ts.Stock[Res.Wood] - s0) / 90;
    }

    [Fact]
    public void StorehouseNearForestRaisesWoodIncome()
    {
        float without = WoodIncome(false), with = WoodIncome(true);
        Assert.True(with > without * 1.15f, $"採集所なし {without:0.00}/s、あり {with:0.00}/s");
    }

    [Fact]
    public void StorehouseCandidatesAndProposal()
    {
        var w = MapLayout.CreateValley();
        w.Vision[0].Enabled = false;
        var sites = Context.Sites(w, 0);
        Assert.Contains(sites.Values, c => c.Kind == BuildingKind.Storehouse && c.Note.Contains("森の縁"));
        Assert.Contains(sites.Values, c => c.Kind == BuildingKind.Storehouse && c.Note.Contains("金の資源"));
        Assert.Contains("採集所:", Context.SitesText(sites));

        var r = new Applier(w, 0).Apply(new ToolCall("propose_build", J("""{"building": "storehouse", "near": "gold"}""")), "", Array.Empty<int>(), sites);
        Assert.Contains("採集所", r);
        Assert.Contains(w.Teams[0].Plans, p => p.Kind == BuildingKind.Storehouse);
    }

    [Fact]
    public void EnemyBuildsStorehouseForLongHauls()
    {
        var w = MapLayout.CreateValley();
        var ai = new EnemyAI(w);
        for (int i = 0; i < 20 * 400 && !w.TeamBuildings(1).Any(b => b.Kind == BuildingKind.Storehouse); i++)
        {
            w.Step();
            ai.Step();
        }
        Assert.Contains(w.TeamBuildings(1), b => b.Kind == BuildingKind.Storehouse);
    }
}
