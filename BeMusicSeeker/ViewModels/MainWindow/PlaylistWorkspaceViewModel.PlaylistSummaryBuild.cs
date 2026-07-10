using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Livet;
using NLog;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    /// <summary>
    /// Builds raw summary rows and publishes the fresh filtered and sorted result owned by this workspace.
    /// </summary>
    /// <param name="library">The library that supplies the owned-chart digest snapshot.</param>
    /// <param name="playlists">The playlist collection whose current tables are summarized.</param>
    /// <param name="syncStatusSnapshot">The immutable-for-this-build runtime sync-status snapshot.</param>
    /// <param name="logger">The concrete diagnostics sink used by the summary pipeline.</param>
    /// <param name="runAsync">Whether to run the build on the task pool.</param>
    /// <returns>The accepted data generation, or zero when the workspace cannot start a build.</returns>
    internal long RebuildPlaylistSummaryView(
        BMSLibrary library,
        BMSPlaylist playlists,
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> syncStatusSnapshot,
        Logger logger,
        bool runAsync = true)
    {
        if (!TryBeginPlaylistSummaryDataBuild(out PlaylistSummaryDataBuildRequest buildRequest))
        {
            return 0L;
        }

        long dataRebuildGeneration = buildRequest.Generation;
        void Execute()
        {
            var stopwatch = Stopwatch.StartNew();
            PlaylistSummaryRowsBuildResult buildResult;
            try
            {
                buildResult = BuildPlaylistSummaryRows(
                    library,
                    playlists,
                    syncStatusSnapshot,
                    buildRequest.TableCountCacheGeneration,
                    buildRequest.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                LogBuild(logger, "playlist_summary_build_cancelled dataGeneration=" + dataRebuildGeneration
                    + " currentDataGeneration=" + CurrentPlaylistSummaryDataRebuildGeneration
                    + " buildMs=" + stopwatch.ElapsedMilliseconds);
                return;
            }

            long buildMs = stopwatch.ElapsedMilliseconds;
            if (!IsCurrentPlaylistSummaryDataRebuildGeneration(dataRebuildGeneration))
            {
                LogBuild(logger, "playlist_summary_build_stale rawCount=" + buildResult.Rows.Count
                    + " dataGeneration=" + dataRebuildGeneration
                    + " currentDataGeneration=" + CurrentPlaylistSummaryDataRebuildGeneration
                    + " buildMs=" + buildMs);
                return;
            }

            if (buildResult.UnloadedTableCount == 0)
            {
                TrySetPlaylistSummaryRowsCache(buildResult.Rows, dataRebuildGeneration);
            }

            string sortColumn = PlaylistSummarySortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name);
            string sortDirection = PlaylistSummarySortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString();
            BMSLibrary.PlaylistSummaryOwnedHashSnapshot ownedSnapshot = buildResult.OwnedHashSnapshot;
            LogBuild(logger, "playlist_summary_build tableCount=" + buildResult.TableCount
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
            LogBuild(logger, "playlist_summary_cache tableCount=" + buildResult.TableCount
                + " entryScanCount=" + buildResult.EntryScanCount
                + " cacheHit=" + buildResult.SummaryCacheHitCount
                + " cacheMiss=" + buildResult.SummaryCacheMissCount
                + " elapsedMs=" + buildMs);

            long presentationGeneration = BeginPlaylistSummaryPresentationGeneration();
            ApplyPlaylistSummaryPresentation(
                buildResult.Rows,
                stopwatch,
                buildMs,
                presentationGeneration,
                logger,
                dataRebuildGeneration: dataRebuildGeneration);
        }

        void Action()
        {
            try
            {
                Execute();
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
            buildTask.Logging("RebuildPlaylistSummaryView");
        }
        else
        {
            Action();
        }
        return dataRebuildGeneration;
    }

    /// <summary>
    /// Reapplies filtering and sorting to the current raw-row cache, rebuilding data when no fresh cache exists.
    /// </summary>
    /// <param name="library">The library used if a data rebuild becomes necessary.</param>
    /// <param name="playlists">The playlist collection used if a data rebuild becomes necessary.</param>
    /// <param name="syncStatusSnapshot">The sync-status snapshot used if a data rebuild becomes necessary.</param>
    /// <param name="logger">The concrete diagnostics sink used by the presentation pipeline.</param>
    internal void RefreshPlaylistSummaryPresentation(
        BMSLibrary library,
        BMSPlaylist playlists,
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> syncStatusSnapshot,
        Logger logger)
    {
        List<PlaylistSummaryRow> cachedRows = GetPlaylistSummaryRowsCacheSnapshot(
            out long cacheGeneration,
            out long dataRebuildGeneration);
        if (cachedRows == null)
        {
            RebuildPlaylistSummaryView(library, playlists, syncStatusSnapshot, logger);
            return;
        }

        long presentationGeneration = BeginPlaylistSummaryPresentationGeneration();
        ApplyPlaylistSummaryPresentation(
            cachedRows,
            Stopwatch.StartNew(),
            0L,
            presentationGeneration,
            logger,
            dataRebuildGeneration: dataRebuildGeneration,
            cacheGeneration: cacheGeneration);
    }

    private PlaylistSummaryRowsBuildResult BuildPlaylistSummaryRows(
        BMSLibrary library,
        BMSPlaylist playlists,
        IReadOnlyDictionary<string, PlaylistSyncRuntimeStatus> syncStatusSnapshot,
        long expectedTableCountCacheGeneration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BMSLibrary.PlaylistSummaryOwnedHashSnapshot ownedHashSnapshot = library?.GetPlaylistSummaryOwnedHashSnapshot(cancellationToken);
        int ownedSnapshotVersion = ownedHashSnapshot?.Version ?? 0;
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
        foreach (BMSTable table in tablesSnapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool entriesLoaded = table.ArePlaylistEntriesLoaded;
            PlaylistSummaryCountResult countResult = default;
            string countCacheKey = entriesLoaded ? GetPlaylistSummaryTableCountCacheKey(table, ownedSnapshotVersion) : null;
            if (entriesLoaded && TryGetPlaylistSummaryTableCount(countCacheKey, out countResult))
            {
                result.SummaryCacheHitCount++;
            }
            else if (entriesLoaded)
            {
                countResult = CalculatePlaylistSummaryCounts(table.GetEntriesExceptDummy(), ownedHashSnapshot, cancellationToken);
                TrySetPlaylistSummaryTableCount(countCacheKey, countResult, expectedTableCountCacheGeneration);
                result.SummaryCacheMissCount++;
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
                OutputBaseDisplayName = CustomFolderOutputBaseRegistry.GetDisplayName(table.custom_folder_output_base_name),
                Name = table.name ?? string.Empty,
                FolderName = table.Output_dir ?? string.Empty,
                FolderNameUndefined = IsPlaylistSummaryFolderNameUndefined(table),
                CompatPrefix = table.compat_prefix ?? string.Empty,
                Symbol = table.symbol ?? string.Empty,
                LastUpdate = table.last_update,
                TotalCharts = totalCharts,
                OwnedCharts = ownedCharts,
                MissingCharts = totalCharts - ownedCharts,
                OwnedRatio = totalCharts == 0 ? 0.0 : (double)ownedCharts * 100.0 / totalCharts,
                LinkUri = table.Page_url ?? table.GetAbsoluteHeaderUrl(),
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

    private void ApplyPlaylistSummaryPresentation(
        List<PlaylistSummaryRow> rawRows,
        Stopwatch stopwatch,
        long buildMs,
        long presentationGeneration,
        Logger logger,
        long? dataRebuildGeneration = null,
        long? cacheGeneration = null)
    {
        List<PlaylistSummaryRow> safeRawRows = rawRows ?? [];
        PlaylistSummaryPresentationResult presentationResult = BuildPlaylistSummaryPresentationRows(
            safeRawRows,
            PlaylistSummaryKeywordFilter,
            PlaylistSummaryOwnedFilter,
            PlaylistSummarySortParameters,
            useLegacySort: false);
        if (!CanApplyPlaylistSummaryPresentation(presentationGeneration, dataRebuildGeneration, cacheGeneration))
        {
            LogStalePresentation(logger, safeRawRows.Count, presentationGeneration, dataRebuildGeneration, cacheGeneration);
            return;
        }

        void Reflect()
        {
            if (!CanApplyPlaylistSummaryPresentation(presentationGeneration, dataRebuildGeneration, cacheGeneration))
            {
                return;
            }
            TryApplyPlaylistSummary(new PlaylistSummaryApplyRequest
            {
                Rows = new ObservableCollection<PlaylistSummaryRow>(presentationResult.Rows),
                SummaryText = string.Format(
                    BeMusicSeeker.Properties.Resources.Playlist_summary_format,
                    presentationResult.Rows.Sum(row => row.TotalCharts),
                    presentationResult.Rows.Count),
                PresentationGeneration = presentationGeneration,
                DataRebuildGeneration = dataRebuildGeneration,
                CacheGeneration = cacheGeneration
            });
        }

        Dispatcher dispatcher = DispatcherHelper.UIDispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            Reflect();
        }
        else
        {
            dispatcher.BeginInvoke((Action)Reflect);
        }

        Interlocked.Exchange(ref lastPlaylistSummaryBuildElapsedMs, stopwatch.ElapsedMilliseconds);
        LogBuild(logger, "playlist_summary_present inputCount=" + safeRawRows.Count
            + " filteredCount=" + presentationResult.FilteredCount
            + " viewCount=" + presentationResult.Rows.Count
            + " filterMs=" + presentationResult.FilterElapsedMs
            + " sortMs=" + presentationResult.SortElapsedMs
            + " totalMs=" + stopwatch.ElapsedMilliseconds
            + " sortColumn=" + presentationResult.SortColumn
            + " sortDirection=" + presentationResult.SortDirection
            + " sortProfile=" + presentationResult.SortProfile
            + " sortEngine=" + (presentationResult.UseLegacySort ? "legacy" : "fast")
            + (buildMs > 0 ? " buildMs=" + buildMs + " summaryCacheHit=false" : " summaryCacheHit=true"));
    }

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
            return IsCurrentPlaylistSummaryDataRebuildGeneration(dataRebuildGeneration.Value);
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
    /// <param name="useLegacySort">Whether the compatibility sorting engine is required.</param>
    /// <returns>The rows and presentation-stage metrics.</returns>
    internal static PlaylistSummaryPresentationResult BuildPlaylistSummaryPresentationRows(
        IEnumerable<PlaylistSummaryRow> rows,
        string keywordFilter,
        MainWindowViewModel.PlaylistSummaryOwnedFilterType ownedFilter,
        MainWindowViewModel.cSortParameters sortParameters,
        bool useLegacySort)
    {
        var stopwatch = Stopwatch.StartNew();
        List<PlaylistSummaryRow> filteredRows = [.. ApplyPlaylistSummaryFilters(rows, keywordFilter, ownedFilter)];
        long filterElapsedMs = stopwatch.ElapsedMilliseconds;
        List<PlaylistSummaryRow> sortedRows = PlaylistSummarySortEngine.Sort(filteredRows, sortParameters, useLegacySort, out string sortProfile);
        return new PlaylistSummaryPresentationResult
        {
            Rows = sortedRows,
            FilteredCount = filteredRows.Count,
            FilterElapsedMs = filterElapsedMs,
            SortElapsedMs = stopwatch.ElapsedMilliseconds - filterElapsedMs,
            SortProfile = sortProfile,
            SortColumn = sortParameters?.ColumnsName ?? nameof(PlaylistSummaryRow.Name),
            SortDirection = sortParameters?.Direction.ToString() ?? ListSortDirection.Ascending.ToString(),
            UseLegacySort = useLegacySort
        };
    }

    /// <summary>
    /// Counts active hashed entries against explicit owned-digest sets for deterministic tests and callers.
    /// </summary>
    /// <param name="entries">Playlist entries to scan.</param>
    /// <param name="ownedMd5Hashes">Owned MD5 digests.</param>
    /// <param name="ownedSha256Hashes">Owned SHA-256 digests.</param>
    /// <returns>Scanned, total, and owned entry counts.</returns>
    internal static PlaylistSummaryCountResult CalculatePlaylistSummaryCounts(
        IEnumerable<BMSTableEntry> entries,
        HashSet<string> ownedMd5Hashes,
        HashSet<string> ownedSha256Hashes)
    {
        HashSet<string> safeMd5Hashes = ownedMd5Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> safeSha256Hashes = ownedSha256Hashes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return CalculatePlaylistSummaryCounts(
            entries,
            md5 => !string.IsNullOrWhiteSpace(md5) && safeMd5Hashes.Contains(md5),
            sha256 => !string.IsNullOrWhiteSpace(sha256) && safeSha256Hashes.Contains(sha256));
    }

    /// <summary>
    /// Counts active hashed entries against a library-owned digest snapshot.
    /// </summary>
    /// <param name="entries">Playlist entries to scan.</param>
    /// <param name="ownedHashSnapshot">The owned digest snapshot.</param>
    /// <returns>Scanned, total, and owned entry counts.</returns>
    internal static PlaylistSummaryCountResult CalculatePlaylistSummaryCounts(
        IEnumerable<BMSTableEntry> entries,
        BMSLibrary.PlaylistSummaryOwnedHashSnapshot ownedHashSnapshot)
    {
        return CalculatePlaylistSummaryCounts(entries, ownedHashSnapshot, CancellationToken.None);
    }

    private static PlaylistSummaryCountResult CalculatePlaylistSummaryCounts(
        IEnumerable<BMSTableEntry> entries,
        BMSLibrary.PlaylistSummaryOwnedHashSnapshot ownedHashSnapshot,
        CancellationToken cancellationToken)
    {
        return CalculatePlaylistSummaryCounts(
            entries,
            ownedHashSnapshot == null ? null : new Func<string, bool>(ownedHashSnapshot.ContainsMd5),
            ownedHashSnapshot == null ? null : new Func<string, bool>(ownedHashSnapshot.ContainsSha256),
            cancellationToken);
    }

    private static PlaylistSummaryCountResult CalculatePlaylistSummaryCounts(
        IEnumerable<BMSTableEntry> entries,
        Func<string, bool> containsMd5,
        Func<string, bool> containsSha256,
        CancellationToken cancellationToken = default)
    {
        PlaylistSummaryCountResult result = default;
        containsMd5 ??= _ => false;
        containsSha256 ??= _ => false;
        foreach (BMSTableEntry entry in entries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ScannedEntries++;
            if (entry == null || entry.is_removed)
            {
                continue;
            }
            bool hasMd5 = !string.IsNullOrWhiteSpace(entry.md5);
            bool hasSha256 = !string.IsNullOrWhiteSpace(entry.sha256);
            if (!hasMd5 && !hasSha256)
            {
                continue;
            }
            result.TotalCharts++;
            if ((hasMd5 && containsMd5(entry.md5)) || (!hasMd5 && containsSha256(entry.sha256)))
            {
                result.OwnedCharts++;
            }
        }
        return result;
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
        MainWindowViewModel.PlaylistSummaryOwnedFilterType ownedFilter)
    {
        IEnumerable<PlaylistSummaryRow> source = rows ?? [];
        string text = (keywordFilter ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            GridKeywordSearchQuery query = GridKeywordSearchQuery.Parse(text);
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
        MainWindowViewModel.PlaylistSummaryOwnedFilterType ownedFilter)
    {
        if (row == null)
        {
            return false;
        }
        return ownedFilter switch
        {
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedComplete => row.TotalCharts > 0 && row.OwnedCharts == row.TotalCharts,
            MainWindowViewModel.PlaylistSummaryOwnedFilterType.OwnedIncomplete => row.TotalCharts == 0 || row.OwnedCharts < row.TotalCharts,
            _ => true
        };
    }

    private static bool IsPlaylistSummaryFolderNameUndefined(BMSTable table)
    {
        if (table == null)
        {
            return true;
        }
        string explicitOutputDirectoryName = BMSTable.NormalizeOutputDirectoryName(table.output_dir);
        string defaultOutputDirectoryName = BMSTable.CreateDefaultOutputDirectoryName(table.name);
        return string.IsNullOrWhiteSpace(explicitOutputDirectoryName)
            || string.Equals(explicitOutputDirectoryName, defaultOutputDirectoryName, StringComparison.Ordinal);
    }

    private static string GetPlaylistSummaryTableCountCacheKey(BMSTable table, int ownedSnapshotVersion)
    {
        if (table == null)
        {
            return null;
        }
        string tableKey = table.playlist_id.HasValue
            ? "id:" + table.playlist_id.Value.ToString(CultureInfo.InvariantCulture)
            : "name:" + (table.name ?? string.Empty) + "|symbol:" + (table.symbol ?? string.Empty);
        return tableKey
            + "|entryRevision:" + table.PlaylistEntriesRevision.ToString(CultureInfo.InvariantCulture)
            + "|owned:" + ownedSnapshotVersion.ToString(CultureInfo.InvariantCulture)
            + "|state:" + table.PlaylistEntriesLoadState;
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

    private static string GetPlaylistSyncStatusKey(BMSTable table)
    {
        if (table?.playlist_id != null)
        {
            return "id:" + table.playlist_id.Value;
        }
        Uri uri = table?.Page_url ?? table?.Header_url;
        if (uri?.IsAbsoluteUri == true)
        {
            return "uri:" + uri.AbsoluteUri;
        }
        return string.IsNullOrWhiteSpace(table?.name) ? null : "name:" + table.name;
    }

    private void LogStalePresentation(
        Logger logger,
        int inputCount,
        long presentationGeneration,
        long? dataRebuildGeneration,
        long? cacheGeneration)
    {
        LogBuild(logger, "playlist_summary_present_stale inputCount=" + inputCount
            + " generation=" + presentationGeneration
            + " currentGeneration=" + CurrentPlaylistSummaryPresentationGeneration
            + " dataGeneration=" + (dataRebuildGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " currentDataGeneration=" + CurrentPlaylistSummaryDataRebuildGeneration
            + " cacheGeneration=" + (cacheGeneration?.ToString(CultureInfo.InvariantCulture) ?? "-")
            + " currentCacheGeneration=" + CurrentPlaylistSummaryRowsCacheGeneration);
    }

    private static void LogBuild(Logger logger, string message)
    {
        logger?.Info(message);
    }
}

/// <summary>
/// Carries raw-row output and scan metrics between the build and presentation stages.
/// </summary>
internal sealed class PlaylistSummaryRowsBuildResult
{
    internal List<PlaylistSummaryRow> Rows { get; } = [];

    internal BMSLibrary.PlaylistSummaryOwnedHashSnapshot OwnedHashSnapshot { get; set; }

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

    internal bool UseLegacySort;
}
