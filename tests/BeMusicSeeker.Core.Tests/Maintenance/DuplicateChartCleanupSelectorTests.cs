using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 同じフォルダ内の重複整理が共通で使う選定規則 <see cref="DuplicateChartCleanupSelector"/> を、実ファイルで確認します。
/// 戻り値は入力の同一インスタンスの集合として比べ、並び順は契約にしません。
/// </summary>
[TestClass]
public sealed class DuplicateChartCleanupSelectorTests
{
    private static readonly DateTime Old = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mid = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChartFile Existing(TestWorkspace workspace, string name, string hash, DateTime time)
    {
        string path = workspace.GetPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, name);
        File.SetLastWriteTimeUtc(path, time);
        return CreateChart(path, hash);
    }

    private static ChartFile Missing(TestWorkspace workspace, string name, string hash)
        => CreateChart(workspace.GetPath(name), hash);

    private static void AssertDeletions(IEnumerable<ChartFile> expected, IEnumerable<ChartFile> charts)
    {
        List<ChartFile> actual = DuplicateChartCleanupSelector.SelectDeletions(charts);
        Assert.AreEqual(expected.Count(), actual.Count);
        foreach (ChartFile chart in expected)
        {
            Assert.AreEqual(1, actual.Count(value => ReferenceEquals(value, chart)), chart.Path);
        }
    }

    /// <summary>全件が実在するとき更新日時が最も古いものだけを残し、残りを削除対象にします。新しい方を残す誤りと、残す件数の誤りを検出します。</summary>
    [TestMethod]
    public void SelectDeletions_AllExisting_KeepsOldest()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile oldest = Existing(workspace, "b.bms", "h", Old);
        ChartFile middle = Existing(workspace, "a.bms", "h", Mid);
        ChartFile newest = Existing(workspace, "c.bms", "h", New);
        AssertDeletions([middle, newest], [middle, oldest, newest]);
    }

    /// <summary>全件が実在し同日時なら、ファイル名が最も短いものを残します。長い名前を残す誤りを検出します。</summary>
    [TestMethod]
    public void SelectDeletions_SameTime_KeepsShortestName()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile longName = Existing(workspace, "long-name.bms", "h", Mid);
        ChartFile shortName = Existing(workspace, "a.bms", "h", Mid);
        ChartFile middleName = Existing(workspace, "middle.bms", "h", Mid);
        AssertDeletions([longName, middleName], [longName, shortName, middleName]);
    }

    /// <summary>更新日時が古く名前が長いものと、新しく名前が短いものでは、古い方を残します。名前の短さを更新日時より優先する誤りを検出します。</summary>
    [TestMethod]
    public void SelectDeletions_OlderLongNameBeatsNewerShortName()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile older = Existing(workspace, "long-name.bms", "h", Old);
        ChartFile newer = Existing(workspace, "a.bms", "h", New);
        AssertDeletions([newer], [older, newer]);
    }

    /// <summary>名前が短い側が欠落した2件では、欠落を残す一件に選ばず、実在する唯一のコピーも削除対象にしません。削除対象は空です。</summary>
    [TestMethod]
    public void SelectDeletions_TwoCharts_MissingShortName_DeletesNothing()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile missing = Missing(workspace, "a.bms", "h");
        ChartFile existing = Existing(workspace, "long-name.bms", "h", Mid);
        AssertDeletions([], [missing, existing]);
    }

    /// <summary>名前が最も短い1件が欠落した3件では、実在する新しい一件だけが削除対象になります。欠落を残す一件に選ぶ誤りと、欠落を削除対象に含める誤りを検出します。</summary>
    [TestMethod]
    public void SelectDeletions_ThreeCharts_MissingShortest_DeletesOnlyNewerExisting()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile missing = Missing(workspace, "a.bms", "h");
        ChartFile older = Existing(workspace, "bb.bms", "h", Old);
        ChartFile newer = Existing(workspace, "cc.bms", "h", New);
        AssertDeletions([newer], [missing, older, newer]);
    }

    /// <summary>全件が欠落していれば、削除対象は空です。</summary>
    [TestMethod]
    public void SelectDeletions_AllMissing_DeletesNothing()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        AssertDeletions([], [Missing(workspace, "a.bms", "h"), Missing(workspace, "b.bms", "h")]);
    }

    /// <summary>
    /// 属性を取得できない候補（不正な文字を含むパス）は、実在が1件または2件のどちらでも残す側にも削除対象にも入りません。
    /// 取得の失敗を最も古い、または最も新しい日時として扱う誤りを検出します。
    /// </summary>
    // existingCount: 属性取得に失敗する候補のほかに実在する件数。
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void SelectDeletions_UnreadableAttributes_AreExcluded(int existingCount)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile unreadable = CreateChart(Path.Combine(workspace.RootDirectory, "bad\0name.bms"), "h");
        ChartFile older = Existing(workspace, "bb.bms", "h", Old);
        List<ChartFile> charts = [unreadable, older];
        ChartFile[] expected = [];
        if (existingCount == 2)
        {
            ChartFile newer = Existing(workspace, "cc.bms", "h", New);
            charts.Add(newer);
            expected = [newer];
        }
        AssertDeletions(expected, charts);
    }

    /// <summary>パスがディレクトリの候補は、実ファイルとして確認できないため除外します。ディレクトリを残す一件に選ぶ誤りと、削除対象に含める誤りを検出します。</summary>
    [TestMethod]
    public void SelectDeletions_DirectoryPath_IsExcluded()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = workspace.GetPath("a.bms");
        Directory.CreateDirectory(directory);
        ChartFile asDirectory = CreateChart(directory, "h");
        ChartFile older = Existing(workspace, "bb.bms", "h", Mid);
        ChartFile newer = Existing(workspace, "cc.bms", "h", New);
        AssertDeletions([newer], [asDirectory, older, newer]);
    }

    /// <summary>主ハッシュごとの判定は独立しています。欠落のある主ハッシュXが、全件実在の主ハッシュYの選定を変えない（Yは通常どおり最古を残す）ことを確かめます。</summary>
    [TestMethod]
    public void SelectDeletions_MissingInOneHash_DoesNotAffectOtherHash()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile xMissing = Missing(workspace, "xa.bms", "x");
        ChartFile xExisting = Existing(workspace, "xb.bms", "x", Mid);
        ChartFile yOlder = Existing(workspace, "yb.bms", "y", Old);
        ChartFile yNewer = Existing(workspace, "ya.bms", "y", New);
        AssertDeletions([yNewer], [xMissing, xExisting, yOlder, yNewer]);
    }

    /// <summary>主ハッシュを持たない譜面と、同じ主ハッシュが1件だけの譜面は対象外です。ファイルの有無によらず削除対象にしません。</summary>
    [TestMethod]
    public void SelectDeletions_NoHashOrSingleHash_AreOutOfScope()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        ChartFile noHashA = Existing(workspace, "n1.bms", string.Empty, Old);
        ChartFile noHashB = Existing(workspace, "n2.bms", string.Empty, New);
        ChartFile single = Missing(workspace, "s.bms", "single");
        AssertDeletions([], [noHashA, noHashB, single]);
    }
}
