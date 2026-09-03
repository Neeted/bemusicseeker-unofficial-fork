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
