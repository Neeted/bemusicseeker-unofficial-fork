using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
    public void DeleteContextMenuItems_UseSpecificDeleteResourceKeys()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(2, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteEntry\" Header=\"{Binding Source={x:Static vm:ResourceService.Current}, Path=Resources.Remove_playlist_entry, Mode=OneWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemDeleteFile\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_file, Mode=OneWay"));
    }

    [TestMethod]
    public void ContextMenusRetireFixedExternalWebItems()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        foreach (string retiredName in new[]
        {
            "tableContextMenuItemOpenLR2IR",
            "tableContextMenuItemOpenMocha",
            "tableContextMenuItemOpenMinIR",
            "playHistoryContextMenuItemOpenBMSIR",
            "playHistoryContextMenuItemOpenMocha",
            "playHistoryContextMenuItemOpenMinIR"
        })
        {
            Assert.AreEqual(0, CountOccurrences(xaml, "Name=\"" + retiredName + "\""), retiredName);
        }

        StringAssert.Contains(xaml, "Name=\"tableContextMenuItemOpenBMSFile\"");
        StringAssert.Contains(xaml, "Name=\"playHistoryContextMenuItemOpenExplorer\"");
        StringAssert.Contains(xaml, "Name=\"playHistoryContextMenuItemOpenAssociated\"");
        StringAssert.Contains(xaml, "Path=Resources.Open_association, Mode=OneWay");
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_program_actions));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_open_with_program));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_external_launch_failed_format));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_external_settings_invalid));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_external_web_launch_failed));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_external_program_executable_missing));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_external_program_chart_missing));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.RightClick_external_program_launch_failed));
    }

    [TestMethod]
    public void ConfiguredExternalActionFailureUsesTypedLocalizedMessage()
    {
        foreach ((ExternalConfiguredActionFailureKind kind, string expected) in new[]
        {
            (ExternalConfiguredActionFailureKind.InvalidSettings, Resources.RightClick_external_settings_invalid),
            (ExternalConfiguredActionFailureKind.ActionUnavailable, Resources.RightClick_external_action_unavailable),
            (ExternalConfiguredActionFailureKind.WebLaunchFailed, Resources.RightClick_external_web_launch_failed),
            (ExternalConfiguredActionFailureKind.ProgramExecutableMissing, Resources.RightClick_external_program_executable_missing),
            (ExternalConfiguredActionFailureKind.ProgramChartMissing, Resources.RightClick_external_program_chart_missing),
            (ExternalConfiguredActionFailureKind.ProgramLaunchFailed, Resources.RightClick_external_program_launch_failed)
        })
        {
            string message = MainWindow.GetConfiguredExternalActionFailureMessage(
                ExternalConfiguredActionResult.Failure(kind, "raw parser or exception detail"));

            Assert.AreEqual(expected, message, kind.ToString());
            Assert.IsFalse(message.Contains("raw parser or exception detail", StringComparison.Ordinal));
        }
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
    public void PlayHistoryContextMenuOwnerCreatesAssociatedTargetForResolvedRow()
    {
        PlayHistoryRow row = CreateResolvedPlayHistoryRow();
        PlayHistoryWorkflowOwner owner = new();

        Assert.IsTrue(owner.TryCreateContextMenuAction(
            row,
            PlayHistoryContextMenuActionKind.OpenAssociated,
            out PlayHistoryContextMenuAction associatedAction));
        Assert.IsFalse(string.IsNullOrWhiteSpace(associatedAction.Path));
        Assert.IsTrue(owner.TryCreateAssociatedChartOperationTarget(
            row,
            out ChartOperationTarget target));
        Assert.AreEqual(ChartOperationSourceScope.PlayHistory, target.SourceScope);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
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
    public void CanonicalScrollViewer_MaterializesSharedScrollBarsAndPreservesBehavior()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var host = new Grid();
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/Light.xaml"));
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalControls.xaml"));

            var scroller = new ScrollViewer
            {
                Width = 180,
                Height = 120,
                Content = new Border { Width = 600, Height = 420 },
                Style = (Style)host.Resources["App.Canonical.ScrollViewerStyle"],
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false
            };
            host.Children.Add(scroller);
            var window = new Window
            {
                Width = 220,
                Height = 160,
                Content = host
            };

            windowTest.ShowAndWaitForContentRendered(window);
            scroller.ApplyTemplate();
            var vertical = (ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
            var horizontal = (ScrollBar)scroller.Template.FindName("PART_HorizontalScrollBar", scroller);
            Assert.IsNotNull(vertical);
            Assert.IsNotNull(horizontal);
            Assert.AreEqual(Orientation.Vertical, vertical.Orientation);
            Assert.AreEqual(Orientation.Horizontal, horizontal.Orientation);
            Assert.AreEqual(Visibility.Visible, vertical.Visibility);
            Assert.AreEqual(Visibility.Visible, horizontal.Visibility);
            Assert.AreEqual(scroller.ViewportHeight, vertical.ViewportSize, 0.01d);
            Assert.AreEqual(scroller.ViewportWidth, horizontal.ViewportSize, 0.01d);
            Assert.AreEqual(scroller.ScrollableHeight, vertical.Maximum, 0.01d);
            Assert.AreEqual(scroller.ScrollableWidth, horizontal.Maximum, 0.01d);

            vertical.ApplyTemplate();
            horizontal.ApplyTemplate();
            var verticalTrack = (Track)vertical.Template.FindName("PART_Track", vertical);
            var horizontalTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
            Assert.IsNotNull(verticalTrack);
            Assert.IsNotNull(horizontalTrack);
            Assert.AreEqual(Orientation.Vertical, verticalTrack.Orientation);
            Assert.AreEqual(Orientation.Horizontal, horizontalTrack.Orientation);
            Assert.IsTrue(vertical.ActualWidth > 0d && vertical.ActualHeight > 0d);
            Assert.IsTrue(horizontal.ActualWidth > 0d && horizontal.ActualHeight > 0d);

            Border corner = FindVisualDescendants<Border>(scroller)
                .Single(border => Grid.GetRow(border) == 1 && Grid.GetColumn(border) == 1);
            Assert.IsNotNull(corner.Background);
            var scrollPeer = new ScrollViewerAutomationPeer(scroller);
            var scrollProvider = (IScrollProvider)scrollPeer.GetPattern(PatternInterface.Scroll);
            Assert.IsNotNull(scrollProvider);
            Assert.IsTrue(scrollProvider.VerticallyScrollable);
            Assert.IsTrue(scrollProvider.HorizontallyScrollable);
            Assert.IsFalse(scroller.CanContentScroll);
            double initialVerticalOffset = scroller.VerticalOffset;
            double initialHorizontalOffset = scroller.HorizontalOffset;
            scrollProvider.Scroll(ScrollAmount.SmallIncrement, ScrollAmount.SmallIncrement);
            TestUiDispatcherHost.Drain();
            Assert.IsTrue(scroller.VerticalOffset > initialVerticalOffset);
            Assert.IsTrue(scroller.HorizontalOffset > initialHorizontalOffset);
            Assert.AreEqual(scroller.VerticalOffset, vertical.Value, 0.01d);
            Assert.AreEqual(scroller.HorizontalOffset, horizontal.Value, 0.01d);

            AssertScrollCommandBehavior(
                scroller,
                vertical,
                verticalTrack,
                Orientation.Vertical,
                "Canonical context vertical scrollbar");
            AssertScrollCommandBehavior(
                scroller,
                horizontal,
                horizontalTrack,
                Orientation.Horizontal,
                "Canonical context horizontal scrollbar");

            vertical.IsEnabled = false;
            TestUiDispatcherHost.Drain();
            Assert.IsTrue(vertical.Opacity < 1d);
        });
    }

    private static void AssertScrollCommandBehavior(
        ScrollViewer viewer,
        ScrollBar scrollbar,
        Track track,
        Orientation orientation,
        string description)
    {
        RoutedCommand lineStartCommand;
        RoutedCommand lineEndCommand;
        RoutedCommand pageStartCommand;
        RoutedCommand pageEndCommand;
        if (orientation == Orientation.Vertical)
        {
            lineStartCommand = ScrollBar.LineUpCommand;
            lineEndCommand = ScrollBar.LineDownCommand;
            pageStartCommand = ScrollBar.PageUpCommand;
            pageEndCommand = ScrollBar.PageDownCommand;
        }
        else
        {
            lineStartCommand = ScrollBar.LineLeftCommand;
            lineEndCommand = ScrollBar.LineRightCommand;
            pageStartCommand = ScrollBar.PageLeftCommand;
            pageEndCommand = ScrollBar.PageRightCommand;
        }

        FrameworkElement lineStart = FindScrollCommandAffordance(
            scrollbar,
            lineStartCommand,
            description + " line-start");
        FrameworkElement lineEnd = FindScrollCommandAffordance(
            scrollbar,
            lineEndCommand,
            description + " line-end");
        FrameworkElement pageStart = FindScrollCommandAffordance(
            track,
            pageStartCommand,
            description + " page-start");
        FrameworkElement pageEnd = FindScrollCommandAffordance(
            track,
            pageEndCommand,
            description + " page-end");

        double lineStartOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        InvokeScrollAffordance(lineStart, description + " line-start");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, orientation) < lineStartOffset,
            description + " line-start command must decrease the viewer offset.");

        double lineEndOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        InvokeScrollAffordance(lineEnd, description + " line-end");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, orientation) > lineEndOffset,
            description + " line-end command must increase the viewer offset.");

        double pageStartOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        InvokeScrollAffordance(pageStart, description + " page-start");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, orientation) < pageStartOffset,
            description + " page-start command must decrease the viewer offset.");

        double pageEndOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        InvokeScrollAffordance(pageEnd, description + " page-end");
        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        double finalOffset = GetScrollOffset(viewer, orientation);
        Assert.IsTrue(
            finalOffset > pageEndOffset,
            description + " page-end command must increase the viewer offset.");
        Assert.AreEqual(
            finalOffset,
            scrollbar.Value,
            0.01d,
            description + " scrollbar value must follow its viewer offset.");
    }

    private static FrameworkElement FindScrollCommandAffordance(
        DependencyObject root,
        RoutedCommand command,
        string description)
    {
        FrameworkElement[] candidates = FindVisualDescendants<FrameworkElement>(root)
            .Where(element => element is ICommandSource source && ReferenceEquals(source.Command, command))
            .ToArray();
        Assert.AreEqual(
            1,
            candidates.Length,
            description + " must expose exactly one materialized command affordance.");
        FrameworkElement affordance = candidates[0];
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(affordance)
            ?? throw new AssertFailedException(description + " command affordance must expose an Automation peer.");
        Assert.IsNotNull(
            peer.GetPattern(PatternInterface.Invoke),
            description + " command affordance must expose Invoke automation.");
        return affordance;
    }

    private static double SetScrollOffsetToInterior(
        ScrollViewer viewer,
        Orientation orientation,
        string description)
    {
        double maximum = orientation == Orientation.Vertical
            ? viewer.ScrollableHeight
            : viewer.ScrollableWidth;
        Assert.IsTrue(maximum > 0d, description + " must expose a positive scroll extent.");
        if (orientation == Orientation.Vertical)
        {
            viewer.ScrollToVerticalOffset(maximum / 2d);
        }
        else
        {
            viewer.ScrollToHorizontalOffset(maximum / 2d);
        }

        TestUiDispatcherHost.Drain();
        viewer.UpdateLayout();
        double offset = GetScrollOffset(viewer, orientation);
        Assert.IsTrue(
            offset > 0d && offset < maximum,
            description + " must invoke affordances from a proven non-boundary offset.");
        return offset;
    }

    private static double GetScrollOffset(ScrollViewer viewer, Orientation orientation)
        => orientation == Orientation.Vertical ? viewer.VerticalOffset : viewer.HorizontalOffset;

    private static void InvokeScrollAffordance(FrameworkElement affordance, string description)
    {
        Assert.IsTrue(affordance.IsEnabled, description + " command affordance must be enabled.");
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(affordance)
            ?? throw new AssertFailedException(description + " command affordance must expose an Automation peer.");
        IInvokeProvider invokeProvider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new AssertFailedException(description + " command affordance must expose Invoke automation.");
        invokeProvider.Invoke();
    }

    [TestMethod]
    public void CanonicalTopNavigation_UsesSingleSelectionAndStandardAutomation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var host = new StackPanel();
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/Light.xaml"));
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalControls.xaml"));
            var navigation = new ListBox
            {
                Width = 360,
                Height = 56,
                ItemsSource = new[] { "General", "Folder", "Custom Folder" },
                SelectedIndex = 0,
                Style = (Style)host.Resources["App.Canonical.TopNavigationStyle"]
            };
            var contentText = new TextBlock { Text = "General content" };
            var content = new ContentControl
            {
                Height = 80,
                Content = contentText,
                Style = (Style)host.Resources["App.Canonical.TopNavigationContentStyle"]
            };
            host.Children.Add(navigation);
            host.Children.Add(content);
            var window = new Window
            {
                Width = 420,
                Height = 180,
                Content = host
            };

            windowTest.ShowAndWaitForContentRendered(window);
            navigation.ApplyTemplate();
            navigation.UpdateLayout();
            Assert.AreEqual(SelectionMode.Single, navigation.SelectionMode);
            Assert.AreEqual(0, navigation.SelectedIndex);
            StackPanel itemPanel = FindVisualDescendants<StackPanel>(navigation)
                .Single(panel => panel.Children.OfType<ListBoxItem>().Count() == navigation.Items.Count);
            Assert.AreEqual(Orientation.Horizontal, itemPanel.Orientation);
            var items = navigation.Items
                .Cast<object>()
                .Select((_, index) => (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(index))
                .ToArray();
            Assert.IsTrue(items.All(item => item is not null));
            foreach (ListBoxItem item in items)
            {
                item.ApplyTemplate();
                Assert.IsNotNull(item.Template.FindName("SelectionIndicator", item));
                Assert.IsNotNull(item.Template.FindName("FocusBorder", item));
                Assert.IsTrue(item.Focusable);
            }
            Assert.AreEqual(
                KeyboardNavigationMode.Continue,
                KeyboardNavigation.GetDirectionalNavigation(navigation));
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)items[0].Template.FindName("SelectionIndicator", items[0])).Visibility);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)items[1].Template.FindName("SelectionIndicator", items[1])).Visibility);

            var navigationPeer = new ListBoxAutomationPeer(navigation);
            var selectionProvider = (ISelectionProvider)navigationPeer.GetPattern(PatternInterface.Selection);
            Assert.IsNotNull(selectionProvider);
            Assert.IsFalse(selectionProvider.CanSelectMultiple);
            Assert.AreEqual(1, selectionProvider.GetSelection().Length);
            AutomationPeer folderPeer = navigationPeer.GetChildren()
                .Single(peer => peer.GetName() == "Folder");
            var folderSelection = (ISelectionItemProvider)folderPeer.GetPattern(PatternInterface.SelectionItem);
            Assert.IsNotNull(folderSelection);
            folderSelection.Select();
            TestUiDispatcherHost.Drain();
            Assert.AreEqual(1, navigation.SelectedIndex);
            Assert.IsTrue(folderSelection.IsSelected);

            items[2].IsEnabled = false;
            TestUiDispatcherHost.Drain();
            var disabledNavigationChrome = (Border)items[2].Template.FindName("NavigationItemChrome", items[2]);
            Assert.IsTrue(disabledNavigationChrome.Opacity < 1d);
            content.ApplyTemplate();
            content.UpdateLayout();
            Assert.IsFalse(content.Focusable);
            Assert.IsFalse(
                FindVisualDescendants<Border>(content).Any(border =>
                    border.BorderThickness.Left > 0d
                    || border.BorderThickness.Top > 0d
                    || border.BorderThickness.Right > 0d
                    || border.BorderThickness.Bottom > 0d
                    || border.CornerRadius.TopLeft > 0d
                    || border.CornerRadius.TopRight > 0d
                    || border.CornerRadius.BottomRight > 0d
                    || border.CornerRadius.BottomLeft > 0d),
                "The shared top-navigation content role must be an unframed host.");
            Point contentOrigin = contentText.TransformToAncestor(content).Transform(new Point());
            Assert.AreEqual(content.Padding.Left, contentOrigin.X, 0.5d);
            Assert.AreEqual(content.Padding.Top, contentOrigin.Y, 0.5d);
            Assert.AreEqual(
                content.ActualWidth - content.Padding.Left - content.Padding.Right,
                contentText.ActualWidth,
                0.5d,
                "The unframed content host must preserve horizontal padding and stretch alignment.");
        });
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
            "App.AccentForegroundBrush",
            "App.AccentFocusRingBrush",
            "App.AccentHoverBrush",
            "App.AccentPressedBrush",
            "App.DangerBrush",
            "App.DangerForegroundBrush",
            "App.DangerHoverBrush",
            "App.DangerPressedBrush",
            "App.ErrorTextBrush",
            "App.SuccessTextBrush",
            "App.SliderTickBrush",
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

        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] lightKeys = XDocument.Parse(light).Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value ?? string.Empty)
            .Where(key => key.Length > 0)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        string[] darkKeys = XDocument.Parse(dark).Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value ?? string.Empty)
            .Where(key => key.Length > 0)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(lightKeys, darkKeys, "Light and dark theme dictionaries must have exact key parity.");
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
    public void SidebarTreeViewWidthSavePolicy_UsesMeasuredColumnWhenValid()
    {
        Assert.AreEqual(240d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(240d, 250d, 300d));
        Assert.AreEqual(260d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(double.NaN, 260d, 300d));
        Assert.AreEqual(320d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(0d, double.NaN, 320d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(0d, double.NaN, 0d));
    }

    [TestMethod]
    public void AdvancedSettings_UserFacingCopyRetiresPlaylistExpansionLabelAndPromotesSongDbPragmaLabel()
    {
        Assert.AreEqual("song.dbアクセス最適化PRAGMAを有効にする", Resources.Details_test_db_read_optimized_pragmas);
        Assert.IsNull(
            Resources.ResourceManager.GetString(
                "Details_test_startup_expand_playlist_tree",
                CultureInfo.InvariantCulture),
            "The retired playlist-expansion option must not have user-facing localized copy.");
    }

    [TestMethod]
    public void AdvancedSettings_UserFacingLabelsArePromotedToRegularSettingLabels()
    {
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
        Assert.IsFalse(Resources.Details_scan_bms_files_on_startup.Contains("本機能はテスト実装中です"));
        Assert.IsFalse(Resources.Details_test_notcheck_playlists.Contains("本機能はテスト実装中です"));
        Assert.IsFalse(Resources.Details_update_lr2ir_ranking_cache_on_startup.Contains("本機能はテスト実装中です"));
        Assert.IsFalse(Resources.Details_estimate_offline_score_ranking.Contains("本機能はテスト実装中です"));
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

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "Start marker was not found.");
        int endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, "End marker was not found.");
        return text.Substring(startIndex, endIndex - startIndex);
    }

    private static ResourceDictionary CreateResourceDictionary(string source)
    {
        return new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
        };
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

}
