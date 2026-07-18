using System;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable facts published after the scan pipeline has committed its catalog
/// storage replacement. Consumer-specific cache and presentation effects are
/// composed by the application-facing library facade.
/// </summary>
internal sealed class FileScanCatalogReplacementEvent
{
    internal FileScanCatalogReplacementEvent(
        CatalogFileScanStorageReplacementRequest request,
        CatalogFileScanStorageReplacementReceipt receipt,
        LibraryResourceIndex nextResourceIndex,
        bool resourceHealthIndexCurrentAtBase,
        string reason)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        NextResourceIndex = nextResourceIndex;
        ResourceHealthIndexCurrentAtBase = resourceHealthIndexCurrentAtBase;
        Reason = reason ?? string.Empty;
    }

    internal CatalogFileScanStorageReplacementRequest Request { get; }

    internal CatalogFileScanStorageReplacementReceipt Receipt { get; }

    internal LibraryResourceIndex NextResourceIndex { get; }

    internal bool ResourceHealthIndexCurrentAtBase { get; }

    internal string Reason { get; }
}

/// <summary>
/// Immutable facts published when the catalog storage replacement could not be
/// applied. The original exception remains owned by the scan pipeline and is
/// rethrown after consumers invalidate their derived state.
/// </summary>
internal sealed class FileScanCatalogReplacementFailureEvent
{
    internal FileScanCatalogReplacementFailureEvent(
        CatalogFileScanStorageReplacementRequest request,
        bool resourceHealthIndexCurrentAtBase,
        string reason)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ResourceHealthIndexCurrentAtBase = resourceHealthIndexCurrentAtBase;
        Reason = reason ?? string.Empty;
    }

    internal CatalogFileScanStorageReplacementRequest Request { get; }

    internal bool ResourceHealthIndexCurrentAtBase { get; }

    internal string Reason { get; }
}
