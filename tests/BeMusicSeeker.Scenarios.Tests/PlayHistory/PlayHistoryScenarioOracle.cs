using System.Globalization;
using System.IO;
using System.Xml.Linq;
using BeMusicSeeker.Runtime;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.PlayHistory;

/// <summary>外部書込みの宣言値をraw SQLiteと実表示へ別々に照合します。</summary>
internal static class PlayHistoryScenarioOracle
{
    /// <summary>製品の期間factoryを使わず、更新後を基準とする7暦日の範囲に実書込み前後が入ることを確認します。</summary>
    internal static string Recent7DaysAnchor(long from, long to)
    {
        Assert.IsTrue(from <= to);
        DateTime firstDate = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(from), TimeZoneInfo.Local).Date;
        DateTime anchorDate = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(to), TimeZoneInfo.Local).Date;
        DateTime start = anchorDate.AddDays(-6);
        DateTime end = anchorDate.AddDays(1);
        Assert.IsTrue(firstDate >= start && firstDate < end);
        Assert.IsTrue(anchorDate >= start && anchorDate < end);
        return to.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>実導入後も既存scoreから過去履歴が作られず、score値を維持することを確認します。</summary>
    internal static void InstalledWithoutBackfill(PlayHistoryScenarioFixture fixture)
    {
        using var db = new SQLiteConnection(fixture.ScoreDb, SQLiteOpenFlags.ReadOnly);
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM bms_lr2_play_history"));
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM bms_lr2_last_play"));
        Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT playcount FROM score WHERE hash=?", fixture.Md5));
    }

    /// <summary>triggerのID・時刻だけを観測し、hash・回数差・成績・player差分は事前の外部入力と照合します。</summary>
    internal static RawHistory[] Lr2Raw(PlayHistoryScenarioFixture fixture, long from, long to)
    {
        using var db = new SQLiteConnection(fixture.ScoreDb, SQLiteOpenFlags.ReadOnly);
        RawHistory[] rows = db.Query<RawHistory>("SELECT history_id,hash,played_at,finalized,playcount_delta,new_exscore,new_minbp,playtime_delta FROM bms_lr2_play_history").ToArray();
        CollectionAssert.AreEquivalent(new[] { fixture.Md5, PlayHistoryScenarioFixture.UnownedMd5 }, rows.Select(row => row.hash).ToArray());
        Assert.AreEqual(2, rows.Select(row => row.history_id).Distinct().Count());
        foreach (RawHistory row in rows)
        {
            Assert.IsTrue(row.history_id > 0);
            Assert.IsTrue(row.played_at >= from && row.played_at <= to);
            Assert.AreEqual(1, row.finalized); Assert.AreEqual(1, row.playcount_delta);
            bool owned = row.hash == fixture.Md5;
            Assert.AreEqual(owned ? 120 : 45, row.new_exscore);
            Assert.AreEqual(owned ? 5 : 10, row.new_minbp);
            Assert.AreEqual(owned ? 30 : 15, row.playtime_delta);
        }
        return rows;
    }

    /// <summary>実期間・検索が採用したLR2行とsummaryを、trigger行と事前の期待値へ照合します。</summary>
    internal static void Lr2View(PlayHistoryScenarioFixture fixture, RawHistory[] raw, ScenarioHistoryCompleted actual, bool keyword)
    {
        Assert.AreEqual("Lr2", actual.Provider);
        Assert.IsTrue(actual.ViewRequestId > 0);
        string[] expected = keyword ? [fixture.Md5] : [fixture.Md5, PlayHistoryScenarioFixture.UnownedMd5];
        CollectionAssert.AreEquivalent(expected, actual.Rows.Select(row => row.Hash).ToArray());
        Assert.AreEqual(expected.Length, actual.Rows.Length);
        Assert.AreEqual((long)expected.Length, actual.FinalizedRows);
        Assert.AreEqual(expected.Length, actual.ClearUpdateCount, "所持状態にかかわらず対象行のクリア更新を数えます。");
        Assert.IsTrue(actual.PlayCountAvailable); Assert.IsTrue(actual.PlaytimeAvailable);
        Assert.AreEqual(keyword ? 30L : 45L, actual.PlaytimeSeconds);
        foreach (ScenarioHistoryRow row in actual.Rows)
        {
            RawHistory source = raw.Single(item => item.hash == row.Hash);
            Assert.AreEqual(source.history_id, row.HistoryId); Assert.AreEqual(source.played_at, row.PlayedAt);
            Assert.AreEqual(fixture.ScoreDb, row.SourcePath);
            Assert.IsTrue(row.Finalized); Assert.AreEqual(1, row.PlaycountDelta);
            Assert.AreEqual((int?)source.new_exscore, row.NewExscore); Assert.AreEqual((int?)source.new_minbp, row.NewBp);
            Assert.AreEqual(row.Hash == fixture.Md5 ? "HARD" : "CLEAR", row.NewClear);
            if (row.Hash == fixture.Md5) { Assert.IsTrue(fixture.OwnedPaths.Contains(row.Path)); }
            else { Assert.IsTrue(string.IsNullOrEmpty(row.Path)); }
        }
    }

    /// <summary>選択playerの固定日の更新記録だけを読み、境界外・別mode・別playerを混ぜず未知の実績を0回と扱わないことを確認します。</summary>
    internal static void BeatorajaView(PlayHistoryScenarioFixture fixture, ScenarioHistoryCompleted actual, bool keyword)
    {
        Assert.AreEqual("Beatoraja", actual.Provider);
        Assert.IsTrue(actual.ViewRequestId > 0);
        string[] expected = keyword ? [fixture.Sha256] : [fixture.Sha256, PlayHistoryScenarioFixture.UnownedSha];
        CollectionAssert.AreEquivalent(expected, actual.Rows.Select(row => row.Hash).ToArray());
        Assert.AreEqual(expected.Length, actual.Rows.Length);
        Assert.IsFalse(actual.PlayCountAvailable); Assert.IsFalse(actual.PlaytimeAvailable);
        Assert.AreEqual(expected.Length, actual.ClearUpdateCount, "所持状態にかかわらず対象行のクリア更新を数えます。");
        using var db = new SQLiteConnection(fixture.BeatLog, SQLiteOpenFlags.ReadOnly);
        foreach (ScenarioHistoryRow row in actual.Rows)
        {
            bool owned = row.Hash == fixture.Sha256;
            long date = owned ? fixture.BeatFrom : fixture.BeatTo - 1;
            Assert.AreEqual(fixture.BeatLog, row.SourcePath); Assert.AreEqual(date, row.PlayedAt);
            Assert.AreEqual(owned ? (int?)120 : 45, row.NewExscore); Assert.AreEqual((int?)5, row.NewBp);
            Assert.AreEqual(0, row.PlaycountDelta, "scorelog更新記録を一回の実プレイと数えません。");
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM scorelog WHERE rowid=? AND sha256=? AND date=? AND mode=0 AND score=?",
                row.HistoryId, row.Hash, date, owned ? 120 : 45));
            if (owned) { Assert.IsTrue(fixture.OwnedPaths.Contains(row.Path)); }
            else { Assert.IsTrue(string.IsNullOrEmpty(row.Path)); }
        }
    }

    /// <summary>履歴からの移動先を非空の完全path集合で確認します。</summary>
    internal static void Navigation(PlayHistoryScenarioFixture fixture, ScenarioHistoryCompleted actual)
    {
        CollectionAssert.AreEquivalent(fixture.OwnedPaths, actual.Paths.ToArray());
        foreach (string path in fixture.OwnedPaths) { CollectionAssert.AreEqual(fixture.Bytes, File.ReadAllBytes(path)); }
    }

    /// <summary>実Windowで採用された一曲のランプを通常scoreと対応させます。</summary>
    internal static void Lamp(ScenarioHistoryCompleted actual, string category)
    {
        Assert.AreEqual("Ready", actual.LampState); Assert.AreEqual(1, actual.LampTotal); Assert.AreEqual((int?)1, actual.LampPlayed);
        CollectionAssert.AreEquivalent(new[] { category }, actual.LampSegments.Where(segment => segment.Count > 0).Select(segment => segment.Category).ToArray());
        Assert.AreEqual(1, actual.LampSegments.Sum(segment => segment.Count));
    }

    /// <summary>製品providerを再利用せず、保存済みXMLの指定値を読みます。</summary>
    internal static string Setting(PlayHistoryScenarioFixture fixture, string name)
        => XDocument.Load(fixture.SettingsPath).Descendants("setting").Single(item => (string?)item.Attribute("name") == name)
            .Element("value")?.Value ?? throw new InvalidDataException(name);

    /// <summary>外部更新後のraw履歴をSQLite列名で受け取る読取り専用検査用の形です。</summary>
    internal sealed class RawHistory
    {
        public long history_id { get; set; }
        public string hash { get; set; } = "";
        public long played_at { get; set; }
        public int finalized { get; set; }
        public int playcount_delta { get; set; }
        public int new_exscore { get; set; }
        public int new_minbp { get; set; }
        public int playtime_delta { get; set; }
    }
}
