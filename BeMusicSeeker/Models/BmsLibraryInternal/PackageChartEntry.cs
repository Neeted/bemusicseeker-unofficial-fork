using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry
{
    private ChartFile chart;

    private readonly Dictionary<ChartWarningKind, ChartWarning> pendingWarnings = [];

    private bool hasPendingWarningProjection;

    private string installDestination;

    private string installDestinationTitle;

    private string installDestinationArtist;

    private IReadOnlyList<string> installDestinationSuggestions = [];

    private bool hasInstallDestinationProjection;

    internal PackageChartEntry(ChartFile chart)
    {
        this.chart = chart ?? throw new ArgumentNullException(nameof(chart));
        if (GetBmsStorageOwner() == null)
        {
            ReplacePendingWarnings(chart.Warnings);
            ReplacePendingInstallDestination(chart.InstallDestination, chart.InstallDestinationTitle, chart.InstallDestinationArtist, chart.InstallDestinationSuggestions);
        }
    }

    internal PackageChartEntry(BMSFile bmsFile)
        : this(ChartFileProjection.FromBmsFile(bmsFile))
    {
    }

    internal ChartFile Chart
    {
        get
        {
            BMSFile bmsFile = GetBmsStorageOwner();
            if (bmsFile != null)
            {
                return ChartFileProjection.FromBmsFile(bmsFile);
            }
            return hasPendingWarningProjection || hasInstallDestinationProjection
                ? ChartFileProjection.WithPackageState(
                    chart,
                    hasInstallDestinationProjection ? installDestination : chart.InstallDestination,
                    hasInstallDestinationProjection ? installDestinationTitle : chart.InstallDestinationTitle,
                    hasInstallDestinationProjection ? installDestinationArtist : chart.InstallDestinationArtist,
                    hasInstallDestinationProjection ? installDestinationSuggestions : chart.InstallDestinationSuggestions,
                    hasPendingWarningProjection ? [.. pendingWarnings.Values] : chart.Warnings)
                : chart;
        }
    }

    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    internal PackageChartEntry ToChartEntrySnapshot()
    {
        ChartFile currentChart = Chart;
        if (currentChart == null)
        {
            return null;
        }

        BMSFile bmsFile = currentChart.GetBmsStorageOwner();
        return bmsFile != null
            ? FromBmsFile(bmsFile)
            : FromChart(currentChart);
    }

    internal bool IsSameChartTarget(PackageChartEntry targetEntry)
    {
        if (targetEntry?.Chart == null)
        {
            return false;
        }
        if (ReferenceEquals(this, targetEntry))
        {
            return true;
        }
        return IsSameChartTarget(Chart, targetEntry.Chart);
    }

    internal bool IsSameChartTarget(ChartFile targetChart)
    {
        return IsSameChartTarget(Chart, targetChart);
    }

    internal static bool IsSameChartTarget(ChartFile chart, ChartFile targetChart)
    {
        if (chart == null || targetChart == null || chart.Kind != targetChart.Kind)
        {
            return false;
        }
        if (ReferenceEquals(chart, targetChart))
        {
            return true;
        }
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        BMSFile targetBmsFile = targetChart.GetBmsStorageOwner();
        if (bmsFile != null
            && targetBmsFile != null
            && ReferenceEquals(bmsFile, targetBmsFile))
        {
            return true;
        }
        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        LR2SongDBExtended.bmson_song targetBmsonSong = targetChart.GetBmsonStorageOwner();
        if (bmsonSong != null
            && targetBmsonSong != null
            && ReferenceEquals(bmsonSong, targetBmsonSong))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(chart.Path)
            && !string.IsNullOrWhiteSpace(targetChart.Path)
            && chart.Path.Equals(targetChart.Path, StringComparison.OrdinalIgnoreCase);
    }

    internal PackageChartInstallDestinationState CaptureInstallDestinationState()
    {
        ChartFile currentChart = Chart;
        return new PackageChartInstallDestinationState(
            currentChart?.InstallDestination,
            currentChart?.InstallDestinationTitle ?? string.Empty,
            currentChart?.InstallDestinationArtist ?? string.Empty,
            currentChart?.InstallDestinationSuggestions ?? []);
    }

    internal void RestoreInstallDestinationState(PackageChartInstallDestinationState state)
    {
        ReplaceInstallDestinationState(
            state?.Destination,
            state?.Title ?? string.Empty,
            state?.Artist ?? string.Empty,
            state?.Suggestions ?? []);
    }

    internal void SetInstallDestinationPathOnly(string destinationDirectory)
    {
        PackageChartInstallDestinationState state = CaptureInstallDestinationState();
        ReplaceInstallDestinationState(destinationDirectory, state.Title, state.Artist, state.Suggestions);
    }

    internal static PackageChartEntry FromBmsFile(BMSFile bmsFile)
    {
        return bmsFile == null ? null : new PackageChartEntry(bmsFile);
    }

    internal static PackageChartEntry FromChart(ChartFile chart)
    {
        return chart == null ? null : new PackageChartEntry(chart);
    }

    internal bool HasInstallDestinationSuggestion(string destinationDirectory)
    {
        return !string.IsNullOrWhiteSpace(destinationDirectory)
            && (Chart.InstallDestinationSuggestions?.Any(path => string.Equals(path, destinationDirectory, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    internal bool HasLowConfidenceInstallEstimationWarning()
    {
        return (Chart.Warnings ?? []).Any(warning => warning != null && warning.Category == ChartWarningCategory.InstallEstimation && warning.Kind != ChartWarningKind.InstalledDestinationResolveFailed);
    }

    internal void SetSearchingStatus(bool isSearching)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile == null)
        {
            return;
        }
        if (isSearching)
        {
            bmsFile.status |= BMSFile.BMSFileStatus.SEARCHING;
        }
        else
        {
            bmsFile.status &= ~BMSFile.BMSFileStatus.SEARCHING;
        }
    }

    internal void ApplyInstalledPath(string installedPath)
    {
        if (string.IsNullOrWhiteSpace(installedPath))
        {
            return;
        }

        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.path = installedPath;
            ClearInstalledBmsMetadata(bmsFile);
            chart = ChartFileProjection.FromBmsFile(bmsFile);
            return;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            bmsonSong.path = installedPath;
            bmsonSong.folder = Path.GetDirectoryName(installedPath) ?? string.Empty;
            bmsonSong.MaintenanceInfo?.NormalizeForBmson(bmsonSong.path, bmsonSong.md5);
            chart = ChartFileProjection.FromBmsonSong(bmsonSong);
        }
    }

    internal void ApplyInstallDestination(string destinationDirectory, string title, string artist, bool preserveAmbiguousInstallContext = false)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.instl_dst = destinationDirectory;
            bmsFile.InstallDestinationTitle = title ?? string.Empty;
            bmsFile.InstallDestinationArtist = artist ?? string.Empty;
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
            if (!preserveAmbiguousInstallContext)
            {
                bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
                bmsFile.InstallDestinationSuggestions = [];
            }
            return;
        }
        ReplacePendingInstallDestination(
            destinationDirectory,
            title,
            artist,
            preserveAmbiguousInstallContext ? installDestinationSuggestions : [],
            forceProjection: true);
        if (!preserveAmbiguousInstallContext)
        {
            ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        }
    }

    internal void ApplyInstallDestinationMetadata(string destinationDirectory, string title, string artist)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.instl_dst = destinationDirectory;
            bmsFile.InstallDestinationTitle = title ?? string.Empty;
            bmsFile.InstallDestinationArtist = artist ?? string.Empty;
            return;
        }
        ReplacePendingInstallDestination(
            destinationDirectory,
            title,
            artist,
            installDestinationSuggestions,
            forceProjection: true);
    }

    internal void ApplyInstallEstimationResult(InstallEstimationResult result)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            ApplyInstallEstimationResultToFile(bmsFile, result);
            return;
        }

        InstallEstimationCandidate selectedCandidate = result?.SelectedCandidate;
        string[] suggestionPaths = [.. (result?.SuggestedDestinationDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)];
        bool isLowConfidence = result?.Confidence == InstallEstimationConfidence.Low;
        ReplacePendingInstallDestination(
            result?.ShouldAutoApplyDestination == true && !string.IsNullOrWhiteSpace(result.DestinationDirectory)
                ? result.DestinationDirectory
                : null,
            result?.HasViableDestination == true ? selectedCandidate?.RepresentativeTitle ?? string.Empty : string.Empty,
            result?.HasViableDestination == true ? selectedCandidate?.RepresentativeArtist ?? string.Empty : string.Empty,
            isLowConfidence ? suggestionPaths : [],
            forceProjection: true);
        ApplyInstallEstimationWarnings(result);
    }

    internal void ApplyInstalledDestinationResolveFailed()
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.instl_dst = null;
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            bmsFile.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, Properties.Resources.Warning_InstalledDestinationResolveFailed);
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = [];
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
            return;
        }
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, Properties.Resources.Warning_InstalledDestinationResolveFailed);
    }

    internal void ClearInstallDestination()
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.instl_dst = null;
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
            bmsFile.InstallDestinationSuggestions = [];
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            return;
        }
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
    }

    internal void ClearPostInstallState()
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
            bmsFile.ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
            bmsFile.InstallDestinationSuggestions = [];
            bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
            return;
        }
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
        ReplacePendingInstallDestination(Chart.InstallDestination, Chart.InstallDestinationTitle, Chart.InstallDestinationArtist, [], forceProjection: true);
    }

    internal void ClearStructuredWarnings()
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.ClearStructuredWarnings();
            return;
        }
        pendingWarnings.Clear();
        hasPendingWarningProjection = true;
    }

    internal void ClearWarningsByCategory(ChartWarningCategory category)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.ClearWarningsByCategory(category);
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
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.SetWarning(kind, message);
            return;
        }
        ChartWarning warning = ChartWarning.Create(kind, message);
        pendingWarnings[warning.Kind] = warning;
        hasPendingWarningProjection = true;
    }

    internal void ReplaceWarningsByCategory(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.ReplaceWarningsByCategory(category, warnings);
            chart = ChartFileProjection.FromBmsFile(bmsFile);
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

    private void ApplyInstallEstimationWarnings(InstallEstimationResult result)
    {
        ReplaceWarningsByCategory(ChartWarningCategory.InstallEstimation, BuildInstallEstimationWarnings(result));
    }

    private static IEnumerable<ChartWarning> BuildInstallEstimationWarnings(InstallEstimationResult result)
    {
        InstallEstimationCandidate selectedCandidate = result?.SelectedCandidate;
        InstallEstimationCandidate secondCandidate = result?.SecondCandidate;
        string[] suggestionPaths = [.. (result?.SuggestedDestinationDirectories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)];
        bool isLowConfidence = result?.Confidence == InstallEstimationConfidence.Low;
        InstallEstimationLowConfidenceKind lowConfidenceKind = result?.LowConfidenceKind ?? InstallEstimationLowConfidenceKind.None;
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.AmbiguousCandidates && selectedCandidate != null && secondCandidate != null)
        {
            return [ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, string.Format(Properties.Resources.Warning_InstallEstimationAmbiguous, selectedCandidate.DirectoryPath, secondCandidate.DirectoryPath))];
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.InstalledDestinationAmbiguous && suggestionPaths.Length >= 2)
        {
            return [ChartWarning.Create(ChartWarningKind.InstalledDestinationAmbiguous, string.Format(Properties.Resources.Warning_InstalledDestinationAmbiguous, string.Join(Environment.NewLine, suggestionPaths.Select(path => "- " + path))))];
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.MetadataMismatch && selectedCandidate != null)
        {
            return [ChartWarning.Create(ChartWarningKind.InstallEstimationMetadataMismatch, string.Format(Properties.Resources.Warning_InstallEstimationMetadataMismatch, selectedCandidate.DirectoryPath))];
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.ReinstallNotImproved && selectedCandidate != null)
        {
            return [ChartWarning.Create(ChartWarningKind.InstallEstimationReinstallNotImproved, string.Format(Properties.Resources.Warning_InstallEstimationReinstallNotImproved, selectedCandidate.DirectoryPath))];
        }
        return [];
    }

    private static void ApplyInstallEstimationResultToFile(BMSFile bmsFile, InstallEstimationResult result)
    {
        if (bmsFile == null)
        {
            return;
        }
        InstallEstimationCandidate selectedCandidate = result?.SelectedCandidate;
        bmsFile.ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        bmsFile.IsInstallDestinationSuggestionPopupOpen = false;
        bmsFile.instl_dst = result?.ShouldAutoApplyDestination == true && !string.IsNullOrWhiteSpace(result.DestinationDirectory) ? result.DestinationDirectory : null;
        if (result?.HasViableDestination == true)
        {
            bmsFile.InstallDestinationTitle = selectedCandidate?.RepresentativeTitle ?? string.Empty;
            bmsFile.InstallDestinationArtist = selectedCandidate?.RepresentativeArtist ?? string.Empty;
        }
        else
        {
            bmsFile.InstallDestinationTitle = string.Empty;
            bmsFile.InstallDestinationArtist = string.Empty;
        }
        bmsFile.InstallDestinationSuggestions = result?.Confidence == InstallEstimationConfidence.Low
            ? [.. (result?.SuggestedDestinationDirectories ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3)]
            : [];
        bmsFile.ReplaceWarningsByCategory(ChartWarningCategory.InstallEstimation, BuildInstallEstimationWarnings(result));
    }

    private static void ClearInstalledBmsMetadata(BMSFile chartFile)
    {
        chartFile.parent = null;
        chartFile.folder = null;
        chartFile.adddate = null;
        chartFile.date = null;
    }

    private void ReplacePendingInstallDestination(string destinationDirectory, string title, string artist, IReadOnlyList<string> suggestions, bool forceProjection = false)
    {
        installDestination = destinationDirectory ?? string.Empty;
        installDestinationTitle = title ?? string.Empty;
        installDestinationArtist = artist ?? string.Empty;
        installDestinationSuggestions = suggestions ?? [];
        hasInstallDestinationProjection = forceProjection
            ||
            !string.IsNullOrWhiteSpace(installDestination)
            || !string.IsNullOrWhiteSpace(installDestinationTitle)
            || !string.IsNullOrWhiteSpace(installDestinationArtist)
            || installDestinationSuggestions.Count > 0;
    }

    private void ReplaceInstallDestinationState(string destinationDirectory, string title, string artist, IReadOnlyList<string> suggestions)
    {
        BMSFile bmsFile = GetBmsStorageOwner();
        if (bmsFile != null)
        {
            bmsFile.instl_dst = destinationDirectory;
            bmsFile.InstallDestinationTitle = title ?? string.Empty;
            bmsFile.InstallDestinationArtist = artist ?? string.Empty;
            bmsFile.InstallDestinationSuggestions = suggestions ?? [];
            return;
        }
        ReplacePendingInstallDestination(destinationDirectory, title, artist, suggestions, forceProjection: true);
    }

    private BMSFile GetBmsStorageOwner()
    {
        return chart?.GetBmsStorageOwner();
    }

    internal static PackageChartEntry FromPath(string filePath)
    {
        try
        {
            if (ChartFileKindResolver.IsBmsonFilePath(filePath))
            {
                return FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(filePath)));
            }
            return FromBmsFile(BMSFile.CreateBMSFileFromFile(filePath));
        }
        catch
        {
            return null;
        }
    }

}

internal sealed class PackageChartInstallDestinationState(
    string destination,
    string title,
    string artist,
    IReadOnlyList<string> suggestions)
{
    public string Destination { get; } = destination;

    public string Title { get; } = title ?? string.Empty;

    public string Artist { get; } = artist ?? string.Empty;

    public IReadOnlyList<string> Suggestions { get; } = suggestions ?? [];
}
