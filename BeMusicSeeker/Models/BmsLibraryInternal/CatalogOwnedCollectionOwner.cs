using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Owns the catalog's derived owned-chart collection and its storage-row/version coupling.
/// Installed-chart lookup state is kept with this owned collection; other consumer-specific
/// projections remain composed by <see cref="BMSLibrary"/>.
/// </summary>
internal sealed partial class CatalogOwnedCollectionOwner
{
    private readonly object gate = new();

    private OwnedChartCollectionState collection = new();

    private bool initialized;

    private int bmsRowsVersion = -1;

    private int bmsonRowsVersion = -1;

    private int collectionVersion;

    private readonly object hashIndexSnapshotGate = new();

    private OwnedChartHashIndexRoot hashIndexRoot;

    private OwnedChartHashIndexVersionedSnapshot hashIndexSnapshot;

    private int hashIndexSnapshotVersion;

    private int hashIndexInvalidationVersion;

    private int digestMutationWindowDepth;

    /// <summary>
    /// owned hash rootの実処理を記録する任意の内部 observer です。
    /// production では設定せず、設定時も owner へ再入しません。
    /// </summary>
    internal Action<string> StoreWorkObserver { get; set; }

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

    /// <summary>
    /// digest mutation windowを閉じます。確定済み hash facts は mutation 中に適用済みのため、ここでは失効しません。
    /// </summary>
    internal void EndDigestMutationWindow()
    {
        Interlocked.Decrement(ref digestMutationWindowDepth);
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

    /// <summary>
    /// owned hash rootを破棄し、次回consumer利用時にfull buildへ戻します。
    /// </summary>
    internal void InvalidateHashIndexSnapshot()
    {
        lock (hashIndexSnapshotGate)
        {
            hashIndexRoot = null;
            hashIndexSnapshot = null;
            hashIndexInvalidationVersion++;
        }
    }

    /// <summary>
    /// 所持 hash の optional root が構築済みかを返します。
    /// </summary>
    /// <returns>構築済みなら true。</returns>
    internal bool IsHashIndexWarm()
    {
        lock (hashIndexSnapshotGate)
        {
            return hashIndexRoot != null;
        }
    }

    /// <summary>
    /// owned hash rootを必要時だけ構築し、構築済みなら同じ immutable rootを再利用します。
    /// </summary>
    /// <param name="storageRowsOwner">storage row versionの所有者。</param>
    /// <param name="cancellationToken">build中断用token。</param>
    /// <param name="cacheHit">既存rootを返したか。</param>
    /// <param name="staleRetryCount">version競合による再試行回数。</param>
    /// <returns>現在の owned hash snapshot。</returns>
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
                if (currentSnapshot != null
                    && IsHashIndexSnapshotCurrent(currentSnapshot, storageRowsOwner, CollectionVersion))
                {
                    cacheHit = true;
                    return currentSnapshot;
                }
                waitForDigestWindow = IsDigestMutationWindowActive();
                invalidationVersion = hashIndexInvalidationVersion;
            }

            if (waitForDigestWindow)
            {
                WaitForDigestMutationWindowIdle(cancellationToken);
                staleRetryCount++;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            EnsureCurrent(storageRowsOwner, cancellationToken);
            OwnedChartHashIndexSnapshot builtSnapshot = null;
            StorageRowsVersionSnapshot storageRowsVersion;
            int ownedCollectionVersion;
            bool needsBuild;
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
                    ownedCollectionVersion = CollectionVersion;
                    lock (hashIndexSnapshotGate)
                    {
                        needsBuild = hashIndexRoot == null
                            || !IsHashIndexSnapshotCurrent(
                                hashIndexSnapshot,
                                storageRowsVersion,
                                ownedCollectionVersion);
                    }
                    if (needsBuild)
                    {
                        builtSnapshot = collection.CreateOwnedHashIndexSnapshot(
                            cancellationToken,
                            StoreWorkObserver);
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (hashIndexSnapshotGate)
            {
                if (hashIndexInvalidationVersion != invalidationVersion
                    || CollectionVersion != ownedCollectionVersion
                    || storageRowsOwner.BmsRowsVersion != storageRowsVersion.BmsRowsVersion
                    || storageRowsOwner.BmsonRowsVersion != storageRowsVersion.BmsonRowsVersion
                    || IsDigestMutationWindowActive())
                {
                    staleRetryCount++;
                    continue;
                }

                if (needsBuild)
                {
                    hashIndexRoot = OwnedChartHashIndexRoot.Create(builtSnapshot);
                    hashIndexSnapshotVersion = Math.Max(1, hashIndexSnapshotVersion + 1);
                    StoreWorkObserver?.Invoke("owned_hash_root_capture");
                    cacheHit = false;
                }
                else
                {
                    cacheHit = true;
                }
                hashIndexSnapshot = CreateHashIndexVersionedSnapshotUnsafe(
                    stopwatch.ElapsedMilliseconds,
                    ownedCollectionVersion,
                    storageRowsVersion);
                return hashIndexSnapshot;
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
            && storageRowsOwner.BmsonRowsVersion == snapshot.BmsonRowsVersion
            && snapshot.InvalidationVersion == Volatile.Read(ref hashIndexInvalidationVersion);
    }

    private bool IsHashIndexSnapshotCurrent(
        OwnedChartHashIndexVersionedSnapshot snapshot,
        StorageRowsVersionSnapshot storageRowsVersion,
        int currentOwnedCollectionVersion)
    {
        return snapshot != null
            && snapshot.OwnedCollectionVersion == currentOwnedCollectionVersion
            && storageRowsVersion.BmsRowsVersion == snapshot.BmsRowsVersion
            && storageRowsVersion.BmsonRowsVersion == snapshot.BmsonRowsVersion
            && snapshot.InvalidationVersion == hashIndexInvalidationVersion;
    }

    private OwnedChartHashIndexVersionedSnapshot CreateHashIndexVersionedSnapshotUnsafe(
        long buildElapsedMs,
        int ownedCollectionVersion,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        return OwnedChartHashIndexVersionedSnapshot.CreateFromRoot(
            hashIndexRoot,
            hashIndexSnapshotVersion,
            buildElapsedMs,
            hashIndexInvalidationVersion,
            ownedCollectionVersion,
            storageRowsVersion.BmsRowsVersion,
            storageRowsVersion.BmsonRowsVersion,
            StoreWorkObserver);
    }

    /// <summary>
    /// owned collection versionだけを進めます。hash factsの適用とsnapshotのsource version更新は、
    /// mutation dispatchのpublication境界で一体に行います。
    /// </summary>
    /// <returns>進めた owned collection version。</returns>
    internal int IncrementVersion()
    {
        return Interlocked.Increment(ref collectionVersion);
    }

    /// <summary>
    /// facts適用後のowned collection versionを、構築済みhash snapshotへ反映します。
    /// </summary>
    internal void RebaseHashIndexSnapshot()
    {
        StorageRowsVersionSnapshot storageRowsVersion;
        int ownedCollectionVersion;
        lock (gate)
        {
            storageRowsVersion = new StorageRowsVersionSnapshot(
                bmsRowsVersion,
                bmsonRowsVersion);
            ownedCollectionVersion = CollectionVersion;
        }
        lock (hashIndexSnapshotGate)
        {
            if (hashIndexRoot == null
                || IsHashIndexSnapshotCurrent(
                    hashIndexSnapshot,
                    storageRowsVersion,
                    ownedCollectionVersion))
            {
                return;
            }
            hashIndexSnapshot = CreateHashIndexVersionedSnapshotUnsafe(
                hashIndexSnapshot?.BuildElapsedMs ?? 0L,
                ownedCollectionVersion,
                storageRowsVersion);
        }
    }

    /// <summary>
    /// 構築済み owned hash rootへ旧新 factsを局所適用します。
    /// </summary>
    /// <param name="deltas">適用する旧新 hash facts。</param>
    /// <param name="requiresFullInvalidate">旧 facts不足などで全再構築が必要か。</param>
    /// <returns>構築済み rootへ適用した場合は true。</returns>
    internal bool ApplyHashIndexDeltas(
        IReadOnlyList<OwnedChartHashIndexDelta> deltas,
        bool requiresFullInvalidate)
    {
        return ApplyHashIndexDeltasCore(deltas, requiresFullInvalidate, skipCurrentSnapshot: true);
    }

    private bool ApplyHashIndexDeltasCore(
        IReadOnlyList<OwnedChartHashIndexDelta> deltas,
        bool requiresFullInvalidate,
        bool skipCurrentSnapshot)
    {
        StorageRowsVersionSnapshot storageRowsVersion;
        int ownedCollectionVersion;
        lock (gate)
        {
            storageRowsVersion = new StorageRowsVersionSnapshot(
                bmsRowsVersion,
                bmsonRowsVersion);
            ownedCollectionVersion = CollectionVersion;
        }
        lock (hashIndexSnapshotGate)
        {
            if (hashIndexRoot == null)
            {
                return false;
            }
            if (requiresFullInvalidate)
            {
                hashIndexRoot = null;
                hashIndexSnapshot = null;
                hashIndexInvalidationVersion++;
                StoreWorkObserver?.Invoke("owned_hash_full_invalidate");
                return true;
            }

            if (skipCurrentSnapshot
                && IsHashIndexSnapshotCurrent(
                    hashIndexSnapshot,
                    storageRowsVersion,
                    ownedCollectionVersion))
            {
                // IncrementVersion は facts 適用前に呼ばれるため、先行した getter が
                // 現在 source から再構築済みなら、その rootへ旧deltaを重ねません。
                return true;
            }

            OwnedChartHashIndexRoot nextRoot = hashIndexRoot.ApplyDeltas(
                deltas,
                StoreWorkObserver,
                out bool membershipChanged);
            hashIndexRoot = nextRoot;
            if (membershipChanged)
            {
                hashIndexSnapshotVersion = Math.Max(1, hashIndexSnapshotVersion + 1);
            }
            hashIndexSnapshot = CreateHashIndexVersionedSnapshotUnsafe(
                hashIndexSnapshot?.BuildElapsedMs ?? 0L,
                ownedCollectionVersion,
                storageRowsVersion);
            StoreWorkObserver?.Invoke("owned_hash_delta_apply");
            return true;
        }
    }

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
                var rebuiltCollection = OwnedChartCollectionState.FromStorageRows(
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

    /// <summary>
    /// durable catalog mutationをowned collectionへ反映し、初回BMSON canonical順序正規化の発生を返します。
    /// </summary>
    /// <param name="removeRequests">明示されたowner/path remove。</param>
    /// <param name="pathChanges">durable commit後に確定したpath facts。</param>
    /// <param name="addedBmsFiles">追加または置換するBMS storage row。</param>
    /// <param name="addedBmsonSongs">追加または置換するBMSON storage row。</param>
    /// <param name="storageRowsVersion">適用前後のstorage row version。</param>
    /// <param name="bmsonCanonicalOrderNormalized">今回のupsertで初回BMSON順序正規化が発生したか。</param>
    /// <returns>owned collectionへmutationを適用できた場合は<see langword="true"/>。</returns>
    internal bool ApplyMutation(
        IReadOnlyList<OwnedChartRemoveRequest> removeRequests,
        IReadOnlyList<LibraryChartPathChange> pathChanges,
        IReadOnlyList<BMSFile> addedBmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        StorageRowsVersionSnapshot storageRowsVersion,
        out bool bmsonCanonicalOrderNormalized)
    {
        return ApplyMutationCore(
            removeRequests,
            pathChanges,
            addedBmsFiles,
            addedBmsonSongs,
            storageRowsVersion,
            out bmsonCanonicalOrderNormalized);
    }

    /// <summary>
    /// durable catalogの削除・移動だけをowned collectionへ反映します。
    /// </summary>
    /// <param name="removeRequests">明示されたowner/path remove。</param>
    /// <param name="pathChanges">durable commit後に確定したpath facts。</param>
    /// <param name="storageRowsVersion">適用前後のstorage row version。</param>
    /// <param name="bmsonCanonicalOrderNormalized">常にfalse。BMSON upsertを実行していないことを表します。</param>
    /// <returns>owned collectionへmutationを適用できた場合は<see langword="true"/>。</returns>
    internal bool ApplyMutation(
        IReadOnlyList<OwnedChartRemoveRequest> removeRequests,
        IReadOnlyList<LibraryChartPathChange> pathChanges,
        StorageRowsVersionSnapshot storageRowsVersion,
        out bool bmsonCanonicalOrderNormalized)
    {
        return ApplyMutationCore(
            removeRequests,
            pathChanges,
            null,
            null,
            storageRowsVersion,
            out bmsonCanonicalOrderNormalized);
    }

    private bool ApplyMutationCore(
        IReadOnlyList<OwnedChartRemoveRequest> removeRequests,
        IReadOnlyList<LibraryChartPathChange> pathChanges,
        IReadOnlyList<BMSFile> addedBmsFiles,
        IReadOnlyList<LR2SongDBExtended.bmson_song> addedBmsonSongs,
        StorageRowsVersionSnapshot storageRowsVersion,
        out bool bmsonCanonicalOrderNormalized)
    {
        bmsonCanonicalOrderNormalized = false;
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
                bmsonCanonicalOrderNormalized = collection.UpsertStorageRows(
                    addedBmsFiles ?? [],
                    addedBmsonSongs ?? []);
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

    /// <summary>
    /// digest factsをowned collectionと構築済みhash rootへ同じmutation境界で適用します。
    /// </summary>
    /// <param name="digestChanges">永続化済みの旧新digest facts。</param>
    /// <returns>owned collectionへ適用できた場合は true。</returns>
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

            try
            {
                collection.ApplyDigestChanges(digestChanges);
                List<OwnedChartHashIndexDelta> hashDeltas = [.. digestChanges
                    .Where(change => change?.HasDigestChange == true)
                    .Select(change => new OwnedChartHashIndexDelta(
                        change.OldMd5,
                        change.OldSha256,
                        change.NewMd5,
                        change.NewSha256))];
                ApplyHashIndexDeltasCore(
                    hashDeltas,
                    requiresFullInvalidate: false,
                    skipCurrentSnapshot: false);
                return true;
            }
            catch
            {
                ClearHashIndexUnsafe();
                throw;
            }
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
        ClearHashIndexUnsafe();
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

        var replacement = OwnedChartCollectionState.FromStorageRows(
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
        ClearHashIndexUnsafe();
    }

    private void ClearHashIndexUnsafe()
    {
        lock (hashIndexSnapshotGate)
        {
            if (hashIndexRoot == null && hashIndexSnapshot == null)
            {
                return;
            }
            hashIndexRoot = null;
            hashIndexSnapshot = null;
            hashIndexInvalidationVersion++;
            StoreWorkObserver?.Invoke("owned_hash_full_invalidate");
        }
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
