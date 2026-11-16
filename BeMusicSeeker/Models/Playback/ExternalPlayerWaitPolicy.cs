#nullable enable annotations
using System;
using System.Threading;

namespace BeMusicSeeker.Models;

/// <summary>外部プレイヤーの期限判定と同期待機を、同じ単調時計の境界で扱います。</summary>
internal sealed class ExternalPlayerWaitPolicy
{
    /// <summary>本番の単調時計と実待機で10秒を上限にします。</summary>
    internal static ExternalPlayerWaitPolicy Default { get; } = new(TimeSpan.FromSeconds(10));

    private readonly TimeSpan timeout;

    private readonly TimeProvider timeProvider;

    private readonly Action<TimeSpan> wait;

    /// <summary>期限と時計・待機を接続します。省略時はOSの単調時計とThread.Sleepを使います。</summary>
    internal ExternalPlayerWaitPolicy(TimeSpan timeout, TimeProvider? timeProvider = null, Action<TimeSpan>? wait = null)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        this.timeout = timeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.wait = wait ?? (interval =>
        {
            if (interval > TimeSpan.Zero) { Thread.Sleep(interval); }
            else { Thread.Yield(); }
        });
    }

    /// <summary>完了・中止を確認してから操作し、単調時計の期限内で再確認します。</summary>
    internal bool WaitUntil(
        Func<bool> completed,
        Func<bool>? aborted,
        Action attempt,
        string timeoutMessage,
        int pollMilliseconds = 10)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(attempt);
        long started = timeProvider.GetTimestamp();
        while (!completed())
        {
            if (aborted?.Invoke() == true)
            {
                return false;
            }
            if (timeProvider.GetElapsedTime(started) >= timeout)
            {
                throw new TimeoutException(timeoutMessage);
            }
            attempt();
            Wait(pollMilliseconds);
        }
        return true;
    }

    /// <summary>終了を待ち、期限の半分で一度だけ強制終了を試みます。終了未確認なら期限で失敗します。</summary>
    internal void WaitForProcessExit(
        Func<bool> hasExited,
        Action forceTerminate,
        string timeoutMessage)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(forceTerminate);
        long started = timeProvider.GetTimestamp();
        var forceTerminateAt = TimeSpan.FromTicks(timeout.Ticks / 2);
        int pollMilliseconds = Math.Max(
            1,
            Math.Min(100, (int)Math.Ceiling(timeout.TotalMilliseconds / 20d)));
        bool forceTerminateAttempted = false;
        while (!hasExited())
        {
            if (!forceTerminateAttempted && timeProvider.GetElapsedTime(started) >= forceTerminateAt)
            {
                forceTerminateAttempted = true;
                try
                {
                    forceTerminate();
                    if (hasExited())
                    {
                        return;
                    }
                }
                catch
                {
                }
            }
            if (timeProvider.GetElapsedTime(started) >= timeout)
            {
                throw new TimeoutException(timeoutMessage);
            }
            Wait(pollMilliseconds);
        }
    }

    /// <summary>フォーカス・キー入力・埋込み後の待機を同じ境界へ渡します。0以下では実行権を譲ります。</summary>
    /// <remarks>
    /// 呼出し側の固定待機は、外部プレイヤーがフォーカス・埋込み・キー入力を処理するための猶予です。
    /// uBMplay はフォーカスとキー送信の前後に50ms、BMIIDXView2015 は埋込み後に100ms、キー押下後に40ms待ちます。
    /// </remarks>
    internal void Wait(int milliseconds) => wait(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));
}
