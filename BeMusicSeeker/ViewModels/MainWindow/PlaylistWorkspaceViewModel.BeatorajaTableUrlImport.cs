using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private int beatorajaTableUrlImportRunning;

    internal event EventHandler<BeatorajaTableUrlImportConfirmationRequestedEventArgs> BeatorajaTableUrlImportConfirmationRequested;

    internal event EventHandler<BeatorajaTableUrlImportNotificationRequestedEventArgs> BeatorajaTableUrlImportNotificationRequested;

    internal event EventHandler<BeatorajaTableUrlImportSummaryReadyEventArgs> BeatorajaTableUrlImportSummaryReady;

    internal void StartBeatorajaTableUrlImport(string rootPath)
    {
        if (Interlocked.CompareExchange(ref beatorajaTableUrlImportRunning, 1, 0) != 0)
        {
            RequestBeatorajaTableUrlImportNotification(
                BeatorajaTableUrlImportNotificationKind.Warning,
                BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_already_running,
                BeMusicSeeker.Properties.Resources.Warning,
                "beatoraja Table URL import already running notification");
            return;
        }

        bool started = false;
        try
        {
            BMSPlaylist playlistStore = getPlaylistStore?.Invoke();
            BMSLibrary playlistLibrary = getPlaylistLibrary?.Invoke();
            if (playlistStore == null || playlistLibrary == null || playlistStore.BMSTables == null)
            {
                RequestBeatorajaTableUrlImportNotification(
                    BeatorajaTableUrlImportNotificationKind.Warning,
                    BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                    BeMusicSeeker.Properties.Resources.Warning,
                    "beatoraja Table URL import initialization notification");
                return;
            }
            if (!BeatorajaConfigService.IsBeatorajaRootPathValid(rootPath))
            {
                RequestBeatorajaTableUrlImportNotification(
                    BeatorajaTableUrlImportNotificationKind.Error,
                    BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaRootPath,
                    BeMusicSeeker.Properties.Resources.Error,
                    "beatoraja Table URL import root path validation notification");
                return;
            }

            IReadOnlyList<string> rawUrls = BeatorajaConfigService.ReadTableUrls(rootPath);
            if (rawUrls.Count == 0)
            {
                RequestBeatorajaTableUrlImportNotification(
                    BeatorajaTableUrlImportNotificationKind.Information,
                    BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_no_urls,
                    BeMusicSeeker.Properties.Resources.Information,
                    "beatoraja Table URL import no URLs notification");
                return;
            }
            IReadOnlyList<BeatorajaTableUrlImportTarget> targets = BuildBeatorajaTableUrlImportTargets(rawUrls);
            if (targets.Count == 0)
            {
                RequestBeatorajaTableUrlImportNotification(
                    BeatorajaTableUrlImportNotificationKind.Information,
                    BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_no_urls,
                    BeMusicSeeker.Properties.Resources.Information,
                    "beatoraja Table URL import no valid URLs notification");
                return;
            }

            if (!RequestBeatorajaTableUrlImportConfirmation())
            {
                return;
            }

            string tablePath = BeatorajaConfigService.GetTablePath(rootPath);
            started = true;
            _ = Task.Run(() => ImportBeatorajaTableUrlsAsync(rootPath, tablePath, targets)).Logging("ImportBeatorajaTableUrlsAsync");
        }
        catch (Exception ex)
        {
            WriteBeatorajaTableUrlImportWarning(ex, "beatoraja_table_url_import_start_failed root=" + (rootPath ?? string.Empty));
            RequestBeatorajaTableUrlImportNotification(
                BeatorajaTableUrlImportNotificationKind.Error,
                BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                "beatoraja Table URL import start failure notification");
        }
        finally
        {
            if (!started)
            {
                Interlocked.Exchange(ref beatorajaTableUrlImportRunning, 0);
            }
        }
    }

    internal bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string rootPath)
    {
        try
        {
            BMSPlaylist playlistStore = getPlaylistStore?.Invoke();
            if (playlistStore == null || playlistStore.BMSTables == null || !BeatorajaConfigService.IsBeatorajaRootPathValid(rootPath))
            {
                return false;
            }
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rawUrl in BeatorajaConfigService.ReadTableUrls(rootPath))
            {
                if (string.IsNullOrWhiteSpace(rawUrl) || !Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri uri) || !seenUrls.Add(uri.AbsoluteUri))
                {
                    continue;
                }
                if (FindBMSTableByExactConfigTableUrl(rawUrl) == null)
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            WriteBeatorajaTableUrlImportWarning(ex, "beatoraja_table_url_import_guide_check_failed root=" + (rootPath ?? string.Empty));
        }
        return false;
    }

    private async Task ImportBeatorajaTableUrlsAsync(string rootPath, string tablePath, IReadOnlyList<BeatorajaTableUrlImportTarget> targets)
    {
        const int BeatorajaTableUrlImportPostProgressStepCount = 4;
        List<BeatorajaTableUrlImportOutcome> outcomes = [];
        List<BMSTable> orderedImportedTables = [];
        int completedCount = 0;
        int totalCount = targets?.Count ?? 0;
        int progressTotalCount = totalCount + BeatorajaTableUrlImportPostProgressStepCount;
        int postProgressCompletedCount = totalCount;
        var totalStopwatch = Stopwatch.StartNew();
        BMSPlaylist tables = GetPlaylistStore();
        using BMSPlaylist.OperationNotificationScope notificationScope = BMSPlaylist.BeginOperationNotificationScope();
        BeginPlaylistSyncProgressOperation();
        try
        {
            UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_check_urls);
            List<BMSTable> rawUrlChangedTables = [];
            List<BeatorajaTableUrlImportWorkItem> loadItems = [];
            foreach (BeatorajaTableUrlImportTarget target in targets ?? [])
            {
                var item = new BeatorajaTableUrlImportWorkItem(target);
                if (target == null)
                {
                    completedCount++;
                    UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_check_urls);
                    continue;
                }
                if (target.Uri == null)
                {
                    outcomes.Add(BeatorajaTableUrlImportOutcome.Failed(target.RawUrl, target.Exception ?? new ArgumentException(BeMusicSeeker.Properties.Resources.Error_URIMustBeAbsolute)));
                    completedCount++;
                    UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_check_urls);
                    continue;
                }
                BMSTable existingTable = FindBMSTableByConfigTableUrl(target.RawUrl, target.Uri);
                if (existingTable != null)
                {
                    if (ApplyBeatorajaTableUrlImportRawUrl(existingTable, target.RawUrl, target.Uri))
                    {
                        rawUrlChangedTables.Add(existingTable);
                    }
                    outcomes.Add(BeatorajaTableUrlImportOutcome.Existing(target.Uri, existingTable.name));
                    orderedImportedTables.Add(existingTable);
                    completedCount++;
                    UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, target.Uri, existingTable.name, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_check_urls);
                    continue;
                }
                loadItems.Add(item);
            }
            if (rawUrlChangedTables.Count > 0)
            {
                tables.CommitBMSTableHeadersToDB(rawUrlChangedTables);
                tables.QueueBeatorajaBmtExportForTables(rawUrlChangedTables, "beatoraja_table_url_import_raw_url_changed");
            }

            if (loadItems.Count > 0)
            {
                int completedBeforeLoad = completedCount;
                List<BMSPlaylist.PlaylistExternalTableLoadResult> loadResults = await tables.LoadExternalTableSnapshotsAsync(
                    loadItems.Select(item => item.SourceTable),
                    inheritLocalTableProperties: false,
                    snapshot => UpdateBeatorajaTableUrlImportProgress(
                        completedBeforeLoad + Math.Max(0, snapshot?.CompletedTableCount ?? 0),
                        progressTotalCount,
                        snapshot?.CurrentUri,
                        snapshot?.CurrentTableName ?? string.Empty,
                        BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_load_tables),
                    "beatoraja_table_url_import",
                    CancellationToken.None,
                    schedulePlaylistUrlCompletionRefresh: false).ConfigureAwait(false);
                var itemBySourceTable = loadItems.ToDictionary(item => item.SourceTable);
                completedCount = totalCount;
                if (loadResults.Any(result => result?.Succeeded != true))
                {
                    UpdateBeatorajaTableUrlImportProgress(totalCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_restore_bmt);
                }
                foreach (BMSPlaylist.PlaylistExternalTableLoadResult loadResult in loadResults)
                {
                    if (loadResult == null || !itemBySourceTable.TryGetValue(loadResult.SourceTable, out BeatorajaTableUrlImportWorkItem item))
                    {
                        continue;
                    }
                    if (loadResult.Succeeded)
                    {
                        item.LoadedTable = loadResult.ExternalTable;
                        continue;
                    }
                    item.ExternalImportException = loadResult.Exception;
                    WriteBeatorajaTableUrlImportWarning(loadResult.Exception, "beatoraja_table_url_external_import_failed uri=" + item.Target.Uri.AbsoluteUri);
                    try
                    {
                        item.LoadedTable = BeatorajaBmtTableImportService.LoadCachedTable(tablePath, item.Target.RawUrl);
                        item.RestoredFromBmt = true;
                    }
                    catch (Exception restoreException)
                    {
                        Exception failure = new InvalidOperationException(
                            string.Format(BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_failed_with_bmt_restore_format, restoreException.Message),
                            item.ExternalImportException ?? restoreException);
                        WriteBeatorajaTableUrlImportWarning(restoreException, "beatoraja_table_url_bmt_restore_failed uri=" + item.Target.Uri.AbsoluteUri);
                        item.Failure = failure;
                        outcomes.Add(BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, failure));
                        RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Target.Uri, failure));
                        UpdateBeatorajaTableUrlImportProgress(totalCount, progressTotalCount, item.Target.Uri, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_restore_bmt);
                    }
                }

                List<BeatorajaTableUrlImportWorkItem> registrationItems = [.. loadItems
                    .Where(item => item.LoadedTable != null && item.Failure == null)];
                foreach (BeatorajaTableUrlImportWorkItem item in registrationItems.Where(item => string.IsNullOrWhiteSpace(item.LoadedTable.Output_dir)))
                {
                    item.Failure = new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_OutputDirNameEmpty);
                    outcomes.Add(BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, item.Failure));
                    RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Target.Uri, item.Failure));
                    UpdateBeatorajaTableUrlImportProgress(totalCount, progressTotalCount, item.Target.Uri, item.LoadedTable.name, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_restore_bmt);
                }
                registrationItems = [.. registrationItems.Where(item => item.Failure == null)];
                if (registrationItems.Count > 0)
                {
                    bool registered = false;
                    try
                    {
                        UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_register_playlists);
                        await tables.RegistrateExternalTablesAsync(
                            registrationItems.Select(item => item.LoadedTable),
                            renameDuplicateName: true,
                            "beatoraja_table_url_import",
                            CancellationToken.None).ConfigureAwait(false);
                        registered = true;
                        postProgressCompletedCount++;
                    }
                    catch (Exception ex)
                    {
                        WriteBeatorajaTableUrlImportWarning(ex, "beatoraja_table_url_import_batch_registration_failed");
                        foreach (BeatorajaTableUrlImportWorkItem item in registrationItems)
                        {
                            outcomes.Add(BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, ex));
                            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Target.Uri, ex));
                        }
                        postProgressCompletedCount += 2;
                        UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_apply_bmt_sort);
                    }
                    if (registered)
                    {
                        foreach (BeatorajaTableUrlImportWorkItem item in registrationItems)
                        {
                            BMSTable importedTable = item.LoadedTable;
                            orderedImportedTables.Add(importedTable);
                            RecordPlaylistSyncResult(CreateBeatorajaTableUrlImportSyncStatus(item, importedTable));
                        }
                        Exception referenceUpdateException = null;
                        try
                        {
                            UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_update_references);
                            CompleteImportedPlaylistRegistrations(
                                [.. registrationItems.Select(item => item.LoadedTable)],
                                "beatoraja_table_url_import");
                            postProgressCompletedCount++;
                        }
                        catch (Exception ex)
                        {
                            referenceUpdateException = ex;
                            WriteBeatorajaTableUrlImportWarning(ex, "beatoraja_table_url_import_reference_update_failed");
                            postProgressCompletedCount++;
                        }
                        foreach (BeatorajaTableUrlImportWorkItem item in registrationItems)
                        {
                            BMSTable importedTable = item.LoadedTable;
                            outcomes.Add(item.RestoredFromBmt
                                ? BeatorajaTableUrlImportOutcome.RestoredFromBmt(item.Target.Uri, importedTable.name)
                                : BeatorajaTableUrlImportOutcome.Imported(item.Target.Uri, importedTable.name));
                            if (referenceUpdateException != null)
                            {
                                outcomes.Add(BeatorajaTableUrlImportOutcome.Warning(item.Target.Uri, importedTable.name, referenceUpdateException));
                            }
                        }
                    }
                }
                else
                {
                    postProgressCompletedCount += 2;
                }
            }
            else
            {
                postProgressCompletedCount += 2;
            }

            UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_apply_bmt_sort);
            ApplyImportedTablesToBmtFront(orderedImportedTables);
            postProgressCompletedCount++;
            UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_finish);
            UpdateBeatorajaTableUrlImportProgress(progressTotalCount, progressTotalCount, null, string.Empty, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_phase_finish);
        }
        finally
        {
            totalStopwatch.Stop();
            WriteBeatorajaTableUrlImportInfo("beatoraja_table_url_import completed targetCount=" + totalCount + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
            EndPlaylistSyncProgressOperation();
            PlaylistImportNotificationsFlushRequested?.Invoke(
                this,
                new PlaylistImportNotificationsFlushRequestedEventArgs(
                    notificationScope,
                    "beatoraja Table URL import notification"));
            Interlocked.Exchange(ref beatorajaTableUrlImportRunning, 0);
        }
        (BeatorajaTableUrlImportSummaryReady
            ?? throw new InvalidOperationException("beatoraja Table URL import summary routing is not configured."))
            (this, new BeatorajaTableUrlImportSummaryReadyEventArgs(
                new BeatorajaTableUrlImportSummary(outcomes)));
    }


    private static PlaylistSyncAttemptResult CreateBeatorajaTableUrlImportSyncStatus(BeatorajaTableUrlImportWorkItem item, BMSTable importedTable)
    {
        if (item?.RestoredFromBmt == true && item.ExternalImportException != null)
        {
            return PlaylistSyncAttemptResult.CreateFailure(null, importedTable, item.Target?.Uri, item.ExternalImportException);
        }
        return PlaylistSyncAttemptResult.CreateSuccess(importedTable, importedTable, item?.Target?.Uri, updated: false);
    }

    private bool RequestBeatorajaTableUrlImportConfirmation()
    {
        BeatorajaTableUrlImportConfirmationRequestedEventArgs request = new();
        (BeatorajaTableUrlImportConfirmationRequested
            ?? throw new InvalidOperationException("beatoraja Table URL import confirmation routing is not configured."))
            (this, request);
        return request.Confirmed;
    }

    private void RequestBeatorajaTableUrlImportNotification(
        BeatorajaTableUrlImportNotificationKind kind,
        string message,
        string caption,
        string routeName)
    {
        (BeatorajaTableUrlImportNotificationRequested
            ?? throw new InvalidOperationException("beatoraja Table URL import notification routing is not configured."))
            (this, new BeatorajaTableUrlImportNotificationRequestedEventArgs(kind, message, caption, routeName));
    }

    private void WriteBeatorajaTableUrlImportWarning(Exception exception, string message)
    {
        (beatorajaTableUrlImportWarningLog
            ?? throw new InvalidOperationException("beatoraja Table URL import warning logging is not configured."))
            (exception, message ?? string.Empty);
    }

    private void WriteBeatorajaTableUrlImportInfo(string message)
    {
        (beatorajaTableUrlImportInfoLog
            ?? throw new InvalidOperationException("beatoraja Table URL import info logging is not configured."))
            (message ?? string.Empty);
    }

    private void UpdateBeatorajaTableUrlImportProgress(int completedCount, int totalCount, Uri currentUri, string currentTableName, string phaseText = null)
    {
        string detail = BuildBeatorajaTableUrlImportProgressDetail(phaseText, currentTableName);
        ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            IsActive = totalCount > 0,
            TotalTableCount = Math.Max(totalCount, 0),
            CompletedTableCount = Math.Max(0, completedCount),
            CurrentTableName = detail,
            CurrentUri = currentUri,
            LabelFormat = BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_label_format,
            SingleLabel = BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_progress_single_label
        });
    }

    private static string BuildBeatorajaTableUrlImportProgressDetail(string phaseText, string currentTableName)
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

    private bool ApplyBeatorajaTableUrlImportRawUrl(BMSTable table, string rawUrl, Uri uri)
    {
        if (table == null || string.IsNullOrWhiteSpace(rawUrl) || uri == null || !uri.IsAbsoluteUri)
        {
            return false;
        }
        using (table.ReaderWriterLock.GetWriterGuard())
        {
            bool changed = false;
            if (IsSameAbsoluteUri(table.Page_url, uri))
            {
                changed = !string.Equals(table.page_url, rawUrl, StringComparison.Ordinal);
                table.Page_url = uri;
            }
            else if (IsSameAbsoluteUri(table.GetAbsoluteHeaderUrl(), uri))
            {
                changed = table.Page_url != null || !string.Equals(table.header_url, rawUrl, StringComparison.Ordinal);
                table.Page_url = null;
                table.Header_url = uri;
            }
            return changed;
        }
    }

    private BMSTable FindBMSTableByConfigTableUrl(string rawUrl, Uri uri)
    {
        BMSPlaylist tables = getPlaylistStore?.Invoke();
        if (uri == null || !uri.IsAbsoluteUri || tables == null)
        {
            return null;
        }
        tables.AcquireReaderLockBMSTables();
        try
        {
            IEnumerable<BMSTable> tableSnapshot = tables.BMSTables ?? Enumerable.Empty<BMSTable>();
            return tableSnapshot.FirstOrDefault(table => HasSamePersistedTableUrl(table, rawUrl))
                ?? tableSnapshot.FirstOrDefault(table => IsSameAbsoluteUri(table?.Page_url, uri) || IsSameAbsoluteUri(table?.GetAbsoluteHeaderUrl(), uri));
        }
        finally
        {
            tables.FreeReaderLockBMSTables();
        }
    }

    private BMSTable FindBMSTableByExactConfigTableUrl(string rawUrl)
    {
        BMSPlaylist tables = getPlaylistStore?.Invoke();
        if (string.IsNullOrWhiteSpace(rawUrl) || tables == null)
        {
            return null;
        }
        tables.AcquireReaderLockBMSTables();
        try
        {
            IEnumerable<BMSTable> tableSnapshot = tables.BMSTables ?? Enumerable.Empty<BMSTable>();
            return tableSnapshot.FirstOrDefault(table => HasSamePersistedTableUrl(table, rawUrl));
        }
        finally
        {
            tables.FreeReaderLockBMSTables();
        }
    }

    private static bool HasSamePersistedTableUrl(BMSTable table, string rawUrl)
    {
        if (table == null || string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }
        if (string.Equals(table.page_url, rawUrl, StringComparison.Ordinal))
        {
            return true;
        }
        return table.Header_url != null
            && table.Header_url.IsAbsoluteUri
            && string.Equals(table.header_url, rawUrl, StringComparison.Ordinal);
    }

    private static bool IsSameAbsoluteUri(Uri left, Uri right)
    {
        return left != null
            && right != null
            && left.IsAbsoluteUri
            && right.IsAbsoluteUri
            && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);
    }

    private static IReadOnlyList<BeatorajaTableUrlImportTarget> BuildBeatorajaTableUrlImportTargets(IEnumerable<string> rawUrls)
    {
        var targets = new List<BeatorajaTableUrlImportTarget>();
        var seenUrlKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rawUrl in rawUrls ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawUrl))
            {
                continue;
            }
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri uri))
            {
                targets.Add(new BeatorajaTableUrlImportTarget(rawUrl, null, new ArgumentException(BeMusicSeeker.Properties.Resources.Error_URIMustBeAbsolute)));
                continue;
            }
            if (!seenUrlKeys.Add(uri.AbsoluteUri))
            {
                continue;
            }
            targets.Add(new BeatorajaTableUrlImportTarget(rawUrl, uri, null));
        }
        return targets;
    }

    private sealed class BeatorajaTableUrlImportTarget
    {
        internal BeatorajaTableUrlImportTarget(string rawUrl, Uri uri, Exception exception)
        {
            RawUrl = rawUrl ?? string.Empty;
            Uri = uri;
            Exception = exception;
        }

        internal string RawUrl { get; }

        internal Uri Uri { get; }

        internal Exception Exception { get; }
    }

    private sealed class BeatorajaTableUrlImportWorkItem
    {
        internal BeatorajaTableUrlImportWorkItem(BeatorajaTableUrlImportTarget target)
        {
            Target = target;
            if (target?.Uri != null)
            {
                SourceTable = new BMSTable
                {
                    name = target.RawUrl,
                    Page_url = target.Uri
                };
            }
        }

        internal BeatorajaTableUrlImportTarget Target { get; }

        internal BMSTable SourceTable { get; }

        internal BMSTable LoadedTable { get; set; }

        internal bool RestoredFromBmt { get; set; }

        internal Exception ExternalImportException { get; set; }

        internal Exception Failure { get; set; }
    }
}

internal enum BeatorajaTableUrlImportNotificationKind
{
    Information,
    Warning,
    Error
}

internal sealed class BeatorajaTableUrlImportConfirmationRequestedEventArgs : EventArgs
{
    internal bool Confirmed { get; set; }
}

internal sealed class BeatorajaTableUrlImportNotificationRequestedEventArgs : EventArgs
{
    internal BeatorajaTableUrlImportNotificationRequestedEventArgs(
        BeatorajaTableUrlImportNotificationKind kind,
        string message,
        string caption,
        string routeName)
    {
        Kind = kind;
        Message = message ?? string.Empty;
        Caption = caption ?? string.Empty;
        RouteName = routeName ?? string.Empty;
    }

    internal BeatorajaTableUrlImportNotificationKind Kind { get; }

    internal string Message { get; }

    internal string Caption { get; }

    internal string RouteName { get; }
}

internal sealed class BeatorajaTableUrlImportSummaryReadyEventArgs : EventArgs
{
    internal BeatorajaTableUrlImportSummaryReadyEventArgs(BeatorajaTableUrlImportSummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    internal BeatorajaTableUrlImportSummary Summary { get; }
}
