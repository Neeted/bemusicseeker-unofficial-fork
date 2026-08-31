using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable terminal facts for an automatic folder-rename batch.
/// </summary>
internal sealed class AutoRenameBatchResult
{
    internal AutoRenameBatchResult(
        bool hasActionablePlan,
        int appliedPlanCount,
        FileDbMutationBatchReceipt mutationReceipt)
    {
        HasActionablePlan = hasActionablePlan;
        AppliedPlanCount = appliedPlanCount;
        MutationReceipt = mutationReceipt ?? new FileDbMutationBatchReceipt([]);
    }

    internal bool HasActionablePlan { get; }

    internal int AppliedPlanCount { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    internal bool HasDurableCommit => MutationReceipt.HasDurableCommit;

    internal bool ManualRecoveryRequired => MutationReceipt.ManualRecoveryRequired;

    internal bool CompletedWithCleanupFailure => MutationReceipt.CompletedWithCleanupFailure;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt.RecoveryPaths;
}
