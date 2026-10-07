using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>確定した保存対象の共通値です。DB行や変更可能な解析元を保持しません。</summary>
internal sealed class ChartStorageTargetSet
{
    private ChartStorageTargetSet(IEnumerable<ChartFile> charts)
    {
        Charts = Array.AsReadOnly(charts.ToArray());
        BmsCharts = Array.AsReadOnly(Charts.Where(chart => chart.Kind == ChartFileKind.Bms).ToArray());
        BmsonCharts = Array.AsReadOnly(Charts.Where(chart => chart.Kind == ChartFileKind.Bmson).ToArray());
    }

    /// <summary>確定したBMS形式の保存対象です。</summary>
    internal IReadOnlyList<ChartFile> BmsCharts { get; }

    /// <summary>確定したbmson形式の保存対象です。</summary>
    internal IReadOnlyList<ChartFile> BmsonCharts { get; }

    /// <summary>確定したpathと解析値、共有する短命なリソース結果を持つ対象です。</summary>
    internal IReadOnlyList<ChartFile> Charts { get; }

    /// <summary>対象の物理フォルダを重複なしで返します。</summary>
    internal List<string> GetDistinctChartDirectories() => [.. Charts
        .Select(chart => chart.Path)
        .Select(DirectoryExt.GetDirectoryNameSimple)
        .Where(directory => !string.IsNullOrWhiteSpace(directory))
        .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>入力の共通値を固定します。bmsonの集約はDBのexact pathに限ります。</summary>
    internal static ChartStorageTargetSet FromCharts(IEnumerable<ChartFile> charts)
    {
        List<ChartFile> bmsCharts = [];
        var bmsonCharts = new Dictionary<string, ChartFile>(StringComparer.Ordinal);
        foreach (ChartFile chart in charts ?? [])
        {
            if (chart == null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(chart.Path) || string.IsNullOrWhiteSpace(chart.Md5))
            {
                throw new InvalidOperationException("Owned chart persistence requires a path and MD5.");
            }
            if (chart.Kind == ChartFileKind.Bms)
            {
                bmsCharts.Add(chart);
            }
            else
            {
                bmsonCharts[chart.Path] = chart;
            }
        }
        return new ChartStorageTargetSet(bmsCharts.Concat(bmsonCharts.Values));
    }

    /// <summary>preflightで確定した導入先の共通値を、live entryを変更せず固定します。</summary>
    internal static ChartStorageTargetSet FromInstalledCharts(IEnumerable<ChartFile> charts) => FromCharts(charts);
}
