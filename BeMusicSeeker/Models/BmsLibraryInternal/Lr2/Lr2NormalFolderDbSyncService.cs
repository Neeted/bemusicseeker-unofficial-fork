using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2NormalFolderDbSyncRequest
{
    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> DirectoryPaths { get; set; } = [];

    /// <summary>
    /// trueのとき、<see cref="DirectoryPaths"/>を通常行の生成と日時取り直しの完全な対象集合として扱います。
    /// 登録rootや<see cref="RequiredCustomParentDirectories"/>の祖先を暗黙に加えず、変更と無関係な登録rootの行を生成・更新しません。
    /// 局所mutationだけが指定し、全体同期・差分読込み・初期化の既定動作（全登録rootを含める）は変えません。
    /// </summary>
    public bool DirectoryPathsAreComplete { get; set; }

    /// <summary>所有者が今回の物理残存を確定したcustomの親。DB行だけを残存根拠にしません。</summary>
    public IReadOnlyCollection<string> RequiredCustomParentDirectories { get; set; } = [];

    /// <summary>成功した物理範囲内で消失を確認した管理外customのraw完全一致キーです。</summary>
    public IReadOnlyCollection<string> RemovedCustomPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> PruneScopeDirectories { get; set; } = [];

    /// <summary>今回の確定表一覧と配置が所有する表ディレクトリです。通常行の生成は保ち、内部行の削除だけを管理出力側へ委ねます。</summary>
    public IReadOnlyCollection<string> PruneExcludedDirectories { get; set; } = [];

    /// <summary>局所mutationで子孫を含めず整理する物理directoryです。保存済みrawキーは混ぜません。</summary>
    public IReadOnlyCollection<string> PruneExactDirectories { get; set; } = [];

    /// <summary>全登録走査で取得済みの全folder面です。局所mutationは指定せず、対象限定のDB読取りを使います。</summary>
    public Lr2FolderTableSnapshot FolderTableSnapshot { get; set; }

    public Func<string, DateTime?> DirectoryLastWriteTimeUtcResolver { get; set; }

    public Func<string, IEnumerable<string>> FolderInfoLinesReader { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public bool AllowPrune { get; set; }

    public bool UseScopedExistingRows { get; set; }
}

internal sealed class Lr2NormalFolderDbSyncResult(
    int generatedCount,
    int upsertedCount,
    int deletedCount,
    int skippedUnsupportedPathCount,
    int skippedMissingMetadataCount,
    int metadataRequestedDirectoryCount,
    int metadataResolvedDirectoryCount,
    int folderInfoCandidateCount,
    int folderInfoAppliedCount,
    int folderInfoReadFailureCount,
    int skippedIncompatibleChartPathCount,
    long targetBuildMs,
    long metadataBuildMs,
    long existingReadMs,
    long rowGenerateMs,
    long planMs,
    long writeMs,
    long elapsedMs)
{
    public int GeneratedCount { get; } = generatedCount;

    public int UpsertedCount { get; } = upsertedCount;

    public int DeletedCount { get; } = deletedCount;

    public int SkippedUnsupportedPathCount { get; } = skippedUnsupportedPathCount;

    public int SkippedMissingMetadataCount { get; } = skippedMissingMetadataCount;

    public int MetadataRequestedDirectoryCount { get; } = metadataRequestedDirectoryCount;

    public int MetadataResolvedDirectoryCount { get; } = metadataResolvedDirectoryCount;

    public int FolderInfoCandidateCount { get; } = folderInfoCandidateCount;

    public int FolderInfoAppliedCount { get; } = folderInfoAppliedCount;

    public int FolderInfoReadFailureCount { get; } = folderInfoReadFailureCount;

    public int SkippedIncompatibleChartPathCount { get; } = skippedIncompatibleChartPathCount;

    public long TargetBuildMs { get; } = targetBuildMs;

    public long MetadataBuildMs { get; } = metadataBuildMs;

    public long ExistingReadMs { get; } = existingReadMs;

    public long RowGenerateMs { get; } = rowGenerateMs;

    public long PlanMs { get; } = planMs;

    public long WriteMs { get; } = writeMs;

    public long ElapsedMs { get; } = elapsedMs;

    public bool HasChanges => UpsertedCount > 0 || DeletedCount > 0;
}

/// <summary>局所整理範囲のfolderinfo確認先と、対象外の通常階層が引き続き必要とする祖先です。</summary>
internal sealed record Lr2LocalFolderDependencies(
    IReadOnlyList<string> FolderInfoCandidateDirectories,
    IReadOnlyList<string> RetainedAncestorDirectories);

internal static class Lr2NormalFolderDbSyncService
{
    internal static Lr2NormalFolderDbSyncResult Sync(
        LR2SongDBExtended songDb,
        Lr2NormalFolderDbSyncRequest request,
        bool commitTransaction = true)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2NormalFolderDbSyncRequest();
        var stopwatch = Stopwatch.StartNew();
        var stepStopwatch = Stopwatch.StartNew();

        List<string> rootDirectories = NormalizeRootDirectories(request.RootDirectories);
        IReadOnlyCollection<string> directoryMetadataTargets;
        if (request.DirectoryPathsAreComplete)
        {
            directoryMetadataTargets = ResolveCompleteDirectoryMetadataTargets(rootDirectories, request.DirectoryPaths);
        }
        else
        {
            directoryMetadataTargets = ResolveDirectoryMetadataTargets(
                rootDirectories,
                request.DirectoryPaths,
                request.ChartPaths);
            directoryMetadataTargets = [.. directoryMetadataTargets.Concat(
                CreateDirectoryMetadataTargetsFromDirectories(rootDirectories, request.RequiredCustomParentDirectories))
                .Distinct(StringComparer.Ordinal)];
        }
        long targetBuildMs = RestartElapsed(stepStopwatch);
        Lr2FolderDirectoryMetadataSnapshot metadataSnapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = directoryMetadataTargets,
            FolderInfoFilePaths = request.FolderInfoFilePaths,
            FolderInfoFileEntries = request.FolderInfoFileEntries,
            DirectoryLastWriteTimeUtcResolver = request.DirectoryLastWriteTimeUtcResolver,
            FolderInfoLinesReader = request.FolderInfoLinesReader
        });
        long metadataBuildMs = RestartElapsed(stepStopwatch);
        IReadOnlyCollection<LR2SongDB.folder> existingRows = request.FolderTableSnapshot?.Rows ?? ReadExistingRows(
            songDb,
            request,
            rootDirectories,
            directoryMetadataTargets);
        long existingReadMs = RestartElapsed(stepStopwatch);
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = rootDirectories,
            DirectoryPaths = directoryMetadataTargets,
            ExistingRows = existingRows,
            DirectoryMetadataResolver = metadataSnapshot.Resolve,
            GeneratedAtUtc = request.GeneratedAtUtc,
            IncludeRootDirectories = !request.DirectoryPathsAreComplete
        });
        long rowGenerateMs = RestartElapsed(stepStopwatch);
        bool hasExplicitPrune = request.PruneScopeDirectories?.Count > 0 || request.PruneExactDirectories?.Count > 0;
        IReadOnlyCollection<string> pruneScopeDirectories = request.PruneScopeDirectories?.Count > 0
            ? NormalizePruneScopeDirectories(request.PruneScopeDirectories, rootDirectories)
            : hasExplicitPrune
                ? []
                : null;
        IReadOnlyCollection<string> pruneExactDirectories = request.PruneExactDirectories?.Count > 0
            ? NormalizePruneExactDirectories(request.PruneExactDirectories, rootDirectories)
            : [];
        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            existingRows,
            rootDirectories,
            pruneScopeDirectories,
            pruneExactDirectories,
            request.PruneExcludedDirectories);
        bool canPrune = request.AllowPrune;
        if (!canPrune && plan.DeletePaths.Count > 0)
        {
            plan = new Lr2FolderGenerationSyncPlan(plan.UpsertRows, []);
        }
        if (request.RemovedCustomPaths.Count > 0)
        {
            plan = new Lr2FolderGenerationSyncPlan(plan.UpsertRows,
                [.. plan.DeletePaths.Concat(request.RemovedCustomPaths).Distinct(StringComparer.Ordinal)]);
        }
        long planMs = RestartElapsed(stepStopwatch);
        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(
            songDb,
            plan,
            commitTransaction);
        long writeMs = RestartElapsed(stepStopwatch);

        stopwatch.Stop();
        return new Lr2NormalFolderDbSyncResult(
            generation.Rows.Count,
            writeResult.UpsertedCount,
            writeResult.DeletedCount,
            generation.SkippedUnsupportedPathCount,
            generation.SkippedMissingMetadataCount,
            metadataSnapshot.RequestedDirectoryCount,
            metadataSnapshot.ResolvedDirectoryCount,
            metadataSnapshot.FolderInfoCandidateCount,
            metadataSnapshot.FolderInfoAppliedCount,
            metadataSnapshot.FolderInfoReadFailureCount,
            skippedIncompatibleChartPathCount: 0,
            targetBuildMs,
            metadataBuildMs,
            existingReadMs,
            rowGenerateMs,
            planMs,
            writeMs,
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>全登録走査の取得面から、登録root内のcustom依存候補を一度だけ抽出します。物理残存は呼出元の走査面で確認します。</summary>
    internal static IReadOnlyList<string> CaptureCustomDependencyCandidates(
        Lr2FolderTableSnapshot snapshot, IEnumerable<string> rootDirectories)
    {
        List<string> roots = NormalizeRootDirectories(rootDirectories);
        return [.. snapshot.Rows.Where(row => Path.IsPathFullyQualified(row.path)
                && string.Equals(Path.GetExtension(row.path), ".lr2folder", StringComparison.OrdinalIgnoreCase)
                && roots.Any(root => Lr2FolderPath.IsSameOrDescendant(row.path, root)))
            .Select(row => row.path)];
    }

    /// <summary>prune subtreeと削除候補祖先の子階層だけからcustom候補を取得します。旧exact削除キー自体は変更しません。</summary>
    internal static IReadOnlyList<string> CaptureCustomDependencyCandidates(
        LR2SongDBExtended songDb, IEnumerable<string> roots,
        IEnumerable<string> pruneScopes, IEnumerable<string> pruneExactDirectories)
    {
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in (pruneScopes ?? []).Concat(pruneExactDirectories ?? []))
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (!string.IsNullOrWhiteSpace(normalized)
                && (roots ?? []).Any(root => Lr2FolderPath.IsSameOrDescendant(normalized, root)))
            {
                prefixes.Add(Lr2FolderPath.ToFolderPath(normalized));
            }
        }
        return Lr2FolderExistingRowLookup.QueryCustomFolderDependencyPaths(songDb, prefixes);
    }

    /// <summary>局所整理する通常行の物理directoryを限定取得し、folderinfoの残存確認へ渡します。</summary>
    internal static Lr2LocalFolderDependencies CaptureFolderInfoDependencyDirectories(
        LR2SongDBExtended songDb, IEnumerable<string> pruneScopes, IEnumerable<string> pruneExactDirectories)
    {
        return new Lr2LocalFolderDependencies([.. Lr2FolderExistingRowLookup.QueryPathPrefixScopes(songDb,
                (pruneScopes ?? []).Select(Lr2FolderPath.ToFolderPath))
            .Where(row => row.type == 1 && row.path.Length > 0 && Lr2FolderPath.IsDirectorySeparator(row.path[^1]))
            .Select(row => Lr2FolderPath.NormalizeDirectoryPath(row.path))
            .Concat(pruneExactDirectories ?? [])
            .Concat(pruneScopes ?? [])
            .Distinct(StringComparer.Ordinal)],
            Lr2FolderExistingRowLookup.QueryAncestorsWithRetainedNormalChildren(songDb, pruneExactDirectories ?? [], pruneScopes ?? []));
    }

    internal static IReadOnlyCollection<string> CreateDirectoryMetadataTargets(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartPaths)
    {
        List<string> roots = NormalizeRootDirectories(rootDirectories);
        List<string> rootsForMatching = [.. roots.OrderByDescending(root => root.Length)];
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            result.Add(root);
        }

        foreach (string chartPath in chartPaths ?? [])
        {
            string chartDirectory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(chartPath));
            if (string.IsNullOrWhiteSpace(chartDirectory))
            {
                continue;
            }

            string root = FindContainingRoot(chartDirectory, rootsForMatching);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (string directory in EnumerateDirectoriesFromRoot(root, chartDirectory))
            {
                result.Add(directory);
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyCollection<string> CreateDirectoryMetadataTargetsFromDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartDirectories)
    {
        return CreateDirectoryMetadataTargetsFromDirectoriesCore(
            rootDirectories,
            chartDirectories,
            pathsAlreadyNormalized: false);
    }

    /// <summary>走査済みdirectoryから登録rootと祖先を一度確定し、同操作の走査結果と同期scopeで共有する一覧を返します。</summary>
    internal static IReadOnlyList<string> CreateDirectoryMetadataTargetsFromNormalizedDirectories(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartDirectories)
    {
        return CreateDirectoryMetadataTargetsFromDirectoriesCore(
            rootDirectories,
            chartDirectories,
            pathsAlreadyNormalized: true);
    }

    /// <summary>
    /// 指定directoryから、それを含む登録rootまでの祖先だけを返します。変更と無関係な登録rootは加えません。
    /// 局所mutationが通常行を書き換える範囲を、変更の根拠となるdirectoryから限定して作るために使います。
    /// </summary>
    /// <param name="rootDirectories">包含rootの判定に使う登録root。</param>
    /// <param name="directories">起点directory。登録rootの外は無視します。</param>
    /// <returns>起点と祖先（包含rootを含む）の重複なし一覧。</returns>
    internal static IReadOnlyList<string> CreateAncestorDirectoryTargetsWithinRoots(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> directories)
    {
        return CreateDirectoryMetadataTargetsFromDirectoriesCore(
            rootDirectories,
            directories,
            pathsAlreadyNormalized: false,
            includeAllRoots: false);
    }

    private static IReadOnlyList<string> CreateDirectoryMetadataTargetsFromDirectoriesCore(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartDirectories,
        bool pathsAlreadyNormalized,
        bool includeAllRoots = true)
    {
        List<string> roots = NormalizeRootDirectories(rootDirectories);
        List<string> rootsForMatching = [.. roots.OrderByDescending(root => root.Length)];
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (includeAllRoots)
        {
            foreach (string root in roots)
            {
                result.Add(root);
            }
        }

        foreach (string chartDirectory in chartDirectories ?? [])
        {
            string normalized = pathsAlreadyNormalized
                ? chartDirectory
                : Lr2FolderPath.NormalizeDirectoryPath(chartDirectory);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            string root = FindContainingRoot(normalized, rootsForMatching);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (string directory in EnumerateDirectoriesFromRoot(root, normalized))
            {
                result.Add(directory);
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyCollection<string> ResolveCompleteDirectoryMetadataTargets(
        IReadOnlyCollection<string> rootDirectories,
        IEnumerable<string> directoryPaths)
    {
        List<string> rootsForMatching = [.. (rootDirectories ?? []).OrderByDescending(root => root.Length)];
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directoryPath in directoryPaths ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !string.IsNullOrWhiteSpace(FindContainingRoot(normalized, rootsForMatching)))
            {
                result.Add(normalized);
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyCollection<string> ResolveDirectoryMetadataTargets(
        IReadOnlyCollection<string> rootDirectories,
        IEnumerable<string> directoryPaths,
        IEnumerable<string> chartPaths)
    {
        bool hasDirectoryPaths = false;
        foreach (string directoryPath in directoryPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                hasDirectoryPaths = true;
                break;
            }
        }

        if (!hasDirectoryPaths)
        {
            return CreateDirectoryMetadataTargets(rootDirectories, chartPaths);
        }

        List<string> roots = NormalizeRootDirectories(rootDirectories);
        List<string> rootsForMatching = [.. roots.OrderByDescending(root => root.Length)];
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string root in roots)
        {
            result.Add(root);
        }

        foreach (string directoryPath in directoryPaths ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directoryPath);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            string root = FindContainingRoot(normalized, rootsForMatching);
            if (!string.IsNullOrWhiteSpace(root))
            {
                result.Add(normalized);
            }
        }

        return [.. result.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<LR2SongDB.folder> ReadExistingRows(
        LR2SongDBExtended songDb,
        Lr2NormalFolderDbSyncRequest request,
        IReadOnlyCollection<string> rootDirectories,
        IReadOnlyCollection<string> directoryMetadataTargets)
    {
        if (request?.UseScopedExistingRows != true)
        {
            return [.. songDb.Table<LR2SongDB.folder>()];
        }

        var rowsByPath = new Dictionary<string, LR2SongDB.folder>(StringComparer.Ordinal);
        IReadOnlyCollection<string> normalizedPruneScopeDirectories = NormalizePruneScopeDirectories(request.PruneScopeDirectories, rootDirectories);
        foreach (LR2SongDB.folder row in QueryExistingRowsByExactPaths(
            songDb,
            CreateExistingRowExactPathScope(directoryMetadataTargets, request.PruneExactDirectories,
                normalizedPruneScopeDirectories)))
        {
            AddExistingRow(rowsByPath, row);
        }

        foreach (LR2SongDB.folder row in QueryExistingRowsByScopeDirectories(
            songDb,
            normalizedPruneScopeDirectories))
        {
            AddExistingRow(rowsByPath, row);
        }

        return [.. rowsByPath.Values];
    }

    private static IReadOnlyCollection<string> CreateExistingRowExactPathScope(
        IEnumerable<string> directoryMetadataTargets,
        IEnumerable<string> pruneExactDirectories,
        IReadOnlyCollection<string> pruneScopeDirectories)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in directoryMetadataTargets ?? [])
        {
            AddFolderPath(result, directory);
        }
        if (pruneScopeDirectories.Count > 0)
        {
            result = new HashSet<string>(FilterExactPathsOutsidePruneScopes(result, pruneScopeDirectories), StringComparer.Ordinal);
        }
        foreach (string directory in pruneExactDirectories ?? [])
        {
            AddFolderPath(result, directory);
        }
        return [.. result];
    }

    private static IReadOnlyCollection<string> FilterExactPathsOutsidePruneScopes(
        IEnumerable<string> exactPaths,
        IReadOnlyCollection<string> pruneScopeDirectories)
    {
        if (exactPaths == null)
        {
            return [];
        }

        if (pruneScopeDirectories == null || pruneScopeDirectories.Count == 0)
        {
            return [.. exactPaths];
        }

        return [.. exactPaths.Where(path => !Lr2FolderPath.IsDirectorySeparator(path[path.Length - 1])
            || !pruneScopeDirectories.Any(scope => Lr2FolderPath.IsSameOrDescendant(path, scope)))];
    }

    private static void AddFolderPath(ISet<string> result, string directory)
    {
        if (result == null)
        {
            return;
        }

        string path = Lr2FolderPath.ToFolderPath(directory);
        if (!string.IsNullOrWhiteSpace(path))
        {
            result.Add(path);
        }
    }

    private static void AddExistingRow(IDictionary<string, LR2SongDB.folder> rowsByPath, LR2SongDB.folder row)
    {
        if (rowsByPath == null || string.IsNullOrWhiteSpace(row?.path))
        {
            return;
        }

        rowsByPath[row.path] = row;
    }

    private static IEnumerable<LR2SongDB.folder> QueryExistingRowsByExactPaths(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> exactPaths)
    {
        if (songDb == null || exactPaths == null || exactPaths.Count == 0)
        {
            return [];
        }

        string[] paths = [.. exactPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal)];
        return Lr2FolderExistingRowLookup.QueryExactPaths(songDb, paths);
    }

    private static IEnumerable<LR2SongDB.folder> QueryExistingRowsByScopeDirectories(
        LR2SongDBExtended songDb,
        IReadOnlyCollection<string> scopeDirectories)
    {
        if (songDb == null || scopeDirectories == null || scopeDirectories.Count == 0)
        {
            return [];
        }

        var scopePaths = new List<string>();
        foreach (string scopeDirectory in scopeDirectories)
        {
            string scopePath = Lr2FolderPath.ToFolderPath(scopeDirectory);
            if (string.IsNullOrWhiteSpace(scopePath))
            {
                continue;
            }

            scopePaths.Add(scopePath);
        }
        return Lr2FolderExistingRowLookup.QueryPathPrefixScopes(songDb, scopePaths);
    }

    private static List<string> NormalizeRootDirectories(IEnumerable<string> rootDirectories)
    {
        return [.. (rootDirectories ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> NormalizePruneScopeDirectories(IEnumerable<string> pruneScopeDirectories, IReadOnlyCollection<string> rootDirectories)
    {
        if (rootDirectories == null || rootDirectories.Count == 0)
        {
            return [];
        }

        return [.. NormalizeRootDirectories(pruneScopeDirectories)
            .Where(path => rootDirectories.Any(root => Lr2FolderPath.IsSameOrDescendant(path, root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static List<string> NormalizePruneExactDirectories(
        IEnumerable<string> pruneExactDirectories,
        IReadOnlyCollection<string> rootDirectories)
    {
        if (rootDirectories == null || rootDirectories.Count == 0)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string directory in pruneExactDirectories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (!string.IsNullOrWhiteSpace(normalized)
                && rootDirectories.Any(root => Lr2FolderPath.IsSameOrDescendant(normalized, root)))
            {
                result.Add(normalized);
            }
        }
        return [.. result];
    }

    private static IEnumerable<string> EnumerateDirectoriesFromRoot(string root, string targetDirectory)
    {
        var stack = new Stack<string>();
        string current = targetDirectory;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendantNormalized(current, root))
        {
            stack.Push(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
    }

    private static string FindContainingRoot(string directory, IEnumerable<string> roots)
    {
        foreach (string root in roots ?? [])
        {
            if (Lr2FolderPath.IsSameOrDescendantNormalized(directory, root))
            {
                return root;
            }
        }
        return null;
    }

    private static long RestartElapsed(Stopwatch stopwatch)
    {
        long elapsedMs = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        return elapsedMs;
    }
}
