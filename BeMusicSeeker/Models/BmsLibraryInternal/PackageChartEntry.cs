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

    private string installDestination;

    private string installDestinationTitle;

    private string installDestinationArtist;

    private bool hasInstallDestinationProjection;

    internal PackageChartEntry(ChartFile chart, BMSFile compatibilityAdapter = null)
    {
        this.chart = chart ?? throw new ArgumentNullException(nameof(chart));
        this.compatibilityAdapter = compatibilityAdapter;
        if (compatibilityAdapter == null && chart.BmsFile == null)
        {
            ReplacePendingWarnings(chart.Warnings);
            ReplacePendingInstallDestination(chart.InstallDestination, chart.InstallDestinationTitle, chart.InstallDestinationArtist);
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
            return hasPendingWarningProjection || hasInstallDestinationProjection
                ? ChartFileProjection.WithPackageState(
                    chart,
                    hasInstallDestinationProjection ? installDestination : chart.InstallDestination,
                    hasInstallDestinationProjection ? installDestinationTitle : chart.InstallDestinationTitle,
                    hasInstallDestinationProjection ? installDestinationArtist : chart.InstallDestinationArtist,
                    hasPendingWarningProjection ? [.. pendingWarnings.Values] : chart.Warnings)
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
            if (hasInstallDestinationProjection)
            {
                compatibilityAdapter.instl_dst = string.IsNullOrWhiteSpace(installDestination) ? null : installDestination;
                compatibilityAdapter.InstallDestinationTitle = installDestinationTitle ?? string.Empty;
                compatibilityAdapter.InstallDestinationArtist = installDestinationArtist ?? string.Empty;
            }
        }
        return compatibilityAdapter;
    }

    internal void ApplyInstallDestination(string destinationDirectory, string title, string artist, bool preserveAmbiguousInstallContext = false)
    {
        BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
        if (writebackFile != null)
        {
            writebackFile.instl_dst = destinationDirectory;
            writebackFile.InstallDestinationTitle = title ?? string.Empty;
            writebackFile.InstallDestinationArtist = artist ?? string.Empty;
            writebackFile.IsInstallDestinationSuggestionPopupOpen = false;
            if (!preserveAmbiguousInstallContext)
            {
                writebackFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
                writebackFile.InstallDestinationSuggestions = [];
            }
            return;
        }
        ReplacePendingInstallDestination(destinationDirectory, title, artist, forceProjection: true);
        if (!preserveAmbiguousInstallContext)
        {
            ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        }
    }

    internal void ClearInstallDestination()
    {
        BMSFile writebackFile = compatibilityAdapter ?? chart.BmsFile;
        if (writebackFile != null)
        {
            writebackFile.instl_dst = null;
            writebackFile.InstallDestinationTitle = string.Empty;
            writebackFile.InstallDestinationArtist = string.Empty;
            writebackFile.InstallDestinationSuggestions = [];
            writebackFile.IsInstallDestinationSuggestionPopupOpen = false;
            writebackFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            return;
        }
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
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

    private void ReplacePendingInstallDestination(string destinationDirectory, string title, string artist, bool forceProjection = false)
    {
        installDestination = destinationDirectory ?? string.Empty;
        installDestinationTitle = title ?? string.Empty;
        installDestinationArtist = artist ?? string.Empty;
        hasInstallDestinationProjection = forceProjection
            ||
            !string.IsNullOrWhiteSpace(installDestination)
            || !string.IsNullOrWhiteSpace(installDestinationTitle)
            || !string.IsNullOrWhiteSpace(installDestinationArtist);
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
