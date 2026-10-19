using System;
using System.Collections.Generic;
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

    /// <summary>score ownerの現在確定位置です。sourceの反映済み位置とは区別します。</summary>
    int ScoreSnapshotVersion { get; }

    long CatalogRevision { get; }

    void EnsureEntriesLoaded(BMSTable table, string reason);

    /// <summary>同じ観測点のlookupと基準版を捕捉します。初回は最初の投影前に一回捕捉し、対象全entryで共有します。</summary>
    BMSLibrary.ScoreSnapshot GetScoreSnapshot();

    /// <summary>必要な候補と current を一つのカタログ読取り区間で捕捉します。</summary>
    CatalogReadSnapshot CapturePlaylistReadSnapshot(IEnumerable<PlaylistEntryLookupKey> keys, CancellationToken cancellationToken);

    PlaylistLibraryResolveIndexSnapshot GetResolveIndexSnapshot(
        CancellationToken cancellationToken,
        out bool cacheHit,
        out int staleRetryCount);

    BMSLibrary.PlaylistLibraryResolveIndexRuntimeState GetResolveIndexRuntimeState();

    BeMusicSeeker.Models.ChartDetails ResolveChartInfo(string sha256, string md5);

    /// <summary>同じentry/currentと捕捉済み参照入力から一行を投影します。参照入力はgetterの可変モデル読取りへ置き換えません。</summary>
    PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        BeMusicSeeker.Models.ChartDetails chartInfo,
        LibraryChartRef resolvedChartRef,
        PlaylistEntryValue entryValue = null,
        long entryOrder = 0,
        ChartListReferenceInput referenceInput = null);
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

    /// <summary>確定結果の接続元が同じ library であることを検査する識別です。</summary>
    internal BMSLibrary Library => library;
    public int ChartInfoIndexVersion => library.ChartInfoIndexVersion;

    public int ScoreSnapshotVersion => library.GetScoreRuntimeStateForDiagnostics().SnapshotVersion;

    public long CatalogRevision => library.CatalogRevision;

    public void EnsureEntriesLoaded(BMSTable table, string reason)
    {
        playlists.EnsurePlaylistEntriesLoaded(table, reason);
        // 初回の表示入力では通常一覧と同じ専門警告を準備します。差分投影ではこの入口を通りません。
        library.GetResourceHealthIndexSnapshotForView("playlist_detail_source");
    }

    public CatalogReadSnapshot CapturePlaylistReadSnapshot(IEnumerable<PlaylistEntryLookupKey> keys, CancellationToken cancellationToken)
        => library.CapturePlaylistCatalogReadSnapshot(keys, cancellationToken);

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

    /// <summary>要求が一括捕捉した参照入力を共有し、専門警告と再生状態だけを既存投影へ接続します。</summary>
    public PlaylistDetailSourceRow CreateSourceRow(
        BMSTableEntry entry,
        ChartFile resolvedChart,
        BMSScore score,
        BeMusicSeeker.Models.ChartDetails chartInfo,
        LibraryChartRef resolvedChartRef,
        PlaylistEntryValue entryValue = null,
        long entryOrder = 0,
        ChartListReferenceInput referenceInput = null)
    {
        PlaylistDetailSourceRow row = rowProjection.CreatePlaylistDetailSourceRow(
            library,
            entry,
            resolvedChart,
            score,
            chartInfo,
            resolvedChartRef,
            entryValue,
            entryOrder,
            referenceInput);
        if (referenceInput != null) { row = row.WithReferenceDisplay(referenceInput); }
        return row.WithResourceHealthProjection(resolvedChart == null ? ResourceHealthWarningProjection.Empty
            : library.TryGetCurrentResourceHealthWarningProjection(resolvedChart));
    }
}
