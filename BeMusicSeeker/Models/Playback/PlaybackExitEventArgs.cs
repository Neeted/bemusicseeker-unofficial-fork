using System;

namespace BeMusicSeeker.Models;

/// <summary>自然終了の必須後処理で生じた故障を、既存の世代付き終了観測へ渡します。</summary>
internal sealed class PlaybackExitEventArgs(Exception failure, bool failureNotificationOwnedByCaller = false) : EventArgs
{
    /// <summary>設定パスと元原因を保持する終了処理の故障です。</summary>
    internal Exception Failure { get; } = failure ?? throw new ArgumentNullException(nameof(failure));

    /// <summary>同じ故障を通知責務付きの例外で返す開始・停止要求が通知を所有します。終了観測は状態解除・通知を先取りしません。</summary>
    internal bool FailureNotificationOwnedByCaller { get; } = failureNotificationOwnedByCaller;
}
