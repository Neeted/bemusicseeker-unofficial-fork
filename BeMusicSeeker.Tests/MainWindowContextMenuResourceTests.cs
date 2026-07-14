using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
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
    public void ChartFilterInput_BindsToChildOwnerAndUsesRequestSnapshots()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string viewModel = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        StringAssert.Contains(viewModel, "public ModeFilterType ModeFilter");
        StringAssert.Contains(viewModel, "public string KeywordFilter");
        Assert.IsFalse(viewModel.Contains("private ModeFilterType _ModeFilter"));
        Assert.IsFalse(viewModel.Contains("private string _KeywordFilter"));
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
    public void DeleteContextMenuItems_UseSpecificDeleteResourceKeys()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteEntry\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Remove_playlist_entry, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteFile\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_file, Mode=OneWay"));
    }

    [TestMethod]
    public void PlaylistOverwriteLevel_RoutesThroughWorkspaceMutationOwner()
    {
        string mainWindowSource = SourceTextTestHelper.ReadMainWindowSourceText();
        string rootViewModelSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs");
        string workspaceMutationSource = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.Mutations.cs");
        string route = ExtractBetween(
            mainWindowSource,
            "private void treeViewPlaylistTableContextMenuItemOverwriteLevelClick",
            "private async void treeViewPlaylistTableContextMenuItemRemoveTableClick");

        StringAssert.Contains(route, "viewModel.PlaylistWorkspace.ReplaceBmsFileLevelByTableEntryLevelAsync(bmsTable)");
        Assert.IsFalse(route.Contains("viewModel.ReplaceBMSFileLevelByTableEntryLevel("));
        StringAssert.Contains(workspaceMutationSource, "internal Task ReplaceBmsFileLevelByTableEntryLevelAsync(BMSTable bmsTable)");
        StringAssert.Contains(workspaceMutationSource, "GetPlaylistLibrary().ReplaceBmsFileLevelByTableEntryLevel(bmsTable)");
        Assert.AreEqual(-1, rootViewModelSource.IndexOf("ReplaceBMSFileLevelByTableEntryLevel(", StringComparison.Ordinal));
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

        StringAssert.Contains(mainTable, "DataContext=\"{Binding MainChartList}\"");
        StringAssert.Contains(mainTable, "ItemsSource=\"{Binding Rows, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SelectedIndex=\"{Binding SelectedIndex, Mode=TwoWay}\"");
        StringAssert.Contains(mainTable, "RowDragKind=\"{Binding RowDragKind, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "ColumnsSettings=\"{Binding ColumnsSettings, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SortColumnName=\"{Binding SortParameters.ColumnsName, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SortDirection=\"{Binding SortParameters.Direction, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "Visibility=\"{Binding DataContext.PlaylistWorkspace.IsPlaylistSummaryMode, ElementName=window, Converter={qc:QuickConverter '!$P ? Visibility.Visible : Visibility.Collapsed'}}\"");
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
        Assert.IsFalse(PlayHistoryContextMenuState.TryCreate(playHistoryRow, out PlayHistoryContextMenuState unresolvedState));
        Assert.IsNull(unresolvedState);
        Assert.IsNull(GridRowResolver.GetRepositorySha256(playHistoryRow));
        Assert.IsTrue(PlayHistoryContextMenuState.TryCreate(resolvedPlayHistoryRow, out PlayHistoryContextMenuState resolvedState));
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
        string terminalShellOwnerCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.TerminalShell.cs");
        string playlistDetailTerminalCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlaylistWorkspaceViewModel.DetailTerminal.cs");
        string viewExecutionCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.ViewExecution.cs");
        string presentationStateCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryPresentationState.cs");
        string summaryRow = FindElementByAttribute(mainWindowDocument, "Name", "playHistorySummaryBar")
            .ToString(SaveOptions.DisableFormatting);

        StringAssert.Contains(summaryRow, "Visibility=\"{qc:MultiBinding '($P0 &amp;&amp; !$P1) ? Visibility.Visible : Visibility.Collapsed'");
        StringAssert.Contains(summaryRow, "P0={Binding IsPlayHistoryViewActive}");
        StringAssert.Contains(summaryRow, "P1={Binding PlaylistWorkspace.IsPlaylistSummaryMode}");
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
        StringAssert.Contains(playlistDetailTerminalCode, "CommitPlayHistorySourceClear(");
        StringAssert.Contains(playlistDetailTerminalCode, "PublishPlayHistorySourceClear(");
        StringAssert.Contains(playlistDetailTerminalCode, "LogPlayHistorySourceClear(");
        StringAssert.Contains(terminalShellOwnerCode, "ownershipTransferred: true");
        StringAssert.Contains(terminalShellOwnerCode, "new AggregateException(tablePublishException, shellPublishException)");
        Assert.IsFalse(rootViewModelCode.Contains("CreatePlayHistoryViewDiagnostics"));
        Assert.IsFalse(rootViewModelCode.Contains("CountDistinctPlayHistoryFolderLabels"));
        StringAssert.Contains(rootViewModelCode, "SnapshotPlayHistoryDisplayTargetTables");
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
        StringAssert.Contains(rootViewModelCode, "playHistoryWorkflowOwner.ExecuteViewFromShell(");
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

        StringAssert.Contains(viewModelCode, "playHistoryWorkflowOwner.ExecuteViewFromShell(");
        StringAssert.Contains(viewExecutionCode, "ResolveViewRequest(");
        StringAssert.Contains(viewExecutionCode, "RegisterRequest(");
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
        StringAssert.Contains(providerSelection, "ApplicationSettings.UseBeatorajaScoreDb");
        StringAssert.Contains(providerSelection, "GetActiveScoreSourceForDiagnostics() == ActiveScoreSource.Beatoraja");
        Assert.IsFalse(providerSelection.Contains("GetScoreSnapshotForDiagnostics"));
        StringAssert.Contains(rowCode, "safeIndex.ResolveChartByMd5(string.Empty, sha256)");
        StringAssert.Contains(rowCode, "safeIndex.ResolvePlaylistReference(resolvedMd5, sha256)");
        StringAssert.Contains(rowCode, "FormatBeatorajaOption");
    }

    [TestMethod]
    public void PlayHistoryView_KeywordFilterUpdatedReusesProjectedState()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string viewExecutionCode = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.ViewExecution.cs");
        string refreshTargets = ExtractBetween(viewModelCode, "private void RefreshPlayHistoryDisplayTargets", "internal void ReplacePlayHistoryDisplayTargetSetsForTest");
        string displayTargetRefreshOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargetRefresh.cs");
        string flushPendingUiRefresh = ExtractBetween(viewModelCode, "private void FlushPendingUiRefresh", "private void ScheduleDeferredPlaylistReferenceApply");
        string playlistTablesHandler = ExtractBetween(viewModelCode, "listenerForBMSPlaylist.RegisterHandler(() => tables.BMSTables", "listenerForBMSPlaylistBMSTablesCollection.RegisterHandler");
        string playlistTablesCollectionHandler = ExtractBetween(viewModelCode, "listenerForBMSPlaylistBMSTablesCollection.RegisterHandler", "listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationCompletedVersion");
        string playlistEntriesHydrationHandler = ExtractBetween(viewModelCode, "listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationCompletedVersion", "listenerForBMSPlaylist.RegisterHandler(() => tables.IsWriteLockHeldBMSTables");
        string state = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryPresentationState.cs");
        string workflowOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.cs");
        string displayTargetOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargets.cs");

        StringAssert.Contains(viewModelCode, "playHistoryWorkflowOwner.ExecuteViewFromShell(");
        Assert.IsFalse(viewModelCode.Contains("private void ApplyPlayHistoryView("));
        StringAssert.Contains(viewExecutionCode, "request.RequestedMode == MainViewUpdateMode.SortUpdated");
        Assert.IsFalse(viewExecutionCode.Contains("playHistoryWorkflowOwner.ApplyDisplayTarget("));
        Assert.IsFalse(viewExecutionCode.Contains("playHistoryWorkflowOwner.ApplyKeywordFilters("));
        Assert.IsFalse(viewExecutionCode.Contains("PlayHistorySortEngine.TrySort("));
        StringAssert.Contains(viewModelCode, "mode == MainViewUpdateMode.KeywordFilterUpdated && parameter is PlayHistoryViewRequest playHistoryKeywordRequest");
        StringAssert.Contains(viewModelCode, "playHistoryWorkflowOwner.ExecuteViewFromShell(");
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
        StringAssert.Contains(refreshTargets, "PlayHistory.ReplaceDisplayTargetCatalog(");
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
        StringAssert.Contains(flushPendingUiRefresh, "RefreshPlayHistoryDisplayTargets();");
        StringAssert.Contains(playlistTablesHandler, "PlayHistory.QueueDisplayTargetCatalogRefresh();");
        StringAssert.Contains(playlistTablesCollectionHandler, "PlayHistory.QueueDisplayTargetCatalogRefresh();");
        Assert.IsFalse(playlistTablesHandler.Contains("RefreshPlayHistoryDisplayTargets();"));
        Assert.IsFalse(playlistTablesCollectionHandler.Contains("RefreshPlayHistoryDisplayTargets();"));
        StringAssert.Contains(playlistEntriesHydrationHandler, "if (PlayHistory.SelectedDisplayTarget.UsesProjection)");
        StringAssert.Contains(playlistEntriesHydrationHandler, "playHistoryWorkflowOwner.QueueDisplayTargetRefresh(");
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
        string settingDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string editDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlayHistoryFolderDisplayPresetEditDialog.xaml"));
        string editDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlayHistoryFolderDisplayPresetEditDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string displayTargetOwner = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "ViewModels", "MainWindow", "PlayHistoryWorkflowOwner.DisplayTargets.cs");
        string toolbar = FindElementByAttribute(mainWindowDocument, "Name", "mainTableToolbar")
            .ToString(SaveOptions.DisableFormatting);
        string saveAndClose = ExtractBetween(settingDialogCode, "private async void SaveAndClose", "internal static bool ShouldResetSettingsOnCancel");
        string saveSettings = ExtractBetween(viewModelCode, "public async Task SaveSettings()", "public async Task SaveSettingsForInitialInitialize()");
        string saveSettingsCore = ExtractBetween(viewModelCode, "private async Task SaveSettingsCore", "public void SaveOperationModeForRestart");
        string beginPlayHistoryFilterRequest = ExtractBetween(viewModelCode, "internal long BeginPlayHistoryFilterRequest", "private static PlayHistoryDiagnostic CreatePlayHistoryDiagnostic");

        StringAssert.Contains(toolbar, "Visibility=\"{Binding IsPlayHistoryViewActive");
        StringAssert.Contains(toolbar, "ItemsSource=\"{Binding PlayHistory.DisplayTargets}\"");
        StringAssert.Contains(toolbar, "SelectedValue=\"{Binding PlayHistory.SelectedDisplayTargetIdentity, Mode=TwoWay}\"");
        StringAssert.Contains(toolbar, "SelectedValuePath=\"Identity\"");
        StringAssert.Contains(toolbar, "DisplayMemberPath=\"DisplayName\"");
        StringAssert.Contains(displayTargetOwner, "public string SelectedDisplayTargetIdentity");
        StringAssert.Contains(displayTargetOwner, "isRefreshingDisplayTargets");
        StringAssert.Contains(beginPlayHistoryFilterRequest, "EnsurePlayHistoryDisplayTargetSelection();");
        StringAssert.Contains(displayTargetOwner, "nextItems.AddRange(displayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSet));");
        StringAssert.Contains(displayTargetOwner, "nextItems.AddRange(displayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly));");
        StringAssert.Contains(displayTargetOwner, ".Select(PlayHistoryDisplayTargetItem.FromPlaylist));");
        StringAssert.Contains(viewModelCode, "playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity");
        StringAssert.Contains(displayTargetOwner, "preferredDisplayTargetIdentity");
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Play_history_display_target_folder_only_set_format));
        StringAssert.Contains(settingDialogXaml, "Path=Resources.Play_history_folder_display_preset, Mode=OneWay");
        StringAssert.Contains(settingDialogXaml, "ItemsSource=\"{Binding settingDialog.PlayHistoryFolderDisplayPresets}\"");
        Assert.IsFalse(settingDialogXaml.Contains("ItemsSource=\"{Binding settingDialog.PlayHistoryFolderDisplayPresetPlaylistOptions}\""));
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
        StringAssert.Contains(saveAndClose, "await settingDialogViewModel.SaveSettings();");
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
            rowUrl: null!,
            rowUrlDiff: null!,
            isPendingSelected: true,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: []));

        Assert.IsTrue(state.IsInstallListSelected);
        Assert.IsFalse(state.IsPlaylistContext);
        Assert.AreSame(rowTarget, state.RowTarget);
        Assert.AreEqual(rowTarget.Chart.Path, state.ChartPath);
        Assert.AreEqual(1, state.SelectedTargets.Count);
        Assert.AreSame(rowTarget, state.SelectedTargets[0]);
        Assert.IsFalse(state.IsBmsonContextRow);
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
            rowUrl: new Uri("https://example.test/main"),
            rowUrlDiff: new Uri("https://example.test/diff"),
            isPendingSelected: false,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: [selectedTarget]));

        Assert.IsFalse(state.IsInstallListSelected);
        Assert.IsTrue(state.IsPlaylistContext);
        Assert.AreEqual(new Uri("https://example.test/main"), state.RowUrl);
        Assert.AreEqual(new Uri("https://example.test/diff"), state.RowUrlDiff);
        Assert.AreEqual(1, state.SelectedTargets.Count);
        Assert.AreSame(selectedTarget, state.SelectedTargets[0]);
        Assert.IsFalse(state.IsBmsonContextRow);
        Assert.IsTrue(state.HasResourceHealthTarget);
        Assert.IsFalse(state.CanShowResourceHealthMenu);
        Assert.IsTrue(state.HasBmsonSelection);
        Assert.IsFalse(state.HasBmsSelection);
        Assert.IsFalse(state.CanAutoRenameFolders);
        Assert.IsFalse(state.CanFixEncoding);
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
            rowUrl: null!,
            rowUrlDiff: null!,
            isPendingSelected: false,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: [rowTarget]));

        Assert.IsTrue(state.HasScoreViewerTarget);
        Assert.IsTrue(state.HasRankingTarget);
        Assert.IsTrue(state.HasResourceHealthTarget);
        Assert.IsTrue(state.CanOpenLr2Ir);
        Assert.IsTrue(state.CanOpenInstallDestination);
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
    public void DeleteInstallPackageRecordsRequest_RequiresTargetsAndPreservesKind()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        DeleteInstallPackageRecordsRequest pending = DeleteInstallPackageRecordsRequest.CreatePending([target]);
        DeleteInstallPackageRecordsRequest installed = DeleteInstallPackageRecordsRequest.CreateInstalled([target]);

        Assert.AreEqual(DeleteInstallPackageRecordsKind.Pending, pending.Kind);
        Assert.IsTrue(pending.IsPending);
        Assert.IsFalse(pending.IsInstalled);
        Assert.AreEqual(1, pending.SelectedRowCount);
        Assert.AreSame(target, pending.Targets[0]);
        Assert.AreEqual(DeleteInstallPackageRecordsKind.Installed, installed.Kind);
        Assert.IsFalse(installed.IsPending);
        Assert.IsTrue(installed.IsInstalled);
        Assert.ThrowsException<ArgumentException>(() => DeleteInstallPackageRecordsRequest.CreatePending([]));
        Assert.ThrowsException<ArgumentException>(() => DeleteInstallPackageRecordsRequest.CreateInstalled([null!]));
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
        Assert.IsFalse(method.Contains("selectedTargets.Add(rowTarget)"));
    }

    [TestMethod]
    public void LibraryFolderContextMenus_ExposeLightReloadAndFullReinitialize()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "MethodName=\"ReloadFileDiff\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "MethodName=\"ReinitializeLibrary\""));
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
        string codeBehind = SourceTextTestHelper.ReadProductionSourceText("BeMusicSeeker", "Views", "PlaybackPanelView.xaml.cs");
        XDocument playbackDocument = LoadPlaybackPanelXamlDocument();
        XDocument mainWindowDocument = LoadMainWindowXamlDocument();
        XElement playbackRoot = playbackDocument.Root;
        XElement mainGrid = FindElementByAttribute(mainWindowDocument, "Name", "grid");
        XElement playbackRow = DirectChild(mainGrid, "Grid.RowDefinitions").Elements().First();
        XElement statusBar = FindElementByAttribute(mainWindowDocument, "Name", "progressStatusBar");

        StringAssert.Contains(mainWindowXaml, "DataContext=\"{Binding PlaybackPanel}\"");
        StringAssert.Contains(mainWindowXaml, "BrowserHtml=\"{Binding DataContext.BrowserHtml, ElementName=window}\"");
        StringAssert.Contains(mainWindowXaml, "PlaybackStarting=\"playbackPanelViewPlaybackStarting\"");
        StringAssert.Contains(mainWindowXaml, "PlaybackStarted=\"playbackPanelViewPlaybackStarted\"");
        Assert.IsFalse(mainWindowCode.Contains("PlaybackPanel.PlaybackStarting +="));
        Assert.IsFalse(mainWindowCode.Contains("PlaybackPanel.PlaybackStarted +="));
        Assert.IsFalse(mainWindowCode.Contains("viewModel.PlaybackPanel.Start()"));
        Assert.IsFalse(mainWindowCode.Contains("GridRowResolver.TryGetBmsPlayerFile(e.Row"));
        Assert.IsFalse(mainWindowCode.Contains("viewModel.PlaybackPanel.SetBmsPlayerHeader"));
        StringAssert.Contains(mainWindowCode, "viewModel.PlaybackPanel.HandleTableSelection(e.SelectedRow)");
        StringAssert.Contains(mainWindowCode, "viewModel.PlaybackPanel.HandleTableRowActivation(e.RowIndex, e.Row)");
        Assert.AreEqual("{qc:MultiBinding '$P1 == Visibility.Visible ? $P0 + $P2 : $P0', P0={Binding ActualHeight, ElementName=playbackPanelView}, P1={Binding Visibility, ElementName=progressStatusBar}, P2={Binding Height, ElementName=progressStatusBar}}", GetAttributeValue(mainWindowDocument.Root, "MinHeight"));
        Assert.AreEqual("Auto", GetAttributeValue(playbackRow, "Height"));
        Assert.AreEqual("{Binding ActualHeight, ElementName=playbackPanelView}", GetAttributeValue(playbackRow, "MinHeight"));
        Assert.AreEqual("Top", GetAttributeValue(playbackRoot, "VerticalAlignment"));
        Assert.AreEqual("286", GetAttributeValue(playbackRoot, "Height"));
        XElement playbackGrid = FindElementByAttribute(playbackDocument, "Name", "gridBMSPlayer");
        XElement playbackRows = DirectChild(playbackGrid, "Grid.RowDefinitions");
        CollectionAssert.AreEqual(new[] { "*", "30" }, playbackRows.Elements().Select(row => GetAttributeValue(row, "Height")).ToArray());
        XElement playbackBody = FindElementByAttribute(playbackDocument, "Name", "gridBMSPlayerBody");
        Assert.AreEqual("0", GetAttributeValue(playbackBody, "Grid.Row"));
        Assert.AreEqual(string.Empty, GetAttributeValue(playbackBody, "Height"));
        Assert.AreEqual("1", GetAttributeValue(FindElementByAttribute(playbackDocument, "Name", "gridBMSPlayerControls"), "Grid.Row"));
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

        Assert.AreEqual("{Binding ProgressHub}", GetAttributeValue(statusBar, "DataContext"));
        AssertStatusBarBinding(statusBar, "IsStartupProgressActive");
        AssertStatusBarBinding(statusBar, "StartupProgressLabel");
        AssertStatusBarBinding(statusBar, "StartupProgressSubLabel");
        AssertStatusBarBinding(statusBar, "StartupProgressMaximum");
        AssertStatusBarBinding(statusBar, "StartupProgressValue");
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
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusProgressMaximum");
        AssertStatusBarBinding(statusBar, "Lr2SongDbSyncStatusProgressValue");

        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cancelDropInstallQueueClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cancelMaintenanceRescanClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"retryLr2SongDbSyncClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cancelLr2SongDbSyncClick\""));
        Assert.AreEqual(1, CountOccurrences(statusBar.ToString(SaveOptions.DisableFormatting), "Click=\"cleanupLr2SongDbSyncStartupScanBlockersClick\""));
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
        StringAssert.Contains(code, "viewModel.PlaylistWorkspace.EnqueueExternalPlaylistBMSTableImport(dataContext.url);");
        StringAssert.Contains(code, "viewModel.PlaylistWorkspace.EnqueueExternalPlaylistBMSTableImport(uri);");
        StringAssert.Contains(dialogCode, "viewModel.PlaylistWorkspace.EnqueueExternalPlaylistBMSTableImports(parseResult.ValidUris);");
        StringAssert.Contains(dialogCode, "ParsePlaylistUriInput(textBoxURIInput.Text)");
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding settingDialog.BeatorajaBmtHashOutputModeOptions}\"");
        StringAssert.Contains(xaml, "SelectedValue=\"{Binding settingDialog.BeatorajaBmtHashOutputMode, Mode=TwoWay}\"");
        StringAssert.Contains(xaml, "Path=Resources.Keep_beatoraja_bmt_files_when_output_disabled");
        StringAssert.Contains(xaml, "Path=Resources.Register_beatoraja_bmt_urls");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidBeatorajaRootPath");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidBeatorajaScoreDbPath");
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        StringAssert.Contains(settingDialogCode, "ReloadScoresOnly()");
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
        StringAssert.Contains(playlistTab, "IsChecked=\"{Binding settingDialog.EnableStellaFullPlaylistUrlCompletion}\"");
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding settingDialog.AvailableBMSDirectories}\"");
        StringAssert.Contains(xaml, "SelectedItem=\"{Binding settingDialog.SelectedBmsSearchRootPath, Mode=TwoWay}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding settingDialog.RemoveDirCommand}\"");
        Assert.IsFalse(xaml.Contains("ToolTip=\"未実装\""));
        StringAssert.Contains(viewModelCode, "ApplicationSettings.StandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidStandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "ownerViewModel.files?.HasOwnedChartUnderRealPath(dir) == true");
        Assert.IsFalse(viewModelCode.Contains("ownerViewModel.BMSFiles != null && ownerViewModel.BMSFiles.Any"));
    }

    [TestMethod]
    public void StandaloneLibraryMode_UsesPortableSongDbAndBuildsPlaylist()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string compositionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        string portablePathCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "PortableSettingsPath.cs"));
        string standaloneDbCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "StandaloneLibraryDatabase.cs"));

        StringAssert.Contains(portablePathCode, "DataDirectoryPath => Path.Combine(AppBaseDirectory, \"data\")");
        StringAssert.Contains(portablePathCode, "StandaloneSongDbPath => Path.Combine(DataDirectoryPath, \"song.db\")");
        StringAssert.Contains(standaloneDbCode, "FileMode.OpenOrCreate");
        StringAssert.Contains(standaloneDbCode, "BMSPlaylist.EnsureSchema(songDbPath)");
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
            "if (shouldReloadExternalPlaylist)");
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
        StringAssert.Contains(SourceTextTestHelper.ReadBmsLibrarySourceText(), "public List<string> SearchTargets { get; set; } = [];");
        Assert.IsFalse(viewModelCode.Contains("throw new NotImplementedException();"));
    }

    [TestMethod]
    public void Lr2PlaybackPlayer_IsIndependentFromLibraryOperationMode()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string compositionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string lr2PlaybackXaml = ExtractBetween(
            xaml,
            "Name=\"radioButtonPlayLR2body\"",
            "Path=Resources.Movie_playback");
        string checkValidation = ExtractBetween(
            viewModelCode,
            "public bool CheckValidation(out string errMsg)",
            "public async Task SaveSettings()");
        string initialize = ExtractBetween(
            viewModelCode,
            "public async void Initialize()",
            "listenerForBMSLibrary = new PropertyChangedEventListener(files);");
        string saveFollowup = ExtractBetween(
            viewModelCode,
            "private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)",
            "public bool CheckValidation()");
        string lr2RootPathProperty = ExtractBetween(
            viewModelCode,
            "public string LR2RootPath",
            "public Dictionary<string, Point> LR2bodyResolutions");
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
        StringAssert.Contains(compositionCode, "new LR2body(startupSettings.LR2bodyPath, createLr2PlayerConfig())");
        Assert.IsFalse(initialize.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(saveFollowup, "ownerViewModel.applicationComposition.CreateBmsPlayerForSettings(ApplicationSettings)");
        StringAssert.Contains(saveFollowup, "ownerViewModel.PlaybackPanel.ReplacePlayer(replacementPlayer);");
        Assert.IsFalse(saveFollowup.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(viewModelCode, "private LR2Config CreateLR2PlayerConfig(StartupSettingsSnapshot startupSettings)");
        StringAssert.Contains(viewModelCode, "private bool IsLR2PlayerRootPathValid(string value)");
        Assert.IsFalse(lr2RootPathGetter.Contains("Settings.Default.LR2RootPath = null"));
        StringAssert.Contains(lr2RootPathGetter, "return ApplicationSettings.LR2RootPath;");
    }

    [TestMethod]
    public void SettingDialogOperationModeChange_ConfirmsAndRestartsAfterInitialization()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string operationModeProperty = ExtractBetween(
            viewModelCode,
            "public bool OperationModeLR2DB",
            "public bool CanUseLr2Features");
        string restartMethod = ExtractBetween(
            viewModelCode,
            "private void ConfirmAndRestartForOperationModeChange(bool value)",
            "public string LR2bodyPath");

        StringAssert.Contains(operationModeProperty, "return operationModeLR2DB;");
        StringAssert.Contains(operationModeProperty, "if (!ownerViewModel.HasActiveLibraryProfile)");
        StringAssert.Contains(operationModeProperty, "SetOperationModeSelection(value);");
        StringAssert.Contains(operationModeProperty, "return;");
        StringAssert.Contains(operationModeProperty, "ConfirmAndRestartForOperationModeChange(value);");
        Assert.IsFalse(operationModeProperty.Contains("Settings.Default.OperationModeLR2DB = value;"));
        StringAssert.Contains(viewModelCode, "public bool HasActiveLibraryProfile => hasActiveLibraryProfile;");
        StringAssert.Contains(viewModelCode, "hasActiveLibraryProfile = true;");
        StringAssert.Contains(restartMethod, "Resources.Confirm_RestartForOperationModeChange");
        StringAssert.Contains(restartMethod, "SaveOperationModeForRestart(value);");
        StringAssert.Contains(restartMethod, "RestartApplication()");
        Assert.IsFalse(restartMethod.Contains("CheckValidation("));
        Assert.IsFalse(restartMethod.Contains("ReloadFileDiff()"));
        Assert.IsFalse(restartMethod.Contains("ReloadScoresOnly()"));
    }

    [TestMethod]
    public void SettingDialogSaveAndClose_DoesNotHandleOperationModeRestart()
    {
        string root = FindRepositoryRoot();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string appCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "App.cs"));
        string saveAndClose = ExtractBetween(
            settingDialogCode,
            "private async void SaveAndClose",
            "private async void detailTabItemBackupButtonClicked");

        Assert.IsFalse(saveAndClose.Contains("Resources.Confirm_RestartForOperationModeChange"));
        Assert.IsFalse(saveAndClose.Contains("SaveSettingsForRestart"));
        StringAssert.Contains(saveAndClose, "try");
        StringAssert.Contains(saveAndClose, "finally");
        StringAssert.Contains(saveAndClose, "shouldInitializeAfterSave = !viewModel.HasActiveLibraryProfile;");
        StringAssert.Contains(saveAndClose, "LogSettingsDialogPerformance(");
        StringAssert.Contains(saveAndClose, "MainWindowViewModel.SettingDialogViewModel.RestartMode.None");
        StringAssert.Contains(saveAndClose, "if (shouldInitializeAfterSave)");
        StringAssert.Contains(saveAndClose, "SaveSettingsForInitialInitialize()");
        StringAssert.Contains(saveAndClose, "HideThisOverlay();");
        StringAssert.Contains(saveAndClose, "Msg_initsetting_completed");
        Assert.IsTrue(saveAndClose.IndexOf("Msg_initsetting_completed", StringComparison.Ordinal) < saveAndClose.IndexOf("viewModel.Initialize();", StringComparison.Ordinal));
        StringAssert.Contains(saveAndClose, "settingDialogViewModel.CheckValidation(out errMsg)");
        StringAssert.Contains(saveAndClose, "settingDialogViewModel.CheckValidationBeforeSave(out errMsg)");
        StringAssert.Contains(saveAndClose, "settingDialogRootGrid.IsEnabled = true;");
        Assert.IsTrue(saveAndClose.IndexOf("settingDialogRootGrid.IsEnabled = true;", StringComparison.Ordinal) < saveAndClose.IndexOf("\"settings_save_and_close\"", StringComparison.Ordinal));
        StringAssert.Contains(saveAndClose, "viewModel.IsLibraryOperationInProgress");
        StringAssert.Contains(saveAndClose, "Resources.Msg_settings_apply_blocked_during_initialization");
        StringAssert.Contains(saveAndClose, "settingDialogViewModel.ResetSettings();");
        StringAssert.Contains(saveAndClose, "SyncAppearanceThemeSelection(settingDialogViewModel);");
        StringAssert.Contains(saveAndClose, "Msg_invalid_setting");
        StringAssert.Contains(saveAndClose, "Msg_error_unexpected");
        Assert.IsTrue(saveAndClose.IndexOf("viewModel.IsLibraryOperationInProgress", StringComparison.Ordinal) < saveAndClose.IndexOf("SaveSettingsForInitialInitialize()", StringComparison.Ordinal));
        Assert.IsFalse(settingDialogCode.Contains("firstStartupInitializationStarted"));
        StringAssert.Contains(appCode, "public void RestartApplication()");
        StringAssert.Contains(appCode, "ReleaseSingleInstanceMutex();");
        StringAssert.Contains(appCode, "Environment.GetCommandLineArgs().Skip(1)");
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
            "public async void Initialize()",
            "public void CloseProcess()");
        string validationFailure = ExtractBetween(
            initialize,
            "if (!settingDialog.CheckValidation(out string startupValidationErrorMessage))",
            "if (startupSettings.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync(startupSettings))");

        Assert.IsFalse(validationFailure.Contains("DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings,"));
        StringAssert.Contains(validationFailure, "RaiseInitialSetupLanguageDialogRequested();");
        StringAssert.Contains(validationFailure, "ShowUiMessage(BeMusicSeeker.Properties.Resources.Msg_init_settings_check");
        StringAssert.Contains(validationFailure, "RaiseInitializationExceptionRequested();");
        Assert.IsTrue(validationFailure.IndexOf("RaiseInitialSetupLanguageDialogRequested", StringComparison.Ordinal) < validationFailure.IndexOf("Msg_init_settings_check", StringComparison.Ordinal));

        Assert.IsFalse(mainWindow.Contains("MessageKey=\"InitialSetupLanguageDialog\""));
        StringAssert.Contains(mainWindowCode, "InitialSetupLanguageDialogRequested += MainWindowViewModel_InitialSetupLanguageDialogRequested;");
        StringAssert.Contains(mainWindowCode, "ShowInitialSetupLanguageDialogOverlay();");
        StringAssert.Contains(mainWindow, "<v:InitialSetupLanguageDialog x:Name=\"initialSetupLanguageDialog\"");
        StringAssert.Contains(initialDialog, "ItemsSource=\"{Binding settingDialog.Languages, Mode=OneWay}\"");
        StringAssert.Contains(initialDialog, "SelectedItem=\"{Binding Path=settingDialog.Language}\"");
        StringAssert.Contains(initialDialog, "Resources.Msg_init_settings");
        StringAssert.Contains(initialDialog, "Resources.InitialSetupLanguageDialogTitle");
        StringAssert.Contains(initialDialog, "Resources.InitialSetupLanguageDialogContinue");
        StringAssert.Contains(initialDialogCode, "mainWindow.HideOverlayDialog(this);");
        StringAssert.Contains(initialDialogCode, "mainWindow.ShowSettingDialogOverlay();");
    }

    [TestMethod]
    public void MainWindowViewModel_RaisesUiInteractionsThroughTypedEvents()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string helperBody = ExtractMethodBody(viewModelCode, "private void RaiseUiInteractionOnUiThread(EventHandler handler, string interactionName)");

        StringAssert.Contains(helperBody, "handler(this, EventArgs.Empty);");
        StringAssert.Contains(helperBody, "dispatcher.Invoke(DispatcherPriority.Normal");
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
        string compositionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "ApplicationComposition.cs"));
        string reloadFileDiff = ExtractBetween(
            viewModelCode,
            "public async void ReloadFileDiff()",
            "public async void ReinitializeLibrary()");
        string initialize = ExtractBetween(
            viewModelCode,
            "public async void Initialize()",
            "public void CloseProcess()");
        string endSuppression = ExtractBetween(
            viewModelCode,
            "private void EndUiUpdateSuppression()",
            "private bool QueueStartupBackgroundTask");
        string deferredExternalSync = ExtractBetween(
            viewModelCode,
            "private void StartDeferredExternalPlaylistSync(string reason, bool fromReloadTables, Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction, long operationToken)",
            "public async void Initialize()");

        StringAssert.Contains(viewModelCode, "public bool IsLibraryOperationInProgress");
        StringAssert.Contains(viewModelCode, "private long GetActiveStartupProgressOperationToken()");
        StringAssert.Contains(viewModelCode, "private bool IsStartupProgressOperationTokenCurrent(long operationToken)");
        StringAssert.Contains(viewModelCode, "playlistSyncProgressUiVersion");
        StringAssert.Contains(viewModelCode, "if (uiVersion != Interlocked.Read(ref playlistSyncProgressUiVersion))");
        Assert.IsTrue(reloadFileDiff.IndexOf("await _semaphore.WaitAsync();", StringComparison.Ordinal) < reloadFileDiff.IndexOf("StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff)", StringComparison.Ordinal));
        Assert.IsTrue(initialize.IndexOf("applicationComposition.CreateBmsLibrary(libraryProfile)", StringComparison.Ordinal) < initialize.IndexOf("StartStartupProgressOperation(StartupProgressOperationKind.Startup)", StringComparison.Ordinal));
        StringAssert.Contains(initialize, "StartDeferredExternalPlaylistSync(\"Initialize\", fromReloadTables: false, CreatePlaylistReferenceReplaceUpdateCallback(), operationToken)");
        StringAssert.Contains(initialize, "queueBeatorajaBmtExportAfterHydration: startupSettings.SkipInitPlaylistLoad");
        StringAssert.Contains(initialize, "BMSPlaylist.GetBMSTableInfo(startupSettings.TableListURL)");
        StringAssert.Contains(initialize, "() => files.CreateBeatorajaBmtSongHashResolver());");
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
        StringAssert.Contains(deferredExternalSync, "tables.QueueBeatorajaBmtExportAll(\"DeferredExternalSync:\" + reason)");
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
        StringAssert.Contains(viewModelCode, "private void ShowInitialSetupCompletionMessageIfPending()");
        StringAssert.Contains(viewModelCode, "ShowInitialSetupCompletionMessageIfPending();");
        StringAssert.Contains(endSuppression, "FlushPendingUiRefresh(uiRefreshChannel, operationToken)");
        StringAssert.Contains(viewModelCode, "ScheduleDeferredPlaylistReferenceApply(\"DeferredExternalSync:\" + reason, operationToken)");
    }

    [TestMethod]
    public void SettingDialogOperationModeRestartSave_SavesOnlyOperationMode()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        StringAssert.Contains(restartSaveMethod, "reloadSettings();");
        StringAssert.Contains(restartSaveMethod, "ApplicationSettings.OperationModeLR2DB = operationMode;");
        StringAssert.Contains(restartSaveMethod, "string playHistorySelectedDisplayTargetIdentity = playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity;");
        StringAssert.Contains(restartSaveMethod, "playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity = playHistorySelectedDisplayTargetIdentity;");
        StringAssert.Contains(restartSaveMethod, "saveSettings();");
        Assert.IsTrue(
            restartSaveMethod.IndexOf("string playHistorySelectedDisplayTargetIdentity = playHistoryDisplaySettingsStore.SelectedDisplayTargetIdentity;", StringComparison.Ordinal)
            < restartSaveMethod.IndexOf("reloadSettings();", StringComparison.Ordinal),
            "Restart save must preserve the in-memory play-history display target before reloading settings.");
        Assert.IsTrue(
            restartSaveMethod.IndexOf("reloadSettings();", StringComparison.Ordinal)
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string playerDriverProperty = ExtractBetween(
            viewModelCode,
            "public int PlayerDriverIndex",
            "private static BassAudioPlayer.DeviceDriver NormalizePlayerDriver");
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
            "public SettingDialogViewModel(MainWindowViewModel owner)");

        StringAssert.Contains(xaml, "IsChecked=\"{Binding settingDialog.UseInternalPlayer}\"");
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
        StringAssert.Contains(languageProperty, "App.AvailableCultures.TryGetValue");
        Assert.IsFalse(languageProperty.Contains("catch"));
    }

    [TestMethod]
    public void SettingDialogValidation_RequiresInstallDestinationInAllOperationModes()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string checkValidation = ExtractBetween(
            viewModelCode,
            "public bool CheckValidation(out string errMsg)",
            "public async Task SaveSettings()");

        Assert.IsFalse(xaml.Contains("IsEnabled=\"{Binding settingDialog.CanSaveSettings"));
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string backupSavedSettings = ExtractBetween(
            viewModelCode,
            "private void backupSavedSettingsCore(SettingsSnapshotRefreshScope scope)",
            "private async Task necessaryStepsAfterSaved(SettingsPostSaveImpact impact)");
        string restartDecision = ExtractBetween(
            viewModelCode,
            "public RestartMode IsNeedRestartForSaved()",
            "public RestartMode IsNeedRestartForSaveOrCancel()");
        string pendingDecision = ExtractBetween(
            viewModelCode,
            "internal bool HasPendingSettingChanges()",
            "private bool HasSettingValueChanges()");
        string saveCore = ExtractBetween(
            viewModelCode,
            "private async Task SaveSettingsCore(bool runPostSaveActions)",
            "public void SaveOperationModeForRestart");
        string saveOrCancelDecision = ExtractMethodBody(
            viewModelCode,
            "public RestartMode IsNeedRestartForSaveOrCancel()");

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
        StringAssert.Contains(saveOrCancelDecision, "return RestartMode.None;");
    }

    [TestMethod]
    public void SearchRootChanges_UpdateRuntimeSearchTargetsBeforeFileDiffReload()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string settingDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string playlistCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string runtimeSync = ExtractBetween(
            viewModelCode,
            "private void ApplyRuntimeSearchRootsForCurrentMode()",
            "private bool IsLR2SongDBPathValid()");
        string rootAdd = ExtractBetween(
            viewModelCode,
            "public void AddBmsSearchRootPathFromMainWindowPicker",
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
            viewModelCode,
            "public async void ReloadFileDiff()",
            "public async void ReinitializeLibrary()");
        string manualResyncClickHandler = ExtractBetween(
            settingDialogCode,
            "private async void resyncLr2SongDbSyncDataButtonClicked",
            "private async void detailTabItemBackupButtonClicked");
        string manualResyncPlaylistMethod = ExtractBetween(
            playlistCode,
            "private async Task<Lr2SongDbSyncPreparedDataSurface> ReOutputAllCustomFoldersForLr2SongDbSyncCoreAsync",
            "private sealed class CustomFolderOutputProjection");

        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = [.. lr2config.GetBMSSearchDirectories()];");
        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = [.. GetStandaloneBmsRootPathsForCurrentSession()];");
        StringAssert.Contains(rootAdd, "ApplyRuntimeSearchRootsForCurrentMode();");
        Assert.IsTrue(rootAdd.IndexOf("ApplyRuntimeSearchRootsForCurrentMode();", StringComparison.Ordinal) < rootAdd.IndexOf("ownerViewModel.ReloadFileDiff();", StringComparison.Ordinal));
        StringAssert.Contains(saveCore, "ApplyRuntimeSearchRootsForCurrentMode();");
        StringAssert.Contains(viewModelCode, "SyncLr2SongDbSyncFolderDataAfterSettingsChange(\"SettingDialog.SaveSettings\")");
        StringAssert.Contains(viewModelCode, "private Lr2SongDbSyncPreparedDataSurface ReOutputAllCustomFoldersForLr2GeneratedDataSync(string reason)");
        StringAssert.Contains(viewModelCode, "tables?.ReOutputAllCustomFoldersForLr2SongDbSync(");
        StringAssert.Contains(viewModelCode, "files?.SyncLr2BuiltinCustomFolderRows(reason) ?? Lr2SongDbSyncPreparedDataSurface.Empty;");
        StringAssert.Contains(viewModelCode, "Lr2SongDbSyncPreparedDataSurface.Merge(playlistSurface, builtinSurface);");
        StringAssert.Contains(viewModelCode, "files?.QueueLr2SongDbSync(reason, force: false, allowIncompleteToQueue: false);");
        StringAssert.Contains(viewModelCode, "public async Task RequestLr2SongDbSyncAsync(string reason, bool force)");
        StringAssert.Contains(viewModelCode, "files?.QueueLr2SongDbSync(");
        StringAssert.Contains(viewModelCode, "() => ReOutputAllCustomFoldersForLr2GeneratedDataSync(reason)");
        StringAssert.Contains(viewModelCode, "files?.TryRunLr2SongDbSyncDataPreparation(");
        StringAssert.Contains(viewModelCode, "files?.PublishLr2SongDbSyncExternalStageProgress(");
        StringAssert.Contains(viewModelCode, "public bool CanRequestLr2SongDbSyncDataResync => HasActiveLibraryProfile");
        StringAssert.Contains(viewModelCode, "&& settingDialog?.OperationModeLR2DB == true");
        StringAssert.Contains(viewModelCode, "&& !IsLibraryOperationInProgress;");
        StringAssert.Contains(settingDialogXaml, "<Grid Margin=\"20,2,10,4\" IsEnabled=\"{Binding IsChecked, ElementName=radioButtonUseLR2}\">");
        StringAssert.Contains(settingDialogXaml, "HorizontalAlignment=\"Center\"");
        StringAssert.Contains(settingDialogXaml, "IsEnabled=\"{Binding CanRequestLr2SongDbSyncDataResync, Mode=OneWay}\"");
        StringAssert.Contains(manualResyncClickHandler, "if (!viewModel.CanRequestLr2SongDbSyncDataResync)");
        StringAssert.Contains(settingDialogCode, "await viewModel.RequestLr2SongDbSyncAsync(\"setting_dialog_manual_resync\", force: true);");
        StringAssert.Contains(manualResyncClickHandler, "HideThisOverlay();");
        StringAssert.Contains(manualResyncClickHandler, "await Dispatcher.Yield(DispatcherPriority.Background);");
        StringAssert.Contains(playlistCode, "RepairMissingCustomFolderOutputsAfterHydrationCore(reason, verifyRootOutputDirectoryRows, settings)");
        StringAssert.Contains(playlistCode, "Lr2FolderFileDbSyncResult syncResult = SyncCustomFolderRowsBatch(");
        StringAssert.Contains(playlistCode, "materialization.DirectoryRowGenerationScopeDirectories");
        Assert.IsFalse(
            playlistCode.Contains("CreateCustomFolderOutputUpdateCallback"),
            "Startup playlist hydration must not run per-table custom folder output callbacks; missing .lr2folder repair must use the batch materialization path.");
        Assert.IsFalse(
            manualResyncPlaylistMethod.IndexOf("ReOutputCustomFolder(table)", StringComparison.Ordinal) >= 0,
            "Manual LR2 generated-data sync must batch app-managed custom folder projection instead of running per-table physical reoutput and DB sync.");
        Assert.IsTrue(
            viewModelCode.IndexOf("() => ReOutputAllCustomFoldersForLr2GeneratedDataSync(reason)", StringComparison.Ordinal)
            < viewModelCode.IndexOf("files?.QueueLr2SongDbSync(reason, force: false, allowIncompleteToQueue: false);", StringComparison.Ordinal),
            "LR2 generated-data preparation must stay behind the LR2 preparation/queue gate instead of running as an unguarded pre-step.");
        Assert.IsFalse(
            manualResyncClickHandler.IndexOf("settingDialogRootGrid.IsEnabled = false;", StringComparison.Ordinal) >= 0,
            "Manual LR2 generated-data sync must not disable the entire settings dialog while playlist projection is running.");
        Assert.IsFalse(
            manualResyncClickHandler.IndexOf("ClearValue(UIElement.IsEnabledProperty)", StringComparison.Ordinal) >= 0
            || manualResyncClickHandler.IndexOf(".IsEnabled = false", StringComparison.Ordinal) >= 0,
            "Manual LR2 generated-data sync must not overwrite the button IsEnabled binding.");
        Assert.IsTrue(
            manualResyncClickHandler.IndexOf("if (!viewModel.CanRequestLr2SongDbSyncDataResync)", StringComparison.Ordinal)
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
        StringAssert.Contains(reloadFileDiff, "files.QueueLr2SongDbSync(");
        StringAssert.Contains(reloadFileDiff, "prepareGeneratedData: () => ReOutputAllCustomFoldersForLr2GeneratedDataSync(\"ReloadFileDiff\")");
        StringAssert.Contains(viewModelCode, "prepareGeneratedData: () => ReOutputAllCustomFoldersForLr2GeneratedDataSync(\"status_bar_cleanup_retry\")");
        Assert.IsTrue(
            reloadFileDiff.IndexOf("files.ReloadFileDiff();", StringComparison.Ordinal)
            < reloadFileDiff.IndexOf("files.QueueLr2SongDbSync(", StringComparison.Ordinal),
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
            IReadOnlyList<string> normalized = MainWindowViewModel.SettingDialogViewModel.NormalizeStandaloneBmsRootPaths([firstRoot, secondRoot, installRoot]);

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
            IReadOnlyList<string> deserialized = MainWindowViewModel.SettingDialogViewModel.DeserializeStandaloneBmsRootPaths(longRoot);
            string serialized = MainWindowViewModel.SettingDialogViewModel.SerializeStandaloneBmsRootPaths(deserialized);

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
            "private void treeViewLibraryFolderContextMenuItemUnregisterRootFolder",
            "private void treeViewLibraryFolderContextMenuItemAutoRenameAllFoldersClick");

        Assert.IsFalse(unregisterHandler.Contains("Task.Run"));
        StringAssert.Contains(unregisterHandler, "viewModel.RemoveBMSDirectoryFromRootFolderAndSave(path);");
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
            "private void tableContextMenuItemRenameBMSFileClick");
        string moveFileClick = ExtractBetween(
            mainWindowCode,
            "private async void tableContextMenuItemMoveFileClick",
            "private void fixEncodingSelectedBMS");
        string contextMenuOpening = ExtractBetween(
            mainWindowCode,
            "bool canAutoRenameFolders =",
            "if (menuItem17 != null)");
        string autoRenameAll = ExtractBetween(
            viewModelCode,
            "public void AutoRenameAllChartFolders",
            "internal void AutoRenameChartFolders");
        string autoRenameAllModel = ExtractBetween(
            bmsLibraryCode,
            "internal bool AutoRenameAllChartFolders",
            "private bool ApplyAutoRenamePlans");
        string cellEditEnded = ExtractBetween(
            mainWindowCode,
            "private void customTableView_CellEditEnded",
            "private static MainChartListCellEditContext CreateMainChartListCellEditContext");
        string regularOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string rootCellEditRoute = ExtractBetween(
            viewModelCode,
            "private void RegularChartListOwnerFolderEditRequested",
            "private void RegularChartListOwnerInstallDestinationEditRequested");

        StringAssert.Contains(autoRenameClick, "GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary)");
        StringAssert.Contains(autoRenameClick, "ChartFolderAutoRenameRequest.TryCreate(targets, out ChartFolderAutoRenameRequest request)");
        StringAssert.Contains(autoRenameClick, "AutoRenameChartFolders(request)");
        Assert.IsFalse(autoRenameClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(autoRenameClick.Contains("targetSnapshot"));
        Assert.IsFalse(autoRenameClick.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(autoRenameClick.Contains("GetSelectedBmsChartFiles(ChartOperationCapabilities.None)"));
        StringAssert.Contains(moveFileClick, "GetSelectedChartTargets().Where(target => target.HasCapability(ChartOperationCapabilities.MoveInLibrary)");
        StringAssert.Contains(moveFileClick, "ChartLibraryMoveRequest.TryCreate(targets, dstDir, out ChartLibraryMoveRequest request)");
        StringAssert.Contains(moveFileClick, "viewModel.MoveLibraryCharts(request)");
        Assert.IsFalse(moveFileClick.Contains("viewModel.MoveLibraryCharts(targets, dstDir)"));
        StringAssert.Contains(contextMenuOpening, "contextMenuState.CanAutoRenameFolders");
        StringAssert.Contains(contextMenuStateBuilderCode, "hasBmsSelection || hasBmsonSelection");
        StringAssert.Contains(autoRenameAll, "files?.HasAutoRenameAllChartFolderTargets(parentDir) != true");
        StringAssert.Contains(autoRenameAll, "files?.AutoRenameAllChartFolders(parentDir, UpdateFolderAutoRenameProgressStatus) == true");
        Assert.IsFalse(autoRenameAll.Contains("IEnumerable<BeMusicSeeker.Models.BMSFile> enumerable = BMSFiles;"));
        StringAssert.Contains(autoRenameAllModel, "CreateOwnedRealPathChartDirectoriesUnsafe(parentDir)");
        StringAssert.Contains(autoRenameAllModel, "BuildAutoRenamePlansForSourceFolders");
        Assert.IsFalse(bmsLibraryCode.Contains("CreateLibraryChartSnapshotsForFolderOperations"));
        Assert.IsFalse(autoRenameAllModel.Contains("CreateOwnedSubtreeChartSnapshot"));
        Assert.IsFalse(autoRenameAllModel.Contains("BMSFiles ??"));
        Assert.IsFalse(autoRenameAllModel.Contains("files?.BmsonSongs"));
        StringAssert.Contains(cellEditEnded, "viewModel.MainChartList.RequestCellEditEnded(");
        StringAssert.Contains(regularOwnerCode, "RenameChartFolderRequest.TryCreate(folderTarget, out RenameChartFolderRequest renameRequest)");
        StringAssert.Contains(rootCellEditRoute, "RenameChartFolder(request.Request, request.FolderName)");
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
        StringAssert.Contains(resolveIndexHelper, "ownedChartCollection.CreatePlaylistLibraryResolveRefSnapshot(cancellationToken.ThrowIfCancellationRequested)");
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
            viewModelCode,
            "private NormalLibraryRefreshNotificationBatch ApplyNormalLibraryRefreshNotification",
            "private bool ApplyLatestNormalLibraryRefreshNotification");
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

        StringAssert.Contains(notificationHandler, "MainChartList.RowProjection.PruneTransientStatesToOwnedCharts(files)");
        Assert.IsFalse(notificationHandler.Contains("files?.BMSFiles"));
        Assert.IsFalse(notificationHandler.Contains("files?.BmsonSongs"));
        StringAssert.Contains(bmsonSync, "library?.CreateNormalLibrarySourceStorageOwnerView()");
        Assert.IsFalse(bmsonSync.Contains("library?.BmsonSongs"));
        Assert.IsFalse(bmsonSync.Contains("OrderBy(song => song.path"));
        StringAssert.Contains(bmsonSync, "mainChartList.RowProjection.PruneTransientStatesToOwnedCharts(library)");
        Assert.IsFalse(bmsonSync.Contains("PruneSharedChartTransientStateCacheToCurrentStorageRows"));
        StringAssert.Contains(pruneHelper, "library?.CreateOwnedChartRuntimeStatePrimaryKeySnapshot()");
        Assert.IsFalse(pruneHelper.Contains("foreach (BeMusicSeeker.Models.BMSFile"));
        Assert.IsFalse(pruneHelper.Contains("foreach (LR2SongDBExtended.bmson_song"));
        StringAssert.Contains(modelKeyHelper, "rwlockBMSFiles.GetReaderGuard()");
        StringAssert.Contains(modelKeyHelper, "ownedChartCollection.CreateChartRuntimeStatePrimaryKeySnapshot()");
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
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string notificationVersionHandler = ExtractBetween(
            viewModelCode,
            "listenerForBMSLibrary.RegisterHandler(() => files.NormalLibraryRefreshNotificationVersion",
            "listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgress");
        string notificationSyncHelper = ExtractBetween(
            viewModelCode,
            "private BmsonLibraryRowCacheSyncResult SyncNormalLibraryStorageRowCachesForRefreshNotification",
            "internal MainViewOperationSection CurrentMainViewOperationSection");
        string notificationBatchApplier = ExtractBetween(
            viewModelCode,
            "private void ApplyNormalLibraryRefreshNotificationBatch",
            "private BmsonLibraryRowCacheSyncResult SyncNormalLibraryStorageRowCachesForRefreshNotification");
        string latestNotificationApplier = ExtractBetween(
            viewModelCode,
            "private bool ApplyLatestNormalLibraryRefreshNotification",
            "private void ApplyNormalLibraryRefreshNotificationBatch");

        StringAssert.Contains(notificationVersionHandler, "ApplyNormalLibraryRefreshNotificationBatch(refreshNotification, \"normal_library_refresh\")");
        StringAssert.Contains(latestNotificationApplier, "ApplyNormalLibraryRefreshNotificationBatch(notificationBatch, \"library_charts_changed\")");
        Assert.IsFalse(viewModelCode.Contains("fallbackToCurrentOwnedCollectionVersion"));
        Assert.IsFalse(viewModelCode.Contains("NotifiesInstallDestinationOverlayProperties"));
        Assert.IsFalse(libraryCode.Contains("NotifiesInstallDestinationOverlayProperties"));
        StringAssert.Contains(notificationBatchApplier, "SyncNormalLibraryStorageRowCachesForRefreshNotification(notificationBatch)");
        string sourceChangedBranch = ExtractBlockAfter(notificationBatchApplier, "if (notificationBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged))");
        string presentationBranch = ExtractBlockAfter(notificationBatchApplier, "else");
        StringAssert.Contains(sourceChangedBranch, "RefreshNormalLibraryAfterSourceChanged(reason)");
        Assert.IsFalse(sourceChangedBranch.Contains("RefreshNormalLibraryForNotificationPresentationEffects"));
        StringAssert.Contains(presentationBranch, "RefreshNormalLibraryForNotificationPresentationEffects(notificationBatch)");
        Assert.IsFalse(presentationBranch.Contains("RefreshNormalLibraryAfterSourceChanged"));
        StringAssert.Contains(notificationSyncHelper, "notificationBatch.NotifiesBmsFiles");
        StringAssert.Contains(notificationSyncHelper, "notificationBatch.NotifiesBmsonSongs");
        StringAssert.Contains(notificationSyncHelper, "OwnedChartStorageOwnerView sourceOwnerView = notificationBatch.NotifiesBmsFiles || notificationBatch.NotifiesBmsonSongs");
        StringAssert.Contains(notificationSyncHelper, "regularChartListOwner.PruneBmsRows(sourceOwnerView?.BmsFiles)");
        Assert.IsFalse(notificationSyncHelper.Contains("files?.BMSFiles"));
        StringAssert.Contains(notificationSyncHelper, "regularChartListOwner.SyncBmsonRows(files, sourceOwnerView)");
        Assert.IsFalse(viewModelCode.Contains("listenerForBMSLibrary.RegisterHandler(() => files.BMSFiles"));
        Assert.IsFalse(viewModelCode.Contains("listenerForBMSLibrary.RegisterHandler(() => files.BmsonSongs"));
        Assert.IsFalse(viewModelCode.Contains("private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFiles"));
        Assert.IsFalse(viewModelCode.Contains("files?.BMSFiles"));
        StringAssert.Contains(libraryCode, "internal void ReplaceBmsFileLevelByTableEntryLevel(BMSTable bmsTable)");
        StringAssert.Contains(libraryCode, "List<BMSFile> bmsFiles = [.. from file in _BMSFiles ?? []");
        StringAssert.Contains(libraryCode, "dbGateway.UpdateSongLevels(bmsFiles)");
    }

    [TestMethod]
    public void Lr2SongDbRuntimeWritesUseLr2SongDbSyncStatusBoundary()
    {
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string[] lines = libraryCode.Split(["\r\n", "\n"], StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].IndexOf("dbGateway.UpsertSongs(", StringComparison.Ordinal) < 0
                && lines[i].IndexOf("dbGateway.UpdateSongLevels(", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            string localContext = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, i - 4)).Take(5));
            StringAssert.Contains(localContext, "ExecuteLr2SongDbWrite(");
        }
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
        StringAssert.Contains(libraryCode, "SuppressResourceHealthIndexInvalidation");
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

        Assert.IsFalse(viewModelCode.Contains("private void InvalidateNormalLibrarySortKeys(string reason)"));
        StringAssert.Contains(viewModelCode, "regularChartListOwner.InvalidateIdentitySortKeys(clearSourceRows)");
        StringAssert.Contains(viewModelCode, "regularChartListOwner.InvalidateSortCacheByDependency(dependency");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibrarySortDependency(MainViewDataDependency.InstallDestination");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibrarySortDependency(MainViewDataDependency.Maintenance");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibrarySortDependency(MainViewDataDependency.Warning");
        StringAssert.Contains(viewModelCode, "InvalidateNormalLibraryReferenceTableSortKeys()");
    }

    [TestMethod]
    public void ChartContextMenu_BmsOnlyAndChartCommonHandlersUseExpectedSelectionHelpers()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string contextMenuResource = ExtractBetween(
            mainWindowCode,
            "private bool TryGetTableContextMenuResource",
            "private void keywordSearchBoxTextChanged");
        string renameInvalidExtensionClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemRenameBMSFileClick",
            "private void tableContextMenuItemRemoveBMSFileClick");
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
            "private void tableContextMenuItemForceFileScanCheckSelectedCharts",
            "private void tableContextMenuRemoveInstallDestinationClick");
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
        StringAssert.Contains(renameInvalidExtensionClick, "GetSelectedBmsFormatCharts(ChartOperationCapabilities.RenameInvalidExtension)");
        StringAssert.Contains(renameInvalidExtensionClick, "viewModel.RenameBMSFilesExtensions(list, \".bmx\")");
        Assert.IsFalse(renameInvalidExtensionClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RenameInvalidExtension)"));
        StringAssert.Contains(encodingFixClick, "GetSelectedBmsFiles(ChartOperationCapabilities.RunBmsEncodingFix)");
        Assert.IsFalse(encodingFixClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunBmsEncodingFix)"));
        StringAssert.Contains(audioConvertClick, "GetSelectedBmsFiles(ChartOperationCapabilities.ConvertToAudio)");
        Assert.IsFalse(audioConvertClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.ConvertToAudio)"));
        StringAssert.Contains(resourceHealthClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthClick, "ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request)");
        StringAssert.Contains(resourceHealthClick, "viewModel.ForceResourceHealthCheckCharts(request)");
        Assert.IsFalse(resourceHealthClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(resourceHealthClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        StringAssert.Contains(resourceHealthIgnoreClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthIgnoreClick, "ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request)");
        StringAssert.Contains(resourceHealthIgnoreClick, "viewModel.SetChartResourceWarningsIgnored(request)");
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        StringAssert.Contains(resourceHealthUnignoreClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthUnignoreClick, "ChartResourceHealthRequest.TryCreate(targets, out ChartResourceHealthRequest request)");
        StringAssert.Contains(resourceHealthUnignoreClick, "viewModel.SetChartResourceWarningsIgnored(request, unset: true)");
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
    public void PendingPackageChartHandlersUseChartTargetsForPackageOperations()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string forceInstall = ExtractBetween(
            mainWindowCode,
            "private async void forceInstallSelectedPendingCharts",
            "private static string GetManualInstallConfirmationMessage");
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
            "private bool ConfirmMergeDestinationSearch");
        string openInstallDestination = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemOpenInstallDestinationClick",
            "private void treeViewInstallPackageContextMenuOpenInstallDestinationClick");

        StringAssert.Contains(forceInstall, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(forceInstall, "PendingInstallPackageOperationRequest.CreateForceInstall(targets)");
        StringAssert.Contains(forceInstall, "viewModel.InstallPendingCharts(request)");
        StringAssert.Contains(forceInstall, "private async Task ForceInstallSelectedPendingChartsAsync");
        Assert.IsFalse(forceInstall.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(manualInstall, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(manualInstall, "PendingInstallPackageOperationRequest.CreateManualInstall(targets)");
        StringAssert.Contains(manualInstall, "viewModel.InstallPendingCharts(request)");
        StringAssert.Contains(manualInstall, "private async Task ManualInstallSelectedPendingChartsAsync");
        Assert.IsFalse(manualInstall.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(deletePackages, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(deletePackages, "DeleteInstallPackageRecordsRequest.CreatePending(selectedPendingTargets)");
        StringAssert.Contains(deletePackages, "DeleteInstallPackageRecordsRequest.CreateInstalled(selectedInstalledTargets)");
        StringAssert.Contains(deletePackages, "viewModel.DeleteInstallPackageRecords(request)");
        StringAssert.Contains(viewModelCode, "internal void DeleteInstallPackageRecords(DeleteInstallPackageRecordsRequest request)");
        StringAssert.Contains(viewModelCode, "internal void InstallPendingCharts(PendingInstallPackageOperationRequest request)");
        StringAssert.Contains(deletePackages, "private async Task DeleteInstallPackageRecordsFromContextMenuAsync");
        Assert.IsFalse(deletePackages.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        Assert.IsFalse(deletePackages.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(deletePackages.Contains("GetSelectedChartCompatibilityAdapters"));
        StringAssert.Contains(estimateSearch, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(estimateSearch, "PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch(targets)");
        StringAssert.Contains(estimateSearch, "viewModel.SearchPendingInstallDestination(request)");
        StringAssert.Contains(estimateSearch, "private async Task SearchInstallDestinationSelectedPendingChartsAsync");
        Assert.IsFalse(estimateSearch.Contains("PendingInstallDestinationTargetSnapshot"));
        Assert.IsFalse(estimateSearch.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(mergeSearch, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(mergeSearch, "PendingInstallDestinationSearchRequest.CreateMergeDestinationSearch(targets)");
        StringAssert.Contains(mergeSearch, "viewModel.SearchPendingInstallDestination(request)");
        StringAssert.Contains(mergeSearch, "private async Task SearchMergeDestinationSelectedPendingChartsAsync");
        Assert.IsFalse(mergeSearch.Contains("PendingInstallDestinationTargetSnapshot"));
        Assert.IsFalse(mergeSearch.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(viewModelCode, "internal void SearchPendingInstallDestination(PendingInstallDestinationSearchRequest request)");
        StringAssert.Contains(openInstallDestination, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(openInstallDestination, "TryResolveInstallDestination(targets[0].Chart");
        Assert.IsFalse(openInstallDestination.Contains("GetSelectedPendingChartCompatibilityAdapters"));
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
        string addReferenceChartsCoordinator = ExtractMethodBody(
            libraryCode,
            "internal static void AddReferenceBMSTablesToCharts(IPlaylistReferenceApplyHost host, BMSTable table, IEnumerable<ChartFile> charts)");

        StringAssert.Contains(addChartRows, "List<ChartFile> resolvedCharts");
        StringAssert.Contains(addChartRows, "library.AddReferenceBMSTablesToCharts(table, resolvedCharts)");
        Assert.IsTrue(addChartRows.IndexOf("library.AddReferenceBMSTablesToCharts(table, resolvedCharts)", StringComparison.Ordinal) < addChartRows.IndexOf("PublishEntriesChanged(table)", StringComparison.Ordinal));
        Assert.IsFalse(addChartRows.Contains("resolvedBmsFiles"));
        StringAssert.Contains(addReferenceCharts, "PlaylistReferenceApplyCoordinator.AddReferenceBMSTablesToCharts(this, table, charts)");
        StringAssert.Contains(addReferenceChartsCoordinator, "host.ReplacePlaylistReferenceIndexTable(table)");
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
            "private static MainChartListCellEditContext CreateMainChartListCellEditContext");
        string regularOwnerCode = SourceTextTestHelper.ReadProductionSourceText(
            "BeMusicSeeker", "ViewModels", "MainWindow", "RegularChartListOwner.cs");
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();

        StringAssert.Contains(editBeginning, "viewModel.MainChartList.TryBeginCellEdit(");
        Assert.IsFalse(editBeginning.Contains("GetCompatibilityBmsFile"));
        StringAssert.Contains(editEnded, "viewModel.MainChartList.RequestCellEditEnded(");
        StringAssert.Contains(regularOwnerCode, "target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination)");
        StringAssert.Contains(regularOwnerCode, "PendingInstallDestinationEditRequest.TryCreate(installTarget, out PendingInstallDestinationEditRequest installRequest)");
        StringAssert.Contains(viewModelCode, "SetPendingInstallDestination(request.Request, request.DestinationDirectory)");
        Assert.IsFalse(editEnded.Contains("PendingInstallDestinationEditTargetSnapshot"));
        Assert.IsFalse(editEnded.Contains("GetCompatibilityBmsFile"));
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
            "private void tableContextMenuSearchCorrectInstallationDirectoryChartsClick",
            "private void tableContextMenuFixInstallationDirectoryClick");
        string fixRepair = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuFixInstallationDirectoryClick",
            "private void tableContextMenuItemDeleteEntryClick");

        StringAssert.Contains(searchRepair, "GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation)");
        StringAssert.Contains(searchRepair, "RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request)");
        StringAssert.Contains(searchRepair, "base.DataContext is not MainWindowViewModel viewModel");
        StringAssert.Contains(searchRepair, "request.MaterializeRepairEntries()");
        StringAssert.Contains(searchRepair, "viewModel.SearchCorrectInstallationDirectoryCharts(request)");
        Assert.IsFalse(searchRepair.Contains("IRepairInstalledLocationTargetSnapshot"));
        Assert.IsFalse(searchRepair.Contains("GetSelectedChartCompatibilityAdapters"));
        StringAssert.Contains(fixRepair, "GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation)");
        StringAssert.Contains(fixRepair, "RepairInstalledLocationRequest.TryCreate(targets, out RepairInstalledLocationRequest request)");
        StringAssert.Contains(fixRepair, "base.DataContext is not MainWindowViewModel viewModel");
        StringAssert.Contains(fixRepair, "request.HasInstallDestination");
        Assert.IsFalse(fixRepair.Contains("repairTargets.MaterializeRepairEntries()"));
        StringAssert.Contains(fixRepair, "viewModel.FixInstallationDirectoryCharts(request)");
        Assert.IsFalse(fixRepair.Contains("IRepairInstalledLocationTargetSnapshot"));
        Assert.IsFalse(fixRepair.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(fixRepair.Contains("target.Chart?.InstallDestination"));
    }

    [TestMethod]
    public void FullScanInstallDestinationClearUsesRepairRequest()
    {
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string clearInstallDestination = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuRemoveInstallDestinationClick",
            "private void tableContextMenuSearchCorrectInstallationDirectoryChartsClick");

        StringAssert.Contains(clearInstallDestination, "GetSelectedChartTargets(capability)");
        StringAssert.Contains(clearInstallDestination, "RepairInstalledLocationRequest.TryCreate(targets, out repairRequest)");
        StringAssert.Contains(clearInstallDestination, "repairRequest?.MaterializeRepairEntries()");
        StringAssert.Contains(clearInstallDestination, "pendingInstallRequest?.MaterializeLooseEntries()");
        StringAssert.Contains(clearInstallDestination, "PendingInstallDestinationClearRequest.TryCreate(pendingTargets, out pendingInstallRequest)");
        StringAssert.Contains(clearInstallDestination, "viewModel.ClearPendingInstallDestination(pendingInstallRequest)");
        StringAssert.Contains(clearInstallDestination, "viewModel.ClearInstallDestinationForCharts(repairRequest)");
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
            "if (firstStartupProvider())");

        StringAssert.Contains(fileInitializeBlock, "files.InitializeStartup");
        StringAssert.Contains(fileInitializeBlock, "FailStartupProgressOperation(ex.Message);");
        StringAssert.Contains(fileInitializeBlock, "_semaphore.Release();");
        StringAssert.Contains(fileInitializeBlock, "SetStartupUiInteractionBlocked(false);");
        StringAssert.Contains(fileInitializeBlock, "RaiseInitializationExceptionRequested();");
        Assert.IsFalse(fileInitializeBlock.Contains("throw;"));
    }

    [TestMethod]
    public void PostStartupWarmup_UsesOneOwnedLifecycleAndRunsAdjacentIndexesBeforeVirtualSortPrewarm()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string scheduler = ExtractBetween(
            viewModelCode,
            "private void SchedulePostStartupBestEffortWarmups",
            "private void RunPostStartupOwnedAdjacentIndexWarmup");
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

        StringAssert.Contains(scheduler, "ScheduleVirtualNormalLibraryOrderPrewarm(reason)");
        Assert.IsFalse(ownedWarmup.Contains("Wait()"));
        StringAssert.Contains(ownedWarmup, "cancellationToken.ThrowIfCancellationRequested()");

        int realPathWarmup = ownedWarmup.IndexOf("WarmOwnedRealPathDirectoryView", StringComparison.Ordinal);
        int installDestinationOverlayWarmup = ownedWarmup.IndexOf("WarmInstallDestinationOverlaySnapshot", StringComparison.Ordinal);
        int primaryHashWarmup = ownedWarmup.IndexOf("WarmInstalledPrimaryHashLookup", StringComparison.Ordinal);
        int playlistSummaryWarmup = ownedWarmup.IndexOf("WarmPlaylistSummaryOwnedHashSnapshot", StringComparison.Ordinal);
        Assert.IsTrue(realPathWarmup >= 0);
        Assert.IsTrue(installDestinationOverlayWarmup > realPathWarmup);
        Assert.IsTrue(primaryHashWarmup > installDestinationOverlayWarmup);
        Assert.IsTrue(playlistSummaryWarmup > primaryHashWarmup);
        StringAssert.Contains(lifecycleScheduler, "regularChartListOwner.TryBeginVirtualOrderPrewarm");
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
            "public async void ReloadScoresOnly()",
            "public async void ReloadFileDiff()");

        StringAssert.Contains(method, "files.InitializeScoresOnly(null)");
        Assert.IsFalse(method.Contains("ReloadTables("));
        Assert.IsFalse(method.Contains("tables.Initialize("));
        Assert.IsFalse(method.Contains("StartDeferredExternalPlaylistSync("));
        Assert.IsFalse(method.Contains("ScheduleDeferredPlaylistReferenceApply("));
        Assert.IsFalse(method.Contains("playlist_entries_hydration"));
    }

    [TestMethod]
    public void ReloadTables_DoesNotInitializeScoresOnly()
    {
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string method = ExtractBetween(
            viewModelCode,
            "public async void ReloadTables()",
            "public async void ReloadScoresOnly()");

        StringAssert.Contains(method, "tables.ReloadTables(updateCallbackAction, queueBeatorajaBmtExportAfterHydration: false)");
        StringAssert.Contains(method, "StartDeferredExternalPlaylistSync(\"ReloadTables\", fromReloadTables: true, updateCallbackAction, operationToken)");
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

        StringAssert.Contains(workspaceCode, "playlists.ReloadPlaylistTargetsAsync(");
        StringAssert.Contains(workspaceCode, "CreateReferenceReplaceUpdateCallback(playlists, library)");
        StringAssert.Contains(workspaceCode, "requireCurrentTargetForApply: true");
        StringAssert.Contains(workspaceCode, "playlists.QueueBeatorajaBmtExportAll(\"manual_resync\")");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspacePlaylistSyncResultReported");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspacePlaylistReferenceTableReplaced");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.ReplaceCurrentPlaylistDetailSelectionTable(request.OldTable, request.NewTable)");
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.RemapCurrentPlaylistDetailFolderSelection(request.Table, request.RewrittenFolders)");
        Assert.AreEqual(-1, viewModelCode.IndexOf("ReplaceCurrentPlaylistSelectionTable(", StringComparison.Ordinal));
        Assert.AreEqual(-1, viewModelCode.IndexOf("RemapCurrentPlaylistFolderSelection(", StringComparison.Ordinal));
        StringAssert.Contains(viewModelCode, "PlaylistWorkspace.RequestPlaylistDetailReloadRefresh();");
        StringAssert.Contains(viewModelCode, "InvokeMainChartListPresentationAction(");
        StringAssert.Contains(viewModelCode, "RefreshChartRowsView(MainViewUpdateMode.TreeViewFilterNotChanged);");
        Assert.IsFalse(viewModelCode.Contains("public async Task ResyncPlaylistsAsync(IEnumerable<BMSTable> tablesToResync)"));
        Assert.IsFalse(workspaceCode.Contains("ResetBMSTableAsync("));
        Assert.IsFalse(workspaceCode.Contains("ShowPlaylistLoadFailure("));
    }

    [TestMethod]
    public void BeatorajaBmtFullExport_DoesNotShowNoOpProgress()
    {
        string playlistCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string method = ExtractBetween(
            playlistCode,
            "internal void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath = null)",
            "private bool IsCurrentBeatorajaBmtFullExportGeneration(long generation)");

        StringAssert.Contains(method, "bool shouldReportProgress = projectionTablesSnapshot.Count > 0;");
        StringAssert.Contains(method, "if (shouldReportProgress)");
        StringAssert.Contains(method, "ReportBeatorajaBmtExportProgress(progressOperationId, true, projectionTablesSnapshot.Count, 0, string.Empty);");
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
        string replaceCallback = ExtractBetween(
            viewModelCode,
            "private Action<BMSPlaylist.PlaylistTableUpdateContext> CreatePlaylistReferenceReplaceUpdateCallback()",
            "private static bool ShouldScheduleDeferredPlaylistReferenceApplyAfterExternalSync");

        AssertReplaceInvalidatesReferenceSortKey(propertyDialogSave);
        StringAssert.Contains(replaceCallback, "ReferenceEntriesChanged");
        AssertReplaceInvalidatesReferenceSortKey(replaceCallback);
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
        int invalidateIndex = source.IndexOf("InvalidateNormalLibraryReferenceTableSortKeys()", StringComparison.Ordinal);
        if (invalidateIndex < 0)
        {
            invalidateIndex = source.IndexOf("PlaylistPropertyReferenceSortInvalidationRequested", StringComparison.Ordinal);
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
        Assert.AreEqual(1, CountOccurrences(xaml, "ItemsSource=\"{Binding settingDialog.AppearanceThemeOptions}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "SelectedValue=\"{Binding settingDialog.AppearanceTheme, Mode=TwoWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Appearance_theme, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Appearance_table, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Value=\"{Binding settingDialog.CustomTableFontSize, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Value=\"{Binding settingDialog.CustomTableRowHeight, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Value=\"{Binding settingDialog.CustomTableHeaderHeight, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\""));
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
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding settingDialog.ShowScoreViewerRegisterConfirmMsg}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding settingDialog.ShowDiffBMSInstallConfirmMsg}\"");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding settingDialog.ShowDuplicateFileCheckConfirmMsg}\"");
        StringAssert.Contains(xaml, "Path=Resources.Details_show_diag_duplicate_file_check, Mode=OneWay");
        StringAssert.Contains(xaml, "Style=\"{StaticResource styleWrappingSettingCheckBox}\" IsChecked=\"{Binding settingDialog.ShowRecommUpdatedMsg}\"");
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
        StringAssert.Contains(mainWindow, "ElementName=KeywordSearchBox, Mode=OneWay, Converter={qc:QuickConverter '!String.IsNullOrWhiteSpace($P)'}");
        StringAssert.Contains(mainWindow, "ElementName=KeywordSearchBoxPlaylistSummary, Mode=OneWay, Converter={qc:QuickConverter '!String.IsNullOrWhiteSpace($P)'}");
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
        Assert.AreEqual(240d, MainWindow.ResolveTreeViewWidthForSave(240d, 250d, 300d));
        Assert.AreEqual(260d, MainWindow.ResolveTreeViewWidthForSave(double.NaN, 260d, 300d));
        Assert.AreEqual(320d, MainWindow.ResolveTreeViewWidthForSave(0d, double.NaN, 320d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, MainWindow.ResolveTreeViewWidthForSave(0d, double.NaN, 0d));
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
        string viewModel = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        string statusBridge = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "MainWindowViewModel.PlaylistUrlAcquisitionEvents.cs"));
        string compositionCode = File.ReadAllText(Path.Combine(
            root,
            "BeMusicSeeker",
            "ViewModels",
            "ApplicationComposition.cs"));

        StringAssert.Contains(mainWindowCode, "viewModel.PlaylistWorkspace.OpenSinglePlaylistUrlAsync(url)");
        StringAssert.Contains(mainWindowCode, "bulkViewModel.PlaylistWorkspace.DownloadSelectedPlaylistUrlsAsync(");
        StringAssert.Contains(mainWindowCode, "viewModel.PlaylistWorkspace.DownloadSelectedPlaylistExternalPackagesAsync(");
        Assert.IsFalse(mainWindowCode.Contains("DownloadPlaylistUrlCandidateAsync"));
        Assert.IsFalse(mainWindowCode.Contains("playlistUrlBulkDownload"));
        StringAssert.Contains(workspaceCode, "DownloadCandidateAsync");
        StringAssert.Contains(workspaceCode, "DownloadSelectedPlaylistUrlsAsync");
        StringAssert.Contains(workspaceCode, "DownloadSelectedPlaylistExternalPackagesAsync");
        StringAssert.Contains(workspaceCode, "CancelPlaylistUrlDownload");
        StringAssert.Contains(workflowCode, "IPlaylistUrlDownloadGateway");
        Assert.IsFalse(workflowCode.Contains("NLog"));
        Assert.IsFalse(workspaceOwnerCode.Contains("ConfigurePlaylistUrlAcquisition"));
        StringAssert.Contains(workspaceOwnerCode, "playlistUrlAcquisitionOptionsProvider");
        StringAssert.Contains(workspaceCode, "playlistUrlInstallQueueActiveProvider");
        StringAssert.Contains(compositionCode, "new PlaylistUrlAcquisitionWorkflow(");
        StringAssert.Contains(compositionCode, "PlaylistExternalPackageLookupService.CreateDefault()");
        StringAssert.Contains(compositionCode, "playlistUrlAcquisitionOptionsProvider");
        StringAssert.Contains(compositionCode, "playlistUrlInstallQueueActiveProvider");
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
        StringAssert.Contains(workspaceCode, "GetPlaylistUrlAcquisitionOptions");
        StringAssert.Contains(workspaceCode, "DispatchPlaylistUrlAcquisitionAction");
        StringAssert.Contains(workspaceCode, "PlaylistUrlDownloadStatusChanged");
        StringAssert.Contains(statusBridge, "RefreshInstallPipelineStatus");

        string dropHandler = ExtractBetween(mainWindowCode, "private void Window_Drop", "private void Window_DragOver");
        string dragOverHandler = ExtractBetween(mainWindowCode, "private void Window_DragOver", "private void Window_MouseLeftButtonDown");
        StringAssert.Contains(dropHandler, "IsPlaylistUrlDownloadRunning");
        StringAssert.Contains(dragOverHandler, "IsPlaylistUrlDownloadRunning");
        StringAssert.Contains(mainWindowCode, "PlaylistUrlAcquisitionConfirmationRequested");
        StringAssert.Contains(mainWindowCode, "PlaylistUrlAcquisitionSummaryReady");
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
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
            "if (installTblCheck)");
        string deferredRun = ExtractBetween(
            libraryCode,
            "private RankingRefreshRunResult RunDeferredRankingRefresh(int requestVersion)",
            "private bool IsDeferredRankingRefreshRequestSuperseded");

        StringAssert.Contains(initializeTail, "options.EnableDownloadLr2IrScoreAndDetectUnsent || options.UpdateLr2IrRankingCacheOnStartup");
        StringAssert.Contains(deferredRun, "if (optionsSnapshot.UpdateLr2IrRankingCacheOnStartup)");
        StringAssert.Contains(deferredRun, "ranking_cache_refresh skipped reason=disabled");
    }

    [TestMethod]
    public void CommonOpenFileDialogActions_ReplaceLegacyDialogsAndSupportStandaloneMultiSelect()
    {
        string root = FindRepositoryRoot();
        string settingDialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string mainWindowXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
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
        StringAssert.Contains(coordinatorCode, "dialog.DefaultExtension = defaultExtension.TrimStart('.');");
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
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string loadPlaylistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));

        StringAssert.Contains(settingDialogCode, "new UiSaveFilePickerRequest(");
        StringAssert.Contains(settingDialogCode, "\".sql\"");
        StringAssert.Contains(settingDialogCode, "addExtension: true");
        StringAssert.Contains(mainWindowCode, "new UiSaveFilePickerRequest(");
        StringAssert.Contains(mainWindowCode, "\".json\"");
        StringAssert.Contains(mainWindowCode, "addExtension: true");
        StringAssert.Contains(loadPlaylistCode, "new UiFilePickerRequest(");
        StringAssert.Contains(loadPlaylistCode, "defaultExtension: \".json\"");
    }

    [TestMethod]
    public void SettingDialog_DoesNotUseMultiParameterQuickConverterBindings()
    {
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));

        Assert.IsFalse(
            settingDialogXaml.IndexOf("qc:Binding '$P0", StringComparison.Ordinal) >= 0,
            "SettingDialog is loaded during MainWindow startup; multi-parameter QuickConverter bindings can fail during BAML load.");
        StringAssert.Contains(settingDialogXaml, "Lr2_song_db_sync_data_resync");
        StringAssert.Contains(settingDialogXaml, "IsEnabled=\"{Binding IsChecked, ElementName=radioButtonUseLR2}\"");
        Assert.IsFalse(settingDialogXaml.Contains("checkBoxEnableLr2SongDbFullGeneration"));
    }

    [TestMethod]
    public void SettingDialogUninstall_ShowsResultBeforeExit()
    {
        string root = FindRepositoryRoot();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string viewModelCode = SourceTextTestHelper.ReadMainWindowViewModelSourceText();
        string uninstallClickHandler = ExtractMethodBody(settingDialogCode, "private async void detailTabItemUninstallButtonClicked(object sender, RoutedEventArgs e)");

        StringAssert.Contains(uninstallClickHandler, "UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_success_uninstall");
        StringAssert.Contains(uninstallClickHandler, "UiDialogRoute.ShowMessageBox(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_failed_uninstall");
        StringAssert.Contains(uninstallClickHandler, "if (viewModel.IsLibraryOperationInProgress)");
        StringAssert.Contains(uninstallClickHandler, "BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization");
        StringAssert.Contains(uninstallClickHandler, "settingDialogRootGrid.IsEnabled = false;");
        StringAssert.Contains(uninstallClickHandler, "if (!closeAfterSuccess)");
        StringAssert.Contains(uninstallClickHandler, "settingDialogRootGrid.IsEnabled = true;");
        Assert.IsTrue(
            uninstallClickHandler.IndexOf("Msg_success_uninstall", StringComparison.Ordinal)
            < uninstallClickHandler.IndexOf("アプリケーションを終了します", StringComparison.Ordinal));
        Assert.IsTrue(
            uninstallClickHandler.IndexOf("viewModel.IsLibraryOperationInProgress", StringComparison.Ordinal)
            < uninstallClickHandler.IndexOf("続行しますか？", StringComparison.Ordinal));
        Assert.IsTrue(
            uninstallClickHandler.IndexOf("settingDialogRootGrid.IsEnabled = false;", StringComparison.Ordinal)
            < uninstallClickHandler.IndexOf("viewModel.UninstallAllData();", StringComparison.Ordinal));
        Assert.IsTrue(
            uninstallClickHandler.IndexOf("base.Dispatcher.BeginInvoke", StringComparison.Ordinal)
            < uninstallClickHandler.IndexOf("closeAfterSuccess = true;", StringComparison.Ordinal));

        string uninstallMethod = ExtractMethodBody(viewModelCode, "internal void UninstallAllData()");
        Assert.IsFalse(uninstallMethod.Contains("Msg_success_uninstall"));
        Assert.IsFalse(uninstallMethod.Contains("Msg_failed_uninstall"));
        Assert.IsFalse(uninstallMethod.Contains("base.Messenger.Raise"));
        Assert.IsFalse(uninstallMethod.Contains("Dispatcher.Invoke"));
    }

    [TestMethod]
    public void EstimatedInstallPostProcessing_IsBatchedAndUsesResourceHealthDelta()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string resourceHealthCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "ResourceHealthWarningProjection.cs"));
        string installEstimationDoc = File.ReadAllText(Path.Combine(root, "devdocs", "spec", "install-estimation-current-logic.md"));

        StringAssert.Contains(libraryCode, "internal sealed class EstimatedInstallBatchApplyContext");
        StringAssert.Contains(libraryCode, "host.ApplyEstimatedInstallBatchLibraryState(batchApplyContext, canUseResourceHealthIndexDelta)");
        StringAssert.Contains(libraryCode, "ApplyEstimatedInstallBatchLibraryState(context)");
        string batchContext = ExtractBetween(libraryCode, "internal sealed class EstimatedInstallBatchApplyContext", "internal sealed class PendingEstimatedInstallCollectionApplyResult");
        StringAssert.Contains(batchContext, "public List<ChartFile> AddedCharts { get; } = [];");
        Assert.IsFalse(batchContext.Contains("AddedBmsFiles"));
        Assert.IsFalse(batchContext.Contains("AddedBmsonSongs"));
        StringAssert.Contains(batchContext, "AddInstalledTargets(ChartStorageTargetSet addedTargets");
        StringAssert.Contains(libraryCode, "static ChartStorageTargetSet CreateAddedStorageTargets(PackageInstallExecutionResult installResult)");
        Assert.IsFalse(libraryCode.Contains("CreateResourceMaintenanceTargetSet(installResult?.AddedCharts)"));
        Assert.IsFalse(libraryCode.Contains("CreateResourceMaintenanceTargetSet(IEnumerable<BMSFile> bmsFiles"));
        Assert.IsFalse(libraryCode.Contains("CreateBmsResourceMaintenanceTargetCharts"));
        string estimatedBatchApplyMethod = ExtractMethodBody(libraryCode, "internal static DirectoryResourceLookupCache.ReverseLookupMutationResult ApplyEstimatedInstallBatchLibraryState");
        StringAssert.Contains(estimatedBatchApplyMethod, "ChartStorageTargetSet.FromCharts(context.AddedCharts)");
        Assert.IsFalse(libraryCode.Contains("ResolveAddedBmsonSongsFromInstalledPackages"));
        Assert.IsFalse(libraryCode.Contains("CreateAddedBmsonChartProjectionsFromInstalledPackages"));
        string estimatedInstallCoordinator = ExtractMethodBody(libraryCode, "internal static void InstallPendingPackagesToEstimatedDestinations");
        string estimatedInstallMaintenanceBridge = ExtractMethodBody(libraryCode, "int IPendingEstimatedInstallHost.ApplyEstimatedInstallMaintenanceAndInlineChartInfo");
        Assert.IsFalse(estimatedInstallCoordinator.Contains("foreach (LR2SongDBExtended.bmson_song song in BmsonSongs"));
        StringAssert.Contains(estimatedInstallCoordinator, "batchApplyContext.AddedCharts");
        StringAssert.Contains(libraryCode, "CreateAddedBmsonChartProjections(addedCharts)");
        StringAssert.Contains(libraryCode, "BuildEstimatedInstallMaintenanceTargets(deferredMaintenanceCharts)");
        Assert.IsFalse(estimatedInstallMaintenanceBridge.Contains("ResourceHealthIndexUpdateMode.FullOnUpdates"));
        StringAssert.Contains(estimatedInstallMaintenanceBridge, "resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates");
        StringAssert.Contains(estimatedBatchApplyMethod, "host.LogReverseLookupMutationAndQueueWarmupIfNeeded(\"install_package\", reverseLookupMutation);");
        StringAssert.Contains(libraryCode, "resource_health_index_delta reason=");
        StringAssert.Contains(libraryCode, "if (estimatedInstallMaintenanceTargets.Count > 0)");
        StringAssert.Contains(libraryCode, "setMaintenanceInfo(");
        StringAssert.Contains(libraryCode, "estimatedInstallMaintenanceTargets,");
        StringAssert.Contains(libraryCode, "resourceHealthMutationReason: \"install_package_estimated\"");
        StringAssert.Contains(resourceHealthCode, "internal ResourceHealthIndexSnapshot ApplyDelta(");
        StringAssert.Contains(resourceHealthCode, "HashSet<ResourceHealthChartKey> targetKeys");

        StringAssert.Contains(installEstimationDoc, "推定先への移動");
        StringAssert.Contains(installEstimationDoc, "group は逐次");
        StringAssert.Contains(installEstimationDoc, "library/cache/index は batch 末尾");
        StringAssert.Contains(installEstimationDoc, "resource health index は delta");
    }

    [TestMethod]
    public void DuplicateMergeMaintenanceDefersResourceHealthIndexRebuildAndLogsDuplicateSearchStages()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string mergeMethod = ExtractMethodBody(libraryCode, "internal static void MergeChartDirectory(");
        string mergeMaintenanceHostMethod = ExtractMethodBody(libraryCode, "void ILibraryMergeDirectoryHost.SetMergeFolderMaintenanceInfo");
        string duplicateSearchMethod = ExtractMethodBody(libraryCode, "public void SearchDuplicateChartGroups()");

        StringAssert.Contains(libraryCode, "DeferOnUpdates");
        StringAssert.Contains(libraryCode, "ResourceHealthIndexUpdateMode.DeltaOnUpdates");
        StringAssert.Contains(libraryCode, "resourceHealthMutationReason = string.IsNullOrWhiteSpace(resourceHealthMutationReason)");
        StringAssert.Contains(libraryCode, "? \"setMaintenanceInfo\"");
        StringAssert.Contains(libraryCode, "BuildOwnedChartCollectionMaintenanceMutationResult(");
        StringAssert.Contains(libraryCode, "DispatchOwnedChartCollectionMutation(mutationResult, resourceHealthMutationReason)");
        StringAssert.Contains(mergeMethod, "ChartStorageTargetSet movedTargets = ChartStorageTargetSet.FromCharts");
        StringAssert.Contains(mergeMethod, "ChartStorageTargetSet maintenanceTargets = ChartStorageTargetSet.FromCharts");
        StringAssert.Contains(mergeMethod, "destinationMaintenanceTargets.Charts.Concat(movedTargets.Charts)");
        StringAssert.Contains(mergeMethod, "maintenanceTargets.Charts");
        StringAssert.Contains(mergeMethod, "host.ApplyInstalledChartStorageTargets(movedTargets)");
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
        string root = FindRepositoryRoot();
        string mainWindowCode = SourceTextTestHelper.ReadMainWindowSourceText();
        string folderMergeMethod = ExtractMethodBody(mainWindowCode, "private void ExecuteDuplicateFolderMerge(string srcPath, string dstPath, DuplicateGroup duplicateGroup)");
        string hashCleanupMethod = ExtractMethodBody(mainWindowCode, "private void ExecuteDuplicateHashCleanup(DuplicateGroup duplicateGroup, string folderPath)");

        StringAssert.Contains(folderMergeMethod, "Settings.Default.ShowDuplicateFileCheckConfirmMsg && UiDialogRoute.ShowMessageBox");
        StringAssert.Contains(hashCleanupMethod, "Settings.Default.ShowDuplicateFileCheckConfirmMsg && UiDialogRoute.ShowMessageBox");
    }

    [TestMethod]
    public void ResourceHealthForceFilter_ReusesFullOwnedMaintenanceTargets()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string method = ExtractMethodBody(libraryCode, "internal List<ChartFile> GetChartsNeedResourceFix");
        string setOwnedMethod = ExtractMethodBody(libraryCode, "private MaintenanceWorkflowResult setOwnedMaintenanceInfo");
        string buildMutationMethod = ExtractMethodBody(libraryCode, "internal static ResourceHealthIndexMutation BuildMaintenanceMutation");
        string dispatchMethod = ExtractMethodBody(libraryCode, "private void DispatchOwnedChartCollectionMutation");
        string alignMethod = ExtractMethodBody(libraryCode, "private static void AlignResourceHealthFullOwnedTargetVersionAfterOwnedCollectionNotification");

        StringAssert.Contains(method, "CreateFullOwnedResourceMaintenanceTargetSet(");
        StringAssert.Contains(method, "\"force_resource_health_filter\"");
        StringAssert.Contains(method, "ResourceMaintenanceTargetSet targetSet = useOwnedSnapshot");
        StringAssert.Contains(method, "List<ChartFile> targets = targetSet.Charts");
        StringAssert.Contains(setOwnedMethod, "ResourceMaintenanceTargetSet maintenanceTargets = CreateFullOwnedResourceMaintenanceTargetSet(reason)");
        Assert.IsFalse(libraryCode.Contains("targets = null;"));
        StringAssert.Contains(method, "setMaintenanceInfoCoreLocked(");
        StringAssert.Contains(method, "targetSet,");
        StringAssert.Contains(method, "currentMaintenanceTargetCharts: out targets");
        Assert.IsFalse(method.Contains("RescanResourceHealthCharts(targets);"));

        string coreMethod = ExtractMethodBody(libraryCode, "private MaintenanceWorkflowResult setMaintenanceInfoCoreLocked");
        StringAssert.Contains(libraryCode, "RefreshResourceMaintenanceTargetChartsFromCurrentStorageOwners");
        StringAssert.Contains(libraryCode, "ShouldRefreshResourceMaintenanceTargetsFromCurrentStorageOwners(MaintenanceWorkflowResult workflowResult)");
        StringAssert.Contains(coreMethod, "if (ShouldRefreshResourceMaintenanceTargetsFromCurrentStorageOwners(workflowResult))");
        StringAssert.Contains(coreMethod, "maintenanceTargetCharts = RefreshResourceMaintenanceTargetChartsFromCurrentStorageOwners(maintenanceTargetCharts);");
        StringAssert.Contains(coreMethod, "maintenanceTargets = maintenanceTargets.WithCharts(maintenanceTargetCharts);");
        StringAssert.Contains(buildMutationMethod, "List<ChartFile> maintenanceTargetCharts = maintenanceTargets.Charts");
        StringAssert.Contains(buildMutationMethod, "maintenanceTargets.HasFullOwnedVersion");
        StringAssert.Contains(buildMutationMethod, "mutation.FullOwnedTargetSet = maintenanceTargets;");
        StringAssert.Contains(dispatchMethod, "PublishOwnedCollectionChangeNotification(result);");
        StringAssert.Contains(dispatchMethod, "AlignResourceHealthFullOwnedTargetVersionAfterOwnedCollectionNotification(result);");
        StringAssert.Contains(alignMethod, "mutation.FullOwnedTargetSet = mutation.FullOwnedTargetSet.WithOwnedCollectionVersion(result.OwnedCollectionVersion);");
    }

    [TestMethod]
    public void MaintenanceHydrationUsesOwnedStorageOwnerView()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string applyCoordinatorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "MaintenanceHydrationApplyCoordinator.cs"));
        string applyMethod = ExtractMethodBody(libraryCode, "private void ApplyMaintenanceHydrationResult");
        string applyCoordinatorMethod = ExtractMethodBody(applyCoordinatorCode, "internal void Apply");
        string attachMethod = ExtractMethodBody(libraryCode, "internal static void AttachMaintenanceSnapshots");
        string staleMethod = ExtractMethodBody(libraryCode, "internal static void CaptureOwnerPathAndStaleMaintenancePaths");
        string countMethod = ExtractMethodBody(libraryCode, "private int CountInstallableMaintenanceSnapshotTargets");
        string snapshotMethod = ExtractMethodBody(libraryCode, "private InstallableMaintenanceSnapshot CreateInstallableMaintenanceSnapshot");

        StringAssert.Contains(applyMethod, "new MaintenanceHydrationApplyCoordinator(new MaintenanceHydrationApplyHost(this))");
        StringAssert.Contains(applyMethod, "coordinator.Apply(result)");
        StringAssert.Contains(libraryCode, "private sealed class MaintenanceHydrationApplyHost(BMSLibrary owner) : IMaintenanceHydrationApplyHost");
        StringAssert.Contains(libraryCode, "return owner.CreateOwnedChartStorageOwnerViewUnsafe()");
        StringAssert.Contains(libraryCode, "return owner.BeginResourceHealthInputMutation()");
        StringAssert.Contains(libraryCode, "return owner.CreateFullOwnedResourceMaintenanceTargetSet(reason)");
        StringAssert.Contains(libraryCode, "return owner.dbGateway.DeleteMaintenanceRows(staleMaintenancePaths)");
        StringAssert.Contains(libraryCode, "owner.DispatchMaintenanceHydrationResult(result, resourceHealthTargets)");
        StringAssert.Contains(applyCoordinatorMethod, "OwnedChartStorageOwnerView ownerView = host.CreateOwnedChartStorageOwnerView()");
        StringAssert.Contains(applyCoordinatorMethod, "using (host.BeginResourceHealthInputMutation())");
        StringAssert.Contains(applyCoordinatorMethod, "MaintenanceHydrationOwnerAttachService.AttachMaintenanceSnapshots(ownerView, result)");
        StringAssert.Contains(applyCoordinatorMethod, "MaintenanceHydrationOwnerAttachService.CaptureOwnerPathAndStaleMaintenancePaths(ownerView, result)");
        StringAssert.Contains(attachMethod, "foreach (BMSFile item in ownerView.BmsFiles)");
        StringAssert.Contains(attachMethod, "foreach (LR2SongDBExtended.bmson_song item in ownerView.BmsonSongs)");
        StringAssert.Contains(staleMethod, "ownerView.ContainsOwnerPath(maintenancePath)");
        StringAssert.Contains(applyCoordinatorMethod, "ResourceMaintenanceTargetSet resourceHealthTargets = default;");
        StringAssert.Contains(applyCoordinatorMethod, "resourceHealthTargets = host.CreateFullOwnedResourceMaintenanceTargetSet");
        StringAssert.Contains(applyCoordinatorMethod, "result.CleanupDeletedCount = host.DeleteStaleMaintenanceRows(result.StaleMaintenancePaths)");
        StringAssert.Contains(applyCoordinatorMethod, "host.ForceInvalidateResourceHealthIndex(\"maintenance_hydration_cleanup_failed\")");
        StringAssert.Contains(applyCoordinatorMethod, "result.ViewRefreshQueued = true;");
        StringAssert.Contains(applyCoordinatorMethod, "host.DispatchMaintenanceHydrationResult(result, resourceHealthTargets)");
        string dispatchHydrationMethod = ExtractMethodBody(libraryCode, "private void DispatchMaintenanceHydrationResult");
        string dispatchHostMethod = ExtractMethodBody(libraryCode, "private long DispatchMaintenanceHydration");
        StringAssert.Contains(dispatchHydrationMethod, "new MaintenanceHydrationDispatchCoordinator(new MaintenanceHydrationDispatchHost(this))");
        StringAssert.Contains(dispatchHydrationMethod, "coordinator.Dispatch(hydrationResult, fullOwnedTargets)");
        StringAssert.Contains(libraryCode, "private sealed class MaintenanceHydrationDispatchHost(BMSLibrary owner) : IMaintenanceHydrationDispatchHost");
        StringAssert.Contains(libraryCode, "return owner.DispatchMaintenanceHydration(plan);");
        StringAssert.Contains(dispatchHostMethod, "CreateMaintenanceHydrationMutationResult(plan)");
        StringAssert.Contains(dispatchHostMethod, "DispatchOwnedChartCollectionMutation(mutationResult, \"maintenance_hydration\")");
        StringAssert.Contains(dispatchHostMethod, "return mutationResult.ResourceHealthDispatchResult?.IndexMs ?? 0L");
        Assert.IsFalse(applyMethod.Contains("resourceHealthTargetOwnedCollectionVersion"));
        Assert.IsFalse(applyMethod.Contains("resourceHealthTargetInputVersion"));
        Assert.IsFalse(applyMethod.Contains("RebuildResourceHealthIndexSnapshotLocked(\"maintenance_hydration\")"));
        string createHydrationMutationMethod = ExtractMethodBody(libraryCode, "private static OwnedChartCollectionMutationResult CreateMaintenanceHydrationMutationResult");
        string hydrationPlanMethod = ExtractMethodBody(libraryCode, "internal static MaintenanceHydrationDispatchPlan BuildMaintenanceHydrationDispatchPlan");
        string hydrationMutationMethod = ExtractMethodBody(libraryCode, "internal static ResourceHealthIndexMutation BuildMaintenanceHydrationFullRebuildMutation");
        Assert.IsFalse(libraryCode.Contains("BuildMaintenanceHydrationMutationResult"));
        StringAssert.Contains(createHydrationMutationMethod, "throw new ArgumentNullException(nameof(plan))");
        StringAssert.Contains(createHydrationMutationMethod, "new OwnedChartCollectionMutationResult(plan.ResourceHealthMutation)");
        StringAssert.Contains(createHydrationMutationMethod, "WarningPresentationChanged = plan.WarningPresentationChanged");
        StringAssert.Contains(createHydrationMutationMethod, "MaintenancePresentationChanged = plan.MaintenancePresentationChanged");
        StringAssert.Contains(hydrationPlanMethod, "warningPresentationChanged: true");
        StringAssert.Contains(hydrationPlanMethod, "maintenancePresentationChanged: true");
        StringAssert.Contains(hydrationMutationMethod, "RebuildFull = true");
        StringAssert.Contains(hydrationMutationMethod, "FullOwnedTargetSet = fullOwnedTargets");
        StringAssert.Contains(libraryCode, "resource_health_index_full_target_stale");
        Assert.IsFalse(applyMethod.Contains("foreach (BMSFile item in BMSFiles"));
        Assert.IsFalse(applyMethod.Contains("foreach (LR2SongDBExtended.bmson_song item in BmsonSongs"));
        StringAssert.Contains(countMethod, "(BMSFiles?.Count ?? 0) + (BmsonSongs?.Count ?? 0)");
        Assert.IsFalse(countMethod.Contains("CreateOwnedChartStorageOwnerViewUnsafe().Count"));
        StringAssert.Contains(snapshotMethod, "snapshotCount = CreateOwnedChartStorageOwnerViewUnsafe().Count");
        Assert.IsFalse(snapshotMethod.Contains("bmsonSnapshotCount"));
    }

    [TestMethod]
    public void FileScanMutationUsesOwnedCollectionForRemovedCharts()
    {
        string root = FindRepositoryRoot();
        string libraryCode = SourceTextTestHelper.ReadBmsLibrarySourceText();
        string ownedCollectionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "OwnedChartCollectionState.cs"));
        string coordinatorCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "LibraryFileScanStorageMutationCoordinator.cs"));
        string applyMethod = ExtractMethodBody(libraryCode, "private void ApplyLibraryFileScanStorageMutation");
        string coordinatorApplyMethod = ExtractMethodBody(coordinatorCode, "internal void Apply");
        string buildMethod = ExtractMethodBody(libraryCode, "private OwnedChartCollectionMutationResult BuildOwnedChartCollectionFileScanMutationResult");

        StringAssert.Contains(applyMethod, "new LibraryFileScanStorageMutationCoordinator(new LibraryFileScanStorageMutationHost(this))");
        StringAssert.Contains(applyMethod, "coordinator.Apply(fileCheckResult, reason)");
        StringAssert.Contains(libraryCode, "internal sealed class LibraryFileScanStorageMutationHost(BMSLibrary owner) : ILibraryFileScanStorageMutationHost");
        StringAssert.Contains(libraryCode, "return owner.TryCreateOwnedFileScanRemovedStorageOwnerIdentityChartsUnsafe(");
        StringAssert.Contains(libraryCode, "mutationResult = owner.BuildOwnedChartCollectionFileScanMutationResult(");
        StringAssert.Contains(libraryCode, "owner.ApplyOwnedChartCollectionStorageReplacement(storageRows)");
        StringAssert.Contains(coordinatorApplyMethod, "bool removedPayloadAvailable = host.TryCreateRemovedStorageOwnerIdentityCharts(");
        StringAssert.Contains(coordinatorApplyMethod, "out List<ChartFile> removedCharts");
        StringAssert.Contains(coordinatorApplyMethod, "resourceHealthMutation.BaseIndexCurrent");
        StringAssert.Contains(coordinatorApplyMethod, "host.ApplyStorageRowsResourceIndexAndOwnedCollectionReplacement(fileCheckResult)");
        Assert.IsFalse(applyMethod.Contains("BuildOwnedChartCollectionFileScanMutationResult(fileCheckResult, BMSFiles, BmsonSongs)"));
        StringAssert.Contains(buildMethod, "if (removedPayloadAvailable)");
        StringAssert.Contains(buildMethod, "storageMutation.RemoveRequests.AddRange((removedCharts ?? [])");
        Assert.IsFalse(buildMethod.Contains("IReadOnlyList<BMSFile> currentBmsFiles"));
        Assert.IsFalse(buildMethod.Contains("IReadOnlyList<LR2SongDBExtended.bmson_song> currentBmsonSongs"));
        StringAssert.Contains(libraryCode, "ownedChartCollection.CreateFileScanRemovedStorageOwnerIdentityCharts(");
        StringAssert.Contains(ownedCollectionCode, "internal List<ChartFile> CreateFileScanRemovedStorageOwnerIdentityCharts(");
    }

    [TestMethod]
    public void EmptyDbStartupOptimizationDocs_DocumentFileDiffPipeline()
    {
        string root = FindRepositoryRoot();
        string initializationCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryInitializationService.cs"));
        string planDoc = File.ReadAllText(Path.Combine(root, "devdocs", "plan", "empty-db-first-startup-optimization-plan.md"));
        string startupFlowDoc = File.ReadAllText(Path.Combine(root, "devdocs", "spec", "startup-initialization-flow.md"));

        StringAssert.Contains(initializationCode, "private const int DefaultInlineChartInfoBatchSize = 2048;");
        StringAssert.Contains(initializationCode, "var postParseQueue = new BlockingCollection<FileDiffPostParseWorkItem>(postParseQueueCapacity);");
        StringAssert.Contains(initializationCode, "BlockingCollection<FileScanDiffCommitChunk> commitQueue = streamCommitChunks");
        StringAssert.Contains(initializationCode, "BlockingCollection<FileDiffCommitWriterItem> writerQueue");
        StringAssert.Contains(initializationCode, "commitContext.AddChunk(chunk);");
        StringAssert.Contains(initializationCode, "commit_streaming_enabled=");
        StringAssert.Contains(initializationCode, "commit_writer_queue_wait_ms=");
        StringAssert.Contains(initializationCode, "inline_maintenance_shared_resource_cache_entries=");
        StringAssert.Contains(initializationCode, "BuildInlineBmsMaintenanceBatch(");
        StringAssert.Contains(initializationCode, "Task[] workerTasks = [.. Enumerable.Range(0, parserDegree)");
        StringAssert.Contains(initializationCode, "Parallel.For(0, candidates.Count");
        StringAssert.Contains(initializationCode, "inline_maintenance_wall_ms=");
        StringAssert.Contains(initializationCode, "parser_output_wait_ms=");
        StringAssert.Contains(initializationCode, "\" bms=\" + metrics.BmsCount");
        StringAssert.Contains(initializationCode, "\" bmson=\" + metrics.BmsonCount");

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
        StringAssert.Contains(libraryCode, "ProcessPendingInstallEstimateEvaluationPipeline(request, source, token, evaluationContext, evaluationRequests, executionPolicy");
        StringAssert.Contains(libraryCode, "ProcessPendingInstallEstimateEvaluationPipeline(request, source, CancellationToken.None, evaluationContext, evaluationRequests, executionPolicy");
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

    private static PlayHistoryRow CreateResolvedPlayHistoryRow()
    {
        const string hash = "cccccccccccccccccccccccccccccccc";
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
