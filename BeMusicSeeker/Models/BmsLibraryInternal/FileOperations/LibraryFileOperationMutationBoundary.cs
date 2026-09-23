using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Supplies the canonical LR2 mutation admission to the file-operation
/// synchronization owner without retaining the BMSLibrary aggregate.
/// </summary>
internal sealed class LibraryFileOperationMutationBoundary : ILibraryFileOperationMutationBoundary
{
    private readonly BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner;

    internal LibraryFileOperationMutationBoundary(
        BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner)
    {
        this.lr2SynchronizationOwner = lr2SynchronizationOwner ?? throw new ArgumentNullException(nameof(lr2SynchronizationOwner));
    }

    public LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage)
    {
        return lr2SynchronizationOwner.TryBeginMutation(operation, showMessage);
    }

    public bool TryBlockMutation(string operation, bool showMessage)
    {
        return lr2SynchronizationOwner.TryBlockMutation(operation, showMessage);
    }
}

/// <summary>
/// Adds catalog-path convergence admission only to library operations whose
/// filesystem target or catalog membership is derived from the current chart
/// catalog. Pending-only and playlist operations keep using the raw boundary.
/// </summary>
internal sealed class CatalogFileOperationMutationBoundary : ILibraryFileOperationMutationBoundary
{
    private readonly CatalogFileMutationAdmissionOwner mutationAdmissionOwner;

    /// <summary>
    /// Creates a boundary for catalog-dependent library file operations while
    /// leaving the shared raw mutation boundary available to other workflows.
    /// </summary>
    /// <param name="mutationAdmissionOwner">Admission owner that composes the exclusive lease with path-convergence readiness.</param>
    internal CatalogFileOperationMutationBoundary(
        CatalogFileMutationAdmissionOwner mutationAdmissionOwner)
    {
        this.mutationAdmissionOwner = mutationAdmissionOwner ?? throw new ArgumentNullException(nameof(mutationAdmissionOwner));
    }

    /// <summary>
    /// Acquires the shared exclusive mutation lease and, while it is held,
    /// admits the operation only when catalog-path convergence is current.
    /// </summary>
    /// <param name="operation">Operation name used by the shared mutation diagnostics.</param>
    /// <param name="showMessage">Whether a convergence rejection should show its warning.</param>
    /// <returns>The owned mutation lease, or <see langword="null"/> when convergence is not current.</returns>
    public LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage)
    {
        return mutationAdmissionOwner.TryBeginFileOperationMutation(operation, showMessage);
    }

    /// <summary>
    /// Acquires the catalog-gated lease while retaining callers whose legacy
    /// busy-race terminal is a null/non-applied result rather than an exception.
    /// Readiness is still checked only after the raw exclusive lease is owned.
    /// </summary>
    /// <param name="operation">Operation name used by the shared mutation diagnostics.</param>
    /// <param name="showMessage">Whether busy or convergence rejection should show its warning.</param>
    /// <returns>The owned mutation lease, or <see langword="null"/> when busy or not converged.</returns>
    internal LibraryFileMutationLease TryBeginMutationPreservingBusyNull(
        string operation,
        bool showMessage)
    {
        return mutationAdmissionOwner.TryBeginFileOperationMutation(
            operation,
            showMessage,
            showBusyMessage: showMessage,
            throwOnBusyRace: false);
    }

    /// <summary>
    /// Performs the non-reserving catalog-path admission preflight used before
    /// expensive or interactive work; authoritative admission is still repeated
    /// after the exclusive lease is acquired.
    /// </summary>
    /// <param name="operation">Operation name used by the shared mutation diagnostics.</param>
    /// <param name="showMessage">Whether a rejection should show its warning.</param>
    /// <returns><see langword="true"/> when the operation must be blocked.</returns>
    public bool TryBlockMutation(string operation, bool showMessage)
    {
        return mutationAdmissionOwner.TryBlockMutation(operation, showMessage);
    }
}
