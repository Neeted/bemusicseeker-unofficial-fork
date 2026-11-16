using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;

namespace BeMusicSeeker.Desktop.E2E.Tests.Fixtures;

/// <summary>実アプリへ渡すローカルプレイリスト・譜面・設定と、通常起動用の専有HTTP応答を準備します。</summary>
/// <remarks>DBは起動前だけ書きます。実processを終了してからHTTPを終了し、最後に呼出元がWorkspaceを回収します。</remarks>
internal sealed class DesktopPlaylistFixture : IAsyncDisposable
{
    private readonly LocalStartupEndpoint endpoint;
    private readonly string? lr2Root;
    private readonly string? lr2Config;

    private enum InputKind { VirtualRows, SearchAndMemo, FileOperations, OldSchema }

    private DesktopPlaylistFixture(TestWorkspace workspace, InputKind kind)
    {
        ProfileDirectory = workspace.GetPath("profile");
        SongDatabasePath = workspace.GetPath("profile/data/song.db");
        if (kind == InputKind.OldSchema)
        {
            lr2Root = workspace.GetPath("lr2");
            (SongDatabasePath, lr2Config) = SettingsMediaTestFixture.CreateValidLr2Layout(lr2Root);
        }
        LibraryRoot = workspace.GetPath("library");
        Directory.CreateDirectory(LibraryRoot);
        if (kind == InputKind.VirtualRows)
        {
            Charts = Array.AsReadOnly(Enumerable.Range(0, 128).Select(index => CreateChart(LibraryRoot,
                index.ToString("D3", CultureInfo.InvariantCulture),
                "Desktop row " + index.ToString("D3", CultureInfo.InvariantCulture))).ToArray());
        }
        else if (kind == InputKind.SearchAndMemo)
        {
            Charts = Array.AsReadOnly(new[]
            {
                CreateChart(LibraryRoot, "001", "Desktop needle Zebra"),
                CreateChart(LibraryRoot, "002", "Desktop needle Alpha"),
                CreateChart(LibraryRoot, "003", "Desktop needle Middle"),
                CreateChart(LibraryRoot, "004", "Desktop needle Omega"),
                CreateChart(LibraryRoot, "005", "Desktop unrelated")
            });
        }
        else
        {
            var charts = new List<DesktopChartInput>
            {
                CreateChart(Path.Combine(LibraryRoot, RenameOriginalBasename), "chart", "Desktop rename target"),
                CreateChart(Path.Combine(LibraryRoot, "Sentinel"), "sentinel", "Desktop untouched sentinel")
            };
            if (kind == InputKind.OldSchema)
            { charts.Add(CreateChart(Path.Combine(LibraryRoot, "Legacy"), "legacy", "Desktop legacy third")); }
            Charts = charts.AsReadOnly();
            if (kind == InputKind.FileOperations) { Package = CreatePackage(workspace); }
        }
        endpoint = new LocalStartupEndpoint(tableListUrl =>
        {
            SeedDatabase(workspace, kind, tableListUrl);
            WriteSettings(SettingsPath, tableListUrl, kind);
        }, kind == InputKind.SearchAndMemo ? Charts[0] : null);
    }

    /// <summary>通常DBの保存先を限定する専有profileです。</summary>
    internal string ProfileDirectory { get; }
    /// <summary>起動前seedのsong/playlist保存先です。</summary>
    internal string SongDatabasePath { get; }
    /// <summary>製品processが保存する値を、別のfile oracleで直接観測する専有設定ファイルです。</summary>
    internal string SettingsPath => Path.Combine(ProfileDirectory, "config", "user.config");
    /// <summary>実BMSと音源だけを置く登録rootです。</summary>
    internal string LibraryRoot { get; }
    /// <summary>parserを呼ばず入力bytesから宣言した、一意title/hashの集合です。</summary>
    internal IReadOnlyList<DesktopChartInput> Charts { get; }
    /// <summary>登録済み表を他の表から区別する保存済みIDです。</summary>
    internal const int PlaylistId = 731;
    /// <summary>同期OFFの普通folderとして保存する名称です。</summary>
    internal const string FolderName = "Desktop";
    /// <summary>既存の通常更新確認へ渡すNoUpdate応答です。</summary>
    internal string UpdateManifestUrl => endpoint.ManifestUrl;
    /// <summary>Fullでは指定候補、日常・局所では同じbuildの実製品を参照し、起動前資産と揃えます。</summary>
    internal static string ExecutablePath => ScenarioApplicationPaths.ExecutablePath;

    /// <summary>起動前入力を完成させます。グローバルSettings/DB/実利用者profileは変更しません。</summary>
    internal static DesktopPlaylistFixture Create(TestWorkspace workspace) => new(workspace, InputKind.VirtualRows);

    /// <summary>検索一致四譜面と不一致一譜面、同期ONの単一entryを持つDT-01入力を準備します。</summary>
    internal static DesktopPlaylistFixture CreateSearchAndMemo(TestWorkspace workspace) => new(workspace, InputKind.SearchAndMemo);

    /// <summary>同期OFFの空folder二つ、子folder内の改名対象と非対象、library外のZIPを準備します。</summary>
    internal static DesktopPlaylistFixture CreateFileOperations(TestWorkspace workspace) => new(workspace, InputKind.FileOperations);

    /// <summary>専有LR2媒体にsha256列のない旧playlist schemaを作り、取消後も同じ入力を次の明示起動へ渡します。</summary>
    internal static DesktopPlaylistFixture CreateOldSchemaRepair(TestWorkspace workspace) => new(workspace, InputKind.OldSchema);

    /// <summary>DT-02のlibrary外のZIPと、展開結果を独立比較する元入力です。他ケースではnullです。</summary>
    internal DesktopPackageInput? Package { get; }
    /// <summary>DT-01で実検索TextBoxへ入力する、一致四譜面だけに含まれる文字列です。</summary>
    internal const string SearchText = "needle";
    /// <summary>同期ON entryの初期MEMOです。HTTPに編集期待値を渡しません。</summary>
    internal const string InitialMemo = "before";
    /// <summary>DT-01のUI編集で初めて与える保存期待値です。</summary>
    internal const string EditedMemo = "Desktop memo committed";
    /// <summary>DT-02の内部行ドロップ先として使う通常playlist folderです。</summary>
    internal const string ImportFolderName = "ImportTarget";
    /// <summary>DT-02で削除拒否・承認を操作する、初期状態が空のplaylist folderです。</summary>
    internal const string DeleteFolderName = "DeleteMe";
    /// <summary>登録rootの直下に置く物理改名対象の元basenameです。</summary>
    internal const string RenameOriginalBasename = "RenameSource";
    /// <summary>Enterで確定する物理folder改名候補です。</summary>
    internal const string RenameCommittedBasename = "RenameCommitted";

    /// <summary>既存UIA仕様の検索IDを構成します。ordinalと特定titleの対応を保存形式の保証にしません。</summary>
    internal string EntryAutomationId(int ordinal) => "playlist-entry:"
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(PlaylistId.ToString(CultureInfo.InvariantCulture)))
        + ":" + ordinal.ToString(CultureInfo.InvariantCulture);

    private void SeedDatabase(TestWorkspace workspace, InputKind kind, Uri tableListUrl)
    {
        var scope = new ApplicationDatabaseScope();
        string assets = Path.GetDirectoryName(ExecutablePath) ?? throw new InvalidOperationException("実行体の親がありません。");
        var paths = ApplicationPathSnapshot.FromLayout(ExecutablePath, assets, ProfileDirectory,
            Path.Combine(assets, "lang"), workspace.GetPath("temp"), workspace.GetPath("legacy"));
        if (kind == InputKind.OldSchema)
        {
            string configPath = lr2Config ?? throw new InvalidOperationException("専有LR2設定がありません。");
            File.WriteAllText(configPath, "<config><system /><jukebox /><player><id>selected</id></player></config>");
            LR2Config config = TestLr2ConfigFactory.Load(configPath);
            config.AddBMSSearchDirectories([LibraryRoot]);
            config.Save(configPath);
            using (var initial = new SQLite.SQLiteConnection(SongDatabasePath))
            {
                initial.CreateTable<LR2SongDB.song>();
                initial.CreateTable<LR2SongDB.folder>();
            }
            new BmsLibraryDbGateway(scope, new BmsDatabaseLoadOptions(true, true, true), SongDatabasePath).EnsureAppOwnedSchema();
            PlaylistPersistenceRepository.EnsureSchema(scope, SongDatabasePath);
        }
        else { StandaloneLibraryDatabase.EnsurePortableSongDb(scope, paths); }
        using LR2SongDBExtended db = scope.OpenSongWrite(SongDatabasePath);
        db.RunInTransaction(() =>
        {
            bool externalSync = kind == InputKind.SearchAndMemo;
            string name = externalSync ? "Desktop synced playlist" : "Desktop local playlist";
            string[] folders = kind is InputKind.VirtualRows or InputKind.OldSchema ? [FolderName]
                : externalSync ? ["1"] : [ImportFolderName, DeleteFolderName];
            db.Execute("INSERT INTO playlist (playlist_id,name,symbol,folder_order,folder_sort_key,folder_sort_ascending,entry_type,page_url,header_url,data_url,compat_prefix,last_update,org_name,org_symbol,ignore_folder_output,is_external_sync,output_dir,is_root_folder,is_bmt_output,header_sha256,data_sha256) VALUES (?,?,?,?,0,1,0,NULL,?,?,'',?,?,?,0,?,NULL,0,0,NULL,NULL)",
                PlaylistId, name, "DT", JsonSerializer.Serialize(folders),
                externalSync ? new Uri(tableListUrl, "header.json").AbsoluteUri : null,
                externalSync ? new Uri(tableListUrl, "data.json").AbsoluteUri : null,
                DateTime.UnixEpoch.Ticks, name, "DT", externalSync ? 1 : 0);
            foreach (DesktopChartInput chart in Charts)
            {
                if (!Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(chart.Path, out string folder, out string parent))
                { throw new InvalidOperationException("fixtureのsong folderを構成できません。"); }
                int date = checked((int)new DateTimeOffset(File.GetLastWriteTimeUtc(chart.Path)).ToUnixTimeSeconds());
                db.Execute("INSERT INTO song (path,hash,title,artist,folder,parent,adddate,date,txt,mode,level,judge) VALUES (?,?,?,?,?,?,0,?,0,7,1,2)",
                    chart.Path, chart.Md5, chart.Title, "Desktop fixture", folder, parent, date);
                db.Execute("INSERT INTO chart_digest_map (md5,sha256) VALUES (?,?)", chart.Md5, chart.Sha256);
                // 既存行と同内容の走査に解析結果の新規生成を期待せず、入力で定義した資源の保存値をseedする。
                db.Execute("INSERT INTO maintenance (path,hash,encoding,is_encoding_fixed,wav_files_defined,wav_files_existing,bga_files_defined,bga_files_existing,movie_files_defined,movie_files_existing,is_stagefile_defined,is_stagefile_existing,is_banner_defined,is_banner_existing,is_backbmp_defined,is_backbmp_existing) VALUES (?,?,'utf-8',0,1,1,0,0,0,0,0,NULL,0,NULL,0,NULL)",
                    chart.Path, chart.Md5);
                if (kind is InputKind.VirtualRows or InputKind.OldSchema || (externalSync && ReferenceEquals(chart, Charts[0])))
                {
                    // 通常の登録保存ではBMSTableEntryのgetterが未設定URLと親MD5を空文字で出力する。
                    // rawseedもその保存表現に揃え、MEMO操作時の非対象列比較をNULL同値化で緩めない。
                    db.Execute("INSERT INTO playlist_entry (playlist_id,md5,sha256,title,artist,folder,level,adddate,memo,is_removed,url,url_diff,org_md5) VALUES (?,?,?,?,?,?,NULL,?,?,0,'','','')",
                        PlaylistId, chart.Md5, chart.Sha256, chart.Title, "Desktop fixture",
                        externalSync ? "1" : FolderName, DateTime.UnixEpoch.Ticks,
                        externalSync ? InitialMemo : kind == InputKind.OldSchema
                            ? ReferenceEquals(chart, Charts[0]) ? null : ReferenceEquals(chart, Charts[1]) ? string.Empty : InitialMemo
                            : "before " + chart.Title);
                }
            }
            if (kind == InputKind.FileOperations)
            {
                // 未指定ヘッダーは通常生成保存のsong getterが空文字で出力する。改名時の全列比較の入力も同じ保存表現に揃える。
                db.Execute("UPDATE song SET subtitle='',subartist='',genre='',stagefile='',banner='',backbmp=''");
                // 通常の空folder作成はCreateDummyBMSTableEntryの所属行を保存する。folder_orderだけではfolderは存在しない。
                foreach (string folder in new[] { ImportFolderName, DeleteFolderName })
                {
                    db.Execute("INSERT INTO playlist_entry (playlist_id,md5,sha256,title,artist,folder,level,adddate,memo,is_removed,url,url_diff,org_md5) VALUES (?,'00000000000000000000000000000000',NULL,'','',?,NULL,?,NULL,0,'','','')",
                        PlaylistId, folder, DateTime.UnixEpoch.Ticks);
                }
            }
            if (kind == InputKind.OldSchema)
            {
                db.Execute("UPDATE song SET tag='Desktop user tag',favorite=3,adddate=123456789");
                db.Execute("CREATE TABLE desktop_user_notes(id INTEGER PRIMARY KEY,value TEXT,bytes BLOB)");
                db.Execute("INSERT INTO desktop_user_notes VALUES(9,'Desktop user data',X'0011FF')");
                db.Execute("DROP INDEX playlist_entry_idx_uniq");
                db.Execute("DROP INDEX playlist_entry_idx_sha256");
                db.Execute("DROP INDEX playlist_entry_idx_candidate");
                db.Execute("ALTER TABLE playlist_entry DROP COLUMN sha256");
                if (db.Execute("UPDATE app_schema_version SET version=0 WHERE name='app_schema'") != 1)
                { throw new InvalidOperationException("fixtureのapp_schema版を旧版へ変更できません。"); }
            }
        });
    }

    private void WriteSettings(string settingsPath, Uri tableListUrl, InputKind kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath) ?? throw new InvalidOperationException("設定の親がありません。"));
        var settings = new Settings();
        var provider = new PortableSettingsProvider(settingsPath);
        provider.Initialize(nameof(PortableSettingsProvider), new NameValueCollection());
        settings.Providers.Clear(); settings.Providers.Add(provider);
        foreach (SettingsProperty property in settings.Properties) { property.Provider = provider; }
        settings.OperationModeLR2DB = false;
        settings.BMSRootPath = settings.StandaloneBmsRootPaths = settings.BMSInstallDir = LibraryRoot;
        settings.Lang = "ja-JP";
        settings.AssemblyVersion = new SerializableVersion(typeof(ApplicationDatabaseScope).Assembly.GetName().Version
            ?? throw new InvalidOperationException("製品versionがありません。"));
        settings.ScanBmsFilesOnStartup = true;
        settings.SkipInitPlaylistLoad = false;
        settings.EnablePlaylistUrlCompletion = false;
        settings.StartupSelectInstallPending = false;
        settings.CustomTableRowHeight = 25d;
        // 実UIAのセルは水平可視列だけに存在するため、専有profileの操作対象を前方へ置く。
        // 既存index同士の交換により、製品の列追加があっても固定indexや重複を持ち込まない。
        var playlistColumns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.PLAYLIST);
        (playlistColumns.Memo.DisplayIndex, playlistColumns.Folder.DisplayIndex) =
            (playlistColumns.Folder.DisplayIndex, playlistColumns.Memo.DisplayIndex);
        playlistColumns.Memo.Visibility = Visibility.Visible;
        playlistColumns.Memo.Width = 180;
        playlistColumns.Title.Width = 240;
        settings.PlaylistCustomTableColumnSettings = playlistColumns;
        var standardColumns = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.STANDARD);
        (standardColumns.Folder.DisplayIndex, standardColumns.Artist.DisplayIndex) =
            (standardColumns.Artist.DisplayIndex, standardColumns.Folder.DisplayIndex);
        standardColumns.Folder.Visibility = Visibility.Visible;
        standardColumns.Folder.Width = 180;
        standardColumns.Title.Width = 240;
        settings.StandardCustomTableColumnSettings = standardColumns;
        settings.TableListURL = tableListUrl;
        if (kind == InputKind.OldSchema)
        {
            string root = lr2Root ?? throw new InvalidOperationException("専有LR2媒体がありません。");
            settings.OperationModeLR2DB = true;
            settings.LR2RootPath = root;
            settings.LR2SongDBPath = SongDatabasePath;
            settings.LR2ConfigXmlPath = lr2Config ?? throw new InvalidOperationException("専有LR2設定がありません。");
            settings.LR2CustomFolderOutputBaseDir = Path.Combine(root, "output");
            settings.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "root-output");
            settings.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
            Directory.CreateDirectory(settings.LR2CustomFolderOutputBaseDir);
            Directory.CreateDirectory(settings.LR2CustomFolderOutputBaseDirRootType);
            Directory.CreateDirectory(Path.Combine(root, "LR2files", "Database", "Score"));
            settings.SkipInitPlaylistLoad = true;
            settings.UpdateLr2IrRankingCacheOnStartup = false;
            settings.EnableDownloadLr2IrScoreAndDetectUnsent = false;
            settings.UseBeatorajaScoreDb = false;
            settings.EnableBeatorajaBmtOutput = false;
            settings.UseExternalPanelImage = false;
            settings.UsePlayeruBMplay = false;
            settings.UsePlayerLR2body = false;
            settings.UsePlayerBMIIDXView = false;
            settings.BeatorajaRootPath = string.Empty;
            settings.IsLR2BackupEnabled = false;
        }
        settings.Save();
    }

    /// <summary>
    /// 指定ディレクトリへ音源1件とBMSを作り、parserに依存しないMD5・SHA-256付きの入力を返します。
    /// 既存のDesktop fixtureと通常起動の受入（SC-16）が同じ譜面生成を共用するためにinternalとします。
    /// </summary>
    internal static DesktopChartInput CreateChart(string directory, string basename, string title)
    {
        Directory.CreateDirectory(directory);
        byte[] resource = CreateWave();
        string resourcePath = Path.Combine(directory, "tone.wav");
        File.WriteAllBytes(resourcePath, resource);
        string path = Path.Combine(directory, basename + ".bms");
        byte[] bytes = Encoding.ASCII.GetBytes($"#PLAYER 1\r\n#TITLE {title}\r\n#ARTIST Desktop fixture\r\n#BPM 120\r\n#PLAYLEVEL 1\r\n#RANK 2\r\n#WAV01 tone.wav\r\n#00111:01\r\n");
        File.WriteAllBytes(path, bytes);
        return new DesktopChartInput(path, title, Convert.ToHexStringLower(MD5.HashData(bytes)),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), Array.AsReadOnly(bytes),
            new DesktopFileInput(resourcePath, Array.AsReadOnly(resource), Convert.ToHexStringLower(SHA256.HashData(resource))));
    }

    private static DesktopPackageInput CreatePackage(TestWorkspace workspace)
    {
        // 登録rootの外に置き、起動scanで導入済みになることを避ける。
        DesktopChartInput chart = CreateChart(workspace.GetPath("package-input"), "imported", "Desktop ZIP imported");
        string zipPath = workspace.GetPath("drop-input/import.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? throw new InvalidOperationException("ZIPの親がありません。"));
        DesktopArchiveEntryInput[] manifest =
        [
            new("Imported/chart.bms", chart.Bytes, chart.Sha256),
            new("Imported/tone.wav", chart.Resource.Bytes, chart.Resource.Sha256)
        ];
        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (DesktopArchiveEntryInput entry in manifest)
            {
                using Stream output = archive.CreateEntry(entry.RelativePath).Open();
                output.Write(entry.Bytes.ToArray());
            }
        }
        return new DesktopPackageInput(zipPath, chart, Array.AsReadOnly(manifest));
    }

    private static byte[] CreateWave()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(196);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
        writer.Write((short)2); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(160);
        for (int index = 0; index < 80; index++) { writer.Write((short)(index % 8 < 4 ? 2048 : -2048)); }
        writer.Flush(); return stream.ToArray();
    }

    /// <summary>実process終了後に専有HTTPの待受と全処理を回収します。</summary>
    public ValueTask DisposeAsync() => DisposeAsync(ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));

    /// <summary>process回収と同じ絶対期限で専有HTTPの終端を待ちます。</summary>
    internal ValueTask DisposeAsync(ProcessDeadline cleanupDeadline) => endpoint.DisposeAsync(cleanupDeadline);

    private sealed class LocalStartupEndpoint : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly Task serving;
        private readonly string? headerJson;
        private readonly string? dataJson;

        /// <summary>OSが割り当てた専有portを確保し、そのURLを設定へ保存してから応答を開始します。</summary>
        internal LocalStartupEndpoint(Action<Uri> prepareSettings, DesktopChartInput? syncedChart)
        {
            listener.Start();
            string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            ManifestUrl = url + "/update.json";
            try
            {
                if (syncedChart is not null)
                {
                    headerJson = JsonSerializer.Serialize(new
                    {
                        name = "Desktop synced playlist",
                        symbol = "DT",
                        compat_prefix = "",
                        data_url = url + "/data.json",
                        level_order = new[] { "1" }
                    });
                    dataJson = JsonSerializer.Serialize(new[]
                    {
                        new { md5 = syncedChart.Md5, sha256 = syncedChart.Sha256, title = syncedChart.Title,
                            artist = "Desktop fixture", level = "1" }
                    });
                }
                prepareSettings(new Uri(url + "/table-list.json"));
                serving = ServeAsync();
            }
            catch { listener.Stop(); stopping.Dispose(); throw; }
        }

        /// <summary>このfixtureだけが返す通常NoUpdate manifestの絶対URLです。</summary>
        internal string ManifestUrl { get; }

        private async Task ServeAsync()
        {
            const string noUpdate = """
                {"schemaVersion":1,"version":"0.0.0.0","releaseTag":"v0.0.0.0","releasePageUrl":"http://127.0.0.1/unused","packageFormatVersion":1,"minimumUpdaterVersion":"1","assets":[{"kind":"app","label":"Desktop fixture","fileName":"unused.zip","url":"http://127.0.0.1/unused.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","sizeBytes":1,"includesChartInfoMetadata":false}]}
                """;
            while (true)
            {
                // DisposeAsyncはCancelをStopより先に行うが、Acceptはtokenの取消より先にlistenerの非稼働を検査して
                // InvalidOperationException("Not listening")を同期的に投げうる。停止は必ず取消要求の後なので、
                // Acceptの呼出しだけを囲み、取消要求済みの場合に限って停止として扱う。
                TcpClient accepted;
                try { accepted = await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false); }
                catch (InvalidOperationException) when (stopping.IsCancellationRequested) { break; }
                using TcpClient client = accepted;
                await using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false);
                string? line;
                do { line = await reader.ReadLineAsync(stopping.Token).ConfigureAwait(false); } while (line is { Length: > 0 });
                string[] request = requestLine?.Split(' ', 3) ?? [];
                string path = request.Length == 3 && request[0] == "GET" ? request[1].Split('?', 2)[0] : string.Empty;
                (string status, string body) = path switch
                {
                    "/update.json" => ("200 OK", noUpdate),
                    "/table-list.json" => ("200 OK", "[]"),
                    "/header.json" when headerJson is not null => ("200 OK", headerJson),
                    "/data.json" when dataJson is not null => ("200 OK", dataJson),
                    _ => ("404 Not Found", "{}")
                };
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                byte[] response = [.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), .. bytes];
                await stream.WriteAsync(response, stopping.Token).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => DisposeAsync(ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));

        internal async ValueTask DisposeAsync(ProcessDeadline cleanupDeadline)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            listener.Stop();
            try { await serving.WaitAsync(cleanupDeadline.Remaining).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            finally { stopping.Dispose(); }
        }
    }
}

/// <summary>fixture自身が宣言した譜面の実path・表示title・入力bytesのhashです。</summary>
/// <param name="Path">専有workspace内の実BMSファイルです。</param>
/// <param name="Title">入力に直接記載した一意な表示名です。</param>
/// <param name="Md5">入力bytesのMD5です。</param>
/// <param name="Sha256">入力bytesのSHA-256です。</param>
/// <param name="Bytes">FS比較用に保持する、parserに依存しない元BMS bytesです。</param>
/// <param name="Resource">BMSが参照する音源の元入力です。</param>
internal sealed record DesktopChartInput(string Path, string Title, string Md5, string Sha256,
    IReadOnlyList<byte> Bytes, DesktopFileInput Resource);

/// <summary>物理改名の対象・非対象を、元入力bytesと照合するファイルです。</summary>
/// <param name="Path">起動前の専有実pathです。</param>
/// <param name="Bytes">ファイルへ直接書いた元bytesです。</param>
/// <param name="Sha256">元bytesのSHA-256です。</param>
internal sealed record DesktopFileInput(string Path, IReadOnlyList<byte> Bytes, string Sha256);

/// <summary>ZIP展開結果と比較する、archive生成前に宣言した一項目です。</summary>
/// <param name="RelativePath">ZIP内の相対pathです。</param>
/// <param name="Bytes">期待する内容の元bytesです。</param>
/// <param name="Sha256">元bytesのSHA-256です。</param>
internal sealed record DesktopArchiveEntryInput(string RelativePath, IReadOnlyList<byte> Bytes, string Sha256);

/// <summary>実FileDropへ渡すZIPと、raw DB/FS oracleへ渡す独立入力です。</summary>
/// <param name="ZipPath">library外の専有ZIPです。</param>
/// <param name="Chart">ZIPに格納したBMSの元入力です。Pathは導入後pathではありません。</param>
/// <param name="ExpectedManifest">展開後の相対path・内容・hashの期待値です。</param>
internal sealed record DesktopPackageInput(string ZipPath, DesktopChartInput Chart,
    IReadOnlyList<DesktopArchiveEntryInput> ExpectedManifest);
