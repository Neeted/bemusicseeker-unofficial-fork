using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

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

    /// <summary>
    /// Applies the supplied plans and returns durable, diagnostic, and primary
    /// failure facts. The command-owned post-lease effect collection receives
    /// public notifications; dialogs, logs, and authoritative publication
    /// remain deferred until the outer lease has been released.
    /// </summary>
    /// <param name="mutationCapability">外側のfolder mutation leaseが保持するlive capability。</param>
    internal AutoRenameBatchResult ApplyWithReceipts(
        IEnumerable<FolderAutoRenamePlan> plans,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        Action<int, int, string> progressReporter = null)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        long operationId = Stopwatch.GetTimestamp();
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        List<FolderAutoRenamePlan> planList = [.. (plans ?? []).Where(plan => plan != null)];
        bool hasActionablePlan = false;
        int appliedPlanCount = 0;
        List<FileDbMutationReceipt> mutationReceipts = [];
        List<Lr2NormalFolderPathChange> lr2NormalFolderPathChanges = [];
        List<AutoRenameBatchDiagnostic> diagnostics = [];
        var metrics = new AutoRenameBatchMetrics(operationId, planList.Count);
        HashSet<string> movedSourceDirectories = new(StringComparer.OrdinalIgnoreCase);
        int progressTotal = CountAutoRenameProgressPlans(planList);
        metrics.ProgressTotal = progressTotal;
        int progressProcessed = 0;
        diagnostics.Add(new AutoRenameBatchDiagnostic(
            AutoRenameBatchDiagnosticKind.PerformanceLog,
            message: "auto_rename_folders_batch start op=" + operationId
            + " planCount=" + planList.Count
            + " progressTotal=" + progressTotal));
        ReportAutoRenameProgress(
            progressReporter,
            progressTotal,
            progressProcessed,
            string.Empty,
            diagnostics);
        Stopwatch moveLoopStopwatch = Stopwatch.StartNew();
        ExceptionDispatchInfo primaryFailure = null;
        try
        {
            if (planList.Any(IsDriveRootSourcePlan))
            {
                diagnostics.Add(new AutoRenameBatchDiagnostic(
                    AutoRenameBatchDiagnosticKind.DriveRootSkipped));
            }
            foreach (FolderAutoRenamePlan plan in planList)
            {
                bool reportProgress = IsAutoRenameProgressPlan(plan);
                try
                {
                    if (plan.FailureException != null)
                    {
                        diagnostics.Add(new AutoRenameBatchDiagnostic(
                            AutoRenameBatchDiagnosticKind.RenamePlanFailed,
                            sourceDirectory: plan.SourceDirectory,
                            failure: plan.FailureException));
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
                        diagnostics.Add(new AutoRenameBatchDiagnostic(
                            AutoRenameBatchDiagnosticKind.SourceMissing,
                            sourceDirectory: srcDir));
                        continue;
                    }
                    string dstDir = Path.Combine(Path.GetDirectoryName(srcDir), newName);
                    if (host.EntryExists(dstDir))
                    {
                        diagnostics.Add(new AutoRenameBatchDiagnostic(
                            AutoRenameBatchDiagnosticKind.DestinationAlreadyExists,
                            sourceDirectory: srcDir,
                            destinationDirectory: dstDir));
                        metrics.MoveFailedCount++;
                        continue;
                    }
                    metrics.ActionablePlanCount++;

                    FileDbMutationPlan mutationPlan = null;
                    LibraryFolderMoveFacts mutationFacts = null;
                    host.RunWithFolderMoveSnapshotLocks(() =>
                    {
                        mutationPlan = host.BuildFolderMoveMutationPlan(srcDir, dstDir);
                        mutationFacts = host.BuildFolderMoveFacts(srcDir, dstDir, false, false);
                    });
                    Stopwatch moveStopwatch = Stopwatch.StartNew();
                    FileDbMutationCommitResult databaseResult = null;
                    List<Action> mutationPostLeaseNotifications = [];
                    FileDbMutationReceipt mutationReceipt = host.CreateFileDbMutationExecutor(mutationPlan).Execute(() =>
                    {
                        databaseResult = host.ApplyLibraryMutationFactsForFileMutation(
                            mutationFacts.CatalogFacts,
                            mutationFacts.PackageReferenceFacts,
                            "auto_rename_folder",
                            mutationCapability,
                            mutationPostLeaseNotifications.Add,
                            suppressNormalRefreshNotification: true,
                            suppressLr2NormalFolderSync: true,
                            storageRowPathNotificationPolicy: mutationFacts.StorageRowPathNotificationPolicy);
                        if (!databaseResult.DurableCommit)
                        {
                            return databaseResult;
                        }
                        return FileDbMutationCommitResult.Durable(
                            () =>
                            {
                                if (databaseResult.Failure == null)
                                {
                                    databaseResult.DurableFinalizer?.Invoke();
                                    DirectoryResourceLookupCache.ReverseLookupMutationResult reverseLookupMutation =
                                        host.MoveFolderReferencesAfterCommit(srcDir, dstDir);
                                    foreach (Action notification in mutationPostLeaseNotifications)
                                    {
                                        postLeaseNotifications.Add(notification);
                                    }
                                    postLeaseNotifications.Add(() => host.LogReverseLookupMutationAndQueueWarmupIfNeeded(
                                        "auto_rename_folders",
                                        reverseLookupMutation));
                                }
                            },
                            databaseResult.Failure);
                    });
                    mutationReceipts.Add(mutationReceipt);
                    moveStopwatch.Stop();
                    metrics.MoveFileMs += moveStopwatch.ElapsedMilliseconds;
                    // The outer command owns lease release.  Receipt effects
                    // are flushed by that command only after the lease exits.
                    if (!mutationReceipt.DurableCommit
                        || mutationReceipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                    {
                        metrics.MoveFailedCount++;
                        diagnostics.Add(new AutoRenameBatchDiagnostic(
                            AutoRenameBatchDiagnosticKind.MoveFailed,
                            sourceDirectory: srcDir,
                            destinationDirectory: dstDir,
                            failure: mutationReceipt.Failure ?? new IOException("Folder move failed.")));
                        if (mutationReceipt.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed)
                        {
                            primaryFailure ??= ExceptionDispatchInfo.Capture(
                                mutationReceipt.Failure ?? new IOException("Folder move finalization failed."));
                            break;
                        }
                        if (mutationReceipt.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired)
                        {
                            break;
                        }
                        continue;
                    }
                    hasActionablePlan = true;
                    appliedPlanCount++;
                    movedSourceDirectories.Add(srcDir);
                    lr2NormalFolderPathChanges.AddRange((mutationFacts?.CatalogFacts?.ChartPathChanges ?? [])
                        .Where(change => change?.Chart?.GetBmsStorageOwner() != null
                            && !string.IsNullOrWhiteSpace(change.OldPath)
                            && !string.IsNullOrWhiteSpace(change.NewPath))
                        .Select(change => new Lr2NormalFolderPathChange(change.OldPath, change.NewPath)));
                    if (databaseResult?.Failure != null)
                    {
                        primaryFailure ??= ExceptionDispatchInfo.Capture(databaseResult.Failure);
                        diagnostics.Add(new AutoRenameBatchDiagnostic(
                            AutoRenameBatchDiagnosticKind.MoveFailed,
                            sourceDirectory: srcDir,
                            destinationDirectory: dstDir,
                            failure: databaseResult.Failure));
                        break;
                    }
                    if (moveStopwatch.ElapsedMilliseconds >= AutoRenameBatchMetrics.SlowMoveLogThresholdMs)
                    {
                        metrics.SlowMoveCount++;
                        diagnostics.Add(new AutoRenameBatchDiagnostic(
                            AutoRenameBatchDiagnosticKind.PerformanceLog,
                            message: "auto_rename_folder_move slow op=" + metrics.OperationId
                            + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds
                            + " src=" + srcDir
                            + " dst=" + dstDir));
                    }
                }
                finally
                {
                    if (reportProgress)
                    {
                        progressProcessed = Math.Min(progressProcessed + 1, progressTotal);
                        int reportedProgressProcessed = progressProcessed;
                        string reportedPath = plan.SourceDirectory;
                        ReportAutoRenameProgress(
                            progressReporter,
                            progressTotal,
                            reportedProgressProcessed,
                            reportedPath,
                            diagnostics);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            moveLoopStopwatch.Stop();
            metrics.MoveLoopMs = moveLoopStopwatch.ElapsedMilliseconds;
            diagnostics.Add(new AutoRenameBatchDiagnostic(
                AutoRenameBatchDiagnosticKind.PerformanceLog,
                message: "auto_rename_folders_batch move_loop_done op=" + operationId
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
                + " elapsedMs=" + metrics.MoveLoopMs));
            totalStopwatch.Stop();
            diagnostics.Add(new AutoRenameBatchDiagnostic(
                AutoRenameBatchDiagnosticKind.PerformanceLog,
                message: "auto_rename_folders_batch done op=" + operationId
                + " hasActionablePlan=" + hasActionablePlan
                + " movedFolders=" + appliedPlanCount
                + " mutationReceipts=" + mutationReceipts.Count
                + " totalMs=" + totalStopwatch.ElapsedMilliseconds));
        }
        return new AutoRenameBatchResult(
            hasActionablePlan,
            appliedPlanCount,
            new FileDbMutationBatchReceipt(mutationReceipts),
            lr2NormalFolderPathChanges,
            diagnostics,
            primaryFailure);
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

    private static void ReportAutoRenameProgress(
        Action<int, int, string> progressReporter,
        int total,
        int processed,
        string currentPath,
        ICollection<AutoRenameBatchDiagnostic> diagnostics)
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
            diagnostics?.Add(new AutoRenameBatchDiagnostic(
                AutoRenameBatchDiagnosticKind.ProgressReportFailed,
                failure: ex,
                total: total,
                processed: processed));
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
