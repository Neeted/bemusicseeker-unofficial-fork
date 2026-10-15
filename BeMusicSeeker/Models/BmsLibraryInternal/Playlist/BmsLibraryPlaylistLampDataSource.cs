using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// BMSPlaylist と BMSLibrary の current owner state を immutable lamp input へ投影します。
/// </summary>
internal sealed class BmsLibraryPlaylistLampDataSource : IPlaylistLampViewerDataSource
{
    private readonly BMSPlaylist playlist;

    private readonly BMSLibrary library;

    private readonly Func<PlaylistLampHistoricalScoreSourceContext> historicalSourceContextFactory;

    private readonly record struct RawEntrySnapshot(
        string Folder,
        string Md5,
        string Sha256,
        bool IsRemoved);

    /// <summary>
    /// production playlist lamp source を生成します。
    /// </summary>
    /// <param name="playlist">playlist owner。</param>
    /// <param name="library">catalog と score owner。</param>
    /// <param name="historicalSourceContextFactory">
    /// active provider の read-only history source context。null の場合は Latest のみを提供します。
    /// </param>
    public BmsLibraryPlaylistLampDataSource(
        BMSPlaylist playlist,
        BMSLibrary library,
        Func<PlaylistLampHistoricalScoreSourceContext> historicalSourceContextFactory = null)
    {
        this.playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        this.library = library ?? throw new ArgumentNullException(nameof(library));
        this.historicalSourceContextFactory = historicalSourceContextFactory;
    }

    /// <summary>
    /// immutable入力を捕捉します。開始済みcaptureの実終端は呼出元sessionが所有し、
    /// 捕捉内の資源は通常の所有者が解放し、source自体は購読や解放対象を保持しません。
    /// </summary>
    /// <param name="query">捕捉するplaylistと基準日。</param>
    /// <param name="cancellationToken">捕捉の取消token。</param>
    /// <returns>captureの実終端までを表す要求Task。</returns>
    public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
        PlaylistLampViewerQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PlaylistLampAggregationRequest>(Task.Run(async () =>
        {
            BMSTable table = FindTable(query.PlaylistId);
            bool pending = false;
            if (table != null)
            {
                using (table.ReaderWriterLock.GetReaderGuard())
                {
                    pending = table.PlaylistEntriesLoadState is PlaylistEntriesLoadState.NotLoaded or PlaylistEntriesLoadState.Loading;
                }
            }
            if (pending)
            {
                // 準備の所有権は起動側に残し、この捕捉の待機だけを取消可能にします。
                await playlist.StartupReadiness.WaitForRequiredPlaylistReadinessAsync(cancellationToken).ConfigureAwait(false);
            }
            // 準備中の置換・削除も含め、安定IDで現在の表を取り直します。
            return CaptureCore(query, cancellationToken);
        }, cancellationToken));
    }

    private PlaylistLampAggregationRequest CaptureCore(
        PlaylistLampViewerQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BMSTable table = FindTable(query.PlaylistId);
        if (table == null)
        {
            return PlaylistLampAggregationRequest.Deleted(query.PlaylistId, query: query);
        }

        PlaylistEntriesLoadState entriesLoadState;
        string failureMessage;
        string[] folderOrder;
        RawEntrySnapshot[] entries;
        DateTime playlistLastUpdated;
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entriesLoadState = table.PlaylistEntriesLoadState;
            failureMessage = table.EntriesLoadErrorMessage;
            if (entriesLoadState == PlaylistEntriesLoadState.Loading
                || entriesLoadState == PlaylistEntriesLoadState.NotLoaded
                || entriesLoadState == PlaylistEntriesLoadState.Failed)
            {
                folderOrder = [];
                entries = [];
                playlistLastUpdated = default;
            }
            else
            {
                folderOrder = (table.folder_list ?? []).ToArray();
                entries = (table.entries ?? [])
                    .Where(entry => entry != null)
                    .Select(entry => new RawEntrySnapshot(
                        entry.folder,
                        entry.md5,
                        entry.sha256,
                        entry.is_removed))
                    .ToArray();
                playlistLastUpdated = table.last_update;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (entriesLoadState is PlaylistEntriesLoadState.Loading or PlaylistEntriesLoadState.NotLoaded or PlaylistEntriesLoadState.Failed)
        {
            return PlaylistLampAggregationRequest.Failed(
                query.PlaylistId,
                string.IsNullOrWhiteSpace(failureMessage) ? "Playlist entries are unavailable after initialization." : failureMessage,
                query: query);
        }

        PlaylistLibraryResolveIndexSnapshot resolveIndex = library.GetPlaylistLibraryResolveIndexSnapshot(
            cancellationToken,
            out _,
            out _);
        cancellationToken.ThrowIfCancellationRequested();
        BMSLibrary.ScoreSnapshot scoreSnapshot = library.GetScoreSnapshotForDiagnostics();
        var currentLampScoreSnapshot = PlaylistLampScoreSnapshot.FromBmsLibrarySnapshot(scoreSnapshot);
        PlaylistLampHistoricalScoreSnapshotResult historicalResult = ResolveHistoricalScoreSnapshot(
            query,
            currentLampScoreSnapshot,
            cancellationToken);
        PlaylistLampScoreSnapshot lampScoreSnapshot = historicalResult.ScoreSnapshot;
        var lampEntries = new List<PlaylistLampEntrySnapshot>(entries.Length);
        foreach (RawEntrySnapshot entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibraryChartRef resolved = resolveIndex.ResolveChartForPlaylistHash(entry.Md5, entry.Sha256);
            string resolvedMd5 = resolved?.Md5;
            string resolvedSha256 = resolved?.Sha256;
            BeMusicSeeker.Models.ChartDetails chartInfo = library.ResolveChartInfo(entry.Sha256, entry.Md5);
            string chartInfoSha256 = chartInfo?.sha256;
            string identityKey = !string.IsNullOrWhiteSpace(resolvedMd5)
                ? "md5:" + resolvedMd5
                : !string.IsNullOrWhiteSpace(entry.Md5)
                    ? "md5:" + entry.Md5
                    : !string.IsNullOrWhiteSpace(resolvedSha256)
                        ? "sha256:" + resolvedSha256
                        : !string.IsNullOrWhiteSpace(entry.Sha256)
                            ? "sha256:" + entry.Sha256
                            : !string.IsNullOrWhiteSpace(chartInfoSha256)
                                ? "chart-info-sha256:" + chartInfoSha256
                                : resolved?.Path;
            lampEntries.Add(new PlaylistLampEntrySnapshot(
                entry.Folder,
                identityKey,
                resolved != null && !string.IsNullOrWhiteSpace(resolved.Path),
                entry.Md5,
                entry.Sha256,
                resolved?.Path,
                resolvedMd5,
                resolvedSha256,
                entry.IsRemoved,
                string.Equals(entry.Md5, BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, StringComparison.OrdinalIgnoreCase),
                chartInfoSha256));
        }

        DateTime? playlistLastUpdatedValue = playlistLastUpdated == default ? null : playlistLastUpdated;
        return new PlaylistLampAggregationRequest(
            query.PlaylistId,
            folderOrder,
            lampEntries,
            lampScoreSnapshot,
            playlistLastUpdatedValue,
            inputState: PlaylistLampInputState.Loaded,
            query: query,
            historicalDateRange: historicalResult.DateRange,
            historicalStatus: historicalResult.Status,
            historicalFailureMessage: historicalResult.FailureMessage);
    }

    private PlaylistLampHistoricalScoreSnapshotResult ResolveHistoricalScoreSnapshot(
        PlaylistLampViewerQuery query,
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        CancellationToken cancellationToken)
    {
        if (historicalSourceContextFactory == null)
        {
            if (!query.SelectedLocalDate.HasValue)
            {
                return new PlaylistLampHistoricalScoreSnapshotResult(
                    currentScoreSnapshot,
                    new PlaylistLampHistoricalDateRange(null, DateTime.Today),
                    null,
                    PlaylistLampHistoricalSnapshotStatus.Latest);
            }
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot.WithScores(
                    new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                    "Historical score source is not configured.",
                    ScoreTableLoadStatus.Failed),
                new PlaylistLampHistoricalDateRange(null, DateTime.Today),
                query.SelectedLocalDate,
                PlaylistLampHistoricalSnapshotStatus.Unavailable,
                "Historical score source is not configured.");
        }

        PlaylistLampHistoricalScoreSourceContext sourceContext;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceContext = historicalSourceContextFactory();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return CreateHistoricalSourceUnavailable(query, currentScoreSnapshot, exception);
        }
        return new PlaylistLampHistoricalScoreSnapshotReader().Read(
            sourceContext,
            currentScoreSnapshot,
            query.SelectedLocalDate,
            cancellationToken);
    }

    private static PlaylistLampHistoricalScoreSnapshotResult CreateHistoricalSourceUnavailable(
        PlaylistLampViewerQuery query,
        PlaylistLampScoreSnapshot currentScoreSnapshot,
        Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception?.Message)
            ? "Historical score source could not be resolved."
            : exception.Message;
        var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
        PlaylistLampHistoricalDateRange range = new(null, today);
        if (!query.SelectedLocalDate.HasValue)
        {
            return new PlaylistLampHistoricalScoreSnapshotResult(
                currentScoreSnapshot,
                range,
                null,
                PlaylistLampHistoricalSnapshotStatus.Latest,
                message);
        }
        return new PlaylistLampHistoricalScoreSnapshotResult(
            currentScoreSnapshot.WithScores(
                new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase),
                message,
                ScoreTableLoadStatus.Failed),
            range,
            query.SelectedLocalDate,
            PlaylistLampHistoricalSnapshotStatus.Unavailable,
            message);
    }

    private BMSTable FindTable(string playlistId)
    {
        if (!int.TryParse(playlistId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericId))
        {
            return null;
        }
        playlist.AcquireReaderLockBMSTables();
        try
        {
            return (playlist.BMSTables ?? [])
                .FirstOrDefault(table => table?.playlist_id == numericId);
        }
        finally
        {
            playlist.FreeReaderLockBMSTables();
        }
    }

}
