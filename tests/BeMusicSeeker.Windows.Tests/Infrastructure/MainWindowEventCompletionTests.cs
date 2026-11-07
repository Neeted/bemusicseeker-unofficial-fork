using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Windows.Tests.Infrastructure;

[TestClass]
public sealed class MainWindowEventCompletionTests
{
    private static event Action? Raised;

    /// <summary>購読者のないイベントでは、捕捉が戻った時点でCompletionが開始0件の診断付きで失敗済みになります。無期限には待ちません。</summary>
    [TestMethod]
    public void Capture_WithoutHandler_FailsImmediatelyWithZeroStarted()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Raised = null;
            MainWindowEventCompletion.AsyncEventCapture capture = MainWindowEventCompletion.Capture(() => Raised?.Invoke());
            Assert.IsTrue(capture.Completion.IsFaulted, "開始0件はその場で失敗します。");
            Assert.AreEqual(0, capture.StartedCount);
            InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => capture.Completion.GetAwaiter().GetResult());
            StringAssert.Contains(failure.Message, "started=0");
        });
    }

    /// <summary>同期で終わるハンドラーを先に、gateを待つハンドラーを後に購読しても、後のハンドラーの終端まで未完了です。</summary>
    [TestMethod]
    public void Capture_SynchronousHandlerBeforePendingHandler_DoesNotCompleteEarly()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowEventCompletion.AsyncEventCapture? capture = null;
            Exception? bodyFailure = null;
            Raised = null;
            Raised += async () => await Task.CompletedTask;
            Raised += async () => await gate.Task;
            try
            {
                capture = MainWindowEventCompletion.Capture(() => Raised?.Invoke());
                Assert.IsFalse(capture.Completion.IsCompleted, "先に終わった同期ハンドラーで早期完了してはいけません。");
                gate.SetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(capture.Completion, "event completion with pending handler");
                Assert.IsTrue(capture.Completion.IsCompletedSuccessfully);
                Assert.AreEqual(2, capture.StartedCount);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally { Recover(gate, capture, bodyFailure); }
        });
    }

    /// <summary>awaitの後に例外を投げるハンドラーは、元の例外で失敗し、Completion自体も元の例外で失敗して成功にしません。記録は待機で消費されます。</summary>
    [TestMethod]
    public void Capture_HandlerThrowingAfterAwait_FailsWithOriginalException()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = new InvalidOperationException("handler failure");
            MainWindowEventCompletion.AsyncEventCapture? capture = null;
            Exception? bodyFailure = null;
            Raised = null;
            Raised += async () =>
            {
                await gate.Task;
                throw original;
            };
            try
            {
                capture = MainWindowEventCompletion.Capture(() => Raised?.Invoke());
                Assert.IsFalse(capture.Completion.IsCompleted);
                MainWindowEventCompletion.AsyncEventCapture started = capture;
                gate.SetResult();
                Assert.AreSame(original, Assert.ThrowsExactly<InvalidOperationException>(() =>
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(started.Completion, "event completion with failing handler")));
                Assert.AreEqual(0, TestUiDispatcherHost.TakeUnhandledExceptions().Count, "待機が元の例外を観測して消費済みです。");
                TestUiDispatcherHost.ProcessQueuedBackground();
                Assert.IsFalse(capture.Completion.IsCompletedSuccessfully, "例外を投げたハンドラーで成功にしてはいけません。");
                Assert.IsTrue(capture.Completion.IsFaulted, "Completion自体が失敗で終端します。");
                AggregateException completionFailure = capture.Completion.Exception
                    ?? throw new AssertFailedException("The faulted completion has no exception.");
                Assert.AreSame(original, completionFailure.InnerExceptions.Single());
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally { Recover(gate, capture, bodyFailure); }
        });
    }

    /// <summary>単一のasyncハンドラーは、gateの解放による終端後に成功し、開始数は1です。</summary>
    [TestMethod]
    public void Capture_SingleAsyncHandler_SucceedsAfterTermination()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            MainWindowEventCompletion.AsyncEventCapture? capture = null;
            Exception? bodyFailure = null;
            Raised = null;
            Raised += async () => await gate.Task;
            try
            {
                capture = MainWindowEventCompletion.Capture(() => Raised?.Invoke());
                Assert.IsFalse(capture.Completion.IsCompleted);
                gate.SetResult();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(capture.Completion, "event completion with single handler");
                Assert.IsTrue(capture.Completion.IsCompletedSuccessfully);
                Assert.AreEqual(1, capture.StartedCount);
            }
            catch (Exception exception)
            {
                bodyFailure = exception;
                throw;
            }
            finally { Recover(gate, capture, bodyFailure); }
        });
    }

    /// <summary>
    /// 購読を外し、gateを解放して、開始済みのハンドラーの継続と例外の送出をBackground優先度まで消化して回収します。
    /// 終端しないCompletionを無期限に待たないため、回収では待機せず消化だけを行います。
    /// 本体が失敗した場合は、回収側の失敗で本体の失敗を隠さず、本体の失敗のDataへ添付します。本体が成功した場合の回収の失敗は、そのまま失敗にします。
    /// </summary>
    private static void Recover(TaskCompletionSource gate, MainWindowEventCompletion.AsyncEventCapture? capture,
        Exception? bodyFailure)
    {
        Raised = null;
        gate.TrySetResult();
        if (capture == null) { return; }
        try { TestUiDispatcherHost.ProcessQueuedBackground(); }
        catch (Exception recoveryFailure) when (bodyFailure != null)
        {
            bodyFailure.Data["EventCompletionRecoveryFailure"] = recoveryFailure.ToString();
        }
    }
}
