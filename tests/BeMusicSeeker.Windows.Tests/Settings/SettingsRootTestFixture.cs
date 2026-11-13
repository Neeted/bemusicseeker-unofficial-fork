using System.IO;
using System.Reflection;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Tests;

/// <summary>複数の設定テストが使う実媒体・root接続・起動終端の準備を集約します。</summary>
internal static class SettingsRootTestFixture
{
    /// <summary>通常・追加・ROOTの指定された出力候補だけを変更します。</summary>
    internal static void SetCustomFolderOutput(SettingsDialogViewModel dialog, string role, string path)
    {
        switch (role)
        {
            case "normal": dialog.LR2CustomFolderOutputDir = path; break;
            case "additional": dialog.AddCustomFolderAdditionalOutputBaseDirs([path]); break;
            case "root": dialog.LR2CustomFolderAsRootOutputDir = path; break;
            default: throw new ArgumentOutOfRangeException(nameof(role));
        }
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
        IUiDialogService settingsDialogs = dialogs ?? UndeclaredDialogs.Create();
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            settingsEditSession: settingsSession,
            reportSettingsApplyFailure: reportSettingsApplyFailure ?? (_ => { }),
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
            cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
            settingsDialogService: settingsDialogs,
            fileDbMutationDialogService: settingsDialogs,
            dialogService: UndeclaredDialogs.Create());
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
                settingsSession, new ApplicationDataUninstallWorkflowOwner(settingsDialogs, new Lr2ApplicationDataUninstallStore(composition.DatabaseScope)),
                applicationLifetime: TestApplicationContext.CreateLifetime(firstStartup),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                schemaDialogs: settingsDialogs,
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
