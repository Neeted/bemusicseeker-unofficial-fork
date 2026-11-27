using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// フォルダ統合のリンク拒否を、実ジャンクションで確認します。リンクかどうかの判定と、リンクの先へ入らない検査はOSの実際の応答に依存するため、
/// 日常検証ではなくこの専用suiteが担当します。日常検証は、リンクのない統合が従来どおり成功し、検査の失敗が同じ保全で拒否されることを確認します。
/// </summary>
[TestClass]
[TestCategory("Suite.Junction")]
public sealed class FolderMergeJunctionRefusalTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 統合元の配下のジャンクション・リンク先が消えたジャンクション・統合元本体のジャンクション・統合先の二階層目の対応ディレクトリのジャンクションは、
    /// 物理変更の前に拒否し、リンクのパスを書式化した案内を一件だけ出す。統合元・統合先・リンク（リンク先へ入らず自身）・リンク先・DB・所持・登録は変わらず、
    /// 別の通常の組の統合は成功する。
    /// 検査が隠しフォルダの配下を飛ばす誤り、リンク先の消えたリンクを不在や通常として扱う誤り、リンク先へ入って検査や移動をする誤り、
    /// 統合元本体の検査を欠く誤り、統合先の対応する配置だけを検査しない誤り、拒否を汎用の失敗の案内と重ねる誤りを検出する。
    /// </summary>
    /// <remarks>
    /// 統合元本体が壊れたリンクの場合は、挙動をここで固定しない。先に処理される通常ファイルを同じ階層に置き、逐次の移動が先に始まる実装を検出する。
    /// </remarks>
    [TestMethod]
    [DataRow("deep-hidden")]
    [DataRow("broken-below")]
    [DataRow("source-root")]
    [DataRow("destination-nested")]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void MergeChartDirectory_JunctionIsRefusedBeforeAnyChangeAndOtherMergeSucceeds(string shape)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            string external = Path.Combine(workspace.RootDirectory, "external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "target.wav"), "linked audio");
            List<ChartFile> charts = [LibraryResourceIndexTestSupport.CreateChart(destination, "same.bms")];
            string link;
            string linkTarget = external;
            switch (shape)
            {
                case "deep-hidden":
                    charts.Add(LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"));
                    charts.Add(LibraryResourceIndexTestSupport.CreateChart(source, "unique.bms"));
                    string hidden = Path.Combine(source, "Deep", ".hidden");
                    Directory.CreateDirectory(Path.Combine(hidden, "Inner"));
                    File.WriteAllText(Path.Combine(source, "Deep", "plain.wav"), "plain audio");
                    File.WriteAllText(Path.Combine(hidden, "Inner", "inner.wav"), "inner audio");
                    File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
                    link = Path.Combine(hidden, "Inner", "Link");
                    break;
                case "broken-below":
                    charts.Add(LibraryResourceIndexTestSupport.CreateChart(source, "unique.bms"));
                    Directory.CreateDirectory(Path.Combine(source, "Pack"));
                    File.WriteAllText(Path.Combine(source, "Pack", "plain.wav"), "plain audio");
                    link = Path.Combine(source, "Pack", "Broken");
                    break;
                case "source-root":
                    string realSource = Path.Combine(workspace.RootDirectory, "real-source");
                    LibraryResourceIndexTestSupport.CreateChart(realSource, "unique.bms");
                    File.WriteAllText(Path.Combine(realSource, "plain.wav"), "plain audio");
                    link = source;
                    linkTarget = realSource;
                    break;
                case "destination-nested":
                    charts.Add(LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"));
                    Directory.CreateDirectory(Path.Combine(source, "A", "B"));
                    File.WriteAllText(Path.Combine(source, "A", "B", "source.wav"), "source audio");
                    Directory.CreateDirectory(Path.Combine(destination, "A"));
                    link = Path.Combine(destination, "A", "B");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }
            string other = Path.Combine(root, "Other");
            string otherDestination = Path.Combine(root, "OtherDestination");
            charts.Add(LibraryResourceIndexTestSupport.CreateChart(other, "other.bms"));
            charts.Add(LibraryResourceIndexTestSupport.CreateChart(otherDestination, "existing.bms"));
            using var fixture = FolderMergeLinkFixture.Create(databaseScope, songDbPath, [.. charts]);

            DirectoryJunctionFixture.Run(workspace, link, linkTarget, () =>
            {
                if (shape == "broken-below")
                {
                    Directory.Delete(external, recursive: true);
                }
                (string, string)[] roots = [("source", source), ("destination", destination), ("target", linkTarget)];
                string[] before = FolderMergeLinkFixture.Snapshot(roots);

                var notices = new LibraryOperationNotices();
                DuplicateMergeMaintenanceReceipt receipt = fixture.Merge(source, destination, notices);

                FolderMergeLinkFixture.AssertRefusedWithSingleNotice(receipt, notices, string.Format(Resources.Warn_BmsFolderMergeLinkFound, link));
                FolderMergeLinkFixture.AssertSnapshotUnchanged(before, FolderMergeLinkFixture.Snapshot(roots));
                fixture.AssertCatalogUnchanged();

                var otherNotices = new LibraryOperationNotices();
                DuplicateMergeMaintenanceReceipt otherReceipt = fixture.Merge(other, otherDestination, otherNotices);
                Assert.IsTrue(otherReceipt.MergeApplied, otherReceipt.SessionReceipt.PrimaryFailure?.ToString());
                Assert.AreEqual(0, otherNotices.Snapshot().Count);
                Assert.IsTrue(File.Exists(Path.Combine(otherDestination, "other.bms")));
                Assert.IsFalse(Directory.Exists(other));
            });
        });
    }

    /// <summary>
    /// 重複グループの一括整理は、移動元の深い位置のジャンクションと、一フォルダのグループのフォルダ本体のジャンクションを、物理変更の前に理由付きでスキップし、
    /// 案内を出さず、独立した次のグループを完了します。一フォルダのグループの配下のジャンクションは配置を動かさないため拒否せず、重複を整理し、
    /// ジャンクションとリンク先（リンク先へ入らず自身）は変えません。
    /// 二フォルダ以上のグループで移動元がすべて存在せず KEEP 本体がジャンクションの場合も、スキップし、リンク先の譜面を整理しません。
    /// KEEP 本体の検査を欠いてリンク先の譜面をごみ箱へ送る誤り、一括整理だけがリンクの検査を欠く誤り、一フォルダのグループの配下まで拒否する誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("source-deep")]
    [DataRow("single-folder-root")]
    [DataRow("single-folder-nested")]
    [DataRow("keep-root-with-missing-sources")]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void OrganizeDuplicateGroups_JunctionIsSkippedOrIgnoredPerGroupKindAndOtherGroupSucceeds(string shape)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source"), destination = Path.Combine(root, "Destination"), folder = Path.Combine(root, "Folder");
            string external = Path.Combine(workspace.RootDirectory, "external");
            Directory.CreateDirectory(external);
            File.WriteAllText(Path.Combine(external, "target.wav"), "linked audio");
            List<ChartFile> charts = [];
            string link, linkTarget = external;
            (string Keep, string[] Sources) first;
            switch (shape)
            {
                case "source-deep":
                    charts.Add(LibraryResourceIndexTestSupport.CreateChart(destination, "kept.bms"));
                    charts.Add(LibraryResourceIndexTestSupport.CreateChart(source, "unique.bms"));
                    Directory.CreateDirectory(Path.Combine(source, "Deep", "Inner"));
                    link = Path.Combine(source, "Deep", "Inner", "Link");
                    first = (destination, [source]);
                    break;
                case "single-folder-root":
                case "keep-root-with-missing-sources":
                    string realFolder = Path.Combine(workspace.RootDirectory, "real-folder");
                    charts.AddRange(CreateDuplicatePair(realFolder));
                    link = folder;
                    linkTarget = realFolder;
                    // 二フォルダ以上のグループで移動元がすべて存在しない場合は、InspectMerge が統合先を検査しないため、KEEP 本体の検査が必要です。
                    first = shape == "single-folder-root" ? (folder, []) : (folder, [Path.Combine(root, "MissingSource")]);
                    break;
                case "single-folder-nested":
                    charts.AddRange(CreateDuplicatePair(folder));
                    link = Path.Combine(folder, "Link");
                    first = (folder, []);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
            }
            string other = Path.Combine(root, "Other"), otherDestination = Path.Combine(root, "OtherDestination");
            charts.Add(LibraryResourceIndexTestSupport.CreateChart(other, "other.bms"));
            charts.Add(LibraryResourceIndexTestSupport.CreateChart(otherDestination, "existing.bms"));
            // リンク先の譜面は、リンクを介した位置（一フォルダのグループのフォルダ本体）としてカタログに持たせます。
            if (shape is "single-folder-root" or "keep-root-with-missing-sources")
            {
                charts = [.. charts.Select(chart => chart.Path.StartsWith(linkTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? chart with { Path = Path.Combine(folder, Path.GetFileName(chart.Path)) } : chart)];
            }
            using var fixture = FolderMergeLinkFixture.Create(databaseScope, songDbPath, [.. charts]);

            DirectoryJunctionFixture.Run(workspace, link, linkTarget, () =>
            {
                (string, string)[] roots = [("source", source), ("destination", destination), ("folder", folder), ("target", linkTarget)];
                string[] before = FolderMergeLinkFixture.Snapshot(roots);
                var notices = new LibraryOperationNotices();

                DuplicateBatchOrganizeResult result = fixture.Organize([first, (otherDestination, [other])], notices);

                Assert.AreEqual(0, notices.Snapshot().Count);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, result.Groups[1].Status);
                Assert.IsTrue(File.Exists(Path.Combine(otherDestination, "other.bms")));
                Assert.IsFalse(Directory.Exists(other));
                if (shape == "single-folder-nested")
                {
                    Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, result.Groups[0].Status);
                    Assert.AreEqual(1, result.Groups[0].DeletedChartCount);
                    Assert.IsTrue(Directory.EnumerateFileSystemEntries(external).Select(Path.GetFileName).SequenceEqual(["target.wav"]));
                    string[] after = FolderMergeLinkFixture.Snapshot(roots);
                    Assert.IsTrue(after.Any(line => line.StartsWith("L|folder/Link|", StringComparison.Ordinal)), "ジャンクションは変わりません。");
                    Assert.AreEqual(1, Directory.EnumerateFiles(folder, "*.bms").Count());
                    return;
                }
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Skipped, result.Groups[0].Status);
                Assert.AreEqual(DuplicateBatchOrganizeSkipReason.LinkFound, result.Groups[0].SkipReason);
                Assert.AreEqual(link, result.Groups[0].SkipPath);
                string[] afterRoots = FolderMergeLinkFixture.Snapshot(roots);
                CollectionAssert.AreEqual(before.Where(line => !line.Contains("other", StringComparison.OrdinalIgnoreCase)).ToArray(),
                    afterRoots.Where(line => !line.Contains("other", StringComparison.OrdinalIgnoreCase)).ToArray());
            });
        });
    }

    /// <summary>同じ内容で更新日時の異なる二つの譜面（古い方が残り、新しい方がごみ箱へ送られる）を作ります。</summary>
    private static ChartFile[] CreateDuplicatePair(string directory)
    {
        ChartFile older = LibraryResourceIndexTestSupport.CreateChart(directory, "dup1.bms");
        string second = Path.Combine(directory, "dup2.bms");
        File.Copy(older.Path, second);
        File.SetLastWriteTimeUtc(older.Path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return [older, BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(second)) with { Token = new OwnedChartToken() }];
    }
    /// <summary>
    /// 登録BMSルートを含む統合元、またはカタログが未収束の統合は、リンクが同時にあっても既存の拒否の案内だけを一件出す。
    /// 既存の拒否よりリンクの検査を先に行い、リンクの案内へ置き換える誤りと、二つの案内を重ねる誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("registered-root")]
    [DataRow("catalog-unconverged")]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void MergeChartDirectory_ExistingRefusalComesBeforeLinkRefusal(string reason)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            string external = Path.Combine(workspace.RootDirectory, "external");
            Directory.CreateDirectory(external);
            ChartFile[] charts = [LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(destination, "same.bms")];
            Directory.CreateDirectory(Path.Combine(source, "Pack"));
            string link = Path.Combine(source, "Pack", "Link");
            using var fixture = FolderMergeLinkFixture.Create(databaseScope, songDbPath, charts, reason == "registered-root" ? [root, source] : null);
            if (reason == "catalog-unconverged")
            {
                fixture.Library.ResetCatalogPathConvergence(CatalogPathConvergenceBlockReason.StartupFileScanDisabled);
            }

            DirectoryJunctionFixture.Run(workspace, link, external, () =>
            {
                (string, string)[] roots = [("source", source), ("destination", destination), ("target", external)];
                string[] before = FolderMergeLinkFixture.Snapshot(roots);

                var notices = new LibraryOperationNotices();
                DuplicateMergeMaintenanceReceipt receipt = fixture.Merge(source, destination, notices);

                FolderMergeLinkFixture.AssertRefusedWithSingleNotice(
                    receipt,
                    notices,
                    reason == "registered-root"
                        ? string.Format(Resources.Warn_CannotMoveRegisteredBmsRoot, source)
                        : Resources.Warn_CatalogFileMutationRequiresStartupScan,
                    exact: reason == "registered-root");
                FolderMergeLinkFixture.AssertSnapshotUnchanged(before, FolderMergeLinkFixture.Snapshot(roots));
                fixture.AssertCatalogUnchanged();
            });
        });
    }

    /// <summary>
    /// 手動統合の経路で深い位置のジャンクションを拒否し、案内は一件だけで、変更セッションの報告（FileDbMutationReport）を出さない。
    /// 拒否後は受付と活動が解放され、次の統合を受け付けて成功する。拒否を汎用の失敗として報告する誤り、案内を受付の解放前に依頼する誤り、
    /// 受付や活動を解放しない誤りを検出する。
    /// </summary>
    /// <remarks>判断・物理処理・SQLiteは実装を使い、再生・確認・表示だけを既存の窓口で記録する。</remarks>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void RunFolderMergeAsync_JunctionRefusalShowsOneNoticeAndReleasesAdmission()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            string other = Path.Combine(root, "Other");
            string otherDestination = Path.Combine(root, "OtherDestination");
            string external = Path.Combine(workspace.RootDirectory, "external");
            Directory.CreateDirectory(external);
            ChartFile[] charts =
            [
                LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"),
                LibraryResourceIndexTestSupport.CreateChart(source, "unique.bms"),
                LibraryResourceIndexTestSupport.CreateChart(destination, "same.bms"),
                LibraryResourceIndexTestSupport.CreateChart(other, "other.bms"),
                LibraryResourceIndexTestSupport.CreateChart(otherDestination, "existing.bms")
            ];
            Directory.CreateDirectory(Path.Combine(source, "Pack", "Sub"));
            string link = Path.Combine(source, "Pack", "Sub", "Link");
            using var fixture = FolderMergeLinkFixture.Create(databaseScope, songDbPath, charts);
            TestBmsLibrary library = fixture.Library;
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var probe = new OperationNoticeRequestProbe(() => false, () =>
            {
                if (!gate.TryEnter(out IDisposable lease)) { return false; }
                lease.Dispose();
                return true;
            })
            { HoldDisplay = false };
            var activity = new ChartMutationActivityOwner();
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity, new RecordingPresentation([]), probe,
                () => false, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore());

            DirectoryJunctionFixture.Run(workspace, link, external, () =>
            {
                (string, string)[] roots = [("source", source), ("destination", destination), ("target", external)];
                string[] before = FolderMergeLinkFixture.Snapshot(roots);

                DuplicateMaintenanceMutationResult refused = owner
                    .RunFolderMergeAsync(source, destination, new DuplicateGroup(charts.ToList(), [source, destination])).GetAwaiter().GetResult();

                Assert.IsFalse(refused.Succeeded);
                Assert.IsNull(refused.Failure);
                Assert.IsFalse(refused.MutationReceipt.MergeApplied);
                OperationNoticeRequestProbe.Request notice = probe.Requests.Single();
                Assert.AreEqual(string.Format(Resources.Warn_BmsFolderMergeLinkFound, link), notice.Message.MessageBoxText);
                Assert.IsTrue(notice.AdmissionsWereFree, "案内は受付の解放後に依頼する。");
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                FolderMergeLinkFixture.AssertSnapshotUnchanged(before, FolderMergeLinkFixture.Snapshot(roots));
                fixture.AssertCatalogUnchanged();

                DuplicateMaintenanceMutationResult accepted = owner
                    .RunFolderMergeAsync(other, otherDestination, new DuplicateGroup(charts.ToList(), [other, otherDestination])).GetAwaiter().GetResult();
                Assert.IsTrue(accepted.Succeeded, accepted.Failure?.ToString());
                Assert.IsTrue(File.Exists(Path.Combine(otherDestination, "other.bms")));
                Assert.AreEqual(1, probe.Requests.Count, "成功した統合は拒否の案内を重ねない。");
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
            });
        });
    }
}
