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
