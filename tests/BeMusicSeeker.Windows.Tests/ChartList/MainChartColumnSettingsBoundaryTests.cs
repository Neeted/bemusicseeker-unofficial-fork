using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.MainChartColumnSettingsTestDoubles;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class MainChartColumnSettingsBoundaryTests
{
    /// <summary>実compositionの一覧読み込みとsummary resetが同じ列保存主体へ到達し、reset一回の結果をworkspaceへ反映します。</summary>
    [TestMethod]
    public async Task CompositionSharesColumnSettingsStoreAcrossMainTableAndSummaryCoordinator()
    {
        var store = new FakeMainChartColumnSettingsStore();
        var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var composition = new ApplicationComposition(
            runtimeOptions: MainWindowViewModelTestFactory.CreateRuntimeOptions(),
            () => new BmsLibraryOptionsSnapshot(),
            mainChartColumnSettingsStore: store,
            settingsEditSession: new NoOpSettingsEditSession(TestSettingsFactory.CreateIsolatedSettings()),
            playlistWorkspaceDialogService: dialogs,
            uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
        MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();

        MainChartListColumnSelection selection = viewModel.MainChartList.LoadColumnSetting(
            MainViewUpdateMode.FolderFilterSelected,
            MainViewUpdateMode.FolderFilterSelected);
        Assert.AreSame(store.MainColumns, selection.ColumnsSettings);
        Assert.AreSame(store.PlaylistSummaryColumns, selection.PlaylistSummaryColumnsSettings);

        await viewModel.PlaylistWorkspace.ResetPlaylistSummaryColumnsToDefaultAsync();

        Assert.AreSame(store.PlaylistSummaryColumns, viewModel.PlaylistWorkspace.PlaylistSummaryColumnsSettings);
        Assert.AreEqual(1, store.ResetPlaylistSummaryCallCount);
    }

}
