using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    internal PlaylistSourceBuildResult BuildDetailSourceRows(
        BMSTable table,
        PlaylistDetailSelectionScope selectionScope,
        string folderName,
        bool onlyNotOwned,
        PlaylistLibraryIndexSnapshot libraryIndexSnapshot,
        CancellationToken cancellationToken,
        ref string cancellationStage)
    {
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource)
            ?? throw new InvalidOperationException("Playlist detail data source is not attached.");
        var stopwatch = Stopwatch.StartNew();
        int scoreUpdateTargetCount = 0;
        long entryResolveMs = 0L;
        long scoreProbeMs = 0L;
        var scoreProbeMetrics = new PlaylistScoreProbeMetrics();
        if (table == null)
        {
            return new PlaylistSourceBuildResult([], scoreUpdateTargetCount, entryResolveMs, scoreProbeMs, 0L, scoreProbeMetrics);
        }

        dataSource.EnsureEntriesLoaded(table, "BuildDetailSourceRows");
        cancellationToken.ThrowIfCancellationRequested();
        PlaylistLibraryResolveIndexSnapshot libraryResolveIndex = libraryIndexSnapshot?.ResolveIndex
            ?? PlaylistLibraryResolveIndexSnapshot.Empty;
        BMSLibrary.ScoreSnapshot scoreSnapshot = dataSource.GetScoreSnapshot();
        IReadOnlyDictionary<string, BMSScore> scoresByHash = scoreSnapshot?.ActiveScoreSource == ActiveScoreSource.Lr2
            ? scoreSnapshot.ScoresByHash
            : new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, BMSScore> scoresBySha256 = scoreSnapshot?.ActiveScoreSource == ActiveScoreSource.Beatoraja
            ? scoreSnapshot.ScoresBySha256
            : new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);

        cancellationStage = "hash_index";
        cancellationToken.ThrowIfCancellationRequested();
        List<(BMSTableEntry entry, LibraryChartRef resolvedChart)> resolvedEntries = [];
        cancellationStage = "entry_resolve";
        bool includeAllNormalFolders = selectionScope == PlaylistDetailSelectionScope.OverallNormalFolders;
        foreach (BMSTableEntry entry in SnapshotPlaylistEntriesExceptDummy(table))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.is_removed
                || (folderName != null && entry.folder != folderName)
                || (includeAllNormalFolders
                    && string.Equals(entry.folder, "[NO SONG]", StringComparison.Ordinal)))
            {
                continue;
            }
            LibraryChartRef resolvedChart = libraryResolveIndex.ResolveChartForPlaylistEntry(entry);
            bool isOwned = !string.IsNullOrWhiteSpace(resolvedChart?.Path);
            if (onlyNotOwned && isOwned)
            {
                continue;
            }
            resolvedEntries.Add((entry, resolvedChart));
        }

        cancellationToken.ThrowIfCancellationRequested();
        List<(BMSTableEntry entry, LibraryChartRef resolvedChartRef, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo)> preparedEntries = new(resolvedEntries.Count);
        var resolvedChartSnapshotCache = new Dictionary<LibraryChartRef, ChartFile>();
        var chartInfoLookupStopwatch = Stopwatch.StartNew();
        int missingChartInfoResolveTargets = 0;
        int chartInfoResolvedCount = 0;
        foreach ((BMSTableEntry entry, LibraryChartRef resolvedChartRef) in resolvedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChartFile resolvedChart = ResolveChartSnapshot(resolvedChartRef, resolvedChartSnapshotCache);
            LR2SongDBExtended.chart_info entryChartInfo = null;
            if (resolvedChart == null)
            {
                missingChartInfoResolveTargets++;
                entryChartInfo = dataSource.ResolveChartInfo(entry.sha256, entry.md5);
                if (entryChartInfo != null)
                {
                    chartInfoResolvedCount++;
                }
            }
            preparedEntries.Add((entry, resolvedChartRef, resolvedChart, entryChartInfo));
        }
        chartInfoLookupStopwatch.Stop();
        detailViewLog("playlist_chart_info_index_resolve entries=" + resolvedEntries.Count
            + " targets=" + missingChartInfoResolveTargets
            + " found=" + chartInfoResolvedCount
            + " version=" + dataSource.ChartInfoIndexVersion
            + " elapsedMs=" + chartInfoLookupStopwatch.ElapsedMilliseconds);
        entryResolveMs = stopwatch.ElapsedMilliseconds;

        cancellationToken.ThrowIfCancellationRequested();
        cancellationStage = "score_probe";
        var scoreProbeStopwatch = Stopwatch.StartNew();
        List<(BMSTableEntry entry, LibraryChartRef resolvedChartRef, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo, BMSScore scoreSnapshot)> scoredEntries = new(preparedEntries.Count);
        foreach ((BMSTableEntry entry, LibraryChartRef resolvedChartRef, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo) in preparedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BMSScore scoreSnapshotForRow = PlaylistEntryScoreSnapshotResolver.Resolve(
                entry,
                resolvedChart,
                entryChartInfo,
                scoreSnapshot,
                scoresByHash,
                scoresBySha256);
            scoreUpdateTargetCount++;
            if (scoreSnapshotForRow != null)
            {
                scoreProbeMetrics.MatchedScoreCount++;
            }
            scoredEntries.Add((entry, resolvedChartRef, resolvedChart, entryChartInfo, scoreSnapshotForRow));
        }
        scoreProbeStopwatch.Stop();
        scoreProbeMetrics.TargetCount = scoreUpdateTargetCount;
        scoreProbeMetrics.TotalMs = scoreProbeStopwatch.ElapsedMilliseconds;
        scoreProbeMs = scoreProbeStopwatch.ElapsedMilliseconds;

        var playlistRows = new List<PlaylistDetailSourceRow>(scoredEntries.Count);
        cancellationStage = "source_row_materialize";
        foreach ((BMSTableEntry entry, LibraryChartRef resolvedChartRef, ChartFile resolvedChart, LR2SongDBExtended.chart_info entryChartInfo, BMSScore scoreSnapshotForRow) in scoredEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            playlistRows.Add(dataSource.CreateSourceRow(
                entry,
                resolvedChart,
                scoreSnapshotForRow,
                entryChartInfo,
                resolvedChartRef));
        }
        long sourceMaterializeMs = stopwatch.ElapsedMilliseconds - entryResolveMs - scoreProbeMs;
        return new PlaylistSourceBuildResult(
            playlistRows,
            scoreUpdateTargetCount,
            entryResolveMs,
            scoreProbeMs,
            sourceMaterializeMs,
            scoreProbeMetrics);
    }

    internal bool TryPatchDetailSourceChartInfo(
        PlaylistBuildRequest request,
        CancellationToken cancellationToken,
        out int sourceCount,
        out int dependencyCount,
        out int patchedCount,
        out long elapsedMs)
    {
        IPlaylistDetailDataSource dataSource = Volatile.Read(ref detailDataSource)
            ?? throw new InvalidOperationException("Playlist detail data source is not attached.");
        sourceCount = 0;
        dependencyCount = 0;
        patchedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        List<PlaylistDetailSourceRow> sourceRows;
        lock (DetailViewState.SyncRoot)
        {
            sourceRows = DetailViewState.Source.Rows;
        }
        if (sourceRows == null)
        {
            elapsedMs = stopwatch.ElapsedMilliseconds;
            return false;
        }

        sourceCount = sourceRows.Count;
        BMSLibrary.ScoreSnapshot scoreSnapshot = dataSource.GetScoreSnapshot();
        IReadOnlyDictionary<string, BMSScore> scoresByHash = scoreSnapshot?.ActiveScoreSource == ActiveScoreSource.Lr2
            ? scoreSnapshot.ScoresByHash
            : new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, BMSScore> scoresBySha256 = scoreSnapshot?.ActiveScoreSource == ActiveScoreSource.Beatoraja
            ? scoreSnapshot.ScoresBySha256
            : new Dictionary<string, BMSScore>(StringComparer.OrdinalIgnoreCase);
        var resolvedChartInfos = new LR2SongDBExtended.chart_info[sourceRows.Count];
        var resolvedScores = new BMSScore[sourceRows.Count];
        var chartInfoPatchCandidates = new bool[sourceRows.Count];
        for (int index = 0; index < sourceRows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlaylistDetailSourceRow row = sourceRows[index];
            if (row == null || !row.HasEntryChartInfoDependency)
            {
                continue;
            }
            dependencyCount++;
            LR2SongDBExtended.chart_info resolved = dataSource.ResolveChartInfo(row.sha256, row.hash);
            if (AreSameChartInfoIdentity(row.EntryChartInfo, resolved))
            {
                continue;
            }
            resolvedChartInfos[index] = resolved;
            resolvedScores[index] = PlaylistEntryScoreSnapshotResolver.Resolve(
                row.Entry,
                null,
                resolved,
                scoreSnapshot,
                scoresByHash,
                scoresBySha256);
            chartInfoPatchCandidates[index] = true;
        }

        lock (DetailBuildState.SyncRoot)
        {
            if (request.RequestVersion != DetailBuildState.RequestVersion)
            {
                elapsedMs = stopwatch.ElapsedMilliseconds;
                return false;
            }
            lock (DetailViewState.SyncRoot)
            {
                if (!ReferenceEquals(DetailViewState.Source.Rows, sourceRows))
                {
                    elapsedMs = stopwatch.ElapsedMilliseconds;
                    return false;
                }
                var patchedRows = new List<PlaylistDetailSourceRow>(sourceRows);
                for (int index = 0; index < sourceRows.Count; index++)
                {
                    if (!chartInfoPatchCandidates[index])
                    {
                        continue;
                    }
                    PlaylistDetailSourceRow currentRow = sourceRows[index];
                    LR2SongDBExtended.chart_info resolved = resolvedChartInfos[index];
                    if (currentRow == null
                        || !currentRow.HasEntryChartInfoDependency
                        || AreSameChartInfoIdentity(currentRow.EntryChartInfo, resolved))
                    {
                        continue;
                    }
                    patchedRows[index] = currentRow.WithEntryChartInfoAndScore(resolved, resolvedScores[index]);
                    patchedCount++;
                }
                DetailViewState.Source.PreviousRowsWeakReference = new WeakReference<List<PlaylistDetailSourceRow>>(sourceRows);
                DetailViewState.Source.PreviousGenerationId = DetailViewState.Source.GenerationId;
                DetailViewState.Source.Rows = patchedRows;
                DetailViewState.Source.LastBuiltChartInfoIndexVersion = request.Identity.ChartInfoIndexVersion;
                DetailViewState.Source.CurrentIdentity = request.Identity.SourceIdentity;
                DetailViewState.Source.GenerationId++;
            }
        }
        stopwatch.Stop();
        elapsedMs = stopwatch.ElapsedMilliseconds;
        return true;
    }

    private static ChartFile ResolveChartSnapshot(
        LibraryChartRef chartRef,
        IDictionary<LibraryChartRef, ChartFile> cache)
    {
        if (chartRef == null)
        {
            return null;
        }
        if (cache != null && cache.TryGetValue(chartRef, out ChartFile cachedChart))
        {
            return cachedChart;
        }
        ChartFile chart = chartRef.ToChartFileIdentity();
        if (cache != null)
        {
            cache[chartRef] = chart;
        }
        return chart;
    }

    private static bool AreSameChartInfoIdentity(
        LR2SongDBExtended.chart_info existing,
        LR2SongDBExtended.chart_info incoming)
    {
        if (ReferenceEquals(existing, incoming))
        {
            return true;
        }
        if (existing == null || incoming == null)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(existing.sha256) || string.IsNullOrWhiteSpace(incoming.sha256))
        {
            return false;
        }
        if (existing.parser_version <= 0 || incoming.parser_version <= 0)
        {
            return false;
        }
        if (existing.updated_at == default || incoming.updated_at == default)
        {
            return false;
        }
        return string.Equals(existing.sha256, incoming.sha256, StringComparison.OrdinalIgnoreCase)
            && existing.parser_version == incoming.parser_version
            && existing.updated_at == incoming.updated_at;
    }
}
