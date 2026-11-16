using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2DirectoryScopeMatcherTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>ケース開始後だけ利用できる、可変ファイル領域の所有者です。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>constructorと既存field準備の成功後にケース専有領域を取得します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>既存finallyと接続・Task終端の後にケース専有領域を回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>
    /// 対象ディレクトリと子孫だけを範囲内と判定し、共通接頭辞を持つ別フォルダを誤って含む境界判定を検出します。
    /// </summary>
    [TestMethod]
    public void ContainsDirectory_MatchesScopeAndDescendantsOnly()
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BMS");
        string scopeDirectory = Path.Combine(root, "Output");
        var matcher = Lr2DirectoryScopeMatcher.Create([scopeDirectory]);

        Assert.IsTrue(matcher.ContainsDirectory(scopeDirectory));
        Assert.IsTrue(matcher.ContainsDirectory(Path.Combine(scopeDirectory, "Playlist")));
        Assert.IsFalse(matcher.ContainsDirectory(Path.Combine(root, "OutputOther")));
    }

    /// <summary>
    /// ファイルの親ディレクトリで同期範囲を判断し、子孫のファイルを落とす誤りと似た名前の隣接範囲への混入を検出します。
    /// </summary>
    [TestMethod]
    public void ContainsFilePath_UsesParentDirectoryScope()
    {
        string root = Path.Combine(CaseWorkspace.RootDirectory, "BMS");
        string scopeDirectory = Path.Combine(root, "Output");
        var matcher = Lr2DirectoryScopeMatcher.Create([scopeDirectory]);

        Assert.IsTrue(matcher.ContainsFilePath(Path.Combine(scopeDirectory, "Playlist", "item.lr2folder")));
        Assert.IsFalse(matcher.ContainsFilePath(Path.Combine(root, "OutputOther", "item.lr2folder")));
    }

    /// <summary>
    /// ドライブのルートを同期範囲にすると直下のディレクトリを含み、末尾区切りを持つルートの誤除外を検出します。
    /// </summary>
    [TestMethod]
    public void ContainsDirectory_MatchesDriveRootDescendant()
    {
        string root = Path.GetPathRoot(CaseWorkspace.RootDirectory)!;
        var matcher = Lr2DirectoryScopeMatcher.Create([root]);

        Assert.IsTrue(matcher.ContainsDirectory(Path.Combine(root, "BMS")));
    }
}
