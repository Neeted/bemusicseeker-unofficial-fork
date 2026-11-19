using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複グループの一括整理（モデルの入口）を、実ファイル・SQLite・実ライブラリで確認します。
/// 実際のごみ箱は使わず、<see cref="TestFileMutationService"/> が削除の記録と失敗を注入します。
/// 「残る」の判定は製品のハッシュに頼らず、テスト自身がルート配下の譜面ファイルのバイト列で行います。
/// </summary>
[TestClass]
public sealed class DuplicateBatchOrganizeTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private static readonly DateTime BaseTime = new(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 一括整理の結果が、同じ入力の複製へ単体の統合を順に適用した結果（相対パス・バイト列・DB行）と一致することを確認し、
    /// 一括用の移動・採番・上書きの規則を別に持つ誤りを検出します。
    /// </summary>
    /// <remarks>スマート上書きの有無と保護対象名の別名保持の有無の全組合せで、採番・資源の衝突・二階層の配下・保護対象名を含む入力を使います。</remarks>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void OrganizeDuplicateGroups_MatchesSequentialSingleMerges(bool smartOverwrite, bool keepProtectedByRenaming)
    {
        WithTemporarySongDb(databaseScope, stepwiseDb => WithTemporarySongDb(databaseScope, batchDb =>
        {
            using var stepwise = new Rig(databaseScope, stepwiseDb) { SmartOverwrite = smartOverwrite, KeepProtectedByRenaming = keepProtectedByRenaming };
            using var batch = new Rig(databaseScope, batchDb) { SmartOverwrite = smartOverwrite, KeepProtectedByRenaming = keepProtectedByRenaming };
            foreach (Rig rig in new[] { stepwise, batch })
            {
                BuildSequentialEquivalenceTree(rig);
                rig.Start();
            }
            try
            {
                (string Keep, string[] Sources)[] groups = [("K", ["A", "B"]), ("K2", ["C"])];
                foreach ((string keep, string[] sources) in groups)
                {
                    foreach (string source in sources)
                    {
                        DuplicateMergeMaintenanceReceipt receipt = stepwise.Library.MergeChartDirectory(
                            stepwise.Dir(source), stepwise.Dir(keep), 1, new LibraryOperationNotices(), reportAtTerminal: true);
                        Assert.IsTrue(receipt.MergeApplied, receipt.SessionReceipt.PrimaryFailure?.ToString());
                    }
                }
                DuplicateBatchOrganizeResult result = batch.Organize(groups.Select(group => (group.Keep, group.Sources)));

                Assert.IsTrue(result.Admitted);
                Assert.IsTrue(result.Receipt.MergeApplied, result.Receipt.SessionReceipt.PrimaryFailure?.ToString());
                Assert.IsTrue(result.Groups.All(group => group.Status == DuplicateBatchOrganizeGroupStatus.Completed));
                Assert.AreEqual(0, result.Groups.Sum(group => group.TrashedChartCount));
                Assert.IsFalse(Directory.Exists(batch.At("A")) || Directory.Exists(batch.At("B")) || Directory.Exists(batch.At("C")));
                AssertSameTree(stepwise.Tree(), batch.Tree());
                CollectionAssert.AreEquivalent(stepwise.SongRows(), batch.SongRows());
                Assert.AreEqual(1, result.Receipt.SessionReceipt.ApplyCounts.CatalogApplyCount);
            }
            finally
            {
                stepwise.Library.RequestShutdown("batch-equivalence-stepwise");
                batch.Library.RequestShutdown("batch-equivalence-batch");
            }
        }));
    }

    /// <summary>採番（同名で内容が異なる譜面）・資源の衝突・二階層の配下・保護対象名・既に所持する譜面を含む、移動元の異なる三フォルダの入力です。</summary>
    private static void BuildSequentialEquivalenceTree(Rig rig)
    {
        DateTime older = BaseTime.AddDays(-10), newer = BaseTime.AddDays(10);
        rig.Chart("K", "k.bms", "k");
        rig.Resource("K", "common.wav", "k-common", BaseTime);
        rig.Resource("K", "readme.txt", "k-readme", BaseTime);
        rig.Resource("K", "bga/x.png", "k-png", BaseTime);

        rig.Chart("A", "a.bms", "a");
        rig.Chart("A", "k.bms", "a-k");
        rig.Resource("A", "common.wav", "a-common", newer);
        rig.Resource("A", "readme.txt", "a-readme", newer);
        rig.Resource("A", "bga/x.png", "a-png", newer);
        rig.Resource("A", "sub/deep/d.wav", "a-deep", newer);

        rig.Chart("B", "b.bms", "b");
        rig.Chart("B", "known.bms", "k");
        rig.Resource("B", "common.wav", "k-common", older);
        rig.Resource("B", "readme.txt", "b-readme", older);
        rig.Resource("B", "sub/e.wav", "b-sub", older);

        rig.Chart("K2", "m.bms", "m");
        rig.Resource("K2", "common.wav", "m-common", BaseTime);
        rig.Chart("C", "c.bms", "c");
        rig.Resource("C", "common.wav", "c-common", newer);
        rig.Resource("C", "readme.txt", "c-readme", newer);
    }

    /// <summary>
    /// KEEP 直下に元からある同じ主ハッシュの譜面を、更新日時が古いものを残し、同日時なら短い名前を残して一件にし、BMSON も同じ規則で一件にします。
    /// 余分はごみ箱へ送り、フォルダを削除せず、確認も案内も出しません。選定を取り違える誤りと、重複整理が確認や案内を求める誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_KeepDirectDuplicates_KeepsOldestThenShortestName()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            string oldLong = rig.Chart("K", "old_long_name.bms", "x", BaseTime.AddDays(-5));
            string newShort = rig.Chart("K", "x.bms", "x", BaseTime);
            string shortName = rig.Chart("K", "s1.bms", "y");
            string longName = rig.Chart("K", "longer_name.bms", "y");
            string bmsonKeep = rig.Bmson("K", "p1.bmson", "z");
            string bmsonDrop = rig.Bmson("K", "p22.bmson", "z");
            rig.Chart("K", "u.bms", "u");
            rig.Chart("A", "a.bms", "a");
            rig.Start();
            try
            {
                HashSet<string> before = rig.ChartContents();
                var notices = new LibraryOperationNotices();
                DuplicateBatchOrganizeResult result = rig.Organize([("K", ["A"])], notices: notices);

                DuplicateBatchOrganizeGroupResult group = result.Groups.Single();
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, group.Status);
                Assert.AreEqual(3, group.TrashedChartCount);
                Assert.AreEqual(0, group.TrashFailureCount);
                CollectionAssert.AreEqual(new[] { rig.At("A") }, group.MergedSources.ToArray());
                CollectionAssert.AreEquivalent(new[] { newShort, longName, bmsonDrop }, rig.Mutations.ShellDeletedFiles.ToArray());
                Assert.IsTrue(File.Exists(oldLong) && File.Exists(shortName) && File.Exists(bmsonKeep));
                Assert.IsFalse(File.Exists(newShort) || File.Exists(longName) || File.Exists(bmsonDrop));
                Assert.IsTrue(Directory.Exists(rig.At("K")));
                Assert.AreEqual(1, rig.CountDirectCharts("K", "x"));
                Assert.AreEqual(1, rig.CountDirectCharts("K", "y"));
                Assert.AreEqual(1, rig.CountDirectCharts("K", "z", bmson: true));
                Assert.AreEqual(1, rig.CountDirectCharts("K", "a"));
                AssertContentsRemain(before, rig.ChartContents());
                Assert.AreEqual(0, notices.Snapshot().Count);
                Assert.AreEqual(0, rig.Dialogs.ShowCount);
                Assert.AreEqual(0, rig.Dialogs.Posted.Count);
                string[] rows = [.. rig.SongRows().Select(pair => pair.Key)];
                CollectionAssert.DoesNotContain(rows, Path.GetRelativePath(rig.Root, newShort));
                CollectionAssert.DoesNotContain(rows, Path.GetRelativePath(rig.Root, longName));
                CollectionAssert.DoesNotContain(rows, Path.GetRelativePath(rig.Root, bmsonDrop));
                CollectionAssert.Contains(rows, Path.GetRelativePath(rig.Root, oldLong));
            }
            finally
            {
                rig.Library.RequestShutdown("batch-keep-duplicates");
            }
        });
    }

    /// <summary>
    /// 移動元直下の重複は、実際に KEEP へ移る主ハッシュに限って、移動の前に一件へ整理します。
    /// KEEP に既にある主ハッシュは、移動元で整理せず（ごみ箱へ送らず）、既存の統合の入力元の後片付けに任せます。
    /// 移る主ハッシュを二件とも移す誤りと、移らない主ハッシュを整理で失う誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_SourceDuplicates_AreCleanedOnlyForHashesThatMove()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K", "k.bms", "k");
            rig.Chart("A", "m1.bms", "m", BaseTime.AddDays(-1));
            string moving = rig.Chart("A", "m2.bms", "m");
            rig.Chart("A", "kd1.bms", "k");
            rig.Chart("A", "kd2.bms", "k");
            rig.Start();
            try
            {
                HashSet<string> before = rig.ChartContents();
                DuplicateBatchOrganizeResult result = rig.Organize([("K", ["A"])]);

                DuplicateBatchOrganizeGroupResult group = result.Groups.Single();
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, group.Status);
                Assert.AreEqual(1, group.TrashedChartCount);
                CollectionAssert.AreEqual(new[] { moving }, rig.Mutations.ShellDeletedFiles.ToArray(),
                    "KEEP に既にある主ハッシュの譜面は、移動元の重複整理でごみ箱へ送りません。");
                Assert.AreEqual(1, rig.CountDirectCharts("K", "m"));
                Assert.AreEqual(1, rig.CountDirectCharts("K", "k"));
                Assert.IsFalse(File.Exists(rig.At("K\\kd1.bms")) || File.Exists(rig.At("K\\kd2.bms")));
                AssertContentsRemain(before, rig.ChartContents());
            }
            finally
            {
                rig.Library.RequestShutdown("batch-source-duplicates");
            }
        });
    }

    /// <summary>
    /// 一フォルダのグループは、そのフォルダの直下の重複だけを整理します。フォルダと配下の別の譜面は変えず、確認を求めません。
    /// 配下の譜面まで整理する誤りと、重複がなくても完了にならない誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_SingleFolderGroup_TrashesOnlyDirectDuplicates()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("F", "f1.bms", "f", BaseTime.AddDays(-1));
            string duplicate = rig.Chart("F", "f2.bms", "f");
            rig.Chart("F", "g.bms", "g");
            string nested = rig.Chart("F\\sub", "f3.bms", "f");
            rig.Chart("G", "h.bms", "h");
            rig.Start();
            try
            {
                HashSet<string> before = rig.ChartContents();
                DuplicateBatchOrganizeResult result = rig.Organize([("F", []), ("G", [])]);

                Assert.IsTrue(result.Groups.All(group => group.IsSingleFolder && group.Status == DuplicateBatchOrganizeGroupStatus.Completed));
                Assert.AreEqual(1, result.Groups[0].TrashedChartCount);
                Assert.AreEqual(0, result.Groups[1].TrashedChartCount);
                Assert.AreEqual(0, result.Groups[0].MergedSources.Count);
                CollectionAssert.AreEqual(new[] { duplicate }, rig.Mutations.ShellDeletedFiles.ToArray());
                Assert.IsTrue(File.Exists(nested), "直下でない譜面は整理しません。");
                Assert.IsTrue(Directory.Exists(rig.At("F")) && Directory.Exists(rig.At("G")));
                AssertContentsRemain(before, rig.ChartContents());
                Assert.AreEqual(0, rig.Dialogs.ShowCount);
            }
            finally
            {
                rig.Library.RequestShutdown("batch-single-folder");
            }
        });
    }

    /// <summary>
    /// 移動元の予定がある別のグループの有無・結果によらず、操作前に所持していた譜面のバイト列が一件も残らない結果になりません。
    /// 同じグループの別の移動元・成功した別グループ・スキップした別グループ・取消で未処理の別グループ・物理失敗で止まった別グループ・
    /// 型の衝突で残る移動元のそれぞれで、未確定の別の移動元を「残るコピー」と数えて両方を失う誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow("same-group")]
    [DataRow("same-group-type-conflict")]
    [DataRow("other-group-success")]
    [DataRow("other-group-skipped")]
    [DataRow("other-group-canceled")]
    [DataRow("other-group-physical-failure")]
    public void OrganizeDuplicateGroups_NeverLosesTheLastCopy(string scenario)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath) { RegisteredRoots = scenario == "other-group-skipped" ? ["B"] : [] };
            rig.Chart("K1", "kx.bms", "x");
            rig.Chart("A", "ax.bms", "x");
            rig.Chart("A", "ay.bms", "y");
            (string Keep, string[] Sources)[] groups;
            switch (scenario)
            {
                case "same-group":
                    rig.Chart("B", "by.bms", "y");
                    groups = [("K1", ["A", "B"])];
                    break;
                case "same-group-type-conflict":
                    rig.Chart("A", "ay2.bms", "y");
                    rig.Resource("A", "BGA", "file", BaseTime);
                    rig.Resource("K1", "BGA\\s.txt", "directory", BaseTime);
                    rig.Chart("B", "by.bms", "y");
                    groups = [("K1", ["A", "B"])];
                    break;
                default:
                    rig.Chart("K2", "kw.bms", "w");
                    rig.Chart("B", "by.bms", "y");
                    groups = scenario == "other-group-physical-failure" ? [("K2", ["B"]), ("K1", ["A"])] : [("K1", ["A"]), ("K2", ["B"])];
                    break;
            }
            string failingSource = rig.At("B") + Path.DirectorySeparatorChar;
            if (scenario == "other-group-physical-failure")
            {
                rig.Mutations.FailFileTransferWhen = (source, _) => source.StartsWith(failingSource, StringComparison.OrdinalIgnoreCase);
            }
            rig.Start();
            try
            {
                HashSet<string> before = rig.ChartContents();
                using var cancellation = new CancellationTokenSource();
                DuplicateBatchOrganizeResult result = rig.Organize(groups,
                    progress: progress => { if (scenario == "other-group-canceled" && progress.Number == 1) { cancellation.Cancel(); } },
                    cancellationToken: cancellation.Token);

                AssertContentsRemain(before, rig.ChartContents());
                switch (scenario)
                {
                    case "same-group":
                        Assert.AreEqual(1, rig.CountDirectCharts("K1", "y"), "最初の移動元のコピーが KEEP に一件だけ残ります。");
                        Assert.AreEqual(1, rig.CountDirectCharts("K1", "x"));
                        break;
                    case "same-group-type-conflict":
                        Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.PartiallyCompleted, result.Groups[0].Status);
                        Assert.AreEqual(DuplicateBatchOrganizeSourceReason.TypeConflict, result.Groups[0].RetainedSources.Single().Reason);
                        Assert.AreEqual(2, rig.CountDirectCharts("A", "y"), "型の衝突で残る移動元の重複は整理せず、二件とも残ります。");
                        Assert.AreEqual(1, rig.CountDirectCharts("K1", "y"));
                        break;
                    case "other-group-success":
                        Assert.IsTrue(result.Groups.All(group => group.Status == DuplicateBatchOrganizeGroupStatus.Completed));
                        Assert.AreEqual(1, rig.CountDirectCharts("K1", "y"));
                        Assert.AreEqual(0, rig.CountDirectCharts("K2", "y"));
                        break;
                    case "other-group-skipped":
                        Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Skipped, result.Groups[1].Status);
                        Assert.AreEqual(1, rig.CountDirectCharts("K1", "y"));
                        Assert.AreEqual(1, rig.CountDirectCharts("B", "y"));
                        break;
                    case "other-group-canceled":
                        Assert.IsTrue(result.Canceled);
                        Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, result.Groups[0].Status);
                        Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Unprocessed, result.Groups[1].Status);
                        Assert.AreEqual(1, rig.CountDirectCharts("K1", "y"));
                        Assert.AreEqual(1, rig.CountDirectCharts("B", "y"));
                        break;
                    default:
                        Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Failed, result.Groups[0].Status);
                        Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Unprocessed, result.Groups[1].Status);
                        Assert.AreEqual(1, rig.CountDirectCharts("A", "y"));
                        Assert.AreEqual(1, rig.CountDirectCharts("B", "y"));
                        Assert.AreEqual(0, rig.CountDirectCharts("K1", "y"));
                        break;
                }
            }
            finally
            {
                rig.Library.RequestShutdown("batch-last-copy");
            }
        });
    }

    /// <summary>
    /// 書込み前に拒否するグループは、理由付きでスキップし、そのグループのファイル・DB・変更セッションの失敗の事実を変えず、案内も出さず、独立した次のグループへ進みます。
    /// 登録ルート本体・空の登録子を含む祖先・同じグループ内の親子・移動元同士の親子・別グループと同一・別グループの移動元の中の一フォルダのグループを確認し、
    /// 登録ルートの KEEP は統合できる対照として確認します。
    /// </summary>
    [TestMethod]
    [DataRow("registered-root")]
    [DataRow("registered-ancestor")]
    [DataRow("keep-is-registered-root")]
    [DataRow("keep-and-source-nested")]
    [DataRow("sources-nested")]
    [DataRow("other-group-same-source")]
    [DataRow("single-folder-inside-other-source")]
    public void OrganizeDuplicateGroups_RefusesBeforeAnyChangeAndContinues(string scenario)
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string[] registered = scenario switch
            {
                "registered-root" => ["A"],
                "registered-ancestor" => ["A\\Reg"],
                "keep-is-registered-root" => ["K"],
                _ => []
            };
            using var rig = new Rig(databaseScope, songDbPath) { RegisteredRoots = registered };
            rig.Chart("K", "kp.bms", "kp");
            rig.Chart("A", "a.bms", "a");
            rig.Chart("A\\Sub", "sub.bms", "sub");
            rig.Chart("P", "p.bms", "p");
            rig.Chart("P\\Q", "q.bms", "q");
            rig.Chart("S", "s.bms", "s");
            rig.Chart("S\\T", "t.bms", "t");
            rig.Chart("Z", "z.bms", "z");
            rig.Chart("ZK", "zk.bms", "zk");
            if (scenario == "registered-ancestor")
            {
                rig.Dir("A\\Reg");
            }
            (string Keep, string[] Sources)[] groups = scenario switch
            {
                "keep-and-source-nested" => [("P", ["P\\Q"])],
                "sources-nested" => [("K", ["S", "S\\T"])],
                "other-group-same-source" => [("K", ["A"]), ("S", ["A"])],
                "single-folder-inside-other-source" => [("K", ["A"]), ("A\\Sub", [])],
                _ => [("K", ["A"])]
            };
            int[] skipped = scenario switch
            {
                "registered-root" or "registered-ancestor" or "keep-and-source-nested" or "sources-nested" or "single-folder-inside-other-source" => [0],
                "other-group-same-source" => [0, 1],
                _ => []
            };
            DuplicateBatchOrganizeSkipReason reason = scenario switch
            {
                "registered-root" or "registered-ancestor" => DuplicateBatchOrganizeSkipReason.RegisteredRoot,
                "keep-and-source-nested" or "sources-nested" => DuplicateBatchOrganizeSkipReason.NestedFolders,
                _ => DuplicateBatchOrganizeSkipReason.OverlapsOtherGroup
            };
            groups = [.. groups, ("ZK", ["Z"])];
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> beforeTree = rig.Tree();
                List<KeyValuePair<string, string>> beforeRows = rig.SongRows();
                string[] beforeRegistrations = [.. new BmsRootRegistrationStore(rig.Library.FixtureSettings, () => rig.Library.FixtureLr2Owner).CaptureUserRoots()];
                var progress = new List<DuplicateBatchOrganizeProgress>();
                var notices = new LibraryOperationNotices();
                DuplicateBatchOrganizeResult result = rig.Organize(groups, progress.Add, notices: notices);
                CollectionAssert.AreEqual(beforeRegistrations,
                    new BmsRootRegistrationStore(rig.Library.FixtureSettings, () => rig.Library.FixtureLr2Owner).CaptureUserRoots().ToArray(),
                    "拒否しても、登録は変わりません。");

                for (int index = 0; index < groups.Length; index++)
                {
                    bool expectSkipped = skipped.Contains(index);
                    Assert.AreEqual(expectSkipped ? DuplicateBatchOrganizeGroupStatus.Skipped : DuplicateBatchOrganizeGroupStatus.Completed,
                        result.Groups[index].Status, "グループ " + index);
                    if (expectSkipped)
                    {
                        Assert.AreEqual(reason, result.Groups[index].SkipReason, "グループ " + index);
                        string[] folders = [groups[index].Keep, .. groups[index].Sources];
                        AssertFoldersUnchanged(beforeTree, rig.Tree(), folders);
                        AssertRowsUnchanged(beforeRows, rig.SongRows(), folders);
                    }
                }
                Assert.AreEqual(groups.Length, progress.Count);
                Assert.AreEqual(0, notices.Snapshot().Count);
                Assert.AreEqual(0, result.Receipt.SessionReceipt.ItemFailures.Count);
                Assert.IsNull(result.Receipt.SessionReceipt.PhysicalFailure);
                Assert.IsFalse(result.Receipt.SessionReceipt.HasRequiredFailure);
                Assert.AreEqual(0, rig.Dialogs.Posted.Count);
                Assert.IsFalse(Directory.Exists(rig.At("Z")), "次のグループは完了しています。");
                Assert.AreEqual(1, rig.CountDirectCharts("ZK", "z"));
            }
            finally
            {
                rig.Library.RequestShutdown("batch-refusal");
            }
        });
    }

    /// <summary>
    /// 移動元ごとの事前拒否（型の衝突・移動元の欠落・所持譜面なし）は、その移動元を理由付きで残して同じグループの次の移動元へ進み、グループを一部完了にします。
    /// 型の衝突で残す移動元の重複は整理せず二件とも残ります。拒否した移動元を統合したことにする誤りと、拒否が後続を止める誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_SourceRefusals_AreRetainedWithReasonsAndTheGroupContinues()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K", "k.bms", "k");
            rig.Resource("K", "BGA\\sentinel.txt", "directory", BaseTime);
            rig.Chart("A", "a1.bms", "y");
            rig.Chart("A", "a2.bms", "y");
            rig.Resource("A", "BGA", "file", BaseTime);
            rig.Chart("B", "b.bms", "b");
            rig.Resource("C", "only.wav", "resource", BaseTime);
            rig.Chart("D", "d.bms", "d");
            rig.Start();
            Directory.Delete(rig.At("B"), recursive: true);
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                DuplicateBatchOrganizeResult result = rig.Organize([("K", ["A", "B", "C", "D"])]);

                DuplicateBatchOrganizeGroupResult group = result.Groups.Single();
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.PartiallyCompleted, group.Status);
                CollectionAssert.AreEqual(new[] { rig.At("D") }, group.MergedSources.ToArray());
                CollectionAssert.AreEqual(
                    new[]
                    {
                        (rig.At("A"), DuplicateBatchOrganizeSourceReason.TypeConflict),
                        (rig.At("B"), DuplicateBatchOrganizeSourceReason.SourceMissing),
                        (rig.At("C"), DuplicateBatchOrganizeSourceReason.NoCharts)
                    },
                    group.RetainedSources.Select(source => (source.Directory, source.Reason)).ToArray());
                Assert.AreEqual(2, rig.CountDirectCharts("A", "y"));
                AssertFoldersUnchanged(before, rig.Tree(), ["A", "C"]);
                Assert.AreEqual(1, rig.CountDirectCharts("K", "d"));
                Assert.AreEqual(0, rig.Mutations.ShellDeletedFiles.Count);
                Assert.IsFalse(result.Receipt.SessionReceipt.HasRequiredFailure);
            }
            finally
            {
                rig.Library.RequestShutdown("batch-source-refusals");
            }
        });
    }

    /// <summary>
    /// KEEP が存在しないグループは、全ての移動元を宛先の欠落として残し、失敗にします。移動元の譜面・DB は変えません。
    /// 存在しない KEEP へ移動元を統合したことにする誤りと、欠落を一部完了や完了として報告する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_MissingDestination_FailsTheGroupAndKeepsEverySource()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K", "k.bms", "k");
            rig.Chart("A", "a.bms", "a");
            rig.Chart("B", "b.bms", "b");
            rig.Start();
            Directory.Delete(rig.At("K"), recursive: true);
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                List<KeyValuePair<string, string>> beforeRows = rig.SongRows();
                DuplicateBatchOrganizeResult result = rig.Organize([("K", ["A", "B"])]);

                DuplicateBatchOrganizeGroupResult group = result.Groups.Single();
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Failed, group.Status);
                Assert.AreEqual(0, group.MergedSources.Count);
                Assert.IsTrue(group.RetainedSources.All(source => source.Reason == DuplicateBatchOrganizeSourceReason.DestinationMissing));
                AssertFoldersUnchanged(before, rig.Tree(), ["A", "B"]);
                AssertRowsUnchanged(beforeRows, rig.SongRows(), ["A", "B"]);
            }
            finally
            {
                rig.Library.RequestShutdown("batch-missing-destination");
            }
        });
    }

    /// <summary>
    /// 予期しない物理失敗は、そのグループの残りの移動元と後続の全グループを未処理として止め、それまでに成功した分（先行グループと同じグループの先行する移動元）だけを確定します。
    /// 未処理のグループのバイト列は変えず、失敗を隠して後続を続ける誤りと、成功分まで巻き戻す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_UnexpectedPhysicalFailure_CommitsPrecedingWorkAndLeavesTheRestUnprocessed()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K1", "k1.bms", "k1");
            rig.Chart("A1", "a1.bms", "a1");
            rig.Chart("K2", "k2.bms", "k2");
            rig.Chart("B1", "b1.bms", "b1");
            rig.Chart("B2", "b2.bms", "b2");
            rig.Chart("K3", "k3.bms", "k3");
            rig.Chart("C", "c.bms", "c");
            string failingSource = rig.At("B2") + Path.DirectorySeparatorChar;
            rig.Mutations.FailFileTransferWhen = (source, _) => source.StartsWith(failingSource, StringComparison.OrdinalIgnoreCase);
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                DuplicateBatchOrganizeResult result = rig.Organize([("K1", ["A1"]), ("K2", ["B1", "B2"]), ("K3", ["C"])]);

                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, result.Groups[0].Status);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.PartiallyCompleted, result.Groups[1].Status);
                CollectionAssert.AreEqual(new[] { rig.At("B1") }, result.Groups[1].MergedSources.ToArray());
                Assert.AreEqual(
                    (rig.At("B2"), DuplicateBatchOrganizeSourceReason.PhysicalFailure),
                    result.Groups[1].RetainedSources.Select(source => (source.Directory, source.Reason)).Single());
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Unprocessed, result.Groups[2].Status);
                Assert.IsTrue(result.Receipt.SessionReceipt.DurableCommit);
                Assert.IsNotNull(result.Receipt.SessionReceipt.PhysicalFailure);
                Assert.IsTrue(result.Receipt.SessionReceipt.HasRequiredFailure);
                Assert.IsTrue(result.Receipt.SessionReceipt.UnprocessedTargets.Any(target => target.SourcePath == rig.At("C")));
                Assert.IsTrue(File.Exists(rig.At("K1\\a1.bms")) && File.Exists(rig.At("K2\\b1.bms")));
                Assert.IsFalse(Directory.Exists(rig.At("A1")) || Directory.Exists(rig.At("B1")));
                AssertFoldersUnchanged(before, rig.Tree(), ["B2", "C", "K3"]);
                string[] rows = [.. rig.SongRows().Select(pair => pair.Key)];
                CollectionAssert.IsSubsetOf(
                    new[] { "K1\\a1.bms", "K2\\b1.bms", "B2\\b2.bms", "C\\c.bms" },
                    rows);
                CollectionAssert.DoesNotContain(rows, "K3\\c.bms");
            }
            finally
            {
                rig.Library.RequestShutdown("batch-physical-failure");
            }
        });
    }

    /// <summary>
    /// 既存宛先の資源を退避した後に公開も復元もできない場合は、既存の統合と同じ形で、操作の結果に既存宛先の未復元を残します。
    /// 既存宛先の復元不能を通常の失敗へ吸収して結果から落とす誤りと、確定したことにする誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_UnrestoredExistingDestination_IsReportedInTheSessionReceipt()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string note = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Library", "K", "note.wav");
            var files = new UnrestoredDestinationFileMutationService(note);
            using var rig = new Rig(databaseScope, songDbPath) { MutationOverride = files };
            rig.Chart("K", "k.bms", "k");
            rig.Resource("K", "note.wav", "old resource", BaseTime);
            rig.Chart("A", "a.bms", "a");
            rig.Resource("A", "note.wav", "new resource", BaseTime);
            rig.Start();
            try
            {
                DuplicateBatchOrganizeResult result = rig.Organize([("K", ["A"])]);

                Assert.IsTrue(result.Receipt.SessionReceipt.HasUnrestoredExistingDestination);
                Assert.IsFalse(result.Receipt.SessionReceipt.DurableCommit);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Failed, result.Groups.Single().Status);
                Assert.AreEqual(DuplicateBatchOrganizeSourceReason.PhysicalFailure, result.Groups.Single().RetainedSources.Single().Reason);
                Assert.AreEqual(2, files.FailedMoveCount);
                Assert.IsNotNull(files.BackupPath);
                Assert.AreEqual("old resource", File.ReadAllText(files.BackupPath));
                Assert.IsTrue(File.Exists(rig.At("A\\a.bms")));
            }
            finally
            {
                rig.Library.RequestShutdown("batch-unrestored");
            }
        });
    }

    /// <summary>
    /// 開始前の取消は、全グループを未処理にし、進捗を一回も渡さず、ファイルとカタログを変えません。取消は失敗として扱いません。
    /// 開始前の取消でも処理を始める誤りと、取消を失敗として扱う誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_CanceledBeforeStart_ProcessesNothing()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K", "k.bms", "k");
            rig.Chart("A", "a.bms", "a");
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                var progress = new List<DuplicateBatchOrganizeProgress>();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                DuplicateBatchOrganizeResult result = rig.Organize([("K", ["A"])], progress.Add, cancellation.Token);

                Assert.IsTrue(result.Canceled);
                Assert.IsTrue(result.Admitted);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Unprocessed, result.Groups.Single().Status);
                Assert.AreEqual(0, progress.Count);
                AssertSameTree(before, rig.Tree());
                Assert.IsFalse(result.Receipt.SessionReceipt.HasRequiredFailure);
                Assert.IsNull(result.Receipt.SessionReceipt.FinalizationFailure);
            }
            finally
            {
                rig.Library.RequestShutdown("batch-cancel-before");
            }
        });
    }

    /// <summary>
    /// 第2グループの開始の進捗で取消すと、処理中のグループ（二つの移動元）は最後まで進み、残りのグループは進捗なしで未処理になります。
    /// 進捗は 1 始まりの番号・総数・KEEP を同期で渡し、受け手の中でモデルのロックを保持しません。確定と必須保守は取消の対象にしません。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_CancelOnSecondGroupStart_FinishesTheStartedGroupAndMaintainsIt()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K1", "k1.bms", "k1");
            rig.Chart("A", "a.bms", "a");
            rig.Chart("K2", "k2.bms", "k2");
            rig.Chart("B", "b.bms", "b");
            rig.Chart("C", "c.bms", "c");
            rig.Chart("K3", "k3.bms", "k3");
            rig.Chart("D", "d.bms", "d");
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                var progress = new List<DuplicateBatchOrganizeProgress>();
                bool locksFreeInReceiver = true;
                using var cancellation = new CancellationTokenSource();
                DuplicateBatchOrganizeResult result = rig.Organize([("K1", ["A"]), ("K2", ["B", "C"]), ("K3", ["D"])],
                    value =>
                    {
                        progress.Add(value);
                        locksFreeInReceiver &= AreModelLocksFree(rig.Library);
                        if (value.Number == 2)
                        {
                            cancellation.Cancel();
                        }
                    },
                    cancellation.Token);

                CollectionAssert.AreEqual(
                    new[] { (1, 3, rig.At("K1")), (2, 3, rig.At("K2")) },
                    progress.Select(value => (value.Number, value.Total, value.KeepDirectory)).ToArray());
                Assert.IsTrue(locksFreeInReceiver, "進捗の受け手の中で、モデルのロックを保持してはいけません。");
                Assert.IsTrue(result.Canceled);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, result.Groups[0].Status);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Completed, result.Groups[1].Status);
                Assert.AreEqual(2, result.Groups[1].MergedSources.Count);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Unprocessed, result.Groups[2].Status);
                AssertFoldersUnchanged(before, rig.Tree(), ["D", "K3"]);
                Assert.IsTrue(result.Receipt.MergeApplied);
                Assert.IsNull(result.Receipt.SessionReceipt.FinalizationFailure);
                Assert.IsTrue(result.Receipt.MaintenanceHadUpdates, "取消後も、確定済みのグループの必須保守は行います。");
            }
            finally
            {
                rig.Library.RequestShutdown("batch-cancel-second");
            }
        });
    }

    /// <summary>
    /// 進捗の受け手が例外を投げても、グループの処理と結果は変わりません。
    /// 受け手の例外でグループの処理を止める誤りと、結果を失敗へ変える誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_ProgressReceiverFailure_DoesNotChangeTheResult()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K1", "k1.bms", "k1");
            rig.Chart("A", "a.bms", "a");
            rig.Chart("K2", "k2.bms", "k2");
            rig.Chart("B", "b.bms", "b");
            rig.Start();
            try
            {
                int calls = 0;
                DuplicateBatchOrganizeResult result = rig.Organize([("K1", ["A"]), ("K2", ["B"])],
                    _ => { calls++; throw new InvalidOperationException("receiver failure"); });

                Assert.AreEqual(2, calls);
                Assert.IsFalse(result.Canceled);
                Assert.IsTrue(result.Groups.All(group => group.Status == DuplicateBatchOrganizeGroupStatus.Completed));
                Assert.IsTrue(result.Receipt.MergeApplied);
                Assert.IsFalse(result.Receipt.SessionReceipt.HasRequiredFailure);
            }
            finally
            {
                rig.Library.RequestShutdown("batch-progress-failure");
            }
        });
    }

    /// <summary>
    /// 確定（永続化）に失敗した操作では、入力元の後片付けを行わず、物理的に成功していたグループ（統合・重複整理・一フォルダのグループ）を失敗にし、
    /// スキップしたグループはスキップのままにします。ごみ箱へ送った数は実数のままで、ごみ箱へ送った譜面の DB 行も残ります。
    /// 物理成功のグループを完了と報告する誤りと、失敗後に入力元を消す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_CommitFailure_FailsPhysicallySuccessfulGroupsAndKeepsEveryFact()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath) { RegisteredRoots = ["B"] };
            rig.Chart("K1", "k1.bms", "k1");
            string keepDuplicate = rig.Chart("K1", "k1dup.bms", "k1");
            rig.Chart("A1", "a1.bms", "a1");
            rig.Resource("A1", "res.wav", "resource", BaseTime);
            rig.Chart("K2", "k2.bms", "k2");
            rig.Chart("B", "b.bms", "b");
            rig.Chart("F", "f1.bms", "f", BaseTime.AddDays(-1));
            string folderDuplicate = rig.Chart("F", "f2.bms", "f");
            rig.Start();
            using (LR2SongDBExtended db = rig.OpenSongWrite())
            {
                db.Execute("CREATE TRIGGER fail_batch_commit BEFORE INSERT ON song WHEN NEW.path = '"
                    + rig.At("K1\\a1.bms").Replace("'", "''") + "' BEGIN SELECT RAISE(ABORT, 'forced batch commit failure'); END;");
            }
            try
            {
                List<KeyValuePair<string, string>> beforeRows = rig.SongRows();
                DuplicateBatchOrganizeResult result = rig.Organize([("K1", ["A1"]), ("K2", ["B"]), ("F", [])]);

                Assert.IsFalse(result.Receipt.HasDurableCommit);
                Assert.IsNotNull(result.Receipt.SessionReceipt.ApplyFailure);
                Assert.IsTrue(result.Receipt.SessionReceipt.HasRequiredFailure);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Failed, result.Groups[0].Status);
                Assert.AreEqual(0, result.Groups[0].MergedSources.Count);
                Assert.AreEqual(DuplicateBatchOrganizeSourceReason.NotCommitted, result.Groups[0].RetainedSources.Single().Reason);
                Assert.AreEqual(1, result.Groups[0].TrashedChartCount);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Skipped, result.Groups[1].Status);
                Assert.AreEqual(DuplicateBatchOrganizeGroupStatus.Failed, result.Groups[2].Status);
                Assert.AreEqual(1, result.Groups[2].TrashedChartCount);
                CollectionAssert.AreEquivalent(new[] { keepDuplicate, folderDuplicate }, rig.Mutations.ShellDeletedFiles.ToArray());
                Assert.IsTrue(File.Exists(rig.At("A1\\a1.bms")) && File.Exists(rig.At("A1\\res.wav")), "確定に失敗した操作は、入力元の後片付けを行いません。");
                CollectionAssert.IsSubsetOf(
                    beforeRows.Select(pair => pair.Key).ToArray(),
                    rig.SongRows().Select(pair => pair.Key).ToArray());
                Assert.IsTrue(File.Exists(rig.At("K1\\k1.bms")));
            }
            finally
            {
                rig.Library.RequestShutdown("batch-commit-failure");
            }
        });
    }

    /// <summary>
    /// 確定・索引反映・保守の回数は、グループ数に比例しません。一グループと三グループを別のDBで実行し、
    /// セッションの反映回数、カタログ版・一覧・重複群の公開の回数が同じで、成功した全 KEEP が保守済みであることを確認します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_ApplyAndPublicationCountsDoNotGrowWithGroupCount()
    {
        WithTemporarySongDb(databaseScope, oneDb => WithTemporarySongDb(databaseScope, threeDb =>
        {
            (LibraryMutationSessionApplyCounts Counts, Dictionary<string, int> Publications) one = RunCountedOrganize(oneDb, 1);
            (LibraryMutationSessionApplyCounts Counts, Dictionary<string, int> Publications) three = RunCountedOrganize(threeDb, 3);

            Assert.AreEqual(1, one.Counts.CatalogApplyCount);
            Assert.AreEqual(one.Counts, three.Counts, "グループ数が増えても、セッションの反映回数は変わりません。");
            CollectionAssert.AreEquivalent(one.Publications, three.Publications);
        }));
    }

    private (LibraryMutationSessionApplyCounts Counts, Dictionary<string, int> Publications) RunCountedOrganize(string songDbPath, int groupCount)
    {
        using var rig = new Rig(databaseScope, songDbPath);
        List<(string Keep, string[] Sources)> groups = [];
        for (int index = 1; index <= groupCount; index++)
        {
            rig.Chart("K" + index, "k.bms", "k" + index);
            rig.Chart("K" + index, "k2.bms", "k" + index);
            rig.Resource("K" + index, "sound.wav", "audio", BaseTime);
            // 同じ内容の二件は更新日時で選定が決まる（古い a.bms を残し、新しい b.bms を整理する）。同点を作らない。
            rig.Chart("A" + index, "a.bms", "a" + index);
            rig.Chart("A" + index, "b.bms", "a" + index, BaseTime.AddDays(1));
            groups.Add(("K" + index, ["A" + index]));
        }
        rig.Start();
        var publications = new Dictionary<string, int>();
        rig.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BMSLibrary.CatalogRevision) or nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion) or nameof(BMSLibrary.DuplicateChartGroups))
            {
                publications[args.PropertyName] = publications.GetValueOrDefault(args.PropertyName) + 1;
            }
        };
        try
        {
            DuplicateBatchOrganizeResult result = rig.Organize(groups);

            Assert.IsTrue(result.Groups.All(group => group.Status == DuplicateBatchOrganizeGroupStatus.Completed));
            Assert.IsTrue(result.Receipt.MergeApplied);
            Assert.IsTrue(result.Receipt.MaintenanceHadUpdates);
            string[] expectedTrashed = [.. Enumerable.Range(1, groupCount).SelectMany(index => new[] { Path.Combine(rig.At("K" + index), "k2.bms"), Path.Combine(rig.At("A" + index), "b.bms") })];
            CollectionAssert.AreEquivalent(expectedTrashed, rig.Mutations.ShellDeletedFiles.ToArray(), "残る譜面は更新日時と名前の長さの規則だけで決まります。");
            using LR2SongDBExtended db = rig.OpenSongReadOnly();
            for (int index = 1; index <= groupCount; index++)
            {
                foreach (string name in new[] { "k.bms", "a.bms" })
                {
                    string path = rig.At("K" + index + "\\" + name);
                    Assert.AreEqual(1, db.Table<LR2SongDBExtended.maintenance>().Count(row => row.path == path && row.wav_files_existing == 1),
                        "成功した全 KEEP の譜面が保守済みであること: " + path);
                }
            }
            return (result.Receipt.SessionReceipt.ApplyCounts, publications);
        }
        finally
        {
            rig.Library.RequestShutdown("batch-counts");
        }
    }

    /// <summary>
    /// 共通受付が使用中（Busy）の操作全体は、物理変更の前に未適用で返し、進捗を渡さず、全グループを未処理にします。案内は、既存の単体の統合と同じく、受付を取る入口が表示します。
    /// 受付を取れないのに物理変更へ進む誤りと、進捗を渡す誤り、グループの状態を未処理以外にする誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_BusyAdmission_RejectsTheWholeOperationWithOneNotice()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            using var rig = new Rig(databaseScope, songDbPath);
            rig.Chart("K", "k.bms", "k");
            rig.Chart("A", "a.bms", "a");
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                List<KeyValuePair<string, string>> beforeRows = rig.SongRows();
                var notices = new LibraryOperationNotices();
                var progress = new List<DuplicateBatchOrganizeProgress>();
                DuplicateBatchOrganizeResult result;
                using (LibraryFileMutationLease? held = rig.Library.Lr2Synchronization.TryBeginMutation("batch_busy_probe"))
                {
                    Assert.IsNotNull(held);
                    result = rig.Organize([("K", ["A"])], progress.Add, notices: notices);
                }

                // 共通受付（L）の使用中は、既存の単体の統合と同じく、モデルは案内を出さず未適用で返します。Busy の案内は、受付を取る操作の入口が一度だけ表示します。
                AssertWholeOperationRejected(result, notices, progress, expectedNoticeCount: 0);
                AssertSameTree(before, rig.Tree());
                CollectionAssert.AreEqual(beforeRows, rig.SongRows());
            }
            finally
            {
                rig.Library.RequestShutdown("batch-busy");
            }
        });
    }

    /// <summary>
    /// 起動後に実差分読込みでパスが収束していない（カタログ未収束）操作全体は、物理変更の前に未適用で返し、既存の案内を一件だけ出し、全グループを未処理にします。
    /// 未収束のカタログへ物理変更を行う誤りと、拒否の案内を出さない・重ねて出す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_CatalogNotConverged_RejectsTheWholeOperationWithOneNotice()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string source = Path.Combine(root, "Source"), destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);
            string plain = Path.Combine(source, "chart.bms"), dot = Path.Combine(source, ".", "chart.bms"), kept = Path.Combine(destination, "kept.bms");
            File.WriteAllText(plain, BmsText("source"));
            File.WriteAllText(kept, BmsText("kept"));
            using (LR2SongDBExtended setup = databaseScope.OpenSongWrite(songDbPath))
            {
                foreach (string path in new[] { dot, kept })
                {
                    ChartFile row = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)) with { Path = path };
                    setup.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(row), typeof(LR2SongDB.song));
                }
            }
            var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = false, PendingInstallEstimateMaxParallelPackages = 1 };
            IChartFileScanner scanner = CapturedChartFileScanner.FromFixture(
                [plain, kept],
                new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase) { [source] = [], [destination] = [] },
                [root]);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => options, scanner, settings: TestBmsFactory.CreateRootSettings([root]))
            { StartupBackgroundTaskScheduler = (_, _, _, _) => false };
            try
            {
                library.InitializeStartup(null);
                byte[] before = File.ReadAllBytes(plain);
                var notices = new LibraryOperationNotices();
                var progress = new List<DuplicateBatchOrganizeProgress>();

                DuplicateBatchOrganizeResult result = library.OrganizeDuplicateGroups(
                    new DuplicateBatchOrganizeRequest([new DuplicateBatchOrganizeGroup(destination, [source])]), 1, notices, progress.Add, CancellationToken.None);

                AssertWholeOperationRejected(result, notices, progress);
                CollectionAssert.AreEqual(before, File.ReadAllBytes(plain));
                Assert.IsTrue(Directory.Exists(source));
            }
            finally
            {
                library.RequestShutdown("batch-not-converged");
            }
        });
    }

    /// <summary>
    /// 管理出力（プレイリスト出力）の保護を取れない操作全体は、最初の物理変更の前に未適用で返し、既存の案内を一件だけ出します。
    /// 管理出力の保護を取らずに物理変更へ進む誤りと、拒否の案内を出さない・重ねて出す誤りを検出します。
    /// </summary>
    [TestMethod]
    public void OrganizeDuplicateGroups_ManagedOutputBusy_RejectsTheWholeOperationWithOneNotice()
    {
        WithTemporarySongDb(databaseScope, songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            var playlist = new ChartFileOperationSynchronizer();
            using var rig = new Rig(databaseScope, songDbPath)
            {
                PlaylistAdmission = playlist,
                Lr2Owner = new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(Path.Combine(root, "LR2"), root)),
                OperationModeLr2 = true,
                CustomFolderOutputBaseDir = root
            };
            rig.Chart("K", "k.bms", "k");
            rig.Chart("A", "a.bms", "a");
            rig.Start();
            try
            {
                SortedDictionary<string, byte[]> before = rig.Tree();
                var notices = new LibraryOperationNotices();
                var progress = new List<DuplicateBatchOrganizeProgress>();
                DuplicateBatchOrganizeResult result;
                Assert.IsTrue(playlist.TryEnter(out IDisposable? heldPlaylist));
                using (heldPlaylist)
                {
                    result = rig.Organize([("K", ["A"])], progress.Add, notices: notices);
                }

                AssertWholeOperationRejected(result, notices, progress);
                Assert.AreEqual(Resources.Warn_LibraryOperationBusy, notices.Snapshot().Single().MessageBoxText);
                AssertSameTree(before, rig.Tree());
            }
            finally
            {
                rig.Library.RequestShutdown("batch-managed-output-busy");
            }
        });
    }

    private static void AssertWholeOperationRejected(DuplicateBatchOrganizeResult result, LibraryOperationNotices notices, List<DuplicateBatchOrganizeProgress> progress, int expectedNoticeCount = 1)
    {
        Assert.IsFalse(result.Admitted);
        Assert.IsFalse(result.Canceled);
        Assert.IsFalse(result.Receipt.MergeApplied);
        Assert.AreEqual(0, result.Receipt.SessionReceipt.ConfirmedChangeCount);
        Assert.IsTrue(result.Groups.All(group => group.Status == DuplicateBatchOrganizeGroupStatus.Unprocessed
            && group.MergedSources.Count == 0 && group.RetainedSources.Count == 0 && group.TrashedChartCount == 0));
        Assert.AreEqual(0, progress.Count);
        Assert.AreEqual(expectedNoticeCount, notices.Snapshot().Count, "既存の案内の件数が一致しません。");
    }

    private static void AssertContentsRemain(HashSet<string> before, HashSet<string> after)
        => Assert.IsTrue(before.IsSubsetOf(after), "操作前に所持していた譜面のバイト列が、操作後に一件も残らない結果になりました。");

    private static void AssertFoldersUnchanged(SortedDictionary<string, byte[]> before, SortedDictionary<string, byte[]> after, IEnumerable<string> folders)
    {
        foreach (string folder in folders)
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            string[] expected = [.. before.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
            string[] actual = [.. after.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];
            CollectionAssert.AreEqual(expected, actual, folder + " のファイルの集合が変わりました。");
            foreach (string key in expected)
            {
                CollectionAssert.AreEqual(before[key], after[key], key + " のバイト列が変わりました。");
            }
        }
    }

    private static void AssertRowsUnchanged(List<KeyValuePair<string, string>> before, List<KeyValuePair<string, string>> after, IEnumerable<string> folders)
    {
        foreach (string folder in folders)
        {
            string prefix = folder.TrimEnd('\\') + "\\";
            CollectionAssert.AreEqual(
                before.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray(),
                after.Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray(),
                folder + " の DB 行が変わりました。");
        }
    }

    /// <summary>別のスレッドから、モデルの短期ロック（譜面・保留・初期化）の排他取得が待たずにできることで、呼出元スレッドが保持していないことを確かめます。</summary>
    private static bool AreModelLocksFree(BMSLibrary library)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        ReaderWriterLockSlimWrapper[] locks =
        [
            (ReaderWriterLockSlimWrapper)typeof(BMSLibrary).GetProperty("rwlockBMSFiles", flags)!.GetValue(library)!,
            (ReaderWriterLockSlimWrapper)typeof(BMSLibrary).GetField("rwlockPendingInstallCharts", flags)!.GetValue(library)!,
            (ReaderWriterLockSlimWrapper)typeof(BMSLibrary).GetField("rwlockBMSFilesInitializedMin", flags)!.GetValue(library)!
        ];
        return Task.Run(() =>
        {
            foreach (ReaderWriterLockSlimWrapper gate in locks)
            {
                using (gate.GetWriterGuard()) { }
            }
        }).Wait(TimeSpan.FromSeconds(10));
    }

    /// <summary>実ファイルとSQLiteを持つ、一回の一括整理の入力一式です。</summary>
    private sealed class Rig : IDisposable
    {
        private readonly ApplicationDatabaseScope databaseScope;
        private readonly string songDbPath;

        internal Rig(ApplicationDatabaseScope databaseScope, string songDbPath)
        {
            this.databaseScope = databaseScope;
            this.songDbPath = songDbPath;
            Root = Path.Combine(Path.GetDirectoryName(songDbPath)!, "Library");
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal bool SmartOverwrite { get; init; }

        internal bool KeepProtectedByRenaming { get; init; }

        /// <summary>Root に加えて、登録BMSルートとして設定するフォルダの相対パス。登録は設定（standalone）から捕捉されます。</summary>
        internal string[] RegisteredRoots { get; init; } = [];

        internal TestFileMutationService Mutations { get; } = new();

        internal RecordingDialogService Dialogs { get; } = new();

        internal IFileMutationService? MutationOverride { get; init; }

        /// <summary>プレイリスト出力の局所受付。管理出力の保護を取れない場合の再現に使います。</summary>
        internal ChartFileOperationSynchronizer? PlaylistAdmission { get; init; }

        internal Lr2ConfigOwner? Lr2Owner { get; init; }

        internal bool OperationModeLr2 { get; init; }

        internal string? CustomFolderOutputBaseDir { get; init; }

        internal TestBmsLibrary Library { get; private set; } = null!;

        /// <summary>フォルダを作らず、Root からの相対パスの絶対パスだけを返します。</summary>
        internal string At(string relative) => Path.Combine(Root, relative);

        internal string Dir(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        /// <summary>同じ識別子は同一のバイト列（同じ主ハッシュ）、異なる識別子は異なるバイト列の譜面を作ります。更新日時は固定します。</summary>
        internal string Chart(string folder, string name, string id, DateTime? time = null)
        {
            string path = Path.Combine(Dir(folder), name);
            File.WriteAllText(path, BmsText(id));
            File.SetLastWriteTimeUtc(path, time ?? BaseTime);
            return path;
        }

        /// <summary>同じ識別子は同一のバイト列の BMSON 譜面を作ります。</summary>
        internal string Bmson(string folder, string name, string id, DateTime? time = null)
        {
            string path = Path.Combine(Dir(folder), name);
            File.WriteAllText(path, BmsonText(id));
            File.SetLastWriteTimeUtc(path, time ?? BaseTime);
            return path;
        }

        internal string Resource(string folder, string relative, string content, DateTime time)
        {
            string path = Path.Combine(Dir(folder), relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, time);
            return path;
        }

        /// <summary>配下の全譜面をDBと所持へ登録し、実ライブラリを作ります。</summary>
        internal void Start()
        {
            ChartFile[] bms = [.. Directory.EnumerateFiles(Root, "*.bms", SearchOption.AllDirectories)
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            ChartFile[] bmson = [.. Directory.EnumerateFiles(Root, "*.bmson", SearchOption.AllDirectories)
                .Select(path => ChartTestValues.ReadBmson(path))];
            var options = new BmsLibraryOptionsSnapshot
            {
                OperationModeLR2DB = OperationModeLr2,
                LR2CustomFolderOutputBaseDir = CustomFolderOutputBaseDir,
                BMSInstallDir = Root,
                EnableSmartComponentOverwrite = SmartOverwrite,
                KeepSmartOverwriteProtectedFilesByRenaming = KeepProtectedByRenaming
            };
            Library = new TestBmsLibrary(databaseScope, songDbPath, Lr2Owner, null, MutationOverride ?? Mutations, Dialogs,
                new TestUiScheduler(() => null), () => options, playlistOperationAdmission: PlaylistAdmission,
                settings: Lr2Owner == null ? TestBmsFactory.CreateRootSettings([Root, .. RegisteredRoots.Select(relative => Path.Combine(Root, relative))]) : null)
            { BmsCharts = bms, BmsonCharts = bmson };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.UpsertSongs(bms);
            gateway.UpsertBmsonSongs(bmson);
            LibraryResourceIndexTestSupport.GetOwner(Library).Replace(
                LibraryResourceIndex.CreateFromScanResult(ChartDirectoryScanBuilder.BuildFromRoots([Root])));
        }

        /// <summary>要求を作って一括整理を実行します。KEEPと移動元は、Root からの相対パスで指定します。</summary>
        internal DuplicateBatchOrganizeResult Organize(
            IEnumerable<(string Keep, string[] Sources)> groups,
            Action<DuplicateBatchOrganizeProgress>? progress = null,
            CancellationToken cancellationToken = default,
            LibraryOperationNotices? notices = null)
            => Library.OrganizeDuplicateGroups(
                new DuplicateBatchOrganizeRequest(groups.Select(group => new DuplicateBatchOrganizeGroup(
                    Path.Combine(Root, group.Keep), group.Sources.Select(source => Path.Combine(Root, source))))),
                1,
                notices ?? new LibraryOperationNotices(),
                progress,
                cancellationToken);

        internal LR2SongDBExtended OpenSongWrite() => databaseScope.OpenSongWrite(songDbPath);

        internal LR2SongDBExtended OpenSongReadOnly() => databaseScope.OpenSongReadOnly(songDbPath);

        /// <summary>Root 配下の全ファイルを、相対パスとバイト列で返します。</summary>
        internal SortedDictionary<string, byte[]> Tree()
            => new(Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(Root, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        /// <summary>Root 配下の譜面ファイルのバイト列を、重複を除いた集合で返します。</summary>
        internal HashSet<string> ChartContents()
            => [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".bms", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bmson", StringComparison.OrdinalIgnoreCase))
                .Select(path => Convert.ToBase64String(File.ReadAllBytes(path)))];

        /// <summary>指定した内容の譜面が、指定したフォルダの直下に何件あるかを返します。</summary>
        internal int CountDirectCharts(string folder, string id, bool bmson = false)
        {
            string directory = Path.Combine(Root, folder);
            string expected = bmson ? BmsonText(id) : BmsText(id);
            return !Directory.Exists(directory) ? 0 : Directory.EnumerateFiles(directory, bmson ? "*.bmson" : "*.bms")
                .Count(path => File.ReadAllText(path) == expected);
        }

        /// <summary>DB の譜面行を、Root からの相対パスと主ハッシュで返します。</summary>
        internal List<KeyValuePair<string, string>> SongRows()
        {
            using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
            return [.. db.Table<LR2SongDB.song>().Select(row => new KeyValuePair<string, string>(Path.GetRelativePath(Root, row.path), row.hash))
                .Concat(db.Table<LR2SongDBExtended.bmson_song>().Select(row => new KeyValuePair<string, string>(Path.GetRelativePath(Root, row.path), "bmson")))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)];
        }

        public void Dispose()
        {
        }
    }

    private static string BmsText(string id)
        => "#PLAYER 1\r\n#TITLE batch\r\n#ARTIST batch\r\n#BPM 120\r\n#WAV01 sound.wav\r\n#COMMENT " + id + "\r\n#00111:01\r\n";

    private static string BmsonText(string id)
        => "{\"version\":\"1.0.0\",\"info\":{\"title\":\"" + id + "\",\"artist\":\"batch\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[]}";

    private static void AssertSameTree(SortedDictionary<string, byte[]> expected, SortedDictionary<string, byte[]> actual)
    {
        CollectionAssert.AreEqual(expected.Keys.ToArray(), actual.Keys.ToArray(),
            "相対パスの集合が、単体を順に適用した結果と一致しません。");
        foreach (KeyValuePair<string, byte[]> item in expected)
        {
            CollectionAssert.AreEqual(item.Value, actual[item.Key], item.Key + " のバイト列が一致しません。");
        }
    }
}
