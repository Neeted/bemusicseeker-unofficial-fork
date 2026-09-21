using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

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
    public void GetPlaylistLibraryResolveIndexSnapshot_AppliesRemovalAndKeepsPriorSnapshots()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string sharedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string sharedSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Songs");
            Directory.CreateDirectory(rootPath);
            string firstPath = Path.Combine(rootPath, "A.bms");
            string middlePath = Path.Combine(rootPath, "M.bms");
            string lastPath = Path.Combine(rootPath, "Z.bms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(middlePath, "#PLAYER 1");
            File.WriteAllText(lastPath, "#PLAYER 1");
            BMSFile firstFile = CreateLibraryFile(firstPath, sharedMd5, sharedSha256);
            BMSFile middleFile = CreateLibraryFile(middlePath, sharedMd5, sharedSha256);
            BMSFile lastFile = CreateLibraryFile(lastPath, sharedMd5, sharedSha256);
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
            Assert.AreEqual(firstPath, initial.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { firstPath, middlePath, lastPath },
                initial.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            LibraryChartRemovalOutcome removeFirst = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(firstFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeFirst.HasError);
            PlaylistLibraryResolveIndexSnapshot afterFirstRemoval = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterFirstCacheHit,
                out int afterFirstStaleRetries);
            Assert.IsTrue(afterFirstCacheHit);
            Assert.AreEqual(0, afterFirstStaleRetries);
            Assert.AreEqual(middlePath, afterFirstRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { middlePath, lastPath },
                afterFirstRemoval.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            LibraryChartRemovalOutcome removeMiddle = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(middleFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeMiddle.HasError);
            PlaylistLibraryResolveIndexSnapshot afterSecondRemoval = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterSecondCacheHit,
                out int afterSecondStaleRetries);
            Assert.IsTrue(afterSecondCacheHit);
            Assert.AreEqual(0, afterSecondStaleRetries);
            Assert.AreEqual(lastPath, afterSecondRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { lastPath },
                afterSecondRemoval.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            LibraryChartRemovalOutcome removeLast = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(lastFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeLast.HasError);
            PlaylistLibraryResolveIndexSnapshot empty = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool emptyCacheHit,
                out int emptyStaleRetries);
            Assert.IsTrue(emptyCacheHit);
            Assert.AreEqual(0, emptyStaleRetries);
            Assert.IsNull(empty.ResolveChartForPlaylistHash(sharedMd5, null));
            Assert.AreEqual(firstPath, initial.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            Assert.AreEqual(middlePath, afterFirstRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            Assert.AreEqual(lastPath, afterSecondRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
        });
    }

    [TestMethod]
    public void ReloadFileDiff_ReplacesExactCandidateAndRetainsOldSnapshot()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Replacement");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "replace.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Old\r\n#BPM 120\r\n#00111:01\r\n");
            ChartFileSnapshot oldSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            string oldMd5 = oldSnapshot.Md5;
            string oldSha256 = oldSnapshot.Sha256;
            BMSFile oldFile = CreateLibraryFile(chartPath, oldMd5, oldSha256);
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                ScanBmsFilesOnStartup = false,
                UpdateLr2IrRankingCacheOnStartup = false,
                EnableDownloadLr2IrScoreAndDetectUnsent = false,
                UseBeatorajaScoreDb = false,
                EnableReadOptimizedPragmas = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            IChartFileScanner chartFileScanner = CapturedChartFileScanner.FromFixture(
                [chartPath],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [chartDirectory] = []
                },
                [chartDirectory]);
            var library = new TestBmsLibrary(
                songDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                dialogService: new RecordingDialogService())
            {
                SearchTargets = [chartDirectory],
                BMSFiles = [oldFile]
            };

            try
            {
                PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool initialCacheHit,
                    out int _);
                Assert.IsFalse(initialCacheHit);
                Assert.AreEqual(chartPath, initial.ResolveChartForPlaylistHash(oldMd5, null).Path);

                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE New\r\n#BPM 130\r\n#00111:02\r\n");
                ChartFileSnapshot newSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
                string newMd5 = newSnapshot.Md5;
                string newSha256 = newSnapshot.Sha256;
                library.ReloadFileDiff();
                PlaylistLibraryResolveIndexSnapshot updated = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool updatedCacheHit,
                    out int updatedStaleRetries);

                // 実ファイル走査による digest 置換は full replacement を経由するため、
                // lookup は再構築される。旧 snapshot と新 hash の対応は下で検証する。
                Assert.IsFalse(updatedCacheHit);
                Assert.AreEqual(0, updatedStaleRetries);
                Assert.IsNull(updated.ResolveChartForPlaylistHash(oldMd5, null));
                Assert.AreEqual(chartPath, updated.ResolveChartForPlaylistHash(newMd5, null).Path);
                Assert.IsNull(updated.ResolveChartForPlaylistHash(oldMd5, newSha256));
                Assert.AreEqual(chartPath, updated.ResolveChartForPlaylistHash(null, newSha256).Path);
                Assert.IsNotNull(initial.ResolveChartForPlaylistHash(oldMd5, null));
                Assert.IsNull(initial.ResolveChartForPlaylistHash(newMd5, null));
            }
            finally
            {
                library.RequestShutdown("playlist-resolve-exact-replacement-test");
            }
        });
    }

    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_MovesCandidateAndKeepsCanonicalCandidateOrder()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string sharedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string sharedSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string oldDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Move", "Old");
            string newDirectory = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Move", "New");
            Directory.CreateDirectory(oldDirectory);
            string firstPath = Path.Combine(oldDirectory, "A.bms");
            string movedPath = Path.Combine(oldDirectory, "Z.bms");
            string firstMovedPath = Path.Combine(newDirectory, "A.bms");
            string newMovedPath = Path.Combine(newDirectory, "Z.bms");
            File.WriteAllText(firstPath, "#PLAYER 1");
            File.WriteAllText(movedPath, "#PLAYER 1");
            BMSFile firstFile = CreateLibraryFile(firstPath, sharedMd5, sharedSha256);
            BMSFile movedFile = CreateLibraryFile(movedPath, sharedMd5, sharedSha256);
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

            LibraryMutationSessionReceipt moveReceipt = library.RenameChartFolderWithReceipt(
                oldDirectory,
                "New",
                unregister: false,
                renameRootFolder: false);
            Assert.IsTrue(moveReceipt.DurableCommit, moveReceipt.PrimaryFailure?.ToString());

            PlaylistLibraryResolveIndexSnapshot updated = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedCacheHit,
            out int updatedStaleRetries);
            Assert.IsTrue(updatedCacheHit);
            Assert.AreEqual(0, updatedStaleRetries);
            Assert.IsFalse(updated.ContainsCandidate(LibraryChartKind.Bms, firstPath));
            Assert.IsFalse(updated.ContainsCandidate(LibraryChartKind.Bms, movedPath));
            Assert.IsTrue(updated.ContainsCandidate(LibraryChartKind.Bms, firstMovedPath));
            Assert.IsTrue(updated.ContainsCandidate(LibraryChartKind.Bms, newMovedPath));
            Assert.AreEqual(firstMovedPath, updated.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { firstMovedPath, newMovedPath },
                updated.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());
            Assert.IsTrue(initial.ContainsCandidate(LibraryChartKind.Bms, firstPath));
            Assert.IsTrue(initial.ContainsCandidate(LibraryChartKind.Bms, movedPath));
            Assert.AreEqual(movedPath, initial.GetMd5Candidates(sharedMd5)[1].Path);
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
            const string bmsonMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string bmsonSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = bmsonPath,
                md5 = bmsonMd5,
                sha256 = bmsonSha256,
                updated_at = File.GetLastWriteTimeUtc(bmsonPath)
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = false,
                ScanBmsFilesOnStartup = false,
                UpdateLr2IrRankingCacheOnStartup = false,
                EnableDownloadLr2IrScoreAndDetectUnsent = false,
                UseBeatorajaScoreDb = false,
                EnableReadOptimizedPragmas = false,
                PendingInstallEstimateMaxParallelPackages = 1
            };
            var chartFileScanner = new MutableChartFileScanner([bmsonPath, firstBmsPath]);
            var library = new TestBmsLibrary(
                songDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options,
                applicationPathSnapshot: TestBmsFactory.MissingEverythingBridge,
                chartFileScanner: chartFileScanner,
                dialogService: new RecordingDialogService())
            {
                SearchTargets = [chartDirectory]
            };
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);

            try
            {
                List<string> resolveWork = [];
                library.PlaylistLibraryResolveIndexStoreWorkObserver = resolveWork.Add;
                PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool initialCacheHit,
                    out int _);
                Assert.IsFalse(initialCacheHit);
                Assert.AreEqual(bmsonPath, initial.ResolveChartForPlaylistHash(null, bmsonSha256).Path);
                resolveWork.Clear();

                library.ReloadFileDiff();
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

                File.WriteAllText(secondBmsPath, "#PLAYER 1\r\n#TITLE Second\r\n");
                ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondBmsPath);
                BMSFile secondBms = CreateLibraryFile(
                    secondBmsPath,
                    secondSnapshot.Md5,
                    secondSnapshot.Sha256);
                chartFileScanner.SetChartPaths([bmsonPath, firstBmsPath, secondBmsPath]);
                library.ReloadFileDiff();
                PlaylistLibraryResolveIndexSnapshot afterBmsOnlyUpsert = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool bmsOnlyCacheHit,
                    out int bmsOnlyStaleRetries);
                // 新規 BMS の導入は ReloadFileDiff の通常全走査経路で公開されるため、
                // この境界では解決索引を再構築する。
                Assert.IsFalse(bmsOnlyCacheHit);
                Assert.AreEqual(0, bmsOnlyStaleRetries);
                Assert.AreEqual(secondBmsPath, afterBmsOnlyUpsert.ResolveChartForPlaylistHash(secondBms.hash, null).Path);
                Assert.IsTrue(resolveWork.Contains("playlist_resolve_full_root_enumeration"));
                Assert.IsTrue(resolveWork.Contains("playlist_resolve_source_enumeration"));
                Assert.AreEqual(0, resolveWork.Count(operation => operation == "playlist_resolve_delta_apply"));
            }
            finally
            {
                library.RequestShutdown("playlist-resolve-bmson-normalization-test");
            }
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
            string rootPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Songs");
            Directory.CreateDirectory(rootPath);
            var files = new List<BMSFile>
            {
                CreateLibraryFile(Path.Combine(rootPath, "000-removed.bms"), removedMd5, removedSha256)
            };
            File.WriteAllText(files[0].path, "#PLAYER 1");
            for (int index = 1; index <= backgroundCount; index++)
            {
                string hash = index.ToString("x32");
                string sha256 = (index + 100).ToString("x64");
                string path = Path.Combine(rootPath, $"{index:000}-background.bms");
                File.WriteAllText(path, "#PLAYER 1");
                files.Add(CreateLibraryFile(path, hash, sha256));
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
            LibraryChartRemovalOutcome removal = library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(files[0])],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
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
            Assert.AreEqual(files[1].path, updated.ResolveChartForPlaylistHash(files[1].hash, null).Path);
            Assert.IsNotNull(initial.ResolveChartForPlaylistHash(removedMd5, null));
        });
    }

    private sealed class MutableChartFileScanner : IChartFileScanner
    {
        private IReadOnlyList<string> chartPaths = [];

        internal MutableChartFileScanner(IEnumerable<string> chartPaths)
        {
            SetChartPaths(chartPaths);
        }

        internal void SetChartPaths(IEnumerable<string> paths)
        {
            chartPaths = [.. paths ?? []];
        }

        public ChartScanExecutionResult Scan(
            IEnumerable<string> rootDirectories,
            IEnumerable<string> chartExtensions,
            bool verboseLog = false,
            bool includeTextSurface = true,
            bool includeDirectorySurface = false)
        {
            Dictionary<string, IEnumerable<string>> resourcesByDirectory = new(StringComparer.OrdinalIgnoreCase);
            foreach (string? directory in chartPaths.Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                resourcesByDirectory.TryAdd(directory!, []);
            }

            return new ChartScanExecutionResult
            {
                ScanSource = ChartScanSource.Everything,
                Success = true,
                IsComplete = true,
                Result = BmsLibraryInitializationTestSupport.CreateScanResult(
                    chartPaths,
                    resourcesByDirectory,
                    resourcesByDirectory.Keys)
            };
        }
    }

}
