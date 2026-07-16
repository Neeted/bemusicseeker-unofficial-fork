using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the catalog's derived owned-chart collection and its storage-row/version coupling.
/// Consumer-specific projections remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed class CatalogOwnedCollectionOwner
{
    private readonly object gate = new();

    private OwnedChartCollectionState collection = new();

    private bool initialized;

    private int bmsRowsVersion = -1;

    private int bmsonRowsVersion = -1;

    private int collectionVersion;

    internal object Gate => gate;

    internal OwnedChartCollectionState Collection => collection;

    internal bool IsInitialized => initialized;

    internal int BmsRowsVersion => bmsRowsVersion;

    internal int BmsonRowsVersion => bmsonRowsVersion;

    internal int CollectionVersion => Volatile.Read(ref collectionVersion);

    internal int IncrementVersion() => Interlocked.Increment(ref collectionVersion);

    internal bool IsCurrent(int currentBmsRowsVersion, int currentBmsonRowsVersion)
    {
        lock (gate)
        {
            return initialized
                && bmsRowsVersion == currentBmsRowsVersion
                && bmsonRowsVersion == currentBmsonRowsVersion;
        }
    }

    internal bool ApplyBuiltCollection(
        OwnedChartCollectionState rebuiltCollection,
        int rebuiltBmsRowsVersion,
        int rebuiltBmsonRowsVersion)
    {
        if (rebuiltCollection == null)
        {
            return false;
        }

        lock (gate)
        {
            if (initialized
                && bmsRowsVersion == rebuiltBmsRowsVersion
                && bmsonRowsVersion == rebuiltBmsonRowsVersion)
            {
                return false;
            }

            ApplyCollectionUnsafe(rebuiltCollection, rebuiltBmsRowsVersion, rebuiltBmsonRowsVersion);
            return true;
        }
    }

    internal bool ApplyMutation(
        IReadOnlyList<OwnedChartRemoveRequest> removeRequests,
        IReadOnlyList<LibraryChartPathChange> pathChanges,
        IReadOnlyList<BMSFile> addedBmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        lock (gate)
        {
            if (!initialized)
            {
                return false;
            }

            if (bmsRowsVersion != storageRowsVersion.PreviousBmsRowsVersion
                || bmsonRowsVersion != storageRowsVersion.PreviousBmsonRowsVersion)
            {
                ResetUnsafe();
                return false;
            }

            if (removeRequests?.Count > 0)
            {
                collection.RemoveChartRequests(removeRequests);
            }
            if (pathChanges?.Count > 0)
            {
                collection.ApplyPathChanges(pathChanges);
            }
            if (addedBmsFiles?.Count > 0 || addedBmsonSongs?.Count > 0)
            {
                collection.UpsertStorageRows(addedBmsFiles ?? [], addedBmsonSongs ?? []);
            }

            bmsRowsVersion = storageRowsVersion.BmsRowsVersion;
            bmsonRowsVersion = storageRowsVersion.BmsonRowsVersion;
            return true;
        }
    }

    internal bool ApplyDigestChanges(IReadOnlyList<LibraryChartDigestChange> digestChanges)
    {
        if (digestChanges?.Count == 0)
        {
            return false;
        }

        lock (gate)
        {
            if (!initialized)
            {
                return false;
            }

            collection.ApplyDigestChanges(digestChanges);
            return true;
        }
    }

    internal void Invalidate()
    {
        lock (gate)
        {
            ResetUnsafe();
        }
    }

    private void ResetUnsafe()
    {
        collection = new OwnedChartCollectionState();
        initialized = false;
        bmsRowsVersion = -1;
        bmsonRowsVersion = -1;
    }

    internal bool TryCreateFileScanRemovedStorageOwnerIdentityCharts(
        IReadOnlyList<string> deletedPaths,
        IReadOnlyList<string> deletedBmsonPaths,
        IReadOnlyList<BMSFile> nextFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> nextBmsonSongs,
        int currentBmsRowsVersion,
        int currentBmsonRowsVersion,
        out List<ChartFile> removedCharts)
    {
        removedCharts = [];
        lock (gate)
        {
            if (!initialized
                || bmsRowsVersion != currentBmsRowsVersion
                || bmsonRowsVersion != currentBmsonRowsVersion)
            {
                return false;
            }
            removedCharts = collection.CreateFileScanRemovedStorageOwnerIdentityCharts(
                deletedPaths,
                deletedBmsonPaths,
                nextFiles,
                nextBmsonSongs);
            return true;
        }
    }

    internal CatalogOwnedCollectionReplacementResult ReplaceForFileScan(CatalogStorageRowsSnapshot storageRows)
    {
        if (storageRows == null)
        {
            return CatalogOwnedCollectionReplacementResult.NotApplied;
        }

        lock (gate)
        {
            if (!initialized)
            {
                return CatalogOwnedCollectionReplacementResult.NotApplied;
            }
        }

        OwnedChartCollectionState replacement = OwnedChartCollectionState.FromStorageRows(
            storageRows.BmsRows,
            storageRows.BmsonRows,
            out OwnedChartStorageRowFilterSummary filterSummary);
        lock (gate)
        {
            if (!initialized)
            {
                return CatalogOwnedCollectionReplacementResult.NotApplied;
            }
            ApplyCollectionUnsafe(replacement, storageRows.BmsRowsVersion, storageRows.BmsonRowsVersion);
            return new CatalogOwnedCollectionReplacementResult(filterSummary, applied: true);
        }
    }

    private void ApplyCollectionUnsafe(
        OwnedChartCollectionState replacement,
        int replacementBmsRowsVersion,
        int replacementBmsonRowsVersion)
    {
        collection = replacement;
        initialized = true;
        bmsRowsVersion = replacementBmsRowsVersion;
        bmsonRowsVersion = replacementBmsonRowsVersion;
    }
}

internal sealed class CatalogOwnedCollectionReplacementResult(
    OwnedChartStorageRowFilterSummary filterSummary,
    bool applied)
{
    internal static CatalogOwnedCollectionReplacementResult NotApplied { get; } =
        new(new OwnedChartStorageRowFilterSummary(0, 0, 0, 0, 0, 0), applied: false);

    internal OwnedChartStorageRowFilterSummary FilterSummary { get; } = filterSummary;

    internal bool Applied { get; } = applied;
}
