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
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] audioRelativeHashes));
            Assert.IsTrue(result.ImageRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] imageRelativeHashes));
            Assert.AreEqual(1, audioRelativeHashes.Length);
            Assert.AreEqual(1, imageRelativeHashes.Length);
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
    public void BuildFromRoots_AssignsAggregateOwnershipToAllAncestorChartDirectories_AndSelfOwnedToNearestChartDirectory()
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

            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(rootChartDir, out uint[] rootRelativeHashes));
            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(nestedChartDir, out uint[] nestedRelativeHashes));
            CollectionAssert.Contains(rootRelativeHashes, ChartResourceKeyHash.GetLookupHash(Path.Combine("subchart", "sound", "01")));
            CollectionAssert.Contains(nestedRelativeHashes, ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "01")));
            CollectionAssert.Contains(result.SelfOwnedAudioRelativePathHashesByChartDirectory[nestedChartDir], ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "01")));
            Assert.AreEqual(0, result.SelfOwnedAudioRelativePathHashesByChartDirectory[rootChartDir].Length);
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
    public void BuildFromRoots_PathAwareResourcesUseChartRelativeKeys()
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

            Assert.IsTrue(result.AudioRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] audioRelativeHashes));
            Assert.IsTrue(result.ImageRelativePathHashesByChartDirectory.TryGetValue(chartDir, out uint[] imageRelativeHashes));

            uint flatRelativeHash = ChartResourceKeyHash.GetLookupHash("bgm1");
            uint nestedRelativeHash = ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "bgm1"));
            uint imageRelativeHash = ChartResourceKeyHash.GetLookupHash(Path.Combine("clock", "00_001_00"));

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
