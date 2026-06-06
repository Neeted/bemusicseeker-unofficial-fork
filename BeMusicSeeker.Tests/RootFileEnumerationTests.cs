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
    public void RootFileEnumerationEntry_NormalizesLocalTimestampToUtcUnixSeconds()
    {
        DateTime utc = new(2026, 6, 5, 1, 2, 3, DateTimeKind.Utc);
        DateTime local = utc.ToLocalTime();

        var entry = new RootFileEnumerationEntry(@"D:\song.bms", local);

        Assert.AreEqual(utc, entry.LastWriteTimeUtc);
        Assert.AreEqual(ToUnixSeconds(utc), entry.LastWriteTimeUnixSeconds);
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
}
