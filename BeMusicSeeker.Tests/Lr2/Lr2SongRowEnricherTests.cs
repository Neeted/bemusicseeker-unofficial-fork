using System;
using System.IO;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongRowEnricherTests
{
    /// <summary>
    /// 実読込みの日時と利用者のtxt・お気に入り・登録日時・タグを外部保存行へ反映し、列の欠落と親情報の未投影を検出します。
    /// </summary>
    /// <remarks>実ファイルの時刻を読込み・解析・保存行投影へ渡す接続を観測します。</remarks>
    [TestMethod]
    public void CommonToStorageRow_CapturesSnapshotMetadataAndUserColumns()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SongRowEnricher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#ARTIST artist\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var timestamp = new DateTime(2026, 6, 1, 2, 3, 4, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(chartPath, timestamp);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile parsedChart = BmsChartFileParser.ParseSnapshot(snapshot) with
            {
                Txt = 1,
                Favorite = 7,
                AddDate = 12345,
                Tag = "keep-tag"
            };
            LR2SongDB.song parsed = ChartSongStorageMapping.ToBmsRow(parsedChart);

            Assert.AreEqual(Lr2SongRowEnricher.ToLr2UnixSeconds(timestamp), parsed.date);
            Assert.AreEqual(1, parsed.txt);
            Assert.AreEqual(7, parsed.favorite);
            Assert.AreEqual(12345, parsed.adddate);
            Assert.AreEqual("keep-tag", parsed.tag);
            Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(tempDirectoryPath), parsed.folder);
            Assert.IsFalse(string.IsNullOrWhiteSpace(parsed.parent));
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// CP932非対応パスでも譜面行を保ち、表現できないフォルダ・親だけを空にして外部キーの破損や全譜面拒否を検出します。
    /// </summary>
    [TestMethod]
    public void EnrichGeneratedSong_DoesNotThrowForCp932UnsupportedPath()
    {

        var file = new TestableBmsFile
        {
            path = @"C:\BMS\😀\chart.bms"
        };

        Lr2SongRowEnricher.EnrichGeneratedSong(file);

        Assert.IsTrue(string.IsNullOrWhiteSpace(file.folder));
        Assert.IsTrue(string.IsNullOrWhiteSpace(file.parent));
    }

    /// <summary>
    /// 追加生成する外部譜面行の未指定exlevelを0へ補完し、LR2が読む保存列に未定値を残す退行を検出します。
    /// </summary>
    [TestMethod]
    public void EnrichGeneratedSong_DefaultsExLevelToZero()
    {
        var file = new TestableBmsFile
        {
            path = @"C:\BMS\Pack\chart.bms"
        };

        Lr2SongRowEnricher.EnrichGeneratedSong(file);

        Assert.AreEqual(0, file.exlevel);
    }

    /// <summary>
    /// 追加生成する譜面行にLR2互換の軽量列を設定し、未解析のlevel・difficulty・mode・judgeの保存互換値を取り違える誤りを検出します。
    /// </summary>
    [TestMethod]
    public void EnrichGeneratedSong_DefaultsLr2LightweightColumns()
    {
        var file = new TestableBmsFile
        {
            path = @"C:\BMS\Pack\chart.bms"
        };

        Lr2SongRowEnricher.EnrichGeneratedSong(file);

        Assert.AreEqual(0, file.level);
        Assert.AreEqual(-1, file.difficulty);
        Assert.AreEqual(5, file.mode);
        Assert.AreEqual(2, file.judge);
    }

    /// <summary>
    /// 一致MD5の譜面情報を保存行へ反映し、BPM整数化と機能bitの投影を行って既存LR2のmode・judgeを上書きしないことを確認します。
    /// </summary>
    [TestMethod]
    public void EnrichFromChartInfo_AppliesChartMetadataWithoutOverwritingLr2RuntimeMetadata()
    {
        var file = new TestableBmsFile
        {
            path = @"C:\BMS\Pack\chart.bms",
            level = 3,
            difficulty = -1
        };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file.SetJudgeForTest(1);
        file.SetModeForTest(5);
        var chartInfo = new BeMusicSeeker.Models.ChartDetails
        {
            md5 = file.hash,
            level = 12,
            difficulty = 4,
            maxbpm = 180.9,
            minbpm = 90.1,
            mode = 14,
            judge = 100,
            bga = 1,
            exlevel = 120,
            feature = 4 | 8,
            notes = 1234
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(file, chartInfo);

        Assert.AreEqual(12, file.level);
        Assert.AreEqual(4, file.difficulty);
        Assert.AreEqual(180, file.maxbpm);
        Assert.AreEqual(90, file.minbpm);
        Assert.AreEqual(5, file.mode);
        Assert.AreEqual(1, file.judge);
        Assert.AreEqual(1, file.bga);
        Assert.AreEqual(120, file.exlevel);
        Assert.AreEqual(1, file.longnote);
        Assert.AreEqual(1, file.random);
        Assert.AreEqual(1234, file.karinotes);
    }

    /// <summary>
    /// 値が00でも存在するレーンと空白区切りを読んで外部modeへ投影し、音符値だけで譜面種別を判断する誤りを検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow("#00118:00\r\n", 7)]
    [DataRow("#00121:00\r\n", 10)]
    [DataRow("#00129:00\r\n", 14)]
    [DataRow("#00128 00\r\n", 14)]
    public void CreateBMSFileFromSnapshot_InfersLr2ModeFromChannelPresence(string chartText, int expectedMode)
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SongRowEnricher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, chartText, Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            LR2SongDB.song parsed = ChartSongStorageMapping.ToBmsRow(BmsChartFileParser.ParseSnapshot(snapshot));

            Assert.AreEqual(expectedMode, parsed.mode);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// CUSTOMFOLDER指定では元RANKに関わらず外部judgeを2へ投影し、カスタム定義へ通常譜面の判定値を持ち込む誤りを検出します。
    /// </summary>
    [TestMethod]
    public void CreateBMSFileFromSnapshot_CustomFolderDirectiveResetsLr2Judge()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_Lr2SongRowEnricher_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#RANK 3\r\n#CUSTOMFOLDER\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            LR2SongDB.song parsed = ChartSongStorageMapping.ToBmsRow(BmsChartFileParser.ParseSnapshot(snapshot));

            Assert.AreEqual(2, parsed.judge);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 異なるMD5の譜面情報は保存行へ適用せず、別譜面のlevelや音符数を混入する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void EnrichFromChartInfo_IgnoresMismatchedMd5()
    {
        var file = new TestableBmsFile { path = @"C:\BMS\Pack\chart.bms" };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var chartInfo = new BeMusicSeeker.Models.ChartDetails
        {
            md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            level = 12,
            notes = 1234
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(file, chartInfo);

        Assert.IsNull(file.level);
        Assert.IsNull(file.karinotes);
    }

    /// <summary>
    /// 一致譜面情報のnull exlevelを外部保存互換値0へ変換し、欠落値の未補完を検出します。
    /// </summary>
    [TestMethod]
    public void EnrichFromChartInfo_DefaultsNullExLevelToZero()
    {
        var file = new TestableBmsFile { path = @"C:\BMS\Pack\chart.bms" };
        file.SetHash("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var chartInfo = new BeMusicSeeker.Models.ChartDetails
        {
            md5 = file.hash,
            exlevel = null
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(file, chartInfo);

        Assert.AreEqual(0, file.exlevel);
    }

    /// <summary>
    /// 欠落・不正・正常difficultyの既存行投影と新規行補完を揃え、各行のmode・judgeを保って保存経路間の変換不一致を検出します。
    /// </summary>
    /// <remarks>null・-1・6はNormal補完、4は明示難度保持をDataRowで区別します。</remarks>
    [DataTestMethod]
    [DataRow(null, 2)]
    [DataRow(-1, 2)]
    [DataRow(6, 2)]
    [DataRow(4, 4)]
    public void ChartInfoSongProjection_NormalizationMatchesNewChartEnrichment(
        int? difficulty,
        int expectedDifficulty)
    {
        string path = @"C:\BMS\Pack\parity.bms";
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var newChartSong = new TestableBmsFile
        {
            path = path,
            mode = 5,
            difficulty = -1
        };
        newChartSong.SetHash(md5);
        newChartSong.SetJudgeForTest(1);
        var existingSong = new TestableBmsFile
        {
            path = path,
            mode = 14,
            difficulty = 0
        };
        existingSong.SetHash(md5);
        existingSong.SetJudgeForTest(9);
        var chartInfo = new BeMusicSeeker.Models.ChartDetails
        {
            md5 = md5,
            level = null,
            difficulty = difficulty,
            difficulty_defined = difficulty.HasValue,
            maxbpm = 180.9,
            minbpm = null,
            mode = 7,
            judge = 100,
            bga = null,
            exlevel = null,
            feature = 1 | 4 | 16,
            notes = 987
        };

        Lr2SongRowEnricher.EnrichFromChartInfo(newChartSong, chartInfo);
        var projection =
            Lr2ChartInfoSongProjection.Create(existingSong.path, existingSong.hash, chartInfo);
        Assert.IsTrue(projection.ApplyTo(existingSong));

        Assert.AreEqual(expectedDifficulty, newChartSong.difficulty);
        Assert.AreEqual(newChartSong.level, existingSong.level);
        Assert.AreEqual(newChartSong.difficulty, existingSong.difficulty);
        Assert.AreEqual(newChartSong.maxbpm, existingSong.maxbpm);
        Assert.AreEqual(newChartSong.minbpm, existingSong.minbpm);
        Assert.AreEqual(newChartSong.bga, existingSong.bga);
        Assert.AreEqual(newChartSong.exlevel, existingSong.exlevel);
        Assert.AreEqual(newChartSong.longnote, existingSong.longnote);
        Assert.AreEqual(newChartSong.random, existingSong.random);
        Assert.AreEqual(newChartSong.karinotes, existingSong.karinotes);
        Assert.AreEqual(5, newChartSong.mode);
        Assert.AreEqual(14, existingSong.mode);
        Assert.AreEqual(1, newChartSong.judge);
        Assert.AreEqual(9, existingSong.judge);
    }

    /// <summary>
    /// 未指定・負・範囲外のdifficultyをLR2のNormal整数2へ補完し、未定値を有効難度として保存する誤りを検出します。
    /// </summary>
    /// <remarks>DataRowのnull・-1・9は未指定・負値・範囲外です。</remarks>
    [DataTestMethod]
    [DataRow(null)]
    [DataRow(-1)]
    [DataRow(9)]
    public void ApplyLr2ChartMetadataDefaults_DefaultsInvalidDifficultyToNormal(int? difficulty)
    {
        var file = new TestableBmsFile
        {
            difficulty = difficulty
        };

        Lr2SongRowEnricher.ApplyLr2ChartMetadataDefaults(file);

        Assert.AreEqual(2, file.difficulty);
    }

    /// <summary>
    /// 有効な指定difficultyはそのまま保ち、補完処理が明示値までNormalへ置換する誤りを検出します。
    /// </summary>
    [TestMethod]
    public void ApplyLr2ChartMetadataDefaults_KeepsValidDifficulty()
    {
        var file = new TestableBmsFile
        {
            difficulty = 4
        };

        Lr2SongRowEnricher.ApplyLr2ChartMetadataDefaults(file);

        Assert.AreEqual(4, file.difficulty);
    }

    private sealed class TestableBmsFile : LR2SongDB.song
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetJudgeForTest(int? value)
        {
            judge = value;
        }

        public void SetModeForTest(int? value)
        {
            mode = value;
        }
    }
}
