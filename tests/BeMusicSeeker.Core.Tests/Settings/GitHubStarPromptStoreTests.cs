using System.Globalization;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.Tests;

/// <summary>GitHubのスター依頼の保存値を、実際のproviderと専有の設定ファイルで確認します。</summary>
[TestClass]
public sealed class GitHubStarPromptStoreTests
{
    /// <summary>
    /// 保証：項目のない既存の設定は、未終了・回数0・予定なし・表示0回として読む。
    /// 欠損を終了や不正として扱い、既存利用者に依頼が届かなくなる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void MissingKeys_ReadAsUnfinishedWithZeroCountsAndNoSchedule()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        var store = new SettingsGitHubStarPromptStore(() => TestSettingsFactory.OpenSettings(files.Path));

        Assert.AreEqual(GitHubStarPromptRecord.Initial, store.Read());
    }

    /// <summary>
    /// 保証：保存値は異なる文化圏で書いても、状態は Starred・Declined・空の文字列、予定はInvariantCultureの往復形式（O）のUTCで保存され、
    /// 新しい読取り口から同じ値（UTC）で読み戻せる。現在の文化圏の暦・書式で日時を書く誤りと、状態の表記の取り違えを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("ja-JP", DisplayName = "ja-JP")]
    [DataRow("th-TH", DisplayName = "th-TH（仏暦）")]
    public void SavedValues_RoundTripInDocumentedFormat(string cultureName)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var cases = new (GitHubStarPromptRecord Record, string State)[]
            {
                (new(GitHubStarPromptState.Unfinished, 7, new DateTimeOffset(2026, 12, 12, 3, 4, 5, TimeSpan.FromHours(9)).AddTicks(1234567), 2), ""),
                (new(GitHubStarPromptState.Starred, 10, new DateTimeOffset(2027, 1, 2, 0, 0, 0, TimeSpan.Zero), 1), "Starred"),
                (new(GitHubStarPromptState.Declined, 0, null, 3), "Declined")
            };
            foreach ((GitHubStarPromptRecord record, string state) in cases)
            {
                using var files = new SettingsFiles();
                File.WriteAllText(files.Path, Config("Lang", "en-US"));
                new SettingsGitHubStarPromptStore(() => TestSettingsFactory.OpenSettings(files.Path)).Save(record);

                GitHubStarPromptRecord loaded = new SettingsGitHubStarPromptStore(() => TestSettingsFactory.OpenSettings(files.Path)).Read();
                Assert.AreEqual(record, loaded, state);
                Assert.AreEqual(TimeSpan.Zero, loaded.NextEligibleUtc?.Offset ?? TimeSpan.Zero, "読み戻した予定はUTCです。");
                Assert.AreEqual(state, RawValue(files.Path, nameof(Settings.GitHubStarPromptState)));
                Assert.AreEqual(record.StartupCount.ToString(CultureInfo.InvariantCulture), RawValue(files.Path, nameof(Settings.GitHubStarPromptStartupCount), "0"));
                Assert.AreEqual(record.ShownCount.ToString(CultureInfo.InvariantCulture), RawValue(files.Path, nameof(Settings.GitHubStarPromptShownCount), "0"));
                string expectedSchedule = record.NextEligibleUtc is { } schedule
                    ? schedule.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
                    : "";
                Assert.AreEqual(expectedSchedule, RawValue(files.Path, nameof(Settings.GitHubStarPromptNextEligibleUtc)));
                Assert.IsTrue(expectedSchedule.Length == 0 || expectedSchedule.EndsWith('Z'), "予定はUTCの往復形式です。");
                Assert.AreEqual("en-US", RawValue(files.Path, "Lang"), "他の設定キーは保持します。");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// 保証：解釈できない予定・負や整数でない回数・未知の状態を、未設定・0・終了として読み、読むだけでファイルを書き換えない。
    /// 新しい版が書いた状態を古い版で消す誤りと、不正値で読取りが失敗して起動の計数ごと止まる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("FutureState", "-1", "abc", "not-a-date", nameof(GitHubStarPromptState.Unknown), DisplayName = "未知の状態と負の回数")]
    [DataRow("", "abc", "-5", "", nameof(GitHubStarPromptState.Unfinished), DisplayName = "整数でない回数と空の予定")]
    [DataRow("", "1.5", "2147483648", "2026-13-40", nameof(GitHubStarPromptState.Unfinished), DisplayName = "小数・桁あふれ・存在しない日付")]
    public void InvalidOrUnknownValues_ReadAsSpecifiedWithoutRewriting(string state, string startupCount, string shownCount, string schedule, string expectedStateName)
    {
        GitHubStarPromptState expectedState = Enum.Parse<GitHubStarPromptState>(expectedStateName);
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path,
            "<configuration><userSettings><BeMusicSeeker.Properties.Settings>"
            + Setting(nameof(Settings.GitHubStarPromptState), state)
            + Setting(nameof(Settings.GitHubStarPromptStartupCount), startupCount)
            + Setting(nameof(Settings.GitHubStarPromptShownCount), shownCount)
            + Setting(nameof(Settings.GitHubStarPromptNextEligibleUtc), schedule)
            + "</BeMusicSeeker.Properties.Settings></userSettings></configuration>");
        byte[] before = File.ReadAllBytes(files.Path);

        GitHubStarPromptRecord loaded = new SettingsGitHubStarPromptStore(() => TestSettingsFactory.OpenSettings(files.Path)).Read();

        Assert.AreEqual(new GitHubStarPromptRecord(expectedState, 0, null, 0), loaded);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path), "読取りだけでは設定ファイルを書き換えません。");
        Assert.ThrowsExactly<InvalidOperationException>(() => new SettingsGitHubStarPromptStore(() => TestSettingsFactory.OpenSettings(files.Path)).Save(loaded with { State = GitHubStarPromptState.Unknown }));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(files.Path), "未知の状態は保存しません。");
    }

    /// <summary>
    /// 保証：設定ファイルへ書けない場合、保存は例外を呼出元へ伝え、確定値を変えない。
    /// 失敗を握りつぶして、所有者が保存済みと誤認したまま表示へ進む誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SaveFailure_PropagatesToCaller()
    {
        using var files = new SettingsFiles();
        File.WriteAllText(files.Path, Config("Lang", "en-US"));
        Settings settings = TestSettingsFactory.OpenSettings(files.Path);
        File.SetAttributes(files.Path, FileAttributes.ReadOnly);
        var store = new SettingsGitHubStarPromptStore(() => settings);

        Assert.ThrowsExactly<PortableSettingsException>(() => store.Save(new GitHubStarPromptRecord(GitHubStarPromptState.Unfinished, 1, DateTimeOffset.UnixEpoch, 0)));
        Assert.AreEqual(GitHubStarPromptRecord.Initial, store.Read(), "保存の失敗では確定値を変えません。");
    }

    /// <summary>保存された生の値を返します。既定値と同じ値は保存処理が書かないため、項目がなければ既定値を返します。</summary>
    private static string RawValue(string path, string key, string defaultValue = "") =>
        XDocument.Load(path).Descendants("setting").SingleOrDefault(element => (string?)element.Attribute("name") == key)?.Element("value")!.Value
        ?? defaultValue;

    private static string Setting(string key, string value) =>
        $"<setting name=\"{key}\" serializeAs=\"String\"><value>{value}</value></setting>";

    private static string Config(string key, string value) =>
        "<configuration><userSettings><BeMusicSeeker.Properties.Settings>" + Setting(key, value) + "</BeMusicSeeker.Properties.Settings></userSettings></configuration>";
}
