using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Action<Exception, string> playlistSyncFailureLog;

    internal event EventHandler<PlaylistSyncProgressChangedEventArgs> PlaylistSyncProgressChanged;

    internal event EventHandler PlaylistDetailReloadRefreshRequested;

    internal bool ShouldRefreshPlaylistDetailAfterReload(MainViewUpdateMode currentTreeMode)
    {
        return ChartListRefreshCoordinator.IsPlaylistTreeActive(
            MainViewUpdateMode.TreeViewFilterNotChanged,
            currentTreeMode);
    }

    internal void RequestPlaylistDetailReloadRefresh()
    {
        PlaylistDetailReloadRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    internal bool ContainsActivePlaylistTable(BMSTable table)
    {
        return table != null && GetPlaylistStore().ContainsBMSTable(table);
    }

    internal bool ContainsActivePlaylistSummaryRows(IEnumerable<PlaylistSummaryRow> rows)
    {
        return rows != null
            && rows.All(row => row?.TableRef != null && ContainsActivePlaylistTable(row.TableRef));
    }

    internal Task ResyncPlaylistsAsync(IEnumerable<PlaylistSummaryRow> rows)
    {
        if (rows == null)
        {
            return Task.CompletedTask;
        }
        List<BMSTable> tables = [.. rows
            .Where(row => row?.TableRef != null)
            .Select(row => row.TableRef)
            .Distinct()];
        return ResyncPlaylistsAsync(tables);
    }

    internal async Task<BMSTable> ResyncPlaylistTableAsync(BMSTable table)
    {
        if (table == null)
        {
            return null;
        }

        await ResyncPlaylistsAsync([table]);
        return ResolveActivePlaylistTable(table, table.name);
    }

    internal BMSTable ResolveActivePlaylistSummaryTable(PlaylistSummaryRow row)
    {
        return row?.TableRef == null
            ? null
            : ResolveActivePlaylistTable(row.TableRef, row.Name);
    }

    internal BMSTable ResolveActivePlaylistTable(BMSTable previousTable, string fallbackName = null)
    {
        if (previousTable == null)
        {
            return null;
        }

        IEnumerable<BMSTable> activeTables = GetPlaylistStore().BMSTables?.Cast<BMSTable>()
            ?? Enumerable.Empty<BMSTable>();
        if (previousTable.playlist_id.HasValue)
        {
            BMSTable byId = activeTables.FirstOrDefault(candidate =>
                candidate?.playlist_id == previousTable.playlist_id);
            return byId;
        }

        string pageUrl = previousTable.Page_url?.AbsoluteUri ?? string.Empty;
        string headerUrl = previousTable.GetAbsoluteHeaderUrl()?.AbsoluteUri ?? string.Empty;
        if (!string.IsNullOrEmpty(pageUrl) || !string.IsNullOrEmpty(headerUrl))
        {
            BMSTable byUrl = activeTables.FirstOrDefault(candidate =>
                candidate != null
                && string.Equals(
                    candidate.Page_url?.AbsoluteUri ?? string.Empty,
                    pageUrl,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    candidate.GetAbsoluteHeaderUrl()?.AbsoluteUri ?? string.Empty,
                    headerUrl,
                    StringComparison.OrdinalIgnoreCase));
            if (byUrl != null)
            {
                return byUrl;
            }
        }

        string name = string.IsNullOrWhiteSpace(fallbackName)
            ? previousTable.name
            : fallbackName;
        return activeTables.FirstOrDefault(candidate =>
            candidate != null
            && string.Equals(candidate.name, name, StringComparison.Ordinal));
    }

    internal async Task ResyncPlaylistsAsync(IEnumerable<BMSTable> tablesToResync)
    {
        if (tablesToResync == null)
        {
            return;
        }
        List<BMSTable> requestedTables = [.. tablesToResync.Where(table => table != null).Distinct()];
        if (requestedTables.Count == 0)
        {
            return;
        }
        await manualReloadSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            BMSPlaylist playlists = GetPlaylistStore();
            List<BMSTable> activeTables = [.. requestedTables
                .Where(playlists.ContainsBMSTable)
                .Where(table =>
                {
                    Uri uri = table.Page_url ?? table.Header_url;
                    return uri != null && uri.IsAbsoluteUri;
                })];
            if (activeTables.Count == 0)
            {
                return;
            }
            bool isFullReload = activeTables.Count > 1;
            var stopwatch = Stopwatch.StartNew();
            List<PlaylistExternalSyncOwner.PlaylistReloadTargetResult> results = null;
            using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlists.OperationNotificationOwner.BeginSession();
            try
            {
                BeginPlaylistSyncProgressOperation();
                WritePlaylistReloadLog(
                    "playlist_reload_operation started operationKind="
                    + GetPlaylistReloadOperationKindText(isFullReload)
                    + " reason=manual_resync tableCount="
                    + activeTables.Count);
                results = await playlists.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    activeTables,
                    result =>
                    {
                        RecordPlaylistSyncResult(result);
                        LogPlaylistSyncFailure(result);
                    },
                    ReportPlaylistSyncProgress,
                    "manual_resync",
                    requireCurrentTargetForApply: true,
                    publishReferenceReceipts: true);
                playlists.BmtOutput.QueueBeatorajaBmtExportAll("manual_resync");
                RequestPlaylistSummaryDataRefresh(
                    "manual_playlist_resync");
                RequestPlaylistDetailReloadRefresh();
                bool cleanupQueued = QueuePlaylistReloadCleanup(isFullReload, activeTables.Count);
                WritePlaylistReloadLog(
                    "playlist_reload_operation completed operationKind="
                    + GetPlaylistReloadOperationKindText(isFullReload)
                    + " reason=manual_resync tableCount="
                    + activeTables.Count
                    + " processedCount="
                    + (results?.Count ?? 0)
                    + " summaryRebuildMs="
                    + LastPlaylistSummaryBuildElapsedMs
                    + " detailRefreshMs="
                    + LastDetailBuildElapsedMs
                    + " cleanupQueued="
                    + cleanupQueued.ToString().ToLowerInvariant()
                    + " elapsedMs="
                    + stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                EndPlaylistSyncProgressOperation();
                PublishPlaylistOperationNotificationReceipt(
                    notificationSession,
                    "manual playlist resync notification");
            }
        }
        finally
        {
            manualReloadSemaphore.Release();
        }
    }

    private void LogPlaylistSyncFailure(PlaylistSyncAttemptResult result)
    {
        if (result == null || result.Succeeded)
        {
            return;
        }
        playlistSyncFailureLog(
            result.Exception,
            "playlist_manual_resync_failed table="
            + (result.SourceTable?.name ?? string.Empty)
            + " uri="
            + (result.PageUri?.ToString() ?? string.Empty));
    }

    private void ApplyReferenceReplaceReceipt(
        PlaylistExternalSyncOwner.PlaylistTableUpdateReceipt receipt,
        BMSPlaylist playlists = null,
        BMSLibrary library = null)
    {
        if (receipt == null)
        {
            return;
        }
        library ??= getPlaylistLibrary();
        if (library == null)
        {
            return;
        }
        playlists ??= getPlaylistStore();
        bool objectReplaced = receipt.OldTable != null
            && receipt.NewTable != null
            && !ReferenceEquals(receipt.OldTable, receipt.NewTable);
        if (receipt.NewTable == null
            || playlists?.ContainsBMSTable(receipt.NewTable) != true)
        {
            if (receipt.OldTable != null)
            {
                library.RemoveReferenceBMSTables(receipt.OldTable);
            }
            return;
        }
        bool referenceIndexChanged = receipt.Updated
            || receipt.ReferenceEntriesChanged
            || objectReplaced;
        bool referenceIndexApplied = false;
        if (referenceIndexChanged)
        {
            library.ReplaceReferenceBMSTable(
                receipt.OldTable,
                receipt.NewTable,
                receipt.OldEntriesSnapshot,
                receipt.NewEntriesSnapshot);
            referenceIndexApplied = true;
        }
        if (playlists?.ContainsBMSTable(receipt.NewTable) != true)
        {
            if (referenceIndexApplied)
            {
                library.RemoveReferenceBMSTables(receipt.NewTable);
            }
            return;
        }
        if (objectReplaced || receipt.Updated || receipt.ReferenceEntriesChanged)
        {
            ReplaceCurrentPlaylistDetailSelectionTable(
                receipt.OldTable,
                receipt.NewTable);
            if (referenceIndexChanged)
            {
                RequestPlaylistReferenceSortInvalidation();
            }
        }
    }
}

internal sealed class PlaylistSyncProgressChangedEventArgs : EventArgs
{
    internal PlaylistSyncProgressChangedEventArgs(PlaylistSyncProgressSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal PlaylistSyncProgressSnapshot Snapshot { get; }
}

internal sealed class PlaylistSyncResultReportedEventArgs : EventArgs
{
    internal PlaylistSyncResultReportedEventArgs(PlaylistSyncAttemptResult result)
    {
        Result = result;
    }

    internal PlaylistSyncAttemptResult Result { get; }
}

internal sealed class PlaylistReferenceTableReplacedEventArgs : EventArgs
{
    internal PlaylistReferenceTableReplacedEventArgs(
        BMSTable oldTable,
        BMSTable newTable)
    {
        OldTable = oldTable;
        NewTable = newTable;
    }

    internal BMSTable OldTable { get; }

    internal BMSTable NewTable { get; }

}
