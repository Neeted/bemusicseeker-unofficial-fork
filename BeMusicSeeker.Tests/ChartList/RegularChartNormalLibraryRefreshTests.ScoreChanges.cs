using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.RegularChartListOwnerTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class RegularChartNormalLibraryRefreshTests
{
    /// <summary>score未準備からの通常初回表示は従来どおりNoScore入力を使い、表示のための新lookup構築・score確定・通知を開始しません。</summary>
    [TestMethod]
    public void NormalInitialView_DoesNotBuildOrPublishUnpreparedScores()
    {
        WithTemporarySongDb(path =>
        {
            var library = new TestBmsLibrary(path) { BmsCharts = [CreateTestableBmsFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? throw new InvalidOperationException(), "no-score.bms"))] };
            var table = new MainChartListViewModel();
            using RegularChartListOwner owner = CreateOwner(table, CreateWorkspaceForOwner(table));
            owner.AttachNormalLibraryRefreshSource(library);
            int notification = library.NormalLibraryRefreshNotificationVersion;
            var work = new List<string>();
            library.ScoreSnapshotWorkObserver = work.Add;
            try
            {
                Assert.IsFalse(library.ScoreSnapshotReady);
                Assert.IsTrue(owner.TryApplyVirtualNormalLibrary(new RegularVirtualNormalLibraryApplyRequest
                {
                    Library = library,
                    ModeFilter = ChartModeFilter.All,
                    SortColumnName = nameof(LibraryChartRow.stddevVal),
                    SortDirection = ListSortDirection.Ascending,
                    ColumnSelection = new MainChartListColumnSelection(new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD), false, 0,
                        MainViewUpdateMode.FullScanAllChartsFilterSelected, System.Windows.Visibility.Collapsed, new PlaylistSummaryColumnSettings()),
                    Mode = MainViewUpdateMode.FullScanAllChartsFilterSelected,
                    Stopwatch = System.Diagnostics.Stopwatch.StartNew()
                }).WasCommitted);
                LibraryChartRow row = table.Rows[0] as LibraryChartRow
                    ?? throw new AssertFailedException("初回表示の譜面行が生成されませんでした。");
                _ = row.score;
                Assert.IsFalse(library.ScoreSnapshotReady);
                Assert.AreEqual(0, library.ScoreSnapshotVersion);
                Assert.AreEqual(notification, library.NormalLibraryRefreshNotificationVersion);
                Assert.AreEqual(0, work.Count);
            }
            finally { library.ScoreSnapshotWorkObserver = null; library.RequestShutdown("unprepared_score_view"); }
        });
    }
    /// <summary>16/128のwarm元集合でAだけを実setterから配送し、値・再配置・選択とU同一性を保ちます。実lookup/投影/順序/確保の仕事量と実操作Completionを観測します。</summary>
    [DataTestMethod]
    [DataRow(16, false)]
    [DataRow(128, false)]
    [DataRow(16, true)]
    [DataRow(128, true)]
    public void ScoreCommit_ChangesOnlyDependentInputAndWaitsForScheduledCompletion(int count, bool filtered)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path, count);
            Assert.IsTrue(fixture.Build(filtered));
            ChartListVirtualView rows = fixture.View;
            LibraryChartRow? affected = filtered ? null : (LibraryChartRow)rows[0];
            var unrelated = (LibraryChartRow)rows[filtered ? 0 : 1];
            fixture.Table.SelectedIndex = filtered ? 0 : 1;
            int unrelatedNotifications = 0;
            unrelated.PropertyChanged += (_, _) => unrelatedNotifications++;
            var work = new ConcurrentDictionary<string, int>();
            fixture.Library.ScoreSnapshotWorkObserver = kind => work.AddOrUpdate(kind, 1, (_, old) => old + 1);
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, count) => work.AddOrUpdate(kind, count, (_, old) => old + count);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            fixture.Change(0, 40);
            Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
            operation.Run();
            Assert.IsFalse(delivery.IsCompleted, "callback終了だけでは実scheduler Completionの成功を代用しません。");
            Assert.AreSame(rows, fixture.Table.Rows);
            Assert.AreSame(unrelated, rows[0]);
            Assert.AreEqual(0, fixture.Table.SelectedIndex);
            Assert.AreEqual(0, unrelatedNotifications);
            Assert.AreEqual(filtered ? 1 : 2, rows.RealizedRowCount, "背景の未訪問行を実体化しません。");
            Assert.AreEqual(filtered ? 3 : count, rows.Count);
            Assert.AreEqual(2, rows.IndexOfNormalToken(fixture.Library.CaptureNormalLibraryChartSnapshot().BmsRows[0].Token));
            Assert.IsTrue(work.GetValueOrDefault("score_projection_lookup") <= 2);
            Assert.IsTrue(work.GetValueOrDefault("score_projection_value_copied") <= 2);
            Assert.AreEqual(0, work.GetValueOrDefault("score_lookup_source_enumerated"));
            Assert.AreEqual(0, work.GetValueOrDefault("score_lookup_reference_copied"));
            Assert.AreEqual(0, work.GetValueOrDefault("projection_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("order_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("hash_dependency_visit"));
            Assert.IsTrue(work.GetValueOrDefault("source_visit") <= 4);
            Assert.IsTrue(work.GetValueOrDefault("order_key") <= 1);
            Assert.IsTrue(work.GetValueOrDefault("order_access") <= 64);
            Assert.IsTrue(work.GetValueOrDefault("order_compare") <= 64);
            operation.Complete();
            await delivery;
            TestContext?.WriteLine("score warm count=" + count + " totalMs=" + stopwatch.ElapsedMilliseconds + " work="
                + string.Join(",", work.OrderBy(pair => pair.Key).Select(pair => pair.Key + ":" + pair.Value)));
            Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, rows.ScorePosition);
            affected ??= (LibraryChartRow)rows[2];
            Assert.AreEqual(40, affected.score);
            Assert.AreEqual(40d, affected.stddevVal);
        }
    }

    /// <summary>A/Bを同じ正本gateで確定し、両PropertyChanged起動を止めて片方だけ配送します。逆順起動・非表示加入離脱・同条件reuseも独立期待B,U,A/{U,A}へ収束します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ScoreCommits_PartialAndReverseWakeCoversAllKeysBeforeReuse(bool filtered, bool reverseWake)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(fixture.Build(filtered));
            ChartListVirtualView rows = fixture.View;
            int unrelatedIndex = filtered ? 0 : 1;
            var unrelated = (LibraryChartRow)rows[unrelatedIndex];
            fixture.Table.SelectedIndex = unrelatedIndex;
            using var aEntered = new ManualResetEventSlim();
            using var bEntered = new ManualResetEventSlim();
            using var aRelease = new ManualResetEventSlim();
            using var bRelease = new ManualResetEventSlim();
            int aThread = -1, bThread = -1;
            PropertyChangedEventHandler gate = (_, args) =>
            {
                if (args.PropertyName != nameof(BMSLibrary.ScoreSnapshotVersion)) { return; }
                if (Environment.CurrentManagedThreadId == aThread) { aEntered.Set(); aRelease.Wait(); }
                if (Environment.CurrentManagedThreadId == bThread) { bEntered.Set(); bRelease.Wait(); }
            };
            fixture.Library.PropertyChanged += gate;
            Task a = StartLongRunning(() => { aThread = Environment.CurrentManagedThreadId; fixture.Change(0, 40); });
            Task? b = null;
            try
            {
                aEntered.Wait();
                b = StartLongRunning(() => { bThread = Environment.CurrentManagedThreadId; fixture.Change(1, 5); });
                bEntered.Wait();
                Assert.IsTrue(rows.ScorePosition < fixture.Library.ScoreSnapshotVersion);
                if (reverseWake) { bRelease.Set(); await b; } else { aRelease.Set(); await a; }
                Assert.IsFalse(fixture.Build(filtered), "同条件の既存入力は、未配送rangeを最新版へ付け替えて採用しません。");
                Assert.AreSame(rows, fixture.Table.Rows);
                Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
                operation.Run(); operation.Complete();
                await delivery;
                ScheduledScoreOperation continuation = await fixture.Scheduler.NextAsync();
                continuation.Run(); continuation.Complete();
                ChartListVirtualView adopted = fixture.View;
                CollectionAssert.AreEqual(filtered ? new[] { "U", "A" } : new[] { "B", "U", "A" },
                    adopted.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, adopted.ScorePosition);
                Assert.AreSame(unrelated, adopted[filtered ? 0 : 1]);
                Assert.AreEqual(filtered ? 0 : 1, fixture.Table.SelectedIndex);
                aRelease.Set(); bRelease.Set();
                await a; await b;
                await fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                Assert.AreSame(adopted, fixture.Table.Rows);
                Assert.IsTrue(fixture.Build(filtered), "同条件要求へ未反映の比較/filterを最新版としてreuseしません。");
                CollectionAssert.AreEqual(filtered ? new[] { "U", "A" } : new[] { "B", "U", "A" },
                    fixture.Table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
                Assert.AreSame(unrelated, fixture.View[filtered ? 0 : 1]);
            }
            finally
            {
                aRelease.Set(); bRelease.Set();
                fixture.Library.PropertyChanged -= gate;
                await a;
                if (b != null) { await b; }
            }
        }
    }

    /// <summary>Aの初回投影後・Bの投影直前に実scoreを確定し、旧bの混合候補を採用しません。配送後は同じ候補へ変更キーだけを反映します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScoreCandidate_FixesBaselineBeforeFirstProjectionAndPatchesBufferedRanges(bool filtered)
    {
        WithTemporarySongDb(path => VerifyScoreCandidateAsync(path, filtered, subset: false).GetAwaiter().GetResult());
    }

    /// <summary>配送Taskの成功と候補の必要位置coverを独立に確認します。既に配送済みのrangeを持たない旧候補は、番号補正・全再構築・同位置再試行をせず取消で終端します。</summary>
    [TestMethod]
    public void ScoreCandidate_SuccessfulDeliveryWithoutRequiredRangeRejectsCandidateAndStops()
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(fixture.Build());
            ChartListVirtualView displayed = fixture.View;
            Assert.IsTrue(ChartListOrder.TryCreate(displayed.NormalInput, nameof(LibraryChartRow.stddevVal), ListSortDirection.Ascending, out ChartListOrder order));
            var oldCandidate = new ChartListVirtualView(displayed.NormalInput, order, LibraryChartRow.FromSourceRow,
                scorePosition: displayed.ScorePosition);
            fixture.Change(0, 40);
            Task delivered = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
            operation.Run(); operation.Complete();
            await delivered;
            // 要求内rangeを持たない採用境界を直接試す。生産/配送の条件網羅は上の実setter/gate caseが担う。
            Assert.IsTrue(fixture.Owner.TryBeginVirtualRequest(fixture.Library, out RegularChartListRequestLease lease));
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = lease.Token.Register(() => cancelled.TrySetResult());
            Assert.IsFalse(fixture.Owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(oldCandidate)).WasCommitted);
            Task noPending = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            await noPending;
            ScheduledScoreOperation continuation = await fixture.Scheduler.NextAsync();
            continuation.Run(); continuation.Complete();
            await cancelled.Task;
            Assert.AreSame(displayed, fixture.Table.Rows);
            Assert.IsTrue(oldCandidate.ScorePosition < fixture.Library.ScoreSnapshotVersion);
            Assert.AreEqual(0, fixture.Scheduler.Count);
            await fixture.Owner.StopAsync();

        }
    }

    /// <summary>v1予約後に合流した実v2のfanout失敗を同じ配送Taskへ返し、候補を取消終端にします。同位置を再予約せず、実試行を超えるv3の確定時は未完了v1/v2も次配送へ渡します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScoreDelivery_MergedRangeFailureDoesNotRetryAttemptedPosition(bool publishNewerRange)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(fixture.Build());
            ChartListVirtualView displayed = fixture.View;
            int initialScorePosition = displayed.ScorePosition;
            var unrelated = (LibraryChartRow)displayed[1];
            Assert.IsTrue(ChartListOrder.TryCreate(displayed.NormalInput, nameof(LibraryChartRow.stddevVal), ListSortDirection.Ascending, out ChartListOrder order));
            var candidate = new ChartListVirtualView(displayed.NormalInput, order, LibraryChartRow.FromSourceRow,
                scorePosition: displayed.ScorePosition);
            Assert.IsTrue(fixture.Owner.TryBeginVirtualRequest(fixture.Library, out RegularChartListRequestLease lease));
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration cancellation = lease.Token.Register(() => cancelled.TrySetResult());
            fixture.Change(0, 40);
            Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
            int firstVersion = fixture.Library.NormalLibraryRefreshNotificationVersion;
            fixture.Change(1, 5);
            int mergedVersion = fixture.Library.NormalLibraryRefreshNotificationVersion;
            Assert.IsTrue(mergedVersion > firstVersion);
            Assert.AreSame(delivery, fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None));
            Assert.IsFalse(fixture.Owner.TryCommitVirtual(lease, CreateVirtualTerminalInput(candidate)).WasCommitted);
            var failure = new InvalidOperationException("merged score fanout failed");
            int attempts = 0;
            EventHandler<NormalLibraryRefreshAppliedEventArgs> fail = (_, args) =>
            {
                attempts++;
                Assert.AreEqual(mergedVersion, args.NotificationBatch.LatestVersion);
                Assert.AreEqual(2, args.NotificationBatch.ScoreChanges.Count);
                throw failure;
            };
            fixture.Owner.NormalLibraryRefreshApplied += fail;
            try { operation.Run(); }
            finally { fixture.Owner.NormalLibraryRefreshApplied -= fail; }
            Assert.AreEqual(1, attempts);
            Assert.IsFalse(delivery.IsCompleted, "適用失敗も実scheduler Completionまで同じ配送を所有します。");
            Assert.IsFalse(cancelled.Task.IsCompleted);
            Assert.AreSame(displayed, fixture.Table.Rows);
            if (publishNewerRange) { fixture.Change(0, 45); }
            Assert.AreEqual(0, fixture.Scheduler.Count, "実Completion前は追加wakeも同じ予約へ合流します。");
            operation.Complete();
            InvalidOperationException actual = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await delivery);
            Assert.AreSame(failure, actual);
            await cancelled.Task;
            Assert.AreSame(displayed, fixture.Table.Rows, "配送失敗の候補を採用しません。");
            if (publishNewerRange)
            {
                Assert.AreEqual(1, fixture.Scheduler.Count, "実試行v2を超える新しい実確定v3だけを予約します。");
                Task nextDelivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                Assert.AreNotSame(delivery, nextDelivery);
                ScheduledScoreOperation next = await fixture.Scheduler.NextAsync();
                int nextAttempts = 0;
                EventHandler<NormalLibraryRefreshAppliedEventArgs> observeNext = (_, args) =>
                {
                    nextAttempts++;
                    Assert.AreEqual(3, args.NotificationBatch.ScoreChanges.Count, "失敗したfanoutの未完了rangeも新位置の配送へ含めます。");
                    CollectionAssert.AreEqual(new[] { initialScorePosition, initialScorePosition + 1, initialScorePosition + 2 },
                        args.NotificationBatch.ScoreChanges.Select(change => change.BaseVersion).ToArray());
                    CollectionAssert.AreEqual(new[] { initialScorePosition + 1, initialScorePosition + 2, initialScorePosition + 3 },
                        args.NotificationBatch.ScoreChanges.Select(change => change.Version).ToArray());
                    CollectionAssert.AreEqual(new[] { fixture.Scores[0].hash, fixture.Scores[1].hash, fixture.Scores[0].hash },
                        args.NotificationBatch.ScoreChanges.SelectMany(change => change.Md5Keys).ToArray());
                    Assert.IsTrue(args.NotificationBatch.LatestVersion > mergedVersion);
                };
                fixture.Owner.NormalLibraryRefreshApplied += observeNext;
                try { next.Run(); next.Complete(); }
                finally { fixture.Owner.NormalLibraryRefreshApplied -= observeNext; }
                await nextDelivery;
                Assert.AreEqual(1, nextAttempts);
                Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, displayed.ScorePosition);
                // 初期A/B/U=10/30/20からA=40・B=5、次にA=45。比較値とEX scoreを二重換算せず独立期待として固定します。
                CollectionAssert.AreEqual(new[] { ("B", (double?)5, (int?)5), ("U", (double?)20, (int?)20), ("A", (double?)45, (int?)45) },
                    displayed.Cast<LibraryChartRow>().Select(row => (row.Title, row.stddevVal, row.score)).ToArray());
            }
            Assert.AreEqual(0, fixture.Scheduler.Count, "新しい実確定がない同位置の配送を繰り返しません。");
            Assert.AreSame(unrelated, displayed[1]);
            await fixture.Owner.StopAsync();
        }
    }

    /// <summary>呼出元取消・受付後取消・適用例外を共有drainと区別し、実Completionまで受付を共有して停止を回収します。</summary>
    [DataTestMethod]
    [DataRow("caller_cancel")]
    [DataRow("abort")]
    [DataRow("apply_failure")]
    [DataRow("reject")]
    public void ScoreDelivery_FailureAndCancellationReachTerminalWithoutCancellingOtherWaiters(string outcome)
    {
        WithTemporarySongDb(path => RunAsync(path).GetAwaiter().GetResult());
        async Task RunAsync(string path)
        {
            using var fixture = new RegularScoreDeliveryTestFixture(path);
            Assert.IsTrue(fixture.Build());
            if (outcome == "reject") { fixture.Scheduler.Reject = true; }
            fixture.Change(0, 40);
            if (outcome == "reject")
            {
                Task rejected = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await rejected);
                return;
            }
            Task shared = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
            using var cancellation = new CancellationTokenSource();
            Task caller = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await caller);
            Assert.IsFalse(shared.IsCompleted);
            if (outcome == "abort")
            {
                operation.Abort();
                await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () => await shared);
            }
            else
            {
                EventHandler<NormalLibraryRefreshAppliedEventArgs> fail = (_, _) => throw new InvalidOperationException("score fanout failed");
                if (outcome == "apply_failure") { fixture.Owner.NormalLibraryRefreshApplied += fail; }
                try { operation.Run(); operation.Complete(); }
                finally { fixture.Owner.NormalLibraryRefreshApplied -= fail; }
                if (outcome == "apply_failure") { await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await shared); }
                else { await shared; Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, fixture.View.ScorePosition); }
            }
        }
    }

    /// <summary>初回全評価の入力境界を通常/専用subsetで共有して検査し、実配送と候補継続の別UI操作をfinallyまで回収します。</summary>
    internal static async Task VerifyScoreCandidateAsync(string path, bool filtered, bool subset)
    {
        using var fixture = new RegularScoreDeliveryTestFixture(path);
        int lookups = 0;
        bool changed = false;
        fixture.Library.ScoreSnapshotWorkObserver = kind =>
        {
            if (kind == "score_projection_lookup" && Interlocked.Increment(ref lookups) == 2)
            {
                changed = true;
                fixture.Change(0, 40);
                fixture.Change(1, 5);
            }
        };
        Assert.IsFalse(fixture.Build(filtered, subset));
        Assert.IsTrue(changed);
        fixture.Library.ScoreSnapshotWorkObserver = null;
        Task delivery = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
        ScheduledScoreOperation operation = await fixture.Scheduler.NextAsync();
        operation.Run();
        Assert.IsFalse(delivery.IsCompleted);
        operation.Complete();
        await delivery;
        ScheduledScoreOperation continuation = await fixture.Scheduler.NextAsync();
        continuation.Run(); continuation.Complete();
        CollectionAssert.AreEqual(filtered ? new[] { "U", "A" } : new[] { "B", "U", "A" },
            fixture.Table.Rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
        Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, fixture.View.ScorePosition);
        if (subset)
        {
            ChartListVirtualView rows = fixture.View;
            var unrelated = (LibraryChartRow)rows[filtered ? 0 : 1];
            fixture.Table.SelectedIndex = filtered ? 0 : 1;
            var work = new ConcurrentDictionary<string, int>();
            fixture.Library.ScoreSnapshotWorkObserver = kind => work.AddOrUpdate(kind, 1, (_, old) => old + 1);
            fixture.Table.RowProjection.NormalInputWorkObserver = (kind, amount) => work.AddOrUpdate(kind, amount, (_, old) => old + amount);
            fixture.Change(1, 30);
            Task warm = fixture.Owner.DeliverPendingLibraryChangesAsync(fixture.Library, CancellationToken.None);
            ScheduledScoreOperation patch = await fixture.Scheduler.NextAsync();
            patch.Run(); patch.Complete();
            await warm;
            Assert.AreSame(rows, fixture.Table.Rows);
            Assert.AreSame(unrelated, rows[0]);
            Assert.AreEqual(0, fixture.Table.SelectedIndex);
            Assert.AreEqual(0, work.GetValueOrDefault("order_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("projection_buffer"));
            Assert.AreEqual(0, work.GetValueOrDefault("hash_dependency_visit"));
            Assert.IsTrue(work.GetValueOrDefault("order_key") <= 1);
            Assert.IsTrue(work.GetValueOrDefault("score_projection_lookup") <= 2);
            CollectionAssert.AreEqual(new[] { "U", "B", "A" }, rows.Cast<LibraryChartRow>().Select(row => row.Title).ToArray());
            Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, rows.ScorePosition);
        }
    }
}
