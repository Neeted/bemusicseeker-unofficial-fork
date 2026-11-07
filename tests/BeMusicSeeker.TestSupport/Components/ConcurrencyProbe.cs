namespace BeMusicSeeker.TestSupport.Components;

/// <summary>
/// lock・gate・UIの競合を観測するprobeの開始と、停止の検出および到達待ちを分けて提供します。
/// 「保持中に別スレッドの処理が完了すること」は、probeを専用スレッドで動かし、保持側が判定まで保持したまま
/// <see cref="StallTimeout"/>以内の完了で判定します。到達・完了の待機は局所期限を持たず、通知か生成元Taskの終端で待ちます。
/// </summary>
internal static class ConcurrencyProbe
{
    /// <summary>
    /// ロック不成立・デッドロック・UI閉塞だけを検出する停止検出の期限です。到達・完了の待機には使いません。
    /// 保持側が判定まで保持するため、この値は正しい実装の合否に関与しません（完了は即座に観測されます）。
    /// 誤実装が使いうる製品側の有限待機（60秒）より短くし、待った後に完了する誤実装を見逃しません。
    /// </summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>ThreadPoolの起動遅延と枯渇に依存しないよう、probeを専用スレッドで開始します。</summary>
    internal static Task StartDedicated(Action action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>ThreadPoolの起動遅延と枯渇に依存しないよう、結果を返すprobeを専用スレッドで開始します。</summary>
    internal static Task<T> StartDedicated<T>(Func<T> action)
    {
        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>ThreadPoolの起動遅延と枯渇に依存しないよう、非同期処理を専用スレッドから開始し、内側の非同期終端まで一つのTaskで回収します。</summary>
    internal static Task StartDedicatedAsync(Func<Task> action)
    {
        return Task.Factory.StartNew(
                action,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>
    /// 保持中にprobeが<see cref="StallTimeout"/>以内に完了することを表明します。
    /// probeが失敗していれば元の例外を再送出し、期限超過とは取り違えません。<paramref name="holder"/>が
    /// probeより先に終端した場合は、保持が判定前に外れたため即座に失敗します。待機は<see cref="Task.WaitAny(Task[], TimeSpan)"/>による同期待機で行い、poolのタイマーに依存しません。
    /// 呼出し側は保持を解放した後でprobeを必ず回収します。
    /// </summary>
    internal static void AssertCompletesWhileHeld(Task probe, string name, Task? holder = null)
    {
        if (!probe.IsCompleted)
        {
            Task[] waits = holder is null ? [probe] : [probe, holder];
            Task.WaitAny(waits, StallTimeout);
        }

        EvaluateHeld(probe, name, holder);
    }

    /// <summary><see cref="AssertCompletesWhileHeld"/>の非同期版です。待機は<see cref="Task.WhenAny(Task[])"/>で行い、同じ意味を持ちます。</summary>
    internal static async Task AssertCompletesWhileHeldAsync(Task probe, string name, Task? holder = null)
    {
        if (!probe.IsCompleted)
        {
            using var delayCancellation = new CancellationTokenSource();
            var delay = Task.Delay(StallTimeout, delayCancellation.Token);
            Task[] waits = holder is null ? [probe, delay] : [probe, holder, delay];
            await Task.WhenAny(waits).ConfigureAwait(false);
            delayCancellation.Cancel();
        }

        EvaluateHeld(probe, name, holder);
    }

    /// <summary>
    /// 保持中に<paramref name="signal"/>が<see cref="StallTimeout"/>以内に立つことを表明します。
    /// <paramref name="producer"/>が通知より先に失敗すれば元の例外を再送出し、通知なしで正常終了すれば失敗にします。
    /// producerがnullの場合はsignalだけを待ちます。
    /// </summary>
    internal static void AssertSignalsWhileHeld(ManualResetEventSlim signal, Task? producer, string name)
    {
        if (!signal.IsSet)
        {
            if (producer is null)
            {
                signal.Wait(StallTimeout);
            }
            else
            {
                WaitHandle.WaitAny([signal.WaitHandle, ((IAsyncResult)producer).AsyncWaitHandle], StallTimeout);
            }
        }

        if (signal.IsSet)
        {
            return;
        }

        if (producer is { IsCompleted: true })
        {
            ThrowTerminatedWithoutSignal(producer, name);
        }

        Assert.Fail($"{Label(name)}: 保持中に{StallTimeout.TotalSeconds:0}秒以内に通知されませんでした（停止の疑い）。");
    }

    /// <summary>
    /// 到達を期限なしで待ちます。<paramref name="signal"/>か<paramref name="producer"/>の終端のどちらかで戻り、
    /// 通知なしで終端した場合はproducerの失敗を伝播し、正常終了なら失敗にします。停止は全体の制限時間で検出します。
    /// </summary>
    internal static void WaitForSignal(ManualResetEventSlim signal, Task producer, string name)
    {
        if (!signal.IsSet)
        {
            WaitHandle.WaitAny([signal.WaitHandle, ((IAsyncResult)producer).AsyncWaitHandle]);
        }

        if (!signal.IsSet)
        {
            ThrowTerminatedWithoutSignal(producer, name);
        }
    }

    /// <summary>
    /// 条件の成立を期限なしで待ちます。<paramref name="producer"/>が条件より先に終端した場合の扱いは
    /// <see cref="WaitForSignal"/>と同じです。
    /// </summary>
    internal static void WaitUntil(Func<bool> condition, Task producer, string name)
    {
        var spin = new SpinWait();
        while (!condition())
        {
            if (producer.IsCompleted && !condition())
            {
                ThrowTerminatedWithoutSignal(producer, name);
            }

            spin.SpinOnce();
        }
    }

    private static void EvaluateHeld(Task probe, string name, Task? holder)
    {
        if (probe.IsCompleted)
        {
            probe.GetAwaiter().GetResult();
            return;
        }

        if (holder is { IsCompleted: true })
        {
            holder.GetAwaiter().GetResult();
            Assert.Fail($"{Label(name)}: 保持側が判定前に終端しました。");
        }

        Assert.Fail($"{Label(name)}: 保持中に{StallTimeout.TotalSeconds:0}秒以内に完了しませんでした（停止の疑い）。");
    }

    private static void ThrowTerminatedWithoutSignal(Task producer, string name)
    {
        producer.GetAwaiter().GetResult();
        Assert.Fail($"{Label(name)}: 通知なしで終端しました。");
    }

    /// <summary>呼出し側の表明文言を診断の見出しとして連結するため、末尾の句点を除きます。</summary>
    private static string Label(string name) => name.TrimEnd('。', '.', ' ');
}
