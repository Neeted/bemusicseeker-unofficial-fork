using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>通常一覧の終端公開、summaryの世代判定、予備計算と停止・取消をWindowなしで検証します。</summary>
/// <remarks>gateは実workerの走査やUI公開を止めるために使います。sort値の網羅はLibraryChartRowSortEngineTests、compiled画面接続はMainWindow側が分担します。</remarks>
[TestClass]
public sealed class RegularChartCommitAndLifecycleTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();


    /// <summary>行置換準備から新要求が入ると入れ子終端だけを確定し、行と完了IDを最新要求へ揃えて古い外側の上書きを検出します。</summary>
    [TestMethod]
    public void TryCommit_RowsReplacingNestedRequest_LatestRequestWins()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult firstBuild = Build(owner, firstLease, new List<LibraryChartRow>());
        RegularChartListBuildResult nestedBuild = null!;
        RegularChartListTerminalResult nestedTerminal = default;
        long nestedRequestId = 0L;
        bool nested = false;
        table.RowsReplacing += (_, _) =>
        {
            if (nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            nestedRequestId = nestedLease.RequestId;
            nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            nestedTerminal = owner.TryCommit(nestedLease, CreateTerminalInput(nestedBuild));
        };

        RegularChartListTerminalResult firstTerminal = owner.TryCommit(firstLease, CreateTerminalInput(firstBuild));

        Assert.IsTrue(nestedTerminal.WasCommitted);
        Assert.IsFalse(firstTerminal.WasCommitted);
        Assert.AreSame(nestedBuild.Sort.RowsView, table.Rows);
        Assert.AreEqual(nestedRequestId, table.LastCompletion.RequestId);
        Assert.IsTrue(nestedRequestId > firstLease.RequestId);
    }


    /// <summary>準備中に要求を失効すると旧行を保持して取消通知を一回だけlock外で出し、古い確定と取消callbackへのlock持出しを検出します。</summary>
    [TestMethod]
    public void TryCommit_RequestInvalidatedDuringPrepare_DoesNotReplaceRows()
    {
        var originalRows = new List<object>();
        var table = new MainChartListViewModel { Rows = originalRows };
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int canceled = 0;
        table.RowsReplacing += (_, _) => owner.InvalidatePendingRequest();
        table.RowsReplacementCanceled += (_, _) =>
        {
            canceled++;
            Task lockProbe = StartLongRunning(owner.InvalidatePendingRequest);
            Assert.IsTrue(lockProbe.Wait(TimeSpan.FromSeconds(5)), "RowsReplacementCanceled must run after the regular owner lock is released.");
        };

        RegularChartListTerminalResult terminal = owner.TryCommit(lease, CreateTerminalInput(build));

        Assert.IsFalse(terminal.WasCommitted);
        Assert.AreSame(originalRows, table.Rows);
        Assert.AreEqual(1, canceled);
    }


    /// <summary>実体・仮想の終端種類違いと構築行以外の実体行指定を拒否し、別種類の候補や未構築行を確定する誤りを検出します。</summary>
    [TestMethod]
    public void TryCommit_RejectsPresentationOfDifferentKind()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());

        Assert.ThrowsException<ArgumentException>(() => owner.TryCommit(lease, CreateVirtualTerminalInput(new List<object>())));
        Assert.ThrowsException<ArgumentException>(() => owner.TryCommitVirtual(lease, CreateTerminalInput(build)));
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var stopwatch = Stopwatch.StartNew();
        Assert.ThrowsException<ArgumentException>(() => RegularChartListPresentationResult.ForMaterialized(
            build,
            new MainChartListRowsApplyRequest
            {
                Rows = new List<object>(),
                ColumnsSettings = settings,
                SelectionPolicy = MainChartListSelectionPolicy.Preserve,
                Summary = MainChartListSummaryUpdate.Preserve(),
                Stopwatch = stopwatch
            },
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            MainViewUpdateMode.UpdatedNone,
            stopwatch));
    }


    /// <summary>presentation生成後にcallerが行要求を差し替えても捕捉済み行を確定し、後続変更による別行の公開を検出します。</summary>
    [TestMethod]
    public void PresentationResult_SnapshotsRowsApplyRequest()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        var settings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        var stopwatch = Stopwatch.StartNew();
        var request = new MainChartListRowsApplyRequest
        {
            Rows = build.Sort.RowsView,
            ColumnsSettings = settings,
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.NormalRows(build.Sort.RowsView),
            Stopwatch = stopwatch
        };
        var presentation = RegularChartListPresentationResult.ForMaterialized(
            build,
            request,
            new MainChartListColumnSelection(
                settings,
                reused: false,
                elapsedMs: 0L,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                new PlaylistSummaryColumnSettings()),
            MainViewUpdateMode.UpdatedNone,
            stopwatch);

        request.Rows = new List<object>();
        request.Summary = MainChartListSummaryUpdate.Explicit("mutated");

        Assert.IsTrue(owner.TryCommit(lease, presentation).WasCommitted);
        Assert.AreSame(build.Sort.RowsView, table.Rows);
    }


    /// <summary>最新仮想行を確定した後の旧要求は準備も行わず拒否し、最新行と完了IDを保持して遅い旧結果の上書きを検出します。</summary>
    [TestMethod]
    public void TryCommitVirtual_NewerRequestPreventsStaleRowsFromReplacingCurrentRows()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        var staleRows = new List<object> { new() };
        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new(), new() };

        RegularChartListTerminalResult current = owner.TryCommitVirtual(
            currentLease,
            CreateVirtualTerminalInput(currentRows));
        int stalePrepareCount = 0;
        table.RowsReplacing += (_, _) => stalePrepareCount++;
        RegularChartListTerminalResult stale = owner.TryCommitVirtual(
            staleLease,
            CreateVirtualTerminalInput(staleRows));

        Assert.IsTrue(current.WasCommitted);
        Assert.IsFalse(stale.WasCommitted);
        Assert.AreEqual(0, stalePrepareCount);
        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentLease.RequestId, table.LastCompletion.RequestId);
    }


    /// <summary>現在要求の異なるfolder数をworkerで集計し、注入UI処理後に確定済み行のsummaryへ反映して集計結果の配送漏れを検出します。</summary>
    [TestMethod]
    public void VirtualSummary_CurrentRequestUpdatesCommittedRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        using var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                new SettingsMainChartColumnSettingsStore(() => testSettings),
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck),
            _ => { },
            action =>
            {
                pendingUiAction = action;
                uiActionQueued.Set();
            },
            _ => { },
            CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpChartMutationPlaybackPort(),
            new TestUiScheduler(() => null!));
        RegularChartListRequestLease lease = owner.BeginRequest();
        var rows = new List<object> { new(), new() };
        Assert.IsTrue(owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(rows)).WasCommitted);
        var key = new MainViewSummaryCacheKey(1, 1, rows.Count, includeBmsonRows: false, "test");
        ChartListSourceRow[] sourceRows =
        [
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms")
        ];

        owner.ScheduleVirtualSummary(
            lease,
            key,
            sourceRows,
            Enumerable.Range(0, sourceRows.Length).ToArray(),
            rows,
            "test");

        uiActionQueued.Wait();
        pendingUiAction();
        Assert.AreEqual("[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]", table.SummaryText);
    }


    /// <summary>旧summaryのUI処理前に新要求を確定すると旧公開を拒否し、新行とsummaryを保持して遅い集計の上書きを検出します。</summary>
    [TestMethod]
    public void VirtualSummary_StaleRequestCannotUpdateNewerRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        using var owner = new RegularChartListOwner(
            table,
            new PlaylistWorkspaceViewModel(
                action => action(),
                new MainChartListViewModel(action => action()),
                new PlaylistDetailBuildState(),
                new PlaylistDetailViewState(),
                _ => { },
                _ => { },
                () => new CustomFolderOutputSettingsSnapshot(),
                PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
                PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
                PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                new SettingsMainChartColumnSettingsStore(() => testSettings),
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
                PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
                PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
                () => null!,
                () => null!,
                _ => { },
                new ObservableCollection<BMSTable>(),
                (_, _) => false,
                () => true,
                () => MainViewUpdateMode.FolderFilterSelected,
                () => Task.CompletedTask,
                () => false,
                () => { },
                _ => { },
                (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck),
            _ => { },
            action =>
            {
                pendingUiAction = action;
                uiActionQueued.Set();
            },
            _ => { },
            CreatePendingPackageWorkflowOwner(),
            new ChartFileOperationSynchronizer(),
            new ChartMutationActivityOwner(),
            new NoOpChartMutationPlaybackPort(),
            new TestUiScheduler(() => null!));
        RegularChartListRequestLease staleLease = owner.BeginRequest();
        var staleRows = new List<object> { new(), new() };
        Assert.IsTrue(owner.TryCommitVirtual(staleLease, CreateVirtualTerminalInput(staleRows)).WasCommitted);
        var key = new MainViewSummaryCacheKey(1, 1, staleRows.Count, includeBmsonRows: false, "test");
        owner.ScheduleVirtualSummary(
            staleLease,
            key,
            [CreateSourceRow("Folder A", "a.bms"), CreateSourceRow("Folder B", "b.bms")],
            [0, 1],
            staleRows,
            "test");
        uiActionQueued.Wait();

        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new() };
        Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
        string currentSummary = table.SummaryText;
        pendingUiAction();

        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentSummary, table.SummaryText);
    }


    /// <summary>同じkeyの二要求はgate中のfolder走査を共有して最新行へ一回の走査結果を公開し、重複走査と旧行への配送を検出します。</summary>
    [TestMethod]
    public void VirtualSummary_SameKeyRefreshSharesRunningScanWithLatestRows()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var sourceRows = new BlockingIndexedSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        try
        {
            var key = new MainViewSummaryCacheKey(1, 1, 2, includeBmsonRows: false, "test");
            RegularChartListRequestLease firstLease = owner.BeginRequest();
            var firstRows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(firstLease, CreateVirtualTerminalInput(firstRows)).WasCommitted);
            owner.ScheduleVirtualSummary(firstLease, key, sourceRows, [0, 1], firstRows, "first");
            sourceRows.IndexReadStarted.Wait();

            RegularChartListRequestLease currentLease = owner.BeginRequest();
            var currentRows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
            owner.ScheduleVirtualSummary(currentLease, key, sourceRows, [0, 1], currentRows, "current");

            string expectedSummary = "[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]";
            using var summaryUpdated = new ManualResetEventSlim();
            PropertyChangedEventHandler summaryHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(MainChartListViewModel.SummaryText)
                    && string.Equals(table.SummaryText, expectedSummary, StringComparison.Ordinal))
                {
                    summaryUpdated.Set();
                }
            };
            table.PropertyChanged += summaryHandler;
            try
            {
                sourceRows.ReleaseIndexRead.Set();
                summaryUpdated.Wait();
            }
            finally
            {
                table.PropertyChanged -= summaryHandler;
            }
            Assert.AreSame(currentRows, table.Rows);
            Assert.AreEqual(2, sourceRows.IndexReadCount);
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            owner.StopAsync().GetAwaiter().GetResult();
            sourceRows.Dispose();
        }
    }


    /// <summary>旧走査の停止終端から同じkeyの最新走査を予約でき、最新行へ集計を完了して、退役済み作業へ参加して結果を失う競合を検出します。</summary>
    [TestMethod]
    public void VirtualSummary_StaleStopRetiresWorkBeforeLatestSameKeyRequestJoins()
    {
        var table = new MainChartListViewModel();
        var sourceRows = new BlockingIndexedSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        RegularChartListOwner owner = null!;
        RegularChartListRequestLease latestLease = null!;
        IList latestRows = null!;
        IReadOnlyList<int> indexes = Enumerable.Range(0, 5_000)
            .Select(index => index & 1)
            .ToArray();
        var key = new MainViewSummaryCacheKey(1, 1, indexes.Count, includeBmsonRows: false, "terminal-race");
        int rescheduleCount = 0;
        var messages = new ConcurrentQueue<string>();
        owner = CreateOwner(
            table,
            CreateWorkspaceForOwner(),
            action => action(),
            log: message =>
            {
                messages.Enqueue(message);
                if (message.Contains("stale_stopped", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref rescheduleCount);
                    owner.ScheduleVirtualSummary(
                        latestLease,
                        key,
                        sourceRows,
                        indexes,
                        latestRows,
                        "latest_from_terminal");
                }
            });
        try
        {
            RegularChartListRequestLease staleLease = owner.BeginRequest();
            var staleRows = new List<object> { new() };
            Assert.IsTrue(owner.TryCommitVirtual(staleLease, CreateVirtualTerminalInput(staleRows)).WasCommitted);
            owner.ScheduleVirtualSummary(staleLease, key, sourceRows, indexes, staleRows, "stale");
            sourceRows.IndexReadStarted.Wait();

            latestLease = owner.BeginRequest();
            latestRows = Enumerable.Repeat<object>(new(), indexes.Count).ToList();
            Assert.IsTrue(owner.TryCommitVirtual(latestLease, CreateVirtualTerminalInput(latestRows)).WasCommitted);

            string expectedSummary = "[" + indexes.Count + BeMusicSeeker.Properties.Resources.Num_songs
                + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]";
            using var summaryUpdated = new ManualResetEventSlim();
            PropertyChangedEventHandler summaryHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(MainChartListViewModel.SummaryText)
                    && string.Equals(table.SummaryText, expectedSummary, StringComparison.Ordinal))
                {
                    summaryUpdated.Set();
                }
            };
            table.PropertyChanged += summaryHandler;
            try
            {
                sourceRows.ReleaseIndexRead.Set();
                summaryUpdated.Wait();
            }
            finally
            {
                table.PropertyChanged -= summaryHandler;
            }
            Assert.AreEqual(1, Volatile.Read(ref rescheduleCount));
            Assert.AreSame(latestRows, table.Rows);
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            owner.StopAsync().GetAwaiter().GetResult();
            sourceRows.Dispose();
        }
    }


    /// <summary>対象indexだけの集計はfolder表記違いを統合し空folderと無効indexを除外し、大量indexは取消で全件前に止まることを確認します。</summary>
    [TestMethod]
    public void VirtualSummary_IndexedSnapshotCountsDistinctFoldersAndStopsAtChunkBoundary()
    {
        ChartListSourceRow[] sourceRows =
        [
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("folder a", "b.bms"),
            CreateSourceRow("Folder B", "c.bms"),
            CreateSourceRow(string.Empty, "d.bms"),
            CreateSourceRow("Folder C", "filtered-out.bms")
        ];
        int[] indexes = [2, 0, 1, 3, -1, sourceRows.Length];

        int indexed = RegularChartListOwner.CountDistinctFoldersByIndex(
            sourceRows,
            indexes,
            shouldStop: null,
            out int scanned,
            out bool stopped);

        Assert.AreEqual(2, indexed);
        Assert.AreEqual(indexes.Length, scanned);
        Assert.IsFalse(stopped);

        int[] largeIndexes = Enumerable.Repeat(0, 5_000).ToArray();
        _ = RegularChartListOwner.CountDistinctFoldersByIndex(
            sourceRows,
            largeIndexes,
            () => true,
            out int cancelledScanCount,
            out bool cancelled);

        Assert.IsTrue(cancelled);
        Assert.IsTrue(cancelledScanCount > 0 && cancelledScanCount < largeIndexes.Length);
    }


    /// <summary>公開orderのindexへ書込むと拒否し、受信側が共有整列順を直接変更する誤りを検出します。</summary>
    [TestMethod]
    public void VirtualSummary_OrderExposesReadOnlyIndexSnapshot()
    {
        ChartListOrder order = CreateOrder(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));

        Assert.IsTrue(order.Indexes is IList<int> { IsReadOnly: true });
        Assert.ThrowsException<NotSupportedException>(
            () => ((IList<int>)order.Indexes)[0] = 1);
    }


    /// <summary>生成後に生産側index配列を反転してもorderの捕捉順を保持し、後続caller変更による整列順の破壊を検出します。</summary>
    [TestMethod]
    public void VirtualSummary_OrderOwnsIndexSnapshotIndependentOfProducerArray()
    {
        int[] producerIndexes = [1, 0];
        ChartListOrder order = CreateOrder(
                CreateSourceRow("Folder A", "a.bms"),
                CreateSourceRow("Folder B", "b.bms"))
            .WithIndexes(producerIndexes);

        producerIndexes[0] = 0;
        producerIndexes[1] = 1;

        CollectionAssert.AreEqual(new[] { 1, 0 }, order.Indexes.ToArray());
    }


    /// <summary>folder走査をgateで保持した停止は未完了となり、解放後にworkerを回収してUI待ちを増やさず停止することを確認します。</summary>
    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualSummaryWithoutWaitingForUi()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner());
        var sourceRows = new BlockingIndexedSourceRows(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));
        try
        {
            RegularChartListRequestLease lease = owner.BeginRequest();
            var rows = new List<object> { new(), new() };
            Assert.IsTrue(owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(rows)).WasCommitted);
            var key = new MainViewSummaryCacheKey(1, 1, 2, includeBmsonRows: false, "shutdown");
            owner.ScheduleVirtualSummary(lease, key, sourceRows, [0, 1], rows, "shutdown");
            sourceRows.IndexReadStarted.Wait();

            Task stop = owner.StopAsync();

            Assert.IsFalse(stop.IsCompleted);
            sourceRows.ReleaseIndexRead.Set();
            await stop;
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            owner.StopAsync().GetAwaiter().GetResult();
            sourceRows.Dispose();
        }
    }


    /// <summary>確定したsortの次構築はowner cacheから同じ行viewを再利用し、同世代同入力の再整列を検出します。</summary>
    [TestMethod]
    public void Build_AfterCommittedSortCache_ReusesOwnedCache()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var source = new List<LibraryChartRow>();
        RegularChartListRequestLease firstLease = owner.BeginRequest();
        RegularChartListBuildResult first = Build(owner, firstLease, source, MainViewUpdateMode.FolderFilterSelected);
        Assert.IsTrue(owner.TryCommit(firstLease, CreateTerminalInput(first, MainViewUpdateMode.FolderFilterSelected)).WasCommitted);
        Assert.AreEqual(1, owner.CacheCount);

        RegularChartListRequestLease secondLease = owner.BeginRequest();
        RegularChartListBuildResult second = Build(owner, secondLease, source, MainViewUpdateMode.FolderFilterSelected);

        Assert.IsTrue(second.Sort.SortReuse);
        Assert.AreSame(first.Sort.RowsView, second.Sort.RowsView);
    }


    /// <summary>確定行が現在要求に属する状態から要求を失効するとsummary採用条件も失効し、旧集計の公開を許す誤りを検出します。</summary>
    [TestMethod]
    public void InvalidatePendingRequest_DisablesNormalSummaryFreshness()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        Assert.IsTrue(owner.TryCommit(lease, CreateTerminalInput(build)).WasCommitted);
        Assert.IsTrue(owner.IsCurrentRegularRows(table.Rows));

        owner.InvalidatePendingRequest();

        Assert.IsFalse(owner.IsCurrentRegularRows(table.Rows));
    }


    /// <summary>行通知の受信時点で完了IDと列modeを新終端へ揃え、途中状態をbindingへ公開する誤りを検出します。</summary>
    [TestMethod]
    public void TryCommit_RowsNotificationObservesCommittedCompletionAndColumnMode()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        long completionAtNotification = 0L;
        MainViewUpdateMode? columnModeAtNotification = null;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                completionAtNotification = table.LastCompletion.RequestId;
                columnModeAtNotification = table.LastAppliedColumnMode;
            }
        };

        Assert.IsTrue(owner.TryCommit(lease, CreateTerminalInput(build)).WasCommitted);

        Assert.AreEqual(lease.RequestId, completionAtNotification);
        Assert.AreEqual(MainViewUpdateMode.UpdatedNone, columnModeAtNotification);
    }


    /// <summary>行公開中に新要求を確定すると新行と完了IDを保ち、残った外側summary通知を止めて古い公開の続行を検出します。</summary>
    [TestMethod]
    public void TryCommit_RowsPublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        using RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        RegularChartListBuildResult nestedBuild = null!;
        long nestedRequestId = 0L;
        int summaryNotifications = 0;
        bool nested = false;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.SummaryText))
            {
                summaryNotifications++;
            }
            if (e.PropertyName != nameof(MainChartListViewModel.Rows) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            nestedRequestId = nestedLease.RequestId;
            nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            Assert.IsTrue(owner.TryCommit(nestedLease, CreateTerminalInput(nestedBuild)).WasCommitted);
        };

        Assert.IsTrue(owner.TryCommit(outerLease, CreateTerminalInput(outerBuild)).WasCommitted);

        Assert.AreSame(nestedBuild.Sort.RowsView, table.Rows);
        Assert.AreEqual(nestedRequestId, table.LastCompletion.RequestId);
        Assert.AreEqual(0, summaryNotifications);
    }


    /// <summary>workspace列公開中の新要求だけのsummary設定を通知し、外側の後続通知を止めて最新設定の上書きを検出します。</summary>
    [TestMethod]
    public void TryCommit_WorkspacePublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        PlaylistSummaryColumnSettings nestedSummarySettings = null!;
        int summaryNotifications = 0;
        bool nested = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                summaryNotifications++;
            }
            if (e.PropertyName != nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            RegularChartListBuildResult nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            nestedSummarySettings = new PlaylistSummaryColumnSettings();
            RegularChartListPresentationResult nestedInput = CreateTerminalInput(
                nestedBuild,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Collapsed,
                nestedSummarySettings);
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedInput).WasCommitted);
        };
        RegularChartListPresentationResult outerInput = CreateTerminalInput(
            outerBuild,
            MainViewUpdateMode.UpdatedNone,
            Visibility.Visible,
            new PlaylistSummaryColumnSettings());

        Assert.IsTrue(owner.TryCommit(outerLease, outerInput).WasCommitted);

        Assert.IsTrue(nested);
        Assert.AreSame(nestedSummarySettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, summaryNotifications);
    }


    /// <summary>同じ列表示への入れ子確定は必要な外側通知を省略せず一回ずつ公開し、変更なしの新要求による通知欠けを検出します。</summary>
    [TestMethod]
    public void TryCommit_NoOpNestedPresentation_DoesNotSuppressOuterNotifications()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease outerLease = owner.BeginRequest();
        RegularChartListBuildResult outerBuild = Build(owner, outerLease, new List<LibraryChartRow>());
        var sharedSummarySettings = new PlaylistSummaryColumnSettings();
        int visibilityNotifications = 0;
        int summaryNotifications = 0;
        bool nested = false;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.ColumnSettingsVisibilityForPlaylist))
            {
                visibilityNotifications++;
            }
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                summaryNotifications++;
            }
        };
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainChartListViewModel.Rows) || nested)
            {
                return;
            }
            nested = true;
            RegularChartListRequestLease nestedLease = owner.BeginRequest();
            RegularChartListBuildResult nestedBuild = Build(owner, nestedLease, new List<LibraryChartRow>());
            RegularChartListPresentationResult nestedInput = CreateTerminalInput(
                nestedBuild,
                MainViewUpdateMode.UpdatedNone,
                Visibility.Visible,
                sharedSummarySettings);
            Assert.IsTrue(owner.TryCommit(nestedLease, nestedInput).WasCommitted);
        };
        RegularChartListPresentationResult outerInput = CreateTerminalInput(
            outerBuild,
            MainViewUpdateMode.UpdatedNone,
            Visibility.Visible,
            sharedSummarySettings);

        Assert.IsTrue(owner.TryCommit(outerLease, outerInput).WasCommitted);

        Assert.IsTrue(nested);
        Assert.AreEqual(1, visibilityNotifications);
        Assert.AreEqual(1, summaryNotifications);
    }


    /// <summary>Disposeを繰返しても現在要求を取消して以後の受付を閉じ、終了後の新要求と二重解放を検出します。</summary>
    [TestMethod]
    public void Dispose_CancelsCurrentRequestAndRejectsNewRequests()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        RegularChartListRequestLease lease = owner.BeginRequest();

        owner.Dispose();
        owner.Dispose();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryBeginRequest(out _));
        Assert.ThrowsException<ObjectDisposedException>(() => owner.BeginRequest());
    }


    /// <summary>予備計算は一件だけ受理し、他leaseの取消は無効、現在leaseの取消後は新実行を受理して、実行取り違えと受付の閉じ忘れを検出します。</summary>
    [TestMethod]
    public void VirtualOrderPrewarm_AllowsOneRunAndCompletionAllowsNextRun()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));

        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease first));
        Assert.IsTrue(owner.IsVirtualOrderPrewarmRunning);
        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));

        using var staleCancellation = new CancellationTokenSource();
        using var staleLease = new RegularChartListPrewarmLease(-1, staleCancellation.Token, null);
        Assert.IsFalse(owner.CancelVirtualOrderPrewarm(staleLease));
        Assert.IsFalse(first.Token.IsCancellationRequested);
        Assert.IsTrue(owner.CancelVirtualOrderPrewarm(first));
        Assert.IsTrue(first.Token.IsCancellationRequested);

        Assert.IsFalse(owner.IsVirtualOrderPrewarmRunning);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease second));
        Assert.IsTrue(second.RunId > first.RunId);
        second.Dispose();
        owner.Dispose();
    }


    /// <summary>停止は予備計算を取消してleaseの実終端まで保留し、完了後は予備計算と通常要求の新受付を閉じることを確認します。</summary>
    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualOrderPrewarm()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        Task stopTask = owner.StopAsync();
        try
        {
            Assert.IsTrue(lease.Token.IsCancellationRequested);
            Assert.IsFalse(stopTask.IsCompleted);
            lease.Dispose();
            await stopTask;
            Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));
            Assert.IsFalse(owner.TryBeginRequest(out _));
        }
        finally
        {
            lease.Dispose();
            await stopTask;
        }
    }


    /// <summary>identity sort失効で稼働中の予備計算を取消し、旧キーの計算を継続する誤りを検出します。</summary>
    [TestMethod]
    public void SortKeyInvalidation_CancelsVirtualOrderPrewarm()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        owner.InvalidateIdentitySortKeys(clearSourceRows: false);

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        lease.Dispose();
        owner.Dispose();
    }


    /// <summary>sort cache消去で予備計算を取消し、既に捕捉した旧keyの後着公開も拒否して古いorderの復活を検出します。</summary>
    [TestMethod]
    public void ClearSortCache_CancelsPrewarmAndRejectsItsLatePublication()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey staleKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);
        Assert.IsTrue(owner.TryBeginVirtualOrderPrewarm(null, out RegularChartListPrewarmLease lease));

        owner.ClearSortCache();

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(owner.TryPublishVirtualOrder(
            staleKey,
            CreateOrder(CreateSourceRow("Folder A", "a.bms")),
            versions));
        lease.Dispose();
        owner.Dispose();
    }


    /// <summary>停止後は捕捉済みsourceとorderの公開を拒否し世代も現在扱いにせず、終了したownerのcache再生を検出します。</summary>
    [TestMethod]
    public async Task StopAsync_RejectsLateVirtualCachePublication()
    {
        using RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck));
        var rows = new List<ChartListSourceRow> { CreateSourceRow("Folder A", "a.bms") };
        var versions = new RegularChartListExternalVersions(score: 0, chartInfo: 0, maintenanceHydration: 0);
        NormalLibrarySortCacheKey orderKey = owner.CreateVirtualOrderKey(
            owner.SourceGeneration,
            owner.SortKeyGeneration,
            nameof(LibraryChartRow.Title),
            ListSortDirection.Ascending,
            rowCount: 1,
            externalVersions: versions);

        await owner.StopAsync();

        Assert.IsFalse(owner.TryPublishVirtualOrder(orderKey, CreateOrder(rows[0]), versions));
        Assert.IsFalse(owner.IsCurrentVirtualGeneration(orderKey.SourceGeneration, orderKey.SortKeyGeneration));
    }


    /// <summary>置換準備subscriberが例外を投げると元失敗を返して準備取消を一回通知し、未確定置換の回収漏れを検出します。</summary>
    [TestMethod]
    public void PrepareRowsApply_RowsReplacingThrows_CancelsPreparation()
    {
        var table = new MainChartListViewModel { Rows = new List<object>() };
        int canceled = 0;
        table.RowsReplacing += (_, _) => throw new InvalidOperationException("prepare failed");
        table.RowsReplacementCanceled += (_, _) => canceled++;

        Assert.ThrowsException<InvalidOperationException>(() => table.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = new List<object>(),
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD),
            SelectionPolicy = MainChartListSelectionPolicy.Preserve,
            Summary = MainChartListSummaryUpdate.Preserve(),
            Stopwatch = Stopwatch.StartNew()
        }));
        Assert.AreEqual(1, canceled);
    }


    /// <summary>行通知例外でも確定済み行を保持しworkspace列公開を続けて、最初の通知失敗で関連画面を旧状態へ残す誤りを検出します。</summary>
    [TestMethod]
    public void TryCommit_MainRowsPublishThrows_StillPublishesColumnPresentation()
    {
        var table = new MainChartListViewModel();
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        RegularChartListRequestLease lease = owner.BeginRequest();
        RegularChartListBuildResult build = Build(owner, lease, new List<LibraryChartRow>());
        int workspaceNotifications = 0;
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainChartListViewModel.Rows))
            {
                throw new InvalidOperationException("rows publish failed");
            }
        };
        workspace.PropertyChanged += (_, _) => workspaceNotifications++;
        RegularChartListPresentationResult input = CreateTerminalInput(
            build,
            MainViewUpdateMode.FolderFilterSelected,
            Visibility.Visible,
            new PlaylistSummaryColumnSettings());

        Assert.ThrowsException<RegularChartListTerminalPublishException>(() => owner.TryCommit(lease, input));
        Assert.AreSame(build.Sort.RowsView, table.Rows);
        Assert.IsTrue(workspaceNotifications > 0);
    }


    /// <summary>利用者設定から列とsummaryを公開し、sort更新は列を再利用、再初期化は新設定を再読取りして保存値との接続漏れを検出します。</summary>
    [TestMethod]
    public void MainChartListColumnPresentation_LoadCommitAndReuseOwnsMainAndWorkspacePresentation()
    {
        var table = new MainChartListViewModel(
            action => action(),
            _ => { },
            new SettingsMainChartColumnSettingsStore(() => testSettings));
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck);
        testSettings.StandardCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        testSettings.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        using RegularChartListOwner owner = CreateOwner(table, workspace);

        owner.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);
        MainChartListColumnSelection second = table.ResolveColumnSettingForViewUpdate(
            MainViewUpdateMode.SortUpdated,
            MainViewUpdateMode.FolderFilterSelected);

        Assert.IsTrue(second.Reused);
        Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, second.AppliedMode);
        Assert.AreSame(table.ColumnsSettings, second.ColumnsSettings);
        Assert.AreSame(testSettings.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);

        CustomTableColumnSettings beforeInit = testSettings.StandardCustomTableColumnSettings;
        owner.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);

        Assert.AreNotSame(beforeInit, testSettings.StandardCustomTableColumnSettings);
        Assert.AreSame(testSettings.StandardCustomTableColumnSettings, table.ColumnsSettings);
        owner.Dispose();
    }


    /// <summary>履歴modeの列リセットは保存値と列・summary表示を同じ新設定へ揃え、列通知後に関連表示を公開して旧設定残留と途中公開を検出します。</summary>
    [TestMethod]
    public void ResetCurrentColumnPresentation_ResetsAppliedModeAndPublishesRelatedState()
    {
        testSettings.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        testSettings.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        var table = new MainChartListViewModel(
            action => action(),
            _ => { },
            new SettingsMainChartColumnSettingsStore(() => testSettings));
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        var notifications = new List<string>();
        table.PropertyChanged += (_, e) => notifications.Add("table:" + e.PropertyName!);
        workspace.PropertyChanged += (_, e) => notifications.Add("workspace:" + e.PropertyName!);

        owner.InitializeColumnPresentation(MainViewUpdateMode.PlayHistorySelected);
        testSettings.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
        CustomTableColumnSettings persistedBeforeReset = testSettings.PlayHistoryCustomTableColumnSettings;
        testSettings.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
        workspace.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
        notifications.Clear();

        owner.ResetCurrentColumnPresentation();

        Assert.AreNotSame(persistedBeforeReset, table.ColumnsSettings);
        Assert.AreSame(testSettings.PlayHistoryCustomTableColumnSettings, table.ColumnsSettings);
        Assert.AreEqual(MainViewUpdateMode.PlayHistorySelected, table.LastAppliedColumnMode);
        Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.AreSame(testSettings.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
        int tableColumnsIndex = notifications.IndexOf("table:ColumnsSettings");
        int visibilityIndex = notifications.IndexOf("workspace:ColumnSettingsVisibilityForPlaylist");
        int summaryIndex = notifications.IndexOf("workspace:PlaylistSummaryColumnsSettings");
        Assert.IsTrue(tableColumnsIndex >= 0);
        Assert.IsTrue(visibilityIndex > tableColumnsIndex);
        Assert.IsTrue(summaryIndex > visibilityIndex);
        owner.Dispose();
    }


    /// <summary>mode未確定の列リセットは失敗し、列・summary・visibilityを保持して、対象modeのない設定破壊を検出します。</summary>
    [TestMethod]
    public void ResetCurrentColumnPresentation_WithoutInitializationFailsWithoutMutation()
    {
        var table = new MainChartListViewModel(action => action());
        PlaylistWorkspaceViewModel workspace = CreateWorkspaceForOwner();
        using RegularChartListOwner owner = CreateOwner(table, workspace);
        CustomTableColumnSettings columnsBefore = table.ColumnsSettings;
        PlaylistSummaryColumnSettings summaryBefore = workspace.PlaylistSummaryColumnsSettings;
        Visibility visibilityBefore = workspace.ColumnSettingsVisibilityForPlaylist;

        Assert.ThrowsException<InvalidOperationException>(() => owner.ResetCurrentColumnPresentation());

        Assert.AreSame(columnsBefore, table.ColumnsSettings);
        Assert.AreSame(summaryBefore, workspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(visibilityBefore, workspace.ColumnSettingsVisibilityForPlaylist);
        Assert.IsNull(table.LastAppliedColumnMode);
        owner.Dispose();
    }


    /// <summary>利用者が確認したsummary列リセットは設定を置換してworkspaceへ通知し、保存値だけ更新して画面が旧設定に留まる誤りを検出します。</summary>
    [TestMethod]
    public async Task PlaylistSummaryColumnSettings_ResetCreatesDefaultSettingsAndPublishesWorkspace()
    {
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            new MainChartListViewModel(action => action()),
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            _ => { },
            () => new CustomFolderOutputSettingsSnapshot(),
            PlaylistWorkspaceTestPorts.CreateUrlAcquisitionWorkflow(),
            PlaylistWorkspaceTestPorts.CreateExternalPackageLookupService(),
            PlaylistWorkspaceTestPorts.UrlAcquisitionOptionsProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            new SettingsMainChartColumnSettingsStore(() => testSettings),
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
            PlaylistWorkspaceTestPorts.KeywordSearchFavoritesSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistStoreProvider,
            PlaylistWorkspaceTestPorts.PlaylistPropertySaveService,
            () => null!,
            () => null!,
            _ => { },
            new ObservableCollection<BMSTable>(),
            (_, _) => false,
            () => true,
            () => MainViewUpdateMode.FolderFilterSelected,
            () => Task.CompletedTask,
            () => false,
            () => { },
            _ => { },
            (exception, message) => { }, (_, _) => false, (_, _) => false, PlaylistWorkspaceTestPorts.PlaylistRestoreUiApplyScheduler, PlaylistWorkspaceTestPorts.PlaylistRestoreUiThreadCheck, dialogs);
        var oldSettings = new PlaylistSummaryColumnSettings();
        testSettings.PlaylistSummaryColumnsSettings = oldSettings;
        workspace.CommitColumnPresentationWithoutNotification(
            workspace.ColumnSettingsVisibilityForPlaylist,
            oldSettings);
        int notificationCount = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistWorkspaceViewModel.PlaylistSummaryColumnsSettings))
            {
                notificationCount++;
            }
        };

        await workspace.ResetPlaylistSummaryColumnsToDefaultAsync();

        Assert.AreNotSame(oldSettings, testSettings.PlaylistSummaryColumnsSettings);
        Assert.AreSame(testSettings.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(notificationCount > 0);
    }
}
