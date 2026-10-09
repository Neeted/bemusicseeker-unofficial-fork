using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ILibraryFileOperationMutationBoundary
{
    /// <summary>新要求は共通受付を非待機で取得し、受理済み権限は検査して借用します。Busyはnullです。</summary>
    LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null);

    /// <summary>生存権限を検査し、新要求の副作用前に共通受付のBusyを判定します。</summary>
    bool TryBlockMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null);
}

/// <summary>
/// 共通受付の生存するownerに属する明示権限です。内部のfile/catalog/LR2継続へ渡します。
/// ambient contextへ載せず、権限または発行leaseの解放後は変更を認めません。
/// </summary>
internal sealed class LibraryFileMutationCapability : IDisposable
{
    private readonly object ownerIdentity;
    private readonly Func<bool> isLeaseActive;
    private int disposed;

    internal LibraryFileMutationCapability(
        object ownerIdentity,
        Func<bool> isLeaseActive, LibraryFileMutationCapability playlistCapability = null)
    {
        this.ownerIdentity = ownerIdentity ?? throw new ArgumentNullException(nameof(ownerIdentity));
        this.isLeaseActive = isLeaseActive ?? throw new ArgumentNullException(nameof(isLeaseActive));
        PlaylistCapability = playlistCapability;
    }

    /// <summary>同じ受理要求が保持するPの明示権限。消費先がPのownerと生存を検査します。</summary>
    internal LibraryFileMutationCapability PlaylistCapability { get; }

    /// <summary>Lの寿命を延長せず、既に取得したPの権限を内部継続へ一緒に渡します。</summary>
    internal LibraryFileMutationCapability WithPlaylistCapability(LibraryFileMutationCapability playlistCapability)
        => new(ownerIdentity, () => Volatile.Read(ref disposed) == 0 && isLeaseActive(), playlistCapability);

    /// <summary>権限自身と発行元の共通leaseが生存し、同じ受付ownerに属するか検査します。</summary>
    internal bool IsValidFor(object expectedOwner) => expectedOwner != null
        && ReferenceEquals(ownerIdentity, expectedOwner) && Volatile.Read(ref disposed) == 0 && isLeaseActive();

    /// <summary>共通受付のownerと権限・leaseの生存を検査し、不一致・失効は例外にします。</summary>
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
/// 共通の論理受付を所有するlease、または受理済み継続の借用leaseです。
/// 解放は一回だけ権限を失効させ、所有leaseだけが短い共通受付の解放を行います。
/// </summary>
internal sealed class LibraryFileMutationLease : IDisposable
{
    private readonly Action release;
    private readonly object ownerIdentity;
    private readonly Func<bool> isActive;
    private readonly LibraryFileMutationCapability playlistCapability;
    private int disposed;

    internal LibraryFileMutationLease(
        object ownerIdentity,
        Func<bool> isActive,
        Action release, LibraryFileMutationCapability playlistCapability = null)
    {
        this.ownerIdentity = ownerIdentity ?? throw new ArgumentNullException(nameof(ownerIdentity));
        this.isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.playlistCapability = playlistCapability;
    }

    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>生存leaseの明示権限を発行します。lease解放後は発行した全権限も無効です。</summary>
    public LibraryFileMutationCapability CreateMutationCapability()
    {
        if (!IsLive)
        {
            throw new InvalidOperationException("The file-mutation lease is no longer active.");
        }
        return new LibraryFileMutationCapability(
            ownerIdentity,
            () => IsLive, playlistCapability);
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

    /// <summary>対象の物理変更に必要な共通受付を取得、または受理済み権限で借用します。呼出元が後片付け後に解放します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterFolderMoveWriteScope(LibraryFileMutationCapability capability = null)
    {
        // Admission/reservation is intentionally separate from the short
        // model snapshot scope.  Filesystem staging, the DB callback,
        // finalize cleanup, and notifications must never retain collection or
        // model locks.
        return EnterCatalogMutationReservation("library_folder_move", showMessage: true, capability: capability);
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

    /// <summary>対象の物理変更に必要な共通受付を取得、または受理済み権限で借用します。呼出元が後片付け後に解放します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterNormalInvalidExtensionRenameWriteScope(LibraryFileMutationCapability capability = null)
    {
        return EnterCatalogWriteScope("library_invalid_extension_rename", capability);
    }

    internal IDisposable EnterNormalInvalidExtensionRenameSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    /// <summary>対象の物理変更に必要な共通受付を取得、または受理済み権限で借用します。呼出元が後片付け後に解放します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterPendingInvalidExtensionRenameWriteScope(LibraryFileMutationCapability capability = null)
    {
        return EnterMutationReservation(
            "library_pending_invalid_extension_rename",
            showMessage: true, capability: capability);
    }

    internal IDisposable EnterPendingInvalidExtensionRenameSnapshotScope()
    {
        return AcquireScopes(
            () => pendingInstallCharts.GetReaderGuard());
    }

    /// <summary>対象の物理変更に必要な共通受付を取得、または受理済み権限で借用します。呼出元が後片付け後に解放します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterLibraryChartRemovalWriteScope(LibraryFileMutationCapability capability = null)
    {
        return EnterCatalogWriteScope("library_chart_removal", capability);
    }

    internal IDisposable EnterLibraryChartRemovalSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => pendingInstallCharts.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    /// <summary>対象の物理変更に必要な共通受付を取得、または受理済み権限で借用します。呼出元が後片付け後に解放します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterFixInstallationDirectoryWriteScope(LibraryFileMutationCapability capability = null)
    {
        return EnterCatalogMutationReservationPreservingBusyNull(
            nameof(BMSLibrary.FixInstallationDirectoryCharts),
            showMessage: true, capability: capability);
    }

    internal IDisposable EnterFixInstallationDirectorySnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedAll.GetReaderGuard(),
            () => pendingInstallCharts.GetReaderGuard(),
            () => bmsFiles.GetReaderGuard());
    }

    /// <summary>
    /// 重複統合の共通受付を取得または借用します。呼出元が実変更と後片付けの終端で解放します。
    /// </summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterMergeWriteScope(LibraryFileMutationCapability capability = null)
    {
        return EnterCatalogMutationReservationPreservingBusyNull(
            "duplicate_merge_catalog_transition",
            showMessage: true, capability: capability);
    }

    internal IDisposable EnterMergeSnapshotScope()
    {
        return AcquireScopes(
            () => bmsFilesInitializedMin.GetReaderGuard(),
            () => pendingInstallCharts.GetWriterGuard(),
            () => bmsFiles.GetWriterGuard());
    }

    /// <summary>
    /// 受付を予約せず変更前の収束状態と競合を検査します。本受付後も収束状態を再確認します。
    /// </summary>
    /// <param name="operation">変更操作の診断名。</param>
    /// <param name="showMessage">拒否警告を表示するか。</param>
    /// <returns>変更を拒否する場合はtrue。</returns>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal bool TryBlockCatalogMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
        => catalogMutationBoundary != null
            ? catalogMutationBoundary.TryBlockMutation(operation, showMessage, capability)
            : mutationBoundary.TryBlockMutation(operation, showMessage, capability);

    /// <summary>対象の物理変更に必要な共通受付を取得、または受理済み権限で借用します。呼出元が後片付け後に解放します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease EnterWriteScope(string operation, LibraryFileMutationCapability capability = null)
    {
        // Admission is intentionally the only long-lived scope.  Snapshot
        // locks are acquired by the operation immediately around its model
        // snapshot and are released before any filesystem, DB, cleanup, or
        // publication work begins.
        return EnterMutationReservation(operation, showMessage: true, capability: capability);
    }

    private LibraryFileMutationLease EnterMutationReservation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return mutationBoundary.TryBeginMutation(operation, showMessage, capability);
    }

    private LibraryFileMutationLease EnterCatalogWriteScope(string operation, LibraryFileMutationCapability capability = null)
    {
        return EnterCatalogMutationReservation(operation, showMessage: true, capability: capability);
    }

    private LibraryFileMutationLease EnterCatalogMutationReservation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return catalogMutationBoundary != null
            ? catalogMutationBoundary.TryBeginMutation(operation, showMessage, capability)
            : mutationBoundary.TryBeginMutation(operation, showMessage, capability);
    }

    private LibraryFileMutationLease EnterCatalogMutationReservationPreservingBusyNull(
        string operation,
        bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return catalogMutationBoundary != null
            ? catalogMutationBoundary.TryBeginMutationPreservingBusyNull(operation, showMessage, capability)
            : mutationBoundary.TryBeginMutation(operation, showMessage, capability);
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
