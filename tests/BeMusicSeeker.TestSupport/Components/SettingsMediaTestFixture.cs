using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>LR2設定の残留ケースと移管ケースが共有する専用媒体だけを準備します。</summary>
internal static class SettingsMediaTestFixture
{
    /// <summary>LR2の実標準配置と検索登録を作り、各出力基点を専用rootへ隔離します。</summary>
    internal static Settings CreateValidCustomFolderSettings(string root, IEnumerable<string> registeredRoots, Settings? ownedValues = null)
    {
        string songs = Path.Combine(root, "Songs");
        Directory.CreateDirectory(songs);
        (string songDb, string configPath) = CreateValidLr2Layout(root);
        Settings values = CreateValidStandaloneSettings(songs, ownedValues);
        values.OperationModeLR2DB = true;
        values.LR2RootPath = root;
        values.LR2SongDBPath = songDb;
        values.LR2ConfigXmlPath = configPath;
        values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "NormalOutput");
        values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "RootOutput");
        values.LR2CustomFolderAdditionalOutputBaseDirs = "[]";
        Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDir);
        Directory.CreateDirectory(values.LR2CustomFolderOutputBaseDirRootType);
        string[] roots = new[] { songs }.Concat(registeredRoots).ToArray();
        foreach (string path in roots)
        {
            Directory.CreateDirectory(path);
        }
        var config = new BeMusicSeeker.Models.LR2.LR2Config(configPath);
        config.AddBMSSearchDirectories(roots);
        config.Save(config.ConfigFilePath);
        return values;
    }

    /// <summary>LR2の標準song.db・設定XML・実行ファイル識別を専用rootに作ります。</summary>
    internal static (string SongDb, string Config) CreateValidLr2Layout(string root)
    {
        string songDb = Path.Combine(root, "LR2files", "Database", "song.db");
        string config = Path.Combine(root, "LR2files", "Config", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(songDb)!);
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllBytes(songDb, []);
        File.WriteAllText(config, "<config><system /><jukebox /></config>");
        File.WriteAllBytes(Path.Combine(root, "LR2body.exe"), []);
        return (songDb, config);
    }

    /// <summary>外部通信・自動走査を使わない、専用rootの単独動作設定を作ります。</summary>
    internal static Settings CreateValidStandaloneSettings(string root, Settings? ownedValues = null)
    {
        Settings settings = ownedValues ?? TestSettingsFactory.CreateIsolatedSettings();
        void Configure(Settings values)
        {
            values.OperationModeLR2DB = false;
            values.BMSRootPath = root;
            values.StandaloneBmsRootPaths = root;
            values.BMSInstallDir = root;
            values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
            values.EnablePlaylistUrlCompletion = false;
            values.ScanBmsFilesOnStartup = false;
            values.SkipInitPlaylistLoad = true;
            values.UseBeatorajaScoreDb = false;
            values.EnableBeatorajaBmtOutput = false;
            values.UseExternalPanelImage = false;
            values.UsePlayeruBMplay = false;
            values.UsePlayerLR2body = false;
            values.UsePlayerBMIIDXView = false;
            values.IsLR2BackupEnabled = false;
            values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
        }
        Configure(settings);
        return settings;
    }

    /// <summary>各ケースが所有・削除する専用の一時rootを作ります。</summary>
    internal static string CreateTemporaryRoot(TestWorkspace workspace)
    {
        string root = Path.Combine(workspace.RootDirectory, "BeMusicSeeker_SettingDialogEditCompletion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
