using System.Collections.Generic;
namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchResult
{
    public HashSet<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> DeferredInstalledPackages { get; } = [];

    /// <summary>session required finalizer で install destination を一括 clear する package。</summary>
    internal List<ChartPackage> PackagesToClearInstallDestinations { get; } = [];

    public List<ChartFile> DeferredMaintenanceCharts { get; } = [];

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

    public FileDbMutationBatchReceipt MutationReceipt { get; internal set; }

    /// <summary>operation-scoped install session の canonical terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; set; }

    /// <summary>保留項目の変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    public IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        SessionReceipt?.DestinationTypeConflicts ?? MutationReceipt?.DestinationTypeConflicts ?? [];

    public bool HasDurableCommit => SessionReceipt?.DurableCommit ?? (MutationReceipt?.HasDurableCommit == true);

    public bool ManualRecoveryRequired => SessionReceipt?.ManualRecoveryRequired ?? (MutationReceipt?.ManualRecoveryRequired == true);

    /// <summary>
    /// Gets whether a post-durable finalizer failed for any pending install.
    /// </summary>
    public bool HasDurableFinalizationFailure => SessionReceipt?.HasDurableFinalizationFailure ?? (MutationReceipt?.HasDurableFinalizationFailure == true);

    public bool CompletedWithCleanupFailure => SessionReceipt?.CompletedWithCleanupFailure ?? (MutationReceipt?.CompletedWithCleanupFailure == true);

    public IReadOnlyList<string> RecoveryPaths => SessionReceipt?.CandidatePaths ?? MutationReceipt?.RecoveryPaths ?? [];

    public int CleanupOnlySucceeded { get; set; }

    public int CleanupOnlyFailed { get; set; }

    public int CleanupOnlyMissingSource { get; set; }

    public long MoveMs { get; set; }

    public long SongDbMs { get; set; }

    public long MaintenanceMs { get; set; }

    public long ZeroNoteMs { get; set; }

    public long ScoreMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}
