using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2NormalFolderDbSyncRequest
{
    public IReadOnlyCollection<string> RootDirectories { get; set; } = [];

    public IReadOnlyCollection<string> ChartPaths { get; set; } = [];

    public IReadOnlyCollection<string> FolderInfoFilePaths { get; set; } = [];

    public IReadOnlyCollection<string> PruneScopeDirectories { get; set; } = [];

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

        List<string> rootDirectories = NormalizeRootDirectories(request.RootDirectories);
        ChartPathFilterResult chartPathFilter = FilterCompatibleChartPaths(request.ChartPaths);
        List<string> compatibleChartPaths = chartPathFilter.CompatibleChartPaths;
        Lr2FolderDirectoryMetadataSnapshot metadataSnapshot = Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = CreateDirectoryMetadataTargets(rootDirectories, compatibleChartPaths),
            FolderInfoFilePaths = request.FolderInfoFilePaths,
            DirectoryLastWriteTimeUtcResolver = request.DirectoryLastWriteTimeUtcResolver,
            FolderInfoLinesReader = request.FolderInfoLinesReader
        });
        List<LR2SongDB.folder> existingRows = [.. songDb.Table<LR2SongDB.folder>()];
        Lr2FolderGenerationResult generation = Lr2FolderRowGenerator.GenerateNormalDirectoryRows(new Lr2FolderGenerationRequest
        {
            RootDirectories = rootDirectories,
            ChartPaths = compatibleChartPaths,
            ExistingRows = existingRows,
            DirectoryMetadataResolver = metadataSnapshot.Resolve,
            GeneratedAtUtc = request.GeneratedAtUtc
        });
        IReadOnlyCollection<string> pruneScopeDirectories = request.PruneScopeDirectories?.Count > 0
            ? NormalizePruneScopeDirectories(request.PruneScopeDirectories, rootDirectories)
            : null;
        Lr2FolderGenerationSyncPlan plan = Lr2FolderGenerationScopePlanner.PlanNormalDirectorySync(
            generation,
            existingRows,
            rootDirectories,
            pruneScopeDirectories);
        bool canPrune = request.AllowPrune
            && chartPathFilter.SkippedIncompatibleChartPathCount == 0;
        if (!canPrune && plan.DeletePaths.Count > 0)
        {
            plan = new Lr2FolderGenerationSyncPlan(plan.UpsertRows, []);
        }
        Lr2FolderGenerationWriteResult writeResult = Lr2FolderDbWriter.ApplySyncPlan(songDb, plan);

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
            chartPathFilter.SkippedIncompatibleChartPathCount,
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

    private static ChartPathFilterResult FilterCompatibleChartPaths(IEnumerable<string> chartPaths)
    {
        var compatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        foreach (string path in chartPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Lr2CompatibilityEvaluator.EvaluateChartPath(path).CanComputeFolderParent)
            {
                compatible.Add(path);
            }
            else
            {
                skipped++;
            }
        }

        return new ChartPathFilterResult(
            [.. compatible.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)],
            skipped);
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

    private sealed class ChartPathFilterResult(List<string> compatibleChartPaths, int skippedIncompatibleChartPathCount)
    {
        public List<string> CompatibleChartPaths { get; } = compatibleChartPaths;

        public int SkippedIncompatibleChartPathCount { get; } = skippedIncompatibleChartPathCount;
    }
}
