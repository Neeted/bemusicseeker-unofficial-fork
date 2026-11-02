using System.IO;
using System.Security.Cryptography;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.Install;

/// <summary>操作前宣言からraw SQLiteと全実ファイルを照合し、再起動による修復に依存しません。</summary>
internal static class MissingDestinationOracle
{
    /// <summary>外部改名とDB再読込を分け、失敗時の保全・成功時の消費を検査します。</summary>
    internal static void AssertState(MissingDestinationFixture fixture, bool renamed, bool reloaded, bool pending, bool installed)
    {
        string physical = renamed ? fixture.Renamed : fixture.Original;
        string saved = reloaded ? fixture.Renamed : fixture.Original;
        var expectedFiles = fixture.ExistingFiles.Select(file => (Path.Combine(physical, file.Name), file))
            .Concat(fixture.SentinelFiles.Select(file => (Path.Combine(fixture.Library, "Sentinel", file.Name), file)))
            .Concat(installed ? fixture.DifferenceFiles.Select(file => (Path.Combine(fixture.Renamed, file.Name), file)) : []).ToArray();
        CollectionAssert.AreEquivalent(expectedFiles.Select(pair => pair.Item1).ToArray(), Directory.GetFiles(fixture.Library, "*", SearchOption.AllDirectories));
        foreach ((string path, MissingDestinationFile file) in expectedFiles) { AssertFile(path, file); }
        if (renamed) { Assert.IsFalse(Directory.Exists(fixture.Original), "消失した明示先を再作成してはいけません。"); }
        foreach (MissingDestinationFile file in fixture.DifferenceFiles)
        {
            string path = Path.Combine(fixture.Source, file.Name);
            if (installed) { Assert.IsFalse(File.Exists(path), "成功した移動元は消費されます。"); }
            else { AssertFile(path, file); }
        }
        if (Directory.Exists(fixture.Source))
        { CollectionAssert.AreEquivalent(installed ? Array.Empty<string>() : fixture.DifferenceFiles.Select(file => Path.Combine(fixture.Source, file.Name)).ToArray(), Directory.GetFiles(fixture.Source, "*", SearchOption.AllDirectories)); }
        using var db = new SQLiteConnection(fixture.Database, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        var charts = new List<(string Path, MissingDestinationFile File)>
        {
            (Path.Combine(saved, fixture.Existing.Name), fixture.Existing),
            (Path.Combine(fixture.Library, "Sentinel", fixture.Sentinel.Name), fixture.Sentinel)
        };
        if (installed) { charts.Add((Path.Combine(fixture.Renamed, fixture.Difference.Name), fixture.Difference)); }
        CollectionAssert.AreEquivalent(charts.Select(pair => pair.Path).ToArray(), db.Query<PathRow>("SELECT path FROM song").Select(row => row.Path).ToArray());
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT count(*) FROM bmson_song"));
        foreach ((string path, MissingDestinationFile file) in charts)
        {
            Assert.AreEqual(file.Md5, db.ExecuteScalar<string>("SELECT hash FROM song WHERE path=?", path));
            Assert.AreEqual(file.Title, db.ExecuteScalar<string>("SELECT title FROM song WHERE path=?", path));
            Assert.AreEqual(file.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5=?", file.Md5));
            if (file == fixture.Difference) { continue; }
            Assert.AreEqual("利用者タグ", db.ExecuteScalar<string>("SELECT tag FROM song WHERE path=?", path));
            Assert.AreEqual(3, db.ExecuteScalar<int>("SELECT favorite FROM song WHERE path=?", path));
            Assert.AreEqual(123456789, db.ExecuteScalar<int>("SELECT adddate FROM song WHERE path=?", path));
        }
        Assert.AreEqual(pending ? 1 : 0, db.ExecuteScalar<int>("SELECT count(*) FROM install"));
        if (pending)
        {
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM install WHERE path=?", fixture.Source));
            Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT delete_parent FROM install WHERE path=?", fixture.Source));
        }
    }

    private static void AssertFile(string path, MissingDestinationFile expected)
    {
        byte[] bytes = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(expected.Bytes.ToArray(), bytes, path);
        Assert.AreEqual(expected.Md5, Convert.ToHexStringLower(MD5.HashData(bytes)), path);
        Assert.AreEqual(expected.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)), path);
    }

    private sealed class PathRow
    {
        /// <summary>製品行型を使わず読み取る保存pathです。</summary>
        public string? Path { get; set; }
    }
}
