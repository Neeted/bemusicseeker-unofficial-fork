using System;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BMSFileSnapshotTests
{
    [TestMethod]
    public void ReadSnapshot_ComputesHashesAndParsesLikeFileApi()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath,
                "#PLAYER 1\r\n"
                + "#GENRE TestGenre\r\n"
                + "#TITLE MainTitle\r\n"
                + "#SUBTITLE SubTitle\r\n"
                + "#ARTIST MainArtist\r\n"
                + "#SUBARTIST SubArtist\r\n"
                + "#PLAYLEVEL 12\r\n"
                + "#DIFFICULTY 4\r\n"
                + "#RANK 2\r\n"
                + "#BANNER banner.png\r\n"
                + "#STAGEFILE stage.png\r\n"
                + "#BACKBMP back.png\r\n"
                + "#WAV01 keysound.wav\r\n"
                + "#BMP01 image.png\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));
            DateTime lastWriteTimeUtc = new DateTime(2026, 5, 2, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            BMSFile expected = BMSFile.CreateBMSFileFromFile(filePath);
            BMSFile actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            Assert.AreEqual(Path.GetFullPath(filePath), snapshot.Path);
            Assert.AreEqual(new FileInfo(filePath).Length, snapshot.Length);
            Assert.AreEqual(lastWriteTimeUtc, snapshot.LastWriteTimeUtc);
            Assert.AreEqual(expected.hash, snapshot.Md5);
            Assert.AreEqual(expected.sha256, snapshot.Sha256);
            AssertBmsMetadataEqual(expected, actual);
        });
    }

    [TestMethod]
    public void CreateBMSFileFromSnapshot_MatchesFileApiForRepresentativeEncodings()
    {
        string[] encodings = { "shift_jis", "gb2312", "big5", "ks_c_5601-1987" };
        foreach (string encodingName in encodings)
        {
            WithTempDirectory(delegate (string tempDirectory)
            {
                string filePath = Path.Combine(tempDirectory, "chart_" + encodingName + ".bms");
                File.WriteAllText(filePath,
                    "#PLAYER 1\n#TITLE Encoding " + encodingName + "\n#ARTIST Artist\n#WAV01 sound.wav\n#00111:01\n",
                    Encoding.GetEncoding(encodingName));

                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
                BMSFile expected = BMSFile.CreateBMSFileFromFile(filePath, encodingName);
                BMSFile actual = BMSFile.CreateBMSFileFromSnapshot(snapshot, encodingName);

                AssertBmsMetadataEqual(expected, actual);
            });
        }
    }

    [TestMethod]
    public void CreateBMSFileFromSnapshot_UsesSnapshotBytesAfterFileChanges()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE Before\r\n", Encoding.GetEncoding("shift_jis"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            string originalMd5 = snapshot.Md5;

            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE After\r\n", Encoding.GetEncoding("shift_jis"));

            BMSFile actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            Assert.AreEqual("Before", actual.Title);
            Assert.AreEqual(originalMd5, actual.hash);
            Assert.AreNotEqual(BMSFile.CreateBMSFileFromFile(filePath).hash, actual.hash);
        });
    }

    private static void AssertBmsMetadataEqual(BMSFile expected, BMSFile actual)
    {
        Assert.AreEqual(expected.path, actual.path);
        Assert.AreEqual(expected.Title, actual.Title);
        Assert.AreEqual(expected.Artist, actual.Artist);
        Assert.AreEqual(expected.genre, actual.genre);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.banner, actual.banner);
        Assert.AreEqual(expected.stagefile, actual.stagefile);
        Assert.AreEqual(expected.backbmp, actual.backbmp);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.hash, actual.hash);
        Assert.AreEqual(expected.sha256, actual.sha256);
        CollectionAssert.AreEquivalent(expected.WAVfiles.ToArray(), actual.WAVfiles.ToArray());
        CollectionAssert.AreEquivalent(expected.BGAfiles.ToArray(), actual.BGAfiles.ToArray());
    }

    private static void WithTempDirectory(Action<string> action)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BMSFileSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            action(tempDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
