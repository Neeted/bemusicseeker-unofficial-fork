using System;
using System.IO;
using System.Security;
using BeMusicSeeker.Models.LR2;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2SongDbWriter
{
    internal static bool UpsertGeneratedSong(LR2SongDBExtended songDb, BMSFile song)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (song == null || string.IsNullOrWhiteSpace(song.path))
        {
            return false;
        }

        GeneratedSongRow existingSong = FindSongByPath(songDb, song.path);
        Lr2SongRowEnricher.EnrichGeneratedSong(song);
        ApplyGeneratedPersistenceDefaults(song, isNewRow: existingSong == null);
        string previousHash = existingSong?.hash;
        bool changed = false;
        if (existingSong == null)
        {
            songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            changed = true;
        }
        else if (!HasSameGeneratedColumns(song, existingSong))
        {
            UpdateGeneratedColumns(songDb, song);
            changed = true;
        }
        BmsLibraryDbGateway.UpsertChartDigest(songDb, song);
        BmsLibraryDbGateway.DeleteChartDigestIfOrphaned(songDb, previousHash, song.hash);
        return changed;
    }

    private static void ApplyGeneratedPersistenceDefaults(BMSFile song, bool isNewRow)
    {
        if (song == null)
        {
            return;
        }
        if ((!song.date.HasValue || song.date <= 0) && TryGetLastWriteTimeUtc(song.path, out DateTime lastWriteTimeUtc))
        {
            song.date = Lr2SongRowEnricher.ToLr2UnixSeconds(lastWriteTimeUtc);
        }
        if (isNewRow && (!song.adddate.HasValue || song.adddate <= 0))
        {
            song.adddate = Lr2SongRowEnricher.ToLr2UnixSeconds(DateTime.UtcNow);
        }
    }

    private static bool TryGetLastWriteTimeUtc(string path, out DateTime lastWriteTimeUtc)
    {
        lastWriteTimeUtc = default;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    internal static void UpdateDate(LR2SongDBExtended songDb, string path, int date)
    {
        UpdateMetadata(songDb, path, date, null);
    }

    internal static void UpdateMetadata(LR2SongDBExtended songDb, string path, int date, int? textFlag)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        if (textFlag.HasValue)
        {
            songDb.Execute(
                "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
                + " SET " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?, "
                + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " = ?"
                + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
                date,
                textFlag.Value,
                path);
            return;
        }
        songDb.Execute(
            "UPDATE " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " SET " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " = ?"
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path) + " = ?;",
            date,
            path);
    }

    private static GeneratedSongRow FindSongByPath(LR2SongDBExtended songDb, string path)
    {
        var existingRows = songDb.Query<GeneratedSongRow>(
            "SELECT "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.hash) + " AS hash, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.title) + " AS title, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subtitle) + " AS subtitle, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.artist) + " AS artist, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.subartist) + " AS subartist, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.genre) + " AS genre, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.type) + " AS type, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.folder) + " AS folder, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.stagefile) + " AS stagefile, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.banner) + " AS banner, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.backbmp) + " AS backbmp, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.parent) + " AS parent, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.level) + " AS level, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.difficulty) + " AS difficulty, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.maxbpm) + " AS maxbpm, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.minbpm) + " AS minbpm, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.mode) + " AS mode, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.judge) + " AS judge, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.longnote) + " AS longnote, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.bga) + " AS bga, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.random) + " AS random, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.date) + " AS date, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " AS txt, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.karinotes) + " AS karinotes, "
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.exlevel) + " AS exlevel"
            + " FROM " + SQLiteTable<LR2SongDB.song>.GetTableName()
            + " WHERE " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.path)
            + " = ? LIMIT 1;",
            path);
        return existingRows.Count == 0 ? null : existingRows[0];
    }

    private static bool HasSameGeneratedColumns(BMSFile expected, GeneratedSongRow existing)
    {
        return expected != null
            && existing != null
            && string.Equals(expected.hash, existing.hash, StringComparison.Ordinal)
            && string.Equals(expected.title, existing.title, StringComparison.Ordinal)
            && string.Equals(expected.subtitle, existing.subtitle, StringComparison.Ordinal)
            && string.Equals(expected.artist, existing.artist, StringComparison.Ordinal)
            && string.Equals(expected.subartist, existing.subartist, StringComparison.Ordinal)
            && string.Equals(expected.genre, existing.genre, StringComparison.Ordinal)
            && expected.type == existing.type
            && string.Equals(expected.folder, existing.folder, StringComparison.Ordinal)
            && string.Equals(expected.stagefile, existing.stagefile, StringComparison.Ordinal)
            && string.Equals(expected.banner, existing.banner, StringComparison.Ordinal)
            && string.Equals(expected.backbmp, existing.backbmp, StringComparison.Ordinal)
            && string.Equals(expected.parent, existing.parent, StringComparison.Ordinal)
            && expected.level == existing.level
            && expected.difficulty == existing.difficulty
            && expected.maxbpm == existing.maxbpm
            && expected.minbpm == existing.minbpm
            && expected.mode == existing.mode
            && expected.judge == existing.judge
            && expected.longnote == existing.longnote
            && expected.bga == existing.bga
            && expected.random == existing.random
            && expected.date == existing.date
            && (!expected.txt.HasValue || expected.txt == existing.txt)
            && expected.karinotes == existing.karinotes
            && expected.exlevel == existing.exlevel;
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
            + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + " = COALESCE(?, " + SQLiteTable<LR2SongDB.song>.GetColumnName(row => row.txt) + "), "
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

    private sealed class GeneratedSongRow
    {
        public string hash { get; set; }

        public string title { get; set; }

        public string subtitle { get; set; }

        public string artist { get; set; }

        public string subartist { get; set; }

        public string genre { get; set; }

        public int? type { get; set; }

        public string folder { get; set; }

        public string stagefile { get; set; }

        public string banner { get; set; }

        public string backbmp { get; set; }

        public string parent { get; set; }

        public int? level { get; set; }

        public int? difficulty { get; set; }

        public int? maxbpm { get; set; }

        public int? minbpm { get; set; }

        public int? mode { get; set; }

        public int? judge { get; set; }

        public int? longnote { get; set; }

        public int? bga { get; set; }

        public int? random { get; set; }

        public int? date { get; set; }

        public int? txt { get; set; }

        public int? karinotes { get; set; }

        public int? exlevel { get; set; }
    }
}
