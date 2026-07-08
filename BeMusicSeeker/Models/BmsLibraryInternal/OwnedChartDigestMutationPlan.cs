using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class OwnedChartDigestMutationPlan
{
    internal static OwnedChartDigestMutationPlan Empty { get; } = new([]);

    internal OwnedChartDigestMutationPlan(
        IReadOnlyList<LibraryChartDigestChange> digestChanges,
        bool requiresInstalledLookupFullInvalidate = false,
        bool installEstimationMetadataProfileCacheInvalidated = false,
        bool duplicateCacheInvalidated = false,
        bool playlistSummaryOwnedHashInvalidated = false,
        bool ownedCollectionChanged = false,
        bool resourceHealthIndexInvalidated = false,
        bool warningPresentationChanged = false,
        bool bmsFilesStorageRowsChanged = false,
        bool bmsonSongsStorageRowsChanged = false)
    {
        DigestChanges = digestChanges as LibraryChartDigestChange[] ?? [.. digestChanges ?? []];
        RequiresInstalledLookupFullInvalidate = requiresInstalledLookupFullInvalidate;
        InstallEstimationMetadataProfileCacheInvalidated = installEstimationMetadataProfileCacheInvalidated;
        DuplicateCacheInvalidated = duplicateCacheInvalidated;
        PlaylistSummaryOwnedHashInvalidated = playlistSummaryOwnedHashInvalidated;
        OwnedCollectionChanged = ownedCollectionChanged;
        ResourceHealthIndexInvalidated = resourceHealthIndexInvalidated;
        WarningPresentationChanged = warningPresentationChanged;
        BmsFilesStorageRowsChanged = bmsFilesStorageRowsChanged;
        BmsonSongsStorageRowsChanged = bmsonSongsStorageRowsChanged;
    }

    internal IReadOnlyList<LibraryChartDigestChange> DigestChanges { get; }

    internal bool RequiresInstalledLookupFullInvalidate { get; }

    internal bool InstallEstimationMetadataProfileCacheInvalidated { get; }

    internal bool DuplicateCacheInvalidated { get; }

    internal bool PlaylistSummaryOwnedHashInvalidated { get; }

    internal bool OwnedCollectionChanged { get; }

    internal bool ResourceHealthIndexInvalidated { get; }

    internal bool WarningPresentationChanged { get; }

    internal bool BmsFilesStorageRowsChanged { get; }

    internal bool BmsonSongsStorageRowsChanged { get; }
}
