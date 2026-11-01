using System.Reflection;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class ShellShutdownTestSupport
{

    /// <summary>終了処理の依存を置き換え、一時入力は各テストの所有範囲で回収します。</summary>
    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static ShellShutdownWorkflowOwner CreateDirectOwner(
        MainWindowViewModel viewModel,
        Func<Func<Task>, Task>? dispatch = null,
        Action<string>? markShutdown = null,
        StartupUpdateWorkflowOwner? startupUpdate = null,
        ISettingsEditSession? settingsEditSession = null,
        Action? requestApplicationShutdown = null,
        Func<Task>? stopPerformanceDiagnostics = null,
        Action<string>? logShutdown = null,
        Action<string>? logShutdownWarning = null,
        PlaylistWorkspaceViewModel? playlistWorkspace = null,
        PackageInstallWorkflowOwner? packageInstallWorkflow = null,
        MaintenanceRescanWorkflowOwner? maintenanceRescanWorkflow = null,
        FolderAutoRenameWorkflowOwner? folderAutoRenameWorkflow = null,
        Action<Exception>? reportSettingsSaveFailure = null,
        Func<Task>? startApplicationRestart = null,
        IUiDialogService? restartFailureDialogs = null,
        Action<Exception>? reportRestartFailure = null,
        Func<Action<string, Exception>, Task>? completeTemporaryDirectories = null)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = GetPrivateField<StartupBackgroundTaskSchedulerOwner>(
            viewModel,
            "startupBackgroundTaskScheduler");
        startupUpdate ??= new StartupUpdateWorkflowOwner(
                () => Task.FromResult(UpdateCheckResult.NoUpdate("1.0.0.0")),
                asset => Task.FromResult("package.zip"),
                packagePath => new NoOpPreparedUpdaterLaunch(),
                () => { },
                packagePath => { },
                action => Task.Run(action),
                action => action());
        return new ShellShutdownWorkflowOwner(
            MainWindowViewModelTestFactory.GetComposition(viewModel).DatabaseScope,
            startupUpdate,
            new ElevatedProcessWarningWorkflowOwner(() => false),
            scheduler,
            viewModel.RegularChartList,
            playlistWorkspace ?? viewModel.PlaylistWorkspace,
            viewModel.PlayHistory,
            packageInstallWorkflow ?? viewModel.PackageInstallWorkflow,
            maintenanceRescanWorkflow ?? viewModel.MaintenanceRescanWorkflow,
            folderAutoRenameWorkflow ?? viewModel.FolderAutoRenameWorkflow,
            viewModel.PlaybackPanel,
            settingsEditSession
                ?? GetPrivateField<ApplicationComposition>(viewModel, "applicationComposition").SettingsEditSession,
            GetPrivateField<ApplicationComposition>(viewModel, "applicationComposition").OperationAdmission,
            GetPrivateField<ApplicationComposition>(viewModel, "applicationComposition").PlaylistOperationAdmission,
            new SemaphoreSlim(1, 1),
            viewModel.ProgressHub.StartupProgress,
            markShutdown ?? (_ => { }),
            requestApplicationShutdown ?? (() => { }),
            stopPerformanceDiagnostics ?? (() => Task.CompletedTask),
            dispatch ?? (action => action()),
            logShutdown ?? (_ => { }),
            logShutdownWarning ?? (_ => { }),
            value => value ?? string.Empty,
            completeTemporaryDirectories ?? (_ => Task.CompletedTask),
            reportSettingsSaveFailure,
            startApplicationRestart,
            restartFailureDialogs,
            reportRestartFailure);
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static T GetPrivateField<T>(MainWindowViewModel viewModel, string name)
    {
        return (T)typeof(MainWindowViewModel)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
    }

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal sealed class NoOpPreparedUpdaterLaunch : IPreparedUpdaterLaunch
    {
        public UpdaterLaunchReceipt Start()
        {
            return new UpdaterLaunchReceipt();
        }
    }

}
