using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Runtime;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.SettingsScenarios;

/// <summary>起動前の宣言値を、raw媒体と実catalog・表示の双方へ照合します。</summary>
internal static class SettingsScenarioOracle
{
    internal static string Setting(SettingsScenarioFixture fixture, string name)
        => XDocument.Load(fixture.SettingsPath).Descendants("setting").Single(item => (string?)item.Attribute("name") == name)
            .Element("value")?.Value ?? throw new InvalidDataException(name);

    internal static void Roots(SettingsScenarioFixture fixture, string[] expected)
        => CollectionAssert.AreEquivalent(expected, Setting(fixture, "StandaloneBmsRootPaths").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    internal static void Font(SettingsScenarioFixture fixture)
    {
        Assert.AreEqual(SettingsScenarioFixture.Font, double.Parse(Setting(fixture, "CustomTableFontSize"), CultureInfo.InvariantCulture));
        Assert.AreEqual("", Setting(fixture, "BeatorajaRootPath"));
    }

    /// <summary>宣言bytesのidentityをraw保存値・catalog・表示へ照合します。LR2は既存identityの保持、Standaloneは実走査後の保存を確認します。</summary>
    internal static void Library(string database, ScenarioLibrarySnapshot actual, params SettingsChart[] expected)
    {
        foreach (System.Collections.Immutable.ImmutableArray<ScenarioChart> rows in new[] { actual.Catalog, actual.Visible })
        {
            CollectionAssert.AreEquivalent(expected.Select(chart => chart.Path).ToArray(), rows.Select(chart => chart.Path).ToArray());
            foreach (SettingsChart chart in expected)
            {
                ScenarioChart row = rows.Single(item => item.Path == chart.Path);
                Assert.AreEqual(chart.Md5, row.Md5);
                Assert.AreEqual(chart.Sha256, row.Sha256);
                Assert.AreEqual(chart.Title, row.Title);
            }
        }
        using var db = new SQLiteConnection(database, SQLiteOpenFlags.ReadOnly);
        Assert.AreEqual(expected.Length, db.ExecuteScalar<int>("SELECT count(*) FROM song"));
        foreach (SettingsChart chart in expected)
        {
            Assert.AreEqual(chart.Md5, db.ExecuteScalar<string>("SELECT hash FROM song WHERE path=?", chart.Path));
            Assert.AreEqual(chart.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5=?", chart.Md5));
            Assert.AreEqual(chart.Title, db.ExecuteScalar<string>("SELECT title FROM song WHERE path=?", chart.Path));
            CollectionAssert.AreEqual(chart.Bytes, File.ReadAllBytes(chart.Path));
        }
    }

    internal static void Output(SettingsScenarioFixture fixture, string expectedOutput)
    {
        Assert.AreEqual(expectedOutput, Setting(fixture, "LR2CustomFolderOutputBaseDir"));
        string[] roots = XDocument.Load(fixture.Config).Root?.Element("jukebox")?.Elements("path").Select(item => item.Value.TrimEnd('\\', '/')).ToArray()
            ?? throw new InvalidDataException("jukebox");
        Assert.IsTrue(roots.Contains(expectedOutput, StringComparer.OrdinalIgnoreCase));
        using var db = new SQLiteConnection(fixture.SongDb, SQLiteOpenFlags.ReadOnly);
        string directory = Path.Combine(expectedOutput, SettingsScenarioFixture.PlaylistName);
        string[] files = Directory.GetFiles(directory, "*.lr2folder", SearchOption.AllDirectories);
        Assert.IsTrue(files.Length > 0);
        bool observed = false;
        foreach (string file in files)
        {
            string command = File.ReadAllText(file, Encoding.GetEncoding(932)).Split('\n').Select(line => line.Trim())
                .Single(line => line.StartsWith("#COMMAND ", StringComparison.Ordinal))[9..];
            string[] hashes = db.Query<HashRow>("SELECT song.hash FROM song WHERE " + command).Select(row => row.hash).ToArray();
            if (hashes.Length == 0) { continue; }
            CollectionAssert.AreEquivalent(new[] { fixture.A.Md5 }, hashes);
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM folder WHERE path=? COLLATE NOCASE", Path.GetFullPath(file)));
            Assert.AreEqual(command, db.ExecuteScalar<string>("SELECT command FROM folder WHERE path=? COLLATE NOCASE", Path.GetFullPath(file)));
            observed = true;
        }
        Assert.IsTrue(observed, "宣言したhashを選ぶ実出力がありません。");
    }

    private sealed class HashRow { public string hash { get; set; } = ""; }
}
