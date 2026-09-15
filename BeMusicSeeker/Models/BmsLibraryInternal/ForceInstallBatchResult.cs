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

    /// <summary>session required finalizer で install destination を一括 clear する package。</summary>
    internal List<ChartPackage> PackagesToClearInstallDestinations { get; } = [];

    /// <summary>operation-scoped install session の canonical terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; set; }

    /// <summary>強制導入前に見つかった immutable な宛先型衝突を取得します。</summary>
    public IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        SessionReceipt?.DestinationTypeConflicts ?? [];

    public bool HasDurableCommit => SessionReceipt?.DurableCommit == true;

    public bool ManualRecoveryRequired => SessionReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for any batch mutation.
    /// </summary>
    public bool HasDurableFinalizationFailure => SessionReceipt?.HasDurableFinalizationFailure == true;

    public bool CompletedWithCleanupFailure => SessionReceipt?.CompletedWithCleanupFailure == true;

    public IReadOnlyList<string> RecoveryPaths => SessionReceipt?.CandidatePaths ?? [];
}
