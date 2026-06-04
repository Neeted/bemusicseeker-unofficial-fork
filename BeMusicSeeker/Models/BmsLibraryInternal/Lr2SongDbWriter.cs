using System;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongDbWriter
{
    internal static void UpsertGeneratedSong(LR2SongDBExtended songDb, BMSFile song)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return;
        }

        Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(song);
        bool rowExists = TryGetSongHashByPath(songDb, song.path, out string previousHash);
        if (!rowExists)
        {
            songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
        }
        else
        {
            UpdateGeneratedColumns(songDb, song);
        }
        BmsLibraryDbGateway.UpsertChartDigest(songDb, song);
        BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, previousHash, song.hash);
    }

    internal static void UpdateDate(LR2SongDBExtended songDb, string path, int date)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
            date,
            path);
    }

    private static bool TryGetSongHashByPath(LR2SongDBExtended songDb, string path, out string hash)
    {
        var existingRows = songDb.Query<SongHashRow>(
            "SELECT " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " AS Hash"
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " = ? LIMIT 1;",
            path);
        if (existingRows.Count == 0)
        {
            hash = null;
            return false;
        }
        hash = existingRows[0]?.Hash;
        return true;
    }

    private static void UpdateGeneratedColumns(LR2SongDBExtended songDb, BMSFile song)
    {
        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes) + " = ?, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
            song.hash,
            song.title,
            song.subtitle,
            song.artist,
            song.subartist,
            song.genre,
            song.type,
            song.folder,
            song.stagefile,
            song.banner,
            song.backbmp,
            song.parent,
            song.level,
            song.difficulty,
            song.maxbpm,
            song.minbpm,
            song.mode,
            song.judge,
            song.longnote,
            song.bga,
            song.random,
            song.date,
            song.txt,
            song.karinotes,
            song.exlevel,
            song.path);
    }

    private sealed class SongHashRow
    {
        public string Hash { get; set; }
    }
}
