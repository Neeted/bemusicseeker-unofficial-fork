using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private readonly ExternalPlaylistImportQueue externalPlaylistImportQueue = new();

    internal event EventHandler<ExternalPlaylistImportQueueSummaryReadyEventArgs> ExternalPlaylistImportQueueSummaryReady;

    internal event EventHandler<PlaylistImportNotificationsFlushRequestedEventArgs> PlaylistImportNotificationsFlushRequested;

    internal event EventHandler<ExternalPlaylistImportSummaryRefreshFailedEventArgs> ExternalPlaylistImportSummaryRefreshFailed;

    internal void EnqueueExternalPlaylistBMSTableImport(Uri uri)
    {
        EnqueueExternalPlaylistBMSTableImports([uri]);
    }

    internal void EnqueueExternalPlaylistBMSTableImports(IEnumerable<Uri> uris)
    {
        if (externalPlaylistImportQueue.EnqueueRange(uris))
        {
            _ = DrainExternalPlaylistImportQueueAsync().Logging("DrainExternalPlaylistImportQueueAsync");
        }
    }

    private async Task DrainExternalPlaylistImportQueueAsync()
    {
        const int ExternalPlaylistImportPostProgressStepCount = 4;
        List<ExternalPlaylistImportOutcome> outcomes = [];
        BMSPlaylist tables = GetPlaylistStore();
        using BMSPlaylist.OperationNotificationScope notificationScope = BMSPlaylist.BeginOperationNotificationScope();
        BeginPlaylistSyncProgressOperation();
        try
        {
            IReadOnlyList<Uri> batch;
            while ((batch = externalPlaylistImportQueue.DequeueBatch()).Count > 0)
            {
                int totalCount = batch.Count;
                int progressTotalCount = totalCount + ExternalPlaylistImportPostProgressStepCount;
                int postProgressCompletedCount = totalCount;
                List<ExternalPlaylistImportWorkItem> loadItems = [.. batch
                    .Where(uri => uri != null && uri.IsAbsoluteUri)
                    .Select(uri => new ExternalPlaylistImportWorkItem(uri))];
                if (loadItems.Count == 0)
                {
                    continue;
                }
                UpdateExternalPlaylistImportProgress(0, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_load_tables);
                List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loadResults = await tables.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(
                    loadItems.Select(item => item.SourceTable),
                    inheritLocalTableProperties: false,
                    snapshot => UpdateExternalPlaylistImportProgress(
                        Math.Max(0, snapshot?.CompletedTableCount ?? 0),
                        progressTotalCount,
                        snapshot?.CurrentUri,
                        snapshot?.CurrentTableName ?? string.Empty,
                        BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_load_tables),
                    "external_playlist_import",
                    CancellationToken.None,
                    schedulePlaylistUrlCompletionRefresh: false).ConfigureAwait(false);

                var itemBySourceTable = loadItems.ToDictionary(item => item.SourceTable);
                foreach (PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult loadResult in loadResults)
                {
                    if (loadResult == null || !itemBySourceTable.TryGetValue(loadResult.SourceTable, out ExternalPlaylistImportWorkItem item))
                    {
                        continue;
                    }
                    if (loadResult.Succeeded)
                    {
                        item.LoadedTable = loadResult.ExternalTable;
                        continue;
                    }
                    item.Failure = loadResult.Exception ?? new InvalidOperationException("Playlist load returned no table.");
                    outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, item.Failure));
                    RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Uri, item.Failure));
                }

                UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_check_duplicates);
                List<ExternalPlaylistImportWorkItem> registrationItems = PrepareExternalPlaylistImportRegistrationItems(loadItems, outcomes);
                postProgressCompletedCount++;

                List<ExternalPlaylistImportWorkItem> registeredItems = [];
                if (registrationItems.Count > 0)
                {
                    List<ExternalPlaylistImportWorkItem> pendingRegistrationItems = registrationItems;
                    while (pendingRegistrationItems.Count > 0)
                    {
                        try
                        {
                            UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_register_playlists);
                            await tables.ExternalSyncOwner.RegistrateExternalTablesAsync(
                                pendingRegistrationItems.Select(item => item.LoadedTable),
                                renameDuplicateName: false,
                                "external_playlist_import",
                                CancellationToken.None).ConfigureAwait(false);
                            registeredItems = pendingRegistrationItems;
                            break;
                        }
                        catch (PlaylistAlreadyExistsException ex)
                        {
                            List<ExternalPlaylistImportWorkItem> duplicateItems = [.. pendingRegistrationItems
                                .Where(item => string.Equals(item.LoadedTable?.name, ex.PlaylistName, StringComparison.Ordinal))];
                            if (duplicateItems.Count == 0)
                            {
                                WriteExternalPlaylistImportWarning(ex, "external_playlist_import_batch_registration_failed_duplicate_name_unknown");
                                foreach (ExternalPlaylistImportWorkItem item in pendingRegistrationItems)
                                {
                                    outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, ex));
                                    RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Uri, ex));
                                }
                                break;
                            }
                            foreach (ExternalPlaylistImportWorkItem item in duplicateItems)
                            {
                                RecordExternalPlaylistImportDuplicateNameSkip(item, ex.PlaylistName, ex, outcomes);
                            }
                            pendingRegistrationItems = [.. pendingRegistrationItems.Where(item => !duplicateItems.Contains(item))];
                        }
                        catch (Exception ex)
                        {
                            WriteExternalPlaylistImportWarning(ex, "external_playlist_import_batch_registration_failed");
                            foreach (ExternalPlaylistImportWorkItem item in pendingRegistrationItems)
                            {
                                outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, ex));
                                RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Uri, ex));
                            }
                            break;
                        }
                    }
                }
                postProgressCompletedCount++;

                if (registeredItems.Count > 0)
                {
                    Exception referenceUpdateException = null;
                    bool referenceUpdateCompleted = false;
                    try
                    {
                        UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_update_references);
                        referenceUpdateCompleted = CompleteImportedPlaylistRegistrations(
                            [.. registeredItems.Select(item => item.LoadedTable)],
                            "external_playlist_import",
                            queueSummaryRefresh: false);
                    }
                    catch (Exception ex)
                    {
                        referenceUpdateException = ex;
                        WriteExternalPlaylistImportWarning(ex, "external_playlist_import_reference_update_failed");
                    }
                    if (referenceUpdateException == null)
                    {
                        foreach (ExternalPlaylistImportWorkItem item in registeredItems)
                        {
                            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateSuccess(item.LoadedTable, item.LoadedTable, item.Uri, updated: false));
                        }
                        if (referenceUpdateCompleted)
                        {
                            try
                            {
                                QueuePlaylistSummaryDataRefreshFromImport(
                                    "external_playlist_import",
                                    ReportExternalPlaylistImportSummaryRefreshFailure);
                            }
                            catch (Exception ex)
                            {
                                ReportExternalPlaylistImportSummaryRefreshFailure(ex);
                            }
                        }
                    }
                    foreach (ExternalPlaylistImportWorkItem item in registeredItems)
                    {
                        if (referenceUpdateException != null)
                        {
                            outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, referenceUpdateException));
                            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(item.LoadedTable, item.LoadedTable, item.Uri, referenceUpdateException));
                            WriteExternalPlaylistImportWarning(referenceUpdateException, "external_playlist_import_reference_update_warning uri=" + (item.Uri?.ToString() ?? string.Empty));
                            continue;
                        }
                        outcomes.Add(ExternalPlaylistImportOutcome.Imported(item.Uri, item.LoadedTable.name));
                    }
                }
                postProgressCompletedCount++;
                UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_finish);
                UpdateExternalPlaylistImportProgress(progressTotalCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_finish);
            }
        }
        finally
        {
            EndPlaylistSyncProgressOperation();
            PlaylistImportNotificationsFlushRequested?.Invoke(
                this,
                new PlaylistImportNotificationsFlushRequestedEventArgs(
                    notificationScope,
                    "external playlist import notification"));
        }
        ExternalPlaylistImportQueueSummaryReady?.Invoke(
            this,
            new ExternalPlaylistImportQueueSummaryReadyEventArgs(
                new ExternalPlaylistImportQueueSummary(outcomes)));
    }

    private List<ExternalPlaylistImportWorkItem> PrepareExternalPlaylistImportRegistrationItems(IReadOnlyList<ExternalPlaylistImportWorkItem> loadItems, List<ExternalPlaylistImportOutcome> outcomes)
    {
        var reservedNames = new HashSet<string>(GetExternalPlaylistImportExistingNamesSnapshot(), StringComparer.Ordinal);
        List<ExternalPlaylistImportWorkItem> registrationItems = [];
        foreach (ExternalPlaylistImportWorkItem item in loadItems ?? [])
        {
            if (item?.LoadedTable == null || item.Failure != null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.LoadedTable.Output_dir))
            {
                var exception = new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_OutputDirNameEmpty);
                item.Failure = exception;
                outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, exception));
                RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Uri, exception));
                continue;
            }
            string desiredName = item.LoadedTable.name ?? string.Empty;
            if (reservedNames.Contains(desiredName))
            {
                var exception = new PlaylistAlreadyExistsException(BeMusicSeeker.Properties.Resources.Error_PlaylistAlreadyExists, desiredName);
                RecordExternalPlaylistImportDuplicateNameSkip(item, desiredName, exception, outcomes);
                continue;
            }
            reservedNames.Add(desiredName);
            registrationItems.Add(item);
        }
        return registrationItems;
    }

    private void RecordExternalPlaylistImportDuplicateNameSkip(ExternalPlaylistImportWorkItem item, string playlistName, PlaylistAlreadyExistsException exception, List<ExternalPlaylistImportOutcome> outcomes)
    {
        if (item == null)
        {
            return;
        }
        string skippedName = playlistName ?? item.LoadedTable?.name ?? string.Empty;
        item.Failure = exception;
        WriteExternalPlaylistImportInfo("playlist_register_skipped_duplicate_name uri=" + (item.Uri?.ToString() ?? string.Empty) + " table=" + skippedName);
        outcomes?.Add(ExternalPlaylistImportOutcome.SkippedDuplicateName(item.Uri, skippedName, exception));
    }

    private IReadOnlyList<string> GetExternalPlaylistImportExistingNamesSnapshot()
    {
        BMSPlaylist tables = GetPlaylistStore();
        tables.AcquireReaderLockBMSTables();
        try
        {
            return [.. (tables.BMSTables ?? Enumerable.Empty<BMSTable>())
                .Where(table => table != null)
                .Select(table => table.name)
                .Where(name => name != null)];
        }
        finally
        {
            tables.FreeReaderLockBMSTables();
        }
    }

    private void UpdateExternalPlaylistImportProgress(int completedCount, int totalCount, Uri currentUri, string currentTableName, string phaseText = null)
    {
        string detail = BuildExternalPlaylistImportProgressDetail(phaseText, currentTableName);
        ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = totalCount > 0,
            TotalTableCount = Math.Max(totalCount, 0),
            CompletedTableCount = completedCount,
            CurrentTableName = detail,
            CurrentUri = currentUri,
            LabelFormat = BeMusicSeeker.Properties.Resources.Playlist_import_progress_label_format,
            SingleLabel = BeMusicSeeker.Properties.Resources.Playlist_import_progress_single_label
        });
    }

    internal static int ResolveExternalPlaylistImportQueueProgressTotal(int completedCount, bool hasActiveImport, int pendingCount)
    {
        int normalizedCompletedCount = Math.Max(0, completedCount);
        int normalizedPendingCount = Math.Max(0, pendingCount);
        return Math.Max(normalizedCompletedCount + (hasActiveImport ? 1 : 0) + normalizedPendingCount, normalizedCompletedCount);
    }

    private static string BuildExternalPlaylistImportProgressDetail(string phaseText, string currentTableName)
    {
        string phase = phaseText ?? string.Empty;
        string detail = currentTableName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(phase))
        {
            return detail;
        }
        if (string.IsNullOrWhiteSpace(detail))
        {
            return phase;
        }
        return phase + ": " + detail;
    }

    private static Uri NormalizeExternalPlaylistImportUri(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return uri;
        }
        return new Uri(uri.AbsoluteUri, UriKind.Absolute);
    }

    private sealed class ExternalPlaylistImportWorkItem
    {
        internal ExternalPlaylistImportWorkItem(Uri uri)
        {
            Uri = NormalizeExternalPlaylistImportUri(uri);
            SourceTable = new BMSTable
            {
                name = Uri?.ToString() ?? string.Empty,
                Page_url = Uri
            };
        }

        internal Uri Uri { get; }

        internal BMSTable SourceTable { get; }

        internal BMSTable LoadedTable { get; set; }

        internal Exception Failure { get; set; }
    }
    internal bool CompleteImportedPlaylistRegistrations(
        IReadOnlyList<BMSTable> importedTables,
        string reason,
        bool queueSummaryRefresh = true)
    {
        BMSPlaylist tables = GetPlaylistStore();
        BMSLibrary files = GetPlaylistLibrary();
        List<BMSTable> tableList = [.. (importedTables ?? [])
            .Where(table => table != null)
            .Where(table => tables.ContainsBMSTable(table))];
        if (tableList.Count == 0)
        {
            return false;
        }
        files.AddReferenceBMSTablesIncremental(tableList);
        foreach (BMSTable table in tableList.Where(table => !tables.ContainsBMSTable(table)))
        {
            files.RemoveReferenceBMSTables(table);
        }
        RequestPlaylistReferenceSortInvalidation();
        if (queueSummaryRefresh)
        {
            QueuePlaylistSummaryDataRefreshFromImport(
                reason ?? "playlist_registered");
        }
        return true;
    }

    private void QueuePlaylistSummaryDataRefreshFromImport(
        string reason,
        Action<Exception> failure = null)
    {
        dispatchPresentation(() =>
        {
            try
            {
                RequestPlaylistSummaryDataRefresh(
                    reason);
            }
            catch (Exception exception) when (failure != null)
            {
                failure(exception);
            }
        });
    }

    private void ReportExternalPlaylistImportSummaryRefreshFailure(Exception exception)
    {
        WriteExternalPlaylistImportWarning(exception, "external_playlist_import_summary_refresh_failed");
        // The playlist registration already succeeded; summary refresh is a presentation concern.
        // Keep the import result successful, but surface the refresh failure to the user.
        ExternalPlaylistImportSummaryRefreshFailed?.Invoke(
            this,
            new ExternalPlaylistImportSummaryRefreshFailedEventArgs(exception));
    }

    private void WriteExternalPlaylistImportWarning(Exception exception, string message)
    {
        (externalPlaylistImportWarningLog
            ?? throw new InvalidOperationException("External playlist import warning logging is not configured."))
            (exception, message ?? string.Empty);
    }

    private void WriteExternalPlaylistImportInfo(string message)
    {
        (externalPlaylistImportInfoLog
            ?? throw new InvalidOperationException("External playlist import info logging is not configured."))
            (message ?? string.Empty);
    }
}

internal sealed class ExternalPlaylistImportQueueSummaryReadyEventArgs : EventArgs
{
    internal ExternalPlaylistImportQueueSummaryReadyEventArgs(ExternalPlaylistImportQueueSummary summary)
    {
        Summary = summary;
    }

    internal ExternalPlaylistImportQueueSummary Summary { get; }
}

internal sealed class PlaylistImportNotificationsFlushRequestedEventArgs : EventArgs
{
    internal PlaylistImportNotificationsFlushRequestedEventArgs(
        BMSPlaylist.OperationNotificationScope scope,
        string routeName)
    {
        Scope = scope;
        RouteName = routeName;
    }

    internal BMSPlaylist.OperationNotificationScope Scope { get; }

    internal string RouteName { get; }
}

internal sealed class ExternalPlaylistImportSummaryRefreshFailedEventArgs : EventArgs
{
    internal ExternalPlaylistImportSummaryRefreshFailedEventArgs(Exception exception)
    {
        Exception = exception;
    }

    internal Exception Exception { get; }
}
