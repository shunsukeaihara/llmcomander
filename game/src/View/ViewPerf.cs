using System.Collections.Generic;
using System.Diagnostics;

namespace LlmCommander.View;

/// <summary>描画側の処理時間の計測(LLMC_PERF=1 のときだけ集計して5秒ごとに出力)。</summary>
public static class ViewPerf
{
    public static bool Enabled = Godot.OS.GetEnvironment("LLMC_PERF") == "1";
    private static readonly Dictionary<string, (double total, double max, int n)> _t = new();

    public static long Begin() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public static void End(string name, long start)
    {
        if (!Enabled) return;
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var (total, max, n) = _t.GetValueOrDefault(name);
        _t[name] = (total + ms, System.Math.Max(max, ms), n + 1);
    }

    /// <summary>集計を文字列にして消す。</summary>
    public static string Flush(int frames)
    {
        var parts = new List<string>();
        foreach (var (name, (total, max, n)) in _t)
            parts.Add($"{name} 1フレーム平均 {total / System.Math.Max(1, frames):0.00}ms(1回最大 {max:0.0}ms)");
        _t.Clear();
        return string.Join(" / ", parts);
    }
}
