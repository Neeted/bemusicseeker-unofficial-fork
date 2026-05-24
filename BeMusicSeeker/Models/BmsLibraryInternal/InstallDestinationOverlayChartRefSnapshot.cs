using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class InstallDestinationOverlayChartRefSnapshot
{
    private static readonly InstallDestinationOverlayChartRefSnapshot empty = new([]);

    private readonly List<LibraryChartRef> chartRefs;

    private InstallDestinationOverlayChartRefSnapshot(List<LibraryChartRef> chartRefs)
    {
        this.chartRefs = chartRefs ?? [];
    }

    internal static InstallDestinationOverlayChartRefSnapshot Empty => empty;

    internal static InstallDestinationOverlayChartRefSnapshot FromCharts(IEnumerable<ChartFile> charts)
    {
        return FromLibraryChartRefs((charts ?? [])
            .Select(LibraryChartRef.FromChartFile)
            .Where(chart => chart != null));
    }

    internal List<LibraryChartRef> ToList()
    {
        return [.. chartRefs];
    }

    private static InstallDestinationOverlayChartRefSnapshot FromLibraryChartRefs(IEnumerable<LibraryChartRef> charts)
    {
        List<LibraryChartRef> refs = [.. (charts ?? [])
            .Where(chart => chart != null)
            .GroupBy(CreateRuntimeKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => group.First())];
        return refs.Count == 0
            ? Empty
            : new InstallDestinationOverlayChartRefSnapshot(refs);
    }

    private static string CreateRuntimeKey(LibraryChartRef chart)
    {
        if (chart == null)
        {
            return null;
        }

        ChartFileKind chartKind = chart.Kind == LibraryChartKind.Bmson ? ChartFileKind.Bmson : ChartFileKind.Bms;
        string primaryKey = ChartFileRuntimeStateKey.Create(chartKind, chart.Path, chart.Md5, chart.Sha256);
        if (!string.IsNullOrWhiteSpace(primaryKey))
        {
            return primaryKey;
        }

        return ChartFileRuntimeStateKey.CreatePathKey(chartKind, chart.Path);
    }
}
