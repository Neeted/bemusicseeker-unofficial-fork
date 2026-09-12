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
    /// Publishes convergence after the authoritative scan diff and canonical
    /// catalog storage replacement have both completed successfully.
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
    private readonly Action<CatalogPathConvergenceBlockReason> showConvergenceRequiredWarning;

    /// <summary>
    /// Creates the admission owner that composes path-convergence readiness
    /// with the existing LR2/exclusive mutation boundary.
    /// </summary>
    /// <param name="lr2SynchronizationOwner">Existing exclusive mutation admission owner.</param>
    /// <param name="readinessOwner">Process-local catalog path-convergence state.</param>
    /// <param name="showConvergenceRequiredWarning">Interactive warning shown when convergence is not current.</param>
    internal CatalogFileMutationAdmissionOwner(
        BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner,
        CatalogFileMutationReadinessOwner readinessOwner,
        Action<CatalogPathConvergenceBlockReason> showConvergenceRequiredWarning)
    {
        this.lr2SynchronizationOwner = lr2SynchronizationOwner
            ?? throw new ArgumentNullException(nameof(lr2SynchronizationOwner));
        this.readinessOwner = readinessOwner
            ?? throw new ArgumentNullException(nameof(readinessOwner));
        this.showConvergenceRequiredWarning = showConvergenceRequiredWarning
            ?? throw new ArgumentNullException(nameof(showConvergenceRequiredWarning));
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
    /// Performs a non-reserving preflight check. The subsequent authoritative
    /// <see cref="TryBeginFileOperationMutation"/> call still re-checks readiness after it
    /// owns the existing exclusive mutation lease.
    /// </summary>
    /// <param name="operation">Operation name used by the shared mutation diagnostics.</param>
    /// <param name="showMessage">Whether convergence or existing busy-state preflight should show its warning.</param>
    internal bool TryBlockMutation(string operation, bool showMessage)
    {
        if (lr2SynchronizationOwner.TryBlockMutation(operation, showMessage))
        {
            return true;
        }
        if (readinessOwner.IsConverged)
        {
            return false;
        }
        CatalogPathConvergenceBlockReason blockReason = readinessOwner.BlockReason;
        if (showMessage)
        {
            showConvergenceRequiredWarning(blockReason);
        }
        return true;
    }

    /// <summary>
    /// Reserves a file-operation lease and then performs the authoritative
    /// catalog-path readiness check while that lease is held. The caller may
    /// retain its legacy busy-race terminal while readiness rejection remains a
    /// distinct null result after the acquired lease has been released.
    /// </summary>
    /// <param name="operation">Operation name used by the shared mutation diagnostics.</param>
    /// <param name="showMessage">Whether a convergence rejection should show its warning after the lease is released.</param>
    /// <param name="showBusyMessage">Whether the existing mutation owner should show its busy warning before returning null.</param>
    /// <param name="throwOnBusyRace">Whether failure to acquire the raw exclusive lease is surfaced as the legacy busy exception instead of a null terminal.</param>
    internal LibraryFileMutationLease TryBeginFileOperationMutation(
        string operation,
        bool showMessage,
        bool showBusyMessage = true,
        bool throwOnBusyRace = true)
    {
        LibraryFileMutationLease lease = lr2SynchronizationOwner.TryBeginMutation(operation, showBusyMessage);
        if (lease == null)
        {
            if (throwOnBusyRace)
            {
                throw new InvalidOperationException(Resources.Warn_Lr2SongDbSyncRunning);
            }
            return null;
        }
        if (readinessOwner.IsConverged)
        {
            return lease;
        }

        CatalogPathConvergenceBlockReason blockReason = readinessOwner.BlockReason;
        lease.Dispose();
        if (showMessage)
        {
            showConvergenceRequiredWarning(blockReason);
        }
        return null;
    }
}
