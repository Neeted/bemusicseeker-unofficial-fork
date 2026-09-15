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

/// <summary>
/// Carries the detached package projection and executor-local physical receipt for a package move.
/// The receipt is an implementation detail of the physical phase; callers aggregate canonical
/// mutation facts into their operation-scoped session instead of exposing this receipt as the
/// user-operation terminal.
/// </summary>
internal sealed class PackagePhysicalMoveResult
{
    /// <summary>Creates immutable physical-phase output.</summary>
    /// <param name="executionResult">Detached destination projection prepared for the move.</param>
    /// <param name="physicalReceipt">Executor-local receipt describing the physical phase.</param>
    internal PackagePhysicalMoveResult(
        PackageInstallExecutionResult executionResult,
        FileDbMutationReceipt physicalReceipt)
    {
        ExecutionResult = executionResult;
        PhysicalReceipt = physicalReceipt ?? throw new ArgumentNullException(nameof(physicalReceipt));
    }

    /// <summary>Gets the detached destination projection produced by package preflight.</summary>
    internal PackageInstallExecutionResult ExecutionResult { get; }

    /// <summary>Gets the executor-local physical receipt. It is not an operation-scoped catalog receipt.</summary>
    internal FileDbMutationReceipt PhysicalReceipt { get; }
}

/// <summary>
/// 導入・フォルダ統合の session が canonical durable apply 後まで保持する prepared physical mutation です。
/// </summary>
internal sealed class PackageInstallSessionPhysicalMutation
{
    private readonly FileDbMutationPreparedCommit preparedCommit;
    private readonly Action applyLiveState;

    /// <summary>導入・統合の session が durable point 後まで所有する package physical mutation を作成します。</summary>
    /// <param name="preparedCommit">stage/promotion 済み executor state。</param>
    /// <param name="applyLiveState">canonical internal apply 成功後に package live state を進める callback。</param>
    /// <param name="destinationDirectory">operation-level resource scan に追加する成功 destination root。</param>
    internal PackageInstallSessionPhysicalMutation(
        FileDbMutationPreparedCommit preparedCommit,
        Action applyLiveState,
        string destinationDirectory)
    {
        this.preparedCommit = preparedCommit ?? throw new ArgumentNullException(nameof(preparedCommit));
        this.applyLiveState = applyLiveState;
        DestinationDirectory = destinationDirectory;
    }

    /// <summary>resource index の operation-scoped scan 対象 destination directory。</summary>
    internal string DestinationDirectory { get; }

    /// <summary>session terminal facts に追加する confirmed physical target。</summary>
    internal IReadOnlyList<LibraryMutationSessionTarget> ConfirmedTargets => preparedCommit.ConfirmedTargets;

    /// <summary>durable apply 前失敗時に保持する recovery candidate path。</summary>
    internal IReadOnlyList<string> RecoveryCandidatePaths => preparedCommit.RecoveryCandidatePaths;

    /// <summary>canonical durable apply 後に必要な live package state と source cleanup を確定します。</summary>
    /// <param name="applyLiveState">canonical internal apply が成功し live state を進めてよいかどうか。</param>
    /// <param name="finalizationFailure">先行する durable finalization failure。</param>
    internal FileDbMutationReceipt CompleteAfterDurableCommit(
        bool applyLiveState,
        Exception finalizationFailure = null)
    {
        Exception completionFailure = finalizationFailure;
        if (applyLiveState)
        {
            try
            {
                this.applyLiveState?.Invoke();
            }
            catch (Exception exception)
            {
                completionFailure = completionFailure == null
                    ? exception
                    : new AggregateException(completionFailure, exception);
            }
        }
        return preparedCommit.CompleteAfterDurableCommit(completionFailure);
    }

}

/// <summary>
/// 導入・統合の session 向けの一 package physical prepare 結果です。
/// </summary>
internal sealed class PackageInstallSessionMoveResult
{
    /// <summary>一 package の physical prepare 成否を session append 用に固定します。</summary>
    /// <param name="executionResult">成功時の detached install projection。</param>
    /// <param name="physicalMutation">成功時に session が durable point 後まで保持する mutation。</param>
    /// <param name="failureReceipt">prepare failure 時の executor-local receipt。</param>
    /// <param name="isPreflightRefusal">変更開始前に確定し、独立した後続 package を継続できる拒否かどうか。</param>
    internal PackageInstallSessionMoveResult(
        PackageInstallExecutionResult executionResult,
        PackageInstallSessionPhysicalMutation physicalMutation,
        FileDbMutationReceipt failureReceipt,
        bool isPreflightRefusal = false)
    {
        ExecutionResult = executionResult;
        PhysicalMutation = physicalMutation;
        FailureReceipt = failureReceipt;
        IsPreflightRefusal = isPreflightRefusal;
    }

    /// <summary>detached destination projection。</summary>
    internal PackageInstallExecutionResult ExecutionResult { get; }

    /// <summary>durable apply 後まで保持する prepared physical mutation。</summary>
    internal PackageInstallSessionPhysicalMutation PhysicalMutation { get; }

    /// <summary>physical prepare が成立しなかった場合の terminal receipt。</summary>
    internal FileDbMutationReceipt FailureReceipt { get; }

    /// <summary>例外型から推測せず、変更開始前の検証で確定した継続可能な拒否かどうか。</summary>
    internal bool IsPreflightRefusal { get; }

    /// <summary>physical prepare が成功したかどうか。</summary>
    internal bool Succeeded => ExecutionResult != null && PhysicalMutation != null && FailureReceipt == null;
}

internal sealed class PackageInstallExecutionResult
{
    public List<PackageChartEntry> AddedEntries { get; } = [];

    public List<ChartFile> AddedCharts { get; } = [];

    /// <summary>
    /// 同一packageのdurable callbackと後続semantic callbackで共有する導入targetです。
    /// DB用detached rowの再構築を繰り返さないための操作内cacheで、永続化されません。
    /// </summary>
    internal ChartStorageTargetSet InstalledTargetSet { get; set; }

    public List<ChartPackage> FailedPackages { get; } = [];

    public List<ChartPackage> InstalledPackagesToRegister { get; } = [];

    /// <summary>unexpected physical failure により package suffix の実行を停止したかどうか。</summary>
    internal bool StoppedByPhysicalFailure { get; set; }

    /// <summary>suffix 停止理由になった physical receipt。</summary>
    internal FileDbMutationReceipt PhysicalFailureReceipt { get; set; }

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
    /// <summary>Creates immutable command facts from the operation-scoped install session.</summary>
    /// <param name="registeredPackages">Packages durably registered before the terminal was returned.</param>
    /// <param name="sessionReceipt">Canonical install-session terminal facts.</param>
    internal PackageInstallCommandResult(
        IEnumerable<ChartPackage> registeredPackages,
        LibraryMutationSessionReceipt sessionReceipt)
    {
        RegisteredPackages = Array.AsReadOnly([.. (registeredPackages ?? []).Where(package => package != null)]);
        SessionReceipt = sessionReceipt ?? LibraryMutationSessionReceipt.Empty;
    }

    internal IReadOnlyList<ChartPackage> RegisteredPackages { get; }

    /// <summary>導入操作の canonical session 終端 facts。</summary>
    internal LibraryMutationSessionReceipt SessionReceipt { get; }

    /// <summary>パッケージ変更前に見つかった immutable な宛先型衝突を取得します。</summary>
    internal IReadOnlyList<FileDbMutationDestinationTypeConflict> DestinationTypeConflicts =>
        SessionReceipt.DestinationTypeConflicts;

    internal bool HasDurableCommit => SessionReceipt.DurableCommit;

    internal bool HasRequiredFailure => SessionReceipt.HasRequiredFailure;

    /// <summary>Gets whether a post-durable required finalizer failed for this command.</summary>
    internal bool HasDurableFinalizationFailure => SessionReceipt.HasDurableFinalizationFailure;

    internal bool ManualRecoveryRequired => SessionReceipt.ManualRecoveryRequired;

    internal bool CompletedWithCleanupFailure => SessionReceipt.CompletedWithCleanupFailure;

    /// <summary>Gets operation-scoped paths that may require confirmation or recovery.</summary>
    internal IReadOnlyList<string> RecoveryPaths => SessionReceipt.CandidatePaths;
}
