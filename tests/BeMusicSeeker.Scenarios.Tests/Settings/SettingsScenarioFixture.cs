using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Scenarios.Tests.Fixtures;
using BeMusicSeeker.TestSupport.Files;
using SQLite;

namespace BeMusicSeeker.Scenarios.Tests.SettingsScenarios;

/// <summary>設定代表の独立profileと、起動前に確定した小譜面・LR2媒体を所有します。</summary>
internal sealed class SettingsScenarioFixture : IAsyncDisposable
{
    private readonly ScenarioStartupFixture endpoint;
    internal const double Font = 17;
    internal const string PlaylistName = "Settings output";
    internal string Profile { get; }
    internal string SettingsPath { get; }
    internal string Root0 { get; }
    internal string Root1 { get; }
    internal string Candidate { get; }
    internal string SongDb { get; }
    internal string NextSongDb { get; }
    internal string Config { get; }
    internal string NextConfig { get; }
    internal string Output0 { get; }
    internal string Output1 { get; }
    internal SettingsChart A { get; }
    internal SettingsChart B { get; }
    internal string UpdateUrl => endpoint.UpdateManifestUrl;

    /// <summary>すべての期待値は製品起動前の宣言したbytesと配置から決めます。</summary>
    internal SettingsScenarioFixture(TestWorkspace workspace, bool lr2)
    {
        Profile = workspace.GetPath("profile");
        SettingsPath = workspace.GetPath("profile/config/user.config");
        Root0 = workspace.GetPath("library0");
        Root1 = workspace.GetPath("library1");
        Candidate = workspace.GetPath("uncommitted-beatoraja");
        Directory.CreateDirectory(Candidate);
        A = WriteChart(Root0, "SettingsA");
        B = WriteChart(Root1, "SettingsB");
        string lr2Root = workspace.GetPath("lr2");
        SongDb = lr2 ? workspace.GetPath("lr2/LR2files/Database/song.db") : workspace.GetPath("profile/data/song.db");
        // 詳細パスの変更先もLR2が受理する固定名と標準配置を保ち、初期媒体とは別ディレクトリへ置きます。
        NextSongDb = workspace.GetPath("lr2-next/LR2files/Database/song.db");
        Config = workspace.GetPath("lr2/LR2files/Config/config.xml");
        NextConfig = workspace.GetPath("lr2-next/LR2files/Config/config.xml");
        Output0 = workspace.GetPath("lr2/output0");
        Output1 = workspace.GetPath("lr2/output1");
        string rootOutput = workspace.GetPath("lr2/root-output");
        if (lr2)
        {
            foreach (string? directory in new[] { Path.GetDirectoryName(Config), Path.GetDirectoryName(SongDb),
                Path.GetDirectoryName(NextConfig), Path.GetDirectoryName(NextSongDb), Output0, Output1, rootOutput })
            { Directory.CreateDirectory(directory ?? throw new InvalidOperationException()); }
            File.WriteAllBytes(Path.Combine(lr2Root, "LR2body.exe"), []);
            WriteConfig(Config, [Root0]);
            WriteConfig(NextConfig, [Root0, Root1]);
            CreateSongDb(SongDb, [A]);
            CreateSongDb(NextSongDb, [A, B]);
        }
        endpoint = ScenarioStartupFixture.Create(SettingsPath, Root0, values =>
        {
            values.OperationModeLR2DB = lr2;
            values.LR2RootPath = lr2 ? lr2Root : "";
            values.LR2SongDBPath = lr2 ? SongDb : "";
            values.LR2ConfigXmlPath = lr2 ? Config : "";
            values.LR2CustomFolderOutputBaseDir = Output0;
            values.LR2CustomFolderOutputBaseDirRootType = rootOutput;
            values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            values.PlaylistDefaultIgnoreFolderOutput = 4095;
            values.UpdateLr2IrRankingCacheOnStartup = false;
            values.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            values.UseBeatorajaScoreDb = false;
            values.EnableBeatorajaBmtOutput = false;
            values.UseExternalPanelImage = false;
            values.UsePlayeruBMplay = false;
            values.UsePlayerLR2body = false;
            values.UsePlayerBMIIDXView = false;
            values.BeatorajaRootPath = "";
            values.CustomTableFontSize = 11;
            values.IsLR2BackupEnabled = false;
        });
    }

    private static void WriteConfig(string path, string[] roots)
        => new XDocument(new XElement("config", new XElement("system"),
            new XElement("jukebox", roots.Select(root => new XElement("path", root))))).Save(path);

    // LR2代表は保存済みidentityの保持を確認します。初回SHA生成や背景補完の公開時機はこのケースの保証に含めません。
    private static void CreateSongDb(string path, SettingsChart[] charts)
    {
        var scope = new ApplicationDatabaseScope();
        using (var db = new SQLiteConnection(path))
        {
            db.CreateTable<LR2SongDB.song>();
            db.CreateTable<LR2SongDB.folder>();
        }
        new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), path).EnsureAppOwnedSchema();
        PlaylistPersistenceRepository.EnsureSchema(scope, path);
        using var songs = new SQLiteConnection(path);
        foreach (SettingsChart chart in charts)
        {
            songs.Execute("INSERT INTO song(hash,path,title,artist) VALUES(?,?,?,?)", chart.Md5, chart.Path, chart.Title, "Settings Artist");
            songs.Execute("INSERT INTO chart_digest_map(md5,sha256) VALUES(?,?)", chart.Md5, chart.Sha256);
        }
    }

    private static SettingsChart WriteChart(string root, string title)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "chart.bms");
        byte[] bytes = Encoding.ASCII.GetBytes($"#PLAYER 1\r\n#TITLE {title}\r\n#ARTIST Settings Artist\r\n#BPM 120\r\n#PLAYLEVEL 7\r\n#RANK 2\r\n#00111:01\r\n");
        File.WriteAllBytes(path, bytes);
        return new(path, title, Convert.ToHexStringLower(MD5.HashData(bytes)), Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    /// <summary>所有processの全終端後にHTTP入力を回収します。</summary>
    public ValueTask DisposeAsync() => endpoint.DisposeAsync();
}

/// <summary>本番parserに依存しない譜面入力の識別値です。</summary>
internal sealed record SettingsChart(string Path, string Title, string Md5, string Sha256, byte[] Bytes);
