using System.Collections.Generic;
using System;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// receipt 対応 package source cleanup に与える削除同意です。
/// </summary>
internal enum PackageSourceCleanupPolicy
{
    /// <summary>
    /// 現在の mutation plan が消費したファイルだけを削除します。
    /// </summary>
    PreserveUnconsumedContents,

    /// <summary>
    /// 残存譜面の primary hash と範囲外 path を個別確認できた場合だけ、
    /// 残存譜面も削除します。
    /// </summary>
    DeleteVerifiedResidualContents,

    /// <summary>
    /// 統合元が既存 catalog の所持 chart である場合に限り、source 範囲内の
    /// 所持実体を安全判定の保護対象から除外します。外部 copy または確定済み
    /// destination による独立所有の証拠は引き続き必要です。
    /// </summary>
    MergeOwnedSourceContents
}

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
