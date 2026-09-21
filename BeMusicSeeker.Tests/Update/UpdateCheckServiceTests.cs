using System;
using System.IO;
using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateCheckServiceTests
{
    [TestMethod]
    public void UpdateCheckService_DoesNotKeepVersionTxtFallback()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "Update", "UpdateCheckService.cs"));

        Assert.IsFalse(source.Contains("version.txt"), "UpdateCheckService must not read legacy version.txt.");
        Assert.IsFalse(source.Contains("DefaultVersionUrl"), "UpdateCheckService must not keep the legacy version URL.");
        Assert.IsFalse(source.Contains("CheckVersionTxtFallback"), "UpdateCheckService must not keep version.txt fallback logic.");
    }

    [TestMethod]
    public void ParseManifest_AcceptsAppAndMetadataAssets()
    {
        string json = """
            {
              "schemaVersion": 1,
              "version": "2.1.0.0",
              "releaseTag": "v2.1.0.0",
              "releasePageUrl": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/tag/v2.1.0.0",
              "packageFormatVersion": 1,
              "publishedAt": "2026-06-22T00:00:00Z",
              "minimumUpdaterVersion": "1",
              "assets": [
                {
                  "kind": "app",
                  "label": "本体のみ",
                  "fileName": "bemusicseeker-unofficial-fork-v2.1.0.0.zip",
                  "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/bemusicseeker-unofficial-fork-v2.1.0.0.zip",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sizeBytes": 123,
                  "includesChartInfoMetadata": false
                },
                {
                  "kind": "app-with-metadata",
                  "label": "譜面解析済みメタデータ同梱版",
                  "fileName": "bemusicseeker-unofficial-fork-v2.1.0.0-with-metadata.zip",
                  "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/bemusicseeker-unofficial-fork-v2.1.0.0-with-metadata.zip",
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "sizeBytes": 456,
                  "includesChartInfoMetadata": true
                }
              ]
            }
            """;

        UpdateManifest manifest = UpdateCheckService.ParseManifest(json);

        Assert.AreEqual("2.1.0.0", manifest.VersionText);
        Assert.AreEqual(2, manifest.Assets.Count);
        Assert.AreEqual("app", manifest.Assets[0].Kind);
        Assert.AreEqual("app-with-metadata", manifest.Assets[1].Kind);
    }

    [TestMethod]
    public void ParseManifest_RejectsMissingAppAsset()
    {
        string json = """
            {
              "schemaVersion": 1,
              "version": "2.1.0.0",
              "releaseTag": "v2.1.0.0",
              "releasePageUrl": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/tag/v2.1.0.0",
              "packageFormatVersion": 1,
              "publishedAt": "2026-06-22T00:00:00Z",
              "minimumUpdaterVersion": "1",
              "assets": [
                {
                  "kind": "app-with-metadata",
                  "label": "譜面解析済みメタデータ同梱版",
                  "fileName": "bemusicseeker-unofficial-fork-v2.1.0.0-with-metadata.zip",
                  "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/bemusicseeker-unofficial-fork-v2.1.0.0-with-metadata.zip",
                  "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "sizeBytes": 456,
                  "includesChartInfoMetadata": true
                }
              ]
            }
            """;

        Assert.ThrowsException<UpdateManifestValidationException>(() => UpdateCheckService.ParseManifest(json));
    }

    [TestMethod]
    public void ParseManifest_RejectsUnsupportedUpdaterProtocol()
    {
        string json = """
            {
              "schemaVersion": 1,
              "version": "2.1.0.0",
              "releaseTag": "v2.1.0.0",
              "releasePageUrl": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/tag/v2.1.0.0",
              "packageFormatVersion": 1,
              "publishedAt": "2026-06-22T00:00:00Z",
              "minimumUpdaterVersion": "999",
              "assets": [
                {
                  "kind": "app",
                  "label": "本体のみ",
                  "fileName": "bemusicseeker-unofficial-fork-v2.1.0.0.zip",
                  "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/bemusicseeker-unofficial-fork-v2.1.0.0.zip",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sizeBytes": 123,
                  "includesChartInfoMetadata": false
                }
              ]
            }
            """;

        Assert.ThrowsException<UpdateManifestValidationException>(() => UpdateCheckService.ParseManifest(json));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
