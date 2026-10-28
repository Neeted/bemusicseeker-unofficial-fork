using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private const string ExternalPlaylistImportProgressSource = "external_playlist_import";

    internal event EventHandler<ExternalPlaylistImportQueueSummaryReadyEventArgs> ExternalPlaylistImportQueueSummaryReady;

    internal event EventHandler<ExternalPlaylistImportSummaryRefreshFailedEventArgs> ExternalPlaylistImportSummaryRefreshFailed;

    /// <summary>全入力元の選択を一要求として受け付け、登録済みURLを通信前に除きます。拒否した要求を予約しません。</summary>
    /// <param name="selectedUris">画面で検証済みの絶対URI。初出の選択順を維持して処理します。</param>
    /// <returns>実行後の成否とは別の受付結果と、開始・同URL除外の件数。</returns>
    /// <exception cref="ArgumentNullException">URI一覧がnullの場合。</exception>
    /// <exception cref="ArgumentException">nullまたは相対URIが含まれる場合。</exception>
    internal PlaylistImportStartResult TryStartPlaylistImport(IReadOnlyList<Uri> selectedUris)
    {
        ArgumentNullException.ThrowIfNull(selectedUris);
        List<Uri> batch = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Uri uri in selectedUris)
        {
            if (uri?.IsAbsoluteUri != true)
            {
                throw new ArgumentException(BeMusicSeeker.Properties.Resources.Error_URIMustBeAbsolute, nameof(selectedUris));
            }
            if (seen.Add(uri.AbsoluteUri))
            {
                batch.Add(uri);
            }
        }
        if (batch.Count == 0)
        {
            return new PlaylistImportStartResult(PlaylistImportStartStatus.NoTargets);
        }

        BMSPlaylist tables = getPlaylistStore();
        if (tables != null && (tables.IsShutdownRequested || tables.IsPlaylistMutationAdmissionClosed))
        {
            return new PlaylistImportStartResult(PlaylistImportStartStatus.ShuttingDown);
        }
        if (tables?.StartupReadiness.IsRequiredPlaylistReady != true)
        {
            return new PlaylistImportStartResult(PlaylistImportStartStatus.NotReady);
        }
        if (!tables.TryEnterPlaylistMutation(out IDisposable admission))
        {
            return new PlaylistImportStartResult(
                tables.IsShutdownRequested || tables.IsPlaylistMutationAdmissionClosed
                    ? PlaylistImportStartStatus.ShuttingDown
                    : PlaylistImportStartStatus.Busy);
        }

        bool handedOff = false;
        try
        {
            // 画面を開いた後の登録も反映し、同じPを保持する既存の一括処理へ引き渡します。
            var registeredUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (PlaylistImportExistingTable table in CapturePlaylistImportExistingTables(tables))
            {
                if (table.PageUrl?.IsAbsoluteUri == true)
                {
                    registeredUrls.TryAdd(table.PageUrl.AbsoluteUri, table.Name);
                }
                if (table.HeaderUrl?.IsAbsoluteUri == true)
                {
                    registeredUrls.TryAdd(table.HeaderUrl.AbsoluteUri, table.Name);
                }
            }
            List<Uri> targets = [];
            List<ExternalPlaylistImportOutcome> skipped = [];
            foreach (Uri uri in batch)
            {
                if (registeredUrls.TryGetValue(uri.AbsoluteUri, out string tableName))
                {
                    skipped.Add(ExternalPlaylistImportOutcome.SkippedDuplicateUrl(uri, tableName));
                }
                else
                {
                    targets.Add(uri);
                }
            }
            if (targets.Count == 0)
            {
                return new PlaylistImportStartResult(PlaylistImportStartStatus.NoTargets,
                    SkippedDuplicateUrlCount: skipped.Count);
            }

            handedOff = true;
            ImportExternalPlaylistsAsync(tables, targets, skipped, admission).ObserveFault("ImportExternalPlaylistsAsync");
            return new PlaylistImportStartResult(PlaylistImportStartStatus.Started, targets.Count, skipped.Count);
        }
        finally
        {
            if (!handedOff)
            {
                admission.Dispose();
            }
        }
    }

    /// <summary>インポートの事前表示用に、既存表の名前とURLだけを短い読取り区間で捕捉します。</summary>
    /// <returns>可変の表を共有しない値一覧。プレイリスト未接続なら空。</returns>
    internal IReadOnlyList<PlaylistImportExistingTable> CapturePlaylistImportExistingTables()
        => CapturePlaylistImportExistingTables(getPlaylistStore());

    private static IReadOnlyList<PlaylistImportExistingTable> CapturePlaylistImportExistingTables(BMSPlaylist tables)
    {
        if (tables == null)
        {
            return [];
        }
        List<PlaylistImportExistingTable> snapshot = [];
        tables.AcquireReaderLockBMSTables();
        try
        {
            foreach (BMSTable table in tables.BMSTables ?? Enumerable.Empty<BMSTable>())
            {
                using (table.ReaderWriterLock.GetReaderGuard())
                {
                    snapshot.Add(new PlaylistImportExistingTable(table.name ?? string.Empty,
                        table.Page_url, table.GetAbsoluteHeaderUrl()));
                }
            }
        }
        finally
        {
            tables.FreeReaderLockBMSTables();
        }
        return snapshot;
    }

    private async Task ImportExternalPlaylistsAsync(
        BMSPlaylist tables,
        IReadOnlyList<Uri> batch,
        IReadOnlyList<ExternalPlaylistImportOutcome> skipped,
        IDisposable admission)
    {
        const int ExternalPlaylistImportPostProgressStepCount = 4;
        List<ExternalPlaylistImportOutcome> outcomes = [.. skipped];
        StartupReadinessCoordinator readiness = tables.StartupReadiness;
        CancellationToken cancellationToken = readiness.ShutdownToken;
        LibraryFileMutationCapability authority = null;
        bool progressStarted = false;
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = null;
        try
        {
            authority = tables.CreatePlaylistMutationCapability(admission);
            ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
            if (tables.IsShutdownRequested)
            {
                return;
            }
            notificationSession = tables.OperationNotificationOwner.BeginSession();
            BeginPlaylistSyncProgressOperation(ExternalPlaylistImportProgressSource);
            progressStarted = true;
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
                    return;
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
                    schedulePlaylistUrlCompletionRefresh: false, initialImport: true).ConfigureAwait(false);
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
                bool operationModeLR2DB = tables.GetCustomFolderOutputSettings().OperationModeLR2DB;
                List<ExternalPlaylistImportWorkItem> registrationItems = PrepareExternalPlaylistImportRegistrationItems(loadItems, outcomes, operationModeLR2DB);
                postProgressCompletedCount++;

                List<ExternalPlaylistImportWorkItem> registeredItems = [];
                Exception registrationFailure = null;
                if (registrationItems.Count > 0)
                {
                    try
                    {
                        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
                        UpdateExternalPlaylistImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty,
                            BeMusicSeeker.Properties.Resources.Playlist_import_progress_phase_register_playlists);
                        await tables.ExternalSyncOwner.RegistrateExternalTablesAsync(
                            registrationItems.Select(item => item.LoadedTable), renameDuplicateName: false,
                            "external_playlist_import", cancellationToken, authority).ConfigureAwait(false);
                        registeredItems = registrationItems;
                    }
                    catch (OperationCanceledException) when (IsExternalPlaylistImportShutdownRequested(tables, cancellationToken)) { throw; }
                    catch (Exception failure)
                    {
                        registrationFailure = failure;
                        WriteExternalPlaylistImportWarning(failure, "external_playlist_import_batch_registration_failed");
                        registeredItems = [.. registrationItems.Where(item => tables.ContainsBMSTable(item.LoadedTable))];
                        foreach (ExternalPlaylistImportWorkItem item in registrationItems)
                        {
                            outcomes.Add(ExternalPlaylistImportOutcome.Failed(item.Uri, failure));
                            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Uri, failure));
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
                    if (referenceUpdateException == null && registrationFailure == null)
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
                        if (registrationFailure != null) { continue; }
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
            try
            {
                if (progressStarted && !readiness.IsShutdownRequested && !tables.IsShutdownRequested)
                {
                    EndPlaylistSyncProgressOperation(ExternalPlaylistImportProgressSource);
                }
                if (notificationSession != null
                    && !readiness.IsShutdownRequested
                    && !readiness.IsRequiredPlaylistReadinessFaulted
                    && !tables.IsShutdownRequested)
                {
                    ExternalPlaylistImportQueueSummaryReady?.Invoke(
                        this,
                        new ExternalPlaylistImportQueueSummaryReadyEventArgs(
                            new ExternalPlaylistImportQueueSummary(outcomes),
                            notificationSession.TakeReceipt()));
                }
            }
            finally
            {
                notificationSession?.Dispose();
                authority?.Dispose();
                admission?.Dispose();

            }
        }
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

    /// <summary>実storeから捕捉したLR2モードでだけ空出力名を対象別に除外し、残る候補の重複名を検査します。スタンドアロンのUnicode実名は出力名で除外しません。</summary>
    /// <param name="operationModeLR2DB">この候補準備で固定する実storeのLR2連携モード。</param>
    private List<ExternalPlaylistImportWorkItem> PrepareExternalPlaylistImportRegistrationItems(IReadOnlyList<ExternalPlaylistImportWorkItem> loadItems, List<ExternalPlaylistImportOutcome> outcomes, bool operationModeLR2DB)
    {
        var reservedNames = new HashSet<string>(GetExternalPlaylistImportExistingNamesSnapshot(), StringComparer.Ordinal);
        List<ExternalPlaylistImportWorkItem> registrationItems = [];
        foreach (ExternalPlaylistImportWorkItem item in loadItems ?? [])
        {
            if (item?.LoadedTable == null || item.Failure != null)
            {
                continue;
            }
            if (operationModeLR2DB && string.IsNullOrWhiteSpace(item.LoadedTable.Output_dir))
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
            Source = ExternalPlaylistImportProgressSource,
            IsActive = totalCount > 0,
            TotalTableCount = Math.Max(totalCount, 0),
            CompletedTableCount = completedCount,
            CurrentTableName = detail,
            CurrentUri = currentUri,
            LabelFormat = BeMusicSeeker.Properties.Resources.Playlist_import_progress_label_format,
            SingleLabel = BeMusicSeeker.Properties.Resources.Playlist_import_progress_single_label
        });
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
        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
        List<BMSTable> tableList = [.. (importedTables ?? [])
            .Where(table => table != null)
            .Where(table => tables.ContainsBMSTable(table))];
        if (tableList.Count == 0)
        {
            return false;
        }
        ThrowIfExternalPlaylistImportShutdownRequested(tables, cancellationToken);
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

}

/// <summary>インポートの集計と処理中の通知事実を、UI応答を待たない一つの完了通知へまとめます。</summary>
internal sealed class ExternalPlaylistImportQueueSummaryReadyEventArgs : EventArgs
{
    /// <summary>確定した集計と変更不能な通知レシートを接続します。</summary>
    /// <param name="summary">成功・同URL・同名・失敗の集計。</param>
    /// <param name="receipt">必要出力などが集めた通知事実。</param>
    internal ExternalPlaylistImportQueueSummaryReadyEventArgs(
        ExternalPlaylistImportQueueSummary summary,
        PlaylistOperationNotificationOwner.OperationNotificationReceipt receipt)
    {
        Summary = summary;
        Receipt = receipt;
    }

    /// <summary>読み込みと登録の確定した結果を取得します。</summary>
    internal ExternalPlaylistImportQueueSummary Summary { get; }

    /// <summary>集計と同じ非同期表示境界で提示する通知事実を取得します。</summary>
    internal PlaylistOperationNotificationOwner.OperationNotificationReceipt Receipt { get; }
}

internal sealed class ExternalPlaylistImportSummaryRefreshFailedEventArgs : EventArgs
{
    internal ExternalPlaylistImportSummaryRefreshFailedEventArgs(Exception exception)
    {
        Exception = exception;
    }

    internal Exception Exception { get; }
}
