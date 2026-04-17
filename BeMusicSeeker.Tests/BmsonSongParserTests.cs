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
            Assert.AreEqual("preview.ogg", parsed.preview_music);
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
            pending.SetHealthStatus(null, forceUpdate: false, memClear: false);

            CollectionAssert.AreEquivalent(new[] { "keysound.wav", "preview.ogg" }, pending.WAVfiles.ToArray());
            CollectionAssert.AreEquivalent(new[] { "image.png", "movie.mp4" }, pending.BGAfiles.ToArray());
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
}
