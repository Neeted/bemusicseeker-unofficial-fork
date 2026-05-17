#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SQLite;

namespace ChartInfoExportTool;

public sealed class ChartInfoExportOptions
{
    public string SourceSongDbPath { get; set; }

    public string OutputDbPath { get; set; }

    public string ArchiveOutputPath { get; set; }

    public string SevenZipExecutablePath { get; set; }
}

public sealed class ChartInfoExportResult
{
    public string SourceSongDbPath { get; set; }

    public string OutputDbPath { get; set; }

    public string ArchiveOutputPath { get; set; }

    public long ArchiveSizeBytes { get; set; }

    public string BundleId { get; set; }

    public int ChartInfoCount { get; set; }

    public int ChartDigestCount { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    public string ToConsoleSummary()
    {
        return "chart-info-export"
            + " chartInfo=" + ChartInfoCount.ToString(CultureInfo.InvariantCulture)
            + " chartDigest=" + ChartDigestCount.ToString(CultureInfo.InvariantCulture)
            + " bundleId=" + (BundleId ?? string.Empty)
            + " out=\"" + (OutputDbPath ?? string.Empty) + "\""
            + (string.IsNullOrWhiteSpace(ArchiveOutputPath)
                ? string.Empty
                : " archive=\"" + ArchiveOutputPath + "\" archiveBytes=" + ArchiveSizeBytes.ToString(CultureInfo.InvariantCulture));
    }
}

public static class ChartInfoExportRunner
{
    public const int CurrentChartInfoSchemaVersion = 4;

    public const int CurrentChartInfoParserVersion = 20;

    public const int MetadataBundleFormatVersion = 1;

    private const string MetadataDbFileName = "chart-info-metadata.db";

    private static readonly string[] ChartInfoColumns =
    [
        "sha256",
        "md5",
        "charthash",
        "level",
        "difficulty",
        "difficulty_defined",
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
    ];

    public static ChartInfoExportResult Export(ChartInfoExportOptions options)
    {
        options ??= new ChartInfoExportOptions();
        string sourcePath = Path.GetFullPath(RequireFile(options.SourceSongDbPath, "source song.db"));
        string outputPath = Path.GetFullPath(RequirePath(options.OutputDbPath, "output path"));
        string archiveOutputPath = string.IsNullOrWhiteSpace(options.ArchiveOutputPath) ? null : Path.GetFullPath(options.ArchiveOutputPath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Output DB must be different from source DB.", nameof(options.OutputDbPath));
        }
        if (!string.IsNullOrWhiteSpace(archiveOutputPath))
        {
            if (string.Equals(outputPath, archiveOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Archive output must be different from output DB.", nameof(options.ArchiveOutputPath));
            }
            if (string.Equals(sourcePath, archiveOutputPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Archive output must be different from source DB.", nameof(options.ArchiveOutputPath));
            }
        }

        ValidateSourceDatabase(sourcePath);

        string outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        ChartInfoExportResult result;
        DateTime generatedAtUtc = DateTime.UtcNow;
        string bundleId = Guid.NewGuid().ToString("N");
        using (var output = new SQLiteConnection(outputPath, storeDateTimeAsTicks: true))
        {
            CreateOutputSchema(output);
            output.Execute("ATTACH DATABASE " + SqlQuote(sourcePath) + " AS src;");
            try
            {
                InsertChartInfoRows(output);
                InsertChartDigestRows(output);
                int chartInfoCount = output.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_info;");
                int chartDigestCount = output.ExecuteScalar<int>("SELECT COUNT(1) FROM chart_digest_map;");
                output.Execute(
                    "INSERT INTO chart_info_metadata_bundle (bundle_id, format_version, generated_at, chart_info_schema_version, chart_info_parser_version, chart_info_count, chart_digest_count) VALUES (?, ?, ?, ?, ?, ?, ?);",
                    bundleId,
                    MetadataBundleFormatVersion,
                    generatedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    CurrentChartInfoSchemaVersion,
                    CurrentChartInfoParserVersion,
                    chartInfoCount,
                    chartDigestCount);
                result = new ChartInfoExportResult
                {
                    SourceSongDbPath = sourcePath,
                    OutputDbPath = outputPath,
                    BundleId = bundleId,
                    ChartInfoCount = chartInfoCount,
                    ChartDigestCount = chartDigestCount,
                    GeneratedAtUtc = generatedAtUtc
                };
            }
            finally
            {
                output.Execute("DETACH DATABASE src;");
            }
        }

        if (!string.IsNullOrWhiteSpace(archiveOutputPath))
        {
            result.ArchiveOutputPath = archiveOutputPath;
            result.ArchiveSizeBytes = CreateArchive(outputPath, archiveOutputPath, options.SevenZipExecutablePath);
        }
        return result;
    }

    private static long CreateArchive(string outputDbPath, string archiveOutputPath, string sevenZipExecutablePath)
    {
        string sevenZipPath = ResolveSevenZipExecutablePath(sevenZipExecutablePath);
        string archiveDirectory = Path.GetDirectoryName(archiveOutputPath);
        if (!string.IsNullOrWhiteSpace(archiveDirectory))
        {
            Directory.CreateDirectory(archiveDirectory);
        }
        if (File.Exists(archiveOutputPath))
        {
            File.Delete(archiveOutputPath);
        }

        string stagingDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectoryPath);
        try
        {
            string stagedDbPath = Path.Combine(stagingDirectoryPath, MetadataDbFileName);
            File.Copy(outputDbPath, stagedDbPath, overwrite: true);
            RunSevenZip(sevenZipPath, archiveOutputPath, stagingDirectoryPath);
            if (!File.Exists(archiveOutputPath))
            {
                throw new IOException("7z completed but archive was not created: " + archiveOutputPath);
            }

            return new FileInfo(archiveOutputPath).Length;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectoryPath))
                {
                    Directory.Delete(stagingDirectoryPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static string ResolveSevenZipExecutablePath(string sevenZipExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(sevenZipExecutablePath))
        {
            string explicitPath = Path.GetFullPath(sevenZipExecutablePath);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException("7z.exe was not found.", explicitPath);
            }
            return explicitPath;
        }

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

        throw new FileNotFoundException("7z.exe was not found. Install 7-Zip or pass --sevenzip <path>.");
    }

    private static void RunSevenZip(string sevenZipPath, string archiveOutputPath, string stagingDirectoryPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            Arguments = "a -t7z -mx=9 -mmt=on -bd -y " + QuoteProcessArgument(archiveOutputPath) + " " + QuoteProcessArgument(MetadataDbFileName),
            WorkingDirectory = stagingDirectoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start 7z.exe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("7z.exe failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine + stdout
                + Environment.NewLine + stderr);
        }
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }

    private static string RequireFile(string path, string label)
    {
        path = RequirePath(path, label);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(label + " was not found.", path);
        }
        return path;
    }

    private static string RequirePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(label + " is required.");
        }
        return path;
    }

    private static void ValidateSourceDatabase(string sourcePath)
    {
        using var source = new SQLiteConnection(sourcePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex, storeDateTimeAsTicks: true);
        if (!TableExists(source, "chart_info"))
        {
            throw new InvalidDataException("Source DB does not contain chart_info table.");
        }
        RequireColumns(source, "chart_info", ChartInfoColumns);
        if (TableExists(source, "chart_digest_map"))
        {
            RequireColumns(source, "chart_digest_map", new[] { "md5", "sha256" });
        }
    }

    private static void CreateOutputSchema(SQLiteConnection output)
    {
        output.Execute(
            "CREATE TABLE chart_info ("
            + "sha256 TEXT PRIMARY KEY, "
            + "md5 TEXT, "
            + "charthash TEXT, "
            + "level INTEGER, "
            + "difficulty INTEGER, "
            + "difficulty_defined INTEGER, "
            + "mainbpm REAL, "
            + "maxbpm REAL, "
            + "minbpm REAL, "
            + "length INTEGER, "
            + "mode INTEGER, "
            + "judge INTEGER, "
            + "feature INTEGER, "
            + "notes INTEGER, "
            + "n INTEGER, "
            + "ln INTEGER, "
            + "s INTEGER, "
            + "ls INTEGER, "
            + "total REAL, "
            + "total_defined INTEGER, "
            + "density REAL, "
            + "peakdensity REAL, "
            + "enddensity REAL, "
            + "distribution TEXT, "
            + "speedchange TEXT, "
            + "speedchange_count INTEGER, "
            + "lanenotes TEXT, "
            + "parser_version INTEGER, "
            + "updated_at DATETIME"
            + ");");
        output.Execute("CREATE INDEX chart_info_idx_md5 ON chart_info (md5);");
        output.Execute("CREATE INDEX chart_info_idx_parser_version ON chart_info (parser_version);");
        output.Execute("CREATE TABLE chart_digest_map (md5 TEXT PRIMARY KEY, sha256 TEXT);");
        output.Execute(
            "CREATE TABLE chart_info_metadata_bundle ("
            + "bundle_id TEXT PRIMARY KEY, "
            + "format_version INTEGER, "
            + "generated_at TEXT, "
            + "chart_info_schema_version INTEGER, "
            + "chart_info_parser_version INTEGER, "
            + "chart_info_count INTEGER, "
            + "chart_digest_count INTEGER"
            + ");");
    }

    private static void InsertChartInfoRows(SQLiteConnection output)
    {
        output.Execute(
            "INSERT OR REPLACE INTO chart_info ("
            + string.Join(", ", ChartInfoColumns)
            + ") SELECT "
            + "lower(trim(c.sha256)), "
            + "lower(trim(c.md5)), "
            + NormalizedOptionalSha256Expression("c.charthash") + ", "
            + "c.level, c.difficulty, c.difficulty_defined, c.mainbpm, c.maxbpm, c.minbpm, c.length, c.mode, c.judge, c.feature, c.notes, c.n, c.ln, c.s, c.ls, c.total, c.total_defined, c.density, c.peakdensity, c.enddensity, c.distribution, c.speedchange, c.speedchange_count, c.lanenotes, c.parser_version, c.updated_at "
            + "FROM src.chart_info c WHERE "
            + ValidSha256Condition("c.sha256")
            + " AND " + ValidMd5Condition("c.md5")
            + " AND c.parser_version >= " + CurrentChartInfoParserVersion.ToString(CultureInfo.InvariantCulture)
            + ";");
    }

    private static void InsertChartDigestRows(SQLiteConnection output)
    {
        if (TableExists(output, "src", "chart_digest_map"))
        {
            output.Execute(
                "INSERT OR IGNORE INTO chart_digest_map (md5, sha256) "
                + "SELECT lower(trim(d.md5)), lower(trim(d.sha256)) FROM src.chart_digest_map d WHERE "
                + ValidMd5Condition("d.md5")
                + " AND " + ValidSha256Condition("d.sha256")
                + " ORDER BY lower(trim(d.md5)) COLLATE NOCASE ASC, lower(trim(d.sha256)) COLLATE NOCASE ASC;");
        }
        output.Execute(
            "INSERT OR IGNORE INTO chart_digest_map (md5, sha256) "
            + "SELECT md5, sha256 FROM chart_info WHERE "
            + ValidMd5Condition("md5")
            + " AND " + ValidSha256Condition("sha256")
            + " ORDER BY md5 COLLATE NOCASE ASC, sha256 COLLATE NOCASE ASC;");
    }

    private static string NormalizedOptionalSha256Expression(string column)
    {
        return "CASE WHEN " + ValidSha256Condition(column) + " THEN lower(trim(" + column + ")) ELSE NULL END";
    }

    private static string ValidSha256Condition(string column)
    {
        return column + " IS NOT NULL AND length(trim(" + column + ")) = 64 AND lower(trim(" + column + ")) NOT GLOB '*[^0-9a-f]*'";
    }

    private static string ValidMd5Condition(string column)
    {
        return column + " IS NOT NULL AND length(trim(" + column + ")) = 32 AND lower(trim(" + column + ")) NOT GLOB '*[^0-9a-f]*'";
    }

    private static bool TableExists(SQLiteConnection db, string tableName)
    {
        return TableExists(db, null, tableName);
    }

    private static bool TableExists(SQLiteConnection db, string schemaName, string tableName)
    {
        string master = string.IsNullOrWhiteSpace(schemaName) ? "sqlite_master" : schemaName + ".sqlite_master";
        return db.ExecuteScalar<long>("SELECT COUNT(1) FROM " + master + " WHERE type = 'table' AND name = ?;", tableName) > 0L;
    }

    private static void RequireColumns(SQLiteConnection db, string tableName, IEnumerable<string> requiredColumns)
    {
        var columns = new HashSet<string>(
            db.Query<TableInfoRow>("PRAGMA table_info('" + tableName.Replace("'", "''") + "');").Select(row => row.name),
            StringComparer.OrdinalIgnoreCase);
        foreach (string column in requiredColumns)
        {
            if (!columns.Contains(column))
            {
                throw new InvalidDataException("Source DB table " + tableName + " does not contain required column: " + column);
            }
        }
    }

    private static string SqlQuote(string value)
    {
        return "'" + (value ?? string.Empty).Replace("'", "''") + "'";
    }

    private sealed class TableInfoRow
    {
        public string name { get; set; }
    }
}
