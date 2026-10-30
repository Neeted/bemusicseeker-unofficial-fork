using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>生成集合と保存済みrawキーを完全一致で比較します。物理root包含は大小文字を区別せず、局所exact整理指定はOrdinalを保ちます。</summary>
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
        var existingNormalRowsByKey = existingRowList
            .Where(IsNormalDirectoryRow).ToDictionary(row => row.path, StringComparer.Ordinal);
        var nonNormalKeys = new HashSet<string>(existingRowList
            .Where(row => row != null && !IsNormalDirectoryRow(row)).Select(row => row.path), StringComparer.Ordinal);
        var generatedKeys = new HashSet<string>(StringComparer.Ordinal);
        var upserts = new List<LR2SongDB.folder>();
        bool allowPrune = generation.SkippedMissingMetadataCount == 0;
        foreach (LR2SongDB.folder row in generation.Rows ?? [])
        {
            string path = row?.path;
            if (string.IsNullOrWhiteSpace(path) || !generatedKeys.Add(path) || nonNormalKeys.Contains(path))
            {
                continue;
            }
            if (!existingNormalRowsByKey.TryGetValue(path, out LR2SongDB.folder existing)
                || !AreEquivalent(row, existing))
            {
                upserts.Add(row);
            }
        }

        List<string> roots = NormalizeRoots(rootDirectories);
        List<string> pruneScopes = pruneScopeDirectories == null
            ? roots
            : NormalizeRoots(pruneScopeDirectories);
        var pruneExactPaths = new HashSet<string>(
            (pruneExactDirectories ?? []).Select(Lr2FolderPath.ToFolderPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.Ordinal);
        var deletes = new List<string>();
        foreach (LR2SongDB.folder row in existingRowList)
        {
            // 未補正relativeをCWDやdrive-currentで補完して整理範囲へ取り込みません。
            string directory = row?.path != null && Path.IsPathFullyQualified(row.path)
                ? Lr2FolderPath.NormalizeDirectoryPath(row.path) : null;
            if (string.IsNullOrWhiteSpace(directory)
                || !IsNormalDirectoryRow(row)
                || generatedKeys.Contains(row.path)
                || (!IsInScope(directory, pruneScopes) && !pruneExactPaths.Contains(row.path)))
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

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool IsInScope(string folderPath, IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            if (Lr2FolderPath.IsSameOrDescendantNormalized(folderPath, root))
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
