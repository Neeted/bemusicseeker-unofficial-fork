using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupBackgroundTaskSchedulerOwnerTests
{
    /// <summary>
    /// reset前に開始した子Taskの進捗は元世代を維持し、observer失敗を隔離して同名要求の開始・終端を二重表示しない。
    /// </summary>
    [TestMethod]
    public async Task Report_ChildTaskRetainsGenerationAfterResetAndDoesNotDuplicateRequest()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        owner.Reset(false);
        long generation = owner.CurrentGeneration;
        owner.ProgressChanged += _ => throw new InvalidOperationException("display failure");
        owner.ProgressChanged += snapshot =>
        {
            notifications.Enqueue(snapshot);
            if (!snapshot.IsRunning && snapshot.Name == "external_table_catalog")
            {
                terminal.TrySetResult(true);
            }
        };
        var child = Task.Run(async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
            owner.Report("external_table_catalog", "start", 0, false, "");
            owner.Report("external_table_catalog", "done", 1, false, "");
        });
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, child, nameof(child));
            owner.Reset(false);
        }
        finally { release.TrySetResult(true); await child; }
        await TestUiDispatcherHost.AwaitNotificationAsync(terminal.Task, child, nameof(child));
        Assert.IsTrue(notifications.ToArray().All(snapshot => snapshot.Generation == generation));
        Assert.AreEqual(2, notifications.Count);
        notifications.Clear();
        owner.Queue("playlist_library_index_prewarm", "startup", null, () =>
        {
            owner.Report("playlist_library_index_prewarm", "start", 0, false, "");
            owner.Report("playlist_library_index_prewarm", "done", 0, false, "");
            return Task.CompletedTask;
        });
        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(2, notifications.Count, "要求と同名の開始・終端を二重表示しない。");
    }

    /// <summary>
    /// scoreとrankingのworkerを再利用しても各受付の世代・tokenを保ち、実行reporterとschedulerの通知を重複せず依存処理まで終える。
    /// </summary>
    [DataTestMethod]
    [DataRow("score_hydration_deferred")]
    [DataRow("ranking_refresh_deferred")]
    public async Task ExecutionReporter_UsesAcceptanceIdentityAcrossWorkerReuseWithoutDuplicatePresentation(string name)
    {
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        var notifications = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
        owner.ProgressChanged += _ => throw new InvalidOperationException("display failure");
        owner.ProgressChanged += notifications.Enqueue;
        owner.Reset(false, 11);
        long oldGeneration = owner.CurrentGeneration;
        Action<int, bool> oldReporter = owner.CaptureExecutionProgressReporter(name);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextReporter = new TaskCompletionSource<Action<int, bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reusedWorker = Task.Run(async () =>
        {
            oldReporter(11, true);
            entered.TrySetResult(true);
            Action<int, bool> reporter = await nextReporter.Task;
            oldReporter(11, false);
            reporter(22, true);
            reporter(22, false);
        });
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, reusedWorker, nameof(reusedWorker));
            owner.Reset(false, 22);
            Action<int, bool> currentReporter = owner.CaptureExecutionProgressReporter(name);
            nextReporter.TrySetResult(currentReporter);
            await reusedWorker;
            StartupBackgroundTaskProgressSnapshot[] captured = notifications.ToArray();
            Assert.AreEqual(4, captured.Length);
            Assert.IsTrue(captured.Take(2).All(snapshot => snapshot.Generation == oldGeneration && snapshot.Version == 11));
            Assert.IsTrue(captured.Skip(2).All(snapshot => snapshot.Generation == owner.CurrentGeneration && snapshot.Version == 22));
            Assert.IsTrue(captured.Take(2).All(snapshot => snapshot.Request == new OperationProgressRequest(oldGeneration, 11, name, 11)));
            Assert.IsTrue(captured.Skip(2).All(snapshot => snapshot.Request == new OperationProgressRequest(owner.CurrentGeneration, 22, name, 22)));
            notifications.Clear();
            bool dependencyRan = false;
            owner.Queue(name, "test", null, () =>
            {
                owner.Report(name, "start", 0, false, string.Empty);
                currentReporter(33, true);
                currentReporter(33, false);
                owner.Report(name, "done", 0, false, string.Empty);
                return Task.CompletedTask;
            });
            owner.Queue("external_table_catalog", "test", name, () =>
            {
                dependencyRan = true;
                return Task.CompletedTask;
            });
            owner.Start();
            await WaitForFullyIdleAsync(owner);
            StartupBackgroundTaskProgressSnapshot[] execution = notifications.Where(snapshot => snapshot.Name == name).ToArray();
            Assert.AreEqual(2, execution.Length);
            Assert.IsTrue(execution.All(snapshot => snapshot.Version == 33));
            Assert.IsTrue(dependencyRan);
            Assert.IsTrue(owner.IsFullyIdle);
            StringAssert.Contains(owner.BuildSummaryLog(0), name + "{");
        }
        finally
        {
            nextReporter.TrySetResult(oldReporter);
            await reusedWorker;
            if (owner.IsStarted)
            {
                await WaitForFullyIdleAsync(owner);
            }
        }
    }

    /// <summary>
    /// 進捗observerの失敗を局所診断し、警告先の通常完了・例外の双方で他observerへ開始・終端を届けます。要求identityと成功集計を終端まで維持します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProgressChanged_CapturesRequestIdentityAndIsolatesObserverFailure(bool warningThrows)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<StartupBackgroundTaskProgressSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
        var warnings = new ConcurrentQueue<string>();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(logWarning: message =>
        {
            warnings.Enqueue(message);
            if (warningThrows) { throw new InvalidOperationException("warning log failure"); }
        });
        owner.ProgressChanged += _ => throw new InvalidOperationException("display failure");
        owner.ProgressChanged += snapshot =>
        {
            notifications.Enqueue(snapshot);
            if (!snapshot.IsRunning)
            {
                ended.TrySetResult(snapshot);
            }
        };
        owner.Reset(false, 17);
        long generation = owner.CurrentGeneration;
        owner.Queue("playlist_library_index_prewarm", "startup", null, async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
        });
        owner.Start();
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsTrue(notifications.TryPeek(out StartupBackgroundTaskProgressSnapshot? running));
            Assert.IsTrue(running.IsRunning);
            Assert.IsTrue(running.IsPostInitialization);
            Assert.AreEqual(generation, running.Generation);
            Assert.AreEqual(new OperationProgressRequest(generation, 17, "scheduler:playlist_library_index_prewarm", running.Version), running.Request);
        }
        finally { release.TrySetResult(true); await WaitForFullyIdleAsync(owner); }
        await TestUiDispatcherHost.AwaitNotificationAsync(ended.Task, WaitForFullyIdleAsync(owner), nameof(owner));
        StartupBackgroundTaskProgressSnapshot terminal = await ended.Task;
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(2, notifications.Count);
        Assert.IsFalse(terminal.IsRunning);
        Assert.AreEqual(generation, terminal.Generation);
        Assert.IsTrue(terminal.Version > 0);
        Assert.AreEqual(notifications.First().Request, terminal.Request);
        Assert.AreEqual(2, warnings.Count);
        Assert.IsTrue(warnings.All(message => message.Contains("progress notification failed name=playlist_library_index_prewarm", StringComparison.Ordinal)
            && message.Contains("display failure", StringComparison.Ordinal)));
        StringAssert.Contains(owner.BuildSummaryLog(0L), "playlist_library_index_prewarm{queued=1,started=1,completed=1,failed=0,");
    }

    /// <summary>
    /// resetを跨いでqueue済み処理を実行しても受付時の世代・operation tokenを維持し、新しい要求として表示しない。
    /// </summary>
    [TestMethod]
    public async Task Reset_QueuedExecutionPreservesAcceptedRequestIdentity()
    {
        var notifications = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        owner.ProgressChanged += notifications.Enqueue;
        owner.Reset(false, 41);
        long acceptedGeneration = owner.CurrentGeneration;
        owner.Queue("playlist_library_index_prewarm", "accepted", null, () => Task.CompletedTask);
        owner.Reset(false, 42);
        Assert.AreNotEqual(acceptedGeneration, owner.CurrentGeneration);
        owner.Start();
        await WaitForFullyIdleAsync(owner);
        StartupBackgroundTaskProgressSnapshot[] execution = notifications.ToArray();
        Assert.AreEqual(2, execution.Length);
        Assert.IsTrue(execution.All(snapshot => snapshot.Generation == acceptedGeneration));
        Assert.IsTrue(execution.All(snapshot => snapshot.Request?.OperationToken == 41));
        Assert.AreEqual(execution[0].Request, execution[1].Request);
        Assert.IsTrue(owner.IsFullyIdle);
    }

    /// <summary>
    /// 実playlist URL補完の受付を新旧ExecutionContextから呼び、捕捉世代を保ちながら現行要求だけを独立行へ表示する。
    /// </summary>
    /// <remarks>実playlist受付と進捗hubへの接続を使い、scheduler単体では観測できない世代捕捉と表示選別を確認する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaylistUrlCompletion_RealAcceptanceCapturesIndependentCurrentGenerationOrRetainsOldContext(bool oldContext)
    {
        string directory = Path.Combine(Path.GetTempPath(), nameof(StartupBackgroundTaskSchedulerOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acceptance = null;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            string songDbPath = Path.Combine(directory, "song.db");
            using (var db = new LR2SongDBExtended(songDbPath)) { }
            PlaylistPersistenceRepository.EnsureSchema(songDbPath);
            var playlist = new TestBmsPlaylist(songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                new TestLr2PlaylistFolderSynchronizationPort(songDbPath))
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => owner.Queue(name, reason, dependency, work);
            var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
            var notifications = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
            bool displayed = false;
            bool displayedAsChild = false;
            owner.ProgressChanged += status =>
            {
                if (status.Name != "playlist_url_completion") { return; }
                notifications.Enqueue(status);
                hub.UpdateBackgroundTaskProgress(status);
                if (status.IsRunning)
                {
                    displayed = hub.Rows.Any(row => row.Key.StartsWith("background:playlist_url_completion:", StringComparison.Ordinal));
                    displayedAsChild = hub.Rows.Any(row => row.Key.StartsWith("background:playlist_url_completion:", StringComparison.Ordinal) && row.IsChild);
                }
            };
            owner.Reset(false, 11);
            long originalGeneration = owner.CurrentGeneration;
            async Task accept()
            {
                await release.Task;
                playlist.SchedulePlaylistUrlCompletionRefresh("SettingDialog.SaveSettings");
            }
            if (oldContext) { acceptance = Task.Run(accept); }
            else { using (ExecutionContext.SuppressFlow()) { acceptance = Task.Run(accept); } }
            owner.Reset(false, 22);
            long currentGeneration = owner.CurrentGeneration;
            hub.BeginBackgroundProgressGeneration(currentGeneration);
            hub.BeginStartupBackgroundInitializationPresentation(22, currentGeneration);
            owner.Queue("chart_info_hydration", "startup_completed", null, () => Task.CompletedTask);
            owner.Start();
            await WaitForFullyIdleAsync(owner);
            release.TrySetResult(true);
            await acceptance;
            await WaitForFullyIdleAsync(owner);
            StartupBackgroundTaskProgressSnapshot[] captured = notifications.ToArray();
            Assert.AreEqual(2, captured.Length);
            Assert.AreEqual(oldContext ? originalGeneration : currentGeneration, captured[0].Generation);
            Assert.AreEqual(oldContext ? 11L : 0L, captured[0].Request.OperationToken);
            Assert.AreEqual("scheduler:playlist_url_completion", captured[0].Request.Source);
            Assert.AreEqual(captured[0].Request, captured[1].Request);
            Assert.AreEqual(!oldContext, displayed);
            Assert.IsFalse(displayedAsChild);
            Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("background:playlist_url_completion:", StringComparison.Ordinal)));
        }
        finally
        {
            release.TrySetResult(true);
            if (acceptance != null) { await acceptance; }
            owner.RequestShutdown("test_cleanup");
            if (owner.IsStarted) { await WaitForFullyIdleAsync(owner); }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Start前に受理した処理は開始を待ち、Start後に一度実行して全終端へ到達する。
    /// </summary>
    [TestMethod]
    public async Task QueueBeforeStartWaitsUntilSchedulerStarts()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            Assert.IsTrue(owner.Queue("playlist_library_index_prewarm", "startup", null, async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }));
            Assert.IsFalse(entered.Task.IsCompleted);

            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            release.SetResult(true);
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            release.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 未開始予約の取消は一度だけ成立してその予約を破棄し、実行や二重discardを起こさない。
    /// </summary>
    [TestMethod]
    public async Task CancelQueued_DiscardsOnlyTheReservedRequestExactlyOnce()
    {
        int runs = 0;
        int discards = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation reservation = Reserve(owner, "playlist_virtual_order_prewarm");
        Assert.IsNotNull(reservation);
        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "startup",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref discards)));

        Assert.IsTrue(owner.CancelQueued(reservation, "startup_reset"));
        Assert.IsFalse(owner.CancelQueued(reservation, "duplicate_reset"));
        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, runs);
        Assert.AreEqual(1, discards);
    }

    /// <summary>
    /// 取消ログとdiscard callbackが失敗しても処理を破棄してidle通知を届け、schedulerを停止状態に残さない。
    /// </summary>
    [TestMethod]
    public async Task CancelQueued_DiagnosticFailureStillDiscardsAndNotifies()
    {
        int discardCount = 0;
        int shutdownProbeCount = 0;
        var idleNotified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(
            isShutdownRequested: () =>
            {
                Interlocked.Increment(ref shutdownProbeCount);
                return false;
            },
            schedulerIdleChanged: (_, _) => idleNotified.TrySetResult(true),
            logInfo: message =>
            {
                if (message.Contains(" cancelled ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("cancel log failure");
                }
            },
            logWarning: _ => throw new InvalidOperationException("warning log failure"));
        StartupBackgroundTaskReservation reservation = Reserve(owner, "library_folder_tree_refresh");

        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "diagnostic_failure",
            "missing_dependency",
            () => Task.CompletedTask,
            _ =>
            {
                Interlocked.Increment(ref discardCount);
                throw new InvalidOperationException("discard callback failure");
            }));
        owner.Start();
        int probeBeforeCancel = Volatile.Read(ref shutdownProbeCount);

        Assert.IsTrue(owner.CancelQueued(reservation, "diagnostic_cancel"));
        await idleNotified.Task;
        Assert.IsTrue(Volatile.Read(ref shutdownProbeCount) > probeBeforeCancel);
        Assert.AreEqual(1, Volatile.Read(ref discardCount));
        Assert.IsTrue(owner.IsFullyIdle);
    }

    /// <summary>
    /// 同世代で小さいまたは同じowner sequenceの予約を拒否し、先に受理した予約を一度だけ実行する。
    /// </summary>
    [TestMethod]
    public async Task SameGenerationLowerOrEqualOwnerSequenceCannotReplaceCurrentReservation()
    {
        int runs = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        long generation = owner.CurrentGeneration;
        StartupBackgroundTaskReservation first = owner.Reserve(
            "playlist_virtual_order_prewarm",
            generation,
            ownerSequence: 100L);

        Assert.IsNotNull(first);
        Assert.IsNull(owner.Reserve("playlist_virtual_order_prewarm", generation, ownerSequence: 99L));
        Assert.IsNull(owner.Reserve("playlist_virtual_order_prewarm", generation, ownerSequence: 100L));
        Assert.IsTrue(owner.QueueReserved(
            first,
            "first",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(1, Volatile.Read(ref runs));
    }

    /// <summary>
    /// 同世代の新しいowner sequenceだけを実行し、旧予約の投入や取消が新予約を変更しない。
    /// </summary>
    [TestMethod]
    public async Task HigherOwnerSequenceReplacesLowerAndOldCancelCannotAffectIt()
    {
        int oldRuns = 0;
        int newRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        long generation = owner.CurrentGeneration;
        StartupBackgroundTaskReservation oldReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            generation,
            ownerSequence: 200L);
        StartupBackgroundTaskReservation newReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            generation,
            ownerSequence: 201L);

        Assert.IsNotNull(oldReservation);
        Assert.IsNotNull(newReservation);
        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old",
            () =>
            {
                Interlocked.Increment(ref oldRuns);
                return Task.CompletedTask;
            }));
        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));
        Assert.IsFalse(owner.CancelQueued(oldReservation, "old_cancel"));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(0, Volatile.Read(ref oldRuns));
        Assert.AreEqual(1, Volatile.Read(ref newRuns));
    }

    /// <summary>
    /// reset後の新世代は小さいsequenceでも予約を受理し、旧世代の未投入予約を実行しない。
    /// </summary>
    [TestMethod]
    public async Task ResetIsolatesOwnerSequenceOrderingByGeneration()
    {
        int runs = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        long oldGeneration = owner.CurrentGeneration;
        StartupBackgroundTaskReservation oldReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            oldGeneration,
            ownerSequence: 500L);

        owner.Reset(startImmediately: false);
        long newGeneration = owner.CurrentGeneration;
        StartupBackgroundTaskReservation newReservation = owner.Reserve(
            "playlist_virtual_order_prewarm",
            newGeneration,
            ownerSequence: 1L);

        Assert.IsNotNull(newReservation);
        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old_generation",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));
        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new_generation",
            () =>
            {
                Interlocked.Increment(ref runs);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(1, Volatile.Read(ref runs));
    }

    /// <summary>
    /// 新予約の投入後に届く旧同名予約を拒否し、queueされた新処理を旧処理で置換しない。
    /// </summary>
    [TestMethod]
    public async Task OldLateSubmitCannotReplaceNewSameNameQueue()
    {
        int oldRuns = 0;
        int newRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation oldReservation = Reserve(owner, "playlist_virtual_order_prewarm");
        StartupBackgroundTaskReservation newReservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            newReservation,
            "new",
            () =>
            {
                Interlocked.Increment(ref newRuns);
                return Task.CompletedTask;
            }));
        Assert.IsFalse(owner.QueueReserved(
            oldReservation,
            "old_late",
            () =>
            {
                Interlocked.Increment(ref oldRuns);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, oldRuns);
        Assert.AreEqual(1, newRuns);
    }

    /// <summary>
    /// 同じ予約の再投入を開始前に拒否し、取消では最初のdiscardだけを呼んで二重受付を起こさない。
    /// </summary>
    [TestMethod]
    public async Task SameReservationReplayBeforeStartIsRejectedAndFirstDiscardIsRetained()
    {
        int firstRuns = 0;
        int firstDiscards = 0;
        int replayDiscards = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation reservation = Reserve(owner, "playlist_virtual_order_prewarm");

        Assert.IsTrue(owner.QueueReserved(
            reservation,
            "first_reason",
            "first_dependency",
            () =>
            {
                Interlocked.Increment(ref firstRuns);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref firstDiscards)));
        Assert.IsFalse(owner.QueueReserved(
            reservation,
            "replay_reason",
            "replay_dependency",
            () =>
            {
                Interlocked.Increment(ref firstRuns);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref replayDiscards)));

        Assert.IsTrue(owner.CancelQueued(reservation, "test_cancel"));
        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, Volatile.Read(ref firstRuns));
        Assert.AreEqual(1, Volatile.Read(ref firstDiscards));
        Assert.AreEqual(0, Volatile.Read(ref replayDiscards));
        StringAssert.Contains(owner.BuildSummaryLog(0L), "playlist_virtual_order_prewarm{queued=1,");
    }

    /// <summary>
    /// reset後もqueue済み予約の取消を許し、未投入の旧予約と旧世代の新予約は拒否する。
    /// </summary>
    [TestMethod]
    public async Task ResetRetainsQueuedReservationButRejectsOldUnsubmittedReservation()
    {
        int retainedRuns = 0;
        int retainedDiscards = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        StartupBackgroundTaskReservation retainedReservation = Reserve(owner, "playlist_virtual_order_prewarm");
        StartupBackgroundTaskReservation preSubmitReservation = Reserve(owner, "library_folder_tree_refresh");

        Assert.IsTrue(owner.QueueReserved(
            retainedReservation,
            "retained",
            () =>
            {
                Interlocked.Increment(ref retainedRuns);
                return Task.CompletedTask;
            },
            _ => Interlocked.Increment(ref retainedDiscards)));
        long staleGeneration = owner.CurrentGeneration;
        owner.Reset(startImmediately: false);

        Assert.IsTrue(owner.CancelQueued(retainedReservation, "reset_cancel"));
        Assert.IsFalse(owner.CancelQueued(retainedReservation, "duplicate_cancel"));
        Assert.IsNull(Reserve(owner, "stale_generation", staleGeneration));
        Assert.IsFalse(owner.QueueReserved(
            preSubmitReservation,
            "stale",
            () =>
            {
                Interlocked.Increment(ref retainedRuns);
                return Task.CompletedTask;
            }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);
        Assert.AreEqual(0, retainedRuns);
        Assert.AreEqual(1, retainedDiscards);
    }

    /// <summary>
    /// 実行開始でdequeueした予約を再投入できず、開始済み処理を重複実行しない。
    /// </summary>
    [TestMethod]
    public async Task DequeuedReservationCannotBeSubmittedAgainAfterRunningStarts()
    {
        int runs = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            StartupBackgroundTaskReservation reservation = Reserve(owner, "playlist_virtual_order_prewarm");

            Assert.IsTrue(owner.QueueReserved(
                reservation,
                "first",
                async () =>
                {
                    Interlocked.Increment(ref runs);
                    entered.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                }));

            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(owner.QueueReserved(
                reservation,
                "same_receipt_after_dequeue",
                () =>
                {
                    Interlocked.Increment(ref runs);
                    return Task.CompletedTask;
                }));

            release.SetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(1, Volatile.Read(ref runs));
        }
        finally
        {
            release.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 旧処理のdequeueで新しい未投入予約を失効させず、旧終端後に新予約も一度実行する。
    /// </summary>
    [TestMethod]
    public async Task DequeueKeepsNewerPreSubmitReservationCurrent()
    {
        int oldRuns = 0;
        int newRuns = 0;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            StartupBackgroundTaskReservation oldReservation = Reserve(owner, "playlist_virtual_order_prewarm");

            Assert.IsTrue(owner.QueueReserved(
                oldReservation,
                "old",
                async () =>
                {
                    Interlocked.Increment(ref oldRuns);
                    entered.TrySetResult(true);
                    await release.Task.ConfigureAwait(false);
                }));
            StartupBackgroundTaskReservation newReservation = Reserve(owner, "playlist_virtual_order_prewarm");

            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsTrue(owner.QueueReserved(
                newReservation,
                "new",
                () =>
                {
                    Interlocked.Increment(ref newRuns);
                    return Task.CompletedTask;
                }));

            release.SetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(1, Volatile.Read(ref oldRuns));
            Assert.AreEqual(1, Volatile.Read(ref newRuns));
        }
        finally
        {
            release.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// folder treeと補完の後続処理中も必須idleは成立し、後続の実終端までは全体idleが成立しない。
    /// </summary>
    [DataTestMethod]
    [DataRow("library_folder_tree_refresh", "post_initialization_folder_tree_refresh")]
    [DataRow("chart_info_backfill", "post_initialization_default")]
    public async Task PostInitializationOwnerWorkUsesItsLaneWithoutBlockingRequiredIdle(string name, string lane)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            Assert.IsTrue(owner.Queue(name, "deferred", null, async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }));
            owner.Start();

            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsTrue(owner.IsIdle);
            Assert.IsFalse(owner.IsFullyIdle);
            release.SetResult(true);
            await WaitForFullyIdleAsync(owner);

            StringAssert.Contains(
                owner.BuildSummaryLog(0L),
                name + "{queued=1,started=1,completed=1,failed=0,lastStatus=done,lastMs=");
            StringAssert.Contains(
                owner.BuildSummaryLog(0L),
                "lane=" + lane);
        }
        finally
        {
            release.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 後続GCは実行中の必須処理が終端へ到達してから開始し、必須処理と同時に実行しない。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationGarbageCollectionWaitsForRequiredWorkToBecomeIdle()
    {
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("chart_info_hydration", "required", null, async () =>
            {
                requiredEntered.TrySetResult(true);
                await requiredRelease.Task.ConfigureAwait(false);
            });
            owner.Queue("post_initialize_gc", "post", null, () =>
            {
                postEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();
            owner.Start();

            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(postEntered.Task.IsCompleted);
            Assert.IsFalse(owner.IsIdle);

            requiredRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(postEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            requiredRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 後続GCは必須登録の終了と遅れて登録した必須処理の終端を待ち、登録途中の空queueを完了として扱わない。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationGarbageCollectionWaitsForRequiredSchedulingClosure()
    {
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("post_initialize_gc", "post", null, () =>
            {
                postEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            owner.Start();
            owner.MarkPostInitializationSchedulingComplete();

            Assert.IsFalse(postEntered.Task.IsCompleted);

            owner.Queue("chart_info_hydration", "required", null, async () =>
            {
                requiredEntered.TrySetResult(true);
                await requiredRelease.Task.ConfigureAwait(false);
            });
            Assert.IsFalse(postEntered.Task.IsCompleted);

            owner.MarkRequiredInitializationSchedulingComplete();
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(postEntered.Task.IsCompleted);

            requiredRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(postEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            requiredRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 導入済み保守は必須登録が閉じるまで開始せず、遅れて追加した必須処理の終端後に開始する。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationInstallableMaintenanceWaitsForRequiredSchedulingClosureAndLateRequiredEnrollment()
    {
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("installable_maintenance", "post", null, () =>
        {
            maintenanceEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        owner.Start();

        try
        {
            Assert.IsFalse(maintenanceEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());
            owner.MarkPostInitializationSchedulingComplete();
            Assert.IsFalse(maintenanceEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            owner.Queue("chart_info_hydration", "late-required", null, async () =>
            {
                requiredEntered.TrySetResult(true);
                await requiredRelease.Task.ConfigureAwait(false);
            });
            Assert.IsFalse(maintenanceEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            owner.MarkRequiredInitializationSchedulingComplete();
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(maintenanceEntered.Task.IsCompleted, owner.DescribeWaitState());

            requiredRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            requiredRelease.TrySetResult(true);
        }
    }

    /// <summary>
    /// 開始済み導入済み保守の終端までは後から受理した必須処理を開始せず、共通の実行排他を維持する。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationInstallableMaintenanceBlocksFollowingRequiredWorkUntilTerminal()
    {
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("installable_maintenance", "post", null, async () =>
        {
            maintenanceEntered.TrySetResult(true);
            await maintenanceRelease.Task.ConfigureAwait(false);
        });
        owner.MarkRequiredInitializationSchedulingComplete();
        owner.MarkPostInitializationSchedulingComplete();
        owner.Start();

        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            owner.Queue("chart_info_hydration", "late-required", null, async () =>
            {
                requiredEntered.TrySetResult(true);
                await requiredRelease.Task.ConfigureAwait(false);
            });
            Assert.IsFalse(requiredEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            maintenanceRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            requiredRelease.SetResult(true);
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            maintenanceRelease.TrySetResult(true);
            requiredRelease.TrySetResult(true);
        }
    }

    /// <summary>
    /// 必須処理が失敗しても終端として後続保守を開始し、失敗と成功を別々の診断に記録する。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationInstallableMaintenanceStartsAfterRequiredFaultReachesTerminal()
    {
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("chart_info_hydration", "required", null, async () =>
        {
            requiredEntered.TrySetResult(true);
            await requiredRelease.Task.ConfigureAwait(false);
            throw new InvalidOperationException("expected required failure");
        });
        owner.Queue("installable_maintenance", "post", null, () =>
        {
            maintenanceEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        owner.MarkRequiredInitializationSchedulingComplete();
        owner.MarkPostInitializationSchedulingComplete();
        owner.Start();

        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(maintenanceEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            requiredRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);

            StringAssert.Contains(owner.BuildSummaryLog(0L), "chart_info_hydration{queued=1,started=1,completed=0,failed=1,");
            StringAssert.Contains(owner.BuildSummaryLog(0L), "installable_maintenance{queued=1,started=1,completed=1,failed=0,");
        }
        finally
        {
            requiredRelease.TrySetResult(true);
        }
    }

    /// <summary>
    /// custom folder修復は必須登録の終了と遅い必須処理の終端を待ち、登録途中に物理出力を開始しない。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationCustomFolderRepairWaitsForRequiredSchedulingClosureAndLateRequiredEnrollment()
    {
        var repairEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("playlist_custom_folder_output_repair", "post", null, () =>
            {
                repairEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            owner.Start();

            Assert.IsFalse(repairEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());
            owner.MarkPostInitializationSchedulingComplete();
            Assert.IsFalse(repairEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            owner.Queue("chart_info_hydration", "late-required", null, async () =>
            {
                requiredEntered.TrySetResult(true);
                await requiredRelease.Task.ConfigureAwait(false);
            });
            Assert.IsFalse(repairEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            owner.MarkRequiredInitializationSchedulingComplete();
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(repairEntered.Task.IsCompleted, owner.DescribeWaitState());

            requiredRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(repairEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            requiredRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 開始済みcustom folder修復の終端まで後続保守を開始せず、両処理を順に実行して終端を記録する。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationCustomFolderRepairBlocksFollowingRequiredMaintenanceUntilTerminal()
    {
        var repairEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("playlist_custom_folder_output_repair", "post", null, async () =>
            {
                repairEntered.TrySetResult(true);
                await repairRelease.Task.ConfigureAwait(false);
            });
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();
            owner.Start();

            await TestUiDispatcherHost.AwaitNotificationAsync(repairEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            owner.Queue("installable_maintenance", "following-required", null, () =>
            {
                maintenanceEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            Assert.IsFalse(maintenanceEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            repairRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);

            string summary = owner.BuildSummaryLog(0L);
            StringAssert.Contains(summary, "playlist_custom_folder_output_repair{queued=1,started=1,completed=1,failed=0,lastStatus=done,lastMs=");
            StringAssert.Contains(summary, "installable_maintenance{queued=1,started=1,completed=1,failed=0,lastStatus=done,lastMs=");
        }
        finally
        {
            repairRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 登録終了の前後いずれの終了要求でも未開始folder修復を一度だけ破棄し、受理済み保守を実終端まで回収する。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShutdownDiscardsUnstartedCustomFolderRepairExactlyOnceWhileRequiredMaintenanceDrains(bool schedulingCompleted)
    {
        bool shutdownRequested = false;
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repairDiscarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int discardCount = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested);
        owner.Queue("playlist_custom_folder_output_repair", "post", null, () => Task.CompletedTask, _ =>
        {
            if (Interlocked.Increment(ref discardCount) == 1) { repairDiscarded.TrySetResult(true); }
        });
        owner.Queue("installable_maintenance", "required", null, async () =>
        {
            maintenanceEntered.TrySetResult(true);
            await maintenanceRelease.Task.ConfigureAwait(false);
        });
        try
        {
            if (schedulingCompleted)
            {
                owner.MarkRequiredInitializationSchedulingComplete();
                owner.MarkPostInitializationSchedulingComplete();
            }
            owner.Start();
            if (schedulingCompleted) { await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner)); }
            else { Assert.IsFalse(maintenanceEntered.Task.IsCompleted); }
            shutdownRequested = true;
            owner.RequestShutdown("window_close");
            Assert.IsTrue(repairDiscarded.Task.IsCompletedSuccessfully);
            await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.AreEqual(1, Volatile.Read(ref discardCount));
            Assert.IsFalse(owner.IsFullyIdle);
            maintenanceRelease.TrySetResult(true);
            await WaitForFullyIdleAsync(owner);
            StringAssert.Contains(owner.BuildSummaryLog(0L), "installable_maintenance{queued=1,started=1,completed=1,failed=0,lastStatus=done,lastMs=");
        }
        finally
        {
            shutdownRequested = true;
            maintenanceRelease.TrySetResult(true);
            owner.RequestShutdown("test_cleanup");
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 古い世代の必須登録終了は新世代のGCを開始させず、現世代の終了通知だけで実行を許す。
    /// </summary>
    [TestMethod]
    public async Task StaleRequiredSchedulingClosureCannotReleaseNewGenerationGarbageCollection()
    {
        var postEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        owner.Queue("post_initialize_gc", "post", null, () =>
        {
            postEntered.TrySetResult(true);
            return Task.CompletedTask;
        });
        long staleGeneration = owner.CurrentGeneration;
        owner.Reset(startImmediately: true);
        owner.MarkPostInitializationSchedulingComplete();

        Assert.IsFalse(owner.MarkRequiredInitializationSchedulingComplete(staleGeneration));
        Assert.IsFalse(postEntered.Task.IsCompleted);

        Assert.IsTrue(owner.MarkRequiredInitializationSchedulingComplete(owner.CurrentGeneration));
        await TestUiDispatcherHost.AwaitNotificationAsync(postEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
        await WaitForFullyIdleAsync(owner);
    }

    /// <summary>
    /// 旧世代GCの実行中はreset後の必須処理も開始せず、世代変更で実行中の排他を失わない。
    /// </summary>
    [TestMethod]
    public async Task RunningGarbageCollectionBlocksRequiredWorkAfterGenerationReset()
    {
        var garbageCollectionEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var garbageCollectionRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("post_initialize_gc", "post", null, async () =>
            {
                garbageCollectionEntered.TrySetResult(true);
                await garbageCollectionRelease.Task.ConfigureAwait(false);
            });
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();
            owner.Start();

            await TestUiDispatcherHost.AwaitNotificationAsync(garbageCollectionEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));

            owner.Reset(startImmediately: true);
            owner.Queue("chart_info_hydration", "required", null, () =>
            {
                requiredEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();

            // 旧世代のGC gateを保持している間、新しい必須処理が開始しないことを否定観測する。
            Assert.IsFalse(requiredEntered.Task.Wait(TimeSpan.FromMilliseconds(250)), owner.DescribeWaitState());

            garbageCollectionRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            garbageCollectionRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 後続登録の終了だけでは全体idleにならず、最後の処理終端でidle通知を更新する。
    /// </summary>
    [TestMethod]
    public async Task PostInitializationCompletionRequiresSchedulingClosureAndFullIdle()
    {
        var postRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int idleNotifications = 0;
        int finalIdleNotificationArmed = 0;
        var finalIdleNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(
            schedulerIdleChanged: (_, _) =>
            {
                Interlocked.Increment(ref idleNotifications);
                if (Volatile.Read(ref finalIdleNotificationArmed) != 0)
                {
                    finalIdleNotification.TrySetResult(true);
                }
            });

        owner.Queue("playlist_library_index_prewarm", "post", null, async () =>
        {
            await postRelease.Task.ConfigureAwait(false);
        });
        owner.Start();
        owner.MarkPostInitializationSchedulingComplete();

        Assert.IsTrue(owner.IsPostInitializationSchedulingComplete);
        Assert.IsFalse(owner.IsFullyIdle);

        int notificationsBeforeFinalRelease = Volatile.Read(ref idleNotifications);
        Volatile.Write(ref finalIdleNotificationArmed, 1);
        postRelease.SetResult(true);
        await Task.WhenAll(
            WaitForFullyIdleAsync(owner),
            finalIdleNotification.Task);
        Assert.IsTrue(Volatile.Read(ref idleNotifications) > notificationsBeforeFinalRelease);
    }

    /// <summary>
    /// queue・実行中・全終端の各段階でbacklog数を実作業と一致させ、開始や終端の計上を落とさない。
    /// </summary>
    [TestMethod]
    public async Task CaptureWorkSnapshot_ReportsQueuedRunningAndIdleBacklog()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            Assert.IsTrue(owner.Queue("playlist_library_index_prewarm", "startup", null, async () =>
            {
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }));

            StartupBackgroundWorkSnapshot queued = owner.CaptureWorkSnapshot();
            Assert.AreEqual(1, queued.QueuedCount);
            Assert.AreEqual(0, queued.RunningCount);
            Assert.AreEqual(1, queued.BacklogCount);

            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            StartupBackgroundWorkSnapshot running = owner.CaptureWorkSnapshot();
            Assert.AreEqual(0, running.QueuedCount);
            Assert.AreEqual(1, running.RunningCount);
            Assert.AreEqual(1, running.BacklogCount);

            release.SetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(
                new StartupBackgroundWorkSnapshot(0, 0),
                owner.CaptureWorkSnapshot());
        }
        finally
        {
            release.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 必須maintenanceの実終端まで依存する導入済み保守を開始せず、依存順で一度ずつ実行する。
    /// </summary>
    [TestMethod]
    public async Task DependencyAndPriorityOrderingArePreserved()
    {
        var order = new ConcurrentQueue<string>();
        var dependencyRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenanceEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("maintenance_hydration", "startup", null, async () =>
            {
                order.Enqueue("maintenance");
                maintenanceEntered.TrySetResult(true);
                await dependencyRelease.Task.ConfigureAwait(false);
            });
            owner.Queue("installable_maintenance", "startup", "maintenance_hydration", () =>
            {
                order.Enqueue("installable");
                return Task.CompletedTask;
            });
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();
            owner.Start();

            await TestUiDispatcherHost.AwaitNotificationAsync(maintenanceEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            CollectionAssert.AreEqual(new[] { "maintenance" }, order.ToArray());
            dependencyRelease.SetResult(true);
            await WaitForFullyIdleAsync(owner);

            CollectionAssert.AreEqual(new[] { "maintenance", "installable" }, order.ToArray());
        }
        finally
        {
            dependencyRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 同じlaneの未開始要求はURL補完・参照反映・外部同期の優先順で実行し、先着順で重要処理を後回しにしない。
    /// </summary>
    [TestMethod]
    public async Task HigherPriorityRequestStartsBeforeEarlierLowerPriorityRequestInSameLane()
    {
        var order = new ConcurrentQueue<string>();
        var highPriorityEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var highPriorityRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("external_playlist_sync", "low", null, async () =>
            {
                order.Enqueue("low");
                await Task.CompletedTask.ConfigureAwait(false);
            });
            owner.Queue("playlist_ref_apply", "middle", null, async () =>
            {
                order.Enqueue("middle");
                await Task.CompletedTask.ConfigureAwait(false);
            });
            owner.Queue("playlist_url_completion", "high", null, async () =>
            {
                order.Enqueue("high");
                highPriorityEntered.TrySetResult(true);
                await highPriorityRelease.Task.ConfigureAwait(false);
            });

            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(highPriorityEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            CollectionAssert.AreEqual(new[] { "high" }, order.ToArray());

            highPriorityRelease.SetResult(true);
            await WaitForFullyIdleAsync(owner);
            CollectionAssert.AreEqual(new[] { "high", "middle", "low" }, order.ToArray());
        }
        finally
        {
            highPriorityRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 同名再実行の最新処理が終端になるまで依存処理を開始せず、旧処理の終端で早く依存を解放しない。
    /// </summary>
    [TestMethod]
    public async Task SameNameRerunBlocksDependentUntilLatestVersionCompletes()
    {
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("playlist_ref_apply", "first", null, async () =>
            {
                firstEntered.TrySetResult(true);
                await firstRelease.Task.ConfigureAwait(false);
            });
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(firstEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));

            owner.Queue("playlist_ref_apply", "latest", null, async () =>
            {
                latestEntered.TrySetResult(true);
                await latestRelease.Task.ConfigureAwait(false);
            });
            owner.Queue("default_after_ref", "dependent", "playlist_ref_apply", () =>
            {
                dependentEntered.TrySetResult(true);
                return Task.CompletedTask;
            });

            firstRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(latestEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.IsFalse(dependentEntered.Task.IsCompleted);

            latestRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(dependentEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            firstRelease.TrySetResult(true);
            latestRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 新しい同名処理の後に旧workerが終わっても完了versionを後退させず、後続の依存処理を停止させない。
    /// </summary>
    [TestMethod]
    public async Task CompletionVersionDoesNotRegressWhenOlderWorkerFinishesAfterLatestWorker()
    {
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentAfterLatestEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentAfterOlderEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        try
        {
            owner.Queue("chart_info_hydration", "first", null, async () =>
            {
                firstEntered.TrySetResult(true);
                await firstRelease.Task.ConfigureAwait(false);
            });
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(firstEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            owner.Queue("chart_info_hydration", "latest", null, async () =>
            {
                latestEntered.TrySetResult(true);
                await latestRelease.Task.ConfigureAwait(false);
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(latestEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));

            owner.Queue("default_after_latest", "dependent", "chart_info_hydration", () =>
            {
                dependentAfterLatestEntered.TrySetResult(true);
                return Task.CompletedTask;
            });

            latestRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(dependentAfterLatestEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));

            firstRelease.SetResult(true);
            await WaitForFullyIdleAsync(owner);

            owner.Queue("default_after_older", "dependent", "chart_info_hydration", () =>
            {
                dependentAfterOlderEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(dependentAfterOlderEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            latestRelease.TrySetResult(true);
            firstRelease.TrySetResult(true);
        }
    }

    /// <summary>
    /// 開始前に同名要求を合流すると最新だけを一度実行し、置換された旧処理を実行しない。
    /// </summary>
    [TestMethod]
    public async Task CoalescingRunsOnlyTheLatestRequest()
    {
        int firstRuns = 0;
        int latestRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        Assert.IsTrue(owner.Queue("playlist_ref_apply", "first", null, () =>
        {
            Interlocked.Increment(ref firstRuns);
            return Task.CompletedTask;
        }));
        Assert.IsTrue(owner.Queue("playlist_ref_apply", "latest", null, () =>
        {
            Interlocked.Increment(ref latestRuns);
            return Task.CompletedTask;
        }));

        owner.Start();
        await WaitForFullyIdleAsync(owner);

        Assert.AreEqual(0, firstRuns);
        Assert.AreEqual(1, latestRuns);
    }

    /// <summary>
    /// 保持した実workerでlaneと全体の同時実行上限を観測し、超過分をqueueに残して解放後に全六件を実行する。
    /// </summary>
    [TestMethod]
    public async Task LaneAndTotalConcurrencyLimitsAreEnforced()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;
        int readHydrationActive = 0;
        int maximumReadHydrationActive = 0;
        int started = 0;
        var twoStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fourStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoReadHydrationsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();

        void RecordStart(bool isReadHydration)
        {
            int currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            if (isReadHydration)
            {
                int currentRead = Interlocked.Increment(ref readHydrationActive);
                UpdateMaximum(ref maximumReadHydrationActive, currentRead);
                if (currentRead == 2)
                {
                    twoReadHydrationsStarted.TrySetResult(true);
                }
            }
            int currentStarted = Interlocked.Increment(ref started);
            if (currentStarted == 2) { twoStarted.TrySetResult(true); }
            if (currentStarted == 3)
            {
                threeStarted.TrySetResult(true);
            }
            if (currentStarted == 4)
            {
                fourStarted.TrySetResult(true);
            }
        }

        void QueueGatedWork(string name, bool isReadHydration)
        {
            owner.Queue(name, "test", null, async () =>
            {
                RecordStart(isReadHydration);
                await release.Task.ConfigureAwait(false);
                if (isReadHydration)
                {
                    Interlocked.Decrement(ref readHydrationActive);
                }
                Interlocked.Decrement(ref active);
            });
        }

        try
        {
            QueueGatedWork("chart_info_hydration", isReadHydration: true);
            QueueGatedWork("default_a", isReadHydration: false);
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(twoStarted.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            QueueGatedWork("chart_info_hydration", isReadHydration: true);

            await TestUiDispatcherHost.AwaitNotificationAsync(Task.WhenAll(threeStarted.Task, twoReadHydrationsStarted.Task), WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 3), owner.CaptureWorkSnapshot());

            QueueGatedWork("chart_info_hydration", isReadHydration: true);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(1, 3), owner.CaptureWorkSnapshot());

            QueueGatedWork("maintenance_hydration", isReadHydration: false);
            await TestUiDispatcherHost.AwaitNotificationAsync(fourStarted.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            QueueGatedWork("default_b", isReadHydration: false);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(2, 4), owner.CaptureWorkSnapshot());

            release.TrySetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(6, Volatile.Read(ref started));
            Assert.IsTrue(Volatile.Read(ref maximumActive) <= 4);
            Assert.IsTrue(Volatile.Read(ref maximumReadHydrationActive) <= 2);
        }
        finally
        {
            release.TrySetResult(true);
            try
            {
                await WaitForFullyIdleAsync(owner);
            }
            catch
            {
                // 保持した全workerを解放し、検査の元の失敗をcleanup失敗で置換しない。
            }
        }
    }

    /// <summary>
    /// resetでqueue済み作業を失わず旧実行中の数も維持し、新世代を合わせた同時実行上限を超えない。
    /// </summary>
    [TestMethod]
    public async Task ResetRetainsQueuedWorkAndPreservesRunningConcurrencyAccounting()
    {
        var oldRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var counters = new ConcurrencyCounters();
        int queuedRuns = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("default_a", "old", null, async () =>
            {
                int currentActive = Interlocked.Increment(ref counters.Active);
                UpdateMaximum(ref counters.MaximumActive, currentActive);
                oldEntered.TrySetResult(true);
                await oldRelease.Task.ConfigureAwait(false);
                Interlocked.Decrement(ref counters.Active);
            });
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(oldEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));

            owner.Queue("default_b", "retained", null, () =>
            {
                Interlocked.Increment(ref queuedRuns);
                return Task.CompletedTask;
            });
            owner.Reset(startImmediately: true);

            QueueGatedWork(owner, "chart_info_hydration", newRelease, counters);
            await TestUiDispatcherHost.AwaitNotificationAsync(counters.OneNewStarted.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            QueueGatedWork(owner, "chart_info_hydration", newRelease, counters);
            QueueGatedWork(owner, "maintenance_hydration", newRelease, counters);
            QueueGatedWork(owner, "default_c", newRelease, counters);
            await TestUiDispatcherHost.AwaitNotificationAsync(counters.ThreeNewStarted.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.AreEqual(3, Volatile.Read(ref counters.NewStarted));
            Assert.AreEqual(4, Volatile.Read(ref counters.Active));
            Assert.AreEqual(4, Volatile.Read(ref counters.MaximumActive));
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(2, 4), owner.CaptureWorkSnapshot());

            oldRelease.SetResult(true);
            newRelease.SetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(4, Volatile.Read(ref counters.NewStarted));
            Assert.AreEqual(1, Volatile.Read(ref queuedRuns));
        }
        finally
        {
            oldRelease.TrySetResult(true);
            newRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// reset後も完了済み依存を保持し、別処理で待つqueue済み後続が解放後に開始できる。
    /// </summary>
    [TestMethod]
    public async Task ResetPreservesCompletedDependencyForRetainedQueuedWork()
    {
        var followupRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var followupEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            owner.Queue("playlist_url_completion", "lane_blocker", null, async () =>
            {
                await followupRelease.Task.ConfigureAwait(false);
            });
            owner.Queue("chart_info_hydration", "dependency", null, () =>
            {
                dependencyCompleted.TrySetResult(true);
                return Task.CompletedTask;
            });
            owner.Queue("external_playlist_sync", "retained", "chart_info_hydration", async () =>
            {
                followupEntered.TrySetResult(true);
                await Task.CompletedTask.ConfigureAwait(false);
            });

            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(dependencyCompleted.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitUntilAsync(owner, () =>
            {
                string summary = owner.BuildSummaryLog(0L);
                return summary.Contains("chart_info_hydration{") && summary.Contains("completed=1");
            });
            Assert.IsFalse(followupEntered.Task.IsCompleted);

            owner.Reset(startImmediately: true);
            Assert.IsFalse(followupEntered.Task.IsCompleted);
            followupRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(followupEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            followupRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 一般受付・予約・予約受付・scheduler開始・work開始の各診断失敗が受理を取り消さず、実workを一回実行して全件・laneの終端と成功集計へ到達することを確認します。
    /// </summary>
    [DataTestMethod]
    [DataRow("queue", false)]
    [DataRow("reservation", true)]
    [DataRow("reserved_submit", true)]
    [DataRow("scheduler_start", false)]
    [DataRow("start", false)]
    public async Task AcceptanceAndStartDiagnosticFailuresStillRunAcceptedWorkOnce(string diagnosticPoint, bool reserved)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int runs = 0;
        int diagnosticFailures = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(logInfo: message =>
        {
            if ((message.StartsWith("startup_background_task " + diagnosticPoint + " ", StringComparison.Ordinal)
                    && message.Contains("name=default_work ", StringComparison.Ordinal))
                || message == "startup_background_task " + diagnosticPoint)
            {
                Interlocked.Increment(ref diagnosticFailures);
                throw new InvalidOperationException("diagnostic failure");
            }
        });
        try
        {
            async Task Work()
            {
                Interlocked.Increment(ref runs);
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            }

            bool accepted = reserved
                ? owner.QueueReserved(Reserve(owner, "default_work"), "diagnostic_failure", Work)
                : owner.Queue("default_work", "diagnostic_failure", null, Work);
            Assert.IsTrue(accepted);
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.AreEqual(1, runs);
            Assert.IsFalse(owner.IsFullyIdle);
            release.TrySetResult(true);
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(1, diagnosticFailures);
            Assert.AreEqual(1, runs);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 0), owner.CaptureWorkSnapshot());
            StringAssert.Contains(owner.BuildSummaryLog(0L), "default_work{queued=1,started=1,completed=1,failed=0,lastStatus=done,");
            int sameLaneRuns = 0;
            Assert.IsTrue(owner.Queue("default_after", "lane_probe", null, () =>
            {
                Interlocked.Increment(ref sameLaneRuns);
                return Task.CompletedTask;
            }));
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(1, sameLaneRuns);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 0), owner.CaptureWorkSnapshot());
        }
        finally
        {
            release.TrySetResult(true);
            owner.Start();
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 完了ログの失敗を実work失敗へ変換せず、同じlaneの依存後続と成功集計を維持します。所有側で処理した期待取消も正常完了として分担します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DoneDiagnosticFailurePreservesSuccessAndDependentExecution(bool consumeExpectedCancellation)
    {
        int workRuns = 0;
        int dependentRuns = 0;
        int doneLogFailures = 0;
        int warningCount = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(
            logInfo: message =>
            {
                if (message.StartsWith("startup_background_task done name=default_work ", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref doneLogFailures);
                    throw new InvalidOperationException("done log failure");
                }
            },
            logWarning: _ => Interlocked.Increment(ref warningCount));
        try
        {
            owner.Queue("default_work", "test", null, () =>
            {
                Interlocked.Increment(ref workRuns);
                if (consumeExpectedCancellation)
                {
                    try { throw new OperationCanceledException("owned cancellation"); }
                    catch (OperationCanceledException) { }
                }
                return Task.CompletedTask;
            });
            owner.Queue("default_dependent", "test", "default_work", () =>
            {
                Interlocked.Increment(ref dependentRuns);
                return Task.CompletedTask;
            });
            owner.Start();
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(1, workRuns);
            Assert.AreEqual(1, dependentRuns);
            Assert.AreEqual(1, doneLogFailures);
            Assert.AreEqual(0, warningCount);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 0), owner.CaptureWorkSnapshot());
            string summary = owner.BuildSummaryLog(0L);
            StringAssert.Contains(summary, "default_work{queued=1,started=1,completed=1,failed=0,lastStatus=done,");
            StringAssert.Contains(summary, "default_dependent{queued=1,started=1,completed=1,failed=0,lastStatus=done,");
        }
        finally
        {
            owner.Start();
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 通常例外とworkから外へ出た取消例外を失敗として計上し、警告先の通常完了・例外の双方で後続処理の並列枠と依存を解放します。終端後の同じlaneも実行でき、保持中は全idleにせずfinallyで全workを待ちます。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task FailureCompletesDependencyAndRecordsSummary(bool cancellationEscapes, bool warningThrows)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int workRuns = 0;
        int dependentRuns = 0;
        var warnings = new ConcurrentQueue<string>();
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(logWarning: message =>
        {
            warnings.Enqueue(message);
            if (warningThrows) { throw new InvalidOperationException("warning log failure"); }
        });
        try
        {
            owner.Queue("maintenance_hydration", "test", null, async () =>
            {
                Interlocked.Increment(ref workRuns);
                entered.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
                if (cancellationEscapes) { throw new OperationCanceledException("expected work cancellation"); }
                throw new InvalidOperationException("expected work failure");
            });
            owner.Queue("installable_maintenance", "test", "maintenance_hydration", () =>
            {
                Interlocked.Increment(ref dependentRuns);
                return Task.CompletedTask;
            });
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            Assert.AreEqual(0, dependentRuns);
            Assert.IsFalse(owner.IsFullyIdle);
            Task drain = WaitForFullyIdleAsync(owner);
            Assert.IsFalse(drain.IsCompleted);
            release.TrySetResult(true);
            await drain;
            Assert.AreEqual(1, workRuns);
            Assert.AreEqual(1, dependentRuns);
            Assert.IsTrue(owner.IsIdle);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 0), owner.CaptureWorkSnapshot());
            Assert.AreEqual(1, warnings.Count);
            StringAssert.Contains(warnings.Single(), cancellationEscapes ? "expected work cancellation" : "expected work failure");
            string summary = owner.BuildSummaryLog(12L);
            StringAssert.Contains(summary, "maintenance_hydration{queued=1,started=1,completed=0,failed=1,lastStatus=failed,");
            StringAssert.Contains(summary, "installable_maintenance{queued=1,started=1,completed=1,failed=0,lastStatus=done,");
            int sameLaneRuns = 0;
            Assert.IsTrue(owner.Queue("maintenance_hydration", "lane_probe", null, () =>
            {
                Interlocked.Increment(ref sameLaneRuns);
                return Task.CompletedTask;
            }));
            await WaitForFullyIdleAsync(owner);
            Assert.AreEqual(1, sameLaneRuns);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 0), owner.CaptureWorkSnapshot());
            StringAssert.Contains(owner.BuildSummaryLog(12L), "maintenance_hydration{queued=2,started=2,completed=1,failed=1,lastStatus=done,");
        }
        finally
        {
            release.TrySetResult(true);
            owner.MarkRequiredInitializationSchedulingComplete();
            owner.MarkPostInitializationSchedulingComplete();
            owner.Start();
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 終了・破棄診断の通常完了と例外を分担し、任意補完をlock外で一回破棄します。再終了要求でも破棄を繰り返さず、新規受付を拒否し、必須workの実終端までdrainを待ちます。
    /// </summary>
    [DataTestMethod]
    [DataRow("external_playlist_sync", false)]
    [DataRow("external_playlist_sync", true)]
    [DataRow("chart_info_backfill", false)]
    [DataRow("chart_info_backfill", true)]
    public async Task ShutdownDrainsRequiredWorkAndDiscardsOtherWorkOutsideLock(string deferredName, bool diagnosticThrows)
    {
        bool shutdownRequested = false;
        var requiredEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requiredRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool discardedProbeSucceeded = false;
        int discardCount = 0;
        int shutdownLogs = 0;
        int discardLogs = 0;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested,
            logInfo: message =>
            {
                if (message.StartsWith("startup_background_task discarded ", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref discardLogs);
                    if (diagnosticThrows) { throw new InvalidOperationException("discard log failure"); }
                }
            },
            logShutdown: _ =>
            {
                Interlocked.Increment(ref shutdownLogs);
                if (diagnosticThrows) { throw new InvalidOperationException("shutdown log failure"); }
            });
        try
        {
            owner.Queue("chart_info_hydration", "shutdown", null, async () =>
            {
                requiredEntered.TrySetResult(true);
                await requiredRelease.Task.ConfigureAwait(false);
            });
            owner.Queue(deferredName, "shutdown", "chart_info_hydration", () => Task.CompletedTask, reason =>
            {
                // 同期discard callbackを保持し、別threadのprobeがowner lockを取得できることを観測する。
                Interlocked.Increment(ref discardCount);
                discardedProbeSucceeded = ProbeOwnerFromAnotherThreadAsync(owner).GetAwaiter().GetResult();
                discarded.TrySetResult(true);
            });
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(requiredEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));

            shutdownRequested = true;
            owner.RequestShutdown("window_close");
            Assert.IsTrue(discarded.Task.IsCompletedSuccessfully);
            Assert.IsTrue(discardedProbeSucceeded);
            Assert.IsFalse(owner.Queue("post_shutdown", "shutdown", null, () => Task.CompletedTask));
            owner.RequestShutdown("repeated_close");
            Assert.AreEqual(1, discardCount);
            Assert.AreEqual(1, discardLogs);
            Assert.AreEqual(2, shutdownLogs);
            Assert.AreEqual(new StartupBackgroundWorkSnapshot(0, 1), owner.CaptureWorkSnapshot());
            Task drain = WaitForFullyIdleAsync(owner);
            Assert.IsFalse(drain.IsCompleted);
            requiredRelease.SetResult(true);
            await drain;
            Assert.AreEqual(1, discardCount);
            string summary = owner.BuildSummaryLog(1L);
            StringAssert.Contains(summary, deferredName + "{queued=1,started=0,completed=1,failed=0,lastStatus=discarded,");
            StringAssert.Contains(summary, "chart_info_hydration{queued=1,started=1,completed=1,failed=0,lastStatus=done,");
        }
        finally
        {
            requiredRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 終了で最新同名要求を破棄したことも終端として記録し、reset後の依存処理を旧version待ちに残さない。
    /// </summary>
    [TestMethod]
    public async Task ShutdownDiscardMarksLatestSameNameVersionComplete()
    {
        bool shutdownRequested = false;
        var firstRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var discarded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependentEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner(() => shutdownRequested);
        try
        {
            owner.Queue("default_a", "first", null, async () =>
            {
                firstEntered.TrySetResult(true);
                await firstRelease.Task.ConfigureAwait(false);
            });
            owner.Start();
            await TestUiDispatcherHost.AwaitNotificationAsync(firstEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            owner.Queue("default_a", "latest", null, () => Task.CompletedTask, reason => discarded.TrySetResult(true));

            shutdownRequested = true;
            owner.RequestShutdown("window_close");
            Assert.IsTrue(discarded.Task.IsCompletedSuccessfully);
            shutdownRequested = false;
            owner.Reset(startImmediately: true);
            owner.Queue("default_after_discard", "dependent", "default_a", () =>
            {
                dependentEntered.TrySetResult(true);
                return Task.CompletedTask;
            });
            firstRelease.SetResult(true);
            await TestUiDispatcherHost.AwaitNotificationAsync(dependentEntered.Task, WaitForFullyIdleAsync(owner), nameof(owner));
            await WaitForFullyIdleAsync(owner);
        }
        finally
        {
            firstRelease.TrySetResult(true);
            if (!owner.IsStarted) { owner.RequestShutdown("test_cleanup"); }
            await WaitForFullyIdleAsync(owner);
        }
    }

    /// <summary>
    /// 実行数の終端計上後にlock外からidle通知を呼び、通知先の別thread再入とidle観測を成立させる。
    /// </summary>
    [TestMethod]
    public async Task IdleNotificationRunsAfterAccountingOutsideOwnerLock()
    {
        StartupBackgroundTaskSchedulerOwner owner = null!;
        var notified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool observedIdle = false;
        bool idleProbeSucceeded = false;
        owner = CreateOwner(schedulerIdleChanged: (_, _) =>
        {
            try
            {
                observedIdle = owner.IsIdle;
                // 通知の生成元であるロックprobeの失敗も、待機側へそのまま渡します。
                idleProbeSucceeded = ProbeOwnerFromAnotherThreadAsync(owner).GetAwaiter().GetResult();
                notified.TrySetResult(true);
            }
            catch (Exception exception) { notified.TrySetException(exception); }
        });

        owner.Queue("playlist_library_index_prewarm", "test", null, () => Task.CompletedTask);
        owner.Start();

        await notified.Task;
        Assert.IsTrue(observedIdle);
        Assert.IsTrue(idleProbeSucceeded);
        await WaitForFullyIdleAsync(owner);
    }

    private sealed class ConcurrencyCounters
    {
        internal int Active;

        internal int MaximumActive;

        internal int NewStarted;

        internal TaskCompletionSource<bool> OneNewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> ThreeNewStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void QueueGatedWork(
        StartupBackgroundTaskSchedulerOwner owner,
        string name,
        TaskCompletionSource<bool> release,
        ConcurrencyCounters counters)
    {
        owner.Queue(name, "reset", null, async () =>
        {
            int currentActive = Interlocked.Increment(ref counters.Active);
            UpdateMaximum(ref counters.MaximumActive, currentActive);
            int newStarted = Interlocked.Increment(ref counters.NewStarted);
            if (newStarted == 1) { counters.OneNewStarted.TrySetResult(true); }
            if (newStarted == 3)
            {
                counters.ThreeNewStarted.TrySetResult(true);
            }
            await release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref counters.Active);
        });
    }

    private static async Task<bool> ProbeOwnerFromAnotherThreadAsync(StartupBackgroundTaskSchedulerOwner owner)
    {
        try
        {
            await Task.Run(owner.DescribeWaitState)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static long nextOwnerReservationSequence;

    private static StartupBackgroundTaskReservation Reserve(
        StartupBackgroundTaskSchedulerOwner owner,
        string name)
    {
        return owner.Reserve(
            name,
            owner.CurrentGeneration,
            Interlocked.Increment(ref nextOwnerReservationSequence));
    }

    private static StartupBackgroundTaskReservation Reserve(
        StartupBackgroundTaskSchedulerOwner owner,
        string name,
        long expectedGeneration)
    {
        return owner.Reserve(
            name,
            expectedGeneration,
            Interlocked.Increment(ref nextOwnerReservationSequence));
    }

    private static StartupBackgroundTaskSchedulerOwner CreateOwner(
        Func<bool>? isShutdownRequested = null,
        Action<long, long>? schedulerIdleChanged = null,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null,
        Action<string>? logShutdown = null)
    {
        var notificationProbe = new SchedulerNotificationProbe();
        return new StartupBackgroundTaskSchedulerOwner(
            isShutdownRequested ?? (() => false),
            logInfo ?? (_ => { }),
            logWarning ?? (_ => { }),
            logShutdown ?? (_ => { }),
            value => value ?? string.Empty,
            (generation, revision) =>
            {
                notificationProbe.Publish(generation, revision);
                schedulerIdleChanged?.Invoke(generation, revision);
            },
            notificationProbe);
    }

    private static Task WaitForFullyIdleAsync(StartupBackgroundTaskSchedulerOwner owner)
    {
        return WaitUntilAsync(owner, () => owner.IsFullyIdle);
    }

    private static async Task WaitUntilAsync(StartupBackgroundTaskSchedulerOwner owner, Func<bool> predicate)
    {
        var notificationProbe = (SchedulerNotificationProbe)owner.ProgressSynchronization;
        while (!predicate())
        {
            Task<(long Generation, long Revision)> notification = notificationProbe.CaptureNextNotification();
            if (predicate())
            {
                return;
            }
            await notification.ConfigureAwait(false);
        }
    }

    private sealed class SchedulerNotificationProbe
    {
        private readonly object syncRoot = new();

        private TaskCompletionSource<(long Generation, long Revision)> nextNotification = CreateNotificationSource();

        internal Task<(long Generation, long Revision)> CaptureNextNotification()
        {
            lock (syncRoot)
            {
                return nextNotification.Task;
            }
        }

        internal void Publish(long generation, long revision)
        {
            TaskCompletionSource<(long Generation, long Revision)> notification;
            lock (syncRoot)
            {
                notification = nextNotification;
                nextNotification = CreateNotificationSource();
            }
            notification.TrySetResult((generation, revision));
        }

        private static TaskCompletionSource<(long Generation, long Revision)> CreateNotificationSource()
        {
            return new TaskCompletionSource<(long Generation, long Revision)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
            {
                return;
            }
        }
    }
}
