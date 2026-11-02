using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
[TestCategory("Suite.Junction")]
public sealed class LongPathFileSystemTests
{
    /// <summary>既定のコピー呼出しでは安全な非循環ジャンクションを従来どおり辿り、通常ディレクトリとして内容をコピーして元リンクと先を保持します。</summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void CopyDirectory_DefaultFollowsNonCyclicDirectoryLink()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = workspace.RootDirectory;
        string source = Path.Combine(root, "source");
        string target = Path.Combine(root, "target");
        string destination = Path.Combine(root, "destination");
        string link = Path.Combine(source, "link");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sentinel.txt"), "target-content");
        DirectoryJunctionFixture.Run(workspace, link, target, () =>
        {
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.AreEqual(target, new DirectoryInfo(link).ResolveLinkTarget(returnFinalTarget: true)?.FullName, ignoreCase: true);
            Assert.AreEqual(link, LongPathFileSystem.ConfirmDirectoryPathSpelling(Path.Combine(root, "SOURCE", "LINK")), "リンク登録位置を最終リンク先で置換しません。");
            LongPathFileSystem.CopyDirectory(source, destination, overwrite: false);

            Assert.AreEqual("target-content", File.ReadAllText(Path.Combine(destination, "link", "sentinel.txt")));
            Assert.IsFalse((File.GetAttributes(Path.Combine(destination, "link")) & FileAttributes.ReparsePoint) != 0);
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.AreEqual("target-content", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        });
    }
}
