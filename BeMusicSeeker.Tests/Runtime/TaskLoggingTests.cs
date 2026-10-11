using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RibbitTaskEx = Ribbit.Util.Extensions.TaskEx;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TaskLoggingTests
{
    /// <summary>成功したTaskをログ経由でも成功として待てることを確認し、ログ処理による終端の変質を防ぎます。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_PreservesSuccessfulCompletion()
    {
        await Task.CompletedTask.LoggingAndPropagate();
    }

    /// <summary>失敗Taskをログ経由で待っても元の例外instanceを返し、失敗の置換や隠蔽を防ぎます。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_RethrowsOriginalFailure()
    {
        var failure = new InvalidOperationException("workflow failed");

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Task.FromException(failure).LoggingAndPropagate());

        Assert.AreSame(failure, exception);
    }

    /// <summary>取消Taskをログ経由で待ってもCanceledを保持し、Faultedへ誤分類しません。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task propagated = Task.FromCanceled(cancellation.Token).LoggingAndPropagate();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => propagated);

        Assert.IsTrue(propagated.IsCanceled);
        Assert.IsFalse(propagated.IsFaulted);
    }

    /// <summary>結果を持つ成功Taskをログ経由で待っても同じ結果を返し、結果の欠落を防ぎます。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_PreservesGenericResult()
    {
        const int expected = 17;

        int actual = await Task.FromResult(expected).LoggingAndPropagate();

        Assert.AreEqual(expected, actual);
    }

    /// <summary>元Taskのgateが未解放なら転送Taskも未完了で、解放後に元結果を返して早期完了を防ぎます。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_DoesNotCompleteBeforeGenericSource()
    {
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> propagated = RibbitTaskEx.LoggingAndPropagate(
            operation.Task,
            (Task _, string _, string _, int _) => { });

        Assert.IsFalse(propagated.IsCompleted);

        operation.SetResult(17);

        Assert.AreEqual(17, await propagated);
    }

    /// <summary>loggerが失敗しても元Taskの成功結果を保持し、診断失敗で本体結果を上書きしません。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_GenericLoggerFailureDoesNotReplaceResult()
    {
        const int expected = 17;

        int actual = await RibbitTaskEx.LoggingAndPropagate(
            Task.FromResult(expected),
            (Task _, string _, string _, int _) => throw new InvalidOperationException("logger failed"));

        Assert.AreEqual(expected, actual);
    }

    /// <summary>loggerと本体が共に失敗しても本体の元例外を保持し、診断側の例外へ置換しません。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_GenericLoggerFailurePreservesOriginalFailure()
    {
        var failure = new InvalidOperationException("workflow failed");

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => RibbitTaskEx.LoggingAndPropagate(
                Task.FromException<int>(failure),
                (Task _, string _, string _, int _) => throw new InvalidOperationException("logger failed")));

        Assert.AreSame(failure, exception);
    }

    /// <summary>loggerが失敗しても本体の取消分類を保持し、診断失敗をFaultedとして漏らしません。</summary>
    [TestMethod]
    public async Task LoggingAndPropagate_GenericLoggerFailurePreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<int> propagated = RibbitTaskEx.LoggingAndPropagate(
            Task.FromCanceled<int>(cancellation.Token),
            (Task _, string _, string _, int _) => throw new InvalidOperationException("logger failed"));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => propagated);

        Assert.IsTrue(propagated.IsCanceled);
        Assert.IsFalse(propagated.IsFaulted);
    }

    /// <summary>未完了Taskから失敗通知を先行させず、本体失敗後に同じTaskを通知して監視対象の取り違えを防ぎます。</summary>
    [TestMethod]
    public async Task ObserveFault_ReportsGenericFailureWithoutReturningAwaitableTask()
    {
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackSignal = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Task, Action<Task, string, string, int>, string, string, int> observeFault = RibbitTaskEx.ObserveFault;

        observeFault(
            operation.Task,
            (completedTask, _, _, _) => callbackSignal.TrySetResult(completedTask),
            nameof(ObserveFault_ReportsGenericFailureWithoutReturningAwaitableTask),
            nameof(TaskLoggingTests),
            0);

        Assert.IsFalse(callbackSignal.Task.IsCompleted);

        var failure = new InvalidOperationException("workflow failed");
        operation.SetException(failure);

        Task observedTask = await callbackSignal.Task;

        Assert.AreSame(operation.Task, observedTask);
        Assert.IsTrue(operation.Task.IsFaulted);
    }

    /// <summary>失敗監視のloggerが投げても本体の元失敗を保持し、ログ処理が本体状態を変更しません。</summary>
    [TestMethod]
    public async Task ObserveFault_ContainsLoggerFailureAndPreservesSourceFault()
    {
        var operation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Task, Action<Task, string, string, int>, string, string, int> observeFault = RibbitTaskEx.ObserveFault;

        observeFault(
            operation.Task,
            (completedTask, _, _, _) =>
            {
                callbackSignal.TrySetResult(true);
                throw new InvalidOperationException("logger failed");
            },
            nameof(ObserveFault_ContainsLoggerFailureAndPreservesSourceFault),
            nameof(TaskLoggingTests),
            0);

        var failure = new InvalidOperationException("workflow failed");
        operation.SetException(failure);

        Assert.IsTrue(await callbackSignal.Task);
        Assert.IsTrue(operation.Task.IsFaulted);
        Assert.AreSame(failure, operation.Task.Exception?.InnerException);
    }
}
