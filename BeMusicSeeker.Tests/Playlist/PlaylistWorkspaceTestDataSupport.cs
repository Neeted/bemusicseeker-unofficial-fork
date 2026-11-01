using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceTestDataSupport
{
    /// <summary>同じfixtureのDB所有者でLR2 IDと実scoreを準備し、ライブラリへ読み込みます。</summary>
    /// <param name="databaseScope">準備と読取りの接続を共有するDB所有者。</param>
    internal static BMSLibrary CreateLibraryWithLr2Id(ApplicationDatabaseScope databaseScope, string songDbPath, int lr2Id = 123)
    {
        string scoreDbPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "score.db");
        using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
            db.Insert(new LR2ScoreDB.player { id = "test-player", irid = lr2Id });
            db.Insert(new LR2ScoreDB.score { hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        }

        var library = new TestBmsLibrary(databaseScope, songDbPath, null, scoreDbPath);
        library.InitializeScoresOnly(null);
        Assert.AreEqual(lr2Id, library.LR2ID);
        return library;
    }

    /// <summary>同じfixtureのDB所有者を使い、独立した保存入力のheaderとentriesを準備します。</summary>
    /// <param name="databaseScope">保存準備の接続を所有するDB所有者。</param>
    internal static void PersistPlaylistAggregate(ApplicationDatabaseScope databaseScope, string songDbPath, BMSTable table)
    {
        using LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath);
        db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
        foreach (BMSTableEntry entry in table.entries ?? [])
        {
            entry.playlist_id = table.playlist_id;
            db.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
        }
    }

    internal static string[] CreateSongTableRow(string? md5, string path)
    {
        string[] values = new string[30];
        if (md5 != null)
        {
            values[0] = md5;
        }
        values[1] = "Installed chart";
        values[3] = "Artist";
        values[7] = path;
        return values;
    }
}
