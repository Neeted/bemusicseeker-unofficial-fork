using System.Collections.Concurrent;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// テスト用にUIスレッドを模す、単一の専用スレッドで継続を直列に実行する同期の文脈です。
/// 本番のUI窓口と同じく、この文脈から呼ばれたときだけ同期にブロックする窓口の検査で、「UIの文脈の外から依頼したか」を判定します。
/// </summary>
internal sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    private readonly Thread thread;

    internal SingleThreadSynchronizationContext()
    {
        thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach ((SendOrPostCallback callback, object? state) in queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        })
        { IsBackground = true };
        thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

    /// <summary>この文脈のスレッドで処理を開始し、その非同期の終端を返します。</summary>
    internal Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                operation().ContinueWith(task =>
                {
                    if (task.IsFaulted) { completion.TrySetException(task.Exception!.InnerExceptions); }
                    else if (task.IsCanceled) { completion.TrySetCanceled(); }
                    else { completion.TrySetResult(task.Result); }
                }, TaskScheduler.Default);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }, null);
        return completion.Task;
    }

    /// <summary>この文脈のスレッドで処理を開始し、その非同期の終端を返します。</summary>
    internal Task RunAsync(Func<Task> operation)
        => RunAsync(async () =>
        {
            await operation().ConfigureAwait(true);
            return true;
        });

    /// <summary>この文脈のスレッドで同期の処理を実行し、その終端を返します。</summary>
    internal Task RunAsync(Action operation)
        => RunAsync(() =>
        {
            operation();
            return Task.FromResult(true);
        });

    public void Dispose()
    {
        queue.CompleteAdding();
        thread.Join();
    }
}
