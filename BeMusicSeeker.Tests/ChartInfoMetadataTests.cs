using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using ChartInfoExportTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ChartInfoMetadataTests
{
    private const int FeatureMine = 2;

    private const int FeatureRandom = 4;

    private const int FeatureLongByLnMode = 8;

    private const int FeatureChargeNote = 16;

    private const int FeatureStop = 64;

    private const int FeatureScroll = 128;

    private static readonly HashSet<string> ProductionDiffKnownTimeoutSha256s = new(StringComparer.OrdinalIgnoreCase)
    {
        "273433f8e72e768603d18c986b50900538e94c2bd3e50d4487b66d5c0c3c8201",
        "a56958ab747ebb7fd4332afed2493f75a414c00be694e61182cbd3a363871a43",
        "ae3d8c2c5eb88da961df62a6e7fa6ca043b528f1a36de67643eb463b18864d6f",
        "bd496f28d4a61aba6e9315f61fda463209cd908f3b08f0c2e7e06150034e2e59"
    };

    [TestMethod]
    public void EnsureChartInfoSchema_CreatesTableIndexesAndVersionWithoutAlteringSongTable()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            string songTableSqlBefore;
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songTableSqlBefore = songDb.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'song';");
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(songTableSqlBefore, verify.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_md5';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_charthash';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_idx_parser_version';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info_parse_failure';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_parse_failure_idx_sha256';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_parse_failure_idx_parser_version';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info_import_history';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'chart_info_import_history_idx_bundle_sha256';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM app_schema_version WHERE name = 'app_schema' AND version = 1;"));
            CollectionAssert.AreEquivalent(
                new[]
                {
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
                    "bga",
                    "exlevel",
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
                verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info);").Select(row => row.name).ToArray());
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "md5",
                    "sha256",
                    "path",
                    "parser_version",
                    "failure_kind",
                    "exception_type",
                    "message",
                    "parse_timeout_ms",
                    "updated_at"
                },
                verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info_parse_failure);").Select(row => row.name).ToArray());
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "import_key",
                    "bundle_id",
                    "bundle_sha256",
                    "parser_version",
                    "chart_info_imported_count",
                    "chart_digest_imported_count",
                    "failure_cleared_count",
                    "imported_at"
                },
                verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info_import_history);").Select(row => row.name).ToArray());
            Assert.IsTrue(gateway.IsChartInfoSchemaCurrent());
        });
    }

    [TestMethod]
    public void EnsureChartInfoSchema_RecreatesOldTableWithoutDifficultyDefined()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.Execute(
                    "CREATE TABLE chart_info ("
                        + "sha256 TEXT PRIMARY KEY,"
                        + "md5 TEXT,"
                        + "charthash TEXT,"
                        + "level INTEGER,"
                        + "difficulty INTEGER,"
                        + "parser_version INTEGER,"
                        + "updated_at DATETIME"
                        + ");");
                songDb.Execute("INSERT INTO chart_info (sha256, md5, charthash, level, difficulty, parser_version, updated_at) VALUES ('" + new string('a', 64) + "', '" + new string('b', 32) + "', '" + new string('c', 64) + "', 1, 1, 6, CURRENT_TIMESTAMP);");
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.IsTrue(verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info);").Any(row => row.name == "difficulty_defined"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info_parse_failure';"));
            Assert.IsTrue(gateway.IsChartInfoSchemaCurrent());
        });
    }

    [TestMethod]
    public void ChartInfoExport_ExportsCurrentRowsAndComplementsDigestMap()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5A = new('a', 32);
            string md5B = new('b', 32);
            string md5C = new('c', 32);
            string shaA = new('1', 64);
            string shaB = new('2', 64);
            string shaC = new('3', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(CreateChartInfoRow(shaB, md5B, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(CreateChartDigestRow(md5C, shaC), typeof(LR2SongDBExtended.chart_digest_map));
            }

            string outputPath = Path.Combine(tempRootPath, "chart-info-metadata.db");
            ChartInfoExportResult result = ChartInfoExportRunner.Export(new ChartInfoExportOptions
            {
                SourceSongDbPath = songDbPath,
                OutputDbPath = outputPath
            });

            Assert.AreEqual(1, result.ChartInfoCount);
            Assert.AreEqual(2, result.ChartDigestCount);
            using var verify = new SQLiteConnection(outputPath, storeDateTimeAsTicks: true);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", shaA, md5A));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", shaB));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5A, shaA));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5C, shaC));
            ChartInfoMetadataBundleManifestRow manifest = verify.Query<ChartInfoMetadataBundleManifestRow>("SELECT * FROM chart_info_metadata_bundle;").Single();
            Assert.AreEqual(ChartInfoExportRunner.MetadataBundleFormatVersion, manifest.format_version);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoSchemaVersion, manifest.chart_info_schema_version);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, manifest.chart_info_parser_version);
            Assert.AreEqual(1, manifest.chart_info_count);
            Assert.AreEqual(2, manifest.chart_digest_count);
        });
    }

    [TestMethod]
    public void ChartInfoExport_RequiresChartInfoTable()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string outputPath = Path.Combine(tempRootPath, "chart-info-metadata.db");

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoExportRunner.Export(new ChartInfoExportOptions
            {
                SourceSongDbPath = songDbPath,
                OutputDbPath = outputPath
            }));
        });
    }

    [TestMethod]
    public void ChartInfoExport_CreatesArchiveWithRootMetadataDbAndStartupImporterCanImport()
    {
        string sevenZipPath = ResolveInstalledSevenZipPathOrInconclusive();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion), typeof(LR2SongDBExtended.chart_info));
            }

            string outputPath = Path.Combine(tempRootPath, "chart-info-metadata.db");
            string archivePath = Path.Combine(tempRootPath, "chart-info-metadata.7z");
            ChartInfoExportResult result = ChartInfoExportRunner.Export(new ChartInfoExportOptions
            {
                SourceSongDbPath = songDbPath,
                OutputDbPath = outputPath,
                ArchiveOutputPath = archivePath,
                SevenZipExecutablePath = sevenZipPath
            });

            Assert.IsTrue(File.Exists(outputPath));
            Assert.IsTrue(File.Exists(archivePath));
            Assert.AreEqual(archivePath, result.ArchiveOutputPath);
            Assert.IsTrue(result.ArchiveSizeBytes > 0L);
            StringAssert.Contains(result.ToConsoleSummary(), "archive=\"");
            string listOutput = RunSevenZip(sevenZipPath, "l " + QuoteProcessArgument(archivePath));
            StringAssert.Contains(listOutput, "chart-info-metadata.db");

            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string appArchivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.Copy(archivePath, appArchivePath);
            string importDbDirectoryPath = Path.Combine(tempRootPath, "import");
            Directory.CreateDirectory(importDbDirectoryPath);
            string importDbPath = Path.Combine(importDbDirectoryPath, "song.db");
            File.Copy(songDbPath, importDbPath);
            using (var importDb = new LR2SongDBExtended(importDbPath))
            {
                importDb.Execute("DELETE FROM chart_info;");
                importDb.Execute("DELETE FROM chart_digest_map;");
            }
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
                appBaseDirectory,
                new BmsLibraryDbGateway(importDbPath),
                null,
                delegate (string sourceArchivePath, string destinationDirectoryPath)
                {
                    RunSevenZip(sevenZipPath, "x -y " + QuoteProcessArgument(sourceArchivePath) + " -o" + QuoteProcessArgument(destinationDirectoryPath));
                    return [];
                });

            using var verify = new LR2SongDBExtended(importDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.IsFalse(File.Exists(appArchivePath));
            Assert.IsTrue(File.Exists(Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName)));
        });
    }

    [TestMethod]
    public void ChartInfoExport_RejectsArchiveOutputSameAsDbOutput()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
            }

            string outputPath = Path.Combine(tempRootPath, "chart-info-metadata.db");
            Assert.ThrowsException<ArgumentException>(() => ChartInfoExportRunner.Export(new ChartInfoExportOptions
            {
                SourceSongDbPath = songDbPath,
                OutputDbPath = outputPath,
                ArchiveOutputPath = outputPath
            }));
        });
    }

    [TestMethod]
    public void ChartInfoExport_ArchiveOutputRequiresSevenZip()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
            }

            Assert.ThrowsException<FileNotFoundException>(() => ChartInfoExportRunner.Export(new ChartInfoExportOptions
            {
                SourceSongDbPath = songDbPath,
                OutputDbPath = Path.Combine(tempRootPath, "chart-info-metadata.db"),
                ArchiveOutputPath = Path.Combine(tempRootPath, "chart-info-metadata.7z"),
                SevenZipExecutablePath = Path.Combine(tempRootPath, "missing-7z.exe")
            }));
        });
    }

    [TestMethod]
    public void ImportChartInfoMetadataBundle_ImportsMissingAndStaleRowsAndClearsFailures()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string missingMd5 = new('a', 32);
            string staleMd5 = new('b', 32);
            string currentMd5 = new('c', 32);
            string digestOnlyMd5 = new('d', 32);
            string existingDigestMd5 = new('e', 32);
            string unrelatedFailureMd5 = new('f', 32);
            string missingSha = new('1', 64);
            string staleSha = new('2', 64);
            string currentSha = new('3', 64);
            string digestOnlySha = new('4', 64);
            string bundleDigestSha = new('5', 64);
            string localDigestSha = new('6', 64);
            string unrelatedFailureSha = new('7', 64);
            LR2SongDBExtended.chart_info missingBundleRow = CreateChartInfoRow(missingSha, missingMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            missingBundleRow.level = 5;
            LR2SongDBExtended.chart_info staleBundleRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            staleBundleRow.level = 8;
            LR2SongDBExtended.chart_info currentBundleRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            currentBundleRow.level = 12;
            string bundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [missingBundleRow, staleBundleRow, currentBundleRow],
                [
                    CreateChartDigestRow(digestOnlyMd5, digestOnlySha),
                    CreateChartDigestRow(existingDigestMd5, bundleDigestSha)
                ]);

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                LR2SongDBExtended.chart_info staleLocalRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
                staleLocalRow.level = 1;
                LR2SongDBExtended.chart_info currentLocalRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
                currentLocalRow.level = 3;
                songDb.InsertOrReplace(staleLocalRow, typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(currentLocalRow, typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(CreateChartDigestRow(existingDigestMd5, localDigestSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartInfoParseFailureRow(missingMd5, missingSha, "missing.bms", BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad bpm", null), typeof(LR2SongDBExtended.chart_info_parse_failure));
                songDb.InsertOrReplace(CreateChartInfoParseFailureRow(currentMd5, currentSha, "current.bms", BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad bpm", null), typeof(LR2SongDBExtended.chart_info_parse_failure));
                songDb.InsertOrReplace(CreateChartInfoParseFailureRow(unrelatedFailureMd5, unrelatedFailureSha, "unrelated.bms", BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad bpm", null), typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            ChartInfoMetadataBundleImportResult result = new BmsLibraryDbGateway(songDbPath).ImportChartInfoMetadataBundle(bundlePath, BMSFile.GetSHA256Hash(bundlePath));

            Assert.IsFalse(result.Skipped);
            Assert.AreEqual(3, result.SourceChartInfoCount);
            Assert.AreEqual(5, result.SourceDigestCount);
            Assert.AreEqual(2, result.ImportedChartInfoCount);
            Assert.AreEqual(4, result.ImportedDigestCount);
            Assert.AreEqual(2, result.FailureClearedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(5, verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", missingSha).Single().level);
            Assert.AreEqual(8, verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", staleSha).Single().level);
            Assert.AreEqual(3, verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", currentSha).Single().level);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", digestOnlyMd5, digestOnlySha));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", missingMd5, missingSha));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", existingDigestMd5, localDigestSha));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", existingDigestMd5, bundleDigestSha));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 IN (?, ?);", missingMd5, currentMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", unrelatedFailureMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_import_history WHERE bundle_sha256 = ? AND parser_version = ?;", BMSFile.GetSHA256Hash(bundlePath), BmsLibraryDbGateway.CurrentChartInfoParserVersion));
        });
    }

    [TestMethod]
    public void ImportChartInfoMetadataBundle_SkipsAlreadyImportedBundle()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string bundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string bundleSha256 = BMSFile.GetSHA256Hash(bundlePath);
            var gateway = new BmsLibraryDbGateway(songDbPath);

            ChartInfoMetadataBundleImportResult first = gateway.ImportChartInfoMetadataBundle(bundlePath, bundleSha256);
            ChartInfoMetadataBundleImportResult second = gateway.ImportChartInfoMetadataBundle(bundlePath, bundleSha256);

            Assert.IsFalse(first.Skipped);
            Assert.IsTrue(second.Skipped);
            Assert.AreEqual("already_imported", second.SkipReason);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_import_history;"));
        });
    }

    [TestMethod]
    public void ImportChartInfoMetadataBundle_ThrowsForInvalidBundleSchema()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string bundlePath = Path.Combine(tempRootPath, "invalid-chart-info-metadata.db");
            using (var invalid = new SQLiteConnection(bundlePath, storeDateTimeAsTicks: true))
            {
                invalid.Execute("CREATE TABLE not_manifest (id INTEGER);");
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);

            Assert.ThrowsException<InvalidDataException>(() => gateway.ImportChartInfoMetadataBundle(bundlePath, BMSFile.GetSHA256Hash(bundlePath)));
        });
    }

    [TestMethod]
    public void ImportChartInfoMetadataBundle_ImportedDigestMapIsAppliedByLoadSongTable()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "Songs", "chart.bms");
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath));
            File.WriteAllText(chartPath, "#PLAYER 1", Encoding.ASCII);
            string bundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<BMSFileMaintenanceInfo>();
                var song = new TestableBmsFile
                {
                    path = chartPath,
                    folder = "Songs",
                    parent = string.Empty
                };
                song.SetHash(md5);
                songDb.InsertOrReplace(song, typeof(LR2SongDB.song));
            }

            new BmsLibraryDbGateway(songDbPath).ImportChartInfoMetadataBundle(bundlePath, BMSFile.GetSHA256Hash(bundlePath));

            SongTableLoadResult result = new BmsLibraryInitializationService().LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                null,
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(sha, result.LoadedFiles[0].sha256);
            Assert.AreEqual(sha, result.ChartDigestMap[md5]);
        });
    }

    [TestMethod]
    public void StartupImporter_PrefersDatabaseBundleOverArchive()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string dbBundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string appDbPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            string appArchivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.Copy(dbBundlePath, appDbPath);
            File.WriteAllText(appArchivePath, "not used", Encoding.ASCII);
            int extractCount = 0;
            List<string> logs = [];

            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
                appBaseDirectory,
                new BmsLibraryDbGateway(songDbPath),
                logs.Add,
                delegate (string archivePath, string destinationDirectoryPath)
                {
                    extractCount++;
                    throw new InvalidOperationException("archive should not be extracted when db bundle exists.");
                });

            Assert.AreEqual(0, extractCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.IsFalse(File.Exists(appDbPath));
            Assert.IsTrue(File.Exists(Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName)));
            Assert.IsTrue(File.Exists(appArchivePath), "The unused archive should stay in the app root because the db bundle has priority.");
            Assert.IsTrue(logs.Any(message => message.Contains("bundleType=db")));
            Assert.IsFalse(logs.Any(message => message.Contains("bundleType=7z")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_bundle_archive moved") && message.Contains("bundleType=db")));
        });
    }

    [TestMethod]
    public void StartupImporter_AlreadyImportedDatabaseIsMoved()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string dbBundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string appDbPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            File.Copy(dbBundlePath, appDbPath);
            List<string> logs = [];
            var gateway = new BmsLibraryDbGateway(songDbPath);

            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add);
            string archivedPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            File.Copy(archivedPath, appDbPath);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add);

            Assert.IsFalse(File.Exists(appDbPath));
            Assert.IsTrue(File.Exists(archivedPath));
            string[] importedEntries = Directory.GetFileSystemEntries(Path.GetDirectoryName(archivedPath));
            Assert.AreEqual(1, importedEntries.Length);
            Assert.AreEqual(archivedPath, importedEntries[0]);
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import skipped reason=already_imported") && message.Contains("bundleType=db")));
        });
    }

    [TestMethod]
    public void StartupImporter_ImportsArchiveBundleAndMovesImportedArchive()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string dbBundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string archivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.WriteAllText(archivePath, "archive identity", Encoding.ASCII);
            string archiveSha256 = BMSFile.GetSHA256Hash(archivePath);
            List<string> tempDirectories = [];
            int extractCount = 0;
            List<string> logs = [];

            string createTempDirectory()
            {
                string directoryPath = Path.Combine(tempRootPath, "extract-" + tempDirectories.Count.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directoryPath);
                tempDirectories.Add(directoryPath);
                return directoryPath;
            }
            IReadOnlyList<ArchiveEntryMetadata> extractArchive(string sourceArchivePath, string destinationDirectoryPath)
            {
                extractCount++;
                Assert.AreEqual(archivePath, sourceArchivePath);
                File.Copy(dbBundlePath, Path.Combine(destinationDirectoryPath, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName));
                return [];
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add, extractArchive, createTempDirectory);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add, extractArchive, createTempDirectory);

            Assert.AreEqual(1, extractCount);
            CollectionAssert.AllItemsAreUnique(tempDirectories);
            Assert.IsTrue(tempDirectories.All(directoryPath => !Directory.Exists(directoryPath)));
            Assert.IsFalse(File.Exists(archivePath));
            Assert.IsTrue(File.Exists(Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName)));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_import_history WHERE bundle_sha256 = ? AND parser_version = ?;", archiveSha256, BmsLibraryDbGateway.CurrentChartInfoParserVersion));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import done") && message.Contains("bundleType=7z") && message.Contains("extractMs=")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import skipped reason=missing_bundle")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_bundle_archive moved") && message.Contains("bundleType=7z")));
        });
    }

    [TestMethod]
    public void StartupImporter_AlreadyImportedArchiveIsMovedWithoutExtraction()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string dbBundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string archivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.WriteAllText(archivePath, "archive identity", Encoding.ASCII);
            List<string> logs = [];
            int extractCount = 0;

            IReadOnlyList<ArchiveEntryMetadata> extractArchive(string sourceArchivePath, string destinationDirectoryPath)
            {
                extractCount++;
                File.Copy(dbBundlePath, Path.Combine(destinationDirectoryPath, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName));
                return [];
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add, extractArchive);
            string archivedPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.Copy(archivedPath, archivePath);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add, extractArchive);

            Assert.AreEqual(1, extractCount);
            Assert.IsFalse(File.Exists(archivePath));
            Assert.IsTrue(File.Exists(archivedPath));
            string[] importedEntries = Directory.GetFileSystemEntries(Path.GetDirectoryName(archivedPath));
            Assert.AreEqual(1, importedEntries.Length);
            Assert.AreEqual(archivedPath, importedEntries[0]);
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import skipped reason=already_imported") && message.Contains("bundleType=7z") && !message.Contains("extractedDbPath=")));
        });
    }

    [TestMethod]
    public void StartupImporter_ReplacesImportedMetadataCacheWithLatestBundle()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string importedDirectoryPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName);
            Directory.CreateDirectory(importedDirectoryPath);
            File.WriteAllText(Path.Combine(importedDirectoryPath, "chart-info-metadata.old.7z"), "old archive", Encoding.ASCII);
            Directory.CreateDirectory(Path.Combine(importedDirectoryPath, "old-directory"));

            string md5 = new('a', 32);
            string sha = new('1', 64);
            string dbBundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string archivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.WriteAllText(archivePath, "latest archive", Encoding.ASCII);

            IReadOnlyList<ArchiveEntryMetadata> extractArchive(string sourceArchivePath, string destinationDirectoryPath)
            {
                File.Copy(dbBundlePath, Path.Combine(destinationDirectoryPath, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName));
                return [];
            }

            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
                appBaseDirectory,
                new BmsLibraryDbGateway(songDbPath),
                null,
                extractArchive);

            string archivedPath = Path.Combine(importedDirectoryPath, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            string[] importedEntries = Directory.GetFileSystemEntries(importedDirectoryPath);
            Assert.AreEqual(1, importedEntries.Length);
            Assert.AreEqual(archivedPath, importedEntries[0]);
            Assert.AreEqual("latest archive", File.ReadAllText(archivedPath, Encoding.ASCII));
        });
    }

    [TestMethod]
    public void StartupImporter_ArchiveWithoutMetadataDatabaseLogsFailureAndCleansTempDirectory()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string archivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.WriteAllText(archivePath, "archive identity", Encoding.ASCII);
            List<string> tempDirectories = [];
            List<string> logs = [];
            string createTempDirectory()
            {
                string directoryPath = Path.Combine(tempRootPath, "extract-missing-db");
                Directory.CreateDirectory(directoryPath);
                tempDirectories.Add(directoryPath);
                return directoryPath;
            }
            IReadOnlyList<ArchiveEntryMetadata> extractArchive(string sourceArchivePath, string destinationDirectoryPath)
            {
                return [];
            }

            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
                appBaseDirectory,
                new BmsLibraryDbGateway(songDbPath),
                logs.Add,
extractArchive,
createTempDirectory);

            Assert.IsTrue(tempDirectories.Count > 0);
            Assert.IsTrue(tempDirectories.All(directoryPath => !Directory.Exists(directoryPath)));
            Assert.IsTrue(File.Exists(archivePath));
            Assert.IsFalse(Directory.Exists(Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName)));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import failed bundleType=7z")
                && message.Contains("does not contain")));
        });
    }

    [TestMethod]
    public void StartupImporter_DatabaseArchiveFailureDoesNotUndoImport()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string appBaseDirectory = Path.Combine(tempRootPath, "app");
            Directory.CreateDirectory(appBaseDirectory);
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string dbBundlePath = CreateChartInfoMetadataBundle(
                tempRootPath,
                [CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            string appDbPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            File.Copy(dbBundlePath, appDbPath);
            File.WriteAllText(Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName), "blocks archive directory", Encoding.ASCII);
            List<string> logs = [];

            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, new BmsLibraryDbGateway(songDbPath), logs.Add);

            Assert.IsTrue(File.Exists(appDbPath));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_bundle_archive failed") && message.Contains("chart-info-metadata.db")));
        });
    }

    [TestMethod]
    public void ApplyCatalogMutation_LeavesChartInfoRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "Songs", "delete.bms")
            };
            file.SetHash(new string('a', 32));
            file.SetSha256(new string('b', 64));

            using (var songDb = new LR2SongDBExtended(songDbPath))
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

            var storageRowsOwner = new CatalogStorageRowsOwner();
            CatalogStorageRowsSnapshot initialRows = storageRowsOwner.ReplaceRowsAndCaptureSnapshot([file], []);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            Assert.IsTrue(ownedCollectionOwner.ApplyBuiltCollection(
                OwnedChartCollectionState.FromStorageRows([file], []),
                initialRows.BmsRowsVersion,
                initialRows.BmsonRowsVersion));
            var delta = new LibraryMutationDelta();
            delta.ChartRemoveRequests.Add(OwnedChartRemoveRequest.FromOwnerReference(file));
            CatalogMutationReceipt receipt = new CatalogMutationOwner(
                storageRowsOwner,
                ownedCollectionOwner,
                new BmsLibraryDbGateway(songDbPath))
                .ApplyCatalogMutation(delta, delta.ChartRemoveRequests);
            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(file.hash, receipt.RemovedCharts.Single(fact => fact.Kind == ChartFileKind.Bms).Md5);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + file.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = '" + file.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void LoadChartInfosByHash_LoadsRequestedRowsAndUsesStableMd5Representative()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string firstSha = new('1', 64);
            string secondSha = new('2', 64);
            string unrelatedSha = new('3', 64);
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(secondSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(firstSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(unrelatedSha, new string('b', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);

            Dictionary<string, LR2SongDBExtended.chart_info> bySha256 = gateway.LoadChartInfosBySha256([secondSha]);
            Dictionary<string, LR2SongDBExtended.chart_info> byMd5 = gateway.LoadChartInfosByMd5([md5]);

            Assert.AreEqual(1, bySha256.Count);
            Assert.AreEqual(secondSha, bySha256[secondSha].sha256);
            Assert.AreEqual(1, byMd5.Count);
            Assert.AreEqual(firstSha, byMd5[md5].sha256);
            Assert.IsFalse(bySha256.ContainsKey(unrelatedSha));
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadChartInfosByHash_UsesReadOnlyCurrentSchemaWhileProcessLockHeld()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            gateway.UpsertChartInfos([CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);

            using var lockAcquired = new ManualResetEventSlim(false);
            using var releaseLock = new ManualResetEventSlim(false);
            Task lockHolder = Task.Run(delegate
            {
                Assert.IsTrue(LR2SongDBExtended.Lock(TimeSpan.FromSeconds(5)));
                try
                {
                    lockAcquired.Set();
                    Assert.IsTrue(releaseLock.Wait(TimeSpan.FromSeconds(5)));
                }
                finally
                {
                    LR2SongDBExtended.Unlock();
                }
            });

            Assert.IsTrue(lockAcquired.Wait(TimeSpan.FromSeconds(5)));
            Task<Dictionary<string, LR2SongDBExtended.chart_info>> lookupTask = Task.Run(() => gateway.LoadChartInfosBySha256([sha256]));
            try
            {
                Assert.IsTrue(lookupTask.Wait(TimeSpan.FromSeconds(2)), "chart_info lookup should not wait for the writable process lock when schema is current.");
            }
            finally
            {
                releaseLock.Set();
                lockHolder.Wait(TimeSpan.FromSeconds(5));
            }

            Dictionary<string, LR2SongDBExtended.chart_info> rows = lookupTask.Result;
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(md5, rows[sha256].md5);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void DeferredChartInfoHydration_BuildsSessionIndexAndUsesSha256BeforeMd5()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string md5 = new('a', 32);
            string firstSha = new('1', 64);
            string secondSha = new('2', 64);
            string unrelatedSha = new('3', 64);
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(secondSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(firstSha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                CreateChartInfoRow(unrelatedSha, new string('b', 32), BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            Assert.IsFalse(library.ChartInfoIndexHydrated);
            Assert.AreEqual(0, library.ChartInfoIndexVersion);
            Assert.IsNull(library.ResolveChartInfo(firstSha, md5));

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

            Assert.IsTrue(WaitForChartInfoHydration(library), "chart_info hydration did not complete.");
            Assert.IsTrue(library.ChartInfoIndexHydrated);
            Assert.IsTrue(library.ChartInfoIndexVersion > 0);
            Assert.AreEqual(secondSha, library.ResolveChartInfo(secondSha, md5).sha256, "sha256 match should win over md5 fallback.");
            Assert.AreEqual(firstSha, library.ResolveChartInfo(null, md5).sha256, "md5 fallback should use the stable sha256-ordered representative.");
            Assert.AreEqual(unrelatedSha, library.ResolveChartInfo(unrelatedSha, null).sha256);
        });
    }

    [TestMethod]
    public void ParseBms_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "simple.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE Chart Info Test\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 3\r\n"
                    + "#DEFEXRANK 120\r\n"
                    + "#EXLEVEL 9\r\n"
                    + "#RANK 3\r\n"
                    + "#TOTAL 300\r\n"
                    + "#BMP01 bg.png\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00104:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);
            LR2SongDBExtended.chart_info bytesRow = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, digest.hash, digest.sha256);

            AssertChartInfoEquivalent(row, bytesRow);
            Assert.AreEqual(digest.hash, row.md5);
            Assert.AreEqual(digest.sha256, row.sha256);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, row.parser_version);
            Assert.AreEqual(12, row.level);
            Assert.AreEqual(3, row.difficulty);
            Assert.IsTrue(row.difficulty_defined);
            Assert.AreEqual(5, row.mode);
            Assert.AreEqual(100, row.judge);
            Assert.AreEqual(1, row.bga);
            Assert.AreEqual(9, row.exlevel);
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
    public void ParseBms_ExLevelDefaultsToZeroAndIgnoresDefExRank()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "exlevel-default.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE ExLevel Default\r\n"
                    + "#BPM 120\r\n"
                    + "#DEFEXRANK 120\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);

            Assert.AreEqual(0, row.exlevel);
        });
    }

    [TestMethod]
    public void ParseBms_SectionRateUsesParsedRateWithoutSubtractionDrift()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "section-rate.bms");
            var chart = new StringBuilder();
            chart.Append("#PLAYER 1\r\n#BPM 180\r\n");
            chart.Append("#00102:0.5\r\n");
            for (int section = 2; section <= 87; section++)
            {
                chart.Append('#').Append(section.ToString("000", CultureInfo.InvariantCulture)).Append("02:0.9\r\n");
            }
            chart.Append("#08711:0001\r\n");
            File.WriteAllText(chartPath, chart.ToString(), Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(104600, row.length);
            Assert.AreEqual("180.0,0.0,180.0,104600.0", row.speedchange);
        });
    }

    [TestMethod]
    public void ParseBms_SpeedChangeUsesJavaStyleSmallExponentText()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "small-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 218.607\r\n"
                    + "#BPM01 0.0001\r\n"
                    + "#00108:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            StringAssert.Contains(row.speedchange, "1.0E-4");
            Assert.IsFalse(row.speedchange.Contains("0.0001E0"));
        });
    }

    [TestMethod]
    public void ParseBms_DoubleValuesUseJavaParseDoubleRounding()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-parse-double-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 212\r\n"
                    + "#BPM07 114.15384615384615384615384615\r\n"
                    + "#00108:07\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            StringAssert.Contains(result.Row.speedchange, "114.15384615384616");
            Assert.IsFalse(result.Row.speedchange.Contains("114.15384615384615,"));
            StringAssert.Contains(result.ChartString, "B(114.15384615384616)");
        });
    }

    [TestMethod]
    public void ParseBmson_RawJsonDoublesUseJavaParseDoubleRounding()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-parse-double-bpm.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"bpm_events\":[{\"y\":240,\"bpm\":131.4889812233735}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":480}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            StringAssert.Contains(result.Row.speedchange, "131.4889812233735");
            Assert.IsFalse(result.Row.speedchange.Contains("131.48898122337351"));
            StringAssert.Contains(result.ChartString, "B(131.4889812233735)");
        });
    }

    [TestMethod]
    public void JavaDoubleToStringJdk17_MatchesKnownCompatibilityCases()
    {
        Assert.AreEqual("0.0", JavaDoubleToStringJdk17.ToString(0.0));
        Assert.AreEqual("-0.0", JavaDoubleToStringJdk17.ToString(-0.0));
        Assert.AreEqual("NaN", JavaDoubleToStringJdk17.ToString(double.NaN));
        Assert.AreEqual("Infinity", JavaDoubleToStringJdk17.ToString(double.PositiveInfinity));
        Assert.AreEqual("-Infinity", JavaDoubleToStringJdk17.ToString(double.NegativeInfinity));
        Assert.AreEqual("4.9E-324", JavaDoubleToStringJdk17.ToString(double.Epsilon));
        Assert.AreEqual("1.0E-323", JavaDoubleToStringJdk17.ToString(1e-323));
        Assert.AreEqual("9.999999999999999E22", JavaDoubleToStringJdk17.ToString(1e23));
        Assert.AreEqual("1.9999999999999998E23", JavaDoubleToStringJdk17.ToString(2e23));
        Assert.AreEqual("8.409999999999999E21", JavaDoubleToStringJdk17.ToString(8.41e21));
        Assert.AreEqual("7.6999669989E7", JavaDoubleToStringJdk17.ToString(7.6999669989E7));
        Assert.AreEqual("3.141592653589793", JavaDoubleToStringJdk17.ToString(Math.PI));
        Assert.AreEqual("1.7976931348623157E308", JavaDoubleToStringJdk17.ToString(double.MaxValue));
    }

    [TestMethod]
    public void JavaDoubleToStringJdk21_MatchesKnownCompatibilityCases()
    {
        Assert.AreEqual("0.0", JavaDoubleToStringJdk21.ToString(0.0));
        Assert.AreEqual("-0.0", JavaDoubleToStringJdk21.ToString(-0.0));
        Assert.AreEqual("NaN", JavaDoubleToStringJdk21.ToString(double.NaN));
        Assert.AreEqual("Infinity", JavaDoubleToStringJdk21.ToString(double.PositiveInfinity));
        Assert.AreEqual("-Infinity", JavaDoubleToStringJdk21.ToString(double.NegativeInfinity));
        Assert.AreEqual("4.9E-324", JavaDoubleToStringJdk21.ToString(double.Epsilon));
        Assert.AreEqual("9.9E-324", JavaDoubleToStringJdk21.ToString(1e-323));
        Assert.AreEqual("1.0E23", JavaDoubleToStringJdk21.ToString(1e23));
        Assert.AreEqual("2.0E23", JavaDoubleToStringJdk21.ToString(2e23));
        Assert.AreEqual("8.41E21", JavaDoubleToStringJdk21.ToString(8.41e21));
        Assert.AreEqual("7.6999669989E7", JavaDoubleToStringJdk21.ToString(7.6999669989E7));
        Assert.AreEqual("3.141592653589793", JavaDoubleToStringJdk21.ToString(Math.PI));
        Assert.AreEqual("1.7976931348623157E308", JavaDoubleToStringJdk21.ToString(double.MaxValue));
        Assert.AreEqual("1.1451419198103644E18", JavaDoubleToStringJdk21.ToString(1.1451419198103644E18));
        Assert.AreEqual("8.492905781983985E17", JavaDoubleToStringJdk21.ToString(8.492905781983985E17));
    }

    [TestMethod]
    public void JavaDoubleParserJdk17_MatchesKnownCompatibilityCases()
    {
        AssertJavaDoubleParseBits("0", "0000000000000000");
        AssertJavaDoubleParseBits("-0", "8000000000000000");
        AssertJavaDoubleParseBits("NaN", "7ff8000000000000");
        AssertJavaDoubleParseBits("-NaN", "7ff8000000000000");
        AssertJavaDoubleParseBits("Infinity", "7ff0000000000000");
        AssertJavaDoubleParseBits("-Infinity", "fff0000000000000");
        AssertJavaDoubleParseBits("1e23", "44b52d02c7e14af6");
        AssertJavaDoubleParseBits("2e23", "44c52d02c7e14af6");
        AssertJavaDoubleParseBits("1e-323", "0000000000000002");
        AssertJavaDoubleParseBits("4e-324", "0000000000000001");
        AssertJavaDoubleParseBits("2.4703282292062327e-324", "0000000000000000");
        AssertJavaDoubleParseBits("2.4703282292062328e-324", "0000000000000001");
        AssertJavaDoubleParseBits("2.2250738585072014e-308", "0010000000000000");
        AssertJavaDoubleParseBits("1.7976931348623157e308", "7fefffffffffffff");
        AssertJavaDoubleParseBits("1.7976931348623159e308", "7ff0000000000000");
        AssertJavaDoubleParseBits("0x1p0", "3ff0000000000000");
        AssertJavaDoubleParseBits("0x1.8p1", "4008000000000000");
        AssertJavaDoubleParseBits("0x1.fffffffffffffp1023", "7fefffffffffffff");
        AssertJavaDoubleParseBits("0x1.fffffffffffff8p1023", "7ff0000000000000");
        AssertJavaDoubleParseBits("0x0.0000000000001p-1022", "0000000000000001");
        AssertJavaDoubleParseBits("0x1p-1075", "0000000000000000");
        AssertJavaDoubleParseBits("0x1.8p-1075", "0000000000000001");

        Assert.AreEqual("114.15384615384616", JavaDoubleToStringJdk17.ToString(JavaDoubleParserJdk17.ParseDouble("114.15384615384615384615384615")));
        Assert.AreEqual("131.4889812233735", JavaDoubleToStringJdk17.ToString(JavaDoubleParserJdk17.ParseDouble("131.4889812233735")));
    }

    [TestMethod]
    public void ParseBms_InvalidChartLikeLineExtendsTimelineSections()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "invalid-section-tail.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 102\r\n"
                    + "#00111:01\r\n"
                    + "#187???\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(2352, row.length);
            Assert.AreEqual("102.0,0.0,102.0,439999.0", row.speedchange);
        });
    }


    [TestMethod]
    public void ParseBmson_SimpleFixture_ComputesChartMetadata()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath, digest.hash, digest.sha256);
            LR2SongDBExtended.chart_info bytesRow = ChartInfoParser.ParseBytes(File.ReadAllBytes(chartPath), chartPath, digest.hash, digest.sha256);

            AssertChartInfoEquivalent(row, bytesRow);
            Assert.AreEqual(digest.hash, row.md5);
            Assert.AreEqual(digest.sha256, row.sha256);
            Assert.AreEqual(10, row.level);
            Assert.AreEqual(1, row.difficulty);
            Assert.IsFalse(row.difficulty_defined);
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
            Assert.AreEqual(4, row.speedchange_count);
            StringAssert.StartsWith(row.distribution, "#");
            Assert.AreEqual(24, row.lanenotes.Split(',').Length);
            Assert.AreEqual(64, row.charthash.Length);
        });
    }

    [TestMethod]
    public void ParseBmson_ScrollDoesNotCarryToLaterTimelines()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "scroll-reset.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"scroll_events\":[{\"y\":240,\"rate\":2.0}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":480}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual("120.0,0.0,240.0,500.0,120.0,1000.0", row.speedchange);
            Assert.AreEqual(2, row.speedchange_count);
        });
    }

    [TestMethod]
    public void BmsonJsonParser_DuplicateKeysUseLastValueAndDefaultsMatchReference()
    {
        string json = "{"
            + "\"unknown\":1,"
            + "\"scroll_events\":[{\"y\":1,\"rate\":0.5}],"
            + "\"scroll_events\":[{\"y\":2}],"
            + "\"bga\":{\"bga_events\":[{\"y\":1}]},"
            + "\"bga\":{\"bga_events\":[{\"y\":2}]}"
            + "}";

        BmsonDocument document = BmsonJsonParser.Parse(json);

        Assert.IsNotNull(document.Info);
        Assert.AreEqual("beat-7k", document.Info.ModeHint);
        Assert.AreEqual(100, document.Info.JudgeRank);
        Assert.AreEqual(100.0, document.Info.Total, 0.000001);
        Assert.AreEqual(240, document.Info.Resolution);
        Assert.IsFalse(document.Info.Level.HasValue);
        Assert.AreEqual(0, document.Lines.Length);
        Assert.AreEqual(0, document.SoundChannels.Length);
        Assert.AreEqual(1, document.ScrollEvents.Length);
        Assert.AreEqual(2, document.ScrollEvents[0].Y);
        Assert.AreEqual(1.0, document.ScrollEvents[0].Rate, 0.000001);
        Assert.AreEqual(1, document.Bga.BgaEvents.Length);
        Assert.AreEqual(2, document.Bga.BgaEvents[0].Y);
    }

    [TestMethod]
    public void BmsonJsonParser_InvalidJsonRemainsFatal()
    {
        try
        {
            BmsonJsonParser.Parse("{\"info\":");
            Assert.Fail("Invalid bmson JSON should fail.");
        }
        catch (Exception ex)
        {
            StringAssert.Contains(ex.GetType().Name, "Json");
        }
    }

    [TestMethod]
    public void ParseBmson_LevelMissingNullExplicitZeroAndFloatTruncated()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string missingPath = Path.Combine(tempRootPath, "missing-level.bmson");
            File.WriteAllText(
                missingPath,
                "{\"info\":{\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info missing = ChartInfoParser.Parse(missingPath);

            Assert.IsFalse(missing.level.HasValue);
            Assert.AreEqual(1, missing.difficulty);
            Assert.IsFalse(missing.difficulty_defined);

            string zeroPath = Path.Combine(tempRootPath, "zero-level.bmson");
            File.WriteAllText(
                zeroPath,
                "{\"info\":{\"level\":0,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info zero = ChartInfoParser.Parse(zeroPath);

            Assert.AreEqual(0, zero.level);

            string floatPath = Path.Combine(tempRootPath, "float-level.bmson");
            File.WriteAllText(
                floatPath,
                "{\"info\":{\"level\":12.9,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240.9},\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            LR2SongDBExtended.chart_info floatLevel = ChartInfoParser.Parse(floatPath);

            Assert.AreEqual(12, floatLevel.level);
        });
    }

    [TestMethod]
    public void ParseBms_Base62Fixture_UsesBase62ForIndexedDefinitionsAndDataTokens()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);

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
    public void ParseBms_IndexedBpmCommandAcceptsColonSeparator()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "indexed-bpm-colon.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#BPM01:180\r\n"
                    + "#00108:01\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(120.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    [TestMethod]
    public void ParseBms_InitialBpmIsIncludedInMinMaxBpmEvenWhenMeasureZeroChangesBpm()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "measure-zero-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 160\r\n"
                    + "#BPMB4 180\r\n"
                    + "#00003:B4\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(160.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(180.0, row.mainbpm.GetValueOrDefault(), 0.0001);
        });
    }

    [TestMethod]
    public void ParseBms_RandomFixture_UsesStableSelectedBranchOne()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    public void ParseBms_InvalidCompactRandomCommandDoesNotSetRandomFeature()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "invalid-compact-random.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM2\r\n"
                    + "#IF 1\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n",
                Encoding.ASCII);

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            Assert.AreEqual(1, result.Row.notes);
            Assert.AreEqual(0, result.Row.feature & FeatureRandom);
            Assert.IsTrue(result.Diagnostics.Any(item => item.Code == "BMS_RANDOM_INVALID"));
        });
    }

    [TestMethod]
    public void ParseBms_TimelineLongerThanOneDayIsAllowed()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "longer-than-one-day.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 2.7\r\n"
                    + "#99911:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() > 86400 * 1000);
        });
    }

    [TestMethod]
    public void ParseBms_TimelineLongerThanIntMillisecondsIsFatal()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "too-long.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 0.1\r\n"
                    + "#99911:01\r\n",
                Encoding.ASCII);

            InvalidDataException ex = Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
            StringAssert.Contains(ex.Message, "BMS timeline length is too large.");
        });
    }

    [TestMethod]
    public void ParseBms_JavaIntWrappedTimelineAddsDiagnostic()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-int-wrap.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 1\r\n"
                    + "#STOP01 90000\r\n"
                    + "#00009:" + string.Concat(Enumerable.Repeat("01", 40)) + "\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            ChartInfoParser.ChartInfoParseResult result = ChartInfoParser.ParseBytesDetailed(File.ReadAllBytes(chartPath), chartPath);

            Assert.AreEqual(1, result.Row.notes);
            Assert.IsTrue(result.Row.length.GetValueOrDefault() > 0);
            ChartInfoParser.ChartInfoParseDiagnostic diagnostic = result.Diagnostics.Single(item => item.Code == "BMS_JAVA_INT_TIME_WRAP");
            Assert.AreEqual(ChartInfoParser.ChartInfoParseDiagnosticSeverity.Info, diagnostic.Severity);
            StringAssert.Contains(diagnostic.Message, "rawMs=");
            StringAssert.Contains(diagnostic.Message, "wrappedMs=");
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetrySkipsTimelineLongerThanIntMilliseconds()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-too-long.bms");
            File.WriteAllText(
                chartPath,
                "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#BPM 0.1\r\n"
                    + "#99911:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#BPM 120\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(2000, row.length.GetValueOrDefault());
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_MalformedKnownCommandsAreNonFatalAndTotalUndefined()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    public void ParseBms_LevelAndDifficultyUseStrictReferenceParsingAndDifficultyInference()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string validPath = Path.Combine(tempRootPath, "valid-difficulty.bms");
            File.WriteAllText(
                validPath,
                "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 4\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info valid = ChartInfoParser.Parse(validPath);

            Assert.AreEqual(12, valid.level);
            Assert.AreEqual(4, valid.difficulty);
            Assert.IsTrue(valid.difficulty_defined);

            string inferredPath = Path.Combine(tempRootPath, "inferred-difficulty.bms");
            File.WriteAllText(
                inferredPath,
                "#TITLE Strict Parse\r\n"
                    + "#SUBTITLE [Hyper]\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12abc\r\n"
                    + "#DIFFICULTY 0\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info inferred = ChartInfoParser.Parse(inferredPath);

            Assert.IsFalse(inferred.level.HasValue);
            Assert.AreEqual(3, inferred.difficulty);
            Assert.IsFalse(inferred.difficulty_defined);

            string invalidAfterValidPath = Path.Combine(tempRootPath, "invalid-after-valid-difficulty.bms");
            File.WriteAllText(
                invalidAfterValidPath,
                "#BPM 120\r\n"
                    + "#PLAYLEVEL 12.5\r\n"
                    + "#DIFFICULTY 4\r\n"
                    + "#DIFFICULTY nope\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info invalidAfterValid = ChartInfoParser.Parse(invalidAfterValidPath);

            Assert.IsFalse(invalidAfterValid.level.HasValue);
            Assert.AreEqual(4, invalidAfterValid.difficulty);
            Assert.IsTrue(invalidAfterValid.difficulty_defined);
        });
    }

    [TestMethod]
    public void ParseBms_HeaderCommandsAcceptBeatorajaReserveWordFormsAndUnicodeDigits()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "reserve-word-headers.bms");
            File.WriteAllText(
                chartPath,
                "#TITLELegacy Title\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 2８\r\n"
                    + "#DIFFICULTY=2\r\n"
                    + "#RANK ３\r\n"
                    + "#00111:01\r\n",
                Encoding.GetEncoding(932));

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(28, row.level.GetValueOrDefault());
            Assert.AreEqual(2, row.difficulty);
            Assert.IsTrue(row.difficulty_defined);
            Assert.AreEqual(100, row.judge);
        });
    }

    [TestMethod]
    public void ParseBms_TotalRejectsTrailingGarbageButAcceptsDecimal()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string invalidPath = Path.Combine(tempRootPath, "total-invalid.bms");
            File.WriteAllText(invalidPath, "#BPM 120\r\n#TOTAL 100abc\r\n#00111:01\r\n", Encoding.ASCII);

            LR2SongDBExtended.chart_info invalid = ChartInfoParser.Parse(invalidPath);

            Assert.IsFalse(invalid.total_defined);

            string decimalPath = Path.Combine(tempRootPath, "total-decimal.bms");
            File.WriteAllText(decimalPath, "#BPM 120\r\n#TOTAL 100.5\r\n#00111:01\r\n", Encoding.ASCII);

            LR2SongDBExtended.chart_info decimalTotal = ChartInfoParser.Parse(decimalPath);

            Assert.IsTrue(decimalTotal.total_defined);
            Assert.AreEqual(100.5, decimalTotal.total.GetValueOrDefault(), 0.000001);
        });
    }

    [TestMethod]
    public void ParseBms_MissingInitialBpmIsFatal()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-bpm.bms");
            File.WriteAllText(chartPath, "#00111:01\r\n", Encoding.ASCII);

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
        });
    }

    [TestMethod]
    public void ParseBms_MeasureZeroIndexedBpmCanDefineInitialTimelineBpm()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "measure-zero-indexed-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#BPM01 150\r\n"
                    + "#00008:01\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(0.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.notes);
        });
    }

    [TestMethod]
    public void ParseBms_MeasureZeroDirectBpmCanDefineInitialTimelineBpm()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "measure-zero-direct-bpm.bms");
            File.WriteAllText(
                chartPath,
                "#00003:96\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(0.0, row.minbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.maxbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.AreEqual(1, row.notes);
        });
    }

    [TestMethod]
    public void ParseBms_InvalidInitialBpmWithoutTimelineZeroBpmIsFatal()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string[] texts =
            [
                "#BPM 0\r\n#00111:01\r\n",
                "#BPM -120\r\n#00111:01\r\n",
                "#BPM nope\r\n#00111:01\r\n"
            ];

            for (int index = 0; index < texts.Length; index++)
            {
                string chartPath = Path.Combine(tempRootPath, "invalid-bpm-" + index.ToString(CultureInfo.InvariantCulture) + ".bms");
                File.WriteAllText(chartPath, texts[index], Encoding.ASCII);

                Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath));
            }
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetryUsesLaterBranchWhenBranchOneHasInvalidInitialBpm()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-initial-bpm-retry.bms");
            File.WriteAllText(
                chartPath,
                "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#BPM01 150\r\n"
                    + "#00008:01\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(150.0, row.mainbpm.GetValueOrDefault(), 0.0001);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_RandomRetryUsesLaterBranchWhenBranchOneTimelineIsTooLong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-long-timeline-retry.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#00102:1100000\r\n"
                    + "#00211:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() < 86400 * 1000);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_RandomEndIfDoesNotSkipFollowingMainData()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-endif-main-data.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 1\r\n"
                    + "#00104:01\r\n"
                    + "#ENDIF\r\n"
                    + "#IF 2\r\n"
                    + "#00104:02\r\n"
                    + "#ENDIF\r\n"
                    + "#00211:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue(row.length.GetValueOrDefault() > 0);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBms_RandomEndRandomCanCloseRandomBlock()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "random-endrandom-main-data.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 120\r\n"
                    + "#RANDOM 2\r\n"
                    + "#IF 2\r\n"
                    + "#00111:01\r\n"
                    + "#ENDIF\r\n"
                    + "#ENDRANDOM\r\n"
                    + "#00211:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.IsTrue((row.feature & FeatureRandom) != 0);
        });
    }

    [TestMethod]
    public void ParseBmson_UnknownFieldsAreIgnoredAndUnsupportedModeFallsBackToBeat7()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
    public void ParseBms_NormalNoteCollisionOverwritesExistingNoteLikeBeatoraja()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "normal-overwrite.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#001D1:01\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(1, row.n);
            Assert.AreEqual(0, row.ln);
            Assert.AreEqual(0, row.s);
            Assert.AreEqual(0, row.ls);
            Assert.AreEqual(0, row.feature & FeatureMine);
            StringAssert.StartsWith(row.lanenotes, "1,0,0,");
        });
    }

    [TestMethod]
    public void ParseBms_LongNoteEndRemovesInsideLaneNotesLikeBeatoraja()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "ln-inside-collision.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#00111:000100\r\n"
                    + "#00151:010001\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(1, row.notes);
            Assert.AreEqual(0, row.n);
            Assert.AreEqual(1, row.ln);
            StringAssert.StartsWith(row.lanenotes, "0,1,0,");
        });
    }

    [TestMethod]
    public void ParseBms_MalformedChannelLineWithoutColonIsStillDecodedLikeBeatoraja()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "malformed-channel.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#00111;00001800\r\n",
                Encoding.ASCII);

            LR2SongDBExtended.chart_info row = ChartInfoParser.Parse(chartPath);

            Assert.AreEqual(4, row.notes);
            Assert.AreEqual(4, row.n);
            StringAssert.StartsWith(row.lanenotes, "4,0,0,");
        });
    }

    [TestMethod]
    public void ParseBmson_LongNoteAudioDurationAffectsChartHash()
    {
        BmsonSoundNote[] notesWithEqualY =
        [
            new BmsonSoundNote { Y = 0, Continue = false },
            new BmsonSoundNote { Y = 0, Continue = true },
            new BmsonSoundNote { Y = 240, Continue = true }
        ];
        int nextDistinctNoteIndex = 0;
        Assert.AreSame(
            notesWithEqualY[2],
            ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                notesWithEqualY,
                noteIndex: 0,
                ref nextDistinctNoteIndex));
        Assert.AreSame(
            notesWithEqualY[2],
            ChartInfoParser.AdvanceToNextBmsonContinuationNote(
                notesWithEqualY,
                noteIndex: 1,
                ref nextDistinctNoteIndex));
        Assert.IsNull(ChartInfoParser.AdvanceToNextBmsonContinuationNote(
            notesWithEqualY,
            noteIndex: 2,
            ref nextDistinctNoteIndex));

        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
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
            diffs.Add(expected, fromBytesResult.Row, fromBytesResult.ChartString);
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

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
        }
    }

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

            LR2SongDBExtended.chart_info actual;
            ChartInfoParser.ChartInfoParseResult fromBytesResult;
            try
            {
                actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
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
            LR2SongDBExtended.chart_info fromBytes = fromBytesResult.Row;
            AssertChartInfoEquivalent(actual, fromBytes);
            diffs.Add(expected, actual, fromBytesResult.ChartString);
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

    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_AllNonTimeoutRowsMatchBeatoraja()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_PRODUCTION_DIFF_FULL=1 to run the full production diff compatibility fixture.");
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
                diffs.AddParsed(expected, result.Row, result.ChartString, parseStopwatch.ElapsedMilliseconds);
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

    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ParserCompatibilitySlow")]
    [TestCategory("LargeFixture")]
    public void ParseProductionDiffFixture_KnownTimeoutRows_PerformanceAndExpectedValues()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_CHART_INFO_SLOW"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_CHART_INFO_SLOW=1 to run the slow chart_info parser fixtures.");
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
            diffs.AddParsed(expected, result.Row, result.ChartString, parseStopwatch.ElapsedMilliseconds);
        }

        string reportPath = diffs.WriteReport("production_diff_known_timeout");
        Trace.WriteLine("chart_info production diff known-timeout done report=" + reportPath + " " + diffs);
        Assert.AreEqual(rows.Count, diffs.ParsedCount, diffs.ToString());
        Assert.AreEqual(0, diffs.TotalDiffs, diffs.ToString());
    }

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_ParsePathAndBytesAgreeForSamples()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

        string[] sha256s =
        [
            "cf3203eb2b057ca03f6a1579eb50c1169c6cf2b6956058077313ab3eed5f5a3a",
            "dfc23c232b435b8abcfc9363a15400b4115a7bec0d66b0d405e6e6cdfcf224e2",
            "45d530304e95f336578c639f4e38551c380053a7b9b843d38106bad11cc4ce5e",
            "4cf26b3ba8d762de8db62eec0b7790a37da600b303aecffa6391018d83680266",
            "9aa0dd20de15bd0f7d0166afcdaf87f05c0e331fe8cdc3017d931781526a8559",
            "850175e80107119b507b42aa992b632bd3976a9ca7d1b348eb8af7bf854570e6",
            "418806ce0bcd1eecc2256b022d1aad8c9616f7c21389eab61e280952e0f68558",
            "0f9297f384c02a4060f31962e768dc6a34d83aa551fbc5a01c19a6b7c6c40b77",
            "2125eeb135073c7d1f20968bef763ac1dc6290fd836fcc29309c5d351b60667e",
            "b40404294f647c238462a47adf9f5e8a5a38832805c6bed7d8ed81926004afb6"
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

            LR2SongDBExtended.chart_info fromPath = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);
            LR2SongDBExtended.chart_info fromBytes = ChartInfoParser.ParseBytesDetailed(
                File.ReadAllBytes(chartPath),
                chartPath,
                expected.md5,
                expected.sha256,
                timeout: TimeSpan.FromSeconds(10)).Row;

            AssertChartInfoEquivalent(fromPath, fromBytes);
        }
    }

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

    [TestMethod]
    [TestCategory("Compatibility")]
    public void ParseProductionDiffFixture_NoteCollisionCountsMatchBeatorajaSamples()
    {
        string fixtureRootPath = Path.Combine(FindRepoRoot(), "BeMusicSeeker.Tests", "TestData", "chart_info_production_diff");
        string expectedDbPath = Path.Combine(fixtureRootPath, "expected.db");
        Assert.IsTrue(File.Exists(expectedDbPath), "chart_info_production_diff expected.db fixture is missing.");

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

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.n, actual.n, expected.sha256);
            Assert.AreEqual(expected.ln, actual.ln, expected.sha256);
            Assert.AreEqual(expected.s, actual.s, expected.sha256);
            Assert.AreEqual(expected.ls, actual.ls, expected.sha256);
            Assert.AreEqual(expected.lanenotes, actual.lanenotes, expected.sha256);
        }
    }

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

    [TestMethod]
    [TestCategory("Compatibility")]
    [TestCategory("ProductionDiffFull")]
    [TestCategory("LargeFixture")]
    public void ParseProductionLatestDiffFixture_MatchesJdk21ReferenceForReportedFields()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_FULL"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BMS_TEST_PRODUCTION_DIFF_FULL=1 to run the latest production diff compatibility fixture.");
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

            LR2SongDBExtended.chart_info actual = result.Row;
            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.speedchange, actual.speedchange, expected.sha256);
            AssertNullableDouble(expected.density, actual.density, expected.sha256 + " density");
            AssertNullableDouble(expected.peakdensity, actual.peakdensity, expected.sha256 + " peakdensity");
            AssertNullableDouble(expected.enddensity, actual.enddensity, expected.sha256 + " enddensity");
        }
    }

    [TestMethod]
    public void CompatibilityDiffCounts_RecordsSkippedProductionDiffRowsWithoutCountingDiffs()
    {
        RealChartInfoExpectedRow expected = CreateExpectedCompatibilityRow(new string('a', 64), new string('c', 64));
        LR2SongDBExtended.chart_info actual = CreateChartInfoRow(expected.sha256, expected.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
        actual.charthash = new string('d', 64);

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

        LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.notes, actual.notes);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mainbpm.GetValueOrDefault(), actual.mainbpm.GetValueOrDefault(), 0.000001);
        Assert.AreEqual((int)(expected.minbpm ?? 0.0), (int)(actual.minbpm ?? 0.0));
        Assert.AreEqual((int)(expected.maxbpm ?? 0.0), (int)(actual.maxbpm ?? 0.0));
    }

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

            LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(chartPath, expected.md5, expected.sha256);

            Assert.AreEqual(expected.charthash, actual.charthash, expected.sha256);
            Assert.AreEqual(expected.notes, actual.notes, expected.sha256);
            Assert.AreEqual(expected.length, actual.length, expected.sha256);
            Assert.AreEqual(expected.distribution, actual.distribution, expected.sha256);
            Assert.AreEqual(expected.density.GetValueOrDefault(), actual.density.GetValueOrDefault(), 0.000001, expected.sha256);
        }
    }

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

            LR2SongDBExtended.chart_info actual;
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

        LR2SongDBExtended.chart_info actual = ChartInfoParser.Parse(
            chartPath,
            "a0dfd7d70a53e4752d39e09b23877cff",
            "b862bf34bf6fbe034a18cceb9178a7e44a864a77e3475b9d478b9b5e5a46ff01");

        Assert.IsTrue((actual.feature & FeatureRandom) != 0);
        Assert.AreEqual(2295, actual.notes);
        Assert.AreEqual(2255, actual.n);
        Assert.AreEqual(40, actual.s);
        Assert.AreEqual(136083, actual.length);
    }

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

            Assert.ThrowsException<InvalidDataException>(() => ChartInfoParser.Parse(chartPath, sample.md5, sample.sha256), sample.sha256);
        }
    }

    [TestMethod]
    [TestCategory("CompatibilityTool")]
    [Microsoft.VisualStudio.TestTools.UnitTesting.Ignore("Manual smoke check for local JDK/reference repos. Normal dotnet test must not depend on Java.")]
    public void ChartStringDumpTool_BuildsAndDumpsReferenceChartString()
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

        string powershellPath = File.Exists(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")
            ? @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
            : "pwsh";
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments = "-ExecutionPolicy Bypass -File \"" + scriptPath + "\" \"" + chartPath + "\"",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start chartstring-dump.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30000);

        Assert.AreEqual(0, process.ExitCode, stdout + Environment.NewLine + stderr);
        StringAssert.Contains(stdout, "\"ok\":true");
        StringAssert.Contains(stdout, "\"charthash\":");
    }

    [TestMethod]
    public void BackfillChartInfos_ParsesMissingRowsSkipsCurrentRowsAndReparsesStaleRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var file = BMSFile.CreateBMSFileFromFile(chartPath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService();
            List<Tuple<int, int, string>> progress = [];

            ChartInfoBackfillResult first = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                (total, processed, currentPath) => progress.Add(Tuple.Create(total, processed, currentPath)));

            Assert.AreEqual(1, first.TargetCount);
            Assert.AreEqual(1, first.ProcessedCount);
            Assert.AreEqual(1, first.BackfilledCount);
            Assert.AreEqual(0, first.FailedCount);
            Assert.AreEqual("full", first.Mode);
            Assert.AreEqual(1, first.FileReadCount);
            Assert.AreEqual(new FileInfo(chartPath).Length, first.FileReadBytes);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, file.sha256));
            Assert.AreEqual(1, progress.Last().Item1);
            Assert.AreEqual(1, progress.Last().Item2);

            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current failure must lose to current info",
                    null)
            ]);
            ChartInfoBackfillResult second = BackfillChartInfos(service,
                gateway,
                [file],
                []);

            Assert.AreEqual(0, second.TargetCount);
            Assert.AreEqual(0, second.BackfilledCount);
            Assert.AreEqual(1, second.CurrentRowSkippedCount);
            Assert.AreEqual(0, second.FileReadCount);
            Assert.AreEqual(0L, second.FileReadBytes);

            gateway.UpsertChartInfos([CreateChartInfoRow(file.sha256, file.hash, parserVersion: 0)]);
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    parserVersion: 0,
                    "parse_failed",
                    "InvalidDataException",
                    "stale failure permits reparse",
                    null)
            ]);
            ChartInfoBackfillResult third = BackfillChartInfos(service,
                gateway,
                [file],
                []);

            Assert.AreEqual(1, third.TargetCount);
            Assert.AreEqual(1, third.BackfilledCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, verify.ExecuteScalar<int>("SELECT parser_version FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadsOnceAndPersistsDigestAndInfoForMissingSha256()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "single-read.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var readCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var service = new ChartInfoBuildService(delegate (string path)
            {
                readCounts[path] = readCounts.TryGetValue(path, out int count) ? count + 1 : 1;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            List<string> logs = [];
            object logsSync = new();
            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ProcessedCount);
            Assert.AreEqual(1, result.DigestTargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.AreEqual(1, readCounts[chartPath]);
            Assert.AreEqual(1, result.FileReadCount);
            Assert.AreEqual(new FileInfo(chartPath).Length, result.FileReadBytes);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.sha256 + "';"));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill total=", StringComparison.Ordinal) && message.Contains("fileReadCount=1") && message.Contains("fileReadBytes=" + new FileInfo(chartPath).Length)));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_UpdatesOnlyChartInfoSongProjectionAndPreservesOtherColumns()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "generated-columns.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#PLAYLEVEL 12\r\n#DIFFICULTY 4\r\n#BPM 135\r\n#00111:01\r\n",
                Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            using (var seed = new LR2SongDBExtended(songDbPath))
            {
                seed.Execute(
                    "UPDATE song SET title = ?, subtitle = ?, artist = ?, subartist = ?, genre = ?, type = ?, "
                    + "folder = ?, stagefile = ?, banner = ?, backbmp = ?, parent = ?, mode = ?, judge = ?, "
                    + "date = ?, txt = ?, favorite = ?, tag = ?, adddate = ?, "
                    + "level = ?, difficulty = ?, maxbpm = ?, minbpm = ?, bga = ?, exlevel = ?, longnote = ?, random = ?, karinotes = ? "
                    + "WHERE path = ?;",
                    "keep-title",
                    "keep-subtitle",
                    "keep-artist",
                    "keep-subartist",
                    "keep-genre",
                    77,
                    "keep-folder",
                    "keep-stagefile",
                    "keep-banner",
                    "keep-backbmp",
                    "keep-parent",
                    14,
                    9,
                    111111,
                    1,
                    7,
                    "keep-user-tag",
                    123456,
                    1,
                    1,
                    1,
                    2,
                    9,
                    9,
                    9,
                    9,
                    9,
                    chartPath);
            }

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                []);

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionRequestedCount);
            Assert.AreEqual(1, result.SongProjectionMatchedCount);
            Assert.AreEqual(1, result.SongProjectionChangedCount);
            Assert.AreEqual(0, result.SongProjectionMissingCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info chartInfo = verify.Query<LR2SongDBExtended.chart_info>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                file.sha256).Single();
            LR2SongDB.song song = verify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE path = ?;",
                chartPath).Single();
            Assert.AreEqual(chartInfo.level, song.level);
            Assert.AreEqual(Lr2ChartInfoSongProjection.NormalizeDifficulty(chartInfo.difficulty), song.difficulty);
            Assert.AreEqual((int?)chartInfo.maxbpm, song.maxbpm);
            Assert.AreEqual((int?)chartInfo.minbpm, song.minbpm);
            Assert.AreEqual(chartInfo.bga, song.bga);
            Assert.AreEqual(chartInfo.exlevel ?? 0, song.exlevel);
            Assert.AreEqual(chartInfo.notes, song.karinotes);
            Assert.AreEqual(0, song.longnote);
            Assert.AreEqual(0, song.random);
            Assert.AreEqual(chartInfo.level, file.level);
            Assert.AreEqual(Lr2ChartInfoSongProjection.NormalizeDifficulty(chartInfo.difficulty), file.difficulty);
            Assert.AreEqual((int?)chartInfo.maxbpm, file.maxbpm);
            Assert.AreEqual((int?)chartInfo.minbpm, file.minbpm);
            Assert.AreEqual(chartInfo.bga, file.bga);
            Assert.AreEqual(chartInfo.exlevel ?? 0, file.exlevel);
            Assert.AreEqual(chartInfo.notes, file.karinotes);
            Assert.AreEqual(0, file.longnote);
            Assert.AreEqual(0, file.random);
            Assert.AreEqual(file.hash, song.hash);
            Assert.AreEqual(chartPath, song.path);
            Assert.AreEqual("keep-title", song.title);
            Assert.AreEqual("keep-subtitle", song.subtitle);
            Assert.AreEqual("keep-artist", song.artist);
            Assert.AreEqual("keep-subartist", song.subartist);
            Assert.AreEqual("keep-genre", song.genre);
            Assert.AreEqual(77, song.type);
            Assert.AreEqual("keep-folder", song.folder);
            Assert.AreEqual("keep-stagefile", song.stagefile);
            Assert.AreEqual("keep-banner", song.banner);
            Assert.AreEqual("keep-backbmp", song.backbmp);
            Assert.AreEqual("keep-parent", song.parent);
            Assert.AreNotEqual(chartInfo.mode, song.mode);
            Assert.AreEqual(14, song.mode);
            Assert.AreEqual(9, song.judge);
            Assert.AreEqual(111111, song.date);
            Assert.AreEqual(1, song.txt);
            Assert.AreEqual(7, song.favorite);
            Assert.AreEqual("keep-user-tag", song.tag);
            Assert.AreEqual(123456, song.adddate);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReusedCurrentRowProjectsSongColumnsForMissingDigestCandidate()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "reuse-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE reuse\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile { path = chartPath };
            file.SetHash(snapshot.Md5);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            LR2SongDBExtended.chart_info current = CreateChartInfoRow(
                snapshot.Sha256,
                snapshot.Md5,
                BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            current.level = 17;
            current.difficulty = 5;
            current.mode = 14;
            current.updated_at = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            gateway.UpsertChartInfos([current]);
            var committedRows = new List<LR2SongDBExtended.chart_info>();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                storageCommitPublished: publication => committedRows.AddRange(publication.AppliedRows),
                existingRowsSnapshot: new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
                {
                    [snapshot.Sha256] = current
                });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(snapshot.Sha256, file.sha256);
            Assert.AreEqual(17, file.level);
            Assert.AreEqual(5, file.difficulty);
            Assert.AreEqual(5, file.mode);
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreSame(current, committedRows[0]);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(17, song.level);
            Assert.AreEqual(5, song.difficulty);
            Assert.AreEqual(5, song.mode);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
            Assert.AreEqual(current.updated_at, verify.ExecuteScalar<DateTime>("SELECT updated_at FROM chart_info WHERE sha256 = ?;", snapshot.Sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", snapshot.Md5, snapshot.Sha256));
        });
    }

    [DataTestMethod]
    [DataRow(null, 2)]
    [DataRow(-1, 2)]
    [DataRow(6, 2)]
    [DataRow(4, 4)]
    public void BackfillChartInfos_ProjectionNormalizationMatchesNewChartPath(
        int? difficulty,
        int expectedDifficulty)
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "projection-parity.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE parity\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile { path = chartPath, level = 1, difficulty = 1, mode = 5 };
            file.SetHash(snapshot.Md5);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            LR2SongDBExtended.chart_info current = CreateChartInfoRow(
                snapshot.Sha256,
                snapshot.Md5,
                BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            current.level = null;
            current.difficulty = difficulty;
            current.difficulty_defined = difficulty.HasValue;
            current.maxbpm = 199.9;
            current.minbpm = null;
            current.mode = 14;
            current.judge = 100;
            current.bga = null;
            current.exlevel = null;
            current.feature = 1 | 4 | 32;
            current.notes = 2468;
            gateway.UpsertChartInfos([current]);
            var expected = new TestableBmsFile { path = chartPath, mode = 7 };
            expected.SetHash(snapshot.Md5);
            Lr2SongRowEnricher.EnrichFromChartInfo(expected, current);

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                existingRowsSnapshot: new Dictionary<string, LR2SongDBExtended.chart_info>(StringComparer.OrdinalIgnoreCase)
                {
                    [snapshot.Sha256] = current
                });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionMatchedCount);
            Assert.AreEqual(expectedDifficulty, expected.difficulty);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(expected.level, song.level);
            Assert.AreEqual(expected.difficulty, song.difficulty);
            Assert.AreEqual(expected.maxbpm, song.maxbpm);
            Assert.AreEqual(expected.minbpm, song.minbpm);
            Assert.AreEqual(expected.bga, song.bga);
            Assert.AreEqual(expected.exlevel, song.exlevel);
            Assert.AreEqual(expected.longnote, song.longnote);
            Assert.AreEqual(expected.random, song.random);
            Assert.AreEqual(expected.karinotes, song.karinotes);
            Assert.AreEqual(5, song.mode);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_MissingSongRowCommitsFactsWithoutInsertingSong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-song-row.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath, level = 2, difficulty = 1 };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            using (var setup = new LR2SongDBExtended(songDbPath))
            {
                setup.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureChartInfoSchema(setup);
            }
            var warnings = new List<string>();
            var committedRows = new List<LR2SongDBExtended.chart_info>();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [file],
                [],
                logInstallPerformanceWarn: warnings.Add,
                storageCommitPublished: publication => committedRows.AddRange(publication.AppliedRows));

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.SongProjectionRequestedCount);
            Assert.AreEqual(0, result.SongProjectionMatchedCount);
            Assert.AreEqual(0, result.SongProjectionChangedCount);
            Assert.AreEqual(1, result.SongProjectionMissingCount);
            CollectionAssert.Contains(result.SongProjectionMissingPaths, chartPath);
            Assert.IsTrue(warnings.Any(message => message.StartsWith("chart_info_backfill song_projection_missing", StringComparison.Ordinal)));
            Assert.AreEqual(1, committedRows.Count);
            Assert.AreEqual(2, file.level);
            Assert.AreEqual(1, file.difficulty);
            Assert.AreEqual(digest.sha256, file.sha256);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", digest.sha256));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", digest.hash, digest.sha256));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReparsesCurrentVersionRowWithMismatchedMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "identity-mismatch.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile file = BMSFile.CreateBMSFileFromFile(chartPath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfos(
            [
                CreateChartInfoRow(
                    file.sha256,
                    new string('f', 32),
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion)
            ]);

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(),
                gateway,
                [file],
                []);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.CurrentRowSkippedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                file.hash,
                verify.ExecuteScalar<string>("SELECT md5 FROM chart_info WHERE sha256 = ?;", file.sha256));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_IgnoresMaintenanceEncodingAndUsesBeatorajaDefaultDecode()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "ms932-fullwidth-level.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL １\r\n"
                    + "#00111:01\r\n",
                Encoding.GetEncoding(932));

            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            file.SetMaintenanceInfo(
                new BMSFileMaintenanceInfo(file)
                {
                    encoding = "ks_c_5601-1987?"
                },
                suppressPropertyChanged: true);

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                []);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info row = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", file.sha256).Single();
            Assert.AreEqual(1, row.level);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_GroupsDuplicateMissingSha256TargetsByMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartAPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string chartBPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            string text = "#PLAYER 1\r\n#PLAYLEVEL 11\r\n#BPM 120\r\n#00111:01\r\n";
            File.WriteAllText(chartAPath, text, Encoding.ASCII);
            File.WriteAllText(chartBPath, text, Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartAPath);
            var fileA = new TestableBmsFile { path = chartAPath };
            var fileB = new TestableBmsFile { path = chartBPath };
            fileA.SetHash(digest.hash);
            fileB.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([fileA, fileB]);
            int readCount = 0;
            var service = new ChartInfoBuildService(delegate (string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 2);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [fileA, fileB],
                []);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(2, result.DigestTargetCount);
            Assert.AreEqual(2, result.DigestBackfilledCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, readCount);
            Assert.AreEqual(fileA.sha256, fileB.sha256);
            Assert.AreEqual(1L, CountChartInfoRows(songDbPath, fileA.sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info chartInfo = verify.Query<LR2SongDBExtended.chart_info>(
                "SELECT * FROM chart_info WHERE sha256 = ?;",
                fileA.sha256).Single();
            List<LR2SongDB.song> songs = verify.Query<LR2SongDB.song>(
                "SELECT * FROM song WHERE hash = ? ORDER BY path;",
                fileA.hash);
            Assert.AreEqual(2, songs.Count);
            Assert.IsTrue(songs.All(song => song.level == chartInfo.level));
            Assert.AreEqual(11, fileA.level);
            Assert.AreEqual(11, fileB.level);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", fileA.hash, fileA.sha256));
            CollectionAssert.AreEquivalent(new[] { chartAPath, chartBPath }, songs.Select(song => song.path).ToArray());
        });
    }

    [TestMethod]
    public void ChartInfoBuildTargetMapper_SeparatesBmsDigestAndBmsonIdentityRules()
    {
        string bmsMd5 = new string('a', 32);
        string bmsSha256 = new string('b', 64);
        string bmsonMd5 = new string('c', 32);
        string bmsonSha256 = new string('d', 64);
        var bmsFile = new TestableBmsFile { path = @"C:\Charts\a.bms" };
        bmsFile.SetHash(bmsMd5);
        bmsFile.SetSha256(bmsSha256);
        LR2SongDBExtended.bmson_song bmsonSong = new()
        {
            path = @"C:\Charts\b.bmson",
            md5 = bmsonMd5,
            sha256 = bmsonSha256
        };

        ChartFile bmsChart = ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false);
        ChartFile bmsonChart = ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false);

        Assert.AreEqual("md5:" + bmsMd5, ChartInfoBuildTargetMapper.BuildKey(bmsChart));
        Assert.AreEqual("sha256:" + bmsonSha256, ChartInfoBuildTargetMapper.BuildKey(bmsonChart));
        Assert.IsFalse(ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(bmsChart));
        Assert.IsFalse(ChartInfoBuildTargetMapper.IsDigestBackfillTarget(bmsChart));

        var missingDigestBmsFile = new TestableBmsFile { path = @"C:\Charts\missing-digest.bms" };
        missingDigestBmsFile.SetHash(new string('e', 32));
        ChartFile missingDigestBmsChart = ChartFileProjection.FromBmsFile(missingDigestBmsFile, includeWarningSnapshot: false);
        Assert.IsTrue(ChartInfoBuildTargetMapper.IsDigestBackfillTarget(missingDigestBmsChart));

        var noHashBmsFile = new TestableBmsFile { path = @"C:\Charts\no-hash.bms" };
        ChartFile noHashBmsChart = ChartFileProjection.FromBmsFile(noHashBmsFile, includeWarningSnapshot: false);
        Assert.IsTrue(ChartInfoBuildTargetMapper.ShouldSkipBackfillTarget(noHashBmsChart));
    }

    [TestMethod]
    public void ChartInfoBuildTarget_ApplyDigestUpdatesOnlyMissingBmsSha256()
    {
        string sharedDigest = new string('f', 64);
        var bmsFile = new TestableBmsFile { path = @"C:\Charts\a.bms" };
        bmsFile.SetHash(new string('a', 32));
        LR2SongDBExtended.bmson_song bmsonSong = new()
        {
            path = @"C:\Charts\b.bmson",
            md5 = new string('b', 32),
            sha256 = new string('c', 64)
        };
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(ChartFileProjection.FromBmsFile(bmsFile, includeWarningSnapshot: false));
        target.AddChart(ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false));
        var completedDigestFiles = new List<BMSFile>();
        var digestChanges = new List<LibraryChartDigestChange>();

        int applied = target.ApplyDigest(sharedDigest, completedDigestFiles, digestChanges);

        Assert.AreEqual(1, applied);
        Assert.AreEqual(sharedDigest, bmsFile.sha256);
        Assert.AreEqual(new string('c', 64), bmsonSong.sha256);
        CollectionAssert.Contains(completedDigestFiles, bmsFile);
        Assert.AreEqual(1, digestChanges.Count);
        Assert.AreEqual(LibraryChartKind.Bms, digestChanges[0].Kind);
        Assert.AreEqual(bmsFile.path, digestChanges[0].Path);
        Assert.AreEqual(new string('a', 32), digestChanges[0].OldMd5);
        Assert.IsTrue(string.IsNullOrWhiteSpace(digestChanges[0].OldSha256));
        Assert.AreEqual(new string('a', 32), digestChanges[0].NewMd5);
        Assert.AreEqual(sharedDigest, digestChanges[0].NewSha256);
        Assert.IsFalse(digestChanges[0].Md5Changed);
        Assert.IsTrue(digestChanges[0].Sha256Changed);
        Assert.IsFalse(digestChanges[0].PrimaryHashChanged);
    }

    [TestMethod]
    public void ChartStorageOwnerMutator_AppliesBmsonDigestOnlyAfterCommittedStorageWrite()
    {
        string oldMd5 = new string('b', 32);
        string oldSha256 = new string('c', 64);
        string newMd5 = new string('d', 32);
        string newSha256 = new string('e', 64);
        LR2SongDBExtended.bmson_song bmsonSong = new()
        {
            path = @"C:\Charts\b.bmson",
            md5 = oldMd5,
            sha256 = oldSha256
        };
        ChartFile chart = ChartFileProjection.FromBmsonSong(bmsonSong, includeWarningSnapshot: false);
        var snapshot = new ChartFileSnapshot(bmsonSong.path, [1, 2, 3], DateTime.UtcNow, newMd5, newSha256);
        var digestChanges = new List<LibraryChartDigestChange>();

        LR2SongDBExtended.bmson_song persistenceCopy = ChartStorageOwnerMutator.CreateBmsonPersistenceCopy(
            chart,
            snapshot.Md5,
            snapshot.Sha256,
            snapshot.LastWriteTimeUtc);

        Assert.AreEqual(oldMd5, bmsonSong.md5);
        Assert.AreEqual(oldSha256, bmsonSong.sha256);
        Assert.AreEqual(newMd5, persistenceCopy.md5);
        Assert.AreEqual(newSha256, persistenceCopy.sha256);

        int applied = ChartStorageOwnerMutator.ApplyCommittedSnapshot(
            chart,
            snapshot.Md5,
            snapshot.Sha256,
            snapshot.LastWriteTimeUtc,
            row: null,
            digestChanges: digestChanges);

        Assert.AreEqual(0, applied);
        Assert.AreEqual(newMd5, bmsonSong.md5);
        Assert.AreEqual(newSha256, bmsonSong.sha256);
        Assert.AreEqual(1, digestChanges.Count);
        Assert.AreEqual(LibraryChartKind.Bmson, digestChanges[0].Kind);
        Assert.AreEqual(bmsonSong.path, digestChanges[0].Path);
        Assert.AreEqual(oldMd5, digestChanges[0].OldMd5);
        Assert.AreEqual(oldSha256, digestChanges[0].OldSha256);
        Assert.AreEqual(newMd5, digestChanges[0].NewMd5);
        Assert.AreEqual(newSha256, digestChanges[0].NewSha256);
    }

    [TestMethod]
    public void BackfillChartInfos_AppliesChartInfoToBmsonStorageOwner()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "backfill-target.bmson");
            File.WriteAllText(
                chartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"title\":\"backfill target\",\"level\":6,\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"lines\":[{\"y\":0}],"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LR2SongDBExtended.bmson_song song = BmsonSongParser.Parse(chartPath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [],
                [song]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.DigestTargetCount);
            Assert.AreEqual(0, result.DigestBackfilledCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info row = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", song.sha256).Single();
            Assert.AreEqual(song.md5, row.md5);
            Assert.AreEqual(song.sha256, row.sha256);
            Assert.AreEqual(6, row.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'song';"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_TransactionFailureDoesNotPublishCanonicalDigestSongOrIndex()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "rollback.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 9\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath, level = 2 };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            bool indexPublished = false;
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            AggregateException exception = Assert.ThrowsException<AggregateException>(() =>
                service.BackfillChartInfos(
                    gateway,
                    CreateChartSnapshot([file], []),
                    storageCommitPublished: _ => indexPublished = true,
                    chartInfoChunkWriter: request => ApplyChartInfoStorageRequest(
                        gateway,
                        request,
                        _ => throw new InvalidOperationException("injected transaction failure"))));

            Assert.IsTrue(exception.Flatten().InnerExceptions.Any(inner => inner.Message.Contains("injected transaction failure", StringComparison.Ordinal)));
            Assert.IsFalse(indexPublished);
            Assert.IsTrue(string.IsNullOrWhiteSpace(file.sha256));
            Assert.AreEqual(2, file.level);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", chartPath).Single();
            Assert.AreEqual(2, song.level);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_LaterChunkFailureKeepsEarlierPublicationAndDoesNotPublishFailedChunk()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "first-chunk.bms");
            string secondPath = Path.Combine(tempRootPath, "second-chunk.bms");
            File.WriteAllText(firstPath, "#PLAYER 1\r\n#PLAYLEVEL 8\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(secondPath, "#PLAYER 1\r\n#PLAYLEVEL 10\r\n#BPM 140\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile firstDigest = BMSFile.CreateBMSFileFromFile(firstPath);
            BMSFile secondDigest = BMSFile.CreateBMSFileFromFile(secondPath);
            var firstFile = new TestableBmsFile { path = firstPath, level = 2 };
            var secondFile = new TestableBmsFile { path = secondPath, level = 3 };
            firstFile.SetHash(firstDigest.hash);
            secondFile.SetHash(secondDigest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([firstFile, secondFile]);
            var service = new ChartInfoBuildService(
                File.ReadAllBytes,
                workerCountOverride: 1,
                commitChunkSizeOverride: 1);
            int writerCalls = 0;
            var publications = new List<ChartInfoStorageCommitPublication>();

            Assert.ThrowsException<AggregateException>(() =>
                service.BackfillChartInfos(
                    gateway,
                    CreateChartSnapshot([firstFile, secondFile], []),
                    storageCommitPublished: publications.Add,
                    chartInfoChunkWriter: request =>
                    {
                        writerCalls++;
                        return ApplyChartInfoStorageRequest(
                            gateway,
                            request,
                            writerCalls == 2
                                ? _ => throw new InvalidOperationException("injected later chunk failure")
                                : null);
                    }));

            Assert.AreEqual(2, writerCalls);
            Assert.AreEqual(1, publications.Count);
            string committedPath = publications[0].DigestChanges.Single().Path;
            TestableBmsFile committedFile = string.Equals(committedPath, firstPath, StringComparison.OrdinalIgnoreCase)
                ? firstFile
                : secondFile;
            TestableBmsFile failedFile = ReferenceEquals(committedFile, firstFile) ? secondFile : firstFile;
            string committedSha256 = ReferenceEquals(committedFile, firstFile) ? firstDigest.sha256 : secondDigest.sha256;
            int committedLevel = ReferenceEquals(committedFile, firstFile) ? 8 : 10;
            int failedLevel = ReferenceEquals(failedFile, firstFile) ? 2 : 3;
            Assert.AreEqual(committedSha256, committedFile.sha256);
            Assert.AreEqual(committedLevel, committedFile.level);
            Assert.IsTrue(string.IsNullOrWhiteSpace(failedFile.sha256));
            Assert.AreEqual(failedLevel, failedFile.level);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(committedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", committedFile.path));
            Assert.AreEqual(failedLevel, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", failedFile.path));
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_ParsesOnlyProvidedSnapshots()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string targetChartPath = Path.Combine(tempRootPath, "target.bms");
            string untouchedChartPath = Path.Combine(tempRootPath, "untouched.bms");
            File.WriteAllText(targetChartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(untouchedChartPath, "#PLAYER 1\r\n#BPM 150\r\n#00111:01\r\n", Encoding.ASCII);
            var targetDigest = BMSFile.CreateBMSFileFromFile(targetChartPath);
            var untouchedDigest = BMSFile.CreateBMSFileFromFile(untouchedChartPath);
            var targetFile = new TestableBmsFile { path = targetChartPath };
            var untouchedFile = new TestableBmsFile { path = untouchedChartPath };
            targetFile.SetHash(targetDigest.hash);
            untouchedFile.SetHash(untouchedDigest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromBmsFile(targetFile, ChartFileContentReader.ReadSnapshot(targetChartPath))],
                new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(targetFile.hash, result.AppliedRows[0].md5);
            Assert.AreEqual(0, untouchedFile.sha256?.Length ?? 0);
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_GroupsDuplicateBmsMd5AndFansOutStorageApplications()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duplicate-a.bms");
            string secondPath = Path.Combine(tempRootPath, "duplicate-b.bms");
            const string content = "#PLAYER 1\r\n#TITLE duplicate\r\n#PLAYLEVEL 9\r\n#BPM 140\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content, Encoding.ASCII);
            File.WriteAllText(secondPath, content, Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            ChartFileSnapshot secondSnapshot = ChartFileContentReader.ReadSnapshot(secondPath);
            var firstFile = new TestableBmsFile { path = firstPath };
            var secondFile = new TestableBmsFile { path = secondPath };
            firstFile.SetHash(firstSnapshot.Md5);
            secondFile.SetHash(secondSnapshot.Md5);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                new BmsLibraryDbGateway(songDbPath),
                [
                    InlineChartSnapshotTarget.FromBmsFile(firstFile, firstSnapshot),
                    InlineChartSnapshotTarget.FromBmsFile(secondFile, secondSnapshot)
                ],
                new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase));

            Assert.AreEqual(firstSnapshot.Md5, secondSnapshot.Md5);
            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(2, result.StorageApplications.Count);
            CollectionAssert.AreEquivalent(
                new[] { firstPath, secondPath },
                result.StorageApplications.Select(application => application.Chart.Path).ToArray());
            Assert.IsTrue(result.StorageApplications.All(application =>
                application.Row == result.ChartInfoRows[0]));
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_GroupsDuplicateBmsMd5AcrossReadBatches()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string firstPath = Path.Combine(tempRootPath, "duplicate-batch-a.bms");
            string secondPath = Path.Combine(tempRootPath, "duplicate-batch-b.bms");
            const string content = "#PLAYER 1\r\n#TITLE duplicate batch\r\n#PLAYLEVEL 6\r\n#BPM 125\r\n#00111:01\r\n";
            File.WriteAllText(firstPath, content, Encoding.ASCII);
            File.WriteAllText(secondPath, content, Encoding.ASCII);
            ChartFileSnapshot firstSnapshot = ChartFileContentReader.ReadSnapshot(firstPath);
            var firstFile = new TestableBmsFile { path = firstPath };
            var secondFile = new TestableBmsFile { path = secondPath };
            firstFile.SetHash(firstSnapshot.Md5);
            secondFile.SetHash(firstSnapshot.Md5);
            var service = new ChartInfoInlineBuildService(
                new ChartInfoBuildService(),
                parserDegree: 1,
                batchSizeOverride: 1);

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                new BmsLibraryDbGateway(songDbPath),
                [
                    ChartFileProjection.FromBmsFile(firstFile, includeWarningSnapshot: false),
                    ChartFileProjection.FromBmsFile(secondFile, includeWarningSnapshot: false)
                ]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(1, result.ChartInfoRows.Count);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(2, result.StorageApplications.Count);
            CollectionAssert.AreEquivalent(
                new[] { firstPath, secondPath },
                result.StorageApplications.Select(application => application.Chart.Path).ToArray());
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_AppliesExistingCurrentRowWithoutParsing()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "already-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE current\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(snapshot.Md5);
            file.SetSha256(snapshot.Sha256);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            LR2SongDBExtended.chart_info expected = CreateChartInfoRow(file.sha256, file.hash, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([expected]);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            var currentFailures = new Dictionary<string, LR2SongDBExtended.chart_info_parse_failure>(StringComparer.OrdinalIgnoreCase)
            {
                [file.hash] = CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "current failure must lose to current info",
                    null)
            };

            ChartInfoInlineBuildResult result = service.BuildForSnapshots(
                gateway,
                [InlineChartSnapshotTarget.FromBmsFile(file, snapshot)],
                currentFailures);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.AreEqual(1, result.CurrentSkippedCount);
            Assert.AreEqual(1, result.AppliedRows.Count);
            Assert.AreEqual(expected.sha256, result.AppliedRows[0].sha256);
            Assert.AreEqual(expected.md5, result.AppliedRows[0].md5);
        });
    }

    [TestMethod]
    public void ChartInfoSnapshotEvaluator_CurrentRowTakesPrecedenceOverCurrentFailure()
    {
        string md5 = new('a', 32);
        string sha256 = new('b', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("not a parseable chart");
        var file = new TestableBmsFile { path = @"C:\Charts\current.bms" };
        file.SetHash(md5);
        file.SetSha256(sha256);
        var snapshot = new ChartFileSnapshot(file.path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false));
        LR2SongDBExtended.chart_info currentRow = CreateChartInfoRow(
            sha256,
            md5,
            BmsLibraryDbGateway.CurrentChartInfoParserVersion);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot,
            target,
            currentRow,
            hasCurrentParseFailure: true);

        Assert.AreSame(currentRow, result.Row);
        Assert.IsTrue(result.CurrentRowSkipped);
        Assert.IsFalse(result.SkippedPersistedFailure);
        Assert.IsFalse(result.ParseFailed);
    }

    [TestMethod]
    public void ChartInfoSnapshotEvaluator_IdentityMismatchParsesAndNormalizesFailure()
    {
        string md5 = new('c', 32);
        string sha256 = new('d', 64);
        byte[] bytes = Encoding.ASCII.GetBytes("#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n");
        var file = new TestableBmsFile { path = @"C:\Charts\mismatch.bms" };
        file.SetHash(md5);
        file.SetSha256(sha256);
        var snapshot = new ChartFileSnapshot(file.path, bytes, DateTime.UtcNow, md5, sha256);
        ChartInfoBuildTarget target = ChartInfoBuildTargetMapper.Create(
            ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false));
        LR2SongDBExtended.chart_info incompatibleRow = CreateChartInfoRow(
            sha256,
            new string('e', 32),
            BmsLibraryDbGateway.CurrentChartInfoParserVersion);

        ChartInfoBuildService.ChartInfoSnapshotBuildResult result = new ChartInfoBuildService().EvaluateSnapshot(
            snapshot,
            target,
            incompatibleRow,
            hasCurrentParseFailure: false);

        Assert.IsTrue(result.ParseFailed);
        Assert.IsFalse(result.CurrentRowSkipped);
        Assert.IsNotNull(result.ParseFailureRow);
        Assert.AreEqual(md5, result.ParseFailureRow.md5);
        Assert.IsFalse(result.ParseFailureRow.message.Contains('\r'));
        Assert.IsFalse(result.ParseFailureRow.message.Contains('\n'));
        Assert.IsTrue(result.ParseFailureRow.message.Length <= 1024);
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_ParseFailureCanBePersistedWithSong()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-target.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                gateway,
                [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(0, result.SuccessCount);
            Assert.IsTrue(string.IsNullOrWhiteSpace(file.sha256));
            Assert.AreEqual(1, result.StorageApplications.Count);
            var mutationOwner = new CatalogMutationOwner(
                new CatalogStorageRowsOwner(),
                new CatalogOwnedCollectionOwner(),
                gateway);
            CatalogChartInfoStorageWriteReceipt receipt = mutationOwner.ApplyChartInfoStorageWrite(
                new CatalogChartInfoStorageWriteRequest(
                    result.StorageApplications.Select(application => application.CreateBmsPersistenceCopy()),
                    result.StorageApplications.Select(application => application.CreateBmsonPersistenceCopy()),
                    new CatalogChartInfoWriteRequest(
                        chartInfoRows: result.ChartInfoRows,
                        parseFailureRows: result.ParseFailureRows,
                        parseFailureDeleteMd5s: result.ParseFailureDeleteMd5s)));
            Assert.IsTrue(receipt.Applied);
            foreach (ChartInfoStorageApplication application in result.StorageApplications)
            {
                application.ApplyCommitted(result.DigestChanges);
            }
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.hash).Single();
            Assert.AreEqual(file.sha256, failure.sha256);
            Assert.AreEqual(chartPath, failure.path);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual("InvalidDataException", failure.exception_type);
            Assert.IsFalse(failure.parse_timeout_ms.HasValue);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void DeferredChartInfoHydration_IndexesExistingRowsForBmsAndBmson()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string bmsSha = new('1', 64);
            string bmsonSha = new('2', 64);
            var file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "hydrated.bms")
            };
            file.SetHash(new string('a', 32));
            file.SetSha256(bmsSha);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempRootPath, "hydrated.bmson"),
                md5 = new string('b', 32),
                sha256 = bmsonSha
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            LR2SongDBExtended.chart_info bmsRow = CreateChartInfoRow(bmsSha, file.hash, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            LR2SongDBExtended.chart_info bmsonRow = CreateChartInfoRow(bmsonSha, bmsonSong.md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            gateway.UpsertChartInfos([bmsRow, bmsonRow]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
            {
                BMSFiles = [file],
                BmsonSongs = [bmsonSong]
            };

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: false);

            Assert.IsTrue(WaitForChartInfoHydration(library), "chart_info hydration did not complete.");
            Assert.IsFalse(library.ChartInfoHydrationRunning);
            Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
            Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
            LR2SongDBExtended.chart_info resolvedBmsRow = library.ResolveChartInfo(file.sha256, file.hash);
            LR2SongDBExtended.chart_info resolvedBmsonRow = library.ResolveChartInfo(bmsonSong.sha256, bmsonSong.md5);
            Assert.IsNotNull(resolvedBmsRow);
            Assert.AreEqual(bmsSha, resolvedBmsRow.sha256);
            Assert.IsNotNull(resolvedBmsonRow);
            Assert.AreEqual(bmsonSha, resolvedBmsonRow.sha256);
            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);

            InvokeDeferredChartInfoHydration(library, "unit_test_repeat", queueFullBackfillAfterHydration: false);

            Assert.IsTrue(WaitForChartInfoHydration(library), "second chart_info hydration did not complete.");
            Assert.AreEqual(2, library.ChartInfoHydrationTotalCount);
            Assert.AreEqual(0, library.ChartInfoHydrationAppliedCount);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void DeferredChartInfoHydration_UsesActualDataInBothModesAndSkipsFullBackfillWhenCurrent(bool operationModeLr2Db)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current.bms");
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(md5);
            file.SetSha256(sha);
            LR2SongDBExtended.chart_info row = CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            row.speedchange = "120.0,0.0;240.0,1000.0";
            row.lanenotes = "1,2,3,4";
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_info));
            }
            bool originalOperationMode = Settings.Default.OperationModeLR2DB;
            try
            {
                Settings.Default.OperationModeLR2DB = operationModeLr2Db;
                var options = new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = operationModeLr2Db,
                };
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(options);
                    using var songDb = new LR2SongDBExtended(songDbPath);
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
                {
                    BMSFiles = [file]
                };

                Assert.IsFalse(library.ChartInfoIndexHydrated);
                Assert.AreEqual(0, library.ChartInfoIndexVersion);
                Assert.IsNull(library.ResolveChartInfo(sha, md5));

                InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: true);

                Assert.IsTrue(WaitForChartInfoHydration(library), "chart_info hydration did not complete.");
                Assert.IsTrue(library.ChartInfoIndexHydrated);
                Assert.IsTrue(library.ChartInfoIndexVersion > 0);
                Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
                Assert.AreEqual(1, library.ChartInfoBackfillCompletedVersion);
                Assert.AreEqual(1, library.ChartInfoHydrationTotalCount);

                LR2SongDBExtended.chart_info resolved = library.ResolveChartInfo(sha, md5);

                Assert.IsNotNull(resolved);
                Assert.AreEqual(sha, resolved.sha256);
                AssertChartInfoDisplayProjectionEquivalent(row, resolved);
            }
            finally
            {
                Settings.Default.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CatalogChartInfoOwner_ActualDataAllCurrentSnapshotSkipsCandidateSummary(bool operationModeLr2Db)
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha256 = new('1', 64);
            string chartPath = Path.Combine(tempRootPath, "current-snapshot.bms");
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(md5);
            file.SetSha256(sha256);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, md5);
                songDb.InsertOrReplace(CreateChartDigestRow(md5, sha256), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(
                    CreateChartInfoRow(sha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion),
                    typeof(LR2SongDBExtended.chart_info));
            }

            var gateway = new BmsLibraryDbGateway(songDbPath);
            var storageRowsOwner = new CatalogStorageRowsOwner();
            storageRowsOwner.ReplaceBmsRows([file]);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            ownedCollectionOwner.EnsureCurrent(storageRowsOwner);
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, gateway);
            var logs = new ConcurrentQueue<string>();
            var events = new ConcurrentQueue<CatalogChartInfoOwnerEvent>();
            var owner = new CatalogChartInfoOwner(
                _ => { },
                () => false,
                (_, _) => false,
                null,
                logs.Enqueue);
            owner.ConfigureWorkflow(
                gateway,
                mutationOwner,
                storageRowsOwner,
                ownedCollectionOwner,
                logs.Enqueue,
                events.Enqueue);

            owner.QueueDeferredHydration("unit_test_all_current", queueFullBackfillAfterHydration: true);

            Assert.IsTrue(
                SpinWait.SpinUntil(
                    () => owner.ChartInfoHydrationCompletedVersion == owner.ChartInfoHydrationRequestedVersion
                        && owner.ChartInfoBackfillCompletedVersion == owner.ChartInfoBackfillRequestedVersion
                        && !owner.ChartInfoHydrationRunning
                        && !owner.ChartInfoBackfillRunning,
                    10000),
                "chart-info hydration/backfill skip did not complete.");
            Assert.IsTrue(logs.Any(message => message.StartsWith("chart_info_backfill skipped reason=hydration_all_current", StringComparison.Ordinal)));
            Assert.IsFalse(logs.Any(message => message.StartsWith("chart_info_backfill candidate_summary_start", StringComparison.Ordinal)));
            Assert.AreEqual(0, owner.ChartInfoBackfillTotalCount);
            Assert.AreEqual(0, owner.ChartInfoBackfillProcessedCount);
            Assert.IsTrue(events.Any(ownerEvent =>
                ownerEvent.Kind == CatalogChartInfoOwnerEventKind.StartupMemoryCheckpoint
                && ownerEvent.CheckpointStage == "chart_info_backfill"
                && ownerEvent.CheckpointStatus == "skipped"));
        });
    }

    [TestMethod]
    public void CatalogChartInfoOwner_InlinePublicationOrdersDigestIndexesBeforeSessionIndexAndEvents()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "publication-order.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE publication order\r\n#PLAYLEVEL 7\r\n#BPM 130\r\n#00111:01\r\n",
                Encoding.ASCII);
            var file = new TestableBmsFile { path = chartPath };
            file.SetHash(new string('a', 32));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var storageRowsOwner = new CatalogStorageRowsOwner();
            storageRowsOwner.ReplaceBmsRows([file]);
            var ownedCollectionOwner = new CatalogOwnedCollectionOwner();
            ownedCollectionOwner.EnsureCurrent(storageRowsOwner);
            var mutationOwner = new CatalogMutationOwner(storageRowsOwner, ownedCollectionOwner, gateway);
            var eventKinds = new List<CatalogChartInfoOwnerEventKind>();
            CatalogChartInfoOwner owner = null;
            owner = new CatalogChartInfoOwner(_ => { }, () => false, (_, _) => false, null, _ => { });
            owner.ConfigureWorkflow(
                gateway,
                mutationOwner,
                storageRowsOwner,
                ownedCollectionOwner,
                _ => { },
                ownerEvent =>
                {
                    eventKinds.Add(ownerEvent.Kind);
                    if (ownerEvent.Kind == CatalogChartInfoOwnerEventKind.DigestIndexesPrepared)
                    {
                        Assert.AreEqual(0, owner.ChartInfoIndexVersion);
                        Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
                    }
                    if (ownerEvent.Kind is CatalogChartInfoOwnerEventKind.IndexChanged
                        or CatalogChartInfoOwnerEventKind.DigestChanges)
                    {
                        Assert.IsTrue(owner.ChartInfoIndexVersion > 0);
                        Assert.IsNotNull(owner.ResolveChartInfo(file.sha256, file.hash));
                    }
                });

            ChartInfoInlineBuildResult result = owner.BuildInline(
                "publication_order",
                [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

            CollectionAssert.AreEqual(
                new[]
                {
                    CatalogChartInfoOwnerEventKind.DigestIndexesPrepared,
                    CatalogChartInfoOwnerEventKind.IndexChanged,
                    CatalogChartInfoOwnerEventKind.WarningPresentationChanged,
                    CatalogChartInfoOwnerEventKind.DigestChanges
                },
                eventKinds);
            Assert.IsTrue(result.DigestChanges.Count > 0);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void DeferredChartInfoHydration_MissingCurrentRowBackfillsRegardlessOfCompletedLr2Status(bool operationModeLr2Db)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "missing-current.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 13\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            file.SetSha256(digest.sha256);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, chartPath, file.hash);
                songDb.InsertOrReplace(CreateChartDigestRow(file.hash, file.sha256), typeof(LR2SongDBExtended.chart_digest_map));
                if (operationModeLr2Db)
                {
                    string signature = Lr2SongDbSyncSignatureBuilder.Build(new BmsLibraryOptionsSnapshot { OperationModeLR2DB = true });
                    Lr2SongDbSyncStatusService.MarkCompleted(songDb, signature, "unit-test", 1, DateTime.UtcNow);
                }
            }
            bool originalOperationMode = Settings.Default.OperationModeLR2DB;
            try
            {
                Settings.Default.OperationModeLR2DB = operationModeLr2Db;
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
                {
                    BMSFiles = [file]
                };

                InvokeDeferredChartInfoHydration(library, "unit_test_missing", queueFullBackfillAfterHydration: true);

                Assert.IsTrue(WaitForChartInfoHydration(library), "chart_info hydration/backfill did not complete.");
                Assert.IsTrue(WaitForChartInfoBackfill(library), "chart_info backfill did not complete.");
                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.sha256));
                Assert.AreEqual(13, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                Assert.AreEqual(13, file.level);
            }
            finally
            {
                Settings.Default.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void DeferredChartInfoHydration_SkipsFullBackfillWhenNoCandidates()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeDeferredChartInfoHydration(library, "unit_test", queueFullBackfillAfterHydration: true);

            Assert.IsTrue(WaitForChartInfoHydration(library), "chart_info hydration did not complete.");
            Assert.AreEqual(1, library.ChartInfoBackfillRequestedVersion);
            Assert.AreEqual(1, library.ChartInfoBackfillCompletedVersion);
            Assert.IsFalse(library.ChartInfoBackfillRunning);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public void LoadChartInfoHydrationData_UsesRawProjectionAndPreservesCurrentness()
    {
        string previousMode = Environment.GetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE");
        Environment.SetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE", null);
        try
        {
            WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
            {
                var gateway = new BmsLibraryDbGateway(songDbPath);
                string currentMd5 = new('a', 32);
                string staleMd5 = new('b', 32);
                string currentFailureMd5 = new('c', 32);
                string staleFailureMd5 = new('d', 32);
                string currentSha = new('1', 64);
                string staleSha = new('2', 64);
                string currentFailureSha = new('3', 64);
                string staleFailureSha = new('4', 64);
                LR2SongDBExtended.chart_info currentRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
                currentRow.level = null;
                currentRow.difficulty = 3;
                currentRow.difficulty_defined = false;
                currentRow.mainbpm = 123.5;
                currentRow.total = null;
                currentRow.total_defined = true;
                currentRow.density = 12.25;
                currentRow.speedchange_count = 2;
                currentRow.updated_at = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                LR2SongDBExtended.chart_info staleRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
                staleRow.charthash = new string('e', 64);
                staleRow.distribution = "1,2,3";
                staleRow.speedchange = "120.0,0.0;240.0,1.0";
                staleRow.lanenotes = "1,2,3,4";

                using (var songDb = new LR2SongDBExtended(songDbPath))
                {
                    BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                    songDb.InsertOrReplace(currentRow, typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(staleRow, typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(
                        CreateChartInfoParseFailureRow(currentFailureMd5, currentFailureSha, Path.Combine(tempRootPath, "current-failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null),
                        typeof(LR2SongDBExtended.chart_info_parse_failure));
                    songDb.InsertOrReplace(
                        CreateChartInfoParseFailureRow(staleFailureMd5, staleFailureSha, Path.Combine(tempRootPath, "stale-timeout.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 500),
                        typeof(LR2SongDBExtended.chart_info_parse_failure));
                }

                ChartInfoHydrationLoadResult result = gateway.LoadChartInfoHydrationData(TimeSpan.FromMilliseconds(1000));

                Assert.AreEqual("raw_string_display", result.MaterializeMode);
                Assert.AreEqual(2, result.ChartInfoRows);
                Assert.AreEqual(2, result.ParseFailureRows);
                Assert.AreEqual(4, result.RawRows);
                Assert.AreEqual(2, result.ChartInfoBySha256.Count);
                Assert.IsTrue(result.CurrentChartInfoSha256s.Contains(currentSha));
                Assert.IsFalse(result.CurrentChartInfoSha256s.Contains(staleSha));
                Assert.IsTrue(result.CurrentParseFailureMd5s.Contains(currentFailureMd5));
                Assert.IsFalse(result.CurrentParseFailureMd5s.Contains(staleFailureMd5));
                AssertChartInfoDisplayProjectionEquivalent(currentRow, result.ChartInfoBySha256[currentSha]);
                AssertChartInfoDisplayProjectionEquivalent(staleRow, result.ChartInfoBySha256[staleSha]);
                Assert.AreEqual(currentRow.updated_at, result.ChartInfoBySha256[currentSha].updated_at);
                Assert.AreEqual(staleRow.updated_at, result.ChartInfoBySha256[staleSha].updated_at);

                Dictionary<string, LR2SongDBExtended.chart_info> fullRows = gateway.LoadChartInfosBySha256([currentSha, staleSha]);

                AssertChartInfoEquivalent(currentRow, fullRows[currentSha]);
                AssertChartInfoEquivalent(staleRow, fullRows[staleSha]);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMS_CHART_INFO_HYDRATION_LOAD_MODE", previousMode);
        }
    }

    [TestMethod]
    public void GetChartInfoBackfillCandidateSummary_ClassifiesCurrentFailureAndStaleRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            string currentMd5 = new('a', 32);
            string missingDigestMd5 = new('b', 32);
            string staleMd5 = new('c', 32);
            string failureMd5 = new('d', 32);
            string currentSha = new('1', 64);
            string staleSha = new('2', 64);
            string failureSha = new('3', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "current.bms"), currentMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "missing-digest.bms"), missingDigestMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "stale.bms"), staleMd5);
                InsertSongForSummary(songDb, Path.Combine(tempRootPath, "failure.bms"), failureMd5);
                songDb.InsertOrReplace(CreateChartDigestRow(currentMd5, currentSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartDigestRow(staleMd5, staleSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartDigestRow(failureMd5, failureSha), typeof(LR2SongDBExtended.chart_digest_map));
                songDb.InsertOrReplace(CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(
                    CreateChartInfoParseFailureRow(failureMd5, failureSha, Path.Combine(tempRootPath, "failure.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null),
                    typeof(LR2SongDBExtended.chart_info_parse_failure));
            }

            ChartInfoBackfillCandidateSummary summary = gateway.GetChartInfoBackfillCandidateSummary(TimeSpan.FromSeconds(30));

            Assert.AreEqual(4, summary.BmsOwnerCount);
            Assert.AreEqual(0, summary.BmsonOwnerCount);
            Assert.AreEqual(1, summary.CurrentChartInfoOwnerCount);
            Assert.AreEqual(1, summary.CurrentParseFailureOwnerCount);
            Assert.AreEqual(1, summary.MissingDigestOwnerCount);
            Assert.AreEqual(0, summary.MissingChartInfoOwnerCount);
            Assert.AreEqual(1, summary.StaleChartInfoOwnerCount);
            Assert.AreEqual(2, summary.CandidateOwnerCount);
        });
    }

    [TestMethod]
    public void InstallChartPackages_AddsBmsAndBuildsInlineChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceBms");
            string installDir = Path.Combine(tempRootPath, "InstalledBms");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "install.bms");
            File.WriteAllText(
                sourceChartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE install bms\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 12\r\n"
                    + "#DIFFICULTY 3\r\n"
                    + "#DEFEXRANK 120\r\n"
                    + "#EXLEVEL 9\r\n"
                    + "#RANK 3\r\n"
                    + "#TOTAL 300\r\n"
                    + "#BMP01 bg.png\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00104:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            BMSFile pendingChart = BMSFile.CreateBMSFileFromFile(sourceChartPath);
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsFile(pendingChart))]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            BMSFile installedFile = library.BMSFiles.Single();
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + installedFile.path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + installedFile.hash + "' AND sha256 = '" + installedFile.sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedFile.sha256 + "';"));
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", installedFile.path).Single();
            Assert.AreEqual(12, song.level);
            Assert.AreEqual(3, song.difficulty);
            Assert.AreEqual(120, song.maxbpm);
            Assert.AreEqual(120, song.minbpm);
            Assert.AreEqual(5, song.mode);
            Assert.AreEqual(1, song.bga);
            Assert.AreEqual(9, song.exlevel);
            Assert.AreEqual(1, song.longnote);
            Assert.AreEqual(4, song.karinotes);
            Assert.IsNotNull(library.ResolveChartInfo(installedFile.sha256, installedFile.hash));
        });
    }

    [TestMethod]
    public void InstallChartPackages_AddsBmsonAndBuildsInlineChartInfo()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceBmson");
            string installDir = Path.Combine(tempRootPath, "InstalledBmson");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "install.bmson");
            File.WriteAllText(
                sourceChartPath,
                "{"
                    + "\"version\":\"1.0.0\","
                    + "\"info\":{\"title\":\"install bmson\",\"level\":1,\"mode_hint\":\"beat-7k\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LR2SongDBExtended.bmson_song pendingSong = BmsonSongParser.Parse(sourceChartPath);
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ChartFileProjection.FromBmsonSong(pendingSong))]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            LR2SongDBExtended.bmson_song installedSong = library.BmsonSongs.Single();
            Assert.IsNotNull(library.ResolveChartInfo(installedSong.sha256, installedSong.md5));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE path = '" + installedSong.path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedSong.sha256 + "';"));
        });
    }

    [TestMethod]
    public void InstallChartPackages_ChartInfoParseFailurePersistsRecordWithoutBlockingInstall()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceBadBms");
            string installDir = Path.Combine(tempRootPath, "InstalledBadBms");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "bad-install.bms");
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE bad install\r\n#00111:01\r\n", Encoding.ASCII);
            PackageChartEntry pendingChart = PackageChartEntry.FromPath(sourceChartPath);
            var package = ChartPackage.FromChartEntries([pendingChart]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            BMSFile installedFile = library.BMSFiles.Single();
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + installedFile.path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", installedFile.hash).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual("InvalidDataException", failure.exception_type);
        });
    }

    [TestMethod]
    public void ChartInfoParseFailedChartFiles_ProjectsCurrentFailuresAsWarningShims()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var bmsFile = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "bad.bms")
            };
            bmsFile.SetHash(new string('a', 32));
            bmsFile.SetSha256(new string('1', 64));
            var staleBmsFile = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "stale.bms")
            };
            staleBmsFile.SetHash(new string('d', 32));
            staleBmsFile.SetSha256(new string('4', 64));
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempRootPath, "bad.bmson"),
                folder = tempRootPath,
                md5 = new string('b', 32),
                sha256 = new string('2', 64),
                title = "bad bmson",
                artist = "artist"
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(bmsFile.hash, bmsFile.sha256, bmsFile.path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "BMS initial BPM is not defined or invalid.", null),
                CreateChartInfoParseFailureRow(bmsonSong.md5, bmsonSong.sha256, bmsonSong.path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "JsonReaderException", "bad json", null),
                CreateChartInfoParseFailureRow(new string('c', 32), string.Empty, Path.Combine(tempRootPath, "missing.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "missing", null),
                CreateChartInfoParseFailureRow(staleBmsFile.hash, staleBmsFile.sha256, staleBmsFile.path, 0, "parse_failed", "InvalidDataException", "old", null)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
            {
                BMSFiles = [bmsFile, staleBmsFile],
                BmsonSongs = [bmsonSong]
            };

            List<ChartFile> rows = [.. library.ChartInfoParseFailedChartFiles];

            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEqual(
                new[] { bmsFile.path, bmsonSong.path }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                rows.Select(row => row.Path).ToArray());
            foreach (ChartFile row in rows)
            {
                Assert.IsTrue(row.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.IsTrue(ChartWarningProjectionFormatter.HasHighlightedWarning(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual("[1] メタデータ解析エラー", ChartWarningProjectionFormatter.BuildDigestText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                StringAssert.Contains(ChartWarningProjectionFormatter.BuildTooltipText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), "メタデータ解析に失敗しました。");

                LibraryChartRow materializedRow = LibraryChartRow.FromChartFile(row);
                Assert.IsTrue(materializedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.IsTrue(materializedRow.HasHighlightedWarning);
                Assert.AreEqual("[1] メタデータ解析エラー", materializedRow.WarningDigestText);

                ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(row);
                LibraryChartRow virtualMaterializedRow = LibraryChartRow.FromChartFile(sourceRow.Chart);
                Assert.IsTrue(virtualMaterializedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.AreEqual("[1] メタデータ解析エラー", virtualMaterializedRow.WarningDigestText);
            }
            Assert.IsFalse(bmsFile.Warnings.Contains(ChartWarningKind.ChartInfoParseFailure));
            Assert.IsFalse(staleBmsFile.Warnings.Contains(ChartWarningKind.ChartInfoParseFailure));
        });
    }

    [TestMethod]
    public void RemoveChartInfoParseFailuresByMd5_RemovesFailureRowsAndPublishesWarningRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string sharedMd5 = new('a', 32);
            string sharedSha256 = new('1', 64);
            var bmsFile = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "bad.bms")
            };
            bmsFile.SetHash(sharedMd5);
            bmsFile.SetSha256(sharedSha256);
            var bmsonSong = new LR2SongDBExtended.bmson_song
            {
                path = Path.Combine(tempRootPath, "bad.bmson"),
                folder = tempRootPath,
                md5 = sharedMd5,
                sha256 = sharedSha256,
                title = "bad bmson"
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
            }
            gateway.UpsertSongs([bmsFile]);
            gateway.UpsertBmsonSongs([bmsonSong]);
            gateway.UpsertChartInfos([CreateChartInfoRow(sharedSha256, sharedMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(sharedMd5, sharedSha256, bmsFile.path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
            {
                BMSFiles = [bmsFile],
                BmsonSongs = [bmsonSong]
            };
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };
            Assert.AreEqual(2, library.ChartInfoParseFailedChartFiles.Count());
            InvokeDeferredChartInfoHydration(
                library,
                "parse_failure_remove_current_info",
                queueFullBackfillAfterHydration: false);
            Assert.IsTrue(WaitForChartInfoHydration(library));
            int hydrationRequestedVersion = library.ChartInfoHydrationRequestedVersion;
            int backfillRequestedVersion = library.ChartInfoBackfillRequestedVersion;
            refreshNotificationChanged = 0;

            library.RemoveChartInfoParseFailuresByMd5([sharedMd5, sharedMd5.ToUpperInvariant(), " "]);

            Assert.AreEqual(hydrationRequestedVersion, library.ChartInfoHydrationRequestedVersion);
            Assert.AreEqual(backfillRequestedVersion, library.ChartInfoBackfillRequestedVersion);
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.AreEqual(1, refreshNotificationChanged);
            Assert.AreEqual(0, library.ChartInfoParseFailedChartFiles.Count());
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", sharedMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE hash = ?;", sharedMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE md5 = ?;", sharedMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE md5 = ?;", sharedMd5));
            Assert.IsNotNull(library.ResolveChartInfo(sharedSha256, sharedMd5));
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public void RemoveChartInfoParseFailuresByMd5_RetriesOnNextStartupInBothModes(bool operationModeLr2Db)
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "retry-after-delete.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 8\r\n#BPM 140\r\n#00111:01\r\n", Encoding.ASCII);
            BMSFile parsed = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile { path = chartPath };
            file.SetHash(parsed.hash);
            file.SetSha256(parsed.sha256);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertSongs([file]);
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    file.hash,
                    file.sha256,
                    chartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "retry me",
                    null)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
            {
                BMSFiles = [file]
            };
            bool originalOperationMode = Settings.Default.OperationModeLR2DB;
            try
            {
                Settings.Default.OperationModeLR2DB = operationModeLr2Db;
                InvokeDeferredChartInfoHydration(library, "failure_is_current", queueFullBackfillAfterHydration: true);
                Assert.IsTrue(WaitForChartInfoBackfill(library));
                using (var beforeDelete = new LR2SongDBExtended(songDbPath))
                {
                    Assert.AreEqual(0L, beforeDelete.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
                }
                int hydrationRequestedVersion = library.ChartInfoHydrationRequestedVersion;
                int backfillRequestedVersion = library.ChartInfoBackfillRequestedVersion;

                library.RemoveChartInfoParseFailuresByMd5([file.hash]);

                Assert.AreEqual(hydrationRequestedVersion, library.ChartInfoHydrationRequestedVersion);
                Assert.AreEqual(backfillRequestedVersion, library.ChartInfoBackfillRequestedVersion);
                var nextStartupFile = new TestableBmsFile { path = chartPath };
                nextStartupFile.SetHash(parsed.hash);
                nextStartupFile.SetSha256(parsed.sha256);
                var nextStartupLibrary = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService())
                {
                    BMSFiles = [nextStartupFile]
                };

                InvokeDeferredChartInfoHydration(
                    nextStartupLibrary,
                    "next_startup_after_failure_delete",
                    queueFullBackfillAfterHydration: true);
                Assert.IsTrue(WaitForChartInfoHydration(nextStartupLibrary));
                Assert.IsTrue(WaitForChartInfoBackfill(nextStartupLibrary));
                using var verify = new LR2SongDBExtended(songDbPath);
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.sha256));
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", file.hash));
                Assert.AreEqual(8, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                Assert.AreEqual(8, nextStartupFile.level);
            }
            finally
            {
                Settings.Default.OperationModeLR2DB = originalOperationMode;
            }
        });
    }

    [TestMethod]
    public void NormalizeChartInfoParseFailureMd5s_RemovesBlankDuplicatesAndNormalizesCase()
    {
        CollectionAssert.AreEqual(
            new[] { new string('a', 32), new string('b', 32) },
            new ChartInfoParseFailureRemovalRequest([null, " ", new string('A', 32), new string('a', 32), " " + new string('B', 32) + " "]).Md5s.ToArray());
    }

    [TestMethod]
    public void BackfillChartInfos_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(new string('a', 32));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.DigestFailedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_Sha256OnlyFailurePersistsEvaluatorComputedMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "sha-only-bad.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = tempRootPath,
                md5 = null,
                sha256 = snapshot.Sha256,
                title = "bad bmson"
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [],
                [song]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailurePersistedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info_parse_failure failure = verify
                .Query<LR2SongDBExtended.chart_info_parse_failure>(
                    "SELECT * FROM chart_info_parse_failure WHERE md5 = ?;",
                    snapshot.Md5)
                .Single();
            Assert.AreEqual(snapshot.Sha256, failure.sha256);
            Assert.AreEqual(chartPath, failure.path);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_Sha256OnlySuccessClearsFailureByEvaluatorComputedMd5()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "sha-only-success.bmson");
            File.WriteAllText(
                chartPath,
                "{\"version\":\"1.0.0\",\"info\":{\"title\":\"success\",\"level\":5,\"mode_hint\":\"beat-7k\",\"init_bpm\":150,\"judge_rank\":100,\"total\":100,\"resolution\":240},\"lines\":[{\"y\":0}],\"sound_channels\":[{\"notes\":[{\"x\":1,\"y\":0}]}]}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            var song = new LR2SongDBExtended.bmson_song
            {
                path = chartPath,
                folder = tempRootPath,
                md5 = null,
                sha256 = snapshot.Sha256,
                title = "success"
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    snapshot.Md5,
                    snapshot.Sha256,
                    chartPath,
                    parserVersion: 0,
                    "parse_failed",
                    "InvalidDataException",
                    "stale failure",
                    null)
            ]);

            ChartInfoBackfillResult result = BackfillChartInfos(
                new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1),
                gateway,
                [],
                [song]);

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.FailureClearedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(
                0L,
                verify.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;",
                    snapshot.Md5));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_SkipsCurrentPersistedParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-skip.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            var firstFile = new TestableBmsFile
            {
                path = chartPath
            };
            firstFile.SetHash(new string('a', 32));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var firstService = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult first = BackfillChartInfos(firstService,
                gateway,
                [firstFile],
                [],
                null,
                null);

            Assert.AreEqual(1, first.ParseFailedCount);
            Assert.AreEqual(1, first.FailurePersistedCount);

            int readCount = 0;
            var secondFile = new TestableBmsFile
            {
                path = chartPath
            };
            secondFile.SetHash(firstFile.hash);
            var secondService = new ChartInfoBuildService(delegate (string path)
            {
                readCount++;
                return File.ReadAllBytes(path);
            }, workerCountOverride: 1);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult second = BackfillChartInfos(secondService,
                gateway,
                [secondFile],
                [],
                (current, total, target) => { },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(0, second.TargetCount);
            Assert.AreEqual(1, second.FailureSkippedCount);
            Assert.AreEqual(0, second.ParseFailedCount);
            Assert.AreEqual(0, second.FailedCount);
            Assert.AreEqual(0, readCount);
            Assert.AreEqual(0, second.FileReadCount);
            Assert.AreEqual(0L, second.FileReadBytes);
            Assert.IsFalse(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(message => message.Contains("parseFailureSkipped=1")));
        });
    }

    [TestMethod]
    public void ChartInfoInlineBuildService_SkipsCurrentPersistedParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-targeted-skip.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            file.SetHash(snapshot.Md5);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.hash, string.Empty, chartPath, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "JsonReaderException", "bad json", null)
            ]);
            var service = new ChartInfoInlineBuildService(new ChartInfoBuildService(), parserDegree: 1);

            ChartInfoInlineBuildResult result = service.BuildForExistingCharts(
                gateway,
                [ChartFileProjection.FromBmsFile(file, includeWarningSnapshot: false)]);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.FailureSkippedCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(0, result.SuccessCount);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReparsesStalePersistedParseFailureAndUpdatesRecord()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-stale.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(new string('a', 32));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.hash, string.Empty, chartPath, 0, "parse_failed", "JsonReaderException", "old failure", null)
            ]);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                null);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.FailureSkippedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.hash).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreNotEqual("old failure", failure.message);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReparsesShorterTimeoutFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-timeout-stale.bmson");
            File.WriteAllText(chartPath, "not json", Encoding.ASCII);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(new string('a', 32));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.hash, string.Empty, chartPath, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 0)
            ]);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, parseTimeoutOverride: TimeSpan.FromSeconds(1));

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                null);

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(0, result.FailureSkippedCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.hash).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.IsFalse(failure.parse_timeout_ms.HasValue);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_SuccessClearsStaleParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "good-clear.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.hash, string.Empty, chartPath, 0, "parse_failed", "InvalidDataException", "old failure", null)
            ]);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1);

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                null);

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(1, result.FailureClearedCount);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE md5 = ?;", file.hash));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", file.hash));
        });
    }

    [TestMethod]
    public void BackfillChartInfos_ReadFailureLogsWarnImmediately()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var file = new TestableBmsFile
            {
                path = Path.Combine(tempRootPath, "missing-file.bms")
            };
            file.SetHash(new string('a', 32));
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            var service = new ChartInfoBuildService(delegate
            {
                throw new IOException("read boom");
            }, workerCountOverride: 1);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ReadFailedCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(0, result.FileReadCount);
            Assert.AreEqual(0L, result.FileReadBytes);
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any(message => message.StartsWith("WARN chart_info_backfill read_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    [TestMethod]
    public void ParseBytesDetailed_ZeroTimeoutThrowsDedicatedTimeout()
    {
        string text = "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        ChartInfoParser.ChartInfoParseTimeoutException ex = Assert.ThrowsException<ChartInfoParser.ChartInfoParseTimeoutException>(delegate
        {
            ChartInfoParser.ParseBytesDetailed(bytes, ".bms", new string('a', 32), new string('b', 64), null, TimeSpan.Zero);
        });

        StringAssert.Contains(ex.Message, "timed out");
    }

    [TestMethod]
    public void BackfillChartInfos_TimeoutStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "timeout.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, commitChunkSizeOverride: 2, parseTimeoutOverride: TimeSpan.Zero);
            var logs = new ConcurrentQueue<string>();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message => logs.Enqueue("INFO " + message),
                message => logs.Enqueue("WARN " + message));

            Assert.AreEqual(1, result.TargetCount);
            Assert.AreEqual(1, result.ParseFailedCount);
            Assert.AreEqual(1, result.TimeoutFailedCount);
            Assert.AreEqual(1, result.FailedCount);
            Assert.AreEqual(1, result.DigestBackfilledCount);
            Assert.AreEqual(0, result.BackfilledCount);
            Assert.IsFalse(string.IsNullOrWhiteSpace(file.sha256));
            Assert.IsTrue(logs.Any(message => message.Contains("exception=\"ChartInfoParseTimeoutException\"")));
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.hash + "' AND sha256 = '" + file.sha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.hash).Single();
            Assert.AreEqual(file.sha256, failure.sha256);
            Assert.AreEqual("timeout", failure.failure_kind);
            Assert.AreEqual("ChartInfoParseTimeoutException", failure.exception_type);
            Assert.AreEqual(0, failure.parse_timeout_ms);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_CommitsInChunksAndLogsPhaseBoundaries()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            List<TestableBmsFile> files = [];
            for (int index = 0; index < 5; index++)
            {
                string chartPath = Path.Combine(tempRootPath, "chunk-" + index.ToString(CultureInfo.InvariantCulture) + ".bms");
                File.WriteAllText(
                    chartPath,
                    "#PLAYER 1\r\n#TITLE chunk " + index.ToString(CultureInfo.InvariantCulture) + "\r\n#BPM " + (120 + index).ToString(CultureInfo.InvariantCulture) + "\r\n#00111:01\r\n",
                    Encoding.ASCII);
                var digest = BMSFile.CreateBMSFileFromFile(chartPath);
                var file = new TestableBmsFile
                {
                    path = chartPath
                };
                file.SetHash(digest.hash);
                files.Add(file);
            }
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 2, commitChunkSizeOverride: 2);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                files,
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(5, result.TargetCount);
            Assert.AreEqual(5, result.ProcessedCount);
            Assert.AreEqual(5, result.DigestBackfilledCount);
            Assert.AreEqual(5, result.BackfilledCount);
            Assert.AreEqual(3, result.CommitChunks);
            int expectedReaderCount = ChartFileReadPipelinePolicy.ResolveReaderDegree(Environment.ProcessorCount, result.TargetCount);
            Assert.AreEqual(expectedReaderCount, result.ReaderCount);
            Assert.AreEqual(ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(result.WorkerCount, result.ReaderCount), result.QueueCapacity);
            Assert.AreEqual(3, logs.Count(message => message.StartsWith("INFO chart_info_backfill db_commit_chunk_done", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill start", StringComparison.Ordinal)
                && message.Contains("readerCount=" + result.ReaderCount)
                && message.Contains("queueCapacity=" + result.QueueCapacity)));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill parse_done", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill slow_parse_top", StringComparison.Ordinal)));
            int parseDoneIndex = logs.FindIndex(message => message.StartsWith("INFO chart_info_backfill parse_done", StringComparison.Ordinal));
            int finalSummaryIndex = logs.FindIndex(message => message.StartsWith("INFO chart_info_backfill total=", StringComparison.Ordinal));
            int lastCommitDoneIndex = logs.FindLastIndex(message => message.StartsWith("INFO chart_info_backfill db_commit_chunk_done", StringComparison.Ordinal));
            Assert.IsTrue(parseDoneIndex >= 0);
            Assert.IsTrue(finalSummaryIndex > parseDoneIndex);
            Assert.IsTrue(finalSummaryIndex > lastCommitDoneIndex);
            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    [TestMethod]
    public void ChartInfoBuildService_DefaultCommitChunkSize_Is10000()
    {
        Assert.AreEqual(10000, ChartInfoBuildService.ResolveDefaultCommitChunkSize());
    }

    [TestMethod]
    public void UpsertChartInfoBackfillChunk_UsesExplicitSqlAndReplacesRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoBackfillChunk(null, null);
            gateway.UpsertChartInfoBackfillChunk([], []);

            string md5A = new('a', 32);
            string md5B = new('b', 32);
            string shaA = new('1', 64);
            string shaB = new('2', 64);
            string shaC = new('3', 64);
            var updatedAt = new DateTime(2026, 4, 27, 1, 2, 3, DateTimeKind.Utc);
            LR2SongDBExtended.chart_info rowA = CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            rowA.level = null;
            rowA.difficulty = null;
            rowA.difficulty_defined = false;
            rowA.mainbpm = null;
            rowA.total = null;
            rowA.total_defined = false;
            rowA.updated_at = updatedAt;
            LR2SongDBExtended.chart_info rowB = CreateChartInfoRow(shaB, md5B, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            rowB.level = 7;

            gateway.UpsertChartInfoBackfillChunk([new ChartDigestBackfillEntry(md5A, shaA)], null);
            gateway.UpsertChartInfoBackfillChunk(null, [rowA]);
            gateway.UpsertChartInfoBackfillChunk([new ChartDigestBackfillEntry(md5B, shaB)], [rowB]);

            LR2SongDBExtended.chart_info replacement = CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            replacement.level = 12;
            replacement.difficulty_defined = true;
            replacement.total_defined = true;
            replacement.updated_at = updatedAt.AddMinutes(1);
            gateway.UpsertChartInfoBackfillChunk(
                [new ChartDigestBackfillEntry(md5A, shaC)],
                [replacement]);

            using var verify = new LR2SongDBExtended(songDbPath);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5A, shaC));
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info storedA = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", shaA).Single();
            LR2SongDBExtended.chart_info storedB = verify.Query<LR2SongDBExtended.chart_info>("SELECT * FROM chart_info WHERE sha256 = ?;", shaB).Single();
            Assert.AreEqual(12, storedA.level);
            Assert.IsTrue(storedA.difficulty_defined);
            Assert.IsTrue(storedA.total_defined);
            Assert.AreEqual(replacement.updated_at, storedA.updated_at);
            Assert.AreEqual(7, storedB.level);
        });
    }

    [TestMethod]
    public void BackfillChartInfos_JavaIntWrappedTimelineLogsParseDiagnosticAsSuccess()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "java-int-wrap-backfill.bms");
            File.WriteAllText(
                chartPath,
                "#BPM 1\r\n"
                    + "#STOP01 90000\r\n"
                    + "#00009:" + string.Concat(Enumerable.Repeat("01", 40)) + "\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            var digest = BMSFile.CreateBMSFileFromFile(chartPath);
            var file = new TestableBmsFile
            {
                path = chartPath
            };
            file.SetHash(digest.hash);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            var service = new ChartInfoBuildService(File.ReadAllBytes, workerCountOverride: 1, commitChunkSizeOverride: 1);
            List<string> logs = [];
            object logsSync = new();

            ChartInfoBackfillResult result = BackfillChartInfos(service,
                gateway,
                [file],
                [],
                null,
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("INFO " + message);
                    }
                },
                message =>
                {
                    lock (logsSync)
                    {
                        logs.Add("WARN " + message);
                    }
                });

            Assert.AreEqual(1, result.BackfilledCount);
            Assert.AreEqual(0, result.ParseFailedCount);
            Assert.AreEqual(0, result.FailedCount);
            Assert.IsTrue(logs.Any(message => message.StartsWith("INFO chart_info_backfill parse_diagnostic", StringComparison.Ordinal)
                && message.Contains("code=\"BMS_JAVA_INT_TIME_WRAP\"")
                && message.Contains("parseFailed=false")));
            Assert.IsFalse(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void RetryIfLockedOrBusy_RetriesRealDatabaseContention()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker_RetryIfLockedOrBusy_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var holder = new SQLiteConnectionEx(databasePath);
            holder.Execute("CREATE TABLE retry_probe (id INTEGER PRIMARY KEY, value TEXT);");
            holder.Execute("BEGIN IMMEDIATE;");

            using var contender = new SQLiteConnectionEx(databasePath);
            contender.BusyTimeout = TimeSpan.Zero;
            int attempts = 0;
            SQLiteException exception = Assert.ThrowsException<SQLiteException>(delegate
            {
                SQLiteConnectionEx.RetryIfLockedOrBusy(delegate
                {
                    attempts++;
                    contender.Execute("INSERT INTO retry_probe (value) VALUES ('blocked');");
                }, null, 0u);
            });

            Assert.AreEqual(1, attempts);
            Assert.IsTrue(
                exception.Result == SQLite3.Result.Busy || exception.Result == SQLite3.Result.Locked,
                "The provider must expose the real SQLite contention result.");
            holder.Execute("ROLLBACK;");
        }
        finally
        {
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }


    private static long CountChartInfoRows(string songDbPath, string sha256)
    {
        using var songDb = new LR2SongDBExtended(songDbPath);
        return songDb.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + sha256 + "';");
    }

    private static void AssertJavaDoubleParseBits(string value, string expectedHexBits)
    {
        Assert.AreEqual(expectedHexBits, JavaDoubleParserJdk17.ParseDoubleBits(value).ToString("x16", CultureInfo.InvariantCulture), value);
    }

    private static void AssertChartInfoEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.charthash, actual.charthash);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.bga, actual.bga);
        Assert.AreEqual(expected.exlevel, actual.exlevel);
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

    private static void AssertChartInfoDisplayProjectionEquivalent(LR2SongDBExtended.chart_info expected, LR2SongDBExtended.chart_info actual)
    {
        Assert.AreEqual(expected.sha256, actual.sha256);
        Assert.AreEqual(expected.md5, actual.md5);
        Assert.AreEqual(expected.level, actual.level);
        Assert.AreEqual(expected.difficulty, actual.difficulty);
        Assert.AreEqual(expected.difficulty_defined, actual.difficulty_defined);
        Assert.AreEqual(expected.mainbpm, actual.mainbpm);
        Assert.AreEqual(expected.maxbpm, actual.maxbpm);
        Assert.AreEqual(expected.minbpm, actual.minbpm);
        Assert.AreEqual(expected.length, actual.length);
        Assert.AreEqual(expected.mode, actual.mode);
        Assert.AreEqual(expected.judge, actual.judge);
        Assert.AreEqual(expected.bga, actual.bga);
        Assert.AreEqual(expected.exlevel, actual.exlevel);
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
        Assert.AreEqual(expected.speedchange_count, actual.speedchange_count);
        Assert.AreEqual(expected.parser_version, actual.parser_version);
        Assert.IsNull(actual.charthash);
        Assert.IsNull(actual.distribution);
        Assert.IsNull(actual.speedchange);
        Assert.IsNull(actual.lanenotes);
    }

    private static void AssertNullableDouble(double? expected, double? actual, string message)
    {
        if (!expected.HasValue || !actual.HasValue)
        {
            Assert.AreEqual(expected.HasValue, actual.HasValue, message);
            return;
        }
        Assert.IsTrue(Math.Abs(expected.Value - actual.Value) <= 0.000001, message + " expected=" + expected.Value.ToString("R", CultureInfo.InvariantCulture) + " actual=" + actual.Value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static string ResolveInstalledSevenZipPathOrInconclusive()
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

        Assert.Inconclusive("7-Zip CLI is not installed.");
        return null;
    }

    private static string RunSevenZip(string sevenZipPath, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process, "Failed to start 7z.exe.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(30000), "7z.exe timed out." + Environment.NewLine + stdout + Environment.NewLine + stderr);
        Assert.AreEqual(0, process.ExitCode, stdout + Environment.NewLine + stderr);
        return stdout + Environment.NewLine + stderr;
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
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
            difficulty_defined = true,
            mainbpm = 120.0,
            maxbpm = 120.0,
            minbpm = 120.0,
            length = 0,
            mode = 7,
            judge = 100,
            bga = 0,
            exlevel = 0,
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

    private static LR2SongDBExtended.chart_digest_map CreateChartDigestRow(string md5, string sha256)
    {
        return new LR2SongDBExtended.chart_digest_map
        {
            md5 = md5,
            sha256 = sha256
        };
    }

    private static void InsertSongForSummary(LR2SongDBExtended songDb, string path, string md5)
    {
        var file = new TestableBmsFile
        {
            path = path
        };
        file.SetHash(md5);
        songDb.InsertOrReplace(file, typeof(LR2SongDB.song));
    }

    private static LR2SongDBExtended.chart_info_parse_failure CreateChartInfoParseFailureRow(string md5, string sha256, string path, int parserVersion, string failureKind, string exceptionType, string message, int? parseTimeoutMs)
    {
        return new LR2SongDBExtended.chart_info_parse_failure
        {
            md5 = md5,
            sha256 = sha256,
            path = path,
            parser_version = parserVersion,
            failure_kind = failureKind,
            exception_type = exceptionType,
            message = message,
            parse_timeout_ms = parseTimeoutMs,
            updated_at = DateTime.UtcNow
        };
    }

    private static RealChartInfoExpectedRow CreateExpectedCompatibilityRow(string sha256, string charthash)
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
        File.WriteAllBytes(songDbPath, []);
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

    private static string CreateChartInfoMetadataBundle(
        string tempRootPath,
        IEnumerable<LR2SongDBExtended.chart_info> chartInfos,
        IEnumerable<LR2SongDBExtended.chart_digest_map>? chartDigests = null)
    {
        string sourceDirectoryPath = Path.Combine(tempRootPath, Guid.NewGuid().ToString("N") + "-source");
        Directory.CreateDirectory(sourceDirectoryPath);
        string sourcePath = Path.Combine(sourceDirectoryPath, "song.db");
        File.WriteAllBytes(sourcePath, []);
        using (var sourceDb = new LR2SongDBExtended(sourcePath))
        {
            BmsLibraryDbGateway.EnsureBmsonSchema(sourceDb);
            BmsLibraryDbGateway.EnsureChartInfoSchema(sourceDb);
            foreach (LR2SongDBExtended.chart_info row in chartInfos ?? [])
            {
                sourceDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_info));
            }
            foreach (LR2SongDBExtended.chart_digest_map row in chartDigests ?? [])
            {
                sourceDb.InsertOrReplace(row, typeof(LR2SongDBExtended.chart_digest_map));
            }
        }

        string bundlePath = Path.Combine(tempRootPath, Guid.NewGuid().ToString("N") + "-chart-info-metadata.db");
        ChartInfoExportRunner.Export(new ChartInfoExportOptions
        {
            SourceSongDbPath = sourcePath,
            OutputDbPath = bundlePath
        });
        return bundlePath;
    }

    private static void InvokeInstallChartPackages(BMSLibrary library, IEnumerable<ChartPackage> packages, string installDirectory)
    {
        MethodInfo method = typeof(BMSLibrary).GetMethod("installChartPackages", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "installChartPackages method was not found.");
        method.Invoke(
            library,
            [
                packages,
                installDirectory,
                null,
                new List<ChartPackage>(),
                null,
                null,
                false,
                false,
                null,
                null
            ]);
    }

    private static bool WaitForChartInfoBackfill(BMSLibrary library)
    {
        return SpinWait.SpinUntil(
            () => library.ChartInfoBackfillRequestedVersion > 0
                && library.ChartInfoBackfillCompletedVersion == library.ChartInfoBackfillRequestedVersion
                && !library.ChartInfoBackfillRunning,
            10000);
    }

    private static void InvokeDeferredChartInfoHydration(BMSLibrary library, string reason, bool queueFullBackfillAfterHydration)
    {
        MethodInfo method = typeof(BMSLibrary).GetMethod("QueueDeferredChartInfoHydration", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, "QueueDeferredChartInfoHydration method was not found.");
        method.Invoke(library, [reason, queueFullBackfillAfterHydration]);
    }

    private static bool WaitForChartInfoHydration(BMSLibrary library)
    {
        return SpinWait.SpinUntil(
            () => library.ChartInfoHydrationRequestedVersion > 0
                && library.ChartInfoHydrationCompletedVersion == library.ChartInfoHydrationRequestedVersion
                && !library.ChartInfoHydrationRunning,
            10000);
    }

    private static int ReadPositiveIntEnvironmentVariable(string name, int defaultValue)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }
        return defaultValue;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private static ChartInfoBackfillResult BackfillChartInfos(
        ChartInfoBuildService service,
        BmsLibraryDbGateway gateway,
        IEnumerable<BMSFile>? currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song>? currentBmsonSongs,
        Action<int, int, string>? reportProgress = null,
        Action<string>? logInstallPerformance = null,
        Action<string>? logInstallPerformanceWarn = null,
        Action<ChartInfoStorageCommitPublication>? storageCommitPublished = null,
        IReadOnlyDictionary<string, LR2SongDBExtended.chart_info>? existingRowsSnapshot = null)
    {
        return service.BackfillChartInfos(
            gateway,
            CreateChartSnapshot(currentFiles, currentBmsonSongs),
            reportProgress,
            logInstallPerformance,
            logInstallPerformanceWarn,
            storageCommitPublished,
            existingRowsSnapshot,
            request => ApplyChartInfoStorageRequest(gateway, request));
    }

    private static CatalogChartInfoStorageWriteReceipt ApplyChartInfoStorageRequest(
        BmsLibraryDbGateway gateway,
        CatalogChartInfoStorageWriteRequest request,
        Action<LR2SongDBExtended>? afterWrites = null)
    {
        if (request == null || !request.HasChanges)
        {
            return CatalogChartInfoStorageWriteReceipt.NotApplied;
        }
        Lr2ChartInfoSongProjectionWriteResult songProjectionResult =
            Lr2ChartInfoSongProjectionWriteResult.Empty;
        gateway.ExecuteSongDbTransaction(songDb =>
        {
            if (request.BmsRows.Count > 0)
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureSongLookupIndexes(songDb);
                Lr2SongDbWriter.UpsertGeneratedSongs(songDb, request.BmsRows);
            }
            if (request.ChartInfoSongProjections.Count > 0)
            {
                songProjectionResult = Lr2SongDbWriter.UpdateChartInfoSongProjections(
                    songDb,
                    request.ChartInfoSongProjections);
            }
            if (request.BmsonRows.Count > 0)
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                foreach (LR2SongDBExtended.bmson_song row in request.BmsonRows)
                {
                    songDb.InsertOrReplace(row, typeof(LR2SongDBExtended.bmson_song));
                }
            }
            BmsLibraryDbGateway.UpsertChartInfoBackfillChunk(
                songDb,
                request.ChartInfo.DigestEntries,
                request.ChartInfo.ChartInfoRows,
                request.ChartInfo.ParseFailureRows,
                request.ChartInfo.ParseFailureDeleteMd5s);
            afterWrites?.Invoke(songDb);
        });
        return new CatalogChartInfoStorageWriteReceipt(
            applied: true,
            request.BmsRows.Count,
            request.BmsonRows.Count,
            new CatalogChartInfoWriteReceipt(
                applied: request.ChartInfo.HasChanges,
                request.ChartInfo.DigestEntries.Count,
                request.ChartInfo.ChartInfoRows.Count,
                request.ChartInfo.ParseFailureRows.Count,
                request.ChartInfo.ParseFailureDeleteMd5s.Count),
            songProjectionResult);
    }

    private static List<ChartFile> CreateChartSnapshot(
        IEnumerable<BMSFile>? currentFiles,
        IEnumerable<LR2SongDBExtended.bmson_song>? currentBmsonSongs)
    {
        List<ChartFile> charts = [];
        charts.AddRange(ChartFileProjection.FromBmsFiles(currentFiles, includeWarningSnapshot: false));
        charts.AddRange(ChartFileProjection.FromBmsonSongs(currentBmsonSongs, includeWarningSnapshot: false));
        return charts;
    }

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }

    private sealed class ChartInfoMetadataBundleManifestRow
    {
        public string bundle_id { get; set; } = string.Empty;

        public int format_version { get; set; }

        public string generated_at { get; set; } = string.Empty;

        public int chart_info_schema_version { get; set; }

        public int chart_info_parser_version { get; set; }

        public int chart_info_count { get; set; }

        public int chart_digest_count { get; set; }
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

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            return defaultResult == MessageBoxResult.None ? MessageBoxResult.OK : defaultResult;
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

    private sealed class EdgeCaseSampleChartRow
    {
        public int fixture_id { get; set; }

        public string source_path { get; set; } = string.Empty;

        public string fixture_path { get; set; } = string.Empty;

        public string reason { get; set; } = string.Empty;

        public string md5 { get; set; } = string.Empty;

        public string sha256 { get; set; } = string.Empty;

        public int has_beatoraja_song { get; set; }
    }

    private sealed class BmsonCompatibilityDiffCounts
    {
        private readonly List<string> samples = [];

        public int CoreDiffs { get; private set; }

        public int ChartHashDiffs { get; private set; }

        public int LengthDiffs { get; private set; }

        public int DistributionDiffs { get; private set; }

        public int BpmIntegerDiffs { get; private set; }

        public int TotalDiffs { get; private set; }

        public int DensityDiffs { get; private set; }

        public int PeakDensityDiffs { get; private set; }

        public int EndDensityDiffs { get; private set; }

        public int MainBpmDiffs { get; private set; }

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
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

        private void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
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

        private static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoBmsonDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        private static int CountCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
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

        private static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        private static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }
    }

    private sealed class CompatibilityDiffCounts
    {
        private readonly List<string> samples = [];

        private readonly List<FieldDiffRecord> fieldDiffRecords = [];

        private readonly Dictionary<string, int> fieldDiffCounts = new(StringComparer.Ordinal);

        private readonly List<long> parseElapsedMilliseconds = [];

        private readonly List<ParseRecord> parseRecords = [];

        private readonly List<string> skippedSamples = [];

        public int ParsedCount { get; private set; }

        public int TimeoutCount { get; private set; }

        public int ParseFailureCount { get; private set; }

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

        public int CoreDiffs { get; private set; }

        public int ChartHashDiffs { get; private set; }

        public int LengthDiffs { get; private set; }

        public int DensityDiffs { get; private set; }

        public int PeakDensityDiffs { get; private set; }

        public int EndDensityDiffs { get; private set; }

        public int DistributionDiffs { get; private set; }

        public int SpeedChangeDiffs { get; private set; }

        public int BpmIntegerDiffs { get; private set; }

        public int TotalDiffs => fieldDiffRecords.Count;

        public static string GetReportDirectory()
        {
            string configured = Environment.GetEnvironmentVariable("BMS_TEST_PRODUCTION_DIFF_REPORT_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }
            return Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoProductionDiff");
        }

        public void AddParsed(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString, long elapsedMilliseconds)
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

        public void Add(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual, string chartString)
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

        public string WriteReport(string name)
        {
            string directory = Path.Combine(GetReportDirectory(), name);
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

        private void AddCoreDiffs(RealChartInfoExpectedRow expected, LR2SongDBExtended.chart_info actual)
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

        private static bool IsRandomFeature(int? feature)
        {
            return ((feature ?? 0) & 4) != 0;
        }

        private void CompareCoreField(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, bool equals)
        {
            if (equals)
            {
                return;
            }
            CoreDiffs++;
            AddFieldDiff(field, expected, expectedValue, actualValue);
        }

        private void AddFieldDiff(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue)
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

        private void AddSample(string field, RealChartInfoExpectedRow expected, string expectedValue, string actualValue, string chartString)
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

        private void RecordParse(RealChartInfoExpectedRow expected, long elapsedMilliseconds, string status)
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

        private void AddSkippedSample(string status, RealChartInfoExpectedRow expected, long elapsedMilliseconds, Exception exception)
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

        private IEnumerable<string> GetSlowTopRecords()
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

        private static string WriteChartStringArtifact(RealChartInfoExpectedRow expected, string chartString)
        {
            string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ChartInfoDiffs");
            Directory.CreateDirectory(directory);
            string artifactPath = Path.Combine(directory, expected.sha256 + ".csharp.chart.txt");
            File.WriteAllText(artifactPath, chartString ?? string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return artifactPath;
        }

        private static bool NullableDoubleEquals(double? expected, double? actual)
        {
            if (!expected.HasValue || !actual.HasValue)
            {
                return expected.HasValue == actual.HasValue;
            }
            return Math.Abs(expected.Value - actual.Value) <= 0.000001;
        }

        private static bool LevelEqualsBeatoraja(int? expected, int? actual)
        {
            if (expected == actual)
            {
                return true;
            }
            return expected.GetValueOrDefault() == 0 && !actual.HasValue;
        }

        private static string FormatValue(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatValue(int? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string FormatValue(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static void WriteLines(string path, IEnumerable<string> lines)
        {
            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private sealed class ParseRecord
        {
            public int FixtureId { get; set; }

            public string Sha256 { get; set; } = string.Empty;

            public string FixturePath { get; set; } = string.Empty;

            public long ElapsedMilliseconds { get; set; }

            public string Status { get; set; } = string.Empty;
        }

        private sealed class FieldDiffRecord
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
