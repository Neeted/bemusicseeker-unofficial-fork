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
    private readonly LibraryMutationOwner host;

    internal AutoRenameBatchCoordinator(LibraryMutationOwner host)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Applies the supplied plans through one operation-scoped mutation session and returns
    /// durable, diagnostic, and primary failure facts. Physical moves remain item-local, while
    /// canonical apply, reverse lookup, and publication are aggregated for the confirmed set.
    /// </summary>
    /// <param name="plans">Precomputed rename plans owned by the current command.</param>
    /// <param name="mutationCapability">外側のfolder mutation leaseが保持するlive capability。</param>
    /// <param name="postLeaseNotifications">Publications released only after the outer lease exits.</param>
    /// <param name="progressReporter">Optional item-level progress reporter.</param>
    /// <returns>LR2 同期を含む操作単位の session 結果と、解放後に公開する診断。</returns>
    internal AutoRenameBatchResult ApplyWithSessionReceipt(
        IEnumerable<FolderAutoRenamePlan> plans,
        LibraryFileMutationCapability mutationCapability,
        ICollection<Action> postLeaseNotifications,
        Action<int, int, string> progressReporter = null)
    {
        ArgumentNullException.ThrowIfNull(mutationCapability);
        ArgumentNullException.ThrowIfNull(postLeaseNotifications);
        long operationId = Stopwatch.GetTimestamp();
        var totalStopwatch = Stopwatch.StartNew();
        List<FolderAutoRenamePlan> planList = [.. (plans ?? []).Where(plan => plan != null)];
        int appliedPlanCount = 0;
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
        var moveLoopStopwatch = Stopwatch.StartNew();
        ExceptionDispatchInfo primaryFailure = null;
        LibraryMutationOwner.LibraryMutationSession session = host.BeginLibraryMutationSession(
            mutationCapability,
            "auto_rename_folders",
            postLeaseNotifications);

        if (planList.Any(IsDriveRootSourcePlan))
        {
            diagnostics.Add(new AutoRenameBatchDiagnostic(
                AutoRenameBatchDiagnosticKind.DriveRootSkipped));
        }

        for (int planIndex = 0; planIndex < planList.Count; planIndex++)
        {
            FolderAutoRenamePlan plan = planList[planIndex];
            bool reportProgress = IsAutoRenameProgressPlan(plan);
            string sourceDirectory = plan.SourceDirectory;
            string destinationDirectory = plan.DestinationDirectory;
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
                if (string.IsNullOrWhiteSpace(destinationDirectory) || string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    continue;
                }
                string newName = host.NormalizeAutoRenameFolderName(Path.GetFileName(destinationDirectory));
                if (string.IsNullOrWhiteSpace(newName)
                    || Path.GetPathRoot(sourceDirectory).Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (movedSourceDirectories.Contains(sourceDirectory))
                {
                    metrics.SkippedDuplicateSourceCount++;
                    continue;
                }
                if (!host.DirectoryExists(sourceDirectory))
                {
                    metrics.SkippedMissingSourceCount++;
                    diagnostics.Add(new AutoRenameBatchDiagnostic(
                        AutoRenameBatchDiagnosticKind.SourceMissing,
                        sourceDirectory: sourceDirectory));
                    continue;
                }
                destinationDirectory = Path.Combine(Path.GetDirectoryName(sourceDirectory), newName);
                if (host.EntryExists(destinationDirectory))
                {
                    diagnostics.Add(new AutoRenameBatchDiagnostic(
                        AutoRenameBatchDiagnosticKind.DestinationAlreadyExists,
                        sourceDirectory: sourceDirectory,
                        destinationDirectory: destinationDirectory));
                    metrics.MoveFailedCount++;
                    continue;
                }
                metrics.ActionablePlanCount++;

                var buildDeltaStopwatch = Stopwatch.StartNew();
                LibraryFolderMoveFacts mutationFacts = null;
                host.RunWithFolderMoveSnapshotLocks(() =>
                {
                    mutationFacts = host.BuildFolderMoveFacts(
                        sourceDirectory,
                        destinationDirectory,
                        unregister: false,
                        notifyStorageRowPathChanges: false);
                });
                buildDeltaStopwatch.Stop();
                metrics.BuildDeltaMs += buildDeltaStopwatch.ElapsedMilliseconds;

                var moveStopwatch = Stopwatch.StartNew();
                try
                {
                    host.MoveFolderPhysical(sourceDirectory, destinationDirectory);
                }
                finally
                {
                    moveStopwatch.Stop();
                    metrics.MoveFileMs += moveStopwatch.ElapsedMilliseconds;
                }

                var appendDeltaStopwatch = Stopwatch.StartNew();
                session.AppendFolderMove(sourceDirectory, destinationDirectory, mutationFacts);
                appendDeltaStopwatch.Stop();
                metrics.AppendDeltaMs += appendDeltaStopwatch.ElapsedMilliseconds;

                appliedPlanCount++;
                movedSourceDirectories.Add(sourceDirectory);

                if (moveStopwatch.ElapsedMilliseconds >= AutoRenameBatchMetrics.SlowMoveLogThresholdMs)
                {
                    metrics.SlowMoveCount++;
                    diagnostics.Add(new AutoRenameBatchDiagnostic(
                        AutoRenameBatchDiagnosticKind.PerformanceLog,
                        message: "auto_rename_folder_move slow op=" + metrics.OperationId
                        + " elapsedMs=" + moveStopwatch.ElapsedMilliseconds
                        + " src=" + sourceDirectory
                        + " dst=" + destinationDirectory));
                }
            }
            catch (Exception exception)
            {
                metrics.MoveFailedCount++;
                diagnostics.Add(new AutoRenameBatchDiagnostic(
                    AutoRenameBatchDiagnosticKind.MoveFailed,
                    sourceDirectory: sourceDirectory,
                    destinationDirectory: destinationDirectory,
                    failure: exception));
                primaryFailure ??= ExceptionDispatchInfo.Capture(exception);
                session.RecordStoppedSuffix(
                    sourceDirectory,
                    destinationDirectory,
                    exception,
                    CreateRemainingTargets(planList, planIndex + 1));
                break;
            }
            finally
            {
                if (reportProgress)
                {
                    progressProcessed = Math.Min(progressProcessed + 1, progressTotal);
                    ReportAutoRenameProgress(
                        progressReporter,
                        progressTotal,
                        progressProcessed,
                        plan.SourceDirectory,
                        diagnostics);
                }
            }
        }

        moveLoopStopwatch.Stop();
        metrics.MoveLoopMs = moveLoopStopwatch.ElapsedMilliseconds;

        var sessionCommitStopwatch = Stopwatch.StartNew();
        LibraryMutationSessionReceipt sessionReceipt = session.Commit();
        sessionCommitStopwatch.Stop();
        metrics.SessionCommitMs = sessionCommitStopwatch.ElapsedMilliseconds;
        Exception sessionFailure = sessionReceipt.ApplyFailure ?? sessionReceipt.FinalizationFailure;
        if (sessionFailure != null)
        {
            primaryFailure ??= ExceptionDispatchInfo.Capture(sessionFailure);
        }

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
            + " hasActionablePlan=" + (appliedPlanCount > 0)
            + " movedFolders=" + appliedPlanCount
            + " confirmedChanges=" + sessionReceipt.ConfirmedChangeCount
            + " sessionCommitMs=" + metrics.SessionCommitMs
            + " totalMs=" + totalStopwatch.ElapsedMilliseconds));

        return new AutoRenameBatchResult(
            appliedPlanCount > 0,
            appliedPlanCount,
            sessionReceipt,
            diagnostics,
            primaryFailure);
    }

    private static IReadOnlyList<LibraryMutationSessionTarget> CreateRemainingTargets(
        IReadOnlyList<FolderAutoRenamePlan> plans,
        int startIndex)
    {
        var targets = new List<LibraryMutationSessionTarget>();
        for (int index = Math.Max(startIndex, 0); index < (plans?.Count ?? 0); index++)
        {
            FolderAutoRenamePlan plan = plans[index];
            if (plan == null)
            {
                continue;
            }
            targets.Add(new LibraryMutationSessionTarget(
                plan.SourceDirectory,
                plan.DestinationDirectory));
        }
        return targets;
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

        public long SessionCommitMs { get; set; }
    }
}
