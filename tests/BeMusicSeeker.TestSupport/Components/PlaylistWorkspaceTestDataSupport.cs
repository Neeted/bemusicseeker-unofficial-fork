using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>workspaceの実score読取りと保存再読込みに必要な固定DB入力を用意します。</summary>
internal static class PlaylistWorkspaceTestDataSupport
{
    /// <summary>同じfixtureのDB所有者でLR2 IDと実scoreを準備し、ライブラリへ読み込みます。</summary>
    /// <param name="databaseScope">準備と読取りの接続を共有するDB所有者。</param>
    /// <param name="modelUiScheduler">モデル通知の配送境界。省略時の既存callerはWPF既定を維持します。</param>
    internal static BMSLibrary CreateLibraryWithLr2Id(ApplicationDatabaseScope databaseScope, string songDbPath, int lr2Id = 123, IUiScheduler? modelUiScheduler = null)
    {
        string scoreDbPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "score.db");
        using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(scoreDbPath))
        {
            db.CreateTable<LR2ScoreDB.score>();
            db.CreateTable<LR2ScoreDB.player>();
            db.Insert(new LR2ScoreDB.player { id = "test-player", irid = lr2Id });
            db.Insert(new LR2ScoreDB.score { hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" });
        }

        TestBmsLibrary library = modelUiScheduler == null
            ? new TestBmsLibrary(databaseScope, songDbPath, null, scoreDbPath)
            : new TestBmsLibrary(databaseScope, songDbPath, null, scoreDbPath, null, new UnexpectedBmsLibraryDialogService(), modelUiScheduler);
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

    /// <summary>譜面表の既存列配置に合わせた入力行を作り、hash欠落と指定pathを維持します。</summary>
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
