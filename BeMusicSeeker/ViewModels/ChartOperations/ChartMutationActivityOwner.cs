using System;
using System.Threading;
using Ribbit.Logging;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 譜面・パッケージ変更の生存中の活動期間を数え、表示へ開始・終了を知らせます。モデルの変更権限は所有しません。
/// </summary>
internal sealed class ChartMutationActivityOwner
{
    private readonly object syncRoot = new();
    private int activeLeaseCount;
    private bool isActive;

    /// <summary>最外開始と最終解放後の表示更新を通知します。購読者ごとの失敗を診断し、活動状態と他の購読者へ影響させません。</summary>
    internal event EventHandler ActivityChanged;

    /// <summary>生存中の活動期間が一つ以上あるかを、表示通知の成否によらずロック内の正本から読み取ります。</summary>
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

    /// <summary>活動期間を一つ開始し、一回だけ解放できるleaseを返します。表示通知の失敗で受理したleaseを取り消しません。</summary>
    /// <exception cref="InvalidOperationException">活動期間の数が上限へ達しています。</exception>
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
            PublishActivityChanged();
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
            PublishActivityChanged();
        }
    }

    private void PublishActivityChanged()
    {
        EventHandler handlers = ActivityChanged;
        if (handlers == null)
        {
            return;
        }

        // 購読先は表示更新だけを担当し、コマンドの許可判定はIsActiveの正本を読み直します。
        // 通知を活動数のロック外で個別に隔離し、解放時にも本体の失敗を置き換えません。
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception failure)
            {
                try { NLogWrapper.FileLogger?.Warn(failure, "chart_mutation_activity_notification_failed observer=" + handler.Method.Name); }
                catch { /* 任意の診断先の失敗でも、他の購読者への通知を続けます。 */ }
            }
        }
    }

    private sealed class ActivityLease : IDisposable
    {
        private readonly ChartMutationActivityOwner owner;
        private int disposed;

        /// <summary>この管理主体で開始した一つの活動期間を解放するleaseを作ります。</summary>
        internal ActivityLease(ChartMutationActivityOwner owner)
        {
            this.owner = owner;
        }

        /// <summary>所有する活動期間を一回だけ解放します。表示通知の失敗は本体の結果へ追加しません。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Exit();
            }
        }
    }
}
