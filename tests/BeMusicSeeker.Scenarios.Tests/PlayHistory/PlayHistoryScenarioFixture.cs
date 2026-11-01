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

namespace BeMusicSeeker.Scenarios.Tests.PlayHistory;

/// <summary>外部ゲームの入力媒体と事前期待を所有します。LR2履歴表へは直接書き込みません。</summary>
internal sealed class PlayHistoryScenarioFixture : IAsyncDisposable
{
    private readonly ScenarioStartupFixture endpoint;
    internal const string UnownedMd5 = "11111111111111111111111111111111";
    internal const string UnownedSha = "1111111111111111111111111111111111111111111111111111111111111111";
    internal const int PlaylistId = 71;
    internal const string PlaylistName = "History playlist";
    internal const string BeatDay = "2024-02-29";
    internal string Profile { get; }
    internal string SettingsPath { get; }
    internal string SongDb { get; }
    internal string ScoreDb { get; }
    internal string BeatRoot { get; }
    internal string BeatScore { get; }
    internal string BeatLog { get; }
    internal string[] OwnedPaths { get; }
    internal string Md5 { get; }
    internal string Sha256 { get; }
    internal byte[] Bytes { get; }
    internal long BeatFrom { get; }
    internal long BeatTo { get; }
    internal string UpdateUrl => endpoint.UpdateManifestUrl;

    /// <summary>同内容二配置、未所持identity、既存scoreと選択player別のscorelogを起動前に宣言します。</summary>
    internal PlayHistoryScenarioFixture(TestWorkspace workspace)
    {
        Profile = workspace.GetPath("profile");
        SettingsPath = workspace.GetPath("profile/config/user.config");
        string library = workspace.GetPath("library");
        OwnedPaths = [workspace.GetPath("library/a/chart.bms"), workspace.GetPath("library/duplicate/chart.bms")];
        Bytes = Encoding.ASCII.GetBytes("#PLAYER 1\r\n#TITLE HistoryMatch\r\n#ARTIST History Artist\r\n#BPM 120\r\n#PLAYLEVEL 7\r\n#RANK 2\r\n#00111:01\r\n");
        Md5 = Convert.ToHexStringLower(MD5.HashData(Bytes));
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Bytes));
        foreach (string path in OwnedPaths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
            File.WriteAllBytes(path, Bytes);
        }
        string lr2 = workspace.GetPath("lr2");
        SongDb = workspace.GetPath("lr2/LR2files/Database/song.db");
        ScoreDb = workspace.GetPath("lr2/LR2files/Database/Score/selected.db");
        string config = workspace.GetPath("lr2/LR2files/Config/config.xml");
        string output = workspace.GetPath("lr2/output");
        string rootOutput = workspace.GetPath("lr2/root-output");
        foreach (string? directory in new[] { Path.GetDirectoryName(ScoreDb), Path.GetDirectoryName(config), output, rootOutput })
        { Directory.CreateDirectory(directory ?? throw new InvalidOperationException()); }
        File.WriteAllBytes(Path.Combine(lr2, "LR2body.exe"), []);
        new XDocument(new XElement("config", new XElement("system"), new XElement("jukebox", new XElement("path", library)),
            new XElement("player", new XElement("id", "selected")))).Save(config);
        using (var db = new SQLiteConnection(SongDb)) { db.CreateTable<LR2SongDB.song>(); db.CreateTable<LR2SongDB.folder>(); }
        var scope = new ApplicationDatabaseScope();
        new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), SongDb).EnsureAppOwnedSchema();
        PlaylistPersistenceRepository.EnsureSchema(scope, SongDb);
        using (var db = new SQLiteConnection(SongDb))
        {
            foreach (string path in OwnedPaths)
            { db.Execute("INSERT INTO song(hash,path,title,artist) VALUES(?,?,?,?)", Md5, path, "HistoryMatch", "History Artist"); }
            db.Execute("INSERT INTO chart_digest_map(md5,sha256) VALUES(?,?)", Md5, Sha256);
            db.Execute("INSERT INTO playlist(playlist_id,name,symbol,folder_order,folder_sort_key,folder_sort_ascending,entry_type,page_url,header_url,data_url,tag,compat_prefix,last_update,org_symbol,ignore_folder_output,is_external_sync,is_root_folder,bmt_sort,is_bmt_output) VALUES(?,?,?,'[]',0,1,0,'','','','','',638000000000000000,'',4095,0,0,1,0)", PlaylistId, PlaylistName, "H");
            db.Execute("INSERT INTO playlist_entry(playlist_id,md5,sha256,level,title,artist,folder,url,name_diff,adddate,comment,is_removed) VALUES(?,?,?,7,'HistoryMatch','History Artist','7','','',638000000000000000,'',0)", PlaylistId, Md5, Sha256);
        }
        using (var db = new SQLiteConnection(ScoreDb))
        {
            db.CreateTable<LR2ScoreDB.score>(); db.CreateTable<LR2ScoreDB.player>();
            db.Execute("INSERT INTO score(hash,playcount,clear,perfect,great,minbp,maxcombo,totalnotes) VALUES(?,2,1,20,10,20,20,100)", Md5);
            db.Execute("INSERT INTO player(id,playcount,perfect,great,good,bad,poor,playtime,maxcombo) VALUES('selected',2,20,10,0,0,0,60,20)");
        }
        BeatRoot = workspace.GetPath("beatoraja");
        Directory.CreateDirectory(BeatRoot);
        File.WriteAllText(Path.Combine(BeatRoot, "beatoraja.jar"), "");
        File.WriteAllText(Path.Combine(BeatRoot, "config_sys.json"), "{\"playername\":\"other\"}");
        BeatScore = Path.Combine(BeatRoot, "player", "selected", "score.db");
        BeatLog = Path.Combine(BeatRoot, "player", "selected", "scorelog.db");
        BeatFrom = new DateTimeOffset(new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
        BeatTo = new DateTimeOffset(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Local)).ToUnixTimeSeconds();
        CreateBeatPlayer("selected", 5);
        CreateBeatPlayer("other", 1);
        using (var db = new SQLiteConnection(BeatLog))
        {
            AddBeatLog(db, Sha256, 0, BeatFrom, 120);
            AddBeatLog(db, UnownedSha, 0, BeatTo - 1, 45);
            AddBeatLog(db, Sha256, 0, BeatFrom - 1, 50);
            AddBeatLog(db, Sha256, 0, BeatTo, 150);
            AddBeatLog(db, Sha256, 1, BeatFrom + 1, 999);
            AddBeatLog(db, "short", 0, BeatFrom + 2, 999);
        }
        endpoint = ScenarioStartupFixture.Create(SettingsPath, library, values =>
        {
            values.OperationModeLR2DB = true;
            values.LR2RootPath = lr2; values.LR2SongDBPath = SongDb; values.LR2ConfigXmlPath = config;
            values.LR2CustomFolderOutputBaseDir = output; values.LR2CustomFolderOutputBaseDirRootType = rootOutput;
            values.LR2CustomFolderAdditionalOutputBaseDirs = "[]"; values.PlaylistDefaultIgnoreFolderOutput = 4095;
            values.UpdateLr2IrRankingCacheOnStartup = false; values.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            values.UseBeatorajaScoreDb = false; values.EnableBeatorajaBmtOutput = false; values.BeatorajaRootPath = "";
            values.UseExternalPanelImage = false; values.UsePlayeruBMplay = false; values.UsePlayerLR2body = false;
            values.UsePlayerBMIIDXView = false; values.IsLR2BackupEnabled = false;
        });
    }

    private void CreateBeatPlayer(string player, int clear)
    {
        string directory = Path.Combine(BeatRoot, "player", player);
        Directory.CreateDirectory(directory);
        using (var db = new SQLiteConnection(Path.Combine(directory, "score.db")))
        {
            db.Execute("CREATE TABLE score(sha256 TEXT,mode INTEGER,clear INTEGER,epg INTEGER,lpg INTEGER,egr INTEGER,lgr INTEGER,notes INTEGER,combo INTEGER,minbp INTEGER,playcount INTEGER,clearcount INTEGER)");
            db.Execute("INSERT INTO score VALUES(?,0,?,55,0,10,0,100,60,5,3,1)", Sha256, clear);
            // player集計は未提供です。scorelog件数を全プレイ回数や既知の0秒へ読み替えないことを確認します。
        }
        using var log = new SQLiteConnection(Path.Combine(directory, "scorelog.db"));
        log.Execute("CREATE TABLE scorelog(sha256 TEXT,mode INTEGER,clear INTEGER,oldclear INTEGER,score INTEGER,oldscore INTEGER,combo INTEGER,oldcombo INTEGER,minbp INTEGER,oldminbp INTEGER,date INTEGER)");
        if (player == "other") { AddBeatLog(log, UnownedSha, 0, BeatFrom + 10, 999); }
    }

    private static void AddBeatLog(SQLiteConnection db, string hash, int mode, long date, int score)
        => db.Execute("INSERT INTO scorelog VALUES(?,?,5,1,?,50,60,20,5,20,?)", hash, mode, score, date);

    /// <summary>schema導入後に外部ゲーム相当のscore/player書込みだけを行い、製品triggerに履歴を生成させます。</summary>
    internal (long From, long To) PlayLr2()
    {
        long from = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var db = new SQLiteConnection(ScoreDb);
        db.RunInTransaction(() =>
        {
            db.Execute("UPDATE score SET playcount=3,clear=4,perfect=55,great=10,minbp=5,maxcombo=60 WHERE hash=?", Md5);
            db.Execute("UPDATE player SET playcount=3,perfect=75,great=20,playtime=90,maxcombo=60 WHERE id='selected'");
            db.Execute("INSERT INTO score(hash,playcount,clear,perfect,great,minbp,maxcombo,totalnotes) VALUES(?,1,3,20,5,10,25,50)", UnownedMd5);
            db.Execute("UPDATE player SET playcount=4,perfect=95,great=25,playtime=105,maxcombo=60 WHERE id='selected'");
        });
        return (from, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>製品全終端後にHTTP入力を回収します。</summary>
    public ValueTask DisposeAsync() => endpoint.DisposeAsync();
}
