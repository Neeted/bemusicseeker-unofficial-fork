using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>複数の設定テストが使う実媒体・root接続・起動終端の準備を集約します。</summary>
internal static class SettingsRootTestFixture
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

    /// <summary>通常・追加・ROOTの指定された出力候補だけを変更します。</summary>
    internal static void SetCustomFolderOutput(SettingsDialogViewModel dialog, string role, string path)
    {
        switch (role)
        {
            case "normal": dialog.LR2CustomFolderOutputDir = path; break;
            case "additional": dialog.AddCustomFolderAdditionalOutputBaseDir(path); break;
            case "root": dialog.LR2CustomFolderAsRootOutputDir = path; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
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

    /// <summary>実compositionと設定セッションを接続し、明示した継続処理だけを置き換えます。</summary>
    internal static MainWindowViewModel CreateViewModel(
        ISettingsEditSession settingsSession,
        bool firstStartup,
        Func<MainWindowViewModel, Task<bool>>? initializeOwner = null,
        Func<MainWindowViewModel, Task>? reloadScoresOnly = null,
        Action<Exception>? reportSettingsApplyFailure = null,
        Func<MainWindowViewModel, Task>? reloadFileDiff = null,
        ISettingsDialogPlayerFactoryPort? playerFactoryPort = null,
        ISettingsDialogPlaybackRuntimePort? playbackRuntimePort = null,
        IUiDialogService? dialogs = null,
        Func<MainWindowViewModel, LibraryFileMutationCapability, Task>? reloadScoresOnlyUnderAdmission = null)
    {
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            settingsEditSession: settingsSession,
            reportSettingsApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            settingsDialogService: dialogs,
            fileDbMutationDialogService: dialogs);
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
        if (initializeOwner != null || reloadScoresOnly != null || reloadFileDiff != null || reloadScoresOnlyUnderAdmission != null)
        {
            SettingsDialogViewModel testDialog = new(composition.DatabaseScope,
                new TestSettingsDialogStatePort(
                    viewModel,
                    initializeOwner == null
                        ? () => ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(null)
                        : async () => new StartupInitializationResult(await initializeOwner(viewModel)
                            ? StartupInitializationOutcome.Succeeded
                            : StartupInitializationOutcome.SettingsRequired),
                    () =>
                    {
                        SetPrivateField(viewModel, "initializationCompleted", false);
                        SetPrivateField(viewModel, "hasActiveLibraryProfile", false);
                    },
                    reloadScoresOnly: capability => reloadScoresOnlyUnderAdmission != null
                        ? reloadScoresOnlyUnderAdmission(viewModel, capability ?? throw new InvalidOperationException("ScoreOnly continuation requires accepted capability."))
                        : reloadScoresOnly == null ? Task.CompletedTask : reloadScoresOnly(viewModel),
                    reloadFileDiff: reloadFileDiff == null
                        ? _ => Task.CompletedTask
                        : _ => reloadFileDiff(viewModel),
                    initializeAcceptedLibrary: initializeOwner == null
                        ? (capability, approval, observer, forceProjection) => ((ISettingsDialogStatePort)viewModel).InitializeLibraryAsync(capability, approval, observer, forceProjection)
                        : null),
                viewModel.PlaylistWorkspace,
                viewModel.PlaylistWorkspace,
                viewModel.PlayHistory,
                viewModel.LibraryFolderTree,
                new TestSettingsDialogPlayerFactoryPort(),
                new TestSettingsDialogPlaybackRuntimePort(),
                viewModel.Lr2SongDbSyncWorkflow,
                settingsSession, new ApplicationDataUninstallWorkflowOwner(dialogs ?? new UiDialogCoordinator(), new Lr2ApplicationDataUninstallStore(composition.DatabaseScope)),
                applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                schemaDialogs: dialogs,
                reportApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
                externalShellGateway: ExternalShellGatewayPolicy.Current,
                applicationPathSnapshot: ApplicationPathPolicy.Current,
                audioDeviceCatalog: new TestAudioDeviceCatalog(),
                audioSettingsGateway: new TestAudioSettingsGateway(),
                audioDeviceTestWorkflow: AudioDeviceTestWorkflowTestFactory.Create(),
                operationAdmission: composition.OperationAdmission);
            viewModel.SettingDialog.Dispose();
            typeof(MainWindowViewModel)
                .GetProperty("SettingDialog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .SetValue(viewModel, testDialog);
        }
        typeof(SettingsDialogViewModel)
            .GetField("playerFactoryPort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, playerFactoryPort ?? new TestSettingsDialogPlayerFactoryPort());
        typeof(SettingsDialogViewModel)
            .GetField("playbackRuntimePort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingDialog, playbackRuntimePort ?? new TestSettingsDialogPlaybackRuntimePort());
        return viewModel;
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
    internal static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_SettingDialogEditCompletion_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>次の明示操作の準備として、起動後にLを取得し得る受理済み保守の実終端を待ちます。必須起動成功の条件は変更しません。</summary>
    internal static void AwaitStartupBackgroundCompletionOnDispatcher(MainWindowViewModel viewModel, string operationName)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = typeof(MainWindowViewModel)
            .GetField("startupBackgroundTaskScheduler", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel)
            as StartupBackgroundTaskSchedulerOwner ?? throw new InvalidOperationException("Actual startup scheduler is unavailable.");
        var backgroundTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void BackgroundChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (!viewModel.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle)
            {
                backgroundTerminal.TrySetResult();
            }
        }
        viewModel.ProgressHub.PropertyChanged += BackgroundChanged;
        try
        {
            if (viewModel.ProgressHub.IsStartupBackgroundInitializationActive || !scheduler.IsFullyIdle)
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(backgroundTerminal.Task, operationName);
            }
        }
        finally
        {
            viewModel.ProgressHub.PropertyChanged -= BackgroundChanged;
        }
    }

    /// <summary>実rootのprofile採用状態を、初期化を必要としないケースの前提へ合わせます。</summary>
    internal static void SetActiveLibraryProfile(MainWindowViewModel viewModel, bool value)
    {
        typeof(MainWindowViewModel)
            .GetField("hasActiveLibraryProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, value);
    }

    /// <summary>実owner接続を検証するケースの準備として、指定された内部状態を設定します。</summary>
    internal static void SetPrivateField(object instance, string fieldName, object value)
    {
        instance.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(instance, value);
    }

    /// <summary>機器を必要としない実beatoraja構成とplayer/score媒体を、ケース固有rootに作ります。</summary>
    internal static void WriteBeatorajaRoot(string root, string configuredPlayer, IReadOnlyList<string> players, bool createScoreFiles)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
        File.WriteAllText(Path.Combine(root, BeatorajaConfigService.ConfigFileName), System.Text.Json.JsonSerializer.Serialize(
            new { playerpath = "profiles", playername = configuredPlayer, tablepath = "tables", tableURL = Array.Empty<string>() }));
        foreach (string player in players)
        {
            string directory = Path.Combine(root, "profiles", player);
            Directory.CreateDirectory(directory);
            if (createScoreFiles) { File.WriteAllText(Path.Combine(directory, "score.db"), string.Empty); }
        }
    }
}
