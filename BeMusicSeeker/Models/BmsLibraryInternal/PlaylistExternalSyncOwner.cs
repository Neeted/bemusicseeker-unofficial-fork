using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Ribbit.Net;
using Sgml;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 外部プレイリストの HTTP 取得と URI 解決を所有します。
/// </summary>
internal sealed class PlaylistExternalSyncOwner
{
    private const int ExternalPlaylistSyncMaxConcurrency = 32;

    private readonly AppHttpClient httpClient;

    private readonly PlaylistRecommendedTableOwner recommendedTableOwner;

    private readonly Action<Exception, string> logWarning;

    private readonly Func<bool> isPlaylistUrlCompletionEnabled;

    private readonly Action<string> schedulePlaylistUrlCompletionRefresh;

    private readonly PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner;

    private readonly Action<BMSTable, string> ensurePlaylistEntriesLoaded;

    private readonly Func<BMSTable, bool> isActiveTable;

    private readonly Action<BMSTable, string> queueBeatorajaBmtExport;

    private readonly Action<BMSTable, string> applyCachedPlaylistUrlCompletion;

    private readonly Action enterPlaylistUpdating;

    private readonly Action exitPlaylistUpdating;

    private readonly Action<string> logPerformance;

    private readonly Func<BMSTable, bool> addSingleVisibleTable;

    private readonly Func<IEnumerable<BMSTable>, BMSTable> addBatchVisibleTables;

    private readonly Action<IEnumerable<BMSTable>> removeVisibleTables;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> resolveCustomFolderOutputDirectory;

    private readonly Func<BMSTable, string, string, bool, string, string, bool, CustomFolderOutputSettingsSnapshot, bool> tryMigrateCustomFolderOutputDirectory;

    private readonly Action<IEnumerable<BMSTable>, string> queueBeatorajaBmtExports;

    private readonly Action<IEnumerable<BMSTable>, string> applyCachedPlaylistUrlCompletions;

    private readonly Action<IReadOnlyList<BMSTable>, string> reOutputCustomFoldersAfterReload;

    internal event EventHandler<PlaylistTableUpdateReceiptPublishedEventArgs> PlaylistTableUpdateReceiptPublished;

    /// <summary>
    /// 外部プレイリスト取得、正本永続化、関連する派生出力の収束に必要な依存を構成します。
    /// </summary>
    /// <param name="httpClient">外部データ取得に使う HTTP client。</param>
    /// <param name="recommendedTableOwner">おすすめ表の解決 owner。</param>
    /// <param name="logWarning">失敗を記録する callback。</param>
    /// <param name="isPlaylistUrlCompletionEnabled">URL 補完の有効状態を返す callback。</param>
    /// <param name="schedulePlaylistUrlCompletionRefresh">URL 補完 refresh を予約する callback。</param>
    /// <param name="playlistAggregatePersistenceOwner">playlist 正本と active membership の owner。</param>
    /// <param name="ensurePlaylistEntriesLoaded">playlist entry hydration を保証する callback。</param>
    /// <param name="isActiveTable">対象 table が active かを返す callback。</param>
    /// <param name="queueBeatorajaBmtExport">単一 table の `.bmt` 出力を予約する callback。</param>
    /// <param name="applyCachedPlaylistUrlCompletion">単一 table へ URL 補完 cache を反映する callback。</param>
    /// <param name="enterPlaylistUpdating">playlist 更新中状態へ入る callback。</param>
    /// <param name="exitPlaylistUpdating">playlist 更新中状態から抜ける callback。</param>
    /// <param name="logPerformance">性能情報を記録する callback。</param>
    /// <param name="addSingleVisibleTable">単一 table を visible collection へ追加する callback。</param>
    /// <param name="addBatchVisibleTables">複数 table を visible collection へ追加する callback。</param>
    /// <param name="removeVisibleTables">visible collection から table を除く callback。</param>
    /// <param name="customFolderOutputSettingsProvider">custom-folder 出力設定 snapshot provider。</param>
    /// <param name="resolveCustomFolderOutputDirectory">custom-folder 出力先を解決する callback。</param>
    /// <param name="tryMigrateCustomFolderOutputDirectory">custom-folder 出力を移行する callback。</param>
    /// <param name="queueBeatorajaBmtExports">複数 table の `.bmt` 出力を予約する callback。</param>
    /// <param name="applyCachedPlaylistUrlCompletions">複数 table へ URL 補完 cache を反映する callback。</param>
    /// <param name="reOutputCustomFoldersAfterReload">
    /// 正本を永続化した reload result を呼び出し単位でまとめ、`.lr2folder` と LR2 `folder` row を収束させる callback。
    /// </param>
    internal PlaylistExternalSyncOwner(
        AppHttpClient httpClient,
        PlaylistRecommendedTableOwner recommendedTableOwner,
        Action<Exception, string> logWarning,
        Func<bool> isPlaylistUrlCompletionEnabled,
        Action<string> schedulePlaylistUrlCompletionRefresh,
        PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner = null,
        Action<BMSTable, string> ensurePlaylistEntriesLoaded = null,
        Func<BMSTable, bool> isActiveTable = null,
        Action<BMSTable, string> queueBeatorajaBmtExport = null,
        Action<BMSTable, string> applyCachedPlaylistUrlCompletion = null,
        Action enterPlaylistUpdating = null,
        Action exitPlaylistUpdating = null,
        Action<string> logPerformance = null,
        Func<BMSTable, bool> addSingleVisibleTable = null,
        Func<IEnumerable<BMSTable>, BMSTable> addBatchVisibleTables = null,
        Action<IEnumerable<BMSTable>> removeVisibleTables = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> resolveCustomFolderOutputDirectory = null,
        Func<BMSTable, string, string, bool, string, string, bool, CustomFolderOutputSettingsSnapshot, bool> tryMigrateCustomFolderOutputDirectory = null,
        Action<IEnumerable<BMSTable>, string> queueBeatorajaBmtExports = null,
        Action<IEnumerable<BMSTable>, string> applyCachedPlaylistUrlCompletions = null,
        Action<IReadOnlyList<BMSTable>, string> reOutputCustomFoldersAfterReload = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.recommendedTableOwner = recommendedTableOwner ?? throw new ArgumentNullException(nameof(recommendedTableOwner));
        this.logWarning = logWarning;
        this.isPlaylistUrlCompletionEnabled = isPlaylistUrlCompletionEnabled;
        this.schedulePlaylistUrlCompletionRefresh = schedulePlaylistUrlCompletionRefresh;
        this.playlistAggregatePersistenceOwner = playlistAggregatePersistenceOwner;
        this.ensurePlaylistEntriesLoaded = ensurePlaylistEntriesLoaded;
        this.isActiveTable = isActiveTable;
        this.queueBeatorajaBmtExport = queueBeatorajaBmtExport;
        this.applyCachedPlaylistUrlCompletion = applyCachedPlaylistUrlCompletion;
        this.enterPlaylistUpdating = enterPlaylistUpdating;
        this.exitPlaylistUpdating = exitPlaylistUpdating;
        this.logPerformance = logPerformance;
        this.addSingleVisibleTable = addSingleVisibleTable;
        this.addBatchVisibleTables = addBatchVisibleTables;
        this.removeVisibleTables = removeVisibleTables;
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider;
        this.resolveCustomFolderOutputDirectory = resolveCustomFolderOutputDirectory;
        this.tryMigrateCustomFolderOutputDirectory = tryMigrateCustomFolderOutputDirectory;
        this.queueBeatorajaBmtExports = queueBeatorajaBmtExports;
        this.applyCachedPlaylistUrlCompletions = applyCachedPlaylistUrlCompletions;
        this.reOutputCustomFoldersAfterReload = reOutputCustomFoldersAfterReload;
    }

    internal BMSTable LoadExternalTable(Uri pageUri, BMSTable baseTable = null)
    {
        return LoadExternalTableAsync(pageUri, baseTable).GetAwaiter().GetResult();
    }

    internal async Task<BMSTable> LoadExternalTableAsync(
        Uri pageUri,
        BMSTable baseTable = null,
        CancellationToken cancellationToken = default)
    {
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new ArgumentException(Resources.Error_URIMustBeAbsolute, nameof(pageUri));
        }
        if (pageUri.Scheme == "bmseeker")
        {
            return recommendedTableOwner.LoadWalkureTable(pageUri, baseTable);
        }

        Uri originalPageUri = pageUri;
        Uri headerUri = null;
        Uri resolvedHeaderUri = null;
        Uri resolvedDataUri = null;
        BMSTable bmsTable = null;
        try
        {
            string input = await httpClient.GetStringAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
            string headerJson;
            if (TryResolveHeaderUri(input, pageUri, out headerUri))
            {
                resolvedHeaderUri = !headerUri.IsAbsoluteUri ? new Uri(pageUri, headerUri) : headerUri;
                headerJson = await httpClient.GetStringAsync(resolvedHeaderUri, null, cancellationToken).ConfigureAwait(false);
            }
            else if (LooksLikeJsonContent(input))
            {
                headerUri = pageUri;
                pageUri = null;
                resolvedHeaderUri = headerUri;
                headerJson = input;
            }
            else
            {
                throw new PlaylistHeaderUriNotFoundException(originalPageUri);
            }

            bmsTable = new BMSTable();
            if (baseTable != null)
            {
                bmsTable.compat_prefix = baseTable.compat_prefix;
            }
            bmsTable.LoadHeaderJSON(headerJson, pageUri, headerUri, preserveLoadedCompatPrefix: baseTable != null);
            if (baseTable != null)
            {
                bmsTable.playlist_id = baseTable.playlist_id;
                bmsTable.name = baseTable.name;
                bmsTable.symbol = baseTable.symbol;
                bmsTable.ignore_folder_output = baseTable.ignore_folder_output;
                bmsTable.is_external_sync = baseTable.is_external_sync;
                bmsTable.Output_dir = baseTable.Output_dir;
                bmsTable.is_root_folder = baseTable.is_root_folder;
                bmsTable.custom_folder_output_base_name = baseTable.custom_folder_output_base_name;
                bmsTable.bmt_sort = baseTable.bmt_sort;
                bmsTable.is_bmt_output = baseTable.is_bmt_output;
            }
            else
            {
                bmsTable.ignore_folder_output = NormalizeNewPlaylistIgnoreFolderOutputDefault(
                    GetCustomFolderOutputSettings().PlaylistDefaultIgnoreFolderOutput);
            }

            resolvedDataUri = bmsTable.GetAbsoluteDataUrl();
            if (resolvedDataUri == null)
            {
                throw new InvalidOperationException(
                    "Failed to resolve playlist data_url. rawDataUrl=" + FormatTextForLog(bmsTable.data_url)
                    + " pageUrl=" + FormatUriForLog(bmsTable.Page_url)
                    + " headerUrl=" + FormatUriForLog(bmsTable.Header_url)
                    + " sourcePage=" + FormatUriForLog(originalPageUri));
            }
            string dataJson = await httpClient.GetStringAsync(resolvedDataUri, null, cancellationToken).ConfigureAwait(false);
            bmsTable.LoadDataJSON(dataJson);
            return bmsTable;
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(
                ex,
                "playlist_external_load_failed pageUri=" + FormatUriForLog(originalPageUri)
                + " resolvedPageUri=" + FormatUriForLog(pageUri)
                + " rawHeaderUri=" + FormatUriForLog(headerUri)
                + " resolvedHeaderUri=" + FormatUriForLog(resolvedHeaderUri)
                + " rawDataUrl=" + FormatTextForLog(bmsTable?.data_url)
                + " storedPageUrl=" + FormatUriForLog(bmsTable?.Page_url)
                + " storedHeaderUrl=" + FormatUriForLog(bmsTable?.Header_url)
                + " resolvedDataUri=" + FormatUriForLog(resolvedDataUri)
                + " baseTable=" + FormatTextForLog(baseTable?.name));
            throw;
        }
    }

    internal async Task<List<PlaylistExternalTableLoadResult>> LoadExternalTableSnapshotsAsync(
        IEnumerable<BMSTable> targets,
        bool inheritLocalTableProperties,
        Action<PlaylistSyncProgressSnapshot> progressCallback = null,
        string reason = "LoadExternalTableSnapshotsAsync",
        CancellationToken cancellationToken = default,
        bool schedulePlaylistUrlCompletionRefresh = true)
    {
        List<BMSTable> targetSnapshot = [.. (targets ?? [])
            .Where(table => table != null)
            .Distinct()
            .Where(table =>
            {
                Uri uri = table.Page_url ?? table.Header_url;
                return uri != null && uri.IsAbsoluteUri;
            })];
        int completedTableCount = 0;
        PlaylistExternalTableLoadResult[] results = new PlaylistExternalTableLoadResult[targetSnapshot.Count];
        InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
        {
            IsActive = targetSnapshot.Count > 0,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = 0,
            CurrentTableName = string.Empty,
            CurrentUri = null
        }, reason);
        using var semaphoreSlim = new SemaphoreSlim(ExternalPlaylistSyncMaxConcurrency, ExternalPlaylistSyncMaxConcurrency);
        await Task.WhenAll([.. targetSnapshot.Select(async (table, index) =>
        {
            await semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            Uri uri = table.Page_url ?? table.Header_url;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = targetSnapshot.Count,
                    CompletedTableCount = Volatile.Read(ref completedTableCount),
                    CurrentTableName = table.name,
                    CurrentUri = uri
                }, reason);
                BMSTable externalTable = await LoadExternalTableAsync(
                    uri,
                    inheritLocalTableProperties ? table : null,
                    cancellationToken).ConfigureAwait(false);
                results[index] = new PlaylistExternalTableLoadResult
                {
                    SourceTable = table,
                    ExternalTable = externalTable,
                    Uri = uri
                };
            }
            catch (Exception ex)
            {
                logWarning?.Invoke(
                    ex,
                    "playlist_external_snapshot_load_failed reason=" + FormatTextForLog(reason)
                    + " table=" + FormatTextForLog(table?.name)
                    + " uri=" + FormatUriForLog(uri));
                results[index] = new PlaylistExternalTableLoadResult
                {
                    SourceTable = table,
                    Uri = uri,
                    Exception = ex
                };
            }
            finally
            {
                int completed = Interlocked.Increment(ref completedTableCount);
                semaphoreSlim.Release();
                InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = targetSnapshot.Count,
                    CompletedTableCount = completed,
                    CurrentTableName = table.name,
                    CurrentUri = uri
                }, reason);
            }
        })]).ConfigureAwait(false);
        InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = completedTableCount,
            CurrentTableName = string.Empty,
            CurrentUri = null
        }, reason);
        bool urlCompletionEnabled = false;
        try
        {
            urlCompletionEnabled = isPlaylistUrlCompletionEnabled?.Invoke() == true;
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(ex, "playlist_url_completion_enabled_check_failed reason=" + FormatTextForLog(reason));
        }
        if (schedulePlaylistUrlCompletionRefresh
            && targetSnapshot.Count > 0
            && urlCompletionEnabled)
        {
            InvokeResidualAction(
                () => this.schedulePlaylistUrlCompletionRefresh?.Invoke(reason),
                reason + ":url-completion-refresh");
        }
        return [.. results.Where(result => result != null)];
    }

    /// <summary>
    /// 指定した外部プレイリストを並列取得して正本へ反映し、永続化された対象の派生出力を返却前に batch 収束させます。
    /// </summary>
    /// <param name="targets">再取得対象。</param>
    /// <param name="syncResultCallback">table 単位の取得結果を通知する callback。</param>
    /// <param name="progressCallback">進捗を通知する callback。</param>
    /// <param name="reason">ログと派生処理へ渡す理由。</param>
    /// <param name="cancellationToken">取得待機を取り消す token。</param>
    /// <param name="requireCurrentTargetForApply">active table でなくなった対象への反映を拒否するか。</param>
    /// <param name="uriProvider">対象ごとの取得 URI override。</param>
    /// <param name="publishReferenceReceipts">reference table 更新 receipt を publish するか。</param>
    /// <returns>URI を解決できた対象ごとの reload result。</returns>
    internal async Task<List<PlaylistReloadTargetResult>> ReloadPlaylistTargetsAsync(
        IEnumerable<BMSTable> targets,
        Action<PlaylistSyncAttemptResult> syncResultCallback = null,
        Action<PlaylistSyncProgressSnapshot> progressCallback = null,
        string reason = "ReloadPlaylistTargetsAsync",
        CancellationToken cancellationToken = default,
        bool requireCurrentTargetForApply = true,
        Func<BMSTable, Uri> uriProvider = null,
        bool publishReferenceReceipts = false)
    {
        enterPlaylistUpdating?.Invoke();
        try
        {
            if (playlistAggregatePersistenceOwner == null)
            {
                throw new InvalidOperationException("Playlist aggregate persistence owner is required for external reload.");
            }
            Uri ResolveTargetUri(BMSTable table)
            {
                return uriProvider?.Invoke(table) ?? table.Page_url ?? table.Header_url;
            }

            List<BMSTable> targetSnapshot = [.. (targets ?? [])
                .Where(table => table != null)
                .Distinct()
                .Where(table =>
                {
                    Uri uri = ResolveTargetUri(table);
                    return uri != null && uri.IsAbsoluteUri;
                })];
            int completedTableCount = 0;
            List<PlaylistReloadTargetResult> results = [];
            object resultLock = new();
            InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
            {
                IsActive = targetSnapshot.Count > 0,
                TotalTableCount = targetSnapshot.Count,
                CompletedTableCount = 0,
                CurrentTableName = string.Empty,
                CurrentUri = null
            }, reason);
            using var semaphoreSlim = new SemaphoreSlim(ExternalPlaylistSyncMaxConcurrency, ExternalPlaylistSyncMaxConcurrency);
            try
            {
                await Task.WhenAll([.. targetSnapshot.Select(async table =>
                {
                    await semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
                    Uri uri = ResolveTargetUri(table);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = targetSnapshot.Count,
                            CompletedTableCount = Volatile.Read(ref completedTableCount),
                            CurrentTableName = table.name,
                            CurrentUri = uri
                        }, reason);
                        PlaylistReloadTargetResult result = await ReloadPlaylistTargetCoreAsync(
                            table,
                            uri,
                            syncResultCallback,
                            reason,
                            cancellationToken,
                            requireCurrentTargetForApply,
                            allowUriOverride: uriProvider != null,
                            publishReferenceReceipt: publishReferenceReceipts).ConfigureAwait(false);
                        lock (resultLock)
                        {
                            results.Add(result);
                        }
                    }
                    finally
                    {
                        int completed = Interlocked.Increment(ref completedTableCount);
                        semaphoreSlim.Release();
                        InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
                        {
                            IsActive = true,
                            TotalTableCount = targetSnapshot.Count,
                            CompletedTableCount = completed,
                            CurrentTableName = table.name,
                            CurrentUri = uri
                        }, reason);
                    }
                })]).ConfigureAwait(false);
            }
            finally
            {
                // Cancellation can be observed after another target has already committed.
                // Converge derived LR2 output for every durable result before propagating it.
                List<PlaylistReloadTargetResult> resultSnapshot;
                lock (resultLock)
                {
                    resultSnapshot = [.. results];
                }
                ReOutputCustomFoldersForPersistedReloads(resultSnapshot, reason);
            }
            InvokeProgressCallback(progressCallback, new PlaylistSyncProgressSnapshot
            {
                IsActive = false,
                TotalTableCount = targetSnapshot.Count,
                CompletedTableCount = completedTableCount,
                CurrentTableName = string.Empty,
                CurrentUri = null
            }, reason);
            bool urlCompletionEnabled = false;
            try
            {
                urlCompletionEnabled = isPlaylistUrlCompletionEnabled?.Invoke() == true;
            }
            catch (Exception ex)
            {
                logWarning?.Invoke(ex, "playlist_url_completion_enabled_check_failed reason=" + FormatTextForLog(reason));
            }
            if (targetSnapshot.Count > 0 && urlCompletionEnabled)
            {
                InvokeResidualAction(
                    () => schedulePlaylistUrlCompletionRefresh?.Invoke(reason),
                    reason + ":url-completion-refresh");
            }
            return results;
        }
        finally
        {
            exitPlaylistUpdating?.Invoke();
        }
    }

    internal async Task<BMSTable> ReloadAndApplySingleTableAsync(
        BMSTable table,
        Uri pageUri,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Playlist reload URI is not absolute.");
        }
        List<PlaylistReloadTargetResult> results = await ReloadPlaylistTargetsAsync(
            [table],
            reason: reason ?? "ReloadAndApplySingleTableAsync",
            cancellationToken: cancellationToken,
            requireCurrentTargetForApply: true,
            uriProvider: _ => pageUri).ConfigureAwait(false);
        PlaylistReloadTargetResult result = results.SingleOrDefault();
        if (result == null)
        {
            throw new InvalidOperationException("Playlist reload produced no result.");
        }
        if (result.Exception != null)
        {
            throw result.Exception;
        }
        return result.ResultTable;
    }

    internal async Task<List<BMSTable>> UpdateBMSTablesInternalAsync(
        bool reloadExtPlaylist = true,
        Action<PlaylistSyncAttemptResult> syncResultCallback = null,
        Action<PlaylistSyncProgressSnapshot> progressCallback = null,
        CancellationToken cancellationToken = default,
        bool publishReferenceReceipts = false)
    {
        enterPlaylistUpdating?.Invoke();
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            List<BMSTable> tableSnapshot = [.. playlistAggregatePersistenceOwner.GetActiveCollectionSnapshot().Tables];
            List<BMSTable> reloadTargets = [.. tableSnapshot.Where(table =>
            {
                Uri uri = table?.Page_url ?? table?.Header_url;
                return reloadExtPlaylist
                    && table != null
                    && table.is_external_sync
                    && uri != null
                    && uri.IsAbsoluteUri;
            })];
            List<PlaylistReloadTargetResult> results = await ReloadPlaylistTargetsAsync(
                reloadTargets,
                syncResultCallback,
                progressCallback,
                "UpdateBMSTablesInternalAsync",
                cancellationToken,
                requireCurrentTargetForApply: true,
                publishReferenceReceipts: publishReferenceReceipts).ConfigureAwait(false);
            stopwatch.Stop();
            List<BMSTable> updatedTables = [.. results
                .Where(result => result.Succeeded && result.Updated && result.ResultTable != null)
                .Select(result => result.ResultTable)];
            logPerformance?.Invoke(
                "playlist_update table_count=" + tableSnapshot.Count
                + " target_count=" + reloadTargets.Count
                + " updated_count=" + updatedTables.Count
                + " total_ms=" + stopwatch.ElapsedMilliseconds);
            return updatedTables;
        }
        finally
        {
            exitPlaylistUpdating?.Invoke();
        }
    }

    internal BMSTable RegistrateExternalTable(Uri pageUri)
    {
        return RegistrateExternalTableAsync(pageUri).GetAwaiter().GetResult();
    }

    internal async Task<BMSTable> RegistrateExternalTableAsync(
        Uri pageUri,
        CancellationToken cancellationToken = default)
    {
        return await RegistrateExternalTableAsync(
            pageUri,
            renameDuplicateName: false,
            "RegistrateExternalTableAsync",
            preserveSourceUrlText: false,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<BMSTable> RegistrateExternalTableAsync(
        Uri pageUri,
        bool renameDuplicateName,
        string reason,
        bool preserveSourceUrlText = false,
        CancellationToken cancellationToken = default)
    {
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("pageUri.IsAbsoluteUri is not true");
        }
        if (!preserveSourceUrlText)
        {
            pageUri = new Uri(pageUri.AbsoluteUri, UriKind.Absolute);
        }
        BMSTable table = await LoadExternalTableAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await RegistrateExternalTableAsync(
            table,
            renameDuplicateName,
            reason ?? "RegistrateExternalTableAsync",
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<BMSTable> RegistrateExternalTableAsync(
        BMSTable bmsTable,
        bool renameDuplicateName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException(nameof(bmsTable));
        }
        EnsureRegistrationPorts();
        if (!playlistAggregatePersistenceOwner.TryBeginRegistration())
        {
            throw new InvalidOperationException("Playlist registration cannot run while playlist initialization or reload is active.");
        }
        bool migrateCustomFolderOutput = false;
        string customFolderOutputDirectory = null;
        CustomFolderOutputSettingsSnapshot settings;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings = GetCustomFolderOutputSettings();
            PlaylistAggregatePersistenceOwner.PlaylistRegistrationPreparation preparation =
                playlistAggregatePersistenceOwner.PrepareExternalRegistration([bmsTable], renameDuplicateName).Single();
            if (preparation.DuplicateName)
            {
                throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, bmsTable.name);
            }
            if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
            {
                throw new InvalidOperationException(Resources.Error_OutputDirNameEmpty);
            }
            bmsTable.last_update = preparation.LastUpdate;
            bmsTable.name = preparation.Name;
            bmsTable.bmt_sort = preparation.BmtSort;
            bmsTable.is_bmt_output = true;
            if (settings.OperationModeLR2DB)
            {
                migrateCustomFolderOutput = true;
                customFolderOutputDirectory = resolveCustomFolderOutputDirectory?.Invoke(bmsTable, settings);
            }
            await CommitAndAddBMSTableAsync(bmsTable).ConfigureAwait(false);
        }
        finally
        {
            playlistAggregatePersistenceOwner.EndRegistration();
        }
        string operationReason = reason ?? "RegistrateExternalTableAsync";
        if (migrateCustomFolderOutput)
        {
            InvokeResidualAction(
                () => tryMigrateCustomFolderOutputDirectory?.Invoke(
                    bmsTable,
                    customFolderOutputDirectory,
                    customFolderOutputDirectory,
                    bmsTable.is_root_folder,
                    null,
                    null,
                    true,
                    settings),
                operationReason + ":custom-folder-migration",
                bmsTable.name);
        }
        InvokeResidualAction(
            () => queueBeatorajaBmtExport?.Invoke(bmsTable, operationReason),
            operationReason + ":bmt-export",
            bmsTable.name);
        InvokeResidualAction(
            () => applyCachedPlaylistUrlCompletion?.Invoke(bmsTable, operationReason),
            operationReason + ":url-completion",
            bmsTable.name);
        InvokeResidualAction(
            () => schedulePlaylistUrlCompletionRefresh?.Invoke(operationReason),
            operationReason + ":url-completion-refresh",
            bmsTable.name);
        return bmsTable;
    }

    internal async Task<RegisteredExternalTableBatchResult> RegistrateExternalTablesAsync(
        IEnumerable<BMSTable> bmsTables,
        bool renameDuplicateName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        List<BMSTable> tableList = [.. (bmsTables ?? []).Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return new RegisteredExternalTableBatchResult();
        }
        string operationReason = reason ?? "RegistrateExternalTablesAsync";
        List<BMSTable> migrateCustomFolderOutputTables = [];
        List<(BMSTable Table, string Directory)> customFolderOutputTargets = [];
        EnsureRegistrationPorts();
        if (!playlistAggregatePersistenceOwner.TryBeginRegistration())
        {
            throw new InvalidOperationException("Playlist registration cannot run while playlist initialization or reload is active.");
        }
        CustomFolderOutputSettingsSnapshot settings;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings = GetCustomFolderOutputSettings();
            IReadOnlyList<PlaylistAggregatePersistenceOwner.PlaylistRegistrationPreparation> preparations =
                playlistAggregatePersistenceOwner.PrepareExternalRegistration(tableList, renameDuplicateName);
            for (int index = 0; index < tableList.Count; index++)
            {
                BMSTable table = tableList[index];
                PlaylistAggregatePersistenceOwner.PlaylistRegistrationPreparation preparation = preparations[index];
                if (preparation.DuplicateName)
                {
                    throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, table.name);
                }
                if (string.IsNullOrWhiteSpace(table.Output_dir))
                {
                    throw new InvalidOperationException(Resources.Error_OutputDirNameEmpty);
                }
                table.last_update = preparation.LastUpdate;
                table.name = preparation.Name;
                table.bmt_sort = preparation.BmtSort;
                table.is_bmt_output = true;
                if (settings.OperationModeLR2DB)
                {
                    migrateCustomFolderOutputTables.Add(table);
                    customFolderOutputTargets.Add((table, resolveCustomFolderOutputDirectory?.Invoke(table, settings)));
                }
            }
            await CommitAndAddBMSTablesAsync(tableList).ConfigureAwait(false);
        }
        finally
        {
            playlistAggregatePersistenceOwner.EndRegistration();
        }
        foreach ((BMSTable table, string directory) target in customFolderOutputTargets)
        {
            InvokeResidualAction(
                () => tryMigrateCustomFolderOutputDirectory?.Invoke(
                    target.table,
                    target.directory,
                    target.directory,
                    target.table.is_root_folder,
                    null,
                    null,
                    true,
                    settings),
                operationReason + ":custom-folder-migration",
                target.table?.name);
        }
        InvokeResidualAction(
            () => queueBeatorajaBmtExports?.Invoke(tableList, operationReason),
            operationReason + ":bmt-export");
        InvokeResidualAction(
            () => applyCachedPlaylistUrlCompletions?.Invoke(tableList, operationReason),
            operationReason + ":url-completion");
        InvokeResidualAction(
            () => schedulePlaylistUrlCompletionRefresh?.Invoke(operationReason),
            operationReason + ":url-completion-refresh");
        return new RegisteredExternalTableBatchResult
        {
            RegisteredTables = tableList,
            MigratedCustomFolderTables = migrateCustomFolderOutputTables
        };
    }

    private async Task CommitAndAddBMSTableAsync(BMSTable table)
    {
        EnsureRegistrationPorts();
        bool durableCommitted = false;
        Dictionary<BMSTable, int?> originalPlaylistIds = new() { [table] = table.playlist_id };
        try
        {
            playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                [table],
                requireCurrentTarget: false,
                hydrationReason: "ExternalTableRegistration");
            durableCommitted = true;
            if (!addSingleVisibleTable(table))
            {
                playlistAggregatePersistenceOwner.DeleteUnpublishedTables([table], originalPlaylistIds);
                throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, table?.name);
            }
        }
        catch (Exception error)
        {
            if (durableCommitted)
            {
                Exception rollbackFailure = RollbackRegisteredTables([table], originalPlaylistIds);
                if (rollbackFailure != null)
                {
                    throw new AggregateException("Playlist registration failed and rollback encountered errors.", error, rollbackFailure);
                }
            }
            throw;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task CommitAndAddBMSTablesAsync(IEnumerable<BMSTable> tables)
    {
        EnsureRegistrationPorts();
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        bool durableCommitted = false;
        Dictionary<BMSTable, int?> originalPlaylistIds = tableList.ToDictionary(table => table, table => table.playlist_id);
        try
        {
            playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                tableList,
                requireCurrentTarget: false,
                hydrationReason: "ExternalTableBatchRegistration");
            durableCommitted = true;
            BMSTable duplicateTable = addBatchVisibleTables(tableList);
            if (duplicateTable != null)
            {
                playlistAggregatePersistenceOwner.DeleteUnpublishedTables(tableList, originalPlaylistIds);
                throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, duplicateTable.name);
            }
        }
        catch (Exception error)
        {
            if (durableCommitted)
            {
                Exception rollbackFailure = RollbackRegisteredTables(tableList, originalPlaylistIds);
                if (rollbackFailure != null)
                {
                    throw new AggregateException("Playlist registration failed and rollback encountered errors.", error, rollbackFailure);
                }
            }
            throw;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Exception RollbackRegisteredTables(
        IEnumerable<BMSTable> tables,
        IReadOnlyDictionary<BMSTable, int?> originalPlaylistIds)
    {
        List<Exception> failures = [];
        try
        {
            removeVisibleTables?.Invoke(tables);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        try
        {
            playlistAggregatePersistenceOwner.DeleteUnpublishedTables(tables, originalPlaylistIds);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures).Flatten()
        };
    }

    private void EnsureRegistrationPorts()
    {
        if (playlistAggregatePersistenceOwner == null
            || addSingleVisibleTable == null
            || addBatchVisibleTables == null)
        {
            throw new InvalidOperationException("Playlist external registration owner is not fully composed.");
        }
    }

    private CustomFolderOutputSettingsSnapshot GetCustomFolderOutputSettings()
    {
        return customFolderOutputSettingsProvider?.Invoke()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
    }

    private async Task<PlaylistReloadTargetResult> ReloadPlaylistTargetCoreAsync(
        BMSTable table,
        Uri uri,
        Action<PlaylistSyncAttemptResult> syncResultCallback,
        string reason,
        CancellationToken cancellationToken,
        bool requireCurrentTargetForApply = true,
        bool allowUriOverride = false,
        bool publishReferenceReceipt = false)
    {
        BMSTable newTable = table;
        List<BMSTableEntry> oldEntriesSnapshot = null;
        List<BMSTableEntry> newEntriesSnapshot = null;
        PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision persistenceDecision = null;
        bool updated = false;
        bool applySkipped = false;
        int sourceEntriesRevision = 0;
        DateTime sourceLastUpdate = default;
        string sourceStateFingerprint = string.Empty;
        Uri sourceUri = null;
        Exception failure = null;
        try
        {
            ensurePlaylistEntriesLoaded?.Invoke(table, reason);
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                sourceEntriesRevision = table.PlaylistEntriesRevision;
                sourceLastUpdate = table.last_update;
                sourceStateFingerprint = PlaylistAggregatePersistenceOwner.CreateReloadSourceFingerprint(table);
                sourceUri = table.Page_url ?? table.Header_url;
            }
            if (!allowUriOverride && !UriEquals(uri, sourceUri))
            {
                throw new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                    "Playlist reload request no longer matches the active playlist source URI.");
            }
            BMSTable reloadedTable = await LoadExternalTableAsync(uri, table, cancellationToken).ConfigureAwait(false);
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                if (table.PlaylistEntriesRevision != sourceEntriesRevision
                    || table.last_update != sourceLastUpdate
                    || !string.Equals(
                        PlaylistAggregatePersistenceOwner.CreateReloadSourceFingerprint(table),
                        sourceStateFingerprint,
                        StringComparison.Ordinal)
                    || (!allowUriOverride && !UriEquals(uri, table.Page_url ?? table.Header_url)))
                {
                    throw new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                        "Playlist reload source changed while the external snapshot was loading.");
                }
                oldEntriesSnapshot = [.. (table.entries ?? []).Where(entry => entry != null).Select(entry => entry.CreatePlaylistReloadSnapshot())];
                IReadOnlyList<BMSTableEntry> persistedActiveEntries = playlistAggregatePersistenceOwner.LoadPersistedActivePlaylistEntries(table.playlist_id);
                newTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
                    table,
                    reloadedTable,
                    PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(persistedActiveEntries),
                    out persistenceDecision,
                    logLastUpdateDecision: true);
                updated = persistenceDecision.UpdatesLastUpdate;
                if (persistenceDecision.NeedsEntryPersistence)
                {
                    newEntriesSnapshot = [.. (newTable.entries ?? []).Where(entry => entry != null).Select(entry => entry.CreatePlaylistReloadSnapshot())];
                }
                else
                {
                    newEntriesSnapshot = [.. oldEntriesSnapshot];
                    if (persistenceDecision.NeedsHeaderPersistence)
                    {
                        newTable.entries = [.. oldEntriesSnapshot];
                    }
                    if (!persistenceDecision.NeedsStatePersistence)
                    {
                        newTable = table;
                    }
                }
            }
            if (!await playlistAggregatePersistenceOwner.TryApplyReloadedTableAsync(
                table,
                newTable,
                persistenceDecision,
                sourceEntriesRevision,
                sourceLastUpdate,
                sourceStateFingerprint,
                requireCurrentTargetForApply).ConfigureAwait(false))
            {
                failure = new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                    "Playlist reload result could not be applied to the active table.");
                applySkipped = true;
                newTable = table;
                oldEntriesSnapshot = null;
                newEntriesSnapshot = null;
                persistenceDecision = null;
                updated = false;
                InvokeSyncResultCallback(
                    syncResultCallback,
                    PlaylistSyncAttemptResult.CreateFailure(table, uri, failure),
                    reason);
            }
            else
            {
                if (requireCurrentTargetForApply
                    && isActiveTable != null
                    && !isActiveTable(newTable))
                {
                    throw new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                        "Playlist reload result was applied to a table that is no longer active.");
                }
                if (persistenceDecision?.NeedsBmtExport == true)
                {
                    InvokeResidualAction(
                        () => queueBeatorajaBmtExport?.Invoke(newTable, reason),
                        reason + ":bmt-export",
                        newTable?.name);
                }
                InvokeResidualAction(
                    () => applyCachedPlaylistUrlCompletion?.Invoke(newTable, reason),
                    reason + ":url-completion",
                    newTable?.name);
                InvokeSyncResultCallback(
                    syncResultCallback,
                    PlaylistSyncAttemptResult.CreateSuccess(table, newTable, uri, updated),
                    reason);
            }
        }
        catch (PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException ex)
        {
            failure = ex;
            applySkipped = true;
            newTable = table;
            oldEntriesSnapshot = null;
            newEntriesSnapshot = null;
            persistenceDecision = null;
            updated = false;
            logWarning?.Invoke(
                ex,
                "playlist_reload_apply_failed reason=" + FormatTextForLog(reason)
                + " table=" + FormatTextForLog(table?.name)
                + " uri=" + FormatUriForLog(uri));
            InvokeSyncResultCallback(
                syncResultCallback,
                PlaylistSyncAttemptResult.CreateFailure(table, uri, ex),
                reason);
        }
        catch (Exception ex)
        {
            failure = ex;
            applySkipped = true;
            newTable = table;
            oldEntriesSnapshot = null;
            newEntriesSnapshot = null;
            persistenceDecision = null;
            updated = false;
            logWarning?.Invoke(
                ex,
                "playlist_reload_target_failed reason=" + FormatTextForLog(reason)
                + " table=" + FormatTextForLog(table?.name)
                + " uri=" + FormatUriForLog(uri));
            InvokeSyncResultCallback(
                syncResultCallback,
                PlaylistSyncAttemptResult.CreateFailure(table, uri, ex),
                reason);
        }
        PlaylistTableUpdateReceipt updateReceipt = null;
        if (!applySkipped)
        {
            updateReceipt = new PlaylistTableUpdateReceipt(
                table,
                newTable,
                updated,
                persistenceDecision?.NeedsEntryPersistence == true,
                oldEntriesSnapshot,
                newEntriesSnapshot,
                uri,
                reason);
            if (publishReferenceReceipt)
            {
                PublishPlaylistTableUpdateReceipt(updateReceipt, uri);
            }
        }
        return new PlaylistReloadTargetResult
        {
            SourceTable = table,
            ResultTable = newTable,
            Uri = uri,
            Updated = updated,
            StatePersisted = persistenceDecision?.NeedsStatePersistence == true,
            Exception = failure,
            UpdateReceipt = updateReceipt
        };
    }

    private void ReOutputCustomFoldersForPersistedReloads(
        IReadOnlyList<PlaylistReloadTargetResult> results,
        string reason)
    {
        if (reOutputCustomFoldersAfterReload == null)
        {
            return;
        }
        List<BMSTable> persistedTables = [.. (results ?? [])
            .Where(result => result?.Succeeded == true
                && result.StatePersisted
                && result.ResultTable != null)
            .Select(result => result.ResultTable)
            .Distinct()];
        if (persistedTables.Count == 0)
        {
            return;
        }
        InvokeResidualAction(
            () => reOutputCustomFoldersAfterReload(persistedTables, reason),
            reason + ":custom-folder-output");
    }

    private void PublishPlaylistTableUpdateReceipt(
        PlaylistTableUpdateReceipt receipt,
        Uri uri)
    {
        if (receipt == null)
        {
            return;
        }
        foreach (EventHandler<PlaylistTableUpdateReceiptPublishedEventArgs> handler in
            PlaylistTableUpdateReceiptPublished?.GetInvocationList()
                .Cast<EventHandler<PlaylistTableUpdateReceiptPublishedEventArgs>>()
                ?? [])
        {
            try
            {
                handler(
                    this,
                    new PlaylistTableUpdateReceiptPublishedEventArgs(receipt));
            }
            catch (Exception ex)
            {
                logWarning?.Invoke(
                    ex,
                    "playlist_update_receipt_consumer_failed table="
                    + FormatTextForLog(receipt.NewTable?.name)
                    + " uri="
                    + FormatUriForLog(uri));
            }
        }
    }

    private void InvokeProgressCallback(
        Action<PlaylistSyncProgressSnapshot> progressCallback,
        PlaylistSyncProgressSnapshot snapshot,
        string reason)
    {
        if (progressCallback == null)
        {
            return;
        }
        try
        {
            progressCallback(snapshot);
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(
                ex,
                "playlist_sync_progress_callback_failed reason=" + FormatTextForLog(reason));
        }
    }

    private void InvokeSyncResultCallback(
        Action<PlaylistSyncAttemptResult> syncResultCallback,
        PlaylistSyncAttemptResult result,
        string reason)
    {
        if (syncResultCallback == null)
        {
            return;
        }
        try
        {
            syncResultCallback(result);
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(
                ex,
                "playlist_sync_result_callback_failed reason=" + FormatTextForLog(reason)
                + " table=" + FormatTextForLog(result?.SourceTable?.name)
                + " uri=" + FormatUriForLog(result?.PageUri));
        }
    }

    private void InvokeResidualAction(Action action, string operation, string tableName = null)
    {
        if (action == null)
        {
            return;
        }
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logWarning?.Invoke(
                ex,
                "playlist_external_sync_residual_failed operation=" + FormatTextForLog(operation)
                + " table=" + FormatTextForLog(tableName));
        }
    }

    private static bool UriEquals(Uri left, Uri right)
    {
        return string.Equals(left?.AbsoluteUri, right?.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class PlaylistExternalTableLoadResult
    {
        internal BMSTable SourceTable { get; init; }

        internal BMSTable ExternalTable { get; init; }

        internal Uri Uri { get; init; }

        internal Exception Exception { get; init; }

        internal bool Succeeded => Exception == null && ExternalTable != null;
    }

    internal sealed class PlaylistTableUpdateReceipt
    {
        internal PlaylistTableUpdateReceipt(
            BMSTable oldTable,
            BMSTable newTable,
            bool updated,
            bool referenceEntriesChanged,
            IReadOnlyList<BMSTableEntry> oldEntriesSnapshot,
            IReadOnlyList<BMSTableEntry> newEntriesSnapshot,
            Uri uri,
            string reason)
        {
            OldTable = oldTable;
            NewTable = newTable;
            Updated = updated;
            ReferenceEntriesChanged = referenceEntriesChanged;
            OldEntriesSnapshot = oldEntriesSnapshot;
            NewEntriesSnapshot = newEntriesSnapshot;
            Uri = uri;
            Reason = reason ?? string.Empty;
        }

        internal BMSTable NewTable { get; }

        internal bool Updated { get; }

        internal bool ReferenceEntriesChanged { get; }

        internal BMSTable OldTable { get; }

        internal IReadOnlyList<BMSTableEntry> OldEntriesSnapshot { get; }

        internal IReadOnlyList<BMSTableEntry> NewEntriesSnapshot { get; }

        internal Uri Uri { get; }

        internal string Reason { get; }
    }

    internal sealed class PlaylistTableUpdateReceiptPublishedEventArgs : EventArgs
    {
        internal PlaylistTableUpdateReceiptPublishedEventArgs(PlaylistTableUpdateReceipt receipt)
        {
            Receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        }

        internal PlaylistTableUpdateReceipt Receipt { get; }
    }

    internal sealed class PlaylistReloadTargetResult
    {
        internal BMSTable SourceTable { get; init; }

        internal BMSTable ResultTable { get; init; }

        internal Uri Uri { get; init; }

        internal bool Updated { get; init; }

        /// <summary>
        /// 正規の playlist / playlist_entry 状態がこのリロードで永続化されたかを示します。
        /// hash 初期化や entry fingerprint 修復では <see cref="Updated"/> が false の場合もあるため、
        /// durable state に従う派生出力はこの値を基準に収束させます。
        /// </summary>
        internal bool StatePersisted { get; init; }

        internal Exception Exception { get; init; }

        internal PlaylistTableUpdateReceipt UpdateReceipt { get; init; }

        internal bool Succeeded => Exception == null;
    }

    internal sealed class RegisteredExternalTableBatchResult
    {
        internal IReadOnlyList<BMSTable> RegisteredTables { get; init; } = [];

        internal IReadOnlyList<BMSTable> MigratedCustomFolderTables { get; init; } = [];
    }

    private static bool TryResolveHeaderUri(string input, Uri pageUri, out Uri headerUri)
    {
        headerUri = null;
        if (string.IsNullOrWhiteSpace(input) || pageUri == null)
        {
            return false;
        }

        var errorLog = new StringBuilder();
        try
        {
            XDocument document;
            using (var reader = new SgmlReader
            {
                Href = pageUri.AbsoluteUri,
                InputStream = new StringReader(input),
                IgnoreDtd = true,
                ErrorLog = new StringWriter(errorLog)
            })
            {
                document = XDocument.Load(reader);
            }

            XNamespace ns = document.Root.Name.Namespace;
            string value = (from item in document.Descendants(ns + "meta")
                            let name = item.Attribute("name")
                            let content = item.Attribute("content")
                            where name != null && content != null
                                && name.Value == "bmstable"
                                && !string.IsNullOrWhiteSpace(content.Value)
                            select content.Value).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                headerUri = new Uri(value, UriKind.RelativeOrAbsolute);
                return true;
            }
        }
        catch
        {
        }

        Match match = new Regex(
            "name\\s*=\\s*\"bmstable\"[^<>]*content\\s*=\\s*\"([^?\"<>]+)[\"?<>]",
            RegexOptions.IgnoreCase).Match(input);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            return false;
        }
        headerUri = new Uri(match.Groups[1].Value, UriKind.RelativeOrAbsolute);
        return true;
    }

    private static bool LooksLikeJsonContent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        string text = input.TrimStart('\ufeff', ' ', '\t', '\r', '\n');
        return text.StartsWith("{", StringComparison.Ordinal)
            || text.StartsWith("[", StringComparison.Ordinal);
    }

    private static string FormatUriForLog(Uri uri)
    {
        return uri?.ToString() ?? "(null)";
    }

    private static string FormatTextForLog(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static LR2SongDBExtended.playlist.CustomFolderType NormalizeNewPlaylistIgnoreFolderOutputDefault(int value)
    {
        return (LR2SongDBExtended.playlist.CustomFolderType)(
            value & (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders);
    }
}
