using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2NormalFolderSyncScope(
    IReadOnlyList<string> chartPaths,
    IReadOnlyList<string> pruneScopeDirectories)
{
    public static Lr2NormalFolderSyncScope Empty { get; } = new([], []);

    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

    public IReadOnlyList<string> PruneScopeDirectories { get; } = pruneScopeDirectories ?? [];
}

internal static class Lr2NormalFolderSyncScopeBuilder
{
    internal static Lr2NormalFolderSyncScope CreateForFileDiff(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> scannedBmsPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<string> deletedBmsPaths)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0)
        {
            return Lr2NormalFolderSyncScope.Empty;
        }

        var chartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile file in addedBmsFiles ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, file?.path, roots);
        }

        foreach (string deletedPath in deletedBmsPaths ?? [])
        {
            AddTopLevelDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, deletedPath, roots);
        }

        AddCurrentPathsUnderPruneScopes(chartPaths, scannedBmsPaths, pruneScopeDirectories);
        return CreateResult(chartPaths, pruneScopeDirectories);
    }

    internal static Lr2NormalFolderSyncScope CreateForStorageMutation(
        IEnumerable<string> rootDirectories,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<LibraryChartPathChange> pathChanges,
        IEnumerable<OwnedChartRemoveRequest> removeRequests,
        IEnumerable<BMSFile> currentBmsFiles)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0)
        {
            return Lr2NormalFolderSyncScope.Empty;
        }

        var chartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BMSFile file in addedBmsFiles ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, file?.path, roots);
        }

        foreach (LibraryChartPathChange pathChange in pathChanges ?? [])
        {
            if (pathChange?.GetBmsStorageOwner() == null)
            {
                continue;
            }

            AddIfUnderAnyRoot(chartPaths, pathChange.NewPath, roots);
            AddTopLevelDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, pathChange.OldPath, roots);
            AddTopLevelDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, pathChange.NewPath, roots);
        }

        foreach (OwnedChartRemoveRequest removeRequest in removeRequests ?? [])
        {
            if (removeRequest?.Kind != ChartFileKind.Bms)
            {
                continue;
            }

            AddTopLevelDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, removeRequest.Path, roots);
        }

        AddCurrentPathsUnderPruneScopes(
            chartPaths,
            (currentBmsFiles ?? []).Select(file => file?.path),
            pruneScopeDirectories);
        return CreateResult(chartPaths, pruneScopeDirectories);
    }

    private static List<string> NormalizeRoots(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static Lr2NormalFolderSyncScope CreateResult(
        HashSet<string> chartPaths,
        HashSet<string> pruneScopeDirectories)
    {
        return new Lr2NormalFolderSyncScope(
            [.. (chartPaths ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (pruneScopeDirectories ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]);
    }

    private static void AddCurrentPathsUnderPruneScopes(
        HashSet<string> result,
        IEnumerable<string> chartPaths,
        IReadOnlyCollection<string> pruneScopeDirectories)
    {
        if (pruneScopeDirectories == null || pruneScopeDirectories.Count == 0)
        {
            return;
        }

        foreach (string path in chartPaths ?? [])
        {
            AddIfUnderAnyPruneScope(result, path, pruneScopeDirectories);
        }
    }

    private static void AddIfUnderAnyRoot(HashSet<string> result, string chartPath, IReadOnlyCollection<string> rootDirectories)
    {
        if (result == null || string.IsNullOrWhiteSpace(chartPath) || rootDirectories == null || rootDirectories.Count == 0)
        {
            return;
        }

        string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return;
        }

        foreach (string root in rootDirectories)
        {
            if (Lr2FolderPath.IsSameOrDescendant(chartDirectory, root))
            {
                result.Add(chartPath);
                return;
            }
        }
    }

    private static void AddTopLevelDirectoryScopeIfUnderAnyRoot(HashSet<string> result, string chartPath, IReadOnlyCollection<string> rootDirectories)
    {
        if (result == null || string.IsNullOrWhiteSpace(chartPath) || rootDirectories == null || rootDirectories.Count == 0)
        {
            return;
        }

        string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        string rootDirectory = rootDirectories
            .Where(root => Lr2FolderPath.IsSameOrDescendant(directoryPath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.Equals(directoryPath, rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string current = directoryPath;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(current);
                return;
            }
            current = parent;
        }
    }

    private static void AddIfUnderAnyPruneScope(HashSet<string> result, string chartPath, IEnumerable<string> pruneScopeDirectories)
    {
        if (result == null || string.IsNullOrWhiteSpace(chartPath))
        {
            return;
        }

        string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return;
        }

        foreach (string pruneScopeDirectory in pruneScopeDirectories ?? [])
        {
            if (Lr2FolderPath.IsSameOrDescendant(chartDirectory, pruneScopeDirectory))
            {
                result.Add(chartPath);
                return;
            }
        }
    }
}
