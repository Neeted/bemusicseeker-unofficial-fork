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
using BeMusicSeeker.ViewModels;
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
    public void EnsureSchema_DoesNotMigrateLastPlaySortOutputMaskOrCreateLegacyMarker()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            const int playlistId = 6101;
            const int legacyMask = 0x7FE;
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.InsertOrReplace(new BMSTable
                {
                    playlist_id = playlistId,
                    name = "LegacyMask",
                    symbol = "LM",
                    ignore_folder_output = (LR2SongDBExtended.playlist.CustomFolderType)legacyMask
                }, typeof(LR2SongDBExtended.playlist));
            }

            BMSPlaylist.EnsureSchema(songDbPath);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                legacyMask,
                verify.ExecuteScalar<int>("SELECT ignore_folder_output FROM playlist WHERE playlist_id = ?;", playlistId));
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'app_schema_version';"));
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
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_ResetsSelectedPropertiesFromRawExternalData()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"External:Name\",\r\n\"symbol\":\"★\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9501;
            table.name = "Local Name";
            table.symbol = "L";
            table.compat_prefix = string.Empty;
            table.Output_dir = "CustomOutput";
            table.DisableExternalSync();
            table.entries[0].folder = "1";
            table.entries.Add(CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "★1"));
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
            }
            table.Folder_order = ["1", "★1"];
            playlist.BMSTables = new DispatcherCollection<BMSTable>(
                new ObservableCollection<BMSTable>(new[] { table }),
                Dispatcher.CurrentDispatcher);
            var viewModel = new MainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);

            await viewModel.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                new MainWindowViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                {
                    Name = true,
                    Symbol = true,
                    CompatPrefix = true,
                    OutputDirectory = true
                });

            Assert.AreEqual("External:Name", table.name);
            Assert.AreEqual("★", table.symbol);
            Assert.AreEqual("★", table.compat_prefix);
            Assert.AreEqual(BMSTable.CreateDefaultOutputDirectoryName("External:Name"), table.Output_dir);
            Assert.IsFalse(table.is_external_sync);
            Assert.AreEqual("★1", table.entries[0].folder);
            Assert.AreEqual("★★1", table.entries[1].folder);
            CollectionAssert.AreEqual(new[] { "★1", "★★1" }, table.Folder_order);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == 9501);
            Assert.AreEqual("External:Name", persisted.name);
            Assert.AreEqual("★", persisted.symbol);
            Assert.AreEqual("★", persisted.compat_prefix);
            Assert.IsNull(persisted.output_dir);
            Assert.AreEqual("★1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;", 9501, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            Assert.AreEqual("★★1", verify.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;", 9501, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
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
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_DoesNotTakeAnotherPendingTableCurrentOutputDir()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = true;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string dataJsonPath = Path.Combine(tempDirectory, "score.json");
            string headerAPath = Path.Combine(tempDirectory, "header-a.json");
            string headerBPath = Path.Combine(tempDirectory, "header-b.json");
            File.WriteAllBytes(dataJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));
            File.WriteAllBytes(headerAPath, CreateUtf8BomBytes("{\r\n\"name\":\"OutputB\",\r\n\"symbol\":\"A2\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(headerBPath, CreateUtf8BomBytes("{\r\n\"name\":\"OutputB\",\r\n\"symbol\":\"B2\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(songDbPath);
            BMSTable tableA = await playlist.LoadExternalTableAsync(new Uri(headerAPath));
            BMSTable tableB = await playlist.LoadExternalTableAsync(new Uri(headerBPath));
            tableA.playlist_id = 9502;
            tableA.name = "LocalA";
            tableA.symbol = "A1";
            tableA.Output_dir = "LocalA";
            tableB.playlist_id = 9503;
            tableB.name = "OutputB";
            tableB.symbol = "B1";
            tableB.Output_dir = "OutputB";
            playlist.BMSTables = new DispatcherCollection<BMSTable>(
                new ObservableCollection<BMSTable>(new[] { tableA, tableB }),
                Dispatcher.CurrentDispatcher);
            var viewModel = new MainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);

            await viewModel.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                [new PlaylistSummaryRow { TableRef = tableA }, new PlaylistSummaryRow { TableRef = tableB }],
                new MainWindowViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                {
                    Name = true,
                    Symbol = true,
                    OutputDirectory = true
                });

            Assert.AreEqual("LocalA", tableA.name);
            Assert.AreEqual("A1", tableA.symbol);
            Assert.AreEqual("LocalA", tableA.Output_dir);
            Assert.AreEqual("OutputB", tableB.name);
            Assert.AreEqual("B2", tableB.symbol);
            Assert.AreEqual("OutputB", tableB.Output_dir);
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
    public async Task ApplyPlaylistSummaryExternalPropertyInitializationAsync_ReoutputsCustomFolderWhenNameChangesWithoutOutputDirChange()
    {
        bool previousEnablePlaylistUrlCompletion = Settings.Default.EnablePlaylistUrlCompletion;
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        bool previousEnableBeatorajaBmtOutput = Settings.Default.EnableBeatorajaBmtOutput;
        string previousBeatorajaBmtTablePath = Settings.Default.BeatorajaBmtTablePath;
        string previousBeatorajaRootPath = Settings.Default.BeatorajaRootPath;
        Settings.Default.EnablePlaylistUrlCompletion = false;
        Settings.Default.OperationModeLR2DB = false;
        Settings.Default.EnableBeatorajaBmtOutput = false;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = false
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ExternalName\",\r\n\"symbol\":\"EX\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"External Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                BeatorajaBmtOptionsSnapshot.CreateCurrent,
                getOutputSettings);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9504;
            table.name = "LocalName";
            table.symbol = "LC";
            table.Output_dir = "StableOutput";
            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            foreach (BMSTableEntry entry in table.entries)
            {
                entry.playlist_id = table.playlist_id;
                entry.folder = "Folder A";
            }
            table.Folder_order = ["Folder A"];
            playlist.BMSTables = new DispatcherCollection<BMSTable>(
                new ObservableCollection<BMSTable>(new[] { table }),
                Dispatcher.CurrentDispatcher);
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            string outputPath = Path.Combine(providerOutputBaseDir, "StableOutput", "0001.lr2folder");
            string beforeText = File.ReadAllText(outputPath, Encoding.GetEncoding("shift_jis"));
            StringAssert.Contains(beforeText, "#CATEGORY LocalName");
            providerCallCount = 0;
            var viewModel = new ApplicationComposition(
                BmsLibraryOptionsSnapshot.CreateCurrent,
                customFolderOutputSettingsProvider: getOutputSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);
            Settings.Default.BeatorajaRootPath = string.Empty;
            Settings.Default.BeatorajaBmtTablePath = Path.Combine(tempDirectory, "beatoraja-table", "table.json");
            Settings.Default.EnableBeatorajaBmtOutput = true;
            var queuedBmtReasons = new List<string>();
            playlist.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                if (string.Equals(name, "beatoraja_bmt_export", StringComparison.Ordinal))
                {
                    queuedBmtReasons.Add(reason);
                }
                return true;
            };

            await viewModel.ApplyPlaylistSummaryExternalPropertyInitializationAsync(
                [new PlaylistSummaryRow { TableRef = table }],
                new MainWindowViewModel.PlaylistSummaryExternalPropertyInitializationOptions
                {
                    Name = true
                });

            Assert.AreEqual("ExternalName", table.name);
            Assert.AreEqual("StableOutput", table.Output_dir);
            string afterText = string.Join(
                Environment.NewLine,
                Directory.GetFiles(Path.Combine(providerOutputBaseDir, "StableOutput"), "*.lr2folder", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => File.ReadAllText(path, Encoding.GetEncoding("shift_jis"))));
            StringAssert.Contains(afterText, "#CATEGORY ExternalName");
            Assert.IsFalse(afterText.Contains("#CATEGORY LocalName"));
            Assert.AreEqual(1, providerCallCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, "StableOutput")));
            CollectionAssert.DoesNotContain(queuedBmtReasons, "ReOutputCustomFolderAndCommitToDB");
            CollectionAssert.Contains(queuedBmtReasons, "playlist_summary_external_property_initialization");
        }
        finally
        {
            Settings.Default.EnablePlaylistUrlCompletion = previousEnablePlaylistUrlCompletion;
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.EnableBeatorajaBmtOutput = previousEnableBeatorajaBmtOutput;
            Settings.Default.BeatorajaBmtTablePath = previousBeatorajaBmtTablePath;
            Settings.Default.BeatorajaRootPath = previousBeatorajaRootPath;
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
    public void ReOutputCustomFolderAndCommitToDB_UsesInjectedCustomFolderOutputSettings()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        bool previousEnableUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = false;
            string settingsOutputBaseDir = Path.Combine(tempDirectory, "SettingsOutput");
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput");
            Settings.Default.LR2CustomFolderOutputBaseDir = settingsOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "SettingsRootOutput");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = true
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "InjectedOutputTable",
                symbol = "IOT",
                Output_dir = "InjectedOutputTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.OtherFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Injected Folder")
                ],
                Folder_order = ["Injected Folder"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsTrue(File.Exists(Path.Combine(providerOutputBaseDir, "InjectedOutputTable", "0001.lr2folder")));
            Assert.IsFalse(File.Exists(Path.Combine(settingsOutputBaseDir, "InjectedOutputTable", "0001.lr2folder")));
            Assert.IsTrue(Directory.GetFiles(Path.Combine(providerOutputBaseDir, "InjectedOutputTable"), "*.lr2folder")
                .Any(path => File.ReadAllText(path, Encoding.GetEncoding("shift_jis")).IndexOf("#TITLE UNSENT SONGS", StringComparison.Ordinal) >= 0));
            Assert.AreEqual(1, providerCallCount);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent = previousEnableUnsent;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void SyncCustomFolderOutputSearchRootsAfterSettingsChange_UsesInjectedOutputSettings()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = false;
            string settingsNormalOutputBaseDir = Path.Combine(tempDirectory, "SettingsNormalOutput");
            string settingsRootOutputBaseDir = Path.Combine(tempDirectory, "SettingsRootOutput");
            string providerNormalOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootOutput");
            Settings.Default.LR2CustomFolderOutputBaseDir = settingsNormalOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = settingsRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var table = new BMSTable
            {
                playlist_id = 7303,
                is_root_folder = true,
                Output_dir = "InjectedRootOutput"
            };
            var playlist = new BMSPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            Assert.IsTrue(playlist.SyncCustomFolderOutputSearchRootsAfterSettingsChange(settingsRootOutputBaseDir, config));

            string providerRootOutput = Path.Combine(providerRootOutputBaseDir, table.Output_dir);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), providerRootOutput);
            CollectionAssert.Contains(config.GetBMSSearchDirectories(), providerNormalOutputBaseDir);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), settingsRootOutputBaseDir);
            Assert.IsTrue(Directory.Exists(providerRootOutput));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_WritesLr2FolderUnderLongPath()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = BuildLongDirectoryPath(tempDirectory, "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7319,
                name = "LongFolderTable",
                symbol = "LFT",
                Output_dir = "LongFolderTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder B"]
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

            string outputPath = Path.Combine(outputBaseDir, "LongFolderTable", "0001.lr2folder");
            Assert.IsTrue(LongPathFileSystem.FileExists(outputPath));
            StringAssert.Contains(ReadShiftJisText(outputPath), "#TITLE Folder B");
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", outputPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            if (LongPathFileSystem.DirectoryExists(tempDirectory))
            {
                LongPathFileSystem.DeleteDirectory(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ReOutputCustomFolderAndCommitToDB_GeneratesAdditionalOutputBaseParentRow()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string defaultOutputBaseDir = Path.Combine(tempDirectory, "DefaultCustomFolder");
            string additionalOutputBaseDir = Path.Combine(tempDirectory, "Additional");
            Directory.CreateDirectory(additionalOutputBaseDir);
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = defaultOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([additionalOutputBaseDir]);
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7303,
                name = "AdditionalTable",
                symbol = "AT",
                Output_dir = "AdditionalTable",
                custom_folder_output_base_name = "Additional",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
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

            string outputDirectory = Path.Combine(additionalOutputBaseDir, "AdditionalTable");
            string outputPath = Path.Combine(outputDirectory, "0001.lr2folder");
            Assert.IsTrue(File.Exists(outputPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.folder> rows = verify.Table<LR2SongDB.folder>().ToList();
            string rowSummary = string.Join(" | ", rows.Select(row => $"{row.type}:{row.parent}:{row.path}").Take(20));
            LR2SongDB.folder outputBaseRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(additionalOutputBaseDir));
            Assert.IsNotNull(outputBaseRow, rowSummary);
            Assert.AreEqual(1, outputBaseRow.type);
            Assert.AreEqual("Additional", outputBaseRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputBaseRow.parent);
            LR2SongDB.folder tableRow = rows.SingleOrDefault(row => row.path == Lr2FolderPath.ToFolderPath(outputDirectory));
            Assert.IsNotNull(tableRow, rowSummary);
            Assert.AreEqual(1, tableRow.type);
            Assert.AreEqual("AdditionalTable", tableRow.title);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(additionalOutputBaseDir), tableRow.parent);
            LR2SongDB.folder folderFileRow = rows.SingleOrDefault(row => row.path == outputPath);
            Assert.IsNotNull(folderFileRow, rowSummary);
            Assert.AreEqual(2, folderFileRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(outputDirectory), folderFileRow.parent);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyPlaylistSummaryOutputBase_MigratesFromDefaultWhenPreviousAdditionalBaseIsMissing()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string defaultOutputBaseDir = Path.Combine(tempDirectory, "DefaultCustomFolder");
            string newAdditionalOutputBaseDir = Path.Combine(tempDirectory, "NewAdditional");
            string oldOutputDirectory = Path.Combine(defaultOutputBaseDir, "BulkOutput");
            string oldOutputPath = Path.Combine(oldOutputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(oldOutputDirectory);
            Directory.CreateDirectory(newAdditionalOutputBaseDir);
            File.WriteAllText(oldOutputPath, "#TITLE stale default", Encoding.GetEncoding("shift_jis"));
            var viewModel = new MainWindowViewModel();
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = defaultOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs =
                CustomFolderOutputBaseRegistry.SerializeBaseDirectories([newAdditionalOutputBaseDir]);
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7304,
                name = "BulkOutput",
                symbol = "BO",
                Output_dir = "BulkOutput",
                custom_folder_output_base_name = "MissingAdditional",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder D")
                ],
                Folder_order = ["Folder D"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldOutputPath, title = "stale default", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);

            viewModel.ApplyPlaylistSummaryOutputBase(
                [new PlaylistSummaryRow { TableRef = table }],
                "NewAdditional");

            string newOutputDirectory = Path.Combine(newAdditionalOutputBaseDir, "BulkOutput");
            string newOutputPath = Path.Combine(newOutputDirectory, "0001.lr2folder");
            Assert.AreEqual("NewAdditional", table.custom_folder_output_base_name);
            Assert.IsFalse(File.Exists(oldOutputPath));
            Assert.IsTrue(File.Exists(newOutputPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldOutputPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newOutputPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyPlaylistSummaryOutputBase_UsesInjectedCustomFolderSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerNormalOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalCustomFolder");
            string providerAdditionalOutputBaseDir = Path.Combine(tempDirectory, "ProviderAdditionalCustomFolder");
            string globalNormalOutputBaseDir = Path.Combine(tempDirectory, "GlobalNormalCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalNormalOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs =
                    CustomFolderOutputBaseRegistry.SerializeBaseDirectories([providerAdditionalOutputBaseDir])
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                providerCallCount++;
                return operationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7310,
                name = "OutputBaseProviderSettings",
                symbol = "OBPS",
                Output_dir = "OutputBaseProviderSettings",
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder B"]
            };
            string oldOutputDirectory = Path.Combine(providerNormalOutputBaseDir, table.Output_dir);
            string oldOutputPath = Path.Combine(oldOutputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(oldOutputDirectory);
            File.WriteAllText(oldOutputPath, "#TITLE stale normal output", Encoding.GetEncoding("shift_jis"));
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(),
                customFolderOutputSettingsProvider: getOperationSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);

            viewModel.ApplyPlaylistSummaryOutputBase(
                [new PlaylistSummaryRow { TableRef = table }],
                "ProviderAdditionalCustomFolder");

            string newOutputDirectory = Path.Combine(providerAdditionalOutputBaseDir, table.Output_dir);
            Assert.AreEqual(1, providerCallCount);
            Assert.AreEqual("ProviderAdditionalCustomFolder", table.custom_folder_output_base_name);
            Assert.IsFalse(File.Exists(oldOutputPath));
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDirectory, "0001.lr2folder")));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalNormalOutputBaseDir, table.Output_dir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyPlaylistSummaryCustomFolderOutputTypes_AppliesLastPlaySortWhenSchemaUnavailable()
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
            string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7305,
                name = "BulkOutputUnavailable",
                symbol = "BU",
                Output_dir = "BulkOutputUnavailable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                entries =
                [
                    CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder E")
                ],
                Folder_order = ["Folder E"]
            };
            var playlist = new BMSPlaylist(songDbPath, _lr2ScoreDB: scoreDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var viewModel = new MainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);
            viewModel.settingDialog.ApplyLr2PlayHistorySchemaCheckResult(new Lr2PlayHistorySchemaCheckResult
            {
                Status = Lr2PlayHistorySchemaStatus.NotInstalled
            });
            viewModel.ApplyPlaylistSummaryCustomFolderOutputTypes(
                [new PlaylistSummaryRow { TableRef = table }],
                new MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch
                {
                    LastPlaySortFolder = true
                });

            Assert.AreEqual(
                LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder,
                table.ignore_folder_output);
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
    public void ApplyPlaylistSummaryCustomFolderOutputTypes_UsesInjectedCustomFolderSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int viewModelProviderCallCount = 0;
            int playlistProviderCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                viewModelProviderCallCount++;
                return operationSettings;
            };
            Func<CustomFolderOutputSettingsSnapshot> getPlaylistSettings = () =>
            {
                playlistProviderCallCount++;
                return new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                };
            };
            string tempSongDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(tempSongDbPath);
            using (var db = new LR2SongDBExtended(tempSongDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7311,
                name = "OutputTypesProviderSettings",
                symbol = "OTPS",
                Output_dir = "OutputTypesProviderSettings",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder C")
                ],
                Folder_order = ["Folder C"]
            };
            var playlist = new BMSPlaylist(
                tempSongDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getPlaylistSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            using (var seed = new LR2SongDBExtended(tempSongDbPath))
            {
                seed.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
            }
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(),
                customFolderOutputSettingsProvider: getOperationSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);

            viewModel.ApplyPlaylistSummaryCustomFolderOutputTypes(
                [new PlaylistSummaryRow { TableRef = table }],
                new MainWindowViewModel.PlaylistSummaryCustomFolderOutputPatch
                {
                    UserFolder = false
                });

            Assert.AreEqual(1, viewModelProviderCallCount);
            Assert.AreEqual(0, playlistProviderCallCount);
            Assert.IsTrue((table.ignore_folder_output & LR2SongDBExtended.playlist.CustomFolderType.UserFolder)
                == LR2SongDBExtended.playlist.CustomFolderType.UserFolder);
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
            using var verify = new LR2SongDBExtended(tempSongDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single(row => row.playlist_id == table.playlist_id);
            Assert.IsTrue((persisted.ignore_folder_output & LR2SongDBExtended.playlist.CustomFolderType.UserFolder)
                == LR2SongDBExtended.playlist.CustomFolderType.UserFolder);
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
    public async Task PlaylistPropertyDialogApplyPostSaveUpdates_UsesOpenCustomFolderSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        MainWindowViewModel.PlaylistPropertyDialogViewModel? dialog = null;
        try
        {
            DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
            string initialNormalOutputBaseDir = Path.Combine(tempDirectory, "InitialNormalCustomFolder");
            string initialRootOutputBaseDir = Path.Combine(tempDirectory, "InitialRootCustomFolder");
            string changedNormalOutputBaseDir = Path.Combine(tempDirectory, "ChangedNormalCustomFolder");
            string changedRootOutputBaseDir = Path.Combine(tempDirectory, "ChangedRootCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "GlobalNormalCustomFolder");
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot initialSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = initialNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = initialRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            CustomFolderOutputSettingsSnapshot changedSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = changedNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = changedRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            CustomFolderOutputSettingsSnapshot currentSettings = initialSettings;
            int viewModelProviderCallCount = 0;
            int playlistProviderCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getViewModelSettings = () =>
            {
                viewModelProviderCallCount++;
                return currentSettings;
            };
            Func<CustomFolderOutputSettingsSnapshot> getPlaylistSettings = () =>
            {
                playlistProviderCallCount++;
                return new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                };
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7312,
                name = "DialogSnapshotSettings",
                symbol = "DSS",
                Output_dir = "DialogSnapshotSettings",
                entries =
                [
                    CreateEntry("dddddddddddddddddddddddddddddddd", "Folder D")
                ],
                Folder_order = ["Folder D"]
            };
            string oldOutputPath = Path.Combine(initialNormalOutputBaseDir, table.Output_dir, "0000.lr2folder");
            Directory.CreateDirectory(Path.GetDirectoryName(oldOutputPath));
            File.WriteAllText(oldOutputPath, "#TITLE stale normal output", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var playlist = new BMSPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getPlaylistSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                customFolderOutputSettingsProvider: getViewModelSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);
            typeof(MainWindowViewModel)
                .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, config);

            dialog = new MainWindowViewModel.PlaylistPropertyDialogViewModel(viewModel, table);
            currentSettings = changedSettings;
            dialog.is_root_folder = true;
            Assert.IsTrue(dialog.SaveProperties());
            MainWindowViewModel.PlaylistPropertyDialogViewModel savedDialog = dialog;
            savedDialog.Dispose();
            dialog = null;
            await savedDialog.ApplyPostSaveUpdatesAsync();

            string newOutputDirectory = Path.Combine(initialRootOutputBaseDir, table.Output_dir);
            Assert.AreEqual(1, viewModelProviderCallCount);
            Assert.AreEqual(0, playlistProviderCallCount);
            Assert.IsTrue(table.is_root_folder);
            Assert.IsFalse(File.Exists(oldOutputPath));
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDirectory, "0001.lr2folder")));
            Assert.IsFalse(Directory.Exists(Path.Combine(changedRootOutputBaseDir, table.Output_dir)));
            CollectionAssert.Contains(config.GetBMSSearchDirectoriesForChangeTracking(), newOutputDirectory);
            var reloadedConfig = new LR2Config(Path.Combine(tempDirectory, "LR2files", "Config", "config.xml"));
            CollectionAssert.Contains(reloadedConfig.GetBMSSearchDirectoriesForChangeTracking(), newOutputDirectory);
        }
        finally
        {
            dialog?.Dispose();
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            DispatcherHelper.UIDispatcher = previousDispatcher;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task SettingDialogPostSaveCustomFolderSync_UsesOneSavedSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        Dispatcher previousDispatcher = DispatcherHelper.UIDispatcher;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            DispatcherHelper.UIDispatcher = Dispatcher.CurrentDispatcher;
            string oldNormalOutputBaseDir = Path.Combine(tempDirectory, "OldNormalCustomFolder");
            string oldRootOutputBaseDir = Path.Combine(tempDirectory, "OldRootCustomFolder");
            string oldAdditionalOutputBaseDir = Path.Combine(tempDirectory, "OldAdditionalCustomFolder");
            string afterNormalOutputBaseDir = Path.Combine(tempDirectory, "AfterNormalCustomFolder");
            string afterRootOutputBaseDir = Path.Combine(tempDirectory, "AfterRootCustomFolder");
            string afterAdditionalOutputBaseDir = Path.Combine(tempDirectory, "AfterAdditionalCustomFolder", "OldAdditionalCustomFolder");
            string globalNormalOutputBaseDir = Path.Combine(tempDirectory, "GlobalNormalCustomFolder");
            string globalRootOutputBaseDir = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            string globalAdditionalOutputBaseDir = Path.Combine(tempDirectory, "GlobalAdditionalCustomFolder");
            string oldAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([oldAdditionalOutputBaseDir]);
            string afterAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([afterAdditionalOutputBaseDir]);
            string globalAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([globalAdditionalOutputBaseDir]);
            string oldAdditionalOutputBaseName = CustomFolderOutputBaseRegistry.GetDirectoryDisplayName(oldAdditionalOutputBaseDir);

            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            LR2Config config = CreateLr2Config(
                tempDirectory,
                oldNormalOutputBaseDir,
                oldAdditionalOutputBaseDir,
                oldRootOutputBaseDir);

            var normalTable = new BMSTable
            {
                playlist_id = 7321,
                name = "SettingDialogNormalSnapshot",
                symbol = "SDNS",
                Output_dir = "SettingDialogNormalSnapshot",
                entries = [CreateEntry("11111111111111111111111111111111", "Folder N")],
                Folder_order = ["Folder N"]
            };
            var additionalTable = new BMSTable
            {
                playlist_id = 7322,
                name = "SettingDialogAdditionalSnapshot",
                symbol = "SDAS",
                Output_dir = "SettingDialogAdditionalSnapshot",
                custom_folder_output_base_name = oldAdditionalOutputBaseName,
                entries = [CreateEntry("22222222222222222222222222222222", "Folder A")],
                Folder_order = ["Folder A"]
            };
            var rootTable = new BMSTable
            {
                playlist_id = 7323,
                name = "SettingDialogRootSnapshot",
                symbol = "SDRS",
                is_root_folder = true,
                Output_dir = "SettingDialogRootSnapshot",
                entries = [CreateEntry("33333333333333333333333333333333", "Folder R")],
                Folder_order = ["Folder R"]
            };
            string oldNormalOutputPath = Path.Combine(oldNormalOutputBaseDir, normalTable.Output_dir, "stale.lr2folder");
            string oldAdditionalOutputPath = Path.Combine(oldAdditionalOutputBaseDir, additionalTable.Output_dir, "stale.lr2folder");
            string oldRootOutputPath = Path.Combine(oldRootOutputBaseDir, rootTable.Output_dir, "stale.lr2folder");
            foreach (string outputPath in new[] { oldNormalOutputPath, oldAdditionalOutputPath, oldRootOutputPath })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                File.WriteAllText(outputPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            }

            CustomFolderOutputSettingsSnapshot afterSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = afterNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = afterRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = afterAdditionalOutputBaseDirs
            };
            int viewModelProviderCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getViewModelSettings = () =>
            {
                viewModelProviderCallCount++;
                return afterSettings;
            };
            int playlistProviderCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getPlaylistSettings = () =>
            {
                playlistProviderCallCount++;
                return new CustomFolderOutputSettingsSnapshot
                {
                    OperationModeLR2DB = false
                };
            };
            var playlist = new BMSPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getPlaylistSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { normalTable, additionalTable, rootTable }),
                    Dispatcher.CurrentDispatcher)
            };
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                customFolderOutputSettingsProvider: getViewModelSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, playlist);
            typeof(MainWindowViewModel)
                .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(viewModel, config);

            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalNormalOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = globalRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = globalAdditionalOutputBaseDirs;
            MainWindowViewModel.SettingDialogViewModel dialog = viewModel.settingDialog;
            typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetField("tempLR2CustomFolderOutputDir", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dialog, oldNormalOutputBaseDir);
            typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetField("tempLR2CustomFolderAsRootOutputDir", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dialog, oldRootOutputBaseDir);
            typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetField("tempLR2CustomFolderAdditionalOutputBaseDirs", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(dialog, oldAdditionalOutputBaseDirs);

            Type impactType = typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetNestedType("SettingsPostSaveImpact", BindingFlags.NonPublic)!;
            object impact = Enum.Parse(impactType, "CustomFolderSearchRootSync");
            MethodInfo postSaveMethod = typeof(MainWindowViewModel.SettingDialogViewModel)
                .GetMethod("necessaryStepsAfterSaved", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)postSaveMethod.Invoke(dialog, [impact]);

            Assert.AreEqual(1, viewModelProviderCallCount);
            Assert.AreEqual(0, playlistProviderCallCount);
            Assert.IsFalse(File.Exists(oldNormalOutputPath));
            Assert.IsFalse(File.Exists(oldAdditionalOutputPath));
            Assert.IsFalse(File.Exists(oldRootOutputPath));
            Assert.IsTrue(Directory.GetFiles(
                Path.Combine(afterNormalOutputBaseDir, normalTable.Output_dir),
                "*.lr2folder",
                SearchOption.AllDirectories).Length > 0);
            Assert.IsTrue(Directory.GetFiles(
                Path.Combine(afterAdditionalOutputBaseDir, additionalTable.Output_dir),
                "*.lr2folder",
                SearchOption.AllDirectories).Length > 0);
            string afterRootOutputDirectory = Path.Combine(afterRootOutputBaseDir, rootTable.Output_dir);
            Assert.IsTrue(Directory.GetFiles(afterRootOutputDirectory, "*.lr2folder", SearchOption.AllDirectories).Length > 0);
            List<string> searchRoots = config.GetBMSSearchDirectoriesForChangeTracking();
            CollectionAssert.Contains(searchRoots, afterNormalOutputBaseDir);
            CollectionAssert.Contains(searchRoots, afterAdditionalOutputBaseDir);
            CollectionAssert.Contains(searchRoots, afterRootOutputDirectory);
            CollectionAssert.DoesNotContain(searchRoots, globalNormalOutputBaseDir);
            CollectionAssert.DoesNotContain(searchRoots, globalRootOutputBaseDir);
            CollectionAssert.DoesNotContain(searchRoots, globalAdditionalOutputBaseDir);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            DispatcherHelper.UIDispatcher = previousDispatcher;
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
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
            string assistText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "2 ASSIST", "0000.lr2folder"));
            StringAssert.Contains(assistText, "score.clear = 2");
            StringAssert.Contains(assistText, "NOT (score.clear = 2");
            StringAssert.Contains(assistText, "(IFNULL(score.op_history, 0) & 8) != 0");
            Assert.IsFalse(assistText.Contains("16777216"));
            Assert.IsFalse(assistText.Contains("score.rank"));
            string easyText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "3 EASY", "0000.lr2folder"));
            StringAssert.Contains(easyText, "score.clear = 2");
            StringAssert.Contains(easyText, "(IFNULL(score.op_history, 0) & 8) != 0");
            Assert.IsFalse(easyText.Contains("16777216"));
            Assert.IsFalse(easyText.Contains("score.rank"));
            AssertClearFolderCommandMatchesAssistAndEasyRows(songDbPath, assistText, easyText);
            string fcText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "6 FC", "0000.lr2folder"));
            StringAssert.Contains(fcText, "(IFNULL(score.op_history, 0) & 16) = 0");
            string paText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "7 P.A", "0000.lr2folder"));
            StringAssert.Contains(paText, "(IFNULL(score.op_history, 0) & 16) != 0");
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
    public void ReOutputCustomFolderAndCommitToDB_WritesLastPlaySortFolderWhenSchemaInstalled()
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
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
            var table = new BMSTable
            {
                playlist_id = 7306,
                name = "LastPlay",
                symbol = "LP",
                Output_dir = "LastPlay",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A"),
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder B")
                ],
                Folder_order = ["Folder A", "Folder B"]
            };
            var playlist = new BMSPlaylist(songDbPath, _lr2ScoreDB: scoreDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LastPlay");
            string lastPlayText = ReadShiftJisText(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder"));
            StringAssert.Contains(lastPlayText, "#TITLE LastPlay ALL");
            StringAssert.Contains(lastPlayText, "ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC");
            StringAssert.Contains(lastPlayText, "#INFORMATION_A Sort: LAST PLAY DESC");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));

            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder row = verify.Table<LR2SongDB.folder>().Single(item => item.path == Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder"));
            StringAssert.Contains(row.command, "bms_lr2_last_play");
            StringAssert.Contains(row.command, "ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC");
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
    public void ReOutputCustomFoldersAndCommitHeadersToDB_WritesLastPlaySortWhenSchemaNotInstalled()
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
            string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7307,
                name = "MissingLastPlaySchema",
                symbol = "ML",
                Output_dir = "MissingLastPlaySchema",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, _lr2ScoreDB: scoreDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_last_play_schema_missing");

            string outputDir = Path.Combine(outputBaseDir, "MissingLastPlaySchema");
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
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
    public void ReOutputCustomFolderAndCommitToDB_PrunesLastPlaySortWhenOutputBitTurnsOff()
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
            string scoreDbPath = CreateInstalledPlayHistoryScoreDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2SongDBExtended.playlist.CustomFolderType enabledTypes =
                LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
            var table = new BMSTable
            {
                playlist_id = 7308,
                name = "LastPlayPrune",
                symbol = "LPP",
                Output_dir = "LastPlayPrune",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders & ~enabledTypes,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, _lr2ScoreDB: scoreDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFolderAndCommitToDB(table);

            string outputDir = Path.Combine(outputBaseDir, "LastPlayPrune");
            string lastPlayDir = Path.Combine(outputDir, "LAST PLAY SORT");
            string generatedPath = Path.Combine(lastPlayDir, "0000.lr2folder");
            string stalePath = Path.Combine(lastPlayDir, "stale.lr2folder");
            Assert.IsTrue(File.Exists(generatedPath));
            File.WriteAllText(stalePath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.InsertOrReplace(new LR2SongDB.folder { path = stalePath, title = "stale", type = 2, command = "song.hash IS NOT NULL ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC, (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC" }, typeof(LR2SongDB.folder));
            }

            table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
            playlist.ReOutputCustomFolderAndCommitToDB(table);

            Assert.IsFalse(File.Exists(generatedPath));
            Assert.IsFalse(File.Exists(stalePath));
            Assert.IsFalse(Directory.Exists(lastPlayDir));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path LIKE ?;", lastPlayDir + "%"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
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
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
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
    public void ReOutputCustomFolderAndCommitToDB_AllSongsScopeDoesNotRequireUserFolder()
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
                playlist_id = 7311,
                name = "AllScopeOnly",
                symbol = "ASO",
                Output_dir = "AllScopeOnly",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
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

            string outputDir = Path.Combine(outputBaseDir, "AllScopeOnly");
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder")), "#TITLE AllScopeOnly ALL");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "0001.lr2folder")));
            StringAssert.Contains(ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder")), "#TITLE AllScopeOnly ALL NO PLAY");
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0001.lr2folder")));
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
    public void ReOutputCustomFolderAndCommitToDB_UserFolderScopeDoesNotWriteAllSongs()
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
                playlist_id = 7312,
                name = "FolderScopeOnly",
                symbol = "FSO",
                Output_dir = "FolderScopeOnly",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.ClearFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Folder A")
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

            string outputDir = Path.Combine(outputBaseDir, "FolderScopeOnly");
            string folderText = ReadShiftJisText(Path.Combine(outputDir, "0000.lr2folder"));
            StringAssert.Contains(folderText, "#TITLE Folder A");
            Assert.IsFalse(folderText.Contains("#TITLE FolderScopeOnly ALL"));
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "0001.lr2folder")));
            string clearText = ReadShiftJisText(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder"));
            StringAssert.Contains(clearText, "#TITLE Folder A NO PLAY");
            Assert.IsFalse(clearText.Contains("#TITLE FolderScopeOnly ALL NO PLAY"));
            Assert.IsFalse(File.Exists(Path.Combine(outputDir, "CLEAR FOLDER", "0 NO PLAY", "0001.lr2folder")));
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
    public void ReOutputCustomFolderAndCommitToDB_DoesNotExpandOldAllFoldersMaskToNewTypes()
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
                ignore_folder_output = (LR2SongDBExtended.playlist.CustomFolderType)0x7F,
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
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BPM SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "BP SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "PLAY COUNT SORT", "0000.lr2folder")));
            Assert.IsTrue(File.Exists(Path.Combine(outputDir, "LAST PLAY SORT", "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE command LIKE '%bms_lr2_last_play%';"));
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
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
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
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
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(
                songDbPath,
                () => CreateLr2Config(lr2RootPath, bmsRoot),
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
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
            Assert.AreEqual(1, providerCallCount);
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
    public void MissingCustomFolderOutputRepair_VerifiesRootOutputDirectoryRowAfterJukeboxRepair()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string rootOutputBaseDir = Path.Combine(tempDirectory, "RootOutput");
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7618,
                name = "RootStatusCurrent",
                symbol = "RSC",
                Output_dir = "RootStatusCurrent",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            var playlist = new BMSPlaylist(songDbPath, () => CreateLr2Config(lr2RootPath, Path.Combine(tempDirectory, "BMS")))
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_root_status_current");
            string outputDirectory = Path.Combine(rootOutputBaseDir, "RootStatusCurrent");
            string outputFile = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime outputFileTimestamp = File.GetLastWriteTimeUtc(outputFile);
            string outputDirectoryRowPath = Lr2FolderPath.ToFolderPath(outputDirectory);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("DELETE FROM folder WHERE path = ?;", outputDirectoryRowPath);
            }
            var operations = new List<string>();
            playlist.Lr2FolderSyncMutationGuard = operations.Add;

            int skippedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(
                playlist,
                "test_root_directory_row_fast_path",
                verifyRootOutputDirectoryRows: false);

            Assert.AreEqual(0, skippedCount);
            CollectionAssert.AreEqual(Array.Empty<string>(), operations);
            using (var verifySkipped = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual(0L, verifySkipped.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", outputDirectoryRowPath));
            }

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(
                playlist,
                "test_root_directory_row_repair",
                verifyRootOutputDirectoryRows: true);

            Assert.AreEqual(1, repairedCount);
            Assert.AreEqual(outputFileTimestamp, File.GetLastWriteTimeUtc(outputFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder outputDirectoryRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputDirectoryRowPath);
            Assert.AreEqual(1, outputDirectoryRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputDirectoryRow.parent);
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
    public void ReloadTables_RepairsRootOutputDirectoryRowWhenJukeboxRootWasMissing()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string bmsRoot = Path.Combine(tempDirectory, "BMS");
            string settingsRootOutputBaseDir = Path.Combine(tempDirectory, "SettingsRootOutput");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootOutput");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = settingsRootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7619,
                name = "ReloadRootStatusCurrent",
                symbol = "RRSC",
                Output_dir = "ReloadRootStatusCurrent",
                is_root_folder = true,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                foreach (BMSTableEntry entry in table.entries)
                {
                    entry.playlist_id = table.playlist_id;
                    db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
                }
            }
            LR2Config config = CreateLr2Config(lr2RootPath, bmsRoot);
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalOutput"),
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            var playlist = new BMSPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => outputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            playlist.ReOutputCustomFoldersAndCommitHeadersToDB([table], "test_seed_reload_root_status_current");
            string outputDirectory = Path.Combine(providerRootOutputBaseDir, "ReloadRootStatusCurrent");
            string outputFile = Path.Combine(outputDirectory, "0001.lr2folder");
            DateTime outputFileTimestamp = File.GetLastWriteTimeUtc(outputFile);
            string outputDirectoryRowPath = Lr2FolderPath.ToFolderPath(outputDirectory);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.Execute("DELETE FROM folder WHERE path = ?;", outputDirectoryRowPath);
            }
            config.SetBMSSearchDirectories([bmsRoot]);
            config.Save();
            var scheduledTasks = new List<Task>();
            playlist.StartupBackgroundTaskScheduler = delegate (string name, string reason, string dependency, Func<Task> work)
            {
                scheduledTasks.Add(work());
                return true;
            };

            playlist.ReloadTables(queueBeatorajaBmtExportAfterHydration: false);
            Task.WaitAll([.. scheduledTasks]);

            CollectionAssert.Contains(config.GetBMSSearchDirectories(), outputDirectory);
            CollectionAssert.DoesNotContain(config.GetBMSSearchDirectories(), settingsRootOutputBaseDir);
            Assert.AreEqual(outputFileTimestamp, File.GetLastWriteTimeUtc(outputFile));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.folder outputDirectoryRow = verify.Table<LR2SongDB.folder>().Single(row => row.path == outputDirectoryRowPath);
            Assert.AreEqual(1, outputDirectoryRow.type);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.RootParentHash, outputDirectoryRow.parent);
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder;
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
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            CustomFolderOutputSettingsSnapshot repairSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getRepairSettings = () =>
            {
                providerCallCount++;
                return repairSettings;
            };
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
            var playlist = new BMSPlaylist(
                songDbPath,
                () => CreateLr2Config(lr2RootPath, bmsRoot),
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getRepairSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { firstTable, secondTable }),
                    Dispatcher.CurrentDispatcher)
            };
            List<string> operations = [];
            playlist.Lr2FolderSyncMutationGuard = operations.Add;

            int repairedCount = InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test");

            Assert.AreEqual(2, repairedCount);
            Assert.AreEqual(1, providerCallCount);
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
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
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
            Directory.SetLastWriteTimeUtc(noPlayDir, noPlayTimestamp.AddMinutes(1));

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
                | LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
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
            Directory.SetLastWriteTimeUtc(clearDir, noPlayTimestamp.AddMinutes(1));

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
            string providerOutputBaseDir = Path.Combine(bmsRoot, "#ProviderOutput");
            string globalOutputBaseDir = Path.Combine(bmsRoot, "#GlobalOutput");
            Directory.CreateDirectory(bmsRoot);
            Settings.Default.LR2RootPath = lr2RootPath;
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = lr2RootPath,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
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
            var playlist = new BMSPlaylist(
                songDbPath,
                () => CreateLr2Config(lr2RootPath, bmsRoot),
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(1, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_seed"));
            string outputFile = Path.Combine(providerOutputBaseDir, "LockedCurrent", "0000.lr2folder");
            Assert.IsTrue(File.Exists(outputFile));

            providerCallCount = 0;
            using var locked = new FileStream(outputFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Lr2SongDbSyncPreparedDataSurface surface =
                playlist.ReOutputAllCustomFoldersForLr2SongDbSync("test_locked_current");

            Assert.AreEqual(1, providerCallCount);
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, "LockedCurrent")));
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
    public void ReOutputAllCustomFoldersForLr2SongDbSync_EmptyTablesUsesOneSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            Settings.Default.OperationModeLR2DB = false;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput"),
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(),
                    Dispatcher.CurrentDispatcher)
            };

            Lr2SongDbSyncPreparedDataSurface surface =
                playlist.ReOutputAllCustomFoldersForLr2SongDbSync("test_empty_snapshot");

            Assert.AreEqual(1, providerCallCount);
            Assert.IsNotNull(surface);
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
            Directory.SetLastWriteTimeUtc(parentOutputDir, staleTimestamp.AddMinutes(1));
            Directory.SetLastWriteTimeUtc(childOutputDir, staleTimestamp.AddMinutes(1));

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
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
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
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder,
                entries = [entry]
            };
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            playlist.ReOutputCustomFolderAndCommitToDB(table);
            providerCallCount = 0;
            entry.level = 12;

            playlist.CommitBMSTableEntry(entry);

            using var verify = new LR2SongDBExtended(songDbPath);
            List<LR2SongDB.folder> folders = verify.Table<LR2SongDB.folder>().ToList();
            Assert.AreEqual(1, providerCallCount);
            Assert.IsTrue(Directory.Exists(Path.Combine(providerOutputBaseDir, table.Output_dir)));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
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
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            CustomFolderOutputSettingsSnapshot migrationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = Settings.Default.LR2RootPath,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getMigrationSettings = () =>
            {
                providerCallCount++;
                return migrationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7304,
                name = "ToggleTable",
                symbol = "TT",
                Output_dir = "ToggleTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getMigrationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            table.is_root_folder = true;
            string newOutputDir = Path.Combine(rootOutputBaseDir, "ToggleTable");

            playlist.MigrateCustomFolderOutputDirectoryAndCommitToDB(
                table,
                oldOutputDir,
                newOutputDir,
                outputBaseDirBefore: outputBaseDir);

            Assert.AreEqual(1, providerCallCount);
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
    public void MigrateCustomFolderOutputDirectoryAndCommitToDB_PrunesOldRootRelativeRowsWhenMovingToNormalOutput()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string lr2RootPath = Path.Combine(tempDirectory, "LR2");
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string rootOutputBaseDir = Path.Combine(lr2RootPath, "LR2files", "CustomFolder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = rootOutputBaseDir;
            Settings.Default.LR2RootPath = lr2RootPath;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7314,
                name = "RootToNormal",
                symbol = "RTN",
                Output_dir = "RootToNormal",
                is_root_folder = false,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(rootOutputBaseDir, "RootToNormal");
            string oldPhysicalPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldRelativeOutputDir = Path.Combine("LR2files", "CustomFolder", "RootToNormal");
            string oldRelativePath = Path.Combine(oldRelativeOutputDir, "0000.lr2folder");
            string oldRelativeParentPath = oldRelativeOutputDir + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPhysicalPath, "#TITLE stale root", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldRelativePath, title = "stale root", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldRelativeParentPath, title = "stale root parent", type = 1 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            string newOutputDir = Path.Combine(outputBaseDir, "RootToNormal");

            playlist.MigrateCustomFolderOutputDirectoryAndCommitToDB(
                table,
                oldOutputDir,
                newOutputDir,
                queueBeatorajaBmtExport: false,
                wasRootFolderBefore: true,
                rootOutputBaseDirBefore: rootOutputBaseDir);

            string newPath = Path.Combine(newOutputDir, "0000.lr2folder");
            Assert.IsFalse(Directory.Exists(oldOutputDir));
            Assert.IsTrue(File.Exists(newPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldRelativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldRelativeParentPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newPath));
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
    public void MigrateCustomFolderOutputDirectoryAndCommitToDB_DeletesOldOutputDirectoryRecursively()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldOutputBaseDir = Path.Combine(tempDirectory, "OldNormal");
            string parentOldOutputDir = Path.Combine(oldOutputBaseDir, "Parent");
            string parentNewOutputDir = Path.Combine(tempDirectory, "NewNormal", "Parent");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = parentOldOutputDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var parentTable = new BMSTable
            {
                playlist_id = 7313,
                name = "Parent",
                symbol = "P",
                Output_dir = "Parent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            string parentOldPath = Path.Combine(parentOldOutputDir, "0000.lr2folder");
            string nestedOldPath = Path.Combine(parentOldOutputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder");
            string unmanagedOldPath = Path.Combine(parentOldOutputDir, "manual-note.txt");
            Directory.CreateDirectory(parentOldOutputDir);
            Directory.CreateDirectory(Path.GetDirectoryName(nestedOldPath));
            File.WriteAllText(parentOldPath, "#TITLE stale parent", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(nestedOldPath, "#TITLE nested stale", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(unmanagedOldPath, "old custom folder note", Encoding.UTF8);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = parentOldPath, title = "stale parent", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = nestedOldPath, title = "nested stale", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { parentTable }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.MigrateCustomFolderOutputDirectoryAndCommitToDB(
                parentTable,
                parentOldOutputDir,
                parentNewOutputDir,
                queueBeatorajaBmtExport: false,
                outputBaseDirBefore: oldOutputBaseDir);

            string parentNewPath = Path.Combine(parentNewOutputDir, "0000.lr2folder");
            Assert.IsFalse(Directory.Exists(parentOldOutputDir));
            Assert.IsTrue(File.Exists(parentNewPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentOldPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", nestedOldPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentNewPath));
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
    public void MigrateCustomFolderOutputDirectoryAndCommitToDB_PreservesNestedManagedOutputDirectory()
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
            var parentTable = new BMSTable
            {
                playlist_id = 7314,
                name = "MigrationParent",
                symbol = "MP",
                Output_dir = "MigrationParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7315,
                name = "MigrationChild",
                symbol = "MC",
                Output_dir = Path.Combine("MigrationParent", "MigrationChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")],
                Folder_order = ["Child Folder"]
            };
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { parentTable, childTable }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(2, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_migration_nested_seed"));
            string oldParentDirectory = Path.Combine(outputBaseDir, "MigrationParent");
            string childDirectory = Path.Combine(oldParentDirectory, "MigrationChild");
            string childPath = Path.Combine(childDirectory, "0000.lr2folder");
            Assert.IsTrue(File.Exists(childPath));

            parentTable.Output_dir = "MigrationParentMoved";
            string newParentDirectory = Path.Combine(outputBaseDir, "MigrationParentMoved");
            playlist.MigrateCustomFolderOutputDirectoryAndCommitToDB(
                parentTable,
                oldParentDirectory,
                newParentDirectory,
                queueBeatorajaBmtExport: false,
                outputBaseDirBefore: outputBaseDir);

            string newParentPath = Path.Combine(newParentDirectory, "0000.lr2folder");
            Assert.IsTrue(File.Exists(newParentPath));
            Assert.IsTrue(File.Exists(childPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(oldParentDirectory)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newParentPath));
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
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_DoesNotRewritePlaylistEntries()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldOutputBaseDir = Path.Combine(tempDirectory, "OldCustomFolder");
            string newOutputBaseDir = Path.Combine(tempDirectory, "NewCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = newOutputBaseDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            CustomFolderOutputSettingsSnapshot migrationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = Settings.Default.LR2RootPath,
                LR2CustomFolderOutputBaseDir = newOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]",
                EnableDownloadLr2IrScoreAndDetectUnsent = Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getMigrationSettings = () =>
            {
                providerCallCount++;
                return migrationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7310,
                name = "BulkMoveTable",
                symbol = "BMT",
                Output_dir = "BulkMoveTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(oldOutputBaseDir, "BulkMoveTable");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldParentPath = Lr2FolderPath.ToFolderPath(oldOutputDir);
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            BMSTableEntry sentinelEntry = CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Sentinel Folder");
            sentinelEntry.playlist_id = table.playlist_id;
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(sentinelEntry, typeof(LR2SongDBExtended.playlist_entry));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldParentPath, title = "stale parent", type = 1 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getMigrationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [table],
                new Dictionary<BMSTable, string> { [table] = oldOutputDir },
                "test_bulk_output_base_move",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string> { [table] = oldOutputBaseDir });

            Assert.AreEqual(1, providerCallCount);
            string newOutputDir = Path.Combine(newOutputBaseDir, "BulkMoveTable");
            string newPath = Path.Combine(newOutputDir, "0000.lr2folder");
            Assert.IsFalse(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(newPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldParentPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newPath));
            Assert.AreEqual(
                1L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = ? AND md5 = ?;",
                    table.playlist_id,
                    sentinelEntry.md5));
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
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_DeletesOldOutputDirectoriesRecursively()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousLr2RootPath = Settings.Default.LR2RootPath;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldOutputBaseDir = Path.Combine(tempDirectory, "OldNormal");
            string newOutputBaseDir = Path.Combine(tempDirectory, "NewNormal");
            string parentOldOutputDir = Path.Combine(oldOutputBaseDir, "Parent");
            string childOldOutputDir = Path.Combine(oldOutputBaseDir, "Child");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = newOutputBaseDir;
            Settings.Default.LR2RootPath = Path.Combine(tempDirectory, "LR2");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var parentTable = new BMSTable
            {
                playlist_id = 7311,
                name = "Parent",
                symbol = "P",
                Output_dir = "Parent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")
                ],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7312,
                name = "Child",
                symbol = "C",
                Output_dir = "Child",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")
                ],
                Folder_order = ["Child Folder"]
            };
            string parentOldPath = Path.Combine(parentOldOutputDir, "0000.lr2folder");
            string parentOldNestedPath = Path.Combine(parentOldOutputDir, "DJ LEVEL", "AAA", "0000.lr2folder");
            string parentOldUnmanagedPath = Path.Combine(parentOldOutputDir, "manual-note.txt");
            string childOldPath = Path.Combine(childOldOutputDir, "0000.lr2folder");
            Directory.CreateDirectory(parentOldOutputDir);
            Directory.CreateDirectory(Path.GetDirectoryName(parentOldNestedPath));
            Directory.CreateDirectory(childOldOutputDir);
            File.WriteAllText(parentOldPath, "#TITLE stale parent", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(parentOldNestedPath, "#TITLE nested stale parent", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(parentOldUnmanagedPath, "old custom folder note", Encoding.UTF8);
            File.WriteAllText(childOldPath, "#TITLE stale child", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = parentOldPath, title = "stale parent", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = parentOldNestedPath, title = "nested stale parent", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = childOldPath, title = "stale child", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { parentTable, childTable }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [parentTable, childTable],
                new Dictionary<BMSTable, string>
                {
                    [parentTable] = parentOldOutputDir,
                    [childTable] = childOldOutputDir
                },
                "test_bulk_output_base_nested_move",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string>
                {
                    [parentTable] = oldOutputBaseDir,
                    [childTable] = oldOutputBaseDir
                });

            string parentNewPath = Path.Combine(newOutputBaseDir, "Parent", "0000.lr2folder");
            string childNewPath = Path.Combine(newOutputBaseDir, "Child", "0000.lr2folder");
            Assert.IsFalse(Directory.Exists(parentOldOutputDir));
            Assert.IsFalse(Directory.Exists(childOldOutputDir));
            Assert.IsTrue(File.Exists(parentNewPath));
            Assert.IsTrue(File.Exists(childNewPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentOldPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentOldNestedPath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childOldPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", parentNewPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childNewPath));
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
    public void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB_PreservesNestedManagedOutputDirectory()
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
            var parentTable = new BMSTable
            {
                playlist_id = 7316,
                name = "BulkMigrationParent",
                symbol = "BMP",
                Output_dir = "BulkMigrationParent",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Parent Folder")],
                Folder_order = ["Parent Folder"]
            };
            var childTable = new BMSTable
            {
                playlist_id = 7317,
                name = "BulkMigrationChild",
                symbol = "BMC",
                Output_dir = Path.Combine("BulkMigrationParent", "BulkMigrationChild"),
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Child Folder")],
                Folder_order = ["Child Folder"]
            };
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { parentTable, childTable }),
                    Dispatcher.CurrentDispatcher)
            };
            Assert.AreEqual(2, InvokeRepairMissingCustomFolderOutputsAfterHydration(playlist, "test_bulk_migration_nested_seed"));
            string oldParentDirectory = Path.Combine(outputBaseDir, "BulkMigrationParent");
            string childDirectory = Path.Combine(oldParentDirectory, "BulkMigrationChild");
            string childPath = Path.Combine(childDirectory, "0000.lr2folder");
            Assert.IsTrue(File.Exists(childPath));

            parentTable.Output_dir = "BulkMigrationParentMoved";
            string newParentDirectory = Path.Combine(outputBaseDir, "BulkMigrationParentMoved");
            playlist.MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                [parentTable],
                new Dictionary<BMSTable, string> { [parentTable] = oldParentDirectory },
                "test_bulk_migration_nested_move",
                outputBaseDirPathBeforeByTable: new Dictionary<BMSTable, string> { [parentTable] = outputBaseDir });

            string newParentPath = Path.Combine(newParentDirectory, "0000.lr2folder");
            Assert.IsTrue(File.Exists(newParentPath));
            Assert.IsTrue(File.Exists(childPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", childPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", Lr2FolderPath.ToFolderPath(oldParentDirectory)));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", newParentPath));
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
    public void RemoveCustomFolder_UsesOneSettingsSnapshotForTheWholeOperation()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                providerCallCount++;
                return operationSettings;
            };
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7407,
                name = "ProviderSettings",
                symbol = "PS",
                Output_dir = "ProviderSettings"
            };
            string targetDir = Path.Combine(providerOutputBaseDir, table.Output_dir);
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE provider target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "provider target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.RemoveCustomFolder(table);

            Assert.AreEqual(1, providerCallCount);
            Assert.IsFalse(Directory.Exists(targetDir));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
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
    public void MainWindowRemoveBMSTable_UsesInjectedCustomFolderSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootCustomFolder"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                providerCallCount++;
                return operationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7408,
                name = "ViewModelProviderSettings",
                symbol = "VMPS",
                Output_dir = "ViewModelProviderSettings"
            };
            string targetDir = Path.Combine(providerOutputBaseDir, table.Output_dir);
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE provider target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "provider target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var library = new BMSLibrary(songDbPath);
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(),
                customFolderOutputSettingsProvider: getOperationSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);
            typeof(MainWindowViewModel)
                .GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, library);

            viewModel.RemoveBMSTable(table);

            Assert.AreEqual(1, providerCallCount);
            Assert.IsFalse(playlist.ContainsBMSTable(table));
            Assert.IsFalse(Directory.Exists(targetDir));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
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
    public void MainWindowRemoveBMSTable_RemovesInjectedRootOutputSearchDirectory()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootCustomFolder");
            string globalRootOutputBaseDir = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = globalRootOutputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = Path.Combine(tempDirectory, "ProviderCustomFolder"),
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                providerCallCount++;
                return operationSettings;
            };
            var table = new BMSTable
            {
                playlist_id = 7409,
                name = "ViewModelRootProviderSettings",
                symbol = "VMRPS",
                Output_dir = "ViewModelRootProviderSettings",
                is_root_folder = true
            };
            string outputDirectory = Path.Combine(providerRootOutputBaseDir, table.Output_dir);
            Directory.CreateDirectory(outputDirectory);
            config.SetBMSSearchDirectories([outputDirectory]);
            config.Save();
            var playlist = new BMSPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var library = new BMSLibrary(songDbPath);
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(),
                customFolderOutputSettingsProvider: getOperationSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);
            typeof(MainWindowViewModel)
                .GetField("files", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, library);
            typeof(MainWindowViewModel)
                .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, config);

            viewModel.RemoveBMSTable(table);

            Assert.AreEqual(1, providerCallCount);
            Assert.IsFalse(Directory.Exists(outputDirectory));
            List<string> trackedSearchDirectories = config.GetBMSSearchDirectoriesForChangeTracking();
            CollectionAssert.DoesNotContain(trackedSearchDirectories, outputDirectory);
            Assert.IsFalse(trackedSearchDirectories.Any(path => path.StartsWith(globalRootOutputBaseDir, StringComparison.OrdinalIgnoreCase)));
            string configPath = Path.Combine(tempDirectory, "LR2files", "Config", "config.xml");
            var reloadedConfig = new LR2Config(configPath);
            CollectionAssert.DoesNotContain(reloadedConfig.GetBMSSearchDirectoriesForChangeTracking(), outputDirectory);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ApplyPlaylistSummaryRootFolder_UsesInjectedCustomFolderSettingsSnapshot()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousRootOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerNormalOutputBaseDir = Path.Combine(tempDirectory, "ProviderNormalCustomFolder");
            string providerRootOutputBaseDir = Path.Combine(tempDirectory, "ProviderRootCustomFolder");
            string globalNormalOutputBaseDir = Path.Combine(tempDirectory, "GlobalNormalCustomFolder");
            string globalRootOutputBaseDir = Path.Combine(tempDirectory, "GlobalRootCustomFolder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalNormalOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = globalRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot operationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2RootPath = tempDirectory,
                LR2CustomFolderOutputBaseDir = providerNormalOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = providerRootOutputBaseDir,
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOperationSettings = () =>
            {
                providerCallCount++;
                return operationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
            }
            var table = new BMSTable
            {
                playlist_id = 7410,
                name = "RootSwitchProviderSettings",
                symbol = "RSPS",
                Output_dir = "RootSwitchProviderSettings",
                entries =
                [
                    CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDirectory = Path.Combine(providerNormalOutputBaseDir, table.Output_dir);
            string oldOutputPath = Path.Combine(oldOutputDirectory, "0000.lr2folder");
            Directory.CreateDirectory(oldOutputDirectory);
            File.WriteAllText(oldOutputPath, "#TITLE stale normal output", Encoding.GetEncoding("shift_jis"));
            LR2Config config = CreateLr2Config(tempDirectory, Path.Combine(tempDirectory, "ManualBmsRoot"));
            var playlist = new BMSPlaylist(
                songDbPath,
                () => config,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOperationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };
            var viewModel = new ApplicationComposition(
                () => new BmsLibraryOptionsSnapshot(),
                beatorajaBmtOptionsProvider: () => new BeatorajaBmtOptionsSnapshot(),
                customFolderOutputSettingsProvider: getOperationSettings).CreateMainWindowViewModel();
            typeof(MainWindowViewModel)
                .GetField("tables", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, playlist);
            typeof(MainWindowViewModel)
                .GetField("lr2config", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(viewModel, config);

            viewModel.ApplyPlaylistSummaryRootFolder(
                [new PlaylistSummaryRow { TableRef = table }],
                isRootFolder: true);

            string newOutputDirectory = Path.Combine(providerRootOutputBaseDir, table.Output_dir);
            Assert.AreEqual(1, providerCallCount);
            Assert.IsTrue(table.is_root_folder);
            Assert.IsFalse(File.Exists(oldOutputPath));
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDirectory, "0001.lr2folder")));
            List<string> trackedSearchDirectories = config.GetBMSSearchDirectoriesForChangeTracking();
            CollectionAssert.Contains(trackedSearchDirectories, newOutputDirectory);
            Assert.IsFalse(trackedSearchDirectories.Any(path => path.StartsWith(globalRootOutputBaseDir, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalRootOutputBaseDir, table.Output_dir)));
            var reloadedConfig = new LR2Config(Path.Combine(tempDirectory, "LR2files", "Config", "config.xml"));
            CollectionAssert.Contains(reloadedConfig.GetBMSSearchDirectoriesForChangeTracking(), newOutputDirectory);
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderOutputBaseDirRootType = previousRootOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void RemoveCustomFolder_PrunesRootRelativeRows()
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
            var table = new BMSTable
            {
                playlist_id = 7402,
                name = "RootDelete",
                symbol = "RD",
                Output_dir = "RootDelete",
                is_root_folder = true
            };
            string targetDir = Path.Combine(rootOutputBaseDir, "RootDelete");
            string targetPhysicalPath = Path.Combine(targetDir, "0000.lr2folder");
            string targetRelativeDir = Path.Combine("LR2files", "CustomFolder", "RootDelete");
            string targetRelativePath = Path.Combine(targetRelativeDir, "0000.lr2folder");
            string targetRelativeParentPath = targetRelativeDir + Path.DirectorySeparatorChar;
            string siblingRelativePath = Path.Combine("LR2files", "CustomFolder", "RootDeleteSibling", "0000.lr2folder");
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPhysicalPath, "#TITLE target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetRelativePath, title = "target", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = targetRelativeParentPath, title = "target parent", type = 1 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = siblingRelativePath, title = "sibling", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsFalse(Directory.Exists(targetDir));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetRelativePath));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetRelativeParentPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", siblingRelativePath));
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
    public void RemoveCustomFolder_DoesNotDeletePathEscapingOutputBase()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string siblingDir = Path.Combine(tempDirectory, "Sibling");
            string siblingPath = Path.Combine(siblingDir, "0000.lr2folder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7403,
                name = "UnsafeDelete",
                symbol = "UD",
                Output_dir = ".." + Path.DirectorySeparatorChar + "Sibling"
            };
            Directory.CreateDirectory(siblingDir);
            File.WriteAllText(siblingPath, "#TITLE sibling", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = siblingPath, title = "sibling", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsTrue(Directory.Exists(siblingDir));
            Assert.IsTrue(File.Exists(siblingPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", siblingPath));
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
    public void RemoveCustomFolder_DoesNotFallbackToDefaultBaseWhenSavedAdditionalBaseIsMissing()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string targetDir = Path.Combine(outputBaseDir, "FallbackTarget");
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7404,
                name = "MissingBase",
                symbol = "MB",
                Output_dir = "FallbackTarget",
                custom_folder_output_base_name = "MissingAdditionalBase"
            };
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE default target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "default target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.RemoveCustomFolder(table);

            Assert.IsTrue(Directory.Exists(targetDir));
            Assert.IsTrue(File.Exists(targetPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ChangeCustomFolderBaseDirectory_UsesInjectedSettingsForDefaultAdditionalLists()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldBaseDir = Path.Combine(tempDirectory, "OldBase");
            string providerNewBaseDir = Path.Combine(tempDirectory, "ProviderNewBase");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalOutput");
            string oldDirectory = Path.Combine(oldBaseDir, "ProviderDefaults");
            string oldPath = Path.Combine(oldDirectory, "0000.lr2folder");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[\"GlobalAdditional\"]";
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerNewBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7408,
                name = "ProviderDefaults",
                symbol = "PDS",
                Output_dir = "ProviderDefaults",
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Folder A")],
                Folder_order = ["Folder A"]
            };
            Directory.CreateDirectory(oldDirectory);
            File.WriteAllText(oldPath, "#TITLE old", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "old", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ChangeCustomFolderBaseDirectory(oldBaseDir, providerNewBaseDir);

            Assert.AreEqual(1, providerCallCount);
            Assert.IsFalse(Directory.Exists(oldDirectory));
            Assert.IsTrue(File.Exists(Path.Combine(providerNewBaseDir, table.Output_dir, "0000.lr2folder")));
            Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void ChangeCustomFolderBaseDirectory_DoesNotFallbackToDefaultBaseWhenSavedAdditionalBaseIsMissing()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string oldDefaultBaseDir = Path.Combine(tempDirectory, "OldDefault");
            string newDefaultBaseDir = Path.Combine(tempDirectory, "NewDefault");
            string targetDir = Path.Combine(oldDefaultBaseDir, "FallbackTarget");
            string targetPath = Path.Combine(targetDir, "0000.lr2folder");
            Settings.Default.OperationModeLR2DB = true;
            Settings.Default.LR2CustomFolderOutputBaseDir = newDefaultBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7405,
                name = "MissingBaseMigration",
                symbol = "MBM",
                Output_dir = "FallbackTarget",
                custom_folder_output_base_name = "MissingAdditionalBase",
                entries =
                [
                    CreateEntry("ffffffffffffffffffffffffffffffff", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            Directory.CreateDirectory(targetDir);
            File.WriteAllText(targetPath, "#TITLE default target", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = targetPath, title = "default target", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(songDbPath)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.ChangeCustomFolderBaseDirectory(
                oldDefaultBaseDir,
                newDefaultBaseDir,
                additionalOutputBaseDirsBefore: "[]",
                additionalOutputBaseDirsAfter: "[]");

            Assert.IsTrue(Directory.Exists(targetDir));
            Assert.IsTrue(File.Exists(targetPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", targetPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public void MigrateCustomFolderOutputDirectory_DoesNotInferDefaultBaseWhenPreviousAdditionalBaseResolutionFailed()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string previousAdditionalOutputBaseDirs = Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string outputBaseDir = Path.Combine(tempDirectory, "CustomFolder");
            string oldOutputDir = Path.Combine(outputBaseDir, "OldFallback");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string newOutputDir = Path.Combine(outputBaseDir, "NewFallback");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = outputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            CustomFolderOutputSettingsSnapshot migrationSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = outputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "RootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getMigrationSettings = () =>
            {
                providerCallCount++;
                return migrationSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7406,
                name = "MissingBasePropertySave",
                symbol = "MBP",
                Output_dir = "NewFallback",
                custom_folder_output_base_name = null,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("99999999999999999999999999999999", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            Directory.CreateDirectory(oldOutputDir);
            File.WriteAllText(oldPath, "#TITLE old fallback", Encoding.GetEncoding("shift_jis"));
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "old fallback", type = 2 }, typeof(LR2SongDB.folder));
            }
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getMigrationSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(new[] { table }),
                    Dispatcher.CurrentDispatcher)
            };

            playlist.MigrateCustomFolderOutputDirectory(
                table,
                oldOutputDir,
                newOutputDir,
                wasRootFolderBefore: false,
                outputBaseDirBefore: null,
                inferOutputBaseDirBeforeWhenMissing: false);

            Assert.AreEqual(1, providerCallCount);
            Assert.IsTrue(Directory.Exists(oldOutputDir));
            Assert.IsTrue(File.Exists(oldPath));
            Assert.IsTrue(File.Exists(Path.Combine(newOutputDir, "0000.lr2folder")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM folder WHERE path = ?;", oldPath));
        }
        finally
        {
            Settings.Default.OperationModeLR2DB = previousOperationModeLr2Db;
            Settings.Default.LR2CustomFolderOutputBaseDir = previousOutputBaseDir;
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs = previousAdditionalOutputBaseDirs;
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
    public void MigrateCustomFolderOutputDirectory_DeletesOldOutputDirectoryRecursively()
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries =
                [
                    CreateEntry("cccccccccccccccccccccccccccccccc", "Folder A")
                ],
                Folder_order = ["Folder A"]
            };
            string oldOutputDir = Path.Combine(tempDirectory, "CustomFolder", "NestedTable");
            string newOutputDir = Path.Combine(tempDirectory, "MovedCustomFolder", "NestedTable");
            string oldPath = Path.Combine(oldOutputDir, "0000.lr2folder");
            string oldNestedPath = Path.Combine(oldOutputDir, "CLEAR FOLDER", "0 NO PLAY", "0000.lr2folder");
            string oldUnmanagedPath = Path.Combine(oldOutputDir, "manual-note.txt");
            string newPath = Path.Combine(newOutputDir, "0000.lr2folder");
            Directory.CreateDirectory(oldOutputDir);
            Directory.CreateDirectory(Path.GetDirectoryName(oldNestedPath));
            File.WriteAllText(oldPath, "#TITLE stale", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(oldNestedPath, "#TITLE nested stale", Encoding.GetEncoding("shift_jis"));
            File.WriteAllText(oldUnmanagedPath, "old custom folder note", Encoding.UTF8);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.CreateTable<LR2SongDB.folder>();
                db.InsertOrReplace(new LR2SongDB.folder { path = oldPath, title = "stale", type = 2 }, typeof(LR2SongDB.folder));
                db.InsertOrReplace(new LR2SongDB.folder { path = oldNestedPath, title = "nested stale", type = 2 }, typeof(LR2SongDB.folder));
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
            Assert.AreEqual(0, verify.Table<LR2SongDB.folder>().Count(row => row.path == oldNestedPath));
            LR2SongDB.folder generated = verify.Table<LR2SongDB.folder>().Single(row => row.path == newPath);
            Assert.AreEqual("NestedTable ALL", generated.title);
            Assert.IsFalse(Directory.Exists(oldOutputDir));
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
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
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

    private static string CreateBaseScoreDbPath(string tempDirectory)
    {
        string scoreDbPath = Path.Combine(tempDirectory, "score.db");
        using (var db = new LR2ScoreDBExtended(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
        }
        return scoreDbPath;
    }

    private static string CreateInstalledPlayHistoryScoreDbPath(string tempDirectory)
    {
        string scoreDbPath = CreateBaseScoreDbPath(tempDirectory);
        Lr2PlayHistorySchemaCheckResult result = new Lr2PlayHistorySchemaService().InstallOrRepair(scoreDbPath, isLr2LinkedProfile: true);
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, result.Status);
        return scoreDbPath;
    }

    private static string ReadShiftJisText(string path)
    {
        using FileStream stream = LongPathFileSystem.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.GetEncoding("shift_jis"), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string BuildLongDirectoryPath(string root, string leaf)
    {
        string path = root;
        while (Path.Combine(path, leaf).Length <= 270)
        {
            path = Path.Combine(path, "segment-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        }
        return Path.Combine(path, leaf);
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

    private static int InvokeRepairMissingCustomFolderOutputsAfterHydration(BMSPlaylist playlist, string reason, bool verifyRootOutputDirectoryRows = false)
    {
        MethodInfo methodInfo = typeof(BMSPlaylist).GetMethod("RepairMissingCustomFolderOutputsAfterHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(methodInfo);
        return (int)methodInfo.Invoke(playlist, [reason, verifyRootOutputDirectoryRows]);
    }

    private static BMSTableEntry CreateEntry(string md5, string folder)
    {
        return new BMSTableEntry(DynamicJson.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":\"" + folder + "\"}"));
    }

    private static BMSTableEntry CreateEntryWithLevel(string md5, int level)
    {
        return new BMSTableEntry(DynamicJson.Parse("{\"md5\":\"" + md5 + "\",\"title\":\"" + md5 + "\",\"level\":" + level + "}"));
    }

    private static void AssertClearFolderCommandMatchesAssistAndEasyRows(string songDbPath, string assistFolderText, string easyFolderText)
    {
        const string AssistOnlyHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string AssistEasyHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string AssistNoEasyHash = "cccccccccccccccccccccccccccccccc";
        const string AssistNullHistoryHash = "dddddddddddddddddddddddddddddddd";
        string assistCommand = ReadCustomFolderCommand(assistFolderText);
        string easyCommand = ReadCustomFolderCommand(easyFolderText);

        using var db = new LR2SongDBExtended(songDbPath);
        db.Execute("CREATE TABLE IF NOT EXISTS song(hash TEXT PRIMARY KEY, title TEXT);");
        db.Execute("CREATE TABLE IF NOT EXISTS score(hash TEXT PRIMARY KEY, clear INTEGER, rank INTEGER, op_history INTEGER, minbp INTEGER);");
        db.Execute("DELETE FROM playlist_entry WHERE playlist_id = 7305 AND md5 IN (?, ?, ?, ?);", AssistOnlyHash, AssistEasyHash, AssistNoEasyHash, AssistNullHistoryHash);
        InsertCustomFolderClassificationRow(db, AssistOnlyHash, clear: 2, rank: 8, opHistory: ClearTypeStorageConverter.OptionHistoryAssist);
        InsertCustomFolderClassificationRow(db, AssistEasyHash, clear: 2, rank: 0, opHistory: ClearTypeStorageConverter.OptionHistoryAssist | ClearTypeStorageConverter.OptionHistoryEasy);
        InsertCustomFolderClassificationRow(db, AssistNoEasyHash, clear: 2, rank: 0, opHistory: 0);
        InsertCustomFolderClassificationRow(db, AssistNullHistoryHash, clear: 2, rank: 0, opHistory: null);

        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistOnlyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, assistCommand, AssistEasyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistNoEasyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, assistCommand, AssistNullHistoryHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistOnlyHash));
        Assert.AreEqual(1L, CountCustomFolderCommandMatches(db, easyCommand, AssistEasyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistNoEasyHash));
        Assert.AreEqual(0L, CountCustomFolderCommandMatches(db, easyCommand, AssistNullHistoryHash));
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task ExternalTableRegistration_UsesOneCustomFolderSettingsSnapshotPerOperation()
    {
        bool previousOperationModeLr2Db = Settings.Default.OperationModeLR2DB;
        string previousOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDir;
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string providerOutputBaseDir = Path.Combine(tempDirectory, "ProviderOutput");
            string globalOutputBaseDir = Path.Combine(tempDirectory, "GlobalOutput");
            Settings.Default.OperationModeLR2DB = false;
            Settings.Default.LR2CustomFolderOutputBaseDir = globalOutputBaseDir;
            CustomFolderOutputSettingsSnapshot outputSettings = new()
            {
                OperationModeLR2DB = true,
                LR2CustomFolderOutputBaseDir = providerOutputBaseDir,
                LR2CustomFolderOutputBaseDirRootType = Path.Combine(tempDirectory, "ProviderRootOutput"),
                LR2CustomFolderAdditionalOutputBaseDirs = "[]"
            };
            int providerCallCount = 0;
            Func<CustomFolderOutputSettingsSnapshot> getOutputSettings = () =>
            {
                providerCallCount++;
                return outputSettings;
            };
            string songDbPath = CreateTempSongDbPath(tempDirectory);
            BMSPlaylist.EnsureSchema(songDbPath);
            var playlist = new BMSPlaylist(
                songDbPath,
                null,
                null,
                null,
                null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                getOutputSettings)
            {
                BMSTables = new DispatcherCollection<BMSTable>(
                    new ObservableCollection<BMSTable>(),
                    Dispatcher.CurrentDispatcher)
            };
            BMSTable singleTable = new()
            {
                playlist_id = 7801,
                name = "ProviderSingle",
                symbol = "PSS",
                Output_dir = "ProviderSingle",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [CreateEntry("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Single Folder")],
                Folder_order = ["Single Folder"]
            };

            await playlist.RegistrateExternalTableAsync(singleTable, false, "test_external_single");

            BMSTable batchTableA = new()
            {
                playlist_id = 7802,
                name = "ProviderBatchA",
                symbol = "PBA",
                Output_dir = "ProviderBatchA",
                ignore_folder_output = singleTable.ignore_folder_output,
                entries = [CreateEntry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Batch Folder A")],
                Folder_order = ["Batch Folder A"]
            };
            BMSTable batchTableB = new()
            {
                playlist_id = 7803,
                name = "ProviderBatchB",
                symbol = "PBB",
                Output_dir = "ProviderBatchB",
                ignore_folder_output = singleTable.ignore_folder_output,
                entries = [CreateEntry("cccccccccccccccccccccccccccccccc", "Batch Folder B")],
                Folder_order = ["Batch Folder B"]
            };

            var batchResult = await playlist.RegistrateExternalTablesAsync(
                [batchTableA, batchTableB],
                false,
                "test_external_batch");

            Assert.AreEqual(2, providerCallCount);
            Assert.AreEqual(2, batchResult.RegisteredTables.Count);
            foreach (BMSTable table in new[] { singleTable, batchTableA, batchTableB })
            {
                string outputDirectory = Path.Combine(providerOutputBaseDir, table.Output_dir);
                Assert.IsTrue(Directory.Exists(outputDirectory));
                Assert.IsTrue(Directory.EnumerateFiles(outputDirectory, "*.lr2folder").Any());
                Assert.IsFalse(Directory.Exists(Path.Combine(globalOutputBaseDir, table.Output_dir)));
            }
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

    private static void InsertCustomFolderClassificationRow(LR2SongDBExtended db, string hash, int clear, int rank, int? opHistory)
    {
        db.Execute("INSERT OR REPLACE INTO song(hash, title, path) VALUES (?, ?, ?);", hash, hash, hash + ".bms");
        db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (7305, ?, ?, 0);", hash, hash);
        db.Execute("INSERT OR REPLACE INTO score(hash, clear, rank, op_history, minbp) VALUES (?, ?, ?, ?, 0);", hash, clear, rank, opHistory);
    }

    private static long CountCustomFolderCommandMatches(LR2SongDBExtended db, string command, string hash)
    {
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM song LEFT JOIN score ON song.hash = score.hash WHERE song.hash = ? AND " + command, hash);
    }

    private static string ReadCustomFolderCommand(string folderText)
    {
        string line = folderText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .FirstOrDefault(value => value.StartsWith("#COMMAND ", StringComparison.Ordinal));
        Assert.IsFalse(string.IsNullOrWhiteSpace(line));
        return line.Substring("#COMMAND ".Length);
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
