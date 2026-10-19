using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed partial class RegularChartListOwner
{
    // 捕捉を始める前から要求終端までの差分だけを保持します。通知履歴を常駐させません。
    private List<CatalogCommitReceipt> normalBuildCommits;
    private List<ScoreSnapshotChange> normalBuildScoreChanges;
    private BMSLibrary normalBuildLibrary;
    private (RegularChartListRequestLease Lease, RegularChartListPresentationResult Presentation)? pendingNormalPresentation;
    private readonly HashSet<string> normalBuildMd5Keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> normalBuildSha256Keys = new(StringComparer.OrdinalIgnoreCase);
    private Task pendingNormalDelivery = Task.CompletedTask;
    private readonly HashSet<Task> normalCandidateDeliveries = [];
    private int pendingNormalScoreRequiredVersion = -1;
    private int pendingNormalCatalogRequiredRevision = -1;

    /// <summary>確定rootの対象だけを入力・行・派生順序へ反映し、同じbatchを受ける構築中要求にも渡します。</summary>
    private void ApplyIncrementalCatalogNotification(BMSLibrary library, NormalLibraryRefreshNotificationBatch batch)
    {
        lock (syncRoot)
        {
            if (normalBuildCommits != null && ReferenceEquals(normalBuildLibrary, library))
            {
                normalBuildCommits.AddRange(batch.CatalogCommits);
                normalBuildMd5Keys.UnionWith(batch.ChangedDetailMd5s);
                normalBuildSha256Keys.UnionWith(batch.ChangedDetailSha256s);
            }
            foreach (CatalogCommitReceipt commit in batch.CatalogCommits)
            {
                if (virtualSourceRows != null && ReferenceEquals(virtualSourceRows.Library, library)
                    && commit.BaseRevision == virtualSourceRows.Snapshot.CatalogRevision && commit.Snapshot != null)
                {
                    InvalidateCatalogOrderKeysUnsafe(virtualSourceRows, commit);
                    virtualSourceRows = virtualSourceRows.WithSnapshot(commit.Snapshot, commit.Changes);
                }
            }
        }
        if (mainChartList.Rows is ChartListVirtualView view && view.NormalInput is NormalLibrarySourceRows input
            && ReferenceEquals(input.Library, library))
        {
            OwnedChartToken selected = view.GetNormalTokenAt(mainChartList.SelectedIndex);
            if (view.TryApplyCatalogCommits(batch.CatalogCommits, (row, source) => UpdateNormalRow(library, row, source), out bool changed))
            {
                mainChartList.RowProjection.CaptureVersions(library);
                changed |= view.RefreshKeyDependencies(batch.ChangedDetailMd5s, batch.ChangedDetailSha256s,
                    row => row.RefreshDisplayForDataDependency(MainViewDataDependency.ChartInfo));
                changed |= view.RefreshTokens(batch.InstallDestinationChangedCharts.Select(chart => chart.Token).Where(token => token != null),
                    row => row.RefreshDisplayForDataDependency(MainViewDataDependency.InstallDestination));
                if (!ReferenceEquals(mainChartList.Rows, view)) { return; }
                if (selected != null) { mainChartList.SelectedIndex = view.IndexOfNormalToken(selected); }
                if (changed) { view.PublishOrderChanged(); }
                lock (syncRoot)
                {
                    if (ReferenceEquals(mainChartList.Rows, view) && ReferenceEquals(virtualSourceRowsLibrary, library))
                    { virtualSourceRows = view.NormalInput; }
                }
            }
        }
        else if (mainChartList.Rows is ChartListVirtualView subset && ReferenceEquals(subset.ScoreLibrary, library))
        {
            int selected = subset.GetSubsetSourceIndexAt(mainChartList.SelectedIndex);
            bool changed = subset.RefreshSubsetCatalogChanges(batch.CatalogCommits);
            if (selected >= 0) { mainChartList.SelectedIndex = subset.IndexOfSubsetSourceIndex(selected); }
            if (changed) { subset.PublishOrderChanged(); }
        }
    }

    /// <summary>カタログ版やSourceChangedではなく、キャッシュが使用する順序キーの実変更だけで失効させます。</summary>
    private void InvalidateCatalogOrderKeysUnsafe(NormalLibrarySourceRows before, CatalogCommitReceipt commit)
    {
        bool membershipChanged = !ReferenceEquals(before.Snapshot.MembershipOrderIdentity, commit.Snapshot.MembershipOrderIdentity)
            && (before.IncludeBmsonRows || commit.Changes.Any(change => change.Before?.Kind == ChartFileKind.Bms || change.After?.Kind == ChartFileKind.Bms));
        bool Affects(string column)
        {
            if (membershipChanged) { return true; }
            MainViewDataDependency dependency = MainViewRefreshDecisionService.GetSortColumnDependency(column);
            bool maintenanceChanged = commit.Changes.Any(change => change.Before?.ResourceHealthMaintenanceSnapshot != change.After?.ResourceHealthMaintenanceSnapshot
                || change.Before?.WAVHealth != change.After?.WAVHealth || change.Before?.BGAHealth != change.After?.BGAHealth
                || change.Before?.MovieHealth != change.After?.MovieHealth || change.Before?.EncodingName != change.After?.EncodingName);
            bool warningsChanged = maintenanceChanged || commit.Changes.Any(change => change.Before != null && change.After != null
                && (change.Before.ResourceHealthWarningsIgnored != change.After.ResourceHealthWarningsIgnored
                    || !change.Before.Warnings.Select(warning => (warning.Kind, warning.Message)).SequenceEqual(
                        change.After.Warnings.Select(warning => (warning.Kind, warning.Message)))));
            if (dependency == MainViewDataDependency.Warning && warningsChanged)
            { return true; }
            foreach (CatalogChartChange change in commit.Changes)
            {
                if (!before.IncludeBmsonRows && change.After?.Kind == ChartFileKind.Bmson) { continue; }
                if (change.Before == null || change.After == null) { return true; }
                if (ChartListOrder.CompareKeys(ChartListOrder.CaptureKey(before.Project(change.Before), column),
                    ChartListOrder.CaptureKey(before.Project(change.After), column), System.ComponentModel.ListSortDirection.Ascending) != 0)
                { return true; }
            }
            return false;
        }
        foreach (NormalLibrarySortCacheKey key in virtualOrderCache.Keys.ToArray())
        { if (Affects(key.ColumnName)) { virtualOrderCache.Remove(key); } }
        foreach (NormalLibrarySortCacheKey key in sortCache.Keys.ToArray())
        { if (Affects(key.ColumnName)) { sortCache.Remove(key); } }
        foreach (VirtualChartSubsetSortCacheKey key in virtualSubsetOrderCache.Keys.ToArray())
        { if (Affects(key.ColumnName)) { virtualSubsetOrderCache.Remove(key); } }

    }

    /// <summary>候補の値へ既知の差分を適用します。未配送区間は要求ごと保持し、配送が再開するまで再構築しません。</summary>
    private bool PrepareNormalCandidateUnsafe(RegularChartListRequestLease lease, RegularChartListPresentationResult presentation)
    {
        if (presentation.CreateRowsRequest().Rows is not ChartListVirtualView view) { return true; }
        if (view.NormalInput != null)
        {
            foreach (CatalogCommitReceipt commit in normalBuildCommits ?? [])
            {
                if (!commit.IsFullReplacement && commit.Snapshot != null && commit.BaseRevision == view.NormalInput.Snapshot.CatalogRevision)
                {
                    InvalidateCatalogOrderKeysUnsafe(view.NormalInput, commit);
                    if (!view.TryApplyCatalogCommits([commit], (row, source) => UpdateNormalRow(view.NormalInput.Library, row, source), out _))
                    { break; }
                }
            }
            if (!view.TryApplyCatalogCommits(normalBuildCommits ?? [], (row, source) => UpdateNormalRow(view.NormalInput.Library, row, source), out _)
                || !view.IsCatalogInputCurrent)
            {
                pendingNormalPresentation = (lease, presentation);
                return false;
            }
            view.RefreshKeyDependencies(normalBuildMd5Keys, normalBuildSha256Keys, null);
        }
        if (!view.TryApplyScoreChanges(normalBuildScoreChanges ?? [], out _)
            || view.ScoreLibrary != null && view.ScorePosition < view.ScoreLibrary.ScoreSnapshotVersion)
        {
            pendingNormalPresentation = (lease, presentation);
            return false;
        }
        return true;
    }

    /// <summary>候補が必要とする確定位置ごとに一回だけ既存drainを要求し、その成否を要求の終端へ接続します。</summary>
    private void StartPendingLibraryChangeDelivery()
    {
        (RegularChartListRequestLease Lease, RegularChartListPresentationResult Presentation) pending;
        BMSLibrary library;
        int requiredScore, requiredCatalog;
        lock (syncRoot)
        {
            if (!pendingNormalPresentation.HasValue || !pendingNormalDelivery.IsCompleted) { return; }
            pending = pendingNormalPresentation.Value;
            if (!IsCurrentUnsafe(pending.Lease)) { return; }
            library = normalBuildLibrary;
            if (library == null) { return; }
            requiredScore = library.ScoreSnapshotVersion;
            requiredCatalog = library.CatalogRevision;
            if (requiredScore <= pendingNormalScoreRequiredVersion && requiredCatalog <= pendingNormalCatalogRequiredRevision) { return; }
            pendingNormalScoreRequiredVersion = requiredScore;
            pendingNormalCatalogRequiredRevision = requiredCatalog;
        }
        Task delivery = CompletePendingLibraryChangeDeliveryAsync(pending.Lease, pending.Presentation,
            library, requiredScore, requiredCatalog);
        lock (syncRoot)
        {
            if (!delivery.IsCompleted) { normalCandidateDeliveries.Add(delivery); }
            if (IsCurrentUnsafe(pending.Lease)) { pendingNormalDelivery = delivery; }
        }
        _ = RemoveCompletedCandidateDeliveryAsync(delivery);
    }

    private async Task RemoveCompletedCandidateDeliveryAsync(Task delivery)
    {
        try { await delivery.ConfigureAwait(false); }
        finally { lock (syncRoot) { normalCandidateDeliveries.Remove(delivery); } }
    }

    private async Task CompletePendingLibraryChangeDeliveryAsync(RegularChartListRequestLease lease,
        RegularChartListPresentationResult presentation, BMSLibrary library, int requiredScore, int requiredCatalog)
    {
        try
        {
            await DeliverPendingLibraryChangesAsync(library, lease.Token).ConfigureAwait(false);
            lease.Token.ThrowIfCancellationRequested();
            bool ran = false;
            IUiScheduledOperation operation = normalLibraryRefreshUiScheduler.Schedule(() =>
            {
                ran = true;
                lock (syncRoot)
                {
                    if (!IsCurrentUnsafe(lease) || normalBuildCommits == null || !ReferenceEquals(normalBuildLibrary, library)) { return; }
                    if (presentation.CreateRowsRequest().Rows is not ChartListVirtualView view
                        || !view.TryApplyScoreChanges(normalBuildScoreChanges ?? [], out _)
                        || view.ScorePosition < requiredScore)
                    { throw new InvalidOperationException("The delivered score range does not cover the candidate input."); }
                    if (view.NormalInput != null)
                    {
                        _ = view.TryApplyCatalogCommits(normalBuildCommits ?? [], (row, source) => UpdateNormalRow(library, row, source), out _);
                        if (view.NormalInput.Snapshot.CatalogRevision < requiredCatalog)
                        { throw new InvalidOperationException("The delivered catalog range does not cover the candidate input."); }
                    }
                }
                _ = TryCommitCore(lease, presentation);
            }, UiSchedulePriority.Normal);
            if (operation?.IsAccepted != true)
            { throw new InvalidOperationException(operation?.RejectionReason ?? "The candidate continuation was rejected."); }
            using CancellationTokenRegistration registration = lease.Token.Register(operation.Abort);
            await operation.Completion.ConfigureAwait(false);
            if (operation.IsAborted) { throw new OperationCanceledException("The candidate continuation was aborted."); }
            if (!ran) { throw new InvalidOperationException("The candidate continuation completed without execution."); }
            // 別の実確定で必要位置が進んだ場合だけ、次の登録済みrangeを待てる。
            bool advanced;
            lock (syncRoot)
            {
                advanced = IsCurrentUnsafe(lease) && pendingNormalPresentation.HasValue
                    && (library.ScoreSnapshotVersion > requiredScore || library.CatalogRevision > requiredCatalog);
                if (advanced) { pendingNormalDelivery = Task.CompletedTask; }
            }
            if (advanced) { StartPendingLibraryChangeDelivery(); }
        }
        catch (Exception exception)
        {
            CancellationTokenSource cancellation = null;
            lock (syncRoot)
            {
                if (IsCurrentUnsafe(lease))
                {
                    cancellation = currentCancellation;
                    currentCancellation = null;
                    regularRequestActive = false;
                    pendingNormalPresentation = null;
                    normalBuildCommits = null;
                    normalBuildScoreChanges = null;
                    normalBuildLibrary = null;
                    normalBuildMd5Keys.Clear();
                    normalBuildSha256Keys.Clear();
                }
            }
            CancelAndDispose(cancellation);
            logWarning("normal_library_candidate delivery_failed exception=" + exception.GetType().Name + " message=" + exception.Message);
        }
    }

    /// <summary>行に生値を複写せず、同じ確定snapshotから取得した不変譜面へ表示を接続し直します。</summary>
    private void UpdateNormalRow(BMSLibrary library, LibraryChartRow row, ChartListSourceRow source)
    {
        row.UpdateSourceProjection(source.CapturedChart);
        mainChartList.RowProjection.ConfigureLibraryRow(library, row, source);
        row.RefreshDisplayForDataDependency(MainViewDataDependency.SourceMembership);
    }

    /// <summary>明示要求の検索条件を固定し、配送差分にも同じ判定を使用します。</summary>
    private static Func<ChartListSourceRow, bool> CreateNormalFilter(RegularVirtualNormalLibraryApplyRequest request)
    {
        RegularNormalLibraryTreeFilter tree = request.TreeFilter;
        var query = GridKeywordSearchQuery.Parse(request.KeywordFilter);
        HashSet<int?> modes = request.ModeFilter == ChartModeFilter.All ? null
            : RegularChartListFilterService.CreateModeFilterValueSet(request.ModeFilter);
        return row => (tree == null || tree.Matches(row))
            && (!query.HasTokens || query.MatchesChartListSourceRow(row))
            && (modes == null || modes.Contains(row.Mode));
    }

    private static Func<ChartListSourceRow, bool> CreateSubsetFilter(string keyword, ChartModeFilter mode)
    {
        var query = GridKeywordSearchQuery.Parse(keyword);
        HashSet<int?> modes = mode == ChartModeFilter.All ? null : RegularChartListFilterService.CreateModeFilterValueSet(mode);
        return row => (!query.HasTokens || query.MatchesChartListSourceRow(row)) && (modes == null || modes.Contains(row.Mode));
    }

    /// <summary>scoreの実変更キーを表示・構築中要求へ渡し、無関係な入力を取消しません。</summary>
    private void ApplyScoreChanges(BMSLibrary library, IReadOnlyList<ScoreSnapshotChange> changes)
    {
        if (changes.Count == 0) { return; }
        ChartListVirtualView view;
        bool showing;
        lock (syncRoot)
        {
            bool building = normalBuildCommits != null && ReferenceEquals(normalBuildLibrary, library);
            view = mainChartList.Rows as ChartListVirtualView;
            showing = view != null && ReferenceEquals(view.ScoreLibrary, library);
            if (building)
            {
                normalBuildScoreChanges.AddRange(changes);
            }
            _ = PruneCacheUnsafe(sortCache, MainViewDataDependency.Score);
            _ = PruneCacheUnsafe(virtualOrderCache, MainViewDataDependency.Score);
            _ = PruneCacheUnsafe(virtualSubsetOrderCache, MainViewDataDependency.Score);
            if (virtualSourceRows != null && ReferenceEquals(virtualSourceRows.Library, library)
                && !ReferenceEquals(virtualSourceRows, view?.NormalInput)
                && !virtualSourceRows.TryApplyScoreChanges(changes))
            { throw new InvalidOperationException("A score change range has a gap before the cached input."); }
        }
        if (showing)
        {
            OwnedChartToken selected = view.GetNormalTokenAt(mainChartList.SelectedIndex);
            int subsetSelected = view.GetSubsetSourceIndexAt(mainChartList.SelectedIndex);
            if (!view.TryApplyScoreChanges(changes, out bool changed))
            { throw new InvalidOperationException("A score change range has a gap before the displayed input."); }
            if (ReferenceEquals(mainChartList.Rows, view))
            {
                if (selected != null) { mainChartList.SelectedIndex = view.IndexOfNormalToken(selected); }
                else if (subsetSelected >= 0) { mainChartList.SelectedIndex = view.IndexOfSubsetSourceIndex(subsetSelected); }
                if (changed) { view.PublishOrderChanged(); }
                mainChartList.UpdateSummaryText(view);
                mainChartList.RequestDisplayRefresh();
            }
        }
        else
        {
            foreach (ScoreSnapshotChange change in changes)
            {
                foreach (LibraryChartRow row in rowCache.GetRowsForScoreKeys(change.Md5Keys, change.Sha256Keys))
                { row.RefreshDisplayForDataDependency(MainViewDataDependency.Score); }
            }
        }
    }
}
