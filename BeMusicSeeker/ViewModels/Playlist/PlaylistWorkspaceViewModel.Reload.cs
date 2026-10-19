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
    private const string ManualPlaylistReloadProgressSource = "playlist_manual_reload";

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
        return GetPlaylistStore().ResolveActivePlaylistTableForMutation(table);
    }

    internal BMSTable ResolveActivePlaylistSummaryTable(PlaylistSummaryRow row)
    {
        return row?.TableRef == null
            ? null
            : ResolveActivePlaylistTable(row.TableRef);
    }

    /// <summary>同じ正本の参照または永続IDで表示対象を解決します。名前・URLから別対象を作りません。</summary>
    internal BMSTable ResolveActivePlaylistTable(BMSTable previousTable)
    {
        if (previousTable == null)
        {
            return null;
        }

        return GetPlaylistStore().ResolveActivePlaylistTableForMutation(previousTable);
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
        BMSPlaylist playlists = GetPlaylistStore();
        if (playlists.IsPlaylistUpdating
            || !TryEnterPlaylistMutationAdmission(playlists, out IDisposable admission))
        {
            RaiseMutationRejected(PlaylistWorkspaceMutationKind.Reload, isBusy: true, isStale: false);
            return;
        }
        using (admission)
        {
            List<BMSTable> resolvedRequestedTables = [.. requestedTables
                .Select(playlists.ResolveActivePlaylistTableForMutation)
                .Where(table => table != null)
                .Distinct()];
            if (resolvedRequestedTables.Count == 0)
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.Reload, isBusy: false, isStale: true);
                return;
            }
            List<BMSTable> activeTables = [.. resolvedRequestedTables
                .Where(playlists.ContainsBMSTable)
                .Where(table =>
                {
                    Uri uri = table.Page_url ?? table.Header_url;
                    return uri != null && uri.IsAbsoluteUri;
                })];
            if (activeTables.Count == 0)
            {
                RaiseMutationRejected(PlaylistWorkspaceMutationKind.Reload, isBusy: false, isStale: true);
                return;
            }
            bool isFullReload = activeTables.Count > 1;
            var stopwatch = Stopwatch.StartNew();
            PlaylistExternalSyncOwner.PlaylistReloadOperationResult results = null;
            using LibraryFileMutationCapability capability = playlists.CreatePlaylistMutationCapability(admission);
            using PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = playlists.OperationNotificationOwner.BeginSession();
            List<Exception> failures = [];
            try
            {
                BeginPlaylistSyncProgressOperation(ManualPlaylistReloadProgressSource);
                WritePlaylistReloadLog(
                    "playlist_reload_operation started operationKind="
                    + GetPlaylistReloadOperationKindText(isFullReload)
                    + " reason=manual_resync tableCount="
                    + activeTables.Count);
                results = await playlists.ExternalSyncOwner.ReloadPlaylistTargetsAsync(
                    activeTables,
                    syncResultCallback: null,
                    snapshot => ReportPlaylistSyncProgress(snapshot, ManualPlaylistReloadProgressSource,
                        BeMusicSeeker.Properties.Resources.Statusbar_progress_task_playlist_manual_reload),
                    "manual_resync",
                    requireCurrentTargetForApply: true,
                    publishReferenceReceipts: false, capability: capability);
                Exception reloadFailure = AdoptPlaylistReloadResults(results);
                if (reloadFailure != null) { failures.Add(reloadFailure); }
                RequestPlaylistSummaryDataRefresh(
                    "manual_playlist_resync");
                RequestPlaylistDetailReloadRefresh();
                if (reloadFailure == null)
                {
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
            }
            catch (Exception failure) { failures.Add(failure); }
            finally
            {
                CapturePlaylistWorkflowFailure(() => EndPlaylistSyncProgressOperation(ManualPlaylistReloadProgressSource), failures);
                CapturePlaylistWorkflowFailure(() => PublishPlaylistOperationNotificationReceipt(
                    notificationSession, "manual playlist resync notification"), failures);
            }
            ThrowPlaylistWorkflowFailures(failures);
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

    private Exception AdoptPlaylistReloadResults(PlaylistExternalSyncOwner.PlaylistReloadOperationResult result,
        bool applyReferences = true)
    {
        List<Exception> failures = [];
        if (result.Exception is Exception reloadFailure) { failures.Add(reloadFailure); }
        foreach (PlaylistExternalSyncOwner.PlaylistReloadTargetResult target in result)
        {
            if (!applyReferences || target.UpdateReceipt == null) { continue; }
            try { ApplyReferenceReplaceReceipt(target.UpdateReceipt); }
            catch (Exception failure) { failures.Add(failure); }
        }
        foreach (PlaylistExternalSyncOwner.PlaylistReloadTargetResult target in result)
        {
            PlaylistSyncAttemptResult sync = target.IsHandledExternalLoadFailure
                ? PlaylistSyncAttemptResult.CreateReportedExternalFailure(target.SourceTable, target.Uri, target.Exception)
                : target.Exception == null
                    ? PlaylistSyncAttemptResult.CreateSuccess(target.SourceTable, target.ResultTable, target.Uri, target.Updated)
                    : PlaylistSyncAttemptResult.CreateFailure(target.SourceTable, target.Uri, target.Exception);
            CapturePlaylistWorkflowFailure(() => RecordPlaylistSyncResult(sync), failures);
            // 取得失敗の詳細は取得境界で記録済み。STATUSには分類だけを渡します。
            if (!target.IsHandledExternalLoadFailure)
            {
                try { LogPlaylistSyncFailure(sync); }
                catch (Exception diagnosticFailure)
                {
                    try { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(diagnosticFailure, "playlist_manual_resync_diagnostic_failed"); }
                    catch { /* 診断の再通知で元の再取得結果を変えない。 */ }
                }
            }
        }
        return failures.Count switch { 0 => null, 1 => failures[0], _ => new AggregateException(failures).Flatten() };
    }

    private static void CapturePlaylistWorkflowFailure(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception failure) { failures.Add(failure); }
    }

    private static void ThrowPlaylistWorkflowFailures(IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 1) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures).Flatten(); }
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
        playlists ??= getPlaylistStore();
        if (receipt.NewTable != null && playlists?.ContainsBMSTable(receipt.NewTable) == true
            && (receipt.Updated || receipt.ReferenceEntriesChanged || !ReferenceEquals(receipt.OldTable, receipt.NewTable)))
        {
            ReplaceCurrentPlaylistDetailSelectionTable(receipt.OldTable, receipt.NewTable);
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
