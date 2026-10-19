using System;
using System.Collections.Generic;
using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>候補の所属・digest・順序だけに従属する任意索引を所有します。</summary>
internal sealed partial class CatalogOwnedCollectionOwner
{
    private readonly object lockPlaylistLibraryResolveIndexSnapshot = new();
    private PlaylistLibraryResolveIndexSnapshot playlistLibraryResolveIndexSnapshot;
    private int playlistLibraryResolveIndexSnapshotVersion;
    private int playlistLibraryResolveIndexInvalidationVersion;
    /// <summary>実格納処理を観測します。callback はownerへ再入しません。</summary>
    internal Action<string> PlaylistLibraryResolveIndexStoreWorkObserver { get; set; }
    /// <summary>全置換時だけ任意索引を破棄します。局所変更は確定境界で差分反映します。</summary>
    internal void InvalidatePlaylistLibraryResolveIndexSnapshot()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            playlistLibraryResolveIndexSnapshot = null;
            playlistLibraryResolveIndexInvalidationVersion++;
        }
    }
    /// <summary>同じカタログ読取り区間で候補索引を捕捉します。内容更新で候補rootを再構築しません。</summary>
    internal PlaylistLibraryResolveIndexSnapshot GetPlaylistLibraryResolveIndexSnapshot(
        CancellationToken cancellationToken, out bool cacheHit, out int staleRetryCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (WriteGate.GetReaderGuard())
        {
            lock (gate)
                lock (lockPlaylistLibraryResolveIndexSnapshot)
                {
                    staleRetryCount = 0;
                    cacheHit = playlistLibraryResolveIndexSnapshot != null;
                    if (playlistLibraryResolveIndexSnapshot == null)
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        List<PlaylistLibraryResolveChartFact> facts = collection.CreatePlaylistLibraryResolveChartFactSnapshot(
                    cancellationToken.ThrowIfCancellationRequested, PlaylistLibraryResolveIndexStoreWorkObserver);
                        playlistLibraryResolveIndexSnapshot = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartFacts(facts,
                            cancellationToken.ThrowIfCancellationRequested, PlaylistLibraryResolveIndexStoreWorkObserver)
                            .WithMetadata(++playlistLibraryResolveIndexSnapshotVersion, watch.ElapsedMilliseconds,
                                playlistLibraryResolveIndexInvalidationVersion, CatalogRevision);
                    }
                    return playlistLibraryResolveIndexSnapshot;
                }
        }
    }
    /// <summary>任意索引を構築せず、その現在の依存版と捕捉カタログ版を返します。</summary>
    internal (bool IsCached, int SnapshotVersion, long BuildElapsedMs, int InvalidationVersion, int CatalogRevision)
        GetPlaylistLibraryResolveIndexRuntimeState()
    {
        lock (gate)
            lock (lockPlaylistLibraryResolveIndexSnapshot)
            {
                PlaylistLibraryResolveIndexSnapshot snapshot = playlistLibraryResolveIndexSnapshot;
                return (snapshot != null, snapshot?.Version ?? 0, snapshot?.BuildElapsedMs ?? 0,
                    playlistLibraryResolveIndexInvalidationVersion, CatalogRevision);
            }
    }
    /// <summary>候補索引が構築済みかを返し、構築は起こしません。</summary>
    internal bool IsPlaylistLibraryResolveIndexWarm()
    {
        lock (lockPlaylistLibraryResolveIndexSnapshot)
        {
            return playlistLibraryResolveIndexSnapshot != null;
        }
    }
}
