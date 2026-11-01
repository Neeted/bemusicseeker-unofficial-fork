using System.IO;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.Library;

/// <summary>本番の reader・parser を使わず、SC01 の保存結果とファイルの非破壊性を確認します。</summary>
internal static class Sc01LibraryOracle
{
    /// <summary>走査完了後の譜面集合・ハッシュ・title・既存利用者列・資源情報と元 bytes を確認します。</summary>
    /// <remarks>呼出元は実アプリの操作完了を先に待ちます。表示 snapshot との照合は呼出元が担当します。</remarks>
    internal static void AssertDatabaseAndFiles(Sc01LibraryFixture fixture, bool afterExternalChanges)
    {
        IReadOnlyList<Sc01ChartManifest> expected = afterExternalChanges ? fixture.UpdatedCharts : fixture.InitialCharts;
        using (var db = new SQLiteConnection(fixture.SongDatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex))
        {
            Assert.AreEqual(expected.Count(chart => chart.Format == Sc01ChartFormat.Bms), db.ExecuteScalar<int>("SELECT COUNT(*) FROM song"), "song の譜面集合が異なります。");
            Assert.AreEqual(expected.Count(chart => chart.Format == Sc01ChartFormat.Bmson), db.ExecuteScalar<int>("SELECT COUNT(*) FROM bmson_song"), "bmson_song の譜面集合が異なります。");
            Assert.AreEqual(expected.Count, db.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance"), "maintenance に欠落または削除済み譜面が残っています。");
            foreach (Sc01ChartManifest chart in expected)
            {
                string table = chart.Format == Sc01ChartFormat.Bms ? "song" : "bmson_song";
                string md5Column = chart.Format == Sc01ChartFormat.Bms ? "hash" : "md5";
                Assert.AreEqual(1, db.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE path = ?", chart.Path), chart.Path);
                Assert.AreEqual(chart.Title, db.ExecuteScalar<string>($"SELECT title FROM {table} WHERE path = ?", chart.Path), chart.Path);
                Assert.AreEqual(chart.Md5, db.ExecuteScalar<string>($"SELECT {md5Column} FROM {table} WHERE path = ?", chart.Path), chart.Path);
                // SHA-256 の保存正本は BMS と BMSON で異なるため、それぞれの経路を照合します。
                if (chart.Format == Sc01ChartFormat.Bms)
                {
                    Assert.AreEqual(chart.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5 = ?", chart.Md5), chart.Path);
                }
                else
                {
                    Assert.AreEqual(chart.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM bmson_song WHERE path = ?", chart.Path), chart.Path);
                }
                AssertMaintenance(db, chart);
            }
            Assert.AreEqual(Sc01LibraryFixture.PersistedTag, db.ExecuteScalar<string>("SELECT tag FROM song WHERE path = ?", fixture.B.Path));
            Assert.AreEqual(Sc01LibraryFixture.PersistedFavorite, db.ExecuteScalar<int>("SELECT favorite FROM song WHERE path = ?", fixture.B.Path));
            Assert.AreEqual(Sc01LibraryFixture.PersistedAddDate, db.ExecuteScalar<int>("SELECT adddate FROM song WHERE path = ?", fixture.B.Path));
        }
        foreach (Sc01ChartManifest chart in expected) { AssertSourceBytes(chart); }
        if (afterExternalChanges)
        {
            Assert.IsFalse(File.Exists(fixture.A.Path), "外部削除済み A が復元されています。");
            // A の譜面ファイルだけを外部削除しても、その音源の所有権は走査へ渡しません。
            foreach (Sc01ResourceManifest resource in fixture.A.Resources) { AssertFileBytes(resource.Path, resource.RawBytes); }
        }
        else { Assert.IsFalse(File.Exists(fixture.D.Path), "外部追加前の D が作られています。"); }
        AssertFileBytes(fixture.SentinelPath, fixture.SentinelBytes);
    }

    private static void AssertMaintenance(SQLiteConnection db, Sc01ChartManifest chart)
    {
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(chart.Md5, db.ExecuteScalar<string>("SELECT hash FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(chart.Resources.Count(resource => resource.Kind == "audio"), db.ExecuteScalar<int?>("SELECT wav_files_defined FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(chart.Resources.Count(resource => resource.Kind == "audio"), db.ExecuteScalar<int?>("SELECT wav_files_existing FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(0, db.ExecuteScalar<int?>("SELECT bga_files_defined FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(0, db.ExecuteScalar<int?>("SELECT bga_files_existing FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(0, db.ExecuteScalar<int?>("SELECT movie_files_defined FROM maintenance WHERE path = ?", chart.Path), chart.Path);
        Assert.AreEqual(0, db.ExecuteScalar<int?>("SELECT movie_files_existing FROM maintenance WHERE path = ?", chart.Path), chart.Path);
    }

    private static void AssertSourceBytes(Sc01ChartManifest chart)
    {
        AssertFileBytes(chart.Path, chart.RawBytes);
        foreach (Sc01ResourceManifest resource in chart.Resources) { AssertFileBytes(resource.Path, resource.RawBytes); }
    }

    private static void AssertFileBytes(string path, ReadOnlyMemory<byte> expected)
    {
        Assert.IsTrue(File.Exists(path), path);
        CollectionAssert.AreEqual(expected.ToArray(), File.ReadAllBytes(path), path);
    }
}
