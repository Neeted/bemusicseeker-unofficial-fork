using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryOwnedHashTests
{
    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_RejectsCanceledBuildBeforeReadingStorage()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() =>
                library.GetOwnedChartHashIndexSnapshot(cancellation.Token));
        });
    }

    [TestMethod]
    public void GetOwnedChartHashIndexSnapshot_IncludesBmsonHashes()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            SetLibraryFilesWithoutNotification(library,
            [
                CreateLibraryFile(@"C:\Songs\bms.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            ]);
            SetLibraryBmsonSongsWithoutNotification(library,
            [
                ChartTestValues.Empty(ChartFileKind.Bmson) with {
                    Path = @"C:\Songs\bmson\chart.bmson",
                    Folder = @"C:\Songs\bmson",
                    Md5 = "cccccccccccccccccccccccccccccccc",
                    Sha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
                }
            ]);

            OwnedChartHashIndexVersionedSnapshot snapshot = library.GetOwnedChartHashIndexSnapshot();

            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            CollectionAssert.Contains(new List<string>(snapshot.Md5Hashes), "cccccccccccccccccccccccccccccccc");
            CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            CollectionAssert.Contains(new List<string>(snapshot.Sha256Hashes), "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        });
    }


}
