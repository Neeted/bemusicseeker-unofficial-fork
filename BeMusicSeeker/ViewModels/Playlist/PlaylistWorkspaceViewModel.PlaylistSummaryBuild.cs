using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// P取得後に開始時と同じstoreの全正本参照を検証し、BMT選択の保存と必要出力を同じ権限で待ちます。
    /// </summary>
    /// <param name="rows">編集開始時に捕捉した対象行。</param>
    /// <param name="isBmtOutput">保存するBMT出力指定。</param>
    /// <param name="expectedStore">編集開始時に捕捉したstoreの同一参照。省略時は直接操作として現在storeの対象を検証します。</param>
    internal async Task ApplyPlaylistSummaryBmtOutput(IEnumerable<PlaylistSummaryRow> rows, bool isBmtOutput, BMSPlaylist expectedStore = null)
    {
        if (rows == null)
        {
            return;
        }

        BMSPlaylist playlists = GetPlaylistStore();
        using LibraryFileMutationLease accepted = playlists.AcquirePlaylistMutationLease("playlist_summary_bmt_output_changed");
        using LibraryFileMutationCapability authority = accepted.CreateMutationCapability();
        List<BMSTable> changedTables = [.. CaptureCurrentPlaylistSummaryTargets(playlists, rows, expectedStore)
            .Where(table => (table.is_bmt_output != false) != isBmtOutput)];
        if (changedTables.Count == 0)
        {
            return;
        }

        var previousValues = changedTables.ToDictionary(table => table, table => table.is_bmt_output);
        foreach (BMSTable table in changedTables)
        {
            table.is_bmt_output = isBmtOutput;
        }
        PersistPlaylistSummaryHeaderChanges(
            () => playlists.CommitBMSTableHeadersToDB(changedTables, capability: authority),
            () => { foreach (KeyValuePair<BMSTable, bool?> item in previousValues) { item.Key.is_bmt_output = item.Value; } });
        await playlists.BmtOutput.ExportTablesAsync(changedTables, "playlist_summary_bmt_output_changed", authority);
        RequestPlaylistSummaryDataRefresh(
            "playlist_summary_bmt_output_changed");
    }

    /// <summary>
    /// Drains one deferred summary refresh and executes its terminal data or presentation route.
    /// </summary>
    /// <param name="dataRefreshRequired">Whether the caller observed an external data refresh.</param>
    /// <param name="rebuildAsync">Whether a data rebuild should run on the task pool.</param>
    /// <returns>The accepted data generation, or zero when no data rebuild was started.</returns>
    internal long DrainDeferredPlaylistSummaryRefresh(
        bool dataRefreshRequired,
        bool rebuildAsync)
    {
        bool deferredRebuildAsync;
        PlaylistSummaryDeferredRefreshKind refresh = TakeDeferredPlaylistSummaryRefresh(
            dataRefreshRequired,
            out deferredRebuildAsync);
        if (!IsPlaylistSummaryModeRequested)
        {
            return 0L;
        }
        if (refresh == PlaylistSummaryDeferredRefreshKind.Data)
        {
            return RebuildPlaylistSummaryView(rebuildAsync && deferredRebuildAsync);
        }
        if (refresh == PlaylistSummaryDeferredRefreshKind.Presentation)
        {
            RefreshPlaylistSummaryPresentation();
        }
        return 0L;
    }

    /// <summary>
    /// workerでentryを集計し、既存UI適用Taskの終端まで同じbuild寿命でraw公開・絞込み・ソート・採用を回収します。
    /// ページのない内蔵表にはブラウザーで開くリンクを作りません。
    /// </summary>
    /// <param name="runAsync">スレッドプールで構築するか。</param>
    /// <returns>受理したデータ世代。開始できなければ 0。</returns>
    internal long RebuildPlaylistSummaryView(bool runAsync = true)
    {
        if (!TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest))
        {
            return 0L;
        }
        var performanceInteraction = PerformanceInteraction.Existing(
            "playlist_summary",
            buildRequest.Generation,
            buildRequest.Generation);
        if (Net10PerformanceLog.IsEnabled)
        {
            Net10PerformanceLog.Write(performanceInteraction, "input_accepted");
            Net10PerformanceLog.Write(performanceInteraction, "owner_queued");
        }

        BMSLibrary library;
        BMSPlaylist playlists;
        try
        {
            library = getPlaylistLibrary();
            playlists = getPlaylistStore();
        }
        catch
        {
            CompletePlaylistSummaryDataBuild(buildRequest);
            throw;
        }

        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> syncStatusSnapshot = CapturePlaylistSyncStatusSnapshot();

        long dataRebuildGeneration = buildRequest.Generation;
        async Task Execute()
        {
            var stopwatch = Stopwatch.StartNew();
            if (Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(performanceInteraction, "owner_started");
            }
            PlaylistSummaryRowsBuildResult buildResult;
            try
            {
                buildResult = BuildPlaylistSummaryRows(
                    library,
                    playlists,
                    syncStatusSnapshot,
                    buildRequest.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                LogBuild("playlist_summary_build_cancelled dataGeneration=" + dataRebuildGeneration
                    + " currentDataGeneration=" + CurrentPlaylistSummaryDataRebuildGeneration
                    + " buildMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }

            long buildMs = stopwatch.ElapsedMilliseconds;
            if (Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(
                    performanceInteraction,
                    "snapshot_query_projection",
                    "tables=" + buildResult.TableCount
                    + " rows=" + buildResult.Rows.Count
                    + " entryScans=" + buildResult.EntryScanCount
                    + " elapsedMs=" + buildMs);
            }
            if (!IsCurrentPlaylistSummaryDataRebuildGeneration(dataRebuildGeneration))
            {
                LogBuild("playlist_summary_build_stale rawCount=" + buildResult.Rows.Count
                    + " dataGeneration=" + dataRebuildGeneration
                    + " currentDataGeneration=" + CurrentPlaylistSummaryDataRebuildGeneration
                    + " buildMs=" + buildMs);
                return;
            }

            string sortColumn = PlaylistSummarySortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name);
            string sortDirection = PlaylistSummarySortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString();
            OwnedChartHashIndexVersionedSnapshot ownedSnapshot = buildResult.OwnedHashSnapshot;
            LogBuild("playlist_summary_build tableCount=" + buildResult.TableCount
                + " unloadedTableCount=" + buildResult.UnloadedTableCount
                + " entryScanCount=" + buildResult.EntryScanCount
                + " rawCount=" + buildResult.Rows.Count
                + " buildMs=" + buildMs
                + " ownedMd5Count=" + (ownedSnapshot?.Md5Count ?? 0)
                + " ownedSha256Count=" + (ownedSnapshot?.Sha256Count ?? 0)
                + " ownedSnapshotVersion=" + (ownedSnapshot?.Version ?? 0)
                + " ownedHashBuildMs=" + (ownedSnapshot?.BuildElapsedMs ?? 0L)
                + " summaryCacheHit=false tableCacheHit=" + buildResult.SummaryCacheHitCount
                + " tableCacheMiss=" + buildResult.SummaryCacheMissCount
                + " sortColumn=" + sortColumn
                + " sortDirection=" + sortDirection);
            LogBuild("playlist_summary_cache tableCount=" + buildResult.TableCount
                + " entryScanCount=" + buildResult.EntryScanCount
                + " cacheHit=" + buildResult.SummaryCacheHitCount
                + " cacheMiss=" + buildResult.SummaryCacheMissCount
                + " elapsedMs=" + buildMs);

            long presentationGeneration = BeginPlaylistSummaryPresentationGeneration();
            await ApplyPlaylistSummaryPresentationAsync(
                buildResult.Rows,
                stopwatch,
                buildMs,
                presentationGeneration,
                dataRebuildGeneration: dataRebuildGeneration,
                rawCacheReusable: buildResult.UnloadedTableCount == 0).ConfigureAwait(false);
        }

        async Task Action()
        {
            try
            {
                await Execute().ConfigureAwait(false);
            }
            finally
            {
                CompletePlaylistSummaryDataBuild(buildRequest);
            }
        }

        if (runAsync)
        {
            Task buildTask;
            try
            {
                buildTask = Task.Run(Action);
            }
            catch
            {
                CompletePlaylistSummaryDataBuild(buildRequest);
                throw;
            }
            buildTask.ObserveFault("RebuildPlaylistSummaryView");
        }
        else
        {
            Action().ObserveFault("RebuildPlaylistSummaryView");
        }
        return dataRebuildGeneration;
    }

    /// <summary>
    /// 現在rawから要求条件を再評価し、同じUI処理で採用します。再利用不可のrawならentry集計から再構築します。
    /// </summary>
    internal void RefreshPlaylistSummaryPresentation()
    {
        List<PlaylistSummaryRow> cachedRows = GetPlaylistSummaryRowsCacheSnapshot(
            out long cacheGeneration,
            out _);
        if (cachedRows == null)
        {
            RebuildPlaylistSummaryView();
            return;
        }

        long presentationGeneration = BeginPlaylistSummaryPresentationGeneration();
        ApplyPlaylistSummaryPresentationAsync(
            cachedRows,
            Stopwatch.StartNew(),
            0L,
            presentationGeneration,
            cacheGeneration: cacheGeneration,
            isCacheHit: true).ObserveFault("RefreshPlaylistSummaryPresentation");
    }

    private PlaylistSummaryRowsBuildResult BuildPlaylistSummaryRows(
        BMSLibrary library,
        BMSPlaylist playlists,
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> syncStatusSnapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OwnedChartHashIndexVersionedSnapshot ownedHashSnapshot = library?.GetOwnedChartHashIndexSnapshot(cancellationToken);
        summaryOwnedHashContentVersion = ownedHashSnapshot?.Version;
        List<BMSTable> tablesSnapshot = [];
        if (playlists != null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            playlists.AcquireReaderLockBMSTables();
            try
            {
                tablesSnapshot = [.. playlists.BMSTables.Where(table => table != null).OrderBy(table => table.name ?? string.Empty)];
            }
            finally
            {
                playlists.FreeReaderLockBMSTables();
            }
        }

        var result = new PlaylistSummaryRowsBuildResult
        {
            OwnedHashSnapshot = ownedHashSnapshot,
            TableCount = tablesSnapshot.Count
        };
        CustomFolderOutputSettingsSnapshot customFolderOutputSettings = customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
        foreach (BMSTable table in tablesSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool entriesLoaded = table.ArePlaylistEntriesLoaded;
            PlaylistSummaryCountResult countResult = default;
            if (entriesLoaded)
            {
                countResult = playlistCatalogSummaryOwner.GetOrBuildTableCount(
                    table,
                    ownedHashSnapshot,
                    cancellationToken,
                    out bool cacheHit);
                if (cacheHit)
                {
                    result.SummaryCacheHitCount++;
                }
                else
                {
                    result.SummaryCacheMissCount++;
                }
            }
            else
            {
                result.UnloadedTableCount++;
            }

            result.EntryScanCount += countResult.ScannedEntries;
            PlaylistSyncRuntimeStatus syncStatus = GetPlaylistSyncRuntimeStatus(table, syncStatusSnapshot);
            string statusDetail = syncStatus.Detail;
            if (!entriesLoaded)
            {
                statusDetail = string.IsNullOrWhiteSpace(statusDetail)
                    ? BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_loading
                    : string.Format(
                        CultureInfo.CurrentCulture,
                        BeMusicSeeker.Properties.Resources.Statusbar_progress_detail_separator_format,
                        statusDetail,
                        BeMusicSeeker.Properties.Resources.Statusbar_progress_phase_playlist_loading);
            }

            int totalCharts = countResult.TotalCharts;
            int ownedCharts = countResult.OwnedCharts;
            result.Rows.Add(new PlaylistSummaryRow
            {
                PlaylistId = table.playlist_id,
                OutputBaseName = table.custom_folder_output_base_name ?? string.Empty,
                OutputBaseDisplayName = CustomFolderOutputBaseRegistry.GetDisplayName(
                    table.custom_folder_output_base_name,
                    customFolderOutputSettings.LR2CustomFolderOutputBaseDir,
                    customFolderOutputSettings.LR2CustomFolderAdditionalOutputBaseDirs),
                TotalCharts = totalCharts,
                OwnedCharts = ownedCharts,
                MissingCharts = totalCharts - ownedCharts,
                OwnedRatio = totalCharts == 0 ? 0.0 : (double)ownedCharts * 100.0 / totalCharts,
                LinkUri = TryResolvePlaylistTablePageUri(table, out Uri pageUri) ? pageUri : null,
                HeaderUri = table.GetAbsoluteHeaderUrl(),
                DataUri = table.GetAbsoluteDataUrl(),
                IsExternalSync = table.is_external_sync,
                Status = syncStatus.StatusText,
                StatusDetail = statusDetail,
                StatusSortOrder = syncStatus.StatusSortOrder,
                HasFailureStatus = syncStatus.HasFailureStatus,
                IsRootFolder = table.is_root_folder,
                BmtSort = table.bmt_sort ?? int.MaxValue,
                IsBmtOutput = table.is_bmt_output != false,
                TableRef = table
            });
        }
        return result;
    }

    /// <summary>要求条件の捕捉後、rawの正本属性投影・cache公開・filter/sort・view採用を同一UI処理で完結し、その実Taskを返します。</summary>
    private Task ApplyPlaylistSummaryPresentationAsync(
        List<PlaylistSummaryRow> rawRows,
        Stopwatch stopwatch,
        long buildMs,
        long presentationGeneration,
        long? dataRebuildGeneration = null,
        long? cacheGeneration = null,
        bool isCacheHit = false,
        bool? rawCacheReusable = null)
    {
        List<PlaylistSummaryRow> safeRawRows = rawRows ?? [];
        string keywordFilter = PlaylistSummaryKeywordFilter;
        PlaylistOwnedFilter ownedFilter = PlaylistSummaryOwnedFilter;
        var sortParameters = new ChartListSortParameters
        {
            ColumnsName = PlaylistSummarySortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name),
            Direction = PlaylistSummarySortParameters?.Direction ?? ListSortDirection.Ascending
        };
        var interaction = PerformanceInteraction.Existing("playlist_summary", dataRebuildGeneration ?? presentationGeneration, presentationGeneration);
        if (Net10PerformanceLog.IsEnabled)
        {
            if (isCacheHit)
            {
                Net10PerformanceLog.Write(interaction, "input_accepted", "source=summary_cache");
                Net10PerformanceLog.Write(interaction, "owner_queued", "source=summary_cache");
                Net10PerformanceLog.Write(interaction, "owner_started", "source=summary_cache");
            }
            if (!playlistRestoreUiThreadCheck()) { Net10PerformanceLog.Write(interaction, "ui_queued", "rows=" + safeRawRows.Count); }
        }
        return playlistRestoreUiApplyScheduler(() =>
        {
            if (!CanApplyPlaylistSummaryPresentation(presentationGeneration, dataRebuildGeneration, cacheGeneration))
            {
                LogStalePresentation(safeRawRows.Count, presentationGeneration, dataRebuildGeneration, cacheGeneration);
                return;
            }
            if (Net10PerformanceLog.IsEnabled) { Net10PerformanceLog.Write(interaction, "ui_started"); }
            // この列挙で未表示のrawも正本属性へ揃えるため、filter済みの古い候補や候補別deltaは残しません。
            IEnumerable<PlaylistSummaryRow> projected = safeRawRows.Select(row =>
            {
                if (row.TableRef != null) { row.ApplySavedProperties(row.TableRef, notify: false); }
                return row;
            });
            PlaylistSummaryPresentationResult result = BuildPlaylistSummaryPresentationRows(projected, keywordFilter, ownedFilter, sortParameters);
            if (Net10PerformanceLog.IsEnabled)
            {
                Net10PerformanceLog.Write(interaction, "snapshot_query_projection", "phase=presentation inputRows=" + safeRawRows.Count
                    + " outputRows=" + result.Rows.Count + " filterMs=" + result.FilterElapsedMs + " sortMs=" + result.SortElapsedMs);
            }
            long? appliedCacheGeneration = cacheGeneration;
            if (rawCacheReusable.HasValue && dataRebuildGeneration.HasValue
                && TrySetPlaylistSummaryRowsCache(safeRawRows, dataRebuildGeneration.Value, rawCacheReusable.Value))
            {
                appliedCacheGeneration = CurrentPlaylistSummaryRowsCacheGeneration;
            }
            var identity = new PlaylistSummaryPresentationIdentity(
                appliedCacheGeneration ?? -(dataRebuildGeneration ?? presentationGeneration), keywordFilter, ownedFilter,
                sortParameters.ColumnsName, sortParameters.Direction);
            bool applied = TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
            {
                Rows = result.Rows,
                Identity = identity,
                PresentationGeneration = presentationGeneration,
                DataRebuildGeneration = dataRebuildGeneration,
                CacheGeneration = appliedCacheGeneration
            });
            if (applied && Net10PerformanceLog.IsEnabled)
            { Net10PerformanceLog.Write(interaction, "ui_applied", "rows=" + result.Rows.Count); }
            Interlocked.Exchange(ref lastPlaylistSummaryBuildElapsedMs, stopwatch.ElapsedMilliseconds);
            LogBuild("playlist_summary_present inputCount=" + safeRawRows.Count
                + " filteredCount=" + result.FilteredCount + " viewCount=" + result.Rows.Count
                + " filterMs=" + result.FilterElapsedMs + " sortMs=" + result.SortElapsedMs
                + " totalMs=" + stopwatch.ElapsedMilliseconds + " sortColumn=" + result.SortColumn
                + " sortDirection=" + result.SortDirection + " sortProfile=" + result.SortProfile
                + " buildMs=" + buildMs + " summaryCacheHit=" + isCacheHit);
        });
    }

    private static string FormatPlaylistSummaryText(int charts, int tables)
        => string.Format(BeMusicSeeker.Properties.Resources.Playlist_summary_format, charts, tables);

    private bool CanApplyPlaylistSummaryPresentation(
        long presentationGeneration,
        long? dataRebuildGeneration,
        long? cacheGeneration)
    {
        if (presentationGeneration != CurrentPlaylistSummaryPresentationGeneration)
        {
            return false;
        }
        if (dataRebuildGeneration.HasValue)
        {
            if (!IsCurrentPlaylistSummaryDataRebuildGeneration(dataRebuildGeneration.Value))
            {
                return false;
            }
        }
        return !cacheGeneration.HasValue || cacheGeneration.Value == CurrentPlaylistSummaryRowsCacheGeneration;
    }

    /// <summary>
    /// Creates the deterministic presentation projection without mutating workspace state.
    /// </summary>
    /// <param name="rows">Raw summary rows.</param>
    /// <param name="keywordFilter">The parsed-query source text.</param>
    /// <param name="ownedFilter">The ownership completeness filter.</param>
    /// <param name="sortParameters">The requested column and direction.</param>
    /// <returns>The rows and presentation-stage metrics.</returns>
    internal static PlaylistSummaryPresentationResult BuildPlaylistSummaryPresentationRows(
        IEnumerable<PlaylistSummaryRow> rows,
        string keywordFilter,
        PlaylistOwnedFilter ownedFilter,
        ChartListSortParameters sortParameters)
    {
        var stopwatch = Stopwatch.StartNew();
        List<PlaylistSummaryRow> filteredRows = [.. ApplyPlaylistSummaryFilters(rows, keywordFilter, ownedFilter)];
        long filterElapsedMs = stopwatch.ElapsedMilliseconds;
        List<PlaylistSummaryRow> sortedRows = PlaylistSummarySortEngine.Sort(filteredRows, sortParameters, out string sortProfile);
        return new PlaylistSummaryPresentationResult
        {
            Rows = sortedRows,
            FilteredCount = filteredRows.Count,
            FilterElapsedMs = filterElapsedMs,
            SortElapsedMs = stopwatch.ElapsedMilliseconds - filterElapsedMs,
            SortProfile = sortProfile,
            SortColumn = sortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name),
            SortDirection = sortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString()
        };
    }

    /// <summary>
    /// Applies keyword and ownership filters while preserving deferred enumeration.
    /// </summary>
    /// <param name="rows">Rows to filter.</param>
    /// <param name="keywordFilter">The keyword-query source text.</param>
    /// <param name="ownedFilter">The ownership completeness filter.</param>
    /// <returns>The filtered row sequence.</returns>
    internal static IEnumerable<PlaylistSummaryRow> ApplyPlaylistSummaryFilters(
        IEnumerable<PlaylistSummaryRow> rows,
        string keywordFilter,
        PlaylistOwnedFilter ownedFilter)
    {
        IEnumerable<PlaylistSummaryRow> source = rows ?? [];
        string text = (keywordFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            var query = GridKeywordSearchQuery.Parse(text);
            source = source.Where(row => query.MatchesPlaylistSummary(row));
        }
        return source.Where(row => IsPlaylistSummaryRowMatchedOwnedFilter(row, ownedFilter));
    }

    /// <summary>
    /// Evaluates the ownership completeness predicate shared by presentation filters and tests.
    /// </summary>
    /// <param name="row">The row to evaluate.</param>
    /// <param name="ownedFilter">The ownership filter.</param>
    /// <returns><see langword="true"/> when the row belongs in the requested ownership view.</returns>
    internal static bool IsPlaylistSummaryRowMatchedOwnedFilter(
        PlaylistSummaryRow row,
        PlaylistOwnedFilter ownedFilter)
    {
        if (row == null)
        {
            return false;
        }
        return ownedFilter switch
        {
            PlaylistOwnedFilter.OwnedComplete => row.TotalCharts > 0 && row.OwnedCharts == row.TotalCharts,
            PlaylistOwnedFilter.OwnedIncomplete => row.TotalCharts == 0 || row.OwnedCharts < row.TotalCharts,
            _ => true
        };
    }

    private static PlaylistSyncRuntimeStatus GetPlaylistSyncRuntimeStatus(
        BMSTable table,
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> snapshot)
    {
        string key = GetPlaylistSyncStatusKey(table);
        if (!string.IsNullOrWhiteSpace(key)
            && snapshot != null
            && snapshot.TryGetValue(key, out PlaylistSyncRuntimeStatus status)
            && status != null)
        {
            return status;
        }
        return PlaylistSyncStatusMapper.CreateNone();
    }

    private void LogStalePresentation(
        int inputCount,
        long presentationGeneration,
        long? dataRebuildGeneration,
        long? cacheGeneration)
    {
        LogBuild("playlist_summary_present_stale inputCount=" + inputCount
            + " generation=" + presentationGeneration
            + " currentGeneration=" + CurrentPlaylistSummaryPresentationGeneration
            + " dataGeneration=" + (dataRebuildGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " currentDataGeneration=" + CurrentPlaylistSummaryDataRebuildGeneration
            + " cacheGeneration=" + (cacheGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " currentCacheGeneration=" + CurrentPlaylistSummaryRowsCacheGeneration);
    }

    private void LogBuild(string message)
    {
        detailRetentionLog(message);
    }
}

/// <summary>
/// Carries raw-row output and scan metrics between the build and presentation stages.
/// </summary>
internal sealed class PlaylistSummaryRowsBuildResult
{
    internal List<PlaylistSummaryRow> Rows { get; } = [];

    internal OwnedChartHashIndexVersionedSnapshot OwnedHashSnapshot { get; set; }

    internal int TableCount { get; set; }

    internal int EntryScanCount { get; set; }

    internal int UnloadedTableCount { get; set; }

    internal int SummaryCacheHitCount { get; set; }

    internal int SummaryCacheMissCount { get; set; }
}

/// <summary>
/// Carries filtered and sorted rows together with presentation-stage diagnostics.
/// </summary>
internal struct PlaylistSummaryPresentationResult
{
    internal List<PlaylistSummaryRow> Rows;

    internal int FilteredCount;

    internal long FilterElapsedMs;

    internal long SortElapsedMs;

    internal string SortProfile;

    internal string SortColumn;

    internal string SortDirection;

}
