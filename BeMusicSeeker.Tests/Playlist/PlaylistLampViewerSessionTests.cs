using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>ランプ集計セッションの最新要求・状態公開・購読終了を検証する。</summary>
/// <remarks>集計完了を制御して取消に応じない旧処理の公開拒否を観測する。DBの履歴復元と実ウィンドウの表示・閉鎖は別の情報源・表示ケースへ分担する。</remarks>
[TestClass]
public sealed class PlaylistLampViewerSessionTests
{
    private static readonly DateTime PlaylistUpdatedAt = new(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc);

    private static readonly DateTime ScoreUpdatedAt = new(2026, 8, 28, 2, 3, 4, DateTimeKind.Utc);

    /// <summary>Loading通知の中でセッションを破棄しても開始処理を正常に回収し、購読を一回解除する。取消源の早期破棄によるObjectDisposedExceptionと購読漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_disposeDuringLoadingDoesNotDisposeRefreshCancellationBeforeLinkedTokenCreation()
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

            Assert.IsTrue(session.IsDisposed);
            Assert.AreEqual(1, source.AddCount);
            Assert.AreEqual(1, source.RemoveCount);
            Assert.AreEqual(1, source.CaptureCount);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>所属・所持・スコア情報源・chart_info世代を更新した通知で再集計し、受理した新結果だけを保持する。旧依存情報の配送や新しい結果の公開漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_refreshesForRevisionAndPassesDependencyStampToTheAcceptedBuild()
    {
        PlaylistLampAggregationRequest first = ReadyRequest("playlist", 1, 1, new PlaylistLampDependencyStamp(
            1,
            2,
            3,
            4,
            5,
            ActiveScoreSource.Lr2,
            ScoreTableLoadStatus.Loaded));
        PlaylistLampAggregationRequest second = ReadyRequest("playlist", 2, 2, new PlaylistLampDependencyStamp(
            2,
            6,
            7,
            8,
            9,
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            chartInfoIndexVersion: 10));
        var source = new FakeLampSource(first);
        var executor = new GatedBuildExecutor();
        using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);

        List<Task> startedRefreshes = [];
        try
        {
            Task firstRefresh = session.StartAsync();
            startedRefreshes.Add(firstRefresh);
            await executor.WaitForBuildCountAsync(1);
            PlaylistLampAggregationResult firstResult = executor.AggregateBuild(0);
            executor.Complete(0, firstResult);
            await firstRefresh;

            TaskCompletionSource<PlaylistLampAggregationResult> acceptedSecond = NewCompletion<PlaylistLampAggregationResult>();
            session.ResultChanged += (_, args) =>
            {
                if (args.Result.State == PlaylistLampViewerState.Ready
                    && args.Result.Statistics.TotalCount == 2)
                {
                    acceptedSecond.TrySetResult(args.Result);
                }
            };
            source.Replace(second);
            source.Raise("playlist", 1, second.DependencyStamp);
            await executor.WaitForBuildCountAsync(2);

            PlaylistLampAggregationResult secondResult = executor.AggregateBuild(1);
            executor.Complete(1, secondResult);
            PlaylistLampAggregationResult accepted = await acceptedSecond.Task;

            Assert.AreSame(secondResult, accepted);
            Assert.AreSame(secondResult, session.Current);
            Assert.AreEqual(2, executor.RequestAt(1).DependencyStamp.EntriesRevision);
            Assert.AreEqual(6, executor.RequestAt(1).DependencyStamp.CatalogVersion);
            Assert.AreEqual(7, executor.RequestAt(1).DependencyStamp.OwnedCollectionVersion);
            Assert.AreEqual(ActiveScoreSource.Beatoraja, executor.RequestAt(1).DependencyStamp.ScoreSource);
            Assert.AreEqual(10, executor.RequestAt(1).DependencyStamp.ChartInfoIndexVersion);
        }
        finally
        {
            session.Dispose();
            // 取消に応じない捕捉済みbuildのgateを通知由来分も解放し、公開StartAsyncから保持したTaskを回収する。
            for (int index = 0; index < executor.BuildCount; index++)
            {
                executor.Complete(index, executor.AggregateBuild(index));
            }
            await Task.WhenAll(startedRefreshes);
        }
    }

    /// <summary>別プレイリストの通知と受理済み番号より古い通知は追加集計を起こさず、対象の新通知だけを受理する。対象分離と通知順の判断漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_ignoresOtherPlaylistAndOlderSourceNotifications()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var executor = new GatedBuildExecutor();
        using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);

        List<Task> startedRefreshes = [];
        try
        {
            Task initialRefresh = session.StartAsync();
            startedRefreshes.Add(initialRefresh);
            await executor.WaitForBuildCountAsync(1);
            executor.Complete(0, executor.AggregateBuild(0));
            await initialRefresh;

            source.Raise("other-playlist", 100);
            Assert.AreEqual(1, executor.BuildCount);

            source.Replace(ReadyRequest("playlist", 2, 2));
            source.Raise("playlist", 10);
            await executor.WaitForBuildCountAsync(2);
            executor.Complete(1, executor.AggregateBuild(1));
            await WaitForReadyBuildAsync(session, 2);

            source.Raise("playlist", 9);
            Assert.AreEqual(2, executor.BuildCount);
        }
        finally
        {
            session.Dispose();
            // 取消に応じない捕捉済みbuildのgateを通知由来分も解放し、公開StartAsyncから保持したTaskを回収する。
            for (int index = 0; index < executor.BuildCount; index++)
            {
                executor.Complete(index, executor.AggregateBuild(index));
            }
            await Task.WhenAll(startedRefreshes);
        }
    }

    /// <summary>新しい所属の集計を先に公開し、取消に応じない旧集計を後で完了しても結果と通知を増やさない。古い所属結果による表示の巻戻しを検出する。</summary>
    [TestMethod]
    public async Task Session_doesNotPublishSupersededGatedBuild()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var executor = new GatedBuildExecutor();
        using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
        var acceptedReadyResults = new List<PlaylistLampAggregationResult>();
        TaskCompletionSource<PlaylistLampAggregationResult> acceptedSecond = NewCompletion<PlaylistLampAggregationResult>();
        session.ResultChanged += (_, args) =>
        {
            if (args.Result.State == PlaylistLampViewerState.Ready)
            {
                lock (acceptedReadyResults)
                {
                    acceptedReadyResults.Add(args.Result);
                }
                if (args.Result.Statistics.TotalCount == 2)
                {
                    acceptedSecond.TrySetResult(args.Result);
                }
            }
        };

        List<Task> startedRefreshes = [];
        try
        {
            Task firstRefresh = session.StartAsync();
            startedRefreshes.Add(firstRefresh);
            await executor.WaitForBuildCountAsync(1);
            PlaylistLampAggregationRequest secondRequest = ReadyRequest("playlist", 2, 2);
            source.Replace(secondRequest);
            source.Raise("playlist", 1, secondRequest.DependencyStamp);
            await executor.WaitForBuildCountAsync(2);

            PlaylistLampAggregationResult secondResult = executor.AggregateBuild(1);
            executor.Complete(1, secondResult);
            await acceptedSecond.Task;
            int acceptedCountAfterSecond;
            lock (acceptedReadyResults)
            {
                acceptedCountAfterSecond = acceptedReadyResults.Count;
            }

            PlaylistLampAggregationResult staleResult = executor.AggregateBuild(0);
            executor.Complete(0, staleResult);
            await firstRefresh;

            Assert.AreSame(secondResult, session.Current);
            lock (acceptedReadyResults)
            {
                Assert.AreEqual(acceptedCountAfterSecond, acceptedReadyResults.Count);
                Assert.IsFalse(acceptedReadyResults.Any(result => result.Statistics.TotalCount == 1 && !ReferenceEquals(result, secondResult)));
            }
        }
        finally
        {
            session.Dispose();
            // 取消に応じない捕捉済みbuildのgateを通知由来分も解放し、公開StartAsyncから保持したTaskを回収する。
            for (int index = 0; index < executor.BuildCount; index++)
            {
                executor.Complete(index, executor.AggregateBuild(index));
            }
            await Task.WhenAll(startedRefreshes);
        }
    }

    /// <summary>削除・所属読取り失敗は操作不可、スコアだけの障害は所持数付きReadyとして公開する。障害の種類を空結果へ混同したりスコア障害で全体を終端化する誤りを検出する。</summary>
    [TestMethod]
    public async Task Session_preservesDeletedFailedAndDegradedStates()
    {
        var source = new FakeLampSource(PlaylistLampAggregationRequest.Deleted("playlist"));
        using var session = new PlaylistLampViewerSession("playlist", source);

        await session.StartAsync();
        Assert.AreEqual(PlaylistLampViewerState.Deleted, session.Current.State);
        Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);

        source.Replace(PlaylistLampAggregationRequest.Failed("playlist", "entry load failed"));
        await session.RefreshAsync();
        Assert.AreEqual(PlaylistLampViewerState.Failed, session.Current.State);
        StringAssert.Contains(session.Current.FailureMessage, "entry load failed");
        Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);

        source.Replace(ReadyRequest(
            "playlist",
            3,
            2,
            dependencyStamp: default,
            scoreSnapshot: new PlaylistLampScoreSnapshot(
                ActiveScoreSource.Lr2,
                ScoreTableLoadStatus.Failed,
                8,
                9,
                null,
                failureMessage: "score db failed")));
        await session.RefreshAsync();

        Assert.AreEqual(PlaylistLampViewerState.Ready, session.Current.State);
        Assert.AreEqual(2, session.Current.Statistics.TotalCount);
        Assert.AreEqual(2, session.Current.Statistics.OwnedCount);
        Assert.AreEqual(0, session.Current.Statistics.MissingCount);
        Assert.IsFalse(session.Current.ScoreDataAvailable);
        Assert.IsNull(session.Current.Statistics.PlayedCount);
        Assert.IsNull(session.Current.Statistics.UnplayedCount);
        Assert.IsNull(session.Current.Statistics.PlayRate);
        Assert.IsFalse(session.Current.ClearSegments.Any(segment => segment.IsInvokable));
    }

    /// <summary>入力捕捉の元例外をFailedと診断へ変換して操作を禁止する。捕捉障害を正常な空プレイリストとして公開する誤りを検出する。</summary>
    [TestMethod]
    public async Task Session_translatesCaptureFailureToFailedInsteadOfEmpty()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1))
        {
            CaptureFailure = new InvalidOperationException("entry snapshot unavailable")
        };
        using var session = new PlaylistLampViewerSession("playlist", source);

        await session.StartAsync();

        Assert.AreEqual(PlaylistLampViewerState.Failed, session.Current.State);
        Assert.AreEqual(0, session.Current.Statistics.TotalCount);
        StringAssert.Contains(session.Current.FailureMessage, "entry snapshot unavailable");
        Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);
    }

    /// <summary>同じ情報源の2セッションで片方を繰返し破棄しても他方は更新でき、両方の購読をそれぞれ一回解除する。共有状態の巻込みと解除の重複・漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_instancesAreIndependentAndDisposeUnsubscribesExactlyOnce()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var executorA = new GatedBuildExecutor();
        var executorB = new GatedBuildExecutor();
        using var sessionA = new PlaylistLampViewerSession("playlist", source, buildExecutor: executorA);
        using var sessionB = new PlaylistLampViewerSession("playlist", source, buildExecutor: executorB);

        List<Task> startedRefreshes = [];
        try
        {
            Task firstA = sessionA.StartAsync();
            startedRefreshes.Add(firstA);
            Task firstB = sessionB.StartAsync();
            startedRefreshes.Add(firstB);
            await executorA.WaitForBuildCountAsync(1);
            await executorB.WaitForBuildCountAsync(1);
            executorA.Complete(0, executorA.AggregateBuild(0));
            executorB.Complete(0, executorB.AggregateBuild(0));
            await Task.WhenAll(firstA, firstB);

            sessionA.Dispose();
            sessionA.Dispose();
            Assert.AreEqual(2, source.AddCount);
            Assert.AreEqual(1, source.RemoveCount);

            TaskCompletionSource<PlaylistLampAggregationResult> acceptedB = NewCompletion<PlaylistLampAggregationResult>();
            sessionB.ResultChanged += (_, args) =>
            {
                if (args.Result.State == PlaylistLampViewerState.Ready
                    && args.Result.Statistics.TotalCount == 2)
                {
                    acceptedB.TrySetResult(args.Result);
                }
            };
            source.Replace(ReadyRequest("playlist", 2, 2));
            source.Raise("playlist", 1);
            await executorB.WaitForBuildCountAsync(2);
            executorB.Complete(1, executorB.AggregateBuild(1));
            await acceptedB.Task;

            Assert.AreEqual(1, executorA.BuildCount);
            Assert.AreEqual(2, executorB.BuildCount);
            sessionB.Dispose();
            sessionB.Dispose();
            Assert.AreEqual(2, source.RemoveCount);
        }
        finally
        {
            sessionA.Dispose();
            sessionB.Dispose();
            // 取消に応じない捕捉済みbuildのgateを通知由来分も解放し、公開StartAsyncから保持したTaskを回収する。
            for (int index = 0; index < executorA.BuildCount; index++)
            {
                executorA.Complete(index, executorA.AggregateBuild(index));
            }
            for (int index = 0; index < executorB.BuildCount; index++)
            {
                executorB.Complete(index, executorB.AggregateBuild(index));
            }
            await Task.WhenAll(startedRefreshes);
        }
    }

    /// <summary>異なる基準日の連続要求を両方捕捉し、最新日を要求と公開結果に保持する。要求内容の共有・上書きや最新日の反映漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_queryUpdateKeepsTheLatestImmutableQuery()
    {
        var source = new QueryAwareLampSource();
        using var session = new PlaylistLampViewerSession("playlist", source);

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
    }

    /// <summary>後の日付の集計を先に受理し、取消に応じない先の日付の完了後も新結果を維持する。日付要求の世代検査漏れを検出する。</summary>
    [TestMethod]
    public async Task Session_doesNotPublishStaleEarlierQueryAfterLaterQueryAccepted()
    {
        var source = new QueryAwareLampSource();
        var executor = new GatedBuildExecutor();
        using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
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
        }
    }

    private static async Task WaitForReadyBuildAsync(PlaylistLampViewerSession session, int totalCount)
    {
        TaskCompletionSource<PlaylistLampAggregationResult> completion = NewCompletion<PlaylistLampAggregationResult>();
        EventHandler<PlaylistLampAggregationResultChangedEventArgs> handler = (_, args) =>
        {
            if (args.Result.State == PlaylistLampViewerState.Ready
                && args.Result.Statistics.TotalCount == totalCount)
            {
                completion.TrySetResult(args.Result);
            }
        };
        session.ResultChanged += handler;
        try
        {
            if (session.Current.State == PlaylistLampViewerState.Ready
                && session.Current.Statistics.TotalCount == totalCount)
            {
                completion.TrySetResult(session.Current);
            }
            await completion.Task;
        }
        finally
        {
            session.ResultChanged -= handler;
        }
    }

    private static PlaylistLampAggregationRequest ReadyRequest(
        string playlistId,
        int revision,
        int count,
        PlaylistLampDependencyStamp dependencyStamp = default,
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
            PlaylistUpdatedAt,
            dependencyStamp: dependencyStamp);
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeLampSource : IPlaylistLampViewerDataSource
    {
        private readonly object stateGate = new();
        private EventHandler<PlaylistLampViewerSourceChangedEventArgs>? changed;
        private PlaylistLampAggregationRequest request;
        private int captureCount;

        internal FakeLampSource(PlaylistLampAggregationRequest request)
        {
            this.request = request;
        }

        internal Exception? CaptureFailure { get; set; }

        internal int AddCount { get; private set; }

        internal int RemoveCount { get; private set; }

        internal int CaptureCount => Volatile.Read(ref captureCount);

        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed
        {
            add
            {
                lock (stateGate)
                {
                    AddCount++;
                    changed += value;
                }
            }
            remove
            {
                lock (stateGate)
                {
                    RemoveCount++;
                    changed -= value;
                }
            }
        }

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(PlaylistLampViewerQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref captureCount);
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = CaptureFailure;
            if (failure != null)
            {
                throw failure;
            }
            lock (stateGate)
            {
                return ValueTask.FromResult(request);
            }
        }

        internal void Replace(PlaylistLampAggregationRequest request)
        {
            lock (stateGate)
            {
                this.request = request;
            }
        }

        internal void Raise(string playlistId, long sequence, PlaylistLampDependencyStamp dependencyStamp = default)
        {
            EventHandler<PlaylistLampViewerSourceChangedEventArgs>? handler;
            lock (stateGate)
            {
                handler = changed;
            }
            handler?.Invoke(this, new PlaylistLampViewerSourceChangedEventArgs(playlistId, sequence, dependencyStamp));
        }
    }

    private sealed class QueryAwareLampSource : IPlaylistLampViewerDataSource
    {
        internal List<PlaylistLampViewerQuery> CapturedQueries { get; } = [];

#pragma warning disable CS0067 // インターフェイスのイベント面を満たすが、このテストダブルでは発火させない。
        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs>? Changed;
#pragma warning restore CS0067

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

        private static PlaylistLampAggregationRequest CreateRequest(PlaylistLampViewerQuery query)
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
                historicalDateRange: new PlaylistLampHistoricalDateRange(new DateTime(2026, 8, 27), new DateTime(2026, 8, 29)),
                historicalStatus: query.SelectedLocalDate.HasValue
                    ? PlaylistLampHistoricalSnapshotStatus.Available
                    : PlaylistLampHistoricalSnapshotStatus.Latest);
        }
    }

    private sealed class GatedBuildExecutor : IPlaylistLampViewerBuildExecutor
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
            _ = aggregationService;
            _ = cancellationToken;
            TaskCompletionSource<PlaylistLampAggregationResult> completion = NewCompletion<PlaylistLampAggregationResult>();
            lock (stateGate)
            {
                requests.Add(request);
                completions.Add(completion);
                buildChanged.TrySetResult(true);
                buildChanged = NewCompletion<bool>();
            }
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

        internal PlaylistLampAggregationRequest RequestAt(int index)
        {
            lock (stateGate)
            {
                return requests[index];
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
