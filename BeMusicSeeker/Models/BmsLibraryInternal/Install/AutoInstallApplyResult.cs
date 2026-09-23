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

    /// <summary>operation-scoped install session の canonical terminal facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; set; }

    public bool ManualRecoveryRequired => SessionReceipt?.ManualRecoveryRequired == true;

    /// <summary>
    /// Gets whether a post-durable finalizer failed for the candidate batch.
    /// </summary>
    public bool HasDurableFinalizationFailure => SessionReceipt?.HasDurableFinalizationFailure == true;

    public bool CompletedWithCleanupFailure => SessionReceipt?.CompletedWithCleanupFailure == true;

    /// <summary>操作の session が保持する復旧候補を取得します。</summary>
    public IReadOnlyList<string> RecoveryPaths => SessionReceipt?.CandidatePaths
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
    /// <summary>候補の physical prepare 結果を、操作内の成功依存の判定へ渡します。</summary>
    /// <param name="failedPackages">physical prepare が成立しなかった package。</param>
    /// <param name="stoppedByPhysicalFailure">予期しない失敗により後続候補を停止するかどうか。</param>
    /// <param name="physicalFailureReceipt">後続停止の原因と保全対象を示す局所結果。</param>
    /// <param name="successfulCharts">所有判定へ追加してよい、physical success が確定した譜面。</param>
    internal AutoInstallCandidateApplyResult(
        IEnumerable<ChartPackage> failedPackages,
        bool stoppedByPhysicalFailure = false,
        FileDbMutationReceipt physicalFailureReceipt = null,
        IEnumerable<ChartFile> successfulCharts = null)
    {
        FailedPackages = [.. (failedPackages ?? []).Where(package => package != null)];
        StoppedByPhysicalFailure = stoppedByPhysicalFailure;
        PhysicalFailureReceipt = physicalFailureReceipt;
        SuccessfulPrimaryHashes = Array.AsReadOnly([.. (successfulCharts ?? [])
            .Select(ChartLookupKey.GetPrimaryHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    internal IReadOnlyList<ChartPackage> FailedPackages { get; }

    /// <summary>候補の physical prepare failure により同じ operation の後続処理を停止したかどうか。</summary>
    internal bool StoppedByPhysicalFailure { get; }

    /// <summary>suffix 停止理由になった physical mutation receipt。</summary>
    internal FileDbMutationReceipt PhysicalFailureReceipt { get; }

    /// <summary>後続候補の duplicate 判定へ重ねる、実際に physical success した primary hash。</summary>
    internal IReadOnlyList<string> SuccessfulPrimaryHashes { get; }
}
