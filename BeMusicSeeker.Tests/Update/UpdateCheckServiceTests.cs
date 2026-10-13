using System;
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

    /// <summary>整数tokenのInt32/Int64範囲外だけを型付き検証失敗へ変換し、元OverflowExceptionと対象項目を保ちます。</summary>
    [TestMethod]
    [DataRow("schemaVersion", "2147483648", "Int32")]
    [DataRow("schemaVersion", "-2147483649", "Int32")]
    [DataRow("schemaVersion", "999999999999999999999999999999999999999999", "Int32")]
    [DataRow("packageFormatVersion", "2147483648", "Int32")]
    [DataRow("packageFormatVersion", "-2147483649", "Int32")]
    [DataRow("packageFormatVersion", "999999999999999999999999999999999999999999", "Int32")]
    [DataRow("sizeBytes", "9223372036854775808", "Int64")]
    [DataRow("sizeBytes", "-9223372036854775809", "Int64")]
    [DataRow("sizeBytes", "999999999999999999999999999999999999999999", "Int64")]
    public void ParseManifest_OutOfRangeIntegerRetainsValidationCause(string field, string value, string range)
    {
        UpdateManifestValidationException failure = Assert.ThrowsException<UpdateManifestValidationException>(
            () => UpdateCheckService.ParseManifest(CreateManifestWithInteger(field, value)));

        Assert.IsInstanceOfType<OverflowException>(failure.InnerException);
        StringAssert.Contains(failure.Message, field);
        StringAssert.Contains(failure.Message, range);
    }

    /// <summary>表現可能な非対応版・非正数sizeは従来の意味検証を維持し、範囲外変換失敗と混同しません。</summary>
    [TestMethod]
    [DataRow("schemaVersion", "-2147483648", "Unsupported schemaVersion:")]
    [DataRow("schemaVersion", "2147483647", "Unsupported schemaVersion:")]
    [DataRow("schemaVersion", "2", "Unsupported schemaVersion:")]
    [DataRow("packageFormatVersion", "-2147483648", "Unsupported packageFormatVersion:")]
    [DataRow("packageFormatVersion", "2147483647", "Unsupported packageFormatVersion:")]
    [DataRow("packageFormatVersion", "2", "Unsupported packageFormatVersion:")]
    [DataRow("sizeBytes", "-9223372036854775808", "asset sizeBytes must be positive.")]
    [DataRow("sizeBytes", "0", "asset sizeBytes must be positive.")]
    public void ParseManifest_RepresentableIntegerKeepsSemanticValidation(string field, string value, string expectedMessage)
    {
        UpdateManifestValidationException failure = Assert.ThrowsException<UpdateManifestValidationException>(
            () => UpdateCheckService.ParseManifest(CreateManifestWithInteger(field, value)));

        Assert.IsNull(failure.InnerException);
        StringAssert.StartsWith(failure.Message, expectedMessage);
    }

    /// <summary>sizeBytesの正のInt64上限は引き続き受理し、例外分類の修正で新たな入力上限を導入しません。</summary>
    [TestMethod]
    public void ParseManifest_MaximumInt64SizeRemainsAccepted()
    {
        UpdateManifest manifest = UpdateCheckService.ParseManifest(CreateManifestWithInteger("sizeBytes", "9223372036854775807"));

        Assert.AreEqual(long.MaxValue, manifest.Assets[0].SizeBytes);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual(1, manifest.PackageFormatVersion);
    }

    private static string CreateManifestWithInteger(string field, string value)
    {
        string schemaVersion = field == "schemaVersion" ? value : "1";
        string packageFormatVersion = field == "packageFormatVersion" ? value : "1";
        string sizeBytes = field == "sizeBytes" ? value : "123";
        return $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "version": "2.1.0.0",
              "releaseTag": "v2.1.0.0",
              "releasePageUrl": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/tag/v2.1.0.0",
              "packageFormatVersion": {{packageFormatVersion}},
              "minimumUpdaterVersion": "1",
              "assets": [
                {
                  "kind": "app",
                  "label": "app",
                  "fileName": "app.zip",
                  "url": "https://github.com/Neeted/bemusicseeker-unofficial-fork/releases/download/v2.1.0.0/app.zip",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "sizeBytes": {{sizeBytes}},
                  "includesChartInfoMetadata": false
                }
              ]
            }
            """;
    }

}
