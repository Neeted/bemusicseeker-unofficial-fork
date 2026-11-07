using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Windows.Tests.Infrastructure;

[TestClass]
public sealed class DispatcherHostTests
{
    /// <summary>既終端と受理後終了の要求が、停止の二次失敗を含め元例外とthread回収を保持します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void OwnedDispatcher_HostFailureEndsAcceptedAndLaterRequests(bool requestAfterEnd, bool shutdownFails)
    {
        using var ready = new ManualResetEventSlim();
        using var fatalEntered = new ManualResetEventSlim();
        using var releaseFatal = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var original = new InvalidOperationException("host failure");
        var secondary = new InvalidOperationException("shutdown failure");
        Dispatcher? dispatcher = null;
        Exception? terminalFailure = null;
        Task? request = null;
        DispatcherOperation? accepted = null;
        int actionCalls = 0;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            if (shutdownFails) { dispatcher.ShutdownStarted += (_, _) => throw secondary; }
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                fatalEntered.Set();
                releaseFatal.Wait();
                throw original;
            }));
            ready.Set();
            try { Dispatcher.Run(); }
            catch (Exception exception) { terminalFailure = exception; }
            finally
            {
                TestUiDispatcherHost.CompleteOwnedDispatcher(dispatcher, completed, exception =>
                {
                    if (terminalFailure == null) { terminalFailure = exception; }
                    else { terminalFailure.Data["TestDispatcherShutdownFailure"] = exception.ToString(); }
                });
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            ready.Wait();
            fatalEntered.Wait();
            Dispatcher target = dispatcher ?? throw new AssertFailedException("The owned dispatcher was not created.");
            if (requestAfterEnd)
            {
                releaseFatal.Set();
                completed.Wait();
                Assert.AreSame(original, Assert.ThrowsExactly<InvalidOperationException>(() =>
                    TestUiDispatcherHost.InvokeOnOwnedDispatcher(target, thread, completed, () => terminalFailure,
                        () => actionCalls++)));
            }
            else
            {
                var registered = new TaskCompletionSource<DispatcherOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherHookEventHandler posted = (_, args) =>
                {
                    if (args.Operation.Priority == DispatcherPriority.Send) { registered.TrySetResult(args.Operation); }
                };
                target.Hooks.OperationPosted += posted;
                try
                {
                    request = Task.Run(() => TestUiDispatcherHost.InvokeOnOwnedDispatcher(target, thread, completed,
                        () => terminalFailure, () => actionCalls++));
                    Task.WhenAny(registered.Task, request).GetAwaiter().GetResult();
                    if (request.IsCompleted) { request.GetAwaiter().GetResult(); Assert.Fail("The request ended before its actual registration."); }
                    accepted = registered.Task.GetAwaiter().GetResult();
                    releaseFatal.Set();
                    Assert.AreSame(original, Assert.ThrowsExactly<InvalidOperationException>(() => request.GetAwaiter().GetResult()));
                }
                finally { target.Hooks.OperationPosted -= posted; }
            }
            Assert.IsFalse(thread.IsAlive, "The target recovery must join its thread before returning the failure.");
            Assert.AreEqual(0, actionCalls);
            Assert.IsTrue(completed.IsSet);
            if (accepted != null) { Assert.IsTrue(accepted.Task.IsCompleted); }
            if (shutdownFails)
            {
                string detail = original.Data["TestDispatcherShutdownFailure"] as string
                    ?? throw new AssertFailedException("The secondary shutdown failure was not preserved.");
                StringAssert.Contains(detail, secondary.Message);
            }
            Assert.AreSame(original, Assert.ThrowsExactly<InvalidOperationException>(() =>
                TestUiDispatcherHost.JoinOwnedDispatcher(thread, completed, () => terminalFailure)));
            Assert.IsFalse(thread.IsAlive);
        }
        finally
        {
            releaseFatal.Set();
            completed.Wait();
            thread.Join();
            if (request != null)
            {
                try { request.GetAwaiter().GetResult(); }
                catch (Exception exception) when (ReferenceEquals(exception, original)) { }
            }
        }
    }

    /// <summary>呼出し本体の例外を同じ例外として返し、その後の呼出しも実行できることから、一要求の失敗で共有Dispatcherを破壊する誤りを検出します。</summary>
    [TestMethod]
    public void Invoke_ActionFailurePreservesHealthyHostAndFollowingRequest()
    {
        var original = new InvalidOperationException("action failure");
        Assert.AreSame(original, Assert.ThrowsExactly<InvalidOperationException>(() => TestUiDispatcherHost.Invoke(() => throw original)));
        bool followingCompleted = false;
        TestUiDispatcherHost.Invoke(() => followingCompleted = true);
        Assert.IsTrue(followingCompleted);
    }

    /// <summary>正常なDispatcher停止の回収から戻った時点で専有STAスレッドが終了していることを確認し、終了通知だけで回収を終える誤りを検出します。</summary>
    [TestMethod]
    public void OwnedDispatcher_NormalRecoveryJoinsBeforeReturning()
    {
        using var ready = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? terminalFailure = null;
        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            try { Dispatcher.Run(); }
            catch (Exception exception) { terminalFailure = exception; }
            finally { TestUiDispatcherHost.CompleteOwnedDispatcher(dispatcher, completed, exception => terminalFailure ??= exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            ready.Wait();
            (dispatcher ?? throw new AssertFailedException("The owned dispatcher was not created.")).BeginInvokeShutdown(DispatcherPriority.Send);
            TestUiDispatcherHost.JoinOwnedDispatcher(thread, completed, () => terminalFailure);
            Assert.IsFalse(thread.IsAlive);
            Assert.IsTrue(completed.IsSet);
            Assert.IsNull(terminalFailure);
        }
        finally
        {
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
            completed.Wait();
            thread.Join();
        }
    }

    /// <summary>通常Taskの待機が予約consumerの成功・失敗・取消をそのまま観測します。</summary>
    [TestMethod]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("canceled")]
    [DataRow("producer_failure")]
    [DataRow("producer_canceled")]
    [DataRow("missing")]
    public void AwaitTaskOnDispatcher_ObservesScheduledConsumerTerminal(string outcome)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new InvalidOperationException("consumer failed");
            DispatcherOperation producer = TestUiDispatcherHost.Dispatcher.InvokeAsync(new Action(() =>
            {
                switch (outcome)
                {
                    case "success": completion.TrySetResult(); break;
                    case "failure": completion.TrySetException(failure); break;
                    case "canceled": completion.TrySetCanceled(); break;
                    case "producer_failure": throw failure;
                }
            }), DispatcherPriority.Background, outcome == "producer_canceled" ? new CancellationToken(canceled: true) : CancellationToken.None);
            Task observation = TestUiDispatcherHost.AwaitNotificationAsync(completion.Task, producer.Task, outcome);
            try
            {
                switch (outcome)
                {
                    case "success": TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome); break;
                    case "failure":
                    case "producer_failure":
                        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                            TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome)));
                        break;
                    case "canceled":
                    case "producer_canceled":
                        Assert.ThrowsExactly<TaskCanceledException>(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome));
                        if (outcome == "producer_canceled") { Assert.IsTrue(producer.Task.IsCanceled); }
                        break;
                    case "missing":
                        Assert.ThrowsExactly<InvalidOperationException>(() => TestUiDispatcherHost.AwaitTaskOnDispatcher(observation, outcome));
                        break;
                }
            }
            finally
            {
                try { TestUiDispatcherHost.AwaitTaskOnDispatcher(producer.Task, "consumer-test-producer-terminal"); }
                catch (InvalidOperationException exception) when (outcome == "producer_failure" && ReferenceEquals(exception, failure)) { }
                catch (TaskCanceledException) when (outcome == "producer_canceled") { }
            }
        });
    }

    /// <summary>連続した呼出しが同じApplicationとSTAのDispatcherスレッドへ届くことを確認し、要求ごとに別のWPF環境を作る誤りを検出します。</summary>
    [TestMethod]
    public void Invoke_ReusesSingleApplicationDispatcherAndStaThread()
    {
        Application? firstApplication = null;
        Dispatcher? firstDispatcher = null;
        Thread? firstThread = null;
        ApartmentState firstApartmentState = ApartmentState.Unknown;
        TestUiDispatcherHost.Invoke(() =>
        {
            firstApplication = Application.Current;
            firstDispatcher = Dispatcher.CurrentDispatcher;
            firstThread = Thread.CurrentThread;
            firstApartmentState = Thread.CurrentThread.GetApartmentState();
        });

        Application? secondApplication = null;
        Dispatcher? secondDispatcher = null;
        Thread? secondThread = null;
        TestUiDispatcherHost.Invoke(() =>
        {
            secondApplication = Application.Current;
            secondDispatcher = Dispatcher.CurrentDispatcher;
            secondThread = Thread.CurrentThread;
        });

        Assert.IsNotNull(firstApplication);
        Assert.AreSame(firstApplication, secondApplication);
        Assert.AreSame(firstDispatcher, secondDispatcher);
        Assert.AreSame(TestUiDispatcherHost.Dispatcher, firstDispatcher);
        Assert.AreSame(firstThread, secondThread);
        Assert.AreEqual(ApartmentState.STA, firstApartmentState);
    }

    /// <summary>
    /// UIスレッド上で、先に積んだNormal・Backgroundの予約と、Backgroundの予約が消化中に積んだNormalの予約が、
    /// 積んだ順で戻る前に実行済みであり、先に積んだContextIdleの予約は未実行のままであることを確認します。
    /// Loadedまでしか進めない誤りと、idle優先度まで処理する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ProcessQueuedBackground_DrainsThroughBackgroundOnUiThreadWithoutIdle()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
            var order = new List<string>();
            DispatcherOperation? idle = null;
            try
            {
                idle = dispatcher.InvokeAsync(() => order.Add("idle"), DispatcherPriority.ContextIdle);
                dispatcher.InvokeAsync(() => order.Add("background-1"), DispatcherPriority.Background);
                dispatcher.InvokeAsync(() =>
                {
                    order.Add("background-2");
                    dispatcher.InvokeAsync(() => order.Add("normal-from-background"), DispatcherPriority.Normal);
                }, DispatcherPriority.Background);
                dispatcher.InvokeAsync(() => order.Add("normal"), DispatcherPriority.Normal);

                TestUiDispatcherHost.ProcessQueuedBackground();

                CollectionAssert.AreEqual(
                    new[] { "normal", "background-1", "background-2", "normal-from-background" }, order,
                    "Background以上の予約は優先度順・同優先度内FIFOで戻る前に実行済みです。");
                Assert.AreEqual(DispatcherOperationStatus.Pending, idle.Status, "ContextIdleの予約は境界に含まれません。");
            }
            finally
            {
                idle?.Abort();
            }
        });
    }

    /// <summary>テストスレッドから呼んでも、呼出し前に積まれたBackgroundの予約が積んだ順で戻る前に実行済みであることを確認します。UIスレッドの分岐で待たない誤りの決定的な検出はUIスレッド版が担います。テストスレッドの分岐で待たない誤りは、既存の終端待ちの経路で検出され、このテストの観測だけではDispatcherスレッドの進行次第で確率的です。</summary>
    [TestMethod]
    public void ProcessQueuedBackground_DrainsPriorBackgroundFromTestThread()
    {
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var order = new List<string>();
        dispatcher.InvokeAsync(() => order.Add("background-1"), DispatcherPriority.Background);
        dispatcher.InvokeAsync(() =>
        {
            order.Add("background-2");
            dispatcher.InvokeAsync(() => order.Add("normal-from-background"), DispatcherPriority.Normal);
        }, DispatcherPriority.Background);

        TestUiDispatcherHost.ProcessQueuedBackground();

        string[] observed = [];
        TestUiDispatcherHost.Invoke(() => observed = order.ToArray());
        CollectionAssert.AreEqual(new[] { "background-1", "background-2", "normal-from-background" }, observed);
    }
}
