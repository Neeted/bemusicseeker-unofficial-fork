using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Coordinates folder auto rename batch execution without owning BMSLibrary private state.
/// </summary>
internal sealed class AutoRenameBatchCoordinator
{
    private readonly LibraryFileOperationOwner host;

    internal AutoRenameBatchCoordinator(LibraryFileOperationOwner host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    internal bool Apply(
        IEnumerable<FolderAutoRenamePlan> plans,
        Action<int, int, string> progressReporter = null)
    {
        long operationId = Stopwatch.GetTimestamp();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        List<FolderAutoRenamePlan> planList = [.. (plans ?? []).Where(plan => plan != null)];
        bool hasActionablePlan = false;
        LibraryMutationDelta batchMutation = new LibraryMutationDelta();
        List<LibraryFolderPathChange> movedFolders = [];
        var metrics = new AutoRenameBatchMetrics(operationId, planList.Count);
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts = host.CreateInstallDestinationOverlayChartRefSnapshot();
        HashSet<string> movedSourceDirectories = new(StringComparer.OrdinalIgnoreCase);
        int progressTotal = CountAutoRenameProgressPlans(planList);
        metrics.ProgressTotal = progressTotal;
        int progressProcessed = 0;
        host.LogInstallPerformance("auto_rename_folders_batch start op=" + operationId
            + " planCount=" + planList.Count
            + " progressTotal=" + progressTotal);
        ReportAutoRenameProgress(progressReporter, progressTotal, progressProcessed, string.Empty);
        if (planList.Any(IsDriveRootSourcePlan))
        {
            host.ShowDriveRootBmsSkipped();
        }
        Stopwatch moveLoopStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (FolderAutoRenamePlan plan in planList)
            {
                bool reportProgress = IsAutoRenameProgressPlan(plan);
                try
                {
                    if (plan.FailureException != null)
                    {
                        host.ShowRenameFailed(plan);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(plan.DestinationDirectory) || string.IsNullOrWhiteSpace(plan.SourceDirectory))
                    {
                        continue;
                    }
                    hasActionablePlan = true;
                    ApplyAutoRenamePlanToBatch(
                        plan,
                        batchMutation,
                        movedFolders,
                        installDestinationOverlayCharts,
                        movedSourceDirectories,
                        metrics);
                }
                finally
                {
                    if (reportProgress)
                    {
                        progressProcessed = Math.Min(progressProcessed + 1, progressTotal);
                        ReportAutoRenameProgress(progressReporter, progressTotal, progressProcessed, plan.SourceDirectory);
                    }
                }
            }
        }
        finally
        {
            moveLoopStopwatch.Stop();
            metrics.MoveLoopMs = moveLoopStopwatch.ElapsedMilliseconds;
            host.LogInstallPerformance("auto_rename_folders_batch move_loop_done op=" + operationId
                + " planCount=" + metrics.PlanCount
                + " progressTotal=" + metrics.ProgressTotal
                + " actionable=" + metrics.ActionablePlanCount
                + " movedFolders=" + movedFolders.Count
                + " skippedDuplicateSource=" + metrics.SkippedDuplicateSourceCount
                + " skippedMissingSource=" + metrics.SkippedMissingSourceCount
                + " moveFailed=" + metrics.MoveFailedCount
                + " slowMoves=" + metrics.SlowMoveCount
                + " pathChanges=" + batchMutation.ChartPathChanges.Count
                + " folderPathChanges=" + batchMutation.FolderPathChanges.Count
                + " buildDeltaMs=" + metrics.BuildDeltaMs
                + " moveFileMs=" + metrics.MoveFileMs
                + " appendDeltaMs=" + metrics.AppendDeltaMs
                + " elapsedMs=" + metrics.MoveLoopMs);
            ApplyAutoRenameBatchChanges(batchMutation, movedFolders, metrics);
            totalStopwatch.Stop();
            host.LogInstallPerformance("auto_rename_folders_batch done op=" + operationId
                + " hasActionablePlan=" + hasActionablePlan
                + " movedFolders=" + movedFolders.Count
                + " pathChanges=" + batchMutation.ChartPathChanges.Count
                + " folderPathChanges=" + batchMutation.FolderPathChanges.Count
                + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
        }
        return hasActionablePlan;
    }

    private static bool IsDriveRootSourcePlan(FolderAutoRenamePlan plan)
    {
        return !string.IsNullOrWhiteSpace(plan.SourceDirectory)
            && Path.GetPathRoot(plan.SourceDirectory).Equals(plan.SourceDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountAutoRenameProgressPlans(IEnumerable<FolderAutoRenamePlan> plans)
    {
        return (plans ?? []).Count(IsAutoRenameProgressPlan);
    }

    private static bool IsAutoRenameProgressPlan(FolderAutoRenamePlan plan)
    {
        return plan?.FailureException != null
            || (!string.IsNullOrWhiteSpace(plan?.SourceDirectory)
                && !string.IsNullOrWhiteSpace(plan.DestinationDirectory));
    }

    private static void ReportAutoRenameProgress(Action<int, int, string> progressReporter, int total, int processed, string currentPath)
    {
        if (progressReporter == null || total <= 0)
        {
            return;
        }
        try
        {
            progressReporter(total, Math.Max(0, Math.Min(processed, total)), currentPath ?? string.Empty);
        }
        catch (Exception ex)
        {
            NLogWrapper.FileLogger?.Warn(ex, "auto_rename_progress_report_failed processed=" + processed + " total=" + total);
        }
    }

    private void ApplyAutoRenamePlanToBatch(
        FolderAutoRenamePlan plan,
        LibraryMutationDelta batchMutation,
        List<LibraryFolderPathChange> movedFolders,
        InstallDestinationOverlayChartRefSnapshot installDestinationOverlayCharts,
        HashSet<string> movedSourceDirectories,
        AutoRenameBatchMetrics metrics)
    {
        string srcDir = plan.SourceDirectory;
        string newName = host.NormalizeAutoRenameFolderName(Path.GetFileName(plan.DestinationDirectory));
        if (string.IsNullOrWhiteSpace(newName) || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (movedSourceDirectories.Contains(srcDir))
        {
            metrics.SkippedDuplicateSourceCount++;
            return;
        }
        if (!host.DirectoryExists(srcDir))
        {
            metrics.SkippedMissingSourceCount++;
            host.ShowRenameFolderNotExists(srcDir);
            return;
        }

        string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
        metrics.ActionablePlanCount++;
        Stopwatch stopwatch = Stopwatch.StartNew();
        LibraryMutationDelta delta = host.BuildFolderMoveDelta(srcDir, dstDir, installDestinationOverlayCharts);
        stopwatch.Stop();
        metrics.BuildDeltaMs += stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        if (!host.TryMoveLibraryChartFolderFileOnly(srcDir, dstDir))
        {
            stopwatch.Stop();
            metrics.MoveFileMs += stopwatch.ElapsedMilliseconds;
            metrics.MoveFailedCount++;
            return;
        }
        stopwatch.Stop();
        metrics.MoveFileMs += stopwatch.ElapsedMilliseconds;
        if (stopwatch.ElapsedMilliseconds >= AutoRenameBatchMetrics.SlowMoveLogThresholdMs)
        {
            metrics.SlowMoveCount++;
            host.LogInstallPerformance("auto_rename_folder_move slow op=" + metrics.OperationId
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds
                + " src=" + srcDir
                + " dst=" + dstDir
                + " pathChanges=" + delta.ChartPathChanges.Count
                + " folderPathChanges=" + delta.FolderPathChanges.Count);
        }
        movedSourceDirectories.Add(srcDir);
        movedFolders?.Add(new LibraryFolderPathChange
        {
            OldFolderPath = srcDir,
            NewFolderPath = dstDir
        });
        stopwatch.Restart();
        AppendLibraryMutationDelta(batchMutation, delta);
        stopwatch.Stop();
        metrics.AppendDeltaMs += stopwatch.ElapsedMilliseconds;
    }

    private void ApplyAutoRenameBatchChanges(
        LibraryMutationDelta batchMutation,
        List<LibraryFolderPathChange> movedFolders,
        AutoRenameBatchMetrics metrics)
    {
        Stopwatch tailStopwatch = Stopwatch.StartNew();
        long reverseLookupMs = 0;
        long mutationApplyMs = 0;
        int movedFolderCount = movedFolders?.Count ?? 0;
        host.LogInstallPerformance("auto_rename_folders_batch tail_start op=" + metrics.OperationId
            + " movedFolders=" + movedFolderCount
            + " pathChanges=" + (batchMutation?.ChartPathChanges.Count ?? 0)
            + " folderPathChanges=" + (batchMutation?.FolderPathChanges.Count ?? 0));
        try
        {
            if (movedFolders?.Count > 0)
            {
                Stopwatch reverseLookupStopwatch = Stopwatch.StartNew();
                MovedFolderReferenceUpdateResult updateResult = host.UpdateMovedFolderReferences(movedFolders);
                reverseLookupStopwatch.Stop();
                reverseLookupMs = reverseLookupStopwatch.ElapsedMilliseconds;
                host.LogInstallPerformance("auto_rename_folders_reverse_lookup done op=" + metrics.OperationId
                    + " moves=" + updateResult.MoveCount
                    + " lookupKeys=" + updateResult.LookupKeyCount
                    + " matchedKeys=" + updateResult.MatchedKeyCount
                    + " serviceElapsedMs=" + updateResult.ElapsedMs
                    + " elapsedMs=" + reverseLookupMs);
                host.LogReverseLookupMutationAndQueueWarmupIfNeeded("auto_rename_folders", updateResult.MutationResult);
            }
        }
        finally
        {
            if (HasLibraryMutationDeltaChanges(batchMutation))
            {
                Stopwatch mutationStopwatch = Stopwatch.StartNew();
                host.ApplyLibraryMutationDeltaWithPerformanceContext(batchMutation, "auto_rename_folders");
                mutationStopwatch.Stop();
                mutationApplyMs = mutationStopwatch.ElapsedMilliseconds;
            }
            tailStopwatch.Stop();
            host.LogInstallPerformance("auto_rename_folders_batch tail_done op=" + metrics.OperationId
                + " movedFolders=" + movedFolderCount
                + " reverseLookupMs=" + reverseLookupMs
                + " mutationApplyMs=" + mutationApplyMs
                + " elapsedMs=" + tailStopwatch.ElapsedMilliseconds);
        }
    }

    private static void AppendLibraryMutationDelta(LibraryMutationDelta target, LibraryMutationDelta source)
    {
        if (target == null || source == null)
        {
            return;
        }
        target.ChartRemoveRequests.AddRange(source.ChartRemoveRequests);
        target.AddedBmsFiles.AddRange(source.AddedBmsFiles);
        target.AddedBmsonSongs.AddRange(source.AddedBmsonSongs);
        target.ChartPathChanges.AddRange(source.ChartPathChanges);
        target.FolderPathChanges.AddRange(source.FolderPathChanges);
        target.UpdatedInstallDestinations.AddRange(source.UpdatedInstallDestinations);
        target.UpdatedInstalledPackagePaths.AddRange(source.UpdatedInstalledPackagePaths);
        target.Failures.AddRange(source.Failures);
        target.NotifyStorageRowPathChanges |= source.NotifyStorageRowPathChanges;
        target.RaiseInstalledPackagesChanged |= source.RaiseInstalledPackagesChanged;
        target.InvalidateInstalledDirectoryIndex |= source.InvalidateInstalledDirectoryIndex;
        target.InvalidateParentFolderCache |= source.InvalidateParentFolderCache;
        target.ClearDuplicatedCache |= source.ClearDuplicatedCache;
        target.RenamedCount += source.RenamedCount;
        target.DuplicateDeletedCount += source.DuplicateDeletedCount;
        target.SkippedCount += source.SkippedCount;
        target.TotalMs += source.TotalMs;
    }

    private static bool HasLibraryMutationDeltaChanges(LibraryMutationDelta delta)
    {
        return delta != null
            && (delta.ChartRemoveRequests.Count > 0
                || delta.ChartPathChanges.Count > 0
                || delta.FolderPathChanges.Count > 0
                || delta.UpdatedInstallDestinations.Count > 0
                || delta.UpdatedInstalledPackagePaths.Count > 0
                || delta.Failures.Count > 0
                || delta.NotifyStorageRowPathChanges
                || delta.RaiseInstalledPackagesChanged
                || delta.InvalidateInstalledDirectoryIndex
                || delta.InvalidateParentFolderCache
                || delta.ClearDuplicatedCache);
    }

    private sealed class AutoRenameBatchMetrics(long operationId, int planCount)
    {
        public const long SlowMoveLogThresholdMs = 500;

        public long OperationId { get; } = operationId;

        public int PlanCount { get; } = planCount;

        public int ProgressTotal { get; set; }

        public int ActionablePlanCount { get; set; }

        public int SkippedDuplicateSourceCount { get; set; }

        public int SkippedMissingSourceCount { get; set; }

        public int MoveFailedCount { get; set; }

        public int SlowMoveCount { get; set; }

        public long BuildDeltaMs { get; set; }

        public long MoveFileMs { get; set; }

        public long AppendDeltaMs { get; set; }

        public long MoveLoopMs { get; set; }
    }
}
