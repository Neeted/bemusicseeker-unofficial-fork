using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
using static BeMusicSeeker.TestSupport.Components.ChartInfoHydrationTestSupport;
namespace BeMusicSeeker.Tests;

/// <summary>
/// 譜面情報の導入、解析失敗、警告、再評価、DB競合の境界を確認します。
/// </summary>
[TestClass]
public sealed class ChartInfoInstallFailureRetryTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

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
            var library = new TestBmsLibrary(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(tempRootPath)), null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            ChartFile installedFile = library.BmsCharts.Single();
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
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
    [TestMethod]
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

            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
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
            var library = new TestBmsLibrary(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(tempRootPath)), null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            ChartFile installedFile = library.BmsCharts.Single();
            Assert.AreEqual(installedSnapshot.Md5, installedFile.Md5);
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
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
    [DataRow("keyboard-24k", 24, 25, false)]
    [DataRow("keyboard-24k-double", 48, 50, true)]
    [DoNotParallelize]
    public async Task InstallChartPackages_AddsBmsonAndBuildsInlineChartInfo(string mode, int commonMode, int detailMode, bool estimated)
    {

        await WithTemporarySongDb(async (string tempRootPath, string songDbPath) =>
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
                    + "\"info\":{\"title\":\"install bmson\",\"level\":1,\"mode_hint\":\"" + mode + "\",\"init_bpm\":120,\"judge_rank\":100,\"total\":100,\"resolution\":240},"
                    + "\"lines\":[{\"y\":0},{\"y\":960},{\"y\":1920}],"
                    + "\"stop_events\":[{\"y\":960,\"duration\":240},{\"y\":1440,\"duration\":240}],"
                    + "\"bga\":{\"layer_events\":[{\"y\":1200,\"id\":1}],\"poor_events\":[{\"y\":1200,\"id\":1}]},"
                    + "\"sound_channels\":[{\"name\":\"tone\",\"notes\":[{\"x\":1,\"y\":0},{\"x\":1,\"y\":1920}]}]"
                    + "}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllBytes(Path.Combine(sourceDir, "tone.wav"), ScheduledAudioFixture.BuildFloatWave(48000, Enumerable.Repeat(0.125f, 960).ToArray()));
            ChartFile pendingSong = ChartTestValues.ReadBmson(sourceChartPath);
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingSong))]);
            package.path = sourceDir;
            package.delete_parent = false;
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null,
                new InstallServiceComponentTestSupport.RealFileMutationService(), new RecordingDialogService(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot
                {
                    OperationModeLR2DB = false,
                    BMSInstallDir = installDir,
                    FolderNameFormat = "%TITLE%",
                    KeepInstallablePackagesPending = false,
                    PendingInstallEstimateMaxParallelPackages = 1
                }, settings: TestBmsFactory.CreateRootSettings([installDir]))
            { BmsCharts = [], BmsonCharts = [], ChartPackagesPending = [], ChartPackagesInstalled = [] };

            if (estimated)
            {
                package.ChartEntries.Single().ApplyInstallDestinationMetadata(installDir, "Installed", "Artist");
                library.ChartPackagesPending = [package];
                library.InstallPendingPackagesToEstimatedDestinations([package], new LibraryOperationNotices());
            }
            else
            {
                await library.InstallChartPackagesAutoWithProgressAsync([sourceDir], CancellationToken.None,
                    new InstallServiceComponentTestSupport.RecordingPackageInstallProgressWriter(), new LibraryOperationNotices());
            }

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            ChartFile installedSong = library.BmsonCharts.Single();
            ChartDetails detail = library.ResolveChartInfo(installedSong.Sha256, installedSong.Md5);
            Assert.IsNotNull(detail);
            Assert.AreEqual(commonMode, installedSong.Mode);
            Assert.AreEqual(detailMode, detail.mode);
            Assert.AreEqual(4, detail.speedchange_count);
            Assert.AreEqual(2, detail.notes);
            Assert.AreEqual(0, detail.bga);
            Assert.AreEqual(22, detail.parser_version);
            await ScheduledAudioFixture.AssertInstalledChartAudioAsync(installedSong.Path, [0, 240000], writeWave: true);
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM bmson_song WHERE path = '" + installedSong.Path.Replace("'", "''") + "';"));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM chart_info WHERE sha256 = '" + installedSong.Sha256 + "';"));
            ChartFile reloaded = ChartSongStorageMapping.FromBmsonRow(verify.Table<LR2SongDBExtended.bmson_song>().Single());
            Assert.AreEqual(commonMode, reloaded.Mode);
            Assert.AreEqual(installedSong.Md5, reloaded.Md5);
            Assert.AreEqual(installedSong.Sha256, reloaded.Sha256);
            Assert.AreEqual(detailMode, verify.Table<LR2SongDBExtended.chart_info>().Single().mode);
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
            var library = new TestBmsLibrary(databaseScope, songDbPath, new Lr2ConfigOwner(TestLr2ConfigFactory.CreateLr2Config(tempRootPath)), null, null, new RecordingDialogService(), settings: testSettings);

            InvokeInstallChartPackages(library, [package], installDir);

            Assert.AreEqual(0, library.ChartInfoBackfillRequestedVersion);
            ChartFile installedFile = library.BmsCharts.Single();
            using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
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
    [DoNotParallelize]
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
            ChartFile nullDiagnosticFile = ChartTestValues.Empty() with
            {
                Path = Path.Combine(tempRootPath, "nulls.bms"),
                Md5 = new string('e', 32),
                Sha256 = new string('5', 64)
            };
            ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(tempRootPath, "bad.bmson"),
                Folder = tempRootPath,
                Md5 = new string('b', 32),
                Sha256 = new string('2', 64),
                RawTitle = "bad bmson",
                RawArtist = "artist"
            };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.EnsureChartInfoSchema();
            gateway.UpsertChartInfoParseFailures(
            [
                CreateChartInfoParseFailureRow(bmsFile.Md5, bmsFile.Sha256, bmsFile.Path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "BMS initial BPM is not defined or invalid.", null),
                CreateChartInfoParseFailureRow(bmsonSong.Md5, bmsonSong.Sha256, bmsonSong.Path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "JsonReaderException", "bad json", null),
                CreateChartInfoParseFailureRow(new string('c', 32), string.Empty, Path.Combine(tempRootPath, "missing.bms"), BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", "InvalidDataException", "missing", null),
                CreateChartInfoParseFailureRow(staleBmsFile.Md5, staleBmsFile.Sha256, staleBmsFile.Path, 0, "parse_failed", "InvalidDataException", "old", null),
                CreateChartInfoParseFailureRow(nullDiagnosticFile.Md5, nullDiagnosticFile.Sha256, nullDiagnosticFile.Path, BmsLibraryDbGateway.CurrentChartInfoParserVersion, "parse_failed", null!, null!, null)
            ]);
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
            {
                BmsCharts = [bmsFile, staleBmsFile, nullDiagnosticFile],
                BmsonCharts = [bmsonSong]
            };

            List<ChartFile> rows = [.. library.ChartInfoParseFailedChartFiles];
            using LR2SongDBExtended failureDb = databaseScope.OpenSongReadOnly(songDbPath);

            Assert.AreEqual(3, rows.Count);
            CollectionAssert.AreEqual(
                new[] { bmsFile.Path, bmsonSong.Path, nullDiagnosticFile.Path }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
                rows.Select(row => row.Path).ToArray());
            foreach (ChartFile row in rows)
            {
                ChartWarning parseFailureWarning = row.Warnings.Single(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure);
                LR2SongDBExtended.chart_info_parse_failure failure = failureDb
                    .Table<LR2SongDBExtended.chart_info_parse_failure>()
                    .Single(candidate => string.Equals(candidate.md5, row.Md5, StringComparison.OrdinalIgnoreCase));
                // 記録された診断は原文のまま使い、欠落(null)の行だけ現在の言語の代替語を使います。
                string expectedReason = !string.IsNullOrWhiteSpace(failure.exception_type) ? failure.exception_type
                    : !string.IsNullOrWhiteSpace(failure.failure_kind) ? failure.failure_kind : Resources.WarningDigest_ChartInfoParseFailure;
                string expectedBody = !string.IsNullOrWhiteSpace(failure.message) ? failure.message : Resources.WarningDigest_ChartInfoParseFailure;
                string expectedMessage = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.Warning_ChartInfoParseFailure,
                    expectedReason,
                    expectedBody);
                Assert.IsTrue(ChartWarningProjectionFormatter.HasHighlightedWarning(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual("[1] " + Resources.WarningDigest_ChartInfoParseFailure, ChartWarningProjectionFormatter.BuildDigestText(row, ResourceHealthWarningProjection.Empty, hasResourceHealthProjection: false));
                Assert.AreEqual(failure.exception_type ?? string.Empty, parseFailureWarning.ExceptionType);
                Assert.AreEqual(failure.failure_kind ?? string.Empty, parseFailureWarning.FailureKind);
                Assert.AreEqual(failure.message ?? string.Empty, parseFailureWarning.DiagnosticMessage);
                Assert.AreEqual(expectedMessage, ChartWarningProjectionFormatter.GetDetailText(parseFailureWarning));
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

            // 言語を変えても診断の原文は不変で、欠落した本文の代替語だけが読取り時に現在の言語になります。
            ChartWarning nullDiagnostics = rows.Single(row => row.Path == nullDiagnosticFile.Path)
                .Warnings.Single(warning => warning.Kind == ChartWarningKind.ChartInfoParseFailure);
            CultureInfo? previousResourcesCulture = Resources.Culture;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                string japaneseLabel = Resources.WarningDigest_ChartInfoParseFailure;
                Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, "parse_failed", japaneseLabel),
                    ChartWarningProjectionFormatter.GetDetailText(nullDiagnostics));
                ResourceService.Current.ChangeCulture("en-US");
                string englishLabel = Resources.WarningDigest_ChartInfoParseFailure;
                Assert.AreNotEqual(japaneseLabel, englishLabel);
                Assert.AreEqual(string.Format(CultureInfo.CurrentCulture, Resources.Warning_ChartInfoParseFailure, "parse_failed", englishLabel),
                    ChartWarningProjectionFormatter.GetDetailText(nullDiagnostics));
                Assert.AreEqual(string.Empty, nullDiagnostics.ExceptionType);
                Assert.AreEqual("parse_failed", nullDiagnostics.FailureKind);
                Assert.AreEqual(string.Empty, nullDiagnostics.DiagnosticMessage);
            }
            finally
            {
                ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
                Resources.Culture = previousResourcesCulture;
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
                var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
                gateway.EnsureChartInfoSchema();
                using (LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath))
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
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
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
                using LR2SongDBExtended verify = databaseScope.OpenSongReadOnly(songDbPath);
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
    [TestMethod]
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
                var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
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
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
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
                var nextStartupLibrary = new TestBmsLibrary(databaseScope, songDbPath, null, null, null, new RecordingDialogService(), settings: testSettings)
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

}
