using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsonSongParserTests
{
    [TestMethod]
    public void Parse_ExtractsHashesAndMetadata()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "chart.bmson");
        try
        {
            File.WriteAllText(filePath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{"
                + "\"title\":\"Main\","
                + "\"subtitle\":\"Sub\","
                + "\"chart_name\":\"Another\","
                + "\"artist\":\"Artist\","
                + "\"genre\":\"Genre\","
                + "\"level\":12,"
                + "\"mode_hint\":\"beat-14k\","
                + "\"banner_image\":\"banner.png\","
                + "\"back_image\":\"back.png\","
                + "\"eyecatch_image\":\"stage.png\","
                + "\"preview_music\":\"preview.ogg\","
                + "\"subartists\":[\"Sub1\",\"Sub2\"]"
                + "},"
                + "\"sound_channels\":[],"
                + "\"bpm_events\":[],"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            var parsed = BmsonSongParser.Parse(filePath);

            Assert.AreEqual(Path.GetFullPath(filePath), parsed.path);
            Assert.AreEqual("Main", parsed.title);
            Assert.AreEqual("Sub [Another]", parsed.subtitle);
            Assert.AreEqual("Artist Sub1,Sub2", parsed.artist);
            Assert.AreEqual("Genre", parsed.genre);
            Assert.AreEqual(12d, parsed.level);
            Assert.AreEqual("beat-14k", parsed.mode_hint);
            Assert.AreEqual("banner.png", parsed.banner);
            Assert.AreEqual("back.png", parsed.backbmp);
            Assert.AreEqual("stage.png", parsed.stagefile);
            Assert.AreEqual("preview.wav", parsed.preview_music);
            Assert.AreEqual(32, parsed.md5.Length);
            Assert.AreEqual(64, parsed.sha256.Length);
            Assert.AreEqual(14, BmsonSongParser.ResolvePlaylistMode(parsed.mode_hint));
            Assert.AreEqual("Main Sub [Another]", BmsonSongParser.ComposeDisplayTitle(parsed));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Parse_ExtractsPendingHealthComponentFiles()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "chart.bmson");
        try
        {
            File.WriteAllText(filePath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{"
                + "\"title\":\"Main\","
                + "\"artist\":\"Artist\","
                + "\"preview_music\":\"preview.ogg\""
                + "},"
                + "\"sound_channels\":[{\"name\":\"keysound.wav\",\"notes\":[]}],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"movie.mp4\"},{\"id\":2,\"name\":\"image.png\"}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            var parsed = BmsonSongParser.Parse(filePath);
            PendingChartEntry pending = PendingChartEntry.CreateFromBmsonSong(parsed);
            pending.SetHealthStatus(forceUpdate: false, memClear: false);

            CollectionAssert.AreEquivalent(new[] { "keysound.wav", "preview.wav" }, pending.WAVfiles.ToArray());
            CollectionAssert.AreEquivalent(new[] { "image.png", "movie.mp4" }, pending.BGAfiles.ToArray());
            Assert.AreEqual(string.Empty, pending.tag);
            Assert.AreEqual(2, pending.maintenanceInfo.wav_files_defined);
            Assert.AreEqual(0, pending.maintenanceInfo.wav_files_existing);
            Assert.AreEqual(1, pending.maintenanceInfo.bga_files_defined);
            Assert.AreEqual(1, pending.maintenanceInfo.movie_files_defined);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Parse_UsesStructuredResourceLocationsOnly()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "chart.bmson");
        try
        {
            File.WriteAllText(filePath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{"
                + "\"title\":\"Main\","
                + "\"artist\":\"Artist\","
                + "\"preview_music\":\"/preview.ogg\","
                + "\"banner_image\":\"banner.jpg\","
                + "\"back_image\":\"bg\\\\back.bmp\","
                + "\"eyecatch_image\":\"stage.png\""
                + "},"
                + "\"metadata\":{\"name\":\"ignored.ogg\"},"
                + "\"sound_channels\":[{\"name\":\"sounds/keysound.ogg\",\"notes\":[]}],"
                + "\"key_channels\":[{\"name\":\"keys/hidden.mp3\",\"notes\":[]}],"
                + "\"mine_channels\":[{\"name\":\"mines/mine.wav\",\"notes\":[]}],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"images\\\\layer.jpg\"},{\"id\":2,\"name\":\"movies\\\\clip.mp4\"}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            var parsed = BmsonSongParser.Parse(filePath);

            CollectionAssert.AreEquivalent(
                new[] { "preview.wav", Path.Combine("sounds", "keysound.wav"), Path.Combine("keys", "hidden.wav"), Path.Combine("mines", "mine.wav") },
                parsed.wav_files.ToArray());
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine("images", "layer.png"), Path.Combine("movies", "clip.mp4") },
                parsed.bga_files.ToArray());
            Assert.AreEqual("banner.png", parsed.banner);
            Assert.AreEqual(Path.Combine("bg", "back.png"), parsed.backbmp);
            Assert.AreEqual("stage.png", parsed.stagefile);
            Assert.IsFalse(parsed.wav_files.Contains("ignored.ogg"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ParseSnapshot_MatchesParse()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "chart.bmson");
        try
        {
            File.WriteAllText(filePath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{"
                + "\"title\":\"Main\","
                + "\"subtitle\":\"Sub\","
                + "\"chart_name\":\"Another\","
                + "\"artist\":\"Artist\","
                + "\"genre\":\"Genre\","
                + "\"level\":12,"
                + "\"mode_hint\":\"beat-14k\","
                + "\"banner_image\":\"banner.png\","
                + "\"back_image\":\"back.png\","
                + "\"eyecatch_image\":\"stage.png\","
                + "\"preview_music\":\"preview.ogg\","
                + "\"subartists\":[\"Sub1\",\"Sub2\"]"
                + "},"
                + "\"sound_channels\":[{\"name\":\"keysound.wav\",\"notes\":[]}],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"image.png\"}]},"
                + "\"bpm_events\":[],"
                + "\"lines\":[{\"y\":0}]"
                + "}");
            DateTime lastWriteTimeUtc = new DateTime(2026, 5, 2, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);

            var expected = BmsonSongParser.Parse(filePath);
            var actual = BmsonSongParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(filePath));

            AssertBmsonEqual(expected, actual);
            Assert.IsTrue(expected.HasFreshResourceReferences);
            Assert.IsTrue(actual.HasFreshResourceReferences);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ParseSnapshot_UsesSnapshotBytesAfterFileChanges()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "chart.bmson");
        try
        {
            File.WriteAllText(filePath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{\"title\":\"Before\",\"artist\":\"Artist\",\"level\":7,\"mode_hint\":\"beat-7k\"},"
                + "\"sound_channels\":[],"
                + "\"lines\":[{\"y\":0}]"
                + "}");
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);

            File.WriteAllText(filePath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{\"title\":\"After\",\"artist\":\"Artist\",\"level\":7,\"mode_hint\":\"beat-7k\"},"
                + "\"sound_channels\":[],"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            var actual = BmsonSongParser.ParseSnapshot(snapshot);

            Assert.AreEqual("Before", actual.title);
            Assert.AreEqual(snapshot.Md5, actual.md5);
            Assert.AreEqual(snapshot.Sha256, actual.sha256);
            Assert.IsTrue(actual.HasFreshResourceReferences);
            Assert.AreNotEqual(BmsonSongParser.Parse(filePath).md5, actual.md5);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ParseSnapshot_InvalidJsonThrowsParserException()
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string filePath = Path.Combine(tempDirectory, "chart.bmson");
        try
        {
            ChartFileSnapshot snapshot = ChartFileContentReader.CreateSnapshot(filePath, System.Text.Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\",\"info\":"), DateTime.UtcNow);

            try
            {
                BmsonSongParser.ParseSnapshot(snapshot);
                Assert.Fail("Expected bmson parser to throw for invalid JSON.");
            }
            catch (Exception ex)
            {
                StringAssert.Contains(ex.GetType().Name, "Json");
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void ResolvePlaylistMode_MapsPhase5DisplayModes()
    {
        Assert.AreEqual(5, BmsonSongParser.ResolvePlaylistMode("beat-5k"));
        Assert.AreEqual(7, BmsonSongParser.ResolvePlaylistMode("beat-7k"));
        Assert.AreEqual(10, BmsonSongParser.ResolvePlaylistMode("beat-10k"));
        Assert.AreEqual(14, BmsonSongParser.ResolvePlaylistMode("beat-14k"));
        Assert.AreEqual(9, BmsonSongParser.ResolvePlaylistMode("popn-5k"));
        Assert.AreEqual(9, BmsonSongParser.ResolvePlaylistMode("popn-9k"));
        Assert.AreEqual(24, BmsonSongParser.ResolvePlaylistMode("keyboard-24k"));
        Assert.AreEqual(48, BmsonSongParser.ResolvePlaylistMode("keyboard-24k-double"));
        Assert.IsNull(BmsonSongParser.ResolvePlaylistMode("unknown-mode"));
    }

    [TestMethod]
    public void PendingChartEntry_UpdateFromBmsonSong_ReusesRowAndUpdatesDisplayFields()
    {
        PendingChartEntry row = PendingChartEntry.CreateFromBmsonSong(new BeMusicSeeker.Models.LR2.LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\OldFolder\chart.bmson",
            folder = @"C:\Songs\OldFolder",
            title = "OldTitle",
            artist = "OldArtist",
            mode_hint = "beat-7k"
        });

        row.UpdateFromBmsonSong(new BeMusicSeeker.Models.LR2.LR2SongDBExtended.bmson_song
        {
            path = @"C:\Songs\NewFolder\chart.bmson",
            folder = @"C:\Songs\NewFolder",
            title = "NewTitle",
            artist = "NewArtist",
            mode_hint = "keyboard-24k"
        });

        Assert.AreEqual(@"C:\Songs\NewFolder\chart.bmson", row.path);
        Assert.AreEqual("NewFolder", row.Folder);
        Assert.AreEqual("NewTitle", row.Title);
        Assert.AreEqual("NewArtist", row.Artist);
        Assert.AreEqual(24, row.mode);
    }

    private static void AssertBmsonEqual(BeMusicSeeker.Models.LR2.LR2SongDBExtended.bmson_song expected, BeMusicSeeker.Models.LR2.LR2SongDBExtended.bmson_song actual)
    {
        Assert.AreEqual(expected.path, actual.path);
        Assert.AreEqual(expected.folder, actual.folder);
        Assert.AreEqual(expected.title, actual.title);
        Assert.AreEqual(expected.subtitle, actual.subtitle);
        Assert.AreEqual(expected.artist, actual.artist);
        Assert.AreEqual(expected.genre, actual.genre);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.mode_hint, actual.mode_hint);
        Assert.AreEqual(expected.banner, actual.banner);
        Assert.AreEqual(expected.backbmp, actual.backbmp);
        Assert.AreEqual(expected.stagefile, actual.stagefile);
        Assert.AreEqual(expected.preview_music, actual.preview_music);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.updated_at, actual.updated_at);
        CollectionAssert.AreEquivalent(expected.wav_files.ToArray(), actual.wav_files.ToArray());
        CollectionAssert.AreEquivalent(expected.bga_files.ToArray(), actual.bga_files.ToArray());
    }
}
