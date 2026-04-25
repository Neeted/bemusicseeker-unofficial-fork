using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartInfoMetadataTests
{
    private const int FeatureMine = 2;

    private const int FeatureRandom = 4;

    private const int FeatureLongByLnMode = 8;

    private const int FeatureChargeNote = 16;

    private const int FeatureStop = 64;

    private const int FeatureScroll = 128;

    [TestMethod]
    public void EnsureChartInfoSchema_CreatesTableIndexesAndVersionWithoutAlteringSongTable()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            string songTableSqlBefore;
            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songTableSqlBefore = songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'song';");
            }

            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(songTableSqlBefore, verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_md5';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_charthash';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_parser_version';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'chart_info_schema' AND version = 1;"));
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "sha256",
                    "md5",
                    "charthash",
                    "level",
                    "difficulty",
                    "mainbpm",
                    "maxbpm",
                    "minbpm",
                    "length",
                    "mode",
                    "judge",
                    "feature",
                    "notes",
                    "n",
                    "ln",
                    "s",
                    "ls",
                    "total",
                    "total_defined",
                    "density",
                    "peakdensity",
                    "enddensity",
                    "distribution",
                    "speedchange",
                    "speedchange_count",
                    "lanenotes",
                    "parser_version",
                    "updated_at"
                },
                verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info);").Select((ColumnNameRow row) => row.name).ToArray());
            Assert.IsTrue(gateway.IsChartInfoSchemaCurrent());
        });
    }

    [TestMethod]
    public void DeleteSongsAndMaintenance_LeavesChartInfoRows()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            TestableBmsFile file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "Songs", "delete.bms")
            };
            file.SetHash(new string('a', 32));
            file.SetSha256(new string('b', 64));

            using (LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new BMSFileMaintenanceInfo { path = file.path, hash = file.hash }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(CreateChartInfoRow(file.sha256, file.hash, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = file.hash,
                    sha256 = file.sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            new BmsLibraryDbGateway(songDbPath).DeleteSongsAndMaintenance(new[] { file });

            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + file.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = '" + file.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void ParseBms_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "simple.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE Chart Info Test\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 3\r\n"
                    + "#RANK 3\r\n"
                    + "#TOTAL 300\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);
            LR2SongDBExtended.chart_info bytesRow = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, digest.hash, digest.sha256);

            AssertChartInfoEquivalent(row, bytesRow);
            Assert.AreEqual(digest.hash, row.md5);
            Assert.AreEqual(digest.sha256, row.sha256);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, row.parser_version);
            Assert.AreEqual(12, row.level);
            Assert.AreEqual(3, row.difficulty);
            Assert.AreEqual(5, row.mode);
            Assert.AreEqual(100, row.judge);
            Assert.AreEqual(120.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(120.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(2, row.n);
            Assert.AreEqual(1, row.ln);
            Assert.AreEqual(1, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.IsTrue(row.total_defined);
            Assert.AreEqual(300.0, row.total.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & 1) != 0);
            Assert.IsTrue((row.feature & FeatureMine) != 0);
            Assert.AreEqual(0, row.speedchange_count);
            StringAssert.StartsWith(row.distribution, "#");
            StringAssert.StartsWith(row.lanenotes, "1,1,1,");
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBmson_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "simple.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"level\":10,\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"ln_type\":2},"
                    + "\"lines\":[{\"y\":0},{\"y\":960}],"
                    + "\"bpm_events\":[{\"y\":480,\"bpm\":180}],"
                    + "\"stop_events\":[{\"y\":240,\"duration\":120}],"
                    + "\"scroll_events\":[{\"y\":720,\"rate\":0.5}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0},{\"x\":8,\"y\":240},{\"x\":2,\"y\":480,\"l\":240,\"t\":2}]}],"
                    + "\"mine_channels\":[{\"notes\":[{\"x\":3,\"y\":960,\"damage\":1.0}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);
            LR2SongDBExtended.chart_info bytesRow = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, digest.hash, digest.sha256);

            AssertChartInfoEquivalent(row, bytesRow);
            Assert.AreEqual(digest.hash, row.md5);
            Assert.AreEqual(digest.sha256, row.sha256);
            Assert.AreEqual(10, row.level);
            Assert.AreEqual(7, row.mode);
            Assert.AreEqual(100, row.judge);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(2, row.ln);
            Assert.AreEqual(1, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.IsTrue(row.total_defined);
            Assert.AreEqual(260.0, row.total.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & FeatureChargeNote) != 0);
            Assert.IsTrue((row.feature & FeatureMine) != 0);
            Assert.IsTrue((row.feature & FeatureStop) != 0);
            Assert.IsTrue((row.feature & FeatureScroll) != 0);
            Assert.AreEqual(3, row.speedchange_count);
            StringAssert.StartsWith(row.distribution, "#");
            Assert.AreEqual(24, row.lanenotes.Split(',').Length);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBms_Base62Fixture_UsesBase62ForIndexedDefinitionsAndDataTokens()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "base62.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#BASE 62\r\n"
                    + "#BPMa0 180\r\n"
                    + "#00108:a0\r\n"
                    + "#00111:a0\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.speedchange_count);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBms_RandomFixture_UsesStableSelectedBranchOne()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 2\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 1\r\n"
                    + "#00112:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(0, row.s);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_MalformedKnownCommandsAreNonFatalAndTotalUndefined()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "malformed.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#BPMzz nope\r\n"
                    + "#STOPzz nope\r\n"
                    + "#TOTAL nope\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsFalse(row.total_defined);
            Assert.IsTrue(row.total.GetValueOrDefault() > 0.0);
        });
    }

    [TestMethod]
    public void ParseBms_MissingInitialBpmIsFatal()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-bpm.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n", Encoding.ASCII);

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
        });
    }

    [TestMethod]
    public void ParseBmson_UnknownFieldsAreIgnoredAndUnsupportedModeFallsBackToBeat7()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "unknown.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"unknown_root\":1,"
                    + "\"info\":{\"level\":3,\"mode_hint\":\"unsupported-mode\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"unknown_info\":2},"
                    + "\"sound_channels\":[{\"unknown_channel\":3,\"notes\":[{\"x\":1,\"y\":0,\"unknown_note\":4}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(7, row.mode);
            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    [TestMethod]
    public void ParseBms_LongNoteChartHashUsesBeatorajaNumericLongNoteMarker()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "long-charthash.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#00151:0101\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual("c903e9b613077c6540374617cd9f5fd1916bcc77ba8e3e8770d30bea10b30319", row.charthash);
            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.ln);
        });
    }

    [TestMethod]
    public void ParseBmson_LongNoteAudioDurationAffectsChartHash()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duration-a.bmson");
            string secondPath = Path.Combine(tempRootPath, "duration-b.bmson");
            File.WriteAllText(firstPath, CreateBmsonLongNoteWithContinuation(240), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(secondPath, CreateBmsonLongNoteWithContinuation(360), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info first = ChartInfoParser.Parse(firstPath);
            LR2SongDBExtended.chart_info second = ChartInfoParser.Parse(secondPath);

            Assert.AreEqual(first.notes, second.notes);
            Assert.AreEqual(first.ln, second.ln);
            Assert.AreNotEqual(first.charthash, second.charthash);
        });
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealBeatorajaCompatibilitySample_ReducesKnownDiffs()
    {
        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_real expected.db fixture is missing.");

        using SQLiteConnection connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1000, rows.Count);
        CompatibilityDiffCounts diffs = new CompatibilityDiffCounts();
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing real chart fixture: " + expected.fixture_path);

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
            LR2SongDBExtended.chart_info fromBytes = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);
            AssertChartInfoEquivalent(actual, fromBytes);
            diffs.Add(expected, actual);
        }

        Assert.AreEqual(0, diffs.CoreDiffs, diffs.ToString());
        Assert.IsTrue(diffs.DensityDiffs <= 100, diffs.ToString());
        Assert.IsTrue(diffs.PeakDensityDiffs <= 85, diffs.ToString());
        Assert.IsTrue(diffs.EndDensityDiffs <= 85, diffs.ToString());
        Assert.IsTrue(diffs.DistributionDiffs <= 400, diffs.ToString());
        Assert.IsTrue(diffs.SpeedChangeDiffs <= 180, diffs.ToString());
        Assert.IsTrue(diffs.LengthDiffs <= 130, diffs.ToString());
        Assert.IsTrue(diffs.ChartHashDiffs <= 700, diffs.ToString());
        Assert.IsTrue(diffs.BpmIntegerDiffs <= 1, diffs.ToString());
    }

    [TestMethod]
    public void BackfillChartInfos_ParsesMissingRowsSkipsCurrentRowsAndReparsesStaleRows()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService();
            List<Tuple<int, int, string>> progress = new List<Tuple<int, int, string>>();

            ChartInfoBackfillResult first = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                (total, processed, currentPath) => progress.Add(Tuple.Create(total, processed, currentPath)));

            Assert.AreEqual(1, first.TargetCount);
            Assert.AreEqual(1, first.ProcessedCount);
            Assert.AreEqual(1, first.BackfilledCount);
            Assert.AreEqual(0, first.FailedCount);
            Assert.IsNotNull(file.ChartInfo);
            Assert.AreEqual(file.sha256, file.ChartInfo.sha256);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, file.sha256));
            Assert.AreEqual(1, progress.Last().Item1);
            Assert.AreEqual(1, progress.Last().Item2);

            ChartInfoBackfillResult second = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(0, second.TargetCount);
            Assert.AreEqual(0, second.BackfilledCount);

            gateway.UpsertChartInfos(new[] { CreateChartInfoRow(file.sha256, file.hash, parserVersion: 0) });
            ChartInfoBackfillResult third = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, third.TargetCount);
            Assert.AreEqual(1, third.BackfilledCount);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadsOnceAndPersistsDigestAndInfoForMissingSha256()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "single-read.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            Dictionary<string, int> readCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ChartInfoBuildService service = new ChartInfoBuildService(delegate(string path)
            {
                readCounts[path] = readCounts.TryGetValue(path, out int count) ? count + 1 : 1;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            List<string> logs = new List<string>();
            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ProcessedCount);
            Assert.AreEqual(1, result.DigestTargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.AreEqual(1, readCounts[chartPath]);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsNotNull(file.ChartInfo);
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_GroupsDuplicateMissingSha256TargetsByMd5()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string chartBPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            string text = "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n";
            File.WriteAllText(chartAPath, text, Encoding.ASCII);
            File.WriteAllText(chartBPath, text, Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartAPath);
            TestableBmsFile fileA = new TestableBmsFile { path = chartAPath };
            TestableBmsFile fileB = new TestableBmsFile { path = chartBPath };
            fileA.SetHash(digest.hash);
            fileB.SetHash(digest.hash);
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            int readCount = 0;
            ChartInfoBuildService service = new ChartInfoBuildService(delegate(string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { fileA, fileB },
                Array.Empty<LR2SongDBExtended.bmson_song>());

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(2, result.DigestTargetCount);
            Assert.AreEqual(2, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, readCount);
            Assert.AreEqual(fileA.sha256, fileB.sha256);
            Assert.IsNotNull(fileA.ChartInfo);
            Assert.AreSame(fileA.ChartInfo, fileB.ChartInfo);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            TestableBmsFile file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(new string('a', 32));
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.DigestFailedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsTrue(logs.Count >= 2);
            StringAssert.StartsWith(logs[0], "WARN chart_info_backfill parse_failed");
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using LR2SongDBExtended verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadFailureLogsWarnImmediately()
    {
        WithTemporarySongDb(delegate(string tempRootPath, string songDbPath)
        {
            TestableBmsFile file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "missing-file.bms")
            };
            file.SetHash(new string('a', 32));
            BmsLibraryDbGateway gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            ChartInfoBuildService service = new ChartInfoBuildService(delegate
            {
                throw new IOException("read boom");
            }, workerCountOverride: 1);
            List<string> logs = new List<string>();

            ChartInfoBackfillResult result = service.BackfillChartInfos(
                gateway,
                new[] { file },
                Array.Empty<LR2SongDBExtended.bmson_song>(),
                null,
                (string message) => logs.Add("INFO " + message),
                (string message) => logs.Add("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ReadFailedCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.IsTrue(logs.Count >= 2);
            StringAssert.StartsWith(logs[0], "WARN chart_info_backfill read_failed");
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
        });
    }

    private static long CountChartInfoRows(string songDbPath, string sha256)
    {
        using LR2SongDBExtended songDb = new LR2SongDBExtended(songDbPath);
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + sha256 + "';");
    }

    private static void AssertChartInfoEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.feature, actual.feature);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.n, actual.n);
        Assert.AreEqual(expected.ln, actual.ln);
        Assert.AreEqual(expected.s, actual.s);
        Assert.AreEqual(expected.ls, actual.ls);
        Assert.AreEqual(expected.total, actual.total);
        Assert.AreEqual(expected.total_defined, actual.total_defined);
        Assert.AreEqual(expected.density, actual.density);
        Assert.AreEqual(expected.peakdensity, actual.peakdensity);
        Assert.AreEqual(expected.enddensity, actual.enddensity);
        Assert.AreEqual(expected.distribution, actual.distribution);
        Assert.AreEqual(expected.speedchange, actual.speedchange);
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.lanenotes, actual.lanenotes);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
    }

    private static LR2SongDBExtended.chart_info CreateChartInfoRow(string sha256, string md5, int parserVersion)
    {
        return new LR2SongDBExtended.chart_info
        {
            sha256 = sha256,
            md5 = md5,
            charthash = new string('c', 64),
            level = 1,
            difficulty = 1,
            mainbpm = 120.0,
            maxbpm = 120.0,
            minbpm = 120.0,
            length = 0,
            mode = 7,
            judge = 100,
            feature = FeatureLongByLnMode,
            notes = 1,
            n = 1,
            ln = 0,
            s = 0,
            ls = 0,
            total = 260.0,
            total_defined = false,
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0",
            parser_version = parserVersion,
            updated_at = DateTime.UtcNow
        };
    }

    private static string CreateBmsonLongNoteWithContinuation(int continuationY)
    {
        return "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"level\":1,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240,\"ln_type\":2},"
            + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0,\"l\":480},{\"x\":0,\"y\":" + continuationY.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"c\":true}]}]"
            + "}";
    }

    private static void WithTemporarySongDb(Action<string, string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, Array.Empty<byte>());
        try
        {
            testAction(tempRootPath, songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }

        public void SetSha256(string value)
        {
            ApplySha256(value);
        }
    }

    private sealed class RealChartInfoExpectedRow
    {
        public int fixture_id { get; set; }

        public string fixture_path { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string charthash { get; set; } = string.Empty;

        public int? level { get; set; }

        public int? difficulty { get; set; }

        public double? mainbpm { get; set; }

        public double? maxbpm { get; set; }

        public double? minbpm { get; set; }

        public int? length { get; set; }

        public int mode { get; set; }

        public int judge { get; set; }

        public int feature { get; set; }

        public int notes { get; set; }

        public int n { get; set; }

        public int ln { get; set; }

        public int s { get; set; }

        public int ls { get; set; }

        public double? total { get; set; }

        public double? density { get; set; }

        public double? peakdensity { get; set; }

        public double? enddensity { get; set; }

        public string distribution { get; set; } = string.Empty;

        public string speedchange { get; set; } = string.Empty;

        public int speedchange_count { get; set; }

        public string lanenotes { get; set; } = string.Empty;
    }

    private sealed class CompatibilityDiffCounts
    {
        public int CoreDiffs { get; private set; }

        public int ChartHashDiffs { get; private set; }

        public int LengthDiffs { get; private set; }

        public int DensityDiffs { get; private set; }

        public int PeakDensityDiffs { get; private set; }

        public int EndDensityDiffs { get; private set; }

        public int DistributionDiffs { get; private set; }

        public int SpeedChangeDiffs { get; private set; }

        public int BpmIntegerDiffs { get; private set; }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            CoreDiffs += CountCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
            }
            if (!string.Equals(expected.speedchange, actual.speedchange, StringComparison.Ordinal))
            {
                SpeedChangeDiffs++;
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
            }
        }

        public override string ToString()
        {
            return "chart_info real compatibility diffs: "
                + "core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " distribution=" + DistributionDiffs
                + " speedchange=" + SpeedChangeDiffs
                + " bpmInteger=" + BpmIntegerDiffs;
        }

        private static int CountCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
        {
            int count = 0;
            count += expected.notes == actual.notes ? 0 : 1;
            count += expected.n == actual.n ? 0 : 1;
            count += expected.ln == actual.ln ? 0 : 1;
            count += expected.s == actual.s ? 0 : 1;
            count += expected.ls == actual.ls ? 0 : 1;
            count += expected.judge == actual.judge ? 0 : 1;
            count += expected.feature == actual.feature ? 0 : 1;
            count += expected.mode == actual.mode ? 0 : 1;
            count += NullableDoubleEquals(expected.mainbpm, actual.mainbpm) ? 0 : 1;
            count += string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal) ? 0 : 1;
            count += NullableDoubleEquals(expected.total, actual.total) ? 0 : 1;
            return count;
        }

        private static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }
    }
}
