using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class Lr2FolderExistingRowLookup
{
    private const string TempExactPathTable = "lr2_folder_existing_path_scope";
    private const string TempPathColumn = "path";
    private const int TempInsertChunkSize = 400;

    internal static IReadOnlyList<LR2SongDB.folder> QueryExactPaths(
        LR2SongDBExtended songDb,
        IEnumerable<string> exactPaths)
    {
        if (songDb == null)
        {
            return [];
        }

        List<string> paths = BuildLookupPaths(exactPaths);
        if (paths.Count == 0)
        {
            return [];
        }

        BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
        PrepareTempExactPathTable(songDb);
        try
        {
            InsertTempExactPaths(songDb, paths);
            string folderTable = SQLiteTable<LR2SongDB.folder>.GetTableName();
            string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
            return songDb.Query<LR2SongDB.folder>(
                "SELECT f.*"
                + " FROM temp." + TempExactPathTable + " AS p"
                + " CROSS JOIN " + folderTable + " AS f INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE f." + pathColumn + " = p." + TempPathColumn + " COLLATE NOCASE;");
        }
        finally
        {
            ClearTempExactPathTable(songDb);
        }
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes)
    {
        if (songDb == null)
        {
            return [];
        }

        List<string> prefixes = BuildLookupPaths(folderPathPrefixes);
        if (prefixes.Count == 0)
        {
            return [];
        }

        BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
        string folderTable = SQLiteTable<LR2SongDB.folder>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        foreach (string prefix in prefixes)
        {
            if (!TryCreatePrefixUpperBound(prefix, out string upperBound))
            {
                continue;
            }

            foreach (LR2SongDB.folder row in songDb.Query<LR2SongDB.folder>(
                "SELECT * FROM " + folderTable + " INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE " + pathColumn + " COLLATE NOCASE >= ?"
                + " AND " + pathColumn + " COLLATE NOCASE < ?;",
                prefix,
                upperBound))
            {
                if (!string.IsNullOrWhiteSpace(row?.path))
                {
                    rowsByPath[row.path] = row;
                }
            }
        }
        return [.. rowsByPath.Values];
    }

    private static List<string> BuildLookupPaths(IEnumerable<string> paths)
    {
        return [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)];
    }

    private static void PrepareTempExactPathTable(LR2SongDBExtended songDb)
    {
        songDb.Execute(
            "CREATE TEMP TABLE IF NOT EXISTS " + TempExactPathTable
            + " (" + TempPathColumn + " TEXT PRIMARY KEY COLLATE NOCASE);");
        ClearTempExactPathTable(songDb);
    }

    private static void InsertTempExactPaths(LR2SongDBExtended songDb, IReadOnlyList<string> paths)
    {
        for (int offset = 0; offset < paths.Count; offset += TempInsertChunkSize)
        {
            int count = Math.Min(TempInsertChunkSize, paths.Count - offset);
            string placeholders = string.Join(",", Enumerable.Repeat("(?)", count));
            object[] args = new object[count];
            for (int index = 0; index < count; index++)
            {
                args[index] = paths[offset + index];
            }

            songDb.Execute(
                "INSERT OR IGNORE INTO temp." + TempExactPathTable
                + " (" + TempPathColumn + ") VALUES " + placeholders + ";",
                args);
        }
    }

    private static void ClearTempExactPathTable(LR2SongDBExtended songDb)
    {
        songDb.Execute("DELETE FROM temp." + TempExactPathTable + ";");
    }

    private static bool TryCreatePrefixUpperBound(string prefix, out string upperBound)
    {
        upperBound = null;
        if (string.IsNullOrEmpty(prefix))
        {
            return false;
        }

        char[] chars = prefix.ToCharArray();
        for (int index = chars.Length - 1; index >= 0; index--)
        {
            if (chars[index] == char.MaxValue)
            {
                continue;
            }

            chars[index] = (char)(chars[index] + 1);
            upperBound = new string(chars, 0, index + 1);
            return true;
        }

        return false;
    }
}
