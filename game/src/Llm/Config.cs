using System;
using System.Collections.Generic;
using System.IO;

namespace LlmCommander.Llm;

/// <summary>
/// 設定の読み込み。環境変数を優先し、なければプロジェクト直下の .env を読む。
///   キーとモデルの一覧は LlmSetup を参照。
/// </summary>
public sealed class Config
{
    private readonly Dictionary<string, string> _values = new();

    public static Config Load(string? dotenvPath)
    {
        var c = new Config();
        if (dotenvPath != null && File.Exists(dotenvPath))
        {
            foreach (var raw in File.ReadAllLines(dotenvPath))
            {
                var line = raw.Trim();
                if (line == "" || line.StartsWith('#')) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                c._values[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
            }
        }
        return c;
    }

    public string? Get(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return _values.TryGetValue(key, out var v) && v != "" ? v : null;
    }

    public string Get(string key, string fallback) => Get(key) ?? fallback;
}
