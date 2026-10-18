using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartResourceDiagnosticsTests
{
    /// <summary>元ファイルを一度解析した定義番号・未使用・未定義を保ち、存在確認だけを導入後の同梱＋候補へ向けます。入力元の音源で候補の欠損を誤って埋めません。</summary>
    [TestMethod]
    public void Read_PreservesOriginalDefinitionsWhileUsingCapturedInstallationPlacement()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "chart.bms");
            File.WriteAllText(path, "#WAV01 tone.wav\n#WAV02 absent.wav\n#WAV03 unused.wav\n#00111:0102ZZ\n", Encoding.ASCII);
            File.WriteAllText(Path.Combine(directory, "absent.ogg"), "source only");
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(@"C:\Candidate", ["tone.ogg"]);
            var placement = new ChartResourcePlacement(ChartResourcePlacementBasis.FirstCandidate, @"C:\Candidate", candidate: cache.GetEntryOrNull(@"C:\Candidate"));
            ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms, placement);
            Assert.AreEqual(1, result.Issues.Length);
            Assert.AreEqual("02", result.Issues.Single().Definition.Id);
            Assert.AreEqual(ChartResourceDiagnosticReason.Missing, result.Issues.Single().Reason);
            Assert.AreEqual("03", result.UnusedDefinitions.Single().Id);
            Assert.AreEqual("ZZ", result.UndefinedReferences.Single().Id);
            ChartResourceDiagnostics current = new ChartResourceDiagnosticsService().Read(path, ChartFileKind.Bms);
            Assert.AreEqual("01", current.Issues.Single().Definition.Id);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>WAV/BMPの未定義は別名前空間で重複排除し、BASE62の大小文字を維持します。未使用BMPの同番号複数定義と別番号同パスを残し、空パスの定義を未定義番号へ変換しません。</summary>
    [TestMethod]
    public void Diagnose_BmsUnifiesResourceDefinitionsWithoutMergingNumberNamespaces()
    {
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(Snapshot("""
            #BASE 62
            #WAVAA visual.png
            #BMPAA sound.wav
            #WAV02 shared.wav
            #BMP02 shared.png
            #BMP02 other.png
            #BMP03 shared.png
            #BMP04 EMPTY
            #00111:0101AAaa
            #00104:0101AAaa04
            """.Replace("#BMP04 EMPTY", "#BMP04 ", StringComparison.Ordinal)), ChartFileKind.Bms);
        CollectionAssert.AreEquivalent(new[] { "#WAV01", "#WAVaa", "#BMP01", "#BMPaa" },
            result.UndefinedReferences.Select(item => item.Source + item.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "#WAV02:shared.wav", "#BMP02:shared.png", "#BMP02:other.png", "#BMP03:shared.png" },
            result.UnusedDefinitions.Select(item => item.Source + item.Id + ":" + item.Reference.RawPath).ToArray());
        Assert.AreEqual(3, result.Issues.Length);
        Assert.AreEqual("#WAV", result.Issues.Single(item => item.Definition.Reference.RawPath == "visual.png").Definition.Source);
        Assert.AreEqual("#BMP", result.Issues.Single(item => item.Definition.Reference.RawPath == "sound.wav").Definition.Source);
        Assert.AreEqual(ChartResourceDiagnosticReason.InvalidPath,
            result.Issues.Single(item => item.Definition.Id == "04").Reason);
    }

    /// <summary>BMSONの音声三系統とBGAの未使用を定義単位で観測します。三種イベントの同じ数値は一件とし、既存受理の文字列・object不正値から0を創作せず未使用0ヘッダーを保ちます。</summary>
    [TestMethod]
    public void Diagnose_BmsonPreservesChannelSourcesAndAggregatesUndefinedBgaNumbers()
    {
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(Snapshot("""
            {"info":{"back_image":"back.png"},
             "sound_channels":[{"name":"sound.wav","notes":[{"x":1,"y":0}]},{"name":"shared.wav","notes":[]},{"name":null,"notes":[]}],
             "key_channels":[{"name":"key.wav","notes":[{"x":1,"y":0}]},{"name":"shared.wav","notes":[]}],
             "mine_channels":[{"name":"mine.wav","notes":[{"x":1,"y":0}]},{"name":"shared.wav","notes":[]}],
             "bga":{"bga_header":[{"id":1,"name":"used.png"},{"id":2,"name":"unused.png"},{"id":0,"name":"zero.png"}],
                    "bga_events":[{"id":1,"y":0},{"id":9,"y":1},{"id":"bad","y":2},{"id":{"unsupported":1},"y":3}],
                    "layer_events":[{"id":9,"y":0},{"id":10,"y":1}],"poor_events":[{"id":10,"y":0},{"id":9,"y":1},{"id":11,"y":2}]}}
            """), ChartFileKind.Bmson);
        Assert.AreEqual(5, result.Issues.Length);
        CollectionAssert.AreEquivalent(new[] { "sound_channels[1]", "sound_channels[2]", "key_channels[1]", "mine_channels[1]", "bga_header[1]", "bga_header[2]" },
            result.UnusedDefinitions.Select(item => item.Source).ToArray());
        Assert.IsTrue(result.UnusedDefinitions.Where(item => !item.Source.StartsWith("bga_header", StringComparison.Ordinal)).All(item => item.Id == null));
        CollectionAssert.AreEquivalent(new[] { "bga_header:9", "bga_header:10", "bga_header:11" }, result.UndefinedReferences.Select(item => item.Source + ":" + item.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { "info.back_image", "sound_channels[0]", "key_channels[0]", "mine_channels[0]", "bga_header[0]" },
            result.Issues.Select(item => item.Definition.Source).ToArray());
    }

    /// <summary>未使用定義だけのCP932復号不能を必要資源の確認不能へ混入させません。実bytesを通し、通常解析の既存InputDiagnosticを保持したまま、診断の未使用定義へIDと状態を残します。</summary>
    [TestMethod]
    public void Diagnose_UnusedUndecodableWavDoesNotBecomeRequiredIssue()
    {
        ChartFileSnapshot snapshot = SnapshotWithInvalidCp932("\n", ("#WAV01 unused.wav", true));
        var service = new ChartResourceDiagnosticsService();
        ChartResourceDiagnostics result = service.Diagnose(snapshot, ChartFileKind.Bms);
        Assert.AreEqual(0, result.Issues.Length);
        Assert.AreEqual(1, result.UnusedDefinitions.Length);
        Assert.AreEqual("01", result.UnusedDefinitions[0].Id);
        Assert.AreEqual("#WAV", result.UnusedDefinitions[0].Source);
        Assert.AreEqual(ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported, result.UnusedDefinitions[0].Reference.Status);
        Assert.IsFalse(result.UnusedDefinitions[0].Reference.IsUsed);

        ChartFile regular = BmsChartFileParser.ParseSnapshot(snapshot);
        Assert.IsTrue(regular.Resources.Any(reference => reference.Usage == ChartResourceUsage.InputDiagnostic
            && reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
        Assert.IsFalse(regular.Resources.First(reference => reference.Usage == ChartResourceUsage.Normal).Status
            == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported);
    }

    /// <summary>実bytesの複数復号不能を、それぞれのID・定義行へ対応付けます。同IDの正常定義まで復号不能とせず、全角空白付きの実採用定義とCR/LFの境界も既存パーサーに従います。</summary>
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r\n")]
    [DataRow("\r")]
    public void Diagnose_UndecodableDefinitionsKeepOccurrenceAndUsageContext(string newline)
    {
        ChartFileSnapshot snapshot = SnapshotWithInvalidCp932(newline,
            ("#WAV01 valid.wav", false),
            ("\u3000#WAV01 bad-a.wav", true),
            (" #WAV01 bad-b.wav", true),
            ("#WAV02 bad-c.wav", true),
            ("#BMP01 bad-image.png", true),
            ("#00111:0102", false),
            ("#00104:01", false));
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(snapshot, ChartFileKind.Bms);
        Assert.AreEqual(5, result.Issues.Length);
        ChartResourceDiagnosticIssue valid = result.Issues.Single(issue => issue.Definition.Reference.RawPath == "valid.wav");
        Assert.AreEqual("01", valid.Definition.Id);
        Assert.AreEqual(ChartResourceDiagnosticReason.Missing, valid.Reason);
        ChartResourceDiagnosticIssue[] undecodable = result.Issues.Where(issue => issue.Reason == ChartResourceDiagnosticReason.Cp932DecodeUnsupported).ToArray();
        Assert.AreEqual(4, undecodable.Length);
        foreach ((string id, string filename) in new[] { ("01", "bad-a.wav"), ("01", "bad-b.wav"), ("02", "bad-c.wav"), ("01", "bad-image.png") })
        {
            Assert.AreEqual(1, undecodable.Count(issue => issue.Definition.Id == id && issue.Definition.Reference.RawPath.StartsWith(filename, StringComparison.Ordinal)));
        }
        Assert.IsFalse(result.Issues.Any(issue => issue.Definition.Reference.Usage == ChartResourceUsage.InputDiagnostic));
        Assert.AreEqual("#BMP", result.Issues.Single(issue => issue.Definition.Reference.RawPath.StartsWith("bad-image.png", StringComparison.Ordinal)).Definition.Source);
    }

    /// <summary>任意画像の復号状態は実採用した行へ結び付けます。最初の非空値の採用、後続の無視、空指定の後の採用を実bytesで確認し、無視された不正値を正常画像の理由へ転用しません。</summary>
    [TestMethod]
    public void Diagnose_OptionalImageDecodeStatusFollowsSelectedDefinition()
    {
        ChartFileSnapshot snapshot = SnapshotWithInvalidCp932("\n",
            ("\u3000#STAGEFILE stage.png", true),
            ("#STAGEFILE ignored-stage.png", false),
            ("#BACKBMP first-back.png", false),
            ("#BACKBMP ignored-back.png", true),
            ("#BANNER ", false),
            ("#BANNER banner.png", true));
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(snapshot, ChartFileKind.Bms);
        Assert.AreEqual(3, result.Issues.Length);
        foreach (ChartResourceUsage usage in new[] { ChartResourceUsage.Stagefile, ChartResourceUsage.Banner })
        {
            Assert.AreEqual(ChartResourceDiagnosticReason.Cp932DecodeUnsupported,
                result.Issues.Single(issue => issue.Definition.Reference.Usage == usage).Reason);
        }
        ChartResourceDiagnosticIssue back = result.Issues.Single(issue => issue.Definition.Reference.Usage == ChartResourceUsage.Backbmp);
        Assert.AreEqual(ChartResourceDiagnosticReason.Missing, back.Reason);
        Assert.AreEqual("first-back.png", back.Definition.Reference.RawPath);
        Assert.IsFalse(result.Issues.Any(issue => issue.Definition.Reference.RawPath.StartsWith("ignored", StringComparison.Ordinal)));
    }

    /// <summary>先行バイトがLF/CRを消費する既存の置換復号では、次の物理定義行は前の値へ含まれます。この解釈を保ったまま、その後に採用される不正定義の状態が行ずれしないことを確認します。</summary>
    [TestMethod]
    [DataRow("\n")]
    [DataRow("\r")]
    public void Diagnose_DecodeConsumedNewlineKeepsParserLineAssociation(string newline)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(string.Join(newline,
            "#WAV01 merged.wav~", "#WAV02 swallowed.wav", "#WAV03 valid.wav", "#WAV04 later.wav~0", "#00111:01020304"));
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '~')
            {
                bytes[index] = 0x81;
            }
        }
        var snapshot = new ChartFileSnapshot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "chart.bms"),
            bytes, DateTime.UtcNow, "md5", "sha256");
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(snapshot, ChartFileKind.Bms);
        Assert.AreEqual(3, result.Issues.Length);
        Assert.AreEqual(ChartResourceDiagnosticReason.Cp932DecodeUnsupported, result.Issues.Single(issue => issue.Definition.Id == "01").Reason);
        Assert.AreEqual(ChartResourceDiagnosticReason.Missing, result.Issues.Single(issue => issue.Definition.Id == "03").Reason);
        Assert.AreEqual(ChartResourceDiagnosticReason.Cp932DecodeUnsupported, result.Issues.Single(issue => issue.Definition.Id == "04").Reason);
        CollectionAssert.AreEqual(new[] { "#WAV02" }, result.UndefinedReferences.Select(item => item.Source + item.Id).ToArray());
    }

    /// <summary>全行・LNOBJを使う実パーサーを通し、同パス別IDと同ID複数定義を定義単位で残し、未定義の反復をID一件へ集約します。使用IDの互換条件網羅はChartResourceSnapshotTestsへ分担します。</summary>
    [TestMethod]
    public void Diagnose_BmsPreservesDefinitionUnitsAndDistinctUndefinedIds()
    {
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(Snapshot("""
            #TITLE Diagnostic
            #ARTIST Author
            #LNOBJ ZZ
            #WAV01 shared.wav
            #WAV02 shared.wav
            #WAV03 unused-a.wav
            #WAV03 unused-b.wav
            #WAV04 used-a.wav
            #WAV04 used-b.wav
            #BMP05 not-a-wav-definition.wav
            #00111:0104ZZ09ZZ09
            """), ChartFileKind.Bms);

        Assert.AreEqual("Diagnostic", result.Title);
        Assert.AreEqual("Author", result.Artist);
        CollectionAssert.AreEquivalent(new[] { "02:shared.wav", "03:unused-a.wav", "03:unused-b.wav", "05:not-a-wav-definition.wav" },
            result.UnusedDefinitions.Select(item => item.Id + ":" + item.Reference.RawPath).ToArray());
        CollectionAssert.AreEquivalent(new[] { "01:shared.wav", "04:used-a.wav", "04:used-b.wav" },
            result.Issues.Select(item => item.Definition.Id + ":" + item.Definition.Reference.RawPath).ToArray());
        CollectionAssert.AreEquivalent(new[] { "#WAVZZ", "#WAV09" }, result.UndefinedReferences.Select(item => item.Source + item.Id).ToArray());
        Assert.IsFalse(result.Issues.Any(item => item.Definition.Id is "ZZ" or "09"));
    }

    /// <summary>固有の物理フォルダと実サービスで音声・画像・動画の種別別代替を照合し、三種の補助画像、不正・未対応・不足を区別します。省略任意画像は空入力で不足にしません。</summary>
    [TestMethod]
    public void Read_UsesSharedExtensionRulesAndReportsRequiredReferenceReasons()
    {
        string directory = CreateDirectory();
        try
        {
            foreach (string name in new[] { "tone.ogg", "picture.jpg", "video.mp4", "stage.jpg" })
            {
                File.WriteAllBytes(Path.Combine(directory, name), []);
            }
            string path = Path.Combine(directory, "chart.bms");
            File.WriteAllText(path, """
                #WAV01 tone.wav
                #WAV02 bad|name.wav
                #WAV03 ../outside.wav
                #WAV04 absent.wav
                #BMP01 picture.bmp
                #BMP02 video.avi
                #BMP03 unsupported.dat
                #BMP04 absent.png
                #STAGEFILE stage.bmp
                #BACKBMP back.png
                #BANNER banner.bmp
                #00111:01020304
                #00104:01020304
                """, Encoding.GetEncoding("shift_jis"));
            var service = new ChartResourceDiagnosticsService();
            ChartResourceDiagnostics result = service.Read(path, ChartFileKind.Bms);
            Assert.AreEqual(7, result.Issues.Length);
            Assert.IsTrue(result.Issues.Any(item => item.Definition.Id == "02" && item.Reason == ChartResourceDiagnosticReason.InvalidPath));
            Assert.IsTrue(result.Issues.Any(item => item.Definition.Id == "03" && item.Definition.Reference.Kind == ChartResourceKind.Audio && item.Reason == ChartResourceDiagnosticReason.Unsupported));
            Assert.IsTrue(result.Issues.Any(item => item.Definition.Reference.RawPath == "unsupported.dat" && item.Reason == ChartResourceDiagnosticReason.Unsupported));
            CollectionAssert.AreEquivalent(new[] { "absent.wav", "absent.png", "back.png", "banner.bmp" },
                result.Issues.Where(item => item.Reason == ChartResourceDiagnosticReason.Missing).Select(item => item.Definition.Reference.RawPath).ToArray());
            Assert.IsFalse(result.Issues.Any(item => item.Definition.Reference.Usage == ChartResourceUsage.Stagefile));
            Assert.IsTrue(result.Issues.Any(item => item.Definition.Reference.Usage == ChartResourceUsage.Backbmp));
            Assert.IsTrue(result.Issues.Any(item => item.Definition.Reference.Usage == ChartResourceUsage.Banner));

            Assert.AreEqual(0, service.Diagnose(Snapshot("#TITLE No resources", path), ChartFileKind.Bms).Issues.Length);
            Assert.AreEqual(0, service.Diagnose(Snapshot("#STAGEFILE \n#BACKBMP \n#BANNER ", path), ChartFileKind.Bms).Issues.Length);
            Assert.AreEqual(0, service.Diagnose(Snapshot("{\"info\":{\"eyecatch_image\":\"\",\"back_image\":\"\",\"banner_image\":\"\"}}", path), ChartFileKind.Bmson).Issues.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>BMSONは必要資源だけを検査し、未使用音声チャンネルと未定義BGA参照番号を元の項目名で保持します。数値の既存受理はBmsonSongParserTestsへ分担します。</summary>
    [TestMethod]
    public void Diagnose_BmsonChecksUsedResourcesAndRetainsNativeDefinitionSources()
    {
        ChartResourceDiagnostics result = new ChartResourceDiagnosticsService().Diagnose(Snapshot("""
            {"info":{"title":"BMSON","artist":"Artist"},
             "sound_channels":[{"name":"missing.wav","notes":[{"x":1,"y":0}]},{"name":"unused.wav","notes":[]}],
             "bga":{"bga_header":[{"id":7,"name":"missing.mp4"}],"bga_events":[{"id":7,"y":0},{"id":99,"y":1}]}}
            """), ChartFileKind.Bmson);

        Assert.AreEqual(2, result.Issues.Length);
        Assert.IsTrue(result.Issues.Any(item => item.Definition.Id == "7" && item.Definition.Reference.RawPath == "missing.mp4"));
        Assert.IsFalse(result.Issues.Any(item => item.Definition.Id == "99"));
        Assert.AreEqual(1, result.UnusedDefinitions.Length);
        Assert.AreEqual("sound_channels[1]", result.UnusedDefinitions[0].Source);
        Assert.IsNull(result.UnusedDefinitions[0].Id);
        Assert.AreEqual("bga_header", result.UndefinedReferences.Single().Source);
        Assert.AreEqual("99", result.UndefinedReferences.Single().Id);
    }

    /// <summary>実ファイルの取得失敗と実BMSON解析失敗を空の成功に変換しません。画面の失敗表示はViewModelテストへ分担します。</summary>
    [TestMethod]
    public void Read_PropagatesFileAndParserFailures()
    {
        string directory = CreateDirectory();
        try
        {
            var service = new ChartResourceDiagnosticsService();
            Assert.ThrowsException<FileNotFoundException>(() => service.Read(Path.Combine(directory, "missing.bms"), ChartFileKind.Bms));
            string path = Path.Combine(directory, "bad.bmson");
            File.WriteAllText(path, "{");
            Assert.ThrowsException<JsonSerializationException>(() => service.Read(path, ChartFileKind.Bmson));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ChartFileSnapshot Snapshot(string text, string? path = null)
        => new(path ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "chart.bms"),
            Encoding.GetEncoding("shift_jis").GetBytes(text), DateTime.UtcNow, "md5", "sha256");

    private static ChartFileSnapshot SnapshotWithInvalidCp932(string newline, params (string Text, bool Corrupt)[] lines)
    {
        var bytes = new List<byte>();
        foreach ((string text, bool corrupt) in lines)
        {
            bytes.AddRange(Encoding.GetEncoding("shift_jis").GetBytes(text));
            if (corrupt)
            {
                bytes.AddRange([0x81, 0x30]); // 不正なCP932の対を作り、後続の改行自体は保ちます。
            }
            bytes.AddRange(Encoding.ASCII.GetBytes(newline));
        }
        return new ChartFileSnapshot(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "chart.bms"),
            bytes.ToArray(), DateTime.UtcNow, "md5", "sha256");
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), nameof(ChartResourceDiagnosticsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
