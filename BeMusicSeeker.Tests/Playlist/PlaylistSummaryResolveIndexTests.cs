using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>譜面解決索引の変更反映・候補選択・局所更新を検証する。</summary>
/// <remarks>専用SQLiteと実ファイル変更を使い、確定後のライブラリから索引を更新する接続を担う。検索の入力判断だけでは検出できない旧候補の残存・全件再構築を確認する。</remarks>
[TestClass]
public sealed class PlaylistSummaryResolveIndexTests
{
    /// <summary>索引を再利用した後にライブラリ集合を差替えると、新世代で旧ハッシュを除き新ハッシュを解決する。集合差替後の索引失効漏れを検出する。</summary>
    [TestMethod]
    public void GetPlaylistLibraryResolveIndexSnapshot_CachesAndRebuildsAfterOwnershipChange()
    {
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts =
                [
                    CreateLibraryFile(@"C:\Songs\old.bms", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
                ]
            };

            PlaylistLibraryResolveIndexSnapshot first = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool firstCacheHit, out int _);
            PlaylistLibraryResolveIndexSnapshot second = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool secondCacheHit, out int _);
            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState cachedState = library.GetPlaylistLibraryResolveIndexRuntimeState();

            Assert.IsFalse(firstCacheHit);
            Assert.IsTrue(secondCacheHit);
            Assert.AreEqual(first.Version, second.Version);
            Assert.IsTrue(cachedState.IsCached);
            Assert.AreEqual(first.Version, cachedState.SnapshotVersion);
            Assert.IsNotNull(first.ResolveChartForPlaylistHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null));

            library.BmsCharts =
            [
                CreateLibraryFile(@"C:\Songs\new.bms", "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd")
            ];

            BMSLibrary.PlaylistLibraryResolveIndexRuntimeState invalidatedState = library.GetPlaylistLibraryResolveIndexRuntimeState();
            PlaylistLibraryResolveIndexSnapshot third = library.GetPlaylistLibraryResolveIndexSnapshot(System.Threading.CancellationToken.None, out bool thirdCacheHit, out int _);

            Assert.IsFalse(invalidatedState.IsCached);
            Assert.IsFalse(thirdCacheHit);
            Assert.IsTrue(third.Version > second.Version);
            Assert.IsTrue(third.InvalidationVersion > second.InvalidationVersion);
            Assert.AreEqual(library.CatalogRevision, third.CatalogRevision);
            Assert.IsTrue(third.CatalogRevision > second.CatalogRevision);
            Assert.IsNull(third.ResolveChartForPlaylistHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null));
            Assert.IsNotNull(third.ResolveChartForPlaylistHash("cccccccccccccccccccccccccccccccc", null));
        });
    }

    /// <summary>同じハッシュの候補を順に実削除し、残る先頭候補へ切替えつつ過去の索引を保持する。削除済み候補の選択と旧スナップショットの可変共有を検出する。</summary>
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
            ChartFile firstFile = CreateLibraryFile(firstPath, sharedMd5, sharedSha256);
            ChartFile middleFile = CreateLibraryFile(middlePath, sharedMd5, sharedSha256);
            ChartFile lastFile = CreateLibraryFile(lastPath, sharedMd5, sharedSha256);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = [firstFile, middleFile, lastFile]
            };

            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int _);
            Assert.IsFalse(initialCacheHit);
            Assert.AreEqual(firstPath, initial.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { firstPath, middlePath, lastPath },
                initial.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            LibraryChartRemovalOutcome removeFirst = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((firstFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeFirst.HasError);
            PlaylistLibraryResolveIndexSnapshot afterFirstRemoval = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterFirstCacheHit,
                out int _);
            Assert.IsTrue(afterFirstCacheHit);
            Assert.AreEqual(middlePath, afterFirstRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { middlePath, lastPath },
                afterFirstRemoval.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            LibraryChartRemovalOutcome removeMiddle = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((middleFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeMiddle.HasError);
            PlaylistLibraryResolveIndexSnapshot afterSecondRemoval = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool afterSecondCacheHit,
                out int _);
            Assert.IsTrue(afterSecondCacheHit);
            Assert.AreEqual(lastPath, afterSecondRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { lastPath },
                afterSecondRemoval.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());

            LibraryChartRemovalOutcome removeLast = library.RemoveLibraryCharts(
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((lastFile))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removeLast.HasError);
            PlaylistLibraryResolveIndexSnapshot empty = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool emptyCacheHit,
                out int _);
            Assert.IsTrue(emptyCacheHit);
            Assert.IsNull(empty.ResolveChartForPlaylistHash(sharedMd5, null));
            Assert.AreEqual(firstPath, initial.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            Assert.AreEqual(middlePath, afterFirstRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            Assert.AreEqual(lastPath, afterSecondRemoval.ResolveChartForPlaylistHash(sharedMd5, null).Path);
        });
    }

    /// <summary>実ファイル内容の変更を差分再読込みし、新MD5・SHAだけで解決でき、旧索引は旧内容を保持する。digest置換後の旧ハッシュ残存とMD5不一致時のSHA誤補完を検出する。</summary>
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
            ChartFile oldFile = CreateLibraryFile(chartPath, oldMd5, oldSha256);
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
                dialogService: new RecordingDialogService(), settings: TestBmsFactory.CreateRootSettings([chartDirectory]))
            {

                BmsCharts = [oldFile]
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
                    out int _);

                // 実ファイル走査によるdigest変更は集合置換で索引を再構築し、旧索引は保持する。
                Assert.IsFalse(updatedCacheHit);
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

    /// <summary>実フォルダ改名の確定後は候補を新パスへ移し、順序と先頭選択を維持して旧索引を変更しない。パス変更の配送漏れと候補順の逆転を検出する。</summary>
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
            ChartFile firstFile = CreateLibraryFile(firstPath, sharedMd5, sharedSha256);
            ChartFile movedFile = CreateLibraryFile(movedPath, sharedMd5, sharedSha256);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService())
            {
                BmsCharts = [firstFile, movedFile]
            };

            PlaylistLibraryResolveIndexSnapshot initial = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool initialCacheHit,
                out int _);
            Assert.IsFalse(initialCacheHit);
            CollectionAssert.AreEqual(
                new[] { firstFile.Path, movedFile.Path },
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
            out int _);
            Assert.IsTrue(updatedCacheHit);
            Assert.IsFalse(updated.ContainsCandidate(ChartFileKind.Bms, firstPath));
            Assert.IsFalse(updated.ContainsCandidate(ChartFileKind.Bms, movedPath));
            Assert.IsTrue(updated.ContainsCandidate(ChartFileKind.Bms, firstMovedPath));
            Assert.IsTrue(updated.ContainsCandidate(ChartFileKind.Bms, newMovedPath));
            Assert.AreEqual(firstMovedPath, updated.ResolveChartForPlaylistHash(sharedMd5, null).Path);
            CollectionAssert.AreEqual(
                new[] { firstMovedPath, newMovedPath },
                updated.GetMd5Candidates(sharedMd5).Select(chart => chart.Path).ToArray());
            Assert.IsTrue(initial.ContainsCandidate(ChartFileKind.Bms, firstPath));
            Assert.IsTrue(initial.ContainsCandidate(ChartFileKind.Bms, movedPath));
            Assert.AreEqual(movedPath, initial.GetMd5Candidates(sharedMd5)[1].Path);
        });
    }

    /// <summary>初回BMSONを含む実差分走査と次のBMS走査は実全置換として索引を再構築し、旧snapshotと代表を保持します。upsert時のwarm順序更新はCatalogMaintenanceCurrentValueTestsへ分担します。</summary>
    [TestMethod]
    public void ReloadFileDiff_RebuildsResolveIndexForInitialBmsonAndFollowingBmsScan()
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
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = bmsonPath,
                Md5 = bmsonMd5,
                Sha256 = bmsonSha256,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(bmsonPath)
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
                dialogService: new RecordingDialogService(), settings: TestBmsFactory.CreateRootSettings([chartDirectory]))
            {

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

                int beforeScan = library.NormalLibraryRefreshNotificationVersion;
                library.ReloadFileDiff();
                Assert.IsTrue(library.GetNormalLibraryRefreshNotificationsAfter(beforeScan).CatalogCommits.Any(receipt => receipt.IsFullReplacement),
                    "ReloadFileDiffは差分走査後の集合を実全置換する入口です。upsertのwarm契約で上書きしません。");
                BMSLibrary.PlaylistLibraryResolveIndexRuntimeState invalidated = library.GetPlaylistLibraryResolveIndexRuntimeState();
                Assert.IsFalse(invalidated.IsCached);
                PlaylistLibraryResolveIndexSnapshot afterNormalization = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool afterNormalizationCacheHit,
                    out int _);
                Assert.IsFalse(afterNormalizationCacheHit);
                Assert.AreEqual(bmsonPath, afterNormalization.ResolveChartForPlaylistHash(null, bmsonSha256).Path);
                Assert.IsTrue(resolveWork.Contains("playlist_resolve_full_root_enumeration"));
                Assert.IsTrue(resolveWork.Contains("playlist_resolve_source_enumeration"));
                Assert.AreEqual(bmsonPath, initial.ResolveChartForPlaylistHash(null, bmsonSha256).Path);
                resolveWork.Clear();

                File.WriteAllText(secondBmsPath, "#PLAYER 1\r\n#TITLE Second\r\n");
                ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondBmsPath);
                ChartFile secondBms = CreateLibraryFile(
                    secondBmsPath,
                    secondSnapshot.Md5,
                    secondSnapshot.Sha256);
                chartFileScanner.SetChartPaths([bmsonPath, firstBmsPath, secondBmsPath]);
                library.ReloadFileDiff();
                PlaylistLibraryResolveIndexSnapshot afterBmsOnlyUpsert = library.GetPlaylistLibraryResolveIndexSnapshot(
                    CancellationToken.None,
                    out bool bmsOnlyCacheHit,
                    out int _);
                // 新規 BMS の導入は ReloadFileDiff の通常全走査経路で公開されるため、
                // この境界では解決索引を再構築する。
                Assert.IsFalse(bmsOnlyCacheHit);
                Assert.AreEqual(secondBmsPath, afterBmsOnlyUpsert.ResolveChartForPlaylistHash(secondBms.Md5, null).Path);
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

    /// <summary>事前構築後の既知削除は対象bucketだけを更新し、他16件や全索引を走査せず旧索引を維持する。局所変更による全件処理の退行を検出する。</summary>
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
            var files = new List<ChartFile>
            {
                CreateLibraryFile(Path.Combine(rootPath, "000-removed.bms"), removedMd5, removedSha256)
            };
            File.WriteAllText(files[0].Path, "#PLAYER 1");
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
                BmsCharts = files
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
                library.PrepareLibraryChartRemoval([LibraryChartRef.FromChartFile((files[0]))]),
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: []);
            Assert.IsFalse(removal.HasError);
            PlaylistLibraryResolveIndexSnapshot updated = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool updatedCacheHit,
                out int _);
            PlaylistLibraryResolveIndexSnapshot cached = library.GetPlaylistLibraryResolveIndexSnapshot(
                CancellationToken.None,
                out bool cachedCacheHit,
                out int _);

            Assert.IsTrue(updatedCacheHit);
            Assert.IsTrue(cachedCacheHit);
            Assert.AreEqual(updated.Version, cached.Version);
            Assert.AreEqual(0, storeWork.Count(operation => operation == "playlist_resolve_source_enumeration"));
            Assert.AreEqual(0, storeWork.Count(operation => operation == "playlist_resolve_source_entry_visited"));
            Assert.AreEqual(0, storeWork.Count(operation => operation == "playlist_resolve_full_root_enumeration"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_delta_apply"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_bucket_update"));
            Assert.IsTrue(storeWork.Contains("playlist_resolve_root_capture"));
            Assert.IsNull(updated.ResolveChartForPlaylistHash(removedMd5, null));
            Assert.AreEqual(files[1].Path, updated.ResolveChartForPlaylistHash(files[1].Md5, null).Path);
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
