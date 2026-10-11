using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Tests.Performance;

/// <summary>明示性能計測の入力規模を指定します。</summary>
internal enum Net10PerformanceCorpusScale
{
    Small,
    Medium,
    Large
}

/// <summary>実測定の規模選択だけを共有します。</summary>
internal static class Net10PerformanceCorpus
{
    /// <summary>未設定・空白時は全規模を返し、明示指定は重複を除く。区切りだけで解析後の集合が空になる指定は拒否する。</summary>
    internal static IReadOnlyList<Net10PerformanceCorpusScale> GetConfiguredScales()
    {
        string? value = Environment.GetEnvironmentVariable("BMS_NET10_PERF_SCALES");
        if (string.IsNullOrWhiteSpace(value))
        {
            return Enum.GetValues<Net10PerformanceCorpusScale>();
        }

        Net10PerformanceCorpusScale[] scales = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => Enum.Parse<Net10PerformanceCorpusScale>(item, ignoreCase: true))
            .Distinct()
            .ToArray();
        if (scales.Length == 0)
        {
            throw new InvalidOperationException("At least one performance corpus scale is required.");
        }
        return scales;
    }
}
