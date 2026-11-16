using System.Reflection;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Wpf;

/// <summary>実MainWindowの構築・表示とshutdownを同じWPF所有scopeへ接続します。</summary>
internal static class MainWindowPresentationTestHarness
{
    /// <summary>constructor-onlyの表示保証に不要な実起動を、既存のactivation境界で抑止します。</summary>
    /// <remarks>実起動はMainWindowViewHostTestsとPlaylistのActualMainWindowFixtureが本番経路を検証します。</remarks>
    internal static void PrepareConstructorOnlyShell(MainWindowViewModel viewModel)
    {
        PropertyInfo activation = typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.ShellActivationWorkflow),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("constructor-only activation boundary is unavailable.");
        activation.SetValue(viewModel,
            new ShellActivationWorkflowOwner(viewModel.StartupUpdateWorkflow,
                new ElevatedProcessWarningWorkflowOwner(() => false),
                () => Task.FromResult(new StartupInitializationResult(StartupInitializationOutcome.Succeeded, CompletionPublished: true))));
    }

    /// <summary>実親Windowの終端と設定復元を回収してconstructor-only操作を検証します。</summary>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<MainWindowViewModel, MainWindow> test,
        bool allowStartupUiInteraction = false,
        MainWindowLibraryReloadMenuTerminal? libraryReloadMenuTerminal = null,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null,
        MainWindowZeroNoteRecheckTerminal? zeroNoteRecheckTerminal = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        IUiDialogService? dialogService = null)
    {
        ArgumentNullException.ThrowIfNull(test);
        RunConstructorOnly(settings, (_, viewModel, window) => test(viewModel, window),
            allowStartupUiInteraction, libraryReloadMenuTerminal, regularLibraryTreeTerminal,
            maintenanceTreeTerminal, installTreeTerminal, zeroNoteRecheckTerminal,
            playlistWorkspaceDialogService, dialogService);
    }

    /// <summary>表示する実親Windowを、終了処理を所有する同じscopeで追跡します。</summary>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<TestWindowPresentationScope, MainWindowViewModel, MainWindow> test,
        bool allowStartupUiInteraction = false,
        MainWindowLibraryReloadMenuTerminal? libraryReloadMenuTerminal = null,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null,
        MainWindowZeroNoteRecheckTerminal? zeroNoteRecheckTerminal = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        IUiDialogService? dialogService = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(test);

        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var lifetime = new PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
                settingsEditSession: new NoOpSettingsEditSession(settings),
                uiScheduler: new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                playlistWorkspaceDialogService: playlistWorkspaceDialogService,
                dialogService: dialogService ?? UndeclaredDialogs.Create())
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                PrepareConstructorOnlyShell(viewModel);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(!allowStartupUiInteraction);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: libraryReloadMenuTerminal,
                    regularLibraryTreeTerminal: regularLibraryTreeTerminal,
                    maintenanceTreeTerminal: maintenanceTreeTerminal,
                    installTreeTerminal: installTreeTerminal,
                    zeroNoteRecheckTerminal: zeroNoteRecheckTerminal,
                    folderAutoRenameTerminal: null,
                    duplicateMaintenanceTerminal: null,
                    maintenanceRescanTerminal: null,
                    packageCatalogTerminal: null,
                    pendingInstallEstimationTerminal: null,
                    pendingInstallationTerminal: null,
                    installedLocationRepairTerminal: null,
                    pendingBulkMaintenanceTerminal: null,
                    mainChartCellEditTerminal: null));
                TestUiDispatcherHost.ProcessQueuedPresentation();
                test(windowTest, viewModel, window);
            });
        });
    }

    /// <summary>本番起動を開始せず、元Windowの資源とcompiled内容・Bindingを同じscopeの実Windowで描画します。</summary>
    internal static void ShowCompiledContent(TestWindowPresentationScope scope, MainWindowViewModel viewModel, MainWindow window)
    {
        object content = window.Content;
        window.Content = null;
        var host = new Window { Resources = window.Resources, Content = content, DataContext = viewModel, Width = 1000, Height = 700 };
        scope.ShowAndWaitForContentRendered(host);
    }

    /// <summary>実 Window の terminal 完了を観測し、共有 test Application の終了を抑止する。</summary>
    internal sealed class PresentationApplicationLifetime : IApplicationLifetimePort
    {
        /// <summary>OS終了を実行せず、shellが実終端で発行する終了要求を既存lifetimeへ通知します。</summary>
        internal TaskCompletionSource ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsFirstStartup => false;

        public void CompleteFirstStartup() { }

        public void MarkCoordinatedShutdownStarted(string reason) { }

        public void ExitForUnrestoredDestination(System.Exception failure)
        => throw new System.InvalidOperationException("Unexpected unrestored destination exit.", failure);

        /// <summary>テストが所有する一時入力は各fixtureで回収し、アプリ全体の回収を行いません。</summary>
        public Task CompleteTemporaryDirectoriesAsync(Action<string, Exception> logWarning) => Task.CompletedTask;

        public void RequestShutdown() => ShutdownRequested.TrySetResult();

        public Task RestartApplicationAsync() => Task.CompletedTask;
    }
}
