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

internal sealed class Lr2NormalFolderCurrentBmsLookup(
    Func<string, bool> hasBmsChartUnderDirectory,
    Func<string, IReadOnlyList<string>> getBmsChartPathsUnderDirectory)
{
    internal static Lr2NormalFolderCurrentBmsLookup Empty { get; } = new(_ => false, _ => []);

    internal static Lr2NormalFolderCurrentBmsLookup CreateFromChartPaths(IEnumerable<string> chartPaths)
    {
        var mutableIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string chartPath in chartPaths ?? [])
        {
            string currentDirectory = Lr2FolderPath.NormalizeDirectoryPath(
                Lr2FolderPath.SafeGetDirectoryName(chartPath));
            while (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                if (!mutableIndex.TryGetValue(currentDirectory, out List<string> indexedPaths))
                {
                    indexedPaths = [];
                    mutableIndex.Add(currentDirectory, indexedPaths);
                }
                indexedPaths.Add(chartPath);

                string parentDirectory = Lr2FolderPath.SafeGetParentNormalizedDirectory(currentDirectory);
                if (string.IsNullOrWhiteSpace(parentDirectory)
                    || string.Equals(parentDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                currentDirectory = parentDirectory;
            }
        }

        var index = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, List<string>> entry in mutableIndex)
        {
            index[entry.Key] = Array.AsReadOnly(entry.Value.ToArray());
        }

        return new Lr2NormalFolderCurrentBmsLookup(
            directoryPath =>
            {
                string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
                return !string.IsNullOrWhiteSpace(normalizedDirectory)
                    && index.ContainsKey(normalizedDirectory);
            },
            directoryPath =>
            {
                string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
                return !string.IsNullOrWhiteSpace(normalizedDirectory)
                    && index.TryGetValue(normalizedDirectory, out IReadOnlyList<string> paths)
                    ? paths
                    : [];
            });
    }

    internal bool HasBmsChartUnderDirectory(string directoryPath)
    {
        return !string.IsNullOrWhiteSpace(directoryPath)
            && hasBmsChartUnderDirectory?.Invoke(directoryPath) == true;
    }

    internal IReadOnlyList<string> GetBmsChartPathsUnderDirectory(string directoryPath)
    {
        return string.IsNullOrWhiteSpace(directoryPath)
            ? []
            : getBmsChartPathsUnderDirectory?.Invoke(directoryPath) ?? [];
    }
}

internal static class Lr2NormalFolderSyncScopeBuilder
{
    internal static Lr2NormalFolderSyncScope CreateForFileDiff(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> scannedBmsPaths,
        IEnumerable<string> changedDirectoryPaths = null,
        IEnumerable<string> pruneScopeDirectoryPaths = null)
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

        foreach (string directoryPath in changedDirectoryPaths ?? [])
        {
            AddDirectoryIfUnderAnyRoot(directoryPaths, directoryPath, roots);
        }
        foreach (string directoryPath in pruneScopeDirectoryPaths ?? [])
        {
            AddDirectoryIfUnderAnyRoot(pruneScopeDirectories, directoryPath, roots);
        }
        HashSet<string> currentAncestorDirectories = pruneScopeDirectories.Count > 0
            ? CreateCurrentAncestorDirectorySet(scannedBmsPaths, roots)
            : [];
        foreach (string pruneScopeDirectory in pruneScopeDirectories)
        {
            AddEmptyAncestorExactScopesForDirectoryIfUnderAnyRoot(pruneExactDirectories, pruneScopeDirectory, roots, currentAncestorDirectories);
        }

        AddCurrentPathsUnderPruneScopes(chartPaths, scannedBmsPaths, pruneScopeDirectories);
        return CreateResult(chartPaths, directoryPaths, pruneScopeDirectories, pruneExactDirectories);
    }

    internal static Lr2NormalFolderSyncScope CreateForCatalogMutation(
        IEnumerable<string> rootDirectories,
        Lr2NormalFolderCatalogMutationReceipt receipt)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0 || receipt?.HasBmsMutation != true)
        {
            return Lr2NormalFolderSyncScope.Empty;
        }

        var chartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneExactDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Lr2NormalFolderCurrentBmsLookup currentBmsLookup = receipt.CreateCurrentBmsLookup();

        foreach (string addedChartPath in receipt.AddedBmsChartPaths ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, addedChartPath, roots);
        }

        foreach (Lr2NormalFolderPathChange pathChange in receipt.PathChanges ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, pathChange.NewPath, roots);
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, pathChange.OldPath, roots);
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, pathChange.NewPath, roots);
            AddEmptyAncestorExactScopesIfUnderAnyRoot(
                pruneExactDirectories,
                pathChange.OldPath,
                roots,
                currentBmsLookup);
        }

        foreach (string removedChartPath in receipt.RemovedBmsChartPaths ?? [])
        {
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, removedChartPath, roots);
            AddEmptyAncestorExactScopesIfUnderAnyRoot(
                pruneExactDirectories,
                removedChartPath,
                roots,
                currentBmsLookup);
        }

        AddCurrentPathsUnderPruneScopes(chartPaths, currentBmsLookup, pruneScopeDirectories);
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
        Lr2NormalFolderCurrentBmsLookup currentBmsLookup,
        IReadOnlyCollection<string> pruneScopeDirectories)
    {
        if (pruneScopeDirectories == null || pruneScopeDirectories.Count == 0)
        {
            return;
        }

        var pruneScopeSet = new HashSet<string>(pruneScopeDirectories, StringComparer.OrdinalIgnoreCase);
        foreach (string pruneScopeDirectory in pruneScopeDirectories)
        {
            foreach (string path in currentBmsLookup?.GetBmsChartPathsUnderDirectory(pruneScopeDirectory) ?? [])
            {
                AddIfUnderAnyPruneScope(result, path, pruneScopeSet);
            }
        }
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
        Lr2NormalFolderCurrentBmsLookup currentBmsLookup)
    {
        if (result == null || string.IsNullOrWhiteSpace(chartPath) || rootDirectories == null || rootDirectories.Count == 0)
        {
            return;
        }

        string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
        AddEmptyAncestorExactScopesForDirectoryIfUnderAnyRoot(result, directoryPath, rootDirectories, currentBmsLookup);
    }

    private static void AddEmptyAncestorExactScopesForDirectoryIfUnderAnyRoot(
        HashSet<string> result,
        string directoryPath,
        IReadOnlyCollection<string> rootDirectories,
        Lr2NormalFolderCurrentBmsLookup currentBmsLookup)
    {
        if (result == null || string.IsNullOrWhiteSpace(directoryPath) || rootDirectories == null || rootDirectories.Count == 0)
        {
            return;
        }

        string normalizedDirectoryPath = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        if (string.IsNullOrWhiteSpace(normalizedDirectoryPath))
        {
            return;
        }

        string rootDirectory = rootDirectories
            .Where(root => Lr2FolderPath.IsSameOrDescendant(normalizedDirectoryPath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        string current = normalizedDirectoryPath;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendant(current, rootDirectory))
        {
            if (currentBmsLookup?.HasBmsChartUnderDirectory(current) != true)
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

    private static void AddEmptyAncestorExactScopesForDirectoryIfUnderAnyRoot(
        HashSet<string> result,
        string directoryPath,
        IReadOnlyCollection<string> rootDirectories,
        ISet<string> currentAncestorDirectories)
    {
        Lr2NormalFolderCurrentBmsLookup lookup = currentAncestorDirectories == null
            ? Lr2NormalFolderCurrentBmsLookup.Empty
            : new Lr2NormalFolderCurrentBmsLookup(
                directory => currentAncestorDirectories.Contains(Lr2FolderPath.NormalizeDirectoryPath(directory)),
                _ => []);
        AddEmptyAncestorExactScopesForDirectoryIfUnderAnyRoot(result, directoryPath, rootDirectories, lookup);
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
