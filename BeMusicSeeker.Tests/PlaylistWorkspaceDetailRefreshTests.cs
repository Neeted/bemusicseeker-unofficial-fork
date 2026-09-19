using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using static BeMusicSeeker.Tests.PlaylistWorkspaceTestDataSupport;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistWorkspaceDetailRefreshTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        TestResourceInitializer.EnsureJapaneseResources();
    }

    [TestMethod]
    public void BuildDetailSourceRows_FiltersFolderAndRemovedEntriesInsideWorkspace()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var included = new TestablePlaylistEntry("11111111111111111111111111111111", "included") { folder = "target" };
        var otherFolder = new TestablePlaylistEntry("22222222222222222222222222222222", "other") { folder = "other" };
        var removed = new TestablePlaylistEntry("33333333333333333333333333333333", "removed") { folder = "target", is_removed = true };
        var table = new BMSTable
        {
            entries = [included, otherFolder, removed]
        };
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            PlaylistDetailSelectionScope.Folder,
            "target",
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(included, result.SourceRows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("source_row_materialize", cancellationStage);
    }

    [TestMethod]
    public void BuildDetailSourceRows_OrdinaryRootRetainsSpecialFolderRows()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var normal = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "normal")
        {
            folder = "normal"
        };
        var special = new TestablePlaylistEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "special")
        {
            folder = "[NO SONG]"
        };
        var table = new BMSTable
        {
            entries = [normal, special]
        };
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            PlaylistDetailSelectionScope.OrdinaryRoot,
            folderName: null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot
            {
                ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty
            },
            CancellationToken.None,
            ref cancellationStage);

        CollectionAssert.AreEquivalent(
            new[] { normal, special },
            result.SourceRows.Select(row => row.Entry).ToArray(),
            "the ordinary playlist root must preserve its legacy [NO SONG] projection");
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_ReplacesCurrentGenerationWithoutMutatingOldRow()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('a', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var newInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = oldInfo.sha256,
            parser_version = 2,
            updated_at = new DateTime(2026, 2, 1)
        };
        var entry = new TestablePlaylistEntry("44444444444444444444444444444444", "patch");
        entry.SetSha256(oldInfo.sha256);
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: oldInfo);
        workspace.DetailViewState.Source.Rows = [oldRow];
        workspace.DetailBuildState.RequestVersion = 7;
        dataSource.ChartInfo = newInfo;
        var request = new PlaylistBuildRequest
        {
            RequestVersion = 7,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(),
                PlaylistDetailSelectionScope.OrdinaryRoot,
                null,
                PlaylistDetailFilter.PlaylistFilter,
                null,
                ChartModeFilter.All,
                null,
                libraryIndexVersion: 1,
                playlistRevision: 1,
                scoreSnapshotVersion: 1,
                chartInfoIndexVersion: 2,
                hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            request,
            CancellationToken.None,
            out int sourceCount,
            out int dependencyCount,
            out int patchedCount,
            out _);

        Assert.IsTrue(patched);
        Assert.AreEqual(1, sourceCount);
        Assert.AreEqual(1, dependencyCount);
        Assert.AreEqual(1, patchedCount);
        Assert.AreSame(oldInfo, oldRow.EntryChartInfo);
        Assert.AreNotSame(oldRow, workspace.DetailViewState.Source.Rows[0]);
        Assert.AreSame(newInfo, workspace.DetailViewState.Source.Rows[0].EntryChartInfo);
        Assert.AreEqual(2, workspace.DetailViewState.Source.LastBuiltChartInfoIndexVersion);
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_ReResolvesUnownedBeatorajaScoreFromHydratedChartInfo()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var entry = new TestablePlaylistEntry("66666666666666666666666666666666", "chart-info-score");
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        workspace.DetailViewState.Source.Rows = [oldRow];
        workspace.DetailBuildState.RequestVersion = 9;

        var chartInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('c', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 3, 1)
        };
        dataSource.ChartInfo = chartInfo;
        var score = new BMSScore
        {
            hash = entry.md5,
            clear = ClearType.HARD,
            rank = RankType.AAA,
            perfect = 100,
            totalnotes = 100
        };
        dataSource.ScoreSnapshot = new BMSLibrary.ScoreSnapshot
        {
            ActiveScoreSource = ActiveScoreSource.Beatoraja,
            LoadStatus = ScoreTableLoadStatus.Loaded,
            Version = 2,
            SourceGeneration = 1,
            ScoresBySha256 = new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase)
            {
                [chartInfo.sha256] = score
            }
        };
        var request = new PlaylistBuildRequest
        {
            RequestVersion = 9,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(),
                PlaylistDetailSelectionScope.OrdinaryRoot,
                null,
                PlaylistDetailFilter.PlaylistFilter,
                null,
                ChartModeFilter.All,
                null,
                libraryIndexVersion: 1,
                playlistRevision: 1,
                scoreSnapshotVersion: 2,
                chartInfoIndexVersion: 2,
                hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            request,
            CancellationToken.None,
            out _,
            out _,
            out int patchedCount,
            out _);

        Assert.IsTrue(patched);
        Assert.AreEqual(1, patchedCount);
        Assert.AreEqual(ClearType.NO_SONG, oldRow.clear);
        PlaylistDetailSourceRow patchedRow = workspace.DetailViewState.Source.Rows[0];
        Assert.AreNotSame(oldRow, patchedRow);
        Assert.AreSame(chartInfo, patchedRow.EntryChartInfo);
        Assert.AreEqual(ClearType.HARD, patchedRow.clear);
        Assert.AreEqual(RankType.AAA, patchedRow.rank);
        Assert.AreEqual(200, patchedRow.score);
        Assert.AreEqual(100, patchedRow.totalnotes);
    }

    [TestMethod]
    public void TryPatchDetailSourceChartInfo_RejectsStaleRequestWithoutReplacingSource()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = new string('b', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var entry = new TestablePlaylistEntry("55555555555555555555555555555555", "stale");
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null, entryChartInfo: oldInfo);
        List<PlaylistDetailSourceRow> oldRows = [oldRow];
        workspace.DetailViewState.Source.Rows = oldRows;
        workspace.DetailBuildState.RequestVersion = 8;
        dataSource.ChartInfo = new LR2SongDBExtended.chart_info
        {
            sha256 = oldInfo.sha256,
            parser_version = 2,
            updated_at = new DateTime(2026, 2, 1)
        };
        var staleRequest = new PlaylistBuildRequest
        {
            RequestVersion = 7,
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(), PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null,
                ChartModeFilter.All, null, 1, 1, 1, 2, hasResolvedSelection: true)
        };

        bool patched = workspace.TryPatchDetailSourceChartInfo(
            staleRequest,
            CancellationToken.None,
            out _, out _, out _, out _);

        Assert.IsFalse(patched);
        Assert.AreSame(oldRows, workspace.DetailViewState.Source.Rows);
        Assert.AreSame(oldInfo, oldRow.EntryChartInfo);
    }

    [TestMethod]
    public void BuildDetailSourceRows_OnlyNotOwnedExcludesResolvedLibraryCharts()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var owned = new TestablePlaylistEntry("66666666666666666666666666666666", "owned");
        var missing = new TestablePlaylistEntry("77777777777777777777777777777777", "missing");
        var table = new BMSTable { entries = [owned, missing] };
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromPath(LibraryChartKind.Bms, @"C:\songs\owned.bms", owned.md5, null)]);
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            PlaylistDetailSelectionScope.Folder,
            null,
            onlyNotOwned: true,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = resolveIndex },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(missing, result.SourceRows[0].Entry);
    }

    [TestMethod]
    public void BuildDetailSourceRows_CancellationIsNotHidden()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("88888888888888888888888888888888", "cancel")]
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        string cancellationStage = string.Empty;

        Assert.ThrowsException<OperationCanceledException>(() => workspace.BuildDetailSourceRows(
            table,
            PlaylistDetailSelectionScope.OrdinaryRoot,
            null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            cancellation.Token,
            ref cancellationStage));
    }

    [TestMethod]
    public async Task RequestDetailRefresh_OwnsRequestThroughRebuildAndTerminalApply()
    {
        var logs = new List<string>();
        var mainChartList = new MainChartListViewModel(action => action());
        var workspace = new PlaylistWorkspaceViewModel(
            action => action(),
            mainChartList,
            new PlaylistDetailBuildState(),
            new PlaylistDetailViewState(),
            _ => { },
            logs.Add,
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
        var dataSource = new FakePlaylistDetailDataSource();
        workspace.SetDetailDataSource(dataSource);
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "request-entry");
        var table = new BMSTable { entries = [entry] };
        workspace.RequestDetailSelection(table);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("request", ChartModeFilter.All));
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = "TITLE",
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Task requestCompletion = workspace.WaitForDetailRequestCompletionAsync(requestVersion);
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();
        await Task.WhenAll(requestCompletion, workerIdle).ConfigureAwait(false);
        Assert.AreEqual(requestVersion, workspace.DetailBuildState.RequestVersion);
        Assert.AreSame(table, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual(1, workspace.DetailViewState.Source.Rows.Count);
        Assert.AreSame(entry, workspace.DetailViewState.Source.Rows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("REQUEST", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
        Assert.IsTrue(mainChartList.LastCompletion.RequestId > 0L);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, mainChartList.LastCompletion.Mode);
        Assert.IsTrue(logs.Exists(log => log.Contains("main_view_build mode=PlaylistFilterSelected")
            && log.Contains("isPlaylistDetailView=true")));
    }

    [TestMethod]
    public void RequestDetailRefresh_AfterShutdownIsIgnoredWithoutStartingWorker()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.CancelDetailBuilds();
        workspace.RequestDetailSelection(new BMSTable());

        workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.IsTrue(workspace.DetailBuildState.ShutdownCancellationRequested);
        Assert.IsFalse(workspace.DetailBuildState.WorkerRunning);
        Assert.IsNull(workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public void RequestDetailRefresh_WithoutOwnerSelectionIsIgnored()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        int initialRequestVersion = workspace.DetailBuildState.RequestVersion;

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.AreEqual(0, requestVersion);
        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.AreEqual(initialRequestVersion, workspace.DetailBuildState.RequestVersion);
        Assert.IsNull(workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public void PlaylistSyncStatusOwner_ReplacesSourceKeyAndUsesSourceFallback()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var sourceTable = new BMSTable { playlist_id = 1 };
        var resultTable = new BMSTable { playlist_id = 2 };

        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                sourceTable,
                resultTable,
                new Uri("https://example.test/table"),
                updated: true));

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> snapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        Assert.IsFalse(snapshot.ContainsKey("id:1"));
        Assert.IsTrue(snapshot.ContainsKey("id:2"));
        Assert.AreEqual(PlaylistSyncStatusKind.Updated, snapshot["id:2"].Kind);

        var fallbackSourceTable = new BMSTable { name = "fallback" };
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                fallbackSourceTable,
                new BMSTable(),
                new Uri("https://example.test/fallback"),
                updated: false));

        snapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        Assert.IsTrue(snapshot.ContainsKey("name:fallback"));
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, snapshot["name:fallback"].Kind);
    }

    [TestMethod]
    public void PlaylistSyncStatusOwner_CapturesIsolatedSnapshots()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable { playlist_id = 3 };
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateSuccess(
                table,
                table,
                new Uri("https://example.test/initial"),
                updated: false));

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> firstSnapshot = workspace.CapturePlaylistSyncStatusSnapshot();
        workspace.RecordPlaylistSyncResult(
            PlaylistSyncAttemptResult.CreateFailure(
                table,
                new Uri("https://example.test/failure"),
                new InvalidOperationException("failure")));
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> secondSnapshot = workspace.CapturePlaylistSyncStatusSnapshot();

        Assert.AreNotSame(firstSnapshot["id:3"], secondSnapshot["id:3"]);
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, firstSnapshot["id:3"].Kind);
        Assert.AreEqual(PlaylistSyncStatusKind.UnknownError, secondSnapshot["id:3"].Kind);
        workspace.RecordPlaylistSyncResult(null);
        Assert.AreEqual(PlaylistSyncStatusKind.Ok, firstSnapshot["id:3"].Kind);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_SuppressesInactiveUntilLastScopeEnds()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.BeginPlaylistSyncProgressOperation();
        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            TotalTableCount = 2,
            CompletedTableCount = 1,
            CurrentTableName = "active"
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false
        });

        Assert.AreEqual(1, snapshots.Count);
        Assert.IsTrue(snapshots[0].IsActive);

        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(1, snapshots.Count);

        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(2, snapshots.Count);
        Assert.IsFalse(snapshots[1].IsActive);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_TracksBeatorajaOperationIdsExactlyOnce()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 41,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 41,
            TotalTableCount = 1,
            CompletedTableCount = 1
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 99
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 41
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 41
        });

        Assert.AreEqual(3, snapshots.Count);
        Assert.IsTrue(snapshots[0].IsActive);
        Assert.IsTrue(snapshots[1].IsActive);
        Assert.IsFalse(snapshots[2].IsActive);
    }

    [TestMethod]
    public void PlaylistSyncProgressOwner_BeatorajaCompletionDoesNotClearManualScope()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        List<PlaylistSyncProgressSnapshot> snapshots = [];
        workspace.PlaylistSyncProgressChanged += (_, request) => snapshots.Add(request.Snapshot);

        workspace.BeginPlaylistSyncProgressOperation();
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = true,
            OperationId = 7,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            OperationId = 7
        });

        Assert.AreEqual(1, snapshots.Count);
        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(2, snapshots.Count);
        Assert.IsFalse(snapshots[1].IsActive);
    }

    [TestMethod]
    public void TreeSelection_NormalFolderAndNotOwnedUseCanonicalDetailSelection()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, e) => request = e;

        workspace.RequestDetailSelection(table, PlaylistFolderNode.CreateFolder("Folder A"));

        PlaylistTreeSelectionActivatedEventArgs folderRequest = request
            ?? throw new AssertFailedException("Folder selection activation was not raised.");
        Assert.IsFalse(folderRequest.IsSummary);
        Assert.AreSame(table, folderRequest.Detail.Table);
        Assert.AreEqual("Folder A", folderRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistFilter, folderRequest.Detail.Filter);
        Assert.AreEqual(1L, folderRequest.SelectionRevision);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));

        workspace.RequestDetailSelection(
            table,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));

        PlaylistTreeSelectionActivatedEventArgs notOwnedRequest = request
            ?? throw new AssertFailedException("Not-owned selection activation was not raised.");
        Assert.AreSame(table, notOwnedRequest.Detail.Table);
        Assert.IsNull(notOwnedRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistNotOwnedFilterSelected, notOwnedRequest.Detail.Filter);
        Assert.AreEqual(2L, notOwnedRequest.SelectionRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(notOwnedRequest.Detail, notOwnedRequest.SelectionRevision));
    }

    [TestMethod]
    public void TreeSelection_SummaryCanBeRequestedRepeatedly()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        int requestCount = 0;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            Assert.IsTrue(request.IsSummary);
            requestCount++;
        };

        workspace.RequestSummarySelection();
        workspace.RequestSummarySelection();

        Assert.AreEqual(2, requestCount);
        Assert.IsNull(workspace.CapturePlaylistDetailSelection());
    }

    [TestMethod]
    public void TreeSelectionActivationsPublishOnCallerThread()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        int callerThreadId = Thread.CurrentThread.ManagedThreadId;
        List<int> requestThreadIds = [];
        workspace.TreeSelectionActivated += (_, _) => requestThreadIds.Add(Thread.CurrentThread.ManagedThreadId);

        workspace.RequestSummarySelection();
        workspace.RequestDetailSelection(new BMSTable());

        CollectionAssert.AreEqual(new[] { callerThreadId, callerThreadId }, requestThreadIds);
    }

    [TestMethod]
    public void TreeSelection_StaleSummaryIsNotActivatedAfterDetailSelection()
    {
        Queue<Action> pendingActions = new();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: action => pendingActions.Enqueue(action));
        List<PlaylistTreeSelectionActivatedEventArgs> activations = [];
        workspace.TreeSelectionActivated += (_, request) => activations.Add(request);

        workspace.RequestSummarySelection();
        workspace.RequestDetailSelection(new BMSTable());

        Assert.AreEqual(2, pendingActions.Count);
        pendingActions.Dequeue()();
        Assert.AreEqual(0, activations.Count);
        pendingActions.Dequeue()();
        Assert.AreEqual(1, activations.Count);
        Assert.IsFalse(activations[0].IsSummary);
    }

    [TestMethod]
    public void TreeSelection_CurrentSummaryActivationOwnsPresentation()
    {
        int presentationRefreshRequestCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
            {
                presentationRefreshRequestCount++;
                return request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation;
            });
        PlaylistTreeSelectionActivatedEventArgs? activation = null;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            if (request.IsSummary)
            {
                activation = request;
            }
        };

        workspace.RequestSummarySelection();
        PlaylistTreeSelectionActivatedEventArgs currentSummary = activation
            ?? throw new AssertFailedException("Summary selection activation was not raised.");

        Assert.IsTrue(currentSummary.SummaryModeChanged);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.IsTrue(workspace.IsPlaylistSummaryModeRequested);
        Assert.AreEqual(string.Empty, workspace.GridHeaderText);
        Assert.AreEqual(1, presentationRefreshRequestCount);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryPresentationRefresh());
    }

    [TestMethod]
    public void TreeSelection_ReentrantDetailSelectionPreventsStaleSummaryRefresh()
    {
        int summaryRefreshCount = 0;
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            presentationRefreshDeferredProvider: request =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.SummaryPresentation)
                {
                    summaryRefreshCount++;
                }
                return false;
            });
        var detailTable = new BMSTable();
        bool redirected = false;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            if (request.IsSummary && !redirected)
            {
                redirected = true;
                workspace.RequestDetailSelection(detailTable);
            }
        };

        workspace.RequestSummarySelection();

        Assert.IsTrue(redirected);
        Assert.IsFalse(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(0, summaryRefreshCount);
        Assert.AreSame(detailTable, workspace.CapturePlaylistDetailSelection().Table);
    }

    [TestMethod]
    public void TreeSelection_CurrentDetailActivationIsRejectedAfterClear()
    {
        Queue<Action> pendingActions = new();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
            out _,
            dispatchPresentation: action => pendingActions.Enqueue(action));
        int activationCount = 0;
        workspace.TreeSelectionActivated += (_, _) => activationCount++;

        workspace.RequestDetailSelection(new BMSTable());
        workspace.SetPlaylistSummaryMode(enabled: true);
        workspace.ClearPlaylistDetailSelection();

        Assert.AreEqual(1, pendingActions.Count);
        pendingActions.Dequeue()();
        Assert.AreEqual(0, activationCount);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
    }

    [TestMethod]
    public void TreeSelection_CurrentDetailActivationOwnsSummaryTransition()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var table = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? activation = null;
        workspace.TreeSelectionActivated += (_, request) =>
        {
            if (!request.IsSummary)
            {
                activation = request;
            }
        };
        workspace.SetPlaylistSummaryMode(enabled: true);
        Assert.IsTrue(workspace.TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest));

        workspace.RequestDetailSelection(table);
        PlaylistTreeSelectionActivatedEventArgs selected = activation
            ?? throw new AssertFailedException("Detail selection activation was not raised.");
        Assert.IsTrue(selected.SummaryModeChanged);
        Assert.IsTrue(buildRequest.CancellationToken.IsCancellationRequested);
        Assert.IsTrue(workspace.IsPlaylistSummaryMode);
        Assert.IsFalse(workspace.IsPlaylistSummaryModeRequested);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_summary_header, workspace.GridHeaderText);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(selected.Detail, selected.SelectionRevision));
        workspace.CompletePlaylistSummaryDataBuild(buildRequest);
    }

    [TestMethod]
    public async Task RequestDetailRefresh_StaleInputUsesCurrentOwnerSelection()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("old", ChartModeFilter.All));
        var oldTable = new BMSTable();
        var currentTable = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, selectionRequest) =>
        {
            if (!selectionRequest.IsSummary)
            {
                request = selectionRequest;
            }
        };
        workspace.RequestDetailSelection(oldTable);
        _ = request
            ?? throw new AssertFailedException("Initial detail selection request was not raised.");

        workspace.RequestDetailSelection(currentTable);
        workspace.RequestPlaylistDetailFilter(
            MainViewUpdateMode.KeywordFilterUpdated,
            new ChartListFilterSnapshot("current", ChartModeFilter.All));
        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Assert.IsTrue(requestVersion > 0);
        Task requestCompletion = workspace.WaitForDetailRequestCompletionAsync(requestVersion);
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();
        await Task.WhenAll(requestCompletion, workerIdle).ConfigureAwait(false);
        Assert.AreSame(currentTable, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual("CURRENT", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
    }

    [TestMethod]
    public void TreeSelection_NullTableSelectionRemainsResolvedAsSelectionState()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, e) => request = e;

        workspace.RequestDetailSelection(null);

        PlaylistTreeSelectionActivatedEventArgs detailRequest = request
            ?? throw new AssertFailedException("Null-table detail selection activation was not raised.");
        Assert.IsFalse(detailRequest.IsSummary);
        Assert.IsNotNull(detailRequest.Detail);
        Assert.IsNull(detailRequest.Detail.Table);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(
            detailRequest.Detail,
            detailRequest.SelectionRevision));
        Assert.IsNotNull(workspace.CapturePlaylistDetailSelection());
        Assert.IsNull(workspace.CapturePlaylistDetailSelection().Table);
    }

    [TestMethod]
    public void TreeSelection_OwnerPreservesReplacementRemapAndContentRevision()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var oldTable = new BMSTable();
        var newTable = new BMSTable();
        PlaylistTreeSelectionActivatedEventArgs? request = null;
        workspace.TreeSelectionActivated += (_, e) => request = e;

        workspace.RequestDetailSelection(oldTable, PlaylistFolderNode.CreateFolder("Folder A"));
        PlaylistTreeSelectionActivatedEventArgs selected = request
            ?? throw new AssertFailedException("Detail selection activation was not raised.");

        Assert.IsTrue(workspace.ReplaceCurrentPlaylistDetailSelectionTable(oldTable, newTable));
        PlaylistDetailSelection replaced = workspace.CapturePlaylistDetailSelection()
            ?? throw new AssertFailedException("Replaced detail selection was not retained.");
        Assert.AreSame(newTable, replaced.Table);
        Assert.AreEqual("Folder A", replaced.FolderName);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(selected.Detail, selected.SelectionRevision));

        Assert.IsTrue(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { ["Folder A"] = "Folder B" }));
        Assert.AreEqual("Folder B", workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.RequestDetailSelection(newTable, PlaylistFolderNode.CreateFolder(string.Empty));
        Assert.IsTrue(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { [string.Empty] = "Root Renamed" }));
        Assert.AreEqual("Root Renamed", workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.RequestDetailSelection(
            newTable,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
        Assert.IsFalse(workspace.RemapCurrentPlaylistDetailFolderSelection(
            newTable,
            new Dictionary<string, string> { [string.Empty] = "Not Owned Renamed" }));
        Assert.IsNull(workspace.CapturePlaylistDetailSelection().FolderName);

        workspace.DetailViewState.Source.PlaylistContentRevision = 9;
        Assert.IsTrue(workspace.MarkCurrentPlaylistDetailEntriesChanged(newTable, "test_entries_changed"));
        Assert.AreEqual(10, workspace.DetailViewState.Source.PlaylistContentRevision);
        Assert.IsFalse(workspace.MarkCurrentPlaylistDetailEntriesChanged(oldTable, "test_non_current_entries_changed"));
    }

    [TestMethod]
    public void RequestPlaylistDetailScoreSnapshotRefresh_DefersDuringEditAndPreservesHighestVersion()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.IsPlaylistDetailViewActive = true;
        workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion = 3;
        workspace.DetailViewState.Source.IsPlaylistCellEditing = true;
        var refreshes = new List<PlaylistDetailScoreSnapshotRefreshRequestedEventArgs>();
        workspace.PlaylistDetailScoreSnapshotRefreshRequested += (_, request) => refreshes.Add(request);

        workspace.RequestPlaylistDetailScoreSnapshotRefresh(5);
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(4);

        Assert.AreEqual(2, refreshes.Count);
        Assert.IsTrue(refreshes[0].DeferredByEdit);
        Assert.IsFalse(refreshes[0].RefreshRequired);
        Assert.IsTrue(refreshes[1].DeferredByEdit);
        Assert.IsFalse(refreshes[1].RefreshRequired);
        Assert.AreEqual(5, workspace.DetailViewState.Source.PendingScoreSnapshotRefreshVersion);
        workspace.DetailViewState.Source.IsPlaylistCellEditing = false;
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(5);
        Assert.AreEqual(3, refreshes.Count);
        Assert.IsTrue(refreshes[2].RefreshRequired);
        Assert.IsTrue(refreshes[2].DeferredByEdit == false);
        Assert.AreEqual(5, refreshes[2].ScoreSnapshotVersion);
        Assert.AreEqual(3, refreshes[2].LastBuiltVersion);
    }

    [TestMethod]
    public void RequestPlaylistDetailScoreSnapshotRefresh_IgnoresStaleOrInactiveRequests()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        workspace.DetailViewState.Source.LastBuiltScoreSnapshotVersion = 3;
        var refreshes = new List<PlaylistDetailScoreSnapshotRefreshRequestedEventArgs>();
        workspace.PlaylistDetailScoreSnapshotRefreshRequested += (_, request) => refreshes.Add(request);

        workspace.RequestPlaylistDetailScoreSnapshotRefresh(3);
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(4);
        Assert.AreEqual(0, refreshes.Count);

        workspace.IsPlaylistDetailViewActive = true;
        workspace.IsPlaylistSummaryMode = true;
        workspace.RequestPlaylistDetailScoreSnapshotRefresh(4);
        Assert.AreEqual(0, refreshes.Count);
    }

    [TestMethod]
    public void RequestPlaylistDetailReloadRefresh_PublishesReloadOpportunity()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        int refreshCount = 0;
        workspace.PlaylistDetailReloadRefreshRequested += (_, _) => refreshCount++;

        Assert.IsTrue(workspace.ShouldRefreshPlaylistDetailAfterReload(MainViewUpdateMode.PlaylistFilterSelected));
        workspace.RequestPlaylistDetailReloadRefresh();
        Assert.AreEqual(1, refreshCount);

        Assert.IsFalse(workspace.ShouldRefreshPlaylistDetailAfterReload(MainViewUpdateMode.PlayHistorySelected));
        workspace.RequestPlaylistDetailReloadRefresh();
        Assert.AreEqual(2, refreshCount);

        workspace.IsPlaylistSummaryMode = true;
        workspace.RequestPlaylistDetailReloadRefresh();
        Assert.AreEqual(3, refreshCount);
    }

    [TestMethod]
    public void SetDetailDataSource_ReinitializeUsesReplacementSource()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource firstSource);
        var replacementSource = new FakePlaylistDetailDataSource();
        using var activeBuildCancellation = new CancellationTokenSource();
        PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null,
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        workspace.DetailBuildState.RequestVersion = 10;
        workspace.DetailBuildState.CurrentBuildCancellation = activeBuildCancellation;
        workspace.DetailBuildState.CurrentBuildRequest = new PlaylistBuildRequest { Identity = identity };
        workspace.DetailBuildState.WorkerRunning = true;
        workspace.DetailViewState.Source.CurrentIdentity = identity.SourceIdentity;
        workspace.SetDetailDataSource(replacementSource);
        Assert.AreEqual(11, workspace.DetailBuildState.RequestVersion);
        var replacementRequest = new PlaylistBuildRequest { Identity = identity };
        PlaylistBuildQueueRegisterResult registerResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            workspace.DetailBuildState,
            replacementRequest,
            currentViewIdentity: null,
            lastBuiltScoreSnapshotVersion: 0,
            isShutdownRequested: false);
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("99999999999999999999999999999999", "replacement")]
        };
        string cancellationStage = string.Empty;

        workspace.BuildDetailSourceRows(
            table,
            PlaylistDetailSelectionScope.OrdinaryRoot,
            null,
            onlyNotOwned: false,
            new PlaylistLibraryIndexSnapshot { ResolveIndex = PlaylistLibraryResolveIndexSnapshot.Empty },
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(0, firstSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual(1, replacementSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual(12, workspace.DetailBuildState.RequestVersion);
        Assert.IsTrue(activeBuildCancellation.IsCancellationRequested);
        Assert.IsNull(workspace.DetailViewState.Source.CurrentIdentity);
        Assert.IsTrue(registerResult.Enqueued);
        Assert.AreSame(replacementRequest, workspace.DetailBuildState.PendingRequest);
    }

    [TestMethod]
    public async Task DetailRequestCompletionTracksAppliedSupersededAndShutdownTerminalStates()
    {
        var state = new PlaylistDetailBuildState();
        PlaylistRequestIdentity firstIdentity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, "first",
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        PlaylistRequestIdentity secondIdentity = PlaylistRequestFactory.CreateIdentity(
            new BMSTable(), PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, "second",
            ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true);
        var first = new PlaylistBuildRequest { Identity = firstIdentity };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, first, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        Task firstCompletion = PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(
            state, first.RequestVersion);
        Assert.IsFalse(firstCompletion.IsCompleted);
        var duplicate = new PlaylistBuildRequest { Identity = firstIdentity };
        PlaylistBuildQueueRegisterResult duplicateResult = PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, duplicate, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        Assert.IsFalse(duplicateResult.Enqueued);
        Assert.AreEqual(first.RequestVersion, duplicate.RequestVersion);

        var second = new PlaylistBuildRequest { Identity = secondIdentity };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, second, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        await firstCompletion.ConfigureAwait(false);
        Task secondCompletion = PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(
            state, second.RequestVersion);
        Task detailIdle = PlaylistDetailBuildQueueCoordinator.WaitForIdleAsync(state);
        Assert.IsFalse(secondCompletion.IsCompleted);
        Assert.IsFalse(detailIdle.IsCompleted);

        PlaylistDetailBuildQueueCoordinator.CancelForShutdown(state);
        await secondCompletion.ConfigureAwait(false);
        Assert.IsFalse(detailIdle.IsCompleted);
        Assert.IsFalse(PlaylistDetailBuildQueueCoordinator.TryTakeNextRequestOrStopWorker(state, out _));
        await detailIdle.ConfigureAwait(false);

        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator
            .WaitForRequestCompletionAsync(state, first.RequestVersion).IsCompleted);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator
            .WaitForRequestCompletionAsync(state, state.RequestVersion + 1).IsCompleted);
    }

    [TestMethod]
    public async Task RequestDetailRefresh_DataSourceFailureCompletesRequestAndStopsWorker()
    {
        var failureEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        dataSource.EnsureEntriesLoadedAction = () =>
        {
            failureEntered.TrySetResult(true);
            failureRelease.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("detail data source failed");
        };
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "failure")]
        };
        workspace.RequestDetailSelection(table);
        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);
        Task requestCompletion = workspace.WaitForDetailRequestCompletionAsync(requestVersion);
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();

        Exception? primaryFailure = null;
        try
        {
            await failureEntered.Task.ConfigureAwait(false);
            Assert.IsFalse(requestCompletion.IsCompleted);
            Assert.IsFalse(workerIdle.IsCompleted);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            // 本体の失敗を確定させた後、ゲートを開けて要求と worker の終端を回収する。
            failureRelease.TrySetResult(true);
            try
            {
                await Task.WhenAll(requestCompletion, workerIdle).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (primaryFailure != null)
            {
                TestContext.WriteLine("detail worker cleanup: " + cleanupFailure);
            }
        }
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.IsTrue(workspace.IsDetailBuildIdle);
        Assert.IsNull(workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual(0, workspace.DetailViewState.Source.Rows.Count);
    }

    [TestMethod]
    public async Task DetailSourceRetirementCompletesSupersededRequestWithoutDiscardingItsWaiter()
    {
        var state = new PlaylistDetailBuildState();
        var request = new PlaylistBuildRequest
        {
            Identity = PlaylistRequestFactory.CreateIdentity(
                new BMSTable(), PlaylistDetailSelectionScope.OrdinaryRoot, null, PlaylistDetailFilter.PlaylistFilter, null,
                ChartModeFilter.All, null, 1, 1, 1, 1, hasResolvedSelection: true)
        };
        PlaylistDetailBuildQueueCoordinator.RegisterRequest(
            state, request, currentViewIdentity: null, lastBuiltScoreSnapshotVersion: 0, isShutdownRequested: false);
        Task requestCompletion = PlaylistDetailBuildQueueCoordinator.WaitForRequestCompletionAsync(
            state, request.RequestVersion);

        PlaylistSourceRetirementRequest retirement = state.PrepareSourceRetirement();

        await requestCompletion.ConfigureAwait(false);
        Assert.IsTrue(PlaylistDetailBuildQueueCoordinator
            .WaitForRequestCompletionAsync(state, retirement.RequestVersion).IsCompleted);
    }

    [TestMethod]
    public async Task RequestDetailRefresh_UsesAttachedResolveIndexAndPreservesStorageOwnerIdentity()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var ownedChart = new BMSFile
        {
            path = @"C:\owned\chart.bms",
            hash = md5,
            title = "Owned chart"
        };
        dataSource.ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromBmsFile(ownedChart)]);

        var entry = new TestablePlaylistEntry(md5, "playlist entry");
        var table = new BMSTable { entries = [entry] };
        workspace.RequestDetailSelection(table);
        workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = "TITLE",
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        int requestVersion = workspace.RequestDetailRefresh(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        Task requestCompletion = workspace.WaitForDetailRequestCompletionAsync(requestVersion);
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();
        await Task.WhenAll(requestCompletion, workerIdle).ConfigureAwait(false);

        Assert.AreEqual(1, dataSource.ResolveIndexCallCount);
        Assert.AreEqual(1, workspace.DetailViewState.Source.Rows.Count);
        PlaylistDetailSourceRow row = workspace.DetailViewState.Source.Rows[0];
        Assert.AreSame(entry, row.Entry);
        Assert.IsTrue(row.IsOwned);
        Assert.AreSame(ownedChart, row.BmsPlayerFile);
    }

    [TestMethod]
    public async Task PlaylistLibraryIndexPrewarm_UsesWorkspaceSchedulerAndRuntimeCache()
    {
        var queuedWork = new List<Func<Task>>();
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource, (_, work) =>
        {
            queuedWork.Add(work);
            return true;
        });

        workspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");

        Assert.AreEqual(1, queuedWork.Count);
        await queuedWork[0]().ConfigureAwait(false);
        Assert.AreEqual(1, dataSource.ResolveIndexCallCount);

        dataSource.RuntimeState = new BMSLibrary.PlaylistLibraryResolveIndexRuntimeState
        {
            IsCached = true,
            OwnedCollectionVersion = (int)dataSource.OwnedChartCollectionVersion,
            BuildElapsedMs = 12L
        };
        PlaylistLibraryIndexReadinessSnapshot readiness = workspace.CapturePlaylistLibraryIndexReadinessSnapshot();
        Assert.AreEqual("cached", readiness.State);
        Assert.AreEqual(12L, readiness.BuildElapsedMs);
    }

    [TestMethod]
    public async Task PlaylistLibraryIndexPrewarm_RealDataSourceReusesWarmResolveIndexAfterTwoRemovals()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceDetailRefreshTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            File.WriteAllBytes(songDbPath, []);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.song>();
                db.CreateTable<LR2SongDB.folder>();
                db.CreateTable<LR2SongDBExtended.maintenance>();
                db.CreateTable<LR2SongDBExtended.bmson_song>();
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);

            string firstDirectory = Path.Combine(tempDirectory, "RemoveFirst");
            string secondDirectory = Path.Combine(tempDirectory, "RemoveSecond");
            string firstPath = Path.Combine(firstDirectory, "first.bms");
            string secondPath = Path.Combine(secondDirectory, "second.bms");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(secondPath, "#PLAYER 1");
            var first = new BMSFile
            {
                path = firstPath,
                hash = new string('a', 32)
            };
            var second = new BMSFile
            {
                path = secondPath,
                hash = new string('b', 32)
            };
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new FileDbReportRecordingDialogs())
            {
                BMSFiles = [first, second],
                BmsonSongs = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(first.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(second.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }

            library.GetOwnedChartHashIndexSnapshot();
            library.WarmInstalledPrimaryHashLookup("playlist_prewarm_two_removals");
            library.CreateInstalledChartLookupSnapshotForDiagnostics();
            PlaylistLibraryResolveIndexSnapshot initialResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int initialStaleRetries);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(0, initialStaleRetries);

            List<string> playlistWork = [];
            library.PlaylistLibraryResolveIndexStoreWorkObserver = playlistWork.Add;
            LibraryChartRemovalOutcome firstOutcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(first)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [firstDirectory]);
            LibraryChartRemovalOutcome secondOutcome = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(second)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [secondDirectory]);
            Assert.IsFalse(firstOutcome.HasError);
            Assert.IsFalse(secondOutcome.HasError);

            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var queuedWork = new List<Func<Task>>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                prewarmScheduler: (_, work) =>
                {
                    queuedWork.Add(work);
                    return true;
                });
            workspace.SetDetailDataSource(new PlaylistDetailDataSource(
                library,
                playlist,
                new MainChartRowProjectionOwner()));

            workspace.SchedulePlaylistLibraryIndexPrewarm("initialize_completed");
            Assert.AreEqual(1, queuedWork.Count);
            await queuedWork[0]().ConfigureAwait(false);

            PlaylistLibraryIndexReadinessSnapshot readiness = workspace.CapturePlaylistLibraryIndexReadinessSnapshot();
            Assert.AreEqual("cached", readiness.State);
            Assert.AreEqual(0, playlistWork.Count(operation =>
                operation == "playlist_resolve_source_enumeration"
                || operation == "playlist_resolve_full_root_enumeration"));
            PlaylistLibraryResolveIndexSnapshot finalResolve = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool finalCacheHit,
                out int finalStaleRetries);
            Assert.AreNotSame(initialResolve, finalResolve);
            Assert.IsTrue(finalCacheHit);
            Assert.AreEqual(0, finalStaleRetries);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistLibraryIndexPrewarm_DuplicateRefreshDefersUntilUiPriorityEnds()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource, (_, _) => true);
        workspace.BeginDuplicateRefreshPriorityWindow("test");

        workspace.InvalidatePlaylistLibraryIndexSnapshot(
            "owned_collection_changed",
            startupReadyOperable: true,
            MainViewUpdateMode.DuplicateFilterSelected);

        Assert.IsNull(workspace.GetPlaylistLibraryIndexPrewarmTask());
        workspace.ReleaseDuplicateRefreshPriorityWindow("test_done");
        Task prewarmTask = workspace.GetPlaylistLibraryIndexPrewarmTask();
        Assert.IsNotNull(prewarmTask);
        await prewarmTask.ConfigureAwait(false);
        Assert.AreEqual(1, dataSource.ResolveIndexCallCount);
    }

    [TestMethod]
    public void QueueExternalPlaylistSync_SchedulerRejectionPublishesSkippedLifecycle()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            int schedulerCalls = 0;
            var queued = new List<PlaylistExternalSyncRequestEventArgs>();
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                externalSyncScheduler: (_, _) =>
                {
                    schedulerCalls++;
                    return false;
                });
            workspace.PlaylistExternalSyncQueued += (_, request) => queued.Add(request);
            workspace.PlaylistExternalSyncCompleted += (_, request) => completed.Add(request);
            workspace.RefreshPlaylistTreeTables(playlist);

            workspace.QueueExternalPlaylistSync(
                "test_rejection",
                fromReloadTables: true,
                publishReferenceReceipt: false,
                operationToken: 11L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("test_rejection", queued[0].Reason);
            Assert.AreEqual(11L, queued[0].OperationToken);
            Assert.AreEqual(1, completed.Count);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.IsFalse(completed[0].PublishesReferenceReceipt);
            Assert.IsTrue(workspace.IsDeferredExternalPlaylistSyncIdle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistExternalSyncReceiptSubscription_FollowsTypedPlaylistStoreReplacement()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string firstDirectory = Path.Combine(tempDirectory, "first");
            string secondDirectory = Path.Combine(tempDirectory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            string firstSongDbPath = Path.Combine(firstDirectory, "song.db");
            string secondSongDbPath = Path.Combine(secondDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(firstSongDbPath))
            using (var __ = new LR2SongDBExtended(secondSongDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(firstSongDbPath);
            PlaylistPersistenceRepository.EnsureSchema(secondSongDbPath);

            string firstHeaderPath = Path.Combine(firstDirectory, "table.json");
            string secondHeaderPath = Path.Combine(secondDirectory, "table.json");
            string firstScorePath = Path.Combine(firstDirectory, "score.json");
            string secondScorePath = Path.Combine(secondDirectory, "score.json");
            File.WriteAllText(firstScorePath, "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"first\",\"level\":\"1\"}]", Encoding.UTF8);
            File.WriteAllText(secondScorePath, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"second\",\"level\":\"1\"}]", Encoding.UTF8);
            File.WriteAllText(firstHeaderPath, "{\"name\":\"first\",\"symbol\":\"F\",\"data_url\":\"./score.json\",\"level_order\":[1]}", Encoding.UTF8);
            File.WriteAllText(secondHeaderPath, "{\"name\":\"second\",\"symbol\":\"S\",\"data_url\":\"./score.json\",\"level_order\":[1]}", Encoding.UTF8);

            var firstPlaylist = new TestBmsPlaylist(firstSongDbPath);
            BMSTable firstTable = await firstPlaylist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(firstHeaderPath));
            firstTable.playlist_id = 8101;
            firstPlaylist.BMSTables = new ObservableCollection<BMSTable>([firstTable]);
            PersistPlaylistAggregate(firstSongDbPath, firstTable);

            var secondPlaylist = new TestBmsPlaylist(secondSongDbPath);
            BMSTable secondTable = await secondPlaylist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(secondHeaderPath));
            secondTable.playlist_id = 8102;
            secondPlaylist.BMSTables = new ObservableCollection<BMSTable>([secondTable]);
            PersistPlaylistAggregate(secondSongDbPath, secondTable);

            BMSPlaylist currentPlaylist = firstPlaylist;
            BMSLibrary currentLibrary = new TestBmsLibrary(firstSongDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => firstPlaylist,
                playlistLibraryProvider: () => currentLibrary);
            int sortInvalidationCount = 0;
            workspace.PlaylistReferenceSortInvalidationRequested += (_, _) => sortInvalidationCount++;
            workspace.RefreshPlaylistTreeTables(firstPlaylist, currentLibrary);

            File.WriteAllText(firstHeaderPath, "{\"name\":\"first-updated\",\"symbol\":\"F2\",\"data_url\":\"./score.json\",\"level_order\":[1]}", Encoding.UTF8);
            firstTable.header_sha256 = null;

            currentPlaylist = secondPlaylist;
            currentLibrary = new TestBmsLibrary(secondSongDbPath);
            workspace.RefreshPlaylistTreeTables(secondPlaylist, currentLibrary);

            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> oldResults =
                await firstPlaylist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [firstTable],
                    reason: "typed_old_store_receipt",
                    publishReferenceReceipts: true);

            Assert.IsTrue(oldResults.Single().Succeeded);
            Assert.AreEqual(0, sortInvalidationCount, "Receipts from the retired store must be ignored.");
            Assert.AreEqual("second", workspace.PlaylistTreeTables.Single().name);

            File.WriteAllText(secondScorePath, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"second-updated\",\"level\":\"1\"}]", Encoding.UTF8);
            secondTable.header_sha256 = null;
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> currentResults =
                await secondPlaylist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [secondTable],
                    reason: "typed_current_store_receipt",
                    publishReferenceReceipts: true);

            Assert.IsTrue(currentResults.Single().Succeeded);
            Assert.AreEqual(1, sortInvalidationCount, "The active store receipt must reach the workspace.");
            TestUiDispatcherHost.Drain();
            Assert.AreEqual("second-updated", secondPlaylist.BMSTables.Single().entries.Single().title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task PlaylistTreeStoreReplacement_WaitsForCurrentHydrationApplyThroughTypedReceipt()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        Task? apply = null;
        Task? replacement = null;
        Task? hydration = null;
        Exception? primaryFailure = null;
        var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacementNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            string firstDirectory = Path.Combine(tempDirectory, "first");
            string secondDirectory = Path.Combine(tempDirectory, "second");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            string firstSongDbPath = Path.Combine(firstDirectory, "song.db");
            string secondSongDbPath = Path.Combine(secondDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(firstSongDbPath))
            using (var __ = new LR2SongDBExtended(secondSongDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(firstSongDbPath);
            PlaylistPersistenceRepository.EnsureSchema(secondSongDbPath);
            var firstPlaylist = new TestBmsPlaylist(firstSongDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([
                    new BMSTable { playlist_id = 8301, name = "first" }])
            };
            var secondPlaylist = new TestBmsPlaylist(secondSongDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([
                    new BMSTable { playlist_id = 8302, name = "second" }])
            };
            firstPlaylist.BMSTables[0].MarkEntriesNotLoaded();
            var applyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task>? scheduledHydration = null;
            PlaylistHydrationCompletionReceipt? requestReceipt = null;
            ObservableCollection<BMSTable>? requestTables = null;
            long requestGeneration = 0L;
            int requestedVersion = 0;
            firstPlaylist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                scheduledHydration = work;
                return true;
            };
            var firstLibrary = new TestBmsLibrary(firstSongDbPath);
            var secondLibrary = new TestBmsLibrary(secondSongDbPath);
            BMSLibrary currentLibrary = firstLibrary;
            int replacementRouteExpected = 0;
            var replacementCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementRouteEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaylistWorkspaceViewModel workspace = CreateBareHydrationWorkspace(
                () => firstPlaylist,
                () =>
                {
                    // Resolving the library only proves that the public route
                    // was scheduled. The replacement completion assertion below
                    // uses the typed catalog-notification port, which is reached
                    // after AttachPlaylistTreeStore has invalidated the receipt.
                    if (Volatile.Read(ref replacementRouteExpected) == 1)
                    {
                        replacementCallStarted.TrySetResult(true);
                    }
                    return currentLibrary;
                });
            workspace.ConfigureCatalogNotificationQueue(action =>
            {
                if (Volatile.Read(ref replacementRouteExpected) == 1)
                {
                    // PublishPlaylistCatalogChanged is invoked only after the
                    // store replacement and receipt invalidation have completed.
                    // Hold this owner-scoped typed port so the replacement task
                    // cannot complete before the test observes that boundary.
                    replacementRouteEntered.TrySetResult(true);
                    // Action 型の通知キュー内では await できないため、外側の Task が解放を所有する。
                    releaseReplacementNotification.Task.GetAwaiter().GetResult();
                }

                action();
            });
            workspace.PlaylistEntriesHydrationRequested += (_, request) =>
            {
                requestReceipt = request.CompletionReceipt;
                requestTables = request.SourceTables;
                requestGeneration = request.Generation;
                requestedVersion = request.Version;
                if (!workspace.TryBeginPlaylistHydrationNotification(
                    request.SourceStore,
                    request.SourceTables,
                    request.Generation,
                    request.CompletionReceipt))
                {
                    return;
                }
                workspace.ExecuteCurrentPlaylistHydrationNotification(
                    request.SourceStore,
                    request.SourceTables,
                    request.Generation,
                    request.CompletionReceipt,
                    () => { });
            };
            workspace.PlaylistPresentationRefreshRequested += (_, request) =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.Tree)
                {
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, deferred: false);
                }
            };
            workspace.RefreshPlaylistTreeTables(firstPlaylist);
            firstPlaylist.QueueDeferredPlaylistEntriesHydration("typed_replacement_barrier");
            Assert.IsNotNull(scheduledHydration);
            Assert.IsTrue(requestedVersion > 0);
            Assert.IsNotNull(requestReceipt);
            Assert.IsTrue(requestReceipt!.IsRequestPublished);
            Assert.IsNotNull(requestTables);

            workspace.PlaylistEntriesHydrationCompleted += (_, _) => { };
            apply = Task.Run(() =>
            {
                Assert.IsTrue(workspace.TryBeginPlaylistHydrationNotification(
                    firstPlaylist,
                    requestTables!,
                    requestGeneration,
                    requestReceipt));
                Assert.IsTrue(workspace.PublishPlaylistEntriesHydrationCompleted(
                    requestedVersion,
                    firstPlaylist,
                    requestTables,
                    requestGeneration,
                    requestReceipt));
                Assert.IsTrue(workspace.ExecuteCurrentPlaylistHydrationNotification(
                    firstPlaylist,
                    requestTables,
                    requestGeneration,
                    requestReceipt,
                    () =>
                    {
                        applyEntered.TrySetResult(true);
                        // 同期 callback の境界では、外側のテストが解放した結果を直接待つ。
                        releaseApply.Task.GetAwaiter().GetResult();
                    }));
                workspace.ApplyPlaylistEntriesHydrationCompleted(
                    requestedVersion,
                    deferred: false,
                    firstPlaylist,
                    requestTables,
                    requestGeneration,
                    requestReceipt);
            });
            await applyEntered.Task.ConfigureAwait(false);

            currentLibrary = secondLibrary;
            Volatile.Write(ref replacementRouteExpected, 1);
            replacement = Task.Run(() => workspace.RefreshPlaylistTreeTables(secondPlaylist));
            await replacementCallStarted.Task.ConfigureAwait(false);
            Assert.IsFalse(replacementRouteEntered.Task.IsCompleted);
            Assert.AreSame(firstPlaylist.BMSTables, workspace.PlaylistTreeTables);

            releaseApply.TrySetResult(true);
            await replacementRouteEntered.Task.ConfigureAwait(false);
            Assert.IsFalse(replacement.IsCompleted, "Store replacement must wait for the active terminal hydration apply.");
            Assert.AreSame(secondPlaylist.BMSTables, workspace.PlaylistTreeTables);
            releaseReplacementNotification.TrySetResult(true);
            hydration = Task.Run(scheduledHydration!);
            await Task.WhenAll(apply, replacement, hydration).ConfigureAwait(false);
            Assert.AreSame(secondPlaylist.BMSTables, workspace.PlaylistTreeTables);
            Assert.AreEqual(
                firstPlaylist.PlaylistEntriesHydrationRequestedVersion,
                firstPlaylist.PlaylistEntriesHydrationCompletedVersion);
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
            throw;
        }
        finally
        {
            releaseApply.TrySetResult(true);
            releaseReplacementNotification.TrySetResult(true);
            Task[] pendingTasks = [
                .. new[] { apply, replacement, hydration }
                    .Where(task => task != null)
                    .Cast<Task>()];
            if (pendingTasks.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pendingTasks).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure) when (primaryFailure != null)
                {
                    TestContext.WriteLine("playlist tree replacement cleanup: " + cleanupFailure);
                }
            }
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DiscardDeferredExternalPlaylistSyncForShutdown_PublishesSkippedLifecycle()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            Func<Task>? scheduledWork = null;
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                externalSyncScheduler: (_, work) =>
                {
                    scheduledWork = work;
                    return true;
                });
            workspace.PlaylistExternalSyncCompleted += (_, request) => completed.Add(request);
            workspace.RefreshPlaylistTreeTables(playlist);

            workspace.QueueExternalPlaylistSync(
                "test_shutdown_discard",
                fromReloadTables: false,
                publishReferenceReceipt: true,
                operationToken: 17L);

            Assert.IsNotNull(scheduledWork);
            workspace.DiscardDeferredExternalPlaylistSyncForShutdown("test_shutdown");

            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual("test_shutdown_discard", completed[0].Reason);
            Assert.AreEqual(1, completed[0].Version);
            Assert.AreEqual(17L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].PublishesReferenceReceipt);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.IsTrue(workspace.IsDeferredExternalPlaylistSyncIdle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void QueueExternalPlaylistSync_CoalescesAndRunsLatestRequestSnapshot()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            int schedulerCalls = 0;
            Func<Task>? scheduledWork = null;
            var queued = new List<PlaylistExternalSyncRequestEventArgs>();
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                externalSyncScheduler: (_, work) =>
                {
                    schedulerCalls++;
                    scheduledWork = work;
                    return true;
                });
            workspace.PlaylistExternalSyncQueued += (_, request) => queued.Add(request);
            workspace.PlaylistExternalSyncCompleted += (_, request) => completed.Add(request);
            workspace.RefreshPlaylistTreeTables(playlist);

            workspace.QueueExternalPlaylistSync(
                "first_request",
                fromReloadTables: false,
                publishReferenceReceipt: false,
                operationToken: 1L);
            workspace.QueueExternalPlaylistSync(
                "latest_request",
                fromReloadTables: false,
                publishReferenceReceipt: true,
                operationToken: 2L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.IsNotNull(scheduledWork);
            Assert.AreEqual(2, queued.Count);
            Assert.AreEqual("latest_request", queued[1].Reason);
            Assert.AreEqual(2L, queued[1].OperationToken);
            Assert.IsTrue(queued[1].PublishesReferenceReceipt);

            scheduledWork!().GetAwaiter().GetResult();

            Assert.IsTrue(workspace.IsDeferredExternalPlaylistSyncIdle);
            Assert.IsTrue(completed.Any(request =>
                request.Version == 2
                && request.Succeeded
                && request.PublishesReferenceReceipt));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void QueuePlaylistReferenceApply_SchedulerRejectionPublishesSkippedLifecycle()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            int schedulerCalls = 0;
            var queued = new List<PlaylistReferenceApplyQueuedEventArgs>();
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                referenceApplyScheduler: (_, _) =>
                {
                    schedulerCalls++;
                    return false;
                });
            workspace.PlaylistReferenceApplyWorkflow.Queued += (_, request) => queued.Add(request);
            workspace.PlaylistReferenceApplyWorkflow.Completed += (_, request) => completed.Add(request);

            workspace.PlaylistReferenceApplyWorkflow.Queue("test_rejection", 11L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("test_rejection", queued[0].Reason);
            Assert.AreEqual(11L, queued[0].OperationToken);
            Assert.AreEqual(1, completed.Count);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.AreEqual(1, workspace.PlaylistReferenceApplyWorkflow.LastCompletedVersion);
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void QueuePlaylistReferenceApply_CoalescesAndRunsLatestRequestSnapshot()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var hydrationTable = new BMSTable { name = "Hydration" };
            hydrationTable.MarkEntriesNotLoaded();
            playlist.BMSTables.Add(hydrationTable);
            playlist.StartupBackgroundTaskScheduler = (_, _, _, _) => true;
            playlist.QueueDeferredPlaylistEntriesHydration("queued_before_reference");
            var library = new TestBmsLibrary(songDbPath);
            int schedulerCalls = 0;
            Func<Task>? scheduledWork = null;
            var queued = new List<PlaylistReferenceApplyQueuedEventArgs>();
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            var presentation = new List<PlaylistReferenceApplyPresentationRequestedEventArgs>();
            var lifecycle = new List<string>();
            bool hydrationArbitrationObservedAfterLifecycle = false;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                referenceApplyScheduler: (_, work) =>
                {
                    schedulerCalls++;
                    scheduledWork = work;
                    return true;
                },
                presentationRefreshDeferredProvider: request =>
                {
                    if (request.Kind == PlaylistPresentationRefreshKind.HydrationCompleted)
                    {
                        hydrationArbitrationObservedAfterLifecycle = lifecycle.Contains("hydration");
                    }
                    return false;
                });
            workspace.RefreshPlaylistTreeTables(playlist);
            workspace.PlaylistReferenceApplyWorkflow.Queued += (_, request) => queued.Add(request);
            workspace.PlaylistReferenceApplyWorkflow.Completed += (_, request) => completed.Add(request);
            workspace.PlaylistEntriesHydrationCompleted += (_, _) => lifecycle.Add("hydration");
            workspace.PlaylistReferenceApplyWorkflow.PresentationRequested += (_, request) =>
            {
                lifecycle.Add("presentation");
                presentation.Add(request);
            };

            workspace.PlaylistReferenceApplyWorkflow.Queue("first_request", 1L);
            workspace.PlaylistReferenceApplyWorkflow.Queue("latest_request", 2L);

            Assert.AreEqual(1, schedulerCalls);
            Assert.IsNotNull(scheduledWork);
            Assert.AreEqual(2, queued.Count);
            Assert.AreEqual("latest_request", queued[1].Reason);
            Assert.AreEqual(2L, queued[1].OperationToken);

            scheduledWork!().GetAwaiter().GetResult();

            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual(2, completed[0].Version);
            Assert.AreEqual("latest_request", completed[0].Reason);
            Assert.AreEqual(2L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].Succeeded);
            Assert.AreEqual(1, presentation.Count);
            Assert.AreEqual(2, presentation[0].Version);
            Assert.AreEqual(2L, presentation[0].OperationToken);
            Assert.IsTrue(
                hydrationArbitrationObservedAfterLifecycle,
                "Hydration lifecycle must be published before presentation arbitration.");
            CollectionAssert.AreEqual(new[] { "hydration", "presentation" }, lifecycle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaylistTreeHydrationReceipt_RequestsPresentationRefreshAfterSnapshotApply()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>(new[]
                    {
                        new BMSTable
                        {
                            playlist_id = 7053,
                            name = "ReceiptPresentation"
                        }
                    })
            };
            playlist.BMSTables[0].MarkEntriesNotLoaded();
            Task scheduledWork = null!;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                scheduledWork = work();
                return true;
            };
            var library = new TestBmsLibrary(songDbPath);
            Func<Task>? referenceApplyWork = null;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                referenceApplyScheduler: (_, work) =>
                {
                    referenceApplyWork = work;
                    return true;
                });
            var presentations = new List<PlaylistReferenceApplyPresentationRequestedEventArgs>();
            workspace.PlaylistReferenceApplyWorkflow.PresentationRequested += (_, request) => presentations.Add(request);

            workspace.RefreshPlaylistTreeTables(playlist);
            playlist.QueueDeferredPlaylistEntriesHydration("receipt_presentation");

            Assert.IsNotNull(scheduledWork);
            scheduledWork.GetAwaiter().GetResult();
            Assert.IsNotNull(referenceApplyWork);
            referenceApplyWork!().GetAwaiter().GetResult();
            Assert.AreEqual(1, presentations.Count);
            Assert.AreEqual("receipt_presentation", presentations[0].Reason);
            Assert.AreEqual(1, presentations[0].Version);
            Assert.AreEqual(0L, presentations[0].OperationToken);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void PlaylistTreeHydrationReceipt_DoesNotApplySnapshotInvalidatedBeforeWorkspaceConsumption()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7054,
                name = "StaleReceipt"
            };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            Task hydrationWork = null!;
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                hydrationWork = work();
                return true;
            };
            playlist.PlaylistEntriesHydrationReceiptPublished += (_, _) =>
            {
                using (table.ReaderWriterLock.GetWriterGuard())
                {
                    table.MarkEntriesNotLoaded();
                }
            };
            int referenceApplyScheduleCount = 0;
            var library = new TestBmsLibrary(songDbPath);
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                referenceApplyScheduler: (_, _) =>
                {
                    referenceApplyScheduleCount++;
                    return true;
                });
            workspace.RefreshPlaylistTreeTables(playlist);

            playlist.QueueDeferredPlaylistEntriesHydration("stale_receipt");

            Assert.IsNotNull(hydrationWork);
            hydrationWork.GetAwaiter().GetResult();
            Assert.AreEqual(
                0,
                referenceApplyScheduleCount,
                "An invalidated model receipt must not wake reference application with an old snapshot.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void DiscardPlaylistReferenceApplyForShutdown_PublishesSkippedLifecycle()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            Func<Task>? scheduledWork = null;
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(
                out _,
                playlistStoreProvider: () => playlist,
                referenceApplyScheduler: (_, work) =>
                {
                    scheduledWork = work;
                    return true;
                });
            workspace.PlaylistReferenceApplyWorkflow.Completed += (_, request) => completed.Add(request);

            workspace.PlaylistReferenceApplyWorkflow.Queue("test_shutdown_discard", 17L);
            Assert.IsNotNull(scheduledWork);

            workspace.PlaylistReferenceApplyWorkflow.DiscardForShutdown("test_shutdown");

            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual("test_shutdown_discard", completed[0].Reason);
            Assert.AreEqual(1, completed[0].Version);
            Assert.AreEqual(17L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].WasSkipped);
            Assert.IsFalse(completed[0].Succeeded);
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

}
