using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowContextMenuResourceTests
{
    [TestMethod]
    public void MainColumnReset_RoutesThroughRegularChartListOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string playlistWorkspaceSource = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string regularOwnerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs");

        StringAssert.Contains(mainWindowSource, "mainWindowViewModel.RegularChartList.ResetCurrentColumnPresentation();");
        Assert.IsFalse(rootViewModelSource.Contains("public void LoadColumnSetting()"));
        Assert.IsFalse(rootViewModelSource.Contains("LoadColumnSetting();"));
        StringAssert.Contains(rootViewModelSource, "regularChartListOwner.InitializeColumnPresentation(treeViewFilterTypeSelected);");
        Assert.IsFalse(playlistWorkspaceSource.Contains("CommitMainTableColumnSetting"));
        StringAssert.Contains(regularOwnerSource, "InitializeColumnPresentation(MainViewUpdateMode currentTreeMode)");
        StringAssert.Contains(regularOwnerSource, "ResetCurrentColumnPresentation()");
        StringAssert.Contains(regularOwnerSource, "CommitColumnPresentationWithoutNotification");
        StringAssert.Contains(regularOwnerSource, "PublishColumnPresentation(commit)");
    }

    [TestMethod]
    public void ChartFilterInput_BindsToChildOwnerAndUsesRequestSnapshots()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string viewModel = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string rootViewModel = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string regularOwner = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs"));
        string playlistRequestOwner = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.DetailRequests.cs"));

        StringAssert.Contains(xaml, "{Binding ChartFilters.ModeFilter");
        Assert.AreEqual(5, CountOccurrences(xaml, "{Binding ChartFilters.ModeFilter, Source={StaticResource vm}"));
        StringAssert.Contains(xaml, "{Binding ChartFilters.KeywordFilter");
        StringAssert.Contains(viewModel, "public ChartListFilterViewModel ChartFilters");
        Assert.IsFalse(rootViewModel.Contains("public ModeFilterType ModeFilter"));
        Assert.IsFalse(rootViewModel.Contains("public string KeywordFilter"));
        Assert.IsFalse(rootViewModel.Contains("ModeFilterType"));
        Assert.IsFalse(rootViewModel.Contains("private string _KeywordFilter"));
        StringAssert.Contains(viewModel, "ChartFilters.UpdateKeywordSearchContext(");
        StringAssert.Contains(viewModel, "PlaylistWorkspace.RequestDetailRefresh(");
        StringAssert.Contains(viewModel, "ShouldUsePlaylistBuildCoalescingWindow(route.Mode, route.RequestedMode)");
        StringAssert.Contains(viewModel, "CapturePlaylistOpenReadinessSnapshot()");
        Assert.IsFalse(viewModel.Contains("CreatePlaylistDetailRefreshInput("));
        Assert.IsFalse(viewModel.Contains("PlaylistWorkspace.CapturePlaylistDetailSelection(out long selectionRevision)"));
        Assert.IsFalse(viewModel.Contains("PlaylistWorkspace.CapturePlaylistDetailFilterSnapshot()"));
        Assert.IsFalse(viewModel.Contains("parameter as PlaylistDetailSelection"));
        Assert.IsFalse(viewModel.Contains("ChartListFilterSnapshot filters = ChartFilters.CaptureSnapshot();\r\n        ChartListSortParameters sortParameters = PlaylistWorkspace.CapturePlaylistDetailSortParameters();"));
        StringAssert.Contains(playlistRequestOwner, "PlaylistDetailSelection selection = playlistDetailSelection;");
        StringAssert.Contains(playlistRequestOwner, "ChartListSortParameters currentSort = CapturePlaylistDetailSortParameters();");
        StringAssert.Contains(playlistRequestOwner, "Filters = new ChartListFilterSnapshot(keywordFilter, modeFilter)");
        Assert.IsFalse(regularOwner.Contains("RegularChartModeFilter"));
        Assert.IsFalse(regularOwner.Contains("SetFilters("));
        StringAssert.Contains(regularOwner, "ChartListFilterSnapshot Filters");
    }

    [TestMethod]
    public void LibraryFolderTree_BindsToChildOwnerAndRemovesRootRelay()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs"));

        StringAssert.Contains(xaml, "ItemsSource=\"{Binding LibraryFolderTree.BMSParentFolderList}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding LibraryFolderTree.BMSParentFolderList, Source={StaticResource vm}}\"");
        Assert.AreEqual(2, CountOccurrences(xaml, "DataContext.LibraryFolderTree.IsWriteLockHeldInitializeBMSFiles"));
        StringAssert.Contains(mainWindowSource, "viewModel.LibraryFolderTree.BMSParentFolderList?.FirstOrDefault()");
        StringAssert.Contains(rootViewModelSource, "public LibraryFolderTreeViewModel LibraryFolderTree");
        Assert.IsTrue(
            rootViewModelSource.IndexOf("files.SearchTargets.AddRange(libraryProfile.SearchRoots);", StringComparison.Ordinal)
                < rootViewModelSource.IndexOf("LibraryFolderTree.AttachLibrary(files);", StringComparison.Ordinal));
        Assert.IsFalse(rootViewModelSource.Contains("NotifyBmsParentFolderListChanged"));
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsWriteLockHeldInitializeBMSFiles\r\n"));
    }

    [TestMethod]
    public void InstallTree_BindsToChildOwnerAndRemovesRootRelay()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");

        StringAssert.Contains(xaml, "ItemsSource=\"{Binding InstallTree.ChartPackagesInstalled}\"");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding InstallTree.ChartPackagesPending}\"");
        Assert.AreEqual(2, CountOccurrences(xaml, "DataContext.InstallTree.IsWriteLockHeldPendingInstallCharts"));
        StringAssert.Contains(rootViewModelSource, "public InstallTreeViewModel InstallTree");
        StringAssert.Contains(rootViewModelSource, "InstallTree.ApplyPresentation(");
        Assert.IsFalse(rootViewModelSource.Contains("listenerForBMSLibraryChartPackagesInstalledCollection"));
        Assert.IsFalse(rootViewModelSource.Contains("listenerForBMSLibraryChartPackagesPendingCollection"));
        Assert.IsFalse(rootViewModelSource.Contains("HandleChartPackagesInstalledCollectionChanged"));
        Assert.IsFalse(rootViewModelSource.Contains("HandleChartPackagesPendingCollectionChanged"));
        Assert.IsFalse(rootViewModelSource.Contains("RebindChartPackagesInstalledCollectionListener"));
        Assert.IsFalse(rootViewModelSource.Contains("RebindChartPackagesPendingCollectionListener"));
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsWriteLockHeldPendingInstallCharts"));
        Assert.IsFalse(rootViewModelSource.Contains("public ObservableCollection<ChartPackage> ChartPackagesInstalled"));
        Assert.IsFalse(rootViewModelSource.Contains("public ObservableCollection<ChartPackage> ChartPackagesPending"));
    }

    [TestMethod]
    public void MaintenanceTree_BindsToChildOwnerAndRemovesRootRelay()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");

        StringAssert.Contains(xaml, "ItemsSource=\"{Binding MaintenanceTree.DuplicateChartGroups}\"");
        Assert.AreEqual(2, CountOccurrences(xaml, "DataContext.MaintenanceTree.IsWriteLockHeldInitializdBMSFilesHealthStatus"));
        Assert.AreEqual(2, CountOccurrences(xaml, "DataContext.MaintenanceTree.IsWriteLockHeldInitializeBMSFilesEncodingInfo"));
        Assert.AreEqual(2, CountOccurrences(xaml, "DataContext.MaintenanceTree.IsWriteLockHeldInitializeBMSFilesZeroNote"));
        Assert.AreEqual(2, CountOccurrences(xaml, "DataContext.MaintenanceTree.IsWriteLockHeldDuplicateChartGroups"));
        StringAssert.Contains(mainWindowSource, "viewModel.MaintenanceTree.DuplicateChartGroups");
        StringAssert.Contains(mainWindowSource, "nameof(MaintenanceTreeViewModel.DuplicateChartGroups)");
        StringAssert.Contains(rootViewModelSource, "public MaintenanceTreeViewModel MaintenanceTree");
        StringAssert.Contains(rootViewModelSource, "MaintenanceTree.ApplyDuplicateGroupsPresentation()");
        Assert.IsFalse(rootViewModelSource.Contains("listenerForBMSLibrary.RegisterHandler(() => files.DuplicateChartGroups"));
        Assert.IsFalse(rootViewModelSource.Contains("listenerForBMSLibrary.RegisterHandler(() => files.DuplicateChartGroupsInvalidationVersion"));
        Assert.IsFalse(rootViewModelSource.Contains("RaisePropertyChanged(() => IsWriteLockHeldInitializdBMSFilesHealthStatus"));
        Assert.IsFalse(rootViewModelSource.Contains("RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFilesEncodingInfo"));
        Assert.IsFalse(rootViewModelSource.Contains("RaisePropertyChanged(() => IsWriteLockHeldInitializeBMSFilesZeroNote"));
        Assert.IsFalse(rootViewModelSource.Contains("RaisePropertyChanged(() => IsWriteLockHeldDuplicateChartGroups"));
        Assert.IsFalse(rootViewModelSource.Contains("public List<DuplicateGroup> DuplicateChartGroups"));
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsWriteLockHeldInitializdBMSFilesHealthStatus"));
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsWriteLockHeldInitializeBMSFilesEncodingInfo"));
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsWriteLockHeldInitializeBMSFilesZeroNote"));
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsWriteLockHeldDuplicateChartGroups"));
    }

    [TestMethod]
    public void RegularLibraryTreeNavigation_RoutesThroughRegularChartListOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string ownerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string directoryRoute = ExtractBetween(
            mainWindowSource,
            "private void directoryFolderSelect",
            "private void playHistoryPeriodSelect");
        string artistRoute = ExtractBetween(
            mainWindowSource,
            "private void artistFolderSelect",
            "private void rootFolderSelect");
        string rootRoute = ExtractBetween(
            mainWindowSource,
            "private void rootFolderSelect",
            "private List<PlaylistSummaryRow> getSelectedPlaylistSummaryRows");

        StringAssert.Contains(directoryRoute, "ShouldBlockStartupUiInteraction(\"tree_directory_folder_select\")");
        StringAssert.Contains(directoryRoute, "viewModel.RegularChartList.NavigateTree(");
        StringAssert.Contains(directoryRoute, "RegularChartFolderFilterKind.Directory");
        StringAssert.Contains(directoryRoute, "treeViewItem.Header.ToString()");
        StringAssert.Contains(artistRoute, "ShouldBlockStartupUiInteraction(\"tree_artist_folder_select\")");
        StringAssert.Contains(artistRoute, "viewModel.RegularChartList.NavigateTree(");
        StringAssert.Contains(artistRoute, "RegularChartFolderFilterKind.Artist");
        StringAssert.Contains(rootRoute, "ShouldBlockStartupUiInteraction(\"tree_root_folder_select\")");
        StringAssert.Contains(rootRoute, "viewModel.RegularChartList.NavigateTree(filterKind: null)");
        StringAssert.Contains(ownerSource, "playlistWorkspace.RequestPlaylistSummaryMode(enabled: false)");
        StringAssert.Contains(ownerSource, "TreeNavigationPresentationRequested");
        Assert.IsFalse(rootViewModelSource.Contains("ExecFolderFilter"));
        Assert.IsFalse(rootViewModelSource.Contains("FolderFilterType"));
        Assert.IsFalse(rootViewModelSource.Contains("SetNormalLibraryTreeFilter"));
        Assert.IsFalse(rootViewModelSource.Contains("RaisePropertyChanged(\"FolderFilter\")"));
    }

    [TestMethod]
    public void MaintenanceTreeNavigation_RoutesThroughRegularChartListOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string ownerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string maintenanceRoutes = ExtractBetween(
            mainWindowSource,
            "private async void fullScanCheckFolderSelect",
            "private async void treeViewZeroNoteContextMenuItemRecheckClick");

        string[] modes =
        [
            "FileMissingFilterSelected",
            "FullScanAllChartsFilterSelected",
            "FileMissingIgnoredFilterSelected",
            "DuplicateFilterSelected",
            "GarbledFilterSelected",
            "GarbleFixedFilterSelected",
            "UnregisteredFilterSelected",
            "ZeroNoteFilterSelected",
            "ChartInfoParseErrorFilterSelected"
        ];
        foreach (string mode in modes)
        {
            StringAssert.Contains(maintenanceRoutes, "NavigateMaintenanceAsync(MainViewUpdateMode." + mode);
        }
        StringAssert.Contains(maintenanceRoutes, "ShouldBlockStartupUiInteraction(");
        StringAssert.Contains(maintenanceRoutes, "e.Handled = true;");
        StringAssert.Contains(maintenanceRoutes, "treeRoot.IsExpanded = true;");
        StringAssert.Contains(maintenanceRoutes, "treeViewItem.DataContext is DuplicateGroup");
        StringAssert.Contains(ownerSource, "EnsureDuplicateChartGroupsReady(library, reason)");
        StringAssert.Contains(ownerSource, "MaintenanceNavigationPresentationRequested");
        Assert.IsFalse(rootViewModelSource.Contains("ExecMaintenanceFilter"));
        Assert.IsFalse(rootViewModelSource.Contains("MaintenanceFilterType"));
        Assert.IsFalse(rootViewModelSource.Contains("duplicateChartGroupsRefreshLock"));
        Assert.IsFalse(rootViewModelSource.Contains("duplicateChartGroupsRefreshRunning"));
    }

    [TestMethod]
    public void ZeroNoteRecheck_RoutesThroughMaintenanceWorkflowOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string route = ExtractBetween(
            mainWindowSource,
            "private async void treeViewZeroNoteContextMenuItemRecheckClick",
            "private async void newlyInstalledFolderSelect");

        StringAssert.Contains(route, "viewModel.ZeroNoteMaintenance");
        StringAssert.Contains(route, ".RecheckAsync()");
        Assert.IsFalse(route.Contains("Task.Run"));
        Assert.IsFalse(rootViewModelSource.Contains("void RecheckZeroNoteWarnings("));
        Assert.IsFalse(rootViewModelSource.Contains("files.RecheckZeroNoteWarnings()"));
    }

    [TestMethod]
    public void InstallPackageNavigation_RoutesThroughRegularChartListOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string ownerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string navigationRoutes = ExtractBetween(
            mainWindowSource,
            "private async void newlyInstalledFolderSelect",
            "private void treeViewPlaylistRootContextMenuOpend");

        StringAssert.Contains(navigationRoutes, "ShouldBlockStartupUiInteraction(\"tree_newly_installed_select\")");
        StringAssert.Contains(navigationRoutes, "ShouldBlockStartupUiInteraction(\"tree_pending_install_select\")");
        StringAssert.Contains(navigationRoutes, "NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected");
        StringAssert.Contains(navigationRoutes, "NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected");
        StringAssert.Contains(navigationRoutes, "treeViewItem.DataContext is ChartPackage package");
        StringAssert.Contains(navigationRoutes, "treeRoot.IsExpanded = true;");
        StringAssert.Contains(ownerSource, "InstallNavigationPresentationRequested");
        StringAssert.Contains(mainWindowSource, ".NavigateInstallAsync(MainViewUpdateMode.PendingInstallFolderSelected)");
        StringAssert.Contains(mainWindowSource, ".NavigateInstallAsync(MainViewUpdateMode.NewlyInstalledFolderSelected)");
        Assert.IsFalse(mainWindowSource.Contains("ExecInstallFilter"));
        Assert.IsFalse(rootViewModelSource.Contains("InstallFilterType"));
        Assert.IsFalse(rootViewModelSource.Contains("ExecInstallFilter"));
    }

    [TestMethod]
    public void DeleteContextMenuItems_UseSpecificDeleteResourceKeys()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteEntry\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Remove_playlist_entry, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteFile\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_file, Mode=OneWay"));
    }

    [TestMethod]
    public void PlaylistOverwriteLevel_RoutesThroughWorkflowOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string levelOverwriteSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistTableLevelOverwriteWorkflowOwner.cs");
        string route = ExtractBetween(
            mainWindowSource,
            "private async void treeViewPlaylistTableContextMenuItemOverwriteLevelClick",
            "private async void treeViewPlaylistTableContextMenuItemRemoveTableClick");

        StringAssert.Contains(route, "PlaylistTableLevelOverwriteWorkflow");
        StringAssert.Contains(route, "OverwriteAsync(bmsTable)");
        Assert.AreEqual(-1, route.IndexOf("UiDialogRoute.ShowMessageBox", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("bmseeker:table.recommended", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("ConfirmPlaylistOverwriteLevel", StringComparison.Ordinal));
        StringAssert.Contains(levelOverwriteSource, "internal async Task OverwriteAsync(BMSTable table)");
        StringAssert.Contains(levelOverwriteSource, "ReplaceBmsFileLevelByTableEntryLevel(table)");
        Assert.AreEqual(-1, rootViewModelSource.IndexOf("ReplaceBMSFileLevelByTableEntryLevel(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PlaylistTableJsonExportRoutesThroughWorkspaceOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string route = ExtractBetween(
            mainWindowSource,
            "private async void treeViewPlaylistTableContextMenuItemExportTableClick",
            "private async void treeViewPlaylistTableContextMenuItemOverwriteLevelClick");
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string workspaceSource = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string selectionSpec = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "devdocs",
            "spec",
            "file-selection-dialogs.md"));

        StringAssert.Contains(route, "ExportPlaylistTableAsync(bmsTable)");
        StringAssert.Contains(route, "LoggingAndPropagate(\"treeViewPlaylistTableContextMenuItemExportTableClick\")");
        Assert.AreEqual(-1, route.IndexOf("new UiDialogCoordinator", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("UiSaveFilePickerRequest", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("ThrowIfPickerFailed", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("Task.Run", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("ExportBMSTable(", StringComparison.Ordinal));
        StringAssert.Contains(workspaceSource, "internal async Task ExportPlaylistTableAsync(BMSTable bmsTable)");
        Assert.AreEqual(-1, workspaceSource.IndexOf("internal Task ExportPlaylistTableAsync(BMSTable bmsTable, string fileNameHeader, string fileNameData)", StringComparison.Ordinal));
        StringAssert.Contains(workspaceSource, "new UiSaveFilePickerRequest(");
        StringAssert.Contains(workspaceSource, "HeaderToJson()");
        StringAssert.Contains(workspaceSource, "DataToJson()");
        Assert.AreEqual(-1, rootViewModelSource.IndexOf("ExportBMSTable(", StringComparison.Ordinal));
        StringAssert.Contains(selectionSpec, "PlaylistWorkspace.ExportPlaylistTableAsync(bmsTable)");
    }

    [TestMethod]
    public void PlaylistTableRemoval_RoutesThroughWorkflowOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string route = ExtractBetween(
            mainWindowSource,
            "private async void treeViewPlaylistTableContextMenuItemRemoveTableClick",
            "private async void treeViewPlaylistTableCcontextMenuItemOpenPropertyDialogClick");

        StringAssert.Contains(route, "PlaylistRemovalWorkflow");
        StringAssert.Contains(route, "RemoveTreeTableAsync(");
        StringAssert.Contains(route, "SelectNextSiblingOrRoot(");
        Assert.AreEqual(-1, route.IndexOf("ConfirmPlaylistTableRemoval", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("RemoveTableAsync", StringComparison.Ordinal));
        Assert.AreEqual(-1, route.IndexOf("UiDialogRoute.ShowMessageBox", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InstallPackageTreeHeaders_BindToPackageDisplayTitle()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(4, CountOccurrences(xaml, "DisplayTitle"));
        Assert.AreEqual(-1, xaml.IndexOf("P={Binding ChartFiles}", StringComparison.Ordinal));
        Assert.AreEqual(-1, xaml.IndexOf("ChartFiles[", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PlayHistoryTree_ExposesExpectedPeriodNodes()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string playHistoryTree = ExtractBetween(xaml, "Name=\"treeViewItemPlayHistory\"", "</TreeView>");

        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"treeViewItemPlayHistory\""));
        Assert.IsTrue(xaml.IndexOf("Path=Resources.Maintenance", StringComparison.Ordinal) < xaml.IndexOf("Name=\"treeViewItemPlayHistory\"", StringComparison.Ordinal));
        Assert.AreEqual(1, CountOccurrences(playHistoryTree, "Selected=\"playHistoryPeriodSelect\""));
        StringAssert.Contains(playHistoryTree, "<EventSetter Event=\"Selected\" Handler=\"playHistoryPeriodSelect\" />");
        foreach (string tag in new[] { "All", "Today", "Yesterday", "Recent7Days", "Recent30Days", "Diagnostics" })
        {
            Assert.AreEqual(1, CountOccurrences(playHistoryTree, "Tag=\"" + tag + "\""), tag);
        }
        foreach (string resource in new[]
        {
            "Play_history_tree_root",
            "Play_history_period_all",
            "Play_history_period_today",
            "Play_history_period_yesterday",
            "Play_history_period_recent_7_days",
            "Play_history_period_recent_30_days",
            "Play_history_period_archive",
            "Play_history_period_diagnostics"
        })
        {
            StringAssert.Contains(playHistoryTree, "Path=Resources." + resource + ", Mode=OneWay", resource);
            Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.ResourceManager.GetString(resource)), resource);
        }
        StringAssert.Contains(playHistoryTree, "ItemsSource=\"{Binding PlayHistory.ArchivePeriodTree}\"");
        StringAssert.Contains(playHistoryTree, "<HierarchicalDataTemplate DataType=\"{x:Type vm:PlayHistoryPeriodTreeItem}\" ItemsSource=\"{Binding Children}\" ItemContainerStyle=\"{StaticResource styleTreeViewItemPlayHistoryPeriodContainer}\">");
        StringAssert.Contains(playHistoryTree, "<DataTemplate x:Key=\"templateTreeViewItemHeaderPlayHistoryPeriod\">");
        StringAssert.Contains(playHistoryTree, "<TreeViewItem Focusable=\"False\" HeaderTemplate=\"{StaticResource templateTreeViewItemHeaderPlayHistoryPeriod}\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Play_history_period_archive, Mode=OneWay}\" ItemsSource=\"{Binding PlayHistory.ArchivePeriodTree}\" ItemContainerStyle=\"{StaticResource styleTreeViewItemPlayHistoryPeriodContainer}\" />");
    }

    [TestMethod]
    public void PlayHistoryMainTable_UsesBoundDragKindAndRejectsChartContextMenu()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainTable = ExtractBetween(xaml, "<v:CustomTableView x:Name=\"customTableView\"", "<v:CustomTableView x:Name=\"customTablePlaylistSummary\"");
        string playHistoryContextMenu = ExtractBetween(xaml, "<ContextMenu x:Key=\"playHistoryContextMenu\"", "<ContextMenu x:Key=\"treeViewPlaylistRootContextMenu\"");
        PlayHistoryRow playHistoryRow = CreateUnresolvedPlayHistoryRow();
        PlayHistoryRow resolvedPlayHistoryRow = CreateResolvedPlayHistoryRow();
        LibraryChartRow libraryRow = LibraryChartRow.FromBmsFile(CreateContextMenuBmsFile());
        PlayHistoryWorkflowOwner playHistoryOwner = new();

        StringAssert.Contains(mainTable, "DataContext=\"{Binding MainChartList}\"");
        StringAssert.Contains(mainTable, "ItemsSource=\"{Binding Rows, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SelectedIndex=\"{Binding SelectedIndex, Mode=TwoWay}\"");
        StringAssert.Contains(mainTable, "RowDragKind=\"{Binding RowDragKind, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "ColumnsSettings=\"{Binding ColumnsSettings, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SortColumnName=\"{Binding SortParameters.ColumnsName, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SortDirection=\"{Binding SortParameters.Direction, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "Visibility=\"{Binding DataContext.PlaylistWorkspace.IsPlaylistSummaryMode, ElementName=window, Converter={StaticResource notBooleanToVisibilityCollapsedConverter}}\"");
        StringAssert.Contains(xaml, "IsChecked=\"{Binding MainChartList.ColumnsSettings.Title.Visibility, Source={StaticResource vm}");
        Assert.AreEqual(69, CountOccurrences(xaml, "IsChecked=\"{Binding MainChartList.ColumnsSettings."));
        Assert.IsFalse(xaml.Contains("ColumnsSettingsChartRowsView"));
        Assert.IsFalse(xaml.Contains("ChartRowsViewToSummaryTextConverter"));
        Assert.IsFalse(mainTable.Contains("ItemsSource=\"{Binding ChartRowsView"));
        Assert.IsFalse(mainTable.Contains("SelectedIndex=\"{Binding SelectedIndexChartRowsView"));
        Assert.IsFalse(mainTable.Contains("RowDragKind=\"{Binding ChartRowsViewRowDragKind"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Text=\"{Binding MainChartList.SummaryText}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Text=\"{Binding PlaylistWorkspace.PlaylistSummaryText}\""));
        StringAssert.Contains(xaml, "x:Name=\"customTablePlaylistSummary\"");
        StringAssert.Contains(xaml, "DataContext=\"{Binding PlaylistWorkspace}\"");
        StringAssert.Contains(xaml, "IsChecked=\"{Binding PlaylistWorkspace.PlaylistSummaryColumnsSettings.PlaylistId.Visibility, Source={StaticResource vm}");
        Assert.IsFalse(xaml.Contains("IsChecked=\"{Binding PlaylistSummaryColumnsSettings."));
        StringAssert.Contains(xaml, "Visibility=\"{Binding PlaylistWorkspace.ColumnSettingsVisibilityForPlaylist, Source={StaticResource vm}}\"");
        Assert.IsFalse(xaml.Contains("Visibility=\"{Binding ColumnSettingsVisibilityForPlaylist, Source={StaticResource vm}}\""));
        Assert.IsFalse(xaml.Contains("Text=\"{Binding GridSummaryText}\""));
        Assert.IsFalse(mainTable.Contains("DataContext.MainTableSortParameters"));
        Assert.IsFalse(mainTable.Contains("RowDragKind=\"PlaylistDropCandidateRows\""));
        Assert.IsFalse(MainWindow.TryResolveTableContextMenuPolicy(playHistoryRow, ChartOperationSourceScope.Library, out bool playHistoryMissingContextMenu));
        Assert.IsFalse(playHistoryMissingContextMenu);
        Assert.IsTrue(MainWindow.TryResolveTableContextMenuPolicy(libraryRow, ChartOperationSourceScope.Library, out bool libraryMissingContextMenu));
        Assert.IsFalse(libraryMissingContextMenu);
        Assert.AreEqual(1, CountOccurrences(xaml, "x:Key=\"playHistoryContextMenu\""));
        StringAssert.Contains(playHistoryContextMenu, "Opened=\"playHistoryContextMenuOpened\"");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemOpenBMSIR\"");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemOpenMocha\"");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemOpenMinIR\"");
        StringAssert.Contains(playHistoryContextMenu, "Click=\"playHistoryContextMenuItemOpenMochaClick\"");
        StringAssert.Contains(playHistoryContextMenu, "Click=\"playHistoryContextMenuItemOpenMinIRClick\"");
        Assert.IsFalse(playHistoryContextMenu.Contains("Click=\"tableContextMenuItemOpenMochaClick\""));
        Assert.IsFalse(playHistoryContextMenu.Contains("Click=\"tableContextMenuItemOpenMinIRClick\""));
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        Assert.IsFalse(mainWindowSource.Contains("PlayHistoryContextMenuState.TryCreate"));
        StringAssert.Contains(mainWindowSource, "row is PlayHistoryRow");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemOpenExplorer\"");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemRegisterScore\"");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemCopyMd5\"");
        StringAssert.Contains(playHistoryContextMenu, "Name=\"playHistoryContextMenuItemCopySha256\"");
        Assert.IsFalse(playHistoryContextMenu.Contains("playHistoryContextMenuItemCopyRawHash"));
        Assert.IsFalse(playHistoryContextMenu.Contains("tableContextMenuItemDeleteFile"));
        Assert.IsFalse(playHistoryContextMenu.Contains("tableContextMenuItemUpdateRankingData"));
        Assert.IsTrue(playHistoryContextMenu.IndexOf("playHistoryContextMenuItemOpenBMSIR", StringComparison.Ordinal) < playHistoryContextMenu.IndexOf("playHistoryContextMenuItemOpenMocha", StringComparison.Ordinal));
        Assert.IsTrue(playHistoryContextMenu.IndexOf("playHistoryContextMenuItemOpenMinIR", StringComparison.Ordinal) < playHistoryContextMenu.IndexOf("playHistoryContextMenuItemOpenExplorer", StringComparison.Ordinal));
        Assert.IsTrue(playHistoryContextMenu.IndexOf("playHistoryContextMenuItemRegisterScore", StringComparison.Ordinal) < playHistoryContextMenu.IndexOf("playHistoryContextMenuItemCopyMd5", StringComparison.Ordinal));
        foreach (string resource in new[]
        {
            "Play_history_copy_md5",
            "Play_history_copy_sha256"
        })
        {
            StringAssert.Contains(playHistoryContextMenu, "Path=Resources." + resource + ", Mode=OneWay", resource);
            Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.ResourceManager.GetString(resource)), resource);
        }
        Assert.IsFalse(Resources.Play_history_copy_sha256.Contains("Repository"));
        Assert.IsFalse(playHistoryOwner.TryCreateContextMenuState(playHistoryRow, out PlayHistoryContextMenuState unresolvedState));
        Assert.IsNull(unresolvedState);
        Assert.IsNull(GridRowResolver.GetRepositorySha256(playHistoryRow));
        Assert.IsTrue(playHistoryOwner.TryCreateContextMenuState(resolvedPlayHistoryRow, out PlayHistoryContextMenuState resolvedState));
        Assert.AreEqual(new string('d', 64), GridRowResolver.GetRepositorySha256(resolvedPlayHistoryRow));
        Assert.IsTrue(resolvedState.CanOpenBmsIr);
        Assert.IsTrue(resolvedState.CanOpenRepository);
        Assert.IsTrue(resolvedState.CanOpenExplorer);
        Assert.IsTrue(resolvedState.CanOpenScoreViewer);
        Assert.IsTrue(resolvedState.CanCopyMd5);
        Assert.IsTrue(resolvedState.CanCopySha256);
        Assert.IsTrue(resolvedState.HasExternalLinkItem);
        Assert.IsTrue(resolvedState.HasLocalChartItem);
        Assert.IsTrue(resolvedState.HasHashCopyItem);
        Assert.AreEqual("cccccccccccccccccccccccccccccccc", resolvedState.GetCopyValue(PlayHistoryContextMenuState.CopyMd5Kind));
        Assert.AreEqual(new string('d', 64), resolvedState.GetCopyValue(PlayHistoryContextMenuState.CopySha256Kind));
        Assert.IsTrue(playHistoryOwner.TryCreateContextMenuAction(
            resolvedPlayHistoryRow,
            PlayHistoryContextMenuActionKind.OpenBmsIr,
            out PlayHistoryContextMenuAction bmsIrAction));
        StringAssert.Contains(bmsIrAction.Url, "songmd5=cccccccccccccccccccccccccccccccc");
        Assert.IsTrue(playHistoryOwner.TryCreateContextMenuAction(
            resolvedPlayHistoryRow,
            PlayHistoryContextMenuActionKind.OpenMocha,
            out PlayHistoryContextMenuAction mochaAction));
        StringAssert.Contains(mochaAction.Url, new string('d', 64));
        Assert.IsTrue(playHistoryOwner.TryCreateContextMenuAction(
            resolvedPlayHistoryRow,
            PlayHistoryContextMenuActionKind.OpenMinIr,
            out PlayHistoryContextMenuAction minIrAction));
        StringAssert.Contains(minIrAction.Url, new string('d', 64));
        Assert.IsTrue(playHistoryOwner.TryCreateContextMenuAction(
            resolvedPlayHistoryRow,
            PlayHistoryContextMenuActionKind.RegisterScoreViewer,
            out PlayHistoryContextMenuAction scoreViewerAction));
        Assert.AreEqual("C:\\BMS\\play-history-resolved.bms", scoreViewerAction.ScoreViewerTarget.Path);
        Assert.IsFalse(playHistoryOwner.TryCreateContextMenuAction(
            playHistoryRow,
            PlayHistoryContextMenuActionKind.OpenMocha,
            out _));
    }

    [TestMethod]
    public void PlayHistoryContextMenuOwnerRejectsMalformedExternalIdentifiersWithoutFallback()
    {
        PlayHistoryRow row = CreateResolvedPlayHistoryRow("not-a-md5");
        PlayHistoryWorkflowOwner owner = new();

        Assert.IsFalse(owner.TryCreateContextMenuAction(
            row,
            PlayHistoryContextMenuActionKind.OpenBmsIr,
            out PlayHistoryContextMenuAction rejectedAction));
        Assert.IsNull(rejectedAction);
    }

    [TestMethod]
    public void PlayHistoryMainTable_ShowsDedicatedSummaryCardsAndDiagnostics()
    {
        XDocument mainWindowDocument = LoadMainWindowXamlDocument();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string rootViewModelCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string mainChartListCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "MainChartListViewModel.cs");
        string playHistoryWorkflowCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.cs");
        string displayTargetOwnerCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargets.cs");
        string playlistWorkspaceCode = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string terminalShellOwnerCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.TerminalShell.cs");
        string playlistDetailTerminalCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.DetailTerminal.cs");
        string viewExecutionCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.ViewExecution.cs");
        string presentationStateCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryPresentationState.cs");
        string summaryRow = FindElementByAttribute(mainWindowDocument, "Name", "playHistorySummaryBar")
            .ToString(SaveOptions.DisableFormatting);

        StringAssert.Contains(summaryRow, "<MultiBinding Converter=\"{StaticResource playHistorySummaryVisibilityConverter}\">");
        StringAssert.Contains(summaryRow, "<Binding Path=\"PlayHistory.IsViewActive\" />");
        StringAssert.Contains(summaryRow, "<Binding Path=\"PlaylistWorkspace.IsPlaylistSummaryMode\" />");
        StringAssert.Contains(summaryRow, "ItemsSource=\"{Binding PlayHistory.SummaryCards}\"");
        StringAssert.Contains(summaryRow, "Text=\"{Binding PlayHistory.SummaryDiagnosticText}\"");
        StringAssert.Contains(summaryRow, "Visibility=\"{Binding PlayHistory.SummaryDiagnosticText");
        StringAssert.Contains(summaryRow, "Text=\"{Binding Label}\"");
        StringAssert.Contains(summaryRow, "Text=\"{Binding Value}\"");
        StringAssert.Contains(summaryRow, "Command=\"{Binding DataContext.PlayHistory.ToggleSummaryFilterCommand");
        StringAssert.Contains(summaryRow, "CommandParameter=\"{Binding}\"");
        StringAssert.Contains(summaryRow, "<DataTrigger Binding=\"{Binding Compact}\" Value=\"True\">");
        StringAssert.Contains(summaryRow, "<DataTrigger Binding=\"{Binding IsSelected}\" Value=\"True\">");
        StringAssert.Contains(summaryRow, "App.WarningTextBrush");
        StringAssert.Contains(viewModelCode, "DiagnosticText = diagnosticSummaryText");
        StringAssert.Contains(playHistoryWorkflowCode, "public bool IsViewActive");
        Assert.IsFalse(rootViewModelCode.Contains("IsPlayHistoryViewActive"));
        Assert.IsFalse(rootViewModelCode.Contains("class cSortParameters"));
        Assert.IsFalse(rootViewModelCode.Contains("public cSortParameters SortParameters"));
        Assert.IsFalse(rootViewModelCode.Contains("PlayHistorySortParameters"));
        Assert.IsFalse(rootViewModelCode.Contains("ToCompatibilitySortParameters"));
        Assert.IsFalse(rootViewModelCode.Contains("CloneSortParameters"));
        StringAssert.Contains(playHistoryWorkflowCode, "Deactivate(clearViewActivity: false);");
        StringAssert.Contains(viewExecutionCode, "ApplySortedRows(");
        Assert.IsFalse(viewModelCode.Contains("playHistoryWorkflowOwner.ApplySortedRows("));
        Assert.IsFalse(viewModelCode.Contains("playHistoryWorkflowOwner.ApplyTerminal("));
        StringAssert.Contains(presentationStateCode, "SetSummaryCards(request.SummaryCards)");
        StringAssert.Contains(presentationStateCode, "SetDiagnosticText(request.DiagnosticText)");
        Assert.IsFalse(mainChartListCode.Contains("ApplyPlayHistoryTerminal("));
        Assert.IsFalse(mainChartListCode.Contains("PlayHistoryTerminalRequest"));
        Assert.IsFalse(mainChartListCode.Contains("PlayHistoryTerminalCommitResult"));
        StringAssert.Contains(playHistoryWorkflowCode, "ApplyTerminal(");
        StringAssert.Contains(playHistoryWorkflowCode, "ApplyPresentation(");
        StringAssert.Contains(playHistoryWorkflowCode, "ApplySortedRows(");
        StringAssert.Contains(playHistoryWorkflowCode, "BuildPresentationOnly(");
        StringAssert.Contains(playHistoryWorkflowCode, "BuildReadPresentation(");
        StringAssert.Contains(playHistoryWorkflowCode, "BuildReadView(");
        StringAssert.Contains(playHistoryWorkflowCode, "public sealed partial class PlayHistoryWorkflowOwner : ViewModel");
        StringAssert.Contains(playHistoryWorkflowCode, "public IReadOnlyList<PlayHistoryPeriodTreeItem> ArchivePeriodTree");
        StringAssert.Contains(playHistoryWorkflowCode, "public IReadOnlyList<PlayHistorySummaryCard> SummaryCards");
        StringAssert.Contains(playHistoryWorkflowCode, "public string SummaryDiagnosticText");
        StringAssert.Contains(playHistoryWorkflowCode, "public ListenerCommand<PlayHistorySummaryCard> ToggleSummaryFilterCommand");
        StringAssert.Contains(viewExecutionCode, "BuildPresentationOnly(");
        StringAssert.Contains(viewExecutionCode, "BuildReadView(");
        StringAssert.Contains(viewExecutionCode, "InvokePresentationSynchronously(");
        Assert.IsFalse(rootViewModelCode.Contains("private void ApplyPlayHistoryReadWorkflow("));
        Assert.IsFalse(rootViewModelCode.Contains("private bool TryApplyPlayHistoryPresentationOnly("));
        Assert.IsFalse(rootViewModelCode.Contains("private void ApplyPlayHistorySortedRows("));
        Assert.IsFalse(rootViewModelCode.Contains("new PlayHistoryTerminalRequest"));
        Assert.IsFalse(rootViewModelCode.Contains("ApplyPlaylistViewFromSource("));
        Assert.IsFalse(rootViewModelCode.Contains("ResolveChartInfoForPlaylistEntry("));
        Assert.IsFalse(rootViewModelCode.Contains("MainChartList.ColumnsSettings = Settings.Default.StandardCustomTableColumnSettings"));
        Assert.IsFalse(rootViewModelCode.Contains("playHistoryWorkflowOwner.PublishTerminalShellState("));
        Assert.IsFalse(rootViewModelCode.Contains("playHistoryWorkflowOwner.PublishTerminalShellStateAfterTablePublishFailure("));
        Assert.IsFalse(rootViewModelCode.Contains("ConfigureTerminalShellPublish("));
        StringAssert.Contains(playHistoryWorkflowCode, "PublishTerminalShellState(");
        StringAssert.Contains(playHistoryWorkflowCode, "PublishTerminalShellStateAfterTablePublishFailure(");
        StringAssert.Contains(playlistDetailTerminalCode, "TryCommitPlayHistoryRowsAndSource(");
        StringAssert.Contains(playlistDetailTerminalCode, "PublishPlayHistorySourceClear(");
        StringAssert.Contains(playlistDetailTerminalCode, "LogPlayHistorySourceClear(");
        StringAssert.Contains(terminalShellOwnerCode, "ownershipTransferred: true");
        StringAssert.Contains(terminalShellOwnerCode, "new AggregateException(tablePublishException, shellPublishException)");
        Assert.IsFalse(rootViewModelCode.Contains("CreatePlayHistoryViewDiagnostics"));
        Assert.IsFalse(rootViewModelCode.Contains("CountDistinctPlayHistoryFolderLabels"));
        Assert.IsFalse(rootViewModelCode.Contains("SnapshotPlayHistoryDisplayTargetTables"));
        StringAssert.Contains(playlistWorkspaceCode, "CapturePlaylistTreeTablesSnapshot");
        StringAssert.Contains(displayTargetOwnerCode, "ReplaceDisplayTargetCatalog");
        Assert.IsFalse(rootViewModelCode.Contains("MergePlayHistoryDiagnostics"));
        Assert.IsFalse(rootViewModelCode.Contains("playHistoryWorkflowOwner.ReadCache"));
        Assert.IsFalse(rootViewModelCode.Contains("playHistoryWorkflowOwner.CreateProjectionResult"));
        Assert.IsFalse(rootViewModelCode.Contains("ResolveBeatorajaPeriodSummaryOverride"));
        Assert.IsFalse(rootViewModelCode.Contains("public IReadOnlyList<PlayHistoryPeriodTreeItem> PlayHistoryArchivePeriodTree"));
        Assert.IsFalse(rootViewModelCode.Contains("public IReadOnlyList<PlayHistorySummaryCard> PlayHistorySummaryCards"));
        Assert.IsFalse(rootViewModelCode.Contains("public string PlayHistorySummaryDiagnosticText"));
        Assert.IsFalse(rootViewModelCode.Contains("TogglePlayHistorySummaryCardFilterCommand"));
        Assert.IsFalse(rootViewModelCode.Contains("RaisePropertyChanged(\"PlayHistoryArchivePeriodTree\")"));
        Assert.IsFalse(rootViewModelCode.Contains("RaisePropertyChanged(\"PlayHistorySummaryCards\")"));
        Assert.IsFalse(rootViewModelCode.Contains("RaisePropertyChanged(\"PlayHistorySummaryDiagnosticText\")"));
        StringAssert.Contains(rootViewModelCode, "PlayHistory.ExecuteView(");
        StringAssert.Contains(viewExecutionCode, "BuildReadView(");
        StringAssert.Contains(mainChartListCode, "PrepareRowsTransition(");
        StringAssert.Contains(mainChartListCode, "ApplyPresentation(");
        Assert.IsFalse(mainChartListCode.Contains("ApplyCoordinatedRows("));
    }

    [TestMethod]
    public void PlayHistoryView_LogsDedicatedStageEvents()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string reportPlayHistoryReadWorkflowProgress = SourceTextTestHelper.ExtractMethodBody(viewModelCode, "private void ReportPlayHistoryReadWorkflowProgress(");
        string viewExecutionLog = SourceTextTestHelper.ExtractMethodBody(viewModelCode, "private void LogPlayHistoryViewExecution(");
        string staleRequestLog = ExtractBetween(viewModelCode, "private void LogStalePlayHistoryViewRequest", "private static void LogPlayHistoryDiagnostics");

        StringAssert.Contains(viewModelCode, "LogPlayHistoryEvent(");
        AssertLogPlayHistoryEventContract(reportPlayHistoryReadWorkflowProgress, "play_history_read_done", "period=", "requestId=", "schemaStatus=", "rows=", "diagnosticsCount=", "elapsedMs=");
        AssertLogPlayHistoryEventContract(reportPlayHistoryReadWorkflowProgress, "play_history_read_period_index_done", "period=", "requestId=", "schemaStatus=", "days=", "diagnosticsCount=", "elapsedMs=");
        AssertLogPlayHistoryEventContract(reportPlayHistoryReadWorkflowProgress, "play_history_read_period_index_skipped", "period=", "requestId=", "schemaStatus=", "reason=schema_unavailable");
        AssertLogPlayHistoryEventContract(reportPlayHistoryReadWorkflowProgress, "play_history_projection_done", "projectionEventName", "schemaStatus=", "fallback=", "reason=", "projection_index_failed", "schema_unavailable");
        AssertLogPlayHistoryEventContract(reportPlayHistoryReadWorkflowProgress, "play_history_projection_skipped", "projectionEventName", "rawCount=", "projectedCount=", "diagnosticsCount=", "projectionMs=");
        AssertLogPlayHistoryEventContract(reportPlayHistoryReadWorkflowProgress, "play_history_projection_fallback", "projectionEventName", "fallback=", "reason=", "projection_index_failed");
        AssertLogPlayHistoryEventContract(viewExecutionLog, "play_history_view_presentation_skipped", "period=", "requestId=", "reason=no_current_matching_state", "totalMs=");
        AssertLogPlayHistoryEventContract(viewExecutionLog, "play_history_view_apply", "period=", "requestId=", "sortOnly=", "schemaStatus=", "sourceCount=", "projectedCount=", "viewCount=", "totalMs=");
        AssertLogPlayHistoryEventContract(staleRequestLog, "play_history_view_stale_skipped", "mode=", "requestedMode=", "requestId=", "currentRequestId=", "elapsedMs=");
    }

    [TestMethod]
    public void PlayHistoryView_SelectsBeatorajaProviderAndProjectsSha256Rows()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string reportPlayHistoryReadWorkflowProgress = SourceTextTestHelper.ExtractMethodBody(viewModelCode, "private void ReportPlayHistoryReadWorkflowProgress(");
        string viewExecutionCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.ViewExecution.cs");
        string workflowOwnerCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.cs"));
        string providerSelection = ExtractBetween(viewModelCode, "private bool ShouldUseBeatorajaPlayHistoryProvider", "private string ResolveMainViewBeatorajaPlayHistoryScoreDbPath");
        string rowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "PlayHistoryRow.cs"));

        StringAssert.Contains(viewModelCode, "PlayHistory.ExecuteView(");
        Assert.IsFalse(viewExecutionCode.Contains("ResolveViewRequest("));
        Assert.IsFalse(viewExecutionCode.Contains("RegisterRequest("));
        Assert.IsFalse(viewModelCode.Contains("private void ApplyPlayHistoryView("));
        StringAssert.Contains(viewModelCode, "ResolvePlayHistoryReadSourceContext");
        StringAssert.Contains(viewExecutionCode, "BuildReadView(");
        StringAssert.Contains(viewExecutionCode, "SnapshotSummaryFilterTexts(source.Provider)");
        Assert.IsFalse(viewModelCode.Contains("selectedPlayHistorySummaryFilterKeys"));
        Assert.IsFalse(viewModelCode.Contains("PrunePlayHistorySummaryCardFilters"));
        StringAssert.Contains(reportPlayHistoryReadWorkflowProgress, "provider=\" + progress.Read.Provider");
        Assert.IsFalse(viewModelCode.Contains("playHistoryWorkflowOwner.ReadCache"));
        Assert.IsFalse(viewModelCode.Contains("playHistoryWorkflowOwner.CreateProjectionResult"));
        StringAssert.Contains(workflowOwnerCode, "readCache.ReadBeatoraja(");
        StringAssert.Contains(workflowOwnerCode, "periodRequest.ToBeatorajaReadRequest");
        StringAssert.Contains(workflowOwnerCode, "CreateProjectionResult(");
        StringAssert.Contains(workflowOwnerCode, "private readonly HashSet<string> selectedSummaryFilterKeys");
        StringAssert.Contains(workflowOwnerCode, "PruneSummaryFilters(request.ViewState.Provider)");
        Assert.IsTrue(
            workflowOwnerCode.IndexOf("PlayHistoryReadWorkflowProgress.ProjectionCompleted", StringComparison.Ordinal)
            < workflowOwnerCode.IndexOf("PlayHistoryReadPresentationBuildResult presentation = BuildReadPresentation", StringComparison.Ordinal));
        StringAssert.Contains(workflowOwnerCode, "library.CreateBeatorajaPlayHistoryProjectionIndex");
        StringAssert.Contains(workflowOwnerCode, "PlayHistoryRow.ProjectBeatorajaRows(readResult, projectionIndex)");
        StringAssert.Contains(providerSelection, "GetStartupSettingsSnapshot().UseBeatorajaScoreDb");
        StringAssert.Contains(providerSelection, "GetActiveScoreSourceForDiagnostics() == ActiveScoreSource.Beatoraja");
        Assert.IsFalse(providerSelection.Contains("GetScoreSnapshotForDiagnostics"));
        StringAssert.Contains(rowCode, "safeIndex.ResolveChartByMd5(string.Empty, sha256)");
        StringAssert.Contains(rowCode, "safeIndex.ResolvePlaylistReference(resolvedMd5, sha256)");
        StringAssert.Contains(rowCode, "FormatBeatorajaOption");
    }

    [TestMethod]
    public void StartupReadiness_DoesNotWaitForLibraryFolderTreeCompletion()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string completedHandler = ExtractBetween(
            viewModelCode,
            "private void LibraryFolderTreeDeferredRefreshCompleted(",
            "private void InstallTreePresentationChanged(");
        string flushPendingUiRefresh = ExtractBetween(
            viewModelCode,
            "private void FlushPendingUiRefresh(",
            "private void ChartMutationActivityChanged");

        Assert.IsTrue(completedHandler.IndexOf("TryLogStartupReadyOperable", StringComparison.Ordinal) < 0);
        StringAssert.Contains(completedHandler, "library_folder_tree_ready");

        int scheduleIndex = flushPendingUiRefresh.IndexOf(
            "LibraryFolderTree.ScheduleDeferredRefresh",
            StringComparison.Ordinal);
        int readinessIndex = flushPendingUiRefresh.IndexOf(
            "TryLogStartupReadyOperable(operationToken)",
            StringComparison.Ordinal);
        Assert.IsTrue(scheduleIndex >= 0);
        Assert.IsTrue(readinessIndex > scheduleIndex);
        StringAssert.Contains(flushPendingUiRefresh, "if (flag)");
        StringAssert.Contains(flushPendingUiRefresh, "if (logReadiness)");
        Assert.IsTrue(flushPendingUiRefresh.IndexOf("else if (logReadiness)", StringComparison.Ordinal) < 0);
        StringAssert.Contains(viewModelCode, "startupReadyOperableStopwatch == null || !startupReadyUiReached");
    }

    [TestMethod]
    public void PlayHistoryView_KeywordFilterUpdatedReusesProjectedState()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string viewExecutionCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.ViewExecution.cs");
        string displayTargetRefreshOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargetRefresh.cs");
        string flushPendingUiRefresh = ExtractBetween(viewModelCode, "private void FlushPendingUiRefresh", "private void ChartMutationActivityChanged");
        string playlistStoreNotifications = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.PlaylistStoreNotifications.cs");
        string state = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryPresentationState.cs");
        string workflowOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.cs");
        string displayTargetOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargets.cs");

        StringAssert.Contains(viewModelCode, "PlayHistory.ExecuteView(");
        Assert.IsFalse(viewModelCode.Contains("private void ApplyPlayHistoryView("));
        StringAssert.Contains(viewExecutionCode, "request.RequestedMode == MainViewUpdateMode.SortUpdated");
        Assert.IsFalse(viewExecutionCode.Contains("playHistoryWorkflowOwner.ApplyDisplayTarget("));
        Assert.IsFalse(viewExecutionCode.Contains("playHistoryWorkflowOwner.ApplyKeywordFilters("));
        Assert.IsFalse(viewExecutionCode.Contains("PlayHistorySortEngine.TrySort("));
        StringAssert.Contains(viewModelCode, "mode == MainViewUpdateMode.KeywordFilterUpdated && parameter is PlayHistoryViewRequest playHistoryKeywordRequest");
        StringAssert.Contains(viewModelCode, "PlayHistory.ExecuteView(");
        StringAssert.Contains(viewExecutionCode, "BuildPresentationOnly(");
        Assert.IsFalse(viewExecutionCode.Contains("ApplyDisplayTarget("));
        StringAssert.Contains(viewModelCode, "if (PlayHistory.SelectedDisplayTarget.UsesProjection)");
        Assert.IsFalse(viewExecutionCode.Contains("ApplyKeywordFilters("));
        StringAssert.Contains(workflowOwner, "request.RequestedMode == MainViewUpdateMode.SortUpdated && keywordStale");
        StringAssert.Contains(workflowOwner, "request.RequestedMode == MainViewUpdateMode.SortUpdated && displayTargetStale");
        StringAssert.Contains(workflowOwner, "state.AllProjectedRows");
        StringAssert.Contains(workflowOwner, "state.FilterSourceRows");
        StringAssert.Contains(workflowOwner, "state.KeywordFilterIdentity");
        StringAssert.Contains(workflowOwner, "state.DisplayTargetIdentity");
        Assert.IsFalse(viewModelCode.Contains("private void QueuePlayHistoryKeywordFilterRefresh"));
        Assert.IsFalse(viewModelCode.Contains("private void QueuePlayHistoryDisplayTargetRefresh"));
        StringAssert.Contains(viewModelCode, "playHistoryWorkflowOwner.QueueKeywordFilterRefresh(");
        StringAssert.Contains(viewModelCode, "playHistoryWorkflowOwner.QueueDisplayTargetRefresh(");
        Assert.IsFalse(viewModelCode.Contains("private void RefreshPlayHistoryDisplayTargets"));
        StringAssert.Contains(displayTargetRefreshOwner, "internal void RefreshDisplayTargetCatalog(");
        StringAssert.Contains(displayTargetOwner, "DisplayTargetRefreshRequested");
        StringAssert.Contains(displayTargetOwner, "AdvanceDisplayTargetRevision(nextIdentity);");
        StringAssert.Contains(displayTargetOwner, "persistDisplayTargetIdentity(nextIdentity);");
        StringAssert.Contains(workflowOwner, "internal void QueueDisplayTargetRefresh(");
        StringAssert.Contains(workflowOwner, "internal void QueueKeywordFilterRefresh(");
        StringAssert.Contains(workflowOwner, "TryBeginDisplayTargetRefresh(");
        StringAssert.Contains(workflowOwner, "CompleteDisplayTargetRefresh(request.DisplayTargetRevision)");
        StringAssert.Contains(workflowOwner, "CompleteKeywordRefresh(request.KeywordFilterRevision)");
        StringAssert.Contains(workflowOwner, "refreshView(request);");
        StringAssert.Contains(displayTargetRefreshOwner, "displayTargetCatalogRefreshRequestedRevision");
        StringAssert.Contains(displayTargetRefreshOwner, "displayTargetCatalogRefreshCompletedRevision");
        StringAssert.Contains(displayTargetRefreshOwner, "displayTargetCatalogRefreshScheduled");
        StringAssert.Contains(displayTargetRefreshOwner, "if (refreshRevision == Interlocked.Read(ref displayTargetCatalogRefreshRequestedRevision))");
        StringAssert.Contains(displayTargetRefreshOwner, "ScheduleDisplayTargetCatalogRefresh(Refresh);");
        StringAssert.Contains(viewExecutionCode, "QueueDisplayTargetRefresh(");
        StringAssert.Contains(viewExecutionCode, "QueueKeywordFilterRefresh(");
        StringAssert.Contains(flushPendingUiRefresh, "PlayHistory.RefreshDisplayTargetCatalog();");
        StringAssert.Contains(flushPendingUiRefresh, "PlaylistWorkspace.RefreshPlaylistTreePresentation();");
        StringAssert.Contains(flushPendingUiRefresh, "UpdateChartKeywordSearchContext();");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.PlaylistTablesPresentationChanged += PlaylistWorkspacePlaylistTablesPresentationChanged;");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.PlaylistKeywordValueCandidatesChanged += PlaylistWorkspacePlaylistKeywordValueCandidatesChanged;");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.PlaylistEntriesHydrationCompleted += PlaylistWorkspacePlaylistEntriesHydrationCompleted;");
        StringAssert.Contains(playlistStoreNotifications, "PlaylistTablesPresentationChanged?.Invoke(this, EventArgs.Empty);");
        StringAssert.Contains(playlistStoreNotifications, "PlaylistEntriesHydrationCompleted?.Invoke(");
        string playlistPropertyEditing = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.PropertyEditing.cs");
        StringAssert.Contains(playlistPropertyEditing, "PlaylistKeywordValueCandidatesChanged?.Invoke(this, EventArgs.Empty);");
        StringAssert.Contains(viewModelCode, "if (PlayHistory.SelectedDisplayTarget.UsesProjection)");
        StringAssert.Contains(viewModelCode, "playHistoryWorkflowOwner.QueueDisplayTargetRefresh(");
        Assert.IsFalse(viewModelCode.Contains("playHistoryKeywordFilterQueuedRevision"));
        Assert.IsFalse(viewModelCode.Contains("playHistoryDisplayTargetQueuedRevision"));
        Assert.IsFalse(viewModelCode.Contains("playHistoryDisplayTargetsRefreshRequestedRevision"));
        StringAssert.Contains(workflowOwner, "internal long KeywordRevision");
        StringAssert.Contains(workflowOwner, "internal long DisplayTargetRevision");
        StringAssert.Contains(workflowOwner, "Interlocked.CompareExchange(ref keywordQueuedRevision");
        StringAssert.Contains(workflowOwner, "Interlocked.CompareExchange(ref displayTargetQueuedRevision");
        StringAssert.Contains(state, "AllProjectedRows");
        StringAssert.Contains(state, "FilterSourceRows");
        StringAssert.Contains(state, "KeywordFilterIdentity");
        StringAssert.Contains(state, "KeywordFilterRevision");
        StringAssert.Contains(state, "DisplayTargetIdentity");
        StringAssert.Contains(state, "DisplayTargetRevision");
    }

    [TestMethod]
    public void PlayHistoryDisplayTargetDropdown_BindsToPlayHistoryViewState()
    {
        XDocument mainWindowDocument = LoadMainWindowXamlDocument();
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string editDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlayHistoryFolderDisplayPresetEditDialog.xaml"));
        string editDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlayHistoryFolderDisplayPresetEditDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string settingsDialogViewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string displayTargetOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargets.cs");
        string toolbar = FindElementByAttribute(mainWindowDocument, "Name", "mainTableToolbar")
            .ToString(SaveOptions.DisableFormatting);
        string applySettings = ExtractBetween(settingsDialogViewModelCode, "internal async Task ApplySettingsAsync()", "private Settings ApplicationSettings");
        string saveSettings = ExtractBetween(settingsDialogViewModelCode, "public async Task SaveSettings()", "public async Task SaveSettingsForInitialInitialize()");
        string saveSettingsCore = ExtractBetween(settingsDialogViewModelCode, "private async Task SaveSettingsCore", "public void SaveOperationModeForRestart");

        StringAssert.Contains(toolbar, "Visibility=\"{Binding PlayHistory.IsViewActive");
        StringAssert.Contains(toolbar, "ItemsSource=\"{Binding PlayHistory.DisplayTargets}\"");
        StringAssert.Contains(toolbar, "SelectedValue=\"{Binding PlayHistory.SelectedDisplayTargetIdentity, Mode=TwoWay}\"");
        StringAssert.Contains(toolbar, "SelectedValuePath=\"Identity\"");
        StringAssert.Contains(toolbar, "DisplayMemberPath=\"DisplayName\"");
        StringAssert.Contains(displayTargetOwner, "public string SelectedDisplayTargetIdentity");
        StringAssert.Contains(displayTargetOwner, "isRefreshingDisplayTargets");
        StringAssert.Contains(viewModelCode, "PlayHistory.PeriodRequestActivated += PlayHistoryPeriodRequestActivated;");
        StringAssert.Contains(viewModelCode, "private void PlayHistoryPeriodRequestActivated(");
        Assert.IsFalse(viewModelCode.Contains("BeginPlayHistoryFilterRequest"));
        StringAssert.Contains(displayTargetOwner, "nextItems.AddRange(displayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSet));");
        StringAssert.Contains(displayTargetOwner, "nextItems.AddRange(displayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly));");
        StringAssert.Contains(displayTargetOwner, ".Select(PlayHistoryDisplayTargetItem.FromPlaylist));");
        StringAssert.Contains(viewModelCode, "playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity");
        StringAssert.Contains(displayTargetOwner, "preferredDisplayTargetIdentity");
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Play_history_display_target_folder_only_set_format));
        StringAssert.Contains(settingDialogXaml, "Path=Resources.Play_history_folder_display_preset, Mode=OneWay");
        StringAssert.Contains(settingDialogXaml, "ItemsSource=\"{Binding PlayHistoryFolderDisplayPresets}\"");
        Assert.IsFalse(settingDialogXaml.Contains("ItemsSource=\"{Binding PlayHistoryFolderDisplayPresetPlaylistOptions}\""));
        StringAssert.Contains(editDialogXaml, "ItemsSource=\"{Binding PlaylistOptions}\"");
        StringAssert.Contains(editDialogXaml, "Path=Resources.Play_history_folder_display_preset_playlists, Mode=OneWay");
        StringAssert.Contains(editDialogXaml, "Click=\"SaveAndClose\"");
        StringAssert.Contains(editDialogXaml, "IsCancel=\"True\"");
        StringAssert.Contains(editDialogCode, "TryApplyPlayHistoryFolderDisplayPresetEditSession(session, out string errMsg)");
        StringAssert.Contains(editDialogCode, "DialogResult = true;");
        StringAssert.Contains(editDialogCode, "DialogResult = false;");
        StringAssert.Contains(settingDialogXaml, "Click=\"buttonAddPlayHistoryFolderDisplayPresetClicked\"");
        StringAssert.Contains(settingDialogXaml, "Click=\"buttonRemovePlayHistoryFolderDisplayPresetClicked\"");
        StringAssert.Contains(settingDialogXaml, "Click=\"buttonEditPlayHistoryFolderDisplayPresetClicked\"");
        StringAssert.Contains(applySettings, "await SaveSettings();");
        StringAssert.Contains(saveSettings, "await SaveSettingsCore(runPostSaveActions: true);");
        StringAssert.Contains(saveSettingsCore, "PersistPlayHistoryFolderDisplayPresetsIfChanged();");
    }

    [TestMethod]
    public void ChartInfoParseFailureContextMenu_UsesDedicatedResourceAndVisibilityPolicy()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemRemoveChartInfoParseFailure\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_info_parse_failure_record, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Click=\"tableContextMenuItemRemoveChartInfoParseFailureClick\""));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Remove_chart_info_parse_failure_record));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Msg_remove_chart_info_parse_failure_record));
        Assert.IsTrue(MainWindow.ShouldShowChartInfoParseFailureRemovalMenu(true, [new string('a', 32)]));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenu(false, [new string('a', 32)]));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenu(true, [" "]));
    }

    [TestMethod]
    public void ChartInfoParseFailureContextMenu_RoutesRemovalThroughWorkflowOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string handler = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemRemoveChartInfoParseFailureClick",
            "private void tableContextMenuItemAutoRenameFolderClick");

        StringAssert.Contains(handler, "viewModel.ChartInfoParseFailureRemoval.BeginRemove");
        StringAssert.Contains(handler, "new ChartInfoParseFailureRemovalRequest(md5s)");
        StringAssert.Contains(handler, "ChartInfoParseFailureRemovalAcceptance acceptance = await operation.Acceptance;");
        StringAssert.Contains(handler, "if (acceptance.Accepted)");
        StringAssert.Contains(handler, "ChartInfoParseFailureRemovalResult result = await operation.Completion;");
        StringAssert.Contains(handler, "e.Handled = true;");
        Assert.IsFalse(handler.Contains("acceptedCallback"));
        Assert.IsFalse(handler.Contains("UiDialogRoute.ShowMessageBox"));
        Assert.IsFalse(handler.Contains("Task.Run"));
        Assert.IsFalse(handler.Contains("viewModel.RemoveChartInfoParseFailuresByMd5"));
    }

    [TestMethod]
    public void ResourceHealthContextMenu_AllowsBmsonOnlyChartTargets()
    {
        ChartOperationTarget bmsonTarget = CreateContextMenuTarget(
            ChartFileKind.Bmson,
            ChartOperationCapabilities.RunResourceHealthCheck);
        ChartOperationTarget bmsOnlyTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.RunBmsEncodingFix);

        Assert.IsTrue(MainWindow.ShouldShowResourceHealthContextMenu(false, [bmsonTarget]));
        Assert.IsFalse(MainWindow.ShouldShowResourceHealthContextMenu(true, [bmsonTarget]));
        Assert.IsFalse(MainWindow.ShouldShowResourceHealthContextMenu(false, [bmsOnlyTarget]));
    }

    [TestMethod]
    public void ChartContextMenuStateBuilder_UsesRowTargetAsSelectionFallback()
    {
        ChartOperationTarget rowTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.OpenFile);

        ChartContextMenuState state = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow: false,
            isPendingSelected: true,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: []));

        Assert.IsTrue(state.IsInstallListSelected);
        Assert.IsFalse(state.IsPlaylistContext);
        Assert.AreEqual(1, state.SelectedTargets.Count);
        Assert.AreSame(rowTarget, state.SelectedTargets[0]);
        Assert.IsFalse(state.HasBmsonSelection);
        Assert.IsTrue(state.HasBmsSelection);
        Assert.IsFalse(state.CanMoveSelectedFiles);
        Assert.IsFalse(state.CanConvertToAudio);
        Assert.IsTrue(state.CanDeleteInstallPackages);
    }

    [TestMethod]
    public void ChartContextMenuStateBuilder_PreservesSelectionWhenItExists()
    {
        ChartOperationTarget rowTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.OpenFile);
        ChartOperationTarget selectedTarget = CreateContextMenuTarget(
            ChartFileKind.Bmson,
            ChartOperationCapabilities.RunResourceHealthCheck);

        ChartContextMenuState state = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow: true,
            isPendingSelected: false,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: [selectedTarget]));

        Assert.IsFalse(state.IsInstallListSelected);
        Assert.IsTrue(state.IsPlaylistContext);
        Assert.AreEqual(1, state.SelectedTargets.Count);
        Assert.AreSame(selectedTarget, state.SelectedTargets[0]);
        Assert.IsTrue(state.HasResourceHealthTarget);
        Assert.IsFalse(state.CanShowResourceHealthMenu);
        Assert.IsTrue(state.HasBmsonSelection);
        Assert.IsFalse(state.HasBmsSelection);
        Assert.IsFalse(state.CanAutoRenameFolders);
        Assert.IsFalse(state.CanFixEncoding);
    }

    [TestMethod]
    public void RankingCacheContextMenu_DelegatesSelectionSnapshotToOwner()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindow = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "MainWindow.cs");
        string handler = ExtractMethodBody(
            mainWindow,
            "private void tableContextMenuItemUpdateRankingDataClick");

        StringAssert.Contains(xaml, "Click=\"tableContextMenuItemUpdateRankingDataClick\"");
        StringAssert.Contains(handler, "GetSelectedChartTargets()");
        StringAssert.Contains(handler, "RankingCacheDownloadWorkflow.Request(targets)");
        Assert.AreEqual(1, CountOccurrences(handler, ".RankingCacheDownloadWorkflow.Request("));
        Assert.IsFalse(handler.Contains("Task.Run"));
        Assert.IsFalse(mainWindow.Contains("GetLR2IRCacheHashes("));
    }

    [TestMethod]
    public void ScoreViewerContextMenus_DelegateSelectionAndAvailabilityToOwner()
    {
        string mainWindow = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "MainWindow.cs");
        string normalMenu = ExtractBetween(
            mainWindow,
            "private void tableContextMenuOpened",
            "private void tableContextMenuPlaylistMissingOpened");
        string playlistMissingMenu = ExtractBetween(
            mainWindow,
            "private void tableContextMenuPlaylistMissingOpened",
            "private void playHistoryContextMenuOpened");
        string clickHandler = ExtractMethodBody(
            mainWindow,
            "private async void tableContextMenuItemRegisterBMSFileToScoreViwer");

        StringAssert.Contains(normalMenu, "ScoreViewerRegistration.HasScoreViewerTarget(selectedTargets)");
        StringAssert.Contains(normalMenu, "ScoreViewerRegistration.CanRegisterScoreViewer(selectedTargets)");
        StringAssert.Contains(playlistMissingMenu, "ScoreViewerRegistration.HasScoreViewerTarget([rowTarget])");
        StringAssert.Contains(clickHandler, "GetSelectedChartTargets()");
        StringAssert.Contains(clickHandler, "viewModel.ScoreViewerRegistration.RunAsync(");
        Assert.IsFalse(mainWindow.Contains("GetSelectedGridScoreViewerTargets"));
        Assert.IsFalse(mainWindow.Contains("TryCreateScoreViewerTarget"));
        Assert.IsFalse(normalMenu.Contains("HasScoreViewerTarget ="));
    }

    [TestMethod]
    public void ChartContextMenuStateBuilder_ResolvesCapabilityPolicy()
    {
        ChartOperationTarget rowTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UseLr2Ir
                | ChartOperationCapabilities.UpdateRanking
                | ChartOperationCapabilities.RunResourceHealthCheck
                | ChartOperationCapabilities.UpdateInstallDestination
                | ChartOperationCapabilities.RemoveFromLibrary
                | ChartOperationCapabilities.RenameInvalidExtension
                | ChartOperationCapabilities.UseScoreViewer);

        ChartContextMenuState state = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow: false,
            isPendingSelected: false,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: [rowTarget]));

        Assert.IsTrue(state.HasResourceHealthTarget);
        Assert.IsTrue(state.CanShowResourceHealthMenu);
        Assert.IsTrue(state.CanMoveSelectedFiles);
        Assert.IsTrue(state.CanDeleteFiles);
        Assert.IsTrue(state.CanRenameInvalidExtension);
        Assert.IsTrue(state.CanShowFolderViewSeparator);
        Assert.IsTrue(state.CanAutoRenameFolders);
        Assert.IsTrue(state.CanFixEncoding);
        Assert.IsTrue(state.CanConvertToAudio);
        Assert.IsFalse(state.CanDeleteInstallPackages);
    }

    [TestMethod]
    public void ChartOperationTargetSelectionResolver_FiltersRowsByCapability()
    {
        LibraryChartRow row = LibraryChartRow.FromBmsFile(CreateContextMenuBmsFile());

        List<ChartOperationTarget> openFileTargets = ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            [row, new object()],
            ChartOperationSourceScope.Library,
            ChartOperationCapabilities.OpenFile));
        List<ChartOperationTarget> pendingInstallTargets = ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            [row],
            ChartOperationSourceScope.Library,
            ChartOperationCapabilities.UpdateInstallDestination));

        Assert.AreEqual(1, openFileTargets.Count);
        Assert.AreSame(row.Chart, openFileTargets[0].Chart);
        Assert.AreEqual(0, pendingInstallTargets.Count);
    }

    [TestMethod]
    public void ChartOperationTargetSelectionResolver_RejectsNullRows()
    {
        Assert.ThrowsException<ArgumentException>(() => ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            [null!],
            ChartOperationSourceScope.Library)));
    }

    [TestMethod]
    public void PackageCatalogRemovalRequest_RequiresTargetsAndPreservesSection()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        PackageCatalogRemovalRequest pending = PackageCatalogRemovalRequest.CreatePending([target]);
        PackageCatalogRemovalRequest installed = PackageCatalogRemovalRequest.CreateInstalled([target]);

        Assert.AreEqual(PackageCatalogSection.Pending, pending.Section);
        Assert.IsTrue(pending.IsPending);
        Assert.AreSame(target, pending.Targets[0]);
        Assert.AreEqual(PackageCatalogSection.Installed, installed.Section);
        Assert.IsFalse(installed.IsPending);
        Assert.ThrowsException<ArgumentException>(() => PackageCatalogRemovalRequest.CreatePending([]));
        Assert.ThrowsException<ArgumentException>(() => PackageCatalogRemovalRequest.CreateInstalled([null!]));
    }

    [TestMethod]
    public void PackageCatalogHandlersDelegateConfirmationAndMutationToWorkflowOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string ownerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PackageCatalogWorkflowOwner.cs");
        string mainWindowXaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Views",
            "MainWindow.xaml"));
        string clearInstalled = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstalledContextMenuClearAllClick",
            "private async void treeViewInstallPendingContextMenuClearAllClick");
        string clearPending = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPendingContextMenuClearAllClick",
            "private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
        string mutationObserver = ExtractBetween(
            mainWindowCode,
            "private static async Task<PackageCatalogMutationResult> ObservePackageCatalogMutationAsync",
            "private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick");
        string removePendingPackage = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuClearFolderClick",
            "private async void treeViewInstalledFolderContextMenuClearFolderClick");
        string removeInstalledPackage = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstalledFolderContextMenuClearFolderClick",
            "private async void treeViewInstallPackageContextMenuRemoveInstallDestinationClick");

        StringAssert.Contains(clearInstalled, ".ClearAllAsync(PackageCatalogSection.Installed)");
        Assert.AreEqual(1, CountOccurrences(clearInstalled, ".ClearAllAsync("));
        Assert.IsFalse(clearInstalled.Contains("ConfirmClearAll"));
        StringAssert.Contains(clearPending, ".ClearAllAsync(PackageCatalogSection.Pending)");
        Assert.AreEqual(1, CountOccurrences(clearPending, ".ClearAllAsync("));
        Assert.IsFalse(clearPending.Contains("ConfirmClearAll"));
        StringAssert.Contains(removePendingPackage, ".RemovePackageAsync(");
        StringAssert.Contains(removePendingPackage, "PackageCatalogSection.Pending");
        Assert.AreEqual(1, CountOccurrences(removePendingPackage, ".RemovePackageAsync("));
        Assert.IsFalse(removePendingPackage.Contains("ConfirmRemovePackage"));
        StringAssert.Contains(removeInstalledPackage, ".RemovePackageAsync(");
        StringAssert.Contains(removeInstalledPackage, "PackageCatalogSection.Installed");
        Assert.AreEqual(1, CountOccurrences(removeInstalledPackage, ".RemovePackageAsync("));
        Assert.IsFalse(removeInstalledPackage.Contains("ConfirmRemovePackage"));
        Assert.IsFalse(clearInstalled.Contains("ObservePackageCatalogConfirmation"));
        Assert.IsFalse(clearPending.Contains("ObservePackageCatalogConfirmation"));
        StringAssert.Contains(mainWindowXaml, "Click=\"treeViewInstallPackageContextMenuClearFolderClick\"");
        StringAssert.Contains(mainWindowXaml, "Click=\"treeViewInstalledFolderContextMenuClearFolderClick\"");
        foreach (string handler in new[] { clearInstalled, clearPending, removePendingPackage, removeInstalledPackage })
        {
            StringAssert.Contains(handler, "viewModel.PackageCatalog");
            StringAssert.Contains(handler, "ObservePackageCatalogMutationAsync(");
            Assert.IsFalse(handler.Contains("e.Handled = true;\r\n        await ObservePackageCatalogMutationAsync"));
            Assert.IsFalse(handler.Contains("UiDialogRoute"));
        }
        StringAssert.Contains(removePendingPackage, "CaptureNextSiblingOrRoot(");
        StringAssert.Contains(removeInstalledPackage, "CaptureNextSiblingOrRoot(");
        Assert.IsFalse(removePendingPackage.Contains("() =>"));
        Assert.IsFalse(removeInstalledPackage.Contains("() =>"));
        StringAssert.Contains(
            mutationObserver,
            "result = await operation;");
        StringAssert.Contains(mutationObserver, "_ = Task.FromException(exception).Logging(routeName)");
        StringAssert.Contains(
            mutationObserver,
            "_ = Task.FromException(result.Failure).Logging(routeName)");
        Assert.IsTrue(
            mutationObserver.IndexOf("_ = Task.FromException(result.Failure).Logging(routeName)", StringComparison.Ordinal)
            < mutationObserver.LastIndexOf("return result;", StringComparison.Ordinal));
        StringAssert.Contains(ownerCode, "dialogs.ConfirmAsync(");
        StringAssert.Contains(ownerCode, "store.RemoveAll(");
        StringAssert.Contains(ownerCode, "store.RemovePackages(");
        StringAssert.Contains(ownerCode, "internal event EventHandler<PackageCatalogMutationPhaseEventArgs> MutationPhasePublished;");
        StringAssert.Contains(viewModelCode, "PackageCatalog.MutationPhasePublished += PackageCatalogMutationPhasePublished;");
        Assert.IsFalse(ownerCode.Contains("IPackageCatalogMutationPresentation"));
        Assert.IsFalse(ownerCode.Contains("NoOpPackageCatalogMutationPresentation"));
        Assert.IsFalse(viewModelCode.Contains("IPackageCatalogMutationPresentation"));
        Assert.IsFalse(viewModelCode.Contains("packageCatalogPresentation"));
        Assert.IsFalse(ownerCode.Contains("ConfirmRemovePackage"));
        Assert.IsFalse(ownerCode.Contains("PackageCatalogConfirmationResult"));
    }

    [TestMethod]
    public void PendingInstallPackageOperationRequest_RequiresTargetsAndPreservesKind()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        PendingInstallPackageOperationRequest forceInstall = PendingInstallPackageOperationRequest.CreateForceInstall([target]);
        PendingInstallPackageOperationRequest manualInstall = PendingInstallPackageOperationRequest.CreateManualInstall([target]);

        Assert.AreEqual(PendingInstallPackageOperationKind.ForceInstall, forceInstall.Kind);
        Assert.IsTrue(forceInstall.IsForceInstall);
        Assert.IsFalse(forceInstall.IsManualInstall);
        Assert.AreEqual(1, forceInstall.SelectedRowCount);
        Assert.AreSame(target, forceInstall.Targets[0]);
        Assert.AreEqual(PendingInstallPackageOperationKind.ManualInstall, manualInstall.Kind);
        Assert.IsFalse(manualInstall.IsForceInstall);
        Assert.IsTrue(manualInstall.IsManualInstall);
        Assert.ThrowsException<ArgumentException>(() => PendingInstallPackageOperationRequest.CreateForceInstall([]));
        Assert.ThrowsException<ArgumentException>(() => PendingInstallPackageOperationRequest.CreateManualInstall([null!]));
    }

    [TestMethod]
    public void PendingInstallDestinationSearchRequest_MaterializesLooseTargetsAndPreservesKind()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        PendingInstallDestinationSearchRequest installSearch = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([target]);
        PendingInstallDestinationSearchRequest mergeSearch = PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch([target]);

        Assert.AreEqual(PendingInstallDestinationSearchKind.InstallDestination, installSearch.Kind);
        Assert.IsTrue(installSearch.HasTargets);
        Assert.AreEqual(1, installSearch.SelectedRowCount);
        Assert.AreEqual(0, installSearch.PackageTargets.Count);
        Assert.AreEqual(1, installSearch.LooseEntries.Count);
        Assert.AreEqual(target.Chart.Path, installSearch.LooseEntries[0].Chart.Path);
        Assert.AreEqual(PendingInstallDestinationSearchKind.MergeDestination, mergeSearch.Kind);
        Assert.ThrowsException<ArgumentException>(() => PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([]));
        Assert.ThrowsException<ArgumentException>(() => PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch([null!]));
    }

    [TestMethod]
    public void PendingInstallDestinationClearRequest_TryCreateMaterializesLooseTargets()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate([target], out PendingInstallDestinationClearRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual(0, request.PackageTargets.Count);
        Assert.AreEqual(1, request.LooseEntries.Count);
        Assert.AreEqual(target.Chart.Path, request.LooseEntries[0].Chart.Path);
        Assert.IsFalse(PendingInstallDestinationClearRequest.TryCreate([], out PendingInstallDestinationClearRequest emptyRequest));
        Assert.IsNull(emptyRequest);
        Assert.IsFalse(PendingInstallDestinationClearRequest.TryCreate([null!], out PendingInstallDestinationClearRequest nullRequest));
        Assert.IsNull(nullRequest);
    }

    [TestMethod]
    public void PendingInstallDestinationEditRequest_TryCreatePreservesLazyLooseTarget()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(target, out PendingInstallDestinationEditRequest request));

        Assert.IsTrue(request.HasTarget);
        Assert.IsNull(request.PackageEntry);
        Assert.AreSame(target.Chart, request.ChartFile);
        PackageChartEntry entry = request.GetOrCreateChartEntry();
        Assert.IsNotNull(entry);
        Assert.AreEqual(target.Chart.Path, entry.Chart.Path);
        Assert.IsFalse(PendingInstallDestinationEditRequest.TryCreate(null, out PendingInstallDestinationEditRequest nullRequest));
        Assert.IsNull(nullRequest);
    }

    [TestMethod]
    public void RepairInstalledLocationRequest_TryCreatePreservesRepairTargets()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.RepairInstalledLocation);

        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual(1, request.Targets.Count);
        Assert.AreSame(target, request.Targets[0]);
        Assert.IsFalse(request.HasInstallDestination);
        request.MaterializeRepairEntries();
        Assert.AreEqual(1, request.RepairEntries.Count);
        Assert.AreEqual(target.Chart.Path, request.RepairEntries[0].Chart.Path);
        Assert.IsFalse(RepairInstalledLocationRequest.TryCreate([], out RepairInstalledLocationRequest emptyRequest));
        Assert.IsNull(emptyRequest);
    }

    [TestMethod]
    public void TableContextMenuOpened_UsesContextMenuStateBuilderForUiIndependentState()
    {
        string code = SourceTextTestHelper.ReadMainWindowSourceText();
        string method = ExtractBetween(code, "private void tableContextMenuOpened", "private void tableContextMenuPlaylistMissingOpened");

        StringAssert.Contains(method, "ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(");
        StringAssert.Contains(method, "CapturePlaylistUrlContextMenuAvailability(");
        Assert.IsFalse(method.Contains("BuildPlaylistUrlTargets("));
        Assert.IsFalse(method.Contains("BuildPlaylistExternalPackageMd5Targets("));
        Assert.IsFalse(method.Contains("selectedTargets.Add(rowTarget)"));
    }

    [TestMethod]
    public void LibraryFolderContextMenus_ExposeLightReloadAndFullReinitialize()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(0, CountOccurrences(xaml, "MethodName=\"ReloadFileDiff\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Click=\"treeViewLibraryFolderContextMenuItemReloadClick\""));
        string reloadHandler = ExtractBetween(
            mainWindowCode,
            "private async void treeViewLibraryFolderContextMenuItemReloadClick",
            "private async void treeViewLibraryFolderContextMenuItemUnregisterRootFolder");
        StringAssert.Contains(reloadHandler, "await viewModel.ReloadFileDiffAsync()");
        StringAssert.Contains(reloadHandler, ".LoggingAndPropagate(\"treeViewLibraryFolderContextMenuItemReloadClick\")");
        Assert.IsFalse(mainWindowCode.Contains("viewModel.ReloadFileDiff();"));
        Assert.AreEqual(0, CountOccurrences(xaml, "MethodName=\"ReinitializeLibrary\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Click=\"treeViewLibraryFolderContextMenuItemReinitializeClick\""));
        string reinitializeHandler = ExtractBetween(
            mainWindowCode,
            "private async void treeViewLibraryFolderContextMenuItemReinitializeClick",
            "private async void treeViewLibraryFolderContextMenuItemUnregisterRootFolder");
        StringAssert.Contains(reinitializeHandler, "await viewModel.ReinitializeLibraryAsync()");
        StringAssert.Contains(reinitializeHandler, ".LoggingAndPropagate(\"treeViewLibraryFolderContextMenuItemReinitializeClick\")");
        Assert.AreEqual(2, CountOccurrences(xaml, "Path=Resources.Reinitialize_library, Mode=OneWay"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Reinitialize_library));
    }

    [TestMethod]
    public void PlaybackHeaderTextBlocks_InheritPlaybackPanelDataContext()
    {
        XDocument document = LoadPlaybackPanelXamlDocument();
        XElement header = FindElementByAttribute(document, "Name", "gridPlayerTitle");
        XElement compactHeader = FindElementByAttribute(document, "Name", "gridBMSPlayerControlsTitle2");
        XElement slider = FindElementByAttribute(document, "Name", "sliderPlayer");
        XElement playButton = FindElementByAttribute(document, "Name", "buttonBMSPlayerControlsPlayAndPauseButton");

        Assert.AreEqual(string.Empty, GetAttributeValue(header, "DataContext"));
        Assert.AreEqual(string.Empty, GetAttributeValue(compactHeader, "DataContext"));
        Assert.AreEqual(string.Empty, GetAttributeValue(slider, "DataContext"));
        Assert.AreEqual(string.Empty, GetAttributeValue(playButton, "DataContext"));
        Assert.AreEqual("{Binding CurrentlyPlayingTime, Mode=TwoWay, Converter={StaticResource timeSpanToDoubleSecConverter}}", GetAttributeValue(slider, "Value"));
        Assert.IsTrue(document.Descendants().Any(element =>
            GetAttributeValue(element, "Text").IndexOf("PlayerVolume", StringComparison.Ordinal) >= 0));
        Assert.IsTrue(document.Descendants().Any(element =>
            GetAttributeValue(element, "Value").IndexOf("PlayerVolume", StringComparison.Ordinal) >= 0));
        Assert.AreEqual("{Binding NowPlayingBmsFile.status, Converter={StaticResource nowPlayingBMStoPlayButtonStringComverter}, FallbackValue=play}", GetAttributeValue(playButton, "Content"));

        Assert.AreEqual("{Binding PlayerHeaderArtist}", GetAttributeValue(FindElementByAttribute(header, "Name", "gridBMSPlayerControlsArtist"), "Text"));
        Assert.AreEqual("{Binding PlayerHeaderTitle}", GetAttributeValue(FindElementByAttribute(header, "Name", "gridBMSPlayerControlsTitle"), "Text"));
        Assert.AreEqual("{Binding PlayerHeaderSubtitle}", GetAttributeValue(FindElementByAttribute(header, "Name", "gridBMSPlayerControlsSubtitle"), "Text"));
        Assert.AreEqual("{Binding PlayerHeaderArtist}", GetAttributeValue(FindElementByAttribute(header, "Name", "gridBMSPlayerControlsArtistForMovie"), "Text"));
        Assert.AreEqual("{Binding PlayerHeaderTitle}", GetAttributeValue(FindElementByAttribute(header, "Name", "gridBMSPlayerControlsTitleForMovie"), "Text"));
        Assert.AreEqual("{Binding PlayerHeaderSubtitle}", GetAttributeValue(FindElementByAttribute(header, "Name", "gridBMSPlayerControlsSubtitleForMovie"), "Text"));

        CollectionAssert.AreEquivalent(
            new[] { "PlayerHeaderTitle", "PlayerHeaderSubtitle", "PlayerHeaderArtist" },
            compactHeader.Descendants()
                .Where(element => element.Name.LocalName == "Binding")
                .Select(element => GetAttributeValue(element, "Path"))
                .Where(path => path.StartsWith("PlayerHeader", StringComparison.Ordinal))
                .ToArray());
        Assert.IsTrue(compactHeader.Descendants().Any(element => element.Name.LocalName == "Binding"
            && GetAttributeValue(element, "Path") == "Visibility"
            && GetAttributeValue(element, "ElementName") == "gridPlayerTitle"));
    }

    [TestMethod]
    public void PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner()
    {
        string xaml = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "PlaybackPanelView.xaml");
        string mainWindowXaml = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "MainWindow.xaml");
        string mainWindowCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "MainWindow.cs");
        string mainWindowViewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string codeBehind = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "PlaybackPanelView.xaml.cs");
        XDocument playbackDocument = LoadPlaybackPanelXamlDocument();
        XDocument mainWindowDocument = LoadMainWindowXamlDocument();
        XElement playbackRoot = playbackDocument.Root;
        XElement mainGrid = FindElementByAttribute(mainWindowDocument, "Name", "grid");
        XElement playbackRow = DirectChild(mainGrid, "Grid.RowDefinitions").Elements().First();
        XElement statusBar = FindElementByAttribute(mainWindowDocument, "Name", "progressStatusBar");

        StringAssert.Contains(mainWindowXaml, "DataContext=\"{Binding PlaybackPanel}\"");
        Assert.IsFalse(mainWindowXaml.Contains("BrowserHtml="));
        StringAssert.Contains(mainWindowXaml, "PlaybackStarting=\"playbackPanelViewPlaybackStarting\"");
        StringAssert.Contains(mainWindowXaml, "PlaybackStarted=\"playbackPanelViewPlaybackStarted\"");
        Assert.IsFalse(mainWindowCode.Contains("PlaybackPanel.PlaybackStarting +="));
        Assert.IsFalse(mainWindowCode.Contains("PlaybackPanel.PlaybackStarted +="));
        Assert.IsFalse(mainWindowCode.Contains("viewModel.PlaybackPanel.Start()"));
        Assert.IsFalse(mainWindowCode.Contains("playbackPanelView.RotatePanelState()"));
        StringAssert.Contains(mainWindowCode, "playbackPanelView.EnsureSelectedSurfaceAvailable();");
        Assert.IsFalse(mainWindowCode.Contains("GridRowResolver.TryGetBmsPlayerFile(e.Row"));
        Assert.IsFalse(mainWindowCode.Contains("viewModel.PlaybackPanel.SetBmsPlayerHeader"));
        StringAssert.Contains(mainWindowCode, "viewModel.PlaybackPanel.HandleTableSelection(e.SelectedRow)");
        StringAssert.Contains(mainWindowCode, "viewModel.PlaybackPanel.HandleTableRowActivation(e.RowIndex, e.Row)");
        XElement minHeight = mainWindowDocument.Root.Elements().Single(element => element.Name.LocalName == "Window.MinHeight");
        StringAssert.Contains(minHeight.ToString(SaveOptions.DisableFormatting), "StaticResource mainWindowMinHeightConverter");
        StringAssert.Contains(minHeight.ToString(SaveOptions.DisableFormatting), "ElementName=\"playbackPanelView\" Path=\"ActualHeight\"");
        Assert.AreEqual("Auto", GetAttributeValue(playbackRow, "Height"));
        Assert.AreEqual("{Binding ActualHeight, ElementName=playbackPanelView}", GetAttributeValue(playbackRow, "MinHeight"));
        Assert.AreEqual("Top", GetAttributeValue(playbackRoot, "VerticalAlignment"));
        Assert.AreEqual("286", GetAttributeValue(playbackRoot, "Height"));
        XElement playbackGrid = FindElementByAttribute(playbackDocument, "Name", "gridBMSPlayer");
        XElement webBrowser = FindElementByAttribute(playbackDocument, "Name", "webBrowser");
        XElement playbackRows = DirectChild(playbackGrid, "Grid.RowDefinitions");
        CollectionAssert.AreEqual(new[] { "*", "30" }, playbackRows.Elements().Select(row => GetAttributeValue(row, "Height")).ToArray());
        XElement playbackBody = FindElementByAttribute(playbackDocument, "Name", "gridBMSPlayerBody");
        Assert.AreEqual("0", GetAttributeValue(playbackBody, "Grid.Row"));
        Assert.AreEqual(string.Empty, GetAttributeValue(playbackBody, "Height"));
        Assert.AreEqual("1", GetAttributeValue(FindElementByAttribute(playbackDocument, "Name", "gridBMSPlayerControls"), "Grid.Row"));
        Assert.AreEqual("Collapsed", GetAttributeValue(webBrowser, "Visibility"));
        Assert.IsFalse(playbackRoot.Elements().Any(element => element.Name.LocalName == "UserControl.Style"));
        Assert.IsFalse(xaml.Contains("animationOpenBMSPlayerBody"));
        Assert.IsFalse(xaml.Contains("animationCloseBMSPlayerBody"));
        StringAssert.Contains(codeBehind, "ApplyPlaybackPanelHeight(playbackPanel?.PlayerPanelState ?? PlayerPanelState.TITLE_LARGE, false)");
        StringAssert.Contains(codeBehind, "ApplyPlaybackPanelHeight(subscribedPlaybackPanel.PlayerPanelState, IsLoaded && !isClosingOrClosed)");
        Assert.AreEqual("28", GetAttributeValue(statusBar, "Height"));
        Assert.AreEqual("Bottom", GetAttributeValue(statusBar, "DockPanel.Dock"));
        FindElementByAttribute(mainWindowDocument, "Name", "mainTableToolbar");
        FindElementByAttribute(mainWindowDocument, "Name", "playHistorySummaryBar");
        Assert.IsFalse(mainWindowXaml.Contains("MaxHeight=\"{Binding Height, ElementName=playbackPanelView}\""));
        Assert.IsFalse(mainWindowXaml.Contains("Name=\"gridBMSPlayer\""));
        Assert.IsFalse(xaml.Contains("ElementName=\"settingDialog\""));
        Assert.IsFalse(xaml.Contains("ElementName=\"playlistPropertyDialog\""));
        Assert.IsFalse(xaml.Contains("ElementName=\"playlistSummaryBulkEditDialog\""));
        Assert.IsFalse(xaml.Contains("ElementName=\"loadPlaylistURIDialog\""));
        Assert.IsFalse(xaml.Contains("BrowserHtml"));
        Assert.IsFalse(codeBehind.Contains("BrowserHtml"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("BrowserHtml"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("BrowserSource"));
        StringAssert.Contains(codeBehind, "private bool IsMoviePlayerSurfaceAvailable() => false;");
        Assert.IsFalse(codeBehind.Contains("TrySelectMoviePlayerSurface"));
        Assert.IsFalse(codeBehind.Contains("webBrowserLoadCompleted"));
        Assert.IsFalse(xaml.Contains("LoadCompleted="));
        Assert.IsFalse(xaml.Contains("WebBrowserUtility.Html"));
        StringAssert.Contains(codeBehind, "ResolveSurfaceState(");
        StringAssert.Contains(codeBehind, "if (resolvedState.HasFlag(PlayerPanelState.BMS_PLAYER)) ShowBmsPlayer(); else CollapseBmsPlayer();");
        Assert.IsFalse(xaml.Contains("DataContext.PlayerPanelState"));
        StringAssert.Contains(xaml, "EffectivePlayerPanelState");
        string ensureSurface = ExtractBetween(codeBehind, "public void EnsureSelectedSurfaceAvailable()", "public bool TrySelectBmsPlayerSurface()");
        Assert.IsFalse(ensureSurface.Contains("RotatePanelState("));
        StringAssert.Contains(ensureSurface, "ApplySelectedSurface(PlaybackPanel.PlayerPanelState);");
        string hostEnabledChanged = ExtractBetween(codeBehind, "private void windowsFormsHostIsEnabledChanged", "private void gridBMSPlayerControlsNextButtonClicked");
        Assert.IsFalse(hostEnabledChanged.Contains("RotatePanelState("));
        StringAssert.Contains(hostEnabledChanged, "ApplySelectedSurface(PlaybackPanel.PlayerPanelState);");
        string capabilityChange = ExtractBetween(codeBehind, "if (e.PropertyName == nameof(PlaybackPanelViewModel.UsesUbMplay)", "private void ApplyPlaybackPanelHeight");
        StringAssert.Contains(capabilityChange, "ApplySelectedSurface(subscribedPlaybackPanel.PlayerPanelState);");
        Assert.IsFalse(xaml.Contains("MainWindowViewModelResourceExtension"));
        Assert.IsFalse(xaml.Contains("Source={StaticResource vm}"));
        StringAssert.Contains(xaml, "<FontFamily x:Key=\"LigatureSymbols\">");
        StringAssert.Contains(xaml, "<FontFamily x:Key=\"Commodore\">");
        StringAssert.Contains(xaml, "PlacementTarget.DataContext");
        Assert.IsFalse(xaml.Contains("Path=\"PlayerPanelState\" Source=\"{x:Static prop:Settings.Default}\""));
        Assert.IsFalse(xaml.Contains("UsePlayeruBMplay, Source={x:Static prop:Settings.Default}"));
        Assert.IsFalse(xaml.Contains("UsePlayerLR2body, Source={x:Static prop:Settings.Default}"));
        Assert.IsFalse(xaml.Contains("UsePlayerBMIIDXView, Source={x:Static prop:Settings.Default}"));
        Assert.IsFalse(codeBehind.Contains("MainWindowViewModel"));
        Assert.IsFalse(codeBehind.Contains("Settings.Default"));
        Assert.IsFalse(xaml.Contains("prop:Settings.Default"));
        StringAssert.Contains(xaml, "{Binding CanSeek}");
        StringAssert.Contains(xaml, "{Binding CanChangeHighSpeed}");
        StringAssert.Contains(xaml, "{Binding RepeatPlayMode, Mode=TwoWay}");
        StringAssert.Contains(xaml, "PreviewMouseLeftButtonDown=\"gridBMSPlayerControlsNextButtonClicked\"");
        StringAssert.Contains(xaml, "PreviewMouseLeftButtonDown=\"gridBMSPlayerControlsPlayStopButtonClicked\"");
        StringAssert.Contains(codeBehind, "viewModel.NextCommand.Execute()");
        StringAssert.Contains(codeBehind, "viewModel.StopCommand.Execute()");
        string nextHandler = ExtractBetween(codeBehind, "private void gridBMSPlayerControlsNextButtonClicked", "private void gridBMSPlayerControlsPreviousButtonClicked");
        string stopHandler = ExtractBetween(codeBehind, "private void gridBMSPlayerControlsPlayStopButtonClicked", "private void gridBMSPlayerControlsFastForwardButtonClicked");
        Assert.IsFalse(nextHandler.Contains("e.Handled = true"));
        StringAssert.Contains(stopHandler, "e.Handled = true");
    }

    [TestMethod]
    public void ProgressStatusBar_BindsThroughProgressHubDataContextAndKeepsRootClickHandlers()
    {
        XDocument document = LoadMainWindowXamlDocument();
        XElement statusBar = document.Descendants().Single(element => element.Name.LocalName == "StatusBar");
        string mainWindowViewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string mainWindowCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Views",
            "MainWindow.cs");
        string progressHubCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "OperationProgressHubViewModel.cs");
        string startupProgressOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "StartupProgressWorkflowOwner.cs");

        Assert.AreEqual("{Binding ProgressHub}", GetAttributeValue(statusBar, "DataContext"));
        AssertStatusBarBinding(statusBar, "StartupProgress.IsActive");
        AssertStatusBarBinding(statusBar, "StartupProgress.Label");
        AssertStatusBarBinding(statusBar, "StartupProgress.SubLabel");
        AssertStatusBarBinding(statusBar, "StartupProgress.Maximum");
        AssertStatusBarBinding(statusBar, "StartupProgress.Value");
        AssertStatusBarBinding(statusBar, "IsInstallPipelineStatusActive");
        AssertStatusBarBinding(statusBar, "InstallPipelineLabel");
        AssertStatusBarBinding(statusBar, "InstallPipelineSubLabel");
        AssertStatusBarBinding(statusBar, "InstallPipelineMaximum");
        AssertStatusBarBinding(statusBar, "InstallPipelineValue");
        AssertStatusBarBinding(statusBar, "InstallPipelineCanCancel");
        AssertStatusBarBinding(statusBar, "IsPlaylistSyncProgressActive");
        AssertStatusBarBinding(statusBar, "PlaylistSyncProgressLabel");
        AssertStatusBarBinding(statusBar, "PlaylistSyncProgressSubLabel");
        AssertStatusBarBinding(statusBar, "PlaylistSyncProgressMaximum");
        AssertStatusBarBinding(statusBar, "PlaylistSyncProgressValue");
        AssertStatusBarBinding(statusBar, "IsMaintenanceRescanProgressActive");
        AssertStatusBarBinding(statusBar, "MaintenanceRescanLabel");
        AssertStatusBarBinding(statusBar, "MaintenanceRescanSubLabel");
        AssertStatusBarBinding(statusBar, "MaintenanceRescanMaximum");
        AssertStatusBarBinding(statusBar, "MaintenanceRescanValue");
        AssertStatusBarBinding(statusBar, "MaintenanceRescanCanCancel");
        AssertStatusBarBinding(statusBar, "IsFolderAutoRenameProgressActive");
        AssertStatusBarBinding(statusBar, "FolderAutoRenameProgressLabel");
        AssertStatusBarBinding(statusBar, "FolderAutoRenameProgressSubLabel");
        AssertStatusBarBinding(statusBar, "FolderAutoRenameProgressMaximum");
        AssertStatusBarBinding(statusBar, "FolderAutoRenameProgressValue");
        AssertStatusBarBinding(statusBar, "IsLr2SongDbSyncStatusActive");
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusLabel");
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusSubLabel");
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusToolTip");
        AssertStatusBarBinding(statusBar, "IsLr2SongDbSyncRetryVisible");
        AssertStatusBarBinding(statusBar, "IsLr2SongDbSyncCancelVisible");
        AssertStatusBarBinding(statusBar, "IsLr2SongDbSyncCleanupVisible");
        AssertStatusBarBinding(statusBar, "IsLr2SongDbSyncStatusProgressVisible");
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsStartupProgressActive"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string StartupProgressLabel"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string StartupProgressSubLabel"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public double StartupProgressValue"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public double StartupProgressMaximum"));
        StringAssert.Contains(startupProgressOwnerCode, "ApplyPresentation(isActive, label, subLabel, value, maximum);");
        Assert.IsFalse(mainWindowViewModelCode.Contains("UpdateStartupProgress(isActive"));
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusProgressMaximum");
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusProgressValue");
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsLr2SongDbSyncStatusActive"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string Lr2SongDbSyncStatusLabel"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string Lr2SongDbSyncStatusSubLabel"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string Lr2SongDbSyncStatusToolTip"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public double Lr2SongDbSyncStatusProgressValue"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public double Lr2SongDbSyncStatusProgressMaximum"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsLr2SongDbSyncStatusProgressVisible"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsLr2SongDbSyncRetryVisible"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsLr2SongDbSyncCancelVisible"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsLr2SongDbSyncCleanupVisible"));
        StringAssert.Contains(mainWindowViewModelCode, "ProgressHub.UpdateLr2SongDbSyncStatus(");
        string startupProgressRecompute = ExtractMethodBody(
            startupProgressOwnerCode,
            "internal void RecomputeStartupProgressPresentation(long expectedOperationToken = 0L)");
        StringAssert.Contains(startupProgressRecompute, "ApplyPresentation(isActive, label, subLabel, value, maximum);");
        Assert.IsFalse(startupProgressRecompute.Contains("ProgressHub."));
        string progressHubHandler = ExtractMethodBody(
            mainWindowViewModelCode,
            "private void StartupProgressWorkflowOwnerPropertyChanged(object sender, PropertyChangedEventArgs e)");
        Assert.IsFalse(progressHubHandler.Contains("RaisePropertyChanged(propertyName)"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("ShouldShowLr2SongDbSyncStatusForTest"));

        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cancelDropInstallQueueClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cancelMaintenanceRescanClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"retryLr2SongDbSyncClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cancelLr2SongDbSyncClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cleanupLr2SongDbSyncStartupScanBlockersClick\""));
        string retryHandler = ExtractMethodBody(mainWindowCode, "private void retryLr2SongDbSyncClick");
        string cancelHandler = ExtractMethodBody(mainWindowCode, "private void cancelLr2SongDbSyncClick");
        string cleanupHandler = ExtractMethodBody(mainWindowCode, "private void cleanupLr2SongDbSyncStartupScanBlockersClick");
        StringAssert.Contains(retryHandler, ".Lr2SongDbSyncWorkflow.RequestStatusBarRetry();");
        StringAssert.Contains(cancelHandler, ".Lr2SongDbSyncWorkflow.CancelStatusBarSync();");
        StringAssert.Contains(cleanupHandler, ".Lr2SongDbSyncWorkflow.CleanupStartupScanBlockersAndRetry();");
        Assert.AreEqual(1, CountOccurrences(retryHandler, ".Lr2SongDbSyncWorkflow."));
        Assert.AreEqual(1, CountOccurrences(cancelHandler, ".Lr2SongDbSyncWorkflow."));
        Assert.AreEqual(1, CountOccurrences(cleanupHandler, ".Lr2SongDbSyncWorkflow."));
        Assert.IsFalse(mainWindowCode.Contains("RequestLr2SongDbSync("));
        Assert.IsFalse(mainWindowCode.Contains("CleanupLr2SongDbSyncStartupScanBlockersAndRetry()"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public bool IsPlaylistSyncProgressActive"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string PlaylistSyncProgressLabel"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public string PlaylistSyncProgressSubLabel"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public double PlaylistSyncProgressValue"));
        Assert.IsFalse(mainWindowViewModelCode.Contains("public double PlaylistSyncProgressMaximum"));
        StringAssert.Contains(progressHubCode, "private void UpdatePlaylistSyncProgress(PlaylistSyncProgressSnapshot snapshot)");
        StringAssert.Contains(progressHubCode, "PlaylistWorkspacePlaylistSyncProgressChanged");
    }

    [TestMethod]
    public void StartupInteractionBlock_UsesProgressOwnerAndTypedShutdownBoundary()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelSource = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string startupProgressSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "StartupProgressWorkflowOwner.cs");
        string shutdownOwnerSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "ShellShutdownWorkflowOwner.cs");

        StringAssert.Contains(startupProgressSource, "public bool IsStartupUiInteractionBlocked");
        StringAssert.Contains(startupProgressSource, "internal void SetStartupUiInteractionBlocked(bool value)");
        StringAssert.Contains(shutdownOwnerSource, "StartupProgressWorkflowOwner startupProgressWorkflowOwner");
        StringAssert.Contains(shutdownOwnerSource, "startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(true)");
        StringAssert.Contains(shutdownOwnerSource, "startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false)");
        StringAssert.Contains(viewModelSource, "startupProgressWorkflowOwner.IsStartupUiInteractionBlocked");
        StringAssert.Contains(viewModelSource, "nameof(StartupProgressWorkflowOwner.IsStartupUiInteractionBlocked)");
        StringAssert.Contains(mainWindowSource, "viewModel.ProgressHub.StartupProgress.IsStartupUiInteractionBlocked");
        Assert.IsFalse(rootViewModelSource.Contains("public bool IsStartupUiInteractionBlocked"));
        Assert.IsFalse(rootViewModelSource.Contains("internal void SetStartupUiInteractionBlocked"));
        Assert.IsFalse(mainWindowSource.Contains("viewModel.IsStartupUiInteractionBlocked"));
        Assert.IsFalse(mainWindowSource.Contains("SetStartupUiInteractionBlocked"));
    }

    [TestMethod]
    public void SidebarLayout_SplittersCoverResizableRegionsAndExposeWideHitAreas()
    {
        XDocument document = LoadMainWindowXamlDocument();
        XElement splitterStyle = FindElementByAttribute(document, "Key", "SidebarSplitterStyle");
        List<XElement> sidebarSplitters = FindElementsByAttribute(document, "Style", "{StaticResource SidebarSplitterStyle}").ToList();
        XElement verticalSplitter = FindElementByAttribute(document, "Name", "gridSplitter");
        XElement treePane = FindElementByAttribute(document, "Name", "gridTreePane");
        XElement horizontalSplitter = FindElementByAttribute(document, "Name", "gridSplitterTree");
        XElement separatorLine = FindElementByAttribute(splitterStyle, "Name", "SeparatorLine");

        Assert.AreEqual(2, sidebarSplitters.Count, "Only the sidebar width splitter and sidebar tree splitter should use the shared splitter hit-test style.");
        Assert.IsTrue(sidebarSplitters.All(element => element.Name.LocalName == "GridSplitter"));
        Assert.IsNotNull(
            splitterStyle.Descendants().FirstOrDefault(element => element.Name.LocalName == "Grid" && GetAttributeValue(element, "Background") == "Transparent"),
            "The splitter template root must be transparent so the full control bounds are hit-testable.");

        Assert.AreEqual(GetAttributeValue(treePane, "Grid.Row"), GetAttributeValue(verticalSplitter, "Grid.Row"));
        Assert.AreEqual(GetAttributeValue(treePane, "Grid.RowSpan"), GetAttributeValue(verticalSplitter, "Grid.RowSpan"));
        Assert.IsTrue(
            GetNumericAttribute(verticalSplitter, "Width") > GetNumericAttribute(separatorLine, "Width"),
            "The sidebar width splitter hit area must be wider than the visible vertical separator line.");

        XElement rowDefinitions = DirectChild(treePane, "Grid.RowDefinitions");
        List<XElement> rows = rowDefinitions.Elements().Where(element => element.Name.LocalName == "RowDefinition").ToList();
        XElement playlistTree = FindElementByAttribute(document, "Name", "treeViewPlaylist");
        XElement libraryTree = FindElementByAttribute(document, "Name", "treeView");
        int splitterRow = int.Parse(GetAttributeValue(horizontalSplitter, "Grid.Row"), CultureInfo.InvariantCulture);
        Assert.AreEqual(2, rows.Count, "The tree splitter hit area must be overlaid at the boundary instead of consuming a dedicated layout row.");
        Assert.AreEqual("0", GetAttributeValue(playlistTree, "Grid.Row"));
        Assert.AreEqual("1", GetAttributeValue(libraryTree, "Grid.Row"));
        Assert.AreEqual(GetAttributeValue(libraryTree, "Grid.Row"), GetAttributeValue(horizontalSplitter, "Grid.Row"));
        Assert.AreEqual("Top", GetAttributeValue(horizontalSplitter, "VerticalAlignment"));
        Assert.AreEqual("0,-2,0,0", GetAttributeValue(horizontalSplitter, "Margin"));
        Assert.IsTrue(
            GetNumericAttribute(horizontalSplitter, "Height") > ResolveRowSeparatorLineHeight(splitterStyle),
            "The sidebar tree splitter hit area must be taller than the visible horizontal separator line.");

        Assert.IsTrue(GetNumericAttribute(FindElementByAttribute(document, "Name", "gridColumn0"), "MinWidth") > GetNumericAttribute(verticalSplitter, "Width"));
        Assert.AreEqual("CurrentAndNext", GetAttributeValue(verticalSplitter, "ResizeBehavior"));
        Assert.AreEqual("PreviousAndCurrent", GetAttributeValue(horizontalSplitter, "ResizeBehavior"));
        Assert.AreEqual("True", GetAttributeValue(document.Root, "UseLayoutRounding"));
        Assert.AreEqual("True", GetAttributeValue(document.Root, "SnapsToDevicePixels"));
    }

    [TestMethod]
    public void CustomTableScrollBars_UseThemeThicknessAndCornerFiller()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "CustomTableView.cs"));

        StringAssert.Contains(source, "private const double ScrollBarThickness = 15d;");
        StringAssert.Contains(source, "FocusVisualStyle = null;");
        StringAssert.Contains(source, "UseLayoutRounding = true;");
        StringAssert.Contains(source, "SnapsToDevicePixels = true;");
        StringAssert.Contains(source, "SetResourceReference(Panel.BackgroundProperty, \"ScrollBar.TrackBackgroundBrush\")");
        StringAssert.Contains(source, "Width = ScrollBarThickness");
        StringAssert.Contains(source, "Height = ScrollBarThickness");
        StringAssert.Contains(source, "scrollBarCorner.SetResourceReference(Border.BackgroundProperty, \"ScrollBar.TrackBackgroundBrush\")");
        StringAssert.Contains(source, "SetColumn(scrollBarCorner, 1);");
        StringAssert.Contains(source, "SetRow(scrollBarCorner, 1);");
        StringAssert.Contains(source, "scrollBarCorner.Visibility = verticalScrollBar.Visibility == Visibility.Visible && horizontalScrollBar.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;");
    }

    [TestMethod]
    public void AppStyles_SuppressDottedFocusVisuals()
    {
        string root = FindRepositoryRoot();
        string styles = File.ReadAllText(Path.Combine(root, "Simple Styles.xaml")).Replace("\r\n", "\n");
        string mainWindow = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml")).Replace("\r\n", "\n");

        Assert.AreEqual(-1, styles.IndexOf("StrokeDashArray", StringComparison.Ordinal));
        Assert.AreEqual(-1, mainWindow.IndexOf("TreeViewItemFocusVisual", StringComparison.Ordinal));
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleButton\" TargetType=\"{x:Type Button}\" BasedOn=\"{x:Null}\">\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleCheckBox\" TargetType=\"{x:Type CheckBox}\">\n    <Setter Property=\"UIElement.SnapsToDevicePixels\" Value=\"True\" />\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleRadioButton\" TargetType=\"{x:Type RadioButton}\">\n    <Setter Property=\"UIElement.SnapsToDevicePixels\" Value=\"True\" />\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleListBoxItem\" TargetType=\"{x:Type ListBoxItem}\">\n    <Setter Property=\"UIElement.SnapsToDevicePixels\" Value=\"True\" />\n    <Setter Property=\"FrameworkElement.OverridesDefaultStyle\" Value=\"True\" />\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleComboBox\" TargetType=\"{x:Type ComboBox}\">\n    <Setter Property=\"UIElement.SnapsToDevicePixels\" Value=\"True\" />\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleComboBoxItem\" TargetType=\"{x:Type ComboBoxItem}\">\n    <Setter Property=\"UIElement.SnapsToDevicePixels\" Value=\"True\" />\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleTabItem\" TargetType=\"{x:Type TabItem}\">\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleSlider\" TargetType=\"{x:Type Slider}\">\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style x:Key=\"SimpleTreeView\" TargetType=\"{x:Type TreeView}\">\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(styles, "<Style TargetType=\"{x:Type ToggleButton}\">\n    <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(mainWindow, "<TreeView Name=\"treeViewPlaylist\" Grid.Row=\"0\" ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\" VirtualizingStackPanel.IsVirtualizing=\"True\" VirtualizingStackPanel.VirtualizationMode=\"Recycling\" BorderThickness=\"0\" Padding=\"1,5\" Background=\"{DynamicResource App.BackgroundBrush}\" Foreground=\"{DynamicResource App.TextBrush}\" FocusVisualStyle=\"{x:Null}\"");
        StringAssert.Contains(mainWindow, "<TreeView Name=\"treeView\" Grid.Row=\"1\" ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\" VirtualizingStackPanel.IsVirtualizing=\"True\" VirtualizingStackPanel.VirtualizationMode=\"Recycling\" BorderThickness=\"0\" Padding=\"1,5\" Background=\"{DynamicResource App.BackgroundBrush}\" Foreground=\"{DynamicResource App.TextBrush}\" FocusVisualStyle=\"{x:Null}\"");
        StringAssert.Contains(mainWindow, "<Style x:Key=\"SidebarSplitterStyle\" TargetType=\"{x:Type GridSplitter}\">\n        <Setter Property=\"Focusable\" Value=\"False\" />\n        <Setter Property=\"IsTabStop\" Value=\"False\" />\n        <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(mainWindow, "<Style x:Key=\"SliderStylePlayer\" TargetType=\"{x:Type Slider}\">\n        <Setter Property=\"Stylus.IsPressAndHoldEnabled\" Value=\"False\" />\n        <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(mainWindow, "<ToggleButton x:Name=\"KeywordSearchHelpButton\" Width=\"18\" Height=\"20\" Padding=\"0\" BorderThickness=\"0\" Background=\"Transparent\" Foreground=\"{DynamicResource App.AccentBrush}\" FocusVisualStyle=\"{x:Null}\"");
        StringAssert.Contains(mainWindow, "<ToggleButton x:Name=\"PlaylistSummaryKeywordSearchHelpButton\" Width=\"18\" Height=\"20\" Padding=\"0\" BorderThickness=\"0\" Background=\"Transparent\" Foreground=\"{DynamicResource App.AccentBrush}\" FocusVisualStyle=\"{x:Null}\"");

        foreach (Match match in Regex.Matches(mainWindow, "<Style TargetType=\"\\{x:Type TreeViewItem\\}\">(?<body>.*?)</Style>", RegexOptions.Singleline))
        {
            StringAssert.Contains(match.Groups["body"].Value, "FocusVisualStyle\" Value=\"{x:Null}\"");
        }
    }

    [TestMethod]
    public void SimpleScrollViewer_FillsScrollBarCorner()
    {
        string styles = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Simple Styles.xaml"));

        StringAssert.Contains(styles, "<Border Grid.Column=\"1\" Grid.Row=\"1\" Background=\"{DynamicResource ScrollBar.TrackBackgroundBrush}\" />");
        StringAssert.Contains(styles, "Name=\"PART_HorizontalScrollBar\" Visibility=\"{TemplateBinding ScrollViewer.ComputedHorizontalScrollBarVisibility}\" Grid.Column=\"0\" Grid.Row=\"1\" Height=\"15\"");
        StringAssert.Contains(styles, "Name=\"PART_VerticalScrollBar\" Visibility=\"{TemplateBinding ScrollViewer.ComputedVerticalScrollBarVisibility}\" Grid.Column=\"1\" Grid.Row=\"0\" Width=\"15\"");
        StringAssert.Contains(styles, "<Thumb Style=\"{DynamicResource SimpleThumbStyle}\" />");
        StringAssert.Contains(styles, "HorizontalAlignment=\"Center\"");
        Assert.IsFalse(styles.Contains("HorizontalContentAlignment=\"Right\""));
        Assert.IsFalse(styles.Contains("TargetName=\"PART_Thumb\""));
        string simpleTreeView = styles.Substring(styles.IndexOf("x:Key=\"SimpleTreeView\"", StringComparison.Ordinal), 900);
        StringAssert.Contains(simpleTreeView, "BorderThickness=\"{TemplateBinding Control.BorderThickness}\"");
        StringAssert.Contains(simpleTreeView, "BorderBrush=\"{TemplateBinding Control.BorderBrush}\"");
        StringAssert.Contains(simpleTreeView, "Padding=\"{TemplateBinding Control.Padding}\"");
    }

    [TestMethod]
    public void SimpleScrollBar_UsesNativeHorizontalTrack()
    {
        string styles = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Simple Styles.xaml"));
        string verticalTemplate = ExtractBetween(styles, "<ControlTemplate x:Key=\"SimpleVerticalScrollBarTemplate\"", "<ControlTemplate x:Key=\"SimpleHorizontalScrollBarTemplate\"");
        string horizontalTemplate = ExtractBetween(styles, "<ControlTemplate x:Key=\"SimpleHorizontalScrollBarTemplate\"", "<Style x:Key=\"SimpleScrollBar\"");
        string scrollBarStyle = ExtractBetween(styles, "<Style x:Key=\"SimpleScrollBar\"", "<Style TargetType=\"{x:Type ScrollBar}\"");

        StringAssert.Contains(verticalTemplate, "<Track Name=\"PART_Track\" Grid.Row=\"1\" Orientation=\"Vertical\" IsDirectionReversed=\"True\">");
        StringAssert.Contains(horizontalTemplate, "<Track Name=\"PART_Track\" Grid.Column=\"1\" Orientation=\"Horizontal\">");
        StringAssert.Contains(horizontalTemplate, "Command=\"ScrollBar.LineLeftCommand\"");
        StringAssert.Contains(horizontalTemplate, "Command=\"ScrollBar.LineRightCommand\"");
        StringAssert.Contains(horizontalTemplate, "Command=\"ScrollBar.PageLeftCommand\"");
        StringAssert.Contains(horizontalTemplate, "Command=\"ScrollBar.PageRightCommand\"");
        StringAssert.Contains(horizontalTemplate, "<Thumb Style=\"{DynamicResource SimpleHorizontalThumbStyle}\" />");
        StringAssert.Contains(scrollBarStyle, "<Setter Property=\"Template\" Value=\"{StaticResource SimpleVerticalScrollBarTemplate}\" />");
        StringAssert.Contains(scrollBarStyle, "<Setter Property=\"Template\" Value=\"{StaticResource SimpleHorizontalScrollBarTemplate}\" />");
        Assert.IsFalse(horizontalTemplate.Contains("RotateTransform"));
        Assert.IsFalse(scrollBarStyle.Contains("LayoutTransform"));
    }

    [TestMethod]
    public void ContextMenuTemplates_ConstrainTallMenusWithScrollViewer()
    {
        string styles = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Simple Styles.xaml"));

        StringAssert.Contains(styles, "<views:MenuMaxHeightConverter x:Key=\"MenuMaxHeightConverter\" />");
        StringAssert.Contains(styles, "<Setter Property=\"MaxHeight\" Value=\"{Binding Source={x:Static SystemParameters.WorkArea}, Path=Height, Converter={StaticResource MenuMaxHeightConverter}}\" />");
        StringAssert.Contains(styles, "VerticalScrollBarVisibility=\"Auto\" CanContentScroll=\"False\" MaxHeight=\"{TemplateBinding MaxHeight}\"");
        StringAssert.Contains(styles, "Name=\"SubMenu\" Background=\"{DynamicResource App.PopupBackgroundBrush}\" Grid.IsSharedSizeScope=\"True\" MaxHeight=\"{Binding Source={x:Static SystemParameters.WorkArea}, Path=Height, Converter={StaticResource MenuMaxHeightConverter}}\"");
        StringAssert.Contains(styles, "VerticalScrollBarVisibility=\"Auto\" CanContentScroll=\"False\" MaxHeight=\"{Binding ElementName=SubMenu, Path=MaxHeight}\"");

        var converter = new MenuMaxHeightConverter();

        Assert.AreEqual(576d, converter.Convert(768d, typeof(double), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(160d, converter.Convert(double.NaN, typeof(double), null, CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void PlaylistTableListImportMenu_StaysOpenOnlyForLeafItemsAndUsesQueue()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string code = SourceTextTestHelper.ReadMainWindowSourceText();
        string dialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));
        string dialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.xaml"));
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));

        string menuSnippet = xaml.Substring(xaml.IndexOf("Name=\"treeViewPlaylistRootContextMenuItemLoadPlaylistCollection\"", StringComparison.Ordinal), 1200);
        StringAssert.Contains(menuSnippet, "<Setter Property=\"MenuItem.StaysOpenOnClick\" Value=\"True\" />");
        StringAssert.Contains(menuSnippet, "<DataTrigger Binding=\"{Binding url}\" Value=\"{x:Null}\">");
        StringAssert.Contains(menuSnippet, "<Setter Property=\"MenuItem.StaysOpenOnClick\" Value=\"False\" />");
        StringAssert.Contains(menuSnippet, "ItemsSource=\"{Binding PlaylistWorkspace.BMSExternalTableListExt.Children}\"");
        Assert.IsFalse(menuSnippet.Contains("ItemsSource=\"{Binding BMSExternalTableListExt.Children}\""));
        string collectionHandler = SourceTextTestHelper.ExtractMethodBody(
            code,
            "private void treeViewPlaylistRootContextMenuItemLoadPlaylistCollectionClick(");
        string walkureHandler = SourceTextTestHelper.ExtractMethodBody(
            code,
            "private void treeViewPlaylistRootContextMenuItemLoadWalkureTableClick(");
        StringAssert.Contains(collectionHandler, "viewModel.PlaylistWorkspace.TryEnqueueExternalPlaylistCollectionImport(menuItem.DataContext as BMSTableSimple);");
        StringAssert.Contains(walkureHandler, "viewModel.PlaylistWorkspace.TryEnqueueBuiltInExternalPlaylistImport((string)menuItem.Tag);");
        Assert.AreEqual(-1, collectionHandler.IndexOf("IsWriteLockHeldBMSTablesInitializeMin", StringComparison.Ordinal));
        Assert.AreEqual(-1, walkureHandler.IndexOf("new Uri((string)menuItem.Tag)", StringComparison.Ordinal));
        StringAssert.Contains(dialogCode, "SubmitExternalPlaylistUriText(textBoxURIInput.Text)");
        Assert.AreEqual(-1, dialogCode.IndexOf("EnqueueExternalPlaylistBMSTableImports(", StringComparison.Ordinal));
        Assert.AreEqual(-1, dialogCode.IndexOf("ParsePlaylistUriInput(", StringComparison.Ordinal));
        StringAssert.Contains(dialogCode, "AppendUriInputLine(textBoxURIInput.Text, result.FileName)");
        StringAssert.Contains(dialogXaml, "AcceptsReturn=\"True\"");
        StringAssert.Contains(dialogXaml, "VerticalContentAlignment=\"Top\"");
        StringAssert.Contains(dialogXaml, "VerticalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(dialogXaml, "HorizontalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(resources, "Playlist_import_progress_label_format");
        StringAssert.Contains(resources, "Playlist_import_progress_single_label");
        StringAssert.Contains(resources, "Playlist_import_progress_phase_load_tables");
        StringAssert.Contains(resources, "Playlist_import_progress_phase_check_duplicates");
        StringAssert.Contains(resources, "Playlist_import_progress_phase_register_playlists");
        StringAssert.Contains(resources, "Playlist_import_progress_phase_update_references");
        StringAssert.Contains(resources, "Playlist_import_progress_phase_finish");
        StringAssert.Contains(resources, "Playlist_uri_input_no_valid_uri");
        StringAssert.Contains(resources, "Playlist_uri_input_invalid_lines_format");
        foreach (string languageFile in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            string languageJson = File.ReadAllText(languageFile);
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_label_format\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_single_label\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_phase_load_tables\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_phase_check_duplicates\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_phase_register_playlists\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_phase_update_references\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_phase_finish\"");
            StringAssert.Contains(languageJson, "\"Playlist_uri_input_no_valid_uri\"");
            StringAssert.Contains(languageJson, "\"Playlist_uri_input_invalid_lines_format\"");
        }
    }

    [TestMethod]
    public void DropInstallQueueProgressStrings_AreLocalized()
    {
        string root = FindRepositoryRoot();
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        const string key = "Drop_install_queue_extracting_sub_label_format";

        StringAssert.Contains(resources, "name=\"" + key + "\"");
        StringAssert.Contains(resourceCode, key);
        foreach (string languageFile in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            string languageJson = File.ReadAllText(languageFile);
            StringAssert.Contains(languageJson, "\"" + key + "\"");
        }
    }

    [TestMethod]
    public void SettingDialogBeatorajaScoreDbStrings_AreLocalized()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string[] keys =
        [
            "Beatoraja_integration",
            "FilePath_beatoraja_root",
            "Use_beatoraja_scoreDB",
            "Beatoraja_player",
            "Use_beatoraja_bmt_output",
            "Beatoraja_bmt_hash_output_mode",
            "Beatoraja_bmt_hash_output_original",
            "Beatoraja_bmt_hash_output_fill_missing",
            "Beatoraja_bmt_hash_output_prefer_sha256_only",
            "Keep_beatoraja_bmt_files_when_output_disabled",
            "Register_beatoraja_bmt_urls",
            "Error_InvalidBeatorajaRootPath",
            "Error_InvalidBeatorajaScoreDbPath"
        ];
        foreach (string key in keys)
        {
            StringAssert.Contains(resources, "name=\"" + key + "\"");
            StringAssert.Contains(resourceCode, key);
            foreach (string languageFile in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
            {
                string languageJson = File.ReadAllText(languageFile);
                StringAssert.Contains(languageJson, "\"" + key + "\"");
            }
        }
        StringAssert.Contains(xaml, "Path=Resources.Beatoraja_integration");
        StringAssert.Contains(xaml, "Path=Resources.FilePath_beatoraja_root");
        StringAssert.Contains(xaml, "Path=Resources.Use_beatoraja_scoreDB");
        StringAssert.Contains(xaml, "Path=Resources.Beatoraja_player");
        StringAssert.Contains(xaml, "Path=Resources.Use_beatoraja_bmt_output");
        StringAssert.Contains(xaml, "Path=Resources.Beatoraja_bmt_hash_output_mode");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding BeatorajaBmtHashOutputModeOptions}\"");
        StringAssert.Contains(xaml, "SelectedValue=\"{Binding BeatorajaBmtHashOutputMode, Mode=TwoWay}\"");
        StringAssert.Contains(xaml, "Path=Resources.Keep_beatoraja_bmt_files_when_output_disabled");
        StringAssert.Contains(xaml, "Path=Resources.Register_beatoraja_bmt_urls");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidBeatorajaRootPath");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidBeatorajaScoreDbPath");
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        StringAssert.Contains(viewModelCode, "UninstallLr2PlayHistorySchemaAsync()");
        Assert.IsFalse(settingDialogCode.Contains("ReloadTables()"));
        Assert.IsFalse(xaml.Contains("Content=\"beatoraja"));
        Assert.IsFalse(xaml.Contains("Title=\"score.db"));
        Assert.IsFalse(xaml.Contains("Filter=\"score.db|score.db"));
    }

    [TestMethod]
    public void SettingDialogGeneralAndPlaylistGroups_AreSeparatedByFeature()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string resources = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Properties", "Resources.cs"));
        string settingsCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Properties", "Settings.cs"));
        string generalTab = ExtractBetween(
            xaml,
            "Name=\"tabItemGeneral\"",
            "<TabItem Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Appearance");
        string playlistTab = ExtractBetween(
            xaml,
            "Path=Resources.Playlist, Mode=OneWay}\">",
            "<TabItem Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Install");

        StringAssert.Contains(generalTab, "Path=Resources.Operation_Mode");
        StringAssert.Contains(generalTab, "Path=Resources.Language");
        StringAssert.Contains(generalTab, "Path=Resources.Lr2_song_db_sync_data_resync");
        Assert.IsFalse(generalTab.Contains("Path=Resources.Enable_lr2_song_db_sync"));
        Assert.IsFalse(resources.Contains("name=\"Enable_lr2_song_db_sync\""));
        Assert.IsFalse(resourceCode.Contains("Enable_lr2_song_db_sync"));
        Assert.IsFalse(settingsCode.Contains("public bool EnableLR2SongDbSync"));
        foreach (string languageFile in Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "lang"), "*.json"))
        {
            string languageJson = File.ReadAllText(languageFile);
            Assert.IsFalse(languageJson.Contains("\"Enable_lr2_song_db_sync\""));
        }
        StringAssert.Contains(generalTab, "Path=Resources.Beatoraja_integration");
        Assert.IsTrue(generalTab.IndexOf("Path=Resources.Language", StringComparison.Ordinal) < generalTab.IndexOf("Path=Resources.Operation_Mode", StringComparison.Ordinal));
        Assert.IsTrue(generalTab.IndexOf("Path=Resources.Operation_Mode", StringComparison.Ordinal) < generalTab.IndexOf("Path=Resources.Beatoraja_integration", StringComparison.Ordinal));
        Assert.IsFalse(generalTab.Contains("Path=Resources.Appearance"));
        string detailsTab = ExtractBetween(
            xaml,
            "Name=\"tabItemProperty\"",
            "Name=\"tabItemVersionInfo\"");
        Assert.IsFalse(detailsTab.Contains("Path=Resources.Language"));
        Assert.IsTrue(xaml.IndexOf("Name=\"tabItemGeneral\"", StringComparison.Ordinal) < xaml.IndexOf("<TabItem Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Appearance", StringComparison.Ordinal));
        Assert.IsTrue(xaml.IndexOf("<TabItem Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Appearance", StringComparison.Ordinal) < xaml.IndexOf("<TabItem Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Playback", StringComparison.Ordinal));

        Assert.AreEqual(1, CountOccurrences(playlistTab, "Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Playlist_table_uri, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(playlistTab, "Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Playlist_md5_url_mapping_tsv_uri, Mode=OneWay}\""));
        Assert.AreEqual(0, CountOccurrences(playlistTab, "<Label Height=\"28\" Padding=\"0,6,0,0\" Content=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Playlist_md5_url_mapping_tsv_uri"));
        StringAssert.Contains(playlistTab, "Path=Resources.Playlist_output_desc");
        Assert.IsFalse(playlistTab.Contains("Path=Resources.Playlist_output_note"));
        StringAssert.Contains(playlistTab, "Path=Resources.Playlist_url_completion_enable");
        StringAssert.Contains(playlistTab, "Path=Resources.Playlist_url_completion_overwrite");
        StringAssert.Contains(playlistTab, "Path=Resources.Playlist_url_completion_stella_full_enable");
        StringAssert.Contains(playlistTab, "IsChecked=\"{Binding EnableStellaFullPlaylistUrlCompletion}\"");
        Assert.IsTrue(playlistTab.IndexOf("Path=Resources.Playlist_url_completion_enable", StringComparison.Ordinal) < playlistTab.IndexOf("Path=Resources.Playlist_url_completion_overwrite", StringComparison.Ordinal));
        Assert.IsTrue(playlistTab.IndexOf("Path=Resources.Playlist_url_completion_overwrite", StringComparison.Ordinal) < playlistTab.IndexOf("Path=Resources.Playlist_url_completion_stella_full_enable", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SettingDialogStandaloneBmsRoots_AreLocalizedAndImplemented()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string[] keys =
        [
            "Standalone_BMSDirectories",
            "Add_BMSDirectory",
            "Remove_BMSDirectory",
            "Error_InvalidStandaloneBmsRootPaths"
        ];
        foreach (string key in keys)
        {
            StringAssert.Contains(resources, "name=\"" + key + "\"");
            StringAssert.Contains(resourceCode, key);
            foreach (string languageFile in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
            {
                string languageJson = File.ReadAllText(languageFile);
                StringAssert.Contains(languageJson, "\"" + key + "\"");
            }
        }

        StringAssert.Contains(xaml, "Path=Resources.Standalone_BMSDirectories");
        StringAssert.Contains(xaml, "Path=Resources.Add_BMSDirectory");
        StringAssert.Contains(xaml, "Path=Resources.Remove_BMSDirectory");
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding AvailableBMSDirectories}\"");
        StringAssert.Contains(xaml, "SelectedItem=\"{Binding SelectedBmsSearchRootPath, Mode=TwoWay}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding RemoveDirCommand}\"");
        Assert.IsFalse(xaml.Contains("ToolTip=\"未実装\""));
        StringAssert.Contains(viewModelCode, "ApplicationSettings.StandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidStandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "searchRootRuntimePort.HasOwnedChartUnderRealPath(dir)");
        Assert.IsFalse(viewModelCode.Contains("libraryPort.BMSFiles != null && libraryPort.BMSFiles.Any"));
    }

    [TestMethod]
    public void StandaloneLibraryMode_UsesPortableSongDbAndBuildsPlaylist()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string compositionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        string standaloneDbCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "StandaloneLibraryDatabase.cs"));

        ApplicationPathSnapshot pathSnapshot = ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(root, "BeMusicSeeker.exe"));
        Assert.AreEqual(
            Path.Combine(root, "data", "song.db"),
            pathSnapshot.StandaloneSongDbPath);
        StringAssert.Contains(standaloneDbCode, "FileMode.OpenOrCreate");
        StringAssert.Contains(standaloneDbCode, "PlaylistPersistenceRepository.EnsureSchema(songDbPath)");
        StringAssert.Contains(viewModelCode, "StandaloneLibraryDatabase.EnsurePortableSongDb()");
        StringAssert.Contains(viewModelCode, "applicationComposition.CreateBmsPlaylist(");
        StringAssert.Contains(compositionCode, "libraryProfile.SongDbPath");
        StringAssert.Contains(viewModelCode, "files.SearchTargets.AddRange(libraryProfile.SearchRoots)");
        StringAssert.Contains(viewModelCode, "return [];");
        string propertySaveServiceCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistPropertySaveService.cs"));
        string saveFollowup = ExtractBetween(
            propertySaveServiceCode,
            "internal async Task ApplyPostSaveUpdatesAsync(PlaylistPropertySaveCommit commit)",
            "private static bool IsValid(");
        int headerCommitIndex = saveFollowup.IndexOf("store.CommitBMSTableHeaderToDB(table);", StringComparison.Ordinal);
        int fullCommitIndex = saveFollowup.IndexOf("store.CommitBMSTableWithEntriesToDB(table);", StringComparison.Ordinal);
        int detailRefreshIndex = saveFollowup.IndexOf("PlaylistPropertyEntriesChanged", StringComparison.Ordinal);
        int selectionReplaceIndex = saveFollowup.IndexOf("PlaylistPropertyReferenceTableReplaced", StringComparison.Ordinal);
        int folderSelectionRemapIndex = saveFollowup.IndexOf("PlaylistPropertyFolderSelectionRemapped", StringComparison.Ordinal);
        int referenceSortInvalidationIndex = saveFollowup.IndexOf("PlaylistPropertyReferenceSortInvalidationRequested", StringComparison.Ordinal);
        int syncResultIndex = saveFollowup.IndexOf("PlaylistPropertySyncResultReported", StringComparison.Ordinal);
        int lr2CustomFolderIndex = saveFollowup.IndexOf("if (settings.OperationModeLR2DB)", StringComparison.Ordinal);
        string externalReloadBlock = ExtractBetween(
            saveFollowup,
            "bool shouldReloadExternalPlaylist =",
            ";");
        Assert.IsTrue(headerCommitIndex >= 0);
        Assert.IsTrue(fullCommitIndex >= 0);
        Assert.IsTrue(detailRefreshIndex >= 0);
        Assert.IsTrue(selectionReplaceIndex >= 0);
        Assert.IsTrue(folderSelectionRemapIndex >= 0);
        Assert.IsTrue(referenceSortInvalidationIndex >= 0);
        Assert.IsTrue(syncResultIndex >= 0);
        Assert.IsTrue(selectionReplaceIndex < folderSelectionRemapIndex);
        Assert.IsTrue(folderSelectionRemapIndex < referenceSortInvalidationIndex);
        Assert.IsTrue(referenceSortInvalidationIndex < syncResultIndex);
        Assert.IsFalse(saveFollowup.Contains("IPlaylistPropertySaveInteraction"));
        StringAssert.Contains(saveFollowup, "if (prefixChanged && !externalResyncApplied)");
        Assert.IsFalse(
            externalReloadBlock.Contains("baseline.CompatPrefix"),
            "Changing the folder prefix is a local playlist property edit and must not trigger an external reload by itself.");
        Assert.IsTrue(lr2CustomFolderIndex > headerCommitIndex);
        Assert.IsTrue(lr2CustomFolderIndex > fullCommitIndex);
        Assert.IsFalse(viewModelCode.Contains("throw new NotImplementedException();"));
    }

    [TestMethod]
    public void Lr2PlaybackPlayer_IsIndependentFromLibraryOperationMode()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string compositionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string rootViewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string lr2PlaybackXaml = ExtractBetween(
            xaml,
            "Name=\"radioButtonPlayLR2body\"",
            "Path=Resources.Movie_playback");
        string checkValidation = ExtractBetween(
            viewModelCode,
            "public bool CheckValidation(out string errMsg)",
            "public async Task SaveSettings()");
        string initialize = ExtractBetween(
            rootViewModelCode,
            "internal async Task<bool> InitializeAsync()",
            "listenerForBMSLibrary = new PropertyChangedEventListener(files);");
        string saveFollowup = ExtractBetween(
            viewModelCode,
            "private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)",
            "public bool CheckValidation()");
        string lr2RootPathProperty = ExtractBetween(
            viewModelCode,
            "public string LR2RootPath",
            "public Dictionary<string, PlayerResolution> LR2bodyResolutions");
        string lr2RootPathGetter = ExtractBetween(
            lr2RootPathProperty,
            "get",
            "set");

        Assert.IsFalse(lr2PlaybackXaml.Contains("OperationModeLR2DB"));
        StringAssert.Contains(checkValidation, "else if (UsePlayerLR2body)");
        StringAssert.Contains(checkValidation, "if (!IsLR2PlayerRootPathValid())");
        Assert.IsFalse(checkValidation.Contains("OperationModeLR2DB && UsePlayerLR2body"));
        StringAssert.Contains(initialize, "applicationComposition.CreateBmsPlayer(");
        StringAssert.Contains(compositionCode, "if (startupSettings.UsePlayerLR2body && File.Exists(startupSettings.LR2bodyPath))");
        StringAssert.Contains(compositionCode, "new LR2body(startupSettings.LR2bodyPath, createLr2PlayerConfig(), playerSettingsGateway, externalPlayerProcessGateway)");
        Assert.IsFalse(initialize.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(saveFollowup, "playerFactoryPort.CreateBmsPlayerForSettings(StartupSettingsSnapshot.CreateCurrent(ApplicationSettings))");
        StringAssert.Contains(saveFollowup, "await playbackRuntimePort.ApplyPlayerSettingsAsync(replacementPlayer)");
        Assert.IsFalse(saveFollowup.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(rootViewModelCode, "private LR2Config CreateLR2PlayerConfig(StartupSettingsSnapshot startupSettings)");
        StringAssert.Contains(viewModelCode, "private bool IsLR2PlayerRootPathValid(string value)");
        Assert.IsFalse(lr2RootPathGetter.Contains("Settings.Default.LR2RootPath = null"));
        StringAssert.Contains(lr2RootPathGetter, "return ApplicationSettings.LR2RootPath;");
    }

    [TestMethod]
    public void SettingDialogOperationModeChange_ConfirmsAndRestartsAfterInitialization()
    {
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string rootViewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string operationModeProperty = ExtractBetween(
            viewModelCode,
            "public bool OperationModeLR2DB",
            "public bool CanUseLr2Features");
        string restartMethod = ExtractBetween(
            viewModelCode,
            "private void ConfirmAndRestartForOperationModeChange(bool value)",
            "public string LR2bodyPath");

        StringAssert.Contains(operationModeProperty, "return operationModeLR2DB;");
        StringAssert.Contains(operationModeProperty, "if (!statePort.HasActiveLibraryProfile)");
        StringAssert.Contains(operationModeProperty, "SetOperationModeSelection(value);");
        StringAssert.Contains(operationModeProperty, "return;");
        StringAssert.Contains(operationModeProperty, "ConfirmAndRestartForOperationModeChange(value);");
        Assert.IsFalse(operationModeProperty.Contains("Settings.Default.OperationModeLR2DB = value;"));
        StringAssert.Contains(rootViewModelCode, "public bool HasActiveLibraryProfile => hasActiveLibraryProfile;");
        StringAssert.Contains(rootViewModelCode, "hasActiveLibraryProfile = true;");
        StringAssert.Contains(restartMethod, "Resources.Confirm_RestartForOperationModeChange");
        StringAssert.Contains(restartMethod, "SaveOperationModeForRestart(value);");
        StringAssert.Contains(restartMethod, "RestartForOperationModeChangeAsync()");
        Assert.IsFalse(restartMethod.Contains("CheckValidation("));
        Assert.IsFalse(restartMethod.Contains("ReloadFileDiffAsync()"));
        Assert.IsFalse(restartMethod.Contains("ReloadScoresOnly()"));
    }

    [TestMethod]
    public void SettingDialogApplyClick_UsesOwnerCompletionRoute()
    {
        string root = FindRepositoryRoot();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string settingDialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string appCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "App.cs"));
        Assert.IsFalse(settingDialogCode.Contains("SaveAndClose"));
        Assert.IsFalse(settingDialogCode.Contains("CancelAndClose"));
        Assert.IsFalse(settingDialogCode.Contains("ShouldResetSettingsOnCancel"));
        Assert.IsFalse(settingDialogCode.Contains("ShouldCloseSettingsWithoutSave"));
        Assert.IsFalse(settingDialogCode.Contains("IsNeedRestartForSaveOrCancel"));
        StringAssert.Contains(settingDialogXaml, "Click=\"buttonOKClick\"");
        Assert.IsFalse(settingDialogXaml.Contains("Command=\"{Binding ApplyCommand}\""));
        StringAssert.Contains(settingDialogXaml, "Command=\"{Binding CancelCommand}\"");
        StringAssert.Contains(settingDialogXaml, "IsEnabled=\"{Binding IsEditCompletionEnabled}\"");
        Assert.IsFalse(settingDialogXaml.Contains("{Binding settingDialog."));
        StringAssert.Contains(mainWindowXaml, "<v:SettingDialog x:Name=\"settingDialog\" DataContext=\"{Binding SettingDialog}\"");
        StringAssert.Contains(mainWindowXaml, "PlaybackPanel=\"{Binding DataContext.PlaybackPanel, ElementName=window}\"");
        StringAssert.Contains(settingDialogXaml, "ElementName=settingDialog, Mode=OneWay");
        StringAssert.Contains(settingDialogXaml, "ElementName=settingDialog, Mode=TwoWay");
        StringAssert.Contains(settingDialogCode, "DependencyProperty PlaybackPanelProperty");
        StringAssert.Contains(settingDialogCode, "public PlaybackPanelViewModel PlaybackPanel");
        StringAssert.Contains(settingDialogCode, "private async void buttonOKClick(object sender, RoutedEventArgs e)");
        StringAssert.Contains(settingDialogCode, "await GetSettingDialogViewModel()");
        StringAssert.Contains(settingDialogCode, "ApplySettingsAsync()");
        StringAssert.Contains(settingDialogCode, "LoggingAndPropagate(\"buttonOKClick\")");
        Assert.IsFalse(mainWindowXaml.Contains("EventName=\"ContentRendered\""));
        string contentRendered = ExtractMethodBody(mainWindowCode, "private async void MainWindow_ContentRendered");
        StringAssert.Contains(contentRendered, "await initializationTask.LoggingAndPropagate(\"MainWindow_ContentRendered\")");
        StringAssert.Contains(contentRendered, "viewModel.ShellActivationWorkflow.ActivateRenderedShell(");
        StringAssert.Contains(contentRendered, "ApplyStartupInitialSelectionRequest,");
        StringAssert.Contains(mainWindowCode, "viewModel.PropertyChanged += MainWindowViewModel_PropertyChanged;");
        StringAssert.Contains(mainWindowCode, "subscribedViewModel.PropertyChanged -= MainWindowViewModel_PropertyChanged;");
        string initializationHandler = ExtractMethodBody(mainWindowCode, "private void MainWindowViewModel_PropertyChanged");
        StringAssert.Contains(initializationHandler, "nameof(MainWindowViewModel.IsInitializationCompleted)");
        StringAssert.Contains(initializationHandler, "!viewModel.IsInitializationCompleted");
        StringAssert.Contains(initializationHandler, "IsShellClosingOrClosed()");
        StringAssert.Contains(initializationHandler, "!ReferenceEquals(subscribedViewModel, viewModel)");
        StringAssert.Contains(initializationHandler, "viewModel.PlaybackPanel.AttachWindowHost(new Win32ExternalPlayerWindowHost(playbackPanelView.PlayerHostHandle));");
        StringAssert.Contains(initializationHandler, "playbackPanelView.EnsureSelectedSurfaceAvailable();");
        Assert.IsFalse(viewModelCode.Contains("InitializationSucceeded"));
        Assert.IsFalse(mainWindowCode.Contains("InitializationSucceeded"));
        Assert.IsFalse(viewModelCode.Contains("public async void InitializeAsync()"));
        Assert.IsFalse(viewModelCode.Contains("InitializeForSettingsAsync"));
        Assert.IsFalse(viewModelCode.Contains("ApplyCommand"));
        Assert.IsFalse(viewModelCode.Contains("ExecuteApplyCommand"));
        StringAssert.Contains(viewModelCode, "internal async Task ApplySettingsAsync()");
        StringAssert.Contains(viewModelCode, "await SaveSettingsForInitialInitialize();");
        StringAssert.Contains(viewModelCode, "await SaveSettings();");
        StringAssert.Contains(viewModelCode, "ClosePresentation();");
        StringAssert.Contains(viewModelCode, "bool initializationSucceeded = true;");
        StringAssert.Contains(viewModelCode, "initializationSucceeded = await statePort.InitializeLibraryAsync();");
        Assert.IsFalse(viewModelCode.Contains("MarkLibraryInitializationFailed"));
        StringAssert.Contains(viewModelCode, "if (initializationSucceeded)");
        StringAssert.Contains(viewModelCode, "IsEditCompletionInProgress = false;");
        Assert.IsFalse(settingDialogCode.Contains("settings_save_and_close"));
        Assert.IsFalse(settingDialogCode.Contains("SyncAppearanceThemeSelection"));
        Assert.IsFalse(settingDialogCode.Contains("firstStartupInitializationStarted"));
        StringAssert.Contains(appCode, "public Task RestartApplicationAsync()");
        StringAssert.Contains(appCode, "ReleaseSingleInstanceMutex();");
        StringAssert.Contains(appCode, "Environment.GetCommandLineArgs().Skip(1)");
    }

    [TestMethod]
    public void PlaylistDialogs_UseDirectWorkspaceComposition()
    {
        string root = FindRepositoryRoot();
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string loadPlaylistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(mainWindowXaml, "<v:SettingDialog x:Name=\"settingDialog\" DataContext=\"{Binding SettingDialog}\" PlaybackPanel=\"{Binding DataContext.PlaybackPanel, ElementName=window}\" PlaylistWorkspace=\"{Binding DataContext.PlaylistWorkspace, ElementName=window}\"");
        StringAssert.Contains(mainWindowXaml, "<v:LoadPlaylistURIDialog x:Name=\"loadPlaylistURIDialog\" DataContext=\"{Binding PlaylistWorkspace}\"");
        StringAssert.Contains(settingDialogCode, "DependencyProperty PlaylistWorkspaceProperty");
        StringAssert.Contains(settingDialogCode, "public PlaylistWorkspaceViewModel PlaylistWorkspace");
        StringAssert.Contains(settingDialogCode, "playlistWorkspace.StartBeatorajaTableUrlImport(");
        StringAssert.Contains(settingDialogCode, "playlistWorkspace.BackupPlaylistAsync(result.FileName)");
        StringAssert.Contains(settingDialogCode, "playlistWorkspace.RestorePlaylistBackupAsync(result.FileName)");
        Assert.IsFalse(settingDialogCode.Contains("mainWindowViewModel.PlaylistWorkspace.StartBeatorajaTableUrlImport("));
        Assert.IsFalse(settingDialogCode.Contains("viewModel.PlaylistWorkspace.BackupPlaylistAsync("));
        Assert.IsFalse(settingDialogCode.Contains("viewModel.PlaylistWorkspace.RestorePlaylistBackupAsync("));
        StringAssert.Contains(loadPlaylistCode, "base.DataContext is PlaylistWorkspaceViewModel playlistWorkspace");
        Assert.IsFalse(loadPlaylistCode.Contains("base.DataContext is MainWindowViewModel"));
        StringAssert.Contains(mainWindowCode, "PlaylistWorkspace.PlaylistOperationNotificationPresentationRequested += MainWindow_PlaylistWorkspacePlaylistOperationNotificationPresentationRequested;");
        StringAssert.Contains(mainWindowCode, "PlaylistWorkspace.BeatorajaTableUrlImportSummaryReady += MainWindow_PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady;");
        Assert.IsFalse(viewModelCode.Contains("PlaylistWorkspacePlaylistOperationNotificationPresentationRequested"));
        Assert.IsFalse(viewModelCode.Contains("PlaylistWorkspaceBeatorajaTableUrlImportSummaryReady"));
    }

    [TestMethod]
    public void FirstStartupValidationFailure_UsesInitialSetupLanguageDialog()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindow = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string initialDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml"));
        string initialDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml.cs"));
        string initialize = ExtractBetween(
            viewModelCode,
            "internal async Task<bool> InitializeAsync()",
            "private void PlaylistWorkspacePlaylistTablesPresentationChanged");
        string validationFailure = ExtractBetween(
            initialize,
            "if (!SettingDialog.CheckValidation(out string startupValidationErrorMessage))",
            "if (startupSettings.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync(startupSettings))");

        Assert.IsFalse(validationFailure.Contains("DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings,"));
        StringAssert.Contains(validationFailure, "SettingDialog?.RequestInitialSetupLanguageDialog();");
        StringAssert.Contains(validationFailure, "ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_init_settings_check");
        StringAssert.Contains(validationFailure, "SettingDialog?.RequestOpen()");
        Assert.IsTrue(validationFailure.IndexOf("RequestInitialSetupLanguageDialog", StringComparison.Ordinal) < validationFailure.IndexOf("Msg_init_settings_check", StringComparison.Ordinal));

        Assert.IsFalse(mainWindow.Contains("MessageKey=\"InitialSetupLanguageDialog\""));
        StringAssert.Contains(mainWindowCode, "void ISettingDialogPresentationPort.OpenInitialSetupLanguageDialog()");
        StringAssert.Contains(mainWindowCode, "RunOnUiThreadSynchronously(() => ShowOverlayDialog(initialSetupLanguageDialog))");
        StringAssert.Contains(mainWindowCode, "viewModel.SettingDialog.AttachPresentationPort(this);");
        StringAssert.Contains(mainWindow, "<v:InitialSetupLanguageDialog x:Name=\"initialSetupLanguageDialog\" DataContext=\"{Binding SettingDialog}\"");
        StringAssert.Contains(initialDialog, "ItemsSource=\"{Binding Languages, Mode=OneWay}\"");
        StringAssert.Contains(initialDialog, "SelectedItem=\"{Binding Path=Language}\"");
        StringAssert.Contains(initialDialog, "Resources.Msg_init_settings");
        StringAssert.Contains(initialDialog, "Resources.InitialSetupLanguageDialogTitle");
        StringAssert.Contains(initialDialog, "Resources.InitialSetupLanguageDialogContinue");
        Assert.IsFalse(initialDialogCode.Contains("MainWindow"));
        StringAssert.Contains(initialDialog, "Command=\"{Binding OpenCommand}\"");
        Assert.IsFalse(initialDialog.Contains("{Binding settingDialog."));
    }

    [TestMethod]
    public void InitialSetupPresentation_UsesSettingsPresentationPort()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        Assert.IsFalse(viewModelCode.Contains("InitialSetupLanguageDialogRequested"));
        Assert.IsFalse(viewModelCode.Contains("RaiseUiInteractionOnUiThread"));
        StringAssert.Contains(mainWindowCode, "void ISettingDialogPresentationPort.OpenInitialSetupLanguageDialog()");
        Assert.AreEqual(0, CountOccurrences(viewModelCode, "base.Messenger.Raise("));
        Assert.IsFalse(viewModelCode.Contains("RaiseInteractionMessageOnUiThread"));
        Assert.IsFalse(viewModelCode.Contains("new InteractionMessage"));
        Assert.IsFalse(viewModelCode.Contains("ownerViewModel.Messenger.Raise("));
        Assert.IsFalse(mainWindowCode.Contains(".Messenger.Raise("));
        StringAssert.Contains(mainWindowCode, "SubscribeViewModelUiInteractions(viewModel);");
        StringAssert.Contains(mainWindowCode, "UnsubscribeViewModelUiInteractions();");
    }

    [TestMethod]
    public void StartupReloadProgress_UsesSerializedOperationTokens()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string rootViewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string progressHubCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "OperationProgressHubViewModel.cs");
        string startupProgressOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "StartupProgressWorkflowOwner.cs");
        string compositionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        string reloadFileDiff = ExtractBetween(
            viewModelCode,
            "internal async Task ReloadFileDiffAsync()",
            "internal async Task ReinitializeLibraryAsync()");
        string reinitialize = ExtractBetween(
            viewModelCode,
            "internal async Task ReinitializeLibraryAsync()",
            "internal static string BuildAppSchemaRepairWarningMessage");
        string initialize = ExtractBetween(
            viewModelCode,
            "internal async Task<bool> InitializeAsync()",
            "private void PlaylistWorkspacePlaylistTablesPresentationChanged");
        string endSuppression = ExtractBetween(
            viewModelCode,
            "private void EndUiUpdateSuppression()",
            "private void RefreshLibraryMainViewForCurrentFilter");
        string externalSyncWorkspace = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs");

        StringAssert.Contains(viewModelCode, "public bool IsLibraryOperationInProgress");
        StringAssert.Contains(startupProgressOwnerCode, "internal long GetActiveStartupProgressOperationToken()");
        StringAssert.Contains(startupProgressOwnerCode, "internal bool IsStartupProgressOperationTokenCurrent(long operationToken)");
        StringAssert.Contains(progressHubCode, "playlistSyncProgressUiVersion");
        StringAssert.Contains(progressHubCode, "if (uiVersion != Interlocked.Read(ref playlistSyncProgressUiVersion)");
        StringAssert.Contains(progressHubCode, "|| isShellClosing()");
        StringAssert.Contains(progressHubCode, "dispatchPlaylistProgressAction(reflect);");
        Assert.IsFalse(mainWindowCode.Contains("playlistSyncProgressUiVersion"));
        Assert.IsFalse(rootViewModelCode.Contains("playlistSyncProgressUiVersion"));
        StringAssert.Contains(startupProgressOwnerCode, "startupProgressState.OperationKind == StartupProgressOperationKind.ReloadFileDiff");
        Assert.IsTrue(reloadFileDiff.IndexOf("await _semaphore.WaitAsync();", StringComparison.Ordinal) < reloadFileDiff.IndexOf("StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff)", StringComparison.Ordinal));
        StringAssert.Contains(reloadFileDiff, ".LoggingAndPropagate(\"ReloadFileDiff\")");
        Assert.IsTrue(
            reloadFileDiff.IndexOf("Lr2SongDbSyncWorkflow.QueueAfterReloadFileDiff(\"ReloadFileDiff\")", StringComparison.Ordinal)
            < reloadFileDiff.IndexOf("PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue(", StringComparison.Ordinal));
        Assert.IsTrue(
            reloadFileDiff.IndexOf("PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue(", StringComparison.Ordinal)
            < reloadFileDiff.IndexOf("MarkStartupProgressFailureCleanupComplete(operationToken)", StringComparison.Ordinal));
        StringAssert.Contains(reinitialize, ".LoggingAndPropagate(\"FullReinitialize\")");
        Assert.IsTrue(
            reinitialize.IndexOf("PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue(", StringComparison.Ordinal)
            < reinitialize.IndexOf("_semaphore.Release();", StringComparison.Ordinal));
        Assert.IsFalse(viewModelCode.Contains("public async void ReinitializeLibrary()"));
        Assert.IsTrue(initialize.IndexOf("applicationComposition.CreateBmsLibrary(libraryProfile)", StringComparison.Ordinal) < initialize.IndexOf("StartStartupProgressOperation(StartupProgressOperationKind.Startup)", StringComparison.Ordinal));
        StringAssert.Contains(initialize, "PlaylistWorkspace.QueueExternalPlaylistSync(");
        StringAssert.Contains(initialize, "queueBeatorajaBmtExportAfterHydration: startupSettings.SkipInitPlaylistLoad");
        StringAssert.Contains(initialize, "files.InitializeStartup(");
        StringAssert.Contains(initialize, "startupPerformanceInteraction);");
        StringAssert.Contains(initialize, "\"external_table_catalog\"");
        StringAssert.Contains(initialize, "PlaylistWorkspace.LoadExternalTableCollectionAsync(");
        StringAssert.Contains(initialize, "BMSPlaylist.GetBMSTableInfoAsync");
        Assert.IsTrue(
            initialize.IndexOf("\"external_table_catalog\"", StringComparison.Ordinal)
            < initialize.LastIndexOf("_semaphore.Release();", StringComparison.Ordinal));
        Assert.IsFalse(initialize.Contains("void taskAdd2()"));
        StringAssert.Contains(initialize, "() => files.CreateBeatorajaBmtSongHashResolver(),");
        StringAssert.Contains(initialize, "files.Lr2PlaylistFolderSynchronization);");
        StringAssert.Contains(compositionCode, "playlistUrlCompletionOptionsProvider,");
        StringAssert.Contains(compositionCode, "beatorajaBmtOptionsProvider,");
        StringAssert.Contains(compositionCode, "customFolderOutputSettingsProvider);");
        StringAssert.Contains(initialize, "if (startupSettings.OperationModeLR2DB && startupSettings.IsLR2BackupEnabled)");
        StringAssert.Contains(initialize, "Backup.SaveBackupsWithResult(startupSettings.LR2BackupPath, new TimeSpan(startupSettings.LR2BackupSpan, 0, 0, 0), startupSettings.LR2BackupNum, bkPaths)");
        StringAssert.Contains(initialize, "if (!startupSettings.SkipInitPlaylistLoad)");
        Assert.IsFalse(initialize.Contains("Settings.Default.IsLR2BackupEnabled"));
        Assert.IsFalse(initialize.Contains("Settings.Default.LR2BackupTarget"));
        Assert.IsFalse(initialize.Contains("Settings.Default.LR2BackupPath"));
        Assert.IsFalse(initialize.Contains("Settings.Default.LR2BackupSpan"));
        Assert.IsFalse(initialize.Contains("Settings.Default.LR2BackupNum"));
        Assert.IsFalse(initialize.Contains("Settings.Default.TableListURL"));
        StringAssert.Contains(externalSyncWorkspace, "currentPlaylists.BmtOutput.QueueBeatorajaBmtExportAll(\"DeferredExternalSync:\" + request.Reason)");
        StringAssert.Contains(externalSyncWorkspace, "currentPlaylists.ExternalSyncOwner.UpdateBMSTablesInternalAsync(");
        Assert.IsFalse(viewModelCode.Contains("StartDeferredExternalPlaylistSync("));
        StringAssert.Contains(initialize, "initialSetupCompletionMessagePending = true;");
        Assert.IsFalse(initialize.Contains("Resources.Msg_init_completed"));
        StringAssert.Contains(initialize, "await EnsureAppSchemaRepairApprovedForStartupAsync(startupSettings)");
        string appSchemaStartupPreflight = ExtractBetween(
            viewModelCode,
            "private async Task<bool> EnsureAppSchemaRepairApprovedForStartupAsync(StartupSettingsSnapshot startupSettings)",
            "private void ApplyAppSchemaRepairForStartupOrThrow");
        string gatewayCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryDbGateway.cs"));
        string repairAppOwnedSchema = ExtractBetween(
            gatewayCode,
            "internal static void RepairAppOwnedSchema(LR2SongDBExtended songDb)",
            "internal static void EnsureAppOwnedSchema(LR2SongDBExtended songDb)");
        StringAssert.Contains(appSchemaStartupPreflight, "ShowUiConfirmation(BuildAppSchemaRepairWarningMessage(preflightResult)");
        StringAssert.Contains(appSchemaStartupPreflight, "await Task.Run(delegate");
        StringAssert.Contains(appSchemaStartupPreflight, "ApplyAppSchemaRepairForStartupOrThrow(appSchemaPreflightService, preflightResult, startupSettings.LR2SongDBPath);");
        Assert.IsFalse(repairAppOwnedSchema.Contains("RepairChartDigestMapConsistency"));
        Assert.IsFalse(repairAppOwnedSchema.Contains("BMSFile.GetSHA256Hash"));
        StringAssert.Contains(viewModelCode, "private void ShowInitialSetupCompletionMessageIfPending(");
        StringAssert.Contains(viewModelCode, "long expectedOperationToken = 0L");
        StringAssert.Contains(viewModelCode, "ShowInitialSetupCompletionMessageIfPending(expectedOperationToken, requireBackgroundTasksIdle: true);");
        StringAssert.Contains(endSuppression, "FlushPendingUiRefresh(uiRefreshChannel, operationToken)");
        StringAssert.Contains(reloadFileDiff, "PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue(\"ReloadFileDiff\", operationToken);");
        StringAssert.Contains(reinitialize, "PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue(\"FullReinitialize\", operationToken);");
        Assert.IsFalse(viewModelCode.Contains("private void ScheduleDeferredPlaylistReferenceApply("));
    }

    [TestMethod]
    public void SettingDialogOperationModeRestartSave_SavesOnlyOperationMode()
    {
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string initialSaveMethod = ExtractBetween(
            viewModelCode,
            "public async Task SaveSettingsForInitialInitialize()",
            "private async Task SaveSettingsCore(bool runPostSaveActions)");
        string saveCore = ExtractBetween(
            viewModelCode,
            "private async Task SaveSettingsCore(bool runPostSaveActions)",
            "public void SaveOperationModeForRestart");
        string restartSaveMethod = ExtractBetween(
            viewModelCode,
            "public void SaveOperationModeForRestart(bool operationMode)",
            "public void ResetSettings()");

        StringAssert.Contains(initialSaveMethod, "SaveSettingsCore(runPostSaveActions: false)");
        StringAssert.Contains(initialSaveMethod, "backupSavedSettings();");
        Assert.IsFalse(initialSaveMethod.Contains("necessaryStepsAfterSaved"));
        StringAssert.Contains(saveCore, "if (lr2ConfigBoundaryChanged && operationModeLR2DB && lr2config != null)");
        StringAssert.Contains(saveCore, "customFolderOutputBaseJukeboxAdoptionNeeded = HasCustomFolderOutputBaseJukeboxAdoptionConflicts();");
        StringAssert.Contains(saveCore, "customFolderSearchRootSyncNeeded = lr2ConfigBoundaryChanged");
        StringAssert.Contains(saveCore, "|| customFolderOutputBaseSettingsChanged");
        StringAssert.Contains(saveCore, "|| customFolderOutputBaseJukeboxAdoptionNeeded;");
        StringAssert.Contains(saveCore, "postSaveImpact = BuildSettingsPostSaveImpact(customFolderSearchRootSyncNeeded);");
        StringAssert.Contains(saveCore, "snapshotRefreshScope = BuildSettingsSnapshotRefreshScope(");
        StringAssert.Contains(saveCore, "lr2ConfigBoundaryChanged);");
        StringAssert.Contains(saveCore, "\"settings_change_classification\"");
        StringAssert.Contains(saveCore, "if (postSaveNeeded)");
        StringAssert.Contains(saveCore, "await necessaryStepsAfterSaved(postSaveImpact);");
        StringAssert.Contains(saveCore, "backupSavedSettingsCore(snapshotRefreshScope);");
        StringAssert.Contains(saveCore, "\"settings_save\"");
        StringAssert.Contains(saveCore, "postSaveImpact=");
        StringAssert.Contains(saveCore, "backupSnapshotMs=");
        StringAssert.Contains(saveCore, "lr2ConfigNeedsSave = lr2config.EnsureDatabaseAutoReloadManualOnly();");
        StringAssert.Contains(saveCore, "if ((lr2SearchRootsChanged || lr2ConfigNeedsSave) && lr2config != null)");
        Assert.IsTrue(
            saveCore.IndexOf("if ((lr2SearchRootsChanged || lr2ConfigNeedsSave) && lr2config != null)", StringComparison.Ordinal)
            < saveCore.IndexOf("if (runPostSaveActions)", StringComparison.Ordinal),
            "LR2 config persistence, including autoreload normalization, must not be hidden behind runtime post-save actions.");
        StringAssert.Contains(restartSaveMethod, "settingsEditSession.Reload();");
        StringAssert.Contains(restartSaveMethod, "ApplicationSettings.OperationModeLR2DB = operationMode;");
        StringAssert.Contains(restartSaveMethod, "string playHistorySelectedDisplayTargetIdentity = playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity;");
        StringAssert.Contains(restartSaveMethod, "playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = playHistorySelectedDisplayTargetIdentity;");
        StringAssert.Contains(restartSaveMethod, "settingsEditSession.Save();");
        Assert.IsTrue(
            restartSaveMethod.IndexOf("string playHistorySelectedDisplayTargetIdentity = playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity;", StringComparison.Ordinal)
            < restartSaveMethod.IndexOf("settingsEditSession.Reload();", StringComparison.Ordinal),
            "Restart save must preserve the in-memory play-history display target before reloading settings.");
        Assert.IsTrue(
            restartSaveMethod.IndexOf("settingsEditSession.Reload();", StringComparison.Ordinal)
            < restartSaveMethod.IndexOf("playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = playHistorySelectedDisplayTargetIdentity;", StringComparison.Ordinal),
            "Restart save must restore the play-history display target after reloading settings.");
        Assert.IsFalse(restartSaveMethod.Contains("ResetSettings();"));
        Assert.IsFalse(restartSaveMethod.Contains("SetOperationModeSelection"));
        Assert.IsFalse(restartSaveMethod.Contains("RaisePropertyChanged"));
        Assert.IsFalse(restartSaveMethod.Contains("CheckValidation("));
        Assert.IsFalse(restartSaveMethod.Contains("necessaryStepsAfterSaved"));
    }

    [TestMethod]
    public void SettingDialogModeSpecificGetters_DoNotClearPersistedSettings()
    {
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string lr2CustomFolderGetter = ExtractBetween(
            viewModelCode,
            "public string LR2CustomFolderOutputDir",
            "public string BMSInstallDir");
        string bmsInstallDirGetter = ExtractBetween(
            viewModelCode,
            "public string BMSInstallDir",
            "public string LR2CustomFolderAsRootOutputDir");
        string lr2CustomFolderRootGetter = ExtractBetween(
            viewModelCode,
            "public string LR2CustomFolderAsRootOutputDir",
            "public Uri TableListURL");

        Assert.IsFalse(lr2CustomFolderGetter.Contains("Settings.Default.LR2CustomFolderOutputBaseDir = null"));
        Assert.IsFalse(bmsInstallDirGetter.Contains("Settings.Default.BMSInstallDir = null"));
        Assert.IsFalse(lr2CustomFolderRootGetter.Contains("Settings.Default.LR2CustomFolderOutputBaseDirRootType = null"));
        StringAssert.Contains(lr2CustomFolderGetter, "return ApplicationSettings.LR2CustomFolderOutputBaseDir;");
        StringAssert.Contains(bmsInstallDirGetter, "return ApplicationSettings.BMSInstallDir;");
        StringAssert.Contains(lr2CustomFolderRootGetter, "return ApplicationSettings.LR2CustomFolderOutputBaseDirRootType;");
    }

    [TestMethod]
    public void SettingDialogAudioAndPlayerBindings_DoNotCreateFalsePendingChanges()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string playerDriverProperty = ExtractBetween(
            viewModelCode,
            "public int PlayerDriverIndex",
            "private static bool IsAudiblePlayerDriver");
        string playerDriverGetter = ExtractBetween(
            playerDriverProperty,
            "get",
            "set");
        string playerDeviceProperty = ExtractBetween(
            viewModelCode,
            "public string PlayerDevice",
            "public SampleRate PlayerSampleRate");
        string playerDeviceGetter = ExtractBetween(
            playerDeviceProperty,
            "get",
            "set");
        string playerSelectionProperties = ExtractBetween(
            viewModelCode,
            "public bool UsePlayeruBMplay",
            "public bool IsSaveLR2bodyWindowPosition");
        string resetSettings = ExtractBetween(
            viewModelCode,
            "public void ResetSettings()",
            "public RestartMode IsNeedRestartForSaved()");
        string saveFollowup = ExtractBetween(
            viewModelCode,
            "private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)",
            "public bool CheckValidation()");
        string tableListUrlGetter = ExtractBetween(
            ExtractBetween(viewModelCode, "public Uri TableListURL", "public bool EnablePlaylistUrlCompletion"),
            "get",
            "set");
        string playlistMd5UrlGetter = ExtractBetween(
            ExtractBetween(viewModelCode, "public string PlaylistMd5UrlMappingTsvUri", "public bool IsLR2BackupEnabled"),
            "get",
            "set");
        string folderNameFormatGetter = ExtractBetween(
            ExtractBetween(viewModelCode, "public string FolderNameFormat", "public bool UseOnlyShiftJISChars"),
            "get",
            "set");
        string encodeFileNameFormatGetter = ExtractBetween(
            ExtractBetween(viewModelCode, "public string EncodeFileNameFormat", "public ReadOnlyObservableCollection<string> PlayerDriverNames"),
            "get",
            "set");
        string languageProperty = ExtractBetween(
            viewModelCode,
            "public string Language",
            "internal SettingsDialogViewModel(");

        StringAssert.Contains(xaml, "IsChecked=\"{Binding UseInternalPlayer}\"");
        StringAssert.Contains(xaml, "SelectedIndex=\"{Binding PlayerDeviceIndex, Mode=TwoWay}\"");
        Assert.IsFalse(xaml.Contains("SelectedValuePath=\"Driver\" DisplayMemberPath=\"FriendlyName\""));
        StringAssert.Contains(xaml, "IsEnabled=\"{Binding IsAudioDeviceTestAvailable, Mode=OneWay}\"");
        StringAssert.Contains(xaml, "Text=\"{Binding AudioDeviceTestStatusMessage, Mode=OneWay}\"");
        StringAssert.Contains(viewModelCode, "public bool IsEditCompletionEnabled => !IsEditCompletionInProgress && !IsAudioDeviceTestInProgress;");
        StringAssert.Contains(viewModelCode, "public bool IsEditCancellationEnabled => !IsEditCompletionInProgress");
        StringAssert.Contains(viewModelCode, "if (IsEditCompletionInProgress || IsAudioDeviceTestInProgress)");
        StringAssert.Contains(settingDialogCode, "await settingDialogViewModel.RunAudioDeviceTestAsync();");
        Assert.IsFalse(settingDialogCode.Contains("AudioPlayerInitTest"));
        Assert.IsFalse(settingDialogCode.Contains("Task.Run"));
        Assert.IsFalse(xaml.Contains("Name=\"radioButtonInternalPlayer\" Height=\"20\" GroupName=\"Player\" Margin=\"30,0,0,0\" IsChecked=\"True\""));
        StringAssert.Contains(playerSelectionProperties, "public bool UseInternalPlayer");
        StringAssert.Contains(playerSelectionProperties, "SetPlayerSelection(usePlayeruBMplay: false, usePlayerLR2body: false, usePlayerBMIIDXView: false);");
        Assert.IsFalse(playerDriverGetter.Contains("Settings.Default.PlayerDriver ="));
        Assert.IsFalse(playerDeviceGetter.Contains("Settings.Default.PlayerDevice ="));
        Assert.IsFalse(playerDeviceGetter.Contains("Settings.Default.PlayerDeviceName ="));
        StringAssert.Contains(playerDeviceGetter, "return ResolvePlayerDeviceDescriptor().Driver ?? ApplicationSettings.PlayerDevice;");
        StringAssert.Contains(resetSettings, "if (playerDeviceNames != null)");
        Assert.IsFalse(saveFollowup.Contains("AudioPlayerInitTest(playSound: false)"));
        Assert.IsFalse(tableListUrlGetter.Contains("Settings.Default.TableListURL ="));
        Assert.IsFalse(playlistMd5UrlGetter.Contains("Settings.Default.PlaylistMd5UrlMappingTsvUri = null"));
        Assert.IsFalse(folderNameFormatGetter.Contains("Settings.Default.FolderNameFormat ="));
        Assert.IsFalse(encodeFileNameFormatGetter.Contains("Settings.Default.EncodeFileNameFormat ="));
        StringAssert.Contains(languageProperty, "cultureCatalog.Cultures.TryGetValue");
        Assert.IsFalse(languageProperty.Contains("catch"));
    }

    [TestMethod]
    public void SettingDialogValidation_RequiresInstallDestinationInAllOperationModes()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string checkValidation = ExtractBetween(
            viewModelCode,
            "public bool CheckValidation(out string errMsg)",
            "public async Task SaveSettings()");

        Assert.IsFalse(xaml.Contains("IsEnabled=\"{Binding CanSaveSettings"));
        StringAssert.Contains(checkValidation, "if (!IsBMSInstallDirValid())");
        StringAssert.Contains(checkValidation, "Resources.Error_InvalidBmsInstallDir");
        Assert.IsFalse(checkValidation.Contains("OperationModeLR2DB && !IsBMSInstallDirValid()"));
        StringAssert.Contains(resources, "name=\"Error_InvalidBmsInstallDir\"");
        StringAssert.Contains(resources, "name=\"Confirm_RestartForOperationModeChange\"");
        StringAssert.Contains(resources, "name=\"Error_RestartApplicationFailed\"");
        StringAssert.Contains(resourceCode, "Error_InvalidBmsInstallDir");
        StringAssert.Contains(resourceCode, "Confirm_RestartForOperationModeChange");
        StringAssert.Contains(resourceCode, "Error_RestartApplicationFailed");
        foreach (string languageFile in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            string languageJson = File.ReadAllText(languageFile);
            StringAssert.Contains(languageJson, "\"Error_InvalidBmsInstallDir\"");
            StringAssert.Contains(languageJson, "\"Confirm_RestartForOperationModeChange\"");
            StringAssert.Contains(languageJson, "\"Error_RestartApplicationFailed\"");
        }
    }

    [TestMethod]
    public void SettingDialogReloadDecision_UsesExplicitSettingDiffs()
    {
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string backupSavedSettings = ExtractBetween(
            viewModelCode,
            "private void backupSavedSettingsCore(SettingsSnapshotRefreshScope scope)",
            "private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)");
        string restartDecision = ExtractMethodBody(
            viewModelCode,
            "public RestartMode IsNeedRestartForSaved()");
        string pendingDecision = ExtractBetween(
            viewModelCode,
            "internal bool HasPendingSettingChanges()",
            "private bool HasSettingValueChanges()");
        string saveCore = ExtractBetween(
            viewModelCode,
            "private async Task SaveSettingsCore(bool runPostSaveActions)",
            "public void SaveOperationModeForRestart");
        StringAssert.Contains(backupSavedSettings, "tempStandaloneBmsRootPaths = SerializeBmsRootPathsForChangeTracking(StandaloneBmsRootPathList);");
        StringAssert.Contains(backupSavedSettings, "tempLR2ConfigBmsSearchRoots = SerializeLR2ConfigBmsSearchRoots();");
        StringAssert.Contains(backupSavedSettings, "scope.HasFlag(SettingsSnapshotRefreshScope.StandaloneSearchRoots)");
        StringAssert.Contains(backupSavedSettings, "scope.HasFlag(SettingsSnapshotRefreshScope.CustomFolderOutputBase)");
        StringAssert.Contains(backupSavedSettings, "scope.HasFlag(SettingsSnapshotRefreshScope.Lr2SearchRoots)");
        StringAssert.Contains(backupSavedSettings, "scope.HasFlag(SettingsSnapshotRefreshScope.PlayHistoryDisplayPreset)");
        StringAssert.Contains(backupSavedSettings, "settings_backup_snapshot");
        StringAssert.Contains(viewModelCode, "if (lr2ConfigBoundaryChanged)");
        StringAssert.Contains(viewModelCode, "scope |= SettingsSnapshotRefreshScope.Lr2SearchRoots | SettingsSnapshotRefreshScope.ValidationState;");
        Assert.IsFalse(backupSavedSettings.Contains("tempEnableLR2SongDbSync"));
        Assert.IsFalse(viewModelCode.Contains("tempValidation"));
        StringAssert.Contains(pendingDecision, "HasSearchRootSettingsChanged()");
        Assert.IsFalse(pendingDecision.Contains("isSearchRootsChanged"));
        Assert.IsFalse(pendingDecision.Contains("isBMSDirectoryAdded"));
        Assert.IsFalse(pendingDecision.Contains("isBMSDirectoryRemoved"));
        StringAssert.Contains(saveCore, "searchRootsChanged = HasSearchRootSettingsChanged();");
        StringAssert.Contains(viewModelCode, "HasPathSettingValueChanged(tempLR2RootPath, ApplicationSettings.LR2RootPath");
        StringAssert.Contains(viewModelCode, "SerializeBmsRootPathsForChangeTracking(lr2config.GetBMSSearchDirectoriesForChangeTracking())");
        Assert.IsFalse(restartDecision.Contains("CheckValidation()"));
        StringAssert.Contains(restartDecision, "scoreSourceChanged");
        StringAssert.Contains(restartDecision, "HasLR2ConfigBmsSearchRootsChanged()");
        StringAssert.Contains(restartDecision, "HasStandaloneBmsRootPathsChanged()");
        StringAssert.Contains(restartDecision, "HasCustomFolderOutputBaseSettingsChanged()");
        Assert.IsFalse(viewModelCode.Contains("IsNeedRestartForSaveOrCancel"));
    }

    [TestMethod]
    public void SearchRootChanges_UpdateRuntimeSearchTargetsBeforeFileDiffReload()
    {
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string rootViewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string settingDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string playlistCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string customFolderMaintenanceOwnerCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "PlaylistCustomFolderOutputMaintenanceOwner.cs"));
        string runtimeSync = ExtractBetween(
            viewModelCode,
            "private void ApplyRuntimeSearchRootsForCurrentMode()",
            "private bool IsLR2SongDBPathValid()");
        string rootAdd = ExtractBetween(
            viewModelCode,
            "public async Task AddBmsSearchRootPathFromMainWindowPicker",
            "public void AddBmsSearchRootPathFromPicker");
        string saveCore = ExtractBetween(
            viewModelCode,
            "private async Task SaveSettingsCore(bool runPostSaveActions)",
            "public void SaveOperationModeForRestart");
        string postSaveSteps = ExtractBlockAfter(
            viewModelCode,
            "private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)");
        string buildPostSaveImpact = ExtractBetween(
            viewModelCode,
            "private SettingsPostSaveImpact BuildSettingsPostSaveImpact(bool customFolderSearchRootSyncNeeded)",
            "private static bool HasPostSaveImpact");
        string reloadFileDiff = ExtractBetween(
            rootViewModelCode,
            "internal async Task ReloadFileDiffAsync()",
            "internal async Task ReinitializeLibraryAsync()");
        string manualResyncClickHandler = ExtractBetween(
            settingDialogCode,
            "private async void resyncLr2SongDbSyncDataButtonClicked",
            "private async void detailTabItemBackupButtonClicked");
        string manualResyncPlaylistMethod = ExtractBetween(
            playlistCode,
            "private async Task<Lr2SongDbSyncPreparedDataSurface> ReOutputAllCustomFoldersForLr2SongDbSyncCoreAsync",
            "private List<CustomFolderDefinition> BuildCustomFolderDefinitions(");

        StringAssert.Contains(runtimeSync, "searchRootRuntimePort.ApplySearchTargets(lr2config.GetBMSSearchDirectories());");
        StringAssert.Contains(runtimeSync, "searchRootRuntimePort.ApplySearchTargets(GetStandaloneBmsRootPathsForCurrentSession());");
        StringAssert.Contains(rootAdd, "ApplyRuntimeSearchRootsForCurrentMode();");
        Assert.IsTrue(rootAdd.IndexOf("ApplyRuntimeSearchRootsForCurrentMode();", StringComparison.Ordinal) < rootAdd.IndexOf("await ReloadFileDiffAsync();", StringComparison.Ordinal));
        StringAssert.Contains(saveCore, "ApplyRuntimeSearchRootsForCurrentMode();");
        StringAssert.Contains(viewModelCode, "lr2SongDbSyncWorkflow.SyncFolderDataAfterSettingsChange(\"SettingDialog.SaveSettings\")");
        StringAssert.Contains(viewModelCode, "lr2SongDbSyncWorkflow.SyncExternalFolderRowsAfterCustomFolderOutputBaseSettingsChange(\"SettingDialog.SaveSettings\")");
        Assert.IsFalse(viewModelCode.Contains("private Lr2SongDbSyncPreparedDataSurface ReOutputAllCustomFoldersForLr2GeneratedDataSync(string reason)"));
        Assert.IsFalse(viewModelCode.Contains("files?.QueueLr2SongDbSync("));
        Assert.IsFalse(viewModelCode.Contains("files?.TryRunLr2SongDbSyncDataPreparation("));
        Assert.IsFalse(viewModelCode.Contains("files?.PublishLr2SongDbSyncExternalStageProgress("));
        StringAssert.Contains(viewModelCode, "internal async Task RequestLr2SongDbSyncAsync()");
        Assert.IsFalse(viewModelCode.Contains("public async Task RequestLr2SongDbSyncAsync(string reason, bool force)"));
        StringAssert.Contains(viewModelCode, "await lr2SongDbSyncWorkflow.RequestManualResyncAsync().ConfigureAwait(false);");
        StringAssert.Contains(viewModelCode, "public bool CanRequestLr2SongDbSyncDataResync => statePort.HasActiveLibraryProfile");
        StringAssert.Contains(viewModelCode, "&& OperationModeLR2DB");
        StringAssert.Contains(viewModelCode, "&& !fileDiffReloadPending");
        StringAssert.Contains(viewModelCode, "&& !statePort.IsLibraryOperationInProgress;");
        Assert.IsFalse(viewModelCode.Contains("public bool CanRequestLr2SongDbSyncDataResync => HasActiveLibraryProfile"));
        StringAssert.Contains(settingDialogXaml, "<Grid Margin=\"20,2,10,4\" IsEnabled=\"{Binding IsChecked, ElementName=radioButtonUseLR2}\">");
        StringAssert.Contains(settingDialogXaml, "HorizontalAlignment=\"Center\"");
        StringAssert.Contains(settingDialogXaml, "IsEnabled=\"{Binding CanRequestLr2SongDbSyncDataResync, Mode=OneWay}\"");
        StringAssert.Contains(manualResyncClickHandler, "SettingsDialogViewModel settingDialogViewModel = GetSettingDialogViewModel();");
        StringAssert.Contains(manualResyncClickHandler, "if (!settingDialogViewModel.CanRequestLr2SongDbSyncDataResync)");
        StringAssert.Contains(manualResyncClickHandler, "settingDialogViewModel.IsLr2SongDbSyncDataResyncBlockedByLibraryOperation");
        Assert.IsFalse(manualResyncClickHandler.Contains("GetMainWindowViewModel()"));
        StringAssert.Contains(settingDialogCode, "await settingDialogViewModel.RequestLr2SongDbSyncAsync();");
        StringAssert.Contains(manualResyncClickHandler, "HideThisOverlay();");
        StringAssert.Contains(manualResyncClickHandler, "await Dispatcher.Yield(DispatcherPriority.Background);");
        StringAssert.Contains(playlistCode, "RepairMissingCustomFolderOutputsAfterHydrationCore(reason, verifyRootOutputDirectoryRows, settings)");
        StringAssert.Contains(customFolderMaintenanceOwnerCode, "outputOwner.MaterializeBatch(");
        StringAssert.Contains(customFolderMaintenanceOwnerCode, "syncMaterialization(new CustomFolderBatchMaterializationRequest(materialization))");
        StringAssert.Contains(customFolderMaintenanceOwnerCode, "DirectoryRowGenerationScopeDirectories = result?.DirectoryRowGenerationScopeDirectories ?? []");
        Assert.IsFalse(
            playlistCode.Contains("CreateCustomFolderOutputUpdateCallback"),
            "Startup playlist hydration must not run per-table custom folder output callbacks; missing .lr2folder repair must use the batch materialization path.");
        Assert.IsFalse(
            manualResyncPlaylistMethod.IndexOf("ReOutputCustomFolder(table)", StringComparison.Ordinal) >= 0,
            "Manual LR2 generated-data sync must batch app-managed custom folder projection instead of running per-table physical reoutput and DB sync.");
        string workflowOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "Lr2SongDbSyncWorkflowOwner.cs");
        StringAssert.Contains(workflowOwnerCode, "runtime.TryRunDataPreparation(");
        StringAssert.Contains(workflowOwnerCode, "runtime.PreparePlaylistGeneratedData(reason)");
        StringAssert.Contains(workflowOwnerCode, "runtime.PrepareBuiltinGeneratedData(reason)");
        StringAssert.Contains(workflowOwnerCode, "runtime.Queue(reason, force: false, allowIncompleteToQueue: false)");
        Assert.IsFalse(
            manualResyncClickHandler.IndexOf("settingDialogOperationGrid.IsEnabled = false;", StringComparison.Ordinal) >= 0,
            "Manual LR2 generated-data sync must not disable the entire settings dialog while playlist projection is running.");
        Assert.IsFalse(
            manualResyncClickHandler.IndexOf("ClearValue(UIElement.IsEnabledProperty)", StringComparison.Ordinal) >= 0
            || manualResyncClickHandler.IndexOf(".IsEnabled = false", StringComparison.Ordinal) >= 0,
            "Manual LR2 generated-data sync must not overwrite the button IsEnabled binding.");
        Assert.IsTrue(
            manualResyncClickHandler.IndexOf("if (!settingDialogViewModel.CanRequestLr2SongDbSyncDataResync)", StringComparison.Ordinal)
            < manualResyncClickHandler.IndexOf("Msg_confirm_lr2_song_db_sync_data_resync", StringComparison.Ordinal),
            "Manual LR2 generated-data sync must reject invalid profile/operation state before showing the destructive confirmation.");
        StringAssert.Contains(buildPostSaveImpact, "tempOperationModeLR2DB != ApplicationSettings.OperationModeLR2DB");
        StringAssert.Contains(viewModelCode, "private enum SettingsPostSaveImpact");
        StringAssert.Contains(viewModelCode, "BuildSettingsPostSaveImpact(bool customFolderSearchRootSyncNeeded)");
        StringAssert.Contains(viewModelCode, "SettingsPostSaveImpact.Lr2CoreSync");
        StringAssert.Contains(viewModelCode, "SettingsPostSaveImpact.ExternalLr2FolderRowsSync");
        StringAssert.Contains(postSaveSteps, "impact.HasFlag(SettingsPostSaveImpact.Lr2CoreSync)");
        StringAssert.Contains(postSaveSteps, "else if (impact.HasFlag(SettingsPostSaveImpact.ExternalLr2FolderRowsSync))");
        StringAssert.Contains(postSaveSteps, "\"settings_post_save\"");
        Assert.IsFalse(postSaveSteps.Contains("tempEnableLR2SongDbSync"));
        Assert.IsFalse(buildPostSaveImpact.Contains("tempEnableLR2SongDbSync"));
        StringAssert.Contains(buildPostSaveImpact, "tempLR2RootPath, ApplicationSettings.LR2RootPath");
        StringAssert.Contains(buildPostSaveImpact, "tempLR2CustomFolderOutputDir, ApplicationSettings.LR2CustomFolderOutputBaseDir");
        StringAssert.Contains(buildPostSaveImpact, "tempLR2CustomFolderAsRootOutputDir, ApplicationSettings.LR2CustomFolderOutputBaseDirRootType");
        StringAssert.Contains(reloadFileDiff, "Lr2SongDbSyncWorkflow.QueueAfterReloadFileDiff(\"ReloadFileDiff\")");
        Assert.IsFalse(reloadFileDiff.Contains("files.QueueLr2SongDbSync("));
        Assert.IsFalse(viewModelCode.Contains("prepareGeneratedData: () => ReOutputAllCustomFoldersForLr2GeneratedDataSync(\"status_bar_cleanup_retry\")"));
        Assert.IsTrue(
            reloadFileDiff.IndexOf("files.ReloadFileDiff();", StringComparison.Ordinal)
            < reloadFileDiff.IndexOf("Lr2SongDbSyncWorkflow.QueueAfterReloadFileDiff(\"ReloadFileDiff\")", StringComparison.Ordinal),
            "LR2 song.db sync status should be evaluated after file diff has applied root/source changes.");
    }

    [TestMethod]
    public void Lr2CompatibilityTree_IsAlwaysShown()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string unregisteredTreeItem = ExtractBetween(
            xaml,
            "Path=Resources.Unregistered_in_lr2_db",
            "Path=Resources.Search_zero_note");

        Assert.IsFalse(unregisteredTreeItem.Contains("IsLr2CompatibilityTreeVisible"));
        Assert.IsFalse(unregisteredTreeItem.Contains("OperationModeLR2DB"));
        Assert.IsFalse(viewModelCode.Contains("IsLr2CompatibilityTreeVisible"));
    }

    [TestMethod]
    public void StandaloneRootNormalization_PreservesExistingRootsWhenAddingInstallDestination()
    {
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string addStandalone = ExtractBetween(
            viewModelCode,
            "private void AddStandaloneBmsRootPathsCore",
            "private void AddBMSDirectoriesToLR2Config");
        string basePath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(basePath, "RootA");
        string secondRoot = Path.Combine(basePath, "RootB");
        string installRoot = Path.Combine(basePath, "Install");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(installRoot);
        try
        {
            IReadOnlyList<string> normalized = SettingsDialogViewModel.NormalizeStandaloneBmsRootPaths([firstRoot, secondRoot, installRoot]);

            Assert.AreEqual(3, normalized.Count);
            Assert.IsTrue(normalized.Contains(firstRoot, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(normalized.Contains(secondRoot, StringComparer.OrdinalIgnoreCase));
            Assert.IsTrue(normalized.Contains(installRoot, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(basePath, recursive: true);
        }

        Assert.IsFalse(addStandalone.Contains("StandaloneBmsRootPathList.Clear();"));
        StringAssert.Contains(addStandalone, "StandaloneBmsRootPathList.Add(path);");
        StringAssert.Contains(addStandalone, "GetSetMethod().Invoke(this, [requestedPath]);");
    }

    [TestMethod]
    public void StandaloneRootDeserialization_PreservesExistingLr2IncompatibleRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StandaloneLong_" + Guid.NewGuid().ToString("N"));
        string longRoot = BuildLongDirectoryPath(tempRoot, "root");
        LongPathFileSystem.CreateDirectory(longRoot);

        try
        {
            IReadOnlyList<string> deserialized = SettingsDialogViewModel.DeserializeStandaloneBmsRootPaths(longRoot);
            string serialized = SettingsDialogViewModel.SerializeStandaloneBmsRootPaths(deserialized);

            Assert.IsFalse(Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(longRoot));
            CollectionAssert.Contains(deserialized.ToList(), longRoot);
            StringAssert.Contains(serialized, longRoot);
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempRoot))
            {
                LongPathFileSystem.DeleteDirectory(tempRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public void RootFolderUnregister_RunsOnUiThreadAndStandaloneParentFolderCacheAcceptsEmptyRoots()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string parentFolderCacheCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryParentFolderCacheService.cs"));
        string unregisterHandler = ExtractBetween(
            mainWindowCode,
            "private async void treeViewLibraryFolderContextMenuItemUnregisterRootFolder",
            "private async void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick");

        Assert.IsFalse(unregisterHandler.Contains("Task.Run"));
        StringAssert.Contains(unregisterHandler, "await settingDialogViewModel.RequestRemoveBmsSearchRootAsync(path)");
        Assert.IsFalse(unregisterHandler.Contains("LongPathFileSystem.DirectoryExists"));
        Assert.IsFalse(unregisterHandler.Contains("UiDialogRoute.ShowMessageBox"));
        Assert.IsFalse(unregisterHandler.Contains("Window.GetWindow(this)"));
        string settingDialogViewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        StringAssert.Contains(settingDialogViewModelCode, "internal async Task RequestRemoveBmsSearchRootAsync(string dir)");
        StringAssert.Contains(settingDialogViewModelCode, "schemaDialogs.ConfirmAsync(new UiConfirmationRequest(");
        Assert.IsFalse(settingDialogViewModelCode.Contains("RemoveBMSDirectoryFromRootFolderAndSave"));
        Assert.IsFalse(parentFolderCacheCode.Contains("throw new NotImplementedException();"));
        StringAssert.Contains(parentFolderCacheCode, "return true;");
    }

    [TestMethod]
    public void AutoRenameFolder_UsesChartSelectionIncludingBmson()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string bmsLibraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string contextMenuStateBuilderCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow", "ChartContextMenuStateBuilder.cs"));
        string autoRenameClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemAutoRenameFolderClick",
            "private async void tableContextMenuItemRenameBMSFileClick");
        string autoRenameAllClick = ExtractBetween(
            mainWindowCode,
            "private async void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick",
            "private async void treeViewInstalledContextMenuClearAllClick");
        string folderAutoRenameWorkflowOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "FolderAutoRenameWorkflowOwner.cs");
        string moveFileClick = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemMoveFileClick",
            "private void fixEncodingSelectedBMS");
        string contextMenuOpening = ExtractBetween(
            mainWindowCode,
            "bool canAutoRenameFolders =",
            "if (menuItem17 != null)");
        string autoRenameAll = ExtractMethodBody(
            folderAutoRenameWorkflowOwnerCode,
            "private bool ExecuteMutation(");
        string autoRenameAllModel = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Models", "BMSLibrary.LibraryFileOperationOwner.cs");
        string applicationCompositionCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs");
        string progressHubCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "OperationProgressHubViewModel.cs");
        string cellEditEnded = ExtractBetween(
            mainWindowCode,
            "private void customTableView_CellEditEnded",
            "private void RefreshCustomTableViewDisplayAsync");
        string regularOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        StringAssert.Contains(autoRenameClick, "GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary)");
        StringAssert.Contains(autoRenameClick, "viewModel.FolderAutoRenameWorkflow.RequestStartSelected(targets);");
        Assert.IsFalse(autoRenameClick.Contains("Task.Run"));
        Assert.IsFalse(autoRenameClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(autoRenameClick.Contains("targetSnapshot"));
        Assert.IsFalse(autoRenameClick.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(autoRenameClick.Contains("GetSelectedBmsChartFiles(ChartOperationCapabilities.None)"));
        Assert.IsFalse(autoRenameClick.Contains("ChartFolderAutoRenameRequest.TryCreate"));
        Assert.IsFalse(autoRenameClick.Contains("FolderAutoRenameWorkflow.IsActive"));
        Assert.IsFalse(autoRenameClick.Contains("FolderAutoRenameWorkflow.StartSelected"));
        StringAssert.Contains(autoRenameAllClick, "await viewModel.FolderAutoRenameWorkflow");
        StringAssert.Contains(autoRenameAllClick, "RequestStartAllAsync(path)");
        StringAssert.Contains(autoRenameAllClick, "e.Handled = true;");
        Assert.IsFalse(autoRenameAllClick.Contains("UiDialogRoute.ShowMessageBox"));
        Assert.IsFalse(autoRenameAllClick.Contains("LongPathFileSystem.DirectoryExists"));
        Assert.IsFalse(autoRenameAllClick.Contains("FolderAutoRenameWorkflow.StartAll"));
        StringAssert.Contains(folderAutoRenameWorkflowOwnerCode, "dialogs.ConfirmAsync(new UiConfirmationRequest(");
        StringAssert.Contains(folderAutoRenameWorkflowOwnerCode, "UiDialogRoute.ThrowIfNotShown(confirmation");
        StringAssert.Contains(moveFileClick, "GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary)");
        StringAssert.Contains(moveFileClick, "new SelectedChartMoveRequest(targets, dstDir)");
        StringAssert.Contains(moveFileClick, "viewModel.SelectedChartMutations");
        Assert.AreEqual(1, CountOccurrences(moveFileClick, "MoveAsync("));
        StringAssert.Contains(moveFileClick, "e.Handled = true;");
        Assert.IsFalse(moveFileClick.Contains("ConfirmMove"));
        Assert.IsFalse(moveFileClick.Contains("SelectedChartMoveConfirmationResult"));
        Assert.IsFalse(moveFileClick.Contains("SelectedChartMoveOperation"));
        Assert.IsFalse(moveFileClick.Contains("confirmation.Operation"));
        Assert.IsFalse(moveFileClick.Contains("Task.Run"));
        Assert.IsFalse(moveFileClick.Contains("viewModel.MoveLibraryCharts"));
        StringAssert.Contains(contextMenuOpening, "contextMenuState.CanAutoRenameFolders");
        StringAssert.Contains(contextMenuStateBuilderCode, "hasBmsSelection || hasBmsonSelection");
        StringAssert.Contains(autoRenameAll, "library.BeginOperationDialogScope()");
        StringAssert.Contains(folderAutoRenameWorkflowOwnerCode, "mutationPort.RenameAll(run.Library, run.ParentDirectory, progressReporter)");
        StringAssert.Contains(folderAutoRenameWorkflowOwnerCode, "playback.StopPlaybackForFolderMutation");
        StringAssert.Contains(viewModelCode, "FolderAutoRenameWorkflow = childComposition.FolderAutoRenameWorkflow;");
        StringAssert.Contains(viewModelCode, "FolderAutoRenameWorkflow.CompletionPublished += FolderAutoRenameWorkflowCompletionPublished;");
        StringAssert.Contains(applicationCompositionCode, "ProgressHub.AttachWorkflowProgressSources(");
        StringAssert.Contains(progressHubCode, "folderAutoRenameWorkflow.ProgressChanged += UpdateFolderAutoRenameProgress;");
        Assert.IsFalse(viewModelCode.Contains("PackageInstallWorkflow.StatusChanged += PackageInstallWorkflowStatusChanged;"));
        Assert.IsFalse(viewModelCode.Contains("MaintenanceRescanWorkflow.ProgressChanged += MaintenanceRescanWorkflowProgressChanged;"));
        Assert.IsFalse(viewModelCode.Contains("FolderAutoRenameWorkflow.ProgressChanged += FolderAutoRenameWorkflowProgressChanged;"));
        Assert.IsFalse(viewModelCode.Contains("PackageInstallWorkflowStatusChanged("));
        Assert.IsFalse(viewModelCode.Contains("MaintenanceRescanWorkflowProgressChanged("));
        Assert.IsFalse(viewModelCode.Contains("FolderAutoRenameWorkflowProgressChanged("));
        Assert.IsFalse(viewModelCode.Contains("public void AutoRenameAllChartFolders"));
        Assert.IsFalse(viewModelCode.Contains("internal void AutoRenameChartFolders"));
        Assert.IsFalse(viewModelCode.Contains("BeginFolderAutoRenameProgress"));
        Assert.IsFalse(viewModelCode.Contains("UpdateFolderAutoRenameProgressStatus"));
        Assert.IsFalse(viewModelCode.Contains("FinishFolderAutoRenameProgress"));
        Assert.IsFalse(viewModelCode.Contains("DispatchFolderAutoRenameProgressUpdate"));
        Assert.IsFalse(autoRenameAll.Contains("IEnumerable<BeMusicSeeker.Models.BMSFile> enumerable = BMSFiles;"));
        StringAssert.Contains(autoRenameAllModel, "CreateOwnedRealPathChartDirectoriesUnsafe");
        StringAssert.Contains(autoRenameAllModel, "BuildAutoRenamePlansForSourceFolders");
        Assert.IsFalse(autoRenameAllModel.Contains("ILibraryFileOperationPort"));
        Assert.IsFalse(bmsLibraryCode.Contains("CreateLibraryChartSnapshotsForFolderOperations"));
        Assert.IsFalse(autoRenameAllModel.Contains("CreateOwnedSubtreeChartSnapshot"));
        Assert.IsFalse(autoRenameAllModel.Contains("BMSFiles ??"));
        Assert.IsFalse(autoRenameAllModel.Contains("files?.BmsonSongs"));
        StringAssert.Contains(cellEditEnded, "viewModel.MainChartList.RequestCellEditEnded(");
        StringAssert.Contains(regularOwnerCode, "RenameChartFolderRequest.TryCreate(folderTarget, out RenameChartFolderRequest renameRequest)");
        Assert.IsFalse(viewModelCode.Contains("RegularChartListOwnerFolderEditRequested"));
        StringAssert.Contains(regularOwnerCode, "RenameChartFolderAsync(renameRequest, request.Text)");
        Assert.IsFalse(cellEditEnded.Contains("CreateRenameChartFolderTargetSnapshot"));
        Assert.IsFalse(cellEditEnded.Contains("targetSnapshot"));
        Assert.IsFalse(cellEditEnded.Contains("viewModel.RenameChartFolder(target, newFolder)"));
    }

    [TestMethod]
    public void PlaylistLibraryIndexUsesOwnedResolveIndex()
    {
        string viewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string playlistWorkspaceCode = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string playlistDataSourceCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistDetailDataSource.cs");
        string bmsLibraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string createPlaylistLibraryIndex = ExtractBetween(
            playlistWorkspaceCode,
            "private PlaylistLibraryIndexSnapshot CreatePlaylistLibraryIndexSnapshot",
            "internal PlaylistLibraryIndexReadinessSnapshot CapturePlaylistLibraryIndexReadinessSnapshot");
        string resolveIndexHelper = ExtractBetween(
            bmsLibraryCode,
            "private PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshotUnsafe",
            "private ChartInfoHydrationOwnerSummary CreateChartInfoHydrationOwnerSummaryUnsafe");

        StringAssert.Contains(createPlaylistLibraryIndex, "dataSource.GetResolveIndexSnapshot(");
        StringAssert.Contains(playlistDataSourceCode, "library.GetPlaylistLibraryResolveIndexSnapshot(");
        Assert.IsFalse(createPlaylistLibraryIndex.Contains("foreach (BeMusicSeeker.Models.BMSFile file in BMSFiles"));
        Assert.IsFalse(createPlaylistLibraryIndex.Contains("files?.BmsonSongs"));
        Assert.IsFalse(viewModelCode.Contains("playlistLibraryIndexSync"));
        Assert.IsFalse(viewModelCode.Contains("GetOrCreatePlaylistLibraryIndexSnapshot"));
        StringAssert.Contains(resolveIndexHelper, "catalogOwnedCollectionOwner.Collection.CreatePlaylistLibraryResolveRefSnapshot(cancellationToken.ThrowIfCancellationRequested)");
        StringAssert.Contains(resolveIndexHelper, "PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(refs, cancellationToken.ThrowIfCancellationRequested)");
    }

    [TestMethod]
    public void SharedTransientStatePruneUsesOwnedRuntimeStatePrimaryKeySnapshot()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string bmsLibraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string projectionOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainChartRowProjectionOwner.cs"));
        string regularOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs"));
        string notificationHandler = ExtractBetween(
            regularOwnerCode,
            "private void ApplyNormalLibraryRefreshNotificationBatch",
            "private BmsonLibraryRowCacheSyncResult SyncNormalLibraryStorageRowCachesForRefreshNotification");
        string bmsonSync = ExtractBetween(
            regularOwnerCode,
            "internal BmsonLibraryRowCacheSyncResult SyncBmsonRows",
            "internal BmsonLibraryRowCacheSyncResult RemoveBmsonRows");
        string pruneHelper = ExtractBetween(
            projectionOwnerCode,
            "internal void PruneTransientStatesToOwnedCharts",
            "internal void ClearTransientStates");
        string modelKeyHelper = ExtractBetween(
            bmsLibraryCode,
            "internal HashSet<string> CreateOwnedChartRuntimeStatePrimaryKeySnapshot",
            "private void EnsureOwnedChartCollectionBuiltUnsafe");

        StringAssert.Contains(notificationHandler, "mainChartList.RowProjection.PruneTransientStatesToOwnedCharts(library)");
        Assert.IsFalse(notificationHandler.Contains("MainWindowViewModel"));
        StringAssert.Contains(bmsonSync, "library?.CreateNormalLibrarySourceStorageOwnerView()");
        Assert.IsFalse(bmsonSync.Contains("library?.BmsonSongs"));
        Assert.IsFalse(bmsonSync.Contains("OrderBy(song => song.path"));
        StringAssert.Contains(bmsonSync, "mainChartList.RowProjection.PruneTransientStatesToOwnedCharts(library)");
        Assert.IsFalse(bmsonSync.Contains("PruneSharedChartTransientStateCacheToCurrentStorageRows"));
        StringAssert.Contains(pruneHelper, "library?.CreateOwnedChartRuntimeStatePrimaryKeySnapshot()");
        Assert.IsFalse(pruneHelper.Contains("foreach (BeMusicSeeker.Models.BMSFile"));
        Assert.IsFalse(pruneHelper.Contains("foreach (LR2SongDBExtended.bmson_song"));
        StringAssert.Contains(modelKeyHelper, "rwlockBMSFiles.GetReaderGuard()");
        StringAssert.Contains(modelKeyHelper, "catalogOwnedCollectionOwner.Collection.CreateChartRuntimeStatePrimaryKeySnapshot()");
        Assert.IsFalse(modelKeyHelper.Contains("CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe()"));
    }

    [TestMethod]
    public void MainTableRowProjectionStateIsOwnedOutsideRootWorkflow()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string projectionOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "MainChartRowProjectionOwner.cs"));
        string regularOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs"));
        string playlistDataSourceCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistDetailDataSource.cs"));

        Assert.IsFalse(viewModelCode.Contains("chartTransientStatesByKey"));
        Assert.IsFalse(viewModelCode.Contains("chartInfoProjectionVersionCache"));
        Assert.IsFalse(viewModelCode.Contains("scoreSnapshotProjectionVersionCache"));
        Assert.IsFalse(viewModelCode.Contains("private void ApplyLibraryChartRowProviders"));
        Assert.IsFalse(viewModelCode.Contains("private ChartFileTransientState TryGetSharedChartTransientState"));
        Assert.IsFalse(viewModelCode.Contains("private LR2SongDBExtended.chart_info ResolveChartInfoForProjection"));
        StringAssert.Contains(regularOwnerCode, "mainChartList.RowProjection.BuildNormalSourceRows(");
        StringAssert.Contains(regularOwnerCode, "mainChartList.RowProjection.BuildPackageSourceRows(");
        StringAssert.Contains(regularOwnerCode, "mainChartList.RowProjection.BuildStandardSourceRows(");
        Assert.IsFalse(viewModelCode.Contains("MainChartList.RowProjection.CreatePlaylistDetailSourceRow("));
        StringAssert.Contains(playlistDataSourceCode, "rowProjection.CreatePlaylistDetailSourceRow(");
        StringAssert.Contains(projectionOwnerCode, "private readonly Dictionary<string, ChartFileTransientState> transientStatesByKey");
        Assert.IsFalse(projectionOwnerCode.Contains("MainWindowViewModel"));
        Assert.IsFalse(projectionOwnerCode.Contains("Application.Current"));
        Assert.IsFalse(projectionOwnerCode.Contains("DispatcherHelper"));
        Assert.IsFalse(projectionOwnerCode.Contains("ForTest"));
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationOwnsStorageRowSourceRefresh()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string rootViewModelCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs"));
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string regularOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs"));
        string resourceHealthOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "SelectedChartResourceHealthWorkflowOwner.cs"));
        string refreshEventHandler = ExtractBetween(
            viewModelCode,
            "private void RegularChartListOwnerNormalLibraryRefreshApplied",
            "private void PlaylistWorkspacePlaylistDetailScoreSnapshotRefreshRequested");

        StringAssert.Contains(regularOwnerCode, "AttachNormalLibraryRefreshSource(BMSLibrary library)");
        StringAssert.Contains(regularOwnerCode, "RegisterHandler(");
        StringAssert.Contains(regularOwnerCode, "QueueLatestNormalLibraryRefreshNotification(");
        StringAssert.Contains(viewModelCode, "regularChartListOwner.QueueLatestNormalLibraryRefreshNotification(\"library_charts_changed\")");
        Assert.IsFalse(viewModelCode.Contains("fallbackToCurrentOwnedCollectionVersion"));
        Assert.IsFalse(viewModelCode.Contains("NotifiesInstallDestinationOverlayProperties"));
        Assert.IsFalse(libraryCode.Contains("NotifiesInstallDestinationOverlayProperties"));
        StringAssert.Contains(regularOwnerCode, "SyncNormalLibraryStorageRowCachesForRefreshNotification(library, notificationBatch)");
        StringAssert.Contains(regularOwnerCode, "ApplyNormalLibraryRefreshNotificationEffects(notificationBatch, reason)");
        StringAssert.Contains(regularOwnerCode, "InvalidateNormalLibrarySortDependency(MainViewDataDependency.Maintenance, \"maintenance_changed\")");
        StringAssert.Contains(refreshEventHandler, "RefreshNormalLibraryAfterSourceChanged(request.Reason)");
        StringAssert.Contains(refreshEventHandler, "RefreshNormalLibraryForNotificationPresentationEffects(request.NotificationBatch)");
        StringAssert.Contains(rootViewModelCode, "invalidateSortDependency: false");
        string maintenanceCompletionHandler = ExtractMethodBody(
            rootViewModelCode,
            "private void MaintenanceRescanWorkflowCompletionPublished(MaintenanceRescanCompletionReceipt receipt)");
        StringAssert.Contains(resourceHealthOwnerCode, "internal event EventHandler RescanCompleted;");
        StringAssert.Contains(resourceHealthOwnerCode, "RescanCompleted?.Invoke(this, EventArgs.Empty);");
        StringAssert.Contains(rootViewModelCode, "SelectedChartResourceHealth.RescanCompleted += SelectedChartResourceHealthRescanCompleted;");
        Assert.IsFalse(rootViewModelCode.Contains("ISelectedChartResourceHealthRefreshPort"));
        Assert.IsFalse(rootViewModelCode.Contains("selectedChartResourceHealthRefresh"));
        Assert.IsFalse(rootViewModelCode.Contains("ForceResourceHealthCheckCharts("));
        Assert.IsFalse(rootViewModelCode.Contains("SetChartResourceWarningsIgnored("));
        StringAssert.Contains(maintenanceCompletionHandler, "RefreshResourceHealthViewsAfterMaintenanceChanged");
        StringAssert.Contains(maintenanceCompletionHandler, "invalidateSortDependency: false");
        StringAssert.Contains(regularOwnerCode, "notificationBatch.NotifiesBmsFiles");
        StringAssert.Contains(regularOwnerCode, "notificationBatch.NotifiesBmsonSongs");
        StringAssert.Contains(regularOwnerCode, "OwnedChartStorageOwnerView sourceOwnerView = notificationBatch.NotifiesBmsFiles");
        StringAssert.Contains(regularOwnerCode, "PruneBmsRows(sourceOwnerView?.BmsFiles)");
        Assert.IsFalse(regularOwnerCode.Contains("library?.BMSFiles"));
        StringAssert.Contains(regularOwnerCode, "SyncBmsonRows(library, sourceOwnerView)");
        Assert.IsFalse(rootViewModelCode.Contains("NormalLibraryRefreshNotificationBatch refreshNotification"));
        Assert.IsFalse(rootViewModelCode.Contains("ApplyNormalLibraryRefreshNotificationBatch("));
        Assert.IsFalse(rootViewModelCode.Contains("listenerForBMSLibrary.RegisterHandler(() => files.BMSFiles"));
        Assert.IsFalse(rootViewModelCode.Contains("listenerForBMSLibrary.RegisterHandler(() => files.BmsonSongs"));
        Assert.IsFalse(rootViewModelCode.Contains("private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFiles"));
        Assert.IsFalse(rootViewModelCode.Contains("files?.BMSFiles"));
        StringAssert.Contains(libraryCode, "internal BmsFileLevelOverwriteOutcome ReplaceBmsFileLevelByTableEntryLevel(BMSTable bmsTable)");
        StringAssert.Contains(libraryCode, "List<BMSFile> bmsFiles = [.. from file in _BMSFiles ?? []");
        StringAssert.Contains(libraryCode, "catalogMutationOwner.ApplyPlaylistLevelRows(bmsFiles)");
    }

    [TestMethod]
    public void Lr2SongDbRuntimeWritesUseLr2SongDbSyncStatusBoundary()
    {
        string libraryCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "BMSLibrary.cs");
        string synchronizationOwnerCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Models", "BMSLibrary.Lr2SynchronizationOwner.cs");
        Assert.IsFalse(libraryCode.Contains("dbGateway.UpsertSongs("));
        Assert.IsFalse(libraryCode.Contains("dbGateway.UpdateSongLevels("));
        Assert.IsFalse(libraryCode.Contains("ExecuteLr2SongDbWrite("));
        StringAssert.Contains(libraryCode, "catalogMutationOwner.ApplyModeChangeSongRows(list)");
        StringAssert.Contains(libraryCode, "catalogMutationOwner.ApplyPlaylistLevelRows(bmsFiles)");
        StringAssert.Contains(synchronizationOwnerCode, "\"lr2_song_db_write failed\"");
        StringAssert.Contains(synchronizationOwnerCode, "failureFact.ExceptionTypeName");
    }

    [TestMethod]
    public void OwnedMutationDispatcherDoesNotUseLegacyInvalidationSuppressions()
    {
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string[] removedSuppressions =
        [
            "SuppressInstalledChartLookupInvalidation",
            "SuppressParentFolderListInvalidationOnCurrentThread",
            "SuppressDuplicateChartGroupsInvalidationOnCurrentThread",
            "SuppressPlaylistSummaryOwnedHashInvalidationOnCurrentThread",
            "SuppressOwnedChartCollectionInvalidation",
            "SuppressOwnedChartCollectionChangeNotificationOnCurrentThread",
            "SuppressInstallDestinationRuntimeStatePruning"
        ];

        foreach (string removedSuppression in removedSuppressions)
        {
            Assert.IsFalse(libraryCode.Contains(removedSuppression), removedSuppression);
        }
        StringAssert.Contains(libraryCode, "resourceHealthOwner.SuppressInvalidation()");
    }

    [TestMethod]
    public void DuplicateFilterViewUsesChartFileParameters()
    {
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string ownerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs");

        Assert.IsFalse(viewModelCode.Contains("CreateBmsChartSnapshot("));
        Assert.IsFalse(viewModelCode.Contains("private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesGarbled"));
        Assert.IsFalse(viewModelCode.Contains("private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesUnregistered"));
        Assert.IsFalse(libraryCode.Contains("public IEnumerable<BMSFile> BMSFilesGarbled"));
        Assert.IsFalse(libraryCode.Contains("public List<BMSFile> BMSFilesUnregistered"));
        StringAssert.Contains(libraryCode, "internal IEnumerable<ChartFile> ChartFilesGarbled");
        StringAssert.Contains(libraryCode, "internal IEnumerable<ChartFile> ChartFilesUnregistered");
        StringAssert.Contains(ownerCode, "internal static bool TryResolveDuplicateSource(");
        Assert.IsFalse(ownerCode.Contains("List<BeMusicSeeker.Models.BMSFile>"));
    }

    [TestMethod]
    public void VirtualChartSubsetRoutesTerminalApplyThroughRegularChartOwner()
    {
        string root = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.cs");
        string owner = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs");

        Assert.IsFalse(root.Contains("TryApplyVirtualChartSubsetLibraryView"));
        Assert.IsFalse(root.Contains("ApplyMainLibraryChartListView"));
        Assert.IsFalse(root.Contains("private ChartListOrder GetOrCreateVirtualChartSubsetOrder("));
        Assert.IsFalse(root.Contains("private static int[] ApplyVirtualChartSubsetFilters("));
        Assert.IsFalse(root.Contains("ComputeVirtualChartSubsetSourceRowsSignatureForTest"));
        StringAssert.Contains(owner, "internal RegularChartListEntryResult ApplyRegularView(");
        StringAssert.Contains(owner, "TryApplyVirtualChartSubset(");
        StringAssert.Contains(owner, "TryCommitVirtual(");
    }

    [TestMethod]
    public void RegularChartCacheInvalidationUsesTypedOwnerDependencies()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string installDestinationOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PendingPackageWorkflowOwner.cs");

        Assert.IsFalse(viewModelCode.Contains("private void InvalidateNormalLibrarySortKeys(string reason)"));
        StringAssert.Contains(viewModelCode, "regularChartListOwner.InvalidateIdentitySortKeys(clearSourceRows)");
        StringAssert.Contains(viewModelCode, "regularChartListOwner.InvalidateSortCacheByDependency(dependency");
        StringAssert.Contains(installDestinationOwnerCode, "PublishMutationApplied(");
        StringAssert.Contains(viewModelCode, "mutationApplied.InstallDestinationStateChanged");
        StringAssert.Contains(viewModelCode, "MainViewDataDependency.InstallDestination");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibrarySortDependency(MainViewDataDependency.Maintenance");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibrarySortDependency(MainViewDataDependency.Warning");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibraryReferenceTableSortKeys()");
    }

    [TestMethod]
    public void ChartContextMenu_BmsOnlyAndChartCommonHandlersUseExpectedSelectionHelpers()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string selectedMutationOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "SelectedChartMutationWorkflowOwner.cs");
        StringAssert.Contains(viewModelCode, "SelectedChartMutations.WorkflowChanged += SelectedChartMutationWorkflowChanged;");
        StringAssert.Contains(selectedMutationOwnerCode, "chartMutationActivity.Enter()");
        StringAssert.Contains(selectedMutationOwnerCode, "PublishRefreshSuppressionChanged(isSuppressed: true");
        StringAssert.Contains(selectedMutationOwnerCode, "PublishMutationApplied(libraryPathChanged: true)");
        Assert.IsFalse(viewModelCode.Contains("ISelectedChartMutationActivityPort"));
        Assert.IsFalse(viewModelCode.Contains("ISelectedChartMutationRefreshPort"));
        Assert.IsFalse(selectedMutationOwnerCode.Contains("ISelectedChartMutationActivityPort"));
        Assert.IsFalse(selectedMutationOwnerCode.Contains("ISelectedChartMutationRefreshPort"));
        Assert.IsFalse(viewModelCode.Contains("SelectedChartMutationActivityChangedEventArgs"));
        Assert.IsFalse(selectedMutationOwnerCode.Contains("SelectedChartMutationActivityChangedEventArgs"));
        string contextMenuResource = ExtractBetween(
            mainWindowCode,
            "private bool TryGetTableContextMenuResource",
            "private void keywordSearchBoxTextChanged");
        string renameInvalidExtensionClick = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemRenameBMSFileClick",
            "private async void tableContextMenuItemRemoveBMSFileClick");
        string deleteClick = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemRemoveBMSFileClick",
            "private async void tableContextMenuItemMoveFileClick");
        string encodingFixClick = ExtractBetween(
            mainWindowCode,
            "private void fixEncodingSelectedBMS",
            "private void ignoreFileScanCheckSelectedCharts");
        string audioConvertClick = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemConvertToAudioFileClick",
            "private void playlistTableDrop");
        string resourceHealthClick = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemForceFileScanCheckSelectedCharts",
            "private async void tableContextMenuRemoveInstallDestinationClick");
        string resourceHealthIgnoreClick = ExtractBetween(
            mainWindowCode,
            "private void ignoreFileScanCheckSelectedCharts",
            "private void notIgnoredFileScanCheckSelectedCharts");
        string resourceHealthUnignoreClick = ExtractBetween(
            mainWindowCode,
            "private void notIgnoredFileScanCheckSelectedCharts",
            "private async void forceInstallSelectedPendingCharts");

        StringAssert.Contains(contextMenuResource, "TryResolveTableContextMenuPolicy(row, GetCurrentChartOperationSourceScope(), out usePlaylistMissingContextMenu)");
        Assert.IsFalse(contextMenuResource.Contains("GetRealBmsFile"));
        StringAssert.Contains(renameInvalidExtensionClick, "GetSelectedChartTargets(ChartOperationCapabilities.RenameInvalidExtension)");
        StringAssert.Contains(renameInvalidExtensionClick, "SelectedInvalidExtensionRenameRequest");
        StringAssert.Contains(renameInvalidExtensionClick, "viewModel.SelectedChartMutations");
        Assert.AreEqual(1, CountOccurrences(renameInvalidExtensionClick, "RenameInvalidExtensionsAsync("));
        StringAssert.Contains(renameInvalidExtensionClick, "e.Handled = true;");
        Assert.IsFalse(renameInvalidExtensionClick.Contains("ConfirmRenameInvalidExtensions"));
        Assert.IsFalse(renameInvalidExtensionClick.Contains("SelectedInvalidExtensionRenameConfirmationResult"));
        Assert.IsFalse(renameInvalidExtensionClick.Contains("SelectedInvalidExtensionRenameOperation"));
        Assert.IsFalse(renameInvalidExtensionClick.Contains("confirmation.Operation"));
        Assert.IsFalse(renameInvalidExtensionClick.Contains("viewModel.RenameBMSFilesExtensions"));
        Assert.IsFalse(renameInvalidExtensionClick.Contains("Task.Run"));
        StringAssert.Contains(deleteClick, "GetCurrentMainViewOperationSection()");
        StringAssert.Contains(deleteClick, "GetSelectedChartTargets(IsPendingMainViewSection(section))");
        StringAssert.Contains(deleteClick, "TryGetContextMenuChartTarget(sender, e.Source, out ChartOperationTarget contextTarget)");
        StringAssert.Contains(deleteClick, "SelectedChartDeleteRequest");
        StringAssert.Contains(deleteClick, "e.Handled = true;");
        Assert.AreEqual(1, CountOccurrences(deleteClick, "DeleteAsync("));
        Assert.IsFalse(deleteClick.Contains("ConfirmDelete"));
        Assert.IsFalse(deleteClick.Contains("SelectedChartDeleteConfirmationResult"));
        Assert.IsFalse(deleteClick.Contains("SelectedChartDeleteOperation"));
        Assert.IsFalse(deleteClick.Contains("confirmation.Operation"));
        Assert.IsFalse(deleteClick.Contains("Task.Run"));
        Assert.IsFalse(deleteClick.Contains("RemoveLibraryCharts"));
        Assert.IsFalse(deleteClick.Contains("RemovePendingCharts"));
        StringAssert.Contains(encodingFixClick, "SelectedChartEncodingRequest");
        StringAssert.Contains(encodingFixClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunBmsEncodingFix)");
        StringAssert.Contains(encodingFixClick, "viewModel.SelectedChartMutations.ApplyEncoding(request)");
        Assert.IsFalse(encodingFixClick.Contains("GetSelectedBmsFiles(ChartOperationCapabilities.RunBmsEncodingFix)"));
        Assert.IsFalse(encodingFixClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunBmsEncodingFix)"));
        Assert.IsFalse(encodingFixClick.Contains("FixEncodingBMSFiles"));
        Assert.IsFalse(encodingFixClick.Contains("SetBMSFilesEncoding"));
        Assert.IsFalse(encodingFixClick.Contains("Task.Run"));
        StringAssert.Contains(audioConvertClick, "SelectedChartAudioConversionRequest");
        StringAssert.Contains(audioConvertClick, "GetSelectedChartTargets(ChartOperationCapabilities.ConvertToAudio)");
        StringAssert.Contains(audioConvertClick, "viewModel.SelectedChartAudioConversion.RunAsync(request)");
        Assert.IsFalse(audioConvertClick.Contains("GetSelectedBmsFiles(ChartOperationCapabilities.ConvertToAudio)"));
        Assert.IsFalse(audioConvertClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.ConvertToAudio)"));
        Assert.IsFalse(audioConvertClick.Contains("PickFolderAsync"));
        Assert.IsFalse(audioConvertClick.Contains("Settings.Default"));
        Assert.IsFalse(audioConvertClick.Contains("Task.Run"));
        Assert.IsFalse(audioConvertClick.Contains("RunProgressUntilTaskCompletesAsync"));
        Assert.IsFalse(audioConvertClick.Contains("ConvertBMSToAudioFiles"));
        Assert.IsFalse(viewModelCode.Contains("ConvertBMSToAudioFiles"));
        StringAssert.Contains(resourceHealthClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthClick, "ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request)");
        StringAssert.Contains(resourceHealthClick, "viewModel.SelectedChartResourceHealth.RescanAsync(request)");
        Assert.IsFalse(resourceHealthClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(resourceHealthClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        StringAssert.Contains(resourceHealthIgnoreClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthIgnoreClick, "ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request)");
        StringAssert.Contains(resourceHealthIgnoreClick, "viewModel.SelectedChartResourceHealth.SetWarningsIgnored(request)");
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        StringAssert.Contains(resourceHealthUnignoreClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthUnignoreClick, "ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request)");
        StringAssert.Contains(resourceHealthUnignoreClick, "viewModel.SelectedChartResourceHealth.SetWarningsIgnored(request, unset: true)");
        Assert.IsFalse(resourceHealthUnignoreClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(resourceHealthUnignoreClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        Assert.IsFalse(resourceHealthClick.Contains("GetSelectedBmsChartFiles(ChartOperationCapabilities.RunResourceHealthCheck)"));
        Assert.IsFalse(resourceHealthClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunResourceHealthCheck)"));
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("GetSelectedBmsChartFiles(ChartOperationCapabilities.RunResourceHealthCheck)"));
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunResourceHealthCheck)"));
        Assert.IsFalse(resourceHealthUnignoreClick.Contains("GetSelectedBmsChartFiles(ChartOperationCapabilities.RunResourceHealthCheck)"));
        Assert.IsFalse(resourceHealthUnignoreClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunResourceHealthCheck)"));
        Assert.IsFalse(mainWindowCode.Contains("GetSelectedBmsChartFiles("));
        Assert.IsFalse(mainWindowCode.Contains("GetSelectedChartCompatibilityAdapters("));
        Assert.IsFalse(mainWindowCode.Contains("GetChartCompatibilityAdapterFromTarget"));
    }

    [TestMethod]
    public void SelectedChartExternalActionsRouteThroughFeatureOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string ownerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "SelectedChartExternalActionWorkflowOwner.cs");
        string explorerClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemOpenExplorerClick",
            "private async void tableContextMenuItemOpenInstallDestinationClick");
        string fileClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemOpenBMSFileClick",
            "private void tableContextMenuItemOpenLR2IRClick");
        string lr2IrClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemOpenLR2IRClick",
            "private void tableContextMenuItemOpenMochaClick");
        string mochaClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemOpenMochaClick",
            "private void tableContextMenuItemOpenMinIRClick");
        string minIrClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemOpenMinIRClick",
            "private async void tableContextMenuItemOpenURLClick");
        string normalMenu = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuOpened",
            "private void tableContextMenuPlaylistMissingOpened");
        string playlistMissingMenu = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuPlaylistMissingOpened",
            "private void playHistoryContextMenuOpened");

        StringAssert.Contains(explorerClick, "SelectedChartExternalActionKind.OpenExplorer");
        StringAssert.Contains(fileClick, "SelectedChartExternalActionKind.OpenFile");
        StringAssert.Contains(lr2IrClick, "SelectedChartExternalActionKind.OpenLr2Ir");
        StringAssert.Contains(mochaClick, "SelectedChartExternalActionKind.OpenMocha");
        StringAssert.Contains(minIrClick, "SelectedChartExternalActionKind.OpenMinIr");
        foreach (string handler in new[] { explorerClick, fileClick, lr2IrClick, mochaClick, minIrClick })
        {
            Assert.AreEqual(1, CountOccurrences(handler, "SelectedChartExternalActions.Execute("));
            Assert.IsFalse(handler.Contains("Process.Start"));
            Assert.IsFalse(handler.Contains("ExplorerOpenService"));
            Assert.IsFalse(handler.Contains("LongPathFileSystem.FileExists"));
        }
        Assert.IsFalse(mainWindowCode.Contains("GetBmsIrSongUrl"));
        Assert.IsFalse(mainWindowCode.Contains("GetMochaSongUrl"));
        Assert.IsFalse(mainWindowCode.Contains("GetMinIrSongUrl"));
        Assert.IsFalse(mainWindowCode.Contains("OpenRepositoryUrlForRow"));
        StringAssert.Contains(normalMenu, "SelectedChartExternalActions.CanExecute(");
        StringAssert.Contains(playlistMissingMenu, "SelectedChartExternalActions.CanExecute(");
        Assert.IsFalse(normalMenu.Contains("GetRepositorySha256(row)"));
        Assert.IsFalse(playlistMissingMenu.Contains("GetRepositorySha256(row)"));
        Assert.IsFalse(normalMenu.Contains("contextMenuState.CanOpenLr2Ir"));
        Assert.IsFalse(normalMenu.Contains("contextMenuState.ChartPath"));
        Assert.IsFalse(normalMenu.Contains("contextMenuState.RowTarget"));
        StringAssert.Contains(viewModelCode, "SelectedChartExternalActions = childComposition.SelectedChartExternalActions;");
        StringAssert.Contains(ownerCode, "SelectedChartExternalActionKind.OpenExplorer");
        StringAssert.Contains(ownerCode, "internal bool CanExecute(");
        StringAssert.Contains(ownerCode, "bms-ir.org/new/song?songmd5=");
        StringAssert.Contains(ownerCode, "mocha-repository.info/song.php?sha256=");
        StringAssert.Contains(ownerCode, "gaftalk.com/minir/#/viewer/song/");
    }

    [TestMethod]
    public void RelatedDocumentMenuRoutesThroughFeatureOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string ownerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "SelectedChartExternalActionWorkflowOwner.cs");
        string normalMenu = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuOpened",
            "private void tableContextMenuPlaylistMissingOpened");
        string clickHandler = ExtractMethodBody(
            mainWindowCode,
            "private void tableContextMenuItemOpenDocumentFileClick");

        StringAssert.Contains(normalMenu, "SelectedChartExternalActions.CanQueryRelatedDocuments(rowTarget)");
        StringAssert.Contains(normalMenu, "PopulateRelatedDocumentsMenuAsync(");
        Assert.IsFalse(normalMenu.Contains("Task.Run"));
        Assert.IsFalse(normalMenu.Contains("LongPathFileSystem.EnumerateFiles"));
        Assert.IsFalse(normalMenu.Contains("DirectoryExt.GetDirectoryNameSimple"));
        Assert.IsFalse(mainWindowCode.Contains("changeSubmenuOpenDocumentTask"));
        Assert.IsFalse(mainWindowCode.Contains("tableContextMenuTaskTokenSource"));
        Assert.IsFalse(mainWindowCode.Contains("calcelAllContextMenuTasks"));
        Assert.IsFalse(clickHandler.Contains("Process.Start"));
        StringAssert.Contains(clickHandler, "SelectedChartExternalActions.OpenRelatedDocument(dataContext)");
        StringAssert.Contains(ownerCode, "QueryRelatedDocumentsAsync(");
        StringAssert.Contains(ownerCode, "OpenRelatedDocument(string path)");
        StringAssert.Contains(ownerCode, "relatedDocumentFileEnumerator");
    }

    [TestMethod]
    public void MainWindowUsesExplicitApplicationCompositionBindingRoot()
    {
        string root = FindRepositoryRoot();
        string appCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "App.cs"));
        string appXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "App.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string mainWindowXaml = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Views", "MainWindow.xaml");

        Assert.IsFalse(appXaml.Contains("StartupUri"));
        Assert.AreEqual(1, CountOccurrences(appCode, "new ApplicationComposition("));
        StringAssert.Contains(appCode, "uiScheduler: new WpfUiScheduler(() => base.Dispatcher)");
        Assert.AreEqual(1, CountOccurrences(appCode, "MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();"));
        Assert.AreEqual(1, CountOccurrences(appCode, "Resources[\"vm\"] = viewModel;"));
        Assert.AreEqual(1, CountOccurrences(appCode, "MainWindow mainWindow = new(viewModel);"));
        Assert.AreEqual(1, CountOccurrences(appCode, "MainWindow = mainWindow;"));
        Assert.AreEqual(1, CountOccurrences(appCode, "mainWindow.Show();"));
        StringAssert.Contains(appCode, "await CreateAndShowMainWindowAsync().ConfigureAwait(true);");
        StringAssert.Contains(appCode, "catch (Exception exception)");
        StringAssert.Contains(appCode, "await HandleStartupCompositionFailureAsync(exception).ConfigureAwait(true);");
        StringAssert.Contains(appCode, "MarkCoordinatedShutdownStarted(\"startup_composition_failed\");");
        StringAssert.Contains(appCode, "ReleaseSingleInstanceMutex();");
        StringAssert.Contains(appCode, "Shutdown(1);");
        int compositionIndex = appCode.IndexOf("ApplicationComposition composition =", StringComparison.Ordinal);
        int resourceIndex = appCode.IndexOf("Resources[\"vm\"] = viewModel;", StringComparison.Ordinal);
        int windowIndex = appCode.IndexOf("MainWindow mainWindow = new(viewModel);", StringComparison.Ordinal);
        int showIndex = appCode.IndexOf("mainWindow.Show();", StringComparison.Ordinal);
        Assert.IsTrue(compositionIndex >= 0 && compositionIndex < resourceIndex);
        Assert.IsTrue(resourceIndex < windowIndex && windowIndex < showIndex);
        Assert.IsFalse(mainWindowXaml.Contains("MainWindowViewModelResourceExtension"));
        Assert.IsFalse(mainWindowXaml.Contains("DataContext=\"{DynamicResource vm}\""));
        StringAssert.Contains(mainWindowCode, "public MainWindow(MainWindowViewModel viewModel)");
        StringAssert.Contains(mainWindowCode, "DataContext = viewModel;");
        int dataContextIndex = mainWindowCode.IndexOf("DataContext = viewModel;", StringComparison.Ordinal);
        int initializeComponentIndex = mainWindowCode.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        Assert.IsTrue(dataContextIndex >= 0 && dataContextIndex < initializeComponentIndex);
        Assert.IsFalse(mainWindowCode.Contains("ApplicationComposition.CreateDefault().SaveSettings()"));

        string productionCode = string.Join(
            Environment.NewLine,
            Directory.GetFiles(Path.Combine(root, "BeMusicSeeker"), "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
        Assert.IsFalse(productionCode.Contains("MainWindowViewModelResourceExtension"));
        Assert.IsFalse(productionCode.Contains("public MainWindow()"));
        Assert.IsFalse(productionCode.Contains("public MainWindowViewModel()"));
    }

    [TestMethod]
    public void FullResourceHealthContextMenu_RoutesConfirmationThroughWorkflowOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string handler = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemForceFileScanCheckAllCharts",
            "private async void tableContextMenuItemRemoveChartInfoParseFailureClick");

        StringAssert.Contains(handler, "ShouldBlockStartupUiInteraction(\"datagrid_context_menu_full_scan_all_charts\")");
        StringAssert.Contains(handler, "viewModel.MaintenanceRescanWorkflow.RequestStartAsync()");
        StringAssert.Contains(handler, "MaintenanceRescanStartStatus.Failed");
        Assert.IsFalse(handler.Contains("UiDialogRoute.ShowMessageBox"));
        Assert.IsFalse(handler.Contains("MaintenanceRescanWorkflow.Start"));
        int blockIndex = handler.IndexOf("ShouldBlockStartupUiInteraction", StringComparison.Ordinal);
        int requestIndex = handler.IndexOf("RequestStartAsync", StringComparison.Ordinal);
        Assert.IsTrue(blockIndex >= 0 && requestIndex > blockIndex, "Startup blocking must precede the confirmation request.");
    }

    [TestMethod]
    public void PendingPackageChartHandlersUseChartTargetsForPackageOperations()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string pendingPackageOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PendingPackageWorkflowOwner.cs");
        string normalMenu = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuOpened",
            "private void tableContextMenuPlaylistMissingOpened");
        string chartContextMenuStateCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "ChartContextMenuState.cs");
        string forceInstall = ExtractBetween(
            mainWindowCode,
            "private async void forceInstallSelectedPendingCharts",
            "private async void manualInstallSelectedPendingCharts");
        string manualInstall = ExtractBetween(
            mainWindowCode,
            "private async void manualInstallSelectedPendingCharts",
            "private async void searchInstallDestinationSelectedPendingCharts");
        string deletePackages = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemDeleteInstallPackagesClick",
            "private async void searchMergeDestinationSelectedPendingCharts");
        string estimateSearch = ExtractBetween(
            mainWindowCode,
            "private async void searchInstallDestinationSelectedPendingCharts",
            "private async void tableContextMenuItemDeleteInstallPackagesClick");
        string mergeSearch = ExtractBetween(
            mainWindowCode,
            "private async void searchMergeDestinationSelectedPendingCharts",
            "private async void tableContextMenuItemConvertToAudioFileClick");
        string openInstallDestination = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemOpenInstallDestinationClick",
            "private async void treeViewInstallPackageContextMenuOpenInstallDestinationClick");
        string openPackageInstallDestination = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuOpenInstallDestinationClick",
            "private void tableContextMenuItemOpenBMSFileClick");
        string openPackageSource = ExtractBetween(
            mainWindowCode,
            "private void treeViewInstallPackageContextMenuOpenExplorerClick",
            "private async void treeViewInstallPackageContextMenuClearFolderClick");
        string clearPackageDestination = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuRemoveInstallDestinationClick",
            "private async void treeViewInstallPackageContextMenuForceInstallClick");
        string forceInstallPackage = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuForceInstallClick",
            "private async void treeViewInstallPackageContextMenuManualInstallClick");
        string manualInstallPackage = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuManualInstallClick",
            "private async void treeViewInstallPackageContextMenuSearchInstallationDirectoryClick");
        string searchPackageDestination = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuSearchInstallationDirectoryClick",
            "private async void treeViewInstallPackageContextMenuSearchMergeDestinationClick");
        string searchPackageMergeDestination = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPackageContextMenuSearchMergeDestinationClick",
            "private void treeViewDuplicateFolderContextMenuOpened");

        StringAssert.Contains(forceInstall, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(forceInstall, "PendingInstallPackageOperationRequest.CreateForceInstall(targets)");
        StringAssert.Contains(forceInstall, "viewModel.PendingPackages");
        StringAssert.Contains(forceInstall, ".InstallPendingAsync(request)");
        StringAssert.Contains(forceInstall, "ObservePendingPackageMutationAsync(");
        StringAssert.Contains(forceInstall, "PackageCatalogSelectionPlan");
        Assert.IsFalse(forceInstall.Contains("prepareShellForMutation"));
        Assert.IsFalse(forceInstall.Contains("() =>"));
        StringAssert.Contains(forceInstall, "private async Task ForceInstallSelectedPendingChartsAsync");
        Assert.IsFalse(forceInstall.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(forceInstall, "ApplyPendingPackageMutationViewAsync(");
        Assert.IsFalse(forceInstall.Contains("selectionPlan.Apply(this);"));
        Assert.IsFalse(forceInstall.Contains("NavigateInstallAsync"));
        StringAssert.Contains(manualInstall, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(manualInstall, "PendingInstallPackageOperationRequest.CreateManualInstall(targets)");
        StringAssert.Contains(manualInstall, "viewModel.PendingPackages");
        StringAssert.Contains(manualInstall, ".InstallPendingAsync(request)");
        StringAssert.Contains(manualInstall, "ObservePendingPackageMutationAsync(");
        StringAssert.Contains(manualInstall, "PackageCatalogSelectionPlan");
        Assert.IsFalse(manualInstall.Contains("prepareShellForMutation"));
        Assert.IsFalse(manualInstall.Contains("() =>"));
        StringAssert.Contains(manualInstall, "private async Task ManualInstallSelectedPendingChartsAsync");
        Assert.IsFalse(manualInstall.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(manualInstall, "ApplyPendingPackageMutationViewAsync(");
        Assert.IsFalse(manualInstall.Contains("selectionPlan.Apply(this);"));
        Assert.IsFalse(manualInstall.Contains("NavigateInstallAsync"));
        StringAssert.Contains(deletePackages, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(deletePackages, "PackageCatalogRemovalRequest.CreatePending(selectedPendingTargets)");
        StringAssert.Contains(deletePackages, "PackageCatalogRemovalRequest.CreateInstalled(selectedInstalledTargets)");
        StringAssert.Contains(deletePackages, "viewModel.PackageCatalog");
        StringAssert.Contains(deletePackages, ".RemoveSelectionAsync(");
        Assert.AreEqual(1, CountOccurrences(deletePackages, ".RemoveSelectionAsync("));
        Assert.IsFalse(deletePackages.Contains("ConfirmRemoveSelection"));
        StringAssert.Contains(deletePackages, "ObservePackageCatalogMutationAsync(");
        Assert.IsFalse(viewModelCode.Contains("DeleteInstallPackageRecordsRequest"));
        Assert.IsFalse(viewModelCode.Contains("InstallPendingCharts(PendingInstallPackageOperationRequest request)"));
        StringAssert.Contains(deletePackages, "private async Task RemovePackageCatalogSelectionFromContextMenuAsync");
        Assert.IsFalse(deletePackages.Contains("UiDialogRoute"));
        Assert.IsFalse(deletePackages.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        Assert.IsFalse(deletePackages.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(deletePackages.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(deletePackages.Contains("ObservePackageCatalogConfirmation"));
        StringAssert.Contains(deletePackages, "ApplyPackageCatalogMutationViewAsync(");
        Assert.IsFalse(deletePackages.Contains("selectionPlan.Apply(this);"));
        Assert.IsFalse(deletePackages.Contains("NavigateInstallAsync"));
        int handledIndex = deletePackages.LastIndexOf("e.Handled = true;", StringComparison.Ordinal);
        int mutationObserverIndex = deletePackages.LastIndexOf("ObservePackageCatalogMutationAsync(", StringComparison.Ordinal);
        Assert.IsTrue(handledIndex >= 0 && mutationObserverIndex > handledIndex);
        StringAssert.Contains(estimateSearch, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(estimateSearch, "PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch(targets)");
        StringAssert.Contains(estimateSearch, "viewModel.PendingPackages");
        StringAssert.Contains(estimateSearch, ".SearchPendingAsync(request)");
        StringAssert.Contains(estimateSearch, ".LoggingAndPropagate(");
        StringAssert.Contains(estimateSearch, "private async Task SearchInstallDestinationSelectedPendingChartsAsync");
        Assert.IsFalse(estimateSearch.Contains("PendingInstallDestinationTargetSnapshot"));
        Assert.IsFalse(estimateSearch.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(mergeSearch, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(mergeSearch, "PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch(targets)");
        StringAssert.Contains(mergeSearch, "viewModel.PendingPackages");
        StringAssert.Contains(mergeSearch, ".SearchPendingAsync(request)");
        StringAssert.Contains(mergeSearch, ".LoggingAndPropagate(");
        StringAssert.Contains(mergeSearch, "private async Task SearchMergeDestinationSelectedPendingChartsAsync");
        Assert.IsFalse(mergeSearch.Contains("PendingInstallDestinationTargetSnapshot"));
        Assert.IsFalse(mergeSearch.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        Assert.IsFalse(viewModelCode.Contains("internal void SearchPendingInstallDestination(PendingInstallDestinationSearchRequest request)"));
        Assert.IsFalse(mainWindowCode.Contains("private bool ConfirmMergeDestinationSearch"));
        StringAssert.Contains(clearPackageDestination, "viewModel.PendingPackages");
        StringAssert.Contains(clearPackageDestination, ".ClearPackagesAsync([pkg])");
        StringAssert.Contains(clearPackageDestination, ".LoggingAndPropagate(");
        Assert.IsTrue(clearPackageDestination.IndexOf("e.Handled = true", StringComparison.Ordinal) < clearPackageDestination.IndexOf("await viewModel.PendingPackages", StringComparison.Ordinal));
        StringAssert.Contains(forceInstallPackage, ".ForceInstallPackagesAsync(");
        StringAssert.Contains(forceInstallPackage, "ObservePendingPackageMutationAsync(");
        StringAssert.Contains(forceInstallPackage, "CaptureNextSiblingOrRoot(");
        Assert.IsFalse(forceInstallPackage.Contains("prepareShellForMutation"));
        Assert.IsFalse(forceInstallPackage.Contains("() =>"));
        StringAssert.Contains(forceInstallPackage, "ApplyPendingPackageMutationViewAsync(");
        Assert.IsFalse(forceInstallPackage.Contains("selectionPlan.Apply(this);"));
        Assert.IsFalse(forceInstallPackage.Contains("NavigateInstallAsync"));
        Assert.IsTrue(forceInstallPackage.IndexOf("e.Handled = true", StringComparison.Ordinal) < forceInstallPackage.IndexOf("viewModel.PendingPackages", StringComparison.Ordinal));
        Assert.IsFalse(forceInstallPackage.Contains("viewModel.ForceInstallPendingPackages"));
        StringAssert.Contains(manualInstallPackage, ".ManualInstallPackagesAsync(");
        StringAssert.Contains(manualInstallPackage, "ObservePendingPackageMutationAsync(");
        StringAssert.Contains(manualInstallPackage, "CaptureNextSiblingOrRoot(");
        Assert.IsFalse(manualInstallPackage.Contains("prepareShellForMutation"));
        Assert.IsFalse(manualInstallPackage.Contains("() =>"));
        StringAssert.Contains(manualInstallPackage, "ApplyPendingPackageMutationViewAsync(");
        Assert.IsFalse(manualInstallPackage.Contains("selectionPlan.Apply(this);"));
        Assert.IsFalse(manualInstallPackage.Contains("NavigateInstallAsync"));
        Assert.IsTrue(manualInstallPackage.IndexOf("e.Handled = true", StringComparison.Ordinal) < manualInstallPackage.IndexOf("viewModel.PendingPackages", StringComparison.Ordinal));
        Assert.IsFalse(manualInstallPackage.Contains("Settings.Default"));
        StringAssert.Contains(searchPackageDestination, ".SearchPackagesAsync(PendingInstallDestinationSearchKind.InstallDestination, [pkg])");
        StringAssert.Contains(searchPackageDestination, ".LoggingAndPropagate(");
        Assert.IsTrue(searchPackageDestination.IndexOf("e.Handled = true", StringComparison.Ordinal) < searchPackageDestination.IndexOf("await viewModel.PendingPackages", StringComparison.Ordinal));
        StringAssert.Contains(searchPackageMergeDestination, ".SearchPackagesAsync(PendingInstallDestinationSearchKind.MergeDestination, [pkg])");
        StringAssert.Contains(searchPackageMergeDestination, ".LoggingAndPropagate(");
        Assert.IsTrue(searchPackageMergeDestination.IndexOf("e.Handled = true", StringComparison.Ordinal) < searchPackageMergeDestination.IndexOf("await viewModel.PendingPackages", StringComparison.Ordinal));
        Assert.IsFalse(viewModelCode.Contains("SearchInstallDestinationForPendingPackages"));
        Assert.IsFalse(viewModelCode.Contains("SearchMergeDestinationForPendingPackages"));
        Assert.IsFalse(viewModelCode.Contains("ClearInstallDestinationForPendingPackages"));
        StringAssert.Contains(normalMenu, "PendingPackages.CanOpenInstallDestination(");
        Assert.IsFalse(normalMenu.Contains("contextMenuState.CanOpenInstallDestination"));
        StringAssert.Contains(pendingPackageOwnerCode, "internal bool CanOpenInstallDestination(");
        Assert.IsFalse(chartContextMenuStateCode.Contains("CanOpenInstallDestination"));
        StringAssert.Contains(openInstallDestination, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(openInstallDestination, ".OpenInstallDestinationForChartsAsync(targets)");
        StringAssert.Contains(openInstallDestination, ".LoggingAndPropagate(\"tableContextMenuItemOpenInstallDestinationClick\")");
        Assert.IsFalse(openInstallDestination.Contains("TryResolveInstallDestination"));
        StringAssert.Contains(openPackageInstallDestination, ".OpenInstallDestinationForPackageAsync(dataContext)");
        StringAssert.Contains(openPackageInstallDestination, ".LoggingAndPropagate(\"treeViewInstallPackageContextMenuOpenInstallDestinationClick\")");
        Assert.IsFalse(viewModelCode.Contains("TryGetInstalledDirectoryByHash"));
        Assert.IsFalse(openInstallDestination.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(openPackageSource, "viewModel.PendingPackages.OpenPackageSourceInExplorer(dataContext)");
        Assert.IsFalse(openPackageSource.Contains("LongPathFileSystem.DirectoryExists"));
        Assert.IsFalse(openPackageSource.Contains("LongPathFileSystem.FileExists"));
        Assert.IsFalse(openPackageSource.Contains("ExplorerOpenService.OpenDirectory"));
        Assert.IsFalse(openPackageSource.Contains("ExplorerOpenService.OpenFileAndSelect"));
        StringAssert.Contains(pendingPackageOwnerCode, "IExternalShellGateway externalShellGateway");
        Assert.IsFalse(pendingPackageOwnerCode.Contains("ExplorerOpenService.OpenDirectory"));
        Assert.IsFalse(pendingPackageOwnerCode.Contains("ExplorerOpenService.OpenFileAndSelect"));
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        Assert.AreEqual(2, Regex.Matches(xaml, "Click=\"treeViewInstallPackageContextMenuOpenExplorerClick\"").Count);
    }

    [TestMethod]
    public async Task PendingPackageMutationTerminalApply_AggregatesOwnerBeforePrepareFailure()
    {
        var ownerFailure = new InvalidOperationException("mutation failed");
        var prepareFailure = new InvalidOperationException("view preparation failed");
        var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));

        AggregateException exception = await Assert.ThrowsExceptionAsync<AggregateException>(
            () => InvokePendingPackageMutationViewAsync(
                window,
                null,
                PendingPackageMutationResult.FailedAfterMutation(
                    ownerFailure,
                    PackageCatalogSection.Pending),
                null,
                () => throw prepareFailure));

        Assert.AreSame(ownerFailure, exception.InnerExceptions[0]);
        Assert.AreSame(prepareFailure, exception.InnerExceptions[1]);
    }

    [TestMethod]
    public void StartupUpdateFailurePresentationDefersReceiptWhenShellIsClosing()
    {
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => { });

        bool shouldDefer = MainWindow.ShouldDeferStartupUpdateFailurePresentation(
            shellClosing: true,
            updateShutdownPreparationFailed: false,
            receiptException);

        Assert.IsTrue(shouldDefer);
        receiptException.DeferAcknowledge();
        Assert.IsFalse(receiptException.ShouldAcknowledgeAfterPresentation);
        Assert.IsFalse(MainWindow.ShouldDeferStartupUpdateFailurePresentation(
            shellClosing: true,
            updateShutdownPreparationFailed: false,
            new UpdaterLaunchFailureException("ready handshake failed")));
    }

    [TestMethod]
    public void PendingPackageMutationTerminalApply_NavigatesOnceOnlyForInitiallySelectedRoot()
    {
        RunOnStaDispatcherThread(() =>
        {
            var composition = new ApplicationComposition(
                uiScheduler: new WpfUiScheduler(() => Dispatcher.CurrentDispatcher), applicationLifetime: TestApplicationContext.CreateLifetime(), cultureCatalog: TestApplicationContext.CreateCultureCatalog());
            MainWindowViewModel viewModel = composition.CreateMainWindowViewModel();
            int navigationCount = 0;
            viewModel.RegularChartList.InstallNavigationPresentationRequested +=
                (_, _) => navigationCount++;
            var window = (MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));

            var selectedRoot = new TreeViewItem { IsSelected = true };
            InvokePendingPackageMutationViewAsync(
                window,
                viewModel,
                PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending),
                selectedRoot)
                .GetAwaiter()
                .GetResult();
            Assert.AreEqual(1, navigationCount);

            var unselectedRoot = new TreeViewItem();
            InvokePendingPackageMutationViewAsync(
                window,
                viewModel,
                PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending),
                unselectedRoot)
                .GetAwaiter()
                .GetResult();
            Assert.AreEqual(1, navigationCount);
        });
    }

    private static Task InvokePendingPackageMutationViewAsync(
        MainWindow window,
        MainWindowViewModel? viewModel,
        PendingPackageMutationResult result,
        TreeViewItem? sectionRoot,
        Action? prepareView = null)
    {
        MethodInfo method = typeof(MainWindow).GetMethod(
            "ApplyPendingPackageMutationViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Pending package terminal-apply helper was not found.");
        return (Task)method.Invoke(
            window,
            [
                viewModel,
                result,
                null,
                sectionRoot,
                PackageCatalogSection.Pending,
                MainViewUpdateMode.PendingInstallFolderSelected,
                "PendingPackageMutationTerminalApplyTests",
                prepareView
            ]);
    }

    private static void RunOnStaDispatcherThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeShutdown();
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    [TestMethod]
    public void PendingPackageBulkMaintenanceHandlersDelegateCompleteRoutesToOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string ownerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PendingPackageWorkflowOwner.cs");
        string deleteSources = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPendingContextMenuDeleteInstalledOnlyPackagesClick",
            "private async void treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick");
        string renameZeroNote = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPendingContextMenuRenameZeroNoteToInvalidExtClick",
            "private async void treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick");
        string overwriteResources = ExtractBetween(
            mainWindowCode,
            "private async void treeViewInstallPendingContextMenuOverwriteInstalledOnlyPackagesResourcesClick",
            "private void treeViewInstallPackageContextMenuOpenExplorerClick");

        StringAssert.Contains(deleteSources, ".DeleteInstalledOnlyPendingPackageSourcesAsync()");
        StringAssert.Contains(renameZeroNote, ".RenamePendingZeroNoteChartsAsync()");
        StringAssert.Contains(overwriteResources, ".OverwriteInstalledOnlyPendingPackageResourcesAsync()");
        foreach (string handler in new[] { deleteSources, renameZeroNote, overwriteResources })
        {
            StringAssert.Contains(handler, "viewModel.PendingPackages");
            StringAssert.Contains(handler, ".LoggingAndPropagate(");
            Assert.IsTrue(
                handler.IndexOf("e.Handled = true", StringComparison.Ordinal)
                < handler.IndexOf("await viewModel.PendingPackages", StringComparison.Ordinal));
            Assert.IsFalse(handler.Contains("UiDialogRoute"));
            Assert.IsFalse(handler.Contains("Task.Run"));
            Assert.IsFalse(handler.Contains("RunProgressUntilTaskCompletesAsync"));
        }

        Assert.IsFalse(viewModelCode.Contains("GetPendingPackagesContainingOnlyInstalledCharts("));
        Assert.IsFalse(viewModelCode.Contains("GetPendingBmsFormatChartFilesSnapshot("));
        Assert.IsFalse(viewModelCode.Contains("DeletePendingPackageSources("));
        Assert.IsFalse(viewModelCode.Contains("RenamePendingZeroNoteBmsFormatChartsToInvalidExtensions("));
        Assert.IsFalse(viewModelCode.Contains("OverwritePendingInstalledOnlyPackagesResources("));
        StringAssert.Contains(ownerCode, "dialogs.RunWithProgressAsync(");
        StringAssert.Contains(ownerCode, "store.DeletePendingPackageSources(");
        StringAssert.Contains(ownerCode, "store.RenamePendingZeroNoteCharts(");
        StringAssert.Contains(ownerCode, "store.OverwriteInstalledOnlyPendingPackageResources(");
        StringAssert.Contains(viewModelCode, "PendingPackages.WorkflowChanged += PendingPackageWorkflowChanged;");
        Assert.IsFalse(ownerCode.Contains("IPendingPackageMutationPresentation"));
        Assert.IsFalse(ownerCode.Contains("pendingPackagePresentation"));
        Assert.IsFalse(ownerCode.Contains("presentation."));
        Assert.IsFalse(viewModelCode.Contains("IPendingPackageMutationPresentation"));
        Assert.IsFalse(viewModelCode.Contains("pendingPackagePresentation"));
    }

    [TestMethod]
    public void PlaylistDropReferenceRefreshUsesChartTargets()
    {
        string viewModelCode = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        string addChartRows = ExtractBetween(
            viewModelCode,
            "private void AddRowsToFolder",
            "private void DeleteEntries");
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string addReferenceCharts = ExtractBetween(
            libraryCode,
            "internal void AddReferenceBMSTablesToCharts",
            "internal void RefreshReferenceDisplayForTable");
        string referenceOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "Models",
            "BmsLibraryInternal",
            "BmsLibraryPlaylistReferenceOwner.cs");

        StringAssert.Contains(addChartRows, "List<ChartFile> resolvedCharts");
        StringAssert.Contains(addChartRows, "library.AddReferenceBMSTablesToCharts(table, resolvedCharts)");
        Assert.IsTrue(addChartRows.IndexOf("library.AddReferenceBMSTablesToCharts(table, resolvedCharts)", StringComparison.Ordinal) < addChartRows.IndexOf("PublishEntriesChanged(table)", StringComparison.Ordinal));
        Assert.IsFalse(addChartRows.Contains("resolvedBmsFiles"));
        StringAssert.Contains(addReferenceCharts, "playlistReferenceOwner.AddReferenceBMSTablesToCharts(SnapshotPlaylistReferenceTable(table))");
        StringAssert.Contains(referenceOwnerCode, "internal void AddReferenceBMSTablesToCharts(PlaylistReferenceTableSnapshot tableSnapshot)");
        StringAssert.Contains(referenceOwnerCode, "ReplaceTable(tableSnapshot)");
        Assert.IsFalse(libraryCode.Contains("IPlaylistReferenceApplyHost"));
        Assert.IsFalse(libraryCode.Contains("PlaylistReferenceApplyCoordinator"));
        Assert.IsFalse(addReferenceCharts.Contains("AddReferenceBMSTableToCharts(table, charts)"));
        Assert.IsFalse(addReferenceCharts.Contains("IEnumerable<BMSFile>"));
    }

    [TestMethod]
    public void PendingInstallDestinationCellEditUsesChartTargets()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string editBeginning = ExtractBetween(
            mainWindowCode,
            "private void customTableView_CellEditBeginning",
            "private void customTableView_CellEditStarted");
        string editEnded = ExtractBetween(
            mainWindowCode,
            "private void customTableView_CellEditEnded",
            "private void RefreshCustomTableViewDisplayAsync");
        string regularOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(editBeginning, "viewModel.MainChartList.TryBeginCellEdit(");
        StringAssert.Contains(editBeginning, "e.Row, e.EditPropertyName");
        Assert.IsFalse(editBeginning.Contains("GetCompatibilityBmsFile"));
        StringAssert.Contains(editEnded, "viewModel.MainChartList.RequestCellEditEnded(");
        StringAssert.Contains(regularOwnerCode, "target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination)");
        StringAssert.Contains(regularOwnerCode, "PendingInstallDestinationEditRequest.TryCreate(installTarget, out PendingInstallDestinationEditRequest installRequest)");
        StringAssert.Contains(regularOwnerCode, ".SetPendingAsync(installRequest, request.Text)");
        Assert.IsFalse(viewModelCode.Contains("RegularChartListOwnerInstallDestinationEditRequested"));
        Assert.IsFalse(editEnded.Contains("PendingInstallDestinationEditTargetSnapshot"));
        Assert.IsFalse(editEnded.Contains("GetCompatibilityBmsFile"));
        Assert.IsFalse(viewModelCode.Contains("CurrentMainViewOperationSection"));
        Assert.IsFalse(viewModelCode.Contains("CurrentMainViewChartOperationSourceScope"));
        StringAssert.Contains(mainWindowCode, "MainChartList.CurrentOperationContext");
    }

    [TestMethod]
    public void MainTableCellEditStarted_IsRaisedOnlyAfterEditorIsInstalled()
    {
        string customTableSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Views", "CustomTableView.cs");
        string beginEdit = ExtractBetween(
            customTableSource,
            "private bool BeginCellEdit(CustomTableHitTestResult hit, string replacementText)",
            "private TextBox CreateCellEditor");

        int beginningIndex = beginEdit.IndexOf("CellEditBeginning?.Invoke", StringComparison.Ordinal);
        int visibleRectIndex = beginEdit.IndexOf("TryCreateVisibleCellInteriorRect", StringComparison.Ordinal);
        int activeEditorIndex = beginEdit.IndexOf("activeEditor = textBox", StringComparison.Ordinal);
        int startedIndex = beginEdit.IndexOf("CellEditStarted?.Invoke", StringComparison.Ordinal);

        Assert.IsTrue(beginningIndex >= 0 && beginningIndex < visibleRectIndex);
        Assert.IsTrue(activeEditorIndex >= 0 && activeEditorIndex < startedIndex);
    }

    [TestMethod]
    public void RepairInstalledLocationHandlersUseChartTargets()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string searchRepair = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuSearchCorrectInstallationDirectoryChartsClick",
            "private async void tableContextMenuFixInstallationDirectoryClick");
        string fixRepair = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuFixInstallationDirectoryClick",
            "private async void tableContextMenuItemDeleteEntryClick");

        StringAssert.Contains(searchRepair, "GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation)");
        StringAssert.Contains(searchRepair, "RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request)");
        StringAssert.Contains(searchRepair, "base.DataContext is not MainWindowViewModel viewModel");
        StringAssert.Contains(searchRepair, "viewModel.PendingPackages");
        StringAssert.Contains(searchRepair, ".SearchCorrectAsync(request)");
        StringAssert.Contains(searchRepair, ".LoggingAndPropagate(");
        Assert.IsTrue(
            searchRepair.IndexOf("e.Handled = true", StringComparison.Ordinal) < searchRepair.IndexOf("await viewModel.PendingPackages", StringComparison.Ordinal),
            "The routed event must be handled before the asynchronous workflow yields.");
        Assert.IsFalse(searchRepair.Contains("IRepairInstalledLocationTargetSnapshot"));
        Assert.IsFalse(searchRepair.Contains("GetSelectedChartCompatibilityAdapters"));
        StringAssert.Contains(fixRepair, "GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation)");
        StringAssert.Contains(fixRepair, "RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request)");
        StringAssert.Contains(fixRepair, "base.DataContext is not MainWindowViewModel viewModel");
        Assert.IsFalse(fixRepair.Contains("repairTargets.MaterializeRepairEntries()"));
        StringAssert.Contains(fixRepair, "viewModel.PendingPackages");
        StringAssert.Contains(fixRepair, ".FixInstalledLocationsAsync(request)");
        StringAssert.Contains(fixRepair, ".LoggingAndPropagate(");
        Assert.IsTrue(
            fixRepair.IndexOf("e.Handled = true", StringComparison.Ordinal) < fixRepair.IndexOf("await viewModel.PendingPackages", StringComparison.Ordinal),
            "The routed event must be handled before the asynchronous workflow yields.");
        Assert.IsFalse(fixRepair.Contains("IRepairInstalledLocationTargetSnapshot"));
        Assert.IsFalse(fixRepair.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(fixRepair.Contains("target.Chart?.InstallDestination"));
    }

    [TestMethod]
    public void PlaylistEntryRemovalRoutesThroughWorkspaceOwner()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string handler = SourceTextTestHelper.ExtractMethodBody(
            mainWindowCode,
            "private async void tableContextMenuItemDeleteEntryClick(");

        StringAssert.Contains(handler, "DeleteSelectedEntriesAsync(GetSelectedGridRowsSnapshot())");
        StringAssert.Contains(handler, ".Logging(\"tableContextMenuItemDeleteEntryClick\")");
        Assert.AreEqual(-1, handler.IndexOf("GetSelectedGridPlaylistEntries", StringComparison.Ordinal));
        Assert.AreEqual(-1, handler.IndexOf("GroupBy", StringComparison.Ordinal));
        Assert.AreEqual(-1, handler.IndexOf("Task.WhenAll", StringComparison.Ordinal));
        Assert.AreEqual(-1, handler.IndexOf("DeleteEntriesAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FullScanInstallDestinationClearUsesRepairRequest()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string clearInstallDestination = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuRemoveInstallDestinationClick",
            "private async void tableContextMenuSearchCorrectInstallationDirectoryChartsClick");

        StringAssert.Contains(clearInstallDestination, "GetSelectedChartTargets(capability)");
        StringAssert.Contains(clearInstallDestination, "RepairInstalledLocationRequest.TryCreate(targets, out repairRequest)");
        StringAssert.Contains(clearInstallDestination, "PendingInstallDestinationClearRequest.TryCreate(pendingTargets, out pendingInstallRequest)");
        StringAssert.Contains(clearInstallDestination, "viewModel.PendingPackages");
        StringAssert.Contains(clearInstallDestination, ".ClearPendingAsync(pendingInstallRequest)");
        StringAssert.Contains(clearInstallDestination, ".ClearCorrectAsync(repairRequest)");
        Assert.AreEqual(2, CountOccurrences(clearInstallDestination, ".LoggingAndPropagate("));
        Assert.IsFalse(clearInstallDestination.Contains("PendingInstallDestinationTargetSnapshot"));
        Assert.IsFalse(clearInstallDestination.Contains("IRepairInstalledLocationTargetSnapshot"));
        Assert.IsFalse(clearInstallDestination.Contains("viewModel.ClearInstallDestinationForCharts(repairRequest.ChartFiles)"));
        Assert.IsFalse(clearInstallDestination.Contains("repairRequest.ChartFiles"));
        Assert.IsFalse(clearInstallDestination.Contains("GetSelectedChartCompatibilityAdapters"));
    }

    [TestMethod]
    public void StartupInitialize_ReleasesSemaphoreWhenFileInitializationFails()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string fileInitializeBlock = ExtractBetween(
            viewModelCode,
            "startupReadyDataReached = false;",
            "if (applicationLifetime.IsFirstStartup)");

        StringAssert.Contains(fileInitializeBlock, "files.InitializeStartup");
        StringAssert.Contains(fileInitializeBlock, "FailStartupProgressOperation(ex.Message);");
        StringAssert.Contains(fileInitializeBlock, "_semaphore.Release();");
        StringAssert.Contains(fileInitializeBlock, "startupProgressWorkflowOwner.SetStartupUiInteractionBlocked(false);");
        StringAssert.Contains(fileInitializeBlock, "SettingDialog?.RequestOpen()");
        Assert.IsFalse(fileInitializeBlock.Contains("throw;"));
    }

    [TestMethod]
    public void PostStartupWarmup_UsesOneOwnedLifecycleAndRunsAdjacentIndexesBeforeVirtualSortPrewarm()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string scoreOnly = ExtractBetween(
            viewModelCode,
            "internal async Task ReloadScoresOnlyAsync()",
            "internal async Task ReloadFileDiffAsync()");
        string fullReinitialize = ExtractBetween(
            viewModelCode,
            "internal async Task ReinitializeLibraryAsync()",
            "internal static string BuildAppSchemaRepairWarningMessage");
        string reloadTables = ExtractBetween(
            viewModelCode,
            "internal async Task ReloadTablesAsync()",
            "internal async Task ReloadScoresOnlyAsync()");
        string reloadFileDiff = ExtractBetween(
            viewModelCode,
            "internal async Task ReloadFileDiffAsync()",
            "internal async Task ReinitializeLibraryAsync()");
        string scheduler = ExtractBetween(
            viewModelCode,
            "private void SchedulePostStartupBestEffortWarmups",
            "private void RunPostStartupOwnedAdjacentIndexWarmup");
        string startupCompletion = ExtractBetween(
            viewModelCode,
            "private void TryLogStartupInitializationComplete",
            "private void TryLogStartupPostInitializationComplete");
        string completion = ExtractBetween(
            viewModelCode,
            "private void CompleteStartupPostInitializationWarmup",
            "private bool QueueDeferredStartupPresentationFlushAfterInitialization");
        string ownedWarmup = ExtractBetween(
            viewModelCode,
            "private void RunPostStartupOwnedAdjacentIndexWarmup",
            "private void ScheduleVirtualNormalLibraryOrderPrewarm");
        string lifecycleScheduler = ExtractBetween(
            viewModelCode,
            "private void ScheduleVirtualNormalLibraryOrderPrewarm",
            "private static int CountPrewarmDescriptorsByPriority");
        string regularOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "RegularChartListOwner.cs");
        string virtualWarmup = ExtractBetween(
            regularOwnerCode,
            "internal void RunVirtualOrderPrewarm",
            "private static IReadOnlyList<VirtualNormalLibrarySortDescriptor> CreateVirtualOrderPrewarmDescriptors");

        StringAssert.Contains(scheduler, "ScheduleVirtualNormalLibraryOrderPrewarm(reason, warmupCompleted)");
        StringAssert.Contains(scheduler, "startupPostInitializationWarmupsPending = 1");
        StringAssert.Contains(scheduler, "startupPostInitializationVirtualWarmupScheduled");
        StringAssert.Contains(scheduler, "CompleteStartupPostInitializationWarmup");
        StringAssert.Contains(scheduler, "startupBackgroundTaskScheduler.CurrentGeneration");
        StringAssert.Contains(scheduler, "IsCurrentStartupPostInitializationCallback(");
        StringAssert.Contains(scheduler, "MarkRequiredInitializationSchedulingComplete(schedulerGeneration)");
        StringAssert.Contains(completion, "IsCurrentStartupPostInitializationCallback(");
        StringAssert.Contains(scoreOnly, "MarkNonStartupBackgroundSchedulingComplete();");
        StringAssert.Contains(fullReinitialize, "MarkNonStartupBackgroundSchedulingComplete();");
        StringAssert.Contains(reloadTables, "MarkNonStartupBackgroundSchedulingComplete();");
        StringAssert.Contains(reloadFileDiff, "MarkNonStartupBackgroundSchedulingComplete();");
        Assert.IsTrue(
            fullReinitialize.IndexOf("PlaylistWorkspace.PlaylistReferenceApplyWorkflow.Queue(\"FullReinitialize\", operationToken);", StringComparison.Ordinal)
                < fullReinitialize.IndexOf("MarkNonStartupBackgroundSchedulingComplete();", StringComparison.Ordinal));
        int startupWarmupSchedule = viewModelCode.IndexOf(
            "SchedulePostStartupBestEffortWarmups(\"startup_initialization_ready\", operationToken);",
            StringComparison.Ordinal);
        int startupPhaseCompletion = viewModelCode.IndexOf(
            "startupProgressWorkflowOwner.SkipUnrequestedStartupProgressPhases(",
            startupWarmupSchedule,
            StringComparison.Ordinal);
        Assert.IsTrue(startupWarmupSchedule >= 0 && startupWarmupSchedule < startupPhaseCompletion);
        StringAssert.Contains(startupCompletion, "SchedulePostStartupBestEffortWarmups(\"startup_initialization_complete\", expectedOperationToken);");
        Assert.IsTrue(
            startupCompletion.IndexOf("LogUiSuppression(\"startup_initialization_complete elapsedMs=\" + elapsedMs);", StringComparison.Ordinal)
                < startupCompletion.IndexOf("SchedulePostStartupBestEffortWarmups(\"startup_initialization_complete\", expectedOperationToken);", StringComparison.Ordinal));
        StringAssert.Contains(viewModelCode, "startupPostInitializationLr2Enrolled");
        StringAssert.Contains(viewModelCode, "TryCompleteStartupBackgroundTasksPhaseIfIdle(operationToken);");
        StringAssert.Contains(viewModelCode, "ShellShutdownWorkflow?.IsShutdownRequested == true");
        Assert.IsFalse(ownedWarmup.Contains("Wait()"));
        StringAssert.Contains(ownedWarmup, "cancellationToken.ThrowIfCancellationRequested()");

        int realPathWarmup = ownedWarmup.IndexOf("WarmOwnedRealPathDirectoryView", StringComparison.Ordinal);
        int installDestinationOverlayWarmup = ownedWarmup.IndexOf("WarmInstallDestinationOverlaySnapshot", StringComparison.Ordinal);
        int primaryHashWarmup = ownedWarmup.IndexOf("WarmInstalledPrimaryHashLookup", StringComparison.Ordinal);
        int playlistSummaryWarmup = ownedWarmup.IndexOf("WarmOwnedChartHashIndexSnapshot", StringComparison.Ordinal);
        Assert.IsTrue(realPathWarmup >= 0);
        Assert.IsTrue(installDestinationOverlayWarmup > realPathWarmup);
        Assert.IsTrue(primaryHashWarmup > installDestinationOverlayWarmup);
        Assert.IsTrue(playlistSummaryWarmup > primaryHashWarmup);
        StringAssert.Contains(lifecycleScheduler, "regularChartListOwner.TryBeginVirtualOrderPrewarm");
        StringAssert.Contains(lifecycleScheduler, "startupBackgroundTaskScheduler.Queue(");
        StringAssert.Contains(lifecycleScheduler, "playlist_virtual_order_prewarm");
        Assert.IsFalse(lifecycleScheduler.Contains("Task.Run(", StringComparison.Ordinal));
        StringAssert.Contains(lifecycleScheduler, "using (lease)");
        int ownedRun = lifecycleScheduler.IndexOf("RunPostStartupOwnedAdjacentIndexWarmup", StringComparison.Ordinal);
        int virtualRun = lifecycleScheduler.IndexOf("RunVirtualOrderPrewarm", StringComparison.Ordinal);
        Assert.IsTrue(ownedRun >= 0);
        Assert.IsTrue(virtualRun > ownedRun);
        Assert.IsFalse(virtualWarmup.Contains(".Wait("));
    }

    [TestMethod]
    public void ReloadScoresOnly_DoesNotSchedulePlaylistReloadWork()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string method = ExtractBetween(
            viewModelCode,
            "internal async Task ReloadScoresOnlyAsync()",
            "internal async Task ReloadFileDiffAsync()");

        StringAssert.Contains(method, "files.InitializeScoresOnly(null)");
        StringAssert.Contains(method, ".LoggingAndPropagate(\"ReloadScoresOnly\")");
        Assert.IsFalse(viewModelCode.Contains("public async void ReloadScoresOnly()"));
        Assert.IsFalse(method.Contains("ReloadTables("));
        Assert.IsFalse(method.Contains("tables.Initialize("));
        Assert.IsFalse(method.Contains("StartDeferredExternalPlaylistSync("));
        Assert.IsFalse(method.Contains("ScheduleDeferredPlaylistReferenceApply("));
        Assert.IsFalse(method.Contains("playlist_entries_hydration"));
    }

    [TestMethod]
    public void ReloadTables_DoesNotInitializeScoresOnly()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string method = ExtractBetween(
            viewModelCode,
            "internal async Task ReloadTablesAsync()",
            "internal async Task ReloadScoresOnlyAsync()");
        string handler = ExtractBetween(
            mainWindowCode,
            "private async void treeViewPlaylistRootContextMenuItemReloadClick",
            "private async void treeViewPlaylistRootContextMenuItemCreateNewPlaylistClick");

        StringAssert.Contains(xaml, "Click=\"treeViewPlaylistRootContextMenuItemReloadClick\"");
        Assert.IsFalse(xaml.Contains("LivetCallMethodAction MethodName=\"ReloadTables\""));
        StringAssert.Contains(handler, "await viewModel.ReloadTablesAsync().LoggingAndPropagate(\"treeViewPlaylistRootContextMenuItemReloadClick\")");
        StringAssert.Contains(method, "tables.ReloadTables(queueBeatorajaBmtExportAfterHydration: false)");
        StringAssert.Contains(method, "PlaylistWorkspace.QueueExternalPlaylistSync(");
        StringAssert.Contains(method, ".LoggingAndPropagate(\"ReloadTables\")");
        Assert.IsTrue(
            method.IndexOf("PlaylistWorkspace.QueueExternalPlaylistSync(", StringComparison.Ordinal)
                < method.IndexOf("MarkNonStartupBackgroundSchedulingComplete();", StringComparison.Ordinal));
        Assert.IsFalse(viewModelCode.Contains("public async void ReloadTables()"));
        Assert.IsFalse(method.Contains("files.InitializeScoresOnly"));
        Assert.IsFalse(method.Contains("QueueDeferredScoreHydration"));
        Assert.IsFalse(method.Contains("QueueDeferredRankingRefresh"));
    }

    [TestMethod]
    public void ManualPlaylistResync_UsesBatchReloadWithoutFailureDialogs()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string workspaceCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.Reload.cs");
        string propertyEditingCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.PropertyEditing.cs");
        string propertySaveEventsCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.PlaylistPropertySaveEvents.cs");

        StringAssert.Contains(workspaceCode, "playlists.ExternalSyncOwner.ReloadPlaylistTargetsAsync(");
        StringAssert.Contains(workspaceCode, "publishReferenceReceipts: true");
        StringAssert.Contains(workspaceCode, "requireCurrentTargetForApply: true");
        StringAssert.Contains(workspaceCode, "playlists.BmtOutput.QueueBeatorajaBmtExportAll(\"manual_resync\")");
        StringAssert.Contains(workspaceCode, "LogPlaylistSyncFailure(result)");
        StringAssert.Contains(workspaceCode, "playlist_manual_resync_failed table=");
        StringAssert.Contains(propertyEditingCode, "LogPlaylistPropertyExternalSyncFailure(request)");
        StringAssert.Contains(propertyEditingCode, "playlist_property_resync_failed table=");
        Assert.IsFalse(viewModelCode.Contains("PlaylistWorkspacePlaylistSyncResultReported"));
        Assert.IsFalse(propertySaveEventsCode.Contains("playlist_property_resync_failed table="));
        Assert.IsFalse(viewModelCode.Contains("PlaylistWorkspacePlaylistReferenceTableReplaced"));
        StringAssert.Contains(viewModelCode, "publishReferenceReceipt: true");
        Assert.AreEqual(-1, viewModelCode.IndexOf("files.ReplaceReferenceBMSTable(", StringComparison.Ordinal));
        Assert.IsFalse(viewModelCode.Contains("PlaylistWorkspace.RemapCurrentPlaylistDetailFolderSelection("));
        Assert.AreEqual(-1, viewModelCode.IndexOf("ReplaceCurrentPlaylistSelectionTable(", StringComparison.Ordinal));
        Assert.AreEqual(-1, viewModelCode.IndexOf("RemapCurrentPlaylistFolderSelection(", StringComparison.Ordinal));
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.RequestPlaylistDetailReloadRefresh();");
        StringAssert.Contains(viewModelCode, "InvokeMainChartListPresentationAction(");
        StringAssert.Contains(viewModelCode, "RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);");
        string playlistReferencePresentationHandler = ExtractBetween(
            viewModelCode,
            "private void PlaylistReferenceApplyWorkflowPresentationRequested(",
            "private void FinalizeMainViewBuild(");
        int suppressionIndex = playlistReferencePresentationHandler.IndexOf(
            "TrySuppress(UiRefreshChannel.LibraryMainView | UiRefreshChannel.PlaylistTree)",
            StringComparison.Ordinal);
        int startupDeferIndex = playlistReferencePresentationHandler.IndexOf(
            "TryDeferStartupPresentationRefresh(",
            StringComparison.Ordinal);
        Assert.IsTrue(
            suppressionIndex >= 0 && startupDeferIndex > suppressionIndex,
            "Playlist reference presentation must honor ordinary UI suppression before startup deferral.");
        Assert.IsFalse(viewModelCode.Contains("public async Task ResyncPlaylistsAsync(IEnumerable<BMSTable> tablesToResync)"));
        Assert.IsFalse(workspaceCode.Contains("ResetBMSTableAsync("));
        Assert.IsFalse(workspaceCode.Contains("ShowPlaylistLoadFailure("));
    }

    [TestMethod]
    public void BeatorajaBmtFullExport_DoesNotShowNoOpProgress()
    {
        string playlistCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "PlaylistBmtOutputOwner.cs"));
        string method = ExtractBetween(
            playlistCode,
            "internal void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath = null)",
            "internal void QueueBeatorajaBmtExportForTable(BMSTable table, string reason)");

        StringAssert.Contains(method, "bool shouldReportProgress = projectionTablesSnapshot.Count > 0;");
        StringAssert.Contains(method, "if (shouldReportProgress)");
        StringAssert.Contains(method, "ReportProgress(progressOperationId, true, projectionTablesSnapshot.Count, 0, string.Empty);");
        StringAssert.Contains(method, "shouldReportProgress");
        StringAssert.Contains(method, ": null);");
    }

    [TestMethod]
    public void PlaylistReferenceReplace_InvalidatesIndexBackedBmsonDisplay()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string propertyDialogSave = ExtractBetween(
            File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistPropertySaveService.cs")),
            "internal async Task ApplyPostSaveUpdatesAsync(PlaylistPropertySaveCommit commit)",
            "private static bool IsValid(");
        string replaceReceipt = ExtractBetween(
            File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.Reload.cs")),
            "private void ApplyReferenceReplaceReceipt(",
            "internal sealed class PlaylistSyncProgressChangedEventArgs");

        AssertReplaceInvalidatesReferenceSortKey(propertyDialogSave);
        StringAssert.Contains(replaceReceipt, "ReferenceEntriesChanged");
        AssertReplaceInvalidatesReferenceSortKey(replaceReceipt);
    }

    private static void AssertReplaceInvalidatesReferenceSortKey(string source)
    {
        int replaceIndex = source.IndexOf("files.ReplaceReferenceBMSTable(", StringComparison.Ordinal);
        if (replaceIndex < 0)
        {
            replaceIndex = source.IndexOf("ownerViewModel.files.ReplaceReferenceBMSTable(", StringComparison.Ordinal);
        }
        if (replaceIndex < 0)
        {
            replaceIndex = source.IndexOf("GetLibrary().ReplaceReferenceBMSTable(", StringComparison.Ordinal);
        }
        if (replaceIndex < 0)
        {
            replaceIndex = source.IndexOf("library.ReplaceReferenceBMSTable(", StringComparison.Ordinal);
        }
        int invalidateIndex = source.IndexOf("InvalidateNormalLibraryReferenceTableSortKeys()", StringComparison.Ordinal);
        if (invalidateIndex < 0)
        {
            invalidateIndex = source.IndexOf("PlaylistPropertyReferenceSortInvalidationRequested", StringComparison.Ordinal);
        }
        if (invalidateIndex < 0)
        {
            invalidateIndex = source.IndexOf("RequestPlaylistReferenceSortInvalidation()", StringComparison.Ordinal);
        }

        Assert.IsTrue(replaceIndex >= 0);
        Assert.IsTrue(invalidateIndex > replaceIndex);
    }

    [TestMethod]
    public void SidebarTreeViewWidthPolicy_NormalizesInvalidPersistedValues()
    {
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.NaN));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.PositiveInfinity));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.NegativeInfinity));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(-1d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(0d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(Settings.MinTreeViewWidth - 1d));
        Assert.AreEqual(Settings.MinTreeViewWidth, Settings.NormalizeTreeViewWidth(Settings.MinTreeViewWidth));
        Assert.AreEqual(250d, Settings.NormalizeTreeViewWidth(250d));
        Assert.AreEqual(320d, Settings.NormalizeTreeViewWidth(320d));
    }

    [TestMethod]
    public void SidebarTreeViewWidthSetting_PropertyNormalizesBackingValue()
    {
        var settings = new Settings();

        settings["TreeViewWidth"] = 0d;
        Assert.AreEqual(Settings.DefaultTreeViewWidth, settings.TreeViewWidth);

        settings.TreeViewWidth = 0d;
        Assert.AreEqual(Settings.DefaultTreeViewWidth, (double)settings["TreeViewWidth"]);

        settings.TreeViewWidth = Settings.MinTreeViewWidth;
        Assert.AreEqual(Settings.MinTreeViewWidth, (double)settings["TreeViewWidth"]);
    }

    [TestMethod]
    public void AppearanceThemeSetting_NormalizesValuesAndResourcesExist()
    {
        var settings = new Settings();

        settings["AppearanceTheme"] = "Dark";
        Assert.AreEqual(AppThemeService.Dark, settings.AppearanceTheme);

        settings["AppearanceTheme"] = "Unknown";
        Assert.AreEqual(AppThemeService.Light, settings.AppearanceTheme);

        settings.AppearanceTheme = "dark";
        Assert.AreEqual(AppThemeService.Dark, (string)settings["AppearanceTheme"]);

        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_theme));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_theme_light));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_theme_dark));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_table));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_table_font_size));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_table_row_height));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_table_header_height));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Appearance_table_reset_defaults));
    }

    [TestMethod]
    public void CustomTableAppearanceSettings_NormalizeValues()
    {
        var settings = new Settings();

        settings["CustomTableFontSize"] = double.NaN;
        settings["CustomTableRowHeight"] = double.PositiveInfinity;
        settings["CustomTableHeaderHeight"] = double.NegativeInfinity;

        Assert.AreEqual(Settings.DefaultCustomTableFontSize, settings.CustomTableFontSize);
        Assert.AreEqual(Settings.DefaultCustomTableRowHeight, settings.CustomTableRowHeight);
        Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, settings.CustomTableHeaderHeight);

        settings.CustomTableFontSize = Settings.MinCustomTableFontSize - 1d;
        settings.CustomTableRowHeight = Settings.MaxCustomTableRowHeight + 1d;
        settings.CustomTableHeaderHeight = Settings.MinCustomTableHeaderHeight - 1d;

        Assert.AreEqual(Settings.MinCustomTableFontSize, (double)settings["CustomTableFontSize"]);
        Assert.AreEqual(Settings.MaxCustomTableRowHeight, (double)settings["CustomTableRowHeight"]);
        Assert.AreEqual(Settings.MinCustomTableHeaderHeight, (double)settings["CustomTableHeaderHeight"]);
    }

    [TestMethod]
    public void ThemeResourceDictionaries_DefineRequiredTableAndAppKeys()
    {
        string root = FindRepositoryRoot();
        string light = File.ReadAllText(Path.Combine(root, "Themes", "Light.xaml"));
        string dark = File.ReadAllText(Path.Combine(root, "Themes", "Dark.xaml"));

        foreach (string key in new[]
        {
            "App.BackgroundBrush",
            "App.SurfaceBrush",
            "App.TextBrush",
            "App.BorderBrush",
            "App.DialogBorderBrush",
            "App.ControlPressedBrush",
            "App.ControlBackgroundActiveBrush",
            "App.InputFocusBorderBrush",
            "App.MenuSelectedBackgroundBrush",
            "App.MenuSelectedTextBrush",
            "ScrollBar.TrackBackgroundBrush",
            "ScrollBar.ThumbBackgroundBrush",
            "ScrollBar.ThumbHoverBackgroundBrush",
            "ScrollBar.ThumbDraggingBackgroundBrush",
            "ScrollBar.ThumbBorderBrush",
            "ScrollBar.ButtonBackgroundBrush",
            "ScrollBar.ButtonHoverBackgroundBrush",
            "ScrollBar.ButtonPressedBackgroundBrush",
            "ScrollBar.ButtonBorderBrush",
            "ScrollBar.ArrowBrush",
            "Table.RowBackgroundBrush",
            "Table.AlternatingRowBackgroundBrush",
            "Table.SelectedRowBackgroundBrush",
            "Table.CurrentCellTextBrush",
            "Table.TextBrush",
            "Table.SelectedTextBrush",
            "Table.CheckBoxBackgroundBrush"
        })
        {
            StringAssert.Contains(light, "x:Key=\"" + key + "\"");
            StringAssert.Contains(dark, "x:Key=\"" + key + "\"");
        }
    }

    [TestMethod]
    public void SettingDialog_ExposesAppearanceThemeSelector()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));

        Assert.AreEqual(1, CountOccurrences(xaml, "<TabItem Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Appearance, Mode=OneWay}\">"));
        Assert.AreEqual(1, CountOccurrences(xaml, "ItemsSource=\"{Binding AppearanceThemeOptions}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "SelectedValue=\"{Binding AppearanceTheme, Mode=TwoWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Appearance_theme, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Appearance_table, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Value=\"{Binding CustomTableFontSize, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Value=\"{Binding CustomTableRowHeight, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Value=\"{Binding CustomTableHeaderHeight, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Click=\"resetCustomTableAppearanceDefaultsButtonClick\""));
    }

    [TestMethod]
    public void SettingDialog_UsesScopedModernLayoutStyles()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));

        StringAssert.Contains(xaml, "<Border Width=\"640\" Height=\"500\" CornerRadius=\"8\"");
        StringAssert.Contains(xaml, "BorderBrush=\"{DynamicResource App.DialogBorderBrush}\"");
        StringAssert.Contains(xaml, "<Style TargetType=\"{x:Type GroupBox}\" BasedOn=\"{StaticResource {x:Type GroupBox}}\">");
        StringAssert.Contains(xaml, "<Setter Property=\"Padding\" Value=\"10,8,10,10\" />");
        StringAssert.Contains(xaml, "TextElement.FontWeight=\"SemiBold\"");
        StringAssert.Contains(xaml, "CornerRadius=\"4\"");
        StringAssert.Contains(xaml, "<ScrollViewer Margin=\"4\" VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">");
        StringAssert.Contains(xaml, "<Style TargetType=\"{x:Type Button}\" BasedOn=\"{StaticResource {x:Type Button}}\">");
        StringAssert.Contains(xaml, "<Style TargetType=\"{x:Type TextBox}\" BasedOn=\"{StaticResource {x:Type TextBox}}\">");
        StringAssert.Contains(xaml, "<Style TargetType=\"{x:Type ComboBox}\" BasedOn=\"{StaticResource {x:Type ComboBox}}\">");
        StringAssert.Contains(xaml, "x:Key=\"styleWrappingSettingCheckBox\"");
        StringAssert.Contains(xaml, "TextWrapping=\"Wrap\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding ShowScoreViewerRegisterConfirmMsg}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding ShowDiffBMSInstallConfirmMsg}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding ShowDuplicateFileCheckConfirmMsg}\"");
        StringAssert.Contains(xaml, "Path=Resources.Details_show_diag_duplicate_file_check, Mode=OneWay");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding ShowRecommUpdatedMsg}\"");
        StringAssert.Contains(xaml, "Path=Resources.Details_initialization_settings, Mode=OneWay");
        StringAssert.Contains(xaml, "Path=Resources.Details_lr2_integration_settings, Mode=OneWay");
        StringAssert.Contains(xaml, "<GroupBox Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Install, Mode=OneWay}\">");
        Assert.AreEqual(0, CountOccurrences(xaml, "VerticalScrollBarVisibility=\"Disabled\" HorizontalScrollBarVisibility=\"Auto\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "MaxHeight=\"260\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Note, Mode=OneWay}\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Advanced_settings, Mode=OneWay}\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "GroupBox Padding=\"5\""));
        Assert.AreEqual(0, CountOccurrences(xaml, "GroupBox Padding=\"5,5,5,0\""));
    }

    [TestMethod]
    public void PlaylistPropertyDialog_UsesScopedModernLayoutStyles()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlaylistPropertyDialog.xaml"));

        StringAssert.Contains(xaml, "<Border Width=\"520\" Height=\"350\" CornerRadius=\"8\"");
        StringAssert.Contains(xaml, "BorderBrush=\"{DynamicResource App.DialogBorderBrush}\"");
        StringAssert.Contains(xaml, "<Style TargetType=\"{x:Type TabControl}\" BasedOn=\"{StaticResource {x:Type TabControl}}\">");
        StringAssert.Contains(xaml, "<ScrollViewer Margin=\"4\" VerticalScrollBarVisibility=\"Auto\" HorizontalScrollBarVisibility=\"Disabled\">");
        StringAssert.Contains(xaml, "<Style TargetType=\"{x:Type GroupBox}\" BasedOn=\"{StaticResource {x:Type GroupBox}}\">");
        StringAssert.Contains(xaml, "TextElement.FontWeight=\"SemiBold\"");
        StringAssert.Contains(xaml, "CornerRadius=\"4\"");
        StringAssert.Contains(xaml, "<Grid DockPanel.Dock=\"Bottom\" VerticalAlignment=\"Bottom\" Margin=\"0,8,0,0\">");
        StringAssert.Contains(xaml, "<Button Content=\"OK\" MinWidth=\"64\" Margin=\"0,0,8,0\" Click=\"SaveAndClose\" />");
        StringAssert.Contains(xaml, "<Button Content=\"Cancel\" MinWidth=\"64\" Click=\"CancelAndClose\" />");
        StringAssert.Contains(xaml, "ListBox Name=\"foldersListBox\" DockPanel.Dock=\"Left\" Width=\"300\" Height=\"130\"");
        Assert.AreEqual(0, CountOccurrences(xaml, "GroupBox Padding=\"5"));
        Assert.AreEqual(0, CountOccurrences(xaml, "Margin=\"5,5,5"));
    }

    [TestMethod]
    public void ThemeStyles_ApplyToMenusAndStandardControls()
    {
        string styles = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Simple Styles.xaml"));
        string mainWindow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        foreach (string targetType in new[]
        {
            "ContextMenu",
            "MenuItem",
            "Separator",
            "Button",
            "TextBox",
            "ComboBox",
            "ComboBoxItem",
            "CheckBox",
            "RadioButton",
            "Label",
            "GroupBox",
            "TabControl",
            "TabItem",
            "Expander"
        })
        {
            StringAssert.Contains(styles, "Style TargetType=\"{x:Type " + targetType + "}\"");
        }

        string simpleMenuItem = styles.Substring(styles.IndexOf("x:Key=\"SimpleMenuItem\"", StringComparison.Ordinal));
        simpleMenuItem = simpleMenuItem.Substring(0, simpleMenuItem.IndexOf("x:Key=\"SimpleSeparator\"", StringComparison.Ordinal));
        Assert.IsFalse(simpleMenuItem.Contains("SystemColors.MenuTextBrushKey"));
        Assert.IsFalse(simpleMenuItem.Contains("SystemColors.HighlightBrushKey"));
        Assert.IsFalse(simpleMenuItem.Contains("SystemColors.HighlightTextBrushKey"));
        Assert.IsFalse(simpleMenuItem.Contains("SystemColors.GrayTextBrushKey"));
        StringAssert.Contains(simpleMenuItem, "App.PopupBackgroundBrush");
        StringAssert.Contains(simpleMenuItem, "ArrowPanelPath");
        StringAssert.Contains(simpleMenuItem, "Data=\"M0,0 L4,4 L0,8 Z\"");

        string simpleSeparator = styles.Substring(styles.IndexOf("x:Key=\"SimpleSeparator\"", StringComparison.Ordinal));
        simpleSeparator = simpleSeparator.Substring(0, simpleSeparator.IndexOf("x:Key=\"SimpleTabControl\"", StringComparison.Ordinal));
        StringAssert.Contains(simpleSeparator, "OverridesDefaultStyle");
        StringAssert.Contains(simpleSeparator, "App.SeparatorBrush");
        StringAssert.Contains(simpleSeparator, "x:Static MenuItem.SeparatorStyleKey");
        Assert.IsFalse(simpleSeparator.Contains("MinWidth=\"{Binding ActualWidth"));

        foreach (string controlStyle in new[] { "SimpleButton", "SimpleCheckBox", "SimpleRadioButton", "SimpleLabel", "SimpleListBox", "SimpleListBoxItem", "SimpleExpander", "SimpleTabItem" })
        {
            string marker = "x:Key=\"" + controlStyle + "\"";
            string snippet = styles.Substring(styles.IndexOf(marker, StringComparison.Ordinal), Math.Min(900, styles.Length - styles.IndexOf(marker, StringComparison.Ordinal)));
            StringAssert.Contains(snippet, "App.TextBrush");
        }
        string simpleLabel = styles.Substring(styles.IndexOf("x:Key=\"SimpleLabel\"", StringComparison.Ordinal), 500);
        StringAssert.Contains(simpleLabel, "VerticalContentAlignment\" Value=\"Center\"");
        string simpleTextBox = styles.Substring(styles.IndexOf("x:Key=\"SimpleTextBox\"", StringComparison.Ordinal), 1200);
        StringAssert.Contains(simpleTextBox, "VerticalContentAlignment\" Value=\"Center\"");
        StringAssert.Contains(simpleTextBox, "VerticalAlignment=\"{TemplateBinding Control.VerticalContentAlignment}\"");
        StringAssert.Contains(simpleTextBox, "CaretBrush\" Value=\"{DynamicResource App.TextBrush}\"");
        string simpleComboBox = styles.Substring(styles.IndexOf("x:Key=\"SimpleComboBox\"", StringComparison.Ordinal), 1200);
        StringAssert.Contains(simpleComboBox, "VerticalContentAlignment\" Value=\"Center\"");
        StringAssert.Contains(simpleComboBox, "VerticalAlignment=\"{TemplateBinding Control.VerticalContentAlignment}\"");
        string simpleProgressBar = styles.Substring(styles.IndexOf("x:Key=\"SimpleProgressBar\"", StringComparison.Ordinal), 700);
        StringAssert.Contains(simpleProgressBar, "ProgressBar.TrackBrush");
        StringAssert.Contains(simpleProgressBar, "ProgressBar.IndicatorBrush");
        StringAssert.Contains(styles, "x:Key=\"ProgressBar.IndicatorBrush\" Color=\"#FF06B025\"");
        StringAssert.Contains(mainWindow, "App.ControlBackgroundActiveBrush");
        StringAssert.Contains(mainWindow, "ElementName=KeywordSearchBox, Mode=OneWay, Converter={StaticResource stringToBooleanConverter}");
        StringAssert.Contains(mainWindow, "ElementName=KeywordSearchBoxPlaylistSummary, Mode=OneWay, Converter={StaticResource stringToBooleanConverter}");
        StringAssert.Contains(mainWindow, "TextBox Name=\"KeywordSearchBox\" Width=\"390\"");
        StringAssert.Contains(mainWindow, "TextBox Name=\"KeywordSearchBoxPlaylistSummary\" Width=\"390\"");
        StringAssert.Contains(styles, "Data=\"M2,6 L5,9 L11,2\"");
        StringAssert.Contains(styles, "Name=\"IndeterminateMark\"");

        StringAssert.Contains(mainWindow, "x:Key=\"styleContextMenuLeftAlignment\" TargetType=\"{x:Type MenuItem}\" BasedOn=\"{StaticResource {x:Type MenuItem}}\"");
        StringAssert.Contains(mainWindow, "x:Key=\"stylePlaylistSummaryContextMenuLeftAlignment\" TargetType=\"{x:Type MenuItem}\" BasedOn=\"{StaticResource {x:Type MenuItem}}\"");
        Assert.AreEqual(4, CountOccurrences(mainWindow, "<Style TargetType=\"{x:Type MenuItem}\" BasedOn=\"{StaticResource {x:Type MenuItem}}\">"));
    }

    [TestMethod]
    public void AppDialogs_UseThemeResourcesAndCoordinatorMessageRoutes()
    {
        string root = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.IsFalse(mainWindow.Contains("<v:ThemedInformationDialogInteractionMessageAction />"));
        Assert.IsFalse(mainWindow.Contains("<v:ThemedConfirmationDialogInteractionMessageAction />"));

        foreach (string relativePath in new[]
        {
            Path.Combine("BeMusicSeeker", "Views", "SettingDialog.xaml"),
            Path.Combine("BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml"),
            Path.Combine("BeMusicSeeker", "Views", "PlaylistPropertyDialog.xaml"),
            Path.Combine("BeMusicSeeker", "Views", "LoadPlaylistURIDialog.xaml"),
            Path.Combine("BeMusicSeeker", "Views", "PendingDeleteConfirmDialog.xaml"),
            Path.Combine("Parago", "Windows", "ProgressDialog.xaml")
        })
        {
            string xaml = File.ReadAllText(Path.Combine(root, relativePath));
            StringAssert.Contains(xaml, "App.TextBrush");
            StringAssert.Contains(xaml, "App.DialogBackgroundBrush");
        }

        string settingDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        StringAssert.Contains(settingDialog, "App.WarningTextBrush");
        Assert.IsFalse(settingDialog.Contains("Foreground=\"#FFFF0000\""));

        string initialSetupDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml"));
        StringAssert.Contains(initialSetupDialog, "App.DialogOverlayBrush");
        StringAssert.Contains(initialSetupDialog, "App.DialogBorderBrush");

        Assert.AreEqual(MessageBoxResult.Cancel, ThemedMessageBox.NormalizeDefaultResult(MessageBoxButton.OKCancel, MessageBoxResult.None));
        Assert.AreEqual(MessageBoxResult.No, ThemedMessageBox.NormalizeDefaultResult(MessageBoxButton.YesNo, MessageBoxResult.None));
        Assert.AreEqual(true, ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Yes));
        Assert.AreEqual(false, ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.No));
        Assert.AreEqual(null, ThemedMessageBox.ToConfirmationResponse(MessageBoxResult.Cancel));
    }

    [TestMethod]
    public void SidebarTreeViewWidthSavePolicy_UsesMeasuredColumnWhenValid()
    {
        Assert.AreEqual(240d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(240d, 250d, 300d));
        Assert.AreEqual(260d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(double.NaN, 260d, 300d));
        Assert.AreEqual(320d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(0d, double.NaN, 320d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(0d, double.NaN, 0d));
    }

    [TestMethod]
    public void AdvancedSettings_RemovePlaylistExpandSettingAndPromoteSongDbPragmaLabel()
    {
        string root = FindRepositoryRoot();
        string viewModel = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string settings = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Settings.cs"));
        string appConfig = File.ReadAllText(Path.Combine(root, "app.config"));
        string settingDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));

        Assert.AreEqual("song.dbアクセス最適化PRAGMAを有効にする", Resources.Details_test_db_read_optimized_pragmas);
        Assert.IsFalse(viewModel.Contains("_IsPlaylistTreeExpanded"));
        Assert.AreEqual(0, CountOccurrences(viewModel + settings + appConfig + settingDialog, "StartupExpandPlaylistTree"));
        Assert.AreEqual(0, CountOccurrences(File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"))
            + File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs")), "Details_test_startup_expand_playlist_tree"));

        foreach (string languagePath in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            string json = File.ReadAllText(languagePath);
            Assert.AreEqual(0, CountOccurrences(json, "Details_test_startup_expand_playlist_tree"), languagePath);
            Assert.AreEqual(1, CountOccurrences(json, "Details_test_db_read_optimized_pragmas"), languagePath);
        }
    }

    [TestMethod]
    public void AdvancedSettings_TestPrefixLabelsArePromotedToRegularSettingLabels()
    {
        string root = FindRepositoryRoot();
        string viewModel = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string[] promotedLabels =
        [
            Resources.Details_scan_bms_files_on_startup,
            Resources.Details_test_notcheck_playlists,
            Resources.Details_test_startup_select_install_pending,
            Resources.Details_update_lr2ir_ranking_cache_on_startup,
            Resources.Details_estimate_offline_score_ranking,
            Resources.Details_test_download_and_install,
            Resources.Details_test_keep_installable_pending,
            Resources.Details_test_delete_pending_source_after_install,
            Resources.Details_test_smart_component_overwrite,
            Resources.Details_test_keep_smart_overwrite_protected_by_rename
        ];

        foreach (string label in promotedLabels)
        {
            Assert.IsFalse(label.Contains("[テスト中]"), label);
            Assert.IsFalse(label.Contains("[TEST]"), label);
        }
        Assert.AreEqual(0, CountOccurrences(viewModel, "本機能はテスト実装中です"));
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_disable_startup_file_scan");
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_skip_init_playlist_load");
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_enable_lr2ir_ranking_cache_startup_update");
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_enable_offline_score_ranking_estimation");
    }

    [TestMethod]
    public void DownloadAndInstall_UsesChartFileKindResolverForDirectChartFiles()
    {
        string root = FindRepositoryRoot();
        string workflowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "PlaylistUrlAcquisitionWorkflow.cs"));
        string candidateHelper = ExtractBetween(workflowCode, "private static bool IsDownloadAndInstallCandidateFileName", "private static string NormalizeDownloadUrlString");
        string downloadResponseMethod = ExtractBetween(workflowCode, "private async Task<PlaylistUrlDownloadResult> DownloadPlaylistUrlResponseCandidateAsync(Uri requestedUri, AppHttpResponse response, string tempDirectory, int remainingSharedPageResolutionDepth", "private static bool IsDownloadAndInstallCandidateFileName");
        MethodInfo helper = typeof(PlaylistUrlAcquisitionWorkflow).GetMethod("IsDownloadAndInstallCandidateFileName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(helper);

        StringAssert.Contains(candidateHelper, "ChartFileKindResolver.IsSupportedChartFilePath(fileName)");
        StringAssert.Contains(candidateHelper, "DownloadAndInstallArchiveExtensions");
        Assert.IsFalse(candidateHelper.Contains("BMSFile.bmsExtensions"));
        StringAssert.Contains(downloadResponseMethod, "IsDownloadAndInstallCandidateFileName(fileName)");
        Assert.IsFalse(downloadResponseMethod.Contains("BMSFile.bmsExtensions"));
        Assert.IsTrue((bool)helper.Invoke(null, ["chart.bmson"]));
        Assert.IsTrue((bool)helper.Invoke(null, ["chart.bms"]));
        Assert.IsTrue((bool)helper.Invoke(null, ["package.lzh"]));
        Assert.IsFalse((bool)helper.Invoke(null, ["notes.txt"]));
    }

    [TestMethod]
    public void PlaylistUrlTargets_UseRequestedColumnAndDeduplicateExactUrls()
    {
        PlaylistDetailRow first = CreatePlaylistUrlRow("https://example.invalid/package.zip", "https://example.invalid/diff-a.zip");
        PlaylistDetailRow duplicateMain = CreatePlaylistUrlRow("https://example.invalid/package.zip", "https://example.invalid/diff-b.zip");

        List<Uri> mainTargets = PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets([first, duplicateMain, new object()], isDiffUrl: false);
        List<Uri> diffTargets = PlaylistContextMenuTargetResolver.BuildPlaylistUrlTargets([first, duplicateMain], isDiffUrl: true);

        Assert.AreEqual(1, mainTargets.Count);
        Assert.AreEqual("https://example.invalid/package.zip", mainTargets[0].ToString());
        Assert.AreEqual(2, diffTargets.Count);
        Assert.AreEqual("https://example.invalid/diff-a.zip", diffTargets[0].ToString());
        Assert.AreEqual("https://example.invalid/diff-b.zip", diffTargets[1].ToString());
    }

    [TestMethod]
    public void PlaylistExternalPackageLookupContextMenu_IsBelowDiffUrlAndUsesResources()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string tableContextMenu = ExtractBetween(xaml, "<ContextMenu x:Key=\"tableContextMenu\"", "<ContextMenu x:Key=\"tableContextMenuPlaylistMissing\"");
        string missingContextMenu = ExtractBetween(xaml, "<ContextMenu x:Key=\"tableContextMenuPlaylistMissing\"", "<ContextMenu x:Key=\"playHistoryContextMenu\"");

        Assert.AreEqual(2, CountOccurrences(xaml, "Name=\"tableContextMenuItemFindExternalPackage\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Find_external_package_from_playlist_md5, Mode=OneWay}\" Click=\"tableContextMenuItemFindExternalPackageClick\""));
        Assert.IsTrue(tableContextMenu.IndexOf("tableContextMenuItemOpenURLdiff", StringComparison.Ordinal) < tableContextMenu.IndexOf("tableContextMenuItemFindExternalPackage", StringComparison.Ordinal));
        Assert.IsTrue(missingContextMenu.IndexOf("tableContextMenuItemOpenURLdiff", StringComparison.Ordinal) < missingContextMenu.IndexOf("tableContextMenuItemFindExternalPackage", StringComparison.Ordinal));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Find_external_package_from_playlist_md5));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Confirm_SelectedPlaylistExternalPackageLookup));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Warn_SelectedPlaylistExternalPackageLookup));
    }

    [TestMethod]
    public void PlaylistExternalPackageMd5Targets_UsePlaylistEntryMd5AndDeduplicate()
    {
        PlaylistDetailRow ownedRow = CreatePlaylistExternalPackageRow("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", isOwned: true);
        PlaylistDetailSourceRow missingRow = CreatePlaylistExternalPackageSourceRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        PlaylistDetailRow otherRow = CreatePlaylistExternalPackageRow("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", isOwned: false);
        PlaylistDetailRow dummyFolderRow = CreatePlaylistExternalPackageRow(BMSTableEntry.DUMMY_MD5_FOR_EMPTY_FOLDER, isOwned: false);
        PlaylistDetailRow invalidRow = CreatePlaylistExternalPackageRow(null, isOwned: false);

        List<string> targets = PlaylistContextMenuTargetResolver.BuildPlaylistExternalPackageMd5Targets([ownedRow, missingRow, otherRow, dummyFolderRow, invalidRow, new object()]);

        CollectionAssert.AreEqual(
            new[]
            {
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
            },
            targets);
    }

    [TestMethod]
    public void PlaylistUrlBulkImport_IsOwnedByWorkspaceAndShellForwarded()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string workspaceCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.PlaylistUrlAcquisition.cs"));
        string workspaceOwnerCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "PlaylistWorkspaceViewModel.cs"));
        string workflowCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "Models",
            "PlaylistUrlAcquisitionWorkflow.cs"));
        string progressHubCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "OperationProgressHubViewModel.cs");
        string compositionCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs"));

        StringAssert.Contains(mainWindowCode, "viewModel.PlaylistWorkspace.RunSinglePlaylistUrlAsync(url)");
        StringAssert.Contains(mainWindowCode, "RunPlaylistUrlActionAsync(GetEffectiveContextMenuRows(contextRow)");
        StringAssert.Contains(mainWindowCode, "RunPlaylistExternalPackageLookupAsync(GetEffectiveContextMenuRows(contextRow))");
        StringAssert.Contains(mainWindowCode, "CapturePlaylistUrlContextMenuAvailability(");
        Assert.IsFalse(mainWindowCode.Contains("CanStartPlaylistExternalPackageLookup"));
        Assert.IsFalse(mainWindowCode.Contains("BuildPlaylistUrlTargets("));
        Assert.IsFalse(mainWindowCode.Contains("BuildPlaylistExternalPackageMd5Targets("));
        Assert.IsFalse(mainWindowCode.Contains("DownloadPlaylistUrlCandidateAsync"));
        Assert.IsFalse(mainWindowCode.Contains("playlistUrlBulkDownload"));
        StringAssert.Contains(mainWindowCode, "viewModel.PlaylistWorkspace.PlaylistUrlInstallTreeExpansionRequested += MainWindow_PlaylistUrlInstallTreeExpansionRequested");
        Assert.IsFalse(mainWindowCode.Contains("PlaylistUrlInstallQueued += PlaylistWorkspacePlaylistUrlInstallQueued"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlSingleInstallRequested"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlInstallQueueRequested"));
        Assert.IsFalse(mainWindowCode.Contains("PackageInstallWorkflow.EnqueueSingle"));
        Assert.IsFalse(mainWindowCode.Contains("PackageInstallWorkflow.Enqueue(request"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlAcquisitionConfirmationRequested"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlAcquisitionNotificationRequested"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlAcquisitionSummaryReady"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlBrowserOpenRequested"));
        Assert.IsFalse(mainWindowCode.Contains("Process.Start(request.Uri.ToString())"));
        StringAssert.Contains(workspaceCode, "DownloadCandidateAsync");
        StringAssert.Contains(workspaceCode, "RunPlaylistUrlActionAsync");
        StringAssert.Contains(workspaceCode, "RunPlaylistExternalPackageLookupAsync");
        StringAssert.Contains(workspaceCode, "CapturePlaylistUrlContextMenuAvailability(");
        Assert.IsFalse(workspaceCode.Contains("PlaylistUrlAcquisitionConfirmationRequested"));
        Assert.IsFalse(workspaceCode.Contains("PlaylistUrlAcquisitionNotificationRequested"));
        Assert.IsFalse(workspaceCode.Contains("PlaylistUrlAcquisitionSummaryReady"));
        StringAssert.Contains(workspaceCode, "CancelPlaylistUrlDownload");
        StringAssert.Contains(workspaceCode, "playlistUrlInstallSink");
        StringAssert.Contains(workspaceCode, "playlistUrlInstallSink(Array.AsReadOnly(pathSnapshot))");
        StringAssert.Contains(workspaceCode, "playlistUrlAcquisitionPresentationScheduler(");
        StringAssert.Contains(workspaceCode, "PlaylistUrlInstallTreeExpansionRequested?.Invoke()");
        StringAssert.Contains(workspaceCode, "internal event Action PlaylistUrlInstallTreeExpansionRequested");
        Assert.IsFalse(workspaceCode.Contains("PlaylistUrlInstallQueued"));
        StringAssert.Contains(workspaceCode, "playlistUrlBrowserOpenSink");
        StringAssert.Contains(workspaceCode, "playlistUrlAcquisitionPresentationScheduler(");
        Assert.IsFalse(workspaceCode.Contains("PlaylistUrlSingleInstallRequestedEventArgs"));
        Assert.IsFalse(workspaceCode.Contains("PlaylistUrlInstallQueueRequestedEventArgs"));
        StringAssert.Contains(workflowCode, "IPlaylistUrlDownloadGateway");
        Assert.IsFalse(workflowCode.Contains("NLog"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigurePlaylistUrlAcquisition"));
        StringAssert.Contains(workspaceOwnerCode, "playlistUrlAcquisitionOptionsProvider");
        StringAssert.Contains(workspaceOwnerCode, "playlistUrlBrowserOpenSink");
        StringAssert.Contains(workspaceCode, "playlistUrlInstallQueueActiveProvider");
        StringAssert.Contains(compositionCode, "new PlaylistUrlAcquisitionWorkflow(");
        StringAssert.Contains(compositionCode, "PlaylistExternalPackageLookupService.CreateDefault()");
        StringAssert.Contains(compositionCode, "playlistUrlAcquisitionOptionsProvider");
        StringAssert.Contains(compositionCode, "playlistUrlInstallQueueActiveProvider");
        StringAssert.Contains(compositionCode, "playlistUrlInstallSink");
        StringAssert.Contains(compositionCode, "playlistUrlBrowserOpenSink");
        Assert.IsFalse(compositionCode.Contains("playlistUrlInstallTreeExpansionSink"));
        StringAssert.Contains(compositionCode, "externalPlaylistImportWarningLog");
        StringAssert.Contains(compositionCode, "externalPlaylistImportInfoLog");
        StringAssert.Contains(compositionCode, "beatorajaTableUrlImportWarningLog");
        StringAssert.Contains(compositionCode, "beatorajaTableUrlImportInfoLog");
        Assert.IsFalse(compositionCode.Contains("ConfigurePlaylistUrlAcquisition"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureExternalPlaylistImportLogging"));
        Assert.IsFalse(compositionCode.Contains("ConfigureExternalPlaylistImportLogging"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureBeatorajaTableUrlImportLogging"));
        Assert.IsFalse(compositionCode.Contains("ConfigureBeatorajaTableUrlImportLogging"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigurePlaylistSummaryColumnSettingsStore"));
        Assert.IsFalse(compositionCode.Contains("ConfigurePlaylistSummaryColumnSettingsStore"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureSummaryBmtSort"));
        Assert.IsFalse(compositionCode.Contains("ConfigureSummaryBmtSort"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureKeywordSearchHistory"));
        Assert.IsFalse(compositionCode.Contains("ConfigureKeywordSearchHistory"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureDetailEditing"));
        Assert.IsFalse(compositionCode.Contains("ConfigureDetailEditing"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigurePropertyEditing"));
        Assert.IsFalse(compositionCode.Contains("ConfigurePropertyEditing"));
        Assert.IsFalse(mainWindowCode.Contains("new PlaylistPropertySaveService("));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureSummaryBulkEditing"));
        Assert.IsFalse(compositionCode.Contains("ConfigureSummaryBulkEditing"));
        Assert.IsFalse(mainWindowCode.Contains("ConfigureSummaryBulkEditing"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureSummaryBulkWarningLogging"));
        Assert.IsFalse(compositionCode.Contains("ConfigureSummaryBulkWarningLogging"));
        Assert.IsFalse(mainWindowCode.Contains("ConfigureSummaryBulkWarningLogging"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigureMutations"));
        Assert.IsFalse(compositionCode.Contains("ConfigureMutations"));
        Assert.IsFalse(mainWindowCode.Contains("RunPlaylistOperationWithNotifications"));
        StringAssert.Contains(workspaceCode, "GetPlaylistUrlAcquisitionOptions");
        StringAssert.Contains(workspaceCode, "PlaylistUrlDownloadStatusChanged");
        StringAssert.Contains(progressHubCode, "PlaylistWorkspacePlaylistUrlDownloadStatusChanged");
        StringAssert.Contains(progressHubCode, "UpdatePlaylistUrlDownloadStatus(snapshot)");
        Assert.IsFalse(mainWindowCode.Contains("PlaylistWorkspacePlaylistUrlDownloadStatusChanged"));
        StringAssert.Contains(workspaceCode, "playlistWorkspaceDialogService.ConfirmAsync(");
        StringAssert.Contains(workspaceCode, "playlistWorkspaceDialogService.ShowMessageAsync(");
        StringAssert.Contains(workspaceCode, "Confirm_SelectedPlaylistExternalPackageLookup");
        StringAssert.Contains(workspaceCode, "Msg_SelectedPlaylistUrlDownloadResult");

        string dropHandler = ExtractBetween(mainWindowCode, "private void Window_Drop", "private void Window_DragOver");
        string dragOverHandler = ExtractBetween(mainWindowCode, "private void Window_DragOver", "private void Window_MouseLeftButtonDown");
        StringAssert.Contains(dropHandler, "IsPlaylistUrlDownloadRunning");
        StringAssert.Contains(dragOverHandler, "IsPlaylistUrlDownloadRunning");
        Assert.IsFalse(mainWindowCode.Contains("PlaylistUrlAcquisitionConfirmationRequested"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistUrlAcquisitionNotificationRequested"));
        Assert.IsFalse(mainWindowCode.Contains("PlaylistUrlAcquisitionSummaryReady"));
    }

    [TestMethod]
    public void PlaylistUrlDownload_BrowserFallbackRecognizesPageUrls()
    {
        Assert.IsTrue(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/folder/")));
        Assert.IsTrue(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/index.htm")));
        Assert.IsTrue(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/index.html")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/package.zip")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://venue.bmssearch.net/event/1/1")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://bmssearch.net/bmses/1")));
    }

    [TestMethod]
    public void PlaylistUrlDownload_NormalizesCloudStorageShareUrls()
    {
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://drive.google.com/file/d/abc123/view?usp=sharing")).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://drive.google.com/open?id=abc123&usp=sharing")).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://docs.google.com/uc?export=download&id=abc123")).ToString());
        Assert.AreEqual(
            "https://www.dropbox.com/scl/fi/token/package.zip?rlkey=key&dl=1",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://www.dropbox.com/scl/fi/token/package.zip?rlkey=key&dl=0")).ToString());
        Assert.AreEqual(
            "https://notdropbox.com/scl/fi/token/package.zip?dl=0",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://notdropbox.com/scl/fi/token/package.zip?dl=0")).ToString());
    }

    [TestMethod]
    public void PlaylistUrlDownload_ResolvesKnownSharedDownloadPages()
    {
        const string googleDriveWarningHtml = """
            <html><body>
            <form id="download-form" method="get" action="/download">
              <input type="hidden" name="id" value="abc123">
              <input type="hidden" name="confirm" value="token">
              <input type="submit" name="submit" value="download">
            </form>
            </body></html>
            """;
        const string mediaFireHtml = """
            <html><body>
            <a id="downloadButton" href="https://download123.mediafire.com/abc/package.zip">Download</a>
            </body></html>
            """;
        const string manbowHtml = """
            <html><body>
            <Th>DownLoadAddress</Th><td><a href="https://example.invalid/body.zip">Download</a></td>
            </body></html>
            """;
        const string venueHtml = """
            <html><body>
            <a href="https://example.invalid/readme.html">info</a>
            <a href="https://drive.google.com/file/d/abc123/view?usp=sharing">Download</a>
            </body></html>
            """;
        const string bmsSearchHtml = """
            <html><body>
            <a href="/help.html">help</a>
            <a href="/archives/package.lzh">archive</a>
            </body></html>
            """;
        const string bmsSearchNextHtml = """
            <html><body>
            <script>self.__next_f.push([1,"7:{\"title\":\"\uD83D\uDE00\",\"downloads\":[{\"url\":\"https://anonymous.bms.ms/data/body.zip\",\"description\":\"\"}],\"relatedLinks\":[]}"])</script>
            </body></html>
            """;
        const string venueNextHtml = """
            <html><body>
            <script>self.__next_f.push([1,"2c:{\"description\":\"3.5型\",\"downloadURL\":\"https://anonymous.bms.ms/data/itsfree_battle.7z\",\"type\":\"CORE\"}"])</script>
            </body></html>
            """;
        const string venueMixedPackageHtml = """
            <html><body>
            <script>self.__next_f.push([1,"16:{\"downloadURL\":\"https://drive.google.com/file/d/eventPackage/view?usp=sharing\",\"description\":\"イベント全体\"}\n2c:{\"description\":\"通常版\",\"downloadURL\":\"https://drive.google.com/file/d/entryCore/view?usp=sharing\",\"type\":\"CORE\"}"])</script>
            </body></html>
            """;
        const string venueTypeFirstCoreHtml = """
            <html><body>
            <script>self.__next_f.push([1,"16:{\"downloadURL\":\"https://drive.google.com/file/d/eventPackage/view?usp=sharing\",\"description\":\"イベント全体\"}\n2c:{\"description\":\"通常版\",\"type\":\"CORE\",\"downloadURL\":\"https://drive.google.com/file/d/entryTypeFirst/view?usp=sharing\"}"])</script>
            </body></html>
            """;
        const string venuePackageAnchorHtml = """
            <html><body>
            <a href="https://drive.google.com/file/d/eventPackage/view?usp=sharing">イベント全体</a>
            <a href="https://drive.google.com/file/d/entryAnchor/view?usp=sharing">通常版</a>
            <script>self.__next_f.push([1,"16:{\"downloadURL\":\"https://drive.google.com/file/d/eventPackage/view?usp=sharing\",\"description\":\"イベント全体\"}"])</script>
            </body></html>
            """;
        const string venueMediaFireHtml = """
            <html><body>
            <a href="https://www.mediafire.com/file/abc/package.zip/file">Download</a>
            </body></html>
            """;
        const string mediaFireArchiveNamedLandingHtml = """
            <html><body>
            <a id="downloadButton" class="input popsok" href="https://download123.mediafire.com/token/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z">Download</a>
            </body></html>
            """;
        const string googleDriveUnsafeActionHtml = """
            <html><body>
            <form id="download-form" method="get" action="file:///C:/secret.zip">
              <input type="hidden" name="id" value="abc123">
            </form>
            </body></html>
            """;
        const string mediaFireUnsafeHostHtml = """
            <html><body>
            <a id="downloadButton" href="https://evilmediafire.com/abc/package.zip">Download</a>
            </body></html>
            """;
        const string manbowUnsafeSchemeHtml = """
            <html><body>
            <Th>DownLoadAddress</Th><td><a href="file:///C:/secret.zip">Download</a></td>
            </body></html>
            """;
        const string venueUnsafeHostHtml = """
            <html><body>
            <a href="https://evil.example.invalid/index.html">not a direct archive</a>
            </body></html>
            """;
        const string venueSourcePageOnlyHtml = """
            <html><body>
            <a href="https://bmssearch.net/bmses/2uLp8a8bJYLmrx">BMS SEARCH page</a>
            </body></html>
            """;
        const string venueGoogleDriveFolderOnlyHtml = """
            <html><body>
            <a href="https://drive.google.com/drive/folders/folder123">Google Drive folder</a>
            </body></html>
            """;

        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&confirm=token",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"), googleDriveWarningHtml).ToString());
        Assert.AreEqual(
            "https://download123.mediafire.com/abc/package.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://www.mediafire.com/file/abc/package.zip/file"), mediaFireHtml).ToString());
        Assert.AreEqual(
            "https://example.invalid/body.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"), manbowHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/event/1/1"), venueHtml).ToString());
        Assert.AreEqual(
            "https://bmssearch.net/archives/package.lzh",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://bmssearch.net/bmses/1"), bmsSearchHtml).ToString());
        Assert.AreEqual(
            "https://anonymous.bms.ms/data/body.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://bmssearch.net/bmses/2uLp8a8bJYLmrx"), bmsSearchNextHtml).ToString());
        Assert.AreEqual(
            "https://anonymous.bms.ms/data/itsfree_battle.7z",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueNextHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=entryCore&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/bmstukuru2025/80"), venueMixedPackageHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=entryTypeFirst&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/bmstukuru2025/80"), venueTypeFirstCoreHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=entryAnchor&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/bmstukuru2025/80"), venuePackageAnchorHtml).ToString());
        Assert.AreEqual(
            "https://www.mediafire.com/file/abc/package.zip/file",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueMediaFireHtml).ToString());
        Assert.AreEqual(
            "https://download123.mediafire.com/token/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://www.mediafire.com/file/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z"), mediaFireArchiveNamedLandingHtml).ToString());
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"), googleDriveUnsafeActionHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://www.mediafire.com/file/abc/package.zip/file"), mediaFireUnsafeHostHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://evilmediafire.com/file/abc/package.zip/file"), mediaFireHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"), manbowUnsafeSchemeHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/event/1/1"), venueUnsafeHostHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueSourcePageOnlyHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueGoogleDriveFolderOnlyHtml));
    }

    [TestMethod]
    public void PlaylistUrlDownload_ResolvesContentDispositionFileName()
    {
        Assert.AreEqual(
            "BMSをたくさん作るぜ'25_250126更新分.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveContentDispositionFileName("attachment; filename*=UTF-8''BMS%E3%82%92%E3%81%9F%E3%81%8F%E3%81%95%E3%82%93%E4%BD%9C%E3%82%8B%E3%81%9C%2725_250126%E6%9B%B4%E6%96%B0%E5%88%86.zip"));

        string mojibake = Encoding.GetEncoding("ISO-8859-1").GetString(Encoding.UTF8.GetBytes("BMSをたくさん作るぜ'25_250126更新分.zip"));
        Assert.AreEqual(
            "BMSをたくさん作るぜ'25_250126更新分.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveContentDispositionFileName("attachment; filename=\"" + mojibake + "\""));
    }

    [TestMethod]
    public void PlaylistUrlDownload_CreatesStableResolvedDownloadKeys()
    {
        Assert.AreEqual(
            "gdrive:abc123",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download&confirm=t&uuid=volatile")));
        Assert.AreEqual(
            "gdrive:abc123",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://drive.google.com/file/d/abc123/view?usp=sharing")));
        Assert.AreEqual(
            "mediafire:ar4aa9p35amxfer",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://download123.mediafire.com/token/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z")));
        Assert.AreEqual(
            "mediafire:ar4aa9p35amxfer",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://www.mediafire.com/file/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z/file")));
    }

    [TestMethod]
    public void UserSettingDefaults_AppConfigAndSettingsCodeStayInSync()
    {
        string root = FindRepositoryRoot();
        string settingsCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Settings.cs"));
        string appConfigPath = Path.Combine(root, "app.config");
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string appCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "App.cs"));
        string legacyMigratorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "LegacyUserConfigMigrator.cs"));

        Dictionary<string, string> settingsDefaults = ReadSettingsCodeDefaults(settingsCode);
        var appConfigDefaults = XDocument.Load(appConfigPath)
            .Descendants("setting")
            .Where(setting => setting.Attribute("name") != null)
            .ToDictionary(
                setting => setting.Attribute("name").Value,
                setting => setting.Element("value")?.Value ?? string.Empty);

        foreach (string key in settingsDefaults.Keys.Intersect(appConfigDefaults.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            Assert.AreEqual(settingsDefaults[key], appConfigDefaults[key], key);
        }

        Assert.AreEqual(Settings.DefaultTableListUrl, settingsDefaults["TableListURL"]);
        Assert.AreEqual(Settings.DefaultTableListUrl, appConfigDefaults["TableListURL"]);
        Assert.AreEqual("False", settingsDefaults["EstimateOfflineScoreRanking"]);
        Assert.AreEqual("False", appConfigDefaults["EstimateOfflineScoreRanking"]);
        Assert.AreEqual("False", settingsDefaults["UpdateLr2IrRankingCacheOnStartup"]);
        Assert.AreEqual("False", appConfigDefaults["UpdateLr2IrRankingCacheOnStartup"]);
        Assert.AreEqual(string.Empty, settingsDefaults["PlayHistoryDisplayTargetSetsJson"]);
        Assert.AreEqual(string.Empty, appConfigDefaults["PlayHistoryDisplayTargetSetsJson"]);
        Assert.AreEqual(string.Empty, settingsDefaults["PlayHistorySelectedDisplayTargetIdentity"]);
        Assert.AreEqual(string.Empty, appConfigDefaults["PlayHistorySelectedDisplayTargetIdentity"]);
        Assert.IsFalse(settingsDefaults.ContainsKey("SkipEstimateOfflineScoreRanking"));
        Assert.IsFalse(appConfigDefaults.ContainsKey("SkipEstimateOfflineScoreRanking"));
        StringAssert.Contains(settingsCode, "internal const string LegacyTableListUrl = \"http://www.ribbit.xyz/bms/tables/table_info.json\";");
        StringAssert.Contains(settingsCode, "[DefaultSettingValue(DefaultTableListUrl)]");

        string tableListProperty = ExtractBetween(viewModelCode, "public Uri TableListURL", "public bool EnablePlaylistUrlCompletion");
        StringAssert.Contains(tableListProperty, "new Uri(Settings.DefaultTableListUrl)");
        Assert.IsFalse(tableListProperty.Contains(Settings.LegacyTableListUrl));

        StringAssert.Contains(legacyMigratorCode, "Settings.LegacyTableListUrl");
        StringAssert.Contains(legacyMigratorCode, "Settings.DefaultTableListUrl");
        StringAssert.Contains(legacyMigratorCode, "NormalizeMigratedConfig");
        Assert.IsFalse(appCode.Contains("MigrateApplicationSettings"));
    }

    [TestMethod]
    public void StartupRankingRefresh_IsQueuedOnlyWhenIrScoreOrRankingCacheStartupUpdateIsEnabled()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string initializeTail = ExtractBetween(
            libraryCode,
            "if (updateIrScore && activeScoreSource != ActiveScoreSource.None)",
            "stopwatchInitialize.Stop();");
        string deferredRun = ExtractBetween(
            libraryCode,
            "private RankingRefreshRunResult RunDeferredRankingRefresh(int requestVersion)",
            "private bool IsDeferredRankingRefreshRequestSuperseded");

        StringAssert.Contains(initializeTail, "options.EnableDownloadLr2IrScoreAndDetectUnsent || options.UpdateLr2IrRankingCacheOnStartup");
        StringAssert.Contains(deferredRun, "if (optionsSnapshot.UpdateLr2IrRankingCacheOnStartup)");
        StringAssert.Contains(deferredRun, "ranking_cache_refresh skipped reason=disabled");
    }

    [TestMethod]
    public void WpfFileDialogRoutes_SupportStandaloneMultiSelect()
    {
        string root = FindRepositoryRoot();
        string settingDialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string allPickerXaml = settingDialogXaml + mainWindowXaml;

        Assert.AreEqual(0, CountOccurrences(allPickerXaml, "<l:FolderBrowserDialogInteractionMessageAction"));
        Assert.AreEqual(0, CountOccurrences(allPickerXaml, "<l:OpenFileDialogInteractionMessageAction"));
        Assert.IsFalse(allPickerXaml.Contains("v:CommonOpenFileDialogInteractionMessageAction"));
        Assert.IsFalse(allPickerXaml.Contains("FolderSelectionMessage"));
        Assert.IsFalse(allPickerXaml.Contains("OpeningFileSelectionMessage"));
        Assert.IsFalse(File.Exists(Path.Combine(root, "BeMusicSeeker", "Views", "CommonOpenFileDialogInteractionMessageAction.cs")));
        StringAssert.Contains(settingDialogCode, "PickRootFolderForSetting(");
        StringAssert.Contains(settingDialogCode, "PickFileForSetting(");
        StringAssert.Contains(settingDialogCode, "PickDirectoryForSetting(");
        StringAssert.Contains(settingDialogCode, "SetRootFolderPathFromPicker(");
        StringAssert.Contains(settingDialogCode, "SetFilePathFromPicker(");
        StringAssert.Contains(settingDialogCode, "SetDirectoryPathFromPicker(");
        StringAssert.Contains(settingDialogCode, "AddBmsSearchRootPathFromPicker(");
        StringAssert.Contains(viewModelCode, "AddBmsSearchRootPathFromMainWindowPicker(");

        StringAssert.Contains(settingDialogXaml, "Click=\"buttonAddBmsSearchRootPathsClicked\"");
        StringAssert.Contains(settingDialogCode, "PickFolderAsync(new UiFolderPickerRequest(");
        StringAssert.Contains(settingDialogCode, "multiselect: true");
        StringAssert.Contains(settingDialogCode, "settingDialogViewModel.AddBmsSearchRootPaths(result.FolderPaths);");
        StringAssert.Contains(viewModelCode, "private void AddBmsSearchRootPaths(IEnumerable<string> paths, string settingPropertyPath, bool saveImmediately)");
        StringAssert.Contains(viewModelCode, "public void AddStandaloneBmsRootPaths(IEnumerable<string> paths)");
        StringAssert.Contains(viewModelCode, "NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(paths ?? [])");
        StringAssert.Contains(viewModelCode, "ThrowIfLr2IncompatibleStandaloneBmsRoots(requestedPaths)");

        Type utilityType = typeof(MainWindow).Assembly.GetType("BeMusicSeeker.Views.Dialogs.UiFilePickerUtilities");
        Assert.IsNotNull(utilityType);
        MethodInfo parseMethod = utilityType.GetMethod("ParseFilterPairs", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parseMethod);
        MethodInfo inferDefaultExtensionMethod = utilityType.GetMethod("InferDefaultExtension", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(inferDefaultExtensionMethod);
        var parsed = ((System.Collections.IEnumerable)parseMethod.Invoke(null, ["song.db (*.db)|*.db|すべてのファイル(*.*)|*.*"]))
            .Cast<Tuple<string, string>>()
            .ToList();
        Assert.AreEqual(2, parsed.Count);
        Assert.AreEqual("song.db (*.db)", parsed[0].Item1);
        Assert.AreEqual("*.db", parsed[0].Item2);
        Assert.AreEqual("*.*", parsed[1].Item2);
        var fallback = ((System.Collections.IEnumerable)parseMethod.Invoke(null, ["broken"]))
            .Cast<Tuple<string, string>>()
            .ToList();
        Assert.AreEqual("*.*", fallback[0].Item2);
        Assert.AreEqual("db", inferDefaultExtensionMethod.Invoke(null, ["song.db", "song.db (*.db)|*.db|すべてのファイル(*.*)|*.*"]));
        Assert.AreEqual("xml", inferDefaultExtensionMethod.Invoke(null, ["config.xml", "|config.xm?|すべてのファイル(*.*)|*.*"]));
        Assert.AreEqual("bmp", inferDefaultExtensionMethod.Invoke(null, [string.Empty, "Image file|*.bmp;*.gif;*.jpg;*.jpeg;*.png|すべてのファイル(*.*)|*.*"]));
        Assert.IsNull(inferDefaultExtensionMethod.Invoke(null, [string.Empty, "すべてのファイル(*.*)|*.*"]));
        string coordinatorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "Dialogs", "UiDialogCoordinator.cs"));
        StringAssert.Contains(coordinatorCode, "dialog.DefaultExt = \".\" + defaultExtension.TrimStart('.');");
        StringAssert.Contains(coordinatorCode, "UiFilePickerUtilities.InferDefaultExtension(request.FileName, request.Filter)");
        StringAssert.Contains(settingDialogCode, "\"|config.xm?|");
        StringAssert.Contains(settingDialogCode, "\"song.db (*.db)|*.db|");
        StringAssert.Contains(Resources.FileDialogFilter_scoreDB, "score.db (*.db)|*.db|");
    }

    [TestMethod]
    public void FileDialogs_SetDefaultExtensionsForTypedFileNames()
    {
        string root = FindRepositoryRoot();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string loadPlaylistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));

        StringAssert.Contains(settingDialogCode, "new UiSaveFilePickerRequest(");
        StringAssert.Contains(settingDialogCode, "\".sql\"");
        StringAssert.Contains(settingDialogCode, "addExtension: true");
        string playlistWorkspaceCode = SourceTextTestHelper.ReadPlaylistWorkspaceViewModelSourceText();
        StringAssert.Contains(playlistWorkspaceCode, "new UiSaveFilePickerRequest(");
        StringAssert.Contains(playlistWorkspaceCode, "\".json\"");
        StringAssert.Contains(playlistWorkspaceCode, "addExtension: true");
        StringAssert.Contains(loadPlaylistCode, "new UiFilePickerRequest(");
        StringAssert.Contains(loadPlaylistCode, "defaultExtension: \".json\"");
    }

    [TestMethod]
    public void SettingDialog_UsesTypedBindingConverters()
    {
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));

        Assert.IsFalse(settingDialogXaml.Contains("QuickConverter", StringComparison.Ordinal));
        StringAssert.Contains(settingDialogXaml, "x:Key=\"wasapiControlEnabledConverter\"");
        StringAssert.Contains(settingDialogXaml, "Lr2_song_db_sync_data_resync");
        StringAssert.Contains(settingDialogXaml, "IsEnabled=\"{Binding IsChecked, ElementName=radioButtonUseLR2}\"");
        Assert.IsFalse(settingDialogXaml.Contains("checkBoxEnableLr2SongDbFullGeneration"));
    }

    [TestMethod]
    public void SettingDialogUninstall_ShowsResultBeforeExit()
    {
        string root = FindRepositoryRoot();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadSettingsDialogViewModelSourceText();
        string uninstallClickHandler = ExtractMethodBody(settingDialogCode, "private async void detailTabItemUninstallButtonClicked(object sender, RoutedEventArgs e)");

        StringAssert.Contains(uninstallClickHandler, "await settingDialogViewModel.UninstallApplicationDataAsync();");
        StringAssert.Contains(uninstallClickHandler, "settingDialogOperationGrid.IsEnabled = false;");
        StringAssert.Contains(uninstallClickHandler, "if (!closeAfterSuccess)");
        StringAssert.Contains(uninstallClickHandler, "settingDialogOperationGrid.IsEnabled = true;");
        StringAssert.Contains(uninstallClickHandler, "window.Close();");
        Assert.IsFalse(uninstallClickHandler.Contains("UninstallAllData"));
        Assert.IsFalse(uninstallClickHandler.Contains("Task.Run"));
        Assert.IsFalse(uninstallClickHandler.Contains("Msg_success_uninstall"));
        Assert.IsFalse(uninstallClickHandler.Contains("Msg_failed_uninstall"));
        Assert.IsFalse(uninstallClickHandler.Contains("Msg_settings_apply_blocked_during_initialization"));
        Assert.IsFalse(uninstallClickHandler.Contains("Application.Current.MainWindow"));
        Assert.IsFalse(uninstallClickHandler.Contains("base.Dispatcher.BeginInvoke"));
        Assert.IsTrue(
            uninstallClickHandler.IndexOf("await settingDialogViewModel.UninstallApplicationDataAsync();", StringComparison.Ordinal)
            < uninstallClickHandler.IndexOf("window.Close();", StringComparison.Ordinal));
        Assert.IsTrue(
            uninstallClickHandler.IndexOf("settingDialogOperationGrid.IsEnabled = false;", StringComparison.Ordinal)
            < uninstallClickHandler.IndexOf("await settingDialogViewModel.UninstallApplicationDataAsync();", StringComparison.Ordinal));
        Assert.IsFalse(viewModelCode.Contains("internal void UninstallAllData()"));
        StringAssert.Contains(viewModelCode, "internal Task<ApplicationDataUninstallResult> UninstallApplicationDataAsync()");
        StringAssert.Contains(viewModelCode, "new ApplicationDataUninstallRequest(");
        StringAssert.Contains(viewModelCode, "new ApplicationDataUninstallWorkflowOwner(");
    }

    [TestMethod]
    public void DuplicateMergeMaintenanceDefersResourceHealthIndexRebuildAndLogsDuplicateSearchStages()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string mergeOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "Models", "BMSLibrary.LibraryFileOperationOwner.Merge.cs");
        string mergeMethod = mergeOwnerCode;
        string mergeMaintenanceHostMethod = mergeOwnerCode;
        string duplicateSearchMethod = libraryCode;

        StringAssert.Contains(libraryCode, "DeferOnUpdates");
        StringAssert.Contains(libraryCode, "ResourceHealthIndexUpdateMode.DeltaOnUpdates");
        StringAssert.Contains(libraryCode, "resourceHealthMutationReason = receipt.Reason;");
        StringAssert.Contains(libraryCode, "Reason = reason ?? \"maintenance\"");
        StringAssert.Contains(libraryCode, "BuildOwnedChartCollectionMaintenanceMutationResult(");
        StringAssert.Contains(libraryCode, "DispatchOwnedChartCollectionMutation(mutationResult, resourceHealthMutationReason)");
        StringAssert.Contains(mergeMethod, "ChartStorageTargetSet movedTargets = ChartStorageTargetSet.FromCharts");
        StringAssert.Contains(mergeMethod, "ApplyMergeFolderMaintenance(destinationMaintenanceChartSnapshots)");
        StringAssert.Contains(mergeMethod, "ApplyLibraryMutationDeltaWithPerformanceContext(catalogDelta");
        Assert.IsFalse(mergeMethod.Contains("NormalizeResourceMaintenanceTargetCharts(maintenanceTargets"));
        Assert.IsFalse(mergeMethod.Contains("ChartStorageTargetSet.FromRows(movedBmsFiles, movedBmsonSongs)"));
        StringAssert.Contains(mergeMaintenanceHostMethod, "resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates");
        StringAssert.Contains(mergeMaintenanceHostMethod, "resourceHealthMutationReason: \"merge_folder\"");
        StringAssert.Contains(duplicateSearchMethod, "CreateOwnedDuplicateChartRowSnapshotUnsafe()");
        StringAssert.Contains(duplicateSearchMethod, "ownedSnapshotMs=");
        StringAssert.Contains(duplicateSearchMethod, "clearDuplicateStateMs=");
        StringAssert.Contains(duplicateSearchMethod, "duplicateWarningFullClearPending");
        StringAssert.Contains(duplicateSearchMethod, "duplicateService.ClearDuplicateState(duplicateWarningClearTargets)");
        Assert.IsFalse(duplicateSearchMethod.Contains("duplicateService.BuildSnapshot(bmsSnapshot, bmsonSnapshot)"));
        StringAssert.Contains(duplicateSearchMethod, "analyzeMs=");
        StringAssert.Contains(duplicateSearchMethod, "applyWarningsMs=");
        StringAssert.Contains(duplicateSearchMethod, "duplicateHashCount=");
        StringAssert.Contains(duplicateSearchMethod, "duplicateHashRowCount=");
        StringAssert.Contains(duplicateSearchMethod, "connectedDirCount=");
        StringAssert.Contains(duplicateSearchMethod, "materializedChartCount=");
    }

    [TestMethod]
    public void DuplicateFileCheckConfirmations_RespectSharedMessageSetting()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string ownerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker",
            "ViewModels",
            "MainWindow",
            "DuplicateMaintenanceWorkflowOwner.cs");
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(mainWindowCode, "viewModel.DuplicateMaintenanceWorkflow");
        StringAssert.Contains(viewModelCode, "DuplicateMaintenanceWorkflow.WorkflowChanged += DuplicateMaintenanceWorkflowChanged;");
        StringAssert.Contains(ownerCode, "showConfirmationProvider()");
        StringAssert.Contains(ownerCode, "Msg_merge_bms_target");
        StringAssert.Contains(ownerCode, "Msg_cleanup_duplicate_hash");
        StringAssert.Contains(ownerCode, "chartMutationActivity.Enter()");
        StringAssert.Contains(ownerCode, "PublishRefreshSuppressionChanged(isSuppressed: true)");
        StringAssert.Contains(ownerCode, "PublishRefreshPriorityWindowChanged(isActive: true");
        Assert.IsFalse(viewModelCode.Contains("IDuplicateMaintenanceActivityPort"));
        Assert.IsFalse(viewModelCode.Contains("IDuplicateMaintenanceRefreshPort"));
        Assert.IsFalse(ownerCode.Contains("IDuplicateMaintenanceActivityPort"));
        Assert.IsFalse(ownerCode.Contains("IDuplicateMaintenanceRefreshPort"));
        Assert.IsFalse(viewModelCode.Contains("DuplicateMaintenanceActivityChangedEventArgs"));
        Assert.IsFalse(ownerCode.Contains("DuplicateMaintenanceActivityChangedEventArgs"));
        Assert.IsFalse(ownerCode.Contains("NoOpDuplicateMaintenance"));
        string duplicateContextHandler = ExtractMethodBody(
            mainWindowCode,
            "private void treeViewDuplicateFolderContextMenuOpened(");
        StringAssert.Contains(
            duplicateContextHandler,
            "CaptureDuplicateFolderMergeDestinations(duplicateGroup, dataContext)");
        Assert.IsFalse(duplicateContextHandler.Contains("duplicateGroup.Folders.Except"));
        string duplicateExplorerHandler = ExtractMethodBody(
            mainWindowCode,
            "private void treeViewDuplicateFolderContextMenuOpenExplorerClick(");
        StringAssert.Contains(
            duplicateExplorerHandler,
            "DuplicateMaintenanceWorkflow.OpenDuplicateFolderInExplorer(dataContext)");
        Assert.IsFalse(duplicateExplorerHandler.Contains("LongPathFileSystem.DirectoryExists"));
        Assert.IsFalse(duplicateExplorerHandler.Contains("ExplorerOpenService.OpenDirectory"));
        string duplicateKeyHandler = ExtractMethodBody(
            mainWindowCode,
            "private async void duplicateFolderKeyDown(");
        StringAssert.Contains(
            duplicateKeyHandler,
            "CaptureDuplicateFolderKeyboardAction(duplicateGroup, srcPath)");
        Assert.IsFalse(duplicateKeyHandler.Contains("duplicateGroup.Folders.Count"));
        Assert.IsFalse(duplicateKeyHandler.Contains("FirstOrDefault(f => !f.Equals"));
        StringAssert.Contains(duplicateKeyHandler, "RunFolderMergeAsync(");
        StringAssert.Contains(duplicateKeyHandler, "RunHashCleanupAsync(");
        string duplicateMergeHandler = ExtractMethodBody(
            mainWindowCode,
            "private async void treeViewDuplicateFolderContextMenuItemMergeIntoTargetClick(");
        StringAssert.Contains(duplicateMergeHandler, "RunFolderMergeAsync(");
        Assert.IsFalse(mainWindowCode.Contains("ExecuteDuplicateFolderMergeAsync("));
        Assert.IsFalse(mainWindowCode.Contains("ExecuteDuplicateHashCleanupAsync("));
        Assert.IsFalse(mainWindowCode.Contains("ConfirmFolderMerge("));
        Assert.IsFalse(mainWindowCode.Contains("ConfirmHashCleanup("));
        Assert.IsFalse(mainWindowCode.Contains("DuplicateFolderMergeOperation"));
        Assert.IsFalse(mainWindowCode.Contains("DuplicateHashCleanupOperation"));
        Assert.IsFalse(mainWindowCode.Contains("DuplicateMaintenanceConfirmationResult"));
        Assert.IsFalse(mainWindowCode.Contains("DuplicateHashCleanupConfirmationResult"));
        Assert.IsFalse(ownerCode.Contains("ConfirmFolderMerge("));
        Assert.IsFalse(ownerCode.Contains("ConfirmHashCleanup("));
        Assert.IsFalse(ownerCode.Contains("IDuplicateMaintenanceOperation"));
        Assert.IsFalse(ownerCode.Contains("issuedOperations"));
    }

    [TestMethod]
    public void FileScanMutationUsesOwnedCollectionForRemovedCharts()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string pipelineOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "LibraryFileScanPipelineOwner.cs"));
        string ownedCollectionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "OwnedChartCollectionState.cs"));
        string ownedCollectionOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "CatalogOwnedCollectionOwner.cs"));
        string mutationOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "CatalogMutationOwner.cs"));
        string projectionMethod = ExtractMethodBody(libraryCode, "private OwnedChartCollectionMutationResult CreateFileScanMutationProjection");

        StringAssert.Contains(libraryCode, "catalogMutationOwner.CreateFileScanStorageReplacementRequest(");
        StringAssert.Contains(libraryCode, "catalogMutationOwner.ApplyFileScanStorageReplacement(");
        StringAssert.Contains(libraryCode, "catalogMutationOwner.ApplyInstalledTargetUpsert(");
        StringAssert.Contains(mutationOwnerCode, "CreateInstalledTargetUpsertRequestUnsafe(");
        Assert.IsFalse(libraryCode.Contains("LibraryFileScanStorageMutationHost"));
        Assert.IsFalse(libraryCode.Contains("InstalledChartStorageTargetsApplyHost"));
        Assert.IsFalse(libraryCode.Contains("ApplyInstalledChartStorageRowsUnsafe"));
        Assert.IsFalse(libraryCode.Contains("BuildOwnedChartCollectionFileScanMutationResult"));
        Assert.IsFalse(pipelineOwnerCode.Contains("storageMutationCoordinatorFactory"));
        Assert.IsFalse(projectionMethod.Contains("SongTableFileCheckResult fileCheckResult"));
        StringAssert.Contains(projectionMethod, "request.RemovedPayloadAvailable");
        StringAssert.Contains(projectionMethod, "request.AddedBmsFiles");
        StringAssert.Contains(mutationOwnerCode, "TryCreateFileScanRemovedStorageOwnerIdentityCharts(");
        StringAssert.Contains(mutationOwnerCode, "ReplaceRowsAndCaptureSnapshot(");
        StringAssert.Contains(mutationOwnerCode, "ReplaceForFileScan(storageRows)");
        StringAssert.Contains(mutationOwnerCode, "CatalogFileScanStorageReplacementReceipt");
        StringAssert.Contains(mutationOwnerCode, "CatalogInstalledTargetUpsertReceipt");
        StringAssert.Contains(ownedCollectionOwnerCode, "collection.CreateFileScanRemovedStorageOwnerIdentityCharts(");
        StringAssert.Contains(ownedCollectionCode, "internal List<ChartFile> CreateFileScanRemovedStorageOwnerIdentityCharts(");
    }

    [TestMethod]
    public void EmptyDbStartupOptimizationDocs_DocumentFileDiffPipeline()
    {
        string root = FindRepositoryRoot();
        string initializationCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryInitializationService.cs"));
        string parseCommitOwnerCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "FileScanParseCommitOwner.cs"));
        string planDoc = File.ReadAllText(Path.Combine(root, "devdocs", "plan", "empty-db-first-startup-optimization-plan.md"));
        string startupFlowDoc = File.ReadAllText(Path.Combine(root, "devdocs", "spec", "startup-initialization-flow.md"));

        StringAssert.Contains(parseCommitOwnerCode, "private const int DefaultInlineChartInfoBatchSize = 2048;");
        StringAssert.Contains(parseCommitOwnerCode, "var postParseQueue = new BlockingCollection<FileDiffPostParseWorkItem>(postParseQueueCapacity);");
        StringAssert.Contains(parseCommitOwnerCode, "BlockingCollection<FileScanDiffCommitChunk> commitQueue = streamCommitChunks");
        StringAssert.Contains(parseCommitOwnerCode, "BlockingCollection<FileDiffCommitWriterItem> writerQueue");
        StringAssert.Contains(parseCommitOwnerCode, "commitContext.AddChunk(chunk);");
        StringAssert.Contains(initializationCode, "commit_streaming_enabled=");
        StringAssert.Contains(initializationCode, "commit_writer_queue_wait_ms=");
        StringAssert.Contains(initializationCode, "inline_maintenance_shared_resource_cache_entries=");
        StringAssert.Contains(parseCommitOwnerCode, "BuildInlineBmsMaintenanceBatch(");
        StringAssert.Contains(parseCommitOwnerCode, "Task[] workerTasks = [.. Enumerable.Range(0, parserDegree)");
        StringAssert.Contains(parseCommitOwnerCode, "Parallel.For(0, candidates.Count");
        StringAssert.Contains(initializationCode, "inline_maintenance_wall_ms=");
        StringAssert.Contains(initializationCode, "parser_output_wait_ms=");
        StringAssert.Contains(parseCommitOwnerCode, "\" bms=\" + metrics.BmsCount");
        StringAssert.Contains(parseCommitOwnerCode, "\" bmson=\" + metrics.BmsonCount");

        StringAssert.Contains(planDoc, "parser output queue capacity");
        StringAssert.Contains(planDoc, "2048");
        StringAssert.Contains(planDoc, "inline_maintenance_wall_ms");
        StringAssert.Contains(startupFlowDoc, "post-parse worker");
        StringAssert.Contains(startupFlowDoc, "commit aggregator");
        StringAssert.Contains(startupFlowDoc, "post-parse work item は parsed candidate 1 件");
    }

    [TestMethod]
    public void InstallEstimationParallelism_UsesUnifiedBatchPolicy()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string batchSourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "PendingInstallEstimateBatchSource.cs"));
        string installEstimationDoc = File.ReadAllText(Path.Combine(root, "devdocs", "spec", "install-estimation-current-logic.md"));

        StringAssert.Contains(batchSourceCode, "ManualReestimate");
        StringAssert.Contains(libraryCode, "private sealed class InstallEstimationExecutionPolicy");
        StringAssert.Contains(libraryCode, "internal static InstallEstimationExecutionPolicy ForManualBatch()");
        StringAssert.Contains(libraryCode, "internal static InstallEstimationExecutionPolicy ForPendingBatch(int workItemDegree)");
        StringAssert.Contains(libraryCode, "ProcessPendingInstallEstimateEvaluationPipeline(");
        StringAssert.Contains(libraryCode, "CancellationToken.None,");
        StringAssert.Contains(libraryCode, "ref firstVisibleInteraction);");
        StringAssert.Contains(libraryCode, "EvaluatePendingInstallEstimateRequest(dispatchRequest, evaluationContext, executionPolicy, token)");
        StringAssert.Contains(libraryCode, "executionPolicy.CandidateEvaluationDegree");
        StringAssert.Contains(libraryCode, "BmsLibraryInstallEstimationService.ResolveCandidateEvaluationDegree(asParallel)");
        StringAssert.Contains(libraryCode, "PendingInstallEstimateBatchSource.ManualReestimate => \"manual_reestimate\"");
        StringAssert.Contains(libraryCode, "PendingInstallEstimateBatchSource.ManualReestimate => InstallEstimationProgressSource.ManualReestimate");
        StringAssert.Contains(libraryCode, "ProcessManualPackageEstimateBatch(packageList)");
        StringAssert.Contains(libraryCode, "if (!fixMode && looseEntries.Count == 0 && packageTargets.Count > 1)");
        StringAssert.Contains(installEstimationDoc, "手動の複数 package 推定");
        StringAssert.Contains(installEstimationDoc, "loose chart が混じる手動 chart 群推定");
    }

    private static PlayHistoryRow CreateResolvedPlayHistoryRow(string hash = "cccccccccccccccccccccccccccccccc")
    {
        string sha256 = new string('d', 64);
        BMSFile file = BMSFile.FromSongTableRawValues(
        [
            hash,
            "Resolved Play History",
            "",
            "Artist",
            "",
            "",
            "",
            "C:\\BMS\\play-history-resolved.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]);
        file.ApplySnapshotDigest(hash, sha256);
        PlaylistLibraryResolveIndexSnapshot resolveIndex = PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs([LibraryChartRef.FromBmsFile(file)]);
        PlayHistoryProjectionIndex projectionIndex = PlayHistoryProjectionIndex.Create(
            resolveIndex,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 });
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 2,
                        hash = hash,
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);

        return projected.Rows.Single();
    }

    private static PlayHistoryRow CreateUnresolvedPlayHistoryRow()
    {
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 1,
                        hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            PlayHistoryProjectionIndex.Empty);

        return projected.Rows.Single();
    }

    private static BMSFile CreateContextMenuBmsFile()
    {
        BMSFile file = BMSFile.FromSongTableRawValues(
        [
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "Context Menu BMS",
            "",
            "Artist",
            "",
            "",
            "",
            "C:\\BMS\\context-menu.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]);
        file.ApplySnapshotDigest(
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        return file;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static PlaylistDetailRow CreatePlaylistUrlRow(string url, string diffUrl)
    {
        var row = (PlaylistDetailRow)FormatterServices.GetUninitializedObject(typeof(PlaylistDetailRow));
        SetPrivateField(row, "url", string.IsNullOrWhiteSpace(url) ? null : new Uri(url));
        SetPrivateField(row, "urlDiff", string.IsNullOrWhiteSpace(diffUrl) ? null : new Uri(diffUrl));
        return row;
    }

    private static PlaylistDetailRow CreatePlaylistExternalPackageRow(string? md5, bool isOwned)
    {
        var row = (PlaylistDetailRow)FormatterServices.GetUninitializedObject(typeof(PlaylistDetailRow));
        SetPrivateField(row, "<Entry>k__BackingField", CreatePlaylistEntry(md5));
        SetPrivateField(row, "<IsOwned>k__BackingField", isOwned);
        return row;
    }

    private static PlaylistDetailSourceRow CreatePlaylistExternalPackageSourceRow(string? md5)
    {
        var row = (PlaylistDetailSourceRow)FormatterServices.GetUninitializedObject(typeof(PlaylistDetailSourceRow));
        SetPrivateField(row, "<Entry>k__BackingField", CreatePlaylistEntry(md5));
        return row;
    }

    private static BMSTableEntry CreatePlaylistEntry(string? md5)
    {
        return BMSTableEntry.CreateHydratedPlaylistEntry(
            playlistId: 1,
            md5Value: md5,
            sha256Value: null,
            levelValue: null,
            titleValue: "Playlist Entry",
            artistValue: "Artist",
            folderValue: string.Empty,
            lr2BmsIdValue: string.Empty,
            urlValue: string.Empty,
            urlDiffValue: string.Empty,
            nameDiffValue: string.Empty,
            orgMd5Value: string.Empty,
            addDateValue: null,
            commentValue: string.Empty,
            memoValue: string.Empty,
            isRemovedValue: false);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, fieldName);
        field.SetValue(target, value);
    }

    private static ChartOperationTarget CreateContextMenuTarget(ChartFileKind kind, ChartOperationCapabilities capabilities)
    {
        string path = kind == ChartFileKind.Bmson ? @"C:\Charts\chart.bmson" : @"C:\Charts\chart.bms";
        BMSFile bmsFile = kind == ChartFileKind.Bms ? new BMSFile { path = path } : null!;
        var chart = new ChartFile(
            kind,
            path,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new string('b', 64),
            "Title",
            "Title",
            "Artist",
            string.Empty,
            "Charts",
            string.Empty,
            "7",
            7,
            1,
            null,
            bmsFile,
            kind == ChartFileKind.Bmson ? new LR2SongDBExtended.bmson_song { path = path } : null);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    private static Dictionary<string, string> ReadSettingsCodeDefaults(string settingsCode)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(
            settingsCode,
            @"\[DefaultSettingValue\((?<value>DefaultAppearanceTheme|DefaultTableListUrl|null|""(?<literal>(?:\\.|[^""])*)"")\)\]\s*public\s+\S+\s+(?<name>\w+)\s*\{",
            RegexOptions.Singleline))
        {
            string valueExpression = match.Groups["value"].Value;
            string value = valueExpression switch
            {
                "DefaultAppearanceTheme" => Settings.DefaultAppearanceTheme,
                "DefaultTableListUrl" => Settings.DefaultTableListUrl,
                "null" => "<null>",
                _ => match.Groups["literal"].Value
            };
            defaults[match.Groups["name"].Value] = value;
        }
        return defaults;
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    private static XDocument LoadMainWindowXamlDocument()
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"), LoadOptions.PreserveWhitespace);
    }

    private static XDocument LoadPlaybackPanelXamlDocument()
    {
        return XDocument.Load(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlaybackPanelView.xaml"), LoadOptions.PreserveWhitespace);
    }

    private static XElement FindElementByAttribute(XContainer container, string attributeLocalName, string value)
    {
        List<XElement> matches = FindElementsByAttribute(container, attributeLocalName, value).ToList();
        Assert.AreEqual(1, matches.Count, attributeLocalName + "=" + value);
        return matches[0];
    }

    private static IEnumerable<XElement> FindElementsByAttribute(XContainer container, string attributeLocalName, string value)
    {
        return container.Descendants()
            .Where(element => string.Equals(GetAttributeValue(element, attributeLocalName), value, StringComparison.Ordinal));
    }

    private static XElement DirectChild(XElement parent, string localName)
    {
        List<XElement> matches = parent.Elements()
            .Where(element => element.Name.LocalName == localName)
            .ToList();
        Assert.AreEqual(1, matches.Count, localName);
        return matches[0];
    }

    private static string GetAttributeValue(XElement element, string attributeLocalName)
    {
        return element?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == attributeLocalName)
            ?.Value ?? string.Empty;
    }

    private static double GetNumericAttribute(XElement element, string attributeLocalName)
    {
        string value = GetAttributeValue(element, attributeLocalName);
        Assert.IsTrue(double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number), attributeLocalName + "=" + value);
        return number;
    }

    private static double ResolveRowSeparatorLineHeight(XElement splitterStyle)
    {
        XElement heightSetter = splitterStyle.Descendants()
            .Single(element => element.Name.LocalName == "Setter"
                && GetAttributeValue(element, "TargetName") == "SeparatorLine"
                && GetAttributeValue(element, "Property") == "Height");
        return GetNumericAttribute(heightSetter, "Value");
    }

    private static void AssertStatusBarBinding(XElement statusBar, string path)
    {
        string statusBarText = statusBar.ToString(SaveOptions.DisableFormatting);
        Assert.IsTrue(
            statusBarText.IndexOf("{" + "Binding " + path, StringComparison.Ordinal) >= 0
                || statusBar.Descendants().Any(element => element.Name.LocalName == "Binding"
                    && GetAttributeValue(element, "Path") == path),
            "StatusBar should bind " + path + " through its ProgressHub DataContext.");
    }

    private static void AssertLogPlayHistoryEventContract(string source, string eventName, params string[] requiredFragments)
    {
        StringAssert.Contains(source, "\"" + eventName + "\"");
        StringAssert.Contains(source, "LogPlayHistoryEvent(");
        foreach (string fragment in requiredFragments)
        {
            StringAssert.Contains(source, fragment, eventName + " should include " + fragment);
        }
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "Start marker was not found.");
        int endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, "End marker was not found.");
        return text.Substring(startIndex, endIndex - startIndex);
    }

    private static string ExtractBlockAfter(string text, string marker)
    {
        int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(markerIndex >= 0, "Block marker was not found.");
        int braceIndex = text.IndexOf('{', markerIndex + marker.Length);
        Assert.IsTrue(braceIndex >= 0, "Block start was not found.");
        int depth = 0;
        for (int index = braceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(braceIndex, index - braceIndex + 1);
                }
            }
        }
        Assert.Fail("Block end was not found.");
        return string.Empty;
    }

    private static string ExtractMethodBody(string text, string signature)
    {
        int signatureIndex = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.IsTrue(signatureIndex >= 0, "Method signature was not found.");
        int braceIndex = text.IndexOf('{', signatureIndex);
        Assert.IsTrue(braceIndex >= 0, "Method body start was not found.");
        int depth = 0;
        for (int index = braceIndex; index < text.Length; index++)
        {
            if (text[index] == '{')
            {
                depth++;
            }
            else if (text[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(braceIndex, index - braceIndex + 1);
                }
            }
        }
        Assert.Fail("Method body end was not found.");
        return string.Empty;
    }
}
