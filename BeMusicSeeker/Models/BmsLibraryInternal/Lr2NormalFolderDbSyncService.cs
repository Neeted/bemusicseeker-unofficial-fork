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

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyDictionary<string, RootFileEnumerationEntry> FolderInfoFileEntries { get; set; } =
        new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> PruneScopeDirectories { get; set; } = [];

    public IReadOnlyCollection<string> PruneExactDirectories { get; set; } = [];

    public Func<string, DateTime?> DirectoryLastWriteTimeUtcResolver { get; set; }

    public Func<string, IEnumerable<string>> FolderInfoLinesReader { get; set; }

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

    public bool AllowPrune { get; set; }
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

internal static class Lr2NormalFolderDbSyncService
{
    internal static Lr2NormalFolderDbSyncResult Sync(
        LR2SongDBExtended songDb,
        Lr2NormalFolderDbSyncRequest request)
    {
        if (songDb == null)
        {
            throw new ArgumentNullException(nameof(songDb));
        }

        request ??= new Lr2NormalFolderDbSyncRequest();
        var stopwatch = Stopwatch.StartNew();
        var stepStopwatch = Stopwatch.StartNew();

        List<string> rootDirectories = NormalizeRootDirectories(request.RootDirectories);
        IReadOnlyCollection<string> directoryMetadataTargets = ResolveDirectoryMetadataTargets(
            rootDirectories,
            request.DirectoryPaths,
            request.ChartPaths);
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
        List<LR2SongDB.folder> existingRows = [.. songDb.Table<LR2SongDB.folder>()];
        long existingReadMs = RestartElapsed(stepStopwatch);
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = rootDirectories,
            DirectoryPaths = directoryMetadataTargets,
            ExistingRows = existingRows,
            DirectoryMetadataResolver = metadataSnapshot.Resolve,
            GeneratedAtUtc = request.GeneratedAtUtc
        });
        long rowGenerateMs = RestartElapsed(stepStopwatch);
        bool hasExplicitPrune = request.PruneScopeDirectories?.Count > 0 || request.PruneExactDirectories?.Count > 0;
        IReadOnlyCollection<string> pruneScopeDirectories = request.PruneScopeDirectories?.Count > 0
            ? NormalizePruneScopeDirectories(request.PruneScopeDirectories, rootDirectories)
            : hasExplicitPrune
                ? []
                : null;
        IReadOnlyCollection<string> pruneExactDirectories = request.PruneExactDirectories?.Count > 0
            ? NormalizePruneScopeDirectories(request.PruneExactDirectories, rootDirectories)
            : [];
        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            existingRows,
            rootDirectories,
            pruneScopeDirectories,
            pruneExactDirectories);
        bool canPrune = request.AllowPrune;
        if (!canPrune && plan.DeletePaths.Count > 0)
        {
            plan = new Lr2FolderGenerationSyncPlan(plan.UpsertRows, []);
        }
        long planMs = RestartElapsed(stepStopwatch);
        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(songDb, plan);
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

    internal static IReadOnlyCollection<string> CreateDirectoryMetadataTargets(
        IEnumerable<string> rootDirectories,
        IEnumerable<string> chartPaths)
    {
        List<string> roots = NormalizeRootDirectories(rootDirectories);
        List<string> rootsForMatching = [.. roots.OrderByDescending(root => root.Length)];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        List<string> roots = NormalizeRootDirectories(rootDirectories);
        List<string> rootsForMatching = [.. roots.OrderByDescending(root => root.Length)];
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            result.Add(root);
        }

        foreach (string chartDirectory in chartDirectories ?? [])
        {
            string normalized = Lr2FolderPath.NormalizeDirectoryPath(chartDirectory);
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
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    private static IEnumerable<string> EnumerateDirectoriesFromRoot(string root, string targetDirectory)
    {
        var stack = new Stack<string>();
        string current = targetDirectory;
        while (!string.IsNullOrWhiteSpace(current)
            && Lr2FolderPath.IsSameOrDescendant(current, root))
        {
            stack.Push(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(current));
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
            if (Lr2FolderPath.IsSameOrDescendant(directory, root))
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
