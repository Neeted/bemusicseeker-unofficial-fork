using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly Action<Exception, string> playlistSyncFailureLog;

    internal event EventHandler<PlaylistSyncProgressChangedEventArgs> PlaylistSyncProgressChanged;

    internal event EventHandler<PlaylistReferenceTableReplacedEventArgs> PlaylistReferenceTableReplaced;

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
            BMSLibrary library = GetPlaylistLibrary();
            bool isFullReload = activeTables.Count > 1;
            var stopwatch = Stopwatch.StartNew();
            List<BMSPlaylist.PlaylistReloadTargetResult> results = null;
            try
            {
                BeginPlaylistSyncProgressOperation();
                WritePlaylistReloadLog(
                    "playlist_reload_operation started operationKind="
                    + GetPlaylistReloadOperationKindText(isFullReload)
                    + " reason=manual_resync tableCount="
                    + activeTables.Count);
                results = await playlists.ReloadPlaylistTargetsAsync(
                    activeTables,
                    [CreateReferenceReplaceUpdateCallback(playlists, library)],
                    result =>
                    {
                        RecordPlaylistSyncResult(result);
                        LogPlaylistSyncFailure(result);
                    },
                    ReportPlaylistSyncProgress,
                    "manual_resync",
                    requireCurrentTargetForApply: true);
                playlists.QueueBeatorajaBmtExportAll("manual_resync");
                PlaylistSummaryDataRefreshRequested?.Invoke(
                    this,
                    new PlaylistSummaryDataRefreshRequestedEventArgs(
                        "manual_playlist_resync",
                        invalidateTableCountCache: true));
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

    private Action<BMSPlaylist.PlaylistTableUpdateContext> CreateReferenceReplaceUpdateCallback(
        BMSPlaylist playlists,
        BMSLibrary library)
    {
        return updateContext =>
        {
            if (updateContext == null)
            {
                return;
            }
            bool objectReplaced = updateContext.OldTable != null
                && updateContext.NewTable != null
                && !ReferenceEquals(updateContext.OldTable, updateContext.NewTable);
            if (updateContext.NewTable == null
                || !playlists.ContainsBMSTable(updateContext.NewTable))
            {
                if (updateContext.OldTable != null)
                {
                    library.RemoveReferenceBMSTables(updateContext.OldTable);
                }
                return;
            }
            bool referenceIndexChanged = updateContext.Updated
                || updateContext.ReferenceEntriesChanged
                || objectReplaced;
            bool referenceIndexApplied = false;
            if (referenceIndexChanged)
            {
                library.ReplaceReferenceBMSTable(
                    updateContext.OldTable,
                    updateContext.NewTable,
                    updateContext.OldEntriesSnapshot,
                    updateContext.NewEntriesSnapshot);
                referenceIndexApplied = true;
            }
            if (!playlists.ContainsBMSTable(updateContext.NewTable))
            {
                if (referenceIndexApplied)
                {
                    library.RemoveReferenceBMSTables(updateContext.NewTable);
                }
                return;
            }
            if (objectReplaced || updateContext.Updated || updateContext.ReferenceEntriesChanged)
            {
                PlaylistReferenceTableReplaced?.Invoke(
                    this,
                    new PlaylistReferenceTableReplacedEventArgs(
                        updateContext.OldTable,
                        updateContext.NewTable,
                        referenceIndexChanged));
            }
        };
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
        BMSTable newTable,
        bool referenceIndexChanged)
    {
        OldTable = oldTable;
        NewTable = newTable;
        ReferenceIndexChanged = referenceIndexChanged;
    }

    internal BMSTable OldTable { get; }

    internal BMSTable NewTable { get; }

    internal bool ReferenceIndexChanged { get; }
}
