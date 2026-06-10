using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryLr2FullGenerationSyncTests
{
    [TestMethod]
    public void ShouldIncludeLr2TextSurface_OnlyWhenLr2FullGenerationEnabled()
    {
        Assert.IsTrue(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            EnableLR2SongDbFullGeneration = true
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            EnableLR2SongDbFullGeneration = false
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            EnableLR2SongDbFullGeneration = true
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(null));
    }

    [TestMethod]
    public void GetBmsDirectories_ExcludesCustomFolderOutputSearchRootsAndExplicitChildren()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string nestedCustomOutputLikeDirectory = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string normalOutputBase = Path.Combine(scope.DirectoryPath, "NormalCustomFolderOutput");
            string normalOutputChild = Path.Combine(normalOutputBase, "Table");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootCustomFolderOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "Table");
            Directory.CreateDirectory(bmsRoot);
            Directory.CreateDirectory(nestedCustomOutputLikeDirectory);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(normalOutputChild);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(rootOutputChild);
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot, nestedCustomOutputLikeDirectory, normalOutputBase, normalOutputChild, rootOutputBase, rootOutputChild]
            };

            HashSet<string> directories = [.. InvokeGetBmsDirectories(library).Select(NormalizeDirectory)];

            Assert.IsTrue(directories.Contains(NormalizeDirectory(bmsRoot)));
            Assert.IsTrue(directories.Contains(NormalizeDirectory(nestedCustomOutputLikeDirectory)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(normalOutputBase)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(normalOutputChild)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(rootOutputBase)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(rootOutputChild)));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void GetBmsDirectories_ReportsCustomFolderOutputRootNormalizationCounts()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string missingRoot = Path.Combine(scope.DirectoryPath, "Missing");
            string normalOutputBase = Path.Combine(scope.DirectoryPath, "NormalCustomFolderOutput");
            string normalOutputChild = Path.Combine(normalOutputBase, "Table");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootCustomFolderOutput");
            Directory.CreateDirectory(bmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(normalOutputChild);
            Directory.CreateDirectory(rootOutputBase);
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot, missingRoot, normalOutputBase, normalOutputChild, rootOutputBase]
            };

            List<string> directories = InvokeGetBmsDirectories(library, out object normalization);

            CollectionAssert.AreEqual(new[] { NormalizeDirectory(bmsRoot) }, directories.Select(NormalizeDirectory).ToArray());
            Assert.AreEqual(5, GetPrivateInt(normalization, "RequestedRootCount"));
            Assert.AreEqual(4, GetPrivateInt(normalization, "ExistingRootCount"));
            Assert.AreEqual(1, GetPrivateInt(normalization, "RootCount"));
            Assert.AreEqual(2, GetPrivateInt(normalization, "ConfiguredCustomOutputRootCount"));
            Assert.AreEqual(3, GetPrivateInt(normalization, "ExcludedCustomOutputRootCount"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_SyncsNormalFolderRowsWhenFullGenerationEnabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string songDirectory = Path.Combine(packDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile file = CreateSyncTestFile(chartPath, snapshot);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_lr2_normal_folder_add");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder[] folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.AreEqual(3, folders.Count(folder => folder.type == 1));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(packDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(songDirectory)));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_DoesNotSyncNormalFolderRowsWhenFullGenerationDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = false;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile file = CreateSyncTestFile(chartPath, snapshot);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_lr2_normal_folder_add_disabled");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PrunesNormalFolderRowsForRemovedBmsWhenFullGenerationEnabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string keepDirectory = Path.Combine(packDirectory, "Keep");
            string removeDirectory = Path.Combine(packDirectory, "Remove");
            Directory.CreateDirectory(keepDirectory);
            Directory.CreateDirectory(removeDirectory);
            string keepPath = Path.Combine(keepDirectory, "keep.bms");
            string removePath = Path.Combine(removeDirectory, "remove.bms");
            File.WriteAllText(keepPath, "#TITLE Keep\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(removePath, "#TITLE Remove\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile keepFile = CreateSyncTestFile(keepPath, ChartFileContentReader.ReadSnapshot(keepPath));
            BMSFile removeFile = CreateSyncTestFile(removePath, ChartFileContentReader.ReadSnapshot(removePath));
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.CreateTable<LR2SongDB.song>();
                setup.InsertOrReplace(keepFile, typeof(LR2SongDB.song));
                setup.InsertOrReplace(removeFile, typeof(LR2SongDB.song));
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([keepFile, removeFile], []), "test_lr2_normal_folder_seed");
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(removeFile));

            InvokeApplyLibraryMutationDelta(library, delta);

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(packDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(keepDirectory)));
            Assert.IsFalse(folders.Any(folder => folder.path == ToFolderPath(removeDirectory)));
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(keepPath));
            Assert.IsNull(verify.Find<LR2SongDB.song>(removePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_MovesNormalFolderRowsForMovedBmsWhenFullGenerationEnabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string oldDirectory = Path.Combine(packDirectory, "Old");
            string newDirectory = Path.Combine(packDirectory, "New");
            Directory.CreateDirectory(oldDirectory);
            Directory.CreateDirectory(newDirectory);
            string oldPath = Path.Combine(oldDirectory, "chart.bms");
            string newPath = Path.Combine(newDirectory, "chart.bms");
            string newFolderInfoPath = Path.Combine(newDirectory, "folderinfo.txt");
            DateTime newDirectoryTimestamp = new(2026, 6, 11, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(oldPath, "#TITLE Moved\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(newPath, "#TITLE Moved\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(newFolderInfoPath, "#TITLE Owned Mutation New", Encoding.GetEncoding("shift_jis"));
            Directory.SetLastWriteTimeUtc(newDirectory, newDirectoryTimestamp);
            BMSFile file = CreateSyncTestFile(oldPath, ChartFileContentReader.ReadSnapshot(oldPath));
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.CreateTable<LR2SongDB.song>();
                setup.InsertOrReplace(file, typeof(LR2SongDB.song));
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_lr2_normal_folder_seed");
            var delta = new LibraryMutationDelta();
            delta.ChartPathChanges.Add(new LibraryChartPathChange
            {
                Chart = ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false, includeResourceReferences: false),
                OldPath = oldPath,
                NewPath = newPath
            });

            InvokeApplyLibraryMutationDelta(library, delta);

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.AreEqual(newPath, file.path);
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(rootDirectory)));
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(packDirectory)));
            Assert.IsFalse(folders.Any(folder => folder.path == ToFolderPath(oldDirectory)));
            LR2SongDB.folder newFolder = folders.Single(folder => folder.path == ToFolderPath(newDirectory));
            Assert.AreEqual("Owned Mutation New", newFolder.title);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(newDirectoryTimestamp), newFolder.date);
            Assert.IsNull(verify.Find<LR2SongDB.song>(oldPath));
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(newPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BlocksWhileFullGenerationSyncIsRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeBeginLr2FullGenerationSyncRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_blocked_add"));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2FullGenerationSyncRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BlocksRunningSyncEvenWhenFeatureIsDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = false;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile file = CreateSyncTestFile(chartPath, snapshot);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeBeginLr2FullGenerationSyncRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_disabled_running_add"));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2FullGenerationSyncRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SetModeAndCommitToDb_BlocksWhileFullGenerationSyncIsRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            var library = new BMSLibrary(scope.SongDbPath)
            {
                BMSFiles = []
            };
            InvokeBeginLr2FullGenerationSyncRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeSetModeAndCommitToDb(library, []));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2FullGenerationSyncRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DoesNotQueueWhenFeatureIsDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = false;
            ResetLr2FolderDiscoverySettings();
            var library = new BMSLibrary(scope.SongDbPath);
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationDataSync("test_disabled");

            Assert.AreEqual(Lr2FullGenerationStatusKind.NotNeeded, snapshot.Status);
            Assert.AreEqual(1, library.Lr2FullGenerationStatusVersion);
            Assert.AreEqual(Lr2FullGenerationStatusKind.NotNeeded, library.GetLr2FullGenerationStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(0, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.AreEqual(0, library.Lr2FullGenerationSyncCompletedVersion);
            Assert.IsFalse(library.Lr2FullGenerationSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void FileDiffNormalFolderSyncFailureMarksFullGenerationIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            var result = new SongTableFileCheckResult
            {
                Lr2NormalFolderSyncFailed = true,
                Lr2NormalFolderSyncFailureReason = "db locked"
            };

            InvokeMarkLr2FullGenerationIncompleteAfterFileDiffNormalFolderSyncFailure(library, options, result);

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_normal_folder_file_diff_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2FullGenerationStatusSnapshot snapshot = library.GetLr2FullGenerationStatusSnapshot();
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_normal_folder_file_diff_sync_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void MutationNormalFolderSyncFailureMarksFullGenerationIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };

            InvokeMarkLr2FullGenerationIncompleteAfterNormalFolderSyncFailure(
                library,
                options,
                stage: "lr2_normal_folder_mutation_sync_failed",
                detail: "lr2_normal_folder_mutation_sync_failed: db locked",
                logReason: "lr2_normal_folder_mutation_sync_failed");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2FullGenerationStatusSnapshot snapshot = library.GetLr2FullGenerationStatusSnapshot();
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SongDbWriteFailureMarksFullGenerationIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };

            InvokeMarkLr2FullGenerationIncompleteAfterSongDbWriteFailure(
                library,
                options,
                stage: "lr2_song_db_test_write_failed",
                detail: "lr2_song_db_test_write_failed: db locked",
                logReason: "lr2_song_db_test_write_failed");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_test_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2FullGenerationStatusSnapshot snapshot = library.GetLr2FullGenerationStatusSnapshot();
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_test_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void FileDiffSongDbWriteFailureMarksFullGenerationIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };

            InvokeMarkLr2FullGenerationIncompleteAfterFileDiffSongDbWriteFailure(
                library,
                options,
                new InvalidOperationException("db locked"),
                "reload_file_diff");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_file_diff_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2FullGenerationStatusSnapshot snapshot = library.GetLr2FullGenerationStatusSnapshot();
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_file_diff_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void MaintenanceSongDbWriteFailureMarksFullGenerationIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }

            InvokeMarkLr2FullGenerationIncompleteAfterMaintenanceSongDbWriteFailure(
                library,
                new InvalidOperationException("db locked"),
                "manual_rescan_all_owned");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_maintenance_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2FullGenerationStatusSnapshot snapshot = library.GetLr2FullGenerationStatusSnapshot();
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_maintenance_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DoesNotQueueAgainWhileRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };
            InvokeBeginLr2FullGenerationSyncRequest(library);

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationDataSync("test_running");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Running, snapshot.Status);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Running, library.GetLr2FullGenerationStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.IsTrue(library.Lr2FullGenerationSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DoesNotStartFromIncompleteWhenDisallowed()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            var library = new BMSLibrary(scope.SongDbPath);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkIncomplete(
                    setup,
                    Lr2FullGenerationSignatureBuilder.Build(new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
                        EnableLR2SongDbFullGeneration = true
                    }),
                    "scoped-folder-sync",
                    processedCursor: null,
                    totalCount: null,
                    stage: "lr2_builtin_folder_scoped_sync_failed",
                    detail: "failed",
                    nowUtc: DateTime.UtcNow);
            }
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationDataSync(
                "test_settings_scoped_failure",
                force: false,
                allowIncompleteToQueue: false);

            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, snapshot.StoredStatus);
            Assert.IsFalse(queued);
            Assert.AreEqual(0, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.IsFalse(library.Lr2FullGenerationSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_RunsPrepareBeforeMarkingSyncRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            library.StartupBackgroundTaskScheduler = delegate
            {
                return true;
            };
            bool prepareCalled = false;

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationDataSync(
                "test_prepare_order",
                force: false,
                () =>
                {
                    prepareCalled = true;
                    Assert.IsFalse(library.Lr2FullGenerationSyncRunning);
                    Assert.AreEqual(0, library.Lr2FullGenerationSyncRequestedVersion);
                    return Lr2FullGenerationPreparedDataSurface.Empty;
                });

            Assert.IsTrue(prepareCalled);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, snapshot.Status);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.IsTrue(library.Lr2FullGenerationSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncLr2BuiltinCustomFolderRows_DoesNotPruneAppManagedOutputUnderBuiltinPhysicalDirectory()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            string appManagedDirectory = Path.Combine(builtinRoot, "BeMusicSeekerRoot");
            Directory.CreateDirectory(appManagedDirectory);
            Settings.Default.LR2RootPath = lr2Root;
            string appManagedLr2FolderPath = Path.Combine(appManagedDirectory, "0000.lr2folder");
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = appManagedLr2FolderPath,
                    title = "App Managed",
                    parent = Lr2SongFolderParentNormalizer.RootParentHash,
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new BMSLibrary(scope.SongDbPath);

            library.SyncLr2BuiltinCustomFolderRows("test_builtin_scope");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsNotNull(verify.Table<LR2SongDB.folder>().ToList().SingleOrDefault(row => row.path == appManagedLr2FolderPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncLr2BuiltinCustomFolderRows_UsesFolderInfoForCategoryRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            Settings.Default.LR2RootPath = lr2Root;
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(Path.Combine(randomDirectory, "random.lr2folder"), "#TITLE Random", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0x1, titleFlashHours: 24, Path.Combine(scope.DirectoryPath, "BMS"));
            var library = new BMSLibrary(scope.SongDbPath, () => config);

            Lr2FullGenerationPreparedDataSurface surface = library.SyncLr2BuiltinCustomFolderRows("test_builtin_folderinfo");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder category = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual("Random Folder Info", category.title);
            CollectionAssert.Contains(surface.FolderInfoFilePaths.ToList(), folderInfoPath);
            CollectionAssert.Contains(surface.TextFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(randomDirectory));
            Assert.IsTrue(surface.DirectoryEntries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(randomDirectory)));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_UsesScanSurfaceFolderInfoForParentRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            Directory.CreateDirectory(tableDirectory);
            string folderInfoPath = Path.Combine(tableDirectory, "folderinfo.txt");
            string lr2FolderPath = Path.Combine(tableDirectory, "external.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(folderInfoPath, "#TITLE Surface Table", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(lr2FolderPath, "#TITLE External Folder", Encoding.GetEncoding("shift_jis"));
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, timestamp)
                },
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
                },
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "test_lr2folder_file_diff_folderinfo");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder parentRow = verify.Table<LR2SongDB.folder>().ToList().Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual("Surface Table", parentRow.title);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(row => row.path == lr2FolderPath);
            Assert.AreEqual("External Folder", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
            CollectionAssert.Contains(fileCheckResult.Lr2ScanTextFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(rootDirectory));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_InitializePrunesExternalRowsAndProtectsManagedOutputRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            BMSPlaylist.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(managedDirectory);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            string currentPath = Path.Combine(tableDirectory, "current.lr2folder");
            string stalePath = Path.Combine(tableDirectory, "stale.lr2folder");
            string managedCurrentPath = Path.Combine(managedDirectory, "managed.lr2folder");
            string managedStalePath = Path.Combine(managedDirectory, "stale.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(currentPath, "#TITLE Current", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(managedCurrentPath, "#TITLE Managed Physical", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(currentPath, timestamp);
            File.SetLastWriteTimeUtc(managedCurrentPath, timestamp);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9101,
                    name = "ManagedTable",
                    symbol = "M",
                    Output_dir = "ManagedTable"
                }, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    title = "Stale",
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = managedCurrentPath,
                    title = "Managed Existing",
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = managedStalePath,
                    title = "Managed Stale",
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [currentPath, managedCurrentPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [currentPath] = new RootFileEnumerationEntry(currentPath, timestamp),
                    [managedCurrentPath] = new RootFileEnumerationEntry(managedCurrentPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "initialize");

            CollectionAssert.AreEqual(new[] { currentPath }, fileCheckResult.Lr2ScanLr2FolderFilePaths.ToArray());
            Assert.IsTrue(fileCheckResult.Lr2ScanLr2FolderCandidatesAlreadyFiltered);
            Assert.AreEqual(1, fileCheckResult.Lr2ScanLr2FolderAppManagedFilteredCount);
            Assert.IsFalse(fileCheckResult.Lr2ScanLr2FolderFileEntries.ContainsKey(managedCurrentPath));

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == currentPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            LR2SongDB.folder managedCurrent = verify.Table<LR2SongDB.folder>().Single(row => row.path == managedCurrentPath);
            Assert.AreEqual("Managed Existing", managedCurrent.title);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == managedStalePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [DataTestMethod]
    [DataRow("reload_file_diff")]
    [DataRow("full_reinitialize")]
    public void ApplyLr2FolderFileDiffSync_ExplicitFileDiffPrunesRowsInScope(string reason)
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            Directory.CreateDirectory(tableDirectory);
            string currentPath = Path.Combine(tableDirectory, "current.lr2folder");
            string stalePath = Path.Combine(tableDirectory, "stale.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(currentPath, "#TITLE Current", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(currentPath, timestamp);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    title = "Stale",
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [currentPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [currentPath] = new RootFileEnumerationEntry(currentPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, reason);

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == currentPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_UsesScopedTextMetadataForBuiltinFolderInfoOutsideBmsRoot()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(builtinRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            Settings.Default.LR2RootPath = lr2Root;
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            string lr2FolderPath = Path.Combine(randomDirectory, "random.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(folderInfoPath, "#TITLE Builtin Random", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(lr2FolderPath, "#TITLE Random Folder", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(folderInfoPath, timestamp);
            File.SetLastWriteTimeUtc(lr2FolderPath, timestamp);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, builtinRoot],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "test_lr2folder_file_diff_builtin_folderinfo");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder category = verify.Table<LR2SongDB.folder>().ToList().Single(row => row.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual("Builtin Random", category.title);
            CollectionAssert.Contains(fileCheckResult.Lr2ScanFolderInfoFilePaths.ToList(), folderInfoPath);
            CollectionAssert.Contains(fileCheckResult.Lr2ScanTextFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(randomDirectory));
            Assert.IsTrue(fileCheckResult.Lr2ScanDirectoryEntries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(randomDirectory)));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncLr2BuiltinCustomFolderRows_PrunesRelativeBuiltinRowsWhenSourceDirectoryMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            Settings.Default.LR2RootPath = Path.Combine(scope.DirectoryPath, "MissingLR2");
            const string staleBuiltinPath = @"LR2files\CustomFolder\favorite.lr2folder";
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = staleBuiltinPath,
                    title = "Favorite",
                    parent = Lr2SongFolderParentNormalizer.RootParentHash,
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new BMSLibrary(scope.SongDbPath);

            library.SyncLr2BuiltinCustomFolderRows("test_builtin_missing_source");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsNull(verify.Table<LR2SongDB.folder>().ToList().SingleOrDefault(row => row.path == staleBuiltinPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryBeginLr2FullGenerationSyncRequest_DoesNotAdvanceVersionWhenAlreadyRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            var library = new BMSLibrary(scope.SongDbPath);

            Assert.IsTrue(InvokeTryBeginLr2FullGenerationSyncRequest(library, out int firstVersion));
            Assert.AreEqual(1, firstVersion);
            Assert.IsFalse(InvokeTryBeginLr2FullGenerationSyncRequest(library, out int secondVersion));
            Assert.AreEqual(firstVersion, secondVersion);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.IsTrue(library.Lr2FullGenerationSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void IsLr2FullGenerationSyncInputCurrent_UsesSnapshotSurfaceAndDetectsRootChange()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            object input = InvokeCreateLr2FullGenerationSyncInput(library);

            Assert.IsTrue(InvokeIsLr2FullGenerationSyncInputCurrent(library, input));
            File.WriteAllText(Path.Combine(rootDirectory, "folderinfo.txt"), "#TITLE Root Title");
            File.WriteAllText(Path.Combine(rootDirectory, "custom.lr2folder"), "#TITLE Custom");
            File.WriteAllText(Path.Combine(rootDirectory, "readme.txt"), "text group");
            Assert.IsTrue(InvokeIsLr2FullGenerationSyncInputCurrent(library, input));

            string otherRootDirectory = Path.Combine(scope.DirectoryPath, "OtherBMS");
            Directory.CreateDirectory(otherRootDirectory);
            library.SearchTargets = [otherRootDirectory];

            Assert.IsFalse(InvokeIsLr2FullGenerationSyncInputCurrent(library, input));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void IsLr2FullGenerationSyncInputCurrent_DetectsNewerScanSurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string chartDirectory = Path.Combine(rootDirectory, "Pack");
            Directory.CreateDirectory(chartDirectory);
            string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, new DateTime(2026, 6, 5, 8, 0, 0, DateTimeKind.Utc))
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });
            object input = InvokeCreateLr2FullGenerationSyncInput(library);

            Assert.IsTrue(InvokeIsLr2FullGenerationSyncInputCurrent(library, input));

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, new DateTime(2026, 6, 5, 8, 1, 0, DateTimeKind.Utc))
                },
                Lr2ScanTextFileDirectories = [chartDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            Assert.IsFalse(InvokeIsLr2FullGenerationSyncInputCurrent(library, input));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CaptureLr2FullGenerationScanSurface_SkipsWhenLr2FolderSurfaceMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });
            object capturedInput = InvokeCreateLr2FullGenerationSyncInput(library);
            Assert.IsTrue(GetInputInt(capturedInput, "ScanSurfaceGeneration") > 0);

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanTextFileDirectories = []
            });

            object input = InvokeCreateLr2FullGenerationSyncInput(library);

            Assert.AreEqual(0, GetInputInt(input, "ScanSurfaceGeneration"));
            Assert.IsFalse(InvokeIsLr2FullGenerationSyncInputCurrent(library, capturedInput));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CaptureLr2FullGenerationScanSurface_PreservesPreviousSurfaceWhenNormalFolderSyncUnapplied()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string folderInfoPath = Path.Combine(rootDirectory, "folderinfo.txt");
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            DateTime previousTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime failedTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, previousTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });
            object previousInput = InvokeCreateLr2FullGenerationSyncInput(library);
            int previousGeneration = GetInputInt(previousInput, "ScanSurfaceGeneration");

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2NormalFolderSyncFailed = true,
                Lr2NormalFolderSyncFailureReason = "write failed",
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, failedTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            object inputAfterFailure = InvokeCreateLr2FullGenerationSyncInput(library);
            Assert.AreEqual(previousGeneration, GetInputInt(inputAfterFailure, "ScanSurfaceGeneration"));
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(inputAfterFailure, "FolderInfoFileEntries");
            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out RootFileEnumerationEntry entry));
            Assert.AreEqual(previousTimestamp, entry.LastWriteTimeUtc);

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2NormalFolderSkippedMissingMetadataCount = 1,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, failedTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            object inputAfterMissingMetadata = InvokeCreateLr2FullGenerationSyncInput(library);
            Assert.AreEqual(previousGeneration, GetInputInt(inputAfterMissingMetadata, "ScanSurfaceGeneration"));
            entries = GetInputEntryMap(inputAfterMissingMetadata, "FolderInfoFileEntries");
            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out entry));
            Assert.AreEqual(previousTimestamp, entry.LastWriteTimeUtc);

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2NormalFolderInfoReadFailureCount = 1,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, failedTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            object inputAfterFolderInfoReadFailure = InvokeCreateLr2FullGenerationSyncInput(library);
            Assert.AreEqual(previousGeneration, GetInputInt(inputAfterFolderInfoReadFailure, "ScanSurfaceGeneration"));
            entries = GetInputEntryMap(inputAfterFolderInfoReadFailure, "FolderInfoFileEntries");
            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out entry));
            Assert.AreEqual(previousTimestamp, entry.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_MergesPreparedLr2FolderSurfaceAfterPreparedOutput()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            string staleLr2FolderPath = Path.Combine(outputBase, "stale.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            string folderInfoPath = Path.Combine(rootDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Surface Root", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, new DateTime(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc))
                },
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [staleLr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [staleLr2FolderPath] = new RootFileEnumerationEntry(staleLr2FolderPath, new DateTime(2026, 6, 7, 0, 30, 0, DateTimeKind.Utc))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_lr2folder_surface",
                () =>
                {
                    Directory.CreateDirectory(outputBase);
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                }));
            int appliedScanSurfaceGeneration = GetPrivateIntField(
                library,
                "lr2FullGenerationPreparedDataSurfaceAppliedScanGeneration");
            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            Assert.IsTrue(appliedScanSurfaceGeneration > 0);
            Assert.AreEqual(appliedScanSurfaceGeneration, GetInputInt(input, "ScanSurfaceGeneration"));
            Assert.AreEqual(
                0,
                GetPrivateIntField(library, "lr2FullGenerationPreparedDataSurfaceAppliedScanGeneration"));
            CollectionAssert.Contains(GetInputStringList(input, "FolderInfoFilePaths").ToList(), folderInfoPath);
            CollectionAssert.Contains(GetInputStringList(input, "TextFileDirectories").ToList(), rootDirectory);
            CollectionAssert.Contains(GetInputStringList(input, "Lr2FolderDiscoveryDirectories").ToList(), outputBase);
            CollectionAssert.Contains(GetInputStringList(input, "Lr2FolderFilePaths").ToList(), lr2FolderPath);
            CollectionAssert.DoesNotContain(GetInputStringList(input, "Lr2FolderFilePaths").ToList(), staleLr2FolderPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));

            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync(
                "test_prepare_lr2folder_surface",
                force: true,
                prepareGeneratedData: () =>
                {
                    Directory.CreateDirectory(outputBase);
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                });

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(row => row.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Prepared Folder", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBase), lr2Folder.parent);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == lr2FolderPath && row.title == "stale"));
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2FullGenerationSyncInput_ReusesScanSurfaceDirectoryEntries()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            DateTime surfaceTimestamp = new(2026, 6, 7, 4, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, surfaceTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });
            Directory.SetLastWriteTimeUtc(rootDirectory, liveTimestamp);

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "DirectoryEntries");

            string rootKey = Lr2FolderPath.NormalizeDirectoryPath(rootDirectory);
            Assert.IsTrue(entries.TryGetValue(rootKey, out RootFileEnumerationEntry entry));
            Assert.AreEqual(surfaceTimestamp, entry.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2FullGenerationSyncInput_ReusesScanSurfaceDirectoryEntriesForLr2FolderParents()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "Table");
            string lr2FolderPath = Path.Combine(tableDirectory, "select.lr2folder");
            Directory.CreateDirectory(tableDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Table", Encoding.GetEncoding("shift_jis"));
            DateTime surfaceTimestamp = new(2026, 6, 7, 4, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [tableDirectory] = new RootFileEnumerationEntry(tableDirectory, surfaceTimestamp)
                },
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, surfaceTimestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });
            Directory.SetLastWriteTimeUtc(tableDirectory, liveTimestamp);

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "DirectoryEntries");

            string tableKey = Lr2FolderPath.NormalizeDirectoryPath(tableDirectory);
            Assert.IsTrue(entries.TryGetValue(tableKey, out RootFileEnumerationEntry entry));
            Assert.AreEqual(surfaceTimestamp, entry.LastWriteTimeUtc);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2FullGenerationDataPreparation_UsesPreparedSurfaceWithoutCapturedScanSurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsFalse(InvokeHasLr2FullGenerationPreparedDataSurface(library));
            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_lr2folder_without_scan_surface",
                () =>
                {
                    Directory.CreateDirectory(outputBase);
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                }));
            Assert.IsTrue(InvokeHasLr2FullGenerationPreparedDataSurface(library));
            Assert.AreEqual(
                0,
                GetPrivateIntField(library, "lr2FullGenerationPreparedDataSurfaceAppliedScanGeneration"));

            object input = InvokeCreateLr2FullGenerationSyncInput(library);

            Assert.AreEqual(0, GetInputInt(input, "ScanSurfaceGeneration"));
            CollectionAssert.Contains(GetInputStringList(input, "Lr2FolderDiscoveryDirectories").ToList(), outputBase);
            CollectionAssert.Contains(GetInputStringList(input, "Lr2FolderFilePaths").ToList(), lr2FolderPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
            Assert.IsFalse(InvokeHasLr2FullGenerationPreparedDataSurface(library));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2FullGenerationDataPreparation_UsesPreparedDirectoryEntries()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputBase);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            DateTime preparedTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = preparedTimestamp.AddHours(2);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_lr2folder_directory_entries",
                () => CreatePreparedLr2FolderSurface(
                    outputBase,
                    lr2FolderPath,
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [outputBase] = new RootFileEnumerationEntry(outputBase, preparedTimestamp)
                    })));
            Directory.SetLastWriteTimeUtc(outputBase, liveTimestamp);

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "DirectoryEntries");

            string outputKey = Lr2FolderPath.NormalizeDirectoryPath(outputBase);
            Assert.IsTrue(entries.TryGetValue(outputKey, out RootFileEnumerationEntry entry));
            Assert.AreEqual(preparedTimestamp, entry.LastWriteTimeUtc);
            Assert.IsFalse(InvokeHasLr2FullGenerationPreparedDataSurface(library));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2FullGenerationDataPreparation_KeepsMetadataOnlySurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            DateTime preparedTimestamp = new(2026, 6, 8, 3, 0, 0, DateTimeKind.Utc);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_metadata_only",
                () => new Lr2FullGenerationPreparedDataSurface(
                    [],
                    [],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, preparedTimestamp)
                    },
                    [],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                    [],
                    discoveryComplete: true)));
            Assert.IsTrue(InvokeHasLr2FullGenerationPreparedDataSurface(library));

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "DirectoryEntries");

            string rootKey = Lr2FolderPath.NormalizeDirectoryPath(rootDirectory);
            Assert.IsTrue(entries.TryGetValue(rootKey, out RootFileEnumerationEntry entry));
            Assert.AreEqual(preparedTimestamp, entry.LastWriteTimeUtc);
            Assert.IsFalse(InvokeHasLr2FullGenerationPreparedDataSurface(library));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PreparedDataSurfaceMerge_KeepsMetadataSurfaces()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string playlistDirectory = Path.Combine(scope.DirectoryPath, "Playlist");
        string builtinDirectory = Path.Combine(scope.DirectoryPath, "LR2files", "CustomFolder", "RANDOM");
        Directory.CreateDirectory(playlistDirectory);
        Directory.CreateDirectory(builtinDirectory);
        string playlistLr2FolderPath = Path.Combine(playlistDirectory, "0000.lr2folder");
        string builtinFolderInfoPath = Path.Combine(builtinDirectory, "folderinfo.txt");
        File.WriteAllText(playlistLr2FolderPath, "#TITLE Playlist", Encoding.GetEncoding("shift_jis"));
        File.WriteAllText(builtinFolderInfoPath, "#TITLE Random", Encoding.GetEncoding("shift_jis"));
        DateTime playlistTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
        DateTime builtinTimestamp = new(2026, 6, 8, 2, 0, 0, DateTimeKind.Utc);

        Lr2FullGenerationPreparedDataSurface merged = Lr2FullGenerationPreparedDataSurface.Merge(
            CreatePreparedLr2FolderSurface(
                playlistDirectory,
                playlistLr2FolderPath,
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [playlistDirectory] = new RootFileEnumerationEntry(playlistDirectory, playlistTimestamp)
                }),
            new Lr2FullGenerationPreparedDataSurface(
                [builtinDirectory],
                [],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [builtinDirectory] = new RootFileEnumerationEntry(builtinDirectory, builtinTimestamp)
                },
                [builtinFolderInfoPath],
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [builtinFolderInfoPath] = new RootFileEnumerationEntry(builtinFolderInfoPath, builtinTimestamp)
                },
                [builtinDirectory],
                discoveryComplete: true));

        CollectionAssert.Contains(merged.Lr2FolderFilePaths.ToList(), playlistLr2FolderPath);
        Assert.IsTrue(merged.DirectoryEntries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(playlistDirectory)));
        Assert.IsTrue(merged.DirectoryEntries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(builtinDirectory)));
        CollectionAssert.Contains(merged.FolderInfoFilePaths.ToList(), builtinFolderInfoPath);
        CollectionAssert.Contains(merged.TextFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(builtinDirectory));
    }

    [TestMethod]
    public void CreateLr2TextMetadataSourceDirectoriesOutsideRoots_ExcludesOverlappingDiscoveryRoots()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string parentDirectory = Path.Combine(scope.DirectoryPath, "Library");
        string rootDirectory = Path.Combine(parentDirectory, "BMS");
        string childDirectory = Path.Combine(rootDirectory, "Nested");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "LR2files", "CustomFolder");
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateLr2TextMetadataSourceDirectoriesOutsideRoots", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);

        var result = ((IEnumerable<string>)methodInfo.Invoke(
            null,
            new object[] { new[] { parentDirectory, rootDirectory, childDirectory, outsideDirectory }, new[] { rootDirectory } })).ToList();

        CollectionAssert.AreEqual(
            new[] { Lr2FolderPath.NormalizeDirectoryPath(outsideDirectory) },
            result.ToArray());
    }

    [TestMethod]
    public void TryRunLr2FullGenerationDataPreparation_OverlaysPreparedFolderInfoSurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            string folderInfoPath = Path.Combine(outputBase, "folderinfo.txt");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputBase);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(folderInfoPath, "#TITLE Prepared Info", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            DateTime oldTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime preparedTimestamp = oldTimestamp.AddHours(1);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputBase),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [folderInfoPath],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, oldTimestamp)
                },
                Lr2ScanTextFileDirectories = [],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, oldTimestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_folderinfo_surface",
                () => CreatePreparedLr2FolderSurface(
                    outputBase,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(outputBase),
                    [folderInfoPath],
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [folderInfoPath] = new RootFileEnumerationEntry(folderInfoPath, preparedTimestamp)
                    },
                    [outputBase])));

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "FolderInfoFileEntries");
            IReadOnlyCollection<string> textFileDirectories = GetInputStringList(input, "TextFileDirectories");

            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out RootFileEnumerationEntry entry));
            Assert.AreEqual(preparedTimestamp, entry.LastWriteTimeUtc);
            CollectionAssert.Contains(textFileDirectories.ToList(), Lr2FolderPath.NormalizeDirectoryPath(outputBase));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2FullGenerationSyncInput_ReplacesOnlyPreparedScopeTextFileDirectories()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string preparedDirectory = Path.Combine(outputBase, "Table");
            string preparedPrefixSibling = Path.Combine(outputBase, "TableOther");
            string stalePreparedChildDirectory = Path.Combine(preparedDirectory, "OldText");
            string lr2FolderPath = Path.Combine(preparedDirectory, "0000.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(preparedDirectory);
            Directory.CreateDirectory(preparedPrefixSibling);
            Directory.CreateDirectory(stalePreparedChildDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, preparedDirectory, preparedPrefixSibling, stalePreparedChildDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory, stalePreparedChildDirectory, preparedPrefixSibling],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [Path.Combine(preparedDirectory, "stale.lr2folder")],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_text_dirs_scope_boundary",
                () => CreatePreparedLr2FolderSurface(
                    preparedDirectory,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(preparedDirectory),
                    textFileDirectories: [preparedDirectory])));

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            List<string> textFileDirectories = GetInputStringList(input, "TextFileDirectories").ToList();

            CollectionAssert.Contains(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(rootDirectory));
            CollectionAssert.Contains(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(preparedDirectory));
            CollectionAssert.Contains(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(preparedPrefixSibling));
            CollectionAssert.DoesNotContain(textFileDirectories, Lr2FolderPath.NormalizeDirectoryPath(stalePreparedChildDirectory));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2FullGenerationDataPreparation_DoesNotPromoteOldScanSurfaceWhenLr2FolderRootsChanged()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string oldOutputBase = Path.Combine(scope.DirectoryPath, "OldOutput");
            string newOutputBase = Path.Combine(scope.DirectoryPath, "NewOutput");
            string oldLr2FolderPath = Path.Combine(oldOutputBase, "old.lr2folder");
            string newLr2FolderPath = Path.Combine(newOutputBase, "new.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(oldOutputBase);
            Directory.CreateDirectory(newOutputBase);
            Settings.Default.LR2CustomFolderOutputBaseDir = oldOutputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, oldOutputBase],
                Lr2ScanLr2FolderFilePaths = [oldLr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [oldLr2FolderPath] = new RootFileEnumerationEntry(oldLr2FolderPath, new DateTime(2026, 6, 7, 0, 30, 0, DateTimeKind.Utc))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            Settings.Default.LR2CustomFolderOutputBaseDir = newOutputBase;
            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_lr2folder_roots_changed",
                () =>
                {
                    Directory.CreateDirectory(newOutputBase);
                    File.WriteAllText(newLr2FolderPath, "#TITLE New Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(newOutputBase, newLr2FolderPath);
                }));
            Assert.AreEqual(
                0,
                GetPrivateIntField(library, "lr2FullGenerationPreparedDataSurfaceAppliedScanGeneration"));

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            Assert.AreEqual(0, GetInputInt(input, "ScanSurfaceGeneration"));
            CollectionAssert.Contains(lr2FolderFilePaths, newLr2FolderPath);
            CollectionAssert.DoesNotContain(lr2FolderFilePaths, oldLr2FolderPath);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2FullGenerationSyncInput_KeepsExternalLr2FolderInOutputBaseOutsidePreparedScope()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "Output");
            string appOutputDir = Path.Combine(outputBase, "Table");
            string preparedLr2FolderPath = Path.Combine(appOutputDir, "0000.lr2folder");
            string externalLr2FolderPath = Path.Combine(outputBase, "external.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(appOutputDir);
            File.WriteAllText(externalLr2FolderPath, "#TITLE External Folder", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_keeps_external_output_base_lr2folder",
                () =>
                {
                    File.WriteAllText(preparedLr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(appOutputDir, preparedLr2FolderPath);
                }));

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, preparedLr2FolderPath);
            CollectionAssert.Contains(lr2FolderFilePaths, externalLr2FolderPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CaptureLr2FullGenerationScanSurface_ExcludesManagedOutputCandidates()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            BMSPlaylist.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(managedDirectory, "0000.lr2folder");
            string externalOutputPath = Path.Combine(outputBase, "external.lr2folder");
            Directory.CreateDirectory(managedDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(externalOutputPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9102,
                    name = "ManagedTable",
                    symbol = "M",
                    Output_dir = "ManagedTable"
                }, typeof(LR2SongDBExtended.playlist));
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            InvokeCaptureLr2FullGenerationScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [managedPath, externalOutputPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [managedPath] = new RootFileEnumerationEntry(managedPath, timestamp),
                    [externalOutputPath] = new RootFileEnumerationEntry(externalOutputPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            CollectionAssert.DoesNotContain(lr2FolderFilePaths, managedPath);
            CollectionAssert.Contains(lr2FolderFilePaths, externalOutputPath);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_ClearsPreparedSurfaceWhenFollowupQueueIsCurrentNoop()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "Output");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputBase);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                EnableLR2SongDbFullGeneration = true
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2FullGenerationStatusService.MarkCompleted(
                    setup,
                    Lr2FullGenerationSignatureBuilder.Build(options),
                    runId: "already_current",
                    totalCount: 0,
                    nowUtc: DateTime.UtcNow);
            }

            Assert.IsTrue(library.TryRunLr2FullGenerationDataPreparation(
                "test_prepare_then_noop_queue",
                () =>
                {
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                }));
            Assert.IsTrue(InvokeHasLr2FullGenerationPreparedDataSurface(library));

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationDataSync(
                "test_prepare_then_noop_queue",
                force: false,
                allowIncompleteToQueue: false);

            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, snapshot.Status);
            Assert.IsFalse(InvokeHasLr2FullGenerationPreparedDataSurface(library));
            Assert.AreEqual(0, library.Lr2FullGenerationSyncRequestedVersion);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2FullGenerationSyncInputWithoutScanSurface_UsesTargetFolderInfoOnly()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string songDirectory = Path.Combine(packDirectory, "Song");
            string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
            Directory.CreateDirectory(songDirectory);
            Directory.CreateDirectory(unrelatedDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            string packFolderInfoPath = Path.Combine(packDirectory, "folderinfo.txt");
            string songTextPath = Path.Combine(songDirectory, "readme.txt");
            string unrelatedFolderInfoPath = Path.Combine(unrelatedDirectory, "folderinfo.txt");
            File.WriteAllText(chartPath, "#TITLE Test");
            File.WriteAllText(packFolderInfoPath, "#TITLE Pack", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(songTextPath, "notes", Encoding.UTF8);
            File.WriteAllText(unrelatedFolderInfoPath, "#TITLE Other", Encoding.GetEncoding("shift_jis"));
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles =
                [
                    new TestableBmsFile
                    {
                        path = chartPath
                    }
                ]
            };

            object input = InvokeCreateLr2FullGenerationSyncInput(library);
            List<string> folderInfoFilePaths = GetInputStringList(input, "FolderInfoFilePaths").ToList();
            List<string> textFileDirectories = GetInputStringList(input, "TextFileDirectories").ToList();

            CollectionAssert.Contains(folderInfoFilePaths, packFolderInfoPath);
            CollectionAssert.DoesNotContain(folderInfoFilePaths, unrelatedFolderInfoPath);
            CollectionAssert.Contains(textFileDirectories, songDirectory);
            CollectionAssert.DoesNotContain(textFileDirectories, unrelatedDirectory);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_RunsFullGenerationSyncAndMarksCompletedWhenClean()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string songDirectory = Path.Combine(packDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            File.WriteAllText(Path.Combine(packDirectory, "folderinfo.txt"), "#TITLE Pack Title");
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Parsed Title\r\n#ARTIST Parsed Artist\r\n#BPM 120\r\n#PLAYLEVEL 4\r\n#DIFFICULTY 2\r\n#RANK 3\r\n#00111:01\r\n");
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            File.WriteAllText(Path.Combine(songDirectory, "readme.txt"), "text group");
            string customFolderPath = Path.Combine(rootDirectory, "custom.lr2folder");
            File.WriteAllText(customFolderPath, "#TITLE Custom Folder");

            var library = new BMSLibrary(scope.SongDbPath);
            library.SearchTargets = [rootDirectory];
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(chartSnapshot.Md5);
            file.ApplySha256(chartSnapshot.Sha256);
            file.folder = "00000000";
            file.parent = "11111111";
            library.BMSFiles = [file];

            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new TestableBmsFile
                {
                    path = chartPath,
                    adddate = 98765,
                    tag = "keep-tag"
                }.WithHashAndFavorite("cccccccccccccccccccccccccccccccc", 3), typeof(LR2SongDB.song));
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                setup.InsertOrReplace(new LR2SongDBExtended.chart_info
                {
                    sha256 = chartSnapshot.Sha256,
                    md5 = chartSnapshot.Md5,
                    level = 9,
                    difficulty = 3,
                    maxbpm = 180.7,
                    minbpm = 120.4,
                    mode = 7,
                    feature = 4 | 8,
                    notes = 1234,
                    parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion
                }, typeof(LR2SongDBExtended.chart_info));
                string stalePath = ToFolderPath(Path.Combine(rootDirectory, "Removed"));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1,
                    date = 1
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = customFolderPath,
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.Combine(rootDirectory, "stale.lr2folder"),
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }

            string queuedName = string.Empty;
            string queuedReason = string.Empty;
            var observedStages = new List<string>();
            library.PropertyChanged += (sender, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(BMSLibrary.Lr2FullGenerationSyncStage), StringComparison.Ordinal))
                {
                    observedStages.Add(library.Lr2FullGenerationSyncStage);
                }
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                queuedName = name;
                queuedReason = reason;
                work().GetAwaiter().GetResult();
                return true;
            };

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationDataSync("test_enabled");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, snapshot.Status);
            Assert.AreEqual("lr2_full_generation_sync", queuedName);
            Assert.AreEqual("test_enabled", queuedReason);
            CollectionAssert.Contains(observedStages, "chart_info_hydration");
            CollectionAssert.Contains(observedStages, "input_surface");
            CollectionAssert.Contains(observedStages, "compatibility_projection_index");
            CollectionAssert.Contains(observedStages, "chart_info_resolver_snapshot");
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, row.stage);
            Assert.AreEqual(row.total_count, row.processed_cursor);
            Assert.IsTrue(row.total_count > 0);
            Assert.IsNotNull(row.completed_at);

            string expectedRootFolderPath = ToFolderPath(rootDirectory);
            string expectedPackFolderPath = ToFolderPath(packDirectory);
            string expectedSongFolderPath = ToFolderPath(songDirectory);
            string expectedRemovedFolderPath = ToFolderPath(Path.Combine(rootDirectory, "Removed"));
            string expectedCustomFolderPath = customFolderPath;
            string expectedStaleLr2FolderPath = Path.Combine(rootDirectory, "stale.lr2folder");
            var folderRows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder root = folderRows.Single(folder => folder.path == expectedRootFolderPath);
            LR2SongDB.folder pack = folderRows.Single(folder => folder.path == expectedPackFolderPath);
            LR2SongDB.folder song = folderRows.Single(folder => folder.path == expectedSongFolderPath);
            LR2SongDB.folder custom = folderRows.Single(folder => folder.path == expectedCustomFolderPath);
            Assert.AreEqual(1, root.type);
            Assert.AreEqual("Pack Title", pack.title);
            Assert.AreEqual("Song", song.title);
            Assert.AreEqual(2, custom.type);
            Assert.AreEqual("Custom Folder", custom.title);
            Assert.AreEqual(0, folderRows.Count(folder => folder.path == expectedRemovedFolderPath));
            Assert.AreEqual(0, folderRows.Count(folder => folder.path == expectedStaleLr2FolderPath));
            Assert.AreEqual(1, folderRows.Count(folder => folder.path == expectedCustomFolderPath));
            string expectedSongFolderHash = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory);
            Assert.AreEqual(expectedSongFolderHash, verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", chartPath));
            Assert.IsFalse(string.IsNullOrWhiteSpace(verify.ExecuteScalar<string>("SELECT parent FROM song WHERE path = ?;", chartPath)));
            Assert.AreEqual("Parsed Title", verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("Parsed Artist", verify.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(file.hash, verify.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(9, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT difficulty FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(180, verify.ExecuteScalar<int>("SELECT maxbpm FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(120, verify.ExecuteScalar<int>("SELECT minbpm FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(5, verify.ExecuteScalar<int>("SELECT mode FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT random FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT longnote FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1234, verify.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(98765, verify.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("keep-tag", verify.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("00000000", file.folder);
            Assert.AreEqual("11111111", file.parent);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncCompletedVersion);
            Assert.IsFalse(library.Lr2FullGenerationSyncRunning);
            Assert.AreEqual(row.total_count.GetValueOrDefault(), library.Lr2FullGenerationSyncTotalCount);
            Assert.AreEqual(row.processed_cursor.GetValueOrDefault(), library.Lr2FullGenerationSyncProcessedCount);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, library.Lr2FullGenerationSyncStage);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, library.GetLr2FullGenerationStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_PreflightCancelMarksDurableCancelledStatus()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            bool cancelRequested = false;
            library.PropertyChanged += (sender, args) =>
            {
                if (!cancelRequested
                    && string.Equals(args.PropertyName, nameof(BMSLibrary.Lr2FullGenerationSyncStage), StringComparison.Ordinal)
                    && string.Equals(library.Lr2FullGenerationSyncStage, "chart_info_hydration", StringComparison.Ordinal))
                {
                    cancelRequested = library.CancelLr2FullGenerationSync("test_preflight_cancel");
                }
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_preflight_cancel", force: true);

            Assert.IsTrue(cancelRequested);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Cancelled", row.status);
            Assert.AreEqual("chart_info_hydration", row.stage);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_ServiceCancelKeepsDurableProgress()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Cancel In Service\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = [file]
            };
            bool cancelRequested = false;
            library.PropertyChanged += (sender, args) =>
            {
                if (!cancelRequested
                    && string.Equals(args.PropertyName, nameof(BMSLibrary.Lr2FullGenerationSyncStage), StringComparison.Ordinal)
                    && string.Equals(library.Lr2FullGenerationSyncStage, "normal_folders", StringComparison.Ordinal))
                {
                    cancelRequested = library.CancelLr2FullGenerationSync("test_service_cancel");
                }
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_service_cancel", force: true);

            Assert.IsTrue(cancelRequested);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Cancelled", row.status);
            Assert.AreEqual("normal_folders", row.stage);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.IsTrue(row.total_count.GetValueOrDefault() > 0);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DefaultsDifficultyWithoutUsingStaleRowsAsAnchor()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Undefined Difficulty\r\n#PLAYLEVEL 1\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = [file]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                setup.Execute(
                    "INSERT INTO song (path, folder, mode, karinotes, difficulty) VALUES (?, ?, ?, ?, ?);",
                    Path.Combine(songDirectory, "stale.bms"),
                    Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory),
                    5,
                    0,
                    4);
            }
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_difficulty_normalization");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path LIKE '%stale.bms';"));
            Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT difficulty FROM song WHERE path = ?;", chartPath));
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DoesNotQueueSecondSyncWhenCompletedStatusIsCurrent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Noop Sync\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(chartSnapshot.Md5);
            file.ApplySha256(chartSnapshot.Sha256);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = [file]
            };
            int scheduledCount = 0;
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                scheduledCount++;
                work().GetAwaiter().GetResult();
                return true;
            };

            Lr2FullGenerationStatusSnapshot first = library.QueueLr2FullGenerationDataSync("test_first");
            int requestedVersionAfterFirst = library.Lr2FullGenerationSyncRequestedVersion;
            int completedVersionAfterFirst = library.Lr2FullGenerationSyncCompletedVersion;
            int statusVersionAfterFirst = library.Lr2FullGenerationStatusVersion;
            Lr2FullGenerationStatusSnapshot second = library.QueueLr2FullGenerationDataSync("test_second");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, first.Status);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, second.Status);
            Assert.AreEqual(1, scheduledCount);
            Assert.AreEqual(requestedVersionAfterFirst, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.AreEqual(completedVersionAfterFirst, library.Lr2FullGenerationSyncCompletedVersion);
            Assert.AreEqual(statusVersionAfterFirst + 1, library.Lr2FullGenerationStatusVersion);
            Assert.IsFalse(library.Lr2FullGenerationSyncRunning);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DoesNotQueueWhenCompletedCopiedSongDbIsCurrent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Copied Noop Sync\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var firstLibrary = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = [file]
            };
            int firstScheduledCount = 0;
            firstLibrary.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                firstScheduledCount++;
                work().GetAwaiter().GetResult();
                return true;
            };

            Lr2FullGenerationStatusSnapshot first = firstLibrary.QueueLr2FullGenerationDataSync("test_first_for_copy");
            string copiedDirectory = Path.Combine(scope.DirectoryPath, "Copied");
            Directory.CreateDirectory(copiedDirectory);
            string copiedSongDbPath = Path.Combine(copiedDirectory, "song.db");
            File.Copy(scope.SongDbPath, copiedSongDbPath, overwrite: true);
            var copiedLibrary = new BMSLibrary(copiedSongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = [file]
            };
            int copiedScheduledCount = 0;
            copiedLibrary.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                copiedScheduledCount++;
                work().GetAwaiter().GetResult();
                return true;
            };

            Lr2FullGenerationStatusSnapshot second = copiedLibrary.QueueLr2FullGenerationDataSync("test_copied_song_db");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, first.Status);
            Assert.AreEqual(1, firstScheduledCount);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, second.Status);
            Assert.AreEqual(0, copiedScheduledCount);
            Assert.AreEqual(0, copiedLibrary.Lr2FullGenerationSyncRequestedVersion);
            Assert.AreEqual(0, copiedLibrary.Lr2FullGenerationSyncCompletedVersion);
            using var verify = new LR2SongDBExtended(copiedSongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncService_PreservesUserColumnsWhenRunningOnCopiedSongDb()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE Copied User Columns\r\n#ARTIST Parsed Artist\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);

        using (var sourceDb = new LR2SongDBExtended(scope.SongDbPath))
        {
            sourceDb.CreateTable<LR2SongDB.song>();
            var existing = new TestableBmsFile
            {
                path = chartPath,
                adddate = 123456,
                tag = "copied-user-tag"
            }.WithHashAndFavorite("cccccccccccccccccccccccccccccccc", 5);
            existing.SetTitleForTest("Old Copied Title");
            sourceDb.InsertOrReplace(existing, typeof(LR2SongDB.song));
        }

        string copiedDirectory = Path.Combine(scope.DirectoryPath, "CopiedUserColumns");
        Directory.CreateDirectory(copiedDirectory);
        string copiedSongDbPath = Path.Combine(copiedDirectory, "song.db");
        File.Copy(scope.SongDbPath, copiedSongDbPath, overwrite: true);
        using var copiedDb = new LR2SongDBExtended(copiedSongDbPath);
        copiedDb.CreateTable<LR2SongDB.folder>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(copiedDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "copied-user-columns",
            RunId = "copied-user-columns-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual("Copied User Columns", copiedDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(chartSnapshot.Md5, copiedDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(5, copiedDb.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(123456, copiedDb.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("copied-user-tag", copiedDb.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_WithNoRootsCompletesEmptyGeneration()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_no_roots");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncRequestedVersion);
            Assert.AreEqual(1, library.Lr2FullGenerationSyncCompletedVersion);
            Assert.AreEqual(0, library.Lr2FullGenerationSyncFailedVersion);
            Assert.IsFalse(library.Lr2FullGenerationSyncRunning);
            Assert.AreEqual(string.Empty, library.Lr2FullGenerationSyncFailureMessage);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, library.GetLr2FullGenerationStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_WithNoRootsStillSyncsSongRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "Loose");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE test");
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash("dddddddddddddddddddddddddddddddd");
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_no_roots_with_song");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(1, row.processed_cursor);
            Assert.AreEqual(1, row.total_count);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory), verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_ParsesReadableSnapshotContentInsteadOfPreservingNullSongColumns()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "ReadableSnapshot");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            byte[] directiveBytes = Encoding.ASCII.GetBytes(
                "#TITLE Parsed Readable Snapshot\r\n#ARTIST Parsed Artist\r\n#PLAYLEVEL 7\r\n#RANK 3\r\n#00118:01\r\n");
            File.WriteAllBytes(chartPath, [.. directiveBytes, 0x82]);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                setup.Execute(
                    "INSERT INTO song (hash, path, level, difficulty, mode, judge) VALUES (?, ?, NULL, NULL, NULL, NULL);",
                    "dddddddddddddddddddddddddddddddd",
                    chartPath);
            }
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_readable_snapshot_song_parse");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual("Parsed Readable Snapshot", verify.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("Parsed Artist", verify.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(7, verify.ExecuteScalar<int>("SELECT COALESCE(level, -999) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT COALESCE(difficulty, -999) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(7, verify.ExecuteScalar<int>("SELECT COALESCE(mode, -999) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT COALESCE(judge, -999) FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SnapshotSongRowParser_AllowsUnknownEncodingToUseParserDefault()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("#TITLE unknown fallback\r\n");
        var snapshot = new ChartFileSnapshot(
            @"D:\BMS\Unknown\chart.bms",
            bytes,
            new DateTime(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc),
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('b', 64));
        var detectionResult = new BMSFile.BmsEncodingDetectionResult(
            "unknown",
            BMSFile.EncodingDetectionOutcome.Unknown,
            fastAscii: false,
            decodedText: null);

        BMSFile song = BMSFile.CreateBMSFileFromSnapshot(snapshot, detectionResult);

        Assert.AreEqual("unknown fallback", song.title);
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_BuildsMissingChartInfoInsideSongRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoBuild");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE generated chart info\r\n#ARTIST tester\r\n#BPM 150\r\n#PLAYLEVEL 12\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
            }
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_chart_info_build");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", snapshot.Sha256, snapshot.Md5));
            Assert.AreEqual(12, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            Assert.AreEqual(library.ChartInfoBackfillRequestedVersion, library.ChartInfoBackfillCompletedVersion);
            Assert.IsFalse(library.ChartInfoBackfillRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_RebuildsStaleChartInfoInsideSongRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "StaleChartInfoBuild");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE stale chart info\r\n#BPM 130\r\n#PLAYLEVEL 10\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
                setup.InsertOrReplace(CreateChartInfo(snapshot.Sha256, snapshot.Md5, level: 99, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1), typeof(LR2SongDBExtended.chart_info));
            }
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_stale_chart_info_build");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.chart_info chartInfo = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", snapshot.Sha256).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfo.parser_version);
            Assert.AreEqual(10, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncService_FallsBackToSongCopyWhenChartSnapshotCannotBeRead()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Missing");
        Directory.CreateDirectory(songDirectory);
        string missingChartPath = Path.Combine(songDirectory, "missing.bms");
        var file = new TestableBmsFile
        {
            path = missingChartPath
        };
        file.SetTitleForTest("Existing Title");
        file.SetArtistForTest("Existing Artist");
        file.SetHash("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        file.ApplySha256("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");
        file.SetTextGroupFlag(1);
        file.folder = "stale-folder";
        file.parent = "stale-parent";
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "missing-song",
            RunId = "missing-song",
            SongRows = [file],
            TextFileDirectories = [],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowParseFailureCount);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory), songDb.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("Existing Title", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("Existing Artist", songDb.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", missingChartPath));
        Assert.AreEqual("stale-folder", file.folder);
        Assert.AreEqual("stale-parent", file.parent);
    }

    [TestMethod]
    public void SyncService_DeletesUnknownRootFolderRowAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "OutsideRoot");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(outsideDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(outsideDirectory),
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "unknown-root-folder",
            RunId = "unknown-root-folder",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.UnknownRootFolderRowCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == ToFolderPath(outsideDirectory)));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_CompletesWhenCurrentSongRowIsOutsideRootAndKeepsDiagnostic()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "OutsideRoot");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(outsideDirectory);
        string chartPath = Path.Combine(outsideDirectory, "outside.bms");
        File.WriteAllText(chartPath, "#TITLE Outside Root\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "unknown-root-current-song",
            RunId = "unknown-root-current-song",
            RootDirectories = [rootDirectory],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.IsNull(result.IncompleteReason);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.UnknownRootSongRowCount);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(chartPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_DoesNotTreatNegativeSongDateAsMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "negative-date.bms");
            File.WriteAllText(chartPath, "#TITLE Negative Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
            file.date = -1;
            using var songDb = new LR2SongDBExtended(scope.SongDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDB.folder>();
            songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

            Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
            {
                Signature = "negative-song-date",
                RunId = "negative-song-date",
                RootDirectories = [rootDirectory],
                ChartPaths = [chartPath],
                SongRows = [file],
                StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
            });

            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
            Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateMissingSongRowCount);
            LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncService_DoesNotTreatNullSongDateAsMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "null-date.bms");
            File.WriteAllText(chartPath, "#TITLE Null Date\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
            file.date = null;
            using var songDb = new LR2SongDBExtended(scope.SongDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDB.folder>();
            songDb.InsertOrReplace(file.CreateSongRowPersistenceCopy(), typeof(LR2SongDB.song));

            Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
            {
                Signature = "null-song-date",
                RunId = "null-song-date",
                RootDirectories = [rootDirectory],
                ChartPaths = [chartPath],
                SongRows = [file],
                StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
            });

            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
            Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateMissingSongRowCount);
            LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncService_DeletesFolderDateMissingRowAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "broken.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 2,
            date = 0
        }, typeof(LR2SongDB.folder));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "folder-date-missing",
            RunId = "folder-date-missing",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateMissingFolderRowCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().Count(folder => folder.path == lr2FolderPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_RepairsFolderDateWhenStaleAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Table");
        DateTime rootTime = new(2026, 6, 5, 1, 0, 0, DateTimeKind.Utc);
        DateTime lr2FolderTime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(lr2FolderPath, lr2FolderTime);
        Directory.SetLastWriteTimeUtc(rootDirectory, rootTime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(rootDirectory),
            type = 1,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 2,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(lr2FolderTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));
        const string signature = "folder-date-stale";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "folder-date-stale-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = CreateFileEntryMap(lr2FolderPath),
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, rootTime)
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime), songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", ToFolderPath(rootDirectory)));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(lr2FolderTime), songDb.ExecuteScalar<int>("SELECT date FROM folder WHERE path = ?;", lr2FolderPath));
    }

    [TestMethod]
    public void SyncService_DeletesLegacyNormalFolderRowWhenNotExpected()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string legacyDirectory = Path.Combine(rootDirectory, "Legacy");
        Directory.CreateDirectory(legacyDirectory);
        DateTime legacyTime = new(2026, 6, 5, 2, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(legacyDirectory, legacyTime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(legacyDirectory),
            type = 0,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(legacyTime.AddMinutes(-1))
        }, typeof(LR2SongDB.folder));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "legacy-folder-date-stale",
            RunId = "legacy-folder-date-stale-run",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        string legacyFolderPath = ToFolderPath(legacyDirectory);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == legacyFolderPath));
    }

    [TestMethod]
    public void SyncService_DeletesMissingFolderTargetsAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string missingDirectory = Path.Combine(rootDirectory, "MissingPack");
        string missingLr2FolderPath = Path.Combine(rootDirectory, "missing.lr2folder");
        Directory.CreateDirectory(rootDirectory);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(missingDirectory),
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = missingLr2FolderPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));
        const string signature = "folder-target-missing";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "folder-target-missing-run",
            RootDirectories = [rootDirectory],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingLr2FolderPath],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        Assert.IsTrue(result.StartupScanDiagnosticResult.IsClean);
        string missingFolderPath = ToFolderPath(missingDirectory);
        string rootFolderPath = ToFolderPath(rootDirectory);
        List<LR2SongDB.folder> folderRows = songDb.Table<LR2SongDB.folder>().ToList();
        Assert.AreEqual(0, folderRows.Count(folder => folder.path == missingFolderPath));
        Assert.AreEqual(0, folderRows.Count(folder => folder.path == missingLr2FolderPath));
        Assert.AreEqual(1, folderRows.Count(folder => folder.path == rootFolderPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void CleanupStartupScanBlockerFolderRows_DeletesOnlyFolderBlockers()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "KnownRoot");
        string outsideDirectory = Path.Combine(scope.DirectoryPath, "OutsideRoot");
        string dateMissingPath = Path.Combine(rootDirectory, "date-missing.lr2folder");
        string missingTargetPath = Path.Combine(rootDirectory, "missing.lr2folder");
        string legacyMissingDirectory = Path.Combine(rootDirectory, "LegacyMissing");
        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(outsideDirectory);
        DateTime rootTime = new(2026, 6, 5, 3, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(rootDirectory, rootTime);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(rootDirectory),
            type = 1,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(rootTime)
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(outsideDirectory),
            type = 1,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = dateMissingPath,
            type = 2,
            date = 0
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = missingTargetPath,
            type = 2,
            date = 1
        }, typeof(LR2SongDB.folder));
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(legacyMissingDirectory),
            type = 0,
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2StartupScanBlockerCleanupResult result = Lr2FullGenerationSyncService.CleanupStartupScanBlockerFolderRows(
            songDb,
            [rootDirectory],
            [rootDirectory],
            [],
            lr2RootPath: null);

        Assert.AreEqual(4, result.DiagnosticBefore.CleanupFolderRowCount);
        Assert.AreEqual(4, result.DeletedFolderRowCount);
        Assert.IsTrue(result.DiagnosticAfter.IsClean);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual(ToFolderPath(rootDirectory), songDb.Table<LR2SongDB.folder>().Single().path);
    }

    [TestMethod]
    public void SyncService_LeavesIncompleteWhenSourceBecomesStaleBeforeCompletion()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "Root");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE source stale\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        string stalePath = Path.Combine(scope.DirectoryPath, "Stale", "stale.bms");
        songDb.InsertOrReplace(new TestableBmsFile
        {
            path = stalePath,
            date = 1
        }.WithHashAndFavorite("dddddddddddddddddddddddddddddddd", favoriteValue: null), typeof(LR2SongDB.song));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "source-stale",
            RunId = "source-stale",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            IsSourceCurrent = () => false
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.SourceStaleStage, result.FinalStage);
        Assert.AreEqual(Lr2FullGenerationSyncService.SourceStaleReason, result.IncompleteReason);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        Assert.AreEqual(Lr2FullGenerationSyncService.SourceStaleStage, row.stage);
        StringAssert.Contains(row.last_error, Lr2FullGenerationSyncService.SourceStaleReason);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(stalePath));
    }

    [TestMethod]
    public void SyncService_SkipsSongRowsWhenVerifierConfirmsCurrent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string chartPath = Path.Combine(scope.DirectoryPath, "Current", "chart.bms");
        var file = new TestableBmsFile
        {
            path = chartPath,
            date = 123456
        }.WithHashAndFavorite("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", favoriteValue: null);
        file.ApplySha256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        file.SetTitleForTest("already current");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        Assert.IsTrue(Lr2SongDbWriter.UpsertGeneratedSong(songDb, file));
        var logs = new List<string>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "skip-song-rows",
            RunId = "skip-song-rows",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc),
            SongRowsSkipVerifier = (_, rows) => new Lr2FullGenerationSongRowsSkipVerificationResult
            {
                CanSkip = true,
                Reason = "test_current",
                TargetRows = rows.Count,
                VerifiedRows = rows.Count
            },
            LogInstallPerformance = logs.Add
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowSkippedCount);
        Assert.AreEqual(result.TotalCount, result.ProcessedCount);
        Assert.IsTrue(logs.Any(log => log.Contains("lr2_full_generation_sync song_rows_skip action=skip")));
        Assert.IsFalse(logs.Any(log => log.Contains("pipeline_start stage=song_rows")));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(row.total_count, row.processed_cursor);
    }

    [TestMethod]
    public void SyncService_TransientSongRowSkipPathsSkipOnlyMatchingRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string skippedPath = Path.Combine(scope.DirectoryPath, "Skipped", "already-inserted.bms");
        string processDirectory = Path.Combine(scope.DirectoryPath, "Process");
        Directory.CreateDirectory(processDirectory);
        string processPath = Path.Combine(processDirectory, "process.bms");
        File.WriteAllText(processPath, "#TITLE processed transient remainder\r\n", Encoding.ASCII);
        ChartFileSnapshot processSnapshot = ChartFileContentReader.ReadSnapshot(processPath);
        var skippedFile = new TestableBmsFile
        {
            path = skippedPath,
            date = 123456
        }.WithHashAndFavorite("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", favoriteValue: null);
        skippedFile.ApplySha256("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        TestableBmsFile processFile = CreateSyncTestFile(processPath, processSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        var logs = new List<string>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "transient-song-row-skip",
            RunId = "transient-song-row-skip",
            SongRows = [skippedFile, processFile],
            TransientSongRowsSkipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                skippedPath
            },
            StartedAtUtc = new DateTime(2026, 6, 9, 0, 0, 0, DateTimeKind.Utc),
            LogInstallPerformance = logs.Add
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(2, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowSkippedCount);
        Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = ?;", skippedPath));
        Assert.AreEqual("processed transient remainder", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", processPath));
        Assert.IsTrue(logs.Any(log => log.Contains("pipeline_start stage=song_rows")
            && log.Contains("transientSkipPaths=1")));
        Assert.IsTrue(logs.Any(log => log.Contains("chunk_done stage=song_rows")
            && log.Contains("transientSkipped=1")));
        Assert.IsTrue(logs.Any(log => log.Contains("pipeline_done stage=song_rows")
            && log.Contains("transientSkipped=1")));
    }

    [TestMethod]
    public void SyncService_ResumesFromCompletedNormalFolderBoundary()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume song\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "resume-normal-complete";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 3,
            stage: "normal_folders_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        InsertNormalFolderRow(songDb, songDirectory, Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "resume-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.IsNull(result.NormalFolderSyncResult);
        Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual("resume song", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(row.total_count, row.processed_cursor);
    }

    [TestMethod]
    public void SyncService_ResyncsExpectedNormalFolderRowsAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMissingFolderRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume missing folder\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "resume-normal-missing-folder";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 3,
            stage: "normal_folders_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "resume-missing-folder-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedFolderRowCount);
        string rootFolderPath = ToFolderPath(rootDirectory);
        string songFolderPath = ToFolderPath(songDirectory);
        List<LR2SongDB.folder> folderRows = songDb.Table<LR2SongDB.folder>().ToList();
        Assert.AreEqual(1, folderRows.Count(folder => folder.path == rootFolderPath));
        Assert.AreEqual(1, folderRows.Count(folder => folder.path == songFolderPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_ResyncsExpectedLr2FolderRowAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMissingLr2FolderRoot");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE table\r\n", Encoding.GetEncoding("shift_jis"));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            type = 99,
            title = "unsupported type",
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(lr2FolderPath))
        }, typeof(LR2SongDB.folder));
        const string signature = "resume-lr2folder-missing-row";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "resume-missing-lr2folder-row-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = CreateFileEntryMap(lr2FolderPath),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        LR2SongDB.folder folderRow = songDb.Find<LR2SongDB.folder>(lr2FolderPath);
        Assert.IsNotNull(folderRow);
        Assert.AreEqual(2, folderRow.type);
        Assert.AreEqual("table", folderRow.title);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_SkipsExpectedLr2FolderRowWhenMetadataIsMissingAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeUnreadableLr2FolderRoot");
        Directory.CreateDirectory(rootDirectory);
        string missingLr2FolderPath = Path.Combine(rootDirectory, "missing.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        const string signature = "resume-lr2folder-missing-metadata";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 2,
            stage: "lr2folder_files_completed",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "resume-missing-metadata-lr2folder-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingLr2FolderPath],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_RestartsWhenResumeTotalCountDiffers()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMismatchRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume mismatch\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "resume-total-mismatch";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 2,
            totalCount: 99,
            stage: "normal_folders_completed",
            detail: "old total",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "restart-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.IsNotNull(result.NormalFolderSyncResult);
        Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any());
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(3, row.total_count);
    }

    [TestMethod]
    public void SyncService_ResumesInsideSongRowsFromDurableCursor()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeSongRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string firstPath = Path.Combine(songDirectory, "first.bms");
        string secondPath = Path.Combine(songDirectory, "second.bms");
        File.WriteAllText(firstPath, "#TITLE first updated\r\n");
        File.WriteAllText(secondPath, "#TITLE second updated\r\n");
        ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
        ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);
        TestableBmsFile firstFile = CreateSyncTestFile(firstPath, firstSnapshot);
        TestableBmsFile secondFile = CreateSyncTestFile(secondPath, secondSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        var existingFirstRow = new TestableBmsFile
        {
            path = firstPath,
            date = 1
        }.WithHashAndFavorite(firstFile.hash, favoriteValue: null);
        existingFirstRow.SetTitleForTest("first stale");
        songDb.InsertOrReplace(existingFirstRow, typeof(LR2SongDB.song));
        const string signature = "resume-song-row";
        Lr2FullGenerationStatusService.MarkIncomplete(
            songDb,
            signature,
            "previous-run",
            processedCursor: 3,
            totalCount: 4,
            stage: "song_rows",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        InsertNormalFolderRow(songDb, songDirectory, Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "resume-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual("first stale", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("second updated", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(4, row.processed_cursor);
    }

    [TestMethod]
    public void SyncService_PrunesStaleSongRowsAndMaintenance()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "Current");
        Directory.CreateDirectory(songDirectory);
        string currentPath = Path.Combine(songDirectory, "current.bms");
        File.WriteAllText(currentPath, "#TITLE current\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile currentFile = CreateSyncTestFile(currentPath, ChartFileContentReader.ReadSnapshot(currentPath));
        string stalePath = Path.Combine(scope.DirectoryPath, "OldRoot", "stale.bms");
        string staleHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.CreateTable<LR2SongDBExtended.chart_digest_map>();
        songDb.InsertOrReplace(new TestableBmsFile
        {
            path = stalePath,
            date = 1
        }.WithHashAndFavorite(staleHash, favoriteValue: null), typeof(LR2SongDB.song));
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = stalePath,
            hash = staleHash
        }, typeof(LR2SongDBExtended.maintenance));
        songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
        {
            md5 = staleHash,
            sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
        }, typeof(LR2SongDBExtended.chart_digest_map));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "prune-stale-song-row",
            RunId = "prune-stale-song-row-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [currentPath],
            SongRows = [currentFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.StaleSongRowPrunedCount);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.UnknownRootSongRowCount);
        Assert.IsNotNull(songDb.Find<LR2SongDB.song>(currentPath));
        Assert.IsNull(songDb.Find<LR2SongDB.song>(stalePath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?;", stalePath));
        Assert.AreEqual(0, songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM chart_digest_map WHERE md5 = ?;", staleHash));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
    }

    [TestMethod]
    public void SyncService_RollsBackFailedSongRowChunkAndRetriesFromChunkStart()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "RollbackSongRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string firstPath = Path.Combine(songDirectory, "first.bms");
        string secondPath = Path.Combine(songDirectory, "second.bms");
        File.WriteAllText(firstPath, "#TITLE first rollback\r\n", Encoding.ASCII);
        File.WriteAllText(secondPath, "#TITLE second rollback\r\n", Encoding.ASCII);
        TestableBmsFile firstFile = CreateSyncTestFile(firstPath, ChartFileContentReader.ReadSnapshot(firstPath));
        TestableBmsFile secondFile = CreateSyncTestFile(secondPath, ChartFileContentReader.ReadSnapshot(secondPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "rollback-song-chunk";
        songDb.Execute(
            "CREATE TRIGGER fail_second_song_insert BEFORE INSERT ON song"
            + " WHEN NEW.path = '" + EscapeSqlLiteral(secondPath) + "'"
            + " BEGIN SELECT RAISE(ABORT, 'fail_second_song_insert'); END;");

        Assert.ThrowsException<SQLite.SQLiteException>(() => Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "rollback-fail-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        }));
        Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
        LR2SongDBExtended.lr2_full_generation_status failed = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Failed", failed.status);
        Assert.AreEqual("song_rows", failed.stage);
        Assert.AreEqual(2, failed.processed_cursor);
        Assert.AreEqual(4, failed.total_count);

        songDb.Execute("DROP TRIGGER fail_second_song_insert;");
        Lr2FullGenerationSyncResult retry = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "rollback-retry-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, retry.FinalStage);
        Assert.AreEqual(2, retry.SongRowProcessedCount);
        Assert.AreEqual("first rollback", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("second rollback", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_full_generation_status completed = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(4, completed.processed_cursor);
    }

    [TestMethod]
    public void SyncService_ParsesSongRowsWithDetectedUtf8Encoding()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Utf8");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "utf8.bms");
        File.WriteAllText(chartPath, "#TITLE 解析タイトル\r\n#ARTIST 解析アーティスト\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        var file = new TestableBmsFile
        {
            path = chartPath
        };
        file.SetHash(snapshot.Md5);
        file.ApplySha256(snapshot.Sha256);
        file.SetTitleForTest("Stale Title");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "utf8-song",
            RunId = "utf8-song",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowParseFailureCount);
        Assert.AreEqual("解析タイトル", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("解析アーティスト", songDb.ExecuteScalar<string>("SELECT artist FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("Stale Title", file.title);
    }

    [TestMethod]
    public void SyncService_BuildsChartInfoFromSongRowSnapshotsWhenNoResolverIsProvided()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfo");
        Directory.CreateDirectory(songDirectory);
        string currentPath = Path.Combine(songDirectory, "current.bms");
        string stalePath = Path.Combine(songDirectory, "stale.bms");
        string mismatchPath = Path.Combine(songDirectory, "mismatch.bms");
        File.WriteAllText(currentPath, "#PLAYER 1\r\n#TITLE current\r\n#BPM 150\r\n#PLAYLEVEL 7\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        File.WriteAllText(stalePath, "#PLAYER 1\r\n#TITLE stale\r\n#BPM 150\r\n#PLAYLEVEL 9\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        File.WriteAllText(mismatchPath, "#PLAYER 1\r\n#TITLE mismatch\r\n#BPM 150\r\n#PLAYLEVEL 11\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot currentSnapshot = ChartFileContentReader.ReadSnapshot(currentPath);
        ChartFileSnapshot staleSnapshot = ChartFileContentReader.ReadSnapshot(stalePath);
        ChartFileSnapshot mismatchSnapshot = ChartFileContentReader.ReadSnapshot(mismatchPath);
        TestableBmsFile currentFile = CreateSyncTestFile(currentPath, currentSnapshot);
        TestableBmsFile staleFile = CreateSyncTestFile(stalePath, staleSnapshot);
        TestableBmsFile mismatchFile = CreateSyncTestFile(mismatchPath, mismatchSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.InsertOrReplace(CreateChartInfo(currentSnapshot.Sha256, currentSnapshot.Md5, level: 7), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(staleSnapshot.Sha256, staleSnapshot.Md5, level: 9, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(mismatchSnapshot.Sha256, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", level: 11), typeof(LR2SongDBExtended.chart_info));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "chart-info-current",
            RunId = "chart-info-current",
            SongRows = [currentFile, staleFile, mismatchFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(3, result.SongRowProcessedCount);
        Assert.AreEqual(3, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", currentPath));
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", stalePath));
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", mismatchPath));
        Assert.AreEqual(3L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
    }

    [TestMethod]
    public void SyncService_SkipsChartInfoParseWhenCurrentParseFailureIsKnown()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoFailureSkip");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "failure-skip.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE failure skip\r\n#BPM 150\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        bool chartInfoCallbackCalled = false;
        bool parseFailureCallbackCalled = false;

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "chart-info-failure-skip",
            RunId = "chart-info-failure-skip",
            SongRows = [file],
            CurrentChartInfoParseFailureMd5s = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                snapshot.Md5
            },
            ChartInfoRowsCommitted = _ => chartInfoCallbackCalled = true,
            ChartInfoParseFailuresCommitted = (_, _) => parseFailureCallbackCalled = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(0, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(0L, songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        Assert.IsFalse(chartInfoCallbackCalled);
        Assert.IsFalse(parseFailureCallbackCalled);
    }

    [TestMethod]
    public void SyncService_UsesRequestChartInfoResolverWithoutChartInfoDbLookup()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoResolver");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "resolver.bms");
        File.WriteAllText(chartPath, "#TITLE resolver\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "chart-info-resolver",
            RunId = "chart-info-resolver",
            SongRows = [file],
            ChartInfoResolver = CreateChartInfoResolver(
            [
                CreateChartInfo(snapshot.Sha256, snapshot.Md5, level: 13)
            ]),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(13, songDb.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_RequestChartInfoResolverUsesStableMd5FallbackWhenSha256DoesNotMatch()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Md5Fallback");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE md5 fallback\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.InsertOrReplace(CreateChartInfo(new string('2', 64), snapshot.Md5, level: 22), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(new string('1', 64), snapshot.Md5, level: 11), typeof(LR2SongDBExtended.chart_info));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "chart-info-md5",
            RunId = "chart-info-md5",
            SongRows = [file],
            ChartInfoResolver = CreateChartInfoResolver(
            [
                CreateChartInfo(new string('2', 64), snapshot.Md5, level: 22),
                CreateChartInfo(new string('1', 64), snapshot.Md5, level: 11)
            ]),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(11, songDb.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_RebuildsChartInfoWhenRequestResolverReturnsStaleRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "StaleResolver");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "stale-resolver.bms");
        File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE stale resolver\r\n#BPM 150\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "chart-info-stale-resolver",
            RunId = "chart-info-stale-resolver",
            SongRows = [file],
            ChartInfoResolver = _ => CreateChartInfo(snapshot.Sha256, snapshot.Md5, level: 99, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        LR2SongDBExtended.chart_info chartInfo = songDb.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", snapshot.Sha256).Single();
        Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, chartInfo.parser_version);
        Assert.AreEqual(1, songDb.ExecuteScalar<int>("SELECT COALESCE(karinotes, -1) FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void SyncService_CancelledRequestMarksCancelledStatus()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() => Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "sig_cancel",
            RunId = "run_cancel",
            CancellationToken = cancellation.Token
        }));

        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.IsNotNull(row);
        Assert.AreEqual(Lr2FullGenerationStatusKind.Cancelled.ToString(), row.status);
        Assert.AreEqual("final_validation", row.stage);
        Assert.AreEqual(0, row.processed_cursor);
        Assert.AreEqual(0, row.total_count);
    }

    [TestMethod]
    public void SyncService_CancelledAfterFolderStageResumesAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "CancelResumeRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE cancel resume\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        using var cancellation = new CancellationTokenSource();
        const string signature = "cancel-resume-after-folder";
        var progressEvents = new List<Lr2FullGenerationSyncProgress>();

        Assert.ThrowsException<OperationCanceledException>(() => Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            CancellationToken = cancellation.Token,
            ProgressReporter = progress =>
            {
                if (progress != null)
                {
                    progressEvents.Add(progress);
                }
                if (progress?.Stage == "song_rows")
                {
                    cancellation.Cancel();
                }
            }
        }));

        LR2SongDBExtended.lr2_full_generation_status cancelled = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Cancelled", cancelled.status);
        Assert.AreEqual("song_rows", cancelled.stage);
        Assert.AreEqual(2, cancelled.processed_cursor);
        Assert.AreEqual(3, cancelled.total_count);
        Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
        Lr2FullGenerationSyncProgress songRowsProgress = progressEvents.First(progress => progress.Stage == "song_rows" && progress.StageTotalCount > 0);
        Assert.AreEqual(2, songRowsProgress.ProcessedCursor);
        Assert.AreEqual(3, songRowsProgress.TotalCount);
        Assert.AreEqual(0, songRowsProgress.StageProcessedCount);
        Assert.AreEqual(1, songRowsProgress.StageTotalCount);

        Lr2FullGenerationSyncResult resumed = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = signature,
            RunId = "resume-after-cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory, songDirectory),
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, resumed.FinalStage);
        Assert.IsNull(resumed.NormalFolderSyncResult);
        Assert.AreEqual(1, resumed.SongRowProcessedCount);
        Assert.AreEqual("cancel resume", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_full_generation_status completed = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(3, completed.processed_cursor);
        Assert.AreEqual(3, completed.total_count);
    }

    [TestMethod]
    public void SyncService_UpsertsLr2CompatibilityFactsWithoutReplacingMaintenanceHealth()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2Compatibility");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE lr2 compatibility\r\n#WAV01 emoji😀.wav\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = chartPath,
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            wav_files_defined = 99,
            wav_files_existing = 88
        }, typeof(LR2SongDBExtended.maintenance));

        var committedCompatibilityFacts = new List<BMSFileMaintenanceInfo>();
        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "lr2-compatibility",
            RunId = "lr2-compatibility",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            Lr2CompatibilityFactsCommitted = infos => committedCompatibilityFacts.AddRange(infos)
        });

        Assert.AreEqual(1, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(1, committedCompatibilityFacts.Count);
        Assert.AreEqual(chartPath, committedCompatibilityFacts[0].path);
        Assert.AreEqual(file.hash, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(99, songDb.ExecuteScalar<int>("SELECT wav_files_defined FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(88, songDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", chartPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_resource_warning_flags FROM maintenance WHERE path = ?;", chartPath);
        Assert.IsTrue((flags & (int)Lr2ResourceWarningFlags.RawPathEncodingUnsupported) != 0);
    }

    [TestMethod]
    public void SyncService_UpsertsLr2CompatibilityFactsByPathNocaseWithoutDuplicate()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2CompatibilityNocase");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE lr2 compatibility nocase\r\n#WAV01 emoji😀.wav\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
        string existingPath = Path.Combine(songDirectory, "CHART.BMS");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureMaintenanceSchema(songDb);
        songDb.InsertOrReplace(new BMSFileMaintenanceInfo
        {
            path = existingPath,
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        }, typeof(LR2SongDBExtended.maintenance));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "lr2-compatibility-nocase",
            RunId = "lr2-compatibility-nocase",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(
            1,
            songDb.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ? COLLATE NOCASE;", chartPath));
        Assert.AreEqual(file.hash, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", existingPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_resource_warning_flags FROM maintenance WHERE path = ?;", existingPath);
        Assert.IsTrue((flags & (int)Lr2ResourceWarningFlags.RawPathEncodingUnsupported) != 0);
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_ProjectsLr2CompatibilityWarningsToLiveRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "LiveLr2Compatibility");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE live lr2 compatibility\r\n#WAV01 emoji😀.wav\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 99,
                wav_files_existing = 88
            }, suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.Calculated);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_live_lr2_compatibility");

            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2ResourcePathUnsupported));
            Assert.AreEqual(99, file.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(88, file.maintenanceInfo.wav_files_existing);
            int flags = file.maintenanceInfo.lr2_resource_warning_flags.GetValueOrDefault();
            Assert.IsTrue((flags & (int)Lr2ResourceWarningFlags.RawPathEncodingUnsupported) != 0);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversLr2FolderWithoutCharts()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string nestedDirectory = Path.Combine(rootDirectory, "Custom");
            Directory.CreateDirectory(nestedDirectory);
            string lr2FolderPath = Path.Combine(nestedDirectory, "table.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE 入れ子表\r\n#COMMAND song.level = 12\r\n#MAXTRACKS 64", Encoding.GetEncoding("shift_jis"));
            string outsideLr2FolderPath = Path.Combine(scope.DirectoryPath, "outside.lr2folder");
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.Combine(rootDirectory, "stale.lr2folder"),
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = outsideLr2FolderPath,
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_lr2folder_only");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, row.stage);
            Assert.AreEqual(row.total_count, row.processed_cursor);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("入れ子表", lr2Folder.title);
            Assert.AreEqual("song.level = 12", lr2Folder.command);
            Assert.AreEqual(64, lr2Folder.max);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == Path.Combine(rootDirectory, "stale.lr2folder")));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == outsideLr2FolderPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversLr2FolderFromCustomFolderOutputBase()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string outputBase = Path.Combine(scope.DirectoryPath, "CustomOutput");
            Directory.CreateDirectory(outputBase);
            string lr2FolderPath = Path.Combine(outputBase, "0000.lr2folder");
            string stalePath = Path.Combine(outputBase, "stale.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Output Folder", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = []
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 2,
                    title = "Keep Stale",
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_custom_folder_output_base");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Output Folder", lr2Folder.title);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == stalePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversRootCustomFolderOutputAsRootRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string outputBase = Path.Combine(scope.DirectoryPath, "RootCustomOutput");
            Directory.CreateDirectory(outputBase);
            string lr2FolderPath = Path.Combine(outputBase, "root.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_root_custom_folder_output_base");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Root Output", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_GeneratesNormalCustomFolderOutputBaseParentRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Normal Output", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_normal_custom_folder_output_parent");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder outputBaseRow = rows.SingleOrDefault(folder => folder.path == Lr2FolderPath.ToFolderPath(outputBase));
            Assert.IsNotNull(outputBaseRow, rowSummary);
            Assert.AreEqual(1, outputBaseRow.type);
            Assert.AreEqual("#BeMusicSeeker", outputBaseRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseRow.parent);
            LR2SongDB.folder tableRow = rows.Single(folder => folder.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("Table", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBase), tableRow.parent);
            LR2SongDB.folder lr2Folder = rows.Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_GeneratesRootCustomFolderOutputParentRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string outputBase = Path.Combine(scope.DirectoryPath, "RootCustomOutput");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = outputBase;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_root_custom_folder_output_parent");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder parentRow = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual("Table", parentRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            Assert.IsTrue(parentRow.date.GetValueOrDefault() > 0);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversEnabledLr2BuiltinCustomFolderAsRelativeRootRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "favorite.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Favorite", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0x2, titleFlashHours: 24, bmsRoot);
            var library = new BMSLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_lr2_builtin_custom_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\favorite.lr2folder");
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Favorite", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_GeneratesBuiltinCustomFolderCategoryRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            string subDirectory = Path.Combine(randomDirectory, "Sub");
            Directory.CreateDirectory(subDirectory);
            File.WriteAllText(Path.Combine(randomDirectory, "folderinfo.txt"), "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(Path.Combine(subDirectory, "folderinfo.txt"), "#TITLE Sub Folder Info", Encoding.GetEncoding("shift_jis"));
            string lr2FolderPath = Path.Combine(subDirectory, "random.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Random", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0x1, titleFlashHours: 24, bmsRoot);
            var library = new BMSLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_builtin_custom_folder_category");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder category = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\");
            Assert.AreEqual(2, category.type);
            Assert.AreEqual("Random Folder Info", category.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, category.parent);
            Assert.IsTrue(category.date.GetValueOrDefault() > 0);
            LR2SongDB.folder subCategory = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\Sub\");
            Assert.AreEqual(2, subCategory.type);
            Assert.AreEqual("Sub Folder Info", subCategory.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM"), subCategory.parent);
            Assert.IsTrue(subCategory.date.GetValueOrDefault() > 0);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\RANDOM\Sub\random.lr2folder");
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"LR2files\CustomFolder\RANDOM\Sub"), lr2Folder.parent);
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_SkipsDisabledLr2BuiltinCustomFolder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "favorite.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Favorite", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = @"LR2files\CustomFolder\RANDOM\",
                    type = 2,
                    title = "Stale Random",
                    parent = Lr2SongFolderParentNormalizer.RootParentHash
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = @"LR2files\CustomFolder\favorite.lr2folder",
                    type = 2,
                    title = "Stale Favorite",
                    parent = Lr2SongFolderParentNormalizer.RootParentHash
                }, typeof(LR2SongDB.folder));
            }
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0, titleFlashHours: 24, bmsRoot);
            var library = new BMSLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_lr2_builtin_custom_folder_disabled");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsFalse(verify.Table<LR2SongDB.folder>().Any(folder => folder.path == @"LR2files\CustomFolder\RANDOM\"));
            Assert.IsFalse(verify.Table<LR2SongDB.folder>().Any(folder => folder.path == @"LR2files\CustomFolder\favorite.lr2folder"));
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversBuiltinCourseFolderAsTypeSixRegardlessOfCustomFolderMask()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "course1.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Course", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0, titleFlashHours: 24, bmsRoot);
            var library = new BMSLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_lr2_builtin_course_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\course1.lr2folder");
            Assert.AreEqual(6, lr2Folder.type);
            Assert.AreEqual("Course", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversBuiltinNewSongFolderOnlyWhenRecentSongExists()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(bmsRoot, "Pack");
            Directory.CreateDirectory(packDirectory);
            string chartPath = Path.Combine(packDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Recent\r\n#ARTIST Artist\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);

            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string customFolderDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            Directory.CreateDirectory(customFolderDirectory);
            string lr2FolderPath = Path.Combine(customFolderDirectory, "newsong.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE New Song", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0, titleFlashHours: 24, bmsRoot);
            var library = new BMSLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = [file]
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_lr2_builtin_newsong_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\newsong.lr2folder");
            Assert.AreEqual(3, lr2Folder.type);
            Assert.AreEqual("New Song", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DoesNotDiscoverBuiltinLr2RivalFolder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string rivalDirectory = Path.Combine(lr2Root, "LR2files", "Rival");
            Directory.CreateDirectory(rivalDirectory);
            string lr2FolderPath = Path.Combine(rivalDirectory, "rival.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Rival", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_lr2_builtin_rival_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsFalse(folders.Any(folder => folder.path == @"LR2files\Rival\rival.lr2folder"));
            Assert.IsFalse(folders.Any(folder => string.Equals(folder.path, lr2FolderPath, StringComparison.OrdinalIgnoreCase)));
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationDataSync_DiscoversRivalFolderFromNormalScanRootAsExternalFolder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string rivalDirectory = Path.Combine(rootDirectory, "__RIVAL__");
            Directory.CreateDirectory(rivalDirectory);
            string lr2FolderPath = Path.Combine(rivalDirectory, "rival.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Rival External", Encoding.GetEncoding("shift_jis"));
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2FullGenerationDataSync("test_external_rival_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Rival External", lr2Folder.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rivalDirectory),
                lr2Folder.parent);
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncService_DeletesMissingDiscoveredLr2FolderRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string missingPath = Path.Combine(rootDirectory, "missing.lr2folder");
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = missingPath,
            type = 2,
            title = "Keep",
            date = 1
        }, typeof(LR2SongDB.folder));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "test",
            RunId = "run",
            RootDirectories = [rootDirectory],
            DirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingPath],
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.ItemCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.DeletedCount);
        Assert.AreEqual(0, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == missingPath));
        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.IsTrue(result.StartupScanDiagnosticResult.IsClean);
    }

    [TestMethod]
    public void SyncService_UsesEnumeratedLr2FolderTimestampForGeneratedRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Table");
        DateTime enumeratedTimestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
        File.SetLastWriteTimeUtc(lr2FolderPath, liveTimestamp);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "lr2folder-enumerated-time",
            RunId = "lr2folder-enumerated-time-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, enumeratedTimestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == lr2FolderPath);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(enumeratedTimestamp), row.date);
        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.GeneratedCount);
        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
    }

    [TestMethod]
    public void SyncService_PreservesUnchangedLr2FolderRowBeforeDefinitionParse()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Reparsed Title");
        DateTime timestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            title = "Preserved Title",
            type = 2,
            parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory),
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp),
            adddate = 12345
        }, typeof(LR2SongDB.folder));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "lr2folder-preserve",
            RunId = "lr2folder-preserve-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == lr2FolderPath);
        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.PreservedCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.GeneratedCount);
        Assert.AreEqual("Preserved Title", row.title);
        Assert.AreEqual(12345, row.adddate);
        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
    }

    [TestMethod]
    public void SyncService_PreservesEntriesOnlyLr2FolderRowBeforeDefinitionParse()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2FolderPath = Path.Combine(rootDirectory, "table.lr2folder");
        File.WriteAllText(lr2FolderPath, "#TITLE Reparsed Entries Only");
        DateTime timestamp = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = lr2FolderPath,
            title = "Preserved Entries Only",
            type = 2,
            parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory),
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp)
        }, typeof(LR2SongDB.folder));

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "lr2folder-entries-only-preserve",
            RunId = "lr2folder-entries-only-preserve-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
            },
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == lr2FolderPath);
        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.PreservedCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.GeneratedCount);
        Assert.AreEqual("Preserved Entries Only", row.title);
        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
    }

    [TestMethod]
    public void SyncService_UsesEnumeratedDirectoryTimestampForNormalFolderRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        DateTime enumeratedTimestamp = new(2026, 6, 5, 5, 0, 0, DateTimeKind.Utc);
        DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
        Directory.SetLastWriteTimeUtc(rootDirectory, liveTimestamp);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2FullGenerationSyncResult result = Lr2FullGenerationSyncService.Run(songDb, new Lr2FullGenerationSyncRequest
        {
            Signature = "directory-enumerated-time",
            RunId = "directory-enumerated-time-run",
            RootDirectories = [rootDirectory],
            DirectoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [rootDirectory] = new RootFileEnumerationEntry(rootDirectory, enumeratedTimestamp)
            },
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        LR2SongDB.folder row = songDb.Table<LR2SongDB.folder>().Single(folder => folder.path == ToFolderPath(rootDirectory));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(enumeratedTimestamp), row.date);
        Assert.AreEqual(1, result.NormalFolderSyncResult.GeneratedCount);
        Assert.AreEqual(Lr2FullGenerationSyncService.CompletedStage, result.FinalStage);
        Assert.AreEqual(0, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
    }

    [TestMethod]
    public void FolderInfoCandidateEnumeration_ReturnsTargetFolderInfoMetadata()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
        Directory.CreateDirectory(chartDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        string unrelatedFolderInfoPath = Path.Combine(unrelatedDirectory, "folderinfo.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 6, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(folderInfoPath, "#TITLE Pack");
        File.WriteAllText(unrelatedFolderInfoPath, "#TITLE Other");
        File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshot(
            [rootDirectory],
            [chartDirectory]);

        CollectionAssert.AreEqual(new[] { folderInfoPath }, snapshot.Paths.ToArray());
        Assert.IsTrue(snapshot.DiscoveryComplete);
        Assert.IsTrue(snapshot.EntriesByPath.TryGetValue(folderInfoPath, out RootFileEnumerationEntry entry));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), entry.LastWriteTimeUnixSeconds);
    }

    [TestMethod]
    public void TextMetadataCandidateEnumeration_ReturnsTargetFolderInfoAndTextDirectories()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
        Directory.CreateDirectory(chartDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        string readmePath = Path.Combine(chartDirectory, "readme.txt");
        string unrelatedTextPath = Path.Combine(unrelatedDirectory, "readme.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 6, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(folderInfoPath, "#TITLE Pack");
        File.WriteAllText(readmePath, "notes");
        File.WriteAllText(unrelatedTextPath, "other");
        File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);

        Lr2TextMetadataCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateTextMetadataSnapshot(
            [rootDirectory],
            [chartDirectory]);

        CollectionAssert.AreEqual(new[] { folderInfoPath }, snapshot.FolderInfoCandidates.Paths.ToArray());
        CollectionAssert.AreEqual(new[] { chartDirectory }, snapshot.TextFileDirectories.ToArray());
        CollectionAssert.DoesNotContain(snapshot.TextFileDirectories.ToList(), unrelatedDirectory);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), snapshot.FolderInfoCandidates.EntriesByPath[folderInfoPath].LastWriteTimeUnixSeconds);
    }

    [TestMethod]
    public void FolderInfoCandidateEnumerationFromEntries_MatchesTargetDirectoryCaseInsensitively()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        Directory.CreateDirectory(chartDirectory);
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 7, 0, 0, DateTimeKind.Utc);
        RootFileEnumerationEntry entry = new(folderInfoPath, folderInfoTimestamp);

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(
            [entry],
            [chartDirectory.ToUpperInvariant()]);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(folderInfoPath) }, snapshot.Paths.ToArray());
        Assert.IsTrue(snapshot.DiscoveryComplete);
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), snapshot.EntriesByPath[Path.GetFullPath(folderInfoPath)].LastWriteTimeUnixSeconds);
    }

    [TestMethod]
    public void FolderInfoCandidateEnumerationFromEntries_UsesOnlyTargetDirectories()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string targetDirectory = Path.Combine(rootDirectory, "Target");
        string unrelatedDirectory = Path.Combine(rootDirectory, "Other");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        string folderInfoPath = Path.Combine(targetDirectory, "folderinfo.txt");
        string unrelatedFolderInfoPath = Path.Combine(unrelatedDirectory, "folderinfo.txt");
        DateTime folderInfoTimestamp = new(2026, 6, 5, 7, 30, 0, DateTimeKind.Utc);
        File.WriteAllText(folderInfoPath, "#TITLE Target");
        File.WriteAllText(unrelatedFolderInfoPath, "#TITLE Other");
        File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimestamp);

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromEntries(
            [
                new RootFileEnumerationEntry(folderInfoPath, folderInfoTimestamp),
                new RootFileEnumerationEntry(unrelatedFolderInfoPath, folderInfoTimestamp)
            ],
            [targetDirectory]);

        CollectionAssert.AreEqual(new[] { folderInfoPath }, snapshot.Paths.ToArray());
        Assert.IsTrue(snapshot.DiscoveryComplete);
        Assert.IsTrue(snapshot.EntriesByPath.TryGetValue(folderInfoPath, out RootFileEnumerationEntry entry));
        Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(folderInfoTimestamp), entry.LastWriteTimeUnixSeconds);
    }

    [TestMethod]
    public void FolderInfoCandidateEnumerationFromSurface_KeepsPathOnlyCandidate()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string chartDirectory = Path.Combine(rootDirectory, "Pack");
        Directory.CreateDirectory(chartDirectory);
        string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");

        Lr2FolderInfoCandidateSnapshot snapshot = Lr2FolderInfoCandidateEnumerationService.CreateSnapshotFromSurface(
            [folderInfoPath],
            [],
            [chartDirectory]);

        CollectionAssert.AreEqual(new[] { Path.GetFullPath(folderInfoPath) }, snapshot.Paths.ToArray());
        Assert.IsFalse(snapshot.EntriesByPath[Path.GetFullPath(folderInfoPath)].LastWriteTimeUnixSeconds.HasValue);
    }

    private sealed class TestDatabaseScope : IDisposable
    {
        public string DirectoryPath { get; }

        public string SongDbPath { get; }

        private TestDatabaseScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
            SongDbPath = Path.Combine(directoryPath, "song.db");
            using var _ = new LR2SongDBExtended(SongDbPath);
        }

        public static TestDatabaseScope Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryLr2FullGenerationSyncTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return new TestDatabaseScope(directoryPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }

    private static string ToFolderPath(string directoryPath)
    {
        return Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    private static void InsertNormalFolderRow(LR2SongDBExtended songDb, string directoryPath, string parentHash)
    {
        songDb.InsertOrReplace(new LR2SongDB.folder
        {
            path = ToFolderPath(directoryPath),
            title = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            type = 1,
            parent = parentHash,
            date = Lr2SongRowEnricher.ToLr2UnixSeconds(Directory.GetLastWriteTimeUtc(directoryPath)),
            adddate = Lr2SongRowEnricher.ToLr2UnixSeconds(new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc))
        }, typeof(LR2SongDB.folder));
    }

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet targets, string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyInstalledChartStorageTargets", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [targets, reason]);
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyLibraryMutationDelta", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [delta]);
    }

    private static void InvokeBeginLr2FullGenerationSyncRequest(BMSLibrary library)
    {
        bool started = InvokeTryBeginLr2FullGenerationSyncRequest(library, out _);
        Assert.IsTrue(started);
    }

    private static bool InvokeTryBeginLr2FullGenerationSyncRequest(BMSLibrary library, out int requestVersion)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("TryBeginLr2FullGenerationSyncRequest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        object[] arguments = [0];
        bool started = (bool)methodInfo.Invoke(library, arguments);
        requestVersion = (int)arguments[0];
        return started;
    }

    private static object InvokeCreateLr2FullGenerationSyncInput(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateLr2FullGenerationSyncInput", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return methodInfo.Invoke(library, []);
    }

    private static bool InvokeIsLr2FullGenerationSyncInputCurrent(BMSLibrary library, object input)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("IsLr2FullGenerationSyncInputCurrent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (bool)methodInfo.Invoke(library, [input]);
    }

    private static bool InvokeHasLr2FullGenerationPreparedDataSurface(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("HasLr2FullGenerationPreparedDataSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (bool)methodInfo.Invoke(library, []);
    }

    private static Lr2FullGenerationPreparedDataSurface CreatePreparedLr2FolderSurface(
        string scopeDirectory,
        string filePath,
        IReadOnlyDictionary<string, RootFileEnumerationEntry>? directoryEntries = null,
        IEnumerable<string>? folderInfoFilePaths = null,
        IReadOnlyDictionary<string, RootFileEnumerationEntry>? folderInfoFileEntries = null,
        IEnumerable<string>? textFileDirectories = null)
    {
        return new Lr2FullGenerationPreparedDataSurface(
            [scopeDirectory],
            [filePath],
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [filePath] = new RootFileEnumerationEntry(filePath, File.GetLastWriteTimeUtc(filePath))
            },
            directoryEntries,
            folderInfoFilePaths,
            folderInfoFileEntries,
            textFileDirectories,
            discoveryComplete: true);
    }

    private static IReadOnlyCollection<string> GetInputStringList(object input, string propertyName)
    {
        PropertyInfo propertyInfo = input.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(propertyInfo);
        return ((IEnumerable<string>)propertyInfo.GetValue(input)).ToList();
    }

    private static bool GetInputBool(object input, string propertyName)
    {
        PropertyInfo propertyInfo = input.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(propertyInfo);
        return (bool)propertyInfo.GetValue(input);
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> GetInputEntryMap(object input, string propertyName)
    {
        PropertyInfo propertyInfo = input.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(propertyInfo);
        return (IReadOnlyDictionary<string, RootFileEnumerationEntry>)propertyInfo.GetValue(input);
    }

    private static int GetInputInt(object input, string propertyName)
    {
        PropertyInfo propertyInfo = input.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(propertyInfo);
        return (int)propertyInfo.GetValue(input);
    }

    private static int GetPrivateIntField(object target, string fieldName)
    {
        FieldInfo fieldInfo = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return (int)fieldInfo.GetValue(target);
    }

    private static void InvokeCaptureLr2FullGenerationScanSurface(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        SongTableFileCheckResult result)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CaptureLr2FullGenerationScanSurface", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, rootDirectories, result]);
    }

    private static void InvokeApplyLr2FolderFileDiffSync(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult result,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("ApplyLr2FolderFileDiffSync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, rootDirectories, result, reason, null]);
    }

    private static void InvokeSetModeAndCommitToDb(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("setModeAndCommitToDB", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [files, false]);
    }

    private static void InvokeMarkLr2FullGenerationIncompleteAfterFileDiffNormalFolderSyncFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult result)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("MarkLr2FullGenerationIncompleteAfterFileDiffNormalFolderSyncFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, result]);
    }

    private static void InvokeMarkLr2FullGenerationIncompleteAfterNormalFolderSyncFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("MarkLr2FullGenerationIncompleteAfterNormalFolderSyncFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, stage, detail, logReason]);
    }

    private static void InvokeMarkLr2FullGenerationIncompleteAfterSongDbWriteFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("MarkLr2FullGenerationIncompleteAfterSongDbWriteFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, stage, detail, logReason]);
    }

    private static void InvokeMarkLr2FullGenerationIncompleteAfterFileDiffSongDbWriteFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        Exception exception,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("MarkLr2FullGenerationIncompleteAfterFileDiffSongDbWriteFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, exception, reason]);
    }

    private static void InvokeMarkLr2FullGenerationIncompleteAfterMaintenanceSongDbWriteFailure(
        BMSLibrary library,
        Exception exception,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("MarkLr2FullGenerationIncompleteAfterMaintenanceSongDbWriteFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [exception, reason]);
    }

    private static List<string> InvokeGetBmsDirectories(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("getBMSDirectories", BindingFlags.Instance | BindingFlags.NonPublic, null, [], null);
        Assert.IsNotNull(methodInfo);
        return (List<string>)methodInfo.Invoke(library, null);
    }

    private static List<string> InvokeGetBmsDirectories(BMSLibrary library, out object normalization)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod(
            "getBMSDirectories",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(object).MakeByRefType()],
            null);
        if (methodInfo == null)
        {
            methodInfo = typeof(BMSLibrary)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == "getBMSDirectories" && method.GetParameters().Length == 1);
        }
        object[] args = [null!];
        List<string> result = (List<string>)methodInfo.Invoke(library, args);
        normalization = args[0];
        return result;
    }

    private static int GetPrivateInt(object instance, string propertyName)
    {
        Assert.IsNotNull(instance);
        PropertyInfo propertyInfo = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(propertyInfo);
        return (int)propertyInfo.GetValue(instance);
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ResetTouchedSettings()
    {
        Settings.Default.OperationModeLR2DB = true;
        Settings.Default.EnableLR2SongDbFullGeneration = false;
        ResetLr2FolderDiscoverySettings();
    }

    private static void ResetLr2FolderDiscoverySettings()
    {
        Settings.Default.LR2CustomFolderOutputBaseDir = string.Empty;
        Settings.Default.LR2CustomFolderOutputBaseDirRootType = string.Empty;
        Settings.Default.LR2RootPath = string.Empty;
    }

    private static LR2Config CreateLr2Config(string lr2RootPath, int customFolderMask, int titleFlashHours, params string[] bmsRoots)
    {
        string configDirectory = Path.Combine(lr2RootPath, "LR2files", "Config");
        Directory.CreateDirectory(configDirectory);
        string pathElements = string.Join(
            string.Empty,
            (bmsRoots ?? [])
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => "<path>" + EscapeXml(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar) + "</path>"));
        string configPath = Path.Combine(configDirectory, "config.xml");
        File.WriteAllText(
            configPath,
            "<config><system><customfolder>" + customFolderMask + "</customfolder><titleflash>" + titleFlashHours + "</titleflash></system><jukebox>" + pathElements + "</jukebox></config>",
            Encoding.UTF8);
        return new LR2Config(configPath);
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    private static TestableBmsFile CreateSyncTestFile(string path, ChartFileSnapshot snapshot)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(snapshot.Md5);
        file.ApplySha256(snapshot.Sha256);
        return file;
    }

    private static string EscapeSqlLiteral(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }

    private static Dictionary<string, RootFileEnumerationEntry> CreateFileEntryMap(params string[] filePaths)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string filePath in filePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            result[filePath] = new RootFileEnumerationEntry(filePath, File.GetLastWriteTimeUtc(filePath));
        }

        return result;
    }

    private static Dictionary<string, RootFileEnumerationEntry> CreateDirectoryEntryMap(params string[] directoryPaths)
    {
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string directoryPath in directoryPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            result[directoryPath] = RootFileEnumerationEntry.FromDirectoryInfo(directoryPath);
        }

        return result;
    }

    private static LR2SongDBExtended.chart_info CreateChartInfo(string sha256, string md5, int level, int? parserVersion = null)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            level = level,
            parser_version = parserVersion ?? BmsLibraryDbGateway.CurrentChartInfoParserVersion
        };
    }

    private static Func<BMSFile, LR2SongDBExtended.chart_info> CreateChartInfoResolver(
        IEnumerable<LR2SongDBExtended.chart_info> rows)
    {
        var bySha256 = new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
        var md5Candidates = new Dictionary<string, SortedDictionary<string, LR2SongDBExtended.chart_info>>(StringComparer.OrdinalIgnoreCase);
        foreach (LR2SongDBExtended.chart_info row in rows ?? [])
        {
            if (row == null || row.parser_version != BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256))
            {
                bySha256[row.sha256] = row;
            }
            if (!string.IsNullOrWhiteSpace(row.md5) && !string.IsNullOrWhiteSpace(row.sha256))
            {
                if (!md5Candidates.TryGetValue(row.md5, out SortedDictionary<string, LR2SongDBExtended.chart_info> candidates))
                {
                    candidates = new SortedDictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase);
                    md5Candidates[row.md5] = candidates;
                }
                candidates[row.sha256] = row;
            }
        }

        Dictionary<string, LR2SongDBExtended.chart_info> byMd5 = md5Candidates
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Value.First().Value, StringComparer.OrdinalIgnoreCase);
        return row =>
        {
            if (row == null)
            {
                return null!;
            }
            if (!string.IsNullOrWhiteSpace(row.sha256)
                && bySha256.TryGetValue(row.sha256, out LR2SongDBExtended.chart_info bySha256Row))
            {
                return bySha256Row;
            }
            if (!string.IsNullOrWhiteSpace(row.hash)
                && byMd5.TryGetValue(row.hash, out LR2SongDBExtended.chart_info byMd5Row))
            {
                return byMd5Row;
            }
            return null!;
        };
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetFavorite(int? value)
        {
            favorite = value;
        }

        public void SetTitleForTest(string value)
        {
            title = value;
        }

        public void SetArtistForTest(string value)
        {
            artist = value;
        }

        public TestableBmsFile WithHashAndFavorite(string hashValue, int? favoriteValue)
        {
            SetHash(hashValue);
            SetFavorite(favoriteValue);
            return this;
        }
    }
}
