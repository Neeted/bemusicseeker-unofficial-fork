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

    private static int ToUnixSeconds(DateTime timestampUtc)
    {
        DateTime utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        return (int)Math.Floor((utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
    }
}
