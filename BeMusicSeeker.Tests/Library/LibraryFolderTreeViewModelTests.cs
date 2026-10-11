using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Diagnostics;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LibraryFolderTreeViewModelTests
{
    /// <summary>
    /// 保証：実UI Dispatcherのない構築を拒否し、後の表示反映を実行できない所有者を成立させない。
    /// </summary>
    [TestMethod]
    public void Constructor_RequiresLiveUiDispatcher()
    {
        Assert.ThrowsException<InvalidOperationException>(() => new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => null!)));
    }

    /// <summary>
    /// 保証：終了済みDispatcherへの遅延更新要求は表示・shell再受付を行わず終端し、再試行を残さない。
    /// </summary>
    /// <remarks>終了後の投稿拒否を確認するため専用STA Dispatcherを実際に停止してから要求する。</remarks>
    [TestMethod]
    public async Task DeferredRefresh_DropsRequestAfterDispatcherShutdownWithoutRetry()
    {
        var dispatcherReady = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread dispatcherThread = new(() =>
        {
            try
            {
                Dispatcher shutdownDispatcher = Dispatcher.CurrentDispatcher;
                dispatcherReady.TrySetResult(shutdownDispatcher);
                shutdownDispatcher.InvokeShutdown();
            }
            catch (Exception exception)
            {
                dispatcherReady.TrySetException(exception);
            }
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        Dispatcher shutdownDispatcher = await dispatcherReady.Task;
        dispatcherThread.Join();

        LibraryFolderTreeViewModel owner = CreateTreeOwner(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => shutdownDispatcher));
        int refreshRequests = 0;
        owner.CacheRefreshRequested += (_, _) => refreshRequests++;
        owner.ScheduleDeferredRefresh(operationToken: 1);
        await owner.WaitForDeferredRefreshIdleAsync();
        Assert.AreEqual(0, refreshRequests);
    }

    /// <summary>
    /// 保証：投稿済みの遅延更新がDispatcher停止で中断されたら更新要求・完了通知を出さずidleへ戻り、待機を残さない。
    /// </summary>
    /// <remarks>送信優先度のgateで更新をqueueに留めて実Dispatcherを停止し、受付後の中断を観測する。</remarks>
    [TestMethod]
    public async Task DeferredRefresh_DropsQueuedRequestWhenDispatcherShutsDown()
    {
        Dispatcher dispatcher = null!;
        LibraryFolderTreeViewModel owner = null!;
        var dispatcherReady = new TaskCompletionSource<(
            Dispatcher Dispatcher,
            LibraryFolderTreeViewModel Owner)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockerEntered = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlocker = new ManualResetEventSlim();
        Thread dispatcherThread = new(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                owner = CreateTreeOwner(
                    _ => true,
                    _ => new ExplorerOpenResult(),
                    new WpfUiScheduler(() => dispatcher));
                dispatcherReady.TrySetResult((dispatcher, owner));
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                dispatcherReady.TrySetException(exception);
            }
        });
        dispatcherThread.SetApartmentState(ApartmentState.STA);
        dispatcherThread.Start();
        (dispatcher, owner) = await dispatcherReady.Task;

        int refreshRequests = 0;
        int refreshCompletions = 0;
        owner.CacheRefreshRequested += (_, _) => Interlocked.Increment(ref refreshRequests);
        owner.DeferredRefreshCompleted += (_, _) => Interlocked.Increment(ref refreshCompletions);
        DispatcherOperation blocker = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            blockerEntered.TrySetResult(null);
            releaseBlocker.Wait();
        }));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(blockerEntered.Task, blocker.Task, "library-tree.dispatcher-blocker");

            var refreshOperationPosted = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherHookEventHandler operationPosted = (_, args) =>
            {
                if (args.Operation.Priority == DispatcherPriority.Background)
                {
                    refreshOperationPosted.TrySetResult(null);
                }
            };
            dispatcher.Hooks.OperationPosted += operationPosted;

            owner.ScheduleDeferredRefresh(operationToken: 1);
            Task refreshIdle = owner.WaitForDeferredRefreshIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(refreshOperationPosted.Task, refreshIdle, "library-tree.refresh-posted");
            Assert.IsFalse(refreshIdle.IsCompleted);
            dispatcher.Hooks.OperationPosted -= operationPosted;
            _ = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)dispatcher.InvokeShutdown);
            releaseBlocker.Set();

            Assert.IsTrue(dispatcherThread.Join(TimeSpan.FromSeconds(5)));
            await refreshIdle;
            Assert.AreEqual(0, refreshRequests);
            Assert.AreEqual(0, refreshCompletions);
        }
        finally
        {
            releaseBlocker.Set();
            if (!dispatcher.HasShutdownStarted) { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            Assert.IsTrue(dispatcherThread.Join(TimeSpan.FromSeconds(5)));
            await blocker.Task;
            await owner.WaitForDeferredRefreshIdleAsync();
        }
    }

    /// <summary>
    /// 表示待ちの複数更新を最新tokenへ集約して一度だけ完了し、同じidle待機を保ってshellの再受付を要求しない。
    /// </summary>
    /// <remarks>共有Dispatcherのqueueをgateで保持するため並列実行を抑止し、準備workerと実受信の終端を別々に回収する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredRefresh_CoalescesToLatestOperationWithoutShellRetry()
    {
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        LibraryFolderTreeViewModel owner = null!;
        var workers = new ConcurrentQueue<Task>();
        var firstWorker = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        TestUiDispatcherHost.Invoke(() =>
        {
            owner = CreateTreeOwner(_ => true, _ => new ExplorerOpenResult(), new WpfUiScheduler(() => dispatcher));
            owner.ConfigureDeferredRefreshScheduler((_, work) =>
            {
                var worker = Task.Run(work);
                workers.Enqueue(worker);
                firstWorker.TrySetResult(worker);
                return true;
            });
        });
        var blockerEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseBlocker = new ManualResetEventSlim();
        var latestRefreshCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshRequests = 0;
        int refreshCompletions = 0;
        long completedOperationToken = -1;
        EventHandler<LibraryFolderTreeRefreshRequestedEventArgs> requested = (_, _) => Interlocked.Increment(ref refreshRequests);
        EventHandler<LibraryFolderTreeRefreshCompletedEventArgs> completed = (_, args) =>
        {
            completedOperationToken = args.OperationToken;
            Interlocked.Increment(ref refreshCompletions);
            latestRefreshCompleted.TrySetResult(null);
        };
        owner.CacheRefreshRequested += requested;
        owner.DeferredRefreshCompleted += completed;
        Task refreshIdle = Task.CompletedTask;
        DispatcherOperation blocker = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
        {
            blockerEntered.TrySetResult(null);
            releaseBlocker.Wait();
        }));
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(blockerEntered.Task, blocker.Task, "library-tree.dispatcher-blocker");
            owner.ScheduleDeferredRefresh(operationToken: 0);
            refreshIdle = owner.WaitForDeferredRefreshIdleAsync();
            await TestUiDispatcherHost.AwaitNotificationAsync(firstWorker.Task, refreshIdle, "library-tree.first-worker-scheduled");
            await (await firstWorker.Task);
            owner.ScheduleDeferredRefresh(operationToken: 42);
            Assert.IsFalse(refreshIdle.IsCompleted);
            Assert.AreSame(refreshIdle, owner.WaitForDeferredRefreshIdleAsync());
            releaseBlocker.Set();

            await TestUiDispatcherHost.AwaitNotificationAsync(latestRefreshCompleted.Task, refreshIdle, "library-tree.latest-refresh-applied");
            await refreshIdle;
            Assert.AreEqual(42, completedOperationToken);
            Assert.AreEqual(1, refreshCompletions);
            Assert.AreEqual(0, refreshRequests);
        }
        finally
        {
            releaseBlocker.Set();
            try
            {
                await Task.WhenAll(blocker.Task, refreshIdle);
            }
            finally
            {
                try { await Task.WhenAll(workers.ToArray()); }
                finally
                {
                    TestUiDispatcherHost.Invoke(() =>
                    {
                        owner.CacheRefreshRequested -= requested;
                        owner.DeferredRefreshCompleted -= completed;
                        owner.DetachLibrary();
                    });
                }
            }
        }
    }

    /// <summary>
    /// 表示待ちに実ライブラリの入力が失効したら最新tokenとinteractionを再受付へ渡し、同じidle待機で再受付後の実反映まで待つ。
    /// </summary>
    /// <remarks>共有Dispatcherをgateで保持するため並列実行を抑止し、SourceInvalidationとDeferredContinuationを区別して再受付を観測する。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public async Task DeferredRefresh_StaleSourceRequestsShellReadmissionBeforeRetry()
    {
        await OwnedChartCollectionTestSupport.WithTemporarySongDbAsync(async songDbPath =>
        {
            Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
            LibraryFolderTreeViewModel owner = null!;
            var workers = new ConcurrentQueue<Task>();
            var firstWorker = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            TestUiDispatcherHost.Invoke(() =>
            {
                var library = new TestBmsLibrary(songDbPath) { BmsCharts = [], SearchTargets = [] };
                owner = CreateTreeOwner(_ => true, _ => new ExplorerOpenResult(), new WpfUiScheduler(() => dispatcher));
                owner.AttachLibrary(library);
                owner.ConfigureDeferredRefreshScheduler((_, work) =>
                {
                    var worker = Task.Run(work);
                    workers.Enqueue(worker);
                    firstWorker.TrySetResult(worker);
                    return true;
                });
            });
            var blockerEntered = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseBlocker = new ManualResetEventSlim();
            var readmissionRequested = new TaskCompletionSource<LibraryFolderTreeRefreshRequestedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseReadmission = new ManualResetEventSlim();
            var latestRefreshCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var interaction = PerformanceInteraction.Start("startup", 7L);
            var latestInteraction = PerformanceInteraction.Start("startup", 42L);
            int refreshCompletions = 0;
            long completedOperationToken = -1;
            EventHandler<LibraryFolderTreeRefreshRequestedEventArgs> requested = (_, args) =>
            {
                // 入力失効の通知では最新要求を上書きせず、実受信が求めた継続だけを再受付する。
                if (args.Origin != LibraryFolderTreeRefreshRequestOrigin.DeferredContinuation) { return; }
                owner.ScheduleDeferredRefresh(args.OperationToken, args.Interaction);
                readmissionRequested.TrySetResult(args);
                releaseReadmission.Wait();
            };
            EventHandler<LibraryFolderTreeRefreshCompletedEventArgs> completed = (_, args) =>
            {
                completedOperationToken = args.OperationToken;
                Interlocked.Increment(ref refreshCompletions);
                latestRefreshCompleted.TrySetResult(null);
            };
            owner.CacheRefreshRequested += requested;
            owner.DeferredRefreshCompleted += completed;
            Task refreshIdle = Task.CompletedTask;
            DispatcherOperation blocker = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() =>
            {
                blockerEntered.TrySetResult(null);
                releaseBlocker.Wait();
            }));
            try
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(blockerEntered.Task, blocker.Task, "library-tree.dispatcher-blocker");
                owner.ScheduleDeferredRefresh(operationToken: 7, interaction: interaction);
                refreshIdle = owner.WaitForDeferredRefreshIdleAsync();
                await TestUiDispatcherHost.AwaitNotificationAsync(firstWorker.Task, refreshIdle, "library-tree.first-worker-scheduled");
                await (await firstWorker.Task);
                owner.ScheduleDeferredRefresh(operationToken: 42, interaction: latestInteraction);
                owner.InvalidateLibraryFolderCache();
                releaseBlocker.Set();

                await TestUiDispatcherHost.AwaitNotificationAsync(readmissionRequested.Task, refreshIdle, "library-tree.readmission-request");
                LibraryFolderTreeRefreshRequestedEventArgs request = await readmissionRequested.Task;
                Assert.AreEqual(LibraryFolderTreeRefreshRequestOrigin.DeferredContinuation, request.Origin);
                Assert.AreEqual(42, request.OperationToken);
                Assert.AreEqual(latestInteraction.InteractionId, request.Interaction.InteractionId);
                Assert.IsFalse(refreshIdle.IsCompleted);
                Assert.AreSame(refreshIdle, owner.WaitForDeferredRefreshIdleAsync());
                releaseReadmission.Set();

                await TestUiDispatcherHost.AwaitNotificationAsync(latestRefreshCompleted.Task, refreshIdle, "library-tree.latest-refresh-applied");
                await refreshIdle;
                Assert.AreEqual(42, completedOperationToken);
                Assert.AreEqual(1, refreshCompletions);
            }
            finally
            {
                releaseReadmission.Set();
                releaseBlocker.Set();
                try
                {
                    await Task.WhenAll(blocker.Task, refreshIdle);
                }
                finally
                {
                    try { await Task.WhenAll(workers.ToArray()); }
                    finally
                    {
                        TestUiDispatcherHost.Invoke(() =>
                        {
                            owner.CacheRefreshRequested -= requested;
                            owner.DeferredRefreshCompleted -= completed;
                            owner.DetachLibrary();
                        });
                    }
                }
            }
        });
    }

    /// <summary>
    /// 保証：一つの更新に全診断段階と同じinteractionを付け、後の独立更新へ先のinteractionを持ち越さない。
    /// </summary>
    /// <remarks>実ライブラリのフォルダ捕捉からUI受信まで通し、段階記録の欠落と相関の漏出を確認する。</remarks>
    [TestMethod]
    public async Task DeferredRefresh_EmitsAggregateStagesForOneInteraction()
    {
        string tempRootPath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_LibraryFolderTree_Stages_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            var stages = new ConcurrentQueue<string>();
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = []
            };
            library.SearchTargets = [tempRootPath];
            LibraryFolderTreeViewModel owner = CreateTreeOwner(
                _ => true,
                _ => new ExplorerOpenResult(),
                new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                stages.Enqueue,
                stages.Enqueue);
            owner.AttachLibrary(library);
            owner.InvalidateLibraryFolderCache();

            var interaction = PerformanceInteraction.Start("startup", 17L);
            owner.ScheduleDeferredRefresh(42L, interaction);
            await owner.WaitForDeferredRefreshIdleAsync();

            string[] expectedStages =
            [
                "request_accepted",
                "worker_queued",
                "worker_started",
                "model_reader_wait_start",
                "model_reader_wait_end",
                "path_snapshot_complete",
                "cache_build_complete",
                "ui_queued",
                "ui_started",
                "ui_applied"
            ];
            string[] actual = stages.ToArray();
            foreach (string expectedStage in expectedStages)
            {
                Assert.IsTrue(
                    actual.Any(value => value.IndexOf("stage=" + expectedStage, StringComparison.Ordinal) >= 0),
                    "Missing aggregate stage: " + expectedStage);
            }
            Assert.IsTrue(actual
                .Where(value => value.IndexOf("stage=", StringComparison.Ordinal) >= 0)
                .All(value => value.IndexOf(
                "interactionId=" + interaction.InteractionId,
                StringComparison.Ordinal) >= 0));

            int entryCountBeforeIndependentRefresh = stages.Count;
            owner.InvalidateLibraryFolderCache();
            owner.ScheduleDeferredRefresh(43L);
            await owner.WaitForDeferredRefreshIdleAsync();
            string[] independentRefreshMarkers = stages
                .Skip(entryCountBeforeIndependentRefresh)
                .Where(value => value.IndexOf("stage=", StringComparison.Ordinal) >= 0)
                .ToArray();
            Assert.IsFalse(independentRefreshMarkers.Any(value => value.IndexOf(
                "interactionId=" + interaction.InteractionId,
                StringComparison.Ordinal) >= 0));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：構成したschedulerへ遅延更新の実workを渡し、所有する実行窓口を迂回して別の背景処理を開始しない。
    /// </summary>
    [TestMethod]
    public void DeferredRefreshUsesConfiguredOwnerScheduler()
    {
        string queuedName = null!;
        Func<Task> queuedWork = null!;
        LibraryFolderTreeViewModel owner = CreateTreeOwner(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));
        owner.ConfigureDeferredRefreshScheduler((name, work) =>
        {
            queuedName = name;
            queuedWork = work;
            return true;
        });

        owner.ScheduleDeferredRefresh(operationToken: 42);

        Assert.AreEqual("library_folder_tree_refresh", queuedName);
        Assert.IsNotNull(queuedWork);
    }

    /// <summary>
    /// 保証：scheduler未接続の遅延更新は失敗させ、追跡できない作業を暗黙に開始しない。
    /// </summary>
    [TestMethod]
    public void DeferredRefreshFailsFastWithoutOwnerScheduler()
    {
        var owner = new LibraryFolderTreeViewModel(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => TestUiDispatcherHost.Dispatcher));

        Assert.ThrowsException<InvalidOperationException>(
            () => owner.ScheduleDeferredRefresh(operationToken: 42));
    }

    /// <summary>
    /// 保証：接続したライブラリの検索先を重複なしの並びで公開し、検索先変更後は一度の再受付で新しいフォルダへ反映する。
    /// </summary>
    /// <remarks>実ライブラリと更新通知・受信側を接続し、設定変更の受渡しと一覧の再反映を確認する。</remarks>
    [TestMethod]
    public async Task AttachedLibrary_ExposesSortedDistinctFoldersAndInvalidatesAfterSearchRootChange()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LibraryFolderTree_" + Guid.NewGuid().ToString("N"));
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        string firstRoot = Path.Combine(tempRootPath, "Zeta");
        string secondRoot = Path.Combine(tempRootPath, "Alpha");
        string replacementRoot = Path.Combine(tempRootPath, "Beta");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(replacementRoot);
        File.WriteAllBytes(songDbPath, []);
        LibraryFolderTreeViewModel? owner = null;
        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            var library = new TestBmsLibrary(songDbPath);
            library.SearchTargets = [firstRoot, secondRoot, firstRoot];
            var firstRefreshApplied = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRefreshApplied = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            owner = CreateTreeOwner(
                _ => true,
                _ => new ExplorerOpenResult(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            int parentFolderPropertyChanges = 0;
            int cacheRefreshRequests = 0;
            owner.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LibraryFolderTreeViewModel.BMSParentFolderList))
                {
                    parentFolderPropertyChanges++;
                    if (parentFolderPropertyChanges == 1)
                    {
                        firstRefreshApplied.TrySetResult(null);
                    }
                    else
                    {
                        secondRefreshApplied.TrySetResult(null);
                    }
                }
            };
            owner.CacheRefreshRequested += (_, args) =>
            {
                cacheRefreshRequests++;
                owner.ScheduleDeferredRefresh(args.OperationToken, args.Interaction);
            };

            owner.AttachLibrary(library);
            Assert.IsTrue(owner.IsLibraryAttached);
            await TestUiDispatcherHost.AwaitNotificationAsync(firstRefreshApplied.Task, owner.WaitForDeferredRefreshIdleAsync(), "library-tree.first-application");
            CollectionAssert.AreEqual(
                new[] { secondRoot, firstRoot },
                owner.BMSParentFolderList.ToArray());
            Assert.AreEqual(library.IsWriteLockHeldInitializeBMSFiles, owner.IsWriteLockHeldInitializeBMSFiles);
            cacheRefreshRequests = 0;

            int propertyChangesBeforeInvalidation = parentFolderPropertyChanges;
            owner.ApplySearchTargets([replacementRoot, secondRoot]);
            CollectionAssert.AreEqual(
                new[] { replacementRoot, secondRoot },
                library.SearchTargets.ToArray());
            owner.InvalidateLibraryFolderCache();

            await TestUiDispatcherHost.AwaitNotificationAsync(secondRefreshApplied.Task, owner.WaitForDeferredRefreshIdleAsync(), "library-tree.second-application");
            CollectionAssert.AreEqual(
                new[] { secondRoot, replacementRoot },
                owner.BMSParentFolderList.ToArray());
            Assert.IsTrue(parentFolderPropertyChanges > propertyChangesBeforeInvalidation);
            Assert.AreEqual(1, cacheRefreshRequests);
        }
        finally
        {
            if (owner != null) { await owner.WaitForDeferredRefreshIdleAsync(); }
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：ライブラリ未接続の検索先変更と失効操作は無作用で終わり、存在しない対象の更新要求を出さない。
    /// </summary>
    [TestMethod]
    public void DetachedLibrary_SearchRootRuntimeIsNoOp()
    {
        LibraryFolderTreeViewModel owner = CreateTreeOwner(
            _ => true,
            _ => new ExplorerOpenResult(),
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));
        int refreshRequests = 0;
        owner.CacheRefreshRequested += (_, _) => refreshRequests++;

        Assert.IsFalse(owner.IsLibraryAttached);
        owner.ApplySearchTargets(["detached"]);
        owner.InvalidateLibraryFolderCache();

        Assert.AreEqual(0, refreshRequests);
    }

    /// <summary>
    /// 保証：BMS・bmsonの所持確認は実パスの親子境界を守り、文字列prefixだけの近似フォルダや未所持先を含めない。
    /// </summary>
    /// <remarks>ライブラリへ接続したツリーから所持索引の実問い合わせを行い、二形式と対象受渡しを確認する。</remarks>
    [TestMethod]
    public void AttachedLibrary_OwnedChartQueryPreservesPathBoundariesForBmsAndBmson()
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_LibraryFolderTree_OwnedChart_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);

        try
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            }

            string bmsPath = Path.Combine("C:\\Installed", "Bms", "chart.bms");
            string bmsonPath = Path.Combine("C:\\Installed", "Bmson", "chart.bmson");
            string[] bmsValues = new string[29];
            bmsValues[0] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            bmsValues[7] = bmsPath;
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(bmsValues))],
                BmsonCharts =
                [
                    ChartTestValues.Empty(ChartFileKind.Bmson) with {
                        Path = bmsonPath,
                        Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                    }
                ]
            };
            LibraryFolderTreeViewModel owner = CreateTreeOwner(
                _ => true,
                _ => new ExplorerOpenResult(),
                new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

            owner.AttachLibrary(library);

            Assert.IsTrue(owner.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bms")));
            Assert.IsTrue(owner.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Bmson")));
            Assert.IsTrue(owner.HasOwnedChartUnderRealPath("C:\\Installed"));
            Assert.IsFalse(owner.HasOwnedChartUnderRealPath("C:\\Install"));
            Assert.IsFalse(owner.HasOwnedChartUnderRealPath(Path.Combine("C:\\Installed", "Missing")));
            Assert.IsFalse(owner.HasOwnedChartUnderRealPath(null));
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保証：Explorer起動はディレクトリ確認後に同じ指定パスへ一度だけ行い、検査と起動の対象を取り違えない。
    /// </summary>
    [TestMethod]
    public void OpenFolderInExplorer_ValidatesBeforeOpeningExactlyOnce()
    {
        List<string> validationPaths = [];
        List<string> openedPaths = [];
        LibraryFolderTreeViewModel owner = CreateTreeOwner(
            path =>
            {
                validationPaths.Add(path);
                return true;
            },
            path =>
            {
                openedPaths.Add(path);
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.OpenedDirectory,
                    RequestedPath = path,
                    OpenedPath = path
                };
            },
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

        owner.OpenFolderInExplorer("C:\\Library");

        CollectionAssert.AreEqual(new[] { "C:\\Library" }, validationPaths);
        CollectionAssert.AreEqual(new[] { "C:\\Library" }, openedPaths);
    }

    /// <summary>
    /// 保証：存在しないフォルダをExplorerへ渡さず、利用不能な対象へ外部作用を開始しない。
    /// </summary>
    [TestMethod]
    public void OpenFolderInExplorer_MissingFolderDoesNotInvokeExplorer()
    {
        LibraryFolderTreeViewModel owner = CreateTreeOwner(
            _ => false,
            _ => throw new AssertFailedException("Explorer should not be invoked for a missing folder."),
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

        owner.OpenFolderInExplorer("C:\\Missing");
    }

    /// <summary>
    /// 保証：Explorer起動失敗の後に別の経路で再起動せず、同じ対象へ外部作用を一度だけ要求する。
    /// </summary>
    [TestMethod]
    public void OpenFolderInExplorer_PreservesFailedExplorerResultWithoutFallback()
    {
        int openCount = 0;
        LibraryFolderTreeViewModel owner = CreateTreeOwner(
            _ => true,
            path =>
            {
                openCount++;
                return new ExplorerOpenResult
                {
                    Kind = ExplorerOpenResultKind.Failed,
                    RequestedPath = path,
                    FailureReason = "shell_failed"
                };
            },
            new WpfUiScheduler(() => Dispatcher.CurrentDispatcher));

        owner.OpenFolderInExplorer("C:\\Library");

        Assert.AreEqual(1, openCount);
    }

    private static LibraryFolderTreeViewModel CreateTreeOwner(
        Func<string, bool> directoryExists,
        Func<string, ExplorerOpenResult> openDirectory,
        IUiScheduler uiScheduler,
        Action<string>? log = null,
        Action<string>? logWarning = null)
    {
        var owner = new LibraryFolderTreeViewModel(
            directoryExists,
            openDirectory,
            uiScheduler,
            log,
            logWarning);
        owner.ConfigureDeferredRefreshScheduler((_, work) =>
        {
            var ignored = Task.Run(work);
            return true;
        });
        return owner;
    }
}
