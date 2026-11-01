using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Runtime;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.Playlist;

/// <summary>宣言済み入力から実SQLite・JSON・新Settings providerと実表示を別々に照合します。</summary>
internal static class Sc09PlaylistOracle
{
    internal static void AssertExternal(Sc09PlaylistFixture fixture, int id, ScenarioPlaylistSnapshot snapshot, bool edited)
    {
        AssertRows(snapshot.Rows, id, [(fixture.A, "External", fixture.A.Level, edited ? Sc09PlaylistFixture.ExternalMemo : null, true),
            (fixture.U, "External", fixture.U.Level, null, false)]);
        using SQLiteConnection db = Open(fixture);
        Entry[] entries = db.Query<Entry>("SELECT rowid AS Identity, playlist_id AS TableId, md5, sha256, title, artist, folder, level, memo FROM playlist_entry WHERE playlist_id = ?", id).ToArray();
        AssertEntries(entries, id, [(fixture.A, "External", fixture.A.Level, edited ? Sc09PlaylistFixture.ExternalMemo : null),
            (fixture.U, "External", fixture.U.Level, null)]);
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT is_external_sync FROM playlist WHERE playlist_id = ?", id));
    }

    /// <summary>次の操作へ進む前に、実セル表示と実SQLiteの即時保存を確認します。</summary>
    internal static void AssertLocalEdit(Sc09PlaylistFixture fixture, int id, ScenarioPlaylistSnapshot snapshot, bool memoEdited)
    {
        ScenarioPlaylistRow row = snapshot.Rows.Single(row => row.Md5 == fixture.A.Md5);
        Assert.AreEqual(Sc09PlaylistFixture.EditedLevel, double.Parse(row.Level ?? "", CultureInfo.InvariantCulture));
        using SQLiteConnection db = Open(fixture);
        Assert.AreEqual(Sc09PlaylistFixture.EditedLevel, db.ExecuteScalar<double>("SELECT level FROM playlist_entry WHERE playlist_id = ? AND md5 = ?", id, fixture.A.Md5));
        if (memoEdited)
        {
            Assert.AreEqual(Sc09PlaylistFixture.LocalMemo, row.Memo);
            Assert.AreEqual(Sc09PlaylistFixture.LocalMemo, db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id = ? AND md5 = ?", id, fixture.A.Md5));
        }
    }

    internal static string[] AssertFinalDatabase(Sc09PlaylistFixture fixture, int externalId, int localId)
    {
        using SQLiteConnection db = Open(fixture);
        Table[] tables = db.Query<Table>("SELECT playlist_id AS Id, name, symbol, is_external_sync AS ExternalSync, folder_order AS FolderOrder FROM playlist").ToArray();
        CollectionAssert.AreEquivalent(new[] { externalId, localId }, tables.Select(table => table.Id).ToArray());
        Table external = tables.Single(table => table.Id == externalId);
        Assert.AreEqual(Sc09PlaylistFixture.ExternalName, external.Name);
        Assert.IsTrue(external.ExternalSync);
        Table local = tables.Single(table => table.Id == localId);
        Assert.AreEqual(Sc09PlaylistFixture.LocalName, local.Name);
        Assert.AreEqual(Sc09PlaylistFixture.LocalSymbol, local.Symbol);
        Assert.IsFalse(local.ExternalSync);
        CollectionAssert.AreEqual(Sc09PlaylistFixture.FinalFolderOrder, JsonSerializer.Deserialize<string[]>(local.FolderOrder));
        Entry[] entries = db.Query<Entry>("SELECT rowid AS Identity, playlist_id AS TableId, md5, sha256, title, artist, folder, level, memo FROM playlist_entry").ToArray();
        AssertEntries(entries.Where(entry => entry.TableId == externalId).ToArray(), externalId,
            [(fixture.A, "External", fixture.A.Level, Sc09PlaylistFixture.ExternalMemo), (fixture.U, "External", fixture.U.Level, null)]);
        AssertEntries(entries.Where(entry => entry.TableId == localId).ToArray(), localId,
            [(fixture.A, Sc09PlaylistFixture.Alpha, Sc09PlaylistFixture.EditedLevel, Sc09PlaylistFixture.LocalMemo),
             (fixture.B, Sc09PlaylistFixture.Beta, fixture.B.Level, null)]);
        CollectionAssert.AreEquivalent(new[] { externalId, localId }, entries.Select(entry => entry.TableId).Distinct().ToArray());
        return entries.Select(entry => $"{entry.Identity}:{entry.TableId}:{entry.Md5}:{entry.Folder}").Order(StringComparer.Ordinal).ToArray();
    }

    internal static void AssertLocal(Sc09PlaylistFixture fixture, int id, ScenarioPlaylistSnapshot snapshot)
    {
        ScenarioPlaylistTable table = snapshot.Tables.Single(table => table.Id == id);
        Assert.IsFalse(table.ExternalSync);
        CollectionAssert.AreEqual(Sc09PlaylistFixture.FinalFolderOrder, table.FolderOrder.ToArray());
        CollectionAssert.AreEqual(Sc09PlaylistFixture.FinalFolderOrder, table.DisplayFolders.ToArray());
        AssertRows(snapshot.Rows, id, [(fixture.A, Sc09PlaylistFixture.Alpha, Sc09PlaylistFixture.EditedLevel, Sc09PlaylistFixture.LocalMemo, true),
            (fixture.B, Sc09PlaylistFixture.Beta, fixture.B.Level, null, true)]);
    }

    internal static void AssertJson(Sc09PlaylistFixture fixture)
    {
        using var header = JsonDocument.Parse(File.ReadAllBytes(fixture.HeaderPath));
        using var data = JsonDocument.Parse(File.ReadAllBytes(fixture.DataPath));
        Assert.AreEqual(Sc09PlaylistFixture.LocalName, header.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(Sc09PlaylistFixture.LocalSymbol, header.RootElement.GetProperty("symbol").GetString());
        CollectionAssert.AreEqual(Sc09PlaylistFixture.FinalFolderOrder, header.RootElement.GetProperty("folder_order").EnumerateArray().Select(item => item.GetString()).ToArray());
        JsonElement[] entries = data.RootElement.EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(new[] { fixture.A.Md5, fixture.B.Md5 }, entries.Select(entry => entry.GetProperty("md5").GetString()).ToArray());
        foreach ((Sc09Chart Chart, string Folder, double Level) expected in new[] { (Chart: fixture.A, Folder: Sc09PlaylistFixture.Alpha, Level: Sc09PlaylistFixture.EditedLevel),
            (Chart: fixture.B, Folder: Sc09PlaylistFixture.Beta, Level: fixture.B.Level) })
        {
            JsonElement entry = entries.Single(entry => entry.GetProperty("md5").GetString() == expected.Chart.Md5);
            Assert.AreEqual(expected.Chart.Sha256, entry.GetProperty("sha256").GetString());
            Assert.AreEqual(expected.Chart.Title, entry.GetProperty("title").GetString());
            Assert.AreEqual(expected.Chart.Artist, entry.GetProperty("artist").GetString());
            Assert.AreEqual(expected.Folder, entry.GetProperty("folder").GetString());
            Assert.AreEqual(expected.Folder, entry.GetProperty("level").GetString());
            Assert.AreEqual(JsonValueKind.Number, entry.GetProperty("org_level").ValueKind);
            Assert.AreEqual(expected.Level, entry.GetProperty("org_level").GetDouble());
            Assert.IsFalse(entry.TryGetProperty("memo", out _), "MEMOはJSON出力契約に含みません。");
        }
    }

    internal static void AssertSettings(Sc09PlaylistFixture fixture, ScenarioPlaylistSnapshot snapshot)
    {
        Assert.AreEqual(Sc09PlaylistFixture.EditedFont, snapshot.MainFontSize);
        Assert.AreEqual(Sc09PlaylistFixture.EditedFont, snapshot.SummaryFontSize);
        var settings = new Settings();
        var provider = new PortableSettingsProvider(fixture.SettingsPath);
        provider.Initialize(nameof(PortableSettingsProvider), new NameValueCollection());
        settings.Providers.Clear();
        settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties) { property.Provider = provider; }
        settings.Reload();
        Assert.AreEqual(Sc09PlaylistFixture.EditedFont, settings.CustomTableFontSize);
        Assert.AreEqual(string.Empty, settings.BeatorajaRootPath);
        var raw = XDocument.Load(fixture.SettingsPath);
        string Value(string name) => raw.Descendants("setting").Single(element => (string?)element.Attribute("name") == name).Element("value")?.Value
            ?? throw new AssertFailedException("設定の生値がありません: " + name);
        Assert.AreEqual(Sc09PlaylistFixture.EditedFont, double.Parse(Value("CustomTableFontSize"), CultureInfo.InvariantCulture));
        Assert.AreEqual(string.Empty, Value("BeatorajaRootPath"));
    }

    private static SQLiteConnection Open(Sc09PlaylistFixture fixture) => new(fixture.DatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
    private static void AssertRows(IReadOnlyList<ScenarioPlaylistRow> rows, int tableId,
        (Sc09Chart Chart, string Folder, double Level, string? Memo, bool Owned)[] expected)
    {
        CollectionAssert.AreEquivalent(expected.Select(item => item.Chart.Md5).ToArray(), rows.Select(row => row.Md5).ToArray());
        foreach ((Sc09Chart Chart, string Folder, double Level, string? Memo, bool Owned) item in expected)
        {
            ScenarioPlaylistRow row = rows.Single(row => row.Md5 == item.Chart.Md5);
            Assert.AreEqual(tableId, row.TableId);
            Assert.AreEqual(item.Chart.Sha256, row.Sha256);
            Assert.AreEqual(item.Chart.Title, row.Title);
            Assert.AreEqual(item.Chart.Artist, row.Artist);
            Assert.AreEqual(item.Folder, row.Folder);
            Assert.AreEqual(item.Level, double.Parse(row.Level ?? throw new AssertFailedException("Levelがありません。"), CultureInfo.InvariantCulture));
            Assert.AreEqual(item.Memo ?? string.Empty, row.Memo ?? string.Empty);
            Assert.AreEqual(item.Owned, row.Owned);
        }
    }
    private static void AssertEntries(Entry[] entries, int tableId, (Sc09Chart Chart, string Folder, double Level, string? Memo)[] expected)
    {
        CollectionAssert.AreEquivalent(expected.Select(item => item.Chart.Md5).ToArray(), entries.Select(entry => entry.Md5).ToArray());
        foreach ((Sc09Chart Chart, string Folder, double Level, string? Memo) item in expected)
        {
            Entry entry = entries.Single(entry => entry.Md5 == item.Chart.Md5);
            Assert.AreEqual(tableId, entry.TableId);
            Assert.AreEqual(item.Chart.Sha256, entry.Sha256);
            Assert.AreEqual(item.Chart.Title, entry.Title);
            Assert.AreEqual(item.Chart.Artist, entry.Artist);
            Assert.AreEqual(item.Folder, entry.Folder);
            Assert.AreEqual(item.Level, entry.Level);
            Assert.AreEqual(item.Memo ?? string.Empty, entry.Memo ?? string.Empty);
        }
    }
    private sealed class Table
    {
        public Table() { }
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Symbol { get; set; } = "";
        public bool ExternalSync { get; set; }
        public string FolderOrder { get; set; } = "";
    }
    private sealed class Entry
    {
        public Entry() { }
        public long Identity { get; set; }
        public int TableId { get; set; }
        public string Md5 { get; set; } = "";
        public string? Sha256 { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Folder { get; set; } = "";
        public double? Level { get; set; }
        public string? Memo { get; set; }
    }
}
