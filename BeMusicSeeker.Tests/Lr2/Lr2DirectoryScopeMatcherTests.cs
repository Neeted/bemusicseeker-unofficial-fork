using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2DirectoryScopeMatcherTests
{
    /// <summary>
    /// 対象ディレクトリと子孫だけを範囲内と判定し、共通接頭辞を持つ別フォルダを誤って含む境界判定を検出します。
    /// </summary>
    [TestMethod]
    public void ContainsDirectory_MatchesScopeAndDescendantsOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "BMS");
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
        string root = Path.Combine(Path.GetTempPath(), "BMS");
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
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        var matcher = Lr2DirectoryScopeMatcher.Create([root]);

        Assert.IsTrue(matcher.ContainsDirectory(Path.Combine(root, "BMS")));
    }
}
