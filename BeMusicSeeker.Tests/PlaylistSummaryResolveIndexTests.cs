using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaylistSummaryResolveIndexTests
{
    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_CachesAndRebuildsAfterOwnershipChange()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };

            PlaylistLibraryResolveIndexSnapshot first = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool firstCacheHit, out int firstStaleRetries);
            PlaylistLibraryResolveIndexSnapshot second = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool secondCacheHit, out int secondStaleRetries);
            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState cachedState = library.GetPlaylistLibraryResolveIndexRuntimeState();

            Assert.IsFalse(firstCacheHit);
            Assert.AreEqual(0, firstStaleRetries);
            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(0, secondStaleRetries);
            Assert.AreEqual(first.Version, second.Version);
            Assert.IsTrue(cachedState.IsCached);
            Assert.AreEqual(first.Version, cachedState.SnapshotVersion);
            Assert.IsTrue(first.ChartsByMd5.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

            library.BMSFiles =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState invalidatedState = library.GetPlaylistLibraryResolveIndexRuntimeState();
            PlaylistLibraryResolveIndexSnapshot third = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool thirdCacheHit, out int thirdStaleRetries);

            Assert.IsFalse(invalidatedState.IsCached);
            Assert.IsFalse(thirdCacheHit);
            Assert.AreEqual(0, thirdStaleRetries);
            Assert.IsTrue(third.Version > second.Version);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.OwnedChartCollectionVersion, third.OwnedCollectionVersion);
            Assert.IsTrue(third.BmsRowsVersion > second.BmsRowsVersion);
            Assert.IsFalse(third.ChartsByMd5.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.IsTrue(third.ChartsByMd5.ContainsKey("cccccccccccccccccccccccccccccccc"));
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_ReturnsCachedDuringDigestMutationWindowAndRebuildsAfter()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool initialCacheHit, out int _);
            Assert.IsFalse(initialCacheHit);

            using (BeginOwnedDigestMutationWindow(library))
            {
                BMSLibrary.PlaylistLibraryResolveIndexRuntimeState transientState = library.GetPlaylistLibraryResolveIndexRuntimeState();
                PlaylistLibraryResolveIndexSnapshot transientSnapshot = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool transientCacheHit, out int transientStaleRetries);

                Assert.IsTrue(transientState.IsCached);
                Assert.AreEqual(initial.Version, transientState.SnapshotVersion);
                Assert.IsTrue(transientCacheHit);
                Assert.AreEqual(0, transientStaleRetries);
                Assert.AreEqual(initial.Version, transientSnapshot.Version);
            }

            PlaylistLibraryResolveIndexSnapshot rebuilt = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool rebuiltCacheHit, out int _);
            PlaylistLibraryResolveIndexSnapshot cached = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool cachedCacheHit, out int _);

            Assert.IsFalse(rebuiltCacheHit);
            Assert.IsTrue(rebuilt.Version > initial.Version);
            Assert.IsTrue(cachedCacheHit);
            Assert.AreEqual(rebuilt.Version, cached.Version);
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexRuntimeState_ReportsCachedRefDuringDigestMutationWindow()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles =
                [
                    CreateLibraryFile(@"C:\Songs\chart.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };
            PlaylistLibraryResolveIndexSnapshot snapshot = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool cacheHit, out int _);
            Assert.IsFalse(cacheHit);

            using (BeginOwnedDigestMutationWindow(library))
            {
                BMSLibrary.PlaylistLibraryResolveIndexRuntimeState state = library.GetPlaylistLibraryResolveIndexRuntimeState();

                Assert.IsTrue(state.IsCached);
                Assert.AreEqual(snapshot.Version, state.SnapshotVersion);
            }
        });
    }


}
