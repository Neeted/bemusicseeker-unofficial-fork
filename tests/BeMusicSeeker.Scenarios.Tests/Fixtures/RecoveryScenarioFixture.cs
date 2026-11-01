using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.Fixtures;

/// <summary>復旧代表の専有LR2配置と、起動前に宣言した値・型・元bytesだけを所有します。</summary>
internal sealed class RecoveryScenarioFixture : IAsyncDisposable
{
    private readonly ScenarioStartupFixture endpoint;
    internal const int MainId = 71;
    internal const int CourseTableId = 72;
    internal const string Memo = "復元前 A の MEMO";
    internal const double EditedFont = 17;
    internal string Profile { get; }
    internal string SongDb { get; }
    internal string Config { get; }
    internal string SettingsPath { get; }
    internal string SqlPath { get; }
    internal string Output { get; }
    internal string Backups { get; }
    internal string ScoreRoot { get; }
    internal string Lr2Root { get; }
    internal string UpdateUrl => endpoint.UpdateManifestUrl;
    internal RecoveryChart[] Charts { get; }
    internal Dictionary<string, byte[]> OriginalFiles { get; } = [];
    internal Dictionary<string, byte[]> ScoreFiles { get; } = [];
    internal Dictionary<string, Dictionary<string, object?>[]> Manifest { get; } = [];

    /// <summary>schema旧版またはバックアップ対象を起動前にだけ準備します。</summary>
    internal RecoveryScenarioFixture(TestWorkspace workspace, bool legacy = false, bool startupBackup = false)
    {
        Profile = workspace.GetPath("profile");
        SettingsPath = workspace.GetPath("profile/config/user.config");
        Lr2Root = workspace.GetPath("lr2");
        (SongDb, Config) = SettingsMediaTestFixture.CreateValidLr2Layout(Lr2Root);
        string library = workspace.GetPath("library");
        Output = workspace.GetPath("lr2/output");
        string rootOutput = workspace.GetPath("lr2/root-output");
        Backups = workspace.GetPath("backups");
        ScoreRoot = Path.Combine(Lr2Root, "LR2files", "Database", "Score");
        SqlPath = workspace.GetPath("exports/playlist.sql");
        foreach (string path in new[] { library, Output, rootOutput, Backups, ScoreRoot, Path.GetDirectoryName(SqlPath) ?? throw new InvalidOperationException() })
        { Directory.CreateDirectory(path); }
        Charts = Enumerable.Range(0, 3).Select(index => WriteChart(library, index)).ToArray();
        File.WriteAllText(Config, "<config><system /><jukebox /><player><id>selected</id></player></config>");
        var config = new LR2Config(Config);
        config.AddBMSSearchDirectories([library]);
        config.Save(Config);

        using (var db = new SQLiteConnection(SongDb))
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
        }
        var scope = new ApplicationDatabaseScope();
        new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), SongDb).EnsureAppOwnedSchema();
        PlaylistPersistenceRepository.EnsureSchema(scope, SongDb);
        using (var db = new SQLiteConnection(SongDb))
        {
            foreach (RecoveryChart chart in Charts)
            {
                db.Execute("INSERT INTO song(hash,path,title,artist,tag,favorite,adddate) VALUES(?,?,?,?,?,?,?)",
                    chart.Md5, chart.Path, chart.Title, "Recovery Artist", "利用者タグ", 3, 123456789);
            }
            db.Execute("CREATE TABLE user_notes(id INTEGER PRIMARY KEY, value TEXT, bytes BLOB)");
            db.Execute("INSERT INTO user_notes VALUES(9,'unknown user table',X'0011FF')");
            DeclarePlaylistRows();
            foreach ((string table, Dictionary<string, object?>[] rows) in Manifest)
            {
                foreach (Dictionary<string, object?> row in rows)
                {
                    db.Execute($"INSERT INTO {table}({string.Join(',', row.Keys)}) VALUES({string.Join(',', row.Keys.Select(_ => "?"))})", row.Values.ToArray());
                }
            }
            if (legacy)
            {
                db.Execute("DROP INDEX playlist_entry_idx_uniq");
                db.Execute("DROP INDEX playlist_entry_idx_sha256");
                db.Execute("DROP INDEX playlist_entry_idx_candidate");
                db.Execute("ALTER TABLE playlist_entry DROP COLUMN sha256");
                db.Execute("UPDATE app_schema_version SET version=0 WHERE name='app_schema'");
            }
        }
        File.WriteAllText(workspace.GetPath("expected-manifest.json"), JsonSerializer.Serialize(Manifest), new UTF8Encoding(false));
        CreateScore(Path.Combine(ScoreRoot, "selected.db"), 101, sparse: false);
        CreateScore(Path.Combine(ScoreRoot, "other.db"), 202, sparse: false);
        CreateScore(Path.Combine(ScoreRoot, "nested", "deep", "history.db"), 303, sparse: true);
        string scoreSentinel = Path.Combine(ScoreRoot, "nested", "keep.txt");
        File.WriteAllText(scoreSentinel, "Score subtree 非DB sentinel");
        foreach (string path in Directory.GetFiles(ScoreRoot, "*", SearchOption.AllDirectories))
        { ScoreFiles.Add(Path.GetRelativePath(ScoreRoot, path), File.ReadAllBytes(path)); }
        if (startupBackup)
        {
            foreach (int days in new[] { 30, 20, 10 })
            {
                string old = Path.Combine(Backups, DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(old, "old.txt"), "generation-" + days);
            }
        }
        endpoint = ScenarioStartupFixture.Create(SettingsPath, library, settings =>
        {
            settings.OperationModeLR2DB = true;
            settings.LR2RootPath = Lr2Root;
            settings.LR2SongDBPath = SongDb;
            settings.LR2ConfigXmlPath = Config;
            settings.LR2CustomFolderOutputBaseDir = Output;
            settings.LR2CustomFolderOutputBaseDirRootType = rootOutput;
            settings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            settings.ScanBmsFilesOnStartup = true;
            settings.SkipInitPlaylistLoad = true;
            settings.EnablePlaylistUrlCompletion = false;
            settings.UpdateLr2IrRankingCacheOnStartup = false;
            settings.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            settings.UseBeatorajaScoreDb = false;
            settings.EnableBeatorajaBmtOutput = false;
            settings.UseExternalPanelImage = false;
            settings.UsePlayeruBMplay = false;
            settings.UsePlayerLR2body = false;
            settings.UsePlayerBMIIDXView = false;
            settings.BeatorajaRootPath = string.Empty;
            settings.CustomTableFontSize = 11;
            settings.IsLR2BackupEnabled = startupBackup;
            settings.LR2BackupTarget = Backup.Target.Config | Backup.Target.SongDB | Backup.Target.ScoreDB;
            settings.LR2BackupPath = Backups;
            settings.LR2BackupSpan = 1;
            settings.LR2BackupNum = 2;
        });
    }

    private RecoveryChart WriteChart(string library, int index)
    {
        string directory = Path.Combine(library, "chart" + index);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "chart.bms");
        string title = "Recovery" + (char)('A' + index);
        byte[] bytes = Encoding.ASCII.GetBytes($"#PLAYER 1\r\n#TITLE {title}\r\n#ARTIST Recovery Artist\r\n#BPM 120\r\n#PLAYLEVEL 7\r\n#RANK 2\r\n#WAV01 sound.wav\r\n#00111:01\r\n");
        File.WriteAllBytes(path, bytes);
        OriginalFiles.Add(path, bytes);
        string sound = Path.Combine(directory, "sound.wav");
        byte[] soundBytes = [82, 73, 70, 70, (byte)index, 0, 0, 0, 87, 65, 86, 69];
        File.WriteAllBytes(sound, soundBytes);
        OriginalFiles.Add(sound, soundBytes);
        return new(path, Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(bytes)), title);
    }

    private void DeclarePlaylistRows()
    {
        Dictionary<string, object?> Header(int id, string name) => new()
        {
            ["playlist_id"] = id,
            ["name"] = name,
            ["symbol"] = "R",
            ["folder_order"] = "[\"Alpha\",\"Beta\"]",
            ["folder_sort_key"] = 0,
            ["folder_sort_ascending"] = 1,
            ["entry_type"] = 0,
            ["page_url"] = "",
            ["header_url"] = "",
            ["data_url"] = "",
            ["tag"] = "fixture",
            ["header_sha256"] = null,
            ["data_sha256"] = null,
            ["compat_prefix"] = "",
            ["last_update"] = 638000000000000000L,
            ["org_name"] = null,
            ["org_symbol"] = "",
            ["ignore_folder_output"] = 4095,
            ["is_external_sync"] = 0,
            ["output_dir"] = null,
            ["custom_folder_output_base_name"] = null,
            ["is_root_folder"] = 0,
            ["bmt_sort"] = id == MainId ? 1 : 2,
            ["is_bmt_output"] = 0
        };
        Dictionary<string, object?> Entry(int table, int chart, string folder, string? memo) => new()
        {
            ["playlist_id"] = table,
            ["md5"] = Charts[chart].Md5,
            ["sha256"] = Charts[chart].Sha256,
            ["level"] = 7.5,
            ["title"] = Charts[chart].Title,
            ["artist"] = "Recovery Artist",
            ["folder"] = folder,
            ["lr2_bmsid"] = null,
            ["url"] = "",
            ["url_diff"] = null,
            ["name_diff"] = "",
            ["org_md5"] = null,
            ["adddate"] = 638000000000000000L,
            ["comment"] = "",
            ["memo"] = memo,
            ["is_removed"] = 0
        };
        Manifest.Add("playlist", [Header(MainId, "Recovery Main"), Header(CourseTableId, "Recovery Course")]);
        Manifest.Add("playlist_entry", [Entry(MainId, 0, "Alpha", Memo), Entry(MainId, 1, "Beta", null), Entry(CourseTableId, 1, "Alpha", "")]);
        Manifest.Add("playlist_course", [new() { ["course_id"] = 81, ["playlist_id"] = CourseTableId, ["course_order"] = 0,
            ["course_json"] = JsonSerializer.Serialize(new { name = "Recovery course", md5 = new[] { Charts[0].Md5, Charts[1].Md5 } }) }]);
    }

    private void CreateScore(string path, int sentinel, bool sparse)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        using var db = new SQLiteConnection(path);
        db.CreateTable<LR2ScoreDB.score>();
        db.CreateTable<LR2ScoreDB.player>();
        db.Execute("INSERT INTO score(hash,clear,perfect) VALUES(?,4,?)", Charts[0].Md5, sentinel);
        db.Execute("CREATE TABLE sentinel(value INTEGER PRIMARY KEY, note TEXT)");
        db.Execute("INSERT INTO sentinel VALUES(?, 'before VACUUM')", sentinel);
        if (sparse)
        {
            db.Execute("CREATE TABLE removed_pages(payload BLOB)");
            db.Execute("INSERT INTO removed_pages VALUES(zeroblob(262144))");
            db.Execute("DROP TABLE removed_pages");
            Assert.IsTrue(db.ExecuteScalar<int>("PRAGMA freelist_count") > 0, "VACUUM前後を識別できる入力を作ります。");
        }
    }

    /// <summary>全製品process終了後に専有HTTPを回収します。</summary>
    public ValueTask DisposeAsync() => endpoint.DisposeAsync();
}

/// <summary>本番parserから独立した入力bytesの識別値です。</summary>
internal sealed record RecoveryChart(string Path, string Md5, string Sha256, string Title);
