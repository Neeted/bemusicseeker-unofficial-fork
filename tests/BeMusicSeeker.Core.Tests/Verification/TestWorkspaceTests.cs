using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>ファイル領域の所有を他の資源の寿命から独立して検査します。</summary>
[TestClass]
public sealed class TestWorkspaceTests
{
    /// <summary>通常の相対パスとケース内へ戻るパスを解決し、途中の親参照だけで拒否する誤実装を検出します。</summary>
    [TestMethod]
    [DataRow("value.txt", "value.txt")]
    [DataRow("nested/value.txt", "nested/value.txt")]
    [DataRow("nested/../value.txt", "value.txt")]
    public void RelativePathsResolveWithinOwnedDirectory(string input, string expected)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        Assert.AreEqual(Path.Combine(workspace.RootDirectory, expected.Replace('/', Path.DirectorySeparatorChar)), workspace.GetPath(input));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(workspace.RootDirectory).Length);
    }

    /// <summary>rooted入力と正規化後の領域外参照を拒否し、同じprefixの兄弟領域や既存の外側ファイルへ干渉しないことを確認します。</summary>
    [TestMethod]
    [DataRow("absolute-inside")]
    [DataRow("drive-relative")]
    [DataRow("root-relative")]
    [DataRow("unc")]
    [DataRow("parent")]
    [DataRow("nested-parent")]
    [DataRow("prefix-sibling")]
    public void RootedAndEscapingPathsAreRejected(string condition)
    {
        using var run = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        using var workspace = TestWorkspace.Create(run.RootDirectory);
        string outside = run.GetPath("outside.txt");
        File.WriteAllText(outside, "保持する外側の内容");
        string input = condition switch
        {
            "absolute-inside" => Path.Combine(workspace.RootDirectory, "value.txt"),
            "drive-relative" => "C:value.txt",
            "root-relative" => @"\value.txt",
            "unc" => @"\\server\share\value.txt",
            "parent" => "../outside.txt",
            "nested-parent" => "nested/../../outside.txt",
            "prefix-sibling" => $"../{workspace.CaseId}-sibling/value.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };
        Assert.Throws<Exception>(() => workspace.GetPath(input));
        Assert.AreEqual("保持する外側の内容", File.ReadAllText(outside));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(workspace.RootDirectory).Length);
    }

    /// <summary>同じ実行領域で並行作成しても識別子が衝突せず、解放が兄弟の可変ファイルを削除しないことを保証します。</summary>
    [TestMethod]
    public async Task ParallelCasesOwnDifferentDirectoriesAndPreserveSiblings()
    {
        using var run = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        TestWorkspace[] cases = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => TestWorkspace.Create(run.RootDirectory))));
        try
        {
            Assert.AreEqual(cases.Length, cases.Select(workspace => workspace.CaseId).Distinct().Count());
            foreach (TestWorkspace workspace in cases)
            {
                File.WriteAllText(workspace.GetPath("value"), workspace.CaseId);
            }

            cases[0].Dispose();
            Assert.IsFalse(Directory.Exists(cases[0].RootDirectory));
            foreach (TestWorkspace workspace in cases.Skip(1))
            {
                Assert.AreEqual(workspace.CaseId, File.ReadAllText(workspace.GetPath("value")));
            }
        }
        finally
        {
            foreach (TestWorkspace workspace in cases)
            {
                workspace.Dispose();
            }
        }
    }

    /// <summary>証跡保持を指定した領域はDispose後も内容を保ち、指定しない領域は削除されることを保証します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreserveArtifactsControlsOnlyOwnedDirectoryDeletion(bool preserve)
    {
        using var run = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var workspace = TestWorkspace.Create(run.RootDirectory);
        File.WriteAllText(workspace.GetPath("evidence"), "保存値");
        if (preserve)
        {
            workspace.PreserveArtifacts();
        }

        workspace.Dispose();
        Assert.AreEqual(preserve, Directory.Exists(workspace.RootDirectory));
        if (preserve)
        {
            Assert.AreEqual("保存値", File.ReadAllText(workspace.GetPath("evidence")));
        }

        Assert.IsTrue(Directory.Exists(run.RootDirectory));
    }

    /// <summary>未終端のfile handleが削除を拒む条件を成功扱いせず、元の領域を保持して失敗を呼出元へ伝えます。</summary>
    [TestMethod]
    public void DeletionFailureIsNotHidden()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        using (var held = new FileStream(workspace.GetPath("held"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(workspace.Dispose);
            Assert.IsTrue(Directory.Exists(workspace.RootDirectory));
        }
    }
}
