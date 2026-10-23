using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
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
    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>実BMSパッケージを物理導入し、同じ処理内でsong・digest対応・詳細と索引へ反映して別の全件補完待ちや保存漏れを検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [TestMethod]
    public void InstallChartPackages_AddsBmsAndBuildsInlineChartInfo()
    {

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
                    + "#BMP01 bg.png\r\n#00104:01\r\n"
                    + "#00111:0100\r\n"
                    + "#00112:0001\r\n"
                    + "#00116:0100\r\n"
                    + "#00104:0100\r\n"
                    + "#00251:0101\r\n"
                    + "#003D1:01\r\n",
                Encoding.ASCII);
            ChartFile pendingChart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceChartPath));
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingChart))]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            ChartFile installedFile = library.BmsCharts.Single();
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + installedFile.Path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + installedFile.Md5 + "' AND sha256 = '" + installedFile.Sha256 + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedFile.Sha256 + "';"));
            LR2SongDB.song song = verify.Query<LR2SongDB.song>("SELECT * FROM song WHERE path = ?;", installedFile.Path).Single();
            Assert.AreEqual(12, song.level);
            Assert.AreEqual(3, song.difficulty);
            Assert.AreEqual(120, song.maxbpm);
            Assert.AreEqual(120, song.minbpm);
            Assert.AreEqual(5, song.mode);
            Assert.AreEqual(1, song.bga);
            Assert.AreEqual(9, song.exlevel);
            Assert.AreEqual(1, song.longnote);
            Assert.AreEqual(4, song.karinotes);
            Assert.IsNotNull(library.ResolveChartInfo(installedFile.Sha256, installedFile.Md5));
        });
    }

    /// <summary>発見後に変わったBMSの導入後MD5で失敗を照合し、一致する現在失敗は再利用して発見時だけの失敗で新内容の解析を止める誤りを検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [DataTestMethod]
    [DataRow(true)] // 導入後MD5に一致する現在失敗を再利用する。
    [DataRow(false)] // 発見時MD5だけの失敗で新内容の解析を止めない。
    public void InstallChartPackages_UsesInstalledSnapshotMd5ForFailureLookup(bool failureMatchesInstalledSnapshot)
    {

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
            var pendingChart = PackageChartEntry.FromPath(sourceChartPath);
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
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            ChartFile installedFile = library.BmsCharts.Single();
            Assert.AreEqual(installedSnapshot.Md5, installedFile.Md5);
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
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

    /// <summary>実bmsonを物理導入して同じ処理内で専用song表・詳細・索引へ反映し、別の全件補完の予約や形式固有の保存漏れを検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [TestMethod]
    public void InstallChartPackages_AddsBmsonAndBuildsInlineChartInfo()
    {

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
            ChartFile pendingSong = ChartTestValues.ReadBmson(sourceChartPath);
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingSong))]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            ChartFile installedSong = library.BmsonCharts.Single();
            Assert.IsNotNull(library.ResolveChartInfo(installedSong.Sha256, installedSong.Md5));
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE path = '" + installedSong.Path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedSong.Sha256 + "';"));
        });
    }

    /// <summary>初期BPMのない実BMSでも物理導入とsong保存を完了し、詳細だけの失敗履歴を確定して導入全体の中止や失敗分類の消失を検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [TestMethod]
    public void InstallChartPackages_ChartInfoParseFailurePersistsRecordWithoutBlockingInstall()
    {

        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string sourceDir = Path.Combine(tempRootPath, "SourceBadBms");
            string installDir = Path.Combine(tempRootPath, "InstalledBadBms");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(installDir);
            string sourceChartPath = Path.Combine(sourceDir, "bad-install.bms");
            File.WriteAllText(sourceChartPath, "#PLAYER 1\r\n#TITLE bad install\r\n#00111:01\r\n", Encoding.ASCII);
            var pendingChart = PackageChartEntry.FromPath(sourceChartPath);
            var package = ChartPackage.FromChartEntries([pendingChart]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            ChartFile installedFile = library.BmsCharts.Single();
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE path = '" + installedFile.Path.Replace("'", "''") + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", installedFile.Md5).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreEqual("InvalidDataException", failure.exception_type);
        });
    }

    /// <summary>現在版の所持BMS・bmson失敗だけを一覧へ投影し、旧版・未所持の混入や通常・仮想行での警告件数・リソース文言の欠落を検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [TestMethod]
    public void ChartInfoParseFailedChartFiles_ProjectsCurrentFailuresAsWarningShims()
    {

        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            ChartFile bmsFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "bad.bms")
            };
            bmsFile = bmsFile with { Md5 = new string('a', 32) };
            bmsFile = bmsFile with { Sha256 = new string('1', 64) };
            ChartFile staleBmsFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "stale.bms")
            };
            staleBmsFile = staleBmsFile with { Md5 = new string('d', 32) };
            staleBmsFile = staleBmsFile with { Sha256 = new string('4', 64) };
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempRootPath, "bad.bmson"),
                Folder = tempRootPath,
                Md5 = new string('b', 32),
                Sha256 = new string('2', 64),
                RawTitle = "bad bmson",
                RawArtist = "artist"
            };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(bmsFile.Md5, bmsFile.Sha256, bmsFile.Path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "BMS initial BPM is not defined or invalid.", null),
                CreateChartInfoParseFailureRow(bmsonSong.Md5, bmsonSong.Sha256, bmsonSong.Path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "JsonReaderException", "bad json", null),
                CreateChartInfoParseFailureRow(new string('c', 32), string.Empty, Path.Combine(tempRootPath, "missing.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "missing", null),
                CreateChartInfoParseFailureRow(staleBmsFile.Md5, staleBmsFile.Sha256, staleBmsFile.Path, 0, "parse_failed", "InvalidDataException", "old", null)
            ]);
            var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
            {
                BmsCharts = [bmsFile, staleBmsFile],
                BmsonCharts = [bmsonSong]
            };

            List<ChartFile> rows = [.. library.ChartInfoParseFailedChartFiles];
            using var failureDb = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);

            Assert.AreEqual(2, rows.Count);
            CollectionAssert.AreEqual(
                new[] { bmsFile.Path, bmsonSong.Path }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
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
                Assert.IsTrue(ChartWarningProjectionFormatter.HasHighlightedWarning(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, ChartWarningProjectionFormatter.BuildDigestText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual(expectedMessage, parseFailureWarning.Message);
                StringAssert.Contains(ChartWarningProjectionFormatter.BuildTooltipText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false), expectedMessage);

                var materializedRow = LibraryChartRow.FromChartFile(row);
                Assert.IsTrue(materializedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.IsTrue(materializedRow.HasHighlightedWarning);
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, materializedRow.WarningDigestText);

                var sourceRow = ChartListSourceRow.FromChartFile(row);
                var virtualMaterializedRow = LibraryChartRow.FromChartFile(sourceRow.Chart);
                Assert.IsTrue(virtualMaterializedRow.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure));
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, virtualMaterializedRow.WarningDigestText);
            }
        });
    }

    /// <summary>同じMD5の失敗を正規化して削除し、song・bmson・現在詳細を保持して警告更新を一回通知し、同一起動の補完再予約を検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [TestMethod]
    public async Task RemoveChartInfoParseFailuresByMd5_RemovesFailureRowsAndPublishesWarningRefresh()
    {

        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var workers = new ConcurrentBag<Task>();
            Exception? primaryFailure = null;
            try
            {
                string sharedMd5 = new('a', 32);
                string sharedSha256 = new('1', 64);
                ChartFile bmsFile = ChartTestValues.Empty() with
                {
                    Path = Path.Combine(tempRootPath, "bad.bms")
                };
                bmsFile = bmsFile with { Md5 = sharedMd5 };
                bmsFile = bmsFile with { Sha256 = sharedSha256 };
                ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = Path.Combine(tempRootPath, "bad.bmson"),
                    Folder = tempRootPath,
                    Md5 = sharedMd5,
                    Sha256 = sharedSha256,
                    RawTitle = "bad bmson"
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
                    CreateChartInfoParseFailureRow(sharedMd5, sharedSha256, bmsFile.Path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "bad", null)
                ]);
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [bmsFile],
                    BmsonCharts = [bmsonSong]
                };
                library.StartupBackgroundTaskScheduler = (_, _, _, acceptedWork) =>
                {
                    workers.Add(Task.Run(acceptedWork));
                    return true;
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
                await TestUiDispatcherHost.AwaitNotificationAsync(AwaitChartInfoHydrationAsync(library), Task.WhenAll(workers), "failure-removal.hydration");
                await Task.WhenAll(workers);
                await library.OperationAdmission.WaitForIdleAsync();
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
                using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", sharedMd5));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM song WHERE hash = ?;", sharedMd5));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE md5 = ?;", sharedMd5));
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE md5 = ?;", sharedMd5));
                Assert.IsNotNull(library.ResolveChartInfo(sharedSha256, sharedMd5));
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception ex)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = ex;
                    }
                    else
                    {
                        primaryFailure.Data["chart-info-failure.worker-cleanup"] = ex;
                    }
                }
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>現在失敗の明示削除では即時再予約せず、LR2連携・単独双方の次回起動で実解析と保存・同token適用を再開する保証の退行を検出する。</summary>
    /// <remarks>本番ライブラリの導入・警告・受付を実DBへ接続する。設定と入力はcaseごとに所有し、保存・通知を計算値で代替しない。</remarks>
    [DataTestMethod]
    [DataRow(false)] // 単独動作でも次回起動の実データ照合から再評価する。
    [DataRow(true)] // LR2連携の同期状態を解析済みの根拠にしない。
    public async Task RemoveChartInfoParseFailuresByMd5_RetriesOnNextStartupInBothModes(bool operationModeLr2Db)
    {
        await WithTemporarySongDb(async delegate (string tempRootPath, string songDbPath)
        {
            var workers = new ConcurrentBag<Task>();
            Exception? primaryFailure = null;
            bool originalOperationMode = testSettings.OperationModeLR2DB;
            try
            {
                string chartPath = Path.Combine(tempRootPath, "retry-after-delete.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#PLAYLEVEL 8\r\n#BPM 140\r\n#00111:01\r\n", Encoding.ASCII);
                ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                ChartFile file = (ChartTestValues.Empty() with { Path = chartPath });
                file = file with { Md5 = parsed.Md5 };
                file = file with { Sha256 = parsed.Sha256 };
                var gateway = new BmsLibraryDbGateway(songDbPath);
                gateway.UpsertSongs([file]);
                gateway.UpsertChartInfoParseFailures(
                [
                    CreateChartInfoParseFailureRow(
                        file.Md5,
                        file.Sha256,
                        chartPath,
                        BmsLibraryDbGateway.CurrentChartInfoParserVersion,
                        "parse_failed",
                        "InvalidDataException",
                        "retry me",
                        null)
                ]);
                var library = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [file]
                };
                library.StartupBackgroundTaskScheduler = (_, _, _, acceptedWork) =>
                {
                    workers.Add(Task.Run(acceptedWork));
                    return true;
                };
                testSettings.OperationModeLR2DB = operationModeLr2Db;
                InvokeDeferredChartInfoHydration(library, "failure_is_current", queueFullBackfillAfterHydration: true);
                await TestUiDispatcherHost.AwaitNotificationAsync(AwaitChartInfoBackfillAsync(library), Task.WhenAll(workers), "failure-retry.initial-backfill");
                await Task.WhenAll(workers);
                // 完了版の通知と、受理済み背景処理の受付解放は別の終端である。
                await library.OperationAdmission.WaitForIdleAsync();
                using (LR2SongDBExtended beforeDelete = gateway.OpenSongDbReadOnly())
                {
                    Assert.AreEqual(0L, beforeDelete.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
                }
                int hydrationRequestedVersion = library.ChartInfoHydrationRequestedVersion;
                int backfillRequestedVersion = library.ChartInfoBackfillRequestedVersion;

                library.RemoveChartInfoParseFailuresByMd5([file.Md5]);

                Assert.AreEqual(hydrationRequestedVersion, library.ChartInfoHydrationRequestedVersion);
                Assert.AreEqual(backfillRequestedVersion, library.ChartInfoBackfillRequestedVersion);
                ChartFile nextStartupFile = (ChartTestValues.Empty() with { Path = chartPath });
                nextStartupFile = nextStartupFile with { Md5 = parsed.Md5 };
                nextStartupFile = nextStartupFile with { Sha256 = parsed.Sha256 };
                var nextStartupLibrary = new TestBmsLibrary(songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
                {
                    BmsCharts = [nextStartupFile]
                };

                nextStartupLibrary.StartupBackgroundTaskScheduler = (_, _, _, acceptedWork) =>
                {
                    workers.Add(Task.Run(acceptedWork));
                    return true;
                };
                ChartFile ownedAtStartup = nextStartupLibrary.BmsCharts.Single();
                InvokeDeferredChartInfoHydration(
                    nextStartupLibrary,
                    "next_startup_after_failure_delete",
                    queueFullBackfillAfterHydration: true);
                await TestUiDispatcherHost.AwaitNotificationAsync(AwaitChartInfoHydrationAsync(nextStartupLibrary), Task.WhenAll(workers), "failure-retry.next-hydration");
                await TestUiDispatcherHost.AwaitNotificationAsync(AwaitChartInfoBackfillAsync(nextStartupLibrary), Task.WhenAll(workers), "failure-retry.next-backfill");
                await Task.WhenAll(workers);
                await nextStartupLibrary.OperationAdmission.WaitForIdleAsync();
                using LR2SongDBExtended verify = gateway.OpenSongDbReadOnly();
                Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = ?;", file.Sha256));
                Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5));
                Assert.AreEqual(8, verify.ExecuteScalar<int>("SELECT level FROM song WHERE path = ?;", chartPath));
                Assert.AreEqual(8, nextStartupLibrary.BmsCharts.Single().Level);
                Assert.IsNull(ownedAtStartup.Level);
                Assert.AreSame(ownedAtStartup.Token, nextStartupLibrary.BmsCharts.Single().Token);
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
            }
            finally
            {
                try
                {
                    await Task.WhenAll(workers);
                }
                catch (Exception ex)
                {
                    if (primaryFailure is null)
                    {
                        primaryFailure = ex;
                    }
                    else
                    {
                        primaryFailure.Data["chart-info-failure.worker-cleanup"] = ex;
                    }
                }
                testSettings.OperationModeLR2DB = originalOperationMode;
            }
            if (primaryFailure is not null)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }
        });
    }

    /// <summary>削除要求の空値を除きMD5の空白・大小文字を正規化して重複をまとめ、同じ対象の二重削除や有効hashの脱落を検出する。</summary>
    [TestMethod]
    public void NormalizeChartInfoParseFailureMd5s_RemovesBlankDuplicatesAndNormalizesCase()
    {
        CollectionAssert.AreEqual(
            new[] { new string('a', 32), new string('b', 32) },
            new ChartInfoParseFailureRemovalRequest([null, " ", new string('A', 32), new string('a', 32), " " + new string('B', 32) + " "]).Md5s.ToArray());
    }

    /// <summary>詳細解析が失敗しても捕捉SHAのdigest対応を確定し、失敗件数と警告を最終集計前に残して次回照合の手掛かりを失う誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ParseFailureStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = new string('a', 32) };
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
            string committedSha256 = result.DigestChanges.Single().NewSha256;
            Assert.IsFalse(string.IsNullOrWhiteSpace(committedSha256));
            Assert.IsTrue(logs.Count >= 2);
            Assert.IsTrue(logs.Any(message => message.StartsWith("WARN chart_info_backfill parse_failed", StringComparison.Ordinal)));
            StringAssert.StartsWith(logs[logs.Count - 1], "INFO chart_info_backfill total=");
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.Md5 + "' AND sha256 = '" + committedSha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    /// <summary>現在版の保存失敗を同じMD5の補完で再利用して再読込みを省き、失敗を新規発生扱いする誤りや不要な再解析を検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_SkipsCurrentPersistedParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-skip.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile firstFile = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            firstFile = firstFile with { Md5 = new string('a', 32) };
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
            ChartFile secondFile = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            secondFile = secondFile with { Md5 = firstFile.Md5 };
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

    /// <summary>旧解析版の保存失敗を再解析して現在の失敗へ更新し、古い履歴を永続的な省略理由として扱う誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ReparsesStalePersistedParseFailureAndUpdatesRecord()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-stale.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.Md5, string.Empty, chartPath, 0, "parse_failed", "JsonReaderException", "old failure", null)
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5).Single();
            Assert.AreEqual(BmsLibraryDbGateway.CurrentChartInfoParserVersion, failure.parser_version);
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.AreNotEqual("old failure", failure.message);
        });
    }

    /// <summary>現在より短い期限のtimeout履歴は再解析し、今回の非timeout失敗へ更新して古い時間上限で処理を省く誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ReparsesShorterTimeoutFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "bad-timeout-stale.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE bad\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = new string('a', 32) };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.Md5, string.Empty, chartPath, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "timeout", "ChartInfoParseTimeoutException", "old timeout", 0)
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5).Single();
            Assert.AreEqual("parse_failed", failure.failure_kind);
            Assert.IsFalse(failure.parse_timeout_ms.HasValue);
        });
    }

    /// <summary>旧失敗のある有効BMSを補完して詳細を確定し同MD5の失敗を消し、成功後も失敗警告の原因を残す誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_SuccessClearsStaleParseFailure()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "good-clear.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.EnsureChartInfoBackfillSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(file.Md5, string.Empty, chartPath, 0, "parse_failed", "InvalidDataException", "old failure", null)
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE md5 = ?;", file.Md5));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5));
        });
    }

    /// <summary>読取りportのIOExceptionを読取り失敗として集計・最終summary前に警告し、解析失敗履歴へ誤保存する誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_ReadFailureLogsWarnBeforeFinalSummary()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "missing-file.bms")
            };
            file = file with { Md5 = new string('a', 32) };
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info_parse_failure;"));
        });
    }

    /// <summary>解析期限0を専用timeout例外で伝え、通常の解析成功や一般失敗へ分類を置き換える誤りを検出する。</summary>
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

    /// <summary>期限0で詳細がtimeoutしてもdigest対応と期限付き失敗履歴を実保存し、捕捉hashや再評価に必要な失敗分類を失う誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_TimeoutStillPersistsDigest()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            string chartPath = Path.Combine(tempRootPath, "timeout.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#BPM 120\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
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
            string committedSha256 = result.DigestChanges.Single().NewSha256;
            Assert.IsFalse(string.IsNullOrWhiteSpace(committedSha256));
            Assert.IsTrue(logs.Any(message => message.Contains("exception=\"ChartInfoParseTimeoutException\"")));
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = '" + file.Md5 + "' AND sha256 = '" + committedSha256 + "';"));
            Assert.AreEqual(0L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            LR2SongDBExtended.chart_info_parse_failure failure = verify.Query<LR2SongDBExtended.chart_info_parse_failure>("SELECT * FROM chart_info_parse_failure WHERE md5 = ?;", file.Md5).Single();
            Assert.AreEqual(committedSha256, failure.sha256);
            Assert.AreEqual("timeout", failure.failure_kind);
            Assert.AreEqual("ChartInfoParseTimeoutException", failure.exception_type);
            Assert.AreEqual(0, failure.parse_timeout_ms);
        });
    }

    /// <summary>五つの異なる譜面を指定chunkサイズ2で実保存し、三回の確定ログと解析・最終集計の順序を保って対象の欠落や二重保存を検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void BackfillChartInfos_CommitsInChunksAndLogsPhaseBoundaries()
    {
        WithTemporarySongDb(delegate (string tempRootPath, string songDbPath)
        {
            List<ChartFile> files = [];
            for (int index = 0; index < 5; index++)
            {
                string chartPath = Path.Combine(tempRootPath, "chunk-" + index.ToString(CultureInfo.InvariantCulture) + ".bms");
                File.WriteAllText(
                    chartPath,
                    "#PLAYER 1\r\n#TITLE chunk " + index.ToString(CultureInfo.InvariantCulture) + "\r\n#BPM " + (120 + index).ToString(CultureInfo.InvariantCulture) + "\r\n#00111:01\r\n",
                    Encoding.ASCII);
                ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                ChartFile file = ChartTestValues.Empty() with
                {
                    Path = chartPath
                };
                file = file with { Md5 = digest.Md5 };
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
            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(5L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
        });
    }

    /// <summary>空・片側・両側の保存要求とnullable入力を受理して同hashの行を置換し、無関係の行や更新日時・明示状態を取り違える誤りを検出する。</summary>
    /// <remarks>実SQLiteの保存・失敗履歴を観測し、補完結果の件数だけでは確定成功としない。</remarks>
    [TestMethod]
    public void UpsertChartInfoBackfillChunk_AcceptsNullableInputsAndReplacesRows()
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
            BeMusicSeeker.Models.ChartDetails rowA = CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            rowA = rowA with { level = null };
            rowA = rowA with { difficulty = null };
            rowA = rowA with { difficulty_defined = false };
            rowA = rowA with { mainbpm = null };
            rowA = rowA with { total = null };
            rowA = rowA with { total_defined = false };
            rowA = rowA with { updated_at = updatedAt };
            BeMusicSeeker.Models.ChartDetails rowB = CreateChartInfoRow(shaB, md5B, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            rowB = rowB with { level = 7 };

            gateway.UpsertChartInfoBackfillChunk([new ChartDigestBackfillEntry(md5A, shaA)], null);
            gateway.UpsertChartInfoBackfillChunk(null, [rowA]);
            gateway.UpsertChartInfoBackfillChunk([new ChartDigestBackfillEntry(md5B, shaB)], [rowB]);

            BeMusicSeeker.Models.ChartDetails replacement = CreateChartInfoRow(shaA, md5A, BmsLibraryDbGateway.CurrentChartInfoParserVersion);
            replacement = replacement with { level = 12 };
            replacement = replacement with { difficulty_defined = true };
            replacement = replacement with { total_defined = true };
            replacement = replacement with { updated_at = updatedAt.AddMinutes(1) };
            gateway.UpsertChartInfoBackfillChunk(
                [new ChartDigestBackfillEntry(md5A, shaC)],
                [replacement]);

            using var verify = new LR2SongDBExtended(songDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map;"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_digest_map WHERE md5 = ? AND sha256 = ?;", md5A, shaC));
            Assert.AreEqual(2L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info;"));
            BeMusicSeeker.Models.ChartDetails storedA = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", shaA).Single();
            BeMusicSeeker.Models.ChartDetails storedB = verify.Query<BeMusicSeeker.Models.ChartDetails>("SELECT * FROM chart_info WHERE sha256 = ?;", shaB).Single();
            Assert.AreEqual(12, storedA.level);
            Assert.IsTrue(storedA.difficulty_defined);
            Assert.IsTrue(storedA.total_defined);
            Assert.AreEqual(replacement.updated_at, storedA.updated_at);
            Assert.AreEqual(7, storedB.level);
        });
    }

    /// <summary>参照互換の時間wrapを含むBMSを詳細補完の成功件数へ集計し、情報診断を失敗警告や失敗件数へ誤分類する誤りを検出する。</summary>
    /// <remarks>補完入口を実DB窓口へ接続して成功分類と診断を観測する。保存と失敗履歴の読戻しはBackfillChartInfos_SuccessClearsStaleParseFailureが担当する。</remarks>
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
            ChartFile digest = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            ChartFile file = ChartTestValues.Empty() with
            {
                Path = chartPath
            };
            file = file with { Md5 = digest.Md5 };
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

}
