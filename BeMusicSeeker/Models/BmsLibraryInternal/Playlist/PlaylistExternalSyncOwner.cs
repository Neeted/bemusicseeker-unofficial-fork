using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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

    private readonly Func<BMSTable, string, LibraryFileMutationCapability, Task> exportBeatorajaBmt;

    private readonly Action<BMSTable, string> applyCachedPlaylistUrlCompletion;

    private readonly Action enterPlaylistUpdating;

    private readonly Action exitPlaylistUpdating;

    private readonly Action<string> logPerformance;

    private readonly Func<BMSTable, bool> addSingleVisibleTable;

    private readonly Func<IEnumerable<BMSTable>, BMSTable> addBatchVisibleTables;

    private readonly Action<IEnumerable<BMSTable>> removeVisibleTables;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> resolveCustomFolderOutputDirectory;

    private readonly Func<BMSTable, string, string, bool, string, string, bool, CustomFolderOutputSettingsSnapshot, LibraryFileMutationCapability, bool> tryMigrateCustomFolderOutputDirectory;

    private readonly Func<IEnumerable<BMSTable>, string, LibraryFileMutationCapability, Task> exportBeatorajaBmts;

    private readonly Action<IEnumerable<BMSTable>, string> applyCachedPlaylistUrlCompletions;

    private readonly Action<IReadOnlyList<BMSTable>, string, LibraryFileMutationCapability> reOutputCustomFoldersAfterReload;

    private readonly Func<LibraryFileMutationCapability, LibraryFileMutationLease> mutationAdmission;
    private readonly Action<IReadOnlyList<PlaylistTableUpdateReceipt>> requiredReferenceApply;

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
    /// <param name="exportBeatorajaBmt">確定した単表の必要BMT出力を同じ生存Pで直接待つcallback。</param>
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
    /// <param name="exportBeatorajaBmts">確定した変更表集合の必要BMT出力を同じ生存Pで直接待つcallback。</param>
    /// <param name="applyCachedPlaylistUrlCompletions">複数 table へ URL 補完 cache を反映する callback。</param>
    /// <param name="reOutputCustomFoldersAfterReload">
    /// 正本を永続化した reload result を呼び出し単位でまとめ、`.lr2folder` と LR2 `folder` row を収束させる callback。
    /// </param>
    /// <param name="requiredReferenceApply">全guard解放後・必要出力前に同じ確定receiptを参照モデルへ渡す必須接続。任意UI配送の抑制では省略しません。</param>
    internal PlaylistExternalSyncOwner(
        AppHttpClient httpClient,
        PlaylistRecommendedTableOwner recommendedTableOwner,
        Action<Exception, string> logWarning,
        Func<bool> isPlaylistUrlCompletionEnabled,
        Action<string> schedulePlaylistUrlCompletionRefresh,
        PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner = null,
        Action<BMSTable, string> ensurePlaylistEntriesLoaded = null,
        Func<BMSTable, bool> isActiveTable = null,
        Func<BMSTable, string, LibraryFileMutationCapability, Task> exportBeatorajaBmt = null,
        Action<BMSTable, string> applyCachedPlaylistUrlCompletion = null,
        Action enterPlaylistUpdating = null,
        Action exitPlaylistUpdating = null,
        Action<string> logPerformance = null,
        Func<BMSTable, bool> addSingleVisibleTable = null,
        Func<IEnumerable<BMSTable>, BMSTable> addBatchVisibleTables = null,
        Action<IEnumerable<BMSTable>> removeVisibleTables = null,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider = null,
        Func<BMSTable, CustomFolderOutputSettingsSnapshot, string> resolveCustomFolderOutputDirectory = null,
        Func<BMSTable, string, string, bool, string, string, bool, CustomFolderOutputSettingsSnapshot, LibraryFileMutationCapability, bool> tryMigrateCustomFolderOutputDirectory = null,
        Func<IEnumerable<BMSTable>, string, LibraryFileMutationCapability, Task> exportBeatorajaBmts = null,
        Action<IEnumerable<BMSTable>, string> applyCachedPlaylistUrlCompletions = null,
        Action<IReadOnlyList<BMSTable>, string, LibraryFileMutationCapability> reOutputCustomFoldersAfterReload = null,
        Func<LibraryFileMutationCapability, LibraryFileMutationLease> mutationAdmission = null,
        Action<IReadOnlyList<PlaylistTableUpdateReceipt>> requiredReferenceApply = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.recommendedTableOwner = recommendedTableOwner ?? throw new ArgumentNullException(nameof(recommendedTableOwner));
        // 診断先の失敗で元の取得・保存結果を置き換えない。
        this.logWarning = (exception, message) =>
        {
            try { logWarning?.Invoke(exception, message); }
            catch { /* 診断失敗の再通知は行わない。 */ }
        };
        this.isPlaylistUrlCompletionEnabled = isPlaylistUrlCompletionEnabled;
        this.schedulePlaylistUrlCompletionRefresh = schedulePlaylistUrlCompletionRefresh;
        this.playlistAggregatePersistenceOwner = playlistAggregatePersistenceOwner;
        this.ensurePlaylistEntriesLoaded = ensurePlaylistEntriesLoaded;
        this.isActiveTable = isActiveTable;
        this.exportBeatorajaBmt = exportBeatorajaBmt;
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
        this.exportBeatorajaBmts = exportBeatorajaBmts;
        this.applyCachedPlaylistUrlCompletions = applyCachedPlaylistUrlCompletions;
        this.reOutputCustomFoldersAfterReload = reOutputCustomFoldersAfterReload;
        this.mutationAdmission = mutationAdmission;
        this.requiredReferenceApply = requiredReferenceApply;
    }

    internal BMSTable LoadExternalTable(Uri pageUri, BMSTable baseTable = null)
    {
        return LoadExternalTableAsync(pageUri, baseTable).GetAwaiter().GetResult();
    }

    /// <summary>
    /// 外部表の通信または内蔵表のローカル計算へ取消しを伝播し、完成した表を返します。
    /// </summary>
    /// <param name="pageUri">外部表の取得元。</param>
    /// <param name="baseTable">ローカル設定を引き継ぐ既存表。</param>
    /// <param name="cancellationToken">取得と取得待機を取り消すトークン。</param>
    /// <returns>取得が完了した表。取得失敗・取消しは呼出し元へ伝播します。</returns>
    internal Task<BMSTable> LoadExternalTableAsync(
        Uri pageUri,
        BMSTable baseTable = null,
        CancellationToken cancellationToken = default)
        => LoadExternalTableCoreAsync(pageUri, baseTable, cancellationToken, null);

    private async Task<BMSTable> LoadExternalTableCoreAsync(
        Uri pageUri, BMSTable baseTable, CancellationToken cancellationToken,
        Lazy<Task<WalkureScoreInput>> scoreCapture)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new ArgumentException(Resources.Error_URIMustBeAbsolute, nameof(pageUri));
        }
        if (pageUri.Scheme == "bmseeker")
        {
            return await recommendedTableOwner.LoadWalkureTableAsync(pageUri, baseTable, cancellationToken, scoreCapture).ConfigureAwait(false);
        }

        Uri originalPageUri = pageUri;
        Uri headerUri = null;
        Uri resolvedHeaderUri = null;
        Uri resolvedDataUri = null;
        BMSTable bmsTable = null;
        try
        {
            (string input, bool isJson) = await GetExternalContentAsync(pageUri, cancellationToken, detectKind: true).ConfigureAwait(false);
            string headerJson;
            if (isJson)
            {
                headerUri = pageUri;
                pageUri = null;
                resolvedHeaderUri = headerUri;
                headerJson = input;
            }
            else if (TryResolveHeaderUri(input, pageUri, out headerUri))
            {
                resolvedHeaderUri = !headerUri.IsAbsoluteUri ? new Uri(pageUri, headerUri) : headerUri;
                (headerJson, _) = await GetExternalContentAsync(resolvedHeaderUri, cancellationToken).ConfigureAwait(false);
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
            (string dataJson, _) = await GetExternalContentAsync(resolvedDataUri, cancellationToken).ConfigureAwait(false);
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

    private async Task<(string Text, bool IsJson)> GetExternalContentAsync(Uri uri, CancellationToken cancellationToken, bool detectKind = false)
    {
        // HttpClientが扱う4 schemeとローカルファイルを維持し、未対応の取得元だけを入力失敗にします。
        // 読取り全体のNotSupportedExceptionを捕捉して内部故障まで隠すことはしません。
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss" or "file"))
        {
            throw new UriFormatException("Unsupported playlist resource URI scheme: " + uri.Scheme);
        }
        // 従来の通常外部表と同じく、一資源の送信開始から本文完了まで総期限を維持する。
        // file入力も同じ上限で読むが、ローカル処理へHTTPの期限は付けない。
        using CancellationTokenSource deadline = uri.IsFile ? null : httpClient.CreateRequestDeadline();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline?.Token ?? CancellationToken.None);
        using AppHttpResponse response = await httpClient.OpenReadAsync(uri, cancellation.Token).ConfigureAwait(false);
        return await ExternalPlaylistContentReader.ReadAsync(response.ResponseStream, detectKind, cancellation.Token).ConfigureAwait(false);
    }

    /// <summary>複数表を取得します。内蔵推薦は一操作内で同じfreshなスコア結果・失敗を共有します。</summary>
    /// <param name="targets">取得元URIを持つ対象表。</param>
    /// <param name="inheritLocalTableProperties">既存表のローカル設定を生成結果へ引き継ぐか。</param>
    /// <param name="progressCallback">表ごとの進捗通知先。</param>
    /// <param name="reason">ログと後処理へ渡す理由。</param>
    /// <param name="cancellationToken">取得待機・原入力読取り・計算を取り消すトークン。</param>
    /// <param name="schedulePlaylistUrlCompletionRefresh">独立したURL補完の更新を予約するか。</param>
    /// <returns>各対象の取得結果。個別の失敗を成功表へ置き換えません。</returns>
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
        var results = new PlaylistExternalTableLoadResult[targetSnapshot.Count];
        Lazy<Task<WalkureScoreInput>> scoreCapture = recommendedTableOwner.CreateScoreCapture(cancellationToken);
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
                BMSTable externalTable = await LoadExternalTableCoreAsync(
                    uri,
                    inheritLocalTableProperties ? table : null,
                    cancellationToken, scoreCapture).ConfigureAwait(false);
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
    /// 指定した外部プレイリストを並列取得して正本へ反映し、永続化された対象の派生出力を返却前に一括で収束させます。
    /// 通信と対象捕捉より前にPを非待機取得します。
    /// 同じ受付を保存・派生出力・必要公開・通知・全開始済みTaskの終端まで保持します。
    /// 内蔵推薦は一操作内の原入力を共有し、実力変化の通知は反映成功後に行います。
    /// </summary>
    /// <param name="targets">再取得対象。</param>
    /// <param name="syncResultCallback">対象の保存と必須参照反映後に、返却する同じ確定結果を通知する任意callback。派生出力の全体失敗は含めません。</param>
    /// <param name="progressCallback">進捗を通知する callback。</param>
    /// <param name="reason">ログと派生処理へ渡す理由。</param>
    /// <param name="cancellationToken">取得待機を取り消す token。</param>
    /// <param name="requireCurrentTargetForApply">active table でなくなった対象への反映を拒否するか。</param>
    /// <param name="uriProvider">対象ごとの取得 URI override。</param>
    /// <param name="publishReferenceReceipts">reference table 更新 receipt を publish するか。</param>
    /// <param name="capability">既に受理された同じ変更の局所権限。nullの新規要求は通信前に受付を判定します。</param>
    /// <param name="outputDerivedState">この入口がLR2/BMTまで所有するか。falseでは同じPの保存サービスが結果から必要出力を行います。</param>
    /// <returns>対象ごとの確定参照・変更・失敗と、取消し・一括出力の全体失敗。取得中に退役した対象は再生成しません。</returns>
    internal async Task<PlaylistReloadOperationResult> ReloadPlaylistTargetsAsync(
        IEnumerable<BMSTable> targets,
        Action<PlaylistReloadTargetResult> syncResultCallback = null,
        Action<PlaylistSyncProgressSnapshot> progressCallback = null,
        string reason = "ReloadPlaylistTargetsAsync",
        CancellationToken cancellationToken = default,
        bool requireCurrentTargetForApply = true,
        Func<BMSTable, Uri> uriProvider = null,
        bool publishReferenceReceipts = false,
        LibraryFileMutationCapability capability = null, bool outputDerivedState = true)
    {
        using LibraryFileMutationLease admission = mutationAdmission?.Invoke(capability);
        using LibraryFileMutationCapability authority = admission?.CreateMutationCapability();
        cancellationToken.ThrowIfCancellationRequested();
        enterPlaylistUpdating?.Invoke();
        List<PlaylistReloadTargetResult> results = [];
        Exception primaryFailure = null;
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
                    if (requireCurrentTargetForApply && isActiveTable?.Invoke(table) == false)
                    {
                        throw new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                            "Playlist reload target is no longer active.");
                    }
                    Uri uri = ResolveTargetUri(table);
                    return uri != null && uri.IsAbsoluteUri;
                })];
            int completedTableCount = 0;
            Lazy<Task<WalkureScoreInput>> scoreCapture = recommendedTableOwner.CreateScoreCapture(cancellationToken);
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
                            publishReferenceReceipt: publishReferenceReceipts,
                            scoreCapture: scoreCapture).ConfigureAwait(false);
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
            catch (Exception failure) { primaryFailure = failure; }
            {
                // 取消しや出力失敗でも確定した対象結果を呼出元へ返す。
                List<PlaylistReloadTargetResult> resultSnapshot;
                lock (resultLock)
                {
                    resultSnapshot = [.. results];
                }
                List<Exception> outputFailures = [];
                if (primaryFailure != null) { outputFailures.Add(primaryFailure); }
                try { if (outputDerivedState) { ReOutputCustomFoldersForPersistedReloads(resultSnapshot, reason, authority); } }
                catch (Exception failure) { outputFailures.Add(failure); }
                if (outputDerivedState && exportBeatorajaBmts != null)
                {
                    try
                    {
                        await exportBeatorajaBmts(resultSnapshot.Where(result => result.NeedsBmtExport)
                            .Select(result => result.ResultTable).Distinct(), reason, authority).ConfigureAwait(false);
                    }
                    catch (Exception failure) { outputFailures.Add(failure); }
                }
                primaryFailure = CombineFailures(outputFailures);
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
        }
        catch (Exception failure) { primaryFailure = CombineFailures([primaryFailure, failure]); }
        finally
        {
            try { exitPlaylistUpdating?.Invoke(); }
            catch (Exception failure) { primaryFailure = CombineFailures([primaryFailure, failure]); }
        }
        return new PlaylistReloadOperationResult(results, primaryFailure);
    }

    /// <summary>通信と対象捕捉より前に局所受付を判定し、同じ権限で一表の保存・出力・必要公開の終端まで待ちます。</summary>
    /// <param name="capability">既に受理された同じ変更の生存局所権限。nullは通信前の新規非待機受付です。</param>
    /// <param name="callerOwnsDerivedOutputs">同じPの保存サービスが必要出力を所有する場合はtrue。通常単表入口はfalseです。</param>
    /// <returns>確定参照と後続失敗を別々に保持する一対象の操作結果。</returns>
    internal async Task<PlaylistReloadOperationResult> ReloadAndApplySingleTableAsync(
        BMSTable table,
        Uri pageUri,
        string reason,
        CancellationToken cancellationToken = default,
        LibraryFileMutationCapability capability = null, bool callerOwnsDerivedOutputs = false)
    {
        if (table == null)
        {
            throw new ArgumentNullException(nameof(table));
        }
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Playlist reload URI is not absolute.");
        }
        PlaylistReloadOperationResult results = await ReloadPlaylistTargetsAsync(
            [table],
            reason: reason ?? "ReloadAndApplySingleTableAsync",
            cancellationToken: cancellationToken,
            requireCurrentTargetForApply: true,
            uriProvider: _ => pageUri, capability: capability, outputDerivedState: !callerOwnsDerivedOutputs).ConfigureAwait(false);
        PlaylistReloadTargetResult result = results.SingleOrDefault();
        if (result == null)
        {
            results.ThrowIfFailed();
            throw new InvalidOperationException("Playlist reload produced no result.");
        }
        return results;
    }

    /// <summary>現在表を捕捉して取得し、最初の反映から派生出力・必要通知の実終端まで一つの局所権限で更新します。</summary>
    /// <param name="syncResultCallback">各対象の保存と必須参照反映後に、最終結果と同じ確定結果を逐次渡す任意callback。</param>
    /// <param name="capability">同じ受理済み変更の生存権限。nullは通信前に非待機で受付を判定します。</param>
    /// <returns>確定参照と対象失敗を保持した結果。更新中状態の終端通知失敗も全体失敗として採用後に渡します。</returns>
    internal async Task<PlaylistReloadOperationResult> UpdateBMSTablesInternalAsync(
        bool reloadExtPlaylist = true,
        Action<PlaylistReloadTargetResult> syncResultCallback = null,
        Action<PlaylistSyncProgressSnapshot> progressCallback = null,
        CancellationToken cancellationToken = default,
        bool publishReferenceReceipts = false,
        LibraryFileMutationCapability capability = null)
    {
        using LibraryFileMutationLease admission = mutationAdmission?.Invoke(capability);
        using LibraryFileMutationCapability authority = admission?.CreateMutationCapability();
        cancellationToken.ThrowIfCancellationRequested();
        enterPlaylistUpdating?.Invoke();
        PlaylistReloadOperationResult results = null;
        Exception workflowFailure = null;
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
            results = await ReloadPlaylistTargetsAsync(
                reloadTargets,
                syncResultCallback,
                progressCallback,
                "UpdateBMSTablesInternalAsync",
                cancellationToken,
                requireCurrentTargetForApply: true,
                publishReferenceReceipts: publishReferenceReceipts, capability: authority).ConfigureAwait(false);
            stopwatch.Stop();
            List<BMSTable> updatedTables = [.. results
                .Where(result => result.Succeeded && result.Updated && result.ResultTable != null)
                .Select(result => result.ResultTable)];
            logPerformance?.Invoke(
                "playlist_update table_count=" + tableSnapshot.Count
                + " target_count=" + reloadTargets.Count
                + " updated_count=" + updatedTables.Count
                + " total_ms=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception failure) { workflowFailure = failure; }
        finally
        {
            try { exitPlaylistUpdating?.Invoke(); }
            catch (Exception failure) { workflowFailure = CombineFailures([workflowFailure, failure]); }
        }
        if (results == null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(workflowFailure).Throw();
            throw new InvalidOperationException("Playlist reload failure propagation unexpectedly returned.");
        }
        return workflowFailure == null ? results
            : new PlaylistReloadOperationResult(results, CombineFailures([results.OperationException, workflowFailure]));
    }

    internal BMSTable RegistrateExternalTable(Uri pageUri)
    {
        return RegistrateExternalTableAsync(pageUri).GetAwaiter().GetResult();
    }

    /// <summary>URI入力の通信前に保存受付を判定し、実登録・管理出力・必要公開の終端まで待ちます。</summary>
    /// <param name="capability">同じ受理済み変更の生存局所権限。nullは通信前に新規受付を判定します。</param>
    internal async Task<BMSTable> RegistrateExternalTableAsync(
        Uri pageUri,
        CancellationToken cancellationToken = default,
        LibraryFileMutationCapability capability = null)
    {
        return await RegistrateExternalTableAsync(
            pageUri,
            renameDuplicateName: false,
            "RegistrateExternalTableAsync",
            preserveSourceUrlText: false,
            cancellationToken, capability).ConfigureAwait(false);
    }

    /// <summary>元URIの文字列契約を保って取得し、準備済み入力の登録へ生存権限を転送します。保存後の失敗と確定事実を返します。</summary>
    /// <param name="capability">既に受理された同じ変更の局所権限。nullの新規要求は通信前に受付を判定します。</param>
    internal async Task<BMSTable> RegistrateExternalTableAsync(
        Uri pageUri,
        bool renameDuplicateName,
        string reason,
        bool preserveSourceUrlText = false,
        CancellationToken cancellationToken = default,
        LibraryFileMutationCapability capability = null)
    {
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("pageUri.IsAbsoluteUri is not true");
        }
        if (!preserveSourceUrlText)
        {
            pageUri = new Uri(pageUri.AbsoluteUri, UriKind.Absolute);
        }
        using LibraryFileMutationLease admission = mutationAdmission?.Invoke(capability);
        using LibraryFileMutationCapability authority = admission?.CreateMutationCapability();
        BMSTable table = await LoadExternalTableAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await RegistrateExternalTableAsync(
            table,
            renameDuplicateName,
            reason ?? "RegistrateExternalTableAsync",
            cancellationToken, authority).ConfigureAwait(false);
    }

    internal async Task<BMSTable> RegistrateExternalTableAsync(
        BMSTable bmsTable,
        bool renameDuplicateName,
        string reason,
        CancellationToken cancellationToken = default,
        LibraryFileMutationCapability capability = null)
    {
        using LibraryFileMutationLease admission = mutationAdmission?.Invoke(capability);
        using LibraryFileMutationCapability authority = admission?.CreateMutationCapability();
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
                playlistAggregatePersistenceOwner.PrepareExternalRegistration([bmsTable], renameDuplicateName, settings).Single();
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
        List<Exception> outputFailures = [];
        try { requiredReferenceApply?.Invoke([new(null, bmsTable, true, true, null, operationReason)]); }
        catch (Exception failure) { outputFailures.Add(failure); }
        if (migrateCustomFolderOutput)
        {
            try
            {
                tryMigrateCustomFolderOutputDirectory?.Invoke(bmsTable, customFolderOutputDirectory,
                    customFolderOutputDirectory, bmsTable.is_root_folder, null, null, true, settings, authority);
            }
            catch (Exception failure) { outputFailures.Add(failure); }
        }
        if (exportBeatorajaBmt != null)
        {
            try { await exportBeatorajaBmt(bmsTable, operationReason, authority).ConfigureAwait(false); }
            catch (Exception failure) { outputFailures.Add(failure); }
        }
        if (outputFailures.Count == 1) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outputFailures[0]).Throw(); }
        if (outputFailures.Count > 1) { throw new AggregateException(outputFailures).Flatten(); }
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
        CancellationToken cancellationToken = default,
        LibraryFileMutationCapability capability = null)
    {
        using LibraryFileMutationLease admission = mutationAdmission?.Invoke(capability);
        using LibraryFileMutationCapability authority = admission?.CreateMutationCapability();
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
                playlistAggregatePersistenceOwner.PrepareExternalRegistration(tableList, renameDuplicateName, settings);
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
            cancellationToken.ThrowIfCancellationRequested();
            await CommitAndAddBMSTablesAsync(tableList).ConfigureAwait(false);
            // DBと可視集合の確定後は、単表登録と同じく必要参照・出力の実終端まで所有します。
        }
        finally
        {
            playlistAggregatePersistenceOwner.EndRegistration();
        }
        List<Exception> outputFailures = [];
        try { requiredReferenceApply?.Invoke([.. tableList.Select(table => new PlaylistTableUpdateReceipt(null, table, true, true, null, operationReason))]); }
        catch (Exception failure) { outputFailures.Add(failure); }
        foreach ((BMSTable table, string directory) target in customFolderOutputTargets)
        {
            try
            {
                tryMigrateCustomFolderOutputDirectory?.Invoke(target.table, target.directory, target.directory,
                    target.table.is_root_folder, null, null, true, settings, authority);
            }
            catch (Exception failure) { outputFailures.Add(failure); }
        }
        if (exportBeatorajaBmts != null)
        {
            try { await exportBeatorajaBmts(tableList, operationReason, authority).ConfigureAwait(false); }
            catch (Exception failure) { outputFailures.Add(failure); }
        }
        if (outputFailures.Count == 1) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outputFailures[0]).Throw(); }
        if (outputFailures.Count > 1) { throw new AggregateException(outputFailures).Flatten(); }
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
        var originalPlaylistIds = tableList.ToDictionary(table => table, table => table.playlist_id);
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
        Action<PlaylistReloadTargetResult> syncResultCallback,
        string reason,
        CancellationToken cancellationToken,
        bool requireCurrentTargetForApply = true,
        bool allowUriOverride = false,
        bool publishReferenceReceipt = false,
        Lazy<Task<WalkureScoreInput>> scoreCapture = null)
    {
        BMSTable newTable = table;
        PlaylistAggregatePersistenceOwner.PlaylistReloadPersistenceDecision persistenceDecision = null;
        bool updated = false;
        bool applySkipped = false;
        bool handledExternalLoadFailure = false;
        Uri sourceUri = null;
        Exception failure = null;
        string previousOrgName = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ensurePlaylistEntriesLoaded?.Invoke(table, reason);
            cancellationToken.ThrowIfCancellationRequested();
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                previousOrgName = table.org_name;
                sourceUri = table.Page_url ?? table.Header_url;
            }
            if (!allowUriOverride && !UriEquals(uri, sourceUri))
            {
                throw new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                    "Playlist reload request no longer matches the active playlist source URI.");
            }
            BMSTable reloadedTable;
            try
            {
                reloadedTable = await LoadExternalTableCoreAsync(uri, table, cancellationToken, scoreCapture).ConfigureAwait(false);
            }
            catch (Exception sourceFailure) when (uri.Scheme != "bmseeker"
                && !(sourceFailure is OperationCanceledException && cancellationToken.IsCancellationRequested)
                && sourceFailure is not OutOfMemoryException)
            {
                // 未採用の外部生成は原因分類によらず対象で閉じる。DB読込み・保存・公開や内蔵推薦は含めない。
                handledExternalLoadFailure = true;
                throw;
            }
            cancellationToken.ThrowIfCancellationRequested();
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                IReadOnlyList<BMSTableEntry> persistedActiveEntries = playlistAggregatePersistenceOwner.LoadPersistedActivePlaylistEntries(table.playlist_id);
                newTable = PlaylistAggregatePersistenceOwner.MergeReloadedBMSTableState(
                    table,
                    reloadedTable,
                    PlaylistAggregatePersistenceOwner.BuildComparablePlaylistEntryRows(persistedActiveEntries),
                    out persistenceDecision,
                    logLastUpdateDecision: true);
                updated = persistenceDecision.UpdatesLastUpdate;
                if (!persistenceDecision.NeedsEntryPersistence)
                {
                    if (persistenceDecision.NeedsHeaderPersistence)
                    {
                        // headerだけの実表置換は既存entry値を材料にします。参照UI専用の旧新mirrorは作りません。
                        newTable.entries = [.. (table.entries ?? []).Where(entry => entry != null).Select(entry => entry.CreatePlaylistReloadSnapshot())];
                    }
                    if (!persistenceDecision.NeedsStatePersistence)
                    {
                        newTable = table;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            (bool Applied, Exception PublicationFailure) application = await playlistAggregatePersistenceOwner.TryApplyReloadedTableAsync(
                table,
                newTable,
                persistenceDecision,
                requireCurrentTargetForApply).ConfigureAwait(false);
            if (!application.Applied)
            {
                failure = new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                    "Playlist reload result could not be applied to the active table.");
                applySkipped = true;
                newTable = table;
                persistenceDecision = null;
                updated = false;
            }
            else
            {
                failure = application.PublicationFailure;
                if (requireCurrentTargetForApply
                    && isActiveTable != null
                    && !isActiveTable(newTable))
                {
                    throw new PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException(
                        "Playlist reload result was applied to a table that is no longer active.");
                }
                InvokeResidualAction(
                    () => applyCachedPlaylistUrlCompletion?.Invoke(newTable, reason),
                    reason + ":url-completion",
                    newTable?.name);
                InvokeResidualAction(() => recommendedTableOwner.NotifyAppliedSkillChange(previousOrgName, newTable),
                    reason + ":skill-change", newTable?.name);
            }
        }
        catch (PlaylistAggregatePersistenceOwner.PlaylistReloadApplyException ex)
        {
            failure = ex;
            applySkipped = true;
            newTable = table;
            persistenceDecision = null;
            updated = false;
            logWarning?.Invoke(
                ex,
                "playlist_reload_apply_failed reason=" + FormatTextForLog(reason)
                + " table=" + FormatTextForLog(table?.name)
                + " uri=" + FormatUriForLog(uri));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 操作tokenの取消しは全体へ渡し、他対象の確定結果は一括待機後も保持する。
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
            applySkipped = true;
            newTable = table;
            persistenceDecision = null;
            updated = false;
            if (!handledExternalLoadFailure)
            {
                logWarning?.Invoke(
                    ex,
                    "playlist_reload_target_failed reason=" + FormatTextForLog(reason)
                    + " table=" + FormatTextForLog(table?.name)
                    + " uri=" + FormatUriForLog(uri));
            }
        }
        PlaylistTableUpdateReceipt updateReceipt = null;
        if (!applySkipped)
        {
            updateReceipt = new PlaylistTableUpdateReceipt(
                table,
                newTable,
                updated,
                persistenceDecision?.NeedsEntryPersistence == true,
                uri,
                reason);
            try { requiredReferenceApply?.Invoke([updateReceipt]); }
            catch (Exception referenceFailure) { failure = CombineFailures([failure, referenceFailure]); }
            if (publishReferenceReceipt)
            {
                try { PublishPlaylistTableUpdateReceipt(updateReceipt, uri); }
                catch (Exception publicationFailure) { failure = CombineFailures([failure, publicationFailure]); }
            }
        }
        var result = new PlaylistReloadTargetResult
        {
            SourceTable = table,
            ResultTable = newTable,
            Uri = uri,
            Updated = updated,
            StatePersisted = persistenceDecision?.NeedsStatePersistence == true,
            NeedsBmtExport = persistenceDecision?.NeedsBmtExport == true,
            Exception = failure,
            IsHandledExternalLoadFailure = handledExternalLoadFailure,
            UpdateReceipt = updateReceipt
        };
        InvokeSyncResultCallback(syncResultCallback, result, reason);
        return result;
    }

    private void ReOutputCustomFoldersForPersistedReloads(
        IReadOnlyList<PlaylistReloadTargetResult> results,
        string reason, LibraryFileMutationCapability capability)
    {
        if (reOutputCustomFoldersAfterReload == null)
        {
            return;
        }
        List<BMSTable> persistedTables = [.. (results ?? [])
            .Where(result => result?.StatePersisted == true
                && result.ResultTable != null)
            .Select(result => result.ResultTable)
            .Distinct()];
        if (persistedTables.Count == 0)
        {
            return;
        }
        reOutputCustomFoldersAfterReload(persistedTables, reason, capability);
    }

    private void PublishPlaylistTableUpdateReceipt(
        PlaylistTableUpdateReceipt receipt,
        Uri uri)
    {
        if (receipt == null)
        {
            return;
        }
        List<Exception> failures = [];
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
                failures.Add(ex);
                logWarning?.Invoke(
                    ex,
                    "playlist_update_receipt_consumer_failed table="
                    + FormatTextForLog(receipt.NewTable?.name)
                    + " uri="
                    + FormatUriForLog(uri));
            }
        }
        if (failures.Count == 1) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
        if (failures.Count > 1) { throw new AggregateException(failures).Flatten(); }
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
        Action<PlaylistReloadTargetResult> syncResultCallback,
        PlaylistReloadTargetResult result,
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
                + " uri=" + FormatUriForLog(result?.Uri));
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
            Uri uri,
            string reason)
        {
            OldTable = oldTable;
            NewTable = newTable;
            Updated = updated;
            ReferenceEntriesChanged = referenceEntriesChanged;
            Uri = uri;
            Reason = reason ?? string.Empty;
        }

        internal BMSTable NewTable { get; }

        internal bool Updated { get; }

        internal bool ReferenceEntriesChanged { get; }

        internal BMSTable OldTable { get; }

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

    /// <summary>確定した対象一覧と、取消し・一括出力など対象へ割当てられない失敗を返します。</summary>
    internal sealed class PlaylistReloadOperationResult : IReadOnlyList<PlaylistReloadTargetResult>
    {
        private readonly IReadOnlyList<PlaylistReloadTargetResult> targets;

        /// <summary>対象の確定結果を固定し、対象別に割当てない全体失敗を保持します。</summary>
        internal PlaylistReloadOperationResult(IReadOnlyList<PlaylistReloadTargetResult> targets, Exception operationException)
        {
            this.targets = targets.ToArray();
            OperationException = operationException;
        }

        /// <summary>操作全体の後続失敗。対象別失敗は各結果に保持します。</summary>
        internal Exception OperationException { get; }

        /// <summary>処理済み外部取得失敗を除き、確定情報を採用した呼出元へ渡す未処理の対象失敗と全体失敗です。</summary>
        internal Exception Exception => CombineFailures(targets
            .Where(target => !target.IsHandledExternalLoadFailure)
            .Select(target => target.Exception).Append(OperationException));

        /// <summary>固定された対象結果一覧の件数を取得します。</summary>
        public int Count => targets.Count;

        /// <summary>固定された対象結果一覧から指定位置の結果を取得します。</summary>
        /// <param name="index">対象結果一覧内の位置です。</param>
        /// <returns>指定位置の対象結果です。</returns>
        public PlaylistReloadTargetResult this[int index] => targets[index];

        /// <summary>固定された対象結果一覧を列挙します。</summary>
        /// <returns>対象結果一覧の列挙子です。</returns>
        public IEnumerator<PlaylistReloadTargetResult> GetEnumerator() => targets.GetEnumerator();

        /// <summary>固定された対象結果一覧を非ジェネリックな列挙子で列挙します。</summary>
        /// <returns>対象結果一覧の列挙子です。</returns>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>参照・変更の採用後に未処理失敗だけを通知境界へ伝播し、処理済み取得失敗は各対象に残します。</summary>
        internal void ThrowIfFailed()
        {
            if (Exception is Exception failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        }
    }

    private static Exception CombineFailures(IEnumerable<Exception> failures)
    {
        Exception[] actual = [.. failures.Where(failure => failure != null)];
        return actual.Length switch { 0 => null, 1 => actual[0], _ => new AggregateException(actual).Flatten() };
    }

    /// <summary>
    /// 保存と必須参照反映後の一対象の確定結果です。同じインスタンスを逐次通知と最終集約で共有し、
    /// 後続する一括派生出力の失敗では対象の成否を書き換えません。
    /// </summary>
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

        /// <summary>確定した変更から同じPでBMT出力する必要があるか。</summary>
        internal bool NeedsBmtExport { get; init; }

        internal Exception Exception { get; init; }

        /// <summary>
        /// 外部取得境界で処理済みの対象失敗か。STATUSへ記録し、バッチから再送出しません。
        /// 元例外は単表プロパティ保存の既存契約に残し、保存・公開の失敗と区別します。
        /// </summary>
        internal bool IsHandledExternalLoadFailure { get; init; }

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
            // 取得済み本文だけを解析し、parserへの通信委譲でHTTP ownerの期限・取消しを迂回させない。
            using (var reader = new SgmlReader
            {
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
