using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal sealed partial class RegularChartListOwner
{
    /// <summary>導入先の確定対象だけを既存入力・実体化行・依存順序へ反映します。表示交換後は現在viewを対象とし、全source再構築を行いません。</summary>
    internal void ApplyPendingDestinationProjection(PendingPackageMutationAppliedEventArgs mutation)
    {
        mainChartList.RowProjection.UpdateTransientStates(mutation.ChangedCharts, forceInstallDestinationProjection: true);
        if (!mutation.HasLocalDestinationProjection || mainChartList.Rows is not ChartListVirtualView view) { return; }
        using IDisposable selection = view.PreserveSelection();
        bool changed = view.RefreshKeyDependencies(
            mutation.ProjectionCharts.Where(chart => chart != null).Select(chart => chart.Md5),
            mutation.ProjectionCharts.Where(chart => chart != null).Select(chart => chart.Sha256),
            row => row.RefreshDisplayForDataDependency(MainViewDataDependency.InstallDestination));
        if (changed) { view.PublishOrderChanged(); }
        mainChartList.UpdateSummaryText(view);
    }

    /// <summary>固定入力を受ける呼出しも、UI入力の捕捉、画面外での構築、UIへの採用を行う共通処理へ渡します。</summary>
    internal Task<RegularChartListEntryResult> ApplyMainLibraryViewAsync(
        ChartListRefreshRoute route, BMSLibrary library, object parameter, object treeParameter,
        bool preserveSummary, Stopwatch stopwatch, ChartListFilterSnapshot filters = null,
        RegularChartNavigationRequest navigation = null)
    {
        if (route.Kind != ChartListRefreshRouteKind.ContinueMainLibrary)
        { throw new ArgumentException("The regular chart-list owner requires a main-library route.", nameof(route)); }
        return ApplyMainLibraryViewAsync(() => new RegularChartListEntryRequest
        {
            Library = library,
            Mode = route.Mode,
            RequestedMode = route.RequestedMode,
            CurrentTreeMode = route.CurrentTreeMode,
            Parameter = parameter,
            TreeParameter = treeParameter,
            IncludeBmsonRows = route.IncludeBmsonRows,
            PreserveSummary = preserveSummary,
            Stopwatch = stopwatch,
            Filters = filters
        }, navigation);
    }

    /// <summary>
    /// UI入力を一回捕捉し、画面外での構築、UI採用、必要な通知配送と表示同期までを同じTaskで所有します。
    /// 重複データが未準備なら一覧を作らず準備へ合流します。完成通知を受けたUIが、その時点の選択と条件で表示を開始します。
    /// </summary>
    /// <param name="captureInput">この呼出しの表示入力をUIで一回捕捉する処理です。</param>
    /// <param name="navigation">画面へ適用済み、または正規の移動要求が持つ受付参照です。初期値のnullを後発受付へ付け替えません。</param>
    /// <param name="presentationCompleted">実採用・必須配送後、UI/ロック外で同じTaskに含める表示同期です。</param>
    internal Task<RegularChartListEntryResult> ApplyMainLibraryViewAsync(
        Func<RegularChartListEntryRequest> captureInput, RegularChartNavigationRequest navigation,
        Action<RegularChartListEntryResult> presentationCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(captureInput);
        var start = new TaskCompletionSource(TaskCreationOptions.None);
        long expectedRequestId;
        Task<RegularChartListEntryResult> execution;
        lock (syncRoot)
        {
            if (disposed || !IsCurrentNavigationUnsafe(navigation)) { return Task.FromResult(default(RegularChartListEntryResult)); }
            expectedRequestId = currentRequestId;
            execution = ExecuteAsync();
            viewPreparationTasks.Add(execution);
        }
        execution.ObserveFault("regularChartListViewBuild");
        _ = ReleaseViewPreparationAsync(execution);
        start.SetResult();
        return execution;

        async Task<RegularChartListEntryResult> ExecuteAsync()
        {
            await start.Task.ConfigureAwait(false);
            RegularChartListEntryResult result = default;
            await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
            {
                lock (syncRoot)
                {
                    if (!IsCurrentNavigationUnsafe(navigation) || currentRequestId != expectedRequestId) { return Task.CompletedTask; }
                }
                RegularChartListEntryRequest input = captureInput();
                if (input == null) { return Task.CompletedTask; }
                bool dataPending;
                lock (syncRoot) { dataPending = IsDuplicateDataPreparationActiveUnsafe(input.Library); }
                if (input.CurrentTreeMode == MainViewUpdateMode.DuplicateFilterSelected
                    && input.ConfirmedUnresolvedDuplicateSource == null && input.DuplicateInput == null
                    && (input.Library?.DuplicateChartGroups == null || dataPending))
                {
                    // データ未準備では一覧を採用しない。進行中の準備と完成通知は既存のTask集合で回収する。
                    _ = PrepareDuplicateDataAsync("regular_entry");
                    return Task.CompletedTask;
                }
                PreparedRegularView prepared = CapturePrepared(input);
                return Task.Run(async () =>
                {
                    if (prepared != null)
                    { result = await BuildAndApplyViewAsync(prepared, presentationCompleted).ConfigureAwait(false); }
                });
            }).ConfigureAwait(false);
            return result;
        }

        PreparedRegularView CapturePrepared(RegularChartListEntryRequest input)
        {
            if (!IsCurrentNavigation(navigation)) { return null; }
            input.Navigation = navigation;
            if (input.CurrentTreeMode == MainViewUpdateMode.DuplicateFilterSelected && input.ConfirmedUnresolvedDuplicateSource == null)
            {
                input.DuplicateInput ??= RegularDuplicateViewInput.Capture(input.Library);
                if (input.DuplicateInput == null) { return null; }
            }
            if (input.CurrentTreeMode is MainViewUpdateMode.NewlyInstalledFolderSelected or MainViewUpdateMode.PendingInstallFolderSelected)
            {
                if (!IsSubsetVirtualRequest(input.Mode, input.CurrentTreeMode))
                { throw new InvalidOperationException("Package views require a virtual subset request."); }
                return PreparePackageView(new ChartListRefreshRoute(ChartListRefreshRouteKind.ContinueMainLibrary,
                    input.Mode, input.RequestedMode, input.CurrentTreeMode, false, input.IncludeBmsonRows),
                    input.Library, input.Parameter, input.TreeParameter, input.PreserveSummary,
                    input.Stopwatch, input.Filters, expectedRequestId, navigation);
            }
            return PrepareRegularView(input);
        }
    }

    private PreparedRegularView PreparePackageView(
        ChartListRefreshRoute route, BMSLibrary library, object parameter, object treeParameter,
        bool preserveSummary, Stopwatch stopwatch, ChartListFilterSnapshot filters, long expectedRequestId,
        RegularChartNavigationRequest navigation)
    {
        if (route.Kind != ChartListRefreshRouteKind.ContinueMainLibrary)
        {
            throw new ArgumentException("The regular chart-list owner requires a main-library route.", nameof(route));
        }
        if (stopwatch == null || route.IncludeBmsonRows && library == null)
        {
            throw new ArgumentException("A complete regular chart-list entry request is required.");
        }
        lock (syncRoot)
        {
            if (disposed || navigation != null && !IsCurrentNavigationUnsafe(navigation)
                || currentRequestId != expectedRequestId)
            {
                return null;
            }
        }
        bool installed = route.CurrentTreeMode == MainViewUpdateMode.NewlyInstalledFolderSelected;
        object target = treeParameter ?? parameter;
        // UI所有の所属集合をworkerへ持ち出さない。ChartEntriesは各packageの固定列をworkerで一度だけ取得する。
        ChartPackage[] packages = target is ChartPackage package
            ? [package]
            : [.. (installed ? library?.ChartPackagesInstalled : library?.ChartPackagesPending) ?? []];
        ChartListSortSpecification sort = CaptureSort();
        bool sortWasReset = !TryResolveVirtualSort(sort, out string column, out ListSortDirection direction);
        if (sortWasReset) { column = nameof(LibraryChartRow.Title); direction = ListSortDirection.Ascending; }
        filters ??= ChartListFilterSnapshot.Default;
        var entry = new RegularChartListEntryRequest
        {
            Library = library,
            Mode = route.Mode,
            RequestedMode = route.RequestedMode,
            CurrentTreeMode = route.CurrentTreeMode,
            Stopwatch = stopwatch
        };
        var request = new RegularVirtualChartSubsetApplyRequest
        {
            Library = library,
            ApplyResourceHealthProjection = installed,
            TreeMode = route.CurrentTreeMode,
            SelectionContext = target as ChartPackage,
            SubsetName = installed
                ? target is ChartPackage ? "newly_installed_package" : "newly_installed_all"
                : target is ChartPackage ? "pending_install_package" : "pending_install_all",
            KeywordFilter = filters.KeywordFilter,
            ModeFilter = filters.ModeFilter,
            SortColumnName = column,
            SortDirection = direction,
            ExternalVersions = CaptureExternalVersions(library, default),
            ColumnSelection = mainChartList.ResolveColumnSettingForViewUpdate(
                ChartListRefreshCoordinator.ResolveMainColumnSettingMode(route.Mode, route.CurrentTreeMode), route.CurrentTreeMode),
            PreserveSummary = preserveSummary,
            Mode = route.Mode,
            Stopwatch = stopwatch
        };
        if (!TryBeginVirtualRequest(library, retireDetailSource: true, out RegularChartListRequestLease lease,
                out PlaylistSourceRetirementRequest retirement, expectedRequestId, navigation))
        {
            return null;
        }
        request.DetailSourceRetirement = retirement;
        mainChartList.RowProjection.CaptureVersions(library);
        RegularVirtualChartSubsetApplyResult result = default;
        return new PreparedRegularView(lease, () =>
        {
            request.SourceEntries = CreatePackageEntrySnapshot(packages);
            result = BuildVirtualChartSubset(request, lease, out RegularChartListPresentationResult presentation);
            return presentation;
        }, terminal =>
        {
            result = result.WithTerminal(terminal);
            LogSubsetVirtualEntry(entry, sort, request.SubsetName, result, sortWasReset);
            if (terminal.WasCommitted && sortWasReset) { ResetSortIfCurrent(lease, sort); }
            return new RegularChartListEntryResult(terminal.WasCommitted, RegularChartListEntryRoute.SubsetVirtual, sortWasReset);
        });
    }

    private bool IsCurrentLease(RegularChartListRequestLease lease)
    {
        lock (syncRoot) { return IsCurrentUnsafe(lease); }
    }

    private void ResetSortIfCurrent(RegularChartListRequestLease lease, ChartListSortSpecification sort)
    {
        lock (syncRoot) { if (IsCurrentUnsafe(lease)) { ResetSortParameters(sort); } }
    }

    private async Task<RegularChartListEntryResult> BuildAndApplyViewAsync(PreparedRegularView prepared,
        Action<RegularChartListEntryResult> presentationCompleted)
    {
        RegularChartListRequestLease lease = prepared.Lease;
        if (!IsCurrentLease(lease) || prepared.InputIsCurrent?.Invoke() == false) { return default; }
        RegularChartListPresentationResult presentation;
        try { presentation = prepared.Build(); }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested) { return default; }
        if (presentation == null) { return default; }
        presentation.InputIsCurrent = prepared.InputIsCurrent;
        presentation.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancellation = lease.Token.Register(() =>
            presentation.Completion.TrySetResult(RegularChartListTerminalResult.Stale()));
        Task delivery = Task.CompletedTask;
        bool failed = false;
        try
        {
            await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
            {
                // PrepareRowsTransitionを含む採用全体はUIで行う。有効な保留候補はCompletionで待つ。
                _ = TryCommitCore(lease, presentation);
                lock (syncRoot)
                {
                    if (!IsCurrentUnsafe(lease)) { presentation.Completion.TrySetResult(RegularChartListTerminalResult.Stale()); }
                    else { delivery = pendingNormalDelivery; }
                }
                // 採用時の同期例外もUI配送のTaskへ返し、一覧要求の失敗として観測する。
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            RegularChartListTerminalResult terminal = await presentation.Completion.Task.ConfigureAwait(false);
            Task completedDelivery;
            lock (syncRoot) { completedDelivery = IsCurrentUnsafe(lease) ? pendingNormalDelivery : Task.CompletedTask; }
            await Task.WhenAll(delivery, completedDelivery).ConfigureAwait(false);
            RegularChartListEntryResult result = default;
            await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
            {
                result = prepared.Finish(terminal);
                presentationCompleted?.Invoke(result);
                // 終了処理と表示同期の例外も、一覧採用と同じTaskの失敗として返す。
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return result;
        }
        catch { failed = true; throw; }
        finally
        {
            presentation.Completion.TrySetResult(RegularChartListTerminalResult.Stale());
            Task finalDelivery;
            lock (syncRoot) { finalDelivery = IsCurrentUnsafe(lease) ? pendingNormalDelivery : Task.CompletedTask; }
            try { await Task.WhenAll(delivery, finalDelivery).ConfigureAwait(false); }
            catch when (failed) { /* 本体の元例外を保持し、追加配送の実終端だけ回収する。 */ }
            if (presentation.Completion.Task.IsFaulted) { _ = presentation.Completion.Task.Exception; }
        }
    }

    // 通常一覧、導入一覧、並べ替えと画面移動は同じ集合へ登録し、実際の完了後に追跡を外す。
    private async Task ReleaseViewPreparationAsync(Task execution)
    {
        try { await execution.ConfigureAwait(false); }
        catch { /* 元のTaskを呼出し元とObserveFaultへ保持し、追跡解除だけを行う。 */ }
        finally
        {
            lock (syncRoot)
            {
                viewPreparationTasks.Remove(execution);
                if (duplicateDataPreparation is { } active && ReferenceEquals(active.Work, execution)) { duplicateDataPreparation = null; }
            }
        }
    }
}
