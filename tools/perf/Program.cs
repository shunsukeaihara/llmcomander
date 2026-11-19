// シミュレーションの重さを測る。両軍とも人口上限いっぱい(村人30 + 兵70)で、複数の戦線をぶつける。
//   dotnet run -c Release [-- 秒数]
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using LlmCommander.Command;
using LlmCommander.Sim;

Profiler.Enabled = true;
double seconds = args.Length > 0 && double.TryParse(args[0], out var sec) ? sec : 120;
bool warm = args.Contains("--warmup");
if (warm)
{
    var sw0 = Stopwatch.StartNew();
    Scenarios.Warmup(60);
    Console.WriteLine($"準備の空回し: {sw0.Elapsed.TotalMilliseconds:0}ms");
}
Run(warm ? "空回しのあと" : "1回目(コンパイル前)");
Run("2回目(コンパイル済み)");

void Run(string label)
{
Console.WriteLine($"\n### {label}  GC回数(開始時) {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
Profiler.Totals.Clear();
var w = Scenarios.BigBattle();
Console.WriteLine($"ユニット {w.Units.Count}体で {seconds}秒(ゲーム内)を計測");
var times = new List<double>();
var sw = new Stopwatch();
int steps = (int)(seconds / Defs.Dt);
// 最初の数ステップは JIT コンパイルの時間が入るので捨てる
for (int i = 0; i < 3; i++) w.Step();
Profiler.Totals.Clear();
var worst = new List<(double ms, double t, double paths)>();
for (int i = 0; i < steps && w.Winner == null; i++)
{
    double pathsBefore = Profiler.Totals.GetValueOrDefault("経路探索の回数");
    var before = new Dictionary<string, double>(Profiler.Totals);
    sw.Restart();
    w.Step();
    times.Add(sw.Elapsed.TotalMilliseconds);
    worst.Add((times[^1], w.Time, Profiler.Totals.GetValueOrDefault("経路探索の回数") - pathsBefore));
    if (times[^1] > 10)
        Console.WriteLine($"  !! {w.Time:0.0}秒 {times[^1]:0.0}ms: " + string.Join(" ", Profiler.Totals
            .Where(kv => !kv.Key.EndsWith("回数")).Select(kv => (kv.Key, d: kv.Value - before.GetValueOrDefault(kv.Key)))
            .Where(x => x.d > 0.5).Select(x => $"{x.Key} {x.d:0.0}ms")) + $"  GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    if (i % (20 * 15) == 0)
        Console.WriteLine($"  {w.Time,5:0}秒: ユニット {w.Units.Count,3}体  直近の1ステップ平均 {times.TakeLast(300).Average():0.00}ms  最大 {times.TakeLast(300).Max():0.00}ms");
}
Console.WriteLine("重かったステップ: " + string.Join(", ", worst.OrderByDescending(x => x.ms).Take(5).Select(x => $"{x.t:0.0}秒 {x.ms:0.0}ms(経路探索{x.paths}回)")));
times.Sort();
Console.WriteLine($"1ステップ: 平均 {times.Average():0.00}ms / 中央値 {times[times.Count / 2]:0.00}ms / 99% {times[(int)(times.Count * 0.99)]:0.00}ms / 最大 {times[^1]:0.00}ms");
Console.WriteLine($"20Hz の予算 50ms に対して平均 {times.Average() / 50:P1}");
foreach (var (name, ms) in Profiler.Totals.Where(kv => !kv.Key.EndsWith("回数")).OrderByDescending(kv => kv.Value))
    Console.WriteLine($"  {name,-12} 合計 {ms,8:0}ms  ({ms / times.Sum():P0})");
Console.WriteLine($"  経路探索の回数 {Profiler.Totals.GetValueOrDefault("経路探索の回数"):0}");
Console.WriteLine($"GC回数(終了時) {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}  一時停止合計 {GC.GetTotalPauseDuration().TotalMilliseconds:0}ms");
}

