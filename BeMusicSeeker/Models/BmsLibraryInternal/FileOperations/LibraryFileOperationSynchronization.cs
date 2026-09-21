using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryFileOperationMutationBoundary
{
    LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage);

    bool TryBlockMutation(string operation, bool showMessage);
}

/// <summary>
/// Identifies the exclusive file-mutation lease that owns a nested catalog or
/// filesystem apply.  The capability is deliberately explicit: it is not
/// carried by an ambient execution context and cannot authorize a later
/// operation after either the capability or its lease has been disposed.
/// </summary>
internal sealed class LibraryFileMutationCapability : IDisposable
{
    private readonly object ownerIdentity;
    private readonly Func<bool> isLeaseActive;
    private int disposed;

    internal LibraryFileMutationCapability(
        object ownerIdentity,
        Func<bool> isLeaseActive)
    {
        this.ownerIdentity = ownerIdentity ?? throw new ArgumentNullException(nameof(ownerIdentity));
        this.isLeaseActive = isLeaseActive ?? throw new ArgumentNullException(nameof(isLeaseActive));
    }

    internal void Validate(object expectedOwner)
    {
        if (expectedOwner == null
            || !ReferenceEquals(ownerIdentity, expectedOwner)
            || Volatile.Read(ref disposed) != 0
            || !isLeaseActive())
        {
            throw new InvalidOperationException(
                "The file-mutation capability is not owned by the active lease.");
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
    }
}

/// <summary>
/// Exclusive logical lease returned by the mutation owner. Disposal first
/// invalidates all capabilities and then performs the short owner release.
/// </summary>
internal sealed class LibraryFileMutationLease : IDisposable
{
    private readonly Action release;
    private readonly object ownerIdentity;
    private readonly Func<bool> isActive;
    private int disposed;

    internal LibraryFileMutationLease(
        object ownerIdentity,
        Func<bool> isActive,
        Action release)
    {
        this.ownerIdentity = ownerIdentity ?? throw new ArgumentNullException(nameof(ownerIdentity));
        this.isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
    }

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public LibraryFileMutationCapability CreateMutationCapability()
    {
        if (!IsLive)
        {
            throw new InvalidOperationException("The file-mutation lease is no longer active.");
        }
        return new LibraryFileMutationCapability(
            ownerIdentity,
            () => IsLive);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            // Invalidation is represented by the disposed bit and is visible
            // to every nested capability before the owner release callback.
            // The callback is deliberately short and has no I/O or external
            // publication responsibility.
            release();
        }
    }

    private bool IsLive => Volatile.Read(ref disposed) == 0 && isActive();
}

/// <summary>
/// Owns the lock ordering and mutation reservation scopes for file operations.
/// Callers receive one disposable scope and never enumerate foreign locks.
/// </summary>
internal sealed class LibraryFileOperationSynchronization
{
    private readonly ILibraryFileOperationMutationBoundary mutationBoundary;

    private readonly CatalogFileOperationMutationBoundary catalogMutationBoundary;

    private readonly ReaderWriterLockSlimWrapper bmsFilesInitializedAll;

    private readonly ReaderWriterLockSlimWrapper bmsFilesInitializedMin;

    private readonly ReaderWriterLockSlimWrapper pendingInstallCharts;

    private readonly ReaderWriterLockSlimWrapper bmsFiles;

    /// <summary>
    /// Creates the file-operation synchronization owner with separate raw and
    /// catalog-gated mutation admission. The catalog boundary defaults to the
    /// raw boundary for isolated fixtures that do not compose readiness gating.
    /// </summary>
    /// <param name="mutationBoundary">Raw exclusive mutation boundary used by convergence, playlist, and pending-only flows.</param>
    /// <param name="bmsFilesInitializedAll">Lock protecting the fully initialized chart catalog.</param>
    /// <param name="bmsFilesInitializedMin">Lock protecting the minimal initialized chart catalog.</param>
    /// <param name="pendingInstallCharts">Lock protecting pending installation charts.</param>
    /// <param name="bmsFiles">Lock protecting the canonical chart collection.</param>
    /// <param name="catalogMutationBoundary">Catalog-dependent mutation boundary; omitted only by fixtures that intentionally use raw admission.</param>
    internal LibraryFileOperationSynchronization(
        ILibraryFileOperationMutationBoundary mutationBoundary,
        ReaderWriterLockSlimWrapper bmsFilesInitializedAll,
        ReaderWriterLockSlimWrapper bmsFilesInitializedMin,
        ReaderWriterLockSlimWrapper pendingInstallCharts,
        ReaderWriterLockSlimWrapper bmsFiles,
        CatalogFileOperationMutationBoundary catalogMutationBoundary = null)
    {
        this.mutationBoundary = mutationBoundary ?? throw new ArgumentNullException(nameof(mutationBoundary));
        this.catalogMutationBoundary = catalogMutationBoundary;
        this.bmsFilesInitializedAll = bmsFilesInitializedAll ?? throw new ArgumentNullException(nameof(bmsFilesInitializedAll));
        this.bmsFilesInitializedMin = bmsFilesInitializedMin ?? throw new ArgumentNullException(nameof(bmsFilesInitializedMin));
        this.pendingInstallCharts = pendingInstallCharts ?? throw new ArgumentNullException(nameof(pendingInstallCharts));
        this.bmsFiles = bmsFiles ?? throw new ArgumentNullException(nameof(bmsFiles));
    }

    internal LibraryFileMutationLease EnterFolderMoveWriteScope()
    {
        // Admission/reservation is intentionally separate from the short
        // model snapshot scope.  Filesystem staging, the DB callback,
        // finalize cleanup, and notifications must never retain collection or
        // model locks.
        return EnterCatalogMutationReservation("library_folder_move", showMessage: true);
    }

    internal IDisposable EnterFolderMoveSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => pendingInstallCharts.GetWriterGuard(),
            () => bmsFiles.GetWriterGuard());
    }

    internal IDisposable EnterFolderMoveReadScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    internal LibraryFileMutationLease EnterNormalInvalidExtensionRenameWriteScope()
    {
        return EnterCatalogWriteScope("library_invalid_extension_rename");
    }

    internal IDisposable EnterNormalInvalidExtensionRenameSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    internal LibraryFileMutationLease EnterPendingInvalidExtensionRenameWriteScope()
    {
        return EnterMutationReservation(
            "library_pending_invalid_extension_rename",
            showMessage: true);
    }

    internal IDisposable EnterPendingInvalidExtensionRenameSnapshotScope()
    {
        return AcquireScopes(
            () => pendingInstallCharts.GetReaderGuard());
    }

    internal LibraryFileMutationLease EnterLibraryChartRemovalWriteScope()
    {
        return EnterCatalogWriteScope("library_chart_removal");
    }

    internal IDisposable EnterLibraryChartRemovalSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => pendingInstallCharts.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    internal LibraryFileMutationLease EnterFixInstallationDirectoryWriteScope()
    {
        return EnterCatalogMutationReservationPreservingBusyNull(
            nameof(BMSLibrary.FixInstallationDirectoryCharts),
            showMessage: true);
    }

    internal IDisposable EnterFixInstallationDirectorySnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedAll.GetReaderGuard(),
            () => pendingInstallCharts.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    /// <summary>
    /// Reserves the single logical mutation lease used by duplicate merge.
    /// </summary>
    internal LibraryFileMutationLease EnterMergeWriteScope()
    {
        return EnterCatalogMutationReservationPreservingBusyNull(
            "duplicate_merge_catalog_transition",
            showMessage: true);
    }

    internal IDisposable EnterMergeSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => pendingInstallCharts.GetWriterGuard(),
            () => bmsFiles.GetWriterGuard());
    }

    /// <summary>
    /// Performs the cheap catalog-dependent mutation preflight without reserving
    /// the exclusive lease. The authoritative readiness check is repeated by the
    /// catalog boundary after reservation to close the preflight race.
    /// </summary>
    /// <param name="operation">Operation name used by mutation diagnostics.</param>
    /// <param name="showMessage">Whether a rejection should show its warning.</param>
    /// <returns><see langword="true"/> when the operation must be blocked.</returns>
    internal bool TryBlockCatalogMutation(string operation, bool showMessage)
        => catalogMutationBoundary != null
            ? catalogMutationBoundary.TryBlockMutation(operation, showMessage)
            : mutationBoundary.TryBlockMutation(operation, showMessage);

    internal LibraryFileMutationLease EnterWriteScope(string operation)
    {
        // Admission is intentionally the only long-lived scope.  Snapshot
        // locks are acquired by the operation immediately around its model
        // snapshot and are released before any filesystem, DB, cleanup, or
        // publication work begins.
        return EnterMutationReservation(operation, showMessage: true);
    }

    private LibraryFileMutationLease EnterMutationReservation(string operation, bool showMessage)
    {
        return mutationBoundary.TryBeginMutation(operation, showMessage);
    }

    private LibraryFileMutationLease EnterCatalogWriteScope(string operation)
    {
        return EnterCatalogMutationReservation(operation, showMessage: true);
    }

    private LibraryFileMutationLease EnterCatalogMutationReservation(string operation, bool showMessage)
    {
        return catalogMutationBoundary != null
            ? catalogMutationBoundary.TryBeginMutation(operation, showMessage)
            : mutationBoundary.TryBeginMutation(operation, showMessage);
    }

    private LibraryFileMutationLease EnterCatalogMutationReservationPreservingBusyNull(
        string operation,
        bool showMessage)
    {
        return catalogMutationBoundary != null
            ? catalogMutationBoundary.TryBeginMutationPreservingBusyNull(operation, showMessage)
            : mutationBoundary.TryBeginMutation(operation, showMessage);
    }

    private static IDisposable AcquireScopes(
        params Func<IDisposable>[] acquisitions)
    {
        return AcquireScopesWithExisting(new List<IDisposable>(), acquisitions);
    }

    private static IDisposable AcquireScopesWithExisting(
        IReadOnlyList<IDisposable> existingScopes,
        IReadOnlyList<Func<IDisposable>> acquisitions)
    {
        List<IDisposable> scopes = [.. (existingScopes ?? [])];
        try
        {
            foreach (Func<IDisposable> acquire in acquisitions ?? [])
            {
                scopes.Add(acquire());
            }
            return new CompositeDisposable(scopes);
        }
        catch
        {
            CompositeDisposable.DisposeScopesSafely(scopes);
            throw;
        }
    }

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IReadOnlyList<IDisposable> scopes;

        internal CompositeDisposable(params IDisposable[] scopes)
            : this((IReadOnlyList<IDisposable>)scopes)
        {
        }

        internal CompositeDisposable(IReadOnlyList<IDisposable> scopes)
        {
            this.scopes = scopes ?? [];
        }

        public void Dispose()
        {
            Exception firstException = null;
            for (int index = scopes.Count - 1; index >= 0; index--)
            {
                try
                {
                    scopes[index]?.Dispose();
                }
                catch (Exception exception)
                {
                    firstException ??= exception;
                }
            }
            if (firstException != null)
            {
                throw firstException;
            }
        }

        internal static void DisposeScopesSafely(IEnumerable<IDisposable> scopes)
        {
            try
            {
                new CompositeDisposable([.. (scopes ?? [])]).Dispose();
            }
            catch
            {
                // Preserve the acquisition failure while still attempting every release.
            }
        }
    }
}
