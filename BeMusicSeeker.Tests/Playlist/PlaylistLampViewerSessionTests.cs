using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>ランプ集計セッションの最新要求・状態公開・処理終了を検証する。</summary>
/// <remarks>集計完了を制御して取消に応じない旧処理の公開拒否を観測する。DBの履歴復元と実ウィンドウの表示・閉鎖は別の情報源・表示ケースへ分担する。</remarks>
[TestClass]
public sealed class PlaylistLampViewerSessionTests
{
    private static readonly DateTime PlaylistUpdatedAt = new(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc);

    private static readonly DateTime ScoreUpdatedAt = new(2026, 8, 28, 2, 3, 4, DateTimeKind.Utc);

    /// <summary>Loading通知の中でセッションを破棄しても開始処理を正常に回収し、新たな受付を停止する。取消源の早期破棄によるObjectDisposedExceptionと処理放置を検出する。</summary>
    [TestMethod]
    public async Task Session_disposeDuringLoadingKeepsCancellationAliveUntilRefreshCompletes()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var session = new PlaylistLampViewerSession("playlist", source);
        TaskCompletionSource<bool> loadingPublished = NewCompletion<bool>();
        session.ResultChanged += (_, args) =>
        {
            if (args.Result.State == PlaylistLampViewerState.Loading)
            {
                session.Dispose();
                loadingPublished.TrySetResult(true);
            }
        };

        try
        {
            await session.StartAsync();
            await loadingPublished.Task;
            await session.DisposeAsync();

            Assert.IsTrue(session.IsDisposed);
            Assert.AreEqual(1, source.CaptureCount);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    /// <summary>削除・所属読取り失敗は操作不可、スコアだけの障害は所持数付きReadyとする。独立した初回要求で空結果との混同と障害の停止範囲を検証する。</summary>
    [TestMethod]
    [DataRow(0)] // Deletedは削除済みとして公開。
    [DataRow(1)] // 所属取得失敗はFailedとして公開。
    [DataRow(2)] // スコアだけの失敗はReadyの所持情報を維持。
    public async Task Session_preservesDeletedFailedAndDegradedStates(int condition)
    {
        PlaylistLampAggregationRequest request = condition switch
        {
            0 => PlaylistLampAggregationRequest.Deleted("playlist"),
            1 => PlaylistLampAggregationRequest.Failed("playlist", "entry load failed"),
            _ => ReadyRequest("playlist", 3, 2, scoreSnapshot: new PlaylistLampScoreSnapshot(
                ActiveScoreSource.Lr2, ScoreTableLoadStatus.Failed, 8, 9, null, failureMessage: "score db failed"))
        };
        await using var session = new PlaylistLampViewerSession("playlist", new FakeLampSource(request));
        await session.StartAsync();
        Assert.AreEqual(condition == 0 ? PlaylistLampViewerState.Deleted : condition == 1 ? PlaylistLampViewerState.Failed : PlaylistLampViewerState.Ready, session.Current.State);
        Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);
        if (condition == 1) { StringAssert.Contains(session.Current.FailureMessage, "entry load failed"); }
        if (condition == 2)
        {
            Assert.AreEqual(2, session.Current.Statistics.TotalCount);
            Assert.AreEqual(2, session.Current.Statistics.OwnedCount);
            Assert.AreEqual(0, session.Current.Statistics.MissingCount);
            Assert.IsNull(session.Current.Statistics.PlayedCount);
            Assert.IsNull(session.Current.Statistics.UnplayedCount);
            Assert.IsNull(session.Current.Statistics.PlayRate);
            Assert.IsFalse(session.Current.ClearSegments.Any(segment => segment.IsInvokable));
        }
    }

    /// <summary>入力捕捉の元例外をFailedと診断へ変換して操作を禁止する。捕捉障害を正常な空プレイリストとして公開する誤りを検出する。</summary>
    [TestMethod]
    public async Task Session_translatesCaptureFailureToFailedInsteadOfEmpty()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1))
        {
            CaptureFailure = new InvalidOperationException("entry snapshot unavailable")
        };
        await using var session = new PlaylistLampViewerSession("playlist", source);

        await session.StartAsync();

        Assert.AreEqual(PlaylistLampViewerState.Failed, session.Current.State);
        Assert.AreEqual(0, session.Current.Statistics.TotalCount);
        StringAssert.Contains(session.Current.FailureMessage, "entry snapshot unavailable");
        Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);
    }

    /// <summary>異なる基準日の連続要求を捕捉し、最新日を公開する。同日再設定と最新中の最新再設定では捕捉を増やさず、再実行の追加と要求の巻戻しを検出する。</summary>
    [TestMethod]
    public async Task Session_queryUpdateKeepsTheLatestImmutableQuery()
    {
        var source = new QueryAwareLampSource();
        await using var session = new PlaylistLampViewerSession("playlist", source);

        await session.StartAsync();
        DateTime firstDate = new(2026, 8, 27);
        DateTime secondDate = new(2026, 8, 28);
        Task first = session.UpdateSelectedAsOfDateAsync(firstDate);
        Task second = session.UpdateSelectedAsOfDateAsync(secondDate);
        await Task.WhenAll(first, second);

        Assert.AreEqual(secondDate, session.Query.SelectedLocalDate);
        Assert.AreEqual(secondDate, session.Current.Query.SelectedLocalDate);
        Assert.IsTrue(source.CapturedQueries.Exists(query => query.SelectedLocalDate == firstDate));
        Assert.IsTrue(source.CapturedQueries.Exists(query => query.SelectedLocalDate == secondDate));
        int count = source.CapturedQueries.Count;
        await session.UpdateSelectedAsOfDateAsync(secondDate);
        Assert.AreEqual(count, source.CapturedQueries.Count);
        await session.UpdateSelectedAsOfDateAsync(null);
        int latestCount = source.CapturedQueries.Count;
        await session.UpdateSelectedAsOfDateAsync(null);
        Assert.AreEqual(latestCount, source.CapturedQueries.Count);

    }

    /// <summary>後の日付の集計を先に受理し、取消に応じない先の日付の完了後も新結果を維持する。日付要求の世代検査漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_doesNotPublishStaleEarlierQueryAfterLaterQueryAccepted()
    {
        var source = new QueryAwareLampSource();
        var executor = new GatedBuildExecutor();
        await using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
        var acceptedReadyResults = new List<PlaylistLampAggregationResult>();
        TaskCompletionSource<PlaylistLampAggregationResult> secondAccepted = NewCompletion<PlaylistLampAggregationResult>();
        session.ResultChanged += (_, args) =>
        {
            if (args.Result.State != PlaylistLampViewerState.Ready)
            {
                return;
            }
            lock (acceptedReadyResults)
            {
                acceptedReadyResults.Add(args.Result);
            }
            if (args.Result.Query.SelectedLocalDate == new DateTime(2026, 8, 28))
            {
                secondAccepted.TrySetResult(args.Result);
            }
        };

        List<Task> startedRefreshes = [];
        try
        {
            Task initial = session.StartAsync();
            startedRefreshes.Add(initial);
            await executor.WaitForBuildCountAsync(1);
            executor.Complete(0, executor.AggregateBuild(0));
            await initial;

            DateTime firstDate = new(2026, 8, 27);
            DateTime secondDate = new(2026, 8, 28);
            Task first = session.UpdateSelectedAsOfDateAsync(firstDate);
            startedRefreshes.Add(first);
            await executor.WaitForBuildCountAsync(2);
            Task second = session.UpdateSelectedAsOfDateAsync(secondDate);
            startedRefreshes.Add(second);
            await executor.WaitForBuildCountAsync(3);

            PlaylistLampAggregationResult secondResult = executor.AggregateBuild(2);
            executor.Complete(2, secondResult);
            PlaylistLampAggregationResult acceptedSecond = await secondAccepted.Task;

            // 取消を無視するgateを使い、後の日付の受理後に旧処理を完了して世代検査で拒否させる。
            PlaylistLampAggregationResult staleFirstResult = executor.AggregateBuild(1);
            executor.Complete(1, staleFirstResult);
            await Task.WhenAll(first, second);

            Assert.AreSame(secondResult, acceptedSecond);
            Assert.AreSame(secondResult, session.Current);
            lock (acceptedReadyResults)
            {
                Assert.IsFalse(acceptedReadyResults.Any(result => result.Query.SelectedLocalDate == firstDate));
            }
        }
        finally
        {
            session.Dispose();
            // gateは取消に応じないため、失敗時も全開始処理を解放してから回収する。
            for (int index = 0; index < executor.BuildCount; index++)
            {
                executor.Complete(index, executor.AggregateBuild(index));
            }
            await Task.WhenAll(startedRefreshes);
            await session.DisposeAsync();
        }
    }

    /// <summary>
    /// 初回Ready後の異なる日付変更をcapture待ち/build待ちで競合させ、最新だけの終端では
    /// 停止を完了せず、取消を無視する旧refreshも回収します。停止後の日付変更も新受付しません。
    /// capture待ちでは取消状態の即時反映と後続buildの未開始を検証し、誤って開始したbuild自体は止めません。
    /// </summary>
    [TestMethod]
    [DataRow(false)] // buildの実Taskをholdします。
    [DataRow(true)] // captureの実Taskをholdします。
    public async Task Session_disposeDrainsSupersededDateRefreshes(bool holdCapture)
    {
        var source = new FakeLampSource(QueryAwareLampSource.CreateRequest(PlaylistLampViewerQuery.Latest("playlist")));
        var executor = new GatedBuildExecutor(holdBuilds: !holdCapture);
        var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
        var captures = new List<(TaskCompletionSource<PlaylistLampAggregationRequest> Completion, CancellationToken Token)>();
        TaskCompletionSource<bool> captureChanged = NewCompletion<bool>();
        Task? earlier = null;
        Task? latest = null;
        Task? drain = null;
        int published = 0;
        session.ResultChanged += (_, _) => Interlocked.Increment(ref published);
        try
        {
            Task initial = session.StartAsync();
            await executor.WaitForBuildCountAsync(1);
            executor.Complete(0, executor.AggregateBuild(0));
            await initial;
            source.CaptureOverride = (query, token) =>
            {
                if (!holdCapture) { return ValueTask.FromResult(QueryAwareLampSource.CreateRequest(query)); }
                TaskCompletionSource<PlaylistLampAggregationRequest> completion = NewCompletion<PlaylistLampAggregationRequest>();
                lock (captures)
                {
                    captures.Add((completion, token));
                    captureChanged.TrySetResult(true);
                    captureChanged = NewCompletion<bool>();
                }
                return new ValueTask<PlaylistLampAggregationRequest>(completion.Task);
            };
            earlier = session.UpdateSelectedAsOfDateAsync(new DateTime(2026, 8, 26));
            if (holdCapture) { await waitForCaptures(1); }
            else { await executor.WaitForBuildCountAsync(2); }
            latest = session.UpdateSelectedAsOfDateAsync(new DateTime(2026, 8, 27));
            if (holdCapture) { await waitForCaptures(2); }
            else { await executor.WaitForBuildCountAsync(3); }
            PlaylistLampAggregationResult stoppedResult = session.Current;
            session.Dispose();
            drain = session.DisposeAsync().AsTask();
            if (holdCapture)
            {
                Assert.IsTrue(captures.All(capture => capture.Token.IsCancellationRequested),
                    "停止は取消callbackの実行順によらず、受理済みcaptureのトークンへ直ちに反映します。");
            }
            int stoppedPublished = Volatile.Read(ref published);
            int stoppedCaptures = source.CaptureCount;
            await session.UpdateSelectedAsOfDateAsync(new DateTime(2026, 8, 28));
            if (holdCapture)
            {
                captures[1].Completion.TrySetResult(QueryAwareLampSource.CreateRequest(new PlaylistLampViewerQuery("playlist", new DateTime(2026, 8, 27))));
            }
            else { executor.Complete(2, executor.AggregateBuild(2)); }
            await latest;
            Assert.IsFalse(drain.IsCompleted, "最新refreshだけの終端では旧capture/buildを解放済みにしません。");
            if (holdCapture) { captures[0].Completion.TrySetResult(QueryAwareLampSource.CreateRequest(new PlaylistLampViewerQuery("playlist", new DateTime(2026, 8, 26)))); }
            else { executor.Complete(1, executor.AggregateBuild(1)); }
            await Task.WhenAll(earlier, drain);
            if (holdCapture)
            {
                Assert.AreEqual(1, executor.BuildCount, "取消を無視して戻ったcaptureから後続buildを開始しません。");
            }
            Assert.AreEqual(stoppedCaptures, source.CaptureCount);
            Assert.AreEqual(stoppedPublished, Volatile.Read(ref published));
            Assert.AreSame(stoppedResult, session.Current);
        }
        finally
        {
            session.Dispose();
            lock (captures)
            {
                foreach ((TaskCompletionSource<PlaylistLampAggregationRequest> Completion, CancellationToken Token) capture in captures) { capture.Completion.TrySetResult(source.Request); }
            }
            for (int index = 0; index < executor.BuildCount; index++) { executor.Complete(index, executor.AggregateBuild(index)); }
            if (latest != null) { await latest; }
            if (earlier != null) { await earlier; }
            await session.DisposeAsync();
        }

        async Task waitForCaptures(int count)
        {
            while (true)
            {
                Task changed;
                lock (captures)
                {
                    if (captures.Count >= count) { return; }
                    changed = captureChanged.Task;
                }
                await changed;
            }
        }
    }

    /// <summary>
    /// 停止取消callbackからCurrent・重複停止・drain取得へ再入しても詰まらず、captureを返した後も
    /// callbackとCTS後片付けの実終端までdrainを完了しません。callback内の観測は外で表明します。
    /// </summary>
    [TestMethod]
    public async Task Session_disposeOwnsCancellationCallbacksAndAllowsStopReentry()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var session = new PlaylistLampViewerSession("playlist", source);
        using var callbackRelease = new ManualResetEventSlim();
        TaskCompletionSource<bool> callbackEntered = NewCompletion<bool>();
        TaskCompletionSource<PlaylistLampAggregationRequest> captureRelease = NewCompletion<PlaylistLampAggregationRequest>();
        Task? callbackDrain = null;
        PlaylistLampAggregationResult? callbackCurrent = null;
        Task? refresh = null;
        try
        {
            await session.StartAsync();
            source.CaptureOverride = (_, token) =>
            {
                token.Register(() =>
                {
                    callbackCurrent = session.Current;
                    session.Dispose();
                    callbackDrain = session.DisposeAsync().AsTask();
                    callbackEntered.TrySetResult(true);
                    callbackRelease.Wait();
                });
                return new ValueTask<PlaylistLampAggregationRequest>(captureRelease.Task);
            };
            refresh = session.UpdateSelectedAsOfDateAsync(new DateTime(2026, 8, 26));
            session.Dispose();
            Task drain = session.DisposeAsync().AsTask();
            await callbackEntered.Task;
            captureRelease.TrySetResult(source.Request);
            Assert.IsNotNull(callbackCurrent);
            Assert.IsNotNull(callbackDrain);
            Assert.IsFalse(drain.IsCompleted);
            Assert.IsFalse(callbackDrain.IsCompleted);
            callbackRelease.Set();
            await Task.WhenAll(refresh, drain, callbackDrain);
        }
        finally
        {
            callbackRelease.Set();
            captureRelease.TrySetResult(source.Request);
            session.Dispose();
            if (refresh != null) { await refresh; }
            await session.DisposeAsync();
        }
    }

    /// <summary>別日付の取得が失敗したら旧日付の集計を流用せず未取得とし、最新への明示変更で復帰する。失敗による恒久停止と正常空への置換を検出する。</summary>
    [TestMethod]
    public async Task Session_dateFailureRejectsOtherDateResultAndLatestRecovers()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        await using var session = new PlaylistLampViewerSession("playlist", source);
        await session.StartAsync();
        Assert.AreEqual(PlaylistLampViewerState.Ready, session.Current.State);
        source.CaptureFailure = new InvalidOperationException("date capture unavailable");
        DateTime date = new(2026, 8, 26);
        await session.UpdateSelectedAsOfDateAsync(date);
        Assert.AreEqual(PlaylistLampViewerState.Failed, session.Current.State);
        Assert.AreEqual(date, session.Current.Query.SelectedLocalDate);
        Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);
        source.CaptureFailure = null;
        await session.UpdateSelectedAsOfDateAsync(null);
        Assert.AreEqual(PlaylistLampViewerState.Ready, session.Current.State);
        Assert.IsNull(session.Current.Query.SelectedLocalDate);
        Assert.AreEqual(1, session.Current.Statistics.TotalCount);
    }

    private static PlaylistLampAggregationRequest ReadyRequest(
        string playlistId,
        int revision,
        int count,
        PlaylistLampScoreSnapshot? scoreSnapshot = null)
    {
        var entries = new List<PlaylistLampEntrySnapshot>();
        var scoresByHash = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < count; index++)
        {
            string hash = playlistId + "-" + revision + "-" + index;
            entries.Add(new PlaylistLampEntrySnapshot(
                "folder",
                hash,
                true,
                md5: hash,
                resolvedMd5: hash,
                resolvedPath: "C:/charts/" + hash + ".bms"));
            scoresByHash[hash] = new PlaylistLampScore(hash, "", ClearType.CLEAR, RankType.A, 100, 0, 100, 1);
        }
        scoreSnapshot ??= new PlaylistLampScoreSnapshot(
            ActiveScoreSource.Lr2,
            ScoreTableLoadStatus.Loaded,
            revision,
            revision,
            ScoreUpdatedAt,
            scoresByHash);
        return new PlaylistLampAggregationRequest(
            playlistId,
            ["folder"],
            entries,
            scoreSnapshot,
            PlaylistUpdatedAt);
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeLampSource : IPlaylistLampViewerDataSource
    {
        private readonly object stateGate = new();
        private PlaylistLampAggregationRequest request;
        private int captureCount;

        internal FakeLampSource(PlaylistLampAggregationRequest request)
        {
            this.request = request;
        }

        internal Exception? CaptureFailure { get; set; }

        internal Func<PlaylistLampViewerQuery, CancellationToken, ValueTask<PlaylistLampAggregationRequest>>? CaptureOverride { get; set; }

        internal PlaylistLampAggregationRequest Request { get { lock (stateGate) { return request; } } }

        internal int CaptureCount => Volatile.Read(ref captureCount);

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(PlaylistLampViewerQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref captureCount);
            if (CaptureOverride != null) { return CaptureOverride(query, cancellationToken); }
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = CaptureFailure;
            if (failure != null)
            {
                throw failure;
            }
            lock (stateGate)
            {
                return ValueTask.FromResult(new PlaylistLampAggregationRequest(
                    query.PlaylistId, request.FolderOrder, request.Entries, request.ScoreSnapshot,
                    request.PlaylistLastUpdated, request.InputState, request.FailureMessage, query: query,
                    historicalDateRange: request.HistoricalDateRange, historicalStatus: request.HistoricalStatus,
                    historicalFailureMessage: request.HistoricalFailureMessage));
            }
        }

        internal void Replace(PlaylistLampAggregationRequest request)
        {
            lock (stateGate)
            {
                this.request = request;
            }
        }

    }

    private sealed class QueryAwareLampSource : IPlaylistLampViewerDataSource
    {
        internal List<PlaylistLampViewerQuery> CapturedQueries { get; } = [];


        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            PlaylistLampViewerQuery query,
            CancellationToken cancellationToken)
        {
            lock (CapturedQueries)
            {
                CapturedQueries.Add(query);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CreateRequest(query));
        }

        internal static PlaylistLampAggregationRequest CreateRequest(PlaylistLampViewerQuery query)
        {
            string hash = "query-chart";
            PlaylistLampEntrySnapshot[] entries = new[]
            {
                new PlaylistLampEntrySnapshot(
                    "folder",
                    hash,
                    true,
                    md5: hash,
                    resolvedMd5: hash,
                    resolvedPath: "C:/charts/query-chart.bms")
            };
            var scores = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
            {
                [hash] = PlaylistLampScore.FromExScore(hash, null, ClearType.CLEAR, 100, 100)
            };
            var scoreSnapshot = new PlaylistLampScoreSnapshot(
                ActiveScoreSource.Lr2,
                ScoreTableLoadStatus.Loaded,
                1,
                1,
                null,
                scores);
            return new PlaylistLampAggregationRequest(
                query.PlaylistId,
                ["folder"],
                entries,
                scoreSnapshot,
                query: query,
                historicalDateRange: new PlaylistLampHistoricalDateRange(new DateTime(2026, 8, 26), new DateTime(2026, 8, 29)),
                historicalStatus: query.SelectedLocalDate.HasValue
                    ? PlaylistLampHistoricalSnapshotStatus.Available
                    : PlaylistLampHistoricalSnapshotStatus.Latest);
        }
    }

    private sealed class GatedBuildExecutor(bool holdBuilds = true) : IPlaylistLampViewerBuildExecutor
    {
        private readonly object stateGate = new();
        private readonly List<PlaylistLampAggregationRequest> requests = [];
        private readonly List<TaskCompletionSource<PlaylistLampAggregationResult>> completions = [];
        private TaskCompletionSource<bool> buildChanged = NewCompletion<bool>();

        internal int BuildCount
        {
            get
            {
                lock (stateGate)
                {
                    return requests.Count;
                }
            }
        }

        public Task<PlaylistLampAggregationResult> BuildAsync(
            PlaylistLampAggregationRequest request,
            PlaylistLampAggregationService aggregationService,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            TaskCompletionSource<PlaylistLampAggregationResult> completion = NewCompletion<PlaylistLampAggregationResult>();
            lock (stateGate)
            {
                requests.Add(request);
                completions.Add(completion);
                buildChanged.TrySetResult(true);
                buildChanged = NewCompletion<bool>();
            }
            // captureの終端待ちを検証するときは余計なbuildを件数で失敗にし、未解放gateへ変えません。
            if (!holdBuilds) { completion.TrySetResult(aggregationService.Aggregate(request)); }
            return completion.Task;
        }

        internal async Task WaitForBuildCountAsync(int count)
        {
            while (true)
            {
                Task changedTask;
                lock (stateGate)
                {
                    if (requests.Count >= count)
                    {
                        return;
                    }
                    changedTask = buildChanged.Task;
                }
                await changedTask;
            }
        }

        internal PlaylistLampAggregationResult AggregateBuild(int index)
        {
            lock (stateGate)
            {
                return new PlaylistLampAggregationService().Aggregate(requests[index]);
            }
        }

        internal void Complete(int index, PlaylistLampAggregationResult result)
        {
            lock (stateGate)
            {
                completions[index].TrySetResult(result);
            }
        }
    }
}
