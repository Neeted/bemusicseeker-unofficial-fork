using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2NormalFolderSyncScope(
    IReadOnlyList<string> chartPaths,
    IReadOnlyList<string> directoryPaths,
    IReadOnlyList<string> pruneScopeDirectories,
    IReadOnlyList<string> pruneExactDirectories)
{
    public static Lr2NormalFolderSyncScope Empty { get; } = new([], [], [], []);

    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

    public IReadOnlyList<string> DirectoryPaths { get; } = directoryPaths ?? [];

    public IReadOnlyList<string> PruneScopeDirectories { get; } = pruneScopeDirectories ?? [];

    public IReadOnlyList<string> PruneExactDirectories { get; } = pruneExactDirectories ?? [];
}

internal static class Lr2NormalFolderSyncScopeBuilder
{
    internal static Lr2NormalFolderSyncScope CreateForFileDiff(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> scannedBmsPaths,
        IEnumerable<BMSFile> addedBmsFiles,
        IEnumerable<string> deletedBmsPaths,
        IEnumerable<string> changedFolderInfoDirectoryPaths = null)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0)
        {
            return Lr2NormalFolderSyncScope.Empty;
        }

        var chartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneExactDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> deletedPathList = [.. deletedBmsPaths ?? []];
        HashSet<string> currentAncestorDirectories = deletedPathList.Count > 0
            ? CreateCurrentAncestorDirectorySet(scannedBmsPaths, roots)
            : [];
        foreach (BMSFile file in addedBmsFiles ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, file?.path, roots);
        }

        foreach (string deletedPath in deletedPathList)
        {
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, deletedPath, roots);
            AddEmptyAncestorExactScopesIfUnderAnyRoot(pruneExactDirectories, deletedPath, roots, currentAncestorDirectories);
        }

        foreach (string directoryPath in changedFolderInfoDirectoryPaths ?? [])
        {
            AddDirectoryIfUnderAnyRoot(directoryPaths, directoryPath, roots);
        }

        AddCurrentPathsUnderPruneScopes(chartPaths, scannedBmsPaths, pruneScopeDirectories);
        return CreateResult(chartPaths, directoryPaths, pruneScopeDirectories, pruneExactDirectories);
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
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneExactDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<LibraryChartPathChange> pathChangeList = [.. pathChanges ?? []];
        List<OwnedChartRemoveRequest> removeRequestList = [.. removeRequests ?? []];
        List<string> currentBmsPaths = [.. (currentBmsFiles ?? []).Select(file => file?.path)];
        bool needsEmptyAncestorCheck = pathChangeList.Any(pathChange => pathChange?.GetBmsStorageOwner() != null)
            || removeRequestList.Any(removeRequest => removeRequest?.Kind == ChartFileKind.Bms);
        HashSet<string> currentAncestorDirectories = needsEmptyAncestorCheck
            ? CreateCurrentAncestorDirectorySet(currentBmsPaths, roots)
            : [];
        foreach (BMSFile file in addedBmsFiles ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, file?.path, roots);
        }

        foreach (LibraryChartPathChange pathChange in pathChangeList)
        {
            if (pathChange?.GetBmsStorageOwner() == null)
            {
                continue;
            }

            AddIfUnderAnyRoot(chartPaths, pathChange.NewPath, roots);
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, pathChange.OldPath, roots);
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, pathChange.NewPath, roots);
            AddEmptyAncestorExactScopesIfUnderAnyRoot(pruneExactDirectories, pathChange.OldPath, roots, currentAncestorDirectories);
        }

        foreach (OwnedChartRemoveRequest removeRequest in removeRequestList)
        {
            if (removeRequest?.Kind != ChartFileKind.Bms)
            {
                continue;
            }

            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, removeRequest.Path, roots);
            AddEmptyAncestorExactScopesIfUnderAnyRoot(pruneExactDirectories, removeRequest.Path, roots, currentAncestorDirectories);
        }

        AddCurrentPathsUnderPruneScopes(
            chartPaths,
            currentBmsPaths,
            pruneScopeDirectories);
        return CreateResult(chartPaths, directoryPaths, pruneScopeDirectories, pruneExactDirectories);
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
        HashSet<string> directoryPaths,
        HashSet<string> pruneScopeDirectories,
        HashSet<string> pruneExactDirectories)
    {
        return new Lr2NormalFolderSyncScope(
            [.. (chartPaths ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (directoryPaths ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (pruneScopeDirectories ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (pruneExactDirectories ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]);
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

        var pruneScopeSet = new HashSet<string>(pruneScopeDirectories, StringComparer.OrdinalIgnoreCase);
        foreach (string path in chartPaths ?? [])
        {
            AddIfUnderAnyPruneScope(result, path, pruneScopeSet);
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

    private static void AddDirectoryIfUnderAnyRoot(HashSet<string> result, string directoryPath, IReadOnlyCollection<string> rootDirectories)
    {
        if (result == null || string.IsNullOrWhiteSpace(directoryPath) || rootDirectories == null || rootDirectories.Count == 0)
        {
            return;
        }

        string normalized = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        foreach (string root in rootDirectories)
        {
            if (Lr2FolderPath.IsSameOrDescendant(normalized, root))
            {
                result.Add(normalized);
                return;
            }
        }
    }

    private static void AddChartDirectoryScopeIfUnderAnyRoot(HashSet<string> result, string chartPath, IReadOnlyCollection<string> rootDirectories)
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
        if (string.IsNullOrWhiteSpace(rootDirectory)
            || string.Equals(directoryPath, rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        result.Add(directoryPath);
    }

    private static void AddEmptyAncestorExactScopesIfUnderAnyRoot(
        HashSet<string> result,
        string chartPath,
        IReadOnlyCollection<string> rootDirectories,
        ISet<string> currentAncestorDirectories)
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
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        string current = directoryPath;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendant(current, rootDirectory))
        {
            if (currentAncestorDirectories?.Contains(current) != true)
            {
                result.Add(current);
            }
            if (string.Equals(current, rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
        }
    }

    private static HashSet<string> CreateCurrentAncestorDirectorySet(IEnumerable<string> chartPaths, IReadOnlyCollection<string> rootDirectories)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rootDirectories == null || rootDirectories.Count == 0)
        {
            return result;
        }

        List<string> rootsForMatching = [.. rootDirectories.OrderByDescending(root => root.Length)];
        foreach (string chartPath in chartPaths ?? [])
        {
            string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
            if (string.IsNullOrWhiteSpace(chartDirectory))
            {
                continue;
            }

            string rootDirectory = rootsForMatching.FirstOrDefault(root => Lr2FolderPath.IsSameOrDescendant(chartDirectory, root));
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                continue;
            }

            string current = chartDirectory;
            while (!string.IsNullOrWhiteSpace(current)
                && Lr2FolderPath.IsSameOrDescendant(current, rootDirectory))
            {
                result.Add(current);
                if (string.Equals(current, rootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
            }
        }

        return result;
    }

    private static void AddIfUnderAnyPruneScope(HashSet<string> result, string chartPath, ISet<string> pruneScopeDirectories)
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

        string current = chartDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (pruneScopeDirectories?.Contains(current) == true)
            {
                result.Add(chartPath);
                return;
            }
            string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = parent;
        }
    }
}
