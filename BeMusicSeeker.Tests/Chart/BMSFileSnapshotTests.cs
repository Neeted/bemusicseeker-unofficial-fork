using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BMSFileSnapshotTests
{
    /// <summary>
    /// 読み取り後に実ファイルを置換しても捕捉時の独立計算したhash・時刻・生メタデータ・参照を解析へ渡し、再読取りによる入力混在を検出する。
    /// </summary>
    [TestMethod]
    public void CapturedInputKeepsHashesTimeAndRawMetadataAfterFileReplacement()
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
                + "#BMP01 image.png\r\n#00104:01\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));
            var lastWriteTimeUtc = new DateTime(2026, 5, 2, 1, 2, 3, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, lastWriteTimeUtc);

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            byte[] capturedBytes = File.ReadAllBytes(filePath);
            string expectedMd5 = Convert.ToHexStringLower(MD5.HashData(capturedBytes));
            string expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(capturedBytes));
            File.WriteAllText(filePath, "#TITLE Replacement\n#ARTIST Replacement\n");
            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

            Assert.AreEqual(Path.GetFullPath(filePath), snapshot.Path);
            Assert.AreEqual(capturedBytes.LongLength, snapshot.Length);
            Assert.AreEqual(lastWriteTimeUtc, snapshot.LastWriteTimeUtc);
            Assert.AreEqual(expectedMd5, snapshot.Md5);
            Assert.AreEqual(expectedSha256, snapshot.Sha256);
            Assert.AreEqual(expectedMd5, actual.Md5);
            Assert.AreEqual(expectedSha256, actual.Sha256);
            Assert.AreEqual(lastWriteTimeUtc, actual.LastWriteTimeUtc);
            Assert.AreEqual("MainTitle", actual.RawTitle);
            Assert.AreEqual("SubTitle", actual.RawSubtitle);
            Assert.AreEqual("MainTitle SubTitle", actual.Title);
            Assert.AreEqual("MainArtist", actual.RawArtist);
            Assert.AreEqual("SubArtist", actual.Subartist);
            Assert.AreEqual("MainArtist SubArtist", actual.Artist);
            Assert.AreEqual("TestGenre", actual.Genre);
            Assert.AreEqual(12d, actual.Level);
            Assert.AreEqual(4, actual.Difficulty);
            Assert.IsNull(actual.Token);
            Assert.IsTrue(actual.Resources.Any(reference => reference.RawPath == "keysound.wav"));
        });
    }

    /// <summary>
    /// 長い実パスのBMSをRibbit入口から読めてタイトル・作者・hashを得られ、通常パス長制約による読み取り失敗を検出する。
    /// </summary>
    [TestMethod]
    public void RibbitBmsFileConstructor_ReadsLongPathChart()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string longDirectoryPath = BuildLongDirectoryPath(tempDirectory, "ribbit-read");
            LongPathFileSystem.CreateDirectory(longDirectoryPath);
            string filePath = Path.Combine(longDirectoryPath, "kabukin________________________________________________________________________________________________________________________________________________.bms");
            WriteAllText(filePath,
                "#PLAYER 1\r\n"
                + "#TITLE LongPathRibbit\r\n"
                + "#ARTIST Reader\r\n"
                + "#WAV01 keysound.wav\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            var file = new Ribbit.BMS.BMSFile(filePath);

            Assert.AreEqual(filePath, file.Path);
            Assert.AreEqual("LongPathRibbit", file.Title);
            Assert.AreEqual("Reader", file.Artist);
            Assert.IsTrue(file.Md5?.Length == 32);
        });
    }

    /// <summary>
    /// 区切りと親移動を含む入力パスを正規化し、保存パスと拡張長I/Oパスへ不正な表現が残る誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ReadSnapshot_NormalizesAbsolutePathBeforeStorageAndExtendedIo()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE Normalized\r\n", Encoding.ASCII);
            string inputPath = Path.Combine(tempDirectory, "nested", "..", "chart.bms")
                .Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(inputPath);
            string extendedPath = LongPathFileSystem.ToExtendedPath(inputPath);

            Assert.AreEqual(Path.GetFullPath(filePath), snapshot.Path);
            StringAssert.StartsWith(extendedPath, @"\\?\");
            Assert.IsFalse(extendedPath.Contains("/"));
            Assert.IsFalse(extendedPath.Contains(@"\..\"));
        });
    }

    /// <summary>
    /// 韓国語を検出して表示メタデータを読み直しても音声・画像の参照はCP932解釈を保ち、LR2互換参照の書換えを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_WithDetectedEncodingKeepsResourceReferencesShiftJis()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "korean-resource.bms");
            const string title = "\uacaf";
            const string artist = "\uac00\ub098";
            const string resourceName = "\uac00.wav";
            const string imageName = "\uac00.png";
            File.WriteAllText(
                filePath,
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#ARTIST " + artist + "\r\n#WAV01 " + resourceName + "\r\n#STAGEFILE " + imageName + "\r\n#BANNER " + imageName + "\r\n#BACKBMP " + imageName + "\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile shiftJisParsed = BmsChartFileParser.ParseSnapshot(snapshot);
            BmsEncodingDetectionResult detectionResult = BmsEncodingDetector.Detect((snapshot).Bytes);

            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot, detectionResult);

            Assert.AreEqual("ks_c_5601-1987", detectionResult.EncodingName);
            Assert.AreEqual(title, actual.RawTitle);
            Assert.AreEqual(artist, actual.RawArtist);
            Assert.AreEqual(
                shiftJisParsed.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).RawPath,
                actual.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).RawPath);
            Assert.AreEqual(
                shiftJisParsed.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).NormalizedPath,
                actual.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).NormalizedPath);
            CollectionAssert.AreEqual(
                shiftJisParsed.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                actual.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).OrderBy(path => path, StringComparer.Ordinal).ToArray());
            Assert.AreEqual(shiftJisParsed.Stagefile, actual.Stagefile);
            Assert.AreEqual(shiftJisParsed.Banner, actual.Banner);
            Assert.AreEqual(shiftJisParsed.Backbmp, actual.Backbmp);
            Assert.AreNotEqual(resourceName, actual.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).RawPath);
            Assert.AreNotEqual(imageName, actual.Stagefile);
        });
    }

    /// <summary>
    /// UTF-8 BOM付き入力の表示メタデータをUTF-8で読んでも音声参照はCP932のままとし、BOM自動解釈による参照変更を検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_WithDetectedUtf8BomKeepsResourceReferencesShiftJis()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "utf8-bom-resource.bms");
            const string title = "\u3042 UTF-8";
            const string resourceName = "\u3042.wav";
            File.WriteAllText(
                filePath,
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#WAV01 " + resourceName + "\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile shiftJisParsed = BmsChartFileParser.ParseSnapshot(snapshot);
            var detectionResult = new BmsEncodingDetectionResult(
                "utf-8",
                BmsEncodingDetectionOutcome.Utf8,
                fastAscii: false,
                decodedText: File.ReadAllText(filePath, Encoding.UTF8));

            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot, detectionResult);

            Assert.AreEqual(title, actual.RawTitle);
            Assert.AreEqual(
                shiftJisParsed.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).RawPath,
                actual.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).RawPath);
            Assert.AreNotEqual(resourceName, actual.Resources.Single(reference => reference.Usage == ChartResourceUsage.Normal).RawPath);
        });
    }

    /// <summary>
    /// 通常・LN・二人用チャンネルとPMS拡張子から期待する鍵数を判定し、ゼロ列や無効区切りでモードが変わる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_DetectsModeFromChannelsAndExtension()
    {
        (string FileName, string ChannelLine, int ExpectedMode)[] cases =
        [
            ("fivekey.bms", "#00111:01", 5),
            ("default-five.bms", "#00111:000000", 5),
            ("legacy-seven.bms", "#00118:01", 7),
            ("pms-forced.pms", "#00111:01", 9),
            ("tenkey.bms", "#00121:01", 10),
            ("fourteenkey.bms", "#00128:01", 14),
            ("ln-seven.bms", "#00158:01", 7),
            ("ln-tenkey.bms", "#00161:01", 10),
            ("fullwidth-leading-space.bms", "\u3000#00111:01", 5),
            ("whitespace-separator-is-ignored.bms", "#00111 01", 5)
        ];
        foreach ((string fileName, string channelLine, int expectedMode) in cases)
        {
            WithTempDirectory(delegate (string tempDirectory)
            {
                string filePath = Path.Combine(tempDirectory, fileName);
                File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE Mode\r\n" + channelLine + "\r\n", Encoding.GetEncoding("shift_jis"));

                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
                ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

                Assert.AreEqual(expectedMode, actual.Mode, fileName);
            });
        }
    }

    /// <summary>
    /// 空のリソース定義と絶対パスを正常参照へ含めず、音声・画像の相対参照を正規化して不正参照の混入を検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_NormalizesDirectiveResourcesAndIgnoresInvalidDefinitions()
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
                + "#WAV02 audio/hit.mp3\r\n#00101:02\r\n"
                + "#WAV03\r\n#00101:03\r\n"
                + "#BMP01 image.jpg\r\n#00104:01\r\n"
                + "#BMP02 movie.mp4\r\n#00104:02\r\n"
                + "#BMP03 C:\\absolute.png\r\n#00104:03\r\n"
                + "#UNKNOWN ignored\r\n"
                + "#00111 01\r\n"
                + "#00112:00\r\n"
                + "#00113:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

            CollectionAssert.Contains(actual.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "sound.wav");
            CollectionAssert.Contains(actual.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Path.Combine("audio", "hit.wav"));
            CollectionAssert.Contains(actual.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind != ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "image.png");
            CollectionAssert.Contains(actual.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind != ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "movie.mp4");
            Assert.IsFalse(actual.Resources.Where(reference => reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Any(reference => string.IsNullOrWhiteSpace(reference.NormalizedPath)));
            Assert.IsFalse(actual.Resources.Where(reference => reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind != ChartResourceKind.Audio).Any(reference => reference.NormalizedPath.Contains(":")));
            Assert.AreEqual(5, actual.Mode);
        });
    }

    /// <summary>
    /// LR2互換の欠落・不正な数値指示とMAXTRACKS末尾文字を解釈し、保存用の難易度・判定・レベルが変わる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_UsesLr2SongMetadataDefaults()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "defaults.bms");
            File.WriteAllText(filePath,
                "#TITLE Defaults\r\n"
                + "#RANK foo\r\n"
                + "#MAXTRACKS 12tail\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

            Assert.AreEqual(12, actual.Level);
            Assert.AreEqual(-1, actual.Difficulty);
            Assert.AreEqual(5, actual.Mode);
            Assert.AreEqual(0, actual.Judge);
        });
    }

    /// <summary>
    /// 難易度の欠落・ゼロ・等号区切り・不正・範囲外・負値を区別して生値を保持し、早すぎる補正で情報を失う誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_KeepsRawLr2DifficultyDirectiveValuesBeforeFinalization()
    {
        (string CaseName, string DifficultyLine, int ExpectedDifficulty)[] cases =
        [
            ("missing", "", -1),
            ("zero", "#DIFFICULTY 0\r\n", 0),
            ("equals-separator", "#DIFFICULTY=5\r\n", 5),
            ("invalid", "#DIFFICULTY nope\r\n", 0),
            ("out-of-range", "#DIFFICULTY 7\r\n", 7),
            ("negative", "#DIFFICULTY -2\r\n", -2)
        ];
        foreach ((string caseName, string difficultyLine, int expectedDifficulty) in cases)
        {
            WithTempDirectory(delegate (string tempDirectory)
            {
                string filePath = Path.Combine(tempDirectory, caseName + ".bms");
                File.WriteAllText(filePath,
                    "#TITLE Raw Difficulty\r\n"
                    + difficultyLine
                    + "#00111:01\r\n",
                    Encoding.GetEncoding("shift_jis"));

                ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
                ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

                Assert.AreEqual(expectedDifficulty, actual.Difficulty, caseName);
            });
        }
    }

    /// <summary>
    /// 等号区切りのレベル・難易度・判定指示を受理し、LR2が読める既存入力を読み落とす誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_AcceptsLr2NumericDirectiveSeparators()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "numeric-separators.bms");
            File.WriteAllText(filePath,
                "#TITLE Numeric Separators\r\n"
                + "#PLAYLEVEL=12\r\n"
                + "#DIFFICULTY=5\r\n"
                + "#RANK=3\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

            Assert.AreEqual(12, actual.Level);
            Assert.AreEqual(5, actual.Difficulty);
            Assert.AreEqual(3, actual.Judge);
        });
    }

    /// <summary>
    /// 難易度未指定のファイル名先頭だけでは難易度を補わず、無関係な命名から値を捏造する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_DoesNotInferDifficultyFromFilenamePrefix()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "_a_filename_prefix.bms");
            File.WriteAllText(filePath,
                "#TITLE Filename Prefix\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

            Assert.AreEqual(-1, actual.Difficulty);
        });
    }

    /// <summary>
    /// タイトル・ジャンルの難易度語を読み取り、明示難易度と字幕を優先して元の表示文字列を改変する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_InfersLr2DifficultyFromTitleAndGenre()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string titleTokenPath = Path.Combine(tempDirectory, "title-token.bms");
            File.WriteAllText(titleTokenPath,
                "#TITLE Token Song [Hyper]\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFile titleToken = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(titleTokenPath));

            Assert.AreEqual("Token Song [Hyper]", titleToken.RawTitle);
            Assert.IsTrue(string.IsNullOrWhiteSpace(titleToken.RawSubtitle));
            Assert.AreEqual(3, titleToken.Difficulty);

            string titleDashPath = Path.Combine(tempDirectory, "title-dash.bms");
            File.WriteAllText(titleDashPath,
                "#TITLE Dash Song -Hyper-\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFile titleDash = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(titleDashPath));

            Assert.AreEqual("Dash Song -Hyper-", titleDash.RawTitle);
            Assert.IsTrue(string.IsNullOrWhiteSpace(titleDash.RawSubtitle));
            Assert.AreEqual(3, titleDash.Difficulty);

            string titleSuffixPath = Path.Combine(tempDirectory, "title-suffix.bms");
            File.WriteAllText(titleSuffixPath,
                "#TITLE Suffix Song Easy\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFile titleSuffix = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(titleSuffixPath));

            Assert.AreEqual("Suffix Song Easy", titleSuffix.RawTitle);
            Assert.AreEqual(string.Empty, titleSuffix.RawSubtitle);
            Assert.AreEqual(1, titleSuffix.Difficulty);

            string genreTokenPath = Path.Combine(tempDirectory, "genre-token.bms");
            File.WriteAllText(genreTokenPath,
                "#GENRE Style <Another>\r\n"
                + "#TITLE Genre Song\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFile genreToken = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(genreTokenPath));

            Assert.AreEqual("Style <Another>", genreToken.Genre);
            Assert.AreEqual("Genre Song", genreToken.RawTitle);
            Assert.AreEqual(4, genreToken.Difficulty);

            string explicitAfterPath = Path.Combine(tempDirectory, "explicit-after-title.bms");
            File.WriteAllText(explicitAfterPath,
                "#TITLE Explicit Song [Hyper]\r\n"
                + "#DIFFICULTY 5\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFile explicitAfter = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(explicitAfterPath));

            Assert.AreEqual("Explicit Song [Hyper]", explicitAfter.RawTitle);
            Assert.IsTrue(string.IsNullOrWhiteSpace(explicitAfter.RawSubtitle));
            Assert.AreEqual(5, explicitAfter.Difficulty);

            string explicitSubtitlePath = Path.Combine(tempDirectory, "explicit-subtitle.bms");
            File.WriteAllText(explicitSubtitlePath,
                "#TITLE Manual Song [Hyper]\r\n"
                + "#SUBTITLE [Manual]\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFile explicitSubtitle = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(explicitSubtitlePath));

            Assert.AreEqual("Manual Song [Hyper]", explicitSubtitle.RawTitle);
            Assert.AreEqual("[Manual]", explicitSubtitle.RawSubtitle);
            Assert.AreEqual(3, explicitSubtitle.Difficulty);
        });
    }

    /// <summary>
    /// 音声・画像・動画の原記述と正規化参照を保持し、親移動の非対応参照も理由付きで残して診断情報の消失を検出する。
    /// </summary>
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
                + "#BMP01 visual\\bg.final.png\r\n#00104:01\r\n"
                + "#BMP02 movie\\op.mpg\r\n#00104:02\r\n"
                + "#WAV02 ..\\shared\\hit.wav\r\n#00101:02\r\n"
                + "#00111:01\r\n",
                Encoding.GetEncoding("shift_jis"));

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile actual = BmsChartFileParser.ParseSnapshot(snapshot);

            Assert.AreEqual(4, actual.Resources.Count);
            AssertResourceReference(actual, ChartResourceKind.Audio, @".\sound\kick.wav", Path.Combine("sound", "kick.wav"));
            AssertResourceReference(actual, ChartResourceKind.Image, @"visual\bg.final.png", Path.Combine("visual", "bg.final.png"));
            AssertResourceReference(actual, ChartResourceKind.Movie, @"movie\op.mpg", Path.Combine("movie", "op.mpg"));
            Assert.AreEqual(1, actual.Resources.Count(reference => reference.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported || reference.Status == ChartResourcePathNormalizationStatus.Cp932DecodeUnsupported));
            Assert.AreEqual(@"..\shared\hit.wav", actual.Resources.First(reference => reference.Status == ChartResourcePathNormalizationStatus.ParentTraversalUnsupported).RawPath);
        });
    }

    /// <summary>
    /// ASCII入力をCP932として判定し、保守用文字コード名と判定結果がASCIIでぶれる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void DetectEncodingOfBMSFile_AsciiUsesCp932CompatibilityEncoding()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "ascii.bms");
            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE ASCII\r\n#00111:01\r\n", Encoding.ASCII);

            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            BmsEncodingDetectionResult result = BmsEncodingDetector.Detect((snapshot).Bytes);

            Assert.AreEqual("shift_jis", result.EncodingName);
        });
    }

    /// <summary>
    /// 捕捉後に文字コードの異なるファイルへ置換しても捕捉bytesの判定結果を保ち、再読取りの内容を混ぜる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void DetectEncodingOfBMSFile_UsesSnapshotBytesAfterFileChanges()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#TITLE \uac00\ub098\ub2e4\r\n", Encoding.GetEncoding("ks_c_5601-1987"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            string snapshotEncoding = BmsEncodingDetector.Detect(snapshot.Bytes).EncodingName;

            File.WriteAllText(filePath, "#TITLE ASCII\r\n", Encoding.GetEncoding("shift_jis"));

            Assert.AreEqual(snapshotEncoding, BmsEncodingDetector.Detect(snapshot.Bytes).EncodingName);
            Assert.AreNotEqual(BmsEncodingDetector.Detect(ChartFileContentReader.ReadSnapshot(filePath).Bytes).EncodingName, BmsEncodingDetector.Detect(snapshot.Bytes).EncodingName);
        });
    }

    /// <summary>
    /// 捕捉後の実ファイル置換を表示メタデータの再読解へ反映せず、取得済みのタイトル・字幕・作者・ジャンルが混在する誤りを検出する。
    /// </summary>
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
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(filePath));

            File.WriteAllText(filePath,
                "#PLAYER 1\r\n#TITLE After\r\n#SUBTITLE SubAfter\r\n#ARTIST ArtistAfter\r\n#SUBARTIST SubArtistAfter\r\n#GENRE GenreAfter\r\n",
                Encoding.GetEncoding("shift_jis"));

            file = BmsChartFileParser.ApplyMetadataEncoding(snapshot, file, "shift_jis");

            Assert.AreEqual("Before", file.RawTitle);
            Assert.AreEqual("SubBefore", file.RawSubtitle);
            Assert.AreEqual("Before SubBefore", file.Title);
            Assert.AreEqual("ArtistBefore", file.RawArtist);
            Assert.AreEqual("SubArtistBefore", file.Subartist);
            Assert.AreEqual("ArtistBefore SubArtistBefore", file.Artist);
            Assert.AreEqual("GenreBefore", file.Genre);
        });
    }

    /// <summary>
    /// 韓国語の明示文字コードで生タイトル・字幕・作者・ジャンルを読み直し、合成表示や原文を欠落させる誤りを検出する。
    /// </summary>
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
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(filePath));
            Assert.AreNotEqual(title + " " + subtitle, file.Title);

            file = BmsChartFileParser.ApplyMetadataEncoding(ChartFileContentReader.ReadSnapshot(file.Path), file, "ks_c_5601-1987");

            Assert.AreEqual(title, file.RawTitle);
            Assert.AreEqual(subtitle, file.RawSubtitle);
            Assert.AreEqual(title + " " + subtitle, file.Title);
            Assert.AreEqual(artist, file.RawArtist);
            Assert.AreEqual(subartist, file.Subartist);
            Assert.AreEqual(artist + " " + subartist, file.Artist);
            Assert.AreEqual(genre, file.Genre);
        });
    }

    /// <summary>
    /// 曖昧CP932名で再読解して更新時刻だけを更新し、利用者列の追加日時と以前の捕捉値を変更する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void MetadataEncoding_NormalizesShiftJisQuestionAndPreservesUserColumns()
    {
        WithTempDirectory(delegate (string tempDirectory)
        {
            string filePath = Path.Combine(tempDirectory, "chart.bms");
            File.WriteAllText(filePath, "#PLAYER 1\r\n#TITLE ASCII\r\n#ARTIST Artist\r\n", Encoding.GetEncoding("shift_jis"));
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(filePath));
            file = file with { Date = 123 };
            file = file with { AddDate = 456 };

            ChartFile captured = file;
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(file.Path);
            file = BmsChartFileParser.ApplyMetadataEncoding(snapshot, file, "shift_jis?");

            Assert.AreEqual("ASCII", file.RawTitle);
            Assert.AreEqual("Artist", file.RawArtist);
            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(snapshot.LastWriteTimeUtc), file.Date);
            Assert.AreEqual(456, file.AddDate);
            Assert.AreEqual(123, captured.Date);
            Assert.AreEqual(456, captured.AddDate);
        });
    }

    /// <summary>
    /// 検出済み韓国語本文を再読解へ接続して生メタデータと表示を復元し、音声参照の互換性を失う誤りを検出する。
    /// </summary>
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
                "#PLAYER 1\r\n#TITLE " + title + "\r\n#SUBTITLE " + subtitle + "\r\n#ARTIST " + artist + "\r\n#SUBARTIST " + subartist + "\r\n#GENRE " + genre + "\r\n#WAV01 sound.wav\r\n#00101:01\r\n",
                Encoding.GetEncoding("ks_c_5601-1987"));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(filePath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);
            Assert.AreNotEqual(title, file.Title);

            BmsEncodingDetectionResult detectionResult = BmsEncodingDetector.Detect((snapshot).Bytes);
            file = BmsChartFileParser.ApplyEncodingDetection(snapshot, file, detectionResult);

            Assert.AreEqual("ks_c_5601-1987", detectionResult.EncodingName);
            Assert.AreEqual(title, file.RawTitle);
            Assert.AreEqual(subtitle, file.RawSubtitle);
            Assert.AreEqual(title + " " + subtitle, file.Title);
            Assert.AreEqual(artist, file.RawArtist);
            Assert.AreEqual(subartist, file.Subartist);
            Assert.AreEqual(artist + " " + subartist, file.Artist);
            Assert.AreEqual(genre, file.Genre);
            CollectionAssert.Contains(file.Resources.Where(reference => reference.Usage == ChartResourceUsage.Normal && reference.Status == ChartResourcePathNormalizationStatus.Valid && reference.Kind == ChartResourceKind.Audio).Select(reference => reference.NormalizedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), "sound.wav");
        });
    }

    private static void AssertResourceReference(ChartFile file, ChartResourceKind kind, string rawPath, string normalizedPath)
    {
        Assert.IsTrue(
            file.Resources.Any(reference =>
                reference.Kind == kind
                && reference.RawPath == rawPath
                && reference.NormalizedPath == normalizedPath),
            kind + "|" + rawPath + "|" + normalizedPath);
    }

    private static void WithTempDirectory(Action<string> action)
    {
        string tempDirectory = Path.Combine(Path.GetTempPath(), "BMSFileSnapshotTests", Guid.NewGuid().ToString("N"));
        LongPathFileSystem.CreateDirectory(tempDirectory);
        try
        {
            action(tempDirectory);
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempDirectory))
            {
                LongPathFileSystem.DeleteDirectory(tempDirectory, recursive: true);
            }
        }
    }

    private static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    private static void WriteAllText(string path, string contents, Encoding encoding)
    {
        using FileStream stream = LongPathFileSystem.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(contents);
    }
}
