using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2FolderGenerationSyncPlan(
    IReadOnlyList<LR2SongDB.folder> upsertRows,
    IReadOnlyList<string> deletePaths)
{
    public IReadOnlyList<LR2SongDB.folder> UpsertRows { get; } = upsertRows;

    public IReadOnlyList<string> DeletePaths { get; } = deletePaths;

    public bool HasChanges => UpsertRows.Count > 0 || DeletePaths.Count > 0;
}

internal static class Lr2FolderGenerationScopePlanner
{
    private const int NormalFolderType = 1;

    private static readonly IEqualityComparer<string> PathComparer = StringComparer.OrdinalIgnoreCase;

    internal static Lr2FolderGenerationSyncPlan PlanNormalDirectorySync(
        Lr2FolderGenerationResult generation,
        IEnumerable<LR2SongDB.folder> existingRows,
        IEnumerable<string> rootDirectories,
        IEnumerable<string> pruneScopeDirectories = null,
        IEnumerable<string> pruneExactDirectories = null)
    {
        if (generation == null)
        {
            return new Lr2FolderGenerationSyncPlan([], []);
        }

        List<LR2SongDB.folder> existingRowList = [.. existingRows ?? []];
        Dictionary<string, List<LR2SongDB.folder>> existingNormalRowsByKey = CreateExistingNormalRowMap(existingRowList);
        HashSet<string> nonNormalKeys = CreateNonNormalKeySet(existingRowList);
        var generatedKeys = new HashSet<string>(PathComparer);
        var generatedExactPaths = new HashSet<string>(StringComparer.Ordinal);
        var upserts = new List<LR2SongDB.folder>();
        bool allowPrune = generation.SkippedMissingMetadataCount == 0;
        foreach (LR2SongDB.folder row in generation.Rows ?? [])
        {
            string path = Lr2FolderPath.ToFolderPath(row?.path);
            if (string.IsNullOrWhiteSpace(path) || generatedKeys.Contains(path) || nonNormalKeys.Contains(path))
            {
                continue;
            }

            generatedKeys.Add(path);
            generatedExactPaths.Add(row.path);
            LR2SongDB.folder exactExisting = null;
            if (existingNormalRowsByKey.TryGetValue(path, out List<LR2SongDB.folder> existingCandidates))
            {
                exactExisting = existingCandidates.FirstOrDefault(candidate => string.Equals(candidate.path, row.path, StringComparison.Ordinal));
            }

            if (exactExisting == null && existingCandidates?.Count > 0 && !allowPrune)
            {
                continue;
            }

            if (exactExisting == null || !AreEquivalent(row, exactExisting))
            {
                upserts.Add(row);
            }
        }

        List<string> roots = NormalizeRoots(rootDirectories);
        List<string> pruneScopes = pruneScopeDirectories == null
            ? roots
            : NormalizeRoots(pruneScopeDirectories);
        var pruneExactPaths = new HashSet<string>(
            NormalizeExactDirectories(pruneExactDirectories)
                .Select(Lr2FolderPath.ToFolderPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.Ordinal);
        var deletes = new List<string>();
        foreach (LR2SongDB.folder row in existingRowList)
        {
            string path = Lr2FolderPath.ToFolderPath(row?.path);
            if (string.IsNullOrWhiteSpace(path)
                || !IsNormalDirectoryRow(row)
                || generatedExactPaths.Contains(row.path)
                || (!IsInScope(path, pruneScopes) && !pruneExactPaths.Contains(path)))
            {
                continue;
            }
            if (!allowPrune)
            {
                continue;
            }
            deletes.Add(row.path);
        }

        return new Lr2FolderGenerationSyncPlan(upserts, deletes);
    }

    private static Dictionary<string, List<LR2SongDB.folder>> CreateExistingNormalRowMap(IEnumerable<LR2SongDB.folder> existingRows)
    {
        var result = new Dictionary<string, List<LR2SongDB.folder>>(PathComparer);
        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            if (!IsNormalDirectoryRow(row))
            {
                continue;
            }

            string path = Lr2FolderPath.ToFolderPath(row?.path);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }
            if (!result.TryGetValue(path, out List<LR2SongDB.folder> rows))
            {
                rows = [];
                result.Add(path, rows);
            }
            rows.Add(row);
        }
        return result;
    }

    private static HashSet<string> CreateNonNormalKeySet(IEnumerable<LR2SongDB.folder> existingRows)
    {
        var result = new HashSet<string>(PathComparer);
        foreach (LR2SongDB.folder row in existingRows ?? [])
        {
            if (row == null || IsNormalDirectoryRow(row))
            {
                continue;
            }

            string path = Lr2FolderPath.ToFolderPath(row.path);
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(path);
            }
        }
        return result;
    }

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> NormalizeExactDirectories(IEnumerable<string> directories)
    {
        return [.. (directories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)];
    }

    private static bool IsInScope(string folderPath, IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            if (Lr2FolderPath.IsSameOrDescendant(folderPath, root))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsNormalDirectoryRow(LR2SongDB.folder row)
    {
        return row?.type == NormalFolderType
            && !string.IsNullOrWhiteSpace(row.path);
    }

    private static bool AreEquivalent(LR2SongDB.folder expected, LR2SongDB.folder existing)
    {
        return string.Equals(expected?.title, existing?.title, StringComparison.Ordinal)
            && string.Equals(expected?.subtitle, existing?.subtitle, StringComparison.Ordinal)
            && string.Equals(expected?.category, existing?.category, StringComparison.Ordinal)
            && string.Equals(expected?.info_a, existing?.info_a, StringComparison.Ordinal)
            && string.Equals(expected?.info_b, existing?.info_b, StringComparison.Ordinal)
            && string.Equals(expected?.command, existing?.command, StringComparison.Ordinal)
            && string.Equals(expected?.path, existing?.path, StringComparison.Ordinal)
            && expected?.type == existing?.type
            && string.Equals(expected?.banner, existing?.banner, StringComparison.Ordinal)
            && string.Equals(expected?.parent, existing?.parent, StringComparison.OrdinalIgnoreCase)
            && expected?.date == existing?.date
            && expected?.max == existing?.max
            && expected?.adddate == existing?.adddate;
    }
}
