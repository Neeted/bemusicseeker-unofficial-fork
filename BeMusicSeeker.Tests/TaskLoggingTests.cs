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
    [TestMethod]
    public async Task LoggingAndPropagate_PreservesSuccessfulCompletion()
    {
        await Task.CompletedTask.LoggingAndPropagate();
    }

    [TestMethod]
    public async Task LoggingAndPropagate_RethrowsOriginalFailure()
    {
        var failure = new InvalidOperationException("workflow failed");

        InvalidOperationException exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => Task.FromException(failure).LoggingAndPropagate());

        Assert.AreSame(failure, exception);
    }

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

    [TestMethod]
    public async Task LoggingAndPropagate_PreservesGenericResult()
    {
        const int expected = 17;

        int actual = await Task.FromResult(expected).LoggingAndPropagate();

        Assert.AreEqual(expected, actual);
    }

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

    [TestMethod]
    public async Task LoggingAndPropagate_GenericLoggerFailureDoesNotReplaceResult()
    {
        const int expected = 17;

        int actual = await RibbitTaskEx.LoggingAndPropagate(
            Task.FromResult(expected),
            (Task _, string _, string _, int _) => throw new InvalidOperationException("logger failed"));

        Assert.AreEqual(expected, actual);
    }

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

        Task observedTask = await callbackSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreSame(operation.Task, observedTask);
        Assert.IsTrue(operation.Task.IsFaulted);
    }

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

        Assert.IsTrue(await callbackSignal.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(operation.Task.IsFaulted);
        Assert.AreSame(failure, operation.Task.Exception?.InnerException);
    }
}
