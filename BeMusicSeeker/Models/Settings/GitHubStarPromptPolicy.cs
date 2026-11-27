#nullable enable
using System;

namespace BeMusicSeeker.Models;

/// <summary>GitHubのスター依頼の保存済みの状態です。</summary>
internal enum GitHubStarPromptState
{
    /// <summary>未終了です。条件を満たせば表示します。</summary>
    Unfinished,

    /// <summary>利用者がスターを付けに行き、依頼を終了しました。</summary>
    Starred,

    /// <summary>利用者が今後表示しないを選び、依頼を終了しました。</summary>
    Declined,

    /// <summary>この版が知らない保存値です。新しい版が書いた値を消さないため、終了として扱い保存しません。</summary>
    Unknown
}

/// <summary>GitHubのスター依頼の画面で利用者が選んだ操作です。</summary>
internal enum GitHubStarPromptChoice
{
    /// <summary>リポジトリをブラウザで開けました。</summary>
    Star,

    /// <summary>あとで（閉じる操作とEscを含む、ブラウザで開けなかった場合も同じ）。</summary>
    Later,

    /// <summary>今後表示しない。</summary>
    Decline
}

/// <summary>GitHubのスター依頼の保存値です。</summary>
/// <param name="State">依頼の状態。</param>
/// <param name="StartupCount">前回の基準時点から数えた起動の回数。</param>
/// <param name="NextEligibleUtc">次に表示できるUTCの日時。未設定はnull。</param>
/// <param name="ShownCount">画面を表示した回数。</param>
internal sealed record GitHubStarPromptRecord(
    GitHubStarPromptState State,
    int StartupCount,
    DateTimeOffset? NextEligibleUtc,
    int ShownCount)
{
    /// <summary>保存値がない場合の値です。</summary>
    internal static GitHubStarPromptRecord Initial { get; } = new(GitHubStarPromptState.Unfinished, 0, null, 0);
}

/// <summary>
/// GitHubのスター依頼の起動の数え方、保存値による表示可否、選択後の値を決めます。
/// 保存・表示・時刻の取得は持たず、呼出元が渡した値だけから結果を返します。
/// </summary>
/// <remarks>
/// しつこさを避けるため、初回は十分に使った後（起動10回かつ14日）にだけ表示し、
/// あとでの後も同じ回数と30日を待ち、表示は合計3回までとします。
/// </remarks>
internal static class GitHubStarPromptPolicy
{
    /// <summary>表示に必要な起動の回数です。数える回数もこの値で頭打ちにします。</summary>
    internal const int RequiredStartupCount = 10;

    /// <summary>表示の上限回数です。</summary>
    internal const int MaxShownCount = 3;

    /// <summary>最初に数えた起動から最初の表示までの待ち時間です。</summary>
    internal static readonly TimeSpan FirstScheduleDelay = TimeSpan.FromDays(14);

    /// <summary>あとでを選んでから次の表示までの待ち時間です。</summary>
    internal static readonly TimeSpan LaterDelay = TimeSpan.FromDays(30);

    /// <summary>未終了で表示回数が上限未満か、つまり起動を数え表示を検討する状態かを返します。</summary>
    internal static bool IsActive(GitHubStarPromptRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.State == GitHubStarPromptState.Unfinished && record.ShownCount < MaxShownCount;
    }

    /// <summary>
    /// 数える起動を一回反映した値を返します。予定が未設定なら <paramref name="nowUtc"/> の14日後を設定し、
    /// 回数は上限まで1を加えます。検討する状態でなければ同じ値を返します。
    /// </summary>
    internal static GitHubStarPromptRecord RecordCountedStartup(GitHubStarPromptRecord record, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsActive(record))
        {
            return record;
        }

        return record with
        {
            NextEligibleUtc = record.NextEligibleUtc ?? nowUtc + FirstScheduleDelay,
            // 手で書き換えた大きな値でも桁あふれさせず、上限で止めます。
            StartupCount = record.StartupCount >= RequiredStartupCount ? RequiredStartupCount : record.StartupCount + 1
        };
    }

    /// <summary>保存値から見て、<paramref name="nowUtc"/> に表示できるかを返します。予定と同時刻は表示できます。</summary>
    internal static bool CanShow(GitHubStarPromptRecord record, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        return IsActive(record)
            && record.StartupCount >= RequiredStartupCount
            && record.NextEligibleUtc is { } nextEligible
            && nowUtc >= nextEligible;
    }

    /// <summary>
    /// 表示が成立した後の選択を反映した値を返します。表示回数に1を加え、スターと今後表示しないは依頼を終了し、
    /// あとでは回数を0へ戻して予定を <paramref name="nowUtc"/> の30日後にします。
    /// </summary>
    internal static GitHubStarPromptRecord ApplyChoice(GitHubStarPromptRecord record, GitHubStarPromptChoice choice, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        GitHubStarPromptRecord shown = record with { ShownCount = record.ShownCount + 1 };
        return choice switch
        {
            GitHubStarPromptChoice.Star => shown with { State = GitHubStarPromptState.Starred },
            GitHubStarPromptChoice.Decline => shown with { State = GitHubStarPromptState.Declined },
            GitHubStarPromptChoice.Later => shown with { StartupCount = 0, NextEligibleUtc = nowUtc + LaterDelay },
            _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown GitHub star prompt choice.")
        };
    }
}
