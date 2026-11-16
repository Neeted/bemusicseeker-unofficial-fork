using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoHydrationTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistLampSourceTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>ライブラリから集計入力を捕捉するハッシュ補完・履歴障害・取得元の一貫性・明示捕捉を検証する。</summary>
/// <remarks>専用SQLiteと実メタデータ取込みで情報源の接続を確認し、カテゴリ計算はPlaylistLampAggregationTestsへ分担する。</remarks>
[TestClass]
public sealed class BmsLibraryPlaylistLampDataSourceTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>通常JSONのMD5だけ/SHAだけの二所属を実保存・読戻しし、所持・path変化を往復しても保存所属キーと総数2を維持します。</summary>
    [TestMethod]
    public async Task CaptureAsync_PreservesSavedMembershipAcrossOwnershipChanges()
    {
        await WithTemporarySongDb(async (unusedDirectory, songDbPath) =>
        {
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            string md5 = new('a', 32), sha = new('b', 64);
            var table = new BMSTable("{\"name\":\"Membership\",\"symbol\":\"M\",\"data_url\":\"data.json\"}",
                _data_json: "[{\"md5\":\"" + md5 + "\",\"level\":\"1\"},{\"sha256\":\"" + sha + "\",\"level\":\"1\"}]")
            { playlist_id = 17 };
            string directory = Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException();
            string scorePath = Path.Combine(directory, "score.db");
            using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath))
            {
                db.CreateTable<LR2ScoreDB.player>(); db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.score { hash = md5, clear = ClearType.CLEAR, perfect = 80, totalnotes = 100 });
            }
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = true);
            var library = new TestBmsLibrary(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(directory)), scorePath,
                new TestFileMutationService(), new PlaylistSummaryAggregationTestDoubles.RecordingDialogService(), new TestUiScheduler(() => null), settings: settings)
            { BmsCharts = [] };
            library.InitializeScoresOnly(null);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = new ObservableCollection<BMSTable>([table]) };
            try
            {
                playlist.CommitBMSTableWithEntriesToDB(table);
                using (LR2SongDBExtended read = databaseScope.OpenSongReadOnly(songDbPath))
                { Assert.AreEqual(2, read.Table<LR2SongDBExtended.playlist_entry>().Count()); }
                var repository = new PlaylistPersistenceRepository(databaseScope, songDbPath);
                BMSTable reloaded = repository.LoadPlaylistHeaders().Single();
                reloaded.entries = [.. repository.LoadPersistedPlaylistEntries(reloaded.playlist_id, activeOnly: false)];
                playlist.BMSTables = new ObservableCollection<BMSTable>([reloaded]);
                Assert.AreNotSame(table, reloaded);
                var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
                string[]? identities = null;
                foreach (int step in Enumerable.Range(0, 4))
                {
                    library.BmsCharts = step is 1 or 2 ? [CreateLibraryFile("C:/charts/" + step + ".bms", md5, sha)] : [];
                    PlaylistLampAggregationRequest request = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("17"), CancellationToken.None);
                    Assert.AreEqual(2, request.Entries.Count);
                    string[] current = request.Entries.Select(entry => entry.IdentityKey).ToArray();
                    Assert.AreEqual(2, current.Distinct(StringComparer.Ordinal).Count());
                    if (identities != null) { CollectionAssert.AreEqual(identities, current); }
                    identities = current;
                    Assert.AreEqual(step is 1 or 2 ? 2 : 0, request.Entries.Count(entry => entry.IsOwned));
                    if (step == 1)
                    {
                        PlaylistLampAggregationResult result = new PlaylistLampAggregationService().Aggregate(request);
                        Assert.AreEqual(2, result.Statistics.TotalCount);
                        Assert.AreEqual(2, result.FolderRows.Single().Count);
                        using PlaylistWorkspaceViewModel workspace = SharedPlaylistWorkspaceFixture.CreateDetailWorkspace(out _,
                            playlistStoreProvider: () => playlist, playlistLibraryProvider: () => library);
                        workspace.SetDetailDataSource(new PlaylistDetailDataSource(library, playlist, new MainChartRowProjectionOwner()));
                        workspace.InitializePlaylistDetailFilter(ChartListFilterSnapshot.Default);
                        foreach (PlaylistLampViewerNavigationRequest navigation in new[]
                        {
                            PlaylistLampViewerNavigationRequest.ForFolder(PlaylistLampSegmentInvocationRequest.ForClear("17", request.FolderOrder.Single(), PlaylistLampClearCategory.NORMAL)),
                            PlaylistLampViewerNavigationRequest.ForOverall("17", PlaylistLampSegmentKind.Clear, PlaylistLampClearCategory.NORMAL, null)
                        })
                        {
                            Assert.IsTrue(workspace.TryRequestPlaylistLampNavigation(navigation));
                            await workspace.RequestDetailRefreshTask(MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlaylistFilterSelected,
                                MainViewUpdateMode.PlaylistFilterSelected, false, default);
                            await workspace.WaitForDetailBuildIdleAsync();
                            Assert.AreEqual(2, workspace.DetailViewState.View.Rows.Count);
                        }
                    }
                }
            }
            finally { playlist.RequestShutdown("lamp-membership"); library.RequestShutdown("lamp-membership"); }
        });
    }

    /// <summary>実フォルダ名[NO SONG]と未所持MD5エントリを捕捉し、実chart_infoの補完SHAを所持SHAとは区別して渡す。名前による所属欠落・補完漏れ・所持誤認を検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_keepsChartInfoShaSeparateAndUsesItForBeatorajaLookup()
    {

        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string md5 = new('a', 32);
            string chartInfoSha256 = new('c', 64);
            using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
            {
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoRow(chartInfoSha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }

            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new ChartInfoHydrationTestSupport.RecordingDialogService(), new TestUiScheduler(() => null))
            {
                BmsCharts = []
            };
            InvokeDeferredChartInfoHydration(library, "playlist_lamp_data_source_test", queueFullBackfillAfterHydration: false);
            await AwaitChartInfoHydrationAsync(library);

            BMSTableEntry entry = CreateEntry(md5, null);
            entry.folder = "[NO SONG]";
            var table = new BMSTable
            {
                playlist_id = 17,
                Folder_order = ["[NO SONG]"],
                entries = [entry]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);

            PlaylistLampAggregationRequest request = await source.CaptureAsync(
                PlaylistLampViewerQuery.Latest("17"),
                CancellationToken.None);
            PlaylistLampEntrySnapshot snapshot = request.Entries.Single();

            CollectionAssert.AreEqual(new[] { "[NO SONG]" }, request.FolderOrder.ToArray());
            Assert.AreEqual("[NO SONG]", snapshot.FolderName);
            Assert.IsFalse(snapshot.IsOwned);
            Assert.AreEqual(chartInfoSha256, snapshot.ChartInfoSha256);
            Assert.AreEqual(string.Empty, snapshot.ResolvedSha256);
            Assert.AreEqual(chartInfoSha256, snapshot.ScoreSha256);
        });
    }

    /// <summary>履歴源の生成が失敗しても所属入力を維持し、最新は未設定、過去日はスコア利用不可と診断を返す。ビューア全体の終端化や架空のスコアへの置換を検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_historicalSourceFactoryFailure_is_nonterminal_for_latest_and_historical_queries()
    {

        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new ChartInfoHydrationTestSupport.RecordingDialogService(), new TestUiScheduler(() => null))
            {
                BmsCharts = []
            };
            BMSTableEntry entry = CreateEntry(new string('e', 32), null);
            entry.folder = "folder";
            var table = new BMSTable
            {
                playlist_id = 19,
                Folder_order = ["folder"],
                entries = [entry]
            };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null))
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var source = new BmsLibraryPlaylistLampDataSource(
                playlist,
                library,
                _ => throw new InvalidOperationException("history source path unavailable"));

            PlaylistLampAggregationRequest latest = await source.CaptureAsync(
                PlaylistLampViewerQuery.Latest("19"),
                CancellationToken.None);
            Assert.AreEqual(PlaylistLampInputState.Loaded, latest.InputState);
            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Latest, latest.HistoricalStatus);
            Assert.IsFalse(latest.HistoricalDateRange.HasHistoricalDates);
            Assert.AreEqual(ActiveScoreSource.None, latest.ScoreSnapshot.Source);
            Assert.AreEqual(ScoreTableLoadStatus.NotConfigured, latest.ScoreSnapshot.LoadStatus);
            StringAssert.Contains(latest.HistoricalFailureMessage, "history source path unavailable");

            PlaylistLampAggregationRequest historical = await source.CaptureAsync(
                new PlaylistLampViewerQuery("19", DateTime.Today),
                CancellationToken.None);
            Assert.AreEqual(PlaylistLampInputState.Loaded, historical.InputState);
            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, historical.HistoricalStatus);
            Assert.AreEqual(ScoreTableLoadStatus.Failed, historical.ScoreSnapshot.LoadStatus);
            Assert.IsFalse(historical.ScoreSnapshot.IsScoreDataAvailable);
            Assert.IsFalse(historical.HistoricalDateRange.HasHistoricalDates);
            StringAssert.Contains(historical.HistoricalFailureMessage, "history source path unavailable");
        });
    }

    /// <summary>実DBの再読取り失敗後の明示捕捉は利用不可となり、次の成功と所属・所持変更を明示捕捉へ反映する。前の不変入力の破壊と外部DB変更の自動採用も検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_recapturesCurrentMembershipOwnershipAndApplicationScore()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string hash = new('f', 32);
            string sha = new('b', 64);
            string scorePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException(), "score.db");
            using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.score { hash = hash, clear = ClearType.CLEAR, perfect = 10, totalnotes = 100 });
            }
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService(databaseScope).InstallOrRepair(scorePath, isLr2LinkedProfile: true).Status);
            // 日付選択は取得済み履歴の範囲内でだけ可能です。外部更新より前の取得組へ既存の履歴を含めます。
            using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath)) { InsertLr2SourceHistory(db, hash, DateTime.Today.AddDays(-1), 10); }
            Settings sourceSettings = TestSettingsFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = true);
            var sourceOwner = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException()));
            var library = new TestBmsLibrary(databaseScope, songDbPath, sourceOwner, scorePath, new TestFileMutationService(), new PlaylistSummaryAggregationTestDoubles.RecordingDialogService(), new TestUiScheduler(() => null), settings: sourceSettings) { BmsCharts = [] };
            try
            {
                library.InitializeScoresOnly(null);
                BMSTableEntry entry = CreateEntry(hash, sha);
                entry.folder = "first";
                var table = new BMSTable { playlist_id = 21, Folder_order = ["first"], entries = [entry] };
                var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null)) { BMSTables = new ObservableCollection<BMSTable>([table]) };
                var source = new BmsLibraryPlaylistLampDataSource(playlist, library, PlaylistLampHistoricalScoreSourceContext.FromSnapshot);
                PlaylistLampAggregationRequest first = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
                Assert.AreEqual(ScoreTableLoadStatus.Loaded, first.ScoreSnapshot.LoadStatus);
                Assert.AreEqual(20, first.ScoreSnapshot.Resolve(first.Entries.Single()).ExScore);
                Assert.IsFalse(first.Entries.Single().IsOwned);
                using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath)) { db.Execute("UPDATE score SET perfect = 30, playcount = playcount + 1 WHERE hash = ?", hash); }
                // DB更新だけではアプリの保持scoreを変更せず、明示捕捉でもその保持値を使う。
                PlaylistLampAggregationRequest beforeReload = await source.CaptureAsync(new PlaylistLampViewerQuery("21", DateTime.Today), default);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, beforeReload.HistoricalStatus);
                Assert.AreEqual(20, beforeReload.ScoreSnapshot.Resolve(beforeReload.Entries.Single()).ExScore);
                using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath))
                {
                    db.Execute("ALTER TABLE score RENAME TO retained_score");
                    Assert.ThrowsExactly<SQLite.SQLiteException>(() => db.Table<BMSScore>().ToList());
                }
                library.InitializeScoresOnly(null);
                PlaylistLampAggregationRequest afterFailure = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
                Assert.AreEqual(ScoreTableLoadStatus.Failed, afterFailure.ScoreSnapshot.LoadStatus);
                Assert.IsFalse(afterFailure.ScoreSnapshot.IsScoreDataAvailable);
                using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scorePath)) { db.Execute("ALTER TABLE retained_score RENAME TO score"); }
                library.InitializeScoresOnly(null);
                library.BmsCharts = [CreateLibraryFile("C:/charts/source.bms", hash, sha)];
                BMSTableEntry changed = CreateEntry(hash, sha);
                changed.folder = "second";
                table.Folder_order = ["second"];
                table.entries = [changed];
                PlaylistLampAggregationRequest next = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
                Assert.AreEqual("second", next.Entries.Single().FolderName);
                Assert.IsTrue(next.Entries.Single().IsOwned);
                Assert.AreEqual(60, next.ScoreSnapshot.Resolve(next.Entries.Single()).ExScore);
                Assert.AreEqual("first", first.Entries.Single().FolderName);
                Assert.IsFalse(first.Entries.Single().IsOwned);
                Assert.AreEqual(20, first.ScoreSnapshot.Resolve(first.Entries.Single()).ExScore);
            }
            finally { library.RequestShutdown("playlist-score-fixture-cleanup"); }
        });
    }

    /// <summary>未準備表だけ既存準備Taskを待ち、完了後に同IDの置換表を捕捉する。別の準備済み表まで待たせる誤りとheaderを空成功にする誤りを検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_waitsForRequiredEntriesAndResolvesReplacementWithoutBlockingReadyTable()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null)) { BmsCharts = [] };
            var header = new BMSTable { playlist_id = 22 };
            header.MarkEntriesNotLoaded();
            var ready = new BMSTable { playlist_id = 23, entries = [] };
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null)) { BMSTables = new ObservableCollection<BMSTable>([header, ready]) };
            playlist.StartupReadiness.BeginPlaylistInitialization();
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            Task<PlaylistLampAggregationRequest> waiting = source.CaptureAsync(PlaylistLampViewerQuery.Latest("22"), default).AsTask();
            try
            {
                PlaylistLampAggregationRequest available = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("23"), default);
                Assert.AreEqual(PlaylistLampInputState.Loaded, available.InputState);
                Assert.IsFalse(waiting.IsCompleted);
                BMSTableEntry entry = CreateEntry(new string('a', 32), null);
                entry.folder = "hydrated";
                playlist.BMSTables = new ObservableCollection<BMSTable>([new BMSTable { playlist_id = 22, entries = [entry] }, ready]);
                playlist.StartupReadiness.MarkRequiredPlaylistReady();
                PlaylistLampAggregationRequest captured = await waiting;
                Assert.AreEqual(PlaylistLampInputState.Loaded, captured.InputState);
                Assert.AreEqual("hydrated", captured.Entries.Single().FolderName);
            }
            finally
            {
                playlist.StartupReadiness.MarkRequiredPlaylistReady();
                await waiting;
            }
        });
    }

    /// <summary>捕捉取消は共有準備Taskを停止せず、その待機だけを終える。準備故障はsessionでFailedへ確定してLoadingに留めない。</summary>
    [TestMethod]
    public async Task CaptureAsync_cancellationDoesNotOwnReadinessAndReadinessFailureBecomesFailed()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null)) { BmsCharts = [] };
            var table = new BMSTable { playlist_id = 24 };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath), uiScheduler: new TestUiScheduler(() => null)) { BMSTables = new ObservableCollection<BMSTable>([table]) };
            playlist.StartupReadiness.BeginPlaylistInitialization();
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            using var cancel = new CancellationTokenSource();
            Task<PlaylistLampAggregationRequest> capture = source.CaptureAsync(PlaylistLampViewerQuery.Latest("24"), cancel.Token).AsTask();
            try
            {
                cancel.Cancel();
                try { await capture; Assert.Fail("取消された捕捉が正常終了した。"); }
                catch (OperationCanceledException) { }
                Assert.IsFalse(playlist.StartupReadiness.RequiredPlaylistReadiness.IsCompleted);
                await using var session = new PlaylistLampViewerSession("24", source);
                Task start = session.StartAsync();
                playlist.StartupReadiness.FailRequiredPlaylistReadiness(new InvalidOperationException("required entries failed"));
                await start;
                Assert.AreEqual(PlaylistLampViewerState.Failed, session.Current.State);
                Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);
            }
            finally { playlist.StartupReadiness.MarkRequiredPlaylistReady(); }
        });
    }

}
