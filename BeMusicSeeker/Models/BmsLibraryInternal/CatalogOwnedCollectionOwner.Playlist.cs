using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// <see cref="CatalogOwnedCollectionOwner"/> が保持する playlist resolve index の状態です。
/// owned collection と同じ source version 境界で snapshot を構築・更新します。
/// </summary>
internal sealed partial class CatalogOwnedCollectionOwner
{
    private readonly object lockPlaylistLibraryResolveIndexSnapshot = new();

    private PlaylistLibraryResolveIndexSnapshot playlistLibraryResolveIndexSnapshot;

    private int playlistLibraryResolveIndexSnapshotVersion;

    private int playlistLibraryResolveIndexInvalidationVersion;

    private int playlistLibraryResolveIndexInvalidationOwnedCollectionVersion;

    /// <summary>
    /// playlist resolve root の実格納処理を記録する任意の内部 observer です。
    /// production では設定せず、設定時も owner へ再入しません。
    /// </summary>
    internal Action<string> PlaylistLibraryResolveIndexStoreWorkObserver { get; set; }

    /// <summary>
    /// playlist resolve snapshot を破棄し、次回要求で再構築します。
    /// </summary>
    /// <param name="ownedCollectionVersion">失効時点の owned collection version。未指定時は現在値を使います。</param>
    internal void InvalidatePlaylistLibraryResolveIndexSnapshot(int ownedCollectionVersion = 0)
    {
        int resolvedOwnedCollectionVersion = ownedCollectionVersion > 0
            ? ownedCollectionVersion
            : CollectionVersion;
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            playlistLibraryResolveIndexSnapshot = null;
            playlistLibraryResolveIndexInvalidationVersion++;
            playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = resolvedOwnedCollectionVersion;
        }
    }

    /// <summary>
    /// mutation 後の source version を、構築済み playlist resolve snapshot へ反映します。
    /// </summary>
    /// <param name="storageRowsOwner">storage row version の所有者。</param>
    internal void RebasePlaylistLibraryResolveIndexSnapshot(
        CatalogStorageRowsOwner storageRowsOwner)
    {
        if (storageRowsOwner == null)
        {
            throw new ArgumentNullException(nameof(storageRowsOwner));
        }

        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            PlaylistLibraryResolveIndexSnapshot snapshot = playlistLibraryResolveIndexSnapshot;
            if (snapshot == null)
            {
                return;
            }

            StorageRowsVersionSnapshot storageRowsVersion = storageRowsOwner.CaptureVersionSnapshot();
            int ownedCollectionVersion = CollectionVersion;
            if (snapshot.OwnedCollectionVersion == ownedCollectionVersion
                && snapshot.BmsRowsVersion == storageRowsVersion.BmsRowsVersion
                && snapshot.BmsonRowsVersion == storageRowsVersion.BmsonRowsVersion)
            {
                return;
            }

            playlistLibraryResolveIndexSnapshot = snapshot.WithMetadata(
                version: snapshot.Version,
                buildElapsedMs: snapshot.BuildElapsedMs,
                invalidationVersion: snapshot.InvalidationVersion,
                ownedCollectionVersion: ownedCollectionVersion,
                bmsRowsVersion: storageRowsVersion.BmsRowsVersion,
                bmsonRowsVersion: storageRowsVersion.BmsonRowsVersion);
        }
    }

    /// <summary>
    /// warm な playlist resolve root に mutation facts を局所適用します。
    /// facts 不足または全置換境界では既存の全失効契約へ戻します。
    /// </summary>
    /// <param name="storageRowsOwner">storage row version の所有者。</param>
    /// <param name="digestChanges">digest の旧新 facts。</param>
    /// <param name="digestMutationApplied">digest facts が正本へ適用済みか。</param>
    /// <param name="installedLookupMutation">installed lookup の旧新 facts。</param>
    /// <param name="addedCharts">追加 chart facts。</param>
    /// <param name="ownedCollectionChanged">owned collection に変更があるか。</param>
    /// <param name="playlistResolveIndexInvalidated">事前に全失効が必要と判断されたか。</param>
    /// <param name="bmsonCanonicalOrderNormalized">BMSON canonical 順序を正規化したか。</param>
    /// <param name="ownedCollectionVersion">mutation と対応する owned collection version。</param>
    /// <returns>全失効された場合は <see langword="true"/>。</returns>
    internal bool ApplyPlaylistLibraryResolveIndexMutation(
        CatalogStorageRowsOwner storageRowsOwner,
        IReadOnlyList<LibraryChartDigestChange> digestChanges,
        bool digestMutationApplied,
        InstalledChartLookupMutation installedLookupMutation,
        IReadOnlyList<ChartFile> addedCharts,
        bool ownedCollectionChanged,
        bool playlistResolveIndexInvalidated,
        bool bmsonCanonicalOrderNormalized,
        int ownedCollectionVersion = 0)
    {
        if (storageRowsOwner == null)
        {
            throw new ArgumentNullException(nameof(storageRowsOwner));
        }

        int digestChangeCount = digestChanges?.Count ?? 0;
        if (!ownedCollectionChanged && digestChangeCount == 0)
        {
            return playlistResolveIndexInvalidated;
        }

        PlaylistLibraryResolveIndexSnapshot snapshot;
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            snapshot = playlistLibraryResolveIndexSnapshot;
        }

        if (playlistResolveIndexInvalidated || bmsonCanonicalOrderNormalized)
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot(ownedCollectionVersion);
            return true;
        }

        // cold consumer には mutation 用の optional index を構築しません。
        if (snapshot == null)
        {
            return false;
        }

        var removals = new List<PlaylistLibraryResolveChartFact>();
        var removalKeys = new HashSet<string>(StringComparer.Ordinal);
        var additionPaths = new List<(LibraryChartKind Kind, string Path)>();
        var additionKeys = new HashSet<string>(StringComparer.Ordinal);
        bool factsComplete = true;

        if (digestChangeCount > 0)
        {
            if (!digestMutationApplied)
            {
                factsComplete = false;
            }
            foreach (LibraryChartDigestChange change in digestChanges)
            {
                if (change?.HasDigestChange != true
                    || !AddPlaylistResolveRemovalFact(
                        removals,
                        removalKeys,
                        change.Kind,
                        change.Path,
                        change.OldMd5,
                        change.OldSha256))
                {
                    factsComplete = false;
                    continue;
                }
                AddPlaylistResolveAdditionPath(
                    additionPaths,
                    additionKeys,
                    change.Kind,
                    change.Path);
            }
        }
        else
        {
            InstalledChartLookupMutation mutation = installedLookupMutation;
            if (mutation == null || mutation.RequiresFullInvalidate)
            {
                factsComplete = false;
            }
            else
            {
                foreach (InstalledChartLookupMutationEntry removed in mutation.Removed)
                {
                    if (!AddPlaylistResolveRemovalFact(
                        removals,
                        removalKeys,
                        ToLibraryChartKind(removed.Kind),
                        removed.Path,
                        removed.Md5,
                        removed.Sha256))
                    {
                        factsComplete = false;
                    }
                }
                foreach (InstalledChartLookupPathMutationEntry moved in mutation.Moved)
                {
                    if (!AddPlaylistResolveRemovalFact(
                        removals,
                        removalKeys,
                        ToLibraryChartKind(moved.Kind),
                        moved.OldPath,
                        moved.Md5,
                        moved.Sha256))
                    {
                        factsComplete = false;
                    }
                    else
                    {
                        AddPlaylistResolveAdditionPath(
                            additionPaths,
                            additionKeys,
                            ToLibraryChartKind(moved.Kind),
                            moved.NewPath);
                    }
                }

                foreach (ChartFile added in addedCharts ?? [])
                {
                    if (added == null
                        || string.IsNullOrWhiteSpace(added.Path)
                        || string.IsNullOrWhiteSpace(added.Md5))
                    {
                        factsComplete = false;
                        continue;
                    }
                    AddPlaylistResolveAdditionPath(
                        additionPaths,
                        additionKeys,
                        ToLibraryChartKind(added.Kind),
                        added.Path);
                }
            }
        }

        if (!factsComplete || (removals.Count == 0 && additionPaths.Count == 0))
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot(ownedCollectionVersion);
            return true;
        }

        if (!TryCreateOwnedCanonicalPlaylistChartFacts(
            storageRowsOwner,
            additionPaths,
            out List<PlaylistLibraryResolveChartFact> additions))
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot(ownedCollectionVersion);
            return true;
        }

        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            if (!ReferenceEquals(snapshot, playlistLibraryResolveIndexSnapshot))
            {
                playlistLibraryResolveIndexSnapshot = null;
                playlistLibraryResolveIndexInvalidationVersion++;
                playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = CollectionVersion;
                return true;
            }

            if (!snapshot.TryApplyDelta(
                removals,
                additions,
                PlaylistLibraryResolveIndexStoreWorkObserver,
                out PlaylistLibraryResolveIndexSnapshot nextSnapshot))
            {
                playlistLibraryResolveIndexSnapshot = null;
                playlistLibraryResolveIndexInvalidationVersion++;
                playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = CollectionVersion;
                return true;
            }

            StorageRowsVersionSnapshot storageRowsVersion = storageRowsOwner.CaptureVersionSnapshot();
            playlistLibraryResolveIndexSnapshot = nextSnapshot.WithMetadata(
                version: Interlocked.Increment(ref playlistLibraryResolveIndexSnapshotVersion),
                buildElapsedMs: snapshot.BuildElapsedMs,
                invalidationVersion: playlistLibraryResolveIndexInvalidationVersion,
                ownedCollectionVersion: CollectionVersion,
                bmsRowsVersion: storageRowsVersion.BmsRowsVersion,
                bmsonRowsVersion: storageRowsVersion.BmsonRowsVersion);
        }
        return false;
    }

    /// <summary>
    /// playlist detail の entry hash 解決に使う owned collection 隣接 index を返します。
    /// </summary>
    /// <param name="storageRowsOwner">storage row version の所有者。</param>
    /// <param name="cancellationToken">構築中の cancellation token。</param>
    /// <param name="cacheHit">既存 snapshot を再利用した場合は true。</param>
    /// <param name="staleRetryCount">version 競合による再試行回数。</param>
    /// <returns>playlist detail 用 resolve index。</returns>
    internal PlaylistLibraryResolveIndexSnapshot GetPlaylistLibraryResolveIndexSnapshot(
        CatalogStorageRowsOwner storageRowsOwner,
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        if (storageRowsOwner == null)
        {
            throw new ArgumentNullException(nameof(storageRowsOwner));
        }

        PlaylistLibraryResolveIndexSnapshot snapshot;
        staleRetryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool waitForDigestWindow = false;
            int invalidationVersion;
            int invalidationOwnedCollectionVersion;
            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                snapshot = playlistLibraryResolveIndexSnapshot;
                int currentOwnedCollectionVersion = CollectionVersion;
                if (snapshot != null)
                {
                    if (IsPlaylistLibraryResolveIndexSnapshotCurrent(
                        snapshot,
                        storageRowsOwner,
                        currentOwnedCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistLibraryResolveIndexSnapshot = null;
                    playlistLibraryResolveIndexInvalidationVersion++;
                    playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = currentOwnedCollectionVersion;
                }
                if (IsDigestMutationWindowActive())
                {
                    waitForDigestWindow = true;
                }
                invalidationVersion = playlistLibraryResolveIndexInvalidationVersion;
                invalidationOwnedCollectionVersion = playlistLibraryResolveIndexInvalidationOwnedCollectionVersion;
            }
            if (waitForDigestWindow)
            {
                WaitForDigestMutationWindowIdle(cancellationToken);
                staleRetryCount++;
                continue;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            PlaylistLibraryResolveIndexSnapshot rebuiltSnapshot;
            StorageRowsVersionSnapshot storageRowsVersion;
            int ownedCollectionVersion;
            using (storageRowsOwner.WriteGate.GetReaderGuard())
            {
                EnsureCurrent(storageRowsOwner, cancellationToken);
                rebuiltSnapshot = CreatePlaylistLibraryResolveIndexSnapshotUnsafe(
                    storageRowsOwner,
                    cancellationToken,
                    out storageRowsVersion);
                ownedCollectionVersion = CollectionVersion;
            }
            rebuiltSnapshot = rebuiltSnapshot.WithMetadata(
                version: 0,
                buildElapsedMs: stopwatch.ElapsedMilliseconds,
                invalidationVersion: invalidationVersion,
                ownedCollectionVersion: invalidationOwnedCollectionVersion == ownedCollectionVersion
                    ? invalidationOwnedCollectionVersion
                    : ownedCollectionVersion,
                bmsRowsVersion: storageRowsVersion.BmsRowsVersion,
                bmsonRowsVersion: storageRowsVersion.BmsonRowsVersion);

            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                snapshot = playlistLibraryResolveIndexSnapshot;
                if (snapshot != null)
                {
                    if (IsPlaylistLibraryResolveIndexSnapshotCurrent(
                        snapshot,
                        storageRowsOwner,
                        CollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistLibraryResolveIndexSnapshot = null;
                    playlistLibraryResolveIndexInvalidationVersion++;
                    playlistLibraryResolveIndexInvalidationOwnedCollectionVersion = CollectionVersion;
                    staleRetryCount++;
                    continue;
                }
                if (playlistLibraryResolveIndexInvalidationVersion != invalidationVersion
                    || CollectionVersion != ownedCollectionVersion
                    || !IsStorageRowsVersionCurrent(storageRowsOwner, storageRowsVersion))
                {
                    staleRetryCount++;
                    continue;
                }
                if (IsDigestMutationWindowActive())
                {
                    staleRetryCount++;
                    continue;
                }
                rebuiltSnapshot = rebuiltSnapshot.WithMetadata(
                    version: Interlocked.Increment(ref playlistLibraryResolveIndexSnapshotVersion),
                    buildElapsedMs: rebuiltSnapshot.BuildElapsedMs,
                    invalidationVersion: rebuiltSnapshot.InvalidationVersion,
                    ownedCollectionVersion: rebuiltSnapshot.OwnedCollectionVersion,
                    bmsRowsVersion: rebuiltSnapshot.BmsRowsVersion,
                    bmsonRowsVersion: rebuiltSnapshot.BmsonRowsVersion);
                playlistLibraryResolveIndexSnapshot = rebuiltSnapshot;
                cacheHit = false;
                return rebuiltSnapshot;
            }
        }
    }

    /// <summary>
    /// playlist resolve index の runtime state を source version と同じ境界で取得します。
    /// </summary>
    /// <param name="storageRowsOwner">storage row version の所有者。</param>
    /// <returns>cache 状態と version の値。</returns>
    internal (
        bool IsCached,
        int SnapshotVersion,
        long BuildElapsedMs,
        int InvalidationVersion,
        int OwnedCollectionVersion) GetPlaylistLibraryResolveIndexRuntimeState(
            CatalogStorageRowsOwner storageRowsOwner)
    {
        if (storageRowsOwner == null)
        {
            throw new ArgumentNullException(nameof(storageRowsOwner));
        }

        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            PlaylistLibraryResolveIndexSnapshot snapshot = playlistLibraryResolveIndexSnapshot;
            int currentOwnedCollectionVersion = CollectionVersion;
            if (IsPlaylistLibraryResolveIndexSnapshotCurrent(
                snapshot,
                storageRowsOwner,
                currentOwnedCollectionVersion))
            {
                return (
                    IsCached: true,
                    SnapshotVersion: snapshot.Version,
                    BuildElapsedMs: snapshot.BuildElapsedMs,
                    InvalidationVersion: snapshot.InvalidationVersion,
                    OwnedCollectionVersion: snapshot.OwnedCollectionVersion);
            }
            return (
                IsCached: false,
                SnapshotVersion: 0,
                BuildElapsedMs: 0L,
                InvalidationVersion: playlistLibraryResolveIndexInvalidationVersion,
                OwnedCollectionVersion: currentOwnedCollectionVersion);
        }
    }

    /// <summary>
    /// playlist resolve index が構築済みかを、build を起こさずに確認します。
    /// </summary>
    /// <returns>snapshot が存在すれば true。</returns>
    internal bool IsPlaylistLibraryResolveIndexWarm()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            return playlistLibraryResolveIndexSnapshot != null;
        }
    }

    private bool IsPlaylistLibraryResolveIndexSnapshotCurrent(
        PlaylistLibraryResolveIndexSnapshot snapshot,
        CatalogStorageRowsOwner storageRowsOwner,
        int currentOwnedCollectionVersion)
    {
        return snapshot != null
            && snapshot.OwnedCollectionVersion == currentOwnedCollectionVersion
            && storageRowsOwner.BmsRowsVersion == snapshot.BmsRowsVersion
            && storageRowsOwner.BmsonRowsVersion == snapshot.BmsonRowsVersion;
    }

    private bool IsStorageRowsVersionCurrent(
        CatalogStorageRowsOwner storageRowsOwner,
        StorageRowsVersionSnapshot storageRowsVersion)
    {
        return storageRowsOwner.BmsRowsVersion == storageRowsVersion.BmsRowsVersion
            && storageRowsOwner.BmsonRowsVersion == storageRowsVersion.BmsonRowsVersion;
    }

    private bool TryCreateOwnedCanonicalPlaylistChartFacts(
        CatalogStorageRowsOwner storageRowsOwner,
        IEnumerable<(LibraryChartKind Kind, string Path)> paths,
        out List<PlaylistLibraryResolveChartFact> facts)
    {
        facts = [];
        using (storageRowsOwner.WriteGate.GetReaderGuard())
        {
            lock (storageRowsOwner.VersionGate)
            {
                lock (gate)
                {
                    if (!initialized)
                    {
                        facts = null;
                        return false;
                    }

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach ((LibraryChartKind Kind, string Path) request in paths ?? [])
                    {
                        if (string.IsNullOrWhiteSpace(request.Path))
                        {
                            return false;
                        }

                        string identityKey = (request.Kind == LibraryChartKind.Bmson ? "bmson" : "bms")
                            + "\u001f"
                            + request.Path;
                        if (!seen.Add(identityKey))
                        {
                            continue;
                        }

                        PlaylistLibraryResolveIndexStoreWorkObserver?.Invoke("playlist_resolve_exact_path_query");
                        if (!collection.TryGetCanonicalChartRefForExactPath(
                            request.Kind,
                            request.Path,
                            out LibraryChartRef chartRef,
                            out OwnedChartCanonicalOrderKey stableOrder))
                        {
                            return false;
                        }
                        PlaylistLibraryResolveIndexStoreWorkObserver?.Invoke("playlist_resolve_exact_path_entry_visited");
                        var fact = PlaylistLibraryResolveChartFact.FromChart(
                            chartRef,
                            stableOrder);
                        if (fact == null)
                        {
                            return false;
                        }
                        facts.Add(fact);
                    }
                    return true;
                }
            }
        }
    }

    private PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshotUnsafe(
        CatalogStorageRowsOwner storageRowsOwner,
        CancellationToken cancellationToken,
        out StorageRowsVersionSnapshot storageRowsVersion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (storageRowsOwner.VersionGate)
        {
            StorageRowsVersionSnapshot currentVersion = storageRowsOwner.CaptureVersionSnapshot();
            lock (gate)
            {
                if (!IsCurrent(currentVersion.BmsRowsVersion, currentVersion.BmsonRowsVersion))
                {
                    throw new InvalidOperationException("Owned chart collection storage row version is not current.");
                }
                storageRowsVersion = currentVersion;
                List<PlaylistLibraryResolveChartFact> facts = collection.CreatePlaylistLibraryResolveChartFactSnapshot(
                    cancellationToken.ThrowIfCancellationRequested,
                    PlaylistLibraryResolveIndexStoreWorkObserver);
                cancellationToken.ThrowIfCancellationRequested();
                return PlaylistLibraryResolveIndexSnapshot.FromLibraryChartFacts(
                    facts,
                    cancellationToken.ThrowIfCancellationRequested,
                    PlaylistLibraryResolveIndexStoreWorkObserver);
            }
        }
    }

    private static bool AddPlaylistResolveRemovalFact(
        List<PlaylistLibraryResolveChartFact> removals,
        ISet<string> removalKeys,
        LibraryChartKind kind,
        string path,
        string md5,
        string sha256)
    {
        if (removals == null
            || removalKeys == null
            || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(md5))
        {
            return false;
        }

        string key = (kind == LibraryChartKind.Bmson ? "bmson" : "bms")
            + "\u001f"
            + path
            + "\u001f"
            + md5.Trim()
            + "\u001f"
            + (sha256?.Trim() ?? string.Empty);
        if (removalKeys.Add(key))
        {
            removals.Add(PlaylistLibraryResolveChartFact.ForRemoval(kind, path, md5, sha256));
        }
        return true;
    }

    private static void AddPlaylistResolveAdditionPath(
        List<(LibraryChartKind Kind, string Path)> additionPaths,
        ISet<string> additionKeys,
        LibraryChartKind kind,
        string path)
    {
        if (additionPaths == null
            || additionKeys == null
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string key = (kind == LibraryChartKind.Bmson ? "bmson" : "bms")
            + "\u001f"
            + path;
        if (additionKeys.Add(key))
        {
            additionPaths.Add((kind, path));
        }
    }

    private static LibraryChartKind ToLibraryChartKind(ChartFileKind kind)
    {
        return kind == ChartFileKind.Bmson ? LibraryChartKind.Bmson : LibraryChartKind.Bms;
    }
}
