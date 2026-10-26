using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
                new RealFileMutationService(), null, new FileMutationOptions(ReadOnlyNormalizationScope.TargetOnly),
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

    private static BmsLibraryOptionsSnapshot ProtectedNameOptions() => new()
    {
        EnableSmartComponentOverwrite = true,
        KeepSmartOverwriteProtectedFilesByRenaming = true,
        OperationModeLR2DB = false,
        DeletePendingPackageSourceAfterInstall = false
    };

    private static string WriteProtectedNameFile(string directory, string relativePath, byte[] bytes, bool newer)
    {
        string path = Path.Combine(directory, relativePath);
        if (Path.GetDirectoryName(path) is string parent) { Directory.CreateDirectory(parent); }
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, newer ? 3 : 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static void AssertProtectedNameFiles(string directory, IReadOnlyDictionary<string, byte[]> expected)
    {
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path)).ToArray());
        foreach ((string relativePath, byte[] bytes) in expected) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(directory, relativePath))); }
    }

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
