using System;
using System.Threading;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 物理 path を共有する譜面変更・一時再生コピー・保留操作・導入予約を相互排他します。
///
/// <para>受付は非待機とし、競合時は経路ごとの Busy 結果を返します。Busy の案内以外の確認、
/// 再生停止、filesystem / catalog の変更は始めません。自動導入は最初の受理から queue の drain まで
/// 一つの lease を所有し、追加 drop では取り直しません。</para>
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
