#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SQLite;

namespace ChartInfoCompareTool;

public sealed class ChartInfoCompareOptions
{
    public string AppSongDbPath { get; set; } = @"D:\LR2beta3\LR2files\Database\song.db";

    public string BeatorajaSongDbPath { get; set; } = @"D:\beatoraja\songdata.db";

    public string BeatorajaInfoDbPath { get; set; } = @"D:\beatoraja\songinfo.db";

    public string OutputDirectory { get; set; }

    public string FixtureOutputDirectory { get; set; }

    public int MaxFixtureCount { get; set; } = 1000;

    public bool OverwriteFixture { get; set; }

    public double Epsilon { get; set; } = 0.000001;
}

public static class ChartInfoCompareRunner
{
    private const int FeatureRandom = 4;

    public static ChartInfoCompareResult Compare(ChartInfoCompareOptions options)
    {
        options ??= new ChartInfoCompareOptions();
        ValidateInputFile(options.AppSongDbPath, "app song.db");
        ValidateInputFile(options.BeatorajaSongDbPath, "beatoraja songdata.db");
        ValidateInputFile(options.BeatorajaInfoDbPath, "beatoraja songinfo.db");

        Dictionary<string, AppChartInfoRow> chartInfoRows = LoadDictionary<AppChartInfoRow>(
            options.AppSongDbPath,
            "SELECT sha256, md5, charthash, level, difficulty, maxbpm, minbpm, length, mode, judge, feature, notes, n, ln, s, ls, total, density, peakdensity, enddensity, mainbpm, distribution, speedchange, lanenotes FROM chart_info WHERE sha256 IS NOT NULL AND sha256 <> '';");
        Dictionary<string, BeatorajaInformationRow> informationRows = LoadDictionary<BeatorajaInformationRow>(
            options.BeatorajaInfoDbPath,
            "SELECT sha256, n, ln, s, ls, total, density, peakdensity, enddensity, mainbpm, distribution, speedchange, lanenotes FROM information WHERE sha256 IS NOT NULL AND sha256 <> '';");

        var result = new ChartInfoCompareResult
        {
            AppSongDbPath = options.AppSongDbPath,
            BeatorajaSongDbPath = options.BeatorajaSongDbPath,
            BeatorajaInfoDbPath = options.BeatorajaInfoDbPath,
            ChartInfoCount = chartInfoRows.Count,
            InformationCount = informationRows.Count,
            ComparedAtUtc = DateTime.UtcNow
        };
        var referenceSha256s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (SQLiteConnection songDb = OpenReadOnly(options.BeatorajaSongDbPath))
        {
            foreach (BeatorajaSongRow song in songDb.Query<BeatorajaSongRow>(
                         "SELECT sha256, md5, path, title, subtitle, charthash, level, difficulty, maxbpm, minbpm, length, mode, judge, feature, notes FROM song WHERE sha256 IS NOT NULL AND sha256 <> '';"))
            {
                if (!informationRows.TryGetValue(song.sha256, out BeatorajaInformationRow information))
                {
                    continue;
                }
                result.ReferenceCount++;
                referenceSha256s.Add(song.sha256);
                bool random = (song.feature & FeatureRandom) != 0;
                if (random)
                {
                    result.RandomReferenceCount++;
                }
                if (!chartInfoRows.TryGetValue(song.sha256, out AppChartInfoRow actual))
                {
                    AddMissing(result, song, random);
                    continue;
                }

                result.MatchedChartInfoCount++;
                if (random)
                {
                    if (HasAnyDiff(song, information, actual, options.Epsilon))
                    {
                        result.RandomValueDiffCount++;
                        result.AddRandomSample(song);
                    }
                    continue;
                }

                CompareNonRandom(result, song, information, actual, options.Epsilon);
            }
        }

        foreach (AppChartInfoRow row in chartInfoRows.Values)
        {
            if (!referenceSha256s.Contains(row.sha256))
            {
                result.ExtraChartInfoCount++;
                result.AddExtraSample(row);
            }
        }

        result.Finish();
        ExportFixtureIfRequested(options, result);
        WriteReports(options, result);
        return result;
    }

    private static void ValidateInputFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(label + " was not found.", path);
        }
    }

    private static SQLiteConnection OpenReadOnly(string path)
    {
        return new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
    }

    private static Dictionary<string, T> LoadDictionary<T>(string dbPath, string sql) where T : IHasSha256, new()
    {
        using SQLiteConnection connection = OpenReadOnly(dbPath);
        return connection.Query<T>(sql)
            .Where(row => !string.IsNullOrWhiteSpace(row.sha256))
            .GroupBy(row => row.sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddMissing(ChartInfoCompareResult result, BeatorajaSongRow song, bool random)
    {
        result.MissingChartInfoCount++;
        result.MissingSamples.Add(CompareSample.Missing(song, random));
        if (random)
        {
            result.MissingRandomCount++;
        }
        else
        {
            result.MissingNonRandomCount++;
            result.AddNonRandomDiffChart(song);
        }
    }

    private static bool HasAnyDiff(BeatorajaSongRow song, BeatorajaInformationRow information, AppChartInfoRow actual, double epsilon)
    {
        int before = 0;
        var accumulator = new FieldDiffAccumulator();
        CompareRows(accumulator, song, information, actual, epsilon);
        return accumulator.TotalDiffs > before;
    }

    private static void CompareNonRandom(ChartInfoCompareResult result, BeatorajaSongRow song, BeatorajaInformationRow information, AppChartInfoRow actual, double epsilon)
    {
        var accumulator = new FieldDiffAccumulator(result, song, actual);
        CompareRows(accumulator, song, information, actual, epsilon);
        if (accumulator.TotalDiffs > 0)
        {
            result.AddNonRandomDiffChart(song);
        }
    }

    private static void CompareRows(FieldDiffAccumulator diffs, BeatorajaSongRow song, BeatorajaInformationRow info, AppChartInfoRow actual, double epsilon)
    {
        if (!IsBmson(song.path))
        {
            diffs.CompareStringIgnoreCase("md5", song.md5, actual.md5);
        }
        diffs.CompareStringIgnoreCase("charthash", song.charthash, actual.charthash);
        diffs.CompareLevel("level", song.level, actual.level);
        diffs.CompareInt("difficulty", song.difficulty, actual.difficulty);
        diffs.CompareBpmInt("maxbpm", song.maxbpm, actual.maxbpm);
        diffs.CompareBpmInt("minbpm", song.minbpm, actual.minbpm);
        diffs.CompareInt("length", song.length, actual.length);
        diffs.CompareInt("mode", song.mode, actual.mode);
        diffs.CompareInt("judge", song.judge, actual.judge);
        diffs.CompareInt("feature", song.feature, actual.feature);
        diffs.CompareInt("notes", song.notes, actual.notes);
        diffs.CompareInt("n", info.n, actual.n);
        diffs.CompareInt("ln", info.ln, actual.ln);
        diffs.CompareInt("s", info.s, actual.s);
        diffs.CompareInt("ls", info.ls, actual.ls);
        diffs.CompareDouble("total", info.total, actual.total, epsilon);
        diffs.CompareDouble("density", info.density, actual.density, epsilon);
        diffs.CompareDouble("peakdensity", info.peakdensity, actual.peakdensity, epsilon);
        diffs.CompareDouble("enddensity", info.enddensity, actual.enddensity, epsilon);
        diffs.CompareDouble("mainbpm", info.mainbpm, actual.mainbpm, epsilon);
        diffs.CompareStringExact("distribution", info.distribution, actual.distribution);
        diffs.CompareStringExact("speedchange", info.speedchange, actual.speedchange);
        diffs.CompareStringExact("lanenotes", info.lanenotes, actual.lanenotes);
    }

    private static bool IsBmson(string path)
    {
        return string.Equals(Path.GetExtension(path), ".bmson", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteReports(ChartInfoCompareOptions options, ChartInfoCompareResult result)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            return;
        }
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "summary.json"), result.ToJson(), new UTF8Encoding(false));
        WriteCsv(Path.Combine(options.OutputDirectory, "field_diffs.csv"), new[] { "field,count" }.Concat(result.FieldDiffs.Select(diff => Csv(diff.Field) + "," + diff.Count.ToString(CultureInfo.InvariantCulture))));
        WriteCsv(Path.Combine(options.OutputDirectory, "diff_samples.csv"), new[] { "field,sha256,path,expected,actual" }.Concat(result.DiffSamples.Select(sample => sample.ToCsv())));
        WriteCsv(Path.Combine(options.OutputDirectory, "missing_chart_info.csv"), new[] { "field,sha256,path,expected,actual" }.Concat(result.MissingSamples.Select(sample => sample.ToCsv())));
        WriteCsv(Path.Combine(options.OutputDirectory, "extra_chart_info.csv"), new[] { "field,sha256,path,expected,actual" }.Concat(result.ExtraSamples.Select(sample => sample.ToCsv())));
        WriteCsv(Path.Combine(options.OutputDirectory, "random_value_diff_samples.csv"), new[] { "field,sha256,path,expected,actual" }.Concat(result.RandomDiffSamples.Select(sample => sample.ToCsv())));
    }

    private static void ExportFixtureIfRequested(ChartInfoCompareOptions options, ChartInfoCompareResult result)
    {
        if (string.IsNullOrWhiteSpace(options.FixtureOutputDirectory))
        {
            return;
        }
        if (result.NonRandomDiffChartCount > options.MaxFixtureCount)
        {
            result.FixtureExportSkippedReason = "non_random_diff_count_exceeds_limit";
            return;
        }
        if (Directory.Exists(options.FixtureOutputDirectory))
        {
            if (!options.OverwriteFixture)
            {
                throw new IOException("Fixture output already exists. Use --overwrite-fixture: " + options.FixtureOutputDirectory);
            }
            DeleteDirectory(options.FixtureOutputDirectory);
        }
        Directory.CreateDirectory(options.FixtureOutputDirectory);

        Dictionary<string, BeatorajaInformationRow> informationRows = LoadDictionary<BeatorajaInformationRow>(
            options.BeatorajaInfoDbPath,
            "SELECT sha256, n, ln, s, ls, total, density, peakdensity, enddensity, mainbpm, distribution, speedchange, lanenotes FROM information WHERE sha256 IS NOT NULL AND sha256 <> '';");
        using var fixtureDb = new SQLiteConnection(Path.Combine(options.FixtureOutputDirectory, "expected.db"));
        fixtureDb.CreateTable<FixtureSampleChart>();
        fixtureDb.CreateTable<FixtureBeatorajaSong>();
        fixtureDb.CreateTable<FixtureBeatorajaInformation>();
        fixtureDb.CreateTable<FixtureExpectedChartInfo>();
        fixtureDb.CreateTable<FixtureComparisonSummary>();

        List<ManifestEntry> manifestEntries = [];
        int fixtureId = 0;
        foreach (BeatorajaSongRow song in result.NonRandomDiffSongs.OrderBy(row => row.sha256, StringComparer.OrdinalIgnoreCase))
        {
            if (!informationRows.TryGetValue(song.sha256, out BeatorajaInformationRow info))
            {
                continue;
            }
            fixtureId++;
            string fixturePath = CopyFixtureChart(options.FixtureOutputDirectory, song);
            fixtureDb.Insert(new FixtureSampleChart
            {
                fixture_id = fixtureId,
                sha256 = song.sha256,
                md5 = song.md5,
                source_path = song.path,
                fixture_path = fixturePath,
                reason = "production_diff"
            });
            fixtureDb.Insert(FixtureBeatorajaSong.From(song));
            fixtureDb.Insert(FixtureBeatorajaInformation.From(info));
            fixtureDb.Insert(FixtureExpectedChartInfo.From(song, info));
            manifestEntries.Add(new ManifestEntry
            {
                fixture_id = fixtureId,
                sha256 = song.sha256,
                md5 = song.md5,
                source_path = song.path,
                fixture_path = fixturePath
            });
        }

        foreach (KeyValuePair<string, string> item in result.GetSummaryValues())
        {
            fixtureDb.Insert(new FixtureComparisonSummary { name = item.Key, value = item.Value });
        }
        File.WriteAllText(Path.Combine(options.FixtureOutputDirectory, "manifest.json"), BuildManifestJson(options, result, manifestEntries), new UTF8Encoding(false));
        result.FixtureExportedCount = fixtureId;
    }

    private static string CopyFixtureChart(string fixtureRoot, BeatorajaSongRow song)
    {
        string extension = Path.GetExtension(song.path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".bms";
        }
        string relativePath = Path.Combine("charts", song.sha256.Substring(0, 2), song.sha256 + extension.ToLowerInvariant());
        string destination = Path.Combine(fixtureRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? fixtureRoot);
        if (File.Exists(song.path))
        {
            File.Copy(song.path, destination, overwrite: true);
        }
        return relativePath.Replace('\\', '/');
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
        FileAttributes rootAttributes = File.GetAttributes(path);
        if ((rootAttributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    private static string BuildManifestJson(ChartInfoCompareOptions options, ChartInfoCompareResult result, IReadOnlyList<ManifestEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine("  \"generated_at_utc\": " + Json(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)) + ",");
        builder.AppendLine("  \"app_song_db\": " + Json(options.AppSongDbPath) + ",");
        builder.AppendLine("  \"beatoraja_song_db\": " + Json(options.BeatorajaSongDbPath) + ",");
        builder.AppendLine("  \"beatoraja_info_db\": " + Json(options.BeatorajaInfoDbPath) + ",");
        builder.AppendLine("  \"non_random_diff_count\": " + result.NonRandomDiffChartCount.ToString(CultureInfo.InvariantCulture) + ",");
        builder.AppendLine("  \"entries\": [");
        for (int i = 0; i < entries.Count; i++)
        {
            ManifestEntry entry = entries[i];
            builder.Append("    {\"fixture_id\": ").Append(entry.fixture_id.ToString(CultureInfo.InvariantCulture))
                .Append(", \"sha256\": ").Append(Json(entry.sha256))
                .Append(", \"md5\": ").Append(Json(entry.md5))
                .Append(", \"source_path\": ").Append(Json(entry.source_path))
                .Append(", \"fixture_path\": ").Append(Json(entry.fixture_path))
                .Append("}");
            builder.AppendLine(i + 1 == entries.Count ? string.Empty : ",");
        }
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    internal static string Csv(string value)
    {
        value ??= string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    internal static string Json(string value)
    {
        if (value == null)
        {
            return "null";
        }
        var builder = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static void WriteCsv(string path, IEnumerable<string> lines)
    {
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
    }
}

public sealed class ChartInfoCompareResult
{
    private readonly Dictionary<string, FieldDiffSummary> fieldDiffs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BeatorajaSongRow> nonRandomDiffSongs = new(StringComparer.OrdinalIgnoreCase);

    public string AppSongDbPath { get; set; }

    public string BeatorajaSongDbPath { get; set; }

    public string BeatorajaInfoDbPath { get; set; }

    public DateTime ComparedAtUtc { get; set; }

    public int ChartInfoCount { get; set; }

    public int InformationCount { get; set; }

    public int ReferenceCount { get; set; }

    public int RandomReferenceCount { get; set; }

    public int MatchedChartInfoCount { get; set; }

    public int MissingChartInfoCount { get; set; }

    public int MissingNonRandomCount { get; set; }

    public int MissingRandomCount { get; set; }

    public int ExtraChartInfoCount { get; set; }

    public int RandomValueDiffCount { get; set; }

    public int NonRandomDiffChartCount { get; private set; }

    public int FixtureExportedCount { get; set; }

    public string FixtureExportSkippedReason { get; set; }

    public List<CompareSample> DiffSamples { get; } = [];

    public List<CompareSample> MissingSamples { get; } = [];

    public List<CompareSample> ExtraSamples { get; } = [];

    public List<CompareSample> RandomDiffSamples { get; } = [];

    public IReadOnlyList<FieldDiffSummary> FieldDiffs => fieldDiffs.Values.OrderByDescending(item => item.Count).ThenBy(item => item.Field, StringComparer.Ordinal).ToList();

    public IReadOnlyCollection<BeatorajaSongRow> NonRandomDiffSongs => nonRandomDiffSongs.Values;

    public bool HasNonRandomProblems => MissingNonRandomCount > 0 || NonRandomDiffChartCount > 0;

    internal void AddFieldDiff(string field, BeatorajaSongRow song, string expected, string actual)
    {
        if (!fieldDiffs.TryGetValue(field, out FieldDiffSummary summary))
        {
            summary = new FieldDiffSummary { Field = field };
            fieldDiffs[field] = summary;
        }
        summary.Count++;
        if (DiffSamples.Count < 200 && summary.SampleCount < 10)
        {
            summary.SampleCount++;
            DiffSamples.Add(CompareSample.Diff(field, song, expected, actual));
        }
    }

    internal void AddNonRandomDiffChart(BeatorajaSongRow song)
    {
        if (!nonRandomDiffSongs.ContainsKey(song.sha256))
        {
            nonRandomDiffSongs[song.sha256] = song;
        }
    }

    internal void AddRandomSample(BeatorajaSongRow song)
    {
        if (RandomDiffSamples.Count < 50)
        {
            RandomDiffSamples.Add(CompareSample.Diff("random_value_diff", song, "beatoraja", "chart_info"));
        }
    }

    internal void AddExtraSample(AppChartInfoRow row)
    {
        if (ExtraSamples.Count < 200)
        {
            ExtraSamples.Add(CompareSample.Extra(row));
        }
    }

    internal void Finish()
    {
        NonRandomDiffChartCount = nonRandomDiffSongs.Count;
    }

    public string ToConsoleSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine("chart_info production compare");
        foreach (KeyValuePair<string, string> item in GetSummaryValues())
        {
            builder.AppendLine(item.Key + "=" + item.Value);
        }
        foreach (FieldDiffSummary diff in FieldDiffs.Take(20))
        {
            builder.AppendLine("field_diff " + diff.Field + "=" + diff.Count.ToString(CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    public string ToJson()
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        List<KeyValuePair<string, string>> values = [.. GetSummaryValues()];
        for (int i = 0; i < values.Count; i++)
        {
            KeyValuePair<string, string> item = values[i];
            builder.Append("  ").Append(ChartInfoCompareRunner.Json(item.Key)).Append(": ").Append(ChartInfoCompareRunner.Json(item.Value)).AppendLine(",");
        }
        builder.AppendLine("  \"field_diffs\": [");
        IReadOnlyList<FieldDiffSummary> diffs = FieldDiffs;
        for (int i = 0; i < diffs.Count; i++)
        {
            FieldDiffSummary diff = diffs[i];
            builder.Append("    {\"field\": ").Append(ChartInfoCompareRunner.Json(diff.Field)).Append(", \"count\": ").Append(diff.Count.ToString(CultureInfo.InvariantCulture)).Append("}");
            builder.AppendLine(i + 1 == diffs.Count ? string.Empty : ",");
        }
        builder.AppendLine("  ]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    public IEnumerable<KeyValuePair<string, string>> GetSummaryValues()
    {
        yield return Pair("compared_at_utc", ComparedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        yield return Pair("chart_info_count", ChartInfoCount);
        yield return Pair("beatoraja_information_count", InformationCount);
        yield return Pair("reference_count", ReferenceCount);
        yield return Pair("random_reference_count", RandomReferenceCount);
        yield return Pair("matched_chart_info_count", MatchedChartInfoCount);
        yield return Pair("missing_chart_info_count", MissingChartInfoCount);
        yield return Pair("missing_non_random_count", MissingNonRandomCount);
        yield return Pair("missing_random_count", MissingRandomCount);
        yield return Pair("extra_chart_info_count", ExtraChartInfoCount);
        yield return Pair("random_value_diff_count", RandomValueDiffCount);
        yield return Pair("non_random_diff_chart_count", NonRandomDiffChartCount);
        yield return Pair("fixture_exported_count", FixtureExportedCount);
        yield return Pair("fixture_export_skipped_reason", FixtureExportSkippedReason ?? string.Empty);
    }

    private static KeyValuePair<string, string> Pair(string key, int value)
    {
        return Pair(key, value.ToString(CultureInfo.InvariantCulture));
    }

    private static KeyValuePair<string, string> Pair(string key, string value)
    {
        return new KeyValuePair<string, string>(key, value);
    }
}

public sealed class FieldDiffSummary
{
    public string Field { get; set; }

    public int Count { get; set; }

    internal int SampleCount { get; set; }
}

public sealed class CompareSample
{
    public string Field { get; set; }

    public string Sha256 { get; set; }

    public string Path { get; set; }

    public string Expected { get; set; }

    public string Actual { get; set; }

    public string ToCsv()
    {
        return ChartInfoCompareRunner.Csv(Field)
            + "," + ChartInfoCompareRunner.Csv(Sha256)
            + "," + ChartInfoCompareRunner.Csv(Path)
            + "," + ChartInfoCompareRunner.Csv(Expected)
            + "," + ChartInfoCompareRunner.Csv(Actual);
    }

    internal static CompareSample Diff(string field, BeatorajaSongRow song, string expected, string actual)
    {
        return new CompareSample { Field = field, Sha256 = song.sha256, Path = song.path, Expected = expected, Actual = actual };
    }

    internal static CompareSample Missing(BeatorajaSongRow song, bool random)
    {
        return new CompareSample { Field = random ? "missing_random" : "missing_non_random", Sha256 = song.sha256, Path = song.path, Expected = "beatoraja", Actual = "missing" };
    }

    internal static CompareSample Extra(AppChartInfoRow row)
    {
        return new CompareSample { Field = "extra_chart_info", Sha256 = row.sha256, Path = string.Empty, Expected = "not_in_beatoraja_reference", Actual = "chart_info" };
    }
}

internal sealed class FieldDiffAccumulator
{
    private readonly ChartInfoCompareResult result;
    private readonly BeatorajaSongRow song;

    public int TotalDiffs { get; private set; }

    public FieldDiffAccumulator()
    {
    }

    public FieldDiffAccumulator(ChartInfoCompareResult result, BeatorajaSongRow song, AppChartInfoRow actualRow)
    {
        this.result = result;
        this.song = song;
    }

    public void CompareStringIgnoreCase(string field, string expected, string actual)
    {
        if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            Add(field, expected, actual);
        }
    }

    public void CompareStringExact(string field, string expected, string actual)
    {
        if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal))
        {
            Add(field, expected, actual);
        }
    }

    public void CompareInt(string field, int expected, int? actual)
    {
        if (!actual.HasValue || expected != actual.Value)
        {
            Add(field, expected.ToString(CultureInfo.InvariantCulture), actual?.ToString(CultureInfo.InvariantCulture) ?? "NULL");
        }
    }

    public void CompareLevel(string field, int expected, int? actual)
    {
        if (expected == 0 && !actual.HasValue)
        {
            return;
        }
        CompareInt(field, expected, actual);
    }

    public void CompareBpmInt(string field, int expected, double? actual)
    {
        int actualInt = actual.HasValue ? (int)actual.Value : 0;
        if (expected != actualInt)
        {
            Add(field, expected.ToString(CultureInfo.InvariantCulture), actual?.ToString("R", CultureInfo.InvariantCulture) ?? "NULL");
        }
    }

    public void CompareDouble(string field, double expected, double? actual, double epsilon)
    {
        if (!actual.HasValue || Math.Abs(expected - actual.Value) > epsilon)
        {
            Add(field, expected.ToString("R", CultureInfo.InvariantCulture), actual?.ToString("R", CultureInfo.InvariantCulture) ?? "NULL");
        }
    }

    private void Add(string field, string expected, string actual)
    {
        TotalDiffs++;
        result?.AddFieldDiff(field, song, expected, actual);
    }
}

public interface IHasSha256
{
    string sha256 { get; set; }
}

public class BeatorajaSongRow : IHasSha256
{
    public string sha256 { get; set; }
    public string md5 { get; set; }
    public string path { get; set; }
    public string title { get; set; }
    public string subtitle { get; set; }
    public string charthash { get; set; }
    public int level { get; set; }
    public int difficulty { get; set; }
    public int maxbpm { get; set; }
    public int minbpm { get; set; }
    public int length { get; set; }
    public int mode { get; set; }
    public int judge { get; set; }
    public int feature { get; set; }
    public int notes { get; set; }
}

public class BeatorajaInformationRow : IHasSha256
{
    public string sha256 { get; set; }
    public int n { get; set; }
    public int ln { get; set; }
    public int s { get; set; }
    public int ls { get; set; }
    public double total { get; set; }
    public double density { get; set; }
    public double peakdensity { get; set; }
    public double enddensity { get; set; }
    public double mainbpm { get; set; }
    public string distribution { get; set; }
    public string speedchange { get; set; }
    public string lanenotes { get; set; }
}

public sealed class AppChartInfoRow : IHasSha256
{
    public string sha256 { get; set; }
    public string md5 { get; set; }
    public string charthash { get; set; }
    public int? level { get; set; }
    public int? difficulty { get; set; }
    public double? maxbpm { get; set; }
    public double? minbpm { get; set; }
    public int? length { get; set; }
    public int? mode { get; set; }
    public int? judge { get; set; }
    public int? feature { get; set; }
    public int? notes { get; set; }
    public int? n { get; set; }
    public int? ln { get; set; }
    public int? s { get; set; }
    public int? ls { get; set; }
    public double? total { get; set; }
    public double? density { get; set; }
    public double? peakdensity { get; set; }
    public double? enddensity { get; set; }
    public double? mainbpm { get; set; }
    public string distribution { get; set; }
    public string speedchange { get; set; }
    public string lanenotes { get; set; }
}

public sealed class FixtureSampleChart
{
    [PrimaryKey]
    public int fixture_id { get; set; }
    public string sha256 { get; set; }
    public string md5 { get; set; }
    public string source_path { get; set; }
    public string fixture_path { get; set; }
    public string reason { get; set; }
}

public sealed class FixtureBeatorajaSong : BeatorajaSongRow
{
    public static FixtureBeatorajaSong From(BeatorajaSongRow row)
    {
        return new FixtureBeatorajaSong
        {
            sha256 = row.sha256,
            md5 = row.md5,
            path = row.path,
            title = row.title,
            subtitle = row.subtitle,
            charthash = row.charthash,
            level = row.level,
            difficulty = row.difficulty,
            maxbpm = row.maxbpm,
            minbpm = row.minbpm,
            length = row.length,
            mode = row.mode,
            judge = row.judge,
            feature = row.feature,
            notes = row.notes
        };
    }
}

public sealed class FixtureBeatorajaInformation : BeatorajaInformationRow
{
    public static FixtureBeatorajaInformation From(BeatorajaInformationRow row)
    {
        return new FixtureBeatorajaInformation
        {
            sha256 = row.sha256,
            n = row.n,
            ln = row.ln,
            s = row.s,
            ls = row.ls,
            total = row.total,
            density = row.density,
            peakdensity = row.peakdensity,
            enddensity = row.enddensity,
            mainbpm = row.mainbpm,
            distribution = row.distribution,
            speedchange = row.speedchange,
            lanenotes = row.lanenotes
        };
    }
}

public sealed class FixtureExpectedChartInfo : IHasSha256
{
    [PrimaryKey]
    public string sha256 { get; set; }
    public string md5 { get; set; }
    public string charthash { get; set; }
    public int level { get; set; }
    public int difficulty { get; set; }
    public int maxbpm { get; set; }
    public int minbpm { get; set; }
    public int length { get; set; }
    public int mode { get; set; }
    public int judge { get; set; }
    public int feature { get; set; }
    public int notes { get; set; }
    public int n { get; set; }
    public int ln { get; set; }
    public int s { get; set; }
    public int ls { get; set; }
    public double total { get; set; }
    public double density { get; set; }
    public double peakdensity { get; set; }
    public double enddensity { get; set; }
    public double mainbpm { get; set; }
    public string distribution { get; set; }
    public string speedchange { get; set; }
    public string lanenotes { get; set; }

    public static FixtureExpectedChartInfo From(BeatorajaSongRow song, BeatorajaInformationRow info)
    {
        return new FixtureExpectedChartInfo
        {
            sha256 = song.sha256,
            md5 = song.md5,
            charthash = song.charthash,
            level = song.level,
            difficulty = song.difficulty,
            maxbpm = song.maxbpm,
            minbpm = song.minbpm,
            length = song.length,
            mode = song.mode,
            judge = song.judge,
            feature = song.feature,
            notes = song.notes,
            n = info.n,
            ln = info.ln,
            s = info.s,
            ls = info.ls,
            total = info.total,
            density = info.density,
            peakdensity = info.peakdensity,
            enddensity = info.enddensity,
            mainbpm = info.mainbpm,
            distribution = info.distribution,
            speedchange = info.speedchange,
            lanenotes = info.lanenotes
        };
    }
}

public sealed class FixtureComparisonSummary
{
    [PrimaryKey]
    public string name { get; set; }
    public string value { get; set; }
}

internal sealed class ManifestEntry
{
    public int fixture_id { get; set; }
    public string sha256 { get; set; }
    public string md5 { get; set; }
    public string source_path { get; set; }
    public string fixture_path { get; set; }
}
