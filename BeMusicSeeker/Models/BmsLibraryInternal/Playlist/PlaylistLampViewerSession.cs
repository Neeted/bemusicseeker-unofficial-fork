using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

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
internal sealed class PlaylistLampViewerSession : IDisposable
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

    /// <summary>
    /// 一つの refresh が所有する cancellation source です。
    /// superseder は cancel だけを要求し、underlying CTS の dispose は所有 refresh の finally
    /// に限定します。cancel callback と dispose の競合時にも callback の外で安全に dispose します。
    /// </summary>
    private sealed class RefreshCancellation : IDisposable
    {
        private readonly object gate = new();

        private readonly CancellationTokenSource source = new();

        private bool cancellationRequested;

        private bool cancellationRunning;

        private bool disposed;

        private bool disposeAfterCancellation;

        private bool sourceDisposed;

        internal CancellationTokenSource CreateLinkedTokenSource(CancellationToken callerCancellationToken)
        {
            lock (gate)
            {
                if (disposed)
                {
                    throw new ObjectDisposedException(nameof(RefreshCancellation));
                }
                return CancellationTokenSource.CreateLinkedTokenSource(source.Token, callerCancellationToken);
            }
        }

        internal void Cancel()
        {
            lock (gate)
            {
                if (disposed || sourceDisposed || cancellationRequested)
                {
                    return;
                }
                cancellationRequested = true;
                cancellationRunning = true;
            }

            try
            {
                source.Cancel();
            }
            finally
            {
                bool disposeSource;
                lock (gate)
                {
                    cancellationRunning = false;
                    disposeSource = disposeAfterCancellation && !sourceDisposed;
                    disposeAfterCancellation = false;
                    if (disposeSource)
                    {
                        sourceDisposed = true;
                    }
                }
                if (disposeSource)
                {
                    source.Dispose();
                }
            }
        }

        public void Dispose()
        {
            bool disposeSource;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                disposeSource = !cancellationRunning && !sourceDisposed;
                if (disposeSource)
                {
                    sourceDisposed = true;
                }
                else if (cancellationRunning)
                {
                    disposeAfterCancellation = true;
                }
            }
            if (disposeSource)
            {
                source.Dispose();
            }
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
        ArgumentNullException.ThrowIfNull(nextQuery);
        if (!string.Equals(nextQuery.PlaylistId, playlistId, StringComparison.Ordinal))
        {
            throw new ArgumentException("query playlistId does not match the session.", nameof(nextQuery));
        }
        lock (stateGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return Task.CompletedTask;
            }
            query = nextQuery;
            ++sourceInvalidationVersion;
        }
        return RefreshAsync(cancellationToken);
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
    /// 現在 generation を supersede して snapshot を再構築します。
    /// </summary>
    /// <param name="cancellationToken">呼び出し側が待機を中断する token。</param>
    /// <returns>この refresh の完了 task。</returns>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCancellation previousCancellation;
        RefreshCancellation buildCancellation;
        long requestedGeneration;
        long requestedInvalidationVersion;
        PlaylistLampViewerQuery requestedQuery;
        lock (stateGate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }
            requestedGeneration = ++generation;
            requestedInvalidationVersion = sourceInvalidationVersion;
            requestedQuery = query;
            previousCancellation = activeBuildCancellation;
            buildCancellation = new RefreshCancellation();
            activeBuildCancellation = buildCancellation;
        }
        CancellationTokenSource linkedCancellation = null;
        CancellationToken linkedCancellationToken = default;
        try
        {
            previousCancellation?.Cancel();
            PublishLoading(requestedGeneration, requestedInvalidationVersion, requestedQuery);
            linkedCancellation = buildCancellation.CreateLinkedTokenSource(cancellationToken);
            linkedCancellationToken = linkedCancellation.Token;
            PlaylistLampAggregationRequest request = await dataSource
                .CaptureAsync(requestedQuery, linkedCancellationToken)
                .ConfigureAwait(false);
            linkedCancellationToken.ThrowIfCancellationRequested();
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
                .BuildAsync(request, aggregationService, linkedCancellationToken)
                .ConfigureAwait(false);
            TryPublish(requestedGeneration, requestedInvalidationVersion, result);
        }
        catch (OperationCanceledException) when (linkedCancellationToken.IsCancellationRequested)
        {
            // A superseded or caller-cancelled build has no result to publish.
        }
        catch (Exception exception)
        {
            var failedRequest = PlaylistLampAggregationRequest.Failed(
                playlistId,
                DescribeFailure(exception),
                query: requestedQuery);
            PlaylistLampAggregationResult failedResult = aggregationService.Aggregate(failedRequest);
            TryPublish(requestedGeneration, requestedInvalidationVersion, failedResult);
        }
        finally
        {
            linkedCancellation?.Dispose();
            lock (stateGate)
            {
                if (requestedGeneration == generation
                    && ReferenceEquals(activeBuildCancellation, buildCancellation))
                {
                    activeBuildCancellation = null;
                }
            }
            buildCancellation.Dispose();
        }
    }

    /// <summary>session が破棄済みかどうかです。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// source subscription と進行中 build を一度だけ解除します。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        RefreshCancellation cancellation;
        lock (stateGate)
        {
            ++generation;
            ++sourceInvalidationVersion;
            cancellation = activeBuildCancellation;
            activeBuildCancellation = null;
        }
        dataSource.Changed -= DataSourceChanged;
        cancellation?.Cancel();
        ResultChanged = null;
    }

    private void DataSourceChanged(object sender, PlaylistLampViewerSourceChangedEventArgs e)
    {
        if (Volatile.Read(ref disposed) != 0 || e == null)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(e.PlaylistId)
            && !string.Equals(e.PlaylistId, playlistId, StringComparison.Ordinal))
        {
            return;
        }
        lock (stateGate)
        {
            if (e.Sequence > 0 && e.Sequence <= lastNotificationSequence)
            {
                return;
            }
            if (e.Sequence > lastNotificationSequence)
            {
                lastNotificationSequence = e.Sequence;
            }
            // Invalidate the current build before scheduling capture.  A source can publish
            // while its owner still holds a write lock, so the actual refresh (and its
            // cancellation of the previous build) is intentionally deferred below.
            ++sourceInvalidationVersion;
        }
        // Source notifications can originate while a table/library writer owns its lock.
        // Defer the refresh so snapshot capture and result subscribers never run inline on
        // that publication stack.
        Task.Run(() => RefreshAsync()).ObserveFault("PlaylistLampViewerSession.DataSourceChanged");
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
