using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SQLite;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>DT02の元入力とraw DB/FSを照合します。製品のparser・DB reader・path正規化を期待値生成に使いません。</summary>
internal static class DesktopMutationOracle
{
    /// <summary>通常空folderの独立した保存キーを確認し、その一行だけを除いた全列snapshotを削除承認の期待値にします。</summary>
    internal static string[] ReadEntriesExceptEmptyFolder(string databasePath, string folder)
    {
        using SQLiteConnection db = Open(databasePath);
        const string key = "playlist_id=? AND folder=? AND md5='00000000000000000000000000000000'";
        object[] arguments = [DesktopPlaylistFixture.PlaylistId, folder];
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE " + key, arguments));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id=? AND folder=?", arguments));
        string columns = string.Join("||','||", db.GetTableInfo("playlist_entry")
            .Select(column => "quote(" + Identifier(column.Name) + ")"));
        return db.Query<RawValue>("SELECT " + columns + " AS Value FROM playlist_entry WHERE NOT (" + key + ")", arguments)
            .Select(row => row.Value).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>専有DBの指定表を、NULL・blobを区別するSQL literalの行集合として読みます。</summary>
    internal static string[] ReadTable(string databasePath, string table, params string[] excludedColumns)
    {
        using SQLiteConnection db = Open(databasePath);
        string columns = string.Join("||','||", db.GetTableInfo(table)
            .Where(column => !excludedColumns.Contains(column.Name, StringComparer.Ordinal))
            .Select(column => "quote(" + Identifier(column.Name) + ")"));
        return db.Query<RawValue>("SELECT " + columns + " AS Value FROM " + Identifier(table))
            .Select(row => row.Value).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>schema確認の取消前後に、全user tableとschema定義の論理内容を比較します。</summary>
    internal static string[] ReadDatabase(string path)
    {
        using SQLiteConnection db = Open(path);
        string[] names = db.Query<RawValue>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name")
            .Select(row => row.Value).ToArray();
        var result = new List<string>();
        result.AddRange(db.Query<RawValue>("SELECT quote(type)||','||quote(name)||','||quote(tbl_name)||','||quote(sql) AS Value FROM sqlite_master ORDER BY type,name")
            .Select(row => row.Value));
        foreach (string name in names)
        {
            result.Add("table=" + name);
            result.AddRange(ReadTable(path, name));
        }
        return result.ToArray();
    }

    /// <summary>保存された通常folder名をJSONとして読みます。</summary>
    internal static string[] ReadFolders(string path)
    {
        using SQLiteConnection db = Open(path);
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT is_external_sync FROM playlist WHERE playlist_id=?", DesktopPlaylistFixture.PlaylistId));
        return JsonSerializer.Deserialize<string[]>(db.ExecuteScalar<string>("SELECT folder_order FROM playlist WHERE playlist_id=?",
            DesktopPlaylistFixture.PlaylistId)) ?? throw new InvalidDataException("folder_orderがありません。");
    }

    /// <summary>fixture元bytesを指定pathに照合し、非対象への書込みを検出します。</summary>
    internal static void AssertChartFiles(DesktopChartInput chart, string chartPath)
    {
        CollectionAssert.AreEqual(chart.Bytes.ToArray(), File.ReadAllBytes(chartPath));
        string resourcePath = Path.Combine(Path.GetDirectoryName(chartPath) ?? throw new InvalidOperationException(), Path.GetFileName(chart.Resource.Path));
        CollectionAssert.AreEqual(chart.Resource.Bytes.ToArray(), File.ReadAllBytes(resourcePath));
    }

    /// <summary>hash・pathと、LR2保存形式のfolder/parent CRCを元の期待pathから確認します。</summary>
    internal static void AssertSong(string databasePath, DesktopChartInput chart, string expectedPath)
    {
        using SQLiteConnection db = Open(databasePath);
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE path=? AND hash=? AND title=?",
            expectedPath, chart.Md5, chart.Title));
        Assert.AreEqual(chart.Sha256, db.ExecuteScalar<string>("SELECT sha256 FROM chart_digest_map WHERE md5=?", chart.Md5));
        string directory = Path.GetDirectoryName(expectedPath) ?? throw new InvalidOperationException();
        Assert.AreEqual(DirectoryCrc(directory), db.ExecuteScalar<string>("SELECT folder FROM song WHERE path=?", expectedPath));
        Assert.AreEqual(DirectoryCrc(Path.GetDirectoryName(directory) ?? throw new InvalidOperationException()),
            db.ExecuteScalar<string>("SELECT parent FROM song WHERE path=?", expectedPath));
    }

    /// <summary>導入された一意hashのpathを読取り、登録root配下の新規配置であることを独立に限定します。</summary>
    internal static string ReadInstalledPath(DesktopPlaylistFixture fixture, DesktopPackageInput package)
    {
        using SQLiteConnection db = Open(fixture.SongDatabasePath);
        Assert.AreEqual(3, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT COUNT(*) FROM song WHERE hash=?", package.Chart.Md5));
        string path = db.ExecuteScalar<string>("SELECT path FROM song WHERE hash=?", package.Chart.Md5);
        string relative = Path.GetRelativePath(fixture.LibraryRoot, path);
        Assert.IsFalse(Path.IsPathRooted(relative));
        Assert.IsFalse(relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.AreNotEqual("..", relative);
        Assert.AreEqual("chart.bms", Path.GetFileName(path));
        Assert.AreNotEqual(fixture.LibraryRoot, Path.GetDirectoryName(path));
        Assert.AreEqual(0, db.ExecuteScalar<int>("SELECT COUNT(*) FROM install"));
        AssertSong(fixture.SongDatabasePath, package.Chart, path);
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException();
        string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        string[] expectedFiles = package.ExpectedManifest.Select(entry => entry.RelativePath["Imported/".Length..]).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expectedFiles, files);
        foreach (DesktopArchiveEntryInput entry in package.ExpectedManifest)
        {
            Assert.IsTrue(entry.RelativePath.StartsWith("Imported/", StringComparison.Ordinal));
            CollectionAssert.AreEqual(entry.Bytes.ToArray(), File.ReadAllBytes(Path.Combine(directory, entry.RelativePath["Imported/".Length..])));
        }
        return path;
    }

    /// <summary>登録root内のfileと子directory集合を宣言済み三作品だけに限定します。</summary>
    internal static void AssertLibraryFiles(DesktopPlaylistFixture fixture, string installedPath, string renameChartPath)
    {
        string sentinelPath = fixture.Charts[1].Path;
        string[] charts = [renameChartPath, sentinelPath, installedPath];
        string[] expectedFiles = charts.SelectMany(path => new[]
        {
            path, Path.Combine(Path.GetDirectoryName(path) ?? throw new InvalidOperationException(), "tone.wav")
        }).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(expectedFiles,
            Directory.GetFiles(fixture.LibraryRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        string[] expectedDirectories = charts.Select(path => Path.GetDirectoryName(path) ?? throw new InvalidOperationException())
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        CollectionAssert.AreEqual(expectedDirectories,
            Directory.GetDirectories(fixture.LibraryRoot, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>旧schemaの版を製品modelから独立して読みます。</summary>
    internal static int SchemaVersion(string path)
    {
        using SQLiteConnection db = Open(path);
        return db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name='app_schema'");
    }

    /// <summary>LR2の旧schema入力と修復後を、宣言済みmembership・MEMO型・利用者列・未知tableで照合します。</summary>
    internal static void AssertLegacyStorage(DesktopPlaylistFixture fixture, bool repaired)
    {
        using SQLiteConnection db = Open(fixture.SongDatabasePath);
        Assert.AreEqual(repaired ? 1 : 0, db.ExecuteScalar<int>("SELECT version FROM app_schema_version WHERE name='app_schema'"));
        Assert.AreEqual(repaired, db.GetTableInfo("playlist_entry").Any(column => column.Name == "sha256"));
        Assert.AreEqual(fixture.Charts.Count, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry"));
        Assert.AreEqual(fixture.Charts.Count, db.ExecuteScalar<int>("SELECT count(*) FROM song"));
        for (int index = 0; index < fixture.Charts.Count; index++)
        {
            DesktopChartInput chart = fixture.Charts[index];
            object[] key = [DesktopPlaylistFixture.PlaylistId, chart.Md5];
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(DesktopPlaylistFixture.FolderName, db.ExecuteScalar<string>("SELECT folder FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(index == 0 ? "null" : "text", db.ExecuteScalar<string>("SELECT typeof(memo) FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(index == 0 ? null : index == 1 ? string.Empty : DesktopPlaylistFixture.InitialMemo,
                db.ExecuteScalar<string>("SELECT memo FROM playlist_entry WHERE playlist_id=? AND md5=?", key));
            Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM song WHERE hash=? AND path=? AND tag='Desktop user tag' AND favorite=3 AND adddate=123456789", chart.Md5, chart.Path));
        }
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM desktop_user_notes"));
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT count(*) FROM desktop_user_notes WHERE id=9 AND value='Desktop user data' AND typeof(value)='text' AND bytes=X'0011FF' AND typeof(bytes)='blob'"));
    }

    private static SQLiteConnection Open(string path) => new(path, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
    private static string Identifier(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";

    private static string DirectoryCrc(string path)
    {
        // LR2外部保存契約: CP932、元の大小文字、末尾\一つとNUL、符号付きbyteのCRC32。
        Encoding encoding = CodePagesEncodingProvider.Instance.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
            ?? throw new InvalidOperationException("CP932が利用できません。");
        uint crc = uint.MaxValue;
        foreach (byte value in encoding.GetBytes(path.TrimEnd('\\') + "\\\0"))
        {
            crc ^= unchecked((uint)(sbyte)value);
            for (int bit = 0; bit < 8; bit++) { crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1; }
        }
        return (~crc).ToString("x", CultureInfo.InvariantCulture);
    }

    private sealed class RawValue
    {
        public RawValue() { }
        public string Value { get; set; } = string.Empty;
    }
}
