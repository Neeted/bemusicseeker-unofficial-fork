using System;
using System.IO;
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
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        bool previousFullGeneration = Settings.Default.EnableLR2SongDbFullGeneration;
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
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.EnableLR2SongDbFullGeneration = previousFullGeneration;
        }
    }

    [TestMethod]
    public void QueueLr2FullGenerationBackfillIfNeeded_QueuesStartupTaskAndMarksIncompleteWhenNeeded()
    {
        bool previousOperationMode = Settings.Default.OperationModeLR2DB;
        bool previousFullGeneration = Settings.Default.EnableLR2SongDbFullGeneration;
        using TestDatabaseScope scope = TestDatabaseScope.Create();
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.EnableLR2SongDbFullGeneration = true;
            var library = new BMSLibrary(scope.SongDbPath);
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
            Assert.AreEqual("backfill_runner_not_implemented", row.last_error);
            Assert.AreEqual(0, row.processed_cursor);
            Assert.AreEqual(0, row.total_count);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationMode;
            Settings.Default.EnableLR2SongDbFullGeneration = previousFullGeneration;
        }
    }

    private sealed class TestDatabaseScope : IDisposable
    {
        private readonly string directoryPath;

        public string SongDbPath { get; }

        private TestDatabaseScope(string directoryPath)
        {
            this.directoryPath = directoryPath;
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
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
