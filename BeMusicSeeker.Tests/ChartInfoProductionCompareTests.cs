using System;
using System.IO;
using System.Linq;
using System.Text;
using ChartInfoCompareTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartInfoProductionCompareTests
{
    [TestMethod]
    public void Compare_ReportsMissingExtraFieldDiffsAndIgnoresRandomValueDiffs()
    {
        WithProductionCompareFixture(delegate(ProductionCompareFixture fixture)
        {
            ChartInfoCompareResult result = ChartInfoCompareRunner.Compare(new ChartInfoCompareOptions
            {
                AppSongDbPath = fixture.AppDbPath,
                BeatorajaSongDbPath = fixture.SongDbPath,
                BeatorajaInfoDbPath = fixture.InfoDbPath,
                OutputDirectory = fixture.ReportPath
            });

            Assert.AreEqual(7, result.ReferenceCount);
            Assert.AreEqual(5, result.MatchedChartInfoCount);
            Assert.AreEqual(2, result.MissingChartInfoCount);
            Assert.AreEqual(1, result.MissingNonRandomCount);
            Assert.AreEqual(1, result.MissingRandomCount);
            Assert.AreEqual(1, result.ExtraChartInfoCount);
            Assert.AreEqual(1, result.RandomValueDiffCount);
            Assert.AreEqual(2, result.NonRandomDiffChartCount);
            Assert.AreEqual(1, result.FieldDiffs.Single((FieldDiffSummary diff) => diff.Field == "charthash").Count);
            Assert.AreEqual(1, result.FieldDiffs.Single((FieldDiffSummary diff) => diff.Field == "difficulty").Count);
            Assert.IsFalse(result.FieldDiffs.Any((FieldDiffSummary diff) => diff.Field == "md5"));
            Assert.IsFalse(result.FieldDiffs.Any((FieldDiffSummary diff) => diff.Field == "level"));
            Assert.IsFalse(result.FieldDiffs.Any((FieldDiffSummary diff) => diff.Field == "maxbpm"));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ReportPath, "summary.json")));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ReportPath, "field_diffs.csv")));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ReportPath, "missing_chart_info.csv")));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ReportPath, "extra_chart_info.csv")));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.ReportPath, "random_value_diff_samples.csv")));
        });
    }

    [TestMethod]
    public void Compare_ExportsFixtureWhenNonRandomDiffCountIsWithinLimit()
    {
        WithProductionCompareFixture(delegate(ProductionCompareFixture fixture)
        {
            string exportPath = Path.Combine(fixture.RootPath, "export");

            ChartInfoCompareResult result = ChartInfoCompareRunner.Compare(new ChartInfoCompareOptions
            {
                AppSongDbPath = fixture.AppDbPath,
                BeatorajaSongDbPath = fixture.SongDbPath,
                BeatorajaInfoDbPath = fixture.InfoDbPath,
                OutputDirectory = null,
                FixtureOutputDirectory = exportPath,
                MaxFixtureCount = 10,
                OverwriteFixture = true
            });

            Assert.AreEqual(2, result.NonRandomDiffChartCount);
            Assert.AreEqual(2, result.FixtureExportedCount);
            Assert.IsTrue(File.Exists(Path.Combine(exportPath, "manifest.json")));
            Assert.IsTrue(File.Exists(Path.Combine(exportPath, "expected.db")));
            using SQLiteConnection expected = new SQLiteConnection(Path.Combine(exportPath, "expected.db"));
            Assert.AreEqual(2L, expected.ExecuteScalar<long>("SELECT COUNT(1) FROM FixtureSampleChart;"));
            Assert.AreEqual(2L, expected.ExecuteScalar<long>("SELECT COUNT(1) FROM FixtureExpectedChartInfo;"));
            string[] copiedCharts = Directory.GetFiles(Path.Combine(exportPath, "charts"), "*", SearchOption.AllDirectories);
            Assert.AreEqual(2, copiedCharts.Length);
        });
    }

    [TestMethod]
    public void Compare_SkipsFixtureExportWhenDiffCountExceedsLimit()
    {
        WithProductionCompareFixture(delegate(ProductionCompareFixture fixture)
        {
            string exportPath = Path.Combine(fixture.RootPath, "export");

            ChartInfoCompareResult result = ChartInfoCompareRunner.Compare(new ChartInfoCompareOptions
            {
                AppSongDbPath = fixture.AppDbPath,
                BeatorajaSongDbPath = fixture.SongDbPath,
                BeatorajaInfoDbPath = fixture.InfoDbPath,
                OutputDirectory = null,
                FixtureOutputDirectory = exportPath,
                MaxFixtureCount = 1
            });

            Assert.AreEqual(2, result.NonRandomDiffChartCount);
            Assert.AreEqual("non_random_diff_count_exceeds_limit", result.FixtureExportSkippedReason);
            Assert.IsFalse(Directory.Exists(exportPath));
        });
    }

    private static void WithProductionCompareFixture(Action<ProductionCompareFixture> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoCompareTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ProductionCompareFixture fixture = new ProductionCompareFixture(root);
            fixture.Create();
            action(fixture);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ProductionCompareFixture
    {
        public ProductionCompareFixture(string rootPath)
        {
            RootPath = rootPath;
            AppDbPath = Path.Combine(rootPath, "song.db");
            SongDbPath = Path.Combine(rootPath, "songdata.db");
            InfoDbPath = Path.Combine(rootPath, "songinfo.db");
            ChartRootPath = Path.Combine(rootPath, "charts");
            ReportPath = Path.Combine(rootPath, "reports");
        }

        public string RootPath { get; }

        public string AppDbPath { get; }

        public string SongDbPath { get; }

        public string InfoDbPath { get; }

        public string ChartRootPath { get; }

        public string ReportPath { get; }

        public void Create()
        {
            Directory.CreateDirectory(ChartRootPath);
            using (SQLiteConnection app = new SQLiteConnection(AppDbPath))
            using (SQLiteConnection song = new SQLiteConnection(SongDbPath))
            using (SQLiteConnection info = new SQLiteConnection(InfoDbPath))
            {
                app.Execute("CREATE TABLE chart_info (sha256 TEXT PRIMARY KEY, md5 TEXT, charthash TEXT, level INTEGER, difficulty INTEGER, maxbpm REAL, minbpm REAL, length INTEGER, mode INTEGER, judge INTEGER, feature INTEGER, notes INTEGER, n INTEGER, ln INTEGER, s INTEGER, ls INTEGER, total REAL, density REAL, peakdensity REAL, enddensity REAL, mainbpm REAL, distribution TEXT, speedchange TEXT, lanenotes TEXT);");
                song.Execute("CREATE TABLE song (sha256 TEXT PRIMARY KEY, md5 TEXT, path TEXT, title TEXT, subtitle TEXT, charthash TEXT, level INTEGER, difficulty INTEGER, maxbpm INTEGER, minbpm INTEGER, length INTEGER, mode INTEGER, judge INTEGER, feature INTEGER, notes INTEGER);");
                info.Execute("CREATE TABLE information (sha256 TEXT PRIMARY KEY, n INTEGER, ln INTEGER, s INTEGER, ls INTEGER, total REAL, density REAL, peakdensity REAL, enddensity REAL, mainbpm REAL, distribution TEXT, speedchange TEXT, lanenotes TEXT);");

                InsertReference(song, info, Sha('a'), "exact.bms", "hash-a", level: 12, difficulty: 3, feature: 0);
                InsertChartInfo(app, Sha('a'), "hash-a", level: 12, difficulty: 3, feature: 0);

                InsertReference(song, info, Sha('b'), "level-null.bms", "hash-b", level: 0, difficulty: 2, feature: 0);
                InsertChartInfo(app, Sha('b'), "hash-b", level: null, difficulty: 2, feature: 0, maxbpm: 180.9);

                InsertReference(song, info, Sha('c'), "value-diff.bms", "hash-c", level: 7, difficulty: 4, feature: 0);
                InsertChartInfo(app, Sha('c'), "wrong-c", level: 7, difficulty: 3, feature: 0);

                InsertReference(song, info, Sha('d'), "missing-non-random.bms", "hash-d", level: 5, difficulty: 2, feature: 0);

                InsertReference(song, info, Sha('e'), "random-diff.bms", "hash-e", level: 5, difficulty: 2, feature: 4);
                InsertChartInfo(app, Sha('e'), "wrong-e", level: 5, difficulty: 9, feature: 4);

                InsertReference(song, info, Sha('f'), "missing-random.bms", "hash-f", level: 5, difficulty: 2, feature: 4);

                InsertChartInfo(app, Sha('g'), "extra-g", level: 1, difficulty: 1, feature: 0);

                InsertReference(song, info, Sha('h'), "bmson-md5-is-ignored.bmson", "hash-h", level: 3, difficulty: 1, feature: 0);
                song.Execute("UPDATE song SET md5 = '' WHERE sha256 = ?;", Sha('h'));
                InsertChartInfo(app, Sha('h'), "hash-h", level: 3, difficulty: 1, feature: 0);
            }
        }

        private void InsertReference(SQLiteConnection song, SQLiteConnection info, string sha256, string fileName, string charthash, int level, int difficulty, int feature)
        {
            string path = Path.Combine(ChartRootPath, fileName);
            File.WriteAllText(path, "#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            song.Execute(
                "INSERT INTO song (sha256, md5, path, title, subtitle, charthash, level, difficulty, maxbpm, minbpm, length, mode, judge, feature, notes) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                sha256,
                "md5" + sha256.Substring(0, 29),
                path,
                "title",
                "",
                charthash,
                level,
                difficulty,
                180,
                120,
                1000,
                7,
                100,
                feature,
                1);
            info.Execute(
                "INSERT INTO information (sha256, n, ln, s, ls, total, density, peakdensity, enddensity, mainbpm, distribution, speedchange, lanenotes) VALUES (?, 1, 0, 0, 0, 260.0, 1.0, 1.0, 0.0, 120.0, '#', '120.0,0.0', '1,0,0');",
                sha256);
        }

        private static void InsertChartInfo(SQLiteConnection app, string sha256, string charthash, int? level, int difficulty, int feature, double maxbpm = 180.0)
        {
            app.Execute(
                "INSERT INTO chart_info (sha256, md5, charthash, level, difficulty, maxbpm, minbpm, length, mode, judge, feature, notes, n, ln, s, ls, total, density, peakdensity, enddensity, mainbpm, distribution, speedchange, lanenotes) VALUES (?, ?, ?, ?, ?, ?, 120.0, 1000, 7, 100, ?, 1, 1, 0, 0, 0, 260.0, 1.0, 1.0, 0.0, 120.0, '#', '120.0,0.0', '1,0,0');",
                sha256,
                "md5" + sha256.Substring(0, 29),
                charthash,
                level,
                difficulty,
                maxbpm,
                feature);
        }

        private static string Sha(char c)
        {
            return new string(c, 64);
        }
    }
}
