using System;
using System.Collections.Generic;
using System.Threading;

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

    /// <summary>
    /// playlist resolve root の実格納処理を記録する任意の内部 observer です。
    /// production では設定せず、設定時も owner へ再入しません。
    /// </summary>
    internal Action<string> PlaylistLibraryResolveIndexStoreWorkObserver { get; set; }

    /// <summary>
    /// playlist resolve snapshot を破棄し、次回要求で再構築します。
    /// </summary>
    internal void InvalidatePlaylistLibraryResolveIndexSnapshot()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            playlistLibraryResolveIndexSnapshot = null;
            playlistLibraryResolveIndexInvalidationVersion++;
        }
    }

    /// <summary>
    /// 自身の mutation 後の集合版を、構築済み playlist resolve snapshot へ反映します。
    /// </summary>
    internal void RebasePlaylistLibraryResolveIndexSnapshot()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            PlaylistLibraryResolveIndexSnapshot snapshot = playlistLibraryResolveIndexSnapshot;
            if (snapshot == null)
            {
                return;
            }

            int ownedCollectionVersion = OwnedCollectionVersion;
            if (snapshot.OwnedCollectionVersion == ownedCollectionVersion)
            {
                return;
            }

            playlistLibraryResolveIndexSnapshot = snapshot.WithMetadata(
                version: snapshot.Version,
                buildElapsedMs: snapshot.BuildElapsedMs,
                invalidationVersion: snapshot.InvalidationVersion,
                ownedCollectionVersion: ownedCollectionVersion);
        }
    }

    /// <summary>
    /// warm な playlist resolve root に mutation facts を局所適用します。
    /// facts 不足または全置換境界では既存の全失効契約へ戻します。
    /// </summary>
    /// <param name="digestChanges">digest の旧新 facts。</param>
    /// <param name="digestMutationApplied">digest facts が正本へ適用済みか。</param>
    /// <param name="installedLookupMutation">installed lookup の旧新 facts。</param>
    /// <param name="addedCharts">追加 chart facts。</param>
    /// <param name="ownedCollectionChanged">owned collection に変更があるか。</param>
    /// <param name="playlistResolveIndexInvalidated">事前に全失効が必要と判断されたか。</param>
    /// <param name="bmsonCanonicalOrderNormalized">BMSON canonical 順序を正規化したか。</param>
    /// <returns>全失効された場合は <see langword="true"/>。</returns>
    internal bool ApplyPlaylistLibraryResolveIndexMutation(
        IReadOnlyList<LibraryChartDigestChange> digestChanges,
        bool digestMutationApplied,
        InstalledChartLookupMutation installedLookupMutation,
        IReadOnlyList<ChartFile> addedCharts,
        bool ownedCollectionChanged,
        bool playlistResolveIndexInvalidated,
        bool bmsonCanonicalOrderNormalized)
    {
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
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            return true;
        }

        // cold consumer には mutation 用の optional index を構築しません。
        if (snapshot == null)
        {
            return false;
        }

        if (digestChangeCount == 0
            && installedLookupMutation?.HasChanges != true
            && (addedCharts?.Count ?? 0) == 0)
        {
            // level/mode などの基本表示値だけの変更は、hash/path の root を維持して集合版だけを捕捉します。
            RebasePlaylistLibraryResolveIndexSnapshot();
            return false;
        }

        var removals = new List<PlaylistLibraryResolveChartFact>();
        var removalKeys = new HashSet<string>(StringComparer.Ordinal);
        var additionPaths = new List<(ChartFileKind Kind, string Path)>();
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
                        ToChartFileKind(removed.Kind),
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
                        ToChartFileKind(moved.Kind),
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
                            ToChartFileKind(moved.Kind),
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
                        ToChartFileKind(added.Kind),
                        added.Path);
                }
            }
        }

        if (!factsComplete || (removals.Count == 0 && additionPaths.Count == 0))
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            return true;
        }

        if (!TryCreateOwnedCanonicalPlaylistChartFacts(
            additionPaths,
            out List<PlaylistLibraryResolveChartFact> additions))
        {
            InvalidatePlaylistLibraryResolveIndexSnapshot();
            return true;
        }

        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            if (!ReferenceEquals(snapshot, playlistLibraryResolveIndexSnapshot))
            {
                playlistLibraryResolveIndexSnapshot = null;
                playlistLibraryResolveIndexInvalidationVersion++;
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
                return true;
            }

            playlistLibraryResolveIndexSnapshot = nextSnapshot.WithMetadata(
                version: Interlocked.Increment(ref playlistLibraryResolveIndexSnapshotVersion),
                buildElapsedMs: snapshot.BuildElapsedMs,
                invalidationVersion: playlistLibraryResolveIndexInvalidationVersion,
                ownedCollectionVersion: OwnedCollectionVersion);
        }
        return false;
    }

    /// <summary>
    /// playlist detail の entry hash 解決に使う owned collection 隣接 index を返します。
    /// 自身の読取り排他と集合排他で構築前の集合版を捕捉し、構築後の集合版・失効版を確認して公開します。
    /// </summary>
    /// <param name="cancellationToken">構築中の cancellation token。</param>
    /// <param name="cacheHit">既存 snapshot を再利用した場合は true。</param>
    /// <param name="staleRetryCount">version 競合による再試行回数。</param>
    /// <returns>playlist detail 用 resolve index。</returns>
    internal PlaylistLibraryResolveIndexSnapshot GetPlaylistLibraryResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        PlaylistLibraryResolveIndexSnapshot snapshot;
        staleRetryCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool waitForDigestWindow = false;
            int invalidationVersion;
            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                snapshot = playlistLibraryResolveIndexSnapshot;
                int currentOwnedCollectionVersion = OwnedCollectionVersion;
                if (snapshot != null)
                {
                    if (IsPlaylistLibraryResolveIndexSnapshotCurrent(
                        snapshot,
                        currentOwnedCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistLibraryResolveIndexSnapshot = null;
                    playlistLibraryResolveIndexInvalidationVersion++;
                }
                if (IsDigestMutationWindowActive())
                {
                    waitForDigestWindow = true;
                }
                invalidationVersion = playlistLibraryResolveIndexInvalidationVersion;
            }
            if (waitForDigestWindow)
            {
                WaitForDigestMutationWindowIdle(cancellationToken);
                staleRetryCount++;
                continue;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            PlaylistLibraryResolveIndexSnapshot rebuiltSnapshot;
            int ownedCollectionVersion;
            using (WriteGate.GetReaderGuard())
            {
                rebuiltSnapshot = CreatePlaylistLibraryResolveIndexSnapshotUnsafe(
                    cancellationToken,
                    out ownedCollectionVersion);
            }
            rebuiltSnapshot = rebuiltSnapshot.WithMetadata(
                version: 0,
                buildElapsedMs: stopwatch.ElapsedMilliseconds,
                invalidationVersion: invalidationVersion,
                ownedCollectionVersion: ownedCollectionVersion);

            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                snapshot = playlistLibraryResolveIndexSnapshot;
                if (snapshot != null)
                {
                    if (IsPlaylistLibraryResolveIndexSnapshotCurrent(
                        snapshot,
                        OwnedCollectionVersion))
                    {
                        cacheHit = true;
                        return snapshot;
                    }
                    playlistLibraryResolveIndexSnapshot = null;
                    playlistLibraryResolveIndexInvalidationVersion++;
                    staleRetryCount++;
                    continue;
                }
                if (playlistLibraryResolveIndexInvalidationVersion != invalidationVersion
                    || OwnedCollectionVersion != ownedCollectionVersion)
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
                    ownedCollectionVersion: rebuiltSnapshot.OwnedCollectionVersion);
                playlistLibraryResolveIndexSnapshot = rebuiltSnapshot;
                cacheHit = false;
                return rebuiltSnapshot;
            }
        }
    }

    /// <summary>
    /// 自身の集合版で playlist resolve index の最新性を確認し、構築せず runtime state を取得します。
    /// </summary>
    /// <returns>cache 状態と version の値。</returns>
    internal (
        bool IsCached,
        int SnapshotVersion,
        long BuildElapsedMs,
        int InvalidationVersion,
        int OwnedCollectionVersion) GetPlaylistLibraryResolveIndexRuntimeState()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            PlaylistLibraryResolveIndexSnapshot snapshot = playlistLibraryResolveIndexSnapshot;
            int currentOwnedCollectionVersion = OwnedCollectionVersion;
            if (IsPlaylistLibraryResolveIndexSnapshotCurrent(
                snapshot,
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

    private static bool IsPlaylistLibraryResolveIndexSnapshotCurrent(
        PlaylistLibraryResolveIndexSnapshot snapshot,
        int currentOwnedCollectionVersion)
    {
        return snapshot != null
            && snapshot.OwnedCollectionVersion == currentOwnedCollectionVersion;
    }

    private bool TryCreateOwnedCanonicalPlaylistChartFacts(
        IEnumerable<(ChartFileKind Kind, string Path)> paths,
        out List<PlaylistLibraryResolveChartFact> facts)
    {
        facts = [];
        using (WriteGate.GetReaderGuard())
        {
            lock (gate)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach ((ChartFileKind Kind, string Path) request in paths ?? [])
                {
                    if (string.IsNullOrWhiteSpace(request.Path))
                    {
                        return false;
                    }

                    string identityKey = (request.Kind == ChartFileKind.Bmson ? "bmson" : "bms")
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
                        ChartFileProjection.CaptureBasicSnapshot(collection.ResolveCurrentChart(chartRef)),
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

    /// <summary>呼出側の読取り排他内で、自身の集合からfactsと構築前の集合版を同じ集合排他で捕捉します。</summary>
    /// <param name="cancellationToken">構築中の取消要求。</param>
    /// <param name="ownedCollectionVersion">factsを読み出す前に捕捉した、公開時の最新性確認に使う単一の集合版。</param>
    /// <returns>構築したplaylist参照解決索引。公開版の付与と最新性確認は呼出側が行います。</returns>
    private PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshotUnsafe(
        CancellationToken cancellationToken,
        out int ownedCollectionVersion)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ownedCollectionVersion = OwnedCollectionVersion;
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

    private static bool AddPlaylistResolveRemovalFact(
        List<PlaylistLibraryResolveChartFact> removals,
        ISet<string> removalKeys,
        ChartFileKind kind,
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

        string key = (kind == ChartFileKind.Bmson ? "bmson" : "bms")
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
        List<(ChartFileKind Kind, string Path)> additionPaths,
        ISet<string> additionKeys,
        ChartFileKind kind,
        string path)
    {
        if (additionPaths == null
            || additionKeys == null
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string key = (kind == ChartFileKind.Bmson ? "bmson" : "bms")
            + "\u001f"
            + path;
        if (additionKeys.Add(key))
        {
            additionPaths.Add((kind, path));
        }
    }

    private static ChartFileKind ToChartFileKind(ChartFileKind kind)
    {
        return kind;
    }
}
