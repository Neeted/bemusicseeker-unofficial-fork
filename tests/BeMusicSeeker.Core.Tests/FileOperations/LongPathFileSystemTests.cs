using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LongPathFileSystemTests
{
    /// <summary>正規化済み包含はdrive/UNCrootと末尾区切りを保持し、要素境界のない接頭辞兄弟を含めません。実ドライブ・共有の列挙は行いません。</summary>
    [TestMethod]
    [DataRow(@"D:\Songs\a.bms", @"D:\", true)]
    [DataRow(@"d:\Songs\a.bms", @"D:\Songs\", true)]
    [DataRow(@"D:\Songs2\a.bms", @"D:\Songs", false)]
    [DataRow(@"\\server\share\Songs\a.bmson", @"\\server\share\", true)]
    [DataRow(@"\\server\share2\Songs\a.bmson", @"\\server\share", false)]
    public void NormalizedDirectoryContainment_RespectsRootAndElementBoundaries(string candidate, string ancestor, bool expected)
        => Assert.AreEqual(expected, LongPathFileSystem.IsSameOrDescendantNormalizedDirectoryPath(candidate, ancestor));

}
