using System;
using System.Linq;

namespace BeMusicSeeker.Models;

/// <summary>自然終了から移譲した復元故障の通知責務を、表示世代が失効しても返却先へ渡します。</summary>
internal sealed class PlaybackExitFailureException : Exception
{
    /// <summary>返却する例外全体を保持し、独立した原因やpathを失わず通知責務を付けます。</summary>
    /// <param name="failure">同じ要求で確定した元の返却例外。backupと終了引数は元の復元原因を保持します。</param>
    internal PlaybackExitFailureException(Exception failure)
        : base("Playback exit restoration failure must be reported by the caller.", failure ?? throw new ArgumentNullException(nameof(failure)))
    {
    }

    /// <summary>後段の単独・複合包装をたどり、自然終了から移譲された通知責務だけを判定します。</summary>
    /// <param name="failure">開始側で最終処理する例外。</param>
    internal static bool RequiresNotification(Exception failure) => failure != null
        && (failure is PlaybackExitFailureException
            || (failure is AggregateException aggregate
                ? aggregate.InnerExceptions.Any(RequiresNotification)
                : RequiresNotification(failure.InnerException)));
}
