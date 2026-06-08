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
        Assert.IsTrue(entries.TryGetValue(target, out RootFileEnumerationEntry entry));
        Assert.AreEqual(timestamp, entry.LastWriteTimeUtc);
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
