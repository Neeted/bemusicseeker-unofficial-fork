using System.Globalization;
using System.Text;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>Coreと互換suiteで同一の参照行と差分判定を共有します。</summary>
internal static class ChartInfoMetadataTestSupport
{

    internal static readonly HashSet<string> ProductionDiffKnownTimeoutSha256s = new(StringComparer.OrdinalIgnoreCase)
    {
        "273433f8e72e768603d18c986b50900538e94c2bd3e50d4487b66d5c0c3c8201",
        "a56958ab747ebb7fd4332afed2493f75a414c00be694e61182cbd3a363871a43",
        "ae3d8c2c5eb88da961df62a6e7fa6ca043b528f1a36de67643eb463b18864d6f",
        "bd496f28d4a61aba6e9315f61fda463209cd908f3b08f0c2e7e06150034e2e59"
    };

    /// <summary>専用環境に導入済みの7-Zipを取得し、準備不足を失敗として返します。</summary>
    internal static string ResolveInstalledSevenZipPath()
    {
        string[] candidates =
        [
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        ];
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new AssertFailedException("7-Zip CLI is not installed.");
    }

    internal static RealChartInfoExpectedRow CreateExpectedCompatibilityRow(string sha256, string charthash)
    {
        return new RealChartInfoExpectedRow
        {
            fixture_id = 1,
            fixture_path = "synthetic.bms",
            sha256 = sha256,
            md5 = new string('b', 32),
            charthash = charthash,
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
            density = 1.0,
            peakdensity = 1.0,
            enddensity = 0.0,
            distribution = "#",
            speedchange = "120.0,0.0",
            speedchange_count = 0,
            lanenotes = "1,0,0"
        };
    }

    internal static int ReadPositiveIntEnvironmentVariable(string name, int defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }
        return defaultValue;
    }

    internal sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }

    internal sealed class ChartInfoMetadataBundleManifestRow
    {
        public string bundle_id { get; set; } = string.Empty;

        public int format_version { get; set; }

        public string generated_at { get; set; } = string.Empty;

        public int chart_info_schema_version { get; set; }

        public int chart_info_parser_version { get; set; }

        public int chart_info_count { get; set; }

        public int chart_digest_count { get; set; }
    }

    internal sealed class RealChartInfoExpectedRow
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

    internal sealed class EdgeCaseSampleChartRow
    {
        public int fixture_id { get; set; }

        public string source_path { get; set; } = string.Empty;

        public string fixture_path { get; set; } = string.Empty;

        public string reason { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public int has_beatoraja_song { get; set; }
    }

    /// <summary>参照値との差分を元の許容差で集計し、診断を呼出元の専有領域へ出力します。</summary>
    internal sealed class BmsonCompatibilityDiffCounts
    {
        private readonly string diagnosticDirectory;

        /// <summary>診断出力先をケース所有の絶対pathで受け取ります。</summary>
        internal BmsonCompatibilityDiffCounts(string diagnosticDirectory) => this.diagnosticDirectory = diagnosticDirectory;

        internal readonly List<string> samples = [];

        public int CoreDiffs { get; internal set; }

        public int ChartHashDiffs { get; internal set; }

        public int LengthDiffs { get; internal set; }

        public int DistributionDiffs { get; internal set; }

        public int BpmIntegerDiffs { get; internal set; }

        public int TotalDiffs { get; internal set; }

        public int DensityDiffs { get; internal set; }

        public int PeakDensityDiffs { get; internal set; }

        public int EndDensityDiffs { get; internal set; }

        public int MainBpmDiffs { get; internal set; }

        public void Add(RealChartInfoExpectedRow expected, BeMusicSeeker.Models.ChartDetails actual, string chartString)
        {
            CoreDiffs += CountCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
                AddSample("length", expected, expected.length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), actual.length.GetValueOrDefault().ToString(CultureInfo.InvariantCulture), chartString);
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
            if (!NullableDoubleEquals(expected.total, actual.total))
            {
                TotalDiffs++;
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
            if (!NullableDoubleEquals(expected.mainbpm, actual.mainbpm))
            {
                MainBpmDiffs++;
                AddSample(
                    "mainbpm",
                    expected,
                    (expected.mainbpm ?? 0.0).ToString("R", CultureInfo.InvariantCulture),
                    (actual.mainbpm ?? 0.0).ToString("R", CultureInfo.InvariantCulture),
                    chartString);
            }
        }

        public override string ToString()
        {
            return "chart_info bmson compatibility diffs: "
                + "core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " distribution=" + DistributionDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + " total=" + TotalDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " mainbpm=" + MainBpmDiffs
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples));
        }

        internal void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
        {
            if (samples.Count >= 25 || samples.Count(item => item.StartsWith(field + " ", StringComparison.Ordinal)) >= 5)
            {
                return;
            }
            string artifactPath = WriteChartStringArtifact(expected, chartString);
            samples.Add(
                field
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " expected=" + expectedValue
                    + " actual=" + actualValue
                    + " chartString=" + artifactPath);
        }

        internal string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = diagnosticDirectory;
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        internal static int CountCoreDiffs(RealChartInfoExpectedRow expected, BeMusicSeeker.Models.ChartDetails actual)
        {
            int count = 0;
            count += string.Equals(expected.sha256, actual.sha256, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            count += string.Equals(expected.md5, actual.md5, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            count += LevelEqualsBeatoraja(expected.level, actual.level) ? 0 : 1;
            count += expected.difficulty == actual.difficulty ? 0 : 1;
            count += expected.mode == actual.mode ? 0 : 1;
            count += expected.judge == actual.judge ? 0 : 1;
            count += expected.feature == actual.feature ? 0 : 1;
            count += expected.notes == actual.notes ? 0 : 1;
            count += expected.n == actual.n ? 0 : 1;
            count += expected.ln == actual.ln ? 0 : 1;
            count += expected.s == actual.s ? 0 : 1;
            count += expected.ls == actual.ls ? 0 : 1;
            count += string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal) ? 0 : 1;
            return count;
        }

        internal static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        internal static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }
    }

    /// <summary>参照値との差分を元の許容差で集計し、診断を呼出元の専有領域へ出力します。</summary>
    internal sealed class CompatibilityDiffCounts
    {
        private readonly string diagnosticDirectory;

        /// <summary>診断出力先をケース所有の絶対pathで受け取ります。</summary>
        internal CompatibilityDiffCounts(string diagnosticDirectory) => this.diagnosticDirectory = diagnosticDirectory;

        internal readonly List<string> samples = [];

        internal readonly List<FieldDiffRecord> fieldDiffRecords = [];

        internal readonly Dictionary<string, int> fieldDiffCounts = new(StringComparer.Ordinal);

        internal readonly List<long> parseElapsedMilliseconds = [];

        internal readonly List<ParseRecord> parseRecords = [];

        internal readonly List<string> skippedSamples = [];

        public int ParsedCount { get; internal set; }

        public int TimeoutCount { get; internal set; }

        public int ParseFailureCount { get; internal set; }

        public long TotalElapsedMs => parseElapsedMilliseconds.Sum();

        public double AvgParseMs => parseElapsedMilliseconds.Count == 0 ? 0.0 : parseElapsedMilliseconds.Average();

        public long MaxParseMs => parseElapsedMilliseconds.Count == 0 ? 0L : parseElapsedMilliseconds.Max();

        public long ParseP95Ms
        {
            get
            {
                if (parseElapsedMilliseconds.Count == 0)
                {
                    return 0L;
                }
                long[] sorted = [.. parseElapsedMilliseconds.OrderBy(value => value)];
                int index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
                return sorted[index];
            }
        }

        public int CoreDiffs { get; internal set; }

        public int ChartHashDiffs { get; internal set; }

        public int LengthDiffs { get; internal set; }

        public int DensityDiffs { get; internal set; }

        public int PeakDensityDiffs { get; internal set; }

        public int EndDensityDiffs { get; internal set; }

        public int DistributionDiffs { get; internal set; }

        public int SpeedChangeDiffs { get; internal set; }

        public int BpmIntegerDiffs { get; internal set; }

        public int TotalDiffs => fieldDiffRecords.Count;

        public void AddParsed(RealChartInfoExpectedRow expected, BeMusicSeeker.Models.ChartDetails actual, string chartString, long elapsedMilliseconds)
        {
            ParsedCount++;
            RecordParse(expected, elapsedMilliseconds, "success");
            Add(expected, actual, chartString);
        }

        public void AddTimeout(RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            TimeoutCount++;
            RecordParse(expected, elapsedMilliseconds, "timeout");
            AddSkippedSample("timeout", expected, elapsedMilliseconds, exception);
        }

        public void AddParseFailure(RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            ParseFailureCount++;
            RecordParse(expected, elapsedMilliseconds, "failed");
            AddSkippedSample("failed", expected, elapsedMilliseconds, exception);
        }

        public void Add(RealChartInfoExpectedRow expected, BeMusicSeeker.Models.ChartDetails actual, string chartString)
        {
            if (IsRandomFeature(expected.feature) || IsRandomFeature(actual.feature))
            {
                return;
            }
            AddCoreDiffs(expected, actual);
            if (!string.Equals(expected.charthash, actual.charthash, StringComparison.OrdinalIgnoreCase))
            {
                ChartHashDiffs++;
                AddFieldDiff("charthash", expected, expected.charthash, actual.charthash);
                AddSample("charthash", expected, expected.charthash, actual.charthash, chartString);
            }
            if (expected.length != actual.length)
            {
                LengthDiffs++;
                AddFieldDiff("length", expected, FormatValue(expected.length), FormatValue(actual.length));
            }
            if (!NullableDoubleEquals(expected.density, actual.density))
            {
                DensityDiffs++;
                AddFieldDiff("density", expected, FormatValue(expected.density), FormatValue(actual.density));
            }
            if (!NullableDoubleEquals(expected.peakdensity, actual.peakdensity))
            {
                PeakDensityDiffs++;
                AddFieldDiff("peakdensity", expected, FormatValue(expected.peakdensity), FormatValue(actual.peakdensity));
            }
            if (!NullableDoubleEquals(expected.enddensity, actual.enddensity))
            {
                EndDensityDiffs++;
                AddFieldDiff("enddensity", expected, FormatValue(expected.enddensity), FormatValue(actual.enddensity));
            }
            if (!string.Equals(expected.distribution, actual.distribution, StringComparison.Ordinal))
            {
                DistributionDiffs++;
                AddFieldDiff("distribution", expected, expected.distribution, actual.distribution);
            }
            if (!string.Equals(expected.speedchange, actual.speedchange, StringComparison.Ordinal))
            {
                SpeedChangeDiffs++;
                AddFieldDiff("speedchange", expected, expected.speedchange, actual.speedchange);
            }
            if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0)
                || (int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
            {
                BpmIntegerDiffs++;
                if ((int)(expected.minbpm ?? 0.0) != (int)(actual.minbpm ?? 0.0))
                {
                    AddFieldDiff("minbpm_integer", expected, FormatValue(expected.minbpm), FormatValue(actual.minbpm));
                }
                if ((int)(expected.maxbpm ?? 0.0) != (int)(actual.maxbpm ?? 0.0))
                {
                    AddFieldDiff("maxbpm_integer", expected, FormatValue(expected.maxbpm), FormatValue(actual.maxbpm));
                }
                AddSample(
                    "bpm",
                    expected,
                    (expected.minbpm ?? 0.0).ToString("R") + "/" + (expected.maxbpm ?? 0.0).ToString("R"),
                    (actual.minbpm ?? 0.0).ToString("R") + "/" + (actual.maxbpm ?? 0.0).ToString("R"),
                    chartString);
            }
        }

        /// <summary>集計結果をケース専有の報告領域へ保存し、生成先を返します。</summary>
        public string WriteReport(string reportDirectory, string name)
        {
            string directory = Path.Combine(reportDirectory, name);
            Directory.CreateDirectory(directory);

            WriteLines(
                Path.Combine(directory, "field_diffs.csv"),
                new[] { "field,count" }.Concat(fieldDiffCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => Csv(pair.Key) + "," + pair.Value.ToString(CultureInfo.InvariantCulture))));

            WriteLines(
                Path.Combine(directory, "diff_rows.csv"),
                new[] { "fixture_id,sha256,fixture_path,field,expected,actual" }.Concat(fieldDiffRecords.Select(record => record.ToCsv())));

            WriteLines(
                Path.Combine(directory, "skipped_rows.csv"),
                new[] { "sample" }.Concat(skippedSamples.Select(Csv)));

            WriteLines(
                Path.Combine(directory, "slow_parse_top.csv"),
                new[] { "rank,status,elapsed_ms,fixture_id,sha256,fixture_path" }.Concat(parseRecords
                    .OrderByDescending(record => record.ElapsedMilliseconds)
                    .Take(20)
                    .Select((record, index) =>
                        (index + 1).ToString(CultureInfo.InvariantCulture)
                            + "," + Csv(record.Status)
                            + "," + record.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                            + "," + record.FixtureId.ToString(CultureInfo.InvariantCulture)
                            + "," + Csv(record.Sha256)
                            + "," + Csv(record.FixturePath))));

            WriteLines(
                Path.Combine(directory, "summary.txt"),
                [ToString()]);

            return directory;
        }

        public override string ToString()
        {
            return "chart_info real compatibility diffs: "
                + "parsed=" + ParsedCount
                + " timeout=" + TimeoutCount
                + " parseFailure=" + ParseFailureCount
                + " totalParseMs=" + TotalElapsedMs
                + " avgParseMs=" + AvgParseMs.ToString("0.###", CultureInfo.InvariantCulture)
                + " maxParseMs=" + MaxParseMs
                + " p95ParseMs=" + ParseP95Ms
                + " core=" + CoreDiffs
                + " charthash=" + ChartHashDiffs
                + " length=" + LengthDiffs
                + " density=" + DensityDiffs
                + " peakdensity=" + PeakDensityDiffs
                + " enddensity=" + EndDensityDiffs
                + " distribution=" + DistributionDiffs
                + " speedchange=" + SpeedChangeDiffs
                + " bpmInteger=" + BpmIntegerDiffs
                + " totalDiffs=" + TotalDiffs
                + (fieldDiffCounts.Count == 0 ? string.Empty : " fieldTop=" + string.Join(" | ", fieldDiffCounts
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(20)
                    .Select(pair => pair.Key + "=" + pair.Value.ToString(CultureInfo.InvariantCulture))))
                + (samples.Count == 0 ? string.Empty : " samples=" + string.Join(" | ", samples))
                + (skippedSamples.Count == 0 ? string.Empty : " skipped=" + string.Join(" | ", skippedSamples))
                + (parseRecords.Count == 0 ? string.Empty : " slowTop=" + string.Join(" | ", GetSlowTopRecords()));
        }

        internal void AddCoreDiffs(RealChartInfoExpectedRow expected, BeMusicSeeker.Models.ChartDetails actual)
        {
            CompareCoreField("level", expected, FormatValue(expected.level), FormatValue(actual.level), LevelEqualsBeatoraja(expected.level, actual.level));
            CompareCoreField("difficulty", expected, FormatValue(expected.difficulty), FormatValue(actual.difficulty), expected.difficulty == actual.difficulty);
            CompareCoreField("notes", expected, FormatValue(expected.notes), FormatValue(actual.notes), expected.notes == actual.notes);
            CompareCoreField("n", expected, FormatValue(expected.n), FormatValue(actual.n), expected.n == actual.n);
            CompareCoreField("ln", expected, FormatValue(expected.ln), FormatValue(actual.ln), expected.ln == actual.ln);
            CompareCoreField("s", expected, FormatValue(expected.s), FormatValue(actual.s), expected.s == actual.s);
            CompareCoreField("ls", expected, FormatValue(expected.ls), FormatValue(actual.ls), expected.ls == actual.ls);
            CompareCoreField("judge", expected, FormatValue(expected.judge), FormatValue(actual.judge), expected.judge == actual.judge);
            CompareCoreField("feature", expected, FormatValue(expected.feature), FormatValue(actual.feature), expected.feature == actual.feature);
            CompareCoreField("mode", expected, FormatValue(expected.mode), FormatValue(actual.mode), expected.mode == actual.mode);
            CompareCoreField("mainbpm", expected, FormatValue(expected.mainbpm), FormatValue(actual.mainbpm), NullableDoubleEquals(expected.mainbpm, actual.mainbpm));
            CompareCoreField("lanenotes", expected, expected.lanenotes, actual.lanenotes, string.Equals(expected.lanenotes, actual.lanenotes, StringComparison.Ordinal));
            CompareCoreField("total", expected, FormatValue(expected.total), FormatValue(actual.total), NullableDoubleEquals(expected.total, actual.total));
        }

        internal static bool IsRandomFeature(int? feature)
        {
            return ((feature ?? 0) & 4) != 0;
        }

        internal void CompareCoreField(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, bool equals)
        {
            if (equals)
            {
                return;
            }
            CoreDiffs++;
            AddFieldDiff(field, expected, expectedValue, actualValue);
        }

        internal void AddFieldDiff(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue)
        {
            fieldDiffCounts.TryGetValue(field, out int count);
            fieldDiffCounts[field] = count + 1;
            fieldDiffRecords.Add(new FieldDiffRecord
            {
                FixtureId = expected.fixture_id,
                Sha256 = expected.sha256,
                FixturePath = expected.fixture_path,
                Field = field,
                Expected = expectedValue ?? string.Empty,
                Actual = actualValue ?? string.Empty
            });
        }

        internal void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
        {
            if (samples.Count >= 5)
            {
                return;
            }
            string artifactPath = WriteChartStringArtifact(expected, chartString);
            samples.Add(
                field
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " expected=" + expectedValue
                    + " actual=" + actualValue
                    + " chartString=" + artifactPath);
        }

        internal void RecordParse(RealChartInfoExpectedRow expected, long elapsedMilliseconds, string status)
        {
            parseElapsedMilliseconds.Add(elapsedMilliseconds);
            parseRecords.Add(new ParseRecord
            {
                FixtureId = expected.fixture_id,
                Sha256 = expected.sha256,
                FixturePath = expected.fixture_path,
                ElapsedMilliseconds = elapsedMilliseconds,
                Status = status
            });
        }

        internal void AddSkippedSample(string status, RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
        {
            if (skippedSamples.Count >= 10)
            {
                return;
            }
            skippedSamples.Add(
                status
                    + " fixture_id=" + expected.fixture_id
                    + " path=" + expected.fixture_path
                    + " sha256=" + expected.sha256
                    + " elapsedMs=" + elapsedMilliseconds
                    + " exception=" + exception.GetType().Name
                    + " message=" + (exception.Message ?? string.Empty));
        }

        internal IEnumerable<string> GetSlowTopRecords()
        {
            return parseRecords
                .OrderByDescending(record => record.ElapsedMilliseconds)
                .Take(10)
                .Select(record => "ranked"
                        + " status=" + record.Status
                        + " elapsedMs=" + record.ElapsedMilliseconds
                        + " fixture_id=" + record.FixtureId
                        + " path=" + record.FixturePath
                        + " sha256=" + record.Sha256);
        }

        internal string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = diagnosticDirectory;
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        internal static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        internal static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }

        internal static string FormatValue(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        internal static string FormatValue(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        internal static string FormatValue(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        internal static void WriteLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        internal static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        internal sealed class ParseRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public long ElapsedMilliseconds { get; set; }

            public string Status { get; set; } = string.Empty;
        }

        internal sealed class FieldDiffRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public string Field { get; set; } = string.Empty;

            public string Expected { get; set; } = string.Empty;

            public string Actual { get; set; } = string.Empty;

            public string ToCsv()
            {
                return FixtureId.ToString(CultureInfo.InvariantCulture)
                    + "," + Csv(Sha256)
                    + "," + Csv(FixturePath)
                    + "," + Csv(Field)
                    + "," + Csv(Expected)
                    + "," + Csv(Actual);
            }
        }
    }
}
