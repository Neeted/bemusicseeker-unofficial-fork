using BeMusicSeeker.Models.Update;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>実際の更新の管理主体と、取消か解放まで終わらない取得の代替を組み立て、回数と終端を観測します。</summary>
internal sealed class StartupUpdateTransferHarness
{
    private int prepareCount;
    private int deleteCount;
    private int failureCount;
    private int shutdownRequestCount;
    private int terminalCount;
    private readonly bool holdUntilReleased;
    private Task worker = Task.CompletedTask;

    /// <param name="bindShutdownPreparation">終了準備の窓口を代替で結ぶか。実際のShell終了処理を接続するケースは、その処理が結ぶため偽にします。</param>
    /// <param name="holdUntilReleased">取消を受けても、<see cref="Release"/> まで取得を終えないか。偽なら取消を受けた時点で取消として終えます。</param>
    internal StartupUpdateTransferHarness(bool bindShutdownPreparation = true, bool holdUntilReleased = false)
    {
        this.holdUntilReleased = holdUntilReleased;
        UpdateAssetInfo asset = new()
        {
            FileName = "app.zip",
            Url = "https://example.test/app.zip",
            Sha256 = new string('a', 64),
            SizeBytes = 1,
            IncludesChartInfoMetadata = false
        };
        var available = UpdateCheckResult.Available(
            new Version(2, 0, 0, 0), "1.0.0.0", "2.0.0.0", [asset], "https://example.test/releases/v2.0.0.0");
        Owner = new StartupUpdateWorkflowOwner(
            () => Task.FromResult(available),
            TransferAsync,
            _ =>
            {
                Interlocked.Increment(ref prepareCount);
                return new FakePreparedUpdaterLaunch();
            },
            () => { },
            _ => Interlocked.Increment(ref deleteCount),
            action => worker = ConcurrencyProbe.StartDedicatedAsync(action),
            action => action());
        Owner.PresentationRequested += request => request.Complete(asset);
        Owner.FailurePresentationRequested += _ =>
        {
            Interlocked.Increment(ref failureCount);
            return Task.CompletedTask;
        };
        Owner.ApplicationShutdownRequested += () => Interlocked.Increment(ref shutdownRequestCount);
        if (bindShutdownPreparation)
        {
            Owner.BindShutdownPreparation(reason => Task.FromResult(new ShutdownPreparationResult(reason, 1L, false, 0)));
        }
        Owner.ProgressChanged += snapshot =>
        {
            if (snapshot.Phase == StartupUpdateProgressPhase.WaitingForInitialization)
            {
                WaitingObserved.TrySetResult(true);
            }
        };
        Owner.TerminalPublished += published =>
        {
            Receipt = published;
            Interlocked.Increment(ref terminalCount);
            Terminal.TrySetResult(true);
        };
    }

    internal StartupUpdateWorkflowOwner Owner { get; }

    internal TaskCompletionSource<bool> TransferEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<bool> CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<bool> WaitingObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<bool> Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource<bool> ReleaseSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal StartupUpdateWorkflowCompletionReceipt? Receipt { get; private set; }

    internal int PrepareCount => Volatile.Read(ref prepareCount);

    internal int DeleteCount => Volatile.Read(ref deleteCount);

    internal int FailureCount => Volatile.Read(ref failureCount);

    internal int ShutdownRequestCount => Volatile.Read(ref shutdownRequestCount);

    internal int TerminalCount => Volatile.Read(ref terminalCount);

    internal void Release() => ReleaseSignal.TrySetResult(true);

    internal Task AwaitAsync(Task notification, string name)
        => TestUiDispatcherHost.AwaitNotificationAsync(notification, worker, name);

    /// <summary>待合せを解放し、終了を通知して、作業と終端の受領を回収します。</summary>
    internal async Task FinishAsync()
    {
        Release();
        Owner.NotifyClosing();
        await worker;
        await Task.WhenAll(Owner.WaitForIdleAsync(), Owner.WaitForTerminalAsync());
    }

    private async Task<string> TransferAsync(UpdateAssetInfo asset, CancellationToken token, IProgress<UpdateDownloadProgress> progress)
    {
        token.Register(() => CancelObserved.TrySetResult(true));
        TransferEntered.TrySetResult(true);
        Task first = await Task.WhenAny(ReleaseSignal.Task, holdUntilReleased ? ReleaseSignal.Task : CancelObserved.Task).ConfigureAwait(false);
        if (first == CancelObserved.Task || token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        return "package.zip";
    }
}
