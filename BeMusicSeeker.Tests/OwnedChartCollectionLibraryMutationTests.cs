using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualBasic.FileIO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartCollectionLibraryMutationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RemoveLibraryCharts_CatalogFailureReturnsAfterConfirmedFilesystemDeletion(bool afterCommit)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string folder = Path.Combine(root, "Pack");
            Directory.CreateDirectory(folder);
            string chartPath = Path.Combine(folder, "delete.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            string lr2Root = Path.Combine(root, "LR2");
            LR2Config config = BmsPlaylistTestSupport.CreateLr2Config(lr2Root, root);
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(songDbPath, () => config, null, filesystem, null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = afterCommit, LR2RootPath = lr2Root })
            {
                BMSFiles = [file], BmsonSongs = []
            };
            string folderRowPath = Lr2FolderPath.ToFolderPath(folder);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                db.InsertOrReplace(new LR2SongDB.folder { path = folderRowPath, title = "Pack", type = 1 }, typeof(LR2SongDB.folder));
                db.Execute(afterCommit
                    ? "CREATE TRIGGER fail_delete BEFORE DELETE ON folder BEGIN SELECT RAISE(ABORT, 'required-folder-prune-fault'); END;"
                    : "CREATE TRIGGER fail_delete BEFORE DELETE ON song BEGIN SELECT RAISE(ABORT, 'catalog-delete-fault'); END;");
            }

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts([LibraryChartRef.FromBmsFile(file)], false, []);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(chartPath, outcome.Targets.Single().Path);
            Assert.IsTrue(outcome.CatalogApplyAttempted);
            Assert.AreEqual(afterCommit, outcome.CatalogDurable);
            Assert.AreEqual(afterCommit, outcome.RequiredFinalizationFailed);
            Assert.IsNotNull(outcome.CatalogFailure);
            using var readback = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(afterCommit ? 0 : 1, readback.Table<LR2SongDB.song>().Count());
            Assert.AreEqual(1, readback.Table<LR2SongDB.folder>().Count(row => row.path == folderRowPath));
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_OnlySuccessfulApiTargetsAreConfirmed()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string partialDirectory = Path.Combine(root, "Partial");
            Directory.CreateDirectory(partialDirectory);
            string success = Path.Combine(root, "success.bms");
            string missing = Path.Combine(root, "missing.bms");
            string partial = Path.Combine(partialDirectory, "partial.bms");
            File.WriteAllText(success, "#PLAYER 1");
            File.WriteAllText(partial, "#PLAYER 1");
            var files = new[] { CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", success),
                CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", missing),
                CreateFile("cccccccccccccccccccccccccccccccc", partial) };
            var partialFailure = new IOException("directory changed before failure");
            var filesystem = new TestFileMutationService { BeforeDirectoryDelete = path =>
            {
                Assert.AreEqual(partialDirectory, path);
                File.Delete(partial);
                throw partialFailure;
            }};
            var dialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(songDbPath, null, null, filesystem, dialogs)
            { BMSFiles = files, BmsonSongs = [] };
            using (var db = new LR2SongDBExtended(songDbPath))
                foreach (var file in files)
                    db.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

            LibraryChartRemovalOutcome outcome = library.RemoveLibraryCharts(
                files.Select(LibraryChartRef.FromBmsFile), false, [partialDirectory]);

            Assert.AreEqual(1, outcome.ConfirmedChartCount);
            Assert.AreEqual(LibraryChartRemovalState.Confirmed, outcome.Targets.Single(target => target.Path == success).State);
            Assert.AreEqual(LibraryChartRemovalState.NotExecuted, outcome.Targets.Single(target => target.Path == missing).State);
            Assert.AreEqual(LibraryChartRemovalState.Unconfirmed, outcome.Targets.Single(target => target.Path == partial).State);
            Assert.AreSame(partialFailure, outcome.Targets.Single(target => target.Path == partial).Failure);
            Assert.IsFalse(File.Exists(success));
            Assert.IsFalse(File.Exists(partial));
            Assert.AreEqual(1, filesystem.FileDeleteCalls);
            Assert.AreEqual(1, filesystem.DirectoryDeleteCalls);
            Assert.AreEqual(0, dialogs.ModelMessages);
            using var readback = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(new[] { missing, partial }, readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterKeepsOwnedCollectionInitializedAndSynced()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Second", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath);
            using var initialBmsFilesNotification = new ManualResetEventSlim(false);
            System.ComponentModel.PropertyChangedEventHandler initialHandler = delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    initialBmsFilesNotification.Set();
                }
            };
            library.PropertyChanged += initialHandler;
            library.BMSFiles = [first, second];
            library.BmsonSongs = [];
            library.DuplicateChartGroups = [];
            Assert.IsTrue(initialBmsFilesNotification.Wait(TimeSpan.FromSeconds(5)));
            library.PropertyChanged -= initialHandler;
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
            int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int parentFolderVersionChanged = 0;
            int bmsFilesChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "BMSParentFolderListCacheVersion")
                {
                    parentFolderVersionChanged++;
                }
                if (args.PropertyName == nameof(BMSLibrary.BMSFiles))
                {
                    bmsFilesChanged++;
                }
            };
            var delta = new LibraryMutationDelta
            {
                InvalidateInstalledDirectoryIndex = true,
                InvalidateParentFolderCache = true,
                ClearDuplicatedCache = true
            };
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReferenceChart(initialSnapshot[0]));

            InvokeApplyLibraryMutationDelta(library, delta);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
            Assert.AreEqual(1, parentFolderVersionChanged);
            Assert.AreEqual(0, bmsFilesChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsTrue(batch.NotifiesBmsFiles);
            Assert.IsFalse(batch.NotifiesBmsonSongs);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreSame(second, library.BMSFiles[0]);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void BMSFilesReplacement_InvalidatesOwnedCollectionVersionAndRebuildsOnNextView()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "First", "chart.bms"));
            var replacement = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Installed", "Replacement", "chart.bms"));
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [first],
                BmsonSongs = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            library.BMSFiles = [replacement];
            List<ChartFile> rebuiltSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, rebuiltSnapshot.Count);
            Assert.AreSame(replacement, rebuiltSnapshot[0].GetBmsStorageOwner());
        });
    }

    [TestMethod]
    public void StorageRowPropertiesExposeReadOnlyViews()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            Assert.IsFalse(library.BMSFiles is List<BMSFile>);
            Assert.IsFalse(library.BmsonSongs is List<LR2SongDBExtended.bmson_song>);
            Assert.ThrowsException<NotSupportedException>(() => ((IList<BMSFile>)library.BMSFiles).Add(CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine("C:\\Installed", "Other", "chart.bms"))));
            Assert.ThrowsException<NotSupportedException>(() => ((IList<LR2SongDBExtended.bmson_song>)library.BmsonSongs).Clear());
            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
        });
    }

    [TestMethod]
    public void StorageRowSettersCopyInputLists()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(Path.Combine("C:\\Installed", "Bmson", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            List<BMSFile> inputBmsFiles = [bmsFile];
            List<LR2SongDBExtended.bmson_song> inputBmsonSongs = [bmsonSong];
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = inputBmsFiles,
                BmsonSongs = inputBmsonSongs
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);

            inputBmsFiles.Clear();
            inputBmsonSongs.Clear();
            List<ChartFile> afterInputMutationSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(1, library.BMSFiles.Count);
            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreEqual(2, afterInputMutationSnapshot.Count);
            Assert.AreSame(bmsFile, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bms).GetBmsStorageOwner());
            Assert.AreSame(bmsonSong, afterInputMutationSnapshot.Single(chart => chart.Kind == ChartFileKind.Bmson).GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void HasOwnedChartUnderRealPath_UsesOwnedCollectionForBmsAndBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine("C:\\Installed", "Bms", "chart.bms"));
            var bmsonSong = CreateBmsonSong(
                Path.Combine("C:\\Installed", "Bmson", "chart.bmson"),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };

            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bms")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bmson")));
            Assert.IsTrue(library.HasOwnedChartUnderRealPath("C:\\Installed"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath("C:\\Install"));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
            Assert.IsFalse(library.HasOwnedChartUnderRealPath(null));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregistersBmsonStorageRowsInLibraryBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var first = CreateBmsonSong(Path.Combine("C:\\Installed", "First", "chart.bmson"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var second = CreateBmsonSong(Path.Combine("C:\\Installed", "Second", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, []);
            SetLibraryBmsonSongsWithoutNotification(library, [first, second]);
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(2, initialSnapshot.Count);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int bmsonSongsChanged = 0;
            library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                {
                    bmsonSongsChanged++;
                }
            };
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(first));

            InvokeApplyLibraryMutationDelta(library, delta);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

            Assert.AreEqual(1, library.BmsonSongs.Count);
            Assert.AreSame(second, library.BmsonSongs.Single());
            Assert.AreEqual(0, bmsonSongsChanged);
            Assert.IsTrue(batch.NotifiesStorageRows);
            Assert.IsFalse(batch.NotifiesBmsFiles);
            Assert.IsTrue(batch.NotifiesBmsonSongs);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(second, afterSnapshot[0].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterPathlessStorageRowIsNoOpForOwnedBoundary()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var pathless = CreateBmsonSong(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            var kept = CreateBmsonSong(Path.Combine("C:\\Installed", "Kept", "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            var library = new TestBmsLibrary(songDbPath)
            {
                BMSFiles = [],
                BmsonSongs = [pathless, kept]
            };
            Assert.AreEqual(1, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(pathless));

            InvokeApplyLibraryMutationDelta(library, delta);
            List<ChartFile> afterSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);

            Assert.AreEqual(2, library.BmsonSongs.Count);
            Assert.IsTrue(library.BmsonSongs.Contains(pathless));
            Assert.IsTrue(library.BmsonSongs.Contains(kept));
            Assert.AreEqual(1, afterSnapshot.Count);
            Assert.AreSame(kept, afterSnapshot[0].GetBmsonStorageOwner());
        });
    }

    [TestMethod]
    public void RemoveLibraryCharts_RoutesUnregisterThroughOwnedMutationAndInstalledLookupDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartDirectory = Path.Combine(Path.GetDirectoryName(songDbPath), "DeleteTarget");
            Directory.CreateDirectory(chartDirectory);
            string chartPath = Path.Combine(chartDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService())
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [],
                DuplicateChartGroups = []
            };
            List<ChartFile> initialSnapshot = InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library);
            Assert.AreEqual(1, initialSnapshot.Count);
            InstalledChartLookupIndexSnapshot initialLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsTrue(initialLookup.ContainsPrimaryHash(bmsFile.hash));

            library.RemoveLibraryCharts(
                [LibraryChartRef.FromBmsFile(bmsFile)],
                sendToRecycleBin: false,
                approvedWholeFolderDeletePaths: [chartDirectory]);

            Assert.IsFalse(File.Exists(chartPath));
            Assert.AreEqual(0, library.BMSFiles.Count);
            Assert.IsNull(library.DuplicateChartGroups);
            Assert.AreEqual(0, InvokeCreateOwnedChartInfoFullBackfillTargetSnapshot(library).Count);
            InstalledChartLookupIndexSnapshot updatedLookup = InvokeCreateInstalledChartLookupSnapshot(library);
            Assert.IsFalse(updatedLookup.ContainsPrimaryHash(bmsFile.hash));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_UnregisterAppliesCurrentResourceHealthIndexDelta()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            var bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Installed", "Resource", "chart.bms"));
            bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
            {
                hash = bmsFile.hash,
                wav_files_defined = 2,
                wav_files_existing = 1
            }, suppressPropertyChanged: true);
            var library = new TestBmsLibrary(songDbPath);
            SetLibraryFilesWithoutNotification(library, [bmsFile]);
            SetLibraryBmsonSongsWithoutNotification(library, []);
            EnsureCurrentResourceHealthIndex(library);
            Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(bmsFile));

            InvokeApplyLibraryMutationDelta(library, delta);

            Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
            Assert.IsFalse(HasNoCurrentResourceHealthIndex(library));
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PathChangeInvalidatesCurrentResourceHealthIndex()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ResourceMutation_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            File.WriteAllText(newBmsPath, "#PLAYER 1");
            try
            {
                var oldSnapshotFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", oldBmsPath);
                oldSnapshotFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(oldSnapshotFile)
                {
                    hash = oldSnapshotFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                TestableBmsFile bmsFile = CreateFile(oldSnapshotFile.hash, newBmsPath);
                bmsFile.SetMaintenanceInfo(new BMSFileMaintenanceInfo(bmsFile)
                {
                    hash = bmsFile.hash,
                    wav_files_defined = 2,
                    wav_files_existing = 1
                }, suppressPropertyChanged: true);
                var library = new TestBmsLibrary(songDbPath);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, []);
                EnsureCurrentResourceHealthIndex(library);
                Assert.AreEqual(1, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                var delta = new LibraryMutationDelta();
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);

                Assert.AreEqual(0, library.TryGetCurrentResourceHealthIndexSnapshotForView().TargetCount);
                Assert.IsTrue(HasNoCurrentResourceHealthIndex(library));
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_DispatchesParentFolderOnceAndClearsDuplicateCache()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string songDbPath)
        {
            string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_OwnedMutationDispatch_" + Guid.NewGuid().ToString("N"));
            string oldDirectoryPath = Path.Combine(tempRootPath, "Old");
            string newDirectoryPath = Path.Combine(tempRootPath, "New");
            Directory.CreateDirectory(oldDirectoryPath);
            Directory.CreateDirectory(newDirectoryPath);
            string oldBmsPath = Path.Combine(oldDirectoryPath, "chart.bms");
            string newBmsPath = Path.Combine(newDirectoryPath, "chart.bms");
            string oldBmsonPath = Path.Combine(oldDirectoryPath, "chart.bmson");
            string newBmsonPath = Path.Combine(newDirectoryPath, "chart.bmson");
            File.WriteAllText(newBmsPath, "#PLAYER 1");
            File.WriteAllText(newBmsonPath, "{}");
            try
            {
                TestableBmsFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", newBmsPath);
                LR2SongDBExtended.bmson_song bmsonSong = CreateBmsonSong(oldBmsonPath, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
                var library = new TestBmsLibrary(songDbPath);
                SetLibraryFilesWithoutNotification(library, [bmsFile]);
                SetLibraryBmsonSongsWithoutNotification(library, [bmsonSong]);
                SetDuplicateChartGroupsWithoutNotification(library, []);
                int baselineOwnedCollectionVersion = library.OwnedChartCollectionVersion;
                int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
                int ownedCollectionVersionChanged = 0;
                int bmsFilesChanged = 0;
                int bmsonSongsChanged = 0;
                bool bmsonPathAvailableAtNotification = false;
                int baselineParentFolderVersion = library.BMSParentFolderListCacheVersion;
                int baselineDuplicateInvalidationVersion = library.DuplicateChartGroupsInvalidationVersion;
                int parentFolderVersionChanged = 0;
                library.PropertyChanged += delegate (object _, System.ComponentModel.PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == "OwnedChartCollectionVersion")
                    {
                        ownedCollectionVersionChanged++;
                    }
                    if (args.PropertyName == "BMSFiles")
                    {
                        bmsFilesChanged++;
                    }
                    if (args.PropertyName == nameof(BMSLibrary.BmsonSongs))
                    {
                        bmsonSongsChanged++;
                    }
                    if (args.PropertyName == "OwnedChartCollectionVersion")
                    {
                        bmsonPathAvailableAtNotification = library.BmsonSongs.Any(song => song.path == newBmsonPath);
                    }
                    if (args.PropertyName == "BMSParentFolderListCacheVersion")
                    {
                        parentFolderVersionChanged++;
                    }
                };
                var delta = new LibraryMutationDelta
                {
                    InvalidateParentFolderCache = true,
                    ClearDuplicatedCache = true,
                    NotifyStorageRowPathChanges = true
                };
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsFile(bmsFile),
                    OldPath = oldBmsPath,
                    NewPath = newBmsPath
                });
                delta.ChartPathChanges.Add(new LibraryChartPathChange
                {
                    Chart = ChartFileProjection.FromBmsonSong(bmsonSong),
                    OldPath = oldBmsonPath,
                    NewPath = newBmsonPath
                });

                InvokeApplyLibraryMutationDelta(library, delta);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);

                Assert.AreEqual(baselineOwnedCollectionVersion + 1, library.OwnedChartCollectionVersion);
                Assert.AreEqual(1, ownedCollectionVersionChanged);
                Assert.AreEqual(0, bmsFilesChanged);
                Assert.AreEqual(0, bmsonSongsChanged);
                Assert.IsTrue(batch.NotifiesStorageRows);
                Assert.IsTrue(batch.NotifiesBmsFiles);
                Assert.IsTrue(batch.NotifiesBmsonSongs);
                Assert.IsTrue(bmsonPathAvailableAtNotification);
                Assert.AreEqual(baselineParentFolderVersion + 1, library.BMSParentFolderListCacheVersion);
                Assert.AreEqual(1, parentFolderVersionChanged);
                Assert.IsNull(library.DuplicateChartGroups);
                Assert.AreEqual(baselineDuplicateInvalidationVersion + 1, library.DuplicateChartGroupsInvalidationVersion);
            }
            finally
            {
                if (Directory.Exists(tempRootPath))
                {
                    Directory.Delete(tempRootPath, recursive: true);
                }
            }
        });
    }
}
