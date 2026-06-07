using System;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongRowEnricherTests
{
    [TestMethod]
    public void EnrichParsedSong_AppliesSnapshotMetadataAndPreservesUserColumns()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SongRowEnricher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#ARTIST artist\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var timestamp = new DateTime(2026, 6, 1, 2, 3, 4, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(chartPath, timestamp);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot);
            var existing = new TestableBmsFile
            {
                adddate = 12345,
                tag = "keep-tag"
            };
            existing.SetFavorite(7);

            Lr2SongRowEnricher.EnrichParsedSong(parsed, snapshot, textFlag: 1, existing);

            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp), parsed.date);
            Assert.AreEqual(1, parsed.txt);
            Assert.AreEqual(7, parsed.favorite);
            Assert.AreEqual(12345, parsed.adddate);
            Assert.AreEqual("keep-tag", parsed.tag);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tempDirectoryPath), parsed.folder);
            Assert.IsFalse(string.IsNullOrWhiteSpace(parsed.parent));
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EnrichGeneratedSong_DoesNotThrowForCp932UnsupportedPath()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new TestableBmsFile
        {
            path = @"C:\BMS\😀\chart.bms"
        };

        Lr2SongRowEnricher.EnrichGeneratedSong(file);

        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.IsTrue(string.IsNullOrWhiteSpace(file.folder));
        Assert.IsTrue(string.IsNullOrWhiteSpace(file.parent));
    }

    [TestMethod]
    public void EnrichGeneratedSong_DefaultsExLevelToZero()
    {
        var file = new TestableBmsFile
        {
            path = @"C:\BMS\Pack\chart.bms"
        };

        Lr2SongRowEnricher.EnrichGeneratedSong(file);

        Assert.AreEqual(0, file.exlevel);
    }

    [TestMethod]
    public void EnrichFromChartInfo_AppliesDetailedColumnsWithoutOverwritingLightweightMetadata()
    {
        var file = new TestableBmsFile
        {
            level = 3,
            difficulty = -1
        };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetJudgeForTest(1);
        file.SetModeForTest(5);
        var chartInfo = new LR2SongDBExtended.chart_info
        {
            md5 = file.hash,
            level = 12,
            difficulty = 4,
            maxbpm = 180.9,
            minbpm = 90.1,
            mode = 14,
            judge = 100,
            bga = 1,
            exlevel = 120,
            feature = 4 | 8,
            notes = 1234
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(file, chartInfo);

        Assert.AreEqual(3, file.level);
        Assert.AreEqual(-1, file.difficulty);
        Assert.AreEqual(180, file.maxbpm);
        Assert.AreEqual(90, file.minbpm);
        Assert.AreEqual(5, file.mode);
        Assert.AreEqual(1, file.judge);
        Assert.AreEqual(1, file.bga);
        Assert.AreEqual(120, file.exlevel);
        Assert.AreEqual(1, file.longnote);
        Assert.AreEqual(1, file.random);
        Assert.AreEqual(1234, file.karinotes);
    }

    [DataTestMethod]
    [DataRow("#00118:00\r\n", 7)]
    [DataRow("#00121:00\r\n", 10)]
    [DataRow("#00129:00\r\n", 14)]
    [DataRow("#00128 00\r\n", 14)]
    public void CreateBMSFileFromSnapshot_InfersLr2ModeFromChannelPresence(string chartText, int expectedMode)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SongRowEnricher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, chartText, Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            Assert.AreEqual(expectedMode, parsed.mode);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void CreateBMSFileFromSnapshot_CustomFolderDirectiveResetsLr2Judge()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SongRowEnricher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#RANK 3\r\n#CUSTOMFOLDER\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            BMSFile parsed = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            Assert.AreEqual(2, parsed.judge);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    [TestMethod]
    public void EnrichFromChartInfo_IgnoresMismatchedMd5()
    {
        var file = new TestableBmsFile();
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var chartInfo = new LR2SongDBExtended.chart_info
        {
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            level = 12,
            notes = 1234
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(file, chartInfo);

        Assert.IsNull(file.level);
        Assert.IsNull(file.karinotes);
    }

    [TestMethod]
    public void EnrichFromChartInfo_DefaultsNullExLevelToZero()
    {
        var file = new TestableBmsFile();
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var chartInfo = new LR2SongDBExtended.chart_info
        {
            md5 = file.hash,
            exlevel = null
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(file, chartInfo);

        Assert.AreEqual(0, file.exlevel);
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetFavorite(int? value)
        {
            favorite = value;
        }

        public void SetJudgeForTest(int? value)
        {
            judge = value;
        }

        public void SetModeForTest(int? value)
        {
            mode = value;
        }
    }
}
