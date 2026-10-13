using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ribbit.Logging;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// playlist lamp data source の更新通知です。
/// </summary>
internal sealed class PlaylistLampViewerSourceChangedEventArgs : EventArgs
{
    /// <summary>
    /// 更新通知を生成します。
    /// </summary>
    /// <param name="playlistId">更新対象 playlist identity。null/空は全 playlist。</param>
    /// <param name="sequence">source 内で単調増加する通知 sequence。</param>
    /// <param name="dependencyStamp">通知時点の依存 version。</param>
    /// <param name="sourceIdentity">通知元 score source identity。</param>
    public PlaylistLampViewerSourceChangedEventArgs(
        string playlistId,
        long sequence,
        PlaylistLampDependencyStamp dependencyStamp = default,
        string sourceIdentity = null)
    {
        PlaylistId = playlistId ?? string.Empty;
        Sequence = sequence;
        DependencyStamp = dependencyStamp;
        SourceIdentity = sourceIdentity ?? string.Empty;
    }

    /// <summary>更新対象 playlist identity。</summary>
    public string PlaylistId { get; }

    /// <summary>source 通知 sequence。</summary>
    public long Sequence { get; }

    /// <summary>通知時点の依存 version。</summary>
    public PlaylistLampDependencyStamp DependencyStamp { get; }

    /// <summary>通知元 score source の immutable identity。</summary>
    public string SourceIdentity { get; }
}

/// <summary>
/// ランプビューアの immutable input を提供する narrow source contract です。
/// </summary>
internal interface IPlaylistLampViewerDataSource
{
    /// <summary>source の更新通知。</summary>
    event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed;

    /// <summary>
    /// 指定 query の immutable snapshot を取得します。
    /// </summary>
    /// <param name="query">playlist identity と任意の historical local date。</param>
    /// <param name="cancellationToken">取得を中断する token。</param>
    /// <returns>immutable aggregation request。</returns>
    ValueTask<PlaylistLampAggregationRequest> CaptureAsync(PlaylistLampViewerQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// session build 実行の narrow seam です。
/// production は default executor を使い、テストは deterministic gate を差し替えます。
/// </summary>
internal interface IPlaylistLampViewerBuildExecutor
{
    /// <summary>
    /// aggregation を実行します。
    /// </summary>
    /// <param name="request">immutable aggregation request。</param>
    /// <param name="aggregationService">純粋 aggregation service。</param>
    /// <param name="cancellationToken">build を中断する token。</param>
    /// <returns>aggregation result。</returns>
    Task<PlaylistLampAggregationResult> BuildAsync(
        PlaylistLampAggregationRequest request,
        PlaylistLampAggregationService aggregationService,
        CancellationToken cancellationToken);
}

/// <summary>
/// production session で pure aggregation を background work として実行します。
/// </summary>
internal sealed class PlaylistLampViewerDefaultBuildExecutor : IPlaylistLampViewerBuildExecutor
{
    /// <inheritdoc />
    public Task<PlaylistLampAggregationResult> BuildAsync(
        PlaylistLampAggregationRequest request,
        PlaylistLampAggregationService aggregationService,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => aggregationService.Aggregate(request, cancellationToken),
            cancellationToken);
    }
}

/// <summary>
/// playlist lamp の最新 immutable result を所有し、source 通知ごとに generation を更新します。
/// </summary>
internal sealed class PlaylistLampViewerSession : IDisposable, IAsyncDisposable
{
    private readonly object stateGate = new();

    private readonly string playlistId;

    private readonly IPlaylistLampViewerDataSource dataSource;

    private readonly PlaylistLampAggregationService aggregationService;

    private readonly IPlaylistLampViewerBuildExecutor buildExecutor;

    private PlaylistLampAggregationResult current;

    private PlaylistLampViewerQuery query;

    private RefreshCancellation activeBuildCancellation;

    private long generation;

    private long sourceInvalidationVersion;

    private long lastNotificationSequence;

    private int disposed;

    private readonly HashSet<RefreshOperation> refreshes = [];

    private Task stopTask;

    private Exception cleanupFailure;

    // 受付の時点で予約し、キュー待ち・supersede済みも実finallyまで所有します。
    private sealed class RefreshOperation
    {
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly RefreshCancellation Cancellation = new();
        internal RefreshCancellation PreviousCancellation;
        internal long Generation;
        internal long InvalidationVersion;
        internal PlaylistLampViewerQuery Query;
        internal string FailureRoute;
    }

    /// <summary>
    /// refresh専有の取消源です。取消要求は捕捉・集計へ即時に反映し、登録済みcallbackは非同期に実行します。
    /// refreshのfinallyがcallbackの終端を待ってから取消登録と専有CTSを解放します。
    /// </summary>
    private sealed class RefreshCancellation
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource source = new();
        private Task cancellationTask = Task.CompletedTask;
        private bool finished;
        private CancellationTokenRegistration callerRegistration;

        /// <summary>呼出元の取消しを専有取消元へ転送し、捕捉・集計に共通のトークンを返します。</summary>
        /// <param name="callerToken">呼出元の取消要求。</param>
        /// <returns>取消状態を即時に共有し、callbackの終端をこの管理主体が所有するトークン。</returns>
        internal CancellationToken RegisterCallerCancellation(CancellationToken callerToken)
        {
            callerRegistration = callerToken.Register(RequestCancellation);
            // 連結CTSを挟むと、CancelAsyncのcallbackが走るまで捕捉側へ取消状態が届きません。
            return source.Token;
        }

        /// <summary>取消状態を即時に成立させ、非同期callbackの実終端を一度だけ所有します。</summary>
        internal void RequestCancellation()
        {
            lock (gate)
            {
                if (finished || source.IsCancellationRequested) { return; }
                // 取消状態と実callbackのTaskを一緒に公開します。CancelAsyncはcallbackを同期実行しません。
                cancellationTask = source.CancelAsync();
            }
        }

        /// <summary>新たな取消しを止め、開始済みcallbackの終端後に取消登録と専有取消元を解放します。</summary>
        /// <returns>必要な解放を全て試みた結果。</returns>
        internal async Task FinishAsync()
        {
            Task cancellation;
            lock (gate)
            {
                finished = true;
                cancellation = cancellationTask;
            }
            var failures = new List<Exception>();
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
            try { callerRegistration.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { source.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count > 0) { throw new AggregateException(failures); }
        }
    }

    /// <summary>
    /// session を生成します。
    /// </summary>
    /// <param name="playlistId">安定した playlist identity。</param>
    /// <param name="dataSource">snapshot source。</param>
    /// <param name="aggregationService">pure aggregation service。</param>
    /// <param name="buildExecutor">任意の deterministic build executor。</param>
    public PlaylistLampViewerSession(
        string playlistId,
        IPlaylistLampViewerDataSource dataSource,
        PlaylistLampAggregationService aggregationService = null,
        IPlaylistLampViewerBuildExecutor buildExecutor = null)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            throw new ArgumentException("playlistId is required.", nameof(playlistId));
        }
        this.playlistId = playlistId;
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.aggregationService = aggregationService ?? new PlaylistLampAggregationService();
        this.buildExecutor = buildExecutor ?? new PlaylistLampViewerDefaultBuildExecutor();
        query = PlaylistLampViewerQuery.Latest(playlistId);
        current = new PlaylistLampAggregationResult(
            playlistId,
            PlaylistLampViewerState.Loading,
            [],
            [],
            [],
            new PlaylistLampStatistics(
                0,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                null),
            null,
            string.Empty);
        dataSource.Changed += DataSourceChanged;
    }

    /// <summary>session が管理する安定 playlist identity。</summary>
    public string PlaylistId => playlistId;

    /// <summary>現在の immutable query。null date は Latest を表します。</summary>
    public PlaylistLampViewerQuery Query
    {
        get
        {
            lock (stateGate)
            {
                return query;
            }
        }
    }

    /// <summary>現在の generation。</summary>
    public long Generation
    {
        get
        {
            lock (stateGate)
            {
                return generation;
            }
        }
    }

    /// <summary>最新 result。取得時点の immutable 値です。</summary>
    public PlaylistLampAggregationResult Current
    {
        get
        {
            lock (stateGate)
            {
                return current;
            }
        }
    }

    /// <summary>result が更新されたときに発生します。</summary>
    public event EventHandler<PlaylistLampAggregationResultChangedEventArgs> ResultChanged;

    /// <summary>
    /// 初回 snapshot の読み込みを開始します。
    /// </summary>
    /// <param name="cancellationToken">呼び出し側が待機を中断する token。</param>
    /// <returns>この refresh の完了 task。</returns>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        return RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// query を置き換え、前の build を supersede して再取得します。
    /// </summary>
    /// <param name="nextQuery">新しい immutable query。</param>
    /// <param name="cancellationToken">呼び出し側が待機を中断する token。</param>
    /// <returns>置き換え後 refresh の完了 task。</returns>
    public Task UpdateQueryAsync(
        PlaylistLampViewerQuery nextQuery,
        CancellationToken cancellationToken = default)
    {
        return UpdateQueryCore(nextQuery, cancellationToken, failureRoute: null);
    }

    private Task UpdateQueryCore(
        PlaylistLampViewerQuery nextQuery,
        CancellationToken cancellationToken,
        string failureRoute)
    {
        ArgumentNullException.ThrowIfNull(nextQuery);
        if (!string.Equals(nextQuery.PlaylistId, playlistId, StringComparison.Ordinal))
        {
            throw new ArgumentException("query playlistId does not match the session.", nameof(nextQuery));
        }
        RefreshOperation operation;
        lock (stateGate)
        {
            if (disposed != 0) { return Task.CompletedTask; }
            query = nextQuery;
            ++sourceInvalidationVersion;
            operation = ReserveRefresh(failureRoute);
        }
        _ = RunRefreshAsync(operation, cancellationToken);
        return operation.Completion.Task;
    }

    /// <summary>
    /// selected local date を query として置き換えます。null は Latest です。
    /// </summary>
    /// <param name="selectedLocalDate">選択日、または null。</param>
    /// <param name="cancellationToken">呼び出し側が待機を中断する token。</param>
    /// <returns>置き換え後 refresh の完了 task。</returns>
    public Task UpdateSelectedAsOfDateAsync(
        DateTime? selectedLocalDate,
        CancellationToken cancellationToken = default)
    {
        return UpdateQueryAsync(
            new PlaylistLampViewerQuery(playlistId, selectedLocalDate),
            cancellationToken);
    }

    /// <summary>
    /// VMの日付変更を一方向で受理します。Taskを返さない画面入口の故障診断も、
    /// sessionがrefreshの実終端より前に所有し、別の通知継続を残しません。
    /// </summary>
    /// <param name="selectedLocalDate">選択日、またはLatestを表すnull。</param>
    internal void RequestSelectedAsOfDate(DateTime? selectedLocalDate)
    {
        _ = UpdateQueryCore(
            new PlaylistLampViewerQuery(playlistId, selectedLocalDate),
            default,
            "PlaylistLampViewerViewModel.SelectedAsOfDate");
    }

    /// <summary>
    /// 現在 generation を supersede して snapshot を再構築します。
    /// </summary>
    /// <param name="cancellationToken">呼び出し側が待機を中断する token。</param>
    /// <returns>この refresh の完了 task。</returns>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshOperation operation;
        lock (stateGate)
        {
            if (disposed != 0) { return Task.CompletedTask; }
            operation = ReserveRefresh();
        }
        _ = RunRefreshAsync(operation, cancellationToken);
        return operation.Completion.Task;
    }

    // stateGate内からだけ呼びます。外部source・publish・取消を呼ぶ前に所有を成立させます。
    private RefreshOperation ReserveRefresh(string failureRoute = null)
    {
        var operation = new RefreshOperation
        {
            Generation = ++generation,
            InvalidationVersion = sourceInvalidationVersion,
            Query = query,
            FailureRoute = failureRoute,
            PreviousCancellation = activeBuildCancellation
        };
        activeBuildCancellation = operation.Cancellation;
        refreshes.Add(operation);
        return operation;
    }

    private async Task RunRefreshAsync(RefreshOperation operation, CancellationToken cancellationToken)
    {
        long requestedGeneration = operation.Generation;
        long requestedInvalidationVersion = operation.InvalidationVersion;
        PlaylistLampViewerQuery requestedQuery = operation.Query;
        RefreshCancellation buildCancellation = operation.Cancellation;
        CancellationToken refreshCancellationToken = default;
        Exception operationFailure = null;
        try
        {
            operation.PreviousCancellation?.RequestCancellation();
            PublishLoading(requestedGeneration, requestedInvalidationVersion, requestedQuery);
            refreshCancellationToken = buildCancellation.RegisterCallerCancellation(cancellationToken);
            PlaylistLampAggregationRequest request = await dataSource
                .CaptureAsync(requestedQuery, refreshCancellationToken)
                .ConfigureAwait(false);
            refreshCancellationToken.ThrowIfCancellationRequested();
            if (request == null)
            {
                request = PlaylistLampAggregationRequest.Failed(
                    playlistId,
                    "data source returned no snapshot.",
                    query: requestedQuery);
            }
            else if (!string.Equals(request.PlaylistId, playlistId, StringComparison.Ordinal))
            {
                request = PlaylistLampAggregationRequest.Failed(
                    playlistId,
                    "data source returned a snapshot for another playlist.",
                    query: requestedQuery);
            }
            else if (!requestedQuery.Equals(request.Query))
            {
                request = PlaylistLampAggregationRequest.Failed(
                    playlistId,
                    "data source returned a snapshot for another query.",
                    query: requestedQuery);
            }
            PlaylistLampAggregationResult result = await buildExecutor
                .BuildAsync(request, aggregationService, refreshCancellationToken)
                .ConfigureAwait(false);
            TryPublish(requestedGeneration, requestedInvalidationVersion, result);
        }
        catch (OperationCanceledException) when (refreshCancellationToken.IsCancellationRequested)
        {
            // supersedeまたは呼出元取消の結果は公開しません。
        }
        catch (Exception exception)
        {
            // 表示用メッセージへ縮約する前に、取得・集計の元例外をこの境界で診断します。
            TryLogFailure(exception, "playlist_lamp_refresh_failed playlistId=" + playlistId
                + " generation=" + requestedGeneration);
            try
            {
                var failedRequest = PlaylistLampAggregationRequest.Failed(
                    playlistId,
                    DescribeFailure(exception),
                    query: requestedQuery);
                PlaylistLampAggregationResult failedResult = aggregationService.Aggregate(failedRequest);
                TryPublish(requestedGeneration, requestedInvalidationVersion, failedResult);
            }
            catch (Exception failure) { operationFailure = failure; }
        }
        finally
        {
            Exception failure = null;
            try { await buildCancellation.FinishAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure = exception; }
            Exception completionFailure = operationFailure == null ? failure
                : failure == null ? operationFailure : new AggregateException(operationFailure, failure);
            if (completionFailure != null && operation.FailureRoute != null)
            {
                TryLogFailure(completionFailure, operation.FailureRoute);
            }
            lock (stateGate)
            {
                if (ReferenceEquals(activeBuildCancellation, buildCancellation)) { activeBuildCancellation = null; }
                if (failure != null)
                {
                    cleanupFailure = cleanupFailure == null ? failure : new AggregateException(cleanupFailure, failure);
                }
                if (completionFailure == null) { operation.Completion.TrySetResult(); }
                else
                {
                    operation.Completion.TrySetException(completionFailure);
                    if (operation.FailureRoute != null) { _ = operation.Completion.Task.Exception; }
                }
                refreshes.Remove(operation);
            }
        }
    }

    /// <summary>sessionが新しい要求の受付を停止済みかどうかです。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// 新規受付と公開を即時停止し、全受理refresh・購読解除・取消callbackの回収を一度要求します。
    /// callbackの実行や進行中captureを同期的に待ちません。
    /// </summary>
    public void Dispose()
    {
        TaskCompletionSource completion;
        RefreshOperation[] accepted;
        lock (stateGate)
        {
            if (stopTask != null) { return; }
            disposed = 1;
            ++generation;
            ++sourceInvalidationVersion;
            activeBuildCancellation = null;
            accepted = refreshes.ToArray();
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            ResultChanged = null;
        }
        foreach (RefreshOperation operation in accepted) { operation.Cancellation.RequestCancellation(); }
        _ = Task.Run(() => StopCoreAsync(accepted, completion));
    }

    /// <summary>
    /// 受付を停止し、queued/supersededを含む全refresh・取消callback・CTS解放と購読解除を待ちます。
    /// 未確認の必須解放は、後続の待機でも同じ失敗として保持します。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (stateGate) { return new ValueTask(stopTask); }
    }

    private async Task StopCoreAsync(RefreshOperation[] accepted, TaskCompletionSource completion)
    {
        Exception unsubscribeFailure = null;
        try { dataSource.Changed -= DataSourceChanged; }
        catch (Exception exception) { unsubscribeFailure = exception; }
        try { await Task.WhenAll(accepted.Select(operation => operation.Completion.Task)).ConfigureAwait(false); }
        catch { /* 各refreshの必須回収故障はcleanupFailureが保持します。 */ }
        lock (stateGate)
        {
            if (unsubscribeFailure != null)
            {
                cleanupFailure = cleanupFailure == null ? unsubscribeFailure : new AggregateException(cleanupFailure, unsubscribeFailure);
            }
            if (cleanupFailure == null) { completion.TrySetResult(); }
            else { completion.TrySetException(new AggregateException(cleanupFailure)); }
        }
    }

    private void DataSourceChanged(object sender, PlaylistLampViewerSourceChangedEventArgs e)
    {
        if (e == null || (!string.IsNullOrWhiteSpace(e.PlaylistId)
            && !string.Equals(e.PlaylistId, playlistId, StringComparison.Ordinal))) { return; }
        RefreshOperation operation;
        lock (stateGate)
        {
            if (disposed != 0 || (e.Sequence > 0 && e.Sequence <= lastNotificationSequence)) { return; }
            if (e.Sequence > lastNotificationSequence) { lastNotificationSequence = e.Sequence; }
            ++sourceInvalidationVersion;
            operation = ReserveRefresh("PlaylistLampViewerSession.DataSourceChanged");
        }
        // 発火元がmodel writerを保持していても、capture・取消・通知先はそのstack上で動かしません。
        _ = Task.Run(() => RunRefreshAsync(operation, default));
    }

    private void PublishLoading(
        long requestedGeneration,
        long requestedInvalidationVersion,
        PlaylistLampViewerQuery requestedQuery)
    {
        PlaylistLampAggregationResult loading = new(
            playlistId,
            PlaylistLampViewerState.Loading,
            [],
            [],
            [],
            new PlaylistLampStatistics(
                0,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                false,
                null,
                null),
            null,
            string.Empty,
            requestedQuery);
        TryPublish(requestedGeneration, requestedInvalidationVersion, loading);
    }

    private void TryPublish(
        long requestedGeneration,
        long requestedInvalidationVersion,
        PlaylistLampAggregationResult result)
    {
        if (result == null)
        {
            return;
        }
        EventHandler<PlaylistLampAggregationResultChangedEventArgs> handler;
        lock (stateGate)
        {
            if (Volatile.Read(ref disposed) != 0
                || requestedGeneration != generation
                || requestedInvalidationVersion != sourceInvalidationVersion)
            {
                return;
            }
            current = result;
            handler = ResultChanged;
        }
        handler?.Invoke(this, new PlaylistLampAggregationResultChangedEventArgs(requestedGeneration, result));
    }

    private static void TryLogFailure(Exception exception, string route)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, route); }
        catch { /* 任意診断はrefreshの結果や必須解放の成否を変えません。 */ }
    }

    private static string DescribeFailure(Exception exception)
    {
        string message = exception?.Message?.Trim();
        return string.IsNullOrWhiteSpace(message)
            ? exception?.GetType().Name ?? "lamp snapshot build failed."
            : exception.GetType().Name + ": " + message;
    }
}

/// <summary>
/// session が新しい aggregation result を受理した通知です。
/// </summary>
internal sealed class PlaylistLampAggregationResultChangedEventArgs : EventArgs
{
    /// <summary>通知を生成します。</summary>
    /// <param name="generation">受理された generation。</param>
    /// <param name="result">受理された immutable result。</param>
    public PlaylistLampAggregationResultChangedEventArgs(long generation, PlaylistLampAggregationResult result)
    {
        Generation = generation;
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>受理された generation。</summary>
    public long Generation { get; }

    /// <summary>受理された immutable result。</summary>
    public PlaylistLampAggregationResult Result { get; }
}
