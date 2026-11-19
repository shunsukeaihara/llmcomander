using System.Collections.Generic;
using System.Diagnostics;

namespace LlmCommander.Sim;

/// <summary>処理の段階ごとの時間の計測(tools/perf 用)。Enabled が false のときはほぼ何もしない。</summary>
public static class Profiler
{
    public static bool Enabled;
    public static readonly Dictionary<string, double> Totals = new();

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(string name, long start)
    {
        if (!Enabled) return;
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Totals[name] = Totals.GetValueOrDefault(name) + ms;
    }
}
