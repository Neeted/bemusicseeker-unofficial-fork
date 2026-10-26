using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>Windowを作らず、実Dispatcher配送時の世代と閉鎖の境界を検証する。</summary>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistLampViewerViewModelTests
{
    /// <summary>旧FailedをDispatcherへ投入してから新日付受理または破棄を先行させ、旧配送で表示・選択・操作を戻さない。session公開時だけの世代検査では見逃す競合を検出する。</summary>
    [TestMethod]
    [DataRow(false)] // 過去日から最新への明示操作をSendで受理してから、旧DataBindを配送する。
    [DataRow(true)] // DisposeをSendで受理してから、旧DataBindを配送する。
    public async Task Viewer_rejectsQueuedEarlierResultAfterNewDateOrDisposal(bool dispose)
    {
        var source = new DateSource();
        await using var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: new InlineBuildExecutor());
        PlaylistLampViewerViewModel? viewModel = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        TestUiDispatcherHost.Invoke(() => viewModel = new PlaylistLampViewerViewModel("playlist", "table", session, dispatcher, _ => { }));
        Assert.IsNotNull(viewModel);
        DispatcherOperation? blocked = null;
        EventHandler<PlaylistLampAggregationResultChangedEventArgs> failedHandler = (_, args) =>
        {
            if (args.Result.State == PlaylistLampViewerState.Failed) { failedPublished.TrySetResult(); }
        };
        session.ResultChanged += failedHandler;
        try
        {
            await session.StartAsync();
            await dispatcher.InvokeAsync(() => Assert.IsTrue(viewModel.IsAsOfDateSelectionEnabled), DispatcherPriority.ContextIdle).Task;
            DateTime date = new(2026, 8, 27);
            await dispatcher.InvokeAsync(() => viewModel.SelectedAsOfDate = date).Task;
            PlaylistLampAggregationResult before = viewModel.CurrentResult;
            // 通常Taskはテスト側でawaitし、Dispatcherを保持した間に旧Failedを確実に投入する。
            blocked = dispatcher.InvokeAsync(() => { entered.TrySetResult(); release.Wait(); });
            await entered.Task;
            source.DateCompletion.TrySetResult(PlaylistLampAggregationRequest.Failed("playlist", "date unavailable", query: session.Query));
            await failedPublished.Task;
            DispatcherOperation newer = dispatcher.InvokeAsync(() =>
            {
                if (dispose) { viewModel.Dispose(); }
                else { viewModel.LatestCommand.Execute(); }
            }, DispatcherPriority.Send);
            release.Set();
            await blocked.Task;
            await newer.Task;
            await dispatcher.InvokeAsync(() =>
            {
                if (dispose)
                {
                    Assert.AreSame(before, viewModel.CurrentResult);
                    Assert.AreEqual(0, viewModel.RankSegments.Count);
                }
                else
                {
                    Assert.IsNull(viewModel.SelectedAsOfDate);
                    Assert.AreSame(session.Current, viewModel.CurrentResult);
                    Assert.AreEqual(PlaylistLampViewerState.Ready, viewModel.State);
                    Assert.IsTrue(viewModel.IsGraphVisible);
                    Assert.IsTrue(viewModel.RankSegments.Any(segment => segment.IsInvokable));
                    Assert.IsFalse(viewModel.IsFailure);
                }
            }, DispatcherPriority.ContextIdle).Task;
        }
        finally
        {
            release.Set();
            source.DateCompletion.TrySetResult(PlaylistLampAggregationRequest.Failed("playlist", "test cleanup", query: session.Query));
            if (blocked != null) { await blocked.Task; }
            session.ResultChanged -= failedHandler;
            await dispatcher.InvokeAsync(viewModel.Dispose).Task;
            await session.DisposeAsync();
        }
    }

    /// <summary>取得失敗の統計と情報源は利用不可とし、正常ゼロや未設定情報源に見せない。別日付結果を流用する誤りはsession側が分担する。</summary>
    [TestMethod]
    public void Viewer_failureDisplaysUnavailableInsteadOfZeroOrUnconfiguredSource()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var session = new PlaylistLampViewerSession("playlist", new DateSource(failLatest: true), buildExecutor: new InlineBuildExecutor());
            using var viewModel = new PlaylistLampViewerViewModel("playlist", "table", session, TestUiDispatcherHost.Dispatcher, _ => { });
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.StartAsync(), "failed projection");
                Assert.IsTrue(viewModel.IsFailure);
                Assert.IsFalse(viewModel.IsGraphVisible);
                Assert.AreEqual(Resources.PlaylistLampViewer_unavailable, viewModel.ScoreSourceText);
                Assert.AreEqual(1, viewModel.StatisticGroups.Count);
                Assert.AreEqual(4, viewModel.StatisticGroups.Single().Count);
                Assert.AreEqual(2, viewModel.SupplementaryStatistics.Count);
                Assert.IsTrue(viewModel.StatisticGroups.SelectMany(group => group).Concat(viewModel.SupplementaryStatistics)
                    .All(item => item.Value == Resources.PlaylistLampViewer_unavailable));
            }
            finally
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "failed projection drain");
            }
        });
    }

    /// <summary>同じ集計値を異文化の表示境界へ与え、9項目の群所属・順序・固定数値書式と分粒度の補助日時2件を確認します。計算の正しさは既存の集計テストが担当します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    public void Viewer_statisticsKeepOwnershipPlayAndScoreGroupsSeparateFromSupplementaryDetails(string cultureName)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
                Assert.AreEqual(cultureName, CultureInfo.CurrentCulture.Name);
                var timestamp = new DateTime(2026, 8, 29, 10, 49, 7, DateTimeKind.Unspecified);
                var executor = new StatisticsBuildExecutor(timestamp);
                var session = new PlaylistLampViewerSession("playlist", new DateSource(), buildExecutor: executor);
                using var viewModel = new PlaylistLampViewerViewModel("playlist", "table", session, TestUiDispatcherHost.Dispatcher, _ => { });
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.StartAsync(), "statistics projection");
                    string[][] labels =
                    [
                        [Resources.PlaylistLampViewer_total, Resources.PlaylistLampViewer_owned, Resources.PlaylistLampViewer_missing, Resources.PlaylistLampViewer_ownership_rate],
                        [Resources.PlaylistLampViewer_played, Resources.PlaylistLampViewer_no_play, Resources.PlaylistLampViewer_play_rate],
                        [Resources.PlaylistLampViewer_average_ex_rate, Resources.PlaylistLampViewer_clear_rate]
                    ];
                    string[][] values = [["210000", "145000", "65000", "69.05%"],
                        ["123000", "87000", "58.57%"], ["73.40%", "41.00%"]];
                    Assert.AreEqual(3, viewModel.StatisticGroups.Count);
                    for (int group = 0; group < labels.Length; group++)
                    {
                        CollectionAssert.AreEqual(labels[group], viewModel.StatisticGroups[group].Select(item => item.Label).ToArray());
                        CollectionAssert.AreEqual(values[group], viewModel.StatisticGroups[group].Select(item => item.Value).ToArray());
                    }
                    CollectionAssert.AreEqual(new[] { Resources.PlaylistLampViewer_score_source, Resources.PlaylistLampViewer_playlist_last_update },
                        viewModel.SupplementaryStatistics.Select(item => item.Label).ToArray());
                    CollectionAssert.AreEqual(new[] { Resources.PlaylistLampViewer_source_lr2, "2026-08-29 10:49" },
                        viewModel.SupplementaryStatistics.Select(item => item.Value).ToArray());
                }
                finally
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "statistics projection drain");
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        });
    }

    private sealed class StatisticsBuildExecutor(DateTime timestamp) : IPlaylistLampViewerBuildExecutor
    {
        public Task<PlaylistLampAggregationResult> BuildAsync(PlaylistLampAggregationRequest request, PlaylistLampAggregationService aggregation, CancellationToken token)
        {
            var statistics = new PlaylistLampStatistics(210000, 145000, 65000, 123000, 87000,
                145000d / 210000, 123000d / 210000, 0.734, 0.41, true, null, timestamp);
            return Task.FromResult(new PlaylistLampAggregationResult(request.PlaylistId, PlaylistLampViewerState.Ready,
                [], [], [], statistics, request.ScoreSnapshot, null, request.Query));
        }
    }

    private sealed class DateSource(bool failLatest = false) : IPlaylistLampViewerDataSource
    {
        internal TaskCompletionSource<PlaylistLampAggregationRequest> DateCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(PlaylistLampViewerQuery query, CancellationToken token)
        {
            if (query.SelectedLocalDate.HasValue) { return new ValueTask<PlaylistLampAggregationRequest>(DateCompletion.Task); }
            if (failLatest) { return ValueTask.FromResult(PlaylistLampAggregationRequest.Failed(query.PlaylistId, "capture unavailable", query: query)); }
            var score = new PlaylistLampScore("hash", "", ClearType.CLEAR, RankType.A, 100, 0, 100, 1);
            var scores = new Dictionary<string, PlaylistLampScore> { ["hash"] = score };
            return ValueTask.FromResult(new PlaylistLampAggregationRequest(query.PlaylistId, ["folder"],
                [new PlaylistLampEntrySnapshot("folder", "hash", true, md5: "hash", resolvedMd5: "hash")],
                new PlaylistLampScoreSnapshot(ActiveScoreSource.Lr2, ScoreTableLoadStatus.Loaded, 1, 1, null, scores), query: query,
                historicalDateRange: new PlaylistLampHistoricalDateRange(new DateTime(2026, 8, 26), DateTime.Today)));
        }
    }

    private sealed class InlineBuildExecutor : IPlaylistLampViewerBuildExecutor
    {
        public Task<PlaylistLampAggregationResult> BuildAsync(PlaylistLampAggregationRequest request, PlaylistLampAggregationService aggregation, CancellationToken token)
            => Task.FromResult(aggregation.Aggregate(request, token));
    }
}
