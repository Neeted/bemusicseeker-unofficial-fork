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
[DoNotParallelize]
public sealed class BeatorajaBmtOptionsSnapshotTests
{
    [TestMethod]
    public void CreateCurrentCapturesAllBmtExportSettings()
    {
        bool previousEnabled = Settings.Default.EnableBeatorajaBmtOutput;
        bool previousKeepFiles = Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled;
        string previousRootPath = Settings.Default.BeatorajaRootPath;
        string previousTablePath = Settings.Default.BeatorajaBmtTablePath;
        bool previousRegisterUrls = Settings.Default.RegisterBeatorajaBmtUrls;
        string previousHashMode = Settings.Default.BeatorajaBmtHashOutputMode;
        try
        {
            Settings.Default.EnableBeatorajaBmtOutput = true;
            Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled = true;
            Settings.Default.BeatorajaRootPath = "beatoraja-root";
            Settings.Default.BeatorajaBmtTablePath = "table.json";
            Settings.Default.RegisterBeatorajaBmtUrls = true;
            Settings.Default.BeatorajaBmtHashOutputMode = "FillMissingMd5Sha256";

            BeatorajaBmtOptionsSnapshot snapshot = BeatorajaBmtOptionsSnapshot.CreateCurrent(Settings.Default);

            Assert.IsTrue(snapshot.EnableBeatorajaBmtOutput);
            Assert.IsTrue(snapshot.KeepBeatorajaBmtFilesWhenOutputDisabled);
            Assert.AreEqual("beatoraja-root", snapshot.BeatorajaRootPath);
            Assert.AreEqual("table.json", snapshot.BeatorajaBmtTablePath);
            Assert.IsTrue(snapshot.RegisterBeatorajaBmtUrls);
            Assert.AreEqual("FillMissingMd5Sha256", snapshot.BeatorajaBmtHashOutputMode);
        }
        finally
        {
            Settings.Default.EnableBeatorajaBmtOutput = previousEnabled;
            Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled = previousKeepFiles;
            Settings.Default.BeatorajaRootPath = previousRootPath;
            Settings.Default.BeatorajaBmtTablePath = previousTablePath;
            Settings.Default.RegisterBeatorajaBmtUrls = previousRegisterUrls;
            Settings.Default.BeatorajaBmtHashOutputMode = previousHashMode;
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
