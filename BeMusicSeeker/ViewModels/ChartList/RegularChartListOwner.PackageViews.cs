using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

internal sealed partial class RegularChartListOwner
{
    /// <summary>
    /// 新規・保留packageの入口をUIで捕捉し、入力準備、警告、順序と検索をworkerで計算します。
    /// UI入力捕捉の予約中も既存要求IDで失効を判定し、新しい選択のleaseを取り直しません。
    /// 非packageは従来の実行コンテキストで同期経路へ渡します。
    /// 戻り値は同じ要求のUI採用と既存の必須配送までを表し、終了時も実構築を回収します。
    /// </summary>
    internal async Task<RegularChartListEntryResult> ApplyMainLibraryViewAsync(
        ChartListRefreshRoute route,
        BMSLibrary library,
        object parameter,
        object treeParameter,
        bool preserveSummary,
        Stopwatch stopwatch,
        ChartListFilterSnapshot filters = null)
    {
        bool packageView = route.CurrentTreeMode is MainViewUpdateMode.NewlyInstalledFolderSelected
            or MainViewUpdateMode.PendingInstallFolderSelected;
        if (!packageView || !IsSubsetVirtualRequest(route.Mode, route.CurrentTreeMode))
        {
            return ApplyMainLibraryView(route, library, parameter, treeParameter, preserveSummary, stopwatch, filters);
        }
        long expectedRequestId;
        lock (syncRoot) { expectedRequestId = currentRequestId; }
        Task<RegularChartListEntryResult> execution = null;
        if (normalLibraryRefreshUiScheduler.CanExecuteInline) { BeginView(); }
        else { await normalLibraryRefreshUiScheduler.InvokeAsync(BeginView).ConfigureAwait(false); }
        return await (execution ?? throw new InvalidOperationException("The regular view entry did not execute.")).ConfigureAwait(false);

        void BeginView()
        {
            execution = BeginPackageViewBuild(route, library, parameter, treeParameter, preserveSummary, stopwatch, filters, expectedRequestId);
        }
    }

    private Task<RegularChartListEntryResult> BeginPackageViewBuild(
        ChartListRefreshRoute route, BMSLibrary library, object parameter, object treeParameter,
        bool preserveSummary, Stopwatch stopwatch, ChartListFilterSnapshot filters, long expectedRequestId)
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
            if (disposed || currentRequestId != expectedRequestId)
            {
                return Task.FromResult(new RegularChartListEntryResult(false, RegularChartListEntryRoute.SubsetVirtual, sortWasReset: false));
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
                out PlaylistSourceRetirementRequest retirement, expectedRequestId))
        {
            return Task.FromResult(new RegularChartListEntryResult(false, RegularChartListEntryRoute.SubsetVirtual, sortWasReset));
        }
        request.DetailSourceRetirement = retirement;
        mainChartList.RowProjection.CaptureVersions(library);
        Task<RegularChartListEntryResult> execution;
        lock (syncRoot)
        {
            // StopAsyncと同じ保護内で実Taskを登録し、workerの起動が遅れても新しい要求のleaseを取り直さない。
            if (!IsCurrentUnsafe(lease))
            {
                return Task.FromResult(new RegularChartListEntryResult(false, RegularChartListEntryRoute.SubsetVirtual, sortWasReset));
            }
            execution = Task.Run(() => BuildAndApplyPackageViewAsync(packages, entry, request, lease, sort, sortWasReset));
            packageViewBuildTasks.Add(execution);
        }
        execution.ObserveFault("regularChartListPackageViewBuild");
        _ = ReleasePackageViewBuildAsync(execution);
        return execution;
    }

    private async Task<RegularChartListEntryResult> BuildAndApplyPackageViewAsync(
        ChartPackage[] packages, RegularChartListEntryRequest entry, RegularVirtualChartSubsetApplyRequest request,
        RegularChartListRequestLease lease, ChartListSortSpecification sort, bool sortWasReset)
    {
        if (lease.Token.IsCancellationRequested)
        {
            return new RegularChartListEntryResult(false, RegularChartListEntryRoute.SubsetVirtual, sortWasReset);
        }
        request.SourceEntries = CreatePackageEntrySnapshot(packages);
        RegularVirtualChartSubsetApplyResult result = BuildVirtualChartSubset(request, lease, out RegularChartListPresentationResult presentation, captureProjectionVersions: false);
        if (presentation != null)
        {
            presentation.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration cancellation = lease.Token.Register(() =>
                presentation.Completion.TrySetResult(RegularChartListTerminalResult.Stale()));
            Task delivery = Task.CompletedTask;
            try
            {
                await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
                {
                    _ = TryCommitVirtual(lease, presentation);
                }).ConfigureAwait(false);
                lock (syncRoot) { delivery = IsCurrentUnsafe(lease) ? pendingNormalDelivery : Task.CompletedTask; }
                RegularChartListTerminalResult terminal = await presentation.Completion.Task.ConfigureAwait(false);
                result = result.WithTerminal(terminal);
                await normalLibraryRefreshUiScheduler.InvokeAsync(() =>
                {
                    LogSubsetVirtualEntry(entry, sort, request.SubsetName, result, sortWasReset);
                    if (result.WasCommitted && sortWasReset)
                    {
                        lock (syncRoot) { if (IsCurrentUnsafe(lease)) { ResetSortParameters(sort); } }
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                presentation.Completion.TrySetResult(RegularChartListTerminalResult.Stale());
                Task finalDelivery;
                lock (syncRoot) { finalDelivery = IsCurrentUnsafe(lease) ? pendingNormalDelivery : Task.CompletedTask; }
                try { await Task.WhenAll(delivery, finalDelivery).ConfigureAwait(false); }
                catch { /* 配送失敗は同じ候補のCompletionへ元例外を保持済み。実Taskの終端だけを回収する。 */ }
                if (presentation.Completion.Task.IsFaulted) { _ = presentation.Completion.Task.Exception; }
            }
        }
        return new RegularChartListEntryResult(result.WasCommitted, RegularChartListEntryRoute.SubsetVirtual, sortWasReset);
    }

    private async Task ReleasePackageViewBuildAsync(Task execution)
    {
        try { await execution.ConfigureAwait(false); }
        catch { /* 元のTaskを呼出し元とObserveFaultへ保持し、追跡解除だけを行う。 */ }
        finally { lock (syncRoot) { packageViewBuildTasks.Remove(execution); } }
    }
}
