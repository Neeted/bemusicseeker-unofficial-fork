using System;
using System.IO;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2PlaylistEntryPersistenceTests
{
    [TestMethod]
    public void PlaylistEntryTitleRoundTripsThroughSongDb()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker-playlist-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string databasePath = Path.Combine(root, "song.db");
        try
        {
            using (var database = new LR2SongDBExtended(databasePath))
            {
                database.CreateTable<LR2SongDBExtended.playlist_entry>();
                database.Insert(new LR2SongDBExtended.playlist_entry
                {
                    playlist_id = 1,
                    md5 = "839194ef691f63a91a97b2a7c481bb1d",
                    title = "E1 Fixture Song"
                });
            }

            using (var database = new LR2SongDBExtended(databasePath))
            {
                LR2SongDBExtended.playlist_entry entry = database.FindWithQuery<LR2SongDBExtended.playlist_entry>(
                    "SELECT * FROM playlist_entry WHERE playlist_id = ?",
                    1);
                Assert.IsNotNull(entry);
                Assert.AreEqual("E1 Fixture Song", entry.title);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
