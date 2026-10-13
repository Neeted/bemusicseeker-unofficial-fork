using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using BeMusicSeeker.Models.Utils;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>ライブラリから集計入力を捕捉するハッシュ補完・履歴障害・更新通知を検証する。</summary>
/// <remarks>専用SQLiteと実メタデータ取込みで情報源の接続を確認し、カテゴリ計算はPlaylistLampAggregationTestsへ分担する。</remarks>
[TestClass]
public sealed class BmsLibraryPlaylistLampDataSourceTests
{
    /// <summary>未所持MD5エントリを実chart_infoで補完し、所持SHAとは区別したスコア検索SHAと取込み世代を渡す。補完漏れや所持誤認を検出する。</summary>
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
            await using var source = new BmsLibraryPlaylistLampDataSource(playlist, library);

            PlaylistLampAggregationRequest request = await source.CaptureAsync(
                PlaylistLampViewerQuery.Latest("17"),
                CancellationToken.None);
            PlaylistLampEntrySnapshot snapshot = request.Entries.Single();

            Assert.IsFalse(snapshot.IsOwned);
            Assert.AreEqual(chartInfoSha256, snapshot.ChartInfoSha256);
            Assert.AreEqual(string.Empty, snapshot.ResolvedSha256);
            Assert.AreEqual(chartInfoSha256, snapshot.ScoreSha256);
            Assert.AreEqual(library.ChartInfoIndexVersion, request.DependencyStamp.ChartInfoIndexVersion);
            Assert.IsTrue(request.DependencyStamp.ChartInfoIndexVersion > 0);
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
            await using var source = new BmsLibraryPlaylistLampDataSource(
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

    /// <summary>購読後の実chart_info取込みを待ち、全プレイリスト向けの世代付き変更通知を受ける。補完情報が更新されても再集計へ通知しない接続漏れを検出する。</summary>
    [TestMethod]
    public async Task Source_raisesChangedWhenChartInfoIndexVersionChanges()
    {

        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string md5 = new('b', 32);
            string chartInfoSha256 = new('d', 64);
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
            BMSTableEntry entry = CreateEntry(md5, null);
            entry.folder = "folder";
            var table = new BMSTable
            {
                playlist_id = 18,
                Folder_order = ["folder"],
                entries = [entry]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            await using var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            var changed = new TaskCompletionSource<PlaylistLampViewerSourceChangedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            source.Changed += (_, args) =>
            {
                if (string.IsNullOrEmpty(args.PlaylistId))
                {
                    changed.TrySetResult(args);
                }
            };

            InvokeDeferredChartInfoHydration(library, "playlist_lamp_dependency_test", queueFullBackfillAfterHydration: false);
            await AwaitChartInfoHydrationAsync(library);
            PlaylistLampViewerSourceChangedEventArgs notification = await changed.Task;

            Assert.IsTrue(notification.Sequence > 0);
            Assert.IsTrue(library.ChartInfoIndexVersion > 0);
        });
    }
    /// <summary>
    /// 実playlist writer中のcollection変更を発火元へ返した後、queued readerが実ロック待ちへ到達し、
    /// source停止がそのreaderの取得・解放・worker終端を待ちます。活動数だけで終了する誤りを検出します。
    /// </summary>
    [TestMethod]
    public async Task Source_disposeAsyncDrainsQueuedPlaylistReaderBehindActualWriter()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService()) { BmsCharts = [] };
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>() };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            // 受付済みworkerが実readerへ到達したことだけを観測します。フィールド配置は保証対象にしません。
            ReaderWriterLockSlimWrapper playlistLock = typeof(BMSPlaylist).GetField("rwlockBMSTables", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(playlist)
                as ReaderWriterLockSlimWrapper ?? throw new AssertFailedException("実playlist lockを観測できない。");
            using var writerRelease = new ManualResetEventSlim();
            var publicationReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publication = Task.Run(() =>
            {
                playlist.AcquireWriterLockBMSTables();
                try
                {
                    playlist.BMSTables.Add(new BMSTable { playlist_id = 20 });
                    publicationReturned.TrySetResult();
                    writerRelease.Wait();
                }
                finally { playlist.FreeWriterLockBMSTables(); }
            });
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(publicationReturned.Task, publication, "source writer publication returned");
                Assert.IsTrue(SpinWait.SpinUntil(() => playlistLock.WaitingReadCount > 0, TimeSpan.FromSeconds(5)),
                    "queued workerが実playlist readerへ到達しなかった。");
                Task drain = source.DisposeAsync().AsTask();
                Assert.IsFalse(drain.IsCompleted);
                writerRelease.Set();
                await Task.WhenAll(publication, drain);
                await source.DisposeAsync();
                Assert.AreEqual(0, playlistLock.WaitingReadCount);
            }
            finally
            {
                writerRelease.Set();
                await publication;
                await source.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// 実score writerを保持してtable変更を発火し、Task.Run前のscore identity readerを観測します。
    /// 停止はqueued workerだけでなく同期発火元のmodel読取りと実終端も待ちます。
    /// </summary>
    [TestMethod]
    public async Task Source_disposeAsyncDrainsPreQueueScoreReaderBehindActualWriter()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService()) { BmsCharts = [] };
            var table = new BMSTable { playlist_id = 21 };
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([table]) };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            // score writerを実取得し、キュー前readerのWaitingReadCountだけを局所反射で観測します。
            ReaderWriterLockSlimWrapper scoreLock = typeof(BMSLibrary).GetField("rwlockBMSScores", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(library)
                as ReaderWriterLockSlimWrapper ?? throw new AssertFailedException("実score lockを観測できない。");
            using var writerRelease = new ManualResetEventSlim();
            var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = Task.Run(() =>
            {
                using (scoreLock.GetWriterGuard())
                {
                    writerEntered.TrySetResult();
                    writerRelease.Wait();
                }
            });
            Task? publication = null;
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(writerEntered.Task, writer, "source score writer acquired");
                publication = Task.Run(() => table.Folder_order = ["changed"]);
                Assert.IsTrue(SpinWait.SpinUntil(() => scoreLock.WaitingReadCount > 0, TimeSpan.FromSeconds(5)),
                    "同期発火元がキュー前score readerへ到達しなかった。");
                Task drain = source.DisposeAsync().AsTask();
                Assert.IsFalse(drain.IsCompleted);
                Assert.IsFalse(publication.IsCompleted);
                writerRelease.Set();
                await Task.WhenAll(writer, publication, drain);
                await source.DisposeAsync();
                Assert.AreEqual(0, scoreLock.WaitingReadCount);
            }
            finally
            {
                writerRelease.Set();
                await writer;
                if (publication != null) { await publication; }
                await source.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// 既取得Changed delegateの先頭subscriberからsession/source停止へ再入しても詰まらず、
    /// 残るsubscriberを含む通知実終端まで待ちます。任意通知throwを後続解放故障へ混ぜません。
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)] // 後続subscriberの任意通知故障もsource回収の成功を変えません。
    public async Task Source_disposeAsyncDrainsRetainedChangedDelegateAndRejectsSessionRefresh(bool subscriberThrows)
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService()) { BmsCharts = [] };
            var table = new BMSTable { playlist_id = 22 };
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([table]) };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            using var subscriberRelease = new ManualResetEventSlim();
            var subscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tailInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaylistLampViewerSession? session = null;
            Task? callbackDrain = null;
            source.Changed += (_, _) =>
            {
                session?.Dispose();
                callbackDrain = source.DisposeAsync().AsTask();
                subscriberEntered.TrySetResult();
                subscriberRelease.Wait();
            };
            session = new PlaylistLampViewerSession("22", source);
            source.Changed += (_, _) =>
            {
                tailInvoked.TrySetResult();
                if (subscriberThrows) { throw new InvalidOperationException("optional retained source notification"); }
            };
            Task? publication = null;
            try
            {
                await session.StartAsync();
                PlaylistLampAggregationResult before = session.Current;
                publication = Task.Run(() => table.Folder_order = ["changed"]);
                await subscriberEntered.Task;
                Assert.IsNotNull(callbackDrain);
                Assert.IsFalse(callbackDrain.IsCompleted);
                Task drain = source.DisposeAsync().AsTask();
                Assert.IsFalse(drain.IsCompleted);
                subscriberRelease.Set();
                await Task.WhenAll(publication, tailInvoked.Task, callbackDrain, drain, session.DisposeAsync().AsTask());
                await source.DisposeAsync();
                Assert.AreSame(before, session.Current, "既取得session handlerは停止後のrefreshを受理しません。");
            }
            finally
            {
                subscriberRelease.Set();
                if (publication != null) { await publication; }
                await session.DisposeAsync();
                await source.DisposeAsync();
            }
        });
    }

}
