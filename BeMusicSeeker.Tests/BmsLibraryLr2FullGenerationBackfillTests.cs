using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsLibraryLr2FullGenerationBackfillTests
{
    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_DoesNotQueueWhenFeatureIsDisabled()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = false;
            var library = new BMSLibrary(scope.SongDbPath);
            bool queued = false;
            library.StartupBackgroundTaskScheduler = delegate
            {
                queued = true;
                return true;
            };

            Lr2FullGenerationStatusSnapshot snapshot = library.QueueLr2FullGenerationBackfillIfNeeded("test_disabled");

            Assert.AreEqual(Lr2FullGenerationStatusKind.NotNeeded, snapshot.Status);
            Assert.IsFalse(queued);
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_RunsNormalFolderStageAndLeavesSongStageIncomplete()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            string rootDirectory = Path.Combine(scope.DirectoryPath, "BMS");
            string packDirectory = Path.Combine(rootDirectory, "Pack");
            string songDirectory = Path.Combine(packDirectory, "Song");
            Directory.CreateDirectory(songDirectory);
            File.WriteAllText(Path.Combine(packDirectory, "folderinfo.txt"), "#TITLE Pack Title");
            string chartPath = Path.Combine(songDirectory, "chart.bms");
            File.WriteAllText(chartPath, "*---------------------- HEADER FIELD");

            var library = new BMSLibrary(scope.SongDbPath);
            library.SearchTargets = [rootDirectory];
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            library.BMSFiles = [file];

            using (var setup = new LR2SongDBExtended(scope.SongDbPath))
            {
                string stalePath = ToFolderPath(Path.Combine(rootDirectory, "Removed"));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = stalePath,
                    type = 1,
                    date = 1
                }, typeof(LR2SongDB.folder));
                setup.InsertOrReplace(new LR2SongDB.folder
                {
                    path = Path.Combine(rootDirectory, "custom.lr2folder"),
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
            Assert.AreEqual("Incomplete", row.status);
            Assert.AreEqual(Lr2FullGenerationBackfillService.SongRowsPendingReason, row.last_error);
            Assert.AreEqual(Lr2FullGenerationBackfillService.SongRowsPendingStage, row.stage);
            Assert.AreEqual(row.total_count, row.processed_cursor);
            Assert.IsTrue(row.total_count > 0);

            string expectedRootFolderPath = ToFolderPath(rootDirectory);
            string expectedPackFolderPath = ToFolderPath(packDirectory);
            string expectedSongFolderPath = ToFolderPath(songDirectory);
            string expectedRemovedFolderPath = ToFolderPath(Path.Combine(rootDirectory, "Removed"));
            string expectedCustomFolderPath = Path.Combine(rootDirectory, "custom.lr2folder");
            var folderRows = verify.Table<LR2SongDB.folder>().ToList();
            LR2SongDB.folder root = folderRows.Single(folder => folder.path == expectedRootFolderPath);
            LR2SongDB.folder pack = folderRows.Single(folder => folder.path == expectedPackFolderPath);
            LR2SongDB.folder song = folderRows.Single(folder => folder.path == expectedSongFolderPath);
            Assert.AreEqual(1, root.type);
            Assert.AreEqual("Pack Title", pack.title);
            Assert.AreEqual("Song", song.title);
            Assert.AreEqual(0, folderRows.Count(folder => folder.path == expectedRemovedFolderPath));
            Assert.AreEqual(1, folderRows.Count(folder => folder.path == expectedCustomFolderPath));
        }
        finally
        {
            ResetTouchedSettings();
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_WithNoRootsDoesNotCompleteGeneration()
    {
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
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
            Assert.AreEqual(Lr2FullGenerationBackfillService.SongRowsPendingReason, row.last_error);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList().Count);
        }
        finally
        {
            ResetTouchedSettings();
        }
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

    private static void ResetTouchedSettings()
    {
        Settings.Default.OperationModeLR2DB = true;
        Settings.Default.EnableLR2SongDbFullGeneration = false;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
