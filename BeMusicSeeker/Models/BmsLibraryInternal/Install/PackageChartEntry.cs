using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Threading;

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

    private ResourceHealthMaintenanceSnapshot originalResourceHealth;

    private IReadOnlyList<ChartResourceReference> packageLayoutDefinitions;

    /// <summary>元パッケージの捕捉時だけ全定義を取得します。通常Resourcesの非nullは未使用定義の保持を保証しないため、未捕捉なら明示的に読みます。</summary>
    internal IReadOnlyList<ChartResourceReference> AcquirePackageLayoutDefinitions()
    {
        lock (mutationGate)
        {
            if (packageLayoutDefinitions != null) { return packageLayoutDefinitions; }
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart.Path);
            if (chart.Kind == ChartFileKind.Bmson)
            {
                var details = new ChartResourceParseDetails();
                BmsonChartFileParser.ParseSnapshot(snapshot, details);
                packageLayoutDefinitions = CaptureBmsonPackageLayoutDefinitions(details);
            }
            else { packageLayoutDefinitions = BmsChartFileParser.ParseSnapshot(snapshot, BmsChartFileParser.GetConcreteFixedEncoding(chart)).Resources; }
            return packageLayoutDefinitions;
        }
    }

    /// <summary>packageのbool確定後、定義元情報を含まない短命材料も解放します。</summary>
    internal void ReleasePackageLayoutDefinitions()
    {
        lock (mutationGate) { packageLayoutDefinitions = null; }
    }

    private static IReadOnlyList<ChartResourceReference> CaptureBmsonPackageLayoutDefinitions(ChartResourceParseDetails details)
        => details.Definitions.Where(definition => definition.Source != "info.preview_music")
            .Select(definition => definition.Reference).ToArray();

    /// <summary>最後の評価で捕捉した不変配置です。通常の導入先編集では再評価し、成功した改名・移動では捕捉済み資源を保ってpathだけを追従させます。</summary>
    internal ChartResourcePlacement ResourcePlacement { get; private set; }

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
            // DB由来の旧null診断は、通常参照が残っていてもCP932取得済みの証拠にはなりません。
            bool needsLr2Facts = chart.Kind == ChartFileKind.Bms
                && chart.ResourceHealthMaintenanceSnapshot is { Origin: MaintenanceInfoOrigin.DbHydrated, Lr2WarningFlags: null };
            if (chart.Resources != null && !needsLr2Facts)
            {
                RefreshUnsupportedResourcePathWarningCore();
                return;
            }
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chart.Path);
            ChartFile parsed = chart.Kind == ChartFileKind.Bmson ? BmsonChartFileParser.ParseSnapshot(snapshot)
                : BmsChartFileParser.ParseSnapshot(snapshot, BmsChartFileParser.GetConcreteFixedEncoding(chart));
            ResourceHealthMaintenanceSnapshot previous = chart.ResourceHealthMaintenanceSnapshot;
            ResourceHealthMaintenanceSnapshot facts = parsed.ResourceHealthMaintenanceSnapshot;
            chart = chart with
            {
                Resources = parsed.Resources,
                ResourceHealthMaintenanceSnapshot = previous == null ? facts : facts == null ? previous : previous with
                {
                    Lr2WarningFlags = facts.Lr2WarningFlags,
                    Lr2ResourceMaxRelativeCp932Bytes = facts.Lr2ResourceMaxRelativeCp932Bytes,
                    Lr2ResourceHasParentTraversal = facts.Lr2ResourceHasParentTraversal
                }
            };
            RefreshUnsupportedResourcePathWarningCore();
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
            RefreshUnsupportedResourcePathWarningCore();
        }
        RaiseChartChanged();
    }

    /// <summary>計算済みの現在値と健康度・LR2警告を一緒に反映します。登録時結果を変えず、未取得の資源結果を古い参照で補完しません。</summary>
    internal IReadOnlyList<ChartWarning> ApplyCalculatedResourceHealth(ChartFile value, ChartResourcePlacement placement)
    {
        ArgumentNullException.ThrowIfNull(value);
        ResourceHealthMaintenanceSnapshot health = value.ResourceHealthMaintenanceSnapshot;
        IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(health);
        using IDisposable deferred = DeferPropertyChangedNotifications();
        ApplyCurrentChart(value);
        ReplaceWarningsByCategory(ChartWarningCategory.Lr2Compatibility,
            value.Warnings.Where(warning => warning.Category == ChartWarningCategory.Lr2Compatibility));
        ReplaceResourceHealthProjection(health, warnings, placement);
        return warnings;
    }

    /// <summary>保存成功後の文字コード・新参照・元配置健康度を採用します。既存のDST・候補と配置を保ち、その後の再推定は新しい参照を使います。</summary>
    internal void ApplyCommittedEncoding(ChartFile value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (mutationGate)
        {
            chart = value;
            packageLayoutDefinitions = null;
            originalResourceHealth = value.ResourceHealthMaintenanceSnapshot;
            hasResourceHealthProjection = false;
            resourceHealthProjectionState = ChartFileTransientState.Empty;
        }
        RaiseChartChanged();
    }

    /// <summary>共通値・導入状態と登録時の元配置結果を同じ時点で捕捉したdetached項目を作ります。不変の配置と元配置結果は共有します。</summary>
    internal PackageChartEntry ToChartEntrySnapshot()
    {
        lock (mutationGate)
        {
            ChartFile currentChart = Chart;
            if (currentChart == null) { return null; }
            PackageChartEntry snapshot = FromChart(currentChart);
            snapshot.ResourcePlacement = ResourcePlacement;
            snapshot.originalResourceHealth = originalResourceHealth;
            return snapshot;
        }
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

    /// <summary>成功scopeを現在の保留状態へ順に適用します。改名・純移動では診断Messageを保ち、削除・統合の確定DST存続時は候補評価だけを失効して非候補診断を保持します。</summary>
    internal void ApplyLibraryFolderChange(LibraryInstallDestinationChange change)
    {
        using IDisposable deferred = DeferPropertyChangedNotifications();
        lock (mutationGate)
        {
            PackageChartInstallDestinationState state = CaptureInstallDestinationState();
            bool destinationAffected = LibraryInstallDestinationChange.IsUnderFolder(state.Destination, change.SourceFolderPath);
            bool candidatesAffected = state.Suggestions.Any(path => LibraryInstallDestinationChange.IsUnderFolder(path, change.SourceFolderPath));
            bool placementAffected = ResourcePlacement?.Basis != ChartResourcePlacementBasis.Current
                && LibraryInstallDestinationChange.IsUnderFolder(ResourcePlacement?.Directory, change.SourceFolderPath);
            if (!destinationAffected && !candidatesAffected && !placementAffected) { return; }

            if (change.InvalidateCandidates)
            {
                if (string.IsNullOrWhiteSpace(state.Destination)
                    || (destinationAffected && string.IsNullOrWhiteSpace(change.DestinationFolderPath)))
                {
                    BmsLibraryPackageInstallService.ClearPendingInstallDestination(this);
                    return;
                }
                ReplaceInstallDestinationState(
                    destinationAffected ? LibraryInstallDestinationChange.RelocatePath(state.Destination, change.SourceFolderPath, change.DestinationFolderPath) : state.Destination,
                    state.Title, state.Artist, []);
                // 手動確定でも残す入力元・所持先解決の診断は、候補評価の失効に巻き込みません。
                ReplaceWarningsByCategory(ChartWarningCategory.InstallEstimation,
                    Chart.Warnings.Where(warning => warning.Kind is ChartWarningKind.UnsupportedResourcePath
                        or ChartWarningKind.InstalledDestinationResolveFailed or ChartWarningKind.SourceSurfaceScanLimitExceeded));
                if (destinationAffected)
                {
                    ResourcePlacement = ResourcePlacement?.Relocate(change.SourceFolderPath, change.DestinationFolderPath);
                }
            }
            else
            {
                ReplaceInstallDestinationState(
                    LibraryInstallDestinationChange.RelocatePath(state.Destination, change.SourceFolderPath, change.DestinationFolderPath),
                    state.Title, state.Artist,
                    state.Suggestions.Select(path => LibraryInstallDestinationChange.RelocatePath(path, change.SourceFolderPath, change.DestinationFolderPath)).ToArray());
                ResourcePlacement = ResourcePlacement?.Relocate(change.SourceFolderPath, change.DestinationFolderPath);
            }
        }
        RaiseChartChanged();
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

    /// <summary>候補と推定警告を保持したまま、導入先と表示根拠だけを更新します。</summary>
    /// <param name="destinationDirectory">新しい導入先。空値による編集も含みます。</param>
    /// <param name="title">導入先の分類根拠となった実在ペアの題名。</param>
    /// <param name="artist">題名と同じ実在ペアのアーティスト。</param>
    internal void ApplyInstallDestinationMetadata(string destinationDirectory, string title, string artist)
    {
        ReplacePendingInstallDestination(
            destinationDirectory,
            title,
            artist,
            CaptureInstallDestinationState().Suggestions,
            forceProjection: true);
    }

    /// <summary>推定結果を一つの項目変更で反映し、音源不足などで自動確定できない場合も候補を保持します。</summary>
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
            result?.HasViableDestination == true ? selectedCandidate?.DisplayTitle ?? string.Empty : string.Empty,
            result?.HasViableDestination == true ? selectedCandidate?.DisplayArtist ?? string.Empty : string.Empty,
            isLowConfidence || result?.ShouldAutoApplyDestination != true ? suggestionPaths : [],
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

    /// <summary>候補・代表・DSTを変更せず、採用要求の親参照WARNINGだけを更新します。未取得時は既存事実を維持します。</summary>
    private void RefreshUnsupportedResourcePathWarningCore()
    {
        if (chart.Resources == null) { return; }
        if (chart.Resources.Any(reference => reference.RestrictsAutomaticDestination))
        {
            pendingWarnings[ChartWarningKind.UnsupportedResourcePath] = ChartWarning.Create(ChartWarningKind.UnsupportedResourcePath, Properties.Resources.Warning_UnsupportedResourcePath);
            projectedWarningCategories.Add(ChartWarningCategory.InstallEstimation);
        }
        else { pendingWarnings.Remove(ChartWarningKind.UnsupportedResourcePath); }
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

    /// <summary>登録入口で元配置の評価結果を一度だけ確定します。通常の再評価からは呼びません。</summary>
    internal void InitializeOriginalResourceHealth(ResourceHealthMaintenanceSnapshot value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (mutationGate)
        {
            if (originalResourceHealth != null)
            {
                throw new InvalidOperationException("Original resource health has already been initialized.");
            }
            originalResourceHealth = value;
        }
    }

    /// <summary>消去前に登録時の必須結果を取得します。欠落を再評価や未判定へ置き換えません。</summary>
    internal ResourceHealthMaintenanceSnapshot GetOriginalResourceHealth()
    {
        lock (mutationGate)
        {
            return originalResourceHealth ?? throw new InvalidOperationException("Original resource health has not been initialized.");
        }
    }

    /// <summary>導入の確定後、保留の元配置にだけ必要な結果への参照を解放します。</summary>
    internal void DiscardOriginalResourceHealth()
    {
        lock (mutationGate)
        {
            originalResourceHealth = null;
        }
    }

    /// <summary>導入成功に向けたdetached準備と成功後のlive反映で、元配置結果・候補・導入前の構造警告と一時警告を解除します。</summary>
    /// <remarks>準備時はdetached項目だけへ適用し、失敗・未処理のlive項目では元の警告を保持します。LR2投影の所有を解放して確定現在値の診断へ追従し、成功先の健康度は必須保守で再評価します。</remarks>
    internal void ClearPostInstallState()
    {
        DiscardOriginalResourceHealth();
        ClearWarningsByCategory(ChartWarningCategory.PackageLayout);
        ClearWarningsByCategory(ChartWarningCategory.InstallEstimation);
        ClearWarningsByCategory(ChartWarningCategory.ResourceHealth);
        ClearResourceHealthProjection();
        lock (mutationGate)
        {
            // 空の投影で上書きすると成功先の新しい診断も隠すため、分類の所有ごと解放します。
            ClearWarningsByCategoryCore(ChartWarningCategory.Lr2Compatibility);
            projectedWarningCategories.Remove(ChartWarningCategory.Lr2Compatibility);
        }
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
    internal void ReplaceResourceHealthProjection(ResourceHealthMaintenanceSnapshot maintenanceInfo, IEnumerable<ChartWarning> warnings, ChartResourcePlacement placement = null)
    {
        lock (mutationGate)
        {
            ReplaceWarningsByCategoryCore(ChartWarningCategory.ResourceHealth, warnings);
            projectedWarningCategories.Add(ChartWarningCategory.ResourceHealth);
            ResourcePlacement = placement;
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
        ResourcePlacement = null;
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
        ChartWarning preservedParent = chart.Resources == null && pendingWarnings.TryGetValue(ChartWarningKind.UnsupportedResourcePath, out ChartWarning previousParent) ? previousParent : null;
        foreach (ChartWarningKind kind in pendingWarnings.Where(pair => pair.Value.Category == category).Select(pair => pair.Key).ToList())
        {
            pendingWarnings.Remove(kind);
        }
        if (category == ChartWarningCategory.InstallEstimation)
        {
            if (preservedParent != null) { pendingWarnings[ChartWarningKind.UnsupportedResourcePath] = preservedParent; }
            else { RefreshUnsupportedResourcePathWarningCore(); }
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
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.AmbiguousCandidates && selectedCandidate != null && (result.AmbiguousCandidateDirectory != null || secondCandidate != null))
        {
            yield return ChartWarning.Create(ChartWarningKind.InstallEstimationAmbiguous, string.Format(Properties.Resources.Warning_InstallEstimationAmbiguous, selectedCandidate.DirectoryPath, result.AmbiguousCandidateDirectory ?? secondCandidate.DirectoryPath));
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.InstalledDestinationAmbiguous && suggestionPaths.Length >= 2)
        {
            yield return ChartWarning.Create(ChartWarningKind.InstalledDestinationAmbiguous, string.Format(Properties.Resources.Warning_InstalledDestinationAmbiguous, string.Join(Environment.NewLine, suggestionPaths.Select(path => "- " + path))));
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.InstalledDestinationAutoAppliedAmbiguous && suggestionPaths.Length >= 2)
        {
            yield return ChartWarning.Create(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, string.Format(Properties.Resources.Warning_InstalledDestinationAutoAppliedAmbiguous, string.Join(Environment.NewLine, suggestionPaths.Select(path => "- " + path))));
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.MetadataMismatch && selectedCandidate != null)
        {
            (ChartWarningKind kind, string message) = selectedCandidate.MetadataRelation switch
            {
                InstallEstimationMetadataRelation.Unrelated => (ChartWarningKind.InstallEstimationUnrelated, Properties.Resources.Warning_InstallEstimationUnrelated),
                InstallEstimationMetadataRelation.Unavailable => (ChartWarningKind.InstallEstimationMetadataUnavailable, Properties.Resources.Warning_InstallEstimationMetadataUnavailable),
                _ => (ChartWarningKind.InstallEstimationMetadataMismatch, Properties.Resources.Warning_InstallEstimationMetadataMismatch)
            };
            yield return ChartWarning.Create(kind, string.Format(message, selectedCandidate.DirectoryPath));
        }
        if (isLowConfidence && lowConfidenceKind == InstallEstimationLowConfidenceKind.ReinstallNotImproved && selectedCandidate != null)
        {
            yield return ChartWarning.Create(ChartWarningKind.InstallEstimationReinstallNotImproved, string.Format(Properties.Resources.Warning_InstallEstimationReinstallNotImproved, selectedCandidate.DirectoryPath));
        }
        if (result?.HasViableDestination == true && selectedCandidate != null)
        {
            if (result.AudioCompleteness == InstallAudioCompleteness.Missing && !result.AudioAutoApplyAllowed)
            {
                yield return ChartWarning.Create(ChartWarningKind.InstallEstimationAudioMissing,
                    string.Format(Properties.Resources.Warning_InstallEstimationAudioMissing, selectedCandidate.DirectoryPath));
            }
            else if (result.AudioCompleteness == InstallAudioCompleteness.Unverifiable)
            {
                yield return ChartWarning.Create(ChartWarningKind.InstallEstimationAudioUnverifiable,
                    string.Format(Properties.Resources.Warning_InstallEstimationAudioUnverifiable, selectedCandidate.DirectoryPath));
            }
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

    /// <summary>一回捕捉した入力から共通基本値と、元packageの判定まで共有する短命の全定義材料を構成します。</summary>
    internal static PackageChartEntry FromPath(string filePath) => FromPath(filePath, out _);

    /// <summary>一度読んだ入力から項目と発見専用のPREVIEWを返します。付帯値を長命の項目へ保持しません。</summary>
    internal static PackageChartEntry FromPath(string filePath, out ChartResourceReference? preview) => FromPath(filePath, out preview, out _);

    /// <summary>発見時だけ元の読取り失敗も返し、不完全なscopeを空の成功へ置換しないために使います。</summary>
    internal static PackageChartEntry FromPath(string filePath, out ChartResourceReference? preview, out Exception failure, Func<ChartFileSnapshot, ChartFile> fixedEncodingSource = null)
    {
        preview = null;
        failure = null;
        try
        {
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            bool isBmson = ChartFileKindResolver.IsBmsonFilePath(filePath);
            ChartResourceParseDetails details = isBmson ? new ChartResourceParseDetails() : null;
            ChartFile previous = isBmson ? null : fixedEncodingSource?.Invoke(snapshot);
            string fixedEncoding = BmsChartFileParser.GetConcreteFixedEncodingName(previous?.EncodingName, previous?.ResourceHealthMaintenanceSnapshot?.EncodingFixed == true);
            ChartFile parsed = isBmson ? BmsonChartFileParser.ParseSnapshot(snapshot, details)
                : BmsChartFileParser.ParseForPackageDiscovery(snapshot, fixedEncoding, out preview);
            if (previous != null)
            {
                parsed = ChartFileProjection.WithMaintenance(parsed, parsed.ResourceHealthMaintenanceSnapshot with
                { Encoding = previous.EncodingName, EncodingFixed = previous.ResourceHealthMaintenanceSnapshot.EncodingFixed });
            }
            PackageChartEntry entry = FromChart(parsed);
            // 同じsnapshotの全定義を健康度による未使用解放より先に消費します。BMSONのpreviewだけを定義元で除外します。
            entry.packageLayoutDefinitions = isBmson ? CaptureBmsonPackageLayoutDefinitions(details) : entry.Chart.Resources;
            return entry;
        }
        catch (Exception exception)
        {
            failure = exception;
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
