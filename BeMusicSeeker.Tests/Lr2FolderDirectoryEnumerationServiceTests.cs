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
    [TestMethod]
    public void CreateEntriesFromSurface_UsesDirectNormalizedTargetLookup()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string target = Lr2FolderPath.NormalizeDirectoryPath(Path.Combine(scope.DirectoryPath, "BMS", "Table"));
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

    [TestMethod]
    public void CreateEntriesFromSurface_FallsBackToEntryPathWhenKeyIsNotNormalizedTarget()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string target = Lr2FolderPath.NormalizeDirectoryPath(Path.Combine(scope.DirectoryPath, "BMS", "Table"));
        var sourceEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["raw-key"] = new RootFileEnumerationEntry(target)
        };

        IReadOnlyDictionary<string, RootFileEnumerationEntry> entries =
            Lr2FolderDirectoryEnumerationService.CreateEntriesFromSurface(sourceEntries, [target]);

        Assert.AreEqual(1, entries.Count);
        Assert.IsTrue(entries.ContainsKey(target));
    }

    [TestMethod]
    public void CreateEntriesFromGroupedResult_CompletesMissingExistingTargetWithinAllowedRoots()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "BMS");
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

    [TestMethod]
    public void CompleteMissingEntriesFromDirectoryMetadata_LeavesOutsideMissingAndUnreadableTargetsAbsent()
    {
        using TestDirectoryScope scope = TestDirectoryScope.Create();
        string root = Path.Combine(scope.DirectoryPath, "BMS");
        string inside = Path.Combine(root, "Inside");
        string outside = Path.Combine(scope.DirectoryPath, "Outside");
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

    private sealed class TestDirectoryScope : IDisposable
    {
        private TestDirectoryScope(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static TestDirectoryScope Create()
        {
            string path = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectoryScope(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
