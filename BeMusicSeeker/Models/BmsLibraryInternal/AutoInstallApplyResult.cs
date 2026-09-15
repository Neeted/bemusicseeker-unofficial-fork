using System;
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

    /// <summary>operation-scoped install session の canonical terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; set; }

    public bool ManualRecoveryRequired => SessionReceipt?.ManualRecoveryRequired
        ?? (MutationReceipt?.ManualRecoveryRequired == true);

    /// <summary>
    /// Gets whether a post-durable finalizer failed for the candidate batch.
    /// </summary>
    public bool HasDurableFinalizationFailure => SessionReceipt?.HasDurableFinalizationFailure
        ?? (MutationReceipt?.HasDurableFinalizationFailure == true);

    public bool CompletedWithCleanupFailure => SessionReceipt?.CompletedWithCleanupFailure
        ?? (MutationReceipt?.CompletedWithCleanupFailure == true);

    /// <summary>Gets session recovery candidates, falling back to legacy batch facts for legacy callers.</summary>
    public IReadOnlyList<string> RecoveryPaths => SessionReceipt?.CandidatePaths
        ?? MutationReceipt?.RecoveryPaths
        ?? [];

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
        FileDbMutationBatchReceipt mutationReceipt,
        bool stoppedByPhysicalFailure = false,
        FileDbMutationReceipt physicalFailureReceipt = null,
        IEnumerable<ChartFile> successfulCharts = null)
    {
        FailedPackages = [.. (failedPackages ?? []).Where(package => package != null)];
        MutationReceipt = mutationReceipt;
        StoppedByPhysicalFailure = stoppedByPhysicalFailure;
        PhysicalFailureReceipt = physicalFailureReceipt;
        SuccessfulPrimaryHashes = Array.AsReadOnly([.. (successfulCharts ?? [])
            .Select(ChartLookupKey.GetPrimaryHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    internal IReadOnlyList<ChartPackage> FailedPackages { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    /// <summary>候補の physical prepare failure により同じ operation の後続処理を停止したかどうか。</summary>
    internal bool StoppedByPhysicalFailure { get; }

    /// <summary>suffix 停止理由になった physical mutation receipt。</summary>
    internal FileDbMutationReceipt PhysicalFailureReceipt { get; }

    /// <summary>後続候補の duplicate 判定へ重ねる、実際に physical success した primary hash。</summary>
    internal IReadOnlyList<string> SuccessfulPrimaryHashes { get; }

    internal bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for the candidate batch.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;
}
