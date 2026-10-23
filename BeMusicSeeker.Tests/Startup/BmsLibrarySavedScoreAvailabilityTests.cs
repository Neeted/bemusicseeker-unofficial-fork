using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>保存成績の実再読込みで、有効な旧結果の保持と次の通常要求への接続を確認します。</summary>
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

    /// <summary>
    /// 同じLR2・beatoraja DBのSELECT失敗では採用済み成績・lookup・購読・時刻・版を保ち、
    /// 正常0件も有効な旧結果として扱う。障害解消後は正常空を採用し、旧値を永久保持しない。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void InitializeScoresOnly_FailedReloadRetainsLoadedResultAndNextSuccessAdoptsEmpty(bool beatoraja, bool empty)
    {
        using var fixture = new ScoreFixture(beatoraja, empty);
        fixture.Library.InitializeScoresOnly(null);
        BMSLibrary.ScoreSnapshot loaded = fixture.Library.GetScoreSnapshotForDiagnostics();
        BMSScore? score = beatoraja ? loaded.ScoresBySha256.Values.SingleOrDefault() : loaded.Scores.SingleOrDefault();
        if (score != null) { score.ranking = 3; }
        BMSLibrary.ScoreSnapshot previous = fixture.Library.GetScoreSnapshotForDiagnostics();
        DateTime loadedAtUtc = previous.LoadedAtUtc;
        int loadedVersion = previous.Version;
        Assert.IsTrue(fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(empty ? 0 : 1, fixture.Library.ScoreSubscriptionCount);
        fixture.BreakScoreSelect();

        fixture.Library.InitializeScoresOnly(null);

        Assert.AreSame(previous, fixture.Library.GetScoreSnapshotForDiagnostics());
        Assert.AreEqual(loadedAtUtc, fixture.Library.GetScoreSnapshotForDiagnostics().LoadedAtUtc);
        Assert.AreEqual(loadedVersion, fixture.Library.ScoreSnapshotVersion);
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, previous.LoadStatus);
        Assert.IsTrue(fixture.Library.ScoreSnapshotReady);
        Assert.AreEqual(empty ? 0 : 1, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        Assert.AreEqual(empty ? null : 20, fixture.Lookup().Score);
        if (score != null)
        {
            int version = fixture.Library.ScoreSnapshotVersion;
            score.ranking = 7;
            Assert.AreEqual(version + 1, fixture.Library.ScoreSnapshotVersion);
            Assert.AreEqual(7, fixture.Lookup().Ranking, "保持した実体の通常setterと購読も引き続き利用します。");
        }

        fixture.RestoreEmptyScoreTable();
        fixture.Library.InitializeScoresOnly(null);

        Assert.AreNotSame(previous, fixture.Library.GetScoreSnapshotForDiagnostics());
        Assert.AreEqual(ScoreTableLoadStatus.Loaded, fixture.Library.GetScoreSnapshotForDiagnostics().LoadStatus);
        Assert.IsTrue(fixture.Library.ScoreSnapshotReady);
        Assert.IsNull(fixture.Lookup().Score);
        Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
    }

    /// <summary>実再読込みの失敗案内は旧成績を利用可能に保ち、親Lと初期化排他の解放後に既存警告文言で一度届きます。</summary>
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
        Assert.AreEqual((true, true, true, (int?)20), observations.Single());
        Assert.AreEqual((Resources.Error_RecommendationScoreReadFailed, Resources.MessageBoxTitle_Warning,
            UiDialogButton.OK, UiDialogIcon.Exclamation), fixture.Dialogs.Messages.Single());
    }

    /// <summary>再読込み失敗で旧成績を保っても、無効設定に従う未送信表示の解除は一回のsnapshot公開にまとめ、元の取得時刻を維持します。</summary>
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
            Assert.IsFalse(score.IsLr2IrScoreUnsent);
            Assert.AreSame(score, fixture.Library.GetBMSScores().Single());
            Assert.AreEqual(previous.Version + 1, fixture.Library.ScoreSnapshotVersion);
            Assert.AreEqual(previous.LoadedAtUtc, fixture.Library.GetScoreSnapshotForDiagnostics().LoadedAtUtc);
            Assert.IsTrue(fixture.Library.ScoreSnapshotReady);
            Assert.AreEqual(20, fixture.Lookup().Score);
            Assert.AreEqual(1, publications);
            Assert.AreEqual(1, fixture.Dialogs.Messages.Count);
        }
        finally { fixture.Library.PropertyChanged -= record; }
    }

    private sealed class ScoreFixture : IDisposable
    {
        private const string Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private readonly string root = Path.Combine(Path.GetTempPath(), "BmsSavedScoreAvailability", Guid.NewGuid().ToString("N"));
        private string scorePath;
        private readonly bool beatoraja;
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
                uiScheduler: new TestUiScheduler(() => Dispatcher.CurrentDispatcher), optionsSnapshotProvider: () => options)
            { BmsCharts = [] };
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
