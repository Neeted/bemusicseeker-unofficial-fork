using System.Collections.Generic;
using System;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PackageInstallExecutionResult
{
    public List<PackageChartEntry> AddedEntries { get; } = [];

    public List<ChartFile> AddedCharts { get; } = [];

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<ChartPackage> InstalledPackagesToRegister { get; } = [];

    /// <summary>
    /// Per-package filesystem/DB receipts.  This remains immutable after the
    /// package loop completes so partial durable progress is observable.
    /// </summary>
    public FileDbMutationBatchReceipt MutationReceipt { get; internal set; }

    /// <summary>
    /// Gets whether a post-durable finalizer failed for this package batch.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt?.HasDurableFinalizationFailure == true;

    /// <summary>
    /// Pending install-row path consumed by this package, if one exists.
    /// </summary>
    public string InstallPathToDelete { get; internal set; }

    public long MoveMs { get; set; }

    public long SongDbMs { get; set; }

    public long MaintenanceMs { get; set; }

    public long ScoreMs { get; set; }

    public long ApplyMs { get; set; }

    public long TotalMs { get; set; }
}

/// <summary>
/// Immutable terminal facts for the package-install command seam.
/// </summary>
internal sealed class PackageInstallCommandResult
{
    internal PackageInstallCommandResult(
        IEnumerable<ChartPackage> registeredPackages,
        FileDbMutationBatchReceipt mutationReceipt)
    {
        RegisteredPackages = Array.AsReadOnly([.. (registeredPackages ?? []).Where(package => package != null)]);
        MutationReceipt = mutationReceipt ?? new FileDbMutationBatchReceipt([]);
    }

    internal IReadOnlyList<ChartPackage> RegisteredPackages { get; }

    internal FileDbMutationBatchReceipt MutationReceipt { get; }

    internal bool HasDurableCommit => MutationReceipt.HasDurableCommit;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for this command.
    /// </summary>
    internal bool HasDurableFinalizationFailure => MutationReceipt.HasDurableFinalizationFailure;

    internal bool ManualRecoveryRequired => MutationReceipt.ManualRecoveryRequired;

    internal bool CompletedWithCleanupFailure => MutationReceipt.CompletedWithCleanupFailure;

    internal IReadOnlyList<string> RecoveryPaths => MutationReceipt.RecoveryPaths;
}
