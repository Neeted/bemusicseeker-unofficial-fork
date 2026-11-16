using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using SQLite;
using SQLitePCL;

namespace BeMusicSeeker.Scenarios.Tests.Recovery;

/// <summary>本番の復元・dump parser・モデルを使わず、宣言値とSQLite型、元ファイル、LR2 SQLを照合します。</summary>
internal static class RecoveryScenarioOracle
{
    /// <summary>三表の全列を宣言manifestと照合します。行順・物理配置・dump表記は固定しません。</summary>
    internal static void AssertPlaylist(RecoveryScenarioFixture fixture, string? database = null, IReadOnlyDictionary<int, string>? savedFolderOrders = null)
    {
        using var db = new SQLiteConnection(database ?? fixture.SongDb, SQLiteOpenFlags.ReadOnly);
        foreach ((string table, Dictionary<string, object?>[] rows) in fixture.Manifest)
        {
            CollectionAssert.AreEquivalent(rows[0].Keys.ToArray(), db.GetTableInfo(table).Select(column => column.Name).ToArray(), table);
            Assert.AreEqual(rows.Length, db.ExecuteScalar<int>($"SELECT count(*) FROM {table}"), table);
            foreach (Dictionary<string, object?> row in rows)
            {
                string where = table == "playlist_entry" ? "playlist_id=? AND md5=?" : table == "playlist_course" ? "course_id=?" : "playlist_id=?";
                object?[] key = table == "playlist_entry" ? [row["playlist_id"], row["md5"]] : [row[table == "playlist_course" ? "course_id" : "playlist_id"]];
                Assert.AreEqual(1, db.ExecuteScalar<int>($"SELECT count(*) FROM {table} WHERE {where}", key));
                foreach ((string column, object? value) in row)
                {
                    string expectedType = value switch { null => "null", string => "text", double => "real", _ => "integer" };
                    Assert.AreEqual(expectedType, db.ExecuteScalar<string>($"SELECT typeof({column}) FROM {table} WHERE {where}", key), $"{table}.{column}");
                    if (value is not null)
                    {
                        string expected = Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidOperationException();
                        string actual = db.ExecuteScalar<string>($"SELECT CAST({column} AS TEXT) FROM {table} WHERE {where}", key);
                        if (column == "folder_order")
                        {
                            CollectionAssert.AreEqual(JsonSerializer.Deserialize<string[]>(expected), JsonSerializer.Deserialize<string[]>(actual));
                            if (savedFolderOrders is not null)
                            { Assert.AreEqual(savedFolderOrders[Convert.ToInt32(row["playlist_id"], CultureInfo.InvariantCulture)], actual, "SQL保存前の文字列表現を復元します。"); }
                        }
                        else { Assert.AreEqual(expected, actual, $"{table}.{column}"); }
                    }
                }
            }
        }
    }

    /// <summary>公開SQLを独立SQLiteへそのまま実行し、ファイル存在だけを成功にしません。</summary>
    internal static void AssertSqlBackup(RecoveryScenarioFixture fixture, string replayPath, IReadOnlyDictionary<int, string> savedFolderOrders)
    {
        Assert.IsTrue(File.Exists(fixture.SqlPath));
        using (var empty = new SQLiteConnection(replayPath))
        {
            foreach ((string table, Dictionary<string, object?>[] rows) in fixture.Manifest)
            { empty.Execute($"CREATE TABLE {table}({string.Join(',', rows[0].Keys.Select(column => '"' + column + '"'))})"); }
        }
        Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_open(replayPath, out sqlite3 connection));
        try { Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_exec(connection, File.ReadAllText(fixture.SqlPath).Replace('\v', '\n'))); }
        finally { Assert.AreEqual(raw.SQLITE_OK, raw.sqlite3_close(connection)); }
        AssertPlaylist(fixture, replayPath, savedFolderOrders);
    }

    /// <summary>復元対象外の利用者値、score、譜面・音源を独立に確認します。</summary>
    internal static void AssertSentinels(RecoveryScenarioFixture fixture)
    {
        using var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly);
        foreach (RecoveryChart chart in fixture.Charts)
        {
            Assert.AreEqual("利用者タグ", db.ExecuteScalar<string>("SELECT tag FROM song WHERE hash=?", chart.Md5));
            Assert.AreEqual(3, db.ExecuteScalar<int>("SELECT favorite FROM song WHERE hash=?", chart.Md5));
            Assert.AreEqual(123456789, db.ExecuteScalar<int>("SELECT adddate FROM song WHERE hash=?", chart.Md5));
        }
        Assert.AreEqual("unknown user table", db.ExecuteScalar<string>("SELECT value FROM user_notes WHERE id=9"));
        Assert.AreEqual("0011FF", db.ExecuteScalar<string>("SELECT hex(bytes) FROM user_notes WHERE id=9"));
        foreach ((string path, byte[] bytes) in fixture.OriginalFiles) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path), path); }
        foreach ((string path, byte[] bytes) in fixture.ScoreFiles)
        { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(fixture.ScoreRoot, path)), path); }
    }

    /// <summary>通常font commitの保存Bをraw providerファイルで確認します。</summary>
    internal static void AssertSettingsB(RecoveryScenarioFixture fixture)
    {
        var config = XDocument.Load(fixture.SettingsPath);
        string value = config.Descendants("setting").Single(item => (string?)item.Attribute("name") == "CustomTableFontSize")
            .Element("value")?.Value ?? throw new InvalidOperationException("font設定がありません。");
        Assert.AreEqual(RecoveryScenarioFixture.EditedFont, double.Parse(value, CultureInfo.InvariantCulture));
    }

    /// <summary>生成された全曲folderのSQLを実DBで評価し、復元Aだけの集合と対応folder行を確認します。</summary>
    internal static void AssertLr2Output(RecoveryScenarioFixture fixture)
    {
        string directory = Path.Combine(fixture.Output, "Recovery Main");
        string[] files = Directory.GetFiles(directory, "*.lr2folder", SearchOption.AllDirectories);
        Assert.IsTrue(files.Length > 0);
        using var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly);
        bool observedAll = false;
        foreach (string file in files)
        {
            string text = File.ReadAllText(file, Encoding.GetEncoding(932));
            string command = text.Split('\n').Select(line => line.Trim()).Single(line => line.StartsWith("#COMMAND ", StringComparison.Ordinal))[9..];
            string[] hashes = db.Query<HashRow>("SELECT song.hash FROM song WHERE " + command).Select(row => row.hash).Order(StringComparer.Ordinal).ToArray();
            if (hashes.Length != 2) { continue; }
            CollectionAssert.AreEquivalent(new[] { fixture.Charts[0].Md5, fixture.Charts[1].Md5 }, hashes);
            Assert.IsFalse(hashes.Contains(fixture.Charts[2].Md5));
            // 通常型の管理出力なので、組込みLR2files/CustomFolderの相対pathとは区別して物理絶対pathを照合します。
            string databasePath = Path.GetFullPath(file).Replace('/', '\\');
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM folder WHERE path=? COLLATE NOCASE", databasePath));
            Assert.AreEqual(2, db.ExecuteScalar<int>("SELECT type FROM folder WHERE path=? COLLATE NOCASE", databasePath));
            Assert.AreEqual(command, db.ExecuteScalar<string>("SELECT command FROM folder WHERE path=? COLLATE NOCASE", databasePath));
            observedAll = true;
        }
        Assert.IsTrue(observedAll, "復元Aのhash集合を選ぶ実LR2出力がありません。");
    }

    private sealed class HashRow { public string hash { get; set; } = ""; }
}
