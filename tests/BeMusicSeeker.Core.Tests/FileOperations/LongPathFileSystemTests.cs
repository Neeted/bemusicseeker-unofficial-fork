using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LongPathFileSystemTests
{
    /// <summary>ディレクトリ改名の大小文字変更に通常ファイルを渡した場合は拒否し、元の表記と内容を保持します。</summary>
    [TestMethod]
    public void MoveDirectory_CaseOnlyRejectsFileSourceWithoutRenaming()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string source = Path.Combine(workspace.RootDirectory, "song");
        string destination = Path.Combine(workspace.RootDirectory, "Song");
        File.WriteAllText(source, "preserved");
        Assert.ThrowsExactly<IOException>(() => LongPathFileSystem.MoveDirectory(source, destination, overwrite: false));
        CollectionAssert.AreEqual(new[] { source }, Directory.EnumerateFiles(workspace.RootDirectory).ToArray());
        Assert.AreEqual("preserved", File.ReadAllText(source));
    }

    /// <summary>外部で実表記が変わりDBの表記が古いまま実表記を入力した場合のように、大小文字だけ異なる両パスが同一の物理フォルダを指すなら宛先衝突とせず、同名の完全一致や別フォルダは衝突、存在しない宛先は衝突なしとすることを確認します。</summary>
    [TestMethod]
    public void HasDirectoryMoveDestinationConflict_SamePhysicalDirectoryWithDifferentSpellingIsNotConflict()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string actual = Path.Combine(workspace.RootDirectory, "Song");
        Directory.CreateDirectory(actual);
        string stale = Path.Combine(workspace.RootDirectory, "song");
        Assert.IsFalse(LongPathFileSystem.HasDirectoryMoveDestinationConflict(stale, actual));
        Assert.IsTrue(LongPathFileSystem.HasDirectoryMoveDestinationConflict(actual, actual));
        Assert.IsFalse(LongPathFileSystem.HasDirectoryMoveDestinationConflict(actual, Path.Combine(workspace.RootDirectory, "missing")));
        string other = Path.Combine(workspace.RootDirectory, "Other");
        Directory.CreateDirectory(other);
        Assert.IsTrue(LongPathFileSystem.HasDirectoryMoveDestinationConflict(actual, other));
    }

    /// <summary>代替方式のネイティブ構造体がBY_HANDLE_FILE_INFORMATIONと同じ52バイトであり、識別子の読取り位置がずれないことを確認します。</summary>
    [TestMethod]
    public void ByHandleFileInformation_MatchesNativeSize()
        => Assert.AreEqual(52, System.Runtime.InteropServices.Marshal.SizeOf<LongPathFileSystem.ByHandleFileInformation>());

    /// <summary>代替方式(GetFileInformationByHandle)でも、同一ディレクトリを別表記で読むと識別子が一致し、別ディレクトリでは異なり、存在しないパスは読取りに失敗することを確認します。</summary>
    [TestMethod]
    public void TryReadDirectoryIdentity_LegacyDistinguishesDirectories()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string first = Path.Combine(workspace.RootDirectory, "Song");
        string second = Path.Combine(workspace.RootDirectory, "Other");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Assert.IsTrue(LongPathFileSystem.TryReadDirectoryIdentity(first, true, out (ulong Volume, ulong IdLow, ulong IdHigh) a));
        Assert.IsTrue(LongPathFileSystem.TryReadDirectoryIdentity(Path.Combine(workspace.RootDirectory, "song"), true, out (ulong Volume, ulong IdLow, ulong IdHigh) sameDirectory));
        Assert.IsTrue(LongPathFileSystem.TryReadDirectoryIdentity(second, true, out (ulong Volume, ulong IdLow, ulong IdHigh) b));
        Assert.AreEqual(a, sameDirectory);
        Assert.AreNotEqual(a, b);
        Assert.IsFalse(LongPathFileSystem.TryReadDirectoryIdentity(Path.Combine(workspace.RootDirectory, "missing"), true, out _));
    }

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
