using System;
using System.Threading;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Explains why catalog-dependent file mutation admission is currently closed.
/// Keep this intentionally small: startup scan disablement needs actionable UI
/// guidance, while exceptional convergence failures already have their own
/// diagnostics and share the generic fallback warning.
/// </summary>
internal enum CatalogPathConvergenceBlockReason
{
    Other = 0,
    StartupFileScanDisabled = 1
}

/// <summary>
/// Tracks whether the currently loaded catalog generation has completed an
/// authoritative file-diff convergence pass. This state is process-local and
/// is deliberately reset whenever catalog loading or a file-diff refresh starts.
/// </summary>
internal sealed class CatalogFileMutationReadinessOwner
{
    private const int StateUnverified = 0;
    private const int StateStartupFileScanDisabled = 1;
    private const int StateConverged = 2;

    private int state;

    /// <summary>
    /// Gets whether catalog-dependent file mutations may be admitted for the
    /// currently loaded catalog generation.
    /// </summary>
    internal bool IsConverged => Volatile.Read(ref state) == StateConverged;

    /// <summary>
    /// Gets the compact reason used only to select the user-facing warning when
    /// admission is closed.
    /// </summary>
    internal CatalogPathConvergenceBlockReason BlockReason =>
        Volatile.Read(ref state) == StateStartupFileScanDisabled
            ? CatalogPathConvergenceBlockReason.StartupFileScanDisabled
            : CatalogPathConvergenceBlockReason.Other;

    /// <summary>
    /// Invalidates the current convergence fact before a catalog load or
    /// authoritative file-diff attempt begins.
    /// </summary>
    /// <param name="reason">Reason retained only while this generation remains unverified.</param>
    internal void Reset(CatalogPathConvergenceBlockReason reason = CatalogPathConvergenceBlockReason.Other)
    {
        Volatile.Write(
            ref state,
            reason == CatalogPathConvergenceBlockReason.StartupFileScanDisabled
                ? StateStartupFileScanDisabled
                : StateUnverified);
    }

    /// <summary>
    /// 完全走査の正本・索引と、入口が必要とするLR2・参照・表示の終端成功後に収束を公開します。
    /// 部分解除や保存だけの成功では呼び出しません。
    /// </summary>
    internal void MarkConverged()
    {
        Volatile.Write(ref state, StateConverged);
    }
}

/// <summary>
/// Adds catalog-path convergence admission to the existing exclusive file
/// mutation lease without changing the lease used by convergence operations
/// themselves.
/// </summary>
internal sealed class CatalogFileMutationAdmissionOwner
{
    private readonly BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner;
    private readonly CatalogFileMutationReadinessOwner readinessOwner;
    private readonly Func<CatalogPathConvergenceBlockReason, BMSLibrary.OperationDialogMessage> createConvergenceRequiredWarning;

    /// <summary>
    /// Creates the admission owner that composes path-convergence readiness
    /// with the existing LR2/exclusive mutation boundary.
    /// </summary>
    /// <param name="lr2SynchronizationOwner">Existing exclusive mutation admission owner.</param>
    /// <param name="readinessOwner">Process-local catalog path-convergence state.</param>
    /// <param name="createConvergenceRequiredWarning">収束が現在でないときの警告の案内を作る処理。案内は呼出元が渡した操作の収集先へ加えます。</param>
    internal CatalogFileMutationAdmissionOwner(
        BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner,
        CatalogFileMutationReadinessOwner readinessOwner,
        Func<CatalogPathConvergenceBlockReason, BMSLibrary.OperationDialogMessage> createConvergenceRequiredWarning)
    {
        this.lr2SynchronizationOwner = lr2SynchronizationOwner
            ?? throw new ArgumentNullException(nameof(lr2SynchronizationOwner));
        this.readinessOwner = readinessOwner
            ?? throw new ArgumentNullException(nameof(readinessOwner));
        this.createConvergenceRequiredWarning = createConvergenceRequiredWarning
            ?? throw new ArgumentNullException(nameof(createConvergenceRequiredWarning));
    }

    /// <summary>
    /// Gets whether catalog-path convergence is current while a caller already
    /// owns the shared exclusive mutation lease.
    /// </summary>
    internal bool IsConverged => readinessOwner.IsConverged;

    /// <summary>
    /// Gets the currently retained user-facing block reason.
    /// </summary>
    internal CatalogPathConvergenceBlockReason BlockReason => readinessOwner.BlockReason;

    /// <summary>
    /// 予約しない事前検査です。受理済み権限は同じ管理主体へ照合し、本受付では収束状態を再確認します。
    /// </summary>
    /// <param name="operation">変更操作の診断名。</param>
    /// <param name="notices">拒否警告を加える、この操作の収集先。nullは警告を求めません。表示は所有者が受付の解放後に行います。</param>
    /// <param name="capability">同じ共通受付の生存権限。nullは新規要求の競合判定です。</param>
    internal bool TryBlockMutation(string operation, LibraryOperationNotices notices, LibraryFileMutationCapability capability = null)
    {
        if (lr2SynchronizationOwner.TryBlockMutation(operation, capability))
        {
            return true;
        }
        if (readinessOwner.IsConverged)
        {
            return false;
        }
        CatalogPathConvergenceBlockReason blockReason = readinessOwner.BlockReason;
        notices?.Add(createConvergenceRequiredWarning(blockReason));
        return true;
    }

    /// <summary>
    /// Reserves a file-operation lease and then performs the authoritative
    /// catalog-path readiness check while that lease is held. The caller may
    /// retain its legacy busy-race terminal while readiness rejection remains a
    /// distinct null result after the acquired lease has been released.
    /// </summary>
    /// <param name="operation">Operation name used by the shared mutation diagnostics.</param>
    /// <param name="notices">拒否警告を加える、この操作の収集先。nullは警告を求めません。表示は所有者が受付の解放後に行います。</param>
    /// <param name="throwOnBusyRace">Whether failure to acquire the raw exclusive lease is surfaced as the legacy busy exception instead of a null terminal.</param>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease TryBeginFileOperationMutation(
        string operation,
        LibraryOperationNotices notices,
        bool throwOnBusyRace = true, LibraryFileMutationCapability capability = null)
    {
        LibraryFileMutationLease lease = lr2SynchronizationOwner.TryBeginMutation(operation, capability);
        if (lease == null)
        {
            if (throwOnBusyRace)
            {
                throw new InvalidOperationException(Resources.Warn_LibraryOperationBusy);
            }
            return null;
        }
        if (readinessOwner.IsConverged)
        {
            return lease;
        }

        CatalogPathConvergenceBlockReason blockReason = readinessOwner.BlockReason;
        lease.Dispose();
        notices?.Add(createConvergenceRequiredWarning(blockReason));
        return null;
    }
}
