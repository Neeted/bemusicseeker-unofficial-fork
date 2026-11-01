namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実Dispatcherへ配送するcompiled async voidハンドラーの終端だけを捕捉します。</summary>
internal static class MainWindowEventCompletion
{
    /// <summary>compiledイベントのasync void寿命を現在のDispatcherへ接続し、Taskや通知の完了から推測せず実ハンドラー終端を返します。</summary>
    internal static Task CaptureAsyncEventCompletion(Action raiseEvent)
    {
        SynchronizationContext original = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The event fixture requires the WPF synchronization context.");
        var context = new EventCompletionContext(original);
        SynchronizationContext.SetSynchronizationContext(context);
        try { raiseEvent(); }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        return context.Completion.Task;
    }

    /// <summary>async voidの開始・終端通知だけを観測し、処理・例外の配送は実Dispatcherへ委譲します。</summary>
    internal sealed class EventCompletionContext(SynchronizationContext original) : SynchronizationContext
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeOperations;
        public override void OperationStarted() { Interlocked.Increment(ref activeOperations); original.OperationStarted(); }
        public override void OperationCompleted()
        {
            original.OperationCompleted();
            if (Interlocked.Decrement(ref activeOperations) == 0) { Completion.TrySetResult(); }
        }
        public override void Post(SendOrPostCallback callback, object? state) => original.Post(callback, state);
        public override void Send(SendOrPostCallback callback, object? state) => original.Send(callback, state);
        public override SynchronizationContext CreateCopy() => this;
    }
}
