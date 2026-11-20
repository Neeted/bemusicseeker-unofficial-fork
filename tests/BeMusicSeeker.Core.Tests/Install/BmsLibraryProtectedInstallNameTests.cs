using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.InstallServiceComponentTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>保護3拡張子の採番は未処理の通常名も避け、正逆の実計画入力から同じ最終bytesを保ちます。処理済みだけの予約と既存内容の上書きを検出します。</summary>
    [TestMethod]
    [DataRow(".txt", false)]
    [DataRow(".txt", true)]
    [DataRow(".bmx", false)]
    [DataRow(".bmx", true)]
    [DataRow(".pmx", false)]
    [DataRow(".pmx", true)]
    public void BuildFinalComponentMovePlan_ReservesAllNormalNamesRegardlessOfOrder(string extension, bool reverse)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            WriteProtectedNameFile(destination, "readme" + extension, [1], false);
            string first = WriteProtectedNameFile(source, "readme" + extension, [2, 3], true);
            string second = WriteProtectedNameFile(source, "readme(1)" + extension, [4, 5, 6], true);
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan(reverse ? [second, first] : [first, second], destination, null);

            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(candidates.PlanItems, [], destination, ProtectedNameOptions());

            CollectionAssert.AreEquivalent(new[] { first + "=>" + Path.Combine(destination, "readme(2)" + extension), second + "=>" + Path.Combine(destination, "readme(1)" + extension) },
                plan.PlanItems.Select(item => item.SourcePath + "=>" + item.DestinationPath).ToArray());
            FileDbMutationReceipt receipt = ExecuteProtectedNamePlan(plan);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]>
            {
                ["readme" + extension] = [1],
                ["readme(1)" + extension] = [4, 5, 6],
                ["readme(2)" + extension] = [2, 3]
            });
            Assert.IsFalse(File.Exists(first));
            Assert.IsFalse(File.Exists(second));
        });
    }

    /// <summary>通常leafの祖先directory、通常(2)と実在(3)を採番に奪わず、正逆計画と実serviceで(4)・元階層・全bytesを保ちます。</summary>
    /// <remarks>順序の保証は明示計画入力、実物理/回収の接続は同じfixtureのservice一回へ分担し、OS列挙順から推測しません。</remarks>
    [TestMethod]
    public void BuildFinalComponentMovePlan_ReservesDirectoryAncestorsAndMultipleSuffixes()
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            WriteProtectedNameFile(destination, "readme.txt", [1], false);
            WriteProtectedNameFile(destination, "readme(3).txt", [9, 9, 9, 9], false);
            string first = WriteProtectedNameFile(source, "readme.txt", [2, 3], true);
            string second = WriteProtectedNameFile(source, "readme(2).txt", [4, 5, 6], true);
            string tone = WriteProtectedNameFile(source, Path.Combine("readme(1).txt", "Audio", "tone.wav"), [7, 8], true);
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan(
                [first, second, Path.Combine(source, "readme(1).txt")], destination, null);
            string[] expected = [first + "=>" + Path.Combine(destination, "readme(4).txt"), second + "=>" + Path.Combine(destination, "readme(2).txt"),
                tone + "=>" + Path.Combine(destination, "readme(1).txt", "Audio", "tone.wav")];
            foreach (IReadOnlyList<ComponentMovePlanItem> ordered in new[] { candidates.PlanItems, candidates.PlanItems.AsEnumerable().Reverse().ToList() })
            {
                (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(ordered, [], destination, ProtectedNameOptions());
                CollectionAssert.AreEquivalent(expected, plan.PlanItems.Select(item => item.SourcePath + "=>" + item.DestinationPath).ToArray());
            }

            ChartPackage package = ChartPackageTestExtensions.CreatePackage(Enumerable.Empty<ChartFile>());
            package.path = source;
            bool callbackSawSources = false;
            FileDbMutationReceipt receipt = service.MovePackageFilesWithReceipt(package, destination, ProtectedNameOptions(),
                (_, _) => throw new AssertFailedException("明示宛先を新規命名しません。"), exception => exception.Message,
                new RealFileMutationService(), new LibraryOperationNotices(), new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
                new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree), _ => { }, _ =>
                {
                    callbackSawSources = File.Exists(first) && File.Exists(second) && File.Exists(tone);
                    return FileDbMutationCommitResult.Durable();
                }, sourceCleanupPolicy: PackageSourceCleanupPolicy.PreserveUnconsumedContents, showMessageBoxOnInstallFail: false);

            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            Assert.IsTrue(callbackSawSources);
            Assert.IsTrue(Directory.Exists(Path.Combine(destination, "readme(1).txt")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "readme(1).txt")));
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]>
            {
                ["readme.txt"] = [1],
                ["readme(3).txt"] = [9, 9, 9, 9],
                ["readme(4).txt"] = [2, 3],
                ["readme(2).txt"] = [4, 5, 6],
                [Path.Combine("readme(1).txt", "Audio", "tone.wav")] = [7, 8]
            });
            Assert.IsFalse(Directory.Exists(source));
        });
    }

    /// <summary>元から採番されたstemにも従来の(n)を付け、正逆入力でも別通常名を奪わず元のsuffix文字列を解析し直しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildFinalComponentMovePlan_PreservesAlreadySuffixedStem(bool reverse)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            WriteProtectedNameFile(destination, "readme(1).txt", [1], false);
            string first = WriteProtectedNameFile(source, "readme(1).txt", [2, 3], true);
            string second = WriteProtectedNameFile(source, "readme(1)(1).txt", [4, 5, 6], true);
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan(reverse ? [second, first] : [first, second], destination, null);
            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(candidates.PlanItems, [], destination, ProtectedNameOptions());
            FileDbMutationReceipt receipt = ExecuteProtectedNamePlan(plan);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            Assert.IsTrue(receipt.DurableCommit);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]>
            {
                ["readme(1).txt"] = [1],
                ["readme(1)(1).txt"] = [4, 5, 6],
                ["readme(1)(2).txt"] = [2, 3]
            });
            Assert.IsFalse(File.Exists(first));
            Assert.IsFalse(File.Exists(second));
        });
    }

    /// <summary>通常の非保護資源は新しい入力で同名へ上書きし、計画予約だけを理由に採番・省略へ変更しません。</summary>
    [TestMethod]
    public void BuildFinalComponentMovePlan_PreservesOrdinaryResourceOverwrite()
    {
        WithTemporaryDirectory(root =>
        {
            string destination = Path.Combine(root, "Destination");
            WriteProtectedNameFile(destination, "sound.wav", [1], false);
            string source = WriteProtectedNameFile(Path.Combine(root, "Source"), "sound.wav", [2, 3], true);
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan([source], destination, null);
            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(candidates.PlanItems, [], destination, ProtectedNameOptions());
            FileDbMutationReceipt receipt = ExecuteProtectedNamePlan(plan);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]> { ["sound.wav"] = [2, 3] });
            Assert.IsFalse(File.Exists(source));
        });
    }

    /// <summary>
    /// 所持集合にない譜面拡張子（5種・大文字小文字違い）のファイルが、宛先の同名で内容の異なるファイルを、スマート上書き・保護改名の設定と更新日時の新・旧・同によらず上書きしません。
    /// 入力元は「(1)」付きの空き名へ置かれ、双方のbytesが残ります。サイズと更新日時の一致を同内容とみなす誤り、保護拡張子の集合へ足すだけの実装（保護改名オン時のみ働く）、設定ごとに結果が変わる実装を検出します。
    /// </summary>
    [TestMethod]
    [DataRow(".bms", false, false, "newer")]
    [DataRow(".bms", false, true, "newer")]
    [DataRow(".bms", true, false, "newer")]
    [DataRow(".bms", true, true, "newer")]
    [DataRow(".bms", false, false, "older")]
    [DataRow(".bms", false, true, "older")]
    [DataRow(".bms", true, false, "older")]
    [DataRow(".bms", true, true, "older")]
    [DataRow(".bms", false, false, "same")]
    [DataRow(".bms", false, true, "same")]
    [DataRow(".bms", true, false, "same")]
    [DataRow(".bms", true, true, "same")]
    [DataRow(".bme", true, false, "newer")]
    [DataRow(".bml", true, false, "older")]
    [DataRow(".pms", true, false, "same")]
    [DataRow(".bmson", true, false, "older")]
    [DataRow(".BMSON", true, false, "older")]
    [DataRow(".BMS", false, false, "newer")]
    public void BuildFinalComponentMovePlan_UnownedChartExtensionNeverOverwritesDifferentDestination(string extension, bool smart, bool rename, string timeKind)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            // 同サイズ・同更新日時でも内容が異なる入力で、サイズと日時だけの同一判定を検出する。
            string destinationFile = WriteProtectedNameFile(destination, "extra" + extension, [1, 1], false);
            string sourceFile = WriteProtectedNameFile(source, "extra" + extension, [2, 2], false);
            File.SetLastWriteTimeUtc(destinationFile, new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(sourceFile, new DateTime(2020, 1, timeKind == "newer" ? 3 : timeKind == "older" ? 1 : 2, 0, 0, 0, DateTimeKind.Utc));
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan([sourceFile], destination, null);

            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(candidates.PlanItems, [], destination, SmartRenameOptions(smart, rename));

            CollectionAssert.AreEqual(new[] { sourceFile + "=>" + Path.Combine(destination, "extra(1)" + extension) },
                plan.PlanItems.Select(item => item.SourcePath + "=>" + item.DestinationPath).ToArray());
            FileDbMutationReceipt receipt = ExecuteProtectedNamePlan(plan);
            Assert.AreEqual(FileDbMutationTerminalState.Completed, receipt.TerminalState);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]>
            {
                ["extra" + extension] = [1, 1],
                ["extra(1)" + extension] = [2, 2]
            });
            Assert.IsFalse(File.Exists(sourceFile));
        });
    }

    /// <summary>宛先に同名がない譜面拡張子のファイルは採番せず同じ名前のまま置きます。常に採番する誤りを検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildFinalComponentMovePlan_UnownedChartExtensionWithoutDestinationKeepsName(bool smart)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            Directory.CreateDirectory(destination);
            string sourceFile = WriteProtectedNameFile(source, "extra.bmson", [2, 2], true);
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan([sourceFile], destination, null);

            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(candidates.PlanItems, [], destination, SmartRenameOptions(smart, false));

            Assert.AreEqual(FileDbMutationTerminalState.Completed, ExecuteProtectedNamePlan(plan).TerminalState);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]> { ["extra.bmson"] = [2, 2] });
            Assert.IsFalse(File.Exists(sourceFile));
        });
    }

    /// <summary>
    /// 譜面拡張子でない「.bmsx」は通常の資源として従来どおり扱い、スマート上書きオフ・新しい入力は上書き、オンで古い入力は宛先を保って採番しません。
    /// 前方一致で譜面拡張子とみなす誤りを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, true, new byte[] { 2, 2 })]
    [DataRow(true, true, new byte[] { 2, 2 })]
    [DataRow(true, false, new byte[] { 1, 1 })]
    public void BuildFinalComponentMovePlan_BmsxStaysOrdinaryResource(bool smart, bool sourceNewer, byte[] expected)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            WriteProtectedNameFile(destination, "extra.bmsx", [1, 1], !sourceNewer);
            string sourceFile = WriteProtectedNameFile(source, "extra.bmsx", [2, 2], sourceNewer);
            if (!sourceNewer) { File.SetLastWriteTimeUtc(sourceFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)); File.SetLastWriteTimeUtc(Path.Combine(destination, "extra.bmsx"), new DateTime(2020, 1, 3, 0, 0, 0, DateTimeKind.Utc)); }
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan([sourceFile], destination, null);

            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(candidates.PlanItems, [], destination, SmartRenameOptions(smart, false));

            Assert.AreEqual(FileDbMutationTerminalState.Completed, ExecuteProtectedNamePlan(plan).TerminalState);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]> { ["extra.bmsx"] = expected });
            Assert.IsFalse(File.Exists(sourceFile));
        });
    }

    /// <summary>
    /// 譜面拡張子の衝突の採番も、後続の通常の予定先・未作成の親ディレクトリ・渡された譜面の実宛先・宛先に既にある別内容の「(n)」を奪わず、入力の順序によらず同じ結果になります。
    /// スマート上書きのオフ・オンの両方で、予約を無視した採番や処理済みだけを予約する実装を検出します。
    /// </summary>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, true)]
    public void BuildFinalComponentMovePlan_UnownedChartExtensionReservesAllNamesRegardlessOfOrder(bool smart, bool rename, bool reverse)
    {
        WithTemporaryDirectory(root =>
        {
            string source = Path.Combine(root, "Source");
            string destination = Path.Combine(root, "Destination");
            WriteProtectedNameFile(destination, "extra.bmson", [1], false);
            WriteProtectedNameFile(destination, "extra(3).bmson", [9, 9, 9, 9], false);
            string first = WriteProtectedNameFile(source, "extra.bmson", [2, 3], true);
            string second = WriteProtectedNameFile(source, "extra(1).bmson", [4, 5, 6], true);
            string tone = WriteProtectedNameFile(source, Path.Combine("extra(2).bmson", "Audio", "tone.wav"), [7, 8], true);
            string chartDestination = Path.Combine(destination, "extra(4).bmson");
            var service = new BmsLibraryPackageInstallService();
            ComponentMovePlanBuildResult candidates = service.BuildComponentMovePlan([first, second, Path.Combine(source, "extra(2).bmson")], destination, null);
            IReadOnlyList<ComponentMovePlanItem> ordered = reverse ? candidates.PlanItems.AsEnumerable().Reverse().ToList() : candidates.PlanItems;

            (IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan = service.BuildFinalComponentMovePlan(
                ordered, [new KeyValuePair<string, string>(Path.Combine(source, "chart.bms"), chartDestination)], destination, SmartRenameOptions(smart, rename));

            CollectionAssert.AreEquivalent(new[]
            {
                first + "=>" + Path.Combine(destination, "extra(5).bmson"),
                second + "=>" + Path.Combine(destination, "extra(1).bmson"),
                tone + "=>" + Path.Combine(destination, "extra(2).bmson", "Audio", "tone.wav")
            }, plan.PlanItems.Select(item => item.SourcePath + "=>" + item.DestinationPath).ToArray());
            Assert.AreEqual(FileDbMutationTerminalState.Completed, ExecuteProtectedNamePlan(plan).TerminalState);
            AssertProtectedNameFiles(destination, new Dictionary<string, byte[]>
            {
                ["extra.bmson"] = [1],
                ["extra(3).bmson"] = [9, 9, 9, 9],
                ["extra(5).bmson"] = [2, 3],
                ["extra(1).bmson"] = [4, 5, 6],
                [Path.Combine("extra(2).bmson", "Audio", "tone.wav")] = [7, 8]
            });
        });
    }

    private static BmsLibraryOptionsSnapshot SmartRenameOptions(bool smart, bool rename) => new()
    {
        EnableSmartComponentOverwrite = smart,
        KeepSmartOverwriteProtectedFilesByRenaming = rename,
        OperationModeLR2DB = false,
        DeletePendingPackageSourceAfterInstall = false
    };

    private static BmsLibraryOptionsSnapshot ProtectedNameOptions() => new()
    {
        EnableSmartComponentOverwrite = true,
        KeepSmartOverwriteProtectedFilesByRenaming = true,
        OperationModeLR2DB = false,
        DeletePendingPackageSourceAfterInstall = false
    };

    /// <summary>本番計画が選んだ宛先と消費元を実executorへ渡すfixtureです。DB確定は局所代替で、実SQLite接続は別の導入・Merge代表が分担します。</summary>
    private static FileDbMutationReceipt ExecuteProtectedNamePlan((IReadOnlyList<ComponentMovePlanItem> PlanItems, IReadOnlyList<string> SourceCleanupFiles) plan)
    {
        var paths = plan.PlanItems.Select(item => new FileDbMutationPathPlan(item.SourcePath, item.DestinationPath,
            LongPathFileSystem.CreateMutationSiblingPath(item.DestinationPath, "stage"),
            LongPathFileSystem.EntryExists(item.DestinationPath) ? LongPathFileSystem.CreateMutationSiblingPath(item.DestinationPath, "backup") : string.Empty,
            isDirectory: false)).ToList();
        var mutation = new FileDbMutationPlan(Guid.NewGuid(), paths, plan.SourceCleanupFiles, [], recursiveSourceCleanup: false);
        FileDbMutationReceipt receipt = new FileDbMutationExecutor(mutation, new RealFileMutationService(),
            new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly), new FileMutationOptions(ReadOnlyNormalizationScope.RecursiveDirectoryTree))
            .Execute(() => FileDbMutationCommitResult.Durable());
        foreach (FileDbMutationPathPlan path in paths)
        {
            Assert.IsFalse(LongPathFileSystem.EntryExists(path.StagingPath));
            Assert.IsFalse(LongPathFileSystem.EntryExists(path.BackupPath));
        }
        return receipt;
    }
}
