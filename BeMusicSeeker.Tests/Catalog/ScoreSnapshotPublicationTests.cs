using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>実score setterと全lookup再公開から、独立rangeが通常queueへ登録されるmodel境界を検証します。</summary>
[TestClass]
public sealed class ScoreSnapshotPublicationTests
{
    /// <summary>背景score16/128件で二つの実setterと二lookupの仕事を固定し、lookup全列挙・複製なしで連続rangeを登録する。</summary>
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void LocalSetters_RegisterContinuousRangesWithoutCloningScoreLookup(int count)
    {
        using var fixture = new Fixture(count);
        var work = new List<string>();
        fixture.Library.ScoreSnapshotWorkObserver = work.Add;
        fixture.Library.InitializeScoresOnly(null);
        Assert.IsTrue(work.Count(value => value == "score_lookup_source_enumerated") >= count);
        BMSLibrary.ScoreSnapshot before = fixture.Library.GetScoreSnapshotForDiagnostics();
        int handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        int catalogRevision = fixture.Library.CatalogRevision;
        work.Clear();
        before.ScoresByHash[Fixture.Hash(1)].ranking = 40;
        before.ScoresByHash[Fixture.Hash(2)].ranking = 5;
        Assert.AreEqual(40, fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "a.bms", Fixture.Hash(1), null).Ranking);
        Assert.AreEqual(5, fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "b.bms", Fixture.Hash(2), null).Ranking);
        NormalLibraryRefreshNotificationBatch batch = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled);
        Assert.AreEqual(2, batch.ScoreChanges.Count);
        Assert.AreEqual(before.Version, batch.ScoreChanges[0].BaseVersion);
        Assert.AreEqual(before.Version + 1, batch.ScoreChanges[0].Version);
        Assert.AreEqual(before.Version + 1, batch.ScoreChanges[1].BaseVersion);
        Assert.AreEqual(before.Version + 2, batch.ScoreChanges[1].Version);
        CollectionAssert.AreEqual(new[] { Fixture.Hash(1) }, batch.ScoreChanges[0].Md5Keys.ToArray());
        CollectionAssert.AreEqual(new[] { Fixture.Hash(2) }, batch.ScoreChanges[1].Md5Keys.ToArray());
        Assert.AreEqual(catalogRevision, fixture.Library.CatalogRevision);
        BMSLibrary.ScoreSnapshot after = fixture.Library.GetScoreSnapshotForDiagnostics();
        Assert.AreSame(before.Scores, after.Scores);
        Assert.AreSame(before.ScoresByHash, after.ScoresByHash);
        Assert.AreSame(before.ScoresBySha256, after.ScoresBySha256);
        Assert.AreEqual(2, work.Count(value => value == "score_subscription_lookup"));
        Assert.AreEqual(2, work.Count(value => value == "score_projection_lookup"));
        Assert.AreEqual(2, work.Count(value => value == "score_projection_value_copied"));
        Assert.AreEqual(0, work.Count(value => value.Contains("enumerated", StringComparison.Ordinal) || value.Contains("reference_copied", StringComparison.Ordinal)));
    }

    /// <summary>全lookup採用は旧新keyのunionと新購読を同じ版で登録し、削除・LR2からSHA情報源切替・空lookup再公開も欠落させない。</summary>
    [TestMethod]
    public void FullLookupPublication_RegistersOldNewKeysAndEmptyVersionPosition()
    {
        using var fixture = new Fixture(2);
        fixture.Library.InitializeScoresOnly(null);
        BMSLibrary.ScoreSnapshot before = fixture.Library.GetScoreSnapshotForDiagnostics();
        BMSScore retired = before.ScoresByHash[Fixture.Hash(1)];
        fixture.ReplaceLr2Keys([2, 3]);
        int handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        fixture.Library.InitializeScoresOnly(null);
        ScoreSnapshotChange replacement = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled).ScoreChanges.Single();
        Assert.AreEqual(before.Version, replacement.BaseVersion);
        CollectionAssert.AreEquivalent(new[] { Fixture.Hash(1), Fixture.Hash(2), Fixture.Hash(3) }, replacement.Md5Keys.ToArray());
        Assert.AreEqual(2, fixture.Library.ScoreSubscriptionCount);
        int adoptedVersion = fixture.Library.ScoreSnapshotVersion;
        retired.ranking = 99;
        Assert.AreEqual(adoptedVersion, fixture.Library.ScoreSnapshotVersion);
        BMSScore adopted = fixture.Library.GetScoreSnapshotForDiagnostics().ScoresByHash[Fixture.Hash(3)];
        adopted.ranking = 7;
        Assert.AreEqual(adoptedVersion + 1, fixture.Library.ScoreSnapshotVersion);

        handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        fixture.UseBeatoraja();
        fixture.Library.InitializeScoresOnly(null);
        Assert.IsTrue(fixture.Library.ScoreSnapshotReady, "切替先の実DBが設定・loaderの受理条件を満たしている必要があります。");
        Assert.AreEqual(ActiveScoreSource.Beatoraja, fixture.Library.GetActiveScoreSourceForDiagnostics());
        ScoreSnapshotChange switched = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled).ScoreChanges.Single();
        CollectionAssert.AreEquivalent(new[] { Fixture.Hash(2), Fixture.Hash(3) }, switched.Md5Keys.ToArray());
        CollectionAssert.AreEqual(new[] { Fixture.Sha256 }, switched.Sha256Keys.ToArray());
        Assert.AreEqual(1, fixture.Library.ScoreSubscriptionCount);
        Assert.AreEqual(20, fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "sha.bms", null, Fixture.Sha256).Score);
        Assert.AreEqual(20, fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "captured.bms", Fixture.Hash(1), null, before).Score);
        Assert.IsNull(fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "current.bms", Fixture.Hash(1), null).Score);
        Assert.IsNull(fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bmson, "bmson.bmson", Fixture.Hash(1), null, before).Score);
        adoptedVersion = fixture.Library.ScoreSnapshotVersion;
        adopted.ranking = 8;
        Assert.AreEqual(adoptedVersion, fixture.Library.ScoreSnapshotVersion);
        handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        BMSScore shaScore = fixture.Library.GetScoreSnapshotForDiagnostics().ScoresBySha256[Fixture.Sha256];
        shaScore.ranking = 9;
        ScoreSnapshotChange shaChange = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled).ScoreChanges.Single();
        Assert.AreEqual(adoptedVersion, shaChange.BaseVersion);
        Assert.AreEqual(adoptedVersion + 1, shaChange.Version);
        Assert.AreEqual(0, shaChange.Md5Keys.Count);
        CollectionAssert.AreEqual(new[] { Fixture.Sha256 }, shaChange.Sha256Keys.ToArray());
        Assert.AreEqual(9, fixture.Library.ResolveChartScoreSnapshot(ChartFileKind.Bms, "sha.bms", null, Fixture.Sha256).Ranking);

        fixture.ClearBeatoraja();
        handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        fixture.Library.InitializeScoresOnly(null);
        ScoreSnapshotChange removed = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled).ScoreChanges.Single();
        CollectionAssert.AreEqual(new[] { Fixture.Sha256 }, removed.Sha256Keys.ToArray());
        Assert.AreEqual(0, fixture.Library.ScoreSubscriptionCount);
        handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        fixture.Library.InitializeScoresOnly(null);
        ScoreSnapshotChange empty = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled).ScoreChanges.Single();
        Assert.AreEqual(removed.Version, empty.BaseVersion);
        Assert.AreEqual(removed.Version + 1, empty.Version);
        Assert.AreEqual(0, empty.Md5Keys.Count + empty.Sha256Keys.Count);
    }

    /// <summary>実setterの前後にcatalog全置換があってもscore rangeを保ち、wakeを逆順にdrainしても登録順を欠落・逆転しない。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScoreQueue_PreservesRangesAcrossCatalogResetAndReversedWake(bool scoreFirst)
    {
        using var fixture = new Fixture(2);
        fixture.Library.InitializeScoresOnly(null);
        BMSLibrary.ScoreSnapshot lookup = fixture.Library.GetScoreSnapshotForDiagnostics();
        int handled = fixture.Library.NormalLibraryRefreshNotificationVersion;
        var wakes = new List<Action>();
        var delivered = new List<ScoreSnapshotChange>();
        PropertyChangedEventHandler wake = (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
            {
                wakes.Add(() =>
                {
                    NormalLibraryRefreshNotificationBatch batch = fixture.Library.GetNormalLibraryRefreshNotificationsAfter(handled);
                    handled = batch.LatestVersion;
                    delivered.AddRange(batch.ScoreChanges);
                });
            }
        };
        fixture.Library.PropertyChanged += wake;
        try
        {
            if (scoreFirst) { lookup.ScoresByHash[Fixture.Hash(1)].ranking = 40; }
            fixture.Library.BmsCharts = [ChartTestValues.Empty() with { Path = "replacement.bms", Md5 = Fixture.Hash(9) }];
            if (!scoreFirst) { lookup.ScoresByHash[Fixture.Hash(1)].ranking = 40; }
            lookup.ScoresByHash[Fixture.Hash(2)].ranking = 5;
            for (int index = wakes.Count - 1; index >= 0; index--) { wakes[index](); }
            Assert.AreEqual(2, delivered.Count);
            Assert.AreEqual(lookup.Version, delivered[0].BaseVersion);
            Assert.AreEqual(lookup.Version + 1, delivered[0].Version);
            Assert.AreEqual(delivered[0].Version, delivered[1].BaseVersion);
            Assert.AreEqual(fixture.Library.ScoreSnapshotVersion, delivered[1].Version);
            CollectionAssert.AreEqual(new[] { Fixture.Hash(1), Fixture.Hash(2) }, delivered.SelectMany(change => change.Md5Keys).ToArray());
        }
        finally { fixture.Library.PropertyChanged -= wake; }
    }

    /// <summary>補助通知だけの破棄は独立score結果を消さず、catalog resetの整理とも独立して同じrangeを返す。</summary>
    [TestMethod]
    public void Publisher_LegacyClearPreservesIndependentScoreRange()
    {
        var publisher = new NormalLibraryRefreshPublisher();
        var score = new ScoreSnapshotChange(4, 5, ["a"], []);
        int scoreNotification = publisher.Register(score);
        int legacy = publisher.Publish(new NormalLibraryRefreshPublishRequest { ResetsPriorNotifications = true });
        publisher.ClearLegacyNotification(legacy);
        publisher.ClearLegacyNotification(scoreNotification);
        NormalLibraryRefreshNotificationBatch batch = publisher.GetNotificationsAfter(0);
        Assert.AreSame(score, batch.ScoreChanges.Single());
        Assert.IsTrue(batch.HasRefreshNotification);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Lr2SongDbSyncTestSupport.TestDatabaseScope database = Lr2SongDbSyncTestSupport.TestDatabaseScope.Create();
        private string Root => database.DirectoryPath;
        private readonly string scorePath;
        private readonly string beatorajaPath;
        private BmsLibraryOptionsSnapshot options = new();
        internal const string Sha256 = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        internal TestBmsLibrary Library { get; }
        internal static string Hash(int index) => index.ToString("x32");
        internal Fixture(int count)
        {
            scorePath = Path.Combine(Root, "score.db");
            // 設定側の既存受理条件はファイル名score.dbです。LR2と別directoryへ実DBを準備します。
            string beatorajaDirectory = Path.Combine(Root, "beatoraja");
            Directory.CreateDirectory(beatorajaDirectory);
            beatorajaPath = Path.Combine(beatorajaDirectory, "score.db");
            ReplaceLr2Keys(Enumerable.Range(1, count));
            using (var db = new SQLite.SQLiteConnection(beatorajaPath))
            {
                db.Execute("CREATE TABLE score (sha256 TEXT, mode INTEGER, clear INTEGER, epg INTEGER, lpg INTEGER, egr INTEGER, lgr INTEGER, notes INTEGER, combo INTEGER, minbp INTEGER, playcount INTEGER, clearcount INTEGER)");
                db.Execute("INSERT INTO score VALUES (?, 0, 4, 10, 0, 0, 0, 100, 10, 1, 1, 1)", Sha256);
            }
            Library = new TestBmsLibrary(database.SongDbPath, null, scorePath,
                fileMutationService: null, dialogService: null, uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                optionsSnapshotProvider: () => options);
        }
        internal void ReplaceLr2Keys(IEnumerable<int> indices)
        {
            using var db = new LR2ScoreDBExtended(scorePath);
            db.CreateTable<LR2ScoreDB.player>();
            db.CreateTable<LR2ScoreDB.score>();
            db.DeleteAll<LR2ScoreDB.score>();
            foreach (int index in indices) { db.Insert(new LR2ScoreDB.score { hash = Hash(index), perfect = 10, totalnotes = 100 }); }
        }
        internal void UseBeatoraja() => options = new BmsLibraryOptionsSnapshot { UseBeatorajaScoreDb = true, BeatorajaScoreDbPath = beatorajaPath };
        internal void ClearBeatoraja() { using var db = new SQLite.SQLiteConnection(beatorajaPath); db.Execute("DELETE FROM score"); }
        public void Dispose()
        {
            try { Library.RequestShutdown("score-publication-fixture"); }
            finally { database.Dispose(); }
        }
    }
}
