using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry : INotifyPropertyChanged
{
    private ChartFile chart;

    private readonly Dictionary<ChartWarningKind, ChartWarning> pendingWarnings = [];

    private readonly HashSet<ChartWarningCategory> projectedWarningCategories = [];

    private string installDestination;

    private string installDestinationTitle;

    private string installDestinationArtist;

    private IReadOnlyList<string> installDestinationSuggestions = [];

    private bool hasInstallDestinationProjection;

    private bool? searchingStatusProjection;

    public event PropertyChangedEventHandler PropertyChanged;

    internal PackageChartEntry(ChartFile chart)
    {
        this.chart = chart ?? throw new ArgumentNullException(nameof(chart));
        ReplacePendingInstallDestination(chart.InstallDestination, chart.InstallDestinationTitle, chart.InstallDestinationArtist, chart.InstallDestinationSuggestions);
        ReplacePendingWarnings(chart.Warnings);
    }

    internal ChartFile Chart
    {
        get
        {
            ChartFile currentChart = CreateCurrentChartFromStorageOwner();
            ChartFile projectedChart = projectedWarningCategories.Count > 0 || hasInstallDestinationProjection
                ? ChartFileProjection.WithPackageState(
                    currentChart,
                    hasInstallDestinationProjection ? installDestination : currentChart.InstallDestination,
                    hasInstallDestinationProjection ? installDestinationTitle : currentChart.InstallDestinationTitle,
                    hasInstallDestinationProjection ? installDestinationArtist : currentChart.InstallDestinationArtist,
                    hasInstallDestinationProjection ? installDestinationSuggestions : currentChart.InstallDestinationSuggestions,
                    projectedWarningCategories.Count > 0 ? BuildProjectedWarnings(currentChart.Warnings) : currentChart.Warnings)
                : currentChart;
            return searchingStatusProjection.HasValue
                ? ChartFileProjection.WithStatus(projectedChart, ApplySearchingStatusProjection(projectedChart.Status, searchingStatusProjection.Value))
                : projectedChart;
        }
    }

    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    internal object GetStorageMutationSyncRoot()
    {
        return (object)chart.GetBmsStorageOwner()
            ?? (object)chart.GetBmsonStorageOwner()
            ?? this;
    }

    internal PackageChartEntry ToChartEntrySnapshot()
    {
        ChartFile currentChart = Chart;
        if (currentChart == null)
        {
            return null;
        }

        return FromChart(currentChart);
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
        return ChartFileIdentity.IsSameChartTarget(Chart, targetEntry.Chart);
    }

    internal bool IsSameChartTarget(ChartFile targetChart)
    {
        return ChartFileIdentity.IsSameChartTarget(Chart, targetChart);
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
        if (searchingStatusProjection == isSearching)
        {
            return;
        }
        searchingStatusProjection = isSearching;
        RaiseChartChanged();
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
            RaiseChartChanged();
            return;
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            bmsonSong.path = installedPath;
            bmsonSong.folder = Path.GetDirectoryName(installedPath) ?? string.Empty;
            bmsonSong.MaintenanceInfo?.NormalizeForBmson(bmsonSong.path, bmsonSong.md5);
            chart = ChartFileProjection.FromBmsonSong(bmsonSong);
            RaiseChartChanged();
        }
    }

    internal void ApplyInstallDestination(string destinationDirectory, string title, string artist, bool preserveAmbiguousInstallContext = false)
    {
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
        ReplacePendingInstallDestination(
            destinationDirectory,
            title,
            artist,
            installDestinationSuggestions,
            forceProjection: true);
    }

    internal void ApplyInstallEstimationResult(InstallEstimationResult result)
    {
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
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, Properties.Resources.Warning_InstalledDestinationResolveFailed);
    }

    internal void ClearInstallDestination()
    {
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
    }

    internal void ClearPostInstallState()
    {
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
        ReplacePendingInstallDestination(Chart.InstallDestination, Chart.InstallDestinationTitle, Chart.InstallDestinationArtist, [], forceProjection: true);
    }

    internal void ClearStructuredWarnings()
    {
        pendingWarnings.Clear();
        projectedWarningCategories.Clear();
        foreach (ChartWarningCategory category in GetStructuredWarningClearCategories())
        {
            projectedWarningCategories.Add(category);
        }
        RaiseChartChanged();
    }

    internal void ClearWarningsByCategory(ChartWarningCategory category)
    {
        foreach (ChartWarningKind kind in pendingWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            pendingWarnings.Remove(kind);
        }
        projectedWarningCategories.Add(category);
        RaiseChartChanged();
    }

    internal void SetWarning(ChartWarningKind kind, string message)
    {
        ChartWarning warning = ChartWarning.Create(kind, message);
        pendingWarnings[warning.Kind] = warning;
        projectedWarningCategories.Add(warning.Category);
        RaiseChartChanged();
    }

    internal void ReplaceWarningsByCategory(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
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
        projectedWarningCategories.Add(category);
        RaiseChartChanged();
    }

    private void ReplacePendingWarnings(IEnumerable<ChartWarning> warnings)
    {
        pendingWarnings.Clear();
        projectedWarningCategories.Clear();
        bool hasBmsOwner = chart.GetBmsStorageOwner() != null;
        foreach (ChartWarning warning in warnings ?? [])
        {
            if (warning != null && (!hasBmsOwner || IsPackageProjectionWarningCategory(warning.Category)))
            {
                pendingWarnings[warning.Kind] = warning;
                projectedWarningCategories.Add(warning.Category);
            }
        }
        RaiseChartChanged();
    }

    private IReadOnlyList<ChartWarning> BuildProjectedWarnings(IEnumerable<ChartWarning> ownerWarnings)
    {
        return [.. (ownerWarnings ?? [])
            .Where(warning => warning != null && !projectedWarningCategories.Contains(warning.Category))
            .Concat(pendingWarnings.Values)];
    }

    private IEnumerable<ChartWarningCategory> GetStructuredWarningClearCategories()
    {
        return GetBmsStorageOwner() != null
            ? EnumeratePackageProjectionWarningCategories()
            : Enum.GetValues(typeof(ChartWarningCategory)).Cast<ChartWarningCategory>();
    }

    private static IEnumerable<ChartWarningCategory> EnumeratePackageProjectionWarningCategories()
    {
        yield return ChartWarningCategory.PackageLayout;
        yield return ChartWarningCategory.InstalledState;
        yield return ChartWarningCategory.ResourceHealth;
        yield return ChartWarningCategory.InstallEstimation;
    }

    private static bool IsPackageProjectionWarningCategory(ChartWarningCategory category)
    {
        return category == ChartWarningCategory.PackageLayout
            || category == ChartWarningCategory.InstalledState
            || category == ChartWarningCategory.ResourceHealth
            || category == ChartWarningCategory.InstallEstimation;
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
        RaiseChartChanged();
    }

    private void ReplaceInstallDestinationState(string destinationDirectory, string title, string artist, IReadOnlyList<string> suggestions)
    {
        ReplacePendingInstallDestination(destinationDirectory, title, artist, suggestions, forceProjection: true);
    }

    private static ChartFileStatus ApplySearchingStatusProjection(ChartFileStatus status, bool isSearching)
    {
        return isSearching
            ? status | ChartFileStatus.SEARCHING
            : status & ~ChartFileStatus.SEARCHING;
    }

    private void RaiseChartChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chart)));
    }

    private BMSFile GetBmsStorageOwner()
    {
        return chart?.GetBmsStorageOwner();
    }

    private ChartFile CreateCurrentChartFromStorageOwner()
    {
        BMSFile bmsFile = chart.GetBmsStorageOwner();
        if (bmsFile != null)
        {
            return ChartFileProjection.FromBmsFile(bmsFile);
        }

        LR2SongDBExtended.bmson_song bmsonSong = chart.GetBmsonStorageOwner();
        if (bmsonSong != null)
        {
            return ChartFileProjection.FromBmsonSong(bmsonSong);
        }

        return chart;
    }

    internal static PackageChartEntry FromPath(string filePath)
    {
        try
        {
            if (ChartFileKindResolver.IsBmsonFilePath(filePath))
            {
                return FromChart(ChartFileProjection.FromBmsonSong(BmsonSongParser.Parse(filePath)));
            }
            return FromChart(ChartFileProjection.FromBmsFile(BMSFile.CreateBMSFileFromFile(filePath)));
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
