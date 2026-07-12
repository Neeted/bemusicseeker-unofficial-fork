using System;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Serializes chart-file mutations and temporary playback copies that share physical paths.
/// </summary>
internal sealed class ChartFileOperationSynchronizer
{
    private readonly object syncRoot = new();

    internal IDisposable Enter()
    {
        Monitor.Enter(syncRoot);
        return new Releaser(syncRoot);
    }

    private sealed class Releaser : IDisposable
    {
        private object target;

        internal Releaser(object target)
        {
            this.target = target;
        }

        public void Dispose()
        {
            object current = Interlocked.Exchange(ref target, null);
            if (current != null)
            {
                Monitor.Exit(current);
            }
        }
    }
}
