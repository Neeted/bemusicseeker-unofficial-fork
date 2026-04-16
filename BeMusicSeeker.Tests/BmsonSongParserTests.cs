using System;
using System.IO;
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
}
