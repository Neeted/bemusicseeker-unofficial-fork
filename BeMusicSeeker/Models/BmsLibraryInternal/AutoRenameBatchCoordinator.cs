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
        return ApplyWithReceipts(plans, progressReporter).HasActionablePlan;
    }

    internal AutoRenameBatchResult ApplyWithReceipts(
        IEnumerable<FolderAutoRenamePlan> plans,
        Action<int, int, string> progressReporter = null)
    {
        long operationId = Stopwatch.GetTimestamp();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        List<FolderAutoRenamePlan> planList = [.. (plans ?? []).Where(plan => plan != null)];
        bool hasActionablePlan = false;
        int appliedPlanCount = 0;
        List<FileDbMutationReceipt> mutationReceipts = [];
        var metrics = new AutoRenameBatchMetrics(operationId, planList.Count);
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
                    string srcDir = plan.SourceDirectory;
                    string newName = host.NormalizeAutoRenameFolderName(Path.GetFileName(plan.DestinationDirectory));
                    if (string.IsNullOrWhiteSpace(newName)
                        || Path.GetPathRoot(srcDir).Equals(srcDir, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (movedSourceDirectories.Contains(srcDir))
                    {
                        metrics.SkippedDuplicateSourceCount++;
                        continue;
                    }
                    if (!host.DirectoryExists(srcDir))
                    {
                        metrics.SkippedMissingSourceCount++;
                        host.ShowRenameFolderNotExists(srcDir);
                        continue;
                    }
                    string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
                    if (host.EntryExists(dstDir))
                    {
                        host.ShowMoveDestinationAlreadyExists(srcDir, dstDir);
                        metrics.MoveFailedCount++;
                        continue;
                    }
                    metrics.ActionablePlanCount++;

                    FileDbMutationPlan mutationPlan = null;
                    LibraryMutationDelta mutationDelta = null;
                    host.RunWithFolderMoveSnapshotLocks(() =>
                    {
                        mutationPlan = host.BuildFolderMoveMutationPlan(srcDir, dstDir);
                        mutationDelta = host.BuildFolderMoveDelta(srcDir, dstDir, false, false);
                    });
                    Stopwatch moveStopwatch = Stopwatch.StartNew();
                    FileDbMutationReceipt mutationReceipt = host.CreateFileDbMutationExecutor(mutationPlan).Execute(() =>
                    {
                        FileDbMutationCommitResult databaseResult = host.ApplyLibraryMutationDeltaForFileMutation(
                            mutationDelta,
                            "auto_rename_folder",
                            suppressNormalRefreshNotification: true);
                        if (!databaseResult.DurableCommit)
                        {
                            return databaseResult;
                        }
                        return FileDbMutationCommitResult.Durable(
                            () =>
                            {
                                try
                                {
                                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                                        host.MoveFolderReferencesAfterCommit(srcDir, dstDir);
                                    host.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                                        "auto_rename_folders",
                                        reverseLookupMutation);
                                }
                                finally
                                {
                                    databaseResult.PostCommit?.Invoke();
                                }
                            },
                            databaseResult.Failure);
                    });
                    moveStopwatch.Stop();
                    metrics.MoveFileMs += moveStopwatch.ElapsedMilliseconds;
                    mutationReceipts.Add(mutationReceipt);
                    if (!mutationReceipt.DurableCommit)
                    {
                        metrics.MoveFailedCount++;
                        host.ShowFolderMoveFailed(
                            srcDir,
                            dstDir,
                            mutationReceipt.Failure ?? new IOException("Folder move failed."));
                        if (mutationReceipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired)
                        {
                            break;
                        }
                        continue;
                    }
                    hasActionablePlan = true;
                    appliedPlanCount++;
                    movedSourceDirectories.Add(srcDir);
                    if (moveStopwatch.ElapsedMilliseconds >= AutoRenameBatchMetrics.SlowMoveLogThresholdMs)
                    {
                        metrics.SlowMoveCount++;
                        host.LogInstallPerformance("auto_rename_folder_move slow op=" + metrics.OperationId
                            + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds
                            + " src=" + srcDir
                            + " dst=" + dstDir);
                    }
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
            if (appliedPlanCount > 0)
            {
                // Each plan has its own durable catalog receipt, but a batch
                // exposes one normal refresh barrier to preserve the command's
                // historical observable boundary.
                host.PublishAutoRenameBatchRefreshNotification();
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
                + " movedFolders=" + appliedPlanCount
                + " skippedDuplicateSource=" + metrics.SkippedDuplicateSourceCount
                + " skippedMissingSource=" + metrics.SkippedMissingSourceCount
                + " moveFailed=" + metrics.MoveFailedCount
                + " slowMoves=" + metrics.SlowMoveCount
                + " buildDeltaMs=" + metrics.BuildDeltaMs
                + " moveFileMs=" + metrics.MoveFileMs
                + " appendDeltaMs=" + metrics.AppendDeltaMs
                + " elapsedMs=" + metrics.MoveLoopMs);
            totalStopwatch.Stop();
            host.LogInstallPerformance("auto_rename_folders_batch done op=" + operationId
                + " hasActionablePlan=" + hasActionablePlan
                + " movedFolders=" + appliedPlanCount
                + " mutationReceipts=" + mutationReceipts.Count
                + " totalMs=" + totalStopwatch.ElapsedMilliseconds);
        }
        return new AutoRenameBatchResult(
            hasActionablePlan,
            appliedPlanCount,
            new FileDbMutationBatchReceipt(mutationReceipts));
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
