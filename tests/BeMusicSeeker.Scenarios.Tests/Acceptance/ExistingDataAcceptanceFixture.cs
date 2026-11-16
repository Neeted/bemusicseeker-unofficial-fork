using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Scenarios;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>追跡された旧DBとXML入力を専有profileへ移設し、旧受入の意味上の行を照合します。</summary>
internal sealed class ExistingDataAcceptanceFixture
{
    private readonly JsonElement manifest;
    private readonly Dictionary<string, string> settings;
    private readonly string legacyHash;
    private readonly string bmsHash;
    private readonly bool isUpdateAcceptance;

    /// <summary>固定manifestの由来とDB hashを検査し、既存行のパスだけを専有領域へ移設します。</summary>
    internal ExistingDataAcceptanceFixture(string repository, string profileRoot, string appRoot, bool lr2, bool update)
    {
        AppRoot = appRoot;
        isUpdateAcceptance = update;
        string fixtureRoot = Path.Combine(repository, "devdocs", "acceptance", "net10-existing-data");
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixtureRoot, "fixture-manifest.json")));
        manifest = document.RootElement.Clone();
        Assert.AreEqual(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(manifest.GetProperty("provenance").GetProperty("sourceCommit").GetString()));
        string sourceDatabase = Path.Combine(fixtureRoot, Text("fixtureFile"));
        Assert.AreEqual(Text("fixtureSha256").ToUpperInvariant(), Hash(sourceDatabase));
        IsLr2 = lr2;
        BmsRoot = Path.Combine(profileRoot, lr2 ? @"lr2\bms" : "bms");
        BmsPath = Path.Combine(BmsRoot, Text("songRelativePath"));
        Directory.CreateDirectory(Path.GetDirectoryName(BmsPath) ?? throw new InvalidDataException());
        File.Copy(Path.Combine(fixtureRoot, "fixture.bms"), BmsPath);
        InstallPath = Path.Combine(profileRoot, @"install\Fixture\package");
        Directory.CreateDirectory(InstallPath);
        File.Copy(Path.Combine(fixtureRoot, @"package\e1-fixture-package.marker"), Path.Combine(InstallPath, "e1-fixture-package.marker"));
        File.Copy(Path.Combine(fixtureRoot, "fixture.bms"), Path.Combine(InstallPath, "e1-fixture.bms"));
        DatabasePath = lr2 ? Path.Combine(profileRoot, @"lr2\LR2files\song.db") : Path.Combine(appRoot, @"data\song.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? throw new InvalidDataException());
        File.Copy(sourceDatabase, DatabasePath);
        using (var database = new SQLiteConnection(DatabasePath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex))
        {
            Assert.AreEqual(1, database.Execute("UPDATE song SET path = ? WHERE hash = ? AND title = ?", BmsPath, Text("songHash"), Text("songTitle")));
            Assert.AreEqual(1, database.Execute("UPDATE maintenance SET path = ? WHERE hash = ?", BmsPath, Text("songHash")));
            Assert.AreEqual(1, database.Execute("UPDATE install SET path = ? WHERE path = ?", InstallPath, "__E1_INSTALL_PATH__"));
        }
        string user = Path.Combine(profileRoot, "user");
        string local = Path.Combine(user, @"AppData\Local");
        string temp = Path.Combine(profileRoot, "temp");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(temp);
        ChildEnvironment = new Dictionary<string, string?>
        {
            ["LOCALAPPDATA"] = local,
            ["USERPROFILE"] = user,
            ["TEMP"] = temp,
            ["TMP"] = temp
        };
        settings = new Dictionary<string, string>
        {
            ["AssemblyVersion"] = manifest.GetProperty("settings").GetProperty("assemblyVersion").GetString() ?? throw new InvalidDataException(),
            ["Lang"] = manifest.GetProperty("settings").GetProperty("language").GetString() ?? throw new InvalidDataException(),
            ["AppearanceTheme"] = manifest.GetProperty("settings").GetProperty("appearanceTheme").GetString() ?? throw new InvalidDataException(),
            ["OperationModeLR2DB"] = lr2.ToString(),
            ["BMSInstallDir"] = BmsRoot,
            ["BMSRootPath"] = BmsRoot,
            ["TableListURL"] = update ? "https://example.invalid/e2-update-table-list" : "https://example.invalid/e1-existing-data-table-list",
            ["EnablePlaylistUrlCompletion"] = "False",
            ["EnableStellaFullPlaylistUrlCompletion"] = "False",
            ["SkipInitPlaylistLoad"] = "True"
        };
        if (!update) { settings["ScanBmsFilesOnStartup"] = "False"; }
        if (lr2)
        {
            string config = Path.Combine(profileRoot, @"lr2\LR2files\Config\config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(config) ?? throw new InvalidDataException());
            new XDocument(new XElement("config", new XElement("system", new XElement("autoreload", "0")),
                new XElement("jukebox", new XElement("path", @"bms\")))).Save(config);
            settings["LR2RootPath"] = Path.Combine(profileRoot, "lr2");
            settings["LR2SongDBPath"] = DatabasePath;
            settings["LR2ConfigXmlPath"] = config;
            settings["LR2CustomFolderOutputBaseDir"] = Path.Combine(profileRoot, "custom-output");
            settings["LR2CustomFolderOutputBaseDirRootType"] = Path.Combine(profileRoot, "custom-root-output");
            Directory.CreateDirectory(settings["LR2CustomFolderOutputBaseDir"]);
            Directory.CreateDirectory(settings["LR2CustomFolderOutputBaseDirRootType"]);
        }
        string legacyDirectory = update ? @"BeMusicSeeker.exe_Url_E2Standalone\1.0.0.0" :
            manifest.GetProperty("profiles").EnumerateArray().Single(p => p.GetProperty("operationModeLr2Db").GetBoolean() == lr2)
                .GetProperty("legacyConfigDirectory").GetString() ?? throw new InvalidDataException();
        LegacyPath = Path.Combine(local, "BeMusicSeeker", legacyDirectory, "user.config");
        LegacySettingsFixture.Write(LegacyPath, settings);
        legacyHash = Hash(LegacyPath);
        bmsHash = Hash(BmsPath);
        Assert.IsFalse(File.Exists(PortablePath), "通常起動前にportable設定を設置しません。");
    }

    /// <summary>展開済み実配布の専有installationです。</summary>
    internal string AppRoot { get; }
    /// <summary>LR2の正規化を期待するprofileかを示します。</summary>
    internal bool IsLr2 { get; }
    /// <summary>移設した譜面rootです。</summary>
    internal string BmsRoot { get; }
    /// <summary>不変性を確認する旧譜面です。</summary>
    internal string BmsPath { get; }
    /// <summary>旧install行の移設先です。</summary>
    internal string InstallPath { get; }
    /// <summary>実起動が使用する旧DBです。</summary>
    internal string DatabasePath { get; }
    /// <summary>移行元の旧設定です。</summary>
    internal string LegacyPath { get; }
    /// <summary>通常起動が作成するportable設定です。</summary>
    internal string PortablePath => Path.Combine(AppRoot, @"config\user.config");
    /// <summary>親を変更せず通常childへ渡す専有環境です。</summary>
    internal IReadOnlyDictionary<string, string?> ChildEnvironment { get; }
    /// <summary>旧受入の起動記述です。CWDと診断の保存先は展開済み配布のrootです。</summary>
    internal PublishedApplicationSession.Launch Launch => new(AppRoot, AppRoot, ChildEnvironment, AppRoot);

    /// <summary>各旧受入固有のoracleで保存行と元設定を照合し、既存データ受入では設定・譜面不変とLR2同期完了も確認します。</summary>
    internal void AssertSavedState()
    {
        Assert.AreEqual(legacyHash, Hash(LegacyPath));
        if (!isUpdateAcceptance)
        {
            Dictionary<string, string> actual = ReadSettings();
            foreach (string key in new[] { "Lang", "AppearanceTheme", "OperationModeLR2DB", "BMSInstallDir", "BMSRootPath" }
                .Concat(IsLr2 ? new[] { "LR2RootPath", "LR2SongDBPath", "LR2ConfigXmlPath", "LR2CustomFolderOutputBaseDir", "LR2CustomFolderOutputBaseDirRootType" } : []))
            {
                Assert.AreEqual(settings[key], actual[key], key);
            }
            Assert.AreEqual(bmsHash, Hash(BmsPath));
        }
        using var database = new SQLiteConnection(DatabasePath, SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.FullMutex);
        void One(string sql, params object[] values) => Assert.AreEqual(1, database.ExecuteScalar<int>(sql, values), sql);
        One("SELECT COUNT(*) FROM install WHERE path = ?", InstallPath);
        One("SELECT COUNT(*) FROM playlist_course WHERE course_id = ? AND playlist_id = ? AND course_json = ?", Number("courseId"), Number("playlistId"), Text("courseJson"));
        if (isUpdateAcceptance)
        {
            // 更新受入は起動scanを無効化しません。旧oracleが許容するfolderの絶対パス正規化を維持します。
            // 設定は初回起動後の保存値と更新後をcase側で比較し、旧入力への完全一致を追加しません。
            One("SELECT COUNT(*) FROM song WHERE hash = ? AND title = ?", Text("songHash"), Text("songTitle"));
            One("SELECT COUNT(*) FROM folder WHERE title = ? AND (path = ? OR path LIKE ?)", Text("folderTitle"), Text("folderRelativePath"), @"%\Fixture\");
            One("SELECT COUNT(*) FROM playlist WHERE playlist_id = ? AND name = ?", Number("playlistId"), Text("playlistName"));
            One("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND title = ?", Number("playlistId"), Text("entryMd5"), Text("entryTitle"));
            return;
        }
        One("SELECT COUNT(*) FROM song WHERE hash = ? AND title = ? AND path = ?", Text("songHash"), Text("songTitle"), BmsPath);
        One("SELECT COUNT(*) FROM maintenance WHERE hash = ? AND path = ?", Text("songHash"), BmsPath);
        One("SELECT COUNT(*) FROM playlist WHERE playlist_id = ? AND name = ? AND symbol = ?", Number("playlistId"), Text("playlistName"), Text("playlistSymbol"));
        One("SELECT COUNT(*) FROM playlist_entry WHERE playlist_id = ? AND md5 = ? AND title = ? AND folder = ?", Number("playlistId"), Text("entryMd5"), Text("entryTitle"), Text("entryFolder"));
        if (!IsLr2)
        {
            One("SELECT COUNT(*) FROM folder WHERE title = ? AND path = ?", Text("folderTitle"), Text("folderRelativePath"));
            return;
        }
        Assert.AreEqual(0, database.ExecuteScalar<int>("SELECT COUNT(*) FROM folder WHERE path = ?", Text("folderRelativePath")));
        string folder = Path.Combine(BmsRoot, "Fixture");
        One("SELECT COUNT(*) FROM folder WHERE title = ? AND path = ? AND type = ? AND date = ?", "Fixture", folder.TrimEnd('\\') + "\\", 1,
            new DateTimeOffset(Directory.GetLastWriteTimeUtc(folder)).ToUnixTimeSeconds());
        One("SELECT COUNT(*) FROM lr2_song_db_sync_status WHERE name = ?", "default");
        One("SELECT COUNT(*) FROM lr2_song_db_sync_status WHERE name = ? AND status = ? AND stage = ? AND last_error = '' AND run_id IS NOT NULL AND run_id <> '' AND processed_cursor IS NOT NULL AND total_count IS NOT NULL AND total_count > 0 AND processed_cursor = total_count AND updated_at IS NOT NULL AND completed_at IS NOT NULL", "default", "Completed", "completed");
    }

    /// <summary>更新前後で保持すべき保存値を、旧受入と同じXML値から読みます。</summary>
    internal Dictionary<string, string> ReadSettings() => XDocument.Load(PortablePath)
        .Descendants("setting").ToDictionary(e => (string?)e.Attribute("name") ?? throw new InvalidDataException(),
            e => e.Element("value")?.Value ?? throw new InvalidDataException());

    /// <summary>可変出力から期待値を作らず、元入力との同一性を検査するSHA-256です。</summary>
    internal static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private string Text(string key) => manifest.GetProperty("database").GetProperty(key).GetString() ?? throw new InvalidDataException(key);
    private int Number(string key) => manifest.GetProperty("database").GetProperty(key).GetInt32();
}
