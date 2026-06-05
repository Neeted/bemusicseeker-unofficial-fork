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
public sealed class BmsLibraryLr2FullGenerationBackfillTests
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
            BMSFile file = CreateBackfillTestFile(chartPath, snapshot);
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
            BMSFile file = CreateBackfillTestFile(chartPath, snapshot);
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
            BMSFile keepFile = CreateBackfillTestFile(keepPath, ChartFileContentReader.ReadSnapshot(keepPath));
            BMSFile removeFile = CreateBackfillTestFile(removePath, ChartFileContentReader.ReadSnapshot(removePath));
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
            File.WriteAllText(oldPath, "#TITLE Moved\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(newPath, "#TITLE Moved\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = CreateBackfillTestFile(oldPath, ChartFileContentReader.ReadSnapshot(oldPath));
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
            Assert.IsTrue(folders.Any(folder => folder.path == ToFolderPath(newDirectory)));
            Assert.IsNull(verify.Find<LR2SongDB.song>(oldPath));
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(newPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BlocksWhileFullGenerationBackfillIsRunning()
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
            BMSFile file = CreateBackfillTestFile(chartPath, snapshot);
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeBeginLr2FullGenerationBackfillRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_blocked_add"));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2FullGenerationBackfillRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BlocksRunningBackfillEvenWhenFeatureIsDisabled()
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
            BMSFile file = CreateBackfillTestFile(chartPath, snapshot);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new BMSLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeBeginLr2FullGenerationBackfillRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []), "test_disabled_running_add"));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2FullGenerationBackfillRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SetModeAndCommitToDb_BlocksWhileFullGenerationBackfillIsRunning()
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
            InvokeBeginLr2FullGenerationBackfillRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeSetModeAndCommitToDb(library, []));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2FullGenerationBackfillRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DoesNotQueueWhenFeatureIsDisabled()
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

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationBackfillIfNeeded("test_disabled");

            Assert.AreEqual(Lr2FullGenerationStatusKind.NotNeeded, snapshot.Status);
            Assert.AreEqual(1, library.Lr2FullGenerationStatusVersion);
            Assert.AreEqual(Lr2FullGenerationStatusKind.NotNeeded, library.GetLr2FullGenerationStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(0, library.Lr2FullGenerationBackfillRequestedVersion);
            Assert.AreEqual(0, library.Lr2FullGenerationBackfillCompletedVersion);
            Assert.IsFalse(library.Lr2FullGenerationBackfillRunning);
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
    public void QueueLr2FullGenerationBackfillIfNeeded_DoesNotQueueAgainWhileRunning()
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
            InvokeBeginLr2FullGenerationBackfillRequest(library);

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationBackfillIfNeeded("test_running");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Running, snapshot.Status);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Running, library.GetLr2FullGenerationStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(1, library.Lr2FullGenerationBackfillRequestedVersion);
            Assert.IsTrue(library.Lr2FullGenerationBackfillRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryBeginLr2FullGenerationBackfillRequest_DoesNotAdvanceVersionWhenAlreadyRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            ResetLr2FolderDiscoverySettings();
            var library = new BMSLibrary(scope.SongDbPath);

            Assert.IsTrue(InvokeTryBeginLr2FullGenerationBackfillRequest(library, out int firstVersion));
            Assert.AreEqual(1, firstVersion);
            Assert.IsFalse(InvokeTryBeginLr2FullGenerationBackfillRequest(library, out int secondVersion));
            Assert.AreEqual(firstVersion, secondVersion);
            Assert.AreEqual(1, library.Lr2FullGenerationBackfillRequestedVersion);
            Assert.IsTrue(library.Lr2FullGenerationBackfillRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void IsLr2FullGenerationBackfillInputCurrent_DetectsFolderInfoSurfaceChange()
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

            object input = InvokeCreateLr2FullGenerationBackfillInput(library);

            Assert.IsTrue(InvokeIsLr2FullGenerationBackfillInputCurrent(library, input));
            File.WriteAllText(Path.Combine(rootDirectory, "folderinfo.txt"), "#TITLE Root Title");
            Assert.IsFalse(InvokeIsLr2FullGenerationBackfillInputCurrent(library, input));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_RunsFullGenerationBackfillAndMarksCompletedWhenClean()
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
            File.WriteAllText(chartPath, "#TITLE Parsed Title\r\n#ARTIST Parsed Artist\r\n#BPM 120\r\n#00111:01\r\n");
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
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                queuedName = name;
                queuedReason = reason;
                work().GetAwaiter().GetResult();
                return true;
            };

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationBackfillIfNeeded("test_enabled");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, snapshot.Status);
            Assert.AreEqual("lr2_full_generation_backfill", queuedName);
            Assert.AreEqual("test_enabled", queuedReason);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, row.stage);
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
            Assert.AreEqual(7, verify.ExecuteScalar<int>("SELECT mode FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT random FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT longnote FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1234, verify.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT txt FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(3, verify.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(98765, verify.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("keep-tag", verify.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual("00000000", file.folder);
            Assert.AreEqual("11111111", file.parent);
            Assert.AreEqual(1, library.Lr2FullGenerationBackfillRequestedVersion);
            Assert.AreEqual(1, library.Lr2FullGenerationBackfillCompletedVersion);
            Assert.IsFalse(library.Lr2FullGenerationBackfillRunning);
            Assert.AreEqual(row.total_count.GetValueOrDefault(), library.Lr2FullGenerationBackfillTotalCount);
            Assert.AreEqual(row.processed_cursor.GetValueOrDefault(), library.Lr2FullGenerationBackfillProcessedCount);
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, library.Lr2FullGenerationBackfillStage);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, library.GetLr2FullGenerationStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DoesNotQueueSecondBackfillWhenCompletedStatusIsCurrent()
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
            File.WriteAllText(chartPath, "#TITLE Noop Backfill\r\n#00111:01\r\n", Encoding.ASCII);
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

            Lr2FullGenerationStatusSnapshot first = library.QueueLr2FullGenerationBackfillIfNeeded("test_first");
            int requestedVersionAfterFirst = library.Lr2FullGenerationBackfillRequestedVersion;
            int completedVersionAfterFirst = library.Lr2FullGenerationBackfillCompletedVersion;
            int statusVersionAfterFirst = library.Lr2FullGenerationStatusVersion;
            Lr2FullGenerationStatusSnapshot second = library.QueueLr2FullGenerationBackfillIfNeeded("test_second");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, first.Status);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, second.Status);
            Assert.AreEqual(1, scheduledCount);
            Assert.AreEqual(requestedVersionAfterFirst, library.Lr2FullGenerationBackfillRequestedVersion);
            Assert.AreEqual(completedVersionAfterFirst, library.Lr2FullGenerationBackfillCompletedVersion);
            Assert.AreEqual(statusVersionAfterFirst + 1, library.Lr2FullGenerationStatusVersion);
            Assert.IsFalse(library.Lr2FullGenerationBackfillRunning);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DoesNotQueueWhenCompletedCopiedSongDbIsCurrent()
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
            File.WriteAllText(chartPath, "#TITLE Copied Noop Backfill\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateBackfillTestFile(chartPath, chartSnapshot);
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

            Lr2FullGenerationStatusSnapshot first = firstLibrary.QueueLr2FullGenerationBackfillIfNeeded("test_first_for_copy");
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

            Lr2FullGenerationStatusSnapshot second = copiedLibrary.QueueLr2FullGenerationBackfillIfNeeded("test_copied_song_db");

            Assert.AreEqual(Lr2FullGenerationStatusKind.Needed, first.Status);
            Assert.AreEqual(1, firstScheduledCount);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Completed, second.Status);
            Assert.AreEqual(0, copiedScheduledCount);
            Assert.AreEqual(0, copiedLibrary.Lr2FullGenerationBackfillRequestedVersion);
            Assert.AreEqual(0, copiedLibrary.Lr2FullGenerationBackfillCompletedVersion);
            using var verify = new LR2SongDBExtended(copiedSongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void BackfillService_PreservesUserColumnsWhenRunningOnCopiedSongDb()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE Copied User Columns\r\n#ARTIST Parsed Artist\r\n#00111:01\r\n", Encoding.ASCII);
        ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, chartSnapshot);

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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(copiedDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "copied-user-columns",
            RunId = "copied-user-columns-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, result.FinalStage);
        Assert.AreEqual("Copied User Columns", copiedDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(chartSnapshot.Md5, copiedDb.ExecuteScalar<string>("SELECT hash FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(5, copiedDb.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual(123456, copiedDb.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?;", chartPath));
        Assert.AreEqual("copied-user-tag", copiedDb.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_WithNoRootsDoesNotCompleteGeneration()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_no_roots");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Incomplete", row.status);
            StringAssert.Contains(row.last_error, Lr2FullGenerationBackfillService.StartupScanBlockersReason);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
            Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(1, library.Lr2FullGenerationBackfillRequestedVersion);
            Assert.AreEqual(0, library.Lr2FullGenerationBackfillCompletedVersion);
            Assert.AreEqual(1, library.Lr2FullGenerationBackfillFailedVersion);
            Assert.IsFalse(library.Lr2FullGenerationBackfillRunning);
            StringAssert.Contains(library.Lr2FullGenerationBackfillFailureMessage, Lr2FullGenerationBackfillService.StartupScanBlockersReason);
            Assert.AreEqual(Lr2FullGenerationStatusKind.Incomplete, library.GetLr2FullGenerationStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_WithNoRootsStillBackfillsSongRows()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_no_roots_with_song");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Incomplete", row.status);
            StringAssert.Contains(row.last_error, Lr2FullGenerationBackfillService.StartupScanBlockersReason);
            Assert.AreEqual(1, row.processed_cursor);
            Assert.AreEqual(1, row.total_count);
            Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory), verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_BuildsMissingChartInfoBeforeSongRows()
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
            TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_chart_info_build");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", snapshot.Sha256, snapshot.Md5));
            Assert.AreEqual(12, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT karinotes FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
            Assert.AreEqual(library.ChartInfoBackfillRequestedVersion, library.ChartInfoBackfillCompletedVersion);
            Assert.IsFalse(library.ChartInfoBackfillRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_RebuildsStaleChartInfoBeforeSongRows()
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
            TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_stale_chart_info_build");

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
    public void BackfillService_FallsBackToSongCopyWhenChartSnapshotCannotBeRead()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
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
    public void BackfillService_LeavesIncompleteWhenUnknownRootFolderRowRemains()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "unknown-root-folder",
            RunId = "unknown-root-folder",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.UnknownRootFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "unknownRootFolderRows=1");
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenFolderDateIsMissing()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "folder-date-missing",
            RunId = "folder-date-missing",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.DateMissingFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "dateMissingFolderRows=1");
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenFolderDateIsStaleAfterResume()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "folder-date-stale-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = CreateFileEntryMap(lr2FolderPath),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(2, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "dateStaleFolderRows=2");
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenLegacyNormalFolderRowDateIsStale()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "legacy-folder-date-stale",
            RunId = "legacy-folder-date-stale-run",
            RootDirectories = [rootDirectory],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "dateStaleFolderRows=1");
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenFolderTargetIsMissingAfterResume()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "folder-target-missing-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingLr2FolderPath],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(2, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "dateStaleFolderRows=2");
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

        Lr2StartupScanBlockerCleanupResult result = Lr2FullGenerationBackfillService.CleanupStartupScanBlockerFolderRows(
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
    public void BackfillService_LeavesIncompleteWhenSourceBecomesStaleBeforeCompletion()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "Root");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE source stale\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "source-stale",
            RunId = "source-stale",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc),
            IsSourceCurrent = () => false
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.SourceStaleStage, result.FinalStage);
        Assert.AreEqual(Lr2FullGenerationBackfillService.SourceStaleReason, result.IncompleteReason);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        Assert.AreEqual(Lr2FullGenerationBackfillService.SourceStaleStage, row.stage);
        StringAssert.Contains(row.last_error, Lr2FullGenerationBackfillService.SourceStaleReason);
    }

    [TestMethod]
    public void BackfillService_ResumesFromCompletedNormalFolderBoundary()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume song\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "resume-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, result.FinalStage);
        Assert.IsNull(result.NormalFolderSyncResult);
        Assert.AreEqual(2, songDb.Table<LR2SongDB.folder>().Count());
        Assert.AreEqual("resume song", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(row.total_count, row.processed_cursor);
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenExpectedNormalFolderRowsAreMissingAfterResume()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMissingFolderRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume missing folder\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "resume-missing-folder-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(2, result.StartupScanDiagnosticResult.MissingExpectedFolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "missingExpectedFolderRows=2");
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenExpectedLr2FolderRowIsMissingAfterResume()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "resume-missing-lr2folder-row-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [lr2FolderPath],
            Lr2FolderFileEntries = CreateFileEntryMap(lr2FolderPath),
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "missingExpectedLr2FolderRows=1");
    }

    [TestMethod]
    public void BackfillService_LeavesIncompleteWhenExpectedLr2FolderMetadataIsMissingAfterResume()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "resume-missing-metadata-lr2folder-run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingLr2FolderPath],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.MissingExpectedLr2FolderRowCount);
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Incomplete", row.status);
        StringAssert.Contains(row.last_error, "missingExpectedLr2FolderRows=1");
    }

    [TestMethod]
    public void BackfillService_RestartsWhenResumeTotalCountDiffers()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "ResumeMismatchRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE resume mismatch\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "restart-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, result.FinalStage);
        Assert.IsNotNull(result.NormalFolderSyncResult);
        Assert.IsTrue(songDb.Table<LR2SongDB.folder>().Any());
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(3, row.total_count);
    }

    [TestMethod]
    public void BackfillService_ResumesInsideSongRowsFromDurableCursor()
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
        TestableBmsFile firstFile = CreateBackfillTestFile(firstPath, firstSnapshot);
        TestableBmsFile secondFile = CreateBackfillTestFile(secondPath, secondSnapshot);
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
            processedCursor: 4,
            totalCount: 5,
            stage: "song_rows",
            detail: "interrupted",
            nowUtc: new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc));
        InsertNormalFolderRow(songDb, rootDirectory, Lr2SongFolderParentNormalizer.RootParentHash);
        InsertNormalFolderRow(songDb, songDirectory, Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory));

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "resume-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, result.FinalStage);
        Assert.AreEqual(1, result.SongRowProcessedCount);
        Assert.AreEqual("first stale", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("second updated", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_full_generation_status row = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", row.status);
        Assert.AreEqual(5, row.processed_cursor);
    }

    [TestMethod]
    public void BackfillService_RollsBackFailedSongRowChunkAndRetriesFromChunkStart()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "RollbackSongRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string firstPath = Path.Combine(songDirectory, "first.bms");
        string secondPath = Path.Combine(songDirectory, "second.bms");
        File.WriteAllText(firstPath, "#TITLE first rollback\r\n", Encoding.ASCII);
        File.WriteAllText(secondPath, "#TITLE second rollback\r\n", Encoding.ASCII);
        TestableBmsFile firstFile = CreateBackfillTestFile(firstPath, ChartFileContentReader.ReadSnapshot(firstPath));
        TestableBmsFile secondFile = CreateBackfillTestFile(secondPath, ChartFileContentReader.ReadSnapshot(secondPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        const string signature = "rollback-song-chunk";
        songDb.Execute(
            "CREATE TRIGGER fail_second_song_insert BEFORE INSERT ON song"
            + " WHEN NEW.path = '" + EscapeSqlLiteral(secondPath) + "'"
            + " BEGIN SELECT RAISE(ABORT, 'fail_second_song_insert'); END;");

        Assert.ThrowsException<SQLite.SQLiteException>(() => Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
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
        Assert.AreEqual(3, failed.processed_cursor);
        Assert.AreEqual(5, failed.total_count);

        songDb.Execute("DROP TRIGGER fail_second_song_insert;");
        Lr2FullGenerationBackfillResult retry = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "rollback-retry-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [firstPath, secondPath],
            SongRows = [firstFile, secondFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, retry.FinalStage);
        Assert.AreEqual(2, retry.SongRowProcessedCount);
        Assert.AreEqual("first rollback", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", firstPath));
        Assert.AreEqual("second rollback", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", secondPath));
        LR2SongDBExtended.lr2_full_generation_status completed = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(5, completed.processed_cursor);
    }

    [TestMethod]
    public void BackfillService_ParsesSongRowsWithDetectedUtf8Encoding()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
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
    public void BackfillService_AppliesOnlyCurrentCompatibleChartInfo()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfo");
        Directory.CreateDirectory(songDirectory);
        string currentPath = Path.Combine(songDirectory, "current.bms");
        string stalePath = Path.Combine(songDirectory, "stale.bms");
        string mismatchPath = Path.Combine(songDirectory, "mismatch.bms");
        File.WriteAllText(currentPath, "#TITLE current\r\n");
        File.WriteAllText(stalePath, "#TITLE stale\r\n");
        File.WriteAllText(mismatchPath, "#TITLE mismatch\r\n");
        ChartFileSnapshot currentSnapshot = ChartFileContentReader.ReadSnapshot(currentPath);
        ChartFileSnapshot staleSnapshot = ChartFileContentReader.ReadSnapshot(stalePath);
        ChartFileSnapshot mismatchSnapshot = ChartFileContentReader.ReadSnapshot(mismatchPath);
        TestableBmsFile currentFile = CreateBackfillTestFile(currentPath, currentSnapshot);
        TestableBmsFile staleFile = CreateBackfillTestFile(stalePath, staleSnapshot);
        TestableBmsFile mismatchFile = CreateBackfillTestFile(mismatchPath, mismatchSnapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.InsertOrReplace(CreateChartInfo(currentSnapshot.Sha256, currentSnapshot.Md5, level: 7), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(staleSnapshot.Sha256, staleSnapshot.Md5, level: 9, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(mismatchSnapshot.Sha256, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", level: 11), typeof(LR2SongDBExtended.chart_info));

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "chart-info-current",
            RunId = "chart-info-current",
            SongRows = [currentFile, staleFile, mismatchFile],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(3, result.SongRowProcessedCount);
        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(7, songDb.ExecuteScalar<int>("SELECT COALESCE(level, -1) FROM song WHERE path = ?;", currentPath));
        Assert.AreEqual(-1, songDb.ExecuteScalar<int>("SELECT COALESCE(level, -1) FROM song WHERE path = ?;", stalePath));
        Assert.AreEqual(-1, songDb.ExecuteScalar<int>("SELECT COALESCE(level, -1) FROM song WHERE path = ?;", mismatchPath));
    }

    [TestMethod]
    public void BackfillService_UsesStableMd5ChartInfoFallbackWhenSha256DoesNotMatch()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Md5Fallback");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE md5 fallback\r\n");
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
        songDb.InsertOrReplace(CreateChartInfo(new string('2', 64), snapshot.Md5, level: 22), typeof(LR2SongDBExtended.chart_info));
        songDb.InsertOrReplace(CreateChartInfo(new string('1', 64), snapshot.Md5, level: 11), typeof(LR2SongDBExtended.chart_info));

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "chart-info-md5",
            RunId = "chart-info-md5",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowChartInfoAppliedCount);
        Assert.AreEqual(11, songDb.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
    }

    [TestMethod]
    public void BackfillService_CancelledRequestMarksCancelledStatus()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsException<OperationCanceledException>(() => Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
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
    public void BackfillService_CancelledAfterFolderStageResumesAndCompletes()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "CancelResumeRoot");
        string songDirectory = Path.Combine(rootDirectory, "Song");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE cancel resume\r\n#00111:01\r\n", Encoding.ASCII);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.song>();
        songDb.CreateTable<LR2SongDB.folder>();
        using var cancellation = new CancellationTokenSource();
        const string signature = "cancel-resume-after-folder";
        var progressEvents = new List<Lr2FullGenerationBackfillProgress>();

        Assert.ThrowsException<OperationCanceledException>(() => Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
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
        Lr2FullGenerationBackfillProgress songRowsProgress = progressEvents.First(progress => progress.Stage == "song_rows" && progress.StageTotalCount > 0);
        Assert.AreEqual(2, songRowsProgress.ProcessedCursor);
        Assert.AreEqual(3, songRowsProgress.TotalCount);
        Assert.AreEqual(0, songRowsProgress.StageProcessedCount);
        Assert.AreEqual(1, songRowsProgress.StageTotalCount);

        Lr2FullGenerationBackfillResult resumed = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = signature,
            RunId = "resume-after-cancel-run",
            RootDirectories = [rootDirectory],
            ChartPaths = [chartPath],
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 1, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, resumed.FinalStage);
        Assert.IsNull(resumed.NormalFolderSyncResult);
        Assert.AreEqual(1, resumed.SongRowProcessedCount);
        Assert.AreEqual("cancel resume", songDb.ExecuteScalar<string>("SELECT title FROM song WHERE path = ?;", chartPath));
        LR2SongDBExtended.lr2_full_generation_status completed = songDb.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
        Assert.AreEqual("Completed", completed.status);
        Assert.AreEqual(3, completed.processed_cursor);
        Assert.AreEqual(3, completed.total_count);
    }

    [TestMethod]
    public void BackfillService_UpsertsLr2CompatibilityFactsWithoutReplacingMaintenanceHealth()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string songDirectory = Path.Combine(scope.DirectoryPath, "Lr2Compatibility");
        Directory.CreateDirectory(songDirectory);
        string chartPath = Path.Combine(songDirectory, "chart.bms");
        File.WriteAllText(chartPath, "#TITLE lr2 compatibility\r\n#WAV01 emoji😀.wav\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
        TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "lr2-compatibility",
            RunId = "lr2-compatibility",
            SongRows = [file],
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.SongRowLr2CompatibilityAppliedCount);
        Assert.AreEqual(file.hash, songDb.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(99, songDb.ExecuteScalar<int>("SELECT wav_files_defined FROM maintenance WHERE path = ?;", chartPath));
        Assert.AreEqual(88, songDb.ExecuteScalar<int>("SELECT wav_files_existing FROM maintenance WHERE path = ?;", chartPath));
        int flags = songDb.ExecuteScalar<int>("SELECT lr2_resource_warning_flags FROM maintenance WHERE path = ?;", chartPath);
        Assert.IsTrue((flags & (int)Lr2ResourceWarningFlags.RawPathEncodingUnsupported) != 0);
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_ProjectsLr2CompatibilityWarningsToLiveRows()
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
            TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_live_lr2_compatibility");

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
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversLr2FolderWithoutCharts()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_lr2folder_only");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_full_generation_status row = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Incomplete", row.status);
            Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, row.stage);
            StringAssert.Contains(row.last_error, "unknownRootFolderRows=1");
            Assert.AreEqual(row.total_count, row.processed_cursor);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("入れ子表", lr2Folder.title);
            Assert.AreEqual("song.level = 12", lr2Folder.command);
            Assert.AreEqual(64, lr2Folder.max);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == Path.Combine(rootDirectory, "stale.lr2folder")));
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == outsideLr2FolderPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversLr2FolderFromCustomFolderOutputBase()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_custom_folder_output_base");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Output Folder", lr2Folder.title);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == stalePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversRootCustomFolderOutputAsRootRow()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_root_custom_folder_output_base");

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
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversEnabledLr2BuiltinCustomFolderAsRelativeRootRow()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_lr2_builtin_custom_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\favorite.lr2folder");
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Favorite", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_SkipsDisabledLr2BuiltinCustomFolder()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_lr2_builtin_custom_folder_disabled");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
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
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversBuiltinCourseFolderAsTypeSixRegardlessOfCustomFolderMask()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_lr2_builtin_course_folder");

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
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversBuiltinNewSongFolderOnlyWhenRecentSongExists()
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
            TestableBmsFile file = CreateBackfillTestFile(chartPath, snapshot);

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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_lr2_builtin_newsong_folder");

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
    public void QueueLr2FullGenerationBackfillIfNeeded_DoesNotDiscoverBuiltinLr2RivalFolder()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_lr2_builtin_rival_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsFalse(folders.Any(folder => folder.path == @"LR2files\Rival\rival.lr2folder"));
            Assert.IsFalse(folders.Any(folder => string.Equals(folder.path, lr2FolderPath, StringComparison.OrdinalIgnoreCase)));
            LR2SongDBExtended.lr2_full_generation_status status = verify.Find<LR2SongDBExtended.lr2_full_generation_status>(Lr2FullGenerationStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DiscoversRivalFolderFromNormalScanRootAsExternalFolder()
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

            library.QueueLr2FullGenerationBackfillIfNeeded("test_external_rival_folder");

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
            Assert.AreEqual(Lr2FullGenerationBackfillService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void BackfillService_DoesNotPruneLr2FolderRowsWhenDiscoveredFileCannotBeRead()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
        {
            Signature = "test",
            RunId = "run",
            RootDirectories = [rootDirectory],
            Lr2FolderDiscoveryDirectories = [rootDirectory],
            Lr2FolderPruneDirectories = [rootDirectory],
            Lr2FolderFilePaths = [missingPath],
            Lr2FolderFileDiscoveryComplete = true,
            StartedAtUtc = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.AreEqual(1, result.Lr2FolderFileSyncResult.ItemCount);
        Assert.AreEqual(0, result.Lr2FolderFileSyncResult.DeletedCount);
        Assert.AreEqual(1, songDb.Table<LR2SongDB.folder>().ToList().Count(folder => folder.path == missingPath));
    }

    [TestMethod]
    public void BackfillService_UsesEnumeratedLr2FolderTimestampForGeneratedRow()
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

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
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
        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
    }

    [TestMethod]
    public void BackfillService_UsesEnumeratedDirectoryTimestampForNormalFolderRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        DateTime enumeratedTimestamp = new(2026, 6, 5, 5, 0, 0, DateTimeKind.Utc);
        DateTime liveTimestamp = enumeratedTimestamp.AddMinutes(10);
        Directory.SetLastWriteTimeUtc(rootDirectory, liveTimestamp);
        using var songDb = new LR2SongDBExtended(scope.SongDbPath);
        songDb.CreateTable<LR2SongDB.folder>();

        Lr2FullGenerationBackfillResult result = Lr2FullGenerationBackfillService.Run(songDb, new Lr2FullGenerationBackfillRequest
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
        Assert.AreEqual(Lr2FullGenerationBackfillService.StartupScanBlockersStage, result.FinalStage);
        Assert.AreEqual(1, result.StartupScanDiagnosticResult.DateStaleFolderRowCount);
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
            string directoryPath = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryLr2FullGenerationBackfillTests), Guid.NewGuid().ToString("N"));
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

    private static void InvokeBeginLr2FullGenerationBackfillRequest(BMSLibrary library)
    {
        bool started = InvokeTryBeginLr2FullGenerationBackfillRequest(library, out _);
        Assert.IsTrue(started);
    }

    private static bool InvokeTryBeginLr2FullGenerationBackfillRequest(BMSLibrary library, out int requestVersion)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("TryBeginLr2FullGenerationBackfillRequest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        object[] arguments = [0];
        bool started = (bool)methodInfo.Invoke(library, arguments);
        requestVersion = (int)arguments[0];
        return started;
    }

    private static object InvokeCreateLr2FullGenerationBackfillInput(BMSLibrary library)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("CreateLr2FullGenerationBackfillInput", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return methodInfo.Invoke(library, []);
    }

    private static bool InvokeIsLr2FullGenerationBackfillInputCurrent(BMSLibrary library, object input)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("IsLr2FullGenerationBackfillInputCurrent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (bool)methodInfo.Invoke(library, [input]);
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

    private static TestableBmsFile CreateBackfillTestFile(string path, ChartFileSnapshot snapshot)
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
