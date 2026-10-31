using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using ChartInfoExportTool;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報のスキーマ、bundle、起動時取込み、カタログ反映の境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoMetadataSchemaExportImportTests
{
    /// <summary>既存song表を変えず詳細・失敗・取込み履歴の保存表と検索索引を作成し、保存schema・版の欠落や既存LR2列の破壊を検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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

            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
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

    /// <summary>難度の明示状態を持たない旧詳細表を再構築し、互換不能な旧行を現在schemaの成功行として残す誤りを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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

            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.IsTrue(verify.Query<ColumnNameRow>("PRAGMA table_info(chart_info);").Any(row => row.name == "difficulty_defined"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'chart_info_parse_failure';"));
            Assert.IsTrue(gateway.IsChartInfoSchemaCurrent());
        });
    }

    /// <summary>現在解析版の詳細だけを実bundleへ出力し、詳細からのdigest補完と既存digestを保持して旧行の混入やmanifest件数のずれを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(shaB, md5B, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1)), typeof(LR2SongDBExtended.chart_info));
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
            using var verify = new SQLiteConnection(outputPath, SQLiteOpenFlags.ReadOnly, storeDateTimeAsTicks: true);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", shaA, md5A));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", shaB));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5A, shaA));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5C, shaC));
            ChartInfoMetadataBundleManifestRow manifest = verify.Query<ChartInfoMetadataBundleManifestRow>("SELECT * FROM chart_info_metadata_bundle;").Single();
            Assert.AreEqual(ChartInfoExportRunner.MetadataBundleFormatVersion, manifest.format_version);
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoSchemaVersion, manifest.chart_info_schema_version);
            Assert.AreEqual(21, manifest.chart_info_parser_version);
            Assert.AreEqual(1, manifest.chart_info_count);
            Assert.AreEqual(2, manifest.chart_digest_count);
        });
    }

    /// <summary>詳細表のない実DBからのexportを失敗へ伝え、空の正常bundleとして配布する誤りを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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

    /// <summary>実7zへ出力したrootのmetadata DBを起動入口から取り込み、保存行の復元とarchive移動までを接続して配布形式の不整合を検出する。</summary>
    /// <remarks>実7zが必要な配布・起動接続。専用環境で実行し、未導入時は失敗とする。他caseの注入抽出ではarchive形式自体を保証しない。</remarks>
    [TestMethod]
    [TestCategory("EnvironmentIntegration")]
    public void ChartInfoExport_CreatesArchiveWithRootMetadataDbAndStartupImporterCanImport()
    {
        string sevenZipPath = ResolveInstalledSevenZipPath();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string md5 = new('a', 32);
            string sha = new('1', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
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
                null);

            using var verify = new LR2SongDBExtended(importDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.IsFalse(File.Exists(appArchivePath));
            Assert.IsTrue(File.Exists(Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName)));
        });
    }

    /// <summary>DB出力先とarchive出力先が同じ要求を拒否し、生成したDBをarchiveで上書きする誤りを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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

    /// <summary>archive要求の7z実行ファイルがない場合に明示失敗とし、DBだけの出力へ黙って切り替える誤りを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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

    /// <summary>bundleから未登録・旧版行と対応失敗を更新し、現在行・既存digest・無関係の失敗は保持して同一bundle再取込みを省く保証の退行を検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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
            BeMusicSeeker.Models.ChartDetails missingBundleRow = CreateChartInfoRow(missingSha, missingMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            missingBundleRow = missingBundleRow with { level = 5 };
            BeMusicSeeker.Models.ChartDetails staleBundleRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            staleBundleRow = staleBundleRow with { level = 8 };
            BeMusicSeeker.Models.ChartDetails currentBundleRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            currentBundleRow = currentBundleRow with { level = 12 };
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
                // 再解析・取込みの対象条件を作る入力行だけを一括準備する。
                songDb.RunInTransaction(() =>
                {
                    BeMusicSeeker.Models.ChartDetails staleLocalRow = CreateChartInfoRow(staleSha, staleMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion - 1);
                    staleLocalRow = staleLocalRow with { level = 1 };
                    BeMusicSeeker.Models.ChartDetails currentLocalRow = CreateChartInfoRow(currentSha, currentMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
                    currentLocalRow = currentLocalRow with { level = 3 };
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(staleLocalRow), typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(currentLocalRow), typeof(LR2SongDBExtended.chart_info));
                    songDb.InsertOrReplace(CreateChartDigestRow(existingDigestMd5, localDigestSha), typeof(LR2SongDBExtended.chart_digest_map));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoParseFailureRow(missingMd5, missingSha, "missing.bms", BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad bpm", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoParseFailureRow(currentMd5, currentSha, "current.bms", BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad bpm", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
                    songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoParseFailureRow(unrelatedFailureMd5, unrelatedFailureSha, "unrelated.bms", BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad bpm", null)), typeof(LR2SongDBExtended.chart_info_parse_failure));
                });
            }

            string bundleSha256 = ChartFileContentReader.ComputeSha256(bundlePath);
            var gateway = new BmsLibraryDbGateway(songDbPath);
            ChartInfoMetadataBundleImportResult result = gateway.ImportChartInfoMetadataBundle(bundlePath, bundleSha256);
            ChartInfoMetadataBundleImportResult repeated = gateway.ImportChartInfoMetadataBundle(bundlePath, bundleSha256);

            Assert.IsFalse(result.Skipped);
            Assert.IsTrue(repeated.Skipped);
            Assert.AreEqual("already_imported", repeated.SkipReason);
            Assert.AreEqual(3, result.SourceChartInfoCount);
            Assert.AreEqual(5, result.SourceDigestCount);
            Assert.AreEqual(2, result.ImportedChartInfoCount);
            Assert.AreEqual(4, result.ImportedDigestCount);
            Assert.AreEqual(2, result.FailureClearedCount);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(5, verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", missingSha).Single().level);
            Assert.AreEqual(8, verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", staleSha).Single().level);
            Assert.AreEqual(3, verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", currentSha).Single().level);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", digestOnlyMd5, digestOnlySha));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", missingMd5, missingSha));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", existingDigestMd5, localDigestSha));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", existingDigestMd5, bundleDigestSha));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 IN (?, ?);", missingMd5, currentMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", unrelatedFailureMd5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_import_history WHERE bundle_sha256 = ? AND parser_version = ?;", bundleSha256, BmsLibraryDbGateway.CurrentChartInfoParserVersion));
        });
    }

    /// <summary>manifestを持たない実DBをbundleとして拒否し、不正schemaの正常取込みを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
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

            Assert.ThrowsException<InvalidDataException>(() => gateway.ImportChartInfoMetadataBundle(bundlePath, ChartFileContentReader.ComputeSha256(bundlePath)));
        });
    }

    /// <summary>取り込んだdigest対応を通常songロードへ接続し、所持譜面のSHAと対応表へ保存値が渡らない誤りを検出する。</summary>
    /// <remarks>実SQLiteのschema・保存行・出力を観測し、モデル内の値だけでは検出できない保存互換を確認する。</remarks>
    [TestMethod]
    public void ImportChartInfoMetadataBundle_ImportedDigestMapIsAppliedByLoadSongTable()
    {

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
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                ChartFile song = ChartTestValues.Empty() with
                {
                    Path = chartPath,
                    Folder = "Songs"
                };
                song = song with { Md5 = md5 };
                songDb.InsertOrReplace(ChartTestValues.CreateBmsStorageRow(song, string.Empty), typeof(LR2SongDB.song));
            }

            new BmsLibraryDbGateway(songDbPath).ImportChartInfoMetadataBundle(bundlePath, ChartFileContentReader.ComputeSha256(bundlePath));

            SongTableLoadResult result = new BmsLibraryInitializationService().LoadSongTable(
                new BmsLibraryDbGateway(songDbPath),
                new BmsLibraryOptionsSnapshot(),
                null,
                null,
                null,
                ex => ex.Message);

            Assert.AreEqual(1, result.LoadedFiles.Count);
            Assert.AreEqual(sha, result.LoadedFiles[0].Sha256);
            Assert.AreEqual(sha, result.ChartDigestMap[md5]);
        });
    }

    /// <summary>DBとarchiveが両方ある起動ではDBだけを取り込み、移動後の再配置も履歴で省いて不要な抽出や再保存を検出する。</summary>
    /// <remarks>実DBとファイル移動を観測し、抽出portは条件を注入して起動時の優先・失敗・回収を分担する。</remarks>
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
            string archivedDbPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName, ChartInfoMetadataBundleStartupImporter.MetadataDbFileName);
            File.Copy(archivedDbPath, appDbPath);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(
                appBaseDirectory,
                new BmsLibraryDbGateway(songDbPath),
                logs.Add);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.IsFalse(File.Exists(appDbPath));
            Assert.IsTrue(File.Exists(archivedDbPath));
            Assert.AreEqual(1, Directory.GetFileSystemEntries(Path.GetDirectoryName(archivedDbPath)!).Length);
            Assert.IsTrue(File.Exists(appArchivePath), "The unused archive should stay in the app root because the db bundle has priority.");
            Assert.IsTrue(logs.Any(message => message.Contains("bundleType=db")));
            Assert.IsFalse(logs.Any(message => message.Contains("bundleType=7z")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_bundle_archive moved") && message.Contains("bundleType=db")));
        });
    }

    /// <summary>archiveを一度だけ抽出・保存・移動してtempと古いcacheを除き、再配置を履歴で省いて入力・保存・回収の取り違えを検出する。</summary>
    /// <remarks>実DBとファイル移動を観測し、抽出portは条件を注入して起動時の優先・失敗・回収を分担する。</remarks>
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
            string importedDirectoryPath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.ImportedMetadataDirectoryName);
            Directory.CreateDirectory(importedDirectoryPath);
            File.WriteAllText(Path.Combine(importedDirectoryPath, "chart-info-metadata.old.7z"), "old archive", Encoding.ASCII);
            Directory.CreateDirectory(Path.Combine(importedDirectoryPath, "old-directory"));
            string archivePath = Path.Combine(appBaseDirectory, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.WriteAllText(archivePath, "archive identity", Encoding.ASCII);
            string archiveSha256 = ChartFileContentReader.ComputeSha256(archivePath);
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
            string archivedPath = Path.Combine(importedDirectoryPath, ChartInfoMetadataBundleStartupImporter.MetadataArchiveFileName);
            File.Copy(archivedPath, archivePath);
            ChartInfoMetadataBundleStartupImporter.TryImportFromBaseDirectory(appBaseDirectory, gateway, logs.Add, extractArchive, createTempDirectory);

            Assert.AreEqual(1, extractCount);
            CollectionAssert.AllItemsAreUnique(tempDirectories);
            Assert.IsTrue(tempDirectories.All(directoryPath => !Directory.Exists(directoryPath)));
            Assert.IsFalse(File.Exists(archivePath));
            Assert.IsTrue(File.Exists(archivedPath));
            string[] importedEntries = Directory.GetFileSystemEntries(importedDirectoryPath);
            Assert.AreEqual(1, importedEntries.Length);
            Assert.AreEqual(archivedPath, importedEntries[0]);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_import_history WHERE bundle_sha256 = ? AND parser_version = ?;", archiveSha256, BmsLibraryDbGateway.CurrentChartInfoParserVersion));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import done") && message.Contains("bundleType=7z") && message.Contains("extractMs=")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import skipped reason=missing_bundle")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_import skipped reason=already_imported")
                && message.Contains("bundleType=7z")
                && !message.Contains("extractedDbPath=")));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_bundle_archive moved") && message.Contains("bundleType=7z")));
        });
    }

    /// <summary>抽出後metadata DBがないarchiveを失敗記録してtempだけ回収し、入力の喪失や成功cacheへの誤移動を検出する。</summary>
    /// <remarks>実DBとファイル移動を観測し、抽出portは条件を注入して起動時の優先・失敗・回収を分担する。</remarks>
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

    /// <summary>取込み後のarchive移動が失敗しても実DB確定を保持し、後処理失敗で保存を取り消す誤りや失敗診断の欠落を検出する。</summary>
    /// <remarks>実DBとファイル移動を観測し、抽出portは条件を注入して起動時の優先・失敗・回収を分担する。</remarks>
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ? AND md5 = ?;", sha, md5));
            Assert.IsTrue(logs.Any(message => message.Contains("chart_info_metadata_bundle_archive failed") && message.Contains("chart-info-metadata.db")));
        });
    }

    /// <summary>所持譜面の実削除でsong・保守・digest対応を消して詳細cacheは保持し、別所持候補でも再利用できる内容情報を削る誤りを検出する。</summary>
    /// <remarks>本番catalog ownerから実SQLiteへ削除を確定し、計算済み削除候補だけの検査では代替しない。</remarks>
    [TestMethod]
    public void ApplyCatalogMutation_LeavesChartInfoRows()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "Songs", "delete.bms")
            };
            file = file with { Md5 = new string('a', 32) };
            file = file with { Sha256 = new string('b', 64) };

            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                BmsLibraryDbGateway.EnsureBmsonSchema(songDb);
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(file), typeof(LR2SongDB.song));
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5 }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(CreateChartInfoRow(file.Sha256, file.Md5, parserVersion: BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
                songDb.InsertOrReplace(new LR2SongDBExtended.chart_digest_map
                {
                    md5 = file.Md5,
                    sha256 = file.Sha256
                }, typeof(LR2SongDBExtended.chart_digest_map));
            }

            var storageRowsOwner = new CatalogOwnedCollectionOwner();
            CatalogChartCollectionSnapshot initialRows = storageRowsOwner.ReplaceChartsAndCaptureSnapshot([file], []);
            file = initialRows.BmsRows.Single();
            var catalogFacts = new LibraryCatalogMutationFacts(
                [OwnedChartRemoveRequest.FromChart(file)],
                [],
                []);
            CatalogMutationReceipt receipt = new CatalogMutationOwner(storageRowsOwner, new BmsLibraryDbGateway(songDbPath))
                .ApplyCatalogMutation(catalogFacts);
            Assert.IsTrue(receipt.Applied);
            Assert.AreEqual(file.Md5, receipt.RemovedCharts.Single(fact => fact.Kind == ChartFileKind.Bms).Md5);

            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + file.Path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = '" + file.Path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.Md5 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + file.Sha256 + "';"));
        });
    }

}
