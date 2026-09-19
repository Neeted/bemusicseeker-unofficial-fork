using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainChartColumnSettingsBoundaryTests
{
    [TestMethod]
    public async Task CompositionSharesColumnSettingsStoreAcrossMainTableAndSummaryCoordinator()
    {
        var store = new FakeMainChartColumnSettingsStore();
        var dialogs = new PlaylistWorkspaceTestPorts.PlaylistWorkspaceDialogService
        {
            ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
        };
        var composition = new ApplicationComposition(
            () => new BmsLibraryOptionsSnapshot(),
            mainChartColumnSettingsStore: store,
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

    [TestMethod]
    public void MainChartListUsesStoreForInitializationAndSummaryCompatibility()
    {
        var store = new FakeMainChartColumnSettingsStore();
        var mainChartList = new MainChartListViewModel(action => action(), _ => { }, store);

        MainChartListColumnSelection selection = mainChartList.LoadColumnSetting(
            MainViewUpdateMode.PlayHistorySelected,
            MainViewUpdateMode.PlayHistorySelected,
            isInit: true);

        Assert.AreSame(store.MainColumns, selection.ColumnsSettings);
        Assert.AreSame(store.PlaylistSummaryColumns, selection.PlaylistSummaryColumnsSettings);
        Assert.IsTrue(store.LastReset);
        Assert.IsTrue(store.LastEnsurePlaylistSummary);
    }

    private sealed class FakeMainChartColumnSettingsStore : IMainChartColumnSettingsStore
    {
        internal CustomTableColumnSettings MainColumns { get; } =
            new(CustomTableColumnSettings.ViewKind.STANDARD);

        internal PlaylistSummaryColumnSettings PlaylistSummaryColumns { get; private set; } =
            new();

        internal bool LastReset { get; private set; }

        internal bool LastEnsurePlaylistSummary { get; private set; }

        internal int ResetPlaylistSummaryCallCount { get; private set; }

        public CustomTableColumnSettings GetMain(CustomTableColumnSettings.ViewKind viewKind, bool reset)
        {
            LastReset = reset;
            return MainColumns;
        }

        public bool IsMainReady(CustomTableColumnSettings.ViewKind viewKind)
        {
            return MainColumns != null;
        }

        public PlaylistSummaryColumnSettings GetPlaylistSummary(bool ensureCompatibility)
        {
            LastEnsurePlaylistSummary = ensureCompatibility;
            return PlaylistSummaryColumns;
        }

        public PlaylistSummaryColumnSettings ResetPlaylistSummary()
        {
            ResetPlaylistSummaryCallCount++;
            PlaylistSummaryColumns = new PlaylistSummaryColumnSettings();
            return PlaylistSummaryColumns;
        }
    }
}
