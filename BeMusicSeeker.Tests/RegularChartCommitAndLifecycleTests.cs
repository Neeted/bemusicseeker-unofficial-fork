using System;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RegularChartCommitAndLifecycleTests
{

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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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


    [TestMethod]
    public void TryCommit_RequestInvalidatedDuringPrepare_DoesNotReplaceRows()
    {
        var originalRows = new List<object>();
        var table = new MainChartListViewModel { Rows = originalRows };
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void TryCommit_RejectsPresentationOfDifferentKind()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void PresentationResult_SnapshotsRowsApplyRequest()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        RegularChartListPresentationResult presentation = RegularChartListPresentationResult.ForMaterialized(
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


    [TestMethod]
    public void TryCommitVirtual_NewerRequestPreventsStaleRowsFromReplacingCurrentRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void VirtualSummary_CurrentRequestUpdatesCommittedRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
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
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            new NoOpFolderAutoRenamePlaybackPort(),
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

        Assert.IsTrue(uiActionQueued.Wait(TimeSpan.FromSeconds(5)));
        pendingUiAction();
        Assert.AreEqual("[2" + BeMusicSeeker.Properties.Resources.Num_songs + " / 2" + BeMusicSeeker.Properties.Resources.Num_folders + "]", table.SummaryText);
    }


    [TestMethod]
    public void VirtualSummary_StaleRequestCannotUpdateNewerRows()
    {
        var table = new MainChartListViewModel();
        Action pendingUiAction = null!;
        using var uiActionQueued = new ManualResetEventSlim();
        var owner = new RegularChartListOwner(
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
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            new NoOpFolderAutoRenamePlaybackPort(),
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
        Assert.IsTrue(uiActionQueued.Wait(TimeSpan.FromSeconds(5)));

        RegularChartListRequestLease currentLease = owner.BeginRequest();
        var currentRows = new List<object> { new() };
        Assert.IsTrue(owner.TryCommitVirtual(currentLease, CreateVirtualTerminalInput(currentRows)).WasCommitted);
        string currentSummary = table.SummaryText;
        pendingUiAction();

        Assert.AreSame(currentRows, table.Rows);
        Assert.AreEqual(currentSummary, table.SummaryText);
    }


    [TestMethod]
    public void VirtualSummary_SameKeyRefreshSharesRunningScanWithLatestRows()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            Assert.IsTrue(sourceRows.IndexReadStarted.Wait(TimeSpan.FromSeconds(5)));

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
                Assert.IsTrue(summaryUpdated.Wait(TimeSpan.FromSeconds(5)));
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
            sourceRows.Dispose();
        }
    }


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
            Assert.IsTrue(sourceRows.IndexReadStarted.Wait(TimeSpan.FromSeconds(5)));

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
                Assert.IsTrue(summaryUpdated.Wait(TimeSpan.FromSeconds(5)),
                    "Actual summary: " + table.SummaryText
                    + Environment.NewLine + string.Join(Environment.NewLine, messages));
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
            sourceRows.Dispose();
        }
    }


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
        Assert.AreEqual(1_024, cancelledScanCount);
    }


    [TestMethod]
    public void VirtualSummary_OrderExposesReadOnlyIndexSnapshot()
    {
        ChartListOrder order = CreateOrder(
            CreateSourceRow("Folder A", "a.bms"),
            CreateSourceRow("Folder B", "b.bms"));

        Assert.IsFalse(order.Indexes is int[]);
        Assert.IsTrue(order.Indexes is IList<int> { IsReadOnly: true });
        Assert.ThrowsException<NotSupportedException>(
            () => ((IList<int>)order.Indexes)[0] = 1);
    }


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


    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualSummaryWithoutWaitingForUi()
    {
        var table = new MainChartListViewModel();
        var owner = CreateOwner(table, CreateWorkspaceForOwner());
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
            Assert.IsTrue(sourceRows.IndexReadStarted.Wait(TimeSpan.FromSeconds(5)));

            Task stop = owner.StopAsync();

            Assert.IsFalse(stop.IsCompleted);
            sourceRows.ReleaseIndexRead.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            sourceRows.ReleaseIndexRead.Set();
            sourceRows.Dispose();
        }
    }


    [TestMethod]
    public void Build_AfterCommittedSortCache_ReusesOwnedCache()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void InvalidatePendingRequest_DisablesNormalSummaryFreshness()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void TryCommit_RowsNotificationObservesCommittedCompletionAndColumnMode()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void TryCommit_RowsPublishNestedRequest_StopsRemainingOuterNotifications()
    {
        var table = new MainChartListViewModel();
        RegularChartListOwner owner = CreateOwner(table, new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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


    [TestMethod]
    public void Dispose_CancelsCurrentRequestAndRejectsNewRequests()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void VirtualOrderPrewarm_AllowsOneRunAndCompletionAllowsNextRun()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        Assert.AreEqual(first.RunId + 1, second.RunId);
        second.Dispose();
        owner.Dispose();
    }


    [TestMethod]
    public async Task StopAsync_CancelsAndDrainsVirtualOrderPrewarm()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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

        Assert.IsTrue(lease.Token.IsCancellationRequested);
        Assert.IsFalse(stopTask.IsCompleted);
        lease.Dispose();
        await stopTask;
        Assert.IsFalse(owner.TryBeginVirtualOrderPrewarm(null, out _));
        Assert.IsFalse(owner.TryBeginRequest(out _));
    }


    [TestMethod]
    public void SortKeyInvalidation_CancelsVirtualOrderPrewarm()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public void ClearSortCache_CancelsPrewarmAndRejectsItsLatePublication()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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


    [TestMethod]
    public async Task StopAsync_RejectsLateVirtualCachePublication()
    {
        RegularChartListOwner owner = CreateOwner(new MainChartListViewModel(), new PlaylistWorkspaceViewModel(
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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        RegularVirtualSourceRowsLookup sourceLookup = owner.LookupVirtualSourceRows(null, includeBmsonRows: false);
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

        owner.TryPublishVirtualSourceRows(sourceLookup, rows);
        Assert.IsFalse(owner.LookupVirtualSourceRows(null, includeBmsonRows: false).CacheHit);
        Assert.IsFalse(owner.TryPublishVirtualOrder(orderKey, CreateOrder(rows[0]), versions));
        Assert.IsFalse(owner.IsCurrentVirtualGeneration(orderKey.SourceGeneration, orderKey.SortKeyGeneration));
    }


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
            PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
            PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
            PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
            PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
            PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
            PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
        RegularChartListOwner owner = CreateOwner(table, workspace);
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


    [TestMethod]
    [DoNotParallelize]
    public void MainChartListColumnPresentation_LoadCommitAndReuseOwnsMainAndWorkspacePresentation()
    {
        CustomTableColumnSettings previousStandard = Settings.Default.StandardCustomTableColumnSettings;
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            var table = new MainChartListViewModel(
                action => action(),
                _ => { },
                new SettingsMainChartColumnSettingsStore(() => Settings.Default));
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
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            Settings.Default.StandardCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
            RegularChartListOwner owner = CreateOwner(table, workspace);

            owner.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);
            MainChartListColumnSelection second = table.ResolveColumnSettingForViewUpdate(
                MainViewUpdateMode.SortUpdated,
                MainViewUpdateMode.FolderFilterSelected);

            Assert.IsTrue(second.Reused);
            Assert.AreEqual(MainViewUpdateMode.FolderFilterSelected, second.AppliedMode);
            Assert.AreSame(table.ColumnsSettings, second.ColumnsSettings);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);

            CustomTableColumnSettings beforeInit = Settings.Default.StandardCustomTableColumnSettings;
            owner.InitializeColumnPresentation(MainViewUpdateMode.FolderFilterSelected);

            Assert.AreNotSame(beforeInit, Settings.Default.StandardCustomTableColumnSettings);
            Assert.AreSame(Settings.Default.StandardCustomTableColumnSettings, table.ColumnsSettings);
            owner.Dispose();
        }
        finally
        {
            Settings.Default.StandardCustomTableColumnSettings = previousStandard;
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }


    [TestMethod]
    [DoNotParallelize]
    public void ResetCurrentColumnPresentation_ResetsAppliedModeAndPublishesRelatedState()
    {
        CustomTableColumnSettings previousPlayHistory = Settings.Default.PlayHistoryCustomTableColumnSettings;
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
        {
            Settings.Default.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
            var table = new MainChartListViewModel(
                action => action(),
                _ => { },
                new SettingsMainChartColumnSettingsStore(() => Settings.Default));
            var workspace = CreateWorkspaceForOwner();
            var owner = CreateOwner(table, workspace);
            var notifications = new List<string>();
            table.PropertyChanged += (_, e) => notifications.Add("table:" + e.PropertyName);
            workspace.PropertyChanged += (_, e) => notifications.Add("workspace:" + e.PropertyName);

            owner.InitializeColumnPresentation(MainViewUpdateMode.PlayHistorySelected);
            Settings.Default.PlayHistoryCustomTableColumnSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAY_HISTORY);
            CustomTableColumnSettings persistedBeforeReset = Settings.Default.PlayHistoryCustomTableColumnSettings;
            Settings.Default.PlaylistSummaryColumnsSettings = new PlaylistSummaryColumnSettings();
            workspace.ColumnSettingsVisibilityForPlaylist = Visibility.Visible;
            notifications.Clear();

            owner.ResetCurrentColumnPresentation();

            Assert.AreNotSame(persistedBeforeReset, table.ColumnsSettings);
            Assert.AreSame(Settings.Default.PlayHistoryCustomTableColumnSettings, table.ColumnsSettings);
            Assert.AreEqual(MainViewUpdateMode.PlayHistorySelected, table.LastAppliedColumnMode);
            Assert.AreEqual(Visibility.Collapsed, workspace.ColumnSettingsVisibilityForPlaylist);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            int tableColumnsIndex = notifications.IndexOf("table:ColumnsSettings");
            int visibilityIndex = notifications.IndexOf("workspace:ColumnSettingsVisibilityForPlaylist");
            int summaryIndex = notifications.IndexOf("workspace:PlaylistSummaryColumnsSettings");
            Assert.IsTrue(tableColumnsIndex >= 0);
            Assert.IsTrue(visibilityIndex > tableColumnsIndex);
            Assert.IsTrue(summaryIndex > visibilityIndex);
            owner.Dispose();
        }
        finally
        {
            Settings.Default.PlayHistoryCustomTableColumnSettings = previousPlayHistory;
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }


    [TestMethod]
    public void ResetCurrentColumnPresentation_WithoutInitializationFailsWithoutMutation()
    {
        var table = new MainChartListViewModel(action => action());
        var workspace = CreateWorkspaceForOwner();
        var owner = CreateOwner(table, workspace);
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


    [TestMethod]
    [DoNotParallelize]
    public async Task PlaylistSummaryColumnSettings_ResetCreatesDefaultSettingsAndPublishesWorkspace()
    {
        PlaylistSummaryColumnSettings previousSummary = Settings.Default.PlaylistSummaryColumnsSettings;
        try
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
                PlaylistWorkspaceTestPorts.InactiveInstallQueueProvider,
            PlaylistWorkspaceTestPorts.PlaylistUrlInstallSink,
            PlaylistWorkspaceTestPorts.PlaylistUrlBrowserOpenSink,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportWarningLog,
                PlaylistWorkspaceTestPorts.ExternalPlaylistImportInfoLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportWarningLog,
                PlaylistWorkspaceTestPorts.BeatorajaTableUrlImportInfoLog,
                PlaylistWorkspaceTestPorts.PlaylistSummaryColumnSettingsStore,
                PlaylistWorkspaceTestPorts.PlaylistSummaryBmtSortCoordinator,
                PlaylistWorkspaceTestPorts.KeywordSearchHistorySettingsStore,
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
            Settings.Default.PlaylistSummaryColumnsSettings = oldSettings;
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

            Assert.AreNotSame(oldSettings, Settings.Default.PlaylistSummaryColumnsSettings);
            Assert.AreSame(Settings.Default.PlaylistSummaryColumnsSettings, workspace.PlaylistSummaryColumnsSettings);
            Assert.IsTrue(notificationCount > 0);
        }
        finally
        {
            Settings.Default.PlaylistSummaryColumnsSettings = previousSummary;
        }
    }
}
