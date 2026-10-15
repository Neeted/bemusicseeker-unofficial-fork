using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>ライブラリから集計入力を捕捉するハッシュ補完・履歴障害・明示捕捉を検証する。</summary>
/// <remarks>専用SQLiteと実メタデータ取込みで情報源の接続を確認し、カテゴリ計算はPlaylistLampAggregationTestsへ分担する。</remarks>
[TestClass]
public sealed class BmsLibraryPlaylistLampDataSourceTests
{
    /// <summary>未所持MD5エントリを実chart_infoで補完し、所持SHAとは区別したスコア検索SHAを渡す。補完漏れや所持誤認を検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_keepsChartInfoShaSeparateAndUsesItForBeatorajaLookup()
    {

        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string md5 = new('a', 32);
            string chartInfoSha256 = new('c', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoRow(chartInfoSha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }

            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService())
            {
                BmsCharts = []
            };
            InvokeDeferredChartInfoHydration(library, "playlist_lamp_data_source_test", queueFullBackfillAfterHydration: false);
            await AwaitChartInfoHydrationAsync(library);

            BMSTableEntry entry = CreateEntry(md5, null);
            entry.folder = "folder";
            var table = new BMSTable
            {
                playlist_id = 17,
                Folder_order = ["folder"],
                entries = [entry]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);

            PlaylistLampAggregationRequest request = await source.CaptureAsync(
                PlaylistLampViewerQuery.Latest("17"),
                CancellationToken.None);
            PlaylistLampEntrySnapshot snapshot = request.Entries.Single();

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
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService(),
                new ChartInfoMetadataTestSupport.RecordingDialogService())
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
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var source = new BmsLibraryPlaylistLampDataSource(
                playlist,
                library,
                () => throw new InvalidOperationException("history source path unavailable"));

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

    /// <summary>実スコアDBの再読取りと所属・所持変更を次の明示捕捉へ反映し、前の不変入力を保つ。外部DB変更だけを保持scoreへ自動採用する誤りも検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_recapturesCurrentMembershipOwnershipAndApplicationScore()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string hash = new('f', 32);
            string sha = new('b', 64);
            string scorePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException(), "score.db");
            using (var db = new LR2ScoreDBExtended(scorePath))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.score { hash = hash, clear = ClearType.CLEAR, perfect = 10, totalnotes = 100 });
            }
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService().InstallOrRepair(scorePath, isLr2LinkedProfile: true).Status);
            var library = new TestBmsLibrary(songDbPath, null, scorePath, new TestFileMutationService()) { BmsCharts = [] };
            library.InitializeScoresOnly(null);
            BMSTableEntry entry = CreateEntry(hash, sha);
            entry.folder = "first";
            var table = new BMSTable { playlist_id = 21, Folder_order = ["first"], entries = [entry] };
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([table]) };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library, () => PlaylistLampHistoricalScoreSourceContext.Lr2(scorePath));
            PlaylistLampAggregationRequest first = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
            Assert.AreEqual(ScoreTableLoadStatus.Loaded, first.ScoreSnapshot.LoadStatus);
            Assert.AreEqual(20, first.ScoreSnapshot.Resolve(first.Entries.Single()).ExScore);
            Assert.IsFalse(first.Entries.Single().IsOwned);
            using (var db = new LR2ScoreDBExtended(scorePath)) { db.Execute("UPDATE score SET perfect = 30, playcount = playcount + 1 WHERE hash = ?", hash); }
            // DB更新だけではアプリの保持scoreを変更せず、明示捕捉でもその保持値を使う。
            PlaylistLampAggregationRequest beforeReload = await source.CaptureAsync(new PlaylistLampViewerQuery("21", DateTime.Today), default);
            Assert.AreEqual(20, beforeReload.ScoreSnapshot.Resolve(beforeReload.Entries.Single()).ExScore);
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
        });
    }

    /// <summary>未準備表だけ既存準備Taskを待ち、完了後に同IDの置換表を捕捉する。別の準備済み表まで待たせる誤りとheaderを空成功にする誤りを検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_waitsForRequiredEntriesAndResolvesReplacementWithoutBlockingReadyTable()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService()) { BmsCharts = [] };
            var header = new BMSTable { playlist_id = 22 };
            header.MarkEntriesNotLoaded();
            var ready = new BMSTable { playlist_id = 23, entries = [] };
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([header, ready]) };
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
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService()) { BmsCharts = [] };
            var table = new BMSTable { playlist_id = 24 };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([table]) };
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
