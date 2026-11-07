using System.Collections;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Ribbit.BMS;
using static BeMusicSeeker.TestSupport.Components.PlaybackPanelTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class PlaybackPanelViewModelTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>同じ行への先行入力と後続入力が、それぞれの実Task・Ready・世代を受け取り、受付拒否は以前の開始を受け取りません。</summary>
    [TestMethod]
    public async Task InputReceiptBelongsToItsOwnAcceptedAction()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("chart.bms");
        File.WriteAllText(path, "#BPM 120");
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var player = new PreloadBmsPlayer
        {
            BeginOperation = _ =>
            {
                if (++starts == 1) { return new(firstReady.Task, completion.Task); }
                secondEntered.TrySetResult();
                return new(secondReady.Task, completion.Task);
            }
        };
        ChartFile chart = ChartTestValues.Empty() with { Path = path };
        var rows = new MainChartListViewModel { Rows = new List<object> { chart } };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var first = new PlaybackInputReceipt();
        var second = new PlaybackInputReceipt();
        Task earlier = panel.HandleTableRowActivation(0, chart, receipt: first);
        Task later = Task.CompletedTask;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, earlier, "first-receipt-start");
            later = panel.HandleTableRowActivation(0, chart, receipt: second);
            firstReady.TrySetResult();
            await earlier;
            await TestUiDispatcherHost.AwaitNotificationAsync(secondEntered.Task, later, "second-receipt-start");
            Assert.IsNotNull(first.ActionTask);
            Assert.IsTrue(first.ActionTask.IsCompletedSuccessfully);
            Assert.IsNotNull(first.Start);
            Assert.AreSame(firstReady.Task, first.Start.Ready);
            secondReady.TrySetResult();
            await later;
            Assert.IsNotNull(second.ActionTask);
            Assert.AreNotSame(first.ActionTask, second.ActionTask);
            Assert.IsNotNull(second.Start);
            Assert.AreSame(secondReady.Task, second.Start.Ready);
            Assert.AreSame(completion.Task, second.Start.Completion);
            Assert.AreNotEqual(first.Start.Generation, second.Start.Generation);
            Assert.AreEqual(path, second.Start.ChartPath);
            Assert.IsNull(first.Failure);
            Assert.IsNull(second.Failure);
            var rejected = new PlaybackInputReceipt();
            await panel.HandleTableRowActivation(-1, chart, receipt: rejected);
            Assert.IsNull(rejected.ActionTask);
            Assert.IsNull(rejected.Start);
        }
        finally
        {
            firstReady.TrySetResult(); secondReady.TrySetResult(); completion.TrySetResult();
            try { await Task.WhenAll(earlier, later); }
            finally { await panel.CloseForShutdown(); }
        }
    }

    /// <summary>通常の候補列から一時配置Bへ進み、実parser入力不良だけなら回収・元名復元・受付解放後にCへ進む。Stopと独立した回収・復元失敗では再開しない。</summary>
    [TestMethod]
    [DataRow("continue")]
    [DataRow("stop")]
    [DataRow("cleanup")]
    [DataRow("restore")]
    public async Task TemporaryInputFailureReturnsToOriginalCursorOnlyAfterSuccessfulCleanup(string condition)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string source = Directory.CreateDirectory(workspace.GetPath("source")).FullName;
        string destination = Directory.CreateDirectory(workspace.GetPath("destination")).FullName;
        string first = workspace.GetPath("A.bms");
        string last = workspace.GetPath("C.bms");
        string bad = Path.Combine(source, "bad.bms");
        string copied = Path.Combine(destination, "bad_.bms");
        File.WriteAllText(first, "#BPM 120\n#00111:01\n");
        File.WriteAllText(last, "#BPM 120\n#00111:01\n");
        File.WriteAllText(bad, "#BPM 120\n#RANDOM 2147483647\n");
        byte[] original = File.ReadAllBytes(bad);
        File.WriteAllText(Path.Combine(destination, "bad.bms"), "existing destination");
        string songDb = workspace.GetPath("song.db");
        File.WriteAllBytes(songDb, []);
        PackageChartEntry entry = ChartPackageTestExtensions.CreateEntryWithInstallDestination(ChartTestValues.Empty() with { Path = bad }, destination);
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { ChartTestValues.Empty() with { Path = first },
            LibraryChartRow.FromChartFile(entry.Chart), ChartTestValues.Empty() with { Path = last } }
        };
        var operations = new ChartFileOperationSynchronizer();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogs = new FakePlaybackDialogService();
        var notices = new List<Exception>();
        var player = new PreloadBmsPlayer
        {
            BeginOperation = path =>
            {
                if (path == copied)
                {
                    Exception failure = Assert.ThrowsExactly<BMSFile.InvalidBmsFileException>(() => PlaybackChart.Load(path));
                    entered.TrySetResult(failure);
                    return new PlaybackStartOperation(ready.Task, ready.Task);
                }
                if (path == last)
                {
                    Assert.IsFalse(operations.IsActive);
                    Assert.IsFalse(File.Exists(copied));
                    CollectionAssert.AreEqual(original, File.ReadAllBytes(bad));
                }
                return new PlaybackStartOperation(Task.CompletedTask, completion.Task);
            }
        };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, failure => notices.Add(failure), operations, temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        panel.AttachLibrary(new TestBmsLibrary(new ApplicationDatabaseScope(), songDb, null, null, null, null,
            new TestUiScheduler(() => null), settings: testSettings));
        FileStream? locked = null;
        Task? navigation = null;
        Task stop = Task.CompletedTask;
        try
        {
            await panel.StartAtIndex(0);
            var input = new PlaybackInputReceipt();
            navigation = panel.HandleNextButton(input);
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, navigation, "temporary-input-ready");
            Assert.IsTrue(operations.IsActive);
            Assert.IsFalse(File.Exists(bad));
            if (condition == "cleanup") { locked = new FileStream(copied, FileMode.Open, FileAccess.Read, FileShare.Read); }
            if (condition == "restore") { File.WriteAllText(bad, "third party"); }
            if (condition == "stop")
            {
                stop = panel.StopPlayback(closeProcess: true);
                Assert.IsNull(panel.NowPlayingChart);
                Assert.IsFalse(stop.IsCompleted, "Ready所有gateの実終端までStopも待ちます。");
            }
            ready.SetException(await entered.Task);
            await Task.WhenAll(navigation, stop);
            Assert.IsNotNull(input.ActionTask);
            Assert.IsTrue(input.ActionTask.IsCompletedSuccessfully, "製品の候補探索終端は開始成功と別です。");
            Assert.IsNotNull(input.Start);
            Assert.AreSame(ready.Task, input.Start.Ready);
            Assert.AreEqual(bad, input.Start.ChartPath);
            Assert.AreEqual(copied, input.Start.PlaybackPath);
            Assert.IsNotNull(input.Failure, "次候補が成功しても最初の実故障を隠しません。");
            Assert.IsFalse(operations.IsActive);
            CollectionAssert.AreEqual(condition == "continue" ? new[] { first, copied, last } : new[] { first, copied }, player.Starts.ToArray());
            Assert.AreEqual(condition == "continue" ? last : null, panel.NowPlayingChart?.Path);
            Assert.AreEqual(condition == "continue" ? 1 : 0, notices.Count);
            if (condition == "continue") { Assert.AreSame(await entered.Task, notices[0]); }
            if (condition is "cleanup" or "restore")
            {
                var aggregate = dialogs.LastPlaybackFailure as AggregateException;
                Assert.IsNotNull(aggregate);
                Exception[] causes = aggregate.Flatten().InnerExceptions.ToArray();
                Assert.AreEqual(2, causes.Length);
                Assert.IsTrue(causes.Any(cause => ReferenceEquals(cause, entered.Task.Result)));
                Assert.IsTrue(causes.Any(cause => cause is IOException));
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual(condition == "cleanup" ? 1 : 0, dialogs.TemporaryFilesRemainNotificationCount);
            }
            else { Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount); }
            Assert.AreEqual("existing destination", File.ReadAllText(Path.Combine(destination, "bad.bms")));
            if (condition == "restore") { Assert.AreEqual("third party", File.ReadAllText(bad)); }
            else { CollectionAssert.AreEqual(original, File.ReadAllBytes(bad)); }
        }
        finally
        {
            locked?.Dispose();
            ready.TrySetCanceled();
            completion.TrySetResult();
            try { await Task.WhenAll(navigation ?? Task.CompletedTask, stop); }
            finally { panel.BeginShutdown(); await panel.CloseForShutdown(); }
        }
    }

    /// <summary>方式の確定は現再生を維持し、候補のない送り・Pause/Resume/Restart/Stop・L Busyではfactoryを消費せず、次の開始だけが捕捉した形式能力を適用し、成功後は再生成しません。</summary>
    /// <remarks>外部から内蔵へのbmson明示起動と、内蔵から外部への通常送りでbmsonを除外することを同じ利用フローで確認します。変更の停止順はFileMutation系列が担当します。</remarks>
    [TestMethod]
    public async Task ConfirmedPlayerFactoryIsUsedOnlyAtNextPlaybackStart()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = Enumerable.Range(0, 4).Select(_ => workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new PreloadBmsPlayer { Completion = completion.Task };
        var second = new PreloadBmsPlayer { SupportsBmson = true, Completion = completion.Task };
        var third = new PreloadBmsPlayer { Completion = completion.Task };
        object[] charts = paths.Select((path, index) => (object)(ChartTestValues.Empty(index is 1 or 2 ? ChartFileKind.Bmson : ChartFileKind.Bms) with { Path = path })).ToArray();
        var rows = new MainChartListViewModel { Rows = charts.ToList(), SelectedIndex = 0 };
        var operations = new ChartFileOperationSynchronizer();
        var panel = new PlaybackPanelViewModel(first, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("再生失敗"), operations, temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        int created = 0;
        var runtime = (ISettingsDialogPlaybackRuntimePort)panel;
        try
        {
            await panel.StartAtIndex(0);
            ChartFile? current = panel.NowPlayingChart;
            runtime.UsePlayerForNextPlayback(() => { created++; return second; }, supportsBmson: true);
            await panel.Previous();
            Assert.AreSame(current, panel.NowPlayingChart);
            Assert.AreEqual(0, created, "先頭で候補のないPreviousは次回要求を消費しません。");
            Assert.AreEqual(0, first.CloseProcessCount);
            Assert.AreSame(current, panel.NowPlayingChart);
            Assert.IsTrue(panel.IsPlaying);
            await panel.Start(forceNewPlay: false);
            Assert.AreEqual(0, created, "一時停止は次の再生開始に含めません。");
            Assert.AreSame(current, panel.NowPlayingChart);
            Assert.IsTrue(panel.IsPaused);
            await panel.Start(forceNewPlay: false);
            Assert.AreSame(current, panel.NowPlayingChart);
            Assert.IsTrue(panel.IsPlaying);
            panel.RestartPlayingBmsFile();
            await panel.StopPlayback(closeProcess: true);
            Assert.AreEqual(0, created, "一時停止・再開・再演奏・停止は次回要求を消費しません。");
            Assert.IsTrue(operations.TryEnter(out IDisposable busy));
            using (busy) { await panel.StartAtIndex(1); }
            Assert.AreEqual(0, created);
            Assert.AreEqual(0, second.Starts.Count, "L Busyを解放するだけでは自動開始しません。");
            int closedBeforeReplacement = first.CloseProcessCount;
            await panel.ExecuteTableRowActivation(1, charts[1]);
            Assert.AreEqual(1, created);
            Assert.AreEqual(closedBeforeReplacement + 1, first.CloseProcessCount);
            int closedAfterReplacement = first.CloseProcessCount;
            CollectionAssert.AreEqual(new[] { paths[1] }, second.Starts.ToArray());
            Assert.AreEqual(paths[1], panel.NowPlayingChart?.Path);
            await panel.StartAtIndex(0);
            Assert.AreEqual(1, created, "採用成功後の明示開始は同じfactoryを再実行しません。");
            Assert.AreEqual(closedAfterReplacement, first.CloseProcessCount, "採用成功後の開始で旧playerを再び終了しません。");
            CollectionAssert.AreEqual(new[] { paths[1], paths[0] }, second.Starts.ToArray());
            Assert.AreEqual(paths[0], panel.NowPlayingChart?.Path);
            runtime.UsePlayerForNextPlayback(() => { created++; return third; }, supportsBmson: false);
            await panel.Next();
            Assert.AreEqual(2, created);
            CollectionAssert.AreEqual(new[] { paths[3] }, third.Starts.ToArray(), "置換後の外部playerには途中のbmsonを渡しません。");
            Assert.AreEqual(paths[3], panel.NowPlayingChart?.Path);
            runtime.UsePlayerForNextPlayback(() => { created++; return second; }, supportsBmson: true);
            int closed = third.CloseProcessCount;
            ChartFile? last = panel.NowPlayingChart;
            await panel.Next();
            Assert.AreEqual(2, created, "末尾で候補のないNextは次回要求を消費しません。");
            Assert.AreEqual(closed, third.CloseProcessCount);
            Assert.AreSame(last, panel.NowPlayingChart);
            Assert.IsTrue(panel.IsPlaying);
        }
        finally
        {
            completion.TrySetResult();
            panel.BeginShutdown();
            await panel.CloseForShutdown();
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    /// <summary>置換の失敗は元原因を返して要求を保持し、置換中の新保存は成功/失敗どちらでも残します。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task PendingPlayerReplacementRetainsFailuresAndNewerRequests(bool failReplacement, bool saveNewRequest)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = Enumerable.Range(0, 3).Select(_ => workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new PreloadBmsPlayer { Completion = completion.Task };
        var second = new PreloadBmsPlayer { SupportsBmson = true, Completion = completion.Task };
        var third = new PreloadBmsPlayer { Completion = completion.Task };
        object[] charts = paths.Select((path, index) => (object)(ChartTestValues.Empty(index == 1 ? ChartFileKind.Bmson : ChartFileKind.Bms) with { Path = path })).ToArray();
        var rows = new MainChartListViewModel { Rows = charts.ToList(), SelectedIndex = 0 };
        var panel = new PlaybackPanelViewModel(first, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("再生失敗"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var runtime = (ISettingsDialogPlaybackRuntimePort)panel;
        var failure = new IOException("旧playerの解放失敗");
        int secondCreated = 0;
        int thirdCreated = 0;
        Task? start = null;
        try
        {
            await panel.StartAtIndex(0);
            first.CloseCompletion = () => { entered.TrySetResult(); return release.Task; };
            runtime.UsePlayerForNextPlayback(() => { secondCreated++; return second; }, supportsBmson: true);
            Task acceptedStart = panel.StartAtIndex(2);
            start = acceptedStart;
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, acceptedStart, "pending-player-close");
            if (saveNewRequest)
            {
                runtime.UsePlayerForNextPlayback(() => { thirdCreated++; return third; }, supportsBmson: false);
            }
            if (failReplacement) { release.SetException(failure); }
            else { release.SetResult(); }
            if (failReplacement)
            {
                Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<IOException>(() => acceptedStart));
                Assert.AreEqual(0, second.Starts.Count, "置換失敗を旧playerでの代替成功に変えません。");
            }
            else { await acceptedStart; }
            first.CloseCompletion = null;
            await panel.StartAtIndex(2);
            Assert.AreEqual(saveNewRequest ? 1 : 2, secondCreated);
            Assert.AreEqual(saveNewRequest ? 1 : 0, thirdCreated);
            CollectionAssert.AreEqual(new[] { paths[2] }, (saveNewRequest ? third : second).Starts.ToArray());
        }
        finally
        {
            release.TrySetResult();
            first.CloseCompletion = null;
            completion.TrySetResult();
            try
            {
                if (start != null) { try { await start; } catch (IOException) { } }
            }
            finally
            {
                try
                {
                    panel.BeginShutdown();
                    await panel.CloseForShutdown();
                }
                finally { foreach (string path in paths) { File.Delete(path); } }
            }
        }
    }

    /// <summary>通常/一時配置のClose待ち・採用後UI待ちをStopで失効させ、未採用要求の保持と採用済みplayerの再利用、新保存の保持を確認します。</summary>
    /// <remarks>実queue・次回設定port・置換を通し、Close/UIのgateを解放して開始と停止の実終端を回収します。停止中の不要copy、故障通知、自動再開がなく、受理済み面要求だけを一度配送することを観測します。</remarks>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    [DataRow(true, true, true)]
    public async Task PendingPlayerReplacement_StopCancelsStartWithoutConsumingUnadoptedOrNewRequest(
        bool temporaryInstall, bool afterAdoption, bool saveNewRequest)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            var databaseScope = new ApplicationDatabaseScope();
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(root);
            string firstPath = Path.Combine(root, "first.bms");
            string targetPath = Path.Combine(root, "target.bms");
            string destination = Path.Combine(root, "destination");
            File.WriteAllText(firstPath, "first");
            File.WriteAllText(targetPath, "target");
            Directory.CreateDirectory(destination);
            var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new PreloadBmsPlayer { Completion = completion.Task, Duration = TimeSpan.FromSeconds(1) };
            var second = new PreloadBmsPlayer { Completion = completion.Task, Duration = TimeSpan.FromSeconds(2), SupportsBmson = true };
            var third = new PreloadBmsPlayer { Completion = completion.Task };
            ChartFile target = ChartTestValues.Empty() with { Path = targetPath };
            if (temporaryInstall) { target = ChartPackageTestExtensions.CreateEntryWithInstallDestination(target, destination).Chart; }
            var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = firstPath }, target }, SelectedIndex = 0 };
            var dispatcher = new QueuedPlaybackUiDispatcher();
            var dialogs = new FakePlaybackDialogService();
            var operations = new ChartFileOperationSynchronizer();
            var panel = new PlaybackPanelViewModel(first, dispatcher, new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("停止は入力故障ではありません。"), operations, temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            if (temporaryInstall)
            {
                string songDb = Path.Combine(root, "song.db");
                File.WriteAllBytes(songDb, []);
                panel.AttachLibrary(new TestBmsLibrary(databaseScope, songDb, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings));
            }
            var settingsRuntime = (ISettingsDialogPlaybackRuntimePort)panel;
            int secondCreated = 0;
            int thirdCreated = 0;
            int surfaces = 0;
            int started = 0;
            Task start = Task.CompletedTask;
            Task stop = Task.CompletedTask;
            try
            {
                await panel.StartAtIndex(0);
                dispatcher.RunAll();
                panel.PlaybackStarted += (_, _) => started++;
                first.CloseCompletion = () => { closeEntered.TrySetResult(); return releaseClose.Task; };
                settingsRuntime.UsePlayerForNextPlayback(() => { secondCreated++; return second; }, true);
                start = panel.ExecuteTableRowActivation(1, target, () => surfaces++);
                await TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, start, "stop-replacement-close-entered");
                Assert.IsFalse(start.IsCompleted, "旧Closeの実終端前には開始要求を返しません。");
                if (afterAdoption)
                {
                    releaseClose.TrySetResult();
                    await TestUiDispatcherHost.AwaitNotificationAsync(dispatcher.WaitForPendingActionsAsync(1), start, "stop-replacement-ui-pending");
                }
                stop = panel.StopPlayback(closeProcess: true);
                Assert.IsNull(panel.NowPlayingChart, "Stopが世代と表示を先に失効させます。");
                if (saveNewRequest) { settingsRuntime.UsePlayerForNextPlayback(() => { thirdCreated++; return third; }, false); }
                Assert.AreEqual(0, Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count());
                releaseClose.TrySetResult();
                await CompleteQueuedPlaybackUiAsync(Task.WhenAll(start, stop), dispatcher);
                Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual(0, second.Starts.Count);
                Assert.AreEqual(0, third.Starts.Count);
                Assert.AreEqual(0, started);
                Assert.AreEqual(1, surfaces, "受理済みの面選択意図は取消でも一度配送します。");
                Assert.IsNull(panel.NowPlayingChart);
                Assert.IsFalse(panel.IsPlaying);
                Assert.AreEqual(0, rows.SelectedIndex);
                Assert.IsFalse(operations.IsActive, "一時配置のLを要求終端で解放します。");
                Assert.AreEqual(afterAdoption ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(1), panel.CurrentlyPlayingDuration);
                Assert.AreEqual(1, secondCreated);
                Assert.AreEqual(0, thirdCreated, "停止した要求から新保存へ乗り換えません。");
                first.CloseCompletion = null;
                await CompleteQueuedPlaybackUiAsync(panel.StartAtIndex(1), dispatcher);
                Assert.AreEqual(saveNewRequest || afterAdoption ? 1 : 2, secondCreated);
                Assert.AreEqual(saveNewRequest ? 1 : 0, thirdCreated);
                Assert.AreEqual(1, (saveNewRequest ? third : second).Starts.Count);
                Assert.AreSame(target, panel.NowPlayingChart);
                Assert.IsTrue(panel.IsPlaying);
                Assert.IsFalse(operations.IsActive);
                Assert.IsTrue(File.Exists(targetPath));
                Assert.AreEqual(0, Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).Count());
            }
            finally
            {
                releaseClose.TrySetResult();
                first.CloseCompletion = null;
                completion.TrySetResult();
                try { await CompleteQueuedPlaybackUiAsync(Task.WhenAll(start, stop), dispatcher); }
                finally
                {
                    panel.BeginShutdown();
                    try { await CompleteQueuedPlaybackUiAsync(panel.CloseForShutdown(), dispatcher); }
                    finally { DisposeWorkspace(); }
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>factoryの実行中にStopが完了しても、その要求は再び有効化されず、未採用factoryを次の明示開始へ保持します。</summary>
    [TestMethod]
    public async Task PendingPlayerFactory_StopBeforeReturnKeepsRequestForExplicitStart()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("path.tmp");
        File.WriteAllBytes(path, []);
        var first = new FakeBmsPlayer();
        var second = new FakeBmsPlayer();
        var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
        var panel = new PlaybackPanelViewModel(first, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("停止は故障ではありません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        int created = 0;
        ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() =>
        {
            if (++created == 1) { panel.StopPlayback().GetAwaiter().GetResult(); }
            return second;
        }, false);
        try
        {
            await panel.StartAtIndex(0);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.AreEqual(0, second.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            await panel.StartAtIndex(0);
            Assert.AreEqual(2, created);
            Assert.IsTrue(panel.IsPlaying);
            Assert.AreEqual(1, second.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
        }
        finally { panel.BeginShutdown(); await panel.CloseForShutdown(); File.Delete(path); }
    }

    /// <summary>停止で世代が失効していても、旧Closeの実故障を取消へ変えず一度通知し、未採用要求を保持します。</summary>
    [TestMethod]
    public async Task PendingPlayerReplacement_StopDoesNotSuppressOldCloseFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("path.tmp");
        File.WriteAllBytes(path, []);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new PreloadBmsPlayer();
        var second = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
        var panel = new PlaybackPanelViewModel(first, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("Close故障を入力故障へ変えません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        first.CloseCompletion = () => { closeEntered.TrySetResult(); return releaseClose.Task; };
        int created = 0;
        ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return second; }, false);
        var failure = new IOException("旧Closeの実故障");
        Task start = panel.HandleTableRowActivation(0, rows.Rows[0]);
        Task stop = Task.CompletedTask;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, start, "stop-close-failure");
            stop = panel.StopPlayback(closeProcess: true);
            first.CloseCompletion = null;
            releaseClose.TrySetException(failure);
            await Task.WhenAll(start, stop);
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingChart);
            await panel.StartAtIndex(0);
            Assert.AreEqual(2, created);
            Assert.IsTrue(panel.IsPlaying);
        }
        finally
        {
            first.CloseCompletion = null;
            releaseClose.TrySetResult();
            try { await Task.WhenAll(start, stop); }
            finally { panel.BeginShutdown(); await panel.CloseForShutdown(); File.Delete(path); }
        }
    }

    /// <summary>通常・一時配置の受理後に実player交換の統計準備が失敗しても候補を保持し、次の明示開始で再採用し、成功後は再生成しません。</summary>
    /// <remarks>旧曲の実開始から設定runtime portへfactoryを渡し、実queue・受付・ReplacePlayerAsyncと既存probeを通します。故障通知と要求Taskの終端で自動再試行なし・旧session保全・一時経路のL解放を確認し、直接交換自体の失敗保証は既存SettingsRuntimeApplyFailureケースへ分担します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfirmedPlayerFactory_ApplyFailureKeepsCandidateForNextExplicitStart(bool temporaryInstall)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            var databaseScope = new ApplicationDatabaseScope();
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(root);
            string oldPath = Path.Combine(root, "old.bms");
            string targetPath = Path.Combine(root, "target.bms");
            string destination = Path.Combine(root, "destination");
            string songDb = Path.Combine(root, "song.db");
            File.WriteAllText(oldPath, "old chart");
            File.WriteAllText(targetPath, "target chart");
            File.WriteAllBytes(songDb, []);
            Directory.CreateDirectory(destination);
            ChartFile oldChart = ChartTestValues.Empty() with { Path = oldPath, RawTitle = "現在の曲" };
            ChartFile target = ChartTestValues.Empty() with { Path = targetPath, RawTitle = "次の曲" };
            if (temporaryInstall) { target = ChartPackageTestExtensions.CreateEntryWithInstallDestination(target, destination).Chart; }
            var rows = new MainChartListViewModel { Rows = new List<object> { oldChart, target } };
            var first = new FakeBmsPlayer();
            var replacement = new FakeBmsPlayer { ThrowOnTelemetryRead = true };
            var dialogs = new FakePlaybackDialogService();
            var operations = new ChartFileOperationSynchronizer();
            var panel = new PlaybackPanelViewModel(first, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("入力故障へ読み替えません。"), operations, temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            if (temporaryInstall) { panel.AttachLibrary(new TestBmsLibrary(databaseScope, songDb, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings)); }
            int created = 0;
            Task operation = Task.CompletedTask;
            try
            {
                operation = panel.StartAtIndex(0);
                await operation;
                Assert.AreSame(oldChart, panel.NowPlayingChart);
                Assert.IsTrue(panel.IsPlaying);
                ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return replacement; }, supportsBmson: false);
                Task notified = dialogs.WaitForPlaybackFailureAsync();
                operation = panel.HandleTableRowActivation(1, target);
                await TestUiDispatcherHost.AwaitNotificationAsync(notified, operation, "next-player-apply-failure");
                await operation;
                Assert.AreEqual(1, created, "失敗した要求の終端でfactoryを自動再試行しません。");
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.IsInstanceOfType<InvalidOperationException>(dialogs.LastPlaybackFailure);
                Assert.AreSame(oldChart, panel.NowPlayingChart);
                Assert.AreEqual("現在の曲", panel.PlayerHeaderTitle);
                Assert.IsTrue(panel.IsPlaying);
                Assert.AreEqual(0, rows.SelectedIndex);
                Assert.AreEqual(0, first.CloseProcessCount);
                Assert.AreEqual(0, replacement.CloseProcessCount);
                Assert.AreEqual(0, replacement.Commands.Count);
                Assert.IsFalse(operations.IsActive, "採用準備が失敗しても一時試聴のLを解放します。");
                Assert.AreEqual(0, Directory.GetFiles(destination).Length);

                replacement.ThrowOnTelemetryRead = false;
                operation = panel.StartAtIndex(1);
                await operation;
                Assert.AreEqual(2, created, "失敗時に保持したfactoryは次の明示開始で再実行します。");
                Assert.AreEqual(1, first.CloseProcessCount);
                Assert.AreSame(target, panel.NowPlayingChart);
                Assert.IsTrue(panel.IsPlaying);
                Assert.AreEqual(1, replacement.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                Assert.IsFalse(operations.IsActive);
                operation = panel.StartAtIndex(1);
                await operation;
                Assert.AreEqual(2, created, "採用成功後は保留factoryを再実行しません。");
                Assert.AreEqual(1, first.CloseProcessCount);
                Assert.AreEqual(2, replacement.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                Assert.AreSame(target, panel.NowPlayingChart);
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.IsFalse(operations.IsActive);
            }
            finally
            {
                try { await operation; }
                finally
                {
                    panel.BeginShutdown();
                    try { await panel.CloseForShutdown(); }
                    finally { DisposeWorkspace(); }
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>冒頭の競合確認後に別操作がLを先取りすると、Start/Next/Previous/実際の行activation入口はいずれも旧player・保留factory・session・異なる曲名/字幕/作者・表示対象・選択・イベント・実ファイルを変えません。旧終了callbackと後続の明示開始・停止・終了が引き続き有効で、受理後だけ保留playerを一回採用します。</summary>
    [TestMethod]
    [DataRow("Start")]
    [DataRow("Next")]
    [DataRow("Previous")]
    [DataRow("Activate")]
    [DataRow("PlayButton")]
    public async Task TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput(string entry)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            var databaseScope = new ApplicationDatabaseScope();
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "pending.bms");
            string normal = Path.Combine(root, "normal.bms");
            string previousNormal = Path.Combine(root, "previous-normal.bms");
            string destination = Path.Combine(root, "destination");
            string songDb = Path.Combine(root, "song.db");
            Directory.CreateDirectory(destination);
            File.WriteAllText(source, "pending");
            File.WriteAllText(normal, "normal");
            File.WriteAllText(previousNormal, "previous normal");
            File.WriteAllBytes(songDb, []);
            ChartFile pending = ChartPackageTestExtensions.CreateEntryWithInstallDestination(ChartTestValues.Empty() with { Path = source, Title = "新しい曲 [新しい字幕]", RawTitle = "新しい曲", Subtitle = "[新しい字幕]", Artist = "新しい作者" }, destination).Chart;
            var pendingRow = LibraryChartRow.FromChartFile(pending);
            ChartFile normalChart = ChartTestValues.Empty() with { Path = normal, Title = "現在の曲 [現在の字幕]", RawTitle = "現在の曲", Subtitle = "[現在の字幕]", Artist = "現在の作者" };
            // 通常曲はkind/pathで識別するため、前後の開始位置には別の実在pathを使います。
            var rows = new MainChartListViewModel { Rows = new List<object> { normalChart, pendingRow, normalChart with { Path = previousNormal } } };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), entered, release);
            var operations = new ChartFileOperationSynchronizer();
            var player = new FakeBmsPlayer();
            var replacementPlayer = new FakeBmsPlayer();
            int createdPlayers = 0;
            var dialogs = new FakePlaybackDialogService();
            var settings = new InMemoryPlaybackSettingsStore { SinglePlay = true };
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue, settings, dialogs, _ => { }, operations, temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            panel.AttachLibrary(new TestBmsLibrary(databaseScope, songDb, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings));
            Task attempt = Task.CompletedTask;
            IDisposable? otherOperation = null;
            try
            {
                if (entry != "PlayButton") { await panel.StartAtIndex(entry == "Previous" ? 2 : 0); }
                if (entry is "Start" or "PlayButton") { rows.SelectedIndex = 1; }
                if (entry == "Activate") { panel.TogglePause(); }
                ChartFile? oldChart = panel.NowPlayingChart;
                (string PlayerHeaderTitle, string PlayerHeaderSubtitle, string PlayerHeaderArtist, ChartFileStatus, int SelectedIndex) oldState = (panel.PlayerHeaderTitle, panel.PlayerHeaderSubtitle, panel.PlayerHeaderArtist, panel.GetPlaybackStatus(oldChart), rows.SelectedIndex);
                ChartFile? oldDisplayedChart = panel.DisplayedChart;
                string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray();
                int starting = 0;
                int started = 0;
                panel.PlaybackStarting += (_, _) => starting++;
                panel.PlaybackStarted += (_, _) => started++;
                Action<object, EventArgs>? oldExit = player.ExitHandler;
                ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { createdPlayers++; return replacementPlayer; }, supportsBmson: false);
                int surfaceRequests = 0;
                Action requestSurface = () => surfaceRequests++;
                attempt = entry switch
                {
                    "Activate" => panel.HandleTableRowActivation(1, pendingRow, requestSurface),
                    "PlayButton" => panel.HandlePlayButton(requestSurface),
                    _ => Task.Run(() => entry switch
                    {
                        "Start" => panel.Start(requestPlayerSurface: requestSurface),
                        "Next" => panel.Next(),
                        _ => panel.Previous()
                    })
                };
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, attempt, "temporary-admission-candidate");
                Assert.IsTrue(operations.TryEnter(out otherOperation));
                release.Set();
                await attempt;
                Assert.AreSame(oldChart, panel.NowPlayingChart);
                Assert.AreEqual(oldState, (panel.PlayerHeaderTitle, panel.PlayerHeaderSubtitle, panel.PlayerHeaderArtist, panel.GetPlaybackStatus(oldChart), rows.SelectedIndex));
                Assert.AreSame(oldDisplayedChart, panel.DisplayedChart);
                CollectionAssert.AreEqual(files, Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().ToArray());
                Assert.AreEqual(0, Directory.GetFiles(destination).Length);
                Assert.AreEqual(0, starting);
                Assert.AreEqual(0, started);
                Assert.AreEqual(0, surfaceRequests, "停止/一時停止からの競合拒否でも面選択を投稿しません。");
                Assert.AreEqual(0, createdPlayers, "受付拒否では確定済みの次player factoryを消費しません。");
                Assert.AreEqual(0, player.CloseProcessCount, "受付拒否では旧player/processを終了しません。");
                Assert.AreEqual(0, replacementPlayer.Commands.Count);
                Assert.AreEqual(entry == "PlayButton" ? 0 : 1, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                otherOperation.Dispose();
                otherOperation = null;
                Assert.AreSame(oldChart, panel.NowPlayingChart);
                oldExit?.Invoke(player, EventArgs.Empty);
                Assert.IsNull(panel.NowPlayingChart, "拒否で旧世代の終了callbackを無効化しません。");
                await panel.StartAtIndex(1);
                Assert.AreEqual(1, createdPlayers, "次の明示入力が受理された後だけ保留playerを一回採用します。");
                Assert.AreEqual(1, replacementPlayer.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                Assert.AreEqual(1, starting);
                Assert.AreEqual(1, started);
                Assert.IsNotNull(panel.NowPlayingChart);
                Assert.AreEqual(source, panel.NowPlayingChart.Path);
                Assert.AreEqual("新しい曲", panel.PlayerHeaderTitle);
                Assert.AreEqual("[新しい字幕]", panel.PlayerHeaderSubtitle);
                Assert.AreEqual("新しい作者", panel.PlayerHeaderArtist);
                Assert.AreEqual(source, panel.DisplayedChart?.Path);
                await panel.StopPlayback(closeProcess: true);
                await panel.CloseForShutdown();
                Assert.IsFalse(operations.IsActive);
                Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
            }
            finally
            {
                release.Set();
                otherOperation?.Dispose();
                try { await attempt; }
                finally
                {
                    await panel.CloseForShutdown();
                    DisposeWorkspace();
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>入力時の停止・一時停止だけが受付後の面要求を作り、再生中Pauseでは要求しません。受理後失敗/取消と空選択でも面意図を維持します。</summary>
    [TestMethod]
    [DataRow("Stopped", 1)]
    [DataRow("Paused", 1)]
    [DataRow("Playing", 0)]
    [DataRow("Failure", 1)]
    [DataRow("Cancel", 1)]
    [DataRow("Empty", 1)]
    public async Task SurfaceRequest_UsesInputStateAndKeepsAcceptedIntent(string condition, int expectedRequests)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("path.tmp");
        File.WriteAllBytes(path, []);
        var player = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } }, SelectedIndex = condition == "Empty" ? -1 : 0 };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        int requests = 0;
        try
        {
            if (condition is "Paused" or "Playing")
            {
                await panel.StartAtIndex(0);
                if (condition == "Paused") { panel.TogglePause(); }
            }
            if (condition == "Failure") { player.PlayStartException = new InvalidOperationException("開始故障"); }
            if (condition == "Cancel") { player.PlayStartException = new OperationCanceledException(); }
            await panel.Start(false, () => requests++);
            Assert.AreEqual(expectedRequests, requests);
            Assert.AreEqual(condition is "Stopped" or "Paused", panel.IsPlaying);
            Assert.AreEqual(condition == "Empty" ? 0 : 1, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(condition == "Failure" ? 1 : 0, dialogs.PlaybackFailureNotificationCount);
        }
        finally { await panel.CloseForShutdown(); File.Delete(path); }
    }

    /// <summary>Ready待ち中のボタン入力が元々面を要求しない場合、順次実行で後続要求がPause状態を見ても入力時の条件を変えません。</summary>
    [TestMethod]
    public async Task SurfaceRequest_PlayButtonKeepsInputConditionWhileEarlierReadyIsPending()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("path.tmp");
        File.WriteAllBytes(path, []);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task };
        var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task start = panel.StartAtIndex(0);
        Task buttons = Task.CompletedTask;
        int requests = 0;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, start, "surface-input-ready-pending");
            Assert.IsFalse(panel.IsStoppedOrPaused);
            buttons = Task.WhenAll(panel.HandlePlayButton(() => requests++), panel.HandlePlayButton(() => requests++));
            ready.TrySetResult();
            await Task.WhenAll(start, buttons);
            Assert.IsTrue(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
            Assert.AreEqual(0, requests);
            Assert.AreEqual(2, player.Commands.Count(command => command == "Pause"));
        }
        finally
        {
            ready.TrySetResult();
            await Task.WhenAll(start, buttons);
            await panel.CloseForShutdown();
            File.Delete(path);
        }
    }

    /// <summary>面要求をUIへ未配送でも後続の受理を完了でき、世代が変わっても受付順に配送します。投稿またはcallback故障は再生結果を変えません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SurfaceRequest_QueuedFifoAndOptionalFailureDoNotChangePlayback(bool failDispatch)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var player = new FakeBmsPlayer();
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList(), SelectedIndex = 0 };
        var dialogs = new FakePlaybackDialogService();
        var panel = new PlaybackPanelViewModel(player, dispatcher, new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var requests = new List<int>();
        try
        {
            dispatcher.RunAll();
            dispatcher.FailNextDispatch = failDispatch;
            await panel.Start(requestPlayerSurface: () => { requests.Add(1); throw new IOException("任意の面選択故障"); });
            Assert.IsTrue(panel.IsPlaying);
            panel.TogglePause();
            rows.SelectedIndex = 1;
            await panel.Start(requestPlayerSurface: () => requests.Add(2));
            Assert.IsTrue(panel.IsPlaying);
            Assert.AreEqual(0, requests.Count, "面要求の配送を待ちません。");
            dispatcher.RunAll();
            CollectionAssert.AreEqual(failDispatch ? new[] { 2 } : new[] { 1, 2 }, requests);
            Assert.AreEqual(paths[1], panel.NowPlayingChart?.Path);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
            panel.TogglePause();
            await panel.Start(false, () => requests.Add(3));
            panel.BeginShutdown();
            dispatcher.RunAll();
            CollectionAssert.AreEqual(failDispatch ? new[] { 2 } : new[] { 1, 2 }, requests, "終了後は受理済み面要求も配送しません。");
        }
        finally { await panel.CloseForShutdown(); dispatcher.RunAll(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>先行開始のReady待機中にbmson行を起動してから外部方式を保存すると、FIFO後に拒否し、現在曲・選択・header・面・要求を維持します。</summary>
    /// <remarks>実Handle入口のTaskで行解決への到達と拒否終端を観測し、後続bmsの明示開始で要求の残存を確認します。finallyでReadyと行gateを解放し、開始・起動Taskとownerを回収します。</remarks>
    [TestMethod]
    public async Task QueuedRowActivationRechecksPendingCapabilityWithoutChangingCurrentHeader()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = Enumerable.Range(0, 3).Select(_ => workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")).ToArray();
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRow = new ManualResetEventSlim();
        var player = new PreloadBmsPlayer { SupportsBmson = true, Ready = ready.Task, Completion = completion.Task };
        var replacement = new PreloadBmsPlayer { Completion = completion.Task };
        object[] charts = paths.Select((path, index) => (object)(ChartTestValues.Empty(index == 1 ? ChartFileKind.Bmson : ChartFileKind.Bms) with { Path = path, RawTitle = index == 1 ? "拒否対象" : "現在曲" })).ToArray();
        var rows = new MainChartListViewModel { Rows = charts.ToList(), SelectedIndex = 2 };
        var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), enteredRow, releaseRow);
        var dialogs = new FakePlaybackDialogService();
        var settings = new InMemoryPlaybackSettingsStore { PlayerPanelState = PlayerPanelState.TITLE_LARGE };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue,
            settings, dialogs, _ => Assert.Fail("再生失敗"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        int created = 0;
        int surfaceRequests = 0;
        Task start = panel.StartAtIndex(2);
        Task? activation = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, start, "queued-activation-ready");
            ChartFile? current = panel.NowPlayingChart;
            activation = panel.HandleTableRowActivation(1, charts[1], () => surfaceRequests++);
            Assert.AreSame(current, panel.DisplayedChart, "FIFO待機中の未確定行をheaderへ先行表示しません。");
            Assert.AreEqual("現在曲", panel.PlayerHeaderTitle);
            ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return replacement; }, supportsBmson: false);
            ready.SetResult();
            await start;
            await TestUiDispatcherHost.AwaitNotificationAsync(enteredRow.Task, activation, "queued-activation-row");
            releaseRow.Set();
            await activation;
            Assert.AreEqual(0, created);
            Assert.AreEqual(0, player.CloseProcessCount);
            CollectionAssert.AreEqual(new[] { paths[2] }, player.Starts.ToArray());
            Assert.AreSame(current, panel.NowPlayingChart);
            Assert.AreSame(current, panel.DisplayedChart);
            Assert.AreEqual("現在曲", panel.PlayerHeaderTitle);
            Assert.AreEqual(2, rows.SelectedIndex);
            Assert.IsTrue(panel.IsPlaying);
            Assert.AreEqual(0, surfaceRequests);
            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, settings.PlayerPanelState);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
            await panel.HandleTableRowActivation(0, charts[0]);
            Assert.AreEqual(1, created, "拒否した行起動は次回方式の要求を消費しません。");
            CollectionAssert.AreEqual(new[] { paths[0] }, replacement.Starts.ToArray());
            Assert.AreEqual(paths[0], panel.NowPlayingChart?.Path);
        }
        finally
        {
            ready.TrySetResult();
            releaseRow.Set();
            completion.TrySetResult();
            try
            {
                await Task.WhenAll(start, activation ?? Task.CompletedTask);
            }
            finally
            {
                try
                {
                    panel.BeginShutdown();
                    await panel.CloseForShutdown();
                }
                finally { foreach (string path in paths) { File.Delete(path); } }
            }
        }
    }

    /// <summary>Ready待ち中に実UI入口で受け付けた二行をStopで取り消し、全Task終端後も停止を保ちます。次回factoryは取消で消費せず、停止後の明示入力だけが採用します。</summary>
    [TestMethod]
    public async Task QueuedRowActivation_StopCancelsAcceptedRowsAndPreservesNextPlayer()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = Enumerable.Range(0, 4).Select(index => workspace.GetPath($"{index}.bms")).ToArray();
        foreach (string path in paths) { File.WriteAllBytes(path, []); }
        object[] charts = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToArray();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
        var replacement = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = charts.ToList() };
        var dialogs = new FakePlaybackDialogService();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("取消から入力不良通知を出しません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task start = panel.StartAtIndex(0);
        Task second = Task.CompletedTask;
        Task third = Task.CompletedTask;
        Task stop = Task.CompletedTask;
        int created = 0;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, start, "stop-queued-ready");
            second = panel.HandleTableRowActivation(1, charts[1]);
            third = panel.HandleTableRowActivation(2, charts[2]);
            ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return replacement; }, true);
            stop = panel.HandleStopButton();
            Assert.IsNull(panel.NowPlayingChart, "StopのUI受付で現在曲を解除します。");
            ready.SetResult();
            await Task.WhenAll(start, second, third, stop);
            CollectionAssert.AreEqual(new[] { paths[0] }, player.Starts.ToArray());
            Assert.AreEqual(0, replacement.Starts.Count);
            Assert.AreEqual(0, created);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
            await panel.HandleTableRowActivation(3, charts[3]);
            Assert.AreEqual(1, created);
            CollectionAssert.AreEqual(new[] { paths[3] }, replacement.Starts.ToArray());
            Assert.AreEqual(paths[3], panel.NowPlayingChart?.Path);
        }
        finally
        {
            ready.TrySetResult();
            completion.TrySetResult();
            try { await Task.WhenAll(start, second, third, stop); }
            finally { panel.BeginShutdown(); await panel.CloseForShutdown(); }
        }
    }

    /// <summary>停止なしの行activationは先行Ready後に全行を順次開始し、通常選曲の世代更新を入力取消へ読み替えません。</summary>
    [TestMethod]
    public async Task AcceptedRowActivations_KeepEachStartWithoutStop()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = Enumerable.Range(0, 3).Select(index => workspace.GetPath($"{index}.bms")).ToArray();
        foreach (string path in paths) { File.WriteAllBytes(path, []); }
        object[] charts = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToArray();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = charts.ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("入力不良"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task start = panel.StartAtIndex(0);
        Task second = Task.CompletedTask;
        Task third = Task.CompletedTask;
        try
        {
            Func<Task>? secondAction = panel.AcceptTableRowActivation(1, charts[1]);
            Func<Task>? thirdAction = panel.AcceptTableRowActivation(2, charts[2]);
            Assert.IsNotNull(secondAction);
            Assert.IsNotNull(thirdAction);
            second = secondAction();
            third = thirdAction();
            CollectionAssert.AreEqual(new[] { paths[0] }, player.Starts.ToArray());
            Assert.IsFalse(second.IsCompleted);
            Assert.IsFalse(third.IsCompleted);
            ready.SetResult();
            await Task.WhenAll(start, second, third);
            CollectionAssert.AreEqual(paths, player.Starts.ToArray());
            Assert.AreEqual(paths[2], panel.NowPlayingChart?.Path);
        }
        finally
        {
            ready.TrySetResult();
            completion.TrySetResult();
            try { await Task.WhenAll(start, second, third); }
            finally { panel.BeginShutdown(); await panel.CloseForShutdown(); }
        }
    }

    /// <summary>本番受付のB/C→Stop→Dをworkerと異なる順に実行してもA/Dだけを開始します。D先行では旧Closeを呼ばず、Close先行ではDのBeginStartが解放を待ちます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcceptedStop_DoesNotCloseLaterStart(bool closeFirst)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = Enumerable.Range(0, 4).Select(index => workspace.GetPath($"{index}.bms")).ToArray();
        foreach (string path in paths) { File.WriteAllBytes(path, []); }
        object[] charts = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToArray();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = charts.ToList() };
        var dialogs = new FakePlaybackDialogService();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("入力不良"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task stopTask = Task.CompletedTask;
        Task newStart = Task.CompletedTask;
        int closes = 0;
        int closeCompletions = 0;
        try
        {
            await panel.StartAtIndex(0);
            Func<Task>? second = panel.AcceptTableRowActivation(1, charts[1]);
            Func<Task>? third = panel.AcceptTableRowActivation(2, charts[2]);
            Func<Task> stop = panel.AcceptStopPlayback(closeProcess: true);
            Func<Task>? last = panel.AcceptTableRowActivation(3, charts[3]);
            Assert.IsNotNull(second);
            Assert.IsNotNull(third);
            Assert.IsNotNull(last);
            player.BeforeClose = () => closes++;
            player.CloseCompletion = () => { closeCompletions++; closeEntered.TrySetResult(); return releaseClose.Task; };
            if (closeFirst)
            {
                stopTask = stop();
                await TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, stopTask, "accepted-stop-close");
                newStart = last();
                CollectionAssert.AreEqual(new[] { paths[0] }, player.Starts.ToArray());
                Assert.IsFalse(newStart.IsCompleted);
                releaseClose.SetResult();
                await Task.WhenAll(stopTask, newStart);
            }
            else
            {
                newStart = last();
                await newStart;
                stopTask = stop();
                await stopTask;
            }
            await second();
            await third();
            CollectionAssert.AreEqual(new[] { paths[0], paths[3] }, player.Starts.ToArray());
            Assert.AreEqual(closeFirst ? 1 : 0, closes, "D開始後の旧StopはplayerをCloseしません。");
            Assert.AreEqual(closeFirst ? 1 : 0, closeCompletions);
            Assert.AreEqual(paths[3], panel.NowPlayingChart?.Path);
            Assert.IsTrue(panel.IsPlaying);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            releaseClose.TrySetResult();
            completion.TrySetResult();
            try { await Task.WhenAll(stopTask, newStart); }
            finally
            {
                player.BeforeClose = null;
                player.CloseCompletion = null;
                panel.BeginShutdown();
                await panel.CloseForShutdown();
            }
        }
    }

    /// <summary>通常開始AのStop受付後、後続の空選択Playは停止世代を上書きせずAの実Closeと終端を保ちます。空選択判定中の再Stopでも同じ停止所有を維持します。</summary>
    /// <remarks>単一Stopが世代捕捉と現在曲判定の間へ入る到達はsourceの排他順で確認し、このケースは同じ空選択clearの競合を既存queue境界で固定します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptySelectionStart_AfterStopDoesNotInvalidateCapturedClose(bool stopAgain)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("playing.bms");
        File.WriteAllBytes(path, []);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSelection = new ManualResetEventSlim();
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = new List<object> { ChartTestValues.Empty() with { Path = path } } };
        var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), selectionEntered, releaseSelection);
        var dialogs = new FakePlaybackDialogService();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue,
            new InMemoryPlaybackSettingsStore(), dialogs, _ => Assert.Fail("空選択と停止で入力不良を通知しません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Func<Task>? firstStop = null;
        Task play = Task.CompletedTask;
        Task stop = Task.CompletedTask;
        bool closeFinished = false;
        int surfaces = 0;
        try
        {
            await panel.StartAtIndex(0);
            CollectionAssert.AreEqual(new[] { path }, player.Starts.ToArray());
            firstStop = panel.AcceptStopPlayback(closeProcess: true);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.AreEqual(0, player.CloseProcessCount, "最初のStopは受付だけで、AのCloseは未実行です。");
            rows.SelectedIndex = -1;
            if (stopAgain)
            {
                queue.BeforeSelectedIndexRead = () =>
                {
                    queue.BeforeSelectedIndexRead = null;
                    selectionEntered.TrySetResult();
                    releaseSelection.Wait();
                };
            }
            play = panel.HandlePlayButton(() => surfaces++);
            Func<Task> capturedStop = firstStop;
            if (stopAgain)
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(selectionEntered.Task, play, "empty-selection-entered");
                capturedStop = panel.AcceptStopPlayback(closeProcess: true);
                releaseSelection.Set();
            }
            await play;
            CollectionAssert.AreEqual(new[] { path }, player.Starts.ToArray(), "空選択では別の開始を行いません。");
            Assert.AreEqual(1, surfaces, "空選択でも入力時に受理した面要求を維持します。");
            player.CloseCompletion = async () =>
            {
                closeEntered.TrySetResult();
                await releaseClose.Task;
                closeFinished = true;
            };
            if (!stopAgain) { firstStop = null; }
            stop = capturedStop();
            await TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, stop, "empty-selection-stop-close");
            Assert.IsFalse(stop.IsCompleted, "実Closeの終端まで停止Taskを完了させません。");
            releaseClose.SetResult();
            await stop;
            Assert.IsTrue(closeFinished);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            releaseSelection.Set();
            releaseClose.TrySetResult();
            completion.TrySetResult();
            try { await Task.WhenAll(play, stop); }
            finally
            {
                try { if (firstStop != null) { await firstStop(); } }
                finally
                {
                    player.CloseCompletion = null;
                    panel.BeginShutdown();
                    await panel.CloseForShutdown();
                }
            }
        }
    }

    /// <summary>遅い旧世代の終了故障を一度通知し、現在曲とそのPLAY表示は保全します。</summary>
    [TestMethod]
    public async Task NaturalExit_StaleFailurePreservesReplacementAndStillNotifies()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var player = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.StartAtIndex(0);
            Action<object, EventArgs> oldExit = player.ExitHandler ?? throw new AssertFailedException("旧終了通知がありません。");
            await panel.StartAtIndex(1);
            var failure = new IOException("遅い復元故障");
            oldExit(player, new PlaybackExitEventArgs(failure));
            oldExit(player, new PlaybackExitEventArgs(failure));
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(paths[1], panel.NowPlayingChart.Path);
            Assert.IsTrue(panel.IsPlaying);
        }
        finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>曲の途中に一覧・再生モード・位置を変えても先読み候補を差し替えず、次の実選曲は最新一覧へ従って一回準備することを確認します。</summary>
    [TestMethod]
    public async Task SongStart_PreparesOnceAndUsesLiveTargetAfterListAndModeChanges()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount),
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(paths[1], player.Preloads.Single().Path);
            await panel.Start(forceNewPlay: false); // 同じ曲のPause
            panel.CurrentlyPlayingTime = TimeSpan.FromSeconds(1);
            rows.Rows = new List<object> { (ChartTestValues.Empty() with { Path = paths[0] }), (ChartTestValues.Empty() with { Path = paths[2] }) };
            panel.RepeatPlayMode = true;
            Assert.AreEqual(1, player.Preloads.Count, "曲の途中では候補を差し替えません。");
            await panel.Next();
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(paths[2], panel.NowPlayingChart.Path, "先読みしたBではなく現在の送り先Cへ進みます。");
            Assert.AreEqual(paths[0], player.Preloads.Last().Path, "Cの開始で次の一件を準備します。");
        }
        finally
        {
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            foreach (string path in paths) { File.Delete(path); }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>ファイル変更の受付中は再生を拒否し、現在曲の停止終端後だけ書込みを許可します。停止失敗時には物理変更と自動再開を行わないことも確認します。</summary>
    /// <remarks>Ready待ちの代表はmutationの同期停止受付を現在曲解除で観測し、後発UI Stopのworkerを遅らせても変更前停止が実Closeへ合流することを確認します。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task FileMutation_StopsCurrentSongBeforeWritingAndRejectsPlaybackWhileBusy(bool failStop, bool laterUiStop)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!laterUiStop) { ready.SetResult(); }
        var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var operations = new ChartFileOperationSynchronizer();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), operations, temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var store = new PhysicalChartDeletionStore();
        var mutation = new SelectedChartMutationWorkflowOwner(
            () => (BMSLibrary)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(BMSLibrary)),
            operations, new ChartMutationActivityOwner(), panel, new FileDbReportRecordingDialogs(), new UnusedPendingDeletionDialog(), store);
        var target = new ChartOperationTarget(CreatePlaybackTargetChart(ChartFileKind.Bms, paths[1]), null,
            ChartOperationSourceScope.Library, true, false, false, ChartOperationCapabilities.RemoveFromLibrary);
        Task<SelectedChartMutationResult>? deletion = null;
        Task start = Task.CompletedTask;
        Func<Task>? delayedUiStop = null;
        void ObserveStop(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(panel.NowPlayingChart) && panel.NowPlayingChart == null)
            {
                stopAccepted.TrySetResult();
            }
        }
        panel.PropertyChanged += ObserveStop;
        try
        {
            start = panel.StartAtIndex(0);
            await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, start, "playback.mutation-start");
            if (!laterUiStop) { await start; }
            player.CloseCompletion = () => { entered.TrySetResult(); return cleanup.Task; };
            deletion = mutation.DeleteAsync(new SelectedChartDeleteRequest([target], target, MainViewOperationSection.Library));
            if (laterUiStop)
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(stopAccepted.Task, deletion, "playback.mutation-stop-accepted");
                Assert.IsTrue(operations.IsActive);
                delayedUiStop = panel.AcceptStopPlayback(closeProcess: true);
                ready.SetResult();
            }
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, deletion, "playback.mutation-stop");
            await panel.StartAtIndex(1);
            Assert.AreEqual(1, player.Starts.Count, "変更受付中の再生を後から実行する予約にはしません。");
            Assert.AreEqual(0, store.Mutations, "停止完了前にファイルを変更しません。");
            Assert.IsTrue(File.Exists(paths[1]));
            Assert.IsFalse(deletion.IsCompleted, "実Closeの終端前に変更要求を完了させません。");
            if (failStop) { cleanup.SetException(new IOException("停止処理が失敗しました。")); }
            else { cleanup.SetResult(); }
            SelectedChartMutationResult result = await deletion;
            await start;
            Assert.AreEqual(!failStop, result.Succeeded);
            Assert.AreEqual(failStop ? 0 : 1, store.Mutations);
            Assert.IsNull(panel.NowPlayingChart, "変更対象が次曲だけでも現在曲を止めます。");
            Assert.IsTrue(File.Exists(paths[0]));
            Assert.AreEqual(failStop, File.Exists(paths[1]));
            Assert.IsFalse(operations.IsActive);
            Assert.AreEqual(1, player.Starts.Count, "変更後には自動再開しません。");
        }
        finally
        {
            ready.TrySetResult();
            cleanup.TrySetResult();
            completion.TrySetResult();
            try { await Task.WhenAll(start, deletion is null ? Task.CompletedTask : deletion); }
            finally
            {
                panel.PropertyChanged -= ObserveStop;
                player.CloseCompletion = null;
                try { if (delayedUiStop != null) { await delayedUiStop(); } }
                finally
                {
                    await panel.StopPlayback(closeProcess: true);
                    foreach (string path in paths) { File.Delete(path); }
                }
            }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>内蔵playerのReady受信で開始を確定し、演奏CompletionやUI投稿の受信を待たずに次候補を一件準備することを確認します。</summary>
    [TestMethod]
    public async Task InternalReady_StartsNextPreparationBeforeCompletionOrUiPublication()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string firstPath = workspace.GetPath("firstPath.tmp");
        File.WriteAllBytes(firstPath, []);
        string secondPath = workspace.GetPath("secondPath.tmp");
        File.WriteAllBytes(secondPath, []);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = new List<object> { (ChartTestValues.Empty() with { Path = firstPath }), (ChartTestValues.Empty() with { Path = secondPath }) } };
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var panel = new PlaybackPanelViewModel(player, dispatcher, new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount),
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        rows.SelectedIndex = 0;
        int surfaceRequests = 0;
        Task start = panel.Start(requestPlayerSurface: () => surfaceRequests++);
        try
        {
            await player.StartObserved.Task;
            Assert.IsTrue(((ChartFileStatus)panel.PlaybackStatusValue).HasFlag(ChartFileStatus.LOADING));
            Assert.IsFalse(start.IsCompleted);
            Assert.AreEqual(0, player.Preloads.Count);
            ready.SetResult();
            await start;
            Assert.IsTrue(panel.IsPlaying);
            Assert.IsFalse(completion.Task.IsCompleted);
            Assert.AreEqual(secondPath, player.Preloads.Single().Path);
            Assert.AreEqual(0, rows.SelectedIndex);
            Assert.AreEqual(0, surfaceRequests);
            dispatcher.RunAll();
            Assert.AreEqual(1, surfaceRequests);
        }
        finally
        {
            ready.TrySetResult();
            await start;
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            dispatcher.RunAll();
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>先のNextがReady待機中でも後から届いたNextを捨てず、受付順に各対象を一回ずつ開始することを確認します。</summary>
    [TestMethod]
    public async Task ReceivedNextRequests_KeepEachStartWhileEarlierReadyIsPending()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task? firstNext = null;
        Task? secondNext = null;
        try
        {
            await panel.StartAtIndex(0);
            player.Ready = ready.Task;
            firstNext = panel.Next();
            secondNext = panel.Next();
            CollectionAssert.AreEqual(paths.Take(2).ToArray(), player.Starts.ToArray());
            Assert.IsFalse(firstNext.IsCompleted);
            Assert.IsFalse(secondNext.IsCompleted);
            ready.SetResult();
            await Task.WhenAll(firstNext, secondNext);
            CollectionAssert.AreEqual(paths, player.Starts.ToArray());
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(paths[2], panel.NowPlayingChart.Path);
        }
        finally
        {
            ready.TrySetResult();
            if (firstNext != null) { await firstNext; }
            if (secondNext != null) { await secondNext; }
            await panel.StopPlayback(closeProcess: true);
            completion.TrySetResult();
            foreach (string path in paths) { File.Delete(path); }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>Readyの入力不良が開始時または非同期に届いても一回通知して次の有効曲へ進み、同じ失敗や面要求を重ねず、同じ開始内では待機中に保存された次回player要求を消費しません。</summary>
    /// <remarks>同期失敗は既存のskip保証、非同期失敗はReady待機中の次回設定保存と後続の明示開始までを確認します。</remarks>
    [TestMethod]
    [DataRow("bmson", 0)]
    [DataRow("bmson", 1)]
    [DataRow("bmson", 2)]
    [DataRow("bms", 0)]
    [DataRow("bms", 1)]
    [DataRow("bms", 2)]
    [DataRow("data", 1)]
    public async Task InternalReady_InvalidInputNotifiesAndSkipsOnce(string inputKind, int failureBoundary)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string firstPath = workspace.GetPath("firstPath.tmp");
        File.WriteAllBytes(firstPath, []);
        string nextPath = workspace.GetPath("nextPath.tmp");
        File.WriteAllBytes(nextPath, []);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool asynchronous = failureBoundary == 2;
        string invalidPath = workspace.GetPath("invalid." + (inputKind == "bmson" ? "bmson" : "bms"));
        File.WriteAllText(invalidPath, inputKind == "bmson" ? "{\"info\":{\"init_bpm\":0}}" : "#BPM 120\n#RANDOM 2147483647\n");
        Exception failure = inputKind switch
        {
            "bmson" => Assert.ThrowsExactly<Ribbit.BMS.InvalidBmsonFileException>(() => Ribbit.BMS.PlaybackChart.Load(invalidPath)),
            "bms" => Assert.ThrowsExactly<Ribbit.BMS.BMSFile.InvalidBmsFileException>(() => Ribbit.BMS.PlaybackChart.Load(invalidPath)),
            _ => new InvalidDataException("Invalid selected chart.")
        };
        if (!asynchronous) { ready.SetException(failure); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer
        {
            BeginOperation = path => path == firstPath
                ? failureBoundary == 0 ? throw failure : new PlaybackStartOperation(ready.Task, ready.Task)
                : new PlaybackStartOperation(Task.CompletedTask, completion.Task)
        };
        var replacement = new PreloadBmsPlayer { Completion = completion.Task };
        int created = 0;
        int notices = 0;
        var rows = new MainChartListViewModel { Rows = new List<object> { (ChartTestValues.Empty() with { Path = firstPath }), (ChartTestValues.Empty() with { Path = nextPath }) } };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), observed => { Assert.AreSame(failure, observed); notices++; }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        rows.SelectedIndex = 0;
        int surfaceRequests = 0;
        Task start = panel.Start(requestPlayerSurface: () => surfaceRequests++);
        try
        {
            await player.StartObserved.Task;
            if (asynchronous)
            {
                Assert.IsTrue(((ChartFileStatus)panel.PlaybackStatusValue).HasFlag(ChartFileStatus.LOADING));
                ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return replacement; }, supportsBmson: false);
                ready.SetException(failure);
            }
            await start;
            Assert.AreEqual(1, notices);
            Assert.AreEqual(1, surfaceRequests, "次候補へのskipでも面要求を重ねません。");
            CollectionAssert.AreEqual(new[] { firstPath, nextPath }, player.Starts.ToArray());
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(nextPath, panel.NowPlayingChart.Path);
            Assert.IsTrue(panel.IsPlaying);
            Assert.AreEqual(0, created, "候補のskipは新しい再生要求としてpendingを取り直しません。");
            if (asynchronous)
            {
                await panel.StartAtIndex(1);
                Assert.AreEqual(1, created);
                CollectionAssert.AreEqual(new[] { nextPath }, replacement.Starts.ToArray());
            }
        }
        finally
        {
            ready.TrySetException(failure);
            await start;
            completion.TrySetResult();
            panel.BeginShutdown();
            await panel.CloseForShutdown();
            File.Delete(firstPath);
            File.Delete(nextPath);
        }
    }

    /// <summary>短曲の終了callbackがReady前に重複して届いても、Ready後に自然停止を一回だけ実行することを確認します。</summary>
    [TestMethod]
    public async Task InternalShortSong_CallbackAndCompletionBeforeReadyCauseOneNaturalStop()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string path = workspace.GetPath("path.tmp");
        File.WriteAllBytes(path, []);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Ready = ready.Task };
        var rows = new MainChartListViewModel { Rows = new List<object> { (ChartTestValues.Empty() with { Path = path }) } };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task closed = player.WaitForCommandAsync("Close");
        Task start = panel.StartAtIndex(0);
        try
        {
            await player.StartObserved.Task;
            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            Assert.IsFalse(closed.IsCompleted);
            ready.SetResult();
            await start;
            await closed;
            Assert.AreEqual(1, player.CloseProcessCount);
            Assert.IsNull(panel.NowPlayingChart);
        }
        finally
        {
            ready.TrySetResult();
            await start;
            await panel.CloseProcess();
            File.Delete(path);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>単曲再生の自然停止が先読み回収失敗を受け取ると、元の原因を一回通知し停止表示を維持して次曲を始めないことを確認します。</summary>
    [TestMethod]
    public async Task NaturalSinglePlayStop_NotifiesPreloadCleanupFailureOnceWithoutStartingNextSong()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { Completion = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore { SinglePlay = true, RepeatPlay = false }, dialogs,
            _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var failure = new InvalidOperationException("The next-song decoder cleanup failed.");
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(paths[1], player.Preloads.Single().Path);
            player.CloseCompletion = () => { closeEntered.TrySetResult(); return closeCompletion.Task; };

            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            completion.SetResult();
            await closeEntered.Task;
            closeCompletion.SetException(failure);
            await dialogs.WaitForPlaybackFailureAsync();

            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            CollectionAssert.AreEqual(new[] { paths[0] }, player.Starts.ToArray());
            Assert.AreEqual(1, player.CloseProcessCount);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
        }
        finally
        {
            completion.TrySetResult();
            closeCompletion.TrySetResult();
            player.CloseCompletion = null;
            await panel.CloseForShutdown();
            await completion.Task;
            foreach (string path in paths) { File.Delete(path); }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>採用した先読みが停止後に失敗しても元の原因を一回通知し、失効した開始の表示や再生を復活させないことを確認します。</summary>
    [TestMethod]
    public async Task AdoptedPreloadFailure_AfterStopNotifiesOnceWithoutRestarting()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer
        {
            BeginOperation = path =>
            {
                if (path == paths[0]) { return new PlaybackStartOperation(Task.CompletedTask, firstCompletion.Task); }
                nextStartEntered.TrySetResult();
                return new PlaybackStartOperation(nextPreparation.Task, nextPreparation.Task);
            }
        };
        var dialogs = new FakePlaybackDialogService();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), dialogs, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount),
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var failure = new InvalidOperationException("The adopted next-song preparation failed.");
        Task? next = null;
        Task? stop = null;
        try
        {
            await panel.StartAtIndex(0);
            Assert.AreEqual(paths[1], player.Preloads.Single().Path);
            next = panel.Next();
            await TestUiDispatcherHost.AwaitNotificationAsync(nextStartEntered.Task, next, "playback.next-start");
            stop = panel.StopPlayback(closeProcess: true);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.IsFalse(next.IsCompleted);
            Assert.IsFalse(stop.IsCompleted);

            nextPreparation.SetException(failure);
            await Task.WhenAll(next, stop);
            await dialogs.WaitForPlaybackFailureAsync();

            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            CollectionAssert.AreEqual(paths, player.Starts.ToArray());
            Assert.IsNull(panel.NowPlayingChart);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
        }
        finally
        {
            nextPreparation.TrySetCanceled();
            firstCompletion.TrySetResult();
            if (next != null) { await next; }
            if (stop != null) { await stop; }
            await panel.CloseForShutdown();
            await firstCompletion.Task;
            foreach (string path in paths) { File.Delete(path); }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>player交換と終了の双方は旧playerのClose終端を待ち、回収前に新playerの表示値へ切り替えないことを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InternalReplacementAndShutdown_WaitForCloseTerminal(bool shutdown)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer
        {
            Duration = TimeSpan.FromSeconds(10),
            CloseCompletion = () => { entered.TrySetResult(); return cleanup.Task; }
        };
        var replacement = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(20) };
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        Task terminal = shutdown ? panel.CloseForShutdown() : panel.ReplacePlayerAsync(replacement);
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, terminal, "playback.terminal-close");
            Assert.IsFalse(terminal.IsCompleted);
            Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
            cleanup.SetResult();
            await terminal;
            if (!shutdown) { Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration); }
        }
        finally
        {
            cleanup.TrySetResult();
            await terminal;
            await panel.CloseProcess();
        }
    }

    /// <summary>一時配置が必要な次候補はさらに先へ飛ばさず先読みを止め、配置先がなくコピー不要の未導入候補だけを直接準備することを確認します。</summary>
    /// <remarks>実在する譜面と配置先の有無を候補計算へ渡し、コピーは開始せず局所playerの準備要求を観測します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreloadCandidate_DoesNotSkipTemporaryTargetAndAllowsPendingTargetWithoutCopy(bool destinationExists)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            int unexpectedNotificationCount = 0;
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(root);
            string destination = Path.Combine(root, "destination");
            if (destinationExists) { Directory.CreateDirectory(destination); }
            string currentPath = Path.Combine(root, "current.bms");
            string nextPath = Path.Combine(root, "pending.bms");
            string thirdPath = Path.Combine(root, "third.bms");
            foreach (string path in new[] { currentPath, nextPath, thirdPath }) { File.WriteAllText(path, string.Empty); }
            ChartFile nextChart = ChartPackageTestExtensions.CreateEntryWithInstallDestination((ChartTestValues.Empty() with { Path = nextPath }), destination).Chart;
            var rows = new MainChartListViewModel { Rows = new List<object> { (ChartTestValues.Empty() with { Path = currentPath }), LibraryChartRow.FromChartFile(nextChart), (ChartTestValues.Empty() with { Path = thirdPath }) } };
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new PreloadBmsPlayer { Completion = completion.Task };
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            try
            {
                await panel.StartAtIndex(0);
                Assert.AreEqual(destinationExists ? null : nextPath, player.NextSongPath);
                Assert.AreEqual(destinationExists ? 0 : 1, player.Preloads.Count);
                Assert.IsNotNull(panel.NowPlayingChart);
                Assert.AreEqual(currentPath, panel.NowPlayingChart.Path);
                Assert.AreEqual(0, rows.SelectedIndex);
            }
            finally
            {
                await panel.StopPlayback(closeProcess: true);
                completion.TrySetResult();
                DisposeWorkspace();
            }
            Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>通常入力と一時配置入力の外部開始は旧Closeの実終端を待ち、開始呼出しまで入力を保持します。同期失敗と返却Taskの遅い失敗を各一回通知することも確認します。</summary>
    /// <remarks>一時配置の物理コピー・回収または確認から開始への接続を通すため実ライブラリを接続し、playerの実行は局所probeで制御します。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LegacyStart_WaitsForPreviousCloseAndRetainsInputUntilInvocation(
        bool temporaryTarget, bool failAtInvocation)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            var databaseScope = new ApplicationDatabaseScope();
            int unexpectedNotificationCount = 0;
            string directory = workspace.RootDirectory;
            string source = Path.Combine(directory, "source");
            string destination = Path.Combine(directory, "destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string currentPath = Path.Combine(directory, "current.bms");
            string targetPath = Path.Combine(source, "next.bms");
            string playbackPath = temporaryTarget ? Path.Combine(destination, "next.bms") : targetPath;
            File.WriteAllText(currentPath, string.Empty);
            File.WriteAllText(targetPath, string.Empty);
            string songDbPath = Path.Combine(directory, "song.db");
            File.WriteAllBytes(songDbPath, []);
            ChartFile currentFile = (ChartTestValues.Empty() with { Path = currentPath });
            ChartFile targetFile = (ChartTestValues.Empty() with { Path = targetPath });
            object targetRow = temporaryTarget
                ? LibraryChartRow.FromChartFile(ChartPackageTestExtensions
                    .CreateEntryWithInstallDestination(targetFile, destination).Chart)
                : targetFile;
            var chartList = new MainChartListViewModel { Rows = new List<object> { currentFile, targetRow } };
            var player = new FakeBmsPlayer();
            var dialogs = new FakePlaybackDialogService();
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(),
                new MainChartListPlaybackQueue(chartList), new InMemoryPlaybackSettingsStore(),
                dialogs, _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            panel.AttachLibrary(new TestBmsLibrary(databaseScope, songDbPath, null, null, null, null, new TestUiScheduler(() => null), settings: testSettings));
            var closeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseClose = new ManualResetEventSlim();
            Task close = Task.CompletedTask;
            Task start = Task.CompletedTask;
            int startedCount = 0;
            bool inputExistedAtInvocation = false;
            var failure = new InvalidOperationException("external player start failed");
            try
            {
                long generation = panel.BeginPlayback(currentFile, 0);
                Assert.IsTrue(await panel.TryPlayStart(generation, currentPath, null));
                panel.PlaybackStarted += (_, _) => Interlocked.Increment(ref startedCount);
                player.BeforeClose = () => { closeEntered.TrySetResult(); releaseClose.Wait(); };
                close = panel.StopPlayback(closeProcess: true);
                await TestUiDispatcherHost.AwaitNotificationAsync(closeEntered.Task, close, "playback.close-process");
                player.PlayStartTask = completion.Task;
                player.PlayStartException = failAtInvocation ? failure : null;
                player.BeforePlayStart = () => inputExistedAtInvocation = File.Exists(playbackPath);

                start = panel.StartAtIndex(1);

                Assert.IsFalse(start.IsCompleted, "旧playerの終了中は新しい開始呼出しを待ちます。");
                Assert.IsTrue(File.Exists(playbackPath), "開始呼出し前の入力を除去しません。");
                Assert.IsFalse(player.Commands.Contains("PlayStart:" + playbackPath));
                Assert.AreEqual(0, startedCount);
                Assert.IsTrue(((ChartFileStatus)panel.PlaybackStatusValue).HasFlag(ChartFileStatus.LOADING));
                Assert.IsFalse(panel.IsPlaying);
                releaseClose.Set();
                await close;
                await start;

                Assert.IsTrue(inputExistedAtInvocation);
                Assert.AreEqual(1, player.Commands.Count(command => command == "PlayStart:" + playbackPath));
                Assert.IsTrue(File.Exists(targetPath));
                if (temporaryTarget) { Assert.IsFalse(File.Exists(playbackPath)); }
                if (failAtInvocation)
                {
                    Assert.AreSame(failure, dialogs.LastPlaybackFailure);
                    Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                    Assert.AreEqual(0, startedCount);
                    Assert.IsNull(panel.NowPlayingChart);
                }
                else
                {
                    Assert.IsFalse(completion.Task.IsCompleted, "選曲と一時配置はplayerの返却Taskの終了まで保持しません。");
                    Assert.AreEqual(1, startedCount);
                    Assert.IsTrue(panel.IsPlaying);
                    Assert.IsNotNull(panel.NowPlayingChart);
                    Assert.AreEqual(targetPath, panel.NowPlayingChart.Path);
                    Task notified = dialogs.WaitForPlaybackFailureAsync();
                    completion.SetException(failure);
                    await notified;
                    Assert.AreSame(failure, dialogs.LastPlaybackFailure);
                    Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                    Assert.AreEqual(1, startedCount);
                    Assert.IsNull(panel.NowPlayingChart);
                }
            }
            finally
            {
                releaseClose.Set();
                completion.TrySetResult();
                await Task.WhenAll(close, start);
                player.BeforeClose = null;
                await panel.StopPlayback(closeProcess: true);
                DisposeWorkspace();
            }
            Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>通常発見した入れ子パッケージで衝突なし・入力元兄弟・両側複数・ディレクトリ占有から開始名を決め、元入力と第三者のバイト内容・相対配置を保全します。</summary>
    /// <remarks>実コピーを内蔵BeginStartのReadyまで保持し、要求TaskとLの未終端、Completion前の所有物回収を観測します。ディレクトリ占有の代表はReady故障を重ねます。兄弟衝突の一代表はReady途中に実Stopを入力し、通常取消の無通知・元名復元・受付解放と次の明示再試聴を確認します。</remarks>
    [TestMethod]
    [DataRow(0, "chart.bms", false, false)]
    [DataRow(1, "chart__.bms", false, false)]
    [DataRow(2, "chart_____.bms", false, false)]
    [DataRow(3, "chart___.bms", false, false)]
    [DataRow(3, "chart___.bms", true, false)]
    [DataRow(1, "chart__.bms", false, true)]
    public async Task TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion(int collision, string expectedName, bool failReady, bool stopBeforeReady)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            PlaybackPanelViewModel? panel = null;
            Task start = Task.CompletedTask;
            Task retry = Task.CompletedTask;
            Task stop = Task.CompletedTask;
            try
            {
                string source = Path.Combine(root, "source");
                string destination = Path.Combine(root, "destination");
                Directory.CreateDirectory(Path.Combine(source, "charts"));
                Directory.CreateDirectory(Path.Combine(destination, "charts/extra"));
                string chartPath = Path.Combine(source, "charts", "chart.bms");
                const string chartText = "#PLAYER 1\n#TITLE Nested\n#WAV01 extra/kick.wav\n#STAGEFILE extra/banner.png\n#00111:01\n";
                File.WriteAllText(chartPath, chartText);
                File.WriteAllText(Path.Combine(source, "root.bms"), "#PLAYER 1\n#TITLE Root\n#STAGEFILE root-stage.png\n");
                File.WriteAllText(Path.Combine(source, "root-stage.png"), "root background");
                string[] resources = ["charts/extra/kick.wav", "charts/other/kick.wav", "charts/extra/banner.png", "top.wav"];
                foreach (string resource in resources)
                {
                    string path = Path.Combine(source, resource);
                    string? parent = Path.GetDirectoryName(path);
                    Assert.IsNotNull(parent);
                    Directory.CreateDirectory(parent);
                    File.WriteAllText(path, resource);
                }
                string[] sourceCollisions = collision switch
                {
                    1 => ["chart_.bms"],
                    2 => ["chart_.bms", "chart___.bms"],
                    _ => []
                };
                string[] destinationCollisions = collision switch
                {
                    1 or 3 => ["chart.bms"],
                    2 => ["chart.bms", "chart__.bms", "chart____.bms"],
                    _ => []
                };
                foreach (string name in sourceCollisions)
                {
                    File.WriteAllText(Path.Combine(source, "charts", name), "#PLAYER 1\n#TITLE Source sibling " + name + "\n");
                }
                foreach (string name in destinationCollisions)
                {
                    File.WriteAllText(Path.Combine(destination, "charts", name), "existing destination " + name);
                }
                if (collision == 3)
                {
                    Directory.CreateDirectory(Path.Combine(source, "charts/chart_.bms"));
                    File.WriteAllBytes(Path.Combine(source, "charts/chart_.bms/keep.bin"), [0, 127, 255]);
                    Directory.CreateDirectory(Path.Combine(destination, "charts/chart__.bms"));
                    File.WriteAllBytes(Path.Combine(destination, "charts/chart__.bms/keep.bin"), [255, 0, 127]);
                }
                File.WriteAllText(Path.Combine(destination, "charts/extra/banner.png"), "existing image");
                string outside = Path.Combine(root, "outside.wav");
                File.WriteAllText(outside, "outside package");
                string songDb = Path.Combine(root, "song.db");
                File.WriteAllBytes(songDb, []);
                (Dictionary<string, byte[]> Files, string[] Directories) sourceBefore = ReadTree(source);
                (Dictionary<string, byte[]> Files, string[] Directories) destinationBefore = ReadTree(destination);
                byte[] chartBytes = File.ReadAllBytes(chartPath);
                byte[] outsideBytes = File.ReadAllBytes(outside);
                ChartPackage package = new BmsLibraryPackageInstallService()
                    .SearchChartPackagesRecursivelyWithMetadata(source).Packages.Single();
                Assert.AreEqual(source, package.path);
                Assert.IsTrue(package.ChartEntries.Count >= 2);
                Assert.IsTrue(package.ChartEntries.Any(entry => entry.Chart.Path == Path.Combine(source, "root.bms")));
                var failure = new InvalidOperationException("内蔵Readyの元原因");
                var dialogs = new FakePlaybackDialogService();
                var player = new PreloadBmsPlayer { Ready = ready.Task, Completion = completion.Task };
                var operations = new ChartFileOperationSynchronizer();
                panel = CreateTemporaryInstallPanel(testSettings, chartPath, destination, songDb,
                    player, dialogs, out _, operations, discoveredPackage: package, uiScheduler: new TestUiScheduler(() => null));
                string expectedPath = Path.Combine(destination, "charts", expectedName);
                start = panel.StartAtIndex(0);
                await TestUiDispatcherHost.AwaitNotificationAsync(player.StartObserved.Task, start, "temporary-ready-start");
                string copiedPath = await player.StartObserved.Task;
                Assert.AreEqual(expectedPath, copiedPath);
                Assert.IsFalse(player.LastAllowPreload);
                Assert.IsTrue(operations.IsActive, "Ready前から一時配置の権限を保持します。");
                Assert.IsFalse(start.IsCompleted, "実開始要求はReadyを待ちます。");
                CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(copiedPath));
                CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(Path.Combine(source, "charts", expectedName)));
                Assert.AreEqual(collision == 0, File.Exists(chartPath), "衝突なしなら元名を保ち、改名時は最終名へ移動します。");
                foreach (KeyValuePair<string, byte[]> file in sourceBefore.Files.Where(pair => pair.Key != Path.Combine("charts", "chart.bms")))
                {
                    CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(Path.Combine(source, file.Key)), file.Key);
                }
                foreach (KeyValuePair<string, byte[]> file in destinationBefore.Files)
                {
                    CollectionAssert.AreEqual(file.Value, File.ReadAllBytes(Path.Combine(destination, file.Key)), file.Key);
                }
                foreach (string resource in resources.Where(path => !path.EndsWith("banner.png", StringComparison.Ordinal)))
                {
                    Assert.AreEqual(resource, File.ReadAllText(Path.Combine(destination, resource)));
                }
                Assert.AreEqual("existing image", File.ReadAllText(Path.Combine(destination, "charts/extra/banner.png")));
                Assert.IsFalse(File.Exists(Path.Combine(destination, "outside.wav")));
                if (stopBeforeReady)
                {
                    stop = panel.HandleStopButton();
                    Assert.IsNull(panel.NowPlayingChart);
                    Assert.IsFalse(start.IsCompleted, "停止だけでは準備Taskと所有コピーの回収は完了しません。");
                    Assert.IsTrue(operations.IsActive);
                    CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(copiedPath));
                    CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(Path.Combine(source, "charts", expectedName)));
                    ready.SetCanceled();
                }
                else if (failReady) { ready.SetException(failure); }
                else { ready.SetResult(); }
                await Task.WhenAll(start, stop);
                Assert.IsFalse(operations.IsActive, "回収と元名復元の終端後にLを解放します。");
                AssertTree(source, sourceBefore);
                AssertTree(destination, destinationBefore);
                CollectionAssert.AreEqual(outsideBytes, File.ReadAllBytes(outside));
                Assert.IsFalse(completion.Task.IsCompleted, "演奏Taskの終端まで一時配置や受付を保持しません。");
                Assert.AreEqual(failReady ? 1 : 0, dialogs.PlaybackFailureNotificationCount);
                if (failReady)
                {
                    Assert.AreSame(failure, dialogs.LastPlaybackFailure);
                    Assert.IsNull(panel.NowPlayingChart);
                }
                else if (stopBeforeReady)
                {
                    Assert.IsNull(panel.NowPlayingChart);
                    Assert.IsFalse(panel.IsPlaying);
                    Assert.IsFalse(panel.IsPaused);
                }
                else
                {
                    Assert.IsNotNull(panel.NowPlayingChart);
                    Assert.AreEqual(chartPath, panel.NowPlayingChart.Path);
                }
                if (collision == 0 || failReady || stopBeforeReady)
                {
                    await panel.StopPlayback(closeProcess: true);
                    player.Ready = Task.CompletedTask;
                    player.BeforePlayStart = () =>
                    {
                        CollectionAssert.AreEqual(chartBytes, File.ReadAllBytes(expectedPath));
                        Assert.AreEqual("charts/extra/kick.wav", File.ReadAllText(Path.Combine(destination, "charts/extra/kick.wav")));
                    };
                    retry = panel.StartAtIndex(0);
                    await retry;
                    CollectionAssert.AreEqual(new[] { expectedPath, expectedPath }, player.Starts.ToArray());
                    Assert.AreEqual(failReady ? 1 : 0, dialogs.PlaybackFailureNotificationCount);
                    Assert.IsTrue(panel.IsPlaying);
                    Assert.IsFalse(operations.IsActive);
                    AssertTree(source, sourceBefore);
                    AssertTree(destination, destinationBefore);
                }
            }
            finally
            {
                ready.TrySetResult();
                completion.TrySetResult();
                try { await Task.WhenAll(start, stop, retry, completion.Task); }
                finally
                {
                    try { if (panel != null) { await panel.CloseForShutdown(); } }
                    finally { if (Directory.Exists(root)) { DisposeWorkspace(); } }
                }
            }

            static (Dictionary<string, byte[]> Files, string[] Directories) ReadTree(string path) =>
                (Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                    .ToDictionary(file => Path.GetRelativePath(path, file), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase),
                Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                    .Select(directory => Path.GetRelativePath(path, directory)).ToArray());

            static void AssertTree(string path, (Dictionary<string, byte[]> Files, string[] Directories) expected)
            {
                (Dictionary<string, byte[]> Files, string[] Directories) actual = ReadTree(path);
                CollectionAssert.AreEquivalent(expected.Files.Keys.ToArray(), actual.Files.Keys.ToArray());
                CollectionAssert.AreEquivalent(expected.Directories, actual.Directories);
                foreach (KeyValuePair<string, byte[]> file in expected.Files) { CollectionAssert.AreEqual(file.Value, actual.Files[file.Key], file.Key); }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>外部player開始後の別Taskが入れ子譜面と相対音源を読めることをgateで観測し、開始Task終端後の回収・元名復元・既存宛先保全を確認します。</summary>
    /// <remarks>一時配置の物理コピー・回収または確認から開始への接続を通すため実ライブラリを接続し、playerの実行は局所probeで制御します。</remarks>
    [TestMethod]
    public async Task TemporaryCopy_ExternalDelayedReadKeepsNestedChartAndRestoresOriginalName()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            string source = Path.Combine(root, "source");
            string destination = Path.Combine(root, "destination");
            Directory.CreateDirectory(Path.Combine(source, "charts/extra"));
            Directory.CreateDirectory(Path.Combine(destination, "charts"));
            string chart = Path.Combine(source, "charts/chart.bms");
            string resource = Path.Combine(source, "charts/extra/kick.wav");
            File.WriteAllText(chart, "#WAV01 extra/kick.wav");
            File.WriteAllText(resource, "kick");
            File.WriteAllText(Path.Combine(destination, "charts/chart.bms"), "existing chart");
            string songDb = Path.Combine(root, "song.db");
            File.WriteAllBytes(songDb, []);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
            var operations = new ChartFileOperationSynchronizer();
            PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(testSettings, chart, destination, songDb, player,
                new FakePlaybackDialogService(), out _, operations, packageRoot: source, uiScheduler: new TestUiScheduler(() => null),
                // 外部開始後の保持中に読取りと受付の所有を観測するため、製品既定の保持時間を使います。
                temporaryPreviewHold: PlaybackPanelViewModel.DefaultTemporaryPreviewHold);
            Task reader = Task.CompletedTask;
            Task start = Task.CompletedTask;
            string copied = Path.Combine(destination, "charts", "chart_.bms");
            player.BeforePlayStart = () =>
            {
                reader = Task.Run(async () =>
                {
                    await releaseRead.Task;
                    Assert.IsTrue(operations.IsActive, "外部開始後の保持時間もLを所有します。");
                    Assert.AreEqual("#WAV01 extra/kick.wav", File.ReadAllText(copied));
                    Assert.AreEqual("kick", File.ReadAllText(Path.Combine(destination, "charts/extra/kick.wav")));
                    readObserved.TrySetResult();
                });
                started.TrySetResult(copied);
            };
            try
            {
                start = Task.Run(() => panel.StartAtIndex(0));
                await TestUiDispatcherHost.AwaitNotificationAsync(started.Task, start, "temporary-external-start");
                releaseRead.TrySetResult();
                await TestUiDispatcherHost.AwaitNotificationAsync(readObserved.Task, reader, "temporary-external-relative-read");
                await start;
                Assert.IsFalse(completion.Task.IsCompleted, "外部playerの返却Task終端までコピーや選曲を保持しません。");
                Assert.IsFalse(File.Exists(copied));
                Assert.IsFalse(Directory.Exists(Path.Combine(destination, "charts/extra")));
                Assert.AreEqual("existing chart", File.ReadAllText(Path.Combine(destination, "charts/chart.bms")));
                Assert.AreEqual("#WAV01 extra/kick.wav", File.ReadAllText(chart));
                Assert.AreEqual("kick", File.ReadAllText(resource));
                Assert.IsFalse(operations.IsActive, "回収と元名復元の後にLを解放します。");
                Assert.IsFalse(File.Exists(Path.Combine(source, "charts/chart_.bms")));
                Assert.IsTrue(player.Commands.Contains("PlayStart:" + copied));
            }
            finally
            {
                releaseRead.TrySetResult();
                completion.TrySetResult();
                try { await Task.WhenAll(start, reader); }
                finally
                {
                    try { await panel.StopPlayback(closeProcess: true); }
                    finally { DisposeWorkspace(); }
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>停止・早送り・効果表示・プレイ側・速度の各commandが再生ownerを通して対象playerへ届くことを、実際の呼出し通知で確認します。</summary>
    [TestMethod]
    public async Task PlaybackCommands_ExecuteThroughPlaybackOwner()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);

        Task closeObserved = player.WaitForCommandAsync("Close");
        panel.StopCommand.Execute();
        await closeObserved;

        Task fastForwardStartObserved = player.WaitForCommandAsync("FastForwardStart");
        panel.FastForwardStartCommand.Execute();
        await fastForwardStartObserved;

        Task fastForwardEndObserved = player.WaitForCommandAsync("FastForwardEnd");
        panel.FastForwardEndCommand.Execute();
        await fastForwardEndObserved;

        Task showEffectObserved = player.WaitForCommandAsync("ShowEffect");
        panel.ShowEffectCommand.Execute();
        await showEffectObserved;

        Task changePlaysideObserved = player.WaitForCommandAsync("ChangePlayside");
        panel.ChangePlaysideCommand.Execute();
        await changePlaysideObserved;

        Task increaseHighSpeedObserved = player.WaitForCommandAsync("IncreaseHighSpeed");
        panel.IncreaseHighSpeedCommand.Execute();
        await increaseHighSpeedObserved;

        Task decreaseHighSpeedObserved = player.WaitForCommandAsync("DecreaseHighSpeed");
        panel.DecreaseHighSpeedCommand.Execute();
        await decreaseHighSpeedObserved;
    }

    /// <summary>指定したplayer統計とその変更を表示へ反映し、seek・pause・restart・方向操作・音量変更が同じplayerへ正しく渡ることを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_TracksTelemetryAndDelegatesPlayerCommands()
    {
        var player = new FakeBmsPlayer
        {
            Duration = TimeSpan.FromSeconds(120),
            StopTime = TimeSpan.FromSeconds(115),
            BmsDuration = TimeSpan.FromSeconds(100),
            MusicDuration = TimeSpan.FromSeconds(110),
            CurrentVoices = 2,
            MaxVoices = 8,
            NoteDensity = 3,
            NoteDensityMax = 9,
            Bpm = 180,
            MinBpm = 120,
            MaxBpm = 240,
            Total = 75.5,
            Combo = 12,
            Notes = 345,
            Measure = 16,
            LastMeasure = 64
        };
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);

        Assert.AreEqual(player.Duration, panel.CurrentlyPlayingDuration);
        Assert.AreEqual(player.StopTime, panel.CurrentlyPlayingStopTime);
        Assert.AreEqual(player.BmsDuration, panel.CurrentlyPlayingBmsDuration);
        Assert.AreEqual(player.MusicDuration, panel.CurrentlyPlayingMusicDuration);
        Assert.AreEqual(player.CurrentVoices, panel.CurrentlyPlayingCurrentVoices);
        Assert.AreEqual(player.MaxVoices, panel.CurrentlyPlayingMaxVoices);
        Assert.AreEqual(player.NoteDensity, panel.CurrentlyPlayingNoteDensity);
        Assert.AreEqual(player.NoteDensityMax, panel.CurrentlyPlayingNoteDensityMax);
        Assert.AreEqual(player.Bpm, panel.CurrentlyPlayingBpm);
        Assert.AreEqual(player.MinBpm, panel.CurrentlyPlayingMinBpm);
        Assert.AreEqual(player.MaxBpm, panel.CurrentlyPlayingMaxBpm);
        Assert.AreEqual(player.Total, panel.CurrentlyPlayingTotal);
        Assert.AreEqual(player.Combo, panel.CurrentlyPlayingCombo);
        Assert.AreEqual(player.Notes, panel.CurrentlyPlayingNotes);
        Assert.AreEqual(player.Measure, panel.CurrentlyPlayingMeasure);
        Assert.AreEqual(player.LastMeasure, panel.CurrentlyPlayingLastMeasure);

        player.Duration = TimeSpan.FromSeconds(200);
        player.CurrentTime = TimeSpan.FromSeconds(5);
        player.Raise(nameof(IBMSPlayer.Duration));
        player.Raise(nameof(IBMSPlayer.CurrentTime));
        Assert.AreEqual(player.Duration, panel.CurrentlyPlayingDuration);
        Assert.AreEqual(player.CurrentTime, panel.CurrentlyPlayingTime);

        panel.CurrentlyPlayingTime = TimeSpan.FromSeconds(15);
        Assert.AreEqual(TimeSpan.FromSeconds(15), player.CurrentTime);

        long playbackGeneration = panel.BeginPlayback(ChartTestValues.Empty(), 0);
        Assert.IsTrue(panel.TryPlayStart(playbackGeneration, "chart.bms", null).GetAwaiter().GetResult());
        panel.TogglePause();
        panel.RestartPlayingBmsFile();
        panel.FastForwardStart();
        panel.FastForwardEnd();
        panel.FastBackwardStart();
        panel.FastBackwardEnd();
        panel.ShowInfo();
        panel.ShowEffect();
        panel.ChangePlayside();
        panel.IncreaseHighSpeed();
        panel.DecreaseHighSpeed();
        int originalVolume = testSettings.uBMplayVolume;
        try
        {
            panel.PlayerVolume = originalVolume == 100 ? 99 : originalVolume + 1;
            Assert.IsTrue(player.Commands.Contains("VolumeChanged"));
        }
        finally
        {
            testSettings.uBMplayVolume = originalVolume;
        }

        CollectionAssert.AreEqual(
            new[]
            {
                "PlayStart:chart.bms",
                "Pause",
                "Restart",
                "FastForwardStart",
                "FastForwardEnd",
                "FastBackwardStart",
                "FastBackwardEnd",
                "ShowInfo",
                "ShowEffect",
                "ChangePlayside",
                "IncreaseHighSpeed",
                "DecreaseHighSpeed",
                "VolumeChanged"
            },
            player.Commands.ToArray());
    }

    /// <summary>UI投稿が未実行の間は統計を更新せず、停止で失効した開始通知を捨てながら最新統計だけ反映することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_DispatchesTelemetryAndSuppressesStalePlaybackEventsThroughUiBoundary()
    {
        var player = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var panel = new PlaybackPanelViewModel(
            player,
            dispatcher,
            new MainChartListPlaybackQueue(new MainChartListViewModel()),
            new SettingsPlaybackSettingsStore(() => testSettings),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        int startingCount = 0;
        int startedCount = 0;
        panel.PlaybackStarting += (_, _) => startingCount++;
        panel.PlaybackStarted += (_, _) => startedCount++;

        player.Duration = TimeSpan.FromSeconds(20);
        player.Raise(nameof(IBMSPlayer.Duration));
        long generation = panel.BeginPlayback(ChartTestValues.Empty(), 0);
        panel.StopPlayback().GetAwaiter().GetResult();

        Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
        Assert.AreEqual(0, startingCount);
        Assert.AreEqual(0, startedCount);
        Assert.AreEqual(2, dispatcher.PendingCount);

        dispatcher.RunAll();

        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);
        Assert.AreEqual(0, startingCount);
        Assert.AreEqual(0, startedCount);
        panel.NotifyPlaybackStarted(generation);
        dispatcher.RunAll();
        Assert.AreEqual(0, startedCount);
    }

    /// <summary>Close後の統計再読込みをUI受信まで待ち、旧playerの未受信更新が交換済みplayerの表示値を上書きしないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_CloseRefreshUsesUiBoundaryAndIgnoresReplacedPlayer()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var dispatcher = new QueuedPlaybackUiDispatcher();
        var panel = new PlaybackPanelViewModel(
            first,
            dispatcher,
            new MainChartListPlaybackQueue(new MainChartListViewModel()),
            new SettingsPlaybackSettingsStore(() => testSettings),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);

        first.Duration = TimeSpan.FromSeconds(20);
        panel.CloseProcess().GetAwaiter().GetResult();
        Assert.AreEqual(TimeSpan.FromSeconds(10), panel.CurrentlyPlayingDuration);
        dispatcher.RunAll();
        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);

        first.Duration = TimeSpan.FromSeconds(30);
        panel.CloseProcess().GetAwaiter().GetResult();
        var replacement = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(40) };
        Task pendingActions = dispatcher.WaitForPendingActionsAsync(2);
        Task replacementTask = panel.ReplacePlayerAsync(replacement);
        await Task.WhenAny(pendingActions, replacementTask);
        if (replacementTask.IsCompleted)
        {
            await replacementTask;
        }
        await pendingActions;
        dispatcher.RunAll();
        await replacementTask;

        Assert.AreEqual(TimeSpan.FromSeconds(40), panel.CurrentlyPlayingDuration);
    }

    /// <summary>player Close中にも別Taskからsession開始を完了でき、外部呼出しをsession guardの内側で待つdeadlockを起こさないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_StopDoesNotHoldSessionGuardAcrossPlayerClose()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        ChartFile replacementSession = ChartTestValues.Empty();
        Task? sessionProbe = null;
        player.BeforeClose = () =>
        {
            sessionProbe = ConcurrencyProbe.StartDedicated(() => panel.BeginPlayback(replacementSession, 7));
            ConcurrencyProbe.AssertCompletesWhileHeld(sessionProbe, "player Close中のsession開始");
        };

        panel.BeginPlayback(ChartTestValues.Empty(), 3);
        panel.StopPlayback(closeProcess: true).GetAwaiter().GetResult();

        Assert.IsNotNull(sessionProbe);
        sessionProbe.GetAwaiter().GetResult();
        Assert.AreSame(replacementSession, panel.NowPlayingChart);
    }

    /// <summary>進行中のplayer開始を停止が追い越さず、開始呼出し終端後にCloseして現在曲を解除する順序を確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_StopSerializesWithInFlightPlayerStartAndClosesItAfterward()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        var startEntered = new ManualResetEventSlim();
        var releaseStart = new ManualResetEventSlim();
        player.BeforePlayStart = () =>
        {
            startEntered.Set();
            releaseStart.Wait();
        };
        long generation = panel.BeginPlayback(ChartTestValues.Empty(), 0);

        Task<bool> start = Task.Run(
            () => panel.TryPlayStart(generation, "race.bms", null));
        startEntered.Wait();
        var stop = Task.Run(() => panel.StopPlayback(closeProcess: true));
        Assert.IsFalse(stop.Wait(TimeSpan.FromMilliseconds(100)));

        releaseStart.Set();
        stop.Wait();
        start.Wait();
        CollectionAssert.AreEqual(
            new[] { "PlayStart:race.bms", "Close" },
            player.Commands.ToArray());
        Assert.IsNull(panel.NowPlayingChart);
    }

    /// <summary>player交換で旧playerを閉じ購読を解除し、既存window hostを新playerへ接続して旧統計通知を無視することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_ReplacementClosesOldPlayerDetachesEventsAndKeepsWindowHost()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var second = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(20) };
        PlaybackPanelViewModel panel = CreatePanel(testSettings, first);
        IExternalPlayerWindowHost host = new Win32ExternalPlayerWindowHost(new IntPtr(42));
        panel.AttachWindowHost(host);

        panel.ReplacePlayerAsync(second).GetAwaiter().GetResult();

        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreSame(host, second.WindowHost);
        Assert.AreEqual(second.Duration, panel.CurrentlyPlayingDuration);

        first.Duration = TimeSpan.FromSeconds(99);
        first.Raise(nameof(IBMSPlayer.Duration));
        Assert.AreEqual(second.Duration, panel.CurrentlyPlayingDuration);

        panel.CloseProcess().GetAwaiter().GetResult();
        Assert.AreEqual(1, second.CloseProcessCount);
    }

    /// <summary>設定適用portの交換が旧playerと現在曲を回収しhostを引き継ぎ、機器テストportの停止は交換後playerを閉じることを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_SettingsRuntimePortSeparatesReplacementAndAudioTestStop()
    {
        var first = new FakeBmsPlayer();
        var replacement = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, first);
        IExternalPlayerWindowHost host = new Win32ExternalPlayerWindowHost(new IntPtr(42));
        panel.AttachWindowHost(host);
        ChartFile playingFile = ChartTestValues.Empty();
        panel.BeginPlayback(playingFile, 0);

        var settingsRuntime = (ISettingsDialogPlaybackRuntimePort)panel;
        panel.ReplacePlayerAsync(replacement, clearPlaybackStatus: true).GetAwaiter().GetResult();

        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreSame(host, replacement.WindowHost);
        Assert.IsNull(panel.NowPlayingChart);

        ((IAudioDeviceTestPlaybackPort)panel).StopPlayback().GetAwaiter().GetResult();

        Assert.AreEqual(1, replacement.CloseProcessCount);
        settingsRuntime.NotifySettingsChanged();
    }

    /// <summary>設定適用の呼出しは旧Closeが保留でもTaskを返し、解放後の実終端で交換を確定して呼出しlaneを塞がないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_SettingsReplacementReturnsTaskWhilePreviousPlayerClosesOffCallerLane()
    {
        using var closeEntered = new ManualResetEventSlim();
        using var releaseClose = new ManualResetEventSlim();
        var first = new FakeBmsPlayer
        {
            BeforeClose = () =>
            {
                closeEntered.Set();
                releaseClose.Wait();
            }
        };
        var replacement = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, first);

        Task replacementTask =
            panel.ReplacePlayerAsync(replacement, clearPlaybackStatus: true);

        closeEntered.Wait();
        Assert.IsFalse(replacementTask.IsCompleted);
        releaseClose.Set();
        replacementTask.Wait();
        Assert.AreEqual(1, first.CloseProcessCount);
        Assert.AreEqual(replacement.Duration, panel.CurrentlyPlayingDuration);
    }

    /// <summary>交換候補の統計準備が失敗した場合、元player・session・通知購読を維持しどちらのplayerも閉じないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_SettingsRuntimeApplyFailurePreservesPreviousPlayerAndSession()
    {
        var first = new FakeBmsPlayer { Duration = TimeSpan.FromSeconds(10) };
        var replacement = new FakeBmsPlayer { ThrowOnTelemetryRead = true };
        PlaybackPanelViewModel panel = CreatePanel(testSettings, first);
        ChartFile playingFile = ChartTestValues.Empty();
        panel.BeginPlayback(playingFile, 0);

        try
        {
            panel
                .ReplacePlayerAsync(replacement, clearPlaybackStatus: true)
                .GetAwaiter()
                .GetResult();
            Assert.Fail("Expected replacement preparation to fail.");
        }
        catch (InvalidOperationException)
        {
        }

        Assert.AreSame(playingFile, panel.NowPlayingChart);
        Assert.AreEqual(0, first.CloseProcessCount);
        Assert.AreEqual(0, replacement.CloseProcessCount);
        first.Duration = TimeSpan.FromSeconds(20);
        first.Raise(nameof(IBMSPlayer.Duration));
        Assert.AreEqual(TimeSpan.FromSeconds(20), panel.CurrentlyPlayingDuration);
    }

    /// <summary>現在曲のsessionだけが開始・終了を通知し、旧世代および停止後の終了callbackでは現在状態を変更しないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_OwnsSessionStateAndIgnoresStaleExitCallbacks()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        ChartFile first = ChartTestValues.Empty();
        ChartFile second = ChartTestValues.Empty();
        int exitCount = 0;
        int startingCount = 0;
        int startedCount = 0;
        panel.PlaybackStarting += (_, _) => startingCount++;
        panel.PlaybackStarted += (_, _) => startedCount++;

        long firstGeneration = panel.BeginPlayback(first, 3);
        Assert.IsTrue(panel.TryPlayStart(firstGeneration, "first.bms", (_, _) => exitCount++).GetAwaiter().GetResult());
        Action<object, EventArgs> firstExit = player.ExitHandler!;
        panel.NotifyPlaybackStarted(firstGeneration);

        Assert.AreSame(first, panel.NowPlayingChart);
        Assert.IsTrue(panel.IsPlaying);
        Assert.AreEqual(1, startingCount);
        Assert.AreEqual(1, startedCount);

        long secondGeneration = panel.BeginPlayback(second, 4);
        Assert.IsTrue(panel.TryPlayStart(secondGeneration, "second.bms", (_, _) => exitCount++).GetAwaiter().GetResult());
        Action<object, EventArgs> secondExit = player.ExitHandler!;
        firstExit(player, EventArgs.Empty);
        Assert.AreEqual(0, exitCount);

        secondExit(player, EventArgs.Empty);
        Assert.AreEqual(1, exitCount);

        panel.StopPlayback().GetAwaiter().GetResult();
        secondExit(player, EventArgs.Empty);
        Assert.AreEqual(1, exitCount);
        Assert.IsNull(panel.NowPlayingChart);
        Assert.IsTrue(panel.IsStoppedOrPaused);
        Assert.AreEqual(0, player.CloseProcessCount);
    }

    /// <summary>同じ曲への開始操作でpauseと再開を切り替え、再生中・停止中のbindingへ状態通知を揃えて公開することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_TogglePausePublishesPlayingAndPausedStates()
    {
        var player = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        ChartFile file = ChartTestValues.Empty();
        var changed = new List<string>();
        panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
        long generation = panel.BeginPlayback(file, 0);
        Assert.IsTrue(panel.TryPlayStart(generation, "chart.bms", null).GetAwaiter().GetResult());

        Assert.IsTrue(panel.IsPlaying);
        Assert.IsFalse(panel.IsPaused);
        Assert.IsFalse(panel.IsStoppedOrPaused);
        changed.Clear();

        panel.Start(forceNewPlay: false).GetAwaiter().GetResult();

        Assert.IsFalse(panel.IsPlaying);
        Assert.IsTrue(panel.IsPaused);
        Assert.IsTrue(panel.IsStoppedOrPaused);
        CollectionAssert.IsSubsetOf(
            new[] { nameof(panel.IsPlaying), nameof(panel.IsPaused), nameof(panel.IsStoppedOrPaused) },
            changed);
        changed.Clear();

        panel.Start(forceNewPlay: false).GetAwaiter().GetResult();

        Assert.IsTrue(panel.IsPlaying);
        Assert.IsFalse(panel.IsPaused);
        Assert.IsFalse(panel.IsStoppedOrPaused);
        Assert.AreEqual(2, player.Commands.Count(command => command == "Pause"));
        CollectionAssert.IsSubsetOf(
            new[] { nameof(panel.IsPlaying), nameof(panel.IsPaused), nameof(panel.IsStoppedOrPaused) },
            changed);
    }

    /// <summary>BMS・bmsonの共通キューで種類と大小文字を無視したpathに再生bitを投影し、元のスコア・検索bitを保全しながら終了通知で一回だけ送ることを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanelChartQueueKeepsIdentityStatusAndSingleAdvance()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { SupportsBmson = true, Ready = ready.Task };
        var chartList = new MainChartListViewModel { SelectedIndex = 0 };
        var settings = new InMemoryPlaybackSettingsStore();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(chartList),
            settings, new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        chartList.RowProjection.SetPlaybackStatusProvider(panel.GetPlaybackStatus);
        ChartFile first = (ChartTestValues.Empty() with { Path = paths[0] });
        first = ChartFileProjection.WithScore(first, ChartScoreSnapshot.FromBmsScore(new BMSScore { IsLr2IrScoreUnsent = true }, first.Path));
        var entry = PackageChartEntry.FromChart((first));
        entry.SetSearchingStatus(isSearching: true);
        var firstRow = LibraryChartRow.FromChartFile(first);
        chartList.RowProjection.ConfigureLibraryRow(null, firstRow);
        LibraryChartRow searchingRow = chartList.RowProjection.CreatePackageRow(null, entry);
        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = paths[1], RawTitle = "Bmson", Subtitle = "[Another]", RawArtist = "Author" };
        LibraryChartRow bmsonRow = chartList.RowProjection.CreateLibraryRow(null, (bmson));
        ChartFile last = (ChartTestValues.Empty() with { Path = paths[2] });
        chartList.Rows = new List<object> { firstRow, bmsonRow, last };
        Task start = panel.StartAtIndex(0);
        try
        {
            await player.StartObserved.Task;
            Assert.AreEqual(ChartFileStatus.LOADING, firstRow.status & ChartFileStatus.PLAYALL);
            Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, first.Status);
            Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, firstRow.Chart.Status);
            Assert.AreEqual(ChartFileStatus.LOADING | ChartFileStatus.SEARCHING | ChartFileStatus.SCORE_UNSENT, searchingRow.status);
            ready.SetResult();
            await start;
            Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.SCORE_UNSENT, firstRow.status);
            Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.SEARCHING | ChartFileStatus.SCORE_UNSENT, searchingRow.status);
            Assert.AreEqual(ChartFileStatus.NONE, bmsonRow.status & ChartFileStatus.PLAYALL, "先読み候補に状態を付けません。");
            LibraryChartRow alias = chartList.RowProjection.CreateLibraryRow(null,
                ((ChartTestValues.Empty() with { Path = paths[0].ToUpperInvariant() })));
            Assert.AreEqual(ChartFileStatus.PLAY, alias.status & ChartFileStatus.PLAYALL);
            Assert.AreEqual(ChartFileStatus.NONE, panel.GetPlaybackStatus((ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = paths[0] })));
            Assert.AreEqual(ChartFileStatus.NONE, panel.GetPlaybackStatus((last)));
            panel.TogglePause();
            Assert.AreEqual(ChartFileStatus.PAUSE, firstRow.status & ChartFileStatus.PLAYALL);
            Assert.AreEqual(ChartFileStatus.PAUSE, alias.status & ChartFileStatus.PLAYALL);
            panel.TogglePause();
            panel.FastForwardStart();
            Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.FORWARD, firstRow.status & ChartFileStatus.PLAYALL);
            panel.FastForwardEnd();
            panel.FastBackwardStart();
            Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.BACKWARD, firstRow.status & ChartFileStatus.PLAYALL);
            panel.FastBackwardEnd();
            Assert.AreEqual(ChartFileStatus.PLAY, firstRow.status & ChartFileStatus.PLAYALL);
            Action<object, EventArgs> firstExit = player.ExitHandler ?? throw new AssertFailedException("終了通知がありません。");
            var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler nextStartedHandler = (_, _) =>
            {
                if (panel.NowPlayingChart?.Path == paths[1])
                {
                    nextStarted.TrySetResult();
                }
            };
            panel.PlaybackStarted += nextStartedHandler;
            try
            {
                firstExit(player, EventArgs.Empty);
                await nextStarted.Task;
            }
            finally { panel.PlaybackStarted -= nextStartedHandler; }
            firstExit(player, EventArgs.Empty);
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(ChartFileKind.Bmson, panel.NowPlayingChart.Kind);
            Assert.AreEqual("Bmson", panel.PlayerHeaderTitle);
            Assert.AreEqual("[Another]", panel.PlayerHeaderSubtitle);
            Assert.AreEqual(ChartFileStatus.PLAY, bmsonRow.status & ChartFileStatus.PLAYALL);
            Assert.AreEqual(ChartFileStatus.NONE, alias.status & ChartFileStatus.PLAYALL);
            Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, firstRow.status);
            Assert.AreEqual(ChartFileStatus.SEARCHING | ChartFileStatus.SCORE_UNSENT, searchingRow.status);
            panel.TogglePause();
            Assert.AreEqual(ChartFileStatus.PAUSE, bmsonRow.status & ChartFileStatus.PLAYALL);
            panel.TogglePause();
            await panel.Next();
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(paths[2], panel.NowPlayingChart.Path);
            Assert.AreEqual(ChartFileStatus.NONE, bmsonRow.status & ChartFileStatus.PLAYALL);
            CollectionAssert.AreEqual(paths, player.Starts.ToArray());
            await panel.StopPlayback(closeProcess: true);
            Assert.AreEqual(ChartFileStatus.NONE, panel.GetPlaybackStatus((last)));
            Assert.AreEqual(ChartFileStatus.SCORE_UNSENT, first.Status);
        }
        finally
        {
            ready.TrySetResult();
            await start;
            panel.BeginShutdown();
            await panel.CloseForShutdown();
            foreach (string path in paths)
            {
                File.Delete(path);
            }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }
    /// <summary>現在playerまたはpending要求がbmson非対応なら、明示行を副作用前に拒否して現在曲・選択・header・状態を保ちます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaybackPanelDoesNotSendBmsonToAnExternalPlayerWithoutThatCapability(bool pendingExternal)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer { SupportsBmson = pendingExternal, Completion = completion.Task };
        ChartFile current = ChartTestValues.Empty() with { Path = paths[0], RawTitle = "現在曲" };
        var row = LibraryChartRow.FromChartFile(ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = paths[1], RawTitle = "拒否対象" });
        var list = new MainChartListViewModel { Rows = new List<object> { current, row }, SelectedIndex = 0 };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(list),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("再生失敗"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        int created = 0;
        try
        {
            await panel.StartAtIndex(0);
            if (pendingExternal)
            {
                ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return new FakeBmsPlayer(); }, supportsBmson: false);
            }
            panel.TogglePause();
            string[] commands = player.Commands.ToArray();
            int surfaceRequests = 0;
            await panel.HandleTableRowActivation(1, row, () => surfaceRequests++);
            Assert.AreEqual(0, surfaceRequests);
            await panel.ExecuteTableRowActivation(1, row);
            Assert.AreSame(current, panel.NowPlayingChart);
            Assert.AreEqual(0, list.SelectedIndex);
            Assert.AreEqual("現在曲", panel.PlayerHeaderTitle);
            Assert.IsTrue(panel.IsPaused, "面要求の対象となる一時停止入力も、形式拒否で状態を変えません。");
            Assert.AreEqual(0, created);
            CollectionAssert.AreEqual(commands, player.Commands.ToArray());
        }
        finally
        {
            completion.TrySetResult();
            panel.BeginShutdown();
            await panel.CloseForShutdown();
            foreach (string path in paths) { File.Delete(path); }
        }
    }

    /// <summary>現在曲の終了で次曲と一覧選択を一回進め、旧終了callbackの再送ではさらに送りを行わないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_PlayerExitAdvancesExactlyOnceToTheNextChart()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string firstPath = workspace.GetPath("firstPath.tmp");
        File.WriteAllBytes(firstPath, []);
        string secondPath = workspace.GetPath("secondPath.tmp");
        File.WriteAllBytes(secondPath, []);
        bool originalRepeat = testSettings.RepeatPlayMode;
        bool originalFolderSkip = testSettings.FolderSkipPlayMode;
        bool originalSingle = testSettings.SinglePlayMode;
        var player = new FakeBmsPlayer();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = firstPath }), (ChartTestValues.Empty() with { Path = secondPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            testSettings.RepeatPlayMode = false;
            testSettings.FolderSkipPlayMode = false;
            testSettings.SinglePlayMode = false;
            panel.Start().GetAwaiter().GetResult();
            Action<object, EventArgs> firstExit = player.ExitHandler!;

            firstExit(player, EventArgs.Empty);

            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(secondPath, panel.NowPlayingChart.Path);
            Assert.AreEqual(1, chartList.SelectedIndex);
            Assert.IsFalse(chartList.Rows.Cast<ChartFile>().ElementAt(0).Status.HasFlag(ChartFileStatus.PLAYALL));
            Assert.IsTrue(panel.IsPlaying);
            CollectionAssert.AreEqual(
                new[] { "PlayStart:" + firstPath, "PlayStart:" + secondPath },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());

            firstExit(player, EventArgs.Empty);
            Assert.AreEqual(2, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
        }
        finally
        {
            testSettings.RepeatPlayMode = originalRepeat;
            testSettings.FolderSkipPlayMode = originalFolderSkip;
            testSettings.SinglePlayMode = originalSingle;
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    /// <summary>停止・交換で準備済み開始を拒否し、Close後は実行済みsessionの終了callbackも失効することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_StopReplaceAndCloseInvalidatePreparedOrRunningSessions()
    {
        var firstPlayer = new FakeBmsPlayer();
        var replacementPlayer = new FakeBmsPlayer();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, firstPlayer);

        long stoppedGeneration = panel.BeginPlayback(ChartTestValues.Empty(), 1);
        panel.StopPlayback().GetAwaiter().GetResult();
        Assert.IsFalse(panel.TryPlayStart(stoppedGeneration, "stopped.bms", null).GetAwaiter().GetResult());
        Assert.IsFalse(firstPlayer.Commands.Any(command => command == "PlayStart:stopped.bms"));

        long replacedGeneration = panel.BeginPlayback(ChartTestValues.Empty(), 2);
        panel.ReplacePlayerAsync(replacementPlayer).GetAwaiter().GetResult();
        Assert.IsFalse(panel.TryPlayStart(replacedGeneration, "replaced.bms", null).GetAwaiter().GetResult());
        Assert.IsFalse(replacementPlayer.Commands.Any(command => command == "PlayStart:replaced.bms"));

        long runningGeneration = panel.BeginPlayback(ChartTestValues.Empty(), 3);
        int exitCount = 0;
        Assert.IsTrue(panel.TryPlayStart(runningGeneration, "running.bms", (_, _) => exitCount++).GetAwaiter().GetResult());
        Action<object, EventArgs> exit = replacementPlayer.ExitHandler!;
        panel.CloseProcess().GetAwaiter().GetResult();
        exit(replacementPlayer, EventArgs.Empty);

        Assert.AreEqual(0, exitCount);
        Assert.AreEqual(1, replacementPlayer.CloseProcessCount);
    }

    /// <summary>正常曲の再生中でも、明示Startのリピート対象一万件がすべて利用不能なら有限探索で停止し、現在曲を解除します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_StopsAfterAllRepeatCandidatesAreUnavailable()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 1);
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { ChartTestValues.Empty() with { Path = paths[0] } },
            SelectedIndex = 0
        };
        var player = new FakeBmsPlayer();
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new InMemoryPlaybackSettingsStore { RepeatPlay = true },
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.Start();
            Assert.IsTrue(panel.IsPlaying);
            chartList.Rows = Enumerable.Range(0, 10000).Select(_ => (object)ChartTestValues.Empty()).ToList();
            chartList.SelectedIndex = 0;

            await panel.Start();

            Assert.IsNull(panel.NowPlayingChart);
            Assert.IsFalse(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
            CollectionAssert.AreEqual(new[] { "PlayStart:" + paths[0] }, player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
        }
        finally
        {
            try { await panel.CloseForShutdown(); }
            finally { File.Delete(paths[0]); }
        }
    }

    /// <summary>入力不良が4096件続いても再帰的送りで落ちず、各対象を一回試行・通知して停止することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_StopsAfterManyInvalidChartsWithoutRecursiveAdvance()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        bool originalRepeat = testSettings.RepeatPlayMode;
        bool originalFolderSkip = testSettings.FolderSkipPlayMode;
        var player = new FakeBmsPlayer { PlayStartException = new InvalidDataException("invalid chart") };
        var chartList = new MainChartListViewModel
        {
            Rows = Enumerable.Range(0, 4096).Select(_ => (object)(ChartTestValues.Empty() with { Path = chartPath })).ToList(),
            SelectedIndex = 0
        };
        int warningCount = 0;
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            new FakePlaybackDialogService(),
            _ => warningCount++,
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            testSettings.RepeatPlayMode = false;
            testSettings.FolderSkipPlayMode = false;

            panel.Start().GetAwaiter().GetResult();

            Assert.IsNull(panel.NowPlayingChart);
            Assert.AreEqual(4096, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(4096, warningCount);
        }
        finally
        {
            testSettings.RepeatPlayMode = originalRepeat;
            testSettings.FolderSkipPlayMode = originalFolderSkip;
            File.Delete(chartPath);
        }
    }

    /// <summary>開始呼出しの同期失敗では先に現在曲を解除してから元例外を一回通知することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_NotifiesAndStopsAfterPlayerStartFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var failure = new InvalidOperationException("player start failed");
        var player = new FakeBmsPlayer { PlayStartException = failure };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        dialogs.BeforePlaybackFailureNotification = () =>
        {
            Assert.IsNull(panel.NowPlayingChart);
        };
        try
        {
            panel.Start().GetAwaiter().GetResult();

            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingChart);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>外部開始の返却Taskが遅れて失敗した場合、元例外を通知して表示状態だけ解除し追加Closeを行わないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_ObservesAsynchronousPlayerStartFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var failure = new IOException("async player start failed");
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.Start().GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);

            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(failure);

            await failureNotification;
            Assert.AreSame(failure, dialogs.LastPlaybackFailure);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.AreEqual(0, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>非同期開始の成功前に届いた終了callbackを保留し、成功確定後に一回引き渡すことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_DefersExitUntilAsynchronousStartSucceeds()
    {
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        ChartFile file = ChartTestValues.Empty();
        int exitCount = 0;
        long generation = panel.BeginPlayback(file, 0);
        Task<bool> playStartTask = panel.TryPlayStart(generation, "chart.bms", (_, _) => exitCount++);

        Action<object, EventArgs> exitHandler = player.ExitHandler!;
        exitHandler(player, EventArgs.Empty);

        Assert.AreEqual(0, exitCount);
        Assert.AreSame(file, panel.NowPlayingChart);

        completion.SetResult(new object());

        Assert.IsTrue(await playStartTask);
        Assert.AreEqual(1, exitCount);
        Assert.AreSame(file, panel.NowPlayingChart);
    }

    /// <summary>非同期開始が失敗した場合、表示を停止へ戻しそのsessionの終了callbackを引き渡さないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_DropsExitAfterAsynchronousStartFailure()
    {
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var failure = new IOException("async player start failed before exit");
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player);
        ChartFile file = ChartTestValues.Empty();
        int exitCount = 0;
        long generation = panel.BeginPlayback(file, 0);
        Task<bool> playStartTask = panel.TryPlayStart(generation, "chart.bms", (_, _) => exitCount++);

        completion.SetException(failure);

        await Assert.ThrowsExactlyAsync<IOException>(async () => await playStartTask);
        Assert.IsNull(panel.NowPlayingChart);
        player.ExitHandler!(player, EventArgs.Empty);

        Assert.AreEqual(0, exitCount);
        Assert.IsNull(panel.NowPlayingChart);
    }

    /// <summary>commandから開始した未完了Taskが取消された場合、現在曲解除の通知まで到達し故障ダイアログを出さないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_StopsCurrentPlaybackWhenAsyncPlayerStartIsCanceled()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            var stopped = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler stoppedHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(PlaybackPanelViewModel.NowPlayingChart)
                    && panel.NowPlayingChart == null)
                {
                    stopped.TrySetResult(null);
                }
            };
            panel.PropertyChanged += stoppedHandler;
            try
            {
                panel.StartCommand.Execute();
                completion.SetCanceled();

                await stopped.Task;
            }
            finally
            {
                panel.PropertyChanged -= stoppedHandler;
            }
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingChart);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>開始時点ですでに取消済みのTaskも停止として扱い、現在曲を残したり故障通知を出したりしないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_TreatsAlreadyCanceledPlayerStartAsStoppedWithoutFailureDialog()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var player = new FakeBmsPlayer
        {
            PlayStartTask = Task.FromCanceled<object>(new CancellationToken(canceled: true))
        };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.Start().GetAwaiter().GetResult();

            Assert.IsNull(panel.NowPlayingChart);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>旧開始の遅い失敗は元Taskへ返しても、新世代の曲を解除せず故障通知を追加しないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_IgnoresLateStartFailureAfterNewGenerationBegins()
    {
        var firstCompletion = new TaskCompletionSource<object>();
        var secondCompletion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = firstCompletion.Task };
        var dialogs = new FakePlaybackDialogService();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player, dialogs);
        ChartFile firstFile = ChartTestValues.Empty();
        ChartFile secondFile = ChartTestValues.Empty();

        long firstGeneration = panel.BeginPlayback(firstFile, 0);
        Task<bool> firstObservationTask = panel.TryPlayStart(firstGeneration, "first.bms", null);

        panel.StopPlayback().GetAwaiter().GetResult();
        long secondGeneration = panel.BeginPlayback(secondFile, 1);
        player.PlayStartTask = secondCompletion.Task;
        Task<bool> secondObservationTask = panel.TryPlayStart(secondGeneration, "second.bms", null);

        firstCompletion.SetException(new IOException("stale player start failed"));

        await Assert.ThrowsExactlyAsync<IOException>(
            async () => await firstObservationTask);
        Assert.AreSame(secondFile, panel.NowPlayingChart);
        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        secondCompletion.SetCanceled();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            async () => await secondObservationTask);
        Assert.IsNull(panel.NowPlayingChart);
    }

    /// <summary>停止後およびplayer交換後の遅い開始失敗が新しい表示状態を消したり通知を追加したりしないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_IgnoresLateStartFailureAfterStopAndReplacement()
    {
        var firstCompletion = new TaskCompletionSource<object>();
        var secondCompletion = new TaskCompletionSource<object>();
        var firstPlayer = new FakeBmsPlayer { PlayStartTask = firstCompletion.Task };
        var replacementPlayer = new FakeBmsPlayer { PlayStartTask = secondCompletion.Task };
        var dialogs = new FakePlaybackDialogService();
        PlaybackPanelViewModel panel = CreatePanel(testSettings, firstPlayer, dialogs);
        ChartFile firstFile = ChartTestValues.Empty();
        long firstGeneration = panel.BeginPlayback(firstFile, 0);
        Task<bool> firstStart = panel.TryPlayStart(firstGeneration, "first.bms", null);

        panel.StopPlayback().GetAwaiter().GetResult();
        firstCompletion.SetException(new IOException("stale after stop"));
        await Assert.ThrowsExactlyAsync<IOException>(async () => await firstStart);
        Assert.IsNull(panel.NowPlayingChart);
        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);

        ChartFile secondFile = ChartTestValues.Empty();
        long secondGeneration = panel.BeginPlayback(secondFile, 1);
        firstPlayer.PlayStartTask = secondCompletion.Task;
        Task<bool> secondStart = panel.TryPlayStart(secondGeneration, "second.bms", null);
        panel.ReplacePlayerAsync(replacementPlayer).GetAwaiter().GetResult();
        secondCompletion.SetException(new IOException("stale after replacement"));

        await Assert.ThrowsExactlyAsync<IOException>(async () => await secondStart);
        Assert.AreSame(secondFile, panel.NowPlayingChart);
        Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        panel.StopPlayback().GetAwaiter().GetResult();
    }

    /// <summary>非同期開始失敗を通知した後も同じplayerで明示再生を受け付け、余分なCloseや再通知なしで開始できることを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_RecoversAfterAsynchronousPlayerStartFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(new IOException("recoverable player start failed"));
            await failureNotification;

            player.PlayStartTask = Task.CompletedTask;
            panel.Start().GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(2, player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.AreEqual(0, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>直接開始の失敗通知自体が失敗しても処理を収束させ、次の再生や余分なCloseを始めないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_DirectStartContainsNotificationFailureWithoutFollowingPlayback()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var playerFailure = new InvalidOperationException("player start failed");
        var notificationFailure = new InvalidOperationException("notification failed");
        var player = new FakeBmsPlayer { PlayStartException = playerFailure };
        var dialogs = new FakePlaybackDialogService { PlaybackFailureException = notificationFailure };
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            Assert.AreSame(playerFailure, dialogs.LastPlaybackFailure);
            Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
            Assert.IsNull(panel.NowPlayingChart);
            Assert.AreEqual(0, player.CloseProcessCount);
            Assert.AreEqual(
                1,
                player.Commands.Count(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>通知先がthrowする設定で停止commandを実行し、元のplayer停止失敗が通知先へ届くことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_CommandFailureUsesPlaybackNotification()
    {
        var operationFailure = new IOException("player stop failed");
        var notificationFailure = new InvalidOperationException("notification failed");
        var player = new FakeBmsPlayer
        {
            BeforeClose = () => throw operationFailure
        };
        var dialogs = new FakePlaybackDialogService
        {
            PlaybackFailureException = notificationFailure
        };
        PlaybackPanelViewModel panel = CreatePanel(testSettings, player, dialogs);
        Task failureNotification = dialogs.WaitForPlaybackFailureAsync();

        panel.StopCommand.Execute();

        await failureNotification;

        Assert.AreSame(operationFailure, dialogs.LastPlaybackFailure);
    }

    /// <summary>通知先がthrowする設定で非同期の開始処理を失敗させ、元のplayer開始失敗が通知先へ届くことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_ContainsAsynchronousNotificationFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var playerFailure = new IOException("async player start failed");
        var notificationFailure = new InvalidOperationException("async notification failed");
        var completion = new TaskCompletionSource<object>();
        var player = new FakeBmsPlayer { PlayStartTask = completion.Task };
        var dialogs = new FakePlaybackDialogService { PlaybackFailureException = notificationFailure };
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
            completion.SetException(playerFailure);

            await failureNotification;
            Assert.AreSame(playerFailure, dialogs.LastPlaybackFailure);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>同期失敗済み開始の呼出し中にplayerを交換しても、旧失敗が交換後の曲を停止したり新playerを閉じたりしないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_DoesNotStopReplacementAfterSynchronousStartFailure()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var failure = new IOException("synchronous player start failed");
        var player = new FakeBmsPlayer { PlayStartTask = Task.FromException<object>(failure) };
        var replacement = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task replacementTask = Task.CompletedTask;
        player.BeforePlayStart = () => replacementTask = panel.ReplacePlayerAsync(replacement);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            replacementTask.GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(0, replacement.CloseProcessCount);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>同期取消済み開始の呼出し中にplayerを交換しても、新playerの表示と寿命を旧取消で破壊しないことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_DoesNotStopReplacementAfterSynchronousStartCancellation()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string chartPath = workspace.GetPath("chartPath.tmp");
        File.WriteAllBytes(chartPath, []);
        var player = new FakeBmsPlayer
        {
            PlayStartTask = Task.FromCanceled<object>(new CancellationToken(canceled: true))
        };
        var replacement = new FakeBmsPlayer();
        var dialogs = new FakePlaybackDialogService();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = chartPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            dialogs,
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task replacementTask = Task.CompletedTask;
        player.BeforePlayStart = () => replacementTask = panel.ReplacePlayerAsync(replacement);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            replacementTask.GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(0, replacement.CloseProcessCount);
            Assert.AreEqual(0, dialogs.PlaybackFailureNotificationCount);
        }
        finally
        {
            File.Delete(chartPath);
        }
    }

    /// <summary>一時配置からの外部開始で非同期失敗と取消を順に起こし、表示を解除してもplayerを閉じず失敗だけ一回通知することを確認します。</summary>
    /// <remarks>一時配置の物理コピー・回収または確認から開始への接続を通すため実ライブラリを接続し、playerの実行は局所probeで制御します。</remarks>
    [TestMethod]
    public async Task PlaybackPanel_TemporaryInstallAsyncStartFailureAndCancellationClearStateWithoutClosingPlayer()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            bool originalLr2Database = testSettings.OperationModeLR2DB;
            string root = workspace.RootDirectory;
            string sourceDirectory = Path.Combine(root, "Source");
            string installDirectory = Path.Combine(root, "Install");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string songDbPath = Path.Combine(root, "song.db");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(chartPath, "#TITLE test");
            File.WriteAllBytes(songDbPath, []);
            var firstCompletion = new TaskCompletionSource<object>();
            var secondCompletion = new TaskCompletionSource<object>();
            var player = new FakeBmsPlayer { PlayStartTask = firstCompletion.Task };
            var dialogs = new FakePlaybackDialogService();
            try
            {
                testSettings.OperationModeLR2DB = true;
                PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(testSettings,
                    chartPath,
                    installDirectory,
                    songDbPath,
                    player,
                    dialogs, uiScheduler: new TestUiScheduler(() => null));

                panel.Start().GetAwaiter().GetResult();
                Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
                firstCompletion.SetException(new IOException("temporary install start failed"));
                await failureNotification;

                player.PlayStartTask = secondCompletion.Task;
                var stopped = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                PropertyChangedEventHandler stoppedHandler = (_, args) =>
                {
                    if (args.PropertyName == nameof(PlaybackPanelViewModel.NowPlayingChart)
                        && panel.NowPlayingChart == null)
                    {
                        stopped.TrySetResult(null);
                    }
                };
                panel.PropertyChanged += stoppedHandler;
                try
                {
                    panel.Start().GetAwaiter().GetResult();
                    secondCompletion.SetCanceled();
                    await stopped.Task;
                }
                finally
                {
                    panel.PropertyChanged -= stoppedHandler;
                }
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.AreEqual(0, player.CloseProcessCount);
            }
            finally
            {
                testSettings.OperationModeLR2DB = originalLr2Database;
                if (Directory.Exists(root))
                {
                    DisposeWorkspace();
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>一時配置の確認受諾時だけ配置先を再生し、拒否時は開始せず停止・Closeへ収束することを確認します。</summary>
    /// <remarks>実LR2bodyの確認分岐と配置から開始への接続を通し、processとwindow操作だけを既存gatewayで代替します。</remarks>
    [TestMethod]
    public void PlaybackPanel_TemporaryInstallConfirmationControlsPlaybackWorkflow()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            bool originalLr2Database = testSettings.OperationModeLR2DB;
            string root = workspace.RootDirectory;
            string sourceDirectory = Path.Combine(root, "Source");
            string installDirectory = Path.Combine(root, "Install");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string songDbPath = Path.Combine(root, "song.db");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(chartPath, "#TITLE test");
            File.WriteAllBytes(songDbPath, []);
            PlaybackPanelViewModel? acceptedPanel = null;
            PlaybackPanelViewModel? rejectedPanel = null;
            var acceptedGateway = new RecordingExternalPlayerProcessGateway();
            try
            {
                testSettings.OperationModeLR2DB = true;

                acceptedGateway.Session.KeepRunning = true;
                acceptedGateway.Session.MainWindowHandle = new ExternalWindowHandle(new IntPtr(21));
                LR2body acceptedPlayer = CreateLr2Player(root, acceptedGateway);
                var acceptedDialogs = new FakePlaybackDialogService { TemporaryInstallConfirmationResult = true };
                acceptedPanel = CreateTemporaryInstallPanel(testSettings,
                    chartPath,
                    installDirectory,
                    songDbPath,
                    acceptedPlayer,
                    acceptedDialogs, uiScheduler: new TestUiScheduler(() => null));
                int startedCount = 0;
                acceptedPanel.PlaybackStarted += (_, _) => startedCount++;

                acceptedPanel.Start().GetAwaiter().GetResult();

                Assert.AreEqual(1, acceptedDialogs.TemporaryInstallConfirmationCount);
                StringAssert.Contains(acceptedGateway.LaunchRequest.Arguments, Path.Combine(installDirectory, Path.GetFileName(chartPath)));
                Assert.AreEqual(1, startedCount);
                Assert.IsNotNull(acceptedPanel.NowPlayingChart);

                acceptedGateway.Session.KeepRunning = false;
                acceptedPanel.BeginShutdown();
                acceptedPanel.CloseForShutdown().GetAwaiter().GetResult();
                var rejectedGateway = new RecordingExternalPlayerProcessGateway();
                LR2body rejectedPlayer = CreateLr2Player(root, rejectedGateway);
                var rejectedDialogs = new FakePlaybackDialogService { TemporaryInstallConfirmationResult = false };
                rejectedPanel = CreateTemporaryInstallPanel(testSettings,
                    chartPath,
                    installDirectory,
                    songDbPath,
                    rejectedPlayer,
                    rejectedDialogs, uiScheduler: new TestUiScheduler(() => null));

                rejectedPanel.Start().GetAwaiter().GetResult();

                Assert.AreEqual(1, rejectedDialogs.TemporaryInstallConfirmationCount);
                Assert.IsFalse(rejectedGateway.Session.Started);
                Assert.IsNull(rejectedPanel.NowPlayingChart);
                rejectedPanel.BeginShutdown();
                rejectedPanel.CloseForShutdown().GetAwaiter().GetResult();
            }
            finally
            {
                acceptedGateway.Session.KeepRunning = false;
                if (acceptedPanel != null) { acceptedPanel.BeginShutdown(); acceptedPanel.CloseForShutdown().GetAwaiter().GetResult(); }
                if (rejectedPanel != null) { rejectedPanel.BeginShutdown(); rejectedPanel.CloseForShutdown().GetAwaiter().GetResult(); }
                testSettings.OperationModeLR2DB = originalLr2Database;
                if (Directory.Exists(root))
                {
                    DisposeWorkspace();
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>一時配置確認のdialogが失敗すると元例外を返し、再生せず現在曲を解除してplayerを閉じることを確認します。</summary>
    /// <remarks>実LR2bodyの確認分岐へ到達し、process未起動と現在曲の解除を観測します。</remarks>
    [TestMethod]
    public void PlaybackPanel_TemporaryInstallDialogFailureStopsAndPropagates()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            bool originalLr2Database = testSettings.OperationModeLR2DB;
            string root = workspace.RootDirectory;
            string sourceDirectory = Path.Combine(root, "Source");
            string installDirectory = Path.Combine(root, "Install");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string songDbPath = Path.Combine(root, "song.db");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(chartPath, "#TITLE test");
            File.WriteAllBytes(songDbPath, []);
            var failure = new InvalidOperationException("confirmation failed");
            var gateway = new RecordingExternalPlayerProcessGateway();
            LR2body player = CreateLr2Player(root, gateway);
            var dialogs = new FakePlaybackDialogService { TemporaryInstallConfirmationException = failure };
            PlaybackPanelViewModel? panel = null;
            try
            {
                testSettings.OperationModeLR2DB = true;
                panel = CreateTemporaryInstallPanel(testSettings,
                    chartPath,
                    installDirectory,
                    songDbPath,
                    player,
                    dialogs, uiScheduler: new TestUiScheduler(() => null));

                Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => panel.Start().GetAwaiter().GetResult()));
                Assert.AreEqual(1, dialogs.TemporaryInstallConfirmationCount);
                Assert.IsFalse(gateway.Session.Started);
                Assert.IsNull(panel.NowPlayingChart);
                panel.BeginShutdown();
                panel.CloseForShutdown().GetAwaiter().GetResult();
            }
            finally
            {
                if (panel != null) { panel.BeginShutdown(); panel.CloseForShutdown().GetAwaiter().GetResult(); }
                testSettings.OperationModeLR2DB = originalLr2Database;
                if (Directory.Exists(root))
                {
                    DisposeWorkspace();
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>行起動からの一時改名が共有禁止handleで失敗すると一回通知し、開始通知なしで停止して元入力と既存宛先を保全することを確認します。</summary>
    /// <remarks>一時配置の物理コピー・回収または確認から開始への接続を通すため実ライブラリを接続し、playerの実行は局所probeで制御します。</remarks>
    [TestMethod]
    public async Task PlaybackPanel_TableRowActivationNotifiesTemporaryInstallMoveFailure()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            bool originalLr2Database = testSettings.OperationModeLR2DB;
            string root = workspace.RootDirectory;
            string sourceDirectory = Path.Combine(root, "Source");
            string installDirectory = Path.Combine(root, "Install");
            string chartPath = Path.Combine(sourceDirectory, "chart.bms");
            string installedChartPath = Path.Combine(installDirectory, "chart.bms");
            string songDbPath = Path.Combine(root, "song.db");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(installDirectory);
            File.WriteAllText(chartPath, "#TITLE source");
            File.WriteAllText(installedChartPath, "#TITLE installed");
            File.WriteAllBytes(songDbPath, []);
            var player = new FakeBmsPlayer();
            var dialogs = new FakePlaybackDialogService();
            try
            {
                testSettings.OperationModeLR2DB = true;
                LibraryChartRow activationRow;
                PlaybackPanelViewModel panel = CreateTemporaryInstallPanel(testSettings,
                    chartPath,
                    installDirectory,
                    songDbPath,
                    player,
                    dialogs,
                    out activationRow, uiScheduler: new TestUiScheduler(() => null));
                int startedCount = 0;
                panel.PlaybackStarted += (_, _) => startedCount++;

                using (FileStream sourceHandle = new(
                    chartPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    Task failureNotification = dialogs.WaitForPlaybackFailureAsync();
                    panel.HandleTableRowActivation(0, activationRow).GetAwaiter().GetResult();
                    await failureNotification;
                }

                Assert.IsNotNull(dialogs.LastPlaybackFailure);
                Assert.AreEqual(1, dialogs.PlaybackFailureNotificationCount);
                Assert.IsNull(panel.NowPlayingChart);
                Assert.AreEqual(0, startedCount);
                Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
                Assert.IsTrue(File.Exists(chartPath));
                Assert.IsTrue(File.Exists(installedChartPath));
            }
            finally
            {
                testSettings.OperationModeLR2DB = originalLr2Database;
                if (Directory.Exists(root))
                {
                    DisposeWorkspace();
                }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>一覧差替え前に取得した譜面snapshotを保持し、差替え中は解放済みproviderへ再アクセスせず新一覧確定後だけ新しい対象を解決することを確認します。</summary>
    [TestMethod]
    public async Task MainChartListPlaybackQueue_ResolvesSnapshotBeforeReplacementWithoutReusingOldProvider()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string path = workspace.GetPath("path.tmp");
        File.WriteAllBytes(path, []);
        string replacementPath = workspace.GetPath("replacementPath.tmp");
        File.WriteAllBytes(replacementPath, []);
        try
        {
            ChartFile source = ((ChartTestValues.Empty() with { Path = path }));
            var row = LibraryChartRow.FromChartFile(source);
            int providerReads = 0;
            bool disposed = false;
            row.SetChartTransientStateProvider((_, _) =>
            {
                Assert.IsFalse(disposed, "The disposed provider must never be accessed.");
                providerReads++;
                return ChartFileTransientState.Empty;
            });
            var chartList = new MainChartListViewModel { Rows = new List<object> { row } };
            var queue = new MainChartListPlaybackQueue(chartList);
            ChartFile snapshot = queue.GetPlaybackChart(0);
            int readsBeforeReplacement = providerReads;
            Assert.IsTrue(readsBeforeReplacement > 0);
            chartList.RowsReplacing += (_, _) => disposed = true;
            chartList.PrepareRowsReplacement();
            ChartFile duringReplacement = await Task.Run(() => queue.GetPlaybackChart(0));
            Assert.IsNull(duringReplacement);
            Assert.AreEqual(path, snapshot.Path);
            Assert.AreEqual(readsBeforeReplacement, providerReads);
            chartList.Rows = new List<object> { (ChartTestValues.Empty() with { Path = replacementPath }) };
            Assert.AreEqual(replacementPath, queue.GetPlaybackChart(0).Path);
        }
        finally
        {
            File.Delete(path);
            File.Delete(replacementPath);
        }
    }

    /// <summary>同chart別entryの位置検索後に並替わっても、元位置へ移った別entryを現在曲として採用しません。</summary>
    [TestMethod]
    public void MainChartListPlaybackQueue_RejectsAnotherEntryMovedToResolvedPosition()
    {
        ChartFile chart = ChartTestValues.Empty() with { Path = Path.Combine(Path.GetTempPath(), $"PlaybackQueue-{Guid.NewGuid():N}.bms"), Md5 = new string('a', 32) };
        var first = new NavigationPlaylistEntry("A");
        var second = new NavigationPlaylistEntry("B");
        var rows = new ReorderingPlaybackRows(new PlaylistDetailSourceRow(first, chart).CreateViewRow(),
            new PlaylistDetailSourceRow(second, chart).CreateViewRow());
        var queue = new MainChartListPlaybackQueue(new MainChartListViewModel { Rows = rows });
        PlaybackQueueItem current = queue.CapturePlaybackItem(0);
        rows.AfterIdentityLocated = rows.Reverse;

        int index = queue.IndexOfPlaybackItem(current.Locator, current.Chart);

        Assert.AreSame(second, rows.GetSelectionIdentity(0), "位置計算後に同chartの別entryへ並替えています。");
        Assert.IsTrue(index == -1 || ReferenceEquals(first, rows.GetSelectionIdentity(index)), "位置なし又は同じentryだけを返します。");
    }

    /// <summary>行取得と識別取得の間に同chart別entryが並替わっても、返した行の譜面とlocatorを一組として捕捉します。</summary>
    [TestMethod]
    public void MainChartListPlaybackQueue_CapturesChartAndLocatorFromReturnedRowDuringReorder()
    {
        ChartFile chart = ChartTestValues.Empty() with { Path = Path.Combine(Path.GetTempPath(), $"PlaybackQueue-{Guid.NewGuid():N}.bms"), Md5 = new string('a', 32) };
        var first = new NavigationPlaylistEntry("A");
        var second = new NavigationPlaylistEntry("B");
        var rows = new ReorderingPlaybackRows(new PlaylistDetailSourceRow(first, chart).CreateViewRow(),
            new PlaylistDetailSourceRow(second, chart).CreateViewRow());
        var queue = new MainChartListPlaybackQueue(new MainChartListViewModel { Rows = rows });
        rows.AfterRowRead = rows.Reverse;

        PlaybackQueueItem item = queue.CapturePlaybackItem(0);

        Assert.AreSame(second, rows.GetSelectionIdentity(0), "行を返す境界で並替えています。");
        Assert.IsTrue(GridRowResolver.TryGetPlaybackChart(rows.LastReturnedRow, out ChartFile returnedChart));
        Assert.AreSame(returnedChart, item.Chart);
        Assert.AreSame(GridRowResolver.GetPlaylistEntry(rows.LastReturnedRow), item.Locator);
    }

    /// <summary>token・packageのない実subsetでも、同kind別pathの非先頭行を捕捉し既存identity索引で同じ位置へ戻ります。</summary>
    [TestMethod]
    public void MainChartListPlaybackQueue_ResolvesTokenlessSubsetItemAtItsCurrentPosition()
    {
        string root = Path.Combine(Path.GetTempPath(), $"PlaybackQueue-{Guid.NewGuid():N}");
        ChartFile[] charts =
        [
            ChartTestValues.Empty() with { Path = Path.Combine(root, "first.bms"), Title = "A" },
            ChartTestValues.Empty() with { Path = Path.Combine(root, "second.bms"), Title = "B" }
        ];
        var sources = charts.Select(chart => ChartListSourceRow.FromChartFile(chart)).ToList();
        var view = new ChartListVirtualView(sources, ChartListOrder.CreateTitleAscending(sources),
            LibraryChartRow.FromSourceRow, scoreInput: new ChartListScoreInput(null));
        var queue = new MainChartListPlaybackQueue(new MainChartListViewModel { Rows = view });

        PlaybackQueueItem item = queue.CapturePlaybackItem(1);

        Assert.AreEqual(charts[1].Path, item.Chart.Path);
        Assert.AreEqual(1, queue.IndexOfPlaybackItem(item.Locator, item.Chart));
    }

    /// <summary>キューが差替え後の行集合を読み、指定した選択を一覧の通知へ接続することで古い行や選択を保持する誤りを検出します。</summary>
    [TestMethod]
    public void MainChartListPlaybackQueue_ForwardsLiveRowsAndSelection()
    {
        object firstRow = new object();
        object secondRow = new object();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { firstRow },
            SelectedIndex = -1
        };
        var queue = new MainChartListPlaybackQueue(chartList);
        var changed = new List<string>();
        chartList.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);

        chartList.Rows = new List<object> { secondRow, firstRow };
        queue.SelectedIndex = 1;

        Assert.AreEqual(2, queue.Count);
        Assert.AreSame(secondRow, queue.GetRow(0));
        Assert.AreEqual(1, chartList.SelectedIndex);
        CollectionAssert.Contains(changed, nameof(MainChartListViewModel.SelectedIndex));
    }

    /// <summary>SingleがONでも明示Next・Previousは現一覧から送り、別行ハイライトや不一致の起動入力で現在曲を変えません。</summary>
    [TestMethod]
    public async Task PlaybackPanel_StartNextAndPreviousFollowLiveQueueSelection()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string firstPath = workspace.GetPath("firstPath.tmp");
        File.WriteAllBytes(firstPath, []);
        string secondPath = workspace.GetPath("secondPath.tmp");
        File.WriteAllBytes(secondPath, []);
        string thirdPath = workspace.GetPath("thirdPath.tmp");
        File.WriteAllBytes(thirdPath, []);
        bool originalRepeat = testSettings.RepeatPlayMode;
        bool originalFolderSkip = testSettings.FolderSkipPlayMode;
        bool originalSingle = testSettings.SinglePlayMode;
        var player = new FakeBmsPlayer();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object>
            {
                (ChartTestValues.Empty() with { Path = firstPath }),
                (ChartTestValues.Empty() with { Path = secondPath }),
                (ChartTestValues.Empty() with { Path = thirdPath })
            },
            SelectedIndex = 1
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            testSettings.RepeatPlayMode = false;
            testSettings.FolderSkipPlayMode = false;
            testSettings.SinglePlayMode = true;

            panel.HandleTableSelection(chartList.Rows[1]);
            Assert.IsNotNull(panel.DisplayedChart);
            Assert.AreEqual(secondPath, panel.DisplayedChart.Path);
            Task playStartObserved = player.WaitForCommandAsync("PlayStart:" + secondPath);
            panel.HandleTableRowActivation(1, chartList.Rows[1]).GetAwaiter().GetResult();
            await playStartObserved;
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(secondPath, panel.NowPlayingChart.Path);
            Assert.AreEqual(1, chartList.SelectedIndex);
            panel.HandleTableSelection(chartList.Rows[0]);
            Assert.IsNotNull(panel.DisplayedChart);
            Assert.AreEqual(secondPath, panel.DisplayedChart.Path);
            panel.HandleTableRowActivation(0, new object()).GetAwaiter().GetResult();

            panel.Next().GetAwaiter().GetResult();
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(thirdPath, panel.NowPlayingChart.Path);
            Assert.AreEqual(2, chartList.SelectedIndex);

            panel.Previous().GetAwaiter().GetResult();
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(secondPath, panel.NowPlayingChart.Path);
            Assert.AreEqual(1, chartList.SelectedIndex);
            CollectionAssert.AreEqual(
                new[] { "PlayStart:" + secondPath, "PlayStart:" + thirdPath, "PlayStart:" + secondPath },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
        }
        finally
        {
            testSettings.RepeatPlayMode = originalRepeat;
            testSettings.FolderSkipPlayMode = originalFolderSkip;
            testSettings.SinglePlayMode = originalSingle;
            File.Delete(firstPath);
            File.Delete(secondPath);
            File.Delete(thirdPath);
        }
    }

    /// <summary>通常停止後の明示再生は受理し、終了受付を閉じた後は各直接入口・command・旧終了callbackを拒否して終了専用Closeだけ行うことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_ShutdownRejectsPlaybackInputsButOrdinaryStopAllowsRestart()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string firstPath = workspace.GetPath("firstPath.tmp");
        File.WriteAllBytes(firstPath, []);
        string secondPath = workspace.GetPath("secondPath.tmp");
        File.WriteAllBytes(secondPath, []);
        var player = new FakeBmsPlayer();
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = firstPath }), (ChartTestValues.Empty() with { Path = secondPath }) },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(rows), new InMemoryPlaybackSettingsStore(),
            new FakePlaybackDialogService(), _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.Start().GetAwaiter().GetResult();
            panel.StopPlayback(closeProcess: true).GetAwaiter().GetResult();
            panel.Start().GetAwaiter().GetResult();
            Assert.AreEqual(2, player.Commands.Count(command => command == "PlayStart:" + firstPath));
            Action<object, EventArgs> exit = player.ExitHandler!;
            panel.BeginShutdown();
            long prepared = panel.BeginPlayback((ChartFile)rows.Rows[0]!, 0);
            string[] before = player.Commands.ToArray();

            await Task.Run(() =>
            {
                exit(player, EventArgs.Empty);
                panel.Next().GetAwaiter().GetResult();
                panel.Previous().GetAwaiter().GetResult();
                panel.Start().GetAwaiter().GetResult();
                panel.StartAtIndex(1).GetAwaiter().GetResult();
                panel.ExecuteTableRowActivation(1, rows.Rows[1]).GetAwaiter().GetResult();
                panel.StopPlayback(closeProcess: true).GetAwaiter().GetResult();
                panel.CloseProcess().GetAwaiter().GetResult();
                panel.TogglePause();
                panel.RestartPlayingBmsFile();
                panel.FastForwardStart();
                panel.FastForwardEnd();
                panel.FastBackwardStart();
                panel.FastBackwardEnd();
                panel.ShowInfo();
                panel.ShowEffect();
                panel.ChangePlayside();
                panel.IncreaseHighSpeed();
                panel.DecreaseHighSpeed();
                panel.PlayerVolume = 37;
                panel.CurrentlyPlayingTime = TimeSpan.FromSeconds(17);
            });
            Assert.IsFalse(await panel.TryPlayStart(prepared, firstPath, (sender, args) => panel.Next(sender, args).GetAwaiter().GetResult()));
            int surfaceRequests = 0;
            await panel.HandleTableRowActivation(1, rows.Rows[1], () => surfaceRequests++);
            await panel.HandlePlayButton(() => surfaceRequests++);
            await panel.Start(requestPlayerSurface: () => surfaceRequests++);
            Assert.AreEqual(0, surfaceRequests);
            Assert.IsFalse(panel.NextCommand.CanExecute);
            Assert.IsFalse(panel.StartCommand.CanExecute);
            CollectionAssert.AreEqual(before, player.Commands.ToArray());
            Assert.AreEqual(TimeSpan.Zero, player.CurrentTime);
            panel.CloseForShutdown().GetAwaiter().GetResult();
            Assert.AreEqual(2, player.CloseProcessCount);
        }
        finally
        {
            File.Delete(firstPath);
            File.Delete(secondPath);
        }
    }

    /// <summary>行解決中のNextに終了が重なると解決の実終端を待ってCloseし、受付を再開して別曲を始めないことを確認します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_ShutdownDrainsEarlierNavigationWithoutStartingAnotherChart()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string firstPath = workspace.GetPath("firstPath.tmp");
        File.WriteAllBytes(firstPath, []);
        string secondPath = workspace.GetPath("secondPath.tmp");
        File.WriteAllBytes(secondPath, []);
        using var releaseRow = new ManualResetEventSlim();
        var enteredRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new FakeBmsPlayer();
        var rows = new MainChartListViewModel
        {
            Rows = new List<object> { (ChartTestValues.Empty() with { Path = firstPath }), (ChartTestValues.Empty() with { Path = secondPath }) },
            SelectedIndex = 0
        };
        var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), enteredRow, releaseRow);
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(),
            queue, new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(),
            _ => { }, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task? next = null;
        Task? terminal = null;
        try
        {
            panel.Start().GetAwaiter().GetResult();
            next = Task.Run(() => panel.Next());
            await TestUiDispatcherHost.AwaitNotificationAsync(enteredRow.Task, next, "playback.next-row");
            panel.BeginShutdown();
            player.BeforeClose = () => Assert.IsTrue(queue.RowResolved, "曲解決を player close が追い越さない。");
            terminal = Task.Run(() =>
            {
                terminalStarted.SetResult();
                panel.CloseForShutdown().GetAwaiter().GetResult();
            });
            await TestUiDispatcherHost.AwaitNotificationAsync(terminalStarted.Task, terminal, "playback.shutdown-worker");
            releaseRow.Set();
            await Task.WhenAll(next, terminal);
            CollectionAssert.AreEqual(new[] { "PlayStart:" + firstPath, "Close" }, player.Commands.ToArray());
        }
        finally
        {
            releaseRow.Set();
            try
            {
                await Task.WhenAll(next ?? Task.CompletedTask, terminal ?? Task.CompletedTask)
                    ;
            }
            finally
            {
                File.Delete(firstPath);
                File.Delete(secondPath);
            }
        }
    }

    /// <summary>手動Nextの後に並んだ旧自然終了は交換後の曲を送らず、後続の明示Nextだけを受付順に実行することを確認します。</summary>
    [TestMethod]
    public async Task NaturalExit_QueuedBehindManualNextDoesNotAdvanceTheReplacementSong()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        int unexpectedNotificationCount = 0;
        string[] paths = [workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp"), workspace.GetPath(Guid.NewGuid().ToString("N") + ".tmp")];
        foreach (string temporaryPath in paths) { File.WriteAllBytes(temporaryPath, []); }
        using var releaseRow = new ManualResetEventSlim();
        var enteredRow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new PreloadBmsPlayer();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var queue = new GatedPlaybackChartQueue(new MainChartListPlaybackQueue(rows), enteredRow, releaseRow);
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue,
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount),
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        var start = Task.Run(() => panel.StartAtIndex(0));
        Task? manualNext = null;
        Task? sentinel = null;
        try
        {
            await TestUiDispatcherHost.AwaitNotificationAsync(enteredRow.Task, start, "playback.start-row");
            manualNext = panel.Next();
            player.ExitHandler?.Invoke(player, EventArgs.Empty);
            sentinel = panel.Next();
            releaseRow.Set();
            await Task.WhenAll(start, manualNext, sentinel);
            CollectionAssert.AreEqual(paths.Take(3).ToArray(), player.Starts.ToArray());
            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(paths[2], panel.NowPlayingChart.Path);
        }
        finally
        {
            releaseRow.Set();
            await Task.WhenAll(start, manualNext ?? Task.CompletedTask, sentinel ?? Task.CompletedTask);
            await panel.CloseForShutdown();
            foreach (string path in paths) { File.Delete(path); }
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }

    /// <summary>準備時除外後も要求方向の有効曲だけへ送り、手動送り先がなければ現在曲・再生状態・選択を維持します。</summary>
    [TestMethod]
    [DataRow("Bmson", false)]
    [DataRow("Missing", false)]
    [DataRow("Multiple", false)]
    [DataRow("None", false)]
    [DataRow("Bmson", true)]
    [DataRow("Unowned", false)]
    [DataRow("Unowned", true)]
    public async Task Navigation_UnavailableCandidatesKeepRequestedDirection(string unavailable, bool forward)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 5);
        ChartFile[] charts = paths.Select(path => ChartTestValues.Empty() with { Path = path }).ToArray();
        charts[1] = unavailable == "Missing"
            ? charts[1] with { Path = paths[1] + ".missing" }
            : CreatePlaybackTargetChart(ChartFileKind.Bmson, paths[1]);
        if (unavailable == "None") { charts[0] = charts[0] with { Path = paths[0] + ".missing" }; }
        int current = unavailable == "Multiple" ? 3 : 2;
        if (unavailable == "Multiple") { charts[2] = charts[2] with { Path = paths[2] + ".missing" }; }
        var rows = new MainChartListViewModel
        {
            Rows = charts.Select((chart, index) => index == 1 && unavailable == "Unowned"
                ? (object)new PlaylistDetailSourceRow(new NavigationPlaylistEntry("missing"), null).CreateViewRow() : chart).ToList(),
            SelectedIndex = forward ? 0 : current
        };
        var player = new FakeBmsPlayer();
        int notices = 0;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.Start();
            ChartFile? original = panel.NowPlayingChart;
            if (forward) { await panel.Next(); } else { await panel.Previous(); }
            int target = forward ? current : 0;
            string[] expectedStarts = unavailable == "None" ? [paths[current]] : [paths[forward ? 0 : current], paths[target]];
            CollectionAssert.AreEqual(expectedStarts,
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).Select(command => command[10..]).ToArray());
            Assert.AreEqual(unavailable == "None" ? paths[current] : paths[target], panel.NowPlayingChart?.Path);
            if (unavailable == "None") { Assert.AreSame(original, panel.NowPlayingChart); }
            Assert.IsTrue(panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
            Assert.AreEqual(unavailable == "None" ? current : target, rows.SelectedIndex);
            Assert.AreEqual(0, notices);
        }
        finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>手動送りの初回候補ゼロはfolder設定や不備種別によらず現在曲・PLAY/PAUSE・次回方式を保ち、自然終曲だけは停止します。</summary>
    /// <remarks>同folder正常曲は中央から前後と自然終了を、未所持・別folder欠落・非対応形式は手動を、混在は自然終了を確認します。実ロード後の枯渇はLegacy/Readyケースへ分担します。</remarks>
    [TestMethod]
    [DataRow("SameFolder", "Next", true, false)]
    [DataRow("SameFolder", "Previous", true, false)]
    [DataRow("SameFolder", "Natural", true, false)]
    [DataRow("Unowned", "Next", false, false)]
    [DataRow("Missing", "Previous", true, true)]
    [DataRow("Unsupported", "Next", false, false)]
    [DataRow("Mixed", "Natural", false, false)]
    public async Task Navigation_InitialEmptyKeepsManualPlaybackAndPendingPlayerButNaturalExitStops(
        string unavailable, string operation, bool folderSkip, bool paused)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            foreach (string folder in new[] { "A", "B" }) { Directory.CreateDirectory(Path.Combine(root, folder)); }
            string currentPath = Path.Combine(root, "A", "current.bms");
            string beforePath = Path.Combine(root, "A", "before.bms");
            string afterPath = Path.Combine(root, "A", "after.bms");
            string missingPath = Path.Combine(root, "B", "missing.bms");
            string unsupportedPath = Path.Combine(root, "B", "unsupported.bmson");
            string healthyPath = Path.Combine(root, "B", "healthy.bms");
            foreach (string path in new[] { currentPath, beforePath, afterPath, unsupportedPath, healthyPath }) { File.WriteAllText(path, ""); }
            ChartFile currentChart = ChartTestValues.Empty() with { Path = currentPath };
            object unowned = new PlaylistDetailSourceRow(new NavigationPlaylistEntry("unowned"), null).CreateViewRow();
            ChartFile missing = ChartTestValues.Empty() with { Path = missingPath };
            ChartFile unsupported = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = unsupportedPath };
            List<object> candidates = unavailable switch
            {
                "SameFolder" => [ChartTestValues.Empty() with { Path = beforePath }, currentChart, ChartTestValues.Empty() with { Path = afterPath }],
                "Unowned" => [currentChart, unowned],
                "Missing" => [missing, currentChart],
                "Unsupported" => [currentChart, unsupported],
                _ => [currentChart, unowned, missing, unsupported]
            };
            int currentIndex = unavailable is "SameFolder" or "Missing" ? 1 : 0;
            var rows = new MainChartListViewModel { Rows = candidates, SelectedIndex = currentIndex };
            var player = new FakeBmsPlayer();
            var replacement = new FakeBmsPlayer();
            int factoryCalls = 0;
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore { FolderSkipPlay = folderSkip }, new FakePlaybackDialogService(),
                _ => Assert.Fail("ロード前の候補除外で入力不良を通知しません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            try
            {
                await panel.StartAtIndex(currentIndex);
                if (paused) { panel.TogglePause(); }
                ChartFile? original = panel.NowPlayingChart;
                int closes = player.CloseProcessCount;
                ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { factoryCalls++; return replacement; }, supportsBmson: false);

                if (operation == "Previous") { await panel.Previous(); }
                else { await panel.Next(operation == "Natural" ? player : null, EventArgs.Empty); }

                CollectionAssert.AreEqual(new[] { "PlayStart:" + currentPath },
                    player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
                Assert.AreEqual(0, factoryCalls);
                if (operation == "Natural")
                {
                    Assert.IsNull(panel.NowPlayingChart);
                    Assert.IsFalse(panel.IsPlaying);
                    Assert.IsFalse(panel.IsPaused);
                    Assert.IsTrue(player.CloseProcessCount > closes);
                }
                else
                {
                    Assert.AreSame(original, panel.NowPlayingChart);
                    Assert.AreEqual(!paused, panel.IsPlaying);
                    Assert.AreEqual(paused, panel.IsPaused);
                    Assert.AreEqual(currentIndex, rows.SelectedIndex);
                    Assert.AreEqual(closes, player.CloseProcessCount);

                    rows.Rows = new List<object> { ChartTestValues.Empty() with { Path = healthyPath } };
                    await panel.StartAtIndex(0);
                    Assert.AreEqual(1, factoryCalls, "候補ゼロで消費しなかった次回要求を、次の正常な明示Startで採用します。");
                    Assert.AreEqual(healthyPath, panel.NowPlayingChart?.Path);
                    CollectionAssert.AreEqual(new[] { "PlayStart:" + healthyPath },
                        replacement.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
                }
            }
            finally
            {
                try { await panel.CloseForShutdown(); }
                finally { DisposeWorkspace(); }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>前曲のロード入力不良後は後方の正常曲へ進み、後続が欠落だけなら停止します。初回候補ゼロの無作用へ読み替えません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Previous_LegacyInvalidStartContinuesBackward(bool remainingMissing)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 4);
        if (remainingMissing) { File.Delete(paths[0]); }
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList(), SelectedIndex = 2 };
        var player = new FakeBmsPlayer();
        player.BeforePlayStart = () => player.PlayStartException = player.Commands.Last() == "PlayStart:" + paths[1]
            ? new InvalidDataException("invalid previous input") : null;
        int notices = 0;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.Start();
            await panel.Previous();
            CollectionAssert.AreEqual(remainingMissing ? new[] { paths[2], paths[1] } : new[] { paths[2], paths[1], paths[0] },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).Select(command => command[10..]).ToArray());
            Assert.AreEqual(remainingMissing ? null : paths[0], panel.NowPlayingChart?.Path);
            Assert.AreEqual(!remainingMissing, panel.IsPlaying);
            Assert.IsFalse(panel.IsPaused);
            Assert.AreEqual(remainingMissing ? 1 : 0, rows.SelectedIndex);
            Assert.AreEqual(1, notices);
        }
        finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>前曲候補のReady入力不良は同期・非同期とも一回通知して後退し、同じ開始中に保存した次回方式は次の明示開始まで保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Previous_InvalidReadyContinuesBackwardWithoutRecapturingPlayer(bool asynchronous)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 4);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        File.WriteAllText(paths[2], "#BPM 120\n#RANDOM 2147483647\n");
        BMSFile.InvalidBmsFileException failure = Assert.ThrowsExactly<Ribbit.BMS.BMSFile.InvalidBmsFileException>(() => Ribbit.BMS.PlaybackChart.Load(paths[2]));
        if (!asynchronous) { ready.SetException(failure); }
        var player = new PreloadBmsPlayer
        {
            BeginOperation = path =>
            {
                if (path != paths[1]) { return new PlaybackStartOperation(Task.CompletedTask, completion.Task); }
                entered.TrySetResult();
                return new PlaybackStartOperation(ready.Task, ready.Task);
            }
        };
        var replacement = new PreloadBmsPlayer { Completion = completion.Task };
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList(), SelectedIndex = 2 };
        int notices = 0;
        int created = 0;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task? previous = null;
        try
        {
            await panel.Start();
            previous = panel.Previous();
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, previous, "previous.invalid-ready");
            if (asynchronous)
            {
                ((ISettingsDialogPlaybackRuntimePort)panel).UsePlayerForNextPlayback(() => { created++; return replacement; }, false);
                ready.SetException(failure);
            }
            await previous;
            CollectionAssert.AreEqual(new[] { paths[2], paths[1], paths[0] }, player.Starts.ToArray());
            Assert.AreEqual(paths[0], panel.NowPlayingChart?.Path);
            Assert.AreEqual(0, rows.SelectedIndex);
            Assert.AreEqual(1, notices);
            Assert.AreEqual(0, created);
            if (asynchronous)
            {
                await panel.StartAtIndex(3);
                Assert.AreEqual(1, created);
                CollectionAssert.AreEqual(new[] { paths[3] }, replacement.Starts.ToArray());
            }
        }
        finally
        {
            ready.TrySetException(failure);
            try { await (previous ?? Task.CompletedTask); }
            finally { completion.TrySetResult(); await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
        }
    }

    /// <summary>同一フォルダを後退中に一覧が5行から3行へ縮小しStopまたは終了が失効させても、元要求と後続要求・終了が終端し、新曲・開始イベント・通知を追加しません。</summary>
    /// <remarks>実queueの行snapshot取得とrowGate解放後に停止します。移動予算超過は旧無限巡回の明示失敗にし、厳密な行取得回数は契約にしません。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Previous_ListShrinkAndInvalidationTerminateNavigation(bool shutdown)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 5);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList(), SelectedIndex = 4 };
        var queue = new PreviousNavigationQueue(new MainChartListPlaybackQueue(rows), entered, release);
        var player = new FakeBmsPlayer();
        int notices = 0;
        int starting = 0;
        int started = 0;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue,
            new InMemoryPlaybackSettingsStore { RepeatPlay = true, FolderSkipPlay = true }, new FakePlaybackDialogService(),
            _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        Task? previous = null;
        Task? terminal = null;
        try
        {
            await panel.Start();
            panel.PlaybackStarting += (_, _) => starting++;
            panel.PlaybackStarted += (_, _) => started++;
            queue.Armed = true;
            previous = Task.Run(() => panel.Previous());
            await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, previous, "previous.folder-snapshot");
            rows.Rows = rows.Rows.Cast<object>().Take(3).ToList();
            if (shutdown)
            {
                panel.BeginShutdown();
                player.BeforeClose = () => Assert.IsTrue(queue.Released, "終了は行解決の終端を待ちます。");
                terminal = panel.CloseForShutdown();
                Assert.IsFalse(terminal.IsCompleted);
            }
            else { await panel.StopPlayback(); }
            release.Set();
            await previous;
            await (terminal ?? Task.CompletedTask);
            CollectionAssert.AreEqual(new[] { "PlayStart:" + paths[4] },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
            Assert.AreEqual(0, starting);
            Assert.AreEqual(0, started);
            Assert.AreEqual(0, notices);
            if (!shutdown)
            {
                await panel.StartAtIndex(1);
                Assert.AreEqual(paths[1], panel.NowPlayingChart?.Path);
                await panel.CloseForShutdown();
            }
        }
        finally
        {
            release.Set();
            try { await Task.WhenAll(previous ?? Task.CompletedTask, terminal ?? Task.CompletedTask); }
            finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
        }
    }

    /// <summary>FolderSkipは後方最寄りの異folderへ戻り、全行同folderなら前後とも捕捉したRepeatで現在曲を一回開始します。探索中のモード変更は次要求へ適用します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Previous_FolderTraversalUsesCapturedModes(bool differentFolder, bool forward)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            string[] paths = Enumerable.Range(0, 5).Select(index => Path.Combine(root, differentFolder && index < 2 ? "A" : "B", index + ".bms")).ToArray();
            foreach (string path in paths) { File.WriteAllText(path, "#TITLE previous\n#BPM 120\n#00111:01\n"); }
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList(), SelectedIndex = 4 };
            var queue = new PreviousNavigationQueue(new MainChartListPlaybackQueue(rows), entered, release);
            var settings = new InMemoryPlaybackSettingsStore { RepeatPlay = true, FolderSkipPlay = true };
            var player = new FakeBmsPlayer();
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), queue, settings,
                new FakePlaybackDialogService(), _ => Assert.Fail("有効な候補は通知しません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            Task? previous = null;
            try
            {
                await panel.Start();
                queue.Armed = true;
                previous = Task.Run(() => forward ? panel.Next() : panel.Previous());
                await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, previous, "previous.mode-snapshot");
                if (!differentFolder) { settings.RepeatPlay = false; settings.FolderSkipPlay = false; }
                release.Set();
                await previous;
                int target = differentFolder ? 1 : 4;
                CollectionAssert.AreEqual(new[] { "PlayStart:" + paths[4], "PlayStart:" + paths[target] },
                    player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).ToArray());
                Assert.AreEqual(target, rows.SelectedIndex);
                Assert.AreEqual(paths[target], panel.NowPlayingChart?.Path);
                if (!differentFolder) { await panel.Previous(); Assert.AreEqual(paths[3], panel.NowPlayingChart?.Path); }
            }
            finally
            {
                release.Set();
                try { await (previous ?? Task.CompletedTask); }
                finally { await panel.CloseForShutdown(); DisposeWorkspace(); }
            }
        }
        finally { DisposeWorkspace(); }
    }

    private static string[] CreateNavigationChartFiles(TestWorkspace workspace, int count)
    {
        string[] paths = Enumerable.Range(0, count)
            .Select(_ => Path.Combine(workspace.RootDirectory, "Previous_" + Guid.NewGuid().ToString("N") + ".bms")).ToArray();
        foreach (string path in paths) { File.WriteAllText(path, "#TITLE previous\n#BPM 120\n#00111:01\n"); }
        return paths;
    }

    /// <summary>同hash別pathの通常viewと同chart別entryのplaylist viewで、並替・現在行が残る絞込み後も生存identityの現位置から送ります。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Navigation_CurrentIdentitySurvivesSortAndFilterWithoutMergingDuplicateCharts(bool playlist)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 3);
        ChartFile[] charts = paths.Select((path, index) => ChartTestValues.Empty() with
        {
            Path = path,
            Title = ((char)('A' + index)).ToString(),
            Md5 = new string('a', 32),
            Token = new OwnedChartToken()
        }).ToArray();
        if (playlist) { charts[1] = charts[1] with { Path = paths[0], Token = charts[0].Token }; }
        NavigationPlaylistEntry[] entries = [new("A"), new("B"), new("C")];
        var table = new BMSTable { entries = entries.Cast<BMSTableEntry>().ToList() };
        IList View(bool descending, bool filter)
        {
            int[] indexes = filter ? [2, 1] : descending ? [2, 1, 0] : [0, 1, 2];
            if (playlist)
            {
                PlaylistRequestIdentity identity = PlaylistRequestFactory.CreateIdentity(table, PlaylistDetailSelectionScope.OrdinaryRoot,
                    null, PlaylistDetailFilter.PlaylistFilter, string.Empty, ChartModeFilter.All,
                    new ChartListSortParameters { ColumnsName = "Title", Direction = descending ? ListSortDirection.Descending : ListSortDirection.Ascending },
                    1, 1, 1, 1, true);
                return new PlaylistDetailVirtualView(indexes.Select(index => new PlaylistDetailSourceRow(entries[index], charts[index], entryOrder: index)).ToList(), identity: identity);
            }
            var sources = indexes.Select(index => ChartListSourceRow.FromChartFile(charts[index], ChartListSourceProjectionMode.OwnerBacked)).ToList();
            Assert.IsTrue(ChartListOrder.TryCreate(sources, "Title", descending ? ListSortDirection.Descending : ListSortDirection.Ascending, out ChartListOrder order));
            return new ChartListVirtualView(sources, order, source => LibraryChartRow.FromChartFile(source.Chart), scoreInput: new ChartListScoreInput(null));
        }
        var rows = new MainChartListViewModel { Rows = View(false, false) };
        var player = new FakeBmsPlayer();
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore(), new FakePlaybackDialogService(), _ => Assert.Fail("有効なidentityで通知しません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.StartAtIndex(0);
            rows.Rows = View(true, false);
            await panel.Previous();
            Assert.AreEqual(charts[1].Path, panel.NowPlayingChart?.Path);
            Assert.AreEqual(1, rows.SelectedIndex, "同chartの別entryを混同せず直前の生存行へ戻ります。");
            rows.Rows = View(true, true);
            await panel.Previous();
            Assert.AreEqual(paths[2], panel.NowPlayingChart?.Path);
            Assert.AreEqual(0, rows.SelectedIndex);
        }
        finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>現在行消失後の手動前後送りは現曲を保ち、通常自然終了は停止、SingleRepeatは同曲だけを再開し、その入力不良で別曲へ進みません。</summary>
    [TestMethod]
    [DataRow("Manual")]
    [DataRow("Natural")]
    [DataRow("SingleRepeat")]
    [DataRow("SingleRepeatInvalid")]
    public async Task Navigation_RemovedCurrentRowKeepsManualPlaybackAndSingleRepeatTarget(string scenario)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 2);
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        var player = new FakeBmsPlayer();
        int notices = 0;
        bool single = scenario.StartsWith("Single", StringComparison.Ordinal);
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore { SinglePlay = single, RepeatPlay = single }, new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.StartAtIndex(0);
            ChartFile? current = panel.NowPlayingChart;
            rows.Rows = new List<object> { ChartTestValues.Empty() with { Path = paths[1] } };
            if (scenario == "Manual")
            {
                string[] commands = player.Commands.ToArray();
                await panel.Next();
                await panel.Previous();
                Assert.AreSame(current, panel.NowPlayingChart);
                Assert.IsTrue(panel.IsPlaying);
                CollectionAssert.AreEqual(commands, player.Commands.ToArray());
            }
            else
            {
                if (scenario == "SingleRepeatInvalid") { player.PlayStartException = new InvalidDataException("same chart reload failed"); }
                await panel.Next(player, EventArgs.Empty);
                CollectionAssert.AreEqual(single ? new[] { paths[0], paths[0] } : new[] { paths[0] },
                    player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).Select(command => command[10..]).ToArray());
                Assert.AreEqual(scenario == "SingleRepeat" ? paths[0] : null, panel.NowPlayingChart?.Path);
                Assert.AreEqual(scenario == "SingleRepeatInvalid" ? 1 : 0, notices);
            }
        }
        finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>FolderSkipでは未所持を除外してから元folderと比較し、先読みと実Nextの両方で同folderの後続を飛ばして異folderを選びます。</summary>
    [TestMethod]
    public async Task Navigation_FolderSkipPreloadAndNextIgnoreUnownedRowsBeforeFolderComparison()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            Directory.CreateDirectory(Path.Combine(root, "A"));
            Directory.CreateDirectory(Path.Combine(root, "B"));
            string[] paths = [Path.Combine(root, "A", "one.bms"), Path.Combine(root, "A", "two.bms"), Path.Combine(root, "B", "one.bms")];
            foreach (string path in paths) { File.WriteAllText(path, ""); }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new PreloadBmsPlayer { Completion = completion.Task };
            var rows = new MainChartListViewModel
            {
                Rows = new List<object>
            {
                ChartTestValues.Empty() with { Path = paths[0] }, new PlaylistDetailSourceRow(new NavigationPlaylistEntry("unowned"), null).CreateViewRow(),
                ChartTestValues.Empty() with { Path = paths[1] }, ChartTestValues.Empty() with { Path = paths[2] }
            }
            };
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore { FolderSkipPlay = true }, new FakePlaybackDialogService(), _ => Assert.Fail("有効な候補で通知しません。"), new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            try
            {
                await panel.StartAtIndex(0);
                Assert.AreEqual(paths[0], panel.NowPlayingChart?.Path);
                Assert.AreEqual(paths[2], player.Preloads.Single().Path);
                await panel.Next();
                CollectionAssert.AreEqual(new[] { paths[0], paths[2] }, player.Starts.ToArray());
                Assert.AreEqual(3, rows.SelectedIndex);
            }
            finally { completion.TrySetResult(); await panel.CloseForShutdown(); DisposeWorkspace(); }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>ロード不良後も送り元Aを固定し、Aotherは大小文字差も含め除外、別folderのBotherは採用します。同期開始例外と遅延Readyの試行列を同じ基準で確認します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Navigation_FolderSkipKeepsSuccessfulOriginAcrossInputFailures(bool nextInBadFolder, bool delayedReady)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            foreach (string folder in new[] { "A", "B", "C" }) { Directory.CreateDirectory(Path.Combine(root, folder)); }
            string[] paths = [Path.Combine(root, "A", "current.bms"), Path.Combine(root, "B", "bad.bms"),
                Path.Combine(root, nextInBadFolder ? "B" : "a", "other.bms"), Path.Combine(root, "C", "target.bms")];
            foreach (string path in paths) { File.WriteAllText(path, ""); }
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            File.WriteAllText(paths[1], "#BPM 120\n#RANDOM 2147483647\n");
            BMSFile.InvalidBmsFileException failure = Assert.ThrowsExactly<Ribbit.BMS.BMSFile.InvalidBmsFileException>(() => Ribbit.BMS.PlaybackChart.Load(paths[1]));
            FakeBmsPlayer player;
            if (delayedReady)
            {
                player = new PreloadBmsPlayer
                {
                    Completion = completion.Task,
                    BeginOperation = path =>
                {
                    if (path == paths[1]) { entered.TrySetResult(); return new PlaybackStartOperation(ready.Task, completion.Task); }
                    return new PlaybackStartOperation(Task.CompletedTask, completion.Task);
                }
                };
            }
            else
            {
                player = new FakeBmsPlayer();
                player.BeforePlayStart = () => player.PlayStartException = player.Commands.Last() == "PlayStart:" + paths[1] ? failure : null;
            }
            int notices = 0;
            var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore { FolderSkipPlay = true }, new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            Task? navigation = null;
            try
            {
                await panel.StartAtIndex(0);
                navigation = panel.Next();
                if (delayedReady)
                {
                    await TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, navigation, "navigation.folder-invalid-ready");
                    ready.TrySetException(failure);
                }
                await navigation;
                int target = nextInBadFolder ? 2 : 3;
                CollectionAssert.AreEqual(new[] { paths[0], paths[1], paths[target] },
                    player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).Select(command => command[10..]).ToArray());
                Assert.AreEqual(paths[target], panel.NowPlayingChart?.Path);
                Assert.AreEqual(1, notices);
            }
            finally
            {
                if (delayedReady) { ready.TrySetException(failure); }
                else { ready.TrySetResult(); }
                completion.TrySetResult();
                try { await (navigation ?? Task.CompletedTask); }
                finally { await panel.CloseForShutdown(); DisposeWorkspace(); }
            }
        }
        finally { DisposeWorkspace(); }
    }

    /// <summary>Repeatの先頭折返し不良を周回終端と誤認せず、手動・自然終了とも次の正常曲へ進み、全開始不良なら一巡だけで停止します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task Navigation_RepeatWrapContinuesAfterFirstInputFailureAndStopsAfterOneFailedCycle(bool natural, bool allInvalid)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string[] paths = CreateNavigationChartFiles(workspace, 3);
        var player = new FakeBmsPlayer();
        var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
        int notices = 0;
        var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
            new InMemoryPlaybackSettingsStore { RepeatPlay = true }, new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            await panel.StartAtIndex(2);
            player.BeforePlayStart = () => player.PlayStartException = allInvalid || player.Commands.Last() == "PlayStart:" + paths[0]
                ? new InvalidDataException("repeat input failed") : null;
            await panel.Next(natural ? player : null, EventArgs.Empty);
            CollectionAssert.AreEqual(allInvalid ? new[] { paths[2], paths[0], paths[1], paths[2] } : new[] { paths[2], paths[0], paths[1] },
                player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).Select(command => command[10..]).ToArray());
            Assert.AreEqual(allInvalid ? null : paths[1], panel.NowPlayingChart?.Path);
            Assert.AreEqual(allInvalid ? 3 : 1, notices);
        }
        finally { await panel.CloseForShutdown(); foreach (string path in paths) { File.Delete(path); } }
    }

    /// <summary>明示Startは同folderの指定曲を優先し、その入力不良後は直前の成功曲ではなく初期対象のfolderを固定して探索します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Navigation_ExplicitStartPrioritizesInitialRowAndKeepsItsFolderAfterFailure(bool invalid)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string root = workspace.RootDirectory;
            foreach (string folder in new[] { "A", "B" }) { Directory.CreateDirectory(Path.Combine(root, folder)); }
            string[] paths = [Path.Combine(root, "A", "initial.bms"), Path.Combine(root, "A", "same.bms"), Path.Combine(root, "B", "previous.bms")];
            foreach (string path in paths) { File.WriteAllText(path, ""); }
            var player = new FakeBmsPlayer();
            var rows = new MainChartListViewModel { Rows = paths.Select(path => (object)(ChartTestValues.Empty() with { Path = path })).ToList() };
            int notices = 0;
            var panel = new PlaybackPanelViewModel(player, new ImmediatePlaybackUiDispatcher(), new MainChartListPlaybackQueue(rows),
                new InMemoryPlaybackSettingsStore { FolderSkipPlay = true }, new FakePlaybackDialogService(), _ => notices++, new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
            try
            {
                int previous = invalid ? 2 : 1;
                await panel.StartAtIndex(previous);
                player.BeforePlayStart = () => player.PlayStartException = invalid && player.Commands.Last() == "PlayStart:" + paths[0]
                    ? new InvalidDataException("initial input failed") : null;
                await panel.StartAtIndex(0);
                CollectionAssert.AreEqual(invalid ? new[] { paths[2], paths[0], paths[2] } : new[] { paths[1], paths[0] },
                    player.Commands.Where(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)).Select(command => command[10..]).ToArray());
                Assert.AreEqual(invalid ? paths[2] : paths[0], panel.NowPlayingChart?.Path);
                Assert.AreEqual(invalid ? 1 : 0, notices);
            }
            finally { await panel.CloseForShutdown(); DisposeWorkspace(); }
        }
        finally { DisposeWorkspace(); }
    }

    private sealed class NavigationPlaylistEntry : BMSTableEntry
    {
        internal NavigationPlaylistEntry(string name) { md5 = new string('a', 32); title = name; }
    }

    /// <summary>実queueへ一覧とidentityだけを提供し、行返却又は位置検索の境界で一度だけ並替えます。</summary>
    private sealed class ReorderingPlaybackRows(params object[] rows) : ArrayList(rows), ICustomTableSelectionIdentitySource
    {
        /// <summary>行を捕捉してから返すまでに、一度だけ実行する更新です。</summary>
        internal Action? AfterRowRead { get; set; }
        /// <summary>位置を計算してから返すまでに、一度だけ実行する更新です。</summary>
        internal Action? AfterIdentityLocated { get; set; }
        /// <summary>queueへ実際に返した行です。並替え後の添字から取り直しません。</summary>
        internal object? LastReturnedRow { get; private set; }

        /// <inheritdoc/>
        public override object? this[int index]
        {
            get
            {
                object? row = base[index];
                LastReturnedRow = row;
                Action? action = AfterRowRead;
                AfterRowRead = null;
                action?.Invoke();
                return row;
            }
            set => base[index] = value;
        }

        /// <inheritdoc/>
        public event EventHandler? SelectionUpdateStarting { add { } remove { } }
        /// <inheritdoc/>
        public event EventHandler? SelectionUpdateCompleted { add { } remove { } }
        /// <inheritdoc/>
        public object? GetSelectionIdentity(int index)
            => index >= 0 && index < Count ? GridRowResolver.GetPlaylistEntry(base[index]) : null;
        /// <inheritdoc/>
        public int IndexOfSelectionIdentity(object identity)
        {
            int index = -1;
            for (int candidate = 0; candidate < Count; candidate++)
            {
                if (ReferenceEquals(identity, GetSelectionIdentity(candidate))) { index = candidate; break; }
            }
            Action? action = AfterIdentityLocated;
            AfterIdentityLocated = null;
            action?.Invoke();
            return index;
        }
        /// <inheritdoc/>
        public bool SharesSelectionIdentityScope(ICustomTableSelectionIdentitySource other) => ReferenceEquals(this, other);
    }

    private sealed class PreviousNavigationQueue(
        IPlaybackChartQueue inner, TaskCompletionSource entered, ManualResetEventSlim release) : IPlaybackChartQueue
    {
        private int claimed;
        private int evaluated;
        internal bool Armed { get; set; }
        internal bool Released { get; private set; }
        public int Count => inner.Count;
        public int SelectedIndex { get => inner.SelectedIndex; set => inner.SelectedIndex = value; }
        public object GetRow(int index) => inner.GetRow(index);
        /// <inheritdoc/>
        public int IndexOfPlaybackItem(object locator, ChartFile chart) => inner.IndexOfPlaybackItem(locator, chart);
        /// <inheritdoc/>
        public PlaybackQueueItem CapturePlaybackItem(int index, object? expectedRow = null)
        {
            PlaybackQueueItem item = inner.CapturePlaybackItem(index, expectedRow);
            if (!Armed) { return item; }
            if (Interlocked.Increment(ref evaluated) > 24) { throw new InvalidOperationException("前曲候補探索が一巡の有限予算を超過しました。"); }
            if (index == 2 && Interlocked.Exchange(ref claimed, 1) == 0)
            {
                entered.TrySetResult();
                release.Wait();
                Released = true;
            }
            return item;
        }
    }

    private sealed class GatedPlaybackChartQueue(
        IPlaybackChartQueue inner, TaskCompletionSource entered, ManualResetEventSlim release, int gatedIndex = 1) : IPlaybackChartQueue
    {
        private int rowResolved;
        private int claimed;
        internal bool RowResolved => Volatile.Read(ref rowResolved) != 0;
        public int Count => inner.Count;
        /// <summary>空選択の判定と停止受付の順序だけを制御します。通常は指定しません。</summary>
        internal Action? BeforeSelectedIndexRead { get; set; }
        public int SelectedIndex
        {
            get { BeforeSelectedIndexRead?.Invoke(); return inner.SelectedIndex; }
            set => inner.SelectedIndex = value;
        }
        /// <inheritdoc/>
        public int IndexOfPlaybackItem(object locator, ChartFile chart) => inner.IndexOfPlaybackItem(locator, chart);
        /// <inheritdoc/>
        public PlaybackQueueItem CapturePlaybackItem(int index, object? expectedRow = null)
        {
            PlaybackQueueItem item = inner.CapturePlaybackItem(index, expectedRow);
            ObserveResolution(index);
            return item;
        }
        public object GetRow(int index)
        {
            object row = inner.GetRow(index);
            ObserveResolution(index);
            return row;
        }
        private void ObserveResolution(int index)
        {
            if (index == gatedIndex && Interlocked.Exchange(ref claimed, 1) == 0)
            {
                entered.TrySetResult();
                release.Wait();
                Interlocked.Exchange(ref rowResolved, 1);
            }
        }
    }

    /// <summary>起動時の行と現在slotが違えば開始を捨て、同じslotに対象が戻った場合だけその譜面を再生することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_RowActivationDropsRequestWhenQueueSlotChanged()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string expectedPath = workspace.GetPath("expectedPath.tmp");
        File.WriteAllBytes(expectedPath, []);
        string replacementPath = workspace.GetPath("replacementPath.tmp");
        File.WriteAllBytes(replacementPath, []);
        ChartFile expectedRow = (ChartTestValues.Empty() with { Path = expectedPath });
        ChartFile replacementRow = (ChartTestValues.Empty() with { Path = replacementPath });
        var player = new FakeBmsPlayer();
        var chartList = new MainChartListViewModel
        {
            Rows = new List<object> { replacementRow },
            SelectedIndex = 0
        };
        var panel = new PlaybackPanelViewModel(
            player,
            new ImmediatePlaybackUiDispatcher(),
            new MainChartListPlaybackQueue(chartList),
            new SettingsPlaybackSettingsStore(() => testSettings),
            new FakePlaybackDialogService(),
            _ => { },
            new ChartFileOperationSynchronizer(), temporaryPreviewHold: PlaybackPanelTestSupport.ShortTemporaryPreviewHold);
        try
        {
            panel.ExecuteTableRowActivation(0, expectedRow).GetAwaiter().GetResult();

            Assert.IsFalse(player.Commands.Any(command => command.StartsWith("PlayStart:", StringComparison.Ordinal)));
            Assert.IsNull(panel.NowPlayingChart);

            chartList.Rows[0] = expectedRow;
            panel.ExecuteTableRowActivation(0, expectedRow).GetAwaiter().GetResult();

            Assert.IsNotNull(panel.NowPlayingChart);
            Assert.AreEqual(expectedPath, panel.NowPlayingChart.Path);
            CollectionAssert.Contains(player.Commands.ToArray(), "PlayStart:" + expectedPath);
        }
        finally
        {
            File.Delete(expectedPath);
            File.Delete(replacementPath);
        }
    }

    /// <summary>表示面の利用可否に従って選択・回転を制御し、選べない面で保存状態を変えず小型指定を維持することを確認します。</summary>
    [TestMethod]
    public void PlaybackPanel_OwnsPanelStateTransitionsUsingViewHostAvailability()
    {
        PlayerPanelState originalPanelState = testSettings.PlayerPanelState;
        PlaybackPanelViewModel panel = CreatePanel(testSettings, new FakeBmsPlayer());
        try
        {
            panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;

            Assert.IsTrue(panel.CanSelectPanelState(PlayerPanelState.TITLE_LARGE, false));
            Assert.IsFalse(panel.CanSelectPanelState(PlayerPanelState.BMS_PLAYER, false));
            Assert.IsFalse(panel.TrySelectPanelState(PlayerPanelState.BMS_PLAYER, false));
            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL;
            panel.RotatePanelState(bmsPlayerSurfaceAvailable: true);
            Assert.AreEqual(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_LARGE;
            panel.RotatePanelState(bmsPlayerSurfaceAvailable: true);
            Assert.AreEqual(PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);

            panel.RotatePanelState(bmsPlayerSurfaceAvailable: false);
            Assert.AreEqual(PlayerPanelState.TITLE_LARGE, panel.PlayerPanelState);

            panel.PlayerPanelState = PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER;
            panel.ToggleCompactPanel();
            Assert.AreEqual(PlayerPanelState.BMS_PLAYER, panel.PlayerPanelState);
        }
        finally
        {
            testSettings.PlayerPanelState = originalPanelState;
        }
    }

    /// <summary>利用不能なplayer面を画像へ解決する際にも小型フラグを保ち、利用可能な場合は要求された面をそのまま使うことを確認します。</summary>
    [TestMethod]
    public void PlaybackPanelViewResolvesUnavailableBmsSurfaceWithImageFallbackAndCompactPreserved()
    {
        Assert.AreEqual(
            PlayerPanelState.BMS_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.BMS_PLAYER, true));
        Assert.AreEqual(
            PlayerPanelState.TITLE_LARGE,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.BMS_PLAYER, false));
        Assert.AreEqual(
            PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, true));
        Assert.AreEqual(
            PlayerPanelState.TITLE_SMALL,
            PlaybackPanelView.ResolveSurfaceState(PlayerPanelState.TITLE_SMALL | PlayerPanelState.BMS_PLAYER, false));
    }

    /// <summary>利用者が指定した表示面・リピート・フォルダ送り・単曲モードを設定storeへ書き戻し、bindingと保存値の不一致を検出します。</summary>
    [TestMethod]
    public void PlaybackPanel_OwnsPersistedPanelAndPlaybackModeBindings()
    {
        PlayerPanelState originalPanelState = testSettings.PlayerPanelState;
        bool originalRepeat = testSettings.RepeatPlayMode;
        bool originalFolderSkip = testSettings.FolderSkipPlayMode;
        bool originalSingle = testSettings.SinglePlayMode;
        PlaybackPanelViewModel panel = CreatePanel(testSettings, new FakeBmsPlayer());
        try
        {
            panel.PlayerPanelState = PlayerPanelState.BMS_PLAYER | PlayerPanelState.TITLE_SMALL;
            panel.RepeatPlayMode = !originalRepeat;
            panel.FolderSkipPlayMode = !originalFolderSkip;
            panel.SinglePlayMode = !originalSingle;

            Assert.AreEqual(panel.PlayerPanelState, testSettings.PlayerPanelState);
            Assert.AreEqual(panel.RepeatPlayMode, testSettings.RepeatPlayMode);
            Assert.AreEqual(panel.FolderSkipPlayMode, testSettings.FolderSkipPlayMode);
            Assert.AreEqual(panel.SinglePlayMode, testSettings.SinglePlayMode);
        }
        finally
        {
            testSettings.PlayerPanelState = originalPanelState;
            testSettings.RepeatPlayMode = originalRepeat;
            testSettings.FolderSkipPlayMode = originalFolderSkip;
            testSettings.SinglePlayMode = originalSingle;
        }
    }

    /// <summary>画像設定通知では現在playerの能力を変えず、実playerの置換完了が能力bindingを更新します。</summary>
    [TestMethod]
    public async Task PlaybackPanel_RefreshesCapabilityBindingsAfterSettingsChange()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        bool workspaceDisposed = false;
        void DisposeWorkspace()
        {
            if (workspaceDisposed) { return; }
            workspaceDisposed = true;
            workspace.Dispose();
        }
        try
        {
            string directory = workspace.RootDirectory;
            bool originalExternalPanelImage = testSettings.UseExternalPanelImage;
            string originalStagefilePath = testSettings.StagefilePath;
            PlaybackPanelViewModel panel = CreatePanel(testSettings, CreateLr2Player(directory));
            var changed = new HashSet<string>(StringComparer.Ordinal);
            panel.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? string.Empty);
            string[] capabilities = ["CanSeek", "CanChangeHighSpeed", "CanShowInfo", "CanShowEffect", "CanChangePlayside", "UsesUbMplay", "UsesLr2Body", "UsesBmiIdxView"];
            try
            {
                testSettings.UseExternalPanelImage = !originalExternalPanelImage;
                testSettings.StagefilePath = "playback-panel-stage.png";
                panel.NotifySettingsChanged();
                Assert.IsFalse(panel.CanSeek);
                Assert.IsFalse(panel.CanChangeHighSpeed);
                Assert.IsFalse(panel.CanShowInfo);
                Assert.IsFalse(panel.CanShowEffect);
                Assert.IsFalse(panel.CanChangePlayside);
                Assert.IsTrue(panel.UsesLr2Body);
                Assert.AreEqual(!originalExternalPanelImage, panel.UseExternalPanelImage);
                Assert.AreEqual("playback-panel-stage.png", panel.StagefilePath);
                CollectionAssert.IsSubsetOf(new[] { "PlayerPanelState", "UseExternalPanelImage", "StagefilePath" }, changed.ToArray());
                Assert.IsFalse(capabilities.Any(changed.Contains), "画像通知にplayer能力通知を混在させません。");
                changed.Clear();
                await panel.ReplacePlayerAsync(CreateUbmPlayer(directory));
                Assert.IsTrue(panel.CanSeek);
                Assert.IsTrue(panel.CanChangeHighSpeed);
                Assert.IsTrue(panel.CanShowInfo);
                Assert.IsTrue(panel.CanShowEffect);
                Assert.IsTrue(panel.CanChangePlayside);
                Assert.IsTrue(panel.UsesUbMplay);
                CollectionAssert.IsSubsetOf(capabilities, changed.ToArray());
                await panel.ReplacePlayerAsync(CreateBmiPlayer(directory));
                Assert.IsTrue(panel.CanSeek);
                Assert.IsTrue(panel.CanChangeHighSpeed);
                Assert.IsFalse(panel.CanShowInfo);
                Assert.IsFalse(panel.CanShowEffect);
                Assert.IsFalse(panel.CanChangePlayside);
                Assert.IsTrue(panel.UsesBmiIdxView);
            }
            finally
            {
                panel.BeginShutdown();
                await panel.CloseForShutdown();
                testSettings.UseExternalPanelImage = originalExternalPanelImage;
                testSettings.StagefilePath = originalStagefilePath;
                DisposeWorkspace();
            }
        }
        finally { DisposeWorkspace(); }
    }

    private static uBMplay CreateUbmPlayer(string directory)
    {
        string executable = Path.Combine(directory, "uBMplay.exe");
        File.WriteAllBytes(executable, []);
        return new uBMplay(executable, new RecordingPlayerSettingsGateway(),
            new RecordingExternalPlayerProcessGateway());
    }

    private static LR2body CreateLr2Player(string directory, IExternalPlayerProcessGateway? processGateway = null)
    {
        string configPath = Path.Combine(directory, "config.xml");
        File.WriteAllText(configPath, "<config><system><windowsize_x>800</windowsize_x><windowsize_y>600</windowsize_y><screenmode>1</screenmode></system><sound><volumemaster>100</volumemaster></sound><jukebox /></config>");
        string executable = Path.Combine(directory, "LR2body.exe");
        File.WriteAllBytes(executable, []);
        var player = new LR2body(executable, new Lr2ConfigOwner(new LR2Config(configPath)),
            new RecordingPlayerSettingsGateway(),
            processGateway ?? new RecordingExternalPlayerProcessGateway());
        ((IExternalWindowPlayer)player).AttachWindowHost(new RecordingExternalPlayerWindowHost(default));
        return player;
    }

    private static ChartFile CreatePlaybackTargetChart(ChartFileKind kind, string path)
    {
        return new ChartFile(kind, path, "playback-port-hash", null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    /// <summary>要求が待つUI配送を通知ごとに進め、配送に依存するTaskを先に待たず実終端へ合流します。</summary>
    private static async Task CompleteQueuedPlaybackUiAsync(Task operation, QueuedPlaybackUiDispatcher dispatcher)
    {
        while (!operation.IsCompleted)
        {
            await Task.WhenAny(operation, dispatcher.WaitForPendingActionsAsync(1));
            dispatcher.RunAll();
        }
        dispatcher.RunAll();
        await operation;
    }
}
