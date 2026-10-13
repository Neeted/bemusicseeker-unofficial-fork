using BeMusicSeeker.Models.Update;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdateCheckServiceTests
{
    /// <summary>壊れたJSONだけをマニフェスト検証失敗へ変換し、取得所有者が診断できる元JSON原因を保持する。</summary>
    [TestMethod]
    public void ParseManifest_MalformedJsonRetainsValidationCause()
    {
        UpdateManifestValidationException failure = Assert.ThrowsException<UpdateManifestValidationException>(
            () => UpdateCheckService.ParseManifest("{\"schemaVersion\": ["));

        Assert.IsInstanceOfType<JsonException>(failure.InnerException);
        StringAssert.StartsWith(failure.Message, "update.json is not valid JSON:");
    }

    /// <summary>
    /// 通常版とメタデータ同梱版の公開manifestを受理して版と資産種別を保持し、既存配布形式を拒否・混同する誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// メタデータ同梱版だけのmanifestは失敗とし、必須の通常版資産なしで更新可能と判断する誤りを検出する。
    /// </summary>
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

    /// <summary>
    /// 現更新ツールが扱えないprotocolを要求するmanifestは失敗とし、非対応資産を正常な更新として受理する誤りを検出する。
    /// </summary>
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

}
