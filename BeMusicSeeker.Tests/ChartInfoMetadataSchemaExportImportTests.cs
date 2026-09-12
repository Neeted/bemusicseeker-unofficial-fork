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

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// Owns chart-info schema, bundle, importer, and catalog-mutation behavior.
/// </summary>
[TestClass]
public sealed class ChartInfoMetadataSchemaExportImportTests
{
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
            Directory.CreateDirectory(Path.GetDirectoryName(chartPath)!);
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
            string[] importedEntries = Directory.GetFileSystemEntries(Path.GetDirectoryName(archivedPath)!);
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
            string[] importedEntries = Directory.GetFileSystemEntries(Path.GetDirectoryName(archivedPath)!);
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

}
