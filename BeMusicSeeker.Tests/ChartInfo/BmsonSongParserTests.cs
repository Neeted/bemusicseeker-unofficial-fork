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
    /// <summary>音声はノートのあるsound/key/mineだけ、BGAはイベントの数値IDだけを使用集合へ解決します。previewと補助画像を保持し、空notesと未参照ヘッダーを除きます。</summary>
    [TestMethod]
    public void ParseSnapshot_UsesChannelNotesAndNumericBgaEventIds()
    {
        const string json = """
            {"info":{"preview_music":"preview.ogg","banner_image":"banner.png"},
             "sound_channels":[{"name":"same.wav","notes":[{"x":1}]},{"name":"same.ogg","notes":[{"x":2}]},{"name":"unused.wav","notes":[]}],
             "key_channels":[{"name":"hidden.wav","notes":[{"x":1}]}],
             "mine_channels":[{"name":"mine.wav","notes":[{"x":1}]}],
             "bga":{"bga_header":[{"id":40,"name":"unused.png"},{"id":20,"name":"bg.png"},{"id":60,"name":"movie.mp4"},{"id":70,"name":"poor.png"}],
                    "bga_events":[{"id":20}],"layer_events":[{"id":60}],"poor_events":[{"id":99},{"id":70}]}}
            """;
        ChartFile chart = BmsonChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\chart.bmson",
            System.Text.Encoding.UTF8.GetBytes(json), DateTime.UtcNow, "md5", "sha256"));
        var resources = ChartResourceSnapshot.Create(chart);
        CollectionAssert.AreEquivalent(new[] { "same", "hidden", "mine", "preview" }, resources.AudioRelativePaths.ToArray());
        CollectionAssert.AreEquivalent(new[] { "bg", "poor" }, resources.VisualRelativePaths.ToArray());
        CollectionAssert.AreEquivalent(new[] { "movie" }, resources.MovieRelativePaths.ToArray());
        Assert.AreEqual(1, resources.OptionalImageReferenceCount);
        Assert.IsFalse(resources.HasUnresolvedAudio);
        Assert.IsFalse(resources.HasUnresolvedVisual);
        Assert.IsFalse(resources.HasUnresolvedMovie);
    }

    /// <summary>従来無視されたイベントIDの文字列・objectをBGA/LAYER/POORすべてで受理します。有効な数値IDだけを使用要求へ残し、詳細配置・ノート数・時間をIDなし入力から変えません。</summary>
    [TestMethod]
    [DataRow("\"bad\"")]
    [DataRow("{\"unsupported\":1}")]
    public void ParseSnapshotAndDetailed_InvalidBgaEventIdsRemainAccepted(string invalidId)
    {
        string Json(string extra) => "{\"info\":{\"title\":\"Song\",\"init_bpm\":120,\"resolution\":240},"
            + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0},{\"x\":2,\"y\":240}]}],"
            + "\"bga\":{\"bga_header\":[{\"id\":0,\"name\":\"unused-zero.png\"},{\"id\":20,\"name\":\"bg.png\"},"
            + "{\"id\":30,\"name\":\"movie.mp4\"},{\"id\":40,\"name\":\"poor.png\"}],"
            + string.Join(",", new[] { ("bga_events", 20), ("layer_events", 30), ("poor_events", 40) }.Select(item =>
                "\"" + item.Item1 + "\":[{\"y\":0,\"id\":" + item.Item2 + "},{\"y\":120" + extra + "},{\"y\":240,\"id\":99}]")) + "}}";
        byte[] baseline = System.Text.Encoding.UTF8.GetBytes(Json(string.Empty));
        byte[] changed = System.Text.Encoding.UTF8.GetBytes(Json(",\"id\":" + invalidId));
        foreach (byte[] bytes in new[] { baseline, changed })
        {
            ChartFile chart = BmsonChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\event.bmson", bytes, DateTime.UnixEpoch, "md5", "sha256"));
            var resources = ChartResourceSnapshot.Create(chart);
            CollectionAssert.AreEquivalent(new[] { "bg", "poor" }, resources.VisualRelativePaths.ToArray());
            CollectionAssert.AreEquivalent(new[] { "movie" }, resources.MovieRelativePaths.ToArray());
            Assert.IsFalse(resources.HasUnresolvedVisual);
            Assert.IsFalse(resources.HasUnresolvedMovie);
        }
        ChartInfoParser.ChartInfoParseResult expected = ChartInfoParser.ParseBytesDetailed(baseline, "event.bmson");
        ChartInfoParser.ChartInfoParseResult actual = ChartInfoParser.ParseBytesDetailed(changed, "event.bmson");
        Assert.IsNotNull(expected.ChartString);
        Assert.AreEqual(expected.ChartString, actual.ChartString);
        Assert.AreEqual(2, expected.Row.notes);
        Assert.AreEqual(expected.Row.notes, actual.Row.notes);
        Assert.AreEqual(expected.Row.n, actual.Row.n);
        Assert.AreEqual(expected.Row.length, actual.Row.length);
        Assert.AreEqual(expected.Row.speedchange, actual.Row.speedchange);
    }

    /// <summary>ノートを持つ音声と参照済みBGAのnull/空nameを省略へ読み替えず、使用カテゴリを照合不能として保持します。previewと補助画像の未指定は要求に加えません。</summary>
    [TestMethod]
    [DataRow("sound_channels", "null")]
    [DataRow("key_channels", "null")]
    [DataRow("mine_channels", "null")]
    [DataRow("bga", "null")]
    [DataRow("sound_channels", "\"\"")]
    [DataRow("key_channels", "\"\"")]
    [DataRow("mine_channels", "\"\"")]
    [DataRow("bga", "\"\"")]
    public void ParseSnapshot_UsedResourcesWithMissingNamesRemainUnresolved(string kind, string name)
    {
        string component = kind == "bga"
            ? "\"bga\":{\"bga_header\":[{\"id\":20,\"name\":" + name + "}],\"bga_events\":[{\"id\":20}]}"
            : "\"" + kind + "\":[{\"name\":" + name + ",\"notes\":[{\"x\":1,\"y\":0}]}]";
        string json = "{\"info\":{\"preview_music\":null,\"banner_image\":null}," + component + "}";
        ChartFile chart = BmsonChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Pending\missing-name.bmson",
            System.Text.Encoding.UTF8.GetBytes(json), DateTime.UtcNow, "md5", "sha256"));
        var resources = ChartResourceSnapshot.Create(chart);
        Assert.AreEqual(0, resources.TotalReferenceCount);
        Assert.AreEqual(kind != "bga", resources.HasUnresolvedAudio);
        Assert.AreEqual(kind == "bga", resources.HasUnresolvedVisual);
        Assert.AreEqual(kind == "bga", resources.HasUnresolvedMovie);
        Assert.AreEqual(1, resources.ResourceReferences.Count(reference => reference.IsUsed && reference.Usage == ChartResourceUsage.Normal));
        ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
        Assert.IsTrue(health.IsInformationChecked);
        Assert.AreEqual(kind == "bga" ? (int?)0 : null, health.WavFilesDefined);
        Assert.AreEqual(kind == "bga" ? (int?)0 : null, health.WavFilesExisting);
        Assert.AreEqual(kind == "bga" ? null : (int?)0, health.BgaFilesDefined);
        Assert.AreEqual(kind == "bga" ? null : (int?)0, health.MovieFilesDefined);
        Assert.AreEqual(false, health.BannerDefined);
    }

    /// <summary>原題省略・null・空文字でも副題と譜面名の表示を保ち、原題への合成表示の混入を検出する。</summary>
    [DataTestMethod]
    [DataRow("")]
    [DataRow("\"title\":null,")]
    [DataRow("\"title\":\"\",")]
    public void Parse_MissingTitleKeepsRawTitleEmptyAndComposedDisplay(string titleField)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string path = Path.Combine(tempDirectory, "chart.bmson");
            File.WriteAllText(path, "{\"version\":\"1.0.0\",\"info\":{" + titleField
                + "\"subtitle\":\"Alternate Name\",\"chart_name\":\"ANOTHER\",\"artist\":\"Artist\"},\"sound_channels\":[]}");
            ChartFile chart = ChartTestValues.ReadBmson(path);
            Assert.AreEqual(string.Empty, chart.RawTitle);
            Assert.AreEqual("Alternate Name [ANOTHER]", chart.Title);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>実 bmson の複合タイトル・副アーティスト・画像・モードを読み取り、メタデータの欠落や資源名の互換変換漏れを検出する。</summary>
    /// <remarks>実ファイルの捕捉と解析を接続し、読み込む内容の保証を確認する。</remarks>
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

            ChartFile parsed = ChartTestValues.ReadBmson(filePath);

            Assert.AreEqual(Path.GetFullPath(filePath), parsed.Path);
            Assert.AreEqual("Main", parsed.RawTitle);
            Assert.AreEqual("Sub", parsed.RawSubtitle);
            Assert.AreEqual("Another", parsed.ChartName);
            Assert.AreEqual("Sub [Another]", parsed.Subtitle);
            Assert.AreEqual("Artist", parsed.RawArtist);
            Assert.AreEqual("Sub1,Sub2", parsed.Subartist);
            Assert.AreEqual("Artist Sub1,Sub2", parsed.Artist);
            Assert.AreEqual("Genre", parsed.Genre);
            Assert.AreEqual(12d, parsed.Level);
            Assert.AreEqual("beat-14k", parsed.ModeHint);
            Assert.AreEqual("banner.png", parsed.Banner);
            Assert.AreEqual("back.png", parsed.Backbmp);
            Assert.AreEqual("stage.png", parsed.Stagefile);
            Assert.AreEqual("preview.wav", parsed.PreviewMusic);
            Assert.AreEqual(32, parsed.Md5.Length);
            Assert.AreEqual(64, parsed.Sha256.Length);
            Assert.AreEqual(14, BmsonChartFileParser.ResolvePlaylistMode(parsed.ModeHint));
            Assert.AreEqual("Main Sub [Another]", parsed.Title);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>音声・画像・動画を含む bmson から有効な参照を索引化し、拡張子だけの入力や未知形式が件数・キーへ混入する誤りを検出する。</summary>
    /// <remarks>実ファイルの捕捉と解析を接続し、読み込む内容の保証を確認する。</remarks>
    [TestMethod]
    public void Parse_ExtractsResourceReferences()
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
                + "\"preview_music\":\"preview.ogg\",\"banner_image\":\".png\",\"back_image\":\"\",\"eyecatch_image\":\".png\""
                + "},"
                + "\"sound_channels\":[{\"name\":\"keysound.wav\",\"notes\":[{\"x\":1,\"y\":0}]},{\"name\":\".wav\",\"notes\":[{\"x\":1,\"y\":0}]},{\"name\":\".ogg\",\"notes\":[{\"x\":1,\"y\":0}]}],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"movie.mp4\"},{\"id\":2,\"name\":\"image.png\"},{\"id\":3,\"name\":\".png\"},{\"id\":4,\"name\":\"mystery.xyz\"}],\"bga_events\":[{\"id\":1,\"y\":0},{\"id\":2,\"y\":0},{\"id\":3,\"y\":0},{\"id\":4,\"y\":0}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            ChartFile parsed = ChartTestValues.ReadBmson(filePath);
            CollectionAssert.AreEquivalent(new[] { "keysound.wav", "preview.wav", ".wav" }, parsed.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            CollectionAssert.AreEquivalent(new[] { "image.png", "movie.mp4", ".png" }, parsed.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && (reference.Kind == ChartResourceKind.Image || reference.Kind == ChartResourceKind.Movie)).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            ChartFile chart = parsed;
            // 生の参照、単譜面、集約の各入口で、拡張子だけの参照を索引へ混入させない。
            foreach (ChartResourceSnapshot snapshot in new[] { ChartResourceSnapshot.Create(parsed.Resources), ChartResourceSnapshot.Create(chart), ChartResourceSnapshot.CreateAggregate([chart]) })
            {
                Assert.AreEqual(2, snapshot.AudioReferenceCount);
                Assert.AreEqual(1, snapshot.VisualReferenceCount);
                Assert.AreEqual(1, snapshot.MovieReferenceCount);
                Assert.AreEqual(0, snapshot.OptionalImageReferenceCount);
                CollectionAssert.AreEquivalent(new[] { "keysound", "preview", "image", "movie" }, snapshot.EnumerateAllRelativePaths().ToArray());
                CollectionAssert.AreEquivalent(new[] { "keysound", "preview", "image", "movie" }.Select(ChartResourceKeyHash.GetLookupHash).ToArray(), snapshot.EnumerateAllRelativePathHashes().ToArray());
                Assert.AreEqual(0, snapshot.UnsupportedResourceReferenceCount);
                Assert.IsFalse(snapshot.HasUnsupportedParentTraversalReference);
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

    /// <summary>親参照を含む各資源を有効キーから除外して未対応情報へ保持し、警告対象の消失や資源種別の取り違えを検出する。</summary>
    /// <remarks>実ファイルの捕捉と解析を接続し、読み込む内容の保証を確認する。</remarks>
    [TestMethod]
    public void Parse_PreservesUnsupportedParentTraversalResourceReferences()
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
                + "\"title\":\"Parent Resource\","
                + "\"artist\":\"Artist\","
                + "\"preview_music\":\"..\\\\Base\\\\preview.ogg\","
                + "\"eyecatch_image\":\"..\\\\Base\\\\stage.png\""
                + "},"
                + "\"sound_channels\":[{\"name\":\"..\\\\Base\\\\keysound.wav\",\"notes\":[{\"x\":1,\"y\":0}]}],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"..\\\\Base\\\\movie.mp4\"}],\"bga_events\":[{\"id\":1,\"y\":0}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            ChartFile parsed = ChartTestValues.ReadBmson(filePath);
            var snapshot = ChartResourceSnapshot.Create(parsed.Resources);

            Assert.AreEqual(0, parsed.Resources.Count(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio));
            Assert.AreEqual(0, parsed.Resources.Count(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && (reference.Kind == ChartResourceKind.Image || reference.Kind == ChartResourceKind.Movie)));
            Assert.AreEqual(4, parsed.Resources.Count(reference => reference.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported || reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
            Assert.AreEqual(4, snapshot.UnsupportedResourceReferenceCount);
            Assert.IsTrue(snapshot.HasUnsupportedParentTraversalReference);
            Assert.AreEqual(2, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Audio));
            Assert.AreEqual(1, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Image));
            Assert.AreEqual(1, snapshot.UnsupportedResourceReferences.Count(reference => reference.Kind == ChartResourceKind.Movie));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>定義された資源欄だけを読み、任意の metadata 名の誤採用や key・mine 音声、任意画像の正規化漏れを検出する。</summary>
    /// <remarks>実ファイルの捕捉と解析を接続し、読み込む内容の保証を確認する。</remarks>
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
                + "\"sound_channels\":[{\"name\":\"sounds/keysound.ogg\",\"notes\":[{\"x\":1,\"y\":0}]}],"
                + "\"key_channels\":[{\"name\":\"keys/hidden.mp3\",\"notes\":[{\"x\":1,\"y\":0}]}],"
                + "\"mine_channels\":[{\"name\":\"mines/mine.wav\",\"notes\":[{\"x\":1,\"y\":0}]}],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"images\\\\layer.jpg\"},{\"id\":2,\"name\":\"movies\\\\clip.mp4\"}],\"bga_events\":[{\"id\":1,\"y\":0},{\"id\":2,\"y\":0}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            ChartFile parsed = ChartTestValues.ReadBmson(filePath);

            CollectionAssert.AreEquivalent(
                new[] { "preview.wav", Path.Combine("sounds", "keysound.wav"), Path.Combine("keys", "hidden.wav"), Path.Combine("mines", "mine.wav") },
                parsed.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { Path.Combine("images", "layer.png"), Path.Combine("movies", "clip.mp4") },
                parsed.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && (reference.Kind == ChartResourceKind.Image || reference.Kind == ChartResourceKind.Movie)).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            Assert.AreEqual("banner.png", parsed.Banner);
            Assert.AreEqual(Path.Combine("bg", "back.png"), parsed.Backbmp);
            Assert.AreEqual("stage.png", parsed.Stagefile);
            Assert.IsFalse(parsed.Resources.Any(reference => reference.Usage == ChartResourceUsage.Normal && reference.Kind == ChartResourceKind.Audio && reference.NormalizedPath == "ignored.ogg"));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>捕捉後に元ファイルを書き換えても元のタイトルとハッシュを返し、解析時の再読込みによる入力世代の混在を検出する。</summary>
    /// <remarks>実ファイルの捕捉と解析を接続し、読み込む内容の保証を確認する。</remarks>
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

            ChartFile actual = BmsonChartFileParser.ParseSnapshot(snapshot);

            Assert.AreEqual("Before", actual.RawTitle);
            Assert.AreEqual(snapshot.Md5, actual.Md5);
            Assert.AreEqual(snapshot.Sha256, actual.Sha256);
            Assert.AreNotEqual(ChartTestValues.ReadBmson(filePath).Md5, actual.Md5);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>途中で切れた JSON の捕捉入力を解析失敗として伝え、破損入力を正常な譜面として受理する誤りを検出する。</summary>
    [TestMethod]
    public void ParseSnapshot_InvalidJsonThrowsParserException()
    {
        string filePath = Path.Combine(Path.GetTempPath(), "BmsonSongParserTests", Guid.NewGuid().ToString("N"), "chart.bmson");
        ChartFileSnapshot snapshot = ChartFileContentReader.CreateSnapshot(filePath, System.Text.Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\",\"info\":"), DateTime.UtcNow);

        try
        {
            BmsonChartFileParser.ParseSnapshot(snapshot);
            Assert.Fail("Expected bmson parser to throw for invalid JSON.");
        }
        catch (Exception ex)
        {
            StringAssert.Contains(ex.GetType().Name, "Json");
        }
    }

    /// <summary>各公開 mode_hint をプレイリストの鍵数へ変換し、popn の表示互換や未知モードの未確定扱いを失う誤りを検出する。</summary>
    [TestMethod]
    public void ResolvePlaylistMode_MapsPhase5DisplayModes()
    {
        Assert.AreEqual(5, BmsonChartFileParser.ResolvePlaylistMode("beat-5k"));
        Assert.AreEqual(7, BmsonChartFileParser.ResolvePlaylistMode("beat-7k"));
        Assert.AreEqual(10, BmsonChartFileParser.ResolvePlaylistMode("beat-10k"));
        Assert.AreEqual(14, BmsonChartFileParser.ResolvePlaylistMode("beat-14k"));
        Assert.AreEqual(9, BmsonChartFileParser.ResolvePlaylistMode("popn-5k"));
        Assert.AreEqual(9, BmsonChartFileParser.ResolvePlaylistMode("popn-9k"));
        Assert.AreEqual(24, BmsonChartFileParser.ResolvePlaylistMode("keyboard-24k"));
        Assert.AreEqual(48, BmsonChartFileParser.ResolvePlaylistMode("keyboard-24k-double"));
        Assert.IsNull(BmsonChartFileParser.ResolvePlaylistMode("unknown-mode"));
    }

}
