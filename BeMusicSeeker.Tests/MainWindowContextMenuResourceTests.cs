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
    public void ContextMenuTemplates_ConstrainTallMenusWithScrollViewer()
    {
        string styles = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Simple Styles.xaml"));

        StringAssert.Contains(styles, "<views:MenuMaxHeightConverter x:Key=\"MenuMaxHeightConverter\" />");
        StringAssert.Contains(styles, "<Setter Property=\"MaxHeight\" Value=\"{Binding Source={x:Static SystemParameters.WorkArea}, Path=Height, Converter={StaticResource MenuMaxHeightConverter}}\" />");
        StringAssert.Contains(styles, "VerticalScrollBarVisibility=\"Auto\" CanContentScroll=\"False\" MaxHeight=\"{TemplateBinding MaxHeight}\"");
        StringAssert.Contains(styles, "Name=\"SubMenu\" Background=\"{DynamicResource App.PopupBackgroundBrush}\" Grid.IsSharedSizeScope=\"True\" MaxHeight=\"{Binding Source={x:Static SystemParameters.WorkArea}, Path=Height, Converter={StaticResource MenuMaxHeightConverter}}\"");
        StringAssert.Contains(styles, "VerticalScrollBarVisibility=\"Auto\" CanContentScroll=\"False\" MaxHeight=\"{Binding ElementName=SubMenu, Path=MaxHeight}\"");

        MenuMaxHeightConverter converter = new MenuMaxHeightConverter();

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
        Assert.IsFalse(code.Contains("await viewModel.RegistrateExternalPlaylistBMSTableAsync(dataContext.url)"));
        Assert.IsFalse(dialogCode.Contains("await viewModel.RegistrateExternalPlaylistBMSTableAsync(targetURI)"));
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
        {
            "Use_beatoraja_scoreDB",
            "FilePath_scoreDB",
            "Open_scoreDB",
            "FileDialogFilter_scoreDB",
            "Error_InvalidBeatorajaScoreDbPath"
        };
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
        StringAssert.Contains(xaml, "Path=Resources.Use_beatoraja_scoreDB");
        StringAssert.Contains(xaml, "Path=Resources.FilePath_scoreDB");
        StringAssert.Contains(xaml, "Path=Resources.Open_scoreDB");
        StringAssert.Contains(xaml, "Path=Resources.FileDialogFilter_scoreDB");
        StringAssert.Contains(viewModelCode, "Resources.Error_InvalidBeatorajaScoreDbPath");
        string settingDialogCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Views", "SettingDialog.cs"));
        StringAssert.Contains(settingDialogCode, "ReloadScoresOnly()");
        Assert.IsFalse(settingDialogCode.Contains("ReloadTables()"));
        Assert.IsFalse(xaml.Contains("Content=\"beatoraja"));
        Assert.IsFalse(xaml.Contains("Title=\"score.db"));
        Assert.IsFalse(xaml.Contains("Filter=\"score.db|score.db"));
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
        {
            "Standalone_BMSDirectories",
            "Add_BMSDirectory",
            "Remove_BMSDirectory",
            "Error_InvalidStandaloneBmsRootPaths"
        };
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
        StringAssert.Contains(viewModelCode, "return new List<string>();");
        StringAssert.Contains(viewModelCode, "temp_output_dir_full_path = Settings.Default.OperationModeLR2DB ? BMSPlaylist.GetCustomFolderOutputDirectory(bmsTable) : null;");
        StringAssert.Contains(File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Models", "BMSLibrary.cs")), "public List<string> SearchTargets { get; set; } = new List<string>();");
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
            "ColumnsSettingsBMSFilesView");
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
        StringAssert.Contains(saveAndClose, "settingDialogViewModel.CheckValidation(out var errMsg)");
        StringAssert.Contains(saveAndClose, "Msg_invalid_setting");
        StringAssert.Contains(saveAndClose, "Msg_error_unexpected");
        Assert.IsFalse(settingDialogCode.Contains("firstStartupInitializationStarted"));
        StringAssert.Contains(appCode, "public void RestartApplication()");
        StringAssert.Contains(appCode, "ReleaseSingleInstanceMutex();");
        StringAssert.Contains(appCode, "Environment.GetCommandLineArgs().Skip(1)");
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

        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = lr2config.GetBMSSearchDirectories().ToList();");
        StringAssert.Contains(runtimeSync, "ownerViewModel.files.SearchTargets = GetStandaloneBmsRootPathsFromSettings().ToList();");
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
            var normalized = MainWindowViewModel.SettingDialogViewModel.NormalizeStandaloneBmsRootPaths(new[] { firstRoot, secondRoot, installRoot });

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
        StringAssert.Contains(addStandalone, "new object[1] { requestedPath }");
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

    [TestMethod]
    public void UserSettingDefaults_AppConfigAndSettingsCodeStayInSync()
    {
        string root = FindRepositoryRoot();
        string settingsCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Properties", "Settings.cs"));
        string appConfigPath = Path.Combine(root, "app.config");
        string viewModelCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "ViewModels", "MainWindowViewModel.cs"));
        string appCode = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "App.cs"));

        Dictionary<string, string> settingsDefaults = ReadSettingsCodeDefaults(settingsCode);
        Dictionary<string, string> appConfigDefaults = XDocument.Load(appConfigPath)
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

        StringAssert.Contains(appCode, "Settings.LegacyTableListUrl");
        StringAssert.Contains(appCode, "Settings.DefaultTableListUrl");
        StringAssert.Contains(appCode, "Settings.Default.TableListURL.ToString()");
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
        StringAssert.Contains(dialogActionCode, "FileNames.ToArray()");

        StringAssert.Contains(settingDialogXaml, "Click=\"buttonAddStandaloneBmsRootPathsClicked\"");
        StringAssert.Contains(settingDialogCode, "Multiselect = true");
        StringAssert.Contains(settingDialogCode, "IsFolderPicker = true");
        StringAssert.Contains(settingDialogCode, "dialog.FileNames");
        StringAssert.Contains(viewModelCode, "public void AddStandaloneBmsRootPaths(IEnumerable<string> paths)");
        StringAssert.Contains(viewModelCode, "NormalizeStandaloneBmsRootPaths(paths ?? Enumerable.Empty<string>())");

        Type actionType = typeof(MainWindow).Assembly.GetType("BeMusicSeeker.Views.CommonOpenFileDialogInteractionMessageAction");
        Assert.IsNotNull(actionType);
        MethodInfo parseMethod = actionType.GetMethod("ParseFilterPairsForTest", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(parseMethod);
        var parsed = ((System.Collections.IEnumerable)parseMethod.Invoke(null, new object[] { "|song.db|すべてのファイル(*.*)|*.*" }))
            .Cast<Tuple<string, string>>()
            .ToList();
        Assert.AreEqual(2, parsed.Count);
        Assert.AreEqual("song.db", parsed[0].Item1);
        Assert.AreEqual("song.db", parsed[0].Item2);
        Assert.AreEqual("*.*", parsed[1].Item2);
        var fallback = ((System.Collections.IEnumerable)parseMethod.Invoke(null, new object[] { "broken" }))
            .Cast<Tuple<string, string>>()
            .ToList();
        Assert.AreEqual("*.*", fallback[0].Item2);
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

    private static Dictionary<string, string> ReadSettingsCodeDefaults(string settingsCode)
    {
        Dictionary<string, string> defaults = new Dictionary<string, string>(StringComparer.Ordinal);
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
}
