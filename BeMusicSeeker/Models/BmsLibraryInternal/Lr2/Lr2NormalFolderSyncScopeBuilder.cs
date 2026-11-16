using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2NormalFolderSyncScope(
    IReadOnlyList<string> chartPaths,
    IReadOnlyList<string> directoryPaths,
    IReadOnlyList<string> pruneScopeDirectories,
    IReadOnlyList<string> pruneExactDirectories)
{
    public static Lr2NormalFolderSyncScope Empty { get; } = new([], [], [], []);

    public IReadOnlyList<string> ChartPaths { get; } = chartPaths ?? [];

    /// <summary>
    /// 通常行を生成・日時取り直しするdirectory。完全走査では登録root内の確定集合、
    /// catalog mutationでは変更元・変更先を含む登録rootと、削除・移動元の存続する直接の親から登録rootまでの祖先です。
    /// </summary>
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
    /// <summary>全登録rootの完全走査で確定した必要directory集合を共有し、全rootの整理範囲を作ります。局所mutationの範囲には流用しません。</summary>
    /// <param name="rootDirectories">完全走査を受理した登録root。</param>
    /// <param name="currentDirectoryPaths">登録root内のroot・祖先を含む確定済み集合。譜面単位の再探索や複製は行いません。</param>
    internal static Lr2NormalFolderSyncScope CreateForFileDiff(
        IEnumerable<string> rootDirectories,
        IReadOnlyList<string> currentDirectoryPaths)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0) { return Lr2NormalFolderSyncScope.Empty; }
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return new Lr2NormalFolderSyncScope([], currentDirectoryPaths, roots, []);
    }

    /// <summary>
    /// catalog mutationの局所同期範囲を作ります。<see cref="Lr2NormalFolderSyncScope.DirectoryPaths"/>には、
    /// 変更元・変更先を含む登録root（BMSの残存によらず保持）と、削除・移動元の存続する直接の親から登録rootまでの祖先を入れます。
    /// 変更と無関係な登録rootは含めません。整理範囲は従来どおりで、変更を含む登録rootは生成対象に入るため整理されません。
    /// </summary>
    internal static Lr2NormalFolderSyncScope CreateForCatalogMutation(
        IEnumerable<string> rootDirectories,
        Lr2NormalFolderCatalogMutationReceipt receipt)
    {
        List<string> roots = NormalizeRoots(rootDirectories);
        if (roots.Count == 0 || receipt?.HasMutation != true)
        {
            return Lr2NormalFolderSyncScope.Empty;
        }

        var chartPaths = new HashSet<string>(StringComparer.Ordinal);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneScopeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pruneExactDirectories = new HashSet<string>(StringComparer.Ordinal);
        Lr2NormalFolderCurrentBmsLookup currentBmsLookup = receipt.CreateCurrentBmsLookup();
        // 開始directoryは正規化して重複を除きます。同じ親を持つ多数の譜面の変更で、同じ探索を繰り返さないためです。
        var survivingParentStarts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string addedChartPath in receipt.AddedBmsChartPaths ?? [])
        {
            AddIfUnderAnyRoot(chartPaths, addedChartPath, roots);
            AddContainingRoot(directoryPaths, Lr2FolderPath.SafeGetDirectoryName(addedChartPath), roots);
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
            AddContainingRoot(directoryPaths, Lr2FolderPath.SafeGetDirectoryName(pathChange.OldPath), roots);
            AddContainingRoot(directoryPaths, Lr2FolderPath.SafeGetDirectoryName(pathChange.NewPath), roots);
            AddNormalizedDirectory(survivingParentStarts, Lr2FolderPath.SafeGetDirectoryName(pathChange.OldPath));
        }

        foreach (string removedChartPath in receipt.RemovedBmsChartPaths ?? [])
        {
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, removedChartPath, roots);
            AddEmptyAncestorExactScopesIfUnderAnyRoot(
                pruneExactDirectories,
                removedChartPath,
                roots,
                currentBmsLookup);
            AddContainingRoot(directoryPaths, Lr2FolderPath.SafeGetDirectoryName(removedChartPath), roots);
            AddNormalizedDirectory(survivingParentStarts, Lr2FolderPath.SafeGetDirectoryName(removedChartPath));
        }

        foreach (CatalogFolderPathReplacement change in receipt.FolderPathChanges)
        {
            bool isDirectory = !change.IsCustomFile;
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, change.OldFolderPath, roots, isDirectory);
            AddChartDirectoryScopeIfUnderAnyRoot(pruneScopeDirectories, change.NewFolderPath, roots, isDirectory);
            AddEmptyAncestorExactScopesForDirectoryIfUnderAnyRoot(pruneExactDirectories,
                isDirectory ? change.OldFolderPath : Lr2FolderPath.SafeGetDirectoryName(change.OldFolderPath), roots, currentBmsLookup);
            AddContainingRoot(directoryPaths, isDirectory ? change.OldFolderPath : Lr2FolderPath.SafeGetDirectoryName(change.OldFolderPath), roots);
            AddContainingRoot(directoryPaths, isDirectory ? change.NewFolderPath : Lr2FolderPath.SafeGetDirectoryName(change.NewFolderPath), roots);
            // 移転元フォルダ・移転元customの親から、変更後もBMSが残る最も近いdirectoryの日時を取り直します。
            AddNormalizedDirectory(survivingParentStarts, Lr2FolderPath.SafeGetDirectoryName(change.OldFolderPath));
        }

        var survivingParentVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var underPruneScopeMemo = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (string startDirectory in survivingParentStarts)
        {
            AddSurvivingDirectParentChain(
                directoryPaths,
                startDirectory,
                roots,
                currentBmsLookup,
                pruneScopeDirectories,
                survivingParentVisited,
                underPruneScopeMemo);
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

        foreach (CatalogFolderPathReplacement change in receipt.FolderPathChanges)
        {
            AddBmsQueryDirectory(pathQueryDirectories, countQueryDirectories, change.OldFolderPath, roots, !change.IsCustomFile);
            AddBmsQueryDirectory(pathQueryDirectories, countQueryDirectories, change.NewFolderPath, roots, !change.IsCustomFile);
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

    private static void AddChartDirectoryScopeIfUnderAnyRoot(HashSet<string> result, string chartPath, IReadOnlyCollection<string> rootDirectories, bool inputIsDirectory = false)
    {
        if (result == null || string.IsNullOrWhiteSpace(chartPath) || rootDirectories == null || rootDirectories.Count == 0)
        {
            return;
        }

        string directoryPath = Lr2FolderPath.NormalizeDirectoryPath(inputIsDirectory ? chartPath : Lr2FolderPath.SafeGetDirectoryName(chartPath));
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

    /// <summary>directoryを一度だけ正規化し、それを含む最も深い登録root（正規化済み）を返します。</summary>
    private static string FindContainingRoot(string directoryPath, IReadOnlyCollection<string> rootDirectories)
    {
        return FindContainingRootNormalized(Lr2FolderPath.NormalizeDirectoryPath(directoryPath), rootDirectories);
    }

    /// <summary>変更元・変更先を含む登録rootを、BMSの残存によらず書込み範囲へ加えます。</summary>
    private static void AddContainingRoot(
        HashSet<string> result,
        string directoryPath,
        IReadOnlyCollection<string> rootDirectories)
    {
        string root = FindContainingRoot(directoryPath, rootDirectories);
        if (result != null && !string.IsNullOrWhiteSpace(root))
        {
            result.Add(root);
        }
    }

    /// <summary>
    /// 削除・移動元の親から登録rootへ向かい、変更の確定後もBMSが残る最も近いdirectoryと、その祖先を登録rootまで加えます。
    /// 判定は捕捉済みのBMS件数（変更directoryと登録rootまでの祖先）だけを使い、全件を走査しません。登録rootの外は対象外です。
    /// 探索済みのdirectoryから上の判定は開始directoryによらず同じため、探索済みに達した時点で打ち切ります。
    /// これにより全開始directoryの探索は、通過する異なるdirectoryの数に比例します。
    /// </summary>
    /// <param name="result">書込み範囲へ加える集合。</param>
    /// <param name="startDirectory">正規化済みの削除・移動元の親directory。</param>
    /// <param name="rootDirectories">正規化済みの登録root。</param>
    /// <param name="currentBmsLookup">変更確定後に捕捉した現在BMSの件数facts。</param>
    /// <param name="pruneScopeDirectories">
    /// 正規化済みの整理範囲の起点（大文字小文字を区別しない集合）。その配下に残るBMSは現在の譜面パスから実際の表記で範囲へ入るため、
    /// ここでは飛ばして上位を探します。大小文字だけの改名で旧表記のdirectoryを存続と誤認し、旧キーの行を残すことを防ぎます。
    /// </param>
    /// <param name="visited">同じ変更の全開始directoryで共有する探索済みdirectory。</param>
    /// <param name="underPruneScopeMemo">同じ変更で共有する、directoryが整理範囲の起点またはその配下かの判定結果。</param>
    private static void AddSurvivingDirectParentChain(
        HashSet<string> result,
        string startDirectory,
        IReadOnlyCollection<string> rootDirectories,
        Lr2NormalFolderCurrentBmsLookup currentBmsLookup,
        HashSet<string> pruneScopeDirectories,
        HashSet<string> visited,
        Dictionary<string, bool> underPruneScopeMemo)
    {
        string root = FindContainingRootNormalized(startDirectory, rootDirectories);
        if (result == null || string.IsNullOrWhiteSpace(root) || currentBmsLookup == null)
        {
            return;
        }

        string current = startDirectory;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendantNormalized(current, root)
            && visited.Add(current))
        {
            if (!IsUnderPruneScope(current, pruneScopeDirectories, underPruneScopeMemo)
                && currentBmsLookup.HasBmsChartUnderDirectory(current))
            {
                AddDirectoryAndAncestorsWithinRoot(result, current, root);
                return;
            }
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
        }
    }

    /// <summary>
    /// 正規化済みdirectoryが整理範囲の起点そのものか、その配下かを、祖先を集合へ引いて判定します。
    /// 整理範囲の数によらず祖先の段数に比例し、判定済みの祖先で打ち切ります。
    /// </summary>
    private static bool IsUnderPruneScope(
        string normalizedDirectory,
        HashSet<string> pruneScopeDirectories,
        Dictionary<string, bool> memo)
    {
        if (pruneScopeDirectories == null || pruneScopeDirectories.Count == 0)
        {
            return false;
        }

        var pending = new List<string>();
        bool isUnder = false;
        string current = normalizedDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (memo.TryGetValue(current, out bool known))
            {
                isUnder = known;
                break;
            }
            pending.Add(current);
            if (pruneScopeDirectories.Contains(current))
            {
                isUnder = true;
                break;
            }
            current = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
        }

        // 見つかった起点・判定済みの祖先より下の通過directoryは、すべて同じ結果になります。
        foreach (string directory in pending)
        {
            memo[directory] = isUnder;
        }
        return isUnder;
    }

    /// <summary>正規化済みdirectoryから登録rootまでの各directoryを加えます。</summary>
    private static void AddDirectoryAndAncestorsWithinRoot(HashSet<string> result, string normalizedDirectory, string root)
    {
        string current = normalizedDirectory;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendantNormalized(current, root))
        {
            result.Add(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            current = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
        }
    }

    /// <summary>正規化済みdirectoryを含む最も深い登録rootを返します。登録rootは正規化済みです。</summary>
    private static string FindContainingRootNormalized(string normalizedDirectory, IReadOnlyCollection<string> rootDirectories)
    {
        if (string.IsNullOrWhiteSpace(normalizedDirectory) || rootDirectories == null)
        {
            return null;
        }

        string result = null;
        foreach (string root in rootDirectories)
        {
            if (Lr2FolderPath.IsSameOrDescendantNormalized(normalizedDirectory, root)
                && (result == null || root.Length > result.Length))
            {
                result = root;
            }
        }
        return result;
    }

    private static void AddNormalizedDirectory(HashSet<string> result, string directoryPath)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            result.Add(normalized);
        }
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
