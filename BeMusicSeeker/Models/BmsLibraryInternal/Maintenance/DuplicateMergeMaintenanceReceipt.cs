using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// フォルダ統合とその post-commit maintenance を一つの user operation として返します。
/// canonical terminal は session receipt とし、merge の確定済み成功を後続 failure と区別します。
/// </summary>
internal sealed class DuplicateMergeMaintenanceReceipt
{
    /// <summary>統合を開始しなかった操作の空の結果。</summary>
    internal static DuplicateMergeMaintenanceReceipt NotApplied { get; } =
        new(
            mergeApplied: false,
            intermediateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates,
            maintenanceResult: MaintenanceWorkflowResultFacts.From(null),
            intermediateDeferred: false,
            maintenanceHadUpdates: false,
            resourceHealthIndexDeferred: false,
            resourceHealthIndexDeltaApplied: false,
            resourceHealthIndexFullRebuilt: false,
            sessionReceipt: LibraryMutationSessionReceipt.Empty);

    /// <summary>merge の確定結果、保守結果、同じ session の終端を固定します。</summary>
    internal DuplicateMergeMaintenanceReceipt(
        bool mergeApplied,
        ResourceHealthIndexUpdateMode intermediateMode,
        MaintenanceWorkflowResultFacts maintenanceResult,
        bool intermediateDeferred,
        bool maintenanceHadUpdates,
        bool resourceHealthIndexDeferred,
        bool resourceHealthIndexDeltaApplied,
        bool resourceHealthIndexFullRebuilt,
        LibraryMutationSessionReceipt sessionReceipt)
    {
        MergeApplied = mergeApplied;
        IntermediateMode = intermediateMode;
        MaintenanceResult = maintenanceResult ?? MaintenanceWorkflowResultFacts.From(null);
        IntermediateDeferred = intermediateDeferred;
        MaintenanceHadUpdates = maintenanceHadUpdates;
        ResourceHealthIndexDeferred = resourceHealthIndexDeferred;
        ResourceHealthIndexDeltaApplied = resourceHealthIndexDeltaApplied;
        ResourceHealthIndexFullRebuilt = resourceHealthIndexFullRebuilt;
        SessionReceipt = sessionReceipt ?? throw new ArgumentNullException(nameof(sessionReceipt));
    }

    /// <summary>
    /// filesystem と catalog の統合が確定したかどうか。後続 maintenance だけの失敗では true を維持します。
    /// </summary>
    internal bool MergeApplied { get; }

    /// <summary>
    /// Gets the resource-health update mode requested for the merge's intermediate maintenance.
    /// </summary>
    internal ResourceHealthIndexUpdateMode IntermediateMode { get; }

    /// <summary>
    /// Gets whether the intermediate resource-health update was deferred.
    /// </summary>
    internal bool IntermediateDeferred { get; }

    /// <summary>
    /// Gets whether catalog maintenance reported updates.
    /// </summary>
    internal bool MaintenanceHadUpdates { get; }

    /// <summary>
    /// Gets the immutable maintenance facts returned by the catalog maintenance owner.
    /// </summary>
    internal MaintenanceWorkflowResultFacts MaintenanceResult { get; }

    /// <summary>
    /// Gets whether resource-health dispatch deferred the update.
    /// </summary>
    internal bool ResourceHealthIndexDeferred { get; }

    /// <summary>
    /// Gets whether resource-health dispatch applied a targeted delta.
    /// </summary>
    internal bool ResourceHealthIndexDeltaApplied { get; }

    /// <summary>
    /// Gets whether resource-health dispatch rebuilt the full index.
    /// </summary>
    internal bool ResourceHealthIndexFullRebuilt { get; }

    /// <summary>
    /// 統合から post-commit maintenance までの immutable な session terminal を取得します。
    /// </summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; }

    /// <summary>マージ変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        SessionReceipt.DestinationTypeConflicts;

    /// <summary>統合の canonical durable point に到達したかどうか。</summary>
    internal bool HasDurableCommit => SessionReceipt.DurableCommit;

    /// <summary>
    /// durable 確定後の required internal apply または maintenance が失敗したかどうか。
    /// </summary>
    internal bool HasDurableFinalizationFailure => SessionReceipt.HasDurableFinalizationFailure;

    /// <summary>physical phase に手動確認が必要な失敗があるかどうか。</summary>
    internal bool ManualRecoveryRequired => SessionReceipt.ManualRecoveryRequired;

    /// <summary>durable success を保った cleanup failure があるかどうか。</summary>
    internal bool CompletedWithCleanupFailure => SessionReceipt.CompletedWithCleanupFailure;

    /// <summary>session に保持した手動確認・復旧候補 path。</summary>
    internal IReadOnlyList<string> RecoveryPaths => SessionReceipt.CandidatePaths;
}
