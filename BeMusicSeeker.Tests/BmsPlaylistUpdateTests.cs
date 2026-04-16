using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
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
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"SnapshotTable\",\r\n\"symbol\":\"S\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"title\":\"Snapshot Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
            BMSPlaylist playlist = new BMSPlaylist(songDbPath);
            BMSTable table = await playlist.LoadExternalTableAsync(new Uri(headerJsonPath));
            table.EnableExternalSync();
            playlist.BMSTables = new DispatcherCollection<BMSTable>(new ObservableCollection<BMSTable>(new[] { table }), Dispatcher.CurrentDispatcher);
            BMSPlaylist.PlaylistTableUpdateContext callbackContext = null;

            List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true, updateCallbackActions: new List<Action<BMSPlaylist.PlaylistTableUpdateContext>>
            {
                delegate(BMSPlaylist.PlaylistTableUpdateContext context)
                {
                    callbackContext = context;
                }
            }, syncResultCallback: null);

            Assert.IsNotNull(callbackContext);
            Assert.AreSame(table, callbackContext.OldTable);
            Assert.IsNotNull(callbackContext.NewTable);
            Assert.IsNotNull(callbackContext.OldEntriesSnapshot);
            Assert.IsNotNull(callbackContext.NewEntriesSnapshot);
            Assert.AreEqual(1, callbackContext.OldEntriesSnapshot.Count);
            Assert.AreEqual(1, callbackContext.NewEntriesSnapshot.Count);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", callbackContext.OldEntriesSnapshot[0].md5);
            Assert.AreEqual("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", callbackContext.NewEntriesSnapshot[0].md5);
            Assert.AreEqual(new string('b', 64), callbackContext.NewEntriesSnapshot[0].sha256);
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
    public async Task UpdateBmsTablesInternalAsync_ReportsPlaylistSyncProgressSnapshots()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsPlaylistUpdateTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string headerJsonPath = Path.Combine(tempDirectory, "header.json");
            string scoreJsonPath = Path.Combine(tempDirectory, "score.json");
            File.WriteAllBytes(headerJsonPath, CreateUtf8BomBytes("{\r\n\"name\":\"ProgressTable\",\r\n\"symbol\":\"P\",\r\n\"data_url\":\"./score.json\",\r\n\"level_order\":[1]\r\n}"));
            File.WriteAllBytes(scoreJsonPath, CreateUtf8BomBytes("[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Progress Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"));

            string songDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "song_snapshot", "song.db");
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
}
