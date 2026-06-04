using System;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
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

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetFavorite(int? value)
        {
            favorite = value;
        }
    }
}
