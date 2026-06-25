using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
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
    public void DeleteContextMenuItems_UseSpecificDeleteResourceKeys()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteEntry\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Remove_playlist_entry, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteFile\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_file, Mode=OneWay"));
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
        StringAssert.Contains(playHistoryTree, "ItemsSource=\"{Binding PlayHistoryArchivePeriodTree}\"");
        StringAssert.Contains(playHistoryTree, "<HierarchicalDataTemplate DataType=\"{x:Type vm:PlayHistoryPeriodTreeItem}\" ItemsSource=\"{Binding Children}\" ItemContainerStyle=\"{StaticResource styleTreeViewItemPlayHistoryPeriodContainer}\">");
        StringAssert.Contains(playHistoryTree, "<DataTemplate x:Key=\"templateTreeViewItemHeaderPlayHistoryPeriod\">");
        StringAssert.Contains(playHistoryTree, "<TreeViewItem Focusable=\"False\" HeaderTemplate=\"{StaticResource templateTreeViewItemHeaderPlayHistoryPeriod}\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Play_history_period_archive, Mode=OneWay}\" ItemsSource=\"{Binding PlayHistoryArchivePeriodTree}\" ItemContainerStyle=\"{StaticResource styleTreeViewItemPlayHistoryPeriodContainer}\" />");
    }

    [TestMethod]
    public void PlayHistoryMainTable_UsesDisplayOnlyDragAndRejectsChartContextMenu()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string mainTable = ExtractBetween(xaml, "<v:CustomTableView x:Name=\"customTableView\"", "<i:Interaction.Triggers>");
        string playHistoryContextMenu = ExtractBetween(xaml, "<ContextMenu x:Key=\"playHistoryContextMenu\"", "<ContextMenu x:Key=\"treeViewPlaylistRootContextMenu\"");
        PlayHistoryRow playHistoryRow = CreateUnresolvedPlayHistoryRow();
        PlayHistoryRow resolvedPlayHistoryRow = CreateResolvedPlayHistoryRow();
        LibraryChartRow libraryRow = LibraryChartRow.FromBmsFile(CreateContextMenuBmsFile());

        StringAssert.Contains(mainTable, "RowDragKind=\"{Binding ChartRowsViewRowDragKind, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SortColumnName=\"{Binding MainTableSortParameters.ColumnsName, Mode=OneWay}\"");
        StringAssert.Contains(mainTable, "SortDirection=\"{Binding MainTableSortParameters.Direction, Mode=OneWay}\"");
        Assert.IsFalse(mainTable.Contains("SortColumnName=\"{Binding SortParameters.ColumnsName"));
        Assert.IsFalse(mainTable.Contains("RowDragKind=\"PlaylistDropCandidateRows\""));
        Assert.IsFalse(MainWindow.TryResolveTableContextMenuPolicyForTest(playHistoryRow, ChartOperationSourceScope.Library, out bool playHistoryMissingContextMenu));
        Assert.IsFalse(playHistoryMissingContextMenu);
        Assert.IsTrue(MainWindow.TryResolveTableContextMenuPolicyForTest(libraryRow, ChartOperationSourceScope.Library, out bool libraryMissingContextMenu));
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
        Assert.IsFalse(MainWindow.TryResolvePlayHistoryContextMenuPolicyForTest(playHistoryRow, out PlayHistoryContextMenuState unresolvedState));
        Assert.IsNull(unresolvedState);
        Assert.IsNull(GridRowResolver.GetRepositorySha256(playHistoryRow));
        Assert.IsTrue(MainWindow.TryResolvePlayHistoryContextMenuPolicyForTest(resolvedPlayHistoryRow, out PlayHistoryContextMenuState resolvedState));
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
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string summaryRow = ExtractBetween(xaml, "<Border Grid.Row=\"2\" Grid.Column=\"1\" Background=\"{DynamicResource App.SurfaceBrush}\"", "<v:CustomTableView x:Name=\"customTableView\"");

        StringAssert.Contains(summaryRow, "Visibility=\"{qc:MultiBinding '($P0 &amp;&amp; !$P1) ? Visibility.Visible : Visibility.Collapsed'");
        StringAssert.Contains(summaryRow, "P0={Binding IsPlayHistoryViewActive}");
        StringAssert.Contains(summaryRow, "P1={Binding IsPlaylistSummaryMode}");
        StringAssert.Contains(summaryRow, "ItemsSource=\"{Binding PlayHistorySummaryCards}\"");
        StringAssert.Contains(summaryRow, "Text=\"{Binding PlayHistorySummaryDiagnosticText}\"");
        StringAssert.Contains(summaryRow, "Text=\"{Binding Label}\"");
        StringAssert.Contains(summaryRow, "Text=\"{Binding Value}\"");
        StringAssert.Contains(summaryRow, "Command=\"{Binding DataContext.TogglePlayHistorySummaryCardFilterCommand");
        StringAssert.Contains(summaryRow, "CommandParameter=\"{Binding}\"");
        StringAssert.Contains(summaryRow, "<DataTrigger Binding=\"{Binding Compact}\" Value=\"True\">");
        StringAssert.Contains(summaryRow, "<DataTrigger Binding=\"{Binding IsSelected}\" Value=\"True\">");
        StringAssert.Contains(summaryRow, "App.WarningTextBrush");
        StringAssert.Contains(viewModelCode, "PlayHistorySummaryCards = CreatePlayHistorySummaryCards(summary, state.Provider, SnapshotSelectedPlayHistorySummaryFilterKeys())");
        StringAssert.Contains(viewModelCode, "string diagnosticSummaryText = FormatPlayHistoryDiagnosticSummary(diagnostics)");
        StringAssert.Contains(viewModelCode, "PlayHistorySummaryDiagnosticText = diagnosticSummaryText");
    }

    [TestMethod]
    public void PlayHistoryView_LogsDedicatedStageEvents()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string applyPlayHistoryView = ExtractBetween(viewModelCode, "private void ApplyPlayHistoryView", "private IReadOnlyList<PlayHistoryRow> ApplyPlayHistoryKeywordFilterRows");
        string presentationOnly = ExtractBetween(viewModelCode, "private bool TryApplyPlayHistoryPresentationOnly", "private void ApplyPlayHistorySortedRows");
        string applyPlayHistorySortedRows = ExtractBetween(viewModelCode, "private void ApplyPlayHistorySortedRows", "private PlayHistoryProjectionResult CreatePlayHistoryProjectionResult");
        string staleRequestLog = ExtractBetween(viewModelCode, "private void LogStalePlayHistoryViewRequest", "private static int CountDistinctPlayHistoryFolderLabels");

        StringAssert.Contains(viewModelCode, "LogPlayHistoryEvent(");
        AssertLogPlayHistoryEventContract(applyPlayHistoryView, "play_history_read_done", "period=", "requestId=", "schemaStatus=", "rows=", "diagnosticsCount=", "elapsedMs=");
        AssertLogPlayHistoryEventContract(applyPlayHistoryView, "play_history_read_period_index_done", "period=", "requestId=", "schemaStatus=", "days=", "diagnosticsCount=", "elapsedMs=");
        AssertLogPlayHistoryEventContract(applyPlayHistoryView, "play_history_read_period_index_skipped", "period=", "requestId=", "schemaStatus=", "reason=schema_unavailable");
        AssertLogPlayHistoryEventContract(applyPlayHistoryView, "play_history_projection_done", "projectionEventName", "schemaStatus=", "fallback=", "reason=", "projection_index_failed", "schema_unavailable");
        AssertLogPlayHistoryEventContract(applyPlayHistoryView, "play_history_projection_skipped", "projectionEventName", "rawCount=", "projectedCount=", "diagnosticsCount=", "projectionMs=");
        AssertLogPlayHistoryEventContract(applyPlayHistoryView, "play_history_projection_fallback", "projectionEventName", "fallback=", "reason=", "projection_index_failed");
        AssertLogPlayHistoryEventContract(presentationOnly, "play_history_view_presentation_skipped", "period=", "requestId=", "reason=no_current_matching_state", "totalMs=");
        AssertLogPlayHistoryEventContract(applyPlayHistorySortedRows, "play_history_view_apply", "period=", "requestId=", "sortOnly=", "schemaStatus=", "sourceCount=", "projectedCount=", "viewCount=", "totalMs=");
        AssertLogPlayHistoryEventContract(staleRequestLog, "play_history_view_stale_skipped", "mode=", "requestedMode=", "requestId=", "currentRequestId=", "elapsedMs=");
    }

    [TestMethod]
    public void PlayHistoryView_SelectsBeatorajaProviderAndProjectsSha256Rows()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string applyPlayHistoryView = ExtractBetween(viewModelCode, "private void ApplyPlayHistoryView", "private IReadOnlyList<PlayHistoryRow> ApplyPlayHistoryKeywordFilterRows");
        string beatorajaProjection = ExtractBetween(viewModelCode, "BeatorajaPlayHistoryReadResult readResult,", "private PlayHistoryViewRequest ResolvePlayHistoryViewRequest");
        string providerSelection = ExtractBetween(viewModelCode, "private bool ShouldUseBeatorajaPlayHistoryProvider", "private string ResolveMainViewBeatorajaPlayHistoryScoreDbPath");
        string rowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "PlayHistoryRow.cs"));

        StringAssert.Contains(applyPlayHistoryView, "ShouldUseBeatorajaPlayHistoryProvider()");
        StringAssert.Contains(applyPlayHistoryView, "playHistoryReadCache.ReadBeatoraja(");
        StringAssert.Contains(applyPlayHistoryView, "periodRequest.ToBeatorajaReadRequest");
        StringAssert.Contains(applyPlayHistoryView, "PlayHistoryRow.ProjectBeatorajaRows");
        StringAssert.Contains(applyPlayHistoryView, "provider=\" + activePlayHistoryProvider");
        StringAssert.Contains(beatorajaProjection, "files.CreateBeatorajaPlayHistoryProjectionIndex");
        StringAssert.Contains(beatorajaProjection, "PlayHistoryRow.ProjectBeatorajaRows(readResult, projectionIndex)");
        StringAssert.Contains(providerSelection, "Settings.Default.UseBeatorajaScoreDb");
        StringAssert.Contains(providerSelection, "GetActiveScoreSourceForDiagnostics() == ActiveScoreSource.Beatoraja");
        Assert.IsFalse(providerSelection.Contains("GetScoreSnapshotForDiagnostics"));
        StringAssert.Contains(rowCode, "safeIndex.ResolveChartByMd5(string.Empty, sha256)");
        StringAssert.Contains(rowCode, "safeIndex.ResolvePlaylistReference(resolvedMd5, sha256)");
        StringAssert.Contains(rowCode, "FormatBeatorajaOption");
    }

    [TestMethod]
    public void PlayHistoryView_KeywordFilterUpdatedReusesProjectedState()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string applyPlayHistoryView = ExtractBetween(viewModelCode, "private void ApplyPlayHistoryView", "private IReadOnlyList<PlayHistoryRow> ApplyPlayHistoryKeywordFilterRows");
        string presentationOnly = ExtractBetween(viewModelCode, "private bool TryApplyPlayHistoryPresentationOnly", "private void ApplyPlayHistorySortedRows");
        string refreshTargets = ExtractBetween(viewModelCode, "private void RefreshPlayHistoryDisplayTargets", "internal void ReplacePlayHistoryDisplayTargetSetsForTest");
        string queueDisplayTargets = ExtractBetween(viewModelCode, "private void QueuePlayHistoryDisplayTargetsRefresh", "internal void ReplacePlayHistoryDisplayTargetSetsForTest");
        string queueDisplayTarget = ExtractBetween(viewModelCode, "private void QueuePlayHistoryDisplayTargetRefresh", "private bool IsCurrentPlayHistoryViewRequest");
        string applySortedRows = ExtractBetween(viewModelCode, "private void ApplyPlayHistorySortedRows", "private PlayHistoryProjectionResult CreatePlayHistoryProjectionResult");
        string flushPendingUiRefresh = ExtractBetween(viewModelCode, "private void FlushPendingUiRefresh", "private void ScheduleDeferredPlaylistReferenceApply");
        string playlistTablesHandler = ExtractBetween(viewModelCode, "listenerForBMSPlaylist.RegisterHandler(() => tables.BMSTables", "listenerForBMSPlaylistBMSTablesCollection.RegisterHandler");
        string playlistTablesCollectionHandler = ExtractBetween(viewModelCode, "listenerForBMSPlaylistBMSTablesCollection.RegisterHandler", "listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationCompletedVersion");
        string playlistEntriesHydrationHandler = ExtractBetween(viewModelCode, "listenerForBMSPlaylist.RegisterHandler(() => tables.PlaylistEntriesHydrationCompletedVersion", "listenerForBMSPlaylist.RegisterHandler(() => tables.IsWriteLockHeldBMSTables");
        string state = ExtractBetween(viewModelCode, "private sealed class PlayHistoryViewState", "private readonly struct SortSnapshot");

        StringAssert.Contains(applyPlayHistoryView, "requestedMode == viewUpdateMode.KeywordFilterUpdated");
        StringAssert.Contains(applyPlayHistoryView, "TryApplyPlayHistoryPresentationOnly");
        StringAssert.Contains(applyPlayHistoryView, "ApplyPlayHistoryDisplayTargetRows(projectedRows, displayTarget, requestId, displayTargetRevision, cancellationToken)");
        StringAssert.Contains(applyPlayHistoryView, "ApplyPlayHistoryKeywordFilterRows(targetRows");
        StringAssert.Contains(viewModelCode, "mode == viewUpdateMode.KeywordFilterUpdated && parameter is PlayHistoryViewRequest playHistoryKeywordRequest");
        StringAssert.Contains(viewModelCode, "ApplyPlayHistoryView(mode, requestedMode, parameter, viewBuildStopwatch);");
        StringAssert.Contains(presentationOnly, "state.AllProjectedRows");
        StringAssert.Contains(presentationOnly, "ApplyPlayHistoryDisplayTargetRows(state.AllProjectedRows, displayTarget, state.RequestId, displayTargetRevision, cancellationToken)");
        StringAssert.Contains(viewModelCode, "if (SelectedPlayHistoryDisplayTarget.UsesProjection)");
        StringAssert.Contains(presentationOnly, "state.FilterSourceRows");
        StringAssert.Contains(presentationOnly, "ApplyPlayHistoryKeywordFilterRows");
        StringAssert.Contains(presentationOnly, "requestedMode == viewUpdateMode.SortUpdated && keywordStateStale");
        StringAssert.Contains(presentationOnly, "requestedMode == viewUpdateMode.SortUpdated && targetStateStale");
        StringAssert.Contains(presentationOnly, "KeywordFilterIdentity");
        StringAssert.Contains(presentationOnly, "DisplayTargetIdentity");
        StringAssert.Contains(viewModelCode, "QueuePlayHistoryKeywordFilterRefresh");
        StringAssert.Contains(viewModelCode, "QueuePlayHistoryKeywordFilterRefresh(advanceRevision: false)");
        StringAssert.Contains(viewModelCode, "QueuePlayHistoryDisplayTargetRefresh");
        StringAssert.Contains(viewModelCode, "QueuePlayHistoryDisplayTargetRefresh(advanceRevision: false)");
        StringAssert.Contains(refreshTargets, "QueuePlayHistoryDisplayTargetRefresh();");
        StringAssert.Contains(queueDisplayTarget, "new PlayHistoryViewRequest(request.PeriodRequest, request.RequestId, keywordRevision, targetRevision)");
        StringAssert.Contains(queueDisplayTarget, "Interlocked.CompareExchange(ref playHistoryDisplayTargetQueuedRevision");
        StringAssert.Contains(queueDisplayTargets, "playHistoryDisplayTargetsRefreshRequestedRevision");
        StringAssert.Contains(queueDisplayTargets, "playHistoryDisplayTargetsRefreshCompletedRevision");
        StringAssert.Contains(queueDisplayTargets, "playHistoryDisplayTargetsRefreshScheduled");
        StringAssert.Contains(queueDisplayTargets, "if (refreshRevision == Interlocked.Read(ref playHistoryDisplayTargetsRefreshRequestedRevision))");
        StringAssert.Contains(queueDisplayTargets, "Schedule(Refresh);");
        StringAssert.Contains(applySortedRows, "QueuePlayHistoryDisplayTargetRefresh(advanceRevision: false)");
        StringAssert.Contains(flushPendingUiRefresh, "RefreshPlayHistoryDisplayTargets();");
        StringAssert.Contains(playlistTablesHandler, "QueuePlayHistoryDisplayTargetsRefresh();");
        StringAssert.Contains(playlistTablesCollectionHandler, "QueuePlayHistoryDisplayTargetsRefresh();");
        Assert.IsFalse(playlistTablesHandler.Contains("RefreshPlayHistoryDisplayTargets();"));
        Assert.IsFalse(playlistTablesCollectionHandler.Contains("RefreshPlayHistoryDisplayTargets();"));
        StringAssert.Contains(playlistEntriesHydrationHandler, "if (SelectedPlayHistoryDisplayTarget.UsesProjection)");
        StringAssert.Contains(playlistEntriesHydrationHandler, "QueuePlayHistoryDisplayTargetRefresh();");
        StringAssert.Contains(viewModelCode, "playHistoryKeywordFilterRevision");
        StringAssert.Contains(viewModelCode, "playHistoryKeywordFilterQueuedRevision");
        StringAssert.Contains(viewModelCode, "playHistoryDisplayTargetRevision");
        StringAssert.Contains(viewModelCode, "playHistoryDisplayTargetQueuedRevision");
        StringAssert.Contains(viewModelCode, "Interlocked.CompareExchange(ref playHistoryKeywordFilterQueuedRevision");
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
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string settingDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string editDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlayHistoryFolderDisplayPresetEditDialog.xaml"));
        string editDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "PlayHistoryFolderDisplayPresetEditDialog.cs"));
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string toolbar = ExtractBetween(xaml, "<Border BorderThickness=\"0\" Grid.Row=\"1\" Grid.ColumnSpan=\"1\" Grid.Column=\"1\"", "<Border DockPanel.Dock=\"Right\" CornerRadius=\"6\" BorderThickness=\"1\" BorderBrush=\"{DynamicResource App.StrongBorderBrush}\" Width=\"Auto\" Margin=\"0,0,2,0\" VerticalAlignment=\"Center\" FlowDirection=\"LeftToRight\" Visibility=\"{Binding IsPlaylistSummaryMode");
        string saveAndClose = ExtractBetween(settingDialogCode, "private async void SaveAndClose", "internal static bool ShouldResetSettingsOnCancel");
        string saveSettings = ExtractBetween(viewModelCode, "public async Task SaveSettings()", "public async Task SaveSettingsForInitialInitialize()");
        string saveSettingsCore = ExtractBetween(viewModelCode, "private async Task SaveSettingsCore", "public void SaveOperationModeForRestart");

        StringAssert.Contains(toolbar, "Visibility=\"{Binding IsPlayHistoryViewActive");
        StringAssert.Contains(toolbar, "ItemsSource=\"{Binding PlayHistoryDisplayTargets}\"");
        StringAssert.Contains(toolbar, "SelectedItem=\"{Binding SelectedPlayHistoryDisplayTarget, Mode=TwoWay}\"");
        StringAssert.Contains(toolbar, "DisplayMemberPath=\"DisplayName\"");
        StringAssert.Contains(viewModelCode, "nextItems.AddRange(playHistoryDisplayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSet));");
        StringAssert.Contains(viewModelCode, "nextItems.AddRange(playHistoryDisplayTargetSets.Select(PlayHistoryDisplayTargetItem.FromTargetSetProjectionOnly));");
        StringAssert.Contains(viewModelCode, ".Select(PlayHistoryDisplayTargetItem.FromPlaylist));");
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
        Assert.IsTrue(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(true, [new string('a', 32)]));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(false, [new string('a', 32)]));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(true, [" "]));
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
    public void LibraryFolderContextMenus_ExposeLightReloadAndFullReinitialize()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "MethodName=\"ReloadFileDiff\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "MethodName=\"ReinitializeLibrary\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Path=Resources.Reinitialize_library, Mode=OneWay"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Reinitialize_library));
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
        string code = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string dialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));
        string dialogXaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.xaml"));
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));

        string menuSnippet = xaml.Substring(xaml.IndexOf("Name=\"treeViewPlaylistRootContextMenuItemLoadPlaylistCollection\"", StringComparison.Ordinal), 1200);
        StringAssert.Contains(menuSnippet, "<Setter Property=\"MenuItem.StaysOpenOnClick\" Value=\"True\" />");
        StringAssert.Contains(menuSnippet, "<DataTrigger Binding=\"{Binding url}\" Value=\"{x:Null}\">");
        StringAssert.Contains(menuSnippet, "<Setter Property=\"MenuItem.StaysOpenOnClick\" Value=\"False\" />");
        StringAssert.Contains(code, "viewModel.EnqueueExternalPlaylistBMSTableImport(dataContext.url);");
        StringAssert.Contains(code, "viewModel.EnqueueExternalPlaylistBMSTableImport(uri);");
        StringAssert.Contains(dialogCode, "viewModel.EnqueueExternalPlaylistBMSTableImports(parseResult.ValidUris);");
        StringAssert.Contains(dialogCode, "ParsePlaylistUriInput(textBoxURIInput.Text)");
        StringAssert.Contains(dialogCode, "AppendUriInputLine(textBoxURIInput.Text, openFileDialog.FileName)");
        StringAssert.Contains(dialogXaml, "AcceptsReturn=\"True\"");
        StringAssert.Contains(dialogXaml, "VerticalContentAlignment=\"Top\"");
        StringAssert.Contains(dialogXaml, "VerticalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(dialogXaml, "HorizontalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(resources, "Playlist_import_progress_label_format");
        StringAssert.Contains(resources, "Playlist_import_progress_single_label");
        StringAssert.Contains(resources, "Playlist_uri_input_no_valid_uri");
        StringAssert.Contains(resources, "Playlist_uri_input_invalid_lines_format");
        foreach (string languageFile in Directory.GetFiles(Path.Combine(root, "lang"), "*.json"))
        {
            string languageJson = File.ReadAllText(languageFile);
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_label_format\"");
            StringAssert.Contains(languageJson, "\"Playlist_import_progress_single_label\"");
            StringAssert.Contains(languageJson, "\"Playlist_uri_input_no_valid_uri\"");
            StringAssert.Contains(languageJson, "\"Playlist_uri_input_invalid_lines_format\"");
        }
    }

    [TestMethod]
    public void SettingDialogBeatorajaScoreDbStrings_AreLocalized()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string resources = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.resx"));
        string resourceCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Resources.cs"));
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        StringAssert.Contains(viewModelCode, "Settings.Default.StandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidStandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "ownerViewModel.files?.HasOwnedChartUnderRealPath(dir) == true");
        Assert.IsFalse(viewModelCode.Contains("ownerViewModel.BMSFiles != null && ownerViewModel.BMSFiles.Any"));
    }

    [TestMethod]
    public void StandaloneLibraryMode_UsesPortableSongDbAndBuildsPlaylist()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string portablePathCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "PortableSettingsPath.cs"));
        string standaloneDbCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "StandaloneLibraryDatabase.cs"));

        StringAssert.Contains(portablePathCode, "DataDirectoryPath => Path.Combine(AppBaseDirectory, \"data\")");
        StringAssert.Contains(portablePathCode, "StandaloneSongDbPath => Path.Combine(DataDirectoryPath, \"song.db\")");
        StringAssert.Contains(standaloneDbCode, "FileMode.OpenOrCreate");
        StringAssert.Contains(standaloneDbCode, "BMSPlaylist.EnsureSchema(songDbPath)");
        StringAssert.Contains(viewModelCode, "StandaloneLibraryDatabase.EnsurePortableSongDb()");
        StringAssert.Contains(viewModelCode, "tables = new BMSPlaylist(");
        StringAssert.Contains(viewModelCode, "libraryProfile.SongDbPath");
        StringAssert.Contains(viewModelCode, "files.SearchTargets.AddRange(libraryProfile.SearchRoots)");
        StringAssert.Contains(viewModelCode, "return [];");
        StringAssert.Contains(viewModelCode, "temp_output_dir_full_path = Settings.Default.OperationModeLR2DB ? MainWindowViewModel.ResolveCustomFolderOutputDirectoryWithNotification(bmsTable, \"playlist property output directory notification\") : null;");
        string saveFollowup = ExtractBetween(
            viewModelCode,
            "internal async Task ApplyPostSaveUpdatesAsync()",
            "protected override void Dispose(bool disposing)");
        int headerCommitIndex = saveFollowup.IndexOf("ownerViewModel.tables.CommitBMSTableHeaderToDB(bmsTable);", StringComparison.Ordinal);
        int fullCommitIndex = saveFollowup.IndexOf("ownerViewModel.tables.CommitBMSTableWithEntriesToDB(bmsTable);", StringComparison.Ordinal);
        int detailRefreshIndex = saveFollowup.IndexOf("ownerViewModel.RefreshChartRowsViewForPlaylist(bmsTable);", StringComparison.Ordinal);
        int selectionReplaceIndex = saveFollowup.IndexOf("ownerViewModel.ReplaceCurrentPlaylistSelectionTable(sourceTable, bmsTable);", StringComparison.Ordinal);
        int folderSelectionRemapIndex = saveFollowup.IndexOf("ownerViewModel.RemapCurrentPlaylistFolderSelection(bmsTable, rewrittenFolders);", StringComparison.Ordinal);
        int lr2CustomFolderIndex = saveFollowup.IndexOf("if (Settings.Default.OperationModeLR2DB)", StringComparison.Ordinal);
        Assert.IsTrue(headerCommitIndex >= 0);
        Assert.IsTrue(fullCommitIndex >= 0);
        Assert.IsTrue(detailRefreshIndex >= 0);
        Assert.IsTrue(selectionReplaceIndex >= 0);
        Assert.IsTrue(folderSelectionRemapIndex >= 0);
        Assert.IsFalse(saveFollowup.Contains("prefixChanged && !externalResync"));
        Assert.IsTrue(lr2CustomFolderIndex > headerCommitIndex);
        Assert.IsTrue(lr2CustomFolderIndex > fullCommitIndex);
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs")), "public List<string> SearchTargets { get; set; } = [];");
        Assert.IsFalse(viewModelCode.Contains("throw new NotImplementedException();"));
    }

    [TestMethod]
    public void Lr2PlaybackPlayer_IsIndependentFromLibraryOperationMode()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
            "ColumnsSettingsChartRowsView");
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
        StringAssert.Contains(initialize, "else if (Settings.Default.UsePlayerLR2body && File.Exists(settingDialog.LR2bodyPath))");
        StringAssert.Contains(initialize, "new LR2body(settingDialog.LR2bodyPath, CreateLR2PlayerConfig())");
        Assert.IsFalse(initialize.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(saveFollowup, "else if (!forceInternalPlayerForStandaloneModeChange && Settings.Default.UsePlayerLR2body)");
        StringAssert.Contains(saveFollowup, "ownerViewModel.bmsPlayer = new LR2body(LR2bodyPath, new LR2Config(Settings.Default.LR2ConfigXmlPath));");
        Assert.IsFalse(saveFollowup.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(viewModelCode, "private LR2Config CreateLR2PlayerConfig()");
        StringAssert.Contains(viewModelCode, "private bool IsLR2PlayerRootPathValid(string value)");
        Assert.IsFalse(lr2RootPathGetter.Contains("Settings.Default.LR2RootPath = null"));
        StringAssert.Contains(lr2RootPathGetter, "return Settings.Default.LR2RootPath;");
    }

    [TestMethod]
    public void SettingDialogOperationModeChange_ConfirmsAndRestartsAfterInitialization()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        StringAssert.Contains(saveAndClose, "settingDialog.Visibility = Visibility.Hidden;");
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
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));
        string initialDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml"));
        string initialDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "InitialSetupLanguageDialog.xaml.cs"));
        string initialize = ExtractBetween(
            viewModelCode,
            "public async void Initialize()",
            "public void SetuBMplayPanel()");
        string validationFailure = ExtractBetween(
            initialize,
            "if (!settingDialog.CheckValidation(out string startupValidationErrorMessage))",
            "if (Settings.Default.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync())");

        Assert.IsFalse(validationFailure.Contains("DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings,"));
        StringAssert.Contains(validationFailure, "RaiseInteractionMessageOnUiThread(new InteractionMessage(\"InitialSetupLanguageDialog\"));");
        StringAssert.Contains(validationFailure, "DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings_check");
        StringAssert.Contains(validationFailure, "RaiseInteractionMessageOnUiThread(new InteractionMessage(\"InitializationException\"));");
        Assert.IsTrue(validationFailure.IndexOf("InitialSetupLanguageDialog", StringComparison.Ordinal) < validationFailure.IndexOf("Msg_init_settings_check", StringComparison.Ordinal));

        StringAssert.Contains(mainWindow, "MessageKey=\"InitialSetupLanguageDialog\"");
        StringAssert.Contains(mainWindow, "TargetObject=\"{Binding ElementName=initialSetupLanguageDialog, Mode=OneWay}\"");
        StringAssert.Contains(mainWindow, "<v:InitialSetupLanguageDialog x:Name=\"initialSetupLanguageDialog\"");
        StringAssert.Contains(initialDialog, "ItemsSource=\"{Binding settingDialog.Languages, Mode=OneWay}\"");
        StringAssert.Contains(initialDialog, "SelectedItem=\"{Binding Path=settingDialog.Language}\"");
        StringAssert.Contains(initialDialog, "Resources.Msg_init_settings");
        StringAssert.Contains(initialDialog, "Resources.InitialSetupLanguageDialogTitle");
        StringAssert.Contains(initialDialog, "Resources.InitialSetupLanguageDialogContinue");
        StringAssert.Contains(initialDialogCode, "settingDialog.Visibility = Visibility.Visible;");
    }

    [TestMethod]
    public void MainWindowViewModel_RaisesMessagesThroughUiThreadHelper()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string helperBody = ExtractMethodBody(viewModelCode, "internal void RaiseInteractionMessageOnUiThread(InteractionMessage message)");

        StringAssert.Contains(helperBody, "base.Messenger.Raise(message);");
        StringAssert.Contains(helperBody, "dispatcher.Invoke(DispatcherPriority.Normal");
        Assert.AreEqual(1, CountOccurrences(viewModelCode, "base.Messenger.Raise("));
        Assert.IsFalse(viewModelCode.Contains("ownerViewModel.Messenger.Raise("));
        Assert.IsFalse(mainWindowCode.Contains(".Messenger.Raise("));
    }

    [TestMethod]
    public void StartupReloadProgress_UsesSerializedOperationTokens()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string reloadFileDiff = ExtractBetween(
            viewModelCode,
            "public async void ReloadFileDiff()",
            "public async void ReinitializeLibrary()");
        string initialize = ExtractBetween(
            viewModelCode,
            "public async void Initialize()",
            "public void SetuBMplayPanel()");
        string endSuppression = ExtractBetween(
            viewModelCode,
            "private void EndUiUpdateSuppression()",
            "private bool QueueStartupBackgroundTask");
        string deferredExternalSync = ExtractBetween(
            viewModelCode,
            "private void StartDeferredExternalPlaylistSync(string reason, bool fromReloadTables, Action<BMSPlaylist.PlaylistTableUpdateContext> updateCallbackAction, long operationToken)",
            "public PlaylistPropertyDialogViewModel playlistPropertyDialog");

        StringAssert.Contains(viewModelCode, "public bool IsLibraryOperationInProgress");
        StringAssert.Contains(viewModelCode, "private long GetActiveStartupProgressOperationToken()");
        StringAssert.Contains(viewModelCode, "private bool IsStartupProgressOperationTokenCurrent(long operationToken)");
        StringAssert.Contains(viewModelCode, "playlistSyncProgressUiVersion");
        StringAssert.Contains(viewModelCode, "if (uiVersion != Interlocked.Read(ref playlistSyncProgressUiVersion))");
        Assert.IsTrue(reloadFileDiff.IndexOf("await _semaphore.WaitAsync();", StringComparison.Ordinal) < reloadFileDiff.IndexOf("StartStartupProgressOperation(StartupProgressOperationKind.ReloadFileDiff)", StringComparison.Ordinal));
        Assert.IsTrue(initialize.IndexOf("files = new BMSLibrary", StringComparison.Ordinal) < initialize.IndexOf("StartStartupProgressOperation(StartupProgressOperationKind.Startup)", StringComparison.Ordinal));
        StringAssert.Contains(initialize, "StartDeferredExternalPlaylistSync(\"Initialize\", fromReloadTables: false, CreatePlaylistReferenceReplaceUpdateCallback(), operationToken)");
        StringAssert.Contains(initialize, "queueBeatorajaBmtExportAfterHydration: Settings.Default.SkipInitPlaylistLoad");
        StringAssert.Contains(deferredExternalSync, "tables.QueueBeatorajaBmtExportAll(\"DeferredExternalSync:\" + reason)");
        StringAssert.Contains(initialize, "initialSetupCompletionMessagePending = true;");
        Assert.IsFalse(initialize.Contains("Resources.Msg_init_completed"));
        StringAssert.Contains(initialize, "await EnsureAppSchemaRepairApprovedForStartupAsync()");
        string appSchemaStartupPreflight = ExtractBetween(
            viewModelCode,
            "private async Task<bool> EnsureAppSchemaRepairApprovedForStartupAsync()",
            "private void ApplyAppSchemaRepairForStartupOrThrow");
        string gatewayCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryDbGateway.cs"));
        string repairAppOwnedSchema = ExtractBetween(
            gatewayCode,
            "internal static void RepairAppOwnedSchema(LR2SongDBExtended songDb)",
            "internal static void EnsureAppOwnedSchema(LR2SongDBExtended songDb)");
        StringAssert.Contains(appSchemaStartupPreflight, "ShowUiConfirmation(BuildAppSchemaRepairWarningMessage(preflightResult)");
        StringAssert.Contains(appSchemaStartupPreflight, "await Task.Run(delegate");
        StringAssert.Contains(appSchemaStartupPreflight, "ApplyAppSchemaRepairForStartupOrThrow(appSchemaPreflightService, preflightResult);");
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        StringAssert.Contains(restartSaveMethod, "Settings.Default.Reload();");
        StringAssert.Contains(restartSaveMethod, "Settings.Default.OperationModeLR2DB = operationMode;");
        StringAssert.Contains(restartSaveMethod, "Settings.Default.Save();");
        Assert.IsFalse(restartSaveMethod.Contains("ResetSettings();"));
        Assert.IsFalse(restartSaveMethod.Contains("SetOperationModeSelection"));
        Assert.IsFalse(restartSaveMethod.Contains("RaisePropertyChanged"));
        Assert.IsFalse(restartSaveMethod.Contains("CheckValidation("));
        Assert.IsFalse(restartSaveMethod.Contains("necessaryStepsAfterSaved"));
    }

    [TestMethod]
    public void SettingDialogModeSpecificGetters_DoNotClearPersistedSettings()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        StringAssert.Contains(lr2CustomFolderGetter, "return Settings.Default.LR2CustomFolderOutputBaseDir;");
        StringAssert.Contains(bmsInstallDirGetter, "return Settings.Default.BMSInstallDir;");
        StringAssert.Contains(lr2CustomFolderRootGetter, "return Settings.Default.LR2CustomFolderOutputBaseDirRootType;");
    }

    [TestMethod]
    public void SettingDialogAudioAndPlayerBindings_DoNotCreateFalsePendingChanges()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        StringAssert.Contains(playerDeviceGetter, "return ResolvePlayerDeviceDescriptor().Driver ?? Settings.Default.PlayerDevice;");
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
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string saveOrCancelDecision = ExtractBetween(
            viewModelCode,
            "public RestartMode IsNeedRestartForSaveOrCancel()",
            "public class PlaylistPropertyDialogViewModel");

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
        StringAssert.Contains(viewModelCode, "HasPathSettingValueChanged(tempLR2RootPath, Settings.Default.LR2RootPath");
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string settingDialogCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string settingDialogXaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "SettingDialog.xaml"));
        string playlistCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSPlaylist.cs"));
        string runtimeSync = ExtractBetween(
            viewModelCode,
            "private void ApplyRuntimeSearchRootsForCurrentMode()",
            "private bool IsLR2SongDBPathValid()");
        string rootAdd = ExtractBetween(
            viewModelCode,
            "private void AddBMSDirectoryToRootFolderAndSave",
            "private void AddBMSDirectoryToSearchRoots");
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
        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = [.. GetStandaloneBmsRootPathsFromSettings()];");
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
        StringAssert.Contains(manualResyncClickHandler, "settingDialog.Visibility = Visibility.Hidden;");
        StringAssert.Contains(manualResyncClickHandler, "await Dispatcher.Yield(DispatcherPriority.Background);");
        StringAssert.Contains(playlistCode, "RepairMissingCustomFolderOutputsAfterHydration(reason, verifyRootOutputDirectoryRows)");
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
        StringAssert.Contains(buildPostSaveImpact, "tempOperationModeLR2DB != Settings.Default.OperationModeLR2DB");
        StringAssert.Contains(viewModelCode, "private enum SettingsPostSaveImpact");
        StringAssert.Contains(viewModelCode, "BuildSettingsPostSaveImpact(bool customFolderSearchRootSyncNeeded)");
        StringAssert.Contains(viewModelCode, "SettingsPostSaveImpact.Lr2CoreSync");
        StringAssert.Contains(viewModelCode, "SettingsPostSaveImpact.ExternalLr2FolderRowsSync");
        StringAssert.Contains(postSaveSteps, "impact.HasFlag(SettingsPostSaveImpact.Lr2CoreSync)");
        StringAssert.Contains(postSaveSteps, "else if (impact.HasFlag(SettingsPostSaveImpact.ExternalLr2FolderRowsSync))");
        StringAssert.Contains(postSaveSteps, "\"settings_post_save\"");
        Assert.IsFalse(postSaveSteps.Contains("tempEnableLR2SongDbSync"));
        Assert.IsFalse(buildPostSaveImpact.Contains("tempEnableLR2SongDbSync"));
        StringAssert.Contains(buildPostSaveImpact, "tempLR2RootPath, Settings.Default.LR2RootPath");
        StringAssert.Contains(buildPostSaveImpact, "tempLR2CustomFolderOutputDir, Settings.Default.LR2CustomFolderOutputBaseDir");
        StringAssert.Contains(buildPostSaveImpact, "tempLR2CustomFolderAsRootOutputDir, Settings.Default.LR2CustomFolderOutputBaseDirRootType");
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string addStandalone = ExtractBetween(
            viewModelCode,
            "private void AddStandaloneBmsRootPath",
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
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
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
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string bmsLibraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string autoRenameClick = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuItemAutoRenameFolderClick",
            "private void tableContextMenuItemRenameBMSFileClick");
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
            "private static bool IsCustomTablePlaylistEditableProperty");

        StringAssert.Contains(autoRenameClick, "GetSelectedChartTargets(ChartOperationCapabilities.MoveInLibrary)");
        StringAssert.Contains(autoRenameClick, "CreateChartOperationTargetSnapshot(targets, ChartOperationCapabilities.MoveInLibrary)");
        StringAssert.Contains(autoRenameClick, "targetSnapshot.Charts.Count > 0");
        Assert.IsFalse(autoRenameClick.Contains("targetSnapshot.ChartFiles.Count"));
        StringAssert.Contains(autoRenameClick, "AutoRenameChartFolders(targetSnapshot)");
        Assert.IsFalse(autoRenameClick.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(autoRenameClick.Contains("GetSelectedBmsChartFiles(ChartOperationCapabilities.None)"));
        StringAssert.Contains(contextMenuOpening, "hasBmsSelection || hasBmsonSelection");
        StringAssert.Contains(autoRenameAll, "files?.HasAutoRenameAllChartFolderTargets(parentDir) != true");
        StringAssert.Contains(autoRenameAll, "files?.AutoRenameAllChartFolders(parentDir, UpdateFolderAutoRenameProgressStatus) == true");
        Assert.IsFalse(autoRenameAll.Contains("IEnumerable<BeMusicSeeker.Models.BMSFile> enumerable = BMSFiles;"));
        StringAssert.Contains(autoRenameAllModel, "CreateOwnedRealPathChartDirectoriesUnsafe(parentDir)");
        StringAssert.Contains(autoRenameAllModel, "BuildAutoRenamePlansForSourceFolders");
        Assert.IsFalse(bmsLibraryCode.Contains("CreateLibraryChartSnapshotsForFolderOperations"));
        Assert.IsFalse(autoRenameAllModel.Contains("CreateOwnedSubtreeChartSnapshot"));
        Assert.IsFalse(autoRenameAllModel.Contains("BMSFiles ??"));
        Assert.IsFalse(autoRenameAllModel.Contains("files?.BmsonSongs"));
        StringAssert.Contains(cellEditEnded, "viewModel.CreateRenameChartFolderTargetSnapshot(target)");
        StringAssert.Contains(cellEditEnded, "viewModel.RenameChartFolder(targetSnapshot, newFolder)");
        Assert.IsFalse(cellEditEnded.Contains("viewModel.RenameChartFolder(target, newFolder)"));
    }

    [TestMethod]
    public void PlaylistLibraryIndexUsesOwnedResolveIndex()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string bmsLibraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string createPlaylistLibraryIndex = ExtractBetween(
            viewModelCode,
            "private PlaylistLibraryIndexSnapshot CreatePlaylistLibraryIndexSnapshot",
            "private PlaylistLibraryIndexSnapshot GetOrCreatePlaylistLibraryIndexSnapshot");
        string resolveIndexHelper = ExtractBetween(
            bmsLibraryCode,
            "private PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshotUnsafe",
            "private ChartInfoHydrationOwnerSummary CreateChartInfoHydrationOwnerSummaryUnsafe");

        StringAssert.Contains(createPlaylistLibraryIndex, "GetPlaylistLibraryResolveIndexSnapshot(cancellationToken");
        Assert.IsFalse(createPlaylistLibraryIndex.Contains("foreach (BeMusicSeeker.Models.BMSFile file in BMSFiles"));
        Assert.IsFalse(createPlaylistLibraryIndex.Contains("files?.BmsonSongs"));
        StringAssert.Contains(resolveIndexHelper, "ownedChartCollection.CreatePlaylistLibraryResolveRefSnapshot(cancellationToken.ThrowIfCancellationRequested)");
        StringAssert.Contains(resolveIndexHelper, "PlaylistLibraryResolveIndexSnapshot.FromLibraryChartRefs(refs, cancellationToken.ThrowIfCancellationRequested)");
    }

    [TestMethod]
    public void SharedTransientStatePruneUsesOwnedRuntimeStatePrimaryKeySnapshot()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string bmsLibraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string notificationHandler = ExtractBetween(
            viewModelCode,
            "private NormalLibraryRefreshNotificationBatch ApplyNormalLibraryRefreshNotification",
            "private bool ApplyLatestNormalLibraryRefreshNotification");
        string bmsonSync = ExtractBetween(
            viewModelCode,
            "private BmsonLibraryRowCacheSyncResult SyncBmsonLibraryRowCache",
            "internal static bool HasBmsonLibrarySortKeyChangedForTest");
        string pruneHelper = ExtractBetween(
            viewModelCode,
            "private void PruneSharedChartTransientStateCacheToCurrentOwnedCharts",
            "private void ClearSharedChartTransientStates");
        string modelKeyHelper = ExtractBetween(
            bmsLibraryCode,
            "internal HashSet<string> CreateOwnedChartRuntimeStatePrimaryKeySnapshot",
            "private void EnsureOwnedChartCollectionBuiltUnsafe");

        StringAssert.Contains(notificationHandler, "PruneSharedChartTransientStateCacheToCurrentOwnedCharts()");
        Assert.IsFalse(notificationHandler.Contains("files?.BMSFiles"));
        Assert.IsFalse(notificationHandler.Contains("files?.BmsonSongs"));
        StringAssert.Contains(bmsonSync, "files?.CreateNormalLibrarySourceStorageOwnerView()");
        Assert.IsFalse(bmsonSync.Contains("files?.BmsonSongs"));
        Assert.IsFalse(bmsonSync.Contains("OrderBy(song => song.path"));
        StringAssert.Contains(bmsonSync, "PruneSharedChartTransientStateCacheToCurrentOwnedCharts()");
        Assert.IsFalse(bmsonSync.Contains("PruneSharedChartTransientStateCacheToCurrentStorageRows"));
        StringAssert.Contains(pruneHelper, "files?.CreateOwnedChartRuntimeStatePrimaryKeySnapshot()");
        Assert.IsFalse(pruneHelper.Contains("foreach (BeMusicSeeker.Models.BMSFile"));
        Assert.IsFalse(pruneHelper.Contains("foreach (LR2SongDBExtended.bmson_song"));
        StringAssert.Contains(modelKeyHelper, "rwlockBMSFiles.GetReaderGuard()");
        StringAssert.Contains(modelKeyHelper, "ownedChartCollection.CreateChartRuntimeStatePrimaryKeySnapshot()");
        Assert.IsFalse(modelKeyHelper.Contains("CreateOwnedInstallDestinationRuntimeStateKeySnapshotUnsafe()"));
    }

    [TestMethod]
    public void NormalLibraryRefreshNotificationOwnsStorageRowSourceRefresh()
    {
        string root = FindRepositoryRoot();
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string notificationVersionHandler = ExtractBetween(
            viewModelCode,
            "listenerForBMSLibrary.RegisterHandler(() => files.NormalLibraryRefreshNotificationVersion",
            "listenerForBMSLibrary.RegisterHandler(() => files.LibraryInitializationProgress");
        string notificationSyncHelper = ExtractBetween(
            viewModelCode,
            "private BmsonLibraryRowCacheSyncResult SyncNormalLibraryStorageRowCachesForRefreshNotification",
            "private static bool ShouldConsumeNormalLibrarySourceGenerationForOwnedCollectionVersion");
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
        StringAssert.Contains(notificationSyncHelper, "PruneRegularBmsLibraryRowCacheByBmsFiles(sourceOwnerView?.BmsFiles)");
        Assert.IsFalse(notificationSyncHelper.Contains("files?.BMSFiles"));
        StringAssert.Contains(notificationSyncHelper, "SyncBmsonLibraryRowCache(sourceOwnerView)");
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
        string libraryCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSLibrary.cs"));
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
        string libraryCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSLibrary.cs"));
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string libraryCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string refreshChartRowsView = ExtractBetween(
            viewModelCode,
            "private void RefreshChartRowsView",
            "private static bool TryNormalizeNormalLibrarySortCacheColumn");
        string virtualSubsetSource = ExtractMethodBody(viewModelCode, "private bool TryGetVirtualChartSubsetSourceFiles");
        string virtualDuplicateSource = ExtractBetween(
            viewModelCode,
            "private static bool TryGetVirtualDuplicateSourceChartsCore",
            "private List<ChartFile> CreateDuplicateChartFileSnapshot");

        StringAssert.Contains(refreshChartRowsView, "bool virtualChartSubsetRequiredFailure = false");
        StringAssert.Contains(refreshChartRowsView, "IsVirtualChartSubsetRequiredForRequest(mode, treeViewFilterTypeSelected)");
        StringAssert.Contains(refreshChartRowsView, "if (!virtualChartSubsetRequiredFailure)");
        Assert.IsFalse(refreshChartRowsView.Contains("case viewUpdateMode.DuplicateFilterSelected:"));
        Assert.IsFalse(refreshChartRowsView.Contains("case viewUpdateMode.FileMissingFilterSelected:"));
        Assert.IsFalse(refreshChartRowsView.Contains("case viewUpdateMode.NewlyInstalledFolderSelected:"));
        StringAssert.Contains(virtualSubsetSource, "sourceCharts = ChartFilesGarbled;");
        StringAssert.Contains(virtualSubsetSource, "sourceCharts = ChartFilesGarbledFixed;");
        StringAssert.Contains(virtualSubsetSource, "sourceCharts = ChartFilesUnregistered;");
        Assert.IsFalse(viewModelCode.Contains("CreateBmsChartSnapshot("));
        Assert.IsFalse(viewModelCode.Contains("private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesGarbled"));
        Assert.IsFalse(viewModelCode.Contains("private IEnumerable<BeMusicSeeker.Models.BMSFile> BMSFilesUnregistered"));
        Assert.IsFalse(libraryCode.Contains("public IEnumerable<BMSFile> BMSFilesGarbled"));
        Assert.IsFalse(libraryCode.Contains("public List<BMSFile> BMSFilesUnregistered"));
        StringAssert.Contains(libraryCode, "internal IEnumerable<ChartFile> ChartFilesGarbled");
        StringAssert.Contains(libraryCode, "internal IEnumerable<ChartFile> ChartFilesUnregistered");
        StringAssert.Contains(virtualDuplicateSource, "DuplicateGroup");
        StringAssert.Contains(virtualDuplicateSource, "CreateDuplicateChartFileSnapshot(groupSnapshot)");
        Assert.IsFalse(virtualDuplicateSource.Contains("List<BeMusicSeeker.Models.BMSFile>"));
        Assert.IsFalse(virtualDuplicateSource.Contains("parameter as List<BeMusicSeeker.Models.BMSFile>"));
    }

    [TestMethod]
    public void ChartContextMenu_BmsOnlyAndChartCommonHandlersUseExpectedSelectionHelpers()
    {
        string mainWindowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.cs"));
        string contextMenuResource = ExtractBetween(
            mainWindowCode,
            "private bool TryGetTableContextMenuResource",
            "private void _renewBMSPlayerControlInfo");
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
            "private void tableContextMenuItemConvertToAudioFileClick",
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
        StringAssert.Contains(resourceHealthClick, "CreateChartOperationTargetSnapshot(targets, ChartOperationCapabilities.RunResourceHealthCheck)");
        Assert.IsFalse(resourceHealthClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        StringAssert.Contains(resourceHealthIgnoreClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthIgnoreClick, "CreateChartOperationTargetSnapshot(targets, ChartOperationCapabilities.RunResourceHealthCheck)");
        Assert.IsFalse(resourceHealthIgnoreClick.Contains("resourceTargets.MaterializeCompatibilityFiles()"));
        StringAssert.Contains(resourceHealthUnignoreClick, "GetSelectedChartTargets(ChartOperationCapabilities.RunResourceHealthCheck)");
        StringAssert.Contains(resourceHealthUnignoreClick, "CreateChartOperationTargetSnapshot(targets, ChartOperationCapabilities.RunResourceHealthCheck)");
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
        string mainWindowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.cs"));
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
        StringAssert.Contains(forceInstall, "viewModel.ForceInstallPendingCharts(targets)");
        Assert.IsFalse(forceInstall.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(manualInstall, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(manualInstall, "viewModel.ManualInstallPendingCharts(targets)");
        Assert.IsFalse(manualInstall.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(deletePackages, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(deletePackages, "viewModel.RemovePendingPackages(selectedPendingTargets)");
        StringAssert.Contains(deletePackages, "viewModel.RemoveInstalledPackageRecords(selectedInstalledTargets)");
        Assert.IsFalse(deletePackages.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        Assert.IsFalse(deletePackages.Contains("CreateChartOperationTargetSnapshot"));
        Assert.IsFalse(deletePackages.Contains("GetSelectedChartCompatibilityAdapters"));
        StringAssert.Contains(estimateSearch, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(estimateSearch, "viewModel.CreatePendingInstallDestinationTargetSnapshot(targets)");
        StringAssert.Contains(estimateSearch, "snapshot.MaterializeLooseEntries()");
        StringAssert.Contains(estimateSearch, "viewModel.SearchInstallDestinationForPendingCharts(snapshot)");
        Assert.IsFalse(estimateSearch.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(mergeSearch, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(mergeSearch, "viewModel.CreatePendingInstallDestinationTargetSnapshot(targets)");
        StringAssert.Contains(mergeSearch, "snapshot.MaterializeLooseEntries()");
        StringAssert.Contains(mergeSearch, "viewModel.SearchMergeDestinationForPendingCharts(snapshot)");
        Assert.IsFalse(mergeSearch.Contains("GetSelectedPendingChartCompatibilityAdapters"));
        StringAssert.Contains(openInstallDestination, "GetSelectedChartTargets(ChartOperationCapabilities.UpdateInstallDestination, isPendingSection: true)");
        StringAssert.Contains(openInstallDestination, "TryResolveInstallDestination(targets[0].Chart");
        Assert.IsFalse(openInstallDestination.Contains("GetSelectedPendingChartCompatibilityAdapters"));
    }

    [TestMethod]
    public void PlaylistDropReferenceRefreshUsesChartTargets()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string addChartRows = ExtractBetween(
            viewModelCode,
            "internal void AddChartRowsToFolderBMSTable",
            "internal static bool ShouldPreservePlaylistEntryForRootFolderDrop");
        string libraryCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string addReferenceCharts = ExtractBetween(
            libraryCode,
            "internal void AddReferenceBMSTablesToCharts",
            "internal void RefreshReferenceDisplayForTable");

        StringAssert.Contains(addChartRows, "List<ChartFile> resolvedCharts");
        StringAssert.Contains(addChartRows, "files.AddReferenceBMSTablesToCharts(bmsTable, resolvedCharts)");
        Assert.IsTrue(addChartRows.IndexOf("files.AddReferenceBMSTablesToCharts(bmsTable, resolvedCharts)", StringComparison.Ordinal) < addChartRows.IndexOf("RefreshChartRowsViewForPlaylist(bmsTable)", StringComparison.Ordinal));
        Assert.IsFalse(addChartRows.Contains("resolvedBmsFiles"));
        StringAssert.Contains(addReferenceCharts, "ReplacePlaylistReferenceIndexTable(table)");
        Assert.IsFalse(addReferenceCharts.Contains("AddReferenceBMSTableToCharts(table, charts)"));
        Assert.IsFalse(addReferenceCharts.Contains("IEnumerable<BMSFile>"));
    }

    [TestMethod]
    public void PendingInstallDestinationCellEditUsesChartTargets()
    {
        string mainWindowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.cs"));
        string editBeginning = ExtractBetween(
            mainWindowCode,
            "private void customTableView_CellEditBeginning",
            "private async void customTableView_CellActionRequested");
        string editEnded = ExtractBetween(
            mainWindowCode,
            "private void customTableView_CellEditEnded",
            "private static bool IsCustomTablePlaylistEditableProperty");

        StringAssert.Contains(editBeginning, "GridRowResolver.TryGetChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target)");
        StringAssert.Contains(editBeginning, "target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination)");
        Assert.IsFalse(editBeginning.Contains("GetCompatibilityBmsFile"));
        StringAssert.Contains(editEnded, "GridRowResolver.TryGetChartOperationTarget(e.Row, GetCurrentChartOperationSourceScope(), out ChartOperationTarget target)");
        StringAssert.Contains(editEnded, "viewModel.CreatePendingInstallDestinationEditTargetSnapshot(target)");
        StringAssert.Contains(editEnded, "viewModel.SetPendingInstallDestination(targetSnapshot, destinationDirectory)");
        Assert.IsFalse(editEnded.Contains("GetCompatibilityBmsFile"));
    }

    [TestMethod]
    public void RepairInstalledLocationHandlersUseChartTargets()
    {
        string mainWindowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.cs"));
        string searchRepair = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuSearchCorrectInstallationDirectoryChartsClick",
            "private void tableContextMenuFixInstallationDirectoryClick");
        string fixRepair = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuFixInstallationDirectoryClick",
            "private void tableContextMenuItemDeleteEntryClick");

        StringAssert.Contains(searchRepair, "GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation)");
        StringAssert.Contains(searchRepair, "viewModel.CreateRepairInstalledLocationTargetSnapshot(targets)");
        StringAssert.Contains(searchRepair, "repairTargets.MaterializeRepairEntries()");
        StringAssert.Contains(searchRepair, "viewModel.SearchCorrectInstallationDirectoryCharts(repairTargets)");
        Assert.IsFalse(searchRepair.Contains("GetSelectedChartCompatibilityAdapters"));
        StringAssert.Contains(fixRepair, "GetSelectedChartTargets(ChartOperationCapabilities.RepairInstalledLocation)");
        StringAssert.Contains(fixRepair, "viewModel.CreateRepairInstalledLocationTargetSnapshot(targets)");
        StringAssert.Contains(fixRepair, "repairTargets.HasInstallDestination");
        Assert.IsFalse(fixRepair.Contains("repairTargets.MaterializeRepairEntries()"));
        StringAssert.Contains(fixRepair, "viewModel.FixInstallationDirectoryCharts(repairTargets)");
        Assert.IsFalse(fixRepair.Contains("GetSelectedChartCompatibilityAdapters"));
        Assert.IsFalse(fixRepair.Contains("target.Chart?.InstallDestination"));
    }

    [TestMethod]
    public void FullScanInstallDestinationClearUsesRepairTargetSnapshot()
    {
        string mainWindowCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.cs"));
        string clearInstallDestination = ExtractBetween(
            mainWindowCode,
            "private void tableContextMenuRemoveInstallDestinationClick",
            "private void tableContextMenuSearchCorrectInstallationDirectoryChartsClick");

        StringAssert.Contains(clearInstallDestination, "GetSelectedChartTargets(capability)");
        StringAssert.Contains(clearInstallDestination, "CreateRepairInstalledLocationTargetSnapshot(targets)");
        StringAssert.Contains(clearInstallDestination, "repairTargets?.MaterializeRepairEntries()");
        StringAssert.Contains(clearInstallDestination, "pendingInstallTargets?.MaterializeLooseEntries()");
        StringAssert.Contains(clearInstallDestination, "viewModel.ClearInstallDestinationForCharts(repairTargets)");
        Assert.IsFalse(clearInstallDestination.Contains("viewModel.ClearInstallDestinationForCharts(repairTargets.ChartFiles)"));
        Assert.IsFalse(clearInstallDestination.Contains("repairTargets.ChartFiles"));
        Assert.IsFalse(clearInstallDestination.Contains("GetSelectedChartCompatibilityAdapters"));
    }

    [TestMethod]
    public void StartupInitialize_ReleasesSemaphoreWhenFileInitializationFails()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string fileInitializeBlock = ExtractBetween(
            viewModelCode,
            "startupReadyDataReached = false;",
            "if (((App)System.Windows.Application.Current).firstStartup)");

        StringAssert.Contains(fileInitializeBlock, "files.InitializeStartup");
        StringAssert.Contains(fileInitializeBlock, "FailStartupProgressOperation(ex.Message);");
        StringAssert.Contains(fileInitializeBlock, "_semaphore.Release();");
        StringAssert.Contains(fileInitializeBlock, "SetStartupUiInteractionBlocked(false);");
        StringAssert.Contains(fileInitializeBlock, "new InteractionMessage(\"InitializationException\")");
        Assert.IsFalse(fileInitializeBlock.Contains("throw;"));
    }

    [TestMethod]
    public void PostStartupWarmup_PrioritizesOwnedAdjacentIndexesBeforeVirtualSortPrewarm()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string scheduler = ExtractBetween(
            viewModelCode,
            "private void SchedulePostStartupBestEffortWarmups",
            "private Task SchedulePostStartupOwnedAdjacentIndexWarmup");
        string ownedWarmup = ExtractBetween(
            viewModelCode,
            "private void RunPostStartupOwnedAdjacentIndexWarmup",
            "private void ScheduleVirtualNormalLibraryOrderPrewarm");
        string virtualWarmup = ExtractBetween(
            viewModelCode,
            "private void RunVirtualNormalLibraryOrderPrewarm",
            "private void LogVirtualNormalLibraryRouteSkipped");

        int ownedSchedule = scheduler.IndexOf("SchedulePostStartupOwnedAdjacentIndexWarmup(reason)", StringComparison.Ordinal);
        int virtualSchedule = scheduler.IndexOf("ScheduleVirtualNormalLibraryOrderPrewarm(reason, ownedAdjacentIndexWarmupTask)", StringComparison.Ordinal);
        StringAssert.Contains(scheduler, "IsVirtualNormalLibraryOrderPrewarmRunning()");
        StringAssert.Contains(scheduler, "skipped=virtual_order_prewarm_running");
        Assert.IsTrue(ownedSchedule >= 0);
        Assert.IsTrue(virtualSchedule > ownedSchedule);
        Assert.IsFalse(ownedWarmup.Contains("Wait()"));

        int realPathWarmup = ownedWarmup.IndexOf("WarmOwnedRealPathDirectoryView", StringComparison.Ordinal);
        int installDestinationOverlayWarmup = ownedWarmup.IndexOf("WarmInstallDestinationOverlaySnapshot", StringComparison.Ordinal);
        int primaryHashWarmup = ownedWarmup.IndexOf("WarmInstalledPrimaryHashLookup", StringComparison.Ordinal);
        int playlistSummaryWarmup = ownedWarmup.IndexOf("WarmPlaylistSummaryOwnedHashSnapshot", StringComparison.Ordinal);
        Assert.IsTrue(realPathWarmup >= 0);
        Assert.IsTrue(installDestinationOverlayWarmup > realPathWarmup);
        Assert.IsTrue(primaryHashWarmup > installDestinationOverlayWarmup);
        Assert.IsTrue(playlistSummaryWarmup > primaryHashWarmup);
        StringAssert.Contains(virtualWarmup, "precedingOwnedAdjacentIndexWarmupTask.Wait()");
        StringAssert.Contains(virtualWarmup, "ownedAdjacentWaitStatus");
    }

    [TestMethod]
    public void ReloadScoresOnly_DoesNotSchedulePlaylistReloadWork()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string method = ExtractBetween(
            viewModelCode,
            "public async Task ResyncPlaylistsAsync(IEnumerable<BMSTable> tablesToResync)",
            "public void RemovePendingPackagesAll");

        StringAssert.Contains(method, "tables.ReloadPlaylistTargetsAsync(");
        StringAssert.Contains(method, "CreatePlaylistReferenceReplaceUpdateCallback()");
        StringAssert.Contains(method, "tables.QueueBeatorajaBmtExportAll(\"manual_resync\")");
        StringAssert.Contains(method, "UpdatePlaylistSyncRuntimeStatus(result)");
        StringAssert.Contains(method, "playlist_manual_resync_failed");
        Assert.IsFalse(method.Contains("ResetBMSTableAsync("));
        Assert.IsFalse(method.Contains("ShowPlaylistLoadFailure("));
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
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string propertyDialogSave = ExtractBetween(
            viewModelCode,
            "internal async Task ApplyPostSaveUpdatesAsync()",
            "protected override void Dispose(bool disposing)");
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
        int invalidateIndex = source.IndexOf("InvalidateNormalLibrarySortKeys(NormalLibraryReferenceTablesChangedReason)", StringComparison.Ordinal);

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
    public void AppDialogs_UseThemeResourcesAndThemedMessageActions()
    {
        string root = FindRepositoryRoot();
        string mainWindow = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.xaml"));

        StringAssert.Contains(mainWindow, "<v:ThemedInformationDialogInteractionMessageAction />");
        StringAssert.Contains(mainWindow, "<v:ThemedConfirmationDialogInteractionMessageAction />");

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
        string viewModel = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string settings = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Settings.cs"));
        string appConfig = File.ReadAllText(Path.Combine(root, "app.config"));
        string settingDialog = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.xaml"));

        Assert.AreEqual("song.dbアクセス最適化PRAGMAを有効にする", Resources.Details_test_db_read_optimized_pragmas);
        StringAssert.Contains(viewModel, "private bool _IsPlaylistTreeExpanded = true;");
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
        string viewModel = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string candidateHelper = ExtractBetween(mainWindowCode, "private static bool IsDownloadAndInstallCandidateFileName", "private static string NormalizeDownloadUrlString");
        string downloadMethod = ExtractBetween(mainWindowCode, "private async Task<DownloadAndInstallResult> downloadAndInstall", "private static bool IsDownloadAndInstallCandidateFileName");
        MethodInfo helper = typeof(MainWindow).GetMethod("IsDownloadAndInstallCandidateFileName", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(helper);

        StringAssert.Contains(candidateHelper, "ChartFileKindResolver.IsSupportedChartFilePath(fileName)");
        StringAssert.Contains(candidateHelper, "DownloadAndInstallArchiveExtensions");
        Assert.IsFalse(candidateHelper.Contains("BMSFile.bmsExtensions"));
        StringAssert.Contains(downloadMethod, "IsDownloadAndInstallCandidateFileName(fileName)");
        Assert.IsFalse(downloadMethod.Contains("BMSFile.bmsExtensions"));
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

        List<Uri> mainTargets = MainWindow.BuildPlaylistUrlTargetsForTest([first, duplicateMain, new object()], isDiffUrl: false);
        List<Uri> diffTargets = MainWindow.BuildPlaylistUrlTargetsForTest([first, duplicateMain], isDiffUrl: true);

        Assert.AreEqual(1, mainTargets.Count);
        Assert.AreEqual("https://example.invalid/package.zip", mainTargets[0].ToString());
        Assert.AreEqual(2, diffTargets.Count);
        Assert.AreEqual("https://example.invalid/diff-a.zip", diffTargets[0].ToString());
        Assert.AreEqual("https://example.invalid/diff-b.zip", diffTargets[1].ToString());
    }

    [TestMethod]
    public void PlaylistUrlBulkImport_UsesSharedHandlerAndSuppressesBrowserFallback()
    {
        string root = FindRepositoryRoot();
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string singleUrlMethod = ExtractBetween(mainWindowCode, "private async Task OpenSinglePlaylistUrlAsync(Uri url)", "private async Task<PlaylistUrlDownloadResult> DownloadSinglePlaylistUrlCandidateWithStatusAsync");
        string singleUrlStatusMethod = ExtractBetween(mainWindowCode, "private async Task<PlaylistUrlDownloadResult> DownloadSinglePlaylistUrlCandidateWithStatusAsync", "private void playlistRootSelect");
        string openUrlHandler = ExtractBetween(mainWindowCode, "private async void tableContextMenuItemOpenURLClick", "private async void tableContextMenuItemOpenURLdiffClick");
        string openUrlDiffHandler = ExtractBetween(mainWindowCode, "private async void tableContextMenuItemOpenURLdiffClick", "private async Task OpenPlaylistUrlFromContextMenuAsync");
        string contextMenuMethod = ExtractBetween(mainWindowCode, "private async Task OpenPlaylistUrlFromContextMenuAsync", "private List<object> GetEffectiveContextMenuRows");
        string dropHandler = ExtractBetween(mainWindowCode, "private void Window_Drop", "private void Window_DragOver");
        string dragOverHandler = ExtractBetween(mainWindowCode, "private void Window_DragOver", "private void Window_MouseLeftButtonDown");
        string selectionHelper = ExtractBetween(mainWindowCode, "private List<object> GetEffectiveContextMenuRows", "internal static List<Uri> BuildPlaylistUrlTargetsForTest");
        string bulkMethod = ExtractBetween(mainWindowCode, "private async Task DownloadSelectedPlaylistUrlsAsync", "private void tableContextMenuItemOpenDocumentFileClick");
        string refreshStatus = ExtractBetween(
            File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs")),
            "private void RefreshInstallPipelineStatus()",
            "private void BeginPlaylistSyncProgressOperation()");

        StringAssert.Contains(openUrlHandler, "OpenPlaylistUrlFromContextMenuAsync(e.Source, isDiffUrl: false");
        StringAssert.Contains(openUrlDiffHandler, "OpenPlaylistUrlFromContextMenuAsync(e.Source, isDiffUrl: true");
        Assert.IsFalse(openUrlHandler.Contains("Process.Start"));
        Assert.IsFalse(openUrlDiffHandler.Contains("Process.Start"));
        StringAssert.Contains(contextMenuMethod, "OpenPlaylistUrlInBrowser(contextRow, isDiffUrl)");
        StringAssert.Contains(contextMenuMethod, "DownloadSelectedPlaylistUrlsAsync(rows, isDiffUrl)");
        Assert.IsFalse(contextMenuMethod.Contains("OpenSinglePlaylistUrlAsync(contextRow, isDiffUrl)"));
        StringAssert.Contains(singleUrlMethod, "DownloadSinglePlaylistUrlCandidateWithStatusAsync(url)");
        StringAssert.Contains(singleUrlMethod, "playlistUrlBulkDownloadRunning");
        StringAssert.Contains(singleUrlStatusMethod, "UpdatePlaylistUrlDownloadStatus(true, 1, 0");
        StringAssert.Contains(singleUrlStatusMethod, "UpdatePlaylistUrlDownloadStatus(true, 1, 1");
        StringAssert.Contains(singleUrlStatusMethod, "UpdatePlaylistUrlDownloadStatus(false, 0, 0");
        StringAssert.Contains(selectionHelper, "GetSelectedGridRowsSnapshot");
        StringAssert.Contains(bulkMethod, "BuildPlaylistUrlTargets(rows, isDiffUrl)");
        StringAssert.Contains(bulkMethod, "DropInstallQueueCanCancel: true");
        StringAssert.Contains(bulkMethod, "Warn_SelectedPlaylistUrlDownloadBlockedByInstallQueue");
        StringAssert.Contains(bulkMethod, "browserFallbackCount++");
        StringAssert.Contains(bulkMethod, "viewModel?.EnqueueDroppedInstallPaths(downloadedPaths)");
        Assert.IsFalse(bulkMethod.Contains("installChartPackages(downloadedPaths)"));
        Assert.IsFalse(bulkMethod.Contains("Process.Start"));
        StringAssert.Contains(dropHandler, "Warn_DropInstallBlockedByPlaylistUrlDownload");
        StringAssert.Contains(dropHandler, "playlistUrlBulkDownloadRunning");
        StringAssert.Contains(dragOverHandler, "DragDropEffects.None");
        StringAssert.Contains(dragOverHandler, "playlistUrlBulkDownloadRunning");
        Assert.IsTrue(refreshStatus.IndexOf("playlistUrlDownloadStatusActive", StringComparison.Ordinal) < refreshStatus.IndexOf("bool dropActive", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PlaylistUrlDownload_BrowserFallbackRecognizesPageUrls()
    {
        Assert.IsTrue(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://example.invalid/folder/")));
        Assert.IsTrue(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://example.invalid/index.htm")));
        Assert.IsTrue(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://example.invalid/index.html")));
        Assert.IsFalse(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://example.invalid/package.zip")));
        Assert.IsFalse(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1")));
        Assert.IsFalse(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://venue.bmssearch.net/event/1/1")));
        Assert.IsFalse(MainWindow.IsBrowserFallbackDownloadUriForTest(new Uri("https://bmssearch.net/bmses/1")));
    }

    [TestMethod]
    public void PlaylistUrlDownload_NormalizesCloudStorageShareUrls()
    {
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            MainWindow.NormalizeDownloadUriForTest(new Uri("https://drive.google.com/file/d/abc123/view?usp=sharing")).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            MainWindow.NormalizeDownloadUriForTest(new Uri("https://drive.google.com/open?id=abc123&usp=sharing")).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            MainWindow.NormalizeDownloadUriForTest(new Uri("https://docs.google.com/uc?export=download&id=abc123")).ToString());
        Assert.AreEqual(
            "https://www.dropbox.com/scl/fi/token/package.zip?rlkey=key&dl=1",
            MainWindow.NormalizeDownloadUriForTest(new Uri("https://www.dropbox.com/scl/fi/token/package.zip?rlkey=key&dl=0")).ToString());
        Assert.AreEqual(
            "https://notdropbox.com/scl/fi/token/package.zip?dl=0",
            MainWindow.NormalizeDownloadUriForTest(new Uri("https://notdropbox.com/scl/fi/token/package.zip?dl=0")).ToString());
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
        const string venueMediaFireHtml = """
            <html><body>
            <a href="https://www.mediafire.com/file/abc/package.zip/file">Download</a>
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
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"), googleDriveWarningHtml).ToString());
        Assert.AreEqual(
            "https://download123.mediafire.com/abc/package.zip",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://www.mediafire.com/file/abc/package.zip/file"), mediaFireHtml).ToString());
        Assert.AreEqual(
            "https://example.invalid/body.zip",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"), manbowHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://venue.bmssearch.net/event/1/1"), venueHtml).ToString());
        Assert.AreEqual(
            "https://bmssearch.net/archives/package.lzh",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://bmssearch.net/bmses/1"), bmsSearchHtml).ToString());
        Assert.AreEqual(
            "https://anonymous.bms.ms/data/body.zip",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://bmssearch.net/bmses/2uLp8a8bJYLmrx"), bmsSearchNextHtml).ToString());
        Assert.AreEqual(
            "https://anonymous.bms.ms/data/itsfree_battle.7z",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://venue.bmssearch.net/freebattle/30"), venueNextHtml).ToString());
        Assert.AreEqual(
            "https://www.mediafire.com/file/abc/package.zip/file",
            MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://venue.bmssearch.net/freebattle/30"), venueMediaFireHtml).ToString());
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"), googleDriveUnsafeActionHtml));
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://www.mediafire.com/file/abc/package.zip/file"), mediaFireUnsafeHostHtml));
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://evilmediafire.com/file/abc/package.zip/file"), mediaFireHtml));
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"), manbowUnsafeSchemeHtml));
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://venue.bmssearch.net/event/1/1"), venueUnsafeHostHtml));
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://venue.bmssearch.net/freebattle/30"), venueSourcePageOnlyHtml));
        Assert.IsNull(MainWindow.ResolveSharedDownloadPageUriForTest(new Uri("https://venue.bmssearch.net/freebattle/30"), venueGoogleDriveFolderOnlyHtml));
    }

    [TestMethod]
    public void UserSettingDefaults_AppConfigAndSettingsCodeStayInSync()
    {
        string root = FindRepositoryRoot();
        string settingsCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Settings.cs"));
        string appConfigPath = Path.Combine(root, "app.config");
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
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
        string dialogActionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "CommonOpenFileDialogInteractionMessageAction.cs"));
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string allPickerXaml = settingDialogXaml + mainWindowXaml;

        Assert.AreEqual(0, CountOccurrences(allPickerXaml, "<l:FolderBrowserDialogInteractionMessageAction"));
        Assert.AreEqual(0, CountOccurrences(allPickerXaml, "<l:OpenFileDialogInteractionMessageAction"));
        StringAssert.Contains(allPickerXaml, "v:CommonOpenFileDialogInteractionMessageAction");
        StringAssert.Contains(dialogActionCode, "CommonOpenFileDialog");
        StringAssert.Contains(dialogActionCode, "IsFolderPicker = true");
        StringAssert.Contains(dialogActionCode, "ShowDialog(Window.GetWindow(AssociatedObject))");
        StringAssert.Contains(dialogActionCode, "message.Response = [.. dialog.FileNames];");

        StringAssert.Contains(settingDialogXaml, "Click=\"buttonAddBmsSearchRootPathsClicked\"");
        StringAssert.Contains(settingDialogCode, "Multiselect = true");
        StringAssert.Contains(settingDialogCode, "IsFolderPicker = true");
        StringAssert.Contains(settingDialogCode, "dialog.FileNames");
        StringAssert.Contains(viewModelCode, "private void AddBmsSearchRootPaths(IEnumerable<string> paths, string messageKey, bool saveImmediately)");
        StringAssert.Contains(viewModelCode, "public void AddStandaloneBmsRootPaths(IEnumerable<string> paths)");
        StringAssert.Contains(viewModelCode, "NormalizeExistingStandaloneBmsRootPathsWithoutLr2Compatibility(paths ?? [])");
        StringAssert.Contains(viewModelCode, "ThrowIfLr2IncompatibleStandaloneBmsRoots(requestedPaths)");

        Type actionType = typeof(MainWindow).Assembly.GetType("BeMusicSeeker.Views.CommonOpenFileDialogInteractionMessageAction");
        Assert.IsNotNull(actionType);
        MethodInfo parseMethod = actionType.GetMethod("ParseFilterPairsForTest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parseMethod);
        MethodInfo inferDefaultExtensionMethod = actionType.GetMethod("InferDefaultExtensionForTest", BindingFlags.Static | BindingFlags.NonPublic);
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
        StringAssert.Contains(dialogActionCode, "dialog.DefaultExtension = defaultExtension");
        StringAssert.Contains(dialogActionCode, "InferDefaultExtensionForTest(message.FileName, message.Filter)");
        StringAssert.Contains(settingDialogXaml, "Filter=\"|config.xm?|");
        StringAssert.Contains(settingDialogXaml, "Filter=\"song.db (*.db)|*.db|");
        StringAssert.Contains(Resources.FileDialogFilter_scoreDB, "score.db (*.db)|*.db|");
    }

    [TestMethod]
    public void FileDialogs_SetDefaultExtensionsForTypedFileNames()
    {
        string root = FindRepositoryRoot();
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string loadPlaylistCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "LoadPlaylistURIDialog.cs"));

        StringAssert.Contains(settingDialogCode, "DefaultExt = \".sql\"");
        StringAssert.Contains(settingDialogCode, "AddExtension = true");
        StringAssert.Contains(mainWindowCode, "fileDialogHeader.DefaultExt = \".json\";");
        StringAssert.Contains(mainWindowCode, "fileDialogData.DefaultExt = \".json\";");
        StringAssert.Contains(mainWindowCode, "fileDialogHeader.AddExtension = true;");
        StringAssert.Contains(mainWindowCode, "fileDialogData.AddExtension = true;");
        StringAssert.Contains(loadPlaylistCode, "DefaultExt = \".json\"");
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
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string uninstallClickHandler = ExtractMethodBody(settingDialogCode, "private async void detailTabItemUninstallButtonClicked(object sender, RoutedEventArgs e)");

        StringAssert.Contains(uninstallClickHandler, "DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_success_uninstall");
        StringAssert.Contains(uninstallClickHandler, "DispatcherMessageBox.Show(Window.GetWindow(this), BeMusicSeeker.Properties.Resources.Msg_failed_uninstall");
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
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string resourceHealthCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "ResourceHealthWarningProjection.cs"));
        string installEstimationDoc = File.ReadAllText(Path.Combine(root, "devdocs", "spec", "install-estimation-current-logic.md"));

        StringAssert.Contains(libraryCode, "private sealed class EstimatedInstallBatchApplyContext");
        StringAssert.Contains(libraryCode, "ApplyEstimatedInstallBatchLibraryState(batchApplyContext)");
        string batchContext = ExtractBetween(libraryCode, "private sealed class EstimatedInstallBatchApplyContext", "private List<ChartPackage> installChartPackages");
        StringAssert.Contains(batchContext, "public List<ChartFile> AddedCharts { get; } = [];");
        Assert.IsFalse(batchContext.Contains("AddedBmsFiles"));
        Assert.IsFalse(batchContext.Contains("AddedBmsonSongs"));
        StringAssert.Contains(batchContext, "AddInstalledTargets(ChartStorageTargetSet addedTargets");
        StringAssert.Contains(libraryCode, "static ChartStorageTargetSet CreateAddedStorageTargets(PackageInstallExecutionResult installResult)");
        Assert.IsFalse(libraryCode.Contains("CreateResourceMaintenanceTargetSet(installResult?.AddedCharts)"));
        Assert.IsFalse(libraryCode.Contains("CreateResourceMaintenanceTargetSet(IEnumerable<BMSFile> bmsFiles"));
        Assert.IsFalse(libraryCode.Contains("CreateBmsResourceMaintenanceTargetCharts"));
        StringAssert.Contains(libraryCode, "ChartStorageTargetSet.FromCharts(context.AddedCharts)");
        Assert.IsFalse(libraryCode.Contains("ResolveAddedBmsonSongsFromInstalledPackages"));
        Assert.IsFalse(libraryCode.Contains("CreateAddedBmsonChartProjectionsFromInstalledPackages"));
        string estimatedInstallMethod = ExtractMethodBody(libraryCode, "public void InstallPendingPackagesToEstimatedDestinations");
        Assert.IsFalse(estimatedInstallMethod.Contains("foreach (LR2SongDBExtended.bmson_song song in BmsonSongs"));
        StringAssert.Contains(libraryCode, "CreateAddedBmsonChartProjections(batchApplyContext.AddedCharts)");
        StringAssert.Contains(libraryCode, "BuildEstimatedInstallMaintenanceTargets(batchResult.DeferredMaintenanceCharts)");
        Assert.IsFalse(estimatedInstallMethod.Contains("ResourceHealthIndexUpdateMode.FullOnUpdates"));
        StringAssert.Contains(estimatedInstallMethod, "resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates");
        StringAssert.Contains(libraryCode, "LogReverseLookupMutationAndQueueWarmupIfNeeded(\"install_package\", reverseLookupMutation);");
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
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string mergeMethod = ExtractMethodBody(libraryCode, "internal void MergeChartDirectory(string src, string dst, long operationId)");
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
        StringAssert.Contains(mergeMethod, "ApplyInstalledChartStorageTargets(movedTargets, \"merge_folder\")");
        Assert.IsFalse(mergeMethod.Contains("NormalizeResourceMaintenanceTargetCharts(maintenanceTargets"));
        Assert.IsFalse(mergeMethod.Contains("ChartStorageTargetSet.FromRows(movedBmsFiles, movedBmsonSongs)"));
        StringAssert.Contains(mergeMethod, "resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates");
        StringAssert.Contains(mergeMethod, "resourceHealthMutationReason: \"merge_folder\"");
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
        string mainWindowCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "MainWindow.cs"));
        string folderMergeMethod = ExtractMethodBody(mainWindowCode, "private void ExecuteDuplicateFolderMerge(string srcPath, string dstPath, DuplicateGroup duplicateGroup)");
        string hashCleanupMethod = ExtractMethodBody(mainWindowCode, "private void ExecuteDuplicateHashCleanup(DuplicateGroup duplicateGroup, string folderPath)");

        StringAssert.Contains(folderMergeMethod, "Settings.Default.ShowDuplicateFileCheckConfirmMsg && DispatcherMessageBox.Show");
        StringAssert.Contains(hashCleanupMethod, "Settings.Default.ShowDuplicateFileCheckConfirmMsg && DispatcherMessageBox.Show");
    }

    [TestMethod]
    public void ResourceHealthForceFilter_ReusesFullOwnedMaintenanceTargets()
    {
        string root = FindRepositoryRoot();
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string method = ExtractMethodBody(libraryCode, "internal List<ChartFile> GetChartsNeedResourceFix");
        string setOwnedMethod = ExtractMethodBody(libraryCode, "private MaintenanceWorkflowResult setOwnedMaintenanceInfo");
        string buildMutationMethod = ExtractMethodBody(libraryCode, "private static ResourceHealthIndexMutation BuildMaintenanceResourceHealthIndexMutation");
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
        StringAssert.Contains(buildMutationMethod, "mutation.FullOwnedTargetSet = maintenanceTargets;");
        StringAssert.Contains(dispatchMethod, "PublishOwnedCollectionChangeNotification(result);");
        StringAssert.Contains(dispatchMethod, "AlignResourceHealthFullOwnedTargetVersionAfterOwnedCollectionNotification(result);");
        StringAssert.Contains(alignMethod, "mutation.FullOwnedTargetSet = mutation.FullOwnedTargetSet.WithOwnedCollectionVersion(result.OwnedCollectionVersion);");
    }

    [TestMethod]
    public void MaintenanceHydrationUsesOwnedStorageOwnerView()
    {
        string root = FindRepositoryRoot();
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string applyMethod = ExtractMethodBody(libraryCode, "private void ApplyMaintenanceHydrationResult");
        string countMethod = ExtractMethodBody(libraryCode, "private int CountInstallableMaintenanceSnapshotTargets");
        string queueMethod = ExtractMethodBody(libraryCode, "private void QueueDeferredInstallableMaintenance");

        StringAssert.Contains(applyMethod, "OwnedChartStorageOwnerView ownerView = CreateOwnedChartStorageOwnerViewUnsafe()");
        StringAssert.Contains(applyMethod, "foreach (BMSFile item in ownerView.BmsFiles)");
        StringAssert.Contains(applyMethod, "foreach (LR2SongDBExtended.bmson_song item in ownerView.BmsonSongs)");
        StringAssert.Contains(applyMethod, "ownerView.ContainsOwnerPath(maintenancePath)");
        StringAssert.Contains(applyMethod, "ResourceMaintenanceTargetSet resourceHealthTargets = default;");
        StringAssert.Contains(applyMethod, "resourceHealthTargets = CreateFullOwnedResourceMaintenanceTargetSet");
        StringAssert.Contains(applyMethod, "DispatchMaintenanceHydrationResult(");
        Assert.IsFalse(applyMethod.Contains("resourceHealthTargetOwnedCollectionVersion"));
        Assert.IsFalse(applyMethod.Contains("resourceHealthTargetInputVersion"));
        Assert.IsFalse(applyMethod.Contains("RebuildResourceHealthIndexSnapshotLocked(\"maintenance_hydration\")"));
        StringAssert.Contains(libraryCode, "BuildMaintenanceHydrationMutationResult");
        StringAssert.Contains(libraryCode, "result.ResourceHealthMutation.RebuildFull = true");
        StringAssert.Contains(libraryCode, "result.ResourceHealthMutation.FullOwnedTargetSet = fullOwnedTargets");
        StringAssert.Contains(libraryCode, "resource_health_index_full_target_stale");
        Assert.IsFalse(applyMethod.Contains("foreach (BMSFile item in BMSFiles"));
        Assert.IsFalse(applyMethod.Contains("foreach (LR2SongDBExtended.bmson_song item in BmsonSongs"));
        StringAssert.Contains(countMethod, "(BMSFiles?.Count ?? 0) + (BmsonSongs?.Count ?? 0)");
        Assert.IsFalse(countMethod.Contains("CreateOwnedChartStorageOwnerViewUnsafe().Count"));
        StringAssert.Contains(queueMethod, "snapshotCount = CreateOwnedChartStorageOwnerViewUnsafe().Count");
        Assert.IsFalse(queueMethod.Contains("bmsonSnapshotCount"));
    }

    [TestMethod]
    public void FileScanMutationUsesOwnedCollectionForRemovedCharts()
    {
        string root = FindRepositoryRoot();
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
        string ownedCollectionCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "OwnedChartCollectionState.cs"));
        string applyMethod = ExtractMethodBody(libraryCode, "private void ApplyLibraryFileScanStorageMutation");
        string buildMethod = ExtractMethodBody(libraryCode, "private OwnedChartCollectionMutationResult BuildOwnedChartCollectionFileScanMutationResult");

        StringAssert.Contains(applyMethod, "bool removedPayloadAvailable = TryCreateOwnedFileScanRemovedStorageOwnerIdentityChartsUnsafe(fileCheckResult, out List<ChartFile> removedCharts);");
        StringAssert.Contains(applyMethod, "mutationResult = BuildOwnedChartCollectionFileScanMutationResult(");
        StringAssert.Contains(applyMethod, "resourceHealthMutation.BaseIndexCurrent");
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
        string libraryCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs"));
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
