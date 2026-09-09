using System.Collections.Generic;
namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingInstallBatchResult
{
    public HashSet<ChartPackage> PendingPackagesToRemove { get; } = [];

    public List<ChartPackage> DeferredInstalledPackages { get; } = [];

    public List<ChartFile> DeferredMaintenanceCharts { get; } = [];

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<string> InstallRowsToDelete { get; } = [];

    public FileDbMutationBatchReceipt MutationReceipt { get; internal set; }

    /// <summary>保留項目の変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    public IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        MutationReceipt?.DestinationTypeConflicts ?? [];

    public bool HasDurableCommit => MutationReceipt?.HasDurableCommit == true;

    public bool ManualRecoveryRequired => MutationReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for any pending install.
    /// </summary>
    public bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;

    public bool CompletedWithCleanupFailure => MutationReceipt?.CompletedWithCleanupFailure == true;

    public IReadOnlyList<string> RecoveryPaths => MutationReceipt?.RecoveryPaths ?? [];

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
