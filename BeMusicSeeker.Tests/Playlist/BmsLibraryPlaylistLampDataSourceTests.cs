using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;
using static BeMusicSeeker.Tests.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>ライブラリから集計入力を捕捉するハッシュ補完・履歴障害・取得元の一貫性・明示捕捉を検証する。</summary>
/// <remarks>専用SQLiteと実メタデータ取込みで情報源の接続を確認し、カテゴリ計算はPlaylistLampAggregationTestsへ分担する。</remarks>
[TestClass]
public sealed class BmsLibraryPlaylistLampDataSourceTests
{
    /// <summary>実フォルダ名[NO SONG]と未所持MD5エントリを捕捉し、実chart_infoの補完SHAを所持SHAとは区別して渡す。名前による所属欠落・補完漏れ・所持誤認を検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_keepsChartInfoShaSeparateAndUsesItForBeatorajaLookup()
    {

        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string md5 = new('a', 32);
            string chartInfoSha256 = new('c', 64);
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                BmsLibraryDbGateway.EnsureChartInfoSchema(songDb);
                songDb.InsertOrReplace(ChartInfoStorageMapping.ToStorage(
                    CreateChartInfoRow(chartInfoSha256, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)), typeof(LR2SongDBExtended.chart_info));
            }

            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService(), new ChartInfoMetadataTestSupport.RecordingDialogService())
            {
                BmsCharts = []
            };
            InvokeDeferredChartInfoHydration(library, "playlist_lamp_data_source_test", queueFullBackfillAfterHydration: false);
            await AwaitChartInfoHydrationAsync(library);

            BMSTableEntry entry = CreateEntry(md5, null);
            entry.folder = "[NO SONG]";
            var table = new BMSTable
            {
                playlist_id = 17,
                Folder_order = ["[NO SONG]"],
                entries = [entry]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);

            PlaylistLampAggregationRequest request = await source.CaptureAsync(
                PlaylistLampViewerQuery.Latest("17"),
                CancellationToken.None);
            PlaylistLampEntrySnapshot snapshot = request.Entries.Single();

            CollectionAssert.AreEqual(new[] { "[NO SONG]" }, request.FolderOrder.ToArray());
            Assert.AreEqual("[NO SONG]", snapshot.FolderName);
            Assert.IsFalse(snapshot.IsOwned);
            Assert.AreEqual(chartInfoSha256, snapshot.ChartInfoSha256);
            Assert.AreEqual(string.Empty, snapshot.ResolvedSha256);
            Assert.AreEqual(chartInfoSha256, snapshot.ScoreSha256);
        });
    }

    /// <summary>履歴源の生成が失敗しても所属入力を維持し、最新は未設定、過去日はスコア利用不可と診断を返す。ビューア全体の終端化や架空のスコアへの置換を検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_historicalSourceFactoryFailure_is_nonterminal_for_latest_and_historical_queries()
    {

        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService(),
                new ChartInfoMetadataTestSupport.RecordingDialogService())
            {
                BmsCharts = []
            };
            BMSTableEntry entry = CreateEntry(new string('e', 32), null);
            entry.folder = "folder";
            var table = new BMSTable
            {
                playlist_id = 19,
                Folder_order = ["folder"],
                entries = [entry]
            };
            var playlist = new TestBmsPlaylist(songDbPath)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };
            var source = new BmsLibraryPlaylistLampDataSource(
                playlist,
                library,
                _ => throw new InvalidOperationException("history source path unavailable"));

            PlaylistLampAggregationRequest latest = await source.CaptureAsync(
                PlaylistLampViewerQuery.Latest("19"),
                CancellationToken.None);
            Assert.AreEqual(PlaylistLampInputState.Loaded, latest.InputState);
            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Latest, latest.HistoricalStatus);
            Assert.IsFalse(latest.HistoricalDateRange.HasHistoricalDates);
            Assert.AreEqual(ActiveScoreSource.None, latest.ScoreSnapshot.Source);
            Assert.AreEqual(ScoreTableLoadStatus.NotConfigured, latest.ScoreSnapshot.LoadStatus);
            StringAssert.Contains(latest.HistoricalFailureMessage, "history source path unavailable");

            PlaylistLampAggregationRequest historical = await source.CaptureAsync(
                new PlaylistLampViewerQuery("19", DateTime.Today),
                CancellationToken.None);
            Assert.AreEqual(PlaylistLampInputState.Loaded, historical.InputState);
            Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, historical.HistoricalStatus);
            Assert.AreEqual(ScoreTableLoadStatus.Failed, historical.ScoreSnapshot.LoadStatus);
            Assert.IsFalse(historical.ScoreSnapshot.IsScoreDataAvailable);
            Assert.IsFalse(historical.HistoricalDateRange.HasHistoricalDates);
            StringAssert.Contains(historical.HistoricalFailureMessage, "history source path unavailable");
        });
    }

    /// <summary>実DBの再読取り失敗後の明示捕捉は利用不可となり、次の成功と所属・所持変更を明示捕捉へ反映する。前の不変入力の破壊と外部DB変更の自動採用も検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_recapturesCurrentMembershipOwnershipAndApplicationScore()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            string hash = new('f', 32);
            string sha = new('b', 64);
            string scorePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException(), "score.db");
            using (var db = new LR2ScoreDBExtended(scorePath))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.score { hash = hash, clear = ClearType.CLEAR, perfect = 10, totalnotes = 100 });
            }
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed,
                new Lr2PlayHistorySchemaService().InstallOrRepair(scorePath, isLr2LinkedProfile: true).Status);
            // 日付選択は取得済み履歴の範囲内でだけ可能です。外部更新より前の取得組へ既存の履歴を含めます。
            using (var db = new LR2ScoreDBExtended(scorePath)) { InsertLr2SourceHistory(db, hash, DateTime.Today.AddDays(-1), 10); }
            Settings sourceSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values => values.OperationModeLR2DB = true);
            var sourceOwner = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException()));
            var library = new TestBmsLibrary(songDbPath, sourceOwner, scorePath, new TestFileMutationService(), new PlaylistSummaryAggregationTestSupport.RecordingDialogService(), settings: sourceSettings) { BmsCharts = [] };
            try
            {
                library.InitializeScoresOnly(null);
                BMSTableEntry entry = CreateEntry(hash, sha);
                entry.folder = "first";
                var table = new BMSTable { playlist_id = 21, Folder_order = ["first"], entries = [entry] };
                var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([table]) };
                var source = new BmsLibraryPlaylistLampDataSource(playlist, library, PlaylistLampHistoricalScoreSourceContext.FromSnapshot);
                PlaylistLampAggregationRequest first = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
                Assert.AreEqual(ScoreTableLoadStatus.Loaded, first.ScoreSnapshot.LoadStatus);
                Assert.AreEqual(20, first.ScoreSnapshot.Resolve(first.Entries.Single()).ExScore);
                Assert.IsFalse(first.Entries.Single().IsOwned);
                using (var db = new LR2ScoreDBExtended(scorePath)) { db.Execute("UPDATE score SET perfect = 30, playcount = playcount + 1 WHERE hash = ?", hash); }
                // DB更新だけではアプリの保持scoreを変更せず、明示捕捉でもその保持値を使う。
                PlaylistLampAggregationRequest beforeReload = await source.CaptureAsync(new PlaylistLampViewerQuery("21", DateTime.Today), default);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, beforeReload.HistoricalStatus);
                Assert.AreEqual(20, beforeReload.ScoreSnapshot.Resolve(beforeReload.Entries.Single()).ExScore);
                using (var db = new LR2ScoreDBExtended(scorePath))
                {
                    db.Execute("ALTER TABLE score RENAME TO retained_score");
                    Assert.ThrowsException<SQLite.SQLiteException>(() => db.Table<BMSScore>().ToList());
                }
                library.InitializeScoresOnly(null);
                PlaylistLampAggregationRequest afterFailure = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
                Assert.AreEqual(ScoreTableLoadStatus.Failed, afterFailure.ScoreSnapshot.LoadStatus);
                Assert.IsFalse(afterFailure.ScoreSnapshot.IsScoreDataAvailable);
                using (var db = new LR2ScoreDBExtended(scorePath)) { db.Execute("ALTER TABLE retained_score RENAME TO score"); }
                library.InitializeScoresOnly(null);
                library.BmsCharts = [CreateLibraryFile("C:/charts/source.bms", hash, sha)];
                BMSTableEntry changed = CreateEntry(hash, sha);
                changed.folder = "second";
                table.Folder_order = ["second"];
                table.entries = [changed];
                PlaylistLampAggregationRequest next = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("21"), default);
                Assert.AreEqual("second", next.Entries.Single().FolderName);
                Assert.IsTrue(next.Entries.Single().IsOwned);
                Assert.AreEqual(60, next.ScoreSnapshot.Resolve(next.Entries.Single()).ExScore);
                Assert.AreEqual("first", first.Entries.Single().FolderName);
                Assert.IsFalse(first.Entries.Single().IsOwned);
                Assert.AreEqual(20, first.ScoreSnapshot.Resolve(first.Entries.Single()).ExScore);
            }
            finally { library.RequestShutdown("playlist-score-fixture-cleanup"); }
        });
    }

    /// <summary>現在成績の捕捉直後に同種のBを公開しても、履歴あり・なし双方の成績とノーツ・日付範囲をAで揃え、次の要求だけBへ更新します。取得元の混在と旧入力の書換えを検出します。</summary>
    /// <remarks>実SQLite・通常のスコア公開・標準compositionの実resolverを接続します。Windowは作らず、表示と要求世代の保証は既存Viewerテストへ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void CaptureAsync_keepsCurrentScoresAndHistoryFromOnePublishedBeatorajaSource()
    {
        WithComposedResolver((viewModel, settings, root, songDbPath) =>
        {
            string h1 = new('1', 64), h2 = new('2', 64), outside = new('3', 64);
            string pathA = CreateBeatorajaSource(Path.Combine(root, "A"), h1, h2, outside, 100, 180, 80, ClearType.CLEAR, ClearType.CLEAR, 150, new DateTime(2026, 8, 1));
            string pathB = CreateBeatorajaSource(Path.Combine(root, "B"), h1, h2, outside, 200, 360, 240, ClearType.HARD, ClearType.HARD, 100, new DateTime(2026, 8, 10));
            settings.UseBeatorajaScoreDb = true;
            settings.BeatorajaScoreDbPath = pathA;
            var library = new TestBmsLibrary(songDbPath, settings: settings) { BmsCharts = [] };
            try
            {
                library.InitializeScoresOnly(null);
                TestBmsPlaylist playlist = CreateScorePlaylist(songDbPath, null, h1, h2);
                bool switchSource = true;
                PlaylistLampHistoricalScoreSourceContext? capturedA = null;
                var source = new BmsLibraryPlaylistLampDataSource(playlist, library, snapshot =>
                {
                    if (switchSource)
                    {
                        switchSource = false;
                        Assert.AreEqual(pathA, snapshot.ScoreDbPath);
                        settings.BeatorajaScoreDbPath = pathB;
                        library.InitializeScoresOnly(null);
                        capturedA = viewModel.ResolvePlaylistLampHistoricalScoreSourceContext(snapshot);
                        return capturedA;
                    }
                    return viewModel.ResolvePlaylistLampHistoricalScoreSourceContext(snapshot);
                });

                PlaylistLampAggregationRequest first = CaptureOnDispatcher(source, new DateTime(2026, 8, 20));
                AssertHistoricalScore(first, h1, ClearType.CLEAR, 150, 100);
                AssertHistoricalScore(first, h2, ClearType.CLEAR, 80, 100);
                Assert.AreEqual(new DateTime(2026, 8, 1), first.HistoricalDateRange.EarliestLocalDate);
                Assert.IsNotNull(capturedA);
                Assert.AreEqual(pathA, capturedA.PlayHistorySnapshot.ScoreDbPath);
                Assert.AreEqual(100, capturedA.PlayHistorySnapshot.Beatoraja.Rows.Single(row => row.sha256 == h1).notes);

                File.Move(pathB, pathB + ".moved");
                File.Move(Path.Combine(Path.GetDirectoryName(pathB) ?? throw new AssertFailedException(), "scorelog.db"), pathB + ".history-moved");
                PlaylistLampAggregationRequest next = CaptureOnDispatcher(source, new DateTime(2026, 8, 19));
                AssertHistoricalScore(next, h1, ClearType.HARD, 100, 200);
                AssertHistoricalScore(next, h2, ClearType.HARD, 240, 200);
                Assert.AreEqual(new DateTime(2026, 8, 10), next.HistoricalDateRange.EarliestLocalDate);
                AssertHistoricalScore(first, h1, ClearType.CLEAR, 150, 100);
                AssertHistoricalScore(first, h2, ClearType.CLEAR, 80, 100);
                Assert.AreEqual(new DateTime(2026, 8, 1), first.HistoricalDateRange.EarliestLocalDate);
                Assert.AreEqual(100, capturedA.PlayHistorySnapshot.Beatoraja.Rows.Single(row => row.sha256 == h1).notes);
            }
            finally { library.RequestShutdown("lamp-source-switch-cleanup"); }
        });
    }

    /// <summary>有効DBがあっても未公開スコアの捕捉は構築・版更新・通知を起こさず利用不可となり、通常ロード後の新要求だけ復帰します。暗黙構築や最新設定への代替を検出します。</summary>
    /// <remarks>実resolverと実DBを使い、既存の作業観測口と公開状態を確認します。共有compositionの購読・ResourcesはWindowなしの共通寿命で回収します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void CaptureAsync_unpublishedScoresStayUnavailableUntilNormalLoad()
    {
        WithComposedResolver((viewModel, settings, root, songDbPath) =>
        {
            string h1 = new('1', 64), h2 = new('2', 64);
            settings.UseBeatorajaScoreDb = true;
            settings.BeatorajaScoreDbPath = CreateBeatorajaSource(root, h1, h2, new string('3', 64), 100, 180, 80, ClearType.CLEAR, ClearType.CLEAR, 150, new DateTime(2026, 8, 1));
            var library = new TestBmsLibrary(songDbPath, settings: settings) { BmsCharts = [] };
            int workCount = 0, notificationCount = 0;
            library.ScoreSnapshotWorkObserver = _ => Interlocked.Increment(ref workCount);
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(BMSLibrary.ScoreSnapshotReady) or nameof(BMSLibrary.ScoreSnapshotVersion))
                {
                    Interlocked.Increment(ref notificationCount);
                }
            };
            try
            {
                int version = library.ScoreSnapshotVersion;
                var source = new BmsLibraryPlaylistLampDataSource(
                    CreateScorePlaylist(songDbPath, null, h1, h2), library,
                    viewModel.ResolvePlaylistLampHistoricalScoreSourceContext);
                Assert.IsFalse(library.ScoreSnapshotReady);
                PlaylistLampAggregationRequest latest = CaptureOnDispatcher(source, null);
                Assert.IsFalse(latest.ScoreSnapshot.IsScoreDataAvailable);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Latest, latest.HistoricalStatus);
                PlaylistLampAggregationRequest historical = CaptureOnDispatcher(source, new DateTime(2026, 8, 20));
                Assert.IsFalse(historical.ScoreSnapshot.IsScoreDataAvailable);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Unavailable, historical.HistoricalStatus);
                Assert.IsFalse(historical.HistoricalDateRange.HasHistoricalDates);
                Assert.IsFalse(library.ScoreSnapshotReady);
                Assert.AreEqual(version, library.ScoreSnapshotVersion);
                Assert.AreEqual(0, workCount);
                Assert.AreEqual(0, notificationCount);

                library.InitializeScoresOnly(null);
                Assert.IsTrue(library.ScoreSnapshotReady);
                Assert.IsTrue(library.ScoreSnapshotVersion > version);
                Assert.IsTrue(CaptureOnDispatcher(source, null).ScoreSnapshot.IsScoreDataAvailable);
                PlaylistLampAggregationRequest available = CaptureOnDispatcher(source, new DateTime(2026, 8, 20));
                AssertHistoricalScore(available, h1, ClearType.CLEAR, 150, 100);
                Assert.AreEqual(new DateTime(2026, 8, 1), available.HistoricalDateRange.EarliestLocalDate);
            }
            finally { library.RequestShutdown("lamp-source-unpublished-cleanup"); }
        });
    }

    /// <summary>LR2のA公開後に最新設定を連携OFF・別パスへ変えても、Aの連携履歴・成績・日付範囲を使います。捕捉済みLR2取得元を最新設定で無効化する誤りを検出します。</summary>
    /// <remarks>正常トリガーを導入した実LR2 DBと実resolverを接続し、旧値欠落など詳細な巻戻し規則は既存履歴スナップショットテストへ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void CaptureAsync_keepsCapturedLr2SourceAfterLatestSettingsDisableLink()
    {
        WithComposedResolver((viewModel, settings, root, songDbPath) =>
        {
            string hash = new('a', 32);
            string pathA = Path.Combine(root, "score.db");
            using (var db = new LR2ScoreDBExtended(pathA))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.score { hash = hash, clear = ClearType.HARD, perfect = 90, totalnotes = 100, playcount = 3 });
            }
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, new Lr2PlayHistorySchemaService().InstallOrRepair(pathA, true).Status);
            using (var db = new SQLite.SQLiteConnection(pathA))
            {
                InsertLr2SourceHistory(db, hash, new DateTime(2026, 8, 1), 40);
                InsertLr2SourceHistory(db, hash, new DateTime(2026, 8, 21), 120);
            }
            settings.OperationModeLR2DB = true;
            var library = new TestBmsLibrary(songDbPath, _lr2ScoreDB: pathA, settings: settings) { BmsCharts = [] };
            try
            {
                library.InitializeScoresOnly(null);
                var source = new BmsLibraryPlaylistLampDataSource(CreateScorePlaylist(songDbPath, hash), library, snapshot =>
                {
                    settings.OperationModeLR2DB = false;
                    settings.LR2RootPath = Path.Combine(root, "B");
                    PlaylistLampHistoricalScoreSourceContext context = viewModel.ResolvePlaylistLampHistoricalScoreSourceContext(snapshot);
                    Assert.IsNotNull(context);
                    Assert.AreEqual(ActiveScoreSource.Lr2, context.ActiveScoreSource);
                    Assert.AreEqual(pathA, context.PlayHistorySnapshot.ScoreDbPath);
                    Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, context.PlayHistorySnapshot.Lr2.SchemaStatus);
                    return context;
                });
                PlaylistLampAggregationRequest request = CaptureOnDispatcher(source, new DateTime(2026, 8, 20));
                AssertHistoricalScore(request, hash, ClearType.HARD, 120, 100);
                Assert.AreEqual(new DateTime(2026, 8, 1), request.HistoricalDateRange.EarliestLocalDate);
            }
            finally { library.RequestShutdown("lamp-source-lr2-cleanup"); }
        });
    }

    private static void WithComposedResolver(Action<MainWindowViewModel, Settings, string, string> body)
    {
        TestUiDispatcherHost.Invoke(() => WithTemporarySongDb((root, songDbPath) =>
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.UseBeatorajaScoreDb = false;
                values.BeatorajaRootPath = string.Empty;
            });
            MainWindowViewModel viewModel = MainWindowViewModelTestFactory.Create(settings);
            var lifetime = new MainWindowTestLifetime(viewModel, Task.CompletedTask);
            lifetime.Run(() => body(viewModel, settings, root, songDbPath));
        }));
    }

    private static TestBmsPlaylist CreateScorePlaylist(string songDbPath, string? md5, params string[] sha256s)
    {
        BMSTableEntry[] entries = sha256s.Length == 0
            ? [CreateEntry(md5, null)]
            : sha256s.Select(sha256 => CreateEntry(md5, sha256)).ToArray();
        foreach (BMSTableEntry entry in entries) { entry.folder = "scores"; }
        return new TestBmsPlaylist(songDbPath)
        {
            BMSTables = new ObservableCollection<BMSTable>([new BMSTable { playlist_id = 25, Folder_order = ["scores"], entries = entries }])
        };
    }

    private static PlaylistLampAggregationRequest CaptureOnDispatcher(BmsLibraryPlaylistLampDataSource source, DateTime? date)
    {
        Task<PlaylistLampAggregationRequest> task = source.CaptureAsync(new PlaylistLampViewerQuery("25", date), default).AsTask();
        TestUiDispatcherHost.AwaitTaskOnDispatcher(task, "lamp-source-capture");
        return task.GetAwaiter().GetResult();
    }

    private static void AssertHistoricalScore(PlaylistLampAggregationRequest request, string key, ClearType clear, int exScore, int notes)
    {
        Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Available, request.HistoricalStatus);
        Assert.IsTrue(request.ScoreSnapshot.IsScoreDataAvailable);
        PlaylistLampScore score = request.ScoreSnapshot.Resolve(request.Entries.Single(entry => entry.ScoreSha256 == key || entry.Md5 == key));
        Assert.IsNotNull(score);
        Assert.AreEqual(clear, score.Clear);
        Assert.AreEqual(exScore, score.ExScore);
        Assert.AreEqual(notes, score.TotalNotes);
    }

    private static string CreateBeatorajaSource(string directory, string h1, string h2, string outside, int notes, int firstExScore, int secondExScore, ClearType secondClear, ClearType oldClear, int oldExScore, DateTime earliestDate)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "score.db");
        using (var db = new SQLite.SQLiteConnection(path))
        {
            db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
            db.Execute("CREATE TABLE player (date INTEGER, playcount INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, egd INTEGER, lgd INTEGER, ebd INTEGER, lbd INTEGER, epr INTEGER, lpr INTEGER, ems INTEGER, lms INTEGER, playtime INTEGER)");
            InsertBeatorajaScore(db, h1, ClearType.HARD, firstExScore, notes);
            InsertBeatorajaScore(db, h2, secondClear, secondExScore, notes);
            InsertBeatorajaScore(db, outside, ClearType.CLEAR, 40, notes);
        }
        using (var db = new SQLite.SQLiteConnection(Path.Combine(directory, "scorelog.db")))
        {
            db.Execute("CREATE TABLE scorelog (sha256 TEXT NOT NULL, mode INTEGER, clear INTEGER, oldclear INTEGER, score INTEGER, oldscore INTEGER, combo INTEGER, oldcombo INTEGER, minbp INTEGER, oldminbp INTEGER, date INTEGER)");
            db.Execute("INSERT INTO scorelog VALUES (?, 0, ?, ?, ?, ?, 0, 0, 0, 0, ?)", h1, (int)ClearType.HARD, (int)oldClear, firstExScore, oldExScore, LocalNoonUnixSeconds(new DateTime(2026, 8, 21)));
            db.Execute("INSERT INTO scorelog VALUES (?, 0, ?, ?, 40, 20, 0, 0, 0, 0, ?)", outside, (int)ClearType.CLEAR, (int)ClearType.EASY, LocalNoonUnixSeconds(earliestDate));
        }
        return path;
    }

    private static void InsertBeatorajaScore(SQLite.SQLiteConnection db, string sha256, ClearType clear, int exScore, int notes)
        => db.Execute("INSERT INTO score VALUES (?, 0, ?, ?, 0, 0, 0, ?, 0, 0, 3, 2)", sha256, (int)clear, exScore / 2, notes);

    private static void InsertLr2SourceHistory(SQLite.SQLiteConnection db, string hash, DateTime date, int oldExScore)
        // LR2保存形式のHARDは4です。アプリ内ClearTypeの値6と区別します。
        => db.Execute("INSERT INTO bms_lr2_play_history (hash, played_at, finalized, score_write_type, old_playcount, new_playcount, playcount_delta, old_clear, old_op_history, old_exscore, old_totalnotes) VALUES (?, ?, 1, 'update', 2, 3, 1, 4, 0, ?, 100)", hash, LocalNoonUnixSeconds(date), oldExScore);

    private static long LocalNoonUnixSeconds(DateTime date)
        => new DateTimeOffset(DateTime.SpecifyKind(date.Date.AddHours(12), DateTimeKind.Local)).ToUnixTimeSeconds();

    /// <summary>未準備表だけ既存準備Taskを待ち、完了後に同IDの置換表を捕捉する。別の準備済み表まで待たせる誤りとheaderを空成功にする誤りを検出する。</summary>
    [TestMethod]
    public async Task CaptureAsync_waitsForRequiredEntriesAndResolvesReplacementWithoutBlockingReadyTable()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService()) { BmsCharts = [] };
            var header = new BMSTable { playlist_id = 22 };
            header.MarkEntriesNotLoaded();
            var ready = new BMSTable { playlist_id = 23, entries = [] };
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([header, ready]) };
            playlist.StartupReadiness.BeginPlaylistInitialization();
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            Task<PlaylistLampAggregationRequest> waiting = source.CaptureAsync(PlaylistLampViewerQuery.Latest("22"), default).AsTask();
            try
            {
                PlaylistLampAggregationRequest available = await source.CaptureAsync(PlaylistLampViewerQuery.Latest("23"), default);
                Assert.AreEqual(PlaylistLampInputState.Loaded, available.InputState);
                Assert.IsFalse(waiting.IsCompleted);
                BMSTableEntry entry = CreateEntry(new string('a', 32), null);
                entry.folder = "hydrated";
                playlist.BMSTables = new ObservableCollection<BMSTable>([new BMSTable { playlist_id = 22, entries = [entry] }, ready]);
                playlist.StartupReadiness.MarkRequiredPlaylistReady();
                PlaylistLampAggregationRequest captured = await waiting;
                Assert.AreEqual(PlaylistLampInputState.Loaded, captured.InputState);
                Assert.AreEqual("hydrated", captured.Entries.Single().FolderName);
            }
            finally
            {
                playlist.StartupReadiness.MarkRequiredPlaylistReady();
                await waiting;
            }
        });
    }

    /// <summary>捕捉取消は共有準備Taskを停止せず、その待機だけを終える。準備故障はsessionでFailedへ確定してLoadingに留めない。</summary>
    [TestMethod]
    public async Task CaptureAsync_cancellationDoesNotOwnReadinessAndReadinessFailureBecomesFailed()
    {
        await WithTemporarySongDb(async (_, songDbPath) =>
        {
            var library = new TestBmsLibrary(songDbPath, null, null, new TestFileMutationService()) { BmsCharts = [] };
            var table = new BMSTable { playlist_id = 24 };
            table.MarkEntriesNotLoaded();
            var playlist = new TestBmsPlaylist(songDbPath) { BMSTables = new ObservableCollection<BMSTable>([table]) };
            playlist.StartupReadiness.BeginPlaylistInitialization();
            var source = new BmsLibraryPlaylistLampDataSource(playlist, library);
            using var cancel = new CancellationTokenSource();
            Task<PlaylistLampAggregationRequest> capture = source.CaptureAsync(PlaylistLampViewerQuery.Latest("24"), cancel.Token).AsTask();
            try
            {
                cancel.Cancel();
                try { await capture; Assert.Fail("取消された捕捉が正常終了した。"); }
                catch (OperationCanceledException) { }
                Assert.IsFalse(playlist.StartupReadiness.RequiredPlaylistReadiness.IsCompleted);
                await using var session = new PlaylistLampViewerSession("24", source);
                Task start = session.StartAsync();
                playlist.StartupReadiness.FailRequiredPlaylistReadiness(new InvalidOperationException("required entries failed"));
                await start;
                Assert.AreEqual(PlaylistLampViewerState.Failed, session.Current.State);
                Assert.IsFalse(session.Current.IsSegmentInvocationEnabled);
            }
            finally { playlist.StartupReadiness.MarkRequiredPlaylistReady(); }
        });
    }
}
