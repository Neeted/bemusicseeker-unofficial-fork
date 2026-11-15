using System.Globalization;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Core.Tests.Settings;

/// <summary>フォントの選択欄に並べる候補の、表示名・並び順・保存する名前の規則を確認します。</summary>
[TestClass]
public sealed class AppFontChoiceTests
{
    private static readonly AppInstalledFontFamily[] Installed =
    [
        new("Zeta Sans", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en-us"] = "Zeta Sans", ["ja-jp"] = "あさひ" }),
        new("Beta Serif", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en-us"] = "Beta Serif" }),
        new("Alpha Mono", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["ja"] = "かえで" })
    ];

    /// <summary>
    /// 表示言語の名前があるフォントはその名前、ないフォントは基本の名前を表示名にして表示名の順に並べ、保存する名前は基本の名前のままであることを確認します。
    /// </summary>
    /// <remarks>表示名を保存値にする誤りと、表示言語の名前がないフォントを候補から落とす誤りを検出します。</remarks>
    [TestMethod]
    public void BuildFontChoices_OrdersByDisplayLanguageNameAndKeepsBaseNameAsSavedValue()
    {
        IReadOnlyList<AppFontChoice> japanese = AppFontService.BuildFontChoices(Installed, CultureInfo.GetCultureInfo("ja-JP"), string.Empty);
        // 文字種が混ざる並び順は比較規則の細部に依存するため、日本語では表示名と保存する名前の対応だけを確認する。
        CollectionAssert.AreEquivalent(
            new[] { ("Zeta Sans", "あさひ"), ("Beta Serif", "Beta Serif"), ("Alpha Mono", "かえで") },
            japanese.Select(choice => (choice.Name, choice.DisplayName)).ToArray());

        IReadOnlyList<AppFontChoice> english = AppFontService.BuildFontChoices(Installed, CultureInfo.GetCultureInfo("en-US"), string.Empty);
        CollectionAssert.AreEqual(
            new[] { ("Alpha Mono", "Alpha Mono"), ("Beta Serif", "Beta Serif"), ("Zeta Sans", "Zeta Sans") },
            english.Select(choice => (choice.Name, choice.DisplayName)).ToArray());
    }

    /// <summary>
    /// 保存済みの名前がインストール済みのフォントにない場合はその名前を候補に残し、インストール済みの名前と空文字（既定）では候補を増やさないことを確認します。
    /// </summary>
    /// <remarks>未インストールの保存値で選択欄の選択が空になる誤りと、同じフォントを重複して並べる誤りを検出します。</remarks>
    [TestMethod]
    public void BuildFontChoices_KeepsUninstalledSavedNameWithoutDuplicatingInstalledOnes()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");

        IReadOnlyList<AppFontChoice> missing = AppFontService.BuildFontChoices(Installed, culture, "  Removed Font  ");
        Assert.AreEqual(Installed.Length + 1, missing.Count);
        AppFontChoice kept = missing.Single(choice => choice.Name == "Removed Font");
        Assert.AreEqual("Removed Font", kept.DisplayName);

        Assert.AreEqual(Installed.Length, AppFontService.BuildFontChoices(Installed, culture, "Beta Serif").Count);
        Assert.AreEqual(Installed.Length, AppFontService.BuildFontChoices(Installed, culture, string.Empty).Count);
    }
}
