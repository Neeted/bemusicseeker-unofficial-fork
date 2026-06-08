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
        return QueryPathPrefixScopes(songDb, folderPathPrefixes, excludedFolderPathPrefixes, lr2FolderRowsOnly: true);
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
        return QueryPathPrefixScopes(songDb, folderPathPrefixes, excludedFolderPathPrefixes, lr2FolderRowsOnly: false);
    }

    private static IReadOnlyList<LR2SongDB.folder> QueryPathPrefixScopes(
        LR2SongDBExtended songDb,
        IEnumerable<string> folderPathPrefixes,
        IEnumerable<string> excludedFolderPathPrefixes,
        bool lr2FolderRowsOnly)
    {
        if (songDb == null)
        {
            return [];
        }

        List<PathPrefixRange> ranges = SubtractExcludedPrefixRanges(
            CreatePrefixRanges(folderPathPrefixes),
            CreatePrefixRanges(excludedFolderPathPrefixes));
        if (ranges.Count == 0)
        {
            return [];
        }

        BmsLibraryDbGateway.EnsureFolderLookupIndexes(songDb);
        string folderTable = SQLiteTable<LR2SongDB.folder>.GetTableName();
        string pathColumn = SQLiteTable<LR2SongDB.folder>.GetColumnName(row => row.path);
        string lr2FolderFilter = lr2FolderRowsOnly
            ? " AND " + pathColumn + " LIKE '%.lr2folder' COLLATE NOCASE"
            : string.Empty;
        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        foreach (PathPrefixRange range in ranges)
        {
            foreach (LR2SongDB.folder row in songDb.Query<LR2SongDB.folder>(
                "SELECT * FROM " + folderTable + " INDEXED BY " + BmsLibraryDbGateway.FolderPathNocaseIndexName
                + " WHERE " + pathColumn + " COLLATE NOCASE >= ?"
                + " AND " + pathColumn + " COLLATE NOCASE < ?"
                + lr2FolderFilter + ";",
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
            .OrderBy(range => range.Lower, StringComparer.OrdinalIgnoreCase)
            .ThenBy(range => range.Upper, StringComparer.OrdinalIgnoreCase)];
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
        var segments = new List<PathPrefixRange>(includeRanges ?? []);
        if (segments.Count == 0 || excludeRanges == null || excludeRanges.Count == 0)
        {
            return segments;
        }

        foreach (PathPrefixRange exclude in excludeRanges)
        {
            var next = new List<PathPrefixRange>();
            foreach (PathPrefixRange segment in segments)
            {
                if (ComparePathBounds(exclude.Upper, segment.Lower) <= 0
                    || ComparePathBounds(exclude.Lower, segment.Upper) >= 0)
                {
                    next.Add(segment);
                    continue;
                }

                if (ComparePathBounds(segment.Lower, exclude.Lower) < 0)
                {
                    string leftUpper = ComparePathBounds(exclude.Lower, segment.Upper) < 0
                        ? exclude.Lower
                        : segment.Upper;
                    if (ComparePathBounds(segment.Lower, leftUpper) < 0)
                    {
                        next.Add(new PathPrefixRange(segment.Lower, leftUpper));
                    }
                }

                if (ComparePathBounds(exclude.Upper, segment.Upper) < 0)
                {
                    string rightLower = ComparePathBounds(exclude.Upper, segment.Lower) > 0
                        ? exclude.Upper
                        : segment.Lower;
                    if (ComparePathBounds(rightLower, segment.Upper) < 0)
                    {
                        next.Add(new PathPrefixRange(rightLower, segment.Upper));
                    }
                }
            }

            segments = next;
            if (segments.Count == 0)
            {
                break;
            }
        }

        return MergePrefixRanges(segments);
    }

    private static int ComparePathBounds(string left, string right)
    {
        return StringComparer.OrdinalIgnoreCase.Compare(left, right);
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

    private readonly struct PathPrefixRange(string lower, string upper)
    {
        public string Lower { get; } = lower;

        public string Upper { get; } = upper;
    }
}
