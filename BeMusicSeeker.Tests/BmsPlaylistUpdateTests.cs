using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Livet;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows.Threading;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsPlaylistUpdateTests
{
    [TestMethod]
    [TestCategory("Playlist")]
    public void UpdateBmsTablesInternal_CallbackFailureDoesNotAbortUpdate()
    {
        string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        BMSPlaylist playlist = new BMSPlaylist(songDbPath);
        playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { new BMSTable() }), Dispatcher.CurrentDispatcher);
        bool callbackInvoked = false;

        List<BMSTable> updated = playlist.UpdateBMSTablesInternal(reloadExtPlaylist: false, updateCallbackActions: new List<Action<BMSPlaylist.PlaylistTableUpdateContext>>
        {
            delegate
            {
                callbackInvoked = true;
                throw new InvalidOperationException("callback failure");
            }
        }, syncResultCallback: null);

        Assert.IsTrue(callbackInvoked);
        Assert.AreEqual(0, updated.Count);
    }

    [TestMethod]
    [TestCategory("Playlist")]
    public async Task UpdateBmsTablesInternalAsync_CallbackFailureDoesNotAbortUpdate()
    {
        string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        BMSPlaylist playlist = new BMSPlaylist(songDbPath);
        playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { new BMSTable() }), Dispatcher.CurrentDispatcher);
        bool callbackInvoked = false;

        List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: false, updateCallbackActions: new List<Action<BMSPlaylist.PlaylistTableUpdateContext>>
        {
            delegate
            {
                callbackInvoked = true;
                throw new InvalidOperationException("callback failure");
            }
        }, syncResultCallback: null);

        Assert.IsTrue(callbackInvoked);
        Assert.AreEqual(0, updated.Count);
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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            BMSPlaylist.PlaylistTableUpdateContext? callbackContext = null;

            List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions: new List<Action<BMSPlaylist.PlaylistTableUpdateContext>>
            {
                delegate(BMSPlaylist.PlaylistTableUpdateContext context)
                {
                    callbackContext = context;
                }
            }, syncResultCallback: null);

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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            table.playlist_id = 900001;
            table.header_sha256 = null;
            table.data_sha256 = null;
            DateTime existingLastUpdate = new DateTime(2024, 6, 1, 10, 20, 30);
            table.last_update = existingLastUpdate;
            using (LR2SongDBExtended db = new LR2SongDBExtended(songDbPath))
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

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync(new[] { table }, new List<Action<BMSPlaylist.PlaylistTableUpdateContext>>
            {
                delegate(BMSPlaylist.PlaylistTableUpdateContext context)
                {
                    callbackContext = context;
                }
            }, reason: "test_hash_initialization");

            Assert.AreEqual(1, results.Count);
            Assert.IsFalse(results[0].Updated);
            Assert.IsNotNull(callbackContext);
            Assert.IsFalse(callbackContext!.Updated);
            Assert.AreEqual(existingLastUpdate, results[0].ResultTable.last_update);
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.header_sha256));
            Assert.IsFalse(string.IsNullOrWhiteSpace(results[0].ResultTable.data_sha256));
            Assert.AreEqual(2, results[0].ResultTable.entries.Count((BMSTableEntry entry) => !entry.is_removed));
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.playlist persisted = verify.Table<LR2SongDBExtended.playlist>().Single((LR2SongDBExtended.playlist row) => row.playlist_id == 900001);
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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);

            List<PlaylistSyncProgressSnapshot> snapshots = new List<PlaylistSyncProgressSnapshot>();

            List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions: null, syncResultCallback: null, progressCallback: snapshots.Add);

            Assert.IsNotNull(updated);
            Assert.IsTrue(snapshots.Count >= 3);
            Assert.IsTrue(snapshots.Any((PlaylistSyncProgressSnapshot s) => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 0));
            Assert.IsTrue(snapshots.Any((PlaylistSyncProgressSnapshot s) => s.IsActive && s.TotalTableCount == 1 && s.CompletedTableCount == 1 && string.Equals(s.CurrentTableName, "ProgressTable", StringComparison.Ordinal)));
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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable externalTable = await playlist.LoadExternalTableAsync(new Uri(externalHeaderPath));
            externalTable.EnableExternalSync();
            BMSTable manualTable = new BMSTable
            {
                name = "ManualTarget",
                symbol = "M",
                Header_url = new Uri(manualHeaderPath)
            };
            manualTable.DisableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { externalTable, manualTable }), Dispatcher.CurrentDispatcher);
            List<PlaylistSyncAttemptResult> syncResults = new List<PlaylistSyncAttemptResult>();

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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.playlist_id = 9001;
            table.DisableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Before\",\"artist\":\"Artist\",\"level\":\"1\"},{\"md5\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"After\",\"artist\":\"Artist\",\"level\":\"2\"}]"));

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync(new[] { table }, reason: "test_explicit_reload");

            Assert.AreEqual(1, results.Count);
            Assert.IsTrue(results[0].Succeeded);
            Assert.IsTrue(results[0].Updated);
            Assert.IsFalse(results[0].ResultTable.is_external_sync);
            using (results[0].ResultTable.ReaderWriterLock.GetReaderGuard())
            {
                Assert.AreEqual(2, results[0].ResultTable.entries.Count((BMSTableEntry entry) => !entry.is_removed));
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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable table = new BMSTable
            {
                name = "NoUri",
                symbol = "N"
            };
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            List<PlaylistSyncProgressSnapshot> snapshots = new List<PlaylistSyncProgressSnapshot>();

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync(new[] { table }, progressCallback: snapshots.Add, reason: "test_skip_no_uri");

            Assert.AreEqual(0, results.Count);
            Assert.IsTrue(snapshots.Count >= 2);
            Assert.IsTrue(snapshots.All((PlaylistSyncProgressSnapshot snapshot) => snapshot.TotalTableCount == 0));
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
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable goodTable = await playlist.LoadExternalTableAsync(new Uri(goodHeaderPath));
            BMSTable badTable = new BMSTable
            {
                name = "BadTarget",
                symbol = "B",
                Header_url = new Uri(badHeaderPath)
            };
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { goodTable, badTable }), Dispatcher.CurrentDispatcher);
            List<PlaylistSyncAttemptResult> syncResults = new List<PlaylistSyncAttemptResult>();

            List<BMSPlaylist.PlaylistReloadTargetResult> results = await playlist.ReloadPlaylistTargetsAsync(new[] { goodTable, badTable }, syncResultCallback: syncResults.Add, reason: "test_partial_failure");

            Assert.AreEqual(2, results.Count);
            Assert.AreEqual(2, syncResults.Count);
            Assert.IsTrue(results.Any((BMSPlaylist.PlaylistReloadTargetResult result) => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(results.Any((BMSPlaylist.PlaylistReloadTargetResult result) => result.SourceTable == badTable && !result.Succeeded));
            Assert.IsTrue(syncResults.Any((PlaylistSyncAttemptResult result) => result.SourceTable == goodTable && result.Succeeded));
            Assert.IsTrue(syncResults.Any((PlaylistSyncAttemptResult result) => result.SourceTable == badTable && !result.Succeeded));
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
            using (LR2SongDBExtended _ = new LR2SongDBExtended(songDbPath))
            {
            }
            BMSPlaylist.EnsureSchema(songDbPath);
            BMSTable persistedTable = new BMSTable
            {
                playlist_id = 7001,
                name = "ReloadedTable",
                symbol = "R",
                Output_dir = "ReloadedTable"
            };
            using (LR2SongDBExtended db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(persistedTable, typeof(LR2SongDBExtended.playlist));
            }
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            playlist.BMSTables = new DispatcherCollection<BMSTable>(
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
                Dispatcher.CurrentDispatcher);
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
            BMSTable table = new BMSTable
            {
                playlist_id = 7101,
                name = "BeforeName",
                symbol = "BN",
                Output_dir = "BeforeName"
            };
            using (LR2SongDBExtended db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                db.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, folder) VALUES (7101, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'Song', '1');");
            }
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            playlist.BMSTables = new DispatcherCollection<BMSTable>(
                new ObservableCollection<BMSTable>(new[] { table }),
                Dispatcher.CurrentDispatcher);

            table.name = "AfterName";
            playlist.CommitBMSTableHeaderToDB(table);

            using (LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath))
            {
                Assert.AreEqual("AfterName", verify.ExecuteScalar<string>("SELECT name FROM playlist WHERE playlist_id = 7101;"));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM playlist_entry WHERE playlist_id = 7101;"));
            }
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
    public void LoadStartupPlaylistEntries_UsesPlaylistIdProjectionAndKeepsRemovedRows()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string songDbPath = Path.Combine(tempDirectory, "song.db");
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute("CREATE TABLE playlist_entry (playlist_id INTEGER NULL, md5 TEXT NULL, sha256 TEXT NULL, level REAL NULL, title TEXT, artist TEXT, folder TEXT, lr2_bmsid TEXT, url TEXT, url_diff TEXT, name_diff TEXT, org_md5 TEXT, adddate TEXT, comment TEXT, memo TEXT, is_removed INTEGER NOT NULL DEFAULT 0);");
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", 10, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "active", 0);
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", 10, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "removed", 1);
                songDb.Execute("INSERT INTO playlist_entry (playlist_id, md5, title, is_removed) VALUES (?, ?, ?, ?);", null, "cccccccccccccccccccccccccccccccc", "orphan", 0);
            }

            PlaylistEntriesHydrationLoadResult result = new BmsLibraryDbGateway(songDbPath).LoadStartupPlaylistEntries();

            Assert.AreEqual("startup_entries", result.Projection);
            Assert.AreEqual(2, result.RowCount);
            Assert.IsTrue(result.Entries.Any((BMSTableEntry entry) => entry.title == "active" && !entry.is_removed));
            Assert.IsTrue(result.Entries.Any((BMSTableEntry entry) => entry.title == "removed" && entry.is_removed));
            Assert.IsFalse(result.Entries.Any((BMSTableEntry entry) => entry.title == "orphan"));
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
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static string CreateTempSongDbPath(string tempDirectory)
    {
        string sourceSongDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
        string tempSongDbPath = Path.Combine(tempDirectory, "song.db");
        File.Copy(sourceSongDbPath, tempSongDbPath, overwrite: true);
        return tempSongDbPath;
    }
}
