using System;
using System.Collections.Generic;
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

/// <summary>
/// PLV-2026-08-28-D2 data-source snapshot and dependency invalidation contracts.
/// </summary>
[TestClass]
public sealed class BmsLibraryPlaylistLampDataSourceTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task CaptureAsync_keepsChartInfoShaSeparateAndUsesItForBeatorajaLookup()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string md5 = new('a', 32);
            string chartInfoSha256 = new('c', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(
                    CreateChartInfoRow(chartInfoSha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                    typeof(LR2SongDBExtended.chart_info));
            }

            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService())
            {
                BMSFiles = []
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
            using var source = new BmsLibraryPlaylistLampDataSource(playlist, library);

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

    [TestMethod]
    [DoNotParallelize]
    public async Task CaptureAsync_historicalSourceFactoryFailure_is_nonterminal_for_latest_and_historical_queries()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService(),
                new ChartInfoMetadataTestSupport.RecordingDialogService())
            {
                BMSFiles = []
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
            using var source = new BmsLibraryPlaylistLampDataSource(
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

    [TestMethod]
    [DoNotParallelize]
    public async Task Source_raisesChangedWhenChartInfoIndexVersionChanges()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string md5 = new('b', 32);
            string chartInfoSha256 = new('d', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(
                    CreateChartInfoRow(chartInfoSha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                    typeof(LR2SongDBExtended.chart_info));
            }

            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService())
            {
                BMSFiles = []
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
            using var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
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
            PlaylistLampViewerSourceChangedEventArgs notification = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(notification.Sequence > 0);
            Assert.IsTrue(library.ChartInfoIndexVersion > 0);
        });
    }
}
