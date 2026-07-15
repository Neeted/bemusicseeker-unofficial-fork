using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly PlaylistPropertySaveService propertySaveService;

    private PlaylistPropertyDialogViewModel activePropertyDialog;

    internal event EventHandler<PlaylistPropertyValidationErrorEventArgs> PlaylistPropertyValidationError;

    internal event EventHandler<PlaylistPropertyExternalSyncConfirmationRequestedEventArgs> PlaylistPropertyExternalSyncConfirmationRequested;

    internal event EventHandler PlaylistPropertyInvalidOutputDirectoryRequested;

    internal event EventHandler<PlaylistReferenceTableReplacedEventArgs> PlaylistPropertyReferenceTableReplaced;

    internal event EventHandler<PlaylistPropertyFolderSelectionRemappedEventArgs> PlaylistPropertyFolderSelectionRemapped;

    internal event EventHandler PlaylistPropertyReferenceSortInvalidationRequested;

    internal event EventHandler<PlaylistPropertyExternalSyncFailedEventArgs> PlaylistPropertyExternalSyncFailed;

    internal event EventHandler<PlaylistPropertyNotificationsFlushRequestedEventArgs> PlaylistPropertyNotificationsFlushRequested;

    public PlaylistPropertyDialogViewModel ActivePropertyDialog
    {
        get => activePropertyDialog;
        private set
        {
            if (!ReferenceEquals(activePropertyDialog, value))
            {
                activePropertyDialog = value;
                RaisePropertyChanged(nameof(ActivePropertyDialog));
            }
        }
    }

    private void ForwardPlaylistPropertyValidationError(
        object sender,
        PlaylistPropertyValidationErrorEventArgs request)
    {
        RaiseRequiredEvent(
            PlaylistPropertyValidationError,
            request,
            nameof(PlaylistPropertyValidationError));
    }

    private void ForwardPlaylistPropertyExternalSyncConfirmationRequested(
        object sender,
        PlaylistPropertyExternalSyncConfirmationRequestedEventArgs request)
    {
        RaiseRequiredEvent(
            PlaylistPropertyExternalSyncConfirmationRequested,
            request,
            nameof(PlaylistPropertyExternalSyncConfirmationRequested));
    }

    private void ForwardPlaylistPropertyInvalidOutputDirectoryRequested(object sender, EventArgs e)
    {
        RaiseRequiredEvent(
            PlaylistPropertyInvalidOutputDirectoryRequested,
            EventArgs.Empty,
            nameof(PlaylistPropertyInvalidOutputDirectoryRequested));
    }

    private void ForwardPlaylistPropertySyncStarted(object sender, EventArgs e)
    {
        BeginPlaylistSyncProgressOperation();
    }

    private void ForwardPlaylistSyncProgressChanged(
        object sender,
        PlaylistSyncProgressChangedEventArgs request)
    {
        ReportPlaylistSyncProgress(request?.Snapshot);
    }

    private void ForwardPlaylistPropertySyncFinished(object sender, EventArgs e)
    {
        EndPlaylistSyncProgressOperation();
    }

    private void ForwardPlaylistPropertyReferenceTableReplaced(
        object sender,
        PlaylistReferenceTableReplacedEventArgs request)
    {
        RaiseRequiredEvent(
            PlaylistPropertyReferenceTableReplaced,
            request,
            nameof(PlaylistPropertyReferenceTableReplaced));
    }

    private void ForwardPlaylistPropertyFolderSelectionRemapped(
        object sender,
        PlaylistPropertyFolderSelectionRemappedEventArgs request)
    {
        RaiseRequiredEvent(
            PlaylistPropertyFolderSelectionRemapped,
            request,
            nameof(PlaylistPropertyFolderSelectionRemapped));
    }

    private void ForwardPlaylistPropertyReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        RaiseRequiredEvent(
            PlaylistPropertyReferenceSortInvalidationRequested,
            EventArgs.Empty,
            nameof(PlaylistPropertyReferenceSortInvalidationRequested));
    }

    private void ForwardPlaylistSyncResultReported(
        object sender,
        PlaylistSyncResultReportedEventArgs request)
    {
        RecordPlaylistSyncResult(request?.Result);
        LogPlaylistSyncFailure(request?.Result);
    }

    private void ForwardPlaylistPropertyExternalSyncFailed(
        object sender,
        PlaylistPropertyExternalSyncFailedEventArgs request)
    {
        if (request != null)
        {
            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(request.Table, request.Uri, request.Exception));
            LogPlaylistPropertyExternalSyncFailure(request);
        }
        RaiseRequiredEvent(
            PlaylistPropertyExternalSyncFailed,
            request,
            nameof(PlaylistPropertyExternalSyncFailed));
    }

    private void LogPlaylistPropertyExternalSyncFailure(PlaylistPropertyExternalSyncFailedEventArgs request)
    {
        playlistSyncFailureLog(
            request.Exception,
            "playlist_property_resync_failed table="
            + (request.Table?.name ?? string.Empty)
            + " uri="
            + (request.Uri?.ToString() ?? string.Empty));
    }

    private void ForwardPlaylistSummaryDataRefreshRequested(
        object sender,
        PlaylistSummaryDataRefreshRequestedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        RequestPlaylistSummaryDataRefresh(
            request.Reason,
            request.InvalidateTableCountCache,
            request.RebuildAsync);
    }

    private void ForwardPlaylistEntriesChanged(
        object sender,
        PlaylistWorkspaceEntriesChangedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        bool detailContentChanged = MarkCurrentPlaylistDetailEntriesChanged(request.Table, "playlist_updated");
        try
        {
            RaiseRequiredEvent(
                EntriesChanged,
                new PlaylistWorkspaceEntriesChangedEventArgs(
                    request.Table,
                    detailContentChanged,
                    request.RefreshSummaryIfVisible),
                nameof(EntriesChanged));
        }
        finally
        {
            if (request.RefreshSummaryIfVisible)
            {
                RequestPlaylistSummaryDataRefresh(
                    "playlist_entries_updated",
                    invalidateTableCountCache: true);
            }
        }
    }

    private void ForwardPlaylistNotificationsFlushRequested(
        object sender,
        PlaylistPropertyNotificationsFlushRequestedEventArgs request)
    {
        RaiseRequiredEvent(
            PlaylistPropertyNotificationsFlushRequested,
            request,
            nameof(PlaylistPropertyNotificationsFlushRequested));
    }

    private void RaiseRequiredEvent(EventHandler handler, EventArgs args, string eventName)
    {
        (handler ?? throw new InvalidOperationException(eventName + " is not subscribed."))(this, args);
    }

    private void RaiseRequiredEvent<TEventArgs>(
        EventHandler<TEventArgs> handler,
        TEventArgs args,
        string eventName)
        where TEventArgs : EventArgs
    {
        (handler ?? throw new InvalidOperationException(eventName + " is not subscribed."))(this, args);
    }

    internal PlaylistPropertyDialogViewModel OpenPropertyDialog(BMSTable table, bool isNewTable = false)
    {
        PlaylistPropertySaveService service = propertySaveService
            ?? throw new InvalidOperationException("Playlist property editing is not available.");
        if (!service.ContainsActiveTable(table))
        {
            return null;
        }
        ActivePropertyDialog?.Dispose();
        ActivePropertyDialog = new PlaylistPropertyDialogViewModel(service, table, isNewTable);
        return ActivePropertyDialog;
    }

    internal void ClosePropertyDialog(PlaylistPropertyDialogViewModel dialog)
    {
        if (ReferenceEquals(ActivePropertyDialog, dialog))
        {
            ActivePropertyDialog = null;
        }
    }

    internal bool CanBeginSummaryPropertyEdit(PlaylistSummaryRow row, string propertyName)
    {
        return row?.TableRef != null
            && IsSummaryPropertyEditable(propertyName)
            && propertySaveService?.ContainsActiveTable(row.TableRef) == true;
    }

    internal async Task<bool> ApplySummaryPropertyEditAsync(
        PlaylistSummaryRow row,
        string propertyName,
        string text)
    {
        PlaylistPropertySaveService service = propertySaveService
            ?? throw new InvalidOperationException("Playlist property editing is not available.");
        if (row?.TableRef == null
            || !IsSummaryPropertyEditable(propertyName)
            || !service.ContainsActiveTable(row.TableRef))
        {
            return false;
        }

        PlaylistPropertySaveCommit commit;
        using (PlaylistPropertyEditSession session = service.BeginEdit(row.TableRef))
        {
            PlaylistPropertyValues values = session.Values;
            switch (propertyName)
            {
                case nameof(PlaylistSummaryRow.Name):
                    {
                        string name = (text ?? string.Empty).Trim();
                        if (string.Equals(values.Name ?? string.Empty, name, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.Name = name;
                        break;
                    }
                case nameof(PlaylistSummaryRow.FolderName):
                    {
                        string outputDirectory = PlaylistPropertySaveService.NormalizeSummaryOutputDirectory(
                            values.Name,
                            text);
                        if (string.Equals(
                            BMSTable.NormalizeOutputDirectoryName(values.OutputDirectory),
                            BMSTable.NormalizeOutputDirectoryName(outputDirectory),
                            StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.OutputDirectory = outputDirectory;
                        break;
                    }
                case nameof(PlaylistSummaryRow.CompatPrefix):
                    {
                        string compatPrefix = (text ?? string.Empty).TrimStart();
                        if (string.Equals(values.CompatPrefix ?? string.Empty, compatPrefix, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.CompatPrefix = compatPrefix;
                        break;
                    }
                case nameof(PlaylistSummaryRow.Symbol):
                    {
                        string symbol = (text ?? string.Empty).Trim();
                        if (string.Equals(values.Symbol ?? string.Empty, symbol, StringComparison.Ordinal))
                        {
                            return true;
                        }
                        values.Symbol = symbol;
                        break;
                    }
                default:
                    return false;
            }
            if (!service.TrySave(session, values, out commit))
            {
                return false;
            }
        }
        await service.ApplyPostSaveUpdatesAsync(commit);
        return true;
    }

    private static bool IsSummaryPropertyEditable(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistSummaryRow.Name), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.FolderName), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.CompatPrefix), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.Symbol), StringComparison.Ordinal);
    }
}
