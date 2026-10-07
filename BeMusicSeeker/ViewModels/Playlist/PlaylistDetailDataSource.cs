using System;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Exposes only the library and playlist data required to build playlist-detail source rows.
/// </summary>
internal interface IPlaylistDetailDataSource
{
    int ChartInfoIndexVersion { get; }

    int ScoreSnapshotVersion { get; }

    long OwnedCollectionVersion { get; }

    void EnsureEntriesLoaded(BMSTable table, string reason);

    BMSLibrary.ScoreSnapshot GetScoreSnapshot();

    PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount);

    BMSLibrary.PlaylistLibraryResolveIndexRuntimeState GetResolveIndexRuntimeState();

    BeMusicSeeker.Models.ChartDetails ResolveChartInfo(string sha256, string md5);

    PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        BeMusicSeeker.Models.ChartDetails chartInfo,
        LibraryChartRef resolvedChartRef);
}

internal sealed class PlaylistDetailDataSource : IPlaylistDetailDataSource
{
    private readonly BMSLibrary library;

    private readonly BMSPlaylist playlists;

    private readonly MainChartRowProjectionOwner rowProjection;

    internal PlaylistDetailDataSource(
        BMSLibrary library,
        BMSPlaylist playlists,
        MainChartRowProjectionOwner rowProjection)
    {
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.playlists = playlists ?? throw new ArgumentNullException(nameof(playlists));
        this.rowProjection = rowProjection ?? throw new ArgumentNullException(nameof(rowProjection));
    }

    public int ChartInfoIndexVersion => library.ChartInfoIndexVersion;

    public int ScoreSnapshotVersion => library.GetScoreRuntimeStateForDiagnostics().SnapshotVersion;

    public long OwnedCollectionVersion => library.OwnedCollectionVersion;

    public void EnsureEntriesLoaded(BMSTable table, string reason)
    {
        playlists.EnsurePlaylistEntriesLoaded(table, reason);
    }

    public BMSLibrary.ScoreSnapshot GetScoreSnapshot()
    {
        return library.GetScoreSnapshotForDiagnostics();
    }

    public PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount)
    {
        return library.GetPlaylistLibraryResolveIndexSnapshot(
            cancellationToken,
            out cacheHit,
            out staleRetryCount);
    }

    public BMSLibrary.PlaylistLibraryResolveIndexRuntimeState GetResolveIndexRuntimeState()
    {
        return library.GetPlaylistLibraryResolveIndexRuntimeState();
    }

    public BeMusicSeeker.Models.ChartDetails ResolveChartInfo(string sha256, string md5)
    {
        return library.ResolveChartInfo(sha256, md5);
    }

    public PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        BeMusicSeeker.Models.ChartDetails chartInfo,
        LibraryChartRef resolvedChartRef)
    {
        return rowProjection.CreatePlaylistDetailSourceRow(
            library,
            entry,
            resolvedChart,
            score,
            chartInfo,
            resolvedChartRef);
    }
}
