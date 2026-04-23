using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsScanResultComparerTests
{
    [TestMethod]
    public void Compare_ReturnsMatch_WhenRelativeHashMapsAreEquivalent()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ScanComparer_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        string soundDir = Path.Combine(chartDir, "sound");
        string imageDir = Path.Combine(chartDir, "image");
        Directory.CreateDirectory(soundDir);
        Directory.CreateDirectory(imageDir);
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(soundDir, "bgm1.wav"), "audio");
        File.WriteAllText(Path.Combine(imageDir, "logo.bmp"), "image");

        try
        {
            BmsScanResult fast = ChartDirectoryScanBuilder.BuildFromRoots(new[] { tempRoot });
            BmsScanResult compatibilityClone = CloneScanResult(fast);

            BmsScanDiffReport report = BmsScanResultComparer.Compare(compatibilityClone, fast);

            Assert.IsTrue(report.IsMatch);
            Assert.AreEqual(0, report.ChartPathDiffCount);
            Assert.AreEqual(0, report.ChartDirectoryDiffCount);
            Assert.AreEqual(0, report.CategoryHashDiffCount);
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
    public void Compare_ReportsRelativeHashDifferences()
    {
        string chartDir = Path.Combine("C:\\Library", "song");
        string chartPath = Path.Combine(chartDir, "chart.bms");
        BmsScanResult left = new BmsScanResult
        {
            ChartFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartPath },
            ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDir },
            AllResourceBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") } }
            },
            AudioBaseNameHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav") } }
            },
            AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { BMSDirectoryFileNameHash.GetLookupHash(Path.Combine("sound", "bgm1.wav")) } }
            }
        };
        BmsScanResult right = CloneScanResult(left);
        right.AudioRelativePathHashesByChartDirectory[chartDir] = new[] { BMSDirectoryFileNameHash.GetLookupHash("bgm1.wav") };

        BmsScanDiffReport report = BmsScanResultComparer.Compare(left, right);

        Assert.IsFalse(report.IsMatch);
        Assert.IsTrue(report.CategoryHashDiffCount > 0);
        Assert.IsTrue(report.Samples.Exists(sample => sample.StartsWith("audio_rel ", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void EverythingAndFastScanner_ProduceEquivalentResults_ForPathAwareFixture_WhenOptInEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_EVERYTHING_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_EVERYTHING_INTEGRATION=1 to run Everything integration parity test.");
        }
        if (!EverythingNative.EnsureBridgeAvailable(out string reason))
        {
            Assert.Inconclusive("Everything bridge unavailable: " + (reason ?? "unknown"));
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_EverythingParity_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        Directory.CreateDirectory(Path.Combine(chartDir, "sound"));
        Directory.CreateDirectory(Path.Combine(chartDir, "clock"));
        Directory.CreateDirectory(Path.Combine(chartDir, "image"));
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(chartDir, "sound", "bgm1.wav"), "audio");
        File.WriteAllText(Path.Combine(chartDir, "clock", "00_001_00.bmp"), "image");
        File.WriteAllText(Path.Combine(chartDir, "image", "logo.bmp"), "image");

        try
        {
            BmsScanExecutionResult everything = new EverythingFileScanner().Scan(new[] { tempRoot }, Array.Empty<string>(), verboseLog: false);
            BmsScanExecutionResult fast = new FastDirectoryFileScanner().Scan(new[] { tempRoot }, Array.Empty<string>(), verboseLog: false);
            if (!everything.Success)
            {
                Assert.Inconclusive("Everything scan failed: " + (everything.ErrorReason ?? "unknown"));
            }
            if (!fast.Success)
            {
                Assert.Fail("Fast scan failed: " + (fast.ErrorReason ?? "unknown"));
            }

            Assert.IsTrue(everything.NativeBridgeUsed);
            Assert.AreEqual("everything_bridge_fixed_scan", everything.NativeBridgeReason);
            Assert.AreEqual(0L, everything.BuildResultMs);
            Assert.AreEqual(0L, everything.HashBuildMs);

            BmsScanDiffReport report = BmsScanResultComparer.Compare(everything.Result, fast.Result);
            Assert.IsTrue(report.IsMatch, string.Join(Environment.NewLine, report.Samples));
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
    public void EverythingRootFileEnumerator_ReturnsAllFilesGroup_WhenOptInEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_EVERYTHING_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_EVERYTHING_INTEGRATION=1 to run Everything integration parity test.");
        }
        if (!EverythingNative.EnsureBridgeAvailable(out string reason))
        {
            Assert.Inconclusive("Everything bridge unavailable: " + (reason ?? "unknown"));
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_EverythingGrouped_" + Guid.NewGuid().ToString("N"));
        string chartDir = Path.Combine(tempRoot, "song");
        Directory.CreateDirectory(Path.Combine(chartDir, "sound"));
        Directory.CreateDirectory(Path.Combine(chartDir, "image"));
        File.WriteAllText(Path.Combine(chartDir, "chart.bms"), "#PLAYER 1");
        File.WriteAllText(Path.Combine(chartDir, "sound", "bgm1.wav"), "audio");
        File.WriteAllText(Path.Combine(chartDir, "image", "logo.bmp"), "image");
        File.WriteAllText(Path.Combine(chartDir, "readme.txt"), "misc");

        try
        {
            RootFileEnumerationResult enumerationResult = new EverythingRootFileEnumerator().EnumerateFiles(
                new[] { tempRoot },
                ChartDirectoryScanBuilder.CreateDefaultEnumerationGroups(includeAllFiles: true),
                verboseLog: false);

            if (!enumerationResult.Success)
            {
                Assert.Inconclusive("Everything grouped enumeration failed: " + (enumerationResult.ErrorReason ?? "unknown"));
            }
            Assert.AreEqual("everything_bridge", enumerationResult.BackendName);
            Assert.AreEqual(4, enumerationResult.TotalFileCount);
            CollectionAssert.Contains(enumerationResult.GetPaths(RootFileEnumerationService.AllFilesGroupName).ToArray(), Path.Combine(chartDir, "readme.txt"));
            CollectionAssert.Contains(enumerationResult.GetPaths(ChartDirectoryScanBuilder.ChartGroupName).ToArray(), Path.Combine(chartDir, "chart.bms"));
            CollectionAssert.Contains(enumerationResult.GetPaths(ChartDirectoryScanBuilder.AudioGroupName).ToArray(), Path.Combine(chartDir, "sound", "bgm1.wav"));
            CollectionAssert.Contains(enumerationResult.GetPaths(ChartDirectoryScanBuilder.ImageGroupName).ToArray(), Path.Combine(chartDir, "image", "logo.bmp"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static BmsScanResult CloneScanResult(BmsScanResult source)
    {
        return new BmsScanResult
        {
            ChartFilePaths = new HashSet<string>(source.ChartFilePaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            ChartDirectories = new HashSet<string>(source.ChartDirectories ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            AllResourceBaseNameHashesByChartDirectory = CloneMap(source.AllResourceBaseNameHashesByChartDirectory),
            AudioBaseNameHashesByChartDirectory = CloneMap(source.AudioBaseNameHashesByChartDirectory),
            ImageBaseNameHashesByChartDirectory = CloneMap(source.ImageBaseNameHashesByChartDirectory),
            MovieBaseNameHashesByChartDirectory = CloneMap(source.MovieBaseNameHashesByChartDirectory),
            AudioRelativePathHashesByChartDirectory = CloneMap(source.AudioRelativePathHashesByChartDirectory),
            ImageRelativePathHashesByChartDirectory = CloneMap(source.ImageRelativePathHashesByChartDirectory),
            MovieRelativePathHashesByChartDirectory = CloneMap(source.MovieRelativePathHashesByChartDirectory),
            SelfOwnedAllResourceBaseNameHashesByChartDirectory = CloneMap(source.SelfOwnedAllResourceBaseNameHashesByChartDirectory),
            SelfOwnedAudioBaseNameHashesByChartDirectory = CloneMap(source.SelfOwnedAudioBaseNameHashesByChartDirectory),
            SelfOwnedImageBaseNameHashesByChartDirectory = CloneMap(source.SelfOwnedImageBaseNameHashesByChartDirectory),
            SelfOwnedMovieBaseNameHashesByChartDirectory = CloneMap(source.SelfOwnedMovieBaseNameHashesByChartDirectory),
            SelfOwnedAudioRelativePathHashesByChartDirectory = CloneMap(source.SelfOwnedAudioRelativePathHashesByChartDirectory),
            SelfOwnedImageRelativePathHashesByChartDirectory = CloneMap(source.SelfOwnedImageRelativePathHashesByChartDirectory),
            SelfOwnedMovieRelativePathHashesByChartDirectory = CloneMap(source.SelfOwnedMovieRelativePathHashesByChartDirectory)
        };
    }

    private static Dictionary<string, uint[]> CloneMap(Dictionary<string, uint[]> source)
    {
        return (source ?? new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value?.ToArray() ?? Array.Empty<uint>(),
                StringComparer.OrdinalIgnoreCase);
    }
}
