using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    private readonly object hashIndexSnapshotGate = new();

    private OwnedChartHashIndexVersionedSnapshot hashIndexSnapshot;

    private int hashIndexSnapshotVersion;

    private int hashIndexInvalidationVersion;

    private int digestMutationWindowDepth;

    internal object Gate => gate;

    internal OwnedChartCollectionState Collection => collection;

    internal bool IsInitialized => initialized;

    internal int BmsRowsVersion => bmsRowsVersion;

    internal int BmsonRowsVersion => bmsonRowsVersion;

    internal int CollectionVersion => Volatile.Read(ref collectionVersion);

    internal void BeginDigestMutationWindow()
    {
        Interlocked.Increment(ref digestMutationWindowDepth);
    }

    internal void EndDigestMutationWindow()
    {
        Interlocked.Decrement(ref digestMutationWindowDepth);
        InvalidateHashIndexSnapshot();
    }

    internal bool IsDigestMutationWindowActive()
    {
        return Volatile.Read(ref digestMutationWindowDepth) > 0;
    }

    internal void WaitForDigestMutationWindowIdle(CancellationToken cancellationToken = default)
    {
        while (IsDigestMutationWindowActive())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(20);
        }
    }

    internal void InvalidateHashIndexSnapshot()
    {
        lock (hashIndexSnapshotGate)
        {
            hashIndexSnapshot = null;
            hashIndexInvalidationVersion++;
        }
    }

    internal OwnedChartHashIndexVersionedSnapshot GetHashIndexSnapshot(
        CatalogStorageRowsOwner storageRowsOwner,
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        if (storageRowsOwner == null)
        {
            throw new ArgumentNullException(nameof(storageRowsOwner));
        }

        staleRetryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool waitForDigestWindow = false;
            int invalidationVersion;
            lock (hashIndexSnapshotGate)
            {
                OwnedChartHashIndexVersionedSnapshot currentSnapshot = hashIndexSnapshot;
                int currentOwnedCollectionVersion = CollectionVersion;
                if (currentSnapshot != null)
                {
                    if (IsHashIndexSnapshotCurrent(currentSnapshot, storageRowsOwner, currentOwnedCollectionVersion))
                    {
                        cacheHit = true;
                        return currentSnapshot;
                    }
                    hashIndexSnapshot = null;
                    hashIndexInvalidationVersion++;
                }
                if (IsDigestMutationWindowActive())
                {
                    waitForDigestWindow = true;
                }
                invalidationVersion = hashIndexInvalidationVersion;
            }

            if (waitForDigestWindow)
            {
                WaitForDigestMutationWindowIdle(cancellationToken);
                staleRetryCount++;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Stopwatch stopwatch = Stopwatch.StartNew();
            EnsureCurrent(storageRowsOwner, cancellationToken);
            OwnedChartHashIndexSnapshot builtSnapshot;
            StorageRowsVersionSnapshot storageRowsVersion;
            int ownedCollectionVersion;
            using (storageRowsOwner.WriteGate.GetReaderGuard())
            {
                storageRowsVersion = storageRowsOwner.CaptureVersionSnapshot();
                lock (gate)
                {
                    if (!initialized
                        || bmsRowsVersion != storageRowsVersion.BmsRowsVersion
                        || bmsonRowsVersion != storageRowsVersion.BmsonRowsVersion)
                    {
                        staleRetryCount++;
                        continue;
                    }
                    builtSnapshot = collection.CreateOwnedHashIndexSnapshot(cancellationToken);
                    ownedCollectionVersion = CollectionVersion;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (hashIndexSnapshotGate)
            {
                OwnedChartHashIndexVersionedSnapshot currentSnapshot = hashIndexSnapshot;
                if (currentSnapshot != null)
                {
                    if (IsHashIndexSnapshotCurrent(currentSnapshot, storageRowsOwner, CollectionVersion))
                    {
                        cacheHit = true;
                        return currentSnapshot;
                    }
                    hashIndexSnapshot = null;
                    hashIndexInvalidationVersion++;
                    staleRetryCount++;
                    continue;
                }
                if (hashIndexInvalidationVersion != invalidationVersion
                    || CollectionVersion != ownedCollectionVersion
                    || storageRowsOwner.BmsRowsVersion != storageRowsVersion.BmsRowsVersion
                    || storageRowsOwner.BmsonRowsVersion != storageRowsVersion.BmsonRowsVersion
                    || IsDigestMutationWindowActive())
                {
                    staleRetryCount++;
                    continue;
                }
                OwnedChartHashIndexVersionedSnapshot rebuiltSnapshot = new(
                    builtSnapshot,
                    Interlocked.Increment(ref hashIndexSnapshotVersion),
                    stopwatch.ElapsedMilliseconds,
                    invalidationVersion,
                    ownedCollectionVersion,
                    storageRowsVersion.BmsRowsVersion,
                    storageRowsVersion.BmsonRowsVersion);
                hashIndexSnapshot = rebuiltSnapshot;
                cacheHit = false;
                return rebuiltSnapshot;
            }
        }
    }

    private bool IsHashIndexSnapshotCurrent(
        OwnedChartHashIndexVersionedSnapshot snapshot,
        CatalogStorageRowsOwner storageRowsOwner,
        int currentOwnedCollectionVersion)
    {
        return snapshot != null
            && snapshot.OwnedCollectionVersion == currentOwnedCollectionVersion
            && storageRowsOwner.BmsRowsVersion == snapshot.BmsRowsVersion
            && storageRowsOwner.BmsonRowsVersion == snapshot.BmsonRowsVersion;
    }

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

    internal void EnsureCurrent(
        CatalogStorageRowsOwner storageRowsOwner,
        CancellationToken cancellationToken = default)
    {
        if (storageRowsOwner == null)
        {
            throw new ArgumentNullException(nameof(storageRowsOwner));
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageRowsVersionSnapshot versions = storageRowsOwner.CaptureVersionSnapshot();
            if (IsCurrent(versions.BmsRowsVersion, versions.BmsonRowsVersion))
            {
                return;
            }
            CatalogStorageRowsSnapshot snapshot;
            using (storageRowsOwner.WriteGate.GetReaderGuard())
            {
                snapshot = storageRowsOwner.CaptureSnapshot();
                if (IsCurrent(snapshot.BmsRowsVersion, snapshot.BmsonRowsVersion))
                {
                    return;
                }
                OwnedChartCollectionState rebuiltCollection = OwnedChartCollectionState.FromStorageRows(
                    snapshot.BmsRows,
                    snapshot.BmsonRows,
                    cancellationToken,
                    out _);
                cancellationToken.ThrowIfCancellationRequested();
                lock (storageRowsOwner.VersionGate)
                {
                    StorageRowsVersionSnapshot currentVersions = storageRowsOwner.CaptureVersionSnapshot();
                    if (currentVersions.BmsRowsVersion != snapshot.BmsRowsVersion
                        || currentVersions.BmsonRowsVersion != snapshot.BmsonRowsVersion)
                    {
                        continue;
                    }
                    if (IsCurrent(snapshot.BmsRowsVersion, snapshot.BmsonRowsVersion))
                    {
                        return;
                    }
                    ApplyBuiltCollection(
                        rebuiltCollection,
                        snapshot.BmsRowsVersion,
                        snapshot.BmsonRowsVersion);
                    return;
                }
            }
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

    internal void ValidateStorageRowUpsert(
        IEnumerable<BMSFile> bmsFiles,
        IEnumerable<LR2SongDBExtended.bmson_song> bmsonSongs)
    {
        lock (gate)
        {
            if (initialized)
            {
                collection.ValidateStorageRows(bmsFiles, bmsonSongs);
            }
            else
            {
                OwnedChartCollectionState.ValidateStorageRowsWithoutExistingCollection(bmsFiles, bmsonSongs);
            }
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
        out List<ChartFile> removedCharts)
    {
        removedCharts = [];
        lock (gate)
        {
            if (!initialized)
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
