using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.DuplicateMergeSafetyTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 統合候補一覧の安全性確認の検査器を、実ジャンクションで確認します。リンクかどうかの判定と、リンク先へ入らない検査はOSの実際の応答に依存するため、
/// 日常検証ではなくこの専用suiteが担当します。手動統合と違い、KEEP側のフォルダの配下のリンクも統合不可にします。
/// </summary>
[TestClass]
[TestCategory("Suite.Junction")]
public sealed class DuplicateMergeSafetyJunctionTests
{
    /// <summary>
    /// 保証：リンク（ジャンクション）を含むグループは統合不可で、リンクの位置を相対パスで返す。KEEP以外の深い隠しフォルダ内、KEEPの配下（手動統合と違いKEEP側でも拒否）、
    /// 二フォルダのグループでフォルダ本体がリンク、リンク先が消えたリンクのどれも該当する。一フォルダのグループは、フォルダ本体がリンクなら統合不可で、配下だけにリンクがあれば安全である。
    /// 検出する誤り：隠しフォルダの配下の見落とし、KEEP側のリンクの許容、本体がリンクのフォルダの通常扱い、リンク先が消えたリンクの不在・通常扱い、
    /// 一フォルダのグループの配下の走査・本体リンクの見逃し。
    /// </summary>
    [TestMethod]
    [DataRow("deep-hidden-in-moving-folder")]
    [DataRow("below-keep-folder")]
    [DataRow("folder-body-of-two-folder-group")]
    [DataRow("broken-link")]
    [DataRow("single-folder-body")]
    [DataRow("single-folder-below")]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void Link_MakesGroupUnmergeableByPositionAndSingleFolderOnlyByBody(string shape)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string a = Path.Combine(workspace.RootDirectory, "g", "a"), b = Path.Combine(workspace.RootDirectory, "g", "b");
        string external = Path.Combine(workspace.RootDirectory, "external");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "target.wav"), "linked audio");
        string link, linkTarget = external, relative;
        string[] folders = [a, b];
        string keep = b;
        switch (shape)
        {
            case "deep-hidden-in-moving-folder":
                string hidden = Path.Combine(a, "Deep", ".hidden");
                Directory.CreateDirectory(Path.Combine(hidden, "Inner"));
                File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
                link = Path.Combine(hidden, "Inner", "Link");
                relative = @"Deep\.hidden\Inner\Link";
                break;
            case "below-keep-folder":
                Directory.CreateDirectory(Path.Combine(a, "Pack"));
                link = Path.Combine(a, "Pack", "Link");
                relative = @"Pack\Link";
                keep = a;
                break;
            case "folder-body-of-two-folder-group":
                Directory.Delete(a);
                link = a;
                linkTarget = Path.Combine(workspace.RootDirectory, "real-a");
                Directory.CreateDirectory(linkTarget);
                relative = string.Empty;
                break;
            case "broken-link":
                Directory.CreateDirectory(Path.Combine(a, "Pack"));
                link = Path.Combine(a, "Pack", "Broken");
                relative = @"Pack\Broken";
                break;
            case "single-folder-body":
                Directory.Delete(a);
                link = a;
                linkTarget = Path.Combine(workspace.RootDirectory, "real-a");
                Directory.CreateDirectory(linkTarget);
                relative = string.Empty;
                folders = [a];
                keep = a;
                break;
            default:
                link = Path.Combine(a, "Link");
                relative = "Link";
                folders = [a];
                keep = a;
                break;
        }

        DirectoryJunctionFixture.Run(workspace, link, linkTarget, () =>
        {
            if (shape == "broken-link") { Directory.Delete(external, recursive: true); }
            (string Path, long Length)[] targetBefore = Directory.Exists(linkTarget)
                ? [.. Directory.EnumerateFileSystemEntries(linkTarget, "*", SearchOption.AllDirectories).Select(path => (path, File.Exists(path) ? new FileInfo(path).Length : -1L))]
                : [];

            DuplicateMergeVerificationResult result = Inspect(folders).Evaluate(keep);

            if (shape == "single-folder-below")
            {
                Assert.AreEqual(DuplicateMergeVerificationStage.Safe, result.Stage, "一フォルダのグループは、配下のリンクだけなら安全です。");
                Assert.AreEqual(0, result.Reasons.Count);
            }
            else
            {
                Assert.AreEqual(DuplicateMergeVerificationStage.Unmergeable, result.Stage);
                DuplicateMergeVerificationReason reason = Reason(result, DuplicateMergeVerificationReasonKind.Link)!;
                Assert.AreEqual(1, reason.Findings.Count, "リンクの先へは入らず、リンク自身だけを返します。");
                Assert.AreEqual(new DuplicateMergeFinding(folders[0], relative), reason.Findings[0]);
            }
            CollectionAssert.AreEqual(targetBefore, Directory.Exists(linkTarget)
                ? Directory.EnumerateFileSystemEntries(linkTarget, "*", SearchOption.AllDirectories).Select(path => (path, File.Exists(path) ? new FileInfo(path).Length : -1L)).ToArray()
                : Array.Empty<(string, long)>(), "リンク先は変わりません。");
        });
    }

    /// <summary>
    /// 保証：リンク先へは入らない。リンク先に別内容の同名ファイル（相手のフォルダの同名のファイルと同じサイズ）とロック中のファイルがあっても、別内容・解析不能は出ず、
    /// 理由はリンクだけで、リンク先の内容も変わらない。
    /// 検出する誤り：リンク先の列挙・読取り（別内容の誤検出、ロック中のファイルの解析不能）、リンク先への書込み。
    /// </summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void Link_DoesNotEnterTheTargetEvenWithDifferentOrLockedSameNameFiles()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string a = Path.Combine(workspace.RootDirectory, "g", "a"), b = Path.Combine(workspace.RootDirectory, "g", "b");
        string external = Path.Combine(workspace.RootDirectory, "external");
        string lockedTargetFile = WriteFile(Path.Combine(external, "x.bin"), PatternBytes(32, 1));
        WriteFile(Path.Combine(b, "Link", "x.bin"), PatternBytes(32, 2));
        Directory.CreateDirectory(a);
        string link = Path.Combine(a, "Link");

        DirectoryJunctionFixture.Run(workspace, link, external, () =>
        {
            byte[] before = File.ReadAllBytes(lockedTargetFile);
            DateTime beforeTime = File.GetLastWriteTimeUtc(lockedTargetFile);
            using (new FileStream(lockedTargetFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                DuplicateMergeVerificationResult result = Inspect([a, b]).Evaluate(a);

                CollectionAssert.AreEqual(new[] { DuplicateMergeVerificationReasonKind.Link }, Kinds(result));
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(lockedTargetFile), "リンク先は変わりません。");
            Assert.AreEqual(beforeTime, File.GetLastWriteTimeUtc(lockedTargetFile));
        });
    }
}
