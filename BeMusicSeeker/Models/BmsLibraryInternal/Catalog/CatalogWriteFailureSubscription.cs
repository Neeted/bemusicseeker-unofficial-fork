using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface ICatalogWriteFailureSink
{
    void PublishCatalogWriteFailureFact(CatalogWriteFailureFact failureFact);
}

/// <summary>
/// Connects catalog write failures to the application owner without introducing
/// a UI event-manager dependency or a strong owner callback reference.
/// </summary>
internal sealed class CatalogWriteFailureSubscription : IDisposable
{
    private CatalogMutationOwner owner;
    private WeakReference<ICatalogWriteFailureSink> target;
    private bool disposed;

    internal CatalogWriteFailureSubscription(
        CatalogMutationOwner owner,
        ICatalogWriteFailureSink target)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.target = new WeakReference<ICatalogWriteFailureSink>(
            target ?? throw new ArgumentNullException(nameof(target)));
        owner.CatalogWriteFailurePublished += HandlePublished;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        CatalogMutationOwner source = owner;
        owner = null;
        target = null;
        if (source != null)
        {
            source.CatalogWriteFailurePublished -= HandlePublished;
        }
    }

    private void HandlePublished(object sender, CatalogWriteFailureFact failureFact)
    {
        if (disposed)
        {
            return;
        }
        if (target != null && target.TryGetTarget(out ICatalogWriteFailureSink sink))
        {
            sink.PublishCatalogWriteFailureFact(failureFact);
            return;
        }
        Dispose();
    }
}
