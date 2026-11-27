namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// 要求ごとの期限を、グローバルな時計やタイマーを変更せずに進めるための手動時計です。
/// 一回限りのタイマーだけを扱い、<see cref="Advance"/> が呼ばれた時点で期限に達したタイマーを同期的に発火します。
/// </summary>
internal sealed class ManualRequestTimeProvider : TimeProvider
{
    private readonly object gate = new();
    private readonly List<RequestTimer> timers = [];
    private TimeSpan elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new RequestTimer(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
            timer.Change(dueTime, period);
        }
        return timer;
    }

    /// <summary>時計を進め、期限に達した全タイマーを一度だけ発火します。</summary>
    internal void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }
        RequestTimer[] due;
        lock (gate)
        {
            elapsed += amount;
            due = timers.Where(timer => timer.Due <= elapsed).ToArray();
            foreach (RequestTimer timer in due)
            {
                timer.Due = TimeSpan.MaxValue;
            }
        }
        foreach (RequestTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class RequestTimer : ITimer
    {
        private readonly ManualRequestTimeProvider owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private bool disposed;

        internal RequestTimer(ManualRequestTimeProvider owner, TimerCallback callback, object? state)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
        }

        internal TimeSpan Due { get; set; } = TimeSpan.MaxValue;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
            {
                throw new NotSupportedException("This request-clock fixture supports one-shot timers only.");
            }
            lock (owner.gate)
            {
                if (disposed)
                {
                    return false;
                }
                Due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : owner.elapsed + dueTime;
                return true;
            }
        }

        internal void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                disposed = true;
                owner.timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
