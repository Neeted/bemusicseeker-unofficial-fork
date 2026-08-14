using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Threading.Tasks;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BeatorajaBmtOptionsSnapshotTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = new();
    [TestMethod]
    public void CreateCurrentCapturesAllBmtExportSettings()
    {
        bool previousEnabled = testSettings.EnableBeatorajaBmtOutput;
        bool previousKeepFiles = testSettings.KeepBeatorajaBmtFilesWhenOutputDisabled;
        string previousRootPath = testSettings.BeatorajaRootPath;
        string previousTablePath = testSettings.BeatorajaBmtTablePath;
        bool previousRegisterUrls = testSettings.RegisterBeatorajaBmtUrls;
        string previousHashMode = testSettings.BeatorajaBmtHashOutputMode;
        try
        {
            testSettings.EnableBeatorajaBmtOutput = true;
            testSettings.KeepBeatorajaBmtFilesWhenOutputDisabled = true;
            testSettings.BeatorajaRootPath = "beatoraja-root";
            testSettings.BeatorajaBmtTablePath = "table.json";
            testSettings.RegisterBeatorajaBmtUrls = true;
            testSettings.BeatorajaBmtHashOutputMode = "FillMissingMd5Sha256";

            BeatorajaBmtOptionsSnapshot snapshot = BeatorajaBmtOptionsSnapshot.CreateCurrent(testSettings);

            Assert.IsTrue(snapshot.EnableBeatorajaBmtOutput);
            Assert.IsTrue(snapshot.KeepBeatorajaBmtFilesWhenOutputDisabled);
            Assert.AreEqual("beatoraja-root", snapshot.BeatorajaRootPath);
            Assert.AreEqual("table.json", snapshot.BeatorajaBmtTablePath);
            Assert.IsTrue(snapshot.RegisterBeatorajaBmtUrls);
            Assert.AreEqual("FillMissingMd5Sha256", snapshot.BeatorajaBmtHashOutputMode);
        }
        finally
        {
            testSettings.EnableBeatorajaBmtOutput = previousEnabled;
            testSettings.KeepBeatorajaBmtFilesWhenOutputDisabled = previousKeepFiles;
            testSettings.BeatorajaRootPath = previousRootPath;
            testSettings.BeatorajaBmtTablePath = previousTablePath;
            testSettings.RegisterBeatorajaBmtUrls = previousRegisterUrls;
            testSettings.BeatorajaBmtHashOutputMode = previousHashMode;
        }
    }

    [TestMethod]
    public async Task DisabledBmtQueueDropsPendingIdsInsteadOfReschedulingForever()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BeatorajaBmtOptionsSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string songDbPath = Path.Combine(tempDirectory, "song.db");
        using (var initialize = new LR2SongDBExtended(songDbPath))
        {
        }
        BeatorajaBmtOptionsSnapshot options = new()
        {
            EnableBeatorajaBmtOutput = true,
            BeatorajaBmtTablePath = Path.Combine(tempDirectory, "table.json")
        };
        var playlist = new TestBmsPlaylist(
            songDbPath,
            null,
            null,
            null,
            null,
            () => new PlaylistUrlCompletionOptionsSnapshot(),
            () => options,
            () => new CustomFolderOutputSettingsSnapshot());
        Func<Task> queuedWork = null!;
        int schedulerCallCount = 0;
        playlist.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
        {
            schedulerCallCount++;
            queuedWork = work;
            return true;
        };
        try
        {
            playlist.BmtOutput.QueueBeatorajaBmtExportForTables([new BMSTable { playlist_id = 1 }], "disabled_queue_test");
            Assert.AreEqual(1, schedulerCallCount);
            Assert.IsNotNull(queuedWork);

            options = new BeatorajaBmtOptionsSnapshot
            {
                EnableBeatorajaBmtOutput = false,
                BeatorajaBmtTablePath = Path.Combine(tempDirectory, "table.json")
            };
            await queuedWork();

            Assert.AreEqual(1, schedulerCallCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
