using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartDirectoryScanBuilderTests
{
    [TestMethod]
    public void BuildFromRoots_AggregatesResourcesIntoOwningChartDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirScan_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        string nestedResourceDir = Path.Combine(chartDir, "sound");
        string imageDir = Path.Combine(chartDir, "image");
        Directory.CreateDirectory(nestedResourceDir);
        Directory.CreateDirectory(imageDir);
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(nestedResourceDir, "00.wav"), "audio");
        File.WriteAllText(Path.Combine(imageDir, "bg.jpg"), "image");
        try
        {
            BmsScanResult result = ChartDirectoryScanBuilder.BuildFromRoots(new[] { tempRoot });

            CollectionAssert.Contains(result.ChartDirectories.ToList(), chartDir);
            Assert.AreEqual(1, result.ChartFilePaths.Count);
            Assert.IsTrue(result.AudioBaseNameHashesByChartDirectory.TryGetValue(chartDir, out uint[] audioBaseHashes));
            Assert.IsTrue(result.ImageBaseNameHashesByChartDirectory.TryGetValue(chartDir, out uint[] imageBaseHashes));
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] audioRelativeHashes));
            Assert.AreEqual(1, audioBaseHashes.Length);
            Assert.AreEqual(1, imageBaseHashes.Length);
            Assert.AreEqual(1, audioRelativeHashes.Length);
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
    public void BuildFromRoots_AssignsResourceToNearestAncestorChartDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirNearest_" + Guid.NewGuid().ToString("N"));
        string rootChartDir = Path.Combine(tempRoot, "rootchart");
        string nestedChartDir = Path.Combine(rootChartDir, "subchart");
        string nestedSoundDir = Path.Combine(nestedChartDir, "sound");
        Directory.CreateDirectory(nestedSoundDir);
        File.WriteAllText(Path.Combine(rootChartDir, "root.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(nestedChartDir, "sub.bmson"), "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Sub\",\"artist\":\"Artist\"},\"lines\":[{\"y\":0}]}");
        File.WriteAllText(Path.Combine(nestedSoundDir, "01.ogg"), "audio");
        try
        {
            BmsScanResult result = ChartDirectoryScanBuilder.BuildFromRoots(new[] { tempRoot });

            Assert.IsTrue(result.AudioBaseNameHashesByChartDirectory.TryGetValue(nestedChartDir, out uint[] nestedHashes));
            Assert.AreEqual(1, nestedHashes.Length);
            Assert.IsTrue(result.AudioBaseNameHashesByChartDirectory.TryGetValue(rootChartDir, out uint[] rootHashes));
            Assert.AreEqual(0, rootHashes.Length);
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
    public void BuildFromRoots_PathAwareResourcesKeepDistinctRelativeHashesWhileCollapsingBaseNames()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartDirRelative_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        string nestedAudioDir = Path.Combine(chartDir, "sound");
        string nestedImageDir = Path.Combine(chartDir, "clock");
        Directory.CreateDirectory(nestedAudioDir);
        Directory.CreateDirectory(nestedImageDir);
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(chartDir, "bgm1.wav"), "flat");
        File.WriteAllText(Path.Combine(nestedAudioDir, "bgm1.wav"), "nested");
        File.WriteAllText(Path.Combine(nestedImageDir, "00_001_00.bmp"), "image");
        try
        {
            BmsScanResult result = ChartDirectoryScanBuilder.BuildFromRoots(new[] { tempRoot });

            Assert.IsTrue(result.AudioBaseNameHashesByChartDirectory.TryGetValue(chartDir, out uint[] audioBaseHashes));
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] audioRelativeHashes));
            Assert.IsTrue(result.ImageRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] imageRelativeHashes));

            uint baseHash = BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav");
            uint flatRelativeHash = BMSDirectoryFileNameHash.GetLookupHash("bgm1.wav");
            uint nestedRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(Path.Combine("sound", "bgm1.wav"));
            uint imageRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(Path.Combine("clock", "00_001_00.png"));

            CollectionAssert.AreEquivalent(new[] { baseHash }, audioBaseHashes);
            CollectionAssert.AreEquivalent(new[] { flatRelativeHash, nestedRelativeHash }, audioRelativeHashes);
            CollectionAssert.Contains(imageRelativeHashes.ToList(), imageRelativeHash);
            Assert.AreNotEqual(flatRelativeHash, nestedRelativeHash);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
