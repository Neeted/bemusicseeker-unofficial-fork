using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>保存成績の実再読込みで、失敗時の非成功投影と次の通常要求への接続を確認します。</summary>
[TestClass]
public sealed class BmsLibrarySavedScoreAvailabilityTests
{
    /// <summary>設定済みLR2・beatorajaの初回SELECT失敗はFailedと対象pathを保持して一度案内し、通常一覧をNO PLAYへ投影します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InitializeScoresOnly_InitialFailureReportsOnceAndProjectsNoPlay(bool beatoraja)
    {
        using var fixture = new ScoreFixture(beatoraja, empty: false);
        fixture.BreakScoreSelect();

        fixture.Library.InitializeScoresOnly(null);

        BMSLibrary.ScoreSnapshot snapshot = fixture.Library.GetScoreSnapshotForDiagnostics();
        Assert.AreEqual(ScoreTableLoadStatus.Failed, snapshot.LoadStatus);
        Assert.AreEqual(fixture.ScorePath, snapshot.ScoreDbPath);
        Assert.AreEqual(beatoraja ? ActiveScoreSource.Beatoraja : ActiveScoreSource.Lr2, snapshot.ActiveScoreSource);
        Assert.IsFalse(fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        Assert.AreEqual(Resources.Error_RecommendationScoreReadFailed, fixture.Dialogs.Messages.Single().Message);
        fixture.AssertNoPlayProjection();
    }

    /// <summary>同種別の別pathへ切り替えた読取りが失敗しても旧DBの値を混ぜず、切替先の失敗状態と一度の案内を維持します。</summary>
    [TestMethod]
    public void InitializeScoresOnly_ChangedBeatorajaPathFailureDoesNotRetainOtherDatabase()
    {
        using var fixture = new ScoreFixture(beatoraja: true, empty: false);
        fixture.Library.InitializeScoresOnly(null);
        Assert.AreEqual(20, fixture.Lookup().Score);
        fixture.SelectBrokenBeatorajaDatabase();

        fixture.Library.InitializeScoresOnly(null);

        BMSLibrary.ScoreSnapshot snapshot = fixture.Library.GetScoreSnapshotForDiagnostics();
        Assert.AreEqual(ScoreTableLoadStatus.Failed, snapshot.LoadStatus);
        Assert.AreEqual(fixture.ScorePath, snapshot.ScoreDbPath);
        Assert.AreEqual(ActiveScoreSource.Beatoraja, snapshot.ActiveScoreSource);
        Assert.IsFalse(fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(0, snapshot.Scores.Count);
        Assert.AreEqual(0, snapshot.ScoresBySha256.Count);
        Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        fixture.AssertNoPlayProjection();
    }

    /// <summary>LR2・beatorajaの未設定と正常空は失敗通知を出さず、NotConfiguredとLoadedを区別して通常一覧をNO PLAYへ投影します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void InitializeScoresOnly_UnconfiguredAndEmptyDoNotReportFailure(bool beatoraja, bool configured)
    {
        using var fixture = new ScoreFixture(beatoraja, empty: true, configured: configured);

        fixture.Library.InitializeScoresOnly(null);

        Assert.AreEqual(configured ? ScoreTableLoadStatus.Loaded : ScoreTableLoadStatus.NotConfigured,
            fixture.Library.GetScoreSnapshotForDiagnostics().LoadStatus);
        Assert.AreEqual(configured, fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
        fixture.AssertNoPlayProjection();
    }

    /// <summary>同じLR2・beatoraja DBのSELECT失敗は旧成績を流用せず、一度警告してNO PLAYへ投影します。次の正常空を採用し、旧実体のsetterを再購読しません。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InitializeScoresOnly_FailedReloadDropsOldResultAndNextSuccessAdoptsEmpty(bool beatoraja)
    {
        using var fixture = new ScoreFixture(beatoraja, empty: false);
        fixture.Library.InitializeScoresOnly(null);
        BMSLibrary.ScoreSnapshot loaded = fixture.Library.GetScoreSnapshotForDiagnostics();
        BMSScore score = beatoraja ? loaded.ScoresBySha256.Values.Single() : loaded.Scores.Single();
        Assert.AreEqual(20, fixture.Lookup().Score);
        fixture.BreakScoreSelect();

        fixture.Library.InitializeScoresOnly(null);

        BMSLibrary.ScoreSnapshot failed = fixture.Library.GetScoreSnapshotForDiagnostics();
        Assert.AreEqual(ScoreTableLoadStatus.Failed, failed.LoadStatus);
        Assert.AreEqual(fixture.ScorePath, failed.ScoreDbPath);
        Assert.AreEqual(beatoraja ? ActiveScoreSource.Beatoraja : ActiveScoreSource.Lr2, failed.ActiveScoreSource);
        Assert.IsFalse(fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        fixture.AssertNoPlayProjection();
        int failedVersion = fixture.Library.ScoreSnapshotVersion;
        score.ranking = 7;
        Assert.AreEqual(failedVersion, fixture.Library.ScoreSnapshotVersion);
        fixture.AssertNoPlayProjection();

        fixture.RestoreEmptyScoreTable();
        fixture.Library.InitializeScoresOnly(null);

        Assert.AreEqual(ScoreTableLoadStatus.Loaded, fixture.Library.GetScoreSnapshotForDiagnostics().LoadStatus);
        Assert.IsTrue(fixture.Library.ScoreSnapshotReady);
        fixture.AssertNoPlayProjection();
        Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
    }

    /// <summary>実再読込みの失敗案内は非成功の投影とともに、親Lと初期化排他の解放後に既存警告文言で一度届きます。</summary>
    [TestMethod]
    public void InitializeScoresOnly_FailedReloadReportsAfterAdmissionAndInitializationLocksAreReleased()
    {
        using var fixture = new ScoreFixture(beatoraja: false, empty: false);
        fixture.Library.InitializeScoresOnly(null);
        fixture.BreakScoreSelect();
        var observations = new List<(bool AdmissionFree, bool LocksFree, bool Ready, int? Score)>();
        fixture.Dialogs.Observe = () =>
        {
            bool admitted = fixture.Library.OperationAdmission.TryEnter(out IDisposable lease);
            lease?.Dispose();
            observations.Add((admitted, !fixture.Library.IsWriteLockHeldInitializeAll && !fixture.Library.IsWriteLockHeldInitializeMin,
                fixture.Library.ScoreSnapshotReady, fixture.Lookup().Score));
        };

        fixture.Library.InitializeScoresOnly(null);

        Assert.AreEqual(1, observations.Count);
        Assert.AreEqual((true, true, false, (int?)null), observations.Single());
        Assert.AreEqual((Resources.Error_RecommendationScoreReadFailed, Resources.MessageBoxTitle_Warning,
            UiDialogButton.OK, UiDialogIcon.Exclamation), fixture.Dialogs.Messages.Single());
    }

    /// <summary>再読込み失敗は旧未送信表示を含む成績を外し、一回のsnapshot公開と一度の案内で非成功を投影します。</summary>
    [TestMethod]
    public void InitializeScoresOnly_FailedReloadStillClearsUnsentStatusWithOnePublication()
    {
        using var fixture = new ScoreFixture(beatoraja: false, empty: false);
        fixture.Library.InitializeScoresOnly(null);
        BMSScore score = fixture.Library.GetBMSScores().Single();
        score.IsLr2IrScoreUnsent = true;
        BMSLibrary.ScoreSnapshot previous = fixture.Library.GetScoreSnapshotForDiagnostics();
        fixture.BreakScoreSelect();
        int publications = 0;
        System.ComponentModel.PropertyChangedEventHandler record = ScoreSnapshotTestSupport.CreateChangeHandler(fixture.Library, _ => publications++);
        fixture.Library.PropertyChanged += record;
        try
        {
            fixture.Library.InitializeScoresOnly(null);
            Assert.AreEqual(0, fixture.Library.GetBMSScores().Count);
            Assert.AreEqual(previous.Version + 1, fixture.Library.ScoreSnapshotVersion);
            Assert.IsFalse(fixture.Library.ScoreSnapshotReady);
            fixture.AssertNoPlayProjection();
            Assert.AreEqual(1, publications);
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        }
        finally { fixture.Library.PropertyChanged -= record; }
    }

    /// <summary>通常成績の失敗と履歴の利用可否を独立して採用し、失敗も次の許可取得まで固定します。</summary>
    [TestMethod]
    public void InitializeScoresOnly_BeatorajaScoreFailureKeepsHistoryAndHistoryFailureKeepsScore()
    {
        using var fixture = new ScoreFixture(beatoraja: true, empty: false);
        fixture.CreateHistory();
        fixture.BreakScoreSelect();
        fixture.Library.InitializeScoresOnly(null);
        BMSLibrary.ScoreSnapshot scoreFailed = fixture.Library.CaptureScoreSnapshotForView();
        Assert.AreEqual(ScoreTableLoadStatus.Failed, scoreFailed.LoadStatus);
        Assert.IsFalse(scoreFailed.PlayHistorySnapshot.Beatoraja.HasErrors);
        Assert.AreEqual(1, scoreFailed.PlayHistorySnapshot.Beatoraja.Rows.Count);
        Assert.IsFalse(scoreFailed.PlayHistorySnapshot.Beatoraja.PlayerSnapshotsAvailable);

        fixture.RestoreEmptyScoreTable();
        fixture.BreakHistory();
        fixture.Library.InitializeScoresOnly(null);
        BMSLibrary.ScoreSnapshot historyFailed = fixture.Library.CaptureScoreSnapshotForView();
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, historyFailed.LoadStatus);
        Assert.IsTrue(historyFailed.PlayHistorySnapshot.Beatoraja.Diagnostics.Any(diagnostic => diagnostic.Code == "play_history_beatoraja_scorelog_missing"));
        Assert.AreEqual(Lr2PlayHistorySchemaStatus.Unreadable, historyFailed.PlayHistorySnapshot.Beatoraja.SchemaStatus);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        fixture.CreateHistory();
        Assert.AreSame(historyFailed.PlayHistorySnapshot, fixture.Library.CaptureScoreSnapshotForView().PlayHistorySnapshot);
        fixture.Library.InitializeScoresOnly(null);
        Assert.AreEqual(1, fixture.Library.CaptureScoreSnapshotForView().PlayHistorySnapshot.Beatoraja.Rows.Count);
        Assert.IsFalse(fixture.Library.CaptureScoreSnapshotForView().PlayHistorySnapshot.Beatoraja.HasErrors);
    }

    /// <summary>IR・順位後続を無効にした条件で、起動が通常成績・全履歴を先行取得し、全再初期化本体はカタログ更新後も外部DBを再取得せず取得組・版を保持します。</summary>
    [TestMethod]
    public async Task InitializeStartup_AcquiresHistoryBeforeViewAndFullReinitializeKeepsCapturedBatch()
    {
        using var fixture = new ScoreFixture(beatoraja: true, empty: false);
        fixture.CreateHistory();
        fixture.Library.InitializeStartup(null);
        BMSLibrary.ScoreSnapshot startup = fixture.Library.CaptureScoreSnapshotForView();
        Assert.IsNotNull(startup);
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, startup.LoadStatus);
        Assert.AreEqual(1, startup.PlayHistorySnapshot.Beatoraja.Rows.Count);
        Assert.AreEqual(100, startup.PlayHistorySnapshot.Beatoraja.Rows.Single().notes);
        await fixture.CompleteBackgroundWorkAsync();
        fixture.Library.BmsCharts = [ChartTestValues.Empty() with { Path = "different.bms", Md5 = new string('a', 32) }];
        Assert.AreEqual("different.bms", fixture.Library.BmsCharts.Single().Path);
        Assert.AreSame(startup.PlayHistorySnapshot, fixture.Library.CaptureScoreSnapshotForView().PlayHistorySnapshot);
        File.Move(fixture.ScorePath, fixture.ScorePath + ".moved");
        fixture.BreakHistory();
        int beforeFullNotification = fixture.Library.NormalLibraryRefreshNotificationVersion;
        int scoreNotifications = 0;
        System.ComponentModel.PropertyChangedEventHandler scoreNotification = (_, args) =>
        {
            if (args.PropertyName is nameof(BMSLibrary.ScoreSnapshotVersion) or nameof(BMSLibrary.ScoreSnapshotReady)) { scoreNotifications++; }
        };
        fixture.Library.PropertyChanged += scoreNotification;
        try
        {
            fixture.Library.Reinitialize();
            await fixture.CompleteBackgroundWorkAsync();
        }
        finally { fixture.Library.PropertyChanged -= scoreNotification; }
        // 仮のカタログ行は保存済みの空目録と走査結果で置き換わり、再初期化そのものの省略と区別します。
        Assert.AreEqual(0, fixture.Library.BmsCharts.Count);
        Assert.AreEqual(0, fixture.Library.GetNormalLibraryRefreshNotificationsAfter(beforeFullNotification).ScoreChanges.Count);
        Assert.AreEqual(0, scoreNotifications);
        BMSLibrary.ScoreSnapshot reinitialized = fixture.Library.CaptureScoreSnapshotForView();
        Assert.AreEqual(startup.Version, fixture.Library.ScoreSnapshotVersion);
        Assert.AreEqual(startup.Version, reinitialized.Version);
        Assert.AreSame(startup.PlayHistorySnapshot, reinitialized.PlayHistorySnapshot);
        Assert.AreEqual(startup.SourceGeneration, reinitialized.SourceGeneration);
        Assert.AreEqual(20, fixture.Lookup().Score);
        Assert.AreEqual(100, reinitialized.PlayHistorySnapshot.Beatoraja.Rows.Single().notes);
        Assert.AreEqual(0, fixture.Dialogs.Messages.Count);
    }

    private sealed class ScoreFixture : IDisposable
    {
        private const string Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly string root = Path.Combine(Path.GetTempPath(), "BmsSavedScoreAvailability", Guid.NewGuid().ToString("N"));
        private string scorePath;
        private readonly bool beatoraja;
        private readonly ConcurrentQueue<Func<Task>> backgroundWork = new();
        private BmsLibraryOptionsSnapshot options;
        internal TestBmsLibrary Library { get; }
        internal string ScorePath => scorePath;
        internal RecordingDialogs Dialogs { get; } = new();

        internal ScoreFixture(bool beatoraja, bool empty, bool configured = true)
        {
            this.beatoraja = beatoraja;
            Directory.CreateDirectory(root);
            string songPath = Path.Combine(root, "song.db");
            scorePath = Path.Combine(root, "score.db");
            using (var db = new LR2SongDBExtended(songPath)) { db.CreateTable<LR2SongDB.song>(); }
            using (var db = new SQLiteConnection(scorePath))
            {
                if (beatoraja)
                {
                    db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                    if (!empty) { db.Execute("INSERT INTO score VALUES (?, 0, 4, 10, 0, 0, 0, 100, 10, 1, 1, 1)", Sha256); }
                }
                else
                {
                    db.CreateTable<LR2ScoreDB.player>();
                    db.CreateTable<LR2ScoreDB.score>();
                    if (!empty) { db.Insert(new LR2ScoreDB.score { hash = Md5, perfect = 10, totalnotes = 100 }); }
                }
            }
            options = new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = beatoraja, BeatorajaScoreDbPath = configured ? scorePath : string.Empty };
            Library = new TestBmsLibrary(songPath, null, beatoraja || !configured ? null : scorePath,
                fileMutationService: null, dialogService: Dialogs,
                uiScheduler: new TestUiScheduler(() => null), optionsSnapshotProvider: () => options)
            { BmsCharts = [] };
            Library.StartupBackgroundTaskScheduler = (_, _, _, work) =>
            {
                backgroundWork.Enqueue(work);
                return true;
            };
        }

        /// <summary>起動の後続workerを順に実行し、終端後に次の明示操作へ進めます。Taskを捨てて受付拒否を避けません。</summary>
        internal async Task CompleteBackgroundWorkAsync()
        {
            while (backgroundWork.TryDequeue(out Func<Task>? work)) { await work(); }
        }

        internal ChartScoreSnapshot Lookup() => Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "retained.bms", Md5, Sha256);

        internal void AssertNoPlayProjection()
        {
            var chart = new ChartFile(ChartFileKind.Bms, "retained.bms", Md5, Sha256,
                "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
            var row = LibraryChartRow.FromChartFile(chart);
            row.SetScoreProjectionProvider(current => Library.ResolveChartScoreSnapshot(current.Kind, current.Path, current.Md5, current.Sha256),
                () => Library.ScoreSnapshotVersion);
            Assert.AreEqual(ClearType.NO_PLAY, row.Chart.Score.Clear);
            Assert.IsNull(row.Chart.Score.Score);
            Assert.IsNull(Lookup().Score);
        }

        internal void CreateHistory()
        {
            using var db = new SQLiteConnection(Path.Combine(root, "scorelog.db"));
            db.Execute("CREATE TABLE scorelog (sha256 TEXT, mode INTEGER, clear INTEGER, oldclear INTEGER, score INTEGER, oldscore INTEGER, combo INTEGER, oldcombo INTEGER, minbp INTEGER, oldminbp INTEGER, date INTEGER)");
            db.Execute("INSERT INTO scorelog VALUES (?, 0, 5, 4, 20, 10, 0, 0, 0, 1, 1000)", Sha256);
        }

        internal void BreakHistory() => File.Delete(Path.Combine(root, "scorelog.db"));

        internal void SelectBrokenBeatorajaDatabase()
        {
            scorePath = Path.Combine(root, "other-score.db");
            using (var db = new SQLiteConnection(scorePath)) { db.Execute("CREATE TABLE unrelated (id INTEGER)"); }
            options = new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = true, BeatorajaScoreDbPath = scorePath };
        }

        internal void BreakScoreSelect()
        {
            using var db = new SQLiteConnection(scorePath);
            db.Execute("ALTER TABLE score RENAME TO retained_score");
            // 本番と同じ対象SELECTが実際に失敗する状態を確認し、単なる正常空を故障入力にしません。
            if (beatoraja) { Assert.ThrowsException<SQLiteException>(() => new BeatorajaScoreDbLoader().LoadModeZeroScores(scorePath)); }
            else
            {
                using var read = new LR2ScoreDBExtended(scorePath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                Assert.ThrowsException<SQLiteException>(() => read.Table<BMSScore>().ToList());
            }
        }

        internal void RestoreEmptyScoreTable()
        {
            using var db = new SQLiteConnection(scorePath);
            db.Execute("ALTER TABLE retained_score RENAME TO score");
            db.Execute("DELETE FROM score");
        }

        public void Dispose()
        {
            Library.RequestShutdown("saved-score-fixture-cleanup");
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingDialogs : IBmsLibraryDialogService
    {
        internal List<(string Message, string Caption, UiDialogButton Button, UiDialogIcon Icon)> Messages { get; } = [];
        internal Action? Observe { get; set; }
        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon,
            UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            Messages.Add((messageBoxText, caption, button, icon));
            Observe?.Invoke();
            return UiDialogDefaultResult.OK;
        }
    }
}
