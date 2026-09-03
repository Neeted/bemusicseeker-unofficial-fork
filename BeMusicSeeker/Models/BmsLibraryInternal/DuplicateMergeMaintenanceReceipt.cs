using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable facts returned by the internal duplicate-folder merge route.
/// The public <c>MergeChartDirectory</c> compatibility entry point remains void;
/// this receipt exists so maintenance dispatch behavior can be verified without
/// observing private state or changing the merge failure contract.
/// </summary>
internal sealed class DuplicateMergeMaintenanceReceipt
{
    internal static DuplicateMergeMaintenanceReceipt NotApplied { get; } =
        new(
            mergeApplied: false,
            intermediateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            maintenanceResult: MaintenanceWorkflowResultFacts.From(null),
            intermediateDeferred: false,
            maintenanceHadUpdates: false,
            resourceHealthIndexDeferred: false,
            resourceHealthIndexDeltaApplied: false,
            resourceHealthIndexFullRebuilt: false,
            mutationReceipt: null);

    internal DuplicateMergeMaintenanceReceipt(
        bool mergeApplied,
        ResourceHealthIndexUpdateMode intermediateMode,
        MaintenanceWorkflowResultFacts maintenanceResult,
        bool intermediateDeferred,
        bool maintenanceHadUpdates,
        bool resourceHealthIndexDeferred,
        bool resourceHealthIndexDeltaApplied,
        bool resourceHealthIndexFullRebuilt,
        FileDbMutationReceipt mutationReceipt = null)
    {
        MergeApplied = mergeApplied;
        IntermediateMode = intermediateMode;
        MaintenanceResult = maintenanceResult ?? MaintenanceWorkflowResultFacts.From(null);
        IntermediateDeferred = intermediateDeferred;
        MaintenanceHadUpdates = maintenanceHadUpdates;
        ResourceHealthIndexDeferred = resourceHealthIndexDeferred;
        ResourceHealthIndexDeltaApplied = resourceHealthIndexDeltaApplied;
        ResourceHealthIndexFullRebuilt = resourceHealthIndexFullRebuilt;
        MutationReceipt = mutationReceipt;
    }

    /// <summary>
    /// Gets whether the source operation completed its filesystem and catalog merge.
    /// </summary>
    internal bool MergeApplied { get; }

    /// <summary>
    /// Gets the resource-health update mode requested for the merge's intermediate maintenance.
    /// </summary>
    internal ResourceHealthIndexUpdateMode IntermediateMode { get; }

    /// <summary>
    /// Gets whether the intermediate resource-health update was deferred.
    /// </summary>
    internal bool IntermediateDeferred { get; }

    /// <summary>
    /// Gets whether catalog maintenance reported updates.
    /// </summary>
    internal bool MaintenanceHadUpdates { get; }

    /// <summary>
    /// Gets the immutable maintenance facts returned by the catalog maintenance owner.
    /// </summary>
    internal MaintenanceWorkflowResultFacts MaintenanceResult { get; }

    /// <summary>
    /// Gets whether resource-health dispatch deferred the update.
    /// </summary>
    internal bool ResourceHealthIndexDeferred { get; }

    /// <summary>
    /// Gets whether resource-health dispatch applied a targeted delta.
    /// </summary>
    internal bool ResourceHealthIndexDeltaApplied { get; }

    /// <summary>
    /// Gets whether resource-health dispatch rebuilt the full index.
    /// </summary>
    internal bool ResourceHealthIndexFullRebuilt { get; }

    /// <summary>
    /// Gets the immutable filesystem/DB terminal receipt for the merge.
    /// </summary>
    internal FileDbMutationReceipt MutationReceipt { get; }

    internal bool HasDurableCommit => MutationReceipt?.DurableCommit == true;

    /// <summary>
    /// Gets whether the merge finalizer failed after the file and catalog
    /// state became durable.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt?.TerminalState == FileDbMutationTerminalState.DurableFinalizationFailed;

    internal bool ManualRecoveryRequired => MutationReceipt?.TerminalState == FileDbMutationTerminalState.ManualRecoveryRequired;

    internal bool CompletedWithCleanupFailure => MutationReceipt?.TerminalState == FileDbMutationTerminalState.CompletedWithCleanupFailure;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];
}
