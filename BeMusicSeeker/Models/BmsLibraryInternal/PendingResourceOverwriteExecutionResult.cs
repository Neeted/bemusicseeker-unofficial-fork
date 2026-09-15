using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingResourceOverwriteExecutionResult
{
    public int Requested { get; set; }

    public int Processed { get; set; }

    public int SucceededInstall { get; set; }

    public int SucceededCleanupOnly { get; set; }

    public int SkippedNotPending { get; set; }

    public int SkippedMissingInstlDst { get; set; }

    public int SkippedMultiDestination { get; set; }

    public int SkippedNoComponentTarget { get; set; }

    public int Failed { get; set; }

    public bool Canceled { get; set; }

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    /// <summary>session required finalizer で install destination を一括 clear する package。</summary>
    internal List<ChartPackage> PackagesToClearInstallDestinations { get; } = [];

    /// <summary>durable success 後の collection finalizer で installed collection に反映する package。</summary>
    internal List<ChartPackage> DeferredInstalledPackages { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

    /// <summary>operation-scoped install session の canonical terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; set; }

    /// <summary>
    /// Deferred authoritative package/catalog publications.  The outer
    /// overwrite command owns their invocation after its lease is released.
    /// </summary>
    public List<Action> PostLeaseEffects { get; } = [];

    public bool HasDurableCommit => SessionReceipt?.DurableCommit == true;

    public bool ManualRecoveryRequired => SessionReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for this overwrite batch.
    /// </summary>
    public bool HasDurableFinalizationFailure => SessionReceipt?.HasDurableFinalizationFailure == true;

    public bool CompletedWithCleanupFailure => SessionReceipt?.CompletedWithCleanupFailure == true;

    public IReadOnlyList<string> RecoveryPaths => SessionReceipt?.CandidatePaths ?? [];

    public PendingInstalledOnlyResourceOverwriteResult ToPublicResult()
    {
        return new PendingInstalledOnlyResourceOverwriteResult
        {
            Requested = Requested,
            Processed = Processed,
            SucceededInstall = SucceededInstall,
            SucceededCleanupOnly = SucceededCleanupOnly,
            SkippedNotPending = SkippedNotPending,
            SkippedMissingInstlDst = SkippedMissingInstlDst,
            SkippedMultiDestination = SkippedMultiDestination,
            SkippedNoComponentTarget = SkippedNoComponentTarget,
            Failed = Failed,
            Canceled = Canceled,
            HasDurableCommit = HasDurableCommit,
            ManualRecoveryRequired = ManualRecoveryRequired,
            HasDurableFinalizationFailure = HasDurableFinalizationFailure,
            CompletedWithCleanupFailure = CompletedWithCleanupFailure,
            RecoveryPaths = RecoveryPaths,
            SessionReceipt = SessionReceipt
        };
    }
}
