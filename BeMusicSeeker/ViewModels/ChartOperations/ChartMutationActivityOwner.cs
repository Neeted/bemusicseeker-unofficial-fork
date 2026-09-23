using System;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the shared live activity window for chart/package mutations.
/// </summary>
internal sealed class ChartMutationActivityOwner
{
    private readonly object syncRoot = new();
    private int activeLeaseCount;
    private bool isActive;

    internal event EventHandler ActivityChanged;

    internal bool IsActive
    {
        get
        {
            lock (syncRoot)
            {
                return isActive;
            }
        }
    }

    internal IDisposable Enter()
    {
        bool publishActive;
        lock (syncRoot)
        {
            if (activeLeaseCount == int.MaxValue)
            {
                throw new InvalidOperationException("Chart mutation activity lease count overflowed.");
            }
            activeLeaseCount++;
            publishActive = activeLeaseCount == 1;
            if (publishActive)
            {
                isActive = true;
            }
        }

        if (publishActive)
        {
            try
            {
                ActivityChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception failure)
            {
                bool publishInactive;
                lock (syncRoot)
                {
                    activeLeaseCount--;
                    publishInactive = activeLeaseCount == 0;
                    if (publishInactive)
                    {
                        isActive = false;
                    }
                }
                if (publishInactive)
                {
                    try
                    {
                        ActivityChanged?.Invoke(this, EventArgs.Empty);
                    }
                    catch (Exception rollbackFailure)
                    {
                        throw new AggregateException(failure, rollbackFailure);
                    }
                }
                throw;
            }
        }

        return new ActivityLease(this);
    }

    private void Exit()
    {
        bool publishInactive;
        lock (syncRoot)
        {
            if (activeLeaseCount <= 0)
            {
                throw new InvalidOperationException("Chart mutation activity lease was released without an active lease.");
            }
            activeLeaseCount--;
            publishInactive = activeLeaseCount == 0;
            if (publishInactive)
            {
                isActive = false;
            }
        }

        if (publishInactive)
        {
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ActivityLease : IDisposable
    {
        private readonly ChartMutationActivityOwner owner;
        private int disposed;

        internal ActivityLease(ChartMutationActivityOwner owner)
        {
            this.owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
