using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
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
    public void SidebarLayout_UsesUnifiedSplitterStyleAndMinimumWidth()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml")).Replace("\r\n", "\n");

        Assert.AreEqual(1, CountOccurrences(xaml, "x:Key=\"SidebarSplitterStyle\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Style=\"{StaticResource SidebarSplitterStyle}\""));
        StringAssert.Contains(xaml, "<Style x:Key=\"SidebarSplitterStyle\" TargetType=\"{x:Type GridSplitter}\">\n        <Setter Property=\"Focusable\" Value=\"False\" />\n        <Setter Property=\"IsTabStop\" Value=\"False\" />\n        <Setter Property=\"FrameworkElement.FocusVisualStyle\" Value=\"{x:Null}\" />");
        StringAssert.Contains(xaml, "<Grid Background=\"Transparent\" SnapsToDevicePixels=\"True\">");
        StringAssert.Contains(xaml, "<Border x:Name=\"SeparatorLine\" Width=\"1\" HorizontalAlignment=\"Right\" Background=\"{TemplateBinding Background}\" />");
        StringAssert.Contains(xaml, "<Trigger Property=\"ResizeDirection\" Value=\"Rows\">");
        StringAssert.Contains(xaml, "<Setter TargetName=\"SeparatorLine\" Property=\"Width\" Value=\"{x:Static s:Double.NaN}\" />");
        StringAssert.Contains(xaml, "<Setter TargetName=\"SeparatorLine\" Property=\"Height\" Value=\"1\" />");
        StringAssert.Contains(xaml, "<Setter TargetName=\"SeparatorLine\" Property=\"VerticalAlignment\" Value=\"Center\" />");
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridSplitter\" Style=\"{StaticResource SidebarSplitterStyle}\" ResizeDirection=\"Columns\" ResizeBehavior=\"CurrentAndNext\" Margin=\"0\" Grid.RowSpan=\"2\" Width=\"5\" HorizontalAlignment=\"Right\" Grid.Row=\"1\" Grid.Column=\"0\" Panel.ZIndex=\"1\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "<RowDefinition Height=\"1\" />"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridSplitterTree\" Style=\"{StaticResource SidebarSplitterStyle}\" Grid.Row=\"1\" Height=\"5\" HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Center\" ResizeDirection=\"Rows\" ResizeBehavior=\"PreviousAndNext\" Margin=\"0\" Panel.ZIndex=\"1\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridColumn0\" MinWidth=\"160\" Width=\"{Binding TreeViewWidth, Source={x:Static prop:Settings.Default}, Mode=OneTime}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"gridTreePane\" Margin=\"0\" Grid.Row=\"1\" Grid.RowSpan=\"2\" Background=\"{DynamicResource App.BackgroundBrush}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "<Border BorderThickness=\"0\" Grid.Row=\"1\" Grid.ColumnSpan=\"1\" Grid.Column=\"1\" Background=\"{DynamicResource App.SurfaceBrush}\">"));
        StringAssert.Contains(xaml, "UseLayoutRounding=\"True\" SnapsToDevicePixels=\"True\"");
        Assert.AreEqual(0, CountOccurrences(xaml, "BorderBrush=\"#FF828790\" BorderThickness=\"1,0,0,0\" Grid.Row=\"1\" Grid.ColumnSpan=\"1\" Grid.Column=\"1\""));
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
        StringAssert.Contains(mainWindow, "<TreeView Name=\"treeView\" Grid.Row=\"2\" ScrollViewer.HorizontalScrollBarVisibility=\"Disabled\" VirtualizingStackPanel.IsVirtualizing=\"True\" VirtualizingStackPanel.VirtualizationMode=\"Recycling\" BorderThickness=\"0\" Padding=\"1,5\" Background=\"{DynamicResource App.BackgroundBrush}\" Foreground=\"{DynamicResource App.TextBrush}\" FocusVisualStyle=\"{x:Null}\"");
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
        StringAssert.Contains(xaml, "ItemsSource=\"{Binding settingDialog.StandaloneBmsRootPathList}\"");
        StringAssert.Contains(xaml, "Command=\"{Binding settingDialog.RemoveDirCommand}\"");
        Assert.IsFalse(xaml.Contains("ToolTip=\"未実装\""));
        StringAssert.Contains(viewModelCode, "Settings.Default.StandaloneBmsRootPaths");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidStandaloneBmsRootPaths");
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
        StringAssert.Contains(viewModelCode, "tables = new BMSPlaylist(libraryProfile.SongDbPath");
        StringAssert.Contains(viewModelCode, "files.SearchTargets.AddRange(libraryProfile.SearchRoots)");
        StringAssert.Contains(viewModelCode, "return [];");
        StringAssert.Contains(viewModelCode, "temp_output_dir_full_path = Settings.Default.OperationModeLR2DB ? BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable) : null;");
        string saveFollowup = ExtractBetween(
            viewModelCode,
            "internal async Task ApplyPostSaveUpdatesAsync()",
            "protected override void Dispose(bool disposing)");
        int headerCommitIndex = saveFollowup.IndexOf("ownerViewModel.tables.CommitBMSTableHeaderToDB(bmsTable);", StringComparison.Ordinal);
        int lr2CustomFolderIndex = saveFollowup.IndexOf("if (Settings.Default.OperationModeLR2DB)", StringComparison.Ordinal);
        Assert.IsTrue(headerCommitIndex >= 0);
        Assert.IsTrue(lr2CustomFolderIndex > headerCommitIndex);
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
            "private async Task necessaryStepsAfterSaved()",
            "public bool CheckValidation()");
        string lr2RootPathProperty = ExtractBetween(
            viewModelCode,
            "public string LR2RootPath",
            "public Dictionary<string, Point> LR2bodyResolutions");

        Assert.IsFalse(lr2PlaybackXaml.Contains("OperationModeLR2DB"));
        StringAssert.Contains(checkValidation, "else if (UsePlayerLR2body)");
        StringAssert.Contains(checkValidation, "if (!IsLR2PlayerRootPathValid())");
        Assert.IsFalse(checkValidation.Contains("OperationModeLR2DB && UsePlayerLR2body"));
        StringAssert.Contains(initialize, "else if (Settings.Default.UsePlayerLR2body && File.Exists(settingDialog.LR2bodyPath))");
        StringAssert.Contains(initialize, "new LR2body(settingDialog.LR2bodyPath, CreateLR2PlayerConfig())");
        Assert.IsFalse(initialize.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(saveFollowup, "else if (Settings.Default.UsePlayerLR2body && tempUsePlayerLR2body != Settings.Default.UsePlayerLR2body)");
        Assert.IsFalse(saveFollowup.Contains("Settings.Default.OperationModeLR2DB && Settings.Default.UsePlayerLR2body"));
        StringAssert.Contains(viewModelCode, "private LR2Config CreateLR2PlayerConfig()");
        StringAssert.Contains(viewModelCode, "private bool IsLR2PlayerRootPathValid(string value)");
        StringAssert.Contains(lr2RootPathProperty, "if (!IsLR2RootPathValid() && !IsLR2PlayerRootPathValid())");
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
        StringAssert.Contains(saveAndClose, "bool shouldInitializeAfterSave = !viewModel.HasActiveLibraryProfile;");
        StringAssert.Contains(saveAndClose, "MainWindowViewModel.SettingDialogViewModel.RestartMode.None");
        StringAssert.Contains(saveAndClose, "if (shouldInitializeAfterSave)");
        StringAssert.Contains(saveAndClose, "SaveSettingsForInitialInitialize()");
        StringAssert.Contains(saveAndClose, "settingDialog.Visibility = Visibility.Hidden;");
        StringAssert.Contains(saveAndClose, "Msg_initsetting_completed");
        Assert.IsTrue(saveAndClose.IndexOf("Msg_initsetting_completed", StringComparison.Ordinal) < saveAndClose.IndexOf("viewModel.Initialize();", StringComparison.Ordinal));
        StringAssert.Contains(saveAndClose, "settingDialogViewModel.CheckValidation(out string errMsg)");
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
            "if (!settingDialog.CheckValidation())",
            "if (Settings.Default.OperationModeLR2DB && !await EnsureAppSchemaRepairApprovedForStartupAsync())");

        Assert.IsFalse(validationFailure.Contains("DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings,"));
        StringAssert.Contains(validationFailure, "base.Messenger.Raise(new InteractionMessage(\"InitialSetupLanguageDialog\"));");
        StringAssert.Contains(validationFailure, "DispatcherMessageBox.Show(BeMusicSeeker.Properties.Resources.Msg_init_settings_check");
        StringAssert.Contains(validationFailure, "base.Messenger.Raise(new InteractionMessage(\"InitializationException\"));");
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
        StringAssert.Contains(appSchemaStartupPreflight, "base.Messenger.Raise(confirmationMessage);");
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
        StringAssert.Contains(saveCore, "if (lr2SearchRootsChanged && lr2config != null)");
        Assert.IsTrue(
            saveCore.IndexOf("if (lr2SearchRootsChanged && lr2config != null)", StringComparison.Ordinal)
            < saveCore.IndexOf("if (runPostSaveActions)", StringComparison.Ordinal),
            "LR2 config persistence must not be hidden behind runtime post-save actions.");
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
            "private void backupSavedSettings()",
            "private async Task necessaryStepsAfterSaved()");
        string restartDecision = ExtractBetween(
            viewModelCode,
            "public RestartMode IsNeedRestartForSaved()",
            "public RestartMode IsNeedRestartForSaveOrCancel()");
        string saveOrCancelDecision = ExtractBetween(
            viewModelCode,
            "public RestartMode IsNeedRestartForSaveOrCancel()",
            "public class PlaylistPropertyDialogViewModel");

        StringAssert.Contains(backupSavedSettings, "tempStandaloneBmsRootPaths = SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList);");
        Assert.IsFalse(viewModelCode.Contains("tempValidation"));
        Assert.IsFalse(restartDecision.Contains("CheckValidation()"));
        StringAssert.Contains(restartDecision, "scoreSourceChanged");
        StringAssert.Contains(restartDecision, "SerializeStandaloneBmsRootPaths(StandaloneBmsRootPathList)");
        StringAssert.Contains(saveOrCancelDecision, "return RestartMode.None;");
    }

    [TestMethod]
    public void SearchRootChanges_UpdateRuntimeSearchTargetsBeforeFileDiffReload()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
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

        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = [.. lr2config.GetBMSSearchDirectories()];");
        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = [.. GetStandaloneBmsRootPathsFromSettings()];");
        StringAssert.Contains(rootAdd, "ApplyRuntimeSearchRootsForCurrentMode();");
        Assert.IsTrue(rootAdd.IndexOf("ApplyRuntimeSearchRootsForCurrentMode();", StringComparison.Ordinal) < rootAdd.IndexOf("ownerViewModel.ReloadFileDiff();", StringComparison.Ordinal));
        StringAssert.Contains(saveCore, "ApplyRuntimeSearchRootsForCurrentMode();");
    }

    [TestMethod]
    public void StandaloneRootNormalization_PreservesExistingRootsWhenAddingInstallDestination()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string addStandalone = ExtractBetween(
            viewModelCode,
            "private void AddStandaloneBmsRootPath",
            "private void AddBMSDirectoryToLR2Config");
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
        StringAssert.Contains(autoRenameAll, "files?.AutoRenameAllChartFolders(parentDir) == true");
        Assert.IsFalse(autoRenameAll.Contains("IEnumerable<BeMusicSeeker.Models.BMSFile> enumerable = BMSFiles;"));
        StringAssert.Contains(autoRenameAllModel, "CreateOwnedRealPathChartDirectorySnapshotUnsafe(parentDir)");
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
            "internal PlaylistLibraryResolveIndexSnapshot CreatePlaylistLibraryResolveIndexSnapshot",
            "private ChartInfoHydrationOwnerSummary CreateChartInfoHydrationOwnerSummaryUnsafe");

        StringAssert.Contains(createPlaylistLibraryIndex, "CreatePlaylistLibraryResolveIndexSnapshot(cancellationToken)");
        Assert.IsFalse(createPlaylistLibraryIndex.Contains("foreach (BeMusicSeeker.Models.BMSFile file in BMSFiles"));
        Assert.IsFalse(createPlaylistLibraryIndex.Contains("files?.BmsonSongs"));
        StringAssert.Contains(resolveIndexHelper, "ownedChartCollection.CreatePlaylistLibraryResolveIndexSnapshot(cancellationToken.ThrowIfCancellationRequested)");
    }

    [TestMethod]
    public void DuplicateFilterViewUsesChartFileParameters()
    {
        string viewModelCode = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string refreshChartRowsView = ExtractBetween(
            viewModelCode,
            "private void RefreshChartRowsView",
            "private static bool TryNormalizeNormalLibrarySortCacheColumn");
        string duplicateFilterBranch = ExtractBetween(
            refreshChartRowsView,
            "case viewUpdateMode.DuplicateFilterSelected:",
            "case viewUpdateMode.GarbledFilterSelected:");

        StringAssert.Contains(duplicateFilterBranch, "DuplicateGroup");
        StringAssert.Contains(duplicateFilterBranch, "DuplicateChartGroups.SelectMany(g => g.ChartFiles)");
        Assert.IsFalse(duplicateFilterBranch.Contains("List<BeMusicSeeker.Models.BMSFile>"));
        Assert.IsFalse(duplicateFilterBranch.Contains("parameter as List<BeMusicSeeker.Models.BMSFile>"));
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

        StringAssert.Contains(contextMenuResource, "ShouldUsePlaylistMissingContextMenu(row, GetCurrentChartOperationSourceScope())");
        Assert.IsFalse(contextMenuResource.Contains("GetRealBmsFile"));
        StringAssert.Contains(renameInvalidExtensionClick, "GetSelectedBmsFormatCharts(ChartOperationCapabilities.RenameInvalidExtension)");
        StringAssert.Contains(renameInvalidExtensionClick, "viewModel.RenameBMSFilesExtensions(list, \".bmx\")");
        Assert.IsFalse(renameInvalidExtensionClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RenameInvalidExtension)"));
        StringAssert.Contains(encodingFixClick, "ToBmsFiles(GetSelectedBmsFormatCharts(ChartOperationCapabilities.RunBmsEncodingFix))");
        Assert.IsFalse(encodingFixClick.Contains("GetSelectedChartCompatibilityAdapters(ChartOperationCapabilities.RunBmsEncodingFix)"));
        StringAssert.Contains(audioConvertClick, "ToBmsFiles(GetSelectedBmsFormatCharts(ChartOperationCapabilities.ConvertToAudio))");
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
            Resources.Details_test_notscan,
            Resources.Details_test_notcheck_playlists,
            Resources.Details_test_startup_select_install_pending,
            Resources.Details_test_notcalc_offrank,
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
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_skip_init_file_check");
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_skip_init_playlist_load");
        StringAssert.Contains(viewModel, "Resources.Msg_confirm_skip_offline_score_ranking_estimation");
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

        StringAssert.Contains(settingDialogXaml, "Click=\"buttonAddStandaloneBmsRootPathsClicked\"");
        StringAssert.Contains(settingDialogCode, "Multiselect = true");
        StringAssert.Contains(settingDialogCode, "IsFolderPicker = true");
        StringAssert.Contains(settingDialogCode, "dialog.FileNames");
        StringAssert.Contains(viewModelCode, "public void AddStandaloneBmsRootPaths(IEnumerable<string> paths)");
        StringAssert.Contains(viewModelCode, "NormalizeStandaloneBmsRootPaths(paths ?? [])");

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
        StringAssert.Contains(libraryCode, "BuildEstimatedInstallMaintenanceTargets(batchResult.DeferredMaintenanceCharts)");
        StringAssert.Contains(libraryCode, "canUseResourceHealthIndexDelta ? ResourceHealthIndexUpdateMode.DeltaOnUpdates : ResourceHealthIndexUpdateMode.FullOnUpdates");
        StringAssert.Contains(libraryCode, "LogReverseLookupMutationAndQueueWarmupIfNeeded(\"install_package\", reverseLookupMutation);");
        StringAssert.Contains(libraryCode, "resource_health_index_delta reason=");
        StringAssert.Contains(libraryCode, "if (estimatedInstallMaintenanceTargets.Count > 0)");
        StringAssert.Contains(libraryCode, "setMaintenanceInfo(");
        StringAssert.Contains(libraryCode, "estimatedInstallMaintenanceTargets,");
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
        StringAssert.Contains(libraryCode, "? \"install_package_estimated\"");
        StringAssert.Contains(libraryCode, ": \"setMaintenanceInfo\"");
        StringAssert.Contains(libraryCode, "DispatchResourceHealthIndexMutation(resourceHealthMutation, resourceHealthMutationReason)");
        StringAssert.Contains(mergeMethod, "resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeferOnUpdates");
        StringAssert.Contains(duplicateSearchMethod, "bmsSnapshotMs=");
        StringAssert.Contains(duplicateSearchMethod, "clearDuplicateStateMs=");
        StringAssert.Contains(duplicateSearchMethod, "duplicateRowSnapshotMs=");
        StringAssert.Contains(duplicateSearchMethod, "analyzeMs=");
        StringAssert.Contains(duplicateSearchMethod, "applyWarningsMs=");
        StringAssert.Contains(duplicateSearchMethod, "materializedChartCount=");
    }

    [TestMethod]
    public void EmptyDbStartupOptimizationDocs_DocumentFileDiffPipeline()
    {
        string root = FindRepositoryRoot();
        string initializationCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BmsLibraryInternal", "BmsLibraryInitializationService.cs"));
        string planDoc = File.ReadAllText(Path.Combine(root, "devdocs", "plan", "empty-db-first-startup-optimization-plan.md"));
        string startupFlowDoc = File.ReadAllText(Path.Combine(root, "devdocs", "spec", "startup-initialization-flow.md"));

        StringAssert.Contains(initializationCode, "private const int DefaultInlineChartInfoBatchSize = 2048;");
        StringAssert.Contains(initializationCode, "var postParseQueue = new BlockingCollection<FileDiffParsedBatch>(postParseQueueCapacity);");
        StringAssert.Contains(initializationCode, "BlockingCollection<FileScanDiffCommitChunk> commitQueue = [];");
        StringAssert.Contains(initializationCode, "BuildInlineBmsMaintenanceBatch(");
        StringAssert.Contains(initializationCode, "Task[] workerTasks = [.. Enumerable.Range(0, parserDegree)");
        StringAssert.Contains(initializationCode, "Parallel.For(0, candidates.Count");
        StringAssert.Contains(initializationCode, "inline_maintenance_wall_ms=");
        StringAssert.Contains(initializationCode, "parser_output_wait_ms=");
        StringAssert.Contains(initializationCode, "\" bms=\" + batchMetrics.BmsCount");
        StringAssert.Contains(initializationCode, "\" bmson=\" + batchMetrics.BmsonCount");

        StringAssert.Contains(planDoc, "parser output queue capacity");
        StringAssert.Contains(planDoc, "2048");
        StringAssert.Contains(planDoc, "inline_maintenance_wall_ms");
        StringAssert.Contains(startupFlowDoc, "post-parse worker");
        StringAssert.Contains(startupFlowDoc, "single DB writer");
        StringAssert.Contains(startupFlowDoc, "既定 batch size は 2048 件");
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker-decomp.sln")))
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

    private static string ExtractBetween(string text, string start, string end)
    {
        int startIndex = text.IndexOf(start, StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0, "Start marker was not found.");
        int endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.IsTrue(endIndex > startIndex, "End marker was not found.");
        return text.Substring(startIndex, endIndex - startIndex);
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
