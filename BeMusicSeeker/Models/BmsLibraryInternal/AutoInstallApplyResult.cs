using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class AutoInstallApplyResult
{
    public List<ChartPackage> PendingPackagesToAdd { get; } = [];

    public List<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> AutoInstalledPackages { get; } = [];

    public List<ChartPackage> AutoInstallFailures { get; } = [];

    public List<ChartPackage> EstimateTargets { get; } = [];

    public List<ChartPackage> InstallRowsToUpsert { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

    /// <summary>
    /// Filesystem/DB terminal facts for the auto-install candidate batch.
    /// </summary>
    public FileDbMutationBatchReceipt MutationReceipt { get; internal set; }

    public bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for the candidate batch.
    /// </summary>
    public bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;

    public bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    public IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];

    public long InstallMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}

/// <summary>
/// Typed result returned by the canonical auto-install candidate executor.
/// </summary>
internal sealed class AutoInstallCandidateApplyResult
{
    internal AutoInstallCandidateApplyResult(
        IEnumerable<ChartPackage> failedPackages,
        FileDbMutationBatchReceipt mutationReceipt)
    {
        FailedPackages = [.. (failedPackages ?? []).Where(package => package != null)];
        MutationReceipt = mutationReceipt;
    }

    internal IReadOnlyList<ChartPackage> FailedPackages { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    internal bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for the candidate batch.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;
}
