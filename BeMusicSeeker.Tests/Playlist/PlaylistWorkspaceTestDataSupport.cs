using System;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

internal static class PlaylistWorkspaceTestDataSupport
{
    internal static string CreatePlaylistRestoreDump(int playlistId, string name, string symbol)
    {
        string playlistSql = "INSERT INTO playlist (playlist_id, name, symbol, folder_order, folder_sort_key, folder_sort_ascending, entry_type, page_url, header_url, data_url, compat_prefix, last_update, org_name, org_symbol, ignore_folder_output, is_external_sync, output_dir, is_root_folder) VALUES ("
            + playlistId
            + ", '"
            + name
            + "', '"
            + symbol
            + "', '', 0, 1, 0, NULL, NULL, NULL, NULL, 638857728000000000, NULL, NULL, 0, 0, NULL, 0);";
        return string.Join("\v" + Environment.NewLine, [playlistSql, string.Empty, string.Empty]);
    }

    internal static BMSLibrary CreateLibraryWithLr2Id(string songDbPath, int lr2Id = 123)
    {
        string scoreDbPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "score.db");
        using (var db = new LR2ScoreDBExtended(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
            db.Insert(new LR2ScoreDB.player { id = "test-player", irid = lr2Id });
            db.Insert(new LR2ScoreDB.score { hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        }

        var library = new TestBmsLibrary(songDbPath, null, scoreDbPath);
        library.InitializeScoresOnly(null);
        Assert.AreEqual(lr2Id, library.LR2ID);
        return library;
    }

    internal static void PersistPlaylistAggregate(string songDbPath, BMSTable table)
    {
        using var db = new LR2SongDBExtended(songDbPath);
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
