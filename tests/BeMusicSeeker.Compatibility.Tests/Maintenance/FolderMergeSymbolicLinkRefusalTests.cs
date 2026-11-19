using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>フォルダ統合のリンク拒否を、実のシンボリックリンク（ファイルとディレクトリ）で確認します。作成できない環境は失敗にします。</summary>
[TestClass]
[TestCategory("Suite.DirectorySymbolicLink")]
public sealed class FolderMergeSymbolicLinkRefusalTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 統合元の深い位置のファイルのシンボリックリンク、統合元のディレクトリのシンボリックリンク、
    /// 統合元のファイルと同じ相対パスにある統合先のファイルのシンボリックリンクは、物理変更の前に拒否し、
    /// リンクのパスを書式化した案内を一件だけ出す。統合元・統合先・リンク・リンク先・DB・所持・登録は変わらず、別の通常の組の統合は成功する。
    /// ジャンクションだけを検出してシンボリックリンクを通す誤り、ファイルのリンクを通常のファイルとして移動・削除する誤り、
    /// 統合先の対応するファイルを検査しない誤り、拒否を汎用の失敗の案内と重ねる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("deep-file")]
    [DataRow("directory")]
    [DataRow("destination-file")]
    [TestCategory("EnvironmentIntegration")]
    public void MergeChartDirectory_SymbolicLinkIsRefusedBeforeAnyChangeAndOtherMergeSucceeds(string shape)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            string externalDirectory = Path.Combine(workspace.RootDirectory, "external");
            string externalFile = Path.Combine(externalDirectory, "target.wav");
            Directory.CreateDirectory(externalDirectory);
            File.WriteAllText(externalFile, "linked audio");
            string other = Path.Combine(root, "Other");
            string otherDestination = Path.Combine(root, "OtherDestination");
            ChartFile[] charts =
            [
                LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"),
                LibraryResourceIndexTestSupport.CreateChart(source, "unique.bms"),
                LibraryResourceIndexTestSupport.CreateChart(destination, "same.bms"),
                LibraryResourceIndexTestSupport.CreateChart(other, "other.bms"),
                LibraryResourceIndexTestSupport.CreateChart(otherDestination, "existing.bms")
            ];
            Directory.CreateDirectory(Path.Combine(source, "Pack", "Deep"));
            File.WriteAllText(Path.Combine(source, "Pack", "plain.wav"), "plain audio");
            Directory.CreateDirectory(Path.Combine(destination, "Pack"));
            string link;
            try
            {
                switch (shape)
                {
                    case "deep-file":
                        link = Path.Combine(source, "Pack", "Deep", "linked.wav");
                        File.CreateSymbolicLink(link, externalFile);
                        break;
                    case "directory":
                        link = Path.Combine(source, "Pack", "DirectoryLink");
                        Directory.CreateSymbolicLink(link, externalDirectory);
                        break;
                    case "destination-file":
                        File.WriteAllText(Path.Combine(source, "Pack", "same.wav"), "source audio");
                        link = Path.Combine(destination, "Pack", "same.wav");
                        File.CreateSymbolicLink(link, externalFile);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                Assert.Fail("実シンボリックリンクを作成できない環境のため、専用環境の準備が不足しています: " + exception.Message);
                return;
            }
            using var fixture = FolderMergeLinkFixture.Create(databaseScope, songDbPath, charts);
            (string, string)[] roots = [("source", source), ("destination", destination), ("external", externalDirectory)];
            string[] before = FolderMergeLinkFixture.Snapshot(roots);

            var notices = new LibraryOperationNotices();
            DuplicateMergeMaintenanceReceipt receipt = fixture.Merge(source, destination, notices);

            FolderMergeLinkFixture.AssertRefusedWithSingleNotice(receipt, notices, string.Format(Resources.Warn_BmsFolderMergeLinkFound, link));
            FolderMergeLinkFixture.AssertSnapshotUnchanged(before, FolderMergeLinkFixture.Snapshot(roots));
            Assert.AreEqual("linked audio", File.ReadAllText(externalFile));
            fixture.AssertCatalogUnchanged();

            var otherNotices = new LibraryOperationNotices();
            DuplicateMergeMaintenanceReceipt otherReceipt = fixture.Merge(other, otherDestination, otherNotices);
            Assert.IsTrue(otherReceipt.MergeApplied, otherReceipt.SessionReceipt.PrimaryFailure?.ToString());
            Assert.AreEqual(0, otherNotices.Snapshot().Count);
            Assert.IsTrue(File.Exists(Path.Combine(otherDestination, "other.bms")));
        });
    }
}
