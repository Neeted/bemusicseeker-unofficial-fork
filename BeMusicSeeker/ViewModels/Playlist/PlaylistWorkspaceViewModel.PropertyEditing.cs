using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

using PropertyValidationError = BeMusicSeeker.ViewModels.PlaylistPropertyValidationError;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly PlaylistPropertySaveService propertySaveService;

    private PlaylistPropertyDialogViewModel activePropertyDialog;

    private int propertyDialogOpenInProgress;
    // 既存編集sessionの対象・開始版で、自分のprefix差分だけをdrain時に分類します。保存終端で参照を解放します。
    private PlaylistPropertyEditSession summaryPropertyEditSession;

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
        ApplyPlaylistPropertyPresentation(() => BeginPlaylistSyncProgressOperation("property_save"));
    }

    private void ForwardPlaylistSyncProgressChanged(
        object sender,
        PlaylistSyncProgressChangedEventArgs request)
    {
        ApplyPlaylistPropertyPresentation(() =>
        {
            if (request?.Snapshot is PlaylistSyncProgressSnapshot snapshot)
            {
                ReportPlaylistSyncProgress(snapshot, "property_save",
                    BeMusicSeeker.Properties.Resources.Statusbar_progress_task_playlist_property_update);
            }
        });
    }

    private void ForwardPlaylistPropertySyncFinished(object sender, EventArgs e)
    {
        ApplyPlaylistPropertyPresentation(() => EndPlaylistSyncProgressOperation("property_save"));
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
            PublishPlaylistCatalogChanged();
            if (request.Reason.StartsWith("playlist_property_", StringComparison.Ordinal))
            {
                PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty);
            }
            if (Volatile.Read(ref summaryPropertyEditSession) == null)
            { RequestPlaylistSummaryDataRefresh(request.Reason, request.RebuildAsync); }
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
            PublishPlaylistCatalogChanged();
            PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty);
            PublishEntriesChanged(request.Table, request.RefreshSummaryIfVisible && Volatile.Read(ref summaryPropertyEditSession) == null);
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

    /// <summary>未登録draftと編集セッションの準備中だけPを保持し、画面の公開前に解放します。</summary>
    internal async Task<PlaylistPropertyDialogViewModel> CreatePlaylistPropertyDialogAsync()
    {
        if (Interlocked.CompareExchange(ref propertyDialogOpenInProgress, 1, 0) != 0)
        {
            return null;
        }
        try
        {
            BMSPlaylist store = GetPlaylistStore();
            if (!TryEnterPlaylistMutationAdmission(store, out IDisposable admission))
            {
                return null;
            }
            using IDisposable accepted = admission;
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
                    return null;
                }
                return OpenPropertyDialogCore(editSession);
            }
            catch
            {
                editSession?.Dispose();
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref propertyDialogOpenInProgress, 0);
        }
    }

    /// <summary>Pの下で旧選択を一意な現行IDへ解決し、その正本から画面公開前の編集準備を行います。</summary>
    internal async Task<PlaylistPropertyDialogViewModel> OpenPropertyDialogAsync(BMSTable table)
    {
        if (Interlocked.CompareExchange(ref propertyDialogOpenInProgress, 1, 0) != 0)
        {
            return null;
        }
        try
        {
            BMSPlaylist store = GetPlaylistStore();
            if (!TryEnterPlaylistMutationAdmission(store, out IDisposable admission))
            {
                return null;
            }
            using IDisposable accepted = admission;
            if (!CanOpenPlaylistEditDialog)
            {
                return null;
            }
            BMSTable currentTable = store.ResolveActivePlaylistTableForMutation(table);
            if (currentTable == null)
            {
                return null;
            }
            PlaylistPropertyEditSession editSession =
                await propertySaveService.CreateEditSessionAsync(currentTable);
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

    /// <summary>表属性の正本保存・必要出力・正規化済みセル表示を同じPで終えます。確定後失敗でも表示を試み、全summary再構築を開始しません。</summary>
    internal async Task<PlaylistSummaryPropertyEditCompletion> CompleteSummaryPropertyEditAsync(
        PlaylistSummaryRow row,
        string propertyName,
        string text,
        bool commit)
    {
        if (!commit || row?.TableRef == null)
        {
            return PlaylistSummaryPropertyEditCompletion.NoOp;
        }

        bool applied;
        PropertyValidationError? validationError;
        try
        {
            (applied, validationError) = await ApplySummaryPropertyEditCoreAsync(row, propertyName, text);
        }
        catch (Exception exception)
        {
            await ShowSummaryPropertyEditMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected
                + Environment.NewLine
                + Environment.NewLine
                + exception.Message,
                "Playlist summary property edit unexpected error");
            return PlaylistSummaryPropertyEditCompletion.RequiresRefresh;
        }

        if (applied)
        {
            return PlaylistSummaryPropertyEditCompletion.Success;
        }

        await ShowSummaryPropertyEditMessageAsync(
            validationError == PropertyValidationError.OutputDirectoryNameMustBeSingleName
                ? BeMusicSeeker.Properties.Resources.Error_OutputFolderNameMustBeSingleName
                : BeMusicSeeker.Properties.Resources.Msg_invalid_setting,
            "Playlist summary property edit validation error");
        return PlaylistSummaryPropertyEditCompletion.RequiresRefresh;
    }

    private async Task ShowSummaryPropertyEditMessageAsync(string message, string routeName)
    {
        UiDialogResult result = await playlistWorkspaceDialogService.ShowMessageAsync(
                UiMessageRequest.CreateError(
                    message,
                    BeMusicSeeker.Properties.Resources.Error))
            .ConfigureAwait(true);
        if (result == null)
        {
            throw new InvalidOperationException(routeName + " returned no dialog result.");
        }
        if (result.Status is UiDialogStatus.Accepted
            or UiDialogStatus.Rejected
            or UiDialogStatus.CancelledByUser
            or UiDialogStatus.ClosedByUser)
        {
            return;
        }
        throw result.Exception
            ?? new InvalidOperationException(
                routeName + " could not be displayed (" + result.Status + ").");
    }

    private async Task<(bool Applied, PropertyValidationError? ValidationError)> ApplySummaryPropertyEditCoreAsync(
        PlaylistSummaryRow row,
        string propertyName,
        string text)
    {
        BMSPlaylist store = GetPlaylistStore();
        if (!TryBeginPlaylistMutation(store, PlaylistWorkspaceMutationKind.PropertySave, out IDisposable admission)) { return (false, null); }
        using IDisposable accepted = admission;
        using LibraryFileMutationCapability authority = store.CreatePlaylistMutationCapability(admission);

        PlaylistPropertySaveService service = propertySaveService
            ?? throw new InvalidOperationException("Playlist property editing is not available.");
        BMSTable table = ResolveActivePlaylistSummaryTable(row);
        if (table == null
            || !CanOpenPlaylistEditDialog
            || !IsSummaryPropertyEditable(propertyName))
        {
            return (false, null);
        }
        PlaylistPropertyEditSession editSession =
            await service.CreateEditSessionAsync(table);
        if (editSession == null)
        {
            return (false, null);
        }
        PlaylistPropertySaveCommit commit = null;
        try
        {
            Volatile.Write(ref summaryPropertyEditSession, editSession);
            PlaylistPropertyValues values = editSession.Values;
            switch (propertyName)
            {
                case nameof(PlaylistSummaryRow.Name):
                    {
                        string name = (text ?? string.Empty).Trim();
                        if (string.Equals(values.Name ?? string.Empty, name, StringComparison.Ordinal))
                        {
                            await ApplySummarySavedPropertiesAsync(table);
                            return (true, null);
                        }
                        values.Name = name;
                        break;
                    }
                case nameof(PlaylistSummaryRow.FolderName):
                    {
                        if (!BMSTable.IsOutputDirectoryNameInputValid(text))
                        {
                            return (false, PropertyValidationError.OutputDirectoryNameMustBeSingleName);
                        }
                        string outputDirectory = PlaylistPropertySaveService.NormalizeSummaryOutputDirectory(
                            values.Name,
                            text);
                        if (string.Equals(
                            BMSTable.NormalizeOutputDirectoryName(values.OutputDirectory),
                            BMSTable.NormalizeOutputDirectoryName(outputDirectory),
                            StringComparison.Ordinal))
                        {
                            await ApplySummarySavedPropertiesAsync(table);
                            return (true, null);
                        }
                        values.OutputDirectory = outputDirectory;
                        break;
                    }
                case nameof(PlaylistSummaryRow.CompatPrefix):
                    {
                        string compatPrefix = (text ?? string.Empty).TrimStart();
                        if (string.Equals(values.CompatPrefix ?? string.Empty, compatPrefix, StringComparison.Ordinal))
                        {
                            await ApplySummarySavedPropertiesAsync(table);
                            return (true, null);
                        }
                        values.CompatPrefix = compatPrefix;
                        break;
                    }
                case nameof(PlaylistSummaryRow.Symbol):
                    {
                        string symbol = (text ?? string.Empty).Trim();
                        if (string.Equals(values.Symbol ?? string.Empty, symbol, StringComparison.Ordinal))
                        {
                            await ApplySummarySavedPropertiesAsync(table);
                            return (true, null);
                        }
                        values.Symbol = symbol;
                        break;
                    }
                default:
                    return (false, null);
            }
            (commit, PropertyValidationError? validationError) = await service.TrySaveAsync(editSession, values, authority);
            if (commit == null)
            {
                return (false, validationError);
            }
            editSession.Adopt(commit);
            try
            {
                await ApplySummarySavedPropertiesAsync(commit.Table).ConfigureAwait(true);
            }
            catch (Exception failure)
            {
                if (commit.Exception == null) { throw; }
                TryLogPlaylistDropSecondaryFailure(failure, "playlist_summary_edit_projection_failed");
            }
            service.PublishSaveFailure(commit);
            return (true, null);
        }
        finally
        {
            Volatile.Write(ref summaryPropertyEditSession, null);
            editSession?.Dispose();
        }
    }

    private Task ApplySummarySavedPropertiesAsync(BMSTable table)
        => playlistRestoreUiApplyScheduler(() =>
        {
            var failures = new List<Exception>();
            try
            {
                playlistSummaryView.PreserveSelection(() =>
                {
                    PlaylistSummaryRow current = null;
                    System.Collections.Specialized.NotifyCollectionChangedEventArgs change = null;
                    bool textChanged = false;
                    int projectedRows = 0, filteredRows = 0, comparisons = 0;
                    lock (playlistSummaryTransitionLock)
                    {
                        playlistSummaryRowsCache.TryGetTableRow(table, out PlaylistSummaryRow raw);
                        if (isPlaylistSummaryMode && appliedPlaylistSummaryIdentity is PlaylistSummaryPresentationIdentity identity)
                        {
                            playlistSummaryView.TryGetTableRow(table, out current);
                            var sort = new ChartListSortParameters { ColumnsName = identity.SortColumn, Direction = identity.SortDirection };
                            IComparer<PlaylistSummaryRow> comparer = PlaylistSummarySortEngine.CreateComparer(sort, out _);
                            var countedComparer = Comparer<PlaylistSummaryRow>.Create((left, right) => { comparisons++; return comparer.Compare(left, right); });
                            change = playlistSummaryView.ApplySavedProperties(table, raw, countedComparer,
                            target => { filteredRows++; return ApplyPlaylistSummaryFilters([target], identity.KeywordFilter, identity.OwnedFilter).Any(); }, out projectedRows);
                            string nextText = FormatPlaylistSummaryText(playlistSummaryView.TotalCharts, playlistSummaryView.Count);
                            textChanged = playlistSummaryText != nextText;
                            playlistSummaryText = nextText;
                        }
                        else if (raw != null) { raw.ApplySavedProperties(table, notify: false); projectedRows++; }
                    }
                    // 表示交換後の旧rowは完了対象にしません。現在行だけ派生属性と局所集合変更を通知します。
                    LogBuild("playlist_summary_edit projectedRows=" + projectedRows + " filteredRows=" + filteredRows + " comparisons=" + comparisons);
                    if (current != null) { TryPublish(current.PublishSavedProperties, failures); }
                    TryPublish(() => playlistSummaryView.PublishLocalChange(change), failures);
                    if (textChanged) { TryPublish(() => RaisePropertyChanged(nameof(PlaylistSummaryText)), failures); }
                    return true;
                }, () => TryPublish(() => playlistSummaryView.PublishSelectionUpdateCompleted(EventArgs.Empty), failures));
            }
            catch (Exception ex) { failures.Add(ex); }
            if (failures.Count > 0) { throw new PlaylistSummaryPublishException(new AggregateException(failures)); }
        });

    private static bool IsSummaryPropertyEditable(string propertyName)
    {
        return string.Equals(propertyName, nameof(PlaylistSummaryRow.Name), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.FolderName), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.CompatPrefix), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(PlaylistSummaryRow.Symbol), StringComparison.Ordinal);
    }
}

internal sealed class PlaylistSummaryPropertyEditCompletion
{
    private PlaylistSummaryPropertyEditCompletion(bool applied, bool refreshRequired)
    {
        IsApplied = applied;
        RefreshRequired = refreshRequired;
    }

    internal static PlaylistSummaryPropertyEditCompletion NoOp { get; } =
        new(false, refreshRequired: false);

    internal static PlaylistSummaryPropertyEditCompletion Success { get; } =
        new(true, refreshRequired: false);

    internal static PlaylistSummaryPropertyEditCompletion RequiresRefresh { get; } =
        new(false, refreshRequired: true);

    internal bool IsApplied { get; }

    internal bool RefreshRequired { get; }
}
