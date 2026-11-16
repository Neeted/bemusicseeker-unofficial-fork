using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

public sealed partial class PlaylistWorkspaceViewModel
{
    private const string BeatorajaTableUrlImportProgressSource = "beatoraja_table_url_import";

    /// <summary>
    /// 確定rootの取込みを開始し、取得・DB・出力の実終端と、通知・要約の表示の依頼までを含むTaskを返します。表示の完了は待ちません。
    /// 結果は取込みを開始した場合の要約で、確認の取消、開始前の拒否、受付後の早い終了ではnullです。
    /// 実storeがLR2連携時だけ空出力名を対象別に拒否し、スタンドアロンではUnicode実名を保持します。
    /// </summary>
    /// <remarks>
    /// 確認は受付の取得より前に待ちます。受付を取得した後に決まる通知・操作通知・要約は、受付を解放してから
    /// 見出し・重要度ごとにまとめて、プレイリスト作業領域の窓口へ表示を依頼します（Ownerは窓口の解決に任せます）。
    /// 要約は通知の後に続けます。この Task は、終了の判定と最初の依頼までを含み、表示の完了は待ちません。
    /// 表示は任意の通知であり、失敗は診断へ残すだけで取込みの結果を変えません。終了の要求後は表示しません。
    /// </remarks>
    internal async Task<BeatorajaTableUrlImportSummary> StartBeatorajaTableUrlImport(string rootPath, Action onAccepted = null, string confirmationMessage = null)
    {
        Task<BeatorajaTableUrlImportSummary> operation = null;
        BeatorajaTableUrlImportNotice notice = null;
        try
        {
            BMSPlaylist playlistStore = getPlaylistStore();
            BMSLibrary playlistLibrary = getPlaylistLibrary();
            if (playlistStore == null || playlistLibrary == null || playlistStore.BMSTables == null)
            {
                notice = CreateBeatorajaTableUrlImportNotice(
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization)),
                    nameof(BeMusicSeeker.Properties.Resources.Warning),
                    "beatoraja Table URL import initialization notification");
            }
            else if (!BeatorajaConfigService.IsBeatorajaRootPathValid(rootPath))
            {
                notice = CreateBeatorajaTableUrlImportNotice(
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaRootPath)),
                    nameof(BeMusicSeeker.Properties.Resources.Error),
                    "beatoraja Table URL import root path validation notification");
            }
            else if (await ConfirmBeatorajaTableUrlImportAsync(confirmationMessage).ConfigureAwait(true))
            {
                (operation, notice) = TryBeginBeatorajaTableUrlImport(playlistStore, rootPath, onAccepted);
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            WriteBeatorajaTableUrlImportWarning(ex, "beatoraja_table_url_import_start_failed root=" + (rootPath ?? string.Empty));
            notice = CreateBeatorajaTableUrlImportNotice(
                PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                PlaylistOperationNotificationOwner.OperationNotificationText.FromResourceWithDetail(
                    nameof(BeMusicSeeker.Properties.Resources.Msg_failed_load_playlist), ex.Message, detailSeparatorLineBreaks: 1),
                nameof(BeMusicSeeker.Properties.Resources.Error),
                "beatoraja Table URL import start failure notification");
        }
        if (operation != null)
        {
            return await operation.ConfigureAwait(true);
        }
        await noticePresenter.BeginRequest(
            PlaylistOperationNotificationOwner.OperationNotificationReceipt.Create([notice.Notification]),
            notice.RouteName).ConfigureAwait(true);
        return null;
    }

    /// <summary>
    /// 確認の後に受付を取得し、取込みを開始します。開始しなかった場合は、受付をここで解放したうえで、呼出し元が表示する通知を返します。
    /// 表示は受付の解放後に呼出し元が行うため、この処理は表示を待ちません。
    /// </summary>
    private (Task<BeatorajaTableUrlImportSummary> Operation, BeatorajaTableUrlImportNotice Notice) TryBeginBeatorajaTableUrlImport(
        BMSPlaylist playlistStore,
        string rootPath,
        Action onAccepted)
    {
        bool started = false;
        LibraryFileMutationLease admission = null;
        try
        {
            if (!playlistStore.TryEnterPlaylistMutation(out IDisposable accepted))
            {
                return (null, CreateBeatorajaTableUrlImportNotice(
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy)),
                    nameof(BeMusicSeeker.Properties.Resources.Warning), "beatoraja Table URL import Busy"));
            }
            admission = (LibraryFileMutationLease)accepted;
            if (!BeatorajaConfigService.IsBeatorajaRootPathValid(rootPath))
            {
                return (null, CreateBeatorajaTableUrlImportNotice(
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Error,
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Error_InvalidBeatorajaRootPath)),
                    nameof(BeMusicSeeker.Properties.Resources.Error),
                    "beatoraja Table URL import final root validation"));
            }
            IReadOnlyList<string> rawUrls = BeatorajaConfigService.ReadTableUrls(rootPath);
            if (rawUrls.Count == 0)
            {
                return (null, CreateBeatorajaTableUrlImportNotice(
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_no_urls)),
                    nameof(BeMusicSeeker.Properties.Resources.Information),
                    "beatoraja Table URL import no URLs notification"));
            }
            IReadOnlyList<BeatorajaTableUrlImportTarget> targets = BuildBeatorajaTableUrlImportTargets(rawUrls);
            if (targets.Count == 0)
            {
                return (null, CreateBeatorajaTableUrlImportNotice(
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Information,
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_no_urls)),
                    nameof(BeMusicSeeker.Properties.Resources.Information),
                    "beatoraja Table URL import no valid URLs notification"));
            }

            string tablePath = BeatorajaConfigService.GetTablePath(rootPath);
            onAccepted?.Invoke();
            Task<BeatorajaTableUrlImportSummary> operation = Task.Run(() => ImportBeatorajaTableUrlsAsync(playlistStore, rootPath, tablePath, targets, admission));
            started = true;
            return (operation, null);
        }
        finally
        {
            if (!started)
            {
                admission?.Dispose();
            }
        }
    }

    internal bool HasUnimportedBeatorajaTableUrlsForBmtOutputGuide(string rootPath)
    {
        try
        {
            BMSPlaylist playlistStore = getPlaylistStore();
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

    /// <summary>受付の下で取込みを行い、受付を解放した後に、確定した操作通知と要約の表示を依頼します。表示の完了は待たず、表示の失敗は取込みの結果を変えません。</summary>
    private async Task<BeatorajaTableUrlImportSummary> ImportBeatorajaTableUrlsAsync(BMSPlaylist tables, string rootPath, string tablePath, IReadOnlyList<BeatorajaTableUrlImportTarget> targets, LibraryFileMutationLease admission)
    {
        var report = new BeatorajaTableUrlImportReport();
        try
        {
            await ImportBeatorajaTableUrlsCoreAsync(tables, rootPath, tablePath, targets, admission, report).ConfigureAwait(false);
        }
        finally
        {
            // 受付・権限・通知の収集は取込みの終端で解放済みです。表示の依頼は受付の外で行い、表示の完了は待ちません。
            BeatorajaTableUrlImportSummary summary = report.Summary;
            await noticePresenter.BeginRequest(
                report.Receipt,
                "beatoraja Table URL import notification",
                summary == null ? null : () => CreateBeatorajaTableUrlImportSummaryRequest(summary)).ConfigureAwait(false);
        }
        return report.Summary;
    }

    private async Task ImportBeatorajaTableUrlsCoreAsync(BMSPlaylist tables, string rootPath, string tablePath, IReadOnlyList<BeatorajaTableUrlImportTarget> targets, LibraryFileMutationLease admission, BeatorajaTableUrlImportReport report)
    {
        const int BeatorajaTableUrlImportPostProgressStepCount = 4;
        // 取得・登録の各段階で確定する結果を要求対象へ結び、通知時に元入力順へ戻します。
        // 同対象の参照更新警告は主結果の直後に保持します。
        var outcomesByTarget = new Dictionary<BeatorajaTableUrlImportTarget, List<BeatorajaTableUrlImportOutcome>>();
        void RecordOutcome(BeatorajaTableUrlImportTarget target, BeatorajaTableUrlImportOutcome outcome)
        {
            if (!outcomesByTarget.TryGetValue(target, out List<BeatorajaTableUrlImportOutcome> results))
            {
                results = [];
                outcomesByTarget.Add(target, results);
            }
            results.Add(outcome);
        }
        var importedTablesByTarget = new Dictionary<BeatorajaTableUrlImportTarget, BMSTable>();
        int completedCount = 0;
        int totalCount = targets?.Count ?? 0;
        int progressTotalCount = totalCount + BeatorajaTableUrlImportPostProgressStepCount;
        int postProgressCompletedCount = totalCount;
        var totalStopwatch = Stopwatch.StartNew();
        // 権限と通知の収集の生成が失敗しても、受付を保持したままにしないよう、生成を受付の解放を保証する try の内側に置きます。
        LibraryFileMutationCapability authority = null;
        PlaylistOperationNotificationOwner.OperationNotificationSession notificationSession = null;
        try
        {
            authority = admission.CreateMutationCapability();
            notificationSession = tables.OperationNotificationOwner.BeginSession();
            notificationSession.WarnStoppedOutputs();
            BeginPlaylistSyncProgressOperation(BeatorajaTableUrlImportProgressSource);
            try
            {
                UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.CheckUrls);
                List<BeatorajaTableUrlImportTarget> pendingRawUrlChanges = [];
                List<BeatorajaTableUrlImportWorkItem> loadItems = [];
                async Task ApplyPendingRawUrlsAsync()
                {
                    List<BMSTable> changed = [];
                    foreach (BeatorajaTableUrlImportTarget target in pendingRawUrlChanges)
                    {
                        BMSTable current = FindBMSTableByConfigTableUrl(target.RawUrl, target.Uri);
                        if (current == null) { continue; }
                        if (ApplyBeatorajaTableUrlImportRawUrl(current, target.RawUrl, target.Uri)) { changed.Add(current); }
                    }
                    if (changed.Count == 0) { return; }
                    await RunWithSavedHeaderPresentationAsync(async () =>
                    {
                        tables.CommitBMSTableHeadersToDB(changed, capability: authority);
                        await tables.BmtOutput.ExportTablesAsync(changed, "beatoraja_table_url_import_raw_url_changed", authority);
                    });
                }
                foreach (BeatorajaTableUrlImportTarget target in targets ?? [])
                {
                    var item = new BeatorajaTableUrlImportWorkItem(target);
                    if (target == null)
                    {
                        completedCount++;
                        UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.CheckUrls);
                        continue;
                    }
                    if (target.Uri == null)
                    {
                        RecordOutcome(target, BeatorajaTableUrlImportOutcome.Failed(target.RawUrl, target.Exception ?? new ArgumentException(BeMusicSeeker.Properties.Resources.Error_URIMustBeAbsolute)));
                        completedCount++;
                        UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.CheckUrls);
                        continue;
                    }
                    BMSTable existingTable = FindBMSTableByConfigTableUrl(target.RawUrl, target.Uri);
                    if (existingTable != null)
                    {
                        pendingRawUrlChanges.Add(target);
                        RecordOutcome(target, BeatorajaTableUrlImportOutcome.Existing(target.Uri, existingTable.name));
                        importedTablesByTarget.Add(target, existingTable);
                        completedCount++;
                        UpdateBeatorajaTableUrlImportProgress(completedCount, progressTotalCount, target.Uri, existingTable.name, PlaylistImportProgressPhase.CheckUrls);
                        continue;
                    }
                    loadItems.Add(item);
                }

                if (loadItems.Count > 0)
                {
                    int completedBeforeLoad = completedCount;
                    List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loadResults = await tables.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(
                        loadItems.Select(item => item.SourceTable),
                        inheritLocalTableProperties: false,
                        snapshot => UpdateBeatorajaTableUrlImportProgress(
                            completedBeforeLoad + Math.Max(0, snapshot?.CompletedTableCount ?? 0),
                            progressTotalCount,
                            snapshot?.CurrentUri,
                            snapshot?.CurrentTableName ?? string.Empty,
                            PlaylistImportProgressPhase.LoadTables),
                        "beatoraja_table_url_import",
                        CancellationToken.None,
                        schedulePlaylistUrlCompletionRefresh: false, initialImport: true).ConfigureAwait(false);
                    var itemBySourceTable = loadItems.ToDictionary(item => item.SourceTable);
                    completedCount = totalCount;
                    if (loadResults.Any(result => result?.Succeeded != true))
                    {
                        UpdateBeatorajaTableUrlImportProgress(totalCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.RestoreBmt);
                    }
                    foreach (PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult loadResult in loadResults)
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
                        if (PlaylistJsonDate.IsDateFailure(loadResult.Exception))
                        {
                            item.Failure = loadResult.Exception;
                            RecordOutcome(item.Target, BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, item.Failure));
                            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateReportedExternalFailure(null, item.Target.Uri, item.Failure));
                            continue;
                        }
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
                            RecordOutcome(item.Target, BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, failure));
                            RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Target.Uri, failure));
                            UpdateBeatorajaTableUrlImportProgress(totalCount, progressTotalCount, item.Target.Uri, string.Empty, PlaylistImportProgressPhase.RestoreBmt);
                        }
                    }

                    List<BeatorajaTableUrlImportWorkItem> registrationItems = [.. loadItems
                        .Where(item => item.LoadedTable != null && item.Failure == null)];
                    bool operationModeLR2DB = tables.GetCustomFolderOutputSettings().OperationModeLR2DB;
                    foreach (BeatorajaTableUrlImportWorkItem item in registrationItems.Where(item => operationModeLR2DB && string.IsNullOrWhiteSpace(item.LoadedTable.Output_dir)))
                    {
                        item.Failure = new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_OutputDirNameEmpty);
                        RecordOutcome(item.Target, BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, item.Failure));
                        RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Target.Uri, item.Failure));
                        UpdateBeatorajaTableUrlImportProgress(totalCount, progressTotalCount, item.Target.Uri, item.LoadedTable.name, PlaylistImportProgressPhase.RestoreBmt);
                    }
                    registrationItems = [.. registrationItems.Where(item => item.Failure == null)];
                    await ApplyPendingRawUrlsAsync();
                    if (registrationItems.Count > 0)
                    {
                        bool registered = false;
                        try
                        {
                            UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.RegisterPlaylists);
                            await tables.ExternalSyncOwner.RegistrateExternalTablesAsync(
                            registrationItems.Select(item => item.LoadedTable),
                            renameDuplicateName: true,
                            "beatoraja_table_url_import",
                            CancellationToken.None, authority, applyNewTableDefaults: true).ConfigureAwait(false);
                            registered = true;
                            postProgressCompletedCount++;
                        }
                        catch (Exception ex)
                        {
                            WriteBeatorajaTableUrlImportWarning(ex, "beatoraja_table_url_import_batch_registration_failed");
                            foreach (BeatorajaTableUrlImportWorkItem item in registrationItems)
                            {
                                RecordOutcome(item.Target, BeatorajaTableUrlImportOutcome.Failed(item.Target.Uri, ex));
                                RecordPlaylistSyncResult(PlaylistSyncAttemptResult.CreateFailure(null, item.Target.Uri, ex));
                            }
                            postProgressCompletedCount += 2;
                            UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.ApplyBmtSort);
                        }
                        if (registered)
                        {
                            foreach (BeatorajaTableUrlImportWorkItem item in registrationItems)
                            {
                                BMSTable importedTable = item.LoadedTable;
                                importedTablesByTarget.Add(item.Target, importedTable);
                                RecordPlaylistSyncResult(CreateBeatorajaTableUrlImportSyncStatus(item, importedTable));
                            }
                            Exception referenceUpdateException = null;
                            try
                            {
                                UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.UpdateReferences);
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
                                RecordOutcome(item.Target, item.RestoredFromBmt
                                    ? BeatorajaTableUrlImportOutcome.RestoredFromBmt(item.Target.Uri, importedTable.name)
                                    : BeatorajaTableUrlImportOutcome.Imported(item.Target.Uri, importedTable.name));
                                if (referenceUpdateException != null)
                                {
                                    RecordOutcome(item.Target, BeatorajaTableUrlImportOutcome.Warning(item.Target.Uri, importedTable.name, referenceUpdateException));
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
                    await ApplyPendingRawUrlsAsync();
                    postProgressCompletedCount += 2;
                }

                UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.ApplyBmtSort);
                List<BMSTable> orderedImportedTables = [.. (targets ?? [])
                    .Where(target => target != null && importedTablesByTarget.ContainsKey(target))
                    .Select(target => importedTablesByTarget[target])];
                await ApplyImportedTablesToBmtFrontAsync(orderedImportedTables, authority);
                postProgressCompletedCount++;
                UpdateBeatorajaTableUrlImportProgress(postProgressCompletedCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.Finish);
                UpdateBeatorajaTableUrlImportProgress(progressTotalCount, progressTotalCount, null, string.Empty, PlaylistImportProgressPhase.Finish);
            }
            finally
            {
                totalStopwatch.Stop();
                WriteBeatorajaTableUrlImportInfo("beatoraja_table_url_import completed targetCount=" + totalCount + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
                EndPlaylistSyncProgressOperation(BeatorajaTableUrlImportProgressSource);
                report.Receipt = notificationSession.TakeReceipt();
            }
            report.Summary = new BeatorajaTableUrlImportSummary((targets ?? [])
                .Where(target => target != null)
                .SelectMany(target => outcomesByTarget.TryGetValue(target, out List<BeatorajaTableUrlImportOutcome> results) ? results : []));
        }
        finally
        {
            admission?.Dispose();
            notificationSession?.Dispose();
            authority?.Dispose();
        }
    }


    private static PlaylistSyncAttemptResult CreateBeatorajaTableUrlImportSyncStatus(BeatorajaTableUrlImportWorkItem item, BMSTable importedTable)
    {
        if (item?.RestoredFromBmt == true && item.ExternalImportException != null)
        {
            return PlaylistSyncAttemptResult.CreateFailure(null, importedTable, item.Target?.Uri, item.ExternalImportException);
        }
        return PlaylistSyncAttemptResult.CreateSuccess(importedTable, importedTable, item?.Target?.Uri, updated: false);
    }

    /// <summary>取込みの確認を窓口で表示し、承認だけをtrueにします。表示の失敗は承認にも取消にも読み替えず、例外にして開始しません。</summary>
    private async Task<bool> ConfirmBeatorajaTableUrlImportAsync(string confirmationMessage)
    {
        UiDialogResult result = await playlistWorkspaceDialogService.ConfirmAsync(new UiConfirmationRequest(
            confirmationMessage ?? BeMusicSeeker.Properties.Resources.Confirm_import_beatoraja_table_urls,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question)).ConfigureAwait(true);
        return ToPlaylistUrlConfirmationDecision(result, "beatoraja Table URL import confirmation");
    }

    private static BeatorajaTableUrlImportNotice CreateBeatorajaTableUrlImportNotice(
        PlaylistOperationNotificationOwner.OperationNotificationSeverity severity,
        PlaylistOperationNotificationOwner.OperationNotificationText text,
        string captionResourceName,
        string routeName)
        => new(new PlaylistOperationNotificationOwner.OperationNotification(text, captionResourceName, severity), routeName);

    /// <summary>取込みの要約を、表示の依頼の時点の言語で整形した表示要求にします。</summary>
    private static UiMessageRequest CreateBeatorajaTableUrlImportSummaryRequest(BeatorajaTableUrlImportSummary summary)
        => new(
            summary.FormatMessage(),
            BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_title,
            UiDialogButton.OK,
            summary.HasFailureOrWarning ? UiDialogIcon.Exclamation : UiDialogIcon.Information,
            UiDialogDefaultResult.OK);

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

    private void UpdateBeatorajaTableUrlImportProgress(int completedCount, int totalCount, Uri currentUri, string currentTableName, PlaylistImportProgressPhase phase = PlaylistImportProgressPhase.None)
    {
        ReportPlaylistSyncProgress(new PlaylistSyncProgressSnapshot
        {
            Source = BeatorajaTableUrlImportProgressSource,
            IsActive = totalCount > 0,
            TotalTableCount = Math.Max(totalCount, 0),
            CompletedTableCount = Math.Max(0, completedCount),
            CurrentTableName = currentTableName ?? string.Empty,
            ImportPhase = phase,
            CurrentUri = currentUri,
            Kind = PlaylistSyncProgressKind.BeatorajaTableUrlImport
        });
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
        BMSPlaylist tables = getPlaylistStore();
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
        BMSPlaylist tables = getPlaylistStore();
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

/// <summary>取込みの途中で確定した、受付の解放後に表示を依頼する通知の事実です。</summary>
internal sealed record BeatorajaTableUrlImportNotice(
    PlaylistOperationNotificationOwner.OperationNotification Notification,
    string RouteName);

/// <summary>受付の下で確定した操作通知と要約を、受付の解放後の表示へ渡します。</summary>
internal sealed class BeatorajaTableUrlImportReport
{
    internal PlaylistOperationNotificationOwner.OperationNotificationReceipt Receipt { get; set; }

    internal BeatorajaTableUrlImportSummary Summary { get; set; }
}
