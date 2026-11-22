// 司令LLMと担当LLMの応答時間と出力を測る。ゲーム画面は使わない。
//   dotnet run -- [発話 ...]    (省略時は用意した発話を順に流す)
// 設定は game/.env か環境変数(OPENAI_API_KEY ほか。一覧は game/src/Llm/LlmSetup.cs)。
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Llm;
using LlmCommander.Sim;
using LlmCommander.View;

var config = Config.Load(Path.Combine(AppContext.BaseDirectory, "../../../../../game/.env"));
var setup = LlmSetup.From(config);
if (setup.Llm == null) { Console.Error.WriteLine(setup.Problem); return 1; }
var llm = setup.Llm;
var settings = setup.Settings;
Console.WriteLine($"LLM: {setup.Provider} / 司令役 {settings.CommanderModel} / 担当 {settings.OfficerModel}");

// 交戦が始まった直後の場面を作る
var w = MapLayout.CreateValley();
DemoScenario.Setup(w);
var front = w.Teams[0].FindOp("戦線A")!;
for (int i = 0; i < 20 * 90 && !w.Members(front).Any(u => w.Time - u.LastFoughtAt < 1); i++) w.Step();
for (int i = 0; i < 20 * 3; i++) w.Step();

string[] utterances = args.Length > 0 ? args : new[]
{
    "弓は引き撃ち、他は撤退",
    "騎兵は右から回り込んで敵の弓を狙え",
    "木のうち2人を食料に回して",
    "兵舎をもう一つ、基地の近くに",
    "OK",
    "新しく作った槍は戦線Aに送って、槍は常に作り続けて",
    "槍の半分を北の金鉱の守りに回せ",
};

var applier = new Applier(w, 0);
var times = new List<double>();
var firsts = new List<double>();
foreach (var u in utterances)
{
    var user = Context.Commander(w, 0, new UtteranceContext(), u);
    var req = new LlmRequest(settings.CommanderModel, settings.CommanderEffort, Tools.CommanderSystem, user, Tools.CommanderTools);
    var sw = Stopwatch.StartNew();
    double? first = null;
    // ゲームと同じく、ストリーミングで受け取って最初の命令が完成した時刻を測る
    var reply = await llm.Call(req, _ => first ??= sw.Elapsed.TotalSeconds);
    times.Add(sw.Elapsed.TotalSeconds);
    if (first is { } f) firsts.Add(f);
    Console.WriteLine($"\n=== 「{u}」 最初の命令 {(first is { } f2 ? f2.ToString("0.00") + "秒" : "なし")} / 完了 {sw.Elapsed.TotalSeconds:0.00}秒 ({settings.CommanderModel}, effort {settings.CommanderEffort})");
    if (reply.Error != null) Console.WriteLine("エラー: " + reply.Error);
    foreach (var c in reply.Calls)
    {
        Console.WriteLine($"  {c.Name} {JsonSerializer.Serialize(c.Input, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}");
        Console.WriteLine($"    → {applier.Apply(c, u, Array.Empty<int>())}");
    }
    for (int i = 0; i < 20 * 2; i++) w.Step();
}

// 担当LLMを1回
var officerReq = new LlmRequest(settings.OfficerModel, settings.OfficerEffort, Tools.OfficerSystem,
    Context.Officer(w, front, new[] { "槍が3体やられた" }), Tools.OfficerTools, 1500);
var osw = Stopwatch.StartNew();
var oreply = await llm.Call(officerReq);
Console.WriteLine($"\n=== 担当LLM({settings.OfficerModel}) {osw.Elapsed.TotalSeconds:0.00}秒 {oreply.Error}");
foreach (var c in oreply.Calls) Console.WriteLine($"  {c.Name} {c.Input}");

Console.WriteLine($"\n司令LLM 完了まで: 平均 {times.Average():0.00}秒 / 最大 {times.Max():0.00}秒 / 最小 {times.Min():0.00}秒");
if (firsts.Count > 0)
    Console.WriteLine($"司令LLM 最初の命令まで: 平均 {firsts.Average():0.00}秒 / 最大 {firsts.Max():0.00}秒 / 最小 {firsts.Min():0.00}秒");
return 0;
