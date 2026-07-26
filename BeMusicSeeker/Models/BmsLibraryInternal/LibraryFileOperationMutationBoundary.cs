using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Supplies the canonical LR2 and package mutation scopes to the file-operation
/// synchronization owner without retaining the BMSLibrary aggregate.
/// </summary>
internal sealed class LibraryFileOperationMutationBoundary : ILibraryFileOperationMutationBoundary
{
    private readonly BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner;

    private readonly PackageLifecycleOwner packageLifecycleOwner;

    internal LibraryFileOperationMutationBoundary(
        BMSLibrary.Lr2SynchronizationOwner lr2SynchronizationOwner,
        PackageLifecycleOwner packageLifecycleOwner)
    {
        this.lr2SynchronizationOwner = lr2SynchronizationOwner ?? throw new ArgumentNullException(nameof(lr2SynchronizationOwner));
        this.packageLifecycleOwner = packageLifecycleOwner ?? throw new ArgumentNullException(nameof(packageLifecycleOwner));
    }

    public IDisposable EnterMutationSequence()
    {
        return lr2SynchronizationOwner.EnterLr2MutationSequence();
    }

    public IDisposable TryBeginMutation(string operation, bool showMessage)
    {
        return lr2SynchronizationOwner.TryBeginMutation(operation, showMessage);
    }

    public bool TryBlockMutation(string operation, bool showMessage)
    {
        return lr2SynchronizationOwner.TryBlockMutation(operation, showMessage);
    }

    public IDisposable BeginCollectionMutationScope()
    {
        return packageLifecycleOwner.BeginCollectionMutationScope();
    }
}
