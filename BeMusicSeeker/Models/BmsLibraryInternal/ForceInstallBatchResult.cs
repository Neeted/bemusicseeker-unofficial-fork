using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class ForceInstallBatchResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int Succeeded { get; set; }

    public int Failed { get; set; }

    public int Skipped { get; set; }

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> DeferredInstalledPackages { get; } = [];

    public FileDbMutationBatchReceipt MutationReceipt { get; internal set; }

    public bool HasDurableCommit => MutationReceipt?.HasDurableCommit == true;

    public bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for any batch mutation.
    /// </summary>
    public bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;

    public bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    public IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];
}
