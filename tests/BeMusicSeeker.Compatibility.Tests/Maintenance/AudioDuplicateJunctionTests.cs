using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;
using static BeMusicSeeker.TestSupport.Components.SplitPlacementTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源の確認と整理を、実ジャンクションで確認します。リンクかどうかの判定と、リンク先へ入らない列挙はOSの実際の応答に依存するため、
/// 日常検証ではなくこの専用suiteが担当します。
/// </summary>
[TestClass]
[TestCategory("Suite.Junction")]
public sealed class AudioDuplicateJunctionTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private static Dictionary<string, byte[]> Contents(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 保証：確認は、フォルダ本体がジャンクションなら整理不可（リンク）で、フォルダ自身を該当に持ち、衝突名と同等の一覧は空になる。
    /// 配下のジャンクションはリンクの先へ入らず、先にある同名のWAVとOGGを衝突名に数えず、整理不可にもしない（フォルダ自身の衝突名がなければ安全）。リンク先は変わらない。
    /// 検出する誤り：リンク先の列挙による衝突名の混入、本体がリンクのフォルダの通常扱い、配下のリンクの整理不可化、リンク先への書込み。
    /// </summary>
    [TestMethod]
    [DataRow("body")]
    [DataRow("below")]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void Inspect_JunctionBodyIsUnorganizableAndJunctionBelowIsNotEntered(string shape)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string folder = Path.Combine(workspace.RootDirectory, "own");
        string external = Path.Combine(workspace.RootDirectory, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "x.wav"), "linked wav");
        File.WriteAllText(Path.Combine(external, "x.ogg"), "linked ogg");
        string link;
        if (shape == "body")
        {
            link = folder;
        }
        else
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "only.wav"), "own wav");
            link = Path.Combine(folder, "Link");
        }
        Dictionary<string, byte[]> targetBefore = Contents(external);

        DirectoryJunctionFixture.Run(workspace, link, external, () =>
        {
            AudioDuplicateFolderInspection result = new AudioDuplicateFolderInspector().Inspect(new AudioDuplicateInspectionRequest(folder));

            Assert.AreEqual(0, result.CollisionNames.Count);
            Assert.AreEqual(0, result.EquivalentCollisionNames.Count);
            if (shape == "body")
            {
                Assert.AreEqual(AudioDuplicateInspectionStage.Unorganizable, result.Stage);
                DuplicateMergeVerificationReason reason = result.Reasons.Single();
                Assert.AreEqual(DuplicateMergeVerificationReasonKind.Link, reason.Kind);
                CollectionAssert.AreEqual(new[] { new DuplicateMergeFinding(folder, string.Empty) }, reason.Findings.ToArray());
            }
            else
            {
                Assert.AreEqual(AudioDuplicateInspectionStage.Safe, result.Stage, "配下のリンクは整理不可の理由にしません。");
                Assert.AreEqual(0, result.Reasons.Count);
            }
            CollectionAssert.AreEqual(targetBefore.Keys.Order().ToArray(), Contents(external).Keys.Order().ToArray(), "リンク先は変わりません。");
            foreach (KeyValuePair<string, byte[]> item in targetBefore)
            {
                CollectionAssert.AreEqual(item.Value, File.ReadAllBytes(item.Key), "リンク先は変わりません。");
            }
        });
    }

    /// <summary>
    /// 保証：整理は、フォルダ本体がジャンクションなら理由付き（リンク）でスキップし、リンク先のファイルを変えず、次のフォルダの整理を続ける。
    /// 検出する誤り：本体がリンクのフォルダでリンク先の音源をごみ箱へ送る、スキップで後続を止める。
    /// </summary>
    [TestMethod]
    [TestCategory("ProcessIntegration")]
    [TestCategory("EnvironmentIntegration")]
    public void Organize_JunctionBodyIsSkippedWithoutTouchingTheTarget()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            string root = Path.Combine(workspace.RootDirectory, "BMS");
            string linked = Path.Combine(root, "Linked"), plain = Path.Combine(root, "Plain");
            string external = Path.Combine(workspace.RootDirectory, "external");
            Directory.CreateDirectory(plain);
            Directory.CreateDirectory(external);
            foreach (string directory in new[] { external, plain })
            {
                File.WriteAllText(Path.Combine(directory, "x.wav"), "wav");
                File.WriteAllText(Path.Combine(directory, "x.ogg"), "ogg");
            }
            var mutations = new TestFileMutationService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, mutations, new RecordingDialogService(),
                new TestUiScheduler(() => null), optionsSnapshotProvider: null,
                settings: TestBmsFactory.CreateRootSettings([root]), rootFileEnumerator: new FastRootFileEnumerator())
            { BmsCharts = [Chart(Path.Combine(plain, "p.bms"), 1)], BmsonCharts = [] };
            try
            {
                Dictionary<string, byte[]> targetBefore = Contents(external);
                AudioDuplicateOrganizeResult result = null!;
                DirectoryJunctionFixture.Run(workspace, linked, external, () =>
                {
                    result = library.OrganizeAudioDuplicateFolders(
                        new AudioDuplicateOrganizeRequest(
                        [
                            new AudioDuplicateOrganizeFolder(linked, ".wav", ["x"]),
                            new AudioDuplicateOrganizeFolder(plain, ".wav", ["x"])
                        ]),
                        1, new LibraryOperationNotices(), null, CancellationToken.None);
                });

                Assert.AreEqual(AudioDuplicateOrganizeStatus.Skipped, result.Folders[0].Status);
                Assert.AreEqual(DuplicateBatchOrganizeSkipReason.LinkFound, result.Folders[0].SkipReason);
                Assert.AreEqual(AudioDuplicateOrganizeStatus.Completed, result.Folders[1].Status);
                Assert.AreEqual(1, result.Folders[1].DeletedCount);
                CollectionAssert.AreEqual(new[] { Path.Combine(plain, "x.wav") }, mutations.ShellDeletedFiles.ToArray());
                CollectionAssert.AreEqual(targetBefore.Keys.Order().ToArray(), Contents(external).Keys.Order().ToArray(), "リンク先は変わりません。");
                Assert.AreEqual("wav", File.ReadAllText(Path.Combine(external, "x.wav")));
            }
            finally
            {
                library.RequestShutdown("audio-organize-junction");
            }
        });
    }
}
