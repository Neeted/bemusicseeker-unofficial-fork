using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class RootFileEnumerationTests
{
    [TestMethod]
    public void FastEnumerator_ReturnsFileMetadataEntriesAndPathCompatibility()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnum_" + Guid.NewGuid().ToString("N"));
        string chartPath = Path.Combine(tempRoot, "song.bms");
        DateTime lastWriteTimeUtc = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(chartPath, "#PLAYER 1");
        File.SetLastWriteTimeUtc(chartPath, lastWriteTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), chartPath);
            RootFileEnumerationEntry entry = result.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, chartPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(chartPath, entry.Path);
            Assert.IsTrue(entry.LastWriteTimeUtc.HasValue);
            Assert.AreEqual(
                ToUnixSeconds(File.GetLastWriteTimeUtc(chartPath)),
                entry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(new FileInfo(chartPath).Length, entry.FileSize);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FastEnumerator_ReturnsLongPathChartEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumLong_" + Guid.NewGuid().ToString("N"));
        string longDirectoryPath = BuildLongDirectoryPath(tempRoot, "charts");
        string chartPath = Path.Combine(longDirectoryPath, "long-chart.bms");
        DateTime lastWriteTimeUtc = new(2026, 6, 24, 1, 2, 3, DateTimeKind.Utc);
        LongPathFileSystem.CreateDirectory(longDirectoryPath);
        WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE LongRoot\r\n");
        File.SetLastWriteTimeUtc(LongPathFileSystem.ToExtendedPath(chartPath), lastWriteTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), chartPath);
            RootFileEnumerationEntry entry = result.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, chartPath);
            Assert.IsNotNull(entry);
            Assert.AreEqual(chartPath, entry.Path);
            Assert.AreEqual(ToUnixSeconds(lastWriteTimeUtc), entry.LastWriteTimeUnixSeconds);
            Assert.IsTrue(entry.FileSize > 0);
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempRoot))
            {
                LongPathFileSystem.DeleteDirectory(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void RootFileEnumerationEntry_NormalizesLocalTimestampToUtcUnixSeconds()
    {
        DateTime utc = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        DateTime local = utc.ToLocalTime();

        var entry = new RootFileEnumerationEntry(@"D:\song.bms", local);

        Assert.AreEqual(utc, entry.LastWriteTimeUtc);
        Assert.AreEqual(ToUnixSeconds(utc), entry.LastWriteTimeUnixSeconds);
    }

    [TestMethod]
    public void RootFileEnumerationResult_KeepsCaseOnlyChartPathsDistinct()
    {
        string upperPath = @"D:\BMS\Pack\Song\Chart.bms";
        string lowerPath = @"D:\BMS\Pack\Song\chart.bms";
        var upperEntry = new RootFileEnumerationEntry(upperPath);
        var lowerEntry = new RootFileEnumerationEntry(lowerPath);
        var enumeration = new RootFileEnumerationResult();
        enumeration.InitializeGroup(ChartDirectoryScanBuilder.ChartGroupName);

        enumeration.AddEntry(ChartDirectoryScanBuilder.ChartGroupName, upperEntry);
        enumeration.AddEntry(ChartDirectoryScanBuilder.ChartGroupName, lowerEntry);

        Assert.AreEqual(2, enumeration.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).Count);
        Assert.AreSame(upperEntry, enumeration.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, upperPath));
        Assert.AreSame(lowerEntry, enumeration.GetEntry(ChartDirectoryScanBuilder.ChartGroupName, lowerPath));

        ChartScanResult scanResult = ChartDirectoryScanBuilder.BuildFromAbsolutePaths(
            [upperEntry, lowerEntry],
            [],
            [],
            [],
            []);
        Assert.AreEqual(2, scanResult.ChartFilePaths.Count);
        Assert.IsTrue(scanResult.ChartFileEntriesByPath.ContainsKey(upperPath));
        Assert.IsTrue(scanResult.ChartFileEntriesByPath.ContainsKey(lowerPath));
    }

    [TestMethod]
    public void FastEnumerator_ReturnsDirectoryMetadataEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumDirs_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Pack", "Song");
        DateTime rootWriteTimeUtc = new(2026, 6, 5, 3, 0, 0, DateTimeKind.Utc);
        DateTime nestedWriteTimeUtc = new(2026, 6, 5, 4, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(nestedDirectory);
        Directory.SetLastWriteTimeUtc(tempRoot, rootWriteTimeUtc);
        Directory.SetLastWriteTimeUtc(nestedDirectory, nestedWriteTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)]);

            Assert.IsTrue(result.Success);
            RootFileEnumerationEntry rootEntry = result.GetEntry(RootFileEnumerationService.DirectoriesGroupName, tempRoot);
            RootFileEnumerationEntry nestedEntry = result.GetEntry(RootFileEnumerationService.DirectoriesGroupName, nestedDirectory);
            Assert.IsNotNull(rootEntry);
            Assert.IsNotNull(nestedEntry);
            Assert.AreEqual(ToUnixSeconds(rootWriteTimeUtc), rootEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(nestedWriteTimeUtc), nestedEntry.LastWriteTimeUnixSeconds);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void BuildDirectoriesQuery_UsesRootPathWithoutTrailingSeparator()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumDirsQuery_" + Guid.NewGuid().ToString("N"));

        string query = EverythingNative.BuildDirectoriesQuery([root + Path.DirectorySeparatorChar]);

        StringAssert.Contains(query, "folder:");
        StringAssert.Contains(query, "path:\"" + root.Replace("\"", "\"\"") + "\"");
        Assert.IsFalse(query.Contains("path:\"" + (root + Path.DirectorySeparatorChar).Replace("\"", "\"\"") + "\""));
    }

    [TestMethod]
    public void BuildFilesQuery_IncludesExcludedDirectoriesWithTrailingSeparator()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumFilesQuery_" + Guid.NewGuid().ToString("N"));
        string excluded = Path.Combine(root, "Output", "Table");

        string query = EverythingNative.BuildFilesQuery([root], ["lr2folder"], [excluded]);

        StringAssert.Contains(query, "file:");
        StringAssert.Contains(query, "!<path:\"" + (excluded + Path.DirectorySeparatorChar).Replace("\"", "\"\"") + "\">");
    }

    [TestMethod]
    public void FastEnumerator_ExcludesConfiguredSubtreeAndKeepsPrefixSibling()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumExclude_" + Guid.NewGuid().ToString("N"));
        string excludedDirectory = Path.Combine(tempRoot, "Output", "Table");
        string prefixSiblingDirectory = Path.Combine(tempRoot, "Output", "TableOther");
        string externalPath = Path.Combine(tempRoot, "External.lr2folder");
        string excludedPath = Path.Combine(excludedDirectory, "Managed.lr2folder");
        string siblingPath = Path.Combine(prefixSiblingDirectory, "Sibling.lr2folder");
        Directory.CreateDirectory(excludedDirectory);
        Directory.CreateDirectory(prefixSiblingDirectory);
        File.WriteAllText(externalPath, "#TITLE External");
        File.WriteAllText(excludedPath, "#TITLE Managed");
        File.WriteAllText(siblingPath, "#TITLE Sibling");

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup("lr2folder", [".lr2folder"], excludedDirectories: [excludedDirectory])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths("lr2folder").ToList(), externalPath);
            CollectionAssert.Contains(result.GetPaths("lr2folder").ToList(), siblingPath);
            CollectionAssert.DoesNotContain(result.GetPaths("lr2folder").ToList(), excludedPath);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FastEnumerator_ExcludedRootReturnsNoDirectoryEntries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumExcludeRoot_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "Nested"));

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true, excludedDirectories: [tempRoot])]);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0, result.GetPaths(RootFileEnumerationService.DirectoriesGroupName).Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void RootFileEnumerationService_ClassifiesBridgeContractFailures()
    {
        Assert.IsTrue(RootFileEnumerationService.IsBridgeContractFailure("bridge_grouped_enumeration_export_missing"));
        Assert.IsTrue(RootFileEnumerationService.IsBridgeContractFailure("bridge_dll_not_found:D:\\app\\native\\EverythingBridge_x64.dll"));
        Assert.IsFalse(RootFileEnumerationService.IsBridgeContractFailure("bridge_grouped_query_failed:2"));
        Assert.IsFalse(RootFileEnumerationService.IsBridgeContractFailure("empty_results_with_roots"));
    }

    [TestMethod]
    public void FastEnumerator_ReturnsUnifiedMetadataSurfaceForTextLr2FolderAndDirectories()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumUnified_" + Guid.NewGuid().ToString("N"));
        string packDirectory = Path.Combine(tempRoot, "Pack");
        string folderInfoPath = Path.Combine(packDirectory, "folderinfo.txt");
        string lr2FolderPath = Path.Combine(tempRoot, "Custom.lr2folder");
        DateTime folderInfoTimeUtc = new(2026, 6, 5, 5, 0, 0, DateTimeKind.Utc);
        DateTime lr2FolderTimeUtc = new(2026, 6, 5, 6, 0, 0, DateTimeKind.Utc);
        DateTime directoryTimeUtc = new(2026, 6, 5, 7, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(packDirectory);
        File.WriteAllText(folderInfoPath, "#TITLE Pack");
        File.WriteAllText(lr2FolderPath, "#TITLE Custom");
        File.SetLastWriteTimeUtc(folderInfoPath, folderInfoTimeUtc);
        File.SetLastWriteTimeUtc(lr2FolderPath, lr2FolderTimeUtc);
        Directory.SetLastWriteTimeUtc(packDirectory, directoryTimeUtc);

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [tempRoot],
                [
                    new RootFileEnumerationGroup(ChartDirectoryScanBuilder.TextGroupName, ChartDirectoryScanBuilder.TextExtensions),
                    new RootFileEnumerationGroup("lr2folder", [".lr2folder"]),
                    new RootFileEnumerationGroup(RootFileEnumerationService.DirectoriesGroupName, [], includeDirectories: true)
                ]);

            Assert.IsTrue(result.Success);
            RootFileEnumerationEntry folderInfoEntry = result.GetEntry(ChartDirectoryScanBuilder.TextGroupName, folderInfoPath);
            RootFileEnumerationEntry lr2FolderEntry = result.GetEntry("lr2folder", lr2FolderPath);
            RootFileEnumerationEntry directoryEntry = result.GetEntry(RootFileEnumerationService.DirectoriesGroupName, packDirectory);
            Assert.IsNotNull(folderInfoEntry);
            Assert.IsNotNull(lr2FolderEntry);
            Assert.IsNotNull(directoryEntry);
            Assert.AreEqual(ToUnixSeconds(folderInfoTimeUtc), folderInfoEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(lr2FolderTimeUtc), lr2FolderEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(directoryTimeUtc), directoryEntry.LastWriteTimeUnixSeconds);
            Assert.IsTrue(folderInfoEntry.FileSize > 0);
            Assert.IsTrue(lr2FolderEntry.FileSize > 0);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FastDirectoryFileScanner_ReturnsDirectorySurfaceWhenRequested()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_FastScannerDirs_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Pack", "Song");
        string chartPath = Path.Combine(nestedDirectory, "chart.bms");
        DateTime rootWriteTimeUtc = new(2026, 6, 7, 1, 0, 0, DateTimeKind.Utc);
        DateTime nestedWriteTimeUtc = new(2026, 6, 7, 2, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(chartPath, "#TITLE Test");
        Directory.SetLastWriteTimeUtc(tempRoot, rootWriteTimeUtc);
        Directory.SetLastWriteTimeUtc(nestedDirectory, nestedWriteTimeUtc);

        try
        {
            ChartScanExecutionResult result = new FastDirectoryFileScanner().Scan(
                [tempRoot],
                ChartDirectoryScanBuilder.ChartExtensions,
                includeTextSurface: true,
                includeDirectorySurface: true);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.DirectoryQueryHitCount >= 2UL);
            Assert.AreEqual(result.NativeBridgeMs, result.DirectoryQueryMs);
            Assert.IsTrue(result.Result.DirectoryEntriesByPath.TryGetValue(tempRoot, out RootFileEnumerationEntry rootEntry));
            Assert.IsTrue(result.Result.DirectoryEntriesByPath.TryGetValue(nestedDirectory, out RootFileEnumerationEntry nestedEntry));
            Assert.AreEqual(ToUnixSeconds(rootWriteTimeUtc), rootEntry.LastWriteTimeUnixSeconds);
            Assert.AreEqual(ToUnixSeconds(nestedWriteTimeUtc), nestedEntry.LastWriteTimeUnixSeconds);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void FastEnumerator_CollapsesDescendantExecutionRoots()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_RootEnumCollapse_" + Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(tempRoot, "Output", "Table");
        string rootChartPath = Path.Combine(tempRoot, "root.bms");
        string nestedChartPath = Path.Combine(nestedDirectory, "nested.bms");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(rootChartPath, "#PLAYER 1");
        File.WriteAllText(nestedChartPath, "#PLAYER 1");

        try
        {
            RootFileEnumerationResult result = new FastRootFileEnumerator().EnumerateFiles(
                [nestedDirectory, tempRoot, Path.Combine(tempRoot, "Output")],
                [new RootFileEnumerationGroup(ChartDirectoryScanBuilder.ChartGroupName, [".bms"])]);

            Assert.IsTrue(result.Success);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), rootChartPath);
            CollectionAssert.Contains(result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToList(), nestedChartPath);
            Assert.AreEqual(2, result.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).Count);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static int ToUnixSeconds(DateTime timestampUtc)
    {
        DateTime utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        return (int)Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
    }

    private static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    private static void WriteAllText(string path, string contents)
    {
        using var stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }
}
