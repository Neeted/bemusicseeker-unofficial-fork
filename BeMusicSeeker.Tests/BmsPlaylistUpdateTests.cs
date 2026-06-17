using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.Util.Extensions;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BmsPlaylistUpdateTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_InvokesMutationGuardBeforeOpeningDatabase()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new BMSPlaylist(songDbPath);
            bool guardInvoked = false;
            playlist.Lr2FolderSyncMutationGuard = operation =>
            {
                guardInvoked = true;
                Assert.AreEqual("playlist_lr2folder_sync", operation);
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            };

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(() =>
                InvokeSyncCustomFolderRows(playlist, Path.Combine(tempDirectory, "Output"), []));

            Assert.IsTrue(guardInvoked);
            Assert.IsInstanceOfType(exception.InnerException, typeof(InvalidOperationException));
            Assert.AreEqual(Resources.Warn_Lr2SongDbSyncRunning, exception.InnerException.Message);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2FolderSync_ReportsFailureBeforeRethrowing()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            var playlist = new BMSPlaylist(songDbPath);
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);
            string reportedOperation = string.Empty;
            Exception reportedException = new InvalidOperationException("not invoked");
            playlist.Lr2FolderSyncFailureReporter = (operation, ex) =>
            {
                reportedOperation = operation;
                reportedException = ex;
            };

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(() =>
                InvokeSyncCustomFolderRows(playlist, Path.Combine(tempDirectory, "Output"), []));

            Assert.AreEqual("playlist_lr2folder_sync", reportedOperation);
            Assert.IsNotNull(reportedException);
            Assert.AreSame(exception.InnerException, reportedException);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void UpdateBmsTablesInternal_CallbackFailureDoesNotAbortUpdate()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { new BMSTable() }), Dispatcher.CurrentDispatcher)
            };
            bool callbackInvoked = false;

            List<BMSTable> updated = playlist.UpdateBMSTablesInternal(reloadExtPlaylist: false, updateCallbackActions:
            [
                delegate
                {
                    callbackInvoked = true;
                    throw new InvalidOperationException("callback failure");
                }
            ], syncResultCallback: null);

            Assert.IsTrue(callbackInvoked);
            Assert.AreEqual(0, updated.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_CallbackFailureDoesNotAbortUpdate()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { new BMSTable() }), Dispatcher.CurrentDispatcher)
            };
            bool callbackInvoked = false;

            List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: false, updateCallbackActions:
            [
                delegate
                {
                    callbackInvoked = true;
                    throw new InvalidOperationException("callback failure");
                }
            ], syncResultCallback: null);

            Assert.IsTrue(callbackInvoked);
            Assert.AreEqual(0, updated.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_PassesOldAndNewEntrySnapshotsToCallback()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"SnapshotTable\",\r\n\"symbol\":\"S\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            BMSPlaylist.PlaylistTableUpdateContext? callbackContext = null;

            List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions:
            [
                delegate(BMSPlaylist.PlaylistTableUpdateContext context)
                {
                    callbackContext = context;
                }
            ], syncResultCallback: null);

            Assert.IsNotNull(callbackContext);
            BMSPlaylist.PlaylistTableUpdateContext actualCallbackContext = callbackContext!;
            Assert.AreSame(table, actualCallbackContext.OldTable);
            Assert.IsNotNull(actualCallbackContext.NewTable);
            Assert.IsNotNull(actualCallbackContext.OldEntriesSnapshot);
            Assert.IsNotNull(actualCallbackContext.NewEntriesSnapshot);
            Assert.AreEqual(1, actualCallbackContext.OldEntriesSnapshot.Count);
            Assert.AreEqual(1, actualCallbackContext.NewEntriesSnapshot.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", actualCallbackContext.OldEntriesSnapshot[0].md5);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", actualCallbackContext.NewEntriesSnapshot[0].md5);
            Assert.AreEqual(new string('b', 64), actualCallbackContext.NewEntriesSnapshot[0].sha256);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_HashInitializationPersistsWithoutUpdatedFlag()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"HashInitTable\",\r\n\"symbol\":\"H\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900001;
            table.header_sha256 = null;
            table.data_sha256 = null;
            var existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
            table.last_update = existingLastUpdate;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Hash Init Song\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Repaired Song\",\"artist\":\"Artist\",\"level\":\"2\"}]"));
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            BMSPlaylist.PlaylistTableUpdateContext? callbackContext = null;

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync([table],
            [
                delegate(BMSPlaylist.PlaylistTableUpdateContext context)
                {
                    callbackContext = context;
                }
            ], reason: "test_hash_initialization");

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Updated);
            Assert.IsNotNull(callbackContext);
            Assert.IsFalse(callbackContext!.Updated);
            Assert.IsTrue(callbackContext.ReferenceEntriesChanged);
            Assert.AreEqual(existingLastUpdate, results[0].ResultTable.last_update);
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.data_sha256));
            Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == 900001);
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(persisted.data_sha256));
            Assert.AreEqual(existingLastUpdate, persisted.last_update);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 900001 AND is_removed = 0;"));
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReportsPlaylistSyncProgressSnapshots()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ProgressTable\",\r\n\"symbol\":\"P\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Progress Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);

            List<PlaylistSyncProgressSnapshot> snapshots = [];

            List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions: null, syncResultCallback: null, progressCallback: snapshots.Add);

            Assert.IsNotNull(updated);
            Assert.IsTrue(snapshots.Count >= 3);
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 0));
            Assert.IsTrue(snapshots.Any(s => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 1 && string.Equals(s.CurrentTableName, "ProgressTable", StringComparison.Ordinal)));
            PlaylistSyncProgressSnapshot playlistSyncProgressSnapshot = snapshots.Last();
            Assert.IsFalse(playlistSyncProgressSnapshot.IsActive);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.TotalTableCount);
            Assert.AreEqual(1, playlistSyncProgressSnapshot.CompletedTableCount);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_ReloadsOnlyExternalSyncTargets()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string externalHeaderPath = Path.Combine(tempDirectory, "external-header.json");
            string externalScorePath = Path.Combine(tempDirectory, "external-score.json");
            string manualHeaderPath = Path.Combine(tempDirectory, "manual-header.json");
            File.WriteAllBytes(externalHeaderPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalTarget\",\r\n\"symbol\":\"E\",\r\n\"data_url\":\"./external-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(externalScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(manualHeaderPath, CreateUtf8BomBytes("{ invalid json"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable externalTable = await playlist.LoadExternalTableAsync(new Uri(externalHeaderPath));
            externalTable.EnableExternalSync();
            var manualTable = new BMSTable
            {
                name = "ManualTarget",
                symbol = "M",
                Header_url = new Uri(manualHeaderPath)
            };
            manualTable.DisableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { externalTable, manualTable }), Dispatcher.CurrentDispatcher);
            List<PlaylistSyncAttemptResult> syncResults = [];

            await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions: null, syncResults.Add);

            Assert.AreEqual(1, syncResults.Count);
            Assert.AreSame(externalTable, syncResults[0].SourceTable);
            Assert.IsTrue(syncResults[0].Succeeded);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ReloadsExplicitTargetRegardlessOfExternalSyncFlag()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ManualTarget\",\r\n\"symbol\":\"M\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9001;
            table.DisableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync([table], reason: "test_explicit_reload");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsFalse(results[0].ResultTable.is_external_sync);
            using (results[0].ResultTable.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, results[0].ResultTable.entries.Count(entry => !entry.is_removed));
            }
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_SkipsTargetsWithoutAbsoluteUri()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            var playlist = new BMSPlaylist(songDbPath);
            var table = new BMSTable
            {
                name = "NoUri",
                symbol = "N"
            };
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            List<PlaylistSyncProgressSnapshot> snapshots = [];

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync([table], progressCallback: snapshots.Add, reason: "test_skip_no_uri");

            Assert.AreEqual(0, results.Count);
            Assert.IsTrue(snapshots.Count >= 2);
            Assert.IsTrue(snapshots.All(snapshot => snapshot.TotalTableCount == 0));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ReloadPlaylistTargetsAsync_ContinuesAfterTargetFailure()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string goodHeaderPath = Path.Combine(tempDirectory, "good-header.json");
            string goodScorePath = Path.Combine(tempDirectory, "good-score.json");
            string badHeaderPath = Path.Combine(tempDirectory, "bad-header.json");
            File.WriteAllBytes(goodHeaderPath, CreateUtf8BomBytes("{\r\n\"name\":\"GoodTarget\",\r\n\"symbol\":\"G\",\r\n\"data_url\":\"./good-score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(goodScorePath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Good Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(badHeaderPath, CreateUtf8BomBytes("{ invalid json"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable goodTable = await playlist.LoadExternalTableAsync(new Uri(goodHeaderPath));
            var badTable = new BMSTable
            {
                name = "BadTarget",
                symbol = "B",
                Header_url = new Uri(badHeaderPath)
            };
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { goodTable, badTable }), Dispatcher.CurrentDispatcher);
            List<PlaylistSyncAttemptResult> syncResults = [];

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync([goodTable, badTable], syncResultCallback: syncResults.Add, reason: "test_partial_failure");

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, syncResults.Count);
            Assert.IsTrue(results.Any(result => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(results.Any(result => result.SourceTable == badTable && !result.Succeeded));
            Assert.IsTrue(syncResults.Any(result => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(syncResults.Any(result => result.SourceTable == badTable && !result.Succeeded));
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReloadTables_ReloadsHeadersWithoutScoreInitialization()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var _ = new LR2SongDBExtended(songDbPath))
            {
            }
            BMSPlaylist.EnsureSchema(songDbPath);
            var persistedTable = new BMSTable
            {
                playlist_id = 7001,
                name = "ReloadedTable",
                symbol = "R",
                Output_dir = "ReloadedTable"
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[]
                    {
                        new BMSTable
                        {
                            playlist_id = 1,
                            name = "OldTable",
                            symbol = "O",
                            Output_dir = "OldTable"
                        }
                    }),
                    Dispatcher.CurrentDispatcher)
            };
            bool hydrationQueued = false;
            playlist.StartupBackgroundTaskScheduler = delegate
            {
                hydrationQueued = true;
                return true;
            };

            playlist.ReloadTables();

            Assert.IsTrue(hydrationQueued);
            Assert.AreEqual(1, playlist.PlaylistEntriesHydrationRequestedVersion);
            Assert.AreEqual(1, playlist.BMSTables.Count);
            Assert.AreEqual("ReloadedTable", playlist.BMSTables[0].name);
            Assert.IsFalse(playlist.BMSTables[0].ArePlaylistEntriesLoaded);
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableHeaderToDB_PersistsPlaylistNameInStandaloneMode()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7101,
                name = "BeforeName",
                symbol = "BN",
                Output_dir = "BeforeName"
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, folder) VALUES (7101, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Song', '1');");
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            table.name = "AfterName";
            playlist.CommitBMSTableHeaderToDB(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual("AfterName", verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = 7101;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 7101;"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableWithEntriesToDB_PersistsPrefixRewrittenFoldersInStandaloneMode()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7201,
                name = "PrefixTable",
                symbol = "PT",
                Output_dir = "PrefixTable",
                compat_prefix = "LEVEL ",
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "LEVEL 1"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "LEVEL 2")
                ],
                Folder_order = ["LEVEL 2", "LEVEL 1"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            table.compat_prefix = "★";
            Assert.IsTrue(table.RewriteCompatibleFolderPrefix("LEVEL ", table.compat_prefix));
            playlist.CommitBMSTableWithEntriesToDB(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            string firstFolder = verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = 7201 AND md5 = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';");
            string secondFolder = verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = 7201 AND md5 = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';");
            CollectionAssert.AreEqual(new[] { "★1", "★2" }, new[] { firstFolder, secondFolder });
            Assert.AreEqual("★", verify.ExecuteScalar<string>("SELECT compat_prefix FROM playlist WHERE playlist_id = 7201;"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsLr2FolderRowsFromGeneratedText()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7301,
                name = "FolderTable",
                symbol = "FT",
                Output_dir = "FolderTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(outputBaseDir, "FolderTable", "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            string text = File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(text, "#TITLE Folder A");
            StringAssert.Contains(text, "#CATEGORY FolderTable");
            StringAssert.Contains(text, "#COMMAND song.hash");

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("Folder A", folder.title);
            Assert.AreEqual("FolderTable", folder.category);
            StringAssert.Contains(folder.command, "playlist_entry");
            Assert.AreEqual(0, folder.max);
            Assert.IsTrue(folder.date.HasValue);
            Assert.IsTrue(folder.adddate.HasValue);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_SyncsRootOutputRowsUnderTableDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootCustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "RootFolderTable",
                symbol = "RFT",
                Output_dir = "RootFolderTable",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Root Folder")
                ],
                Folder_order = ["Root Folder"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputPath = Path.Combine(rootOutputBaseDir, "RootFolderTable", "0001.lr2folder");
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder folder = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual(2, folder.type);
            Assert.AreEqual("Root Folder", folder.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(outputPath)),
                folder.parent);
            LR2SongDB.folder parentFolder = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(Path.GetDirectoryName(outputPath)));
            Assert.AreEqual(1, parentFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentFolder.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesHierarchicalRandomAndSortFolders()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
                | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder
                | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder
                | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder
                | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder;
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "Stella",
                symbol = "ST",
                Output_dir = "Stella",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "st0"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "st1")
                ],
                Folder_order = ["st0", "st1"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "Stella");
            string allText = ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder"));
            StringAssert.Contains(allText, "#TITLE Stella ALL");
            string st0Text = ReadShiftJisText(Path.Combine(outputDir, "0001.lr2folder"));
            StringAssert.Contains(st0Text, "#TITLE st0");
            string randomText = ReadShiftJisText(Path.Combine(outputDir, "0003.lr2folder"));
            StringAssert.Contains(randomText, "#TITLE Stella ALL RANDOM");
            StringAssert.Contains(randomText, "#MAXTRACKS 1");
            StringAssert.Contains(randomText, "ORDER BY random()");

            string noPlayText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"));
            StringAssert.Contains(noPlayText, "#TITLE Stella ALL NO PLAY");
            StringAssert.Contains(noPlayText, "score.clear IS NULL");
            string noPlayRandomText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0003.lr2folder"));
            StringAssert.Contains(noPlayRandomText, "#TITLE Stella ALL NO PLAY RANDOM");
            string fcText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "6 FC", "0000.lr2folder"));
            StringAssert.Contains(fcText, "(score.op_history & 16) = 0");
            string paText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "7 P.A", "0000.lr2folder"));
            StringAssert.Contains(paText, "(score.op_history & 16) != 0");
            string underAText = ReadShiftJisText(Path.Combine(outputDir, "DJ LEVEL", "UNDER A", "0000.lr2folder"));
            StringAssert.Contains(underAText, "score.rank < 6 OR score.rank IS NULL");
            string bpmSortText = ReadShiftJisText(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder"));
            StringAssert.Contains(bpmSortText, "FROM chart_info");
            StringAssert.Contains(bpmSortText, "mainbpm");
            StringAssert.Contains(bpmSortText, "IS NULL ASC");
            string bpSortText = ReadShiftJisText(Path.Combine(outputDir, "BP SORT", "0000.lr2folder"));
            StringAssert.Contains(bpSortText, "score.minbp IS NULL ASC");
            string playCountSortText = ReadShiftJisText(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder"));
            StringAssert.Contains(playCountSortText, "score.playcount IS NULL ASC");
            StringAssert.Contains(playCountSortText, "score.playcount DESC");

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder")));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(Path.Combine(outputDir, "CLEAR FOLDER"))));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesRootRandomFoldersAfterNormalFolders()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
                | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder;
            BMSTableEntry first = CreateEntryWithLevel("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 1);
            BMSTableEntry second = CreateEntryWithLevel("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 2);
            var table = new BMSTable
            {
                playlist_id = 7308,
                name = "RootRandomOrder",
                symbol = "RRO",
                Output_dir = "RootRandomOrder",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries = [first, second],
                Folder_order = ["1", "2"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "RootRandomOrder");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder")), "#TITLE RootRandomOrder ALL");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0001.lr2folder")), "#TITLE 1");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0002.lr2folder")), "#TITLE 2");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0003.lr2folder")), "#TITLE LEVEL 1");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0004.lr2folder")), "#TITLE LEVEL 2");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0005.lr2folder")), "#TITLE RootRandomOrder ALL RANDOM");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0008.lr2folder")), "#TITLE LEVEL 1 RANDOM");
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_TreatsLegacyAllFoldersMaskAsAllDisabled()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "LegacyDisabled",
                symbol = "LD",
                Output_dir = "LegacyDisabled",
                ignore_folder_output = LR2SongDBExtended.playlist.LegacyAllFolders,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LegacyDisabled");
            Assert.IsFalse(Directory.Exists(outputDir));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path LIKE ?;", outputDir + "%"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesExtraLr2FolderRowsInSameOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7306,
                name = "SameOutputPrune",
                symbol = "SOP",
                Output_dir = "SameOutputPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string outputDir = Path.Combine(outputBaseDir, "SameOutputPrune");
            string stalePath = Path.Combine(outputDir, "external.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(stalePath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string expectedPath = Path.Combine(outputDir, "0001.lr2folder");
            Assert.IsTrue(File.Exists(expectedPath));
            Assert.IsFalse(File.Exists(stalePath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", expectedPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", stalePath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesDbOnlyStaleHierarchicalRowsInSameOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder;
            var table = new BMSTable
            {
                playlist_id = 7307,
                name = "HierarchyPrune",
                symbol = "HP",
                Output_dir = "HierarchyPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("ffffffffffffffffffffffffffffffff", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "HierarchyPrune");
            string clearDir = Path.Combine(outputDir, "CLEAR FOLDER");
            string noPlayDir = Path.Combine(clearDir, "0 NO PLAY");
            string noPlayFile = Path.Combine(noPlayDir, "0000.lr2folder");
            using (var verifyInitial = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", noPlayFile));
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
                Assert.AreEqual(1L, verifyInitial.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
            }
            Directory.Delete(clearDir, recursive: true);
            Assert.IsFalse(Directory.Exists(clearDir));

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsFalse(Directory.Exists(clearDir));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", noPlayFile));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Path.Combine(outputDir, "0000.lr2folder")));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_RewritesExistingLr2FolderFiles()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string outputDir = Path.Combine(outputBaseDir, "BulkRewrite");
            string outputFile = Path.Combine(outputDir, "0000.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputFile, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            DateTime oldTimestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(outputFile, oldTimestamp);
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7612,
                name = "BulkRewrite",
                symbol = "BR",
                Output_dir = "BulkRewrite",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_bulk_rewrite");

            string text = ReadShiftJisText(outputFile);
            Assert.AreNotEqual(oldTimestamp, File.GetLastWriteTimeUtc(outputFile));
            Assert.IsTrue(text.IndexOf("#TITLE stale", StringComparison.Ordinal) < 0);
            StringAssert.Contains(text, "#TITLE BulkRewrite ALL");
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == outputFile);
            Assert.AreEqual(File.GetLastWriteTimeUtc(outputFile).ToUnixtime(), row.date);
            Assert.AreEqual("BulkRewrite ALL", row.title);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_ProgressUsesFolderTableSyncLabel()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7616,
                name = "ProgressLabel",
                symbol = "PL",
                Output_dir = "ProgressLabel",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var progressLabels = new List<string>();

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB(
                [table],
                "test_progress_label",
                (_, _, label) => progressLabels.Add(label));

            CollectionAssert.Contains(progressLabels, Resources.Custom_folder_db_sync_progress_single_label);
            Assert.IsFalse(progressLabels.Contains(Resources.Lr2_song_db_sync_status_running));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_SkipsCurrentStatusAfterPhysicalSurfaceCheck()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7617,
                name = "StatusNoOp",
                symbol = "SNO",
                Output_dir = "StatusNoOp",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_status_noop");
            string outputDirectory = Path.Combine(outputBaseDir, "StatusNoOp");
            int physicalProviderCallCount = 0;
            playlist.CustomFolderOutputPhysicalSurfaceProvider = () =>
            {
                physicalProviderCallCount++;
                return CustomFolderOutputPhysicalSurface.FromEntries(
                    Directory.EnumerateFiles(outputDirectory, "*.lr2folder", System.IO.SearchOption.AllDirectories)
                        .Select(path => new RootFileEnumerationEntry(path, File.GetLastWriteTimeUtc(path))),
                    discoveryComplete: true);
            };
            var operations = new List<string>();
            playlist.Lr2FolderSyncMutationGuard = operations.Add;

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_status_noop");

            Assert.AreEqual(0, repairedCount);
            Assert.AreEqual(1, physicalProviderCallCount);
            CollectionAssert.AreEqual(Array.Empty<string>(), operations);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                    table.playlist_id.Value));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_ClearsCustomFolderOutputStatus()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7618,
                name = "StatusDelete",
                symbol = "SD",
                Output_dir = "StatusDelete",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);
            using (var before = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    1L,
                    before.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                        table.playlist_id.Value));
            }

            playlist.RemoveCustomFolder(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                    table.playlist_id.Value));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesFilesAndRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7613,
                name = "DisableAllOutput",
                symbol = "DAO",
                Output_dir = "DisableAllOutput",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_output");
            string outputDir = Path.Combine(outputBaseDir, "DisableAllOutput");
            Assert.IsTrue(Directory.Exists(outputDir));
            Assert.IsTrue(Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using (var verifySeed = new LR2SongDBExtended(songDbPath))
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_output");

            Assert.IsFalse(Directory.Exists(outputDir)
                && Directory.EnumerateFiles(outputDir, "*.lr2folder", System.IO.SearchOption.AllDirectories).Any());
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList()
                .Count(row => row.path != null && row.path.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DisablingAllOutputPrunesDbOnlyRootRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string rootOutputBaseDir = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7614,
                name = "DisableAllRootOutput",
                symbol = "DAR",
                Output_dir = "DisableAllRootOutput",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_disable_all_root_output");
            string outputDir = Path.Combine(rootOutputBaseDir, "DisableAllRootOutput");
            string relativeOutputDir = Path.Combine("LR2files", "CustomFolder", "DisableAllRootOutput");
            using (var verifySeed = new LR2SongDBExtended(songDbPath))
            {
                Assert.IsTrue(verifySeed.Table<LR2SongDB.folder>().ToList()
                    .Any(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
            }
            Directory.Delete(outputDir, recursive: true);

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders;
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_disable_all_root_output");

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().ToList()
                .Count(row => row.path != null && row.path.StartsWith(relativeOutputDir, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFoldersAndCommitHeadersToDB_DoesNotCommitHeaderWhenOutputFails()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            LR2SongDBExtended.playlist.CustomFolderType initialMask =
                LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder;
            var table = new BMSTable
            {
                playlist_id = 7615,
                name = "CommitFailure",
                symbol = "CF",
                Output_dir = "CommitFailure",
                ignore_folder_output = initialMask,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_commit_failure");
            using (var verifySeed = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(
                    (int)initialMask,
                    verifySeed.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = 7615;"));
            }

            string outputBaseFile = Path.Combine(tempDirectory, "output-base-file");
            File.WriteAllText(outputBaseFile, "not a directory", Encoding.UTF8);
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseFile;
            table.symbol = "CF2";

            Exception? exception = null;
            try
            {
                playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_commit_failure");
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            Assert.IsNotNull(exception);
            Assert.IsTrue(
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is ArgumentException
                || exception is NotSupportedException
                || exception is PathTooLongException,
                exception.GetType().FullName);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                (int)initialMask,
                verify.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = 7615;"));
            Assert.AreEqual(
                "CF",
                verify.ExecuteScalar<string>("SELECT symbol FROM playlist WHERE playlist_id = 7615;"));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_BatchesMissingTablesIntoSingleFolderSync()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var firstTable = new BMSTable
            {
                playlist_id = 7501,
                name = "MissingOne",
                symbol = "M1",
                Output_dir = "MissingOne",
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var secondTable = new BMSTable
            {
                playlist_id = 7502,
                name = "MissingTwo",
                symbol = "M2",
                Output_dir = "MissingTwo",
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder B"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { firstTable, secondTable }),
                    Dispatcher.CurrentDispatcher)
            };
            List<string> operations = [];
            playlist.Lr2FolderSyncMutationGuard = operations.Add;

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test");

            Assert.AreEqual(2, repairedCount);
            CollectionAssert.AreEqual(new[] { "playlist_lr2folder_batch_sync" }, operations);
            Assert.IsTrue(File.Exists(Path.Combine(outputBaseDir, "MissingOne", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputBaseDir, "MissingTwo", "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.IsTrue(verify.Table<LR2SongDB.folder>().ToList().Count(row =>
                row.type == 2
                && row.path != null
                && row.path.StartsWith(outputBaseDir, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetExtension(row.path), ".lr2folder", StringComparison.OrdinalIgnoreCase)) >= 2);
            LR2SongDB.folder outputBaseRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(outputBaseDir));
            Assert.AreEqual(1, outputBaseRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseRow.parent);
            string firstOutputDir = Path.Combine(outputBaseDir, "MissingOne");
            LR2SongDB.folder firstTableRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(firstOutputDir));
            Assert.AreEqual(1, firstTableRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputBaseDir), firstTableRow.parent);
            LR2SongDB.folder firstFolderFileRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == Path.Combine(firstOutputDir, "0000.lr2folder"));
            Assert.AreEqual(2, firstFolderFileRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(firstOutputDir), firstFolderFileRow.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_RewritesExistingFileWhenFolderProjectionMissing()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string outputDir = Path.Combine(outputBaseDir, "ExistingProjectionMissing");
            string outputFile = Path.Combine(outputDir, "0000.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputFile, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            DateTime oldTimestamp = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(outputFile, oldTimestamp);
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7601,
                name = "ExistingProjectionMissing",
                symbol = "EPM",
                Output_dir = "ExistingProjectionMissing",
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_projection_missing");

            Assert.AreEqual(1, repairedCount);
            Assert.AreNotEqual(oldTimestamp, File.GetLastWriteTimeUtc(outputFile));
            Assert.IsTrue(File.ReadAllText(outputFile, Encoding.GetEncoding("shift_jis")).IndexOf("#TITLE stale", StringComparison.Ordinal) < 0);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == outputFile);
            Assert.AreEqual(File.GetLastWriteTimeUtc(outputFile).ToUnixtime(), row.date);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_RestoresMissingDirectoryRowsWithoutRewritingFiles()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder;
            var table = new BMSTable
            {
                playlist_id = 7605,
                name = "DirectoryRowRepair",
                symbol = "DRR",
                Output_dir = "DirectoryRowRepair",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(1, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed_directory_rows"));
            string outputDir = Path.Combine(outputBaseDir, "DirectoryRowRepair");
            string clearDir = Path.Combine(outputDir, "CLEAR FOLDER");
            string noPlayDir = Path.Combine(clearDir, "0 NO PLAY");
            string noPlayFile = Path.Combine(noPlayDir, "0000.lr2folder");
            DateTime noPlayTimestamp = File.GetLastWriteTimeUtc(noPlayFile);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("DELETE FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir));
                db.Execute("DELETE FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir));
            }

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_directory_rows_missing");

            Assert.AreEqual(1, repairedCount);
            Assert.AreEqual(noPlayTimestamp, File.GetLastWriteTimeUtc(noPlayFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(clearDir)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(noPlayDir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_PrunesDbOnlyStaleDirectoryRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder;
            var table = new BMSTable
            {
                playlist_id = 7606,
                name = "StaleDirectoryRowRepair",
                symbol = "SDR",
                Output_dir = "StaleDirectoryRowRepair",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(1, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed_stale_directory_rows"));
            string outputDir = Path.Combine(outputBaseDir, "StaleDirectoryRowRepair");
            string clearDir = Path.Combine(outputDir, "CLEAR FOLDER");
            string noPlayFile = Path.Combine(clearDir, "0 NO PLAY", "0000.lr2folder");
            string staleNoPlayDir = Path.Combine(clearDir, "NO PLAY");
            DateTime noPlayTimestamp = File.GetLastWriteTimeUtc(noPlayFile);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Insert(new LR2SongDB.folder
                {
                    path = Lr2FolderPath.ToFolderPath(staleNoPlayDir),
                    title = "NO PLAY",
                    type = 1,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(clearDir),
                    date = noPlayTimestamp.ToUnixtime(),
                    adddate = noPlayTimestamp.ToUnixtime()
                });
            }

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_stale_directory_rows");

            Assert.AreEqual(1, repairedCount);
            Assert.AreEqual(noPlayTimestamp, File.GetLastWriteTimeUtc(noPlayFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(staleNoPlayDir)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(Path.Combine(clearDir, "0 NO PLAY"))));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_PrunesDbOnlyRowsWhenNoExpectedFiles()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7609,
                name = "NoExpectedCustomFolder",
                symbol = "NEC",
                Output_dir = "NoExpectedCustomFolder",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            string outputDir = Path.Combine(outputBaseDir, "NoExpectedCustomFolder");
            string staleDir = Path.Combine(outputDir, "STALE");
            DateTime staleTimestamp = new(2026, 6, 3, 1, 2, 3, DateTimeKind.Utc);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Insert(new LR2SongDB.folder
                {
                    path = Lr2FolderPath.ToFolderPath(staleDir),
                    title = "STALE",
                    type = 1,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir),
                    date = staleTimestamp.ToUnixtime(),
                    adddate = staleTimestamp.ToUnixtime()
                });
            }

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_no_expected_files");

            Assert.AreEqual(1, repairedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(staleDir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2SongDbSyncCustomFolderPreparation_DoesNotReadCurrentExistingManagedFile()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7602,
                name = "LockedCurrent",
                symbol = "LC",
                Output_dir = "LockedCurrent",
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder B"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(1, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed"));
            string outputFile = Path.Combine(outputBaseDir, "LockedCurrent", "0000.lr2folder");
            Assert.IsTrue(File.Exists(outputFile));

            using var locked = new FileStream(outputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Lr2SongDbSyncPreparedDataSurface surface =
                playlist.ReOutputAllCustomFoldersForLr2SongDbSync("test_locked_current");

            CollectionAssert.Contains(surface.Lr2FolderFilePaths.ToList(), outputFile);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_RewritesOnlyMissingExpectedFile()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            string outputDir = Path.Combine(outputBaseDir, "PartialMissing");
            string firstOutputFile = Path.Combine(outputDir, "0000.lr2folder");
            string secondOutputFile = Path.Combine(outputDir, "0001.lr2folder");
            string thirdOutputFile = Path.Combine(outputDir, "0002.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(firstOutputFile, "#TITLE PartialMissing ALL", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(secondOutputFile, "#TITLE Folder A", Encoding.GetEncoding("shift_jis"));
            DateTime firstTimestamp = new(2026, 6, 1, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(firstOutputFile, firstTimestamp);
            File.SetLastWriteTimeUtc(secondOutputFile, firstTimestamp);
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.Insert(new LR2SongDB.folder
                {
                    path = firstOutputFile,
                    title = "PartialMissing ALL",
                    type = 2,
                    category = "PartialMissing",
                    command = "song.hash in (SELECT md5 FROM playlist_entry WHERE playlist_id = 7603 AND is_removed = 0)",
                    info_a = "PartialMissing",
                    info_b = "ALL",
                    max = 0,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir),
                    date = firstTimestamp.ToUnixtime(),
                    adddate = firstTimestamp.ToUnixtime()
                });
                db.Insert(new LR2SongDB.folder
                {
                    path = secondOutputFile,
                    title = "Folder A",
                    type = 2,
                    category = "PartialMissing",
                    command = "song.hash in (SELECT md5 FROM playlist_entry WHERE playlist_id = 7603 AND is_removed = 0 AND folder = 'Folder A')",
                    info_a = "PartialMissing",
                    info_b = "Folder: Folder A",
                    max = 0,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir),
                    date = firstTimestamp.ToUnixtime(),
                    adddate = firstTimestamp.ToUnixtime()
                });
            }
            var table = new BMSTable
            {
                playlist_id = 7603,
                name = "PartialMissing",
                symbol = "PM",
                Output_dir = "PartialMissing",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_partial_missing");

            Assert.AreEqual(1, repairedCount);
            Assert.AreEqual(firstTimestamp, File.GetLastWriteTimeUtc(firstOutputFile));
            Assert.AreEqual(firstTimestamp, File.GetLastWriteTimeUtc(secondOutputFile));
            Assert.IsTrue(File.Exists(thirdOutputFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", firstOutputFile));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", secondOutputFile));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", thirdOutputFile));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void Lr2SongDbSyncCustomFolderPreparation_RemovesExtraLr2FolderInManagedOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7604,
                name = "ExternalCoLocated",
                symbol = "EC",
                Output_dir = "ExternalCoLocated",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(1, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed_external_colocated"));
            string outputDir = Path.Combine(outputBaseDir, "ExternalCoLocated");
            string expectedFile = Path.Combine(outputDir, "0000.lr2folder");
            string extraFile = Path.Combine(outputDir, "external.lr2folder");
            File.WriteAllText(extraFile, "#TITLE External", Encoding.GetEncoding("shift_jis"));
            DateTime extraTimestamp = new(2026, 6, 2, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(extraFile, extraTimestamp);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Insert(new LR2SongDB.folder
                {
                    path = extraFile,
                    title = "External",
                    type = 2,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDir),
                    date = extraTimestamp.ToUnixtime(),
                    adddate = extraTimestamp.ToUnixtime()
                });
            }

            Lr2SongDbSyncPreparedDataSurface surface =
                playlist.ReOutputAllCustomFoldersForLr2SongDbSync("test_external_colocated");

            Assert.IsFalse(File.Exists(extraFile));
            CollectionAssert.Contains(surface.Lr2FolderFilePaths.ToList(), expectedFile);
            Assert.AreEqual(0, surface.Lr2FolderScopeDirectories.Count);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", extraFile));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_ProtectsNestedManagedOutputDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var parentTable = new BMSTable
            {
                playlist_id = 7607,
                name = "NestedParent",
                symbol = "NP",
                Output_dir = "NestedParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7608,
                name = "NestedChild",
                symbol = "NC",
                Output_dir = Path.Combine("NestedParent", "NestedChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")
                ],
                Folder_order = ["Child Folder"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { parentTable, childTable }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(2, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed_nested_outputs"));
            string childOutputDir = Path.Combine(outputBaseDir, "NestedParent", "NestedChild");
            string childOutputFile = Path.Combine(childOutputDir, "0000.lr2folder");
            Assert.IsTrue(File.Exists(childOutputFile));
            DateTime childTimestamp = File.GetLastWriteTimeUtc(childOutputFile);

            playlist.ReOutputCustomFolderAndCommitToDB(parentTable);

            Assert.IsTrue(File.Exists(childOutputFile));
            Assert.AreEqual(childTimestamp, File.GetLastWriteTimeUtc(childOutputFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childOutputFile));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(childOutputDir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MissingCustomFolderOutputRepair_PrunesNestedManagedOutputRowsWhenBothAreTargets()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string outputBaseDir = Path.Combine(bmsRoot, "#BeMusicSeeker");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var parentTable = new BMSTable
            {
                playlist_id = 7610,
                name = "BatchNestedParent",
                symbol = "BNP",
                Output_dir = "BatchNestedParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7611,
                name = "BatchNestedChild",
                symbol = "BNC",
                Output_dir = Path.Combine("BatchNestedParent", "BatchNestedChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")
                ],
                Folder_order = ["Child Folder"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, bmsRoot))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { parentTable, childTable }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(2, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed_batch_nested"));
            string parentOutputDir = Path.Combine(outputBaseDir, "BatchNestedParent");
            string childOutputDir = Path.Combine(parentOutputDir, "BatchNestedChild");
            string parentStaleDir = Path.Combine(parentOutputDir, "STALE_PARENT");
            string childStaleDir = Path.Combine(childOutputDir, "STALE_CHILD");
            DateTime staleTimestamp = new(2026, 6, 4, 1, 2, 3, DateTimeKind.Utc);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Insert(new LR2SongDB.folder
                {
                    path = Lr2FolderPath.ToFolderPath(parentStaleDir),
                    title = "STALE_PARENT",
                    type = 1,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(parentOutputDir),
                    date = staleTimestamp.ToUnixtime(),
                    adddate = staleTimestamp.ToUnixtime()
                });
                db.Insert(new LR2SongDB.folder
                {
                    path = Lr2FolderPath.ToFolderPath(childStaleDir),
                    title = "STALE_CHILD",
                    type = 1,
                    parent = Lr2SongFolderParentNormalizer.ComputeDirectoryHash(childOutputDir),
                    date = staleTimestamp.ToUnixtime(),
                    adddate = staleTimestamp.ToUnixtime()
                });
            }

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_batch_nested_stale");

            Assert.AreEqual(2, repairedCount);
            Assert.IsTrue(File.Exists(Path.Combine(childOutputDir, "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(parentStaleDir)));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(childStaleDir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void CommitBMSTableEntry_ReoutputsLr2FolderRowsForLevelProjection()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            BMSTableEntry entry = CreateEntryWithLevel("cccccccccccccccccccccccccccccccc", 1);
            entry.playlist_id = 7303;
            var table = new BMSTable
            {
                playlist_id = 7303,
                name = "LevelTable",
                symbol = "LT",
                Output_dir = "LevelTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder
                    | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
                    | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
                    | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder
                    | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
                entries = [entry]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            entry.level = 12;

            playlist.CommitBMSTableEntry(entry);

            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.folder> folders = verify.Table<LR2SongDB.folder>().ToList();
            Assert.AreEqual(0, folders.Count(row => row.title == "LEVEL 1"));
            LR2SongDB.folder levelFolder = folders.Single(row => row.title == "LEVEL 12");
            Assert.AreEqual(2, levelFolder.type);
            StringAssert.Contains(levelFolder.command, "playlist_entry");
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectoryAndCommitToDB_PrunesOldRootFlagOutputRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootCustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7304,
                name = "ToggleTable",
                symbol = "TT",
                Output_dir = "ToggleTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(outputBaseDir, "ToggleTable");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldParentPath = Lr2FolderPath.ToFolderPath(oldOutputDir);
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldParentPath, title = "stale parent", type = 1 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            table.is_root_folder = true;
            string newOutputDir = Path.Combine(rootOutputBaseDir, "ToggleTable");

            playlist.MigrateCustomFolderOutputDirectoryAndCommitToDB(table, oldOutputDir, newOutputDir);

            string newPath = Path.Combine(newOutputDir, "0001.lr2folder");
            Assert.IsFalse(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(newPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldPath));
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldParentPath));
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == newPath);
            Assert.AreEqual("Folder A", generated.title);
            Assert.AreEqual(
                Lr2SongFolderParentNormalizer.ComputeDirectoryHash(Path.GetDirectoryName(newPath)),
                generated.parent);
            LR2SongDB.folder parentFolder = verify.Table<LR2SongDB.folder>().Single(row => row.path == Lr2FolderPath.ToFolderPath(Path.GetDirectoryName(newPath)));
            Assert.AreEqual(1, parentFolder.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, parentFolder.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2RootPath = previousLr2RootPath;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_PrunesOnlyExactOutputDirectoryRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7401,
                name = "Folder",
                symbol = "F",
                Output_dir = "Folder"
            };
            string targetPath = Path.Combine(outputBaseDir, "Folder", "0000.lr2folder");
            string siblingPath = Path.Combine(outputBaseDir, "Folder2", "0000.lr2folder");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "target", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = siblingPath, title = "sibling", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.RemoveCustomFolder(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            CollectionAssert.AreEquivalent(
                new[] { siblingPath },
                verify.Table<LR2SongDB.folder>()
                    .ToList()
                    .Select(row => row.path)
                    .Where(path => path.StartsWith(outputBaseDir, StringComparison.OrdinalIgnoreCase))
                    .ToArray());
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_PrunesRowsWhenNoFolderFilesAreGenerated()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7402,
                name = "EmptyTable",
                symbol = "ET",
                Output_dir = "EmptyTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
            };
            string stalePath = Path.Combine(outputBaseDir, "EmptyTable", "0000.lr2folder");
            string outputDir = Path.GetDirectoryName(stalePath);
            Directory.CreateDirectory(outputDir);
            string preservedPath = Path.Combine(outputDir, "keep.txt");
            File.WriteAllText(preservedPath, "not managed by BeMusicSeeker");
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == stalePath));
            Assert.IsTrue(File.Exists(preservedPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_PrunesOldPathWithoutDeletingNestedNewRows()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7403,
                name = "NestedTable",
                symbol = "NT",
                Output_dir = "NestedTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(tempDirectory, "CustomFolder", "NestedTable");
            string newOutputDir = Path.Combine(oldOutputDir, "Moved");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string newPath = Path.Combine(newOutputDir, "0001.lr2folder");
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.MigrateCustomFolderOutputDirectory(table, oldOutputDir, newOutputDir);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldPath));
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == newPath);
            Assert.AreEqual("Folder A", generated.title);
            Assert.IsTrue(File.Exists(newPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_TreatsTrailingSeparatorAsSameDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7404,
                name = "TrailingTable",
                symbol = "TT",
                Output_dir = "TrailingTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string outputDir = Path.Combine(tempDirectory, "CustomFolder", "TrailingTable");
            string outputPath = Path.Combine(outputDir, "0001.lr2folder");
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(outputPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = outputPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.MigrateCustomFolderOutputDirectory(table, outputDir + Path.DirectorySeparatorChar, outputDir);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputPath);
            Assert.AreEqual("Folder A", generated.title);
            Assert.IsTrue(File.Exists(outputPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReplaceBmsFileLevelByTableEntryLevel_UpdatesOnlyExistingSongLevel()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            string chartPath = Path.Combine(tempDirectory, "chart.bms");
            string missingPath = Path.Combine(tempDirectory, "missing.bms");
            const string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string missingMd5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var file = new TestableBmsFile
            {
                path = chartPath,
                level = 3,
                date = 123456,
                adddate = 98765,
                tag = "keep-tag"
            };
            file.SetHash(md5);
            file.SetTitleForTest("KeepTitle");
            var missingFile = new TestableBmsFile
            {
                path = missingPath,
                level = 1
            };
            missingFile.SetHash(missingMd5);
            missingFile.SetTitleForTest("Missing");
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(file, typeof(LR2SongDB.song));
                setup.Execute("DELETE FROM song WHERE path = ?;", missingPath);
            }
            var library = new BMSLibrary(songDbPath)
            {
                BMSFiles = [file, missingFile],
                BmsonSongs = []
            };
            var table = new BMSTable
            {
                entries =
                [
                    CreateEntryWithLevel(md5, 12),
                    CreateEntryWithLevel(missingMd5, 11)
                ]
            };

            library.ReplaceBmsFileLevelByTableEntryLevel(table);

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single(candidate => candidate.path == chartPath);
            Assert.AreEqual(12, row.level);
            Assert.AreEqual("KeepTitle", row.title);
            Assert.AreEqual(123456, row.date);
            Assert.AreEqual(98765, row.adddate);
            Assert.AreEqual("keep-tag", row.tag);
            Assert.AreEqual(0, verify.Table<LR2SongDB.song>().Count(candidate => candidate.path == missingPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void LoadStartupPlaylistEntries_UsesPlaylistIdProjectionAndKeepsRemovedRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NULL, md5 TEXT NULL, sha256 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", 10, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "active", 0);
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", 10, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "removed", 1);
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", null, "cccccccccccccccccccccccccccccccc", "orphan", 0);
            }

            PlaylistEntriesHydrationLoadResult result = new BmsLibraryDbGateway(songDbPath).LoadStartupPlaylistEntries();

            Assert.AreEqual("startup_entries", result.Projection);
            Assert.AreEqual(2, result.RowCount);
            Assert.IsTrue(result.Entries.Any(entry => entry.title == "active" && !entry.is_removed));
            Assert.IsTrue(result.Entries.Any(entry => entry.title == "removed" && entry.is_removed));
            Assert.IsFalse(result.Entries.Any(entry => entry.title == "orphan"));
            Assert.IsTrue(result.DbReadMs >= 0);
            Assert.IsTrue(result.MaterializeMs >= 0);
            Assert.IsTrue(result.ReadOnly);
            Assert.AreEqual(0L, result.DbLockWaitMs);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static byte[] CreateUtf8BomBytes(string text)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
    }

    private static string CreateTempSongDbPath(string tempDirectory)
    {
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        using (var db = new LR2SongDBExtended(tempSongDbPath))
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
        }
        return tempSongDbPath;
    }

    private static string ReadShiftJisText(string path)
    {
        return File.ReadAllText(path, Encoding.GetEncoding("shift_jis"));
    }

    private static LR2Config CreateLr2Config(string lr2RootPath, params string[] bmsRoots)
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
            "<config><system><customfolder>0</customfolder><titleflash>24</titleflash></system><jukebox>" + pathElements + "</jukebox></config>",
            Encoding.UTF8);
        return new LR2Config(configPath);
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }

    private static void InvokeSyncCustomFolderRows(BMSPlaylist playlist, string outputDir, IReadOnlyCollection<Lr2FolderFileSyncItem> items)
    {
        MethodInfo methodInfo = typeof(BMSPlaylist).GetMethod("SyncCustomFolderRows", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        methodInfo.Invoke(playlist, [outputDir, items, null, null, null]);
    }

    private static int InvokeRepairMissingCustomFolderOutputsAfterHydration(BMSPlaylist playlist, string reason)
    {
        MethodInfo methodInfo = typeof(BMSPlaylist).GetMethod("RepairMissingCustomFolderOutputsAfterHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (int)methodInfo.Invoke(playlist, [reason]);
    }

    private static BMSTableEntry CreateEntry(string md5, string folder)
    {
        return new BMSTableEntry(DynamicJson.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":\"" + folder + "\"}"));
    }

    private static BMSTableEntry CreateEntryWithLevel(string md5, int level)
    {
        return new BMSTableEntry(DynamicJson.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":" + level + "}"));
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetTitleForTest(string value)
        {
            Title = value;
        }
    }
}
