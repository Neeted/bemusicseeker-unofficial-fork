using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageChartEntry : INotifyPropertyChanged
{
    private ChartFile chart;

    private readonly object mutationGate = new();

    private readonly Dictionary<ChartWarningKind, ChartWarning> pendingWarnings = [];

    private readonly HashSet<ChartWarningCategory> projectedWarningCategories = [];

    private string installDestination;

    private string installDestinationTitle;

    private string installDestinationArtist;

    private IReadOnlyList<string> installDestinationSuggestions = [];

    private bool hasInstallDestinationProjection;

    private ChartFileTransientState resourceHealthProjectionState = ChartFileTransientState.Empty;

    private bool hasResourceHealthProjection;

    private int projectionVersion;

    private readonly object propertyChangedDeferralGate = new();

    private int propertyChangedDeferralDepth;

    private bool chartPropertyChangedDeferred;

    private bool? searchingStatusProjection;

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>既存の変更範囲内で投影通知をまとめ、範囲の終了時に発行します。</summary>
    internal IDisposable DeferPropertyChangedNotifications()
    {
        lock (propertyChangedDeferralGate)
        {
            propertyChangedDeferralDepth++;
        }
        return new PropertyChangedDeferralScope(this);
    }

    /// <summary>通知を捕捉する範囲を開始し、既存の排他解放後に発行する処理を返します。</summary>
    internal Func<Action> DeferPropertyChangedNotificationPublication()
    {
        lock (propertyChangedDeferralGate)
        {
            propertyChangedDeferralDepth++;
        }
        int released = 0;
        return () => Interlocked.Exchange(ref released, 1) == 0
            ? CompletePropertyChangedDeferralWithoutPublishing()
            : null;
    }

    /// <summary>項目固有の排他境界で、共通現在値と導入状態の所有を開始します。</summary>
    internal PackageChartEntry(ChartFile chart)
    {
        this.chart = chart ?? throw new ArgumentNullException(nameof(chart));
        ReplacePendingInstallDestination(chart.InstallDestination, chart.InstallDestinationTitle, chart.InstallDestinationArtist, chart.InstallDestinationSuggestions);
        ReplacePendingWarnings(chart.Warnings);
        ReplacePendingResourceHealth(chart);
    }

    /// <summary>現在の共通値に、この項目が所有する導入状態と警告を重ねた不変投影です。</summary>
    internal ChartFile Chart
    {
        get
        {
            lock (mutationGate)
            {
                ChartFile currentChart = chart;
                ChartFile projectedChart = projectedWarningCategories.Count > 0 || hasInstallDestinationProjection
                    ? ChartFileProjection.WithPackageState(
                        currentChart,
                        hasInstallDestinationProjection ? installDestination : currentChart.InstallDestination,
                        hasInstallDestinationProjection ? installDestinationTitle : currentChart.InstallDestinationTitle,
                        hasInstallDestinationProjection ? installDestinationArtist : currentChart.InstallDestinationArtist,
                        hasInstallDestinationProjection ? installDestinationSuggestions : currentChart.InstallDestinationSuggestions,
                        projectedWarningCategories.Count > 0 ? BuildProjectedWarnings(currentChart.Warnings) : currentChart.Warnings)
                    : currentChart;
                if (hasResourceHealthProjection)
                {
                    projectedChart = ChartFileProjection.WithTransientState(
                        projectedChart,
                        resourceHealthProjectionState);
                }
                return searchingStatusProjection.HasValue
                    ? ChartFileProjection.WithStatus(projectedChart, ApplySearchingStatusProjection(projectedChart.Status, searchingStatusProjection.Value))
                    : projectedChart;
            }
        }
    }

    /// <summary>評価入口で不足するリソースを明示取得します。取得失敗は空成功に置き換えません。</summary>
    internal void AcquireResources()
    {
        lock (mutationGate)
        {
            if (chart.Resources != null)
            {
                return;
            }
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart.Path);
            ImmutableList<ChartResourceReference> resources = chart.Kind == ChartFileKind.Bmson
                ? BmsonChartFileParser.ParseSnapshot(snapshot).Resources
                : BmsChartFileParser.ParseSnapshot(snapshot).Resources;
            chart = ChartFileProjection.WithResources(chart, resources);
        }
    }

    /// <summary>取得済みの共通リソース結果を索引化します。未取得の結果は正常な空として扱いません。</summary>
    internal ChartResourceSnapshot ResourceSnapshot => ChartResourceSnapshot.Create(Chart);

    /// <summary>この項目の現在値または導入投影が変わった版です。所持識別には使いません。</summary>
    internal int ProjectionVersion => Volatile.Read(ref projectionVersion);

    /// <summary>導入準備と現在値の反映を排他する、エントリー固有の境界です。</summary>
    internal object GetMutationSyncRoot() => mutationGate;

    /// <summary>確定済みの共通現在値を反映します。既存の通知延期境界を維持します。</summary>
    internal void ApplyCurrentChart(ChartFile value)
    {
        lock (mutationGate)
        {
            chart = value ?? throw new ArgumentNullException(nameof(value));
        }
        RaiseChartChanged();
    }

    /// <summary>共通値と導入状態を同じ時点で捕捉したdetached項目を作ります。</summary>
    internal PackageChartEntry ToChartEntrySnapshot()
    {
        ChartFile currentChart = Chart;
        if (currentChart == null)
        {
            return null;
        }

        return FromChart(currentChart);
    }

    /// <summary>項目の共通対象を、所持tokenまたは従来の形式・物理パス条件で比較します。</summary>
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

    /// <summary>共通対象を、所持tokenまたは従来の形式・物理パス条件で比較します。</summary>
    internal bool IsSameChartTarget(ChartFile targetChart)
    {
        return ChartFileIdentity.IsSameChartTarget(Chart, targetChart);
    }

    /// <summary>項目の導入先・代表情報・候補を不変値へ捕捉します。</summary>
    internal PackageChartInstallDestinationState CaptureInstallDestinationState()
    {
        ChartFile currentChart = Chart;
        return new PackageChartInstallDestinationState(
            currentChart?.InstallDestination,
            currentChart?.InstallDestinationTitle ?? string.Empty,
            currentChart?.InstallDestinationArtist ?? string.Empty,
            currentChart?.InstallDestinationSuggestions ?? []);
    }

    /// <summary>既存の導入先補償で、捕捉済みの項目状態を復元します。</summary>
    internal void RestoreInstallDestinationState(PackageChartInstallDestinationState state)
    {
        ReplaceInstallDestinationState(
            state?.Destination,
            state?.Title ?? string.Empty,
            state?.Artist ?? string.Empty,
            state?.Suggestions ?? []);
    }

    /// <summary>候補・警告・代表情報を保ち、導入先パスだけを変更します。</summary>
    internal void SetInstallDestinationPathOnly(string destinationDirectory)
    {
        PackageChartInstallDestinationState state = CaptureInstallDestinationState();
        ReplaceInstallDestinationState(destinationDirectory, state.Title, state.Artist, state.Suggestions);
    }

    /// <summary>共通譜面値から項目の状態所有を開始します。保存行の有無は要求しません。</summary>
    internal static PackageChartEntry FromChart(ChartFile chart)
    {
        return chart == null ? null : new PackageChartEntry(chart);
    }

    /// <summary>検索中の投影を項目の排他内で変更し、解放後に通知します。</summary>
    internal void SetSearchingStatus(bool isSearching)
    {
        lock (mutationGate)
        {
            if (searchingStatusProjection == isSearching)
            {
                return;
            }
            searchingStatusProjection = isSearching;
        }
        RaiseChartChanged();
    }

    /// <summary>導入済みパスと時刻、BMSのテキスト付属状態を適用し、初回追加日時を未確定へ戻します。項目の排他を解放してから通知します。</summary>
    internal void ApplyInstalledPath(string installedPath)
    {
        lock (mutationGate)
        {
            if (string.IsNullOrWhiteSpace(installedPath))
            {
                return;
            }

            DateTime writeTime = GetInstalledLastWriteTime(installedPath);
            chart = ChartFileProjection.WithPath(chart, installedPath) with
            {
                LastWriteTimeUtc = writeTime
            };
            if (chart.Kind == ChartFileKind.Bms)
            {
                chart = chart with
                {
                    Txt = Lr2TextGroupResolver.ResolveFlag(installedPath, chart.Txt.GetValueOrDefault()),
                    AddDate = null,
                    Date = writeTime == default ? null : Lr2SongRowEnricher.ToLr2UnixSeconds(writeTime)
                };
            }
        }
        RaiseChartChanged();
    }

    /// <summary>導入先と代表情報を設定し、導入・推定に伴う状態の置換として候補と推定警告を消去します。</summary>
    /// <param name="destinationDirectory">新しい導入先。</param>
    /// <param name="title">導入先の代表題名。</param>
    /// <param name="artist">導入先の代表アーティスト。</param>
    internal void ApplyInstallDestination(string destinationDirectory, string title, string artist)
    {
        ReplacePendingInstallDestination(
            destinationDirectory,
            title,
            artist,
            [],
            forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
    }

    /// <summary>候補と推定警告を保持したまま、導入先と代表情報だけを更新します。</summary>
    /// <param name="destinationDirectory">新しい導入先。空値による編集も含みます。</param>
    /// <param name="title">導入先の代表題名。</param>
    /// <param name="artist">導入先の代表アーティスト。</param>
    internal void ApplyInstallDestinationMetadata(string destinationDirectory, string title, string artist)
    {
        ReplacePendingInstallDestination(
            destinationDirectory,
            title,
            artist,
            CaptureInstallDestinationState().Suggestions,
            forceProjection: true);
    }

    /// <summary>推定結果の導入先・代表情報・候補・警告を一つの項目変更で反映します。</summary>
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

    /// <summary>導入済み対象の導入先解決失敗を、この項目の警告へ反映します。</summary>
    internal void ApplyInstalledDestinationResolveFailed()
    {
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, Properties.Resources.Warning_InstalledDestinationResolveFailed);
    }

    /// <summary>推定できないリソースパスを、この項目の警告へ反映します。</summary>
    internal void ApplyUnsupportedResourcePathWarning()
    {
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        SetWarning(ChartWarningKind.UnsupportedResourcePath, Properties.Resources.Warning_UnsupportedResourcePath);
    }

    /// <summary>入力元探索が既存上限に達した事実を、この項目の警告へ反映します。</summary>
    internal void ApplySourceSurfaceScanLimitExceededWarning(int maxVisitedFileSystemEntryCount)
    {
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        SetWarning(
            ChartWarningKind.SourceSurfaceScanLimitExceeded,
            string.Format(Properties.Resources.Warning_SourceSurfaceScanLimitExceeded, maxVisitedFileSystemEntryCount));
    }

    /// <summary>導入先の明示的なクリアを、代表情報・候補・推定警告とともに反映します。</summary>
    internal void ClearInstallDestination()
    {
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
    }

    /// <summary>導入成功後、この項目に残る導入前の候補と一時警告を解除します。</summary>
    internal void ClearPostInstallState()
    {
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
        ClearResourceHealthProjection();
        ReplacePendingInstallDestination(null, string.Empty, string.Empty, [], forceProjection: true);
    }

    /// <summary>構造化警告と健全性の投影を項目内で消去します。</summary>
    internal void ClearStructuredWarnings()
    {
        lock (mutationGate)
        {
            pendingWarnings.Clear();
            projectedWarningCategories.Clear();
            ClearResourceHealthProjectionCore();
            foreach (ChartWarningCategory category in GetStructuredWarningClearCategories())
            {
                projectedWarningCategories.Add(category);
            }
        }
        RaiseChartChanged();
    }

    /// <summary>指定分類の警告と対応する健全性投影を項目内で消去します。</summary>
    internal void ClearWarningsByCategory(ChartWarningCategory category)
    {
        lock (mutationGate)
        {
            ClearWarningsByCategoryCore(category);
            projectedWarningCategories.Add(category);
            if (category == ChartWarningCategory.ResourceHealth)
            {
                ClearResourceHealthProjectionCore();
            }
        }
        RaiseChartChanged();
    }

    /// <summary>指定の警告を項目の投影に設定します。</summary>
    internal void SetWarning(ChartWarningKind kind, string message)
    {
        lock (mutationGate)
        {
            var warning = ChartWarning.Create(kind, message);
            pendingWarnings[warning.Kind] = warning;
            projectedWarningCategories.Add(warning.Category);
        }
        RaiseChartChanged();
    }

    /// <summary>指定分類の警告を項目の投影内で一括置換します。</summary>
    internal void ReplaceWarningsByCategory(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
        lock (mutationGate)
        {
            ReplaceWarningsByCategoryCore(category, warnings);
            projectedWarningCategories.Add(category);
            if (category == ChartWarningCategory.ResourceHealth)
            {
                ClearResourceHealthProjectionCore();
            }
        }
        RaiseChartChanged();
    }

    /// <summary>健全性とその警告を同じ項目の投影として置換します。</summary>
    internal void ReplaceResourceHealthProjection(ResourceHealthMaintenanceSnapshot maintenanceInfo, IEnumerable<ChartWarning> warnings)
    {
        lock (mutationGate)
        {
            ReplaceWarningsByCategoryCore(ChartWarningCategory.ResourceHealth, warnings);
            projectedWarningCategories.Add(ChartWarningCategory.ResourceHealth);
            resourceHealthProjectionState = ChartFileTransientState.FromResourceHealthSnapshot(maintenanceInfo);
            hasResourceHealthProjection = resourceHealthProjectionState.HasState;
        }
        RaiseChartChanged();
    }

    private void ClearResourceHealthProjection()
    {
        lock (mutationGate)
        {
            if (!hasResourceHealthProjection && resourceHealthProjectionState?.HasState != true)
            {
                return;
            }
            ClearResourceHealthProjectionCore();
        }
        RaiseChartChanged();
    }

    private void ClearResourceHealthProjectionCore()
    {
        resourceHealthProjectionState = ChartFileTransientState.Empty;
        hasResourceHealthProjection = false;
    }

    private void ReplacePendingResourceHealth(ChartFile chart)
    {
        lock (mutationGate)
        {
            resourceHealthProjectionState = ChartFileTransientState.FromResourceHealthChart(chart);
            hasResourceHealthProjection = resourceHealthProjectionState.HasState;
        }
    }

    private void ClearWarningsByCategoryCore(ChartWarningCategory category)
    {
        foreach (ChartWarningKind kind in pendingWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            pendingWarnings.Remove(kind);
        }
    }

    private void ReplaceWarningsByCategoryCore(ChartWarningCategory category, IEnumerable<ChartWarning> warnings)
    {
        ClearWarningsByCategoryCore(category);
        foreach (ChartWarning warning in warnings ?? [])
        {
            if (warning != null && warning.Category == category)
            {
                pendingWarnings[warning.Kind] = warning;
            }
        }
    }

    private void ReplacePendingWarnings(IEnumerable<ChartWarning> warnings)
    {
        lock (mutationGate)
        {
            pendingWarnings.Clear();
            projectedWarningCategories.Clear();
            bool isBms = chart.Kind == ChartFileKind.Bms;
            foreach (ChartWarning warning in warnings ?? [])
            {
                if (warning != null && (!isBms || IsPackageProjectionWarningCategory(warning.Category)))
                {
                    pendingWarnings[warning.Kind] = warning;
                    projectedWarningCategories.Add(warning.Category);
                }
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
        return chart.Kind == ChartFileKind.Bms
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
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.InstalledDestinationAutoAppliedAmbiguous && suggestionPaths.Length >= 2)
        {
            return [ChartWarning.Create(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, string.Format(Properties.Resources.Warning_InstalledDestinationAutoAppliedAmbiguous, string.Join(Environment.NewLine, suggestionPaths.Select(path => "- " + path))))];
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

    private static DateTime GetInstalledLastWriteTime(string path)
    {
        if (!LongPathFileSystem.FileExists(path))
        {
            return default;
        }
        try
        {
            return LongPathFileSystem.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return default;
        }
    }

    private void ReplacePendingInstallDestination(string destinationDirectory, string title, string artist, IReadOnlyList<string> suggestions, bool forceProjection = false)
    {
        lock (mutationGate)
        {
            installDestination = destinationDirectory ?? string.Empty;
            installDestinationTitle = title ?? string.Empty;
            installDestinationArtist = artist ?? string.Empty;
            installDestinationSuggestions = (suggestions ?? []).ToImmutableList();
            hasInstallDestinationProjection = forceProjection
                ||
                !string.IsNullOrWhiteSpace(installDestination)
                || !string.IsNullOrWhiteSpace(installDestinationTitle)
                || !string.IsNullOrWhiteSpace(installDestinationArtist)
                || installDestinationSuggestions.Count > 0;
        }
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
        Interlocked.Increment(ref projectionVersion);
        lock (propertyChangedDeferralGate)
        {
            if (propertyChangedDeferralDepth > 0)
            {
                chartPropertyChangedDeferred = true;
                return;
            }
        }
        RaiseChartPropertyChanged();
    }

    private Action CompletePropertyChangedDeferralWithoutPublishing()
    {
        bool publishDeferredChange = false;
        lock (propertyChangedDeferralGate)
        {
            if (propertyChangedDeferralDepth <= 0)
            {
                return null;
            }
            propertyChangedDeferralDepth--;
            if (propertyChangedDeferralDepth == 0 && chartPropertyChangedDeferred)
            {
                chartPropertyChangedDeferred = false;
                publishDeferredChange = true;
            }
        }
        return publishDeferredChange ? RaiseChartPropertyChanged : null;
    }

    private void CompletePropertyChangedDeferral()
    {
        CompletePropertyChangedDeferralWithoutPublishing()?.Invoke();
    }

    private void RaiseChartPropertyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chart)));
    }

    private sealed class PropertyChangedDeferralScope(PackageChartEntry owner) : IDisposable
    {
        private PackageChartEntry owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.CompletePropertyChangedDeferral();
        }
    }

    /// <summary>一回捕捉した入力から共通基本値を直接構成します。</summary>
    internal static PackageChartEntry FromPath(string filePath)
    {
        try
        {
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            return FromChart(ChartFileKindResolver.IsBmsonFilePath(filePath)
                ? BmsonChartFileParser.ParseSnapshot(snapshot)
                : BmsChartFileParser.ParseSnapshotWithEncodingDetection(snapshot));
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

    public IReadOnlyList<string> Suggestions { get; } = (suggestions ?? []).ToImmutableList();
}
