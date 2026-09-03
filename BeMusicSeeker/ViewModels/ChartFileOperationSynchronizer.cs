using System;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Serializes chart-file mutations and temporary playback copies that share physical paths.
///
/// <para>The gate is deliberately fail-fast.  A caller that cannot acquire it
/// must return its route-specific busy result without touching dialogs,
/// playback, filesystem state, or catalog state.</para>
/// </summary>
internal sealed class ChartFileOperationSynchronizer
{
    private object activeLease;

    /// <summary>
    /// Attempts to acquire the single chart-file operation lease without
    /// waiting.  The lease may be disposed by any thread and is idempotent.
    /// </summary>
    /// <param name="lease">The acquired lease, or <see langword="null"/> when busy.</param>
    /// <returns><see langword="true"/> only when this call owns the gate.</returns>
    internal bool TryEnter(out IDisposable lease)
    {
        var token = new object();
        if (Interlocked.CompareExchange(ref activeLease, token, null) != null)
        {
            lease = null;
            return false;
        }

        lease = new Releaser(this, token);
        return true;
    }

    private sealed class Releaser : IDisposable
    {
        private ChartFileOperationSynchronizer owner;

        private readonly object token;

        internal Releaser(ChartFileOperationSynchronizer owner, object token)
        {
            this.owner = owner;
            this.token = token;
        }

        public void Dispose()
        {
            ChartFileOperationSynchronizer current = Interlocked.Exchange(ref owner, null);
            if (current != null)
            {
                current.Release(token);
            }
        }
    }

    private void Release(object token)
    {
        // The token comparison prevents a stale lease from clearing a newer
        // lease if release and reacquisition race across threads.
        Interlocked.CompareExchange(ref activeLease, null, token);
    }
}
