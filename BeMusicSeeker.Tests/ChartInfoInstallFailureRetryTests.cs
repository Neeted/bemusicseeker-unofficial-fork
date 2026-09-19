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
/// 譜面情報の導入、解析失敗、警告、再評価、DB競合の境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoInstallFailureRetryTests
{
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

    /// <summary>導入時は発見時ではなく、物理導入後に実読取りしたMD5で解析失敗を検索する。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void InstallChartPackages_UsesInstalledSnapshotMd5ForFailureLookup(bool failureMatchesInstalledSnapshot)
    {
        TestResourceInitializer.EnsureJapaneseResources();
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceChangedBms");
            string installDir = Path.Combine(tempRootPath, "InstalledChangedBms");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "changed.bms");
            File.WriteAllText(
                sourceChartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE discovered A\r\n"
                    + "#BPM 120\r\n"
                    + "#PLAYLEVEL 7\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            PackageChartEntry pendingChart = PackageChartEntry.FromPath(sourceChartPath);
            Assert.IsNotNull(pendingChart);
            string discoveryMd5 = pendingChart.Chart.Md5;

            File.WriteAllText(
                sourceChartPath,
                "#PLAYER 1\r\n"
                    + "#TITLE installed B\r\n"
                    + "#BPM 130\r\n"
                    + "#PLAYLEVEL 8\r\n"
                    + "#00111:01\r\n",
                Encoding.ASCII);
            ChartFileSnapshot installedSnapshot = ChartFileContentReader.ReadSnapshot(sourceChartPath);
            Assert.AreNotEqual(discoveryMd5, installedSnapshot.Md5);

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            string failureMd5 = failureMatchesInstalledSnapshot ? installedSnapshot.Md5 : discoveryMd5;
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(
                    failureMd5,
                    installedSnapshot.Sha256,
                    sourceChartPath,
                    BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                    "parse_failed",
                    "InvalidDataException",
                    "persisted failure",
                    null)
            ]);

            var package = ChartPackage.FromChartEntries([pendingChart]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService());

            InvokeInstallChartPackages(library, [package], installDir);

            BMSFile installedFile = library.BMSFiles.Single();
            Assert.AreEqual(installedSnapshot.Md5, installedFile.hash);
            using var verify = new LR2SongDBExtended(songDbPath);
            if (failureMatchesInstalledSnapshot)
            {
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", installedSnapshot.Sha256));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", installedSnapshot.Md5));
            }
            else
            {
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", installedSnapshot.Sha256));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", discoveryMd5));
            }
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

    /// <summary>不正JSONではなく実BMSの詳細解析失敗を対象に、導入を止めず失敗記録を永続化する。</summary>
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

    /// <summary>現在版の実BMS解析失敗を警告へ投影し、表示文言と件数をリソース契約から検証する。</summary>
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
            using var failureDb = new LR2SongDBExtended(songDbPath);

            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEqual(
                new[] { bmsFile.path, bmsonSong.path }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                rows.Select(row => row.Path).ToArray());
            foreach (ChartFile row in rows)
            {
                ChartWarning parseFailureWarning = row.Warnings.Single(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure);
                LR2SongDBExtended.chart_info_parse_failure failure = failureDb
                    .Table<LR2SongDBExtended.chart_info_parse_failure>()
                    .Single(candidate => string.Equals(candidate.md5, row.Md5, StringComparison.OrdinalIgnoreCase));
                string expectedMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.Warning_ChartInfoParseFailure,
                    failure.exception_type,
                    failure.message);
                Assert.IsTrue(row.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.IsTrue(ChartWarningProjectionFormatter.HasHighlightedWarning(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, ChartWarningProjectionFormatter.BuildDigestText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual(expectedMessage, parseFailureWarning.Message);
                StringAssert.Contains(ChartWarningProjectionFormatter.BuildTooltipText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), expectedMessage);

                LibraryChartRow materializedRow = LibraryChartRow.FromChartFile(row);
                Assert.IsTrue(materializedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.IsTrue(materializedRow.HasHighlightedWarning);
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, materializedRow.WarningDigestText);

                ChartListSourceRow sourceRow = ChartListSourceRow.FromChartFile(row);
                LibraryChartRow virtualMaterializedRow = LibraryChartRow.FromChartFile(sourceRow.Chart);
                Assert.IsTrue(virtualMaterializedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, virtualMaterializedRow.WarningDigestText);
            }
            Assert.IsFalse(bmsFile.Warnings.Contains(ChartWarningKind.ChartInfoParseFailure));
            Assert.IsFalse(staleBmsFile.Warnings.Contains(ChartWarningKind.ChartInfoParseFailure));
        });
    }

    [TestMethod]
    public async Task RemoveChartInfoParseFailuresByMd5_RemovesFailureRowsAndPublishesWarningRefresh()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
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
            library.PropertyChanged += delegate (object? sender, System.ComponentModel.PropertyChangedEventArgs args)
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
            await AwaitChartInfoHydrationAsync(library);
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

    /// <summary>失敗行を削除した後のLR2連携・単独動作の次回起動で、実データ照合から再評価する。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [DoNotParallelize]
    public async Task RemoveChartInfoParseFailuresByMd5_RetriesOnNextStartupInBothModes(bool operationModeLr2Db)
    {
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
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
                await AwaitChartInfoBackfillAsync(library);
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
                await AwaitChartInfoHydrationAsync(nextStartupLibrary);
                await AwaitChartInfoBackfillAsync(nextStartupLibrary);
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

    /// <summary>SHA計算済みの実BMSが詳細解析に失敗しても、次回補完用のdigestを確定する。</summary>
    [TestMethod]
    public void BackfillChartInfos_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
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

    /// <summary>現在版の解析失敗は実DBから再利用し、同じファイルを再読しない。</summary>
    [TestMethod]
    public void BackfillChartInfos_SkipsCurrentPersistedParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-skip.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
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

    /// <summary>古い解析版の失敗は同じBMS入力を再解析し、失敗記録を現在版へ更新する。</summary>
    [TestMethod]
    public void BackfillChartInfos_ReparsesStalePersistedParseFailureAndUpdatesRecord()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-stale.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
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

    /// <summary>短い時間上限で記録された失敗は、現在の上限で再解析する。</summary>
    [TestMethod]
    public void BackfillChartInfos_ReparsesShorterTimeoutFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-timeout-stale.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
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

}
