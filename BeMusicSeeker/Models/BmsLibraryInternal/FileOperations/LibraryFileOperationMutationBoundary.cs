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

    /// <summary>新要求は共通受付を非待機で取得し、受理済み権限は生存ownerを検査して借用します。Busyはnullです。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    public LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return lr2SynchronizationOwner.TryBeginMutation(operation, showMessage, capability);
    }

    /// <summary>受理済み権限を検査し、新要求の共通受付Busyを副作用前に判定します。</summary>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    public bool TryBlockMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return lr2SynchronizationOwner.TryBlockMutation(operation, showMessage, capability);
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
    /// 共通受付を取得または借用し、カタログのパス収束が現在である場合だけ変更を受理します。
    /// 呼出元が実変更・必須反映・後片付けの終端まで受付を保持します。
    /// </summary>
    /// <param name="operation">変更操作の診断名。</param>
    /// <param name="showMessage">パス収束による拒否の警告を表示するか。</param>
    /// <returns>取得または借用したlease。競合またはパス未収束の場合はnull。</returns>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    public LibraryFileMutationLease TryBeginMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return mutationAdmissionOwner.TryBeginFileOperationMutation(operation, showMessage, capability: capability);
    }

    /// <summary>
    /// 共通受付を取得または借用し、パス収束を検査します。
    /// 競合時に例外でなくnull・未適用を返す既存の終端契約を維持します。
    /// </summary>
    /// <param name="operation">変更操作の診断名。</param>
    /// <param name="showMessage">競合またはパス収束による拒否の警告を表示するか。</param>
    /// <returns>取得または借用したlease。競合またはパス未収束の場合はnull。</returns>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    internal LibraryFileMutationLease TryBeginMutationPreservingBusyNull(
        string operation,
        bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return mutationAdmissionOwner.TryBeginFileOperationMutation(
            operation,
            showMessage,
            showBusyMessage: showMessage,
            throwOnBusyRace: false, capability: capability);
    }

    /// <summary>
    /// 受付を予約せず、準備や確認の前に競合とパス収束を検査します。
    /// 本受付後にも収束状態を検査し、この事前検査だけで変更を許可しません。
    /// </summary>
    /// <param name="operation">変更操作の診断名。</param>
    /// <param name="showMessage">拒否の警告を表示するか。</param>
    /// <returns>変更を拒否する場合はtrue。</returns>
    /// <param name="capability">同じ共通受付ownerの生存権限。nullは新規の非待機受付で、借用終端では外側leaseを解放しません。</param>
    public bool TryBlockMutation(string operation, bool showMessage, LibraryFileMutationCapability capability = null)
    {
        return mutationAdmissionOwner.TryBlockMutation(operation, showMessage, capability);
    }
}
