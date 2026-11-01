using System.Collections.ObjectModel;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.ChartInfoComponentTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistLampSourceTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistSummaryAggregationTestSupport;

namespace BeMusicSeeker.Tests;


/// <summary>ライブラリから集計入力を捕捉するハッシュ補完・履歴障害・取得元の一貫性・明示捕捉を検証する。</summary>
/// <remarks>専用SQLiteと実メタデータ取込みで情報源の接続を確認し、カテゴリ計算はPlaylistLampAggregationTestsへ分担する。</remarks>
[TestClass]
public sealed class BmsLibraryPlaylistLampDataSourceTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>現在成績の捕捉直後に同種のBを公開しても、履歴あり・なし双方の成績とノーツ・日付範囲をAで揃え、次の要求だけBへ更新します。取得元の混在と旧入力の書換えを検出します。</summary>
    /// <remarks>実SQLite・通常のスコア公開・標準compositionの実resolverを接続します。Windowは作らず、表示と要求世代の保証は既存Viewerテストへ分担します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void CaptureAsync_keepsCurrentScoresAndHistoryFromOnePublishedBeatorajaSource()
    {
        WithComposedResolver((viewModel, settings, root, songDbPath) =>
        {
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
            string h1 = new('1', 64), h2 = new('2', 64), outside = new('3', 64);
            string pathA = CreateBeatorajaSource(Path.Combine(root, "A"), h1, h2, outside, 100, 180, 80, ClearType.CLEAR, ClearType.CLEAR, 150, new DateTime(2026, 8, 1));
            string pathB = CreateBeatorajaSource(Path.Combine(root, "B"), h1, h2, outside, 200, 360, 240, ClearType.HARD, ClearType.HARD, 100, new DateTime(2026, 8, 10));
            settings.UseBeatorajaScoreDb = true;
            settings.BeatorajaScoreDbPath = pathA;
            var library = new TestBmsLibrary(databaseScope, songDbPath, settings: settings) { BmsCharts = [] };
            try
            {
                library.InitializeScoresOnly(null);
                TestBmsPlaylist playlist = CreateScorePlaylist(databaseScope, songDbPath, null, h1, h2);
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
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
            string h1 = new('1', 64), h2 = new('2', 64);
            settings.UseBeatorajaScoreDb = true;
            settings.BeatorajaScoreDbPath = CreateBeatorajaSource(root, h1, h2, new string('3', 64), 100, 180, 80, ClearType.CLEAR, ClearType.CLEAR, 150, new DateTime(2026, 8, 1));
            var library = new TestBmsLibrary(databaseScope, songDbPath, settings: settings) { BmsCharts = [] };
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
                    CreateScorePlaylist(databaseScope, songDbPath, null, h1, h2), library,
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
            ApplicationDatabaseScope databaseScope = MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope;
            string hash = new('a', 32);
            string pathA = Path.Combine(root, "score.db");
            using (LR2ScoreDBExtended db = databaseScope.OpenScoreWrite(pathA))
            {
                db.CreateTable<LR2ScoreDB.player>();
                db.CreateTable<LR2ScoreDB.score>();
                db.Insert(new LR2ScoreDB.score { hash = hash, clear = ClearType.HARD, perfect = 90, totalnotes = 100, playcount = 3 });
            }
            Assert.AreEqual(Lr2PlayHistorySchemaStatus.Installed, new Lr2PlayHistorySchemaService(databaseScope).InstallOrRepair(pathA, true).Status);
            using (var db = new SQLite.SQLiteConnection(pathA))
            {
                InsertLr2SourceHistory(db, hash, new DateTime(2026, 8, 1), 40);
                InsertLr2SourceHistory(db, hash, new DateTime(2026, 8, 21), 120);
            }
            settings.OperationModeLR2DB = true;
            var library = new TestBmsLibrary(databaseScope, songDbPath, _lr2ScoreDB: pathA, settings: settings) { BmsCharts = [] };
            try
            {
                library.InitializeScoresOnly(null);
                var source = new BmsLibraryPlaylistLampDataSource(CreateScorePlaylist(databaseScope, songDbPath, hash), library, snapshot =>
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
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
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

    /// <summary>呼出し元と同じDB所有者でテスト対象と保存入力を準備します。</summary>
    /// <param name="databaseScope">呼出し元と接続・書込み排他を共有するfixtureのDB所有者。</param>
    private static TestBmsPlaylist CreateScorePlaylist(ApplicationDatabaseScope databaseScope, string songDbPath, string? md5, params string[] sha256s)
    {
        BMSTableEntry[] entries = sha256s.Length == 0
            ? [CreateEntry(md5, null)]
            : sha256s.Select(sha256 => CreateEntry(md5, sha256)).ToArray();
        foreach (BMSTableEntry entry in entries) { entry.folder = "scores"; }
        return new TestBmsPlaylist(databaseScope, songDbPath)
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

}
