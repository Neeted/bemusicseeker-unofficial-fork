using System;
using System.Collections.Generic;
using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FolderDirectoryEnumerationServiceTests
{
    /// <summary>
    /// 正規化済み対象の捕捉時刻をそのまま探索結果へ含め、時刻の欠落や対象キーの取り違えを検出します。
    /// </summary>
    [TestMethod]
    public void CreateEntriesFromSurface_UsesDirectNormalizedTargetLookup()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderDirectoryEnumerationServiceTests), Guid.NewGuid().ToString("N"));
        string target = Lr2FolderPath.NormalizeDirectoryPath(Path.Combine(testDirectory, "BMS", "Table"));
        DateTime timestamp = new(2026, 6, 8, 1, 2, 3, DateTimeKind.Utc);
        var sourceEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [target] = new RootFileEnumerationEntry(target, timestamp)
        };

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(sourceEntries, [target]);

        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries.TryGetValue(target, out RootFileEnumerationEntry? entry));
        Assert.AreEqual(timestamp, entry!.LastWriteTimeUtc);
    }

    /// <summary>
    /// 辞書キーが対象パスでなくてもentryの実パスから対象を拾い、列挙入口のキー表現で正常対象を落とす誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateEntriesFromSurface_FallsBackToEntryPathWhenKeyIsNotNormalizedTarget()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderDirectoryEnumerationServiceTests), Guid.NewGuid().ToString("N"));
        string target = Lr2FolderPath.NormalizeDirectoryPath(Path.Combine(testDirectory, "BMS", "Table"));
        var sourceEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["raw-key"] = new RootFileEnumerationEntry(target)
        };

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(sourceEntries, [target]);

        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries.ContainsKey(target));
    }

    /// <summary>
    /// 一括列挙にない許可ルート内の対象をmetadataから補完し、末尾区切りの重複を除いて既存のルート項目を保つことを確認します。
    /// </summary>
    [TestMethod]
    public void CreateEntriesFromGroupedResult_CompletesMissingExistingTargetWithinAllowedRoots()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderDirectoryEnumerationServiceTests), Guid.NewGuid().ToString("N"));
        string root = Path.Combine(testDirectory, "BMS");
        string target = Path.Combine(root, "#minbp", "InsaneTable");
        DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
        RootFileEnumerationResult result = Lr2SongDbSyncTestSupport.CreateRootFileEnumerationResult(
            RootFileEnumerationService.DirectoriesGroupName,
            [new RootFileEnumerationEntry(root, timestamp.AddMinutes(-1))]);

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedResult(
                result,
                [root + Path.DirectorySeparatorChar],
                [root, target + Path.DirectorySeparatorChar, target],
                path => string.Equals(path, Lr2FolderPath.NormalizeDirectoryPath(target), StringComparison.OrdinalIgnoreCase)
                    ? new RootFileEnumerationEntry(path, timestamp)
                    : null);

        string normalizedRoot = Lr2FolderPath.NormalizeDirectoryPath(root);
        string normalizedTarget = Lr2FolderPath.NormalizeDirectoryPath(target);
        Assert.AreEqual(2, entries.Count);
        Assert.IsTrue(entries.ContainsKey(normalizedRoot));
        Assert.IsTrue(entries.TryGetValue(normalizedTarget, out RootFileEnumerationEntry? targetEntry));
        Assert.AreEqual(timestamp, targetEntry!.LastWriteTimeUtc);
    }

    /// <summary>
    /// 範囲外はmetadataを読まず、未存在・読取不能は補完せずに正常な範囲内だけを含め、無断の範囲拡張や失敗の正常化を検出します。
    /// </summary>
    [TestMethod]
    public void CompleteMissingEntriesFromDirectoryMetadata_LeavesOutsideMissingAndUnreadableTargetsAbsent()
    {
        string testDirectory = Path.Combine(Path.GetTempPath(), nameof(Lr2FolderDirectoryEnumerationServiceTests), Guid.NewGuid().ToString("N"));
        string root = Path.Combine(testDirectory, "BMS");
        string inside = Path.Combine(root, "Inside");
        string outside = Path.Combine(testDirectory, "Outside");
        string missing = Path.Combine(root, "Missing");
        string inaccessible = Path.Combine(root, "Inaccessible");
        DateTime timestamp = new(2026, 6, 10, 1, 2, 3, DateTimeKind.Utc);
        var readPaths = new List<string>();

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CompleteMissingEntriesFromDirectoryMetadata(
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                [root],
                [inside, outside, missing, inaccessible],
                path =>
                {
                    readPaths.Add(path);
                    return string.Equals(path, Lr2FolderPath.NormalizeDirectoryPath(inside), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(path, Lr2FolderPath.NormalizeDirectoryPath(outside), StringComparison.OrdinalIgnoreCase)
                        ? new RootFileEnumerationEntry(path, timestamp)
                        : null;
                });

        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(inside)));
        Assert.IsFalse(entries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(outside)));
        Assert.IsFalse(entries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(missing)));
        Assert.IsFalse(entries.ContainsKey(Lr2FolderPath.NormalizeDirectoryPath(inaccessible)));
        CollectionAssert.DoesNotContain(readPaths, Lr2FolderPath.NormalizeDirectoryPath(outside));
    }

    /// <summary>
    /// bridge契約の失敗は元識別を持つ例外へ伝え、別のファイル読取りで隠す処理を検出します。
    /// </summary>
    [TestMethod]
    public void CreateEntriesFromGroupedResult_PropagatesBridgeContractFailure()
    {
        var result = new RootFileEnumerationResult
        {
            Success = false,
            ErrorReason = "bridge_contract_mismatch:test"
        };

        InvalidOperationException exception = Assert.ThrowsException<InvalidOperationException>(() =>
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedResult(
                result,
                [],
                [],
                _ => throw new AssertFailedException("filesystem reader must not run after bridge failure")));

        StringAssert.Contains(exception.Message, "bridge_contract_mismatch:test");
    }

    /// <summary>
    /// 通常の列挙失敗でも追加のFS読取りへ進まず空結果を返し、失敗した一括列挙を別探索へ読み替える誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateEntriesFromGroupedResult_DoesNotReadFilesystemAfterNonBridgeFailure()
    {
        var result = new RootFileEnumerationResult
        {
            Success = false,
            ErrorReason = "enumeration_failed:test"
        };
        bool readerCalled = false;

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedResult(
                result,
                [Path.GetTempPath()],
                [Path.GetTempPath()],
                _ =>
                {
                    readerCalled = true;
                    return null;
                });

        Assert.AreEqual(0, entries.Count);
        Assert.IsFalse(readerCalled);
    }
}
