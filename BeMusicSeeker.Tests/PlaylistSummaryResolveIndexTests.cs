using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            Assert.IsNotNull(first.ResolveChartForPlaylistHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null));

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
            Assert.IsNull(third.ResolveChartForPlaylistHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null));
            Assert.IsNotNull(third.ResolveChartForPlaylistHash("cccccccccccccccccccccccccccccccc", null));
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_AppliesCandidateDeltaAndKeepsPriorSnapshots()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string sharedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string sharedSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            BMSFile firstFile = CreateLibraryFile(@"C:\Songs\A.bms", sharedMd5, sharedSha256);
            BMSFile middleFile = CreateLibraryFile(@"C:\Songs\M.bms", sharedMd5, sharedSha256);
            BMSFile lastFile = CreateLibraryFile(@"C:\Songs\Z.bms", sharedMd5, sharedSha256);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [firstFile, middleFile, lastFile]
            };

            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int initialStaleRetries);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(0, initialStaleRetries);
            Assert.AreEqual(@"C:\Songs\A.bms", initial.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { @"C:\Songs\A.bms", @"C:\Songs\M.bms", @"C:\Songs\Z.bms" },
                initial.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            var removeFirst = new LibraryMutationDelta();
            removeFirst.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(firstFile));
            InvokeApplyLibraryMutationDelta(library, removeFirst);
            PlaylistLibraryResolveIndexSnapshot afterFirstRemoval = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterFirstCacheHit,
                out int afterFirstStaleRetries);
            Assert.IsTrue(afterFirstCacheHit);
            Assert.AreEqual(0, afterFirstStaleRetries);
            Assert.AreEqual(@"C:\Songs\M.bms", afterFirstRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { @"C:\Songs\M.bms", @"C:\Songs\Z.bms" },
                afterFirstRemoval.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            var removeMiddle = new LibraryMutationDelta();
            removeMiddle.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(middleFile));
            InvokeApplyLibraryMutationDelta(library, removeMiddle);
            PlaylistLibraryResolveIndexSnapshot afterSecondRemoval = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterSecondCacheHit,
                out int afterSecondStaleRetries);
            Assert.IsTrue(afterSecondCacheHit);
            Assert.AreEqual(0, afterSecondStaleRetries);
            Assert.AreEqual(@"C:\Songs\Z.bms", afterSecondRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { @"C:\Songs\Z.bms" },
                afterSecondRemoval.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            var removeLast = new LibraryMutationDelta();
            removeLast.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(lastFile));
            InvokeApplyLibraryMutationDelta(library, removeLast);
            PlaylistLibraryResolveIndexSnapshot empty = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool emptyCacheHit,
                out int emptyStaleRetries);
            Assert.IsTrue(emptyCacheHit);
            Assert.AreEqual(0, emptyStaleRetries);
            Assert.IsNull(empty.ResolveChartForPlaylistHash(sharedMd5, null));
            Assert.AreEqual(@"C:\Songs\A.bms", initial.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            Assert.AreEqual(@"C:\Songs\M.bms", afterFirstRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            Assert.AreEqual(@"C:\Songs\Z.bms", afterSecondRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_ReplacesExactCandidateAndRetainsOldSnapshot()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string chartPath = @"C:\Songs\replace.bms";
            const string oldMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string oldSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const string newMd5 = "cccccccccccccccccccccccccccccccc";
            const string newSha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            BMSFile oldFile = CreateLibraryFile(chartPath, oldMd5, oldSha256);
            BMSFile replacementFile = CreateLibraryFile(chartPath, newMd5, newSha256);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [oldFile]
            };

            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int _);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(chartPath, initial.ResolveChartForPlaylistHash(oldMd5, null).Path);

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([replacementFile], []));
            PlaylistLibraryResolveIndexSnapshot updated = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedCacheHit,
                out int updatedStaleRetries);

            Assert.IsTrue(updatedCacheHit);
            Assert.AreEqual(0, updatedStaleRetries);
            Assert.IsNull(updated.ResolveChartForPlaylistHash(oldMd5, null));
            Assert.AreEqual(chartPath, updated.ResolveChartForPlaylistHash(newMd5, null).Path);
            Assert.IsNull(updated.ResolveChartForPlaylistHash(oldMd5, newSha256));
            Assert.AreEqual(chartPath, updated.ResolveChartForPlaylistHash(null, newSha256).Path);
            Assert.IsNotNull(initial.ResolveChartForPlaylistHash(oldMd5, null));
            Assert.IsNull(initial.ResolveChartForPlaylistHash(newMd5, null));
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_MovesCandidateAndKeepsCanonicalCandidateOrder()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string sharedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string sharedSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Move");
            Directory.CreateDirectory(chartDirectory);
            string firstPath = Path.Combine(chartDirectory, "A.bms");
            string oldMovedPath = Path.Combine(chartDirectory, "Z.bms");
            string movedPath = Path.Combine(chartDirectory, "M.bms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(oldMovedPath, "#PLAYER 1");
            File.WriteAllText(movedPath, "#PLAYER 1");
            BMSFile firstFile = CreateLibraryFile(firstPath, sharedMd5, sharedSha256);
            BMSFile movedFile = CreateLibraryFile(oldMovedPath, sharedMd5, sharedSha256);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = [firstFile, movedFile]
            };

            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int _);
            Assert.IsFalse(initialCacheHit);
            CollectionAssert.AreEqual(
                new[] { firstFile.path, movedFile.path },
                initial.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(movedFile),
                OldPath = movedFile.path,
                NewPath = movedPath
            });
            InvokeApplyLibraryMutationDelta(library, delta);

            PlaylistLibraryResolveIndexSnapshot updated = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedCacheHit,
                out int updatedStaleRetries);
            Assert.IsTrue(updatedCacheHit);
            Assert.AreEqual(0, updatedStaleRetries);
            Assert.IsFalse(updated.ContainsCandidate(LibraryChartKind.Bms, oldMovedPath));
            Assert.IsTrue(updated.ContainsCandidate(LibraryChartKind.Bms, movedPath));
            Assert.AreEqual(firstFile.path, updated.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { firstFile.path, movedPath },
                updated.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());
            Assert.IsTrue(initial.ContainsCandidate(LibraryChartKind.Bms, oldMovedPath));
            Assert.AreEqual(oldMovedPath, initial.GetMd5Candidates(sharedMd5)[1].Path);
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_HandlesInitialBmsonNormalizationOnce()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "BmsonBoundary");
            Directory.CreateDirectory(chartDirectory);
            string bmsonPath = Path.Combine(chartDirectory, "existing.bmson");
            string firstBmsPath = Path.Combine(chartDirectory, "first.bms");
            string secondBmsPath = Path.Combine(chartDirectory, "second.bms");
            File.WriteAllText(bmsonPath, "{}");
            File.WriteAllText(firstBmsPath, "#PLAYER 1");
            File.WriteAllText(secondBmsPath, "#PLAYER 1");
            const string bmsonMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string bmsonSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                md5 = bmsonMd5,
                sha256 = bmsonSha256
            };
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService());
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);

            List<string> resolveWork = [];
            library.PlaylistLibraryResolveIndexStoreWorkObserver = resolveWork.Add;
            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int _);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(bmsonPath, initial.ResolveChartForPlaylistHash(null, bmsonSha256).Path);
            resolveWork.Clear();

            BMSFile firstBms = CreateLibraryFile(
                firstBmsPath,
                "cccccccccccccccccccccccccccccccc",
                "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
            InvokeApplyInstalledChartStorageTargets(
                library,
                ChartStorageTargetSet.FromRows([firstBms], []));
            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState invalidated = library.GetPlaylistLibraryResolveIndexRuntimeState();
            Assert.IsFalse(invalidated.IsCached);
            PlaylistLibraryResolveIndexSnapshot afterNormalization = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterNormalizationCacheHit,
                out int afterNormalizationStaleRetries);
            Assert.IsFalse(afterNormalizationCacheHit);
            Assert.AreEqual(0, afterNormalizationStaleRetries);
            Assert.AreEqual(bmsonPath, afterNormalization.ResolveChartForPlaylistHash(null, bmsonSha256).Path);
            Assert.IsTrue(resolveWork.Contains("playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(resolveWork.Contains("playlist_resolve_source_enumeration"));
            resolveWork.Clear();

            BMSFile secondBms = CreateLibraryFile(
                secondBmsPath,
                "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
            InvokeApplyInstalledChartStorageTargets(
                library,
                ChartStorageTargetSet.FromRows([secondBms], []));
            PlaylistLibraryResolveIndexSnapshot afterBmsOnlyUpsert = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool bmsOnlyCacheHit,
                out int bmsOnlyStaleRetries);
            Assert.IsTrue(bmsOnlyCacheHit);
            Assert.AreEqual(0, bmsOnlyStaleRetries);
            Assert.AreEqual(secondBmsPath, afterBmsOnlyUpsert.ResolveChartForPlaylistHash(secondBms.hash, null).Path);
            Assert.AreEqual(0, resolveWork.Count(operation => operation == "playlist_resolve_full_root_enumeration"));
            Assert.AreEqual(0, resolveWork.Count(operation => operation == "playlist_resolve_source_enumeration"));
            Assert.IsTrue(resolveWork.Contains("playlist_resolve_delta_apply"));
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_WarmDeltaDoesNotEnumerateUnchangedSource()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string removedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string removedSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            const int backgroundCount = 16;
            var files = new List<BMSFile>
            {
                CreateLibraryFile(@"C:\Songs\000-removed.bms", removedMd5, removedSha256)
            };
            for (int index = 1; index <= backgroundCount; index++)
            {
                string hash = index.ToString("x32");
                string sha256 = (index + 100).ToString("x64");
                files.Add(CreateLibraryFile($@"C:\Songs\{index:000}-background.bms", hash, sha256));
            }

            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BMSFiles = files
            };
            List<string> storeWork = [];
            library.PlaylistLibraryResolveIndexStoreWorkObserver = storeWork.Add;
            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int _);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(backgroundCount + 1, storeWork.Count(operation => operation == "playlist_resolve_source_entry_visited"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_full_root_enumeration"));

            storeWork.Clear();
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(files[0]));
            InvokeApplyLibraryMutationDelta(library, delta);
            PlaylistLibraryResolveIndexSnapshot updated = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedCacheHit,
                out int updatedStaleRetries);
            PlaylistLibraryResolveIndexSnapshot cached = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool cachedCacheHit,
                out int cachedStaleRetries);

            Assert.IsTrue(updatedCacheHit);
            Assert.AreEqual(0, updatedStaleRetries);
            Assert.IsTrue(cachedCacheHit);
            Assert.AreEqual(0, cachedStaleRetries);
            Assert.AreEqual(updated.Version, cached.Version);
            Assert.AreEqual(0, storeWork.Count(operation => operation == "playlist_resolve_source_enumeration"));
            Assert.AreEqual(0, storeWork.Count(operation => operation == "playlist_resolve_source_entry_visited"));
            Assert.AreEqual(0, storeWork.Count(operation => operation == "playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_delta_apply"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_bucket_update"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_root_capture"));
            Assert.IsNull(updated.ResolveChartForPlaylistHash(removedMd5, null));
            Assert.AreEqual(@"C:\Songs\001-background.bms", updated.ResolveChartForPlaylistHash(files[1].hash, null).Path);
            Assert.IsNotNull(initial.ResolveChartForPlaylistHash(removedMd5, null));
        });
    }

}
