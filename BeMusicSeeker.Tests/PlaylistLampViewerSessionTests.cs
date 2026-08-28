using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// PLV-07〜09, PLV-11 の session lifecycle and stale-build contract tests.
/// </summary>
[TestClass]
public sealed class PlaylistLampViewerSessionTests
{
    private static readonly DateTime PlaylistUpdatedAt = new(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc);

    private static readonly DateTime ScoreUpdatedAt = new(2026, 8, 28, 2, 3, 4, DateTimeKind.Utc);

    [TestMethod]
    public async Task Session_disposeDuringLoadingDoesNotDisposeRefreshCancellationBeforeLinkedTokenCreation()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var session = new PlaylistLampViewerSession("playlist", source);
        var loadingPublished = NewCompletion<bool>();
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
            await loadingPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

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

        Task firstRefresh = session.StartAsync();
        await executor.WaitForBuildCountAsync(1);
        PlaylistLampAggregationResult firstResult = executor.AggregateBuild(0);
        executor.Complete(0, firstResult);
        await firstRefresh;

        var acceptedSecond = NewCompletion<PlaylistLampAggregationResult>();
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
        PlaylistLampAggregationResult accepted = await acceptedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreSame(secondResult, accepted);
        Assert.AreSame(secondResult, session.Current);
        Assert.AreEqual(2, executor.RequestAt(1).DependencyStamp.EntriesRevision);
        Assert.AreEqual(6, executor.RequestAt(1).DependencyStamp.CatalogVersion);
        Assert.AreEqual(7, executor.RequestAt(1).DependencyStamp.OwnedCollectionVersion);
        Assert.AreEqual(ActiveScoreSource.Beatoraja, executor.RequestAt(1).DependencyStamp.ScoreSource);
        Assert.AreEqual(10, executor.RequestAt(1).DependencyStamp.ChartInfoIndexVersion);
    }

    [TestMethod]
    public async Task Session_ignoresOtherPlaylistAndOlderSourceNotifications()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var executor = new GatedBuildExecutor();
        using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);

        Task initialRefresh = session.StartAsync();
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

    [TestMethod]
    public async Task Session_doesNotPublishSupersededGatedBuild()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var executor = new GatedBuildExecutor();
        using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
        var acceptedReadyResults = new List<PlaylistLampAggregationResult>();
        var acceptedSecond = NewCompletion<PlaylistLampAggregationResult>();
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

        Task firstRefresh = session.StartAsync();
        await executor.WaitForBuildCountAsync(1);
        PlaylistLampAggregationRequest secondRequest = ReadyRequest("playlist", 2, 2);
        source.Replace(secondRequest);
        source.Raise("playlist", 1, secondRequest.DependencyStamp);
        await executor.WaitForBuildCountAsync(2);

        PlaylistLampAggregationResult secondResult = executor.AggregateBuild(1);
        executor.Complete(1, secondResult);
        await acceptedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    [TestMethod]
    public async Task Session_instancesAreIndependentAndDisposeUnsubscribesExactlyOnce()
    {
        var source = new FakeLampSource(ReadyRequest("playlist", 1, 1));
        var executorA = new GatedBuildExecutor();
        var executorB = new GatedBuildExecutor();
        using var sessionA = new PlaylistLampViewerSession("playlist", source, buildExecutor: executorA);
        using var sessionB = new PlaylistLampViewerSession("playlist", source, buildExecutor: executorB);

        Task firstA = sessionA.StartAsync();
        Task firstB = sessionB.StartAsync();
        await executorA.WaitForBuildCountAsync(1);
        await executorB.WaitForBuildCountAsync(1);
        executorA.Complete(0, executorA.AggregateBuild(0));
        executorB.Complete(0, executorB.AggregateBuild(0));
        await Task.WhenAll(firstA, firstB);

        sessionA.Dispose();
        sessionA.Dispose();
        Assert.AreEqual(2, source.AddCount);
        Assert.AreEqual(1, source.RemoveCount);

        var acceptedB = NewCompletion<PlaylistLampAggregationResult>();
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
        await acceptedB.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, executorA.BuildCount);
        Assert.AreEqual(2, executorB.BuildCount);
        sessionB.Dispose();
        sessionB.Dispose();
        Assert.AreEqual(2, source.RemoveCount);
    }

    private static async Task WaitForReadyBuildAsync(PlaylistLampViewerSession session, int totalCount)
    {
        var completion = NewCompletion<PlaylistLampAggregationResult>();
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
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(string playlistId, CancellationToken cancellationToken)
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
            var completion = NewCompletion<PlaylistLampAggregationResult>();
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
                await changedTask.WaitAsync(TimeSpan.FromSeconds(5));
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
