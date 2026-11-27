using BeMusicSeeker.Models;

namespace BeMusicSeeker.Tests;

/// <summary>GitHubのスター依頼の起動の数え方・表示可否・選択の反映を、保存値と時刻だけから確認します。</summary>
[TestClass]
public sealed class GitHubStarPromptPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 保証：数える起動は、予定が未設定のときだけ14日後を設定し、設定済みの予定（過去でも）は変えず、回数を10で頭打ちにする。
    /// 起動のたびに予定を延ばす誤り、10を超えて数え続ける誤り、頭打ち後に値を変えて保存を誘う誤り、上限を超える保存値で桁あふれする誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(0, null, 1, 14d, DisplayName = "初回は回数1と14日後の予定")]
    [DataRow(3, -100d, 4, -100d, DisplayName = "過去の予定は変えない")]
    [DataRow(9, 5d, 10, 5d, DisplayName = "9から10へ")]
    [DataRow(10, 5d, 10, 5d, DisplayName = "10で頭打ち")]
    [DataRow(int.MaxValue, 5d, 10, 5d, DisplayName = "手で書き換えた最大値も桁あふれせず10")]
    public void CountedStartup_SchedulesFourteenDaysOnceAndCapsCountAtTen(int count, double? scheduleDays, int expectedCount, double expectedScheduleDays)
    {
        GitHubStarPromptRecord record = new(GitHubStarPromptState.Unfinished, count, scheduleDays is { } days ? Now.AddDays(days) : null, 1);

        GitHubStarPromptRecord counted = GitHubStarPromptPolicy.RecordCountedStartup(record, Now);

        Assert.AreEqual(expectedCount, counted.StartupCount);
        Assert.AreEqual(Now.AddDays(expectedScheduleDays), counted.NextEligibleUtc);
        Assert.AreEqual(GitHubStarPromptState.Unfinished, counted.State);
        Assert.AreEqual(1, counted.ShownCount);
        if (count == 10)
        {
            Assert.AreEqual(record, counted, "頭打ちで予定が設定済みなら値は変わりません。");
        }
    }

    /// <summary>
    /// 保証：依頼の終了（スター・今後表示しない・未知の状態）と表示回数の上限到達では、数える起動でも値を変えず、表示もしない。
    /// 終了後も回数や予定を書き換える誤りと、上限後に再表示する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(nameof(GitHubStarPromptState.Starred), 0, DisplayName = "スター")]
    [DataRow(nameof(GitHubStarPromptState.Declined), 0, DisplayName = "今後表示しない")]
    [DataRow(nameof(GitHubStarPromptState.Unknown), 0, DisplayName = "未知の状態")]
    [DataRow(nameof(GitHubStarPromptState.Unfinished), 3, DisplayName = "表示3回")]
    [DataRow(nameof(GitHubStarPromptState.Unfinished), 5, DisplayName = "表示5回")]
    public void FinishedOrShownLimitState_ChangesNothing(string stateName, int shownCount)
    {
        GitHubStarPromptState state = Enum.Parse<GitHubStarPromptState>(stateName);
        GitHubStarPromptRecord record = new(state, 10, Now.AddDays(-1), shownCount);

        Assert.IsFalse(GitHubStarPromptPolicy.IsActive(record));
        Assert.AreSame(record, GitHubStarPromptPolicy.RecordCountedStartup(record, Now));
        Assert.IsFalse(GitHubStarPromptPolicy.CanShow(record, Now));
    }

    /// <summary>
    /// 保証：保存値による表示は、未終了・表示回数3未満・回数10以上・予定以降をすべて満たすときだけ可能で、予定と同時刻は表示できる。
    /// 境界の取り違え（回数9で表示、同時刻で見送り、表示回数3で表示）を検出する。
    /// </summary>
    [TestMethod]
    [DataRow(10, 0d, 0, true, DisplayName = "予定と同時刻")]
    [DataRow(10, -1d, 2, true, DisplayName = "予定の後で表示2回")]
    [DataRow(9, -1d, 0, false, DisplayName = "回数9")]
    [DataRow(10, 0.001d, 0, false, DisplayName = "予定の前")]
    [DataRow(10, -1d, 3, false, DisplayName = "表示3回")]
    public void Eligibility_RequiresAllSavedConditionsWithInclusiveSchedule(int count, double scheduleDaysFromNow, int shownCount, bool expected)
    {
        GitHubStarPromptRecord record = new(GitHubStarPromptState.Unfinished, count, Now.AddDays(scheduleDaysFromNow), shownCount);

        Assert.AreEqual(expected, GitHubStarPromptPolicy.CanShow(record, Now));
    }

    /// <summary>
    /// 保証：表示後の選択は表示回数に1を加え、スターと今後表示しないは依頼を終了し、あとでは回数0と30日後の予定にする。
    /// あとでで回数を戻さない誤り、予定の基準を前回の予定にする誤り、終了の状態の取り違えを検出する。
    /// </summary>
    [TestMethod]
    public void Choice_AddsShownCountAndAppliesFinishOrLater()
    {
        GitHubStarPromptRecord shown = new(GitHubStarPromptState.Unfinished, 10, Now.AddDays(-3), 1);

        GitHubStarPromptRecord star = GitHubStarPromptPolicy.ApplyChoice(shown, GitHubStarPromptChoice.Star, Now);
        GitHubStarPromptRecord decline = GitHubStarPromptPolicy.ApplyChoice(shown, GitHubStarPromptChoice.Decline, Now);
        GitHubStarPromptRecord later = GitHubStarPromptPolicy.ApplyChoice(shown, GitHubStarPromptChoice.Later, Now);

        Assert.AreEqual(GitHubStarPromptState.Starred, star.State);
        Assert.AreEqual(2, star.ShownCount);
        Assert.AreEqual(GitHubStarPromptState.Declined, decline.State);
        Assert.AreEqual(2, decline.ShownCount);
        Assert.AreEqual(new GitHubStarPromptRecord(GitHubStarPromptState.Unfinished, 0, Now.AddDays(30), 2), later);
    }

    /// <summary>
    /// 保証：合意した流れを時刻の列で通す。初回は数える起動10回と14日の両方がそろって初めて表示し、
    /// あとでの後は起動10回と30日の両方が必要で、表示は合計3回までとなる。
    /// 片方の条件だけで表示する誤り、あとでの待ちを14日にする誤り、4回目を表示する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void AgreedLifecycle_FirstShowAfterTenStartsAndFourteenDays_LaterNeedsTenStartsAndThirtyDays_AtMostThreeShows()
    {
        GitHubStarPromptRecord record = GitHubStarPromptRecord.Initial;
        DateTimeOffset start = Now;

        // 初日に10回起動しても14日に達しないため表示しません。
        for (int i = 0; i < 10; i++)
        {
            record = GitHubStarPromptPolicy.RecordCountedStartup(record, start);
            Assert.IsFalse(GitHubStarPromptPolicy.CanShow(record, start), "初日の起動 " + (i + 1));
        }
        Assert.IsFalse(GitHubStarPromptPolicy.CanShow(GitHubStarPromptPolicy.RecordCountedStartup(record, start.AddDays(14).AddTicks(-1)), start.AddDays(14).AddTicks(-1)));
        record = GitHubStarPromptPolicy.RecordCountedStartup(record, start.AddDays(14));
        Assert.IsTrue(GitHubStarPromptPolicy.CanShow(record, start.AddDays(14)), "10回かつ14日で表示します。");

        // 14日経っても起動が9回なら表示しません。
        GitHubStarPromptRecord fewStarts = GitHubStarPromptRecord.Initial;
        for (int i = 0; i < 9; i++)
        {
            fewStarts = GitHubStarPromptPolicy.RecordCountedStartup(fewStarts, start.AddDays(i * 3));
        }
        Assert.IsFalse(GitHubStarPromptPolicy.CanShow(fewStarts, start.AddDays(30)));

        DateTimeOffset shownAt = start.AddDays(14);
        for (int show = 1; show <= 3; show++)
        {
            Assert.IsTrue(GitHubStarPromptPolicy.CanShow(record, shownAt), "表示 " + show);
            record = GitHubStarPromptPolicy.ApplyChoice(record, GitHubStarPromptChoice.Later, shownAt);
            Assert.AreEqual(show, record.ShownCount);

            // あとでの後は、30日経っても起動が9回なら表示しません。
            for (int i = 0; i < 9; i++)
            {
                record = GitHubStarPromptPolicy.RecordCountedStartup(record, shownAt.AddDays(1));
            }
            Assert.IsFalse(GitHubStarPromptPolicy.CanShow(record, shownAt.AddDays(31)), "起動9回 " + show);
            record = GitHubStarPromptPolicy.RecordCountedStartup(record, shownAt.AddDays(2));
            Assert.IsFalse(GitHubStarPromptPolicy.CanShow(record, shownAt.AddDays(30).AddTicks(-1)), "30日前 " + show);
            shownAt = shownAt.AddDays(30);
        }

        Assert.AreEqual(3, record.ShownCount);
        Assert.IsFalse(GitHubStarPromptPolicy.CanShow(record, shownAt.AddDays(365)), "4回目は表示しません。");
        Assert.AreSame(record, GitHubStarPromptPolicy.RecordCountedStartup(record, shownAt.AddDays(365)));
    }
}
