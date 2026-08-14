using System;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UbmplaySettingsTests
{
    [TestMethod]
    public void TemporarilyRewriteSettings_PreservesPlayerContractAndRestoresOriginalBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string tempDirectory = Path.Combine(Path.GetTempPath(), "UbmplaySettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string iniPath = Path.Combine(tempDirectory, "ubm.ini");
        const string original =
            "[Main]\r\n"
            + "AlwaysOnTop=True ; keep comment\r\n"
            + "VSYNC=True\r\n"
            + "[Option]\r\n"
            + "BGA=1\r\n"
            + "AutoSeparate=False\r\n"
            + "SkinType=2\r\n"
            + "Volume=12\r\n"
            + "[Other]\r\n"
            + "Title=日本語\r\n";
        try
        {
            File.WriteAllText(iniPath, original, Encoding.GetEncoding("shift_jis"));
            byte[] originalBytes = File.ReadAllBytes(iniPath);

            var rewrite = new uBMplay.TemporarilyRewriteSettings(iniPath, 123);
            string rewritten = File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis"));

            StringAssert.Contains(rewritten, "AlwaysOnTop=False ; keep comment");
            StringAssert.Contains(rewritten, "VSYNC=False");
            StringAssert.Contains(rewritten, "BGA=3");
            StringAssert.Contains(rewritten, "AutoSeparate=True");
            StringAssert.Contains(rewritten, "SkinType=0");
            StringAssert.Contains(rewritten, "Volume=100");
            StringAssert.Contains(rewritten, "Title=日本語");

            rewrite.RevertSettings();

            CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(iniPath));
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
    public void TemporarilyRewriteSettings_CreatesMissingSectionsWithoutThrowing()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string tempDirectory = Path.Combine(Path.GetTempPath(), "UbmplaySettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string iniPath = Path.Combine(tempDirectory, "ubm.ini");
        try
        {
            File.WriteAllText(iniPath, "[Main]\r\n", Encoding.GetEncoding("shift_jis"));

            var rewrite = new uBMplay.TemporarilyRewriteSettings(iniPath, 37);
            string rewritten = File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis"));

            StringAssert.Contains(rewritten, "[Main]");
            StringAssert.Contains(rewritten, "AlwaysOnTop=False");
            StringAssert.Contains(rewritten, "[Option]");
            StringAssert.Contains(rewritten, "Volume=37");

            rewrite.RevertSettings();
            Assert.AreEqual(rewritten, File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis")));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [DataTestMethod]
    [DataRow(-1, "Volume=0")]
    [DataRow(37, "Volume=37")]
    [DataRow(101, "Volume=100")]
    public void TemporarilyRewriteSettings_ClampsVolume(int playerVolume, string expectedVolume)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string tempDirectory = Path.Combine(Path.GetTempPath(), "UbmplaySettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string iniPath = Path.Combine(tempDirectory, "ubm.ini");
        try
        {
            File.WriteAllText(
                iniPath,
                "[Main]\nAlwaysOnTop=True\nVSYNC=True\n[Option]\nBGA=1\nAutoSeparate=False\nSkinType=2\nVolume=12\n",
                Encoding.GetEncoding("shift_jis"));

            _ = new uBMplay.TemporarilyRewriteSettings(iniPath, playerVolume);

            StringAssert.Contains(
                File.ReadAllText(iniPath, Encoding.GetEncoding("shift_jis")),
                expectedVolume);
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
