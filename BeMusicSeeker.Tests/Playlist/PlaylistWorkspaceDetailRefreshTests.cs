using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistWorkspaceFixtureFactory;
using static BeMusicSeeker.Tests.PlaylistWorkspaceTestDataSupport;
using PlaylistWorkspaceViewModelTests = BeMusicSeeker.Tests.PlaylistWorkspaceExternalSourceTests;

namespace BeMusicSeeker.Tests;

/// <summary>detailの入力構築・選択の世代・通知・必須receipt・参照反映と受付の実終端を検証します。</summary>
/// <remarks>判断境界は制御したsourceとschedulerで競争条件を作り、DBや物理削除に依存するケースは実資源を使います。実VMへの表示適用とWPF接続は上位ケースが分担します。</remarks>
[TestClass]
public sealed class PlaylistWorkspaceDetailRefreshTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>指定folderだけを対象に未削除の行をsourceへ作り、別folder・削除行を除外しながら必要な行読込を一回行うことを確認します。</summary>
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
            PlaylistLibraryResolveIndexSnapshot.Empty,
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(included, result.SourceRows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
    }

    /// <summary>通常rootの詳細sourceでは通常folderと旧特殊[NO SONG]行の双方を保持し、lamp全体scopeの除外規則を通常rootへ誤適用することを防ぎます。</summary>
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
            PlaylistLibraryResolveIndexSnapshot.Empty,
            CancellationToken.None,
            ref cancellationStage);

        CollectionAssert.AreEquivalent(
            new[] { normal, special },
            result.SourceRows.Select(row => row.Entry).ToArray(),
            "the ordinary playlist root must preserve its legacy [NO SONG] projection");
    }

    /// <summary>現要求のchart情報更新を新しいsource行へ反映し、旧行の情報を変えず新しい情報版を採用して既存表示の破壊や更新漏れを検出します。</summary>
    [TestMethod]
    public void TryPatchDetailSourceChartInfo_ReplacesCurrentGenerationWithoutMutatingOldRow()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new BeMusicSeeker.Models.ChartDetails
        {
            sha256 = new string('a', 64),
            parser_version = 1,
            updated_at = new DateTime(2026, 1, 1)
        };
        var newInfo = new BeMusicSeeker.Models.ChartDetails
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

    /// <summary>未所持行へSHA情報を補完した際にbeatoraja scoreを再解決し、旧NO_SONG行を変えず新行のclear・rank・scoreへ配送します。</summary>
    [TestMethod]
    public void TryPatchDetailSourceChartInfo_ReResolvesUnownedBeatorajaScoreFromHydratedChartInfo()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var entry = new TestablePlaylistEntry("66666666666666666666666666666666", "chart-info-score");
        var oldRow = new PlaylistDetailSourceRow(entry, resolvedChart: null);
        workspace.DetailViewState.Source.Rows = [oldRow];
        workspace.DetailBuildState.RequestVersion = 9;

        var chartInfo = new BeMusicSeeker.Models.ChartDetails
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

    /// <summary>古い要求版での情報patchを拒否し、既存source集合と旧情報の参照を保って後発表示を上書きしないことを確認します。</summary>
    [TestMethod]
    public void TryPatchDetailSourceChartInfo_RejectsStaleRequestWithoutReplacingSource()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var oldInfo = new BeMusicSeeker.Models.ChartDetails
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
        dataSource.ChartInfo = new BeMusicSeeker.Models.ChartDetails
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

    /// <summary>同じ表の所持・欠損行へ未所持条件を適用し、resolveできる実library参照だけを除外することを確認します。</summary>
    [TestMethod]
    public void BuildDetailSourceRows_OnlyNotOwnedExcludesResolvedLibraryCharts()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _);
        var owned = new TestablePlaylistEntry("66666666666666666666666666666666", "owned");
        var missing = new TestablePlaylistEntry("77777777777777777777777777777777", "missing");
        var table = new BMSTable { entries = [owned, missing] };
        var resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(
            [LibraryChartRef.FromPath(ChartFileKind.Bms, @"C:\songs\owned.bms", owned.md5, null)]);
        string cancellationStage = string.Empty;

        PlaylistSourceBuildResult result = workspace.BuildDetailSourceRows(
            table,
            PlaylistDetailSelectionScope.Folder,
            null,
            onlyNotOwned: true,
            resolveIndex,
            CancellationToken.None,
            ref cancellationStage);

        Assert.AreEqual(1, result.SourceRows.Count);
        Assert.AreSame(missing, result.SourceRows[0].Entry);
    }

    /// <summary>取消済tokenのsource構築をOperationCanceledExceptionで終端し、取消を空集合の正常完了へ隠さないことを確認します。</summary>
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
            PlaylistLibraryResolveIndexSnapshot.Empty,
            cancellation.Token,
            ref cancellationStage));
    }

    /// <summary>保証：detail要求のproducerから適用終端までを待ち、同条件再利用と500→3000の情報更新後の検索集合を保つ。</summary>
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
        var buildEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataSource = new FakePlaylistDetailDataSource
        {
            EnsureEntriesLoadedAction = () => { buildEntered.TrySetResult(); buildRelease.Task.GetAwaiter().GetResult(); },
            ChartInfo = new BeMusicSeeker.Models.ChartDetails { md5 = new string('a', 32), notes = 500 }
        };
        workspace.SetDetailDataSource(dataSource);
        var entry = new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "request-entry");
        var highEntry = new TestablePlaylistEntry(new string('b', 32), "request-high");
        var tiedEntry = new TestablePlaylistEntry(new string('c', 32), "request-tied");
        // 参照だけの索引ではChartInfoを落とすため、固定入力の共通値を含む既存snapshot入口を使う。
        dataSource.ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromCharts(
            new[] { highEntry, tiedEntry }.Select(item =>
                ChartFileProjection.WithChartInfo(ChartTestValues.Empty() with
                {
                    Path = item.title + ".bms",
                    Md5 = item.md5,
                    RawTitle = item.title,
                    Title = item.title
                }, new BeMusicSeeker.Models.ChartDetails { md5 = item.md5, notes = 2500 })));
        var table = new BMSTable { entries = [entry, highEntry, tiedEntry] };
        workspace.RequestDetailSelection(table);
        workspace.InitializePlaylistDetailFilter(new ChartListFilterSnapshot("request notes:>=2000", ChartModeFilter.All));
        workspace.InitializePlaylistDetailSort(new ChartListSortParameters
        {
            ColumnsName = nameof(PlaylistDetailRow.ChartNotes),
            Direction = System.ComponentModel.ListSortDirection.Ascending
        });

        Task requestCompletion = workspace.RequestDetailRefreshTask(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);

        try
        {
            await Task.WhenAny(buildEntered.Task, requestCompletion);
            if (!buildEntered.Task.IsCompleted) { await requestCompletion; Assert.Fail("実detail producerに到達しませんでした。"); }
            Assert.IsFalse(requestCompletion.IsCompleted);
            Assert.AreEqual(0L, mainChartList.LastCompletion.RequestId);
        }
        finally
        {
            buildRelease.TrySetResult();
            await Task.WhenAll(requestCompletion, workspace.WaitForDetailBuildIdleAsync()).ConfigureAwait(false);
        }
        Assert.AreSame(table, workspace.DetailViewState.Source.CurrentTable);
        Assert.AreEqual(3, workspace.DetailViewState.Source.Rows.Count);
        Assert.AreSame(entry, workspace.DetailViewState.Source.Rows[0].Entry);
        Assert.AreEqual(1, dataSource.EnsureEntriesLoadedCallCount);
        Assert.AreEqual("REQUEST NOTES:>=2000", workspace.DetailViewState.View.CurrentIdentity?.KeywordFilter);
        CollectionAssert.AreEquivalent(new[] { "request-high", "request-tied" },
            workspace.DetailViewState.View.Rows.Cast<PlaylistDetailRow>().Select(row => row.Title).ToArray());
        Assert.IsTrue(mainChartList.LastCompletion.RequestId > 0L);
        Assert.AreEqual(MainViewUpdateMode.PlaylistFilterSelected, mainChartList.LastCompletion.Mode);
        Assert.IsTrue(logs.Exists(log => log.Contains("main_view_build mode=PlaylistFilterSelected")
            && log.Contains("isPlaylistDetailView=true")));
        for (int phase = 0; phase < 2; phase++)
        {
            if (phase == 1)
            {
                dataSource.ChartInfo = new BeMusicSeeker.Models.ChartDetails { md5 = entry.md5, notes = 3000 };
                // 固定入力の情報更新を受けた後、通常の内容更新受付でsourceを再構成する。
                workspace.IncrementDetailContentRevision("accepted_notes_update");
            }
            requestCompletion = workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
                MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected,
                useCoalescingWindow: false, openReadiness: default);
            try
            {
                await requestCompletion.ConfigureAwait(false);
            }
            finally
            {
                await Task.WhenAll(requestCompletion, workspace.WaitForDetailBuildIdleAsync()).ConfigureAwait(false);
            }
            CollectionAssert.AreEquivalent(phase == 0 ? new[] { "request-high", "request-tied" }
                : new[] { "request-entry", "request-high", "request-tied" },
                workspace.DetailViewState.View.Rows.Cast<PlaylistDetailRow>().Select(row => row.Title).ToArray());
            Assert.IsTrue(workspace.IsDetailBuildIdle);
        }
    }

    /// <summary>detail終了取消後の選択更新と再要求を未開始として扱い、worker・pending要求を残さず終了状態を維持します。</summary>
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

    /// <summary>管理主体の選択がないdetail要求を登録せず、要求版・pending・idleを維持して空のworker起動を防ぎます。</summary>
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

    /// <summary>同期後に表IDが変われば結果側へ状態を移し、結果の識別子が欠ける場合はsource名へ戻して表示状態の紛失を防ぎます。</summary>
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

    /// <summary>成功状態捕捉後の失敗記録とnull入力が先のsnapshotを変えず、新しいsnapshotだけに失敗を反映することを確認します。</summary>
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

    /// <summary>重なる二つの手動scope中の非active通知を抑え、最後のscopeが終わる時だけ非activeを公開して早過ぎる進捗消去を検出します。</summary>
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

    /// <summary>同一IDの複数active通知を一つの実行として扱い、無関係IDと重複完了を無視して対応IDの終端だけを公開します。</summary>
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

    /// <summary>BMT進捗の完了を公開しても重なる手動scopeを維持し、手動終端を別に通知して異なる操作の進捗を取り消さないことを確認します。</summary>
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
            Source = "bmt",
            OperationId = 7,
            TotalTableCount = 1,
            CompletedTableCount = 0
        });
        workspace.ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            Source = "bmt",
            OperationId = 7
        });

        Assert.AreEqual(2, snapshots.Count);
        Assert.AreEqual("bmt", snapshots[1].Source);
        Assert.IsFalse(snapshots[1].IsActive);
        workspace.EndPlaylistSyncProgressOperation();
        Assert.AreEqual(3, snapshots.Count);
        Assert.AreEqual("playlist", snapshots[2].Source);
        Assert.IsFalse(snapshots[2].IsActive);
    }

    /// <summary>通常folderと未所持選択を異なるfilter・scopeへ変換し、選択版が変わるたびに旧選択を失効させることを確認します。</summary>
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
        Assert.IsTrue(folderRequest.SelectionRevision > 0L);
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));

        workspace.RequestDetailSelection(
            table,
            PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));

        PlaylistTreeSelectionActivatedEventArgs notOwnedRequest = request
            ?? throw new AssertFailedException("Not-owned selection activation was not raised.");
        Assert.AreSame(table, notOwnedRequest.Detail.Table);
        Assert.IsNull(notOwnedRequest.Detail.FolderName);
        Assert.AreEqual(PlaylistDetailFilter.PlaylistNotOwnedFilterSelected, notOwnedRequest.Detail.Filter);
        Assert.IsTrue(notOwnedRequest.SelectionRevision > folderRequest.SelectionRevision);
        Assert.IsFalse(workspace.IsCurrentPlaylistDetailSelection(folderRequest.Detail, folderRequest.SelectionRevision));
        Assert.IsTrue(workspace.IsCurrentPlaylistDetailSelection(notOwnedRequest.Detail, notOwnedRequest.SelectionRevision));
    }

    /// <summary>同じsummary選択を二回明示要求すると二回のactivationを届け、detail選択を残さず再選択機会を失わないことを確認します。</summary>
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

    /// <summary>summaryとdetailの選択要求を呼出元threadへ公開し、即時配送境界を別threadへ暗黙転送しないことを確認します。</summary>
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

    /// <summary>summaryの配送を保留してdetailを後から選び、古いsummary callbackを拒否して現detailだけを公開します。</summary>
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

    /// <summary>summary選択で表示希望と遅延refreshを確定し、表示適用前の実modeを切り替えないことを確認します。</summary>
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
        Assert.AreEqual(1, presentationRefreshRequestCount);
        Assert.IsTrue(workspace.HasDeferredPlaylistSummaryPresentationRefresh());
    }

    /// <summary>summary通知の受信中にdetailへ再選択し、古いsummaryの後続refreshを止めて後発detailを保持することを確認します。</summary>
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

    /// <summary>detailの配送待ち中に選択をclearし、古いactivationを公開せずsummary表示とheaderを保持します。</summary>
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

    /// <summary>summary build中のdetail選択でbuildを取り消し、希望modeだけをdetailへ移して適用前のsummary表示を維持します。</summary>
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

    /// <summary>古い選択から現選択・keywordへ更新してdetail要求を完了し、sourceとviewが現管理主体の条件を採用することを確認します。</summary>
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

    /// <summary>null表のdetail選択を未選択と区別してactivationと現選択状態を保持し、選択意図を失わないことを確認します。</summary>
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

    /// <summary>表参照置換と通常・空名folderの改名に選択を追従させ、未所持scopeを誤改名せず現表の内容変更だけを版へ反映します。</summary>
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

    /// <summary>編集中のscore更新を保留して最大版を保持し、編集終了後の同じ最新版要求で必要refreshを公開することを確認します。</summary>
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

    /// <summary>detail非activeとsummary表示中のscore更新を公開せず、表示対象外へ不要な更新を配送しないことを確認します。</summary>
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

    /// <summary>mode別の再読込判断を区別しつつ、明示refresh機会の通知は履歴・summaryを含む呼出しごとに届けることを確認します。</summary>
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

    /// <summary>data source置換で旧buildを取消し要求版とsource identityを失効させ、次の登録・構築が置換先だけを使うことを確認します。</summary>
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
            PlaylistLibraryResolveIndexSnapshot.Empty,
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

    /// <summary>同条件重複を統合し、後発要求で旧待機を完了、終了取消で現要求を完了し、worker停止まではidle待機を残す終端の分担を確認します。</summary>
    [TestMethod]
    public async Task DetailRequestCompletion_CombinesDuplicatesAndCompletesSupersededAndShutdownWaiters()
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

    /// <summary>source読込をgate後に失敗させ、途中の選択変更でも元例外を保持して要求とworkerを終端し、失敗したsourceを公開しないことを確認します。</summary>
    [TestMethod]
    public async Task RequestDetailRefresh_DataSourceFailureCompletesRequestAndStopsWorker()
    {
        var failureEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        var originalFailure = new InvalidOperationException("detail data source failed");
        dataSource.EnsureEntriesLoadedAction = () =>
        {
            failureEntered.TrySetResult(true);
            failureRelease.Task.GetAwaiter().GetResult();
            throw originalFailure;
        };
        var table = new BMSTable
        {
            entries = [new TestablePlaylistEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "failure")]
        };
        workspace.RequestDetailSelection(table);
        Task requestCompletion = workspace.RequestDetailRefreshTask(
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false,
            openReadiness: default);
        Task workerIdle = workspace.WaitForDetailBuildIdleAsync();

        Exception? primaryFailure = null;
        try
        {
            await Task.WhenAny(failureEntered.Task, requestCompletion);
            if (!failureEntered.Task.IsCompleted) { await requestCompletion; Assert.Fail("実detail producerに到達しませんでした。"); }
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
            workspace.RequestDetailSelection(new BMSTable());
            failureRelease.TrySetResult(true);
            try
            {
                InvalidOperationException actualFailure = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    async () => await requestCompletion);
                Assert.AreSame(originalFailure, actualFailure, "後発の選択変更で実failureを隠しません。");
                await workerIdle.ConfigureAwait(false);
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

    /// <summary>sourceを退役させた時、既存要求の待機を完了して退役後版の待機も残さず、待機先の紛失を防ぎます。</summary>
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

    /// <summary>要求後に索引版が進んでも実際の取得版で詳細を採用し、行・所持tokenを保ち、同じ入力の再要求では再構築しません。</summary>
    [TestMethod]
    public async Task RequestDetailRefresh_UsesAttachedResolveIndexAndPreservesOwnedToken()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ChartFile ownedChart = ChartTestValues.Empty() with
        {
            Path = @"C:\owned\chart.bms",
            Md5 = md5,
            RawTitle = "Owned chart",
            Token = new OwnedChartToken()
        };
        dataSource.CurrentOwnedCollectionVersion = 0;
        dataSource.ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromCharts([ownedChart])
            .WithMetadata(version: 1, buildElapsedMs: 0, invalidationVersion: 0, ownedCollectionVersion: 1);
        dataSource.ResolveIndexAction = () => dataSource.CurrentOwnedCollectionVersion = 1;

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
        Assert.AreSame(ownedChart.Token, row.Chart.Token);
        Assert.AreEqual(ownedChart.Path, row.Chart.Path);
        Assert.AreEqual(1L, workspace.DetailViewState.Source.LastBuiltLibraryIndexVersion);
        await workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
            MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected,
            useCoalescingWindow: false, openReadiness: default).ConfigureAwait(false);
        Assert.AreEqual(1, dataSource.ResolveIndexCallCount);
        Assert.AreSame(row, workspace.DetailViewState.Source.Rows[0]);
    }

    /// <summary>詳細構築中にカタログ版が進めば旧候補を採用せず、表示行・適用版を保ち、次の明示要求で新しい索引の実パスへ更新します。</summary>
    [TestMethod]
    public async Task RequestDetailRefresh_RejectsChangedCatalogAtTerminalAndAllowsNextExplicitRequest()
    {
        PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out FakePlaylistDetailDataSource dataSource);
        const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        ChartFile chart = ChartTestValues.Empty() with { Path = @"C:\owned\first.bms", Md5 = md5, Title = "first" };
        var entry = new TestablePlaylistEntry(md5, "entry");
        workspace.RequestDetailSelection(new BMSTable { entries = [entry] });
        dataSource.ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromCharts([chart])
            .WithMetadata(1, 0, 0, 1);

        async Task Refresh()
        {
            Task request = workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected,
                MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected,
                useCoalescingWindow: false, openReadiness: default);
            try { await request.ConfigureAwait(false); }
            finally { await Task.WhenAll(request, workspace.WaitForDetailBuildIdleAsync()).ConfigureAwait(false); }
        }

        await Refresh();
        IList firstRows = workspace.DetailViewState.View.Rows;
        Assert.AreEqual(@"C:\owned\first.bms", ((PlaylistDetailRow)firstRows[0]!).path);
        dataSource.ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromCharts([chart with { Path = @"C:\owned\second.bms" }])
            .WithMetadata(2, 0, 0, 2);
        // 実producerが索引を捕捉した後、エントリー読込み境界で次の確定版へ進める。
        dataSource.EnsureEntriesLoadedAction = () => dataSource.CurrentOwnedCollectionVersion = 3;
        await Refresh();
        Assert.AreSame(firstRows, workspace.DetailViewState.View.Rows);
        Assert.AreEqual(1L, workspace.DetailViewState.Source.LastBuiltLibraryIndexVersion);
        Assert.AreEqual(2, dataSource.ResolveIndexCallCount, "不採用を自動再試行で救済しません。");

        dataSource.EnsureEntriesLoadedAction = null;
        dataSource.ResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromCharts([chart with { Path = @"C:\owned\third.bms" }])
            .WithMetadata(3, 0, 0, 3);
        await Refresh();
        Assert.AreEqual(@"C:\owned\third.bms", ((PlaylistDetailRow)workspace.DetailViewState.View.Rows[0]!).path);
        Assert.AreEqual(3L, workspace.DetailViewState.Source.LastBuiltLibraryIndexVersion);
        Assert.AreSame(entry, workspace.DetailViewState.Source.Rows[0].Entry);
    }

    /// <summary>既存schedulerへ予約したprewarmを実行して接続sourceの索引を取得し、runtime cacheの準備状態と計測値をreadinessへ配送します。</summary>
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
            OwnedCollectionVersion = (int)dataSource.OwnedCollectionVersion,
            BuildElapsedMs = 12L
        };
        PlaylistLibraryIndexReadinessSnapshot readiness = workspace.CapturePlaylistLibraryIndexReadinessSnapshot();
        Assert.AreEqual("cached", readiness.State);
        Assert.AreEqual(12L, readiness.BuildElapsedMs);
    }

    /// <summary>二譜面の実削除後にworkspaceのprewarmを実行し、新しいresolve索引を全source再列挙なしで再利用することを確認します。</summary>
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
            ChartFile first = ChartTestValues.Empty() with
            {
                Path = firstPath,
                Md5 = new string('a', 32)
            };
            ChartFile second = ChartTestValues.Empty() with
            {
                Path = secondPath,
                Md5 = new string('b', 32)
            };
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new OwnedChartCollectionTestSupport.TestFileMutationService(),
                new FileDbReportRecordingDialogs())
            {
                BmsCharts = [first, second],
                BmsonCharts = []
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(first), typeof(LR2SongDB.song));
                db.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(second), typeof(LR2SongDB.song));
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
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((first))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [firstDirectory]);
            LibraryChartRemovalOutcome secondOutcome = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((second))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [secondDirectory]);
            Assert.IsFalse(firstOutcome.HasError);
            Assert.IsFalse(secondOutcome.HasError);

            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
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

    /// <summary>duplicate表示の優先期間に索引失効を保留し、優先期間終了後だけprewarmを開始して表示競合と更新漏れを防ぎます。</summary>
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

    /// <summary>P保有中の同期を予約せず未開始結果として返し、解放後の次明示要求と任意同期schedulerの拒否を区別して受付を解放します。</summary>
    [TestMethod]
    public async Task ExternalPlaylistSync_PlaylistBusySkipsWithoutSchedulerReservationAndFreshRequestSucceeds()
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
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
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

            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable held));
            try
            {
                await workspace.RunExternalPlaylistSyncAsync("test_rejection", true, false, 11L);
                Assert.AreEqual(0, schedulerCalls);
                Assert.AreEqual(0, queued.Count);
                Assert.AreEqual(1, completed.Count);
                Assert.IsTrue(completed[0].WasSkipped);
                Assert.IsFalse(completed[0].Succeeded);
                Assert.IsFalse(completed[0].PublishesReferenceReceipt);
            }
            finally { held.Dispose(); }
            Assert.AreEqual(1, completed.Count, "拒否要求を解放後に予約実行しません。");
            await workspace.RunExternalPlaylistSyncAsync("fresh_request", true, false, 12L);
            Assert.AreEqual(0, schedulerCalls);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual("fresh_request", queued[0].Reason);
            Assert.AreEqual(12L, queued[0].OperationToken);
            Assert.AreEqual(2, completed.Count);
            Assert.IsTrue(completed[1].Succeeded);
            Assert.IsFalse(completed[1].WasSkipped);
            // 初回起動の任意同期は外側scheduler登録を維持し、登録拒否も見送り結果を発行します。
            workspace.QueueExternalPlaylistSync("optional_initialize", false, false, 13L);
            Assert.AreEqual(1, schedulerCalls);
            Assert.AreEqual(1, queued.Count);
            Assert.AreEqual(3, completed.Count);
            Assert.IsTrue(completed[2].WasSkipped);
            Assert.IsFalse(completed[2].Succeeded);
            Assert.AreEqual(13L, completed[2].OperationToken);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>二つの実DBを持つstoreを置換し、旧storeの実同期receiptを無視して現storeの更新だけをworkspaceへ公開します。</summary>
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

            var firstPlaylist = new TestBmsPlaylist(firstSongDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            BMSTable firstTable = await firstPlaylist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(firstHeaderPath));
            firstTable.playlist_id = 8101;
            firstPlaylist.BMSTables = new ObservableCollection<BMSTable>([firstTable]);
            PersistPlaylistAggregate(firstSongDbPath, firstTable);

            var secondPlaylist = new TestBmsPlaylist(secondSongDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false });
            BMSTable secondTable = await secondPlaylist.ExternalSyncOwner.LoadExternalTableAsync(new Uri(secondHeaderPath));
            secondTable.playlist_id = 8102;
            secondPlaylist.BMSTables = new ObservableCollection<BMSTable>([secondTable]);
            PersistPlaylistAggregate(secondSongDbPath, secondTable);

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

            currentLibrary = new TestBmsLibrary(secondSongDbPath);
            workspace.RefreshPlaylistTreeTables(secondPlaylist, currentLibrary);

            PlaylistExternalSyncOwner.PlaylistReloadOperationResult oldResults =
                await firstPlaylist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [firstTable],
                    reason: "typed_old_store_receipt",
                    publishReferenceReceipts: true);

            Assert.IsTrue(oldResults.Single().Succeeded);
            Assert.AreEqual(0, sortInvalidationCount, "Receipts from the retired store must be ignored.");
            Assert.AreEqual("second", workspace.PlaylistTreeTables.Single().name);

            File.WriteAllText(secondScorePath, "[{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"second-updated\",\"level\":\"1\"}]", Encoding.UTF8);
            secondTable.header_sha256 = null;
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult currentResults =
                await secondPlaylist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    [secondTable],
                    reason: "typed_current_store_receipt",
                    publishReferenceReceipts: true);

            Assert.IsTrue(currentResults.Single().Succeeded);
            Assert.AreEqual(1, sortInvalidationCount, "The active store receipt must reach the workspace.");
            TestUiDispatcherHost.ProcessQueuedPresentation();
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

    /// <summary>
    /// 保証：公開済みのhydration receiptを使う反映中は旧Storeを保ち、反映終端後にStoreを置換する。
    /// 置換通知と必須hydrationの終端まで待ち、要求・完了versionの一致を保つ。
    /// </summary>
    [TestMethod]
    public async Task PlaylistTreeStoreReplacement_WaitsForCurrentHydrationApplyThroughTypedReceipt()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        List<Task> independentWork = [];
        Task? apply = null;
        Task? replacement = null;
        Task? hydration = null;
        TestBmsPlaylist? observedPlaylist = null;
        EventHandler<PlaylistHydrationVersionEventArgs>? publicationObserver = null;
        Exception? primaryFailure = null;
        var releaseApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacementNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModelRequestNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var firstPlaylist = new TestBmsPlaylist(firstSongDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
            {
                BMSTables = new ObservableCollection<BMSTable>([
                    new BMSTable { playlist_id = 8301, name = "first" }])
            };
            var secondPlaylist = new TestBmsPlaylist(secondSongDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
            {
                BMSTables = new ObservableCollection<BMSTable>([
                    new BMSTable { playlist_id = 8302, name = "second" }])
            };
            firstPlaylist.BMSTables[0].MarkEntriesNotLoaded();
            var applyEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replacementStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCaptured = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestPublicationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaylistHydrationCompletionReceipt? requestReceipt = null;
            ObservableCollection<BMSTable>? requestTables = null;
            long requestGeneration = 0L;
            int requestedVersion = 0;
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
                    // library参照の取得は経路到達だけを示すため、置換完了はreceipt失効後のcatalog通知で観測します。
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
                    // store置換とreceipt失効後のcatalog通知を保持し、この境界の観測前に置換Taskが終端しないようにします。
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
                requestCaptured.TrySetResult(true);
            };
            workspace.PlaylistPresentationRefreshRequested += (_, request) =>
            {
                if (request.Kind == PlaylistPresentationRefreshKind.Tree)
                {
                    workspace.ApplyPlaylistTreePresentationRefresh(request.Reason, deferred: false);
                }
            };
            workspace.RefreshPlaylistTreeTables(firstPlaylist);
            observedPlaylist = firstPlaylist;
            publicationObserver = (_, _) =>
            {
                // workspaceの後に同じモデル通知へ購読し、workspaceでの要求公開が戻った境界を観測する。
                requestPublicationObserved.TrySetResult(true);
                // 要求捕捉に届かなかった場合はモデルを進め、初期化Taskとの競争で失敗を判定する。
                if (!requestCaptured.Task.IsCompleted)
                {
                    return;
                }
                // 後続hydrationによるreceipt無効化を反映・置換の観測後まで保持する。外側のテストが解放する。
                releaseModelRequestNotification.Task.GetAwaiter().GetResult();
            };
            firstPlaylist.PlaylistEntriesHydrationRequested += publicationObserver;
            firstPlaylist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                independentWork.Add(Task.Run(work));
                return true;
            };
            hydration = Task.Run(() => firstPlaylist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false));
            Task requestPublication = Task.WhenAll(requestCaptured.Task, requestPublicationObserved.Task);
            // 公開前の初期化失敗も伝播させ、観測通知が来ないまま待機し続けない。
            if (await Task.WhenAny(requestPublication, hydration).ConfigureAwait(false) == hydration)
            {
                await hydration.ConfigureAwait(false);
                Assert.IsTrue(requestPublication.IsCompleted, "必須hydrationの終了前に要求公開を観測する。");
            }
            await requestPublication.ConfigureAwait(false);
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
            await TestUiDispatcherHost.AwaitNotificationAsync(applyEntered.Task, apply, "playlist.hydration-apply");

            currentLibrary = secondLibrary;
            Volatile.Write(ref replacementRouteExpected, 1);
            replacement = Task.Run(() => workspace.RefreshPlaylistTreeTables(secondPlaylist));
            await TestUiDispatcherHost.AwaitNotificationAsync(replacementCallStarted.Task, replacement, "playlist.store-replacement-start");
            Assert.IsFalse(replacementRouteEntered.Task.IsCompleted);
            Assert.AreSame(firstPlaylist.BMSTables, workspace.PlaylistTreeTables);

            releaseApply.TrySetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(replacementRouteEntered.Task, replacement, "playlist.store-replacement-notification");
            Assert.IsFalse(replacement.IsCompleted, "Store replacement must wait for the active terminal hydration apply.");
            Assert.AreSame(secondPlaylist.BMSTables, workspace.PlaylistTreeTables);
            releaseReplacementNotification.TrySetResult(true);
            releaseModelRequestNotification.TrySetResult(true);
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
            releaseModelRequestNotification.TrySetResult(true);
            Task[] pendingTasks = [
                .. independentWork,
                .. new[] { apply, replacement, hydration }
                    .Where(task => task != null)
                    .Cast<Task>()];
            try
            {
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
            }
            finally
            {
                // 通知元のTaskが合流してから購読を解除し、fixtureを破棄する。
                if (observedPlaylist != null && publicationObserver != null)
                {
                    observedPlaylist.PlaylistEntriesHydrationRequested -= publicationObserver;
                }
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }

    /// <summary>受理同期の開始通知中に再入要求を未開始で返し、実行進捗や自動再実行を増やさず、次の明示要求を正常終端します。</summary>
    [TestMethod]
    public async Task ExternalPlaylistSync_ConcurrentRequestIsSkippedWithoutReplayAndFreshRequestSucceeds()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceDetailRefreshTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string songDbPath = Path.Combine(directory, "song.db");
            using (var db = new LR2SongDBExtended(songDbPath)) { }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }) { BMSTables = [] };
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist);
            var completed = new List<PlaylistExternalSyncCompletionEventArgs>();
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            workspace.PlaylistExternalSyncCompleted += (_, fact) => completed.Add(fact);
            workspace.ProgressRequestFactory = (name, version) => new(7, 0, name, version);
            Task? rejected = null;
            bool acceptedDuringSync = false;
            bool rejectedCompletedSynchronously = false;
            workspace.RequestProgressReporter = (request, running) =>
            {
                execution.Add((request, running));
                if (running && request.Version == 1)
                {
                    acceptedDuringSync = playlist.TryEnterPlaylistMutation(out IDisposable unexpectedAdmission);
                    unexpectedAdmission?.Dispose();
                    rejected = workspace.RunExternalPlaylistSyncAsync("rejected", false, true, 22);
                    rejectedCompletedSynchronously = rejected.IsCompleted;
                }
            };
            await workspace.RunExternalPlaylistSyncAsync("first", false, true, 11);
            Assert.IsFalse(acceptedDuringSync);
            Assert.IsTrue(rejectedCompletedSynchronously, "未受理の要求は受付の解放を待ちません。");
            Assert.IsNotNull(rejected);
            await rejected;
            Assert.AreEqual(2, completed.Count);
            Assert.IsTrue(completed.Single(fact => fact.Reason == "rejected").WasSkipped);
            Assert.IsTrue(completed.Single(fact => fact.Reason == "first").Succeeded);
            Assert.AreEqual(2, execution.Count, "A rejected request has no execution or automatic replay.");
            await workspace.RunExternalPlaylistSyncAsync("fresh", false, true, 33);
            Assert.IsTrue(completed.Single(fact => fact.Reason == "fresh").Succeeded);
            Assert.AreEqual(4, execution.Count);
            Assert.IsTrue(playlist.TryEnterPlaylistMutation(out IDisposable next));
            next.Dispose();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>同じ予約に二つの参照反映要求を集約し、最新reason・token・版を開始と終端へ配送して一回の確定表示を公開します。</summary>
    [TestMethod]
    public void QueuePlaylistReferenceApply_CoalescesAndRunsLatestRequestSnapshot()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        List<Task> independentWork = [];
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
            {
                BMSTables = new ObservableCollection<BMSTable>()
            };
            var hydrationTable = new BMSTable { name = "Hydration" };
            hydrationTable.MarkEntriesNotLoaded();
            playlist.BMSTables.Add(hydrationTable);
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                independentWork.Add(Task.Run(work));
                return true;
            };
            playlist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false).GetAwaiter().GetResult();
            var library = new TestBmsLibrary(songDbPath);
            int schedulerCalls = 0;
            Func<Task>? scheduledWork = null;
            var queued = new List<PlaylistReferenceApplyQueuedEventArgs>();
            var completed = new List<PlaylistReferenceApplyCompletedEventArgs>();
            var presentation = new List<PlaylistReferenceApplyPresentationRequestedEventArgs>();
            var lifecycle = new List<string>();
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
                presentationRefreshDeferredProvider: _ => false);
            var progress = new List<(OperationProgressRequest Request, bool Running)>();
            workspace.PlaylistReferenceApplyWorkflow.ProgressRequestFactory = (name, version) => new(9, 0, name, version);
            workspace.PlaylistReferenceApplyWorkflow.RequestProgressReporter = (request, running) => progress.Add((request, running));
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
            Assert.AreEqual(new OperationProgressRequest(9, 2, "playlist_ref_apply", 2), queued[1].ProgressRequest);

            scheduledWork!().GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { (queued[1].ProgressRequest, true), (queued[1].ProgressRequest, false) }, progress.ToArray());

            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
            Assert.AreEqual(1, completed.Count);
            Assert.AreEqual(2, completed[0].Version);
            Assert.AreEqual("latest_request", completed[0].Reason);
            Assert.AreEqual(2L, completed[0].OperationToken);
            Assert.IsTrue(completed[0].Succeeded);
            Assert.AreEqual(1, presentation.Count);
            Assert.AreEqual(2, presentation[0].Version);
            Assert.AreEqual(2L, presentation[0].OperationToken);
            CollectionAssert.AreEqual(new[] { "presentation" }, lifecycle);
        }
        finally
        {
            Task.WhenAll(independentWork).GetAwaiter().GetResult();
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>先行反映の開始通知中に後続要求を受け付け、各cycleのreasonに対応するtoken・版を開始から終端まで取り違えないことを確認します。</summary>
    [TestMethod]
    public async Task ReferenceApply_AcceptanceAfterCaptureKeepsEachCycleOrigin()
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(PlaylistWorkspaceDetailRefreshTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string songDbPath = Path.Combine(directory, "song.db");
            using (var db = new LR2SongDBExtended(songDbPath)) { }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false }) { BMSTables = [] };
            var library = new TestBmsLibrary(songDbPath);
            Func<Task>? scheduled = null;
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library, referenceApplyScheduler: (_, work) => { scheduled = work; return true; });
            workspace.RefreshPlaylistTreeTables(playlist);
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            workspace.PlaylistReferenceApplyWorkflow.ProgressRequestFactory = (name, version) => new(6, 0, name, version);
            workspace.PlaylistReferenceApplyWorkflow.RequestProgressReporter = (request, running) =>
            {
                execution.Add((request, running));
                if (running && request.Version == 1) { workspace.PlaylistReferenceApplyWorkflow.Queue("second", 22); }
            };
            workspace.PlaylistReferenceApplyWorkflow.Queue("first", 11);
            Assert.IsNotNull(scheduled);
            await scheduled();
            CollectionAssert.AreEqual(new[]
            {
                (new OperationProgressRequest(6, 11, "playlist_ref_apply", 1), true),
                (new OperationProgressRequest(6, 11, "playlist_ref_apply", 1), false),
                (new OperationProgressRequest(6, 22, "playlist_ref_apply", 2), true),
                (new OperationProgressRequest(6, 22, "playlist_ref_apply", 2), false)
            }, execution.ToArray());
            Assert.IsTrue(workspace.PlaylistReferenceApplyWorkflow.IsIdle);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>実初期化のhydration receiptを受信して参照反映を実行し、初期化reasonと要求識別を持つ表示要求を一回届けます。</summary>
    [TestMethod]
    public async Task PlaylistTreeHydrationReceipt_RequestsPresentationRefreshAfterSnapshotApply()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        List<Task> independentWork = [];
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
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
            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                independentWork.Add(Task.Run(work));
                return true;
            };
            await playlist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false);
            Assert.IsNotNull(referenceApplyWork);
            await referenceApplyWork!();
            Assert.AreEqual(1, presentations.Count);
            Assert.AreEqual("Initialize", presentations[0].Reason);
            Assert.AreEqual(1, presentations[0].Version);
            Assert.AreEqual(0L, presentations[0].OperationToken);
        }
        finally
        {
            await Task.WhenAll(independentWork).ConfigureAwait(false);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>モデルreceipt公開後、workspace受信前に行の読込状態を失効させ、古いsnapshotから参照反映を予約しないことを確認します。</summary>
    [TestMethod]
    public async Task PlaylistTreeHydrationReceipt_DoesNotApplySnapshotInvalidatedBeforeWorkspaceConsumption()
    {
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistWorkspaceViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        List<Task> independentWork = [];
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
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
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

            playlist.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                independentWork.Add(Task.Run(work));
                return true;
            };
            await playlist.InitializeAsync(reloadExtPlaylist: false, exportBeatorajaBmt: false);
            Assert.AreEqual(
                0,
                referenceApplyScheduleCount,
                "An invalidated model receipt must not wake reference application with an old snapshot.");
        }
        finally
        {
            await Task.WhenAll(independentWork).ConfigureAwait(false);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>予約済み参照反映を終了時に破棄し、捕捉したreason・版・tokenの未開始結果を一回公開してidleへ戻します。</summary>
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
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(), () => new CustomFolderOutputSettingsSnapshot { OperationModeLR2DB = false })
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
