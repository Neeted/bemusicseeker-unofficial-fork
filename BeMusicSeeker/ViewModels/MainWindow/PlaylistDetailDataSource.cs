using System;
using System.Collections.Generic;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Exposes only the library and playlist data required to build playlist-detail source rows.
/// </summary>
internal interface IPlaylistDetailDataSource
{
    int ChartInfoIndexVersion { get; }

    int ScoreSnapshotVersion { get; }

    long OwnedChartCollectionVersion { get; }

    void EnsureEntriesLoaded(BMSTable table, string reason);

    BMSLibrary.ScoreSnapshot GetScoreSnapshot();

    PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount);

    LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5);

    PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        LR2SongDBExtended.chart_info chartInfo,
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

    public long OwnedChartCollectionVersion => library.OwnedChartCollectionVersion;

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

    public LR2SongDBExtended.chart_info ResolveChartInfo(string sha256, string md5)
    {
        return library.ResolveChartInfo(sha256, md5);
    }

    public PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        LR2SongDBExtended.chart_info chartInfo,
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
