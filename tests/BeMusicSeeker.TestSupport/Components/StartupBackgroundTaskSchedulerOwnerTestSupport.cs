using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class StartupBackgroundTaskSchedulerOwnerTestSupport
{
    /// <summary>各ケースが渡す依存を使って検証対象の管理主体を構成し、既存の通知接続を維持します。</summary>
    internal static StartupBackgroundTaskSchedulerOwner CreateOwner(
        Func<bool>? isShutdownRequested = null,
        Action<long, long>? schedulerIdleChanged = null,
        Action<string>? logInfo = null,
        Action<string>? logWarning = null,
        Action<string>? logShutdown = null)
    {
        var notificationProbe = new SchedulerNotificationProbe();
        return new StartupBackgroundTaskSchedulerOwner(
            isShutdownRequested ?? (() => false),
            logInfo ?? (_ => { }),
            logWarning ?? (_ => { }),
            logShutdown ?? (_ => { }),
            value => value ?? string.Empty,
            (generation, revision) =>
            {
                notificationProbe.Publish(generation, revision);
                schedulerIdleChanged?.Invoke(generation, revision);
            },
            notificationProbe);
    }

    /// <summary>
    /// schedulerのidle通知（<c>schedulerIdleChanged</c>）を使うプローブで、<c>IsFullyIdle</c> が真になるまで待ちます。
    /// 通知のたびに条件を判定し直す補助であり、本番の <c>StartupBackgroundTaskSchedulerOwner.WaitForFullyIdleAsync()</c> のTaskとは別物です。
    /// 本番のTaskの契約を検査する用途には使いません。
    /// </summary>
    internal static Task WaitForFullyIdleAsync(StartupBackgroundTaskSchedulerOwner owner)
    {
        return WaitUntilAsync(owner, () => owner.IsFullyIdle);
    }

    /// <summary>既存の待機条件と期限でschedulerの指定状態を待ちます。</summary>
    internal static async Task WaitUntilAsync(StartupBackgroundTaskSchedulerOwner owner, Func<bool> predicate)
    {
        var notificationProbe = (SchedulerNotificationProbe)owner.ProgressSynchronization;
        while (!predicate())
        {
            Task<(long Generation, long Revision)> notification = notificationProbe.CaptureNextNotification();
            if (predicate())
            {
                return;
            }
            await notification.ConfigureAwait(false);
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class SchedulerNotificationProbe
    {
        private readonly object syncRoot = new();

        private TaskCompletionSource<(long Generation, long Revision)> nextNotification = CreateNotificationSource();

        internal Task<(long Generation, long Revision)> CaptureNextNotification()
        {
            lock (syncRoot)
            {
                return nextNotification.Task;
            }
        }

        internal void Publish(long generation, long revision)
        {
            TaskCompletionSource<(long Generation, long Revision)> notification;
            lock (syncRoot)
            {
                notification = nextNotification;
                nextNotification = CreateNotificationSource();
            }
            notification.TrySetResult((generation, revision));
        }

        private static TaskCompletionSource<(long Generation, long Revision)> CreateNotificationSource()
        {
            return new TaskCompletionSource<(long Generation, long Revision)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

}
