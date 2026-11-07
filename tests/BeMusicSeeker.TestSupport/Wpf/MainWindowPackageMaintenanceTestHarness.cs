using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.TestSupport.Wpf;

internal static class MainWindowPackageMaintenanceTestHarness
{
    /// <summary>実shutdown/Closedと設定復元を回収し、表示する親Windowは同じ外側scopeへ追跡します。</summary>
    /// <param name="prepareWindowForPresentation">親Windowを表示するfixtureだけが、表示前に所有するscopeへ準備を接続します。</param>
    /// <param name="installTreeTerminal">保留ツリーの実選択と表示反映を、導入表示構築から分けて観測する場合の終端です。</param>
    /// <param name="uiScheduler">UI配送と開始済みTaskを観測するスケジューラーです。省略時は既存の配送処理を使います。</param>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<MainWindowViewModel, MainWindow> test,
        MainWindowFolderAutoRenameTerminal? folderAutoRenameTerminal = null,
        MainWindowDuplicateMaintenanceTerminal? duplicateMaintenanceTerminal = null,
        MainWindowMaintenanceRescanTerminal? maintenanceRescanTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowPackageCatalogTerminal? packageCatalogTerminal = null,
        MainWindowPendingInstallEstimationTerminal? pendingInstallEstimationTerminal = null,
        MainWindowPendingInstallationTerminal? pendingInstallationTerminal = null,
        MainWindowInstalledLocationRepairTerminal? installedLocationRepairTerminal = null,
        MainWindowPendingBulkMaintenanceTerminal? pendingBulkMaintenanceTerminal = null,
        MainWindowMainChartCellEditTerminal? mainChartCellEditTerminal = null,
        MainWindowSelectedChartContextMenuTerminals? selectedChartContextMenuTerminals = null,
        MainWindowPlaybackTerminal? playbackTerminal = null,
        MainWindowPlaylistWorkspaceTerminals? playlistWorkspaceTerminals = null,
        MainWindowPendingPackageMutationViewTerminal? pendingPackageMutationViewTerminal = null,
        Action<MainWindowViewModel>? prepareViewModel = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        Action<TestWindowPresentationScope, MainWindow>? prepareWindowForPresentation = null,
        Func<TestWindowPresentationScope, Window, ChartPropertiesWindowManager>? chartPropertiesManagerFactory = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null, IUiScheduler? uiScheduler = null)
        => RunConstructorOnly(settings, (_, viewModel, window) => test(viewModel, window),
            folderAutoRenameTerminal, duplicateMaintenanceTerminal, maintenanceRescanTerminal, maintenanceTreeTerminal, packageCatalogTerminal, pendingInstallEstimationTerminal, pendingInstallationTerminal, installedLocationRepairTerminal, pendingBulkMaintenanceTerminal, mainChartCellEditTerminal, selectedChartContextMenuTerminals, playbackTerminal, playlistWorkspaceTerminals, pendingPackageMutationViewTerminal, prepareViewModel, playlistWorkspaceDialogService, prepareWindowForPresentation, chartPropertiesManagerFactory, installTreeTerminal, uiScheduler);

    /// <summary>親子Windowに同じ表示scopeを渡し、実MainWindow寿命を共通補助で回収します。</summary>
    /// <param name="installTreeTerminal">保留ツリーの実選択と表示反映を、導入表示構築から分けて観測する場合の終端です。</param>
    /// <param name="uiScheduler">UI配送と開始済みTaskを観測するスケジューラーです。省略時は既存の配送処理を使います。</param>
    /// <param name="regularLibraryTreeTerminal">通常ツリーの選択から一覧所有者への配送を、一覧の構築から分けて観測する場合の終端です。</param>
    internal static void RunConstructorOnly(
        Settings settings,
        Action<TestWindowPresentationScope, MainWindowViewModel, MainWindow> test,
        MainWindowFolderAutoRenameTerminal? folderAutoRenameTerminal = null,
        MainWindowDuplicateMaintenanceTerminal? duplicateMaintenanceTerminal = null,
        MainWindowMaintenanceRescanTerminal? maintenanceRescanTerminal = null,
        MainWindowMaintenanceTreeTerminal? maintenanceTreeTerminal = null,
        MainWindowPackageCatalogTerminal? packageCatalogTerminal = null,
        MainWindowPendingInstallEstimationTerminal? pendingInstallEstimationTerminal = null,
        MainWindowPendingInstallationTerminal? pendingInstallationTerminal = null,
        MainWindowInstalledLocationRepairTerminal? installedLocationRepairTerminal = null,
        MainWindowPendingBulkMaintenanceTerminal? pendingBulkMaintenanceTerminal = null,
        MainWindowMainChartCellEditTerminal? mainChartCellEditTerminal = null,
        MainWindowSelectedChartContextMenuTerminals? selectedChartContextMenuTerminals = null,
        MainWindowPlaybackTerminal? playbackTerminal = null,
        MainWindowPlaylistWorkspaceTerminals? playlistWorkspaceTerminals = null,
        MainWindowPendingPackageMutationViewTerminal? pendingPackageMutationViewTerminal = null,
        Action<MainWindowViewModel>? prepareViewModel = null,
        IUiDialogService? playlistWorkspaceDialogService = null,
        Action<TestWindowPresentationScope, MainWindow>? prepareWindowForPresentation = null,
        Func<TestWindowPresentationScope, Window, ChartPropertiesWindowManager>? chartPropertiesManagerFactory = null,
        MainWindowInstallTreeTerminal? installTreeTerminal = null, IUiScheduler? uiScheduler = null,
        MainWindowRegularLibraryTreeTerminal? regularLibraryTreeTerminal = null)
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var lifetime = new MainWindowPresentationTestHarness.PresentationApplicationLifetime();
            MainWindowViewModel viewModel = new ApplicationComposition(
                runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
                settingsEditSession: new NoOpSettingsEditSession(settings),
                uiScheduler: uiScheduler ?? new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                applicationLifetime: lifetime,
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(),
                playlistWorkspaceDialogService: playlistWorkspaceDialogService)
                .CreateMainWindowViewModelForTest();
            var ownership = new MainWindowTestLifetime(viewModel, lifetime.ShutdownRequested.Task);
            ownership.Run(() =>
            {
                MainWindowPresentationTestHarness.PrepareConstructorOnlyShell(viewModel);
                viewModel.StartupUpdateWorkflow.NotifyClosing();
                viewModel.ProgressHub.StartupProgress.SetStartupUiInteractionBlocked(false);
                prepareViewModel?.Invoke(viewModel);
                MainWindow window = ownership.CreateWindow(() => new MainWindow(
                    viewModel,
                    settingsWindowCreated: null,
                    libraryReloadMenuTerminal: null,
                    regularLibraryTreeTerminal: regularLibraryTreeTerminal,
                    maintenanceTreeTerminal: maintenanceTreeTerminal,
                    installTreeTerminal: installTreeTerminal,
                    zeroNoteRecheckTerminal: null,
                    columnResetTerminal: null,

                    folderAutoRenameTerminal,
                    duplicateMaintenanceTerminal,
                    maintenanceRescanTerminal,
                    packageCatalogTerminal,
                    pendingInstallEstimationTerminal,
                    pendingInstallationTerminal,
                    installedLocationRepairTerminal,
                    pendingBulkMaintenanceTerminal,
                    mainChartCellEditTerminal,
                    selectedChartContextMenuTerminals,
                    playbackTerminal,
                    playlistWorkspaceTerminals,
                    pendingPackageMutationViewTerminal: pendingPackageMutationViewTerminal,
                    playlistWorkspaceDialogService: playlistWorkspaceDialogService,
                    chartPropertiesManagerFactory: chartPropertiesManagerFactory == null ? null : owner => chartPropertiesManagerFactory(windowTest, owner)));
                prepareWindowForPresentation?.Invoke(windowTest, window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                test(windowTest, viewModel, window);
            });
        });
    }
}
