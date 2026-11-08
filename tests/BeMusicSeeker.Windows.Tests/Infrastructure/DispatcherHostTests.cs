using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using NLog;
using NLog.Config;

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

    /// <summary>
    /// テストスレッドから積んだBeginInvokeの例外が、ホストを終端させず現在ケースの記録として残り、
    /// 後続の要求が受理されることを確認します。ハンドラーがなくホストが終端する誤りと、記録を失う誤りを検出します。
    /// </summary>
    [TestMethod]
    public void UnhandledDispatcherException_IsRecordedForCurrentCaseAndHostContinues()
    {
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var original = new InvalidOperationException("unhandled from BeginInvoke");
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => throw original));
        // 同じ優先度のFIFOで、先に積んだ例外の予約が処理された後に完了する。
        dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.GetAwaiter().GetResult();

        IReadOnlyList<Exception> taken = TestUiDispatcherHost.TakeUnhandledExceptions();
        Assert.AreEqual(1, taken.Count);
        Assert.AreSame(original, taken[0]);
        Assert.AreEqual(0, TestUiDispatcherHost.TakeUnhandledExceptions().Count, "受け取った記録は再度は受け取れません。");

        bool followingCompleted = false;
        TestUiDispatcherHost.Invoke(() => followingCompleted = true);
        Assert.IsTrue(followingCompleted);
    }

    /// <summary>
    /// 現在ケースの複数の記録が発生順に受け取れ、一度受け取ると空になることを確認します。順序を失う誤りと二重に残す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void TakeUnhandledExceptions_ReturnsCurrentCaseRecordsInOrderOnce()
    {
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var first = new InvalidOperationException("first");
        var second = new ArgumentException("second");
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => throw first));
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => throw second));
        dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.GetAwaiter().GetResult();

        IReadOnlyList<Exception> taken = TestUiDispatcherHost.TakeUnhandledExceptions();
        Assert.AreEqual(2, taken.Count);
        Assert.AreSame(first, taken[0]);
        Assert.AreSame(second, taken[1]);
        Assert.AreEqual(0, TestUiDispatcherHost.TakeUnhandledExceptions().Count);
    }

    /// <summary>
    /// 待機・消化APIの呼出し中に例外を投げる予約が実行されると、そのAPIが通知を待たずに元の例外で失敗し、
    /// 記録は報告済みとして残らないことを確認します。待ち続ける誤りと、失敗の後に記録を残して再報告する誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow("AwaitTaskOnDispatcher")]
    [DataRow("AwaitPresentationOnDispatcher")]
    [DataRow("ProcessQueuedPresentation")]
    [DataRow("ProcessQueuedBackground")]
    public void WaitingApi_FailsWithOriginalUnhandledExceptionAndConsumesRecord(string api)
    {
        var original = new InvalidOperationException("unhandled during " + api);
        var neverCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                TestUiDispatcherHost.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => throw original));
                Action call = api switch
                {
                    "AwaitTaskOnDispatcher" => () => TestUiDispatcherHost.AwaitTaskOnDispatcher(neverCompleted.Task, "never-completed"),
                    "AwaitPresentationOnDispatcher" => () => TestUiDispatcherHost.AwaitPresentationOnDispatcher(neverCompleted.Task, "never-completed"),
                    "ProcessQueuedPresentation" => TestUiDispatcherHost.ProcessQueuedPresentation,
                    _ => TestUiDispatcherHost.ProcessQueuedBackground,
                };
                Assert.AreSame(original, Assert.ThrowsExactly<InvalidOperationException>(call));
                Assert.IsFalse(neverCompleted.Task.IsCompleted, "待機対象の通知を待たずに失敗します。");
            });
            Assert.AreEqual(0, TestUiDispatcherHost.TakeUnhandledExceptions().Count, "即座の失敗で報告済みとして消費されます。");
        }
        finally { neverCompleted.TrySetResult(); }
    }

    /// <summary>
    /// Invoke・InvokeAsyncの失敗が呼出し側へ返り、未処理例外の記録には残らないことを確認します。二重に記録する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CallerReturnedFailures_AreNotRecordedAsUnhandled()
    {
        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var invokeFailure = new InvalidOperationException("Invoke");
        var asyncFailure = new InvalidOperationException("InvokeAsync");
        var nestedFailure = new InvalidOperationException("nested Invoke");
        Assert.AreSame(invokeFailure, Assert.ThrowsExactly<InvalidOperationException>(() => TestUiDispatcherHost.Invoke(() => throw invokeFailure)));
        DispatcherOperation operation = dispatcher.InvokeAsync(() => throw asyncFailure, DispatcherPriority.Normal);
        Assert.AreSame(asyncFailure, Assert.ThrowsExactly<InvalidOperationException>(() => operation.Task.GetAwaiter().GetResult()));
        TestUiDispatcherHost.Invoke(() =>
            Assert.AreSame(nestedFailure, Assert.ThrowsExactly<InvalidOperationException>(() => dispatcher.Invoke(() => throw nestedFailure))));
        // 失敗した予約の後に積んだ予約の完了で、記録が遅れて届く可能性を除く。
        dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.GetAwaiter().GetResult();
        TestUiDispatcherHost.ProcessQueuedBackground();

        Assert.AreEqual(0, TestUiDispatcherHost.TakeUnhandledExceptions().Count);
    }

    /// <summary>
    /// 未処理例外がなければ表明は何もせず、あれば元の例外の型・メッセージを失敗の診断に含めて記録を消費することを確認します。
    /// 内容を落とす実装と、記録を残して再報告させる実装を検出します。
    /// </summary>
    [TestMethod]
    public void AssertNoUnhandledExceptions_PassesWhenEmptyAndKeepsContentWhenRecorded()
    {
        TestUiDispatcherHost.AssertNoUnhandledExceptions("empty");

        Dispatcher dispatcher = TestUiDispatcherHost.Dispatcher;
        var original = new ArgumentOutOfRangeException("probe", "distinct-original-message");
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => throw original));
        dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal).Task.GetAwaiter().GetResult();

        AggregateException failure = Assert.ThrowsExactly<AggregateException>(
            () => TestUiDispatcherHost.AssertNoUnhandledExceptions("recorded-context"));
        Assert.AreSame(original, failure.InnerExceptions.Single());
        StringAssert.Contains(failure.Message, "recorded-context");
        StringAssert.Contains(failure.Message, nameof(ArgumentOutOfRangeException));
        StringAssert.Contains(failure.Message, "distinct-original-message");
        Assert.AreEqual(0, TestUiDispatcherHost.TakeUnhandledExceptions().Count, "失敗に内容が残るため、記録は消費されます。");
    }

    /// <summary>
    /// 閉じないWindowがケース終了時の残留として一度だけ報告され、MainWindowに設定しても同じWindowの一つの記録になり、閉じると報告が消えることを確認します。
    /// 記録しない実装、二重に報告する実装、MainWindowを別の記録にする実装を検出します。報告済みの印が基準へ戻ると消える保証は、新しいWindowでは確かめられないため、
    /// <see cref="HostResidue_BaselineWindowBecomingMainWindow_IsReportedEachTimeItBecomesResidue"/> が担います。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void HostResidue_UnclosedWindowIsReportedOnce_AndMainWindowIsTheSameRecord()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Window? previousMain = application.MainWindow;
            Window? plain = null;
            Window? main = null;
            bool plainClosed = false;
            bool mainClosed = false;
            try
            {
                // 前のケースが残したMainWindowに依存しないよう、基準の前にnullへ置き、作ったWindowを明示的にMainWindowへ設定します。
                application.MainWindow = null;
                var state = new HostResidueState(HostResidueBoundary.Observe());
                Assert.IsEmpty(Residues(state, "E-a", "E-b"), "基準と同じ状態では何も報告されません。");

                plain = new Window { Title = "residue-plain" };
                plain.Closed += (_, _) => plainClosed = true;
                application.MainWindow = plain;
                IReadOnlyList<HostResidue> first = Residues(state, "E-a", "E-b");
                Assert.HasCount(1, first);
                Assert.AreEqual("E-a", first[0].Category);
                StringAssert.Contains(first[0].Detail, "residue-plain");
                StringAssert.Contains(first[0].Detail, "Application.MainWindow=yes");
                Assert.IsEmpty(Residues(state, "E-a", "E-b"), "同じ残留は二度目の比較で報告されません。");

                main = new Window { Title = "residue-second" };
                main.Closed += (_, _) => mainClosed = true;
                IReadOnlyList<HostResidue> second = Residues(state, "E-a", "E-b");
                Assert.HasCount(1, second);
                StringAssert.Contains(second[0].Detail, "residue-second");
                StringAssert.Contains(second[0].Detail, "Application.MainWindow=no");
                application.MainWindow = main;
                Assert.IsEmpty(Residues(state, "E-a", "E-b"), "MainWindowへ設定しても同じWindowは再び報告されません。");

                main.Close();
                plain.Close();
                application.MainWindow = null;
                Assert.IsEmpty(Residues(state, "E-a", "E-b"), "閉じて元へ戻した状態では何も報告されません。");

                plain = new Window { Title = "residue-again" };
                plainClosed = false;
                plain.Closed += (_, _) => plainClosed = true;
                Assert.HasCount(1, Residues(state, "E-a", "E-b"));
            }
            finally
            {
                application.MainWindow = previousMain;
                if (main != null && !mainClosed) { main.Close(); }
                if (plain != null && !plainClosed) { plain.Close(); }
            }
        });
    }

    private const uint WsPopup = 0x80000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    /// <summary>
    /// WPFのWindowに対応しないトップレベルHWNDが、HWNDのみの残留として一度だけ報告され、破棄すると消えることを確認します。
    /// 記録の分類が<c>other</c>であることも確認します。このHWNDはクラス名が<c>HwndWrapper[</c>で始まり非表示ですが、HwndSourceを持つため、
    /// 分類の入力のうち実際の収集（HwndSourceの有無）を読まずにwpf-internalと分類する誤りを検出する役割を持ちます（分類の規則自体は
    /// <see cref="HwndClassification_FollowsClassNameHwndSourceAndVisibility"/> が確認します）。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void HostResidue_HwndWithoutWindowIsReportedOnce()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            HwndSource? source = null;
            try
            {
                var state = new HostResidueState(HostResidueBoundary.Observe());
                // 画面に見えない・前面に出ないよう、WS_VISIBLEなしの非表示のポップアップを、WS_EX_NOACTIVATEで非アクティブ・タスクバー非表示にします。位置は補助として画面の外側寄りに置きます。
                var parameters = new HwndSourceParameters("residue-hwnd", 1, 1)
                {
                    WindowStyle = unchecked((int)WsPopup),
                    ExtendedWindowStyle = WsExToolWindow | WsExNoActivate
                };
                parameters.SetPosition(
                    (int)(SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2048d),
                    (int)(SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2048d));
                source = new HwndSource(parameters);
                Assert.IsFalse(IsWindowVisible(source.Handle), "観測用のHWNDは画面に表示されません。");
                IReadOnlyList<HostResidue> first = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
                string handle = $"hwnd=0x{source.Handle:X},";
                // WPFが最初のHwndSourceに伴って内部のHWNDを遅延生成する場合があるため、このテストのHWNDだけを数えます。
                Assert.AreEqual(1, first.Count(residue => residue.Category == "E-a-hwnd" && residue.Detail.Contains(handle)));
                StringAssert.Contains(first.Single(residue => residue.Category == "E-a-hwnd" && residue.Detail.Contains(handle)).Detail, "kind=other");
                Assert.IsEmpty(HostResidueComparison.Compare(state, HostResidueBoundary.Observe()).Where(residue => residue.Detail.Contains(handle)));

                source.Dispose();
                source = null;
                Assert.IsEmpty(HostResidueComparison.Compare(state, HostResidueBoundary.Observe()).Where(residue => residue.Detail.Contains(handle)));
            }
            finally { source?.Dispose(); }
        });
    }

    /// <summary>
    /// 文化圏・<c>Resources["vm"]</c>が基準からずれると報告され、同じ値では二度目を報告せず、別のオブジェクトへ替えると再び報告し、
    /// 基準へ戻すと印が消えて後のずれで再び報告されることを確認します。独立した状態を使い、finallyで元へ戻します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void HostResidue_CultureAndViewModelResource_AreReportedPerDistinctValueAndClearedWhenRestored()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            CultureInfo? originalCulture = Resources.Culture;
            bool hadViewModel = application.Resources.Contains("vm");
            object? originalViewModel = application.Resources["vm"];
            try
            {
                var state = new HostResidueState(HostResidueBoundary.Observe());
                // 前のケースの残留に依存しないよう、基準と異なる文化圏を選びます。
                var shifted = CultureInfo.GetCultureInfo(originalCulture?.Name == "fr-FR" ? "en-US" : "fr-FR");

                Resources.Culture = shifted;
                IReadOnlyList<HostResidue> culture = Residues(state, "E-d");
                Assert.HasCount(1, culture);
                Assert.AreEqual("E-d", culture[0].Category);
                StringAssert.Contains(culture[0].Detail, shifted.Name);
                Assert.IsEmpty(Residues(state, "E-d"));
                Resources.Culture = originalCulture;
                Assert.IsEmpty(Residues(state, "E-d"), "基準へ戻した状態では何も報告されません。");
                Resources.Culture = shifted;
                Assert.HasCount(1, Residues(state, "E-d"));
                Resources.Culture = originalCulture;
                Assert.IsEmpty(Residues(state, "E-d"));

                object firstViewModel = new object();
                object secondViewModel = new object();
                application.Resources["vm"] = firstViewModel;
                IReadOnlyList<HostResidue> first = Residues(state, "E-c");
                Assert.HasCount(1, first);
                Assert.AreEqual("E-c", first[0].Category);
                Assert.IsEmpty(Residues(state, "E-c"));
                application.Resources["vm"] = secondViewModel;
                Assert.HasCount(1, Residues(state, "E-c"));
                application.Resources["vm"] = firstViewModel;
                Assert.HasCount(1, Residues(state, "E-c"), "別のオブジェクトを挟んで最初のオブジェクトへ戻すと、再び報告されます。");
                RestoreViewModel(application, hadViewModel, originalViewModel);
                Assert.IsEmpty(Residues(state, "E-c"), "元のオブジェクトへ戻した状態では何も報告されません。");
                application.Resources["vm"] = firstViewModel;
                Assert.HasCount(1, Residues(state, "E-c"), "基準へ戻して印が消えた後は、同じオブジェクトでも再び報告されます。");
            }
            finally
            {
                Resources.Culture = originalCulture;
                RestoreViewModel(application, hadViewModel, originalViewModel);
            }
        });
    }

    /// <summary>
    /// 基準のWindowがMainWindowとして残留になる・基準へ戻る・再び残留になる変化が、そのたびに報告されることと、
    /// Application.Windowsに含まれないMainWindowが <c>E-b</c> になることを確認します。残留でなくなった印を消さない実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void HostResidue_BaselineWindowBecomingMainWindow_IsReportedEachTimeItBecomesResidue()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Window? previousMain = application.MainWindow;
            Window? baselineWindow = null;
            Window? orphan = null;
            bool baselineClosed = false;
            try
            {
                baselineWindow = new Window { Title = "residue-baseline" };
                baselineWindow.Closed += (_, _) => baselineClosed = true;
                orphan = new Window { Title = "residue-orphan" };
                orphan.Close();
                application.MainWindow = null;
                var state = new HostResidueState(HostResidueBoundary.Observe());
                Assert.IsEmpty(Residues(state, "E-a", "E-b"));

                application.MainWindow = baselineWindow;
                IReadOnlyList<HostResidue> first = Residues(state, "E-a", "E-b");
                Assert.HasCount(1, first);
                StringAssert.Contains(first[0].Detail, "residue-baseline");
                StringAssert.Contains(first[0].Detail, "Application.MainWindow=yes");
                Assert.IsEmpty(Residues(state, "E-a", "E-b"));

                application.MainWindow = null;
                Assert.IsEmpty(Residues(state, "E-a", "E-b"), "基準へ戻した状態では何も報告されません。");
                application.MainWindow = baselineWindow;
                Assert.HasCount(1, Residues(state, "E-a", "E-b"));

                application.MainWindow = orphan;
                IReadOnlyList<HostResidue> unlisted = Residues(state, "E-a", "E-b");
                Assert.HasCount(1, unlisted);
                Assert.AreEqual("E-b", unlisted[0].Category);
                StringAssert.Contains(unlisted[0].Detail, "in Application.Windows=no");
            }
            finally
            {
                application.MainWindow = previousMain;
                if (baselineWindow != null && !baselineClosed) { baselineWindow.Close(); }
            }
        });
    }

    /// <summary>基準と比べた報告のうち、指定の項目だけを返します。他の由来で混ざりうる項目（E-gなど）に表明を依存させません。</summary>
    private static IReadOnlyList<HostResidue> Residues(HostResidueState state, params string[] categories)
        => [.. HostResidueComparison.Compare(state, HostResidueBoundary.Observe()).Where(residue => categories.Contains(residue.Category))];

    /// <summary>未消化操作を、このテストが投稿した操作だけに絞って比べます。他スレッド由来の操作が混ざっても結果が変わりません。</summary>
    private static IReadOnlyList<HostResidue> OwnPendingResidues(HostResidueState state, params DispatcherOperation?[] own)
    {
        HostResidueSnapshot snapshot = HostResidueBoundary.Observe();
        return HostResidueComparison.Compare(state, snapshot with
        {
            PendingOperations = [.. snapshot.PendingOperations.Where(operation => own.Any(candidate => ReferenceEquals(candidate, operation)))]
        });
    }

    private static void RestoreViewModel(Application application, bool hadViewModel, object? originalViewModel)
    {
        if (hadViewModel) { application.Resources["vm"] = originalViewModel; }
        else { application.Resources.Remove("vm"); }
    }

    /// <summary>
    /// NLog構成の同一性の比較を、実際の<c>LogManager.Configuration</c>へ触れずに観測値で確認します。
    /// nullから非nullへの変化は基準として採用し、別のオブジェクトは一度だけ報告し、基準へ戻すと印が消えることを区別します。
    /// </summary>
    [TestMethod]
    public void HostResidue_LogConfiguration_AdoptsFirstNonNullAndReportsOtherObjectsOnce()
    {
        var first = new LoggingConfiguration();
        var other = new LoggingConfiguration();
        var empty = new HostResidueSnapshot([], null, null, "ja-JP", "Light", null, [], []);
        var state = new HostResidueState(empty);

        Assert.IsEmpty(HostResidueComparison.Compare(state, empty), "nullの間は何も報告されません。");
        Assert.IsEmpty(HostResidueComparison.Compare(state, empty with { LogConfiguration = first }), "最初の非nullは基準として採用されます。");
        IReadOnlyList<HostResidue> replaced = HostResidueComparison.Compare(state, empty with { LogConfiguration = other });
        Assert.HasCount(1, replaced);
        Assert.AreEqual("E-f", replaced[0].Category);
        Assert.IsEmpty(HostResidueComparison.Compare(state, empty with { LogConfiguration = other }));
        Assert.IsEmpty(HostResidueComparison.Compare(state, empty with { LogConfiguration = first }), "基準のオブジェクトへ戻した状態では何も報告されません。");
        Assert.HasCount(1, HostResidueComparison.Compare(state, empty with { LogConfiguration = other }));
        Assert.HasCount(1, HostResidueComparison.Compare(state, empty with { LogConfiguration = null }), "基準の後にnullへ戻った構成も差として報告されます。");
    }

    /// <summary>
    /// Background以上で未消化のDispatcher操作が一度だけ報告され、Abortで消え、ContextIdle以下は数えないことを確認します。
    /// 未消化の操作を数えない実装、二重に報告する実装、idle優先度まで数える実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void HostResidue_PendingOperationsAtOrAboveBackground_AreReportedOnce()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Dispatcher dispatcher = Application.Current.Dispatcher;
            DispatcherOperation? normal = null;
            DispatcherOperation? background = null;
            DispatcherOperation? idle = null;
            try
            {
                var state = new HostResidueState(HostResidueBoundary.Observe());
                idle = dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                Assert.IsEmpty(OwnPendingResidues(state, idle), "ContextIdle以下は数えません。");

                normal = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal);
                IReadOnlyList<HostResidue> first = OwnPendingResidues(state, idle, normal);
                Assert.HasCount(1, first);
                Assert.AreEqual("E-g", first[0].Category);
                StringAssert.Contains(first[0].Detail, "Normal=1");
                Assert.IsEmpty(OwnPendingResidues(state, idle, normal));

                background = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                IReadOnlyList<HostResidue> added = OwnPendingResidues(state, idle, normal, background);
                Assert.HasCount(1, added);
                StringAssert.Contains(added[0].Detail, "Background=1");
                StringAssert.Contains(added[0].Detail, "1 pending");

                normal.Abort();
                background.Abort();
                Assert.IsEmpty(OwnPendingResidues(state, idle, normal, background), "消化または取消した状態では何も報告されません。");
            }
            finally
            {
                normal?.Abort();
                background?.Abort();
                idle?.Abort();
            }
        });
    }

    /// <summary>
    /// 失敗にする項目はB・E-a〜E-f・Dの八つだけで、記録だけの項目（E-g、E-a-hwnd、X-detector）は失敗にならないことを確認します。
    /// 一部の項目だけを昇格させる実装、Dを加え忘れる実装、E-gを昇格させる実装、前方一致で<c>E-a-hwnd</c>や<c>D-x</c>まで昇格させる実装、
    /// 大文字小文字を無視して<c>d</c>まで昇格させる実装を検出します。独立の状態で確認し、境界の記録は作りません。
    /// </summary>
    [TestMethod]
    [DataRow("B", true)]
    [DataRow("E-a", true)]
    [DataRow("E-b", true)]
    [DataRow("E-c", true)]
    [DataRow("E-d", true)]
    [DataRow("E-e", true)]
    [DataRow("E-f", true)]
    [DataRow("D", true)]
    [DataRow("D-x", false)]
    [DataRow("d", false)]
    [DataRow("E-g", false)]
    [DataRow("E-a-hwnd", false)]
    [DataRow("X-detector", false)]
    public void Verdict_FailsOnlyForTheEightItems(string category, bool fails)
    {
        var record = new HostObservation(1, category, "detail", "case", null);
        foreach (HostBoundaryKind kind in Enum.GetValues<HostBoundaryKind>())
        {
            Exception? failure = HostObservationVerdict.Evaluate(kind, "case", [record], [], null);
            Assert.AreEqual(fails, failure != null, $"{category} at {kind}");
            if (failure != null) { StringAssert.Contains(failure.Message, record.Format()); }
        }
        Assert.AreEqual(fails, HostObservationVerdict.IsFailureCategory(category));
    }

    /// <summary>
    /// 判定が、未処理例外だけなら同じ参照を返し、記録・復元の失敗・未処理例外が重なれば一つの失敗にまとめて元の例外を参照のまま内部例外へ持つことを確認します。
    /// 未処理例外の内容を変える実装、元の例外を文字列へ落とす実装、複数の失敗を別々に返す実装、B・E-aの両方の行を落とす実装を検出します。
    /// </summary>
    [TestMethod]
    public void Verdict_MergesRecordsRestoreFailuresAndUnhandledExceptionsIntoOneFailure()
    {
        Assert.IsNull(HostObservationVerdict.Evaluate(HostBoundaryKind.CaseEnd, "case", [], [], null));
        var unhandledOriginal = new InvalidOperationException("unhandled-original");
        var unhandled = new AggregateException("unhandled", unhandledOriginal);
        Assert.AreSame(unhandled, HostObservationVerdict.Evaluate(HostBoundaryKind.CaseEnd, "case", [], [], unhandled), "未処理例外だけなら同じ参照です。");
        var detectorOnly = new HostObservation(1, "E-g", "pending", "case", null);
        Assert.AreSame(unhandled, HostObservationVerdict.Evaluate(HostBoundaryKind.CaseStart, "case", [detectorOnly], [], unhandled), "記録だけの項目は未処理例外の失敗を変えません。");

        var unprepared = new HostObservation(2, "B", "unprepared window shown", "case", null);
        var windowResidue = new HostObservation(3, "E-a", "window remained", "case", null);
        var restoreCause = new InvalidOperationException("restore-original");
        HostRestoreFailure[] restoreFailures = [new HostRestoreFailure("close X", restoreCause), new HostRestoreFailure("close Y", null)];
        Exception merged = HostObservationVerdict.Evaluate(HostBoundaryKind.CaseEnd, "case-name", [unprepared, windowResidue, detectorOnly], restoreFailures, unhandled)
            ?? throw new AssertFailedException("A failure is required.");
        StringAssert.Contains(merged.Message, unprepared.Format(), "BとE-aの両方の行を含みます。");
        StringAssert.Contains(merged.Message, windowResidue.Format());
        Assert.DoesNotContain(detectorOnly.Format(), merged.Message, "記録だけの項目の行は失敗に含めません。");
        StringAssert.Contains(merged.Message, "close X");
        StringAssert.Contains(merged.Message, "close Y");
        StringAssert.Contains(merged.Message, "case-name");
        StringAssert.Contains(merged.Message, "TestCleanup");
        IReadOnlyList<Exception> inner = ((AggregateException)merged).Flatten().InnerExceptions;
        Assert.IsTrue(inner.Any(exception => ReferenceEquals(exception, unhandledOriginal)), "未処理例外の元の例外を参照のまま含みます。");
        Assert.IsTrue(inner.Any(exception => ReferenceEquals(exception, restoreCause)), "復元の元の例外を参照のまま含みます。");

        Exception restoreOnly = HostObservationVerdict.Evaluate(HostBoundaryKind.ModuleEnd, null, [], [new HostRestoreFailure("close X", restoreCause)], null)
            ?? throw new AssertFailedException("A restore failure is a failure.");
        Assert.AreSame(restoreCause, ((AggregateException)restoreOnly).Flatten().InnerExceptions.Single());
    }

    /// <summary>
    /// 未表示のWindowをMainWindowにした残留を復元すると、Closedが一回発生してApplication.Windowsから外れ、MainWindowが基準へ戻り、
    /// 続く比較で何も報告されず、復元の失敗が空であることを確認します。閉じない実装、二重に閉じる実装を検出します。
    /// WPFは閉じたMainWindowを自動でnullにするため、MainWindowを戻す処理の識別は <see cref="Restore_ContinuesAfterCancelledOrThrowingClose_AndKeepsTheOriginalException"/> が担います。
    /// 未表示のWindowは構築した時点でApplication.Windowsに含まれるため、残留は<c>E-a</c>（MainWindow=yes）です。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Restore_ClosesUnshownWindowAndReturnsMainWindowToBaseline()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Window? previousMain = application.MainWindow;
            Window? window = null;
            int closed = 0;
            try
            {
                application.MainWindow = null;
                var state = new HostResidueState(HostResidueBoundary.Observe());
                window = new Window { Title = "restore-a" };
                window.Closed += (_, _) => closed++;
                application.MainWindow = window;

                IReadOnlyList<HostResidue> found = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
                HostResidue residue = found.Single(candidate => candidate.Category is "E-a" or "E-b");
                Assert.AreEqual("E-a", residue.Category);
                Assert.AreSame(window, residue.Window);

                IReadOnlyList<HostRestoreFailure> failures = HostResidueRestoration.Restore(application, state, found);

                Assert.IsEmpty(failures);
                Assert.AreEqual(1, closed);
                Assert.IsFalse(application.Windows.Cast<Window>().Contains(window));
                Assert.AreSame(state.BaselineMainWindow, application.MainWindow);
                Assert.IsEmpty(Residues(state, "E-a", "E-b"));
            }
            finally
            {
                application.MainWindow = previousMain;
                if (window != null && closed == 0) { window.Close(); }
            }
        });
    }

    /// <summary>
    /// 復元がDispatcherを進めないことを確認します。復元の前に積んだNormalとBackgroundの予約が、Window・文化圏の復元の後も未消化のままです。
    /// 復元のために待機・消化APIでDispatcherを進める実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Restore_DoesNotAdvanceTheDispatcher()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Dispatcher dispatcher = application.Dispatcher;
            CultureInfo? originalCulture = Resources.Culture;
            Window? previousMain = application.MainWindow;
            Window? window = null;
            DispatcherOperation? normal = null;
            DispatcherOperation? background = null;
            try
            {
                application.MainWindow = null;
                var state = new HostResidueState(HostResidueBoundary.Observe());
                window = new Window { Title = "restore-b" };
                Resources.Culture = CultureInfo.GetCultureInfo(originalCulture?.Name == "fr-FR" ? "en-US" : "fr-FR");
                IReadOnlyList<HostResidue> found = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
                normal = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Normal);
                background = dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

                IReadOnlyList<HostRestoreFailure> failures = HostResidueRestoration.Restore(application, state, found);

                Assert.IsEmpty(failures);
                Assert.IsFalse(application.Windows.Cast<Window>().Contains(window), "復元はWindowを閉じています。");
                Assert.AreEqual(DispatcherOperationStatus.Pending, normal.Status);
                Assert.AreEqual(DispatcherOperationStatus.Pending, background.Status);
            }
            finally
            {
                normal?.Abort();
                background?.Abort();
                Resources.Culture = originalCulture;
                application.MainWindow = previousMain;
                if (window != null && application.Windows.Cast<Window>().Contains(window)) { window.Close(); }
            }
        });
    }

    /// <summary>
    /// 閉鎖を取り消す・例外を投げるWindowがあっても、復元が投げずに失敗を返し、元の例外を参照のまま保持し、他のWindowと文化圏の復元を続け、
    /// 復元できなかったWindowが続く比較で再報告されないことを確認します。最初の失敗で中断する実装、復元の例外を外へ漏らす実装、
    /// 例外を失う実装、失敗で報告済みの印を消す実装を検出します。取り消す場合は例外を伴わない失敗です。
    /// 取り消すWindowをMainWindowにして、閉じられなくても復元の後にMainWindowが基準へ戻ることを確認し、MainWindowを戻さない実装を検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public void Restore_ContinuesAfterCancelledOrThrowingClose_AndKeepsTheOriginalException(bool throws)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            CultureInfo? originalCulture = Resources.Culture;
            Window? previousMain = application.MainWindow;
            Window? ordinary = null;
            Window? stubborn = null;
            bool allowClose = false;
            var original = new InvalidOperationException("closing-original");
            try
            {
                application.MainWindow = null;
                var state = new HostResidueState(HostResidueBoundary.Observe());
                // 閉じる順序は後に作ったものから。取り消すWindowを後に作り、その失敗の後でも通常のWindowが閉じられることを確かめます。
                ordinary = new Window { Title = "restore-c-ordinary" };
                stubborn = new Window { Title = "restore-c-stubborn" };
                stubborn.Closing += (_, e) =>
                {
                    if (allowClose) { return; }
                    if (throws) { throw original; }
                    e.Cancel = true;
                };
                application.MainWindow = stubborn;
                Resources.Culture = CultureInfo.GetCultureInfo(originalCulture?.Name == "fr-FR" ? "en-US" : "fr-FR");
                IReadOnlyList<HostResidue> found = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());

                IReadOnlyList<HostRestoreFailure> failures = HostResidueRestoration.Restore(application, state, found);

                Assert.AreSame(state.BaselineMainWindow, application.MainWindow, "閉鎖の成否によらずMainWindowは基準へ戻ります。");
                HostRestoreFailure failure = failures.Single();
                StringAssert.Contains(failure.Item, "restore-c-stubborn");
                if (throws) { Assert.AreSame(original, failure.Cause); }
                else { Assert.IsNull(failure.Cause); }
                Assert.IsFalse(application.Windows.Cast<Window>().Contains(ordinary), "失敗の後のWindowも閉じています。");
                // 取り消されたWindowは残ります。例外を投げた閉鎖では、WPFが未表示のWindowをApplication.Windowsから外すため、残留の有無は表明しません。
                if (!throws) { Assert.IsTrue(application.Windows.Cast<Window>().Contains(stubborn), "閉じられなかったWindowは残ります。"); }
                Assert.AreEqual(state.Culture.Baseline, Resources.Culture?.Name, "Windowの失敗の後でも文化圏は戻っています。");
                Assert.IsEmpty(Residues(state, "E-a", "E-b", "E-d"), "復元できなかったWindowは、続く比較で再報告されません。");
            }
            finally
            {
                allowClose = true;
                Resources.Culture = originalCulture;
                application.MainWindow = previousMain;
                if (stubborn != null && application.Windows.Cast<Window>().Contains(stubborn)) { stubborn.Close(); }
                if (ordinary != null && application.Windows.Cast<Window>().Contains(ordinary)) { ordinary.Close(); }
            }
        });
    }

    /// <summary>
    /// 未表示のSettingsWindowが、<c>CloseForOwnerShutdown</c>で閉じられてApplication.Windowsから外れ、CloseReasonがOwnerShutdownで、復元の失敗が空であることを確認します。
    /// 通常の<c>Close</c>で閉じる実装は、閉鎖が取り消され（DataContextが無いため例外にもなり）、この表明で検出されます。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Restore_ClosesSettingsWindowForOwnerShutdown()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Window? previousMain = application.MainWindow;
            SettingsWindow? window = null;
            try
            {
                application.MainWindow = null;
                var state = new HostResidueState(HostResidueBoundary.Observe());
                window = new SettingsWindow();
                IReadOnlyList<HostResidue> found = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
                Assert.AreSame(window, found.Single(residue => residue.Category == "E-a").Window);

                IReadOnlyList<HostRestoreFailure> failures = HostResidueRestoration.Restore(application, state, found);

                Assert.IsEmpty(failures);
                Assert.IsFalse(application.Windows.Cast<Window>().Contains(window));
                Assert.AreEqual(SettingsWindowCloseReason.OwnerShutdown, window.CloseReason);
            }
            finally
            {
                application.MainWindow = previousMain;
                if (window != null && application.Windows.Cast<Window>().Contains(window)) { window.CloseForOwnerShutdown(); }
            }
        });
    }

    /// <summary>
    /// 値の復元（<c>Resources["vm"]</c>、文化圏、テーマ、NLog構成）が基準へ戻り、続く比較で報告されないことを確認します。
    /// vmは、基準にキーが無ければキーを削除し（nullの代入ではない）、基準にオブジェクトがあれば同じ参照へ戻ります。
    /// 文化圏は<c>ResourceService</c>の経路で戻り、<c>DisplayCulture</c>の変更通知が発生します（直接代入では通知されません）。
    /// テーマは保存値が戻り、適用の経路を通ります（<c>AppThemeService.Version</c>が変わります。保存値だけを戻す実装を検出します）。
    /// NLogは基準の同じオブジェクトへ戻ります。復元に成功した値は報告済みの印が消えるため、復元の直後（間に比較を挟まず）に同じ値へずらすと、
    /// 再び報告されて復元されます（印を消さない実装が、次のケースの同じ残留を黙って持ち越すことを検出します）。finallyで実際の値を元へ戻します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void Restore_ReturnsValuesToTheBaseline()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            CultureInfo? originalCulture = Resources.Culture;
            bool hadViewModel = application.Resources.Contains("vm");
            object? originalViewModel = application.Resources["vm"];
            string originalTheme = Settings.Default.AppearanceTheme;
            LoggingConfiguration? originalConfiguration = LogManager.Configuration;
            int displayCultureNotifications = 0;
            System.ComponentModel.PropertyChangedEventHandler onChanged = (_, e) =>
            {
                if (e.PropertyName == nameof(ResourceService.DisplayCulture)) { displayCultureNotifications++; }
            };
            ResourceService.Current.PropertyChanged += onChanged;
            try
            {
                // 基準にvmのキーが無い状態から、キーの削除で戻ることを確かめます。
                application.Resources.Remove("vm");
                Settings.Default.AppearanceTheme = AppThemeService.Light;
                AppThemeService.ApplyTheme(AppThemeService.Light);
                LoggingConfiguration baselineConfiguration = LogManager.Configuration ?? new LoggingConfiguration();
                LogManager.Configuration = baselineConfiguration;
                var state = new HostResidueState(HostResidueBoundary.Observe());

                Resources.Culture = CultureInfo.GetCultureInfo(state.Culture.Baseline as string == "fr-FR" ? "en-US" : "fr-FR");
                application.Resources["vm"] = new object();
                Settings.Default.AppearanceTheme = AppThemeService.Dark;
                AppThemeService.ApplyTheme(AppThemeService.Dark);
                LogManager.Configuration = new LoggingConfiguration();
                int themeVersion = AppThemeService.Version;
                IReadOnlyList<HostResidue> found = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
                CollectionAssert.IsSubsetOf(new[] { "E-c", "E-d", "E-e", "E-f" }, found.Select(residue => residue.Category).ToArray());
                displayCultureNotifications = 0;

                IReadOnlyList<HostRestoreFailure> failures = HostResidueRestoration.Restore(application, state, found);

                Assert.IsEmpty(failures);
                Assert.IsFalse(application.Resources.Contains("vm"), "基準にキーが無ければ、nullを代入せずキーを削除します。");
                Assert.AreEqual(state.Culture.Baseline, Resources.Culture?.Name);
                Assert.AreEqual(state.Culture.Baseline, ResourceService.Current.DisplayCulture.Name);
                Assert.IsGreaterThanOrEqualTo(1, displayCultureNotifications, "表示の通知（DisplayCulture）が発生します。");
                Assert.AreEqual(AppThemeService.Light, Settings.Default.AppearanceTheme);
                Assert.AreNotEqual(themeVersion, AppThemeService.Version, "保存値だけでなくテーマの適用の経路を通ります。");
                Assert.AreSame(baselineConfiguration, LogManager.Configuration);

                // 復元の直後に、比較を挟まず同じ値（同じ文化圏、Dark、同じvm、同じNLog構成）へ再びずらすと、四項目とも再び報告されます。
                var shiftedCulture = CultureInfo.GetCultureInfo(state.Culture.Baseline as string == "fr-FR" ? "en-US" : "fr-FR");
                object sameViewModel = new object();
                var sameConfiguration = new LoggingConfiguration();
                for (int round = 0; round < 2; round++)
                {
                    Resources.Culture = shiftedCulture;
                    application.Resources["vm"] = sameViewModel;
                    Settings.Default.AppearanceTheme = AppThemeService.Dark;
                    LogManager.Configuration = sameConfiguration;
                    IReadOnlyList<HostResidue> again = HostResidueComparison.Compare(state, HostResidueBoundary.Observe());
                    CollectionAssert.IsSubsetOf(new[] { "E-c", "E-d", "E-e", "E-f" }, again.Select(residue => residue.Category).ToArray(), $"round {round}");
                    Assert.IsEmpty(HostResidueRestoration.Restore(application, state, again));
                }
                Assert.IsEmpty(Residues(state, "E-c", "E-d", "E-e", "E-f"));

                // 基準にオブジェクトがある状態から、同じ参照へ戻ることを確かめます。
                object baselineViewModel = new object();
                application.Resources["vm"] = baselineViewModel;
                var withViewModel = new HostResidueState(HostResidueBoundary.Observe());
                application.Resources["vm"] = new object();
                found = HostResidueComparison.Compare(withViewModel, HostResidueBoundary.Observe());
                Assert.IsEmpty(HostResidueRestoration.Restore(application, withViewModel, found));
                Assert.AreSame(baselineViewModel, application.Resources["vm"]);
            }
            finally
            {
                ResourceService.Current.PropertyChanged -= onChanged;
                Resources.Culture = originalCulture;
                RestoreViewModel(application, hadViewModel, originalViewModel);
                Settings.Default.AppearanceTheme = originalTheme;
                AppThemeService.ApplyTheme(originalTheme);
                LogManager.Configuration = originalConfiguration;
            }
        });
    }

    /// <summary>
    /// Windowに対応しないHWNDの分類が、クラス名・HwndSourceの有無・表示状態で決まることを確認します。
    /// 表示中や、HwndSourceを持つものまでwpf-internalやimeにする実装、前方一致・部分一致で分類する実装、null・空を誤分類する実装を検出します。
    /// </summary>
    [TestMethod]
    [DataRow("HwndWrapper[x;;y]", false, false, "wpf-internal")]
    [DataRow("HwndWrapper[x;;y]", true, false, "other")]
    [DataRow("HwndWrapper[x;;y]", false, true, "other")]
    [DataRow("hwndwrapper[x;;y]", false, false, "other")]
    [DataRow("IME", false, false, "ime")]
    [DataRow("MSCTFIME UI", false, false, "ime")]
    [DataRow("IME", false, true, "other")]
    [DataRow("IMEX", false, false, "other")]
    [DataRow("", false, false, "other")]
    [DataRow(null, false, false, "other")]
    public void HwndClassification_FollowsClassNameHwndSourceAndVisibility(string? className, bool hasHwndSource, bool isVisible, string expected)
        => Assert.AreEqual(expected, HostResidueComparison.ClassifyHwnd(className, hasHwndSource, isVisible));
}
