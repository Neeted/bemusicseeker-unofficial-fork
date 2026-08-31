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
    internal event EventHandler<ExternalPlaylistImportQueueSummaryReadyEventArgs> ExternalPlaylistImportQueueSummaryReady;

    internal event EventHandler<ExternalPlaylistImportSummaryRefreshFailedEventArgs> ExternalPlaylistImportSummaryRefreshFailed;

    internal bool TryEnqueueExternalPlaylistCollectionImport(BMSTableSimple source)
    {
        if (source?.url == null)
        {
            return false;
        }
        return EnqueueExternalPlaylistBMSTableImports([source.url]);
    }

    internal bool TryEnqueueBuiltInExternalPlaylistImport(string rawTag)
    {
        Uri uri = new(rawTag);
        return EnqueueExternalPlaylistBMSTableImports([uri]);
    }

    internal ExternalPlaylistUriSubmissionResult SubmitExternalPlaylistUriText(string input)
    {
        ExternalPlaylistUriParseResult parseResult = ParseExternalPlaylistUriInput(input);
        if (parseResult.ValidUris.Count > 0)
        {
            EnqueueExternalPlaylistBMSTableImports(parseResult.ValidUris);
        }
        return new ExternalPlaylistUriSubmissionResult(parseResult.ValidUris.Count, parseResult.InvalidLines);
    }

    private static ExternalPlaylistUriParseResult ParseExternalPlaylistUriInput(string input)
    {
        List<Uri> validUris = [];
        List<string> invalidLines = [];
        string[] lines = (input ?? string.Empty).Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        foreach (string line in lines)
        {
            string trimmedLine = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }
            if (Uri.TryCreate(trimmedLine, UriKind.Absolute, out Uri uri))
            {
                validUris.Add(uri);
            }
            else
            {
                invalidLines.Add(trimmedLine);
            }
        }
        return new ExternalPlaylistUriParseResult(validUris, invalidLines);
    }

    private bool EnqueueExternalPlaylistBMSTableImports(IEnumerable<Uri> uris)
    {
        BMSPlaylist tables = getPlaylistStore();
        StartupReadinessCoordinator readiness = tables?.StartupReadiness;
        if (tables == null
            || readiness == null
            || !readiness.TryAdmitExternalPlaylistImports(uris, out bool shouldStartDrain))
        {
            return false;
        }
        if (shouldStartDrain)
        {
            _ = DrainExternalPlaylistImportQueueAsync().Logging("DrainExternalPlaylistImportQueueAsync");
        }
        return true;
    }

    private async Task DrainExternalPlaylistImportQueueAsync()
    {
        const int ExternalPlaylistImportPostProgressStepCount = 4;
        List<ExternalPlaylistImportOutcome> outcomes = [];
        BMSPlaylist tables = getPlaylistStore();
        StartupReadinessCoordinator readiness = tables?.StartupReadiness;
        if (tables == null || readiness == null)
        {
            return;
        }
        if (!readiness.TryBeginExternalPlaylistImportDrain())
        {
            return;
        }
        CancellationToken cancellationToken = readiness.ShutdownToken;
        bool progressStarted = false;
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = null;
        try
        {
            await readiness.WaitForRequiredPlaylistReadinessAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
            if (tables.IsShutdownRequested)
            {
                return;
            }
            notificationSession = tables.OperationNotificationOwner.BeginSession();
            BeginPlaylistSyncProgressOperation();
            progressStarted = true;
            IReadOnlyList<Uri> batch;
            while ((batch = readiness.DequeueExternalPlaylistImportBatch()).Count > 0)
            {
                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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
                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                UpdateExternalPlaylistImportProgress(0, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_load_tables);
                List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loadResults = await tables.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(
                    loadItems.Select(item => item.SourceTable),
                    inheritLocalTableProperties: false,
                    snapshot =>
                    {
                        if (IsExternalPlaylistImportShutdownRequested(tables, cancellationToken))
                        {
                            return;
                        }
                        UpdateExternalPlaylistImportProgress(
                            Math.Max(0, snapshot?.CompletedTableCount ?? 0),
                            progressTotalCount,
                            snapshot?.CurrentUri,
                            snapshot?.CurrentTableName ?? string.Empty,
                            BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_load_tables);
                    },
                    "external_playlist_import",
                    cancellationToken,
                    schedulePlaylistUrlCompletionRefresh: false).ConfigureAwait(false);
                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);

                var itemBySourceTable = loadItems.ToDictionary(item => item.SourceTable);
                foreach (PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult loadResult in loadResults)
                {
                    ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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

                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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
                            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                            UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_register_playlists);
                            await tables.ExternalSyncOwner.RegistrateExternalTablesAsync(
                                pendingRegistrationItems.Select(item => item.LoadedTable),
                                renameDuplicateName: false,
                                "external_playlist_import",
                                cancellationToken).ConfigureAwait(false);
                            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                            registeredItems = pendingRegistrationItems;
                            break;
                        }
                        catch (OperationCanceledException) when (IsExternalPlaylistImportShutdownRequested(tables, cancellationToken))
                        {
                            throw;
                        }
                        catch (PlaylistAlreadyExistsException ex)
                        {
                            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                            List<ExternalPlaylistImportWorkItem> duplicateItems = [.. pendingRegistrationItems
                                .Where(item => string.Equals(item.LoadedTable?.name, ex.PlaylistName, StringComparison.Ordinal))];
                            if (duplicateItems.Count == 0)
                            {
                                WriteExternalPlaylistImportWarning(ex, "external_playlist_import_batch_registration_failed_duplicate_name_unknown");
                                foreach (ExternalPlaylistImportWorkItem item in pendingRegistrationItems)
                                {
                                    ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                                    outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, ex));
                                    RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Uri, ex));
                                }
                                break;
                            }
                            foreach (ExternalPlaylistImportWorkItem item in duplicateItems)
                            {
                                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                                RecordExternalPlaylistImportDuplicateNameSkip(item, ex.PlaylistName, ex, outcomes);
                            }
                            pendingRegistrationItems = [.. pendingRegistrationItems.Where(item => !duplicateItems.Contains(item))];
                        }
                        catch (Exception ex)
                        {
                            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                            WriteExternalPlaylistImportWarning(ex, "external_playlist_import_batch_registration_failed");
                            foreach (ExternalPlaylistImportWorkItem item in pendingRegistrationItems)
                            {
                                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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
                        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                        UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_update_references);
                        referenceUpdateCompleted = CompleteImportedPlaylistRegistrations(
                            [.. registeredItems.Select(item => item.LoadedTable)],
                            "external_playlist_import",
                            queueSummaryRefresh: false,
                            cancellationToken: cancellationToken);
                    }
                    catch (OperationCanceledException) when (IsExternalPlaylistImportShutdownRequested(tables, cancellationToken))
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        referenceUpdateException = ex;
                        WriteExternalPlaylistImportWarning(ex, "external_playlist_import_reference_update_failed");
                    }
                    if (referenceUpdateException == null)
                    {
                        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                        foreach (ExternalPlaylistImportWorkItem item in registeredItems)
                        {
                            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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
                        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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
                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_finish);
                ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                UpdateExternalPlaylistImportProgress(progressTotalCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_finish);
            }
        }
        catch (OperationCanceledException) when (readiness.IsShutdownRequested || tables.IsShutdownRequested || cancellationToken.IsCancellationRequested)
        {
            // Shutdown is a terminal lifecycle boundary.  Do not publish a late progress,
            // notification, summary, or database/UI mutation after it.
        }
        finally
        {
            if (progressStarted && !readiness.IsShutdownRequested && !tables.IsShutdownRequested)
            {
                EndPlaylistSyncProgressOperation();
            }
            if (notificationSession != null
                && !readiness.IsShutdownRequested
                && !tables.IsShutdownRequested)
            {
                PlaylistOperationNotificationPresentationRequested?.Invoke(
                    this,
                    new PlaylistOperationNotificationPresentationRequestedEventArgs(
                        notificationSession?.TakeReceipt(),
                        "external playlist import notification"));
            }
            notificationSession?.Dispose();
            bool drainComplete = readiness.CompleteExternalPlaylistImportDrain();
            if (!drainComplete && !readiness.IsShutdownRequested && !tables.IsShutdownRequested)
            {
                _ = DrainExternalPlaylistImportQueueAsync().Logging("DrainExternalPlaylistImportQueueAsync");
            }
        }
        if (readiness.IsShutdownRequested || tables.IsShutdownRequested)
        {
            return;
        }
        ExternalPlaylistImportQueueSummaryReady?.Invoke(
            this,
            new ExternalPlaylistImportQueueSummaryReadyEventArgs(
                new ExternalPlaylistImportQueueSummary(outcomes)));
    }

    private static bool IsExternalPlaylistImportShutdownRequested(
        BMSPlaylist tables,
        CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested || tables?.IsShutdownRequested == true;
    }

    private static void ThrowIfExternalPlaylistImportShutdownRequested(
        BMSPlaylist tables,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tables?.IsShutdownRequested == true)
        {
            throw new OperationCanceledException(cancellationToken);
        }
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
        bool queueSummaryRefresh = true,
        CancellationToken cancellationToken = default)
    {
        BMSPlaylist tables = GetPlaylistStore();
        BMSLibrary files = GetPlaylistLibrary();
        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
        List<BMSTable> tableList = [.. (importedTables ?? [])
            .Where(table => table != null)
            .Where(table => tables.ContainsBMSTable(table))];
        if (tableList.Count == 0)
        {
            return false;
        }
        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
        files.AddReferenceBMSTablesIncremental(tableList);
        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
        foreach (BMSTable table in tableList.Where(table => !tables.ContainsBMSTable(table)))
        {
            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
            files.RemoveReferenceBMSTables(table);
        }
        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
        RequestPlaylistReferenceSortInvalidation();
        if (queueSummaryRefresh)
        {
            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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
            if (getPlaylistStore()?.IsShutdownRequested == true)
            {
                return;
            }
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

    private sealed class ExternalPlaylistUriParseResult
    {
        internal ExternalPlaylistUriParseResult(IEnumerable<Uri> validUris, IEnumerable<string> invalidLines)
        {
            ValidUris = [.. (validUris ?? [])];
            InvalidLines = [.. (invalidLines ?? [])];
        }

        internal IReadOnlyList<Uri> ValidUris { get; }

        internal IReadOnlyList<string> InvalidLines { get; }
    }
}

internal sealed class ExternalPlaylistUriSubmissionResult
{
    internal ExternalPlaylistUriSubmissionResult(int validUriCount, IEnumerable<string> invalidLines)
    {
        ValidUriCount = Math.Max(0, validUriCount);
        InvalidLines = [.. (invalidLines ?? [])];
    }

    internal int ValidUriCount { get; }

    internal IReadOnlyList<string> InvalidLines { get; }

    internal bool HasValidUris => ValidUriCount > 0;

    internal bool HasInvalidLines => InvalidLines.Count > 0;
}

internal sealed class ExternalPlaylistImportQueueSummaryReadyEventArgs : EventArgs
{
    internal ExternalPlaylistImportQueueSummaryReadyEventArgs(ExternalPlaylistImportQueueSummary summary)
    {
        Summary = summary;
    }

    internal ExternalPlaylistImportQueueSummary Summary { get; }
}

internal sealed class ExternalPlaylistImportSummaryRefreshFailedEventArgs : EventArgs
{
    internal ExternalPlaylistImportSummaryRefreshFailedEventArgs(Exception exception)
    {
        Exception = exception;
    }

    internal Exception Exception { get; }
}
