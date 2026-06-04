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
            var lastWriteTimeUtc = new DateTime(2026, 5, 2, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            var expected = BMSFile.CreateBMSFileFromFile(filePath);
            var actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

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
        string[] encodings = ["shift_jis", "gb2312", "big5", "ks_c_5601-1987"];
        foreach (string encodingName in encodings)
        {
            WithTempDirectory(delegate (string tempDirectory)
            {
                string filePath = Path.Combine(tempDirectory, "chart_" + encodingName + ".bms");
                File.WriteAllText(filePath,
                    "#PLAYER 1\n#TITLE Encoding " + encodingName + "\n#ARTIST Artist\n#WAV01 sound.wav\n#00111:01\n",
                    Encoding.GetEncoding(encodingName));

                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
                var expected = BMSFile.CreateBMSFileFromFile(filePath, encodingName);
                var actual = BMSFile.CreateBMSFileFromSnapshot(snapshot, encodingName);

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

            var actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            Assert.AreEqual("Before", actual.Title);
            Assert.AreEqual(originalMd5, actual.hash);
            Assert.AreNotEqual(BMSFile.CreateBMSFileFromFile(filePath).hash, actual.hash);
        });
    }

    [TestMethod]
    public void CreateBMSFileFromSnapshot_DetectsModeChannelsLikeFileApi()
    {
        (string FileName, string ChannelLine, int ExpectedMode)[] cases =
        [
            ("fivekey.bms", "#00111:01", 5),
            ("default-seven.bms", "#00111:000000", 7),
            ("legacy-seven.bms", "#00116:01", 7),
            ("pms-forced.pms", "#00111:01", 9),
            ("tenkey.bms", "#00121:01", 10),
            ("fourteenkey.bms", "#00128:01", 14),
            ("fullwidth-leading-space.bms", "\u3000#00111:01", 5),
            ("whitespace-separator-is-ignored.bms", "#00111 01", 7)
        ];
        foreach ((string fileName, string channelLine, int expectedMode) in cases)
        {
            WithTempDirectory(delegate (string tempDirectory)
            {
                string filePath = Path.Combine(tempDirectory, fileName);
                File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE Mode\r\n" + channelLine + "\r\n", Encoding.GetEncoding("shift_jis"));

                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
                var expected = BMSFile.CreateBMSFileFromFile(filePath);
                var actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

                Assert.AreEqual(expectedMode, actual.mode, fileName);
                AssertBmsMetadataEqual(expected, actual);
            });
        }
    }

    [TestMethod]
    public void CreateBMSFileFromSnapshot_DirectDirectiveScannerMatchesFileApi()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "directives.bms");
            File.WriteAllText(filePath,
                "\t#GENRE Genre\r\n"
                + " #TITLE Title\r\n"
                + "\u3000#SUBTITLE SubTitle\r\n"
                + "#ARTIST Artist\r\n"
                + "#SUBARTIST SubArtist\r\n"
                + "#PLAYLEVEL 12\r\n"
                + "#DIFFICULTY 4\r\n"
                + "#RANK 2\r\n"
                + "#BANNER banner.bmp\r\n"
                + "#STAGEFILE stage.jpg\r\n"
                + "#BACKBMP back.jpeg\r\n"
                + "#WAV01 sound.ogg\r\n"
                + "#WAV02 audio/hit.mp3\r\n"
                + "#WAV03\r\n"
                + "#BMP01 image.jpg\r\n"
                + "#BMP02 movie.mp4\r\n"
                + "#BMP03 C:\\absolute.png\r\n"
                + "#UNKNOWN ignored\r\n"
                + "#00111 01\r\n"
                + "#00112:00\r\n"
                + "#00113:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            var expected = BMSFile.CreateBMSFileFromFile(filePath);
            var actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            AssertBmsMetadataEqual(expected, actual);
            CollectionAssert.Contains(actual.WAVfiles.ToArray(), "sound.wav");
            CollectionAssert.Contains(actual.WAVfiles.ToArray(), Path.Combine("audio", "hit.wav"));
            CollectionAssert.Contains(actual.BGAfiles.ToArray(), "image.png");
            CollectionAssert.Contains(actual.BGAfiles.ToArray(), "movie.mp4");
            Assert.IsFalse(actual.WAVfiles.Any(string.IsNullOrWhiteSpace));
            Assert.IsFalse(actual.BGAfiles.Any(value => value.Contains(":")));
            Assert.AreEqual(5, actual.mode);
        });
    }

    [TestMethod]
    public void CreateBMSFileFromSnapshot_PreservesRawResourceReferences()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "resources.bms");
            File.WriteAllText(filePath,
                "#PLAYER 1\r\n"
                + "#TITLE Resources\r\n"
                + "#WAV01 .\\sound\\kick.wav\r\n"
                + "#BMP01 visual\\bg.final.png\r\n"
                + "#BMP02 movie\\op.mpg\r\n"
                + "#WAV02 ..\\shared\\hit.wav\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            var actual = BMSFile.CreateBMSFileFromSnapshot(snapshot);

            Assert.AreEqual(3, actual.ResourceReferences.Count);
            AssertResourceReference(actual, ChartResourceKind.Audio, @".\sound\kick.wav", Path.Combine("sound", "kick.wav"));
            AssertResourceReference(actual, ChartResourceKind.Image, @"visual\bg.final.png", Path.Combine("visual", "bg.final.png"));
            AssertResourceReference(actual, ChartResourceKind.Movie, @"movie\op.mpg", Path.Combine("movie", "op.mpg"));
            Assert.AreEqual(1, actual.UnsupportedResourceReferences.Count);
            Assert.AreEqual(@"..\shared\hit.wav", actual.UnsupportedResourceReferences[0].RawPath);
        });
    }

    [TestMethod]
    public void DetectEncodingOfBMSFile_SnapshotMatchesPathApiForRepresentativeEncodings()
    {
        (string CaseName, byte[] Bytes)[] cases =
        [
            ("ascii", Encoding.ASCII.GetBytes("#TITLE ASCII\r\n")),
            ("shift_jis_japanese", Encoding.GetEncoding("shift_jis").GetBytes("#TITLE 日本語\r\n")),
            ("korean_definite", Encoding.GetEncoding("ks_c_5601-1987").GetBytes("#TITLE \uacaf\r\n")),
            ("korean_ambiguous", Encoding.GetEncoding("ks_c_5601-1987").GetBytes("#TITLE \uac00\ub098\r\n")),
            ("utf8", Encoding.UTF8.GetBytes("#TITLE \u3012\u2605\r\n")),
            ("unknown", new byte[] { 0xFF, 0xFF, 0xFF })
        ];
        foreach ((string caseName, byte[] bytes) in cases)
        {
            WithTempDirectory(delegate (string tempDirectory)
            {
                string filePath = Path.Combine(tempDirectory, "chart_" + caseName + ".bms");
                File.WriteAllBytes(filePath, bytes);

                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);

                Assert.AreEqual(BMSFile.DetectEncodingOfBMSFile(filePath), BMSFile.DetectEncodingOfBMSFile(snapshot), caseName);
            });
        }
    }

    [TestMethod]
    public void DetectEncodingOfBMSFile_AsciiUsesSharedFastPath()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "ascii.bms");
            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE ASCII\r\n#00111:01\r\n", Encoding.ASCII);

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            BMSFile.BmsEncodingDetectionResult result = BMSFile.DetectEncodingOfBMSFileDetailed(snapshot);

            Assert.AreEqual("shift_jis", BMSFile.DetectEncodingOfBMSFile(filePath));
            Assert.AreEqual("shift_jis", BMSFile.DetectEncodingOfBMSFile(snapshot));
            Assert.AreEqual("shift_jis", result.EncodingName);
            Assert.AreEqual(BMSFile.EncodingDetectionOutcome.ShiftJis, result.Outcome);
            Assert.IsTrue(result.FastAscii);
        });
    }

    [TestMethod]
    public void DetectEncodingOfBMSFile_UsesSnapshotBytesAfterFileChanges()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#TITLE \uac00\ub098\ub2e4\r\n", Encoding.GetEncoding("ks_c_5601-1987"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            string snapshotEncoding = BMSFile.DetectEncodingOfBMSFile(snapshot);

            File.WriteAllText(filePath, "#TITLE ASCII\r\n", Encoding.GetEncoding("shift_jis"));

            Assert.AreEqual(snapshotEncoding, BMSFile.DetectEncodingOfBMSFile(snapshot));
            Assert.AreNotEqual(BMSFile.DetectEncodingOfBMSFile(filePath), BMSFile.DetectEncodingOfBMSFile(snapshot));
        });
    }

    [TestMethod]
    public void ReloadBMSFileWithEncoding_SnapshotUsesSnapshotBytesAfterFileChanges()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath,
                "#PLAYER 1\r\n#TITLE Before\r\n#SUBTITLE SubBefore\r\n#ARTIST ArtistBefore\r\n#SUBARTIST SubArtistBefore\r\n#GENRE GenreBefore\r\n",
                Encoding.GetEncoding("shift_jis"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            var file = BMSFile.CreateBMSFileFromFile(filePath);

            File.WriteAllText(filePath,
                "#PLAYER 1\r\n#TITLE After\r\n#SUBTITLE SubAfter\r\n#ARTIST ArtistAfter\r\n#SUBARTIST SubArtistAfter\r\n#GENRE GenreAfter\r\n",
                Encoding.GetEncoding("shift_jis"));

            BMSFile.ReloadBMSFileWithEncoding(file, snapshot, "shift_jis");

            Assert.AreEqual("Before", file.title);
            Assert.AreEqual("SubBefore", file.subtitle);
            Assert.AreEqual("Before SubBefore", file.Title);
            Assert.AreEqual("ArtistBefore", file.artist);
            Assert.AreEqual("SubArtistBefore", file.subartist);
            Assert.AreEqual("ArtistBefore SubArtistBefore", file.Artist);
            Assert.AreEqual("GenreBefore", file.genre);
        });
    }

    [TestMethod]
    public void ReloadBMSFileWithEncoding_PathAppliesRawTitleAndArtistMetadata()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "korean.bms");
            const string title = "\uacaf";
            const string subtitle = "[Pattern]";
            const string artist = "\uac00\ub098";
            const string subartist = "obj:Tester";
            const string genre = "KoreanGenre";
            File.WriteAllText(filePath,
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#SUBTITLE " + subtitle + "\r\n#ARTIST " + artist + "\r\n#SUBARTIST " + subartist + "\r\n#GENRE " + genre + "\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));
            var file = BMSFile.CreateBMSFileFromFile(filePath);
            Assert.AreNotEqual(title + " " + subtitle, file.Title);

            BMSFile.ReloadBMSFileWithEncoding(file, "ks_c_5601-1987");

            Assert.AreEqual(title, file.title);
            Assert.AreEqual(subtitle, file.subtitle);
            Assert.AreEqual(title + " " + subtitle, file.Title);
            Assert.AreEqual(artist, file.artist);
            Assert.AreEqual(subartist, file.subartist);
            Assert.AreEqual(artist + " " + subartist, file.Artist);
            Assert.AreEqual(genre, file.genre);
        });
    }

    [TestMethod]
    public void ReloadBMSFileWithEncoding_NormalizesShiftJisQuestionAndClearsDates()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE ASCII\r\n#ARTIST Artist\r\n", Encoding.GetEncoding("shift_jis"));
            var file = BMSFile.CreateBMSFileFromFile(filePath);
            file.date = 123;
            file.adddate = 456;

            BMSFile.ReloadBMSFileWithEncoding(file, "shift_jis?");

            Assert.AreEqual("ASCII", file.title);
            Assert.AreEqual("Artist", file.artist);
            Assert.IsNull(file.date);
            Assert.IsNull(file.adddate);
        });
    }

    [TestMethod]
    public void ReloadBMSMetadataWithEncodingDetection_UsesDetectedSnapshotText()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "korean.bms");
            const string title = "\uacaf";
            const string subtitle = "[Pattern]";
            const string artist = "\uac00\ub098";
            const string subartist = "obj:Tester";
            const string genre = "KoreanGenre";
            File.WriteAllText(filePath,
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#SUBTITLE " + subtitle + "\r\n#ARTIST " + artist + "\r\n#SUBARTIST " + subartist + "\r\n#GENRE " + genre + "\r\n#WAV01 sound.wav\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            var file = BMSFile.CreateBMSFileFromSnapshot(snapshot);
            Assert.AreNotEqual(title, file.Title);

            BMSFile.BmsEncodingDetectionResult detectionResult = BMSFile.DetectEncodingOfBMSFileDetailed(snapshot);
            BMSFile.ReloadBMSMetadataWithEncodingDetection(file, snapshot, detectionResult);

            Assert.AreEqual("ks_c_5601-1987", detectionResult.EncodingName);
            Assert.AreEqual(title, file.title);
            Assert.AreEqual(subtitle, file.subtitle);
            Assert.AreEqual(title + " " + subtitle, file.Title);
            Assert.AreEqual(artist, file.artist);
            Assert.AreEqual(subartist, file.subartist);
            Assert.AreEqual(artist + " " + subartist, file.Artist);
            Assert.AreEqual(genre, file.genre);
            CollectionAssert.Contains(file.WAVfiles.ToArray(), "sound.wav");
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
        CollectionAssert.AreEqual(
            (expected.ResourceReferences ?? []).Select(ToComparableResourceReference).ToArray(),
            (actual.ResourceReferences ?? []).Select(ToComparableResourceReference).ToArray());
        CollectionAssert.AreEqual(
            (expected.UnsupportedResourceReferences ?? []).Select(ToComparableUnsupportedResourceReference).ToArray(),
            (actual.UnsupportedResourceReferences ?? []).Select(ToComparableUnsupportedResourceReference).ToArray());
    }

    private static void AssertResourceReference(BMSFile file, ChartResourceKind kind, string rawPath, string normalizedPath)
    {
        Assert.IsTrue(
            file.ResourceReferences.Any(reference =>
                reference.Kind == kind
                && reference.RawPath == rawPath
                && reference.NormalizedPath == normalizedPath),
            kind + "|" + rawPath + "|" + normalizedPath);
    }

    private static string ToComparableResourceReference(ChartResourceReference reference)
    {
        return reference.Kind + "|" + reference.RawPath + "|" + reference.NormalizedPath;
    }

    private static string ToComparableUnsupportedResourceReference(UnsupportedChartResourceReference reference)
    {
        return reference.Kind + "|" + reference.RawPath + "|" + reference.Reason;
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
