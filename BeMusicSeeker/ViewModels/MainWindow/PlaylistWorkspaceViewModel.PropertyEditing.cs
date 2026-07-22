using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly PlaylistPropertySaveService propertySaveService;

    private PlaylistPropertyDialogViewModel activePropertyDialog;

    private int propertyDialogOpenInProgress;

    private readonly SemaphoreSlim summaryPropertyEditGate = new(1, 1);

    private PlaylistSummaryPendingPropertySave pendingSummaryPropertySave;

    internal event EventHandler<PlaylistPropertyValidationErrorEventArgs> PlaylistPropertyValidationError;

    internal event EventHandler<PlaylistPropertyExternalSyncConfirmationRequestedEventArgs> PlaylistPropertyExternalSyncConfirmationRequested;

    internal event EventHandler PlaylistPropertyInvalidOutputDirectoryRequested;

    internal event EventHandler<PlaylistPropertyExternalSyncFailedEventArgs> PlaylistPropertyExternalSyncFailed;

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

    internal bool CanOpenPlaylistEditDialog =>
        !IsWriteLockHeldBMSTablesInitializeMin
        && !IsWriteLockHeldBMSTables
        && !IsWriteLockHeldAnyBMSTable;

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
        ApplyPlaylistPropertyPresentation(BeginPlaylistSyncProgressOperation);
    }

    private void ForwardPlaylistSyncProgressChanged(
        object sender,
        PlaylistSyncProgressChangedEventArgs request)
    {
        ApplyPlaylistPropertyPresentation(() => ReportPlaylistSyncProgress(request?.Snapshot));
    }

    private void ForwardPlaylistPropertySyncFinished(object sender, EventArgs e)
    {
        ApplyPlaylistPropertyPresentation(EndPlaylistSyncProgressOperation);
    }

    private void ForwardPlaylistPropertyReferenceTableReplaced(
        object sender,
        PlaylistReferenceTableReplacedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ApplyPlaylistPropertyPresentation(
            () => ReplaceCurrentPlaylistDetailSelectionTable(request.OldTable, request.NewTable));
    }

    private void ForwardPlaylistPropertyFolderSelectionRemapped(
        object sender,
        PlaylistPropertyFolderSelectionRemappedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ApplyPlaylistPropertyPresentation(
            () => RemapCurrentPlaylistDetailFolderSelection(request.Table, request.RewrittenFolders));
    }

    private void ForwardPlaylistPropertyReferenceSortInvalidationRequested(object sender, EventArgs e)
    {
        ApplyPlaylistPropertyPresentation(() => RaiseRequiredEvent(
            PlaylistReferenceSortInvalidationRequested,
            EventArgs.Empty,
            nameof(PlaylistReferenceSortInvalidationRequested)));
    }

    private void ForwardPlaylistSyncResultReported(
        object sender,
        PlaylistSyncResultReportedEventArgs request)
    {
        ApplyPlaylistPropertyPresentation(() =>
        {
            RecordPlaylistSyncResult(request?.Result);
            LogPlaylistSyncFailure(request?.Result);
        });
    }

    private void ForwardPlaylistPropertyExternalSyncFailed(
        object sender,
        PlaylistPropertyExternalSyncFailedEventArgs request)
    {
        ApplyPlaylistPropertyPresentation(() =>
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
        });
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
        ApplyPlaylistPropertyPresentation(() =>
        {
            if (request.Reason.StartsWith("playlist_property_", StringComparison.Ordinal))
            {
                PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty);
            }
            RequestPlaylistSummaryDataRefresh(
                request.Reason,
                request.RebuildAsync);
        });
    }

    private void ForwardPlaylistEntriesChanged(
        object sender,
        PlaylistPropertyEntriesChangedEventArgs request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ApplyPlaylistPropertyPresentation(() =>
        {
            PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty);
            PublishEntriesChanged(request.Table, request.RefreshSummaryIfVisible);
        });
    }

    private void ForwardPlaylistOperationNotificationPresentationRequested(
        object sender,
        PlaylistOperationNotificationPresentationRequestedEventArgs request)
    {
        ApplyPlaylistPropertyPresentation(() => RaiseRequiredEvent(
            PlaylistOperationNotificationPresentationRequested,
            request,
            nameof(PlaylistOperationNotificationPresentationRequested)));
    }

    private void ApplyPlaylistPropertyPresentation(Action action)
    {
        playlistRestoreUiApplyScheduler(action).GetAwaiter().GetResult();
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

    internal async Task<PlaylistPropertyDialogViewModel> CreatePlaylistPropertyDialogAsync()
    {
        if (Interlocked.CompareExchange(ref propertyDialogOpenInProgress, 1, 0) != 0)
        {
            return null;
        }
        try
        {
            if (!CanOpenPlaylistEditDialog)
            {
                return null;
            }

            BMSTable table = await CreatePlaylistAsync();
            PlaylistPropertyEditSession editSession = null;
            try
            {
                editSession = await propertySaveService.CreateEditSessionAsync(
                    table,
                    isNewTable: true);
                if (editSession == null)
                {
                    await Task.Run(() => GetPlaylistStore().RemoveBMSTable(table));
                    return null;
                }
                return OpenPropertyDialogCore(editSession);
            }
            catch
            {
                editSession?.Dispose();
                await Task.Run(() => GetPlaylistStore().RemoveBMSTable(table));
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref propertyDialogOpenInProgress, 0);
        }
    }

    internal async Task<PlaylistPropertyDialogViewModel> OpenPropertyDialogAsync(BMSTable table)
    {
        if (Interlocked.CompareExchange(ref propertyDialogOpenInProgress, 1, 0) != 0)
        {
            return null;
        }
        try
        {
            ActivePropertyDialog?.Dispose();
            ActivePropertyDialog = null;
            if (!CanOpenPlaylistEditDialog)
            {
                return null;
            }
            PlaylistPropertyEditSession editSession =
                await propertySaveService.CreateEditSessionAsync(table);
            return editSession == null
                ? null
                : OpenPropertyDialogCore(editSession);
        }
        finally
        {
            Volatile.Write(ref propertyDialogOpenInProgress, 0);
        }
    }

    private PlaylistPropertyDialogViewModel OpenPropertyDialogCore(
        PlaylistPropertyEditSession editSession)
    {
        PlaylistPropertySaveService service = propertySaveService
            ?? throw new InvalidOperationException("Playlist property editing is not available.");
        try
        {
            ActivePropertyDialog?.Dispose();
            ActivePropertyDialog = new PlaylistPropertyDialogViewModel(service, editSession);
            return ActivePropertyDialog;
        }
        catch
        {
            editSession?.Dispose();
            throw;
        }
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
            && CanOpenPlaylistEditDialog
            && IsSummaryPropertyEditable(propertyName)
            && propertySaveService?.ContainsActiveTable(row.TableRef) == true;
    }

    internal async Task<bool> ApplySummaryPropertyEditAsync(
        PlaylistSummaryRow row,
        string propertyName,
        string text)
    {
        await summaryPropertyEditGate.WaitAsync();
        try
        {
            PlaylistPropertySaveService service = propertySaveService
                ?? throw new InvalidOperationException("Playlist property editing is not available.");
            BMSTable table = ResolveActivePlaylistSummaryTable(row);
            if (table == null
                || !CanOpenPlaylistEditDialog
                || !IsSummaryPropertyEditable(propertyName))
            {
                return false;
            }
            if (pendingSummaryPropertySave != null)
            {
                if (!HasSamePlaylistIdentity(pendingSummaryPropertySave.Commit.Table, table)
                    || !SummaryPropertyInputMatches(
                        pendingSummaryPropertySave.Commit.AppliedValues,
                        propertyName,
                        text))
                {
                    throw new InvalidOperationException(
                        "A playlist summary property save follow-up is pending. Retry the same edit before changing another value.");
                }
                if (!await service.IsRetryTargetCurrentAsync(
                    pendingSummaryPropertySave.Session,
                    pendingSummaryPropertySave.Commit))
                {
                    throw new InvalidOperationException(
                        "Playlist properties changed while summary save follow-up was pending. Reopen the edit before saving again.");
                }
                try
                {
                    await Task.Run(() => service.ApplyPostSaveUpdatesAsync(
                        pendingSummaryPropertySave.Commit));
                }
                catch (Exception followupFailure)
                {
                    await ReconcilePendingSummaryPropertySaveAsync(service, followupFailure);
                    throw;
                }
                pendingSummaryPropertySave.Session.Dispose();
                pendingSummaryPropertySave = null;
                return true;
            }

            PlaylistPropertyEditSession editSession =
                await service.CreateEditSessionAsync(table);
            if (editSession == null)
            {
                return false;
            }
            PlaylistPropertySaveCommit commit = null;
            try
            {
                PlaylistPropertyValues values = editSession.Values;
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
                commit = await service.TrySaveAsync(editSession, values);
                if (commit == null)
                {
                    return false;
                }
                try
                {
                    await Task.Run(() => service.ApplyPostSaveUpdatesAsync(commit));
                }
                catch (Exception followupFailure)
                {
                    PlaylistPropertyEditSession reconciledSession;
                    try
                    {
                        reconciledSession = await service.ReconcileFailedSaveAsync(editSession, commit);
                    }
                    catch (Exception reconciliationFailure)
                    {
                        throw new AggregateException(followupFailure, reconciliationFailure).Flatten();
                    }
                    editSession.Dispose();
                    editSession = null;
                    pendingSummaryPropertySave = new PlaylistSummaryPendingPropertySave(
                        commit,
                        reconciledSession);
                    ExceptionDispatchInfo.Capture(followupFailure).Throw();
                    throw new InvalidOperationException("Playlist summary save failure propagation unexpectedly returned.");
                }
                return true;
            }
            finally
            {
                editSession?.Dispose();
            }
        }
        finally
        {
            summaryPropertyEditGate.Release();
        }
    }

    private async Task ReconcilePendingSummaryPropertySaveAsync(
        PlaylistPropertySaveService service,
        Exception followupFailure)
    {
        PlaylistPropertyEditSession reconciledSession;
        try
        {
            reconciledSession = await service.ReconcileFailedSaveAsync(
                pendingSummaryPropertySave.Session,
                pendingSummaryPropertySave.Commit);
        }
        catch (Exception reconciliationFailure)
        {
            throw new AggregateException(followupFailure, reconciliationFailure).Flatten();
        }
        pendingSummaryPropertySave.Session.Dispose();
        pendingSummaryPropertySave = new PlaylistSummaryPendingPropertySave(
            pendingSummaryPropertySave.Commit,
            reconciledSession);
    }

    private static bool HasSamePlaylistIdentity(BMSTable left, BMSTable right)
    {
        return ReferenceEquals(left, right)
            || (left?.playlist_id.HasValue == true
                && right?.playlist_id == left.playlist_id);
    }

    private static bool SummaryPropertyInputMatches(
        PlaylistPropertyValues values,
        string propertyName,
        string text)
    {
        return propertyName switch
        {
            nameof(PlaylistSummaryRow.Name) => string.Equals(
                values.Name ?? string.Empty,
                (text ?? string.Empty).Trim(),
                StringComparison.Ordinal),
            nameof(PlaylistSummaryRow.FolderName) => string.Equals(
                BMSTable.NormalizeOutputDirectoryName(values.OutputDirectory),
                BMSTable.NormalizeOutputDirectoryName(
                    PlaylistPropertySaveService.NormalizeSummaryOutputDirectory(values.Name, text)),
                StringComparison.Ordinal),
            nameof(PlaylistSummaryRow.CompatPrefix) => string.Equals(
                values.CompatPrefix ?? string.Empty,
                (text ?? string.Empty).TrimStart(),
                StringComparison.Ordinal),
            nameof(PlaylistSummaryRow.Symbol) => string.Equals(
                values.Symbol ?? string.Empty,
                (text ?? string.Empty).Trim(),
                StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsSummaryPropertyEditable(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistSummaryRow.Name), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.FolderName), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.CompatPrefix), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.Symbol), StringComparison.Ordinal);
    }
}

internal sealed class PlaylistSummaryPendingPropertySave
{
    internal PlaylistSummaryPendingPropertySave(
        PlaylistPropertySaveCommit commit,
        PlaylistPropertyEditSession session)
    {
        Commit = commit ?? throw new ArgumentNullException(nameof(commit));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    internal PlaylistPropertySaveCommit Commit { get; }

    internal PlaylistPropertyEditSession Session { get; }
}
