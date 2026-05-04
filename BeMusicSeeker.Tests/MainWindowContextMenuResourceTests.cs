using System;
using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Properties;
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
    public void ChartInfoParseFailureContextMenu_UsesDedicatedResourceAndVisibilityPolicy()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(1, CountOccurrences(xaml, "Name=\"tableContextMenuItemRemoveChartInfoParseFailure\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Remove_chart_info_parse_failure_record, Mode=OneWay"));
        Assert.AreEqual(1, CountOccurrences(xaml, "Click=\"tableContextMenuItemRemoveChartInfoParseFailureClick\""));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Remove_chart_info_parse_failure_record));
        Assert.IsFalse(string.IsNullOrWhiteSpace(Resources.Msg_remove_chart_info_parse_failure_record));
        Assert.IsTrue(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(true, new[] { new string('a', 32) }));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(false, new[] { new string('a', 32) }));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenuForTest(true, new[] { " " }));
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
        string xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "BeMusicSeeker", "Views", "MainWindow.xaml"));

        Assert.AreEqual(1, CountOccurrences(xaml, "x:Key=\"SidebarSplitterStyle\""));
        Assert.AreEqual(2, CountOccurrences(xaml, "Style=\"{StaticResource SidebarSplitterStyle}\""));
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
        Settings settings = new Settings();

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
        Settings settings = new Settings();

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

        Assert.AreEqual(1, CountOccurrences(xaml, "ItemsSource=\"{Binding settingDialog.AppearanceThemeOptions}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "SelectedValue=\"{Binding settingDialog.AppearanceTheme, Mode=TwoWay}\""));
        Assert.AreEqual(1, CountOccurrences(xaml, "Path=Resources.Appearance_theme, Mode=OneWay"));
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(AppContext.BaseDirectory);
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
}
