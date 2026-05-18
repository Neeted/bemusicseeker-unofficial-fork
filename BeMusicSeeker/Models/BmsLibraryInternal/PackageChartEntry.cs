using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry
{
    private readonly ChartFile chart;

    private BMSFile compatibilityAdapter;

    private readonly Dictionary<ChartWarningKind, ChartWarning> pendingWarnings = [];

    private bool hasPendingWarningProjection;

    internal PackageChartEntry(ChartFile chart, BMSFile compatibilityAdapter = null)
    {
        this.chart = chart ?? throw new ArgumentNullException(nameof(chart));
        this.compatibilityAdapter = compatibilityAdapter;
        if (compatibilityAdapter == null && chart.BmsFile == null)
        {
            ReplacePendingWarnings(chart.Warnings);
        }
    }

    internal PackageChartEntry(BMSFile compatibilityAdapter)
        : this(ChartFileProjection.FromBmsFile(compatibilityAdapter), compatibilityAdapter)
    {
    }

    internal ChartFile Chart
    {
        get
        {
            BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
            if (writebackFile != null)
            {
                return ChartFileProjection.FromBmsFile(writebackFile);
            }
            return hasPendingWarningProjection
                ? ChartFileProjection.WithWarnings(chart, [.. pendingWarnings.Values])
                : chart;
        }
    }

    internal BMSFile CompatibilityAdapter => compatibilityAdapter;

    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    internal static PackageChartEntry FromCompatibilityAdapter(BMSFile compatibilityAdapter)
    {
        return compatibilityAdapter == null ? null : new PackageChartEntry(compatibilityAdapter);
    }

    internal static PackageChartEntry FromChart(ChartFile chart)
    {
        return chart == null ? null : new PackageChartEntry(chart);
    }

    internal BMSFile GetOrCreateCompatibilityAdapter()
    {
        if (compatibilityAdapter != null)
        {
            return compatibilityAdapter;
        }
        if (Chart.Kind == ChartFileKind.Bmson && Chart.BmsonSong != null)
        {
            compatibilityAdapter = PendingChartEntry.CreateFromBmsonSong(Chart.BmsonSong);
            if (pendingWarnings.Count > 0)
            {
                compatibilityAdapter.ReplaceStructuredWarnings(pendingWarnings.Values);
            }
        }
        return compatibilityAdapter;
    }

    internal void ClearStructuredWarnings()
    {
        BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
        if (writebackFile != null)
        {
            writebackFile.ClearStructuredWarnings();
            return;
        }
        pendingWarnings.Clear();
        hasPendingWarningProjection = true;
    }

    internal void ClearWarningsByCategory(ChartWarningCategory category)
    {
        BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
        if (writebackFile != null)
        {
            writebackFile.ClearWarningsByCategory(category);
            return;
        }
        foreach (ChartWarningKind kind in pendingWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            pendingWarnings.Remove(kind);
        }
        hasPendingWarningProjection = true;
    }

    internal void SetWarning(ChartWarningKind kind, string message)
    {
        BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
        if (writebackFile != null)
        {
            writebackFile.SetWarning(kind, message);
            return;
        }
        ChartWarning warning = ChartWarning.Create(kind, message);
        pendingWarnings[warning.Kind] = warning;
        hasPendingWarningProjection = true;
    }

    internal void ReplaceWarningsByCategory(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
        BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
        if (writebackFile != null)
        {
            writebackFile.ReplaceWarningsByCategory(category, warnings);
            return;
        }
        foreach (ChartWarningKind kind in pendingWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            pendingWarnings.Remove(kind);
        }
        foreach (ChartWarning warning in warnings ?? [])
        {
            if (warning != null && warning.Category == category)
            {
                pendingWarnings[warning.Kind] = warning;
            }
        }
        hasPendingWarningProjection = true;
    }

    private void ReplacePendingWarnings(IEnumerable<ChartWarning> warnings)
    {
        pendingWarnings.Clear();
        foreach (ChartWarning warning in warnings ?? [])
        {
            if (warning != null)
            {
                pendingWarnings[warning.Kind] = warning;
            }
        }
        hasPendingWarningProjection = pendingWarnings.Count > 0;
    }

    internal static PackageChartEntry FromPath(string filePath)
    {
        try
        {
            if (PendingChartEntry.IsBmsonFilePath(filePath))
            {
                return FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(filePath)));
            }
            return FromCompatibilityAdapter(PendingChartEntry.CreateFromFilePath(filePath));
        }
        catch
        {
            return null;
        }
    }

}
