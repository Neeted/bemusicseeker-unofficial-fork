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
    private static readonly IComparer<string> PathBoundComparer = new SQLiteNoCasePathComparer();

    internal static IReadOnlyList<LR2SongDB.folder> QueryExactPaths(
        LR2SongDBExtended songDb,
        IEnumerable<string> exactPaths)
    {
        return QueryExactPaths(songDb, exactPaths, "f.*");
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryExactPathsForNormalFolderMtime(
        LR2SongDBExtended songDb,
        IEnumerable<string> exactPaths)
    {
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        string typeColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.type);
        string dateColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.date);
        return QueryExactPaths(
            songDb,
            exactPaths,
            "f." + pathColumn + ", f." + typeColumn + ", f." + dateColumn);
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryExactPathsForCustomFolderLayout(
        LR2SongDBExtended songDb,
        IEnumerable<string> exactPaths)
    {
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        string typeColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.type);
        string dateColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.date);
        string parentColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.parent);
        return QueryExactPaths(
            songDb,
            exactPaths,
            "f." + pathColumn + ", f." + typeColumn + ", f." + dateColumn + ", f." + parentColumn);
    }

    private static IReadOnlyList<LR2SongDB.folder> QueryExactPaths(
        LR2SongDBExtended songDb,
        IEnumerable<string> exactPaths,
        string selectColumns)
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
                "SELECT " + selectColumns
                + " FROM temp." + TempExactPathTable + " AS p"
                + " CROSS JOIN " + folderTable + " AS f INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE f." + pathColumn + " = p." + TempPathColumn + " COLLATE NOCASE;");
        }
        finally
        {
            ClearTempExactPathTable(songDb);
        }
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryLr2FolderPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes,
        IEnumerable<string> excludedFolderPathPrefixes)
    {
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        return QueryPathPrefixScopes(
            songDb,
            folderPathPrefixes,
            excludedFolderPathPrefixes,
            selectColumns: "*",
            additionalFilter: " AND " + pathColumn + " LIKE '%.lr2folder' COLLATE NOCASE");
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryNormalFolderMtimePathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes)
    {
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        string typeColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.type);
        string dateColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.date);
        return QueryPathPrefixScopes(
            songDb,
            folderPathPrefixes,
            [],
            pathColumn + ", " + typeColumn + ", " + dateColumn,
            " AND " + typeColumn + " = 1");
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes)
    {
        return QueryPathPrefixScopes(songDb, folderPathPrefixes, []);
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes,
        IEnumerable<string> excludedFolderPathPrefixes)
    {
        return QueryPathPrefixScopes(songDb, folderPathPrefixes, excludedFolderPathPrefixes, selectColumns: "*", additionalFilter: string.Empty);
    }

    internal static IReadOnlyList<LR2SongDB.folder> QueryCustomFolderLayoutPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes)
    {
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        string typeColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.type);
        string dateColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.date);
        string parentColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.parent);
        return QueryPathPrefixScopes(
            songDb,
            folderPathPrefixes,
            [],
            pathColumn + ", " + typeColumn + ", " + dateColumn + ", " + parentColumn,
            string.Empty);
    }

    private static IReadOnlyList<LR2SongDB.folder> QueryPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes,
        IEnumerable<string> excludedFolderPathPrefixes,
        string selectColumns,
        string additionalFilter)
    {
        if (songDb == null)
        {
            return [];
        }

        List<PathPrefixRange> excludedRanges = CreatePrefixRanges(excludedFolderPathPrefixes);
        List<PathPrefixRange> ranges = SubtractExcludedPrefixRanges(
            CreatePrefixRanges(folderPathPrefixes),
            excludedRanges);
        if (ranges.Count == 0)
        {
            return [];
        }

        BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
        string folderTable = SQLiteTable<LR2SongDB.folder>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        string filterSql = string.IsNullOrWhiteSpace(additionalFilter) ? string.Empty : additionalFilter;
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        foreach (PathPrefixRange range in ranges)
        {
            foreach (LR2SongDB.folder row in songDb.Query<LR2SongDB.folder>(
                "SELECT " + selectColumns + " FROM " + folderTable + " INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE " + pathColumn + " COLLATE NOCASE >= ?"
                + " AND " + pathColumn + " COLLATE NOCASE < ?"
                + filterSql + ";",
                range.Lower,
                range.Upper))
            {
                if (!string.IsNullOrWhiteSpace(row?.path))
                {
                    rowsByPath[row.path] = row;
                }
            }
        }
        return [.. rowsByPath.Values];
    }

    private static List<PathPrefixRange> CreatePrefixRanges(IEnumerable<string> folderPathPrefixes)
    {
        var ranges = new List<PathPrefixRange>();
        foreach (string prefix in BuildLookupPaths(folderPathPrefixes))
        {
            if (!TryCreatePrefixUpperBound(prefix, out string upperBound)
                || ComparePathBounds(prefix, upperBound) >= 0)
            {
                continue;
            }

            ranges.Add(new PathPrefixRange(prefix, upperBound));
        }

        return MergePrefixRanges(ranges);
    }

    private static List<PathPrefixRange> MergePrefixRanges(IEnumerable<PathPrefixRange> ranges)
    {
        List<PathPrefixRange> ordered = [.. (ranges ?? [])
            .Where(range => !string.IsNullOrWhiteSpace(range.Lower)
                && !string.IsNullOrWhiteSpace(range.Upper)
                && ComparePathBounds(range.Lower, range.Upper) < 0)
            .OrderBy(range => range.Lower, PathBoundComparer)
            .ThenBy(range => range.Upper, PathBoundComparer)];
        var result = new List<PathPrefixRange>();
        foreach (PathPrefixRange range in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(range);
                continue;
            }

            PathPrefixRange last = result[result.Count - 1];
            if (ComparePathBounds(range.Lower, last.Upper) <= 0)
            {
                result[result.Count - 1] = new PathPrefixRange(
                    last.Lower,
                    ComparePathBounds(range.Upper, last.Upper) > 0 ? range.Upper : last.Upper);
                continue;
            }

            result.Add(range);
        }

        return result;
    }

    private static List<PathPrefixRange> SubtractExcludedPrefixRanges(
        IReadOnlyList<PathPrefixRange> includeRanges,
        IReadOnlyList<PathPrefixRange> excludeRanges)
    {
        if (includeRanges == null || includeRanges.Count == 0)
        {
            return [];
        }

        if (excludeRanges == null || excludeRanges.Count == 0)
        {
            return [.. includeRanges];
        }

        var result = new List<PathPrefixRange>();
        int excludeIndex = 0;
        foreach (PathPrefixRange include in includeRanges)
        {
            while (excludeIndex < excludeRanges.Count
                && ComparePathBounds(excludeRanges[excludeIndex].Upper, include.Lower) <= 0)
            {
                excludeIndex++;
            }

            string nextLower = include.Lower;
            for (int index = excludeIndex; index < excludeRanges.Count; index++)
            {
                PathPrefixRange exclude = excludeRanges[index];
                if (ComparePathBounds(exclude.Lower, include.Upper) >= 0)
                {
                    break;
                }

                if (ComparePathBounds(nextLower, exclude.Lower) < 0)
                {
                    result.Add(new PathPrefixRange(
                        nextLower,
                        ComparePathBounds(exclude.Lower, include.Upper) < 0 ? exclude.Lower : include.Upper));
                }

                if (ComparePathBounds(exclude.Upper, nextLower) > 0)
                {
                    nextLower = exclude.Upper;
                }

                if (ComparePathBounds(nextLower, include.Upper) >= 0)
                {
                    break;
                }
            }

            if (ComparePathBounds(nextLower, include.Upper) < 0)
            {
                result.Add(new PathPrefixRange(nextLower, include.Upper));
            }
        }

        return result;
    }

    private static int ComparePathBounds(string left, string right)
    {
        return PathBoundComparer.Compare(left, right);
    }

    private static List<string> BuildLookupPaths(IEnumerable<string> paths)
    {
        return [.. (paths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, PathBoundComparer)];
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

    private readonly struct PathPrefixRange(string lower, string upper)
    {
        public string Lower { get; } = lower;

        public string Upper { get; } = upper;
    }

    private sealed class SQLiteNoCasePathComparer : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }
            if (x == null)
            {
                return -1;
            }
            if (y == null)
            {
                return 1;
            }

            int length = Math.Min(x.Length, y.Length);
            for (int index = 0; index < length; index++)
            {
                char left = FoldAsciiUpper(x[index]);
                char right = FoldAsciiUpper(y[index]);
                if (left != right)
                {
                    return left < right ? -1 : 1;
                }
            }

            if (x.Length == y.Length)
            {
                return 0;
            }
            return x.Length < y.Length ? -1 : 1;
        }

        private static char FoldAsciiUpper(char value)
        {
            return value >= 'A' && value <= 'Z'
                ? (char)(value + ('a' - 'A'))
                : value;
        }
    }
}
