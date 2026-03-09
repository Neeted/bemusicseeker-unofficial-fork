using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
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

        List<BMSTable> updated = playlist.UpdateBMSTablesInternal(reloadExtPlaylist: false, updateCallbackActions: new List<Action<BMSTable, bool, BMSTable>>
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

        List<BMSTable> updated = await playlist.UpdateBMSTablesInternalAsync(reloadExtPlaylist: false, updateCallbackActions: new List<Action<BMSTable, bool, BMSTable>>
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
}
