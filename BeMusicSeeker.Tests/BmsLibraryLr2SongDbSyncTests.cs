using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.Lr2SongDbSyncTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryLr2SongDbSyncTests
{
    [TestMethod]
    public void ShouldIncludeLr2TextSurface_OnlyWhenLr2ModeEnabled()
    {
        Assert.IsTrue(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
        }));
        Assert.IsFalse(BMSLibrary.ShouldIncludeLr2TextSurface(null));
    }

    [TestMethod]
    public void GetBmsDirectories_IncludesNormalOutputSearchRootsAndExcludesAdditionalAndRootOutputRoots()
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
            string additionalOutputBase = Path.Combine(scope.DirectoryPath, "AdditionalCustomFolderOutput");
            string additionalOutputChild = Path.Combine(additionalOutputBase, "Table");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootCustomFolderOutput");
            string rootOutputChild = Path.Combine(rootOutputBase, "Table");
            Directory.CreateDirectory(bmsRoot);
            Directory.CreateDirectory(nestedCustomOutputLikeDirectory);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(normalOutputChild);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(additionalOutputChild);
            Directory.CreateDirectory(rootOutputBase);
            Directory.CreateDirectory(rootOutputChild);
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot, nestedCustomOutputLikeDirectory, normalOutputBase, normalOutputChild, additionalOutputBase, additionalOutputChild, rootOutputBase, rootOutputChild]
            };

            HashSet<string> directories = [.. InvokeGetBmsDirectories(library).Select(NormalizeDirectory)];

            Assert.IsTrue(directories.Contains(NormalizeDirectory(bmsRoot)));
            Assert.IsTrue(directories.Contains(NormalizeDirectory(nestedCustomOutputLikeDirectory)));
            Assert.IsTrue(directories.Contains(NormalizeDirectory(normalOutputBase)));
            Assert.IsTrue(directories.Contains(NormalizeDirectory(normalOutputChild)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(additionalOutputBase)));
            Assert.IsFalse(directories.Contains(NormalizeDirectory(additionalOutputChild)));
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
            string additionalOutputBase = Path.Combine(scope.DirectoryPath, "AdditionalCustomFolderOutput");
            string additionalOutputChild = Path.Combine(additionalOutputBase, "Table");
            string rootOutputBase = Path.Combine(scope.DirectoryPath, "RootCustomFolderOutput");
            Directory.CreateDirectory(bmsRoot);
            Directory.CreateDirectory(normalOutputBase);
            Directory.CreateDirectory(normalOutputChild);
            Directory.CreateDirectory(additionalOutputBase);
            Directory.CreateDirectory(additionalOutputChild);
            Directory.CreateDirectory(rootOutputBase);
            Settings.Default.LR2CustomFolderOutputBaseDir = normalOutputBase;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBase]);
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot, missingRoot, normalOutputBase, normalOutputChild, additionalOutputBase, additionalOutputChild, rootOutputBase]
            };

            List<string> directories = InvokeGetBmsDirectories(library, out object normalization);

            CollectionAssert.AreEqual(new[] { NormalizeDirectory(bmsRoot), NormalizeDirectory(normalOutputBase), NormalizeDirectory(normalOutputChild) }, directories.Select(NormalizeDirectory).ToArray());
            Assert.AreEqual(7, GetPrivateInt(normalization, "RequestedRootCount"));
            Assert.AreEqual(6, GetPrivateInt(normalization, "ExistingRootCount"));
            Assert.AreEqual(3, GetPrivateInt(normalization, "RootCount"));
            Assert.AreEqual(2, GetPrivateInt(normalization, "ConfiguredCustomOutputRootCount"));
            Assert.AreEqual(3, GetPrivateInt(normalization, "ExcludedCustomOutputRootCount"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_SyncsNormalFolderRowsWhenLr2SongDbSyncEnabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []));

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
    public void ApplyInstalledChartStorageTargets_DoesNotSyncNormalFolderRowsWhenLr2ModeDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = false;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []));

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_PrunesNormalFolderRowsForRemovedBmsWhenLr2SongDbSyncEnabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([keepFile, removeFile], []));
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
    public void ApplyLibraryMutationDelta_DoesNotSyncNormalFolderRowsWhenCatalogWriteFails()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Remove\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.CreateTable<LR2SongDB.song>();
                setup.InsertOrReplace(file, typeof(LR2SongDB.song));
                string escapedPath = chartPath.Replace("'", "''");
                setup.Execute(
                    "CREATE TRIGGER fail_catalog_remove BEFORE DELETE ON song WHEN OLD.path = '"
                    + escapedPath
                    + "' BEGIN SELECT RAISE(ABORT, 'forced catalog mutation failure'); END;");
            }

            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(file));

            Assert.ThrowsException<SQLite.SQLiteException>(() => InvokeApplyLibraryMutationDelta(library, delta));

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(chartPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_RollsBackNormalFolderBatchAndKeepsCatalogCommit()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.CreateTable<LR2SongDB.song>();
                setup.Execute(
                    "CREATE TRIGGER fail_lr2_folder_insert BEFORE INSERT ON folder WHEN NEW.path LIKE '%Pack%' "
                    + "BEGIN SELECT RAISE(ABORT, 'forced normal-folder failure'); END;");
            }

            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []));

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsNotNull(verify.Find<LR2SongDB.song>(chartPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            LR2SongDBExtended.lr2_song_db_sync_status status =
                verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), status.status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLibraryMutationDelta_MovesNormalFolderRowsForMovedBmsWhenLr2SongDbSyncEnabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], []));
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
    public void ApplyInstalledChartStorageTargets_BlocksWhileLr2SongDbSyncIsRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Pack", "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Added\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeBeginLr2SongDbSyncRequest(library);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], [])));
            Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, exception.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyInstalledChartStorageTargets_BlocksRunningSyncWhenAlreadyMarkedRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            InvokeBeginLr2SongDbSyncRequest(library);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => InvokeApplyInstalledChartStorageTargets(library, ChartStorageTargetSet.FromRows([file], [])));
            Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, exception.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SetModeAndCommitToDb_BlocksWhileLr2SongDbSyncIsRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                BMSFiles = []
            };
            InvokeBeginLr2SongDbSyncRequest(library);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeSetModeAndCommitToDb(library, []));
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, exception.InnerException.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public async Task PlaylistTableLevelOverwriteWorkflow_PresentsBlockedWarningWithoutLibraryDialog()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                BMSFiles = []
            };
            InvokeBeginLr2SongDbSyncRequest(library);
            var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
            {
                ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
            };
            var workflow = new PlaylistTableLevelOverwriteWorkflowOwner(dialogs, () => library);

            await workflow.OverwriteAsync(new BMSTable());

            Assert.IsNotNull(dialogs.LastMessageRequest);
            Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, dialogs.LastMessageRequest.MessageBoxText);
            Assert.AreEqual(Resources.MessageBoxTitle_Warning, dialogs.LastMessageRequest.Caption);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DoesNotQueueWhenLr2ModeDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = false;
            ResetLr2FolderDiscoverySettings();
            var library = new TestBmsLibrary(scope.SongDbPath);
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };

            Lr2SongDbSyncStatusSnapshot snapshot = library.QueueLr2SongDbSync("test_l2_mode_disabled");

            Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, snapshot.Status);
            Assert.AreEqual(1, library.Lr2SongDbSyncStatusVersion);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.NotNeeded, library.GetLr2SongDbSyncStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(0, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void FileDiffNormalFolderSyncFailureMarksLr2SongDbSyncIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var result = new SongTableFileCheckResult
            {
                Lr2NormalFolderSyncFailed = true,
                Lr2NormalFolderSyncFailureReason = "db locked"
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(library, options, result);

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_normal_folder_file_diff_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_normal_folder_file_diff_sync_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void MutationNormalFolderSyncFailureMarksLr2SongDbSyncIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
                library,
                options,
                stage: "lr2_normal_folder_mutation_sync_failed",
                detail: "lr2_normal_folder_mutation_sync_failed: db locked",
                logReason: "lr2_normal_folder_mutation_sync_failed");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_normal_folder_mutation_sync_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SongDbWriteFailureMarksLr2SongDbSyncIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
                library,
                options,
                stage: "lr2_song_db_test_write_failed",
                detail: "lr2_song_db_test_write_failed: db locked",
                logReason: "lr2_song_db_test_write_failed");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_test_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_test_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void FileDiffSongDbWriteFailureMarksLr2SongDbSyncIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeMarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure(
                library,
                options,
                new InvalidOperationException("db locked"),
                "reload_file_diff");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_file_diff_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_file_diff_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void MaintenanceSongDbWriteFailureMarksLr2SongDbSyncIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    signature: "previous",
                    runId: "completed",
                    totalCount: 1,
                    nowUtc: DateTime.UtcNow);
            }

            GetCatalogMutationOwner(library).PublishCatalogWriteFailureFactBestEffort(
                new CatalogWriteFailureFact(
                    "song_db_write",
                    "lr2_song_db_maintenance_write_failed",
                    "manual_rescan_all_owned",
                    new InvalidOperationException("db locked")));

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete.ToString(), row.status);
            Assert.AreEqual("lr2_song_db_maintenance_write_failed", row.stage);
            StringAssert.Contains(row.last_error, "db locked");
            Lr2SongDbSyncStatusSnapshot snapshot = library.GetLr2SongDbSyncStatusSnapshot();
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.Status);
            Assert.AreEqual("lr2_song_db_maintenance_write_failed", snapshot.Stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PlaylistLr2FolderSynchronization_BlocksWhileLr2SongDbSyncIsRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            var library = new TestBmsLibrary(scope.SongDbPath);
            InvokeBeginLr2SongDbSyncRequest(library);

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                    "playlist_lr2folder_sync",
                    new Lr2FolderFileDbSyncRequest()));

            Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, exception.Message);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void Lr2MutationSequence_BlocksCatalogAndPlaylistWritersUntilReleased()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string chartPath = Path.Combine(rootDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Sequence\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = CreateSyncTestFile(chartPath, ChartFileContentReader.ReadSnapshot(chartPath));
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }

            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
            IDisposable sequence = owner.EnterLr2MutationSequence();
            using var catalogReady = new ManualResetEventSlim(false);
            using var playlistReady = new ManualResetEventSlim(false);
            using var catalogCallStarted = new ManualResetEventSlim(false);
            using var playlistCallStarted = new ManualResetEventSlim(false);
            using var start = new ManualResetEventSlim(false);
            Task catalogWriter = null!;
            Task playlistWriter = null!;
            try
            {
                catalogWriter = Task.Factory.StartNew(
                    () =>
                    {
                        catalogReady.Set();
                        start.Wait();
                        catalogCallStarted.Set();
                        InvokeApplyInstalledChartStorageTargets(
                            library,
                            ChartStorageTargetSet.FromRows([file], []));
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                playlistWriter = Task.Factory.StartNew(
                    () =>
                    {
                        playlistReady.Set();
                        start.Wait();
                        playlistCallStarted.Set();
                        library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                            "playlist_lr2folder_sync",
                            new Lr2FolderFileDbSyncRequest
                            {
                                ScopeDirectories = [rootDirectory],
                                DirectoryRowScopeDirectories = [rootDirectory],
                                DirectoryRowGenerationScopeDirectories = [rootDirectory],
                                DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(DateTime.UtcNow),
                                GeneratedAtUtc = DateTime.UtcNow,
                                AllowPrune = true
                            });
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                Assert.IsTrue(catalogReady.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(playlistReady.Wait(TimeSpan.FromSeconds(5)));
                start.Set();
                Assert.IsTrue(catalogCallStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(playlistCallStarted.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(Task.WhenAny(catalogWriter, playlistWriter).Wait(TimeSpan.FromSeconds(1)));
            }
            finally
            {
                sequence.Dispose();
                start.Set();
                if (catalogWriter != null && playlistWriter != null)
                {
                    Assert.IsTrue(Task.WhenAll(catalogWriter, playlistWriter).Wait(TimeSpan.FromSeconds(30)));
                }
            }

            Assert.IsFalse(catalogWriter.IsFaulted, catalogWriter.Exception?.ToString());
            Assert.IsFalse(playlistWriter.IsFaulted, playlistWriter.Exception?.ToString());
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PlaylistLr2FolderSynchronization_CommitsFolderRowsThroughOwner()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 7, 18, 4, 5, 6, DateTimeKind.Utc);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath);

            Lr2FolderFileDbSyncResult result = library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                "playlist_lr2folder_sync",
                new Lr2FolderFileDbSyncRequest
                {
                    Items =
                    [
                        new Lr2FolderFileSyncItem
                        {
                            FilePath = filePath,
                            LastWriteTimeUtc = timestamp,
                            Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Owner sync"])
                        }
                    ],
                    ScopeDirectories = [outputDirectory],
                    DirectoryRowScopeDirectories = [outputDirectory],
                    DirectoryRowGenerationScopeDirectories = [outputDirectory],
                    DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp),
                    GeneratedAtUtc = timestamp,
                    AllowPrune = true
                });

            Assert.IsTrue(result.HasChanges);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsNotNull(verify.Find<LR2SongDB.folder>(filePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PlaylistLr2FolderSynchronization_BlocksWhilePreparationIsInProgress()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 7, 18, 4, 5, 6, DateTimeKind.Utc);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath);
            BMSLibrary.Lr2SynchronizationOwner owner = GetLr2SynchronizationOwner(library);
            owner.PreparationInProgress = true;

            try
            {
                Assert.IsTrue(owner.TryBlockMutation("test_preparation_mutation", showMessage: false));
                InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                    () => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                        "playlist_lr2folder_sync",
                        new Lr2FolderFileDbSyncRequest
                        {
                            Items =
                            [
                                new Lr2FolderFileSyncItem
                                {
                                    FilePath = filePath,
                                    LastWriteTimeUtc = timestamp,
                                    Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Preparation owner"])
                                }
                            ],
                            ScopeDirectories = [outputDirectory],
                            DirectoryRowScopeDirectories = [outputDirectory],
                            DirectoryRowGenerationScopeDirectories = [outputDirectory],
                            DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp),
                            GeneratedAtUtc = timestamp,
                            AllowPrune = true
                        }));

                Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, exception.Message);
            }
            finally
            {
                owner.PreparationInProgress = false;
            }
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PlaylistLr2FolderSynchronization_AllowsThePreparationOwnedReservation()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string filePath = Path.Combine(outputDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 7, 18, 4, 5, 6, DateTimeKind.Utc);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath);
            Lr2FolderFileDbSyncResult synchronizedResult = null!;

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_preparation_owned_playlist",
                () =>
                {
                    synchronizedResult = library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                        "playlist_lr2folder_sync",
                        new Lr2FolderFileDbSyncRequest
                        {
                            Items =
                            [
                                new Lr2FolderFileSyncItem
                                {
                                    FilePath = filePath,
                                    LastWriteTimeUtc = timestamp,
                                    Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Preparation owner"])
                                }
                            ],
                            ScopeDirectories = [outputDirectory],
                            DirectoryRowScopeDirectories = [outputDirectory],
                            DirectoryRowGenerationScopeDirectories = [outputDirectory],
                            DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp),
                            GeneratedAtUtc = timestamp,
                            AllowPrune = true
                        });
                    return Lr2SongDbSyncPreparedDataSurface.Empty;
                }));

            Assert.IsTrue(synchronizedResult?.HasChanges == true);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsNotNull(verify.Find<LR2SongDB.folder>(filePath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PlaylistLr2FolderSynchronization_PublishesFailureStatusAndRethrowsOriginalException()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string nestedDirectory = Path.Combine(outputDirectory, "Nested");
            Directory.CreateDirectory(nestedDirectory);
            string filePath = Path.Combine(nestedDirectory, "0000.lr2folder");
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath);
            Exception originalException = new InvalidOperationException("forced playlist folder failure");

            InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(
                () => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                    "playlist_lr2folder_sync",
                    new Lr2FolderFileDbSyncRequest
                    {
                        Items =
                        [
                            new Lr2FolderFileSyncItem
                            {
                                FilePath = filePath,
                                LastWriteTimeUtc = DateTime.UtcNow,
                                Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Failure"])
                            }
                        ],
                        ScopeDirectories = [outputDirectory],
                        DirectoryRowScopeDirectories = [outputDirectory],
                        DirectoryRowGenerationScopeDirectories = [outputDirectory],
                        DirectoryMetadataResolver = _ => throw originalException,
                        AllowPrune = true
                    }));

            Assert.AreSame(originalException, exception);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("lr2_playlist_lr2folder_sync_failed", row.stage);
            StringAssert.Contains(row.last_error, "forced playlist folder failure");
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void PlaylistLr2FolderSynchronization_RollsBackWriterFailureAfterPartialPlan()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputDirectory = Path.Combine(scope.DirectoryPath, "Output");
            Directory.CreateDirectory(outputDirectory);
            string firstFilePath = Path.Combine(outputDirectory, "0001.lr2folder");
            string failingFilePath = Path.Combine(outputDirectory, "0002.lr2folder");
            DateTime timestamp = new(2026, 7, 18, 4, 5, 6, DateTimeKind.Utc);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.Execute(
                    "CREATE TRIGGER fail_lr2_folder_insert AFTER INSERT ON folder WHEN NEW.path = '"
                    + failingFilePath.Replace("'", "''")
                    + "' BEGIN SELECT RAISE(ABORT, 'forced folder insert failure'); END;");
            }
            var library = new TestBmsLibrary(scope.SongDbPath);

            SQLite.SQLiteException exception = Assert.ThrowsException<SQLite.SQLiteException>(
                () => library.Lr2PlaylistFolderSynchronization.SyncPlaylistLr2FolderFileRows(
                    "playlist_lr2folder_sync",
                    new Lr2FolderFileDbSyncRequest
                    {
                        Items =
                        [
                            new Lr2FolderFileSyncItem
                            {
                                FilePath = firstFilePath,
                                LastWriteTimeUtc = timestamp,
                                Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE First"])
                            },
                            new Lr2FolderFileSyncItem
                            {
                                FilePath = failingFilePath,
                                LastWriteTimeUtc = timestamp,
                                Definition = Lr2FolderFileProjection.ParseDefinition(["#TITLE Failing"])
                            }
                        ],
                        ScopeDirectories = [outputDirectory],
                        DirectoryRowScopeDirectories = [outputDirectory],
                        DirectoryRowGenerationScopeDirectories = [outputDirectory],
                        DirectoryMetadataResolver = _ => new Lr2FolderDirectoryMetadata(timestamp),
                        GeneratedAtUtc = timestamp,
                        AllowPrune = true
                    }));

            Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Message));
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count());
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("lr2_playlist_lr2folder_sync_failed", row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DoesNotQueueAgainWhileRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };
            InvokeBeginLr2SongDbSyncRequest(library);

            Lr2SongDbSyncStatusSnapshot snapshot = library.QueueLr2SongDbSync("test_running");

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, snapshot.Status);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Running, library.GetLr2SongDbSyncStatusSnapshot().Status);
            Assert.IsFalse(queued);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsTrue(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DoesNotStartFromIncompleteWhenDisallowed()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            var library = new TestBmsLibrary(scope.SongDbPath);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkIncomplete(
                    setup,
                    Lr2SongDbSyncSignatureBuilder.Build(new BmsLibraryOptionsSnapshot
                    {
                        OperationModeLR2DB = true,
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

            Lr2SongDbSyncStatusSnapshot snapshot = library.QueueLr2SongDbSync(
                "test_settings_scoped_failure",
                force: false,
                allowIncompleteToQueue: false);

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Incomplete, snapshot.StoredStatus);
            Assert.IsFalse(queued);
            Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_RunsPrepareBeforeMarkingSyncRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory]
            };
            library.StartupBackgroundTaskScheduler = delegate
            {
                return true;
            };
            bool prepareCalled = false;

            Lr2SongDbSyncStatusSnapshot snapshot = library.QueueLr2SongDbSync(
                "test_prepare_order",
                force: false,
                () =>
                {
                    prepareCalled = true;
                    Assert.IsFalse(library.Lr2SongDbSyncRunning);
                    Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
                    return Lr2SongDbSyncPreparedDataSurface.Empty;
                });

            Assert.IsTrue(prepareCalled);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsTrue(library.Lr2SongDbSyncRunning);
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
            var library = new TestBmsLibrary(scope.SongDbPath);

            library.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows("test_builtin_scope");

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
            ResetLr2FolderDiscoverySettings();
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string randomDirectory = Path.Combine(lr2Root, "LR2files", "CustomFolder", "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            Settings.Default.LR2RootPath = lr2Root;
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Random Folder Info", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(Path.Combine(randomDirectory, "random.lr2folder"), "#TITLE Random", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(lr2Root, customFolderMask: 0x1, titleFlashHours: 24, Path.Combine(scope.DirectoryPath, "BMS"));
            BmsLibraryOptionsSnapshot options = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2Root,
                LR2CustomFolderOutputBaseDir = string.Empty,
                LR2CustomFolderAdditionalOutputBaseDirs = [],
                LR2CustomFolderOutputBaseDirRootType = string.Empty
            };
            var library = new TestBmsLibrary(
                scope.SongDbPath,
                () => config,
                null,
                null,
                () => options);

            Lr2SongDbSyncPreparedDataSurface surface = library.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows("test_builtin_folderinfo");

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
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            Directory.CreateDirectory(tableDirectory);
            string folderInfoPath = Path.Combine(tableDirectory, "folderinfo.txt");
            string lr2FolderPath = Path.Combine(tableDirectory, "external.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(folderInfoPath, "#TITLE Surface Table", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(lr2FolderPath, "#TITLE External Folder", Encoding.GetEncoding("shift_jis"));
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var library = new TestBmsLibrary(
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
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
    public void ApplyLr2FolderFileDiffSync_RepairsMissingParentRowForPreservedExternalLr2Folder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string categoryDirectory = Path.Combine(rootDirectory, "#minbp");
            string tableDirectory = Path.Combine(categoryDirectory, "InsaneTable");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Be Reparsed", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(lr2FolderPath, timestamp);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Lr2FolderPath.ToFolderPath(categoryDirectory),
                    title = "#minbp",
                    type = 1,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory),
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp),
                    adddate = 12345
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = lr2FolderPath,
                    title = "Preserved External Folder",
                    type = 2,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory),
                    date = Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp),
                    adddate = 23456
                }, typeof(LR2SongDB.folder));
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "test_lr2folder_file_diff_preserved_parent_repair");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> rows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder rootRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(rootDirectory));
            Assert.AreEqual(1, rootRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, rootRow.parent);
            LR2SongDB.folder categoryRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(categoryDirectory));
            Assert.AreEqual(1, categoryRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rootDirectory), categoryRow.parent);
            LR2SongDB.folder tableRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("InsaneTable", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(categoryDirectory), tableRow.parent);
            LR2SongDB.folder lr2Folder = rows.Single(row => row.path == lr2FolderPath);
            Assert.AreEqual("Preserved External Folder", lr2Folder.title);
            Assert.AreEqual(23456, lr2Folder.adddate);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_GeneratesSearchRootChildParentRowUnderSearchRoot()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string bmsContainerDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string searchRootDirectory = Path.Combine(bmsContainerDirectory, "#minbp");
            string tableDirectory = Path.Combine(searchRootDirectory, "InsaneTable");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(lr2FolderPath, "#TITLE External Folder", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(lr2FolderPath, timestamp);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [searchRootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [searchRootDirectory],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [searchRootDirectory], fileCheckResult, "test_lr2folder_file_diff_search_root_child_parent");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> rows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder searchRootRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(searchRootDirectory));
            Assert.AreEqual(1, searchRootRow.type);
            Assert.AreEqual("#minbp", searchRootRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, searchRootRow.parent);
            LR2SongDB.folder tableRow = rows.Single(row => row.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("InsaneTable", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(searchRootDirectory), tableRow.parent);
            LR2SongDB.folder lr2Folder = rows.Single(row => row.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("External Folder", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_InitializePrunesExternalRowsAndLeavesManagedOutputRowsToCustomFolderRepair()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "ExternalTable");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(managedDirectory);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            string currentPath = Path.Combine(tableDirectory, "current.lr2folder");
            string stalePath = Path.Combine(tableDirectory, "stale.lr2folder");
            string managedCurrentPath = Path.Combine(managedDirectory, "0000.lr2folder");
            string managedStalePath = Path.Combine(managedDirectory, "stale_external.lr2folder");
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
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
            Assert.AreEqual(0, fileCheckResult.Lr2ScanLr2FolderAppManagedExactFileCount);
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

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_RefiltersAlreadyFilteredCandidatesAgainstCurrentManagedScope()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string externalDirectory = Path.Combine(rootDirectory, "ExternalTable");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string managedDirectory = Path.Combine(outputBase, "ManagedTable");
            Directory.CreateDirectory(externalDirectory);
            Directory.CreateDirectory(managedDirectory);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            string externalPath = Path.Combine(externalDirectory, "current.lr2folder");
            string managedPath = Path.Combine(managedDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(externalPath, "#TITLE Current", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(managedPath, "#TITLE Managed Physical", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(externalPath, timestamp);
            File.SetLastWriteTimeUtc(managedPath, timestamp);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9102,
                    name = "ManagedTable",
                    symbol = "M",
                    Output_dir = "ManagedTable"
                }, typeof(LR2SongDBExtended.playlist));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = managedPath,
                    title = "Managed Existing",
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [externalPath, managedPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [externalPath] = new RootFileEnumerationEntry(externalPath, timestamp),
                    [managedPath] = new RootFileEnumerationEntry(managedPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true,
                Lr2ScanLr2FolderCandidatesAlreadyFiltered = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "initialize");

            CollectionAssert.AreEqual(new[] { externalPath }, fileCheckResult.Lr2ScanLr2FolderFilePaths.ToArray());
            Assert.IsFalse(fileCheckResult.Lr2ScanLr2FolderFileEntries.ContainsKey(managedPath));
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder managedCurrent = verify.Table<LR2SongDB.folder>().Single(row => row.path == managedPath);
            Assert.AreEqual("Managed Existing", managedCurrent.title);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void ApplyLr2FolderFileDiffSync_ReenumeratesWhenAlreadyFilteredManagedScopeShrinks()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string oldManagedDirectory = Path.Combine(outputBase, "OldManagedTable");
            Directory.CreateDirectory(oldManagedDirectory);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            string oldManagedPath = Path.Combine(oldManagedDirectory, "0000.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(oldManagedPath, "#TITLE Former Managed", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(oldManagedPath, timestamp);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = oldManagedPath,
                    title = "Former Managed",
                    type = 2,
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true,
                Lr2ScanLr2FolderCandidatesAlreadyFiltered = true,
                Lr2ScanLr2FolderAppManagedScopeDirectories = [oldManagedDirectory],
                Lr2ScanLr2FolderAppManagedScopeDirectoryCount = 1
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "initialize");

            CollectionAssert.Contains(fileCheckResult.Lr2ScanLr2FolderFilePaths.ToList(), oldManagedPath);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldManagedPath));
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
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
    public void ApplyLr2FolderFileDiffSync_KeepsPhysicalLr2FoldersWhenNoManagedPlaylistScopeExists()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string lr2FolderPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Read", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDBExtended.playlist>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            var fileCheckResult = new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, new DateTime(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            };

            InvokeApplyLr2FolderFileDiffSync(library, options, [rootDirectory], fileCheckResult, "initialize");

            Assert.IsTrue(fileCheckResult.Lr2ScanLr2FolderCandidatesAlreadyFiltered);
            CollectionAssert.Contains(fileCheckResult.Lr2ScanLr2FolderFilePaths.ToList(), lr2FolderPath);
            Assert.IsTrue(fileCheckResult.Lr2ScanLr2FolderFileDiscoveryComplete);
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
        string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
        Directory.CreateDirectory(rootDirectory);
        string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
        string previousLr2Root = Settings.Default.LR2RootPath;
        Settings.Default.LR2RootPath = lr2Root;
        try
        {
            string builtinRoot = Path.Combine(lr2Root, "LR2files", "CustomFolder");
            string randomDirectory = Path.Combine(builtinRoot, "RANDOM");
            Directory.CreateDirectory(randomDirectory);
            string folderInfoPath = Path.Combine(randomDirectory, "folderinfo.txt");
            string lr2FolderPath = Path.Combine(randomDirectory, "random.lr2folder");
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            File.WriteAllText(folderInfoPath, "#TITLE Builtin Random", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(lr2FolderPath, "#TITLE Random Folder", Encoding.GetEncoding("shift_jis"));
            File.SetLastWriteTimeUtc(folderInfoPath, timestamp);
            File.SetLastWriteTimeUtc(lr2FolderPath, timestamp);
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2Root
            };
            var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, () => options)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
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
            Settings.Default.LR2RootPath = previousLr2Root;
        }
    }

    [TestMethod]
    public void SyncLr2BuiltinCustomFolderRows_PrunesRelativeBuiltinRowsWhenSourceDirectoryMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath);

            library.Lr2Synchronization.SyncLr2BuiltinCustomFolderRows("test_builtin_missing_source");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsNull(verify.Table<LR2SongDB.folder>().ToList().SingleOrDefault(row => row.path == staleBuiltinPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryBeginLr2SongDbSyncRequest_DoesNotAdvanceVersionWhenAlreadyRunning()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            var library = new TestBmsLibrary(scope.SongDbPath);

            library.StartupBackgroundTaskScheduler = (_, _, _, _) => true;
            library.QueueLr2SongDbSync("test_request_version", force: true);
            int firstVersion = library.Lr2SongDbSyncRequestedVersion;
            Assert.AreEqual(1, firstVersion);
            library.QueueLr2SongDbSync("test_request_version_duplicate", force: true);
            int secondVersion = library.Lr2SongDbSyncRequestedVersion;
            Assert.AreEqual(firstVersion, secondVersion);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.IsTrue(library.Lr2SongDbSyncRunning);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void IsLr2SongDbSyncInputCurrent_UsesSnapshotSurfaceAndDetectsRootChange()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            object input = InvokeCreateLr2SongDbSyncInput(library);

            Assert.IsTrue(InvokeIsLr2SongDbSyncInputCurrent(library, input));
            File.WriteAllText(Path.Combine(rootDirectory, "folderinfo.txt"), "#TITLE Root Title");
            File.WriteAllText(Path.Combine(rootDirectory, "custom.lr2folder"), "#TITLE Custom");
            File.WriteAllText(Path.Combine(rootDirectory, "readme.txt"), "text group");
            Assert.IsTrue(InvokeIsLr2SongDbSyncInputCurrent(library, input));

            string otherRootDirectory = Path.Combine(scope.DirectoryPath, "OtherBMS");
            Directory.CreateDirectory(otherRootDirectory);
            library.SearchTargets = [otherRootDirectory];

            Assert.IsFalse(InvokeIsLr2SongDbSyncInputCurrent(library, input));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void IsLr2SongDbSyncInputCurrent_DetectsNewerScanSurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string chartDirectory = Path.Combine(rootDirectory, "Pack");
            Directory.CreateDirectory(chartDirectory);
            string folderInfoPath = Path.Combine(chartDirectory, "folderinfo.txt");
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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
            object input = InvokeCreateLr2SongDbSyncInput(library);

            Assert.IsTrue(InvokeIsLr2SongDbSyncInputCurrent(library, input));

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            Assert.IsFalse(InvokeIsLr2SongDbSyncInputCurrent(library, input));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CaptureLr2SongDbSyncScanSurface_SkipsWhenLr2FolderSurfaceMissing()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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
            object capturedInput = InvokeCreateLr2SongDbSyncInput(library);
            Assert.IsTrue(GetInputInt(capturedInput, "ScanSurfaceGeneration") > 0);

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanTextFileDirectories = []
            });

            object input = InvokeCreateLr2SongDbSyncInput(library);

            Assert.AreEqual(0, GetInputInt(input, "ScanSurfaceGeneration"));
            Assert.IsFalse(InvokeIsLr2SongDbSyncInputCurrent(library, capturedInput));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CaptureLr2SongDbSyncScanSurface_PreservesPreviousSurfaceWhenNormalFolderSyncUnapplied()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            string folderInfoPath = Path.Combine(rootDirectory, "folderinfo.txt");
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            DateTime previousTimestamp = new(2026, 6, 8, 1, 0, 0, DateTimeKind.Utc);
            DateTime failedTimestamp = new(2026, 6, 8, 1, 5, 0, DateTimeKind.Utc);
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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
            object previousInput = InvokeCreateLr2SongDbSyncInput(library);
            int previousGeneration = GetInputInt(previousInput, "ScanSurfaceGeneration");

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            object inputAfterFailure = InvokeCreateLr2SongDbSyncInput(library);
            Assert.AreEqual(previousGeneration, GetInputInt(inputAfterFailure, "ScanSurfaceGeneration"));
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(inputAfterFailure, "FolderInfoFileEntries");
            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out RootFileEnumerationEntry entry));
            Assert.AreEqual(previousTimestamp, entry.LastWriteTimeUtc);

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            object inputAfterMissingMetadata = InvokeCreateLr2SongDbSyncInput(library);
            Assert.AreEqual(previousGeneration, GetInputInt(inputAfterMissingMetadata, "ScanSurfaceGeneration"));
            entries = GetInputEntryMap(inputAfterMissingMetadata, "FolderInfoFileEntries");
            Assert.IsTrue(entries.TryGetValue(folderInfoPath, out entry));
            Assert.AreEqual(previousTimestamp, entry.LastWriteTimeUtc);

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            object inputAfterFolderInfoReadFailure = InvokeCreateLr2SongDbSyncInput(library);
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
    public void QueueLr2SongDbSync_MergesPreparedLr2FolderSurfaceAfterPreparedOutput()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            string staleLr2FolderPath = Path.Combine(outputBase, "stale.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            string folderInfoPath = Path.Combine(rootDirectory, "folderinfo.txt");
            File.WriteAllText(folderInfoPath, "#TITLE Surface Root", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_lr2folder_surface",
                () =>
                {
                    Directory.CreateDirectory(outputBase);
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                }));
            object input = InvokeCreateLr2SongDbSyncInput(library);
            int appliedScanSurfaceGeneration = GetInputInt(input, "ScanSurfaceGeneration");
            Assert.IsTrue(appliedScanSurfaceGeneration > 0);
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

            library.QueueLr2SongDbSync(
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
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2SongDbSyncInput_ReusesScanSurfaceDirectoryEntries()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            DateTime surfaceTimestamp = new(2026, 6, 7, 4, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void CreateLr2SongDbSyncInput_ReusesScanSurfaceDirectoryEntriesForLr2FolderParents()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string tableDirectory = Path.Combine(rootDirectory, "Table");
            string lr2FolderPath = Path.Combine(tableDirectory, "select.lr2folder");
            Directory.CreateDirectory(tableDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Table", Encoding.GetEncoding("shift_jis"));
            DateTime surfaceTimestamp = new(2026, 6, 7, 4, 0, 0, DateTimeKind.Utc);
            DateTime liveTimestamp = new(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void TryRunLr2SongDbSyncDataPreparation_UsesPreparedSurfaceWithoutCapturedScanSurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "#BeMusicSeekerOutput");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsFalse(InvokeHasLr2SongDbSyncPreparedDataSurface(library));
            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_lr2folder_without_scan_surface",
                () =>
                {
                    Directory.CreateDirectory(outputBase);
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                }));
            Assert.IsTrue(InvokeHasLr2SongDbSyncPreparedDataSurface(library));
            object input = InvokeCreateLr2SongDbSyncInput(library);

            Assert.AreEqual(0, GetInputInt(input, "ScanSurfaceGeneration"));
            CollectionAssert.Contains(GetInputStringList(input, "Lr2FolderDiscoveryDirectories").ToList(), outputBase);
            CollectionAssert.Contains(GetInputStringList(input, "Lr2FolderFilePaths").ToList(), lr2FolderPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
            Assert.IsFalse(InvokeHasLr2SongDbSyncPreparedDataSurface(library));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2SongDbSyncDataPreparation_UsesPreparedDirectoryEntries()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_lr2folder_directory_entries",
                () => CreatePreparedLr2FolderSurface(
                    outputBase,
                    lr2FolderPath,
                    new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                    {
                        [outputBase] = new RootFileEnumerationEntry(outputBase, preparedTimestamp)
                    })));
            Directory.SetLastWriteTimeUtc(outputBase, liveTimestamp);

            object input = InvokeCreateLr2SongDbSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "DirectoryEntries");

            string outputKey = Lr2FolderPath.NormalizeDirectoryPath(outputBase);
            Assert.IsTrue(entries.TryGetValue(outputKey, out RootFileEnumerationEntry entry));
            Assert.AreEqual(preparedTimestamp, entry.LastWriteTimeUtc);
            Assert.IsFalse(InvokeHasLr2SongDbSyncPreparedDataSurface(library));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2SongDbSyncDataPreparation_KeepsMetadataOnlySurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            DateTime preparedTimestamp = new(2026, 6, 8, 3, 0, 0, DateTimeKind.Utc);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_metadata_only",
                () => new Lr2SongDbSyncPreparedDataSurface(
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
            Assert.IsTrue(InvokeHasLr2SongDbSyncPreparedDataSurface(library));

            object input = InvokeCreateLr2SongDbSyncInput(library);
            IReadOnlyDictionary<string, RootFileEnumerationEntry> entries = GetInputEntryMap(input, "DirectoryEntries");

            string rootKey = Lr2FolderPath.NormalizeDirectoryPath(rootDirectory);
            Assert.IsTrue(entries.TryGetValue(rootKey, out RootFileEnumerationEntry entry));
            Assert.AreEqual(preparedTimestamp, entry.LastWriteTimeUtc);
            Assert.IsFalse(InvokeHasLr2SongDbSyncPreparedDataSurface(library));
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

        Lr2SongDbSyncPreparedDataSurface merged = Lr2SongDbSyncPreparedDataSurface.Merge(
            CreatePreparedLr2FolderSurface(
                playlistDirectory,
                playlistLr2FolderPath,
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [playlistDirectory] = new RootFileEnumerationEntry(playlistDirectory, playlistTimestamp)
                }),
            new Lr2SongDbSyncPreparedDataSurface(
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
        IReadOnlyList<string> result = Lr2SongDbSyncInputSurfaceHelper.CreateLr2TextMetadataSourceDirectoriesOutsideRoots(
            new[] { parentDirectory, rootDirectory, childDirectory, outsideDirectory },
            new[] { rootDirectory });

        CollectionAssert.AreEqual(
            new[] { Lr2FolderPath.NormalizeDirectoryPath(outsideDirectory) },
            result.ToArray());
    }

    [TestMethod]
    public void TryRunLr2SongDbSyncDataPreparation_OverlaysPreparedFolderInfoSurface()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
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

            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void CreateLr2SongDbSyncInput_ReplacesOnlyPreparedScopeTextFileDirectories()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_text_dirs_scope_boundary",
                () => CreatePreparedLr2FolderSurface(
                    preparedDirectory,
                    lr2FolderPath,
                    CreateDirectoryEntryMap(preparedDirectory),
                    textFileDirectories: [preparedDirectory])));

            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void CreateLr2SongDbSyncInput_ExcludesPreparedManagedOutputFilesAfterScanSurfaceMerge()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9303,
                    name = "ManagedTable",
                    symbol = "MT",
                    Output_dir = "ManagedTable",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                }, typeof(LR2SongDBExtended.playlist));
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, outputDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });
            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepared_managed_output_scan_surface",
                () => CreatePreparedLr2FolderSurface(outputDirectory, managedPath)));

            object input = InvokeCreateLr2SongDbSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            CollectionAssert.DoesNotContain(lr2FolderFilePaths, managedPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void TryRunLr2SongDbSyncDataPreparation_DoesNotPromoteOldScanSurfaceWhenLr2FolderRootsChanged()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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
            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_lr2folder_roots_changed",
                () =>
                {
                    Directory.CreateDirectory(newOutputBase);
                    File.WriteAllText(newLr2FolderPath, "#TITLE New Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(newOutputBase, newLr2FolderPath);
                }));
            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void CreateLr2SongDbSyncInput_KeepsExternalLr2FolderInOutputBaseOutsidePreparedScope()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanLr2FolderDiscoveryDirectories = [outputBase],
                Lr2ScanLr2FolderFilePaths = [externalLr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [externalLr2FolderPath] = new RootFileEnumerationEntry(
                        externalLr2FolderPath,
                        File.GetLastWriteTimeUtc(externalLr2FolderPath))
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_keeps_external_output_base_lr2folder",
                () =>
                {
                    File.WriteAllText(preparedLr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(appOutputDir, preparedLr2FolderPath);
                }));

            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void CaptureLr2SongDbSyncScanSurface_ExcludesManagedOutputCandidates()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);

            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
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

            object input = InvokeCreateLr2SongDbSyncInput(library);
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
    public void CreateLr2SongDbSyncAppManagedOutputScope_ReturnsManagedOutputDirectoriesOnly()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string outputBase = Path.Combine(scope.DirectoryPath, "Output");
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9201,
                    name = "CountParity",
                    symbol = "CP",
                    Output_dir = "CountParity",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.OtherFolder
                }, typeof(LR2SongDBExtended.playlist));
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9201,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "Active A",
                    "Folder A",
                    1.2,
                    0);
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9201,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    "Active B",
                    "Folder B",
                    null,
                    0);
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9201,
                    "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
                    "Active Null Folder",
                    null,
                    null,
                    0);
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9201,
                    "ffffffffffffffffffffffffffffffff",
                    "Active Empty Folder",
                    string.Empty,
                    null,
                    0);
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9201,
                    "cccccccccccccccccccccccccccccccc",
                    "Removed Level",
                    "Removed Folder",
                    12.0,
                    1);
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9201,
                    "dddddddddddddddddddddddddddddddd",
                    "Removed Null",
                    "Removed Null Folder",
                    null,
                    1);
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [scope.DirectoryPath],
                BMSFiles = []
            };

            object outputScope = InvokeCreateLr2SongDbSyncAppManagedOutputScope(library);
            List<string> directories = GetInputStringList(outputScope, "Directories").ToList();
            List<string> filePaths = GetInputStringList(outputScope, "FilePaths").ToList();
            List<string> pruneExcludedPaths = GetInputStringList(outputScope, "PruneExcludedPaths").ToList();

            string outputDirectory = Path.Combine(outputBase, "CountParity");
            CollectionAssert.Contains(directories, outputDirectory);
            Assert.AreEqual(0, filePaths.Count);
            Assert.AreEqual(0, pruneExcludedPaths.Count);
            Assert.IsTrue(GetInputBool(outputScope, "IsComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2SongDbSyncAppManagedOutputScope_UsesOutputDirectoryAsManagedBoundary()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9202,
                    name = "ManagedHierarchy",
                    symbol = "MH",
                    Output_dir = "ManagedHierarchy",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                }, typeof(LR2SongDBExtended.playlist));
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, level, is_removed) VALUES (?, ?, ?, ?, ?, ?);",
                    9202,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "Active A",
                    "st0",
                    1.0,
                    0);
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };

            object outputScope = InvokeCreateLr2SongDbSyncAppManagedOutputScope(library);
            List<string> directories = GetInputStringList(outputScope, "Directories").ToList();
            List<string> pruneExcludedPaths = GetInputStringList(outputScope, "PruneExcludedPaths").ToList();

            string outputDirectory = Path.Combine(outputBase, "ManagedHierarchy");
            CollectionAssert.Contains(directories, outputDirectory);
            Assert.AreEqual(0, pruneExcludedPaths.Count);
            Assert.IsTrue(GetInputBool(outputScope, "IsComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_ClearsPreparedSurfaceWhenFollowupQueueIsCurrentNoop()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(scope.DirectoryPath, "Output");
            string lr2FolderPath = Path.Combine(outputBase, "prepared.lr2folder");
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(outputBase);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                Lr2SongDbSyncStatusService.MarkCompleted(
                    setup,
                    Lr2SongDbSyncSignatureBuilder.Build(options),
                    runId: "already_current",
                    totalCount: 0,
                    nowUtc: DateTime.UtcNow);
            }

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepare_then_noop_queue",
                () =>
                {
                    File.WriteAllText(lr2FolderPath, "#TITLE Prepared Folder", Encoding.GetEncoding("shift_jis"));
                    return CreatePreparedLr2FolderSurface(outputBase, lr2FolderPath);
                }));
            Assert.IsTrue(InvokeHasLr2SongDbSyncPreparedDataSurface(library));

            Lr2SongDbSyncStatusSnapshot snapshot = library.QueueLr2SongDbSync(
                "test_prepare_then_noop_queue",
                force: false,
                allowIncompleteToQueue: false);

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, snapshot.Status);
            Assert.IsFalse(InvokeHasLr2SongDbSyncPreparedDataSurface(library));
            Assert.AreEqual(0, library.Lr2SongDbSyncRequestedVersion);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2SongDbSyncInputWithoutScanSurface_UsesTargetFolderInfoOnly()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
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
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            LR2RootPath = string.Empty,
            LR2CustomFolderOutputBaseDir = string.Empty,
            LR2CustomFolderAdditionalOutputBaseDirs = [],
            LR2CustomFolderOutputBaseDirRootType = string.Empty,
            EnableDownloadLr2IrScoreAndDetectUnsent = false
        };
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            () => null!,
            null,
            "test",
            () => options)
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

        object input = InvokeCreateLr2SongDbSyncInput(library);
        List<string> folderInfoFilePaths = GetInputStringList(input, "FolderInfoFilePaths").ToList();
        List<string> textFileDirectories = GetInputStringList(input, "TextFileDirectories").ToList();

        CollectionAssert.Contains(folderInfoFilePaths, packFolderInfoPath);
        CollectionAssert.DoesNotContain(folderInfoFilePaths, unrelatedFolderInfoPath);
        CollectionAssert.Contains(textFileDirectories, songDirectory);
        CollectionAssert.DoesNotContain(textFileDirectories, unrelatedDirectory);
    }

    [TestMethod]
    public void CreateLr2SongDbSyncInputWithoutScanSurface_ExcludesManagedOutputDirectoryFiles()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string externalPath = Path.Combine(outputDirectory, "external.lr2folder");
            string unmanagedSiblingDirectory = Path.Combine(outputBase, "ExternalTable");
            string unmanagedSiblingPath = Path.Combine(unmanagedSiblingDirectory, "external.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(unmanagedSiblingDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(externalPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(unmanagedSiblingPath, "#TITLE External Sibling", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9301,
                    name = "ManagedTable",
                    symbol = "MT",
                    Output_dir = "ManagedTable",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                }, typeof(LR2SongDBExtended.playlist));
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed) VALUES (?, ?, ?, ?, ?);",
                    9301,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "Active",
                    "Folder A",
                    0);
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            object input = InvokeCreateLr2SongDbSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();
            List<string> pruneDirectories = GetInputStringList(input, "Lr2FolderPruneDirectories").ToList();
            List<string> pruneExcludedDirectories = GetInputStringList(input, "Lr2FolderPruneExcludedDirectories").ToList();

            CollectionAssert.DoesNotContain(lr2FolderFilePaths, managedPath);
            CollectionAssert.DoesNotContain(lr2FolderFilePaths, externalPath);
            CollectionAssert.Contains(lr2FolderFilePaths, unmanagedSiblingPath);
            CollectionAssert.Contains(pruneDirectories, outputBase);
            CollectionAssert.Contains(pruneExcludedDirectories, outputDirectory);
            CollectionAssert.DoesNotContain(pruneExcludedDirectories, outputBase);
            CollectionAssert.DoesNotContain(pruneExcludedDirectories, unmanagedSiblingDirectory);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2SongDbSyncInputWithoutScanSurface_ExcludesPreparedManagedOutputFiles()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = true;
            ResetLr2FolderDiscoverySettings();
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string managedPath = Path.Combine(outputDirectory, "0000.lr2folder");
            string externalPath = Path.Combine(outputDirectory, "external.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(externalPath, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9302,
                    name = "ManagedTable",
                    symbol = "MT",
                    Output_dir = "ManagedTable",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                }, typeof(LR2SongDBExtended.playlist));
                setup.Execute(
                    "INSERT INTO playlist_entry (playlist_id, md5, title, folder, is_removed) VALUES (?, ?, ?, ?, ?);",
                    9302,
                    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    "Active",
                    "Folder A",
                    0);
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_prepared_managed_output",
                () => CreatePreparedLr2FolderSurface(string.Empty, managedPath)));

            object input = InvokeCreateLr2SongDbSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            CollectionAssert.DoesNotContain(lr2FolderFilePaths, managedPath);
            CollectionAssert.DoesNotContain(lr2FolderFilePaths, externalPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2SongDbSyncInputWithoutScanSurface_KeepsPhysicalLr2FoldersWhenNoManagedPlaylistScopeExists()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string lr2FolderPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Read", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDBExtended.playlist>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };

            object input = InvokeCreateLr2SongDbSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, lr2FolderPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void CreateLr2SongDbSyncInput_KeepsScanSurfaceLr2FoldersWhenNoManagedPlaylistScopeExists()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(rootDirectory, "#BeMusicSeekerOutput");
            string outputDirectory = Path.Combine(outputBase, "ManagedTable");
            string lr2FolderPath = Path.Combine(outputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(lr2FolderPath, "#TITLE Should Not Read", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDBExtended.playlist>();
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
            };
            DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [rootDirectory], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanNormalFolderDirectoryPaths = [rootDirectory],
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory),
                Lr2ScanFolderInfoFilePaths = [],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanTextFileDirectories = [rootDirectory],
                Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory, outputBase],
                Lr2ScanLr2FolderFilePaths = [lr2FolderPath],
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, timestamp)
                },
                Lr2ScanLr2FolderFileDiscoveryComplete = true
            });

            object input = InvokeCreateLr2SongDbSyncInput(library);
            List<string> lr2FolderFilePaths = GetInputStringList(input, "Lr2FolderFilePaths").ToList();

            CollectionAssert.Contains(lr2FolderFilePaths, lr2FolderPath);
            Assert.IsTrue(GetInputBool(input, "Lr2FolderFileDiscoveryComplete"));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_RunsLr2SongDbSyncAndMarksCompletedWhenClean()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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

            var library = new TestBmsLibrary(
                scope.SongDbPath,
                null,
                null,
                null,
                null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
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

            // Everything can report a complete result before its index observes files created by this test.
            // Use the startup scan surface so the synchronization contract is exercised against deterministic input.
            InvokeCaptureLr2SongDbSyncScanSurface(
                library,
                new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true },
                [rootDirectory],
                new SongTableFileCheckResult
                {
                    Lr2ScanSurfaceAvailable = true,
                    Lr2ScanNormalFolderDirectoryPaths = [rootDirectory, packDirectory, songDirectory],
                    Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, packDirectory, songDirectory),
                    Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(rootDirectory, packDirectory, songDirectory),
                    Lr2ScanFolderInfoFilePaths = [Path.Combine(packDirectory, "folderinfo.txt")],
                    Lr2ScanFolderInfoFileEntries = CreateFileEntryMap(Path.Combine(packDirectory, "folderinfo.txt")),
                    Lr2ScanTextFileDirectories = [packDirectory, songDirectory],
                    Lr2ScanLr2FolderDiscoveryDirectories = [rootDirectory],
                    Lr2ScanLr2FolderFilePaths = [customFolderPath],
                    Lr2ScanLr2FolderFileEntries = CreateFileEntryMap(customFolderPath),
                    Lr2ScanLr2FolderFileDiscoveryComplete = true
                });

            string queuedName = string.Empty;
            string queuedReason = string.Empty;
            bool stagePublicationObserved = false;
            library.PropertyChanged += (sender, args) =>
            {
                if (string.Equals(args.PropertyName, nameof(BMSLibrary.Lr2SongDbSyncStage), StringComparison.Ordinal))
                {
                    stagePublicationObserved = true;
                }
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                queuedName = name;
                queuedReason = reason;
                work().GetAwaiter().GetResult();
                return true;
            };

            Lr2SongDbSyncStatusSnapshot snapshot = library.QueueLr2SongDbSync("test_enabled");
            TestUiDispatcherHost.Drain();

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, snapshot.Status);
            Assert.AreEqual("lr2_song_db_sync", queuedName);
            Assert.AreEqual("test_enabled", queuedReason);
            Assert.IsTrue(stagePublicationObserved);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
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
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(1, library.Lr2SongDbSyncCompletedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            Assert.AreEqual(row.total_count.GetValueOrDefault(), library.Lr2SongDbSyncTotalCount);
            Assert.AreEqual(row.processed_cursor.GetValueOrDefault(), library.Lr2SongDbSyncProcessedCount);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, library.Lr2SongDbSyncStage);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void Lr2PropertyPublication_CoalescesRepeatedChangesIntoOneUiDrain()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        var scheduler = new QueuedUiScheduler();
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            null,
            null,
            null,
            null,
            scheduler);
        var publishedPropertyNames = new List<string>();
        library.PropertyChanged += (_, args) => publishedPropertyNames.Add(args.PropertyName);
        MethodInfo handler = typeof(BMSLibrary).GetMethod(
            "HandleLr2SynchronizationPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(handler);
        string[] expectedPropertyNames =
        [
            nameof(BMSLibrary.Lr2SongDbSyncStage),
            nameof(BMSLibrary.Lr2SongDbSyncProcessedCount),
            nameof(BMSLibrary.Lr2SongDbSyncTotalCount),
            nameof(BMSLibrary.Lr2SongDbSyncStageProcessedCount),
            nameof(BMSLibrary.Lr2SongDbSyncStageTotalCount)
        ];

        for (int iteration = 0; iteration < 20; iteration++)
        {
            foreach (string propertyName in expectedPropertyNames)
            {
                handler.Invoke(
                    library,
                    [library, new System.ComponentModel.PropertyChangedEventArgs(propertyName)]);
            }
        }

        Assert.AreEqual(1, scheduler.ScheduleCount);
        Assert.AreEqual(1, scheduler.PendingCount);

        scheduler.Drain();

        Assert.AreEqual(0, scheduler.PendingCount);
        CollectionAssert.AreEquivalent(expectedPropertyNames, publishedPropertyNames);
    }

    [TestMethod]
    public void Lr2PropertyPublication_RefillDuringDrainUsesNextUiTurn()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        var scheduler = new QueuedUiScheduler();
        var library = new TestBmsLibrary(
            scope.SongDbPath,
            null,
            null,
            null,
            null,
            scheduler);
        MethodInfo handler = typeof(BMSLibrary).GetMethod(
            "HandleLr2SynchronizationPropertyChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(handler);
        string firstPropertyName = nameof(BMSLibrary.Lr2SongDbSyncStage);
        string refilledPropertyName = nameof(BMSLibrary.Lr2SongDbSyncProcessedCount);
        var publishedPropertyNames = new List<string>();
        bool refilled = false;
        library.PropertyChanged += (_, args) =>
        {
            publishedPropertyNames.Add(args.PropertyName);
            if (!refilled && args.PropertyName == firstPropertyName)
            {
                refilled = true;
                handler.Invoke(
                    library,
                    [library, new System.ComponentModel.PropertyChangedEventArgs(refilledPropertyName)]);
            }
        };
        handler.Invoke(
            library,
            [library, new System.ComponentModel.PropertyChangedEventArgs(firstPropertyName)]);

        scheduler.ExecuteNext();

        CollectionAssert.AreEqual(new[] { firstPropertyName }, publishedPropertyNames);
        Assert.AreEqual(2, scheduler.ScheduleCount);
        Assert.AreEqual(1, scheduler.PendingCount);

        scheduler.ExecuteNext();

        CollectionAssert.AreEqual(
            new[] { firstPropertyName, refilledPropertyName },
            publishedPropertyNames);
        Assert.AreEqual(0, scheduler.PendingCount);
    }

    [TestMethod]
    public void QueueLr2SongDbSync_PreflightCancelMarksDurableCancelledStatus()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(rootDirectory);
            var library = new TestBmsLibrary(
                scope.SongDbPath,
                null,
                null,
                null,
                null,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher))
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            bool cancelRequested = false;
            Func<Task> scheduledWork = null;
            FieldInfo ownerField = typeof(BMSLibrary).GetField(
                "lr2SynchronizationOwner",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(ownerField);
            var synchronizationOwner = (System.ComponentModel.INotifyPropertyChanged)ownerField.GetValue(library);
            System.ComponentModel.PropertyChangedEventHandler cancelAtInputSurface = (_, args) =>
            {
                if (!cancelRequested
                    && string.Equals(
                        args.PropertyName,
                        nameof(BMSLibrary.Lr2SongDbSyncStage),
                        StringComparison.Ordinal)
                    && string.Equals(
                        library.Lr2SongDbSyncStage,
                        "input_surface",
                        StringComparison.Ordinal))
                {
                    cancelRequested = library.CancelLr2SongDbSync("test_preflight_cancel");
                }
            };
            synchronizationOwner.PropertyChanged += cancelAtInputSurface;
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                scheduledWork = work;
                return true;
            };

            library.QueueLr2SongDbSync("test_preflight_cancel", force: true);
            Assert.IsNotNull(scheduledWork);
            try
            {
                scheduledWork().GetAwaiter().GetResult();
            }
            finally
            {
                synchronizationOwner.PropertyChanged -= cancelAtInputSurface;
            }
            TestUiDispatcherHost.Drain();

            Assert.IsTrue(cancelRequested);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Cancelled", row.status);
            Assert.AreEqual("input_surface", row.stage);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DefaultsDifficultyWithoutUsingStaleRowsAsAnchor()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Undefined Difficulty\r\n#PLAYLEVEL 1\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            library.QueueLr2SongDbSync("test_difficulty_normalization");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path = ?;", chartPath));
            Assert.AreEqual(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path LIKE '%stale.bms';"));
            Assert.AreEqual(2, verify.ExecuteScalar<int>("SELECT difficulty FROM song WHERE path = ?;", chartPath));
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DoesNotQueueSecondSyncWhenCompletedStatusIsCurrent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            Lr2SongDbSyncStatusSnapshot first = library.QueueLr2SongDbSync("test_first");
            int requestedVersionAfterFirst = library.Lr2SongDbSyncRequestedVersion;
            int completedVersionAfterFirst = library.Lr2SongDbSyncCompletedVersion;
            int statusVersionAfterFirst = library.Lr2SongDbSyncStatusVersion;
            Lr2SongDbSyncStatusSnapshot second = library.QueueLr2SongDbSync("test_second");

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, first.Status);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, second.Status);
            Assert.AreEqual(1, scheduledCount);
            Assert.AreEqual(requestedVersionAfterFirst, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(completedVersionAfterFirst, library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(statusVersionAfterFirst + 1, library.Lr2SongDbSyncStatusVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DoesNotQueueWhenCompletedCopiedSongDbIsCurrent()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string songDirectory = Path.Combine(rootDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "#TITLE Copied Noop Sync\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFileSnapshot chartSnapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, chartSnapshot);
            var firstLibrary = new TestBmsLibrary(scope.SongDbPath)
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

            Lr2SongDbSyncStatusSnapshot first = firstLibrary.QueueLr2SongDbSync("test_first_for_copy");
            string copiedDirectory = Path.Combine(scope.DirectoryPath, "Copied");
            Directory.CreateDirectory(copiedDirectory);
            string copiedSongDbPath = Path.Combine(copiedDirectory, "song.db");
            File.Copy(scope.SongDbPath, copiedSongDbPath, overwrite: true);
            var copiedLibrary = new TestBmsLibrary(copiedSongDbPath)
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

            Lr2SongDbSyncStatusSnapshot second = copiedLibrary.QueueLr2SongDbSync("test_copied_song_db");

            Assert.AreEqual(Lr2SongDbSyncStatusKind.Needed, first.Status);
            Assert.AreEqual(1, firstScheduledCount);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, second.Status);
            Assert.AreEqual(0, copiedScheduledCount);
            Assert.AreEqual(0, copiedLibrary.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(0, copiedLibrary.Lr2SongDbSyncCompletedVersion);
            using var verify = new LR2SongDBExtended(copiedSongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_WithNoRootsCompletesEmptyGeneration()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_no_roots");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(1, library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(1, library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(0, library.Lr2SongDbSyncFailedVersion);
            Assert.IsFalse(library.Lr2SongDbSyncRunning);
            Assert.AreEqual(string.Empty, library.Lr2SongDbSyncFailureMessage);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, library.GetLr2SongDbSyncStatusSnapshot().Status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_WithNoRootsStillSyncsSongRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_no_roots_with_song");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(string.Empty, row.last_error);
            Assert.AreEqual(1, row.processed_cursor);
            Assert.AreEqual(1, row.total_count);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(songDirectory), verify.ExecuteScalar<string>("SELECT folder FROM song WHERE path = ?;", chartPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_ParsesReadableSnapshotContentInsteadOfPreservingNullSongColumns()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "ReadableSnapshot");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            byte[] directiveBytes = Encoding.ASCII.GetBytes(
                "#TITLE Parsed Readable Snapshot\r\n#ARTIST Parsed Artist\r\n#PLAYLEVEL 7\r\n#RANK 3\r\n#00118:01\r\n");
            File.WriteAllBytes(chartPath, [.. directiveBytes, 0x82]);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            library.QueueLr2SongDbSync("test_readable_snapshot_song_parse");

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
    public void QueueLr2SongDbSync_BuildsMissingChartInfoInsideSongRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "ChartInfoBuild");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE generated chart info\r\n#ARTIST tester\r\n#BPM 150\r\n#PLAYLEVEL 12\r\n#RANK 3\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            library.QueueLr2SongDbSync("test_chart_info_build");

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
    public void QueueLr2SongDbSync_RebuildsStaleChartInfoInsideSongRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "StaleChartInfoBuild");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE stale chart info\r\n#BPM 130\r\n#PLAYLEVEL 10\r\n#WAV01 kick.wav\r\n#00111:01\r\n");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            library.QueueLr2SongDbSync("test_stale_chart_info_build");

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

        Lr2StartupScanBlockerCleanupResult result = Lr2SongDbSyncService.CleanupStartupScanBlockerFolderRows(
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
    public void QueueLr2SongDbSync_ProjectsLr2CompatibilityWarningsToLiveRows()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string songDirectory = Path.Combine(scope.DirectoryPath, "LiveLr2Compatibility");
            Directory.CreateDirectory(songDirectory);
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            WriteBasicBms(chartPath, "live lr2 compatibility", CreateLr2TooLongResourcePath());
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            TestableBmsFile file = CreateSyncTestFile(chartPath, snapshot);
            file.SetMaintenanceInfo(new BMSFileMaintenanceInfo(file)
            {
                hash = file.hash,
                wav_files_defined = 99,
                wav_files_existing = 88
            }, suppressPropertyChanged: true, origin: MaintenanceInfoOrigin.Calculated);
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = [file]
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_live_lr2_compatibility");

            Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2ResourcePathTooLong));
            Assert.AreEqual(99, file.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(88, file.maintenanceInfo.wav_files_existing);
            int flags = file.maintenanceInfo.lr2_warning_flags.GetValueOrDefault();
            Assert.IsTrue((flags & (int)Lr2CompatibilityWarningFlags.ResourcePathTooLong) != 0);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DiscoversLr2FolderWithoutCharts()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string nestedDirectory = Path.Combine(rootDirectory, "Custom");
            Directory.CreateDirectory(nestedDirectory);
            string lr2FolderPath = Path.Combine(nestedDirectory, "table.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE 入れ子表\r\n#COMMAND song.level = 12\r\n#MAXTRACKS 64", Encoding.GetEncoding("shift_jis"));
            string outsideLr2FolderPath = Path.Combine(scope.DirectoryPath, "outside.lr2folder");
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            library.QueueLr2SongDbSync("test_lr2folder_only");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDBExtended.lr2_song_db_sync_status row = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(row);
            Assert.AreEqual("Completed", row.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, row.stage);
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
    public void QueueLr2SongDbSync_DiscoversLr2FolderFromCustomFolderOutputBase()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputBase = Path.Combine(scope.DirectoryPath, "CustomOutput");
            Directory.CreateDirectory(outputBase);
            string lr2FolderPath = Path.Combine(outputBase, "0000.lr2folder");
            string stalePath = Path.Combine(outputBase, "stale.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Output Folder", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
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

            library.QueueLr2SongDbSync("test_custom_folder_output_base");

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
    public void QueueLr2SongDbSync_DiscoversRootCustomFolderOutputAsRootRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string outputBase = Path.Combine(scope.DirectoryPath, "RootCustomOutput");
            Directory.CreateDirectory(outputBase);
            string lr2FolderPath = Path.Combine(outputBase, "root.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = outputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_root_custom_folder_output_base");

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
    public void QueueLr2SongDbSync_GeneratesNormalCustomFolderOutputBaseParentRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            string outputBase = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Normal Output", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBase;
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBase,
                LR2CustomFolderAdditionalOutputBaseDirs = [],
                LR2CustomFolderOutputBaseDirRootType = string.Empty
            };
            var library = new TestBmsLibrary(scope.SongDbPath, null, null, null, () => options)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };
            InvokeCaptureLr2SongDbSyncScanSurface(library, options, [bmsRoot], new SongTableFileCheckResult
            {
                Lr2ScanSurfaceAvailable = true,
                Lr2ScanDirectoryEntries = CreateDirectoryEntryMap(bmsRoot, outputBase, tableDirectory),
                Lr2ScanNormalFolderDirectoryEntries = CreateDirectoryEntryMap(bmsRoot, outputBase, tableDirectory),
                Lr2ScanLr2FolderDiscoveryDirectories = [bmsRoot, outputBase],
                Lr2ScanFolderInfoFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                Lr2ScanLr2FolderFileEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [lr2FolderPath] = new RootFileEnumerationEntry(lr2FolderPath, File.GetLastWriteTimeUtc(lr2FolderPath))
                }
            });

            library.QueueLr2SongDbSync("test_normal_custom_folder_output_parent");

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
    public void SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange_SyncsSiblingAndExcludesManagedDirectory()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            PlaylistPersistenceRepository.EnsureSchema(scope.SongDbPath);
            string additionalBase = Path.Combine(scope.DirectoryPath, "Additional");
            string managedDirectory = Path.Combine(additionalBase, "ManagedTable");
            string unmanagedSiblingDirectory = Path.Combine(additionalBase, "ExternalTable");
            string managedPath = Path.Combine(managedDirectory, "0000.lr2folder");
            string unmanagedSiblingPath = Path.Combine(unmanagedSiblingDirectory, "external.lr2folder");
            Directory.CreateDirectory(managedDirectory);
            Directory.CreateDirectory(unmanagedSiblingDirectory);
            File.WriteAllText(managedPath, "#TITLE Managed", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(unmanagedSiblingPath, "#TITLE External Sibling", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalBase]);
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = 9401,
                    name = "ManagedTable",
                    symbol = "MT",
                    Output_dir = "ManagedTable",
                    custom_folder_output_base_name = "Additional",
                    ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                        & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                        & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                }, typeof(LR2SongDBExtended.playlist));
            }
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = true,
                LR2CustomFolderAdditionalOutputBaseDirs = [additionalBase]
            };
            var library = new TestBmsLibrary(
                scope.SongDbPath,
                getLR2Config: null,
                _lr2ScoreDB: null,
                startupRequiredFileScanReason: null,
                optionsSnapshotProvider: () => options)
            {
                SearchTargets = [],
                BMSFiles = []
            };

            library.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange("test_additional_output_base_external_sync");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder additionalBaseRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(additionalBase));
            Assert.IsNotNull(additionalBaseRow, rowSummary);
            Assert.AreEqual(1, additionalBaseRow.type);
            Assert.AreEqual("Additional", additionalBaseRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, additionalBaseRow.parent);
            LR2SongDB.folder unmanagedSiblingDirectoryRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(unmanagedSiblingDirectory));
            Assert.IsNotNull(unmanagedSiblingDirectoryRow, rowSummary);
            Assert.AreEqual(1, unmanagedSiblingDirectoryRow.type);
            Assert.AreEqual("ExternalTable", unmanagedSiblingDirectoryRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(additionalBase), unmanagedSiblingDirectoryRow.parent);
            LR2SongDB.folder unmanagedSiblingRow = rows.SingleOrDefault(row => row.path == unmanagedSiblingPath);
            Assert.IsNotNull(unmanagedSiblingRow, rowSummary);
            Assert.AreEqual(2, unmanagedSiblingRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(unmanagedSiblingDirectory), unmanagedSiblingRow.parent);
            Assert.IsFalse(rows.Any(row => row.path == managedPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange_PreservesRemovedAdditionalBaseRows()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string oldAdditionalBase = Path.Combine(scope.DirectoryPath, "OldAdditional");
            string oldExternalPath = Path.Combine(oldAdditionalBase, "ExternalTable", "external.lr2folder");
            Directory.CreateDirectory(Path.GetDirectoryName(oldExternalPath));
            File.WriteAllText(oldExternalPath, "#TITLE Old External", Encoding.GetEncoding("shift_jis"));
            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                setup.CreateTable<LR2SongDB.folder>();
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = oldExternalPath,
                    type = 2,
                    title = "Old External",
                    date = 1
                }, typeof(LR2SongDB.folder));
            }
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [],
                BMSFiles = []
            };

            library.Lr2Synchronization.SyncExternalLr2FolderRowsForCustomFolderOutputBaseChange("test_removed_additional_output_base_preserve");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.AreEqual(1, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldExternalPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_GeneratesRootCustomFolderOutputParentRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string outputBase = Path.Combine(scope.DirectoryPath, "RootCustomOutput");
            string tableDirectory = Path.Combine(outputBase, "Table");
            Directory.CreateDirectory(tableDirectory);
            string lr2FolderPath = Path.Combine(tableDirectory, "0000.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Root Output", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = outputBase;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_root_custom_folder_output_parent");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder parentRow = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == Lr2FolderPath.ToFolderPath(tableDirectory));
            Assert.AreEqual(1, parentRow.type);
            Assert.AreEqual("Table", parentRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentRow.parent);
            Assert.IsTrue(parentRow.date.GetValueOrDefault() > 0);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tableDirectory), lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DiscoversEnabledLr2BuiltinCustomFolderAsRelativeRootRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_lr2_builtin_custom_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == @"LR2files\CustomFolder\favorite.lr2folder");
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Favorite", lr2Folder.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_GeneratesBuiltinCustomFolderCategoryRow()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            BmsLibraryOptionsSnapshot options = BmsLibraryOptionsSnapshot.CreateCurrent(Settings.Default);
            var library = new TestBmsLibrary(scope.SongDbPath, () => config, null, null, () => options)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_builtin_custom_folder_category");

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
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_SkipsDisabledLr2BuiltinCustomFolder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_lr2_builtin_custom_folder_disabled");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            Assert.IsFalse(verify.Table<LR2SongDB.folder>().Any(folder => folder.path == @"LR2files\CustomFolder\RANDOM\"));
            Assert.IsFalse(verify.Table<LR2SongDB.folder>().Any(folder => folder.path == @"LR2files\CustomFolder\favorite.lr2folder"));
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DiscoversBuiltinCourseFolderAsTypeSixRegardlessOfCustomFolderMask()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_lr2_builtin_course_folder_prepare_surface",
                () => CreatePreparedLr2FolderSurface(lr2Root, lr2FolderPath)));
            library.QueueLr2SongDbSync("test_lr2_builtin_course_folder");

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
    public void QueueLr2SongDbSync_DiscoversBuiltinNewSongFolderOnlyWhenRecentSongExists()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
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
            var library = new TestBmsLibrary(scope.SongDbPath, () => config)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = [file]
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            Assert.IsTrue(library.TryRunLr2SongDbSyncDataPreparation(
                "test_lr2_builtin_newsong_folder_prepare_surface",
                () => CreatePreparedLr2FolderSurface(lr2Root, lr2FolderPath)));
            library.QueueLr2SongDbSync("test_lr2_builtin_newsong_folder");

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
    public void QueueLr2SongDbSync_DoesNotDiscoverBuiltinLr2RivalFolder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string bmsRoot = Path.Combine(scope.DirectoryPath, "BMS");
            Directory.CreateDirectory(bmsRoot);
            string lr2Root = Path.Combine(scope.DirectoryPath, "LR2beta3");
            string rivalDirectory = Path.Combine(lr2Root, "LR2files", "Rival");
            Directory.CreateDirectory(rivalDirectory);
            string lr2FolderPath = Path.Combine(rivalDirectory, "rival.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Rival", Encoding.GetEncoding("shift_jis"));
            Settings.Default.LR2RootPath = lr2Root;
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [bmsRoot],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_lr2_builtin_rival_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            List<LR2SongDB.folder> folders = [.. verify.Table<LR2SongDB.folder>()];
            Assert.IsFalse(folders.Any(folder => folder.path == @"LR2files\Rival\rival.lr2folder"));
            Assert.IsFalse(folders.Any(folder => string.Equals(folder.path, lr2FolderPath, StringComparison.OrdinalIgnoreCase)));
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2SongDbSync_DiscoversRivalFolderFromNormalScanRootAsExternalFolder()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            ResetLr2FolderDiscoverySettings();
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string rivalDirectory = Path.Combine(rootDirectory, "__RIVAL__");
            Directory.CreateDirectory(rivalDirectory);
            string lr2FolderPath = Path.Combine(rivalDirectory, "rival.lr2folder");
            File.WriteAllText(lr2FolderPath, "#TITLE Rival External", Encoding.GetEncoding("shift_jis"));
            var library = new TestBmsLibrary(scope.SongDbPath)
            {
                SearchTargets = [rootDirectory],
                BMSFiles = []
            };
            library.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };

            library.QueueLr2SongDbSync("test_external_rival_folder");

            using var verify = new LR2SongDBExtended(scope.SongDbPath);
            LR2SongDB.folder lr2Folder = verify.Table<LR2SongDB.folder>().ToList().Single(folder => folder.path == lr2FolderPath);
            Assert.AreEqual(2, lr2Folder.type);
            Assert.AreEqual("Rival External", lr2Folder.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(rivalDirectory),
                lr2Folder.parent);
            LR2SongDBExtended.lr2_song_db_sync_status status = verify.Find<LR2SongDBExtended.lr2_song_db_sync_status>(Lr2SongDbSyncStatusService.DefaultStatusName);
            Assert.IsNotNull(status);
            Assert.AreEqual("Completed", status.status);
            Assert.AreEqual(Lr2SongDbSyncService.CompletedStage, status.stage);
        }
        finally
        {
            ResetTouchedSettings();
        }
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
            [chartDirectory],
            new EverythingNative(ApplicationPathPolicy.Current));

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
            [chartDirectory],
            new EverythingNative(ApplicationPathPolicy.Current));

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

    private static void InvokeApplyInstalledChartStorageTargets(BMSLibrary library, ChartStorageTargetSet targets)
    {
        library.ApplyInstalledChartStorageTargets(targets, "install_package");
    }

    private static void InvokeApplyLibraryMutationDelta(BMSLibrary library, LibraryMutationDelta delta)
    {
        library.ApplyLibraryMutationDelta(delta);
    }

    private static void InvokeBeginLr2SongDbSyncRequest(BMSLibrary library)
    {
        library.StartupBackgroundTaskScheduler = (_, _, _, _) => true;
        library.QueueLr2SongDbSync("test_request_version", force: true);
        Assert.IsTrue(library.Lr2SongDbSyncRunning);
    }

    private static object InvokeCreateLr2SongDbSyncInput(BMSLibrary library)
    {
        return library.Lr2Synchronization.CreateLr2SongDbSyncInput();
    }

    private static object InvokeCreateLr2SongDbSyncAppManagedOutputScope(BMSLibrary library)
    {
        return library.Lr2Synchronization.CreateLr2SongDbSyncAppManagedOutputScope();
    }

    private static bool InvokeIsLr2SongDbSyncInputCurrent(BMSLibrary library, object input)
    {
        return GetLr2SynchronizationOwner(library).IsLr2SongDbSyncInputCurrent((Lr2SongDbSyncInput)input);
    }

    private static bool InvokeHasLr2SongDbSyncPreparedDataSurface(BMSLibrary library)
    {
        return GetLr2SynchronizationOwner(library).HasLr2SongDbSyncPreparedDataSurface();
    }

    private static BMSLibrary.Lr2SynchronizationOwner GetLr2SynchronizationOwner(BMSLibrary library)
    {
        return (BMSLibrary.Lr2SynchronizationOwner)library.Lr2Synchronization;
    }

    private static CatalogMutationOwner GetCatalogMutationOwner(BMSLibrary library)
    {
        FieldInfo fieldInfo = typeof(BMSLibrary).GetField(
            "catalogMutationOwner",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(fieldInfo);
        return (CatalogMutationOwner)fieldInfo.GetValue(library);
    }

    private static Lr2SongDbSyncPreparedDataSurface CreatePreparedLr2FolderSurface(
        string scopeDirectory,
        string filePath,
        IReadOnlyDictionary<string, RootFileEnumerationEntry>? directoryEntries = null,
        IEnumerable<string>? folderInfoFilePaths = null,
        IReadOnlyDictionary<string, RootFileEnumerationEntry>? folderInfoFileEntries = null,
        IEnumerable<string>? textFileDirectories = null)
    {
        return new Lr2SongDbSyncPreparedDataSurface(
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

    private static void InvokeCaptureLr2SongDbSyncScanSurface(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        IEnumerable<string> rootDirectories,
        SongTableFileCheckResult result)
    {
        library.Lr2Synchronization.CaptureLr2SongDbSyncScanSurface(options, rootDirectories, result);
    }

    private static void InvokeApplyLr2FolderFileDiffSync(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        IReadOnlyList<string> rootDirectories,
        SongTableFileCheckResult result,
        string reason)
    {
        var owner = new Lr2FolderFileDiffOwner(
            _ => { },
            _ => { },
            exception => exception?.Message ?? string.Empty,
            _ => { },
            library.Lr2Synchronization,
            new EverythingNative(ApplicationPathPolicy.Current));
        owner.Apply(options, rootDirectories, result, reason);
    }

    private static void InvokeSetModeAndCommitToDb(BMSLibrary library, IEnumerable<BMSFile> files)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("setModeAndCommitToDB", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [files, false]);
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        SongTableFileCheckResult result)
    {
        library.Lr2Synchronization.MarkLr2SongDbSyncIncompleteAfterFileDiffNormalFolderSyncFailure(options, result);
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        GetLr2SynchronizationOwner(library).MarkLr2SongDbSyncIncompleteAfterNormalFolderSyncFailure(
            options,
            stage,
            detail,
            logReason);
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        string stage,
        string detail,
        string logReason)
    {
        GetLr2SynchronizationOwner(library).MarkLr2SongDbSyncIncompleteAfterSongDbWriteFailure(
            options,
            stage,
            detail,
            logReason);
    }

    private static void InvokeMarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure(
        BMSLibrary library,
        BmsLibraryOptionsSnapshot options,
        Exception exception,
        string reason)
    {
        MethodInfo methodInfo = typeof(BMSLibrary).GetMethod("MarkLr2SongDbSyncIncompleteAfterFileDiffSongDbWriteFailure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(library, [options, exception, reason]);
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
        ResetLr2FolderDiscoverySettings();
    }

    private static void ResetLr2FolderDiscoverySettings()
    {
        Settings.Default.LR2CustomFolderOutputBaseDir = string.Empty;
        Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
        Settings.Default.LR2CustomFolderOutputBaseDirRootType = string.Empty;
        Settings.Default.LR2RootPath = string.Empty;
        Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = false;
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

    private sealed class QueuedUiScheduler : IUiScheduler
    {
        private readonly Queue<QueuedUiOperation> operations = [];
        private readonly object syncRoot = new();

        internal int PendingCount
        {
            get
            {
                lock (syncRoot)
                {
                    return operations.Count;
                }
            }
        }

        internal int ScheduleCount { get; private set; }

        public bool IsAvailable => true;

        public bool CanExecuteInline => false;

        public bool CheckAccess() => false;

        public IUiScheduledOperation Schedule(
            Action action,
            UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            var operation = new QueuedUiOperation(action);
            lock (syncRoot)
            {
                ScheduleCount++;
                operations.Enqueue(operation);
            }
            return operation;
        }

        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            action();
            return Task.CompletedTask;
        }

        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) =>
            action();

        internal void Drain()
        {
            while (PendingCount > 0)
            {
                ExecuteNext();
            }
        }

        internal void ExecuteNext()
        {
            QueuedUiOperation operation;
            lock (syncRoot)
            {
                if (operations.Count == 0)
                {
                    throw new InvalidOperationException("No queued UI operation is available.");
                }
                operation = operations.Dequeue();
            }
            operation.Execute();
        }
    }

    private sealed class QueuedUiOperation : IUiScheduledOperation
    {
        private readonly Action action;
        private readonly TaskCompletionSource<object> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int aborted;

        internal QueuedUiOperation(Action action)
        {
            this.action = action ?? throw new ArgumentNullException(nameof(action));
        }

        public bool IsAccepted => true;

        public bool IsCompleted => Completion.IsCompleted;

        public bool IsAborted => Volatile.Read(ref aborted) != 0;

        public string RejectionReason => null;

        public Task Completion => completion.Task;

        public void Abort()
        {
            Interlocked.Exchange(ref aborted, 1);
            completion.TrySetCanceled();
        }

        internal void Execute()
        {
            if (IsAborted)
            {
                return;
            }
            try
            {
                action();
                completion.TrySetResult(null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                throw;
            }
        }
    }
}
