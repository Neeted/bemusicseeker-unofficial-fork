using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// BMS・bmsonの詳細解析と外部参照値に対する互換性を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoParserBehaviorTests
{

    /// <summary>実BMS千件を独立beatoraja期待へ照合し、核となる詳細・ハッシュ・整数BPMの差分と既存許容幅を超える分布・密度・長さの退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。期待または解析結果にRandomがある行は値比較から除外する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealBeatorajaCompatibilitySample_ReducesKnownDiffs()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the full BMS chart_info parser compatibility fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_real expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1000, rows.Count);
        var diffs = new CompatibilityDiffCounts();
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing real chart fixture: " + expected.fixture_path);

            ChartInfoParser.ChartInfoParseResult fromBytesResult;
            try
            {
                fromBytesResult = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256, timeout: TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                Assert.Fail("Production diff fixture parse failed: fixture_id="
                    + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " exception=" + ex.GetType().Name
                    + " message=" + ex.Message);
                throw;
            }
            diffs.Add(expected, fromBytesResult.Row, (fromBytesResult.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")));
        }

        Assert.AreEqual(0, diffs.CoreDiffs, diffs.ToString());
        Assert.IsTrue(diffs.DensityDiffs <= 100, diffs.ToString());
        Assert.IsTrue(diffs.PeakDensityDiffs <= 85, diffs.ToString());
        Assert.IsTrue(diffs.EndDensityDiffs <= 85, diffs.ToString());
        Assert.IsTrue(diffs.DistributionDiffs <= 400, diffs.ToString());
        Assert.IsTrue(diffs.SpeedChangeDiffs <= 180, diffs.ToString());
        Assert.IsTrue(diffs.LengthDiffs <= 130, diffs.ToString());
        Assert.AreEqual(0, diffs.ChartHashDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.BpmIntegerDiffs, diffs.ToString());
    }

    /// <summary>重複JSONキーを持つ二つの実bmsonを参照DBへ照合し、キー採用順によるハッシュ・音符・長さの互換違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseRealBmsonDuplicateKeyFixtures_MatchBeatorajaHash()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_bmson_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_bmson_real expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "WHERE sc.sha256 IN ('296314aeb18ba9c44eda264784711861df4fd9a91a9f82a133911e1e5b926749','b7e399df46bc7f800c91c4d81002b806f32b6da70314c47bcc3064467d21e6b1') "
                + "ORDER BY sc.fixture_id;");
        Assert.AreEqual(2, rows.Count);

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
        }
    }

    /// <summary>実bmson千三十四件の比較対象の詳細・ハッシュ・長さ・密度・TOTALを参照DBへ照合し、互換正規化と許容誤差内の一致を維持する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。levelの期待0と解析結果nullを同等とし、min/max BPMは整数化、浮動小数値は1e-6以内の差を許容する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealBmsonBeatorajaCompatibility_AllFixturesMatch()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the full BMSON chart_info parser compatibility fixture");

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_bmson_real");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_bmson_real expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(1034, rows.Count);
        var diffs = new BmsonCompatibilityDiffCounts();
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing bmson chart fixture: " + expected.fixture_path);

            BeMusicSeeker.Models.ChartDetails actual;
            ChartInfoParser.ChartInfoParseResult fromBytesResult;
            try
            {
                fromBytesResult = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);
            }
            catch (Exception ex)
            {
                Assert.Fail("Production diff fixture parse failed: fixture_id="
                    + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " exception=" + ex.GetType().Name
                    + " message=" + ex.Message);
                throw;
            }
            actual = fromBytesResult.Row;
            diffs.Add(expected, actual, (fromBytesResult.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")));
        }

        Assert.AreEqual(0, diffs.CoreDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.ChartHashDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.LengthDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.DistributionDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.BpmIntegerDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.DensityDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.PeakDensityDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.EndDensityDiffs, diffs.ToString());
        Assert.AreEqual(0, diffs.MainBpmDiffs, diffs.ToString());
    }

    /// <summary>既知の遅い四件を分離した実BMS七百五十五件を期限内に参照DBへ照合し、新規timeout・解析失敗・保存値の差分を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。期待または解析結果にRandomがある行は値比較から除外する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_AllNonTimeoutRowsMatchBeatoraja()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("本番diff全件の互換検査にはBMS_TEST_PRODUCTION_DIFF_FULL=1の明示指定が必要。");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");

        Assert.AreEqual(759, rows.Count);
        List<RealChartInfoExpectedRow> rowsToVerify = [.. rows.Where(row => !ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))];
        List<RealChartInfoExpectedRow> knownTimeoutRows = [.. rows.Where(row => ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))];
        Assert.AreEqual(755, rowsToVerify.Count);
        Assert.AreEqual(4, knownTimeoutRows.Count);

        var diffs = new CompatibilityDiffCounts();
        var parserTimeout = TimeSpan.FromSeconds(ReadPositiveIntEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_TIMEOUT_SECONDS", 30));
        var totalStopwatch = Stopwatch.StartNew();
        Trace.WriteLine("chart_info production diff non-timeout start total=" + rowsToVerify.Count
            + " excludedKnownTimeout=" + knownTimeoutRows.Count
            + " timeoutSeconds=" + parserTimeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
            + " reportDir=" + CompatibilityDiffCounts.GetReportDirectory());

        int processed = 0;
        foreach (RealChartInfoExpectedRow expected in rowsToVerify)
        {
            processed++;
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(chartPath), "Missing production diff chart fixture: " + expected.fixture_path);

            var parseStopwatch = Stopwatch.StartNew();
            try
            {
                ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
                    File.ReadAllBytes(chartPath),
                    chartPath,
                    expected.md5,
                    expected.sha256,
                    timeout: parserTimeout);
                parseStopwatch.Stop();
                diffs.AddParsed(expected, result.Row, (result.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")), parseStopwatch.ElapsedMilliseconds);
            }
            catch (ChartInfoParser.ChartInfoParseTimeoutException ex)
            {
                parseStopwatch.Stop();
                diffs.AddTimeout(expected, parseStopwatch.ElapsedMilliseconds, ex);
            }
            catch (Exception ex)
            {
                parseStopwatch.Stop();
                diffs.AddParseFailure(expected, parseStopwatch.ElapsedMilliseconds, ex);
            }

            if (processed % 50 == 0 || processed == rowsToVerify.Count)
            {
                Trace.WriteLine("chart_info production diff progress processed=" + processed
                    + " parsed=" + diffs.ParsedCount
                    + " timeout=" + diffs.TimeoutCount
                    + " failed=" + diffs.ParseFailureCount
                    + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
            }
        }

        totalStopwatch.Stop();
        string reportPath = diffs.WriteReport("production_diff_non_timeout");
        Trace.WriteLine("chart_info production diff non-timeout done elapsedMs=" + totalStopwatch.ElapsedMilliseconds + " report=" + reportPath + " " + diffs);
        Assert.AreEqual(rowsToVerify.Count, diffs.ParsedCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TimeoutCount, diffs.ToString());
        Assert.AreEqual(0, diffs.ParseFailureCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
    }

    /// <summary>通常群から分離した既知の遅い四件を明示実行時の期限内で解析し、解析完了と参照値一致の退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。期待または解析結果にRandomがある行は値比較から除外する。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilitySlow")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_KnownTimeoutRows_PerformanceAndExpectedValues()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_CHART_INFO_SLOW"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("遅いchart_info fixtureの検査にはBMS_TEST_CHART_INFO_SLOW=1の明示指定が必要。");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = [.. connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;")
            .Where(row => ProductionDiffKnownTimeoutSha256s.Contains(row.sha256))];

        Assert.AreEqual(4, rows.Count);
        var diffs = new CompatibilityDiffCounts();
        var parserTimeout = TimeSpan.FromSeconds(ReadPositiveIntEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_SLOW_TIMEOUT_SECONDS", 60));

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));
            var parseStopwatch = Stopwatch.StartNew();
            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
                File.ReadAllBytes(chartPath),
                chartPath,
                expected.md5,
                expected.sha256,
                timeout: parserTimeout);
            parseStopwatch.Stop();
            diffs.AddParsed(expected, result.Row, (result.ChartString ?? throw new AssertFailedException("詳細解析のChartStringがありません。")), parseStopwatch.ElapsedMilliseconds);
        }

        string reportPath = diffs.WriteReport("production_diff_known_timeout");
        Trace.WriteLine("chart_info production diff known-timeout done report=" + reportPath + " " + diffs);
        Assert.AreEqual(rows.Count, diffs.ParsedCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
    }

    /// <summary>実STOP overflow入力の長さ・音符・featureを参照DBへ照合し、Java整数wrapの互換を合成入力だけでは見落とす誤りを検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_JavaIntWrappedStopLengthMatchesBeatoraja()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        const string sha256 = "2125eeb135073c7d1f20968bef763ac1dc6290fd836fcc29309c5d351b60667e";
        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "WHERE sc.sha256 = ?;",
            sha256).Single();
        string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

        ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
            File.ReadAllBytes(chartPath),
            chartPath,
            expected.md5,
            expected.sha256,
            timeout: TimeSpan.FromSeconds(10));

        Assert.AreEqual(expected.length, result.Row.length);
        Assert.AreEqual(expected.notes, result.Row.notes);
        Assert.AreEqual(expected.feature, result.Row.feature);
    }

    /// <summary>衝突を含む四つの実譜面を参照DBへ照合し、通常・LN・scratch各計数とレーン別保存値の互換違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_NoteCollisionCountsMatchBeatorajaSamples()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        // 個々の外部入力はSHA-256と参照DBのfixture_pathで特定する。
        string[] sha256s =
        [
            "4cf26b3ba8d762de8db62eec0b7790a37da600b303aecffa6391018d83680266",
            "dfc23c232b435b8abcfc9363a15400b4115a7bec0d66b0d405e6e6cdfcf224e2",
            "418806ce0bcd1eecc2256b022d1aad8c9616f7c21389eab61e280952e0f68558",
            "0f9297f384c02a4060f31962e768dc6a34d83aa551fbc5a01c19a6b7c6c40b77"
        ];

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        foreach (string sha256 in sha256s)
        {
            RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
                "SELECT sc.fixture_id, sc.fixture_path, e.* "
                    + "FROM FixtureSampleChart sc "
                    + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                    + "WHERE sc.sha256 = ?;",
                sha256).Single();
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.n, actual.n, expected.sha256);
            Assert.AreEqual(expected.ln, actual.ln, expected.sha256);
            Assert.AreEqual(expected.s, actual.s, expected.sha256);
            Assert.AreEqual(expected.ls, actual.ls, expected.sha256);
            Assert.AreEqual(expected.lanenotes, actual.lanenotes, expected.sha256);
        }
    }

    /// <summary>無効な省略RANDOMを含む実譜面で参照hashとfeatureを保ち、地雷は残してrandomを誤設定する分類違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_InvalidCompactRandomDoesNotSetRandomFeature()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        const string sha256 = "e570cff02a4a43809229060f3b0d146efc2635060786a0f9a4f9158bf5265b9d";
        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "WHERE sc.sha256 = ?;",
            sha256).Single();
        string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

        ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath, expected.md5, expected.sha256);

        Assert.AreEqual(expected.charthash, result.Row.charthash);
        Assert.AreEqual(expected.feature, result.Row.feature);
        Assert.AreEqual(0, result.Row.feature & FeatureRandom);
        Assert.IsTrue((result.Row.feature & FeatureMine) != 0);
    }

    /// <summary>報告された七つの実差分入力をJDK21参照へ照合し、ハッシュ・長さ・分布・速度・密度の再発を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionLatestDiffFixture_MatchesJdk21ReferenceForReportedFields()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("最新の本番diff全件の互換検査にはBMS_TEST_PRODUCTION_DIFF_FULL=1の明示指定が必要。");
        }

        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_latest_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_latest_diff expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM FixtureSampleChart sc "
                + "JOIN FixtureExpectedChartInfo e ON e.sha256 = sc.sha256 "
                + "ORDER BY sc.fixture_id;");
        Assert.AreEqual(7, rows.Count);

        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(
                File.ReadAllBytes(chartPath),
                chartPath,
                expected.md5,
                expected.sha256,
                timeout: TimeSpan.FromSeconds(60));

            BeMusicSeeker.Models.ChartDetails actual = result.Row;
            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.speedchange, actual.speedchange, expected.sha256);
            AssertNullableDouble(expected.density, actual.density, expected.sha256 + " density");
            AssertNullableDouble(expected.peakdensity, actual.peakdensity, expected.sha256 + " peakdensity");
            AssertNullableDouble(expected.enddensity, actual.enddensity, expected.sha256 + " enddensity");
        }
    }

    /// <summary>timeoutと解析失敗を成功差分とは別に数え、後から成功解析した行だけのハッシュ差を集計して失敗行の混入を検出する。</summary>
    [TestMethod]
    public void CompatibilityDiffCounts_RecordsSkippedProductionDiffRowsWithoutCountingDiffs()
    {
        RealChartInfoExpectedRow expected = CreateExpectedCompatibilityRow(new string('a', 64), new string('c', 64));
        BeMusicSeeker.Models.ChartDetails actual = CreateChartInfoRow(expected.sha256, expected.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
        actual = actual with { charthash = new string('d', 64) };

        var diffs = new CompatibilityDiffCounts();
        diffs.AddTimeout(expected, 10001, new ChartInfoParser.ChartInfoParseTimeoutException(10000, "test"));
        diffs.AddParseFailure(expected, 12, new InvalidDataException("synthetic parse failure"));

        Assert.AreEqual(0, diffs.ParsedCount);
        Assert.AreEqual(1, diffs.TimeoutCount);
        Assert.AreEqual(1, diffs.ParseFailureCount);
        Assert.AreEqual(0, diffs.ChartHashDiffs);
        Assert.AreEqual(0, diffs.CoreDiffs);

        diffs.AddParsed(expected, actual, string.Empty, 30);

        Assert.AreEqual(1, diffs.ParsedCount);
        Assert.AreEqual(1, diffs.ChartHashDiffs);
        StringAssert.Contains(diffs.ToString(), "timeout=1");
        StringAssert.Contains(diffs.ToString(), "parseFailure=1");
    }

    /// <summary>時刻0の定義で初期BPMを補う実譜面を参照DBへ照合し、ハッシュ・長さ・音符・BPM統計の互換違反を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCase_InitialBpmDefinedByTimelineZeroMatchesBeatoraja()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info initial-BPM edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        RealChartInfoExpectedRow expected = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "WHERE sc.reason = 'initial_bpm_success';").Single();
        string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

        BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mainbpm.GetValueOrDefault(), actual.mainbpm.GetValueOrDefault(), 0.000001);
        Assert.AreEqual((int)(expected.minbpm ?? 0.0), (int)(actual.minbpm ?? 0.0));
        Assert.AreEqual((int)(expected.maxbpm ?? 0.0), (int)(actual.maxbpm ?? 0.0));
    }

    /// <summary>長い時間軸の二つの実入力を参照DBへ照合し、長さだけでなく分布・密度・ハッシュを変える時刻退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_LongTimelineReferenceChartsMatchBeatoraja()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the full chart_info long-timeline edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<RealChartInfoExpectedRow> rows = connection.Query<RealChartInfoExpectedRow>(
            "SELECT sc.fixture_id, sc.fixture_path, e.* "
                + "FROM sample_chart sc "
                + "JOIN expected_chart_info e ON e.sha256 = sc.sha256 "
                + "WHERE sc.reason = 'timeline_long_reference' "
                + "ORDER BY sc.fixture_id;");
        Assert.AreEqual(2, rows.Count);
        foreach (RealChartInfoExpectedRow expected in rows)
        {
            string chartPath = Path.Combine(fixtureRootPath, expected.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.density.GetValueOrDefault(), actual.density.GetValueOrDefault(), 0.000001, expected.sha256);
        }
    }

    /// <summary>overflowから再試行する四つの実random入力を解析し、例外なくrandom状態と正の音符・非負の長さを成立させる保証の退行を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_RandomOverflowFixturesDoNotOverflow()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info RANDOM-overflow edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<EdgeCaseSampleChartRow> samples = connection.Query<EdgeCaseSampleChartRow>(
            "SELECT * FROM sample_chart WHERE reason = 'overflow_retry' ORDER BY fixture_id;");
        Assert.AreEqual(4, samples.Count);
        foreach (EdgeCaseSampleChartRow sample in samples)
        {
            string chartPath = Path.Combine(fixtureRootPath, sample.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            BeMusicSeeker.Models.ChartDetails actual;
            try
            {
                actual = ChartInfoParser.Parse(chartPath, sample.md5, sample.sha256);
            }
            catch (Exception ex)
            {
                Assert.Fail(sample.sha256 + " failed with " + ex.GetType().Name + ": " + ex.Message);
                throw;
            }

            Assert.IsTrue((actual.feature & FeatureRandom) != 0, sample.sha256);
            Assert.IsTrue(actual.notes > 0, sample.sha256);
            Assert.IsTrue(actual.length.GetValueOrDefault() >= 0, sample.sha256);
        }
    }

    /// <summary>実RANDOM/ENDIFの範囲入力で参照の音符・scratch・長さを保ち、枝状態による大量の主データ脱落を検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_RandomEndIfScopeReferenceMatchesBeatorajaCounts()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info RANDOM/ENDIF reference fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string chartPath = Path.Combine(fixtureRootPath, "charts", "b862bf34bf6fbe034a18cceb9178a7e44a864a77e3475b9d478b9b5e5a46ff01.bms");
        Assert.IsTrue(File.Exists(chartPath), "random_endif_scope_reference fixture is missing.");

        BeMusicSeeker.Models.ChartDetails actual = ChartInfoParser.Parse(
            chartPath,
            "a0dfd7d70a53e4752d39e09b23877cff",
            "b862bf34bf6fbe034a18cceb9178a7e44a864a77e3475b9d478b9b5e5a46ff01");

        Assert.IsTrue((actual.feature & FeatureRandom) != 0);
        Assert.AreEqual(2295, actual.notes);
        Assert.AreEqual(2255, actual.n);
        Assert.AreEqual(40, actual.s);
        Assert.AreEqual(136083, actual.length);
    }

    /// <summary>参照でも初期BPM不足として失敗する二つの実入力を拒否し、既定BPM補完で失敗を隠す誤りを検出する。</summary>
    /// <remarks>実配布入力と独立参照DBを使う互換検査。明示実行・規模・解析期限は既存区分のまま保持する。</remarks>
    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilityFull")]
    [TestCategory("LargeFixture")]
    public void ParseRealEdgeCases_InitialBpmReferenceFatalChartsRemainFatal()
    {
        TestOptIn.RequireEnvironmentFlag(
            TestOptIn.ChartInfoFullEnvironmentVariable,
            "the chart_info fatal initial-BPM edge-case fixture");

        string fixtureRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestData", "chart_info_edge_cases");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_edge_cases expected.db fixture is missing.");

        using var connection = new SQLiteConnection(expectedDbPath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        List<EdgeCaseSampleChartRow> samples = connection.Query<EdgeCaseSampleChartRow>(
            "SELECT * FROM sample_chart WHERE reason = 'initial_bpm_fatal_reference' ORDER BY fixture_id;");
        Assert.AreEqual(2, samples.Count);
        foreach (EdgeCaseSampleChartRow sample in samples)
        {
            string chartPath = Path.Combine(fixtureRootPath, sample.fixture_path.Replace('/', Path.DirectorySeparatorChar));

            Assert.ThrowsExactly<InvalidDataException>(() => ChartInfoParser.Parse(chartPath, sample.md5, sample.sha256), sample.sha256);
        }
    }

    /// <summary>専用環境の明示実行でJava参照toolを実譜面へ接続し、成功JSONとchart hashの出力が失われる誤りを検出する。</summary>
    /// <remarks>ローカルJDKと参照リポジトリを準備する専用環境で実行し、準備不足は失敗とする。</remarks>
    [TestMethod]
    [TestCategory("CompatibilityTool")]
    [TestCategory("EnvironmentIntegration")]
    public async Task ChartStringDumpTool_BuildsAndDumpsReferenceChartString()
    {
        string repoRoot = FindRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "tools", "chartstring-dump", "run.ps1");
        string chartPath = Path.Combine(
            repoRoot,
            "BeMusicSeeker.Tests",
            "TestData",
            "chart_info_real",
            "charts",
            "00",
            "00ac147d2ad720b50087e9480708c62240e2816d74bcdcf6ec22dbe7d413f2c6.bms");
        Assert.IsTrue(File.Exists(scriptPath), "chartstring-dump run script is missing.");
        Assert.IsTrue(File.Exists(chartPath), "chartstring-dump smoke fixture is missing.");

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = "-ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + chartPath + "\"",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process? process = null;
        Task[] ownedTasks = [];
        Exception? primaryFailure = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start chartstring-dump.");
            Task exitTask = process.WaitForExitAsync();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            ownedTasks = [exitTask, stdoutTask, stderrTask];
            try
            {
                await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                Assert.Fail("chartstring-dump did not finish with both output streams within 30 seconds.");
            }
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            Assert.AreEqual(0, process.ExitCode, stdout + Environment.NewLine + stderr);
            StringAssert.Contains(stdout, "\"ok\":true");
            StringAssert.Contains(stdout, "\"charthash\":");
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
            }
            try
            {
                await Task.WhenAll(ownedTasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                cleanupFailure = cleanupFailure is null ? ex : new AggregateException(cleanupFailure, ex);
            }
            process?.Dispose();
            if (cleanupFailure is not null)
            {
                if (primaryFailure is null)
                {
                    primaryFailure = cleanupFailure;
                }
                else
                {
                    primaryFailure.Data["chartstring-dump.cleanup"] = cleanupFailure;
                }
            }
        }
        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

    }

}
