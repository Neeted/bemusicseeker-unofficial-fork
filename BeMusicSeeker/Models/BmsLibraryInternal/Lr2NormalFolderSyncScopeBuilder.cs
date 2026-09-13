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

/// <summary>
/// LR2 normal-folder 同期が必要とする現在 BMS の範囲 facts です。
/// query delegate は保持せず、捕捉時にコピーした件数と exact path だけを所有します。
/// </summary>
internal sealed class Lr2NormalFolderCurrentBmsLookup
{
    private readonly IReadOnlyDictionary<string, int> bmsCountsByDirectory;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> bmsPathsByDirectory;

    private Lr2NormalFolderCurrentBmsLookup(
        IReadOnlyDictionary<string, int> bmsCountsByDirectory,
        IReadOnlyDictionary<string, IReadOnlyList<string>> bmsPathsByDirectory,
        LibraryChartRefIndexBmsQueryDiagnostics queryDiagnostics)
    {
        this.bmsCountsByDirectory = bmsCountsByDirectory
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        this.bmsPathsByDirectory = bmsPathsByDirectory
            ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        QueryDiagnostics = queryDiagnostics ?? LibraryChartRefIndexBmsQueryDiagnostics.Empty;
    }

    internal static Lr2NormalFolderCurrentBmsLookup Empty { get; } =
        new(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            LibraryChartRefIndexBmsQueryDiagnostics.Empty);

    /// <summary>
    /// 既存 directory index の件数 query と範囲 query の結果をコピーして保持します。
    /// </summary>
    /// <param name="countFacts">directory ごとの現在 BMS ref 件数。</param>
    /// <param name="pathFacts">範囲 query が返した現在 BMS の exact path。</param>
    /// <param name="queryDiagnostics">捕捉に使った本番 index query の集約診断。</param>
    /// <returns>入力を所有しない不変 lookup。</returns>
    internal static Lr2NormalFolderCurrentBmsLookup CreateFromScopedFacts(
        IEnumerable<KeyValuePair<string, int>> countFacts,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> pathFacts,
        LibraryChartRefIndexBmsQueryDiagnostics queryDiagnostics = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> fact in countFacts ?? new Dictionary<string, int>())
        {
            string directory = Lr2FolderPath.NormalizeDirectoryPath(fact.Key);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                counts[directory] = Math.Max(0, fact.Value);
            }
        }

        var paths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var pathKeysByDirectory = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var derivedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var derivedPaths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var derivedPathKeysByDirectory = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IReadOnlyList<string>> fact in pathFacts
            ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            string directory = Lr2FolderPath.NormalizeDirectoryPath(fact.Key);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (!paths.TryGetValue(directory, out List<string> indexedPaths))
            {
                indexedPaths = [];
                paths.Add(directory, indexedPaths);
                pathKeysByDirectory.Add(directory, new HashSet<string>(StringComparer.Ordinal));
            }

            foreach (string path in fact.Value ?? [])
            {
                if (string.IsNullOrWhiteSpace(path)
                    || !pathKeysByDirectory[directory].Add(path))
                {
                    continue;
                }
                indexedPaths.Add(path);
                AddPathToFacts(
                    path,
                    derivedCounts,
                    derivedPaths,
                    derivedPathKeysByDirectory);
            }
        }

        // 範囲 query は mutation directory を起点にする一方、最終 receipt は
        // 譜面の深い directory を要求することがある。返却済み path から局所
        // の子 facts を組み立て、別の catalog traversal なしで exact な
        // nested scope を projection へ引き継ぐ。
        foreach (KeyValuePair<string, List<string>> entry in derivedPaths)
        {
            if (!paths.TryGetValue(entry.Key, out List<string> indexedPaths))
            {
                indexedPaths = [];
                paths.Add(entry.Key, indexedPaths);
                pathKeysByDirectory.Add(entry.Key, new HashSet<string>(StringComparer.Ordinal));
            }

            foreach (string path in entry.Value)
            {
                if (pathKeysByDirectory[entry.Key].Add(path))
                {
                    indexedPaths.Add(path);
                }
            }
        }
        foreach (KeyValuePair<string, int> entry in derivedCounts)
        {
            if (!counts.ContainsKey(entry.Key))
            {
                counts.Add(entry.Key, entry.Value);
            }
        }

        return new Lr2NormalFolderCurrentBmsLookup(
            counts,
            paths.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)CopyExactPaths(entry.Value),
                StringComparer.OrdinalIgnoreCase),
            queryDiagnostics);
    }

    /// <summary>
    /// full scan の祖先存在判定を不変 facts として保持します。
    /// </summary>
    internal static Lr2NormalFolderCurrentBmsLookup CreateFromDirectorySet(
        IEnumerable<string> directories)
    {
        return CreateFromScopedFacts(
            (directories ?? [])
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => new KeyValuePair<string, int>(directory, 1)),
            []);
    }

    /// <summary>
    /// 自動 rename の deferred catalog apply に合わせて、捕捉済み facts を old/new path へ投影します。
    /// old exact path が捕捉範囲に存在しない変更は追加せず、欠落を空集合へ変換しません。
    /// </summary>
    internal Lr2NormalFolderCurrentBmsLookup ProjectPathChanges(
        IEnumerable<Lr2NormalFolderPathChange> pathChanges)
    {
        var counts = new Dictionary<string, int>(bmsCountsByDirectory, StringComparer.OrdinalIgnoreCase);
        var paths = bmsPathsByDirectory.ToDictionary(
            entry => entry.Key,
            entry => new List<string>(entry.Value ?? []),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> initiallyKnownPaths = new(
            paths.Values.SelectMany(value => value ?? []),
            StringComparer.Ordinal);
        var appliedOldPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (Lr2NormalFolderPathChange pathChange in pathChanges ?? [])
        {
            if (pathChange == null
                || string.IsNullOrWhiteSpace(pathChange.OldPath)
                || string.IsNullOrWhiteSpace(pathChange.NewPath)
                || !initiallyKnownPaths.Contains(pathChange.OldPath)
                || !appliedOldPaths.Add(pathChange.OldPath))
            {
                continue;
            }

            EnsurePathFactDirectories(paths, counts, pathChange.OldPath);
            EnsurePathFactDirectories(paths, counts, pathChange.NewPath);
            AdjustAncestorCounts(counts, pathChange.OldPath, -1);
            AdjustAncestorCounts(counts, pathChange.NewPath, 1);
            RemoveExactPath(paths, pathChange.OldPath);
            AddExactPath(paths, pathChange.NewPath);
        }

        return CreateFromFacts(
            counts,
            paths.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value,
                StringComparer.OrdinalIgnoreCase),
            QueryDiagnostics);
    }

    /// <summary>指定 directory に現在 BMS が存在するかを、捕捉済み件数で判定します。</summary>
    internal bool HasBmsChartUnderDirectory(string directoryPath)
    {
        string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        return !string.IsNullOrWhiteSpace(normalizedDirectory)
            && bmsCountsByDirectory.TryGetValue(normalizedDirectory, out int count)
            && count > 0;
    }

    /// <summary>指定範囲の現在 BMS exact path を、捕捉済み path facts から返します。</summary>
    internal IReadOnlyList<string> GetBmsChartPathsUnderDirectory(string directoryPath)
    {
        string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        return !string.IsNullOrWhiteSpace(normalizedDirectory)
            && bmsPathsByDirectory.TryGetValue(normalizedDirectory, out IReadOnlyList<string> paths)
                ? paths
                : [];
    }

    /// <summary>この lookup の捕捉に使った本番 index query の集約診断です。</summary>
    internal LibraryChartRefIndexBmsQueryDiagnostics QueryDiagnostics { get; }

    private static Lr2NormalFolderCurrentBmsLookup CreateFromFacts(
        IReadOnlyDictionary<string, int> countFacts,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pathFacts,
        LibraryChartRefIndexBmsQueryDiagnostics queryDiagnostics = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, int> fact in countFacts
            ?? new Dictionary<string, int>())
        {
            string directory = Lr2FolderPath.NormalizeDirectoryPath(fact.Key);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                counts[directory] = Math.Max(0, fact.Value);
            }
        }

        var paths = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IReadOnlyList<string>> fact in pathFacts
            ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            string directory = Lr2FolderPath.NormalizeDirectoryPath(fact.Key);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            if (!paths.TryGetValue(directory, out List<string> indexedPaths))
            {
                indexedPaths = [];
                paths.Add(directory, indexedPaths);
            }
            foreach (string path in CopyExactPaths(fact.Value))
            {
                if (!indexedPaths.Contains(path, StringComparer.Ordinal))
                {
                    indexedPaths.Add(path);
                }
            }
        }

        return new Lr2NormalFolderCurrentBmsLookup(
            counts,
            paths.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)CopyExactPaths(entry.Value),
                StringComparer.OrdinalIgnoreCase),
            queryDiagnostics);
    }

    private static void AddPathToFacts(
        string chartPath,
        IDictionary<string, int> counts,
        IDictionary<string, List<string>> paths,
        IDictionary<string, HashSet<string>> pathKeysByDirectory)
    {
        string currentDirectory = Lr2FolderPath.NormalizeDirectoryPath(
            Lr2FolderPath.SafeGetDirectoryName(chartPath));
        while (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            if (!paths.TryGetValue(currentDirectory, out List<string> indexedPaths))
            {
                indexedPaths = [];
                paths.Add(currentDirectory, indexedPaths);
                pathKeysByDirectory.Add(currentDirectory, new HashSet<string>(StringComparer.Ordinal));
            }
            if (pathKeysByDirectory[currentDirectory].Add(chartPath))
            {
                indexedPaths.Add(chartPath);
                counts[currentDirectory] = counts.TryGetValue(currentDirectory, out int count)
                    ? count + 1
                    : 1;
            }

            string parentDirectory = Lr2FolderPath.SafeGetParentNormalizedDirectory(currentDirectory);
            if (string.IsNullOrWhiteSpace(parentDirectory)
                || string.Equals(parentDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            currentDirectory = parentDirectory;
        }
    }

    private static IReadOnlyList<string> CopyExactPaths(IEnumerable<string> source)
    {
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string path in source ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path) && unique.Add(path))
            {
                result.Add(path);
            }
        }
        return Array.AsReadOnly(result.ToArray());
    }

    private static void RemoveExactPath(
        IDictionary<string, List<string>> paths,
        string path)
    {
        foreach (List<string> indexedPaths in paths.Values)
        {
            indexedPaths.RemoveAll(candidate => string.Equals(candidate, path, StringComparison.Ordinal));
        }
    }

    private static void AddExactPath(
        IDictionary<string, List<string>> paths,
        string path)
    {
        string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(path));
        if (string.IsNullOrWhiteSpace(chartDirectory))
        {
            return;
        }

        foreach (KeyValuePair<string, List<string>> entry in paths)
        {
            if (Lr2FolderPath.IsSameOrDescendant(chartDirectory, entry.Key)
                && !entry.Value.Contains(path, StringComparer.Ordinal))
            {
                entry.Value.Add(path);
            }
        }
    }

    private static void EnsurePathFactDirectories(
        IDictionary<string, List<string>> paths,
        IDictionary<string, int> counts,
        string chartPath)
    {
        string directory = Lr2FolderPath.NormalizeDirectoryPath(
            Lr2FolderPath.SafeGetDirectoryName(chartPath));
        bool first = true;
        while (!string.IsNullOrWhiteSpace(directory))
        {
            bool hadFacts = paths.ContainsKey(directory) || counts.ContainsKey(directory);
            if (!paths.ContainsKey(directory))
            {
                paths[directory] = [];
            }
            if (!counts.ContainsKey(directory))
            {
                counts[directory] = 0;
            }

            if (!first && hadFacts)
            {
                break;
            }

            string parentDirectory = Lr2FolderPath.SafeGetParentNormalizedDirectory(directory);
            if (string.IsNullOrWhiteSpace(parentDirectory)
                || string.Equals(parentDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            directory = parentDirectory;
            first = false;
        }
    }

    private static void AdjustAncestorCounts(
        IDictionary<string, int> counts,
        string chartPath,
        int delta)
    {
        string currentDirectory = Lr2FolderPath.NormalizeDirectoryPath(
            Lr2FolderPath.SafeGetDirectoryName(chartPath));
        while (!string.IsNullOrWhiteSpace(currentDirectory))
        {
            if (counts.TryGetValue(currentDirectory, out int count))
            {
                int adjusted = Math.Max(0, count + delta);
                counts[currentDirectory] = adjusted;
            }

            string parentDirectory = Lr2FolderPath.SafeGetParentNormalizedDirectory(currentDirectory);
            if (string.IsNullOrWhiteSpace(parentDirectory)
                || string.Equals(parentDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            currentDirectory = parentDirectory;
        }
    }
}

/// <summary>
/// 局所 LR2 捕捉で訪問する directory query の範囲です。
/// path query は対象 subtree のみ、count query は必要な祖先までを表します。
/// </summary>
/// <param name="pathQueryDirectories">exact path を取得する subtree の起点。</param>
/// <param name="countQueryDirectories">BMS 件数を取得する directory と祖先。</param>
internal sealed class Lr2NormalFolderBmsQueryScope(
    IReadOnlyList<string> pathQueryDirectories,
    IReadOnlyList<string> countQueryDirectories)
{
    internal static Lr2NormalFolderBmsQueryScope Empty { get; } = new([], []);

    /// <summary>exact path query の起点 directory。</summary>
    internal IReadOnlyList<string> PathQueryDirectories { get; } = pathQueryDirectories ?? [];

    /// <summary>BMS count query の対象 directory。</summary>
    internal IReadOnlyList<string> CountQueryDirectories { get; } = countQueryDirectories ?? [];
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

        var chartPaths = new HashSet<string>(StringComparer.Ordinal);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneExactDirectories = new HashSet<string>(StringComparer.Ordinal);
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

    /// <summary>
    /// catalog mutation の現在 BMS facts を捕捉する directory query 範囲を作成します。
    /// path query は old/new/remove の直接 directory、count query は登録 root までの祖先です。
    /// </summary>
    internal static Lr2NormalFolderBmsQueryScope CreateCatalogMutationBmsQueryScope(
        IEnumerable<string> rootDirectories,
        Lr2NormalFolderCatalogMutationReceipt receipt)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0 || receipt?.RequiresCurrentBmsChartSnapshot != true)
        {
            return Lr2NormalFolderBmsQueryScope.Empty;
        }

        var pathQueryDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countQueryDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Lr2NormalFolderPathChange pathChange in receipt.PathChanges ?? [])
        {
            AddBmsQueryDirectory(
                pathQueryDirectories,
                countQueryDirectories,
                pathChange?.OldPath,
                roots);
            AddBmsQueryDirectory(
                pathQueryDirectories,
                countQueryDirectories,
                pathChange?.NewPath,
                roots);
        }
        foreach (string removedChartPath in receipt.RemovedBmsChartPaths ?? [])
        {
            AddBmsQueryDirectory(
                pathQueryDirectories,
                countQueryDirectories,
                removedChartPath,
                roots);
        }

        return CreateBmsQueryScope(pathQueryDirectories, countQueryDirectories);
    }

    /// <summary>
    /// 自動 rename の deferred apply 前に必要な source/destination query 範囲を作成します。
    /// </summary>
    internal static Lr2NormalFolderBmsQueryScope CreateAutoRenameBmsQueryScope(
        IEnumerable<string> rootDirectories,
        IEnumerable<FolderAutoRenamePlan> plans)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0)
        {
            return Lr2NormalFolderBmsQueryScope.Empty;
        }

        var pathQueryDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var countQueryDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FolderAutoRenamePlan plan in plans ?? [])
        {
            AddBmsQueryDirectory(
                pathQueryDirectories,
                countQueryDirectories,
                plan?.SourceDirectory,
                roots,
                inputIsDirectory: true);
            AddBmsQueryDirectory(
                pathQueryDirectories,
                countQueryDirectories,
                plan?.DestinationDirectory,
                roots,
                inputIsDirectory: true);
        }

        return CreateBmsQueryScope(pathQueryDirectories, countQueryDirectories);
    }

    private static Lr2NormalFolderBmsQueryScope CreateBmsQueryScope(
        IEnumerable<string> pathQueryDirectories,
        IEnumerable<string> countQueryDirectories)
    {
        return new Lr2NormalFolderBmsQueryScope(
            [.. (pathQueryDirectories ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (countQueryDirectories ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)]);
    }

    private static void AddBmsQueryDirectory(
        ISet<string> pathQueryDirectories,
        ISet<string> countQueryDirectories,
        string path,
        IReadOnlyCollection<string> roots,
        bool inputIsDirectory = false)
    {
        if (pathQueryDirectories == null || countQueryDirectories == null || roots == null || roots.Count == 0)
        {
            return;
        }

        string directory = inputIsDirectory
            ? Lr2FolderPath.NormalizeDirectoryPath(path)
            : Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(path));
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        string rootDirectory = roots
            .Where(root => Lr2FolderPath.IsSameOrDescendantNormalized(directory, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            return;
        }

        string current = directory;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendantNormalized(current, rootDirectory))
        {
            countQueryDirectories.Add(current);
            if (string.Equals(current, rootDirectory, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
        }

        if (!string.Equals(directory, rootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            pathQueryDirectories.Add(directory);
        }
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
            [.. (chartPaths ?? [])
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)],
            [.. (directoryPaths ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (pruneScopeDirectories ?? []).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            [.. (pruneExactDirectories ?? []).OrderBy(path => path, StringComparer.Ordinal)]);
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
            : Lr2NormalFolderCurrentBmsLookup.CreateFromDirectorySet(currentAncestorDirectories);
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
